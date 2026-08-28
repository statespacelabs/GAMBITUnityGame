using System;
using System.Collections.Generic;
using Unity.Barracuda;
using UnityEngine;

/// <summary>
/// Sole owner of the final command during local45 PPO training. The frozen
/// actor231 navigator owns movement and hidden-target turning; the learner
/// owns visible-target aim and combat actions.
/// </summary>
[DefaultExecutionOrder(-1000)]
public sealed class NavigationAssistedMlAgentsController : MonoBehaviour, IPlayerController
{
    public GambitAgentController Learner;

    private readonly float[] actor = new float[ActorObservationContract.Size];
    private MapIndependentTelemetry telemetry;
    private FrozenNavigatorPolicy navigator;
    private MatchManager match;
    private PlayerCommand navigationCommand;
    private bool opponentVisible;
    private bool initialized;

    public void Initialize(PlayerIdentity identity, MatchManager matchManager)
    {
        if (Learner == null)
            throw new InvalidOperationException("Navigation-assisted training requires a GambitAgentController");

        PlayerBody body = GetComponent<PlayerBody>();
        telemetry = GetComponent<MapIndependentTelemetry>();
        if (telemetry == null)
            telemetry = gameObject.AddComponent<MapIndependentTelemetry>();
        telemetry.Initialize(body);

        Learner.UseNavigationAssistedActions = true;
        Learner.Initialize(identity, matchManager);
        navigator = new FrozenNavigatorPolicy(ReadBackend());
        match = matchManager;
        if (match != null)
        {
            match.OnRoundReset += ResetNavigator;
            match.OnMatchReset += ResetNavigator;
        }
        initialized = true;
        Debug.Log("[Training] actor231 navigator assisting local45 learner for "
            + (identity != null ? identity.DisplayName : gameObject.name));
    }

    private void FixedUpdate()
    {
        if (!initialized || navigator == null)
            return;
        try
        {
            telemetry.BuildObservation(actor, true);
            float[] action = navigator.Act(actor);
            navigationCommand = new PlayerCommand
            {
                MoveX = action[PolicyActionContract.MoveX],
                MoveZ = action[PolicyActionContract.MoveZ],
                Turn = action[PolicyActionContract.Turn],
                LookPitch = action[PolicyActionContract.LookPitch]
            };
            opponentVisible = actor[ActorObservationContract.VisibleEnemyState] > 0.5f;
        }
        catch (Exception exception)
        {
            navigationCommand = PlayerCommand.NoOp;
            initialized = false;
            Debug.LogException(exception);
            Debug.LogError("[Training] actor231 navigator stopped after an inference failure");
        }
    }

    public PlayerCommand GetCommand()
    {
        if (!initialized || Learner == null)
            return PlayerCommand.NoOp;
        return NavigationAssistedCommandComposer.Compose(
            navigationCommand,
            Learner.GetCommand(),
            opponentVisible);
    }

    private void ResetNavigator()
    {
        navigationCommand = PlayerCommand.NoOp;
        opponentVisible = false;
        navigator?.Reset();
    }

    private void OnDestroy()
    {
        if (match != null)
        {
            match.OnRoundReset -= ResetNavigator;
            match.OnMatchReset -= ResetNavigator;
        }
        navigator?.Dispose();
    }

    private static WorkerFactory.Type ReadBackend()
    {
        string value = (Environment.GetEnvironmentVariable("PHASE6_UNITY_ONNX_BACKEND") ?? "cpu")
            .Trim().ToLowerInvariant();
        if (value == "gpu" || value == "compute")
            return WorkerFactory.Type.ComputePrecompiled;
        if (value == "cpu" || value == "burst")
            return WorkerFactory.Type.CSharpBurst;
        throw new InvalidOperationException("PHASE6_UNITY_ONNX_BACKEND must be gpu or cpu");
    }
}

/// <summary>Pure, testable final-action ownership rule for hybrid training.</summary>
public static class NavigationAssistedCommandComposer
{
    public static PlayerCommand Compose(
        PlayerCommand navigation,
        PlayerCommand learner,
        bool opponentVisible)
    {
        return new PlayerCommand
        {
            MoveX = navigation.MoveX,
            MoveZ = navigation.MoveZ,
            Turn = opponentVisible ? learner.Turn : navigation.Turn,
            LookPitch = opponentVisible ? learner.LookPitch : navigation.LookPitch,
            Shoot = opponentVisible && learner.Shoot,
            Reload = learner.Reload,
            Jump = learner.Jump,
            Crouch = learner.Crouch
        };
    }
}

internal sealed class FrozenNavigatorPolicy : IDisposable
{
    private readonly IWorker worker;
    private readonly MapIndependentSafetyLayer safety = new MapIndependentSafetyLayer();
    private readonly float[] hidden = new float[OnnxModelContractValidator.NavigatorHiddenSize];

    public FrozenNavigatorPolicy(WorkerFactory.Type workerType)
    {
        NNModel asset = Resources.Load<NNModel>("MLModels/navigator");
        if (asset == null)
            throw new InvalidOperationException("Bundled actor231 navigator is missing from the build");
        Model model = ModelLoader.Load(asset);
        OnnxModelContractValidator.ValidateNavigator(model);
        worker = WorkerFactory.CreateWorker(workerType, model);
    }

    public float[] Act(float[] observation)
    {
        ActorObservationContract.ValidateBuffer(observation, nameof(observation));
        float[] action;
        float[] nextHidden;
        using (Tensor observationTensor = new Tensor(
            1, 1, ActorObservationContract.Size, 1, observation))
        using (Tensor hiddenTensor = new Tensor(
            1, 1, OnnxModelContractValidator.NavigatorHiddenSize, 1, hidden))
        {
            worker.Execute(new Dictionary<string, Tensor>
            {
                { "actor_observation", observationTensor },
                { "hidden_state", hiddenTensor }
            });
            action = worker.PeekOutput("action_mean").ToReadOnlyArray();
            nextHidden = worker.PeekOutput("next_hidden_state").ToReadOnlyArray();
        }
        OnnxModelContractValidator.ValidateRuntimeOutput(
            action, OnnxModelContractValidator.NavigatorActionSize, "navigator.action_mean");
        OnnxModelContractValidator.ValidateRuntimeOutput(
            nextHidden, OnnxModelContractValidator.NavigatorHiddenSize, "navigator.next_hidden_state");
        Array.Copy(nextHidden, hidden, hidden.Length);
        for (int index = 0; index < action.Length; index++)
            action[index] = Mathf.Clamp(action[index], -1f, 1f);
        safety.Apply(observation, action);
        return action;
    }

    public void Reset()
    {
        Array.Clear(hidden, 0, hidden.Length);
        safety.Reset();
    }

    public void Dispose()
    {
        worker?.Dispose();
    }
}

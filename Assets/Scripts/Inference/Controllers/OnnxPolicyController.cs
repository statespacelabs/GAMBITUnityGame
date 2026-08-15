using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Unity.Barracuda;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Presentation controller that executes the bundled frozen ONNX policy
/// inside Unity. It removes the external ML-Agents/Python lockstep while keeping
/// the actor/local45 schemas, recurrent state, safety layer, and action contract.
/// </summary>
[DefaultExecutionOrder(-1000)]
[UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, null, "Phase6UnityOnnxController")]
public sealed class OnnxPolicyController : MonoBehaviour, IPlayerController
{
    public GambitAgentController TelemetryHelper;

    private readonly float[] actor = new float[ActorObservationContract.Size];
    private readonly float[] local45 = new float[LocalObservationContract.Size];
    private MapIndependentTelemetry actorTelemetry;
    private BarracudaPolicy policy;
    private MatchManager match;
    private PlayerCommand command;
    private string slot;
    private bool initialized;
    private bool wroteInitialParity;
    private bool wroteVisibleParity;
    private long decisions;
    private double inferenceMilliseconds;
    private double maximumInferenceMilliseconds;

    public static int SuccessfulInferenceCount { get; private set; }

    public PlayerCommand GetCommand() => initialized ? command : PlayerCommand.NoOp;

    public void Initialize(PlayerIdentity identity, MatchManager matchManager)
    {
        match = matchManager;
        slot = identity != null && identity.PlayerIndex == 0 ? "A" : "B";
        command = PlayerCommand.NoOp;

        PlayerBody body = GetComponent<PlayerBody>();
        actorTelemetry = GetComponent<MapIndependentTelemetry>();
        if (actorTelemetry == null)
            actorTelemetry = gameObject.AddComponent<MapIndependentTelemetry>();
        actorTelemetry.Initialize(body);

        if (TelemetryHelper == null)
            TelemetryHelper = GetComponent<GambitAgentController>();
        if (TelemetryHelper == null)
            throw new InvalidOperationException("ONNX Unity ONNX requires a telemetry helper");
        TelemetryHelper.InitializeTelemetryOnly(identity, matchManager);

        policy = new BarracudaPolicy(ReadBackend());
        if (match != null)
        {
            match.OnRoundReset += ResetPolicy;
            match.OnMatchReset += ResetPolicy;
        }
        OnnxFrameMonitor.Ensure(policy.BackendName);
        initialized = true;
        Debug.Log("[OnnxPolicy] initialized slot=" + slot
            + " backend=" + policy.BackendName + " external_python=0");
    }

    private void FixedUpdate()
    {
        if (!initialized || policy == null)
            return;
        try
        {
            actorTelemetry.BuildObservation(actor, true);
            TelemetryHelper.BuildTelemetryObservation(local45, true);
            long started = Stopwatch.GetTimestamp();
            float[] action = policy.Act(actor, local45);
            double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            decisions++;
            SuccessfulInferenceCount++;
            inferenceMilliseconds += elapsed;
            maximumInferenceMilliseconds = Math.Max(maximumInferenceMilliseconds, elapsed);
            command = ToCommand(action);
            TelemetryHelper.SetExternalCommandForTelemetry(command);
            WriteParityIfNeeded(action);
            if (decisions % 250 == 0)
            {
                Debug.Log("[OnnxPolicy] slot=" + slot
                    + " decisions=" + decisions.ToString(CultureInfo.InvariantCulture)
                    + " mean_ms=" + (inferenceMilliseconds / decisions).ToString("F4", CultureInfo.InvariantCulture)
                    + " max_ms=" + maximumInferenceMilliseconds.ToString("F4", CultureInfo.InvariantCulture));
            }
        }
        catch (Exception exception)
        {
            command = PlayerCommand.NoOp;
            initialized = false;
            Debug.LogException(exception);
            Debug.LogError("[OnnxPolicy] hard_stop slot=" + slot);
        }
    }

    private static PlayerCommand ToCommand(float[] action)
    {
        PolicyActionContract.ValidateBuffer(action, nameof(action));
        return new PlayerCommand
        {
            MoveX = Mathf.Clamp(action[PolicyActionContract.MoveX], -1f, 1f),
            MoveZ = Mathf.Clamp(action[PolicyActionContract.MoveZ], -1f, 1f),
            Turn = Mathf.Clamp(action[PolicyActionContract.Turn], -1f, 1f),
            LookPitch = 0f,
            Shoot = action[PolicyActionContract.Shoot] > PolicyActionContract.BinaryThreshold,
            Reload = action[PolicyActionContract.Reload] > PolicyActionContract.BinaryThreshold,
            Jump = action[PolicyActionContract.Jump] > PolicyActionContract.BinaryThreshold,
            Crouch = action[PolicyActionContract.Crouch] > PolicyActionContract.BinaryThreshold
        };
    }

    private void ResetPolicy()
    {
        command = PlayerCommand.NoOp;
        policy?.Reset();
        actorTelemetry?.ResetMemory();
        Debug.Log("[OnnxPolicy] recurrent_reset slot=" + slot);
    }

    private void OnDestroy()
    {
        if (match != null)
        {
            match.OnRoundReset -= ResetPolicy;
            match.OnMatchReset -= ResetPolicy;
        }
        policy?.Dispose();
    }

    private void WriteParityIfNeeded(float[] action)
    {
        bool visible = actor[ActorObservationContract.VisibleEnemyState] > 0.5f;
        if (wroteInitialParity && (!visible || wroteVisibleParity))
            return;
        string directory = Environment.GetEnvironmentVariable("PHASE6_UNITY_ONNX_PARITY_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        Directory.CreateDirectory(directory);
        string kind = !wroteInitialParity ? "initial" : "visible";
        OnnxParityRecord record = new OnnxParityRecord
        {
            schema_version = "phase6_unity_onnx_parity_v001",
            backend = policy.BackendName,
            slot = slot,
            visible = visible,
            actor = (float[])actor.Clone(),
            local45 = (float[])local45.Clone(),
            applied_action = (float[])action.Clone()
        };
        File.WriteAllText(Path.Combine(directory, slot + "_" + kind + ".json"),
            JsonUtility.ToJson(record, true));
        if (!wroteInitialParity)
            wroteInitialParity = true;
        else
            wroteVisibleParity = true;
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

internal sealed class BarracudaPolicy : IDisposable
{
    private readonly IWorker navigator;
    private readonly IWorker combat;
    private readonly LocalObservationNormalizer normalizer;
    private readonly MapIndependentSafetyLayer safety = new MapIndependentSafetyLayer();
    private readonly float[] navigatorHidden = new float[OnnxModelContractValidator.NavigatorHiddenSize];
    private readonly float[] combatHidden = new float[OnnxModelContractValidator.CombatHiddenSize];

    public string BackendName { get; }

    public BarracudaPolicy(WorkerFactory.Type workerType)
    {
        NNModel navigatorAsset = Resources.Load<NNModel>("MLModels/navigator");
        NNModel combatAsset = Resources.Load<NNModel>("MLModels/combat");
        TextAsset normalizerAsset = Resources.Load<TextAsset>("MLModels/normalizer");
        if (navigatorAsset == null || combatAsset == null || normalizerAsset == null)
            throw new InvalidOperationException("Bundled ONNX resources are missing from the build");
        Model navigatorModel = ModelLoader.Load(navigatorAsset);
        Model combatModel = ModelLoader.Load(combatAsset);
        OnnxModelContractValidator.Validate(navigatorModel, combatModel);
        navigator = WorkerFactory.CreateWorker(workerType, navigatorModel);
        combat = WorkerFactory.CreateWorker(workerType, combatModel);
        normalizer = new LocalObservationNormalizer(normalizerAsset.text);
        BackendName = workerType.ToString();
        Debug.Log("[OnnxPolicy] models_loaded backend=" + BackendName
            + " navigator_outputs=" + string.Join(",", navigatorModel.outputs)
            + " combat_outputs=" + string.Join(",", combatModel.outputs));
    }

    public float[] Act(float[] actor, float[] local45)
    {
        ActorObservationContract.ValidateBuffer(actor, nameof(actor));
        LocalObservationContract.ValidateBuffer(local45, nameof(local45));
        float[] applied = new float[PolicyActionContract.Size];
        float[] nextNavigatorHidden;
        using (Tensor actorTensor = new Tensor(1, 1, ActorObservationContract.Size, 1, actor))
        using (Tensor hiddenTensor = new Tensor(1, 1, OnnxModelContractValidator.NavigatorHiddenSize, 1, navigatorHidden))
        {
            navigator.Execute(new Dictionary<string, Tensor>
            {
                { "actor_observation", actorTensor },
                { "hidden_state", hiddenTensor }
            });
            float[] means = navigator.PeekOutput("action_mean").ToReadOnlyArray();
            nextNavigatorHidden = navigator.PeekOutput("next_hidden_state").ToReadOnlyArray();
            OnnxModelContractValidator.ValidateRuntimeOutput(
                means, OnnxModelContractValidator.NavigatorActionSize, "navigator.action_mean");
            OnnxModelContractValidator.ValidateRuntimeOutput(
                nextNavigatorHidden, OnnxModelContractValidator.NavigatorHiddenSize, "navigator.next_hidden_state");
            for (int index = 0; index < PolicyActionContract.ContinuousCount; index++)
                applied[index] = Mathf.Clamp(means[index], -1f, 1f);
        }
        Array.Copy(nextNavigatorHidden, navigatorHidden, navigatorHidden.Length);
        safety.Apply(actor, applied);

        if (actor[ActorObservationContract.VisibleEnemyState] > 0.5f)
        {
            float[] normalized = normalizer.Normalize(local45);
            float[] nextCombatHidden;
            using (Tensor observationTensor = new Tensor(1, 1, LocalObservationContract.Size, 1, normalized))
            using (Tensor hiddenTensor = new Tensor(1, 1, OnnxModelContractValidator.CombatHiddenSize, 1, combatHidden))
            {
                combat.Execute(new Dictionary<string, Tensor>
                {
                    { "obs_norm", observationTensor },
                    { "hidden", hiddenTensor }
                });
                float[] combatAction = combat.PeekOutput("action").ToReadOnlyArray();
                nextCombatHidden = combat.PeekOutput("next_hidden").ToReadOnlyArray();
                OnnxModelContractValidator.ValidateRuntimeOutput(
                    combatAction, PolicyActionContract.Size, "combat.action");
                OnnxModelContractValidator.ValidateRuntimeOutput(
                    nextCombatHidden, OnnxModelContractValidator.CombatHiddenSize, "combat.next_hidden");
                Array.Copy(combatAction, applied, applied.Length);
            }
            Array.Copy(nextCombatHidden, combatHidden, combatHidden.Length);
        }
        applied[PolicyActionContract.LookPitch] = 0f;
        for (int index = 0; index < applied.Length; index++)
            if (float.IsNaN(applied[index]) || float.IsInfinity(applied[index]))
                throw new InvalidOperationException("Bundled ONNX policy produced a non-finite action");
        return applied;
    }

    public void Reset()
    {
        Array.Clear(navigatorHidden, 0, navigatorHidden.Length);
        Array.Clear(combatHidden, 0, combatHidden.Length);
        safety.Reset();
    }

    public void Dispose()
    {
        navigator?.Dispose();
        combat?.Dispose();
    }
}

[Serializable]
internal sealed class NormalizerPayload
{
    public float clip_value;
    public float[] mean;
    public float[] std;
}

internal sealed class LocalObservationNormalizer
{
    private readonly NormalizerPayload payload;

    public LocalObservationNormalizer(string json)
    {
        payload = JsonUtility.FromJson<NormalizerPayload>(json);
        if (payload == null || payload.mean == null || payload.std == null
            || payload.mean.Length != LocalObservationContract.Size
            || payload.std.Length != LocalObservationContract.Size)
            throw new InvalidOperationException("ONNX local45 normalizer is invalid");
    }

    public float[] Normalize(float[] values)
    {
        LocalObservationContract.ValidateBuffer(values, nameof(values));
        float[] output = new float[LocalObservationContract.Size];
        for (int index = 0; index < output.Length; index++)
        {
            if (payload.std[index] <= 0f)
                throw new InvalidOperationException("ONNX local45 normalizer std is not positive");
            float value = (values[index] - payload.mean[index]) / payload.std[index];
            output[index] = payload.clip_value > 0f
                ? Mathf.Clamp(value, -payload.clip_value, payload.clip_value) : value;
        }
        return output;
    }
}

internal sealed class MapIndependentSafetyLayer
{
    private const int FlipInterval = 500;
    private int wallSide;
    private int clearTicks;
    private int hiddenTicks;

    public void Reset()
    {
        wallSide = 0;
        clearTicks = 0;
        hiddenTicks = 0;
    }

    public void Apply(float[] actor, float[] output)
    {
        if (actor[ActorObservationContract.VisibleEnemyState] > 0.5f)
        {
            Reset();
            return;
        }
        hiddenTicks++;
        int strongest = 0;
        float strongestValue = actor[ActorObservationContract.HuntCue];
        for (int index = 1; index < ActorObservationContract.HuntBearingCount; index++)
        {
            if (actor[ActorObservationContract.HuntCue + index] > strongestValue)
            {
                strongest = index;
                strongestValue = actor[ActorObservationContract.HuntCue + index];
            }
        }
        int huntValidIndex = ActorObservationContract.HuntCue
            + ActorObservationContract.HuntBearingCount
            + ActorObservationContract.HuntDistanceCount;
        if (actor[huntValidIndex] <= 0.5f || strongestValue <= 0.5f)
            return;
        int target = (strongest + ActorObservationContract.HuntBearingCount / 2)
            % ActorObservationContract.HuntBearingCount;
        float[] clearance = new float[ActorObservationContract.FreeSpaceSectorCount];
        for (int sector = 0; sector < ActorObservationContract.FreeSpaceSectorCount; sector++)
            clearance[sector] = actor[ActorObservationContract.TorsoRays
                + sector * ActorObservationContract.TorsoRaysPerSector * 2];
        bool pressure = actor[ActorObservationContract.ProgressStuckHistory + 4] > 0.25f
            || actor[ActorObservationContract.ProgressStuckHistory + 3] > 0.35f;
        bool active = wallSide != 0;
        if (active && clearance[target] >= 0.14f && !pressure)
        {
            clearTicks++;
            if (clearTicks >= ActorObservationContract.FreeSpaceSectorCount)
            {
                wallSide = 0;
                clearTicks = 0;
                active = false;
            }
        }
        else
        {
            clearTicks = 0;
        }
        if (!active && (clearance[target] < 0.075f || pressure))
        {
            int sectorCount = ActorObservationContract.FreeSpaceSectorCount;
            wallSide = clearance[(target + 1) % sectorCount]
                >= clearance[(target + sectorCount - 1) % sectorCount] ? 1 : -1;
            active = true;
        }
        if (active && hiddenTicks % FlipInterval == 0)
        {
            wallSide = -wallSide;
            clearTicks = 0;
        }
        if (!active)
            return;
        int chosen = target;
        for (int offset = 1; offset < ActorObservationContract.FreeSpaceSectorCount; offset++)
        {
            int sectorCount = ActorObservationContract.FreeSpaceSectorCount;
            int candidate = (target + wallSide * offset + sectorCount * sectorCount) % sectorCount;
            if (clearance[candidate] >= 0.06f)
            {
                chosen = candidate;
                break;
            }
        }
        if (clearance[chosen] < 0.025f)
        {
            chosen = 0;
            for (int index = 1; index < ActorObservationContract.FreeSpaceSectorCount; index++)
                if (clearance[index] > clearance[chosen]) chosen = index;
        }
        float angle = chosen * 45f;
        if (angle > 180f) angle -= 360f;
        output[0] = Mathf.Clamp(Mathf.Sin(angle * Mathf.Deg2Rad) * 0.75f, -0.75f, 0.75f);
        output[1] = Mathf.Abs(angle) < 100f ? 0.85f : 0.15f;
        output[2] = Mathf.Clamp(angle / 75f, -1f, 1f);
    }
}

[Serializable]
internal sealed class OnnxParityRecord
{
    public string schema_version;
    public string backend;
    public string slot;
    public bool visible;
    public float[] actor;
    public float[] local45;
    public float[] applied_action;
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Unity.Barracuda;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Full unified ONNX runtime: one model owns all eight final actions at 30 Hz.
/// No navigator, combat expert, safety layer, or action compositor is applied.
/// </summary>
[DefaultExecutionOrder(-1400)]
public sealed class Gen3UnifiedOnnxController : MonoBehaviour, IPlayerController
{
    private const float DecisionInterval = 1f / 30f;
    private readonly int[] previousEvents =
        new int[UnifiedFairObservationV2Contract.PreviousEventSize];
    private readonly float[] observation =
        new float[UnifiedFairObservationV2Contract.Size];
    private readonly float[] rawToken =
        new float[UnifiedFairObservationV2Contract.RawTokenSize];
    private readonly float[] flatTokens = new float[
        UnifiedFairObservationV2Contract.PrimaryContextLength
        * UnifiedFairObservationV2Contract.RawTokenSize];
    private readonly float[] paddingMask =
        new float[UnifiedFairObservationV2Contract.PrimaryContextLength];
    private readonly List<double> latencyMilliseconds = new List<double>();
    private readonly Gen3UnifiedRingBuffer history = new Gen3UnifiedRingBuffer(
        UnifiedFairObservationV2Contract.PrimaryContextLength,
        UnifiedFairObservationV2Contract.RawTokenSize);

    public string ModelResource = "MLModels/gen3_champion_v2";

    private PlayerBody body;
    private MatchManager match;
    private Gen3UnifiedObservation observationBuilder;
    private IWorker worker;
    private PlayerCommand command;
    private bool initialized;
    private float schedulerAccumulator;
    private float elapsedSinceDecision;
    private long decisions;
    private string slot;

    public PlayerCommand GetCommand() => initialized ? command : PlayerCommand.NoOp;

    public void Initialize(PlayerIdentity identity, MatchManager matchManager)
    {
        body = GetComponent<PlayerBody>();
        match = matchManager;
        slot = identity != null && identity.PlayerIndex == 0 ? "A" : "B";
        observationBuilder = GetComponent<Gen3UnifiedObservation>();
        if (observationBuilder == null)
            observationBuilder = gameObject.AddComponent<Gen3UnifiedObservation>();
        observationBuilder.Initialize(body);

        string resource = string.IsNullOrWhiteSpace(ModelResource)
            ? "MLModels/gen3_champion_v2" : ModelResource.Trim();
        NNModel asset = Resources.Load<NNModel>(resource);
        if (asset == null)
            throw new InvalidOperationException(
                "Qualified Gen3 unified ONNX resource is missing: " + resource
                + ". The V005 workspace contains no approved exported model.");
        Model model = ModelLoader.Load(asset);
        Gen3UnifiedOnnxContractValidator.Validate(model);
        WorkerFactory.Type backend = ReadBackend();
        worker = WorkerFactory.CreateWorker(backend, model);
        if (match != null)
        {
            match.OnHit += OnHit;
            match.OnMiss += OnMiss;
            match.OnKill += OnKill;
            match.OnRoundReset += ResetContext;
            match.OnMatchReset += ResetContext;
        }
        ResetContext();
        initialized = true;
        Debug.Log("[Gen3Unified] initialized slot=" + slot
            + " backend=" + backend + " context=32 hz=30 one_action_owner=1");
    }

    private void FixedUpdate()
    {
        if (!initialized || worker == null)
            return;
        float dt = Mathf.Max(Time.fixedDeltaTime, 0f);
        schedulerAccumulator += dt;
        elapsedSinceDecision += dt;
        if (schedulerAccumulator + 1e-6f < DecisionInterval)
            return;
        schedulerAccumulator = Mathf.Max(0f, schedulerAccumulator - DecisionInterval);
        float actualDelta = decisions == 0
            ? DecisionInterval : Mathf.Max(elapsedSinceDecision, 1e-4f);
        elapsedSinceDecision = 0f;
        try
        {
            Decide(actualDelta);
        }
        catch (Exception exception)
        {
            initialized = false;
            command = PlayerCommand.NoOp;
            Debug.LogException(exception);
            Debug.LogError("[Gen3Unified] hard_stop slot=" + slot);
        }
    }

    private void Decide(float actualDelta)
    {
        observationBuilder.BuildObservation(observation, true, actualDelta);
        int[] events = (int[])previousEvents.Clone();
        Array.Clear(previousEvents, 0, previousEvents.Length);
        UnifiedFairObservationV2Contract.BuildRawToken(
            observation,
            body.LastAppliedCommand,
            events,
            actualDelta,
            true,
            true,
            rawToken);
        history.Append(rawToken);
        history.CopyLeftPadded(flatTokens, paddingMask);

        long started = Stopwatch.GetTimestamp();
        float[] action;
        using (Tensor tokenTensor = new Tensor(
            1,
            UnifiedFairObservationV2Contract.PrimaryContextLength,
            UnifiedFairObservationV2Contract.RawTokenSize,
            1,
            flatTokens))
        using (Tensor maskTensor = new Tensor(
            1,
            1,
            UnifiedFairObservationV2Contract.PrimaryContextLength,
            1,
            paddingMask))
        {
            worker.Execute(new Dictionary<string, Tensor>
            {
                { "tokens", tokenTensor },
                { "padding_mask", maskTensor }
            });
            action = worker.PeekOutput("action").ToReadOnlyArray();
        }
        double elapsed = (Stopwatch.GetTimestamp() - started)
            * 1000.0 / Stopwatch.Frequency;
        Gen3UnifiedOnnxContractValidator.ValidateAction(action);
        command = Gen3UnifiedAction.FromArray(action);
        decisions++;
        latencyMilliseconds.Add(elapsed);
        if (latencyMilliseconds.Count > 2048)
            latencyMilliseconds.RemoveAt(0);
        if (decisions % 250 == 0)
            Debug.Log("[Gen3Unified] slot=" + slot
                + " decisions=" + decisions.ToString(CultureInfo.InvariantCulture)
                + " p95_ms=" + Percentile95().ToString("F4", CultureInfo.InvariantCulture)
                + " budget_ms=33.333");
    }

    private void OnHit(PlayerIdentity shooter, PlayerIdentity victim)
    {
        if (body == null || shooter != body.Identity)
            return;
        previousEvents[0]++;
        previousEvents[1]++;
    }

    private void OnMiss(PlayerIdentity shooter)
    {
        if (body == null || shooter != body.Identity)
            return;
        previousEvents[0]++;
        previousEvents[2]++;
    }

    private void OnKill(PlayerIdentity killer, PlayerIdentity victim)
    {
        if (body != null && killer == body.Identity)
            previousEvents[3]++;
    }

    private void ResetContext()
    {
        history.Reset();
        Array.Clear(previousEvents, 0, previousEvents.Length);
        Array.Clear(flatTokens, 0, flatTokens.Length);
        Array.Clear(paddingMask, 0, paddingMask.Length);
        command = PlayerCommand.NoOp;
        schedulerAccumulator = 0f;
        elapsedSinceDecision = 0f;
        observationBuilder?.ResetMemory();
    }

    private double Percentile95()
    {
        if (latencyMilliseconds.Count == 0)
            return 0d;
        double[] values = latencyMilliseconds.ToArray();
        Array.Sort(values);
        int index = Mathf.Clamp(
            Mathf.CeilToInt(values.Length * 0.95f) - 1, 0, values.Length - 1);
        return values[index];
    }

    private static WorkerFactory.Type ReadBackend()
    {
        string value = (Environment.GetEnvironmentVariable("GEN3_UNIFIED_ONNX_BACKEND") ?? "cpu")
            .Trim().ToLowerInvariant();
        if (value == "gpu" || value == "compute")
            return WorkerFactory.Type.ComputePrecompiled;
        if (value == "cpu" || value == "burst")
            return WorkerFactory.Type.CSharpBurst;
        throw new InvalidOperationException("GEN3_UNIFIED_ONNX_BACKEND must be gpu or cpu");
    }

    private void OnDestroy()
    {
        if (match != null)
        {
            match.OnHit -= OnHit;
            match.OnMiss -= OnMiss;
            match.OnKill -= OnKill;
            match.OnRoundReset -= ResetContext;
            match.OnMatchReset -= ResetContext;
        }
        worker?.Dispose();
        worker = null;
    }
}

public static class Gen3UnifiedOnnxContractValidator
{
    public static void Validate(Model model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));
        ValidateInput(model, "tokens",
            UnifiedFairObservationV2Contract.PrimaryContextLength
            * UnifiedFairObservationV2Contract.RawTokenSize);
        ValidateInput(model, "padding_mask",
            UnifiedFairObservationV2Contract.PrimaryContextLength);
        bool outputFound = false;
        for (int index = 0; index < model.outputs.Count; index++)
            outputFound |= model.outputs[index] == "action";
        if (!outputFound)
            throw new InvalidOperationException("Gen3 unified ONNX is missing output 'action'");
    }

    public static void ValidateAction(float[] action)
    {
        PolicyActionContract.ValidateBuffer(action, nameof(action));
        UnifiedFairObservationV2Contract.AssertFinite(
            action, UnifiedFairObservationV2Contract.ActionSchemaId);
    }

    private static void ValidateInput(Model model, string name, int expectedElements)
    {
        for (int index = 0; index < model.inputs.Count; index++)
        {
            Model.Input input = model.inputs[index];
            if (input.name != name)
                continue;
            int knownElements = 1;
            bool known = false;
            for (int dimension = 0;
                input.shape != null && dimension < input.shape.Length;
                dimension++)
            {
                if (input.shape[dimension] <= 0)
                    continue;
                knownElements *= input.shape[dimension];
                known = true;
            }
            if (!known || knownElements != expectedElements)
                throw new InvalidOperationException("Gen3 unified ONNX input '" + name
                    + "' expected " + expectedElements + " elements but declares "
                    + knownElements);
            return;
        }
        throw new InvalidOperationException("Gen3 unified ONNX is missing input '" + name + "'");
    }
}

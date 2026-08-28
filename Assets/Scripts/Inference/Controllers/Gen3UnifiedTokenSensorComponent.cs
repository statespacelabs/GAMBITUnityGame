using System;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// ML-Agents sensor for a full unified policy. Each decision receives one
/// causal 232-value token; sequence memory belongs to the trainer/model.
/// </summary>
public sealed class Gen3UnifiedTokenSensorComponent : SensorComponent
{
    private readonly int[] pendingEvents =
        new int[UnifiedFairObservationV2Contract.PreviousEventSize];
    private readonly float[] observation =
        new float[UnifiedFairObservationV2Contract.Size];
    private PlayerBody body;
    private MatchManager match;
    private Gen3UnifiedObservation builder;
    private bool initialized;

    public override ISensor[] CreateSensors()
    {
        EnsureInitialized();
        return new ISensor[] { new Gen3UnifiedTokenSensor(this) };
    }

    internal void BuildToken(float[] output, bool advanceState)
    {
        EnsureInitialized();
        float dt = Mathf.Max(Time.fixedDeltaTime, 1e-4f);
        builder.BuildObservation(observation, advanceState, dt);
        UnifiedFairObservationV2Contract.BuildRawToken(
            observation,
            body != null ? body.LastAppliedCommand : PlayerCommand.NoOp,
            pendingEvents,
            dt,
            true,
            true,
            output);
        if (advanceState)
            Array.Clear(pendingEvents, 0, pendingEvents.Length);
    }

    internal void ResetSensor()
    {
        Array.Clear(pendingEvents, 0, pendingEvents.Length);
        builder?.ResetMemory();
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;
        body = GetComponent<PlayerBody>();
        if (body == null)
            throw new InvalidOperationException("Gen3 unified sensor requires PlayerBody");
        builder = GetComponent<Gen3UnifiedObservation>();
        if (builder == null)
            builder = gameObject.AddComponent<Gen3UnifiedObservation>();
        builder.Initialize(body);
        match = body.MatchManager;
        if (match != null)
        {
            match.OnHit += OnHit;
            match.OnMiss += OnMiss;
            match.OnKill += OnKill;
            match.OnRoundReset += ResetSensor;
            match.OnMatchReset += ResetSensor;
        }
        initialized = true;
    }

    private void OnHit(PlayerIdentity shooter, PlayerIdentity victim)
    {
        if (body == null || shooter != body.Identity)
            return;
        pendingEvents[0]++;
        pendingEvents[1]++;
    }

    private void OnMiss(PlayerIdentity shooter)
    {
        if (body == null || shooter != body.Identity)
            return;
        pendingEvents[0]++;
        pendingEvents[2]++;
    }

    private void OnKill(PlayerIdentity killer, PlayerIdentity victim)
    {
        if (body != null && killer == body.Identity)
            pendingEvents[3]++;
    }

    private void OnDestroy()
    {
        if (match == null)
            return;
        match.OnHit -= OnHit;
        match.OnMiss -= OnMiss;
        match.OnKill -= OnKill;
        match.OnRoundReset -= ResetSensor;
        match.OnMatchReset -= ResetSensor;
    }
}

public sealed class Gen3UnifiedTokenSensor : ISensor
{
    private readonly Gen3UnifiedTokenSensorComponent component;
    private readonly float[] cache =
        new float[UnifiedFairObservationV2Contract.RawTokenSize];
    private bool hasCache;

    public Gen3UnifiedTokenSensor(Gen3UnifiedTokenSensorComponent configuredComponent)
    {
        component = configuredComponent;
    }

    public ObservationSpec GetObservationSpec() =>
        ObservationSpec.Vector(UnifiedFairObservationV2Contract.RawTokenSize);

    public int Write(ObservationWriter writer)
    {
        if (!hasCache)
        {
            component.BuildToken(cache, false);
            hasCache = true;
        }
        for (int index = 0; index < cache.Length; index++)
            writer[index] = cache[index];
        return cache.Length;
    }

    public void Update()
    {
        component.BuildToken(cache, true);
        hasCache = true;
    }

    public void Reset()
    {
        component.ResetSensor();
        hasCache = false;
    }

    public byte[] GetCompressedObservation() => null;
    public CompressionSpec GetCompressionSpec() => CompressionSpec.Default();
    public string GetName() => UnifiedFairObservationV2Contract.TokenSchemaId;
}

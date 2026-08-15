using System;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Goal 9 learner-only critic transport. Python dispatches by width and feeds
/// only actor231 to the policy; critic428 is never concatenated to actor input.
/// </summary>
public sealed class Phase5PpoPrivilegedSensorComponent : SensorComponent
{
    public override ISensor[] CreateSensors()
    {
        PlayerBody body = GetComponent<PlayerBody>();
        Phase5MapIndependentTelemetry telemetry = GetComponent<Phase5MapIndependentTelemetry>();
        if (body == null || telemetry == null)
            throw new InvalidOperationException("[Phase5HunterPPO] actor telemetry must precede critic sensor");
        telemetry.Initialize(body);
        return new ISensor[] { new Phase5PpoPrivilegedSensor(body, telemetry) };
    }
}

public sealed class Phase5PpoPrivilegedSensor : ISensor
{
    private readonly PlayerBody body;
    private readonly Phase5MapIndependentTelemetry telemetry;
    private readonly float[] actor = new float[Phase5ActorObservationLayout.ObservationSize];
    private readonly float[] tactical = new float[Phase5PrivilegedCriticTelemetry.TacticalStateSize];
    private readonly float[] critic = new float[Phase5PrivilegedCriticTelemetry.ObservationSize];
    private bool ready;

    public Phase5PpoPrivilegedSensor(PlayerBody configuredBody, Phase5MapIndependentTelemetry configuredTelemetry)
    {
        body = configuredBody;
        telemetry = configuredTelemetry;
    }

    public ObservationSpec GetObservationSpec() =>
        ObservationSpec.Vector(Phase5PrivilegedCriticTelemetry.ObservationSize);

    public int Write(ObservationWriter writer)
    {
        if (!ready)
            Build();
        for (int i = 0; i < critic.Length; i++)
            writer[i] = critic[i];
        return critic.Length;
    }

    public void Update() => Build();
    public void Reset() { Array.Clear(critic, 0, critic.Length); ready = false; }
    public byte[] GetCompressedObservation() => null;
    public CompressionSpec GetCompressionSpec() => CompressionSpec.Default();
    public string GetName() => Phase5PrivilegedCriticTelemetry.SchemaId;

    private void Build()
    {
        if (!telemetry.CopyLatestObservation(actor))
            telemetry.BuildObservation(actor, false);
        Array.Clear(tactical, 0, tactical.Length);
        Phase5PrivilegedCriticTelemetry.Build(body, actor, tactical, critic);
        ready = true;
    }
}

/// <summary>Routing-only candidate/opponent tag; it is never fed to either policy.</summary>
public sealed class Phase5PpoRoleSensorComponent : SensorComponent
{
    public bool CandidateRole;
    public override ISensor[] CreateSensors() =>
        new ISensor[] { new Phase5PpoRoleSensor(() => CandidateRole) };
}

public sealed class Phase5PpoRoleSensor : ISensor
{
    private readonly Func<bool> candidate;
    public Phase5PpoRoleSensor(Func<bool> candidateRole) { candidate = candidateRole; }
    public ObservationSpec GetObservationSpec() => ObservationSpec.Vector(1);
    public int Write(ObservationWriter writer) { writer[0] = candidate() ? 1f : 0f; return 1; }
    public void Update() { }
    public void Reset() { }
    public byte[] GetCompressedObservation() => null;
    public CompressionSpec GetCompressionSpec() => CompressionSpec.Default();
    public string GetName() => "phase5_ppo_role_routing_v001";
}

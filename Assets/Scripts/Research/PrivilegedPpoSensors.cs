using System;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Goal 9 learner-only critic transport. Python dispatches by width and feeds
/// only actor231 to the policy; critic428 is never concatenated to actor input.
/// </summary>
[UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, null, "Phase5PpoPrivilegedSensorComponent")]
public sealed class PpoPrivilegedSensorComponent : SensorComponent
{
    public override ISensor[] CreateSensors()
    {
        PlayerBody body = GetComponent<PlayerBody>();
        MapIndependentTelemetry telemetry = GetComponent<MapIndependentTelemetry>();
        if (body == null || telemetry == null)
            throw new InvalidOperationException("[PrivilegedPPO] actor telemetry must precede critic sensor");
        telemetry.Initialize(body);
        return new ISensor[] { new PpoPrivilegedSensor(body, telemetry) };
    }
}

public sealed class PpoPrivilegedSensor : ISensor
{
    private readonly PlayerBody body;
    private readonly MapIndependentTelemetry telemetry;
    private readonly float[] actor = new float[ActorObservationContract.Size];
    private readonly float[] tactical = new float[PrivilegedCriticTelemetry.TacticalStateSize];
    private readonly float[] critic = new float[PrivilegedCriticTelemetry.ObservationSize];
    private bool ready;

    public PpoPrivilegedSensor(PlayerBody configuredBody, MapIndependentTelemetry configuredTelemetry)
    {
        body = configuredBody;
        telemetry = configuredTelemetry;
    }

    public ObservationSpec GetObservationSpec() =>
        ObservationSpec.Vector(PrivilegedCriticTelemetry.ObservationSize);

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
    public string GetName() => PrivilegedCriticTelemetry.SchemaId;

    private void Build()
    {
        if (!telemetry.CopyLatestObservation(actor))
            telemetry.BuildObservation(actor, false);
        Array.Clear(tactical, 0, tactical.Length);
        PrivilegedCriticTelemetry.Build(body, actor, tactical, critic);
        ready = true;
    }
}

/// <summary>Routing-only candidate/opponent tag; it is never fed to either policy.</summary>
[UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, null, "Phase5PpoRoleSensorComponent")]
public sealed class PpoRoleSensorComponent : SensorComponent
{
    public bool CandidateRole;
    public override ISensor[] CreateSensors() =>
        new ISensor[] { new PpoRoleSensor(() => CandidateRole) };
}

public sealed class PpoRoleSensor : ISensor
{
    private readonly Func<bool> candidate;
    public PpoRoleSensor(Func<bool> candidateRole) { candidate = candidateRole; }
    public ObservationSpec GetObservationSpec() => ObservationSpec.Vector(1);
    public int Write(ObservationWriter writer) { writer[0] = candidate() ? 1f : 0f; return 1; }
    public void Update() { }
    public void Reset() { }
    public byte[] GetCompressedObservation() => null;
    public CompressionSpec GetCompressionSpec() => CompressionSpec.Default();
    public string GetName() => "phase5_ppo_role_routing_v001";
}

using UnityEngine;
using Unity.MLAgents.Sensors;

public class GambitTelemetrySensorComponent : SensorComponent
{
    public override ISensor[] CreateSensors()
    {
        var agent = GetComponent<GambitAgentController>();
        if (agent == null)
        {
            throw new System.InvalidOperationException("[GambitTelemetrySensor] Missing GambitAgentController");
        }
        return new ISensor[] { new GambitTelemetrySensor(agent) };
    }
}

public class GambitTelemetrySensor : ISensor
{
    private readonly GambitAgentController agent;
    private readonly float[] cache = new float[LocalObservationContract.Size];
    private bool hasCache = false;

    public GambitTelemetrySensor(GambitAgentController agent)
    {
        this.agent = agent;
    }

    public ObservationSpec GetObservationSpec()
    {
        return ObservationSpec.Vector(LocalObservationContract.Size);
    }

    public int Write(ObservationWriter writer)
    {
        if (!hasCache)
        {
            agent.BuildTelemetryObservation(cache, false);
            hasCache = true;
        }
        for (int i = 0; i < LocalObservationContract.Size; i++)
        {
            writer[i] = cache[i];
        }
        return LocalObservationContract.Size;
    }

    public byte[] GetCompressedObservation()
    {
        return null;
    }

    public void Update()
    {
        agent.BuildTelemetryObservation(cache, true);
        hasCache = true;
    }

    public void Reset()
    {
        hasCache = false;
    }

    public CompressionSpec GetCompressionSpec()
    {
        return CompressionSpec.Default();
    }

    public string GetName()
    {
        return LocalObservationContract.SchemaId;
    }
}

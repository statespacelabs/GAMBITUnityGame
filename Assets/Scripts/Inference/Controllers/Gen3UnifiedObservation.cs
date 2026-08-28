using System;
using UnityEngine;

/// <summary>
/// Produces the 217-value fair unified observation from the actor231 telemetry,
/// excluding duplicated derived/history fields and adding local accelerations.
/// </summary>
[DefaultExecutionOrder(-1300)]
public sealed class Gen3UnifiedObservation : MonoBehaviour
{
    private const float TeleportResetDistance = 20f;
    private readonly float[] actor231 = new float[ActorObservationContract.Size];
    private PlayerBody body;
    private MapIndependentTelemetry telemetry;
    private bool hasPrevious;
    private Vector3 previousPosition;
    private Vector3 previousVelocityLocal;
    private Vector3 previousEuler;
    private Vector3 previousAngularVelocity;

    public void Initialize(PlayerBody configuredBody)
    {
        body = configuredBody != null ? configuredBody : GetComponent<PlayerBody>();
        if (body == null)
            throw new InvalidOperationException("Gen3 unified observation requires PlayerBody");
        telemetry = GetComponent<MapIndependentTelemetry>();
        if (telemetry == null)
            telemetry = gameObject.AddComponent<MapIndependentTelemetry>();
        telemetry.Initialize(body);
        ResetMemory();
    }

    public void ResetMemory()
    {
        if (body == null)
            body = GetComponent<PlayerBody>();
        hasPrevious = false;
        previousPosition = body != null ? body.transform.position : Vector3.zero;
        previousVelocityLocal = Vector3.zero;
        previousEuler = body != null ? body.transform.eulerAngles : Vector3.zero;
        previousAngularVelocity = Vector3.zero;
        telemetry?.ResetMemory();
    }

    public void BuildObservation(float[] output, bool advanceState, float actualDeltaTime)
    {
        if (body == null || telemetry == null)
            Initialize(GetComponent<PlayerBody>());
        UnifiedFairObservationV2Contract.ValidateObservation(output, nameof(output));
        float dt = Mathf.Max(actualDeltaTime, 1e-4f);
        telemetry.BuildObservation(actor231, advanceState);

        Vector3 position = body.transform.position;
        Quaternion yaw = Quaternion.Euler(0f, body.transform.eulerAngles.y, 0f);
        Vector3 velocityWorld = body.Motor != null
            ? body.Motor.WorldVelocity
            : (position - previousPosition) / dt;
        Vector3 velocityLocal = Quaternion.Inverse(yaw) * velocityWorld;
        Vector3 euler = body.transform.eulerAngles;
        Vector3 angularVelocity = new Vector3(
            Mathf.DeltaAngle(previousEuler.x, euler.x),
            Mathf.DeltaAngle(previousEuler.y, euler.y),
            Mathf.DeltaAngle(previousEuler.z, euler.z)) / dt;

        bool reset = !hasPrevious
            || Vector3.Distance(position, previousPosition) > TeleportResetDistance;
        Vector3 linearAcceleration = reset
            ? Vector3.zero
            : (velocityLocal - previousVelocityLocal) / dt
                / UnifiedFairObservationV2Contract.LinearAccelerationScale;
        Vector3 angularAcceleration = reset
            ? Vector3.zero
            : (angularVelocity - previousAngularVelocity) / dt
                / UnifiedFairObservationV2Contract.AngularAccelerationScale;
        UnifiedFairObservationV2Contract.Encode(
            actor231, linearAcceleration, angularAcceleration, output);

        if (!advanceState)
            return;
        hasPrevious = true;
        previousPosition = position;
        previousVelocityLocal = velocityLocal;
        previousEuler = euler;
        previousAngularVelocity = angularVelocity;
    }
}

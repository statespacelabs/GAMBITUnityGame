using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents.Sensors;

/// <summary>Index contract for phase5_actor_obs_v001.</summary>
public static class Phase5ActorObservationLayout
{
    public const string SchemaId = "phase5_actor_obs_v001";
    public const int ObservationSize = 231;
    public const int SelfVelocity = 0;
    public const int SelfAngularVelocity = 3;
    public const int GroundedCrouched = 6;
    public const int HealthAmmoCooldown = 8;
    public const int RecentDamageCue = 12;
    public const int PreviousAction = 16;
    public const int ProgressStuckHistory = 24;
    public const int TorsoRays = 30;
    public const int FootHeadRays = 94;
    public const int FloorDropStepRays = 158;
    public const int CollisionNormal = 182;
    public const int FreeSpaceSummary = 185;
    public const int VisibleEnemyState = 193;
    public const int LastSeenMemory = 202;
    public const int LastHeardMemory = 207;
    public const int HuntCue = 211;
    public const int TacticalMode = 227;
}

/// <summary>
/// Opt-in ML-Agents sensor component. It is selected only when
/// PHASE5_TELEMETRY_SCHEMA=phase5_actor_obs_v001; local45 stays the default.
/// </summary>
public sealed class Phase5MapIndependentSensorComponent : SensorComponent
{
    public override ISensor[] CreateSensors()
    {
        GambitAgentController agent = GetComponent<GambitAgentController>();
        PlayerBody body = GetComponent<PlayerBody>();
        if (agent == null || body == null)
            throw new InvalidOperationException("[Phase5Telemetry] Missing GambitAgentController or PlayerBody");

        Phase5MapIndependentTelemetry telemetry = GetComponent<Phase5MapIndependentTelemetry>();
        if (telemetry == null)
            telemetry = gameObject.AddComponent<Phase5MapIndependentTelemetry>();
        telemetry.Initialize(body);
        return new ISensor[] { new Phase5MapIndependentSensor(telemetry) };
    }
}

public sealed class Phase5MapIndependentSensor : ISensor
{
    private readonly Phase5MapIndependentTelemetry telemetry;
    private readonly float[] cache = new float[Phase5ActorObservationLayout.ObservationSize];
    private bool hasCache;

    public Phase5MapIndependentSensor(Phase5MapIndependentTelemetry telemetry)
    {
        this.telemetry = telemetry;
    }

    public ObservationSpec GetObservationSpec() =>
        ObservationSpec.Vector(Phase5ActorObservationLayout.ObservationSize);

    public int Write(ObservationWriter writer)
    {
        if (!hasCache)
        {
            telemetry.BuildObservation(cache, false);
            hasCache = true;
        }
        for (int i = 0; i < cache.Length; i++)
            writer[i] = cache[i];
        return cache.Length;
    }

    public byte[] GetCompressedObservation() => null;

    public void Update()
    {
        telemetry.BuildObservation(cache, true);
        hasCache = true;
    }

    public void Reset()
    {
        telemetry.ResetMemory();
        hasCache = false;
    }

    public CompressionSpec GetCompressionSpec() => CompressionSpec.Default();
    public string GetName() => Phase5ActorObservationLayout.SchemaId;
}

/// <summary>
/// Map-independent, actor-yaw-relative observation builder. Hidden opponent
/// geometry is consulted only to determine LOS. Exact current target state is
/// written only in the visible branch. Hidden branches use stale event-time
/// memories and a rate-limited noisy coarse cue.
/// </summary>
public sealed class Phase5MapIndependentTelemetry : MonoBehaviour
{
    private const float LinearSpeedScale = 12f;
    private const float AngularSpeedScale = 720f;
    private const float RayDistance = 40f;
    private const float MemoryVectorScale = 50f;
    private const float HuntUpdatePeriod = 0.5f;
    private const float ProgressThreshold = 0.02f;
    private const int ShortHistory = 25;
    private const int LongHistory = 100;

    private PlayerBody self;
    private PlayerBody opponent;
    private MatchManager match;
    private CharacterController controller;
    private System.Random random;
    private bool initialized;

    private Vector3 previousPosition;
    private Vector3 previousEuler;
    private PlayerCommand previousAction;
    private float lastProgressTime;
    private readonly Queue<float> shortStuck = new Queue<float>();
    private readonly Queue<float> longStuck = new Queue<float>();
    private readonly Queue<float> collisionHistory = new Queue<float>();

    private bool recentDamageValid;
    private Vector3 recentDamageDirectionWorld;
    private float recentDamageTime;
    private bool lastSeenValid;
    private Vector3 lastSeenPointWorld;
    private float lastSeenTime;
    private bool lastHeardValid;
    private Vector3 lastHeardDirectionWorld;
    private float lastHeardTime;

    private float lastHuntUpdate = -999f;
    private bool huntValid;
    private bool huntDropped;
    private int huntBearingBin;
    private int huntDistanceBin;
    private float huntUpdatedAt;
    private float huntDropoutUntil = -999f;

    private readonly float[] torsoDistances = new float[32];
    private readonly float[] latestObservation =
        new float[Phase5ActorObservationLayout.ObservationSize];
    private bool hasLatestObservation;

    public PlayerBody SelfBody => self;
    public PlayerBody OpponentBody => opponent;

    public void Initialize(PlayerBody body)
    {
        if (initialized)
            return;
        self = body;
        match = body != null ? body.MatchManager : null;
        opponent = match != null && body.Identity != null ? match.GetOpponentBody(body.Identity) : null;
        controller = GetComponent<CharacterController>();
        random = new System.Random(StableSeed(gameObject.name));
        if (match != null)
        {
            match.OnHit += OnHit;
            match.OnRoundReset += OnReset;
            match.OnMatchReset += OnReset;
        }
        initialized = true;
        ResetMemory();
        Debug.Log("[Phase5Telemetry] initialized schema=" + Phase5ActorObservationLayout.SchemaId
            + " agent=" + gameObject.name + " dim=" + Phase5ActorObservationLayout.ObservationSize);
    }

    private void OnDestroy()
    {
        if (match != null)
        {
            match.OnHit -= OnHit;
            match.OnRoundReset -= OnReset;
            match.OnMatchReset -= OnReset;
        }
    }

    public void ResetMemory()
    {
        if (self == null)
            return;
        previousPosition = self.transform.position;
        previousEuler = self.transform.eulerAngles;
        previousAction = PlayerCommand.NoOp;
        lastProgressTime = Time.time;
        shortStuck.Clear();
        longStuck.Clear();
        collisionHistory.Clear();
        recentDamageValid = false;
        lastSeenValid = false;
        lastHeardValid = false;
        huntValid = false;
        huntDropped = false;
        lastHuntUpdate = -999f;
        huntDropoutUntil = -999f;
    }

    private void OnReset()
    {
        ResetMemory();
    }

    private void OnHit(PlayerIdentity shooter, PlayerIdentity victim)
    {
        if (self == null || victim != self.Identity || shooter == null)
            return;
        Vector3 worldDirection = shooter.transform.position - self.transform.position;
        worldDirection.y = 0f;
        if (worldDirection.sqrMagnitude <= 1e-6f)
            return;
        float jitterDegrees = NextSigned(12f);
        recentDamageDirectionWorld = Quaternion.AngleAxis(jitterDegrees, Vector3.up) * worldDirection.normalized;
        recentDamageValid = true;
        recentDamageTime = Time.time;
        lastHeardDirectionWorld = Quaternion.AngleAxis(NextSigned(18f), Vector3.up) * worldDirection.normalized;
        lastHeardValid = true;
        lastHeardTime = Time.time;
    }

    public void InjectFalseHeardCue(float localBearingDegrees)
    {
        if (self == null)
            return;
        Vector3 local = Quaternion.Euler(0f, localBearingDegrees, 0f) * Vector3.forward;
        lastHeardDirectionWorld = Quaternion.Euler(0f, self.transform.eulerAngles.y, 0f) * local;
        lastHeardValid = true;
        lastHeardTime = Time.time;
    }

    public void BuildObservation(float[] output, bool advanceState)
    {
        if (!initialized)
            Initialize(GetComponent<PlayerBody>());
        if (output == null || output.Length != Phase5ActorObservationLayout.ObservationSize)
            throw new ArgumentException("phase5_actor_obs_v001 requires exactly 231 values");
        Array.Clear(output, 0, output.Length);
        if (self == null || opponent == null || controller == null)
            return;

        float dt = Mathf.Max(Time.fixedDeltaTime, 1e-4f);
        Vector3 position = self.transform.position;
        Vector3 velocityWorld = self.Motor != null ? self.Motor.WorldVelocity : (position - previousPosition) / dt;
        Quaternion yawRotation = Quaternion.Euler(0f, self.transform.eulerAngles.y, 0f);
        Vector3 velocityLocal = Quaternion.Inverse(yawRotation) * velocityWorld;
        WriteVector(output, Phase5ActorObservationLayout.SelfVelocity, ClampVector(velocityLocal / LinearSpeedScale));

        Vector3 euler = self.transform.eulerAngles;
        Vector3 angular = new Vector3(
            Mathf.DeltaAngle(previousEuler.x, euler.x),
            Mathf.DeltaAngle(previousEuler.y, euler.y),
            Mathf.DeltaAngle(previousEuler.z, euler.z)) / dt / AngularSpeedScale;
        WriteVector(output, Phase5ActorObservationLayout.SelfAngularVelocity, ClampVector(angular));

        output[6] = controller.isGrounded ? 1f : 0f;
        output[7] = self.Motor != null && self.Motor.IsCrouched ? 1f : 0f;
        output[8] = self.Health != null ? self.Health.GetHealthNormalized() : 0f;
        output[9] = self.Weapon != null && self.Weapon.MaxAmmo > 0f ? Mathf.Clamp01(self.Weapon.CurrentAmmo / self.Weapon.MaxAmmo) : 0f;
        output[10] = 0f;
        output[11] = self.Weapon != null ? Mathf.Clamp01(self.Weapon.CooldownRemaining / Mathf.Max(0.001f, self.Weapon.FireCooldownSeconds)) : 0f;

        WriteDirectionCue(output, Phase5ActorObservationLayout.RecentDamageCue, recentDamageValid,
            recentDamageDirectionWorld, recentDamageTime, 5f, yawRotation);
        WriteAction(output, Phase5ActorObservationLayout.PreviousAction, previousAction);

        PlayerCommand currentCommand = self.Controller != null ? self.Controller.GetCommand() : PlayerCommand.NoOp;
        float displacement = Vector3.Distance(previousPosition, position);
        float planarSpeed = new Vector2(velocityWorld.x, velocityWorld.z).magnitude;
        bool movementRequested = Mathf.Abs(currentCommand.MoveX) + Mathf.Abs(currentCommand.MoveZ) > 0.2f;
        float stuck = movementRequested && displacement < ProgressThreshold ? 1f : 0f;
        float collision = self.Motor != null && self.Motor.LastCollisionFlags != CollisionFlags.None ? 1f : 0f;
        if (advanceState)
        {
            Push(shortStuck, stuck, ShortHistory);
            Push(longStuck, stuck, LongHistory);
            Push(collisionHistory, collision, ShortHistory);
            if (displacement >= ProgressThreshold)
                lastProgressTime = Time.time;
        }
        output[24] = Mathf.Clamp01(planarSpeed / LinearSpeedScale);
        output[25] = Mathf.Clamp01(displacement / 1f);
        output[26] = Mean(shortStuck);
        output[27] = Mean(longStuck);
        output[28] = Mean(collisionHistory);
        output[29] = Mathf.Clamp01((Time.time - lastProgressTime) / 5f);

        CastHorizontalRays(output, yawRotation);
        CastFloorRays(output, yawRotation);
        Vector3 collisionNormal = self.Motor != null ? self.Motor.LastCollisionNormalWorld : Vector3.zero;
        WriteVector(output, Phase5ActorObservationLayout.CollisionNormal,
            ClampVector(Quaternion.Inverse(yawRotation) * collisionNormal));
        for (int sector = 0; sector < 8; sector++)
        {
            float minimum = 1f;
            for (int ray = 0; ray < 4; ray++)
                minimum = Mathf.Min(minimum, torsoDistances[sector * 4 + ray]);
            output[Phase5ActorObservationLayout.FreeSpaceSummary + sector] = minimum;
        }

        bool visible = HasLineOfSight();
        if (visible)
        {
            Vector3 relativeWorld = opponent.transform.position - position;
            Vector3 relativeLocal = Quaternion.Inverse(yawRotation) * relativeWorld;
            Vector3 directionLocal = relativeLocal.sqrMagnitude > 1e-6f ? relativeLocal.normalized : Vector3.forward;
            float bearing = Mathf.Atan2(directionLocal.x, directionLocal.z);
            float elevation = Mathf.Asin(Mathf.Clamp(directionLocal.y, -1f, 1f));
            int at = Phase5ActorObservationLayout.VisibleEnemyState;
            output[at] = 1f;
            output[at + 1] = Mathf.Sin(bearing);
            output[at + 2] = Mathf.Cos(bearing);
            output[at + 3] = Mathf.Sin(elevation);
            output[at + 4] = Mathf.Cos(elevation);
            output[at + 5] = Mathf.Clamp01(Mathf.Log(1f + relativeWorld.magnitude) / Mathf.Log(101f));
            Vector3 relativeVelocity = opponent.Motor != null ? opponent.Motor.WorldVelocity - velocityWorld : -velocityWorld;
            WriteVector(output, at + 6, ClampVector(Quaternion.Inverse(yawRotation) * relativeVelocity / LinearSpeedScale));
            if (advanceState)
            {
                lastSeenValid = true;
                lastSeenPointWorld = opponent.transform.position;
                lastSeenTime = Time.time;
            }
        }

        if (lastSeenValid)
        {
            int at = Phase5ActorObservationLayout.LastSeenMemory;
            output[at] = 1f;
            Vector3 staleLocal = Quaternion.Inverse(yawRotation) * (lastSeenPointWorld - position);
            WriteVector(output, at + 1, ClampVector(staleLocal / MemoryVectorScale));
            output[at + 4] = Mathf.Clamp01((Time.time - lastSeenTime) / 10f);
        }
        WriteDirectionCue(output, Phase5ActorObservationLayout.LastHeardMemory, lastHeardValid,
            lastHeardDirectionWorld, lastHeardTime, 5f, yawRotation);

        if (advanceState && Time.time - lastHuntUpdate >= HuntUpdatePeriod - 1e-5f)
            UpdateHuntCue(visible, position, yawRotation);
        WriteHuntCue(output);

        int mode = visible ? 2 : (Mean(shortStuck) > 0.6f ? 3 : (lastSeenValid || lastHeardValid ? 1 : 0));
        output[Phase5ActorObservationLayout.TacticalMode + mode] = 1f;

        if (advanceState)
        {
            previousPosition = position;
            previousEuler = euler;
            previousAction = currentCommand;
            Array.Copy(output, latestObservation, output.Length);
            hasLatestObservation = true;
        }
        AssertFinite(output);
    }

    public bool CopyLatestObservation(float[] output)
    {
        if (output == null || output.Length != Phase5ActorObservationLayout.ObservationSize)
            throw new ArgumentException("phase5_actor_obs_v001 requires exactly 231 values");
        if (!hasLatestObservation)
            return false;
        Array.Copy(latestObservation, output, output.Length);
        return true;
    }

    private void CastHorizontalRays(float[] output, Quaternion yawRotation)
    {
        Vector3 torsoOrigin = self.transform.position + Vector3.up * 0.7f;
        for (int i = 0; i < 32; i++)
        {
            Vector3 direction = yawRotation * (Quaternion.Euler(0f, i * 11.25f, 0f) * Vector3.forward);
            bool hit = StaticRay(torsoOrigin, direction, RayDistance, out float distance);
            float normalized = hit ? Mathf.Clamp01(distance / RayDistance) : 1f;
            torsoDistances[i] = normalized;
            int at = Phase5ActorObservationLayout.TorsoRays + i * 2;
            output[at] = normalized;
            output[at + 1] = hit ? 1f : 0f;
        }
        for (int i = 0; i < 32; i++)
        {
            bool head = i >= 16;
            int radial = i % 16;
            float angle = radial * 22.5f + (head ? 11.25f : 0f);
            Vector3 origin = self.transform.position + Vector3.up * (head ? 1.45f : 0.25f);
            Vector3 direction = yawRotation * (Quaternion.Euler(0f, angle, 0f) * Vector3.forward);
            bool hit = StaticRay(origin, direction, RayDistance, out float distance);
            int at = Phase5ActorObservationLayout.FootHeadRays + i * 2;
            output[at] = hit ? Mathf.Clamp01(distance / RayDistance) : 1f;
            output[at + 1] = hit ? 1f : 0f;
        }
    }

    private void CastFloorRays(float[] output, Quaternion yawRotation)
    {
        for (int i = 0; i < 8; i++)
        {
            Vector3 direction = yawRotation * (Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward);
            Vector3 floorOrigin = self.transform.position + direction * 0.8f + Vector3.up * 1.2f;
            bool floorHit = StaticRay(floorOrigin, Vector3.down, 3f, out float floorDistance);
            bool footBlocked = StaticRay(self.transform.position + Vector3.up * 0.25f, direction, 1f, out _);
            bool headBlocked = StaticRay(self.transform.position + Vector3.up * 1.2f, direction, 1f, out _);
            int at = Phase5ActorObservationLayout.FloorDropStepRays + i * 3;
            output[at] = floorHit ? Mathf.Clamp01(floorDistance / 3f) : 1f;
            output[at + 1] = floorHit ? 1f : 0f;
            output[at + 2] = footBlocked && !headBlocked ? 1f : 0f;
        }
    }

    private bool StaticRay(Vector3 origin, Vector3 direction, float distance, out float hitDistance)
    {
        RaycastHit[] hits = Physics.RaycastAll(origin, direction.normalized, distance, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null)
                continue;
            PlayerBody body = hit.collider.GetComponentInParent<PlayerBody>();
            if (body != null)
                continue;
            hitDistance = hit.distance;
            return true;
        }
        hitDistance = distance;
        return false;
    }

    private bool HasLineOfSight()
    {
        Vector3 origin = self.transform.position + Vector3.up * 0.7f;
        Vector3 target = opponent.transform.position + Vector3.up * 0.7f;
        Vector3 delta = target - origin;
        if (delta.sqrMagnitude <= 1e-6f)
            return true;
        RaycastHit[] hits = Physics.RaycastAll(origin, delta.normalized, delta.magnitude + 0.05f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (RaycastHit hit in hits)
        {
            PlayerBody body = hit.collider != null ? hit.collider.GetComponentInParent<PlayerBody>() : null;
            if (body == self)
                continue;
            return body == opponent;
        }
        return false;
    }

    private void UpdateHuntCue(bool visible, Vector3 position, Quaternion yawRotation)
    {
        lastHuntUpdate = Time.time;
        huntUpdatedAt = Time.time;
        if (Time.time < huntDropoutUntil)
        {
            huntDropped = true;
        }
        else
        {
            huntDropped = random.NextDouble() < ReadProbability("PHASE5_HUNT_CUE_DROPOUT", 0.15f);
            if (huntDropped)
            {
                float minimum = ReadScalar("PHASE5_HUNT_DROPOUT_MIN_SECONDS", 0f, 0f, 30f);
                float maximum = ReadScalar("PHASE5_HUNT_DROPOUT_MAX_SECONDS", minimum, minimum, 30f);
                if (maximum > 0f)
                    huntDropoutUntil = Time.time + minimum
                        + (float)random.NextDouble() * (maximum - minimum);
            }
        }
        // The actor contract permits a rate-limited, quantized, noisy hunt cue
        // while LOS is false. Exact hidden geometry is used only inside this
        // quantizer and is never written to the observation.
        bool sourceValid = opponent != null;
        huntValid = sourceValid && !huntDropped;
        if (!huntValid)
            return;
        Vector3 sourcePoint = opponent.transform.position;
        Vector3 local = Quaternion.Inverse(yawRotation) * (sourcePoint - position);
        float angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        int baseSector = Mathf.FloorToInt(Mathf.Repeat(angle + 180f, 360f) / 45f);
        int jitter = random.NextDouble() < 0.2 ? -1 : (random.NextDouble() > 0.75 ? 1 : 0);
        huntBearingBin = (baseSector + jitter + 8) % 8;
        float distance = local.magnitude;
        int bin = distance < 4f ? 0 : distance < 8f ? 1 : distance < 16f ? 2 : distance < 32f ? 3 : 4;
        int distanceJitter = random.NextDouble() < 0.15 ? -1 : (random.NextDouble() > 0.82 ? 1 : 0);
        huntDistanceBin = Mathf.Clamp(bin + distanceJitter, 0, 4);
    }

    private void WriteHuntCue(float[] output)
    {
        int at = Phase5ActorObservationLayout.HuntCue;
        if (huntValid)
        {
            output[at + huntBearingBin] = 1f;
            output[at + 8 + huntDistanceBin] = 1f;
            output[at + 13] = 1f;
        }
        output[at + 14] = Mathf.Clamp01((Time.time - huntUpdatedAt) / 2f);
        output[at + 15] = huntDropped ? 1f : 0f;
    }

    private void WriteDirectionCue(float[] output, int at, bool valid, Vector3 directionWorld,
        float eventTime, float ageScale, Quaternion yawRotation)
    {
        if (!valid)
            return;
        Vector3 local = Quaternion.Inverse(yawRotation) * directionWorld;
        float bearing = Mathf.Atan2(local.x, local.z);
        output[at] = 1f;
        output[at + 1] = Mathf.Sin(bearing);
        output[at + 2] = Mathf.Cos(bearing);
        output[at + 3] = Mathf.Clamp01((Time.time - eventTime) / ageScale);
    }

    private static void WriteAction(float[] output, int at, PlayerCommand command)
    {
        output[at] = Mathf.Clamp(command.MoveX, -1f, 1f);
        output[at + 1] = Mathf.Clamp(command.MoveZ, -1f, 1f);
        output[at + 2] = Mathf.Clamp(command.Turn, -1f, 1f);
        output[at + 3] = Mathf.Clamp(command.LookPitch, -1f, 1f);
        output[at + 4] = command.Shoot ? 1f : 0f;
        output[at + 5] = command.Reload ? 1f : 0f;
        output[at + 6] = command.Jump ? 1f : 0f;
        output[at + 7] = command.Crouch ? 1f : 0f;
    }

    private static Vector3 ClampVector(Vector3 value) => new Vector3(
        Mathf.Clamp(value.x, -1f, 1f), Mathf.Clamp(value.y, -1f, 1f), Mathf.Clamp(value.z, -1f, 1f));

    private static void WriteVector(float[] output, int at, Vector3 value)
    {
        output[at] = value.x; output[at + 1] = value.y; output[at + 2] = value.z;
    }

    private static void Push(Queue<float> values, float value, int capacity)
    {
        values.Enqueue(value);
        while (values.Count > capacity)
            values.Dequeue();
    }

    private static float Mean(Queue<float> values)
    {
        if (values.Count == 0)
            return 0f;
        float sum = 0f;
        foreach (float value in values)
            sum += value;
        return sum / values.Count;
    }

    private float NextSigned(float maximumDegrees) =>
        (float)(random.NextDouble() * 2.0 - 1.0) * maximumDegrees;

    private static float ReadProbability(string name, float fallback)
    {
        string raw = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(raw) && float.TryParse(raw,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)
            ? Mathf.Clamp01(value) : fallback;
    }

    private static float ReadScalar(string name, float fallback, float minimum, float maximum)
    {
        string raw = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(raw) && float.TryParse(raw,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
            return Mathf.Clamp(value, minimum, maximum);
        return fallback;
    }

    private static int StableSeed(string value)
    {
        unchecked
        {
            int hash = 17;
            foreach (char ch in value ?? "")
                hash = hash * 31 + ch;
            return hash ^ 52025;
        }
    }

    private static void AssertFinite(float[] values)
    {
        for (int i = 0; i < values.Length; i++)
            if (float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                throw new InvalidOperationException("phase5_actor_obs_v001 non-finite value at index " + i);
    }
}

using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// Gambit ML-Agents action/reward controller. A sensor component installed by
/// PolicyInstaller supplies either local45 telemetry or the Gen3 unified token.
///
/// Unlike RLAgentController which sends 12 simplified floats, this sends the
/// exact telemetry schema that the encoder was trained on:
///   where_tel (10): local target vector, local velocity, local acceleration, distance_to_opponent
///   view_tel  (13): target-relative sin/cos aim, local view velocity/acceleration, aim errors
///   rhythm_tel(22): action type counts (14) + timing features (8)
///   obs_schema_version: phase3v2_c_local45
///
/// Total vector observations: LocalObservationContract.Size
/// Visual observations: 1 camera sensor (224x224 RGB) — configured via AddCameraSensor
///
/// Action Space (Phase 3 hybrid — matches the distilled recurrent PPO policy):
///   Continuous (4): move_x, move_y, look_dx (yaw), look_dy (pitch), each in [-1, 1]
///   Discrete   (4 binary branches of size 2): shoot, reload, jump, crouch
/// </summary>
public class GambitAgentController : Agent, IPlayerController
{
    [Header("Gambit Settings")]
    [Tooltip("Camera to capture frames from for the encoder")]
    public Camera AgentCamera;

    [Tooltip("Resolution for the camera sensor")]
    public int CameraWidth = 224;
    public int CameraHeight = 224;

    /// <summary>
    /// When enabled, continuous actions contain only yaw and pitch. A separate
    /// controller owns final command composition with the frozen navigator.
    /// </summary>
    public bool UseNavigationAssistedActions { get; set; }

    private PlayerIdentity identity;
    private MatchManager matchManager;
    private PlayerBody selfBody;
    private PlayerBody opponentBody;
    private PlayerCommand currentCommand;
    private PlayerHealth selfHealth;
    private PlayerHealth opponentHealth;
    private CharacterController characterController;

    // --- Telemetry state for finite differences ---
    private Vector3 prevPosition;
    private Vector3 prevVelocity;
    private Vector3 prevEulerAngles;
    private Vector3 prevViewVelocity;

    // --- Rhythm tracking ---
    private float lastActionTime = -1f;
    private float lastShotTime = -1f;
    private float lastReloadTime = -1f;
    private float lastTargetedActionTime = -1f;
    private bool lastCommandWasShoot = false;
    private bool shotFiredThisStep = false;
    private float episodeStartTime;

    // Phase 3H-Fix hit-mechanics probe: HIT_PROBE_ORACLE=1 ignores the policy and
    // makes the learner stand still + fire perfectly-aimed oracle shots at the
    // opponent, to prove the hit -> damage -> +0.2 reward chain.
    private bool oracleMode = false;

    public void Initialize(PlayerIdentity identity, MatchManager matchManager)
    {
        this.identity = identity;
        this.matchManager = matchManager;

        selfBody = identity.GetComponent<PlayerBody>();
        opponentBody = matchManager.GetOpponentBody(identity);

        selfHealth = selfBody.Health;
        opponentHealth = opponentBody?.Health;
        characterController = GetComponent<CharacterController>();

        // Initialize telemetry state
        prevPosition = transform.position;
        prevVelocity = Vector3.zero;
        prevEulerAngles = transform.eulerAngles;
        prevViewVelocity = Vector3.zero;
        // Subscribe to events
        matchManager.OnRewardSignal += OnRewardReceived;
        matchManager.OnKill += OnKillEvent;
        matchManager.OnHit += OnHitEvent;

        episodeStartTime = Time.time;
        oracleMode = System.Environment.GetEnvironmentVariable("HIT_PROBE_ORACLE") == "1";
        if (oracleMode)
            Debug.Log($"[GambitAgent] HIT_PROBE_ORACLE on for {identity.DisplayName} — ignoring policy, firing oracle shots.");

        // Setup camera sensor if camera is assigned
        if (AgentCamera == null)
        {
            AgentCamera = Camera.main;
        }

        Debug.Log($"[GambitAgent] Initialized for {identity.DisplayName}");
    }

    /// <summary>
    /// Initializes only the local45 telemetry state for an in-process policy.
    /// The ML-Agents Agent remains disabled and does not connect to Python.
    /// </summary>
    public void InitializeTelemetryOnly(PlayerIdentity configuredIdentity, MatchManager configuredMatchManager)
    {
        identity = configuredIdentity;
        matchManager = configuredMatchManager;
        selfBody = identity != null ? identity.GetComponent<PlayerBody>() : null;
        opponentBody = matchManager != null && identity != null
            ? matchManager.GetOpponentBody(identity) : null;
        selfHealth = selfBody != null ? selfBody.Health : null;
        opponentHealth = opponentBody != null ? opponentBody.Health : null;
        characterController = GetComponent<CharacterController>();
        prevPosition = transform.position;
        prevVelocity = Vector3.zero;
        prevEulerAngles = transform.eulerAngles;
        prevViewVelocity = Vector3.zero;
        episodeStartTime = Time.time;
        oracleMode = false;
        currentCommand = PlayerCommand.NoOp;
        lastActionTime = -1f;
        lastShotTime = -1f;
        lastReloadTime = -1f;
        lastTargetedActionTime = -1f;
        lastCommandWasShoot = false;
        shotFiredThisStep = false;
        Debug.Log("[GambitAgent] telemetry-only initialized for "
            + (identity != null ? identity.DisplayName : gameObject.name));
    }

    public void SetExternalCommandForTelemetry(PlayerCommand command)
    {
        if (command.Reload && !currentCommand.Reload)
            lastReloadTime = Time.time - episodeStartTime;
        currentCommand = command;
        lastCommandWasShoot = command.Shoot;
    }

    private void OnDestroy()
    {
        if (matchManager != null)
        {
            matchManager.OnRewardSignal -= OnRewardReceived;
            matchManager.OnKill -= OnKillEvent;
            matchManager.OnHit -= OnHitEvent;
        }
    }

    // ─────────────────────────────────────────────────────
    // IPlayerController Implementation
    // ─────────────────────────────────────────────────────

    public PlayerCommand GetCommand()
    {
        return currentCommand;
    }

    // ─────────────────────────────────────────────────────
    // ML-Agents Agent Overrides
    // ─────────────────────────────────────────────────────

    public override void OnEpisodeBegin()
    {
        currentCommand = PlayerCommand.NoOp;
        episodeStartTime = Time.time;
        lastActionTime = -1f;
        lastShotTime = -1f;
        lastReloadTime = -1f;
        lastTargetedActionTime = -1f;
        prevPosition = transform.position;
        prevVelocity = Vector3.zero;
        prevEulerAngles = transform.eulerAngles;
        prevViewVelocity = Vector3.zero;

        if (matchManager != null)
        {
            if (System.Environment.GetEnvironmentVariable("FORCE_MATCH_RESET_ON_EPISODE_BEGIN") == "1")
            {
                matchManager.ResetMatch();
            }
            else
            {
                matchManager.ResetRound();
            }
        }


        shotFiredThisStep = false;
        lastCommandWasShoot = false;
        decisionStepCounter = 0;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // No-op: the local45 observation is supplied by the custom
        // GambitTelemetrySensor (ISensor) via BuildTelemetryObservation, and the
        // default vector sensor is configured with VectorObservationSize = 0.
        // Adding observations here conflicts with the size-0 sensor (NRE in
        // Agent.SendInfoToBrain) and would double-count the telemetry.
    }

    /// <summary>
    /// Fill the frozen local45 telemetry cache. When advanceState is true the
    /// finite-difference state (prev position/velocity/angles) is advanced; pass
    /// false for a read-only peek (used by GambitTelemetrySensor.Write).
    /// </summary>
    public void NotifyShotFired()
    {
        shotFiredThisStep = true;
    }

    public void BuildTelemetryObservation(float[] obs, bool advanceState)
    {
        LocalObservationContract.ValidateBuffer(obs, nameof(obs));
        if (opponentBody == null || selfBody == null)
        {
            System.Array.Clear(obs, 0, obs.Length);
            return;
        }

        float dt = Time.fixedDeltaTime;
        if (dt < 1e-6f)
            dt = LocalObservationContract.DefaultSimulationDeltaSeconds;
        float tRel = Time.time - episodeStartTime;

        // WHERE_TEL - phase3v2_c_local45, 10 dims:
        // target vector in self-local frame (3), self velocity in self-local frame (3),
        // self acceleration in self-local frame (3), distance to opponent (1).
        const float teleportResetDistance = 20f;
        const float maxLocalLinearSpeed = 50f;
        const float maxLocalLinearAccel = 200f;
        const float maxViewSpeed = 720f;
        const float maxViewAccel = 2000f;
        Vector3 pos = transform.position;
        bool resetFiniteDiff = Vector3.Distance(pos, prevPosition) > teleportResetDistance;
        Vector3 vel = resetFiniteDiff ? Vector3.zero : (pos - prevPosition) / dt;
        Vector3 acc = resetFiniteDiff ? Vector3.zero : (vel - prevVelocity) / dt;
        Vector3 relToOpponentWorld = opponentBody.transform.position - pos;
        Vector3 relToOpponentLocal = transform.InverseTransformDirection(relToOpponentWorld);
        Vector3 velLocal = transform.InverseTransformDirection(vel);
        Vector3 accLocal = transform.InverseTransformDirection(acc);
        velLocal = new Vector3(
            Mathf.Clamp(velLocal.x, -maxLocalLinearSpeed, maxLocalLinearSpeed),
            Mathf.Clamp(velLocal.y, -maxLocalLinearSpeed, maxLocalLinearSpeed),
            Mathf.Clamp(velLocal.z, -maxLocalLinearSpeed, maxLocalLinearSpeed)
        );
        accLocal = new Vector3(
            Mathf.Clamp(accLocal.x, -maxLocalLinearAccel, maxLocalLinearAccel),
            Mathf.Clamp(accLocal.y, -maxLocalLinearAccel, maxLocalLinearAccel),
            Mathf.Clamp(accLocal.z, -maxLocalLinearAccel, maxLocalLinearAccel)
        );
        float distToOpponent = relToOpponentWorld.magnitude;

        // VIEW_TEL - phase3v2_c_local45, 13 dims:
        // target-relative sin/cos aim terms, local view velocity/acceleration, yaw/pitch/magnitude errors.
        Vector3 euler = transform.eulerAngles;
        Vector3 dirToOpp = relToOpponentWorld.normalized;
        float yawErr = Vector3.SignedAngle(transform.forward, dirToOpp, Vector3.up);
        Vector3 localDir = transform.InverseTransformDirection(dirToOpp);
        float pitchErr = -Mathf.Atan2(localDir.y, localDir.z) * Mathf.Rad2Deg;
        float errMag = Mathf.Sqrt(yawErr * yawErr + pitchErr * pitchErr);
        float pitchErrRad = pitchErr * Mathf.Deg2Rad;
        float yawErrRad = yawErr * Mathf.Deg2Rad;
        float sinPitchErr = Mathf.Sin(pitchErrRad);
        float cosPitchErr = Mathf.Cos(pitchErrRad);
        float sinYawErr = Mathf.Sin(yawErrRad);
        float cosYawErr = Mathf.Cos(yawErrRad);
        Vector3 viewVel = resetFiniteDiff ? Vector3.zero : new Vector3(
            Mathf.DeltaAngle(prevEulerAngles.x, euler.x),
            Mathf.DeltaAngle(prevEulerAngles.y, euler.y),
            Mathf.DeltaAngle(prevEulerAngles.z, euler.z)
        ) / dt;
        viewVel = new Vector3(
            Mathf.Clamp(viewVel.x, -maxViewSpeed, maxViewSpeed),
            Mathf.Clamp(viewVel.y, -maxViewSpeed, maxViewSpeed),
            Mathf.Clamp(viewVel.z, -maxViewSpeed, maxViewSpeed)
        );
        Vector3 viewAcc = resetFiniteDiff ? Vector3.zero : (viewVel - prevViewVelocity) / dt;
        viewAcc = new Vector3(
            Mathf.Clamp(viewAcc.x, -maxViewAccel, maxViewAccel),
            Mathf.Clamp(viewAcc.y, -maxViewAccel, maxViewAccel),
            Mathf.Clamp(viewAcc.z, -maxViewAccel, maxViewAccel)
        );
        // The released rhythm vocabulary intentionally records only shooting.
        // The remaining 13 category slots stay zero to preserve the trained model contract.
        if (lastCommandWasShoot && advanceState)
        {
            lastShotTime = tRel;
            lastActionTime = tRel;
        }
        float oppHpFrac = 1f;
        if (opponentHealth != null)
            oppHpFrac = opponentHealth.GetHealthNormalized();

        LocalObservationEncoder.Encode(new LocalObservationFrame
        {
            TargetLocal = relToOpponentLocal,
            SelfVelocityLocal = velLocal,
            SelfAccelerationLocal = accLocal,
            DistanceToOpponent = distToOpponent,
            SinPitchError = sinPitchErr,
            CosPitchError = cosPitchErr,
            SinYawError = sinYawErr,
            CosYawError = cosYawErr,
            ViewVelocity = viewVel,
            ViewAcceleration = viewAcc,
            YawErrorDegrees = yawErr,
            PitchErrorDegrees = pitchErr,
            AimErrorDegrees = errMag,
            ShootCommand = lastCommandWasShoot,
            OpponentHealthFraction = oppHpFrac,
            TimeSinceAnyAction = TimeSince(tRel, lastActionTime),
            TimeSinceShot = TimeSince(tRel, lastShotTime),
            TimeSinceReload = TimeSince(tRel, lastReloadTime),
            TimeSinceTargetedHit = TimeSince(tRel, lastTargetedActionTime),
            ShotFiredThisStep = shotFiredThisStep
        }, obs);

        if (advanceState)
        {
            prevPosition = pos;
            prevVelocity = vel;
            prevEulerAngles = euler;
            prevViewVelocity = viewVel;
            shotFiredThisStep = false;
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        decisionStepCounter++;
        if (oracleMode)
        {
            // Ignore the policy: stand still and fire a perfectly-aimed oracle shot
            // at the opponent's chest (cooldown-gated inside the weapon).
            currentCommand = PlayerCommand.NoOp;
            if (selfBody != null && opponentBody != null && selfBody.Weapon != null)
            {
                Vector3 aimPoint = opponentBody.transform.position + Vector3.up * 0.5f;
                selfBody.Weapon.TryShootAt(aimPoint);
            }
            return;
        }

        // A standalone policy supplies movement + aim. A navigation-assisted
        // local45 policy supplies only aim because actor231 owns movement.
        var ca = actions.ContinuousActions;
        float moveX = UseNavigationAssistedActions ? 0f : Mathf.Clamp(ca[0], -1f, 1f);
        float moveZ = UseNavigationAssistedActions ? 0f : Mathf.Clamp(ca[1], -1f, 1f);
        int aimOffset = UseNavigationAssistedActions ? 0 : 2;
        float lookDx = Mathf.Clamp(ca[aimOffset], -1f, 1f);
        float lookDy = Mathf.Clamp(ca[aimOffset + 1], -1f, 1f);

        // Discrete (4 binary branches): shoot, reload, jump, crouch.
        var da = actions.DiscreteActions;
        bool shoot = da[0] == 1;
        bool reload = da[1] == 1;
        bool jump = da[2] == 1;
        bool crouch = da[3] == 1;

        lastCommandWasShoot = shoot;
        if (reload && !currentCommand.Reload)
            lastReloadTime = Time.time - episodeStartTime;

        currentCommand = new PlayerCommand
        {
            MoveX = moveX,
            MoveZ = moveZ,
            Turn = lookDx,
            LookPitch = lookDy,
            Shoot = shoot,
            Reload = reload,
            Jump = jump,
            Crouch = crouch
        };
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // Fallback: small random continuous look/move + random binaries for testing.
        var ca = actionsOut.ContinuousActions;
        for (int index = 0; index < ca.Length; index++)
            ca[index] = UnityEngine.Random.Range(-1f, 1f);

        var da = actionsOut.DiscreteActions;
        da[0] = UnityEngine.Random.Range(0, 2);
        da[1] = UnityEngine.Random.Range(0, 2);
        da[2] = UnityEngine.Random.Range(0, 2);
        da[3] = UnityEngine.Random.Range(0, 2);
    }

    // ─────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────

    private int decisionStepCounter = 0;

    public int GetDecisionStepCounter() => decisionStepCounter;

    public string GetAreaKey()
    {
        if (identity != null && !string.IsNullOrEmpty(identity.DisplayName))
        {
            string name = identity.DisplayName;
            int idx = name.IndexOf('_');
            return idx > 0 ? name.Substring(0, idx) : name;
        }
        return gameObject.name;
    }

    /// <summary>Phase 3Z: weapon-ray to opponent hurtbox angular miss (degrees).</summary>
    public float ComputeTrueRayHurtboxErrorDeg(
        out float rayToHurtboxMinDistance,
        out float rayToHurtboxCenterDistance)
    {
        rayToHurtboxMinDistance = 999f;
        rayToHurtboxCenterDistance = 999f;
        if (opponentBody == null)
        {
            return 999f;
        }
        PlayerWeapon weapon = GetComponent<PlayerWeapon>();
        if (weapon == null || weapon.AimOrigin == null)
        {
            return 999f;
        }
        Vector3 origin = weapon.AimOrigin.position;
        Vector3 dir = weapon.AimOrigin.forward.normalized;
        GetOpponentHurtboxBounds(out Vector3 hitboxMin, out Vector3 hitboxMax);
        Bounds hurtbox = new Bounds(
            (hitboxMin + hitboxMax) * 0.5f,
            hitboxMax - hitboxMin);
        rayToHurtboxCenterDistance = Vector3.Distance(origin, hurtbox.center);
        Ray weaponRay = new Ray(origin, dir);
        float inflate = LearnerShootGeometry.HurtboxInflateRadius;
        rayToHurtboxMinDistance = LearnerShootGeometry.RayToBoundsMinDistance(
            weaponRay, hurtbox, inflate, out _, out Vector3 closestOnBox);
        Vector3 toClosest = closestOnBox - origin;
        if (toClosest.sqrMagnitude > 1e-8f)
        {
            return LearnerShootGeometry.AngleBetween(dir, toClosest);
        }
        float targetDist = Mathf.Max(rayToHurtboxCenterDistance, 0.1f);
        return Mathf.Atan(rayToHurtboxMinDistance / targetDist) * Mathf.Rad2Deg;
    }

    private void GetOpponentHurtboxBounds(out Vector3 hitboxMin, out Vector3 hitboxMax)
    {
        Vector3 targetPos = opponentBody.transform.position;
        Collider[] cols = opponentBody.GetComponentsInChildren<Collider>();
        if (cols != null && cols.Length > 0)
        {
            hitboxMin = cols[0].bounds.min;
            hitboxMax = cols[0].bounds.max;
            for (int i = 1; i < cols.Length; i++)
            {
                hitboxMin = Vector3.Min(hitboxMin, cols[i].bounds.min);
                hitboxMax = Vector3.Max(hitboxMax, cols[i].bounds.max);
            }
            return;
        }
        hitboxMin = targetPos - Vector3.one * 0.5f;
        hitboxMax = targetPos + Vector3.one * 0.5f;
    }

    public void GetShotGeometrySnapshot(
        out float aimErr, out float yawErr, out float pitchErr, out float dist,
        out Vector3 targetPos, out Vector3 chestPos, out Vector3 hitboxMin,
        out Vector3 hitboxMax, out float shotFiredObs, out int decisionStep,
        out bool aligned)
    {
        decisionStep = decisionStepCounter;
        shotFiredObs = shotFiredThisStep ? 1f : 0f;
        if (opponentBody == null)
        {
            aimErr = 999f; yawErr = 999f; pitchErr = 999f; dist = 0f;
            targetPos = Vector3.zero; chestPos = Vector3.zero;
            hitboxMin = Vector3.zero; hitboxMax = Vector3.zero;
            aligned = false;
            return;
        }
        targetPos = opponentBody.transform.position;
        chestPos = targetPos + Vector3.up * 0.5f;
        dist = Vector3.Distance(transform.position, targetPos);
        Vector3 dirToOpp = (targetPos - transform.position).normalized;
        yawErr = Vector3.SignedAngle(transform.forward, dirToOpp, Vector3.up);
        Vector3 localDir = transform.InverseTransformDirection(dirToOpp);
        pitchErr = -Mathf.Atan2(localDir.y, localDir.z) * Mathf.Rad2Deg;
        aimErr = Mathf.Sqrt(yawErr * yawErr + pitchErr * pitchErr);
        aligned = aimErr < 30f;
        Collider[] cols = opponentBody.GetComponentsInChildren<Collider>();
        if (cols != null && cols.Length > 0)
        {
            hitboxMin = cols[0].bounds.min;
            hitboxMax = cols[0].bounds.max;
            for (int i = 1; i < cols.Length; i++)
            {
                hitboxMin = Vector3.Min(hitboxMin, cols[i].bounds.min);
                hitboxMax = Vector3.Max(hitboxMax, cols[i].bounds.max);
            }
        }
        else
        {
            hitboxMin = targetPos - Vector3.one * 0.5f;
            hitboxMax = targetPos + Vector3.one * 0.5f;
        }
    }

    private float TimeSince(float now, float lastTime)
    {
        if (lastTime < 0f) return 1f; // Normalized max
        float elapsed = Mathf.Clamp(now - lastTime, 0f, LocalObservationContract.TimingWindowSeconds);
        return elapsed / LocalObservationContract.TimingWindowSeconds;
    }

    private void OnRewardReceived(PlayerIdentity player, float reward, string reason)
    {
        if (player != identity) return;
        AddReward(reward);
    }

    private void OnKillEvent(PlayerIdentity killer, PlayerIdentity victim)
    {
        if (killer == identity || victim == identity)
        {
            EndEpisode();
        }
    }

    private void OnHitEvent(PlayerIdentity shooter, PlayerIdentity victim)
    {
        // Track targeted actions for rhythm telemetry
        if (shooter == identity)
        {
            lastTargetedActionTime = Time.time - episodeStartTime;
        }
    }
}

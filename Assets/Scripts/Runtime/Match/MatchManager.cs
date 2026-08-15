using UnityEngine;
using System;

/// <summary>
/// Source of truth for all match state. Owns scoring, health resets,
/// cooldown, spawn resets, and event notifications.
///
/// Kill event order per plan:
///   1. Final hit is registered
///   2. Shooter receives +0.2 hit score
///   3. Victim health reaches 0 or lower
///   4. Kill is registered
///   5. Killer receives +1.0 kill score
///   6. Death count increments
///   7. Health resets according to config
///   8. Positions reset if config says so
///   9. Global cooldown starts
///  10. Match resumes after cooldown
/// </summary>
public class MatchManager : MonoBehaviour
{
    [Header("Configuration")]
    public MatchConfig Config;

    [Header("Players")]
    public PlayerBody PlayerA;
    public PlayerBody PlayerB;

    [Header("Spawn Points")]
    public SpawnPoint SpawnPointA;
    public SpawnPoint SpawnPointB;

    // --- Match State ---
    public bool IsInCooldown { get; private set; }
    public float CooldownRemaining { get; private set; }
    public bool IsMatchOver { get; private set; }
    public bool CombatReady { get; private set; } = true;
    public int ResetGeneration => resetGeneration;

    // --- Events ---
    public event Action<PlayerIdentity, PlayerIdentity> OnHit;       // shooter, victim
    public event Action<PlayerIdentity> OnMiss;                       // shooter
    public event Action<PlayerIdentity, PlayerIdentity> OnKill;      // killer, victim
    public event Action<float> OnCooldownStarted;                     // duration
    public event Action OnCooldownEnded;
    public event Action OnRoundReset;
    public event Action OnMatchReset;
    public event Action OnMatchOver;
    public event Action<PlayerIdentity, PlayerIdentity, float> OnDeferredContinuationStarted; // killer, victim, safety seconds
    public event Action<float, float> OnContinuousContactTimeout; // penalty, safety seconds

    // --- RL Reward Events ---
    /// <summary>
    /// Fired to deliver a reward signal to a specific player's RL agent.
    /// Parameters: (playerIdentity, rewardValue, rewardReason)
    /// </summary>
    public event Action<PlayerIdentity, float, string> OnRewardSignal;

    private float matchStartTime;
    private int resetGeneration;
    private float deferredDamageImmunityUntil;
    private bool deferredTerminalEventDispatchActive;
    private float lastContinuousRecoveryTime = -999f;
    private float lastContinuousContactTime = -999f;
    private float lastContinuousLosMarkTime = -999f;

    public bool IsDeferredDamageImmunityActive
    {
        get { return Time.time < deferredDamageImmunityUntil; }
    }

    public float DeferredDamageImmunityRemaining
    {
        get { return Mathf.Max(0f, deferredDamageImmunityUntil - Time.time); }
    }

    public bool ContinuousContactTimerActive
    {
        get { return IsContinuousContactTimerEnabled() && IsContinuousChallengersEnabled() && !IsMatchOver; }
    }

    public float ContinuousContactTimeoutSeconds
    {
        get { return Mathf.Max(0f, ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_CONTACT_TIMEOUT_SECONDS", 45f)); }
    }

    public float ContinuousContactTimeoutPenalty
    {
        get { return Mathf.Max(0f, ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_CONTACT_TIMEOUT_PENALTY", 0.2f)); }
    }

    public float ContinuousContactTimeRemaining
    {
        get
        {
            float timeout = ContinuousContactTimeoutSeconds;
            if (!ContinuousContactTimerActive || timeout <= 0f)
                return 0f;
            float last = lastContinuousContactTime > 0f ? lastContinuousContactTime : Time.time;
            return Mathf.Clamp(timeout - (Time.time - last), 0f, timeout);
        }
    }

    public bool ShouldSuppressEpisodeEndForDeferredContinuation
    {
        get { return deferredTerminalEventDispatchActive; }
    }

    private void Start()
    {
        if (Config == null)
        {
            Debug.LogError("[MatchManager] No MatchConfig assigned! Loading default from Resources...");
            Config = Resources.Load<MatchConfig>("DefaultMatchConfig");

            if (Config == null)
            {
                Debug.LogError("[MatchManager] No DefaultMatchConfig found in Resources. Creating runtime defaults.");
                Config = ScriptableObject.CreateInstance<MatchConfig>();
            }
        }

        matchStartTime = Time.time;
        lastContinuousContactTime = Time.time;
    }

    private void Update()
    {
        if (GambitRuntimeMode.IsHeadless)
            return;
        TickMatch();
    }

    private void FixedUpdate()
    {
        if (!GambitRuntimeMode.IsHeadless)
            return;
        TickMatch();
    }

    private void TickMatch()
    {
        // Tick cooldown
        if (IsInCooldown)
        {
            CooldownRemaining -= Time.deltaTime;
            if (CooldownRemaining <= 0f)
            {
                IsInCooldown = false;
                CooldownRemaining = 0f;
                Debug.Log("[MatchManager] Cooldown ended. Round resuming.");
                OnCooldownEnded?.Invoke();
            }
        }

        // Deliver timestep penalty to RL agents (only when not in cooldown)
        if (!IsInCooldown && !IsMatchOver)
        {
            if (PlayerA != null)
                OnRewardSignal?.Invoke(PlayerA.Identity, Config.RewardTimestep, "timestep");
            if (PlayerB != null)
                OnRewardSignal?.Invoke(PlayerB.Identity, Config.RewardTimestep, "timestep");
        }

        RecoverContinuousOutOfBoundsIfNeeded();
        UpdateContinuousContactTimer();
    }

    // --- Permission Queries ---

    public bool CanPlayerShoot(PlayerIdentity player)
    {
        return CombatReady && IsKnownPlayer(player) && !IsInCooldown && !IsMatchOver;
    }

    public bool CanPlayerTakeDamage(PlayerIdentity player)
    {
        return CombatReady && IsKnownPlayer(player) && !IsInCooldown && !IsMatchOver && !IsDeferredDamageImmunityActive;
    }

    public bool CanPlayerMove(PlayerIdentity player)
    {
        if (IsMatchOver) return false;
        if (IsInCooldown && !Config.AllowMovementDuringCooldown) return false;
        return true;
    }

    // --- Event Registration ---

    /// <summary>
    /// Called by PlayerHealth when a valid hit occurs (before kill check).
    /// Awards hit score to the shooter.
    /// </summary>
    public void RegisterHit(PlayerIdentity shooter, PlayerIdentity victim)
    {
        // Award hit score
        shooter.Hits++;
        shooter.Score += Config.ScorePerHit;

        Debug.Log($"[MatchManager] HIT: {shooter.DisplayName} → {victim.DisplayName} | " +
                  $"Victim HP: {GetPlayerBody(victim).Health.CurrentHealth} | " +
                  $"Shooter Score: {shooter.Score:F1}");
        MarkContinuousContact("hit");

        // RL rewards
        OnRewardSignal?.Invoke(shooter, Config.RewardHitOpponent, "hit_opponent");
        OnRewardSignal?.Invoke(victim, Config.RewardGetHit, "got_hit");

        OnHit?.Invoke(shooter, victim);
    }

    /// <summary>
    /// Called by PlayerWeapon when a shot misses all valid targets.
    /// </summary>
    public void RegisterMiss(PlayerIdentity shooter)
    {
        OnRewardSignal?.Invoke(shooter, Config.RewardMiss, "miss");
        OnMiss?.Invoke(shooter);
    }

    /// <summary>
    /// Called by PlayerHealth when a hit reduces health to 0 or below.
    /// Handles the full kill sequence per plan ordering.
    /// </summary>
    public void RegisterKill(PlayerIdentity killer, PlayerIdentity victim)
    {
        // Step 5: Award kill score
        killer.Kills++;
        killer.Score += Config.ScorePerKill;

        // Step 6: Increment death count
        victim.Deaths++;

        Debug.Log($"[MatchManager] KILL: {killer.DisplayName} killed {victim.DisplayName} | " +
                  $"Killer Score: {killer.Score:F1} | K:{killer.Kills} D:{victim.Deaths}");
        MarkContinuousContact("kill");

        // RL rewards
        OnRewardSignal?.Invoke(killer, Config.RewardKillOpponent, "kill_opponent");
        OnRewardSignal?.Invoke(victim, Config.RewardDie, "died");

        if (ShouldUseDeferredContinuation())
        {
            DispatchDeferredKillEvent(killer, victim);
            ContinueAfterDeferredKill(killer, victim);
            return;
        }

        // Check match end only after the Phase 4.5 minimum evidence window has
        // allowed terminal events to complete the live trial.
        if (Config.KillsPerMatch > 0 && (killer.Kills >= Config.KillsPerMatch))
        {
            EndMatch(killer);
            OnKill?.Invoke(killer, victim);
            return;
        }

        // Step 7: Health resets
        if (Config.ResetBothHealthAfterKill)
        {
            PlayerA.Health.ResetHealth();
            PlayerB.Health.ResetHealth();
        }
        else
        {
            GetPlayerBody(victim).Health.ResetHealth();
        }

        // Step 8: Position resets
        if (Config.ResetPositionsAfterKill)
        {
            ResetPositions();
        }

        // Step 9: Global cooldown starts
        StartCooldown();

        // Agents may call EndEpisode() from this event. Fire it only after the
        // authoritative match state has finished updating for this kill.
        OnKill?.Invoke(killer, victim);
    }

    // --- Match Control ---

    private void StartCooldown()
    {
        IsInCooldown = true;
        CooldownRemaining = Config.GlobalCooldownSeconds;
        Debug.Log($"[MatchManager] Cooldown started: {Config.GlobalCooldownSeconds}s");
        OnCooldownStarted?.Invoke(Config.GlobalCooldownSeconds);
    }

    private bool ShouldUseDeferredContinuation()
    {
        // Continuous challenger sessions are research-only. Keep the legacy
        // environment contract inert in normal release launches while allowing
        // an explicitly requested continuous session to retain its old reset path.
        return System.Environment.GetEnvironmentVariable(
            "PHASE4_5_CONTINUOUS_CHALLENGERS") == "1";
    }

    private void DispatchDeferredKillEvent(PlayerIdentity killer, PlayerIdentity victim)
    {
        deferredTerminalEventDispatchActive = true;
        try
        {
            OnKill?.Invoke(killer, victim);
        }
        finally
        {
            deferredTerminalEventDispatchActive = false;
        }
    }

    private void ContinueAfterDeferredKill(PlayerIdentity killer, PlayerIdentity victim)
    {
        CombatReady = false;
        int generation = ++resetGeneration;

        if (Config.ResetBothHealthAfterKill)
        {
            PlayerA.Health.ResetHealth();
            PlayerB.Health.ResetHealth();
        }
        else
        {
            GetPlayerBody(victim).Health.ResetHealth();
        }

        if (PlayerA.Weapon != null)
            PlayerA.Weapon.ResetForRound("DeferredKillContinuation", generation);
        if (PlayerB.Weapon != null)
            PlayerB.Weapon.ResetForRound("DeferredKillContinuation", generation);

        IsInCooldown = false;
        CooldownRemaining = 0f;
        IsMatchOver = false;
        bool continuousChallenger = IsContinuousChallengersEnabled();
        bool repositioned = false;
        if (continuousChallenger)
            repositioned = TryRespawnContinuousChallenger(generation);

        float safetySeconds = ParseFloatEnv("PHASE4_5_DEFERRED_SAFETY_SECONDS", 2f);
        deferredDamageImmunityUntil = Time.time + Mathf.Max(0f, safetySeconds);
        Physics.SyncTransforms();
        CombatReady = true;

        if (continuousChallenger)
            OnRoundReset?.Invoke();

        Debug.Log($"[MatchManager] Deferred kill continuation: killer={killer.DisplayName} victim={victim.DisplayName} safety={safetySeconds:F2}s generation={generation}; continuous_challenger={(continuousChallenger ? 1 : 0)} repositioned={(repositioned ? 1 : 0)}.");
        OnDeferredContinuationStarted?.Invoke(killer, victim, Mathf.Max(0f, safetySeconds));
    }


    private void RecoverContinuousOutOfBoundsIfNeeded()
    {
        if (!IsContinuousChallengersEnabled()) return;
        if (PlayerA == null || PlayerB == null || PlayerA.Motor == null || PlayerB.Motor == null) return;
        if (Time.time - lastContinuousRecoveryTime < 1.0f) return;

        Vector3 a = PlayerA.transform.position;
        Vector3 b = PlayerB.transform.position;
        float minY = ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_MIN_VALID_Y", -25f);
        float maxSeparation = ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_MAX_SEPARATION", 180f);
        bool playerMapInvalid = DemoMapRuntime.ControlEnabled
            && (DemoMapRuntime.IsFallDetected(a) || DemoMapRuntime.IsOutOfBounds(a));
        bool opponentMapInvalid = DemoMapRuntime.ControlEnabled
            && (DemoMapRuntime.IsFallDetected(b) || DemoMapRuntime.IsOutOfBounds(b));
        bool playerInvalid = !IsFiniteVector(a) || a.y < minY || playerMapInvalid;
        bool opponentInvalid = !IsFiniteVector(b) || b.y < minY || opponentMapInvalid;
        bool separated = !playerInvalid && !opponentInvalid && Vector3.Distance(a, b) > Mathf.Max(30f, maxSeparation);
        if (!playerInvalid && !opponentInvalid && !separated) return;

        lastContinuousRecoveryTime = Time.time;
        CombatReady = false;
        int generation = ++resetGeneration;
        if (playerInvalid)
            ResetPositions();
        PlayerA.Health.ResetHealth();
        PlayerB.Health.ResetHealth();
        if (PlayerA.Weapon != null)
            PlayerA.Weapon.ResetForRound("ContinuousOutOfBoundsRecovery", generation);
        if (PlayerB.Weapon != null)
            PlayerB.Weapon.ResetForRound("ContinuousOutOfBoundsRecovery", generation);

        bool repositioned = TryRespawnContinuousChallenger(generation);
        float safetySeconds = ParseFloatEnv("PHASE4_5_DEFERRED_SAFETY_SECONDS", 2f);
        deferredDamageImmunityUntil = Time.time + Mathf.Max(0f, safetySeconds);
        Physics.SyncTransforms();
        CombatReady = true;
        OnRoundReset?.Invoke();
        OnDeferredContinuationStarted?.Invoke(PlayerA.Identity, PlayerB.Identity, Mathf.Max(0f, safetySeconds));
        Debug.LogWarning($"[MatchManager] Continuous challenger recovery generation={generation} player_invalid={(playerInvalid ? 1 : 0)} opponent_invalid={(opponentInvalid ? 1 : 0)} separated={(separated ? 1 : 0)} repositioned={(repositioned ? 1 : 0)} safety={safetySeconds:F2}s");
    }

    private bool IsFiniteVector(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z)
            && !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
    }

    private bool IsContinuousContactTimerEnabled()
    {
        string value = System.Environment.GetEnvironmentVariable("PHASE4_5_CONTINUOUS_CHALLENGER_CONTACT_TIMEOUT");
        return value == "1" || value == "true" || value == "True";
    }

    private void MarkContinuousContact(string reason)
    {
        if (!IsContinuousContactTimerEnabled())
            return;
        lastContinuousContactTime = Time.time;
    }

    private void UpdateContinuousContactTimer()
    {
        if (!ContinuousContactTimerActive || PlayerA == null || PlayerB == null || PlayerA.Identity == null || PlayerB.Identity == null)
            return;
        if (!CombatReady || IsInCooldown || IsDeferredDamageImmunityActive)
            return;

        float timeout = ContinuousContactTimeoutSeconds;
        if (timeout <= 0f)
            return;
        if (lastContinuousContactTime <= 0f)
            lastContinuousContactTime = Time.time;
        if (Time.time - lastContinuousContactTime >= timeout)
            HandleContinuousContactTimeout(timeout);
    }

    private void HandleContinuousContactTimeout(float timeout)
    {
        CombatReady = false;
        int generation = ++resetGeneration;
        float penalty = ContinuousContactTimeoutPenalty;
        if (penalty > 0f && PlayerA != null && PlayerA.Identity != null)
        {
            PlayerA.Identity.Score -= penalty;
            OnRewardSignal?.Invoke(PlayerA.Identity, -penalty, "continuous_contact_timeout");
        }

        PlayerA.Health.ResetHealth();
        PlayerB.Health.ResetHealth();
        if (PlayerA.Weapon != null)
            PlayerA.Weapon.ResetForRound("ContinuousContactTimeout", generation);
        if (PlayerB.Weapon != null)
            PlayerB.Weapon.ResetForRound("ContinuousContactTimeout", generation);

        bool repositioned = TryRespawnContinuousChallenger(generation);
        float safetySeconds = ParseFloatEnv("PHASE4_5_DEFERRED_SAFETY_SECONDS", 2f);
        deferredDamageImmunityUntil = Time.time + Mathf.Max(0f, safetySeconds);
        Physics.SyncTransforms();
        CombatReady = true;
        MarkContinuousContact("timeout_respawn");
        OnRoundReset?.Invoke();
        OnContinuousContactTimeout?.Invoke(penalty, Mathf.Max(0f, safetySeconds));
        Debug.LogWarning($"[MatchManager] Continuous contact timeout after {timeout:F1}s; penalty={penalty:F2} generation={generation} repositioned={(repositioned ? 1 : 0)} safety={safetySeconds:F2}s");
    }

    private bool IsContinuousChallengersEnabled()
    {
        return System.Environment.GetEnvironmentVariable("PHASE4_5_CONTINUOUS_CHALLENGERS") == "1";
    }


    private bool HasContinuousLineOfSight()
    {
        if (PlayerA == null || PlayerB == null) return false;
        Vector3 origin = PlayerA.transform.position + Vector3.up * 0.7f;
        Vector3 target = PlayerB.transform.position + Vector3.up * 0.7f;
        Vector3 delta = target - origin;
        float distance = delta.magnitude;
        if (distance <= 1e-3f) return true;

        RaycastHit[] hits = Physics.RaycastAll(origin, delta.normalized, distance + 0.5f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (IsInHierarchy(hit.transform, PlayerA.transform))
                continue;
            if (IsInHierarchy(hit.transform, PlayerB.transform))
                return true;
            return false;
        }
        return true;
    }

    private static bool HasLineOfSightAtPositions(Vector3 positionA, Vector3 positionB)
    {
        Vector3 origin = positionA + Vector3.up * 0.7f;
        Vector3 target = positionB + Vector3.up * 0.7f;
        Vector3 delta = target - origin;
        float distance = delta.magnitude;
        if (distance <= 1e-3f)
            return true;
        return !Physics.Raycast(
            origin,
            delta.normalized,
            distance + 0.05f,
            ~0,
            QueryTriggerInteraction.Ignore);
    }

    private bool IsInHierarchy(Transform candidate, Transform root)
    {
        if (candidate == null || root == null) return false;
        return candidate == root || candidate.IsChildOf(root);
    }

    private bool TryRespawnContinuousChallenger(int generation)
    {
        if (PlayerA == null || PlayerB == null || PlayerA.Motor == null || PlayerB.Motor == null)
            return false;

        float minDistance = ParseFloatEnv(
            "PHASE4_5_CONTINUOUS_CHALLENGER_MIN_DISTANCE",
            ParseFloatEnv("PHASE4_5_PILOT_SPAWN_DISTANCE_MIN", ParseFloatEnv("PHASE4_4_SPAWN_DISTANCE_MIN", 12f)));
        float maxDistance = ParseFloatEnv(
            "PHASE4_5_CONTINUOUS_CHALLENGER_MAX_DISTANCE",
            ParseFloatEnv("PHASE4_5_PILOT_SPAWN_DISTANCE_MAX", ParseFloatEnv("PHASE4_4_SPAWN_DISTANCE_MAX", 24f)));
        if (maxDistance < minDistance)
            maxDistance = minDistance;
        minDistance = Mathf.Max(3f, minDistance);
        maxDistance = Mathf.Max(minDistance, maxDistance);

        float finalMinDistance = Mathf.Clamp(
            ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_FINAL_MIN_DISTANCE", 6f),
            3f,
            maxDistance);
        float distanceDecay = Mathf.Clamp(
            ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_DISTANCE_DECAY", 0.72f),
            0.35f,
            0.95f);
        int rings = Mathf.Clamp((int)ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_DISTANCE_RINGS", 6f), 1, 12);
        int attempts = Mathf.Clamp((int)ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_ATTEMPTS", 144f), rings, 512);
        int attemptsPerRing = Mathf.Max(8, Mathf.CeilToInt(attempts / (float)rings));
        bool requireCover = System.Environment.GetEnvironmentVariable("PHASE4_5_CONTINUOUS_CHALLENGER_REQUIRE_COVER") == "1";
        float minRelocationDistance = Mathf.Max(0f, ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_MIN_RELOCATION_DISTANCE", 8f));

        Vector3 anchor = PlayerA.transform.position;
        if (!IsFiniteVector(anchor))
            return false;
        Quaternion playerRotation = PlayerA.transform.rotation;
        Vector3 previousOpponent = PlayerB.transform.position;
        Vector3 sampleAnchor = anchor;
        sampleAnchor.y = Mathf.Max(sampleAnchor.y, 1f);

        int seed = generation * 73856093 ^ Mathf.RoundToInt(Time.time * 1000f) ^ Time.frameCount * 19349663;
        System.Random rng = new System.Random(seed);
        Vector3 clearFallback = Vector3.zero;
        int clearFallbackRing = -1;
        int clearFallbackAttempt = -1;
        bool haveClearFallback = false;

        for (int ring = 0; ring < rings; ring++)
        {
            float ringOuter = Mathf.Max(finalMinDistance, maxDistance * Mathf.Pow(distanceDecay, ring));
            float ringInner = Mathf.Max(finalMinDistance, ringOuter * 0.65f);
            if (ring == 0)
                ringInner = Mathf.Min(Mathf.Max(finalMinDistance, minDistance), ringOuter);
            if (ringInner > ringOuter)
            {
                float tmp = ringInner;
                ringInner = ringOuter;
                ringOuter = tmp;
            }

            for (int attempt = 0; attempt < attemptsPerRing; attempt++)
            {
                float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                float t = (float)rng.NextDouble();
                float distance = Mathf.Lerp(ringInner, ringOuter, t);
                Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                Vector3 candidate = sampleAnchor + dir * distance;

                if (!TryProjectRespawnPosition(ref candidate, sampleAnchor.y))
                    continue;
                if (HorizontalDistance(anchor, candidate) < finalMinDistance)
                    continue;
                if (IsFiniteVector(previousOpponent) && HorizontalDistance(previousOpponent, candidate) < minRelocationDistance)
                    continue;
                if (!IsRespawnPositionClear(candidate))
                    continue;

                bool covered = !HasLineOfSightAtPositions(anchor, candidate);
                if (covered)
                {
                    PlaceContinuousChallenger(anchor, playerRotation, candidate, generation, covered, ring, attempt, "covered_decay");
                    return true;
                }

                if (!haveClearFallback)
                {
                    clearFallback = candidate;
                    clearFallbackRing = ring;
                    clearFallbackAttempt = attempt;
                    haveClearFallback = true;
                }
            }
        }

        if (!requireCover && haveClearFallback)
        {
            PlaceContinuousChallenger(anchor, playerRotation, clearFallback, generation, false, clearFallbackRing, clearFallbackAttempt, "clear_decay_fallback");
            return true;
        }

        if (TryRespawnContinuousChallengerRadialFallback(anchor, playerRotation, previousOpponent, generation, finalMinDistance, maxDistance, minRelocationDistance))
            return true;

        if (TryRespawnContinuousChallengerAtSpawnPoint(anchor, playerRotation, generation))
            return true;

        Debug.LogWarning($"[MatchManager] Continuous challenger respawn failed after {attemptsPerRing * rings} decay attempts; preserving current positions.");
        return false;
    }

    private void PlaceContinuousChallenger(Vector3 anchor, Quaternion playerRotation, Vector3 candidate, int generation, bool covered, int ring, int attempt, string source)
    {
        Vector3 facePlayer = anchor - candidate;
        facePlayer.y = 0f;
        Quaternion botRotation = facePlayer.sqrMagnitude > 1e-4f
            ? Quaternion.LookRotation(facePlayer.normalized, Vector3.up)
            : PlayerB.transform.rotation;

        PlayerA.Motor.TeleportTo(anchor, playerRotation);
        PlayerB.Motor.TeleportTo(candidate, botRotation);
        lastContinuousContactTime = Time.time;
        lastContinuousLosMarkTime = Time.time;
        Debug.Log(
            $"[MatchManager] Continuous challenger respawn generation={generation} source={source} " +
            $"distance={HorizontalDistance(anchor, candidate):F2} covered={(covered ? 1 : 0)} ring={ring} attempt={attempt} " +
            $"candidate=({candidate.x:F2},{candidate.y:F2},{candidate.z:F2})");
    }

    private bool TryRespawnContinuousChallengerRadialFallback(Vector3 anchor, Quaternion playerRotation, Vector3 previousOpponent, int generation, float minDistance, float maxDistance, float minRelocationDistance)
    {
        int attempts = Mathf.Clamp((int)ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_FALLBACK_ATTEMPTS", 48f), 8, 128);
        float fallbackDistance = Mathf.Clamp(
            ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_FALLBACK_DISTANCE", Mathf.Max(minDistance, Mathf.Min(maxDistance, 12f))),
            minDistance,
            Mathf.Max(minDistance, maxDistance));

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            float angle = (generation * 137.50777f + attempt * 31.0f) * Mathf.Deg2Rad;
            float distance = Mathf.Clamp(fallbackDistance + (attempt % 4) * 2f, minDistance, Mathf.Max(minDistance, maxDistance));
            Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Vector3 candidate = anchor + dir * distance;

            if (!TryProjectRespawnPosition(ref candidate, anchor.y))
            {
                if (DemoMapRuntime.ControlEnabled)
                    continue;
                candidate.y = anchor.y;
            }
            if (IsFiniteVector(previousOpponent) && HorizontalDistance(previousOpponent, candidate) < minRelocationDistance)
                continue;
            if (!IsRespawnPositionClear(candidate))
                continue;

            bool covered = !HasLineOfSightAtPositions(anchor, candidate);
            PlaceContinuousChallenger(anchor, playerRotation, candidate, generation, covered, -2, attempt, "radial_height_fallback");
            return true;
        }
        return false;
    }

    private bool TryRespawnContinuousChallengerAtSpawnPoint(Vector3 anchor, Quaternion playerRotation, int generation)
    {
        if (SpawnPointB == null)
            return false;
        Vector3 candidate = SpawnPointB.transform.position;
        if (!IsFiniteVector(candidate))
            return false;
        if (!TryProjectRespawnPosition(ref candidate, anchor.y) && DemoMapRuntime.ControlEnabled)
            return false;
        bool covered = !HasLineOfSightAtPositions(anchor, candidate);
        PlaceContinuousChallenger(anchor, playerRotation, candidate, generation, covered, -1, -1, "spawn_point_fallback");
        return true;
    }

    private bool TryProjectRespawnPosition(ref Vector3 position, float preferredY)
    {
        if (DemoMapRuntime.ControlEnabled)
        {
            Vector3 mapSurface;
            string mapReason;
            if (!DemoMapRuntime.IsValid || !DemoMapRuntime.TryProjectToSurface(position, out mapSurface, out mapReason))
                return false;
            position = mapSurface;
            return IsFiniteVector(position);
        }

        Vector3 origin = position + Vector3.up * 30f;
        RaycastHit hit;
        if (!Physics.Raycast(origin, Vector3.down, out hit, 80f, ~0, QueryTriggerInteraction.Ignore))
            return false;
        if (hit.normal.y < 0.35f)
            return false;
        position = hit.point;
        position.y = Mathf.Max(hit.point.y + 0.05f, preferredY);
        return IsFiniteVector(position);
    }

    private float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private bool IsRespawnPositionClear(Vector3 position)
    {
        if (!IsFiniteVector(position))
            return false;
        if (DemoMapRuntime.ControlEnabled)
        {
            bool mapClear = DemoMapRuntime.IsValid
                && DemoMapRuntime.IsInsideHorizontalBounds(position, 0f)
                && !DemoMapRuntime.IsFallDetected(position)
                && !DemoMapRuntime.IsOutOfBounds(position)
                && DemoMapRuntime.HasCapsuleClearance(position);
            return mapClear && HasRespawnLocalClearance(position);
        }

        return HasRespawnLocalClearance(position);
    }

    private bool HasRespawnLocalClearance(Vector3 position)
    {
        float radius = Mathf.Clamp(ParseFloatEnv("PHASE4_5_CONTINUOUS_CHALLENGER_CLEARANCE_RADIUS", 0.62f), 0.45f, 1.25f);
        Vector3 bottom = position + Vector3.down * 0.80f;
        Vector3 top = position + Vector3.up * 0.85f;
        Collider[] overlaps = Physics.OverlapCapsule(bottom, top, radius, ~0, QueryTriggerInteraction.Ignore);
        foreach (Collider overlap in overlaps)
        {
            if (overlap == null)
                continue;
            Transform tr = overlap.transform;
            if (PlayerA != null && IsInHierarchy(tr, PlayerA.transform))
                continue;
            if (PlayerB != null && IsInHierarchy(tr, PlayerB.transform))
                continue;
            return false;
        }
        return true;
    }

    private static float ParseFloatEnv(string key, float fallback)
    {
        string value = System.Environment.GetEnvironmentVariable(key);
        float parsed;
        if (!string.IsNullOrEmpty(value) && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed))
            return parsed;
        return fallback;
    }

    private void ResetPositions()
    {
        if (SpawnPointA != null && PlayerA != null)
        {
            PlayerA.Motor.TeleportTo(SpawnPointA.GetSpawnPosition(), SpawnPointA.GetSpawnRotation());
        }
        if (SpawnPointB != null && PlayerB != null)
        {
            PlayerB.Motor.TeleportTo(SpawnPointB.GetSpawnPosition(), SpawnPointB.GetSpawnRotation());
        }
        Debug.Log("[MatchManager] Players reset to spawn positions.");
    }

    public void ResetRound()
    {
        ResetCombatState("ResetRound");

        MarkContinuousContact("manual_round_reset");
        OnRoundReset?.Invoke();
        Debug.Log($"[MatchManager] Round reset generation={resetGeneration}.");
    }

    public void ResetMatch()
    {
        PlayerA.Identity.ResetStats();
        PlayerB.Identity.ResetStats();
        ResetCombatState("ResetMatch");
        matchStartTime = Time.time;
        MarkContinuousContact("manual_match_reset");

        OnMatchReset?.Invoke();
        Debug.Log($"[MatchManager] Match reset generation={resetGeneration}.");
    }

    private void ResetCombatState(string reason)
    {
        CombatReady = false;
        int generation = ++resetGeneration;

        PlayerA.Health.ResetHealth();
        PlayerB.Health.ResetHealth();
        ResetPositions();

        IsInCooldown = false;
        CooldownRemaining = 0f;
        IsMatchOver = false;

        if (PlayerA.Weapon != null)
            PlayerA.Weapon.ResetForRound(reason, generation);
        if (PlayerB.Weapon != null)
            PlayerB.Weapon.ResetForRound(reason, generation);

        Physics.SyncTransforms();
        AssertCombatReady(PlayerA);
        AssertCombatReady(PlayerB);

        CombatReady = true;
    }

    private void AssertCombatReady(PlayerBody player)
    {
        Debug.Assert(player != null, "[MatchManager] Missing player during reset.");
        if (player == null) return;
        Debug.Assert(player.Health.CurrentHealth == player.Health.MaxHealth, $"[MatchManager] {player.Identity.DisplayName} health not reset.");
        Debug.Assert(player.Weapon != null, $"[MatchManager] {player.Identity.DisplayName} missing weapon.");
        if (player.Weapon == null) return;
        Debug.Assert(player.Weapon.OwnerIdentity == player.Identity, $"[MatchManager] {player.Identity.DisplayName} weapon owner mismatch.");
        Debug.Assert(player.Weapon.gameObject.activeInHierarchy, $"[MatchManager] {player.Identity.DisplayName} weapon inactive.");
        Debug.Assert(player.Weapon.enabled, $"[MatchManager] {player.Identity.DisplayName} weapon disabled.");
        Debug.Assert(player.Weapon.CooldownRemaining <= 0f, $"[MatchManager] {player.Identity.DisplayName} weapon cooldown not reset.");
    }

    private void EndMatch(PlayerIdentity winner)
    {
        IsMatchOver = true;
        Debug.Log($"[MatchManager] MATCH OVER! Winner: {winner.DisplayName} " +
                  $"(Score: {winner.Score:F1}, Kills: {winner.Kills})");
        OnMatchOver?.Invoke();
    }

    // --- Utility ---

    private bool IsKnownPlayer(PlayerIdentity player)
    {
        return player != null && PlayerA != null && PlayerB != null &&
               (player == PlayerA.Identity || player == PlayerB.Identity);
    }

    /// <summary>
    /// Returns the opponent of the given player.
    /// </summary>
    public PlayerIdentity GetOpponent(PlayerIdentity player)
    {
        if (player == PlayerA.Identity) return PlayerB.Identity;
        if (player == PlayerB.Identity) return PlayerA.Identity;
        Debug.LogWarning($"[MatchManager] GetOpponent: unknown player {player.DisplayName}");
        return null;
    }

    /// <summary>
    /// Returns the opponent's PlayerBody.
    /// </summary>
    public PlayerBody GetOpponentBody(PlayerIdentity player)
    {
        if (player == PlayerA.Identity) return PlayerB;
        if (player == PlayerB.Identity) return PlayerA;
        return null;
    }

    /// <summary>
    /// Returns the PlayerBody for a given identity.
    /// </summary>
    public PlayerBody GetPlayerBody(PlayerIdentity identity)
    {
        if (identity == PlayerA.Identity) return PlayerA;
        if (identity == PlayerB.Identity) return PlayerB;
        return null;
    }
}

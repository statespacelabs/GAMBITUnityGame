using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Unity.MLAgents;
using UnityEngine;

/// <summary>
/// Opt-in autonomous Phase 5 session owner. It keeps one Unity process alive
/// across lives and challenger changes, owns reset boundaries, and emits one
/// complete telemetry row per cleared session.
/// </summary>
[DefaultExecutionOrder(31000)]
public sealed class Phase5AutonomousSession : MonoBehaviour
{
    public const string SchemaVersion = "phase5_autonomous_session_v001";
    public const string AuditSchemaVersion = "phase5_autonomous_harness_audit_v001";

    private static readonly List<Phase5AutonomousSession> Instances =
        new List<Phase5AutonomousSession>();
    private static bool? enabledFromEnvironment;
    private static bool auditWritten;
    private static string globalFailure = "";
    private static readonly int ProcessId = Process.GetCurrentProcess().Id;
    private static readonly string ProcessStartToken =
        ProcessId.ToString(CultureInfo.InvariantCulture)
        + ":" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

    private static readonly OpponentSpec[] FrozenRoster =
    {
        new OpponentSpec("stationary_unarmed", ScriptedBotController.ScriptedBotMode.Idle, false),
        new OpponentSpec("evasive_unarmed", ScriptedBotController.ScriptedBotMode.RandomStrafe, false),
        new OpponentSpec("strafe_duelist", ScriptedBotController.ScriptedBotMode.StrafeAndFaceShoot, true),
        new OpponentSpec("pursuit_duelist", ScriptedBotController.ScriptedBotMode.ChaseOpponent, true),
        new OpponentSpec("retreat_duelist", ScriptedBotController.ScriptedBotMode.RetreatAndShoot, true),
        new OpponentSpec("hold_angle_armed", ScriptedBotController.ScriptedBotMode.HoldAngleShoot, true),
        new OpponentSpec("los_breaker_unarmed", ScriptedBotController.ScriptedBotMode.StrafeAndFace, false),
        new OpponentSpec("doorway_cross_stop", ScriptedBotController.ScriptedBotMode.DoorwayCrossAndStop, false),
        new OpponentSpec("cover_direction_change", ScriptedBotController.ScriptedBotMode.CoverDirectionChange, false),
        new OpponentSpec("two_obstacle_retreat", ScriptedBotController.ScriptedBotMode.TwoObstacleRetreat, false),
        new OpponentSpec("kite_through_cover", ScriptedBotController.ScriptedBotMode.KiteThroughCover, false),
        new OpponentSpec("false_noisy_sound", ScriptedBotController.ScriptedBotMode.CoverDirectionChange, false),
        new OpponentSpec("stale_last_seen", ScriptedBotController.ScriptedBotMode.DoorwayCrossAndStop, false),
        new OpponentSpec("cue_dropout_kite", ScriptedBotController.ScriptedBotMode.KiteThroughCover, false),
        new OpponentSpec("slow_hold_angle_shooter", ScriptedBotController.ScriptedBotMode.SlowHoldAngleShoot, true),
        new OpponentSpec("accurate_hold_angle_shooter", ScriptedBotController.ScriptedBotMode.HoldAngleShoot, true),
        new OpponentSpec("cover_user_armed", ScriptedBotController.ScriptedBotMode.KiteThroughCoverShoot, true),
    };

    private int areaId;
    private MatchManager match;
    private PlayerBody candidate;
    private PlayerBody opponent;
    private GambitAgentController candidateAgent;
    private ScriptedBotController opponentController;
    private Phase5MapIndependentTelemetry telemetry;
    private PresetConfig preset;
    private int targetSessions;
    private string runId;
    private int layoutSeed;
    private string layoutSplit = "";
    private string layoutFamily = "";
    private Vector3 canonicalSpawnA;
    private Vector3 canonicalSpawnB;
    private bool requestedInitialLos;
    private float optionalGeodesicRouteLength;

    private int sessionOrdinal;
    private int completedSessions;
    private int currentChallenge;
    private int completedChallenges;
    private int sessionClearCount;
    private bool initialized;
    private bool finished;
    private bool harnessApplyingReset;
    private bool pendingAgentResetSuppression;
    private bool pendingBoundary;
    private string pendingBoundaryReason = "";
    private int suppressedAgentResets;
    private int resetCorruptions;
    private int missingTelemetry;
    private int memoryResetCount;
    private int gruResetRequests;
    private int actionHistoryResetCount;
    private int tacticalModeResetCount;
    private int peekHistoryResetCount;

    private float sessionStartedAt;
    private float challengeStartedAt;
    private float firstContactSeconds = -1f;
    private float initialEuclideanDistance;
    private float pathDistance;
    private float pathAtFirstContact;
    private float stuckSeconds;
    private int sideCollisionCount;
    private int losAcquisitions;
    private int losReacquisitions;
    private bool hadLos;
    private bool lostLosAfterAcquisition;
    private int kills;
    private int deaths;
    private int timeouts;
    private int fixedSamples;
    private int saturatedActionDimensions;
    private int actionDimensionsObserved;
    private float[] tacticalModeSeconds = new float[4];
    private Vector3 previousPosition;
    private int previousSideCollisionSteps;
    private float candidateDamageAtStart;
    private float opponentDamageAtStart;
    private readonly float[] actorObservation =
        new float[Phase5ActorObservationLayout.ObservationSize];
    private readonly List<string> challengerIds = new List<string>();
    private readonly List<SearchEventRecord> searchEvents = new List<SearchEventRecord>();
    private bool lastSeenReachedEvent;
    private bool localSearchStartedEvent;
    private bool searchGoalStaleEvent;
    private bool wasActorStuck;
    private int losLostEvents;
    private int lastSeenReachedEvents;
    private int localSearchStartedEvents;
    private int reacquiredEvents;
    private int searchGoalStaleEvents;
    private int unstuckTriggeredEvents;

    public static bool Enabled
    {
        get
        {
            if (!enabledFromEnvironment.HasValue)
                enabledFromEnvironment =
                    (Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_SESSION") ?? "").Trim() == "1";
            return enabledFromEnvironment.Value;
        }
    }

    public static bool ValidateGlobalLaunch()
    {
        string raw = (Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_SESSION") ?? "").Trim();
        if (string.IsNullOrEmpty(raw) || raw == "0")
            return true;
        if (raw != "1")
            return FailGlobal("enable_flag_invalid:" + raw);
        if (!Phase5HeadlessRuntime.Enabled && !Phase5RenderedSmokeRuntime.Enabled)
            return FailGlobal("requires_phase5_headless");
        bool procedural = Phase5ProceduralArenaRuntime.Enabled
            && Phase5ProceduralArenaRuntime.IsValid;
        bool authoredTeacher = Phase5GenericPrivilegedTeacher.Enabled
            && DemoMapRuntime.ControlEnabled
            && DemoMapRuntime.IsValid;
        if (!procedural && !authoredTeacher)
            return FailGlobal("requires_valid_procedural_or_teacher_authored_layout");
        if ((Environment.GetEnvironmentVariable("GAME_MODE") ?? "") != "HumanVsScripted")
            return FailGlobal("game_mode_must_be_HumanVsScripted");
        if ((Environment.GetEnvironmentVariable("ENABLE_VISUAL_OBS") ?? "0") == "1")
            return FailGlobal("visual_observations_forbidden");
        if ((Environment.GetEnvironmentVariable("PHASE4_5_CONTINUOUS_CHALLENGERS") ?? "") != "1")
            return FailGlobal("continuous_challengers_required");
        if ((Environment.GetEnvironmentVariable("FORCE_MATCH_RESET_ON_EPISODE_BEGIN") ?? "0") == "1")
            return FailGlobal("force_match_reset_on_episode_begin_forbidden");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_TRACE_PATH")))
            return FailGlobal("trace_path_missing");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_AUDIT_PATH")))
            return FailGlobal("audit_path_missing");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_RUN_ID")))
            return FailGlobal("run_id_missing");
        if (!TryResolvePreset(
            Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_PRESET"),
            out PresetConfig _))
            return FailGlobal("preset_invalid");
        return true;
    }

    public static void Attach(
        int areaId,
        MatchManager match,
        PlayerBody candidate,
        PlayerBody opponent)
    {
        if (!Enabled)
            return;
        if (match == null || candidate == null || opponent == null)
        {
            FailGlobal("attach_references_missing");
            return;
        }
        Phase5AutonomousSession component =
            match.gameObject.AddComponent<Phase5AutonomousSession>();
        component.Initialize(areaId, match, candidate, opponent);
    }

    public static bool ShouldSuppressAgentDrivenReset(MatchManager owner, string method)
    {
        if (!Enabled || owner == null)
            return false;
        foreach (Phase5AutonomousSession session in Instances)
        {
            if (session == null || session.match != owner || !session.initialized)
                continue;
            if (session.harnessApplyingReset)
                return false;
            if (session.finished)
            {
                session.suppressedAgentResets++;
                UnityEngine.Debug.Log(
                    "[Phase5Autonomous] suppressed post-session agent reset method="
                    + method + " area=" + session.areaId);
                return true;
            }
            if (session.pendingAgentResetSuppression)
            {
                session.pendingAgentResetSuppression = false;
                session.suppressedAgentResets++;
                UnityEngine.Debug.Log(
                    "[Phase5Autonomous] suppressed duplicate agent reset method="
                    + method + " area=" + session.areaId);
                return true;
            }
            session.resetCorruptions++;
            UnityEngine.Debug.LogError(
                "[Phase5Autonomous] unexpected agent-driven reset method="
                + method + " area=" + session.areaId);
            return true;
        }
        return false;
    }

    // MatchManager calls this before publishing OnKill so the autonomous
    // boundary is armed before GambitAgentController ends its episode.
    public static void PrepareKillBoundary(
        MatchManager owner,
        PlayerIdentity killer,
        PlayerIdentity victim)
    {
        if (!Enabled || owner == null)
            return;
        foreach (Phase5AutonomousSession session in Instances)
        {
            if (session == null || session.match != owner || !session.initialized)
                continue;
            session.OnKill(killer, victim);
            return;
        }
    }

    private void Initialize(
        int configuredAreaId,
        MatchManager configuredMatch,
        PlayerBody configuredCandidate,
        PlayerBody configuredOpponent)
    {
        areaId = configuredAreaId;
        match = configuredMatch;
        candidate = configuredCandidate;
        opponent = configuredOpponent;
        candidateAgent = candidate.GetComponent<GambitAgentController>();
        opponentController = opponent.GetComponent<ScriptedBotController>();
        runId = Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_RUN_ID") ?? "";
        targetSessions = ParseInt(
            Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_TARGET_SESSIONS_PER_AREA"),
            2,
            1,
            10000);
        if (!TryResolvePreset(
            Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_PRESET"),
            out preset))
        {
            FailInstance("preset_invalid");
            return;
        }
        if (candidateAgent == null || opponentController == null)
        {
            FailInstance("controller_contract_invalid");
            return;
        }
        if (candidate.GetComponent<HumanController>() != null
            || opponent.GetComponent<HumanController>() != null
            || UnityEngine.Object.FindObjectsOfType<HumanController>(true).Length != 0)
        {
            FailInstance("manual_input_controller_present");
            return;
        }
        if (!Phase5ProceduralArenaRuntime.TryGetAreaDescriptor(
            areaId,
            out layoutSeed,
            out layoutSplit,
            out layoutFamily,
            out canonicalSpawnA,
            out canonicalSpawnB,
            out requestedInitialLos,
            out optionalGeodesicRouteLength))
        {
            if (Phase5GenericPrivilegedTeacher.Enabled
                && DemoMapRuntime.ControlEnabled
                && DemoMapRuntime.IsValid)
            {
                layoutSeed = DemoMapRuntime.ActiveSeed;
                layoutSplit = "authored";
                layoutFamily = "authored_demo";
                canonicalSpawnA = match.SpawnPointA != null
                    ? match.SpawnPointA.GetSpawnPosition()
                    : candidate.transform.position;
                canonicalSpawnB = match.SpawnPointB != null
                    ? match.SpawnPointB.GetSpawnPosition()
                    : opponent.transform.position;
                requestedInitialLos = HasLineOfSight();
                optionalGeodesicRouteLength = 0f;
            }
            else
            {
                FailInstance("layout_descriptor_missing");
                return;
            }
        }

        telemetry = candidate.GetComponent<Phase5MapIndependentTelemetry>();
        if (telemetry == null)
            telemetry = candidate.gameObject.AddComponent<Phase5MapIndependentTelemetry>();
        telemetry.Initialize(candidate);

        match.OnKill += OnKill;
        match.OnHit += OnHit;
        match.OnRoundReset += OnRoundReset;
        match.OnMatchReset += OnRoundReset;
        Instances.Add(this);
        initialized = true;
        pendingAgentResetSuppression = true;
        BeginSession(true);
        UnityEngine.Debug.Log(
            "[Phase5Autonomous] initialized area=" + areaId
            + " preset=" + preset.name
            + " seed=" + layoutSeed
            + " target_sessions=" + targetSessions);
    }

    private void OnDestroy()
    {
        if (match != null)
        {
            match.OnKill -= OnKill;
            match.OnHit -= OnHit;
            match.OnRoundReset -= OnRoundReset;
            match.OnMatchReset -= OnRoundReset;
        }
        Instances.Remove(this);
    }

    private void FixedUpdate()
    {
        if (!initialized || finished)
            return;
        if (pendingBoundary)
        {
            AdvanceBoundary();
            if (finished)
                return;
        }

        SampleTelemetry();
        if (Time.time - challengeStartedAt >= preset.timeoutSeconds)
            TriggerTimeout();
    }

    private void BeginSession(bool initial)
    {
        if (!initial)
            TeleportCanonicalSpawns();
        else
            ApplyHarnessRoundReset();

        currentChallenge = 0;
        completedChallenges = 0;
        challengerIds.Clear();
        searchEvents.Clear();
        lastSeenReachedEvent = false;
        localSearchStartedEvent = false;
        searchGoalStaleEvent = false;
        wasActorStuck = false;
        losLostEvents = 0;
        lastSeenReachedEvents = 0;
        localSearchStartedEvents = 0;
        reacquiredEvents = 0;
        searchGoalStaleEvents = 0;
        unstuckTriggeredEvents = 0;
        sessionStartedAt = Time.time;
        challengeStartedAt = Time.time;
        firstContactSeconds = -1f;
        pathDistance = 0f;
        pathAtFirstContact = 0f;
        stuckSeconds = 0f;
        sideCollisionCount = 0;
        losAcquisitions = 0;
        losReacquisitions = 0;
        hadLos = false;
        lostLosAfterAcquisition = false;
        kills = 0;
        deaths = 0;
        timeouts = 0;
        fixedSamples = 0;
        saturatedActionDimensions = 0;
        actionDimensionsObserved = 0;
        tacticalModeSeconds = new float[4];
        initialEuclideanDistance =
            Vector3.Distance(candidate.transform.position, opponent.transform.position);
        previousPosition = candidate.transform.position;
        previousSideCollisionSteps = candidate.Motor != null
            ? candidate.Motor.TotalSideCollisionSteps
            : 0;
        candidateDamageAtStart = candidate.Weapon != null
            ? candidate.Weapon.TotalDamageApplied
            : 0f;
        opponentDamageAtStart = opponent.Weapon != null
            ? opponent.Weapon.TotalDamageApplied
            : 0f;
        ApplyChallenger();
        ResetAllTransientState("session_begin");
        ValidatePresetLos();
    }

    private void ApplyChallenger()
    {
        OpponentSpec spec = ResolveOpponent(preset, sessionOrdinal, currentChallenge);
        if (preset.name == "reacquire_search")
            spec = FrozenRoster[7 + PositiveModulo(areaId + sessionOrdinal, 7)];
        challengerIds.Add(spec.id);
        opponentController.BotMode = spec.mode;
        if (opponent.Weapon != null)
            opponent.Weapon.ExternalFireSuppressed = !spec.armed;
        if (candidate.Weapon != null)
            candidate.Weapon.ExternalFireSuppressed = preset.name == "reacquire_search";
        challengeStartedAt = Time.time;
        UnityEngine.Debug.Log(
            "[Phase5Autonomous] challenger area=" + areaId
            + " session=" + sessionOrdinal
            + " index=" + currentChallenge
            + " id=" + spec.id
            + " armed=" + (spec.armed ? "1" : "0"));
    }

    private void RecordSearchEvent(string eventName)
    {
        searchEvents.Add(new SearchEventRecord
        {
            event_name = eventName,
            fixed_step = fixedSamples,
            seconds_since_session_start = Mathf.Max(0f, Time.time - sessionStartedAt)
        });
    }

    private void TriggerReacquired()
    {
        if (pendingBoundary)
            return;
        pendingBoundary = true;
        pendingBoundaryReason = "reacquired";
        pendingAgentResetSuppression = true;
        gruResetRequests++;
        ForceCandidateCommandNoOp();
        ApplyHarnessRoundReset();
        candidateAgent.EndEpisode();
    }

    private void TriggerTimeout()
    {
        if (pendingBoundary)
            return;
        timeouts++;
        pendingBoundary = true;
        pendingBoundaryReason = "timeout";
        pendingAgentResetSuppression = true;
        gruResetRequests++;
        ForceCandidateCommandNoOp();
        ApplyHarnessRoundReset();
        candidateAgent.EndEpisode();
    }

    private void OnKill(PlayerIdentity killer, PlayerIdentity victim)
    {
        if (!initialized || finished || pendingBoundary)
            return;
        if (killer == candidate.Identity)
            kills++;
        if (victim == candidate.Identity)
            deaths++;
        pendingBoundary = true;
        pendingBoundaryReason = killer == candidate.Identity ? "kill" : "death";
        pendingAgentResetSuppression = true;
        gruResetRequests++;
        ForceCandidateCommandNoOp();
    }

    private void OnHit(PlayerIdentity shooter, PlayerIdentity victim)
    {
        if (!initialized || finished)
            return;
        if (firstContactSeconds < 0f
            && (shooter == candidate.Identity || victim == candidate.Identity))
        {
            firstContactSeconds = Mathf.Max(0f, Time.time - sessionStartedAt);
            pathAtFirstContact = pathDistance;
        }
    }

    private void OnRoundReset()
    {
        if (!initialized)
            return;
        ResetAllTransientState("match_reset");
    }

    private void AdvanceBoundary()
    {
        pendingBoundary = false;
        completedChallenges++;
        currentChallenge++;
        if (completedChallenges >= preset.challengerCount)
        {
            CompleteSession(pendingBoundaryReason);
            if (completedSessions >= targetSessions)
            {
                finished = true;
                ForceCandidateCommandNoOp();
                if (opponentController != null)
                    opponentController.BotMode = ScriptedBotController.ScriptedBotMode.Idle;
                return;
            }
            sessionOrdinal++;
            BeginSession(false);
            return;
        }
        ApplyChallenger();
        ResetAllTransientState("challenger_change");
    }

    private void CompleteSession(string terminalReason)
    {
        completedSessions++;
        sessionClearCount++;
        float damageInflicted = candidate.Weapon != null
            ? Mathf.Max(0f, candidate.Weapon.TotalDamageApplied - candidateDamageAtStart)
            : 0f;
        float damageReceived = opponent.Weapon != null
            ? Mathf.Max(0f, opponent.Weapon.TotalDamageApplied - opponentDamageAtStart)
            : 0f;
        float euclideanEfficiency =
            pathAtFirstContact > 1e-5f && firstContactSeconds >= 0f
            ? Mathf.Clamp01(initialEuclideanDistance / pathAtFirstContact)
            : 0f;
        float geodesicEfficiency =
            optionalGeodesicRouteLength > 0f
            && pathAtFirstContact > 1e-5f
            && firstContactSeconds >= 0f
            ? Mathf.Clamp01(optionalGeodesicRouteLength / pathAtFirstContact)
            : -1f;
        bool finite = IsFinite(firstContactSeconds)
            && IsFinite(pathDistance)
            && IsFinite(stuckSeconds)
            && IsFinite(damageInflicted)
            && IsFinite(damageReceived);
        if (!finite || fixedSamples <= 0)
            missingTelemetry++;

        SessionRow row = new SessionRow
        {
            schema_version = SchemaVersion,
            run_id = runId,
            process_id = ProcessId,
            process_start_token = ProcessStartToken,
            area_id = areaId,
            session_id = runId + ":area:" + areaId + ":session:" + sessionOrdinal,
            session_ordinal = sessionOrdinal,
            deterministic_seed = StableSeed(layoutSeed, areaId, sessionOrdinal),
            preset = preset.name,
            layout_seed = layoutSeed,
            layout_split = layoutSplit,
            layout_family = layoutFamily,
            spawn_a = ToArray(canonicalSpawnA),
            spawn_b = ToArray(canonicalSpawnB),
            requested_initial_los = requestedInitialLos,
            challenger_ids = challengerIds.ToArray(),
            challengers_expected = preset.challengerCount,
            challengers_completed = completedChallenges,
            terminal_reason = terminalReason,
            session_duration_seconds = Mathf.Max(0f, Time.time - sessionStartedAt),
            time_to_first_contact_seconds = firstContactSeconds,
            initial_euclidean_distance_m = initialEuclideanDistance,
            candidate_path_distance_m = pathDistance,
            path_at_first_contact_m = pathAtFirstContact,
            euclidean_path_efficiency = euclideanEfficiency,
            optional_geodesic_route_length_m = optionalGeodesicRouteLength,
            optional_geodesic_path_efficiency = geodesicEfficiency,
            stuck_seconds = stuckSeconds,
            side_collision_count = sideCollisionCount,
            los_acquisitions = losAcquisitions,
            los_reacquisitions = losReacquisitions,
            search_events = searchEvents.ToArray(),
            los_lost_events = losLostEvents,
            last_seen_reached_events = lastSeenReachedEvents,
            local_search_started_events = localSearchStartedEvents,
            reacquired_events = reacquiredEvents,
            search_goal_stale_events = searchGoalStaleEvents,
            unstuck_triggered_events = unstuckTriggeredEvents,
            stale_location_loop = searchGoalStaleEvent && localSearchStartedEvent && terminalReason == "timeout",
            damage_inflicted = damageInflicted,
            damage_received = damageReceived,
            kills = kills,
            deaths = deaths,
            timeouts = timeouts,
            tactical_mode_seconds = tacticalModeSeconds,
            action_saturation_rate = actionDimensionsObserved > 0
                ? (float)saturatedActionDimensions / actionDimensionsObserved
                : 0f,
            fixed_samples = fixedSamples,
            reset_corruptions = resetCorruptions,
            missing_telemetry = missingTelemetry,
            memory_reset_count = memoryResetCount,
            gru_reset_requests = gruResetRequests,
            action_history_reset_count = actionHistoryResetCount,
            tactical_mode_reset_count = tacticalModeResetCount,
            peek_history_reset_count = peekHistoryResetCount,
            suppressed_duplicate_agent_resets = suppressedAgentResets,
            manual_input_events = 0,
            no_manual_input = UnityEngine.Object.FindObjectsOfType<HumanController>(true).Length == 0,
            session_clear_count = sessionClearCount,
            process_restart_count = 0,
            status = finite
                && fixedSamples > 0
                && resetCorruptions == 0
                && missingTelemetry == 0
                ? "PASS"
                : "FAIL",
        };
        AppendJsonLine(
            Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_TRACE_PATH"),
            JsonUtility.ToJson(row));
    }

    private void SampleTelemetry()
    {
        fixedSamples++;
        Vector3 position = candidate.transform.position;
        float displacement = Vector3.Distance(previousPosition, position);
        if (IsFinite(displacement))
            pathDistance += displacement;
        else
            missingTelemetry++;
        previousPosition = position;

        PlayerCommand command = candidate.Controller != null
            ? candidate.Controller.GetCommand()
            : PlayerCommand.NoOp;
        bool movementRequested =
            Mathf.Abs(command.MoveX) + Mathf.Abs(command.MoveZ) > 0.2f;
        if (movementRequested && displacement < 0.02f)
            stuckSeconds += Time.fixedDeltaTime;
        CountSaturation(command.MoveX);
        CountSaturation(command.MoveZ);
        CountSaturation(command.Turn);
        CountSaturation(command.LookPitch);

        if (candidate.Motor != null)
        {
            int currentSide = candidate.Motor.TotalSideCollisionSteps;
            if (currentSide > previousSideCollisionSteps)
                sideCollisionCount += currentSide - previousSideCollisionSteps;
            previousSideCollisionSteps = currentSide;
        }

        bool reacquiredThisStep = false;
        bool los = HasLineOfSight();
        if (los && !hadLos)
        {
            losAcquisitions++;
            if (lostLosAfterAcquisition)
            {
                losReacquisitions++;
                if (preset.name == "reacquire_search")
                {
                    reacquiredEvents++;
                    RecordSearchEvent("REACQUIRED");
                    reacquiredThisStep = true;
                }
            }
            hadLos = true;
            if (firstContactSeconds < 0f)
            {
                firstContactSeconds = Mathf.Max(0f, Time.time - sessionStartedAt);
                pathAtFirstContact = pathDistance;
            }
        }
        else if (!los && hadLos)
        {
            hadLos = false;
            lostLosAfterAcquisition = true;
            if (preset.name == "reacquire_search")
            {
                losLostEvents++;
                RecordSearchEvent("LOS_LOST");
                if (PositiveModulo(areaId + sessionOrdinal, 7) == 4 && telemetry != null)
                    telemetry.InjectFalseHeardCue(sessionOrdinal % 2 == 0 ? 120f : -135f);
            }
        }

        try
        {
            telemetry.BuildObservation(actorObservation, true);
            int mode = 0;
            float best = actorObservation[Phase5ActorObservationLayout.TacticalMode];
            for (int index = 1; index < 4; index++)
            {
                float value = actorObservation[Phase5ActorObservationLayout.TacticalMode + index];
                if (value > best)
                {
                    best = value;
                    mode = index;
                }
            }
            tacticalModeSeconds[mode] += Time.fixedDeltaTime;

            if (preset.name == "reacquire_search" && !los)
            {
                int memoryAt = Phase5ActorObservationLayout.LastSeenMemory;
                bool memoryValid = actorObservation[memoryAt] > 0.5f;
                float localNorm = Mathf.Sqrt(
                    actorObservation[memoryAt + 1] * actorObservation[memoryAt + 1]
                    + actorObservation[memoryAt + 2] * actorObservation[memoryAt + 2]
                    + actorObservation[memoryAt + 3] * actorObservation[memoryAt + 3]);
                float ageSeconds = actorObservation[memoryAt + 4] * 10f;
                if (memoryValid && !lastSeenReachedEvent && localNorm <= 0.05f)
                {
                    lastSeenReachedEvent = true;
                    lastSeenReachedEvents++;
                    RecordSearchEvent("LAST_SEEN_REACHED");
                }
                if (lastSeenReachedEvent && !localSearchStartedEvent
                    && actorObservation[29] >= 0.1f)
                {
                    localSearchStartedEvent = true;
                    localSearchStartedEvents++;
                    RecordSearchEvent("LOCAL_SEARCH_STARTED");
                }
                if (memoryValid && !searchGoalStaleEvent
                    && ageSeconds >= ParseSearchStaleTimeout())
                {
                    searchGoalStaleEvent = true;
                    searchGoalStaleEvents++;
                    RecordSearchEvent("SEARCH_GOAL_STALE");
                }
                bool actorStuck = actorObservation[26] > 0.6f;
                if (wasActorStuck && actorObservation[26] < 0.25f && displacement >= 0.02f)
                {
                    unstuckTriggeredEvents++;
                    RecordSearchEvent("UNSTUCK_TRIGGERED");
                }
                wasActorStuck = actorStuck;
            }
        }
        catch (Exception ex)
        {
            missingTelemetry++;
            UnityEngine.Debug.LogError("[Phase5Autonomous] telemetry sample failed: " + ex.Message);
        }
        if (reacquiredThisStep)
            TriggerReacquired();
    }

    private void ResetAllTransientState(string reason)
    {
        ForceCandidateCommandNoOp();
        if (telemetry != null)
            telemetry.ResetMemory();
        memoryResetCount++;
        actionHistoryResetCount++;
        tacticalModeResetCount++;
        peekHistoryResetCount++;
        previousPosition = candidate != null ? candidate.transform.position : Vector3.zero;
        previousSideCollisionSteps = candidate != null && candidate.Motor != null
            ? candidate.Motor.TotalSideCollisionSteps
            : 0;
        if (!AuditTelemetryMemoryClear())
        {
            resetCorruptions++;
            UnityEngine.Debug.LogError(
                "[Phase5Autonomous] reset audit failed area=" + areaId + " reason=" + reason);
        }
    }

    private bool AuditTelemetryMemoryClear()
    {
        if (telemetry == null)
            return false;
        return QueueCountIsZero("shortStuck")
            && QueueCountIsZero("longStuck")
            && QueueCountIsZero("collisionHistory")
            && BoolFieldIsFalse("recentDamageValid")
            && BoolFieldIsFalse("lastSeenValid")
            && BoolFieldIsFalse("lastHeardValid")
            && BoolFieldIsFalse("huntValid")
            && BoolFieldIsFalse("huntDropped");
    }

    private bool QueueCountIsZero(string fieldName)
    {
        FieldInfo field = typeof(Phase5MapIndependentTelemetry).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        object value = field != null ? field.GetValue(telemetry) : null;
        System.Collections.ICollection collection =
            value as System.Collections.ICollection;
        return collection != null && collection.Count == 0;
    }

    private bool BoolFieldIsFalse(string fieldName)
    {
        FieldInfo field = typeof(Phase5MapIndependentTelemetry).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        return field != null && field.FieldType == typeof(bool)
            && !(bool)field.GetValue(telemetry);
    }

    private void ForceCandidateCommandNoOp()
    {
        if (candidateAgent == null)
            return;
        FieldInfo field = typeof(GambitAgentController).GetField(
            "currentCommand",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            resetCorruptions++;
            return;
        }
        field.SetValue(candidateAgent, PlayerCommand.NoOp);
    }

    private void ApplyHarnessRoundReset()
    {
        harnessApplyingReset = true;
        try
        {
            match.ResetRound();
        }
        finally
        {
            harnessApplyingReset = false;
        }
    }

    private void TeleportCanonicalSpawns()
    {
        if (candidate != null && candidate.Motor != null && match.SpawnPointA != null)
            candidate.Motor.TeleportTo(
                match.SpawnPointA.GetSpawnPosition(),
                match.SpawnPointA.GetSpawnRotation());
        if (opponent != null && opponent.Motor != null && match.SpawnPointB != null)
            opponent.Motor.TeleportTo(
                match.SpawnPointB.GetSpawnPosition(),
                match.SpawnPointB.GetSpawnRotation());
        Physics.SyncTransforms();
    }

    private void ValidatePresetLos()
    {
        bool los = HasLineOfSight();
        if (preset.requireInitialLos.HasValue
            && los != preset.requireInitialLos.Value)
        {
            FailInstance(
                "preset_initial_los_mismatch:preset=" + preset.name
                + ":requested=" + (preset.requireInitialLos.Value ? "1" : "0")
                + ":actual=" + (los ? "1" : "0"));
        }
    }

    private bool HasLineOfSight()
    {
        if (candidate == null || opponent == null)
            return false;
        Vector3 origin = candidate.transform.position + Vector3.up * 0.7f;
        Vector3 target = opponent.transform.position + Vector3.up * 0.7f;
        Vector3 delta = target - origin;
        if (delta.sqrMagnitude <= 1e-8f)
            return true;
        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            delta.normalized,
            delta.magnitude + 0.5f,
            ~0,
            QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            Transform tr = hit.collider != null ? hit.collider.transform : null;
            if (tr == null || IsInHierarchy(tr, candidate.transform))
                continue;
            return IsInHierarchy(tr, opponent.transform);
        }
        return true;
    }

    private static bool IsInHierarchy(Transform candidate, Transform root)
    {
        return candidate != null && root != null
            && (candidate == root || candidate.IsChildOf(root));
    }

    private void CountSaturation(float value)
    {
        actionDimensionsObserved++;
        if (Mathf.Abs(value) >= 0.95f)
            saturatedActionDimensions++;
    }

    private void FailInstance(string reason)
    {
        resetCorruptions++;
        initialized = false;
        globalFailure = reason;
        UnityEngine.Debug.LogError("[Phase5Autonomous] " + reason + " area=" + areaId);
        if (Application.isBatchMode)
            Application.Quit(87);
    }

    private static bool FailGlobal(string reason)
    {
        globalFailure = reason;
        UnityEngine.Debug.LogError("[Phase5Autonomous] " + reason);
        if (Application.isBatchMode)
            Application.Quit(87);
        return false;
    }

    private void OnApplicationQuit()
    {
        WriteGlobalAudit();
    }

    private static void WriteGlobalAudit()
    {
        if (auditWritten || !Enabled)
            return;
        auditWritten = true;
        List<AreaFinalAudit> areaAudits = new List<AreaFinalAudit>();
        bool pass = string.IsNullOrEmpty(globalFailure) && Instances.Count > 0;
        int completed = 0;
        int corruptions = 0;
        int missing = 0;
        int suppressed = 0;
        foreach (Phase5AutonomousSession session in Instances)
        {
            if (session == null)
                continue;
            bool areaPass = session.initialized
                && session.completedSessions >= session.targetSessions
                && session.resetCorruptions == 0
                && session.missingTelemetry == 0;
            pass &= areaPass;
            completed += session.completedSessions;
            corruptions += session.resetCorruptions;
            missing += session.missingTelemetry;
            suppressed += session.suppressedAgentResets;
            areaAudits.Add(new AreaFinalAudit
            {
                area_id = session.areaId,
                layout_seed = session.layoutSeed,
                layout_family = session.layoutFamily,
                preset = session.preset.name,
                target_sessions = session.targetSessions,
                completed_sessions = session.completedSessions,
                reset_corruptions = session.resetCorruptions,
                missing_telemetry = session.missingTelemetry,
                suppressed_duplicate_agent_resets = session.suppressedAgentResets,
                status = areaPass ? "PASS" : "FAIL",
            });
        }
        AuditDocument audit = new AuditDocument
        {
            schema_version = AuditSchemaVersion,
            status = pass ? "PASS" : "FAIL",
            run_id = Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_RUN_ID") ?? "",
            process_id = ProcessId,
            process_start_token = ProcessStartToken,
            process_restart_count = 0,
            area_count = areaAudits.Count,
            completed_sessions = completed,
            reset_corruptions = corruptions,
            missing_telemetry = missing,
            suppressed_duplicate_agent_resets = suppressed,
            human_controller_count =
                UnityEngine.Object.FindObjectsOfType<HumanController>(true).Length,
            manual_input_events = 0,
            failure_reason = globalFailure,
            areas = areaAudits.ToArray(),
        };
        string path = Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_AUDIT_PATH");
        try
        {
            EnsureParent(path);
            File.WriteAllText(path, JsonUtility.ToJson(audit, true) + "\n");
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError("[Phase5Autonomous] audit write failed: " + ex);
        }
    }

    private static bool TryResolvePreset(string raw, out PresetConfig config)
    {
        string name = (raw ?? "").Trim().ToLowerInvariant();
        switch (name)
        {
            case "hunt_probe":
                config = new PresetConfig(name, 1, ParseTimeout(6f), false);
                return true;
            case "duel_probe":
                config = new PresetConfig(name, 1, ParseTimeout(10f), true);
                return true;
            case "demo_5":
                config = new PresetConfig(name, 5, ParseTimeout(8f), null);
                return true;
            case "endurance_20":
                config = new PresetConfig(name, 20, ParseTimeout(6f), null);
                return true;
            case "ppo_h0":
                config = new PresetConfig(name, 1, ParseTimeout(6f), true);
                return true;
            case "ppo_h1":
            case "ppo_h2":
                config = new PresetConfig(name, 1, ParseTimeout(8f), false);
                return true;
            case "ppo_h3":
                config = new PresetConfig(name, 1, ParseTimeout(10f), null);
                return true;
            case "ppo_h4":
                config = new PresetConfig(name, 1, ParseTimeout(10f), false);
                return true;
            case "ppo_h5_scripted":
                config = new PresetConfig(name, 5, ParseTimeout(10f), null);
                return true;
            case "reacquire_search":
                config = new PresetConfig(name, 1, ParseTimeout(20f), true);
                return true;
            case "peek_p0":
            case "peek_p1":
            case "peek_p2":
            case "peek_p3":
            case "peek_p4":
                config = new PresetConfig(name, 1, ParseTimeout(20f), false);
                return true;
            default:
                config = new PresetConfig();
                return false;
        }
    }

    private static float ParseSearchStaleTimeout()
    {
        string raw = Environment.GetEnvironmentVariable("PHASE5_SEARCH_STALE_TIMEOUT_SECONDS");
        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            return Mathf.Clamp(parsed, 0.5f, 20f);
        return 4f;
    }

    private static float ParseTimeout(float fallback)
    {
        string raw = Environment.GetEnvironmentVariable(
            "PHASE5_AUTONOMOUS_CHALLENGER_TIMEOUT_SECONDS");
        if (float.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float parsed))
            return Mathf.Clamp(parsed, 0.25f, 600f);
        return fallback;
    }

    private static OpponentSpec ResolveOpponent(
        PresetConfig preset,
        int sessionOrdinal,
        int challengeIndex)
    {
        if (preset.name == "hunt_probe")
            return FrozenRoster[0];
        if (preset.name == "duel_probe")
            return FrozenRoster[2];
        if (preset.name == "ppo_h0" || preset.name == "ppo_h1" || preset.name == "ppo_h2")
            return FrozenRoster[0];
        if (preset.name == "ppo_h3")
            return FrozenRoster[5];
        if (preset.name == "ppo_h4")
            return FrozenRoster[6];
        if (preset.name == "ppo_h5_scripted")
            return FrozenRoster[PositiveModulo(sessionOrdinal + challengeIndex, FrozenRoster.Length)];
        if (preset.name == "reacquire_search")
            return FrozenRoster[7 + PositiveModulo(sessionOrdinal, 4)];
        if (preset.name == "peek_p0")
            return FrozenRoster[0];
        if (preset.name == "peek_p1")
            return FrozenRoster[14];
        if (preset.name == "peek_p2")
            return FrozenRoster[15];
        if (preset.name == "peek_p3")
            return FrozenRoster[2];
        if (preset.name == "peek_p4")
            return FrozenRoster[16];
        if (preset.name == "demo_5")
            return FrozenRoster[Mathf.Clamp(challengeIndex, 0, FrozenRoster.Length - 1)];
        int mixed = PositiveModulo(
            sessionOrdinal * 3 + challengeIndex,
            FrozenRoster.Length);
        return FrozenRoster[mixed];
    }

    private static int ParseInt(string raw, int fallback, int minimum, int maximum)
    {
        if (!int.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsed))
            return fallback;
        return Mathf.Clamp(parsed, minimum, maximum);
    }

    private static int PositiveModulo(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static int StableSeed(int layoutSeed, int areaId, int sessionOrdinal)
    {
        unchecked
        {
            int value = layoutSeed;
            value = value * 16777619 + areaId * 73856093;
            value = value * 16777619 + sessionOrdinal * 19349663;
            return value;
        }
    }

    private static float[] ToArray(Vector3 value)
    {
        return new[] { value.x, value.y, value.z };
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static void AppendJsonLine(string path, string line)
    {
        EnsureParent(path);
        File.AppendAllText(path, line + "\n");
    }

    private static void EnsureParent(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("output path missing");
        string parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
    }

    private struct OpponentSpec
    {
        public readonly string id;
        public readonly ScriptedBotController.ScriptedBotMode mode;
        public readonly bool armed;

        public OpponentSpec(
            string id,
            ScriptedBotController.ScriptedBotMode mode,
            bool armed)
        {
            this.id = id;
            this.mode = mode;
            this.armed = armed;
        }
    }

    private struct PresetConfig
    {
        public string name;
        public int challengerCount;
        public float timeoutSeconds;
        public bool? requireInitialLos;

        public PresetConfig(
            string name,
            int challengerCount,
            float timeoutSeconds,
            bool? requireInitialLos)
        {
            this.name = name;
            this.challengerCount = challengerCount;
            this.timeoutSeconds = timeoutSeconds;
            this.requireInitialLos = requireInitialLos;
        }
    }

    [Serializable]
    public sealed class SearchEventRecord
    {
        public string event_name;
        public int fixed_step;
        public float seconds_since_session_start;
    }

    [Serializable]
    public sealed class SessionRow
    {
        public string schema_version;
        public string status;
        public string run_id;
        public int process_id;
        public string process_start_token;
        public int area_id;
        public string session_id;
        public int session_ordinal;
        public int deterministic_seed;
        public string preset;
        public int layout_seed;
        public string layout_split;
        public string layout_family;
        public float[] spawn_a;
        public float[] spawn_b;
        public bool requested_initial_los;
        public string[] challenger_ids;
        public int challengers_expected;
        public int challengers_completed;
        public string terminal_reason;
        public float session_duration_seconds;
        public float time_to_first_contact_seconds;
        public float initial_euclidean_distance_m;
        public float candidate_path_distance_m;
        public float path_at_first_contact_m;
        public float euclidean_path_efficiency;
        public float optional_geodesic_route_length_m;
        public float optional_geodesic_path_efficiency;
        public float stuck_seconds;
        public int side_collision_count;
        public int los_acquisitions;
        public int los_reacquisitions;
        public SearchEventRecord[] search_events;
        public int los_lost_events;
        public int last_seen_reached_events;
        public int local_search_started_events;
        public int reacquired_events;
        public int search_goal_stale_events;
        public int unstuck_triggered_events;
        public bool stale_location_loop;
        public float damage_inflicted;
        public float damage_received;
        public int kills;
        public int deaths;
        public int timeouts;
        public float[] tactical_mode_seconds;
        public float action_saturation_rate;
        public int fixed_samples;
        public int reset_corruptions;
        public int missing_telemetry;
        public int memory_reset_count;
        public int gru_reset_requests;
        public int action_history_reset_count;
        public int tactical_mode_reset_count;
        public int peek_history_reset_count;
        public int suppressed_duplicate_agent_resets;
        public int manual_input_events;
        public bool no_manual_input;
        public int session_clear_count;
        public int process_restart_count;
    }

    [Serializable]
    public sealed class AreaFinalAudit
    {
        public int area_id;
        public int layout_seed;
        public string layout_family;
        public string preset;
        public int target_sessions;
        public int completed_sessions;
        public int reset_corruptions;
        public int missing_telemetry;
        public int suppressed_duplicate_agent_resets;
        public string status;
    }

    [Serializable]
    public sealed class AuditDocument
    {
        public string schema_version;
        public string status;
        public string run_id;
        public int process_id;
        public string process_start_token;
        public int process_restart_count;
        public int area_count;
        public int completed_sessions;
        public int reset_corruptions;
        public int missing_telemetry;
        public int suppressed_duplicate_agent_resets;
        public int human_controller_count;
        public int manual_input_events;
        public string failure_reason;
        public AreaFinalAudit[] areas;
    }
}

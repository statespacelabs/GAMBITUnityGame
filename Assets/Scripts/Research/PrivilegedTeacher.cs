using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// DAgger-only privileged navigation teacher.
///
/// The teacher is not an ML-Agents sensor. Exact target state and temporary
/// NavMesh/A* routes are used only to create labels and select the executed
/// command. The stored student input is phase5_actor_obs_v001, and no world
/// coordinate, map identifier, path node, or waypoint is written to it.
/// </summary>
[UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, null, "Phase5GenericPrivilegedTeacher")]
public sealed class PrivilegedTeacher : MonoBehaviour
{
    public const string LabelSchema = "phase5_teacher_label_v001";
    public const string RolloutSchema = "phase5_dagger_rollout_v001";
    public const string SummarySchema = "phase5_teacher_rollout_summary_v001";

    public enum TeacherMode
    {
        DIRECT_PURSUIT = 0,
        FOLLOW_PATH_CORNER = 1,
        WALL_FOLLOW_LEFT = 2,
        WALL_FOLLOW_RIGHT = 3,
        ESCAPE_STUCK = 4,
        COMBAT_HANDOFF = 5,
        REACQUIRE_LAST_SEEN = 6,
    }

    private static readonly Dictionary<PlayerBody, PrivilegedTeacher> ByBody =
        new Dictionary<PlayerBody, PrivilegedTeacher>();
    private static readonly List<PrivilegedTeacher> Instances =
        new List<PrivilegedTeacher>();
    private static StreamWriter writer;
    private static bool outputInitialized;
    private static bool outputFinalized;
    private static string globalFailure = "";
    private static long globalRows;
    private static long globalTeacherControlledRows;
    private static long globalLosRows;
    private static long globalStuckRows;
    private static long globalWeaponFailures;
    private static long globalOwnerFailures;
    private static long globalObservationFailures;
    private static long globalWriteFailures;
    private static readonly long[] GlobalModeCounts = new long[7];
    private static bool? enabledFromEnvironment;

    private int areaId;
    private MatchManager match;
    private PlayerBody candidate;
    private PlayerBody opponent;
    private MapIndependentTelemetry actorTelemetry;
    private System.Random random;
    private float controlProbability;
    private string roundId = "";
    private string runId = "";
    private int baseSeed;

    private int fixedStep;
    private int episodeId;
    private int routeVariant;
    private int wallSide;
    private int reactionDelaySteps;
    private int handoffDelaySteps;
    private int losTicks;
    private int recoveryTicks;
    private int noProgressTicks;
    private bool previousTeacherControlled;
    private Vector3 previousPosition;
    private bool lastSeenValid;
    private Vector3 lastSeenWorld;
    private float lastSeenTime;
    private float nextReplanTime;
    private string pathSource = "none";
    private readonly List<Vector3> pathCorners = new List<Vector3>();
    private int pathCornerIndex;
    private readonly Queue<PlayerCommand> delayedTeacherCommands =
        new Queue<PlayerCommand>();
    private readonly float[] actorObservation =
        new float[ActorObservationContract.Size];

    public static bool Enabled
    {
        get
        {
            if (!enabledFromEnvironment.HasValue)
                enabledFromEnvironment =
                    (Environment.GetEnvironmentVariable("PHASE5_GENERIC_TEACHER") ?? "").Trim() == "1";
            return enabledFromEnvironment.Value;
        }
    }

    public static bool ValidateGlobalLaunch()
    {
        string raw = (Environment.GetEnvironmentVariable("PHASE5_GENERIC_TEACHER") ?? "").Trim();
        if (string.IsNullOrEmpty(raw) || raw == "0")
            return true;
        if (raw != "1")
            return FailGlobal("enable_flag_invalid:" + raw);
        if (!HeadlessTrainingRuntime.Enabled && !RenderedSmokeRuntime.Enabled)
            return FailGlobal("requires_phase5_headless");
        if (!AutonomousTrainingSession.Enabled)
            return FailGlobal("requires_phase5_autonomous_session");
        if ((Environment.GetEnvironmentVariable("ENABLE_VISUAL_OBS") ?? "0") == "1")
            return FailGlobal("visual_observations_forbidden");
        if ((Environment.GetEnvironmentVariable("PHASE5_DAGGER_STORE_FRAMES") ?? "0") == "1")
            return FailGlobal("frame_storage_forbidden");
        if ((Environment.GetEnvironmentVariable("PHASE5_TELEMETRY_SCHEMA") ?? "")
            != "phase3v2_c_local45")
            return FailGlobal("combat_expert_requires_frozen_local45_transport");
        if ((Environment.GetEnvironmentVariable("PHASE5_TEACHER_LABEL_SCHEMA") ?? "")
            != LabelSchema)
            return FailGlobal("teacher_label_schema_invalid");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHASE5_TEACHER_TRACE_PATH")))
            return FailGlobal("teacher_trace_path_missing");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHASE5_TEACHER_SUMMARY_PATH")))
            return FailGlobal("teacher_summary_path_missing");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHASE5_DAGGER_ROUND")))
            return FailGlobal("dagger_round_missing");
        if (!TryParseProbability(
            Environment.GetEnvironmentVariable("PHASE5_TEACHER_CONTROL_PROBABILITY"),
            out float _))
            return FailGlobal("teacher_control_probability_invalid");
        bool procedural = ProceduralArenaRuntime.Enabled
            && ProceduralArenaRuntime.IsValid;
        bool authored = DemoMapRuntime.ControlEnabled && DemoMapRuntime.IsValid;
        if (!procedural && !authored)
            return FailGlobal("requires_procedural_or_authored_map_runtime");
        InitializeOutput();
        return string.IsNullOrEmpty(globalFailure);
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
        PrivilegedTeacher component =
            match.gameObject.AddComponent<PrivilegedTeacher>();
        component.Initialize(areaId, match, candidate, opponent);
    }

    /// <summary>
    /// Called only from PlayerBody.TickController. The opponent has no registered
    /// teacher and therefore receives its original scripted command unchanged.
    /// </summary>
    public static PlayerCommand ResolveCommand(
        PlayerBody body,
        PlayerCommand policyCommand)
    {
        if (!Enabled || body == null)
            return policyCommand;
        if (!ByBody.TryGetValue(body, out PrivilegedTeacher teacher)
            || teacher == null)
            return policyCommand;
        return teacher.Resolve(policyCommand);
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
        runId = Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_RUN_ID") ?? "";
        roundId = Environment.GetEnvironmentVariable("PHASE5_DAGGER_ROUND") ?? "";
        baseSeed = ParseInt(
            Environment.GetEnvironmentVariable("PHASE5_TEACHER_SEED"),
            56025);
        if (!TryParseProbability(
            Environment.GetEnvironmentVariable("PHASE5_TEACHER_CONTROL_PROBABILITY"),
            out controlProbability))
        {
            FailInstance("control_probability_invalid");
            return;
        }
        actorTelemetry = candidate.GetComponent<MapIndependentTelemetry>();
        if (actorTelemetry == null)
            actorTelemetry = candidate.gameObject.AddComponent<MapIndependentTelemetry>();
        actorTelemetry.Initialize(candidate);
        random = new System.Random(MixSeed(baseSeed, areaId, 0));
        previousPosition = candidate.transform.position;
        match.OnRoundReset += OnBoundary;
        match.OnMatchReset += OnBoundary;
        match.OnKill += OnKill;
        ByBody[candidate] = this;
        Instances.Add(this);
        ResetTeacherState();
        Debug.Log(
            "[PrivilegedTeacher] initialized area=" + areaId
            + " round=" + roundId
            + " control_probability="
            + controlProbability.ToString("F2", CultureInfo.InvariantCulture));
    }

    private void OnDestroy()
    {
        if (match != null)
        {
            match.OnRoundReset -= OnBoundary;
            match.OnMatchReset -= OnBoundary;
            match.OnKill -= OnKill;
        }
        if (candidate != null)
            ByBody.Remove(candidate);
        Instances.Remove(this);
    }

    private void OnApplicationQuit()
    {
        FinalizeOutput();
    }

    private void OnBoundary()
    {
        episodeId++;
        ResetTeacherState();
    }

    private void OnKill(PlayerIdentity killer, PlayerIdentity victim)
    {
        // MatchManager publishes OnRoundReset after deferred continuation. This
        // hook only clears queued commands immediately at the terminal boundary.
        delayedTeacherCommands.Clear();
        previousTeacherControlled = false;
    }

    private void ResetTeacherState()
    {
        random = new System.Random(MixSeed(baseSeed, areaId, episodeId));
        routeVariant = random.Next(0, int.MaxValue);
        wallSide = random.NextDouble() < 0.5 ? -1 : 1;
        reactionDelaySteps = random.Next(0, 5);
        handoffDelaySteps = random.Next(0, 7);
        losTicks = 0;
        recoveryTicks = 0;
        noProgressTicks = 0;
        previousTeacherControlled = false;
        previousPosition = candidate != null ? candidate.transform.position : Vector3.zero;
        lastSeenValid = false;
        lastSeenWorld = Vector3.zero;
        lastSeenTime = -999f;
        nextReplanTime = -999f;
        pathSource = "none";
        pathCorners.Clear();
        pathCornerIndex = 0;
        delayedTeacherCommands.Clear();
    }

    private PlayerCommand Resolve(PlayerCommand policyCommand)
    {
        fixedStep++;
        bool observationFailure = false;
        try
        {
            actorTelemetry.BuildObservation(actorObservation, false);
            AssertFinite(actorObservation);
        }
        catch (Exception ex)
        {
            observationFailure = true;
            globalObservationFailures++;
            globalFailure = "observation_failure:" + ex.Message;
            Debug.LogError("[PrivilegedTeacher] " + globalFailure);
            WriteFailureRow(policyCommand, "OBSERVATION_FAILURE");
            return policyCommand;
        }

        Vector3 position = candidate.transform.position;
        float displacement = Vector3.Distance(previousPosition, position);
        bool priorMovement = previousTeacherControlled
            && candidate.Controller != null
            && (Mathf.Abs(candidate.Controller.GetCommand().MoveX)
                + Mathf.Abs(candidate.Controller.GetCommand().MoveZ) > 0.2f);
        if (priorMovement && displacement < 0.02f)
            noProgressTicks++;
        else
            noProgressTicks = 0;
        previousPosition = position;
        if (noProgressTicks >= 25 && recoveryTicks <= 0)
        {
            recoveryTicks = 25;
            noProgressTicks = 0;
        }

        bool los = ExactLineOfSight(candidate, opponent);
        if (los)
        {
            lastSeenValid = true;
            lastSeenWorld = opponent.transform.position;
            lastSeenTime = Time.time;
            losTicks++;
        }
        else
        {
            losTicks = 0;
        }

        TeacherMode mode;
        Vector3 navigationTarget;
        bool nextCornerValid;
        ResolveNavigation(los, out mode, out navigationTarget, out nextCornerValid);

        PlayerCommand navigationCommand = BuildNavigationCommand(
            mode,
            navigationTarget,
            nextCornerValid);
        delayedTeacherCommands.Enqueue(navigationCommand);
        PlayerCommand delayedNavigation = navigationCommand;
        if (delayedTeacherCommands.Count > reactionDelaySteps)
            delayedNavigation = delayedTeacherCommands.Dequeue();
        while (delayedTeacherCommands.Count > reactionDelaySteps + 1)
            delayedTeacherCommands.Dequeue();

        float blendGate = los
            ? (handoffDelaySteps <= 0
                ? 1f
                : Mathf.Clamp01((float)losTicks / handoffDelaySteps))
            : 0f;
        PlayerCommand teacherAction = los ? policyCommand : delayedNavigation;
        bool teacherControlled = !los && random.NextDouble() < controlProbability;
        PlayerCommand executed = los
            ? policyCommand
            : (teacherControlled ? delayedNavigation : policyCommand);
        previousTeacherControlled = teacherControlled;

        bool ownerFailure = candidate.Weapon == null
            || candidate.Weapon.OwnerIdentity != candidate.Identity;
        bool weaponFailure = candidate.Weapon == null
            || !candidate.Weapon.enabled
            || !candidate.Weapon.gameObject.activeInHierarchy;
        if (ownerFailure) globalOwnerFailures++;
        if (weaponFailure) globalWeaponFailures++;

        bool stuck = recoveryTicks > 0;
        if (stuck) globalStuckRows++;
        globalRows++;
        if (teacherControlled) globalTeacherControlledRows++;
        if (los) globalLosRows++;
        GlobalModeCounts[(int)mode]++;

        Vector3 nextCornerLocal = Vector3.zero;
        if (nextCornerValid)
        {
            Quaternion inverseYaw = Quaternion.Inverse(
                Quaternion.Euler(0f, candidate.transform.eulerAngles.y, 0f));
            Vector3 delta = inverseYaw * (navigationTarget - position);
            nextCornerLocal = delta.sqrMagnitude > 1e-6f
                ? delta.normalized
                : Vector3.zero;
        }

        RolloutRow row = new RolloutRow
        {
            schema_version = RolloutSchema,
            label_schema = LabelSchema,
            student_observation_schema = ActorObservationContract.SchemaId,
            run_id = runId,
            round_id = roundId,
            fixed_step = fixedStep,
            area_id = areaId,
            episode_id = episodeId,
            control_probability = controlProbability,
            teacher_controlled = teacherControlled,
            exact_los = los,
            contact = los,
            teacher_mode = mode.ToString(),
            teacher_tactical_mode = (int)mode,
            actor_observation = Copy(actorObservation),
            policy_action = ToAction(policyCommand),
            teacher_action = ToAction(teacherAction),
            executed_action = ToAction(executed),
            teacher_move = new[] { teacherAction.MoveX, teacherAction.MoveZ },
            navigation_look_bias = new[]
            {
                navigationCommand.Turn,
                navigationCommand.LookPitch,
            },
            combat_navigation_blend_gate = blendGate,
            teacher_next_corner_direction_local =
                new[] { nextCornerLocal.x, nextCornerLocal.y, nextCornerLocal.z },
            teacher_next_corner_valid = nextCornerValid,
            path_source = pathSource,
            path_corner_count = pathCorners.Count,
            reaction_delay_steps = reactionDelaySteps,
            handoff_delay_steps = handoffDelaySteps,
            route_variant = routeVariant,
            wall_follow_side = wallSide,
            stuck = stuck,
            weapon_failure = weaponFailure,
            owner_failure = ownerFailure,
            observation_failure = observationFailure,
        };
        WriteRow(row);
        return executed;
    }

    private void ResolveNavigation(
        bool los,
        out TeacherMode mode,
        out Vector3 navigationTarget,
        out bool nextCornerValid)
    {
        navigationTarget = opponent.transform.position;
        nextCornerValid = false;
        if (los)
        {
            mode = TeacherMode.COMBAT_HANDOFF;
            return;
        }
        if (recoveryTicks > 0)
        {
            recoveryTicks--;
            mode = TeacherMode.ESCAPE_STUCK;
            return;
        }

        bool reacquire = lastSeenValid && Time.time - lastSeenTime <= 5f;
        Vector3 routeGoal = reacquire ? lastSeenWorld : opponent.transform.position;
        if (Time.time >= nextReplanTime || pathCorners.Count == 0)
        {
            nextReplanTime = Time.time + 0.5f
                + (float)random.NextDouble() * 0.35f;
            if (!PrivilegedPathOracle.TryBuild(
                areaId,
                candidate,
                routeGoal,
                routeVariant,
                pathCorners,
                out pathSource))
            {
                pathCorners.Clear();
                pathCornerIndex = 0;
                pathSource = "wall_follow_fallback";
            }
            else
            {
                pathCornerIndex = pathCorners.Count > 1 ? 1 : 0;
            }
        }

        while (pathCornerIndex < pathCorners.Count
            && Vector3.Distance(
                candidate.transform.position,
                pathCorners[pathCornerIndex]) < 1.1f)
            pathCornerIndex++;

        if (pathCornerIndex < pathCorners.Count)
        {
            navigationTarget = pathCorners[pathCornerIndex];
            nextCornerValid = true;
            if (reacquire)
                mode = TeacherMode.REACQUIRE_LAST_SEEN;
            else if (pathCorners.Count <= 2
                || PrivilegedPathOracle.StaticLineClear(
                    candidate,
                    routeGoal))
                mode = TeacherMode.DIRECT_PURSUIT;
            else
                mode = TeacherMode.FOLLOW_PATH_CORNER;
            return;
        }

        mode = wallSide < 0
            ? TeacherMode.WALL_FOLLOW_LEFT
            : TeacherMode.WALL_FOLLOW_RIGHT;
    }

    private PlayerCommand BuildNavigationCommand(
        TeacherMode mode,
        Vector3 target,
        bool targetValid)
    {
        if (mode == TeacherMode.COMBAT_HANDOFF)
            return PlayerCommand.NoOp;
        if (mode == TeacherMode.ESCAPE_STUCK)
        {
            PlayerCommand escape = PlayerCommand.NoOp;
            escape.MoveZ = -0.75f;
            escape.MoveX = wallSide * 0.85f;
            escape.Turn = -wallSide * 0.8f;
            escape.Jump = actorObservation[158 + 2] > 0.5f;
            return escape;
        }
        if (mode == TeacherMode.WALL_FOLLOW_LEFT
            || mode == TeacherMode.WALL_FOLLOW_RIGHT
            || !targetValid)
            return BuildWallFollowCommand();

        Quaternion inverseYaw = Quaternion.Inverse(
            Quaternion.Euler(0f, candidate.transform.eulerAngles.y, 0f));
        Vector3 local = inverseYaw * (target - candidate.transform.position);
        float yawError = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        PlayerCommand command = PlayerCommand.NoOp;
        command.Turn = Mathf.Clamp(yawError / 55f, -1f, 1f);
        command.MoveZ = Mathf.Abs(yawError) < 110f
            ? Mathf.Lerp(0.35f, 1f, 1f - Mathf.Clamp01(Mathf.Abs(yawError) / 110f))
            : 0.1f;
        command.MoveX = Mathf.Clamp(
            local.x / Mathf.Max(2f, Mathf.Abs(local.z) + 1f),
            -0.8f,
            0.8f);
        bool forwardFloor = actorObservation[158 + 1] > 0.5f;
        if (!forwardFloor)
        {
            command.MoveZ = 0f;
            command.MoveX = wallSide * 0.8f;
            command.Turn = wallSide * 0.8f;
        }
        return command;
    }

    private PlayerCommand BuildWallFollowCommand()
    {
        int bestRay = 0;
        float bestScore = float.NegativeInfinity;
        for (int ray = 0; ray < 32; ray++)
        {
            float distance = actorObservation[
                ActorObservationContract.TorsoRays + ray * 2];
            float signedAngle = ray <= 16 ? ray * 11.25f : (ray - 32) * 11.25f;
            float sidePreference = wallSide * signedAngle >= -1f ? 0.12f : -0.08f;
            float forwardPreference = 0.10f
                * Mathf.Cos(signedAngle * Mathf.Deg2Rad);
            float score = distance + sidePreference + forwardPreference;
            if (score > bestScore)
            {
                bestScore = score;
                bestRay = ray;
            }
        }
        float angle = bestRay <= 16
            ? bestRay * 11.25f
            : (bestRay - 32) * 11.25f;
        PlayerCommand command = PlayerCommand.NoOp;
        command.Turn = Mathf.Clamp(angle / 75f, -1f, 1f);
        command.MoveZ = Mathf.Clamp01(bestScore);
        command.MoveX = Mathf.Clamp(angle / 120f, -0.7f, 0.7f);
        return command;
    }

    private void WriteFailureRow(PlayerCommand policyCommand, string mode)
    {
        globalRows++;
        RolloutRow row = new RolloutRow
        {
            schema_version = RolloutSchema,
            label_schema = LabelSchema,
            student_observation_schema = ActorObservationContract.SchemaId,
            run_id = runId,
            round_id = roundId,
            fixed_step = fixedStep,
            area_id = areaId,
            episode_id = episodeId,
            control_probability = controlProbability,
            teacher_mode = mode,
            actor_observation = Copy(actorObservation),
            policy_action = ToAction(policyCommand),
            teacher_action = ToAction(policyCommand),
            executed_action = ToAction(policyCommand),
            observation_failure = true,
        };
        WriteRow(row);
    }

    private static void InitializeOutput()
    {
        if (outputInitialized)
            return;
        outputInitialized = true;
        string path = Environment.GetEnvironmentVariable("PHASE5_TEACHER_TRACE_PATH");
        try
        {
            EnsureParent(path);
            writer = new StreamWriter(path, false);
        }
        catch (Exception ex)
        {
            FailGlobal("trace_open_failed:" + ex.Message);
        }
    }

    private static void WriteRow(RolloutRow row)
    {
        try
        {
            if (!outputInitialized)
                InitializeOutput();
            if (writer == null)
                throw new InvalidOperationException("teacher trace writer unavailable");
            writer.WriteLine(JsonUtility.ToJson(row));
            if (globalRows % 256 == 0)
                writer.Flush();
        }
        catch (Exception ex)
        {
            globalWriteFailures++;
            globalFailure = "trace_write_failed:" + ex.Message;
            Debug.LogError("[PrivilegedTeacher] " + globalFailure);
        }
    }

    private static void FinalizeOutput()
    {
        if (outputFinalized || !Enabled)
            return;
        outputFinalized = true;
        try
        {
            if (writer != null)
            {
                writer.Flush();
                writer.Dispose();
                writer = null;
            }
            string[] modeNames = Enum.GetNames(typeof(TeacherMode));
            ModeCount[] modes = new ModeCount[modeNames.Length];
            for (int i = 0; i < modeNames.Length; i++)
                modes[i] = new ModeCount
                {
                    mode = modeNames[i],
                    count = GlobalModeCounts[i],
                };
            bool pass = string.IsNullOrEmpty(globalFailure)
                && Instances.Count > 0
                && globalRows > 0
                && globalWeaponFailures == 0
                && globalOwnerFailures == 0
                && globalObservationFailures == 0
                && globalWriteFailures == 0;
            RolloutSummary summary = new RolloutSummary
            {
                schema_version = SummarySchema,
                status = pass ? "PASS" : "FAIL",
                run_id = Environment.GetEnvironmentVariable("PHASE5_AUTONOMOUS_RUN_ID") ?? "",
                round_id = Environment.GetEnvironmentVariable("PHASE5_DAGGER_ROUND") ?? "",
                row_count = globalRows,
                teacher_controlled_rows = globalTeacherControlledRows,
                los_rows = globalLosRows,
                stuck_rows = globalStuckRows,
                stuck_rate = globalRows > 0
                    ? (float)globalStuckRows / globalRows
                    : 1f,
                weapon_failures = globalWeaponFailures,
                owner_failures = globalOwnerFailures,
                observation_failures = globalObservationFailures,
                write_failures = globalWriteFailures,
                mode_counts = modes,
                frame_count = 0,
                contains_world_coordinates = false,
                contains_map_identifiers = false,
                contains_path_nodes_or_waypoints = false,
                failure_reason = globalFailure,
            };
            string path = Environment.GetEnvironmentVariable("PHASE5_TEACHER_SUMMARY_PATH");
            EnsureParent(path);
            File.WriteAllText(path, JsonUtility.ToJson(summary, true) + "\n");
            Debug.Log("[PrivilegedTeacherSummary] " + JsonUtility.ToJson(summary));
        }
        catch (Exception ex)
        {
            Debug.LogError("[PrivilegedTeacher] summary write failed: " + ex);
        }
    }

    private void FailInstance(string reason)
    {
        globalFailure = reason;
        Debug.LogError("[PrivilegedTeacher] " + reason + " area=" + areaId);
        if (Application.isBatchMode)
            Application.Quit(88);
    }

    private static bool FailGlobal(string reason)
    {
        globalFailure = reason;
        Debug.LogError("[PrivilegedTeacher] " + reason);
        if (Application.isBatchMode)
            Application.Quit(88);
        return false;
    }

    private static bool TryParseProbability(string raw, out float probability)
    {
        probability = 0f;
        return float.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out probability)
            && probability >= 0f
            && probability <= 1f;
    }

    private static int ParseInt(string raw, int fallback)
    {
        return int.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : fallback;
    }

    private static int MixSeed(int seed, int area, int episode)
    {
        unchecked
        {
            int value = seed * 16777619;
            value ^= area * 73856093;
            value ^= episode * 19349663;
            return value;
        }
    }

    private static bool ExactLineOfSight(PlayerBody self, PlayerBody enemy)
    {
        Vector3 origin = self.transform.position + Vector3.up * 0.7f;
        Vector3 target = enemy.transform.position + Vector3.up * 0.7f;
        Vector3 delta = target - origin;
        if (delta.sqrMagnitude <= 1e-6f)
            return true;
        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            delta.normalized,
            delta.magnitude + 0.05f,
            ~0,
            QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (RaycastHit hit in hits)
        {
            PlayerBody body = hit.collider != null
                ? hit.collider.GetComponentInParent<PlayerBody>()
                : null;
            if (body == self)
                continue;
            return body == enemy;
        }
        return false;
    }

    private static float[] ToAction(PlayerCommand command)
    {
        return new[]
        {
            Mathf.Clamp(command.MoveX, -1f, 1f),
            Mathf.Clamp(command.MoveZ, -1f, 1f),
            Mathf.Clamp(command.Turn, -1f, 1f),
            Mathf.Clamp(command.LookPitch, -1f, 1f),
            command.Shoot ? 1f : 0f,
            command.Reload ? 1f : 0f,
            command.Jump ? 1f : 0f,
            command.Crouch ? 1f : 0f,
        };
    }

    private static float[] Copy(float[] source)
    {
        float[] result = new float[source.Length];
        Array.Copy(source, result, source.Length);
        return result;
    }

    private static void AssertFinite(float[] values)
    {
        for (int i = 0; i < values.Length; i++)
            if (float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                throw new InvalidOperationException("non_finite_actor_observation:" + i);
    }

    private static void EnsureParent(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("output path missing");
        string parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
    }

    [Serializable]
    public sealed class RolloutRow
    {
        public string schema_version;
        public string label_schema;
        public string student_observation_schema;
        public string run_id;
        public string round_id;
        public int fixed_step;
        public int area_id;
        public int episode_id;
        public float control_probability;
        public bool teacher_controlled;
        public bool exact_los;
        public bool contact;
        public string teacher_mode;
        public int teacher_tactical_mode;
        public float[] actor_observation;
        public float[] policy_action;
        public float[] teacher_action;
        public float[] executed_action;
        public float[] teacher_move;
        public float[] navigation_look_bias;
        public float combat_navigation_blend_gate;
        public float[] teacher_next_corner_direction_local;
        public bool teacher_next_corner_valid;
        public string path_source;
        public int path_corner_count;
        public int reaction_delay_steps;
        public int handoff_delay_steps;
        public int route_variant;
        public int wall_follow_side;
        public bool stuck;
        public bool weapon_failure;
        public bool owner_failure;
        public bool observation_failure;
    }

    [Serializable]
    public sealed class ModeCount
    {
        public string mode;
        public long count;
    }

    [Serializable]
    public sealed class RolloutSummary
    {
        public string schema_version;
        public string status;
        public string run_id;
        public string round_id;
        public long row_count;
        public long teacher_controlled_rows;
        public long los_rows;
        public long stuck_rows;
        public float stuck_rate;
        public long weapon_failures;
        public long owner_failures;
        public long observation_failures;
        public long write_failures;
        public ModeCount[] mode_counts;
        public int frame_count;
        public bool contains_world_coordinates;
        public bool contains_map_identifiers;
        public bool contains_path_nodes_or_waypoints;
        public string failure_reason;
    }

    private static class PrivilegedPathOracle
    {
        private const float AgentRadius = 0.65f;

        public static bool TryBuild(
            int areaId,
            PlayerBody self,
            Vector3 goal,
            int variant,
            List<Vector3> output,
            out string source)
        {
            output.Clear();
            source = "none";
            if (TryNavMesh(self.transform.position, goal, output))
            {
                source = "runtime_navmesh";
                return true;
            }
            if (ProceduralArenaRuntime.TryGetPrivilegedTeacherGeometry(
                areaId,
                out Bounds floor,
                out Bounds[] obstacles))
            {
                if (TryBoundsAStar(
                    self.transform.position,
                    goal,
                    floor,
                    obstacles,
                    0.75f,
                    variant,
                    false,
                    output))
                {
                    source = "procedural_astar";
                    return true;
                }
            }
            if (DemoMapRuntime.ControlEnabled && DemoMapRuntime.IsValid)
            {
                if (TryBoundsAStar(
                    self.transform.position,
                    goal,
                    DemoMapRuntime.ActiveBounds,
                    null,
                    2.0f,
                    variant,
                    true,
                    output))
                {
                    source = "authored_physics_astar";
                    return true;
                }
            }
            output.Add(self.transform.position);
            output.Add(goal);
            source = "direct_fallback";
            return true;
        }

        public static bool StaticLineClear(PlayerBody self, Vector3 target)
        {
            Vector3 origin = self.transform.position + Vector3.up * 0.7f;
            Vector3 end = target + Vector3.up * 0.7f;
            Vector3 delta = end - origin;
            if (delta.sqrMagnitude <= 1e-6f)
                return true;
            RaycastHit[] hits = Physics.RaycastAll(
                origin,
                delta.normalized,
                delta.magnitude,
                ~0,
                QueryTriggerInteraction.Ignore);
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null)
                    continue;
                PlayerBody body = hit.collider.GetComponentInParent<PlayerBody>();
                if (body == self)
                    continue;
                if (body != null)
                    return true;
                return false;
            }
            return true;
        }

        private static bool TryNavMesh(
            Vector3 start,
            Vector3 goal,
            List<Vector3> output)
        {
            if (Environment.GetEnvironmentVariable("PHASE5_ENABLE_NAVMESH_ORACLE") != "1")
                return false;
            NavMeshPath path = new NavMeshPath();
            if (!NavMesh.CalculatePath(
                start,
                goal,
                NavMesh.AllAreas,
                path)
                || path.status != NavMeshPathStatus.PathComplete
                || path.corners == null
                || path.corners.Length < 2)
                return false;
            output.AddRange(path.corners);
            return true;
        }

        private static bool TryBoundsAStar(
            Vector3 start,
            Vector3 goal,
            Bounds worldBounds,
            Bounds[] obstacles,
            float resolution,
            int variant,
            bool projectAuthoredSurface,
            List<Vector3> output)
        {
            float margin = projectAuthoredSurface ? 10f : 0f;
            float minX = projectAuthoredSurface
                ? Mathf.Max(worldBounds.min.x, Mathf.Min(start.x, goal.x) - margin)
                : worldBounds.min.x + AgentRadius;
            float maxX = projectAuthoredSurface
                ? Mathf.Min(worldBounds.max.x, Mathf.Max(start.x, goal.x) + margin)
                : worldBounds.max.x - AgentRadius;
            float minZ = projectAuthoredSurface
                ? Mathf.Max(worldBounds.min.z, Mathf.Min(start.z, goal.z) - margin)
                : worldBounds.min.z + AgentRadius;
            float maxZ = projectAuthoredSurface
                ? Mathf.Min(worldBounds.max.z, Mathf.Max(start.z, goal.z) + margin)
                : worldBounds.max.z - AgentRadius;
            int nx = Mathf.Clamp(Mathf.FloorToInt((maxX - minX) / resolution) + 1, 2, 128);
            int nz = Mathf.Clamp(Mathf.FloorToInt((maxZ - minZ) / resolution) + 1, 2, 128);
            int count = nx * nz;
            bool[] free = new bool[count];
            Vector3[] points = new Vector3[count];
            for (int x = 0; x < nx; x++)
            {
                for (int z = 0; z < nz; z++)
                {
                    int index = x * nz + z;
                    Vector3 point = new Vector3(
                        minX + x * resolution,
                        start.y,
                        minZ + z * resolution);
                    if (projectAuthoredSurface)
                    {
                        Vector3 request = new Vector3(
                            point.x,
                            worldBounds.max.y + 1f,
                            point.z);
                        free[index] = DemoMapRuntime.TryProjectToSurface(
                            request,
                            out point,
                            out string _);
                    }
                    else
                    {
                        free[index] = !InsideObstacle(point, obstacles);
                    }
                    points[index] = point;
                }
            }
            int startIndex = FindNearestFree(points, free, start);
            int goalIndex = FindNearestFree(points, free, goal);
            if (startIndex < 0 || goalIndex < 0)
                return false;

            float[] g = new float[count];
            float[] bestF = new float[count];
            int[] came = new int[count];
            bool[] closed = new bool[count];
            for (int i = 0; i < count; i++)
            {
                g[i] = float.PositiveInfinity;
                bestF[i] = float.PositiveInfinity;
                came[i] = -1;
            }
            MinHeap heap = new MinHeap();
            g[startIndex] = 0f;
            float firstF = Heuristic(startIndex, goalIndex, nz);
            bestF[startIndex] = firstF;
            heap.Push(new HeapItem(startIndex, firstF));
            int[] dx = { -1, 0, 1, 0 };
            int[] dz = { 0, 1, 0, -1 };
            int rotation = Mathf.Abs(variant % 4);
            while (heap.Count > 0)
            {
                HeapItem item = heap.Pop();
                int current = item.index;
                if (closed[current])
                    continue;
                closed[current] = true;
                if (current == goalIndex)
                    break;
                int cx = current / nz;
                int cz = current % nz;
                for (int offset = 0; offset < 4; offset++)
                {
                    int direction = (offset + rotation) % 4;
                    int xx = cx + dx[direction];
                    int zz = cz + dz[direction];
                    if (xx < 0 || xx >= nx || zz < 0 || zz >= nz)
                        continue;
                    int next = xx * nz + zz;
                    if (!free[next] || closed[next])
                        continue;
                    if (projectAuthoredSurface
                        && (Mathf.Abs(points[next].y - points[current].y) > 1.25f
                            || !SegmentClear(points[current], points[next])))
                        continue;
                    float tentative = g[current] + 1f;
                    if (tentative >= g[next])
                        continue;
                    came[next] = current;
                    g[next] = tentative;
                    float tie = Hash01(next ^ variant) * 0.002f;
                    float f = tentative + Heuristic(next, goalIndex, nz) + tie;
                    bestF[next] = f;
                    heap.Push(new HeapItem(next, f));
                }
            }
            if (!closed[goalIndex])
                return false;

            List<int> reversed = new List<int>();
            int at = goalIndex;
            while (at >= 0)
            {
                reversed.Add(at);
                if (at == startIndex)
                    break;
                at = came[at];
            }
            if (reversed[reversed.Count - 1] != startIndex)
                return false;
            reversed.Reverse();
            output.Add(start);
            int previousDx = 0;
            int previousDz = 0;
            for (int i = 1; i < reversed.Count; i++)
            {
                int prior = reversed[i - 1];
                int current = reversed[i];
                int stepX = current / nz - prior / nz;
                int stepZ = current % nz - prior % nz;
                if (i > 1 && (stepX != previousDx || stepZ != previousDz))
                    output.Add(points[prior]);
                previousDx = stepX;
                previousDz = stepZ;
            }
            output.Add(goal);
            return output.Count >= 2;
        }

        private static bool InsideObstacle(Vector3 point, Bounds[] obstacles)
        {
            if (obstacles == null)
                return false;
            foreach (Bounds obstacle in obstacles)
            {
                Bounds expanded = obstacle;
                expanded.Expand(new Vector3(
                    AgentRadius * 2f,
                    0f,
                    AgentRadius * 2f));
                if (point.x >= expanded.min.x
                    && point.x <= expanded.max.x
                    && point.z >= expanded.min.z
                    && point.z <= expanded.max.z)
                    return true;
            }
            return false;
        }

        private static int FindNearestFree(
            Vector3[] points,
            bool[] free,
            Vector3 target)
        {
            float best = float.PositiveInfinity;
            int index = -1;
            for (int i = 0; i < points.Length; i++)
            {
                if (!free[i])
                    continue;
                float distance = (points[i] - target).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    index = i;
                }
            }
            return index;
        }

        private static bool SegmentClear(Vector3 a, Vector3 b)
        {
            Vector3 origin = a + Vector3.up * 0.7f;
            Vector3 target = b + Vector3.up * 0.7f;
            Vector3 delta = target - origin;
            if (delta.sqrMagnitude <= 1e-6f)
                return true;
            RaycastHit[] hits = Physics.RaycastAll(
                origin,
                delta.normalized,
                delta.magnitude,
                ~0,
                QueryTriggerInteraction.Ignore);
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null)
                    continue;
                if (hit.collider.GetComponentInParent<PlayerBody>() != null)
                    continue;
                return false;
            }
            return true;
        }

        private static float Heuristic(int a, int b, int nz)
        {
            int ax = a / nz;
            int az = a % nz;
            int bx = b / nz;
            int bz = b % nz;
            return Mathf.Abs(ax - bx) + Mathf.Abs(az - bz);
        }

        private static float Hash01(int value)
        {
            unchecked
            {
                uint x = (uint)value;
                x ^= x >> 17;
                x *= 0xed5ad4bbu;
                x ^= x >> 11;
                x *= 0xac4c1b51u;
                x ^= x >> 15;
                return (x & 0x00ffffffu) / 16777215f;
            }
        }

        private readonly struct HeapItem
        {
            public readonly int index;
            public readonly float priority;

            public HeapItem(int index, float priority)
            {
                this.index = index;
                this.priority = priority;
            }
        }

        private sealed class MinHeap
        {
            private readonly List<HeapItem> items = new List<HeapItem>();
            public int Count => items.Count;

            public void Push(HeapItem item)
            {
                items.Add(item);
                int child = items.Count - 1;
                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (items[parent].priority <= items[child].priority)
                        break;
                    HeapItem swap = items[parent];
                    items[parent] = items[child];
                    items[child] = swap;
                    child = parent;
                }
            }

            public HeapItem Pop()
            {
                HeapItem result = items[0];
                int last = items.Count - 1;
                items[0] = items[last];
                items.RemoveAt(last);
                int parent = 0;
                while (true)
                {
                    int left = parent * 2 + 1;
                    int right = left + 1;
                    if (left >= items.Count)
                        break;
                    int smallest = right < items.Count
                        && items[right].priority < items[left].priority
                        ? right
                        : left;
                    if (items[parent].priority <= items[smallest].priority)
                        break;
                    HeapItem swap = items[parent];
                    items[parent] = items[smallest];
                    items[smallest] = swap;
                    parent = smallest;
                }
                return result;
            }
        }
    }
}

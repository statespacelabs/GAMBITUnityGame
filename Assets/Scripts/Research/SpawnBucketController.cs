using System;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Deterministic spawn-bucket planner. It is inert unless
/// PHASE4_4_ENABLE_SPAWN_BUCKETS=1.
/// </summary>
public static class SpawnBucketController
{
    private const int MaxAttempts = 512;
    private const float AreaSpacing = 500f;

    public struct SpawnPlan
    {
        public bool Enabled;
        public int AreaId;
        public string RequestedBucket;
        public string Bucket;
        public int SpawnSeed;
        public Vector3 PosA;
        public Vector3 PosB;
        public Quaternion RotA;
        public Quaternion RotB;
        public float SpawnDistance;
        public bool InitialLineOfSight;
        public bool ObstacleBetweenPlayers;
        public bool SpawnConstraintSatisfied;
        public string SpawnConstraintFailureReason;
        public float TrialMaxSeconds;
        public int AttemptCount;
        public int SelectedAttempt;
        public string RequestedMapId;
        public string ActiveMapId;
        public string MapProfile;
        public int MapSeed;
        public string MapRegistrySha256;
        public string MapAssetSha256;
        public bool SurfaceValidA;
        public bool SurfaceValidB;
        public bool OutOfBounds;
        public bool FallDetected;
    }

    private struct CandidateAssessment
    {
        public bool HasValue;
        public Vector3 PosA;
        public Vector3 PosB;
        public float Distance;
        public bool InitialLineOfSight;
        public bool ObstacleBetweenPlayers;
        public bool Satisfied;
        public string FailureReason;
        public int Penalty;
        public int Attempt;
    }

    public static bool IsEnabled()
    {
        return DemoMapRuntime.IsDemoConfigured
            || Environment.GetEnvironmentVariable("PHASE4_4_ENABLE_SPAWN_BUCKETS") == "1";
    }

    public static SpawnPlan BuildPlan(int areaId, Vector3 areaOffset)
    {
        SpawnPlan plan = DisabledPlan(areaId);
        if (!IsEnabled())
            return plan;

        string requested = (Environment.GetEnvironmentVariable("PHASE4_4_SPAWN_BUCKET") ?? "random").Trim().ToLowerInvariant();
        int baseSeed = ParseIntEnv("PHASE4_4_SPAWN_SEED", 4400);
        int seed = MixSeed(baseSeed, areaId);
        System.Random rng = new System.Random(seed);
        string bucket = ResolveBucket(requested, rng);
        Vector2 range = ApplyDistanceOverrides(DefaultDistanceRange(bucket));
        bool requireInitialLos = ParseBoolEnv("PHASE4_4_REQUIRE_INITIAL_LOS", false);
        bool requireObstacle = ParseBoolEnv("PHASE4_4_REQUIRE_OBSTACLE_BETWEEN", false);
        float trialMaxSeconds = ParseFloatEnv("PHASE4_4_TRIAL_MAX_SECONDS", 60f);
        if (trialMaxSeconds <= 0f)
            trialMaxSeconds = 60f;

        if (DemoMapRuntime.ControlEnabled)
        {
            return BuildControlledMapPlan(
                areaId,
                requested,
                seed,
                rng,
                bucket,
                range,
                requireInitialLos,
                requireObstacle,
                trialMaxSeconds);
        }

        Vector3 baseA = new Vector3(35f, 1f, -80f) + areaOffset;
        CandidateAssessment best = new CandidateAssessment { HasValue = false, Penalty = int.MaxValue, FailureReason = "no_candidate_attempted" };

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            Vector3 posA = SamplePlayerAPosition(baseA, bucket, attempt, rng);
            float distance = SampleRange(range.x, range.y, rng);
            Vector3 dir = SampleDirection(bucket, attempt, rng);
            Vector3 posB = posA + dir * distance;
            posB.y = posA.y;

            CandidateAssessment candidate = AssessCandidate(
                posA,
                posB,
                bucket,
                requireInitialLos,
                requireObstacle,
                attempt);

            if (!best.HasValue || candidate.Penalty < best.Penalty)
            {
                best = candidate;
            }
            if (candidate.Satisfied && candidate.Penalty == 0)
            {
                best = candidate;
                break;
            }
        }

        if (!best.HasValue)
        {
            best = AssessCandidate(baseA, baseA + Vector3.forward * range.x, bucket, requireInitialLos, requireObstacle, 0);
            best.FailureReason = "no_candidate_available";
            best.Satisfied = false;
        }

        Vector3 flatAB = Vector3.ProjectOnPlane(best.PosB - best.PosA, Vector3.up);
        if (flatAB.sqrMagnitude < 1e-6f)
            flatAB = Vector3.forward;

        plan.Enabled = true;
        plan.AreaId = areaId;
        plan.RequestedBucket = requested;
        plan.Bucket = bucket;
        plan.SpawnSeed = seed;
        plan.PosA = best.PosA;
        plan.PosB = best.PosB;
        plan.RotA = Quaternion.LookRotation(flatAB.normalized, Vector3.up);
        plan.RotB = Quaternion.LookRotation((-flatAB).normalized, Vector3.up);
        plan.SpawnDistance = best.Distance;
        plan.InitialLineOfSight = best.InitialLineOfSight;
        plan.ObstacleBetweenPlayers = best.ObstacleBetweenPlayers;
        plan.SpawnConstraintSatisfied = best.Satisfied;
        plan.SpawnConstraintFailureReason = best.Satisfied ? "" : best.FailureReason;
        plan.TrialMaxSeconds = trialMaxSeconds;
        plan.AttemptCount = MaxAttempts;
        plan.SelectedAttempt = best.Attempt;

        Debug.Log(
            "[SpawnBuckets] " +
            $"area={areaId} requested={requested} bucket={bucket} distance={plan.SpawnDistance:F2} " +
            $"los={(plan.InitialLineOfSight ? 1 : 0)} obstacle={(plan.ObstacleBetweenPlayers ? 1 : 0)} " +
            $"satisfied={(plan.SpawnConstraintSatisfied ? 1 : 0)} reason={plan.SpawnConstraintFailureReason}");
        return plan;
    }

    private static SpawnPlan BuildControlledMapPlan(
        int areaId,
        string requested,
        int seed,
        System.Random rng,
        string bucket,
        Vector2 range,
        bool requireInitialLos,
        bool requireObstacle,
        float trialMaxSeconds)
    {
        const int controlledMaxAttempts = 8192;
        SpawnPlan plan = DisabledPlan(areaId);
        plan.Enabled = true;
        plan.RequestedBucket = requested;
        plan.Bucket = bucket;
        plan.SpawnSeed = seed;
        plan.TrialMaxSeconds = trialMaxSeconds;
        plan.AttemptCount = controlledMaxAttempts;
        plan.RequestedMapId = DemoMapRuntime.RequestedMapId;
        plan.ActiveMapId = DemoMapRuntime.ActiveMapId;
        plan.MapProfile = DemoMapRuntime.ActiveProfile;
        plan.MapSeed = DemoMapRuntime.ActiveSeed;
        plan.MapRegistrySha256 = DemoMapRuntime.RegistrySha256;
        plan.MapAssetSha256 = DemoMapRuntime.ActiveAssetSha256;

        Bounds bounds = DemoMapRuntime.ActiveBounds;
        bool encounterZoneEnabled = TryParseEncounterZone(
            out float encounterMinX,
            out float encounterMaxX,
            out float encounterMinZ,
            out float encounterMaxZ);
        const float horizontalInset = 2f;
        CandidateAssessment best = new CandidateAssessment
        {
            HasValue = false,
            Penalty = int.MaxValue,
            FailureReason = "no_surface_candidate_attempted"
        };
        string lastSurfaceFailure = "";

        if (bounds.size.x <= horizontalInset * 2f || bounds.size.z <= horizontalInset * 2f)
        {
            lastSurfaceFailure = "active_map_bounds_too_small";
        }
        else
        {
            for (int attempt = 0; attempt < controlledMaxAttempts; attempt++)
            {
                Vector3 posA;
                string reasonA;
                bool sampledA;
                if (encounterZoneEnabled)
                {
                    Vector3 requestedA = new Vector3(
                        SampleRange(encounterMinX, encounterMaxX, rng),
                        bounds.max.y + 1f,
                        SampleRange(encounterMinZ, encounterMaxZ, rng));
                    sampledA = DemoMapRuntime.TryProjectToSurface(
                        requestedA, out posA, out reasonA);
                }
                else
                {
                    sampledA = DemoMapRuntime.TrySampleSurface(
                        rng, out posA, out reasonA);
                }
                if (!sampledA)
                {
                    lastSurfaceFailure = "player_a:" + reasonA;
                    continue;
                }

                float distance = SampleRange(range.x, range.y, rng);
                Vector3 direction = SampleDirection(bucket, attempt, rng);
                Vector3 requestedB = posA + direction * distance;
                requestedB.y = bounds.max.y + 1f;
                if (!DemoMapRuntime.TryProjectToSurface(requestedB, out Vector3 posB, out string reasonB))
                {
                    lastSurfaceFailure = "player_b:" + reasonB;
                    continue;
                }
                if (encounterZoneEnabled && (
                    posB.x < encounterMinX || posB.x > encounterMaxX
                    || posB.z < encounterMinZ || posB.z > encounterMaxZ))
                {
                    lastSurfaceFailure = "player_b:outside_encounter_zone";
                    continue;
                }
                if (Mathf.Abs(posA.y - posB.y) > 1.5f)
                {
                    lastSurfaceFailure = "bounded_height_difference_exceeded";
                    continue;
                }

                CandidateAssessment candidate = AssessCandidate(
                    posA,
                    posB,
                    bucket,
                    requireInitialLos,
                    requireObstacle,
                    attempt);
                if (!best.HasValue || candidate.Penalty < best.Penalty)
                    best = candidate;
                if (candidate.Satisfied && candidate.Penalty == 0)
                {
                    best = candidate;
                    break;
                }
            }
        }

        if (!best.HasValue)
        {
            plan.PosA = bounds.center;
            plan.PosB = bounds.center + Vector3.forward;
            plan.RotA = Quaternion.identity;
            plan.RotB = Quaternion.identity;
            plan.SpawnDistance = 1f;
            plan.SpawnConstraintSatisfied = false;
            plan.SpawnConstraintFailureReason = string.IsNullOrEmpty(lastSurfaceFailure)
                ? "no_valid_surface_pair"
                : "no_valid_surface_pair;" + lastSurfaceFailure;
            plan.SelectedAttempt = -1;
            Debug.LogError("[DemoSpawn] " + plan.SpawnConstraintFailureReason);
            Application.Quit(47);
            return plan;
        }

        Vector3 flatAB = Vector3.ProjectOnPlane(best.PosB - best.PosA, Vector3.up);
        if (flatAB.sqrMagnitude < 1e-6f)
            flatAB = Vector3.forward;
        plan.PosA = best.PosA;
        plan.PosB = best.PosB;
        plan.RotA = Quaternion.LookRotation(flatAB.normalized, Vector3.up);
        plan.RotB = Quaternion.LookRotation((-flatAB).normalized, Vector3.up);
        plan.SpawnDistance = best.Distance;
        plan.InitialLineOfSight = best.InitialLineOfSight;
        plan.ObstacleBetweenPlayers = best.ObstacleBetweenPlayers;
        plan.SpawnConstraintSatisfied = best.Satisfied;
        plan.SpawnConstraintFailureReason = best.Satisfied ? "" : best.FailureReason;
        plan.SelectedAttempt = best.Attempt;
        plan.SurfaceValidA = true;
        plan.SurfaceValidB = true;
        plan.OutOfBounds = false;
        plan.FallDetected = false;

        if (!plan.SpawnConstraintSatisfied)
        {
            Debug.LogError($"[DemoSpawn] map={plan.ActiveMapId} bucket={bucket} constraint={plan.SpawnConstraintFailureReason}");
            Application.Quit(47);
        }
        else
        {
            Debug.Log(
                $"[DemoSpawn] map={plan.ActiveMapId} profile={plan.MapProfile} seed={plan.MapSeed} " +
                $"bucket={bucket} distance={plan.SpawnDistance:F2} attempt={plan.SelectedAttempt} " +
                $"los={(plan.InitialLineOfSight ? 1 : 0)} obstacle={(plan.ObstacleBetweenPlayers ? 1 : 0)}");
        }
        return plan;
    }

    public static bool HasLineOfSightBetween(PlayerBody a, PlayerBody b)
    {
        if (a == null || b == null)
            return false;
        Vector3 origin = ChestPoint(a.transform.position);
        Vector3 target = ChestPoint(b.transform.position);
        return HasLineOfSight(origin, target, a.transform, b.transform);
    }

    public static bool HasLineOfSightAtPositions(Vector3 posA, Vector3 posB)
    {
        return HasLineOfSight(ChestPoint(posA), ChestPoint(posB), null, null);
    }

    public static bool HasObstacleBetween(PlayerBody a, PlayerBody b)
    {
        return !HasLineOfSightBetween(a, b);
    }

    public static int AreaFromPosition(Vector3 position)
    {
        return Mathf.Max(0, Mathf.RoundToInt(position.x / AreaSpacing));
    }

    private static SpawnPlan DisabledPlan(int areaId)
    {
        SpawnPlan plan = new SpawnPlan();
        plan.Enabled = false;
        plan.AreaId = areaId;
        plan.RequestedBucket = "";
        plan.Bucket = "";
        plan.SpawnConstraintFailureReason = "";
        plan.TrialMaxSeconds = 60f;
        plan.RequestedMapId = "";
        plan.ActiveMapId = "";
        plan.MapProfile = "";
        plan.MapRegistrySha256 = "";
        plan.MapAssetSha256 = "";
        return plan;
    }

    private static CandidateAssessment AssessCandidate(
        Vector3 posA,
        Vector3 posB,
        string bucket,
        bool requireInitialLos,
        bool requireObstacle,
        int attempt)
    {
        bool los = HasLineOfSightAtPositions(posA, posB);
        bool obstacle = !los;
        string failure = "";
        int penalty = 0;

        if (requireInitialLos && !los)
            AddFailure(ref failure, "required_initial_los_false");
        if (requireObstacle && !obstacle)
            AddFailure(ref failure, "required_obstacle_between_false");
        if (bucket == "obstacle" && !obstacle)
            AddFailure(ref failure, "obstacle_bucket_without_blocking_collider");
        if (bucket == "search_destroy")
        {
            if (los)
                AddFailure(ref failure, "search_destroy_initial_los_true");
            if (!obstacle)
                AddFailure(ref failure, "search_destroy_obstacle_between_false");
        }

        bool satisfied = string.IsNullOrEmpty(failure);
        if (!satisfied)
            penalty += 1000;
        if (bucket == "close" && !los)
            penalty += 5;
        if ((bucket == "obstacle" || bucket == "search_destroy") && los)
            penalty += 10;

        CandidateAssessment candidate = new CandidateAssessment();
        candidate.HasValue = true;
        candidate.PosA = posA;
        candidate.PosB = posB;
        candidate.Distance = Vector3.Distance(posA, posB);
        candidate.InitialLineOfSight = los;
        candidate.ObstacleBetweenPlayers = obstacle;
        candidate.Satisfied = satisfied;
        candidate.FailureReason = satisfied ? "" : failure;
        candidate.Penalty = penalty;
        candidate.Attempt = attempt;
        return candidate;
    }

    private static void AddFailure(ref string failure, string reason)
    {
        if (string.IsNullOrEmpty(failure))
            failure = reason;
        else
            failure += ";" + reason;
    }

    private static Vector3 SamplePlayerAPosition(Vector3 baseA, string bucket, int attempt, System.Random rng)
    {
        if (bucket != "obstacle" && bucket != "search_destroy")
            return baseA;
        if (attempt < MaxAttempts / 2)
            return baseA;

        double angle = rng.NextDouble() * Math.PI * 2.0;
        double radius = 2.0 + rng.NextDouble() * 10.0;
        return baseA + new Vector3((float)(Math.Sin(angle) * radius), 0f, (float)(Math.Cos(angle) * radius));
    }

    private static Vector3 SampleDirection(string bucket, int attempt, System.Random rng)
    {
        if ((bucket == "obstacle" || bucket == "search_destroy") && attempt < 16)
        {
            float deg = attempt * 22.5f;
            return (Quaternion.Euler(0f, deg, 0f) * Vector3.forward).normalized;
        }

        double angle = rng.NextDouble() * Math.PI * 2.0;
        return new Vector3((float)Math.Sin(angle), 0f, (float)Math.Cos(angle)).normalized;
    }

    private static float SampleRange(float min, float max, System.Random rng)
    {
        if (max < min)
        {
            float tmp = min;
            min = max;
            max = tmp;
        }
        if (Mathf.Abs(max - min) < 1e-3f)
            return min;
        return min + (float)rng.NextDouble() * (max - min);
    }

    private static string ResolveBucket(string requested, System.Random rng)
    {
        switch (requested)
        {
            case "close":
            case "close_contact":
                return "close";
            case "mid":
            case "mid_contact":
                return "mid";
            case "far":
            case "far_contact":
                return "far";
            case "obstacle":
            case "obstacle_separated":
                return "obstacle";
            case "search_destroy":
                return "search_destroy";
            case "random":
            default:
                int pick = rng.Next(0, 5);
                if (pick == 0) return "close";
                if (pick == 1) return "mid";
                if (pick == 2) return "far";
                if (pick == 3) return "obstacle";
                return "search_destroy";
        }
    }

    private static Vector2 DefaultDistanceRange(string bucket)
    {
        switch (bucket)
        {
            case "close": return new Vector2(5f, 10f);
            case "mid": return new Vector2(10f, 20f);
            case "far": return new Vector2(20f, 40f);
            case "obstacle": return new Vector2(10f, 30f);
            case "search_destroy": return new Vector2(20f, 50f);
            default: return new Vector2(10f, 20f);
        }
    }

    private static Vector2 ApplyDistanceOverrides(Vector2 fallback)
    {
        float min = fallback.x;
        float max = fallback.y;
        float parsed;
        if (TryParseFloatEnv("PHASE4_4_SPAWN_DISTANCE_MIN", out parsed))
            min = parsed;
        if (TryParseFloatEnv("PHASE4_4_SPAWN_DISTANCE_MAX", out parsed))
            max = parsed;
        if (max < min)
        {
            float tmp = min;
            min = max;
            max = tmp;
        }
        if (max - min < 0.1f)
            max = min + 0.1f;
        return new Vector2(Mathf.Max(0.1f, min), Mathf.Max(0.2f, max));
    }

    private static bool HasLineOfSight(Vector3 origin, Vector3 target, Transform ignoreA, Transform ignoreB)
    {
        Vector3 delta = target - origin;
        float distance = delta.magnitude;
        if (distance <= 1e-3f)
            return true;

        RaycastHit[] hits = Physics.RaycastAll(origin, delta.normalized, distance + 0.05f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            Transform hitTransform = hits[i].transform;
            if (IsInHierarchy(hitTransform, ignoreA))
                continue;
            if (IsInHierarchy(hitTransform, ignoreB))
                return true;
            return false;
        }
        return true;
    }

    private static bool IsInHierarchy(Transform candidate, Transform root)
    {
        if (candidate == null || root == null)
            return false;
        return candidate == root || candidate.IsChildOf(root);
    }

    private static Vector3 ChestPoint(Vector3 position)
    {
        return position + Vector3.up * 0.7f;
    }

    private static bool ParseBoolEnv(string key, bool fallback)
    {
        string value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(value))
            return fallback;
        return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseIntEnv(string key, int fallback)
    {
        string value = Environment.GetEnvironmentVariable(key);
        int parsed;
        if (!string.IsNullOrEmpty(value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            return parsed;
        return fallback;
    }

    private static float ParseFloatEnv(string key, float fallback)
    {
        float parsed;
        if (TryParseFloatEnv(key, out parsed))
            return parsed;
        return fallback;
    }

    private static bool TryParseFloatEnv(string key, out float parsed)
    {
        parsed = 0f;
        string value = Environment.GetEnvironmentVariable(key);
        return !string.IsNullOrEmpty(value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TryParseEncounterZone(
        out float minX, out float maxX, out float minZ, out float maxZ)
    {
        minX = maxX = minZ = maxZ = 0f;
        string rawMinX = Environment.GetEnvironmentVariable("PHASE4_7_ENCOUNTER_MIN_X");
        string rawMaxX = Environment.GetEnvironmentVariable("PHASE4_7_ENCOUNTER_MAX_X");
        string rawMinZ = Environment.GetEnvironmentVariable("PHASE4_7_ENCOUNTER_MIN_Z");
        string rawMaxZ = Environment.GetEnvironmentVariable("PHASE4_7_ENCOUNTER_MAX_Z");
        bool any = !string.IsNullOrEmpty(rawMinX)
            || !string.IsNullOrEmpty(rawMaxX)
            || !string.IsNullOrEmpty(rawMinZ)
            || !string.IsNullOrEmpty(rawMaxZ);
        if (!any)
            return false;
        bool all = float.TryParse(rawMinX, NumberStyles.Float, CultureInfo.InvariantCulture, out minX)
            && float.TryParse(rawMaxX, NumberStyles.Float, CultureInfo.InvariantCulture, out maxX)
            && float.TryParse(rawMinZ, NumberStyles.Float, CultureInfo.InvariantCulture, out minZ)
            && float.TryParse(rawMaxZ, NumberStyles.Float, CultureInfo.InvariantCulture, out maxZ);
        if (!all || maxX <= minX || maxZ <= minZ)
            throw new InvalidOperationException("Phase 4.7 encounter zone is incomplete or invalid");
        return true;
    }

    private static int MixSeed(int baseSeed, int areaId)
    {
        unchecked
        {
            int seed = baseSeed;
            seed = seed * 16777619 + areaId * 73856093;
            seed = seed * 16777619 + 44;
            return seed;
        }
    }
}

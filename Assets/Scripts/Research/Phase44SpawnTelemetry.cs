using System;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// JSONL telemetry writer for Phase 4.4 controlled spawn and trial termination
/// diagnostics. It writes only when PHASE4_4_WRITE_SPAWN_TELEMETRY=1.
/// </summary>
public static class Phase44SpawnTelemetry
{
    public static bool IsEnabled()
    {
        return Environment.GetEnvironmentVariable("PHASE4_4_WRITE_SPAWN_TELEMETRY") == "1";
    }

    public static string TelemetryPath()
    {
        string path = Environment.GetEnvironmentVariable("PHASE4_4_SPAWN_TELEMETRY_PATH");
        if (!string.IsNullOrEmpty(path))
            return path;
        return Path.Combine(Application.persistentDataPath, "phase4_4_spawn_telemetry.jsonl");
    }

    public static void WriteSpawn(
        Phase44SpawnBucketController.SpawnPlan plan,
        int trialIndex,
        float elapsedSeconds,
        float timeToFirstLos,
        float timeToFirstShot,
        float timeToFirstHit,
        float timeToFirstDamage,
        int contactLostCount,
        int reacquireCount,
        float stuckSeconds,
        bool pathingFailure)
    {
        WriteRow(
            "spawn",
            plan,
            trialIndex,
            elapsedSeconds,
            "",
            timeToFirstLos,
            timeToFirstShot,
            timeToFirstHit,
            timeToFirstDamage,
            contactLostCount,
            reacquireCount,
            stuckSeconds,
            pathingFailure);
    }

    public static void WriteTermination(
        Phase44SpawnBucketController.SpawnPlan plan,
        int trialIndex,
        float elapsedSeconds,
        string terminationReason,
        float timeToFirstLos,
        float timeToFirstShot,
        float timeToFirstHit,
        float timeToFirstDamage,
        int contactLostCount,
        int reacquireCount,
        float stuckSeconds,
        bool pathingFailure)
    {
        WriteRow(
            "termination",
            plan,
            trialIndex,
            elapsedSeconds,
            terminationReason,
            timeToFirstLos,
            timeToFirstShot,
            timeToFirstHit,
            timeToFirstDamage,
            contactLostCount,
            reacquireCount,
            stuckSeconds,
            pathingFailure);
    }

    private static void WriteRow(
        string eventType,
        Phase44SpawnBucketController.SpawnPlan plan,
        int trialIndex,
        float elapsedSeconds,
        string terminationReason,
        float timeToFirstLos,
        float timeToFirstShot,
        float timeToFirstHit,
        float timeToFirstDamage,
        int contactLostCount,
        int reacquireCount,
        float stuckSeconds,
        bool pathingFailure)
    {
        if (!IsEnabled())
            return;

        Phase44TelemetryRow row = new Phase44TelemetryRow();
        row.timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        row.event_type = eventType;
        row.area_id = plan.AreaId;
        row.trial_index = trialIndex;
        row.trial_max_seconds = plan.TrialMaxSeconds;
        row.elapsed_seconds = Mathf.Max(0f, elapsedSeconds);
        row.termination_reason = terminationReason ?? "";
        row.spawn_bucket = plan.Bucket ?? "";
        row.spawn_requested_bucket = plan.RequestedBucket ?? "";
        row.spawn_distance = plan.SpawnDistance;
        row.initial_line_of_sight = plan.InitialLineOfSight ? 1 : 0;
        row.obstacle_between_players = plan.ObstacleBetweenPlayers ? 1 : 0;
        row.spawn_seed = plan.SpawnSeed;
        row.spawn_constraint_satisfied = plan.SpawnConstraintSatisfied;
        row.spawn_constraint_failure_reason = plan.SpawnConstraintFailureReason ?? "";
        row.time_to_first_los = timeToFirstLos;
        row.time_to_first_shot = timeToFirstShot;
        row.time_to_first_hit = timeToFirstHit;
        row.time_to_first_damage = timeToFirstDamage;
        row.contact_lost_count = contactLostCount;
        row.reacquire_count = reacquireCount;
        row.stuck_seconds = stuckSeconds;
        row.pathing_failure = pathingFailure;
        row.selected_attempt = plan.SelectedAttempt;
        row.attempt_count = plan.AttemptCount;
        row.requested_map_id = plan.RequestedMapId ?? "";
        row.active_map_id = plan.ActiveMapId ?? "";
        row.map_profile = plan.MapProfile ?? "";
        row.map_seed = plan.MapSeed;
        row.map_registry_sha256 = plan.MapRegistrySha256 ?? "";
        row.map_asset_sha256 = plan.MapAssetSha256 ?? "";
        row.map_bounds_min_x = DemoMapRuntime.ActiveBounds.min.x;
        row.map_bounds_min_y = DemoMapRuntime.ActiveBounds.min.y;
        row.map_bounds_min_z = DemoMapRuntime.ActiveBounds.min.z;
        row.map_bounds_max_x = DemoMapRuntime.ActiveBounds.max.x;
        row.map_bounds_max_y = DemoMapRuntime.ActiveBounds.max.y;
        row.map_bounds_max_z = DemoMapRuntime.ActiveBounds.max.z;
        row.map_collider_count = DemoMapRuntime.ActiveColliderCount;
        row.map_load_milliseconds = DemoMapRuntime.LoadMilliseconds;
        row.surface_valid_a = plan.SurfaceValidA;
        row.surface_valid_b = plan.SurfaceValidB;
        row.out_of_bounds = plan.OutOfBounds;
        row.fall_detected = plan.FallDetected;
        row.spawn_pos_a_x = plan.PosA.x;
        row.spawn_pos_a_y = plan.PosA.y;
        row.spawn_pos_a_z = plan.PosA.z;
        row.spawn_pos_b_x = plan.PosB.x;
        row.spawn_pos_b_y = plan.PosB.y;
        row.spawn_pos_b_z = plan.PosB.z;

        try
        {
            string path = TelemetryPath();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(path, JsonUtility.ToJson(row) + "\n");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Phase44Telemetry] Failed to write telemetry: {ex.Message}");
        }
    }

    [Serializable]
    private class Phase44TelemetryRow
    {
        public string timestamp;
        public string event_type;
        public int area_id;
        public int trial_index;
        public float trial_max_seconds;
        public float elapsed_seconds;
        public string termination_reason;
        public string spawn_bucket;
        public string spawn_requested_bucket;
        public float spawn_distance;
        public int initial_line_of_sight;
        public int obstacle_between_players;
        public int spawn_seed;
        public bool spawn_constraint_satisfied;
        public string spawn_constraint_failure_reason;
        public float time_to_first_los;
        public float time_to_first_shot;
        public float time_to_first_hit;
        public float time_to_first_damage;
        public int contact_lost_count;
        public int reacquire_count;
        public float stuck_seconds;
        public bool pathing_failure;
        public int selected_attempt;
        public int attempt_count;
        public string requested_map_id;
        public string active_map_id;
        public string map_profile;
        public int map_seed;
        public string map_registry_sha256;
        public string map_asset_sha256;
        public float map_bounds_min_x;
        public float map_bounds_min_y;
        public float map_bounds_min_z;
        public float map_bounds_max_x;
        public float map_bounds_max_y;
        public float map_bounds_max_z;
        public int map_collider_count;
        public float map_load_milliseconds;
        public bool surface_valid_a;
        public bool surface_valid_b;
        public bool out_of_bounds;
        public bool fall_detected;
        public float spawn_pos_a_x;
        public float spawn_pos_a_y;
        public float spawn_pos_a_z;
        public float spawn_pos_b_x;
        public float spawn_pos_b_y;
        public float spawn_pos_b_z;
    }
}

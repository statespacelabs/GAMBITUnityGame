using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Phase 3V true shot geometry logger. Writes JSONL when SHOT_DEBUG_UNITY=1.
/// Path from SHOT_DEBUG_UNITY_PATH; per-area sample limit from SHOT_DEBUG_SAMPLE_LIMIT.
/// </summary>
public static class ShotGeometryLogger
{
    private static bool enabled;
    private static string logPath;
    private static int sampleLimitPerArea = 20;
    private static readonly Dictionary<string, int> areaSampleCounts = new Dictionary<string, int>();
    private static int shotIdCounter = 0;
    private static bool configured;

    /// <summary>Configure from SHOT_DEBUG_UNITY_CONFIG JSON file and/or env vars.</summary>
    public static void Configure()
    {
        enabled = false;
        logPath = "";
        sampleLimitPerArea = 20;

        string configPath = Environment.GetEnvironmentVariable("SHOT_DEBUG_UNITY_CONFIG");
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
        {
            try
            {
                string json = File.ReadAllText(configPath);
                var cfg = JsonUtility.FromJson<ShotDebugConfigFile>(json);
                if (cfg != null)
                {
                    enabled = cfg.enabled;
                    logPath = cfg.path ?? "";
                    if (cfg.sample_limit > 0)
                    {
                        sampleLimitPerArea = cfg.sample_limit;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ShotGeometryLogger] config file read failed: {ex.Message}");
            }
        }

        if (!enabled)
        {
            enabled = Environment.GetEnvironmentVariable("SHOT_DEBUG_UNITY") == "1";
        }
        if (string.IsNullOrEmpty(logPath))
        {
            logPath = Environment.GetEnvironmentVariable("SHOT_DEBUG_UNITY_PATH") ?? "";
        }
        string limitStr = Environment.GetEnvironmentVariable("SHOT_DEBUG_SAMPLE_LIMIT");
        if (!string.IsNullOrEmpty(limitStr) && int.TryParse(limitStr, out int parsed))
        {
            sampleLimitPerArea = Mathf.Max(1, parsed);
        }

        if (!enabled || string.IsNullOrEmpty(logPath))
        {
            enabled = false;
        }
        else
        {
            try
            {
                string dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ShotGeometryLogger] mkdir failed: {ex.Message}");
            }
        }

        areaSampleCounts.Clear();
        shotIdCounter = 0;
        configured = true;

        if (enabled)
        {
            Debug.Log(
                $"[ShotGeometryLogger] enabled path={logPath} sample_limit={sampleLimitPerArea}"
            );
        }
    }

    public static bool IsEnabled => enabled;

    private static void EnsureConfigured()
    {
        if (!configured)
        {
            Configure();
        }
    }

    public static bool ShouldLog(string areaKey)
    {
        EnsureConfigured();
        if (!enabled)
        {
            return false;
        }
        int count = areaSampleCounts.TryGetValue(areaKey, out int c) ? c : 0;
        return count < sampleLimitPerArea;
    }

    public static void LogShot(ShotDebugRecord record)
    {
        EnsureConfigured();
        if (!enabled)
        {
            return;
        }
        string areaKey = record.area_id ?? "unknown";
        if (!ShouldLog(areaKey))
        {
            return;
        }

        record.shot_id = ++shotIdCounter;
        record.time = Time.time;
        string json = JsonUtility.ToJson(record);
        try
        {
            File.AppendAllText(logPath, json + "\n");
            areaSampleCounts[areaKey] = areaSampleCounts.TryGetValue(areaKey, out int c) ? c + 1 : 1;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ShotGeometryLogger] write failed: {ex.Message}");
        }
    }
}

[Serializable]
public class ShotDebugRecord
{
    public int shot_id;
    public string area_id;
    public string agent_id;
    public float time;
    public int decision_step;
    public string shot_context;

    public float shot_origin_x;
    public float shot_origin_y;
    public float shot_origin_z;
    public float shot_direction_x;
    public float shot_direction_y;
    public float shot_direction_z;
    public float crosshair_forward_x;
    public float crosshair_forward_y;
    public float crosshair_forward_z;

    public float aim_err_at_fire;
    public float yaw_err_at_fire;
    public float pitch_err_at_fire;
    public float target_distance_at_fire;

    public float target_position_x;
    public float target_position_y;
    public float target_position_z;
    public float target_chest_x;
    public float target_chest_y;
    public float target_chest_z;

    public float hitbox_min_x;
    public float hitbox_min_y;
    public float hitbox_min_z;
    public float hitbox_max_x;
    public float hitbox_max_y;
    public float hitbox_max_z;

    public float intended_target_x;
    public float intended_target_y;
    public float intended_target_z;

    public bool raycast_hit_true;
    public string raycast_hit_collider;
    public string raycast_hit_body_part;
    public float raycast_hit_distance;
    public string raycast_miss_reason;

    public float weapon_spread_angle;
    public float recoil_state;
    public float cooldown_remaining_before_fire;
    public float cooldown_after_fire;
    public float shot_fired_obs_value;

    public float damage_applied_same_step;
    public float damage_applied_next_step;
    public float env_reward_same_step;
    public float env_reward_next_step;
    public bool aligned_at_fire;

    // Phase 3Y ray vs hurtbox diagnostics
    public float target_hurtbox_inflate;
    public string learner_shoot_ray_mode;
    public float camera_forward_x;
    public float camera_forward_y;
    public float camera_forward_z;
    public float hurtbox_center_x;
    public float hurtbox_center_y;
    public float hurtbox_center_z;
    public float closest_point_on_hurtbox_x;
    public float closest_point_on_hurtbox_y;
    public float closest_point_on_hurtbox_z;
    public float ray_to_hurtbox_min_distance;
    public float true_ray_hurtbox_error_deg;
    public float angle_weapon_ray_to_target_chest;
    public float angle_weapon_ray_to_hurtbox_closest;
    public float angle_crosshair_to_target_chest;
    public float weapon_ray_vs_crosshair_angle;

    // Phase 3AA ray correction diagnostics
    public float original_ray_direction_x;
    public float original_ray_direction_y;
    public float original_ray_direction_z;
    public float corrected_ray_direction_x;
    public float corrected_ray_direction_y;
    public float corrected_ray_direction_z;
    public float target_chest_direction_x;
    public float target_chest_direction_y;
    public float target_chest_direction_z;
    public float ray_correction_alpha;
    public float ray_correction_angle_applied_deg;
    public bool ray_correction_applied;
}

[Serializable]
public class ShotDebugConfigFile
{
    public bool enabled;
    public string path;
    public int sample_limit;
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Phase 3AC weapon / reset state debug logger.
/// Enabled via WEAPON_STATE_DEBUG=1; path from WEAPON_STATE_DEBUG_PATH.
/// </summary>
public static class WeaponStateDebugLogger
{
    private static bool enabled;
    private static string logPath;
    private static bool configured;

    public static bool IsEnabled => enabled;

    public static void Configure()
    {
        enabled = Environment.GetEnvironmentVariable("WEAPON_STATE_DEBUG") == "1";
        logPath = Environment.GetEnvironmentVariable("WEAPON_STATE_DEBUG_PATH") ?? "";
        if (!enabled || string.IsNullOrEmpty(logPath))
        {
            enabled = false;
            configured = true;
            return;
        }
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
            Debug.LogWarning($"[WeaponStateDebugLogger] mkdir failed: {ex.Message}");
            enabled = false;
        }
        configured = true;
    }

    public static void LogBlockedFire(
        string areaId,
        string agentId,
        string gameMode,
        int decisionStep,
        string blockedReason,
        bool shootPressed,
        bool canPlayerShoot,
        bool matchOver,
        bool matchCooldown,
        float weaponCooldownRemaining,
        float ammoCurrent,
        float ammoMax,
        bool isReloading,
        bool weaponEnabled,
        string rayMode,
        float rayCorrectionAlpha)
    {
        if (!enabled) return;
        string line =
            "{\"event\":\"blocked_fire\"," +
            "\"area_id\":\"" + Escape(areaId) + "\"," +
            "\"agent_id\":\"" + Escape(agentId) + "\"," +
            "\"game_mode\":\"" + Escape(gameMode) + "\"," +
            "\"decision_step\":" + decisionStep + "," +
            "\"shoot_pressed\":" + (shootPressed ? "true" : "false") + "," +
            "\"shot_fired_this_step\":false," +
            "\"blocked_fire_reason\":\"" + Escape(blockedReason) + "\"," +
            "\"weapon_can_fire\":" + (canPlayerShoot && weaponCooldownRemaining <= 0f ? "true" : "false") + "," +
            "\"weapon_enabled\":" + (weaponEnabled ? "true" : "false") + "," +
            "\"ammo_current\":" + F(ammoCurrent) + "," +
            "\"ammo_max\":" + F(ammoMax) + "," +
            "\"is_reloading\":" + (isReloading ? "true" : "false") + "," +
            "\"cooldown_timer\":" + F(weaponCooldownRemaining) + "," +
            "\"match_over\":" + (matchOver ? "true" : "false") + "," +
            "\"match_cooldown\":" + (matchCooldown ? "true" : "false") + "," +
            "\"ray_mode\":\"" + Escape(rayMode) + "\"," +
            "\"ray_correction_alpha\":" + F(rayCorrectionAlpha) + "}";
        WriteLine(line);
    }

    public static void LogWeaponReset(
        string areaId,
        string agentId,
        string reason)
    {
        if (!enabled) return;
        string line =
            "{\"event\":\"weapon_reset\"," +
            "\"area_id\":\"" + Escape(areaId) + "\"," +
            "\"agent_id\":\"" + Escape(agentId) + "\"," +
            "\"weapon_reset_this_step\":true," +
            "\"reset_reason\":\"" + Escape(reason) + "\"}";
        WriteLine(line);
    }

    private static void WriteLine(string line)
    {
        try
        {
            File.AppendAllText(logPath, line + "\n");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[WeaponStateDebugLogger] write failed: {ex.Message}");
        }
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string F(float v)
    {
        return v.ToString("G9", CultureInfo.InvariantCulture);
    }
}

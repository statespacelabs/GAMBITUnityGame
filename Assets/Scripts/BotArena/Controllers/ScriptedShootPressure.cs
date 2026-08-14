using UnityEngine;

/// <summary>
/// Global scripted-bot shooting pressure knobs read from environment variables.
/// Used by Phase 3R shooter curriculum bridge to soften incoming fire gradually.
/// </summary>
public static class ScriptedShootPressure
{
    public static float DamageScale = 1f;
    public static float CooldownMult = 1f;
    public static float AimThresholdDeg = 15f;
    public static float GlobalWarmupEndTime = 0f;
    public static bool ShootEnabled = true;

    public static bool IsWarmupActive => GlobalWarmupEndTime > 0f && Time.time < GlobalWarmupEndTime;

    public static void LoadFromEnvironment()
    {
        DamageScale = ParseFloat("SCRIPTED_SHOOT_DAMAGE_SCALE", 1f);
        CooldownMult = ParseFloat("SCRIPTED_SHOOT_COOLDOWN_MULT", 1f);
        AimThresholdDeg = ParseFloat("SCRIPTED_SHOOT_AIM_THRESHOLD_DEG", 15f);
        ShootEnabled = System.Environment.GetEnvironmentVariable("SCRIPTED_SHOOT_ENABLED") != "0";

        float warmupSec = ParseFloat("SCRIPTED_SHOOT_WARMUP_SEC", 0f);
        string warmupItersEnv = System.Environment.GetEnvironmentVariable("SCRIPTED_SHOOT_WARMUP_ITERS");
        if (!string.IsNullOrEmpty(warmupItersEnv)
            && int.TryParse(warmupItersEnv, out int warmupIters)
            && warmupIters > 0)
        {
            float secPerIter = ParseFloat("SCRIPTED_SHOOT_WARMUP_SEC_PER_ITER", 45f);
            warmupSec = warmupIters * secPerIter;
        }

        GlobalWarmupEndTime = warmupSec > 0f ? Time.time + warmupSec : 0f;
    }

    private static float ParseFloat(string key, float defaultValue)
    {
        string raw = System.Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(raw))
            return defaultValue;
        return float.TryParse(raw, out float parsed) ? parsed : defaultValue;
    }
}

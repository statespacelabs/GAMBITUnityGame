using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// Headless-only buffered training parity trace. Sampling happens after normal
/// FixedUpdate work; disk I/O is deferred until process shutdown.
/// </summary>
[DefaultExecutionOrder(32000)]
[UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, null, "Phase5RuntimeMetrics")]
public sealed class TrainingRuntimeMetrics : MonoBehaviour
{
    private static TrainingRuntimeMetrics instance;
    private readonly List<AreaState> areas = new List<AreaState>();
    private readonly List<TraceRow> rows = new List<TraceRow>();
    private readonly List<float> resetDurationsMs = new List<float>();
    private int fixedTick;
    private long previousFixedWallTimestamp;
    private readonly List<float> fixedTickWallMs = new List<float>();
    private bool flushed;

    public static bool Enabled =>
        HeadlessTrainingRuntime.Enabled
        && Environment.GetEnvironmentVariable("PHASE5_RUNTIME_METRICS") == "1";

    public static TrainingRuntimeMetrics Ensure()
    {
        if (!Enabled)
            return null;
        if (instance != null)
            return instance;
        GameObject metricsObject = new GameObject("_TrainingRuntimeMetrics");
        instance = metricsObject.AddComponent<TrainingRuntimeMetrics>();
        return instance;
    }

    public static void RegisterArea(int areaId, MatchManager match, PlayerBody playerA, PlayerBody playerB)
    {
        TrainingRuntimeMetrics metrics = Ensure();
        if (metrics == null)
            return;
        metrics.areas.Add(new AreaState(areaId, match, playerA, playerB));
    }

    public static void RecordResetDuration(double milliseconds)
    {
        if (instance == null || !Enabled || double.IsNaN(milliseconds) || double.IsInfinity(milliseconds))
            return;
        instance.resetDurationsMs.Add((float)Math.Max(0.0, milliseconds));
    }

    public static Stopwatch StartResetTimer()
    {
        return Enabled ? Stopwatch.StartNew() : null;
    }

    public static void FinishResetTimer(Stopwatch timer)
    {
        if (timer == null)
            return;
        timer.Stop();
        RecordResetDuration(timer.Elapsed.TotalMilliseconds);
    }

    private void FixedUpdate()
    {
        if (!Enabled)
            return;
        long now = Stopwatch.GetTimestamp();
        float wallMs = previousFixedWallTimestamp == 0
            ? 0f
            : (float)((now - previousFixedWallTimestamp) * 1000.0 / Stopwatch.Frequency);
        previousFixedWallTimestamp = now;
        if (wallMs > 0f && !float.IsNaN(wallMs) && !float.IsInfinity(wallMs))
            fixedTickWallMs.Add(wallMs);
        fixedTick++;
        foreach (AreaState area in areas)
            rows.Add(area.Capture(fixedTick, wallMs));
    }

    private void OnApplicationQuit()
    {
        FlushNow();
    }

    private void OnDestroy()
    {
        FlushNow();
        if (instance == this)
            instance = null;
    }

    public void FlushNow()
    {
        if (flushed || !Enabled)
            return;
        flushed = true;

        string tracePath = Environment.GetEnvironmentVariable("PHASE5_RUNTIME_TRACE_PATH") ?? "";
        string summaryPath = Environment.GetEnvironmentVariable("PHASE5_RUNTIME_SUMMARY_PATH") ?? "";
        try
        {
            if (!string.IsNullOrWhiteSpace(tracePath))
            {
                EnsureParent(tracePath);
                using (StreamWriter writer = new StreamWriter(tracePath, false))
                    foreach (TraceRow row in rows)
                        writer.WriteLine(JsonUtility.ToJson(row));
            }

            RuntimeSummary summary = BuildSummary();
            if (!string.IsNullOrWhiteSpace(summaryPath))
            {
                EnsureParent(summaryPath);
                File.WriteAllText(summaryPath, JsonUtility.ToJson(summary, true) + "\n");
            }
            Debug.Log("[TrainingRuntimeSummary] " + JsonUtility.ToJson(summary));
        }
        catch (Exception ex)
        {
            Debug.LogError("[TrainingRuntimeMetrics] flush failed: " + ex);
        }
    }

    private RuntimeSummary BuildSummary()
    {
        RuntimeSummary summary = new RuntimeSummary();
        summary.schema_version = "phase5_runtime_metrics_v1";
        summary.fixed_ticks = fixedTick;
        summary.area_count = areas.Count;
        summary.trace_rows = rows.Count;
        summary.reset_samples = resetDurationsMs.Count;
        summary.headless_audit = HeadlessTrainingRuntime.LastAudit;
        if (fixedTickWallMs.Count > 0)
        {
            List<float> fixedOrdered = new List<float>(fixedTickWallMs);
            fixedOrdered.Sort();
            summary.fixed_tick_wall_ms_p50 = Percentile(fixedOrdered, 0.50f);
            summary.fixed_tick_wall_ms_p95 = Percentile(fixedOrdered, 0.95f);
        }
        if (resetDurationsMs.Count > 0)
        {
            List<float> ordered = new List<float>(resetDurationsMs);
            ordered.Sort();
            double sum = 0.0;
            foreach (float value in ordered)
                sum += value;
            summary.reset_latency_ms_mean = (float)(sum / ordered.Count);
            summary.reset_latency_ms_p50 = Percentile(ordered, 0.50f);
            summary.reset_latency_ms_p95 = Percentile(ordered, 0.95f);
            summary.reset_latency_ms_max = ordered[ordered.Count - 1];
        }
        summary.status =
            summary.headless_audit != null
            && summary.headless_audit.status == "PASS"
            && summary.fixed_ticks > 0
            && summary.trace_rows > 0 ? "PASS" : "FAIL";
        return summary;
    }

    private static float Percentile(List<float> ordered, float quantile)
    {
        if (ordered == null || ordered.Count == 0)
            return 0f;
        int index = Mathf.Clamp(Mathf.CeilToInt(quantile * ordered.Count) - 1, 0, ordered.Count - 1);
        return ordered[index];
    }

    private static void EnsureParent(string path)
    {
        string parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
    }

    private static bool HasLineOfSight(PlayerBody a, PlayerBody b)
    {
        if (a == null || b == null)
            return false;
        Vector3 origin = a.transform.position + Vector3.up * 0.7f;
        Vector3 target = b.transform.position + Vector3.up * 0.7f;
        Vector3 delta = target - origin;
        float distance = delta.magnitude;
        if (distance <= 1e-4f)
            return true;
        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            delta.normalized,
            distance + 0.05f,
            ~0,
            QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (RaycastHit hit in hits)
        {
            PlayerBody body = hit.collider.GetComponentInParent<PlayerBody>();
            if (body == a)
                continue;
            return body == b;
        }
        return false;
    }

    private sealed class AreaState
    {
        private readonly int areaId;
        private readonly MatchManager match;
        private readonly PlayerBody playerA;
        private readonly PlayerBody playerB;
        private Vector3 previousA;
        private Vector3 previousB;
        private float pathA;
        private float pathB;
        private int hitEventsA;
        private int hitEventsB;
        private int deathEventsA;
        private int deathEventsB;
        private int respawnEvents;
        private int cooldownStartEvents;
        private int cooldownEndEvents;

        public AreaState(int areaId, MatchManager match, PlayerBody playerA, PlayerBody playerB)
        {
            this.areaId = areaId;
            this.match = match;
            this.playerA = playerA;
            this.playerB = playerB;
            previousA = playerA != null ? playerA.transform.position : Vector3.zero;
            previousB = playerB != null ? playerB.transform.position : Vector3.zero;
            if (match != null)
            {
                match.OnHit += HandleHit;
                match.OnKill += HandleKill;
                match.OnRoundReset += HandleRespawn;
                match.OnMatchReset += HandleRespawn;
                match.OnCooldownStarted += HandleCooldownStarted;
                match.OnCooldownEnded += HandleCooldownEnded;
            }
        }

        private void HandleHit(PlayerIdentity shooter, PlayerIdentity victim)
        {
            if (playerA != null && shooter == playerA.Identity) hitEventsA++;
            else if (playerB != null && shooter == playerB.Identity) hitEventsB++;
        }

        private void HandleKill(PlayerIdentity killer, PlayerIdentity victim)
        {
            if (playerA != null && victim == playerA.Identity) deathEventsA++;
            else if (playerB != null && victim == playerB.Identity) deathEventsB++;
        }

        private void HandleRespawn() { respawnEvents++; }
        private void HandleCooldownStarted(float seconds) { cooldownStartEvents++; }
        private void HandleCooldownEnded() { cooldownEndEvents++; }

        public TraceRow Capture(int tick, float fixedTickWallMs)
        {
            Vector3 posA = playerA != null ? playerA.transform.position : Vector3.zero;
            Vector3 posB = playerB != null ? playerB.transform.position : Vector3.zero;
            if (Vector3.Distance(previousA, posA) < 20f)
                pathA += Vector3.Distance(previousA, posA);
            if (Vector3.Distance(previousB, posB) < 20f)
                pathB += Vector3.Distance(previousB, posB);
            previousA = posA;
            previousB = posB;

            PlayerWeapon weaponA = playerA != null ? playerA.Weapon : null;
            PlayerWeapon weaponB = playerB != null ? playerB.Weapon : null;
            PlayerIdentity identityA = playerA != null ? playerA.Identity : null;
            PlayerIdentity identityB = playerB != null ? playerB.Identity : null;
            PlayerMotor motorA = playerA != null ? playerA.Motor : null;
            PlayerMotor motorB = playerB != null ? playerB.Motor : null;
            GambitAgentController gambitA = playerA != null ? playerA.GetComponent<GambitAgentController>() : null;
            GambitAgentController gambitB = playerB != null ? playerB.GetComponent<GambitAgentController>() : null;
            Vector3 cameraA = gambitA != null && gambitA.AgentCamera != null
                ? gambitA.AgentCamera.transform.forward : Vector3.zero;
            Vector3 cameraB = gambitB != null && gambitB.AgentCamera != null
                ? gambitB.AgentCamera.transform.forward : Vector3.zero;
            Vector3 aimA = weaponA != null && weaponA.AimOrigin != null ? weaponA.AimOrigin.forward : Vector3.zero;
            Vector3 aimB = weaponB != null && weaponB.AimOrigin != null ? weaponB.AimOrigin.forward : Vector3.zero;

            TraceRow row = new TraceRow();
            row.fixed_tick = tick;
            row.area_id = areaId;
            row.side_key = "A+B";
            row.fixed_tick_wall_ms = fixedTickWallMs;
            row.sim_time = Time.fixedTime;
            row.a_x = posA.x; row.a_y = posA.y; row.a_z = posA.z;
            row.b_x = posB.x; row.b_y = posB.y; row.b_z = posB.z;
            row.path_a = pathA; row.path_b = pathB;
            row.shots_a = weaponA != null ? weaponA.TotalShotsFired : 0;
            row.shots_b = weaponB != null ? weaponB.TotalShotsFired : 0;
            row.damage_a = weaponA != null ? weaponA.TotalDamageApplied : 0f;
            row.damage_b = weaponB != null ? weaponB.TotalDamageApplied : 0f;
            row.hits_a = identityA != null ? identityA.Hits : 0;
            row.hits_b = identityB != null ? identityB.Hits : 0;
            row.hit_events_a = hitEventsA;
            row.hit_events_b = hitEventsB;
            row.death_events_a = deathEventsA;
            row.death_events_b = deathEventsB;
            row.respawn_events = respawnEvents;
            row.cooldown_start_events = cooldownStartEvents;
            row.cooldown_end_events = cooldownEndEvents;
            row.kills_a = identityA != null ? identityA.Kills : 0;
            row.kills_b = identityB != null ? identityB.Kills : 0;
            row.deaths_a = identityA != null ? identityA.Deaths : 0;
            row.deaths_b = identityB != null ? identityB.Deaths : 0;
            row.hp_a = playerA != null && playerA.Health != null ? playerA.Health.CurrentHealth : 0;
            row.hp_b = playerB != null && playerB.Health != null ? playerB.Health.CurrentHealth : 0;
            row.cooldown_a = weaponA != null ? weaponA.CooldownRemaining : 0f;
            row.cooldown_b = weaponB != null ? weaponB.CooldownRemaining : 0f;
            row.collision_a = motorA != null ? (int)motorA.LastCollisionFlags : 0;
            row.collision_b = motorB != null ? (int)motorB.LastCollisionFlags : 0;
            row.los = HasLineOfSight(playerA, playerB) ? 1 : 0;
            row.reset_generation = match != null ? match.ResetGeneration : 0;
            row.combat_ready = match != null && match.CombatReady ? 1 : 0;
            row.in_cooldown = match != null && match.IsInCooldown ? 1 : 0;
            row.camera_a_x = cameraA.x; row.camera_a_y = cameraA.y; row.camera_a_z = cameraA.z;
            row.camera_b_x = cameraB.x; row.camera_b_y = cameraB.y; row.camera_b_z = cameraB.z;
            row.aim_a_x = aimA.x; row.aim_a_y = aimA.y; row.aim_a_z = aimA.z;
            row.aim_b_x = aimB.x; row.aim_b_y = aimB.y; row.aim_b_z = aimB.z;
            return row;
        }
    }

    [Serializable]
    public sealed class TraceRow
    {
        public int fixed_tick;
        public int area_id;
        public string side_key;
        public float fixed_tick_wall_ms;
        public float sim_time;
        public float a_x, a_y, a_z, b_x, b_y, b_z;
        public float path_a, path_b;
        public int shots_a, shots_b;
        public float damage_a, damage_b;
        public int hits_a, hits_b, kills_a, kills_b, deaths_a, deaths_b;
        public int hit_events_a, hit_events_b, death_events_a, death_events_b;
        public int respawn_events, cooldown_start_events, cooldown_end_events;
        public int hp_a, hp_b;
        public float cooldown_a, cooldown_b;
        public int collision_a, collision_b, los;
        public int reset_generation, combat_ready, in_cooldown;
        public float camera_a_x, camera_a_y, camera_a_z;
        public float camera_b_x, camera_b_y, camera_b_z;
        public float aim_a_x, aim_a_y, aim_a_z;
        public float aim_b_x, aim_b_y, aim_b_z;
    }

    [Serializable]
    public sealed class RuntimeSummary
    {
        public string schema_version;
        public string status;
        public int fixed_ticks;
        public int area_count;
        public int trace_rows;
        public int reset_samples;
        public float fixed_tick_wall_ms_p50;
        public float fixed_tick_wall_ms_p95;
        public float reset_latency_ms_mean;
        public float reset_latency_ms_p50;
        public float reset_latency_ms_p95;
        public float reset_latency_ms_max;
        public HeadlessTrainingRuntime.HeadlessAudit headless_audit;
    }
}

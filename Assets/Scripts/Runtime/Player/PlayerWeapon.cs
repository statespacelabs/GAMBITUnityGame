using UnityEngine;

public enum FireBlockedReason
{
    NONE,
    NO_SHOOT_INPUT,
    WEAPON_INACTIVE,
    WEAPON_DISABLED,
    NO_OWNER,
    WRONG_OWNER,
    NO_AMMO,
    RELOADING,
    COOLDOWN,
    DEAD_OR_RESPAWNING,
    ROUND_OVER,
    GAME_PAUSED,
    UNKNOWN
}

public readonly struct FireAttemptResult
{
    public readonly bool Fired;
    public readonly FireBlockedReason Reason;

    public FireAttemptResult(bool fired, FireBlockedReason reason)
    {
        Fired = fired;
        Reason = reason;
    }
}

/// <summary>
/// Hitscan weapon system. Fires raycasts from AimOrigin forward.
///
/// TryShoot()      — fires along AimOrigin.forward (normal policy path).
/// TryShootAt(pt)  — ORACLE path: fires a perfectly-aimed ray straight at a world
///                   point (the opponent), bypassing aim/motor.
///
/// Phase 3V: SHOT_DEBUG_UNITY=1 logs true raycast geometry to SHOT_DEBUG_UNITY_PATH.
/// </summary>
public class PlayerWeapon : MonoBehaviour
{
    [Header("Aim")]
    public Transform AimOrigin;

    [Header("Configuration (set by GameModeBootstrapper from MatchConfig)")]
    public float Range = 100f;
    public float FireCooldownSeconds = 0.2f;
    public int DamagePerHit = 20;

    [Header("Hit Detection")]
    public LayerMask HitMask = ~0;

    private float lastFireTime = -999f;
    private MatchManager matchManager;
    private PlayerIdentity ownerIdentity;
    private bool debugHitProbe;
    private float damageScale = 1f;
    private float cooldownMult = 1f;
    private GambitAgentController gambitAgent;
    private LearnerShootGeometry.RayCorrectionResult lastRayCorrection;
    private int lastResetGeneration = -1;
    private bool firstShootRequestAfterReset = false;

    public int TotalShotsFired { get; private set; }
    public float TotalDamageApplied { get; private set; }
    public bool ExternalFireSuppressed { get; set; }

    // Phase 3AC debug / reset flags (env-driven).
    public static bool DebugInfiniteAmmo = false;
    public static bool DebugDisableReload = false;
    public static bool DebugForceCanFireIfCooldownReady = false;
    public static bool ForceWeaponResetOnEpisodeBegin = false;
    public static bool ForceWeaponResetOnRespawn = false;

    private void Awake()
    {
        ownerIdentity = GetComponent<PlayerIdentity>();
        ResolveGambitAgent();
        debugHitProbe = System.Environment.GetEnvironmentVariable("DEBUG_HIT_PROBE") == "1";

        if (AimOrigin == null)
        {
            GameObject aimObj = new GameObject("AimOrigin");
            aimObj.transform.SetParent(transform);
            aimObj.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            aimObj.transform.localRotation = Quaternion.identity;
            AimOrigin = aimObj.transform;
        }
    }

    private void ResolveGambitAgent()
    {
        if (gambitAgent == null)
        {
            gambitAgent = GetComponent<GambitAgentController>();
        }
    }

    public void SetMatchManager(MatchManager mm)
    {
        matchManager = mm;
    }

    public void ApplyShootPressure(float scaledDamage, float scaledCooldownMult)
    {
        damageScale = Mathf.Max(0f, scaledDamage);
        cooldownMult = Mathf.Max(0.01f, scaledCooldownMult);
    }

    /// <summary>Reset per-round / per-episode weapon fire state (Phase 3AC).</summary>
    public void ResetForRound(string reason = "episode_begin")
    {
        ResetForRound(reason, lastResetGeneration + 1);
    }

    public void ResetForRound(string reason, int generation)
    {
        if (lastResetGeneration == generation)
        {
            return;
        }

        lastResetGeneration = generation;
        gameObject.SetActive(true);
        enabled = true;
        ownerIdentity = GetComponent<PlayerIdentity>();
        ResolveGambitAgent();
        lastFireTime = -999f;
        firstShootRequestAfterReset = true;

        Debug.Assert(ownerIdentity != null, $"[PlayerWeapon] ResetForRound({reason}) has no owner identity.");
        Debug.Assert(gameObject.activeInHierarchy, $"[PlayerWeapon] ResetForRound({reason}) left weapon inactive.");
        Debug.Assert(enabled, $"[PlayerWeapon] ResetForRound({reason}) left weapon disabled.");

        if (WeaponStateDebugLogger.IsEnabled)
        {
            string area = gambitAgent != null ? gambitAgent.GetAreaKey() : "unknown";
            string agent = ownerIdentity != null ? ownerIdentity.DisplayName : "unknown";
            WeaponStateDebugLogger.LogWeaponReset(area, agent, $"{reason}:gen={generation}");
        }
    }

    public static void LoadDebugFlagsFromEnvironment()
    {
        DebugInfiniteAmmo = System.Environment.GetEnvironmentVariable("DEBUG_INFINITE_AMMO") == "1";
        DebugDisableReload = System.Environment.GetEnvironmentVariable("DEBUG_DISABLE_RELOAD") == "1";
        DebugForceCanFireIfCooldownReady =
            System.Environment.GetEnvironmentVariable("DEBUG_FORCE_CAN_FIRE_IF_COOLDOWN_READY") == "1";
        ForceWeaponResetOnEpisodeBegin =
            System.Environment.GetEnvironmentVariable("FORCE_WEAPON_RESET_ON_EPISODE_BEGIN") == "1";
        ForceWeaponResetOnRespawn =
            System.Environment.GetEnvironmentVariable("FORCE_WEAPON_RESET_ON_RESPAWN") == "1";
    }

    private float EffectiveCooldownSeconds => FireCooldownSeconds * cooldownMult;

    public PlayerIdentity OwnerIdentity => ownerIdentity;
    public string OwnerId => ownerIdentity != null ? ownerIdentity.DisplayName : "none";
    public int LastResetGeneration => lastResetGeneration;
    public float CooldownRemaining
    {
        get
        {
            if (DebugForceCanFireIfCooldownReady)
            {
                return 0f;
            }
            return Mathf.Max(0f, EffectiveCooldownSeconds - (Time.time - lastFireTime));
        }
    }
    public float CurrentAmmo => DebugInfiniteAmmo ? 999f : 1f;
    public float MaxAmmo => DebugInfiniteAmmo ? 999f : 1f;
    public bool IsReloading => false;

    private int EffectiveDamagePerHit =>
        Mathf.Max(1, Mathf.RoundToInt(DamagePerHit * damageScale));

    /// <summary> Normal fire: raycast along AimOrigin.forward. </summary>
    public void TryShoot()
    {
        FireAttemptResult result = TryFire(ownerIdentity, true);
        if (!result.Fired)
        {
            MaybeLogBlockedFire(result, true);
        }
    }

    /// <summary>
    /// Attempt to fire; returns false with blocked_fire_reason when shoot input does not produce a shot.
    /// </summary>
    public bool TryShootWithReason(out string blockedReason)
    {
        FireAttemptResult result = TryFire(ownerIdentity, true);
        blockedReason = result.Reason.ToString();
        return result.Fired;
    }

    public FireAttemptResult TryFire(PlayerIdentity requester, bool shootPressed)
    {
        FireAttemptResult result = EvaluateFireAttempt(requester, shootPressed);
        bool checkFirstShootAfterReset = ShouldCheckFirstShootAfterReset(shootPressed);
        int checkedResetGeneration = lastResetGeneration;
        if (checkFirstShootAfterReset)
        {
            firstShootRequestAfterReset = false;
        }

        if (result.Fired)
        {
            FireAfterGatesPassed();
        }

        AssertFirstShootAfterReset(requester, result, checkFirstShootAfterReset, checkedResetGeneration);
        return result;
    }

    private FireAttemptResult EvaluateFireAttempt(PlayerIdentity requester, bool shootPressed)
    {
        if (!shootPressed)
        {
            return new FireAttemptResult(false, FireBlockedReason.NO_SHOOT_INPUT);
        }

        if (!gameObject.activeInHierarchy)
        {
            return new FireAttemptResult(false, FireBlockedReason.WEAPON_INACTIVE);
        }

        if (!enabled || ExternalFireSuppressed)
        {
            return new FireAttemptResult(false, FireBlockedReason.WEAPON_DISABLED);
        }

        if (ownerIdentity == null)
        {
            ownerIdentity = GetComponent<PlayerIdentity>();
        }

        if (ownerIdentity == null || requester == null)
        {
            return new FireAttemptResult(false, FireBlockedReason.NO_OWNER);
        }

        if (ownerIdentity != requester)
        {
            return new FireAttemptResult(false, FireBlockedReason.WRONG_OWNER);
        }

        if (matchManager != null)
        {
            if (!matchManager.CombatReady)
            {
                return new FireAttemptResult(false, FireBlockedReason.GAME_PAUSED);
            }
            if (matchManager.IsMatchOver)
            {
                return new FireAttemptResult(false, FireBlockedReason.ROUND_OVER);
            }
            if (matchManager.IsInCooldown)
            {
                return new FireAttemptResult(false, FireBlockedReason.COOLDOWN);
            }
            if (!matchManager.CanPlayerShoot(ownerIdentity))
            {
                return new FireAttemptResult(false, FireBlockedReason.WEAPON_DISABLED);
            }
        }
        if (CooldownRemaining > 0f)
        {
            return new FireAttemptResult(false, FireBlockedReason.COOLDOWN);
        }

        return new FireAttemptResult(true, FireBlockedReason.NONE);
    }

    private bool ShouldCheckFirstShootAfterReset(bool shootPressed)
    {
        if (!shootPressed || !firstShootRequestAfterReset)
        {
            return false;
        }
        return matchManager == null || matchManager.CombatReady;
    }

    private void AssertFirstShootAfterReset(
        PlayerIdentity requester,
        FireAttemptResult result,
        bool shouldCheck,
        int checkedResetGeneration)
    {
        if (!shouldCheck)
        {
            return;
        }

        Debug.Assert(
            result.Fired,
            "First post-reset shot failed. " +
            $"agent={(requester != null ? requester.DisplayName : "none")}, " +
            $"generation={checkedResetGeneration}, reason={result.Reason}, " +
            $"owner={OwnerId}, ammo={CurrentAmmo}, reload={IsReloading}, " +
            $"cooldown={CooldownRemaining}, roundOver={(matchManager != null && matchManager.IsMatchOver)}");
    }

    public void MaybeLogBlockedFire(FireAttemptResult result, bool shootPressed)
    {
        MaybeLogBlockedFire(result.Reason.ToString(), shootPressed);
    }

    public void MaybeLogBlockedFire(string blockedReason, bool shootPressed)
    {
        if (!WeaponStateDebugLogger.IsEnabled || string.IsNullOrEmpty(blockedReason) || blockedReason == "NONE")
        {
            return;
        }
        ResolveGambitAgent();
        string area = gambitAgent != null ? gambitAgent.GetAreaKey() : "unknown";
        string agent = ownerIdentity != null ? ownerIdentity.DisplayName : "unknown";
        int step = gambitAgent != null ? gambitAgent.GetDecisionStepCounter() : 0;
        bool matchOver = matchManager != null && matchManager.IsMatchOver;
        bool matchCooldown = matchManager != null && matchManager.IsInCooldown;
        float weaponCooldown = CooldownRemaining;
        WeaponStateDebugLogger.LogBlockedFire(
            area,
            agent,
            System.Environment.GetEnvironmentVariable("GAME_MODE") ?? "",
            step,
            blockedReason,
            shootPressed,
            matchManager != null && matchManager.CanPlayerShoot(ownerIdentity),
            matchOver,
            matchCooldown,
            weaponCooldown,
            CurrentAmmo,
            MaxAmmo,
            IsReloading,
            enabled && gameObject.activeInHierarchy,
            LearnerShootGeometry.ShootRayMode ?? "current",
            LearnerShootGeometry.RayCorrectionAlpha);
    }

    private void FireAfterGatesPassed()
    {
        float cooldownRemaining = BeginFire();
        Vector3 dir = ComputeShootDirection().correctedDir;
        FireRay(AimOrigin.position + dir * 0.6f, dir, "POLICY", cooldownRemaining);
    }

    private float BeginFire()
    {
        float cooldownRemaining = CooldownRemaining;
        lastFireTime = Time.time;
        TotalShotsFired++;
        ResolveGambitAgent();
        if (gambitAgent != null)
        {
            gambitAgent.NotifyShotFired();
        }
        return cooldownRemaining;
    }

    private LearnerShootGeometry.RayCorrectionResult ComputeShootDirection()
    {
        ResolveGambitAgent();
        Vector3 current = AimOrigin.forward.normalized;
        lastRayCorrection = new LearnerShootGeometry.RayCorrectionResult
        {
            originalDir = current,
            correctedDir = current,
            targetChestDir = current,
            correctionAlpha = LearnerShootGeometry.RayCorrectionAlpha,
            correctionAngleAppliedDeg = 0f,
            correctionApplied = false,
        };

        if (gambitAgent == null)
        {
            return lastRayCorrection;
        }

        string mode = LearnerShootGeometry.ShootRayMode;
        if (string.IsNullOrEmpty(mode) || mode == "current")
        {
            return lastRayCorrection;
        }

        if (mode == "camera_forward" && gambitAgent.AgentCamera != null)
        {
            current = gambitAgent.AgentCamera.transform.forward.normalized;
            lastRayCorrection.originalDir = current;
            lastRayCorrection.correctedDir = current;
            return lastRayCorrection;
        }

        gambitAgent.GetShotGeometrySnapshot(
            out float _, out float _, out float _, out float _,
            out Vector3 _, out Vector3 chestPos, out Vector3 __, out Vector3 ___,
            out float ____, out int _____, out bool ______);

        lastRayCorrection = LearnerShootGeometry.ApplyRayCorrection(
            AimOrigin.position, current, chestPos, HitMask, ownerIdentity);
        return lastRayCorrection;
    }

    /// <summary> ORACLE fire at a world point. </summary>
    public void TryShootAt(Vector3 targetPoint)
    {
        FireAttemptResult result = EvaluateFireAttempt(ownerIdentity, true);
        if (!result.Fired)
            return;
        float cooldownRemaining = BeginFire();
        Vector3 dir = (targetPoint - AimOrigin.position).normalized;
        FireRay(AimOrigin.position + dir * 0.6f, dir, "ORACLE", cooldownRemaining);
    }

    private void FireRay(Vector3 origin, Vector3 dir, string ctx, float cooldownRemainingBeforeFire)
    {
        if (debugHitProbe)
        {
            RaycastHit[] all = Physics.RaycastAll(origin, dir, Range, HitMask);
            System.Array.Sort(all, (a, b) => a.distance.CompareTo(b.distance));
            string s = $"[HITPROBE] {ctx} shooter={ownerIdentity?.DisplayName} origin={origin} dir={dir} " +
                       $"range={Range} mask={HitMask.value} nhits={all.Length}";
            for (int i = 0; i < all.Length && i < 6; i++)
            {
                var h = all[i];
                var ph = h.collider.GetComponentInParent<PlayerHealth>();
                bool isOpp = ph != null && ph.GetComponent<PlayerIdentity>() != ownerIdentity;
                s += $" | [{i}] d={h.distance:F2} col={h.collider.name} layer={h.collider.gameObject.layer} " +
                     $"root={h.collider.transform.root.name} isPlayer={(ph != null)} isOpp={isOpp}";
            }
            Debug.Log(s);
        }

        float rewardBefore = gambitAgent != null ? gambitAgent.GetCumulativeReward() : 0f;
        ResolveGambitAgent();
        bool raycastHitTrue = false;
        string hitCollider = "";
        string hitBodyPart = "";
        float hitDistance = 0f;
        string missReason = "no_hit";
        float damageApplied = 0f;

        Ray ray = new Ray(origin, dir.normalized);
        float inflate = gambitAgent != null ? LearnerShootGeometry.HurtboxInflateRadius : 0f;
        bool gotHit = false;
        RaycastHit hit = default;

        if (inflate > 0f)
        {
            gotHit = Physics.SphereCast(ray, inflate, out hit, Range, HitMask);
        }
        else
        {
            gotHit = Physics.Raycast(ray, out hit, Range, HitMask);
        }

        if (gotHit)
        {
            PlayerHealth targetHealth = hit.collider.GetComponentInParent<PlayerHealth>();
            if (targetHealth != null)
            {
                PlayerIdentity targetIdentity = targetHealth.GetComponent<PlayerIdentity>();
                if (targetIdentity != null && targetIdentity != ownerIdentity)
                {
                    int dmg = EffectiveDamagePerHit;
                    raycastHitTrue = true;
                    hitCollider = hit.collider.name;
                    hitBodyPart = InferBodyPart(hit.collider, targetHealth.transform);
                    hitDistance = hit.distance;
                    missReason = "";
                    if (debugHitProbe)
                        Debug.Log($"[HITPROBE] {ctx} HIT opponent {targetIdentity.DisplayName} dist={hit.distance:F2} -> {dmg} dmg");
                    targetHealth.TakeDamage(dmg, ownerIdentity);
                    damageApplied = dmg;
                    TotalDamageApplied += dmg;
                    MaybeLogShotGeometry(origin, dir, ctx, cooldownRemainingBeforeFire,
                        raycastHitTrue, hitCollider, hitBodyPart, hitDistance, missReason,
                        damageApplied, rewardBefore);
                    return;
                }
            }
            missReason = $"blocked_by_{hit.collider.name}";
            if (debugHitProbe)
                Debug.Log($"[HITPROBE] {ctx} blocked by non-opponent collider {hit.collider.name}");
        }
        else
        {
            missReason = "no_raycast_hit";
        }

        if (matchManager != null)
            matchManager.RegisterMiss(ownerIdentity);
        if (debugHitProbe)
            Debug.Log($"[HITPROBE] {ctx} MISS (no opponent on ray)");

        MaybeLogShotGeometry(origin, dir, ctx, cooldownRemainingBeforeFire,
            raycastHitTrue, hitCollider, hitBodyPart, hitDistance, missReason,
            damageApplied, rewardBefore);
    }

    private static string InferBodyPart(Collider col, Transform root)
    {
        string name = col.name.ToLowerInvariant();
        if (name.Contains("head")) return "head";
        if (name.Contains("chest") || name.Contains("torso")) return "chest";
        if (name.Contains("leg") || name.Contains("foot")) return "legs";
        return col.name;
    }

    private void MaybeLogShotGeometry(
        Vector3 origin, Vector3 dir, string ctx, float cooldownRemainingBeforeFire,
        bool raycastHitTrue, string hitCollider, string hitBodyPart, float hitDistance,
        string missReason, float damageApplied, float rewardBefore)
    {
        ResolveGambitAgent();
        if (gambitAgent == null || !ShotGeometryLogger.ShouldLog(gambitAgent.GetAreaKey()))
        {
            return;
        }

        gambitAgent.GetShotGeometrySnapshot(out float aimErr, out float yawErr, out float pitchErr,
            out float dist, out Vector3 targetPos, out Vector3 chestPos, out Vector3 hitboxMin,
            out Vector3 hitboxMax, out float shotFiredObs, out int decisionStep, out bool aligned);

        float rewardAfter = gambitAgent.GetCumulativeReward();
        float cooldownAfter = EffectiveCooldownSeconds;

        Vector3 crosshairFwd = AimOrigin.forward;
        Vector3 camFwd = crosshairFwd;
        if (gambitAgent.AgentCamera != null)
        {
            camFwd = gambitAgent.AgentCamera.transform.forward;
        }

        Bounds hurtbox = new Bounds(
            (hitboxMin + hitboxMax) * 0.5f,
            hitboxMax - hitboxMin);
        Vector3 hurtboxCenter = hurtbox.center;
        Ray weaponRay = new Ray(
            new Vector3(origin.x, origin.y, origin.z),
            new Vector3(dir.x, dir.y, dir.z).normalized);
        Vector3 weaponDir = weaponRay.direction;
        float inflate = LearnerShootGeometry.HurtboxInflateRadius;
        float rayToHurtbox = LearnerShootGeometry.RayToBoundsMinDistance(
            weaponRay, hurtbox, inflate, out Vector3 closestOnRay, out Vector3 closestOnBox);
        Vector3 toClosest = closestOnBox - origin;
        float trueRayErr = toClosest.sqrMagnitude > 1e-8f
            ? LearnerShootGeometry.AngleBetween(weaponDir, toClosest)
            : 0f;
        Vector3 toChest = chestPos - origin;

        var rec = new ShotDebugRecord
        {
            area_id = gambitAgent.GetAreaKey(),
            agent_id = ownerIdentity != null ? ownerIdentity.DisplayName : "unknown",
            decision_step = decisionStep,
            shot_context = ctx,
            shot_origin_x = origin.x,
            shot_origin_y = origin.y,
            shot_origin_z = origin.z,
            shot_direction_x = dir.x,
            shot_direction_y = dir.y,
            shot_direction_z = dir.z,
            crosshair_forward_x = crosshairFwd.x,
            crosshair_forward_y = crosshairFwd.y,
            crosshair_forward_z = crosshairFwd.z,
            camera_forward_x = camFwd.x,
            camera_forward_y = camFwd.y,
            camera_forward_z = camFwd.z,
            aim_err_at_fire = aimErr,
            yaw_err_at_fire = yawErr,
            pitch_err_at_fire = pitchErr,
            target_distance_at_fire = dist,
            target_position_x = targetPos.x,
            target_position_y = targetPos.y,
            target_position_z = targetPos.z,
            target_chest_x = chestPos.x,
            target_chest_y = chestPos.y,
            target_chest_z = chestPos.z,
            hitbox_min_x = hitboxMin.x,
            hitbox_min_y = hitboxMin.y,
            hitbox_min_z = hitboxMin.z,
            hitbox_max_x = hitboxMax.x,
            hitbox_max_y = hitboxMax.y,
            hitbox_max_z = hitboxMax.z,
            intended_target_x = chestPos.x,
            intended_target_y = chestPos.y,
            intended_target_z = chestPos.z,
            raycast_hit_true = raycastHitTrue,
            raycast_hit_collider = hitCollider,
            raycast_hit_body_part = hitBodyPart,
            raycast_hit_distance = hitDistance,
            raycast_miss_reason = missReason,
            weapon_spread_angle = 0f,
            recoil_state = 0f,
            cooldown_remaining_before_fire = Mathf.Max(0f, cooldownRemainingBeforeFire),
            cooldown_after_fire = cooldownAfter,
            shot_fired_obs_value = shotFiredObs,
            damage_applied_same_step = damageApplied,
            env_reward_same_step = rewardAfter - rewardBefore,
            aligned_at_fire = aligned,
            target_hurtbox_inflate = inflate,
            learner_shoot_ray_mode = LearnerShootGeometry.ShootRayMode ?? "current",
            hurtbox_center_x = hurtboxCenter.x,
            hurtbox_center_y = hurtboxCenter.y,
            hurtbox_center_z = hurtboxCenter.z,
            closest_point_on_hurtbox_x = closestOnBox.x,
            closest_point_on_hurtbox_y = closestOnBox.y,
            closest_point_on_hurtbox_z = closestOnBox.z,
            ray_to_hurtbox_min_distance = rayToHurtbox,
            true_ray_hurtbox_error_deg = trueRayErr,
            angle_weapon_ray_to_target_chest = LearnerShootGeometry.AngleBetween(
                weaponDir, toChest),
            angle_weapon_ray_to_hurtbox_closest = LearnerShootGeometry.AngleBetween(
                weaponDir, closestOnBox - origin),
            angle_crosshair_to_target_chest = LearnerShootGeometry.AngleBetween(
                crosshairFwd, toChest),
            weapon_ray_vs_crosshair_angle = LearnerShootGeometry.AngleBetween(
                weaponDir, crosshairFwd),
            original_ray_direction_x = lastRayCorrection.originalDir.x,
            original_ray_direction_y = lastRayCorrection.originalDir.y,
            original_ray_direction_z = lastRayCorrection.originalDir.z,
            corrected_ray_direction_x = lastRayCorrection.correctedDir.x,
            corrected_ray_direction_y = lastRayCorrection.correctedDir.y,
            corrected_ray_direction_z = lastRayCorrection.correctedDir.z,
            target_chest_direction_x = lastRayCorrection.targetChestDir.x,
            target_chest_direction_y = lastRayCorrection.targetChestDir.y,
            target_chest_direction_z = lastRayCorrection.targetChestDir.z,
            ray_correction_alpha = lastRayCorrection.correctionAlpha,
            ray_correction_angle_applied_deg = lastRayCorrection.correctionAngleAppliedDeg,
            ray_correction_applied = lastRayCorrection.correctionApplied,
        };
        ShotGeometryLogger.LogShot(rec);
    }

    public void Reload() { }

    public void Configure(MatchConfig config)
    {
        Range = config.WeaponRange;
        FireCooldownSeconds = config.WeaponFireCooldownSeconds;
        DamagePerHit = config.DamagePerHit;
    }

    private void OnDrawGizmos()
    {
        if (AimOrigin == null) return;
        Gizmos.color = Color.red;
        Gizmos.DrawRay(AimOrigin.position, AimOrigin.forward * Range);
    }
}

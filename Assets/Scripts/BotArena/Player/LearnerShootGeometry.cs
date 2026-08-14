using System;
using UnityEngine;

/// <summary>
/// Phase 3Y/3AA learner shoot geometry: hurtbox inflation, ray mode, partial ray correction.
/// LEARNER_SHOOT_RAY_MODE: current | camera_forward | target_chest_debug | ray_lerp_to_chest
/// RAY_CORRECTION_ALPHA, RAY_CORRECTION_REQUIRE_LOS, RAY_CORRECTION_MAX_ANGLE_DEG
/// </summary>
public static class LearnerShootGeometry
{
    public static float HurtboxInflateRadius { get; private set; }
    public static string ShootRayMode { get; private set; } = "current";
    public static float RayCorrectionAlpha { get; private set; }
    public static bool RayCorrectionRequireLos { get; private set; } = true;
    public static float RayCorrectionMaxAngleDeg { get; private set; } = 30f;

    public struct RayCorrectionResult
    {
        public Vector3 originalDir;
        public Vector3 correctedDir;
        public Vector3 targetChestDir;
        public float correctionAlpha;
        public float correctionAngleAppliedDeg;
        public bool correctionApplied;
    }

    public static void LoadFromEnvironment()
    {
        HurtboxInflateRadius = 0f;
        ShootRayMode = "current";
        RayCorrectionAlpha = 0f;
        RayCorrectionRequireLos = true;
        RayCorrectionMaxAngleDeg = 30f;

        string inflateStr = Environment.GetEnvironmentVariable("TARGET_HURTBOX_INFLATE");
        if (string.IsNullOrEmpty(inflateStr))
        {
            inflateStr = Environment.GetEnvironmentVariable("LEARNER_TARGET_HURTBOX_INFLATE");
        }
        if (!string.IsNullOrEmpty(inflateStr)
            && float.TryParse(inflateStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed))
        {
            HurtboxInflateRadius = Mathf.Max(0f, parsed);
        }

        string mode = Environment.GetEnvironmentVariable("LEARNER_SHOOT_RAY_MODE");
        if (!string.IsNullOrEmpty(mode))
        {
            ShootRayMode = mode.Trim().ToLowerInvariant();
        }

        string alphaStr = Environment.GetEnvironmentVariable("RAY_CORRECTION_ALPHA");
        if (!string.IsNullOrEmpty(alphaStr)
            && float.TryParse(alphaStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float alpha))
        {
            RayCorrectionAlpha = Mathf.Clamp01(alpha);
        }

        string losStr = Environment.GetEnvironmentVariable("RAY_CORRECTION_REQUIRE_LOS");
        if (!string.IsNullOrEmpty(losStr))
        {
            RayCorrectionRequireLos = losStr == "1" || losStr.ToLowerInvariant() == "true";
        }

        string maxAngStr = Environment.GetEnvironmentVariable("RAY_CORRECTION_MAX_ANGLE_DEG");
        if (!string.IsNullOrEmpty(maxAngStr)
            && float.TryParse(maxAngStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float maxAng))
        {
            RayCorrectionMaxAngleDeg = Mathf.Max(0f, maxAng);
        }

        Debug.Log(
            $"[LearnerShootGeometry] inflate={HurtboxInflateRadius} ray_mode={ShootRayMode} "
            + $"alpha={RayCorrectionAlpha} los={RayCorrectionRequireLos} max_angle={RayCorrectionMaxAngleDeg}"
        );
    }

    /// <summary>Apply partial ray correction toward target chest.</summary>
    public static RayCorrectionResult ApplyRayCorrection(
        Vector3 origin,
        Vector3 currentDir,
        Vector3 chestPos,
        LayerMask hitMask,
        PlayerIdentity ownerIdentity)
    {
        Vector3 orig = currentDir.sqrMagnitude > 1e-8f ? currentDir.normalized : Vector3.forward;
        RayCorrectionResult result = new RayCorrectionResult
        {
            originalDir = orig,
            correctedDir = orig,
            targetChestDir = orig,
            correctionAlpha = RayCorrectionAlpha,
            correctionAngleAppliedDeg = 0f,
            correctionApplied = false,
        };

        string mode = ShootRayMode ?? "current";
        if (mode == "target_chest_debug")
        {
            Vector3 toChestFull = chestPos - origin;
            if (toChestFull.sqrMagnitude > 1e-6f)
            {
                result.targetChestDir = toChestFull.normalized;
                result.correctedDir = result.targetChestDir;
                result.correctionApplied = true;
                result.correctionAngleAppliedDeg = AngleBetween(orig, result.correctedDir);
            }
            return result;
        }

        if (mode != "ray_lerp_to_chest" || RayCorrectionAlpha <= 0f)
        {
            return result;
        }

        Vector3 toChest = chestPos - origin;
        if (toChest.sqrMagnitude < 1e-6f)
        {
            return result;
        }

        result.targetChestDir = toChest.normalized;

        if (RayCorrectionRequireLos && !HasLineOfSightToChest(origin, chestPos, hitMask, ownerIdentity))
        {
            return result;
        }

        Vector3 lerped = Vector3.Slerp(orig, result.targetChestDir, RayCorrectionAlpha);
        if (lerped.sqrMagnitude < 1e-8f)
        {
            return result;
        }
        lerped.Normalize();

        float angle = AngleBetween(orig, lerped);
        if (RayCorrectionMaxAngleDeg > 0f && angle > RayCorrectionMaxAngleDeg)
        {
            lerped = Vector3.RotateTowards(
                orig, result.targetChestDir,
                RayCorrectionMaxAngleDeg * Mathf.Deg2Rad, 0f);
            if (lerped.sqrMagnitude < 1e-8f)
            {
                return result;
            }
            lerped.Normalize();
            angle = AngleBetween(orig, lerped);
        }

        result.correctedDir = lerped;
        result.correctionApplied = angle > 0.01f;
        result.correctionAngleAppliedDeg = angle;
        return result;
    }

    private static bool HasLineOfSightToChest(
        Vector3 origin, Vector3 chestPos, LayerMask hitMask, PlayerIdentity ownerIdentity)
    {
        Vector3 toChest = chestPos - origin;
        float dist = toChest.magnitude;
        if (dist < 1e-4f)
        {
            return true;
        }
        Vector3 dir = toChest / dist;
        if (!Physics.Raycast(origin, dir, out RaycastHit hit, dist, hitMask))
        {
            return true;
        }
        PlayerHealth ph = hit.collider.GetComponentInParent<PlayerHealth>();
        if (ph != null)
        {
            PlayerIdentity id = ph.GetComponent<PlayerIdentity>();
            if (id != null && id != ownerIdentity)
            {
                return true;
            }
        }
        return false;
    }

    public static float RayToBoundsMinDistance(
        Ray ray, Bounds bounds, float inflate, out Vector3 closestOnRay, out Vector3 closestOnBox)
    {
        Bounds b = bounds;
        if (inflate > 0f)
        {
            b.Expand(inflate * 2f);
        }

        Vector3 center = b.center;
        Vector3 ext = b.extents;
        closestOnBox = center;
        closestOnRay = ray.origin;

        float bestDist = float.MaxValue;
        const int samples = 8;
        for (int ix = 0; ix <= samples; ix++)
        {
            for (int iy = 0; iy <= samples; iy++)
            {
                for (int iz = 0; iz <= samples; iz++)
                {
                    if (ix != 0 && ix != samples && iy != 0 && iy != samples && iz != 0 && iz != samples)
                    {
                        continue;
                    }
                    float fx = ix / (float)samples;
                    float fy = iy / (float)samples;
                    float fz = iz / (float)samples;
                    Vector3 pt = new Vector3(
                        center.x + (fx * 2f - 1f) * ext.x,
                        center.y + (fy * 2f - 1f) * ext.y,
                        center.z + (fz * 2f - 1f) * ext.z
                    );
                    Vector3 onRay = ClosestPointOnRay(ray, pt);
                    float d = Vector3.Distance(onRay, pt);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        closestOnRay = onRay;
                        closestOnBox = pt;
                    }
                }
            }
        }
        return bestDist;
    }

    public static Vector3 ClosestPointOnRay(Ray ray, Vector3 point)
    {
        float t = Vector3.Dot(point - ray.origin, ray.direction);
        t = Mathf.Max(0f, t);
        return ray.origin + ray.direction * t;
    }

    public static float AngleBetween(Vector3 a, Vector3 b)
    {
        if (a.sqrMagnitude < 1e-8f || b.sqrMagnitude < 1e-8f)
        {
            return 0f;
        }
        return Vector3.Angle(a.normalized, b.normalized);
    }
}

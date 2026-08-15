using System;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Teacher/critic-only builder for phase5_privileged_critic_obs_v001.
/// This component is never registered as an Agent sensor and is forbidden from
/// actor export. The actor prefix and policy-owned tactical state are copied
/// verbatim before the privileged suffix.
/// </summary>
public static class PrivilegedCriticTelemetry
{
    public const string SchemaId = "phase5_privileged_critic_obs_v001";
    public const int ActorSize = ActorObservationContract.Size;
    public const int TacticalStateSize = 132;
    public const int PrivilegedOffset = 363;
    public const int PrivilegedSize = 65;
    public const int ObservationSize = 428;

    public static void Build(PlayerBody self, float[] actorObservation, float[] tacticalState, float[] output)
    {
        if (self == null || actorObservation == null || actorObservation.Length != ActorSize)
            throw new ArgumentException("critic requires phase5_actor_obs_v001 prefix");
        if (tacticalState == null || tacticalState.Length != TacticalStateSize)
            throw new ArgumentException("critic requires phase5_tactical_state_v001");
        if (output == null || output.Length != ObservationSize)
            throw new ArgumentException("phase5_privileged_critic_obs_v001 requires 428 values");
        Array.Clear(output, 0, output.Length);
        Array.Copy(actorObservation, 0, output, 0, ActorSize);
        Array.Copy(tacticalState, 0, output, ActorSize, TacticalStateSize);

        MatchManager match = self.MatchManager;
        PlayerBody enemy = match != null && self.Identity != null ? match.GetOpponentBody(self.Identity) : null;
        if (enemy == null)
            return;
        Quaternion yaw = Quaternion.Euler(0f, self.transform.eulerAngles.y, 0f);
        Quaternion inverseYaw = Quaternion.Inverse(yaw);
        int at = PrivilegedOffset;
        WriteVector(output, at, ClampVector(inverseYaw * (enemy.transform.position - self.transform.position) / 100f));
        at += 3;
        Vector3 relativeVelocity = enemy.Motor != null ? enemy.Motor.WorldVelocity : Vector3.zero;
        relativeVelocity -= self.Motor != null ? self.Motor.WorldVelocity : Vector3.zero;
        WriteVector(output, at, ClampVector(inverseYaw * relativeVelocity / 12f));
        at += 3;
        WriteVector(output, at, ClampVector(inverseYaw * enemy.transform.forward));
        at += 3;
        output[at++] = enemy.Health != null ? enemy.Health.GetHealthNormalized() : 0f;
        output[at++] = enemy.Weapon != null && enemy.Weapon.MaxAmmo > 0f
            ? Mathf.Clamp01(enemy.Weapon.CurrentAmmo / enemy.Weapon.MaxAmmo) : 0f;
        output[at++] = enemy.Weapon != null
            ? Mathf.Clamp01(enemy.Weapon.CooldownRemaining / Mathf.Max(0.001f, enemy.Weapon.FireCooldownSeconds)) : 0f;
        output[at++] = Mathf.Clamp01(Vector3.Distance(self.transform.position, enemy.transform.position) / 100f);

        bool navEnabled = Environment.GetEnvironmentVariable("PHASE5_ENABLE_NAVMESH_ORACLE") == "1";
        float geodesic = 0f;
        float cornerCount = 0f;
        if (navEnabled)
        {
            NavMeshPath path = new NavMeshPath();
            if (NavMesh.CalculatePath(self.transform.position, enemy.transform.position, NavMesh.AllAreas, path)
                && path.status == NavMeshPathStatus.PathComplete && path.corners != null && path.corners.Length > 0)
            {
                for (int i = 1; i < path.corners.Length; i++)
                    geodesic += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                cornerCount = path.corners.Length;
                output[at] = Mathf.Clamp01(geodesic / 200f);
                output[at + 1] = 1f;
            }
        }
        at += 2;
        output[at++] = ExactLineOfSight(self, enemy) ? 1f : 0f;

        Collider[] overlaps = Physics.OverlapSphere(self.transform.position, 10f, ~0, QueryTriggerInteraction.Ignore);
        foreach (Collider collider in overlaps)
        {
            if (collider == null || collider.GetComponentInParent<PlayerBody>() != null)
                continue;
            Vector3 local = inverseYaw * (collider.bounds.center - self.transform.position);
            int sector = Mathf.FloorToInt(Mathf.Repeat(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg + 180f, 360f) / 22.5f);
            output[at + sector] = Mathf.Clamp01(output[at + sector] + 0.125f);
        }
        at += 16;
        Vector3 origin = self.transform.position + Vector3.up * 0.7f;
        for (int ray = 0; ray < 32; ray++)
        {
            Vector3 direction = yaw * (Quaternion.Euler(0f, ray * 11.25f, 0f) * Vector3.forward);
            output[at + ray] = StaticClearance(origin, direction, 100f) / 100f;
        }
        at += 32;
        output[at] = Mathf.Clamp01(cornerCount / 32f);
        AssertFinite(output);
    }

    private static bool ExactLineOfSight(PlayerBody self, PlayerBody enemy)
    {
        Vector3 origin = self.transform.position + Vector3.up * 0.7f;
        Vector3 target = enemy.transform.position + Vector3.up * 0.7f;
        Vector3 delta = target - origin;
        RaycastHit[] hits = Physics.RaycastAll(origin, delta.normalized, delta.magnitude + 0.05f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (RaycastHit hit in hits)
        {
            PlayerBody body = hit.collider != null ? hit.collider.GetComponentInParent<PlayerBody>() : null;
            if (body == self)
                continue;
            return body == enemy;
        }
        return false;
    }

    private static float StaticClearance(Vector3 origin, Vector3 direction, float maximum)
    {
        RaycastHit[] hits = Physics.RaycastAll(origin, direction.normalized, maximum, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider != null && hit.collider.GetComponentInParent<PlayerBody>() == null)
                return hit.distance;
        }
        return maximum;
    }

    private static Vector3 ClampVector(Vector3 value) => new Vector3(
        Mathf.Clamp(value.x, -1f, 1f), Mathf.Clamp(value.y, -1f, 1f), Mathf.Clamp(value.z, -1f, 1f));

    private static void WriteVector(float[] output, int at, Vector3 value)
    {
        output[at] = value.x; output[at + 1] = value.y; output[at + 2] = value.z;
    }

    private static void AssertFinite(float[] values)
    {
        for (int i = 0; i < values.Length; i++)
            if (float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                throw new InvalidOperationException("phase5_privileged_critic_obs_v001 non-finite value at index " + i);
    }
}

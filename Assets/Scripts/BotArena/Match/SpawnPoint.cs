using UnityEngine;

/// <summary>
/// Marks a spawn position and facing direction in the arena.
/// Used by MatchManager for initial placement and post-kill position resets.
/// </summary>
public class SpawnPoint : MonoBehaviour
{
    [Header("Spawn Configuration")]
    [Tooltip("Which player index this spawn point is for (0 = Player A, 1 = Player B)")]
    public int PlayerIndex = 0;

    private const float AreaSpacing = 500f;
    private int rotationRequestCount = 0;

    public Vector3 GetSpawnPosition()
    {
        return transform.position;
    }

    public Quaternion GetSpawnRotation()
    {
        float jitter = SampleSpawnYawJitter();
        return transform.rotation * Quaternion.Euler(0f, jitter, 0f);
    }

    // PHASE3V2_C_V23_SPAWN_YAW_JITTER: evaluator-only independent spawn-yaw perturbation.
    private float SampleSpawnYawJitter()
    {
        string maxEnv = System.Environment.GetEnvironmentVariable("SPAWN_YAW_JITTER_DEG");
        if (string.IsNullOrEmpty(maxEnv))
            return 0f;
        if (!float.TryParse(maxEnv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float maxAbsDeg))
            return 0f;
        maxAbsDeg = Mathf.Abs(maxAbsDeg);
        if (maxAbsDeg <= 0f)
            return 0f;

        int baseSeed = 0;
        string seedEnv = System.Environment.GetEnvironmentVariable("SPAWN_YAW_JITTER_SEED");
        if (!string.IsNullOrEmpty(seedEnv))
            int.TryParse(seedEnv, out baseSeed);

        int generation = rotationRequestCount++;
        int areaId = Mathf.Max(0, Mathf.RoundToInt(transform.position.x / AreaSpacing));
        unchecked
        {
            int seed = baseSeed;
            seed = seed * 16777619 + areaId * 73856093;
            seed = seed * 16777619 + PlayerIndex * 19349663;
            seed = seed * 16777619 + generation * 83492791;
            System.Random rng = new System.Random(seed);
            float unit = (float)(rng.NextDouble() * 2.0 - 1.0);
            float jitter = unit * maxAbsDeg;
            Debug.Log($"[SpawnPoint] PHASE3V2_C_V23_SPAWN_YAW_JITTER area={areaId} player={PlayerIndex} generation={generation} jitterDeg={jitter:F3}");
            return jitter;
        }
    }

    /// <summary>
    /// Draws a visual gizmo in the scene editor to show spawn position and facing direction.
    /// </summary>
    private void OnDrawGizmos()
    {
        Gizmos.color = PlayerIndex == 0 ? Color.blue : Color.red;
        Gizmos.DrawWireSphere(transform.position, 0.5f);
        Gizmos.DrawRay(transform.position, transform.forward * 2f);

        // Draw a small label
#if UNITY_EDITOR
        UnityEditor.Handles.Label(transform.position + Vector3.up * 1.5f,
            $"Spawn {(PlayerIndex == 0 ? "A" : "B")}");
#endif
    }
}

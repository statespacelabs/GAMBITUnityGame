using System;
using System.Collections;
using System.IO;
using UnityEngine;

/// <summary>
/// Runtime leakage probe for the actor-observation sensor.
/// Enabled only by PHASE5_TELEMETRY_LEAKAGE_PROBE=1.
/// </summary>
[UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, null, "Phase5TelemetryLeakageProbe")]
public sealed class TelemetryLeakageProbe : MonoBehaviour
{
    [Serializable]
    private sealed class ProbeResult
    {
        public string schema_version = "phase5_telemetry_leakage_probe_v001";
        public string status;
        public bool hidden_enemy_observation_unchanged;
        public bool hidden_enemy_los_false_before;
        public bool hidden_enemy_los_false_after;
        public float hidden_enemy_max_abs_error;
        public bool rigid_transform_consistent;
        public float rigid_transform_max_abs_error;
        public bool actor_dimension_valid;
        public int actor_dimension;
        public string failure_reason;
    }

    public static void MaybeInstall()
    {
        if (Environment.GetEnvironmentVariable("PHASE5_TELEMETRY_LEAKAGE_PROBE") != "1")
            return;
        if (UnityEngine.Object.FindObjectOfType<TelemetryLeakageProbe>() != null)
            return;
        new GameObject("_TelemetryLeakageProbe").AddComponent<TelemetryLeakageProbe>();
    }

    private IEnumerator Start()
    {
        yield return null;
        yield return new WaitForFixedUpdate();
        ProbeResult result = RunProbe();
        string path = Environment.GetEnvironmentVariable("PHASE5_TELEMETRY_LEAKAGE_PATH") ?? "";
        if (!string.IsNullOrWhiteSpace(path))
        {
            string parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(path, JsonUtility.ToJson(result, true) + "\n");
        }
        Debug.Log("[TelemetryLeakageProbe] " + JsonUtility.ToJson(result));
    }

    private ProbeResult RunProbe()
    {
        ProbeResult result = new ProbeResult();
        GameObject root = null;
        MapIndependentTelemetry telemetry = null;
        PlayerBody self = null;
        PlayerBody enemy = null;
        Vector3 originalSelfPosition = Vector3.zero;
        Quaternion originalSelfRotation = Quaternion.identity;
        Vector3 originalEnemyPosition = Vector3.zero;
        Quaternion originalEnemyRotation = Quaternion.identity;
        try
        {
            MapIndependentTelemetry[] sensors = UnityEngine.Object.FindObjectsOfType<MapIndependentTelemetry>(true);
            if (sensors.Length == 0)
                throw new InvalidOperationException("no phase5_actor_obs_v001 sensor found");
            telemetry = sensors[0];
            self = telemetry.SelfBody;
            enemy = telemetry.OpponentBody;
            if (self == null || enemy == null)
                throw new InvalidOperationException("probe sensor has no player pair");
            originalSelfPosition = self.transform.position;
            originalSelfRotation = self.transform.rotation;
            originalEnemyPosition = enemy.transform.position;
            originalEnemyRotation = enemy.transform.rotation;

            root = new GameObject("_TelemetryLeakageGeometry");
            GameObject floor = CreateBox(root.transform, "Floor", new Vector3(0f, 998.8f, 0f), new Vector3(80f, 0.2f, 80f));
            GameObject wall = CreateBox(root.transform, "Occluder", new Vector3(0f, 1001f, 4f), new Vector3(20f, 4f, 0.4f));
            CreateBox(root.transform, "ObstacleLeft", new Vector3(-5f, 1001f, 0f), new Vector3(1f, 3f, 4f));
            CreateBox(root.transform, "ObstacleRight", new Vector3(6f, 1001f, -2f), new Vector3(2f, 3f, 2f));

            Teleport(self, new Vector3(0f, 1000f, 0f), Quaternion.identity);
            Teleport(enemy, new Vector3(0f, 1000f, 8f), Quaternion.Euler(0f, 180f, 0f));
            Physics.SyncTransforms();
            telemetry.ResetMemory();
            float[] hiddenBefore = new float[ActorObservationContract.Size];
            float[] hiddenAfter = new float[ActorObservationContract.Size];
            telemetry.BuildObservation(hiddenBefore, false);
            Teleport(enemy, new Vector3(3f, 1000f, 12f), Quaternion.Euler(0f, 180f, 0f));
            Physics.SyncTransforms();
            telemetry.BuildObservation(hiddenAfter, false);
            result.actor_dimension = hiddenBefore.Length;
            result.actor_dimension_valid = hiddenBefore.Length == ActorObservationContract.Size;
            result.hidden_enemy_los_false_before = hiddenBefore[ActorObservationContract.VisibleEnemyState] == 0f;
            result.hidden_enemy_los_false_after = hiddenAfter[ActorObservationContract.VisibleEnemyState] == 0f;
            result.hidden_enemy_max_abs_error = MaxAbs(hiddenBefore, hiddenAfter);
            result.hidden_enemy_observation_unchanged = result.hidden_enemy_los_false_before
                && result.hidden_enemy_los_false_after && result.hidden_enemy_max_abs_error <= 1e-6f;

            Teleport(enemy, new Vector3(0f, 1000f, 8f), Quaternion.Euler(0f, 180f, 0f));
            Physics.SyncTransforms();
            telemetry.ResetMemory();
            float[] rigidBefore = new float[ActorObservationContract.Size];
            telemetry.BuildObservation(rigidBefore, false);

            float yawDegrees = 73f;
            Quaternion rotation = Quaternion.Euler(0f, yawDegrees, 0f);
            Vector3 translation = new Vector3(250f, 0f, -400f);
            foreach (Transform child in root.transform)
            {
                child.position = rotation * child.position + translation;
                child.rotation = rotation * child.rotation;
            }
            Teleport(self, rotation * new Vector3(0f, 1000f, 0f) + translation, rotation * Quaternion.identity);
            Teleport(enemy, rotation * new Vector3(0f, 1000f, 8f) + translation,
                rotation * Quaternion.Euler(0f, 180f, 0f));
            Physics.SyncTransforms();
            telemetry.ResetMemory();
            float[] rigidAfter = new float[ActorObservationContract.Size];
            telemetry.BuildObservation(rigidAfter, false);
            result.rigid_transform_max_abs_error = MaxAbs(rigidBefore, rigidAfter);
            result.rigid_transform_consistent = result.rigid_transform_max_abs_error <= 2e-5f;
            result.status = result.actor_dimension_valid
                && result.hidden_enemy_observation_unchanged
                && result.rigid_transform_consistent ? "PASS" : "FAIL";
        }
        catch (Exception ex)
        {
            result.status = "FAIL";
            result.failure_reason = ex.ToString();
        }
        finally
        {
            if (self != null)
                Teleport(self, originalSelfPosition, originalSelfRotation);
            if (enemy != null)
                Teleport(enemy, originalEnemyPosition, originalEnemyRotation);
            Physics.SyncTransforms();
            if (telemetry != null)
                telemetry.ResetMemory();
            if (root != null)
                Destroy(root);
        }
        return result;
    }

    private static GameObject CreateBox(Transform parent, string name, Vector3 position, Vector3 scale)
    {
        GameObject box = new GameObject(name);
        box.transform.SetParent(parent);
        box.transform.position = position;
        box.transform.localScale = scale;
        box.AddComponent<BoxCollider>();
        return box;
    }

    private static void Teleport(PlayerBody body, Vector3 position, Quaternion rotation)
    {
        if (body != null && body.Motor != null)
            body.Motor.TeleportTo(position, rotation);
    }

    private static float MaxAbs(float[] left, float[] right)
    {
        float maximum = 0f;
        int count = Mathf.Min(left.Length, right.Length);
        for (int i = 0; i < count; i++)
            maximum = Mathf.Max(maximum, Mathf.Abs(left[i] - right[i]));
        return maximum;
    }
}

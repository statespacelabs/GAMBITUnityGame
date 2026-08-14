using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

public enum GambitReplayCamera
{
    Player,
    Observer,
    TopDown
}

/// <summary>
/// Deterministically replays a known trajectory in GAMBIT map coordinates.
/// Replay ghosts have no colliders and cannot affect gameplay or spawn queries.
/// </summary>
public sealed class GambitTrajectoryReplay : MonoBehaviour
{
    private GambitTrajectorySession session;
    private readonly Dictionary<int, GameObject> activeTargets = new Dictionary<int, GameObject>();
    private GameObject playerGhost;
    private Camera replayCamera;
    private GambitReplayCamera cameraMode;
    private Renderer[] playerRenderers;
    private float playbackSpeed = 1f;
    private float currentTime;
    private float duration;
    private bool ready;
    private bool playing;

    public bool IsReady => ready;
    public bool IsPlaying => playing;
    public float CurrentTime => currentTime;
    public float Duration => duration;
    public Camera ReplayCamera => replayCamera;
    public GambitTrajectorySession Session => session;
    public Vector3 PlayerPosition => playerGhost != null ? playerGhost.transform.position : Vector3.zero;
    public Quaternion PlayerRotation => playerGhost != null ? playerGhost.transform.rotation : Quaternion.identity;

    public void Initialize(string json, Camera camera, GambitReplayCamera selectedCamera, float speed = 1f)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Trajectory JSON is empty.", nameof(json));
        session = JsonConvert.DeserializeObject<GambitTrajectorySession>(json);
        Validate(session);
        replayCamera = camera != null ? camera : CreateCamera();
        cameraMode = selectedCamera;
        playbackSpeed = Mathf.Max(0.01f, speed);
        duration = session.player.position.key[session.player.position.key.Count - 1];
        playerGhost = CreateGhost("Trajectory_Player", new Color(0.15f, 0.65f, 1f));
        playerRenderers = playerGhost.GetComponentsInChildren<Renderer>(true);
        ConfigureCamera();
        ApplyTime(0f);
        ready = true;
        playing = true;
        Debug.Log($"[GambitReplay] ready duration={duration:F3}s targets={session.targets.Count} camera={cameraMode}");
    }

    private void Update()
    {
        if (!ready || !playing)
            return;
        ApplyTime(Mathf.Min(duration, currentTime + Time.deltaTime * playbackSpeed));
        if (currentTime >= duration)
        {
            playing = false;
            Debug.Log("[GambitReplay] playback complete");
        }
        if (Input.GetKeyDown(KeyCode.Space))
        {
            cameraMode = (GambitReplayCamera)(((int)cameraMode + 1) % 3);
            ConfigureCamera();
        }
    }

    public void SetPlaying(bool value)
    {
        playing = ready && value;
    }

    public void ApplyTime(float time)
    {
        if (session == null || playerGhost == null)
            return;
        currentTime = Mathf.Clamp(time, 0f, duration);
        playerGhost.transform.position = InterpolatePosition(session.player.position, currentTime);
        playerGhost.transform.rotation = InterpolateRotation(session.player.rotation, currentTime);
        if (session.player.fov != null && session.player.fov.key != null && session.player.fov.key.Count > 0)
            replayCamera.fieldOfView = Mathf.Clamp(InterpolateFloat(session.player.fov, currentTime), 20f, 140f);
        UpdateTargets(currentTime);
    }

    public List<GambitTrajectoryEvent> GetEvents(float startExclusive, float endInclusive)
    {
        List<GambitTrajectoryEvent> result = new List<GambitTrajectoryEvent>();
        if (session == null || session.events == null)
            return result;
        foreach (GambitTrajectoryEvent item in session.events)
            if (item.time > startExclusive && item.time <= endInclusive)
                result.Add(item);
        return result;
    }

    public GameObject GetClosestActiveTarget(Vector3 position)
    {
        GameObject closest = null;
        float distance = float.PositiveInfinity;
        foreach (GameObject target in activeTargets.Values)
        {
            if (target == null)
                continue;
            float candidate = Vector3.SqrMagnitude(target.transform.position - position);
            if (candidate < distance)
            {
                distance = candidate;
                closest = target;
            }
        }
        return closest;
    }

    private void UpdateTargets(float time)
    {
        foreach (GambitTrajectoryTarget target in session.targets)
        {
            bool alive = time >= target.spawnTime && (target.destroyTime <= 0f || time <= target.destroyTime);
            if (!alive)
            {
                if (activeTargets.TryGetValue(target.id, out GameObject oldTarget))
                {
                    Destroy(oldTarget);
                    activeTargets.Remove(target.id);
                }
                continue;
            }
            if (!activeTargets.TryGetValue(target.id, out GameObject ghost) || ghost == null)
            {
                float hue = Mathf.Repeat(target.id * 0.173f, 1f);
                ghost = CreateGhost("Trajectory_Target_" + target.id, Color.HSVToRGB(hue, 0.75f, 1f));
                activeTargets[target.id] = ghost;
            }
            ghost.transform.position = InterpolatePosition(target.position, time);
            ghost.transform.rotation = InterpolateRotation(target.rotation, time);
            if (target.scaling != null && target.scaling.key != null && target.scaling.key.Count > 0)
                ghost.transform.localScale = InterpolatePosition(target.scaling, time);
        }
    }

    private void ConfigureCamera()
    {
        if (replayCamera == null || playerGhost == null)
            return;
        bool playerView = cameraMode == GambitReplayCamera.Player;
        foreach (Renderer item in playerRenderers)
            if (item != null) item.enabled = !playerView;
        if (playerView)
        {
            replayCamera.transform.SetParent(playerGhost.transform, false);
            replayCamera.transform.localPosition = new Vector3(0f, 0.72f, 0f);
            replayCamera.transform.localRotation = Quaternion.identity;
            return;
        }
        replayCamera.transform.SetParent(null, true);
        Bounds bounds = CalculateTrajectoryBounds();
        float radius = Mathf.Max(12f, Mathf.Max(bounds.extents.x, bounds.extents.z));
        if (cameraMode == GambitReplayCamera.TopDown)
        {
            replayCamera.transform.position = bounds.center + Vector3.up * Mathf.Max(20f, radius * 1.8f);
            replayCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
        else
        {
            replayCamera.transform.position = bounds.center + new Vector3(radius * 0.9f, radius * 0.75f, -radius * 1.15f);
            replayCamera.transform.LookAt(bounds.center);
        }
    }

    private Bounds CalculateTrajectoryBounds()
    {
        Vector3 first = ToVector3(session.player.position.value[0]);
        Bounds bounds = new Bounds(first, Vector3.one);
        foreach (GambitTrajectoryVector3 value in session.player.position.value)
            bounds.Encapsulate(ToVector3(value));
        foreach (GambitTrajectoryTarget target in session.targets)
            if (target.position != null && target.position.value != null)
                foreach (GambitTrajectoryVector3 value in target.position.value)
                    bounds.Encapsulate(ToVector3(value));
        return bounds;
    }

    private static GameObject CreateGhost(string name, Color color)
    {
        GameObject ghost = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        ghost.name = name;
        ghost.layer = LayerMask.NameToLayer("Ignore Raycast");
        Collider collider = ghost.GetComponent<Collider>();
        if (collider != null) Destroy(collider);
        Renderer renderer = ghost.GetComponent<Renderer>();
        if (renderer != null)
        {
            Shader shader = Shader.Find("Standard");
            if (shader != null)
            {
                Material material = new Material(shader) { color = color };
                renderer.material = material;
            }
        }
        return ghost;
    }

    private static Camera CreateCamera()
    {
        GameObject cameraObject = new GameObject("GAMBIT Replay Camera");
        cameraObject.tag = "MainCamera";
        return cameraObject.AddComponent<Camera>();
    }

    private static Vector3 InterpolatePosition(GambitTrajectorySeries<GambitTrajectoryVector3> series, float time)
    {
        int index = Segment(series.key, time);
        if (index >= series.key.Count - 1)
            return ToVector3(series.value[series.value.Count - 1]);
        float span = series.key[index + 1] - series.key[index];
        float t = span > 0f ? (time - series.key[index]) / span : 0f;
        return Vector3.Lerp(ToVector3(series.value[index]), ToVector3(series.value[index + 1]), Mathf.Clamp01(t));
    }

    private static Quaternion InterpolateRotation(GambitTrajectorySeries<GambitTrajectoryVector3> series, float time)
    {
        int index = Segment(series.key, time);
        GambitTrajectoryVector3 a = series.value[index];
        if (index >= series.key.Count - 1)
            return Quaternion.Euler(a.x, a.y, a.z);
        GambitTrajectoryVector3 b = series.value[index + 1];
        float span = series.key[index + 1] - series.key[index];
        float t = span > 0f ? Mathf.Clamp01((time - series.key[index]) / span) : 0f;
        return Quaternion.Euler(Mathf.LerpAngle(a.x, b.x, t), Mathf.LerpAngle(a.y, b.y, t), Mathf.LerpAngle(a.z, b.z, t));
    }

    private static float InterpolateFloat(GambitTrajectorySeries<float> series, float time)
    {
        int index = Segment(series.key, time);
        if (index >= series.key.Count - 1)
            return series.value[series.value.Count - 1];
        float span = series.key[index + 1] - series.key[index];
        float t = span > 0f ? Mathf.Clamp01((time - series.key[index]) / span) : 0f;
        return Mathf.Lerp(series.value[index], series.value[index + 1], t);
    }

    private static int Segment(List<float> keys, float time)
    {
        if (time <= keys[0]) return 0;
        int low = 0;
        int high = keys.Count - 1;
        while (low + 1 < high)
        {
            int middle = (low + high) / 2;
            if (keys[middle] <= time) low = middle;
            else high = middle;
        }
        return low;
    }

    private static Vector3 ToVector3(GambitTrajectoryVector3 value)
    {
        return value != null ? new Vector3(value.x, value.y, value.z) : Vector3.zero;
    }

    private static void Validate(GambitTrajectorySession value)
    {
        if (value == null || value.player == null)
            throw new InvalidOperationException("Trajectory is missing player data.");
        ValidateSeries(value.player.position, "player.position");
        ValidateSeries(value.player.rotation, "player.rotation");
        if (value.targets == null) value.targets = new List<GambitTrajectoryTarget>();
        if (value.events == null) value.events = new List<GambitTrajectoryEvent>();
        foreach (GambitTrajectoryTarget target in value.targets)
        {
            ValidateSeries(target.position, "target " + target.id + ".position");
            ValidateSeries(target.rotation, "target " + target.id + ".rotation");
        }
    }

    private static void ValidateSeries<T>(GambitTrajectorySeries<T> series, string name)
    {
        if (series == null || series.key == null || series.value == null || series.key.Count == 0
            || series.key.Count != series.value.Count)
            throw new InvalidOperationException("Invalid trajectory series: " + name);
        for (int index = 1; index < series.key.Count; index++)
            if (series.key[index] < series.key[index - 1])
                throw new InvalidOperationException("Trajectory timestamps are not sorted: " + name);
    }
}

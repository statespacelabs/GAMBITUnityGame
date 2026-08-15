using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Media;
#endif

/// <summary>Renders a trajectory deterministically to WebM plus telemetry and a provenance manifest.</summary>
public sealed class GambitTrajectoryExporter : MonoBehaviour
{
    [Serializable]
    private sealed class RenderFrame
    {
        public int frame;
        public float time;
        public Vector3 position;
        public Vector3 viewing_angle;
        public float distance_to_nearest_target;
        public List<GambitTrajectoryEvent> events = new List<GambitTrajectoryEvent>();
    }

    [Serializable]
    private sealed class RenderTelemetry
    {
        public string schema_version = "gambit_trajectory_telemetry_v1";
        public List<RenderFrame> frames = new List<RenderFrame>();
    }

    private GambitTrajectoryReplay replay;
    private GambitReplayRequest request;
    private string trajectoryPath;

    public void Configure(GambitTrajectoryReplay configuredReplay, GambitReplayRequest configuredRequest, string absoluteTrajectoryPath)
    {
        replay = configuredReplay;
        request = configuredRequest;
        trajectoryPath = absoluteTrajectoryPath;
    }

    private IEnumerator Start()
    {
        yield return null;
        if (replay == null || request == null || !replay.IsReady)
        {
            Debug.LogError("[GambitReplay] exporter was not configured");
            yield break;
        }
#if UNITY_EDITOR
        yield return Export();
#else
        Debug.LogError("[GambitReplay] WebM export requires the Unity Editor; standalone builds support preview only.");
#endif
    }

#if UNITY_EDITOR
    private IEnumerator Export()
    {
        replay.SetPlaying(false);
        string root = ResolveOutputDirectory();
        Directory.CreateDirectory(root);
        string videoPath = Path.Combine(root, "trajectory.webm");
        string telemetryPath = Path.Combine(root, "telemetry.json");
        string manifestPath = Path.Combine(root, "manifest.json");
        float timeStep = 1f / request.FrameRate;
        int frameCount = Mathf.FloorToInt(replay.Duration * request.FrameRate) + 1;
        RenderTelemetry telemetry = new RenderTelemetry();
        RenderTexture renderTexture = new RenderTexture(request.Width, request.Height, 24, RenderTextureFormat.ARGB32);
        Texture2D texture = new Texture2D(request.Width, request.Height, TextureFormat.RGBA32, false);
        Camera camera = replay.ReplayCamera;
        RenderTexture previousTarget = camera.targetTexture;
        RenderTexture previousActive = RenderTexture.active;
        camera.targetTexture = renderTexture;
        VideoTrackAttributes attributes = new VideoTrackAttributes
        {
            frameRate = new MediaRational(request.FrameRate, 1),
            width = (uint)request.Width,
            height = (uint)request.Height,
            includeAlpha = false
        };

        Debug.Log($"[GambitReplay] export started frames={frameCount} output={root}");
        try
        {
            using (MediaEncoder encoder = new MediaEncoder(videoPath, attributes))
            {
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float time = Mathf.Min(replay.Duration, frame * timeStep);
                    replay.ApplyTime(time);
                    Physics.SyncTransforms();
                    camera.Render();
                    RenderTexture.active = renderTexture;
                    texture.ReadPixels(new Rect(0f, 0f, request.Width, request.Height), 0, 0, false);
                    texture.Apply(false, false);
                    encoder.AddFrame(texture);

                    GameObject nearest = replay.GetClosestActiveTarget(replay.PlayerPosition);
                    telemetry.frames.Add(new RenderFrame
                    {
                        frame = frame,
                        time = time,
                        position = replay.PlayerPosition,
                        viewing_angle = replay.PlayerRotation.eulerAngles,
                        distance_to_nearest_target = nearest != null
                            ? Vector3.Distance(replay.PlayerPosition, nearest.transform.position) : -1f,
                        events = replay.GetEvents(time - timeStep, time)
                    });
                    if (!Application.isBatchMode && frame % 4 == 0)
                        yield return null;
                }
            }

            File.WriteAllText(telemetryPath, JsonUtility.ToJson(telemetry, true));
            GambitTrajectoryRenderManifest manifest = new GambitTrajectoryRenderManifest
            {
                trajectory_path = trajectoryPath,
                trajectory_sha256 = Sha256File(trajectoryPath),
                map_id = DemoMapRuntime.ActiveMapId,
                map_asset_sha256 = DemoMapRuntime.ActiveAssetSha256,
                camera = request.Camera.ToString(),
                video_file = Path.GetFileName(videoPath),
                width = request.Width,
                height = request.Height,
                frame_rate = request.FrameRate,
                frame_count = frameCount,
                duration_seconds = replay.Duration
            };
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
            Debug.Log($"[GambitReplay] export complete video={videoPath}");
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            renderTexture.Release();
            Destroy(renderTexture);
            Destroy(texture);
        }

        if (Application.isBatchMode)
            EditorApplication.Exit(0);
        else
            EditorApplication.isPlaying = false;
    }

    private string ResolveOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(request.OutputDirectory))
            return Path.GetFullPath(request.OutputDirectory);
        string parent = Path.GetDirectoryName(trajectoryPath) ?? Application.persistentDataPath;
        return Path.Combine(parent, "GambitRenders", Path.GetFileNameWithoutExtension(trajectoryPath));
    }

    private static string Sha256File(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
#endif
}

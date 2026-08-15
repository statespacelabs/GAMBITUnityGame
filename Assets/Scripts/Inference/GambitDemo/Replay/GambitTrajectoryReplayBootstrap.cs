using System;
using System.Globalization;
using System.IO;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

public sealed class GambitReplayRequest
{
    public string TrajectoryPath;
    public string MapId = DemoMapRuntime.DefaultMapId;
    public GambitReplayCamera Camera = GambitReplayCamera.Player;
    public string OutputDirectory = "";
    public int FrameRate = 30;
    public int Width = 640;
    public int Height = 480;
    public float PlaybackSpeed = 1f;
    public bool Export;
}

/// <summary>Explicit replay-mode entry point for editor preview and command-line rendering.</summary>
[DefaultExecutionOrder(-11000)]
public sealed class GambitTrajectoryReplayBootstrap : MonoBehaviour
{
    private const string EditorPrefix = "GambitReplay.";
    private GambitReplayRequest request;

    public static bool IsReplayRequested => TryReadRequest(false, out _);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (!TryReadRequest(true, out GambitReplayRequest replayRequest))
            return;
        GameObject root = new GameObject("GAMBIT Trajectory Replay");
        GambitTrajectoryReplayBootstrap bootstrap = root.AddComponent<GambitTrajectoryReplayBootstrap>();
        bootstrap.request = replayRequest;
    }

    private void Awake()
    {
        foreach (GameModeBootstrapper bootstrapper in FindObjectsOfType<GameModeBootstrapper>())
            bootstrapper.enabled = false;
        GambitDemoStartup startup = FindObjectOfType<GambitDemoStartup>();
        if (startup != null) Destroy(startup.gameObject);
    }

    private void Start()
    {
        try
        {
            if (request == null || string.IsNullOrWhiteSpace(request.TrajectoryPath))
                throw new InvalidOperationException("Replay trajectory path is missing.");
            string absolutePath = Path.GetFullPath(request.TrajectoryPath);
            if (!File.Exists(absolutePath))
                throw new FileNotFoundException("Trajectory JSON was not found.", absolutePath);

            DemoMapRuntime.ConfigureForDemo(request.MapId);
            if (!DemoMapRuntime.Initialize(1))
                throw new InvalidOperationException("GAMBIT map initialization failed for " + request.MapId);

            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject cameraObject = new GameObject("GAMBIT Replay Camera");
                cameraObject.tag = "MainCamera";
                camera = cameraObject.AddComponent<Camera>();
            }
            camera.nearClipPlane = 0.05f;

            GambitTrajectoryReplay replay = gameObject.AddComponent<GambitTrajectoryReplay>();
            replay.Initialize(File.ReadAllText(absolutePath), camera, request.Camera, request.PlaybackSpeed);

            if (request.Export)
            {
                GambitTrajectoryExporter exporter = gameObject.AddComponent<GambitTrajectoryExporter>();
                exporter.Configure(replay, request, absolutePath);
            }
            Debug.Log($"[GambitReplay] mode active map={request.MapId} trajectory={absolutePath} export={request.Export}");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            Debug.LogError("[GambitReplay] startup failed");
#if UNITY_EDITOR
            if (Application.isBatchMode)
                EditorApplication.ExitPlaymode();
#endif
        }
    }

    public static bool TryReadRequest(bool consumeEditorRequest, out GambitReplayRequest request)
    {
        request = ReadCommandLineRequest();
        if (request != null)
            return true;
#if UNITY_EDITOR
        if (!EditorPrefs.GetBool(EditorPrefix + "Pending", false))
            return false;
        request = new GambitReplayRequest
        {
            TrajectoryPath = EditorPrefs.GetString(EditorPrefix + "TrajectoryPath", ""),
            MapId = EditorPrefs.GetString(EditorPrefix + "MapId", DemoMapRuntime.DefaultMapId),
            Camera = (GambitReplayCamera)EditorPrefs.GetInt(EditorPrefix + "Camera", 0),
            OutputDirectory = EditorPrefs.GetString(EditorPrefix + "OutputDirectory", ""),
            FrameRate = EditorPrefs.GetInt(EditorPrefix + "FrameRate", 30),
            Width = EditorPrefs.GetInt(EditorPrefix + "Width", 640),
            Height = EditorPrefs.GetInt(EditorPrefix + "Height", 480),
            PlaybackSpeed = EditorPrefs.GetFloat(EditorPrefix + "PlaybackSpeed", 1f),
            Export = EditorPrefs.GetBool(EditorPrefix + "Export", false)
        };
        if (consumeEditorRequest)
            EditorPrefs.SetBool(EditorPrefix + "Pending", false);
        return !string.IsNullOrWhiteSpace(request.TrajectoryPath);
#else
        return false;
#endif
    }

#if UNITY_EDITOR
    public static void SetEditorRequest(GambitReplayRequest request)
    {
        EditorPrefs.SetString(EditorPrefix + "TrajectoryPath", request.TrajectoryPath ?? "");
        EditorPrefs.SetString(EditorPrefix + "MapId", request.MapId ?? DemoMapRuntime.DefaultMapId);
        EditorPrefs.SetInt(EditorPrefix + "Camera", (int)request.Camera);
        EditorPrefs.SetString(EditorPrefix + "OutputDirectory", request.OutputDirectory ?? "");
        EditorPrefs.SetInt(EditorPrefix + "FrameRate", request.FrameRate);
        EditorPrefs.SetInt(EditorPrefix + "Width", request.Width);
        EditorPrefs.SetInt(EditorPrefix + "Height", request.Height);
        EditorPrefs.SetFloat(EditorPrefix + "PlaybackSpeed", request.PlaybackSpeed);
        EditorPrefs.SetBool(EditorPrefix + "Export", request.Export);
        EditorPrefs.SetBool(EditorPrefix + "Pending", true);
    }

    public static void ExecuteReplay()
    {
        if (ReadCommandLineRequest() == null)
            throw new InvalidOperationException("Use --gambit-replay <trajectory.json> with ExecuteReplay.");
        const string replayScene = "Assets/Scenes/BotArena.unity";
        if (!File.Exists(Path.GetFullPath(replayScene)))
            throw new FileNotFoundException("GAMBIT replay scene was not found.", replayScene);
        EditorSceneManager.OpenScene(replayScene, OpenSceneMode.Single);
        EditorApplication.isPlaying = true;
    }
#endif

    private static GambitReplayRequest ReadCommandLineRequest()
    {
        string[] args = Environment.GetCommandLineArgs();
        string trajectory = Value(args, "--gambit-replay");
        if (string.IsNullOrWhiteSpace(trajectory))
            return null;
        GambitReplayRequest result = new GambitReplayRequest { TrajectoryPath = trajectory };
        result.MapId = Value(args, "--gambit-map") ?? result.MapId;
        result.OutputDirectory = Value(args, "--gambit-output") ?? "";
        result.Export = Has(args, "--gambit-export") || !string.IsNullOrWhiteSpace(result.OutputDirectory);
        if (Enum.TryParse(Value(args, "--gambit-camera"), true, out GambitReplayCamera camera)) result.Camera = camera;
        if (TryInt(Value(args, "--gambit-fps"), out int fps)) result.FrameRate = Mathf.Clamp(fps, 1, 240);
        if (TryInt(Value(args, "--gambit-width"), out int width)) result.Width = Mathf.Clamp(width, 64, 7680);
        if (TryInt(Value(args, "--gambit-height"), out int height)) result.Height = Mathf.Clamp(height, 64, 4320);
        if (float.TryParse(Value(args, "--gambit-speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out float speed))
            result.PlaybackSpeed = Mathf.Max(0.01f, speed);
        return result;
    }

    private static string Value(string[] args, string name)
    {
        for (int index = 0; index + 1 < args.Length; index++)
            if (args[index] == name) return args[index + 1];
        return null;
    }

    private static bool Has(string[] args, string name)
    {
        foreach (string item in args) if (item == name) return true;
        return false;
    }

    private static bool TryInt(string value, out int parsed)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }
}

using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public sealed class GambitTrajectoryReplayWindow : EditorWindow
{
    private const string ScenePath = "Assets/Scenes/BotArena.unity";
    private static readonly string[] MapLabels = { "Ascent", "Breeze", "Bind" };
    private static readonly string[] MapIds = { "arena_ascent_v1", "arena_breeze_v1", "arena_bind_v1" };

    private string trajectoryPath = "";
    private string outputDirectory = "";
    private int mapIndex;
    private GambitReplayCamera cameraMode = GambitReplayCamera.Player;
    private int frameRate = 30;
    private int width = 640;
    private int height = 480;
    private float playbackSpeed = 1f;

    [MenuItem("GAMBIT/Replay Trajectory...", false, 20)]
    public static void Open()
    {
        GetWindow<GambitTrajectoryReplayWindow>(true, "GAMBIT Trajectory Replay");
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Known trajectory replay", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Preview or deterministically render a trajectory JSON on a bundled GAMBIT map. "
            + "Replay mode does not start a live match.", MessageType.Info);

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Trajectory JSON");
        EditorGUILayout.BeginHorizontal();
        trajectoryPath = EditorGUILayout.TextField(trajectoryPath);
        if (GUILayout.Button("Browse", GUILayout.Width(72f)))
        {
            string selected = EditorUtility.OpenFilePanel("Select trajectory JSON", InitialDirectory(trajectoryPath), "json");
            if (!string.IsNullOrWhiteSpace(selected)) trajectoryPath = selected;
        }
        EditorGUILayout.EndHorizontal();

        mapIndex = EditorGUILayout.Popup("Map", mapIndex, MapLabels);
        cameraMode = (GambitReplayCamera)EditorGUILayout.EnumPopup("Camera", cameraMode);
        playbackSpeed = EditorGUILayout.Slider("Preview speed", playbackSpeed, 0.1f, 8f);

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Render settings", EditorStyles.boldLabel);
        frameRate = EditorGUILayout.IntSlider("Frame rate", frameRate, 1, 120);
        width = EditorGUILayout.IntField("Width", width);
        height = EditorGUILayout.IntField("Height", height);
        EditorGUILayout.BeginHorizontal();
        outputDirectory = EditorGUILayout.TextField("Output", outputDirectory);
        if (GUILayout.Button("Browse", GUILayout.Width(72f)))
        {
            string selected = EditorUtility.OpenFolderPanel("Select render output folder", InitialDirectory(outputDirectory), "GambitReplay");
            if (!string.IsNullOrWhiteSpace(selected)) outputDirectory = selected;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(12f);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlaying || !File.Exists(trajectoryPath)))
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Preview", GUILayout.Height(34f))) StartReplay(false);
            if (GUILayout.Button("Render WebM", GUILayout.Height(34f))) StartReplay(true);
            EditorGUILayout.EndHorizontal();
        }
        if (!string.IsNullOrWhiteSpace(trajectoryPath) && !File.Exists(trajectoryPath))
            EditorGUILayout.HelpBox("Select an existing trajectory JSON file.", MessageType.Warning);
        EditorGUILayout.HelpBox("During preview, press Space to cycle Player, Observer, and TopDown cameras.", MessageType.None);
    }

    private void StartReplay(bool export)
    {
        if (!File.Exists(trajectoryPath))
        {
            EditorUtility.DisplayDialog("GAMBIT Replay", "The selected trajectory file does not exist.", "OK");
            return;
        }
        if (export && string.IsNullOrWhiteSpace(outputDirectory))
        {
            outputDirectory = EditorUtility.OpenFolderPanel("Select render output folder", InitialDirectory(trajectoryPath), "GambitReplay");
            if (string.IsNullOrWhiteSpace(outputDirectory)) return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        GambitReplayRequest request = new GambitReplayRequest
        {
            TrajectoryPath = Path.GetFullPath(trajectoryPath),
            MapId = MapIds[Mathf.Clamp(mapIndex, 0, MapIds.Length - 1)],
            Camera = cameraMode,
            OutputDirectory = outputDirectory,
            FrameRate = Mathf.Clamp(frameRate, 1, 120),
            Width = Mathf.Clamp(width, 64, 7680),
            Height = Mathf.Clamp(height, 64, 4320),
            PlaybackSpeed = Mathf.Max(0.01f, playbackSpeed),
            Export = export
        };
        GambitTrajectoryReplayBootstrap.SetEditorRequest(request);
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
        Close();
    }

    private static string InitialDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Application.dataPath;
        if (Directory.Exists(path)) return path;
        string directory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) ? directory : Application.dataPath;
    }
}

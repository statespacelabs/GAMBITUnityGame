using System;
using UnityEngine;

/// <summary>
/// Entry point for the interactive GAMBIT DEMO build. It disables inherited
/// auto-bootstrappers and lets the player select a match before gameplay is
/// created.
/// </summary>
[DefaultExecutionOrder(-10000)]
public sealed class GambitDemoStartup : MonoBehaviour
{
    private bool settingsOpen;
    private bool launched;
    private Vector2 settingsScroll;
    private float settingsContentHeight = 640f;
    private GUIStyle titleStyle;
    private GUIStyle buttonStyle;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (Application.isBatchMode || GambitTrajectoryReplayBootstrap.IsReplayRequested
            || FindObjectOfType<GambitDemoStartup>() != null)
            return;
        GameObject root = new GameObject("GAMBIT DEMO Startup");
        root.AddComponent<GambitDemoStartup>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        foreach (GameModeBootstrapper bootstrapper in FindObjectsOfType<GameModeBootstrapper>())
            bootstrapper.enabled = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void OnGUI()
    {
        if (launched) return;
        EnsureStyles();
        GUI.color = new Color(0.015f, 0.02f, 0.04f, 0.96f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        float width = Mathf.Min(620f, Screen.width - 32f);
        float x = (Screen.width - width) * 0.5f;
        float top = Mathf.Max(35f, Screen.height * 0.14f);
        GUI.Label(new Rect(x, top, width, 62f), "GAMBIT DEMO", titleStyle);
        GUI.Label(new Rect(x, top + 65f, width, 26f), "1v1 arena — local match setup", CenteredLabel(16, new Color(0.68f, 0.76f, 0.88f)));
        if (!settingsOpen)
        {
            if (GUI.Button(new Rect(x + 110f, top + 135f, width - 220f, 54f), "START", buttonStyle)) LaunchMatch();
            if (GUI.Button(new Rect(x + 110f, top + 204f, width - 220f, 45f), "SETTINGS", buttonStyle))
            {
                settingsScroll = Vector2.zero;
                settingsOpen = true;
            }
            GUI.Label(new Rect(x, top + 278f, width, 26f), Summary(), CenteredLabel(13, Color.gray));
            return;
        }

        float panelTop = top + 110f;
        GUI.color = new Color(0.08f, 0.12f, 0.19f, 0.92f);
        GUI.DrawTexture(new Rect(x, panelTop, width, Mathf.Min(540f, Screen.height - panelTop - 24f)), Texture2D.whiteTexture);
        GUI.color = Color.white;
        float viewportHeight = Mathf.Max(120f, Mathf.Min(516f, Screen.height - panelTop - 48f));
        settingsScroll = GUI.BeginScrollView(
            new Rect(x + 12f, panelTop + 12f, width - 24f, viewportHeight),
            settingsScroll,
            new Rect(0f, 0f, width - 48f, Mathf.Max(viewportHeight, settingsContentHeight)));
        settingsContentHeight = DrawSettings(width - 48f);
        GUI.EndScrollView();
    }

    private float DrawSettings(float width)
    {
        float y = 0f;
        GUI.Label(new Rect(0, y, width, 24f), "MATCH", CenteredLabel(16, Color.white));
        y += 30f;
        DrawGameModeGrid(width, ref y);
        y += 14f;
        GUI.Label(new Rect(0, y, width, 20f), "MAP", CenteredLabel(14, new Color(0.75f, 0.84f, 1f)));
        y += 24f;
        string[] mapLabels = { "Ascent", "Breeze", "Bind" };
        string[] mapIds = { "arena_ascent_v1", "arena_breeze_v1", "arena_bind_v1" };
        int selectedMap = Array.IndexOf(mapIds, GambitDemoRuntimeSettings.MapId);
        selectedMap = GUI.SelectionGrid(new Rect(0, y, width, 28f), Mathf.Max(0, selectedMap), mapLabels, 3);
        GambitDemoRuntimeSettings.MapId = mapIds[selectedMap];
        y += 46f;
        DrawEnumGrid("Player A bot", ref GambitDemoRuntimeSettings.PlayerABotMode, width, ref y);
        y += 14f;
        DrawEnumGrid("Player B bot", ref GambitDemoRuntimeSettings.PlayerBBotMode, width, ref y);
        y += 14f;
        if (GambitDemoRuntimeSettings.SupportsBotCameraSelection(GambitDemoRuntimeSettings.GameMode))
        {
            DrawCameraGrid(width, ref y);
            y += 14f;
        }
        GUI.Label(new Rect(0, y, width, 20f), "DISPLAY", CenteredLabel(14, new Color(0.75f, 0.84f, 1f)));
        y += 25f;
        GambitDemoRuntimeSettings.ShowHud = GUI.Toggle(new Rect(8f, y, width - 8f, 22f), GambitDemoRuntimeSettings.ShowHud, " Show debug HUD");
        y += 25f;
        GambitDemoRuntimeSettings.ShowEnemyHeatBar = GUI.Toggle(new Rect(8f, y, width - 8f, 22f), GambitDemoRuntimeSettings.ShowEnemyHeatBar, " Show enemy-distance heat bar");
        y += 31f;
        GUI.Label(new Rect(8f, y, 140f, 22f), "Target frame rate");
        GambitDemoRuntimeSettings.TargetFrameRate = (int)GUI.HorizontalSlider(new Rect(155f, y + 6f, width - 255f, 16f), GambitDemoRuntimeSettings.TargetFrameRate, 30f, 120f);
        GUI.Label(new Rect(width - 90f, y, 85f, 22f), GambitDemoRuntimeSettings.TargetFrameRate + " FPS");
        y += 53f;
        if (GUI.Button(new Rect(0, y, width * 0.48f, 40f), "BACK", buttonStyle)) settingsOpen = false;
        if (GUI.Button(new Rect(width * 0.52f, y, width * 0.48f, 40f), "START MATCH", buttonStyle)) LaunchMatch();
        return y + 56f;
    }

    private static void DrawEnumGrid<T>(string label, ref T value, float width, ref float y) where T : struct, IConvertible
    {
        GUI.Label(new Rect(0, y, width, 20f), label.ToUpperInvariant(), CenteredLabel(14, new Color(0.75f, 0.84f, 1f)));
        y += 24f;
        T[] values = (T[])Enum.GetValues(typeof(T));
        string[] labels = Array.ConvertAll(values, item => item.ToString());
        int current = Array.IndexOf(values, value);
        int columns = values.Length > 5 ? 2 : values.Length;
        int rows = Mathf.CeilToInt(values.Length / (float)columns);
        int next = GUI.SelectionGrid(new Rect(0, y, width, rows * 28f), Mathf.Max(0, current), labels, columns);
        value = values[Mathf.Clamp(next, 0, values.Length - 1)];
        y += rows * 28f;
    }

    private static void DrawGameModeGrid(float width, ref float y)
    {
        GUI.Label(new Rect(0, y, width, 20f), "GAME MODE", CenteredLabel(14, new Color(0.75f, 0.84f, 1f)));
        y += 24f;
        GameModeBootstrapper.GameMode[] values =
            (GameModeBootstrapper.GameMode[])Enum.GetValues(typeof(GameModeBootstrapper.GameMode));
        string[] labels = Array.ConvertAll(values, GambitDemoRuntimeSettings.GameModeDisplayName);
        int current = Array.IndexOf(values, GambitDemoRuntimeSettings.GameMode);
        const int columns = 2;
        int rows = Mathf.CeilToInt(values.Length / (float)columns);
        int next = GUI.SelectionGrid(new Rect(0, y, width, rows * 38f), Mathf.Max(0, current), labels, columns);
        GambitDemoRuntimeSettings.GameMode = values[Mathf.Clamp(next, 0, values.Length - 1)];
        y += rows * 38f + 5f;

        bool training = GambitDemoRuntimeSettings.IsTrainingMode(GambitDemoRuntimeSettings.GameMode);
        string explanation = training
            ? "Training mode: connects Unity ML-Agents to an external trainer."
            : "RL modes run the bundled ONNX policy locally.";
        GUIStyle helpStyle = CenteredLabel(12, training ? new Color(1f, 0.78f, 0.35f) : Color.gray);
        helpStyle.wordWrap = true;
        GUI.Label(new Rect(8f, y, width - 16f, 34f), explanation, helpStyle);
        y += 38f;
    }

    private static void DrawCameraGrid(float width, ref float y)
    {
        GUI.Label(new Rect(0, y, width, 20f), "CAMERA", CenteredLabel(14, new Color(0.75f, 0.84f, 1f)));
        y += 24f;

        GameModeBootstrapper.CameraView[] views =
            (GameModeBootstrapper.CameraView[])Enum.GetValues(typeof(GameModeBootstrapper.CameraView));
        string[] labels = Array.ConvertAll(
            views,
            view => GambitDemoRuntimeSettings.CameraViewDisplayName(GambitDemoRuntimeSettings.GameMode, view));
        int current = Array.IndexOf(views, GambitDemoRuntimeSettings.BotMatchCamera);
        int next = GUI.SelectionGrid(new Rect(0, y, width, 32f), Mathf.Max(0, current), labels, 3);
        GambitDemoRuntimeSettings.BotMatchCamera = views[Mathf.Clamp(next, 0, views.Length - 1)];
        y += 38f;

        GUI.Label(new Rect(0, y, width, 20f), "Switch during the match: 1 Observer  •  2 Player A  •  3 Player B", CenteredLabel(12, Color.gray));
        y += 24f;
    }

    private void LaunchMatch()
    {
        launched = true;
        Application.targetFrameRate = GambitDemoRuntimeSettings.TargetFrameRate;
        DemoMapRuntime.ConfigureForDemo(GambitDemoRuntimeSettings.MapId);
        GameObject runtime = new GameObject("GAMBIT DEMO Runtime");
        MatchConfig config = ScriptableObject.CreateInstance<MatchConfig>();
        GameModeBootstrapper bootstrapper = runtime.AddComponent<GameModeBootstrapper>();
        bootstrapper.AllowEnvironmentOverrides = false;
        bootstrapper.CurrentGameMode = GambitDemoRuntimeSettings.GameMode;
        bootstrapper.MatchConfigAsset = config;
        bootstrapper.PlayerABotMode = GambitDemoRuntimeSettings.PlayerABotMode;
        bootstrapper.PlayerBBotMode = GambitDemoRuntimeSettings.PlayerBBotMode;
        bootstrapper.InitialCameraView = GambitDemoRuntimeSettings.BotMatchCamera;
        runtime.AddComponent<DebugOverlay>();
        runtime.AddComponent<RecordingManager>();
        Destroy(gameObject);
    }

    private void EnsureStyles()
    {
        if (titleStyle != null) return;
        titleStyle = CenteredLabel(46, new Color(0.87f, 0.95f, 1f));
        titleStyle.fontStyle = FontStyle.Bold;
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
    }

    private static GUIStyle CenteredLabel(int size, Color color)
    {
        return new GUIStyle(GUI.skin.label) { fontSize = size, alignment = TextAnchor.MiddleCenter, normal = { textColor = color } };
    }

    private static string Summary()
    {
        string summary = GambitDemoRuntimeSettings.GameModeDisplayName(GambitDemoRuntimeSettings.GameMode)
            + "  •  " + GambitDemoRuntimeSettings.MapId.Replace("arena_", "").Replace("_v1", "");
        if (GambitDemoRuntimeSettings.SupportsBotCameraSelection(GambitDemoRuntimeSettings.GameMode))
            summary += "  •  " + GambitDemoRuntimeSettings.CameraViewDisplayName(
                GambitDemoRuntimeSettings.GameMode,
                GambitDemoRuntimeSettings.BotMatchCamera);
        return summary;
    }
}

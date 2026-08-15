using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Always starts Editor Play mode from the complete release scene. This keeps
/// Unity recovery scenes and partially opened asset scenes from booting without
/// the baked default map.
/// </summary>
[InitializeOnLoad]
public static class GambitPlayModeSceneGuard
{
    public const string DemoScenePath = "Assets/Scenes/BotArena.unity";

    static GambitPlayModeSceneGuard()
    {
        EditorApplication.delayCall += Configure;
    }

    [MenuItem("GAMBIT/Configure Play Mode Start Scene", false, 20)]
    public static void Configure()
    {
        SceneAsset demoScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(DemoScenePath);
        if (demoScene == null)
        {
            Debug.LogError("[GAMBIT] Play Mode start scene is missing: " + DemoScenePath);
            return;
        }

        if (EditorSceneManager.playModeStartScene == demoScene)
            return;

        EditorSceneManager.playModeStartScene = demoScene;
        Debug.Log("[GAMBIT] Play Mode will always start from " + DemoScenePath);
    }
}

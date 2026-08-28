using System;
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class GambitTrainingLauncher
{
    private const string MenuPath = "GAMBIT/Training/Start Training...";
    private static string trainingConfigPath;

    static GambitTrainingLauncher()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    [MenuItem(MenuPath, false, 30)]
    public static void StartTraining()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[Training] Unity is already entering or running Play Mode.");
            return;
        }

        string trainingDirectory = Path.GetFullPath(Path.Combine(
            Application.dataPath,
            "..",
            "Training"));
        string existing = Environment.GetEnvironmentVariable(
            GambitLaunchConfigLoader.EnvironmentVariable);
        trainingConfigPath = !string.IsNullOrWhiteSpace(existing) && File.Exists(existing)
            ? Path.GetFullPath(existing)
            : EditorUtility.OpenFilePanel(
                "Select GAMBIT training configuration",
                trainingDirectory,
                "json");
        if (string.IsNullOrWhiteSpace(trainingConfigPath))
            return;
        if (!File.Exists(trainingConfigPath))
            throw new FileNotFoundException("Training launch config is missing", trainingConfigPath);

        Environment.SetEnvironmentVariable(
            GambitLaunchConfigLoader.EnvironmentVariable,
            trainingConfigPath);
        Debug.Log("[Training] Starting learner versus scripted bot with " + trainingConfigPath);
        EditorApplication.EnterPlaymode();
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode || string.IsNullOrEmpty(trainingConfigPath))
            return;
        string current = Environment.GetEnvironmentVariable(GambitLaunchConfigLoader.EnvironmentVariable);
        if (string.Equals(current, trainingConfigPath, StringComparison.Ordinal))
            Environment.SetEnvironmentVariable(GambitLaunchConfigLoader.EnvironmentVariable, null);
        trainingConfigPath = null;
    }
}

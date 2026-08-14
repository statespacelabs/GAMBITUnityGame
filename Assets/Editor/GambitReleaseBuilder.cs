using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>Reproducible native macOS builds for the GAMBIT interactive demo.</summary>
public static class GambitReleaseBuilder
{
    private const string ScenePath = "Assets/Scenes/BotArena.unity";
    private const string BundleIdentifier = "com.aimslab.gambitdemo";

    [MenuItem("GAMBIT/Build/macOS Native (Universal)", false, 100)]
    public static void BuildMacUniversal()
    {
        BuildMac(2, "Universal");
    }

    private static void BuildMac(int architecture, string architectureName)
    {
        ValidateInputs();
        string outputPath = ReadArgument("--gambit-build-output");
        if (string.IsNullOrWhiteSpace(outputPath))
            outputPath = Path.Combine("Builds", "macOS-" + architectureName, "GAMBIT Demo.app");
        outputPath = Path.GetFullPath(outputPath);

        string outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new BuildFailedException("Unable to resolve the build output directory.");
        Directory.CreateDirectory(outputDirectory);

        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone, BundleIdentifier);
        PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, architecture);

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = outputPath,
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.None
        };

        Debug.Log($"[GambitBuild] Starting macOS {architectureName} build: {outputPath}");
        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException(
                $"GAMBIT macOS {architectureName} build failed: {report.summary.result} "
                + $"errors={report.summary.totalErrors} warnings={report.summary.totalWarnings}");

        Debug.Log(
            $"[GambitBuild] PASS architecture={architectureName} output={outputPath} "
            + $"bytes={report.summary.totalSize} duration={report.summary.totalTime}");
    }

    private static void ValidateInputs()
    {
        string[] requiredAssets =
        {
            ScenePath,
            "Assets/Resources/MLModels/navigator.onnx",
            "Assets/Resources/MLModels/combat.onnx",
            "Assets/Resources/MLModels/normalizer.json",
            "Assets/Resources/Maps/map_catalog.json",
            "Assets/Resources/Maps/ArenaBreeze.prefab",
            "Assets/Resources/Maps/ArenaBind.prefab"
        };
        foreach (string path in requiredAssets)
            if (!File.Exists(Path.GetFullPath(path)))
                throw new BuildFailedException("Required GAMBIT build asset is missing: " + path);

        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(ScenePath, true)
        };
    }

    private static string ReadArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        return "";
    }
}

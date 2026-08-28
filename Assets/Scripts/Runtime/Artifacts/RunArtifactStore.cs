using System;
using System.IO;
using UnityEngine;

/// <summary>Small, shared persistence boundary for match, training, and tournament output.</summary>
public interface IRunArtifactStore
{
    string RunDirectory { get; }
    string WriteJson<T>(string relativePath, T value);
    string WriteText(string relativePath, string value);
}

public sealed class RunArtifactStore : IRunArtifactStore
{
    public string RunDirectory { get; private set; }

    public RunArtifactStore(string outputRoot, string runId)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
            throw new ArgumentException("Output root is required.", nameof(outputRoot));
        RunDirectory = Path.GetFullPath(Path.Combine(outputRoot, SanitizeSegment(runId)));
        Directory.CreateDirectory(RunDirectory);
    }

    public string WriteJson<T>(string relativePath, T value)
    {
        return WriteText(relativePath, JsonUtility.ToJson(value, true));
    }

    public string WriteText(string relativePath, string value)
    {
        string destination = ResolveSafePath(relativePath);
        string directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temporary = destination + ".tmp";
        File.WriteAllText(temporary, value ?? string.Empty);
        if (File.Exists(destination)) File.Delete(destination);
        File.Move(temporary, destination);
        return destination;
    }

    private string ResolveSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("Artifact path must be relative.", nameof(relativePath));

        string fullPath = Path.GetFullPath(Path.Combine(RunDirectory, relativePath));
        string requiredPrefix = RunDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(requiredPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Artifact path leaves the run directory.", nameof(relativePath));
        return fullPath;
    }

    private static string SanitizeSegment(string value)
    {
        string segment = string.IsNullOrWhiteSpace(value) ? "unnamed-run" : value;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            segment = segment.Replace(invalid, '_');
        return segment.Replace('/', '_').Replace('\\', '_');
    }
}

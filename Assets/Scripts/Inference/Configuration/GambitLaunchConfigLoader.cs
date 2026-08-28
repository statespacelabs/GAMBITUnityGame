using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public interface IGambitLaunchConfigSource
{
    bool TryLoad(out GambitLaunchConfig config, out string description);
}

/// <summary>Loads an optional JSON launch file; callers provide a safe in-app fallback.</summary>
public sealed class JsonLaunchConfigSource : IGambitLaunchConfigSource
{
    private readonly string path;

    public JsonLaunchConfigSource(string path)
    {
        this.path = path;
    }

    public bool TryLoad(out GambitLaunchConfig config, out string description)
    {
        config = null;
        description = "";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        config = JsonUtility.FromJson<GambitLaunchConfig>(File.ReadAllText(path));
        description = Path.GetFullPath(path);
        return config != null;
    }
}

public static class GambitLaunchConfigLoader
{
    public const string EnvironmentVariable = "GAMBIT_LAUNCH_CONFIG";

    public static GambitLaunchConfig LoadOrDefault(GambitLaunchConfig fallback)
    {
        string path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path)) return Validate(fallback, "in-app settings");

        JsonLaunchConfigSource source = new JsonLaunchConfigSource(path);
        if (!source.TryLoad(out GambitLaunchConfig config, out string description))
            throw new InvalidOperationException("Launch config was requested but could not be read: " + path);
        return Validate(config, description);
    }

    private static GambitLaunchConfig Validate(GambitLaunchConfig config, string description)
    {
        if (config == null) throw new InvalidOperationException("No GAMBIT launch configuration was supplied.");
        IReadOnlyList<string> errors = config.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid GAMBIT launch configuration (" + description + "): " + string.Join("; ", errors));
        Debug.Log("[GAMBIT] Launch configuration: " + description + " mode=" + config.Mode + " match=" + config.Match.Id);
        return config;
    }
}

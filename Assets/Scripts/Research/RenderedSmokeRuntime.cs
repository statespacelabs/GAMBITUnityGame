using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Two-factor exception used only for Goal 13's final attended rendered smoke.
/// It never enables training, visual observations, or the headless runtime.
/// </summary>
public static class RenderedSmokeRuntime
{
    public const string LaunchFlag = "--phase5-rendered-smoke";

    public static bool Enabled =>
        (Environment.GetEnvironmentVariable("PHASE5_RENDERED_ATTENDED_SMOKE") ?? "0").Trim() == "1"
        && Array.Exists(Environment.GetCommandLineArgs(), argument => argument == LaunchFlag)
        && !Application.isBatchMode
        && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
}

using System;
using UnityEngine;

/// <summary>
/// Runtime execution-mode policy shared by release gameplay. Research-specific
/// launch validation and rendering audits live in the optional research assembly.
/// </summary>
public static class GambitRuntimeMode
{
    public const string LegacyHeadlessFlag = "--phase5-headless";

    private static bool? headless;

    public static bool IsHeadless
    {
        get
        {
            if (!headless.HasValue)
                headless = Application.isBatchMode || HasArgument(LegacyHeadlessFlag);
            return headless.Value;
        }
    }

    private static bool HasArgument(string expected)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int index = 0; index < arguments.Length; index++)
        {
            if (string.Equals(arguments[index], expected, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

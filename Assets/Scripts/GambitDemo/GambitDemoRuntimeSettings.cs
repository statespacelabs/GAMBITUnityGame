using UnityEngine;

/// <summary>
/// Runtime configuration selected from the GAMBIT DEMO start screen.
/// This is intentionally independent of environment variables so an
/// interactive build has one visible, reproducible configuration source.
/// </summary>
public static class GambitDemoRuntimeSettings
{
    public static GameModeBootstrapper.GameMode GameMode = GameModeBootstrapper.GameMode.HumanVsScripted;
    public static ScriptedBotController.ScriptedBotMode PlayerABotMode = ScriptedBotController.ScriptedBotMode.FaceOpponentAndShoot;
    public static ScriptedBotController.ScriptedBotMode PlayerBBotMode = ScriptedBotController.ScriptedBotMode.RandomStrafe;
    public static string MapId = "arena_ascent_v1";
    public static bool ShowEnemyHeatBar = true;
    public static bool ShowHud = true;
    public static int TargetFrameRate = 60;

    public static string GameModeDisplayName(GameModeBootstrapper.GameMode mode)
    {
        switch (mode)
        {
            case GameModeBootstrapper.GameMode.ScriptedVsScripted: return "Scripted vs Scripted";
            case GameModeBootstrapper.GameMode.RLVsScripted: return "RL vs Scripted (trained)";
            case GameModeBootstrapper.GameMode.RLVsRL: return "RL vs RL (trained)";
            case GameModeBootstrapper.GameMode.HumanVsRL: return "Human vs RL (trained)";
            case GameModeBootstrapper.GameMode.HumanVsScripted: return "Human vs Scripted";
            case GameModeBootstrapper.GameMode.HumanVsGambit: return "Human vs RL (for training)";
            case GameModeBootstrapper.GameMode.GambitVsScripted: return "RL vs Scripted (for training)";
            case GameModeBootstrapper.GameMode.GambitVsGambit: return "RL vs RL (for training)";
            default: return mode.ToString();
        }
    }

    public static bool IsTrainingMode(GameModeBootstrapper.GameMode mode)
    {
        return mode == GameModeBootstrapper.GameMode.HumanVsGambit
            || mode == GameModeBootstrapper.GameMode.GambitVsScripted
            || mode == GameModeBootstrapper.GameMode.GambitVsGambit;
    }
}

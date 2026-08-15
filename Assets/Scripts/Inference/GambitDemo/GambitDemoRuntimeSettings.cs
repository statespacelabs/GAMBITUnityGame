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
    public static GameModeBootstrapper.CameraView BotMatchCamera = GameModeBootstrapper.CameraView.Observer;
    public static string MapId = "arena_ascent_v1";
    public static bool ShowEnemyHeatBar = true;
    public static bool ShowHud = true;
    public static int TargetFrameRate = 60;

    public static string GameModeDisplayName(GameModeBootstrapper.GameMode mode)
    {
        switch (mode)
        {
            case GameModeBootstrapper.GameMode.ScriptedVsScripted: return "Scripted vs Scripted";
            case GameModeBootstrapper.GameMode.RLVsScripted: return "RL vs Scripted";
            case GameModeBootstrapper.GameMode.RLVsRL: return "RL vs RL";
            case GameModeBootstrapper.GameMode.HumanVsRL: return "Human vs RL";
            case GameModeBootstrapper.GameMode.HumanVsScripted: return "Human vs Scripted";
            case GameModeBootstrapper.GameMode.HumanVsGambit: return "Human vs RL (Training)";
            case GameModeBootstrapper.GameMode.GambitVsScripted: return "RL vs Scripted (Training)";
            case GameModeBootstrapper.GameMode.GambitVsGambit: return "RL vs RL (Training)";
            default: return mode.ToString();
        }
    }

    public static bool IsTrainingMode(GameModeBootstrapper.GameMode mode)
    {
        return mode == GameModeBootstrapper.GameMode.HumanVsGambit
            || mode == GameModeBootstrapper.GameMode.GambitVsScripted
            || mode == GameModeBootstrapper.GameMode.GambitVsGambit;
    }

    public static bool SupportsBotCameraSelection(GameModeBootstrapper.GameMode mode)
    {
        return mode != GameModeBootstrapper.GameMode.HumanVsRL
            && mode != GameModeBootstrapper.GameMode.HumanVsScripted
            && mode != GameModeBootstrapper.GameMode.HumanVsGambit;
    }

    public static string CameraViewDisplayName(
        GameModeBootstrapper.GameMode mode,
        GameModeBootstrapper.CameraView view)
    {
        if (view == GameModeBootstrapper.CameraView.Observer)
            return "Observer";

        bool playerA = view == GameModeBootstrapper.CameraView.PlayerA;
        switch (mode)
        {
            case GameModeBootstrapper.GameMode.ScriptedVsScripted:
                return playerA ? "Scripted A POV" : "Scripted B POV";
            case GameModeBootstrapper.GameMode.RLVsScripted:
                return playerA ? "RL POV" : "Scripted POV";
            case GameModeBootstrapper.GameMode.RLVsRL:
                return playerA ? "RL A POV" : "RL B POV";
            case GameModeBootstrapper.GameMode.GambitVsScripted:
                return playerA ? "Training RL POV" : "Scripted POV";
            case GameModeBootstrapper.GameMode.GambitVsGambit:
                return playerA ? "Training RL A POV" : "Training RL B POV";
            default:
                return playerA ? "Player A POV" : "Player B POV";
        }
    }
}

using System;

/// <summary>
/// Compatibility adapter for the existing menu. New systems consume MatchSpec;
/// the old enum remains only as a compact set of human-facing presets.
/// </summary>
public static class GameModePresets
{
    public static MatchSpec FromLegacy(
        GameModeBootstrapper.GameMode mode,
        ScriptedBotController.ScriptedBotMode playerABotMode,
        ScriptedBotController.ScriptedBotMode playerBBotMode,
        string mapId,
        int targetFrameRate)
    {
        MatchSpec spec = new MatchSpec
        {
            Id = "demo/" + mode,
            MapId = mapId,
            Execution = ExecutionProfile.Interactive(targetFrameRate),
            PlayerA = PolicySpec.Scripted(playerABotMode.ToString()),
            PlayerB = PolicySpec.Scripted(playerBBotMode.ToString())
        };

        switch (mode)
        {
            case GameModeBootstrapper.GameMode.ScriptedVsScripted:
                break;
            case GameModeBootstrapper.GameMode.RLVsScripted:
                spec.PlayerA = PolicySpec.Onnx();
                break;
            case GameModeBootstrapper.GameMode.RLVsRL:
                spec.PlayerA = PolicySpec.Onnx("onnx/bundled-a");
                spec.PlayerB = PolicySpec.Onnx("onnx/bundled-b");
                break;
            case GameModeBootstrapper.GameMode.HumanVsRL:
                spec.PlayerA = PolicySpec.Human();
                spec.PlayerB = PolicySpec.Onnx();
                break;
            case GameModeBootstrapper.GameMode.HumanVsScripted:
                spec.PlayerA = PolicySpec.Human();
                break;
            case GameModeBootstrapper.GameMode.HumanVsGambit:
                spec.PlayerA = PolicySpec.Human();
                spec.PlayerB = PolicySpec.Training();
                spec.Execution.Kind = GambitExecutionKind.RenderedEvaluation;
                break;
            case GameModeBootstrapper.GameMode.GambitVsScripted:
                spec.PlayerA = PolicySpec.Training();
                spec.Execution.Kind = GambitExecutionKind.RenderedEvaluation;
                break;
            case GameModeBootstrapper.GameMode.GambitVsGambit:
                spec.PlayerA = PolicySpec.Training("GambitAgent");
                spec.PlayerB = PolicySpec.Training("GambitAgent");
                spec.Execution.Kind = GambitExecutionKind.RenderedEvaluation;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown game-mode preset.");
        }
        return spec;
    }

    public static GameModeBootstrapper.ControllerType ControllerTypeFor(PolicySpec policy)
    {
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        switch (policy.Kind)
        {
            case GambitPolicyKind.Human: return GameModeBootstrapper.ControllerType.Human;
            case GambitPolicyKind.Scripted: return GameModeBootstrapper.ControllerType.ScriptedBot;
            case GambitPolicyKind.Onnx: return GameModeBootstrapper.ControllerType.RLBot;
            case GambitPolicyKind.MlAgents: return GameModeBootstrapper.ControllerType.GambitBot;
            default: throw new NotSupportedException("Policy kind is not supported by live matches: " + policy.Kind);
        }
    }

    public static GameModeBootstrapper.GameMode LegacyModeFor(MatchSpec match)
    {
        if (match == null || match.PlayerA == null || match.PlayerB == null)
            throw new ArgumentNullException(nameof(match));
        GambitPolicyKind a = match.PlayerA.Kind;
        GambitPolicyKind b = match.PlayerB.Kind;
        if (a == GambitPolicyKind.Scripted && b == GambitPolicyKind.Scripted)
            return GameModeBootstrapper.GameMode.ScriptedVsScripted;
        if (a == GambitPolicyKind.Onnx && b == GambitPolicyKind.Scripted)
            return GameModeBootstrapper.GameMode.RLVsScripted;
        if (a == GambitPolicyKind.Onnx && b == GambitPolicyKind.Onnx)
            return GameModeBootstrapper.GameMode.RLVsRL;
        if (a == GambitPolicyKind.Human && b == GambitPolicyKind.Onnx)
            return GameModeBootstrapper.GameMode.HumanVsRL;
        if (a == GambitPolicyKind.Human && b == GambitPolicyKind.Scripted)
            return GameModeBootstrapper.GameMode.HumanVsScripted;
        if (a == GambitPolicyKind.Human && b == GambitPolicyKind.MlAgents)
            return GameModeBootstrapper.GameMode.HumanVsGambit;
        if (a == GambitPolicyKind.MlAgents && b == GambitPolicyKind.Scripted)
            return GameModeBootstrapper.GameMode.GambitVsScripted;
        if (a == GambitPolicyKind.MlAgents && b == GambitPolicyKind.MlAgents)
            return GameModeBootstrapper.GameMode.GambitVsGambit;
        throw new NotSupportedException("No legacy game-mode label exists for " + a + " vs " + b + ".");
    }
}

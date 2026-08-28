using System;
using System.Collections.Generic;

/// <summary>Top-level reason Unity was launched. Gameplay remains owned by MatchSpec.</summary>
public enum GambitLaunchMode
{
    Demo,
    Training,
    Tournament,
    Replay
}

/// <summary>Interchangeable source of commands for one player slot.</summary>
public enum GambitPolicyKind
{
    Human,
    Scripted,
    Onnx,
    MlAgents,
    Replay
}

/// <summary>Presentation and simulation budget, independent of policy choice.</summary>
public enum GambitExecutionKind
{
    Interactive,
    RenderedEvaluation,
    HeadlessTraining
}

[Serializable]
public sealed class PolicySpec
{
    public GambitPolicyKind Kind = GambitPolicyKind.Scripted;
    public string Id = "scripted/default";
    public string ModelPath = "";
    public string BehaviorName = "GambitAgent";
    public string ObservationSchema = LocalObservationContract.SchemaId;
    public string ActionSchema = PolicyActionContract.SchemaId;
    public string Variant = "RandomStrafe";

    public PolicySpec Copy()
    {
        return (PolicySpec)MemberwiseClone();
    }

    public static PolicySpec Human(string id = "human/local")
    {
        return new PolicySpec { Kind = GambitPolicyKind.Human, Id = id };
    }

    public static PolicySpec Scripted(string variant, string id = "scripted/default")
    {
        return new PolicySpec { Kind = GambitPolicyKind.Scripted, Id = id, Variant = variant };
    }

    public static PolicySpec Onnx(string id = "onnx/bundled")
    {
        return new PolicySpec { Kind = GambitPolicyKind.Onnx, Id = id };
    }

    public static PolicySpec Training(string behaviorName = "GambitAgent")
    {
        return new PolicySpec
        {
            Kind = GambitPolicyKind.MlAgents,
            Id = "mlagents/training",
            BehaviorName = behaviorName
        };
    }

    public static PolicySpec UnifiedTraining(string behaviorName = "GambitUnifiedAgent")
    {
        return new PolicySpec
        {
            Kind = GambitPolicyKind.MlAgents,
            Id = "mlagents/gen3-unified",
            BehaviorName = behaviorName,
            ObservationSchema = UnifiedFairObservationV2Contract.TokenSchemaId,
            ActionSchema = UnifiedFairObservationV2Contract.ActionSchemaId,
            Variant = "direct-owner"
        };
    }

    public static PolicySpec UnifiedOnnx(
        string modelResource = "MLModels/gen3_champion_v2")
    {
        return new PolicySpec
        {
            Kind = GambitPolicyKind.Onnx,
            Id = "onnx/gen3-unified",
            ModelPath = modelResource,
            ObservationSchema = UnifiedFairObservationV2Contract.SchemaId,
            ActionSchema = UnifiedFairObservationV2Contract.ActionSchemaId,
            Variant = "direct-owner"
        };
    }
}

[Serializable]
public sealed class MatchRulesSpec
{
    public int MaxHealth = 100;
    public int DamagePerHit = 20;
    public int KillsToWin = 5;
    public float ScorePerHit = 0.2f;
    public float ScorePerKill = 1f;
    public float CooldownSeconds = 3f;
    public bool ResetPositionsAfterKill = true;
    public bool ResetHealthAfterKill = true;
}

[Serializable]
public sealed class ExecutionProfile
{
    public GambitExecutionKind Kind = GambitExecutionKind.Interactive;
    public int ArenaCount = 1;
    public int TargetFrameRate = 60;
    public bool ShowHud = true;
    public bool ShowEnemyHeatBar = true;
    public bool RecordCommands;

    public static ExecutionProfile Interactive(int targetFrameRate = 60)
    {
        return new ExecutionProfile { TargetFrameRate = targetFrameRate };
    }

    public static ExecutionProfile Headless(int arenaCount)
    {
        return new ExecutionProfile
        {
            Kind = GambitExecutionKind.HeadlessTraining,
            ArenaCount = Math.Max(1, arenaCount),
            ShowHud = false,
            ShowEnemyHeatBar = false
        };
    }
}

[Serializable]
public sealed class MatchSpec
{
    public string Id = "match/default";
    public string MapId = "arena_ascent_v1";
    public int Seed;
    public PolicySpec PlayerA = PolicySpec.Human();
    public PolicySpec PlayerB = PolicySpec.Scripted("RandomStrafe");
    public MatchRulesSpec Rules = new MatchRulesSpec();
    public ExecutionProfile Execution = ExecutionProfile.Interactive();

    public MatchSpec Copy()
    {
        return new MatchSpec
        {
            Id = Id,
            MapId = MapId,
            Seed = Seed,
            PlayerA = PlayerA == null ? null : PlayerA.Copy(),
            PlayerB = PlayerB == null ? null : PlayerB.Copy(),
            Rules = Rules == null ? null : new MatchRulesSpec
            {
                MaxHealth = Rules.MaxHealth,
                DamagePerHit = Rules.DamagePerHit,
                KillsToWin = Rules.KillsToWin,
                ScorePerHit = Rules.ScorePerHit,
                ScorePerKill = Rules.ScorePerKill,
                CooldownSeconds = Rules.CooldownSeconds,
                ResetPositionsAfterKill = Rules.ResetPositionsAfterKill,
                ResetHealthAfterKill = Rules.ResetHealthAfterKill
            },
            Execution = Execution == null ? null : new ExecutionProfile
            {
                Kind = Execution.Kind,
                ArenaCount = Execution.ArenaCount,
                TargetFrameRate = Execution.TargetFrameRate,
                ShowHud = Execution.ShowHud,
                ShowEnemyHeatBar = Execution.ShowEnemyHeatBar,
                RecordCommands = Execution.RecordCommands
            }
        };
    }
}

[Serializable]
public sealed class TrainingSpec
{
    public string RunId = "training/default";
    public int MaxEpisodes;
    public float EpisodeTimeoutSeconds;
    public string CurriculumId = "curriculum/default";
    public bool WriteMetrics = true;
}

[Serializable]
public sealed class MatchupSpec
{
    public string Id = "matchup/default";
    public PolicySpec PlayerA = PolicySpec.Onnx("policy/a");
    public PolicySpec PlayerB = PolicySpec.Onnx("policy/b");
    public int Repetitions = 1;
    public bool SwapSides = true;
}

[Serializable]
public sealed class TournamentSpec
{
    public string RunId = "tournament/default";
    public List<MatchupSpec> Matchups = new List<MatchupSpec>();
}

[Serializable]
public sealed class OutputSpec
{
    public string RootDirectory = "GambitRuns";
    public bool WriteMatchResults = true;
    public bool WriteTelemetry;
    public bool WriteTrajectory;
}

[Serializable]
public sealed class GambitLaunchConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion = CurrentSchemaVersion;
    public GambitLaunchMode Mode = GambitLaunchMode.Demo;
    public MatchSpec Match = new MatchSpec();
    public TrainingSpec Training;
    public TournamentSpec Tournament;
    public OutputSpec Output = new OutputSpec();

    public IReadOnlyList<string> Validate()
    {
        List<string> errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            errors.Add("Unsupported launch schema version: " + SchemaVersion);
        if (Match == null)
            errors.Add("Match is required.");
        else
        {
            if (string.IsNullOrWhiteSpace(Match.MapId)) errors.Add("Match.MapId is required.");
            if (Match.PlayerA == null) errors.Add("Match.PlayerA is required.");
            if (Match.PlayerB == null) errors.Add("Match.PlayerB is required.");
            if (Match.Rules == null) errors.Add("Match.Rules is required.");
            if (Match.Execution == null) errors.Add("Match.Execution is required.");
            else if (Match.Execution.ArenaCount < 1) errors.Add("ArenaCount must be at least one.");
        }
        if (Mode == GambitLaunchMode.Training && Training == null)
            errors.Add("Training settings are required in Training mode.");
        if (Mode == GambitLaunchMode.Tournament && Tournament == null)
            errors.Add("Tournament settings are required in Tournament mode.");
        return errors;
    }
}

[Serializable]
public sealed class PlayerMatchResult
{
    public string PolicyId;
    public float Score;
    public int Kills;
    public int Deaths;
    public int Hits;
}

[Serializable]
public sealed class MatchResult
{
    public string RunId;
    public string MatchId;
    public string MapId;
    public int Seed;
    public int AreaId;
    public double DurationSeconds;
    public string WinnerPolicyId;
    public PlayerMatchResult PlayerA = new PlayerMatchResult();
    public PlayerMatchResult PlayerB = new PlayerMatchResult();
}

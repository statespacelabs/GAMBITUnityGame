using System.Linq;
using NUnit.Framework;

public sealed class PlatformSpecsTests
{
    [Test]
    public void TrainingLaunchRequiresTrainingSettings()
    {
        GambitLaunchConfig config = new GambitLaunchConfig
        {
            Mode = GambitLaunchMode.Training,
            Training = null
        };

        Assert.That(config.Validate(), Does.Contain("Training settings are required in Training mode."));
    }

    [Test]
    public void LegacyTrainingPresetUsesMlAgentsPolicy()
    {
        MatchSpec match = GameModePresets.FromLegacy(
            GameModeBootstrapper.GameMode.GambitVsScripted,
            ScriptedBotController.ScriptedBotMode.FaceOpponentAndShoot,
            ScriptedBotController.ScriptedBotMode.RandomStrafe,
            "arena_ascent_v1",
            60);

        Assert.That(match.PlayerA.Kind, Is.EqualTo(GambitPolicyKind.MlAgents));
        Assert.That(match.PlayerB.Kind, Is.EqualTo(GambitPolicyKind.Scripted));
        Assert.That(GameModePresets.LegacyModeFor(match),
            Is.EqualTo(GameModeBootstrapper.GameMode.GambitVsScripted));
    }

    [Test]
    public void TournamentScheduleExpandsRepetitionsAndSideSwaps()
    {
        TournamentSpec tournament = new TournamentSpec();
        tournament.Matchups.Add(new MatchupSpec
        {
            Id = "a-vs-b",
            PlayerA = PolicySpec.Onnx("a"),
            PlayerB = PolicySpec.Onnx("b"),
            Repetitions = 3,
            SwapSides = true
        });

        var matches = TournamentSchedule.Expand(tournament, new MatchSpec());

        Assert.That(matches, Has.Count.EqualTo(6));
        Assert.That(matches[0].PlayerA.Id, Is.EqualTo("a"));
        Assert.That(matches[1].PlayerA.Id, Is.EqualTo("b"));
        Assert.That(matches.Select(item => item.Id).Distinct().Count(), Is.EqualTo(6));
    }

    [Test]
    public void NavigationAssistedCompositionHasOneExplicitOwnerPerAction()
    {
        PlayerCommand navigation = new PlayerCommand
        {
            MoveX = 0.25f,
            MoveZ = 0.75f,
            Turn = -0.5f,
            LookPitch = -0.25f
        };
        PlayerCommand learner = new PlayerCommand
        {
            MoveX = -1f,
            MoveZ = -1f,
            Turn = 0.4f,
            LookPitch = 0.3f,
            Shoot = true,
            Reload = true,
            Jump = true,
            Crouch = true
        };

        PlayerCommand hidden = NavigationAssistedCommandComposer.Compose(
            navigation, learner, false);
        Assert.That(hidden.MoveX, Is.EqualTo(0.25f));
        Assert.That(hidden.MoveZ, Is.EqualTo(0.75f));
        Assert.That(hidden.Turn, Is.EqualTo(-0.5f));
        Assert.That(hidden.LookPitch, Is.EqualTo(-0.25f));
        Assert.That(hidden.Shoot, Is.False);
        Assert.That(hidden.Reload, Is.True);

        PlayerCommand visible = NavigationAssistedCommandComposer.Compose(
            navigation, learner, true);
        Assert.That(visible.MoveX, Is.EqualTo(0.25f));
        Assert.That(visible.MoveZ, Is.EqualTo(0.75f));
        Assert.That(visible.Turn, Is.EqualTo(0.4f));
        Assert.That(visible.LookPitch, Is.EqualTo(0.3f));
        Assert.That(visible.Shoot, Is.True);
        Assert.That(visible.Jump, Is.True);
        Assert.That(visible.Crouch, Is.True);
    }

    [Test]
    public void UnifiedPolicyFactoriesDeclareDirectOwnerContracts()
    {
        PolicySpec training = PolicySpec.UnifiedTraining();
        Assert.That(training.Kind, Is.EqualTo(GambitPolicyKind.MlAgents));
        Assert.That(training.ObservationSchema,
            Is.EqualTo(UnifiedFairObservationV2Contract.TokenSchemaId));
        Assert.That(training.ActionSchema,
            Is.EqualTo(UnifiedFairObservationV2Contract.ActionSchemaId));

        PolicySpec onnx = PolicySpec.UnifiedOnnx();
        Assert.That(onnx.Kind, Is.EqualTo(GambitPolicyKind.Onnx));
        Assert.That(onnx.ObservationSchema,
            Is.EqualTo(UnifiedFairObservationV2Contract.SchemaId));
        Assert.That(onnx.ActionSchema,
            Is.EqualTo(UnifiedFairObservationV2Contract.ActionSchemaId));
    }
}

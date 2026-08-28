using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Supported training lifecycle: observes arena episodes, writes stable results,
/// and resets matches. PPO/IQL/PSRO algorithms remain outside Unity.
/// </summary>
public sealed class TrainingSessionCoordinator : MonoBehaviour
{
    private TrainingSpec training;
    private MatchSpec match;
    private OutputSpec output;
    private IRunArtifactStore artifacts;
    private readonly Dictionary<MatchManager, ArenaSession> sessions = new Dictionary<MatchManager, ArenaSession>();
    private int completedEpisodes;

    public int CompletedEpisodes => completedEpisodes;
    public event Action<MatchResult> EpisodeCompleted;
    public event Action SessionCompleted;

    public void Configure(TrainingSpec trainingSpec, MatchSpec matchSpec, OutputSpec outputSpec)
    {
        training = trainingSpec ?? throw new ArgumentNullException(nameof(trainingSpec));
        match = matchSpec ?? throw new ArgumentNullException(nameof(matchSpec));
        output = outputSpec ?? new OutputSpec();
    }

    private IEnumerator Start()
    {
        if (training == null || match == null)
        {
            Debug.LogError("[Training] Coordinator was not configured; disabling.");
            enabled = false;
            yield break;
        }

        yield return null; // GameModeBootstrapper creates arenas during Start.
        MatchManager[] managers = FindObjectsOfType<MatchManager>();
        if (managers.Length == 0)
        {
            Debug.LogError("[Training] No MatchManager instances were created.");
            enabled = false;
            yield break;
        }

        ConfigureAgentEpisodeTimeout();

        string root = Path.IsPathRooted(output.RootDirectory)
            ? output.RootDirectory
            : Path.Combine(Application.persistentDataPath, output.RootDirectory);
        artifacts = new RunArtifactStore(root, training.RunId);
        for (int areaId = 0; areaId < managers.Length; areaId++) Register(managers[areaId], areaId);
        artifacts.WriteJson("launch.json", new GambitLaunchConfig
        {
            Mode = GambitLaunchMode.Training,
            Match = match,
            Training = training,
            Output = output
        });
        Debug.Log($"[Training] run={training.RunId} arenas={managers.Length} output={artifacts.RunDirectory}");
    }

    private void ConfigureAgentEpisodeTimeout()
    {
        if (training.EpisodeTimeoutSeconds <= 0f) return;
        float stepSeconds = Mathf.Max(0.0001f, Time.fixedDeltaTime);
        int maxStep = Mathf.Max(1, Mathf.CeilToInt(training.EpisodeTimeoutSeconds / stepSeconds));
        GambitAgentController[] agents = FindObjectsOfType<GambitAgentController>();
        for (int index = 0; index < agents.Length; index++)
            agents[index].MaxStep = maxStep;
        Debug.Log($"[Training] agent episode timeout={training.EpisodeTimeoutSeconds:F2}s max_step={maxStep} agents={agents.Length}");
    }

    private void Register(MatchManager manager, int areaId)
    {
        ArenaSession session = new ArenaSession
        {
            AreaId = areaId,
            StartedAt = Time.realtimeSinceStartupAsDouble
        };
        session.MatchOverHandler = () => CompleteEpisode(manager, session);
        sessions.Add(manager, session);
        manager.OnMatchOver += session.MatchOverHandler;
    }

    private void CompleteEpisode(MatchManager manager, ArenaSession session)
    {
        completedEpisodes++;
        MatchResult result = MatchResultFactory.Capture(
            training.RunId,
            match,
            manager,
            session.AreaId,
            Time.realtimeSinceStartupAsDouble - session.StartedAt);
        if (training.WriteMetrics && output.WriteMatchResults)
            artifacts.WriteJson($"matches/episode-{completedEpisodes:D8}-area-{session.AreaId:D4}.json", result);
        EpisodeCompleted?.Invoke(result);

        if (training.MaxEpisodes > 0 && completedEpisodes >= training.MaxEpisodes)
        {
            SessionCompleted?.Invoke();
            enabled = false;
            return;
        }

        session.StartedAt = Time.realtimeSinceStartupAsDouble;
        manager.ResetMatch();
    }

    private void OnDestroy()
    {
        foreach (KeyValuePair<MatchManager, ArenaSession> pair in sessions)
            if (pair.Key != null) pair.Key.OnMatchOver -= pair.Value.MatchOverHandler;
        sessions.Clear();
    }

    private sealed class ArenaSession
    {
        public int AreaId;
        public double StartedAt;
        public Action MatchOverHandler;
    }
}

public static class MatchResultFactory
{
    public static MatchResult Capture(
        string runId,
        MatchSpec match,
        MatchManager manager,
        int areaId,
        double durationSeconds)
    {
        PlayerIdentity a = manager.PlayerA.Identity;
        PlayerIdentity b = manager.PlayerB.Identity;
        string aPolicy = match.PlayerA == null ? "unknown/a" : match.PlayerA.Id;
        string bPolicy = match.PlayerB == null ? "unknown/b" : match.PlayerB.Id;
        return new MatchResult
        {
            RunId = runId,
            MatchId = match.Id,
            MapId = match.MapId,
            Seed = match.Seed,
            AreaId = areaId,
            DurationSeconds = durationSeconds,
            WinnerPolicyId = a.Kills == b.Kills ? "draw" : (a.Kills > b.Kills ? aPolicy : bPolicy),
            PlayerA = CapturePlayer(aPolicy, a),
            PlayerB = CapturePlayer(bPolicy, b)
        };
    }

    private static PlayerMatchResult CapturePlayer(string policyId, PlayerIdentity identity)
    {
        return new PlayerMatchResult
        {
            PolicyId = policyId,
            Score = identity.Score,
            Kills = identity.Kills,
            Deaths = identity.Deaths,
            Hits = identity.Hits
        };
    }
}

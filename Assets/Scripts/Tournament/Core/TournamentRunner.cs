using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Deterministic matchup scheduler and result ledger. The host handles scene/match
/// recreation when NextMatchRequested fires, keeping PPO/PSRO logic outside Unity.
/// </summary>
public sealed class TournamentRunner : MonoBehaviour
{
    private TournamentSpec tournament;
    private MatchSpec template;
    private OutputSpec output;
    private List<MatchSpec> schedule;
    private IRunArtifactStore artifacts;
    private int nextIndex;
    private readonly List<MatchResult> results = new List<MatchResult>();

    public IReadOnlyList<MatchResult> Results => results;
    public event Action<MatchSpec> NextMatchRequested;
    public event Action TournamentCompleted;

    public void Configure(TournamentSpec tournamentSpec, MatchSpec matchTemplate, OutputSpec outputSpec)
    {
        tournament = tournamentSpec ?? throw new ArgumentNullException(nameof(tournamentSpec));
        template = matchTemplate ?? throw new ArgumentNullException(nameof(matchTemplate));
        output = outputSpec ?? new OutputSpec();
        schedule = TournamentSchedule.Expand(tournament, template);
    }

    private void Start()
    {
        if (schedule == null)
        {
            Debug.LogError("[Tournament] Runner was not configured; disabling.");
            enabled = false;
            return;
        }
        string root = Path.IsPathRooted(output.RootDirectory)
            ? output.RootDirectory
            : Path.Combine(Application.persistentDataPath, output.RootDirectory);
        artifacts = new RunArtifactStore(root, tournament.RunId);
        artifacts.WriteJson("tournament.json", tournament);
        RequestNext();
    }

    public void SubmitResult(MatchResult result)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));
        results.Add(result);
        artifacts.WriteJson($"matches/match-{results.Count:D8}.json", result);
        RequestNext();
    }

    private void RequestNext()
    {
        if (nextIndex >= schedule.Count)
        {
            artifacts.WriteJson("summary.json", new TournamentResultSet { Results = results });
            TournamentCompleted?.Invoke();
            enabled = false;
            return;
        }
        MatchSpec next = schedule[nextIndex++];
        if (NextMatchRequested == null)
            Debug.LogWarning("[Tournament] No match host subscribed; next match is pending: " + next.Id);
        NextMatchRequested?.Invoke(next);
    }
}

[Serializable]
public sealed class TournamentResultSet
{
    public List<MatchResult> Results = new List<MatchResult>();
}

public static class TournamentSchedule
{
    public static List<MatchSpec> Expand(TournamentSpec tournament, MatchSpec template)
    {
        if (tournament == null) throw new ArgumentNullException(nameof(tournament));
        if (template == null) throw new ArgumentNullException(nameof(template));
        List<MatchSpec> result = new List<MatchSpec>();
        foreach (MatchupSpec matchup in tournament.Matchups)
        {
            if (matchup == null || matchup.PlayerA == null || matchup.PlayerB == null) continue;
            int repetitions = Math.Max(1, matchup.Repetitions);
            for (int repetition = 0; repetition < repetitions; repetition++)
            {
                result.Add(Create(template, matchup, repetition, false));
                if (matchup.SwapSides) result.Add(Create(template, matchup, repetition, true));
            }
        }
        return result;
    }

    private static MatchSpec Create(MatchSpec template, MatchupSpec matchup, int repetition, bool swapped)
    {
        MatchSpec match = template.Copy();
        match.Id = $"{matchup.Id}/r{repetition:D4}/" + (swapped ? "ba" : "ab");
        match.Seed = template.Seed + repetition;
        match.PlayerA = (swapped ? matchup.PlayerB : matchup.PlayerA).Copy();
        match.PlayerB = (swapped ? matchup.PlayerA : matchup.PlayerB).Copy();
        return match;
    }
}

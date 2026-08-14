using UnityEngine;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// Stub recording manager that wraps the existing DatasetExporter for future
/// observer-camera recording of bot matches. Currently only implements match
/// event logging to JSON.
///
/// Recording modes per plan:
///   ObserverCamera, HumanCamera, PlayerACamera, PlayerBCamera, None
/// </summary>
public class RecordingManager : MonoBehaviour
{
    public enum RecordingMode
    {
        None,
        ObserverCamera,
        HumanCamera,
        PlayerACamera,
        PlayerBCamera
    }

    [Header("Recording")]
    public RecordingMode Mode = RecordingMode.None;
    public string OutputFolder = "Match_Recordings";
    public bool LogMatchEvents = true;

    private MatchManager matchManager;
    private List<MatchEvent> eventLog = new List<MatchEvent>();
    private float matchStartTime;

    [System.Serializable]
    public class MatchEvent
    {
        public float time;
        public string eventType;
        public string shooter;
        public string victim;
        public int victimHealthAfter;
        public float shooterScoreAfter;
    }

    public void Initialize(MatchManager mm)
    {
        matchManager = mm;
        matchStartTime = Time.time;

        if (!LogMatchEvents || mm == null) return;

        mm.OnHit += (shooter, victim) =>
        {
            var playerBody = mm.GetPlayerBody(victim);
            eventLog.Add(new MatchEvent
            {
                time = Time.time - matchStartTime,
                eventType = "hit",
                shooter = shooter.DisplayName,
                victim = victim.DisplayName,
                victimHealthAfter = playerBody != null ? playerBody.Health.CurrentHealth : 0,
                shooterScoreAfter = shooter.Score
            });
        };

        mm.OnMiss += (shooter) =>
        {
            eventLog.Add(new MatchEvent
            {
                time = Time.time - matchStartTime,
                eventType = "miss",
                shooter = shooter.DisplayName,
                shooterScoreAfter = shooter.Score
            });
        };

        mm.OnKill += (killer, victim) =>
        {
            eventLog.Add(new MatchEvent
            {
                time = Time.time - matchStartTime,
                eventType = "kill",
                shooter = killer.DisplayName,
                victim = victim.DisplayName,
                shooterScoreAfter = killer.Score
            });
        };

        mm.OnCooldownStarted += (duration) =>
        {
            eventLog.Add(new MatchEvent
            {
                time = Time.time - matchStartTime,
                eventType = "cooldown_started"
            });
        };

        mm.OnCooldownEnded += () =>
        {
            eventLog.Add(new MatchEvent
            {
                time = Time.time - matchStartTime,
                eventType = "cooldown_ended"
            });
        };

        mm.OnRoundReset += () =>
        {
            eventLog.Add(new MatchEvent
            {
                time = Time.time - matchStartTime,
                eventType = "round_reset"
            });
        };

        mm.OnMatchReset += () =>
        {
            eventLog.Clear();
            matchStartTime = Time.time;
            eventLog.Add(new MatchEvent
            {
                time = 0f,
                eventType = "match_reset"
            });
        };

        mm.OnMatchOver += () =>
        {
            eventLog.Add(new MatchEvent
            {
                time = Time.time - matchStartTime,
                eventType = "match_over"
            });
            SaveEventLog();
        };

        Debug.Log("[RecordingManager] Match event logging enabled.");
    }

    private void OnApplicationQuit()
    {
        if (LogMatchEvents && eventLog.Count > 0)
        {
            SaveEventLog();
        }
    }

    private void SaveEventLog()
    {
        string folder = Path.IsPathRooted(OutputFolder)
            ? OutputFolder
            : Path.Combine(Application.dataPath, OutputFolder);

        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string filePath = Path.Combine(folder, $"match_log_{timestamp}.json");

        // Simple JSON array serialization
        string json = "[\n";
        for (int i = 0; i < eventLog.Count; i++)
        {
            json += "  " + JsonUtility.ToJson(eventLog[i]);
            if (i < eventLog.Count - 1) json += ",";
            json += "\n";
        }
        json += "]";

        File.WriteAllText(filePath, json);
        Debug.Log($"[RecordingManager] Match event log saved: {filePath} ({eventLog.Count} events)");
    }
}

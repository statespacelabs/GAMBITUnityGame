using UnityEngine;

/// <summary>
/// Stores ownership and scoring identity for a player.
/// The MatchManager owns all score/stat mutations — controllers must never
/// modify these values directly.
/// </summary>
public class PlayerIdentity : MonoBehaviour
{
    [Header("Identity")]
    public string PlayerId = "Player";
    public string DisplayName = "Player";
    public int PlayerIndex = 0;

    [Header("Stats (read-only at runtime — managed by MatchManager)")]
    public float Score;
    public int Hits;
    public int Kills;
    public int Deaths;

    /// <summary> Team color applied to this player's visible mesh. </summary>
    [HideInInspector] public Color TeamColor = Color.white;

    /// <summary>
    /// Resets all match statistics to zero. Called by MatchManager on match reset.
    /// </summary>
    public void ResetStats()
    {
        Score = 0f;
        Hits = 0;
        Kills = 0;
        Deaths = 0;
    }

    public override string ToString()
    {
        return $"{DisplayName} (P{PlayerIndex}) — Score:{Score:F1} K:{Kills} D:{Deaths} H:{Hits}";
    }
}

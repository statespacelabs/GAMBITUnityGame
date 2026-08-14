using UnityEngine;

/// <summary>
/// Central configuration ScriptableObject holding all gameplay rule values.
/// No rule values should be hardcoded across multiple scripts — everything
/// reads from this single source of truth.
/// </summary>
[CreateAssetMenu(menuName = "BotArena/MatchConfig", fileName = "MatchConfig")]
public class MatchConfig : ScriptableObject
{
    [Header("Health")]
    public int MaxHealth = 100;
    public int DamagePerHit = 20;

    [Header("Scoring")]
    public float ScorePerHit = 0.2f;
    public float ScorePerKill = 1.0f;

    [Header("Cooldown (invulnerability period — players can move but cannot shoot or take damage)")]
    public float GlobalCooldownSeconds = 3.0f;
    [Tooltip("Players can always move during cooldown to reposition and avoid camp-sniping")]
    public bool AllowMovementDuringCooldown = true;

    [Header("Round Reset")]
    public bool ResetPositionsAfterKill = true;
    public bool ResetBothHealthAfterKill = true;

    [Header("Weapon")]
    public float WeaponRange = 100f;
    public float WeaponFireCooldownSeconds = 0.2f;

    [Header("Match")]
    public int KillsPerMatch = 5;

    [Header("RL Rewards (training only)")]
    public float RewardHitOpponent = 0.2f;
    public float RewardKillOpponent = 1.0f;
    public float RewardGetHit = -0.2f;
    public float RewardDie = -1.0f;
    public float RewardMiss = -0.01f;
    public float RewardTimestep = -0.001f;
}

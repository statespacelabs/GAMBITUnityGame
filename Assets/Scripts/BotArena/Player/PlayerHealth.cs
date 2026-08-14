using UnityEngine;

/// <summary>
/// Manages the player's health pool and damage intake.
///
/// Rules per plan:
///   - Does NOT award score directly (delegates to MatchManager)
///   - Does NOT start cooldown directly (delegates to MatchManager)
///   - Does NOT reset positions directly (delegates to MatchManager)
///   - Self-hits are rejected (no damage, no score, no reward)
///   - Damage during cooldown is rejected
/// </summary>
public class PlayerHealth : MonoBehaviour
{
    [Header("Health (set by GameModeBootstrapper from MatchConfig)")]
    public int MaxHealth = 100;

    /// <summary> Current health. Managed by this component and MatchManager. </summary>
    [HideInInspector] public int CurrentHealth;

    private MatchManager matchManager;
    private PlayerIdentity ownerIdentity;

    private void Awake()
    {
        ownerIdentity = GetComponent<PlayerIdentity>();
        CurrentHealth = MaxHealth;
    }

    public void SetMatchManager(MatchManager mm)
    {
        matchManager = mm;
    }

    /// <summary>
    /// Called by PlayerWeapon when this player is hit by a projectile.
    /// Validates the hit, applies damage, and delegates events to MatchManager.
    /// </summary>
    /// <param name="amount">Damage amount (typically DamagePerHit from MatchConfig).</param>
    /// <param name="attacker">Identity of the player who fired the shot.</param>
    public void TakeDamage(int amount, PlayerIdentity attacker)
    {
        // Reject damage during cooldown
        if (matchManager != null && !matchManager.CanPlayerTakeDamage(ownerIdentity))
            return;

        // Reject self-damage
        if (attacker == ownerIdentity)
            return;

        CurrentHealth -= amount;
        CurrentHealth = Mathf.Max(CurrentHealth, 0);

        Debug.Log($"[Health] {ownerIdentity.DisplayName} took {amount} damage from {attacker.DisplayName}. HP: {CurrentHealth}/{MaxHealth}");

        // Notify MatchManager of the hit (scoring happens there)
        if (matchManager != null)
        {
            matchManager.RegisterHit(attacker, ownerIdentity);

            // Check for kill
            if (CurrentHealth <= 0)
            {
                matchManager.RegisterKill(attacker, ownerIdentity);
            }
        }
    }

    /// <summary>
    /// Resets health to maximum. Called by MatchManager during round reset.
    /// </summary>
    public void ResetHealth()
    {
        CurrentHealth = MaxHealth;
    }

    /// <summary>
    /// Configures health parameters from a MatchConfig.
    /// </summary>
    public void Configure(MatchConfig config)
    {
        MaxHealth = config.MaxHealth;
        CurrentHealth = MaxHealth;
    }

    /// <summary> Returns current health as a 0-1 fraction. </summary>
    public float GetHealthNormalized()
    {
        return MaxHealth > 0 ? (float)CurrentHealth / MaxHealth : 0f;
    }
}

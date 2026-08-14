using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// ML-Agents RL controller. Implements IPlayerController to output PlayerCommand
/// and extends Agent for ML-Agents training integration.
///
/// Action Space (Discrete):
///   Branch 0 — Movement (5): none, forward, backward, strafe left, strafe right
///   Branch 1 — Turn (3): none, turn left, turn right
///   Branch 2 — Shoot (2): no shoot, shoot
///
/// Observation Space (12 continuous floats):
///   own health normalized, opponent health normalized,
///   relative opponent position (local x, z), distance normalized,
///   forward dot to opponent, right dot to opponent,
///   can shoot (bool→float), cooldown active (bool→float),
///   cooldown remaining normalized, line of sight (bool→float),
///   aim alignment (dot product)
///
/// Reward signals are delivered via MatchManager.OnRewardSignal events.
/// The agent does NOT directly change health, score, position, opponent state,
/// or cooldown state — it only outputs PlayerCommand.
/// </summary>
public class RLAgentController : Agent, IPlayerController
{
    private PlayerIdentity identity;
    private MatchManager matchManager;
    private PlayerBody selfBody;
    private PlayerBody opponentBody;
    private PlayerCommand currentCommand;

    // Cached references for observations
    private PlayerHealth selfHealth;
    private PlayerHealth opponentHealth;

    public void Initialize(PlayerIdentity identity, MatchManager matchManager)
    {
        this.identity = identity;
        this.matchManager = matchManager;

        selfBody = identity.GetComponent<PlayerBody>();
        opponentBody = matchManager.GetOpponentBody(identity);

        selfHealth = selfBody.Health;
        opponentHealth = opponentBody?.Health;

        // Subscribe to reward signals
        matchManager.OnRewardSignal += OnRewardReceived;

        // Subscribe to kill events for episode end
        matchManager.OnKill += OnKillEvent;

        Debug.Log($"[RLAgent] Initialized for {identity.DisplayName}");
    }

    private void OnDestroy()
    {
        if (matchManager != null)
        {
            matchManager.OnRewardSignal -= OnRewardReceived;
            matchManager.OnKill -= OnKillEvent;
        }
    }

    // ─────────────────────────────────────────────────────
    // IPlayerController Implementation
    // ─────────────────────────────────────────────────────

    public PlayerCommand GetCommand()
    {
        return currentCommand;
    }

    // ─────────────────────────────────────────────────────
    // ML-Agents Agent Overrides
    // ─────────────────────────────────────────────────────

    public override void OnEpisodeBegin()
    {
        currentCommand = PlayerCommand.NoOp;

        // Reset match state
        if (matchManager != null)
        {
            matchManager.ResetRound();
        }
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (opponentBody == null || selfBody == null)
        {
            // Safe defaults: 12 zeros
            for (int i = 0; i < 12; i++) sensor.AddObservation(0f);
            return;
        }

        Transform self = selfBody.transform;
        Transform opp = opponentBody.transform;

        // 1. Own health normalized (0-1)
        sensor.AddObservation(selfHealth.GetHealthNormalized());

        // 2. Opponent health normalized (0-1)
        sensor.AddObservation(opponentHealth.GetHealthNormalized());

        // 3-4. Relative opponent position in local space (x, z)
        Vector3 relativePos = self.InverseTransformPoint(opp.position);
        sensor.AddObservation(relativePos.x / 50f);  // Normalize by arena half-size
        sensor.AddObservation(relativePos.z / 50f);

        // 5. Distance to opponent normalized
        float distance = Vector3.Distance(self.position, opp.position);
        sensor.AddObservation(Mathf.Clamp01(distance / 100f));

        // 6. Forward dot to opponent direction
        Vector3 dirToOpp = (opp.position - self.position).normalized;
        sensor.AddObservation(Vector3.Dot(self.forward, dirToOpp));

        // 7. Right dot to opponent direction
        sensor.AddObservation(Vector3.Dot(self.right, dirToOpp));

        // 8. Can shoot (1.0 or 0.0)
        bool canShoot = matchManager != null && matchManager.CanPlayerShoot(identity);
        sensor.AddObservation(canShoot ? 1f : 0f);

        // 9. Cooldown active (1.0 or 0.0)
        sensor.AddObservation(matchManager != null && matchManager.IsInCooldown ? 1f : 0f);

        // 10. Cooldown remaining normalized
        float cdRemaining = matchManager != null ? matchManager.CooldownRemaining : 0f;
        float cdMax = matchManager != null && matchManager.Config != null ? matchManager.Config.GlobalCooldownSeconds : 3f;
        sensor.AddObservation(cdMax > 0 ? cdRemaining / cdMax : 0f);

        // 11. Line of sight to opponent
        bool hasLineOfSight = false;
        RaycastHit hit;
        if (Physics.Raycast(self.position + Vector3.up * 1.5f, dirToOpp, out hit, 100f))
        {
            PlayerHealth hitHealth = hit.collider.GetComponentInParent<PlayerHealth>();
            if (hitHealth != null && hitHealth == opponentHealth)
            {
                hasLineOfSight = true;
            }
        }
        sensor.AddObservation(hasLineOfSight ? 1f : 0f);

        // 12. Aim alignment (dot product of forward with direction to opponent, 0-1 range)
        float aimAlignment = Mathf.Max(0f, Vector3.Dot(self.forward, dirToOpp));
        sensor.AddObservation(aimAlignment);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // Decode discrete actions into PlayerCommand
        int moveAction = actions.DiscreteActions[0];  // 0=none, 1=forward, 2=back, 3=left, 4=right
        int turnAction = actions.DiscreteActions[1];  // 0=none, 1=left, 2=right
        int shootAction = actions.DiscreteActions[2]; // 0=no, 1=yes

        float moveX = 0f, moveZ = 0f, turn = 0f;

        switch (moveAction)
        {
            case 1: moveZ = 1f; break;   // Forward
            case 2: moveZ = -1f; break;  // Backward
            case 3: moveX = -1f; break;  // Strafe left
            case 4: moveX = 1f; break;   // Strafe right
        }

        switch (turnAction)
        {
            case 1: turn = -1f; break;  // Turn left
            case 2: turn = 1f; break;   // Turn right
        }

        currentCommand = new PlayerCommand
        {
            MoveX = moveX,
            MoveZ = moveZ,
            Turn = turn,
            Shoot = shootAction == 1
        };
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // Fallback heuristic: use WASD + mouse for debugging
        var da = actionsOut.DiscreteActions;

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        if (v > 0.3f) da[0] = 1;
        else if (v < -0.3f) da[0] = 2;
        else if (h < -0.3f) da[0] = 3;
        else if (h > 0.3f) da[0] = 4;
        else da[0] = 0;

        float mouseX = Input.GetAxis("Mouse X");
        if (mouseX < -0.1f) da[1] = 1;
        else if (mouseX > 0.1f) da[1] = 2;
        else da[1] = 0;

        da[2] = Input.GetButton("Fire1") ? 1 : 0;
    }

    // ─────────────────────────────────────────────────────
    // Reward and Episode Handling
    // ─────────────────────────────────────────────────────

    private void OnRewardReceived(PlayerIdentity player, float reward, string reason)
    {
        if (player != identity) return;
        AddReward(reward);
    }

    private void OnKillEvent(PlayerIdentity killer, PlayerIdentity victim)
    {
        // One kill = one episode for training
        if (killer == identity || victim == identity)
        {
            EndEpisode();
        }
    }
}

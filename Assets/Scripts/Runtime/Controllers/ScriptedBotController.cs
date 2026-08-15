using UnityEngine;

/// <summary>
/// Scripted bot controller for debugging and opponent AI.
/// Supports multiple behavior modes selectable via the inspector.
///
/// Uses the same PlayerCommand interface as Human and RL controllers.
/// Must never directly call TakeDamage, edit score, or teleport the opponent.
/// </summary>
public class ScriptedBotController : MonoBehaviour, IPlayerController
{
    public enum ScriptedBotMode
    {
        Idle,
        RandomStrafe,
        StrafeAndFace,
        StrafeAndFaceShoot,
        FaceOpponent,
        FaceOpponentAndShoot,
        HoldAngleShoot,
        ChaseOpponent,
        RetreatAndShoot,
        DoorwayCrossAndStop,
        CoverDirectionChange,
        TwoObstacleRetreat,
        KiteThroughCover,
        SlowHoldAngleShoot,
        KiteThroughCoverShoot
    }

    [Header("Behavior")]
    public ScriptedBotMode BotMode = ScriptedBotMode.FaceOpponentAndShoot;

    [Header("Timing")]
    [Tooltip("How often the bot re-evaluates its strafe direction (seconds)")]
    public float StrafeChangeInterval = 1.5f;

    [Tooltip("How close the bot needs to aim before it shoots (degrees)")]
    public float AimThresholdDegrees = 15f;

    [Tooltip("Turn speed multiplier (1.0 = full speed from PlayerMotor)")]
    public float TurnGain = 1.0f;

    private PlayerIdentity identity;
    private MatchManager matchManager;
    private PlayerIdentity opponent;
    private Transform opponentTransform;

    private float strafeDirection = 1f;
    private float nextStrafeChangeTime = 0f;
    private int debugShotLogCount = 0;
    private ScriptedBotMode previousMode;
    private float modeStartedAt;

    public void Initialize(PlayerIdentity identity, MatchManager matchManager)
    {
        this.identity = identity;
        this.matchManager = matchManager;

        if (matchManager != null)
        {
            opponent = matchManager.GetOpponent(identity);
            if (opponent != null)
            {
                opponentTransform = opponent.transform;
            }
        }
        previousMode = BotMode;
        modeStartedAt = Time.time;
    }

    public PlayerCommand GetCommand()
    {
        if (opponentTransform == null || matchManager == null)
        {
            if (matchManager != null && identity != null)
            {
                opponent = matchManager.GetOpponent(identity);
                if (opponent != null) opponentTransform = opponent.transform;
            }
            return PlayerCommand.NoOp;
        }

        if (previousMode != BotMode)
        {
            previousMode = BotMode;
            modeStartedAt = Time.time;
        }

        switch (BotMode)
        {
            case ScriptedBotMode.Idle:
                return PlayerCommand.NoOp;

            case ScriptedBotMode.RandomStrafe:
                return DoRandomStrafe();

            case ScriptedBotMode.StrafeAndFace:
                return DoStrafeAndFace();

            case ScriptedBotMode.StrafeAndFaceShoot:
                return DoStrafeAndFaceShoot();

            case ScriptedBotMode.FaceOpponent:
                return DoFaceOpponent(false);

            case ScriptedBotMode.FaceOpponentAndShoot:
                return DoFaceOpponent(true);

            case ScriptedBotMode.HoldAngleShoot:
                return DoHoldAngleShoot();

            case ScriptedBotMode.ChaseOpponent:
                return DoChaseOpponent();

            case ScriptedBotMode.RetreatAndShoot:
                return DoRetreatAndShoot();

            case ScriptedBotMode.DoorwayCrossAndStop:
                return DoDoorwayCrossAndStop();

            case ScriptedBotMode.CoverDirectionChange:
                return DoCoverDirectionChange();

            case ScriptedBotMode.TwoObstacleRetreat:
                return DoTwoObstacleRetreat();

            case ScriptedBotMode.KiteThroughCover:
                return DoKiteThroughCover();

            case ScriptedBotMode.SlowHoldAngleShoot:
                return DoSlowHoldAngleShoot();

            case ScriptedBotMode.KiteThroughCoverShoot:
                return DoKiteThroughCoverShoot();

            default:
                return PlayerCommand.NoOp;
        }
    }

    // ─────────────────────────────────────────────────────
    // Behavior Implementations
    // ─────────────────────────────────────────────────────

    private PlayerCommand DoRandomStrafe()
    {
        if (Time.time >= nextStrafeChangeTime)
        {
            strafeDirection = Random.value > 0.5f ? 1f : -1f;
            nextStrafeChangeTime = Time.time + StrafeChangeInterval;
        }

        return new PlayerCommand
        {
            MoveX = strafeDirection,
            MoveZ = 0f,
            Turn = 0f,
            Shoot = false
        };
    }

    /// <summary>
    /// Lateral strafe like RandomStrafe, but continuously tracks and faces the opponent.
    /// Curriculum step between FaceOpponent (gentle strafe + track) and ChaseOpponent.
    /// </summary>
    private PlayerCommand DoStrafeAndFace()
    {
        if (Time.time >= nextStrafeChangeTime)
        {
            strafeDirection = Random.value > 0.5f ? 1f : -1f;
            nextStrafeChangeTime = Time.time + StrafeChangeInterval;
        }

        float turnAmount = CalculateTurnTowardsOpponent(out _);

        return new PlayerCommand
        {
            MoveX = strafeDirection,
            MoveZ = 0f,
            Turn = turnAmount,
            Shoot = false
        };
    }

    /// <summary>
    /// Full lateral strafe + continuous opponent tracking + shooting when aligned.
    /// Harder curriculum step after StrafeAndFace; uses normal PlayerWeapon path.
    /// </summary>
    private PlayerCommand DoStrafeAndFaceShoot()
    {
        if (Time.time >= nextStrafeChangeTime)
        {
            strafeDirection = Random.value > 0.5f ? 1f : -1f;
            nextStrafeChangeTime = Time.time + StrafeChangeInterval;
        }

        float turnAmount = CalculateTurnTowardsOpponent(out float aimAngle);
        bool shouldShoot = ShouldAllowShooting() && Mathf.Abs(aimAngle) < AimThresholdDegrees;

        if (shouldShoot && debugShotLogCount < 5)
        {
            debugShotLogCount++;
            Debug.Log(
                $"[ScriptedBot] StrafeAndFaceShoot shoot attempt #{debugShotLogCount} "
                + $"aimAngle={aimAngle:F1}° player={identity?.PlayerId}"
            );
        }

        return new PlayerCommand
        {
            MoveX = strafeDirection,
            MoveZ = 0f,
            Turn = turnAmount,
            Shoot = shouldShoot
        };
    }

    private PlayerCommand DoFaceOpponent(bool shootWhenAligned)
    {
        float turnAmount = CalculateTurnTowardsOpponent(out float aimAngle);

        bool shouldShoot = false;
        if (shootWhenAligned
            && ShouldAllowShooting()
            && Mathf.Abs(aimAngle) < AimThresholdDegrees)
        {
            shouldShoot = true;
        }

        if (Time.time >= nextStrafeChangeTime)
        {
            strafeDirection = Random.value > 0.5f ? 1f : -1f;
            nextStrafeChangeTime = Time.time + StrafeChangeInterval;
        }

        return new PlayerCommand
        {
            MoveX = strafeDirection * 0.5f,
            MoveZ = 0f,
            Turn = turnAmount,
            Shoot = shouldShoot
        };
    }

    /// <summary>Armed curriculum opponent that holds position and tracks its angle.</summary>
    private PlayerCommand DoHoldAngleShoot()
    {
        float turnAmount = CalculateTurnTowardsOpponent(out float aimAngle);
        return new PlayerCommand
        {
            MoveX = 0f,
            MoveZ = 0f,
            Turn = turnAmount,
            Shoot = ShouldAllowShooting() && Mathf.Abs(aimAngle) < AimThresholdDegrees
        };
    }

    /// <summary>Lower-turn-rate hold-angle opponent for the P1 cover curriculum.</summary>
    private PlayerCommand DoSlowHoldAngleShoot()
    {
        float turnAmount = CalculateTurnTowardsOpponent(out float aimAngle) * 0.35f;
        return new PlayerCommand
        {
            MoveX = 0f,
            MoveZ = 0f,
            Turn = turnAmount,
            Shoot = ShouldAllowShooting() && Mathf.Abs(aimAngle) < AimThresholdDegrees
        };
    }

    private PlayerCommand DoChaseOpponent()
    {
        float turnAmount = CalculateTurnTowardsOpponent(out float aimAngle);
        float distance = Vector3.Distance(transform.position, opponentTransform.position);

        float moveForward = 0f;
        if (distance > 5f)
        {
            moveForward = 1f;
        }

        return new PlayerCommand
        {
            MoveX = 0f,
            MoveZ = moveForward,
            Turn = turnAmount,
            Shoot = Mathf.Abs(aimAngle) < AimThresholdDegrees
        };
    }

    private PlayerCommand DoRetreatAndShoot()
    {
        float turnAmount = CalculateTurnTowardsOpponent(out float aimAngle);
        float distance = Vector3.Distance(transform.position, opponentTransform.position);

        float moveForward = 0f;
        if (distance < 8f)
        {
            moveForward = -1f;
        }

        if (Time.time >= nextStrafeChangeTime)
        {
            strafeDirection = Random.value > 0.5f ? 1f : -1f;
            nextStrafeChangeTime = Time.time + StrafeChangeInterval;
        }

        return new PlayerCommand
        {
            MoveX = strafeDirection * 0.7f,
            MoveZ = moveForward,
            Turn = turnAmount,
            Shoot = Mathf.Abs(aimAngle) < AimThresholdDegrees
        };
    }

    private PlayerCommand DoDoorwayCrossAndStop()
    {
        float elapsed = Time.time - modeStartedAt;
        float turnAmount = CalculateTurnTowardsOpponent(out _);
        return new PlayerCommand
        {
            MoveX = elapsed < 2.6f ? 1f : 0f,
            MoveZ = 0f,
            Turn = turnAmount,
            Shoot = false
        };
    }

    private PlayerCommand DoCoverDirectionChange()
    {
        float phase = Mathf.Repeat(Time.time - modeStartedAt, 6f);
        float turnAmount = CalculateTurnTowardsOpponent(out _);
        return new PlayerCommand
        {
            MoveX = phase < 2f ? 1f : (phase < 4f ? -1f : 0.35f),
            MoveZ = phase >= 4f ? -0.45f : 0f,
            Turn = turnAmount,
            Shoot = false
        };
    }

    private PlayerCommand DoTwoObstacleRetreat()
    {
        float phase = Mathf.Repeat(Time.time - modeStartedAt, 5f);
        float turnAmount = CalculateTurnTowardsOpponent(out _);
        return new PlayerCommand
        {
            MoveX = phase < 2.5f ? 0.75f : -0.75f,
            MoveZ = -1f,
            Turn = turnAmount,
            Shoot = false
        };
    }

    private PlayerCommand DoKiteThroughCover()
    {
        float elapsed = Time.time - modeStartedAt;
        float turnAmount = CalculateTurnTowardsOpponent(out _);
        return new PlayerCommand
        {
            MoveX = Mathf.Sin(elapsed * 1.8f),
            MoveZ = -0.8f,
            Turn = turnAmount,
            Shoot = false
        };
    }

    /// <summary>Cover-using P4 opponent; all damage still uses PlayerWeapon.</summary>
    private PlayerCommand DoKiteThroughCoverShoot()
    {
        float elapsed = Time.time - modeStartedAt;
        float turnAmount = CalculateTurnTowardsOpponent(out float aimAngle);
        return new PlayerCommand
        {
            MoveX = Mathf.Sin(elapsed * 1.8f),
            MoveZ = -0.8f,
            Turn = turnAmount,
            Shoot = ShouldAllowShooting() && Mathf.Abs(aimAngle) < AimThresholdDegrees
        };
    }

    private bool ShouldAllowShooting()
    {
        if (!ScriptedShootPressure.ShootEnabled)
            return false;
        if (ScriptedShootPressure.IsWarmupActive)
            return false;
        return true;
    }

    private float CalculateTurnTowardsOpponent(out float aimAngle)
    {
        Vector3 dirToOpponent = (opponentTransform.position - transform.position).normalized;
        dirToOpponent.y = 0f;

        if (dirToOpponent.sqrMagnitude < 0.001f)
        {
            aimAngle = 0f;
            return 0f;
        }

        aimAngle = Vector3.SignedAngle(transform.forward, dirToOpponent, Vector3.up);
        float turnAmount = Mathf.Clamp(aimAngle / 45f, -1f, 1f) * TurnGain;
        return turnAmount;
    }
}

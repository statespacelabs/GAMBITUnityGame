using UnityEngine;

/// <summary>
/// Handles player movement and turning. Uses CharacterController for 
/// grounded, gravity-aware movement without physics rigidbody complexity.
///
/// Accepts normalized command inputs from PlayerBody:
///   MoveX:     strafe left/right
///   MoveZ:     forward/back
///   Turn:      yaw rotation (look_dx)
///   LookPitch: pitch rotation (look_dy)
///   Jump/Crouch: vertical actions
///
/// Yaw and pitch are tracked as absolute accumulators and written to the body
/// transform as Euler(pitch, yaw, 0). Horizontal movement uses a yaw-only
/// (ground-projected) basis so looking up/down never drives the body into the
/// floor. Pitch is therefore observable via transform.eulerAngles.x, matching
/// the view telemetry the Phase 1 encoder was trained on.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerMotor : MonoBehaviour
{
    [Header("Movement")]
    public float MoveSpeed = 4f;

    [Header("Turning")]
    public float TurnSpeedDegrees = 180f;
    public float PitchSpeedDegrees = 120f;
    public float MaxPitchDegrees = 80f;

    [Header("Vertical")]
    public float Gravity = -9.81f;
    public float JumpSpeed = 5f;
    [Range(0.2f, 1f)] public float CrouchHeightScale = 0.5f;

    private CharacterController characterController;
    private float verticalVelocity = 0f;
    private float yawAngle = 0f;
    private float pitchAngle = 0f;
    private float standingHeight = 2f;

    public CollisionFlags LastCollisionFlags { get; private set; }
    public Vector3 LastCollisionNormalWorld { get; private set; }
    public int TotalCollisionSteps { get; private set; }
    public int TotalSideCollisionSteps { get; private set; }
    public bool IsCrouched => characterController != null && characterController.height < standingHeight * 0.75f;
    public Vector3 WorldVelocity => characterController != null ? characterController.velocity : Vector3.zero;

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();
        standingHeight = characterController.height;
        Vector3 e = transform.eulerAngles;
        yawAngle = e.y;
        pitchAngle = NormalizePitch(e.x);
    }

    /// <summary>
    /// Applies horizontal movement (with optional jump/crouch) on a yaw-only basis
    /// so body pitch does not tilt the movement direction.
    /// </summary>
    /// <param name="moveX">Strafe input: -1 = left, +1 = right.</param>
    /// <param name="moveZ">Forward input: -1 = backward, +1 = forward.</param>
    /// <param name="jump">Request a jump this frame (only applied when grounded).</param>
    /// <param name="crouch">Hold crouch (shrinks the capsule height).</param>
    public void ApplyMovement(float moveX, float moveZ, bool jump = false, bool crouch = false)
    {
        // Ground-projected facing basis so pitch never bends movement into the floor.
        Vector3 move = ComputeHorizontalMove(transform.rotation, moveX, moveZ) * MoveSpeed;

        // Crouch by scaling the controller height.
        characterController.height = crouch ? standingHeight * CrouchHeightScale : standingHeight;

        // Gravity + jump.
        if (characterController.isGrounded)
        {
            verticalVelocity = jump ? JumpSpeed : -0.5f;
        }
        else
        {
            verticalVelocity += Gravity * Time.deltaTime;
        }
        move.y = verticalVelocity;

        LastCollisionNormalWorld = Vector3.zero;
        LastCollisionFlags = characterController.Move(move * Time.deltaTime);
        if (LastCollisionFlags != CollisionFlags.None)
            TotalCollisionSteps++;
        if ((LastCollisionFlags & CollisionFlags.Sides) != 0)
            TotalSideCollisionSteps++;
    }

    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (hit != null)
            LastCollisionNormalWorld = hit.normal;
    }

    /// <summary>
    /// Applies yaw + pitch look. Pitch is clamped to [-MaxPitch, +MaxPitch] and
    /// written to the body transform so weapon aim, camera and the view telemetry
    /// all reflect look_dy.
    /// </summary>
    /// <param name="yaw">Yaw input (look_dx): -1 = left, +1 = right.</param>
    /// <param name="pitch">Pitch input (look_dy): -1 = down, +1 = up.</param>
    public void ApplyLook(float yaw, float pitch)
    {
        yawAngle = IntegrateYaw(yawAngle, yaw, TurnSpeedDegrees, Time.deltaTime);
        // +pitch means "look up", which is a negative Euler X in Unity.
        pitchAngle = IntegratePitch(
            pitchAngle, pitch, PitchSpeedDegrees, MaxPitchDegrees, Time.deltaTime);
        transform.rotation = Quaternion.Euler(pitchAngle, yawAngle, 0f);
    }

    public static Vector3 ComputeHorizontalMove(
        Quaternion rotation,
        float moveX,
        float moveZ)
    {
        Vector3 flatForward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up).normalized;
        Vector3 flatRight = Vector3.ProjectOnPlane(rotation * Vector3.right, Vector3.up).normalized;
        return flatRight * moveX + flatForward * moveZ;
    }

    public static float IntegrateYaw(float currentYaw, float input, float speedDegrees, float deltaTime)
    {
        return currentYaw + input * speedDegrees * Mathf.Max(0f, deltaTime);
    }

    public static float IntegratePitch(
        float currentPitch,
        float input,
        float speedDegrees,
        float maxPitchDegrees,
        float deltaTime)
    {
        // Positive command means look up, which is negative Unity Euler X.
        return Mathf.Clamp(
            currentPitch - input * speedDegrees * Mathf.Max(0f, deltaTime),
            -Mathf.Abs(maxPitchDegrees),
            Mathf.Abs(maxPitchDegrees));
    }

    /// <summary>
    /// Applies yaw rotation only. Retained for controllers that do not produce a
    /// pitch command (scripted/human). Equivalent to ApplyLook(turn, 0).
    /// </summary>
    /// <param name="turn">Turn input: -1 = left, +1 = right.</param>
    public void ApplyTurn(float turn)
    {
        ApplyLook(turn, 0f);
    }

    /// <summary>
    /// Teleports the player to a specific position and rotation.
    /// Used by MatchManager for spawn/reset.
    /// </summary>
    public void TeleportTo(Vector3 position, Quaternion rotation)
    {
        // CharacterController must be disabled to teleport
        characterController.enabled = false;
        transform.position = position;
        transform.rotation = rotation;
        verticalVelocity = 0f;
        yawAngle = rotation.eulerAngles.y;
        pitchAngle = NormalizePitch(rotation.eulerAngles.x);
        characterController.enabled = true;
    }

    /// <summary> Maps a 0..360 Euler X into a signed [-180, 180] pitch. </summary>
    private static float NormalizePitch(float eulerX)
    {
        return eulerX > 180f ? eulerX - 360f : eulerX;
    }
}

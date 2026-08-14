/// <summary>
/// Shared command struct that every controller produces and PlayerBody consumes.
/// This is the sole interface between brains and bodies — no controller may
/// bypass this struct to directly modify health, score, or position.
/// </summary>
public struct PlayerCommand
{
    /// <summary> Strafe axis: -1 = left, +1 = right. </summary>
    public float MoveX;

    /// <summary> Forward/back axis: -1 = backward, +1 = forward. </summary>
    public float MoveZ;

    /// <summary> Yaw turn axis (a.k.a. look_dx): -1 = turn left, +1 = turn right. </summary>
    public float Turn;

    /// <summary> Pitch look axis (look_dy): -1 = look down, +1 = look up. </summary>
    public float LookPitch;

    /// <summary> True on the frame the controller wants to fire. </summary>
    public bool Shoot;

    /// <summary> True on the frame the controller wants to reload. </summary>
    public bool Reload;

    /// <summary> True on the frame the controller wants to jump. </summary>
    public bool Jump;

    /// <summary> True while the controller wants to crouch. </summary>
    public bool Crouch;

    /// <summary> Returns a command with all fields zeroed (no movement, no shooting). </summary>
    public static PlayerCommand NoOp => new PlayerCommand
    {
        MoveX = 0f,
        MoveZ = 0f,
        Turn = 0f,
        LookPitch = 0f,
        Shoot = false,
        Reload = false,
        Jump = false,
        Crouch = false
    };
}

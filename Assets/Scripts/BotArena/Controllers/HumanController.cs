using System;
using UnityEngine;

/// <summary>
/// Human input controller. Reads Unity's legacy Input axes (WASD, Mouse X, Fire1)
/// and outputs PlayerCommand through the same interface as bot controllers.
///
/// Input mapping matches the existing InputManager.asset configuration:
///   - Horizontal (A/D or Arrow Left/Right) -> MoveX
///   - Vertical (W/S or Arrow Up/Down) -> MoveZ
///   - Mouse X -> Turn
///   - Fire1 (Left Mouse or Left Ctrl) -> Shoot
///
/// For Phase 4.5b operator dev smokes only, PHASE4_5_KEYBOARD_TURN=1 enables
/// keyboard yaw via Q/E and J/L, and PHASE4_5_KEYBOARD_SHOOT=1 enables
/// keyboard shooting via Space/K/Enter. These are off by default so normal
/// gameplay is unchanged outside the guarded live-smoke operator path.
/// </summary>
public class HumanController : MonoBehaviour, IPlayerController
{
    [Header("Mouse Sensitivity")]
    [Tooltip("Multiplier for Mouse X axis input")]
    public float MouseSensitivity = 3.0f;

    [Header("Key Bindings (Unity Input Axes)")]
    public string HorizontalAxis = "Horizontal";
    public string VerticalAxis = "Vertical";
    public string MouseXAxis = "Mouse X";
    public string FireButton = "Fire1";

    private PlayerIdentity identity;
    private MatchManager matchManager;
    private bool keyboardTurnEnabled;
    private bool keyboardShootEnabled;

    public void Initialize(PlayerIdentity identity, MatchManager matchManager)
    {
        this.identity = identity;
        this.matchManager = matchManager;
        keyboardTurnEnabled = Environment.GetEnvironmentVariable("PHASE4_5_KEYBOARD_TURN") == "1";
        keyboardShootEnabled = Environment.GetEnvironmentVariable("PHASE4_5_KEYBOARD_SHOOT") == "1";

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        Debug.Log($"[HumanController] Initialized for {identity.DisplayName}. Cursor locked. keyboardTurn={keyboardTurnEnabled} keyboardShoot={keyboardShootEnabled}");
    }

    public PlayerCommand GetCommand()
    {
        float moveX = Input.GetAxis(HorizontalAxis);
        float moveZ = Input.GetAxis(VerticalAxis);

        float mouseX = Input.GetAxis(MouseXAxis) * MouseSensitivity;
        float turn = Mathf.Clamp(mouseX, -1f, 1f);
        if (keyboardTurnEnabled)
        {
            float keyTurn = 0f;
            if (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.J))
            {
                keyTurn -= 1f;
            }
            if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.L))
            {
                keyTurn += 1f;
            }
            if (Mathf.Abs(keyTurn) > 0f)
            {
                turn = Mathf.Clamp(keyTurn, -1f, 1f);
            }
        }

        bool shoot = Input.GetButton(FireButton);
        if (keyboardShootEnabled)
        {
            shoot = shoot || Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.K) || Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter);
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        return new PlayerCommand
        {
            MoveX = moveX,
            MoveZ = moveZ,
            Turn = turn,
            Shoot = shoot
        };
    }

    private void OnDisable()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}

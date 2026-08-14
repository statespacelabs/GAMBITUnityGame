using UnityEngine;

/// <summary>
/// Phase 3AC patch: log blocked fire when shoot command is active but weapon does not fire.
/// </summary>
[RequireComponent(typeof(PlayerIdentity))]
[RequireComponent(typeof(PlayerMotor))]
[RequireComponent(typeof(PlayerWeapon))]
[RequireComponent(typeof(PlayerHealth))]
public class PlayerBody : MonoBehaviour
{
    [Header("Component References")]
    [SerializeField] private PlayerIdentity identity;
    [SerializeField] private PlayerMotor motor;
    [SerializeField] private PlayerWeapon weapon;
    [SerializeField] private PlayerHealth health;

    [Header("Controller")]
    [SerializeField] private MonoBehaviour controllerBehaviour;

    [Header("Visual")]
    [SerializeField] private MeshRenderer bodyRenderer;
    [SerializeField] private Transform cameraAnchor;

    private IPlayerController controller;
    private MatchManager matchManager;

    [HideInInspector] public bool IsLocalPlayer = false;

    public PlayerIdentity Identity => identity;
    public PlayerMotor Motor => motor;
    public PlayerWeapon Weapon => weapon;
    public PlayerHealth Health => health;
    public IPlayerController Controller => controller;
    public MatchManager MatchManager => matchManager;

    private void Awake()
    {
        if (identity == null) identity = GetComponent<PlayerIdentity>();
        if (motor == null) motor = GetComponent<PlayerMotor>();
        if (weapon == null) weapon = GetComponent<PlayerWeapon>();
        if (health == null) health = GetComponent<PlayerHealth>();
        if (bodyRenderer == null) bodyRenderer = GetComponentInChildren<MeshRenderer>();

        if (cameraAnchor == null)
        {
            GameObject anchorObj = new GameObject("CameraAnchor");
            anchorObj.transform.SetParent(transform);
            anchorObj.transform.localPosition = new Vector3(0f, 0.7f, 0f);
            anchorObj.transform.localRotation = Quaternion.identity;
            cameraAnchor = anchorObj.transform;
        }

        if (controllerBehaviour != null)
        {
            controller = controllerBehaviour as IPlayerController;
        }
    }

    private void Update()
    {
        if (Phase5HeadlessRuntime.Enabled)
            return;
        TickController();
    }

    private void FixedUpdate()
    {
        if (!Phase5HeadlessRuntime.Enabled)
            return;
        TickController();
    }

    private void TickController()
    {
        if (controller == null) return;

        PlayerCommand command = controller.GetCommand();
        command = Phase5GenericPrivilegedTeacher.ResolveCommand(this, command);

        if (matchManager != null && !matchManager.CanPlayerMove(identity))
        {
            command.MoveX = 0f;
            command.MoveZ = 0f;
            command.Turn = 0f;
            command.LookPitch = 0f;
            command.Jump = false;
        }

        motor.ApplyMovement(command.MoveX, command.MoveZ, command.Jump, command.Crouch);
        motor.ApplyLook(command.Turn, command.LookPitch);

        if (command.Reload && !PlayerWeapon.DebugDisableReload)
        {
            weapon.Reload();
        }

        if (command.Shoot)
        {
            FireAttemptResult result = weapon.TryFire(identity, true);
            if (!result.Fired)
            {
                weapon.MaybeLogBlockedFire(result, true);
            }
        }
    }

    public void SetController(MonoBehaviour newController)
    {
        controllerBehaviour = newController;
        controller = newController as IPlayerController;

        if (controller == null && newController != null)
        {
            Debug.LogWarning($"[PlayerBody] {identity.DisplayName}: assigned controller {newController.GetType().Name} does not implement IPlayerController!");
        }
        else if (controller != null)
        {
            controller.Initialize(identity, matchManager);
            Debug.Log($"[PlayerBody] {identity.DisplayName}: controller set to {newController.GetType().Name}");
        }
    }

    public void SetMatchManager(MatchManager mm)
    {
        matchManager = mm;
        weapon.SetMatchManager(mm);
        health.SetMatchManager(mm);
    }

    public void AttachCamera(Camera cam)
    {
        if (cam == null || cameraAnchor == null) return;

        cam.transform.SetParent(cameraAnchor);
        cam.transform.localPosition = Vector3.zero;
        cam.transform.localRotation = Quaternion.identity;

        if (bodyRenderer != null)
            bodyRenderer.enabled = false;

        IsLocalPlayer = true;
        Debug.Log($"[PlayerBody] Camera attached to {identity.DisplayName}");
    }

    public void DetachCamera(Camera cam)
    {
        if (cam == null) return;

        cam.transform.SetParent(null);

        if (bodyRenderer != null)
            bodyRenderer.enabled = true;

        IsLocalPlayer = false;
    }

    public void ApplyTeamColor(Color color)
    {
        identity.TeamColor = color;

        MeshRenderer[] renderers = GetComponentsInChildren<MeshRenderer>(true);
        Shader fallbackShader = Shader.Find("Standard");

        foreach (var r in renderers)
        {
            Material newMat;
            if (r.sharedMaterial == null || r.sharedMaterial.shader == null ||
                r.sharedMaterial.shader.name.Contains("Error"))
            {
                newMat = new Material(fallbackShader);
            }
            else
            {
                newMat = new Material(r.sharedMaterial);
            }
            newMat.color = color;
            r.sharedMaterial = newMat;
        }
    }
}

using UnityEngine;
using System;
using System.Globalization;
using System.Collections.Generic;

/// <summary>
/// Scene bootstrapper that configures the game mode, spawns N independent arenas
/// (controlled by the NUM_AREAS environment variable, default 1), assigns
/// controllers based on the selected GameMode, sets up lighting, and initializes
/// a MatchManager per arena.
///
/// Multi-area mode: each arena is spatially offset by AREA_SPACING on the X axis
/// so raycasts, physics, and combat events are fully isolated between arenas.
/// All GambitAgentControllers share the same BehaviorName ("GambitAgent") so
/// ML-Agents batches them into a single decision_steps / terminal_steps call.
/// </summary>
public class GameModeBootstrapper : MonoBehaviour
{
    public enum GameMode
    {
        ScriptedVsScripted,
        RLVsScripted,
        RLVsRL,
        HumanVsRL,
        HumanVsScripted,
        HumanVsGambit,
        GambitVsScripted,
        GambitVsGambit
    }

    public enum ControllerType
    {
        Human,
        ScriptedBot,
        RLBot,
        GambitBot
    }

    [Header("Game Mode")]
    public GameMode CurrentGameMode = GameMode.ScriptedVsScripted;

    [Tooltip("For automated legacy experiments only. Interactive GAMBIT DEMO uses its menu settings instead.")]
    public bool AllowEnvironmentOverrides = false;

    [Header("Match Configuration")]
    public MatchConfig MatchConfigAsset;

    [Header("Scripted Bot Settings")]
    public ScriptedBotController.ScriptedBotMode PlayerABotMode = ScriptedBotController.ScriptedBotMode.FaceOpponentAndShoot;
    public ScriptedBotController.ScriptedBotMode PlayerBBotMode = ScriptedBotController.ScriptedBotMode.RandomStrafe;

    [Header("Spawn Points (used as base positions for area 0)")]
    public SpawnPoint SpawnPointA;
    public SpawnPoint SpawnPointB;

    [Header("References (auto-created if null)")]
    public MatchManager MatchManagerInstance;
    public Camera GameCamera;
    public HUDManager HUDManagerInstance;

    // Spatial separation between arenas (meters). Must be large enough to prevent
    // raycast / physics cross-talk between adjacent arenas.
    private const float AREA_SPACING = 500f;

    // Per-arena runtime state
    private int numAreas = 1;
    private List<PlayerBody> arenaPlayerA = new List<PlayerBody>();
    private List<PlayerBody> arenaPlayerB = new List<PlayerBody>();
    private List<MatchManager> arenaMatchManagers = new List<MatchManager>();
    private List<SpawnPoint> arenaSpawnA = new List<SpawnPoint>();
    private List<SpawnPoint> arenaSpawnB = new List<SpawnPoint>();

    private struct RealisticSpawnPlan
    {
        public bool Enabled;
        public string Bucket;
        public Vector3 PosA;
        public Vector3 PosB;
        public Quaternion RotA;
        public Quaternion RotB;
        public float Distance;
        public bool ObstacleIntent;
    }

    // Legacy single-area accessors (backwards-compatible)
    private PlayerBody playerA => arenaPlayerA.Count > 0 ? arenaPlayerA[0] : null;
    private PlayerBody playerB => arenaPlayerB.Count > 0 ? arenaPlayerB[0] : null;

    private void Start()
    {
        if (!Phase5HeadlessRuntime.ValidateLaunch())
        {
            enabled = false;
            return;
        }

        if (AllowEnvironmentOverrides)
        {
            string areaEnv = System.Environment.GetEnvironmentVariable("NUM_AREAS");
            if (!string.IsNullOrEmpty(areaEnv) && int.TryParse(areaEnv, out int parsed) && parsed > 0)
                numAreas = parsed;
            string modeEnv = System.Environment.GetEnvironmentVariable("GAME_MODE");
            if (!string.IsNullOrEmpty(modeEnv) && System.Enum.TryParse<GameMode>(modeEnv, out var parsedMode))
                CurrentGameMode = parsedMode;
            string botEnv = System.Environment.GetEnvironmentVariable("PLAYER_B_BOT_MODE");
            if (!string.IsNullOrEmpty(botEnv) && System.Enum.TryParse<ScriptedBotController.ScriptedBotMode>(botEnv, out var parsedBot))
                PlayerBBotMode = parsedBot;
        }

        ScriptedShootPressure.LoadFromEnvironment();
        LearnerShootGeometry.LoadFromEnvironment();
        ShotGeometryLogger.Configure();
        WeaponStateDebugLogger.Configure();
        PlayerWeapon.LoadDebugFlagsFromEnvironment();
        Debug.Log(
            $"[Bootstrapper] ShotGeometryLogger enabled={ShotGeometryLogger.IsEnabled}"
        );

        Debug.Log(
            $"[Bootstrapper] Scripted shoot pressure: dmgScale={ScriptedShootPressure.DamageScale} "
            + $"cdMult={ScriptedShootPressure.CooldownMult} aimDeg={ScriptedShootPressure.AimThresholdDeg} "
            + $"warmupEnd={ScriptedShootPressure.GlobalWarmupEndTime}"
        );

        Debug.Log($"[Bootstrapper] Starting BotArenaPhase5 — Mode: {CurrentGameMode}, Areas: {numAreas}");

        SetupEnvironment();
        if (!Phase5ProceduralArenaRuntime.Initialize(numAreas))
        {
            enabled = false;
            return;
        }
        if (!DemoMapRuntime.Initialize(numAreas))
        {
            enabled = false;
            return;
        }
        if (!ValidatePhase5NavigatorLaunch()
            || !ValidatePhase5MapGeneralPpoLaunch()
            || !Phase5RuntimeNavMesh.ValidateLaunch()
            || !Phase5AutonomousSession.ValidateGlobalLaunch()
            || !Phase5GenericPrivilegedTeacher.ValidateGlobalLaunch())
        {
            enabled = false;
            return;
        }
        if (AllowEnvironmentOverrides)
            ApplyPhase45LowLatencyGraphics();

        for (int areaId = 0; areaId < numAreas; areaId++)
        {
            Vector3 areaOffset = new Vector3(areaId * AREA_SPACING, 0f, 0f);
            SetupArea(areaId, areaOffset);
        }

        if (!Phase5ProceduralArenaRuntime.FinalizeAndWriteAudit(
            arenaPlayerA.ToArray(),
            arenaPlayerB.ToArray()))
        {
            enabled = false;
            return;
        }
        if (!Phase5RuntimeNavMesh.WriteAudit())
        {
            enabled = false;
            return;
        }

        if (Phase5HeadlessRuntime.Enabled)
            Phase5HeadlessRuntime.SuppressRenderingAndAudit();
        else
        {
            SetupCamera();
            SetupHUD();
        }
        SetupPhase45LiveTelemetryBridge();
        Phase5TelemetryLeakageProbe.MaybeInstall();

        Debug.Log($"[Bootstrapper] Game initialized successfully — {numAreas} area(s).");
    }

    // ─────────────────────────────────────────────────────
    // Environment Setup
    // ─────────────────────────────────────────────────────

    private void ApplyPhase45LowLatencyGraphics()
    {
        if (System.Environment.GetEnvironmentVariable("PHASE4_5_LOW_LATENCY_GRAPHICS") != "1")
            return;

        int maxQuality = Mathf.Max(0, QualitySettings.names.Length - 1);
        int qualityLevel = ParseIntEnv("PHASE4_5_QUALITY_LEVEL", 0, 0, maxQuality);
        int targetFps = ParseIntEnv("PHASE4_5_TARGET_FPS", 60, 15, 120);

        QualitySettings.SetQualityLevel(qualityLevel, true);
        QualitySettings.vSyncCount = 0;
        QualitySettings.antiAliasing = 0;
        QualitySettings.shadows = ShadowQuality.Disable;
        QualitySettings.shadowDistance = 0f;
        Application.targetFrameRate = targetFps;

        Light sun = UnityEngine.Object.FindObjectOfType<Light>();
        if (sun != null)
            sun.shadows = LightShadows.None;

        Debug.Log("[Bootstrapper] Phase 4.5 low-latency graphics enabled: "
            + "qualityLevel=" + qualityLevel
            + " targetFps=" + targetFps
            + " vSync=0 shadows=off aa=0");
    }

    private static int ParseIntEnv(string name, int defaultValue, int minValue, int maxValue)
    {
        string raw = System.Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out int parsed))
            return defaultValue;
        return Mathf.Clamp(parsed, minValue, maxValue);
    }

    private void SetupEnvironment()
    {
        if (Phase5HeadlessRuntime.Enabled)
            return;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.53f, 0.57f, 0.66f);
        RenderSettings.ambientEquatorColor = new Color(0.4f, 0.4f, 0.45f);
        RenderSettings.ambientGroundColor = new Color(0.25f, 0.22f, 0.2f);
        RenderSettings.ambientIntensity = 1.0f;

        Light sun = UnityEngine.Object.FindObjectOfType<Light>();
        if (sun == null)
        {
            GameObject lightObj = new GameObject("Directional Light");
            sun = lightObj.AddComponent<Light>();
        }
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.956f, 0.839f);
        sun.intensity = 1f;
        sun.transform.rotation = Quaternion.Euler(50, -30, 0);
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.3f;
        RenderSettings.sun = sun;

        QualitySettings.SetQualityLevel(5, true);
    }

    // ─────────────────────────────────────────────────────
    // Per-Area Setup
    // ─────────────────────────────────────────────────────

    private void SetupArea(int areaId, Vector3 offset)
    {
        Debug.Log($"[Bootstrapper] Setting up area {areaId} at offset {offset}");

        // Create arena root for hierarchy clarity
        GameObject arenaRoot = new GameObject($"_Arena_{areaId}");
        arenaRoot.transform.position = offset;

        Phase44SpawnBucketController.SpawnPlan phase44SpawnPlan =
            new Phase44SpawnBucketController.SpawnPlan();
        RealisticSpawnPlan spawnPlan;
        if (Phase5ProceduralArenaRuntime.Enabled)
        {
            if (!Phase5ProceduralArenaRuntime.BuildArea(areaId, offset, arenaRoot.transform)
                || !TryBuildProceduralSpawnPlan(areaId, out spawnPlan))
                throw new InvalidOperationException("Phase 5 procedural area construction failed for area " + areaId);
        }
        else
        {
            // Phase 4.4 controls supersede the older Phase 4.5 realistic-spawn
            // development hook only when explicitly enabled.
            phase44SpawnPlan = Phase44SpawnBucketController.BuildPlan(areaId, offset);
            spawnPlan = phase44SpawnPlan.Enabled
                ? ToRealisticSpawnPlan(phase44SpawnPlan)
                : BuildRealisticSpawnPlan(areaId, offset);
        }
        SpawnPoint spA = CreateSpawnPoint(areaId, 0, offset, spawnPlan);
        SpawnPoint spB = CreateSpawnPoint(areaId, 1, offset, spawnPlan);
        arenaSpawnA.Add(spA);
        arenaSpawnB.Add(spB);

        // Create players
        PlayerBody pA = CreatePlayerBody($"Area{areaId}_Player_A", 0, spA.GetSpawnPosition(), spA.GetSpawnRotation(), areaId);
        PlayerBody pB = CreatePlayerBody($"Area{areaId}_Player_B", 1, spB.GetSpawnPosition(), spB.GetSpawnRotation(), areaId);
        pA.transform.SetParent(arenaRoot.transform);
        pB.transform.SetParent(arenaRoot.transform);

        pA.ApplyTeamColor(Color.HSVToRGB(0.6f, 0.8f, 0.9f));
        pB.ApplyTeamColor(Color.HSVToRGB(0.0f, 0.8f, 0.9f));

        arenaPlayerA.Add(pA);
        arenaPlayerB.Add(pB);

        // Create MatchManager for this area
        MatchManager mm;
        if (areaId == 0 && MatchManagerInstance != null)
        {
            mm = MatchManagerInstance;
        }
        else
        {
            GameObject mmObj = new GameObject($"_MatchManager_{areaId}");
            mmObj.transform.SetParent(arenaRoot.transform);
            mm = mmObj.AddComponent<MatchManager>();
        }
        mm.Config = MatchConfigAsset;
        mm.PlayerA = pA;
        mm.PlayerB = pB;
        mm.SpawnPointA = spA;
        mm.SpawnPointB = spB;
        pA.SetMatchManager(mm);
        pB.SetMatchManager(mm);
        arenaMatchManagers.Add(mm);
        if (areaId == 0)
            MatchManagerInstance = mm;

        // Build synchronously before sensors/controllers are constructed so a
        // Goal 8 launch can never collect an observation without a valid path.
        if (!Phase5RuntimeNavMesh.BuildArea(areaId, pA, pB))
            throw new InvalidOperationException(
                "Phase 5 runtime NavMesh construction failed for area " + areaId);

        // Assign controllers
        ControllerType typeA, typeB;
        GetControllerTypes(out typeA, out typeB);
        AttachController(pA, typeA, PlayerABotMode, areaId);
        AttachController(pB, typeB, PlayerBBotMode, areaId);
        Phase5ProceduralArenaRuntime.ApplyGameplayParameters(areaId, pA, pB);
        ApplyScriptedShootPressureToPlayerB(pB, typeB);
        SetupPhase44TrialControls(areaId, mm, pA, pB, phase44SpawnPlan);
        Phase5RuntimeMetrics.RegisterArea(areaId, mm, pA, pB);
        Phase5AutonomousSession.Attach(areaId, mm, pA, pB);
        Phase5GenericPrivilegedTeacher.Attach(areaId, mm, pA, pB);

        Debug.Log($"[Bootstrapper] Area {areaId}: {pA.Identity.DisplayName}={typeA}, {pB.Identity.DisplayName}={typeB}");
    }

    private bool TryBuildProceduralSpawnPlan(int areaId, out RealisticSpawnPlan plan)
    {
        plan = new RealisticSpawnPlan();
        if (!Phase5ProceduralArenaRuntime.TryGetSpawn(areaId, 0, out Vector3 posA, out Quaternion rotA)
            || !Phase5ProceduralArenaRuntime.TryGetSpawn(areaId, 1, out Vector3 posB, out Quaternion rotB))
            return false;
        plan.Enabled = true;
        plan.Bucket = "phase5_procedural";
        plan.PosA = posA;
        plan.PosB = posB;
        plan.RotA = rotA;
        plan.RotB = rotB;
        plan.Distance = Vector3.Distance(posA, posB);
        plan.ObstacleIntent = !Phase44SpawnBucketController.HasLineOfSightAtPositions(posA, posB);
        return true;
    }

    private RealisticSpawnPlan ToRealisticSpawnPlan(Phase44SpawnBucketController.SpawnPlan phase44Plan)
    {
        RealisticSpawnPlan plan = new RealisticSpawnPlan();
        plan.Enabled = phase44Plan.Enabled;
        plan.Bucket = phase44Plan.Bucket;
        plan.PosA = phase44Plan.PosA;
        plan.PosB = phase44Plan.PosB;
        plan.RotA = phase44Plan.RotA;
        plan.RotB = phase44Plan.RotB;
        plan.Distance = phase44Plan.SpawnDistance;
        plan.ObstacleIntent = phase44Plan.ObstacleBetweenPlayers;
        return plan;
    }

    private void SetupPhase44TrialControls(
        int areaId,
        MatchManager mm,
        PlayerBody pA,
        PlayerBody pB,
        Phase44SpawnBucketController.SpawnPlan phase44SpawnPlan)
    {
        if (!phase44SpawnPlan.Enabled)
            return;
        if (mm == null || pA == null || pB == null)
        {
            Debug.LogError("[Bootstrapper] PHASE4_4_ENABLE_SPAWN_BUCKETS requested but match/player references are missing.");
            return;
        }

        Phase44TrialTimeoutController trial = mm.gameObject.AddComponent<Phase44TrialTimeoutController>();
        trial.Initialize(mm, pA, pB, phase44SpawnPlan, areaId);
        Debug.Log($"[Bootstrapper] Phase 4.4 spawn controls enabled area={areaId} bucket={phase44SpawnPlan.Bucket}");
    }

    private SpawnPoint CreateSpawnPoint(int areaId, int playerIndex, Vector3 areaOffset, RealisticSpawnPlan realisticPlan)
    {
        // Base spawn positions (known-good coordinates from the map)
        Vector3 baseA = new Vector3(35f, 1f, -80f);
        Vector3 baseB = new Vector3(35f, 1f, -77f);
        Quaternion rotA = Quaternion.Euler(0f, 270f, 0f);
        Quaternion rotB = Quaternion.Euler(0f, 90f, 0f);

        // Use inspector-assigned spawn points for area 0 if available and realistic spawns are off.
        if (!realisticPlan.Enabled && areaId == 0)
        {
            if (playerIndex == 0 && SpawnPointA != null) return SpawnPointA;
            if (playerIndex == 1 && SpawnPointB != null) return SpawnPointB;
        }

        Vector3 pos = realisticPlan.Enabled
            ? (playerIndex == 0 ? realisticPlan.PosA : realisticPlan.PosB)
            : (playerIndex == 0 ? baseA : baseB) + areaOffset;
        Quaternion rot = realisticPlan.Enabled
            ? (playerIndex == 0 ? realisticPlan.RotA : realisticPlan.RotB)
            : (playerIndex == 0 ? rotA : rotB);

        GameObject spObj = new GameObject($"SpawnPoint_Area{areaId}_{(playerIndex == 0 ? "A" : "B")}");
        spObj.transform.position = pos;
        spObj.transform.rotation = rot;
        SpawnPoint sp = spObj.AddComponent<SpawnPoint>();
        sp.PlayerIndex = playerIndex;
        return sp;
    }

    private RealisticSpawnPlan BuildRealisticSpawnPlan(int areaId, Vector3 areaOffset)
    {
        RealisticSpawnPlan plan = new RealisticSpawnPlan();
        if (!ShouldEnableRealisticSpawns())
            return plan;

        string requested = (Environment.GetEnvironmentVariable("PHASE4_5_SPAWN_BUCKET") ?? "random").Trim().ToLowerInvariant();
        string bucket = ResolveSpawnBucket(requested);
        Vector2 range = SpawnDistanceRange(bucket);
        float distance = UnityEngine.Random.Range(range.x, range.y);
        float yawDeg = UnityEngine.Random.Range(0f, 360f);
        if (bucket == "obstacle")
            yawDeg = UnityEngine.Random.Range(35f, 145f);
        else if (bucket == "search_destroy")
            yawDeg = UnityEngine.Random.Range(120f, 240f);

        Vector3 baseA = new Vector3(35f, 1f, -80f) + areaOffset;
        Vector3 dir = Quaternion.Euler(0f, yawDeg, 0f) * Vector3.forward;
        Vector3 posA = baseA;
        Vector3 posB = baseA + dir.normalized * distance;
        posB.y = posA.y;

        plan.Enabled = true;
        plan.Bucket = bucket;
        plan.PosA = posA;
        plan.PosB = posB;
        plan.RotA = Quaternion.LookRotation((posB - posA).normalized, Vector3.up);
        plan.RotB = Quaternion.LookRotation((posA - posB).normalized, Vector3.up);
        plan.Distance = Vector3.Distance(posA, posB);
        plan.ObstacleIntent = bucket == "obstacle" || bucket == "search_destroy";

        Environment.SetEnvironmentVariable("PHASE4_5_ACTIVE_SPAWN_BUCKET", plan.Bucket);
        Environment.SetEnvironmentVariable("PHASE4_5_ACTIVE_SPAWN_DISTANCE", plan.Distance.ToString("F3", CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("PHASE4_5_ACTIVE_OBSTACLE_INTENT", plan.ObstacleIntent ? "1" : "0");
        Debug.Log($"[Bootstrapper] Phase 4.5c realistic spawn bucket={plan.Bucket} distance={plan.Distance:F2} posA={plan.PosA} posB={plan.PosB}");
        return plan;
    }

    private bool ShouldEnableRealisticSpawns()
    {
        if (Environment.GetEnvironmentVariable("PHASE4_5_ENABLE_REALISTIC_SPAWNS") != "1")
            return false;
        if (CurrentGameMode != GameMode.HumanVsScripted)
            return false;
        return PlayerBBotMode == ScriptedBotController.ScriptedBotMode.Idle
            || PlayerBBotMode == ScriptedBotController.ScriptedBotMode.FaceOpponent
            || PlayerBBotMode == ScriptedBotController.ScriptedBotMode.StrafeAndFace
            || PlayerBBotMode == ScriptedBotController.ScriptedBotMode.StrafeAndFaceShoot;
    }

    private string ResolveSpawnBucket(string requested)
    {
        switch (requested)
        {
            case "close":
            case "mid":
            case "far":
            case "obstacle":
            case "search_destroy":
                return requested;
            case "random":
            default:
                float roll = UnityEngine.Random.value;
                if (roll < 0.20f) return "close";
                if (roll < 0.50f) return "mid";
                if (roll < 0.75f) return "obstacle";
                if (roll < 0.90f) return "far";
                return "search_destroy";
        }
    }

    private Vector2 SpawnDistanceRange(string bucket)
    {
        switch (bucket)
        {
            case "close": return new Vector2(5f, 10f);
            case "mid": return new Vector2(10f, 20f);
            case "far": return new Vector2(20f, 40f);
            case "obstacle": return new Vector2(10f, 30f);
            case "search_destroy": return new Vector2(20f, 50f);
            default: return new Vector2(10f, 20f);
        }
    }

    // ─────────────────────────────────────────────────────
    // Player Creation
    // ─────────────────────────────────────────────────────

    private PlayerBody CreatePlayerBody(string playerName, int playerIndex, Vector3 spawnPos, Quaternion spawnRot, int areaId)
    {
        GameObject playerObj = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        playerObj.name = playerName;
        playerObj.transform.position = spawnPos;
        playerObj.transform.rotation = spawnRot;

        CapsuleCollider defaultCollider = playerObj.GetComponent<CapsuleCollider>();
        if (defaultCollider != null) Destroy(defaultCollider);

        CharacterController cc = playerObj.AddComponent<CharacterController>();
        cc.height = 2f;
        cc.radius = 0.5f;
        cc.center = new Vector3(0f, 0f, 0f);

        GameObject hurtbox = new GameObject("Hurtbox");
        hurtbox.transform.SetParent(playerObj.transform);
        hurtbox.transform.localPosition = Vector3.zero;
        CapsuleCollider hurtboxCollider = hurtbox.AddComponent<CapsuleCollider>();
        hurtboxCollider.height = 2f;
        hurtboxCollider.radius = 0.5f;
        hurtboxCollider.isTrigger = false;

        PlayerIdentity identity = playerObj.AddComponent<PlayerIdentity>();
        identity.PlayerId = playerName;
        identity.DisplayName = playerName;
        identity.PlayerIndex = playerIndex;

        playerObj.AddComponent<PlayerMotor>();
        PlayerWeapon weapon = playerObj.AddComponent<PlayerWeapon>();
        PlayerHealth health = playerObj.AddComponent<PlayerHealth>();
        PlayerBody body = playerObj.AddComponent<PlayerBody>();

        if (MatchConfigAsset != null)
        {
            weapon.Configure(MatchConfigAsset);
            health.Configure(MatchConfigAsset);
        }

        return body;
    }

    // ─────────────────────────────────────────────────────
    // Controller Assignment
    // ─────────────────────────────────────────────────────

    private void ApplyScriptedShootPressureToPlayerB(PlayerBody playerB, ControllerType typeB)
    {
        if (typeB != ControllerType.ScriptedBot)
            return;

        if (playerB.Weapon != null)
            playerB.Weapon.ApplyShootPressure(
                ScriptedShootPressure.DamageScale,
                ScriptedShootPressure.CooldownMult);

        ScriptedBotController bot = playerB.GetComponent<ScriptedBotController>();
        if (bot != null)
            bot.AimThresholdDegrees = ScriptedShootPressure.AimThresholdDeg;
    }

    private void GetControllerTypes(out ControllerType typeA, out ControllerType typeB)
    {
        if (Phase5AutonomousSession.Enabled)
        {
            typeA = ControllerType.GambitBot;
            typeB = ControllerType.ScriptedBot;
            return;
        }
        switch (CurrentGameMode)
        {
            case GameMode.ScriptedVsScripted: typeA = ControllerType.ScriptedBot; typeB = ControllerType.ScriptedBot; break;
            case GameMode.RLVsScripted: typeA = ControllerType.RLBot; typeB = ControllerType.ScriptedBot; break;
            case GameMode.RLVsRL: typeA = ControllerType.RLBot; typeB = ControllerType.RLBot; break;
            case GameMode.HumanVsRL: typeA = ControllerType.Human; typeB = ControllerType.RLBot; break;
            case GameMode.HumanVsScripted: typeA = ControllerType.Human; typeB = ControllerType.ScriptedBot; break;
            case GameMode.HumanVsGambit: typeA = ControllerType.Human; typeB = ControllerType.GambitBot; break;
            case GameMode.GambitVsScripted: typeA = ControllerType.GambitBot; typeB = ControllerType.ScriptedBot; break;
            case GameMode.GambitVsGambit: typeA = ControllerType.GambitBot; typeB = ControllerType.GambitBot; break;
            default: typeA = ControllerType.ScriptedBot; typeB = ControllerType.ScriptedBot; break;
        }
    }

    private void AttachController(PlayerBody body, ControllerType type, ScriptedBotController.ScriptedBotMode botMode, int areaId)
    {
        MonoBehaviour controller;

        switch (type)
        {
            case ControllerType.Human:
                controller = body.gameObject.AddComponent<HumanController>();
                break;
            case ControllerType.ScriptedBot:
                ScriptedBotController bot = body.gameObject.AddComponent<ScriptedBotController>();
                bot.BotMode = botMode;
                controller = bot;
                break;
            case ControllerType.RLBot:
                // Interactive GAMBIT matches use the bundled, frozen Phase 6
                // policy in-process. Research launches retain the ML-Agents
                // controller so mlagents-learn can connect as before.
                if (!AllowEnvironmentOverrides)
                {
                    bool onnxWasActive = body.gameObject.activeSelf;
                    body.gameObject.SetActive(false);
                    GambitAgentController telemetryHelper =
                        body.gameObject.AddComponent<GambitAgentController>();
                    telemetryHelper.enabled = false;
                    Phase6UnityOnnxController onnxController =
                        body.gameObject.AddComponent<Phase6UnityOnnxController>();
                    onnxController.TelemetryHelper = telemetryHelper;
                    body.SetController(onnxController);
                    body.gameObject.SetActive(onnxWasActive);
                    Debug.Log($"[Bootstrapper] area_id={areaId} bundled Unity ONNX attached to {body.Identity.DisplayName}");
                    return;
                }

                RLAgentController rl = body.gameObject.AddComponent<RLAgentController>();
                var bp = rl.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
                if (bp != null)
                {
                    bp.BehaviorName = "BotArenaAgent";
                    bp.BrainParameters.VectorObservationSize = 12;
                    bp.BrainParameters.ActionSpec = Unity.MLAgents.Actuators.ActionSpec.MakeDiscrete(5, 3, 2);
                }
                controller = rl;
                break;
            case ControllerType.GambitBot:
                bool wasActive = body.gameObject.activeSelf;
                body.gameObject.SetActive(false);

                var gbp = body.gameObject.AddComponent<Unity.MLAgents.Policies.BehaviorParameters>();
                gbp.BehaviorName = "GambitAgent";
                gbp.BrainParameters.VectorObservationSize = 0;
                gbp.BrainParameters.ActionSpec = new Unity.MLAgents.Actuators.ActionSpec(
                    4, new int[] { 2, 2, 2, 2 });

                GameObject agentCamObj = new GameObject("AgentCam_" + body.Identity.DisplayName);
                agentCamObj.transform.SetParent(body.transform);
                agentCamObj.transform.localPosition = new Vector3(0f, 0.7f, 0f);
                agentCamObj.transform.localRotation = Quaternion.identity;
                Camera agentCam = agentCamObj.AddComponent<Camera>();
                if (GameCamera != null) agentCam.CopyFrom(GameCamera);
                agentCam.enabled = false;

                string telemetrySchema = System.Environment.GetEnvironmentVariable("PHASE5_TELEMETRY_SCHEMA") ?? "phase3v2_c_local45";
                bool navigatorEvaluation =
                    (System.Environment.GetEnvironmentVariable("PHASE5_NAVIGATOR_EVAL") ?? "0").Trim() == "1";
                if (navigatorEvaluation)
                {
                    // Goal 7 evaluation keeps the frozen local45 combat stream and
                    // adds actor231 as a separately named sensor. Python dispatches
                    // by observation width, never by sensor order.
                    body.gameObject.AddComponent<GambitTelemetrySensorComponent>();
                    body.gameObject.AddComponent<Phase5MapIndependentSensorComponent>();
                    if (Phase5RuntimeNavMesh.Enabled)
                    {
                        Phase5NavMeshRouteSensorComponent route =
                            body.gameObject.AddComponent<Phase5NavMeshRouteSensorComponent>();
                        route.AreaId = areaId;
                        Debug.Log($"[Bootstrapper] area_id={areaId} navigator_eval=1 sensors=local45,actor231,navmesh32");
                    }
                    else
                    {
                        Debug.Log($"[Bootstrapper] area_id={areaId} navigator_eval=1 sensors=local45,actor231");
                    }
                    if ((System.Environment.GetEnvironmentVariable("PHASE5_MAP_GENERAL_PPO") ?? "0").Trim() == "1")
                    {
                        body.gameObject.AddComponent<Phase5PpoPrivilegedSensorComponent>();
                        Phase5PpoRoleSensorComponent role =
                            body.gameObject.AddComponent<Phase5PpoRoleSensorComponent>();
                        role.CandidateRole = body.Identity != null && body.Identity.PlayerIndex == 0;
                        Debug.Log($"[Bootstrapper] area_id={areaId} goal9_ppo=1 sensors=critic428,role1 candidate={role.CandidateRole}");
                    }
                }
                else if (telemetrySchema == Phase5ActorObservationLayout.SchemaId)
                {
                    body.gameObject.AddComponent<Phase5MapIndependentSensorComponent>();
                    Debug.Log($"[Bootstrapper] area_id={areaId} telemetry_schema={telemetrySchema}");
                }
                else if (telemetrySchema == "phase3v2_c_local45" || string.IsNullOrWhiteSpace(telemetrySchema))
                {
                    body.gameObject.AddComponent<GambitTelemetrySensorComponent>();
                }
                else
                {
                    throw new InvalidOperationException("Unsupported PHASE5_TELEMETRY_SCHEMA: " + telemetrySchema);
                }

                // Conditionally add camera sensor for visual observations
                string enableVisual = System.Environment.GetEnvironmentVariable("ENABLE_VISUAL_OBS");
                if (enableVisual == "1")
                {
                    agentCam.enabled = true;
                    var camSensor = body.gameObject.AddComponent<Unity.MLAgents.Sensors.CameraSensorComponent>();
                    camSensor.Camera = agentCam;
                    camSensor.Width = 160;
                    camSensor.Height = 120;
                    camSensor.SensorName = "GambitVisual";
                    camSensor.Grayscale = false;
                    camSensor.CompressionType = Unity.MLAgents.Sensors.SensorCompressionType.None;
                    Debug.Log($"[Bootstrapper] area_id={areaId} CameraSensor enabled (160x120 RGB, uncompressed)");
                }

                // CRITICAL: Add Agent BEFORE DecisionRequester!
                // DecisionRequester has [RequireComponent(typeof(Agent))]. If added
                // first, Unity auto-creates a bare Agent, and DecisionRequester
                // links to THAT instead of GambitAgentController. Then rewards set
                // on GambitAgentController never reach Python.
                GambitAgentController gambit = body.gameObject.AddComponent<GambitAgentController>();
                gambit.enabled = false;
                gambit.AgentCamera = agentCam;

                var dr = body.gameObject.AddComponent<Unity.MLAgents.DecisionRequester>();
                dr.DecisionPeriod = 1;
                dr.enabled = false;

                body.SetController(gambit);
                gambit.enabled = true;
                dr.enabled = true;
                body.gameObject.SetActive(wasActive);

                Debug.Log($"[Bootstrapper] area_id={areaId} GambitBot attached to {body.Identity.DisplayName}");
                return;
            default:
                controller = body.gameObject.AddComponent<ScriptedBotController>();
                break;
        }

        body.SetController(controller);
    }

    private static bool ValidatePhase5NavigatorLaunch()
    {
        string raw = (System.Environment.GetEnvironmentVariable("PHASE5_NAVIGATOR_EVAL") ?? "0").Trim();
        if (raw == "0" || string.IsNullOrEmpty(raw))
            return true;
        if (raw != "1")
        {
            Debug.LogError("[Phase5Navigator] PHASE5_NAVIGATOR_EVAL must be 0 or 1");
            return false;
        }
        bool renderedSmoke = Phase5RenderedSmokeRuntime.Enabled;
        if (!Phase5HeadlessRuntime.Enabled && !renderedSmoke)
        {
            Debug.LogError(
                "[Phase5Navigator] evaluation requires --phase5-headless or explicit attended rendered smoke"
            );
            return false;
        }
        if (renderedSmoke)
            Debug.Log("[Phase5Navigator] explicit attended rendered smoke accepted");
        string schema = (System.Environment.GetEnvironmentVariable("PHASE5_TELEMETRY_SCHEMA")
            ?? "phase3v2_c_local45").Trim();
        if (schema != "phase3v2_c_local45")
        {
            Debug.LogError("[Phase5Navigator] dual sensor mode requires frozen phase3v2_c_local45");
            return false;
        }
        if ((System.Environment.GetEnvironmentVariable("ENABLE_VISUAL_OBS") ?? "0").Trim() == "1")
        {
            Debug.LogError("[Phase5Navigator] visual observations are forbidden");
            return false;
        }
        return true;
    }

    private static bool ValidatePhase5MapGeneralPpoLaunch()
    {
        string raw = (System.Environment.GetEnvironmentVariable("PHASE5_MAP_GENERAL_PPO") ?? "0").Trim();
        if (raw == "0" || string.IsNullOrEmpty(raw))
            return true;
        if (raw != "1")
        {
            Debug.LogError("[Phase5HunterPPO] PHASE5_MAP_GENERAL_PPO must be 0 or 1");
            return false;
        }
        if (!Phase5HeadlessRuntime.Enabled
            || (System.Environment.GetEnvironmentVariable("PHASE5_NAVIGATOR_EVAL") ?? "0").Trim() != "1")
        {
            Debug.LogError("[Phase5HunterPPO] requires headless navigator dual-sensor mode");
            return false;
        }
        if ((System.Environment.GetEnvironmentVariable("PHASE5_ENABLE_NAVMESH_ORACLE") ?? "0").Trim() == "1"
            || (System.Environment.GetEnvironmentVariable("PHASE5_NAVMESH_UPPER_BOUND") ?? "0").Trim() == "1")
        {
            Debug.LogError("[Phase5HunterPPO] NavMesh actor/oracle inputs are forbidden");
            return false;
        }
        if ((System.Environment.GetEnvironmentVariable("ENABLE_VISUAL_OBS") ?? "0").Trim() == "1")
        {
            Debug.LogError("[Phase5HunterPPO] visual observations are forbidden");
            return false;
        }
        return true;
    }

    // ─────────────────────────────────────────────────────
    // Camera Setup
    // ─────────────────────────────────────────────────────

    private enum CameraView { Observer, PlayerA, PlayerB }
    private CameraView currentView = CameraView.Observer;

    private void SetupCamera()
    {
        if (GameCamera == null)
        {
            GameCamera = Camera.main;
            if (GameCamera == null)
            {
                GameObject camObj = new GameObject("Main Camera");
                camObj.tag = "MainCamera";
                GameCamera = camObj.AddComponent<Camera>();
            }
        }

        GameCamera.fieldOfView = 85f;
        GameCamera.nearClipPlane = 0.05f;
        GameCamera.clearFlags = CameraClearFlags.Skybox;

        if (CurrentGameMode == GameMode.HumanVsRL || CurrentGameMode == GameMode.HumanVsScripted || CurrentGameMode == GameMode.HumanVsGambit)
        {
            SwitchCameraTo(CameraView.PlayerA);
        }
        else
        {
            SwitchCameraTo(CameraView.Observer);
        }
    }

    private void Update()
    {
        if (Phase5HeadlessRuntime.Enabled)
            return;

        if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
            SwitchCameraTo(CameraView.Observer);
        else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
            SwitchCameraTo(CameraView.PlayerA);
        else if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
            SwitchCameraTo(CameraView.PlayerB);
    }

    private void SwitchCameraTo(CameraView view)
    {
        if (GameCamera == null) return;
        if (playerA != null) playerA.DetachCamera(GameCamera);
        if (playerB != null) playerB.DetachCamera(GameCamera);

        currentView = view;
        switch (view)
        {
            case CameraView.Observer:
                SetupObserverCamera();
                break;
            case CameraView.PlayerA:
                if (playerA != null) playerA.AttachCamera(GameCamera);
                break;
            case CameraView.PlayerB:
                if (playerB != null) playerB.AttachCamera(GameCamera);
                break;
        }
    }

    private void SetupObserverCamera()
    {
        if (GameCamera == null || playerA == null || playerB == null) return;
        Vector3 midpoint = (playerA.transform.position + playerB.transform.position) / 2f;
        GameCamera.transform.position = midpoint + new Vector3(0f, 15f, -10f);
        GameCamera.transform.LookAt(midpoint);
    }

    private void SetupPhase45LiveTelemetryBridge()
    {
        string enableBridge = System.Environment.GetEnvironmentVariable("PHASE4_5_LIVE_BRIDGE");
        if (enableBridge != "1")
            return;

        if (MatchManagerInstance == null || playerA == null || playerB == null)
        {
            Debug.LogError("[Bootstrapper] PHASE4_5_LIVE_BRIDGE requested but match/player references are missing.");
            return;
        }

        GameObject bridgeObj = new GameObject("_Phase45LiveTelemetryBridge");
        Phase45LiveTelemetryBridge bridge = bridgeObj.AddComponent<Phase45LiveTelemetryBridge>();
        bridge.Initialize(MatchManagerInstance, playerA, playerB, CurrentGameMode.ToString(), PlayerBBotMode.ToString());
        Debug.Log("[Bootstrapper] Phase 4.5 live telemetry bridge enabled.");
    }

    // ─────────────────────────────────────────────────────
    // HUD Setup
    // ─────────────────────────────────────────────────────

    private void SetupHUD()
    {
        if (HUDManagerInstance == null)
        {
            GameObject hudObj = new GameObject("_HUDManager");
            HUDManagerInstance = hudObj.AddComponent<HUDManager>();
        }

        HUDManagerInstance.Initialize(MatchManagerInstance,
            GambitDemoRuntimeSettings.GameModeDisplayName(CurrentGameMode));
    }
}

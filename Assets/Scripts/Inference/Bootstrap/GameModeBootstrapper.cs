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

    public enum CameraView
    {
        Observer,
        PlayerA,
        PlayerB
    }

    [Header("Game Mode")]
    public GameMode CurrentGameMode = GameMode.ScriptedVsScripted;

    [Tooltip("For automated legacy experiments only. Interactive GAMBIT DEMO uses its menu settings instead.")]
    public bool AllowEnvironmentOverrides = false;

    [Header("Match Configuration")]
    public MatchConfig MatchConfigAsset;
    [Tooltip("Typed runtime request. If omitted, CurrentGameMode is converted to a compatibility preset.")]
    public MatchSpec RequestedMatch;

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

    [Header("Presentation")]
    [Tooltip("Initial camera for matches without a human player. Human matches always start in Player A POV.")]
    public CameraView InitialCameraView = CameraView.Observer;

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

        if (RequestedMatch == null)
        {
            RequestedMatch = GameModePresets.FromLegacy(
                CurrentGameMode,
                PlayerABotMode,
                PlayerBBotMode,
                GambitDemoRuntimeSettings.MapId,
                Application.targetFrameRate > 0 ? Application.targetFrameRate : 60);
        }
        ApplyRequestedMatch();

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

        Debug.Log($"[Bootstrapper] Starting GAMBIT — Mode: {CurrentGameMode}, Areas: {numAreas}");

        SetupEnvironment();
        if (!DemoMapRuntime.Initialize(numAreas))
        {
            enabled = false;
            return;
        }
        if (AllowEnvironmentOverrides)
            ApplyLowLatencyGraphics();

        for (int areaId = 0; areaId < numAreas; areaId++)
        {
            Vector3 areaOffset = new Vector3(areaId * AREA_SPACING, 0f, 0f);
            SetupArea(areaId, areaOffset);
        }

        if (!GambitRuntimeMode.IsHeadless)
        {
            SetupCamera();
            SetupHUD();
        }

        Debug.Log($"[Bootstrapper] Game initialized successfully — {numAreas} area(s).");
    }

    // ─────────────────────────────────────────────────────
    // Environment Setup
    // ─────────────────────────────────────────────────────

    private void ApplyLowLatencyGraphics()
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

    private void ApplyRequestedMatch()
    {
        if (RequestedMatch.Execution != null)
        {
            if (AllowEnvironmentOverrides && GambitRuntimeMode.IsHeadless)
                RequestedMatch.Execution.Kind = GambitExecutionKind.HeadlessTraining;
            GambitRuntimeMode.Configure(RequestedMatch.Execution);
            numAreas = Mathf.Max(numAreas, RequestedMatch.Execution.ArenaCount);
            if (RequestedMatch.Execution.TargetFrameRate > 0)
                Application.targetFrameRate = RequestedMatch.Execution.TargetFrameRate;
        }

        if (UsesUnifiedPolicy(RequestedMatch.PlayerA) || UsesUnifiedPolicy(RequestedMatch.PlayerB))
        {
            Time.fixedDeltaTime = 1f / 30f;
            Debug.Log("[Bootstrapper] unified policy decision clock=30 Hz");
        }

        if (MatchConfigAsset == null || RequestedMatch.Rules == null) return;
        MatchRulesSpec rules = RequestedMatch.Rules;
        MatchConfigAsset.MaxHealth = rules.MaxHealth;
        MatchConfigAsset.DamagePerHit = rules.DamagePerHit;
        MatchConfigAsset.KillsPerMatch = rules.KillsToWin;
        MatchConfigAsset.ScorePerHit = rules.ScorePerHit;
        MatchConfigAsset.ScorePerKill = rules.ScorePerKill;
        MatchConfigAsset.GlobalCooldownSeconds = rules.CooldownSeconds;
        MatchConfigAsset.ResetPositionsAfterKill = rules.ResetPositionsAfterKill;
        MatchConfigAsset.ResetBothHealthAfterKill = rules.ResetHealthAfterKill;
    }

    private static bool UsesUnifiedPolicy(PolicySpec policy)
    {
        return policy != null
            && (policy.ObservationSchema == UnifiedFairObservationV2Contract.SchemaId
                || policy.ObservationSchema == UnifiedFairObservationV2Contract.TokenSchemaId);
    }

    private void SetupEnvironment()
    {
        if (GambitRuntimeMode.IsHeadless)
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

        RealisticSpawnPlan spawnPlan = BuildValidatedSpawnPlan(areaId, offset);
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

        // Assign controllers
        ControllerType typeA, typeB;
        GetControllerTypes(out typeA, out typeB);
        AttachController(pA, RequestedMatch.PlayerA, PlayerABotMode, areaId);
        AttachController(pB, RequestedMatch.PlayerB, PlayerBBotMode, areaId);
        ApplyScriptedShootPressureToPlayerB(pB, typeB);

        Debug.Log($"[Bootstrapper] Area {areaId}: {pA.Identity.DisplayName}={typeA}, {pB.Identity.DisplayName}={typeB}");
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

    private RealisticSpawnPlan BuildValidatedSpawnPlan(int areaId, Vector3 areaOffset)
    {
        RealisticSpawnPlan requested = BuildRealisticSpawnPlan(areaId, areaOffset);
        if (!DemoMapRuntime.ControlEnabled || !DemoMapRuntime.IsValid)
            return requested;

        Vector3 requestedA = requested.Enabled
            ? requested.PosA
            : (areaId == 0 && SpawnPointA != null
                ? SpawnPointA.GetSpawnPosition()
                : new Vector3(35f, 1f, -80f) + areaOffset);
        Vector3 requestedB = requested.Enabled
            ? requested.PosB
            : (areaId == 0 && SpawnPointB != null
                ? SpawnPointB.GetSpawnPosition()
                : new Vector3(35f, 1f, -77f) + areaOffset);

        int seed = unchecked(DemoMapRuntime.ActiveSeed * 73856093 + areaId * 19349663 + 17);
        System.Random rng = new System.Random(seed);
        if (!TryResolveInitialSpawn(requestedA, rng, out Vector3 posA, out string reasonA))
            throw new InvalidOperationException("Unable to resolve safe player A spawn: " + reasonA);
        if (!TryResolveOpponentSpawn(requestedB, posA, rng, out Vector3 posB, out string reasonB))
            throw new InvalidOperationException("Unable to resolve safe player B spawn: " + reasonB);

        Vector3 aToB = posB - posA;
        aToB.y = 0f;
        if (aToB.sqrMagnitude < 1f)
            aToB = Vector3.forward;

        requested.Enabled = true;
        requested.Bucket = requested.Bucket ?? "map_safe";
        requested.PosA = posA;
        requested.PosB = posB;
        requested.RotA = Quaternion.LookRotation(aToB.normalized, Vector3.up);
        requested.RotB = Quaternion.LookRotation(-aToB.normalized, Vector3.up);
        requested.Distance = Vector3.Distance(posA, posB);
        Debug.Log(
            $"[Bootstrapper] Safe map spawns map={DemoMapRuntime.ActiveMapId} "
            + $"distance={requested.Distance:F2} posA={posA} posB={posB}");
        return requested;
    }

    private static bool TryResolveInitialSpawn(
        Vector3 requested,
        System.Random rng,
        out Vector3 resolved,
        out string reason)
    {
        if (DemoMapRuntime.TryProjectToSurface(requested, out resolved, out reason)
            && DemoMapRuntime.IsSafeInitialSpawn(resolved))
            return true;

        for (int attempt = 0; attempt < 96; attempt++)
            if (DemoMapRuntime.TrySampleSurface(rng, out resolved, out reason)
                && DemoMapRuntime.IsSafeInitialSpawn(resolved))
                return true;

        resolved = requested;
        return false;
    }

    private static bool TryResolveOpponentSpawn(
        Vector3 requested,
        Vector3 anchor,
        System.Random rng,
        out Vector3 resolved,
        out string reason)
    {
        if (DemoMapRuntime.TryProjectToSurface(requested, out resolved, out reason)
            && DemoMapRuntime.IsSafeInitialSpawn(resolved)
            && HorizontalDistance(anchor, resolved) >= 3f)
            return true;

        for (int attempt = 0; attempt < 128; attempt++)
        {
            float angle = (float)(rng.NextDouble() * Math.PI * 2d);
            float distance = 5f + (float)rng.NextDouble() * 15f;
            Vector3 candidate = anchor + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
            candidate.y = anchor.y;
            if (!DemoMapRuntime.TryProjectToSurface(candidate, out resolved, out reason))
                continue;
            if (!DemoMapRuntime.IsSafeInitialSpawn(resolved))
                continue;
            if (HorizontalDistance(anchor, resolved) < 3f)
                continue;
            return true;
        }

        for (int attempt = 0; attempt < 96; attempt++)
        {
            if (!DemoMapRuntime.TrySampleSurface(rng, out resolved, out reason))
                continue;
            if (!DemoMapRuntime.IsSafeInitialSpawn(resolved))
                continue;
            if (HorizontalDistance(anchor, resolved) >= 3f)
                return true;
        }

        resolved = requested;
        return false;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
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
        if (RequestedMatch != null)
        {
            typeA = GameModePresets.ControllerTypeFor(RequestedMatch.PlayerA);
            typeB = GameModePresets.ControllerTypeFor(RequestedMatch.PlayerB);
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

    private void AttachController(PlayerBody body, PolicySpec policy, ScriptedBotController.ScriptedBotMode botMode, int areaId)
    {
        PolicyInstaller.Install(body, policy, botMode, GameCamera, AllowEnvironmentOverrides, areaId);
    }

    // ─────────────────────────────────────────────────────
    // Camera Setup
    // ─────────────────────────────────────────────────────

    private CameraView currentView = CameraView.Observer;
    public CameraView CurrentCameraView => currentView;

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

        if (HasHumanPlayer(CurrentGameMode))
            SwitchCameraTo(CameraView.PlayerA);
        else
            SwitchCameraTo(InitialCameraView);
    }

    private void Update()
    {
        if (GambitRuntimeMode.IsHeadless)
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
                if (playerA != null)
                    playerA.AttachCamera(GameCamera);
                else
                    currentView = CameraView.Observer;
                break;
            case CameraView.PlayerB:
                if (playerB != null)
                    playerB.AttachCamera(GameCamera);
                else
                    currentView = CameraView.Observer;
                break;
        }

        if (currentView == CameraView.Observer && view != CameraView.Observer)
            SetupObserverCamera();

        Debug.Log($"[Bootstrapper] Camera view: {currentView}");
    }

    private static bool HasHumanPlayer(GameMode mode)
    {
        return mode == GameMode.HumanVsRL
            || mode == GameMode.HumanVsScripted
            || mode == GameMode.HumanVsGambit;
    }

    private void SetupObserverCamera()
    {
        if (GameCamera == null || playerA == null || playerB == null) return;
        Vector3 midpoint = (playerA.transform.position + playerB.transform.position) / 2f;
        GameCamera.transform.position = midpoint + new Vector3(0f, 15f, -10f);
        GameCamera.transform.LookAt(midpoint);
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

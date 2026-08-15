using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Fail-closed GAMBIT DEMO map selector and geometry service. It is completely
/// inert unless PHASE4_MAP_CONTROL_ENABLED=1.
/// </summary>
public static class DemoMapRuntime
{
    public const string DefaultMapId = "arena_ascent_v1";
    private const string RegistryResourcePath = "Maps/map_catalog";
    private const float MaxWalkableSlopeDegrees = 45f;
    private const float SpawnCenterAboveGround = 1f;
    private const float SpawnFootingProbeRadius = 0.38f;
    private const float SpawnFootingHeightTolerance = 0.30f;
    private const float InitialSpawnFootingRadius = 2.5f;
    private const float InitialSpawnMaxElevationAboveMapBase = 12f;
    private const float MaxSpawnColliderSize = 2000f;
    private const float MaxSpawnColliderCenterOffset = 1000f;

    private static bool initialized;
    private static bool controlEnabled;
    private static bool valid;
    private static GameObject activeRoot;
    private static Bounds activeBounds;
    private static MapRegistry registry;
    private static MapDefinition activeDefinition;
    private static string requestedMapId = DefaultMapId;
    private static string activeMapId = DefaultMapId;
    private static string activeProfile = "original_only";
    private static int activeSeed;
    private static string registrySha256 = "";
    private static int rendererCount;
    private static int colliderCount;
    private static int totalColliderCount;
    private static Collider[] spawnGeometryColliders = new Collider[0];
    private static float loadMilliseconds;
    private static string failureReason = "";
    private static string demoMapId = "";

    /// <summary>Configures an interactive GAMBIT DEMO map without environment variables.</summary>
    public static void ConfigureForDemo(string mapId)
    {
        demoMapId = mapId ?? "";
    }

    public static bool ControlEnabled => controlEnabled;
    public static bool IsDemoConfigured => !string.IsNullOrWhiteSpace(demoMapId);
    public static bool IsValid => valid;
    public static string RequestedMapId => requestedMapId;
    public static string ActiveMapId => activeMapId;
    public static string ActiveProfile => activeProfile;
    public static int ActiveSeed => activeSeed;
    public static string RegistrySha256 => registrySha256;
    public static string ActiveAssetSha256 => activeDefinition != null ? activeDefinition.source_sha256 ?? "" : "";
    public static Bounds ActiveBounds => activeBounds;
    public static int ActiveRendererCount => rendererCount;
    public static int ActiveColliderCount => colliderCount;
    public static int TotalColliderCount => totalColliderCount;
    public static float LoadMilliseconds => loadMilliseconds;
    public static GameObject ActiveRoot => activeRoot;

    public static bool Initialize(int numAreas)
    {
        if (initialized)
            return valid;

        initialized = true;
        bool demoConfigured = !string.IsNullOrWhiteSpace(demoMapId);
        string controlRaw = demoConfigured ? "1" : (Environment.GetEnvironmentVariable("PHASE4_MAP_CONTROL_ENABLED") ?? "").Trim();
        if (!string.IsNullOrEmpty(controlRaw) && controlRaw != "0" && controlRaw != "1")
            return Fail("map_control_enabled_invalid:" + controlRaw);
        controlEnabled = controlRaw == "1";
        activeRoot = FindLoadedSceneObject("Environment_Map");
        requestedMapId = DefaultMapId;
        activeMapId = DefaultMapId;
        activeProfile = "original_only";
        activeSeed = 0;

        if (!controlEnabled)
        {
            valid = true;
            TryCalculateBounds(activeRoot, out activeBounds, out rendererCount, out colliderCount);
            return true;
        }

        float started = Time.realtimeSinceStartup;
        if (numAreas != 1)
            return Fail("controlled_map_requires_num_areas_1");
        string seedRaw = demoConfigured ? "1" : (Environment.GetEnvironmentVariable("PHASE4_MAP_SEED") ?? "").Trim();
        if (string.IsNullOrEmpty(seedRaw) || !int.TryParse(seedRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out activeSeed))
            return Fail("map_seed_missing_or_invalid:" + seedRaw);
        if (Environment.GetEnvironmentVariable("PHASE4_WRITE_MAP_TELEMETRY") == "1"
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHASE4_MAP_TELEMETRY_PATH")))
            return Fail("map_telemetry_path_missing");

        TextAsset registryAsset = Resources.Load<TextAsset>(RegistryResourcePath);
        if (registryAsset == null)
            return Fail("map_registry_resource_missing");
        registrySha256 = Sha256Text(registryAsset.text);

        try
        {
            registry = JsonUtility.FromJson<MapRegistry>(registryAsset.text);
        }
        catch (Exception ex)
        {
            return Fail("map_registry_parse_failed:" + ex.Message);
        }
        if (!ValidateRegistry(registry, out string registryFailure))
            return Fail("map_registry_invalid:" + registryFailure);

        requestedMapId = demoConfigured ? demoMapId : (Environment.GetEnvironmentVariable("PHASE4_MAP_ID") ?? "").Trim();
        activeProfile = demoConfigured ? "original_only" : (Environment.GetEnvironmentVariable("PHASE4_MAP_MIX_PROFILE") ?? "").Trim();
        if (string.IsNullOrEmpty(requestedMapId))
            return Fail("map_id_missing");
        if (string.IsNullOrEmpty(activeProfile) || FindProfile(activeProfile) == null)
            return Fail("map_profile_missing_or_unknown:" + activeProfile);

        activeMapId = requestedMapId.Equals("random", StringComparison.OrdinalIgnoreCase)
            ? ResolveProfileMap(activeProfile, activeSeed)
            : requestedMapId;
        activeDefinition = FindMap(activeMapId);
        if (activeDefinition == null || !activeDefinition.enabled)
            return Fail("map_id_unknown_or_disabled:" + activeMapId);

        GameObject bakedAscent = activeRoot;
        if (activeDefinition.baked_default)
        {
            if (bakedAscent == null)
                return Fail(
                    "baked_default_map_missing: open Assets/Scenes/BotArena.unity "
                    + "or configure it as the Editor Play Mode start scene");
            bakedAscent.SetActive(true);
            activeRoot = bakedAscent;
        }
        else
        {
            if (string.IsNullOrEmpty(activeDefinition.resource_path))
                return Fail("map_resource_path_missing:" + activeMapId);
            GameObject prefab = Resources.Load<GameObject>(activeDefinition.resource_path);
            if (prefab == null)
                return Fail("map_prefab_resource_missing:" + activeDefinition.resource_path);
            if (bakedAscent != null)
                bakedAscent.SetActive(false);
            activeRoot = UnityEngine.Object.Instantiate(prefab);
            activeRoot.name = "Environment_Map";
            activeRoot.transform.position = activeDefinition.position != null ? activeDefinition.position.ToVector3() : Vector3.zero;
            activeRoot.transform.rotation = Quaternion.Euler(activeDefinition.rotation_euler != null ? activeDefinition.rotation_euler.ToVector3() : Vector3.zero);
            activeRoot.transform.localScale = activeDefinition.scale != null ? activeDefinition.scale.ToVector3() : Vector3.one;
        }

        Physics.SyncTransforms();
        if (!TryCalculateBounds(activeRoot, out activeBounds, out rendererCount, out colliderCount))
            return Fail("active_map_has_no_finite_bounds");
        if (rendererCount <= 0)
            return Fail("active_map_has_no_renderers");
        if (colliderCount <= 0)
            return Fail("active_map_has_no_colliders");

        loadMilliseconds = Mathf.Max(0f, (Time.realtimeSinceStartup - started) * 1000f);
        valid = true;
        failureReason = "";
        if (!demoConfigured)
        {
            Environment.SetEnvironmentVariable("PHASE4_ACTIVE_MAP_ID", activeMapId);
            Environment.SetEnvironmentVariable("PHASE4_ACTIVE_MAP_PROFILE", activeProfile);
            Environment.SetEnvironmentVariable("PHASE4_ACTIVE_MAP_SEED", activeSeed.ToString(CultureInfo.InvariantCulture));
        }
        WriteTelemetry("map_loaded", "PASS", "");
        Debug.Log($"[DemoMap] active={activeMapId} requested={requestedMapId} profile={activeProfile} seed={activeSeed} renderers={rendererCount} colliders={colliderCount} bounds={activeBounds}");
        return true;
    }

    public static bool TryProjectToSurface(Vector3 requestedPosition, out Vector3 spawnCenter, out string reason)
    {
        spawnCenter = requestedPosition;
        reason = "";
        if (!controlEnabled || !valid || activeRoot == null)
        {
            reason = "map_runtime_not_active";
            return false;
        }
        if (!IsInsideHorizontalBounds(requestedPosition, 0f))
        {
            reason = "outside_map_bounds";
            return false;
        }

        float top = activeBounds.max.y + 10f;
        float distance = Mathf.Max(20f, activeBounds.size.y + 20f);
        Vector3 origin = new Vector3(requestedPosition.x, top, requestedPosition.z);
        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore);
        bool found = false;
        float bestElevationDelta = float.PositiveInfinity;
        Vector3 bestCandidate = requestedPosition;
        foreach (RaycastHit hit in hits)
        {
            if (!IsSpawnGeometryCollider(hit.collider))
                continue;
            if (Vector3.Angle(hit.normal, Vector3.up) > MaxWalkableSlopeDegrees)
                continue;
            Vector3 candidate = hit.point + Vector3.up * SpawnCenterAboveGround;
            if (!HasCapsuleClearance(candidate))
                continue;
            if (!HasStableFooting(candidate))
                continue;

            // Prefer the surface nearest the requested elevation. Sorting by
            // ray distance selected the highest roof above an otherwise valid
            // ground-level request.
            float elevationDelta = Mathf.Abs(candidate.y - requestedPosition.y);
            if (elevationDelta >= bestElevationDelta)
                continue;
            found = true;
            bestElevationDelta = elevationDelta;
            bestCandidate = candidate;
        }
        if (found)
        {
            spawnCenter = bestCandidate;
            return true;
        }
        reason = "no_walkable_surface_or_clearance";
        return false;
    }

    public static bool TrySampleSurface(System.Random rng, out Vector3 spawnCenter, out string reason)
    {
        spawnCenter = Vector3.zero;
        reason = "";
        if (!controlEnabled || !valid || rng == null || spawnGeometryColliders == null || spawnGeometryColliders.Length == 0)
        {
            reason = "spawn_geometry_colliders_unavailable";
            return false;
        }
        string lastReason = "surface_sample_not_attempted";
        for (int attempt = 0; attempt < 32; attempt++)
        {
            Collider collider = spawnGeometryColliders[rng.Next(0, spawnGeometryColliders.Length)];
            if (collider == null || !collider.enabled)
                continue;
            Bounds bounds = collider.bounds;
            Vector3 request = new Vector3(
                bounds.min.x + (float)rng.NextDouble() * Mathf.Max(0.01f, bounds.size.x),
                activeRoot.transform.position.y + SpawnCenterAboveGround,
                bounds.min.z + (float)rng.NextDouble() * Mathf.Max(0.01f, bounds.size.z));
            if (TryProjectToSurface(request, out spawnCenter, out lastReason))
                return true;
        }
        reason = "collider_guided_surface_sample_failed:" + lastReason;
        return false;
    }

    public static bool HasCapsuleClearance(Vector3 spawnCenter)
    {
        Vector3 lower = spawnCenter + Vector3.down * 0.45f;
        Vector3 upper = spawnCenter + Vector3.up * 0.45f;
        Collider[] overlaps = Physics.OverlapCapsule(lower, upper, 0.40f, ~0, QueryTriggerInteraction.Ignore);
        foreach (Collider overlap in overlaps)
        {
            if (overlap != null && IsInActiveMap(overlap.transform))
                return false;
        }
        return true;
    }

    /// <summary>Requires level map geometry beneath the capsule center and perimeter.</summary>
    public static bool HasStableFooting(Vector3 spawnCenter)
    {
        return HasStableFooting(spawnCenter, SpawnFootingProbeRadius);
    }

    /// <summary>
    /// Initial players need a broad ground patch and a ground-level elevation;
    /// a capsule-sized roof patch is not an acceptable match start location.
    /// </summary>
    public static bool IsSafeInitialSpawn(Vector3 spawnCenter)
    {
        if (activeRoot == null)
            return false;
        float mapBaseCenterY = activeRoot.transform.position.y + SpawnCenterAboveGround;
        if (spawnCenter.y - mapBaseCenterY > InitialSpawnMaxElevationAboveMapBase)
            return false;
        return HasStableFooting(spawnCenter, InitialSpawnFootingRadius);
    }

    private static bool HasStableFooting(Vector3 spawnCenter, float probeRadius)
    {
        float expectedSurfaceY = spawnCenter.y - SpawnCenterAboveGround;
        Vector3[] offsets =
        {
            Vector3.zero,
            new Vector3(probeRadius, 0f, 0f),
            new Vector3(-probeRadius, 0f, 0f),
            new Vector3(0f, 0f, probeRadius),
            new Vector3(0f, 0f, -probeRadius),
            new Vector3(probeRadius * 0.707f, 0f, probeRadius * 0.707f),
            new Vector3(-probeRadius * 0.707f, 0f, probeRadius * 0.707f),
            new Vector3(probeRadius * 0.707f, 0f, -probeRadius * 0.707f),
            new Vector3(-probeRadius * 0.707f, 0f, -probeRadius * 0.707f)
        };
        foreach (Vector3 offset in offsets)
        {
            Vector3 origin = spawnCenter + offset + Vector3.up * 0.15f;
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 1.5f, ~0, QueryTriggerInteraction.Ignore);
            bool supported = false;
            foreach (RaycastHit hit in hits)
            {
                if (!IsSpawnGeometryCollider(hit.collider))
                    continue;
                if (Vector3.Angle(hit.normal, Vector3.up) > MaxWalkableSlopeDegrees)
                    continue;
                if (Mathf.Abs(hit.point.y - expectedSurfaceY) > SpawnFootingHeightTolerance)
                    continue;
                supported = true;
                break;
            }
            if (!supported)
                return false;
        }
        return true;
    }

    public static bool IsInsideHorizontalBounds(Vector3 position, float margin)
    {
        if (!controlEnabled || !valid)
            return true;
        return position.x >= activeBounds.min.x - margin
            && position.x <= activeBounds.max.x + margin
            && position.z >= activeBounds.min.z - margin
            && position.z <= activeBounds.max.z + margin;
    }

    public static bool IsFallDetected(Vector3 position)
    {
        return controlEnabled && valid && position.y < activeBounds.min.y - 5f;
    }

    public static bool IsOutOfBounds(Vector3 position)
    {
        return controlEnabled && valid && (!IsInsideHorizontalBounds(position, 5f) || position.y > activeBounds.max.y + 20f);
    }

    public static bool IsInActiveMap(Transform candidate)
    {
        if (candidate == null || activeRoot == null)
            return false;
        Transform root = activeRoot.transform;
        return candidate == root || candidate.IsChildOf(root);
    }

    private static bool Fail(string reason)
    {
        valid = false;
        failureReason = reason;
        loadMilliseconds = 0f;
        Debug.LogError("[DemoMap] " + reason);
        WriteTelemetry("map_load_failed", "FAIL", reason);
        Application.Quit(47);
        return false;
    }

    /// <summary>
    /// Finds a scene object even when it is inactive. GameObject.Find only
    /// searches active objects, but the baked default map may be disabled by
    /// editor state or map-selection startup ordering before initialization.
    /// </summary>
    private static GameObject FindLoadedSceneObject(string objectName)
    {
        Scene activeScene = SceneManager.GetActiveScene();
        GameObject found = FindInScene(activeScene, objectName);
        if (found != null)
            return found;

        for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (scene == activeScene)
                continue;
            found = FindInScene(scene, objectName);
            if (found != null)
                return found;
        }
        return null;
    }

    private static GameObject FindInScene(Scene scene, string objectName)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return null;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (string.Equals(root.name, objectName, StringComparison.Ordinal))
                return root;

            Transform[] descendants = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform descendant in descendants)
            {
                if (descendant != null
                    && string.Equals(descendant.name, objectName, StringComparison.Ordinal))
                    return descendant.gameObject;
            }
        }
        return null;
    }

    private static MapDefinition FindMap(string mapId)
    {
        if (registry == null || registry.maps == null)
            return null;
        foreach (MapDefinition map in registry.maps)
            if (map != null && string.Equals(map.map_id, mapId, StringComparison.Ordinal))
                return map;
        return null;
    }

    private static MapProfile FindProfile(string profileId)
    {
        if (registry == null || registry.profiles == null)
            return null;
        foreach (MapProfile profile in registry.profiles)
            if (profile != null && string.Equals(profile.profile_id, profileId, StringComparison.Ordinal))
                return profile;
        return null;
    }

    private static string ResolveProfileMap(string profileId, int seed)
    {
        MapProfile profile = FindProfile(profileId);
        if (profile == null || profile.members == null || profile.members.Length == 0)
            return "";
        double total = 0.0;
        foreach (MapProfileMember member in profile.members)
            if (member != null && member.weight > 0f && FindMap(member.map_id) != null)
                total += member.weight;
        if (total <= 0.0)
            return "";
        double pick = StableUnitInterval(profileId + "|" + seed.ToString(CultureInfo.InvariantCulture)) * total;
        double cumulative = 0.0;
        foreach (MapProfileMember member in profile.members)
        {
            if (member == null || member.weight <= 0f || FindMap(member.map_id) == null)
                continue;
            cumulative += member.weight;
            if (pick < cumulative)
                return member.map_id;
        }
        return profile.members[profile.members.Length - 1].map_id;
    }

    private static bool ValidateRegistry(MapRegistry candidate, out string reason)
    {
        reason = "";
        if (candidate == null || candidate.schema_version != "gambit_map_catalog_v1")
        {
            reason = "schema_version";
            return false;
        }
        if (candidate.maps == null || candidate.maps.Length != 3)
        {
            reason = "expected_three_maps";
            return false;
        }
        if (candidate.profiles == null || candidate.profiles.Length != 3)
        {
            reason = "expected_three_profiles";
            return false;
        }
        System.Collections.Generic.HashSet<string> mapIds = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        int bakedDefaults = 0;
        foreach (MapDefinition map in candidate.maps)
        {
            if (map == null || string.IsNullOrEmpty(map.map_id) || string.IsNullOrEmpty(map.source_sha256))
            {
                reason = "map_missing_identity_or_sha";
                return false;
            }
            if (!mapIds.Add(map.map_id))
            {
                reason = "duplicate_map_id:" + map.map_id;
                return false;
            }
            if (map.source_sha256.Length != 64)
            {
                reason = "invalid_map_sha256:" + map.map_id;
                return false;
            }
            if (map.baked_default)
                bakedDefaults++;
            else if (string.IsNullOrEmpty(map.resource_path))
            {
                reason = "resource_path_missing:" + map.map_id;
                return false;
            }
        }
        if (bakedDefaults != 1)
        {
            reason = "expected_one_baked_default";
            return false;
        }
        System.Collections.Generic.HashSet<string> profileIds = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (MapProfile profile in candidate.profiles)
        {
            if (profile == null || string.IsNullOrEmpty(profile.profile_id) || profile.members == null || profile.members.Length == 0)
            {
                reason = "profile_missing_members";
                return false;
            }
            if (!profileIds.Add(profile.profile_id))
            {
                reason = "duplicate_profile_id:" + profile.profile_id;
                return false;
            }
            float total = 0f;
            System.Collections.Generic.HashSet<string> memberIds = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (MapProfileMember member in profile.members)
            {
                if (member == null || string.IsNullOrEmpty(member.map_id) || !mapIds.Contains(member.map_id) || member.weight <= 0f || !memberIds.Add(member.map_id))
                {
                    reason = "invalid_profile_member:" + profile.profile_id;
                    return false;
                }
                total += member.weight;
            }
            if (Mathf.Abs(total - 1f) > 1e-5f)
            {
                reason = "profile_weight_sum:" + profile.profile_id;
                return false;
            }
        }
        return true;
    }

    private static bool TryCalculateBounds(GameObject root, out Bounds bounds, out int renderers, out int colliders)
    {
        bounds = new Bounds(Vector3.zero, Vector3.zero);
        renderers = 0;
        colliders = 0;
        if (root == null)
            return false;
        Renderer[] allRenderers = root.GetComponentsInChildren<Renderer>(true);
        Collider[] allColliders = root.GetComponentsInChildren<Collider>(true);
        renderers = allRenderers.Length;
        totalColliderCount = allColliders.Length;
        colliders = 0;
        bool hasBounds = false;
        System.Collections.Generic.List<Collider> finiteSized = new System.Collections.Generic.List<Collider>();
        System.Collections.Generic.List<float> centerXs = new System.Collections.Generic.List<float>();
        System.Collections.Generic.List<float> centerZs = new System.Collections.Generic.List<float>();
        foreach (Collider collider in allColliders)
        {
            if (collider == null || !collider.enabled)
                continue;
            Bounds candidateBounds = collider.bounds;
            if (!IsFinite(candidateBounds.center) || !IsFinite(candidateBounds.size))
                continue;
            if (candidateBounds.size.x > MaxSpawnColliderSize
                || candidateBounds.size.y > MaxSpawnColliderSize
                || candidateBounds.size.z > MaxSpawnColliderSize)
                continue;
            finiteSized.Add(collider);
            centerXs.Add(candidateBounds.center.x);
            centerZs.Add(candidateBounds.center.z);
        }

        centerXs.Sort();
        centerZs.Sort();
        float medianX = centerXs.Count > 0 ? centerXs[centerXs.Count / 2] : 0f;
        float medianZ = centerZs.Count > 0 ? centerZs[centerZs.Count / 2] : 0f;
        System.Collections.Generic.List<Collider> accepted = new System.Collections.Generic.List<Collider>();
        foreach (Collider collider in finiteSized)
        {
            Bounds candidateBounds = collider.bounds;
            if (Mathf.Abs(candidateBounds.center.x - medianX) > MaxSpawnColliderCenterOffset
                || Mathf.Abs(candidateBounds.center.z - medianZ) > MaxSpawnColliderCenterOffset)
                continue;
            colliders++;
            accepted.Add(collider);
            if (!hasBounds)
            {
                bounds = candidateBounds;
                hasBounds = true;
            }
            else
                bounds.Encapsulate(candidateBounds);
        }
        spawnGeometryColliders = accepted.ToArray();
        if (!hasBounds)
        {
            foreach (Renderer renderer in allRenderers)
            {
                if (renderer == null || !renderer.enabled)
                    continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                    bounds.Encapsulate(renderer.bounds);
            }
        }
        return hasBounds && IsFinite(bounds.center) && IsFinite(bounds.size) && bounds.size.sqrMagnitude > 1f;
    }

    private static bool IsSpawnGeometryCollider(Collider collider)
    {
        if (collider == null || !collider.enabled || !IsInActiveMap(collider.transform))
            return false;
        Bounds candidateBounds = collider.bounds;
        if (!IsFinite(candidateBounds.center) || !IsFinite(candidateBounds.size))
            return false;
        if (candidateBounds.size.x > MaxSpawnColliderSize
            || candidateBounds.size.y > MaxSpawnColliderSize
            || candidateBounds.size.z > MaxSpawnColliderSize)
            return false;
        return candidateBounds.center.x >= activeBounds.min.x - 1f
            && candidateBounds.center.x <= activeBounds.max.x + 1f
            && candidateBounds.center.z >= activeBounds.min.z - 1f
            && candidateBounds.center.z <= activeBounds.max.z + 1f;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    private static double StableUnitInterval(string payload)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
            ulong value = 0UL;
            for (int i = 0; i < 8; i++)
                value = (value << 8) | digest[i];
            return value / ((double)ulong.MaxValue + 1.0);
        }
    }

    private static string Sha256Text(string payload)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
            StringBuilder builder = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest)
                builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }

    private static int ParseIntEnv(string key, int fallback)
    {
        string raw = Environment.GetEnvironmentVariable(key) ?? "";
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
    }

    private static void WriteTelemetry(string eventType, string status, string reason)
    {
        if (Environment.GetEnvironmentVariable("PHASE4_WRITE_MAP_TELEMETRY") != "1")
            return;
        string path = Environment.GetEnvironmentVariable("PHASE4_MAP_TELEMETRY_PATH") ?? "";
        if (string.IsNullOrEmpty(path))
            path = Path.Combine(Application.persistentDataPath, "phase4_7_map_telemetry.jsonl");
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            MapTelemetryRow row = new MapTelemetryRow();
            row.timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            row.event_type = eventType;
            row.status = status;
            row.failure_reason = reason ?? "";
            row.map_control_enabled = controlEnabled;
            row.requested_map_id = requestedMapId ?? "";
            row.active_map_id = activeMapId ?? "";
            row.map_profile = activeProfile ?? "";
            row.map_seed = activeSeed;
            row.registry_sha256 = registrySha256 ?? "";
            row.map_asset_sha256 = ActiveAssetSha256;
            row.renderer_count = rendererCount;
            row.collider_count = colliderCount;
            row.total_collider_count = totalColliderCount;
            row.load_milliseconds = loadMilliseconds;
            row.bounds_min_x = activeBounds.min.x;
            row.bounds_min_y = activeBounds.min.y;
            row.bounds_min_z = activeBounds.min.z;
            row.bounds_max_x = activeBounds.max.x;
            row.bounds_max_y = activeBounds.max.y;
            row.bounds_max_z = activeBounds.max.z;
            File.AppendAllText(path, JsonUtility.ToJson(row) + "\n");
        }
        catch (Exception ex)
        {
            Debug.LogError("[DemoMap] telemetry write failed: " + ex.Message);
        }
    }

    [Serializable]
    public class MapRegistry
    {
        public string schema_version;
        public MapDefinition[] maps;
        public MapProfile[] profiles;
    }

    [Serializable]
    public class MapDefinition
    {
        public string map_id;
        public string display_name;
        public string resource_path;
        public string source_asset_path;
        public string source_sha256;
        public bool baked_default;
        public bool enabled;
        public SerializableVector3 position;
        public SerializableVector3 rotation_euler;
        public SerializableVector3 scale;
    }

    [Serializable]
    public class MapProfile
    {
        public string profile_id;
        public MapProfileMember[] members;
    }

    [Serializable]
    public class MapProfileMember
    {
        public string map_id;
        public float weight;
    }

    [Serializable]
    public class SerializableVector3
    {
        public float x;
        public float y;
        public float z;
        public Vector3 ToVector3() { return new Vector3(x, y, z); }
    }

    [Serializable]
    private class MapTelemetryRow
    {
        public string timestamp;
        public string event_type;
        public string status;
        public string failure_reason;
        public bool map_control_enabled;
        public string requested_map_id;
        public string active_map_id;
        public string map_profile;
        public int map_seed;
        public string registry_sha256;
        public string map_asset_sha256;
        public int renderer_count;
        public int collider_count;
        public int total_collider_count;
        public float load_milliseconds;
        public float bounds_min_x;
        public float bounds_min_y;
        public float bounds_min_z;
        public float bounds_max_x;
        public float bounds_max_y;
        public float bounds_max_z;
    }
}

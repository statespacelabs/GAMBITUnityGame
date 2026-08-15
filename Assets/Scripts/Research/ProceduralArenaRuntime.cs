using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

/// <summary>
/// Opt-in, fail-closed procedural arena loader. Layout manifests are
/// generated and validated offline; this class only constructs simulation
/// colliders and never exposes manifest or connectivity data to actor sensors.
/// </summary>
public static class ProceduralArenaRuntime
{
    public const string LayoutSchema = "phase5_procedural_layout_v001";
    public const string SuiteSchema = "phase5_procedural_layout_suite_v001";
    private const float IsolationHeight = 1000f;

    private static bool initialized;
    private static bool enabled;
    private static bool valid;
    private static LayoutManifest manifest;
    private static readonly List<AreaRuntime> areas = new List<AreaRuntime>();
    private static string manifestPath = "";
    private static string manifestSha256 = "";
    private static string failureReason = "";

    public static bool Enabled => enabled;
    public static bool IsValid => valid;
    public static string FailureReason => failureReason;
    public static string ManifestSha256 => manifestSha256;

    public static bool Initialize(int numAreas)
    {
        if (initialized)
            return !enabled || valid;
        initialized = true;

        string raw = (Environment.GetEnvironmentVariable("PHASE5_PROCEDURAL_LAYOUT") ?? "").Trim();
        if (!string.IsNullOrEmpty(raw) && raw != "0" && raw != "1")
            return Fail("enable_flag_invalid:" + raw);
        enabled = raw == "1";
        if (!enabled)
        {
            valid = true;
            return true;
        }

        if (!HeadlessTrainingRuntime.Enabled)
            return Fail("requires_phase5_headless");
        if ((Environment.GetEnvironmentVariable("PHASE4_MAP_CONTROL_ENABLED") ?? "0").Trim() == "1")
            return Fail("phase4_map_control_conflict");
        if (numAreas < 1)
            return Fail("num_areas_invalid");

        manifestPath = (Environment.GetEnvironmentVariable("PHASE5_LAYOUT_MANIFEST") ?? "").Trim();
        if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
            return Fail("manifest_missing:" + manifestPath);

        string json;
        try
        {
            json = File.ReadAllText(manifestPath);
            manifestSha256 = Sha256File(manifestPath);
            manifest = JsonUtility.FromJson<LayoutManifest>(json);
        }
        catch (Exception ex)
        {
            return Fail("manifest_read_or_parse_failed:" + ex.Message);
        }

        string expectedHash = (Environment.GetEnvironmentVariable("PHASE5_LAYOUT_MANIFEST_SHA256") ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(expectedHash) && expectedHash != manifestSha256)
            return Fail("manifest_sha256_mismatch");
        if (manifest == null || manifest.schema_version != SuiteSchema)
            return Fail("suite_schema_invalid");
        if (!manifest.immutable)
            return Fail("manifest_not_immutable");
        if (manifest.actor_access_to_occupancy_or_navmesh)
            return Fail("actor_connectivity_access_enabled");
        if (manifest.layouts == null || manifest.layouts.Length == 0)
            return Fail("manifest_has_no_layouts");

        string expectedSplit = (Environment.GetEnvironmentVariable("PHASE5_LAYOUT_SPLIT") ?? "").Trim();
        if (!string.IsNullOrEmpty(expectedSplit) && !string.Equals(expectedSplit, manifest.split, StringComparison.Ordinal))
            return Fail("manifest_split_mismatch");

        int[] requestedSeeds;
        if (!TryResolveSeeds(numAreas, out requestedSeeds))
            return false;
        for (int areaId = 0; areaId < numAreas; areaId++)
        {
            LayoutRecord layout = FindLayout(requestedSeeds[areaId]);
            if (!ValidateRecord(layout, manifest.split, out string reason))
                return Fail("layout_invalid:area=" + areaId + ":" + reason);
            areas.Add(new AreaRuntime { area_id = areaId, layout = layout });
        }

        valid = true;
        failureReason = "";
        Debug.Log("[ProceduralArena] initialized split=" + manifest.split
            + " areas=" + numAreas + " manifest_sha256=" + manifestSha256);
        return true;
    }

    public static bool BuildArea(int areaId, Vector3 areaOffset, Transform arenaParent)
    {
        if (!enabled)
            return true;
        AreaRuntime area = FindArea(areaId);
        if (!valid || area == null || area.built)
            return area != null && area.built;

        LayoutRecord layout = area.layout;
        GameObject rootObject = new GameObject("_ProceduralArena_" + areaId + "_" + layout.seed);
        rootObject.transform.SetParent(arenaParent, false);
        rootObject.transform.localPosition = new Vector3(0f, IsolationHeight, 0f);
        area.root = rootObject;
        area.origin = areaOffset + new Vector3(0f, IsolationHeight, 0f);

        area.floor = AddBox(rootObject.transform, "floor", layout.floor, null);
        PhysicMaterial floorMaterial = new PhysicMaterial("ProceduralFloor_" + layout.seed);
        floorMaterial.dynamicFriction = layout.parameters.floor_dynamic_friction;
        floorMaterial.staticFriction = layout.parameters.floor_static_friction;
        floorMaterial.frictionCombine = PhysicMaterialCombine.Average;
        area.floor.sharedMaterial = floorMaterial;

        for (int i = 0; i < layout.obstacles.Length; i++)
        {
            BoxCollider collider = AddBox(rootObject.transform, layout.obstacles[i].id, layout.obstacles[i], null);
            area.obstacles.Add(collider);
        }
        Physics.SyncTransforms();
        area.built = true;
        return true;
    }

    public static bool TryGetSpawn(
        int areaId,
        int playerIndex,
        out Vector3 position,
        out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (!enabled)
            return false;
        AreaRuntime area = FindArea(areaId);
        if (area == null || !area.built)
            return false;
        SpawnRecord spawn = playerIndex == 0 ? area.layout.spawn_a : area.layout.spawn_b;
        position = area.origin + ToVector3(spawn.position);
        rotation = Quaternion.Euler(0f, spawn.yaw_degrees, 0f);
        return true;
    }

    public static void ApplyGameplayParameters(int areaId, PlayerBody playerA, PlayerBody playerB)
    {
        if (!enabled)
            return;
        AreaRuntime area = FindArea(areaId);
        if (area == null)
            return;
        float scale = Mathf.Clamp(area.layout.parameters.movement_speed_scale, 0.5f, 2f);
        if (playerA != null && playerA.Motor != null)
            playerA.Motor.MoveSpeed *= scale;
        if (playerB != null && playerB.Motor != null)
            playerB.Motor.MoveSpeed *= scale;

        ScriptedBotController bot = playerB != null ? playerB.GetComponent<ScriptedBotController>() : null;
        if (bot != null)
        {
            switch (area.layout.parameters.target_motion)
            {
                case "stationary":
                    bot.BotMode = ScriptedBotController.ScriptedBotMode.Idle;
                    break;
                case "strafe":
                    bot.BotMode = ScriptedBotController.ScriptedBotMode.StrafeAndFace;
                    break;
                case "alternating_cover":
                    bot.BotMode = ScriptedBotController.ScriptedBotMode.StrafeAndFaceShoot;
                    bot.StrafeChangeInterval = 0.8f;
                    break;
                case "patrol":
                    bot.BotMode = ScriptedBotController.ScriptedBotMode.ChaseOpponent;
                    break;
            }
        }
    }

    public static bool TryGetAreaDescriptor(
        int areaId,
        out int seed,
        out string split,
        out string family,
        out Vector3 spawnA,
        out Vector3 spawnB,
        out bool requestedInitialLos,
        out float optionalGeodesicRouteLength)
    {
        seed = 0;
        split = "";
        family = "";
        spawnA = Vector3.zero;
        spawnB = Vector3.zero;
        requestedInitialLos = false;
        optionalGeodesicRouteLength = 0f;
        AreaRuntime area = FindArea(areaId);
        if (!enabled || !valid || area == null || !area.built || area.layout == null)
            return false;
        seed = area.layout.seed;
        split = area.layout.split;
        family = area.layout.family;
        spawnA = area.origin + ToVector3(area.layout.spawn_a.position);
        spawnB = area.origin + ToVector3(area.layout.spawn_b.position);
        requestedInitialLos = area.layout.requested_initial_los;
        optionalGeodesicRouteLength = area.layout.validation != null
            ? area.layout.validation.route_length_m
            : 0f;
        return true;
    }

    // Teacher-only geometry handoff. Bounds never enter an Agent sensor or rollout row.
    public static bool TryGetPrivilegedTeacherGeometry(
        int areaId,
        out Bounds floorBounds,
        out Bounds[] obstacleBounds)
    {
        floorBounds = new Bounds();
        obstacleBounds = new Bounds[0];
        if (!PrivilegedTeacher.Enabled)
            return false;
        AreaRuntime area = FindArea(areaId);
        if (!enabled || !valid || area == null || !area.built || area.floor == null)
            return false;
        floorBounds = area.floor.bounds;
        List<Bounds> interior = new List<Bounds>();
        for (int i = 0; i < area.obstacles.Count; i++)
        {
            // The frozen connectivity proof constrains the grid to the floor
            // bounds and excludes perimeter records from occupancy inflation.
            // Mirror that contract so narrow, valid edge corridors are not
            // closed a second time by expanded perimeter colliders.
            PrimitiveRecord record = area.layout.obstacles[i];
            if (record != null && record.kind == "boundary")
                continue;
            interior.Add(area.obstacles[i].bounds);
        }
        obstacleBounds = interior.ToArray();
        return true;
    }

    // Goal 8-only handoff to the runtime NavMesh builder. The actor never sees
    // colliders, bounds, layout records, seeds, or world coordinates; it only
    // receives the bounded egocentric route schema produced by the sensor.
    public static bool TryGetNavMeshBuildGeometry(
        int areaId,
        out BoxCollider floor,
        out Collider[] obstacles,
        out Bounds bounds)
    {
        floor = null;
        obstacles = new Collider[0];
        bounds = new Bounds();
        if (!RuntimeNavMesh.Enabled)
            return false;
        AreaRuntime area = FindArea(areaId);
        if (!enabled || !valid || area == null || !area.built || area.floor == null)
            return false;
        floor = area.floor;
        obstacles = area.obstacles.ToArray();
        bounds = floor.bounds;
        for (int i = 0; i < obstacles.Length; i++)
            if (obstacles[i] != null)
                bounds.Encapsulate(obstacles[i].bounds);
        return true;
    }

    public static bool FinalizeAndWriteAudit(PlayerBody[] playerA, PlayerBody[] playerB)
    {
        if (!enabled)
            return true;
        Physics.SyncTransforms();
        bool allPassed = valid && playerA != null && playerB != null
            && playerA.Length == areas.Count && playerB.Length == areas.Count;
        List<AreaAudit> auditAreas = new List<AreaAudit>();
        for (int i = 0; i < areas.Count; i++)
        {
            AreaRuntime area = areas[i];
            AreaAudit audit = AuditArea(area,
                playerA != null && i < playerA.Length ? playerA[i] : null,
                playerB != null && i < playerB.Length ? playerB[i] : null);
            auditAreas.Add(audit);
            allPassed &= audit.status == "PASS";
        }

        RuntimeAudit output = new RuntimeAudit
        {
            schema_version = "phase5_procedural_layout_runtime_audit_v001",
            status = allPassed ? "PASS" : "FAIL",
            manifest_path = manifestPath,
            manifest_sha256 = manifestSha256,
            split = manifest != null ? manifest.split : "",
            area_count = areas.Count,
            actor_connectivity_access = false,
            areas = auditAreas.ToArray(),
            failure_reason = allPassed ? "" : failureReason,
        };
        string json = JsonUtility.ToJson(output, true);
        string auditPath = (Environment.GetEnvironmentVariable("PHASE5_LAYOUT_AUDIT_PATH") ?? "").Trim();
        if (string.IsNullOrEmpty(auditPath))
            return Fail("audit_path_missing");
        try
        {
            string directory = Path.GetDirectoryName(auditPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(auditPath, json + "\n");
        }
        catch (Exception ex)
        {
            return Fail("audit_write_failed:" + ex.Message);
        }
        Debug.Log("[ProceduralArenaAudit] " + JsonUtility.ToJson(output));
        if (!allPassed)
            return Fail("runtime_audit_failed");
        return true;
    }

    private static AreaAudit AuditArea(AreaRuntime area, PlayerBody playerA, PlayerBody playerB)
    {
        bool floorA = HasFloorBelow(area, playerA);
        bool floorB = HasFloorBelow(area, playerB);
        bool clearA = HasObstacleClearance(area, playerA);
        bool clearB = HasObstacleClearance(area, playerB);
        bool noPlayerOverlap = playerA != null && playerB != null
            && Vector3.Distance(playerA.transform.position, playerB.transform.position) >= 1.01f;
        bool actualLos = HasLineOfSight(area, playerA, playerB);
        bool requestedLos = actualLos == area.layout.requested_initial_los;
        bool colliderCount = area.obstacles.Count == area.layout.obstacles.Length
            && area.floor != null && area.floor.enabled;
        bool frozenProof = area.layout.validation != null
            && area.layout.validation.status == "PASS"
            && area.layout.validation.checks != null
            && area.layout.validation.checks.collision_free_route_exists
            && area.layout.validation.checks.no_unreachable_sealed_pockets;
        bool pass = floorA && floorB && clearA && clearB && noPlayerOverlap
            && requestedLos && colliderCount && frozenProof;
        return new AreaAudit
        {
            area_id = area.area_id,
            seed = area.layout.seed,
            family = area.layout.family,
            status = pass ? "PASS" : "FAIL",
            floor_below_a = floorA,
            floor_below_b = floorB,
            spawn_a_obstacle_clear = clearA,
            spawn_b_obstacle_clear = clearB,
            players_not_overlapping = noPlayerOverlap,
            requested_initial_los = area.layout.requested_initial_los,
            actual_initial_los = actualLos,
            requested_los_satisfied = requestedLos,
            collider_count_matches = colliderCount,
            collider_count = area.obstacles.Count + (area.floor != null ? 1 : 0),
            frozen_connectivity_proof_passed = frozenProof,
            movement_speed_scale = area.layout.parameters.movement_speed_scale,
            target_motion = area.layout.parameters.target_motion,
        };
    }

    private static bool HasFloorBelow(AreaRuntime area, PlayerBody player)
    {
        if (player == null || area.floor == null)
            return false;
        RaycastHit[] hits = Physics.RaycastAll(
            player.transform.position + Vector3.up * 0.25f,
            Vector3.down,
            2f,
            ~0,
            QueryTriggerInteraction.Ignore);
        foreach (RaycastHit hit in hits)
            if (hit.collider == area.floor)
                return true;
        return false;
    }

    private static bool HasObstacleClearance(AreaRuntime area, PlayerBody player)
    {
        if (player == null)
            return false;
        Vector3 position = player.transform.position;
        Collider[] overlaps = Physics.OverlapCapsule(
            position + Vector3.up * 0.51f,
            position + Vector3.up * 1.49f,
            0.48f,
            ~0,
            QueryTriggerInteraction.Ignore);
        foreach (Collider overlap in overlaps)
            if (overlap != null && area.obstacles.Contains(overlap))
                return false;
        return true;
    }

    private static bool HasLineOfSight(AreaRuntime area, PlayerBody playerA, PlayerBody playerB)
    {
        if (playerA == null || playerB == null)
            return false;
        Vector3 origin = playerA.transform.position + Vector3.up * 0.5f;
        Vector3 target = playerB.transform.position + Vector3.up * 0.5f;
        Vector3 delta = target - origin;
        RaycastHit[] hits = Physics.RaycastAll(origin, delta.normalized, delta.magnitude, ~0, QueryTriggerInteraction.Ignore);
        foreach (RaycastHit hit in hits)
            if (hit.collider != null && area.obstacles.Contains(hit.collider))
                return false;
        return true;
    }

    private static BoxCollider AddBox(Transform parent, string name, PrimitiveRecord record, PhysicMaterial material)
    {
        GameObject item = new GameObject("Layout_" + name);
        item.transform.SetParent(parent, false);
        item.transform.localPosition = ToVector3(record.center);
        BoxCollider collider = item.AddComponent<BoxCollider>();
        collider.size = ToVector3(record.size);
        collider.sharedMaterial = material;
        return collider;
    }

    private static bool TryResolveSeeds(int numAreas, out int[] seeds)
    {
        seeds = new int[numAreas];
        string listRaw = (Environment.GetEnvironmentVariable("PHASE5_LAYOUT_SEEDS") ?? "").Trim();
        if (!string.IsNullOrEmpty(listRaw))
        {
            string[] parts = listRaw.Split(',');
            if (parts.Length != numAreas)
                return Fail("seed_list_count_mismatch");
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seeds[i]))
                    return Fail("seed_list_invalid:" + parts[i]);
            return true;
        }

        string seedRaw = (Environment.GetEnvironmentVariable("PHASE5_LAYOUT_SEED") ?? "").Trim();
        if (!int.TryParse(seedRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int firstSeed))
            return Fail("layout_seed_missing_or_invalid:" + seedRaw);
        for (int i = 0; i < numAreas; i++)
            seeds[i] = firstSeed + i;
        return true;
    }

    private static LayoutRecord FindLayout(int seed)
    {
        if (manifest == null || manifest.layouts == null)
            return null;
        foreach (LayoutRecord layout in manifest.layouts)
            if (layout != null && layout.seed == seed)
                return layout;
        return null;
    }

    private static AreaRuntime FindArea(int areaId)
    {
        foreach (AreaRuntime area in areas)
            if (area.area_id == areaId)
                return area;
        return null;
    }

    private static bool ValidateRecord(LayoutRecord layout, string split, out string reason)
    {
        reason = "";
        if (layout == null) { reason = "seed_not_found"; return false; }
        if (layout.schema_version != LayoutSchema) { reason = "schema"; return false; }
        if (layout.split != split) { reason = "split"; return false; }
        if (string.IsNullOrEmpty(layout.family)) { reason = "family"; return false; }
        if (layout.floor == null || layout.floor.primitive != "box") { reason = "floor"; return false; }
        if (layout.obstacles == null || layout.obstacles.Length < 4) { reason = "obstacles"; return false; }
        foreach (PrimitiveRecord obstacle in layout.obstacles)
            if (obstacle == null || obstacle.primitive != "box" || !Finite3(obstacle.center) || !Positive3(obstacle.size))
            { reason = "primitive"; return false; }
        if (layout.spawn_a == null || layout.spawn_b == null
            || !Finite3(layout.spawn_a.position) || !Finite3(layout.spawn_b.position))
        { reason = "spawn"; return false; }
        if (layout.parameters == null || layout.parameters.movement_speed_scale < 0.85f
            || layout.parameters.movement_speed_scale > 1.15f)
        { reason = "parameters"; return false; }
        if (layout.validation == null || layout.validation.status != "PASS"
            || layout.validation.checks == null
            || !layout.validation.checks.spawn_a_valid_floor
            || !layout.validation.checks.spawn_b_valid_floor
            || !layout.validation.checks.collision_free_route_exists
            || !layout.validation.checks.no_unreachable_sealed_pockets
            || !layout.validation.checks.no_immediate_overlap
            || !layout.validation.checks.no_obstacle_overlap
            || !layout.validation.checks.requested_los_satisfied
            || !layout.validation.checks.primitive_colliders_only)
        { reason = "frozen_validation"; return false; }
        return true;
    }

    private static bool Finite3(float[] value)
    {
        return value != null && value.Length == 3
            && IsFinite(value[0]) && IsFinite(value[1]) && IsFinite(value[2]);
    }

    private static bool Positive3(float[] value)
    {
        return Finite3(value) && value[0] > 0f && value[1] > 0f && value[2] > 0f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static Vector3 ToVector3(float[] value)
    {
        return new Vector3(value[0], value[1], value[2]);
    }

    private static string Sha256File(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static bool Fail(string reason)
    {
        valid = false;
        failureReason = reason;
        Debug.LogError("[ProceduralArena] " + reason);
        if (enabled && Application.isBatchMode)
            Application.Quit(86);
        return false;
    }

    private sealed class AreaRuntime
    {
        public int area_id;
        public LayoutRecord layout;
        public GameObject root;
        public Vector3 origin;
        public BoxCollider floor;
        public readonly List<Collider> obstacles = new List<Collider>();
        public bool built;
    }

    [Serializable]
    public sealed class LayoutManifest
    {
        public string schema_version;
        public string split;
        public bool immutable;
        public bool actor_access_to_occupancy_or_navmesh;
        public LayoutRecord[] layouts;
    }

    [Serializable]
    public sealed class LayoutRecord
    {
        public string schema_version;
        public int seed;
        public string split;
        public string family;
        public PrimitiveRecord floor;
        public PrimitiveRecord[] obstacles;
        public SpawnRecord spawn_a;
        public SpawnRecord spawn_b;
        public bool requested_initial_los;
        public ParameterRecord parameters;
        public ValidationRecord validation;
        public string layout_sha256;
    }

    [Serializable]
    public sealed class PrimitiveRecord
    {
        public string id;
        public string primitive;
        public string kind;
        public float[] center;
        public float[] size;
    }

    [Serializable]
    public sealed class SpawnRecord
    {
        public float[] position;
        public float yaw_degrees;
    }

    [Serializable]
    public sealed class ParameterRecord
    {
        public string target_motion;
        public float floor_dynamic_friction;
        public float floor_static_friction;
        public float movement_speed_scale;
    }

    [Serializable]
    public sealed class ValidationRecord
    {
        public string status;
        public ValidationChecks checks;
        public float route_length_m;
    }

    [Serializable]
    public sealed class ValidationChecks
    {
        public bool spawn_a_valid_floor;
        public bool spawn_b_valid_floor;
        public bool collision_free_route_exists;
        public bool no_unreachable_sealed_pockets;
        public bool no_immediate_overlap;
        public bool no_obstacle_overlap;
        public bool requested_los_satisfied;
        public bool primitive_colliders_only;
    }

    [Serializable]
    public sealed class RuntimeAudit
    {
        public string schema_version;
        public string status;
        public string manifest_path;
        public string manifest_sha256;
        public string split;
        public int area_count;
        public bool actor_connectivity_access;
        public AreaAudit[] areas;
        public string failure_reason;
    }

    [Serializable]
    public sealed class AreaAudit
    {
        public int area_id;
        public int seed;
        public string family;
        public string status;
        public bool floor_below_a;
        public bool floor_below_b;
        public bool spawn_a_obstacle_clear;
        public bool spawn_b_obstacle_clear;
        public bool players_not_overlapping;
        public bool requested_initial_los;
        public bool actual_initial_los;
        public bool requested_los_satisfied;
        public bool collider_count_matches;
        public int collider_count;
        public bool frozen_connectivity_proof_passed;
        public float movement_speed_scale;
        public string target_motion;
    }
}

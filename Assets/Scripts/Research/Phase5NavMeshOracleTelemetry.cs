using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Optional Goal 8 runtime NavMesh. It supplies route queries only: it never
/// owns a movement component and never writes a player transform.
/// </summary>
public static class Phase5RuntimeNavMesh
{
    public const string OracleLabel = "NAVMESH_ORACLE";
    public const string ControlLabel = "NAVMESH_LAST_SEEN_CONTROL";
    public const string AuditSchema = "phase5_runtime_navmesh_audit_v001";

    private static readonly List<AreaBuild> Areas = new List<AreaBuild>();
    private static bool? enabledFromEnvironment;
    private static string failureReason = "";

    public static bool Enabled
    {
        get
        {
            if (!enabledFromEnvironment.HasValue)
                enabledFromEnvironment = Read("PHASE5_NAVMESH_UPPER_BOUND", "0") == "1";
            return enabledFromEnvironment.Value;
        }
    }

    public static string RouteMode => Read("PHASE5_NAVMESH_ROUTE_MODE", "oracle");

    public static bool ValidateLaunch()
    {
        string raw = Read("PHASE5_NAVMESH_UPPER_BOUND", "0");
        if (raw != "0" && raw != "1")
            return Fail("enable_flag_invalid:" + raw);
        if (raw == "0")
            return true;
        if (!Phase5HeadlessRuntime.Enabled)
            return Fail("requires_phase5_headless");
        if (Read("PHASE5_NAVIGATOR_EVAL", "0") != "1")
            return Fail("requires_goal7_dual_sensor_mode");
        if (Read("PHASE5_TELEMETRY_SCHEMA", "phase3v2_c_local45") != "phase3v2_c_local45")
            return Fail("requires_frozen_local45");
        if (Read("ENABLE_VISUAL_OBS", "0") == "1")
            return Fail("visual_observations_forbidden");
        string mode = RouteMode;
        if (mode != "oracle" && mode != "last_seen_control")
            return Fail("route_mode_invalid:" + mode);
        string label = Read("PHASE5_NAVMESH_ORACLE_LABEL", "");
        string oracleEnabled = Read("PHASE5_ENABLE_NAVMESH_ORACLE", "0");
        if (mode == "oracle"
            && (label != OracleLabel || oracleEnabled != "1"))
            return Fail("oracle_requires_explicit_label_and_enable");
        if (mode == "last_seen_control"
            && (label != ControlLabel || oracleEnabled != "0"))
            return Fail("last_seen_control_requires_control_label_and_oracle_disabled");
        float hz = ReadFloat("PHASE5_NAVMESH_REPLAN_HZ", 3f);
        if (hz < 1f || hz > 4f)
            return Fail("replan_hz_outside_1_to_4:" + hz.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    public static bool BuildArea(int areaId, PlayerBody playerA, PlayerBody playerB)
    {
        if (!Enabled)
            return true;
        if (playerA == null || playerB == null)
            return Fail("players_missing:area=" + areaId);

        List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
        Bounds bounds;
        string sourceKind;
        if (Phase5ProceduralArenaRuntime.Enabled)
        {
            if (!Phase5ProceduralArenaRuntime.TryGetNavMeshBuildGeometry(
                areaId,
                out BoxCollider floor,
                out Collider[] obstacles,
                out bounds))
                return Fail("procedural_geometry_missing:area=" + areaId);
            sources.Add(ToBoxSource(floor, 0));
            int notWalkable = Mathf.Max(1, NavMesh.GetAreaFromName("Not Walkable"));
            foreach (Collider obstacle in obstacles)
            {
                BoxCollider box = obstacle as BoxCollider;
                if (box != null)
                    sources.Add(ToBoxSource(box, notWalkable));
            }
            sourceKind = "procedural_runtime_generated";
        }
        else
        {
            bounds = DemoMapRuntime.IsValid
                ? DemoMapRuntime.ActiveBounds
                : new Bounds(
                    (playerA.transform.position + playerB.transform.position) * 0.5f,
                    new Vector3(300f, 120f, 300f));
            if (bounds.size.sqrMagnitude < 1f)
                bounds = new Bounds(
                    (playerA.transform.position + playerB.transform.position) * 0.5f,
                    new Vector3(300f, 120f, 300f));
            bounds.Expand(new Vector3(8f, 8f, 8f));
            List<NavMeshBuildMarkup> markups = new List<NavMeshBuildMarkup>();
            List<NavMeshBuildSource> collected = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(
                bounds,
                ~0,
                NavMeshCollectGeometry.PhysicsColliders,
                0,
                markups,
                collected);
            HashSet<Collider> seen = new HashSet<Collider>();
            foreach (NavMeshBuildSource source in collected)
            {
                Collider collider = source.component as Collider;
                if (collider == null
                    || !collider.enabled
                    || collider.isTrigger
                    || collider.GetComponentInParent<PlayerBody>() != null
                    || !seen.Add(collider))
                    continue;
                Bounds colliderBounds = collider.bounds;
                if (colliderBounds.size.sqrMagnitude < 1e-6f)
                    continue;
                sources.Add(ToBoundsBoxSource(colliderBounds, 0));
            }
            sourceKind = "authored_runtime_generated_collider_bounds";
        }
        bounds.Expand(new Vector3(4f, 8f, 4f));
        if (sources.Count == 0)
            return Fail("navmesh_sources_empty:area=" + areaId);

        NavMeshBuildSettings settings = NavMesh.GetSettingsCount() > 0
            ? NavMesh.GetSettingsByIndex(0)
            : NavMesh.CreateSettings();
        settings.agentRadius = 0.50f;
        settings.agentHeight = 1.80f;
        settings.agentClimb = 0.45f;
        settings.agentSlope = 45f;
        settings.minRegionArea = 1f;
        settings.overrideVoxelSize = true;
        settings.voxelSize = 0.15f;
        settings.overrideTileSize = true;
        settings.tileSize = 64;

        NavMeshData data = NavMeshBuilder.BuildNavMeshData(
            settings,
            sources,
            bounds,
            Vector3.zero,
            Quaternion.identity);
        if (data == null)
            return Fail("runtime_navmesh_build_failed:area=" + areaId);
        NavMeshDataInstance instance = NavMesh.AddNavMeshData(data);
        if (!instance.valid)
            return Fail("runtime_navmesh_instance_invalid:area=" + areaId);

        bool sampledA = NavMesh.SamplePosition(
            playerA.transform.position,
            out NavMeshHit hitA,
            4f,
            NavMesh.AllAreas);
        bool sampledB = NavMesh.SamplePosition(
            playerB.transform.position,
            out NavMeshHit hitB,
            4f,
            NavMesh.AllAreas);
        NavMeshPath path = new NavMeshPath();
        bool initialPath = sampledA && sampledB
            && NavMesh.CalculatePath(hitA.position, hitB.position, NavMesh.AllAreas, path)
            && path.status != NavMeshPathStatus.PathInvalid
            && path.corners != null
            && path.corners.Length >= 2;
        Debug.Log("[Phase5NavMesh] initial audit area=" + areaId
            + " sampled_a=" + sampledA
            + " sampled_b=" + sampledB
            + " path_status=" + path.status
            + " corners=" + (path.corners != null ? path.corners.Length : 0)
            + " sources=" + sources.Count
            + " bounds_size=" + bounds.size);
        Areas.Add(new AreaBuild
        {
            area_id = areaId,
            data = data,
            instance = instance,
            source_kind = sourceKind,
            source_count = sources.Count,
            bounds = bounds,
            spawn_a_sampled = sampledA,
            spawn_b_sampled = sampledB,
            initial_path = initialPath,
            initial_corner_count = path.corners != null ? path.corners.Length : 0,
        });
        if (!initialPath)
            return Fail("initial_navmesh_path_invalid:area=" + areaId);
        Debug.Log("[Phase5NavMesh] built area=" + areaId
            + " source=" + sourceKind
            + " sources=" + sources.Count
            + " corners=" + path.corners.Length);
        return true;
    }

    public static bool WriteAudit()
    {
        if (!Enabled)
            return true;
        bool passed = string.IsNullOrEmpty(failureReason)
            && Areas.Count > 0
            && Areas.TrueForAll(area => area.instance.valid
                && area.spawn_a_sampled
                && area.spawn_b_sampled
                && area.initial_path);
        RuntimeAudit audit = new RuntimeAudit
        {
            schema_version = AuditSchema,
            status = passed ? "PASS" : "FAIL",
            oracle_label = RouteMode == "oracle" ? OracleLabel : ControlLabel,
            route_mode = RouteMode,
            replan_hz = ReadFloat("PHASE5_NAVMESH_REPLAN_HZ", 3f),
            area_count = Areas.Count,
            navmesh_agent_count = 0,
            transform_drive_calls = 0,
            exposes_map_identity = false,
            exposes_absolute_coordinates = false,
            areas = Areas.ConvertAll(area => new AreaAudit
            {
                area_id = area.area_id,
                source_kind = area.source_kind,
                source_count = area.source_count,
                spawn_a_sampled = area.spawn_a_sampled,
                spawn_b_sampled = area.spawn_b_sampled,
                initial_path = area.initial_path,
                initial_corner_count = area.initial_corner_count,
                status = area.instance.valid && area.initial_path ? "PASS" : "FAIL",
            }).ToArray(),
            failure_reason = failureReason,
        };
        string path = Read("PHASE5_NAVMESH_AUDIT_PATH", "");
        if (string.IsNullOrWhiteSpace(path))
            return Fail("navmesh_audit_path_missing");
        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonUtility.ToJson(audit, true) + "\n");
        }
        catch (Exception ex)
        {
            return Fail("navmesh_audit_write_failed:" + ex.Message);
        }
        return passed;
    }

    private static NavMeshBuildSource ToBoxSource(BoxCollider collider, int area)
    {
        return new NavMeshBuildSource
        {
            shape = NavMeshBuildSourceShape.Box,
            transform = collider.transform.localToWorldMatrix
                * Matrix4x4.Translate(collider.center),
            size = collider.size,
            area = area,
        };
    }

    private static NavMeshBuildSource ToBoundsBoxSource(Bounds bounds, int area)
    {
        return new NavMeshBuildSource
        {
            shape = NavMeshBuildSourceShape.Box,
            transform = Matrix4x4.TRS(bounds.center, Quaternion.identity, Vector3.one),
            size = bounds.size,
            area = area,
        };
    }

    private static string Read(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static float ReadFloat(string name, float fallback)
    {
        string raw = Read(name, "");
        return float.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float value) ? value : fallback;
    }

    private static bool Fail(string reason)
    {
        failureReason = reason;
        Debug.LogError("[Phase5NavMesh] " + reason);
        if (Enabled && Application.isBatchMode)
            Application.Quit(87);
        return false;
    }

    private sealed class AreaBuild
    {
        public int area_id;
        public NavMeshData data;
        public NavMeshDataInstance instance;
        public string source_kind;
        public int source_count;
        public Bounds bounds;
        public bool spawn_a_sampled;
        public bool spawn_b_sampled;
        public bool initial_path;
        public int initial_corner_count;
    }

    [Serializable]
    private sealed class RuntimeAudit
    {
        public string schema_version;
        public string status;
        public string oracle_label;
        public string route_mode;
        public float replan_hz;
        public int area_count;
        public int navmesh_agent_count;
        public int transform_drive_calls;
        public bool exposes_map_identity;
        public bool exposes_absolute_coordinates;
        public AreaAudit[] areas;
        public string failure_reason;
    }

    [Serializable]
    private sealed class AreaAudit
    {
        public int area_id;
        public string source_kind;
        public int source_count;
        public bool spawn_a_sampled;
        public bool spawn_b_sampled;
        public bool initial_path;
        public int initial_corner_count;
        public string status;
    }
}

public static class Phase5NavMeshRouteLayout
{
    public const string SchemaId = "phase5_navmesh_route_v001";
    public const int ObservationSize = 32;
}

public sealed class Phase5NavMeshRouteSensorComponent : SensorComponent
{
    public int AreaId { get; set; }

    public override ISensor[] CreateSensors()
    {
        PlayerBody body = GetComponent<PlayerBody>();
        if (body == null)
            throw new InvalidOperationException("[Phase5NavMesh] PlayerBody missing");
        Phase5NavMeshRouteTelemetry telemetry =
            GetComponent<Phase5NavMeshRouteTelemetry>();
        if (telemetry == null)
            telemetry = gameObject.AddComponent<Phase5NavMeshRouteTelemetry>();
        telemetry.Initialize(body, AreaId);
        return new ISensor[] { new Phase5NavMeshRouteSensor(telemetry) };
    }
}

public sealed class Phase5NavMeshRouteSensor : ISensor
{
    private readonly Phase5NavMeshRouteTelemetry telemetry;
    private readonly float[] cache =
        new float[Phase5NavMeshRouteLayout.ObservationSize];
    private bool hasCache;

    public Phase5NavMeshRouteSensor(Phase5NavMeshRouteTelemetry telemetry)
    {
        this.telemetry = telemetry;
    }

    public ObservationSpec GetObservationSpec() =>
        ObservationSpec.Vector(Phase5NavMeshRouteLayout.ObservationSize);

    public int Write(ObservationWriter writer)
    {
        if (!hasCache)
        {
            telemetry.BuildObservation(cache, false);
            hasCache = true;
        }
        for (int i = 0; i < cache.Length; i++)
            writer[i] = cache[i];
        return cache.Length;
    }

    public byte[] GetCompressedObservation() => null;

    public void Update()
    {
        telemetry.BuildObservation(cache, true);
        hasCache = true;
    }

    public void Reset()
    {
        telemetry.ResetRoute();
        hasCache = false;
    }

    public CompressionSpec GetCompressionSpec() => CompressionSpec.Default();
    public string GetName() => Phase5NavMeshRouteLayout.SchemaId;
}

/// <summary>
/// Generic route metadata only. All vectors are actor-yaw egocentric and all
/// distances are bounded scalars. World-space corners remain private.
/// </summary>
public sealed class Phase5NavMeshRouteTelemetry : MonoBehaviour
{
    private const int MaxCorners = 3;
    private readonly List<Vector3> corners = new List<Vector3>();
    private readonly float[] actorCache =
        new float[Phase5ActorObservationLayout.ObservationSize];

    private PlayerBody self;
    private PlayerBody opponent;
    private Phase5MapIndependentTelemetry actorTelemetry;
    private int areaId;
    private bool initialized;
    private bool lastSeenValid;
    private Vector3 lastSeenWorld;
    private float lastReplanTime = -999f;
    private float priorRemaining = -1f;
    private float noProgressAge;
    private float routeDelta;
    private NavMeshPathStatus pathStatus = NavMeshPathStatus.PathInvalid;
    private Vector3 destinationWorld;
    private bool destinationValid;
    private bool interceptValid;
    private Vector3 interceptDirectionLocal;
    private float interceptDistance;

    public void Initialize(PlayerBody body, int resolvedAreaId)
    {
        if (initialized)
            return;
        self = body;
        areaId = resolvedAreaId;
        MatchManager match = body != null ? body.MatchManager : null;
        opponent = match != null && body.Identity != null
            ? match.GetOpponentBody(body.Identity) : null;
        actorTelemetry = GetComponent<Phase5MapIndependentTelemetry>();
        if (actorTelemetry == null)
            actorTelemetry = gameObject.AddComponent<Phase5MapIndependentTelemetry>();
        actorTelemetry.Initialize(body);
        if (match != null)
        {
            match.OnRoundReset += ResetRoute;
            match.OnMatchReset += ResetRoute;
        }
        initialized = true;
        ResetRoute();
        Debug.Log("[Phase5NavMesh] sensor initialized area=" + areaId
            + " schema=" + Phase5NavMeshRouteLayout.SchemaId
            + " mode=" + Phase5RuntimeNavMesh.RouteMode);
    }

    private void OnDestroy()
    {
        if (self != null && self.MatchManager != null)
        {
            self.MatchManager.OnRoundReset -= ResetRoute;
            self.MatchManager.OnMatchReset -= ResetRoute;
        }
    }

    public void ResetRoute()
    {
        corners.Clear();
        lastSeenValid = false;
        lastReplanTime = -999f;
        priorRemaining = -1f;
        noProgressAge = 0f;
        routeDelta = 0f;
        pathStatus = NavMeshPathStatus.PathInvalid;
        destinationValid = false;
        interceptValid = false;
    }

    public void BuildObservation(float[] output, bool advanceState)
    {
        if (!initialized)
            Initialize(GetComponent<PlayerBody>(), areaId);
        if (output == null || output.Length != Phase5NavMeshRouteLayout.ObservationSize)
            throw new ArgumentException("phase5_navmesh_route_v001 requires 32 values");
        Array.Clear(output, 0, output.Length);
        if (self == null || opponent == null)
            return;

        bool visible = ExactLineOfSight();
        if (visible && advanceState)
        {
            lastSeenValid = true;
            lastSeenWorld = opponent.transform.position;
        }
        float hz = Mathf.Clamp(ReadFloat("PHASE5_NAVMESH_REPLAN_HZ", 3f), 1f, 4f);
        if (advanceState && Time.time - lastReplanTime >= 1f / hz - 1e-5f)
            Replan(visible);

        output[0] = pathStatus == NavMeshPathStatus.PathComplete ? 1f : 0f;
        output[1] = pathStatus == NavMeshPathStatus.PathPartial ? 1f : 0f;
        output[2] = pathStatus == NavMeshPathStatus.PathInvalid ? 1f : 0f;
        Quaternion inverseYaw = Quaternion.Inverse(
            Quaternion.Euler(0f, self.transform.eulerAngles.y, 0f));
        int first = FirstFutureCorner();
        for (int slot = 0; slot < MaxCorners; slot++)
        {
            int at = 3 + slot * 4;
            int index = first + slot;
            if (index >= corners.Count)
                continue;
            Vector3 local = inverseYaw * (corners[index] - self.transform.position);
            local.y = 0f;
            float distance = local.magnitude;
            Vector3 direction = distance > 1e-5f ? local / distance : Vector3.forward;
            output[at] = 1f;
            output[at + 1] = Mathf.Clamp(direction.x, -1f, 1f);
            output[at + 2] = Mathf.Clamp(direction.z, -1f, 1f);
            output[at + 3] = LogDistance(distance);
        }

        float remaining = RemainingLength(first);
        float direct = destinationValid
            ? PlanarDistance(self.transform.position, destinationWorld) : 0f;
        output[15] = Mathf.Clamp01(remaining / 200f);
        output[16] = Mathf.Clamp01(Mathf.Max(0, corners.Count - first) / 32f);
        output[17] = Curvature(first);
        output[18] = direct > 0.01f
            ? Mathf.Clamp01((remaining / direct) / 4f) : 0f;
        output[19] = Mathf.Clamp01(OffPathDistance() / 10f);
        output[20] = Mathf.Clamp01(noProgressAge / 10f);
        if (interceptValid)
        {
            output[21] = 1f;
            output[22] = Mathf.Clamp(interceptDirectionLocal.x, -1f, 1f);
            output[23] = Mathf.Clamp(interceptDirectionLocal.z, -1f, 1f);
            output[24] = LogDistance(interceptDistance);
        }
        output[25] = Mathf.Clamp01((Time.time - lastReplanTime) / 1f);
        output[26] = Mathf.Clamp(routeDelta, -1f, 1f);
        output[27] = visible ? 1f : 0f;
        output[28] = Phase5RuntimeNavMesh.RouteMode == "oracle" ? 1f : 0f;
        output[29] = Phase5RuntimeNavMesh.RouteMode == "last_seen_control" ? 1f : 0f;
        output[30] = Read("PHASE5_NAVMESH_PREDICTED_INTERCEPT", "0") == "1" ? 1f : 0f;
        output[31] = 0f;

        if (advanceState)
        {
            if (priorRemaining >= 0f)
            {
                float improvement = priorRemaining - remaining;
                routeDelta = Mathf.Clamp(improvement / 2f, -1f, 1f);
                noProgressAge = improvement > 0.02f
                    ? 0f : noProgressAge + Time.fixedDeltaTime;
            }
            priorRemaining = remaining;
        }
        AssertFinite(output);
    }

    private void Replan(bool visible)
    {
        lastReplanTime = Time.time;
        destinationValid = ResolveDestination(visible, out destinationWorld);
        corners.Clear();
        pathStatus = NavMeshPathStatus.PathInvalid;
        if (!destinationValid)
            return;
        if (!NavMesh.SamplePosition(self.transform.position, out NavMeshHit start, 4f, NavMesh.AllAreas)
            || !NavMesh.SamplePosition(destinationWorld, out NavMeshHit goal, 6f, NavMesh.AllAreas))
            return;
        NavMeshPath path = new NavMeshPath();
        if (!NavMesh.CalculatePath(start.position, goal.position, NavMesh.AllAreas, path)
            || path.corners == null
            || path.corners.Length == 0)
            return;
        pathStatus = path.status;
        corners.AddRange(path.corners);
        BuildIntercept(start.position);
    }

    private bool ResolveDestination(bool visible, out Vector3 destination)
    {
        destination = Vector3.zero;
        if (Phase5RuntimeNavMesh.RouteMode == "oracle")
        {
            destination = opponent.transform.position;
            return true;
        }
        if (visible)
        {
            destination = opponent.transform.position;
            lastSeenWorld = destination;
            lastSeenValid = true;
            return true;
        }
        if (lastSeenValid)
        {
            destination = lastSeenWorld;
            return true;
        }
        actorTelemetry.BuildObservation(actorCache, false);
        if (actorCache[224] <= 0.5f)
            return false;
        int bearingBin = Highest(actorCache, 211, 8);
        int distanceBin = Highest(actorCache, 219, 5);
        float angle = (bearingBin + 0.5f) * 45f - 180f;
        float[] distances = { 2f, 6f, 12f, 24f, 40f };
        Vector3 local = Quaternion.Euler(0f, angle, 0f)
            * Vector3.forward * distances[Mathf.Clamp(distanceBin, 0, 4)];
        destination = self.transform.position
            + Quaternion.Euler(0f, self.transform.eulerAngles.y, 0f) * local;
        return true;
    }

    private void BuildIntercept(Vector3 start)
    {
        interceptValid = false;
        if (Read("PHASE5_NAVMESH_PREDICTED_INTERCEPT", "0") != "1"
            || Phase5RuntimeNavMesh.RouteMode != "oracle")
            return;
        Vector3 velocity = opponent.Motor != null
            ? opponent.Motor.WorldVelocity : Vector3.zero;
        float horizon = Mathf.Clamp(
            Vector3.Distance(self.transform.position, opponent.transform.position) / 12f,
            0f,
            1.5f);
        Vector3 predicted = opponent.transform.position + velocity * horizon;
        if (!NavMesh.SamplePosition(predicted, out NavMeshHit goal, 6f, NavMesh.AllAreas))
            return;
        NavMeshPath intercept = new NavMeshPath();
        if (!NavMesh.CalculatePath(start, goal.position, NavMesh.AllAreas, intercept)
            || intercept.status == NavMeshPathStatus.PathInvalid
            || intercept.corners == null
            || intercept.corners.Length < 2)
            return;
        Vector3 local = Quaternion.Inverse(
            Quaternion.Euler(0f, self.transform.eulerAngles.y, 0f))
            * (intercept.corners[1] - self.transform.position);
        local.y = 0f;
        interceptDistance = local.magnitude;
        interceptDirectionLocal = interceptDistance > 1e-5f
            ? local / interceptDistance : Vector3.forward;
        interceptValid = true;
    }

    private int FirstFutureCorner()
    {
        int index = 0;
        while (index < corners.Count
            && PlanarDistance(self.transform.position, corners[index]) < 0.8f)
            index++;
        return index;
    }

    private float RemainingLength(int first)
    {
        if (first >= corners.Count)
            return 0f;
        float length = PlanarDistance(self.transform.position, corners[first]);
        for (int i = first + 1; i < corners.Count; i++)
            length += PlanarDistance(corners[i - 1], corners[i]);
        return length;
    }

    private float Curvature(int first)
    {
        float turns = 0f;
        int count = 0;
        Vector3 prior = self.transform.position;
        for (int i = first; i + 1 < corners.Count; i++)
        {
            Vector3 a = corners[i] - prior;
            Vector3 b = corners[i + 1] - corners[i];
            a.y = 0f; b.y = 0f;
            if (a.sqrMagnitude > 1e-5f && b.sqrMagnitude > 1e-5f)
            {
                turns += Mathf.Abs(Vector3.SignedAngle(a, b, Vector3.up));
                count++;
            }
            prior = corners[i];
        }
        return count > 0 ? Mathf.Clamp01((turns / count) / 180f) : 0f;
    }

    private float OffPathDistance()
    {
        if (corners.Count < 2)
            return 0f;
        float best = float.PositiveInfinity;
        for (int i = 1; i < corners.Count; i++)
        {
            Vector3 a = corners[i - 1];
            Vector3 b = corners[i];
            a.y = self.transform.position.y;
            b.y = self.transform.position.y;
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f
                ? Mathf.Clamp01(Vector3.Dot(self.transform.position - a, ab) / ab.sqrMagnitude)
                : 0f;
            best = Mathf.Min(best, Vector3.Distance(self.transform.position, a + ab * t));
        }
        return float.IsInfinity(best) ? 0f : best;
    }

    private static float PlanarDistance(Vector3 left, Vector3 right)
    {
        left.y = 0f;
        right.y = 0f;
        return Vector3.Distance(left, right);
    }

    private bool ExactLineOfSight()
    {
        Vector3 origin = self.transform.position + Vector3.up * 0.7f;
        Vector3 target = opponent.transform.position + Vector3.up * 0.7f;
        Vector3 delta = target - origin;
        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            delta.normalized,
            delta.magnitude + 0.05f,
            ~0,
            QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (RaycastHit hit in hits)
        {
            PlayerBody body = hit.collider != null
                ? hit.collider.GetComponentInParent<PlayerBody>() : null;
            if (body == self)
                continue;
            return body == opponent;
        }
        return false;
    }

    private static int Highest(float[] values, int at, int count)
    {
        int best = 0;
        for (int i = 1; i < count; i++)
            if (values[at + i] > values[at + best])
                best = i;
        return best;
    }

    private static float LogDistance(float distance) =>
        Mathf.Clamp01(Mathf.Log(1f + Mathf.Max(0f, distance)) / Mathf.Log(201f));

    private static string Read(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static float ReadFloat(string name, float fallback)
    {
        string raw = Read(name, "");
        return float.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float value) ? value : fallback;
    }

    private static void AssertFinite(float[] values)
    {
        for (int i = 0; i < values.Length; i++)
            if (float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                throw new InvalidOperationException(
                    Phase5NavMeshRouteLayout.SchemaId
                    + " non-finite value at " + i);
    }
}

using UnityEngine;

/// <summary>
/// Phase 4.4 trial monitor. It is attached only for PHASE4_4_ENABLE_SPAWN_BUCKETS=1
/// and records contact, engagement, and timeout termination labels.
/// </summary>
public class Phase44TrialTimeoutController : MonoBehaviour
{
    private MatchManager matchManager;
    private PlayerBody playerA;
    private PlayerBody playerB;
    private Phase44SpawnBucketController.SpawnPlan originalPlan;
    private Phase44SpawnBucketController.SpawnPlan currentPlan;
    private bool initialized;
    private bool active;
    private bool finished;
    private float trialStartTime;
    private int trialIndex;
    private float timeToFirstLos = -1f;
    private float timeToFirstShot = -1f;
    private float timeToFirstHit = -1f;
    private float timeToFirstDamage = -1f;
    private int contactLostCount;
    private int reacquireCount;
    private float stuckSeconds;
    private bool pathingFailure;
    private bool lastLos;
    private Vector3 lastPlayerAPosition;
    private Vector3 lastPlayerBPosition;
    private float lastMotionSampleTime;

    public void Initialize(
        MatchManager mm,
        PlayerBody pA,
        PlayerBody pB,
        Phase44SpawnBucketController.SpawnPlan spawnPlan,
        int areaId)
    {
        if (!spawnPlan.Enabled)
        {
            enabled = false;
            return;
        }

        matchManager = mm;
        playerA = pA;
        playerB = pB;
        originalPlan = spawnPlan;
        originalPlan.AreaId = areaId;
        currentPlan = originalPlan;
        initialized = true;

        Subscribe();
        BeginTrial("initialize");
    }

    private void Update()
    {
        if (!initialized || !active || finished)
            return;

        float elapsed = Time.time - trialStartTime;
        bool los = Phase44SpawnBucketController.HasLineOfSightBetween(playerA, playerB);
        if (los && timeToFirstLos < 0f)
            timeToFirstLos = elapsed;
        if (timeToFirstLos >= 0f && lastLos && !los)
            contactLostCount++;
        if (timeToFirstLos >= 0f && !lastLos && los && contactLostCount > 0)
            reacquireCount++;
        lastLos = los;

        UpdateStuckTracking(elapsed, los);

        if (DemoMapRuntime.ControlEnabled)
        {
            Vector3 posA = playerA != null ? playerA.transform.position : currentPlan.PosA;
            Vector3 posB = playerB != null ? playerB.transform.position : currentPlan.PosB;
            currentPlan.FallDetected = DemoMapRuntime.IsFallDetected(posA) || DemoMapRuntime.IsFallDetected(posB);
            currentPlan.OutOfBounds = DemoMapRuntime.IsOutOfBounds(posA) || DemoMapRuntime.IsOutOfBounds(posB);
            if (currentPlan.FallDetected)
            {
                EndTrial("fall_out_of_map", false);
                return;
            }
            if (currentPlan.OutOfBounds)
            {
                EndTrial("out_of_map_bounds", false);
                return;
            }
        }

        if (currentPlan.TrialMaxSeconds > 0f && elapsed >= currentPlan.TrialMaxSeconds)
        {
            EndTrial(ClassifyTimeout(), true);
        }
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }

    private void Subscribe()
    {
        if (matchManager == null)
            return;
        matchManager.OnMiss += OnMiss;
        matchManager.OnHit += OnHit;
        matchManager.OnKill += OnKill;
        matchManager.OnRoundReset += OnRoundReset;
        matchManager.OnMatchReset += OnRoundReset;
    }

    private void Unsubscribe()
    {
        if (matchManager == null)
            return;
        matchManager.OnMiss -= OnMiss;
        matchManager.OnHit -= OnHit;
        matchManager.OnKill -= OnKill;
        matchManager.OnRoundReset -= OnRoundReset;
        matchManager.OnMatchReset -= OnRoundReset;
    }

    private void BeginTrial(string reason)
    {
        if (!initialized)
            return;

        trialIndex++;
        trialStartTime = Time.time;
        finished = false;
        active = true;
        timeToFirstLos = -1f;
        timeToFirstShot = -1f;
        timeToFirstHit = -1f;
        timeToFirstDamage = -1f;
        contactLostCount = 0;
        reacquireCount = 0;
        stuckSeconds = 0f;
        pathingFailure = false;
        lastPlayerAPosition = playerA != null ? playerA.transform.position : Vector3.zero;
        lastPlayerBPosition = playerB != null ? playerB.transform.position : Vector3.zero;
        lastMotionSampleTime = Time.time;

        currentPlan = originalPlan;
        RefreshCurrentGeometry();
        lastLos = currentPlan.InitialLineOfSight;
        if (lastLos)
            timeToFirstLos = 0f;

        Phase44SpawnTelemetry.WriteSpawn(
            currentPlan,
            trialIndex,
            0f,
            timeToFirstLos,
            timeToFirstShot,
            timeToFirstHit,
            timeToFirstDamage,
            contactLostCount,
            reacquireCount,
            stuckSeconds,
            pathingFailure);

        if (!currentPlan.SpawnConstraintSatisfied)
        {
            EndTrial("spawn_constraint_failed", false);
            Debug.LogWarning($"[Phase44Trial] area={currentPlan.AreaId} trial={trialIndex} spawn constraint failed: {currentPlan.SpawnConstraintFailureReason}");
        }
        else
        {
            Debug.Log($"[Phase44Trial] area={currentPlan.AreaId} trial={trialIndex} started reason={reason} max={currentPlan.TrialMaxSeconds:F1}s");
        }
    }

    private void RefreshCurrentGeometry()
    {
        if (playerA == null || playerB == null)
            return;
        currentPlan.SpawnDistance = Vector3.Distance(playerA.transform.position, playerB.transform.position);
        currentPlan.InitialLineOfSight = Phase44SpawnBucketController.HasLineOfSightBetween(playerA, playerB);
        currentPlan.ObstacleBetweenPlayers = !currentPlan.InitialLineOfSight;
    }

    private void OnRoundReset()
    {
        if (!initialized)
            return;
        BeginTrial("round_reset");
    }

    private void OnMiss(PlayerIdentity shooter)
    {
        if (!active || finished)
            return;
        MarkFirstShot();
    }

    private void OnHit(PlayerIdentity shooter, PlayerIdentity victim)
    {
        if (!active || finished)
            return;
        MarkFirstShot();
        float elapsed = Time.time - trialStartTime;
        if (timeToFirstHit < 0f)
            timeToFirstHit = elapsed;
        if (timeToFirstDamage < 0f)
            timeToFirstDamage = elapsed;
    }

    private void OnKill(PlayerIdentity killer, PlayerIdentity victim)
    {
        if (!active || finished)
            return;
        EndTrial("combat_resolved", false);
    }

    private void MarkFirstShot()
    {
        if (timeToFirstShot < 0f)
            timeToFirstShot = Time.time - trialStartTime;
    }

    private void UpdateStuckTracking(float elapsed, bool los)
    {
        float dt = Time.time - lastMotionSampleTime;
        if (dt < 1f)
            return;

        Vector3 posA = playerA != null ? playerA.transform.position : lastPlayerAPosition;
        Vector3 posB = playerB != null ? playerB.transform.position : lastPlayerBPosition;
        float motion = Vector3.Distance(posA, lastPlayerAPosition) + Vector3.Distance(posB, lastPlayerBPosition);
        if (!los && timeToFirstShot < 0f && motion < 0.10f)
            stuckSeconds += dt;
        else
            stuckSeconds = Mathf.Max(0f, stuckSeconds - dt * 0.5f);

        float failureThreshold = Mathf.Min(15f, Mathf.Max(5f, currentPlan.TrialMaxSeconds * 0.5f));
        if (stuckSeconds >= failureThreshold && elapsed >= failureThreshold)
            pathingFailure = true;

        lastPlayerAPosition = posA;
        lastPlayerBPosition = posB;
        lastMotionSampleTime = Time.time;
    }

    private string ClassifyTimeout()
    {
        if (pathingFailure)
            return "pathing_failure_timeout";
        if (timeToFirstLos < 0f)
            return "no_los_timeout";
        if (timeToFirstShot < 0f)
            return "los_no_engagement_timeout";
        if (timeToFirstHit < 0f)
            return "engaged_no_hit_timeout";
        return "partial_combat_timeout";
    }

    private void EndTrial(string reason, bool resetRound)
    {
        if (finished)
            return;
        float elapsed = Mathf.Max(0f, Time.time - trialStartTime);
        Phase44SpawnTelemetry.WriteTermination(
            currentPlan,
            trialIndex,
            elapsed,
            reason,
            timeToFirstLos,
            timeToFirstShot,
            timeToFirstHit,
            timeToFirstDamage,
            contactLostCount,
            reacquireCount,
            stuckSeconds,
            pathingFailure);

        finished = true;
        active = false;
        Debug.Log($"[Phase44Trial] area={currentPlan.AreaId} trial={trialIndex} termination={reason} elapsed={elapsed:F2}s");

        if (resetRound)
        {
            Phase45LiveTelemetryBridge bridge = UnityEngine.Object.FindObjectOfType<Phase45LiveTelemetryBridge>();
            if (bridge != null && bridge.CompleteTrialFromGuardedTimeout(reason))
                return;
        }

        if (resetRound && matchManager != null)
        {
            matchManager.ResetRound();
        }
    }
}

using System;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Live human telemetry bridge.
///
/// Enabled only when PHASE4_5_LIVE_BRIDGE=1. It consumes the Python-authored
/// next_trial_config.json path from PHASE4_5_NEXT_TRIAL_CONFIG, records raw
/// live Unity telemetry to live_trial_telemetry.jsonl, and writes trial_done.json.
/// Python remains responsible for building pilot_feature_rows.csv.
/// </summary>
[UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, null, "Phase45LiveTelemetryBridge")]
public class LiveTelemetryBridge : MonoBehaviour
{
    private MatchManager matchManager;
    private PlayerBody player;
    private PlayerBody opponent;
    private string unityMode = "";
    private string playerBBotMode = "";
    private NextTrialConfig config;
    private string configPath = "";
    private string telemetryPath = "";
    private string trialDonePath = "";
    private bool active;
    private bool finished;
    private float startTime;
    private int tickIndex;
    private float maxSeconds = 0f;
    private float minCompleteSeconds = 0f;
    private Vector3 lastPlayerPos;
    private Vector3 lastOpponentPos;
    private float lastSampleTime;
    private float lastPlayerHp;
    private float lastOpponentHp;
    private bool realisticSpawnsEnabled;
    private bool engagementObserved;
    private string spawnBucket = "";
    private float spawnDistance = -1f;
    private int initialLineOfSight = -1;
    private int obstacleBetweenPlayers = -1;
    private float timeToFirstLos = -1f;
    private float timeToFirstShot = -1f;
    private float timeToFirstHit = -1f;
    private bool hasBeliefPrior;
    private float beliefMu;
    private float beliefSigma;
    private bool continuousChallengers;
    private float opponentRatingMu = 1000f;
    private float opponentRatingSigma = 250f;
    private int liveScoreEventCount;

    public bool HasBeliefPrior { get { return hasBeliefPrior; } }
    public float BeliefMu { get { return beliefMu; } }
    public float BeliefSigma { get { return beliefSigma; } }

    public void Initialize(MatchManager mm, PlayerBody playerA, PlayerBody playerB, string currentUnityMode, string currentPlayerBBotMode)
    {
        matchManager = mm;
        player = playerA;
        opponent = playerB;
        unityMode = currentUnityMode ?? "";
        playerBBotMode = currentPlayerBBotMode ?? "";
    }

    private void Start()
    {
        if (Environment.GetEnvironmentVariable("PHASE4_5_LIVE_BRIDGE") != "1")
        {
            enabled = false;
            return;
        }

        configPath = Environment.GetEnvironmentVariable("PHASE4_5_NEXT_TRIAL_CONFIG") ?? "";
        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
        {
            Debug.LogError($"[LiveTelemetry] Missing next trial config: {configPath}");
            enabled = false;
            return;
        }

        string maxStr = Environment.GetEnvironmentVariable("PHASE4_5_TRIAL_MAX_SECONDS") ?? "0";
        float.TryParse(maxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out maxSeconds);
        string minCompleteStr = Environment.GetEnvironmentVariable("PHASE4_5_MIN_COMPLETE_SECONDS") ?? "0";
        float.TryParse(minCompleteStr, NumberStyles.Float, CultureInfo.InvariantCulture, out minCompleteSeconds);
        continuousChallengers = Environment.GetEnvironmentVariable("PHASE4_5_CONTINUOUS_CHALLENGERS") == "1";

        try
        {
            config = JsonUtility.FromJson<NextTrialConfig>(File.ReadAllText(configPath));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LiveTelemetry] Failed to parse {configPath}: {ex.Message}");
            enabled = false;
            return;
        }

        if (config == null || config.required_outputs == null)
        {
            Debug.LogError("[LiveTelemetry] Malformed next_trial_config.json");
            enabled = false;
            return;
        }

        beliefMu = config.prior_mu;
        beliefSigma = config.prior_sigma;
        opponentRatingMu = config.opponent_rating_mu > 0f ? config.opponent_rating_mu : 1000f;
        opponentRatingSigma = config.opponent_rating_sigma > 0f ? config.opponent_rating_sigma : 250f;
        hasBeliefPrior = beliefMu > 0f && beliefSigma > 0f;

        trialDonePath = config.required_outputs.trial_done_json ?? "";
        telemetryPath = config.required_outputs.live_trial_telemetry_jsonl;
        if (string.IsNullOrEmpty(telemetryPath))
        {
            string dir = Path.GetDirectoryName(trialDonePath);
            telemetryPath = Path.Combine(dir ?? Application.persistentDataPath, "live_trial_telemetry.jsonl");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(telemetryPath));
        if (!string.IsNullOrEmpty(trialDonePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trialDonePath));
        }
        File.WriteAllText(telemetryPath, "");

        if (config.mode != "human")
        {
            FailTrial("next_trial_config_mode_not_human");
            return;
        }
        if (player == null || opponent == null || matchManager == null)
        {
            FailTrial("missing_match_or_player_references");
            return;
        }
        if (!string.IsNullOrEmpty(config.unity.game_mode) && config.unity.game_mode != unityMode)
        {
            FailTrial("unity_mode_mismatch_expected_" + config.unity.game_mode + "_actual_" + unityMode);
            return;
        }
        if (!string.IsNullOrEmpty(config.unity.player_b_bot_mode) && config.unity.player_b_bot_mode != playerBBotMode)
        {
            FailTrial("player_b_bot_mode_mismatch_expected_" + config.unity.player_b_bot_mode + "_actual_" + playerBBotMode);
            return;
        }

        matchManager.OnHit += OnHit;
        matchManager.OnKill += OnKill;
        matchManager.OnMatchOver += OnMatchOver;
        matchManager.OnRoundReset += OnRoundReset;
        matchManager.OnMatchReset += OnRoundReset;
        matchManager.OnContinuousContactTimeout += OnContinuousContactTimeout;

        startTime = Time.time;
        lastSampleTime = Time.time;
        lastPlayerPos = player.transform.position;
        lastOpponentPos = opponent.transform.position;
        lastPlayerHp = PlayerHp();
        lastOpponentHp = OpponentHp();
        InitializeSpawnDiagnostics();
        active = true;
        AppendTelemetry("trial_start", 0, 0, 0, 0, 0, 0);
        Debug.Log($"[LiveTelemetry] Active session={config.session_id} trial={config.trial_index} telemetry={telemetryPath}");
    }

    private void Update()
    {
        if (!active || finished) return;

        if (timeToFirstLos < 0f && HasLineOfSight())
        {
            timeToFirstLos = Time.time - startTime;
            engagementObserved = true;
            AppendTelemetry("first_los", 0, 0, 0, 0, 0, 0);
        }

        AppendTelemetry("tick", 0, 0, 0, 0, 0, 0);
        tickIndex += 1;

        if (Input.GetKeyDown(KeyCode.F10))
        {
            CompleteTrial("operator_f10_complete");
        }
        else if (maxSeconds > 0f && Time.time - startTime >= maxSeconds)
        {
            if (realisticSpawnsEnabled)
                CompleteTrial(engagementObserved ? "trial_timeout_after_engagement" : "no_contact_timeout");
            else
                FailTrial("trial_timeout");
        }
    }

    private void OnDestroy()
    {
        if (matchManager != null)
        {
            matchManager.OnHit -= OnHit;
            matchManager.OnKill -= OnKill;
            matchManager.OnMatchOver -= OnMatchOver;
            matchManager.OnRoundReset -= OnRoundReset;
            matchManager.OnMatchReset -= OnRoundReset;
            matchManager.OnContinuousContactTimeout -= OnContinuousContactTimeout;
        }
        if (active && !finished)
        {
            if (continuousChallengers && Time.time - startTime >= Mathf.Max(0f, minCompleteSeconds))
                CompleteTrial("operator_window_closed_after_continuous_demo");
            else
                FailTrial("bridge_destroyed_before_trial_done");
        }
    }

    private void OnHit(PlayerIdentity shooter, PlayerIdentity victim)
    {
        if (!active || finished) return;
        int hitDealt = shooter == player.Identity ? 1 : 0;
        int hitTaken = victim == player.Identity ? 1 : 0;
        if ((hitDealt == 1 || hitTaken == 1) && timeToFirstHit < 0f)
        {
            timeToFirstHit = Time.time - startTime;
            engagementObserved = true;
        }
        if (hitDealt == 1)
            ApplyLiveBeliefUpdate("hit_dealt", 0.62f, 0.25f);
        else if (hitTaken == 1)
            ApplyLiveBeliefUpdate("hit_taken", 0.38f, 0.25f);
        AppendTelemetry(hitDealt == 1 ? "hit_dealt" : hitTaken == 1 ? "hit_taken" : "hit_other", hitDealt, hitTaken, 0, 0, 0, 0);
    }

    private void OnKill(PlayerIdentity killer, PlayerIdentity victim)
    {
        if (!active || finished) return;
        int killDealt = killer == player.Identity ? 1 : 0;
        int deathTaken = victim == player.Identity ? 1 : 0;
        string reason = killDealt == 1 ? "kill_dealt" : deathTaken == 1 ? "death_taken" : "kill_other";
        if (killDealt == 1)
            ApplyLiveBeliefUpdate("kill_dealt", 1.0f, 1.0f);
        else if (deathTaken == 1)
            ApplyLiveBeliefUpdate("death_taken", 0.0f, 1.0f);
        AppendTelemetry(reason, 0, 0, killDealt, deathTaken, 0, 0);
        if (continuousChallengers)
        {
            AppendTelemetry("continuous_challenger_respawn_pending", 0, 0, 0, 0, 0, 0);
            Debug.Log($"[LiveTelemetry] Continuous challenger kill observed reason={reason}; trial remains active.");
            return;
        }
        CompleteTrial(reason);
    }

    private void OnMatchOver()
    {
        if (!active || finished) return;
        CompleteTrial("match_over");
    }

    private void OnRoundReset()
    {
        if (!active || finished) return;
        AppendTelemetry("round_reset", 0, 0, 0, 0, 0, 0);
    }

    private void OnContinuousContactTimeout(float penalty, float safetySeconds)
    {
        if (!active || finished) return;
        ApplyLiveBeliefUpdate("continuous_contact_timeout", 0.35f, 0.35f);
        AppendTelemetry("continuous_contact_timeout", 0, 0, 0, 0, 0, 0);
    }

    public bool ShouldDeferTerminalCompletion()
    {
        return active && !finished && minCompleteSeconds > 0f && Time.time - startTime < minCompleteSeconds;
    }

    public bool ShouldContinueTerminalKills()
    {
        return active && !finished && continuousChallengers;
    }

    private void CompleteTrial(string reason)
    {
        if (finished) return;
        if (ShouldDeferTerminalCompletion())
        {
            AppendTelemetry("completion_deferred_" + reason, 0, 0, 0, 0, 0, 0);
            Debug.Log($"[LiveTelemetry] Deferring completion reason={reason} elapsed={Time.time - startTime:F2}s min={minCompleteSeconds:F2}s");
            return;
        }
        AppendTelemetry(reason, 0, 0, 0, 0, 0, 0);
        WriteTrialDone("complete", reason, false);
        finished = true;
        active = false;
        Debug.Log($"[LiveTelemetry] Trial complete reason={reason}");
    }

    public bool CompleteTrialFromGuardedTimeout(string reason)
    {
        if (!active || finished)
            return false;
        CompleteTrial(string.IsNullOrEmpty(reason) ? "guarded_trial_timeout" : reason);
        return finished;
    }

    private void FailTrial(string reason)
    {
        if (finished) return;
        try
        {
            if (!string.IsNullOrEmpty(telemetryPath))
            {
                AppendTelemetry("failed", 0, 0, 0, 0, 0, 0);
            }
        }
        catch { }
        WriteTrialDone("failed", reason, false);
        finished = true;
        active = false;
        Debug.LogError($"[LiveTelemetry] Trial failed reason={reason}");
    }

    private float PlayerHp()
    {
        return player != null && player.Health != null ? player.Health.CurrentHealth : 0f;
    }

    private float OpponentHp()
    {
        return opponent != null && opponent.Health != null ? opponent.Health.CurrentHealth : 0f;
    }

    private void AppendTelemetry(string eventType, int hitDealt, int hitTaken, int killDealt, int deathTaken, float explicitDamageDealt, float explicitDamageTaken)
    {
        if (string.IsNullOrEmpty(telemetryPath) || player == null || opponent == null) return;

        float now = Time.time;
        float dt = Mathf.Max(1e-4f, now - lastSampleTime);
        Vector3 playerPos = player.transform.position;
        Vector3 opponentPos = opponent.transform.position;
        Vector3 playerVel = (playerPos - lastPlayerPos) / dt;
        Vector3 opponentVel = (opponentPos - lastOpponentPos) / dt;

        float playerHp = PlayerHp();
        float opponentHp = OpponentHp();
        float damageDealt = explicitDamageDealt > 0f ? explicitDamageDealt : Mathf.Max(0f, lastOpponentHp - opponentHp);
        float damageTaken = explicitDamageTaken > 0f ? explicitDamageTaken : Mathf.Max(0f, lastPlayerHp - playerHp);

        Vector3 toOpponent = opponentPos - playerPos;
        Vector3 flatToOpponent = Vector3.ProjectOnPlane(toOpponent, Vector3.up);
        Vector3 flatForward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up);
        float yawErr = 0f;
        if (flatToOpponent.sqrMagnitude > 1e-6f && flatForward.sqrMagnitude > 1e-6f)
        {
            yawErr = Vector3.SignedAngle(flatForward.normalized, flatToOpponent.normalized, Vector3.up);
        }
        float targetPitch = 0f;
        if (toOpponent.sqrMagnitude > 1e-6f)
        {
            targetPitch = Mathf.Asin(Mathf.Clamp(toOpponent.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        }
        float playerPitch = NormalizePitch(player.transform.eulerAngles.x);
        float pitchErr = targetPitch - playerPitch;
        float aimMag = Mathf.Sqrt(yawErr * yawErr + pitchErr * pitchErr);

        int shotPressed = Input.GetButton("Fire1") ? 1 : 0;
        if (shotPressed == 1 && timeToFirstShot < 0f)
        {
            timeToFirstShot = now - startTime;
            engagementObserved = true;
        }

        LiveTelemetryRow row = new LiveTelemetryRow();
        row.session_id = config != null ? config.session_id : "";
        row.participant_id = config != null ? config.participant_id : "";
        row.trial_index = config != null ? config.trial_index : 0;
        row.mode = "human";
        row.stimulus_id = config != null ? config.stimulus_id : "";
        row.unity_mode = unityMode;
        row.player_b_bot_mode = playerBBotMode;
        row.neural_policy_id = config != null && config.unity != null ? config.unity.neural_policy_id : "";
        row.timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        row.time_sec = now - startTime;
        row.decision_index = tickIndex;
        row.tick_index = tickIndex;
        row.event_type = eventType;
        row.player_hp = playerHp;
        row.opponent_hp = opponentHp;
        row.damage_dealt = damageDealt;
        row.damage_taken = damageTaken;
        row.shot_fired = 0;
        row.shot_pressed = shotPressed;
        row.hit_dealt = hitDealt;
        row.hit_taken = hitTaken;
        row.kill_dealt = killDealt;
        row.death_taken = deathTaken;
        row.player_position_x = playerPos.x;
        row.player_position_y = playerPos.y;
        row.player_position_z = playerPos.z;
        row.player_rotation_x = player.transform.eulerAngles.x;
        row.player_rotation_y = player.transform.eulerAngles.y;
        row.player_rotation_z = player.transform.eulerAngles.z;
        row.player_velocity_x = playerVel.x;
        row.player_velocity_y = playerVel.y;
        row.player_velocity_z = playerVel.z;
        row.opponent_position_x = opponentPos.x;
        row.opponent_position_y = opponentPos.y;
        row.opponent_position_z = opponentPos.z;
        row.opponent_rotation_x = opponent.transform.eulerAngles.x;
        row.opponent_rotation_y = opponent.transform.eulerAngles.y;
        row.opponent_rotation_z = opponent.transform.eulerAngles.z;
        row.opponent_velocity_x = opponentVel.x;
        row.opponent_velocity_y = opponentVel.y;
        row.opponent_velocity_z = opponentVel.z;
        row.aim_error_yaw = yawErr;
        row.aim_error_pitch = pitchErr;
        row.aim_error_mag = aimMag;
        row.trial_reset_generation = matchManager != null ? matchManager.ResetGeneration : 0;
        row.life_id = row.session_id + ":trial:" + row.trial_index.ToString(CultureInfo.InvariantCulture) + ":life:" + row.trial_reset_generation.ToString(CultureInfo.InvariantCulture);
        row.spawn_bucket = spawnBucket;
        row.spawn_distance = spawnDistance;
        row.initial_line_of_sight = initialLineOfSight;
        row.obstacle_between_players = obstacleBetweenPlayers;
        row.time_to_first_los = timeToFirstLos;
        row.time_to_first_shot = timeToFirstShot;
        row.time_to_first_hit = timeToFirstHit;
        row.termination_reason = TerminationReasonForEvent(eventType);
        row.belief_mu = beliefMu;
        row.belief_sigma = beliefSigma;
        row.opponent_rating_mu = opponentRatingMu;
        row.opponent_rating_sigma = opponentRatingSigma;
        row.live_score_event_count = liveScoreEventCount;

        File.AppendAllText(telemetryPath, JsonUtility.ToJson(row) + "\n");

        lastPlayerPos = playerPos;
        lastOpponentPos = opponentPos;
        lastSampleTime = now;
        lastPlayerHp = playerHp;
        lastOpponentHp = opponentHp;
    }


    private void ApplyLiveBeliefUpdate(string reason, float observedScore, float evidenceWeight)
    {
        if (!hasBeliefPrior)
            return;
        observedScore = Mathf.Clamp01(observedScore);
        evidenceWeight = Mathf.Clamp(evidenceWeight, 0.05f, 1.5f);
        float expected = 1f / (1f + Mathf.Pow(10f, (opponentRatingMu - beliefMu) / 400f));
        float k = Mathf.Clamp(beliefSigma * 0.10f * evidenceWeight, 4f, 60f);
        float delta = k * (observedScore - expected);
        beliefMu = Mathf.Clamp(beliefMu + delta, 100f, 2500f);
        float shrink = Mathf.Clamp(1f - 0.030f * evidenceWeight, 0.90f, 0.995f);
        beliefSigma = Mathf.Clamp(beliefSigma * shrink, 120f, 750f);
        liveScoreEventCount += 1;
        Debug.Log($"[LiveTelemetry] Live Bayesian HUD update reason={reason} score={observedScore:F2} expected={expected:F2} delta={delta:F2} mu={beliefMu:F1} sigma={beliefSigma:F1} opponent_mu={opponentRatingMu:F1}");
    }

    private void WriteTrialDone(string status, string reason, bool replayOrSurrogate)
    {
        if (string.IsNullOrEmpty(trialDonePath)) return;
        TrialDone done = new TrialDone();
        done.session_id = config != null ? config.session_id : "";
        done.participant_id = config != null ? config.participant_id : "";
        done.trial_index = config != null ? config.trial_index : 0;
        done.stimulus_id = config != null ? config.stimulus_id : "";
        done.mode = "human";
        done.status = status;
        done.telemetry_path = telemetryPath;
        done.start_time = DateTime.UtcNow.AddSeconds(-(Time.time - startTime)).ToString("o", CultureInfo.InvariantCulture);
        done.end_time = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        done.duration_sec = Mathf.Max(0f, Time.time - startTime);
        done.failure_reason = status == "complete" ? "" : reason;
        done.completion_reason = status == "complete" ? reason : "";
        done.termination_reason = reason;
        done.replay_or_surrogate = replayOrSurrogate;
        File.WriteAllText(trialDonePath, JsonUtility.ToJson(done, true) + "\n");
    }

    private void InitializeSpawnDiagnostics()
    {
        realisticSpawnsEnabled = Environment.GetEnvironmentVariable("PHASE4_5_ENABLE_REALISTIC_SPAWNS") == "1"
            || Environment.GetEnvironmentVariable("PHASE4_4_ENABLE_SPAWN_BUCKETS") == "1";
        spawnBucket = Environment.GetEnvironmentVariable("PHASE4_5_ACTIVE_SPAWN_BUCKET")
            ?? Environment.GetEnvironmentVariable("PHASE4_5_SPAWN_BUCKET")
            ?? Environment.GetEnvironmentVariable("PHASE4_4_SPAWN_BUCKET")
            ?? "";
        spawnDistance = ParseFloatEnv("PHASE4_5_ACTIVE_SPAWN_DISTANCE", Vector3.Distance(player.transform.position, opponent.transform.position));
        bool los = HasLineOfSight();
        initialLineOfSight = los ? 1 : 0;
        timeToFirstLos = los ? 0f : -1f;
        bool obstacleIntent = Environment.GetEnvironmentVariable("PHASE4_5_ACTIVE_OBSTACLE_INTENT") == "1";
        obstacleBetweenPlayers = (!los || obstacleIntent) ? 1 : 0;
        engagementObserved = los;
    }

    private float ParseFloatEnv(string key, float fallback)
    {
        string value = Environment.GetEnvironmentVariable(key) ?? "";
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            return parsed;
        return fallback;
    }

    private bool HasLineOfSight()
    {
        if (player == null || opponent == null) return false;
        Vector3 origin = player.transform.position + Vector3.up * 0.7f;
        Vector3 target = opponent.transform.position + Vector3.up * 0.7f;
        Vector3 delta = target - origin;
        float distance = delta.magnitude;
        if (distance <= 1e-3f) return true;

        RaycastHit[] hits = Physics.RaycastAll(origin, delta.normalized, distance + 0.5f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit hit in hits)
        {
            if (IsInHierarchy(hit.transform, player.transform))
                continue;
            if (IsInHierarchy(hit.transform, opponent.transform))
                return true;
            return false;
        }
        return true;
    }

    private bool IsInHierarchy(Transform candidate, Transform root)
    {
        if (candidate == null || root == null) return false;
        return candidate == root || candidate.IsChildOf(root);
    }

    private string TerminationReasonForEvent(string eventType)
    {
        switch (eventType)
        {
            case "operator_f10_complete":
            case "trial_timeout_after_engagement":
            case "no_contact_timeout":
            case "kill_dealt":
            case "death_taken":
            case "kill_other":
            case "continuous_challenger_respawn_pending":
            case "match_over":
            case "no_los_timeout":
            case "los_no_engagement_timeout":
            case "engaged_no_hit_timeout":
            case "partial_combat_timeout":
            case "pathing_failure_timeout":
            case "guarded_trial_timeout":
            case "failed":
                return eventType;
            default:
                return "";
        }
    }

    private static float NormalizePitch(float eulerX)
    {
        return eulerX > 180f ? eulerX - 360f : eulerX;
    }

    [Serializable]
    public class NextTrialConfig
    {
        public string session_id;
        public string participant_id;
        public int trial_index;
        public string mode;
        public string stimulus_id;
        public float prior_mu;
        public float prior_sigma;
        public float opponent_rating_mu;
        public float opponent_rating_sigma;
        public UnityConfig unity;
        public RequiredOutputs required_outputs;
    }

    [Serializable]
    public class UnityConfig
    {
        public string game_mode;
        public string player_b_bot_mode;
        public string neural_policy_id;
    }

    [Serializable]
    public class RequiredOutputs
    {
        public string trial_done_json;
        public string live_trial_telemetry_jsonl;
    }

    [Serializable]
    public class LiveTelemetryRow
    {
        public string session_id;
        public string participant_id;
        public int trial_index;
        public string mode;
        public string stimulus_id;
        public string unity_mode;
        public string player_b_bot_mode;
        public string neural_policy_id;
        public string timestamp;
        public float time_sec;
        public int decision_index;
        public int tick_index;
        public string event_type;
        public float player_hp;
        public float opponent_hp;
        public float damage_dealt;
        public float damage_taken;
        public int shot_fired;
        public int shot_pressed;
        public int hit_dealt;
        public int hit_taken;
        public int kill_dealt;
        public int death_taken;
        public float player_position_x;
        public float player_position_y;
        public float player_position_z;
        public float player_rotation_x;
        public float player_rotation_y;
        public float player_rotation_z;
        public float player_velocity_x;
        public float player_velocity_y;
        public float player_velocity_z;
        public float opponent_position_x;
        public float opponent_position_y;
        public float opponent_position_z;
        public float opponent_rotation_x;
        public float opponent_rotation_y;
        public float opponent_rotation_z;
        public float opponent_velocity_x;
        public float opponent_velocity_y;
        public float opponent_velocity_z;
        public float aim_error_yaw;
        public float aim_error_pitch;
        public float aim_error_mag;
        public int trial_reset_generation;
        public string life_id;
        public string spawn_bucket;
        public float spawn_distance;
        public int initial_line_of_sight;
        public int obstacle_between_players;
        public float time_to_first_los;
        public float time_to_first_shot;
        public float time_to_first_hit;
        public string termination_reason;
        public float belief_mu;
        public float belief_sigma;
        public float opponent_rating_mu;
        public float opponent_rating_sigma;
        public int live_score_event_count;
    }

    [Serializable]
    public class TrialDone
    {
        public string session_id;
        public string participant_id;
        public int trial_index;
        public string stimulus_id;
        public string mode;
        public string status;
        public string telemetry_path;
        public string start_time;
        public string end_time;
        public float duration_sec;
        public string failure_reason;
        public string completion_reason;
        public string termination_reason;
        public bool replay_or_surrogate;
    }
}

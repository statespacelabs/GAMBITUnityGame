using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Unity.Barracuda;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Presentation-only Phase 6 controller that executes the frozen ONNX policy
/// inside Unity. It removes the external ML-Agents/Python lockstep while keeping
/// the actor/local45 schemas, recurrent state, safety layer, and action contract.
/// </summary>
[DefaultExecutionOrder(-1000)]
public sealed class Phase6UnityOnnxController : MonoBehaviour, IPlayerController
{
    private const int ActorSize = 231;
    private const int Local45Size = 45;
    private const int ActionSize = 8;
    private const int LosIndex = 193;

    public GambitAgentController TelemetryHelper;

    private readonly float[] actor = new float[ActorSize];
    private readonly float[] local45 = new float[Local45Size];
    private Phase5MapIndependentTelemetry actorTelemetry;
    private Phase6BarracudaPolicy policy;
    private MatchManager match;
    private PlayerCommand command;
    private string slot;
    private bool initialized;
    private bool wroteInitialParity;
    private bool wroteVisibleParity;
    private long decisions;
    private double inferenceMilliseconds;
    private double maximumInferenceMilliseconds;

    public static int SuccessfulInferenceCount { get; private set; }

    public PlayerCommand GetCommand() => initialized ? command : PlayerCommand.NoOp;

    public void Initialize(PlayerIdentity identity, MatchManager matchManager)
    {
        match = matchManager;
        slot = identity != null && identity.PlayerIndex == 0 ? "A" : "B";
        command = PlayerCommand.NoOp;

        PlayerBody body = GetComponent<PlayerBody>();
        actorTelemetry = GetComponent<Phase5MapIndependentTelemetry>();
        if (actorTelemetry == null)
            actorTelemetry = gameObject.AddComponent<Phase5MapIndependentTelemetry>();
        actorTelemetry.Initialize(body);

        if (TelemetryHelper == null)
            TelemetryHelper = GetComponent<GambitAgentController>();
        if (TelemetryHelper == null)
            throw new InvalidOperationException("Phase6 Unity ONNX requires a telemetry helper");
        TelemetryHelper.InitializeTelemetryOnly(identity, matchManager);

        policy = new Phase6BarracudaPolicy(ReadBackend());
        if (match != null)
        {
            match.OnRoundReset += ResetPolicy;
            match.OnMatchReset += ResetPolicy;
        }
        Phase6UnityOnnxFrameMonitor.Ensure(policy.BackendName);
        initialized = true;
        Debug.Log("[Phase6UnityONNX] initialized slot=" + slot
            + " backend=" + policy.BackendName + " external_python=0");
    }

    private void FixedUpdate()
    {
        if (!initialized || policy == null)
            return;
        try
        {
            actorTelemetry.BuildObservation(actor, true);
            TelemetryHelper.BuildTelemetryObservation(local45, true);
            long started = Stopwatch.GetTimestamp();
            float[] action = policy.Act(actor, local45);
            double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            decisions++;
            SuccessfulInferenceCount++;
            inferenceMilliseconds += elapsed;
            maximumInferenceMilliseconds = Math.Max(maximumInferenceMilliseconds, elapsed);
            command = ToCommand(action);
            TelemetryHelper.SetExternalCommandForTelemetry(command);
            WriteParityIfNeeded(action);
            if (decisions % 250 == 0)
            {
                Debug.Log("[Phase6UnityONNX] slot=" + slot
                    + " decisions=" + decisions.ToString(CultureInfo.InvariantCulture)
                    + " mean_ms=" + (inferenceMilliseconds / decisions).ToString("F4", CultureInfo.InvariantCulture)
                    + " max_ms=" + maximumInferenceMilliseconds.ToString("F4", CultureInfo.InvariantCulture));
            }
        }
        catch (Exception exception)
        {
            command = PlayerCommand.NoOp;
            initialized = false;
            Debug.LogException(exception);
            Debug.LogError("[Phase6UnityONNX] hard_stop slot=" + slot);
        }
    }

    private static PlayerCommand ToCommand(float[] action)
    {
        return new PlayerCommand
        {
            MoveX = Mathf.Clamp(action[0], -1f, 1f),
            MoveZ = Mathf.Clamp(action[1], -1f, 1f),
            Turn = Mathf.Clamp(action[2], -1f, 1f),
            LookPitch = 0f,
            Shoot = action[4] > 0.5f,
            Reload = action[5] > 0.5f,
            Jump = action[6] > 0.5f,
            Crouch = action[7] > 0.5f
        };
    }

    private void ResetPolicy()
    {
        command = PlayerCommand.NoOp;
        policy?.Reset();
        actorTelemetry?.ResetMemory();
        Debug.Log("[Phase6UnityONNX] recurrent_reset slot=" + slot);
    }

    private void OnDestroy()
    {
        if (match != null)
        {
            match.OnRoundReset -= ResetPolicy;
            match.OnMatchReset -= ResetPolicy;
        }
        policy?.Dispose();
    }

    private void WriteParityIfNeeded(float[] action)
    {
        bool visible = actor[LosIndex] > 0.5f;
        if (wroteInitialParity && (!visible || wroteVisibleParity))
            return;
        string directory = Environment.GetEnvironmentVariable("PHASE6_UNITY_ONNX_PARITY_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        Directory.CreateDirectory(directory);
        string kind = !wroteInitialParity ? "initial" : "visible";
        Phase6UnityOnnxParityRecord record = new Phase6UnityOnnxParityRecord
        {
            schema_version = "phase6_unity_onnx_parity_v001",
            backend = policy.BackendName,
            slot = slot,
            visible = visible,
            actor = (float[])actor.Clone(),
            local45 = (float[])local45.Clone(),
            applied_action = (float[])action.Clone()
        };
        File.WriteAllText(Path.Combine(directory, slot + "_" + kind + ".json"),
            JsonUtility.ToJson(record, true));
        if (!wroteInitialParity)
            wroteInitialParity = true;
        else
            wroteVisibleParity = true;
    }

    private static WorkerFactory.Type ReadBackend()
    {
        string value = (Environment.GetEnvironmentVariable("PHASE6_UNITY_ONNX_BACKEND") ?? "cpu")
            .Trim().ToLowerInvariant();
        if (value == "gpu" || value == "compute")
            return WorkerFactory.Type.ComputePrecompiled;
        if (value == "cpu" || value == "burst")
            return WorkerFactory.Type.CSharpBurst;
        throw new InvalidOperationException("PHASE6_UNITY_ONNX_BACKEND must be gpu or cpu");
    }
}

internal sealed class Phase6BarracudaPolicy : IDisposable
{
    private const int ActorSize = 231;
    private const int Local45Size = 45;
    private const int LosIndex = 193;
    private readonly IWorker navigator;
    private readonly IWorker combat;
    private readonly Phase6Local45Normalizer normalizer;
    private readonly Phase6MapIndependentSafetyLayer safety = new Phase6MapIndependentSafetyLayer();
    private readonly float[] navigatorHidden = new float[128];
    private readonly float[] combatHidden = new float[512];

    public string BackendName { get; }

    public Phase6BarracudaPolicy(WorkerFactory.Type workerType)
    {
        NNModel navigatorAsset = Resources.Load<NNModel>("MLModels/navigator");
        NNModel combatAsset = Resources.Load<NNModel>("MLModels/combat");
        TextAsset normalizerAsset = Resources.Load<TextAsset>("MLModels/normalizer");
        if (navigatorAsset == null || combatAsset == null || normalizerAsset == null)
            throw new InvalidOperationException("Phase6 ONNX resources are missing from the build");
        Model navigatorModel = ModelLoader.Load(navigatorAsset);
        Model combatModel = ModelLoader.Load(combatAsset);
        navigator = WorkerFactory.CreateWorker(workerType, navigatorModel);
        combat = WorkerFactory.CreateWorker(workerType, combatModel);
        normalizer = new Phase6Local45Normalizer(normalizerAsset.text);
        BackendName = workerType.ToString();
        Debug.Log("[Phase6UnityONNX] models_loaded backend=" + BackendName
            + " navigator_outputs=" + string.Join(",", navigatorModel.outputs)
            + " combat_outputs=" + string.Join(",", combatModel.outputs));
    }

    public float[] Act(float[] actor, float[] local45)
    {
        if (actor == null || actor.Length != ActorSize || local45 == null || local45.Length != Local45Size)
            throw new ArgumentException("Phase6 ONNX observation shape mismatch");
        float[] applied = new float[8];
        float[] nextNavigatorHidden;
        using (Tensor actorTensor = new Tensor(1, 1, ActorSize, 1, actor))
        using (Tensor hiddenTensor = new Tensor(1, 1, 128, 1, navigatorHidden))
        {
            navigator.Execute(new Dictionary<string, Tensor>
            {
                { "actor_observation", actorTensor },
                { "hidden_state", hiddenTensor }
            });
            float[] means = navigator.PeekOutput("action_mean").ToReadOnlyArray();
            nextNavigatorHidden = navigator.PeekOutput("next_hidden_state").ToReadOnlyArray();
            for (int index = 0; index < 4; index++)
                applied[index] = Mathf.Clamp(means[index], -1f, 1f);
        }
        Array.Copy(nextNavigatorHidden, navigatorHidden, navigatorHidden.Length);
        safety.Apply(actor, applied);

        if (actor[LosIndex] > 0.5f)
        {
            float[] normalized = normalizer.Normalize(local45);
            float[] nextCombatHidden;
            using (Tensor observationTensor = new Tensor(1, 1, Local45Size, 1, normalized))
            using (Tensor hiddenTensor = new Tensor(1, 1, 512, 1, combatHidden))
            {
                combat.Execute(new Dictionary<string, Tensor>
                {
                    { "obs_norm", observationTensor },
                    { "hidden", hiddenTensor }
                });
                float[] combatAction = combat.PeekOutput("action").ToReadOnlyArray();
                nextCombatHidden = combat.PeekOutput("next_hidden").ToReadOnlyArray();
                Array.Copy(combatAction, applied, applied.Length);
            }
            Array.Copy(nextCombatHidden, combatHidden, combatHidden.Length);
        }
        applied[3] = 0f;
        for (int index = 0; index < applied.Length; index++)
            if (float.IsNaN(applied[index]) || float.IsInfinity(applied[index]))
                throw new InvalidOperationException("Phase6 ONNX produced a non-finite action");
        return applied;
    }

    public void Reset()
    {
        Array.Clear(navigatorHidden, 0, navigatorHidden.Length);
        Array.Clear(combatHidden, 0, combatHidden.Length);
        safety.Reset();
    }

    public void Dispose()
    {
        navigator?.Dispose();
        combat?.Dispose();
    }
}

[Serializable]
internal sealed class Phase6NormalizerPayload
{
    public float clip_value;
    public float[] mean;
    public float[] std;
}

internal sealed class Phase6Local45Normalizer
{
    private readonly Phase6NormalizerPayload payload;

    public Phase6Local45Normalizer(string json)
    {
        payload = JsonUtility.FromJson<Phase6NormalizerPayload>(json);
        if (payload == null || payload.mean == null || payload.std == null
            || payload.mean.Length != 45 || payload.std.Length != 45)
            throw new InvalidOperationException("Phase6 local45 normalizer is invalid");
    }

    public float[] Normalize(float[] values)
    {
        float[] output = new float[45];
        for (int index = 0; index < output.Length; index++)
        {
            if (payload.std[index] <= 0f)
                throw new InvalidOperationException("Phase6 local45 normalizer std is not positive");
            float value = (values[index] - payload.mean[index]) / payload.std[index];
            output[index] = payload.clip_value > 0f
                ? Mathf.Clamp(value, -payload.clip_value, payload.clip_value) : value;
        }
        return output;
    }
}

internal sealed class Phase6MapIndependentSafetyLayer
{
    private const int FlipInterval = 500;
    private int wallSide;
    private int clearTicks;
    private int hiddenTicks;

    public void Reset()
    {
        wallSide = 0;
        clearTicks = 0;
        hiddenTicks = 0;
    }

    public void Apply(float[] actor, float[] output)
    {
        if (actor[193] > 0.5f)
        {
            Reset();
            return;
        }
        hiddenTicks++;
        int strongest = 0;
        float strongestValue = actor[211];
        for (int index = 1; index < 8; index++)
        {
            if (actor[211 + index] > strongestValue)
            {
                strongest = index;
                strongestValue = actor[211 + index];
            }
        }
        if (actor[224] <= 0.5f || strongestValue <= 0.5f)
            return;
        int target = (strongest + 4) % 8;
        float[] clearance = new float[8];
        for (int sector = 0; sector < 8; sector++)
            clearance[sector] = actor[30 + sector * 8];
        bool pressure = actor[28] > 0.25f || actor[27] > 0.35f;
        bool active = wallSide != 0;
        if (active && clearance[target] >= 0.14f && !pressure)
        {
            clearTicks++;
            if (clearTicks >= 8)
            {
                wallSide = 0;
                clearTicks = 0;
                active = false;
            }
        }
        else
        {
            clearTicks = 0;
        }
        if (!active && (clearance[target] < 0.075f || pressure))
        {
            wallSide = clearance[(target + 1) % 8] >= clearance[(target + 7) % 8] ? 1 : -1;
            active = true;
        }
        if (active && hiddenTicks % FlipInterval == 0)
        {
            wallSide = -wallSide;
            clearTicks = 0;
        }
        if (!active)
            return;
        int chosen = target;
        for (int offset = 1; offset < 8; offset++)
        {
            int candidate = (target + wallSide * offset + 64) % 8;
            if (clearance[candidate] >= 0.06f)
            {
                chosen = candidate;
                break;
            }
        }
        if (clearance[chosen] < 0.025f)
        {
            chosen = 0;
            for (int index = 1; index < 8; index++)
                if (clearance[index] > clearance[chosen]) chosen = index;
        }
        float angle = chosen * 45f;
        if (angle > 180f) angle -= 360f;
        output[0] = Mathf.Clamp(Mathf.Sin(angle * Mathf.Deg2Rad) * 0.75f, -0.75f, 0.75f);
        output[1] = Mathf.Abs(angle) < 100f ? 0.85f : 0.15f;
        output[2] = Mathf.Clamp(angle / 75f, -1f, 1f);
    }
}

[Serializable]
internal sealed class Phase6UnityOnnxParityRecord
{
    public string schema_version;
    public string backend;
    public string slot;
    public bool visible;
    public float[] actor;
    public float[] local45;
    public float[] applied_action;
}

[Serializable]
internal sealed class Phase6UnityOnnxFrameSummary
{
    public string schema_version = "phase6_unity_onnx_frame_summary_v001";
    public string backend;
    public string graphics_device;
    public int target_frame_rate;
    public int frames;
    public float mean_frame_ms;
    public float p50_frame_ms;
    public float p95_frame_ms;
    public float p99_frame_ms;
    public float max_frame_ms;
}

[DefaultExecutionOrder(20000)]
public sealed class Phase6UnityOnnxFrameMonitor : MonoBehaviour
{
    private readonly List<float> frameMilliseconds = new List<float>();
    private string backend;
    private bool readyWritten;
    private bool captureStarted;

    public static void Ensure(string backendName)
    {
        Phase6UnityOnnxFrameMonitor existing = FindObjectOfType<Phase6UnityOnnxFrameMonitor>();
        if (existing != null)
            return;
        GameObject monitorObject = new GameObject("_Phase6UnityOnnxFrameMonitor");
        DontDestroyOnLoad(monitorObject);
        Phase6UnityOnnxFrameMonitor monitor = monitorObject.AddComponent<Phase6UnityOnnxFrameMonitor>();
        monitor.backend = backendName;
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup > 0.5f && Time.unscaledDeltaTime > 0f)
            frameMilliseconds.Add(Time.unscaledDeltaTime * 1000f);
        if (!readyWritten && frameMilliseconds.Count >= 120
            && Phase6UnityOnnxController.SuccessfulInferenceCount >= 2)
        {
            string readyPath = Environment.GetEnvironmentVariable("PHASE6_UNITY_ONNX_READY_PATH");
            if (!string.IsNullOrWhiteSpace(readyPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(readyPath));
                File.WriteAllText(readyPath, "ready\n");
            }
            readyWritten = true;
            WriteSummary();
            Debug.Log("[Phase6UnityONNX] capture_ready frames=" + frameMilliseconds.Count);
        }
        if (readyWritten && !captureStarted)
        {
            string armPath = Environment.GetEnvironmentVariable("PHASE6_UNITY_ONNX_CAPTURE_ARM_PATH");
            if (!string.IsNullOrWhiteSpace(armPath) && File.Exists(armPath))
            {
                MatchManager match = FindObjectOfType<MatchManager>();
                if (match == null)
                    throw new InvalidOperationException("Capture-arm reset requires MatchManager");
                match.ResetMatch();
                frameMilliseconds.Clear();
                string startedPath = Environment.GetEnvironmentVariable("PHASE6_UNITY_ONNX_CAPTURE_STARTED_PATH");
                if (!string.IsNullOrWhiteSpace(startedPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(startedPath));
                    File.WriteAllText(startedPath, "started\n");
                }
                captureStarted = true;
                Debug.Log("[Phase6UnityONNX] capture_round_started");
            }
        }
        if (frameMilliseconds.Count > 0 && frameMilliseconds.Count % 300 == 0)
            WriteSummary();
    }

    private void OnApplicationQuit() => WriteSummary();

    private void WriteSummary()
    {
        string path = Environment.GetEnvironmentVariable("PHASE6_UNITY_ONNX_FRAME_SUMMARY_PATH");
        if (string.IsNullOrWhiteSpace(path) || frameMilliseconds.Count == 0)
            return;
        float[] sorted = frameMilliseconds.ToArray();
        Array.Sort(sorted);
        float sum = 0f;
        foreach (float value in sorted) sum += value;
        Phase6UnityOnnxFrameSummary summary = new Phase6UnityOnnxFrameSummary
        {
            backend = backend,
            graphics_device = SystemInfo.graphicsDeviceName,
            target_frame_rate = Application.targetFrameRate,
            frames = sorted.Length,
            mean_frame_ms = sum / sorted.Length,
            p50_frame_ms = Percentile(sorted, 0.50f),
            p95_frame_ms = Percentile(sorted, 0.95f),
            p99_frame_ms = Percentile(sorted, 0.99f),
            max_frame_ms = sorted[sorted.Length - 1]
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(summary, true));
    }

    private static float Percentile(float[] sorted, float fraction)
    {
        int index = Mathf.Clamp(Mathf.CeilToInt(sorted.Length * fraction) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}


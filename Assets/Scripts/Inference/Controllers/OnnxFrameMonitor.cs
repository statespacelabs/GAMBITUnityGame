using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[Serializable]
internal sealed class OnnxFrameSummary
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
[UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, null, "Phase6UnityOnnxFrameMonitor")]
public sealed class OnnxFrameMonitor : MonoBehaviour
{
    private readonly List<float> frameMilliseconds = new List<float>();
    private string backend;
    private bool readyWritten;
    private bool captureStarted;

    public static void Ensure(string backendName)
    {
        OnnxFrameMonitor existing = FindObjectOfType<OnnxFrameMonitor>();
        if (existing != null)
            return;
        GameObject monitorObject = new GameObject("_OnnxFrameMonitor");
        DontDestroyOnLoad(monitorObject);
        OnnxFrameMonitor monitor = monitorObject.AddComponent<OnnxFrameMonitor>();
        monitor.backend = backendName;
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup > 0.5f && Time.unscaledDeltaTime > 0f)
            frameMilliseconds.Add(Time.unscaledDeltaTime * 1000f);
        if (!readyWritten && frameMilliseconds.Count >= 120
            && OnnxPolicyController.SuccessfulInferenceCount >= 2)
        {
            string readyPath = Environment.GetEnvironmentVariable("PHASE6_UNITY_ONNX_READY_PATH");
            if (!string.IsNullOrWhiteSpace(readyPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(readyPath));
                File.WriteAllText(readyPath, "ready\n");
            }
            readyWritten = true;
            WriteSummary();
            Debug.Log("[OnnxPolicy] capture_ready frames=" + frameMilliseconds.Count);
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
                Debug.Log("[OnnxPolicy] capture_round_started");
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
        OnnxFrameSummary summary = new OnnxFrameSummary
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

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// Fail-closed Phase 5 headless launch contract. The mode is enabled only by
/// --phase5-headless and never changes the interactive runtime.
/// </summary>
public static class Phase5HeadlessRuntime
{
    public const string LaunchFlag = "--phase5-headless";
    private static bool? enabled;
    private static HeadlessAudit lastAudit;

    public static bool Enabled
    {
        get
        {
            if (!enabled.HasValue)
                enabled = HasArgument(LaunchFlag);
            return enabled.Value;
        }
    }

    public static HeadlessAudit LastAudit => lastAudit;

    public static bool ValidateLaunch()
    {
        if (!Enabled)
            return true;

        List<string> failures = new List<string>();
        if (!HasArgument("-batchmode"))
            failures.Add("missing_-batchmode");
        if (!HasArgument("-nographics"))
            failures.Add("missing_-nographics");
        if (!Application.isBatchMode)
            failures.Add("Application.isBatchMode_false");
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            failures.Add("graphics_device_not_null:" + SystemInfo.graphicsDeviceType);
        if (Environment.GetEnvironmentVariable("ENABLE_VISUAL_OBS") == "1")
            failures.Add("ENABLE_VISUAL_OBS_forbidden");

        if (failures.Count == 0)
            return true;

        Debug.LogError("[Phase5Headless] launch validation failed: " + string.Join(",", failures));
        Application.Quit(52);
        return false;
    }

    public static HeadlessAudit SuppressRenderingAndAudit()
    {
        HeadlessAudit audit = new HeadlessAudit();
        audit.schema_version = "phase5_headless_audit_v1";
        audit.enabled = Enabled;
        audit.application_batch_mode = Application.isBatchMode;
        audit.graphics_device = SystemInfo.graphicsDeviceType.ToString();

        if (!Enabled)
        {
            lastAudit = audit;
            return audit;
        }

        Collider[] colliders = UnityEngine.Object.FindObjectsOfType<Collider>(true);
        CharacterController[] controllers = UnityEngine.Object.FindObjectsOfType<CharacterController>(true);
        NavMeshAgent[] navAgents = UnityEngine.Object.FindObjectsOfType<NavMeshAgent>(true);
        Camera[] cameras = UnityEngine.Object.FindObjectsOfType<Camera>(true);
        Dictionary<int, Pose> cameraPoses = new Dictionary<int, Pose>();
        foreach (Camera camera in cameras)
        {
            cameraPoses[camera.GetInstanceID()] = new Pose(camera.transform.position, camera.transform.rotation);
        }

        audit.colliders_enabled_before = CountEnabled(colliders);
        audit.character_controllers_enabled_before = CountEnabled(controllers);
        audit.navmesh_agents_enabled_before = CountEnabled(navAgents);
        audit.cameras_total = cameras.Length;

        foreach (Camera camera in cameras)
        {
            camera.enabled = false;
            camera.targetTexture = null;
        }
        foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            renderer.enabled = false;
        foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>(true))
            light.enabled = false;
        foreach (Canvas canvas in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
            canvas.enabled = false;
        foreach (Graphic graphic in UnityEngine.Object.FindObjectsOfType<Graphic>(true))
            graphic.enabled = false;
        foreach (AudioSource source in UnityEngine.Object.FindObjectsOfType<AudioSource>(true))
        {
            source.Stop();
            source.enabled = false;
        }
        foreach (AudioListener listener in UnityEngine.Object.FindObjectsOfType<AudioListener>(true))
            listener.enabled = false;
        foreach (ParticleSystem particles in UnityEngine.Object.FindObjectsOfType<ParticleSystem>(true))
        {
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = false;
        }
        foreach (VideoPlayer video in UnityEngine.Object.FindObjectsOfType<VideoPlayer>(true))
        {
            video.Stop();
            video.enabled = false;
        }
        foreach (Behaviour behaviour in UnityEngine.Object.FindObjectsOfType<Behaviour>(true))
        {
            if (behaviour == null)
                continue;
            string fullName = behaviour.GetType().FullName ?? "";
            if (IsSuppressedVisualBehaviour(fullName))
                behaviour.enabled = false;
        }

        AudioListener.pause = true;
        AudioListener.volume = 0f;
        QualitySettings.vSyncCount = 0;
        QualitySettings.antiAliasing = 0;
        QualitySettings.shadows = ShadowQuality.Disable;
        QualitySettings.shadowDistance = 0f;
        Application.targetFrameRate = -1;

        audit.cameras_enabled_after = CountEnabled(UnityEngine.Object.FindObjectsOfType<Camera>(true));
        audit.renderers_enabled_after = CountEnabled(UnityEngine.Object.FindObjectsOfType<Renderer>(true));
        audit.lights_enabled_after = CountEnabled(UnityEngine.Object.FindObjectsOfType<Light>(true));
        audit.canvases_enabled_after = CountEnabled(UnityEngine.Object.FindObjectsOfType<Canvas>(true));
        audit.ui_graphics_enabled_after = CountEnabled(UnityEngine.Object.FindObjectsOfType<Graphic>(true));
        audit.postprocess_overlay_recorders_enabled_after = CountSuppressedBehavioursEnabled();
        audit.audio_sources_enabled_after = CountEnabled(UnityEngine.Object.FindObjectsOfType<AudioSource>(true));
        audit.audio_listeners_enabled_after = CountEnabled(UnityEngine.Object.FindObjectsOfType<AudioListener>(true));
        audit.particle_systems_playing_after = CountPlaying(UnityEngine.Object.FindObjectsOfType<ParticleSystem>(true));
        audit.video_players_enabled_after = CountEnabled(UnityEngine.Object.FindObjectsOfType<VideoPlayer>(true));
        audit.colliders_enabled_after = CountEnabled(UnityEngine.Object.FindObjectsOfType<Collider>(true));
        audit.character_controllers_enabled_after = CountEnabled(UnityEngine.Object.FindObjectsOfType<CharacterController>(true));
        audit.navmesh_agents_enabled_after = CountEnabled(UnityEngine.Object.FindObjectsOfType<NavMeshAgent>(true));

        audit.camera_transforms_unchanged = true;
        foreach (Camera camera in cameras)
        {
            if (!cameraPoses.TryGetValue(camera.GetInstanceID(), out Pose before))
            {
                audit.camera_transforms_unchanged = false;
                break;
            }
            if (Vector3.Distance(before.position, camera.transform.position) > 1e-7f
                || Quaternion.Angle(before.rotation, camera.transform.rotation) > 1e-6f)
            {
                audit.camera_transforms_unchanged = false;
                break;
            }
        }

        audit.simulation_components_preserved =
            audit.colliders_enabled_before == audit.colliders_enabled_after
            && audit.character_controllers_enabled_before == audit.character_controllers_enabled_after
            && audit.navmesh_agents_enabled_before == audit.navmesh_agents_enabled_after;
        audit.no_render_audio_video =
            audit.cameras_enabled_after == 0
            && audit.renderers_enabled_after == 0
            && audit.lights_enabled_after == 0
            && audit.canvases_enabled_after == 0
            && audit.ui_graphics_enabled_after == 0
            && audit.postprocess_overlay_recorders_enabled_after == 0
            && audit.audio_sources_enabled_after == 0
            && audit.audio_listeners_enabled_after == 0
            && audit.particle_systems_playing_after == 0
            && audit.video_players_enabled_after == 0;
        audit.status = audit.no_render_audio_video
            && audit.simulation_components_preserved
            && audit.camera_transforms_unchanged ? "PASS" : "FAIL";

        lastAudit = audit;
        Debug.Log("[Phase5HeadlessAudit] " + JsonUtility.ToJson(audit));
        if (audit.status != "PASS")
            Application.Quit(53);
        return audit;
    }

    private static bool IsSuppressedVisualBehaviour(string fullName)
    {
        return fullName.Contains("PostProcess")
            || fullName.EndsWith(".Volume")
            || fullName.Contains("Recorder")
            || fullName.Contains("Recording")
            || fullName.Contains("Overlay");
    }

    private static int CountSuppressedBehavioursEnabled()
    {
        int count = 0;
        foreach (Behaviour behaviour in UnityEngine.Object.FindObjectsOfType<Behaviour>(true))
        {
            if (behaviour == null || !behaviour.enabled)
                continue;
            if (IsSuppressedVisualBehaviour(behaviour.GetType().FullName ?? ""))
                count++;
        }
        return count;
    }

    private static bool HasArgument(string expected)
    {
        foreach (string arg in Environment.GetCommandLineArgs())
            if (string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static int CountEnabled<T>(T[] components) where T : Behaviour
    {
        int count = 0;
        foreach (T component in components)
            if (component != null && component.enabled)
                count++;
        return count;
    }

    private static int CountEnabled(Collider[] components)
    {
        int count = 0;
        foreach (Collider component in components)
            if (component != null && component.enabled)
                count++;
        return count;
    }

    private static int CountEnabled(Renderer[] components)
    {
        int count = 0;
        foreach (Renderer component in components)
            if (component != null && component.enabled)
                count++;
        return count;
    }

    private static int CountPlaying(ParticleSystem[] systems)
    {
        int count = 0;
        foreach (ParticleSystem system in systems)
            if (system != null && system.isPlaying)
                count++;
        return count;
    }

    [Serializable]
    public class HeadlessAudit
    {
        public string schema_version;
        public string status;
        public bool enabled;
        public bool application_batch_mode;
        public string graphics_device;
        public int cameras_total;
        public int cameras_enabled_after;
        public int renderers_enabled_after;
        public int lights_enabled_after;
        public int canvases_enabled_after;
        public int ui_graphics_enabled_after;
        public int postprocess_overlay_recorders_enabled_after;
        public int audio_sources_enabled_after;
        public int audio_listeners_enabled_after;
        public int particle_systems_playing_after;
        public int video_players_enabled_after;
        public int colliders_enabled_before;
        public int colliders_enabled_after;
        public int character_controllers_enabled_before;
        public int character_controllers_enabled_after;
        public int navmesh_agents_enabled_before;
        public int navmesh_agents_enabled_after;
        public bool camera_transforms_unchanged;
        public bool simulation_components_preserved;
        public bool no_render_audio_video;
    }
}

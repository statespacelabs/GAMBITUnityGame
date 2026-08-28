using System;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>
/// The one composition boundary that turns a PolicySpec into a Unity controller.
/// Controllers own decisions; PlayerBody remains the sole owner of applied commands.
/// </summary>
public static class PolicyInstaller
{
    private enum MlAgentsPolicyProfile
    {
        LocalFull,
        LocalNavigationAssisted,
        UnifiedToken
    }

    public static void Install(
        PlayerBody body,
        PolicySpec policy,
        ScriptedBotController.ScriptedBotMode fallbackScriptedMode,
        Camera referenceCamera,
        bool useLegacyEnvironmentController,
        int areaId)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (policy == null) throw new ArgumentNullException(nameof(policy));

        switch (policy.Kind)
        {
            case GambitPolicyKind.Human:
                Set(body, body.gameObject.AddComponent<HumanController>());
                return;
            case GambitPolicyKind.Scripted:
                InstallScripted(body, policy, fallbackScriptedMode);
                return;
            case GambitPolicyKind.Onnx:
                InstallOnnx(body, policy, useLegacyEnvironmentController, areaId);
                return;
            case GambitPolicyKind.MlAgents:
                InstallMlAgents(body, policy, referenceCamera, areaId);
                return;
            default:
                throw new NotSupportedException("No live policy installer exists for " + policy.Kind + ".");
        }
    }

    private static void InstallScripted(
        PlayerBody body,
        PolicySpec policy,
        ScriptedBotController.ScriptedBotMode fallback)
    {
        ScriptedBotController.ScriptedBotMode mode = fallback;
        if (!string.IsNullOrWhiteSpace(policy.Variant)
            && !Enum.TryParse(policy.Variant, true, out mode))
            throw new ArgumentException(
                "Unknown scripted bot variant '" + policy.Variant + "'.",
                nameof(policy));

        ScriptedBotController bot = body.gameObject.AddComponent<ScriptedBotController>();
        bot.BotMode = mode;
        Set(body, bot);
    }

    private static void InstallOnnx(
        PlayerBody body,
        PolicySpec policy,
        bool useLegacyEnvironmentController,
        int areaId)
    {
        bool unified = ValidateOnnxPolicy(policy, useLegacyEnvironmentController);
        if (unified)
        {
            InstallUnifiedOnnx(body, policy, areaId);
            return;
        }

        if (useLegacyEnvironmentController)
        {
            RLAgentController legacy = body.gameObject.AddComponent<RLAgentController>();
            BehaviorParameters behavior = legacy.GetComponent<BehaviorParameters>();
            if (behavior != null)
            {
                behavior.BehaviorName = "BotArenaAgent";
                behavior.BrainParameters.VectorObservationSize = 12;
                behavior.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(5, 3, 2);
            }
            Set(body, legacy);
            return;
        }

        bool wasActive = body.gameObject.activeSelf;
        body.gameObject.SetActive(false);
        GambitAgentController telemetry = body.gameObject.AddComponent<GambitAgentController>();
        telemetry.enabled = false;
        OnnxPolicyController onnx = body.gameObject.AddComponent<OnnxPolicyController>();
        onnx.TelemetryHelper = telemetry;
        Set(body, onnx);
        body.gameObject.SetActive(wasActive);
        Debug.Log($"[PolicyInstaller] area_id={areaId} bundled ONNX -> {body.Identity.DisplayName}");
    }

    private static void InstallUnifiedOnnx(PlayerBody body, PolicySpec policy, int areaId)
    {
        bool wasActive = body.gameObject.activeSelf;
        body.gameObject.SetActive(false);
        Gen3UnifiedOnnxController controller =
            body.gameObject.AddComponent<Gen3UnifiedOnnxController>();
        if (!string.IsNullOrWhiteSpace(policy.ModelPath))
            controller.ModelResource = NormalizeResourcePath(policy.ModelPath);
        Set(body, controller);
        body.gameObject.SetActive(wasActive);
        Debug.Log($"[PolicyInstaller] area_id={areaId} unified ONNX -> {body.Identity.DisplayName}");
    }

    private static void InstallMlAgents(PlayerBody body, PolicySpec policy, Camera referenceCamera, int areaId)
    {
        MlAgentsPolicyProfile profile = ResolveMlAgentsProfile(policy);
        bool unified = profile == MlAgentsPolicyProfile.UnifiedToken;
        bool navigationAssisted = profile == MlAgentsPolicyProfile.LocalNavigationAssisted;

        bool wasActive = body.gameObject.activeSelf;
        body.gameObject.SetActive(false);

        BehaviorParameters behavior = body.gameObject.AddComponent<BehaviorParameters>();
        behavior.BehaviorName = string.IsNullOrWhiteSpace(policy.BehaviorName) ? "GambitAgent" : policy.BehaviorName;
        behavior.BrainParameters.VectorObservationSize = 0;
        int continuousActionCount = unified
            ? UnifiedFairObservationV2Contract.ActionContinuousCount
            : navigationAssisted
                ? Local45CombatActionContract.ContinuousCount
                : PolicyActionContract.ContinuousCount;
        int binaryActionCount = unified
            ? UnifiedFairObservationV2Contract.ActionBinaryCount
            : navigationAssisted
                ? Local45CombatActionContract.BinaryActionCount
                : PolicyActionContract.BinaryActionCount;
        int binaryBranchSize = unified
            ? UnifiedFairObservationV2Contract.ActionBinaryBranchSize
            : navigationAssisted
                ? Local45CombatActionContract.BinaryBranchSize
                : PolicyActionContract.BinaryBranchSize;
        behavior.BrainParameters.ActionSpec = new ActionSpec(
            continuousActionCount,
            CreateUniformBranches(binaryActionCount, binaryBranchSize));

        Camera agentCamera = CreateAgentCamera(body, referenceCamera);
        if (unified)
            body.gameObject.AddComponent<Gen3UnifiedTokenSensorComponent>();
        else
            body.gameObject.AddComponent<GambitTelemetrySensorComponent>();
        MaybeInstallVisualSensor(body, agentCamera);

        // Agent must precede DecisionRequester; otherwise RequireComponent can create a second Agent.
        GambitAgentController agent = body.gameObject.AddComponent<GambitAgentController>();
        agent.enabled = false;
        agent.AgentCamera = agentCamera;
        agent.UseNavigationAssistedActions = navigationAssisted;
        Unity.MLAgents.DecisionRequester requester = body.gameObject.AddComponent<Unity.MLAgents.DecisionRequester>();
        requester.DecisionPeriod = 1;
        requester.enabled = false;

        if (navigationAssisted)
        {
            NavigationAssistedMlAgentsController hybrid =
                body.gameObject.AddComponent<NavigationAssistedMlAgentsController>();
            hybrid.Learner = agent;
            Set(body, hybrid);
        }
        else
        {
            Set(body, agent);
        }
        agent.enabled = true;
        requester.enabled = true;
        body.gameObject.SetActive(wasActive);
        Debug.Log($"[PolicyInstaller] area_id={areaId} ML-Agents {behavior.BehaviorName}"
            + (unified ? " unified direct-owner" : navigationAssisted
                ? " + frozen actor231 navigator" : "")
            + $" -> {body.Identity.DisplayName}");
    }

    private static bool ValidateOnnxPolicy(
        PolicySpec policy,
        bool useLegacyEnvironmentController)
    {
        if (policy.ObservationSchema == UnifiedFairObservationV2Contract.TokenSchemaId)
            throw new NotSupportedException(
                UnifiedFairObservationV2Contract.TokenSchemaId
                + " is the ML-Agents training sensor schema. Unified ONNX policies must declare "
                + UnifiedFairObservationV2Contract.SchemaId + ".");

        bool unified = policy.ObservationSchema == UnifiedFairObservationV2Contract.SchemaId;
        if (unified && policy.ActionSchema != UnifiedFairObservationV2Contract.ActionSchemaId)
            throw new NotSupportedException("Unified ONNX requires action schema "
                + UnifiedFairObservationV2Contract.ActionSchemaId + ".");

        if (!unified && !useLegacyEnvironmentController)
        {
            if (policy.ObservationSchema != LocalObservationContract.SchemaId)
                throw new NotSupportedException("Bundled ONNX requires observation schema "
                    + LocalObservationContract.SchemaId + ".");
            if (policy.ActionSchema != PolicyActionContract.SchemaId)
                throw new NotSupportedException("Bundled ONNX requires action schema "
                    + PolicyActionContract.SchemaId + ".");
        }

        return unified;
    }

    private static MlAgentsPolicyProfile ResolveMlAgentsProfile(PolicySpec policy)
    {
        if (policy.ObservationSchema == UnifiedFairObservationV2Contract.SchemaId)
            throw new NotSupportedException(
                UnifiedFairObservationV2Contract.SchemaId
                + " is the unified ONNX base-observation schema. ML-Agents training must declare "
                + UnifiedFairObservationV2Contract.TokenSchemaId + ".");

        if (policy.ObservationSchema == UnifiedFairObservationV2Contract.TokenSchemaId)
        {
            if (policy.ActionSchema == UnifiedFairObservationV2Contract.ActionSchemaId)
                return MlAgentsPolicyProfile.UnifiedToken;
            throw UnsupportedSchemaPair(policy);
        }

        if (policy.ObservationSchema == LocalObservationContract.SchemaId)
        {
            if (policy.ActionSchema == PolicyActionContract.SchemaId)
                return MlAgentsPolicyProfile.LocalFull;
            if (policy.ActionSchema == Local45CombatActionContract.SchemaId)
                return MlAgentsPolicyProfile.LocalNavigationAssisted;
        }

        throw UnsupportedSchemaPair(policy);
    }

    private static NotSupportedException UnsupportedSchemaPair(PolicySpec policy)
    {
        return new NotSupportedException(
            "ML-Agents installer does not support observation/action schema pair '"
            + policy.ObservationSchema + "' / '" + policy.ActionSchema + "'.");
    }

    private static int[] CreateUniformBranches(int count, int branchSize)
    {
        int[] branches = new int[count];
        for (int index = 0; index < branches.Length; index++)
            branches[index] = branchSize;
        return branches;
    }

    private static string NormalizeResourcePath(string path)
    {
        string value = path.Replace('\\', '/').Trim();
        const string prefix = "Assets/Resources/";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value.Substring(prefix.Length);
        int extension = value.LastIndexOf('.');
        if (extension > value.LastIndexOf('/'))
            value = value.Substring(0, extension);
        return value;
    }

    private static Camera CreateAgentCamera(PlayerBody body, Camera referenceCamera)
    {
        GameObject cameraObject = new GameObject("AgentCam_" + body.Identity.DisplayName);
        cameraObject.transform.SetParent(body.transform);
        cameraObject.transform.localPosition = new Vector3(0f, 0.7f, 0f);
        cameraObject.transform.localRotation = Quaternion.identity;
        Camera camera = cameraObject.AddComponent<Camera>();
        if (referenceCamera != null) camera.CopyFrom(referenceCamera);
        camera.enabled = false;
        return camera;
    }

    private static void MaybeInstallVisualSensor(PlayerBody body, Camera camera)
    {
        if (Environment.GetEnvironmentVariable("ENABLE_VISUAL_OBS") != "1") return;
        camera.enabled = true;
        Unity.MLAgents.Sensors.CameraSensorComponent sensor =
            body.gameObject.AddComponent<Unity.MLAgents.Sensors.CameraSensorComponent>();
        sensor.Camera = camera;
        sensor.Width = 160;
        sensor.Height = 120;
        sensor.SensorName = "GambitVisual";
        sensor.Grayscale = false;
        sensor.CompressionType = Unity.MLAgents.Sensors.SensorCompressionType.None;
    }

    private static void Set(PlayerBody body, MonoBehaviour controller)
    {
        body.SetController(controller);
    }
}

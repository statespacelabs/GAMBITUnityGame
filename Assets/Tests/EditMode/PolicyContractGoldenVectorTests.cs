using System.Collections.Generic;
using NUnit.Framework;
using Unity.Barracuda;
using UnityEngine;

public sealed class PolicyContractGoldenVectorTests
{
    [Test]
    public void Local45RecordedFrameMatchesApprovedGoldenVector()
    {
        LocalObservationFrame frame = new LocalObservationFrame
        {
            TargetLocal = new Vector3(1f, 2f, 3f),
            SelfVelocityLocal = new Vector3(4f, 5f, 6f),
            SelfAccelerationLocal = new Vector3(7f, 8f, 9f),
            DistanceToOpponent = 10f,
            SinPitchError = 0.1f,
            CosPitchError = 0.2f,
            SinYawError = 0.3f,
            CosYawError = 0.4f,
            ViewVelocity = new Vector3(11f, 12f, 13f),
            ViewAcceleration = new Vector3(14f, 15f, 16f),
            YawErrorDegrees = 17f,
            PitchErrorDegrees = 18f,
            AimErrorDegrees = 19f,
            ShootCommand = true,
            OpponentHealthFraction = 0.25f,
            TimeSinceAnyAction = 0.1f,
            TimeSinceShot = 0.2f,
            TimeSinceReload = 0.3f,
            TimeSinceTargetedHit = 0.4f,
            ShotFiredThisStep = true
        };

        float[] actual = new float[45];
        LocalObservationEncoder.Encode(frame, actual);
        float[] expected = new float[45];
        expected[0] = 1f; expected[1] = 2f; expected[2] = 3f;
        expected[3] = 4f; expected[4] = 5f; expected[5] = 6f;
        expected[6] = 7f; expected[7] = 8f; expected[8] = 9f;
        expected[9] = 10f;
        expected[10] = 0.1f; expected[11] = 0.2f;
        expected[12] = 0.3f; expected[13] = 0.4f;
        expected[14] = 11f; expected[15] = 12f; expected[16] = 13f;
        expected[17] = 14f; expected[18] = 15f; expected[19] = 16f;
        expected[20] = 17f; expected[21] = 18f; expected[22] = 19f;
        expected[23] = 1f;
        expected[37] = 1f; expected[38] = 1f; expected[39] = 0.25f;
        expected[40] = 0.1f; expected[41] = 0.2f;
        expected[42] = 0.3f; expected[43] = 0.4f; expected[44] = 1f;

        CollectionAssert.AreEqual(expected, actual);
    }

    [Test]
    public void Actor231MissingBodyFallbackMatchesApprovedZeroVector()
    {
        GameObject root = new GameObject("Actor231GoldenVector");
        try
        {
            MapIndependentTelemetry telemetry = root.AddComponent<MapIndependentTelemetry>();
            float[] actual = new float[ActorObservationContract.Size];
            for (int index = 0; index < actual.Length; index++) actual[index] = 99f;
            telemetry.BuildObservation(actual, false);
            CollectionAssert.AreEqual(new float[231], actual);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void BundledOnnxModelsPassShapeAndZeroInputParitySmoke()
    {
        NNModel navigatorAsset = Resources.Load<NNModel>("MLModels/navigator");
        NNModel combatAsset = Resources.Load<NNModel>("MLModels/combat");
        Assert.That(navigatorAsset, Is.Not.Null);
        Assert.That(combatAsset, Is.Not.Null);
        Model navigatorModel = ModelLoader.Load(navigatorAsset);
        Model combatModel = ModelLoader.Load(combatAsset);
        OnnxModelContractValidator.Validate(navigatorModel, combatModel);

        IWorker navigator = WorkerFactory.CreateWorker(WorkerFactory.Type.CSharpRef, navigatorModel);
        IWorker combat = WorkerFactory.CreateWorker(WorkerFactory.Type.CSharpRef, combatModel);
        try
        {
            using (Tensor actor = new Tensor(1, 1, ActorObservationContract.Size, 1))
            using (Tensor hidden = new Tensor(1, 1, OnnxModelContractValidator.NavigatorHiddenSize, 1))
            {
                navigator.Execute(new Dictionary<string, Tensor>
                {
                    { "actor_observation", actor }, { "hidden_state", hidden }
                });
                OnnxModelContractValidator.ValidateRuntimeOutput(
                    navigator.PeekOutput("action_mean").ToReadOnlyArray(),
                    OnnxModelContractValidator.NavigatorActionSize,
                    "navigator.action_mean");
                OnnxModelContractValidator.ValidateRuntimeOutput(
                    navigator.PeekOutput("next_hidden_state").ToReadOnlyArray(),
                    OnnxModelContractValidator.NavigatorHiddenSize,
                    "navigator.next_hidden_state");
            }

            using (Tensor local = new Tensor(1, 1, LocalObservationContract.Size, 1))
            using (Tensor hidden = new Tensor(1, 1, OnnxModelContractValidator.CombatHiddenSize, 1))
            {
                combat.Execute(new Dictionary<string, Tensor>
                {
                    { "obs_norm", local }, { "hidden", hidden }
                });
                OnnxModelContractValidator.ValidateRuntimeOutput(
                    combat.PeekOutput("action").ToReadOnlyArray(),
                    PolicyActionContract.Size,
                    "combat.action");
                OnnxModelContractValidator.ValidateRuntimeOutput(
                    combat.PeekOutput("next_hidden").ToReadOnlyArray(),
                    OnnxModelContractValidator.CombatHiddenSize,
                    "combat.next_hidden");
            }
        }
        finally
        {
            navigator.Dispose();
            combat.Dispose();
        }
    }
}

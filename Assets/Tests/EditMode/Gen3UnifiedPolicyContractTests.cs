using System;
using NUnit.Framework;
using UnityEngine;

public sealed class Gen3UnifiedPolicyContractTests
{
    [Test]
    public void UnifiedObservationRetainsAuditedFieldsAndSixKinematics()
    {
        Assert.That(
            UnifiedFairObservationV2Contract.Actor231IncludedIndices.Length,
            Is.EqualTo(211));
        CollectionAssert.DoesNotContain(
            UnifiedFairObservationV2Contract.Actor231IncludedIndices,
            ActorObservationContract.PreviousAction);
        CollectionAssert.DoesNotContain(
            UnifiedFairObservationV2Contract.Actor231IncludedIndices,
            ActorObservationContract.FreeSpaceSummary);
        CollectionAssert.DoesNotContain(
            UnifiedFairObservationV2Contract.Actor231IncludedIndices,
            ActorObservationContract.TacticalMode);

        float[] actor = new float[ActorObservationContract.Size];
        for (int index = 0; index < actor.Length; index++)
            actor[index] = index + 0.25f;
        float[] output = new float[UnifiedFairObservationV2Contract.Size];
        UnifiedFairObservationV2Contract.Encode(
            actor,
            new Vector3(0.1f, -0.2f, 0.3f),
            new Vector3(-0.4f, 0.5f, -0.6f),
            output);

        for (int index = 0; index < 211; index++)
            Assert.That(output[index], Is.EqualTo(
                actor[UnifiedFairObservationV2Contract.Actor231IncludedIndices[index]]));
        CollectionAssert.AreEqual(
            new[] { 0.1f, -0.2f, 0.3f, -0.4f, 0.5f, -0.6f },
            new ArraySegment<float>(output, 211, 6));
    }

    [Test]
    public void RawTokenCarriesPreviousAppliedActionEventsAndPitch()
    {
        float[] observation = new float[UnifiedFairObservationV2Contract.Size];
        observation[0] = 0.125f;
        PlayerCommand previous = new PlayerCommand
        {
            MoveX = -0.1f,
            MoveZ = 0.2f,
            Turn = -0.3f,
            LookPitch = 0.4f,
            Shoot = true,
            Jump = true
        };
        float[] token = new float[UnifiedFairObservationV2Contract.RawTokenSize];
        UnifiedFairObservationV2Contract.BuildRawToken(
            observation,
            previous,
            new[] { 2, 1, 1, 0 },
            1f / 30f,
            true,
            true,
            token);

        Assert.That(token.Length, Is.EqualTo(232));
        CollectionAssert.AreEqual(
            Gen3UnifiedAction.ToArray(previous),
            new ArraySegment<float>(token, 217, 8));
        Assert.That(token[220], Is.EqualTo(0.4f));
        CollectionAssert.AreEqual(
            new[] { 2f, 1f, 1f, 0f },
            new ArraySegment<float>(token, 225, 4));
    }

    [Test]
    public void UnifiedActionOwnsAllEightOutputsAndRejectsNonFiniteValues()
    {
        float[] action = { -1f, 1f, 0.25f, 1f, 1f, 0f, 1f, 0f };
        PlayerCommand command = Gen3UnifiedAction.FromArray(action);
        Assert.That(command.MoveX, Is.EqualTo(-1f));
        Assert.That(command.LookPitch, Is.EqualTo(1f));
        Assert.That(command.Shoot, Is.True);
        Assert.That(command.Jump, Is.True);
        CollectionAssert.AreEqual(action, Gen3UnifiedAction.ToArray(command));

        action[3] = float.NaN;
        Assert.Throws<InvalidOperationException>(() => Gen3UnifiedAction.FromArray(action));
    }

    [Test]
    public void ContextRingLeftPadsPreservesOrderAndResets()
    {
        Gen3UnifiedRingBuffer ring = new Gen3UnifiedRingBuffer(4, 2);
        ring.Append(new[] { 1f, 10f });
        ring.Append(new[] { 2f, 20f });
        float[] tokens = new float[8];
        float[] mask = new float[4];
        ring.CopyLeftPadded(tokens, mask);
        CollectionAssert.AreEqual(
            new[] { 0f, 0f, 0f, 0f, 1f, 10f, 2f, 20f }, tokens);
        CollectionAssert.AreEqual(new[] { 0f, 0f, 1f, 1f }, mask);

        ring.Reset();
        ring.CopyLeftPadded(tokens, mask);
        CollectionAssert.AreEqual(new float[8], tokens);
        CollectionAssert.AreEqual(new float[4], mask);
        Assert.That(ring.Count, Is.Zero);
    }
}

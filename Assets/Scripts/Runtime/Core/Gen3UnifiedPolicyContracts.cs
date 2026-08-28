using System;
using UnityEngine;

/// <summary>
/// Frozen fair-observation contract imported from the isolated Gen3 V2
/// transfer workspace. One policy owns all final actions.
/// </summary>
public static class UnifiedFairObservationV2Contract
{
    /// <summary>Single-frame fair observation reconstructed internally by unified ONNX inference.</summary>
    public const string SchemaId = "unified_fair_obs_v2_v001";

    /// <summary>Raw history token exposed as the ML-Agents training sensor.</summary>
    public const string TokenSchemaId = "gen3_champion_v2_token_v001";

    public const string ActionSchemaId = "gen3_champion_v2_action8_v001";
    public const int ActionContinuousCount = PolicyActionContract.ContinuousCount;
    public const int ActionBinaryCount = PolicyActionContract.BinaryActionCount;
    public const int ActionBinaryBranchSize = PolicyActionContract.BinaryBranchSize;
    public const string RewardSchemaId = "ascent_event_reward_v1";
    public const int Size = 217;
    public const int ActorIncludedSize = 211;
    public const int ExtraKinematics = ActorIncludedSize;
    public const int PreviousActionSize = 8;
    public const int PreviousEventSize = 4;
    public const int RawTokenSize = Size + PreviousActionSize + PreviousEventSize + 3;
    public const int PrimaryContextLength = 32;
    public const float LinearAccelerationScale = 200f;
    public const float AngularAccelerationScale = 2000f;

    public static readonly int[] Actor231IncludedIndices = BuildIncludedIndices();

    private static int[] BuildIncludedIndices()
    {
        int[] output = new int[ActorIncludedSize];
        int cursor = 0;
        for (int index = 0; index < ActorObservationContract.Size; index++)
        {
            bool previousAction = index >= ActorObservationContract.PreviousAction
                && index < ActorObservationContract.PreviousAction + PolicyActionContract.Size;
            bool freeSpace = index >= ActorObservationContract.FreeSpaceSummary
                && index < ActorObservationContract.FreeSpaceSummary
                    + ActorObservationContract.FreeSpaceSectorCount;
            bool tacticalMode = index >= ActorObservationContract.TacticalMode
                && index < ActorObservationContract.TacticalMode + 4;
            if (previousAction || freeSpace || tacticalMode)
                continue;
            if (cursor >= output.Length)
                throw new InvalidOperationException("Unified observation actor index overflow");
            output[cursor++] = index;
        }
        if (cursor != ActorIncludedSize)
            throw new InvalidOperationException("Unified observation expected 211 retained actor fields");
        return output;
    }

    public static void Encode(
        float[] actor231,
        Vector3 linearAccelerationLocalNormalized,
        Vector3 angularAccelerationNormalized,
        float[] output)
    {
        ActorObservationContract.ValidateBuffer(actor231, nameof(actor231));
        ValidateObservation(output, nameof(output));
        for (int index = 0; index < Actor231IncludedIndices.Length; index++)
            output[index] = actor231[Actor231IncludedIndices[index]];
        WriteVector(output, ExtraKinematics, ClampFinite(linearAccelerationLocalNormalized));
        WriteVector(output, ExtraKinematics + 3, ClampFinite(angularAccelerationNormalized));
        AssertFinite(output, SchemaId);
    }

    public static void BuildRawToken(
        float[] observation,
        PlayerCommand previousAppliedAction,
        int[] previousEventCounts,
        float deltaTime,
        bool schemaValid,
        bool paddingValid,
        float[] output)
    {
        ValidateObservation(observation, nameof(observation));
        if (previousEventCounts == null || previousEventCounts.Length != PreviousEventSize)
            throw new ArgumentException(
                "Previous event counts must be [shot,hit,miss,kill]", nameof(previousEventCounts));
        if (output == null || output.Length != RawTokenSize)
            throw new ArgumentException("Gen3 unified raw token must have 232 values", nameof(output));
        Array.Copy(observation, output, Size);
        float[] action = Gen3UnifiedAction.ToArray(previousAppliedAction);
        Array.Copy(action, 0, output, Size, action.Length);
        int cursor = Size + PreviousActionSize;
        for (int index = 0; index < PreviousEventSize; index++)
            output[cursor++] = Mathf.Max(0, previousEventCounts[index]);
        output[cursor++] = Mathf.Max(0f, deltaTime);
        output[cursor++] = schemaValid ? 1f : 0f;
        output[cursor] = paddingValid ? 1f : 0f;
        AssertFinite(output, TokenSchemaId);
    }

    public static void ValidateObservation(float[] values, string parameterName)
    {
        if (values == null || values.Length != Size)
            throw new ArgumentException(SchemaId + " requires exactly " + Size + " values", parameterName);
    }

    public static void AssertFinite(float[] values, string label)
    {
        for (int index = 0; index < values.Length; index++)
            if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
                throw new InvalidOperationException(label
                    + " produced a non-finite value at index " + index);
    }

    private static void WriteVector(float[] output, int offset, Vector3 value)
    {
        output[offset] = value.x;
        output[offset + 1] = value.y;
        output[offset + 2] = value.z;
    }

    private static Vector3 ClampFinite(Vector3 value)
    {
        if (float.IsNaN(value.x) || float.IsInfinity(value.x)
            || float.IsNaN(value.y) || float.IsInfinity(value.y)
            || float.IsNaN(value.z) || float.IsInfinity(value.z))
            throw new InvalidOperationException(SchemaId + " received non-finite kinematics");
        return new Vector3(
            Mathf.Clamp(value.x, -1f, 1f),
            Mathf.Clamp(value.y, -1f, 1f),
            Mathf.Clamp(value.z, -1f, 1f));
    }
}

/// <summary>Direct eight-action conversion with pitch preserved.</summary>
public static class Gen3UnifiedAction
{
    public static PlayerCommand FromArray(float[] action)
    {
        PolicyActionContract.ValidateBuffer(action, nameof(action));
        UnifiedFairObservationV2Contract.AssertFinite(
            action, UnifiedFairObservationV2Contract.ActionSchemaId);
        return new PlayerCommand
        {
            MoveX = Mathf.Clamp(action[PolicyActionContract.MoveX], -1f, 1f),
            MoveZ = Mathf.Clamp(action[PolicyActionContract.MoveZ], -1f, 1f),
            Turn = Mathf.Clamp(action[PolicyActionContract.Turn], -1f, 1f),
            LookPitch = Mathf.Clamp(action[PolicyActionContract.LookPitch], -1f, 1f),
            Shoot = action[PolicyActionContract.Shoot] > PolicyActionContract.BinaryThreshold,
            Reload = action[PolicyActionContract.Reload] > PolicyActionContract.BinaryThreshold,
            Jump = action[PolicyActionContract.Jump] > PolicyActionContract.BinaryThreshold,
            Crouch = action[PolicyActionContract.Crouch] > PolicyActionContract.BinaryThreshold
        };
    }

    public static float[] ToArray(PlayerCommand command)
    {
        return new[]
        {
            Mathf.Clamp(command.MoveX, -1f, 1f),
            Mathf.Clamp(command.MoveZ, -1f, 1f),
            Mathf.Clamp(command.Turn, -1f, 1f),
            Mathf.Clamp(command.LookPitch, -1f, 1f),
            command.Shoot ? 1f : 0f,
            command.Reload ? 1f : 0f,
            command.Jump ? 1f : 0f,
            command.Crouch ? 1f : 0f
        };
    }
}

/// <summary>Fixed-size causal history with left padding and a validity mask.</summary>
public sealed class Gen3UnifiedRingBuffer
{
    private readonly int capacity;
    private readonly int width;
    private readonly float[][] rows;
    private int count;
    private int next;

    public int Count => count;

    public Gen3UnifiedRingBuffer(int configuredCapacity, int configuredWidth)
    {
        if (configuredCapacity <= 0 || configuredWidth <= 0)
            throw new ArgumentOutOfRangeException("Ring-buffer dimensions must be positive");
        capacity = configuredCapacity;
        width = configuredWidth;
        rows = new float[capacity][];
        for (int index = 0; index < capacity; index++)
            rows[index] = new float[width];
    }

    public void Reset()
    {
        for (int index = 0; index < capacity; index++)
            Array.Clear(rows[index], 0, width);
        count = 0;
        next = 0;
    }

    public void Append(float[] token)
    {
        if (token == null || token.Length != width)
            throw new ArgumentException("Ring-buffer token width mismatch", nameof(token));
        UnifiedFairObservationV2Contract.AssertFinite(
            token, UnifiedFairObservationV2Contract.TokenSchemaId);
        Array.Copy(token, rows[next], width);
        next = (next + 1) % capacity;
        count = Mathf.Min(count + 1, capacity);
    }

    public void CopyLeftPadded(float[] tokens, float[] paddingMask)
    {
        if (tokens == null || tokens.Length != capacity * width)
            throw new ArgumentException("Flat token output has wrong size", nameof(tokens));
        if (paddingMask == null || paddingMask.Length != capacity)
            throw new ArgumentException("Padding mask has wrong size", nameof(paddingMask));
        Array.Clear(tokens, 0, tokens.Length);
        Array.Clear(paddingMask, 0, paddingMask.Length);
        int destinationStart = capacity - count;
        int oldest = (next - count + capacity) % capacity;
        for (int row = 0; row < count; row++)
        {
            int source = (oldest + row) % capacity;
            Array.Copy(rows[source], 0, tokens, (destinationStart + row) * width, width);
            paddingMask[destinationStart + row] = 1f;
        }
    }
}

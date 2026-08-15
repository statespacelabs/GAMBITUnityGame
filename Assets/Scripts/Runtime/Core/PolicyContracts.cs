using System;
using UnityEngine;

/// <summary>Frozen phase3v2_c_local45 observation contract used by the bundled combat model.</summary>
public static class LocalObservationContract
{
    public const string SchemaId = "phase3v2_c_local45";
    public const int Size = 45;

    public const int TargetLocal = 0;
    public const int SelfVelocityLocal = 3;
    public const int SelfAccelerationLocal = 6;
    public const int DistanceToOpponent = 9;

    public const int SinPitchError = 10;
    public const int CosPitchError = 11;
    public const int SinYawError = 12;
    public const int CosYawError = 13;
    public const int ViewVelocity = 14;
    public const int ViewAcceleration = 17;
    public const int YawErrorDegrees = 20;
    public const int PitchErrorDegrees = 21;
    public const int AimErrorDegrees = 22;

    public const int RhythmActionCounts = 23;
    public const int RhythmActionCategoryCount = 14;
    public const int ShootActionCategory = 0;
    public const int RhythmActionCount = 37;
    public const int RhythmHasAnyAction = 38;
    public const int OpponentHealthFraction = 39;
    public const int TimeSinceAnyAction = 40;
    public const int TimeSinceShot = 41;
    public const int TimeSinceReload = 42;
    public const int TimeSinceTargetedHit = 43;
    public const int ShotFiredThisStep = 44;

    public const float TimingWindowSeconds = 5f;
    public const float DefaultSimulationDeltaSeconds = 1f / 50f;

    public static void ValidateBuffer(float[] values, string parameterName)
    {
        if (values == null || values.Length != Size)
            throw new ArgumentException(SchemaId + " requires exactly " + Size + " values", parameterName);
    }
}

/// <summary>Frozen phase5_actor_obs_v001 observation contract used by the navigator model.</summary>
public static class ActorObservationContract
{
    public const string SchemaId = "phase5_actor_obs_v001";
    public const int Size = 231;
    public const int SelfVelocity = 0;
    public const int SelfAngularVelocity = 3;
    public const int GroundedCrouched = 6;
    public const int HealthAmmoCooldown = 8;
    public const int RecentDamageCue = 12;
    public const int PreviousAction = 16;
    public const int ProgressStuckHistory = 24;
    public const int TorsoRays = 30;
    public const int FootHeadRays = 94;
    public const int FloorDropStepRays = 158;
    public const int CollisionNormal = 182;
    public const int FreeSpaceSummary = 185;
    public const int VisibleEnemyState = 193;
    public const int LastSeenMemory = 202;
    public const int LastHeardMemory = 207;
    public const int HuntCue = 211;
    public const int TacticalMode = 227;
    public const int HorizontalRayCount = 32;
    public const int FreeSpaceSectorCount = 8;
    public const int TorsoRaysPerSector = 4;
    public const int HuntBearingCount = 8;
    public const int HuntDistanceCount = 5;

    public static void ValidateBuffer(float[] values, string parameterName)
    {
        if (values == null || values.Length != Size)
            throw new ArgumentException(SchemaId + " requires exactly " + Size + " values", parameterName);
    }
}

/// <summary>Frozen eight-value output contract shared by bundled policy adapters.</summary>
public static class PolicyActionContract
{
    public const string SchemaId = "gambit_policy_action_v1";
    public const int Size = 8;
    public const int ContinuousCount = 4;
    public const int BinaryActionCount = 4;
    public const int BinaryBranchSize = 2;

    public const int MoveX = 0;
    public const int MoveZ = 1;
    public const int Turn = 2;
    public const int LookPitch = 3;
    public const int Shoot = 4;
    public const int Reload = 5;
    public const int Jump = 6;
    public const int Crouch = 7;
    public const float BinaryThreshold = 0.5f;

    public static void ValidateBuffer(float[] values, string parameterName)
    {
        if (values == null || values.Length != Size)
            throw new ArgumentException(SchemaId + " requires exactly " + Size + " values", parameterName);
    }
}

/// <summary>Pure input record for encoding one local45 observation.</summary>
public struct LocalObservationFrame
{
    public Vector3 TargetLocal;
    public Vector3 SelfVelocityLocal;
    public Vector3 SelfAccelerationLocal;
    public float DistanceToOpponent;
    public float SinPitchError;
    public float CosPitchError;
    public float SinYawError;
    public float CosYawError;
    public Vector3 ViewVelocity;
    public Vector3 ViewAcceleration;
    public float YawErrorDegrees;
    public float PitchErrorDegrees;
    public float AimErrorDegrees;
    public bool ShootCommand;
    public float OpponentHealthFraction;
    public float TimeSinceAnyAction;
    public float TimeSinceShot;
    public float TimeSinceReload;
    public float TimeSinceTargetedHit;
    public bool ShotFiredThisStep;
}

/// <summary>Deterministic encoder for the frozen local45 observation layout.</summary>
public static class LocalObservationEncoder
{
    public static void Encode(LocalObservationFrame frame, float[] output)
    {
        LocalObservationContract.ValidateBuffer(output, nameof(output));
        Array.Clear(output, 0, output.Length);

        WriteVector(output, LocalObservationContract.TargetLocal, frame.TargetLocal);
        WriteVector(output, LocalObservationContract.SelfVelocityLocal, frame.SelfVelocityLocal);
        WriteVector(output, LocalObservationContract.SelfAccelerationLocal, frame.SelfAccelerationLocal);
        output[LocalObservationContract.DistanceToOpponent] = frame.DistanceToOpponent;
        output[LocalObservationContract.SinPitchError] = frame.SinPitchError;
        output[LocalObservationContract.CosPitchError] = frame.CosPitchError;
        output[LocalObservationContract.SinYawError] = frame.SinYawError;
        output[LocalObservationContract.CosYawError] = frame.CosYawError;
        WriteVector(output, LocalObservationContract.ViewVelocity, frame.ViewVelocity);
        WriteVector(output, LocalObservationContract.ViewAcceleration, frame.ViewAcceleration);
        output[LocalObservationContract.YawErrorDegrees] = frame.YawErrorDegrees;
        output[LocalObservationContract.PitchErrorDegrees] = frame.PitchErrorDegrees;
        output[LocalObservationContract.AimErrorDegrees] = frame.AimErrorDegrees;

        // The released combat model was trained with only category 0 populated.
        // Categories 1-13 are frozen zero-valued compatibility slots.
        float shoot = frame.ShootCommand ? 1f : 0f;
        output[LocalObservationContract.RhythmActionCounts + LocalObservationContract.ShootActionCategory] = shoot;
        output[LocalObservationContract.RhythmActionCount] = shoot;
        output[LocalObservationContract.RhythmHasAnyAction] = shoot;
        output[LocalObservationContract.OpponentHealthFraction] = Mathf.Clamp01(frame.OpponentHealthFraction);
        output[LocalObservationContract.TimeSinceAnyAction] = Mathf.Clamp01(frame.TimeSinceAnyAction);
        output[LocalObservationContract.TimeSinceShot] = Mathf.Clamp01(frame.TimeSinceShot);
        output[LocalObservationContract.TimeSinceReload] = Mathf.Clamp01(frame.TimeSinceReload);
        output[LocalObservationContract.TimeSinceTargetedHit] = Mathf.Clamp01(frame.TimeSinceTargetedHit);
        output[LocalObservationContract.ShotFiredThisStep] = frame.ShotFiredThisStep ? 1f : 0f;

        AssertFinite(output);
    }

    private static void WriteVector(float[] output, int index, Vector3 value)
    {
        output[index] = value.x;
        output[index + 1] = value.y;
        output[index + 2] = value.z;
    }

    private static void AssertFinite(float[] values)
    {
        for (int index = 0; index < values.Length; index++)
            if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
                throw new InvalidOperationException(LocalObservationContract.SchemaId
                    + " produced a non-finite value at index " + index);
    }
}

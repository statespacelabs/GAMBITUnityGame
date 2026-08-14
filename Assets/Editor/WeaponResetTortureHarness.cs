#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>Exercises weapon and player reset invariants across hostile pre-reset states.</summary>
public static class WeaponResetTortureHarness
{
    private const int GenerationsPerScenario = 100;

    private enum Scenario
    {
        Idle,
        HoldingShoot,
        OnCooldown,
        Reloading,
        ZeroAmmo,
        DyingWhileShooting,
        DyingWhileReloading,
        SimultaneousDeath
    }

    private sealed class Rig
    {
        public GameObject Root;
        public MatchManager Match;
        public PlayerBody A;
        public PlayerBody B;
    }

    [MenuItem("GAMBIT/QA/Run Weapon Reset Stress Test", false, 200)]
    public static void Run()
    {
        try
        {
            RunInternal();
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogError("[WeaponResetTorture] FAIL: " + exception);
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
            throw;
        }
    }

    private static void RunInternal()
    {
        Dictionary<FireBlockedReason, int> blocked = new Dictionary<FireBlockedReason, int>();
        Rig rig = CreateRig();
        int resetChecks = 0;
        try
        {
            foreach (Scenario scenario in Enum.GetValues(typeof(Scenario)))
            {
                for (int generation = 0; generation < GenerationsPerScenario; generation++)
                {
                    ApplyPreResetState(rig, scenario);
                    rig.Match.ResetRound();
                    ValidatePostReset(rig, scenario, generation);
                    RequireFirstShot(rig.A, scenario, generation, blocked);
                    RequireFirstShot(rig.B, scenario, generation, blocked);
                    resetChecks += 2;
                }
            }
        }
        finally
        {
            if (rig.Root != null)
                UnityEngine.Object.DestroyImmediate(rig.Root);
        }

        RequireZero(blocked, FireBlockedReason.NO_OWNER);
        RequireZero(blocked, FireBlockedReason.WRONG_OWNER);
        RequireZero(blocked, FireBlockedReason.WEAPON_DISABLED);
        RequireZero(blocked, FireBlockedReason.UNKNOWN);
        Debug.Log($"[WeaponResetTorture] PASS scenarios={Enum.GetValues(typeof(Scenario)).Length} "
            + $"generations_per_scenario={GenerationsPerScenario} first_shot_checks={resetChecks}");
    }

    private static Rig CreateRig()
    {
        GameObject root = new GameObject("_WeaponResetTortureRig");
        MatchConfig config = ScriptableObject.CreateInstance<MatchConfig>();
        config.MaxHealth = 100;
        config.DamagePerHit = 1;
        config.GlobalCooldownSeconds = 3f;
        config.WeaponFireCooldownSeconds = 0.2f;
        config.KillsPerMatch = 0;
        config.ResetBothHealthAfterKill = true;
        config.ResetPositionsAfterKill = true;

        PlayerBody a = CreatePlayer(root.transform, "Torture_Player_A", 0,
            new Vector3(0f, 1f, 0f), Quaternion.identity, config);
        PlayerBody b = CreatePlayer(root.transform, "Torture_Player_B", 1,
            new Vector3(0f, 1f, 8f), Quaternion.Euler(0f, 180f, 0f), config);
        SpawnPoint spawnA = CreateSpawn(root.transform, "Spawn_A", 0, a.transform.position, a.transform.rotation);
        SpawnPoint spawnB = CreateSpawn(root.transform, "Spawn_B", 1, b.transform.position, b.transform.rotation);

        GameObject matchObject = new GameObject("_TortureMatchManager");
        matchObject.transform.SetParent(root.transform);
        MatchManager match = matchObject.AddComponent<MatchManager>();
        match.Config = config;
        match.PlayerA = a;
        match.PlayerB = b;
        match.SpawnPointA = spawnA;
        match.SpawnPointB = spawnB;
        a.SetMatchManager(match);
        b.SetMatchManager(match);
        match.ResetRound();
        return new Rig { Root = root, Match = match, A = a, B = b };
    }

    private static PlayerBody CreatePlayer(
        Transform root, string name, int index, Vector3 position, Quaternion rotation, MatchConfig config)
    {
        GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        player.name = name;
        player.transform.SetParent(root);
        player.transform.position = position;
        player.transform.rotation = rotation;
        CapsuleCollider defaultCollider = player.GetComponent<CapsuleCollider>();
        if (defaultCollider != null)
            UnityEngine.Object.DestroyImmediate(defaultCollider);

        CharacterController characterController = player.AddComponent<CharacterController>();
        characterController.height = 2f;
        characterController.radius = 0.5f;
        characterController.center = Vector3.zero;

        PlayerIdentity identity = player.AddComponent<PlayerIdentity>();
        identity.PlayerId = name;
        identity.DisplayName = name;
        identity.PlayerIndex = index;

        PlayerMotor motor = player.AddComponent<PlayerMotor>();
        PlayerWeapon weapon = player.AddComponent<PlayerWeapon>();
        PlayerHealth health = player.AddComponent<PlayerHealth>();
        PlayerBody body = player.AddComponent<PlayerBody>();
        InvokeAwake(motor);
        InvokeAwake(weapon);
        InvokeAwake(health);
        InvokeAwake(body);
        weapon.Configure(config);
        health.Configure(config);
        return body;
    }

    private static void InvokeAwake(MonoBehaviour behaviour)
    {
        MethodInfo awake = behaviour.GetType().GetMethod(
            "Awake", BindingFlags.Instance | BindingFlags.NonPublic);
        if (awake != null)
            awake.Invoke(behaviour, null);
    }

    private static SpawnPoint CreateSpawn(
        Transform root, string name, int index, Vector3 position, Quaternion rotation)
    {
        GameObject spawnObject = new GameObject(name);
        spawnObject.transform.SetParent(root);
        spawnObject.transform.position = position;
        spawnObject.transform.rotation = rotation;
        SpawnPoint spawn = spawnObject.AddComponent<SpawnPoint>();
        spawn.PlayerIndex = index;
        return spawn;
    }

    private static void ApplyPreResetState(Rig rig, Scenario scenario)
    {
        switch (scenario)
        {
            case Scenario.Idle:
                break;
            case Scenario.HoldingShoot:
            case Scenario.OnCooldown:
                rig.A.Weapon.TryFire(rig.A.Identity, true);
                rig.B.Weapon.TryFire(rig.B.Identity, true);
                break;
            case Scenario.Reloading:
            case Scenario.ZeroAmmo:
                rig.A.Weapon.Reload();
                rig.B.Weapon.Reload();
                break;
            case Scenario.DyingWhileShooting:
                rig.B.Weapon.TryFire(rig.B.Identity, true);
                rig.A.Health.TakeDamage(rig.A.Health.MaxHealth, rig.B.Identity);
                break;
            case Scenario.DyingWhileReloading:
                rig.A.Weapon.Reload();
                rig.A.Health.TakeDamage(rig.A.Health.MaxHealth, rig.B.Identity);
                break;
            case Scenario.SimultaneousDeath:
                rig.A.Health.CurrentHealth = 0;
                rig.B.Health.CurrentHealth = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void ValidatePostReset(Rig rig, Scenario scenario, int generation)
    {
        Require(rig.Match.CombatReady, scenario, generation, "combatReady false");
        Require(!rig.Match.IsInCooldown, scenario, generation, "match cooldown not cleared");
        Require(!rig.Match.IsMatchOver, scenario, generation, "matchOver not cleared");
        ValidatePlayer(rig.A, scenario, generation);
        ValidatePlayer(rig.B, scenario, generation);
    }

    private static void ValidatePlayer(PlayerBody player, Scenario scenario, int generation)
    {
        string prefix = player.Identity.DisplayName;
        Require(player.Weapon.OwnerIdentity == player.Identity, scenario, generation, prefix + " owner mismatch");
        Require(player.Weapon.gameObject.activeInHierarchy, scenario, generation, prefix + " weapon inactive");
        Require(player.Weapon.enabled, scenario, generation, prefix + " weapon disabled");
        Require(player.Health.CurrentHealth == player.Health.MaxHealth, scenario, generation, prefix + " health not reset");
        Require(player.Weapon.CurrentAmmo == player.Weapon.MaxAmmo, scenario, generation, prefix + " ammo not reset");
        Require(!player.Weapon.IsReloading, scenario, generation, prefix + " still reloading");
        Require(player.Weapon.CooldownRemaining <= 0f, scenario, generation, prefix + " cooldown not reset");
    }

    private static void RequireFirstShot(
        PlayerBody player, Scenario scenario, int generation, Dictionary<FireBlockedReason, int> blocked)
    {
        FireAttemptResult result = player.Weapon.TryFire(player.Identity, true);
        if (result.Fired)
            return;
        blocked.TryGetValue(result.Reason, out int count);
        blocked[result.Reason] = count + 1;
        throw new InvalidOperationException(
            $"first post-reset shot failed scenario={scenario} generation={generation} "
            + $"agent={player.Identity.DisplayName} reason={result.Reason} owner={player.Weapon.OwnerId} "
            + $"ammo={player.Weapon.CurrentAmmo} reload={player.Weapon.IsReloading} "
            + $"cooldown={player.Weapon.CooldownRemaining}");
    }

    private static void RequireZero(Dictionary<FireBlockedReason, int> blocked, FireBlockedReason reason)
    {
        blocked.TryGetValue(reason, out int count);
        if (count != 0)
            throw new InvalidOperationException($"blocked reason {reason} occurred {count} times");
    }

    private static void Require(bool condition, Scenario scenario, int generation, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"scenario={scenario} generation={generation}: {message}");
    }
}
#endif

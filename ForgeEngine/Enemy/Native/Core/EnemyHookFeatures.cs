using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEnemy.Profile;

namespace ForgeEnemy.Native;

/// <summary>One function: the callback classes it owns, the bindings that cannot work without them, and what the
/// player loses when it is withdrawn.</summary>
internal sealed record EnemyHookFeature(EnemyFeature Id, string Name, Type[] Hooks, string[] Bindings, string Lost) : ForgeEngine.Framework.HookFeature(Name, Hooks)
{
    /// <summary>Functions this one reads through; a withdrawn prerequisite withdraws this function too.</summary>
    internal EnemyFeature[] Requires { get; init; } = Array.Empty<EnemyFeature>();

    /// <summary>Why this function is withdrawn before any declaration, by evidence. Its classes are never declared.</summary>
    internal string? Evidence { get; init; }
}

/// <summary>The one table of this package's native callback classes, grouped by the function each supports. The
/// Runtime is the only installer; the table is also the pin <c>HookInstall</c> holds every callback class against.</summary>
internal static class EnemyHookFeatures
{
    private static string B(string suffix) => ModuleDefinition.ProviderId + ".binding." + suffix;

    internal static IReadOnlyList<EnemyHookFeature> Table { get; } = Checked(new[]
    {
        new EnemyHookFeature(EnemyFeature.Tracking, "tracking", Array.Empty<Type>(),
            Array.Empty<string>(), "every enemy function: without the list of enemy lives in the level no plan can name an enemy"),
        new EnemyHookFeature(EnemyFeature.SpawnFact, "spawn-fact", Array.Empty<Type>(),
            new[] { B("spawned") }, "the enemy-spawned trigger and the spawn origin it carries"),
        new EnemyHookFeature(EnemyFeature.Damage, "damage", Array.Empty<Type>(),
            new[] { B("damage_applied"), B("health_changed"), B("killed"), B("staggered"), B("damage_rule") },
            "damage-applied, health-changed, killed and staggered triggers, damage rules and attachment crack stages")
            { Requires = new[] { EnemyFeature.DamageKind } },
        new EnemyHookFeature(EnemyFeature.DamageKind, "damage-kind",
            new[] { typeof(EnemyReceivePushKind), typeof(EnemyReceiveFireKind) },
            Array.Empty<string>(), "the damage kind of every hit: damage facts and damage rules are withdrawn rather than mislabel it"),
        new EnemyHookFeature(EnemyFeature.Death, "death", Array.Empty<Type>(),
            new[] { B("death_started") }, "the enemy-death trigger"),
        new EnemyHookFeature(EnemyFeature.LimbBreak, "limb-break", Array.Empty<Type>(),
            new[] { B("limb_broken") }, "the limb-broken trigger"),
        new EnemyHookFeature(EnemyFeature.LimbProfile, "limb-profile",
            new[] { typeof(EnemyPrefabLimbs), typeof(EnemyLimbModifiers), typeof(EnemyLimbCustomModifiers), typeof(EnemyLimbBackBonus) },
            Array.Empty<string>(), "authored limb health, weak-spot and armor multipliers and the back-damage multiplier"),
        new EnemyHookFeature(EnemyFeature.ModelLimbs, "model-limbs", new[] { typeof(EnemyLimbDestructionBlocked) },
            Array.Empty<string>(), "unbreakable limbs and attachment parts that hold their shape"),
        new EnemyHookFeature(EnemyFeature.Behavior, "behavior",
            new[] { typeof(EnemyTargetSet), typeof(EnemyCourseNodeSet) },
            new[] { B("awakened"), B("target_acquired"), B("target_lost"), B("zone_changed") },
            "the awakened, target-acquired, target-lost and zone-changed triggers and the tag expiry edge")
            { Requires = new[] { EnemyFeature.StateEntries } },
        new EnemyHookFeature(EnemyFeature.ScoutDetection, "scout-detection", new[] { typeof(EnemyScoutDetected) },
            new[] { B("scout_detection") }, "the scout-detected-a-target trigger"),
        new EnemyHookFeature(EnemyFeature.ScoutAbilities, "scout-abilities",
            new[]
            {
                typeof(EnemyFeelerLayoutHook), typeof(EnemyFeelerTendrilHook), typeof(EnemyScreamUpdateHook),
                typeof(EnemyScreamReceiveHook)
            },
            new[] { B("scout_scream") }, "the scout-scream trigger and authored scout feeler and scream fields"),
        new EnemyHookFeature(EnemyFeature.ScannerAbilities, "scanner-abilities",
            new[]
            {
                typeof(EnemyScannerAntennaHook), typeof(EnemyScannerPathMoveHook), typeof(EnemyScannerPathMoveFlyerHook),
                typeof(EnemyScannerHibernateEnterHook), typeof(EnemyScannerHibernateExitHook), typeof(EnemyScannerWakeupHook)
            },
            Array.Empty<string>(), "authored scanner colours for each scanner state"),
        new EnemyHookFeature(EnemyFeature.Tag, "tag", new[] { typeof(EnemyNodeTagged) },
            new[] { B("tagged") }, "the enemy-tagged trigger"),
        new EnemyHookFeature(EnemyFeature.Glue, "glue", new[] { typeof(EnemyNodeGlued) },
            new[] { B("glued") }, "the enemy-glued trigger"),
        new EnemyHookFeature(EnemyFeature.AbilityUsed, "ability-used", new[] { typeof(EnemyNodeAbilityUsed) },
            new[] { B("ability_used") }, "the enemy-ability-used trigger"),
        new EnemyHookFeature(EnemyFeature.MeleeAttack, "melee-attack", new[] { typeof(EnemyMeleeAttackHook) },
            new[] { B("melee_attack") }, "the striker-melee-attack trigger"),
        new EnemyHookFeature(EnemyFeature.Scream, "scream", new[] { typeof(EnemyScreamFlyerHook) },
            new[] { B("screamed") }, "the enemy-screamed trigger"),
        new EnemyHookFeature(EnemyFeature.SleeperDetection, "sleeper-detection",
            new[] { typeof(EnemySleeperDetectionStartHook), typeof(EnemySleeperDetectionEndHook) },
            new[] { B("sleeper_detecting") }, "the sleeper-detecting trigger"),
        new EnemyHookFeature(EnemyFeature.Projectile, "projectile", Array.Empty<Type>(),
            new[] { B("projectile_fired") }, "the projectile-fired trigger")
            { Requires = new[] { EnemyFeature.ProjectileWorld } },
        new EnemyHookFeature(EnemyFeature.Fog, "fog", new[] { typeof(EnemyFogReleasedHook) },
            new[] { B("fog_released") }, "the fog-released trigger and authored fog sphere fields"),
        new EnemyHookFeature(EnemyFeature.Birth, "birth", new[] { typeof(EnemyGaveBirthHook) },
            new[] { B("gave_birth") }, "the gave-birth trigger"),
        new EnemyHookFeature(EnemyFeature.PlayerRelease, "player-release",
            new[] { typeof(EnemySpitOutHook), typeof(EnemyTankGrabStopHook) },
            new[] { B("released_player") }, "the released-player trigger"),
        new EnemyHookFeature(EnemyFeature.Attack, "attack",
            new[]
            {
                typeof(EnemyShooterWindUpHook), typeof(EnemyShooterPerformHook), typeof(EnemyShooterFlyerWindUpHook),
                typeof(EnemyShooterFlyerPerformHook), typeof(EnemyStrikerWindUpHook), typeof(EnemyStrikerPerformHook),
                typeof(EnemyTankWindUpHook), typeof(EnemyTankPerformHook), typeof(EnemyTankMultiWindUpHook),
                typeof(EnemyTankMultiPerformHook)
            },
            new[] { B("attack_performed") }, "the attack-performed trigger"),
        new EnemyHookFeature(EnemyFeature.Noise, "noise",
            new[] { typeof(EnemyPatrollingNoiseHook) },
            new[] { B("heard_noise") }, "the heard-noise trigger for sleeping and patrolling enemies"),
        new EnemyHookFeature(EnemyFeature.Alarm, "alarm", new[] { typeof(EnemyAlarmChargeUpHook), typeof(EnemyAlarmStartHook) },
            new[] { B("alarm_raised") }, "the alarm-raised trigger"),
        new EnemyHookFeature(EnemyFeature.Grab, "grab", new[] { typeof(EnemyPouncerConsumeHook), typeof(EnemyTankTentacleGrabHook) },
            new[] { B("grabbed_player") }, "the grabbed-player trigger"),
        new EnemyHookFeature(EnemyFeature.NativeState, "native-state",
            Array.Empty<Type>(),
            new[]
            {
                B("trigger.enemy.combat_entered"), B("trigger.enemy.combat_left"), B("trigger.enemy.knocked_down"),
                B("trigger.enemy.knockdown_recovering"), B("trigger.enemy.phase_changed")
            },
            "the combat-entered, combat-left, knocked-down, recovering and phase-changed triggers")
            { Requires = new[] { EnemyFeature.StateEntries } },
        new EnemyHookFeature(EnemyFeature.CapCost, "cap-cost", Array.Empty<Type>(),
            Array.Empty<string>(), "authored on-screen cap share"),
        new EnemyHookFeature(EnemyFeature.Wake, "wake",
            new[]
            {
                typeof(EnemyWakeHitreact), typeof(EnemyWakeHitreactFlyer),
                typeof(EnemyWakeHibernationDetection)
            },
            new[] { B("enemy.stationary"), B("query.enemy.stationary"), B("enemy.wake_lock"), B("query.enemy.wake_locked") },
            "the keep-enemy-still action and query (no walking, nothing else), the cannot-wake action and query, and authored keep-asleep-when-hit and gunfire-wakes rules")
            { Requires = new[] { EnemyFeature.StateEntries } },
        new EnemyHookFeature(EnemyFeature.WakeHearing, "wake-hearing", new[] { typeof(EnemyWakeNoiseWindow) },
            Array.Empty<string>(), "authored sneak-wake distance for sleeping enemies and the noise a cannot-wake sleeper ignores"),
        new EnemyHookFeature(EnemyFeature.Appearance, "appearance", new[] { typeof(EnemyAppearanceInitializing) },
            Array.Empty<string>(), "authored model parts, materials, attachments, part colours and glow colours"),
        new EnemyHookFeature(EnemyFeature.Animation, "animation", new[] { typeof(EnemyAnimHandleInitializedHook) },
            Array.Empty<string>(), "authored animation handle overrides"),
        new EnemyHookFeature(EnemyFeature.Look, "look",
            new[] { typeof(EnemyTagMarkerPlaced) },
            Array.Empty<string>(), "authored wake-up glow colours and tag markers"),
        new EnemyHookFeature(EnemyFeature.Waves, "waves",
            new[]
            {
                typeof(EnemyWaveSpawn), typeof(EnemyWaveGroupStep), typeof(EnemyWaveGroup), typeof(EnemyWaveMember),
                typeof(EnemyWaveGroupSpawning), typeof(EnemyWaveUpdate),
                typeof(EnemyWaveDespawn), typeof(EnemyGroupMaintenance)
            },
            new[] { B("wave_started"), B("wave_spawned"), B("wave_exhausted"), B("wave_cleared") },
            "the wave-started, wave-spawned, wave-exhausted and wave-cleared triggers"),
        new EnemyHookFeature(EnemyFeature.ChildSpawning, "child-spawning",
            new[]
            {
                typeof(EnemyChildBirthTrigger), typeof(EnemyChildPopulation), typeof(EnemyChildGroupLimit), typeof(EnemyChildWaveTrigger),
                typeof(EnemyChildWaveCost)
            },
            Array.Empty<string>(), "authored child counts, child wave groups and child wave cost")
            { Requires = new[] { EnemyFeature.Waves } },
        // One callback class per target for the entries three functions read: the behaviour machine's and the
        // locomotion's state changes (and the forced one), and the AI's detected-agent entry.
        new EnemyHookFeature(EnemyFeature.StateEntries, "state-entries",
            new[]
            {
                typeof(EnemyBehaviourStateEntry), typeof(EnemyLocomotionStateEntry), typeof(EnemyLocomotionForceEntry),
                typeof(EnemyDetectedAgentEntry)
            },
            Array.Empty<string>(),
            "the awakened, target, combat and knockdown triggers and the keep-enemy-still and wake rules"),
        new EnemyHookFeature(EnemyFeature.ProjectileWorld, "projectile-world",
            new[]
            {
                typeof(EnemyProjectileAimedShotFrameHook), typeof(EnemyProjectileChaosShotFrameHook), typeof(EnemyProjectileOwnerHook),
                typeof(EnemyProjectileWorldHitHook), typeof(EnemyProjectileEndHook)
            },
            new[] { B("projectile_hit_world"), B("projectile_expired") }, "the projectile-hit-world and projectile-expired triggers")
    });

    /// <summary>The table is indexed by the enum, so the two must list the functions in the same order.</summary>
    private static IReadOnlyList<EnemyHookFeature> Checked(EnemyHookFeature[] table)
    {
        for (var index = 0; index < table.Length; index++)
            if ((int)table[index].Id != index) throw new InvalidOperationException("enemy-hook-feature-order " + table[index].Id);
        if (table.Length != Enum.GetValues<EnemyFeature>().Length) throw new InvalidOperationException("enemy-hook-feature-count");
        return Array.AsReadOnly(table);
    }

    private static readonly IReadOnlyDictionary<Type, EnemyFeature> Owners = Table
        .SelectMany(feature => feature.Hooks.Select(hook => (hook, feature.Id)))
        .ToDictionary(pair => pair.hook, pair => pair.Id);

    /// <summary>The availability that follows from the functions a refused (or evidenced-absent) native class takes
    /// down directly: every function that reads through a withdrawn one is withdrawn with it (every function reads
    /// through tracking), until nothing more changes. Each withdrawn function carries the reason.</summary>
    internal static EnemyHookAvailability Resolve(IReadOnlyDictionary<EnemyFeature, string> direct)
    {
        var reasons = new Dictionary<EnemyFeature, string>(direct);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var feature in Table)
            {
                if (reasons.ContainsKey(feature.Id)) continue;
                var prerequisite = feature.Id == EnemyFeature.Tracking ? null
                    : new[] { EnemyFeature.Tracking }.Concat(feature.Requires).Cast<EnemyFeature?>()
                        .FirstOrDefault(required => reasons.ContainsKey(required!.Value));
                if (prerequisite is not { } missing) continue;
                reasons[feature.Id] = "requires " + Of(missing).Name + ", which is unavailable";
                changed = true;
            }
        }
        var bindings = reasons.Keys.SelectMany(feature => Of(feature).Bindings).Distinct(StringComparer.Ordinal).ToArray();
        return new EnemyHookAvailability(reasons, bindings);
    }

    /// <summary>One withdrawn function as a log line: what stopped, what the player loses and why.</summary>
    internal static string Describe(EnemyHookAvailability availability, EnemyFeature feature)
    {
        var item = Of(feature);
        return "feature=" + item.Name + " bindings=" + (item.Bindings.Length == 0 ? "none" : string.Join(",", item.Bindings.Select(Short)))
            + " lost=" + item.Lost + " reason=" + availability.Withdrawn[feature];
    }

    private static string Short(string binding) => binding[(binding.LastIndexOf(".binding.", StringComparison.Ordinal) + ".binding.".Length)..];
    internal static EnemyHookFeature Of(EnemyFeature feature) => Table[(int)feature];

    /// <summary>The function a callback class belongs to, or null when the class is in no function.</summary>
    internal static EnemyFeature? Of(Type callbacks) => Owners.TryGetValue(callbacks, out var feature) ? feature : null;

    /// <summary>The ability-field flag a hook reads, as the function that owns it.</summary>
    internal static EnemyFeature Of(EnemyAbilityHooks flag) => flag switch
    {
        EnemyAbilityHooks.Scanner => EnemyFeature.ScannerAbilities,
        EnemyAbilityHooks.FogSphere => EnemyFeature.Fog,
        EnemyAbilityHooks.ChildSpawning => EnemyFeature.ChildSpawning,
        _ => EnemyFeature.ScoutAbilities
    };
}

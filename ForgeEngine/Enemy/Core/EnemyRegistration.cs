using System;
using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The one ForgeEnemy provider registration: the game-independent half of it anyway — every row the
/// generated block table (<see cref="Blocks"/>) gives this provider, the base family's eleven binding rows with the
/// permission each of them needs, and every other family's bindings. The native module that answers the rows and
/// the release export that only declares them both read this, so a binding a contract adds reaches the game and
/// the manifest together.
///
/// Only the base family's bindings are spelled here rather than in a contract of their own: every one of them
/// binds a capability the runtime's own contract providers declare, and the native module is what publishes the
/// facts they carry.</summary>
internal static class EnemyRegistration
{
    internal const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>The base family's binding ids, named rather than spelled at each call site. They are the ids the
    /// bindings below declare, the ones the native module publishes its facts under, and the ones the harnesses pin
    /// their plans against.</summary>
    internal const string DamageBinding = ProviderId + ".binding.damage_applied";
    internal const string DamageActionBinding = ProviderId + ".binding.damage";
    internal const string HealBinding = ProviderId + ".binding.heal";
    internal const string HealthChangedBinding = ProviderId + ".binding.health_changed";
    internal const string DeathStartedBinding = ProviderId + ".binding.death_started";
    internal const string LimbBrokenBinding = ProviderId + ".binding.limb_broken";
    internal const string AwakenedBinding = ProviderId + ".binding.awakened";
    internal const string TargetAcquiredBinding = ProviderId + ".binding.target_acquired";
    internal const string TargetLostBinding = ProviderId + ".binding.target_lost";
    internal const string ScoutDetectionBinding = ProviderId + ".binding.scout_detection";
    internal const string ScoutScreamBinding = ProviderId + ".binding.scout_scream";

    internal static readonly HandlerShape DamagePorts = new HandlerShape()
        .Inputs("targets", "source", "instigator", "amount", "damage_kind", "limb", "precision_multi", "stagger_multi", "hit_origin")
        .Outputs("result");
    internal static readonly HandlerSlot DamageTargetsSlot = DamagePorts.InputSlot("targets");
    internal static readonly HandlerSlot DamageSourceSlot = DamagePorts.InputSlot("source");
    internal static readonly HandlerSlot DamageInstigatorSlot = DamagePorts.InputSlot("instigator");
    internal static readonly HandlerSlot DamageAmountSlot = DamagePorts.InputSlot("amount");
    internal static readonly HandlerSlot DamageKindSlot = DamagePorts.InputSlot("damage_kind");
    internal static readonly HandlerSlot DamageLimbSlot = DamagePorts.InputSlot("limb");
    internal static readonly HandlerSlot DamagePrecisionSlot = DamagePorts.InputSlot("precision_multi");
    internal static readonly HandlerSlot DamageStaggerSlot = DamagePorts.InputSlot("stagger_multi");
    internal static readonly HandlerSlot DamageHitOriginSlot = DamagePorts.InputSlot("hit_origin");

    internal static readonly HandlerShape HealPorts = new HandlerShape()
        .Inputs("targets", "source", "amount", "cap").Outputs("result").Parameters("overheal_policy", "cancel_regen");
    internal static readonly HandlerSlot HealSourceSlot = HealPorts.InputSlot("source");
    internal static readonly HandlerSlot HealAmountSlot = HealPorts.InputSlot("amount");
    internal static readonly HandlerSlot HealCapSlot = HealPorts.InputSlot("cap");
    internal static readonly HandlerSlot HealOverhealSlot = HealPorts.ParameterSlot("overheal_policy");
    internal static readonly HandlerSlot HealCancelRegenSlot = HealPorts.ParameterSlot("cancel_regen");

    internal static ProviderDeclaration Declaration() => new ProviderDeclaration(ProviderId, EnginePackage.Version, EnginePackage.Name)
        .DeclareBlocks()
        .Bind(
            new ProviderBinding(DamageBinding, Blocks.TriggerCombatDamageApplied.Id, "gtfo.enemy.damage_applied", "observe", new[] { "gtfo.enemy.health.read" }),
            new ProviderBinding(DamageActionBinding, Blocks.ActionCombatDamage.Id, "gtfo.enemy.damage", "execute", new[] { "gtfo.enemy.health.read", "gtfo.enemy.health.write" }, DamagePorts),
            new ProviderBinding(HealBinding, Blocks.ActionCombatHeal.Id, "gtfo.enemy.heal", "execute", new[] { "gtfo.enemy.health.write" }, HealPorts),
            new ProviderBinding(HealthChangedBinding, Blocks.TriggerCombatHealthChanged.Id, "gtfo.enemy.health_changed", "observe", new[] { "gtfo.enemy.health.read" }),
            new ProviderBinding(DeathStartedBinding, Blocks.TriggerEnemyDeathStarted.Id, "gtfo.enemy.death_started", "observe", new[] { "gtfo.enemy.lifecycle.read" }) { Waitable = true },
            new ProviderBinding(LimbBrokenBinding, Blocks.TriggerCombatLimbBroken.Id, "gtfo.enemy.limb_broken", "observe", new[] { "gtfo.enemy.limbs.read" }),
            new ProviderBinding(AwakenedBinding, Blocks.TriggerEnemyAwakened.Id, "gtfo.enemy.awakened", "observe", new[] { "gtfo.enemy.behavior.read" }) { Waitable = true },
            new ProviderBinding(TargetAcquiredBinding, Blocks.TriggerEnemyTargetAcquired.Id, "gtfo.enemy.target_acquired", "observe", new[] { "gtfo.enemy.targeting.read" }),
            new ProviderBinding(TargetLostBinding, Blocks.TriggerEnemyTargetLost.Id, "gtfo.enemy.target_lost", "observe", new[] { "gtfo.enemy.targeting.read" }),
            new ProviderBinding(ScoutDetectionBinding, Blocks.TriggerEnemyScoutDetection.Id, "gtfo.enemy.scout_detection", "observe", new[] { "gtfo.enemy.detection.read" }),
            new ProviderBinding(ScoutScreamBinding, Blocks.TriggerEnemyScoutScream.Id, "gtfo.enemy.scout_scream", "observe", new[] { "gtfo.enemy.behavior.read" }))
        .Bind(new ProviderBinding(EnemySelectorContract.BindingId, Blocks.SelectorTargetEnemies.Id,
            EnemySelectorContract.HandlerName, "observe", Array.Empty<string>(), EnemySelectorContract.Shape))
        .Bind(ForgeEnemy.AttackInstanceContract.DeclareBindings())
        .Bind(ForgeEnemy.EnemyNodeValueContract.DeclareBindings())
        .Bind(EnemyStationaryContract.DeclareBinding())
        .Bind(EnemyStationaryContract.ReadBinding())
        .Bind(EnemyWakeLockContract.DeclareBinding())
        .Bind(EnemyWakeLockContract.ReadBinding())
        .Bind(EnemyNativeStateContract.Bindings())
        .Bind(ForgeEnemy.EnemyNodeEffectContract.DeclareBindings())
        .Bind(ForgeEnemy.EnemyNodeTriggerContract.DeclareBindings())
        .Bind(ForgeEnemy.EnemyAbilityUsedContract.DeclareBindings())
        .Bind(ForgeEnemy.EnemyActionTriggerContract.DeclareBindings())
        .Bind(ForgeEnemy.EnemyControlContract.DeclareBindings())
        .Bind(ForgeEnemy.EnemyCombatContract.DeclareBindings())
        .Bind(ForgeEnemy.EnemyStaggerContract.DeclareBindings())
        .Bind(ForgeEnemy.GlueContract.DeclareBindings())
        .Bind(ForgeEnemy.Native.EnemyBehaviorContract.DeclareBindings())
        .Bind(ForgeEnemy.EnemyAbilityInterruptedContract.DeclareBindings())
        .Bind(ForgeEnemy.EnemyVolumeContract.DeclareBindings())
        .Bind(ForgeEnemy.EnemyPushContract.DeclareBindings())
        .Bind(ForgeEnemy.CombatDamageRuleContract.DeclareBindings())
        .Bind(ForgeEnemy.Native.EnemyProfileContract.DeclareBindings())
        .Bind(ForgeEnemy.EnemyWaveContract.DeclareBindings());
}

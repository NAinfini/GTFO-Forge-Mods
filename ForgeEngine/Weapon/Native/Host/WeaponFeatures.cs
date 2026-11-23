using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeWeapon.Native;

/// <summary>The player-visible weapon functions. Every hook class belongs to a feature (the one patch of a body two
/// features read belongs to both), and a hook Runtime refuses withdraws the features that own it: that feature's hook bodies run inert through
/// <see cref="WeaponNativeSession.Offer"/> while every other feature keeps running.</summary>
internal enum WeaponFeature
{
    Fire,
    Hit,
    Automatic,
    ReloadState,
    ReloadSpeed,
    Reload,
    Ammunition,
    Melee,
    Damage,
    Equipment,
    FixedTool,
    Appearance,
    Inventory,
    Devices,
    Sentry,
    Lifetime,
}

internal static class WeaponFeatures
{
    /// <summary>The hook classes each feature owns, by class name. Classes missing here are not declared at all.</summary>
    private static readonly (WeaponFeature Feature, string[] Hooks)[] Table =
    {
        (WeaponFeature.Fire, new[]
        {
            "ShotNoiseParameter", "AimStateSampled", "HolderFireHeld", "HolderFirePressed", "ArchetypeUpdateHooks",
        }),
        (WeaponFeature.Hit, new[] { "BulletProjectileStep" }),
        (WeaponFeature.Automatic, new[] { "AttackAutoStarted", "AttackAutoEnded", "AttackBurstStarted", "AttackBurstEnded" }),
        // The reload flag is stored in four unique game bodies (the reload entry's own animation start, its completion
        // callback, the wield and the unwield bodies), so each one reports the flag it leaves. The wield body's one
        // patch is shared with Equipment.
        (WeaponFeature.ReloadState, new[] { "EquipmentWielded" }),
        (WeaponFeature.ReloadSpeed, new[] { "ReloadSequenceBounded" }),
        (WeaponFeature.Reload, new[] { "ReloadCommitted", "ReloadRequested" }),
        (WeaponFeature.Ammunition, new[] { "MagazineRefilled", "PoolBulletsUpdated", "PoolDataArrived", "ResourceUseChecked" }),
        (WeaponFeature.Melee, new[]
        {
            "MeleeChargeStarted", "MeleeChargeRan", "MeleeChargeReleased", "MeleeChargeEnded",
            "MeleeThirdPersonDamage",
        }),
        // The enemy damage receivers are observed through Runtime's shared entries, not declared here.
        (WeaponFeature.Damage, Array.Empty<string>()),
        (WeaponFeature.Equipment, new[]
        {
            "LocalItemWielded", "LocalItemUnwielded", "SyncedItemEquipped", "SyncedItemUnwielded", "EquipmentWielded",
            "EquipmentFieldsDestroyed", "ItemEquippableSetup", "ItemThrown", "UseSequenceStarted", "SyringeApplied", "ThrowUseChecked",
            "InteractTimedSelected", "BiotrackerWieldHold",
        }),
        (WeaponFeature.FixedTool, new[] { "HackingToolSlotSpawn" }),
        // The gear spawn and parts hooks are ForgeCore's GameHooks, subscribed, not declared here.
        (WeaponFeature.Appearance, Array.Empty<string>()),
        (WeaponFeature.Inventory, new[]
        {
            "BackpackItemStored", "BackpackSlotCleared",
            "BackpackInstancesDestroyed",
        }),
        (WeaponFeature.Devices, new[]
        {
            "EnemyScannerBotCallFields", "EnemyScannerCallFields", "EnemyScannerSetup",
            "EnemyScannerTagFields", "FogRepellerInstanceStart", "GlueGunFieldSetup", "GlueGunSingle",
            "LockMelterFirstPersonSetup", "MineDeployerFirstPersonSetup", "MineDeployerInstanceSetup", "MineDetonated",
            "MineWorldDestroyed",
        }),
        (WeaponFeature.Sentry, new[]
        {
            "SentryArchetypeSelected", "SentryDetectionSetupScope", "SentryFired",
            "SentryFiringSetupScope", "SentryGunFirstPersonSetup", "SentryGunInstanceSetup", "SentryPlaced",
        }),
        (WeaponFeature.Lifetime, new[] { "ShardDependencyResetObserved" }),
    };

    private static readonly IReadOnlyDictionary<string, WeaponFeature[]> Owners = Table
        .SelectMany(entry => entry.Hooks.Select(hook => (Hook: hook, entry.Feature)))
        .GroupBy(pair => pair.Hook, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(pair => pair.Feature).ToArray(), StringComparer.Ordinal);

    /// <summary>Features that cannot run on this build, decided by the hook evidence and not by a runtime refusal. Their
    /// hook classes are never declared. Each reason is what the player loses.</summary>
    internal static readonly IReadOnlyDictionary<WeaponFeature, string> Withdrawn = new Dictionary<WeaponFeature, string>();

    /// <summary>The features a hook class belongs to — one, or several when features share one patch of a body —
    /// and empty when the class is not part of any feature.</summary>
    internal static IReadOnlyList<WeaponFeature> Of(Type callbacks)
        => Owners.TryGetValue(callbacks.Name, out var features) ? features : Array.Empty<WeaponFeature>();

    /// <summary>The hook classes that may be declared: every owned class with a feature not withdrawn by evidence.</summary>
    internal static IReadOnlyList<Type> Declarable(IEnumerable<Type> types)
        => types.Where(type => Of(type).Any(feature => !Withdrawn.ContainsKey(feature))).ToArray();

    /// <summary>The stable name a log line and a status row use for a feature.</summary>
    internal static string Label(WeaponFeature feature) => feature.ToString();
}

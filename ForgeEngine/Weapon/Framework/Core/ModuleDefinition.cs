using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>Weapon provider. Wield facts come only from Weapon's own equipment observation; the actor is a
/// player reference owned by the player domain and is never created here. Every row is declared once, outside this
/// package: the equipment, input, combat and inventory facts, the deployed-device facts, the melee hit and the
/// attack-instance facts by the runtime's own trigger contract `forge.contract.trigger`, and the rows this provider
/// owns — the charge, aim and shot-resolution rows, the hit-context read, the state reads that answer over a source
/// attached with the session (the availability gate, the weapon profile and the debug-switch read), and the actions
/// it executes — by the generated block table (<see cref="ProviderDeclaration.DeclareBlocks"/>). This module
/// carries no copy of a row: it supplies the bindings, handlers, shapes and permissions, each with the native body
/// this machine supplies. This provider also owns the one resource kind its facts carry — the ammunition `pool` —
/// so the module registers that provider beside its rows.</summary>
internal static class ModuleDefinition
{
    public const string ProviderId = "forge.module.gtfo.weapon";
    /// <summary>The one mount target kind this provider answers for: an official offline gear block id, which
    /// every instance of that block hangs on, as opposed to one address or one native object.</summary>
    public const string GearBlockAttachmentKind = "gear-block";
    public const string EquippedCapability = Blocks.TriggerInputEquipped.Id;
    public const string UnequippedCapability = Blocks.TriggerInputUnequipped.Id;
    public const string ShotCommittedCapability = Blocks.TriggerCombatShotCommitted.Id;
    public const string HitCandidateCapability = Blocks.TriggerCombatHitCandidate.Id;
    public const string DespawnedCapability = Blocks.TriggerEntityDespawned.Id;
    public const string DeployCompletedCapability = Blocks.TriggerEquipmentDeployCompleted.Id;
    public const string RecallCompletedCapability = Blocks.TriggerEquipmentRecallCompleted.Id;
    public const string EquippedBinding = ProviderId + ".binding.equipped";
    public const string UnequippedBinding = ProviderId + ".binding.unequipped";
    public const string ShotCommittedBinding = ProviderId + ".binding.shot_committed";
    public const string HitCandidateBinding = ProviderId + ".binding.hit_candidate";
    public const string DespawnedBinding = ProviderId + ".binding.despawned";
    public const string DeployCompletedBinding = ProviderId + ".binding.deploy_completed";
    public const string RecallCompletedBinding = ProviderId + ".binding.recall_completed";
    public const string WieldReadPermission = "gtfo.equipment.wield.read";
    public const string CombatReadPermission = "gtfo.weapon.combat.read";
    public const string DeployableReadPermission = "gtfo.equipment.deployable.read";

    /// <summary>The one Weapon provider declaration. Every handler here is the native half's body for one row
    /// this provider owns; a registration only ever declares what it answers, and the runtime refuses an
    /// implemented binding whose handler the registration does not supply, so each block row, its binding and
    /// its shape travel together with that handler or not at all.
    ///
    /// <paramref name="overrideRestores"/> is the same family's restore table, keyed by handler name like every
    /// other table here: only the rows named in it may carry a plan's `effect` block, because a duration the
    /// kernel hands out is a duration something has to be called back for. All four override rows are in it; the
    /// `fire_rate` row can also time itself from its own `duration` port.
    ///
    /// <paramref name="overrides"/> is the instance-override family's own handler table
    /// (<see cref="WeaponOverrideContract.Handlers"/>): its four rows are declared by that contract as one set,
    /// so the table that carries their bodies is what is supplied or not. The inventory pair is the `a-p-item`
    /// half — give and consume. `drop` declares nothing and has no body: the node list has no drop node, so no
    /// binding row is added for it, no capability row is declared (ruling 110.5) and the implementation is gone
    /// with them (ruling 133.3). The reload, inventory and device families travel with their own contracts:
    /// <see cref="ReloadInventoryContract"/> hands back the twelve rows' bindings — the shapes are the runtime
    /// trigger contract's own — and <see cref="InventoryQueryContract"/> hands back the two read rows it evaluates
    /// on demand, each with the evaluator body and the shape that body answers through.</summary>
    public static RuntimeModule Create(
        CommandHandler? ammoAdd = null,
        IReadOnlyDictionary<string, CommandHandler>? overrides = null,
        CommandHandler? inventoryGive = null,
        IReadOnlyDictionary<string, EffectRestoreHandler>? overrideRestores = null,
        CommandHandler? projectileLaunch = null,
        CommandHandler? hitscan = null,
        WeaponPropertyProfileTable? profiles = null,
        CommandHandler? shotRule = null,
        Func<EntityReference, string?>? shotRuleHolders = null,
        CommandHandler? bulletProjectile = null,
        Func<EntityReference, string?>? overrideHolders = null,
        CommandHandler? equipGear = null,
        Func<EntityReference, string?>? equipGearRecipients = null,
        IReadOnlyDictionary<string, RuntimeResourceProvider>? gearResources = null,
        Func<EntityReference, string?>? ammoAddRecipients = null)
    {
        // Every row is the generated block table's; a row whose body this registration was not given stays undeclared.
        var undeclared = new IEnumerable<string>[]
        {
            ammoAdd == null ? new[] { WeaponSupplyContract.AmmoAddCapability } : Array.Empty<string>(),
            overrides == null ? WeaponOverrideContract.All : Array.Empty<string>(),
            inventoryGive == null ? new[] { InventoryActionContract.GiveCapability } : Array.Empty<string>(),
            equipGear == null ? new[] { GearEquipContract.Capability } : Array.Empty<string>()
        }.SelectMany(ids => ids).ToArray();
        var declaration = new ProviderDeclaration(ProviderId, EnginePackage.Version, EnginePackage.Name)
            .DeclareBlocks(undeclared)
            .Bind(
                new ProviderBinding(EquippedBinding, EquippedCapability, "gtfo.equipment.equipped", "observe", new[] { WieldReadPermission }),
                new ProviderBinding(UnequippedBinding, UnequippedCapability, "gtfo.equipment.unequipped", "observe", new[] { WieldReadPermission }),
                new ProviderBinding(ShotCommittedBinding, ShotCommittedCapability, "gtfo.weapon.shot_committed", "observe", new[] { CombatReadPermission }),
                new ProviderBinding(HitCandidateBinding, HitCandidateCapability, "gtfo.weapon.hit_candidate", "observe", new[] { CombatReadPermission }),
                new ProviderBinding(DespawnedBinding, DespawnedCapability, "gtfo.equipment.despawned", "observe", new[] { DeployableReadPermission }),
                new ProviderBinding(DeployCompletedBinding, DeployCompletedCapability, "gtfo.equipment.deploy_completed", "observe", new[] { DeployableReadPermission }),
                new ProviderBinding(RecallCompletedBinding, RecallCompletedCapability, "gtfo.equipment.recall_completed", "observe", new[] { DeployableReadPermission }));
        declaration = declaration
            .Bind(WeaponStateTriggerContract.DeclareBindings())
            .Bind(CombatPrimitiveContract.DeclareBindings(projectileLaunch))
            .Bind(HitscanShotContract.DeclareBindings(hitscan))
            .Bind(ShotRuleContract.DeclareBindings(shotRule))
            .Bind(BulletProjectileContract.DeclareBindings(bulletProjectile))
            .Bind(HitContextContract.DeclareBindings())
            .Bind(WeaponSupplyContract.DeclareBindings(ammoAdd));
        if (overrides != null) declaration = declaration.Bind(WeaponOverrideContract.DeclareBindings());
        declaration = declaration
            .Bind(InventoryActionContract.DeclareBindings(inventoryGive))
            .Bind(GearEquipContract.DeclareBindings(equipGear))
            .Bind(InventoryQueryContract.DeclareBindings())
            .Bind(WeaponDebugSwitchContract.DeclareBindings())
            .Bind(WeaponGateContract.DeclareBindings())
            .Bind(EquipmentEventContract.DeclareBindings())
            .Bind(WeaponProfileContract.DeclareBindings())
            .Bind(WeaponProfileReadContract.DeclareBindings())
            .Bind(ProjectileStateContract.DeclareBindings())
            .Bind(DetonateContract.DeclareBindings())
            .Bind(SentryRecallContract.DeclareBindings())
            .Bind(WeaponDeployableFactsContract.DeclareBindings())
            .Bind(WeaponMeleeHitContract.DeclareBindings())
            .Bind(WeaponBurstContract.DeclareBindings())
            .Bind(ReloadInventoryContract.DeclareBindings());
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        // One body and one shape per declared row. A handler the registration carries but no binding names is
        // refused as unused, which is why every table above answers per supplied body and not per family.
        foreach (var entry in WeaponSupplyContract.Handlers(ammoAdd)) handlers[entry.Key] = entry.Value;
        foreach (var entry in InventoryActionContract.Handlers(inventoryGive)) handlers[entry.Key] = entry.Value;
        foreach (var entry in GearEquipContract.Handlers(equipGear)) handlers[entry.Key] = entry.Value;
        if (overrides != null)
        {
            foreach (var entry in overrides) handlers[entry.Key] = entry.Value;
        }
        // The two inventory reads are evaluated on demand rather than dispatched, so their bodies go in the
        // evaluator table the registration carries beside the handlers. Each body reads the world through the
        // source the native half attaches to it, and refuses by name while no source is attached — which is the
        // state every process that only declares this provider is in. The launch row's body is supplied by the
        // native half, so a process that declared this provider without its game half declares neither its binding
        // nor its shape.
        var evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal);
        foreach (var entry in InventoryQueryContract.Evaluators()) evaluators[entry.Key] = entry.Value;
        // The hit-context read is the third on-demand row and follows the same shape: declared with its evaluator
        // body and refused by code while no native source is attached, which is exactly what this process is.
        foreach (var entry in HitContextContract.Evaluators()) evaluators[entry.Key] = entry.Value;
        // The availability-gate read is the fourth on-demand row and follows the same shape; the debug switch is
        // a dispatched write whose body is held by the same kind of attached source. Both are declared here for
        // every process, because both refuse by code while no native source is attached.
        foreach (var entry in WeaponGateContract.Evaluators()) evaluators[entry.Key] = entry.Value;
        foreach (var entry in WeaponDebugSwitchContract.Handlers()) handlers[entry.Key] = entry.Value;
        // The profile read is the same kind of row: an evaluator over a source attached with the session.
        foreach (var entry in WeaponProfileReadContract.Evaluators()) evaluators[entry.Key] = entry.Value;
        foreach (var entry in ProjectileStateContract.Evaluators()) evaluators[entry.Key] = entry.Value;
        // The whole-profile write and the detonation are the same kind of row: dispatched actions whose bodies
        // answer through an attached source, declared here for every process so both refuse by code while none is.
        foreach (var entry in WeaponProfileContract.Handlers()) handlers[entry.Key] = entry.Value;
        foreach (var entry in DetonateContract.Handlers()) handlers[entry.Key] = entry.Value;
        foreach (var entry in SentryRecallContract.Handlers()) handlers[entry.Key] = entry.Value;
        if (projectileLaunch != null) handlers[CombatPrimitiveContract.ProjectileLaunchHandler] = projectileLaunch;
        if (hitscan != null) handlers[HitscanShotContract.Handler] = hitscan;
        if (shotRule != null) handlers[ShotRuleContract.Handler] = shotRule;
        if (bulletProjectile != null) handlers[BulletProjectileContract.Handler] = bulletProjectile;
        // The shot rule is an `owner` row: the kernel routes it to the machine that holds the equipment, and only
        // the provider that declared the row may answer who that is. A registration without the resolver carries
        // no answer, and the kernel refuses the step with `owner-holder` rather than sending it anywhere.
        var ownerSessions = new Dictionary<string, Func<EntityReference, string?>>(StringComparer.Ordinal);
        if (shotRuleHolders != null) ownerSessions[ShotRuleContract.CapabilityId] = shotRuleHolders;
        if (overrideHolders != null)
            foreach (var capability in new[] { WeaponOverrideContract.FireRateCapability, WeaponOverrideContract.SpreadCapability,
                WeaponOverrideContract.RecoilCapability, WeaponOverrideContract.PropertyCapability, WeaponProfileContract.CapabilityId })
                ownerSessions[capability] = overrideHolders;
        // The gear-equip row is an owner row as well: it is routed to the machine of the player it names.
        foreach (var entry in GearEquipContract.OwnerSessions(equipGearRecipients)) ownerSessions[entry.Key] = entry.Value;
        // The ammunition row is an owner row too: a pool is written on the machine that owns the player's backpack.
        foreach (var entry in WeaponSupplyContract.OwnerSessions(ammoAddRecipients)) ownerSessions[entry.Key] = entry.Value;
        return declaration.Module(RuntimeApi.Fingerprint, handlers, evaluators: evaluators) with
        {
            EffectRestores = overrideRestores,
            OwnerSessions = ownerSessions,
            // The resource kinds this provider answers for. The ammunition pools are this package's own vocabulary
            // (`ReloadInventoryContract.PoolSlots`): the ammunition fact carries a pool reference and the kernel
            // resolves it through its owner, so the registration and the fact are one vocabulary. The `profile`
            // kind is the install's exported property profiles (`WeaponPropertyProfileTable`), which the game session
            // and the offline admission read from the same files, each answering the revision the file holds. A kind
            // with no owner is not silently empty — a compiled reference to it is `stale-resource` — so a process
            // that read no profile table registers none.
            ResourceProviders = ResourceKinds(profiles, gearResources)
        };
    }

    /// <summary>The kinds this registration owns, as one table. The pool kind is always here because its
    /// vocabulary is this package's own; the profile kind is here when a profile table was read, and the weapon and
    /// tool gear kinds when the caller supplied the table they read (the game's offline gear blocks).</summary>
    private static IReadOnlyDictionary<string, RuntimeResourceProvider> ResourceKinds(
        WeaponPropertyProfileTable? profiles, IReadOnlyDictionary<string, RuntimeResourceProvider>? gear)
    {
        var kinds = new Dictionary<string, RuntimeResourceProvider>(ReloadInventoryContract.ResourceProviders(),
            StringComparer.Ordinal);
        if (profiles != null) kinds[WeaponProfileContract.ProfileResourceKind] = profiles.Provider();
        if (gear != null) foreach (var entry in gear) kinds[entry.Key] = entry.Value;
        return kinds;
    }

}

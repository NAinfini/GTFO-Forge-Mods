using System;
using System.Collections.Generic;
using System.Linq;
using Gear;
using HarmonyLib;
using Player;
using ForgeEngine.Framework;

namespace ForgeWeapon.Native;

internal static partial class WeaponNativeHooks
{
    internal static WeaponNativeSession? ObserveCombat(WeaponFeature feature)
        => WeaponNativeSession.Current is { NativeCombatFactsObserved: true } ? WeaponNativeSession.Offer(feature) : null;

    // Attribution can feed another provider's damage fact even when no weapon row is consumed. The window opens only
    // while a loaded plan consumes a trigger whose payload can name equipment: Runtime's one precomputed boolean.
    internal static WeaponNativeSession? ObserveAttribution(WeaponFeature feature = WeaponFeature.Fire)
        => WeaponNativeSession.Current is { NativeAttributionObserved: true } ? WeaponNativeSession.Offer(feature) : null;

    /// <summary>The authored-field hooks, declared in `WeaponFieldHooks.cs`, which only the Native project
    /// compiles: each one writes a native member at a generation point the offline doubles do not carry. Where
    /// that file is not compiled the call compiles away and the package simply has no authored-field writers.</summary>
    static partial void NativeFieldHookTypes(List<Type> types);

    /// <summary>The bio-tracker device hold's one hook, declared in `BiotrackerDevice.cs`, which only the Native
    /// project compiles.</summary>
    static partial void NativeDeviceHookTypes(List<Type> types);
    static partial void NativeResourceLifetimeHookTypes(List<Type> types);
    static partial void NativeResourceLifetimeSubscriptions();
    static partial void NativeFixedToolHookTypes(List<Type> types);
    /// <summary>The subscriptions of `WeaponFieldHooks.cs`, which only the Native project compiles.</summary>
    static partial void NativeFieldSubscriptions();

    private static bool _subscribed;

    /// <summary>Subscribes to the game methods ForgeCore patches once for every package (<c>ForgeCore.GameHooks</c>):
    /// the four fire bodies, the two weapon ray casts, the melee push and per-target hit entries, the sentry's gear-spawn and despawn, and
    /// the mine's spawn and pickup. A bracket calls the subscribers' before in this order and their after in the
    /// reverse, so the order below is the nesting: attribution is the outermost, then the shot rule's window and the
    /// shot noise, and the shot's start and resolution are innermost, which is where the old postfixes ran before the
    /// finalizers' cleanup. Once per process: the subscriptions look the live session up on every call.</summary>
    internal static void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        FireAttribution.Subscribe();
        ShotRuleHooks.Subscribe();
        FireNoiseHooks.Subscribe();
        FireResolution.Subscribe();
        MeleePushPrepared.Subscribe();
        MeleeSwingDamage.Subscribe();
        MeleePushDamage.Subscribe();
        WeaponPlacementHooks.Subscribe();
        AttackAutoFiredEmptyClip.Subscribe();
        AttackSemiBurstFiredEmptyClip.Subscribe();
        ReloadStartedFlag.Subscribe();
        ReloadEndedFlag.Subscribe();
        ReloadUnwieldedFlag.Subscribe();
        ResourceApplied.Subscribe();
        ReloadSpeedHooks.Subscribe();
        BulletHit.Subscribe();
        BackpackItemDeployed.Subscribe();
        GearPartsSpawned.Subscribe();
        GearSpawnCompleted.Subscribe();
        GlueGunBurst.Subscribe();
        FlashlightToggled.Subscribe();
        GearOffersForSlot.Subscribe();
        ItemAppearanceSpawned.Subscribe();
        NativeFieldSubscriptions();
        NativeResourceLifetimeSubscriptions();
    }


    /// <summary>The hook classes this file declares: the equipment, combat, attack-instance, deployed-device and
    /// melee families. The deployed-device five and the melee two are declared by the observers they feed
    /// (<c>WeaponDeployableFacts</c> and <c>WeaponMeleeHitFacts</c>); they are listed here because this file is
    /// the package's own hook list and the one place a patch is declared.</summary>
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
    {
        typeof(BackpackItemStored), typeof(BackpackSlotCleared), typeof(BackpackInstancesDestroyed),
        typeof(LocalItemWielded), typeof(LocalItemUnwielded), typeof(SyncedItemEquipped), typeof(SyncedItemUnwielded),
        // The attack-instance family: the two ends of a burst sequence on the two burst-capable archetypes (the empty-clip
        // paths are ForgeCore's `AutoDryFired` and `SemiDryFired`, subscribed in `Subscribe`).
        typeof(AttackBurstStarted), typeof(AttackBurstEnded),
        typeof(AttackAutoStarted), typeof(AttackAutoEnded),
        // The deployed-device and tool facts: the glue gun's single launch body (its burst is ForgeCore's
        // `GlueBurstFired`, subscribed in `Subscribe`) and the mine's own trigger. The sentry's firing update runs
        // every frame and goes on only when needed (WeaponLazyHooks).
        typeof(GlueGunSingle), typeof(MineDetonated),
        // The melee hit: the third-person body this machine runs for somebody else (the first-person body is ForgeCore's
        // MeleeAttackDamage, subscribed in Subscribe).
        typeof(MeleeThirdPersonDamage),
        // The melee charge family. The ranged charge and the holder's aim state run every frame and are put on only
        // when needed (WeaponLazyHooks).
        typeof(MeleeChargeStarted), typeof(MeleeChargeRan), typeof(MeleeChargeEnded), typeof(MeleeChargeReleased),
        // The equipment facts, declared beside their observer in `EquipmentEventFacts`: the one throw entry that
        // carries a velocity (the push's two ends and the hit branch the swing row skips are ForgeCore's `PushEntered`
        // and `MeleeAttackDamage`, and the attached light's setter ForgeCore's `FlashlightSet`, all subscribed in
        // `Subscribe`).
        typeof(ItemThrown)
    });

    /// <summary>Every hook class the package installs, in one list: this file's own set plus the three families
    /// that declare theirs beside their observer (<c>ReloadInventoryHooks</c>, <c>WeaponPlacementHooks</c> and
    /// <c>ReloadSpeedHooks</c>). <see cref="Plugin"/> installs this and nothing else, so a hook class that is not
    /// carried here is never patched — the failure mode a family's own list would otherwise reintroduce.</summary>
    internal static IReadOnlyList<Type> Installed { get; } = Sum();

    /// <summary>Declares every callback class of this package to Runtime, the only installer of native hooks. Any refused
    /// target refuses the start and is named with Runtime's own reason; no target is skipped or installed here.</summary>
    internal static IReadOnlyDictionary<WeaponFeature, string> Declare()
    {
        Subscribe();
        var unmapped = Installed.Concat(WeaponLazyHooks.All).Where(type => WeaponFeatures.Of(type).Count == 0).Select(type => type.Name).ToArray();
        if (unmapped.Length != 0) throw new NotSupportedException("weapon-hook-feature-unmapped: " + string.Join(", ", unmapped));
        var declared = WeaponFeatures.Declarable(Installed.Distinct()).ToArray();
        var refused = ForgeEngine.GameBindings.NativeHookRegistration.Declare(ModuleDefinition.ProviderId, declared);
        var withdrawn = new Dictionary<WeaponFeature, string>();
        foreach (var refusal in refused)
        {
            var reason = refusal.Callbacks.Name + "=" + refusal.Reason;
            foreach (var feature in WeaponFeatures.Of(refusal.Callbacks))
                withdrawn[feature] = withdrawn.TryGetValue(feature, out var earlier) ? earlier + "; " + reason : reason;
        }
        return withdrawn;
    }

    private static IReadOnlyList<Type> Sum()
    {
        var types = new List<Type>(Types.Count + ReloadInventoryHooks.Types.Count + WeaponPlacementHooks.Types.Count
            + ReloadSpeedHooks.Types.Count);
        types.AddRange(Types);
        types.AddRange(ReloadInventoryHooks.Types);
        types.AddRange(WeaponPlacementHooks.Types);
        types.AddRange(ReloadSpeedHooks.Types);
        NativeFieldHookTypes(types);
        NativeDeviceHookTypes(types);
        NativeResourceLifetimeHookTypes(types);
        NativeFixedToolHookTypes(types);
        return types.AsReadOnly();
    }
}

/// <summary>
/// The hooks on bodies the game runs every frame that only some requests need, put on the first time one needs them
/// instead of at startup: the holder's fire buttons (a fire request), the archetype's update (a fire request, or a plan
/// listening to the ranged charge state), the first-person holder's update (a plan listening to the aim state), the
/// throwing weapon's update (a plan listening to a refused use), the sentry's firing update (a plan listening to a
/// deployed-device or shot fact), the player's noise setter (an authored shot noise) and the enemy scanner's three
/// bodies (an authored scanner block).
/// Each unit is declared to Runtime once, as one set per target, and stays: an IL2CPP unpatch keeps a pass-through
/// detour and leaks the generated trampoline, so taking a unit off again would cost more than leaving it on.
/// </summary>
internal static partial class WeaponLazyHooks
{
    /// <summary><c>Scanner</c> is last: only the Native project's own half of this class (<c>WeaponFieldHooks.cs</c>)
    /// carries its table, so where that file is not compiled the table simply ends before it.</summary>
    internal enum Unit { FireInput, ArchetypeUpdate, Aim, ThrowUse, SentryFire, NoiseSetter, ProjectileStep, Scanner }

    private static readonly Type[][] Units = WithNative(new[]
    {
        new[] { typeof(HolderFireHeld), typeof(HolderFirePressed) },
        // The holder's fire update and the ranged charge reading, one class on BulletWeaponArchetype.Update.
        new[] { typeof(ArchetypeUpdateHooks) },
        new[] { typeof(AimStateSampled) },
        new[] { typeof(ThrowUseChecked) },
        new[] { typeof(SentryFired) },
        new[] { typeof(ShotNoiseParameter) },
        new[] { typeof(BulletProjectileStep) },
    });

    private static Type[][] WithNative(Type[][] common)
    {
        var all = new List<Type[]>(common);
        NativeUnits(all);
        return all.ToArray();
    }

    /// <summary>The units only the Native project compiles, appended after the common ones in <see cref="Unit"/> order.</summary>
    static partial void NativeUnits(List<Type[]> units);

    /// <summary>Every class a unit can put on; mapped to a feature like the startup list.</summary>
    internal static IReadOnlyList<Type> All { get; } = Array.AsReadOnly(Units.SelectMany(unit => unit).ToArray());

    // null: not asked yet; "": on; anything else: Runtime's refusal, kept so a refused unit is not declared again.
    private static readonly string?[] States = new string?[Units.Length];

    /// <summary>Test seam: the offline suites install through their own double.</summary>
    internal static Func<string, IEnumerable<Type>, IReadOnlyList<ForgeEngine.GameBindings.NativeHookRefusal>> Installer { get; set; }
        = ForgeEngine.GameBindings.NativeHookRegistration.Declare;

    internal static bool IsOn(Unit unit) => (int)unit < States.Length && States[(int)unit] == "";

    /// <summary>Puts one unit on if it is not on yet. Null when it is on; otherwise the reason it cannot be.</summary>
    internal static string? Ensure(Unit unit)
    {
        if ((int)unit >= Units.Length) return "weapon-hook-unit-not-compiled";
        var state = States[(int)unit];
        if (state != null) return state.Length == 0 ? null : state;
        var declarable = WeaponFeatures.Declarable(Units[(int)unit]);
        string reason;
        if (declarable.Count != Units[(int)unit].Length) reason = "weapon-hook-feature-withdrawn";
        else
        {
            var refused = Installer(ModuleDefinition.ProviderId, declarable);
            reason = refused.Count == 0 ? "" : string.Join("; ", refused.Select(r => r.Callbacks.Name + "=" + r.Reason));
        }
        States[(int)unit] = reason;
        if (reason.Length != 0)
            WeaponNativeSession.Current?.Log.Text(RuntimeLogLevel.Error, "weapon.native-hook-refused", unit + ": " + reason);
        return reason.Length == 0 ? null : reason;
    }

    /// <summary>Puts every compiled unit on (Authoring, at the first content generation, so a reload never installs a hook).
    /// Returns the refusal of each unit that could not go on.</summary>
    internal static IReadOnlyList<string> EnsureAll()
        => Enumerable.Range(0, Units.Length).Select(index => (unit: (Unit)index, reason: Ensure((Unit)index)))
            .Where(row => row.reason != null).Select(row => row.unit + ": " + row.reason).ToArray();

    /// <summary>Offline suites start each case from nothing put on.</summary>
    internal static void ResetForTest() => Array.Clear(States);
}

internal sealed partial class WeaponNativeSession
{
    private RuntimeSubscriptionGate?[]? _nativeCombatGates;
    private long _nativeCombatGeneration = -1;
    private bool _nativeCombatObserved;

    /// <summary>Puts on, once and for good, each per-frame hook unit whose report a loaded plan now listens to
    /// (<see cref="WeaponLazyHooks"/>). Called on every advanced tick; each observer answers with a boolean until its
    /// unit is asked for.</summary>
    internal void EnsureLazyHooks()
    {
        CombatFactsObserver.Current?.EnsureStateHooks();
        ReloadInventoryHooks.EnsureLazyHooks();
        WeaponDeployableFacts.Current?.EnsureSentryHook(NativeCombatFactsObserved);
    }

    private RuntimeSubscriptionGate? _attributionGate;
    internal bool NativeAttributionObserved => (_attributionGate ??= Identity.Registration.EquipmentAttributionGate()).HasSubscribers;

    internal bool NativeCombatFactsObserved
    {
        get
        {
            var generation = _kernel.SubscriptionGeneration;
            if (_nativeCombatGeneration == generation) return _nativeCombatObserved;
            _nativeCombatGates ??= new[]
            {
                Identity.GateOf(ModuleDefinition.ShotCommittedBinding), Identity.GateOf(ModuleDefinition.HitCandidateBinding),
                Identity.GateOf(HitContextContract.BindingId), Identity.GateOf(CombatPrimitiveContract.ShotResolvedBinding)
            };
            _nativeCombatObserved = false;
            foreach (var gate in _nativeCombatGates)
                if (gate is { HasSubscribers: true }) { _nativeCombatObserved = true; break; }
            _nativeCombatGeneration = generation;
            return _nativeCombatObserved;
        }
    }
}

// Every hook is a postfix that only chooses when to read. Observed values always come from the
// backpack and inventory state after the native body returned; parameters are never trusted as facts.
// Targets and their non-shared native bodies are frozen in evidence/w1-native-hooks.json.

// Every path that stores an item ends here. The adapter's backpack reconcile runs first; then, when the body ran, the
// inventory observer's readback of the slot table and the pool, which the pickup, drop, stack and ammunition rows are
// decided from (a pool gain it sees is the stored item's own charges, so it is reported as a pickup).
[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.CreateAndStoreBackpackItem))]
internal static class BackpackItemStored
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance, bool __runOriginal)
    {
        WeaponNativeSession.Offer(WeaponFeature.Inventory)?.Guard(static (session, pack) => session.Adapter.Reconcile(pack), __instance);
        if (!__runOriginal) return;
        ReloadInventoryHooks.GuardInventory(WeaponFeature.Inventory, static (inventory, pack) => inventory.Reconcile(pack, stored: true), __instance);
    }
}

[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.TryClearSlot))]
internal static class BackpackSlotCleared
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Inventory)?.Guard(static (session, pack) => session.Adapter.Reconcile(pack), __instance);
}

[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.DestroyAllInstance))]
internal static class BackpackInstancesDestroyed
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Inventory)?.Guard(static (session, pack) => session.Adapter.Reconcile(pack), __instance);
}

// Deployables stay in their backpack slot: the sentry or mine marks the slot deployed instead of leaving the
// backpack, and a recall clears the same marker. This hook only chooses when to read that marker back.
// ForgeCore patches the method once (`GameHooks.BackpackDeployed`); the read comes after every other patch of it.
internal static class BackpackItemDeployed
{
    internal static void Subscribe() => ForgeCore.GameHooks.BackpackDeployed.After(Deployed);

    private static void Deployed(ForgeCore.BackpackDeploy call, bool ran)
        => WeaponNativeSession.Offer(WeaponFeature.Inventory)?.Guard(static (session, pack) => session.Adapter.Reconcile(pack), call.Backpack);
}

[HarmonyPatch(typeof(PlayerInventoryLocal), nameof(PlayerInventoryLocal.DoWieldItem))]
internal static class LocalItemWielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventoryLocal __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Equipment)?.Guard(static (session, inventory) => session.Adapter.ReconcileInventory(inventory), __instance);
}

[HarmonyPatch(typeof(PlayerInventoryLocal), nameof(PlayerInventoryLocal.UnWield))]
internal static class LocalItemUnwielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventoryLocal __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Equipment)?.Guard(static (session, inventory) => session.Adapter.ReconcileInventory(inventory), __instance);
}

[HarmonyPatch(typeof(PlayerInventorySynced), nameof(PlayerInventorySynced.DoEquipItem))]
internal static class SyncedItemEquipped
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventorySynced __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Equipment)?.Guard(static (session, inventory) => session.Adapter.ReconcileInventory(inventory), __instance);
}

[HarmonyPatch(typeof(PlayerInventorySynced), nameof(PlayerInventorySynced.UnWield))]
internal static class SyncedItemUnwielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventorySynced __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Equipment)?.Guard(static (session, inventory) => session.Adapter.ReconcileInventory(inventory), __instance);
}

// The Fire bodies are the per-shot dedupe point: one body runs per trigger pull, while the bullet routine it
// calls runs once per hit, so a shotgun's several hits still belong to the one shot opened here.
// Slot 151 has four bodies in this build and each one is reached on its own. `BulletWeapon.Fire` and
// `Shotgun.Fire` are the firing player's own weapon — the unsynced body, and the one that registers the shot
// with `PlayerSync` — while `BulletWeaponSynced.Fire` and `ShotgunSynced.Fire` are the copy every other machine
// holds of that player's weapon, replayed from the replicated shot count. `RifleWeapon` and `RifleWeaponSynced`
// declare no Fire of their own and use their family's body. No one of the four reaches another, so one bracket
// per body publishes exactly one shot fact and a shot counted once stays counted once; the call structure is
// frozen in evidence/w4-player-fire.json. ForgeCore patches the four bodies once (`GameHooks.Fire`); the classes
// below are this package's subscriptions to them.

/// <summary>Opens the equipment attribution of a firing body and leaves it when the body ends, whether it returned
/// or threw. The synced bodies also open and abandon the shot here: it opens before the body runs, where the game's
/// hit callbacks land. The unsynced bodies open it in <see cref="FireResolution"/>.</summary>
internal static class FireAttribution
{
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.Fire.BulletWeapon.Around(Own, OwnDone, OwnFaulted);
        ForgeCore.GameHooks.Fire.Shotgun.Around(Own, OwnDone, OwnFaulted);
        ForgeCore.GameHooks.Fire.BulletWeaponSynced.Around(Remote, RemoteDone, RemoteFaulted);
        ForgeCore.GameHooks.Fire.ShotgunSynced.Around(Remote, RemoteDone, RemoteFaulted);
    }

    private static WeaponCombatObserver.Attribution? Attributed(Weapon weapon)
        => WeaponNativeHooks.ObserveAttribution()?.Guard(static (session, fired) => session.Combat.Attribute(fired),
            weapon, default(WeaponCombatObserver.Attribution?));

    private static WeaponCombatObserver.Attribution? Own(BulletWeapon weapon) => Attributed(weapon);

    private static void OwnDone(BulletWeapon weapon, WeaponCombatObserver.Attribution? scope, bool ran) => WeaponCombatObserver.LeaveAttribution(scope);

    private static void OwnFaulted(BulletWeapon weapon, WeaponCombatObserver.Attribution? scope, Exception error) => WeaponCombatObserver.LeaveAttribution(scope);

    // The synced pair is not a duplicate of the pair above: a remote player's weapon never runs the unsynced body
    // on this machine, so without these two the firing player of every other machine — a joining client on the host,
    // or the host and every other client seen from one machine — produces no shot at all.
    private static WeaponCombatObserver.Attribution? Remote(Weapon weapon)
    {
        var scope = Attributed(weapon);
        WeaponNativeSession.Offer(WeaponFeature.Fire)?.Guard(static (session, fired) => session.Combat.BeginFire(fired), weapon);
        return scope;
    }

    private static void RemoteDone(Weapon weapon, WeaponCombatObserver.Attribution? scope, bool ran)
    {
        if (!ran) WeaponNativeSession.Offer(WeaponFeature.Fire)?.Guard(static (session, fired) => session.Combat.AbandonFire(), weapon);
        WeaponCombatObserver.LeaveAttribution(scope);
    }

    private static void RemoteFaulted(Weapon weapon, WeaponCombatObserver.Attribution? scope, Exception error)
        => WeaponCombatObserver.LeaveAttribution(scope);
}

// One static body serves every weapon family, and a candidate is published into the shot the last `Fire` opened;
// a hit with no open shot is not billed to a player, and the sentry's own firing component
// (`SentryGunInstance_Firing_Bullets.FireBullet` / `UpdateFireShotgunSemi`) is the caller that reaches this
// routine without any weapon `Fire`.
// The firing weapon is not read: the hit data names the shooter and the point. ForgeCore patches the body once
// (`GameHooks.BulletHit`). The before reads the search distance the caller asked for, which is a parameter and so exists
// only inside the invocation that carries it; the after consumes it with the hit, which is how the hit-context read
// reports what the native body was really entered with instead of a second guess at the shot's geometry. The call is
// pooled: only its fields are read here. This publishes nothing and writes nothing the game reads.
internal static class BulletHit
{
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.BulletHit.Before(Searched);
        ForgeCore.GameHooks.BulletHit.After(Hit);
    }

    private static void Searched(ForgeCore.BulletHitCall call)
        => WeaponNativeHooks.ObserveCombat(WeaponFeature.Hit)?.Guard(static (session, distance) => session.Combat.HitSearched(distance), call.AdditionalDistance);

    private static void Hit(ForgeCore.BulletHitCall call, bool ran)
    {
        if (!ran) return;
        // This is an observed hit candidate, never evidence of committed damage. Receivers own damage facts.
        WeaponNativeHooks.ObserveCombat(WeaponFeature.Hit)?.Guard(static (session, data) =>
        {
            // The host's replay of a client's weapon aims with its own spread; that client reports what its shot really reached.
            if (WeaponCombatObserver.DeployableFiringDepth > 0 || !session.ClientFacts.Hit(data)) session.Combat.Hit(data);
        }, call.Data);
    }
}

// The two ends of one firing body, around the shot the attribution brackets above already count. The window is
// what tells a shot that reached something from one that reached nothing: the native hit routine only runs when
// a ray did reach something, so a shot with no impact in its own window is the miss the `shot_resolved` row
// publishes. The bodies are the same two the shot fact is counted from, so a shot that is not an equipment life
// this machine answers for produces no resolution either.
internal static class FireResolution
{
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.Fire.BulletWeapon.Before(Begin);
        ForgeCore.GameHooks.Fire.BulletWeapon.After(Resolve);
        ForgeCore.GameHooks.Fire.Shotgun.Before(Begin);
        ForgeCore.GameHooks.Fire.Shotgun.After(Resolve);
    }

    private static void Begin(Weapon weapon)
        => WeaponNativeSession.Offer(WeaponFeature.Fire)?.Guard(static (session, fired) =>
        {
            session.Combat.BeginFire(fired);
            session.ClientFacts.BeginShot(fired);
        }, weapon);

    private static void Resolve(Weapon weapon, bool ran)
        => WeaponNativeSession.Offer(WeaponFeature.Fire)?.Guard(static (session, call) =>
        {
            session.ClientFacts.EndShot(call.Ran);
            if (!call.Ran) session.Combat.AbandonFire();
            else if (session.Combat.ResolveFire(call.Weapon) is { } shot) session.CombatFacts.ShotResolved(shot);
        }, (Weapon: weapon, Ran: ran));
}

// The two charge families. A melee charge is the `MWS_ChargeUp` state object's own life: `Enter` starts it,
// `Update` runs it, `Exit` ends it, and `OnChargeupRelease` is the charged attack really being swung. A ranged
// charge is the archetype's own per-frame update, read on both sides of the body so a charge that starts and
// ends inside one update is still seen as both.
[HarmonyPatch(typeof(MWS_ChargeUp), nameof(MWS_ChargeUp.Enter))]
internal static class MeleeChargeStarted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MWS_ChargeUp __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Melee)?.Guard(static (_, charge) => CombatFactsObserver.Current?.MeleeEntered(charge), __instance);
}

[HarmonyPatch(typeof(MWS_ChargeUp), nameof(MWS_ChargeUp.Update))]
internal static class MeleeChargeRan
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MWS_ChargeUp __instance)
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.WeaponMeleeCharge);
        WeaponNativeSession.Offer(WeaponFeature.Melee)?.Guard(static (_, charge) => CombatFactsObserver.Current?.MeleeRan(charge), __instance);
    }
}

[HarmonyPatch(typeof(MWS_ChargeUp), nameof(MWS_ChargeUp.Exit))]
internal static class MeleeChargeEnded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MWS_ChargeUp __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Melee)?.Guard(static (_, charge) => CombatFactsObserver.Current?.MeleeEnded(charge), __instance);
}

[HarmonyPatch(typeof(MWS_ChargeUp), "OnChargeupRelease")]
internal static class MeleeChargeReleased
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MWS_ChargeUp __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Melee)?.Guard(static (_, charge) => CombatFactsObserver.Current?.MeleeReleased(charge), __instance);
}

// The holder's own per-frame body, which is where the sights' state is finished being decided. The state is the
// holder's own `ItemAimTrigger`, not the key that asked for it, so an aim that takes time to come up or that an
// EMP folded away is still seen entering and leaving.
[HarmonyPatch(typeof(FirstPersonItemHolder), "Update")]
internal static class AimStateSampled
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(FirstPersonItemHolder __instance)
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.WeaponAim);
        WeaponNativeSession.Offer(WeaponFeature.Fire)?.Guard(static (_, holder) => CombatFactsObserver.Current?.AimSampled(holder), __instance);
    }
}

// A deployed object is a world instance separate from the backpack life that placed it, and its placement and
// ending hooks live in `WeaponPlacementHooks` beside the kind each family declares.

// The one presentation hook: a holder has finished assembling its parts, which is the only point at which every
// part of this holder exists. It reads the gear's own offline block record and writes the authored absolute local
// pose onto the parts it finds; before that it points this holder's own flashlight part at the flashlight row its document
// names, moves its light mount and hides its vanilla shell, so the poses and added parts that follow are never hidden; it publishes no fact and changes no game state. It fires once per holder, so the first person gear,
// another player's third person copy, a menu preview, an icon render and a deployed sentry each reach it on their own.
internal static class GearPartsSpawned
{
    internal static void Subscribe() => ForgeCore.GameHooks.GearPartsSpawned.After(Spawned);

    private static void Spawned(GearPartHolder holder, bool ran)
        => WeaponNativeSession.Offer(WeaponFeature.Appearance)?.Guard(static (session, holder) =>
        {
            // The block comes from the category as well as the catalogue record: another player's gun has only the category.
            var block = session.Models.BlockOf(holder);
            session.Flashlights.Apply(holder, block);
            session.GearParts.Apply(holder, block);
            session.Models.PartsSpawned(holder);
        }, holder);
}

// The loadout policy's two ends. Neither is an observation: one narrows the pool the game is about to read and one
// projects the list it hands to a picker, and both answer the game's own list in full whenever no policy is in
// force for the loaded rundown. `GetAllGearForSlot` is the only source of a slot's selectable gear — the lobby
// window and the in-level inventory flow both build from it — so the projection covers every consumer at once,
// while the expedition gate keeps a gear already carried from being hidden from the HUD. ForgeCore patches both
// game methods once for every package that limits what players may choose (`ForgeCore.Gear.ProjectOffers`, last in
// the patch order); this package only subscribes, once per process, and the handlers look the live session up on
// every call. A projection that fails leaves the game's own list untouched; the loadout session latches itself off.
internal static class GearOffersForSlot
{
    private static bool _subscribed;

    internal static void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        ForgeCore.Gear.ProjectOffers(WeaponHost.ModId, Offered, Admitted);
    }

    // WHY: P5 — guarded like every other hook, with no closure; the game's list is copied by ForgeCore only for a slot a policy really projects.
    private static IReadOnlyList<GearIDRange>? Offered(InventorySlot slot, IReadOnlyList<GearIDRange> offers)
        => WeaponNativeSession.Offer(WeaponFeature.Inventory)?.Guard(static (live, call) => Project(live.Loadout, call.Slot, call.Offers),
            (Slot: slot, Offers: offers), (IReadOnlyList<GearIDRange>?)null);

    // Saved/predefined choices bypass the picker. Change only this invocation's gear; the native equip chain runs
    // once for an admitted choice. An explicitly empty allowed slot has no native equivalent and is refused here (null).
    private static GearIDRange? Admitted(GearIDRange requested)
    {
        var session = WeaponNativeSession.Offer(WeaponFeature.Inventory);
        return session == null ? requested : session.Guard(static (live, gear) => live.Loadout.Admit(gear), requested, requested);
    }

    internal static IReadOnlyList<GearIDRange>? Project(GearLoadoutSession loadout, InventorySlot slot, IReadOnlyList<GearIDRange> offered)
    {
        try
        {
            if (!loadout.Projects(slot)) return null;
            var projected = loadout.Offer(slot, offered);
            return loadout.Projects(slot) ? projected : null;
        }
        catch (Exception error) { loadout.Fault(error); return null; }
    }

    /// <summary>The game's own returned array read item by item, so the projection works in managed gears.</summary>
    internal static GearIDRange[] Items(IList<GearIDRange> live)
    {
        var items = new GearIDRange[live.Count];
        for (int index = 0; index < items.Length; index++) items[index] = live[index];
        return items;
    }
}
// The one chain that rebuilds a weapon's archetype, which is where an accepted instance override is replayed from
// the ledger. ForgeCore patches it once (`GameHooks.SyncedGearSpawnCompleted`); this runs after the game finished and
// after every other patch of it, which is the order the replay needs: the rebuilt instance carries the game's own
// block again, and the ledger's own absolute field set is written onto the new clone. Nothing is replayed for a
// weapon no override was ever accepted for.
internal static class GearSpawnCompleted
{
    internal static void Subscribe() => ForgeCore.GameHooks.SyncedGearSpawnCompleted.After(Completed);

    private static void Completed(BulletWeaponSynced gun, bool ran)
        => WeaponNativeSession.Offer(WeaponFeature.Appearance)?.Guard(static (session, gear) => session.Overrides.OnGearSpawnComplete(gear), gun);
}

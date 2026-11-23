using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using Gear;
using GameData;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeWeapon.Native;

// The native half of the reload and inventory families: this file turns the real game members into the reads the
// observers decide from (`ReloadNativeReads`) and installs the hooks that call them.
//
// Hooks choose when to read. Request-local rejection snapshots bracket the native body; inventory movements are
// read back after it returned. A requested quantity is never trusted as a committed fact. Native bodies are frozen
// in `evidence/reload-facts.json` and `evidence/inventory-facts.json`.

/// <summary>The one reload and inventory session this process runs, and the hooks' way to reach it. A hook body has
/// no session to close over, so it asks here; both observers are null until <see cref="Install"/> has wired them,
/// which is what keeps a hook that fires before the session is built from doing anything at all.</summary>
internal static partial class ReloadInventoryHooks
{

    private static ReloadObserver? _reload;
    private static InventoryObserver? _inventory;
    private static RuntimeModuleHandle? _observationOwner;
    private static RuntimeSubscriptionGate[] _observationGates = Array.Empty<RuntimeSubscriptionGate>();
    private static long _observationGeneration = -1;
    private static bool _observed;

    internal static bool Observed
    {
        get
        {
            if (_observationOwner is not { } owner) return false;
            var generation = owner.SubscriptionGeneration;
            if (_observationGeneration == generation) return _observed;
            _observed = false;
            foreach (var gate in _observationGates)
                if (gate.HasSubscribers) { _observed = true; break; }
            _observationGeneration = generation;
            return _observed;
        }
    }

    /// <summary>The hook classes this family installs. The package's own hook list carries these beside the ones it
    /// already has, so its single `CreateClassProcessor` loop keeps installing everything.</summary>
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
    {
        typeof(ReloadRequested), typeof(ReloadCommitted),
        typeof(MagazineRefilled), typeof(PoolBulletsUpdated), typeof(PoolDataArrived), typeof(UseSequenceStarted), typeof(SyringeApplied),
        typeof(ResourceUseChecked)
    });

    /// <summary>Builds the two observers over the game's own reads and wires them to this package's identity
    /// session. `identity` is what publishes: these facts belong to the one equipment registration the package
    /// already owns, so there is no second place a fact can leave from, and both families ask the one adapter
    /// whether this machine may observe at all.</summary>
    internal static void Install(IForgeEngine kernel, EquipmentIdentitySession identity,
        EquipmentNativeAdapter adapter, WeaponLog log)
    {
        _observationOwner = identity.Registration;
        _observationGates = new List<RuntimeSubscriptionGate>(identity.Registration.SubscriptionGates().Values).ToArray();
        _observationGeneration = -1;
        var reads = new ReloadNativeReads(adapter);
        // Both observers publish through the identity session itself: these facts belong to the one equipment
        // registration, so there is no second place a fact can leave from.
        var reload = new ReloadObserver(reads, identity.IsCurrent, () => kernel.WorldEpoch,
            () => kernel.CurrentTick, adapter.Authoritative, identity, log);
        _reload = reload;
        _inventory = new InventoryObserver(reads, () => kernel.WorldEpoch,
            () => kernel.CurrentTick, adapter.Authoritative, identity, log);
        adapter.InventorySlotEmptied = _inventory.SlotEmptied;
    }

    /// <summary>Drops both sessions for a disposal. Nothing is published: the machine is going away.</summary>
    internal static void Clear()
    {
        _reload?.Clear();
        _inventory?.Clear();
        _reload = null;
        _inventory = null;
        _observationOwner = null;
        _observationGates = Array.Empty<RuntimeSubscriptionGate>();
        _observationGeneration = -1;
        _observed = false;
    }

    internal static ReloadObserver? Reload => _reload;
    internal static InventoryObserver? Inventory => _inventory;

    internal static string ObservePoolHook(PlayerAmmoStorage storage)
    {
        if (!Observed) return "hook-not-observed";
        var session = WeaponNativeSession.Offer(WeaponFeature.Ammunition);
        if (session == null) return "hook-feature-withdrawn";
        if (_inventory == null) return "hook-no-observer";
        if (storage.m_playerBackpack is not { } pack) return "hook-backpack-missing";
        return session.Guard(static (_, call) => call.Observer.Reconcile(call.Pack),
            (Observer: _inventory, Pack: pack), "hook-guard-rejected");
    }

    /// <summary>The item's own reload flag as a body left it: a client's item reports, the host's copy of it stays silent, everything else is read here.</summary>
    internal static void FlagRead(ReloadObserver reload, ItemEquippable item)
    {
        if (ClientWeaponReport.Current?.ReloadFlag(item, item.IsReloading) != true) reload.FlagChanged(item, item.IsReloading);
    }

    /// <summary>The per-frame hooks this family puts on only when a plan listens to what they report.</summary>
    internal static void EnsureLazyHooks() => _inventory?.EnsureUseHooks();

    /// <summary>Every hook body's front door: the package's own session is asked once and its guard decides —
    /// owning thread, disposal, fault and start state — so a hook that fires with no session, on the wrong thread,
    /// or after the session faulted does nothing at all.</summary>
    internal static void Guard<T>(WeaponFeature feature, Action<ReloadObserver, T> body, T state)
    {
        if (!Observed) return;
        var session = WeaponNativeSession.Offer(feature);
        var reload = _reload;
        if (session == null || reload == null) return;
        session.Guard(static (_, call) => call.Body(call.Observer, call.State), (Body: body, Observer: reload, State: state));
    }

    internal static void GuardInventory<T>(WeaponFeature feature, Action<InventoryObserver, T> body, T state)
    {
        if (!Observed) { HookDropped(feature, "hook-not-observed"); return; }
        var session = WeaponNativeSession.Offer(feature);
        var inventory = _inventory;
        if (session == null || inventory == null) { HookDropped(feature, session == null ? "hook-feature-withdrawn" : "hook-no-observer"); return; }
        session.Guard(static (_, call) => call.Body(call.Observer, call.State), (Body: body, Observer: inventory, State: state));
    }

    /// <summary>A pool or backpack hook arrived and the front door turned it away: trace, on the 1st, 10th, 100th ...
    /// occurrence per feature and reason, so a hook that runs without a fact can be told from one that never ran.</summary>
    internal static void HookDropped(WeaponFeature feature, string reason)
    {
        if (WeaponNativeSession.Current?.Log is { } log && log.Logs(RuntimeLogLevel.Trace))
            log.Skipped(ReloadInventoryContract.AmmoChangedBinding, reason, WeaponFeatures.Label(feature));
    }

    internal static TResult GuardUseObservation<T, TResult>(WeaponFeature feature, Func<T, TResult> body, T state, TResult fallback)
    {
        var session = WeaponNativeSession.Offer(feature);
        if (session == null || _inventory == null) return fallback;
        return session.Guard(static (live, call) =>
        {
            try { return call.Body(call.State); }
            catch (Exception ex)
            {
                live.Log.Text(RuntimeLogLevel.Error, WeaponLogCodes.UseObservationFailed, ex.GetType().Name);
                return call.Fallback;
            }
        }, (Body: body, State: state, Fallback: fallback), fallback);
    }

    /// <summary>The wielded item of a player inventory whose backpack is this one. The chain is the game's own —
    /// backpack to owning player, player to agent, agent to inventory — so no table is kept here and a machine
    /// that holds no agent for that player gets null rather than a stale item.</summary>
    internal static ItemEquippable? Wielded(PlayerBackpack? backpack)
        => EquipmentNativeAdapter.Agent(backpack?.Owner)?.Inventory?.m_wieldedItem;
}

/// <summary>The game reads, answered from the native objects the hooks were handed. Every member re-reads the live
/// object on the call and caches nothing, so a value answered here is the game's value at that moment. A read that
/// throws is answered as the fallback and leaves its fact out, and an item this machine holds no recorded
/// equipment life for answers null rather than a guessed reference.</summary>
internal sealed class ReloadNativeReads
{
    private readonly EquipmentNativeAdapter _adapter;
    private NativeSlot[] _slots = Array.Empty<NativeSlot>();
    private string[] _slotNames = Array.Empty<string>();
    private readonly Dictionary<string, InventoryPoolSample> _pools = new(StringComparer.Ordinal);
    private static readonly InventorySlot[] PoolSlots = (InventorySlot[])Enum.GetValues(typeof(InventorySlot));
    private static readonly string[] PoolNames = Array.ConvertAll(PoolSlots, static slot => slot.ToString());

    internal ReloadNativeReads(EquipmentNativeAdapter adapter)
        => _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    /// <summary>The magazine of a native item. `GetCurrentClip` is the base member every weapon family answers, so
    /// one read covers the magazine families and the pressure-and-recharge tool family alike.</summary>
    public int? Clip(object item)
    {
        try { return Equippable(item)?.GetCurrentClip(); }
        catch (Exception failure) { NativeCallFailure.Note("ReloadInventoryHooks.Clip", failure); return null; }
    }

    // A hook can hand over the base `Item` wrapper of an equippable; only TryCast sees through it.
    private static ItemEquippable? Equippable(object item)
        => item as ItemEquippable ?? (item as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<ItemEquippable>();

    public bool IsReloading(object item)
    {
        try { return Equippable(item) is { IsReloading: true }; }
        catch (Exception failure) { NativeCallFailure.Note("ReloadInventoryHooks.IsReloading", failure); return false; }
    }

    public EntityReference? EquipmentOf(object item)
        => item is Item value ? _adapter.CurrentEntityOf(value) : null;

    public EntityReference? OwnerOf(object item)
        => item is Item value ? _adapter.OwnerOf(value) : null;


    /// <summary>The game's own block id of a native item, read from the block the instance itself was set up with:
    /// `ItemDataBlock.persistentID`, which is the number an authored resource id resolves to. A native `Item` has
    /// no id member of its own — `ItemID` belongs to the slot's `BackpackItem` — and its own block is the game's
    /// declaration of what the instance is, so a block that cannot be read answers null rather than a number.
    /// The identity session's table is keyed by the instance, so nothing else in this package needs the block id.</summary>
    public uint? ItemId(object item)
    {
        try { return item is Item value ? value.ItemDataBlock?.persistentID : null; }
        catch (Exception failure) { NativeCallFailure.Note("ReloadInventoryHooks.ItemId", failure); return null; }
    }

    /// <summary>Pocket items use the native per-id count; consumables use their own slot's BulletsInPack.
    /// Slot identity is checked before reading the pool, so a replacement cannot inherit another item's uses.</summary>
    public int? StackCount(object backpack, uint itemId)
    {
        if (backpack is not PlayerBackpack value) return null;
        try
        {
            var block = ItemDataBlock.GetBlock(itemId);
            if (block == null) return null;
            if (block.inventorySlot == InventorySlot.InPocket) return value.CountPocketItem(itemId);
            if (block.inventorySlot != InventorySlot.Consumable && block.inventorySlot != InventorySlot.ConsumableHeavy) return null;
            IList<BackpackItem?>? slots = value.Slots;
            var index = (int)block.inventorySlot;
            if (slots == null || index >= slots.Count) return null;
            var held = slots[index];
            if (held == null || held.ItemID != itemId) return 0;
            var ammo = value.AmmoStorage?.GetInventorySlotAmmo(block.inventorySlot);
            if (ammo == null) return null;
            var count = (double)ammo.BulletsInPack;
            return double.IsFinite(count) && count >= 0 && count <= int.MaxValue && count == Math.Truncate(count)
                ? (int)count : null;
        }
        catch (Exception failure) { NativeCallFailure.Note("ReloadInventoryHooks.StackCount", failure); return null; }
    }

    /// <summary>The player reference behind a native backpack. The backpack itself names its owning player, so the
    /// read goes through the same domain lookup the equipment adapter already uses for an item's owner — the chain
    /// is backpack to player and player to reference, and nothing is derived from a slot or a name.</summary>
    public EntityReference? OwnerOfBackpack(object backpack)
        => backpack is PlayerBackpack value ? _adapter.OwnerOf(value.Owner) : null;

    public (double X, double Y, double Z)? Position(object item)
    {
        if (item is not Item value) return null;
        Transform? transform;
        try { transform = value.transform; }
        catch (Exception error)
        {
            NativeFault.Note("weapon.inventory.position-transform-read", error);
            return null;
        }
        if (transform == null) return null;
        Vector3 position;
        try { position = transform.position; }
        catch (Exception error)
        {
            NativeFault.Note("weapon.inventory.position-value-read", error);
            return null;
        }
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return null;
        return ((double)position.x, (double)position.y, (double)position.z);
    }

    /// <summary>The slot table as the game reports it right now: one entry per slot, with the item the slot holds
    /// and the equipment life this machine records for that item's instance.</summary>
    public IReadOnlyList<NativeSlot>? Slots(object backpack)
    {
        if (backpack is not PlayerBackpack value) return null;
        // Pool callbacks can arrive before the backpack postfix: finish the identity transition before reading.
        _adapter.Reconcile(value);
        // The table is read through the list interface its own array type already implements: the game's slots are
        // an interop array, and nothing here needs that interop shape — only its entries, in slot order.
        IList<BackpackItem?>? slots;
        try { slots = value.Slots; }
        catch (Exception failure) { NativeCallFailure.Note("ReloadInventoryHooks.Slots", failure); return null; }
        if (slots == null) return null;
        if (_slots.Length != slots.Count)
        {
            _slots = new NativeSlot[slots.Count];
            _slotNames = new string[slots.Count];
            for (var index = 0; index < slots.Count; index++) _slotNames[index] = ((InventorySlot)index).ToString();
        }
        for (var index = 0; index < slots.Count; index++)
        {
            var item = slots[index];
            var instance = item == null ? null : item.Instance;
            _slots[index] = new NativeSlot(_slotNames[index], instance,
                item?.Pointer ?? IntPtr.Zero, instance == null ? IntPtr.Zero : instance.Pointer, _adapter.EntityOf(instance));
        }
        return _slots;
    }

    /// <summary>Every slot's pool as it reads right now. A slot the game reports no pool for is left out, so a pool
    /// that was never seen is not a pool that emptied.</summary>
    public Dictionary<string, InventoryPoolSample>? Pools(object backpack)
    {
        if (backpack is not PlayerBackpack value) return null;
        PlayerAmmoStorage? storage;
        try { storage = value.AmmoStorage; }
        catch (Exception error)
        {
            NativeFault.Note("weapon.inventory.pool-storage-read", error);
            return null;
        }
        if (storage == null) return null;
        _pools.Clear();
        for (var index = 0; index < PoolSlots.Length; index++)
        {
            InventorySlotAmmo? ammo;
            try { ammo = storage.GetInventorySlotAmmo(PoolSlots[index]); }
            catch (Exception error)
            {
                NativeFault.Note("weapon.inventory.pool-slot-read", error);
                continue;
            }
            if (ammo == null) continue;
            InventoryPoolSample sample;
            try { sample = new(ammo.BulletsInPack, ammo.AmmoInPack, ammo.CostOfBullet, UnityEngine.Time.frameCount); }
            catch (Exception error)
            {
                NativeFault.Note("weapon.inventory.pool-count-read", error);
                continue;
            }
            if (!float.IsFinite(sample.Raw) || !float.IsFinite(sample.Cost) || sample.Cost <= 0)
            { ReloadInventoryHooks.HookDropped(WeaponFeature.Ammunition, "pool-sample-invalid"); continue; }
            _pools[PoolNames[index]] = sample;
        }
        return _pools;
    }
}

// The reload entry point. It opens a life only when the item's own flag reads true right after, so the observer,
// not this hook, decides whether a reload is under way.
[HarmonyPatch(typeof(PlayerInventoryBase), nameof(PlayerInventoryBase.TriggerReload))]
internal static class ReloadRequested
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventoryBase __instance, bool __runOriginal)
    {
        if (!__runOriginal) return;
        ReloadInventoryHooks.Guard(WeaponFeature.Reload, static (reload, inventory) => { if (!ClientWeaponReport.NotOurs(inventory.m_wieldedItem)) reload.Requested(inventory.m_wieldedItem); }, __instance);
    }
}

// The reload body. The magazine is read back after it ran, which is where the rounds that entered the magazine are
// visible; the life recorded its baseline when it opened, so a gain over that baseline is the transfer.
[HarmonyPatch(typeof(PlayerInventoryBase), nameof(PlayerInventoryBase.DoReload))]
internal static class ReloadCommitted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventoryBase __instance, bool __runOriginal)
    {
        if (!__runOriginal) return;
        ReloadInventoryHooks.Guard(WeaponFeature.Reload, static (reload, inventory) => { if (!ClientWeaponReport.NotOurs(inventory.m_wieldedItem)) reload.MagazineRead(inventory.m_wieldedItem); }, __instance);
    }
}

// The reload's animation start. The item's flag is set on this body's own path, after the reload-time read, and the
// observer reads the flag as the body left it: a rising edge opens the life, and an unchanged value changes nothing.
// The body is unique in the game build and is entered only from the reload entry points.
// ForgeCore patches the body once (`GameHooks.ReloadTriggered`); the flag is read after every other patch of it.
internal static class ReloadStartedFlag
{
    internal static void Subscribe() => ForgeCore.GameHooks.ReloadTriggered.After(Triggered);

    private static void Triggered(ItemEquippable item, bool ran)
    {
        if (!ran) return;
        ReloadInventoryHooks.Guard(WeaponFeature.ReloadState, static (reload, item) => ReloadInventoryHooks.FlagRead(reload, item), item);
    }
}

// The reload's end: the animation's completion callback clears the flag. ForgeCore patches the closure once
// (`GameHooks.ReloadDone`); the flag is read after every other patch of it.
internal static class ReloadEndedFlag
{
    internal static void Subscribe() => ForgeCore.GameHooks.ReloadDone.After(Ended);

    private static void Ended(ItemEquippable __instance, bool __runOriginal)
    {
        if (!__runOriginal) return;
        ReloadInventoryHooks.Guard(WeaponFeature.ReloadState, static (reload, item) => ReloadInventoryHooks.FlagRead(reload, item), __instance);
        // The pool and magazine movement of a reload is reconciled at its end as well: the slot-clip write hook has
        // not been seen to run in a real reload, and this callback has.
        if (__instance.IsReloading || !ReloadInventoryHooks.Observed) return;
        var agent = __instance.Owner;
        var player = agent == null ? null : agent.Owner;
        if (player == null || !PlayerBackpackManager.TryGetBackpack(player, out var backpack) || backpack == null) return;
        ReloadInventoryHooks.GuardInventory(WeaponFeature.Ammunition, static (inventory, pack) => inventory.Reconcile(pack), backpack);
    }
}

// The base wield body also writes the flag; `EquipmentWielded` (WeaponFieldHooks.cs) reads the value it left after the body ran.

// The base unwield body clears the flag. An override that does not call it leaves the flag as it is, and the observer
// reads the value the body left, so nothing is reported for that path. ForgeCore patches the body once
// (`GameHooks.ItemUnwielded`); the flag is read after every other patch of it.
internal static class ReloadUnwieldedFlag
{
    internal static void Subscribe() => ForgeCore.GameHooks.ItemUnwielded.After(Unwielded);

    private static void Unwielded(ItemEquippable item, bool ran)
    {
        if (!ran) return;
        ReloadInventoryHooks.Guard(WeaponFeature.ReloadState, static (reload, item) => ReloadInventoryHooks.FlagRead(reload, item), item);
    }
}

// A slot's clip was written, which is where a reload actually moves rounds from the pack into the magazine. The
// magazine of the inventory that owns this pool is read, and the same readback reconciles the backpack's slots and
// pools: one readback, and the two families decide between them which of them owns each movement.
// The one-argument overload is named explicitly: this build declares a second `SetClipAmmoInSlot` that takes the
// slot, the ammo type and the slot's own pool entry, and a patch declared by name alone would not say which of the
// two bodies it brackets.
[HarmonyPatch(typeof(PlayerAmmoStorage), nameof(PlayerAmmoStorage.SetClipAmmoInSlot), new[] { typeof(InventorySlot) })]
internal static class MagazineRefilled
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerAmmoStorage __instance, InventorySlot slot, bool __runOriginal)
    {
        if (!__runOriginal) return;
        if (!ReloadInventoryHooks.Observed) return;
        var backpack = __instance.m_playerBackpack;
        if (backpack == null) return;
        var wielded = ReloadInventoryHooks.Wielded(backpack);
        if (wielded != null) ReloadInventoryHooks.Guard(WeaponFeature.Ammunition, static (reload, item) => { if (!ClientWeaponReport.NotOurs(item)) reload.MagazineRead(item); }, wielded);
        ReloadInventoryHooks.GuardInventory(WeaponFeature.Ammunition, static (inventory, pack) => inventory.Reconcile(pack), backpack);
    }
}

// The pool's own bullet count being updated. This is the body every player action that moves rounds in or out of
// a pack reaches — a throw's cost, a placement's cost, a resource pack's delivery, a barrier's or a mine's pickup
// (fourteen call sites in this build, evidence/inventory-change-facts.json) — and it is the closest safe stand-in
// for `InventorySlotAmmo.OnBulletsUpdateCallback`, which is the pool's own ready-made callback and a field this
// package must not take over: the game may have wired it, and overwriting it would be a write to native state.
// The postfix only triggers the same readback the other pool hooks trigger, so a movement two of them see is
// recorded once — the readback compares against the last value it published rather than counting calls.
[HarmonyPatch(typeof(PlayerAmmoStorage), nameof(PlayerAmmoStorage.UpdateBulletsInPack))]
internal static class PoolBulletsUpdated
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerAmmoStorage __instance, bool __runOriginal)
    {
        if (!__runOriginal) return;
        ReloadInventoryHooks.HookDropped(WeaponFeature.Ammunition, "hook-reached");
        ReloadInventoryHooks.ObservePoolHook(__instance);
    }
}

// Another machine's pool arrived as storage data. It is the same readback as any other pool write, so the pool that
// grew is published as the fact it is rather than as a second, network-specific one.
[HarmonyPatch(typeof(PlayerAmmoStorage), nameof(PlayerAmmoStorage.SetStorageData))]
internal static class PoolDataArrived
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerAmmoStorage __instance, bool __runOriginal)
    {
        if (!__runOriginal) return;
        if (!ReloadInventoryHooks.Observed) return;
        var backpack = __instance.m_playerBackpack;
        if (backpack != null) ReloadInventoryHooks.GuardInventory(WeaponFeature.Ammunition, static (inventory, pack) => inventory.Reconcile(pack), backpack);
    }
}

// The sequence helper always returns null in this build. Its stored coroutine is the actual start evidence.
[HarmonyPatch(typeof(ItemEquippable), "TryStartAnimationSequenceCoroutine")]
internal static class UseSequenceStarted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ItemEquippable __instance, bool __runOriginal)
    {
        if (!__runOriginal) return;
        if (!ReloadInventoryHooks.Observed) return;
        if (__instance.m_animationSequenceCoroutine != null)
            ReloadInventoryHooks.GuardInventory(WeaponFeature.Equipment, static (inventory, item) => { if (ClientWeaponReport.Current?.UseStarted(item, tracked: false) != true) inventory.UseStarted(item, tracked: false); }, __instance);
    }
}

// A syringe never reaches the sequence helper above: it runs its own `ApplySyringe` coroutine, whose first resume is the
// application starting. The factory call that builds the coroutine is not hooked because the build can inline it into
// its caller; the state machine's own body is the entry (evidence/w1-native-world-paths.json lists it as the syringe
// consumption path). State 0 is the coroutine's first resume. The use is closed at once because the coroutine's end says
// nothing this row reports (it publishes refusals, not outcomes) and the syringe is retired by the application itself.
[HarmonyPatch(typeof(SyringeFirstPerson._ApplySyringe_d__18), nameof(SyringeFirstPerson._ApplySyringe_d__18.MoveNext))]
internal static class SyringeApplied
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Prefix(SyringeFirstPerson._ApplySyringe_d__18 __instance)
    {
        if (__instance.__1__state != 0) return;
        if (!ReloadInventoryHooks.Observed) return;
        ReloadInventoryHooks.GuardInventory(WeaponFeature.Equipment, static (inventory, item) =>
        {
            if (ClientWeaponReport.Current is { } report && report.UseStarted(item)) { report.UseEnded(item); return; }
            inventory.UseStarted(item); inventory.UseEnded(item);
        }, __instance.__4__this);
        // The application starting is also the use taking effect.
        ReloadInventoryHooks.GuardInventory(WeaponFeature.Equipment, static (inventory, item) =>
        {
            if (ClientWeaponReport.Current?.UseCompleted(item) != true) inventory.UseCompleted(item);
        }, __instance.__4__this);
    }
}

// A resource pack takes effect in `ApplyPack`, which the pack's timed hold reaches only when it completes: a hold released early
// publishes `use_started` and never gets here. Read at the start of the body, like the syringe, because the pack can be retired inside it.
// ForgeCore patches the body once (`GameHooks.ResourcePackApplied`); this reads before the body, after every other patch of it.
internal static class ResourceApplied
{
    internal static void Subscribe() => ForgeCore.GameHooks.ResourcePackApplied.Before(Applied);

    private static void Applied(ResourcePackFirstPerson __instance)
    {
        if (!ReloadInventoryHooks.Observed) return;
        ReloadInventoryHooks.GuardInventory(WeaponFeature.Equipment, static (inventory, item) =>
        {
            if (ClientWeaponReport.Current?.UseCompleted(item) != true) inventory.UseCompleted(item);
        }, __instance);
    }
}

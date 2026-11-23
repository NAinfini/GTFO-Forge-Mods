using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeWeapon.Native;

// One native read: keep raw reserve and native whole rounds together without changing the event's round unit.
internal readonly record struct InventoryPoolSample(long Value, float Raw, float Cost, int Frame);

/// <summary>What one slot held at one moment. The item and instance identities are what a slot change is measured
/// against; the equipment reference, the world position and the block id are resolved while the native object is
/// still there, because a clear destroys the instance and nothing about the item is readable afterwards — and the
/// count row needs the block id of a slot that is empty by the time the change is published.</summary>
internal readonly record struct SlotState(IntPtr Item, IntPtr Instance, EntityReference? Equipment, (double X, double Y, double Z)? Position,
    uint? ItemId);

/// <summary>The seven rows decided from one readback of a player's slot table and pools after a native body
/// returned:
///
/// `picked_up` and `dropped` are one slot's occupancy becoming and ceasing to be, so a single slot change produces
/// at most one of them. The drop reports the item the slot no longer holds, from the identity and the position this
/// machine resolved while that item was still readable; evidence `evidence/inventory-facts.json` records why the
/// row is defined as "left the backpack" and why its position port is optional.
///
/// `stack_changed` compares actual per-item counts, including consumable uses when the same instance stays in its
/// slot. Pocket slots sharing an item id publish one change; unreadable counts never become fabricated zeroes.
///
/// `carried_item_changed` is the carry slot's own row: the large item a player hauls through the level changing.
/// Its `item` port is the family's one nullable port, so the slot emptying is answered with a null item.
///
/// `ammo_changed` is the pool readback moving either way: the rounds the pool holds now, the amount it moved by,
/// the pool itself as the resource reference this provider owns, and the reason. It is published for every pool
/// movement the readback sees, including the ones a reload explains, because the pool really did change. The
/// reason is decided by the body that triggered the readback, never guessed from the numbers alone: a loss is
/// `consume`, a gain read after the backpack stored an item is `pickup`, and any other gain is `refill`.
///
/// `use_started` is the item's own use sequence starting on a non-reload use. The sequence entry point is the one
/// every use animation goes through, including the reload sequence, so the reload flag is read first and a reload
/// publishes no use fact: the reload rows already carry that start.
///
/// `use_completed` is a use that took effect: a resource pack's apply body or a syringe's application started, or a throwable left
/// the hand. A use cancelled before that (the pack's timed hold released early) publishes `use_started` and never this row. It is read at
/// the start of the native body that performs the effect, while the item is still the player's.
///
/// `use_failed` is a use the game refused, reported with the gate's own reason as a `use_failure_reason` member. Native
/// gates include throw Update rejection branches and the resource pack's need-resource gate.
/// A shot refused for an empty magazine is `forge.trigger.combat.dry_fire` and is never published here.
/// Native branches and fresh-input provenance are recorded in evidence/item-use-failures.json.</summary>
internal sealed class InventoryObserver
{
    /// <summary>The one slot a large item is carried in, spelled the way the slot read spells every slot name. The
    /// name is the only handle this game-independent half has on that slot: which game value it is stays in the
    /// native read, which enumerates the game's own slot values.</summary>
    internal const string CarriedSlotName = "InLevelCarry";
    // The enum ports of this family travel as the member's index in the set they name (the kernel refuses a member
    // name as `invalid-enum`), so each index is resolved once from the SDK's own table.
    private static readonly int RefillMember = WeaponEnumMembers.Index("ammo_change_reason", ReloadInventoryContract.RefillReason);
    private static readonly int PickupMember = WeaponEnumMembers.Index("ammo_change_reason", ReloadInventoryContract.PickupReason);
    private static readonly int ConsumeMember = WeaponEnumMembers.Index("ammo_change_reason", ReloadInventoryContract.ConsumeReason);

    /// <summary>One player's last readback, keyed by the owner reference so a slot the game renumbers shifts
    /// nothing else in the table.</summary>
    private sealed class Player
    {
        internal Player(EntityReference owner) => Owner = owner;
        internal EntityReference Owner { get; }
        internal Dictionary<string, SlotState> Slots { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, PoolState> Pools { get; } = new(StringComparer.Ordinal);
        /// <summary>Pools that grew while their slot had no equipment to name yet; true when a store caused the growth.</summary>
        internal Dictionary<string, bool> Deferred { get; } = new(StringComparer.Ordinal);
        internal Dictionary<uint, int?> Counts { get; } = new();
        internal HashSet<uint> SampledCounts { get; } = new();
    }

    private sealed class PoolState(InventoryPoolSample sample)
    {
        internal long Value = sample.Value;
        internal InventoryPoolSample Sample = sample;
        internal PoolTrace Trace;
    }

    private readonly record struct PoolTrace(string Reason, EntityReference? Equipment, long Before, long Value, long Count,
        string? DispatchStatus = null, string? DispatchCode = null);

    private readonly ReloadNativeReads _native;
    private readonly Func<long> _world, _tick;
    private readonly Func<bool> _authoritative;
    private readonly EquipmentIdentitySession _identity;
    // WHY: P5 — each row's gate, taken once at construction, read before the event value is built.
    private readonly RuntimeSubscriptionGate _pickedUpGate, _droppedGate, _carriedGate, _stackGate, _ammoGate, _useStartedGate,
        _useCompletedGate, _useFailedGate, _throwReleasedGate;
    private readonly WeaponLog _log;
    private readonly Dictionary<string, Player> _players = new(StringComparer.Ordinal);
    private readonly HashSet<string> _uses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _refusalFrames = new(StringComparer.Ordinal);
    private long _epoch = -1, _sequence;

    internal InventoryObserver(ReloadNativeReads native, Func<long> worldEpoch, Func<long> tick, Func<bool> authoritative,
        EquipmentIdentitySession identity, WeaponLog log)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _world = worldEpoch ?? throw new ArgumentNullException(nameof(worldEpoch));
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        _authoritative = authoritative ?? throw new ArgumentNullException(nameof(authoritative));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _pickedUpGate = identity.GateOf(ReloadInventoryContract.PickedUpBinding);
        _droppedGate = identity.GateOf(ReloadInventoryContract.DroppedBinding);
        _carriedGate = identity.GateOf(ReloadInventoryContract.CarriedItemChangedBinding);
        _stackGate = identity.GateOf(ReloadInventoryContract.StackChangedBinding);
        _ammoGate = identity.GateOf(ReloadInventoryContract.AmmoChangedBinding);
        _useStartedGate = identity.GateOf(ReloadInventoryContract.UseStartedBinding);
        _useCompletedGate = identity.GateOf(ReloadInventoryContract.UseCompletedBinding);
        _useFailedGate = identity.GateOf(ReloadInventoryContract.UseFailedBinding);
        _throwReleasedGate = identity.GateOf(EquipmentEventContract.ThrowReleasedBinding);
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    private bool _throwUseAsked;

    /// <summary>Puts on the throwing weapon's per-frame update hook the first tick a plan listens to refused uses (a
    /// gate with no subscriber table entry is open). Called on every advanced tick: one boolean until asked once.</summary>
    internal void EnsureUseHooks()
    {
        if (_throwUseAsked || !_useFailedGate.HasSubscribers && !_throwReleasedGate.HasSubscribers) return;
        _throwUseAsked = true;
        WeaponLazyHooks.Ensure(WeaponLazyHooks.Unit.ThrowUse);
    }

    /// <summary>One player's slots, pools and wielded slot read back after a native body returned. The first
    /// sighting of a player records the tables without publishing anything: a backpack that was never seen did not
    /// change, and a fact for it would report a pickup nobody made.</summary>
    internal string Reconcile(object backpack, bool stored = false)
    {
        if (backpack == null) return "backpack-missing";
        if (!Enter()) { _log.Skipped(ReloadInventoryContract.AmmoChangedBinding, "reconcile-not-authoritative"); return "reconcile-not-authoritative"; }
        var owner = _native.OwnerOfBackpack(backpack);
        if (owner == null) { _log.Skipped(ReloadInventoryContract.AmmoChangedBinding, "reconcile-owner-unresolved"); return "reconcile-owner-unresolved"; }
        var slots = _native.Slots(backpack);
        if (slots == null) { _log.Skipped(ReloadInventoryContract.AmmoChangedBinding, "reconcile-slots-unreadable"); return "reconcile-slots-unreadable"; }
        if (!_players.TryGetValue(owner.Id, out var state) || state.Owner != owner)
        {
            if (state != null) FlushPoolTraces(state);
            _log.Skipped(ReloadInventoryContract.AmmoChangedBinding, "player-baseline", owner.Id);
            _players[owner.Id] = state = new Player(owner);
            Record(backpack, slots, state, first: true);
        }
        else
        {
            Record(backpack, slots, state, first: false);
        }
        Pools(backpack, slots, state, stored);
        return "observer-returned";
    }

    /// <summary>The adapter observed an empty slot, but has not ended its recorded equipment life yet.
    /// Consume that exact prior occupancy now; later readbacks must not publish the same drop again.</summary>
    internal void SlotEmptied(object backpack, string name, EntityReference equipment)
    {
        if (!Enter() || equipment.WorldEpoch != _epoch) return;
        var owner = _native.OwnerOfBackpack(backpack);
        if (owner == null || !_players.TryGetValue(owner.Id, out var player) || player.Owner != owner
            || !player.Slots.TryGetValue(name, out var prior) || prior.Equipment != equipment) return;
        player.Slots.Remove(name);
        Slot(backpack, name, prior, null, player);
        player.SampledCounts.Clear();
        Count(backpack, player, equipment, prior.ItemId, first: false);
    }

    private void Record(object backpack, IReadOnlyList<NativeSlot> slots, Player state, bool first)
    {
        state.SampledCounts.Clear();
        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            var before = state.Slots.TryGetValue(slot.Name, out var previous) ? previous : (SlotState?)null;
            var now = slot.Item == null
                ? (SlotState?)null
                : new SlotState(slot.ItemIdentity, slot.InstanceIdentity, slot.Equipment,
                    _native.Position(slot.Item), _native.ItemId(slot.Item));
            if (now == null) state.Slots.Remove(slot.Name); else state.Slots[slot.Name] = now.Value;
            if (!first) Slot(backpack, slot.Name, before, now, state);
            if (now is { } current) Count(backpack, state, current.Equipment, current.ItemId, first);
            if (before is { } prior) Count(backpack, state, prior.Equipment, prior.ItemId, first);
        }
    }

    /// <summary>One slot's occupancy change: a pickup or a drop. Counts are read separately for every item id,
    /// including unchanged slots. The carry slot's fact uses these same two occupancy readings.</summary>
    private void Slot(object backpack, string name, SlotState? before, SlotState? now, Player player)
    {
        // The carry slot's own row is decided before the occupancy branches below, which return: a large item that
        // entered or left that slot is still both a pickup fact and a carried-item fact.
        if (name == CarriedSlotName) Carried(player, before, now);
        if (before == null && now is { } current)
        {
            if (current.Equipment == null)
            {
                Unpublished("equipment-unresolved", ReloadInventoryContract.PickedUpBinding, name);
                return;
            }
            if (!_pickedUpGate.HasSubscribers) return;
            var id = Id();
            if (Begin(_pickedUpGate, id, current.Equipment.Id, out _) is not { } fact) return;
            fact.Values.Entity(Blocks.TriggerEquipmentPickedUp.OutActor, player.Owner);
            fact.Values.Entity(Blocks.TriggerEquipmentPickedUp.OutItem, current.Equipment);
            Raise(fact, _pickedUpGate, id, EntityScope.Equipment.Of(current.Equipment), current.Equipment.Id);
            return;
        }
        if (before is { } prior && now == null)
        {
            if (prior.Equipment == null)
            {
                Unpublished("equipment-unresolved", ReloadInventoryContract.DroppedBinding, name);
                return;
            }
            if (!_droppedGate.HasSubscribers) return;
            var id = Id();
            if (Begin(_droppedGate, id, prior.Equipment.Id, out _) is not { } fact) return;
            fact.Values.Entity(Blocks.TriggerEquipmentDropped.OutActor, player.Owner);
            fact.Values.Entity(Blocks.TriggerEquipmentDropped.OutItem, prior.Equipment);
            // An unread place is left out, never written null.
            if (prior.Position is { } point) fact.Values.Vector(Blocks.TriggerEquipmentDropped.OutPosition, point);
            Raise(fact, _droppedGate, id, EntityScope.Equipment.Of(prior.Equipment), prior.Equipment.Id);
            return;
        }
    }

    /// <summary>The carried-item row: the one slot a player hauls a large item in, published when the item that
    /// slot holds changes. The row's `item` port is the one nullable port in this family, so a slot that emptied is
    /// an answer with a null item rather than a refusal; a slot that gained an item this machine cannot name
    /// publishes nothing and reports it, exactly like a pickup. A change of instance under the same reference is
    /// not a change of item and publishes nothing.</summary>
    private void Carried(Player player, SlotState? before, SlotState? now)
    {
        var from = before?.Equipment;
        var to = now?.Equipment;
        if (from == to) return;
        if (now != null && to == null)
        {
            Unpublished("equipment-unresolved", ReloadInventoryContract.CarriedItemChangedBinding, CarriedSlotName);
            return;
        }
        if (!_carriedGate.HasSubscribers) return;
        var id = Id();
        var subject = to?.Id ?? player.Owner.Id;
        if (Begin(_carriedGate, id, subject, out _) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerPlayerCarriedItemChanged.OutPlayer, player.Owner);
        fact.Values.Entity(Blocks.TriggerPlayerCarriedItemChanged.OutItem, to);
        Raise(fact, _carriedGate, id, EntityScope.Player.Of(player.Owner), subject);
    }

    /// <summary>Read each item id once and compare actual counts. The first observation establishes a baseline;
    /// a failed read breaks it, so recovery cannot manufacture a change across an unknown interval.</summary>
    private void Count(object backpack, Player player, EntityReference? equipment, uint? itemId, bool first)
    {
        if (itemId == null || !player.SampledCounts.Add(itemId.Value)) return;
        var known = player.Counts.TryGetValue(itemId.Value, out var before);
        var count = _native.StackCount(backpack, itemId.Value);
        player.Counts[itemId.Value] = count;
        if (first || count == null || known && before == null || equipment == null) return;
        var delta = (long)count.Value - (before ?? 0);
        if (delta == 0) return;
        if (!_stackGate.HasSubscribers) return;
        var id = Id();
        if (Begin(_stackGate, id, equipment.Id, out _) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerEquipmentStackChanged.OutActor, player.Owner);
        fact.Values.Entity(Blocks.TriggerEquipmentStackChanged.OutItem, equipment);
        fact.Values.Integer(Blocks.TriggerEquipmentStackChanged.OutCount, count.Value);
        fact.Values.Integer(Blocks.TriggerEquipmentStackChanged.OutDelta, delta);
        Raise(fact, _stackGate, id, EntityScope.Equipment.Of(equipment), equipment.Id);
    }

    /// <summary>Every pool the player holds, read back after a body that could have written one. A movement in
    /// either direction is the ammunition row's, published for every pool the readback saw move, with the reason
    /// the triggering body gives it: <paramref name="stored"/> is true when the readback follows the backpack
    /// storing an item.</summary>
    private void Pools(object backpack, IReadOnlyList<NativeSlot> slots, Player player, bool stored)
    {
        var pools = _native.Pools(backpack);
        if (pools == null) { _log.Skipped(ReloadInventoryContract.AmmoChangedBinding, "pools-unreadable"); return; }
        foreach (var pool in pools)
        {
            if (!player.Pools.TryGetValue(pool.Key, out var state))
            {
                player.Pools[pool.Key] = state = new(pool.Value);
                if (_log.Logs(RuntimeLogLevel.Trace))
                    TracePool(player, pool.Key, state, "baseline", Equipment(player, pool.Key), pool.Value.Value, pool.Value.Value);
                _log.Skipped(ReloadInventoryContract.AmmoChangedBinding, "pool-baseline", pool.Key);
                continue;
            }
            var before = state.Value;
            var rawBefore = state.Sample.Raw;
            state.Sample = pool.Value;
            var delta = pool.Value.Value - before;
            if (delta == 0)
            {
                if (_log.Logs(RuntimeLogLevel.Trace))
                    TracePool(player, pool.Key, state, rawBefore != pool.Value.Raw ? "raw-changed-whole-unchanged" : "unchanged",
                        Equipment(player, pool.Key), before, pool.Value.Value);
                continue;
            }
            var deferred = player.Deferred.GetValueOrDefault(pool.Key);
            if (Equipment(player, pool.Key) is not { } equipment)
            {
                // A pool that grew before its slot's item is nameable (the backpack stores the rounds a moment before the slot
                // shows the item) keeps its old baseline, so the readback that sees the equipment publishes the whole movement.
                // A pool that fell to an empty slot moved because the item left; the drop is that fact.
                if (delta > 0)
                {
                    player.Deferred[pool.Key] = deferred || stored;
                    TracePool(player, pool.Key, state, "equipment-pending", null, before, pool.Value.Value);
                    _log.Skipped(ReloadInventoryContract.AmmoChangedBinding, "equipment-pending", pool.Key);
                    continue;
                }
                if (player.Slots.ContainsKey(pool.Key))
                {
                    state.Value = pool.Value.Value;
                    TracePool(player, pool.Key, state, "equipment-unresolved", null, before, pool.Value.Value);
                    Unpublished("equipment-unresolved", ReloadInventoryContract.AmmoChangedBinding, pool.Key);
                    continue;
                }
                state.Value = pool.Value.Value; player.Deferred.Remove(pool.Key);
                TracePool(player, pool.Key, state, "slot-empty", null, before, pool.Value.Value);
                _log.Skipped(ReloadInventoryContract.AmmoChangedBinding, "slot-empty", pool.Key);
                continue;
            }
            state.Value = pool.Value.Value; player.Deferred.Remove(pool.Key);
            // `ammo_changed` is about an equipment's magazine: a tool or device that reports none is not that fact.
            // The slot table this reconcile already read; the item is looked up in it, not read again.
            if (ItemIn(slots, pool.Key) is { } held && _native.Clip(held) == null)
            { _log.Skipped(ReloadInventoryContract.AmmoChangedBinding, "no-magazine", pool.Key); continue; }
            var reason = delta < 0 ? ConsumeMember : stored || deferred ? PickupMember : RefillMember;
            AmmoChanged(player, pool.Key, state, equipment, pool.Value.Value, delta, reason);
        }
    }

    private static object? ItemIn(IReadOnlyList<NativeSlot> slots, string name)
    {
        for (var index = 0; index < slots.Count; index++) if (slots[index].Name == name) return slots[index].Item;
        return null;
    }

    /// <summary>One pool moved. The pool the readback decided it from names the resource this provider owns, the
    /// equipment is the life that slot holds, and the two numbers are the readback's own: the rounds the pool
    /// holds now and the amount it moved by, so a drain and a refill are one row with opposite signs, and the
    /// reason the readback's own trigger gives the movement. A movement
    /// this machine cannot name is reported and not published — the row's `equipment` and `pool` ports are both
    /// required, and a fact naming no equipment or no pool would be a fact a plan cannot act on.</summary>
    private void AmmoChanged(Player player, string slot, PoolState state, EntityReference equipment, long value, long delta, int reason)
    {
        var pool = ReloadInventoryContract.PoolReference(slot);
        if (pool == null)
        {
            TracePool(player, slot, state, "pool-unknown", equipment, value - delta, value);
            Unpublished("pool-unknown", ReloadInventoryContract.AmmoChangedBinding, slot);
            return;
        }
        if (!_ammoGate.HasSubscribers)
        { TracePool(player, slot, state, "no-consumer", equipment, value - delta, value); _log.Skipped(ReloadInventoryContract.AmmoChangedBinding, "no-consumer", slot); return; }
        var id = Id();
        DispatchResult answer;
        if (Begin(_ammoGate, id, equipment.Id, out var refused) is not { } fact) answer = refused!;
        else
        {
            fact.Values.Entity(Blocks.TriggerCombatAmmoChanged.OutActor, player.Owner);
            fact.Values.Entity(Blocks.TriggerCombatAmmoChanged.OutEquipment, equipment);
            fact.Values.Resource(Blocks.TriggerCombatAmmoChanged.OutPool, pool);
            fact.Values.Integer(Blocks.TriggerCombatAmmoChanged.OutValue, value);
            fact.Values.Integer(Blocks.TriggerCombatAmmoChanged.OutDelta, delta);
            fact.Values.Enum(Blocks.TriggerCombatAmmoChanged.OutReason, reason);
            answer = Raise(fact, _ammoGate, id, EntityScope.Equipment.Of(equipment), equipment.Id);
        }
        TracePool(player, slot, state, "dispatch", equipment, value - delta, value, answer.Status, answer.Code);
    }

    private void TracePool(Player player, string slot, PoolState state, string reason, EntityReference? equipment, long before, long value,
        string? status = null, string? code = null)
    {
        if (!_log.Logs(RuntimeLogLevel.Trace)) return;

        var prior = state.Trace;
        bool same = prior.Reason == reason && prior.Equipment == equipment && prior.DispatchStatus == status && prior.DispatchCode == code;
        if (!same && prior.Count != 0) WritePoolTrace(player, slot, prior, "terminal");
        var trace = new PoolTrace(reason, equipment, before, value, same ? prior.Count + 1 : 1, status, code);
        state.Trace = trace;
        long count = trace.Count;
        while (count > 1 && count % 10 == 0) count /= 10;
        if (count == 1) WritePoolTrace(player, slot, trace, "sample");
    }

    private void WritePoolTrace(Player player, string slot, PoolTrace trace, string stage)
    {
        _log.Write(RuntimeLogLevel.Trace, "weapon.supply-trace", new RuntimeLogFacts
        {
            Entity = player.Owner.Id,
            Binding = ReloadInventoryContract.AmmoChangedBinding,
            Values = new[]
            {
                RuntimeLogValue.Of("phase", "inventory-pool-publication"), RuntimeLogValue.Of("stage", stage),
                RuntimeLogValue.Of("reason", trace.Reason), RuntimeLogValue.Of("count", trace.Count), RuntimeLogValue.Of("slot", slot),
                RuntimeLogValue.Of("ownerWorldEpoch", player.Owner.WorldEpoch), RuntimeLogValue.Of("ownerLifeEpoch", player.Owner.LifeEpoch),
                RuntimeLogValue.Of("equipment", trace.Equipment?.Id ?? "unresolved"),
                RuntimeLogValue.Of("equipmentWorldEpoch", trace.Equipment?.WorldEpoch ?? -1),
                RuntimeLogValue.Of("equipmentLifeEpoch", trace.Equipment?.LifeEpoch ?? -1),
                RuntimeLogValue.Of("before", trace.Before), RuntimeLogValue.Of("value", trace.Value),
                RuntimeLogValue.Of("delta", trace.Value - trace.Before),
                RuntimeLogValue.Of("dispatchStatus", trace.DispatchStatus ?? "not-submitted"),
                RuntimeLogValue.Of("dispatchCode", trace.DispatchCode ?? trace.Reason)
            }
        });
    }

    private void FlushPoolTraces(Player player)
    {
        if (!_log.Logs(RuntimeLogLevel.Trace)) return;
        foreach (var pool in player.Pools)
            if (pool.Value.Trace.Count != 0) WritePoolTrace(player, pool.Key, pool.Value.Trace, "terminal");
    }

    /// <summary>The equipment the slot held at the last readback, which is what a pool that grew belongs to.</summary>
    private static EntityReference? Equipment(Player player, string slot)
        => player.Slots.TryGetValue(slot, out var state) ? state.Equipment : null;

    /// <summary>An item's use sequence started. A reload sequence reaches the same entry point, so the reload flag
    /// is read first: a reload's start is the reload rows' and is never republished as a use. A tracked use stays open
    /// until <see cref="UseEnded"/> (a throwable's charge); a sequence entry is a start of its own, so it only yields
    /// to a tracked use already open and leaves nothing behind that would silence the next one.</summary>
    /// <param name="reporter">The player whose client reported the use (the host reads the item from that player's backpack):
    /// the actor even while the item's own owner field is not set yet, as at the start of a level. The same for the two below.</param>
    internal void UseStarted(object item, bool tracked = true, EntityReference? reporter = null)
    {
        if (!Enter() || _native.IsReloading(item)) return;
        var equipment = _native.EquipmentOf(item);
        if (equipment == null || (tracked ? !_uses.Add(equipment.Id) : _uses.Contains(equipment.Id))) return;
        if (!_useStartedGate.HasSubscribers) return;
        var id = Id();
        var actor = reporter ?? _native.OwnerOf(item);
        if (Begin(_useStartedGate, id, equipment.Id, out _) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerEquipmentUseStarted.OutActor, actor);
        fact.Values.Entity(Blocks.TriggerEquipmentUseStarted.OutEquipment, equipment);
        Raise(fact, _useStartedGate, id, EntityScope.Equipment.Of(equipment), equipment.Id);
    }

    /// <summary>An item's use took effect. Published from the body that applies it, so a cancelled use never reaches here. The use's open
    /// entry is left to its own end, because a throwable's charge outlives its release.</summary>
    internal void UseCompleted(object item, EntityReference? reporter = null)
    {
        if (!Enter()) return;
        var equipment = _native.EquipmentOf(item);
        if (equipment == null || !_useCompletedGate.HasSubscribers) return;
        var id = Id();
        var actor = reporter ?? _native.OwnerOf(item);
        if (Begin(_useCompletedGate, id, equipment.Id, out _) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerEquipmentUseCompleted.OutActor, actor);
        fact.Values.Entity(Blocks.TriggerEquipmentUseCompleted.OutEquipment, equipment);
        Raise(fact, _useCompletedGate, id, EntityScope.Equipment.Of(equipment), equipment.Id);
    }

    /// <summary>A use sequence that had started is over. This row reports refusals, not outcomes, so an ended use
    /// publishes nothing: nothing read here says whether it succeeded, and `cancelled` would be a claim this machine
    /// cannot support. The open use is only forgotten, so the next sequence can start a new one.</summary>
    internal void UseEnded(object item)
    {
        var equipment = _native.EquipmentOf(item);
        if (equipment != null) _uses.Remove(equipment.Id);
    }

    /// <summary>A request the native hook has proved was refused. Same-frame repeated observations of the same
    /// equipment are one request; the engine frame is only a dedupe token, never a second clock or scheduler.</summary>
    internal void Refused(object item, string reason, int? nativeFrame = null, EntityReference? reporter = null)
    {
        if (!Enter()) return;
        var equipment = _native.EquipmentOf(item);
        var actor = reporter ?? _native.OwnerOf(item);
        if (equipment == null || actor == null) return;
        if (nativeFrame is int frame)
        {
            if (_refusalFrames.TryGetValue(equipment.Id, out var previous) && previous == frame) return;
            _refusalFrames[equipment.Id] = frame;
        }
        if (!_useFailedGate.HasSubscribers) return;
        var id = Id();
        if (Begin(_useFailedGate, id, equipment.Id, out _) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerEquipmentUseFailed.OutActor, actor);
        fact.Values.Entity(Blocks.TriggerEquipmentUseFailed.OutEquipment, equipment);
        fact.Values.Enum(Blocks.TriggerEquipmentUseFailed.OutReason, UseFailureReads.Member(reason));
        Raise(fact, _useFailedGate, id, EntityScope.Equipment.Of(equipment), equipment.Id);
    }

    private bool Enter()
    {
        SyncWorld();
        return _epoch != -1;
    }

    /// <summary>The world epoch and the authority gate, read at the front of every observation. A change drops
    /// every recorded table without a fact: a world transition is a hard boundary, so a slot that differs across it
    /// was never a change this machine watched.</summary>
    internal void SyncWorld()
    {
        var world = _authoritative() ? _world() : -1;
        if (world == _epoch) return;
        if (_epoch != -1 && (_players.Count != 0 || _uses.Count != 0))
            Cleared(world == -1 ? "not-authoritative" : "world-changed");
        foreach (var player in _players.Values) FlushPoolTraces(player);
        _players.Clear();
        _uses.Clear();
        _refusalFrames.Clear();
        _epoch = world;
    }

    /// <summary>Drops every recorded table for a disposal. Nothing is published: the machine is going away.</summary>
    internal void Clear()
    {
        if (_players.Count != 0 || _uses.Count != 0) Cleared("dispose");
        foreach (var player in _players.Values) FlushPoolTraces(player);
        _players.Clear();
        _uses.Clear();
        _refusalFrames.Clear();
        _epoch = -1;
    }

    /// <summary>Begins one fact; null with the refusal when the identity session refuses it. Each caller reads its
    /// cached gate before building the event. Baseline tables are still updated while it is closed, so opening a
    /// subscription cannot report an earlier movement as a new fact.</summary>
    private EventWriter? Begin(RuntimeSubscriptionGate gate, string id, string subject, out DispatchResult? refused)
    {
        refused = null;
        try { return _identity.Begin(gate, _epoch); }
        catch (RuntimeContractException error) { refused = Refused(error, gate, id, subject); return null; }
    }

    private DispatchResult Raise(EventWriter fact, RuntimeSubscriptionGate gate, string id, string scope, string subject)
    {
        try { var answer = fact.Raise(id, _epoch, Tick(), scope); _log.Answered(gate.BindingId, answer, subject); return answer; }
        catch (RuntimeContractException error) { return Refused(error, gate, id, subject); }
    }

    /// <summary>A fact the identity session refuses never reaches the kernel, so the refusal is this package's own
    /// record; a fact the kernel queues or refuses is the kernel's (`trigger.fired` / `event.rejected`).</summary>
    private DispatchResult Refused(RuntimeContractException error, RuntimeSubscriptionGate gate, string id, string subject)
    {
        _log.Reason(WeaponLog.RefusalLevel(error.Code), WeaponLogCodes.FactRejected, error.Code, subject, gate.BindingId);
        return new("rejected", error.Code, id);
    }

    /// <summary>A slot change this machine could not name: a refusal of this package's own, so an error record.</summary>
    private void Unpublished(string reason, string binding, string slot)
    {
        if (!_log.Logs(RuntimeLogLevel.Error)) return;
        _log.Write(RuntimeLogLevel.Error, WeaponLogCodes.FactUnpublished, new RuntimeLogFacts
        {
            Binding = binding,
            Values = new[] { RuntimeLogValue.Of("reason", reason), RuntimeLogValue.Of("slot", slot) }
        });
    }

    /// <summary>The recorded tables were dropped without a fact: info, because a world boundary is not a fault.</summary>
    private void Cleared(string reason)
    {
        if (!_log.Logs(RuntimeLogLevel.Info)) return;
        _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.LivesCleared, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("table", "inventory"), RuntimeLogValue.Of("world", _epoch),
                RuntimeLogValue.Of("count", _players.Count), RuntimeLogValue.Of("uses", _uses.Count),
                RuntimeLogValue.Of("reason", reason) }
        });
    }

    private string Id() => "gtfo.weapon.inventory:" + Number(_epoch) + ":" + Number(checked(++_sequence));
    private long Tick() => Math.Max(0, _tick());
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

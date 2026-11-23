using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using Gear;
using Player;

namespace ForgeWeapon.Native;

internal sealed partial class EquipmentNativeAdapter
{
    private readonly Stack<List<Handle>> _reconcileSnapshots = new();
    private readonly Dictionary<int, string> _inventorySlotNames = new();

    private string SlotName(int index)
    {
        if (!_inventorySlotNames.TryGetValue(index, out var name))
            _inventorySlotNames[index] = name = ((InventorySlot)index).ToString();
        return name;
    }

    internal void Clear()
    {
        foreach (var handle in _byInstance.Values) handle.Life.Active = false;
        foreach (var mount in _holderMounts.Values) mount.Bindings = Array.Empty<HolderBinding?>();
        _byInstance.Clear(); _byEntity.Clear(); _placements.Clear(); _placementsByEntity.Clear();
        _placedByLife.Clear();
        _unresolvedReported.Clear(); _unmatchedReferences.Clear();
    }

    internal void ReconcileInventory(PlayerInventoryBase? inventory)
    {
        if (inventory is null || inventory.WasCollected || inventory == null) return;
        var agent = inventory.Owner; // Unity equality: a destroyed agent reads as null.
        if (agent == null) return;
        var player = agent.Owner;
        if (player != null && PlayerBackpackManager.TryGetBackpack(player, out var backpack)) Reconcile(backpack);
    }

    internal void Reconcile(PlayerBackpack? backpack)
    {
        if (backpack is null || backpack.WasCollected || !Authoritative()) return;
        SyncWorld();
        var pointer = backpack.Pointer;
        var player = backpack.Owner;
        var owner = player == null ? null : _players.ReferenceOf(player);
        // Preserve the original snapshot order, including nested reconciliation from lifetime callbacks.
        var snapshot = _reconcileSnapshots.Count == 0 ? new List<Handle>() : _reconcileSnapshots.Pop();
        try
        {
            foreach (var handle in _byInstance.Values)
                if (handle.BackpackPointer == pointer) snapshot.Add(handle);
            foreach (var stale in snapshot)
            {
                if (owner == null) Retire(stale, "owner-unresolved");
                else if (owner != stale.Owner) Retire(stale, "owner-changed");
                else if (!Matches(stale, backpack)) Retire(stale, "slot-changed");
            }
        }
        finally { snapshot.Clear(); _reconcileSnapshots.Push(snapshot); }
        var slots = backpack.Slots;
        if (slots == null) return;
        var inventory = Inventory(player);
        for (int index = 0; index < slots.Length; index++)
        {
            var item = slots[index];
            if (item is null || item.WasCollected || item.Instance is not { WasCollected: false } instance || instance == null) continue;
            if (owner == null)
            {
                if (_unresolvedReported.Add(pointer))
                    _log.Reason(RuntimeLogLevel.Info, WeaponLogCodes.OwnerUnresolved, "no-player-reference");
                return;
            }
            var instancePointer = item.Instance.Pointer;
            _byInstance.TryGetValue(instancePointer, out var existing);
            var resource = Resource(item, existing?.ResourceId);
            // The deployed marker belongs to the slot: a placed sentry or mine stays this slot's equipment life
            // while the game reports the slot as deployed, and a recall clears the same marker.
            bool deployed = backpack.IsDeployed((InventorySlot)index);
            if (existing != null
                && (existing.BackpackPointer != pointer || existing.SlotIndex != index
                    || existing.ItemPointer != item.Pointer || existing.ResourceId != resource))
            {
                // Moved or replaced: the old life ends; no transfer or re-wield history is reconstructed.
                Retire(existing, "moved-or-replaced");
                existing = null;
            }
            bool recorded = existing != null, locationChanged = recorded && existing!.Deployed != deployed;
            var handle = existing == null
                ? new Handle(NewEntity(), backpack, pointer, item.Pointer, instancePointer, index, resource, owner, deployed)
                : existing.Deployed == deployed && existing.Owner == owner ? existing
                    : existing with { Deployed = deployed, Owner = owner };
            handle.Backpack = backpack;
            // The rebuilt handle shares the life state, so the shot count and the open placement travel with it.
            _byInstance[instancePointer] = handle; _byEntity[handle.Entity.Id] = handle;
            if (!recorded) handle.Life.MountReference = MountBlock(item, index);
            // The marker only moves the slot's location. A deployment is minted by the world instance's spawn and a
            // recall is published by its pickup, which carries the actor; at marker time the slot still holds the tool,
            // so a placement keyed here would be a second identity for the same device.
            var slot = SlotName(index);
            var location = deployed ? EquipmentLocation.Deployed : EquipmentLocation.Inventory;
            var ready = item.IsLoaded;
            var wielded = !deployed && Wielded(inventory, item);
            var observedSlot = deployed ? null : slot;
            var observation = handle.Life.Observation;
            if (observation == null || observation.Entity != handle.Entity || observation.ResourceId != resource
                || observation.ResourceRevision != ResourceRevision || observation.Owner != owner
                || observation.Slot != observedSlot || observation.Location != location
                || observation.IsReady != ready || observation.IsWielded != wielded)
                handle.Life.Observation = observation = new EquipmentObservation(handle.Entity, resource, ResourceRevision,
                    owner, observedSlot, location, ready, wielded);
            try
            {
                // A wield fact the kernel queued or refused is the kernel's own record, so the result is not read.
                _identity!.Record(observation);
                TrackHolder(handle, item, inventory);
                if (!recorded && _log.Logs(RuntimeLogLevel.Info))
                    _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.EquipmentLife, new RuntimeLogFacts
                    {
                        Entity = handle.Entity.Id,
                        Values = new[] { RuntimeLogValue.Of("phase", "started"), RuntimeLogValue.Of("owner", owner.Id),
                            RuntimeLogValue.Of("ownerLife", owner.LifeEpoch), RuntimeLogValue.Of("slot", slot),
                            RuntimeLogValue.Of("resource", resource), RuntimeLogValue.Of("location", deployed ? "Deployed" : "Inventory") }
                    });
                if (locationChanged && _log.Logs(RuntimeLogLevel.Info))
                    _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.EquipmentLife, new RuntimeLogFacts
                    {
                        Entity = handle.Entity.Id,
                        Values = new[] { RuntimeLogValue.Of("phase", "location"), RuntimeLogValue.Of("location", deployed ? "Deployed" : "Inventory") }
                    });
            }
            catch (RuntimeContractException error)
            {
                _log.Reason(RuntimeLogLevel.Error, WeaponLogCodes.ObservationRejected, error.Code, handle.Entity.Id);
                // A life that never started is not reported as ended.
                Retire(handle, recorded ? "observation-rejected" : null);
            }
        }
    }

    internal bool IsNativeCurrent(EquipmentObservation value)
    {
        if (value == null || value.Entity.WorldEpoch != _world || _kernel.WorldEpoch != _world
            || !_byEntity.TryGetValue(value.Entity.Id, out var handle) || handle.Entity != value.Entity) return false;
        if (ReferenceEquals(_departing, handle) && handle.Life.Active && handle.Life.Observation == value) return true;
        if (!TryCurrentBackpack(handle, out var backpack) || !Matches(handle, backpack)) return false;
        var item = backpack.Slots![handle.SlotIndex]!;
        bool deployed = backpack.IsDeployed((InventorySlot)handle.SlotIndex);
        var player = backpack.Owner;
        return deployed == handle.Deployed && player != null && _players.ReferenceOf(player) == value.Owner
            && value.Location == (deployed ? EquipmentLocation.Deployed : EquipmentLocation.Inventory)
            && (deployed ? value.Slot == null : SlotName(handle.SlotIndex) == value.Slot)
            && Resource(item, value.ResourceId) == value.ResourceId && item.IsLoaded == value.IsReady
            && value.IsWielded == (!deployed && Wielded(Inventory(player), item));
    }

    /// <summary>The `gear-block` mount: true only when the subject is one of this provider's recorded equipment
    /// lives and the text the game itself put on that gear is the reference text letter for letter. Nothing is
    /// parsed or normalized here — a second spelling of one block id is a different string and matches nothing.
    /// The native record is re-read on every call rather than cached on the handle, so a slot whose item changed
    /// no longer matches, and a gear the game did not build from an offline block reads no record at all (a
    /// consumable reads its own item id instead, see <see cref="MountBlock"/>). An open deployed world instance
    /// matches through the equipment life that placed it, by the block text recorded when that life started: the
    /// deploy facts name the instance, and the tool's own plans are mounted on the tool's block.</summary>
    internal bool MatchesGearBlock(string? reference, EntityReference subject)
    {
        // A missing reference is no text at all, so there is nothing to compare and nothing to report.
        if (reference == null || subject == null || _identity == null) return false;
        // A reference the catalog does not spell as PlainDecimalId is refused before anything native is read, and
        // the refusal is reported once per distinct text: the same plan re-asks on every event it could belong to.
        if (!IsPlainDecimalId(reference))
        {
            if (_unmatchedReferences.Add(reference) && _log.Logs(RuntimeLogLevel.Error))
                _log.Write(RuntimeLogLevel.Error, WeaponLogCodes.MountReferenceInvalid, new RuntimeLogFacts
                {
                    Values = new[] { RuntimeLogValue.Of("reason", "not-plain-decimal"), RuntimeLogValue.Quote("reference", reference) }
                });
            return false;
        }
        if (subject.WorldEpoch != _world || _kernel.WorldEpoch != _world) return false;
        if (_placementsByEntity.TryGetValue(subject.Id, out var placement))
            return placement.Entity == subject && placement.Instance.Pointer == placement.InstancePointer
                && string.Equals(placement.Handle.Life.MountReference, reference, StringComparison.Ordinal);
        if (!_byEntity.TryGetValue(subject.Id, out var handle) || handle.Entity != subject) return false;
        if (ReferenceEquals(_departing, handle))
            return string.Equals(handle.Life.MountReference, reference, StringComparison.Ordinal);
        var backpack = handle.Backpack;
        if (backpack.Pointer != handle.BackpackPointer || !Matches(handle, backpack)) return false;
        return string.Equals(MountBlock(backpack.Slots![handle.SlotIndex]!, handle.SlotIndex), reference, StringComparison.Ordinal);
    }

    /// <summary>The block text a `gear-block` mount compares against. A gear reads its own offline record
    /// (<see cref="GearBlock"/>). A consumable has no offline record: the game builds it straight from its
    /// `ItemDataBlock`, and the website exports an authored consumable as its own item row whose persistentID is
    /// its stable primary block id. The fixed hacking tool is assembled as gear but authored as an Item:
    /// its BaseItem, not the template's offline record or gear checksum, is the work identity.</summary>
    internal static string? MountBlock(BackpackItem item, int slotIndex)
    {
        if (slotIndex == (int)InventorySlot.HackingTool)
        {
            if (item.GearIDRange != null && item.GearIDRange.GetCompID(eGearComponent.BaseItem) != item.ItemID
                || item.Instance?.ItemDataBlock is { } data && data.persistentID != item.ItemID) return null;
        }
        else if (item.GearIDRange != null) return GearBlock(item);
        if (slotIndex != (int)InventorySlot.Consumable && slotIndex != (int)InventorySlot.HackingTool) return null;
        if (item.ItemID == 0) return null;
        var id = item.ItemID.ToString(CultureInfo.InvariantCulture);
        return IsPlainDecimalId(id) ? id : null;
    }

    /// <summary>The offline gear block text the game itself put on a gear, read from the instance's own record:
    /// `GearManager.LoadOfflineGearDatas` spells it `OfflineGear_ID_<persistentID>`, and the block id is that
    /// suffix. Null for a gear with no such record — every gear that did not come from a
    /// `PlayerOfflineGearDataBlock` — and for a record whose suffix is not a plain decimal id, which is a
    /// spelling this build has no evidence for.</summary>
    internal static string? GearBlock(BackpackItem? item)
        => GearBlockId(item == null ? null : item.GearIDRange);

    /// <summary>The one block-id parser: the game's own offline record text, or null. The `gear-block` mount and
    /// the gear-part pose loader both ask this, so a block id can never have a second spelling in this package.</summary>
    /// <summary>The `weapon` log value of a gear block: its readable game name (authored gear: `<author>_<work>_<number>`), the
    /// block number when the table has no such row. Log points are cold paths; the lookup runs only when a line is written.</summary>
    internal static RuntimeLogValue WeaponName(string blockId)
        => RuntimeLogValue.Of("weapon", uint.TryParse(blockId, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)
            && GameData.PlayerOfflineGearDataBlock.GetBlock(id) is { } block ? block.name : blockId);

    internal static string? GearBlockId(GearIDRange? gear)
    {
        var record = gear == null ? null : gear.PlayfabItemInstanceId;
        if (record == null || !record.StartsWith(GearBlockPrefix, StringComparison.Ordinal)) return null;
        var block = record.Substring(GearBlockPrefix.Length);
        return IsPlainDecimalId(block) ? block : null;
    }

    /// <summary>Plain decimal id text: digits that parse to a `uint` and are that value's own text again, which
    /// is what the website writes with `String(blockId)`. Empty text, a sign, surrounding space, a leading zero,
    /// a radix prefix, a decimal point and anything above `UInt32.MaxValue` are other spellings and are refused.
    /// `NumberStyles.None` already refuses a sign and surrounding space; the round trip refuses a leading zero.</summary>
    private static bool IsPlainDecimalId(string? text)
        => text != null && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && text == value.ToString(CultureInfo.InvariantCulture);

    private EntityReference NewEntity()
        => new(EquipmentIdentityId.Life(_world, checked(++_next)), _world, 1);

    private EntityReference NewDeployedInstance(Handle handle)
        => new(EquipmentIdentityId.DeployedInstance(handle.Entity.Id, checked(++handle.Life.Placements)), _world, 1);

}

using System.Linq;
using ForgeEngine.Framework;
using Gear;
using Player;
using SNetwork;
using ForgeCore;

namespace ForgeWeapon.Native;

internal sealed partial class EquipmentNativeAdapter
{
    public bool Authoritative()
    {
        if (_identity == null || !_canExecute() || !Session.IsHost) return false;
        var state = _kernel.Lifecycle;
        return state.StartupState == RuntimeStartupState.Ready && state.IsHost == true;
    }

    /// <summary>The player reference of a deployed object, re-derived from the native owner every time. Null when
    /// that player has no current reference in its own domain, which is what stops ownerless facts being published.</summary>
    internal EntityReference? OwnerOf(Item? instance)
    {
        var agent = instance == null ? null : instance.Owner;
        var player = agent == null ? null : agent.Owner;
        return player == null ? null : _players.ReferenceOf(player);
    }

    /// <summary>The same lookup for a hit, whose shooter arrives as the agent itself rather than as an item.</summary>
    internal EntityReference? OwnerOf(PlayerAgent? agent)
    {
        var player = agent == null ? null : agent.Owner;
        return player == null ? null : _players.ReferenceOf(player);
    }

    /// <summary>The same lookup for a backpack, whose owning player arrives as the reference itself rather than
    /// through an item or an agent.</summary>
    internal EntityReference? OwnerOf(SNet_Player? player)
        => player == null ? null : _players.ReferenceOf(player);

    /// <summary>The native player one player reference names, or null when this machine cannot name one — the
    /// direction the owner lookups above do not answer, taken from the same directory, so a player this machine
    /// cannot name makes a command naming it a refusal instead of a write against a guessed object.</summary>
    internal SNet_Player? PlayerOf(EntityReference reference) => _players.PlayerOf(reference);

    /// <summary>The live weapon one equipment life currently points at, or null. The answer is the item that life's
    /// own slot holds right now, re-checked against the handle, so a replaced slot or an ended life answers null
    /// like every other read of a stale reference instead of handing back an object the life no longer owns. The
    /// instance-override rows write through this lookup; a life whose slot holds no weapon is unanswered here and
    /// the row refuses it by name.</summary>
    internal BulletWeapon? WeaponOf(EntityReference reference)
    {
        if (reference == null || !_canExecuteOwner() || reference.WorldEpoch != _world
            || !_byEntity.TryGetValue(reference.Id, out var handle) || handle.Entity != reference) return null;
        if (!TryCurrentBackpack(handle, out var backpack) || !Matches(handle, backpack)) return null;
        var item = backpack.Slots![handle.SlotIndex];
        return item?.Instance?.TryCast<BulletWeapon>();
    }

    /// <summary>The backpack slot an item sits in, which names the same gear on every machine; null for an item in no slot.</summary>
    internal static byte? SlotOf(Item item)
    {
        var agent = item.Owner;
        var player = agent == null ? null : agent.Owner;
        if (player == null || !PlayerBackpackManager.TryGetBackpack(player, out var backpack) || backpack?.Slots is not { } slots) return null;
        for (var index = 0; index < slots.Length; index++)
            if (slots[index]?.Instance is { } instance && instance.Pointer == item.Pointer) return (byte)index;
        return null;
    }

    /// <summary>This machine's copy of the item in one slot of a player's backpack.</summary>
    internal static ItemEquippable? ItemIn(PlayerAgent player, byte slot)
    {
        if (player.Owner == null || !PlayerBackpackManager.TryGetBackpack(player.Owner, out var backpack)
            || backpack?.Slots is not { } slots || slot >= slots.Length) return null;
        return slots[slot]?.Instance?.TryCast<ItemEquippable>();
    }

    internal bool LocalHolder(EntityReference reference)
        => EquippableOf(reference) is { } equipment && _players.IsLocal(_players.SessionOf(equipment.Backpack.Owner));

    internal System.Text.Json.JsonElement ExportOwnerMappings(ulong holder, System.Text.Json.JsonElement inputs)
    {
        SyncWorld();
        var requested = inputs.EnumerateObject().Where(field => field.Value.ValueKind == System.Text.Json.JsonValueKind.Object
            && field.Value.TryGetProperty("worldEpoch", out _) && field.Value.TryGetProperty("lifeEpoch", out _))
            .Select(field => RuntimeJson.Entity(field.Value)).ToHashSet();
        return RuntimeJson.From(_byEntity.Values.Where(handle => requested.Contains(handle.Entity)).Select(handle => new
        {
            reference = handle.Entity,
            owner = handle.Owner,
            slot = handle.SlotIndex,
            resource = handle.ResourceId,
            replica = handle.Backpack.Slots![handle.SlotIndex]?.Instance?.pItemData.replicatorRef.keyPlusOne ?? 0
        }).Where(row => row.replica != 0).ToArray());
    }

    // A mapping this machine cannot place yet is skipped, not refused; it is said once per equipment life so a holder whose
    // slot never fills does not look like a silent drop.
    private void SkipOwnerMapping(EntityReference reference, string reason)
    {
        if (_skippedOwnerMappings.Add(reference)) _log.Reason(RuntimeLogLevel.Info, WeaponLogCodes.OwnerUnresolved, reason, reference.Id);
    }

    internal void ImportOwnerMappings(System.Text.Json.JsonElement rows)
    {
        SyncWorld();
        if (_ownerMappingWorld != _world) { _ownerMappingWorld = _world; _retiredOwnerMappings.Clear(); _skippedOwnerMappings.Clear(); }
        foreach (var row in rows.EnumerateArray())
        {
            var reference = RuntimeJson.Entity(row.GetProperty("reference"));
            var owner = RuntimeJson.Entity(row.GetProperty("owner"));
            if (reference.WorldEpoch != _world || owner.WorldEpoch != _world) throw new RuntimeContractException("stale-world", "Equipment mapping world changed.");
            if (_retiredOwnerMappings.Contains(reference)) throw new RuntimeContractException("stale-entity", "Equipment mapping ended.");
            var player = _players.PlayerOf(owner);
            if (player == null || !PlayerBackpackManager.TryGetBackpack(player, out var backpack))
                throw new RuntimeContractException(RuntimeKernel.OwnerHolderCode, "Equipment holder is gone.");
            var slot = row.GetProperty("slot").GetInt32();
            var key = row.GetProperty("replica").GetUInt16();
            if (key == 0) throw new RuntimeContractException(RuntimeKernel.OwnerUnsupportedCode, "Equipment has no native replica identity.");
            if (backpack.Slots == null || slot < 0 || slot >= backpack.Slots.Length || backpack.Slots[slot] is not { Instance: { } instance } item)
            { SkipOwnerMapping(reference, "owner-mapping-slot-empty"); continue; }
            var resource = row.GetProperty("resource").GetString()!;
            if (instance.pItemData.replicatorRef.keyPlusOne != key || Resource(item) != resource)
            { SkipOwnerMapping(reference, "owner-mapping-replica-mismatch"); continue; }
            if (_byEntity.TryGetValue(reference.Id, out var previous) && (previous.Entity != reference || !Matches(previous, backpack)))
            { _retiredOwnerMappings.Add(reference); throw new RuntimeContractException("stale-entity", "Native equipment instance changed."); }
            if (_byInstance.TryGetValue(instance.Pointer, out var old) && old.Entity != reference)
            { _retiredOwnerMappings.Add(old.Entity); Retire(old, "owner-mapping-replaced"); }
            var handle = previous ?? new Handle(reference, backpack, backpack.Pointer, item.Pointer, instance.Pointer, slot, resource, owner, backpack.IsDeployed((InventorySlot)slot));
            _byEntity[reference.Id] = handle; _byInstance[instance.Pointer] = handle;
            _identity!.ImportOwnerObservation(new EquipmentObservation(reference, resource, ResourceRevision, owner,
                handle.Deployed ? null : ((InventorySlot)slot).ToString(), handle.Deployed ? EquipmentLocation.Deployed : EquipmentLocation.Inventory,
                item.IsLoaded, !handle.Deployed && Wielded(Inventory(player), item)));
            TrackHolder(handle, item, Inventory(player));
        }
    }

    /// <summary>The live equippable one equipment life currently points at, with the backpack and slot it sits in,
    /// or null. This is the same current-check `WeaponOf` makes — the reference's world, this machine's recorded
    /// handle for it, and the backpack's own slot table — with two differences: no authority gate, because a
    /// read-only row may answer on a machine that must not write, and no weapon cast, because a magazine read has
    /// to see the item before it can say whether that item is one. The backpack and the slot travel back with the
    /// item because the pool a magazine draws from belongs to the slot, and looking the slot up again would be a
    /// second read of the same table with a second chance to disagree.</summary>
    internal (ItemEquippable Item, PlayerBackpack Backpack, int SlotIndex)? EquippableOf(EntityReference reference)
    {
        if (reference == null || reference.WorldEpoch != _world
            || !_byEntity.TryGetValue(reference.Id, out var handle) || handle.Entity != reference) return null;
        var backpack = handle.Backpack;
        if (backpack.Pointer != handle.BackpackPointer || !Matches(handle, backpack)) return null;
        var slots = backpack.Slots;
        if (slots == null || handle.SlotIndex < 0 || handle.SlotIndex >= slots.Length) return null;
        var item = slots[handle.SlotIndex]?.Instance?.TryCast<ItemEquippable>();
        return item == null ? null : (item, backpack, handle.SlotIndex);
    }

}

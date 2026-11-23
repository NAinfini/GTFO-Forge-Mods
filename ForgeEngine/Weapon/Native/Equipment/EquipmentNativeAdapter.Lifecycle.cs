using System;
using System.Globalization;
using System.Linq;
using ForgeEngine.Framework;
using Player;
using SNetwork;

namespace ForgeWeapon.Native;

internal sealed partial class EquipmentNativeAdapter
{
    internal void SyncWorld()
    {
        long world = _kernel.WorldEpoch;
        // The index clears itself on world, authority and stop transitions; that invalidates every handle.
        // Instance IDs are a per-world sequence; within one world the sequence never rewinds, because retired
        // lives of that world still block reuse of their IDs after an authority-loss clear.
        // The index's own epoch is read as well as this adapter's, because the index clears on authority and stop
        // transitions too, and a placement from the world the index no longer holds must not outlive it either.
        if (world != _world || _identity!.WorldEpoch != world)
        { ClearObserved("world-changed"); _world = world; _next = 0; }
        else if (_byInstance.Count != 0 && _identity.Count == 0) ClearObserved("identity-cleared");
    }

    private void ClearObserved(string reason)
    {
        int count = _byInstance.Count;
        // Placements are dropped with the world they were placed in, without a despawn fact: the world transition
        // is a hard boundary, so their old-world epoch can no longer be published and re-stamping them as
        // new-world facts would attribute an ending to a world they never existed in. This is the one ending a
        // level change cannot report, and it is logged instead.
        int placed = _placements.Count;
        _placements.Clear(); _placementsByEntity.Clear();
        // Every life this boundary drops ends the same way one retirement does, and each is ended exactly once:
        // a life `Retire` already ended is no longer in the table this walks. On a world change the ledger and the
        // profile table were already given up by the session's own lifecycle observer, so the calls are the no-op
        // success they are for an instance that carries nothing; on an index cleared without a world change they
        // are what stops this package's clone from outliving the lives that named it.
        foreach (var handle in _byInstance.Values.ToArray()) EndLife(handle);
        Clear();
        if (count != 0) Cleared("equipment", count, reason);
        if (placed != 0) Cleared("deployments", placed, reason);
    }

    /// <summary>One of this adapter's per-world tables was dropped without a fact: info, a boundary is not a fault.</summary>
    private void Cleared(string table, int count, string reason)
    {
        if (!_log.Logs(RuntimeLogLevel.Info)) return;
        _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.LivesCleared, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("table", table), RuntimeLogValue.Of("world", _world),
                RuntimeLogValue.Of("count", count), RuntimeLogValue.Of("reason", reason) }
        });
    }

    /// <summary>
    /// The hook one equipment life's ending reaches before its identity is dropped: the writes this package made
    /// on that instance end with the life that named them. It is set by the session once the halves that own those
    /// writes exist, and it is called here — before `_byInstance`, `_byEntity` and the identity entry go — because
    /// the revert resolves the instance through the ledger's own table and a lookup that no longer answers would
    /// leave the clone on a native object the game may hand to a later life.
    /// </summary>
    internal Action<EntityReference>? LifeEnded { get; set; }

    internal Action<object, string, EntityReference>? InventorySlotEmptied { get; set; }
    private Handle? _departing;

    private void PublishSlotEnding(Handle handle, string? reason)
    {
        if (!TryCurrentBackpack(handle, out var backpack)) return;
        var slots = backpack.Slots;
        if (reason != "slot-changed" || !handle.Life.Active || handle.Entity.WorldEpoch != _kernel.WorldEpoch
            || backpack.Pointer != handle.BackpackPointer || slots == null || handle.SlotIndex >= slots.Length
            || slots[handle.SlotIndex] is { Instance: not null } || OwnerOf(backpack.Owner) != handle.Owner) return;
        // The empty slot is the observed transition, not a live native item. Only its final facts may validate
        // the recorded identity here; ordinary lookups and commands still require the current native slot.
        var previous = _departing;
        _departing = handle;
        try { InventorySlotEmptied?.Invoke(backpack, SlotName(handle.SlotIndex), handle.Entity); }
        catch (Exception error) { Released("inventory-slot", handle.Entity.Id, error); }
        finally { _departing = previous; }
    }

    /// <summary>One life's writes end here, whatever ended the life. A hook that throws is reported and does not
    /// stop the ending: the identity is going either way, and a half-written table is worse than a reported one.</summary>
    private void EndLife(Handle handle)
    {
        // A cleared slot can no longer resolve the old item. Its recorded native pointer is still the exact
        // reload slot to release, and must not be inherited if the game reuses that object for another life.
        ReloadSpeedSlots.Clear(handle.InstancePointer);
        try { LifeEnded?.Invoke(handle.Entity); }
        catch (Exception error) { Released("equipment-life", handle.Entity.Id, error); }
    }

    private void Retire(Handle handle, string? reason)
    {
        PublishSlotEnding(handle, reason);
        // The world instances this life placed go with it: a placement outliving its equipment life would be an
        // address nothing could ever end, and its despawn fact would never be published.
        foreach (var placed in _placements.Values.Where(p => p.Handle.Entity == handle.Entity).ToArray())
            End(placed, _world, "equipment-ended", recalled: false);
        // The instance's own block comes back before the identity that named it goes: a life that ends here must
        // not leave this package's clone on the native object, which the game may reuse for the next life.
        EndLife(handle);
        handle.Life.Active = false;
        UntrackHolder(handle);
        _byInstance.Remove(handle.InstancePointer);
        _byEntity.Remove(handle.Entity.Id);
        _identity!.Remove(handle.Entity);
        if (reason != null && _log.Logs(RuntimeLogLevel.Info))
            _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.EquipmentLife, new RuntimeLogFacts
            {
                Entity = handle.Entity.Id,
                Values = new[] { RuntimeLogValue.Of("phase", "ended"), RuntimeLogValue.Of("reason", reason) }
            });
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool Matches(Handle handle, PlayerBackpack backpack)
    {
        if (backpack.WasCollected) return false;
        var slots = backpack.Slots;
        if (slots == null || handle.SlotIndex >= slots.Length) return false;
        var item = slots[handle.SlotIndex];
        if (item is null || item.WasCollected || item.Pointer != handle.ItemPointer) return false;
        var instance = item.Instance;
        return instance is not null && !instance.WasCollected && instance != null
            && instance.Pointer == handle.InstancePointer && Resource(item, handle.ResourceId) == handle.ResourceId;
    }

    /// <summary>The agent behind a player reference on this machine, or null when that player holds none here.
    /// Every read that starts from a player passes through this one cast, so a reference this machine cannot
    /// resolve answers null rather than a guessed agent, inventory or owner.</summary>
    internal static PlayerAgent? Agent(SNet_Player? player)
        => player != null && player.HasPlayerAgent ? player.PlayerAgent?.TryCast<PlayerAgent>() : null;

    private static PlayerInventoryBase? Inventory(SNet_Player? player) => Agent(player)?.Inventory;

    private static bool Wielded(PlayerInventoryBase? inventory, BackpackItem item)
    {
        var wielded = inventory?.WieldedItem;
        return wielded != null && item.Instance != null && wielded.Pointer == item.Instance.Pointer;
    }

    private static string Resource(BackpackItem item, string? previous = null)
    {
        var gear = item.GearIDRange;
        var prefix = gear != null ? "gtfo.gear:" : "gtfo.item:";
        ulong id = gear != null ? gear.GetChecksum() : item.ItemID;
        Span<char> digits = stackalloc char[20];
        id.TryFormat(digits, out var width, provider: CultureInfo.InvariantCulture);
        if (previous != null && previous.StartsWith(prefix, StringComparison.Ordinal)
            && previous.AsSpan(prefix.Length).SequenceEqual(digits[..width])) return previous;
        return prefix + id.ToString(CultureInfo.InvariantCulture);
    }
}

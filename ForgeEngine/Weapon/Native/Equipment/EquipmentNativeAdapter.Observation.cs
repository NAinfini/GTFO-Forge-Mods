using System;
using ForgeEngine.Framework;
using Player;

namespace ForgeWeapon.Native;

internal sealed partial class EquipmentNativeAdapter
{
    /// <summary>The recorded life currently mapped to a native item, if any. Not a resolver: callers
    /// still validate the returned reference through Runtime before acting on it.</summary>
    internal EntityReference? EntityOf(Item? instance)
    {
        if (instance is null || instance.WasCollected || instance == null || _identity == null) return null;
        SyncWorld();
        return LevelSentries.EntityOf(instance.Pointer)
            ?? (_byInstance.TryGetValue(instance.Pointer, out var handle) ? handle.Entity : null);
    }

    /// <summary>The recorded life of a native item, read for a fact about it: the life's recorded observation is
    /// brought up to the game's state first, and a life the game no longer holds answers null. A hook that runs
    /// inside a native body (a wield, a use, a slot change) can fire before this package's own reconcile hook of that
    /// body, so the recorded life can still describe the state before the body ran; a reference taken from it is one
    /// the kernel refuses as `stale-entity`. The reconcile is the one every other hook runs, so nothing is derived
    /// here: it re-records the observation or retires the life, and the answer is read after it.</summary>
    internal EntityReference? CurrentEntityOf(Item? instance)
    {
        var recorded = EntityOf(instance);
        if (recorded == null || !_byEntity.TryGetValue(recorded.Id, out var handle)) return recorded;
        RefreshEquipment(recorded);
        var refreshed = EntityOf(instance);
        return refreshed != null && _byEntity.TryGetValue(refreshed.Id, out var fresh)
            && fresh.Life.Observation is { } now && IsNativeCurrent(now) ? refreshed : null;
    }

    private bool _refreshingEquipment;

    /// <summary>Refreshes the observation of this exact life from the current player directory and backpack.
    /// The native slot, item, instance, resource and owner must still match; no replacement inherits its ID.</summary>
    internal void RefreshEquipment(EntityReference reference)
    {
        if (_refreshingEquipment || _identity == null || !Authoritative()
            || !_byEntity.TryGetValue(reference.Id, out var handle) || handle.Entity != reference) return;
        _refreshingEquipment = true;
        try
        {
            if (!TryCurrentBackpack(handle, out var backpack))
            {
                Retire(handle, "native-life-ended");
                return;
            }
            if (handle.Life.Observation is { } seen && IsNativeCurrent(seen)) return;
            Reconcile(backpack);
        }
        finally { _refreshingEquipment = false; }
    }

    private bool TryCurrentBackpack(Handle handle, out PlayerBackpack backpack)
    {
        backpack = null!;
        if (!handle.Life.Active || handle.Entity.WorldEpoch != _kernel.WorldEpoch) return false;
        var player = _players.PlayerOf(handle.Owner);
        if (player is null || player.WasCollected || player.Pointer == IntPtr.Zero
            || !PlayerBackpackManager.TryGetBackpack(player, out var current)
            || current is null || current.WasCollected || current.Pointer != handle.BackpackPointer) return false;
        handle.Backpack = backpack = current;
        return true;
    }

    /// <summary>Counts one committed shot against the equipment life of a firing weapon and returns that life's
    /// reference together with this shot's 1-based index inside it. The counter is the dedupe point: one call per
    /// `Fire` body, never one per hit, so a shotgun's several hits still read as the same shot.</summary>
    internal (EntityReference Equipment, long Index)? RecordShot(Item? weapon)
    {
        if (weapon == null || _identity == null || !Authoritative()) return null;
        var equipment = CurrentEntityOf(weapon);
        if (equipment == null || !_byEntity.TryGetValue(equipment.Id, out var handle)) return null;
        handle.Life.Shots++;
        return (handle.Entity, handle.Life.Shots);
    }

    /// <summary>The readback the attack-instance rows need and no other caller has: the equipment life a weapon
    /// belongs to and the player reference its owner resolves to. Null for a weapon this build has not recorded,
    /// or when the owner has no current reference in its own domain: the same gate a fact needs, because a fact
    /// without its actor is not a fact.</summary>
    public (EntityReference Source, EntityReference Equipment)? AttackTarget(Item? weapon)
    {
        if (weapon == null || _identity == null || !Authoritative()) return null;
        var equipment = CurrentEntityOf(weapon);
        if (equipment == null) return null;
        var owner = OwnerOf(weapon);
        return owner == null ? null : (owner, equipment);
    }

    /// <summary>Read-only snapshot of one recorded equipment life. This is the `gtfo.equipment` observer: it
    /// only ever answers for a life the index already holds, and it re-checks the native object first.</summary>
    internal RuntimeEntitySnapshot? ObserveEquipment(EntityReference reference)
    {
        if (_identity == null || !Authoritative() || reference == null || reference.WorldEpoch != _world
            || !_byEntity.TryGetValue(reference.Id, out var handle) || handle.Entity != reference) return null;
        if (!TryCurrentBackpack(handle, out var backpack) || !Matches(handle, backpack)) return null;
        var item = backpack.Slots![handle.SlotIndex]!;
        var instance = item.Instance!;
        var point = Position(instance);
        if (point == null) return null;
        bool deployed = backpack.IsDeployed((InventorySlot)handle.SlotIndex);
        // A placement that is still open is named in the tags, so a behaviour can carry the deployed instance
        // across steps without inventing an address; the recalled marker survives its own recall.
        var placement = handle.Life.Placed;
        var tags = placement != null && !placement.Recalled
            ? new[] { "deployed", placement.Entity.Id }
            : new[] { deployed || (placement?.Recalled ?? false) ? "deployed" : "inventory" };
        return new RuntimeEntitySnapshot(reference, EntityKinds.Equipment, "player", "alive", tags,
            Array.Empty<string>(), point, parent: handle.Owner);
    }

    /// <summary>Whether one deployed reference still names an open placement, read from this provider's own
    /// placement table and from nothing else. Currency and observability are two questions: an observation has to
    /// carry a world point, while a placement whose object cannot report one is still a placement this provider
    /// holds — the deployment row's optional position says exactly that — so the kernel's currency check is
    /// answered here rather than by the snapshot.</summary>
    internal bool HoldsDeployable(EntityReference reference)
    {
        if (EquipmentIdentityId.ShapeOf(reference?.Id) == EquipmentEntityShape.LevelSentryInstance)
            return LevelSentries.Holds(reference!);
        if (_identity == null || !Authoritative() || reference == null || reference.WorldEpoch != _world) return false;
        return _placementsByEntity.TryGetValue(reference.Id, out var placement) && placement.Entity == reference
            && placement.Instance.Pointer == placement.InstancePointer;
    }

    /// <summary>The tag every deployed world instance carries so a reader can tell it from a carried equipment life,
    /// now that both publish the one `gtfo.equipment` kind.</summary>
    internal const string DeployableTag = "deployable";

    /// <summary>Read-only snapshot of one deployed world instance. Null for a destroyed, reappeared or
    /// never-recorded instance, so a stale reference is refused instead of answered from a replaced object.</summary>
    internal RuntimeEntitySnapshot? ObserveDeployable(EntityReference reference)
    {
        if (EquipmentIdentityId.ShapeOf(reference?.Id) == EquipmentEntityShape.LevelSentryInstance)
        {
            var sentry = LevelSentries.ItemOf(reference!);
            var position = Position(sentry);
            return sentry == null || position == null ? null
                : new RuntimeEntitySnapshot(reference!, EntityKinds.Equipment, "player", "alive", new[] { "deployed", DeployableTag },
                    Array.Empty<string>(), position, parent: OwnerOf(sentry));
        }
        if (_identity == null || !Authoritative() || reference == null || reference.WorldEpoch != _world
            || !_placementsByEntity.TryGetValue(reference.Id, out var placement) || placement.Entity != reference
            || placement.Instance.Pointer != placement.InstancePointer) return null;
        var owner = OwnerOf(placement.Instance);
        if (owner == null) return null;
        var point = Position(placement.Instance);
        if (point == null) return null;
        var tags = placement.Recalled ? new[] { "recalled", DeployableTag } : new[] { "deployed", placement.Handle.ResourceId, DeployableTag };
        return new RuntimeEntitySnapshot(reference, EntityKinds.Equipment, "player", "alive", tags,
            Array.Empty<string>(), point, parent: owner);
    }

}

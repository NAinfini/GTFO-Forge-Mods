using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using Player;

namespace ForgeWeapon.Native;

internal sealed partial class EquipmentNativeAdapter
{
    /// <summary>A deployed world object appeared. The native instance is matched to the backpack life that holds
    /// it, so a placement always reports the equipment it came from. One identity per world instance per world
    /// epoch; the same pointer reappearing after its previous instance ended is a new identity, never the old one.
    /// The deployment fact is published here and not from the backpack marker, because the mine family never sets
    /// that marker — this spawn is the only deploy signal both families share. <paramref name="kind"/> is the
    /// spawning family's own declaration and travels with the identity, because nothing on the instance itself
    /// says which shell it is.</summary>
    internal PlacedObject? TrackDeployed(Item? instance, string? kind)
    {
        if (instance == null || kind == null || _identity == null || !Authoritative()) return null;
        SyncWorld();
        return Placed(instance, OwnerOf(instance), kind);
    }

    /// <summary>One identity per world instance, numbered inside the equipment life that placed it. The owner is
    /// passed in rather than resolved here, because a spawn can be observed before this build has filled the native
    /// owner in.</summary>
    private PlacedObject? Placed(Item instance, EntityReference? owner, string kind)
    {
        // A level sentry has its own identity, and a sentry another mod stood off the tool slot (a summoned turret, a level sentry)
        // is not the tool its owner carries: matching it to the owner's deployed tool life would record it as that tool's placement.
        if (LevelSentries.EntityOf(instance.Pointer) != null || ForgeCore.Gear.IsToolSlotKept(instance.Pointer)) return null;
        var handle = HandleOf(instance, owner, kind);
        if (handle == null) return null;
        if (_placements.TryGetValue(instance.Pointer, out var existing))
        { handle.Life.Placed = existing; return existing; }
        var placement = new PlacedObject(NewDeployedInstance(handle), instance, handle, kind);
        _placements[instance.Pointer] = placement; _placementsByEntity[placement.Entity.Id] = placement;
        _placedByLife[handle.Entity.Id] = placement;
        handle.Life.Placed = placement;
        if (_log.Logs(RuntimeLogLevel.Info))
            _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.DeployableLife, new RuntimeLogFacts
            {
                Entity = placement.Entity.Id,
                Values = new[] { RuntimeLogValue.Of("phase", "started"), RuntimeLogValue.Of("equipment", handle.Entity.Id),
                    RuntimeLogValue.Of("kind", kind), RuntimeLogValue.Of("resource", handle.ResourceId) }
            });
        // An ownerless placement stays tracked and endable, but no fact is published for it: the catalog source is
        // an entity reference, and a fact without its actor would be a lie about who deployed it.
        if (owner == null)
        {
            _log.Reason(RuntimeLogLevel.Error, WeaponLogCodes.FactUnpublished, "owner-unresolved", placement.Entity.Id,
                ModuleDefinition.DeployCompletedBinding);
            return placement;
        }
        // The position is read from the world instance this spawn created, never copied from the request that
        // asked for it. An unreadable point leaves the optional port out and still publishes the placement. The
        // kind is the spawning family's own member, and a family this build cannot name publishes no placement at
        // all: the row's port is the one thing that tells a sentry from a mine, and a guessed member would be
        // worse than a missing fact.
        var kindIndex = WeaponPlacementContract.KindIndex(placement.Kind);
        if (kindIndex == null)
        {
            _log.Reason(RuntimeLogLevel.Error, WeaponLogCodes.FactUnpublished, "kind-unknown", placement.Entity.Id,
                ModuleDefinition.DeployCompletedBinding);
            return placement;
        }
        // Nothing is listening on the deployment row: the kernel would answer `no-consumer` for the event this
        // call is about to build, so the event value is never built. The placement itself stands either way.
        if (_deployGate is { HasSubscribers: true })
        {
            var id = "gtfo.equipment.deploy:" + Number(_world) + ":" + Number(checked(++_deploySequence));
            var fact = Begin(_deployGate, _world);
            WeaponPlacementContract.DeployOutputs(fact.Values, owner, placement.Entity, kindIndex, Position(instance));
            fact.Raise(id, _world, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(_world));
        }
        return placement;
    }

    /// <summary>The equipment life a world instance belongs to. The instance's own recorded life is the answer
    /// whenever this machine has one. On a machine that did not run the placement — the host, for a device a
    /// client placed — the world instance arrives through the item replication path while the backpack marker the
    /// marker path reads back is the placer's own local state, so no recorded life is keyed by that exact
    /// instance; the life is then found by the owner the replicated instance itself reports, which is the
    /// same player the placement belongs to. A sentry still has to name the one slot carrying its deployed marker;
    /// a mine never writes that marker, so its owner and MineDeployer slot family are the native association. More
    /// than one matching current life is resolved by the tool the owner is wielding; with none wielded it is
    /// ambiguous and names no equipment rather than choosing by table order.</summary>
    private Handle? HandleOf(Item instance, EntityReference? owner, string kind)
    {
        if (_byInstance.TryGetValue(instance.Pointer, out var exact)) return exact;
        if (owner == null) return null;
        Handle? found = null, wieldedFound = null;
        var several = false;
        var inventory = Inventory(_players.PlayerOf(owner));
        foreach (var candidate in _byInstance.Values)
        {
            if (candidate.Owner != owner || !MatchesPlacementLife(candidate, kind)) continue;
            if (found != null && found != candidate) several = true;
            found ??= candidate;
            // A device is placed from the tool in the owner's hands, so when a second tool of the same family is carried
            // (a second mine or sentry slot) the wielded one is the native answer, so the second device of a family
            // still gets its identity; only when no candidate is wielded does the match stay ambiguous.
            if (candidate.Backpack.Slots![candidate.SlotIndex] is { } slotItem && Wielded(inventory, slotItem))
                wieldedFound = candidate;
        }
        if (several)
        {
            if (wieldedFound == null)
            {
                _log.Reason(RuntimeLogLevel.Error, WeaponLogCodes.PlacementAmbiguous, "several-slots-deployed", owner.Id);
                return null;
            }
            found = wieldedFound;
        }
        if (found == null)
            _log.Reason(RuntimeLogLevel.Info, WeaponLogCodes.PlacementUnmatched, "no-recorded-life", owner.Id);
        return found;
    }

    /// <summary>Whether one current backpack life is the tool family that produced a world instance. Sentries also
    /// have the game's deployed marker and keep that stronger check. Mines never write that marker in this build,
    /// so their exact owner plus the current slot's MineDeployer family is the complete native association.</summary>
    private static bool MatchesPlacementLife(Handle handle, string kind)
    {
        var backpack = handle.Backpack;
        if (backpack.Pointer != handle.BackpackPointer || !Matches(handle, backpack)) return false;
        var slots = backpack.Slots;
        if (slots == null || handle.SlotIndex < 0 || handle.SlotIndex >= slots.Length) return false;
        if (!string.Equals(PlacementKind(slots[handle.SlotIndex]?.Instance), kind, StringComparison.Ordinal)) return false;
        return string.Equals(kind, WeaponPlacementContract.MineKind, StringComparison.Ordinal)
            || SafeDeployed(backpack, handle.SlotIndex);
    }

    /// <summary>The kind a backpack item's own slot deploys. The first-person tool a placement slot holds names the
    /// family that places it, and this build's sentry tool and mine tool are two classes with one placement body
    /// each. A slot holding the deployed world object itself is the same answer — this build's `SentryGunInstance`
    /// and `MineDeployerInstance` are two more classes with one placement body each — and the two forms are read in
    /// that order because the instance classes are the object the spawn path tracks, while it is the tool that is
    /// still in the backpack at the moment the marker is written. A slot whose item is neither is not a placement
    /// this package can name, and the caller mints nothing for it. Every read is the interop cast, which refuses a
    /// destroyed object instead of throwing, so a slot cleared between the marker write and this read names none.</summary>
    private static string? PlacementKind(Item? instance)
    {
        if (instance == null) return null;
        try
        {
            if (instance.TryCast<SentryGunFirstPerson>() != null) return WeaponPlacementContract.SentryGunKind;
            if (instance.TryCast<MineDeployerFirstPerson>() != null) return WeaponPlacementContract.MineKind;
            if (instance.TryCast<SentryGunInstance>() != null) return WeaponPlacementContract.SentryGunKind;
            if (instance.TryCast<MineDeployerInstance>() != null) return WeaponPlacementContract.MineKind;
        }
        catch (Exception failure) { NativeCallFailure.Note("EquipmentNativeAdapter.Deployments.PlacementKind", failure); /* a destroyed native object is not a placement this build can name. */ }
        return null;
    }

    /// <summary>Whether one recorded life's own backpack slot currently reads as deployed. Read through the same
    /// backpack lookup the rest of this class uses, and false for a slot the table no longer holds, so a retired
    /// life is never joined to a new world instance.</summary>
    private static bool IsDeployedSlot(Handle handle)
    {
        var backpack = handle.Backpack;
        if (backpack.Pointer != handle.BackpackPointer || !Matches(handle, backpack)) return false;
        return SafeDeployed(backpack, handle.SlotIndex);
    }

    /// <summary>The deployed marker of one slot, refusing rather than throwing when the game's own table is gone.</summary>
    private static bool SafeDeployed(PlayerBackpack backpack, int slot)
    {
        try { return backpack.IsDeployed((InventorySlot)slot); }
        catch (Exception failure) { NativeCallFailure.Note("EquipmentNativeAdapter.Deployments.SafeDeployed", failure); return false; }
    }

    /// <summary>The recall of one open placement, published once from the pickup that carries the acting player
    /// (<see cref="End"/>). The world object stays resolvable (tagged recalled) until its own removal arrives, and a
    /// handle with no open placement publishes nothing.</summary>
    private void PublishRecall(Handle handle, EntityReference? owner)
    {
        var placed = handle.Life.Placed;
        if (placed == null) return;
        placed.Recalled = true;
        if (_recallGate is not { HasSubscribers: true }) return;
        var id = "gtfo.equipment.recall:" + Number(_world) + ":" + Number(checked(++_deploySequence));
        var fact = Begin(_recallGate, _world);
        WeaponPlacementContract.RecallOutputs(fact.Values, owner, placed.Entity, WeaponPlacementContract.KindIndex(placed.Kind));
        fact.Raise(id, _world, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(_world));
    }

    /// <summary>One deployed world object is gone. The first ending wins; a second path (OnDespawn then OnDestroy,
    /// or OnDestroy after a recall) never publishes again, because closing removes the placement that both
    /// endings look up. The ending body is the same on both families and needs no kind: the placement it closes
    /// already carries the one its spawn declared.</summary>
    internal void EndDeployed(Item? instance, string reason, bool recalled, PlayerAgent? recallActor = null)
    {
        if (instance is not null && LevelSentries.EntityOf(instance.Pointer) is { } sentry)
        {
            // Publish while the exact producer association is valid. Runtime captures the mounted identity
            // and its epochs on the event; the native association ends immediately after these facts.
            var world = sentry.WorldEpoch;
            if (recalled && _recallGate is { HasSubscribers: true })
            {
                var id = "gtfo.equipment.recall:" + Number(world) + ":" + Number(checked(++_deploySequence));
                var fact = Begin(_recallGate, world);
                WeaponPlacementContract.RecallOutputs(fact.Values, OwnerOf(recallActor), sentry,
                    WeaponPlacementContract.KindIndex(WeaponPlacementContract.SentryGunKind));
                fact.Raise(id, world, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(world));
            }
            if (_despawnGate is { HasSubscribers: true }) PublishDespawn(sentry, world, reason);
            LevelSentries.End(instance.Pointer);
            return;
        }
        if (instance == null || !_placements.TryGetValue(instance.Pointer, out var placement)
            || placement.Instance.Pointer != placement.InstancePointer) return;
        End(placement, _world, reason, recalled, OwnerOf(recallActor));
    }

    /// <summary>Ends one placement and publishes its facts. This is the only ending path, so a level change that
    /// tears the world down, a pickup and a destroy all report exactly once and all report the same way.</summary>
    private void End(PlacedObject placement, long world, string reason, bool recalled, EntityReference? recallActor = null)
    {
        placement.Recalled = recalled;
        placement.Ended = reason;
        // A recall publishes its own fact first: the pickup is what the player did, the removal is what the world
        // did about it, and a plan reading both should see them in that order.
        if (recalled) PublishRecall(placement.Handle, recallActor);
        // Nothing is listening on the despawn row: the event value is never built, and the ending still closes.
        if (_despawnGate is { HasSubscribers: true }) PublishDespawn(placement.Entity, world, reason);
        ReleaseObjectScope(placement);
        Close(placement);
    }

    /// <summary>One deployed world object's removal, on the despawn row whose gate the caller has read. The port
    /// carries a member of the wire set `deployable_removal_reason`, published by name: `recalled` and `destroyed`
    /// are the two endings a native call names, and every other ending (the object despawned, or its equipment's
    /// life ended under it) is `despawned`.</summary>
    private void PublishDespawn(EntityReference entity, long world, string reason)
    {
        var id = "gtfo.equipment.despawn:" + Number(world) + ":" + Number(checked(++_deploySequence));
        var fact = Begin(_despawnGate!, world);
        fact.Values.Entity(Blocks.TriggerEntityDespawned.OutEntity, entity);
        fact.Values.Text(Blocks.TriggerEntityDespawned.OutReason, reason is "recalled" or "destroyed" ? reason : "despawned");
        fact.Raise(id, world, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(world));
    }

    /// <summary>
    /// Drops the `object`-scoped variables of one retired device. A device's object scope lives as long as the
    /// object stands in the world, and the kernel cannot tell a recalled device from a live one — the half that
    /// observed the ending is the half that says so, which is what ruling 117.6 asks for. Only the world the
    /// placement belongs to is answered: a world change clears the whole table itself, and a reference from a world
    /// the kernel has already left would be refused as stale rather than released.
    /// </summary>
    private void ReleaseObjectScope(PlacedObject placement)
        => ReleaseObjectScope(placement.Entity);

    private void ReleaseObjectScope(EntityReference entity)
    {
        if (entity.WorldEpoch != _kernel.WorldEpoch) return;
        var released = _kernel.ReleaseVariableScope(VariableScopeKinds.Object, entity);
        if (released != 0 && _log.Logs(RuntimeLogLevel.Info))
            _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.ObjectScopeReleased, new RuntimeLogFacts
            {
                Entity = entity.Id,
                Values = new[] { RuntimeLogValue.Of("count", released) }
            });
    }

    /// <summary>Something this package holds could not be given back: an error record naming what and why.</summary>
    private void Released(string what, string entity, Exception error)
    {
        if (!_log.Logs(RuntimeLogLevel.Error)) return;
        _log.Write(RuntimeLogLevel.Error, WeaponLogCodes.ReleaseFailed, new RuntimeLogFacts
        {
            Entity = entity,
            Values = new[] { RuntimeLogValue.Of("what", what), RuntimeLogValue.Of("reason", error.GetType().Name) }
        });
    }

    /// <summary>Begins one fact of world <paramref name="worldEpoch"/> under this provider's own registration: the one
    /// publication path of every observer in this package, including the facts the adapter builds itself. The
    /// caller writes the outputs by position and raises it; Runtime owns ordering, dedupe and causality.</summary>
    public EventWriter Begin(RuntimeSubscriptionGate gate, long worldEpoch) => _identity!.Begin(gate, worldEpoch);
    internal EventWriter BeginPredicted(RuntimeSubscriptionGate gate) => _identity!.BeginPredicted(gate);
    internal void PredictionFailed(RuntimeSubscriptionGate gate, Exception error) => _identity!.PredictionFailed(gate, error);

    /// <summary>The kernel-refreshed gate of one of this provider's own bindings, taken once by each observer when it
    /// is attached and read before an event value is built, so a fact nobody subscribes to is never constructed.
    /// Every observer is built after <see cref="Attach"/>.</summary>
    public RuntimeSubscriptionGate GateOf(string binding)
        => (_identity ?? throw new InvalidOperationException("The adapter is not attached to an identity session.")).GateOf(binding);

    private void Close(PlacedObject placement)
    {
        _placements.Remove(placement.InstancePointer);
        _placementsByEntity.Remove(placement.Entity.Id);
        if (_placedByLife.TryGetValue(placement.Handle.Entity.Id, out var open) && ReferenceEquals(open, placement))
            _placedByLife.Remove(placement.Handle.Entity.Id);
        if (placement.Handle.Life.Placed == placement) placement.Handle.Life.Placed = null;
        if (_log.Logs(RuntimeLogLevel.Info))
            _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.DeployableLife, new RuntimeLogFacts
            {
                Entity = placement.Entity.Id,
                Values = new[] { RuntimeLogValue.Of("phase", "ended"), RuntimeLogValue.Of("kind", placement.Kind),
                    RuntimeLogValue.Of("reason", placement.Ended ?? "") }
            });
    }

    /// <summary>The recorded placement of a native world instance, if it is still open.</summary>
    internal PlacedObject? PlacementOf(Item? instance)
        => instance != null && _placements.TryGetValue(instance.Pointer, out var placement)
            && placement.Instance.Pointer == placement.InstancePointer ? placement : null;

    /// <summary>The open placement one equipment life currently holds, or null. This is the answer for a world
    /// instance that arrived without a recorded life of its own: the life is the one its owner's deployed slot
    /// names, and the placement is what that life's own marker path or spawn minted.</summary>
    internal PlacedObject? PlacementOf(EntityReference life)
        => life != null && _placedByLife.TryGetValue(life.Id, out var placement)
            && placement.Instance.Pointer == placement.InstancePointer ? placement : null;

    /// <summary>The recorded placement of a native world instance held as a bare pointer, or null. The device
    /// facts read their subject that way — a firing component and a trigger component reach the device through
    /// their own core, which is the same native object this table keyed the placement on — and a pointer whose
    /// placement already ended answers null rather than the object it used to be.</summary>
    internal PlacedObject? PlacementOf(IntPtr instancePointer)
        => _placements.TryGetValue(instancePointer, out var placement)
            && placement.Instance.Pointer == placement.InstancePointer ? placement : null;

    /// <summary>The sentries one player placed that are still standing and not already recalled. The owner is re-read
    /// from each native instance, the same answer the placement facts report.</summary>
    internal IReadOnlyList<SentryGunInstance> PlacedSentriesOf(EntityReference owner)
    {
        var sentries = new List<SentryGunInstance>();
        foreach (var placement in _placements.Values)
        {
            if (placement.Recalled || placement.Instance.Pointer != placement.InstancePointer
                || !string.Equals(placement.Kind, WeaponPlacementContract.SentryGunKind, StringComparison.Ordinal)
                || OwnerOf(placement.Instance) != owner) continue;
            if (placement.Instance.TryCast<SentryGunInstance>() is { } sentry) sentries.Add(sentry);
        }
        return sentries;
    }

    /// <summary>The native item of an open placement, or null. The owner tier's session resolver asks this because
    /// the reference it is handed names a world instance, and that instance's own owner chain is the game's answer
    /// to who holds it; anything that is not an open placement answers null.</summary>
    internal Item? PlacedItemOf(EntityReference reference)
        => EquipmentIdentityId.ShapeOf(reference?.Id) == EquipmentEntityShape.LevelSentryInstance
            ? LevelSentries.ItemOf(reference!)
            : reference != null && _placementsByEntity.TryGetValue(reference.Id, out var placement)
            && placement.Instance.Pointer == placement.InstancePointer ? placement.Instance : null;

}

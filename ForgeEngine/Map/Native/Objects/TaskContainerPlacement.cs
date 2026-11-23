using System;
using AIGraph;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

internal sealed partial class FixedPlacementSession
{
    private long _containersEpoch = -1;
    private MapLevelReference? _containersLevel;

    internal bool TaskContainersPending
    {
        get
        {
            if (_disposed || _disposing || _level() is not { } level) return false;
            if (_placedEpoch == _worldEpoch() && _placedLevel == level) return false;
            foreach (var row in _items) if (row.Level == level && row.TaskItemsOnly) return true;
            return false;
        }
    }

    private static bool SameZone(RoomLocator a, RoomLocator b) => a.Dimension == b.Dimension
        && a.Layer == b.Layer && a.LocalIndex == b.LocalIndex;
    private static bool SameZone(RoomLocator row, LG_Zone? zone) => zone?.m_layer != null && row.Dimension == (int)zone.m_dimensionIndex
        && row.Layer == (int)zone.m_layer.m_type && row.LocalIndex == (int)zone.LocalIndex;

    private bool HasTaskZone(RoomLocator locator)
    {
        foreach (var row in _items)
            if (row.Level == _level() && row.TaskItemsOnly && SameZone(row.Locator, locator)) return true;
        return false;
    }
    private bool HasTaskZone(LG_Zone zone)
    {
        foreach (var row in _items)
            if (row.Level == _level() && row.TaskItemsOnly && SameZone(row.Locator, zone)) return true;
        return false;
    }

    // The same boxes as FactoryDone, created once after the native course graph is complete.
    // They are not marker jobs, so RemoveUnusedResourceContainerJob does not own or remove them.
    private void EnsureContainers(long epoch, MapLevelReference level)
    {
        if (_containersEpoch == epoch && _containersLevel == level) return;
        if (!TaskGraphReady) throw new InvalidOperationException("Native task container course graph is not ready.");
        if (_containersEpoch >= 0 && _container != null && !_container.WasCollected)
        { UnityEngine.Object.Destroy(_container.gameObject); _container = null; }
        _boxes.Clear();
        if (Container() == null) throw new InvalidOperationException("Authored container level root is unavailable.");
        var random = UnityEngine.Random.state;
        try
        {
            foreach (var row in _items) if (row.Level == level && row.ContainerType != null) PlaceContainer(row, epoch);
            _containersEpoch = epoch; _containersLevel = level;
        }
        catch
        {
            _boxes.Clear();
            if (_container != null && !_container.WasCollected) UnityEngine.Object.Destroy(_container.gameObject);
            _container = null;
            throw;
        }
        finally { UnityEngine.Random.state = random; }
    }

    private bool AuthoredStorage(LG_ResourceContainer_Storage storage)
    {
        foreach (var box in _boxes.Values) if (box.Storage.Pointer == storage.Pointer) return true;
        return false;
    }

    private (FixedItem Row, LG_ResourceContainer_Storage Storage, AIG_CourseNode Node, Transform Align)? SelectTaskSlot(AIG_CourseNode source)
        => SelectTaskSlot(source.m_zone);

    private (FixedItem Row, LG_ResourceContainer_Storage Storage, AIG_CourseNode Node, Transform Align)? SelectTaskSlot(LG_Zone? source)
    {
        if (_worldEpoch() == 0 || _level() is not { } level)
        { TaskRefused("", "task-world-unavailable"); return null; }
        try { EnsureContainers(_worldEpoch(), level); }
        catch (Exception error) { TaskPreparationFailed(error); return null; }
        // Refuse an unresolved authored pool, rather than silently treating its missing member as unused.
        foreach (var row in _items) if (row.Level == level && row.TaskItemsOnly && SameZone(row.Locator, source))
            if (!_boxes.TryGetValue(row.Id, out var box) || box.Storage.WasCollected || box.Storage == null
                || !SameZone(row.Locator, box.Node.m_zone))
            { TaskRefused(row.Id, "task-container-missing-or-wrong-zone"); return null; }
        string id = "", reason = "task-container-full";
        foreach (var row in _items)
        {
            if (row.Level != level || !row.TaskItemsOnly || !SameZone(row.Locator, source)) continue;
            id = row.Id;
            var box = _boxes[row.Id];
            var reserved = 0;
            foreach (var item in _items) if (item.Level == level && item.ContainerId == row.Id) reserved++;
            var align = FixedContainerPlacement.FreeSlot(box.Storage, out reason, reserved);
            if (align != null) return (row, box.Storage, box.Node, align);
        }
        TaskRefused(id, reason);
        return null;
    }

    /// <summary>Called at the original last storage-slot selection, after vanilla has drawn its seed.
    /// The original private factory initializes the same native pack/key in the selected authored storage.
    /// SpawnContent already incremented the source's counter, whereas these private entries only add the
    /// interaction list. Transfer that counter once; do not call OnItemAdded and duplicate the native list.</summary>
    internal bool RouteTaskPack(LG_ResourceContainer_Storage source, ResourceContainerSpawnData pack, int seed, bool key)
    {
        if (!TaskContainersPending || AuthoredStorage(source)) return true;
        var node = source.m_core?.SpawnNode;
        if (node?.m_zone == null || !HasTaskZone(node.m_zone)) return true;
        if (DeferTask(() => RouteTaskPack(source, pack, seed, key))) return false;
        source.AddNumberOfItemsToContainer(-1);
        var slot = SelectTaskSlot(node);
        if (slot == null) return false;
        var chosen = slot.Value;
        try
        {
            if (key) chosen.Storage.PlaceKeyCard(pack, chosen.Align, seed);
            else chosen.Storage.PlaceSmallGenericPickup(pack, chosen.Align, seed);
            var item = chosen.Align.gameObject.GetComponentInChildren<ItemInLevel>(true);
            if (item == null || item.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>() is not { } sync)
            { TaskRefused(chosen.Row.Id, "task-native-pickup-missing"); return false; }
            item.container = chosen.Storage;
            var state = sync.GetCurrentState();
            var placement = state.placement; placement.returnToParentOnRecall = true; state.placement = placement;
            sync.SetStateFromFactory(state);
            chosen.Storage.AddNumberOfItemsToContainer(1);
            TaskPlaced(chosen.Row, item, key ? pack.m_keyItem.DataBlockID : pack.m_genericItemId, -1, chosen.Node);
        }
        catch (Exception error)
        {
            var created = chosen.Align.gameObject.GetComponentInChildren<ItemInLevel>(true);
            if (created != null) UnityEngine.Object.Destroy(created.gameObject);
            TaskRefused(chosen.Row.Id, "task-native-factory-failed", error.GetType().Name);
        }
        return false; // The source's factory never creates a second copy, including on refusal.
    }

    /// <summary>
    /// Completes the native keycard placement for a registered bulkhead key whose distribution never reached a
    /// native weak container. This is only the explicit authored task-container pool in the key's own generated
    /// zone: a key from another zone, another world, an ordinary container or a key already placed is left alone.
    ///
    /// The original public container entry is kept intact. Its one-pack call selects the only exposed free slot,
    /// accounts the item and reaches the private keycard factory, which places the existing <see cref="GateKeyItem"/>'s
    /// pickup, seeds its placement state, writes its spawn node and disables its interaction. This half restores the
    /// container's complete slot table immediately afterwards, adds the same recall-parent fact as the existing
    /// transfer path, then accepts the placement only after the native pickup, parent, node and state all read back.
    /// The caller owns registry membership: <see cref="KeyPlacementAudit"/> invokes this only while enumerating
    /// `ProgressionObjective_BulkheadKey.m_allBulkheadKeysInLevel`.
    /// </summary>
    internal bool TryPlaceUnplacedBulkheadKey(long epoch, GateKeyItem key)
    {
        if (_disposed || _disposing || epoch == 0 || epoch != _worldEpoch()
            || _placedEpoch != epoch || _level() is not { } level || _placedLevel != level) return false;
        if (key.WasCollected) return false;
        var pickup = key.keyPickupCore;
        var transform = pickup?.transform;
        if (pickup == null || pickup.WasCollected || transform == null || transform.WasCollected) return false;
        var parent = transform.parent;
        if (parent != null && !parent.WasCollected) return false;
        var zone = key.SpawnZone;
        if (zone == null || zone.WasCollected)
        { TaskRefused("", "task-native-key-zone-missing"); return false; }
        var currentZone = false;
        foreach (var candidate in ZoneIndex.Current(epoch).Zones())
            if (candidate != null && !candidate.WasCollected && candidate.Pointer == zone.Pointer)
            { currentZone = true; break; }
        if (!currentZone)
        { TaskRefused("", "task-native-key-zone-stale"); return false; }
        if (!HasTaskZone(zone)) return false;

        var selected = SelectTaskSlot(zone);
        if (selected == null) return false;
        var chosen = selected.Value;
        try
        {
            var slots = chosen.Storage.m_storageSlots;
            StorageSlot? slot = null;
            foreach (var candidate in slots)
                if (candidate?.Keycard is { } align && !align.WasCollected && align.Pointer == chosen.Align.Pointer)
                { slot = candidate; break; }
            if (slot == null)
            { TaskRefused(chosen.Row.Id, "task-native-key-slot-missing"); return false; }
            var pack = new ResourceContainerSpawnData
            {
                m_type = eResourceContainerSpawnType.Keycard,
                m_keyItem = key
            };
            var packs = new Il2CppSystem.Collections.Generic.List<ResourceContainerSpawnData>();
            packs.Add(pack);
            var random = UnityEngine.Random.state;
            try
            {
                // SpawnContent draws from the complete slot array without checking its children. Restrict the
                // synchronous call to the free slot selected by the authored pool, then restore the exact array.
                chosen.Storage.m_storageSlots = new[] { slot };
                chosen.Storage.SpawnContent(packs, 0);
            }
            finally
            {
                chosen.Storage.m_storageSlots = slots;
                UnityEngine.Random.state = random;
            }
            var item = chosen.Align.gameObject.GetComponentInChildren<ItemInLevel>(true);
            // The private keycard factory parents the pickup and seeds its replicated state but never writes
            // `ItemInLevel.CourseNode` (game run: item same-key, parent align, state PlacedInLevel, courseNode null).
            // Every other placement of an existing pickup writes that public setter itself, so it is written here
            // once the factory's result is the same key, and then read back like the other facts.
            if (item != null && !item.WasCollected && item.Pointer == pickup.Pointer) pickup.CourseNode = chosen.Node;
            // Each native readback condition has its own reason, so a refusal names which fact the game did not keep.
            var mismatch = item == null || item.WasCollected ? "task-native-key-readback-item-missing"
                : item.Pointer != pickup.Pointer ? "task-native-key-readback-item-mismatch"
                : pickup.CourseNode == null || pickup.CourseNode.WasCollected ? "task-native-key-readback-node-missing"
                : pickup.CourseNode.Pointer != chosen.Node.Pointer ? "task-native-key-readback-node-mismatch"
                : transform.parent == null || transform.parent.WasCollected ? "task-native-key-readback-parent-missing"
                : transform.parent.Pointer != chosen.Align.Pointer ? "task-native-key-readback-parent-mismatch"
                : null;
            if (mismatch != null)
            {
                // The refusal carries every readback fact, so one record says which the game did not keep and whether
                // the factory reached its node write at all.
                var node = pickup.CourseNode;
                var parentNow = transform.parent;
                TaskRefused(chosen.Row.Id, mismatch, mismatch + " item=" + (item == null ? "none" : item.WasCollected ? "collected"
                    : item.Pointer == pickup.Pointer ? "same-key" : "other")
                    + " courseNode=" + (node == null ? "null" : node.WasCollected ? "collected" : node.Pointer == chosen.Node.Pointer ? "chosen" : "other")
                    + " parent=" + (parentNow == null ? "null" : parentNow.WasCollected ? "collected" : parentNow.Pointer == chosen.Align.Pointer ? "align" : "other")
                    + " status=" + (item?.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>()?.GetCurrentState().status.ToString() ?? "unreadable"));
                return false;
            }
            if (item!.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>() is not { } sync)
            { TaskRefused(chosen.Row.Id, "task-native-key-readback-sync-missing"); return false; }
            item.container = chosen.Storage;
            var state = sync.GetCurrentState();
            var placement = state.placement;
            placement.returnToParentOnRecall = true;
            state.placement = placement;
            sync.SetStateFromFactory(state);
            var readback = sync.GetCurrentState();
            AIG_CourseNode? stateNode = null;
            var stateHasNode = readback.placement.node.TryGet(out stateNode);
            if (item.container == null || item.container.WasCollected
                || item.container.Pointer != chosen.Storage.Pointer
                || readback.status != ePickupItemStatus.PlacedInLevel
                || !readback.placement.returnToParentOnRecall
                || !stateHasNode || stateNode == null
                || stateNode.WasCollected || stateNode.Pointer != chosen.Node.Pointer)
            {
                TaskRefused(chosen.Row.Id, "task-native-key-state-readback-mismatch",
                    "task-native-key-state-readback-mismatch container="
                    + (item.container == null || item.container.WasCollected ? "missing" : item.container.Pointer == chosen.Storage.Pointer ? "chosen" : "other")
                    + " status=" + readback.status + " recall=" + readback.placement.returnToParentOnRecall
                    + " stateNode=" + (!stateHasNode || stateNode == null ? "null" : stateNode.WasCollected ? "collected" : stateNode.Pointer == chosen.Node.Pointer ? "chosen" : "other"));
                return false;
            }
            TaskPlaced(chosen.Row, item, key.DataBlockID, -1, chosen.Node);
            return true;
        }
        catch (Exception error)
        {
            // The pickup is the level's original key, so a partial native failure is diagnosed and never destroyed.
            TaskRefused(chosen.Row.Id, "task-native-key-factory-failed", error.GetType().Name);
            return false;
        }
    }

    internal sealed record TaskPickupSlot(FixedItem Row, LG_ResourceContainer_Storage Storage, AIG_CourseNode Node,
        Transform Align, long Epoch, uint ItemId, int ChainIndex);

    internal bool PrepareTaskCarry(LG_PickupItem pickup, uint itemId, int chainIndex, out TaskPickupSlot? slot)
    {
        slot = null;
        if (!TaskContainersPending) return true;
        if (!IsRegionalTaskCarry(pickup)) return true;
        var source = pickup.SpawnNode!;
        var selected = SelectTaskSlot(source);
        if (selected == null) return false;
        var chosen = selected.Value;
        pickup.transform.SetParent(chosen.Align, false);
        pickup.transform.localPosition = Vector3.zero;
        pickup.transform.localRotation = Quaternion.identity;
        pickup.SpawnNode = chosen.Node;
        slot = new TaskPickupSlot(chosen.Row, chosen.Storage, chosen.Node, chosen.Align, _worldEpoch(), itemId, chainIndex);
        return true;
    }

    private bool IsRegionalTaskCarry(LG_PickupItem pickup)
    {
        // Cargo is identified by its native landing hierarchy, never by item ID or an objective flag.
        // Both Type 7 cells and Type 10 HSU keep the original cargo queue and subsequent cage movement.
        var landing = ElevatorShaftLanding.CargoAlign;
        for (var parent = pickup.transform.parent; parent != null; parent = parent.parent)
            if (landing != null && parent.Pointer == landing.Pointer) return false;
        var source = pickup.SpawnNode;
        return source?.m_zone != null && HasTaskZone(source.m_zone);
    }

    internal void CompleteTaskCarry(LG_PickupItem pickup, TaskPickupSlot slot)
    {
        if (_disposed || _disposing || slot.Epoch != _worldEpoch())
        { TaskRefused(slot.Row.Id, "task-world-stale"); return; }
        var item = pickup.gameObject.GetComponentInChildren<ItemInLevel>(true);
        if (item == null || item.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>() is not { } sync
            || !FixedContainerPlacement.Add(slot.Storage, item, out var reason))
        {
            TaskRefused(slot.Row.Id, "task-native-pickup-unsupported");
            if (item != null) UnityEngine.Object.Destroy(item.gameObject);
            return;
        }
        var state = sync.GetCurrentState();
        var placement = state.placement; placement.returnToParentOnRecall = true; state.placement = placement;
        sync.SetStateFromFactory(state);
        TaskPlaced(slot.Row, item, slot.ItemId, slot.ChainIndex, slot.Node);
    }

    private static void TaskPlaced(FixedItem row, ItemInLevel item, uint id, int chain, AIG_CourseNode node)
        => MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.TaskContainerItem, "Native task original placed in authored container",
            RuntimeLogValue.Quote("id", row.Id), RuntimeLogValue.Quote("container", row.Id), RuntimeLogValue.Of("item", (long)id),
            RuntimeLogValue.Of("chainIndex", (long)chain), RuntimeLogValue.Of("instance", (long)item.GetInstanceID()), RuntimeLogValue.Of("node", Node(node)));

    internal static void TaskRefused(string id, string reason, string detail = "")
        => MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.TaskContainerRefused, detail.Length == 0 ? reason : detail,
            RuntimeLogValue.Quote("id", id), RuntimeLogValue.Quote("reason", reason));

    private static void TaskPreparationFailed(Exception error)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Error)) return;
        var exception = MapLog.SafeDetail(error.ToString());
        var parts = (exception.Length + 255) / 256;
        for (var part = 0; part < parts; part++)
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.TaskContainerRefused,
                "Task container preparation failed; join values.exception in part order.",
                RuntimeLogValue.Quote("id", ""), RuntimeLogValue.Of("reason", "task-container-preparation-failed"),
                RuntimeLogValue.Of("part", part + 1), RuntimeLogValue.Of("parts", parts),
                RuntimeLogValue.Of("exception", exception.Substring(part * 256, Math.Min(256, exception.Length - part * 256))));
    }
}

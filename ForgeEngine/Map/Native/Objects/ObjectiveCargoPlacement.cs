using System;
using System.Collections.Generic;
using AIGraph;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

internal sealed partial class FixedPlacementSession
{
    // Captured around the original call, never a second factory invocation or a scan by item id.
    internal sealed record CargoBuild(ElevatorCargoCage Cage, long Epoch, MapLevelReference Level,
        int Chain, IntPtr Queue, Transform[] Before, int Count, int Generic);
    private sealed record CargoSupply(CargoBuild Build, Transform[] Roots, string? Refusal);
    private readonly Dictionary<int, CargoSupply> _cargoSupply = new();
    private readonly HashSet<IntPtr> _claimedCargo = new();
    private long _cargoEpoch = -1;
    /// <summary>The vanilla ItemDataBlock persistentID of the power cell an elevator cargo objective (eWardenObjectiveType.PowerCellDistribution) carries.</summary>
    private const uint BatteryItemPersistentId = 131;
    private MapLevelReference? _cargoLevel;

    internal CargoBuild? BeginObjectiveCargo(ElevatorCargoCage cage, int chain)
    {
        if (_disposed || _disposing || _worldEpoch() == 0 || _level() is not { } level) return null;
        var selected = false;
        foreach (var row in _items) if (row.Level == level && row.ObjectiveCargo is { Layer: 0 } role && role.ChainIndex == chain) selected = true;
        if (!selected) return null;
        if (_cargoEpoch != _worldEpoch() || _cargoLevel != level)
        { _cargoSupply.Clear(); _claimedCargo.Clear(); _cargoEpoch = _worldEpoch(); _cargoLevel = level; }
        if (!WardenObjectiveManager.TryGetWardenObjectiveDataForLayer(LG_LayerType.MainLayer, chain, out var data)
            || data == null || data.Type != eWardenObjectiveType.PowerCellDistribution || data.PowerCellsToDistribute <= 0 || cage.m_itemsToMoveToCargo == null)
        { CargoRefused(chain, "fixed-cargo-native-objective-missing"); return null; }
        var queue = cage.m_itemsToMoveToCargo;
        var before = new Transform[queue.Count];
        for (var i = 0; i < before.Length; i++) before[i] = queue[i];
        return new CargoBuild(cage, _worldEpoch(), level, chain, queue.Pointer, before,
            data.PowerCellsToDistribute, data.GenericItemFromStart == 0 ? 0 : 1);
    }

    internal void CompleteObjectiveCargo(CargoBuild build)
    {
        if (_disposed || _disposing || build.Epoch != _worldEpoch() || build.Level != _level())
        { CargoRefused(build.Chain, "fixed-cargo-world-stale"); return; }
        if (_cargoSupply.TryGetValue(build.Chain, out var prior) && prior.Build.Epoch == build.Epoch && prior.Build.Level == build.Level)
        {
            _cargoSupply[build.Chain] = prior with { Refusal = "fixed-cargo-native-setup-repeated" };
            CargoRefused(build.Chain, "fixed-cargo-native-setup-repeated"); return;
        }
        var queue = build.Cage.m_itemsToMoveToCargo;
        string? refusal = null;
        var roots = new Transform[build.Count];
        if (queue == null || queue.Pointer != build.Queue || queue.Count != build.Before.Length + build.Generic + build.Count)
            refusal = "fixed-cargo-queue-delta-mismatch";
        else
        {
            var seen = new HashSet<IntPtr>();
            for (var i = 0; i < build.Before.Length; i++)
                if (queue[i] == null || build.Before[i] == null || queue[i].Pointer != build.Before[i].Pointer)
                    refusal = "fixed-cargo-queue-prefix-mismatch";
                else seen.Add(queue[i].Pointer);
            for (var i = 0; i < build.Generic; i++)
                if (queue[build.Before.Length + i] == null || !seen.Add(queue[build.Before.Length + i].Pointer))
                    refusal = "fixed-cargo-queue-identity-mismatch";
            for (var i = 0; i < roots.Length; i++)
            {
                var root = queue[build.Before.Length + build.Generic + i];
                var item = root == null ? null : root.gameObject.GetComponentInChildren<ItemInLevel>(true);
                if (root == null || root.WasCollected || !seen.Add(root.Pointer) || item == null || item.WasCollected
                    || item.ItemDataBlock == null || item.ItemDataBlock.persistentID != BatteryItemPersistentId
                    || item.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>() is not { } sync || sync.WasCollected
                    || item.gameObject.GetComponentInChildren<Interact_Base>(true) == null)
                    refusal = "fixed-cargo-native-battery-missing";
                else roots[i] = root;
            }
        }
        _cargoSupply[build.Chain] = new CargoSupply(build, roots, refusal);
        if (refusal != null) CargoRefused(build.Chain, refusal);
    }

    private void CargoRefused(int chain, string reason)
    {
        foreach (var row in _items) if (row.Level == _level() && row.ObjectiveCargo is { Layer: 0 } cargo && cargo.ChainIndex == chain)
            Refuse(row.Id, reason);
    }

    private bool PlaceObjectiveCargo(FixedItem row, long epoch)
    {
        var cargo = row.ObjectiveCargo!;
        if (cargo.Layer != 0 || !_cargoSupply.TryGetValue(cargo.ChainIndex, out var supply))
        { Refuse(row.Id, "fixed-cargo-native-battery-not-found"); return false; }
        var build = supply.Build;
        if (build.Epoch != epoch || build.Level != row.Level || build.Cage == null || build.Cage.WasCollected)
        { Refuse(row.Id, "fixed-cargo-world-stale"); return false; }
        if (supply.Refusal != null) { Refuse(row.Id, supply.Refusal); return false; }
        if (cargo.Count != build.Count || cargo.Index < 0 || cargo.Index >= build.Count
            || !WardenObjectiveManager.TryGetWardenObjectiveDataForLayer(LG_LayerType.MainLayer, cargo.ChainIndex, out var data)
            || data == null || data.Type != eWardenObjectiveType.PowerCellDistribution || data.PowerCellsToDistribute != cargo.Count)
        { Refuse(row.Id, "fixed-cargo-objective-mismatch"); return false; }
        if (row.ContainerId == null || !_boxes.TryGetValue(row.ContainerId, out var box) || box.Storage == null || box.Storage.WasCollected
            || box.Row.Level != row.Level || !SameZone(box.Row.Locator, row.Locator) || box.Row.Locator.ZoneAuthorId != row.Locator.ZoneAuthorId
            || !SameZone(row.Locator, box.Node.m_zone))
        { Refuse(row.Id, "container-missing-or-wrong-zone"); return false; }
        var root = supply.Roots[cargo.Index];
        var queue = build.Cage.m_itemsToMoveToCargo;
        var queueIndex = -1;
        if (queue != null && queue.Pointer == build.Queue && root != null && !root.WasCollected)
            for (var i = 0; i < queue.Count; i++) if (queue[i] != null && queue[i].Pointer == root.Pointer)
            {
                if (queueIndex >= 0) { Refuse(row.Id, "fixed-cargo-queue-identity-mismatch"); return false; }
                queueIndex = i;
            }
        if (queueIndex < 0 || root == null || _claimedCargo.Contains(root.Pointer))
        { Refuse(row.Id, "fixed-cargo-native-battery-not-found"); return false; }
        var item = root.gameObject.GetComponentInChildren<ItemInLevel>(true);
        var sync = item?.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
        if (item == null || item.WasCollected || item.ItemDataBlock?.persistentID != BatteryItemPersistentId || sync == null || sync.WasCollected
            || item.gameObject.GetComponentInChildren<Interact_Base>(true) == null
            || FixedItemIdentity.Key(row.Locator.Dimension, row.Locator.Layer, row.Locator.LocalIndex, row.Id) == null)
        { Refuse(row.Id, "fixed-cargo-native-battery-missing"); return false; }
        var align = FixedContainerPlacement.FreeSlot(box.Storage, out var refusal);
        if (align == null) { Refuse(row.Id, refusal); return false; }
        // Move the factory root, not a replacement pickup. The same sync/objective/lifetime components survive.
        var old = item.container ?? item.GetComponentInParent<LG_ResourceContainer_Storage>();
        if (old != null) { old.OnItemRemoved(item.gameObject); old.AddNumberOfItemsToContainer(-1); }
        root.SetParent(align, false); root.localPosition = Vector3.zero; root.localRotation = Quaternion.identity;
        item.CourseNode = box.Node;
        // Same node-handler path as the vanilla pickup factory: carry SpawnNode reads its terminal component.
        foreach (var handler in root.gameObject.GetComponentsInChildren<iLG_SpawnedInNodeHandler>()) handler.SpawnNode = box.Node;
        var pickup = root.gameObject.GetComponent<LG_PickupItem>();
        if (pickup != null) pickup.SpawnNode = box.Node;
        var node = default(pCourseNode); node.Set(box.Node);
        var state = sync.GetCurrentState();
        state.status = ePickupItemStatus.PlacedInLevel;
        state.placement = new pPickupPlacement { position = align.position, rotation = align.rotation, node = node, returnToParentOnRecall = true };
        sync.SetStateFromFactory(state);
        if (!FixedContainerPlacement.Add(box.Storage, item, out refusal)) { Refuse(row.Id, refusal); return false; }
        queue!.RemoveAt(queueIndex); // Arrival/drop-in cannot move this native root back into the cage.
        _claimedCargo.Add(root.Pointer);
        if (!RegisterIdentity(row, item, epoch)) return false;
        FixedItemRules.Apply(item.transform, row);
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedContainerItem, "Native task battery moved into selected container",
            RuntimeLogValue.Quote("id", row.Id), RuntimeLogValue.Quote("container", row.ContainerId),
            RuntimeLogValue.Of("layer", (long)cargo.Layer), RuntimeLogValue.Of("chainIndex", (long)cargo.ChainIndex),
            RuntimeLogValue.Of("index", (long)cargo.Index), RuntimeLogValue.Of("count", (long)cargo.Count),
            RuntimeLogValue.Of("nativeRoot", (long)root.GetInstanceID()), RuntimeLogValue.Of("instance", (long)item.GetInstanceID()),
            RuntimeLogValue.Of("syncInstance", (long)sync.gameObject.GetInstanceID()), RuntimeLogValue.Of("queueRemoved", true),
            RuntimeLogValue.Of("node", Node(box.Node)));
        return true;
    }
}

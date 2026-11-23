using AIGraph;
using GameData;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>The native factory recipe with an explicit origin node. WorldEventUtils is a convenience query:
/// it re-samples navigation at the align and rejects resource/commodity slots. Authored placements already know
/// their node, so use the factory's SpawnItem/SetStateFromFactory entries without repeating that query.
/// LG_PickupItem.SpawnPickupItemAndGetComp creates the repeller state before spawning and passes its byte index;
/// SetupAsSmallGenericPickup additionally calls SetupFromLevelgen. Keep both initialization steps here.</summary>
internal static class PlacedItemSpawn
{
    internal static bool TrySpawn(uint itemId, Transform align, AIG_CourseNode node, double amount, uint seed,
        out ItemInLevel? placed, out HeavyFogRepellerGlobalState? fogState, out string refusal, bool inContainer = false)
    {
        placed = null;
        fogState = null;
        refusal = "item-block-missing";
        var block = GameDataBlockBase<ItemDataBlock>.GetBlock(itemId);
        if (block == null) return false;
        refusal = "slot-not-placeable";
        if (FixedPlacement.RouteOf((int)block.inventorySlot) == null) return false;
        refusal = "amount-refused";
        if (FixedPlacement.AmountRefusal((int)block.inventorySlot, amount) != null) return false;
        refusal = "items-not-ready";
        if (!ItemSpawnManager.ReadyToSpawnItems) return false;
        refusal = "pickup-mode-missing";
        if (!ItemSpawnManager.ItemHasMode(itemId, ItemMode.Pickup)) return false;
        // A PickupPrefabs filename is not a pickup component. The game's static HSU visual
        // (177) is a mesh-only scene prop; SpawnItem's fallback Item is not an ItemInLevel.
        // Inspect the loaded native prefabs before creating any visual or global item state.
        refusal = "pickup-core-missing";
        var prefabs = ItemSpawnManager.GetItemPrefabs(itemId, ItemMode.Pickup);
        if (prefabs == null || prefabs.Count == 0) return false;
        // SpawnItem instantiates index zero as the pickup root and attaches indices 1..n as
        // visuals (native 12A218D/12A21DA, 12A22C6). Carry items such as a power cell have
        // a generic pickup core followed by a mesh-only visual. Match SpawnItem's child lookup
        // on that first prefab; the attached visuals are not independent pickups.
        if (prefabs[0] == null || prefabs[0].GetComponentInChildren<ItemInLevel>() == null) return false;
        foreach (var prefab in prefabs) if (prefab == null) return false;
        if (inContainer && prefabs[0].GetComponentInChildren<Interact_Base>() == null)
        { refusal = "container-pickup-interaction-missing"; return false; }
        var origin = default(pCourseNode);
        origin.Set(node);
        var custom = new pItemData_Custom
        {
            ammo = (float)((int)block.inventorySlot == FixedPlacement.ResourcePackSlot
                ? FixedPlacement.NativeAmmoForResourcePack(amount) : amount)
        };
        if (itemId == FixedPlacement.FogRepellerItemId)
        {
            refusal = "fog-repeller-state-create-refused";
            if (!CarryItemWithGlobalStateManager.TryCreateItemInstance(eCarryItemWithGlobalStateType.FogRepeller,
                align, out var state, out var index)) return false;
            fogState = state?.TryCast<HeavyFogRepellerGlobalState>();
            if (fogState == null || fogState.WasCollected) return false;
            custom.byteId = index;
        }
        var data = new pItemData
        {
            itemID_gearCRC = itemId,
            slot = block.inventorySlot,
            originLayer = node.LayerType,
            originCourseNode = origin,
            custom = custom
        };
        refusal = "spawn-refused";
        var spawned = ItemSpawnManager.SpawnItem(itemId, ItemMode.Pickup, align.position, align.rotation, true, data, align);
        placed = spawned?.TryCast<ItemInLevel>();
        if (placed == null || placed.WasCollected) return false;
        placed.CourseNode = node;
        // LG_PickupItem.SpawnPickupItemAndGetComp (1A8E842..1A8E8FE) initializes every
        // node handler in the assembled hierarchy. CarryItemPickup_Core reads SpawnNode
        // from its terminal component; setting ItemInLevel.CourseNode alone leaves it unset.
        foreach (var handler in placed.GetComponentsInChildren<iLG_SpawnedInNodeHandler>())
            handler.SpawnNode = node;
        if (placed.TryCast<GenericSmallPickupItem_Core>() is { } small)
        {
            small.SpawnNode = node;
            // The game's pickup callback notifies the objective only for this native flag.
            // Read the active objective of the item's actual layer; presentation pickups and
            // another layer's items must not acquire a goal by sharing a name or a prefab.
            var manager = WardenObjectiveManager.Current;
            var objectiveItem = manager != null
                && manager.TryGetActiveWardenObjectiveData(node.LayerType, out var objective)
                && objective.Type == eWardenObjectiveType.GatherSmallItems
                && objective.Gather_ItemId == itemId
                && !objective.DoNotMarkPickupItemsAsWardenObjectives;
            small.SetupFromLevelgen(unchecked((int)seed), objectiveItem);
        }
        refusal = "pickup-sync-missing";
        var sync = placed.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
        if (sync == null || sync.WasCollected) return false;
        sync.SetStateFromFactory(new pPickupItemState
        {
            status = ePickupItemStatus.PlacedInLevel,
            placement = new pPickupPlacement
            {
                position = align.position,
                rotation = align.rotation,
                node = origin,
                returnToParentOnRecall = inContainer
            },
            custom = custom
        });
        refusal = "";
        return true;
    }
}

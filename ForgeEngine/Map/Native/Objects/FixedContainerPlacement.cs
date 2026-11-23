using System;
using AIGraph;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>M4 containers use the vanilla marker prefab, nested prefab spawners and container Setup.
/// Setup registers storage on its actual course node. OnItemAdded is the game's general container-entry
/// route: unlike SpawnContent's SmallGenericPickup case it accepts a native carry pickup too.</summary>
internal static class FixedContainerPlacement
{
    internal const string BoxPrefab = "Assets/AssetPrefabs/Complex/Mining/SubMarkersDigSite/Props/mini_120x120x120/SM_mini_120x120x120_weak_box_01.prefab";
    internal const string LockerPrefab = "Assets/AssetPrefabs/Complex/Mining/SubMarkersDigSite/Props/mining_container_locker_200x300x50/mining_container_locker_200x300x50_weak_01.prefab";

    internal static LG_ResourceContainer_Storage? Create(FixedItem row, Transform parent, AIG_CourseNode node,
        Vector3 position, Quaternion rotation, out string refusal)
    {
        refusal = "container-prefab-missing";
        var prefab = ForgeCore.GameAssets.Get<GameObject>(row.ContainerType == "box" ? BoxPrefab : LockerPrefab);
        if (prefab == null) return null;
        var host = UnityEngine.Object.Instantiate(prefab, parent);
        try
        {
            host.transform.position = position;
            host.transform.rotation = rotation;
            foreach (var spawner in host.GetComponentsInChildren<LG_PrefabSpawner>())
            { spawner.m_zone = node.m_zone; spawner.m_area = node.m_area; }
            refusal = "container-prefab-build";
            LG_Factory.FindAndBuildSelectorsAndSpawners(host, FixedPlacement.SeedOf(row.Id), true);
            refusal = "container-node-handlers";
            foreach (var handler in host.GetComponentsInChildren<iLG_SpawnedInNodeHandler>()) handler.SpawnNode = node;
            refusal = "container-storage-missing";
            var core = host.GetComponentInChildren<LG_WeakResourceContainer>();
            var storage = host.GetComponentInChildren<LG_ResourceContainer_Storage>();
            if (core == null || storage == null || storage.m_storageSlots == null || storage.m_storageSlots.Length == 0)
            { UnityEngine.Object.Destroy(host); return null; }
            // An authored container holds only its selected pickups, never the marker's random prop contents.
            storage.m_spawnProps = false;
            var locked = row.Lock?.LockedFromStart ?? false;
            var weakType = (eWeakLockType)(int)(row.Lock?.WeakLockType ?? FixedWeakLockType.None);
            refusal = "container-native-setup";
            core.Setup(locked);
            // Vanilla's builder creates a weak lock only for a locked container (0x159ABCE-0x159ABF7).
            if (locked) { refusal = "container-native-lock-setup"; core.SetupWeakLock(weakType); }
            if (core.IsLocked() != locked || locked && (core.WeakLockComponent == null || core.WeakLockComponent.m_lockType != weakType))
            { refusal = "container-lock-setup-mismatch"; UnityEngine.Object.Destroy(host); return null; }
            core.SetContainterEmptyGFX(true);
            if (core.SpawnNode == null || core.SpawnNode.Pointer != node.Pointer)
            { refusal = "container-node-mismatch"; UnityEngine.Object.Destroy(host); return null; }
            refusal = "";
            return storage;
        }
        catch (Exception error)
        {
            UnityEngine.Object.Destroy(host);
            throw new InvalidOperationException("Container initialization failed: id=" + row.Id + " stage=" + refusal, error);
        }
    }

    /// <summary>Each native storage slot holds one pickup. The Keycard align is also the original
    /// SmallGenericPickup align. OnItemAdded imposes no inventory-slot restriction; carry geometry is
    /// retained unchanged. No re-sampling, another container or floor-drop is used on refusal.</summary>
    internal static Transform? FreeSlot(LG_ResourceContainer_Storage storage, out string refusal, int reservedSlots = 0)
    {
        refusal = "container-slot-unsupported";
        var slots = storage.m_storageSlots;
        if (slots == null) return null;
        var supported = false;
        var index = 0;
        foreach (var slot in slots)
        {
            var align = slot?.Keycard;
            if (align == null || align.WasCollected) continue;
            supported = true;
            if (index++ < reservedSlots) continue;
            if (align.gameObject.GetComponentInChildren<ItemInLevel>(true) != null) continue;
            refusal = "";
            return align;
        }
        if (supported) refusal = "container-full";
        return null;
    }

    internal static bool Add(LG_ResourceContainer_Storage storage, ItemInLevel item, out string refusal)
    {
        refusal = "container-pickup-interaction-missing";
        if (item.gameObject.GetComponentInChildren<Interact_Base>() == null) return false;
        // OnPickedUp reads this native cache after detaching the transform (147B697).
        // OnItemAdded only updates interactions/count, so moving a generated key must also replace its cache.
        item.container = storage;
        storage.OnItemAdded(item.gameObject);
        refusal = "";
        return true;
    }

    /// <summary>Takes over the original pickup the level generated for a door's key and moves it to the authored
    /// slot; it never spawns a second key. <paramref name="standsInLevel"/> says the slot is a point on the floor and
    /// not a container slot: a container's own <see cref="Add"/> switches the interaction to the container's state, but a
    /// key that stands in the level has no container to do that, and a key the generator had placed in a container
    /// had its interaction switched off by that placement.</summary>
    internal static bool MoveKey(MapObjectReference address, Transform align, AIG_CourseNode node,
        out ItemInLevel? placed, out string refusal, bool standsInLevel = false)
    {
        placed = null;
        refusal = "door-key-unresolved";
        var door = DoorObservation.ByAddress(address);
        var key = door?.m_locks?.TryCast<LG_SecurityDoor_Locks>()?.m_gateKeyItemNeeded;
        var pickup = key?.keyPickupCore;
        if (pickup == null || pickup.WasCollected || pickup.m_terminalItem == null) return false;
        var sync = pickup.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
        var interaction = pickup.gameObject.GetComponentInChildren<Interact_Base>();
        if (sync == null || sync.WasCollected || interaction == null) return false;
        var old = pickup.GetComponentInParent<LG_ResourceContainer_Storage>();
        if (old != null) { old.OnItemRemoved(pickup.gameObject); old.AddNumberOfItemsToContainer(-1); }
        pickup.SetPlacementFromFactory(align, node);
        pickup.CourseNode = node;
        pickup.m_terminalItem.SpawnNode = node;
        var origin = default(pCourseNode); origin.Set(node);
        var state = sync.GetCurrentState();
        state.status = ePickupItemStatus.PlacedInLevel;
        state.placement = new pPickupPlacement
        {
            position = align.position,
            rotation = align.rotation,
            node = origin,
            returnToParentOnRecall = true
        };
        sync.SetStateFromFactory(state);
        if (standsInLevel) interaction.SetActive(true);
        placed = pickup;
        refusal = "";
        return true;
    }
}

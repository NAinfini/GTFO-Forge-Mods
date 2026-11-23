using System.Globalization;
using ForgeEngine.Framework;
using Il2CppInterop.Runtime;
using LevelGeneration;
using SNetwork;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Native replicator identities are shared by peers; Unity pointers only distinguish local lifetimes.
/// Neither the random cabinet serial nor a Unity instance id is an author address.</summary>
internal static class LevelInstanceObservation
{
    internal static EntityReference? Container(LG_ResourceContainer_Sync sync, LevelObjectModule module,
        out EntityReference? zone, out int serial)
    {
        zone = null; serial = -1;
        if (sync == null || sync.WasCollected) return null;
        var core = sync.GetComponentInParent(Il2CppType.Of<LG_WeakResourceContainer>())?.TryCast<LG_WeakResourceContainer>();
        if (core == null || core.WasCollected || core.m_syncComp == null
            || core.m_syncComp.Pointer != sync.Pointer || core.SpawnNode?.m_zone is not { } nativeZone
            || !ZoneIndex.Current(module.WorldEpoch).TryCoordinates(nativeZone, out var coordinates)) return null;
        var replicator = sync.GetStateReplicator()?.Replicator;
        if (replicator == null || replicator.WasCollected) return null;
        var network = default(SNetStructs.pReplicator);
        network.SetID(replicator);
        if (!network.IsValid()) return null;
        serial = core.m_serialNumber;
        zone = RuntimeZones.Reference(module.WorldEpoch, coordinates.Dimension, coordinates.Layer, coordinates.Zone);
        long pointer = sync.Pointer.ToInt64();
        bool Live(object value) => value is LG_ResourceContainer_Sync current && !current.WasCollected
            && current.gameObject != null && current.Pointer.ToInt64() == pointer
            && network.TryGetID(out var active) && active != null && active.Pointer == replicator.Pointer
            && ZoneIndex.Current(module.WorldEpoch).TryCoordinates(nativeZone, out _);
        return module.CaptureInstance(module.WorldEpoch, "container/network/" + network.keyPlusOne.ToString(CultureInfo.InvariantCulture),
            sync, Live, pointer, (value, reference) => Snapshot(reference, ((LG_ResourceContainer_Sync)value).transform, "container"));
    }

    internal static EntityReference? Item(Item? item, LevelObjectModule module)
    {
        if (item == null || item.WasCollected) return null;
        var network = item.Get_pItemData().replicatorRef;
        // Pickup subclasses also write their state replicator into Get_pItemData().replicatorRef.
        // A valid reference is not proof of a dynamic ItemReplicator: resolve its actual native type.
        if (!network.IsValid()) return FactoryItem(item, module);
        if (!network.TryGetID(out var native) || native == null) return null;
        var replicator = native.TryCast<ItemReplicator>();
        if (replicator == null) return FactoryItem(item, module, native);
        return replicator.Item == null || replicator.Item.Pointer != item.Pointer ? null : Item(replicator, module);
    }

    private static EntityReference? FactoryItem(Item item, LevelObjectModule module, IReplicator? expected = null)
    {
        var pickup = item.TryCast<ItemInLevel>();
        var sync = pickup?.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
        if (sync == null || sync.WasCollected || sync.item == null || sync.item.Pointer != item.Pointer
            || !item.Get_pItemData().originCourseNode.TryGet(out var origin) || origin?.m_zone is not { } zone
            || !ZoneIndex.Current(module.WorldEpoch).TryCoordinates(zone, out _)) return null;
        // GetReplicator dereferences m_stateReplicator in native code, so an unregistered pickup must
        // read the nullable wrapper first. On pickup vanilla clears CourseNode; originCourseNode persists.
        var replicator = sync.GetStateReplicator()?.Replicator;
        if (replicator == null || replicator.WasCollected
            || expected != null && expected.Pointer != replicator.Pointer) return null;
        var network = default(SNetStructs.pReplicator);
        network.SetID(replicator);
        if (!network.IsValid()) return null;
        long pointer = sync.Pointer.ToInt64();
        bool Live(object value) => value is LG_PickupItem_Sync current && !current.WasCollected && current != null
            && current.Pointer.ToInt64() == pointer && current.item is { WasCollected: false } view && view != null
            && view.Pointer == item.Pointer && view.TryCast<ItemInLevel>()?.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>()?.Pointer == current.Pointer
            && current.GetStateReplicator()?.Replicator is { WasCollected: false } registered && registered.Pointer == replicator.Pointer
            && network.TryGetID(out var active) && active != null && active.Pointer == replicator.Pointer
            && ZoneIndex.Current(module.WorldEpoch).TryCoordinates(zone, out _);
        return module.CaptureInstance(module.WorldEpoch, ItemKey(network.keyPlusOne), sync, Live, pointer,
            (value, reference) => ((LG_PickupItem_Sync)value).item is { } view
                ? Snapshot(reference, view.transform, "item") : null);
    }

    internal static EntityReference? Item(ItemReplicator replicator, LevelObjectModule module)
    {
        if (replicator == null || replicator.WasCollected || replicator.Item == null) return null;
        var network = default(SNetStructs.pReplicator);
        network.SetID(replicator.Cast<IReplicator>());
        if (!network.IsValid() || !replicator.GetSpawnData().courseNode.TryGet(out var origin) || origin?.m_zone is not { } originZone
            || !ZoneIndex.Current(module.WorldEpoch).TryCoordinates(originZone, out _)) return null;
        long pointer = replicator.Pointer.ToInt64();
        bool Live(object value) => value is ItemReplicator current && !current.WasCollected
            && network.TryGetID(out var active) && active != null && active.Pointer.ToInt64() == pointer
            && current.Item is { } view && !view.WasCollected && view.gameObject != null
            && ZoneIndex.Current(module.WorldEpoch).TryCoordinates(originZone, out _);
        return module.CaptureInstance(module.WorldEpoch, ItemKey(network.keyPlusOne), replicator, Live, pointer,
            (value, reference) => ((ItemReplicator)value).Item is { } view
                ? Snapshot(reference, view.transform, "item") : null);
    }

    internal static string ItemKey(ushort key) => "item/network/" + key.ToString(CultureInfo.InvariantCulture);

    internal static void Despawn(ItemReplicator replicator, LevelObjectModule module)
    {
        // The native registry may already have unregistered this key when its despawn callback runs. The
        // callback's exact pointer invalidates only the previously captured instance in this world's ledger.
        module.InvalidateNativeInstance(module.WorldEpoch, replicator.Pointer.ToInt64());
    }

    private static RuntimeEntitySnapshot Snapshot(EntityReference reference, Transform transform, string tag)
    {
        var point = transform.position;
        return new RuntimeEntitySnapshot(reference, EntityKinds.LevelObject, "level-object", "alive", new[] { tag },
            new[] { "presentation.marker" }, new double[] { point.x, point.y, point.z });
    }
}

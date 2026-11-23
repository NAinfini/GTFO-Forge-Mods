using ForgeEngine.Framework;
using GameData;
using HarmonyLib;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>Observe a failed native request without consuming markers, drawing random values or changing
/// its target. An already-empty native surplus-removal request is already settled and is not a pickup failure.
/// A completed factory job does not prove a pickup spawned. The original native error stays enabled.
/// Native call edges and the limits of the old patrol evidence are in evidence/pickup-marker-20403457.json.</summary>
[HarmonyPatch(typeof(LG_PopulateFunctionMarkersInZoneJob), nameof(LG_PopulateFunctionMarkersInZoneJob.TriggerFunctionBuilder))]
internal static class PickupMarkerObservation
{
    // ExpeditionFunction.SmallPickupItem in native build 20403457.
    private const ExpeditionFunction Function = (ExpeditionFunction)8;
    internal sealed record Snapshot(int Count, float Weight, int QueueCount, LG_MarkerSpawnerGroup? Group);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(LG_PopulateFunctionMarkersInZoneJob __instance, LG_DistributeItem distItem,
        out Snapshot? __state)
    {
        __state = null;
        if (__instance.m_fallbackMode || distItem.m_function != Function) return;
        var groups = distItem.m_assignedNode?.m_area?.m_markerSpawnerGroups;
        var group = groups != null && groups.Length > (int)Function ? groups[(int)Function] : null;
        // Undefined is the native surplus-removal recipe (LG_DistributePickUpItem.ShouldBeRemoved, RVA 0x1445490).
        // An existing but empty group has already reached that request's target state; it is not a failed item spawn.
        if (group?.m_spawners?.Count == 0)
        {
            var pickup = distItem.TryCast<LG_DistributePickUpItem>();
            if (pickup != null && pickup.m_type == ePickupItemType.Undefined) return;
        }
        __state = new Snapshot(group?.m_spawners?.Count ?? -1, group?.m_weightSum ?? 0,
            __instance.m_distributionData.GenericFunctionItems.m_itemQueue.Count, group);
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_PopulateFunctionMarkersInZoneJob __instance, LG_DistributeItem distItem,
        LG_MarkerSpawner deepestSpawner, Snapshot? __state)
    {
        if (__state == null || deepestSpawner != null) return;
        var node = distItem.m_assignedNode;
        var zone = node?.m_zone;
        var area = node?.m_area;
        var pickup = distItem.TryCast<LG_DistributePickUpItem>();
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.PickupMarkerUnavailable,
            "Native pickup marker selection returned no spawner; item selection and spawning remain unconfirmed.",
            RuntimeLogValue.Of("dimension", zone == null ? -1 : (int)zone.m_dimensionIndex),
            RuntimeLogValue.Of("layer", zone?.m_layer == null ? -1 : (int)zone.m_layer.m_type),
            RuntimeLogValue.Of("zone", zone == null ? -1 : (int)zone.LocalIndex),
            RuntimeLogValue.Of("zoneSeed", zone?.m_settings?.m_zoneData?.SubSeed ?? -1),
            RuntimeLogValue.Of("area", area == null ? -1L : area.UID),
            RuntimeLogValue.Of("request", distItem.Pointer.ToInt64()),
            RuntimeLogValue.Of("markerOffset", distItem.m_markerSeedOffset),
            RuntimeLogValue.Of("markersBefore", __state.Count),
            RuntimeLogValue.Of("weightBefore", __state.Weight),
            RuntimeLogValue.Of("markersAfter", __state.Group?.m_spawners?.Count ?? -1),
            RuntimeLogValue.Of("fallbackQueueDelta", __instance.m_distributionData.GenericFunctionItems.m_itemQueue.Count - __state.QueueCount),
            RuntimeLogValue.Of("pickupType", pickup == null ? "unavailable" : pickup.m_type.ToString()),
            RuntimeLogValue.Of("requestedItem", pickup?.m_genericItemId ?? 0),
            RuntimeLogValue.Of("consumableDistribution", pickup?.m_consumableData?.persistentID ?? 0),
            RuntimeLogValue.Of("allowFallback", distItem.m_allowFunctionFallback),
            RuntimeLogValue.Of("reason", __state.Count < 0 ? "native-marker-group-unavailable"
                : __state.Count == 0 ? "native-marker-pool-empty"
                : __state.Weight <= 0 ? "native-marker-weight-nonpositive" : "native-marker-selection-returned-null"));
    }
}

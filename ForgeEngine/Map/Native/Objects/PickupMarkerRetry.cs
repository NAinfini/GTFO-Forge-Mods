using ForgeEngine.Framework;
using HarmonyLib;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>The generic function-item build. The prefix first runs the pickup retry, then opens the generator
/// placement's spawn slot (<see cref="GeneratorPlacementBuild.BeginSpawn"/>) and claims the request for an authored
/// point (<see cref="FixedFunctionPoints.Claim"/>); the finalizer closes both.
///
/// The pickup retry: the native generic queue erases the pickup subclass when rebuilding its builder. Keep the
/// original pickup recipe and delegate selection, random draws, requeueing and floor placement to the same
/// native TriggerFunctionBuilder that BuildPickupItems uses. Other requests retain the original path.</summary>
[HarmonyPatch(typeof(LG_PopulateFunctionMarkersInZoneJob), nameof(LG_PopulateFunctionMarkersInZoneJob.BuildGenericFunctionItems))]
internal static class GenericFunctionItemsHooks
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(LG_PopulateFunctionMarkersInZoneJob __instance, eDistributionQueue queue, ref bool __result,
        out GeneratorPlacementBuild.Slot? __state)
    {
        __state = null;
        var run = PickupMarkerRetry(__instance, queue, ref __result);
        __state = GeneratorPlacementBuild.BeginSpawn(__instance, queue);
        if (run && MapHost.Session is { } session)
        {
            // The head of the queue this call consumes: the native build dequeues exactly it.
            var requests = (queue == eDistributionQueue.GenericFunctionItems ? __instance.m_distributionData?.GenericFunctionItems
                : __instance.m_distributionData?.PrioritizedClusterItems)?.m_itemQueue;
            LG_DistributeItem? head = requests is { Count: > 0 } ? requests.Peek() : null;
            session.Build(session.Placements, _ => FixedFunctionPoints.Claim(__instance, head), "fixed object point claim");
        }
        return run;
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    internal static void Finalizer(GeneratorPlacementBuild.Slot? __state)
    {
        FixedFunctionPoints.Release();
        GeneratorPlacementBuild.EndSpawn(__state);
    }

    private static bool PickupMarkerRetry(LG_PopulateFunctionMarkersInZoneJob job, eDistributionQueue queue, ref bool result)
    {
        var requests = queue == eDistributionQueue.GenericFunctionItems
            ? job.m_distributionData.GenericFunctionItems : null;
        if (requests == null || requests.Count == 0
            || requests.m_itemQueue.Peek()?.TryCast<LG_DistributePickUpItem>() is not { } pickup) return true;
        // The game's TryGetItem is this Dequeue behind a Count test (the head was just peeked): consume exactly the inspected head once.
        // Not TryGetItem itself: its interop wrapper builds the out item as `new T(pointer)` and throws MissingMethodException.
        var request = requests.m_itemQueue.Dequeue();
        if (request == null || request.Pointer != pickup.Pointer)
            throw new System.InvalidOperationException("Native pickup retry queue head changed before consumption.");
        var builder = new LG_PickupItemBuilder(pickup.m_assignedNode, pickup.m_function, pickup.m_isWardenObjective,
            pickup.m_wardenObjectiveChainIndex, pickup.m_type, pickup.m_comType, pickup.m_comSize,
            pickup.m_artifactCategory, pickup.m_randomSeed, pickup.m_genericItemId)
        {
            m_consumableData = pickup.m_consumableData,
            m_bigPickupData = pickup.m_bigPickupData
        };
        job.TriggerFunctionBuilder(builder, request, out var marker, false);
        // Native BuildGenericFunctionItems returns false after each consumed request, including its last.
        result = false;
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.PickupMarkerRetry,
                "Native pickup retry retained its typed builder; this is delegation, not a spawn readback.",
                RuntimeLogValue.Of("request", request.Pointer.ToInt64()),
                RuntimeLogValue.Of("phase", job.m_fallbackMode ? "native-floor-retry" : "native-marker-retry"),
                RuntimeLogValue.Of("pickupType", pickup.m_type.ToString()),
                RuntimeLogValue.Of("requestedItem", pickup.m_genericItemId),
                RuntimeLogValue.Of("randomSeed", pickup.m_randomSeed),
                RuntimeLogValue.Of("markerOffset", pickup.m_markerSeedOffset),
                RuntimeLogValue.Of("consumableDistribution", pickup.m_consumableData?.persistentID ?? 0),
                RuntimeLogValue.Of("bigPickupDistribution", pickup.m_bigPickupData?.persistentID ?? 0),
                RuntimeLogValue.Of("node", pickup.m_assignedNode?.Pointer.ToInt64() ?? 0),
                RuntimeLogValue.Of("markerReceipt", marker != null));
        return false;
    }
}

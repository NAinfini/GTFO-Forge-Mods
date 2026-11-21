using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LevelGeneration;
using CullingSystem;
using AIGraph;

namespace ForgeDevelopment.Native;

[HarmonyPatch(typeof(LG_Factory), nameof(LG_Factory.Setup))]
internal static class FactoryStart
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        RuntimeDiagnostics.Safe(CaptureDoorTopologyNative.Clear);
        RuntimeDiagnostics.Safe(RecAcousticObservation.Reset);
        RuntimeDiagnostics.Safe(RuntimeDiagnostics.Begin);
    }
}

[HarmonyPatch]
// patch-targets: observation (DevelopmentHookSafety installs it read-only through ForgeCore.Hooks.Observe, beside each method's owner)
internal static class GenerationJob
{
    // IL2CPP folds constant-return bodies across unrelated classes. Never discover Build hooks by inheritance.
    // This explicit set was checked against the native body map; adding a type requires the same audit.
    internal static IEnumerable<MethodBase> TargetMethods() => new[]
    {
        typeof(LG_BuildUnityGraphJob_Cleanup),
        typeof(LG_BuildUnityGraphJob),
        typeof(LG_AlarmShutdownOnTerminalJob),
        typeof(LG_BuildAirGraphJob),
        typeof(LG_BakeAirGraphObstaclesJob),
        typeof(LG_TerminalCorruptedUplinkLinkerJob),
        typeof(LG_TerminalTimedSequenceLinkerJob),
        typeof(LG_HSU.LG_HSUScannerJob),
        typeof(C_BuildCullerNode),
        typeof(C_NodeInstanceSearch),
        typeof(C_ZoneClusterJob),
        typeof(C_CleanupEmptyCullers),
        typeof(C_NodeGenerateCullers),
        typeof(C_BuildCullerWithCallback),
        typeof(C_BuildCrossingCuller),
        typeof(C_BuildDynamicCuller),
        typeof(C_BuildSpecialRenderableObjectCuller),
        typeof(C_BuildCullerPortal),
        typeof(C_PlugCrossingCuller),
        typeof(C_BuildCap),
        typeof(C_BuildCLights),
        typeof(C_GatherCLightsCulling),
        typeof(C_LateCleanup),
        typeof(C_LateCleanup_Area),
        typeof(C_WaitUntilEverythingIsHidden),
        typeof(C_PortalTrimmerJob),
        typeof(C_TrimmPortals),
        typeof(LG_SetupFloor),
        typeof(LG_SecurityDoor.LG_CheckpointScannerJob),
        typeof(LG_Distribute_DetailedPlacedFunctionPerZone),
        typeof(LG_Distribute_DumbwaitersPerZone),
        typeof(LG_Distribute_FunctionPerZone),
        typeof(LG_Distribute_PickupItemsPerZone),
        typeof(LG_Distribute_ProgressionPuzzles),
        typeof(LG_Distribute_ResourcePacksPerZone),
        typeof(LG_Distribute_TerminalsPerZone),
        typeof(LG_Distribute_WardenObjective),
        typeof(LG_PropagateMainPathJob),
        typeof(LG_BuildAIGraphJob_Prepare),
        typeof(LG_BuildAIGraphJob_Start),
        typeof(LG_BuildAIGraphJob_End),
        typeof(LG_BuildAreaJob),
        typeof(LG_BuildBulkheadDoorControllerLogicJob),
        typeof(LG_SimpleDoorLockJob),
        typeof(LG_BuildChainedPuzzleDoorLockJob),
        typeof(LG_BuildKeyItemLockJob),
        typeof(LG_BuildPowerGeneratorLockJob),
        typeof(LG_BuildFloorJob),
        typeof(LG_BuildGateJob),
        typeof(LG_BuildGeomorphJob),
        typeof(LG_BuildLadderAIGNodeJob),
        typeof(LG_BuildLadderJob),
        typeof(LG_BuildPillarJob),
        typeof(LG_BuildCustomPlugJob),
        typeof(LG_BuildPlugJob),
        typeof(LG_BuildPrefabSpawnersJob),
        typeof(LG_BuildZoneLightsJob),
        typeof(LG_BuildCustomLightHandlersJob),
        typeof(LG_CustomGeomorphBuildJob),
        typeof(LG_CustomGeomorphPostCullingJob),
        typeof(LG_FixColliderJob),
        typeof(LG_DistributionSetup),
        typeof(LG_GenerateDistributionDataJob),
        typeof(LG_GenerateAreaNames),
        typeof(LG_GenerateNavigationInfoJob),
        typeof(LG_HandleFunctionMarkerLeftoversInZone),
        typeof(LG_LateGeomorphBuildJob),
        typeof(LG_LateGeomorphScanJob),
        typeof(LG_LateRuntimeOptimizationJob),
        typeof(LG_LinkAICustomPlugJob),
        typeof(LG_LinkAIGateJob),
        typeof(LG_LinkAIPlugJob),
        typeof(LG_LinkAIVolumeBordersJob),
        typeof(LG_LoadComplexDataSetResourcesJob),
        typeof(LG_MarkerLoopJob),
        typeof(LG_MergeStaticIRFsJob),
        typeof(LG_FinalizeStaticRIFs),
        typeof(LG_MergeStaticMeshes),
        typeof(LG_PopulateFunctionMarkersInZoneJob),
        typeof(LG_ProduceMarkersJob),
        typeof(LG_PropagateNavigationInfoJob),
        typeof(LG_RemoveUnusedResourceContainerJob),
        typeof(LG_SetGateTraversableJob),
        typeof(LG_SetNavInfoOnDoorJob),
        typeof(LG_SetupAreaInstancing),
        typeof(LG_SpawnItemsInCargoCageJob),
        typeof(LG_PlaceStaticEnemyInNode),
        typeof(LG_SpawnStaticEnemiesInZoneJob),
        typeof(LG_SpecialNodeFindSourceJob),
        typeof(LG_SpecialNodeLinkIntoVolumeJob),
        typeof(LG_TerminalPasswordLinkerJob),
        typeof(LG_TerminalUniqueCommandsSetupJob),
        typeof(LG_VisualizeMeshDensity),
        typeof(LG_WorldEventObjectCollectionJob),
        typeof(LG_WorldEventObjectAssignmentJob),
        typeof(LG_WorldEventObjectPreperationJob),
        typeof(LG_SpecificPickupSpawningJob),
        typeof(LG_SpecificChainedPuzzleSpawningJob),
        typeof(LG_SpecificTerminalSpawningJob),
        typeof(LG_LoadPopulationShards),
        typeof(LG_PopulateArea),
        typeof(LG_PopulateZone),
        typeof(LG_ZoneJob_CreateExpandFromData),
        typeof(LG_ZoneJob_PrepZones),
        typeof(LG_CollectZoneLightsJob),
        typeof(LG_BuildStaticLevelJob),
        typeof(LG_CreateCourseGraphJob),
        typeof(LG_BuildNodeVolumes),
        typeof(LG_BuildNodeCluster),
        typeof(LG_ScoreNodeCluster),
    }.Select(t => t.GetMethod("Build", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!);

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(LG_FactoryJob __instance, MethodBase __originalMethod, out RuntimeDiagnostics.JobTrace? __state)
    {
        RuntimeDiagnostics.JobTrace? trace = null;
        RuntimeDiagnostics.Safe(() => trace = RuntimeDiagnostics.StartJob(__instance, __originalMethod));
        __state = trace;
    }
    // A replacement prefix can return true without running native Build. Its result
    // cannot establish that the native gate builder was observed.
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_FactoryJob __instance, bool __result, bool __runOriginal)
        => RuntimeDiagnostics.Safe(() => CaptureDoorTopologyNative.ObserveBuild(__instance, __result, __runOriginal, null));

    // Return the original exception unchanged; diagnostics must never make a failed job look successful.
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(bool __result, Exception? __exception, RuntimeDiagnostics.JobTrace? __state)
    {
        RuntimeDiagnostics.Safe(() => RuntimeDiagnostics.EndJob(__state, __result, __exception));
        return __exception;
    }
}

[HarmonyPatch]
internal static class CullingLifecycle
{
    internal static IEnumerable<MethodBase> TargetMethods() => new[] { typeof(C_CullingCluster), typeof(C_CullBucket), typeof(C_Cullable) }
        .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        .Where(m => m.Name is "OnDestroy" or "CleanupLists" or "AddRenderer" or "RemoveRenderer");
    [HarmonyPrefix]
    private static void Prefix(C_Cullable __instance, MethodBase __originalMethod)
        => RuntimeDiagnostics.Safe(() => RuntimeDiagnostics.Culling(__instance, __originalMethod.Name, "before"));
    [HarmonyFinalizer]
    private static Exception? Finalizer(C_Cullable __instance, MethodBase __originalMethod, Exception? __exception)
    {
        RuntimeDiagnostics.Safe(() => RuntimeDiagnostics.Culling(__instance, __originalMethod.Name, "after", __exception));
        return __exception;
    }
}

// Reads the portal, gate, areas and course nodes a culling portal build is about to dereference (fields only, no GetGate),
// and the exception that leaves it. Build is also in GenerationJob's explicit set; this adds the structural readback.
[HarmonyPatch(typeof(C_BuildCullerPortal), nameof(C_BuildCullerPortal.Build))]
internal static class CullerPortalBuild
{
    [HarmonyPrefix]
    private static void Prefix(C_BuildCullerPortal __instance)
        => RuntimeDiagnostics.Safe(() => RuntimeDiagnostics.CullerPortal(__instance, "before"));
    [HarmonyFinalizer]
    private static Exception? Finalizer(C_BuildCullerPortal __instance, Exception? __exception)
    {
        if (__exception != null) RuntimeDiagnostics.Safe(() => RuntimeDiagnostics.CullerPortal(__instance, "exception", __exception));
        return __exception;
    }
}

// High-frequency rendering paths only report an actual exception, without scanning on every draw.
[HarmonyPatch]
internal static class CullingFailure
{
    internal static IEnumerable<MethodBase> TargetMethods() => typeof(C_CullingCluster).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Where(m => m.Name is "Show" or "Hide" or "HideSafe" or "GetShadowRenderingData");
    [HarmonyFinalizer]
    private static Exception? Finalizer(C_CullingCluster __instance, MethodBase __originalMethod, Exception? __exception)
    {
        if (__exception != null) RuntimeDiagnostics.Safe(() => RuntimeDiagnostics.Culling(__instance, __originalMethod.Name, "exception", __exception));
        return __exception;
    }
}

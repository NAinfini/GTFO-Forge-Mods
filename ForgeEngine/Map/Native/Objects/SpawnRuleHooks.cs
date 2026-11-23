using System;
using AIGraph;
using Enemies;
using GameData;
using HarmonyLib;
using LevelGeneration;

namespace ForgeMap.Native;

// The vanilla big-pickup distribution (contract M16). A zone's random big items are drawn by the overload that takes
// the zone's distribution block; it chooses the item id from the block's weights and calls the one shared builder
// `SetupBigPickupItemWithItemId`, which the task objective path and fixed placements call directly as well. Two
// patches tell those apart: the class below marks the distribution call (entry and exit on the simulation thread,
// the exit also when the original throws), and the shared builder's postfix (`BigPickupSetupHooks`, beside the task
// container selection) reads what it spawned while the mark stands. Neither changes an argument, a result or the
// original's order of work.

[HarmonyPatch(typeof(LG_PickupItem), nameof(LG_PickupItem.SetupAsBigPickupItem),
    new[] { typeof(int), typeof(BigPickupDistributionDataBlock), typeof(bool), typeof(int) })]
internal static class SpawnRuleDistributionCall
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => SpawnRuleSession.InDistribution = true;

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static void Finalizer() => SpawnRuleSession.InDistribution = false;
}

// The marker spawner every function prefab of the level is placed through. The game keeps no "used" flag on a marker, so a
// subscriber of ForgeCore's GameHooks.MarkerSpawned records each marker that really received a prefab of a function
// (`FunctionWasSpawned`), which a random model rule reads to skip it. It changes no argument and no result.
internal static class MarkerOccupancyObserved
{
    internal static void Observed(ForgeCore.MarkerSpawn call)
    {
        if (!call.FunctionWasSpawned || !call.Result || MapHost.Session is not { } session) return;
        var marker = call.Marker;
        session.Run(session.SpawnRules, rules => rules.MarkOccupied(marker), "marker occupancy");
    }
}

// The allocator every enemy of the level is spawned through (members only ever spawn on the host). A postfix reads the
// member, the group it belongs to and the node it spawned in; it changes no argument and no result.
[HarmonyPatch(typeof(EnemyAllocator), nameof(EnemyAllocator.SpawnEnemy))]
internal static class SpawnRuleEnemySpawned
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(EnemyAgent __result, AIG_CourseNode courseNode, EnemyGroup group)
    {
        if (__result == null || group == null || MapHost.Session is not { } session) return;
        try { EnemyGroupPlacementReport.Count(__result, group, courseNode); }
        catch (Exception error) { MapLog.Contained("enemy-group-count", error); }
        session.Run(session.SpawnRules, rules => rules.CollectEnemy(__result, group, courseNode), "spawn rule enemy");
    }
}

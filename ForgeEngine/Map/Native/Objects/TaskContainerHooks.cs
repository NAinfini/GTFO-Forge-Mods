using HarmonyLib;
using LevelGeneration;

namespace ForgeMap.Native;

// These original private entries are reached by vanilla SpawnContent after its native seed and slot draws.
[HarmonyPatch(typeof(LG_ResourceContainer_Storage), nameof(LG_ResourceContainer_Storage.PlaceKeyCard))]
internal static class TaskKeyContainerSelection
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(LG_ResourceContainer_Storage __instance, ResourceContainerSpawnData pack, int randomSeed)
    {
        var session = MapHost.Session;
        if (session?.Placements is not { TaskContainersPending: true }) return true;
        var native = false;
        session.Build(session.Placements, placements => native = placements.RouteTaskPack(__instance, pack, randomSeed, true), "task key container selection");
        return native;
    }
}

[HarmonyPatch(typeof(LG_ResourceContainer_Storage), nameof(LG_ResourceContainer_Storage.PlaceSmallGenericPickup))]
internal static class TaskSmallContainerSelection
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(LG_ResourceContainer_Storage __instance, ResourceContainerSpawnData pack, int randomSeed)
    {
        var session = MapHost.Session;
        if (session?.Placements is not { TaskContainersPending: true }) return true;
        var native = false;
        session.Build(session.Placements, placements => native = placements.RouteTaskPack(__instance, pack, randomSeed, false), "task small container selection");
        return native;
    }
}

// The shared big-pickup builder. The prefix is the task-carry container selection: the original factory and
// SetupAsWardenObjective still run exactly once with the original flags and chain. After the body the spawn-rule read
// (what a random item rule produced, see SpawnRuleHooks.cs) runs first, then the task-carry commit.
[HarmonyPatch(typeof(LG_PickupItem), nameof(LG_PickupItem.SetupBigPickupItemWithItemId))]
internal static class BigPickupSetupHooks
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(LG_PickupItem __instance, uint itemId, bool isWardenObjectiveItem, int objectiveChainIndex,
        out FixedPlacementSession.TaskPickupSlot? __state)
    {
        __state = null;
        var session = MapHost.Session;
        if (session?.Placements is not { TaskContainersPending: true }) return true;
        if (session.Placements.ReplayingTaskCarry(__instance)) return true;
        var native = false; FixedPlacementSession.TaskPickupSlot? selected = null;
        session.Build(session.Placements, placements =>
        {
            if (!placements.DeferTaskCarry(__instance, itemId, isWardenObjectiveItem, objectiveChainIndex))
                native = placements.PrepareTaskCarry(__instance, itemId, objectiveChainIndex, out selected);
        }, "task carry container selection");
        __state = selected;
        return native;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_PickupItem __instance, uint itemId, FixedPlacementSession.TaskPickupSlot? __state)
    {
        SpawnRuleItem(__instance, itemId);
        TaskCarryCommit(__instance, __state);
    }

    private static void SpawnRuleItem(LG_PickupItem item, uint itemId)
    {
        if (MapHost.Session is not { } session) return;
        if (SpawnRuleSession.InDistribution) session.Run(session.SpawnRules, rules => rules.CollectItem(item, itemId), "spawn rule item");
        else session.Run(session.SpawnRules, rules => rules.ReportOutside(item, itemId), "spawn rule outside");
    }

    private static void TaskCarryCommit(LG_PickupItem item, FixedPlacementSession.TaskPickupSlot? slot)
    {
        if (slot == null || MapHost.Session is not { } session) return;
        session.Build(session.Placements, placements => placements.CompleteTaskCarry(item, slot), "task carry container commit");
    }
}

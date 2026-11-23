using HarmonyLib;

namespace ForgeMap.Native;

// The Rundown menu callbacks, one declared hook class per native target. Runtime installs them at package load; each
// answers only once `RundownMenu` holds an admitted pack (the old installation was conditional on an installed
// pack and these are the same bodies), except the progression pair, which also protects pages with no Forge pack.
// Every body is `RundownMenu`'s own reflection-confined code; a hook class only chooses when it runs. Parameter
// names `__n` are the native positions, as before: `ref uint __1` of the selection button changes the value the
// native method captures, because the installed HarmonyX does not propagate edits to its argument snapshot.

/// <summary>Progression arrives independently of package discovery/admission and native page placement.</summary>
[HarmonyPatch(typeof(CellMenu.CM_PageRundown_New), "OnRundownProgressionFileUpdated")]
internal static class RundownPageProgressionReceived
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    // Deferring is only safe while the page-placed callback that replays it exists; without it the native
    // progression runs as it always did.
    private static bool Prefix(object __instance)
    {
        RundownMenu.ApplyUnlockAll();
        return MapHost.Session?.Hooks.Offers(MapHookFeatures.RundownProgressionFeature) == false || RundownMenu.ProgressionReceived(__instance);
    }
}

[HarmonyPatch(typeof(CellMenu.CM_PageRundown_New), "PlaceRundown")]
internal static class RundownPagePlaced
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(object __instance, object __0) => RundownMenu.SelectPlayerValues(__instance, __0);
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(object __instance, object __0)
    {
        RundownMenu.ProgressionPlaced(__instance, __0);
        RundownMenu.ArrangeTiers(__instance, __0);
    }
}

[HarmonyPatch(typeof(CellMenu.CM_PageRundown_New), "Update")]
internal static class RundownPageProgressionUpdate
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(object __instance) => RundownMenu.ProgressionUpdated(__instance);
}

[HarmonyPatch(typeof(CellMenu.CM_PageRundown_New), "Setup")]
internal static class RundownPageSetup
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(object __instance) => RundownMenu.MenuStarting(__instance);
}

[HarmonyPatch(typeof(CellMenu.CM_PageRundown_New), "UpdateRundownSelectionButton")]
internal static class RundownPageSelectionButton
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(object __instance, object __0, ref uint __1) => RundownMenu.BindSelection(__instance, __0, ref __1);
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(object __0, uint __1) => RundownMenu.SelectionTitle(__0, __1);
}

[HarmonyPatch(typeof(CellMenu.CM_PageRundown_New), "UpdateText")]
internal static class RundownPageText
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(object __instance) => RundownMenu.RefreshTitles(__instance);
}

/// <summary>Build 20403457 implements the two achievement methods as closed switches: id to RundownName and the
/// inverse. A Forge Rundown extends that round trip once admitted; every other value still runs the original.</summary>
[HarmonyPatch(typeof(AchievementManager), "GetCurrentRundownName")]
internal static class RundownAchievementName
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(ref RundownName __result) => RundownMenu.SelectAchievementRundownName(ref __result);
}

[HarmonyPatch(typeof(AchievementManager), "GetLocalRundownKey")]
internal static class RundownAchievementKey
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(RundownName __0, ref string __result) => RundownMenu.SelectAchievementRundownKey(__0, ref __result);
}

/// <summary>Build 20403457 `ExpeditionInTierData.GetChecksum` (RVA 0x1980940) hashes `Descriptive.PublicName` first; the host
/// compares that checksum (`SNet_SyncManager.SetLevelChecksum`) before a player may build the level. While it hashes, a level
/// whose name is shown per language carries the primary-language text its package shipped, the same on every machine, and
/// afterwards this machine's language again (<see cref="RundownMenu.ChecksumName"/>). Entry bytes: `mov [rsp+8],rbx` (5) and `push rdi` (1) cover the
/// 6 bytes the detour writes.</summary>
[HarmonyPatch(typeof(GameData.ExpeditionInTierData), "GetChecksum")]
internal static class LevelNameChecksum
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(GameData.ExpeditionInTierData __instance) => RundownMenu.ChecksumName(__instance, true);
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(GameData.ExpeditionInTierData __instance) => RundownMenu.ChecksumName(__instance, false);
}

[HarmonyPatch(typeof(RundownManager), "SetActiveExpeditionData")]
internal static class RundownExpeditionDataSet
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix() => RundownMenu.ExpeditionPlayerValues();
}

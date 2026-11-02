using System;
using HarmonyLib;
using SNetwork;

namespace ForgeCore;

/// <summary>One call of <c>PUI_WardenIntel.ShowSubObjectiveMessage</c> or <c>ShowPrioritySubObjectiveMessage</c> (the same parameters).</summary>
public readonly record struct IntelMessage(PUI_WardenIntel Intel, string LastText, string NewText, bool IsAdditional, float Size, float Duration);

public static partial class GameHooks
{
    // ---- The objective panel and the Warden intel message line.
    public static readonly Bracket<(PUI_GameObjectives Panel, int Id, Il2CppSystem.Func<string> Header, Il2CppSystem.Func<string> Text)> ProgressionObjectiveSet
        = new("GameHooks.ProgressionObjectiveSet", typeof(ProgressionObjectiveSetHook));
    public static readonly Bracket<(PUI_GameObjectives Panel, int Id)> ProgressionObjectiveRemoved
        = new("GameHooks.ProgressionObjectiveRemoved", typeof(ProgressionObjectiveRemovedHook));
    public static readonly Bracket<PUI_GameObjectives> ObjectivesCleaned = new("GameHooks.ObjectivesCleaned", typeof(ObjectivesCleanedHook));
    public static readonly Bracket<IntelMessage> SubObjectiveShown = new("GameHooks.SubObjectiveShown", typeof(SubObjectiveShownHook));
    public static readonly Bracket<IntelMessage> PrioritySubObjectiveShown = new("GameHooks.PrioritySubObjectiveShown", typeof(PrioritySubObjectiveShownHook));

    // ---- The session: the game chose and recalled a replacement host after a migration.
    public static readonly Bracket<SNet_SyncManager> MasterMigrationFound = new("GameHooks.MasterMigrationFound", typeof(MasterMigrationFoundHook));
    /// <summary><c>SNet_SessionHub.AddPlayerToSession</c>: the session now lists a player (the call's value). The game's join events run before it.</summary>
    public static readonly Bracket<SNet_Player> SessionPlayerAdded = new("GameHooks.SessionPlayerAdded", typeof(SessionPlayerAddedHook));
    /// <summary><c>SNet_SessionHub.RemovePlayerFromSession</c>: the session no longer lists a player (the call's value).</summary>
    public static readonly Bracket<SNet_Player> SessionPlayerRemoved = new("GameHooks.SessionPlayerRemoved", typeof(SessionPlayerRemovedHook));
}

[HarmonyPatch(typeof(SNet_SessionHub), "AddPlayerToSession")]
internal static class SessionPlayerAddedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(SNet_Player __0) => GameHooks.SessionPlayerAdded.RunBefore(__0);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(SNet_Player __0, bool __runOriginal) => GameHooks.SessionPlayerAdded.RunAfter(__0, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, SNet_Player __0) { if (__exception != null) GameHooks.SessionPlayerAdded.RunFaulted(__0, __exception); return __exception; }
}

[HarmonyPatch(typeof(SNet_SessionHub), "RemovePlayerFromSession")]
internal static class SessionPlayerRemovedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(SNet_Player __0) => GameHooks.SessionPlayerRemoved.RunBefore(__0);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(SNet_Player __0, bool __runOriginal) => GameHooks.SessionPlayerRemoved.RunAfter(__0, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, SNet_Player __0) { if (__exception != null) GameHooks.SessionPlayerRemoved.RunFaulted(__0, __exception); return __exception; }
}

[HarmonyPatch(typeof(PUI_GameObjectives), nameof(PUI_GameObjectives.SetProgressionObjective))]
internal static class ProgressionObjectiveSetHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(PUI_GameObjectives __instance, int ID, Il2CppSystem.Func<string> getHeaderText, Il2CppSystem.Func<string> getSubObjtext,
        out (PUI_GameObjectives, int, Il2CppSystem.Func<string>, Il2CppSystem.Func<string>) __state)
    { __state = (__instance, ID, getHeaderText, getSubObjtext); GameHooks.ProgressionObjectiveSet.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After((PUI_GameObjectives Panel, int Id, Il2CppSystem.Func<string> Header, Il2CppSystem.Func<string> Text) __state, bool __runOriginal)
        => GameHooks.ProgressionObjectiveSet.RunAfter(__state, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, (PUI_GameObjectives Panel, int Id, Il2CppSystem.Func<string> Header, Il2CppSystem.Func<string> Text) __state)
    { if (__exception != null) GameHooks.ProgressionObjectiveSet.RunFaulted(__state, __exception); return __exception; }
}

[HarmonyPatch(typeof(PUI_GameObjectives), nameof(PUI_GameObjectives.RemoveProgressionObjective))]
internal static class ProgressionObjectiveRemovedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(PUI_GameObjectives __instance, int ID, out (PUI_GameObjectives, int) __state)
    { __state = (__instance, ID); GameHooks.ProgressionObjectiveRemoved.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After((PUI_GameObjectives Panel, int Id) __state, bool __runOriginal) => GameHooks.ProgressionObjectiveRemoved.RunAfter(__state, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, (PUI_GameObjectives Panel, int Id) __state)
    { if (__exception != null) GameHooks.ProgressionObjectiveRemoved.RunFaulted(__state, __exception); return __exception; }
}

[HarmonyPatch(typeof(PUI_GameObjectives), nameof(PUI_GameObjectives.OnLevelCleanup))]
internal static class ObjectivesCleanedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(PUI_GameObjectives __instance) => GameHooks.ObjectivesCleaned.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(PUI_GameObjectives __instance, bool __runOriginal) => GameHooks.ObjectivesCleaned.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, PUI_GameObjectives __instance) { if (__exception != null) GameHooks.ObjectivesCleaned.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(PUI_WardenIntel), nameof(PUI_WardenIntel.ShowSubObjectiveMessage))]
internal static class SubObjectiveShownHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(PUI_WardenIntel __instance, string lastSubObj, string newSubObj, bool isAdditional, float size, float duration, out IntelMessage __state)
    { __state = new IntelMessage(__instance, lastSubObj, newSubObj, isAdditional, size, duration); GameHooks.SubObjectiveShown.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(IntelMessage __state, bool __runOriginal) => GameHooks.SubObjectiveShown.RunAfter(__state, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, IntelMessage __state) { if (__exception != null) GameHooks.SubObjectiveShown.RunFaulted(__state, __exception); return __exception; }
}

[HarmonyPatch(typeof(PUI_WardenIntel), nameof(PUI_WardenIntel.ShowPrioritySubObjectiveMessage))]
internal static class PrioritySubObjectiveShownHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(PUI_WardenIntel __instance, string lastSubObj, string newSubObj, bool isAdditional, float size, float duration, out IntelMessage __state)
    { __state = new IntelMessage(__instance, lastSubObj, newSubObj, isAdditional, size, duration); GameHooks.PrioritySubObjectiveShown.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(IntelMessage __state, bool __runOriginal) => GameHooks.PrioritySubObjectiveShown.RunAfter(__state, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, IntelMessage __state) { if (__exception != null) GameHooks.PrioritySubObjectiveShown.RunFaulted(__state, __exception); return __exception; }
}

[HarmonyPatch(typeof(SNet_SyncManager), nameof(SNet_SyncManager.OnFoundNewMasterDuringMigration))]
internal static class MasterMigrationFoundHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(SNet_SyncManager __instance) => GameHooks.MasterMigrationFound.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(SNet_SyncManager __instance, bool __runOriginal) => GameHooks.MasterMigrationFound.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, SNet_SyncManager __instance) { if (__exception != null) GameHooks.MasterMigrationFound.RunFaulted(__instance, __exception); return __exception; }
}

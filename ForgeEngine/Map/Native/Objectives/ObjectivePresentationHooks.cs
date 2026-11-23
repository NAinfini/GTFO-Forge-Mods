using HarmonyLib;
using LevelGeneration;

namespace ForgeMap.Native;

// Member/signature evidence: catalog/native/native-runtime.json types.PUI_GameObjectives;
// .local/post-v1/objective-ui/signatures/PUI_GameObjectives.txt (matching QA interop).
// Postfixes only invalidate: the Map clock redraws after native collection mutations finish. The entry set, entry removed and
// level cleanup of the panel are ForgeCore's hooks (GameHooks.ProgressionObjectiveSet, ProgressionObjectiveRemoved, ObjectivesCleaned),
// which MapGameHooks subscribes; the patches below are the panel methods only Map patches.
[HarmonyPatch(typeof(PUI_GameObjectives), nameof(PUI_GameObjectives.SetMainObjective))]
internal static class ObjectiveMainTextOverride
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(PUI_GameObjectives __instance, LG_LayerType __0, ref Il2CppSystem.Func<string> __1)
        => MapHost.Session?.ObjectiveText?.BeforeMain(__instance, __0, ref __1);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PUI_GameObjectives __instance) => MapHost.Session?.ObjectiveText?.PanelChanged(__instance);
}
[HarmonyPatch(typeof(PUI_GameObjectives), nameof(PUI_GameObjectives.UpdateText))]
internal static class ObjectivePanelTextRefreshed
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PUI_GameObjectives __instance) => MapHost.Session?.ObjectiveText?.PanelTextRefreshed(__instance);
}
[HarmonyPatch(typeof(PUI_GameObjectives), nameof(PUI_GameObjectives.ClearTexts))]
internal static class ObjectivePanelTextsCleared
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PUI_GameObjectives __instance) => MapHost.Session?.ObjectiveText?.PanelChanged(__instance);
}

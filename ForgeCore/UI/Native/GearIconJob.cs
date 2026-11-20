using HarmonyLib;

namespace ForgeUI;

// The icon renderer loads a job's gear when the job's turn comes; a supplied category's job is loaded by GearIcons instead of the donor gun.
// Any other job's gun is spawned by the game inside this call (GearBuilder.AssembleGearAsync), at the world origin: GearIcons takes it
// from the item spawn while the call runs (GearIcons.Building).
[PatchGroup(PatchGroup.GearIcons)]
[HarmonyPatch(typeof(GearIconRendering), nameof(GearIconRendering.LoadGearForIconJob))]
internal static class GearIconJob
{
    [HarmonyPrefix]
    private static bool Prefix(GearIconRendering.IconRenderJob __0)
    {
        bool supplied;
        try { supplied = GearIcons.Load(__0); }
        catch (System.Exception error)
        {
            Plugin.Error("ui-gear-icon-job", "Gear icon job could not be loaded; the game loads it: " + error);
            supplied = false;
        }
        GearIcons.Building = !supplied;
        return !supplied;
    }

    [HarmonyFinalizer]
    private static System.Exception? Finalizer(System.Exception? __exception)
    {
        GearIcons.Building = false;
        return __exception;
    }
}

// The renderer's LateUpdate steps a loaded job here every frame until it is cleaned up, before any camera draws: the job's gear, which the
// renderer keeps at the world origin on its view layer, is put out of every camera's sight first (GearIcons.Hide). While the gun was being
// built it was kept hidden part by part (GearIcons.PartsStepped).
[PatchGroup(PatchGroup.GearIcons)]
[HarmonyPatch(typeof(GearIconRendering), nameof(GearIconRendering.RenderIconJob))]
internal static class GearIconHiding
{
    [HarmonyPrefix]
    private static void Prefix(GearIconRendering.IconRenderJob __0)
    {
        try { GearIcons.Hide(__0?.loadedGearGO); }
        catch (System.Exception error) { Plugin.Error("ui-gear-icon-hide", "Gear icon gear could not be hidden; it stays visible at the world origin while it is drawn: " + error); }
    }
}

// The renderer frames a job's gear by the bounds of every renderer under it; additions a mod left out of framing are taken out again.
[PatchGroup(PatchGroup.GearIcons)]
[HarmonyPatch(typeof(GearIconRendering), nameof(GearIconRendering.RenderIconPrep))]
internal static class GearIconFraming
{
    [HarmonyPostfix]
    private static void Postfix(GearIconRendering.IconRenderJob __0, ref UnityEngine.Bounds __result)
    {
        try { GearIcons.Frame(__0, ref __result); }
        catch (System.Exception error) { Plugin.Error("ui-gear-icon-framing", "Gear icon framing could not leave additions out; the game's framing stays: " + error); }
    }
}

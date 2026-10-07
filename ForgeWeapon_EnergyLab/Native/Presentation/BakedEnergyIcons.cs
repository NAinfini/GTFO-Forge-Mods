using ForgeWeaponEnergyLabExperimental.Presentation;
using Gear;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

[HarmonyPatch(typeof(GearIconRendering), nameof(GearIconRendering.RegisterIconJob))]
internal static class BakedEnergyIconJob
{
    [HarmonyPrefix]
    private static bool Prefix(GearIDRange __0, Il2CppSystem.Action<IconRenderJobResult> __1, IconRenderSettings __2)
        => !EnergyIcons.Request(__0, __1, __2);
}

[HarmonyPatch(typeof(GearIconRendering), nameof(GearIconRendering.RegisterIconJobs))]
internal static class BakedEnergyIconJobs
{
    [HarmonyPrefix]
    private static bool Prefix(GearIDRange __0, Il2CppReferenceArray<IconRenderJobData> __1)
    {
        if (__0 == null || EnergyGears.ModeForCategory(__0.GetCompID(eGearComponent.Category)) == EnergyMode.Off) return true;
        if (__1 == null)
        {
            Plugin.Error("Energy icon batch is missing: " + new System.ArgumentNullException(nameof(__1)));
            return false;
        }
        foreach (var data in __1)
        {
            if (data == null) Plugin.Error("Energy icon job is missing: " + new System.InvalidOperationException("Null job in the native icon batch."));
            else EnergyIcons.Request(__0, data.callback, data.settings);
        }
        return false;
    }
}

[HarmonyPatch(typeof(GearIconRendering), nameof(GearIconRendering.TryGetGearIconSprite))]
internal static class BakedEnergyIconSprite
{
    [HarmonyPrefix]
    private static bool Prefix(uint __0, ref Sprite __1, ref bool __result)
    {
        if (!EnergyIcons.OwnsChecksum(__0)) return true;
        __result = EnergyIcons.TryGetSprite(__0, out var sprite);
        __1 = sprite;
        return false;
    }
}

[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.OnGearSpawnComplete))]
internal static class BindEnergyInventoryModel
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ItemEquippable __instance)
    {
        // The model bind owns its registration snapshot and rollback, including deferred asset mounts.
        try { WeaponModels.BindWeapon(__instance); }
        catch (System.Exception error) { Plugin.Error("Energy native socket binding failed: " + error); }
    }
}

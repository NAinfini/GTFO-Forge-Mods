using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using GameData;
using Gear;
using HarmonyLib;
using Player;

namespace ForgeWeaponEnergyLabExperimental.Native;

[BepInPlugin(Id, "Forge Weapon Energy Lab experimental", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.5.1")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = EnergyLab.PluginId;
    internal const string Version = EnergyProtocol.Version;
    internal static ManualLogSource Logger = null!;
    private static readonly System.Collections.Generic.HashSet<string> ReportedErrors = new();

    // Every distinct error is logged; only an exact repeat of the same full text is suppressed.
    internal static void Error(string message)
    {
        lock (ReportedErrors)
        {
            if (!ReportedErrors.Add(message)) return;
        }
        Logger.LogError(message);
    }

    public override void Load()
    {
        Logger = Log;
        try { EnergyLab.Initialize(System.IO.Path.Combine(Paths.ConfigPath, "ForgeEnergyLab")); }
        catch (System.Exception error)
        {
            Error($"Energy Lab is disabled; {EnergyConfiguration.OverridesFile} refused: " + error);
            return;
        }
        var harmony = new Harmony(Id);
        try
        {
            harmony.PatchAll(typeof(Plugin).Assembly);
            EnergyGearRegistry.Register();
            EnergyNetwork.Register();
            AddComponent<PrototypeController>();
        }
        catch (System.Exception error)
        {
            Error("Energy Lab initialization failed: " + error);
            harmony.UnpatchSelf();
            throw;
        }
    }

    public override bool Unload() => false;
}

// The game's offline gear list must see the new rows on its first pass even if another loader changes
// GameData initialization order. Install is idempotent for a second GTFO-API callback.
[HarmonyPatch(typeof(GearManager), nameof(GearManager.LoadOfflineGearDatas))]
internal static class RegisterBeforeOfflineGearLoad
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => EnergyGearRegistry.Install();
}

[HarmonyPatch(typeof(GearManager), nameof(GearManager.OnGearLoadingDone))]
internal static class AddEnergyGunsToLoadout
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(GearManager __instance) => EnergyGearRegistry.EnsureLoadout(__instance);
}

// Saved loadouts are parsed from JSON; an energy gun saved by an earlier build takes the current components.
[HarmonyPatch(typeof(GearIDRange), nameof(GearIDRange.TryApplyCompJSON))]
internal static class CurrentSavedEnergyPacket
{
    [HarmonyPostfix]
    private static void Postfix(GearIDRange __instance, bool __result)
    {
        if (!__result) return;
        try { EnergyGearRegistry.UseCurrentPacket(__instance); }
        catch (System.Exception error) { Plugin.Error("Saved energy gun packet update failed: " + error); }
    }
}

// Gear assembly sets the shared base-item ItemFPSSettings; energy guns then take their private copy.
[HarmonyPatch(typeof(GearBuilder), nameof(GearBuilder.AssembleGearAsync))]
internal static class EnergyItemFpsSettings
{
    // Every assembly, including a packet that never passed through JSON, builds the current energy gun.
    [HarmonyPrefix]
    private static void Prefix(GearIDRange gearIDRange)
    {
        try { EnergyGearRegistry.UseCurrentPacket(gearIDRange); }
        catch (System.Exception error) { Plugin.Error("Energy gun packet update failed: " + error); }
    }

    [HarmonyPostfix]
    private static void Postfix(GearIDRange gearIDRange, ItemEquippable __result)
    {
        try { EnergyItemFps.Apply(__result, gearIDRange); }
        catch (System.Exception error) { Plugin.Error("Energy first-person settings failed: " + error); }
    }
}

[HarmonyPatch(typeof(GearPartHolder), nameof(GearPartHolder.OnAllPartsSpawned))]
internal static class MountEnergyWeaponModel
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(GearPartHolder __instance)
    {
        try { Presentation.WeaponModels.Ensure(__instance); }
        catch (System.Exception error) { Plugin.Error("Energy model mount failed: " + error); }
    }
}

// Auto.Update keeps native input, empty-clip and reload handling. Orbs fire only on our release input;
// a busy projectile slot is an execution budget, never a second cadence or ammunition gate.
[HarmonyPatch(typeof(BWA_Auto), "PreFireCheck")]
internal static class NativeEnergyShotReady
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Prefix(BWA_Auto __instance, ref bool __result)
    {
        if (EnergyGearRegistry.DefinitionOf(__instance.m_weapon) is not { } gear) return true;
        if (gear.Mode is not (EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc) &&
            PrototypeController.CanLaunch(__instance.m_weapon, gear)) return true;
        __result = false;
        return false;
    }
}

// This game's LateUpdate inlines UpdateIKGrips and reads both grip targets in its native body.
// Update all first-person presentation outputs before those reads, irrespective of MonoBehaviour ordering.
[HarmonyPatch(typeof(FirstPersonItemHolder), nameof(FirstPersonItemHolder.LateUpdate))]
internal static class HolderPosePatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Prefix(FirstPersonItemHolder __instance)
    {
        try { Presentation.WeaponModels.UpdateFirstPersonPose(__instance); }
        catch (System.Exception error) { Plugin.Error("Energy hand pose update failed: " + error); }
    }

    [HarmonyPostfix]
    private static void Postfix(FirstPersonItemHolder __instance)
    {
        try { Presentation.WeaponModels.TickSight(__instance); }
        catch (System.Exception error) { Plugin.Error("Energy sight update failed: " + error); }
    }
}

// Native reload visibility actions apply to the authored magazine as well as the hidden donor.
[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.SetMagazineVisibile))]
internal static class EnergyMagazineVisibility
{
    [HarmonyPostfix]
    private static void Postfix(BulletWeapon __instance, bool value) => Presentation.WeaponModels.SetMagazineVisible(__instance, value);
}

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.OnWield))]
internal static class BindEnergyWieldModel
{
    [HarmonyPostfix]
    private static void Postfix(BulletWeapon __instance)
    {
        if (!PrototypeController.Owns(__instance)) return;
        try { Presentation.WeaponModels.BindWeapon(__instance); }
        catch (System.Exception error) { Plugin.Error("Energy wield model binding failed: " + error); }
    }
}

[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.TryTriggerReloadAnimationSequence))]
internal static class BindEnergyReloadModel
{
    [HarmonyPrefix]
    private static void Prefix(ItemEquippable __instance)
    {
        if (__instance.GearCategoryData == null || EnergyGears.ModeForCategory(__instance.GearCategoryData.persistentID) == EnergyMode.Off) return;
        if (__instance.IsReloading) return;
        if (__instance.IsFirstPerson)
        {
            try { Presentation.WeaponModels.BindWeapon(__instance); }
            catch (System.Exception error) { Plugin.Error("Energy reload model binding failed: " + error); }
        }
    }
}

using ForgeWeaponEnergyLab.Presentation;
using HarmonyLib;

namespace ForgeWeaponEnergyLab.Native;

// ForgeCore patches the gear-spawn completion once (`GameHooks.GearSpawnCompleted`); the bind comes after every other patch of it.
internal static class BindEnergyInventoryModel
{
    internal static void Subscribe() => ForgeCore.GameHooks.GearSpawnCompleted.After(Spawned);

    private static void Spawned(ItemEquippable item, bool ran)
    {
        EnergyMuzzleFlash.Queue(item);
        // The model bind owns its registration snapshot and rollback, including deferred asset mounts.
        try { WeaponModels.BindWeapon(item); }
        catch (System.Exception error) { Plugin.Error("Energy native socket binding failed: " + error); }
    }
}

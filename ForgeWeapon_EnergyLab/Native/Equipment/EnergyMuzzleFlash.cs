using System.Collections.Generic;
using Gear;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

// An energy gun has no vanilla muzzle flash, in any view and on any call path: the game builds that flash with the gun
// (the first-person one in BulletWeapon.SetupEffects, replaced by the third-person one in BulletWeaponSynced.OnGearSpawnComplete,
// both held in Weapon.m_muzzleFlash) and plays it from Fire. The energy gun is switched off once, after its spawn call has
// finished, so nothing about a shot decides whether it shows. The gun's own light and sprites are the energy effects.
internal static class EnergyMuzzleFlash
{
    private static readonly List<BulletWeapon> Pending = new();

    internal static void Register() => ForgeCore.Frame.OnUpdate(Plugin.Id, Silence);

    // The spawn hook runs inside the gun's spawn call, before the flash is built; the next frame is after it.
    internal static void Queue(ItemEquippable item)
    {
        var weapon = item.TryCast<BulletWeapon>();
        if (weapon != null && EnergyGearRegistry.DefinitionOf(weapon) != null) Pending.Add(weapon);
    }

    private static void Silence()
    {
        if (Pending.Count == 0) return;
        foreach (var weapon in Pending)
        {
            try
            {
                var flash = weapon.m_muzzleFlash;
                if (flash == null) continue;
                flash.m_flashIntensity = 0f;
                flash.m_startColor = flash.m_endColor = Color.clear;
                flash.gameObject.SetActive(false);
            }
            catch (System.Exception error) { Plugin.Error("Energy gun muzzle flash could not be switched off: " + error); }
        }
        Pending.Clear();
    }
}

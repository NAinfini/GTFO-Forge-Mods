using Gear;
using Player;
using UnityEngine;

namespace ForgeOrdnance.Arsenal;

/// <summary>The library muzzle flash of the lent heavy weapons, from the slots <c>heavy.mg.muzzle</c> and <c>heavy.amr.muzzle</c> of
/// <see cref="ArsenalFxSlots"/>. A bullet needs no tracer or impact of ours: the game's own stay. The local pilot's own shot is seen around
/// ForgeCore's one patch of <c>BulletWeapon.Fire</c> (<see cref="LauncherFire"/>); another player's shot, around the game's synced fire body the
/// game runs for a remote player's gun, is drawn for the weapon whose look this package put on it. A slot the table does not list draws nothing.</summary>
internal static class HeavyFx
{
    /// <summary>The local pilot's shot.</summary>
    internal static void AfterFire(BulletWeapon weapon)
    {
        if (HeavyWeapons.ActiveLauncher != null || HeavyWeapons.ActiveHoming != null) return;
        var model = HeavyWeapons.ActiveModel;
        var agent = weapon.Owner;
        if (model == null || agent == null || agent != ForgeCore.Players.Local || weapon.ItemDataBlock?.inventorySlot != InventorySlot.GearSpecial) return;
        var eye = agent.FPSCamera.transform;
        var from = eye.position;
        var aim = eye.forward;
        Draw(model, weapon.MuzzleAlign != null ? ForgeCore.Combat.ShownMuzzle(weapon, from) : from + aim * 0.8f, aim);
    }

    /// <summary>Another player's shot: the gun the game runs for a remote player, aimed along its own muzzle.</summary>
    internal static void AfterSyncedFire(ItemEquippable weapon)
    {
        var muzzle = weapon.MuzzleAlign;
        if (muzzle == null || !HeavyWeapons.TryWornModel(weapon.Pointer, out var model)) return;
        Draw(model, muzzle.position, muzzle.forward);
    }

    private static void Draw(string model, Vector3 bore, Vector3 forward)
    {
        var muzzle = HeavyModels.MuzzleOf(model);
        if (muzzle == HeavyMuzzle.None) return; // drawn elsewhere: the tracking rifle from the host's message (HomingFire), the launcher's flame from the host's accepted shot (Grenades.Launch)
        ArsenalFxSlots.Play(muzzle == HeavyMuzzle.Amr ? ArsenalFxSlots.AmrMuzzle : ArsenalFxSlots.MgMuzzle, bore, forward);
    }
}

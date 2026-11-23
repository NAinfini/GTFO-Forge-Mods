using System;
using Gear;
using GameData;

namespace ForgeWeapon.Native;

/// <summary>The weapon side of the one gun-assembly asset entry (<see cref="ForgeCore.Gear"/> in the Runtime): the equip row and the
/// test Loadout fixture both ask here, and a throw is a Failed reading that names it, never a skipped check.</summary>
internal static class GearAssetPreparation
{
    internal static GearAssets Of(GearIDRange gear)
    {
        try
        {
            var slot = ItemDataBlock.GetBlock(gear.GetCompID(eGearComponent.BaseItem))?.inventorySlot;
            // Melee and held tools do not run BulletWeapon.SetupEffects. In particular, they do not need its
            // tracer pool just because their component data contains an empty effect block. Deployed sentries
            // prepare their own effects through GearEffectAssets.PrepareSentry at the placement boundary.
            if (slot is Player.InventorySlot.GearMelee or Player.InventorySlot.GearClass)
                return new(GearAssetState.Ready, "no-bullet-weapon-effects slot=" + slot);
            // BulletWeapon.SetupEffects indexes even an empty FPS path. ItemThrower's empty effect block is not a gun's
            // "disabled flash" value: reject before native assembly can strand a half-built weapon in the backpack.
            var flashId = gear.GetCompID(eGearComponent.MuzzleFlash);
            var flash = WeaponMuzzleFlashDataBlock.GetBlock(flashId);
            var gun = slot is Player.InventorySlot.GearStandard or Player.InventorySlot.GearSpecial;
            if (gun && flash != null && string.IsNullOrEmpty(flash.muzzleFlashFPS))
                return new(GearAssetState.Failed, "BulletWeapon.SetupEffects: empty muzzleFlashFPS; muzzleFlashId=" + flashId);
            var reading = ForgeCore.Gear.Prepare(gear);
            return new((GearAssetState)(int)reading.Status, reading.Detail);
        }
        catch (Exception error) { return new(GearAssetState.Failed, "prepare-threw: " + error.GetType().Name + ": " + error.Message); }
    }
}

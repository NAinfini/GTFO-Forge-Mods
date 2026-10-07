using Gear;

namespace ForgeWeaponEnergyLabExperimental.Native;

internal static class WeaponAmmo
{
    internal static int Available(BulletWeapon weapon) => Weapon.InfiniteAmmo ? int.MaxValue : weapon.GetCurrentClip();
}

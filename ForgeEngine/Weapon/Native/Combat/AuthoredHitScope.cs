namespace ForgeWeapon.Native;

/// <summary>True while an authored hitscan submits its own bullet to the game's hit entry. A definition
/// measurement covers the weapon's native shot, so it reads this to leave that extra bullet out of its sample.</summary>
internal static class AuthoredHitScope
{
    internal static bool Active { get; set; }
}

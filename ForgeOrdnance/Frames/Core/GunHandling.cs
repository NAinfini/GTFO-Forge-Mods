namespace ForgeOrdnance.Frames;

/// <summary>What the suit does to guns, as two separate things. Handling (the suit steadies whatever gun the pilot holds while piloting:
/// the suit gun and the pilot's own primary and special alike): recoil is <see cref="Recoil"/> of the gun's own and the reload takes
/// <see cref="Reload"/> of its own time; leaving the suit gives every gun its own values back at once. Numbers (only the suit's own gun):
/// <see cref="Damage"/> and <see cref="FireRate"/>; the pilot's own guns keep the game's damage, fire rate and magazine.</summary>
internal static class GunHandling
{
    /// <summary>Recoil share of any gun held while piloting: the model's <see cref="WeaponSpec.RecoilMultiplier"/>, times the siege's
    /// <see cref="SiegeSpec.RecoilMultiplier"/> while planted.</summary>
    public static float Recoil(WeaponSpec weapon, SiegeSpec? siege, bool sieged)
        => weapon.RecoilMultiplier * (sieged && siege != null ? siege.RecoilMultiplier : 1f);

    /// <summary>Reload-time share of any gun held while piloting: the model's <see cref="WeaponSpec.ReloadMultiplier"/>, times the siege's
    /// <see cref="SiegeSpec.ReloadMultiplier"/> while planted.</summary>
    public static float Reload(WeaponSpec weapon, SiegeSpec? siege, bool sieged)
        => weapon.ReloadMultiplier * (sieged && siege != null ? siege.ReloadMultiplier : 1f);

    /// <summary>Damage factor: the model's <see cref="WeaponSpec.DamageMultiplier"/> (times the siege's while planted) for the suit gun, 1 for any other gun.</summary>
    public static float Damage(bool suitGun, WeaponSpec weapon, SiegeSpec? siege, bool sieged)
        => suitGun ? weapon.DamageMultiplier * (sieged && siege != null ? siege.DamageMultiplier : 1f) : 1f;

    /// <summary>Fire-rate factor: the siege's <see cref="SiegeSpec.FireRateMultiplier"/> for the suit gun while planted, 1 otherwise.</summary>
    public static float FireRate(bool suitGun, SiegeSpec? siege, bool sieged)
        => suitGun && sieged && siege != null ? siege.FireRateMultiplier : 1f;
}

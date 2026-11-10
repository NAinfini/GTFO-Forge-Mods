using UnityEngine;

namespace ForgeVisual;

/// <summary>
/// The heavy grenade launcher's explosion, shared by every package that fires one: ForgeOrdnance_Arsenal's launcher and
/// ForgeWeapon_EnergyLab's blast gun draw this one effect instead of each shipping its own. The prefab, its scale and its light are data
/// in <c>forge-visual-fx.bundle</c> and <c>fx-slots.forge</c> beside this library (built from the effect library's table like the other
/// packages' effect bundles); the slot is <c>heavy.gl.blast</c>. A clean clone has the table but not the media: the library logs once and
/// draws nothing, as every effect slot does.
/// </summary>
public static class HeavyBlast
{
    /// <summary>The slot the effect library's table gives the launcher's explosion.</summary>
    internal const string Slot = "heavy.gl.blast";
    private static FxSlots _slots = FxSlots.Empty;
    private static FxHandle? _handle;

    internal static void Load()
    {
        _slots = FxSlots.Load(Plugin.Id, "ForgeShared", "forge-visual-fx.bundle", "fx-slots.forge");
        _handle = _slots.Find(Slot);
    }

    /// <summary>Draws the launcher's explosion at <paramref name="at"/>, with the light and size the slot table gives it.</summary>
    public static void Play(Vector3 at)
    {
        if (_handle != null) Fx.Play(_handle, at, Vector3.up);
    }
}

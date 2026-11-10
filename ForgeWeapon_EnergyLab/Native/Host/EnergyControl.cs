using System;
using System.Globalization;
using System.Linq;
using Gear;
using Player;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>Energy Lab's by-name readback, for a driver with no keyboard (ForgeDevelopment's <c>drive_cli.py call static:ForgeWeaponEnergyLab.Native.EnergyControl &lt;Method&gt;</c>)
/// and for other mods: what the loaded definitions and the rundown's <c>Settings.forge</c> made of the guns. Read-only; main thread only.</summary>
public static class EnergyControl
{
    /// <summary>One line per loaded gun: id, slot, mode, model (key and bundle file name), the numbers a rundown or definition can set, and its Look.</summary>
    public static string Guns()
    {
        var lines = EnergyGears.All.Select(g =>
        {
            var d = g.Definition;
            var t = d.Tuning;
            return d.Id + " slot=" + d.Slot + " mode=" + d.Mode + " model=" + d.Model.Key + "@" + System.IO.Path.GetFileName(d.Model.BundlePath)
                   + " magazine=" + t.Magazine + " damage=" + t.Damage.ToString("0.###", CultureInfo.InvariantCulture)
                   + " interval=" + t.Interval.ToString("0.###", CultureInfo.InvariantCulture)
                   + " ammoPerRound=" + t.AmmoPerRound.ToString("0.###", CultureInfo.InvariantCulture)
                   + " look=" + (d.Look == null ? "-" : EnergyLookNames.Canonical(d.Look));
        });
        return string.Join("\n", lines);
    }

    /// <summary>The energy guns the picker offers now (Main and Special slots, after the rundown's Allowed rule), by definition id.</summary>
    public static string Offered()
    {
        var known = ForgeCore.Level.TryActiveExpedition(out _, out _, out _);
        var text = "level=" + (known ? ForgeCore.Level.ActiveKey : "none") + " allowed=" + EnergyLab.Settings.Canonical();
        foreach (var slot in new[] { InventorySlot.GearStandard, InventorySlot.GearSpecial })
        {
            var offers = GearManager.GetAllGearForSlot(slot);
            var ids = offers == null ? Array.Empty<string>()
                : offers.Select(g => EnergyGears.ForCategory(g.GetCompID(eGearComponent.Category))?.Definition.Id).Where(id => id != null).Select(id => id!).ToArray();
            text += "\n" + slot + ": " + (ids.Length == 0 ? "-" : string.Join(",", ids));
        }
        return text;
    }
}

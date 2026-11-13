using ForgeOrdnance.Arsenal;

namespace ForgeOrdnance;

/// <summary>Arsenal's methods of the mod's driver entry (<c>drive_cli.py call static:ForgeOrdnance.OrdnanceControl Racks</c>).</summary>
public static partial class OrdnanceControl
{
    /// <summary>The heavy weapon racks this peer knows, one line each, with the ammunition numbers they hold.</summary>
    public static string Racks() => HeavyWeapons.Describe();
}

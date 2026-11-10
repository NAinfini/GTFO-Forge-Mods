namespace ForgeOrdnanceArsenal;

/// <summary>Arsenal's by-name readback for a driver (<c>drive_cli.py call static:ForgeOrdnanceArsenal.ArsenalControl Racks</c>). Main thread only.</summary>
public static class ArsenalControl
{
    /// <summary>The heavy weapon racks this peer knows, one line each, with the ammunition numbers they hold.</summary>
    public static string Racks() => HeavyWeapons.Describe();
}

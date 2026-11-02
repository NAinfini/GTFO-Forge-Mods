using System;
using ForgeUI;

namespace ForgeOrdnanceArsenal;

/// <summary>This package's key actions, rebound by the player in the game's settings (Controls) through ForgeUI.</summary>
internal static class ArsenalKeys
{
    /// <summary>Gives a lent heavy weapon back at once.</summary>
    internal static KeyAction Drop = null!;

    internal static void Register()
        => Drop = UIKit.Mod(Plugin.Id).AddKeyAction("DropHeavyWeapon", "keys.drop", "keys.group", "G", KeyMode.Press, "on-foot")
                  ?? throw new InvalidOperationException("ForgeUI refused the key action DropHeavyWeapon; see its error.");
}

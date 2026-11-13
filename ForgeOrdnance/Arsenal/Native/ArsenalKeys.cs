using ForgeUI;

namespace ForgeOrdnance.Arsenal;

/// <summary>Arsenal's key actions, rebound by the player in the game's settings (Controls) through ForgeUI.</summary>
internal static class ArsenalKeys
{
    /// <summary>Gives a lent heavy weapon back at once.</summary>
    internal static KeyAction Drop = null!;

    internal static void Register() => Drop = OrdnanceKeys.Add("DropHeavyWeapon", "keys.drop", "keys.group.arsenal", "G", "on-foot");
}

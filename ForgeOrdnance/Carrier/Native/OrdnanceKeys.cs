using System;
using ForgeUI;

namespace ForgeOrdnance;

/// <summary>This package's key actions, rebound by the player in the game's settings (Controls) through ForgeUI. The code keys add
/// to the player's movement keys, which also enter a code.</summary>
internal static class OrdnanceKeys
{
    /// <summary>Held to enter a code; pressed again while holding the ball puts it away. Its trigger is Press or Hold (the code is entered while the action lasts).</summary>
    internal static KeyAction Summon = null!;
    internal static KeyAction Up = null!, Down = null!, Left = null!, Right = null!;

    internal static void Register()
    {
        Summon = Add("Summon", "keys.summon", Group, "Mouse3", "", KeyModes.Press | KeyModes.Hold);
        Up = Add("CodeUp", "keys.codeUp", Group, "UpArrow", EntryContext);
        Down = Add("CodeDown", "keys.codeDown", Group, "DownArrow", EntryContext);
        Left = Add("CodeLeft", "keys.codeLeft", Group, "LeftArrow", EntryContext);
        Right = Add("CodeRight", "keys.codeRight", Group, "RightArrow", EntryContext);
    }

    private const string Group = "keys.group";
    /// <summary>The code keys are read only while a code is entered.</summary>
    private const string EntryContext = "ordnance-code";

    /// <summary>One key action of the mod, pressed by default, under the heading <paramref name="group"/> of the game's key settings
    /// (each part has its own); a refusal by ForgeUI stops the part's load.</summary>
    internal static KeyAction Add(string name, string nameKey, string group, string key, string context, KeyModes allowed = KeyModes.All)
        => UIKit.Mod(Plugin.Id).AddKeyAction(name, nameKey, group, key, KeyMode.Press, context, allowed)
           ?? throw new InvalidOperationException("ForgeUI refused the key action " + name + "; see its error.");
}

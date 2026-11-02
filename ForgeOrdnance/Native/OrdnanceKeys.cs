using System;
using ForgeUI;

namespace ForgeOrdnance;

/// <summary>This package's key actions, rebound by the player in the game's settings (Controls) through ForgeUI. The code keys add
/// to the player's movement keys, which also enter a code.</summary>
internal static class OrdnanceKeys
{
    /// <summary>Held to enter a code; pressed again while holding the ball puts it away.</summary>
    internal static KeyAction Summon = null!;
    internal static KeyAction Up = null!, Down = null!, Left = null!, Right = null!;

    internal static void Register()
    {
        var ui = UIKit.Mod(Plugin.Id);
        Summon = Add(ui, "Summon", "keys.summon", "Mouse3", KeyMode.Hold, "");
        Up = Add(ui, "CodeUp", "keys.codeUp", "UpArrow", KeyMode.Press, EntryContext);
        Down = Add(ui, "CodeDown", "keys.codeDown", "DownArrow", KeyMode.Press, EntryContext);
        Left = Add(ui, "CodeLeft", "keys.codeLeft", "LeftArrow", KeyMode.Press, EntryContext);
        Right = Add(ui, "CodeRight", "keys.codeRight", "RightArrow", KeyMode.Press, EntryContext);
    }

    /// <summary>The code keys are read only while a code is entered.</summary>
    private const string EntryContext = "ordnance-code";

    private static KeyAction Add(UIKitMod ui, string name, string nameKey, string key, KeyMode mode, string context)
        => ui.AddKeyAction(name, nameKey, "keys.group", key, mode, context)
           ?? throw new InvalidOperationException("ForgeUI refused the key action " + name + "; see its error.");
}

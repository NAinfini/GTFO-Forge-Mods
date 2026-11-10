using System;
using ForgeUI;

namespace ForgeOrdnanceFrames;

/// <summary>This package's key actions, rebound by the player in the game's settings (Controls) through ForgeUI. They are live only
/// while piloting, so they may share keys with actions used on foot. Boarding uses the game's own Use key.</summary>
internal static class FrameKeys
{
    /// <summary>Held for 0.8 seconds to climb out (timed by the pilot while the action lasts, so its trigger is Press or Hold).</summary>
    internal static KeyAction Exit = null!;
    /// <summary>Sirius's charge, Thunderbird's siege mode, and for a suit that carries heavy objects (Taishan) putting the carried object on the back or taking it off (Nightowl's scan is passive).</summary>
    internal static KeyAction Special = null!;
    /// <summary>Thunderbird's rocket salvo.</summary>
    internal static KeyAction Rockets = null!;

    private const string Piloting = "exosuit";

    internal static void Register()
    {
        var ui = UIKit.Mod(Plugin.Id);
        Exit = Add(ui, "Exit", "keys.exit", "G", KeyMode.Press, KeyModes.Press | KeyModes.Hold);
        Special = Add(ui, "Special", "keys.special", "V", KeyMode.Press);
        Rockets = Add(ui, "Rockets", "keys.rockets", "B", KeyMode.Press);
    }

    private static KeyAction Add(UIKitMod ui, string name, string nameKey, string key, KeyMode mode, KeyModes allowed = KeyModes.All)
        => ui.AddKeyAction(name, nameKey, "keys.group", key, mode, Piloting, allowed)
           ?? throw new InvalidOperationException("ForgeUI refused the key action " + name + "; see its error.");
}

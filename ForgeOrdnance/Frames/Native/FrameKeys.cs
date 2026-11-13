using ForgeUI;

namespace ForgeOrdnance.Frames;

/// <summary>The suits' key actions, rebound by the player in the game's settings (Controls) through ForgeUI. They are live only
/// while piloting, so they may share keys with actions used on foot. Boarding uses the game's own Use key.</summary>
internal static class FrameKeys
{
    /// <summary>Held for 0.8 seconds to climb out (timed by the pilot while the action lasts, so its trigger is Press or Hold).</summary>
    internal static KeyAction Exit = null!;
    /// <summary>Sirius's charge, Thunderbird's siege mode, and for a suit that carries heavy objects (Taishan) putting the carried object on the back or taking it off (Nightowl's scan is passive).</summary>
    internal static KeyAction Special = null!;
    /// <summary>Thunderbird's rocket salvo.</summary>
    internal static KeyAction Rockets = null!;

    private const string Piloting = "exosuit", Group = "keys.group.frames";

    internal static void Register()
    {
        Exit = OrdnanceKeys.Add("Exit", "keys.exit", Group, "G", Piloting, KeyModes.Press | KeyModes.Hold);
        Special = OrdnanceKeys.Add("Special", "keys.special", Group, "V", Piloting);
        Rockets = OrdnanceKeys.Add("Rockets", "keys.rockets", Group, "B", Piloting);
    }
}

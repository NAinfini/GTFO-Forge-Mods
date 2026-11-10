using UnityEngine;

namespace ForgeReplay;

/// <summary>The two colours of the replay's own plates, sampled from a 1280x720 screenshot of the game's settings page: the window's
/// sprite colours are tints of its textures, not the colour seen. Every other colour is the game's, from ForgeUI's <c>UIKitColors</c>.</summary>
internal static class ReplayPlate
{
    /// <summary>The settings window's background and its outline.</summary>
    internal static readonly Color Back = Hex(0x0A0F12), Line = Hex(0x85A5B7);

    private static Color Hex(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
}

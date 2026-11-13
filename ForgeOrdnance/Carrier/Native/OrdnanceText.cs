using System;
using ForgeUI;

namespace ForgeOrdnance;

/// <summary>Every text the player sees, from <c>lang/&lt;language&gt;.json</c> beside the plugin through ForgeUI, in the language
/// ForgeUI has chosen (its <c>Language</c> setting, or the game's language).</summary>
internal static class OrdnanceText
{
    private static readonly UIKitMod Ui = UIKit.Mod(Plugin.Id);

    /// <summary>The text of <paramref name="key"/>; a cached string.</summary>
    internal static string T(string key) => Ui.Text(key);

    /// <summary>The text of <paramref name="key"/> with its placeholders filled.</summary>
    internal static string T(string key, params object?[] args) => Ui.Text(key, args);

    /// <summary>What the player is told about <paramref name="reason"/>.</summary>
    internal static string Reason(Rejection reason) =>
        T(Enum.IsDefined(typeof(Rejection), reason) && reason != Rejection.None ? "reject." + reason : "reject.Other");
}

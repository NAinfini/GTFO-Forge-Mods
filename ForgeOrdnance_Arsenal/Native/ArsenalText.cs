using System;
using System.Collections.Generic;
using System.IO;
using ForgeOrdnance;
using ForgeUI;

namespace ForgeOrdnanceArsenal;

/// <summary>Every text the player sees, through ForgeUI in the language it has chosen: Arsenal's own words from <c>lang/</c> beside
/// the plugin, and the call-in names from the definitions' <c>ForgeOrdnance/lang/</c> (the same files ForgeOrdnance shows in its
/// picker and HUD).</summary>
internal static class ArsenalText
{
    private static readonly UIKitMod Ui = UIKit.Mod(Plugin.Id);
    private static UITexts? _definitions;

    private static UITexts Definitions => _definitions ??=
        UIKit.Texts(Path.Combine(Path.GetDirectoryName(typeof(ArsenalText).Assembly.Location) ?? ".", "ForgeOrdnance", "lang"));

    /// <summary>Changes whenever the language changes; a text kept with an older value is stale.</summary>
    internal static int Version => UITexts.Version;

    internal static string T(string key) => Ui.Text(key);

    internal static string T(string key, params object?[] args) => Ui.Text(key, args);

    /// <summary>The call-in <paramref name="id"/>'s name in the current language from Arsenal's own definitions' language files, or the id
    /// itself when they do not name it (a call-in from another pack; <see cref="OrdnanceApi.DisplayName"/> names those).</summary>
    internal static string CallIn(string id) => Definitions.TryText(id + ".Name", out var name) ? name : id;

    private static readonly HashSet<string> NamedMarkers = new(StringComparer.Ordinal);

    /// <summary>A heavy weapon is shown under its call-in's name in this player's language on this player's own screens. The gear packet's
    /// public name stays the marker, because the packet replicates and every peer recognizes the lent weapon by it. Runs once when the
    /// definitions are ready, for every heavy weapon definition, so a weapon already in a backpack after a save reload has its name too.
    /// A model is named after the first definition (ordered by Id) that uses it, the same on every peer whether or not it was ever summoned
    /// here; the name is Arsenal's own text, else <see cref="OrdnanceApi.DisplayName"/> (a call-in from another pack).</summary>
    internal static void NameHeavyWeapons()
    {
        foreach (var definition in OrdnanceApi.Definitions)
        {
            if (definition.Parameters is not HeavyParameters heavy) continue;
            var marker = HeavyModels.Marker(heavy.Spec.ModelHash);
            if (!NamedMarkers.Add(marker)) continue;
            var key = definition.Id + ".Name";
            if (Definitions.TryText(key, out _)) UIKit.GearName(Definitions, marker, key);
            else UIKit.GearName(marker, () => OrdnanceApi.DisplayName(definition));
        }
    }
}

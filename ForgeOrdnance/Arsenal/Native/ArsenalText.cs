using System;
using System.Collections.Generic;
using ForgeUI;

namespace ForgeOrdnance.Arsenal;

/// <summary>The call-in names Arsenal shows, as the picker and HUD show them (<see cref="OrdnanceContent.Name"/>); its other words
/// come from <see cref="OrdnanceText"/>.</summary>
internal static class ArsenalText
{
    /// <summary>The call-in <paramref name="id"/>'s name in the current language, or the id itself when no loaded definition has it
    /// (a late join's record carries the id cut to fit).</summary>
    internal static string CallIn(string id) => OrdnanceContent.Set.TryGetById(id, out var definition) ? OrdnanceContent.Name(definition) : id;

    private static readonly HashSet<string> NamedMarkers = new(StringComparer.Ordinal);

    /// <summary>A heavy weapon is shown under its call-in's name in this player's language on this player's own screens. The gear packet's
    /// public name stays the marker, because the packet replicates and every peer recognizes the lent weapon by it. Runs once when the
    /// definitions are ready, for every heavy weapon definition, so a weapon already in a backpack after a save reload has its name too.
    /// A model is named after the first definition (ordered by Id) that uses it, the same on every peer whether or not it was ever summoned
    /// here.</summary>
    internal static void NameHeavyWeapons()
    {
        foreach (var definition in OrdnanceContent.Set.Definitions)
        {
            if (definition.Parameters is not HeavyParameters heavy) continue;
            var marker = HeavyModels.Marker(heavy.Spec.ModelHash);
            if (NamedMarkers.Add(marker)) UIKit.GearName(marker, () => OrdnanceContent.Name(definition));
        }
    }
}

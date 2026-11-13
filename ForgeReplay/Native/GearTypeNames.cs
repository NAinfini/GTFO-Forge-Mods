using System;
using System.Collections.Generic;
using GameData;
using Gear;

namespace ForgeReplay;

/// <summary>What a recorded gear is, in the words of the game's own data and language (Burst Rifle, SMG, Sentry Gun, Hammer), in place of the brand name its
/// loadout line carries (Mechatronic SGB3, Santonian HDH), which tells a player nothing about the weapon. A gun is named by its archetype (the fire mode's block of its
/// category), a tool by its category, a melee weapon by its category (then its melee archetype); the text follows the game's language. The brand name stands in when
/// the game's data has no name for the gear (a gear this install does not know). Main thread only.</summary>
internal static class GearTypeNames
{
    private static readonly Dictionary<string, string> Known = new();

    /// <param name="loadout">The recorded loadout: one gear JSON per line (standard, special, melee, tool).</param>
    /// <param name="line">The loadout's line.</param>
    internal static string Name(string loadout, int line)
    {
        var brand = ReplayFeed.GearName(loadout, line);
        if (brand.Length == 0) return "";
        var json = loadout.Split('\n')[line];
        // A card asks when its row changes; a gear's data does not.
        if (Known.TryGetValue(json, out var known)) return known;
        var name = "";
        try { name = Read(json, line); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A gear's type name could not be read: " + error.Message); }
        return Known[json] = name.Length > 0 ? name : brand;
    }

    private static string Read(string json, int line)
    {
        var gear = new GearIDRange(json);
        var category = GearCategoryDataBlock.GetBlock(gear.GetCompID(eGearComponent.Category));
        if (category == null) return "";
        if (line == 2)
        {
            var melee = Text(category.PublicName);
            return melee.Length > 0 ? melee : MeleeArchetypeDataBlock.GetBlock(category.MeleeArchetype)?.PublicName ?? "";
        }
        if (line == 3) return Text(category.PublicName);
        var type = ArchetypeDataBlock.GetBlock(ForgeCore.Gear.ArchetypeId(gear)) is { } block ? Text(block.PublicName) : "";
        return type.Length > 0 ? type : Text(category.PublicName);
    }


    // The game's text for a name, in the language the game is set to; an author's inline text where it has no text ID. "is null": the type's == throws on a null.
    /// <summary>A localized text in the game's language, or its author text when it has no id; empty for none.</summary>
    internal static string Text(Localization.LocalizedText? text)
        => text is null ? "" : (text.Id != 0 ? Localization.Text.Get(text.Id) : text.UntranslatedText) ?? "";
}

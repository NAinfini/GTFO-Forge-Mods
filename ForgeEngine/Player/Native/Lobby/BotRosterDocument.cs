using ForgeCore;
using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ForgePlayer.Native;

/// <summary>One level as the roster document spells it: `&lt;rundown block id&gt;:&lt;tier A-E&gt;:&lt;zero-based index&gt;`, every
/// number decimal without leading zeros. Both sides of the comparison come from the game: the rundown block the process
/// loaded and the active expedition's own tier and index. A text that is not this shape names no level.</summary>
internal readonly record struct LevelAddress(uint RundownId, char Tier, int Index)
{
    internal static LevelAddress? TryParse(string? text)
    {
        if (text == null) return null;
        var parts = text.Split(':');
        if (parts.Length != 3 || parts[1].Length != 1 || parts[1][0] is < 'A' or > 'E') return null;
        if (!RuntimeJson.TryDecimal(parts[0], out var rundownId) || rundownId > uint.MaxValue) return null;
        if (!RuntimeJson.TryDecimal(parts[2], out var index) || index > int.MaxValue) return null;
        return new LevelAddress((uint)rundownId, parts[1][0], (int)index);
    }

    /// <summary>The game's own tier value (A is 1) and index; a tier outside A-E or a negative index names no level.</summary>
    internal static LevelAddress? FromNative(uint rundownId, int tier, int index)
        => tier is >= 1 and <= 5 && index >= 0 ? new LevelAddress(rundownId, (char)('A' + tier - 1), index) : null;

    public override string ToString()
        => RundownId.ToString(CultureInfo.InvariantCulture) + ":" + Tier + ":" + Index.ToString(CultureInfo.InvariantCulture);
}

internal sealed record BotSlotSetting(int Slot, bool Bot, int? Character, IReadOnlyDictionary<string, uint> Loadout);

internal sealed record BotSlotLevel(LevelAddress Level, IReadOnlyList<BotSlotSetting> Slots);

/// <summary>The saved whole-level bot roster of a Rundown package (`forge/bot-slots.forge`). It carries only stable vanilla
/// identities; the native half resolves each PlayerOfflineGear id from the live loadout offered by the game before it writes
/// any slot permission.</summary>
internal static class BotRosterDocument
{
    internal const string FileName = "bot-slots.forge";
    private static readonly string[] GearSlots = { "GearStandard", "GearSpecial", "GearClass", "GearMelee" };

    internal static IReadOnlyList<BotSlotLevel> Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!Object(root, "schemaVersion", "levels")
            || !FormatVersion.Matches(root)
            || !root.TryGetProperty("levels", out var levels) || levels.ValueKind != JsonValueKind.Array)
            throw new FormatException("Invalid bot slot document.");

        var accepted = new List<BotSlotLevel>();
        var seenLevels = new HashSet<LevelAddress>();
        foreach (var levelRow in levels.EnumerateArray())
        {
            if (!Object(levelRow, "level", "slots")
                || !levelRow.TryGetProperty("level", out var levelNode) || levelNode.ValueKind != JsonValueKind.String
                || LevelAddress.TryParse(levelNode.GetString()) is not { } level || !seenLevels.Add(level)
                || !levelRow.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Array
                || slots.GetArrayLength() > 4)
                throw new FormatException("Invalid bot slot level.");

            var roster = new List<BotSlotSetting>();
            var seenSlots = new HashSet<int>();
            foreach (var slotRow in slots.EnumerateArray())
            {
                if (!Object(slotRow, "slot", "bot", "character", "loadout")
                    || !RuntimeJson.TryInteger(slotRow, "slot", 0, 3, out var slotValue)
                    || !seenSlots.Add((int)slotValue)
                    || !slotRow.TryGetProperty("bot", out var botNode)
                    || botNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new FormatException("Invalid bot slot row.");
                bool bot = botNode.GetBoolean();
                int? character = null;
                if (slotRow.TryGetProperty("character", out _))
                {
                    if (!bot || !RuntimeJson.TryInteger(slotRow, "character", 0, 3, out var characterValue))
                        throw new FormatException("Invalid bot character.");
                    character = (int)characterValue;
                }

                var loadout = new Dictionary<string, uint>(StringComparer.Ordinal);
                if (slotRow.TryGetProperty("loadout", out var loadoutNode))
                {
                    if (!bot || !Object(loadoutNode, GearSlots)) throw new FormatException("Invalid bot loadout.");
                    foreach (var field in loadoutNode.EnumerateObject())
                    {
                        if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetUInt32(out var id) || id == 0)
                            throw new FormatException("Invalid PlayerOfflineGear id in bot loadout.");
                        loadout.Add(field.Name, id);
                    }
                }
                roster.Add(new((int)slotValue, bot, character, new ReadOnlyDictionary<string, uint>(loadout)));
            }
            accepted.Add(new(level, Array.AsReadOnly(roster.OrderBy(row => row.Slot).ToArray())));
        }
        return Array.AsReadOnly(accepted.ToArray());
    }

    /// <summary>An object whose every property is one of the declared fields, with no field repeated: a mistyped field is refused
    /// instead of being read as its own default.</summary>
    private static bool Object(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (Array.IndexOf(fields, property.Name) < 0 || !seen.Add(property.Name)) return false;
        return true;
    }
}

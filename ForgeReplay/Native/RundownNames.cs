using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using ForgeCore;

namespace ForgeReplay;

/// <summary>What the list calls a recording's rundown and level, never by number. The name, in order: the one written into the recording; the title of the
/// rundown block this game has loaded for that ID; the game's own rundowns by ID; the name another recording in the folder wrote for that ID (<see cref="Learn"/>);
/// else "unknown rundown".</summary>
internal static class RundownNames
{
    private static readonly Dictionary<uint, string> Recorded = new();
    private static readonly HashSet<uint> Failed = new();
    // The game's own rundowns by ID (their block titles in the game's data), for a modded game whose blocks replaced them.
    private static readonly Dictionary<uint, string> Official = new() { [32] = "R1 DEVIATION", [33] = "R2 INFECTION", [34] = "R3 THE VESSEL", [37] = "R4 CONTACT", [38] = "R5 REBIRTH", [41] = "R6 DESTINATION", [31] = "R7 RISE", [35] = "R8 DUALITY" };
    private static readonly Regex GameTitle = new(@"RUNDOWN\s*#(\d+)(?:\.\d+)?\s*TITLE:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Spaces = new(@"\s+");

    /// <summary>The rundown's title from the block this game has loaded, as the game shows it (author text as written); null with no block or no title.</summary>
    internal static string? Loaded(uint id)
    {
        try
        {
            // The field and not the Title property, which gave nothing when read in the running game. "is null": the type's == throws on a null.
            var story = GameData.RundownDataBlock.GetBlock(id)?.StorytellingData?._Title_k__BackingField;
            if (story is null) return null;
            var title = Spaces.Replace(Analysis.Untagged(GearTypeNames.Text(story)), " ").Trim();
            if (title.Length == 0) return null;
            // The game's own titles read "RUNDOWN #8.0 TITLE: DUALITY" on two lines; a list says R8 DUALITY. An author's title stays as written.
            var game = GameTitle.Match(title);
            return game.Success ? "R" + game.Groups[1].Value + " " + game.Groups[2].Value.Trim() : title;
        }
        catch (Exception error)
        {
            if (Failed.Add(id)) Plugin.Logger.Error("Could not read the title of rundown " + id + ": " + error);
            return null;
        }
    }

    /// <summary>Remembers the names the recordings wrote, so an older recording of the same rundown can use them.</summary>
    internal static void Learn(IEnumerable<ReplayEntry> entries)
    {
        foreach (var e in entries) if (e.Header is { RundownName.Length: > 0 } h) Recorded[h.RundownId] = h.RundownName;
    }

    private static LevelId? Level(ReplayEntry e)
        => e.Header is { } h ? new LevelId(h.RundownId, h.Tier, h.Index) : LevelId.TryParse(e.Level, out var parsed) ? parsed : null;

    /// <summary>The rundown's name, or null when nothing here can tell.</summary>
    internal static string? Name(ReplayEntry e)
    {
        if (e.Header is { RundownName.Length: > 0 } h) return h.RundownName;
        if (Level(e) is not { } level) return null;
        return Loaded(level.RundownId) ?? (Official.TryGetValue(level.RundownId, out var official) ? official : Recorded.TryGetValue(level.RundownId, out var name) ? name : null);
    }

    /// <summary>The level as players call it: A1.</summary>
    internal static string? Code(ReplayEntry e)
        => Level(e) is { IsValid: true } level ? (char)('A' + level.Tier - 1) + (level.Index + 1).ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>What to say when this game could not start the recording's level; null when it can.</summary>
    internal static string? Refusal(ReplayEntry e)
    {
        if (Level(e) is not { } level) return ReplayList.Say("missing.unknown");
        var name = Name(e);
        return SoloDrop.Check(level) switch
        {
            SoloDropRefusal.None => null,
            SoloDropRefusal.LevelMissing => name == null ? ReplayList.Say("missing.unknown") : ReplayList.Say("missing.level", name, Code(e) ?? ""),
            _ => name == null ? ReplayList.Say("missing.unknown") : ReplayList.Say("missing.rundown", name),
        };
    }
}

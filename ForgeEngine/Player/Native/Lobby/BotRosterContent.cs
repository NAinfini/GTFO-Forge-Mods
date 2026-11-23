using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgePlayer.Native;

/// <summary>Reads a per-level document the installed Rundown packages ship (<c>plugins/&lt;package&gt;/forge/&lt;file&gt;</c>). A document
/// belongs to the package's own Rundown: every level it names must be an authored tier and index of that Rundown, so a package never
/// changes a vanilla level or another package's. A document that cannot be read or breaks a rule is refused alone, with the reason
/// reported; two documents naming the same level are both refused, so no directory order picks a winner. The rest of the package, and
/// every other document, is untouched.</summary>
internal static class LevelDocuments
{
    /// <summary>The accepted rows among the documents ForgeEngine's one content discovery hands this package (I-RESOLVE). The root is a
    /// parameter rather than <c>Paths</c> so a test can point the same entry at a fixture directory. <paramref name="refused"/> receives
    /// one sentence per refusal.</summary>
    internal static IReadOnlyList<TRow> Read<TRow>(string bepInExRoot, string fileName, PublishedTextCodes codes,
        Func<string, IReadOnlyList<TRow>> parse, Func<TRow, LevelAddress> levelOf, Action<string> refused)
    {
        ArgumentNullException.ThrowIfNull(refused);
        if (string.IsNullOrEmpty(bepInExRoot)) return Array.Empty<TRow>();
        var documents = ContentPackages.Read(bepInExRoot, ContentPackages.EngineReader, "forge", fileName, codes, refused);
        if (documents.Count == 0) return Array.Empty<TRow>();

        var packs = ContentPackages.ReadRundowns(bepInExRoot, ContentPackages.EngineReader, diagnostic => refused(diagnostic.Code + " " + diagnostic.Entry + ": " + diagnostic.Detail));
        var parsed = new List<(PublishedResource Document, IReadOnlyList<TRow> Rows)>();
        foreach (var document in documents)
        {
            if (!PublishedResources.TryRead(document, PublishedResources.MaximumFileBytes, codes, refused, out var text)) continue;
            try
            {
                var rows = parse(text!);
                string owner = document.Relative.Substring(0, document.Relative.Length - "/forge/".Length - fileName.Length);
                var pack = packs.FirstOrDefault(row => row.Path == owner + "/forge/rundown.forge");
                if (pack == null) { refused(document.Relative + ": the package has no readable Rundown."); continue; }
                var owned = pack.AuthoredLevels().Select(level => new LevelAddress(pack.RundownId, level.Tier, level.Index)).ToHashSet();
                var foreign = rows.Where(row => !owned.Contains(levelOf(row))).Select(levelOf).ToArray();
                if (foreign.Length != 0)
                { refused(document.Relative + ": level " + foreign[0] + " is not an authored tier and index of this package's Rundown."); continue; }
                parsed.Add((document, rows));
            }
            catch (Exception error) when (error is FormatException or JsonException)
            { refused(document.Relative + ": " + error.Message); }
        }

        var claimed = parsed.SelectMany(row => row.Rows.Select(levelOf)).GroupBy(level => level)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var accepted = new List<TRow>();
        foreach (var (document, rows) in parsed)
        {
            var clash = rows.Select(levelOf).Where(claimed.Contains).ToArray();
            if (clash.Length != 0) refused(document.Relative + ": level " + clash[0] + " is named by more than one document.");
            else accepted.AddRange(rows);
        }
        return accepted;
    }
}

/// <summary>The bot rosters the installed Rundown packages ship (<c>forge/bot-slots.forge</c>).</summary>
internal static class BotRosterContent
{
    internal const string RefusedCode = "player.bot-roster-refused";

    private static readonly PublishedTextCodes Codes = new("player-bot-roster-path", "player-bot-roster-unreadable",
        "player-bot-roster-file-size", "player-bot-roster-json", "player-bot-roster-utf8");

    internal static IReadOnlyList<BotSlotLevel> Read(string bepInExRoot, Action<string> refused)
        => LevelDocuments.Read(bepInExRoot, BotRosterDocument.FileName, Codes, BotRosterDocument.Parse, row => row.Level, refused);
}

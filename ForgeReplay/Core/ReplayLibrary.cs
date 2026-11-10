using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ForgeCore;

namespace ForgeReplay;

/// <summary>How the level ended, written in the footer chunk.</summary>
public enum Outcome : byte
{
    /// <summary>No footer: the file was cut off before the level ended.</summary>
    Unknown = 0,
    /// <summary>The expedition was completed.</summary>
    Success = 1,
    /// <summary>The expedition failed.</summary>
    Failed = 2,
    /// <summary>The level was left without the game saying success or failure.</summary>
    Left = 3,
}

/// <summary>One recording on disk, as the list shows it. <see cref="Summary"/> is the footer of a finished recording (null for an unfinished one). <see cref="Refusal"/> is why it cannot be played (null when it can): the file
/// cannot be read (no header); <see cref="OtherFormat"/> is true when no header could be read because the file is of another
/// recording format (rather than damaged). An unfinished file (crash, quit) is still listed, <see cref="Complete"/> false, and plays up to its last block.</summary>
internal sealed record ReplayEntry(string Path, bool Starred, DateTime Start, string Level, uint DurationMs, Outcome Outcome, bool Complete, ReplayHeader? Header, string? Refusal, RunSummary? Summary = null, bool OtherFormat = false);

/// <summary>The recordings in the replay folder (<c>replay/</c> and <c>replay/starred/</c>): the list, the keep-20 rule and the disk limit. A recording
/// whose header can be read plays here whatever game build, data or mods it was made with: what cannot be restored is skipped while it plays.</summary>
internal static class ReplayLibrary
{
    internal const int Keep = 20;
    internal const string StarredFolder = "starred";

    /// <summary>The recent non-starred recordings (at most <see cref="Keep"/>) and every starred one, newest first.</summary>
    internal static List<ReplayEntry> List(string folder)
    {
        var all = Scan(folder, starred: false).Concat(Scan(System.IO.Path.Combine(folder, StarredFolder), starred: true))
            .OrderByDescending(e => e.Start).ToList();
        var recent = 0;
        return all.Where(e => e.Starred || ++recent <= Keep).ToList();
    }

    /// <summary>Deletes the oldest non-starred recordings beyond <see cref="Keep"/>, and then, while the folder holds more than
    /// <paramref name="limitBytes"/> (0: no limit), the oldest non-starred ones left, whichever rule is stricter. Starred ones are never
    /// deleted and count towards the size; the newest recording is never deleted. Returns how many went.</summary>
    internal static int Prune(string folder, long limitBytes = 0)
    {
        if (!Directory.Exists(folder)) return 0;
        var files = Directory.GetFiles(folder, "*.frpl").OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal).ToList();
        var total = TotalBytes(folder);
        var removed = 0;
        for (var i = files.Count - 1; i >= 1; i--)
        {
            if (i < Keep && (limitBytes <= 0 || total <= limitBytes)) break;
            total -= new FileInfo(files[i]).Length;
            Delete(files[i]);
            removed++;
        }
        return removed;
    }

    /// <summary>The bytes all recordings take, starred ones included.</summary>
    internal static long TotalBytes(string folder)
    {
        long total = 0;
        foreach (var where in new[] { folder, System.IO.Path.Combine(folder, StarredFolder) })
            if (Directory.Exists(where)) foreach (var file in Directory.GetFiles(where, "*.frpl")) total += new FileInfo(file).Length;
        return total;
    }

    /// <summary>Deletes one recording (starred or not), its exported statistics with it, and forgets it.</summary>
    internal static void Delete(string path)
    {
        File.Delete(path);
        var stats = StatsExport.PathOf(path);
        if (File.Exists(stats)) File.Delete(stats);
        Known.Remove(path);
    }

    // Each file as last read, kept while its length and write time stay the same: the list is built again at every click, and reading
    // every header each time made a click stall.
    private static readonly Dictionary<string, (long Length, DateTime Written, ReplayEntry Entry)> Known = new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<ReplayEntry> Scan(string folder, bool starred)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var path in Directory.GetFiles(folder, "*.frpl")) yield return One(path, starred);
    }

    /// <summary>The recording at <paramref name="path"/> as the list shows it (read again only when the file changed).</summary>
    internal static ReplayEntry One(string path, bool starred)
    {
        var info = new FileInfo(path);
        if (Known.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc) return known.Entry;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var split = name.IndexOf('_');
        var level = split < 0 ? name : name.Substring(split + 1);
        DateTime.TryParseExact(split < 0 ? name : name.Substring(0, split), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start);
        var entry = Read(path, starred, start, level);
        Known[path] = (info.Length, info.LastWriteTimeUtc, entry);
        return entry;
    }

    private static ReplayEntry Read(string path, bool starred, DateTime start, string level)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ChunkReader reader;
            try { reader = ChunkLog.Open(stream, ReplayHeader.FormatVersion); }
            catch (ChunkLogFormatException error) { return new ReplayEntry(path, starred, start, level, 0, Outcome.Unknown, false, null, "The recording format is not supported: " + error.Message, OtherFormat: true); }
            ReplayHeader? header = null; var outcome = Outcome.Unknown; uint duration = 0; RunSummary? summary = null;
            foreach (var chunk in reader.Chunks)
            {
                if (chunk.Type == ChunkTypes.Header && header == null) header = ReplayHeader.Decode(reader.Read(chunk));
                else if (chunk.Type == DataChunks.Footer) { summary = RunSummary.Decode(reader.Read(chunk)); outcome = summary.Outcome; }
                else duration = Math.Max(duration, chunk.EndMs);
            }
            if (header == null) return new ReplayEntry(path, starred, start, level, duration, outcome, reader.IsComplete, null, "The recording has no header.");
            return new ReplayEntry(path, starred, start, level, duration, outcome, reader.IsComplete, header, null, summary);
        }
        catch (Exception error) when (error is IOException or FormatException or OverflowException or InvalidDataException or ChunkLogFormatException)
        {
            return new ReplayEntry(path, starred, start, level, 0, Outcome.Unknown, false, null, "The recording cannot be read: " + error.Message);
        }
    }
}

internal static class ChunkTypes
{
    internal const ushort Header = 0;
}

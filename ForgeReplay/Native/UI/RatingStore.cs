using System;
using System.IO;
using System.Linq;

namespace ForgeReplay;

/// <summary>The member rating's files and state (PLAN 6): <c>rating.forge</c> beside the plugin (the fixed anchors and grade cuts) and the cache of
/// every recording's member numbers in the replay folder (<see cref="RatingCache"/>), from which the list rates a recording without reading it. The
/// list's background read (<see cref="ReplayList"/>) feeds the cache; nothing here reads a recording.</summary>
internal static class RatingStore
{
    private const string CacheFile = "member-ratings.json";
    private static RatingTable? _table;
    private static bool _tableRead;
    private static RatingCache? _cache;
    private static bool _dirty;

    /// <summary>The table of <c>rating.forge</c>; null (with the reason in the log, once) when the file is missing or wrong: nothing is then rated.</summary>
    internal static RatingTable? Table
    {
        get
        {
            if (_tableRead) return _table;
            _tableRead = true;
            var path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "rating.forge");
            try { _table = RatingTable.Parse(File.ReadAllText(path)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            { Plugin.Logger.Error("rating.forge (" + path + ") cannot be used, members are not rated: " + error.Message); }
            return _table;
        }
    }

    private static string CachePath => Path.Combine(Plugin.ReplayFolder, CacheFile);

    private static RatingCache Cache
    {
        get
        {
            if (_cache != null) return _cache;
            try { _cache = File.Exists(CachePath) ? RatingCache.Read(File.ReadAllText(CachePath)) : new RatingCache(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Plugin.Logger.Error("The member rating cache cannot be read, it starts again: " + error.Message); _cache = new RatingCache(); }
            return _cache;
        }
    }

    private static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        return info.Length + "-" + info.LastWriteTimeUtc.Ticks;
    }

    /// <summary>True when the recording at <paramref name="path"/> is in the cache as the file is now (nothing to read).</summary>
    internal static bool Has(string path) => _skipped.Contains(path) || Cache.Has(Path.GetFileName(path), Fingerprint(path));

    // Recordings whose read failed: not asked for again until the game restarts.
    private static readonly System.Collections.Generic.HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);

    internal static void Skip(string path) => _skipped.Add(path);

    /// <summary>Adds the members of a recording that was read through to the cache (written to disk by <see cref="Flush"/>).</summary>
    internal static void Record(string path, ReplayHeader header, Analysis analysis)
    {
        if (Table is null) return;
        var duration = analysis.Source?.DurationMs ?? 0;
        var health = ReplayScreen.FullHealth();
        var members = Enumerable.Range(0, 4).Where(slot => ReplayFeed.EverHeld(slot, header.Players, analysis.Presence)).Select(slot => (slot, MemberValues.Read(analysis, slot, duration, health)));
        Cache.Put(Path.GetFileName(path), Fingerprint(path), duration, members);
        _dirty = true;
    }

    /// <summary>Writes the cache if something was added since the last write.</summary>
    internal static void Flush()
    {
        if (!_dirty || _cache == null) return;
        _dirty = false;
        try { ForgeCore.Store.WriteText(CachePath, _cache.Write()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Plugin.Logger.Error("The member rating cache could not be written: " + error.Message); }
    }

    /// <summary>The rating of each slot of the recording at <paramref name="path"/> from the cache (null for a slot nobody held or with no number, or when <c>rating.forge</c> cannot be used, the recording is not read yet or it is shorter than the table's minimum): what the list shows.</summary>
    internal static MemberRating?[]? OfRecording(string path)
    {
        if (Table is not { } table) return null;
        var name = Path.GetFileName(path);
        if (!Cache.Recordings.TryGetValue(name, out var entry) || entry.DurationMs < (long)table.MinSeconds * 1000) return null;
        var result = new MemberRating?[4];
        for (var slot = 0; slot < 4; slot++) result[slot] = Cache.MemberOf(name, slot) is { } values ? MemberRating.Rate(table, values) : null;
        return result;
    }

    /// <summary>The rating of the member in <paramref name="slot"/> of a recording read through; null when <c>rating.forge</c> cannot be used or the recording is shorter than its minimum.</summary>
    internal static MemberRating? Rate(Analysis analysis, int slot)
    {
        if (Table is not { } table) return null;
        var duration = analysis.Source?.DurationMs ?? 0;
        if (duration < (long)table.MinSeconds * 1000) return null;
        return MemberRating.Rate(table, MemberValues.Read(analysis, slot, duration, ReplayScreen.FullHealth()));
    }
}

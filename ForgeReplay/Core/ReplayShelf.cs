using System;
using System.Collections.Generic;
using System.IO;

namespace ForgeReplay;

/// <summary>The plugins that differ between the recording's header and this game, by GUID, for the list's mod table (information only: a
/// recording plays whatever is installed): installed here only, recorded with only, and installed in another version (GUID and both
/// versions). A DLL rebuilt at the same version is not counted: it says nothing a player can act on.</summary>
internal sealed record ModDifference(IReadOnlyList<string> OnlyHere, IReadOnlyList<string> OnlyRecorded, IReadOnlyList<(string Guid, string Recorded, string Here)> OtherVersion)
{
    internal static ModDifference Of(IReadOnlyList<PluginEntry> recorded, IReadOnlyList<PluginEntry> here)
    {
        var mine = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in here) mine[p.Guid] = p.Version;
        var theirs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in recorded) theirs[p.Guid] = p.Version;
        var onlyHere = new List<string>(); var onlyRecorded = new List<string>(); var other = new List<(string, string, string)>();
        foreach (var (guid, version) in theirs)
            if (!mine.TryGetValue(guid, out var v)) onlyRecorded.Add(guid);
            else if (v != version) other.Add((guid, version, v));
        foreach (var guid in mine.Keys) if (!theirs.ContainsKey(guid)) onlyHere.Add(guid);
        onlyHere.Sort(StringComparer.OrdinalIgnoreCase); onlyRecorded.Sort(StringComparer.OrdinalIgnoreCase); other.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Item1, b.Item1));
        return new ModDifference(onlyHere, onlyRecorded, other);
    }
}

/// <summary>The content packages whose files differ between the recording and this game: the ones the recording had that are not installed
/// here, and the ones installed with other files. Empty for a recording that holds no fingerprints (nothing is known then, not that all match).</summary>
internal sealed record ContentDifference(IReadOnlyList<string> Missing, IReadOnlyList<string> Changed)
{
    internal bool Any => Missing.Count + Changed.Count > 0;

    internal static ContentDifference Of(IReadOnlyList<ContentEntry> recorded, IReadOnlyList<ContentEntry> here)
    {
        var mine = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in here) mine[c.Package] = c.Hash;
        var missing = new List<string>(); var changed = new List<string>();
        foreach (var c in recorded)
            if (!mine.TryGetValue(c.Package, out var hash)) missing.Add(c.Package);
            else if (hash != c.Hash) changed.Add(c.Package);
        missing.Sort(StringComparer.OrdinalIgnoreCase); changed.Sort(StringComparer.OrdinalIgnoreCase);
        return new ContentDifference(missing, changed);
    }
}

/// <summary>The list's action on files: starring (moving a recording into <c>replay/starred/</c> and back).</summary>
internal static class ReplayShelf
{
    /// <summary>Moves the recording (and its exported statistics) into or out of the starred folder; returns the new path. A recording still being written
    /// (<paramref name="recording"/>) is never moved and null is returned.</summary>
    internal static string? Star(ReplayEntry entry, bool starred, string folder, string? recording)
    {
        if (entry.Starred == starred) return entry.Path;
        if (recording != null && string.Equals(Path.GetFullPath(recording), Path.GetFullPath(entry.Path), StringComparison.OrdinalIgnoreCase)) return null;
        var target = starred ? Path.Combine(folder, ReplayLibrary.StarredFolder) : folder;
        Directory.CreateDirectory(target);
        var path = Path.Combine(target, Path.GetFileName(entry.Path));
        File.Move(entry.Path, path);
        // The exported statistics go with it (a later delete looks only beside the recording); when they cannot, the recording goes back.
        var stats = StatsExport.PathOf(entry.Path);
        if (File.Exists(stats))
            try { File.Move(stats, StatsExport.PathOf(path), true); }
            catch { File.Move(path, entry.Path); throw; }
        return path;
    }
}

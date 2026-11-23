using System;
using System.Collections.Generic;

namespace ForgeMap;

/// <summary>Authored rows of one install, found by id. One level runs at a time, so an original and its remake in
/// another Rundown may author the same id: an id names the row of the level the world is in, and with no level to
/// read it names a row only when exactly one row carries it. Two rows of one level and id are refused by the caller.</summary>
internal sealed class LevelIdIndex<T> where T : class
{
    private readonly Dictionary<string, List<T>> _rows = new(StringComparer.Ordinal);
    private readonly Func<T, string> _id;
    private readonly Func<T, MapLevelReference> _level;

    internal LevelIdIndex(Func<T, string> id, Func<T, MapLevelReference> level) { _id = id; _level = level; }

    /// <summary>The level the running world is in; null while it cannot be read.</summary>
    internal Func<MapLevelReference?>? CurrentLevel { get; set; }

    internal void Clear() => _rows.Clear();

    /// <summary>False when a row of the same level already carries the id; the row is then not added.</summary>
    internal bool Add(T row)
    {
        var id = _id(row);
        if (!_rows.TryGetValue(id, out var held)) _rows.Add(id, held = new List<T>(1));
        foreach (var other in held) if (_level(other) == _level(row)) return false;
        held.Add(row);
        return true;
    }

    internal bool Holds(T row)
    {
        if (!_rows.TryGetValue(_id(row), out var held)) return false;
        foreach (var other in held) if (ReferenceEquals(other, row)) return true;
        return false;
    }

    internal T? Find(string? id)
    {
        if (id == null || !_rows.TryGetValue(id, out var held)) return null;
        if (held.Count == 1) return held[0];
        if (CurrentLevel?.Invoke() is not { } level) return null;
        foreach (var row in held) if (_level(row) == level) return row;
        return null;
    }
}

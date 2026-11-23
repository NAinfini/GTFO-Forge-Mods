using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

/// <summary>The replicated record of `forge.action.map.door_alarm`: door address → resource id of the scan the door now
/// carries, for the doors whose alarm was replaced and only those. It is the whole content of the native state key
/// <see cref="StateKey"/>, so the host's record, a client's copy, a checkpoint and a migration image are one document.
/// Game-independent: the native half (`DoorAlarmSwaps`) applies what changed.</summary>
internal sealed class DoorAlarmRecord
{
    internal const string StateKey = "forge.map.door-alarms";
    private readonly SortedDictionary<string, string> _doors = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, string> Doors => _doors;

    internal bool Contains(string door) => _doors.ContainsKey(door);
    internal void Set(string door, string scan) => _doors[door] = scan;
    internal void Remove(string door) => _doors.Remove(door);
    internal void Clear() => _doors.Clear();

    internal JsonElement Capture() => JsonSerializer.SerializeToElement(new { doors = _doors });

    /// <summary>A received document, checked whole before anything is applied: one `doors` object of non-empty strings.</summary>
    internal static SortedDictionary<string, string> Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 1
            || !value.TryGetProperty("doors", out var doors) || doors.ValueKind != JsonValueKind.Object)
            throw new FormatException("door-alarm-state");
        var next = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in doors.EnumerateObject())
        {
            if (row.Name.Length == 0 || row.Value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(row.Value.GetString()) || next.ContainsKey(row.Name))
                throw new FormatException("door-alarm-state");
            next.Add(row.Name, row.Value.GetString()!);
        }
        return next;
    }

    /// <summary>Takes <paramref name="next"/> as the record and returns the doors this record named that it no longer
    /// names: those go back to their own alarm.</summary>
    internal string[] Replace(SortedDictionary<string, string> next)
    {
        var dropped = _doors.Keys.Where(door => !next.ContainsKey(door)).ToArray();
        _doors.Clear();
        foreach (var pair in next) _doors.Add(pair.Key, pair.Value);
        return dropped;
    }
}

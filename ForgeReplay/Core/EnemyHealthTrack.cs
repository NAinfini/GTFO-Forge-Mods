using System;
using System.Collections.Generic;

namespace ForgeReplay;

/// <summary>The health an enemy had at a moment, from the health the host synced (EnemyHealth events: the health after each loss). An enemy number is used again by the next enemy in its
/// slot, so each spawn starts its own run of readings. Built while the analysis reads the file and queried by the screen for the enemy it watches.</summary>
internal sealed class EnemyHealthTrack
{
    private sealed class Run { internal uint SpawnMs; internal readonly List<(uint TimeMs, float After)> Readings = new(); }

    private readonly Dictionary<int, List<Run>> _runs = new();

    /// <summary>An enemy came into the level at <paramref name="timeMs"/>: its readings start here.</summary>
    internal void Spawned(int entity, uint timeMs)
    {
        if (!_runs.TryGetValue(entity, out var list)) _runs[entity] = list = new();
        list.Add(new Run { SpawnMs = timeMs });
    }

    /// <summary>The health an enemy was left with by a loss at <paramref name="timeMs"/>.</summary>
    internal void Add(int entity, uint timeMs, float after)
    {
        if (!_runs.TryGetValue(entity, out var list) || list.Count == 0) return;
        var readings = list[^1].Readings;
        if (readings.Count == 0 || readings[^1].TimeMs <= timeMs) readings.Add((timeMs, after));
    }

    /// <summary>What the enemy had left at <paramref name="timeMs"/>: the health after the last loss up to then; false when no loss of the spawn that was alive then was synced
    /// (it may be untouched or the host synced nothing: the caller does not know which).</summary>
    internal bool At(int entity, uint timeMs, out float remaining)
    {
        remaining = 0f;
        if (!_runs.TryGetValue(entity, out var list)) return false;
        Run? run = null;
        foreach (var candidate in list) if (candidate.SpawnMs <= timeMs) run = candidate; else break;
        if (run == null || run.Readings.Count == 0) return false;
        var low = 0; var high = run.Readings.Count - 1; var found = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (run.Readings[mid].TimeMs <= timeMs) { found = mid; low = mid + 1; } else high = mid - 1;
        }
        if (found < 0) return false;
        remaining = run.Readings[found].After;
        return true;
    }
}

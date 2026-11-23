using System;
using System.Collections.Generic;

namespace ForgeMap;

/// <summary>
/// The markers the vanilla spawn points used up in the current world (contract M16). The game keeps no "used" flag on a
/// marker, so the postfix on the spawn call records each marker that really received a function prefab, and a random
/// model rule skips those when it picks its spots. Rows belong to one world: the first mark of a new world drops every
/// row of the previous one, and a query for another world answers false.
/// </summary>
internal sealed class MarkerOccupancy
{
    private readonly HashSet<long> _used = new();
    private long _epoch = long.MinValue;

    public void Mark(long epoch, long marker)
    {
        lock (_used)
        {
            if (epoch != _epoch) { _used.Clear(); _epoch = epoch; }
            _used.Add(marker);
        }
    }

    public bool IsUsed(long epoch, long marker)
    {
        lock (_used) return epoch == _epoch && _used.Contains(marker);
    }

    public int Count(long epoch)
    {
        lock (_used) return epoch == _epoch ? _used.Count : 0;
    }

    public void Clear()
    {
        lock (_used) { _used.Clear(); _epoch = long.MinValue; }
    }
}

/// <summary>
/// Which of a random model rule's free spots receive an instance. Every machine that runs the same level reaches the
/// same candidates in the same order, so the same K-of-N pick the behaviour ledger uses
/// (<see cref="SpawnRuleRegistry.Choose"/>) gives the same spots everywhere, from the level seed and the rule id alone.
/// </summary>
internal static class ModelRulePlacement
{
    /// <summary>The ascending indices of the spots that receive an instance: `count` of `candidates`, or all of them
    /// when there are fewer.</summary>
    public static int[] Pick(uint seed, string ruleId, int candidates, int count)
    {
        count = Math.Min(count, candidates);
        if (count <= 0) return Array.Empty<int>();
        var chosen = SpawnRuleRegistry.Choose(seed, ruleId + "/spot", candidates, count);
        var picked = new int[count];
        for (int i = 0, next = 0; i < chosen.Length; i++)
            if (chosen[i]) picked[next++] = i;
        return picked;
    }
}

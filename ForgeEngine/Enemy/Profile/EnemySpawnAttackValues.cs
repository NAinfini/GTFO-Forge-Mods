using System;
using System.Collections.Generic;

namespace ForgeEnemy.Profile;

/// <summary>One author-selected native attack slot. Group/population are already resolved pack IDs;
/// the ability and wave indices refer to the enemy's actual native lists, never a prefab-name guess.</summary>
internal sealed class EnemySpawnAttackValues
{
    internal int AbilityIndex { get; init; } = -1;
    internal string? SourceName { get; init; }
    internal uint? Group { get; init; }
    internal uint? Population { get; init; }
    internal uint? WaveSettings { get; init; }
    internal uint? WavePopulation { get; init; }
    internal int? WaveIndex { get; init; }
    internal int? Count { get; init; }
    internal float? Delay { get; init; }
    internal float? Interval { get; init; }

    internal bool ChangesNothing => Group == null && Count == null && Delay == null && Interval == null;

    internal static string? Problem(IReadOnlyList<EnemySpawnAttackValues> rows)
    {
        var seen = new HashSet<(int, int)>();
        foreach (var row in rows)
        {
            string name = "Spawning.Attacks[" + row.AbilityIndex + "]";
            if (row.AbilityIndex < 0) return name + " needs an ability index";
            if (!seen.Add((row.AbilityIndex, row.WaveIndex ?? 0))) return name + " repeats the same attack/wave";
            if (row.Group == 0 || row.Population == 0 || row.WaveSettings == 0 || row.WavePopulation == 0) return name + " has a zero group/population identity";
            if (row.WaveSettings > ushort.MaxValue || row.WavePopulation > ushort.MaxValue) return name + " exceeds the native UInt16 wave packet identity";
            if ((row.WaveSettings == null) != (row.WavePopulation == null)) return name + " needs both native wave identities";
            if (row.Population != null && row.Group == null) return name + " has a population without a group";
            if (row.WaveIndex is < 0 || row.Count is < 0) return name + " needs a nonnegative wave/count";
            if (row.Delay is float delay && !float.IsFinite(delay)
                || row.Interval is float interval && !float.IsFinite(interval)) return name + " needs finite timing";

        }
        return null;
    }
}

internal static partial class EnemyFieldResolver
{
    private static void ReadChildSpawning(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.ChildSpawning;
        foreach (var entry in set.Entries(block, "Attacks"))
        {
            int? Integer(string name) => entry.TryGetProperty(name, out var value) && value.TryGetInt32(out int number) ? number : null;
            uint? Identity(string name) => entry.TryGetProperty(name, out var value) && value.TryGetUInt32(out uint number) ? number : null;
            float? Number(string name) => entry.TryGetProperty(name, out var value) && value.TryGetSingle(out float number) ? number : null;
            resolution.SpawnAttacks.Add(new EnemySpawnAttackValues
            {
                AbilityIndex = Integer("AbilityIndex") ?? -1,
                SourceName = entry.TryGetProperty("SourceName", out var sourceName) ? sourceName.GetString() : null,
                Group = Identity("Group"),
                Population = Identity("Population"),
                WaveIndex = Integer("WaveIndex"),
                WaveSettings = Identity("WaveSettings"),
                WavePopulation = Identity("WavePopulation"),
                Count = Integer("Count"),
                Delay = Number("Delay"),
                Interval = Number("Interval")
            });
            foreach (var path in EnemyFieldSchema.Paths)
                if (path.Block == block && path.Path.StartsWith("Attacks.", StringComparison.Ordinal)
                    && entry.TryGetProperty(path.Path[8..], out _)) Mark(resolution, path);
        }
    }
}

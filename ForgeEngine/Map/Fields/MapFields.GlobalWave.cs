using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ForgeMap.Fields;

/// <summary>
/// Group C2 global waves: the map editor's level-wide wave numbers, `forge.globalWave.Settings`, written by
/// `Native/Fields/GlobalWaveApplier.cs` once each level is there, onto the two components the game keeps them on —
/// `EnemyCostManager.AllowedTotalCost`, `EnemyPopulationManager.SetCooldownFactor`, `m_maxHeat`, `m_baseWeightTable`
/// and `SetHeat(eEnemyType, float)`. The initial heat is the current heat of each native heat type;
/// `m_heatTable` is the per-draw increment and is never overwritten by HeatsAtStart. No data block carries these
/// level fields, and no plan verb runs them.
///
/// Every field is optional: a level that sets one number keeps the game's own values for the rest. Each table is
/// exactly `eEnemyType`'s five members in the game's own order (Weakling, Standard, Special, MiniBoss, Boss), the
/// order both components index by. The per-type heat-on-select increments are not authored by this group, and the
/// two per-type cost tables are not map fields (an enemy's one point cost is its profile's).
/// </summary>
internal static partial class MapFieldSchema
{
    internal const string GlobalWaveBlock = "forge.globalWave.Settings";
    internal const string AllowedTotalCost = "AllowedTotalCost";
    internal const string HeatCooldownSpeed = "HeatCooldownSpeed";
    internal const string MaxHeat = "MaxHeat";
    internal const string BaseWeights = "BaseWeights";
    internal const string HeatsAtStart = "HeatsAtStart";

    /// <summary>The members of `eEnemyType`, which is the length every table here has to be.</summary>
    internal const int EnemyTypeCount = 5;

    private static MapFieldGroup GlobalWave() => new("global-wave", new[]
    {
        new MapFieldBlock(GlobalWaveBlock, MapFieldTargetKind.Level, new[]
        {
            new MapFieldPath(AllowedTotalCost, MapFieldKind.Number),
            new MapFieldPath(HeatCooldownSpeed, MapFieldKind.Number),
            new MapFieldPath(MaxHeat, MapFieldKind.Number),
            new MapFieldPath(BaseWeights, MapFieldKind.NumberList, Array.Empty<string>(), EnemyTypeCount),
            new MapFieldPath(HeatsAtStart, MapFieldKind.NumberList, Array.Empty<string>(), EnemyTypeCount)
        }, null)
    });
}

/// <summary>The wave numbers one level sets; a null member keeps the game's own value.</summary>
internal sealed record GlobalWaveTuning(float? AllowedTotalCost, float? HeatCooldownSpeed, float? MaxHeat,
    float[]? BaseWeights, float[]? HeatsAtStart)
{
    internal bool IsEmpty => AllowedTotalCost is null && HeatCooldownSpeed is null && MaxHeat is null
        && BaseWeights is null && HeatsAtStart is null;

    /// <summary>The values as one log line spells them, so a log names exactly what was written.</summary>
    internal string Describe()
    {
        var parts = new List<string>();
        if (AllowedTotalCost is { } allowed) parts.Add(MapFieldSchema.AllowedTotalCost + "=" + Spell(allowed));
        if (HeatCooldownSpeed is { } speed) parts.Add(MapFieldSchema.HeatCooldownSpeed + "=" + Spell(speed));
        if (MaxHeat is { } heat) parts.Add(MapFieldSchema.MaxHeat + "=" + Spell(heat));
        if (BaseWeights is { } weights) parts.Add(MapFieldSchema.BaseWeights + "=[" + string.Join(",", weights.Select(Spell)) + "]");
        if (HeatsAtStart is { } start) parts.Add(MapFieldSchema.HeatsAtStart + "=[" + string.Join(",", start.Select(Spell)) + "]");
        return string.Join(" ", parts);
    }

    private static string Spell(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>The global wave numbers of every level the documents name, built once when the documents load. The
/// numbers are weights, heats and a cost cap: a negative one would make the game's own weighted draw pick a type
/// it never should, so it is refused by field id and the rest of the level's numbers are kept.</summary>
internal sealed class GlobalWavePlan
{
    private readonly Dictionary<MapLevelReference, GlobalWaveTuning> _levels;

    private GlobalWavePlan(Dictionary<MapLevelReference, GlobalWaveTuning> levels) => _levels = levels;

    internal bool IsEmpty => _levels.Count == 0;

    internal GlobalWaveTuning? For(MapLevelReference? level)
        => level is { } key && _levels.TryGetValue(key, out var tuning) ? tuning : null;

    /// <summary>Joins already validated, disjoint owning levels; no document values are interpreted again.</summary>
    internal static GlobalWavePlan CombinePrepared(IEnumerable<GlobalWavePlan> parts)
        => new(parts.SelectMany(part => part._levels).ToDictionary(pair => pair.Key, pair => pair.Value));

    internal static GlobalWavePlan From(IEnumerable<MapFieldEntry> entries, Action<MapDiagnostic> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var levels = new Dictionary<MapLevelReference, GlobalWaveTuning>();
        foreach (var entry in entries)
        {
            if (entry.Block.Block != MapFieldSchema.GlobalWaveBlock) continue;
            var tuning = levels.TryGetValue(entry.Target.Level, out var current) ? current : new GlobalWaveTuning(null, null, null, null, null);
            float? Number(string path) => Value(entry, path, report) is { Count: 1 } one ? one[0] : null;
            float[]? Table(string path) => Value(entry, path, report) is { Count: MapFieldSchema.EnemyTypeCount } table ? table.ToArray() : null;
            tuning = tuning with
            {
                AllowedTotalCost = Number(MapFieldSchema.AllowedTotalCost) ?? tuning.AllowedTotalCost,
                HeatCooldownSpeed = Number(MapFieldSchema.HeatCooldownSpeed) ?? tuning.HeatCooldownSpeed,
                MaxHeat = Number(MapFieldSchema.MaxHeat) ?? tuning.MaxHeat,
                BaseWeights = Table(MapFieldSchema.BaseWeights) ?? tuning.BaseWeights,
                HeatsAtStart = Table(MapFieldSchema.HeatsAtStart) ?? tuning.HeatsAtStart
            };
            if (!tuning.IsEmpty) levels[entry.Target.Level] = tuning;
        }
        return new GlobalWavePlan(levels);
    }

    /// <summary>One field's numbers as the game's own single-precision values, or null when the entry does not carry
    /// it or a number is negative or out of single-precision range (reported by field id).</summary>
    private static IReadOnlyList<float>? Value(MapFieldEntry entry, string path, Action<MapDiagnostic> report)
    {
        if (!entry.Fields.TryGetValue(path, out var value)) return null;
        var numbers = value.Kind == MapFieldKind.Number ? new[] { value.Number } : value.Numbers ?? Array.Empty<double>();
        if (numbers.Any(number => number < 0 || number > float.MaxValue))
        {
            report(MapFieldDocuments.Diagnostic(MapLogCodes.FieldValue, entry.Source + " entry=" + entry.Index.ToString(CultureInfo.InvariantCulture)
                + " field=" + entry.Block.Id(path), "Expected finite non-negative numbers."));
            return null;
        }
        return numbers.Select(number => (float)number).ToArray();
    }
}

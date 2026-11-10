using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeReplay;

/// <summary>One measured number of a member (PLAN 6 member rating): which radar corner it feeds and which way is better. The names are the keys of
/// <c>rating.forge</c>; the numbers they are placed by live in that file, not here.</summary>
internal readonly record struct RatingMetric(string Key, int Dimension, bool LowerIsBetter);

internal static class RatingMetrics
{
    /// <summary>The radar corners in drawing order; each has the language key <c>rating.dim.</c> plus its name.</summary>
    internal static readonly string[] Dimensions = { "output", "kills", "accuracy", "survival", "support", "mission" };

    internal static readonly RatingMetric[] All =
    {
        new("damagePerMinute", 0, false),
        new("killsPerMinute", 1, false),
        new("weakShare", 2, false),
        new("takenPerMinute", 3, true),
        new("downsPerMinute", 3, true),
        new("rescuesPerMinute", 4, false),
        new("gavePerMinute", 4, false),
        new("interactionsPerMinute", 5, false),
    };

    internal static int IndexOf(string key) => Array.FindIndex(All, m => m.Key == key);
}

/// <summary>The numbers of one member of one recording, one per <see cref="RatingMetrics.All"/> (null: the recording does not say, which is not 0),
/// and which of them were worked out rather than read (<see cref="Estimated"/>, one bit per metric).</summary>
internal sealed class MemberValues
{
    internal readonly double?[] Value = new double?[RatingMetrics.All.Length];
    internal uint Estimated;

    internal bool Any => Value.Any(v => v != null);

    /// <summary>The member in <paramref name="slot"/> from the whole read of a recording. Rates are per minute of the recording; what is already
    /// told apart as exact, estimated or empty on the statistics (<see cref="ReplayFeed.Cell"/>) is told apart the same way here.</summary>
    internal static MemberValues Read(Analysis analysis, int slot, uint durationMs, float fullHealth)
    {
        var m = new MemberValues();
        var minutes = durationMs / 60000.0;
        if (minutes <= 0) return m;
        var p = analysis.Players[slot];
        void Put(string key, StatRow row, double? value)
        {
            var cell = ReplayFeed.Cell(row, p, analysis, slot, fullHealth);
            if (cell.Quality == StatQuality.Empty || value == null) return;
            var i = RatingMetrics.IndexOf(key);
            m.Value[i] = value;
            if (cell.Quality == StatQuality.Estimate) m.Estimated |= 1u << i;
        }
        Put("damagePerMinute", StatRow.Damage, (p.BulletDamage + p.MeleeDamage + p.ExplosionDamage) / minutes);
        Put("killsPerMinute", StatRow.Kills, p.Kills / minutes);
        Put("weakShare", StatRow.WeakSpots, p.Landed == 0 ? null : (double)p.Weak / p.Landed);
        Put("takenPerMinute", StatRow.Taken, (analysis.HurtExact(slot) ? p.DamageTaken : p.TakenShare * fullHealth) / minutes);
        Put("downsPerMinute", StatRow.Downs, p.Downs / minutes);
        Put("rescuesPerMinute", StatRow.Rescues, p.Rescues / minutes);
        if (analysis.Report is { } r)
        {
            // What a member handed to the others is the host's to settle, the recorder's own, or a client's rows once the host's records are in force
            // (its Supply records add what its own machine gave; a client's own gifts are recorded on its machine).
            if (analysis.Exact || analysis.HostWhole || slot == analysis.RecorderSlot)
            {
                var gave = 0;
                for (var kind = 0; kind < 3; kind++) gave += r.Gave[slot, kind];
                m.Value[RatingMetrics.IndexOf("gavePerMinute")] = gave / minutes;
            }
            // Terminals used, scans stood in, doors opened: each names the player in the recording itself.
            var did = r.TerminalSpans.Count(t => t.Slot == slot) + r.Scans.Count(s => (s.Slots >> slot & 1) != 0) + r.Doors.Count(d => d.Slot == slot);
            m.Value[RatingMetrics.IndexOf("interactionsPerMinute")] = did / minutes;
        }
        return m;
    }
}

/// <summary>One grade of the total: its name and the lowest score that earns it.</summary>
internal readonly record struct RatingGrade(string Name, double From);

/// <summary>One measured number's two fixed anchors: the owner's value (the bottom of the S band) and the teammate low anchor (the D grade). For a
/// lower-is-better metric the owner value is the lower one.</summary>
internal readonly record struct RatingAnchor(double Owner, double Low);

/// <summary>What <c>rating.forge</c> holds: the shortest recording that is rated, the least dimensions a grade needs, the 15 grade cuts, and for
/// each metric the two fixed anchors its bands are interpolated between. There is no distribution of other players and no provisional grade.</summary>
internal sealed class RatingTable
{
    /// <summary>The score the owner anchor stands for: the bottom of the S band, S-. The owner's better run and any better play reach S and S+.</summary>
    internal const double OwnerScore = 0.91;
    /// <summary>The score the low anchor stands for: the D grade.</summary>
    internal const double LowScore = 0.14;

    internal int MinSeconds { get; private init; }
    internal int MinDimensions { get; private init; }
    internal IReadOnlyList<RatingGrade> Grades { get; private init; } = Array.Empty<RatingGrade>();
    internal RatingAnchor[] Anchors { get; private init; } = Array.Empty<RatingAnchor>();

    internal static RatingTable Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!ForgeCore.FormatVersion.Matches(root)) throw new FormatException("rating.forge: schemaVersion must be the string " + ForgeCore.FormatVersion.Current + ".");
        int Int(string name, int least)
        {
            if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < least) throw new FormatException("rating.forge: " + name + " must be a whole number of at least " + least + ".");
            return n;
        }
        var minSeconds = Int("minSeconds", 0); var minDimensions = Int("minDimensions", 1);
        if (minDimensions > RatingMetrics.Dimensions.Length) throw new FormatException("rating.forge: minDimensions is more than the " + RatingMetrics.Dimensions.Length + " dimensions.");
        if (!root.TryGetProperty("grades", out var gradeList) || gradeList.ValueKind != JsonValueKind.Array || gradeList.GetArrayLength() < 2) throw new FormatException("rating.forge: grades must list at least two grades, best first.");
        var grades = new List<RatingGrade>();
        foreach (var g in gradeList.EnumerateArray())
        {
            if (g.ValueKind != JsonValueKind.Object || !g.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(name.GetString())
                || !g.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Number) throw new FormatException("rating.forge: a grade is {\"name\": text, \"from\": number}.");
            grades.Add(new RatingGrade(name.GetString()!, from.GetDouble()));
        }
        for (var i = 0; i < grades.Count; i++)
            if (grades[i].From < 0 || grades[i].From > 1 || i > 0 && grades[i].From >= grades[i - 1].From) throw new FormatException("rating.forge: the grades' \"from\" must fall from the best grade to the worst, inside 0 to 1.");
        if (grades[^1].From != 0) throw new FormatException("rating.forge: the worst grade must start at 0.");
        if (!root.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object) throw new FormatException("rating.forge: metrics must be an object.");
        foreach (var named in metrics.EnumerateObject())
            if (RatingMetrics.IndexOf(named.Name) < 0) throw new FormatException("rating.forge: the metric " + named.Name + " is not one this version measures.");
        var anchors = new RatingAnchor[RatingMetrics.All.Length];
        for (var i = 0; i < anchors.Length; i++)
        {
            var metric = RatingMetrics.All[i];
            if (!metrics.TryGetProperty(metric.Key, out var node)) throw new FormatException("rating.forge: the metric " + metric.Key + " is missing.");
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("owner", out var ownerNode) || ownerNode.ValueKind != JsonValueKind.Number
                || !node.TryGetProperty("low", out var lowNode) || lowNode.ValueKind != JsonValueKind.Number) throw new FormatException("rating.forge: " + metric.Key + " must be {\"owner\": number, \"low\": number}.");
            var owner = ownerNode.GetDouble(); var low = lowNode.GetDouble();
            if (!double.IsFinite(owner) || !double.IsFinite(low) || owner == low) throw new FormatException("rating.forge: the anchors of " + metric.Key + " must be two different numbers.");
            if (metric.LowerIsBetter ? owner >= low : owner <= low) throw new FormatException("rating.forge: the owner anchor of " + metric.Key + " must be " + (metric.LowerIsBetter ? "below" : "above") + " its low anchor.");
            anchors[i] = new RatingAnchor(owner, low);
        }
        return new RatingTable { MinSeconds = minSeconds, MinDimensions = minDimensions, Grades = grades, Anchors = anchors };
    }

    /// <summary>Where <paramref name="value"/> of <paramref name="metric"/> sits between its low anchor (the D grade, 0.14) and the owner anchor
    /// (the bottom of the S band, 0.91), clamped to 0 to 1: past the owner on the better side earns S/S+, past the low anchor on the worse side D-.</summary>
    internal double Score(int metric, double value)
    {
        var anchor = Anchors[metric];
        return Math.Clamp(LowScore + (value - anchor.Low) / (anchor.Owner - anchor.Low) * (OwnerScore - LowScore), 0, 1);
    }

    internal int GradeOf(double score)
    {
        for (var i = 0; i < Grades.Count; i++) if (score >= Grades[i].From) return i;
        return Grades.Count - 1;
    }
}

/// <summary>What one recording adds to the rating cache: when it was read (the file's fingerprint), how long it ran, and each member's numbers
/// (null for what the recording does not say).</summary>
internal sealed class RatingCacheEntry
{
    public string Fingerprint { get; set; } = "";
    public uint DurationMs { get; set; }
    public double?[][] Members { get; set; } = Array.Empty<double?[]>();
    /// <summary>The player slot of each member and which of its numbers were worked out (a bit per metric), in the order of <see cref="Members"/>: what the list needs to show each player's grade without reading the recording.</summary>
    public int[] Slots { get; set; } = Array.Empty<int>();
    public uint[] Estimated { get; set; } = Array.Empty<uint>();
}

/// <summary>The file in the replay folder that keeps every recording's member numbers, so a recording is read again only when its file changes:
/// one entry per recording file name. Entries of recordings that were deleted since stay: the history of this machine.</summary>
internal sealed class RatingCache
{
    internal const int MaxRecordings = 500;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string SchemaVersion { get; set; } = ForgeCore.FormatVersion.Current;
    public SortedDictionary<string, RatingCacheEntry> Recordings { get; set; } = new(StringComparer.Ordinal);

    internal static RatingCache Read(string text)
    {
        try
        {
            var cache = JsonSerializer.Deserialize<RatingCache>(text, Json);
            if (cache == null || cache.SchemaVersion != ForgeCore.FormatVersion.Current) return new RatingCache();
            foreach (var key in cache.Recordings.Where(r => r.Value.Members.Any(m => m == null || m.Length != RatingMetrics.All.Length) || r.Value.Slots.Length != r.Value.Members.Length || r.Value.Estimated.Length != r.Value.Members.Length).Select(r => r.Key).ToList()) cache.Recordings.Remove(key);
            return cache;
        }
        catch (JsonException) { return new RatingCache(); }
    }

    internal bool Has(string name, string fingerprint) => Recordings.TryGetValue(name, out var e) && e.Fingerprint == fingerprint;

    /// <summary>Puts a read recording in (replacing the earlier read of the same file), keeping the newest <see cref="MaxRecordings"/>.</summary>
    internal void Put(string name, string fingerprint, uint durationMs, IEnumerable<(int Slot, MemberValues Values)> members)
    {
        var kept = members.Where(m => m.Values.Any).ToArray();
        Recordings[name] = new RatingCacheEntry { Fingerprint = fingerprint, DurationMs = durationMs, Members = kept.Select(m => m.Values.Value).ToArray(), Slots = kept.Select(m => m.Slot).ToArray(), Estimated = kept.Select(m => m.Values.Estimated).ToArray() };
        while (Recordings.Count > MaxRecordings) Recordings.Remove(Recordings.Keys.First());
    }

    /// <summary>The numbers of the member in <paramref name="slot"/> of the recording read as <paramref name="name"/>; null when it is not in the cache or that slot had no number.</summary>
    internal MemberValues? MemberOf(string name, int slot)
    {
        if (!Recordings.TryGetValue(name, out var entry)) return null;
        var i = Array.IndexOf(entry.Slots, slot);
        if (i < 0) return null;
        var values = new MemberValues { Estimated = entry.Estimated[i] };
        for (var k = 0; k < values.Value.Length; k++) values.Value[k] = entry.Members[i][k];
        return values;
    }

    internal string Write() => JsonSerializer.Serialize(this, Json);
}

internal enum RatingState : byte
{
    /// <summary>A grade from the fixed anchors of <c>rating.forge</c>.</summary>
    Rated,
    /// <summary>No grade: fewer than <see cref="RatingTable.MinDimensions"/> dimensions have a number.</summary>
    TooFewDimensions,
}

/// <summary>A dimension's score (0 to 1, null: nothing in the recording says it) and whether any number in it was worked out.</summary>
internal readonly record struct DimensionScore(double? Score, bool Estimated);

/// <summary>A member's rating: a score per dimension (the radar's corners) and, unless <see cref="State"/> says otherwise, the grade (an index into <see cref="RatingTable.Grades"/>, best first, -1 for none) of their mean.</summary>
internal sealed record MemberRating(RatingState State, int Grade, double? Score, DimensionScore[] Dimensions)
{
    internal static MemberRating Rate(RatingTable table, MemberValues member)
    {
        var sum = new double[RatingMetrics.Dimensions.Length]; var count = new int[sum.Length];
        var estimated = new bool[sum.Length];
        for (var i = 0; i < RatingMetrics.All.Length; i++)
        {
            if (member.Value[i] is not { } value) continue;
            var metric = RatingMetrics.All[i];
            sum[metric.Dimension] += table.Score(i, value);
            count[metric.Dimension]++;
            estimated[metric.Dimension] |= (member.Estimated >> i & 1) != 0;
        }
        var dimensions = new DimensionScore[sum.Length];
        double total = 0; var known = 0;
        for (var d = 0; d < dimensions.Length; d++)
        {
            dimensions[d] = count[d] == 0 ? new DimensionScore(null, false) : new DimensionScore(sum[d] / count[d], estimated[d]);
            if (count[d] == 0) continue;
            total += sum[d] / count[d]; known++;
        }
        if (known < table.MinDimensions) return new MemberRating(RatingState.TooFewDimensions, -1, null, dimensions);
        var score = total / known;
        return new MemberRating(RatingState.Rated, table.GradeOf(score), score, dimensions);
    }
}

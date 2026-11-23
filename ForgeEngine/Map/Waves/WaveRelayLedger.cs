using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

internal enum WaveStage { Started = 1, Exhausted = 2, Cleared = 3 }

/// <summary>A list entry the checkpoint restore has to start again, and how many seconds of its delay remain.</summary>
internal readonly record struct WaveRelayStart(WaveResource Resource, double DelaySeconds);

/// <summary>
/// What a host's list entries had reached when a checkpoint was stored: per wave resource, the time its started,
/// exhausted and cleared facts first happened. The record travels the Runtime's native-state path with the vanilla
/// checkpoint, so a restore knows exactly which later entries were already owed a start.
///
/// It schedules nothing. Entries an earlier entry starts are ordinary generated behaviors (a wave fact, a delay,
/// <c>wave_start</c>); a restore loses a behavior's pending delay and never re-emits the facts of a wave that had
/// already ended, so <see cref="Plan"/> lists the entries whose predecessor had reached the anchor and that had not
/// started yet, with their remaining delay, and the session starts them through the same wave entry. An entry that
/// had already started is never started again: its address is held in a guard that refuses the first start the
/// relay behavior requests for it after the restore.
/// </summary>
internal sealed class WaveRelayLedger
{
    public const string StateKey = "forge.map.wave-relay";
    public const int MaximumEntries = 4096;

    private sealed class Row { internal double Started = double.NaN, Exhausted = double.NaN, Cleared = double.NaN; }

    private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    /// <summary>Entries whose next start request is refused, by the time that refusal lapses: the replayed fact arrives
    /// right after the restore, so a guard the relay behavior never consumed must not refuse a later, legitimate start.</summary>
    private readonly Dictionary<string, double> _guard = new(StringComparer.Ordinal);
    public const double GuardSeconds = 30;

    /// <summary>The restore has put a saved record back and the owed starts have not been planned yet.</summary>
    public bool Restored { get; private set; }
    public int Count => _rows.Count;

    /// <summary>False when the record is full and the address is new: nothing was stored, the caller reports it.</summary>
    public bool Record(string address, WaveStage stage, double now)
    {
        if (!_rows.TryGetValue(address, out var row))
        {
            if (_rows.Count >= MaximumEntries) return false;
            _rows.Add(address, row = new Row());
        }
        ref double slot = ref Slot(row, stage);
        if (double.IsNaN(slot)) slot = now;
        return true;
    }

    public double? ReachedAt(string address, WaveStage stage)
        => _rows.TryGetValue(address, out var row) && !double.IsNaN(Slot(row, stage)) ? Slot(row, stage) : null;

    private static ref double Slot(Row row, WaveStage stage)
    {
        switch (stage)
        {
            case WaveStage.Started: return ref row.Started;
            case WaveStage.Exhausted: return ref row.Exhausted;
            default: return ref row.Cleared;
        }
    }

    /// <summary>True once per address that had started before the stored checkpoint (or was started again by the restore),
    /// within <see cref="GuardSeconds"/> of the restore: that start request comes from a relay behavior replaying a fact of
    /// its predecessor and must not run twice.</summary>
    public bool RefuseRestoredStart(string address, double now) => _guard.Remove(address, out var until) && now <= until;

    public JsonElement Capture(double now) => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = FormatVersion.Current,
        now,
        entries = _rows.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
        {
            address = pair.Key,
            started = Nullable(pair.Value.Started),
            exhausted = Nullable(pair.Value.Exhausted),
            cleared = Nullable(pair.Value.Cleared)
        }).ToArray()
    });

    private static double? Nullable(double value) => double.IsNaN(value) ? null : value;

    /// <summary>Puts a stored record back. Times are kept as ages against <paramref name="now"/>, so the clock the world
    /// restarts on does not matter. Every started entry a host's earlier entry starts (<paramref name="isRelayEntry"/>) is guarded against a replayed start.</summary>
    public void Restore(JsonElement data, double now, Func<string, bool> isRelayEntry)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException("wave-relay-object");
        string[] fields = { "schemaVersion", "now", "entries" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
            if (System.Array.IndexOf(fields, property.Name) < 0 || !seen.Add(property.Name)) throw new FormatException("wave-relay-field");
        if (seen.Count != fields.Length) throw new FormatException("wave-relay-field");
        if (data.GetProperty("schemaVersion").GetString() != FormatVersion.Current) throw new FormatException("wave-relay-version");
        var savedNow = data.GetProperty("now").GetDouble();
        if (!double.IsFinite(savedNow)) throw new FormatException("wave-relay-now");
        var array = data.GetProperty("entries");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumEntries) throw new FormatException("wave-relay-entries");
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var address = item.GetProperty("address").GetString();
            if (string.IsNullOrEmpty(address) || rows.ContainsKey(address)) throw new FormatException("wave-relay-address");
            rows.Add(address, new Row
            {
                Started = Age(item, "started", savedNow, now),
                Exhausted = Age(item, "exhausted", savedNow, now),
                Cleared = Age(item, "cleared", savedNow, now)
            });
        }
        _rows.Clear(); _guard.Clear();
        foreach (var (address, row) in rows)
        {
            _rows.Add(address, row);
            if (!double.IsNaN(row.Started) && isRelayEntry(address)) _guard[address] = now + GuardSeconds;
        }
        Restored = true;
    }

    private static double Age(JsonElement item, string name, double savedNow, double now)
    {
        var value = item.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return double.NaN;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var at) || !double.IsFinite(at)) throw new FormatException("wave-relay-time");
        return now - Math.Max(0, savedNow - at);
    }

    /// <summary>The entries a restore owes a start: not started when the checkpoint was stored, while the earlier entry
    /// that starts them had reached the anchor. Their guard is armed so a relay behavior that fires for the same
    /// fact afterwards does not start them a second time.</summary>
    public IReadOnlyList<WaveRelayStart> Plan(IEnumerable<WaveHostProgram> hosts, double now)
    {
        var plan = new List<WaveRelayStart>();
        foreach (var host in hosts)
            foreach (var wave in host.Waves)
            {
                if (wave.After is not { } after || after.Ordinal < 0 || after.Ordinal >= host.Waves.Count) continue;
                if (ReachedAt(wave.Address, WaveStage.Started) != null) continue;
                var stage = after.When switch { "exhausted" => WaveStage.Exhausted, "cleared" => WaveStage.Cleared, _ => WaveStage.Started };
                if (ReachedAt(host.Waves[after.Ordinal].Address, stage) is not { } reached) continue;
                plan.Add(new WaveRelayStart(wave, Math.Max(0, after.Seconds - (now - reached))));
            }
        Restored = false;
        foreach (var start in plan) _guard[start.Resource.Address] = now + GuardSeconds;
        return plan;
    }

    public void Clear()
    {
        _rows.Clear(); _guard.Clear(); Restored = false;
    }
}

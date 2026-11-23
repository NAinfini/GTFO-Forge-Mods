using System;
using System.Collections.Generic;
using System.Text.Json;
using ChainedPuzzles;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>
/// Hands the Map's chained-puzzle handles to the host that takes over. The alarm rows and the alarm-state facts each
/// bind a kernel handle to one native puzzle instance; the instance itself stays in the game, so only its identity
/// crosses: the capture names each live puzzle by its own `m_puzzleUID`, and the receiving machine looks that uid up
/// in its own instance table and rebinds the same handle to it. A uid the receiver cannot find, or finds twice, is
/// refused by name; a handle is never pointed at another puzzle.
///
/// A puzzle already collected on the source has nothing left to name: its handle is rebound to an ended object so the
/// kernel's next sweep releases it. A handle that holds anything else is refused — in particular the handles of a
/// wave this package started, which carry a locally owned lifetime and a Mastermind event the new host cannot
/// recall; those end before a migration can be captured.
/// </summary>
internal sealed class MapHandleHandover
{
    internal const string Schema = "map-puzzle-handles";
    internal const string RefusedCode = "migration-native-handle";
    private static readonly object Ended = new();

    private readonly RuntimeModuleHandle _registration;
    private readonly LevelObjectModule _levels;

    private MapHandleHandover(RuntimeModuleHandle registration, LevelObjectModule levels)
    { _registration = registration; _levels = levels; }

    /// <summary>Registers the handover during the registration window, on the registration every Map handle is minted on.</summary>
    internal static MapHandleHandover Register(RuntimeModuleHandle registration, LevelObjectModule levels)
    {
        var handover = new MapHandleHandover(registration, levels);
        registration.RegisterProviderHandover(Schema, handover.Capture, handover.Prepare);
        return handover;
    }

    private sealed record Entry(string Uid, bool Live, string? Alarm);

    /// <summary>Every puzzle-bound handle by kernel slot. A handle bound to something else has no row, which is what
    /// makes the receiving side refuse it.</summary>
    internal JsonElement Capture(JsonElement obligations)
    {
        var rows = new List<object>();
        foreach (var row in obligations.GetProperty("handles").EnumerateArray())
        {
            if (!RuntimeHandle.TryRead(row.GetProperty("handle"), out var handle)
                || !_registration.TryNative(handle, out var native) || native is not ChainedPuzzleInstance puzzle) continue;
            var slot = handle.Local;
            var live = !puzzle.WasCollected;
            rows.Add(new { slot, uid = live ? puzzle.m_puzzleUID ?? "" : "", live, alarm = _levels.AlarmUidOf(slot) });
        }
        return RuntimeJson.From(new { puzzles = rows });
    }

    internal ProviderHandoverAdoption Prepare(JsonElement context)
    {
        var source = Read(context.GetProperty("source"));
        var retained = Read(context.GetProperty("retained"));
        var bindings = new List<ProviderHandleAdoption>();
        var alarms = new Dictionary<string, RuntimeHandle>(StringComparer.Ordinal);
        foreach (var row in context.GetProperty("handles").EnumerateArray())
        {
            if (!RuntimeHandle.TryRead(row.GetProperty("handle"), out var value))
                throw new RuntimeContractException("migration-provider-data", "The Map handover names a handle that is not one.");
            var slot = value.Local;
            if (!row.GetProperty("native").GetBoolean() || !row.GetProperty("liveness").GetBoolean() || row.GetProperty("cancel").GetBoolean()
                || !(source.TryGetValue(slot, out var entry) || retained.TryGetValue(slot, out entry)))
                throw new RuntimeContractException(RefusedCode, "A Map handle that names no chained puzzle cannot be handed over (slot " + slot + ").");
            if (!entry.Live)
            {
                bindings.Add(new ProviderHandleAdoption(value, Ended, static _ => false));
                continue;
            }
            var puzzle = ChainedPuzzleLookup.Find(entry.Uid, out var ambiguous);
            if (puzzle == null)
                throw new RuntimeContractException(RefusedCode, (ambiguous ? "Two chained puzzles answer the uid " : "No chained puzzle answers the uid ") + entry.Uid + ".");
            bindings.Add(new ProviderHandleAdoption(value, puzzle, ChainedPuzzleLookup.Liveness(puzzle)));
            if (entry.Alarm != null) alarms[entry.Alarm] = value;
        }
        Dictionary<string, RuntimeHandle>? previous = null;
        return new ProviderHandoverAdoption(bindings, () => previous = _levels.SwapAlarmHandles(alarms),
            () => { if (previous != null) _levels.SwapAlarmHandles(previous); });
    }

    private static Dictionary<int, Entry> Read(JsonElement envelope)
    {
        var rows = new Dictionary<int, Entry>();
        if (envelope.ValueKind == JsonValueKind.Null) return rows;
        if (!envelope.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("puzzles", out var puzzles)
            || puzzles.ValueKind != JsonValueKind.Array)
            throw new RuntimeContractException("migration-provider-data", "The Map handover payload lacks its puzzle rows.");
        foreach (var row in puzzles.EnumerateArray())
        {
            var alarm = row.GetProperty("alarm");
            if (!rows.TryAdd(row.GetProperty("slot").GetInt32(),
                    new Entry(row.GetProperty("uid").GetString() ?? "", row.GetProperty("live").GetBoolean(),
                        alarm.ValueKind == JsonValueKind.String ? alarm.GetString() : null)))
                throw new RuntimeContractException("migration-provider-data", "The Map handover payload names a slot twice.");
        }
        return rows;
    }
}

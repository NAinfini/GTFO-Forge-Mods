using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ChainedPuzzles;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using LevelGeneration;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of `forge.action.map.door_alarm`: which doors carry a scan other than the one generation gave
/// them, and the one native write that swaps it — the lock component's own `SetupForChainedPuzzle`, which sets the
/// lock's puzzle and rewrites the door's prompt locally (Map/evidence/door-alarm.json). Because that write is local,
/// every machine applies it itself: the host records the swap in the native state key <see cref="DoorAlarmRecord.StateKey"/>, which
/// rides the existing snapshot, delta, checkpoint and migration paths, and each receiver applies the same record.
/// The replacement is a scene scan the level built on every machine, found by its resource id; the alarm a door was
/// built with stays on each machine (it is that machine's own instance) and is what putting back restores.
/// </summary>
internal sealed class DoorAlarmSwaps : IDisposable
{
    private readonly DomainStateChannel.NativeStateSource _state;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    /// <summary>The replaced doors and their scans; the same on every machine.</summary>
    private readonly DoorAlarmRecord _swapped = new();
    /// <summary>Door address → the alarm this machine built the door with. Local, never sent.</summary>
    private readonly Dictionary<string, ChainedPuzzleInstance> _built = new(StringComparer.Ordinal);

    internal DoorAlarmSwaps(RuntimeModuleHandle registration, IForgeEngine kernel)
    {
        _state = DomainStateChannel.RegisterNativeState(kernel, DoorAlarmRecord.StateKey, _swapped.Capture, Receive);
        try
        {
            // A new level drops every swap of the old one; a host migration keeps the level and its doors.
            _lifecycle = registration.ObserveLifecycle(value =>
            {
                if (value.Kind == RuntimeLifecycleKind.WorldChanged && !value.IsMigration) { _swapped.Clear(); _built.Clear(); }
            });
        }
        catch { _state.Dispose(); throw; }
    }

    /// <summary>Replaces one door's alarm with <paramref name="scan"/> (host).</summary>
    internal MapActionOutcome Replace(LG_SecurityDoor door, MapObjectReference address, ChainedPuzzleInstance scan, string scanId)
    {
        if (Lock(door, address, out var locks) is { } refused) return refused;
        var key = address.ToString();
        if (locks!.ChainedPuzzleToSolve is { } held && held.Pointer == scan.Pointer) return MapActionOutcome.AlreadyInState(ResultCode.DoorAlarmAlreadySet);
        if (!Remember(key, locks)) return MapActionOutcome.Refused(ResultCode.DoorAlarmNoScan);
        var outcome = Apply(door, locks, scan, key, true, ResultCode.DoorAlarmReplaced);
        if (outcome.Commit == MapActionCommit.Issued) { _swapped.Set(key, scanId); _state.Changed(); }
        return outcome;
    }

    /// <summary>Puts one door's own alarm back (host).</summary>
    internal MapActionOutcome Restore(LG_SecurityDoor door, MapObjectReference address)
    {
        if (Lock(door, address, out var locks) is { } refused) return refused;
        var key = address.ToString();
        if (!_swapped.Contains(key) || !_built.TryGetValue(key, out var built) || built.WasCollected)
            return MapActionOutcome.AlreadyInState(ResultCode.DoorAlarmAlreadySet);
        var outcome = Apply(door, locks!, built, key, true, ResultCode.DoorAlarmRestored);
        if (outcome.Commit == MapActionCommit.Issued) { _swapped.Remove(key); _state.Changed(); }
        return outcome;
    }

    /// <summary>The door's lock, or the refusal: a door already pulled (its scan running or done, the door opening or
    /// open) keeps its scan, and a door whose lock is not a scan (key, generator, no-key lock) has none to change.</summary>
    private static MapActionOutcome? Lock(LG_SecurityDoor door, MapObjectReference address, out LG_SecurityDoor_Locks? locks)
    {
        locks = null;
        if (door == null || door.WasCollected || !DoorObservation.IsCurrentAddress(door, address)) return MapActionOutcome.Refused(ResultCode.StaleEntity);
        if (door.m_locks?.TryCast<LG_SecurityDoor_Locks>() is not { WasCollected: false } found) return MapActionOutcome.Refused(ResultCode.DoorNoLockComponent);
        locks = found;
        var status = door.LastStatus;
        var held = found.ChainedPuzzleToSolve;
        if (status is eDoorStatus.ChainedPuzzleActivated or eDoorStatus.Opening or eDoorStatus.Open or eDoorStatus.Unlocked
            || held != null && !held.WasCollected && (held.IsActive || held.IsSolved))
            return MapActionOutcome.Refused(ResultCode.DoorAlarmAlreadyPulled);
        if (status is not (eDoorStatus.Closed_LockedWithChainedPuzzle or eDoorStatus.Closed_LockedWithChainedPuzzle_Alarm) || held == null)
            return MapActionOutcome.Refused(ResultCode.DoorAlarmNoScan);
        return null;
    }

    /// <summary>Keeps the alarm generation gave the door the first time this machine changes it.</summary>
    private bool Remember(string key, LG_SecurityDoor_Locks locks)
    {
        if (_built.ContainsKey(key)) return true;
        if (locks.ChainedPuzzleToSolve is not { WasCollected: false } held) return false;
        _built[key] = held;
        return true;
    }

    /// <summary>The native write and its readback. The lock's own setup subscribes its solved callback to the puzzle on
    /// every call, so a puzzle the lock held before would answer twice; the extra subscriptions are taken off the
    /// puzzle's own list. The prompt the setup wrote is read back from the door's open interaction and logged.</summary>
    private static MapActionOutcome Apply(LG_SecurityDoor door, LG_SecurityDoor_Locks locks, ChainedPuzzleInstance scan, string key, bool host, string code)
    {
        eDoorStatus status;
        try
        {
            status = locks.SetupForChainedPuzzle(scan);
            OneSolvedCallback(scan, locks);
            // The setup writes the lock only. When the new puzzle changes the door's status (alarm to plain scan or
            // back), the host publishes it through the door's own replicator, as the key-card unlock does.
            if (host && door.m_sync?.TryCast<LG_Door_Sync>() is { m_stateReplicator: not null } sync)
            {
                var state = sync.GetCurrentSyncState();
                if (state.status != status) { state.status = status; sync.m_stateReplicator.OnStateChange_Master(state); }
            }
        }
        catch (Exception error)
        {
            MapLog.Record(RuntimeLogLevel.Error, DoorLocksCodes.AlarmFailed, "Door alarm could not be changed.",
                RuntimeLogValue.Of("door", key), RuntimeLogValue.Of("exception", error.GetType().Name));
            return MapActionOutcome.Refused(ResultCode.NativeCommitException);
        }
        var text = locks.m_intOpenDoor?.InteractionMessage ?? "";
        var name = scan.Data?.PublicAlarmName ?? "";
        // The alarm prompt is Text 0x348 formatted with the puzzle's public alarm name; a plain scan's prompt carries no name.
        var confirmed = locks.ChainedPuzzleToSolve?.Pointer == scan.Pointer
            && (status != eDoorStatus.Closed_LockedWithChainedPuzzle_Alarm || name.Length == 0 || text.Contains(name, StringComparison.Ordinal));
        MapLog.Record(confirmed ? RuntimeLogLevel.Info : RuntimeLogLevel.Error, confirmed ? DoorLocksCodes.AlarmChanged : DoorLocksCodes.AlarmFailed,
            confirmed ? "Door alarm changed." : "Door alarm readback did not show the new scan.",
            RuntimeLogValue.Of("door", key), RuntimeLogValue.Of("scan", ScanInstanceResolver.ResourceId(scan) ?? ""),
            RuntimeLogValue.Of("alarm", name), RuntimeLogValue.Of("status", ((int)status).ToString(CultureInfo.InvariantCulture)),
            RuntimeLogValue.Of("text", text), RuntimeLogValue.Of("host", host ? "true" : "false"));
        return confirmed ? MapActionOutcome.Issued(code) : MapActionOutcome.Refused(ResultCode.ReadbackUnconfirmed);
    }

    private static void OneSolvedCallback(ChainedPuzzleInstance scan, LG_SecurityDoor_Locks locks)
    {
        var callbacks = scan.OnPuzzleSolved?.GetInvocationList();
        if (callbacks == null) return;
        var seen = false;
        foreach (var callback in callbacks)
        {
            if (callback?.Target?.Pointer != locks.Pointer) continue;
            if (!seen) { seen = true; continue; }
            scan.remove_OnPuzzleSolved(callback.Cast<Il2CppSystem.Action>());
        }
    }

    /// <summary>Another machine's record (or a checkpoint's): every door it names gets that scan, every door it no longer
    /// names gets its own alarm back. A door this machine cannot find or change is logged and left as it is.</summary>
    private void Receive(JsonElement value)
    {
        var next = DoorAlarmRecord.Parse(value);
        if (next.Keys.Any(door => MapObjectDoorAddress.TryParse(door) == null)) throw new FormatException("door-alarm-state");
        var host = Session.IsHost;
        foreach (var door in _swapped.Replace(next))
            if (_built.TryGetValue(door, out var built)) ApplyHere(door, built, host, ResultCode.DoorAlarmRestored);
        foreach (var pair in next)
        {
            var scan = ChainedPuzzleLookup.Find(pair.Value, out _);
            if (scan == null) { Missing(pair.Key, "scan-missing"); continue; }
            ApplyHere(pair.Key, scan, host, ResultCode.DoorAlarmReplaced);
        }
    }

    private void ApplyHere(string key, ChainedPuzzleInstance scan, bool host, string code)
    {
        if (DoorObservation.ByAddress(MapObjectDoorAddress.TryParse(key)!) is not { WasCollected: false } door
            || door.m_locks?.TryCast<LG_SecurityDoor_Locks>() is not { WasCollected: false } locks)
        { Missing(key, "door-missing"); return; }
        if (locks.ChainedPuzzleToSolve is { } held && held.Pointer == scan.Pointer) return;
        if (!Remember(key, locks)) { Missing(key, "door-has-no-scan"); return; }
        Apply(door, locks, scan, key, host, code);
    }

    private static void Missing(string key, string what) => MapLog.Record(RuntimeLogLevel.Error, DoorLocksCodes.AlarmFailed,
        "Door alarm record could not be applied here.", RuntimeLogValue.Of("door", key), RuntimeLogValue.Of("missing", what));

    public void Dispose() { _lifecycle.Dispose(); _state.Dispose(); _swapped.Clear(); _built.Clear(); }
}

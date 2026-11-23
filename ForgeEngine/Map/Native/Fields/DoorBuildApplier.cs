using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeMap.Fields;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// Group E1's native half: writes the door-build block fields (<see cref="MapFieldSchema.GateBlock"/>,
/// <see cref="MapFieldSchema.WeakLockBlock"/>) into the level while it is generated. Generation is local and
/// deterministic, so every peer reads the same documents and writes the same members; nothing goes over the
/// network.
///
/// - The entrance gate of a zone: a prefix on `LG_BuildGateJob.Build` (dump.cs:690299) writes
///   `LG_Gate.ForceSecurityFlip` (dump.cs:684881) on the job's own `m_gate` (dump.cs:690278).
///   Native CalcOpenAtStart skips zone-source gates, and the security-door sync
///   component starts closed. FactoryDone therefore initializes the addressed security door after all native
///   lock and traversal jobs, through its own sync state and recall callback. The gate is a zone's entrance when the
///   zone on either side of it (`LG_ZoneExpander.m_linksFrom`/`m_linksTo`, dump.cs:685540-685541, `LG_Area.m_zone`,
///   dump.cs:684361) names it as its `m_sourceGate` (dump.cs:698078).
/// - A weak door's lock: a prefix on `LG_WeakDoor.SetupWeakLock(eWeakLockType)` (dump.cs:688963) replaces the
///   type argument with the authored one when both sides of the door's own `Gate` (dump.cs:688907) are the
///   authored zone.
///
/// Runtime installs the declared callbacks once; without authored rows they leave native behavior unchanged.
/// </summary>
internal static class DoorBuildApplier
{
    private static DoorBuildPlan? _plan;

    /// <summary>Zones whose declared weak lock reached a weak door of that zone during this generation.</summary>
    private static readonly HashSet<MapZoneKey> _weakLocksMatched = new();

    internal static void Activate(DoorBuildPlan plan)
    {
        _plan = plan.IsEmpty ? null : plan;
        _weakLocksMatched.Clear();
    }

    /// <summary>The values of the level being generated, or null when no document names it. The identity is read
    /// at each call, because a job does not carry its level; the identity record is written once per level.</summary>
    private static DoorBuildLevel? Current() => _plan?.For(LevelIdentity.Read());

    internal static void BuiltGate(LG_BuildGateJob job)
    {
        try
        {
            if (Current() is not { } level) return;
            DoorOrientationObservation.Capture(job);
            var gate = job.m_gate;
            // Vanilla's NoLock roll skips SetupWeakLock completely. An explicit author value still owns this
            // door, so create its lock through the same native entry while the gate is being built.
            if (gate?.SpawnedDoor?.TryCast<LG_WeakDoor>() is { } weak
                && (weak.WeakLocks == null || weak.WeakLocks.Length == 0)
                && WeakLockFor(weak, record: false) is { } weakType
                && weakType is eWeakLockType.Melee or eWeakLockType.Hackable)
            {
                weak.SetupWeakLock(weakType);
                if (weak.WeakLocks == null || weak.WeakLocks.Length == 0)
                    throw new InvalidOperationException("The authored weak door lock was not created by its native setup.");
            }
            if (gate == null || gate.WasCollected || EnteredZone(gate) is not { } zone
                || ZoneIndex.Coordinates(zone) is not { } at
                || !level.Gates.TryGetValue(new MapZoneKey(at.Dimension, at.Layer, at.Zone), out var values)) return;
            // A successful native build consumed the flip. Initial status is reported only after its recall below.
            if (values.ForceOpenFromStart is not true && MapLog.Logs(RuntimeLogLevel.Info))
                MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FieldApplied, "block=" + MapFieldSchema.GateBlock + " target=" + Door(at)
                    + " ForceOpenFromStart=" + Spell(values.ForceOpenFromStart) + " ForceSecurityFlip=" + Spell(values.ForceSecurityFlip),
                    RuntimeLogValue.Of("block", MapFieldSchema.GateBlock));
        }
        catch (Exception error) { Fault("gate-built", error); }
    }

    internal static void ApplyGate(LG_BuildGateJob job)
    {
        try
        {
            var level = Current();
            if (level == null || level.Gates.Count == 0 || job == null) return;
            var gate = job.m_gate;
            if (gate == null || gate.WasCollected) return;
            var zone = EnteredZone(gate);
            if (zone == null || ZoneIndex.Coordinates(zone) is not { } at) return;
            if (!level.Gates.TryGetValue(new MapZoneKey(at.Dimension, at.Layer, at.Zone), out var values)) return;
            if (values.ForceSecurityFlip is bool flip) gate.ForceSecurityFlip = flip;
            // Only true forces the initial open state after native lock/traversal initialization.
            // False/unset withdraws that override and leaves the native opening decision intact.
        }
        catch (Exception error) { Fault("gate", error); }
    }

    internal static void Finish()
    {
        var level = Current();
        if (level == null) { DoorOrientationObservation.Clear(); return; }
        // A weak lock names a zone's own weak door; a declaration no weak door of that zone ever took is an authored value the
        // level cannot honour, so it is reported instead of dropped.
        foreach (var key in level.WeakLocks.Keys)
            if (!_weakLocksMatched.Contains(key))
                MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.WeakLockUnmatched, "block=" + MapFieldSchema.WeakLockBlock + " target="
                    + ZoneTarget(key) + " reason=no-weak-door-in-zone",
                    RuntimeLogValue.Of("block", MapFieldSchema.WeakLockBlock));
        _weakLocksMatched.Clear();
        var index = ZoneIndex.Current(null);
        foreach (var entry in level.Gates)
        {
            if (entry.Value.ForceOpenFromStart is not true) continue;
            var at = new ZoneCoordinates(entry.Key.Dimension, entry.Key.Layer, entry.Key.Zone);
            var gate = index.ZoneAt(at)?.m_sourceGate;
            if (gate == null)
            {
                Fault("initial-state " + Door(at), new InvalidOperationException("The authored entrance door was not generated."));
                continue;
            }
            ApplyInitialState(gate, at);
        }
        DoorOrientationObservation.Finish();
    }

    /// <summary>Every peer initializes the same state before gameplay. SetStateUnsynced alone only writes the
    /// replicator; the native recall callback also applies the open pose, locks, collider and traversal state.
    /// This is initial state, not a gameplay force-open request or a scheduled authored behavior.</summary>
    internal static void ApplyInitialState(LG_Gate gate, ZoneCoordinates at)
    {
        try
        {
            if (Current() is not { } level
                || !level.Gates.TryGetValue(new MapZoneKey(at.Dimension, at.Layer, at.Zone), out var values)
                || values.ForceOpenFromStart is not true) return;
            var zone = EnteredZone(gate);
            if (zone == null || ZoneIndex.Coordinates(zone) != at)
                throw new InvalidOperationException("The gate does not identify the authored entrance.");
            var door = gate.SpawnedDoor?.TryCast<LG_SecurityDoor>()
                ?? throw new InvalidOperationException("The entrance gate has no generated security door.");
            var sync = door.m_sync ?? throw new InvalidOperationException("The entrance door has no initialized sync component.");
            var state = sync.GetCurrentSyncState();
            state.status = eDoorStatus.Open;
            sync.SetStateUnsynced(state);
            door.OnSyncDoorStatusChange(state, true);
            if (door.LastStatus != eDoorStatus.Open || sync.GetCurrentSyncState().status != eDoorStatus.Open)
                throw new InvalidOperationException("The entrance door did not accept its authored initial state.");
            if (MapLog.Logs(RuntimeLogLevel.Info))
                MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FieldApplied, "block=" + MapFieldSchema.GateBlock + " target=" + Door(at)
                    + " ForceOpenFromStart=true ForceSecurityFlip=" + Spell(values.ForceSecurityFlip) + " stage=initial-state status=Open",
                    RuntimeLogValue.Of("block", MapFieldSchema.GateBlock));
        }
        catch (Exception error) { Fault("initial-state " + Door(at), error); }
    }

    /// <summary>The authored lock type for this weak door, or null to leave the game's own roll.</summary>
    internal static eWeakLockType? WeakLockFor(LG_WeakDoor door, bool record = true)
    {
        try
        {
            var level = Current();
            if (level == null || level.WeakLocks.Count == 0 || door == null) return null;
            var gate = door.Gate;
            if (gate == null || gate.WasCollected) return null;
            var from = gate.m_linksFrom?.m_zone;
            var to = gate.m_linksTo?.m_zone;
            // A weak door stands inside one zone; a door whose two sides are different zones, or whose sides do not
            // read, belongs to no single zone an entry could name.
            if (from == null || to == null || from.Pointer != to.Pointer || ZoneIndex.Coordinates(from) is not { } at) return null;
            var zoneKey = new MapZoneKey(at.Dimension, at.Layer, at.Zone);
            if (!level.WeakLocks.TryGetValue(zoneKey, out byte type)) return null;
            _weakLocksMatched.Add(zoneKey);
            if (record && MapLog.Logs(RuntimeLogLevel.Info))
                MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FieldApplied, "block=" + MapFieldSchema.WeakLockBlock + " target=gtfo.zone:"
                    + at.Dimension.ToString(CultureInfo.InvariantCulture) + ":" + at.Layer.ToString(CultureInfo.InvariantCulture) + ":"
                    + at.Zone.ToString(CultureInfo.InvariantCulture) + " eWeakLockType=" + type.ToString(CultureInfo.InvariantCulture),
                    RuntimeLogValue.Of("block", MapFieldSchema.WeakLockBlock));
            return (eWeakLockType)type;
        }
        catch (Exception error) { Fault("weak-lock", error); return null; }
    }

    /// <summary>The zone this gate is the entrance of: the zone on either side that names it as its source gate.
    /// A gate no zone was entered through is no zone's entrance door and carries no authored value.</summary>
    private static LG_Zone? EnteredZone(LG_Gate gate)
    {
        var to = gate.m_linksTo?.m_zone;
        if (to != null && to.m_sourceGate is { } toSource && toSource.Pointer == gate.Pointer) return to;
        var from = gate.m_linksFrom?.m_zone;
        if (from != null && from.m_sourceGate is { } fromSource && fromSource.Pointer == gate.Pointer) return from;
        return null;
    }

    /// <summary>A failure inside generation is reported and leaves the game's own value; it never throws
    /// into the level builder.</summary>
    private static void Fault(string part, Exception error)
    {
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyFailed, "group=door-build part=" + part + " error="
            + error.GetType().Name + ": " + error.Message,
            RuntimeLogValue.Of("group", "door-build"), RuntimeLogValue.Of("exception", error.GetType().Name));
    }

    private static string ZoneTarget(MapZoneKey key) => "gtfo.zone:" + key.Dimension.ToString(CultureInfo.InvariantCulture) + ":"
        + key.Layer.ToString(CultureInfo.InvariantCulture) + ":" + key.Zone.ToString(CultureInfo.InvariantCulture);

    private static string Door(ZoneCoordinates at)
        => MapObjectDoorAddress.Create(at.Dimension, at.Layer, at.Zone)!.ToString();

    private static string Spell(bool? value) => value is bool set ? (set ? "true" : "false") : "unset";
}


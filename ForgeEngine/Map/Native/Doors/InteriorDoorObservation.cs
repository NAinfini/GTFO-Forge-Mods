using ForgeCore;
using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>One-time generation receipts for real internal gates. Reads and writes subsequently use the exact
/// native door; no random serial, nearest door or other room is ever substituted.</summary>
internal sealed class InteriorDoorObservation : IDisposable
{
    internal static InteriorDoorObservation? Current { get; private set; }
    private sealed record Receipt(InteriorDoorRow Row, LG_Geomorph Room, LG_InternalGate Gate, LG_WeakDoor Door,
        IntPtr From, IntPtr To, int RoomInstanceId, int GateInstanceId, int DoorInstanceId);
    private readonly IForgeEngine _kernel;
    private readonly Func<MapLevelReference?> _level;
    private readonly Dictionary<MapObjectReference, Receipt> _doors = new();
    private readonly Dictionary<IntPtr, MapObjectReference> _addresses = new();
    private readonly OnceReporter _once = new();
    private readonly IDisposable _started, _finished;
    private IReadOnlyList<InteriorDoorRow> _rows = Array.Empty<InteriorDoorRow>();
    private MapLevelReference? _readyLevel;
    private long _world = long.MinValue;
    private bool _ready, _disposed;

    internal InteriorDoorObservation(IForgeEngine kernel, Func<MapLevelReference?> level)
    {
        _kernel = kernel; _level = level;
        _started = ForgeCore.FactoryEvents.OnBuildStart(Reset);
        _finished = ForgeCore.FactoryEvents.OnBuildDone(() => { _ready = true; _readyLevel = _level(); Frame(); });
        Current = this;
    }
    internal void Admit(IReadOnlyList<InteriorDoorRow> rows) { _rows = rows; Reset(); }
    /// <summary>A host migration keeps the level and its bound doors: the binding moves to the new world number.</summary>
    internal void Follow(long from, long to) { if (_world == from) _world = to; }
    private void Reset()
    {
        _doors.Clear(); _addresses.Clear(); _once.Reset(); _world = long.MinValue;
        _ready = false; _readyLevel = null;
    }
    internal void Frame()
    {
        if (_disposed || _rows.Count == 0 || _kernel.WorldEpoch == 0) return;
        // A new world number drops the bindings. Without a new build (a client's number arriving after its factory
        // callback) the completed-build fallback below binds the same doors again once the level is in play.
        if (_world != long.MinValue && _world != _kernel.WorldEpoch) Reset();
        if (_world == _kernel.WorldEpoch) return;
        var level = _level();
        // Client world identity can arrive after its factory callback. Bind on the existing Map frame once,
        // only when the native completion flag and InLevel agree; never search an unfinished generation.
        if (!_ready && LG_Factory.BuildDone && GameStateManager.CurrentStateName == eGameStateName.InLevel)
        { _ready = true; _readyLevel = level; }
        if (!_ready || level == null || level != _readyLevel || !LG_Factory.BuildDone) return;
        _world = _kernel.WorldEpoch; _doors.Clear(); _addresses.Clear();
        var rejected = new HashSet<MapObjectReference>();
        var rejectedInstances = new HashSet<IntPtr>();
        foreach (var row in _rows)
        {
            if (row.Level != level) continue;
            Receipt? receipt; string reason;
            try { receipt = Bind(row, out reason); }
            catch (Exception error) { Refuse(row.Address, row.Id, "interior-door-native-read-failed: " + error.GetType().Name); continue; }
            if (receipt == null) { Refuse(row.Address, row.Id, reason); continue; }
            if (rejected.Contains(row.Address) || rejectedInstances.Contains(receipt.Door.Pointer))
            { rejected.Add(row.Address); Refuse(row.Address, row.Id, "interior-door-identity-ambiguous"); continue; }
            MapObjectReference? conflict = _doors.ContainsKey(row.Address) ? row.Address
                : _addresses.TryGetValue(receipt.Door.Pointer, out var prior) ? prior : null;
            if (conflict != null)
            {
                var previous = _doors[conflict];
                _doors.Remove(conflict); _addresses.Remove(previous.Door.Pointer); rejected.Add(conflict); rejected.Add(row.Address);
                rejectedInstances.Add(previous.Door.Pointer); rejectedInstances.Add(receipt.Door.Pointer);
                Refuse(conflict, previous.Row.Id, "interior-door-instance-ambiguous");
                Refuse(row.Address, row.Id, "interior-door-instance-ambiguous"); continue;
            }
            _doors.Add(row.Address, receipt); _addresses.Add(receipt.Door.Pointer, row.Address);
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.InteriorDoorBound, "Interior door resolved from native generation.",
                RuntimeLogValue.Of("author", row.Id), RuntimeLogValue.Of("room", row.RoomInstance),
                RuntimeLogValue.Of("door", row.DoorId), RuntimeLogValue.Of("address", row.Address.ToString()),
                RuntimeLogValue.Of("instance", receipt.Door.GetInstanceID()));
        }
    }
    private Receipt? Bind(InteriorDoorRow row, out string reason)
    {
        reason = "interior-door-room-missing";
        if (!RoomResolver.TryPlace(_world, row.Room, out var hit, out var refusal))
        { reason = refusal ?? reason; return null; }
        var scope = new ZoneCoordinates(row.Room.Dimension, row.Room.Layer, row.Room.LocalIndex);
        var zone = ZoneIndex.Current(_world).ZoneAt(scope);
        if (zone == null || zone.m_areas == null) return null;
        LG_Geomorph? room = null;
        foreach (var area in zone.m_areas)
        {
            var candidate = area?.m_geomorph;
            if (candidate == null || candidate.WasCollected || candidate.GetInstanceID() != hit!.GeomorphInstanceId) continue;
            if (room != null && room.Pointer != candidate.Pointer) { reason = "interior-door-room-ambiguous"; return null; }
            room = candidate;
        }
        if (room == null) return null;
        var pose = row.Doorway;
        var point = room.transform.position + room.transform.rotation * new Vector3((float)pose.X, (float)pose.Y, (float)pose.Z);
        var facing = room.transform.rotation * new Vector3((float)pose.ForwardX, (float)pose.ForwardY, (float)pose.ForwardZ);
        LG_InternalGate? match = null;
        foreach (var gate in Gates(room))
        {
            if (gate.WasCollected) continue;
            // Authored gates already carry the constructor's exact definition receipt. Compare the
            // exported source pose with that definition, not with the built scene object's transform.
            var recipe = RoomRecipeDoorIdentity.Resolve(gate);
            if (recipe != null)
            {
                if (recipe.Value.Recipe.ResourceKey != row.Room.SourcePrefab || recipe.Value.Door.Name != "Interior " + row.DoorId) continue;
                var door = recipe.Value.Door;
                if (!Near(new(door.Position[0], door.Position[1], door.Position[2]), new((float)pose.X, (float)pose.Y, (float)pose.Z), .01f)
                    // The author's door may face either way across its line; the built door's forward leads from its first area to its second.
                    || !Near(new(door.Forward[0], door.Forward[1], door.Forward[2]), new((float)pose.ForwardX, (float)pose.ForwardY, (float)pose.ForwardZ), .001f)
                        && !Near(new(-door.Forward[0], -door.Forward[1], -door.Forward[2]), new((float)pose.ForwardX, (float)pose.ForwardY, (float)pose.ForwardZ), .001f))
                { reason = "interior-door-definition-pose-mismatch"; return null; }
            }
            else
            {
                if (row.Room.SourcePrefab.StartsWith("Assets/Forge/Rooms/", StringComparison.Ordinal)) continue;
                if (!Near(gate.transform.position, point, .01f) || !Near(gate.transform.forward, facing, .001f)) continue;
            }
            if (match != null) { reason = "interior-door-gate-ambiguous"; return null; }
            match = gate;
        }
        if (match == null) { reason = "interior-door-gate-missing"; return null; }
        var from = match.m_linksFrom; var to = match.m_linksTo;
        if (from == null || to == null || from.m_geomorph?.Pointer != room.Pointer
            || to.m_geomorph?.Pointer != room.Pointer || from.m_zone?.Pointer != zone.Pointer || to.m_zone?.Pointer != zone.Pointer)
        { reason = "interior-door-area-mismatch"; return null; }
        var weak = match.SpawnedDoor?.TryCast<LG_WeakDoor>();
        if (weak == null || weak.WasCollected || weak.Gate?.Pointer != match.Pointer)
        { reason = "interior-door-native-instance-missing"; return null; }
        return new(row, room, match, weak, from.Pointer, to.Pointer, room.GetInstanceID(), match.GetInstanceID(), weak.GetInstanceID());
    }
    private static IEnumerable<LG_InternalGate> Gates(LG_Geomorph room)
    {
        var seen = new HashSet<IntPtr>();
        foreach (var area in room.m_areas)
            if (area?.m_gates != null)
                foreach (var entry in area.m_gates)
                    if (entry?.TryCast<LG_InternalGate>() is { } gate && gate.TryCast<LG_PlugDoorAlign>() == null && seen.Add(gate.Pointer))
                        yield return gate;
    }
    private static bool Near(Vector3 value, Vector3 expected, float tolerance)
        => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z)
            && Math.Abs(value.x - expected.x) <= tolerance && Math.Abs(value.y - expected.y) <= tolerance && Math.Abs(value.z - expected.z) <= tolerance;
    internal LG_WeakDoor? ByAddress(MapObjectReference address)
    {
        if (InteriorDoorIdentity.ParseKey(address.Key) == null) return null;
        if (!_doors.TryGetValue(address, out var receipt)) { Refuse(address, "", "interior-door-unbound"); return null; }
        // Unity's `==` sees a destroyed object (a binding made just before the level was torn down): the binding is dropped
        // and the completed-build fallback of the next frame binds again, instead of reading through the dead door.
        if (receipt.Room == null || receipt.Gate == null || receipt.Door == null)
        {
            _doors.Remove(address); _addresses.Remove(receipt.Door!.Pointer);
            _world = long.MinValue; _ready = false; _readyLevel = null;
            Refuse(address, receipt.Row.Id, "interior-door-instance-stale"); return null;
        }
        var zone = ZoneIndex.Current(_kernel.WorldEpoch).ZoneAt(new ZoneCoordinates(receipt.Row.Room.Dimension, receipt.Row.Room.Layer, receipt.Row.Room.LocalIndex));
        if (_world != _kernel.WorldEpoch || _level() != receipt.Row.Level || zone == null
            || receipt.Room.WasCollected || receipt.Gate.WasCollected || receipt.Door.WasCollected
            || receipt.Room.GetInstanceID() != receipt.RoomInstanceId || receipt.Gate.GetInstanceID() != receipt.GateInstanceId
            || receipt.Door.GetInstanceID() != receipt.DoorInstanceId
            || receipt.Door.gameObject == null || receipt.Door.Gate?.Pointer != receipt.Gate.Pointer
            || receipt.Gate.SpawnedDoor?.TryCast<LG_WeakDoor>()?.Pointer != receipt.Door.Pointer
            || receipt.Gate.m_linksFrom?.Pointer != receipt.From || receipt.Gate.m_linksTo?.Pointer != receipt.To
            || receipt.Gate.m_linksFrom?.m_geomorph?.Pointer != receipt.Room.Pointer || receipt.Gate.m_linksTo?.m_geomorph?.Pointer != receipt.Room.Pointer
            || receipt.Gate.m_linksFrom?.m_zone?.Pointer != zone.Pointer || receipt.Gate.m_linksTo?.m_zone?.Pointer != zone.Pointer)
        { Refuse(address, receipt.Row.Id, "interior-door-instance-stale"); return null; }
        return receipt.Door;
    }
    internal MapObjectReference? Address(LG_WeakDoor door)
        => door != null && !door.WasCollected && _addresses.TryGetValue(door.Pointer, out var address)
            && ByAddress(address)?.Pointer == door.Pointer ? address : null;
    private void Refuse(MapObjectReference address, string author, string reason)
    {
        if (!_once.ShouldReport(address + "/" + reason)) return;
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.WeakDoorRefused, "Interior door could not be bound to its native instance.",
            RuntimeLogValue.Of("author", author), RuntimeLogValue.Of("address", address.ToString()), RuntimeLogValue.Of("reason", reason));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _started.Dispose();
        _finished.Dispose();
        Reset();
        if (Current == this) Current = null;
    }
}

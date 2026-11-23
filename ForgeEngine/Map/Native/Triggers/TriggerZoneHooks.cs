using ForgeCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of the trigger zones: the package document read once at start, the zone table the map-object
/// half addresses, the blocking bodies, and the one tick that drives the judging.
///
/// Nothing here registers an entity namespace or a resource kind of its own. A zone is one category of the map
/// object's own namespace, so this half hands its table to the map-object module — which is what makes a plan hang
/// on a zone through the existing `map-object` mount — and the facts themselves are published by that module.
///
/// A document's pose is room-local, so the table handed over is the placed one: the level this world is running
/// places the zones it owns through the room resolver the host composed, once per level, when the level is known —
/// which is after the level's own generation, because that is when a room's world pose exists at all. A zone whose
/// room the resolver cannot name is refused by name and left out of the table, so no plan is dispatched for a
/// volume that would have been judged in coordinates nobody converted. The authored table itself is never
/// mutated: placement is a fold, and the next level places its own zones from the same document.
///
/// The tick is driven by the kernel's own clock, once per advance, rather than by the local player's fixed update:
/// the runtime advances the kernel from the same Unity step, and a player's fixed update exists once per player on
/// the machine. The judgment itself is the host's — <see cref="TriggerZoneModule.Tick"/> is guarded before any
/// world read — while the blocking bodies are every machine's own local physics.
/// </summary>
internal sealed partial class TriggerZoneSession : IDisposable
{
    private readonly IForgeEngine _kernel;
    private readonly MapObjectModule _mapObjects;
    private readonly Func<bool> _authority;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private readonly TriggerZoneSource _source;
    private readonly TriggerZoneModule _module;
    private readonly TriggerZoneColliders _colliders;
    private readonly TriggerZoneVisuals _visuals;
    private readonly RoomLookup? _rooms;
    private readonly Func<MapLevelReference?> _level;
    private readonly Func<bool> _subscribed;
    private readonly Action<string> _report;
    private readonly OnceReporter _once;
    // Authored order, but only for zones with work that can change between frames: route/resize motion,
    // a billboard label, or a visible sensor pulse. Region judgment keeps its complete 0.1 s table in _module.
    private readonly List<int> _frameWork = new();
    private bool[] _motionWork = Array.Empty<bool>();
    private IReadOnlyList<TriggerZone> _authored = Array.Empty<TriggerZone>();
    private MapLevelReference? _placedLevel;
    private long _placedEpoch = -1;
    internal UnityEngine.BoxCollider? InspectCollider(string id, long epoch)
        => !_disposed && _placedEpoch == epoch && _kernel.WorldEpoch == epoch ? _colliders.InspectCollider(id) : null;
    private IDisposable? _buildStarted, _buildFinished;
    private bool _roomsReady;
    // The level whose factory finished. The level identity moves on before the next world and factory begin, so a
    // ready flag left over from the previous level never judges the next level's zones against rooms not built yet.
    private MapLevelReference? _readyLevel;
    private string _placementPhase = "world-ready";
    private const string PlacedCode = "map.trigger-zones-placed";
    private bool _disposed;

    private TriggerZoneSession(IForgeEngine kernel, MapObjectModule mapObjects, Func<bool> authority,
        TriggerZoneSource source, TriggerZoneModule module, TriggerZoneColliders colliders,
        RoomLookup? rooms, Func<MapLevelReference?> level, Func<bool> subscribed, Action<string> report)
    {
        _kernel = kernel; _mapObjects = mapObjects; _authority = authority;
        _source = source;
        _module = module;
        _colliders = colliders;
        _visuals = new TriggerZoneVisuals(report);
        _rooms = rooms;
        _level = level;
        _subscribed = subscribed;
        _report = report; _once = new OnceReporter(report);
        RegisterCheckpointState();
        MapNet.TriggerZones.Changed += RowChanged;
        _lifecycle = mapObjects.Registration.ObserveLifecycle(value =>
        {
            // A host migration keeps the level and its placed zones: only the world number they are stamped with moves.
            // The host table keeps its rows too, and the restore that follows has the new host write its zones again.
            if (value.IsMigration)
            {
                if (_placedEpoch == value.PreviousWorldEpoch)
                {
                    _placedEpoch = value.Current.WorldEpoch;
                    _controller?.FollowWorld(value.Current.WorldEpoch);
                }
                return;
            }
            if (value.Kind is RuntimeLifecycleKind.WorldChanged or RuntimeLifecycleKind.CheckpointRestored)
            {
                _roomsReady = false;
                if (value.Kind == RuntimeLifecycleKind.WorldChanged) _restored.Clear();
                _placementPhase = value.Kind == RuntimeLifecycleKind.CheckpointRestored ? "checkpoint-restored" : "world-ready";
                ClearPlacement();
            }
        });
    }

    /// <summary>The in-level frame's placement step, once per kernel tick (<c>MapPluginSession.InLevelFrame</c>).
    /// The bridge advances only in native InLevel, after LevelAPI.OnEnterLevel. This also handles a client's epoch
    /// arriving after its own factory completed, and recall rebuilding objects inside the same world. It is not a
    /// lifecycle callback, because placing a world is a native side effect.</summary>
    internal void Frame()
    {
        if (_disposed || _placedEpoch == _kernel.WorldEpoch) return;
        if (!_roomsReady && LG_Factory.BuildDone && GameStateManager.CurrentStateName == eGameStateName.InLevel)
        { _roomsReady = true; _readyLevel = _level(); ZoneIndex.Reset(); }
        Refresh();
    }

    private void ClearPlacement()
    {
        _placedLevel = null; _placedEpoch = -1; _controller = null;
        ResetFrameWork(); _once.Reset();
        _visuals.Sync(Array.Empty<TriggerZone>(), null, _kernel.WorldEpoch);
        _colliders.Sync(Array.Empty<TriggerZone>(), null, _kernel.WorldEpoch);
        _source.Load(_authored);
    }

    private void GenerationStarted()
    {
        if (_disposed) return;
        _roomsReady = false;
        _placementPhase = "factory-done";
        ClearPlacement();
    }

    private void GenerationFinished()
    {
        if (_disposed) return;
        _roomsReady = true; _readyLevel = _level();
        _placementPhase = "factory-done";
        // Other generation readers may have cached only a partially populated level.
        var measure = MapLog.Logs(RuntimeLogLevel.Info);
        var startedAt = measure ? Stopwatch.GetTimestamp() : 0;
        var completed = false;
        ZoneIndex.Reset();
        try { Refresh(); completed = true; }
        catch (Exception error) { _report("trigger-zone-placement: generation-complete placement failed." + Environment.NewLine + error); }
        finally
        {
            if (measure) MapLog.Record(RuntimeLogLevel.Info, "level.load-work", "Trigger zone resolution, state and native bodies at factory completion.",
                RuntimeLogValue.Of("work", "trigger-zone-placement"), RuntimeLogValue.Of("phase", "factory-done"),
                RuntimeLogValue.Of("elapsedMs", (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency),
                RuntimeLogValue.Of("completed", completed));
        }
    }

    /// <summary>Reads this install's zone documents, hands the table to the map-object module this session's
    /// provider is composed with — the module must receive it before it registers, because every category is
    /// declared on the one definition at registration time — and builds the bodies of this world's blocking zones.
    /// A document that does not read is reported and left out; it never stops the package from loading.</summary>
    /// <param name="rooms">The one resolver that can name an authored room in the level the game generated, or
    /// null when this build composed none. Every zone of the running level is then refused by name rather than
    /// judged at a pose nobody converted.</param>
    internal static TriggerZoneSession Start(IForgeEngine kernel, MapObjectModule mapObjects, Func<bool> authority,
        Func<MapLevelReference?> level, RoomLookup? rooms, Action<string> report,
        IReadOnlyList<TriggerZone>? prepared = null)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(mapObjects);
        ArgumentNullException.ThrowIfNull(authority); ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(report);
        var source = mapObjects.Zones;
        var module = new TriggerZoneModule(kernel, mapObjects.Registration, mapObjects, authority, level, report);
        // A zone nobody listens to is not work: the two facts this half publishes and the members read are the
        // only reasons to judge a volume, so the clock is taken only while one of those bindings has a subscriber.
        var subscribed = () => mapObjects.TriggerZonesObserved;
        var session = new TriggerZoneSession(kernel, mapObjects, authority, source, module, new TriggerZoneColliders(report), rooms, level, subscribed, report);
        try
        {
            session.AdmitRows(prepared ?? TriggerZoneData.Load(report));
            session._buildStarted = ForgeCore.FactoryEvents.OnBuildStart(session.GenerationStarted);
            session._buildFinished = ForgeCore.FactoryEvents.OnBuildDone(session.GenerationFinished);
            session.Place();
            session.SyncBodies();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Publishes already prepared rows into the existing session. Native placement remains in the
    /// normal level refresh; admitting content neither registers another owner nor builds world objects.</summary>
    internal void AdmitRows(IReadOnlyList<TriggerZone> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _authored = rows;
        _source.Load(rows);
        _placedLevel = null;
        _placedEpoch = -1;
        _controller = null;
        _once.Reset();
        ResetFrameWork();
    }

    /// <summary>The members the host's judgment holds inside one zone of the level it judged, or null when this
    /// machine is not judging that zone: the one read behind `forge.query.map.trigger_zone_members`.</summary>
    internal IReadOnlyCollection<EntityReference>? Members(string zoneId) => _disposed ? null : _module.Members(zoneId);

    /// <summary>How many zones this install's documents gave, for the plugin's one load record.</summary>
    internal int Authored => _authored.Count;

    /// <summary>Whether this half has work a clock tick would spend: a zone the running level placed and a
    /// subscriber on one of the two bindings it publishes or on the members read. A level that placed no zone, and a zone nobody
    /// subscribed to, both answer false — the first because there is nothing to judge, the second because the
    /// judgment would reach nobody.</summary>
    internal bool Pending => !_disposed && _source.Zones.Count > 0 && _subscribed();
    internal bool PresentationPending => !_disposed && _controller != null && _frameWork.Count != 0;
    private double Now => _kernel.CurrentTick < 0 ? 0 : _kernel.CurrentTick / _kernel.TicksPerSecond;

    private bool NeedsFrameWork(int index)
    {
        var zone = _controller!.Zone(index);
        return zone.Placed && zone.Level == _placedLevel && (_controller.Motion(index).Active || zone.LabelVisible
            || zone.Visible && zone.Appearance is { IsSensor: true } || _visuals.Easing(index));
    }

    private void ResetFrameWork()
    {
        _frameWork.Clear();
        _motionWork = Array.Empty<bool>();
        _advancedTick = long.MinValue;
    }

    // The kernel tick Advance last ran on. The presentation frame and the 0.1 s clock tick both ask for it inside one
    // kernel tick; `Now` is the same for both, so the second pass would only repeat the first.
    private long _advancedTick = long.MinValue;

    private void SeedFrameWork()
    {
        ResetFrameWork();
        if (_controller == null) return;
        _motionWork = new bool[_controller.Count];
        if (_frameWork.Capacity < _controller.Count) _frameWork.Capacity = _controller.Count;
        for (var i = 0; i < _controller.Count; i++)
            if (NeedsFrameWork(i)) _frameWork.Add(i);
    }

    /// <summary>A command or a row of the host table is the only live mutation boundary. Apply its native pose once,
    /// then add or remove that zone from the existing clock's dense frame set. The list remains in authored order,
    /// so simultaneous completion and replication retain their prior order.</summary>
    private void StateChanged(int index)
    {
        if (_controller == null || index < 0 || index >= _controller.Count) return;
        var now = Now;
        _visuals.Tick(index, now);
        _colliders.Tick(index);
        var slot = _frameWork.BinarySearch(index);
        var needed = NeedsFrameWork(index);
        if (needed && slot < 0) _frameWork.Insert(~slot, index);
        else if (!needed && slot >= 0) _frameWork.RemoveAt(slot);
    }

    internal void Advance()
    {
        if (_disposed || _controller == null || _controller.Count == 0) return;
        var tick = _kernel.CurrentTick;
        if (tick == _advancedTick) return;
        _advancedTick = tick;
        var now = Now;
        // Preserve the old phase order: every active motion advances before presentation and collision read it.
        for (var slot = 0; slot < _frameWork.Count; slot++)
        {
            var i = _frameWork[slot];
            var zone = _controller.Zone(i);
            var motion = _controller.Motion(i);
            _motionWork[i] = motion.Active;
            if (_motionWork[i] && motion.Advance(now) && _authority())
            {
                motion.Changed(); Publish(i);
                Record(Blocks.ActionMapTriggerZoneMovement.Id, EntityKinds.MapObject + ":" + TriggerZoneAddress.Of(zone.Id), "stop", ResultCode.Completed);
            }
        }
        for (var slot = 0; slot < _frameWork.Count; slot++) _visuals.Tick(_frameWork[slot], now);
        for (var slot = 0; slot < _frameWork.Count;)
        {
            var index = _frameWork[slot];
            // Static labels and sensor pulses do not rewrite an unchanged collider. A motion that completed in the
            // first phase still updates its final pose before leaving the set.
            if (_motionWork[index]) { _colliders.Tick(index); _motionWork[index] = false; }
            if (NeedsFrameWork(index)) slot++;
            else _frameWork.RemoveAt(slot);
        }
    }

    /// <summary>One tick of this machine: the level's zones are placed, the host judges them, and every machine's
    /// blocking bodies follow. The kernel's clock calls it at the cadence of the game's own collision trigger
    /// (`LG_CollisionWorldEventTrigger.COLLISION_CHECK_INTERVAL`, see <see cref="MapClock"/>), so a tick that
    /// arrives less than a cadence after the last one does not exist at all.</summary>
    internal void Tick()
    {
        if (_disposed) return;
        Place();
        Advance();
        _module.Tick();
        SyncBodies();
    }

    /// <summary>The level-facing half of the work, run when a level or its zones may have changed rather than on a
    /// clock tick: this is where a new level's room-local poses become world poses, and it is a native side effect
    /// that belongs to a native stage.</summary>
    internal void Refresh()
    {
        if (_disposed) return;
        Place();
        SyncBodies();
    }

    /// <summary>Places the zones of the level this world is running, once per level: the room resolver answers each
    /// authored room's world pose, and a zone it cannot name is refused by name and left out of the judging table.
    /// Native factory completion or the existing ready tick must establish this generation's room lifetime first.
    /// Checkpoint recall invalidates the placement even though its world epoch stays the same.</summary>
    private void Place()
    {
        if (_disposed || !_roomsReady) return;
        // Ready belongs to the level whose factory finished; another level's rooms do not exist yet.
        if (_readyLevel is { } built && _level() is { } current && built != current) return;
        if (_level() is not { } level)
        { _placedLevel = null; _controller = null; return; }
        if (_placedLevel == level && _placedEpoch == _kernel.WorldEpoch) return;
        var placed = TriggerZonePlacement.Of(_authored, level, _rooms, ReportOnce);
        _source.Load(placed);
        _controller = new TriggerZoneController(_source.Zones, _kernel.WorldEpoch, _source.Zones.Count == 0 ? 0 : Now, _authority());
        _placedLevel = level;
        ApplyRestored(); _restored.Clear();
        Index(); SeedFrameWork(); SyncNetwork(); Advance();
        _placedEpoch = _kernel.WorldEpoch;
        if (MapLog.Logs(RuntimeLogLevel.Info))
        {
            var authoredCount = 0; var placedCount = 0;
            foreach (var zone in _authored) if (zone.Level == level) authoredCount++;
            foreach (var zone in placed) if (zone.Level == level && zone.Placed) placedCount++;
            MapLog.Record(RuntimeLogLevel.Info, PlacedCode, "Trigger zones resolved after native world readiness.",
                RuntimeLogValue.Of("level", level.ToString()), RuntimeLogValue.Of("phase", _placementPhase),
                RuntimeLogValue.Of("authored", authoredCount), RuntimeLogValue.Of("placed", placedCount),
                RuntimeLogValue.Of("refused", authoredCount - placedCount));
        }
    }

    /// <summary>The blocking bodies and the drawn ranges of the level this world is running. Every machine keeps them
    /// in step with the level it is really in, so a body or an outline of a level that ended is never left standing
    /// in the next one.</summary>
    private void SyncBodies()
    {
        var level = _level();
        _colliders.Sync(_source.Zones, level, _kernel.WorldEpoch);
        _visuals.Sync(_source.Zones, level, _kernel.WorldEpoch);
    }

    /// <summary>One refusal, once. Placement runs again for every level, so a document with a room no level can
    /// name would otherwise repeat the same line on every tick.</summary>
    private void ReportOnce(string message)
    {
        _once.Report(message, message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buildStarted?.Dispose();
        _buildFinished?.Dispose();
        _colliders.Dispose();
        _visuals.Dispose();
        _lifecycle.Dispose();
        _checkpointState?.Dispose();
        MapNet.TriggerZones.Changed -= RowChanged;
        _controller = null; ResetFrameWork();
        _module.Dispose();
    }
}

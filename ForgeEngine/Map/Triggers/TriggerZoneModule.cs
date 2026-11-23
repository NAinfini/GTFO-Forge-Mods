using ForgeCore;
using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap;

internal readonly record struct TriggerZoneTickResult(string Status, string Code, int Zones, int Targets, int Entered, int Exited);

/// <summary>Host judgment over indexed zones and target slots. A target keeps one slot for as long as it is a
/// candidate, so a change of the candidate set moves only the targets that came or went, and one zone judges
/// only its members and the candidates its spatial index could not exclude. Stable judgment performs no
/// string-key or dictionary lookups and reuses its buffers; SDK observations and actual event publication
/// retain the SDK's own allocations.</summary>
internal sealed class TriggerZoneModule : IDisposable
{
    public const int MaximumZonesPerTick = TriggerZoneManifest.MaximumZones;
    public const double MaximumJudgedTravel = 3;
    public const int MaximumZones = TriggerZoneManifest.MaximumZones;

    private sealed class ZoneState
    {
        internal ZoneState(TriggerZone zone, int slots) { Zone = zone; Resize(slots); }
        internal readonly TriggerZone Zone;
        internal bool[] Inside = Array.Empty<bool>();
        internal long[] Entered = Array.Empty<long>(), Exited = Array.Empty<long>();
        // Members in arrival order, and the slot of each beside it.
        internal readonly List<EntityReference> Members = new();
        internal readonly List<int> MemberSlots = new();
        internal readonly Dictionary<string, (long Entered, long Exited)> History = new(StringComparer.Ordinal);
        internal readonly double[] PreviousCenter = new double[3];
        internal bool HasCenter;
        internal void Resize(int slots)
        { Array.Resize(ref Inside, slots); Array.Resize(ref Entered, slots); Array.Resize(ref Exited, slots); }
        internal void Leave(int slot)
        { Inside[slot] = false; var at = MemberSlots.IndexOf(slot); MemberSlots.RemoveAt(at); Members.RemoveAt(at); }
        internal void Clear()
        { Array.Clear(Inside, 0, Inside.Length); Members.Clear(); MemberSlots.Clear(); HasCenter = false; }
    }
    private readonly IForgeEngine _kernel;
    private readonly MapObjectModule _mapObjects;
    private readonly Func<bool> _authority;
    private readonly Func<MapLevelReference?> _level;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private readonly OnceReporter _once;
    private readonly List<EntityReference> _players = new(), _enemies = new(), _candidates = new(), _inspect = new();
    private readonly List<int> _inspectionSlots = new(), _judge = new(), _arrived = new();
    private readonly List<EntityPose> _poses = new();
    private IReadOnlyList<TriggerZone>? _source;
    private ZoneState[] _states = Array.Empty<ZoneState>();
    // Everything below is indexed by slot. `_order` lists the slots in the candidates' own order and `_rank` is
    // each slot's place in it, which is the order one zone's facts are published in.
    private readonly Dictionary<EntityReference, int> _slotOf = new();
    private readonly Stack<int> _free = new();
    private readonly Comparison<int> _byRank;
    private List<int> _order = new(), _before = new();
    private int _slots;
    private EntityReference[] _targets = Array.Empty<EntityReference>();
    private int[] _rank = Array.Empty<int>();
    private bool[] _player = Array.Empty<bool>(), _observed = Array.Empty<bool>(), _previousKnown = Array.Empty<bool>();
    private double[][] _positions = Array.Empty<double[]>(), _previous = Array.Empty<double[]>();
    private readonly double[] _relativeBefore = new double[3];
    private readonly TriggerZoneCandidates _spatial = new();
    private MapLevelReference? _judgedLevel;
    // Whether a tick of `_judgedLevel` completed: until one has, an empty table is not "nobody inside".
    private bool _judged;
    private bool _disposed;

    public TriggerZoneModule(IForgeEngine kernel, RuntimeModuleHandle registration, MapObjectModule mapObjects,
        Func<bool> authority, Func<MapLevelReference?> level, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel)); _mapObjects = mapObjects ?? throw new ArgumentNullException(nameof(mapObjects));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority)); _level = level ?? throw new ArgumentNullException(nameof(level));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report))); _lifecycle = registration.ObserveLifecycle(OnLifecycle);
        _byRank = (a, b) => _rank[a].CompareTo(_rank[b]);
    }
    public bool IsDisposed => _disposed;
    public IReadOnlyList<EntityReference> Inside(string zoneId)
        => Members(zoneId) is List<EntityReference> members ? members.ToArray() : Array.Empty<EntityReference>();
    public IReadOnlyCollection<EntityReference>? Members(string zoneId)
    {
        if (_disposed || !_authority() || !_judged || _judgedLevel is not { } level) return null;
        var zone = _mapObjects.Zones.ById(zoneId);
        if (zone == null || zone.Level != level || zone.RuntimeIndex < 0 || zone.RuntimeIndex >= _states.Length) return null;
        if (!ReferenceEquals(_states[zone.RuntimeIndex].Zone, zone)) return null;
        return _states[zone.RuntimeIndex].Members;
    }

    public TriggerZoneTickResult Tick()
    {
        if (_disposed || !_mapObjects.IsRegistered) return Idle("module-disposed");
        if (_kernel.StartupState != RuntimeStartupState.Ready) return Idle("runtime-not-ready");
        if (!_authority()) return Idle("not-authoritative");
        if (_level() is not { } current) { ClearMembership(); _judgedLevel = null; return Idle("level-unknown"); }
        if (_judgedLevel != current) { ClearMembership(); _judgedLevel = current; }
        var source = _mapObjects.Zones.Zones;
        if (!ReferenceEquals(source, _source))
        {
            _source = source; _states = new ZoneState[source.Count];
            for (var i = 0; i < source.Count; i++) _states[i] = new ZoneState(source[i], _targets.Length);
            ForgetTargets(); _players.Clear(); _enemies.Clear(); _judged = false;
        }
        var zoneCount = 0; var needPlayers = false; var needEnemies = false;
        for (var i = 0; i < _states.Length; i++)
        {
            var state = _states[i]; var zone = state.Zone;
            if (zone.Level != current || !zone.Placed) continue;
            if (!zone.Enabled) { state.Clear(); continue; }
            zoneCount++; needPlayers |= zone.Who != TriggerZoneWho.Enemy; needEnemies |= zone.Who != TriggerZoneWho.Player;
        }
        if (zoneCount > MaximumZonesPerTick)
        { ReportOnce("zones", "trigger-zone-tick-budget: the complete set was refused, not truncated."); return new("refused", "trigger-zone-tick-budget", zoneCount, 0, 0, 0); }
        if (zoneCount == 0) { _judged = true; return new("complete", "no-zones", 0, 0, 0, 0); }
        var playersKnown = needPlayers && Read(EntityKinds.Player, _players);
        var enemiesKnown = needEnemies && Read(EntityKinds.Enemy, _enemies);
        if (!needPlayers) _players.Clear(); if (!needEnemies) _enemies.Clear();
        _candidates.Clear(); _candidates.AddRange(_players); _candidates.AddRange(_enemies);
        SyncTargets();
        Array.Clear(_observed, 0, _observed.Length); _inspect.Clear(); _inspectionSlots.Clear();
        foreach (var slot in _order)
        {
            if (!(_player[slot] ? playersKnown : enemiesKnown)) continue;
            _inspect.Add(_targets[slot]); _inspectionSlots.Add(slot);
        }
        if (_inspect.Count > 0)
        {
            if (!_kernel.TryInspectEntityPoses(_inspect, _poses, out var code) || _poses.Count != _inspect.Count)
            { ReportOnce("read:" + code, "trigger-zone-observation: " + code + "; membership was preserved."); return new("refused", code, zoneCount, _order.Count, 0, 0); }
            // The poses follow the distinct request order. No address lookup is performed here.
            for (var i = 0; i < _poses.Count; i++)
            {
                var slot = _inspectionSlots[i];
                var pose = _poses[i];
                if (!pose.Observed)
                    return new("refused", "trigger-zone-observation-order", zoneCount, _order.Count, 0, 0);
                _positions[slot][0] = pose.X; _positions[slot][1] = pose.Y; _positions[slot][2] = pose.Z;
                _observed[slot] = true;
            }
        }
        _spatial.Prepare(_positions, _observed, _order);
        var entered = 0; var exited = 0;
        for (var index = 0; index < _states.Length; index++)
        {
            var state = _states[index]; var zone = state.Zone;
            if (zone.Level != current || !zone.Placed || !zone.Enabled) continue;
            // Judged: the zone's members and the others its index could not exclude, in the candidates' order.
            _judge.Clear(); _judge.AddRange(state.MemberSlots);
            var last = _spatial.Select(zone, state.HasCenter ? state.PreviousCenter : null, out var first);
            for (var i = first; i < last; i++) { var slot = _spatial.Slot(i); if (!state.Inside[slot]) _judge.Add(slot); }
            _judge.Sort(_byRank);
            foreach (var target in _judge)
            {
                if (!_observed[target] || (zone.Who == TriggerZoneWho.Player && !_player[target]) || (zone.Who == TriggerZoneWho.Enemy && _player[target])) continue;
                var reference = _targets[target]; var position = _positions[target]; var was = state.Inside[target];
                if (zone.Holds(position))
                {
                    if (!was) { state.Inside[target] = true; state.Members.Add(reference); state.MemberSlots.Add(target); entered += Publish(true, state, target) ? 1 : 0; }
                }
                else if (was)
                { state.Leave(target); exited += Publish(false, state, target) ? 1 : 0; }
                else if (_previousKnown[target] && state.HasCenter && Travel(_previous[target], position) <= MaximumJudgedTravel)
                {
                    for (var axis = 0; axis < 3; axis++) _relativeBefore[axis] = _previous[target][axis] + zone.Position[axis] - state.PreviousCenter[axis];
                    if (zone.Crossed(_relativeBefore, position))
                    { entered += Publish(true, state, target) ? 1 : 0; exited += Publish(false, state, target) ? 1 : 0; }
                }
            }
            for (var axis = 0; axis < 3; axis++) state.PreviousCenter[axis] = zone.Position[axis]; state.HasCenter = true;
        }
        foreach (var target in _order)
        {
            _previousKnown[target] = _observed[target];
            if (_observed[target]) for (var axis = 0; axis < 3; axis++) _previous[target][axis] = _positions[target][axis];
        }
        _judged = true;
        return new("complete", "zones-judged", zoneCount, _order.Count, entered, exited);
    }

    // Cold path: candidate identities changed. The targets that stay keep their slots and everything filed under
    // them; one that disappeared gives its slot back without an invented exit, and a new one takes a free slot.
    private void SyncTargets()
    {
        var same = _order.Count == _candidates.Count;
        if (same) for (var i = 0; i < _order.Count; i++) if (_targets[_order[i]] != _candidates[i]) { same = false; break; }
        if (same) return;
        (_before, _order) = (_order, _before);
        _order.Clear(); _arrived.Clear();
        foreach (var slot in _before) _rank[slot] = -1;
        for (var i = 0; i < _candidates.Count; i++)
        {
            if (_slotOf.TryGetValue(_candidates[i], out var slot)) _rank[slot] = i; else { slot = -1; _arrived.Add(i); }
            _order.Add(slot);
        }
        // Every release comes before the first claim: a new life of the same entity continues the sequences its
        // earlier life left in the zone's history.
        foreach (var slot in _before) if (_rank[slot] < 0) Release(slot);
        foreach (var i in _arrived) _order[i] = Claim(_candidates[i], i);
    }
    private void Release(int slot)
    {
        var id = _targets[slot].Id;
        _slotOf.Remove(_targets[slot]); _free.Push(slot); _previousKnown[slot] = false;
        foreach (var state in _states)
        {
            if (state.Entered[slot] != 0 || state.Exited[slot] != 0) state.History[id] = (state.Entered[slot], state.Exited[slot]);
            state.Entered[slot] = 0; state.Exited[slot] = 0;
            if (state.Inside[slot]) state.Leave(slot);
        }
    }
    private int Claim(EntityReference reference, int rank)
    {
        if (_free.Count == 0 && _slots == _targets.Length) Grow();
        var slot = _free.Count > 0 ? _free.Pop() : _slots++;
        _slotOf.Add(reference, slot); _targets[slot] = reference; _rank[slot] = rank; _player[slot] = rank < _players.Count;
        foreach (var state in _states)
            if (state.History.TryGetValue(reference.Id, out var history)) { state.Entered[slot] = history.Entered; state.Exited[slot] = history.Exited; }
        return slot;
    }
    private void Grow()
    {
        var size = Math.Max(16, _targets.Length * 2);
        Array.Resize(ref _targets, size); Array.Resize(ref _rank, size); Array.Resize(ref _player, size);
        Array.Resize(ref _observed, size); Array.Resize(ref _previousKnown, size);
        Array.Resize(ref _positions, size); Array.Resize(ref _previous, size);
        for (var i = _slots; i < size; i++) { _positions[i] = new double[3]; _previous[i] = new double[3]; }
        foreach (var state in _states) state.Resize(size);
    }
    private void ForgetTargets()
    { _slotOf.Clear(); _free.Clear(); _order.Clear(); _slots = 0; Array.Clear(_previousKnown, 0, _previousKnown.Length); }
    private bool Read(string kind, List<EntityReference> buffer)
    {
        bool complete; string code;
        // A refused read leaves the buffer as it was, so the previous membership is what is kept.
        try { complete = _kernel.TryEnumerateEntityCandidates(kind, buffer, out code); }
        catch (Exception error) { ReportOnce("reader:" + kind, "trigger-zone-reader-failed: " + kind + " (" + error.GetType().Name + ")"); return false; }
        if (!complete) { ReportOnce("reader:" + kind + ":" + code, "trigger-zone-targets: " + code + "; previous membership was preserved."); return false; }
        return true;
    }
    private bool Publish(bool entered, ZoneState state, int target)
    {
        var sequence = entered ? ++state.Entered[target] : ++state.Exited[target];
        bool published;
        try { published = _mapObjects.ZoneMembership(entered ? TriggerZoneContract.EnteredFact : TriggerZoneContract.ExitedFact, state.Zone, _targets[target], sequence); }
        catch (Exception error) { ReportOnce("publish:" + state.Zone.Id, "trigger-zone fact threw: " + error.GetType().Name); return false; }
        return published;
    }
    private static double Travel(IReadOnlyList<double> a, IReadOnlyList<double> b)
    { var x = a[0] - b[0]; var y = a[1] - b[1]; var z = a[2] - b[2]; return Math.Sqrt(x * x + y * y + z * z); }
    private TriggerZoneTickResult Idle(string code) => new("idle", code, 0, 0, 0, 0);
    private void OnLifecycle(RuntimeLifecycleEvent value)
    { if (value.Kind == RuntimeLifecycleKind.WorldChanged && !value.IsMigration) { ClearMembership(); _judgedLevel = null; _source = null; _states = Array.Empty<ZoneState>(); } }
    private void ClearMembership()
    {
        _judged = false;
        foreach (var state in _states) { state.Clear(); state.History.Clear(); Array.Clear(state.Entered, 0, state.Entered.Length); Array.Clear(state.Exited, 0, state.Exited.Length); }
        ForgetTargets(); _players.Clear(); _enemies.Clear();
        _candidates.Clear(); _inspect.Clear(); _inspectionSlots.Clear(); _poses.Clear();
    }
    private void ReportOnce(string key, string message) { _once.Report(key, message); }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifecycle.Dispose(); ClearMembership(); _source = null; _states = Array.Empty<ZoneState>(); }
}

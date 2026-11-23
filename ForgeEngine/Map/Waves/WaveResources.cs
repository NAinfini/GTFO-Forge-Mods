using System;
using System.Collections.Generic;

namespace ForgeMap;

/// <summary>An exported wave origin. Event IDs exist only after the native start call.</summary>
internal sealed record WaveResource(string Address, string Level, ushort SettingsId, ushort PopulationId,
    string? Revision = null, WaveComposition? Composition = null, IReadOnlyList<WaveNativeMember>? Members = null)
{
    public WaveHostProgram? Host { get; internal set; }
    public int Position { get; internal set; } = -1;
    /// <summary>Set when an earlier entry of the host starts this one; null when the host does.</summary>
    public WaveAfter? After { get; internal set; }

    /// <summary>The native members one run of this wave starts: the exported list, or the row's own pair.</summary>
    public IReadOnlyList<WaveNativeMember> NativeMembers => _nativeMembers ??= Members is { Count: > 0 } members
        ? members : new[] { new WaveNativeMember(SettingsId, PopulationId) };
    private IReadOnlyList<WaveNativeMember>? _nativeMembers;
}

/// <summary>Map owns whole-wave membership. Enemy reads the address, running instance and pending count
/// through one identity service. Epoch checks invalidate registrations, never resource addresses.
/// One run owns one slot per native member; every member event of a run reads the same identity.</summary>
internal sealed class WaveResourceRegistry
{
    private readonly Dictionary<(string, ushort, ushort), WaveResource> _sources = new();
    private readonly HashSet<(string, ushort, ushort)> _ambiguous = new();
    private readonly Dictionary<string, WaveResource> _addresses = new(StringComparer.Ordinal);
    private readonly List<WaveHostProgram> _hosts = new();
    // EventID is a native UInt16. Observation and continuation reads use its dense slot, never a lookup.
    private readonly Run?[] _events = new Run[ushort.MaxValue + 1];
    private readonly List<Run> _runs = new();
    // The newest run native carriers started for each resource in this epoch; carrier members merge into it.
    private readonly Dictionary<WaveResource, Run> _latestNative = new(ReferenceEqualityComparer.Instance);
    private long _epoch = long.MinValue;

    public sealed class Run
    {
        internal Run(WaveResource resource, long id, bool runtime)
        {
            Resource = resource; Id = id; Runtime = runtime;
            Slots = new ushort[resource.NativeMembers.Count];
        }
        internal readonly WaveResource Resource;
        internal readonly long Id;
        internal readonly bool Runtime;
        internal bool Cancelled;
        /// <summary>The native EventID registered for each member, 0 while that member is still pending.</summary>
        internal readonly ushort[] Slots;
        public string? EndCode { get; internal set; }
        public long Instance => Id;
        public bool IsCancelled => Cancelled;
        public WaveResource PinnedResource => Resource;
        public int Members => Slots.Length;
        public int Pending
        {
            get
            {
                int pending = 0;
                foreach (var slot in Slots) if (slot == 0) pending++;
                return pending;
            }
        }
        public ushort EventOf(int member) => Slots[member];
    }

    public IReadOnlyCollection<WaveResource> Resources => _addresses.Values;
    public IReadOnlyList<WaveHostProgram> Hosts => _hosts.AsReadOnly();

    public void AddDocument(string json)
    {
        AddPrepared(WaveResourceDocuments.Read(json));
    }

    internal void AddPrepared(WaveResourceDocument document)
    {
        // The restore record holds one row per address; an install over its capacity is refused whole, never truncated at run time.
        if (_addresses.Count + document.Waves.Count > WaveRelayLedger.MaximumEntries)
            throw new FormatException("wave-resource-too-many: the install holds more than " + WaveRelayLedger.MaximumEntries + " wave addresses");
        foreach (var row in document.Waves)
            if (_addresses.ContainsKey(row.Address)) throw new FormatException("wave-resource-duplicate: " + row.Address);
        foreach (var row in document.Waves)
        {
            _addresses.Add(row.Address, row);
            foreach (var member in row.NativeMembers)
            {
                // A pair repeated inside one resource is one origin with several slots; a pair shared by two
                // resources cannot tell a native carrier which wave it started.
                var key = (row.Level, member.SettingsId, member.PopulationId);
                if (!_sources.TryGetValue(key, out var owner)) _sources.Add(key, row);
                else if (!ReferenceEquals(owner, row)) _ambiguous.Add(key);
            }
        }
        _hosts.AddRange(document.Hosts);
    }

    /// <summary>Installed declaration, available when plans load before an expedition or native blocks exist.</summary>
    public WaveResource? Resolve(string address)
        => _addresses.TryGetValue(address, out var row) ? row : null;

    /// <summary>Execution still requires the declared level to be the active expedition.</summary>
    public WaveResource? Resolve(string address, string? level)
        => Resolve(address) is { } row && row.Level == level ? row : null;

    /// <summary>A native carrier started one member. It joins the newest uncancelled carrier run of the same
    /// resource whose slot for that member is still empty; otherwise it opens a new run.</summary>
    public bool Register(long epoch, string? level, ushort settings, ushort population, ushort eventId, out string reason)
        => Register(epoch, level, settings, population, eventId, out reason, out _);

    public bool Register(long epoch, string? level, ushort settings, ushort population, ushort eventId, out string reason,
        out Run? registered)
    {
        registered = null;
        // A wave outside the exported registry is the game's own and is never refused for a bad epoch or event id:
        // only a wave this registry owns can be refused here, the others answer the same code as an unregistered origin.
        bool owned = level != null && _sources.ContainsKey((level, settings, population));
        // The caller names the kernel's current world: runs of any other epoch, earlier or later, are stale.
        EnsureEpoch(epoch);
        if (epoch <= 0 || eventId == 0) { reason = owned ? "wave-registration-invalid" : "wave-origin-unregistered"; return false; }
        // Every new native Spawn invalidates a previous binding of the reusable EventID, including a wave
        // outside this package. A same-address native allocation must never resurrect an old Forge handle.
        InvalidateEvent(eventId);
        if (level == null || !_sources.TryGetValue((level, settings, population), out var row))
        { reason = "wave-origin-unregistered"; return false; }
        if (_ambiguous.Contains((level, settings, population))) { reason = "wave-origin-ambiguous"; return false; }
        if (!CanRun(row, out reason)) return false;
        Run? run = null;
        int member = -1;
        if (_latestNative.TryGetValue(row, out var latest) && !latest.Cancelled)
            member = EmptySlot(latest, settings, population);
        if (member >= 0) run = latest;
        else
        {
            run = AddRun(row, runtime: false);
            _latestNative[row] = run;
            member = EmptySlot(run, settings, population);
        }
        run!.Slots[member] = eventId;
        _events[eventId] = run;
        registered = run;
        reason = "";
        return true;
    }

    private static int EmptySlot(Run run, ushort settings, ushort population)
    {
        var members = run.Resource.NativeMembers;
        for (int index = 0; index < members.Count; index++)
            if (run.Slots[index] == 0 && members[index].SettingsId == settings && members[index].PopulationId == population)
                return index;
        return -1;
    }

    public string? Read(long epoch, ushort eventId)
        => ReadIdentity(epoch, eventId)?.Address;

    // Replaces the address-only service: pending membership belongs to the same identity owner, not a second ledger in Enemy.
    public (string Address, long Instance, int Pending, bool Cancelled)? ReadIdentity(long epoch, ushort eventId)
        => epoch == _epoch && _events[eventId] is { } run
            ? (run.Resource.Address, run.Id, run.Pending, run.Cancelled) : null;

    /// <summary>An action pins the run before the native start of its first member; there are no managed member requests.</summary>
    public Run BeginRuntime(long epoch, WaveResource resource)
    {
        if (epoch <= 0) throw new InvalidOperationException("wave-registration-stale");
        if (!_addresses.TryGetValue(resource.Address, out var registered) || !ReferenceEquals(registered, resource))
            throw new InvalidOperationException("wave-origin-unregistered");
        EnsureEpoch(epoch);
        return AddRun(resource, runtime: true);
    }

    public bool RegisterRuntime(long epoch, Run run, int member, ushort eventId, out string reason)
    {
        if (epoch != _epoch || run.Id < 1 || run.Id > _runs.Count || !ReferenceEquals(_runs[(int)run.Id - 1], run))
        { reason = "wave-registration-stale"; return false; }
        if (run.Cancelled || eventId == 0) { reason = "wave-pending-cancelled"; return false; }
        if (member < 0 || member >= run.Slots.Length) { reason = "wave-member-unknown"; return false; }
        if (run.Slots[member] != 0) { reason = "wave-instance-already-registered"; return false; }
        InvalidateEvent(eventId);
        run.Slots[member] = eventId;
        _events[eventId] = run; reason = ""; return true;
    }

    /// <summary>Cancels the whole run the event belongs to; every member reads the cancellation.</summary>
    public void Cancel(long epoch, ushort eventId, string code = "explicit-cancel")
    {
        if (epoch == _epoch && _events[eventId] is { } run)
        { run.Cancelled = true; run.EndCode ??= code; }
    }

    public WaveHostProgram? HostOf(WaveResource resource)
        => resource.Host;

    public WaveResource? NativeResource(string? level, ushort settings, ushort population)
        => level != null && _sources.TryGetValue((level, settings, population), out var row)
            && !_ambiguous.Contains((level, settings, population)) ? row : null;

    public bool CanRun(WaveResource resource, out string reason)
    {
        reason = resource.Host?.Readiness == "runtime-required" ? "wave-native-start-disabled" : "";
        return reason.Length == 0;
    }

    public void ChangeEpoch(long epoch) => EnsureEpoch(epoch);

    // An event ID is only a current native lookup. Its previous logical wave can still own live groups after
    // original budget exhaustion; rebinding must not turn that real history into an explicit cancellation.
    private void InvalidateEvent(ushort eventId) => _events[eventId] = null;

    private Run AddRun(WaveResource resource, bool runtime)
    {
        // Runs are append-only within an epoch, so the public instance is exactly the stable index + 1.
        var run = new Run(resource, checked((long)_runs.Count + 1), runtime);
        _runs.Add(run); return run;
    }

    public (string Address, long Instance, int Pending, bool Cancelled)? ReadInstanceIdentity(long epoch, long instance)
    {
        if (epoch != _epoch || epoch <= 0 || instance < 1 || instance > _runs.Count) return null;
        var run = _runs[(int)instance - 1];
        return (run.Resource.Address, run.Id, run.Pending, run.Cancelled);
    }

    /// <summary>A host migration keeps the level and its waves: the runs and event bindings move to the new number.</summary>
    public void FollowEpoch(long epoch) => _epoch = epoch;

    private void EnsureEpoch(long epoch)
    {
        if (_epoch == epoch) return;
        System.Array.Clear(_events, 0, _events.Length);
        _runs.Clear();
        _latestNative.Clear();
        _epoch = epoch;
    }
}

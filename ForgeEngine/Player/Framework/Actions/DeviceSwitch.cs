using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>
/// The hold table. Time is this machine's own clock (injected), because the row runs on every addressed machine
/// and each machine ends its own holds; the tick-to-second conversion is the caller's, at the rate the world
/// reported. Due holds are ended by the caller's own frame calling <see cref="Expire"/>; a lifecycle notification
/// is read-only and a restore reaches native bodies whose hooks publish. The table asks for the kernel's lifecycle
/// only while it holds something, to see a world change, and gives it back as soon as it holds nothing. A world change marks the restore pending and the caller's frame then attempts, through <see cref="RestoreEnded"/>, to put every held device back: a player's interface is not a level
/// object and would otherwise stay dark into the next level. A failed restore keeps its original receipt and
/// identity for explicit cleanup; ticks do not retry it or refresh it into a new hold.
/// </summary>
internal sealed class DeviceSwitchboard : IDisposable
{
    private sealed class Hold
    {
        internal Hold(IDeviceWriter writer, EntityReference target, double until)
        { Writer = writer; Target = target; Until = until; }
        internal IDeviceWriter Writer { get; }
        internal EntityReference Target { get; }
        internal double Until { get; set; }
        internal long Revision { get; set; }
        internal bool Restoring { get; set; }
        internal bool RestoreFailed { get; set; }
    }

    private readonly Func<double> _now;
    private readonly Action<string> _report;
    private readonly Dictionary<string, IDeviceWriter> _writers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Category, string Target), Hold> _holds = new();
    private RuntimeModuleHandle? _registration;
    private RuntimeLifecycleSubscription? _clock;
    private bool _disposed;
    /// <summary>The epoch of the last world a lifecycle notification announced, while the holds of every other
    /// world are still to be restored; -1 when nothing is pending. Local bookkeeping only.</summary>
    private long _restoreOutsideEpoch = -1;

    public DeviceSwitchboard(Func<double> now, Action<string> report)
    {
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>How many devices are held right now.</summary>
    public int Count => _holds.Count;
    /// <summary>Whether the table holds the kernel's lifecycle right now.</summary>
    public bool Ticking => _clock != null;

    /// <summary>Hands the table the registration it takes the kernel's lifecycle through. Without one the table
    /// still holds and restores, and a caller drives <see cref="Expire"/> itself, as it always does.</summary>
    public void Attach(RuntimeModuleHandle registration)
        => _registration = registration ?? throw new ArgumentNullException(nameof(registration));

    public IDisposable Register(IDeviceWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (Array.IndexOf(DeviceCategories.All, writer.Category) < 0)
            throw new ArgumentException("Not a device category: " + writer.Category, nameof(writer));
        if (_writers.ContainsKey(writer.Category))
            throw new InvalidOperationException("A writer for " + writer.Category + " is already registered.");
        _writers.Add(writer.Category, writer);
        return new Registration(this, writer);
    }

    public IDeviceWriter? WriterOf(string category)
        => category != null && _writers.TryGetValue(category, out var writer) ? writer : null;

    public DeviceHoldChange Disable(IDeviceWriter writer, EntityReference target, double seconds)
    {
        ArgumentNullException.ThrowIfNull(writer); ArgumentNullException.ThrowIfNull(target);
        if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var key = (writer.Category, target.Id);
        double until = _now() + seconds;
        if (_holds.TryGetValue(key, out var held))
        {
            RequireIdentity(held, writer, target);
            RequireIdle(held);
            if (held.RestoreFailed)
                throw new RuntimeContractException("device-restore-pending", "Restore the device's retained hold before disabling it again.");
            // The same device pulsed again: the newer pulse decides when it comes back. The device is already off,
            // so nothing is written, and the one restore the first hold owes is still the only one.
            held.Until = until;
            held.Revision++;
            return DeviceHoldChange.Refreshed;
        }
        writer.Disable(target);
        _holds.Add(key, new Hold(writer, target, until));
        TakeClock();
        return DeviceHoldChange.Disabled;
    }

    public DeviceHoldChange Restore(IDeviceWriter writer, EntityReference target)
    {
        ArgumentNullException.ThrowIfNull(writer); ArgumentNullException.ThrowIfNull(target);
        var key = (writer.Category, target.Id);
        if (!_holds.TryGetValue(key, out var held)) return DeviceHoldChange.NotHeld;
        RequireIdentity(held, writer, target);
        RestoreHold(key, held);
        return DeviceHoldChange.Restored;
    }

    /// <summary>Adds every hold that is still running to <paramref name="into"/>, with the seconds it has left on this
    /// machine's clock: what a machine that joins now is owed.</summary>
    public void Running(List<(string Category, EntityReference Target, double Seconds)> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (_holds.Count == 0) return;
        double now = _now();
        foreach (var held in _holds.Values)
            if (!held.Restoring && !held.RestoreFailed && held.Until > now)
                into.Add((held.Writer.Category, held.Target, held.Until - now));
    }

    /// <summary>Attempts each due hold once. Failed restores remain explicit-retry debt, not another timed attempt.</summary>
    public int Expire()
    {
        if (_holds.Count == 0) return 0;
        double now = _now();
        List<((string, string) Key, Hold Held, long Revision)>? due = null;
        foreach (var entry in _holds)
            if (!entry.Value.Restoring && !entry.Value.RestoreFailed && entry.Value.Until <= now)
                (due ??= new()).Add((entry.Key, entry.Value, entry.Value.Revision));
        if (due == null) return 0;
        int attempts = 0;
        foreach (var entry in due)
            if (!entry.Held.RestoreFailed && End(entry.Key, entry.Held, entry.Revision)) attempts++;
        return attempts;
    }

    /// <summary>Attempts every current hold now, including retained failures. Used when their world is ending.</summary>
    public void RestoreAll()
    {
        if (_holds.Count == 0) return;
        foreach (var entry in Snapshot()) End(entry.Key, entry.Held, entry.Revision);
    }

    /// <summary>Attempts every hold of a world that has ended, once, after the lifecycle notification that announced
    /// the new world: the notification only marks the restore pending, and the caller's frame performs it here. A
    /// hold made in the new world is not touched. Failed holds are attempted too, as <see cref="RestoreAll"/> does.</summary>
    public int RestoreEnded()
    {
        long keep = _restoreOutsideEpoch;
        if (keep < 0) return 0;
        _restoreOutsideEpoch = -1;
        if (_holds.Count == 0) return 0;
        int attempts = 0;
        foreach (var entry in Snapshot())
            if (entry.Held.Target.WorldEpoch != keep && End(entry.Key, entry.Held, entry.Revision)) attempts++;
        return attempts;
    }

    private List<((string, string) Key, Hold Held, long Revision)> Snapshot()
    {
        var snapshot = new List<((string, string) Key, Hold Held, long Revision)>(_holds.Count);
        foreach (var entry in _holds) snapshot.Add((entry.Key, entry.Value, entry.Value.Revision));
        return snapshot;
    }

    private static void RequireIdentity(Hold held, IDeviceWriter writer, EntityReference target)
    {
        if (!ReferenceEquals(held.Writer, writer) || held.Target != target)
            throw new RuntimeContractException("device-hold-mismatch", "The retained hold belongs to a different writer or target identity.");
    }

    private static void RequireIdle(Hold held)
    {
        if (held.Restoring)
            throw new RuntimeContractException("device-restore-in-progress", "This device's restore is already in progress.");
    }

    private void RestoreHold((string, string) key, Hold held)
    {
        RequireIdle(held);
        // Even a failed attempt invalidates older bulk snapshots; another callback already visited this receipt.
        long revision = ++held.Revision;
        held.Restoring = true;
        try
        {
            // The writer owns its native receipt. Do not detach our route to it until it confirms restoration.
            held.Writer.Restore(held.Target);
            if (_holds.TryGetValue(key, out var current) && ReferenceEquals(current, held) && held.Revision == revision)
                _holds.Remove(key);
        }
        catch
        {
            held.RestoreFailed = true;
            throw;
        }
        finally { held.Restoring = false; }
    }

    private bool End((string, string) key, Hold held, long revision)
    {
        // A callback may refresh, restore or replace another entry after a bulk cleanup takes its snapshot.
        // It must not end that newer hold, and nested cleanup must not re-enter a writer already restoring.
        if (!_holds.TryGetValue(key, out var current) || !ReferenceEquals(current, held)
            || held.Revision != revision || held.Restoring) return false;
        try { RestoreHold(key, held); }
        catch (Exception error)
        {
            // Reporting is another callback boundary: it must not recursively retry this failed cleanup.
            held.Restoring = true;
            try
            {
                Report("device.restore-failed: " + held.Writer.Category + " " + held.Target.Id + ": "
                    + error.GetType().Name + ": " + error.Message);
            }
            finally { held.Restoring = false; }
        }
        return true;
    }

    /// <summary>Takes the kernel's lifecycle when the first hold appears. Never called from inside a lifecycle
    /// notification: a hold is taken by a command handler.</summary>
    private void TakeClock()
    {
        if (_disposed || _clock != null || _registration == null || !_registration.IsRegistered) return;
        try { _clock = _registration.ObserveLifecycle(OnLifecycle, replayCurrent: false); }
        catch (RuntimeContractException error)
        {
            // The device is already off and its hold recorded; without the tick it comes back only through an
            // explicit restore or the world ending, and the report says so rather than the write being undone.
            Report("device.clock-unavailable: " + error.Code + ": " + error.Message);
        }
    }

    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        // Neither expiry nor the restore of a finished world runs from here: both reach native device bodies whose
        // hooks publish, which a read-only lifecycle notification may not do. A world change only marks the restore
        // pending; the host's own frame calls <see cref="Expire"/> and <see cref="RestoreEnded"/>.
        if (value.Kind == RuntimeLifecycleKind.WorldChanged) _restoreOutsideEpoch = value.Current.WorldEpoch;
        // Giving the registration back is the one kernel mutation a lifecycle callback may make.
        if (_holds.Count == 0) ReleaseClock();
    }

    private void ReleaseClock()
    {
        var clock = _clock;
        _clock = null;
        clock?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreAll();
        ReleaseClock();
        _writers.Clear();
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception) { /* a reporter that throws must not replace the outcome it reports */ }
    }

    private sealed class Registration : IDisposable
    {
        private DeviceSwitchboard? _board;
        private readonly IDeviceWriter _writer;
        internal Registration(DeviceSwitchboard board, IDeviceWriter writer) { _board = board; _writer = writer; }

        public void Dispose()
        {
            var board = _board;
            _board = null;
            if (board == null) return;
            // The writer's own holds end with it: a device it turned off is put back while it can still write.
            foreach (var entry in board.Snapshot())
                if (ReferenceEquals(entry.Held.Writer, _writer)) board.End(entry.Key, entry.Held, entry.Revision);
            if (board._writers.TryGetValue(_writer.Category, out var current) && ReferenceEquals(current, _writer))
                board._writers.Remove(_writer.Category);
        }
    }
}

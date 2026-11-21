using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ForgeDevelopment.Native;

// Source-linked by the offline lifecycle tests. No Harmony/game types here: the
// production delegates use Runtime leases for native admission and signature-specific ABI.
internal sealed class PopulationHookLifecycle
{
    private readonly Action<MethodBase> _removeTarget;
    private readonly List<MethodBase> _attempts = new();
    private readonly HashSet<MethodBase> _installed = new();
    private bool _started, _stopped, _failed;
    internal PopulationHookLifecycle(Action<MethodBase> removeTarget) { _removeTarget = removeTarget; }
    internal int InstalledCount => _installed.Count;
    internal bool StartFailed => _failed;
    internal bool HasHooks => _attempts.Count != 0;
    internal bool CleanupPending { get; private set; }
    internal MethodBase[] PendingTargets => _attempts.ToArray();

    internal void Start(Action configure)
    {
        if (_started || _stopped || _failed) throw new InvalidOperationException("Population hook startup is single-attempt.");
        _started = true;
        try
        {
            configure();
        }
        catch (Exception original)
        {
            _failed = true;
            try { Stop(); }
            catch (Exception cleanup) { throw new AggregateException("Population hook startup and rollback failed.", original, cleanup); }
            throw;
        }
    }
    internal void Install(MethodBase target, Action patch)
    {
        if (!_started || _stopped || _failed || CleanupPending)
            throw new InvalidOperationException("Population hook session is unavailable.");
        if (_attempts.Contains(target)) throw new InvalidOperationException("Duplicate population hook target.");
        // Register before acquisition: Runtime may mutate then fail its rollback.
        _attempts.Add(target);
        patch();
        _installed.Add(target);
    }
    internal void Stop()
    {
        _stopped = true;
        var failures = new List<Exception>();
        foreach (var method in Enumerable.Reverse(_attempts.ToArray()))
        {
            try
            {
                _removeTarget(method);
                _attempts.Remove(method);
                _installed.Remove(method);
            }
            catch (Exception error) { failures.Add(new InvalidOperationException("Population hook removal failed: " + method, error)); }
        }
        CleanupPending = HasHooks;
        if (failures.Count != 0) throw new AggregateException("Population hooks remain pending cleanup.", failures);
    }
}


// Live opt-in on the existing recorder tick. Cleanup retries are not installation
// retries. A failed start is terminal for this controller's process lifetime.
internal sealed class PopulationHookSwitch
{
    private readonly Func<bool> _hasHooks;
    private readonly Action _start, _stop;
    private readonly Action<Exception> _report;
    private bool _attempted, _running;
    private long _nextCleanup;
    internal PopulationHookSwitch(Func<bool> hasHooks, Action start, Action stop, Action<Exception> report)
    { _hasHooks = hasHooks; _start = start; _stop = stop; _report = report; }
    internal bool Requested { get; private set; }
    internal bool StartFailed { get; private set; }
    internal void Tick(bool enabled, bool gameDataLoaded, long now)
    {
        var changed = Requested != enabled;
        Requested = enabled;
        if (changed && enabled && !StartFailed) _attempted = false;
        if (!enabled || StartFailed)
        {
            _running = false;
            Cleanup(now, changed);
            return;
        }
        if (_running) return;
        // A rapid off/on cannot install over a failed previous teardown.
        if (_hasHooks())
        {
            Cleanup(now, false);
            if (_hasHooks()) return;
        }
        if (_attempted || !gameDataLoaded) return;
        _attempted = true;
        try { _start(); _running = true; }
        catch (Exception error)
        {
            StartFailed = true;
            Report(error);
            Cleanup(now, true);
        }
    }
    private void Cleanup(long now, bool immediate)
    {
        if (!_hasHooks() || !immediate && now < _nextCleanup) return;
        _nextCleanup = now + 1000;
        try { _stop(); }
        catch (Exception error) { Report(error); }
    }
    private void Report(Exception error)
    { try { _report(error); } catch (Exception) { /* Cleanup must survive its reporter. */ } }
}

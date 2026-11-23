using System;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>An in-level frame entry. Every joined entry (the Player, Map and Enemy domains, the presentation provider)
/// shares one Runtime native work lease per world (`forge.domain.frame`), which runs the entries in their fixed order
/// on the Runtime's Update (loading and pause included), outside every kernel notification. Each entry keeps its own
/// lifecycle observation: it leaves the frame when the Runtime fails, stops or suspends, and ends with its owner's
/// registration. An entry whose frame throws is reported under its own consumer name and skipped for the rest of that
/// world; the others keep running. A per-tick entry runs at most once per kernel tick of a world, and only while the
/// Runtime is ready, so a paused or migrating kernel, which does not advance, is not driven: the one such gate.</summary>
internal sealed class DomainWorkPhases : IDisposable
{
    private const string SharedConsumer = "forge.domain.frame";
    private static DomainWorkPhases[] _entries = Array.Empty<DomainWorkPhases>();
    private static RuntimeWorkLease? _shared;
    private static long _sharedEpoch = -1;

    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _owner;
    private readonly string _consumer;
    private readonly int _order;
    private readonly Action _frame;
    private readonly bool _perTick;
    private readonly RuntimeLifecycleSubscription _worlds;
    private long _epoch = -1, _tickEpoch = -1, _tick = long.MinValue;
    private bool _active, _failed, _disposed;

    /// <param name="consumer">The name a frame failure is reported under, such as `forge.map.frame`.</param>
    /// <param name="order">The entry's place in the shared frame; lower runs first.</param>
    /// <param name="perTick">Run at most once per kernel tick of a world instead of every frame.</param>
    public DomainWorkPhases(string consumer, int order, RuntimeModuleHandle owner, IForgeEngine kernel, Action frame, bool perTick = false)
    {
        _consumer = consumer; _order = order; _kernel = kernel; _owner = owner; _frame = frame; _perTick = perTick;
        Join(this);
        try
        {
            Renew(kernel.Lifecycle);
            _worlds = owner.ObserveLifecycle(change => Renew(change.Current), replayCurrent: false);
        }
        catch { Leave(this); throw; }
    }

    private void Renew(RuntimeLifecycleSnapshot world)
    {
        if (_disposed) return;
        _active = world.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            && _kernel.FactAdmissionClosure != "runtime-suspended";
        if (_active && _epoch != world.WorldEpoch) { _epoch = world.WorldEpoch; _failed = false; }
        Sync(world.WorldEpoch);
    }

    private static void Join(DomainWorkPhases entry)
    {
        int index = 0;
        while (index < _entries.Length && _entries[index]._order <= entry._order) index++;
        var next = new DomainWorkPhases[_entries.Length + 1];
        Array.Copy(_entries, 0, next, 0, index);
        next[index] = entry;
        Array.Copy(_entries, index, next, index + 1, _entries.Length - index);
        _entries = next;
    }

    private static void Leave(DomainWorkPhases entry)
    {
        int index = Array.IndexOf(_entries, entry);
        if (index < 0) return;
        var next = new DomainWorkPhases[_entries.Length - 1];
        Array.Copy(_entries, 0, next, 0, index);
        Array.Copy(_entries, index + 1, next, index, next.Length - index);
        _entries = next;
        Sync(_sharedEpoch);
    }

    /// <summary>Holds the shared lease for <paramref name="epoch"/> while any entry is active, and drops it otherwise.</summary>
    private static void Sync(long epoch)
    {
        bool any = false;
        foreach (var entry in _entries) any |= entry._active;
        if (!any) { _shared?.Dispose(); _shared = null; _sharedEpoch = -1; return; }
        if (_shared != null && _sharedEpoch == epoch) return;
        _shared?.Dispose(); _shared = null; _sharedEpoch = epoch;
        _shared = RuntimeWorkPhases.Native(SharedConsumer, epoch, 200, Frame);
    }

    private static void Frame(NativeWorkFrame _)
    {
        var entries = _entries;
        foreach (var entry in entries)
        {
            if (entry._disposed || !entry._active || entry._failed || entry._epoch != _sharedEpoch) continue;
            if (!entry._owner.IsRegistered) { entry.Dispose(); continue; }
            if (entry._perTick && !entry.Advanced()) continue;
            try { entry._frame(); }
            catch (Exception error)
            {
                entry._failed = true;
                GameRuntimeBridge.ReportNativeObserverFailure(entry._consumer, error);
            }
        }
    }

    /// <summary>The Runtime is ready and its tick moved on since this entry last ran.</summary>
    private bool Advanced()
    {
        if (_kernel.StartupState != RuntimeStartupState.Ready) return false;
        var epoch = _kernel.WorldEpoch;
        var tick = _kernel.CurrentTick;
        if (tick < 0 || (epoch == _tickEpoch && tick == _tick)) return false;
        _tickEpoch = epoch; _tick = tick;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _active = false; _worlds.Dispose(); Leave(this);
    }
}

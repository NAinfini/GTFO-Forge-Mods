using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>A package's leases on Runtime-owned shared native entries (a domain's, or Development's for as long as one capture or
/// trace runs), renewed for every world. The Runtime
/// owns the physical install; the package owns only these world-bound leases, taken all or none. A lease the Runtime
/// refuses throws its `native-hook-*` reason: while the package loads that refuses startup (or the feature standing on
/// it); when a later world renews, the refusal propagates, or, if <c>laterRefusal</c> was given, it is handed to that
/// callback and the world is left without leases.</summary>
public sealed class DomainWorldObservation : IDisposable
{
    private readonly IForgeEngine _kernel;
    private readonly string _domain;
    private readonly Action<string>? _laterRefusal;
    private readonly Func<long, NativeHookLease>[] _subscribe;
    private readonly RuntimeLifecycleSubscription _worlds;
    private NativeHookLease[] _leases = Array.Empty<NativeHookLease>();
    private string _state = "released";
    private bool _starting = true;

    /// <param name="domain">The package's name in messages, such as `Enemy`.</param>
    /// <param name="laterRefusal">Where a refusal met when a later world renews is reported; null lets it propagate.</param>
    public DomainWorldObservation(string domain, RuntimeModuleHandle owner, IForgeEngine kernel, Action<string>? laterRefusal,
        params Func<long, NativeHookLease>[] subscribe)
    {
        _domain = domain; _kernel = kernel; _laterRefusal = laterRefusal; _subscribe = subscribe;
        Renew(kernel.Lifecycle);
        _starting = false;
        try { _worlds = owner.ObserveLifecycle(change => Renew(change.Current), replayCurrent: false); }
        catch (Exception original)
        {
            try { Release(); }
            catch (Exception cleanup) { throw new AggregateException(_domain + " world-observation startup cleanup failed.", original, cleanup); }
            throw;
        }
    }

    /// <summary>"active" while every lease is; otherwise the first lease's end, or why none is held.</summary>
    public string Status
    {
        get
        {
            if (_leases.Length == 0) return _state;
            foreach (var lease in _leases) if (lease.Status != "active") return lease.Status;
            return "active";
        }
    }

    private void Renew(RuntimeLifecycleSnapshot world)
    {
        if (world.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
        {
            Release(); _state = world.StartupState == RuntimeStartupState.Failed ? "failed" : "stopped";
            return;
        }
        if (_kernel.FactAdmissionClosure == "runtime-suspended") { Release(); _state = "suspended"; return; }
        if (_leases.Length != 0 && _leases[0].WorldEpoch == world.WorldEpoch) return;
        Release();
        // All of a world's leases or none: a refusal releases the ones already taken before it propagates.
        var taken = new NativeHookLease[_subscribe.Length];
        try { for (var index = 0; index < taken.Length; index++) taken[index] = _subscribe[index](world.WorldEpoch); }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            foreach (var lease in taken)
            {
                if (lease == null) continue;
                try { lease.Dispose(); }
                catch (Exception cleanup) { failures.Add(cleanup); }
            }
            if (failures.Count != 1)
                throw new AggregateException(_domain + " world-observation admission and rollback failed.", failures);
            if (_starting || _laterRefusal == null
                || original is not (NotSupportedException or ArgumentException or InvalidOperationException)) throw;
            _laterRefusal(original.Message);
            return;
        }
        _leases = taken;
    }

    private void Release()
    {
        var failed = new List<NativeHookLease>();
        var failures = new List<Exception>();
        foreach (var lease in _leases)
        {
            try { lease.Dispose(); }
            catch (Exception error)
            {
                failures.Add(error);
                if (lease.Status == "active") failed.Add(lease);
            }
        }
        _leases = failed.ToArray();
        if (failures.Count != 0)
            throw new AggregateException(_domain + " world-observation lease cleanup failed.", failures);
    }

    public void Dispose()
    {
        var failures = new List<Exception>();
        try { _worlds?.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { Release(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0)
            throw new AggregateException(_domain + " world-observation cleanup failed.", failures);
    }
}

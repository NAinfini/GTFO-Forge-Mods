using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEnemy.Native.Observation;

namespace ForgeEnemy.Native;

/// <summary>Domain-owned lifetime; the host remains the only runtime, clock and authority gate.</summary>
internal sealed class EnemyPluginSession : IDisposable
{
    private readonly IForgeEngine _kernel;
    private readonly Action _releaseNativeWork;
    private bool _disposed, _faulted, _nativeWorkReleased;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    internal EnemyModule Module { get; private set; } = null!;
    /// <summary>Which native functions stand on this build; a hook body of a withdrawn function is inert.</summary>
    internal EnemyHookAvailability Hooks { get; private set; } = EnemyHookAvailability.Complete;
    internal bool Faulted => Disabled();
    private readonly global::ForgeEngine.GameBindings.WorldFault _fault = new();
    internal string? LastFault { get; private set; }
    internal string? LastReporterFailure { get; private set; }
    private EnemyPluginSession(IForgeEngine kernel, Action releaseNativeWork)
    { _kernel = kernel; _releaseNativeWork = releaseNativeWork; }

    /// <summary>Hands the package's native callback classes to the Runtime through <paramref name="declareNative"/>,
    /// which answers which functions the Runtime could not prove on this build, then registers the module without the
    /// bindings of those functions. The callbacks read <c>EnemyHost.Session</c>, which is set only after this returns, so
    /// a declared target is inert until the module exists. The Runtime is the only installer, so there is nothing to
    /// remove afterwards except the work lease, which <paramref name="releaseNativeWork"/> ends. A
    /// <paramref name="report"/> of null writes cleanup failures as <c>enemy.cleanup-failed</c> under the module's
    /// registration.</summary>
    internal static EnemyPluginSession Start(IForgeEngine kernel, RuntimeLogLevel level, Func<bool> canExecute,
        Action<string>? report, Func<EnemyHookAvailability> declareNative, Action releaseNativeWork)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(canExecute);
        ArgumentNullException.ThrowIfNull(declareNative); ArgumentNullException.ThrowIfNull(releaseNativeWork);
        if (!kernel.IsRegistrationOpen)
            throw new InvalidOperationException("Enemy must register during dependent plugin Load, before Runtime startup.");
        var session = new EnemyPluginSession(kernel, releaseNativeWork);
        report ??= message => session.WriteLog(RuntimeLogLevel.Error, EnemyLogCodes.CleanupFailed, message);
        bool declarationsAttempted = false;
        try
        {
            declarationsAttempted = true;
            session.Hooks = declareNative();
            session.Module = new EnemyModule(kernel, level, () => !session.Disabled() && canExecute(),
                EnemyEntityObserver.Read, EnemyTypeReader.ReadSpawned, session.Hooks, EnemyEntityObserver.ReadPose);
            return session;
        }
        catch (Exception original)
        {
            try
            {
                session.WriteLog(RuntimeLogLevel.Error, EnemyLogCodes.NativeRefused,
                "Enemy startup refused: " + original.GetType().Name + ": " + original.Message);
            }
            catch (Exception reporting) { original.Data["ForgeEnemy.StartupReportingFailure"] = reporting; }
            // Any residual registration is inert even if rollback itself fails.
            session._faulted = true;
            var errors = new List<Exception>();
            if (declarationsAttempted) session.ReleaseNativeWork(errors);
            if (session.Module != null) DomainSession.Cleanup(session.Module.Dispose, errors);
            session._disposed = true;
            if (errors.Count != 0) DomainSession.PreserveCleanupFailure(original, "ForgeEnemy.CleanupFailures", new AggregateException(errors), report);
            throw;
        }
    }

    /// <summary>One record under this provider's registration; nothing is built unless the level is open and nothing
    /// is written before the module exists.</summary>
    internal void WriteLog(RuntimeLogLevel level, string code, string message)
    {
        if (Module is not { } module || !module.Log.Logs(level)) return;
        module.Log.Write(level, code, new RuntimeLogFacts { Detail = message });
    }

    internal void Guard(Action<EnemyModule> callback)
    {
        DomainSession.CheckThread(_threadId, "Enemy");
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed || Disabled() || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try { callback(Module); }
        catch (Exception error) { Fault(error); }
    }

    /// <summary>The in-level frame driver: the one place the Enemy package's per-tick work that publishes facts
    /// runs. Kernel lifecycle observers are read-only (<c>lifecycle-observer-mutation</c>), and observing a native
    /// phase change publishes one. Its <see cref="DomainWorkPhases"/> entry calls it from the Runtime's native work phase,
    /// outside every kernel notification, at most once per kernel tick of a world (a per-tick entry), so a paused kernel is not driven.
    /// A throw disables the session and is reported through the same fault path every guarded entry uses.</summary>
    internal void InFrame()
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.EnemyFrame);
        DomainSession.CheckThread(_threadId, "Enemy");
        if (_disposed || Disabled()) return;
        try { Module.ObserveNativePhasesInFrame(); Module.ObserveHoldersInFrame(); }
        catch (Exception error) { Fault(error); }
    }

    /// <summary>PERF(F4/F6/F7): the closure-free twin of the one-argument `Guard`, for the per-frame and per-hit
    /// hooks. Same checks, same fault path. The caller passes a `static` lambda, which the compiler caches once,
    /// and its data by reference in <paramref name="state"/>, so a hook call allocates nothing.</summary>
    internal void Guard<TState>(ref TState state, EnemyGuardCallback<TState> callback)
    {
        DomainSession.CheckThread(_threadId, "Enemy");
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed || Disabled() || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try { callback(Module, ref state); }
        catch (Exception error) { Fault(error); }
    }

    /// <summary>The one guarded entry that answers a value instead of running a callback: the damage a receiver is
    /// about to take, after whatever rule stands on it. It exists as its own member rather than as a generic
    /// `Guard` overload because the caller is a `ref` parameter of the busiest hook in this package - a `ref`
    /// cannot be captured in a lambda, and a closure per hit is a cost this path does not need to pay. A session
    /// that is gone, faulted or past its runtime's life changes nothing and answers the amount it was handed.
    /// </summary>
    internal float GuardDamageRule(Dam_EnemyDamageBase receiver, float incoming, Agents.Agent? source = null, bool unruled = false)
    {
        DomainSession.CheckThread(_threadId, "Enemy");
        if (_disposed || Disabled() || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            return incoming;
        try { return Module.ApplyDamageRule(receiver, incoming, source, unruled); }
        catch (Exception error) { Fault(error); return incoming; }
    }

    /// <summary>A fault disables the session for the world it happened in. A new world is a new start: the fault already cleared
    /// the module's world state, so the session is ready again for the first guarded entry of the next world. A session that is
    /// disposed (startup rollback or shutdown) stays disabled.</summary>
    private bool Disabled() => _faulted || _fault.Holds(_kernel.WorldEpoch);

    /// <summary>One native callback failure: the module stops touching the world and the session stays disabled
    /// until the next world begins, which is the same answer every guarded entry gives. The failure is one `enemy.disabled`
    /// error record under this provider's registration; a record the kernel or its sink refused leaves the session
    /// disabled all the same and keeps the refusal's type in <see cref="LastReporterFailure"/>.</summary>
    private void Fault(Exception error)
    {
        _fault.Mark(_kernel.WorldEpoch);
        var errors = new List<Exception>(); DomainSession.Cleanup(Module.ClearWorldAfterFault, errors);
        string detail = RuntimeDiagnostics.Describe(error) + CallSite(error);
        LastFault = detail;
        var log = Module.Log;
        try { if (!log.Logs(RuntimeLogLevel.Error)) return; }
        catch (Exception gate) { LastReporterFailure = gate.GetType().Name; return; }
        if (!log.Write(RuntimeLogLevel.Error, EnemyLogCodes.Disabled, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("exception", error.GetType().Name), RuntimeLogValue.Of("cleanupErrors", (long)errors.Count) },
            Detail = detail
        }))
            LastReporterFailure = log.LastFailure;
    }

    /// <summary>The first frames of the failure's stack, so the record names the call that threw and not only the
    /// contract it broke.</summary>
    private static string CallSite(Exception error)
    {
        string? stack;
        try { stack = error.StackTrace; } catch (Exception caught) { EnemyLog.Contained(caught); return ""; }
        if (string.IsNullOrEmpty(stack)) return "";
        var frames = stack.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var site = new System.Text.StringBuilder(" at");
        for (var index = 0; index < frames.Length && index < 6; index++)
            site.Append(' ').Append(frames[index].Trim().Split(" in ")[0]).Append(" <-");
        return site.Length > 1200 ? site.ToString(0, 1200) : site.ToString();
    }

    public void Dispose()
    {
        DomainSession.CheckThread(_threadId, "Enemy");
        if (_disposed)
        {
            // Keep the module reachable as the owner of any failed restorations. Native work is retried only
            // when its previous release threw; a confirmed release is never submitted twice.
            var retryFailures = new List<Exception>();
            try { Module.Dispose(); } catch (Exception error) { retryFailures.Add(error); }
            ReleaseNativeWork(retryFailures);
            if (retryFailures.Count != 0)
                throw new AggregateException("Enemy shutdown cleanup is still incomplete.", retryFailures);
            return;
        }
        var errors = new List<Exception>();
        // A rejected unregistration leaves session state alone. Once ownership has ended, native cleanup failure must
        // not prevent releasing the work lease.
        try { Module.Dispose(); }
        catch (Exception error) when (!Module.IsRegistered) { errors.Add(error); }
        _faulted = true;
        ReleaseNativeWork(errors);
        _disposed = true;
        if (errors.Count != 0) throw new AggregateException("Enemy shutdown cleanup failed.", errors);
    }

    private void ReleaseNativeWork(List<Exception> errors)
    {
        if (_nativeWorkReleased) return;
        try { _releaseNativeWork(); _nativeWorkReleased = true; }
        catch (Exception error) { errors.Add(error); }
    }

}

/// <summary>The callback of the closure-free guard: the module plus the hook's own inputs and outputs, passed by
/// reference so a prefix can hand values back without a capture.</summary>
internal delegate void EnemyGuardCallback<TState>(EnemyModule module, ref TState state);

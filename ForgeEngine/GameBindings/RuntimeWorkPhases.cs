using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ForgeEngine.Framework;
using ForgeCore;

namespace ForgeEngine.GameBindings;

public readonly record struct NativeWorkFrame(long Frame, long WorldEpoch, CancellationToken Cancellation);

/// <summary>Cooperative work over the same CPU allowance used by synchronous diagnostic observers.</summary>
public sealed class DiagnosticWorkBudget
{
    private readonly DiagnosticCpuBudget _cpu;
    private int _remaining;
    public CancellationToken Cancellation { get; internal set; }
    internal DiagnosticWorkBudget(DiagnosticCpuBudget cpu) { _cpu = cpu; }
    internal void Reset(int steps) { _remaining = steps; Cancellation = default; }
    internal bool TryTake()
    {
        if (Cancellation.IsCancellationRequested || _remaining <= 0 || !_cpu.CanContinue) return false;
        _remaining--; return true;
    }
    public bool CanContinue => !Cancellation.IsCancellationRequested && _cpu.HasCooperativeTime;
    /// <summary>The reservation key of the work running now (`work:` + consumer), made once when that work was registered.</summary>
    internal string ReservationKey { get; set; } = "";
    public bool TryReserve(out DiagnosticReservation reservation)
    {
        if (!Cancellation.IsCancellationRequested) return _cpu.TryReserveFor(ReservationKey, out reservation);
        reservation = default; return false;
    }
    internal bool Available => _remaining > 0 && _cpu.CanContinue;
}

// Only Runtime pumps these phases. This is work scheduling, never a second simulation clock or kernel.
internal sealed class NativeWorkScheduler
{
    private sealed record NativeWork(RuntimeWorkLease Lease, int Order, long Sequence, Action<NativeWorkFrame> Run);
    private sealed record DiagnosticWork(RuntimeWorkLease Lease, Func<NativeWorkFrame, DiagnosticWorkBudget, bool> Run)
    {
        internal string ReservationKey { get; } = "work:" + Lease.Consumer;
    }
    private NativeWork[] _native = Array.Empty<NativeWork>();
    private DiagnosticWork[] _diagnostic = Array.Empty<DiagnosticWork>();
    private readonly Func<long> _world;
    private readonly Func<long>? _nativeFrame;
    private readonly Action<string, Exception> _fault;
    private readonly DiagnosticCpuBudget _cpu;
    private readonly DiagnosticWorkBudget _budget;
    private long _sequence, _frame;
    private int _cursor;
    internal NativeWorkScheduler(Func<long> world, Func<long> clock, long slice, Action<string, Exception> fault,
        DiagnosticCpuBudget? cpu = null, Func<long>? nativeFrame = null)
    { _world = world; _fault = fault; _cpu = cpu ?? new(clock, slice); _budget = new(_cpu); _nativeFrame = nativeFrame; }
    private RuntimeWorkLease Lease(string consumer, long? epoch, bool diagnostic)
    {
        if (string.IsNullOrWhiteSpace(consumer) || consumer.Length > 128) throw new ArgumentException("runtime-work-consumer-invalid");
        if (epoch.HasValue && (epoch.Value < 0 || epoch.Value != _world())) throw new InvalidOperationException("runtime-work-world-epoch-stale");
        _native = _native.Where(work => work.Lease.Active(_world())).ToArray();
        _diagnostic = _diagnostic.Where(work => work.Lease.Active(_world())).ToArray();
        var leases = diagnostic ? _diagnostic.Select(work => work.Lease) : _native.Select(work => work.Lease);
        if (leases.Any(lease => lease.Consumer == consumer)) throw new InvalidOperationException("runtime-work-consumer-duplicate");
        if (leases.Count() >= 256) throw new InvalidOperationException("runtime-work-budget");
        return new(consumer, epoch);
    }
    internal RuntimeWorkLease Native(string consumer, long? epoch, int order, Action<NativeWorkFrame> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var lease = Lease(consumer, epoch, false);
        lease.Release = () => _native = _native.Where(work => !ReferenceEquals(work.Lease, lease)).ToArray();
        _native = _native.Append(new NativeWork(lease, order, ++_sequence, callback)).OrderBy(work => work.Order).ThenBy(work => work.Sequence).ToArray();
        return lease;
    }
    internal RuntimeWorkLease Diagnostic(string consumer, long? epoch, Func<NativeWorkFrame, DiagnosticWorkBudget, bool> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var lease = Lease(consumer, epoch, true);
        lease.Release = () => _diagnostic = _diagnostic.Where(work => !ReferenceEquals(work.Lease, lease)).ToArray();
        _diagnostic = _diagnostic.Append(new DiagnosticWork(lease, callback)).ToArray();
        return lease;
    }
    internal void Pump()
    {
        var epoch = _world(); var frame = checked(++_frame);
        _cpu.BeginFrame(_nativeFrame?.Invoke() ?? frame);
        foreach (var work in _native)
        {
            if (_world() != epoch || !work.Lease.Active(epoch)) continue;
            try { work.Run(new(frame, epoch, work.Lease.Cancellation)); }
            // Host work unwinds into the per-frame Guard and remains registered for the next Update.
            catch (Exception error) when (work.Lease.Epoch.HasValue)
            { work.Lease.End("observer-failed"); _fault(work.Lease.Consumer, error); }
        }
        var pending = _diagnostic;
        if (pending.Length == 0) return;
        var budget = _budget; budget.Reset(64);
        for (var visited = 0; visited < pending.Length && budget.Available; visited++)
        {
            var index = _cursor % pending.Length;
            _cursor = (index + 1) % pending.Length;
            var work = pending[index];
            if (_world() != epoch || !work.Lease.Active(epoch)) continue;
            budget.Cancellation = work.Lease.Cancellation; budget.ReservationKey = work.ReservationKey;
            if (!budget.TryTake()) break;
            if (!budget.TryReserve(out var reservation)) break;
            using var charge = reservation;
            try { if (work.Run(new(frame, epoch, work.Lease.Cancellation), budget)) work.Lease.End("completed"); }
            // Only domain diagnostic work is isolated; host exceptions keep their per-frame Guard semantics.
            catch (Exception error) when (work.Lease.Epoch.HasValue)
            { work.Lease.End("observer-failed"); _fault(work.Lease.Consumer, error); }
        }
    }
}

public static class RuntimeWorkPhases
{
    private static readonly NativeWorkScheduler Scheduler = new(
        static () => GameRuntimeBridge.Kernel?.WorldEpoch ?? -1, Stopwatch.GetTimestamp,
        Math.Max(1, Stopwatch.Frequency / 500),
        GameRuntimeBridge.ReportNativeObserverFailure, DiagnosticCpuBudget.Shared, static () => UnityEngine.Time.frameCount);
    static RuntimeWorkPhases()
    {
        DiagnosticCpuBudget.Shared.SetFrameSource(static () => UnityEngine.Time.frameCount);
        // The rundown screen is observed once; the callback ends itself then instead of holding a frame slot for the process.
        RuntimeWorkLease? startup = null;
        startup = Scheduler.Native("forge.runtime.startup-screen", null, 0, _ => { if (StartupScreenObservation.Poll()) startup?.End("completed"); });
    }
    /// <summary>Order ascending, registration order for ties. Runs on Update including loading and pause.</summary>
    public static RuntimeWorkLease Native(string consumer, long worldEpoch, int order, Action<NativeWorkFrame> callback)
        => Scheduler.Native(consumer, worldEpoch, order, callback);
    /// <summary>One unit per invocation, at most 64 domain items and 2 ms measured observer CPU per native frame, shared with synchronous diagnostics.
    /// Mandatory Runtime diagnostics run separately. Return true to complete, false to retain; check budget.CanContinue during cooperative work.
    /// Disposal/world change cancels domain work; a domain callback failure ends only its lease.</summary>
    public static RuntimeWorkLease Diagnostic(string consumer, long worldEpoch, Func<NativeWorkFrame, DiagnosticWorkBudget, bool> callback)
        => Scheduler.Diagnostic(consumer, worldEpoch, callback);
    internal static void Pump() => Scheduler.Pump();
}

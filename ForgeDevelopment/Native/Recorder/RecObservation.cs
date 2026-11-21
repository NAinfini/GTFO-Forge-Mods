using System;
using System.Diagnostics;
using System.Text.Json;

namespace ForgeDevelopment.Native;

// This covers observer work only. A patched game's original body remains observable,
// including legitimate nested calls; reads made by either recorder cannot observe themselves.
internal static class RecObservation
{
    [ThreadStatic] private static bool _active;
    [ThreadStatic] private static bool _readPass;
    [ThreadStatic] private static Action? _validateLease;
    internal static bool Active => _active;
    internal static bool InHook => _active && !_readPass;
    internal static bool TryEnter()
    {
        if (_active) return false;
        _active = true;
        return true;
    }
    internal static bool TryEnterReadPass()
    {
        if (!TryEnter()) return false;
        _readPass = true;
        return true;
    }
    internal static string? NativeReadUnavailable()
    {
        if (InHook) return "unverified-trace-object";
        if (_validateLease == null) return "target-lease-required";
        _validateLease();
        return null;
    }
    internal static IDisposable EnterReadLease(Action validate)
    {
        if (InHook || _validateLease != null) throw new InvalidOperationException("read-lease-boundary");
        _validateLease = validate;
        return new ReadScope();
    }
    internal static T ReadNative<T>(Func<T> read)
    {
        RequireNativeRead();
        return read();
    }
    internal static void RequireNativeRead()
    {
        if (NativeReadUnavailable() is { } reason) throw new InvalidOperationException(reason);
    }
    private sealed class ReadScope : IDisposable
    {
        public void Dispose() => _validateLease = null;
    }
    internal static void Exit() { _active = _readPass = false; }
}

// The recorder reports the Runtime-owned quota; it never replenishes or owns another CPU allowance.
// Before/return scopes never span the observed game's method. An admitted pair always finishes.
internal static class RecTraceBudget
{
    internal const int MillisecondsPerFrame = 2;
    internal static readonly long LimitTicks = Math.Max(1, Stopwatch.Frequency / 500);
    private static volatile bool _enabled;
    internal static bool Active => _enabled;
    private static ForgeCore.DiagnosticBudgetSnapshot _started;

    internal static void Start()
    {
        _started = ForgeCore.DiagnosticCpuBudget.Shared.Snapshot;
        _enabled = true;
    }
    internal static void Stop() => _enabled = false;
    internal static void BeginFrame(int frame)
    {
        ForgeCore.DiagnosticCpuBudget.Shared.BeginFrame(frame);
    }
    internal static bool TryAdmit()
    {
        if (!_enabled) return true;
        if (!ForgeCore.DiagnosticCpuBudget.Shared.TryReserve(out var reservation)) return false;
        reservation.Dispose(); return true;
    }
    internal static Scope Measure() => new(_enabled ? ForgeCore.DiagnosticCpuBudget.Shared.MeasureRequired() : default);
    internal struct Scope : IDisposable
    {
        private ForgeCore.DiagnosticReservation _reservation;
        internal Scope(ForgeCore.DiagnosticReservation reservation) { _reservation = reservation; }
        public void Dispose() => _reservation.Dispose();
    }
    internal static void Charge(long ticks) => ForgeCore.DiagnosticCpuBudget.Shared.Charge(ticks);
    internal static void WriteTo(Utf8JsonWriter json)
    {
        var budget = ForgeCore.DiagnosticCpuBudget.Shared.Snapshot;
        double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
        json.WriteStartObject("traceBudget");
        json.WriteNumber("millisecondsPerFrame", MillisecondsPerFrame);
        json.WriteString("owner", "runtime-diagnostics");
        json.WriteNumber("measuredMs", Ms(budget.MeasuredTicks - _started.MeasuredTicks));
        json.WriteNumber("maxFrameMs", Ms(budget.MaximumFrameTicks));
        json.WriteNumber("maxStepMs", Ms(budget.MaximumStepTicks));
        json.WriteNumber("dropped", budget.Dropped - _started.Dropped);
        json.WriteNumber("budgetHits", budget.BudgetHits - _started.BudgetHits);
        json.WriteNumber("overrunFrames", budget.OverrunFrames - _started.OverrunFrames);
        json.WriteBoolean("incomplete", budget.Dropped != _started.Dropped || budget.OverrunFrames != _started.OverrunFrames);
        json.WriteEndObject();
    }
}

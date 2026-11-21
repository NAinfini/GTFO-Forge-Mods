using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;

namespace ForgeDevelopment.Native;

/// <summary>The native signatures the performance collector can time without changing the original call. Development only: a raw\n/// detour on a native function entry (Dobby, through BepInEx's INativeDetour), installed only after the module's file hash and the\n/// entry's live bytes prove it is the unmodified image (so it never lands on an entry another detour already rewrote).</summary>
internal enum NativeFunctionAbi { Void, InstanceMethod, Int32, Pointer }

/// <summary>One raw function timing declaration.</summary>
internal sealed record NativeFunctionTiming(string Writer, string Module, string ModuleSha256, IntPtr Entry,
    NativeFunctionAbi Abi, NativeTimingSampleObserver Observer);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void NativeTimingVoidCall();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void NativeTimingInstanceCall(IntPtr instance, IntPtr method);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void NativeTimingInt32Call(int value);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void NativeTimingPointerCall(IntPtr value);

internal static class NativeFunctionTimings
{
    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, NativeFunctionTimingInstallation> Entries = new();
    // A callback can already have crossed the native transfer when Undo runs but not yet reached managed Retain.
    // Keep its delegates and restored-entry call alive for the process, after clearing the observer itself.
    private static readonly List<NativeFunctionTimingInstallation> Retired = new();

    internal static void Configure() => NativeTimingRegistration.Register = (module, hash, entry, sample, abi)
        => Declare(new("ForgeDevelopment.Performance", module, hash, entry,
            abi switch
            {
                0 => NativeFunctionAbi.Void,
                1 => NativeFunctionAbi.InstanceMethod,
                2 => NativeFunctionAbi.Int32,
                3 => NativeFunctionAbi.Pointer,
                _ => throw new ArgumentOutOfRangeException(nameof(abi))
            }, sample));

    /// <summary>Install one timing detour after proving its module and original entry bytes; disposing the lease takes it off.</summary>
    internal static IDisposable Declare(NativeFunctionTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        if (string.IsNullOrWhiteSpace(timing.Writer) || timing.Writer.Length > 128
            || string.IsNullOrWhiteSpace(timing.Module) || timing.Module.Length > 260
            || timing.Entry == IntPtr.Zero || timing.Observer == null || !Enum.IsDefined(typeof(NativeFunctionAbi), timing.Abi))
            throw new NotSupportedException(Refusal("native-hook-function-declaration-invalid", timing));
        lock (Gate)
        {
            // Ownership is decided before fingerprint I/O, so an occupied entry never leaks alternate evidence.
            if (Entries.ContainsKey(timing.Entry)) throw new NotSupportedException(Refusal("native-hook-function-entry-owned", timing));
            if (FingerprintRejection(timing) is { } fingerprint)
                throw new NotSupportedException(Refusal("native-hook-function-fingerprint-mismatch detail=" + fingerprint, timing));
            var installation = new NativeFunctionTimingInstallation(timing.Writer, timing.Entry, timing.Observer);
            Entries.Add(timing.Entry, installation);
            try { installation.Install(timing); }
            catch (Exception original)
            {
                // Prepare never writes the entry. Once Apply starts, failure is conservatively treated as committed.
                if (!installation.ApplyAttempted) Entries.Remove(timing.Entry);
                else
                {
                    try { Release(installation, timing.Entry); }
                    catch (Exception cleanup) { throw new AggregateException("native-hook-function-cleanup-pending", original, cleanup); }
                }
                throw;
            }
            return new Lease(installation.Release);
        }
    }

    internal static void Release(NativeFunctionTimingInstallation installation, IntPtr entry)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(entry, out var current)) return;
            if (!ReferenceEquals(current, installation)) throw new InvalidOperationException("native-hook-function-entry-owner-changed");
            installation.Stop();
            Retired.Add(installation);
            Entries.Remove(entry);
        }
    }

    // Proves the module image and the entry bytes are unmodified before a detour lands on them.
    private static string? FingerprintRejection(NativeFunctionTiming timing)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return "requires-windows-x64";
            var module = NativeModuleFingerprint.Verified(timing.Module, timing.ModuleSha256, out var note);
            if (module == IntPtr.Zero) return note;
            return NativeModuleFingerprint.OriginalCode(module, timing.Entry) ? null : "entry-bytes-changed";
        }
        catch (Exception error) { return "verification-" + error.GetType().Name; }
    }

    internal static void ObserverFailed(string writer, Exception error) => Plugin.PluginLog.LogError("native timing observer " + writer + " failed and was removed: " + error);

    private static string Refusal(string reason, NativeFunctionTiming timing)
        => reason + " declaration=" + (string.IsNullOrWhiteSpace(timing.Writer) ? "<invalid>" : timing.Writer)
            + " module=" + (string.IsNullOrWhiteSpace(timing.Module) ? "<invalid>" : timing.Module)
            + " entry=0x" + unchecked((ulong)timing.Entry.ToInt64()).ToString("x");

    private sealed class Lease : IDisposable
    {
        private Action? _release;
        internal Lease(Action release) => _release = release;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
// The same lifecycle runs in production and offline tests. Tests replace only module/native-detour I/O.
internal sealed class NativeFunctionTimingInstallation
{
    [ThreadStatic] private static NativeFunctionTimingInstallation? _executing;
    private readonly string _writer;
    private readonly IntPtr _entry;
    private NativeTimingSampleObserver? _observer;
    private DobbyFunctionTimingDetour? _detour;
    private int _inFlight, _draining, _unhooked, _disposed, _applyAttempted;

    internal NativeFunctionTimingInstallation(string writer, IntPtr entry, NativeTimingSampleObserver observer)
    { _writer = writer; _entry = entry; _observer = observer; }

    internal bool ApplyAttempted => Volatile.Read(ref _applyAttempted) != 0;
    internal string Writer => _writer;

    internal void Install(NativeFunctionTiming definition)
    {
        _detour = definition.Abi switch
        {
            NativeFunctionAbi.Void => DobbyFunctionTimingDetour.Void(definition.Entry, new(InvokeVoid)),
            NativeFunctionAbi.InstanceMethod => DobbyFunctionTimingDetour.Instance(definition.Entry, new(InvokeInstance)),
            NativeFunctionAbi.Int32 => DobbyFunctionTimingDetour.Int32(definition.Entry, new(InvokeInt32)),
            _ => DobbyFunctionTimingDetour.Pointer(definition.Entry, new(InvokePointer))
        };
        Volatile.Write(ref _applyAttempted, 1);
        try { _detour.Apply(); }
        catch { Interlocked.Exchange(ref _observer, null); throw; }
    }

    internal void Release()
        => NativeFunctionTimings.Release(this, _entry);

    private bool Retain()
    {
        Interlocked.Increment(ref _inFlight);
        if (Volatile.Read(ref _draining) == 0) return true;
        Interlocked.Decrement(ref _inFlight);
        while (Volatile.Read(ref _draining) != 0 && Volatile.Read(ref _unhooked) == 0) Thread.SpinWait(16);
        return false;
    }

    private NativeTimingSampleObserver? Observe(IntPtr arg0, out long token)
    {
        token = 0;
        var observer = Volatile.Read(ref _observer);
        if (observer == null) return null;
        try { token = observer.Enter(arg0); return observer; }
        catch (Exception error) { Remove(observer, error); return null; }
    }

    private void Finish(NativeTimingSampleObserver observer, long token, long originalStart, long originalEnd)
    {
        try { observer.Exit(token, originalStart, originalEnd); }
        catch (Exception error) { Remove(observer, error); }
    }

    private void Remove(NativeTimingSampleObserver observer, Exception error)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _observer, null, observer), observer))
            NativeFunctionTimings.ObserverFailed(_writer, error);
    }

    internal void InvokeVoid()
    {
        var detour = _detour!;
        if (!Retain()) { if (Volatile.Read(ref _unhooked) != 0) detour.CallRestoredVoid(); else detour.CallOriginalVoid(); return; }
        try
        {
            var previous = _executing; _executing = this;
            try
            {
                var observer = Observe(IntPtr.Zero, out var token);
                if (observer == null) { detour.CallOriginalVoid(); return; }
                var start = Stopwatch.GetTimestamp();
                try { detour.CallOriginalVoid(); }
                finally { Finish(observer, token, start, Stopwatch.GetTimestamp()); }
            }
            finally { _executing = previous; }
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    internal void InvokeInstance(IntPtr instance, IntPtr method)
    {
        var detour = _detour!;
        if (!Retain()) { if (Volatile.Read(ref _unhooked) != 0) detour.CallRestoredInstance(instance, method); else detour.CallOriginalInstance(instance, method); return; }
        try
        {
            var previous = _executing; _executing = this;
            try
            {
                var observer = Observe(instance, out var token);
                if (observer == null) { detour.CallOriginalInstance(instance, method); return; }
                var start = Stopwatch.GetTimestamp();
                try { detour.CallOriginalInstance(instance, method); }
                finally { Finish(observer, token, start, Stopwatch.GetTimestamp()); }
            }
            finally { _executing = previous; }
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    internal void InvokeInt32(int value)
    {
        var detour = _detour!;
        if (!Retain()) { if (Volatile.Read(ref _unhooked) != 0) detour.CallRestoredInt32(value); else detour.CallOriginalInt32(value); return; }
        try
        {
            var previous = _executing; _executing = this;
            try
            {
                var observer = Observe(new IntPtr(value), out var token);
                if (observer == null) { detour.CallOriginalInt32(value); return; }
                var start = Stopwatch.GetTimestamp();
                try { detour.CallOriginalInt32(value); }
                finally { Finish(observer, token, start, Stopwatch.GetTimestamp()); }
            }
            finally { _executing = previous; }
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    internal void InvokePointer(IntPtr value)
    {
        var detour = _detour!;
        if (!Retain()) { if (Volatile.Read(ref _unhooked) != 0) detour.CallRestoredPointer(value); else detour.CallOriginalPointer(value); return; }
        try
        {
            var previous = _executing; _executing = this;
            try
            {
                var observer = Observe(value, out var token);
                if (observer == null) { detour.CallOriginalPointer(value); return; }
                var start = Stopwatch.GetTimestamp();
                try { detour.CallOriginalPointer(value); }
                finally { Finish(observer, token, start, Stopwatch.GetTimestamp()); }
            }
            finally { _executing = previous; }
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    internal void Stop()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (ReferenceEquals(_executing, this)) throw new InvalidOperationException("native-hook-function-cleanup-from-callback");
        Volatile.Write(ref _draining, 1);
        var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (Volatile.Read(ref _inFlight) != 0 && Stopwatch.GetTimestamp() < until) Thread.Sleep(1);
        if (Volatile.Read(ref _inFlight) != 0)
        {
            Volatile.Write(ref _draining, 0);
            throw new InvalidOperationException("native-hook-function-cleanup-pending");
        }
        if (Volatile.Read(ref _unhooked) == 0)
        {
            try { _detour!.Undo(); }
            catch { Volatile.Write(ref _draining, 0); throw; }
            Volatile.Write(ref _unhooked, 1);
            Interlocked.Exchange(ref _observer, null);
        }
        _detour!.Dispose();
        Volatile.Write(ref _disposed, 1);
    }
}

internal sealed class DobbyFunctionTimingDetour
{
    private readonly INativeDetour _hook;
    private readonly Delegate _callback;
    private readonly NativeTimingVoidCall? _voidOriginal, _voidRestored;
    private readonly NativeTimingInstanceCall? _instanceOriginal, _instanceRestored;
    private readonly NativeTimingInt32Call? _int32Original, _int32Restored;
    private readonly NativeTimingPointerCall? _pointerOriginal, _pointerRestored;

    private DobbyFunctionTimingDetour(INativeDetour hook, Delegate callback, Delegate original, Delegate restored)
    {
        _hook = hook; _callback = callback;
        _voidOriginal = original as NativeTimingVoidCall; _voidRestored = restored as NativeTimingVoidCall;
        _instanceOriginal = original as NativeTimingInstanceCall; _instanceRestored = restored as NativeTimingInstanceCall;
        _int32Original = original as NativeTimingInt32Call; _int32Restored = restored as NativeTimingInt32Call;
        _pointerOriginal = original as NativeTimingPointerCall; _pointerRestored = restored as NativeTimingPointerCall;
    }

    internal static DobbyFunctionTimingDetour Void(IntPtr entry, NativeTimingVoidCall callback)
        => Create(entry, callback);
    internal static DobbyFunctionTimingDetour Instance(IntPtr entry, NativeTimingInstanceCall callback)
        => Create(entry, callback);
    internal static DobbyFunctionTimingDetour Int32(IntPtr entry, NativeTimingInt32Call callback)
        => Create(entry, callback);
    internal static DobbyFunctionTimingDetour Pointer(IntPtr entry, NativeTimingPointerCall callback)
        => Create(entry, callback);

    private static DobbyFunctionTimingDetour Create<T>(IntPtr entry, T callback) where T : Delegate
    {
        var hook = INativeDetour.Create(entry, callback);
        try { return new(hook, callback, hook.GenerateTrampoline<T>(), Marshal.GetDelegateForFunctionPointer<T>(entry)); }
        catch (Exception preparation)
        {
            try { hook.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Native timing trampoline preparation and cleanup failed.", preparation, cleanup); }
            throw;
        }
    }

    public void Apply() => _hook.Apply();
    public void Undo() => _hook.Undo();
    public void Dispose() { _hook.Dispose(); GC.KeepAlive(_callback); }
    public void CallOriginalVoid() => _voidOriginal!();
    public void CallRestoredVoid() => _voidRestored!();
    public void CallOriginalInstance(IntPtr instance, IntPtr method) => _instanceOriginal!(instance, method);
    public void CallRestoredInstance(IntPtr instance, IntPtr method) => _instanceRestored!(instance, method);
    public void CallOriginalInt32(int value) => _int32Original!(value);
    public void CallRestoredInt32(int value) => _int32Restored!(value);
    public void CallOriginalPointer(IntPtr value) => _pointerOriginal!(value);
    public void CallRestoredPointer(IntPtr value) => _pointerRestored!(value);
}

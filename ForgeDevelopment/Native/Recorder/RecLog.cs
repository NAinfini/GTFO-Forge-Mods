using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using UnityEngine;
using BepInExLogger = BepInEx.Logging.Logger;

namespace ForgeDevelopment.Native;

/// <summary>
/// Captures what the game, Unity and BepInEx say into the `log` channel. Three sources are joined here because they
/// see different things: Unity's threaded callback is the only one that sees engine errors and exceptions, BepInEx's
/// listener sees every plugin's line at its own level, and the process handler sees a fault that reaches the top.
/// A record written by the recorder's own listener is skipped, so a session cannot log itself into a loop.
/// </summary>
internal static class RecLog
{
    private static readonly object Gate = new();
    /// <summary>Unity's own level, kept as text because Unity's enum is not stable across versions.</summary>
    private static Application.LogCallback? _unityCallback;
    private static uint _unityCallbackRoot;
    private static bool _unityDetachPending;
    private static Listener? _listener;
    private static UnhandledExceptionEventHandler? _unhandled;
    private static volatile bool _listening;
    private static bool _changing;
    private static long _generation;
    private static int _activeWrites;

    [ThreadStatic] private static bool _inside;

    /// <summary>Owned resources, including a stopped subscription whose cleanup must be retried.</summary>
    internal static bool CleanupPending
    {
        get { using (RecGate.Enter(Gate, "recorder.lock-timeout")) return HasResources; }
    }

    private static bool HasResources => _unityCallback != null || _unityCallbackRoot != 0 ||
        _unityDetachPending || _listener != null || _unhandled != null || ActiveWrites != 0;

    // A full-fence read, paired with the writer's interlocked announcement in Write.
    private static int ActiveWrites => Interlocked.CompareExchange(ref _activeWrites, 0, 0);

    internal static void Start()
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (_changing) throw new InvalidOperationException("Recorder log lifecycle is already changing.");
            if (_listening) return;
            if (HasResources) throw new InvalidOperationException("Recorder log cleanup is still pending; retry Stop before Start.");
            _changing = true;
            try
            {
                var generation = Interlocked.Increment(ref _generation);
                _unityCallback = (Application.LogCallback)(Action<string, string, LogType>)
                    ((message, stack, level) => UnityLog(generation, message, stack, level));
                _unityCallbackRoot = IL2CPP.il2cpp_gchandle_new(_unityCallback.Pointer, false);
                if (_unityCallbackRoot == 0) throw new InvalidOperationException("Could not root the recorder Unity log callback.");
                // Own an attempted subscription even if its accessor attaches and then throws.
                _unityDetachPending = true;
                Application.add_logMessageReceivedThreaded(_unityCallback);
                _listener = new Listener(generation);
                BepInExLogger.Listeners.Add(_listener);
                _unhandled = (sender, args) => OnUnhandled(generation, sender, args);
                AppDomain.CurrentDomain.UnhandledException += _unhandled;
                _listening = true;
            }
            catch (Exception error)
            {
                _listening = false;
                var errors = new List<Exception> { error };
                Cleanup(errors);
                Report(errors);
                throw new AggregateException("Recorder log startup failed.", errors);
            }
            finally { _changing = false; }
        }
    }

    /// <summary>Closes admission before detaching. Failures preserve ownership for a later Stop and are
    /// propagated after every cleanup was attempted, so the runtime cannot retire a still-owned stage.</summary>
    internal static void Stop()
    {
        // Threaded callbacks must stop entering even while another lifecycle operation owns Gate. The fence orders
        // this close before the active-write check below (a writer announces itself before re-checking it).
        _listening = false;
        Thread.MemoryBarrier();
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (_changing) throw new InvalidOperationException("Recorder log lifecycle is already changing.");
            _listening = false;
            _changing = true;
            try
            {
                var errors = new List<Exception>();
                Cleanup(errors);
                // Never wait on a callback: Stop may itself have been called by one, or by a thread
                // holding the session's composition lock. Retain ownership for a subsequent Stop.
                if (ActiveWrites != 0) errors.Add(new InvalidOperationException(
                    "Recorder log callbacks are still composing; retry Stop after they return."));
                if (errors.Count == 0) return;
                Report(errors);
                throw new AggregateException("Recorder log cleanup remains incomplete.", errors);
            }
            finally { _changing = false; }
        }
    }

    private static void Cleanup(List<Exception> errors)
    {
        if (_unityDetachPending)
        {
            try
            {
                // The generated remove accessor casts a null Delegate.Remove result when we are the
                // last subscriber. Match RuntimeDiagnostics' evidenced field update, preserving peers.
                var remaining = Il2CppSystem.Delegate.Remove(Application.s_LogCallbackHandlerThreaded, _unityCallback);
                Application.s_LogCallbackHandlerThreaded = remaining == null ? null : remaining.Cast<Application.LogCallback>();
                _unityDetachPending = false;
            }
            catch (Exception error) { errors.Add(new InvalidOperationException("Could not detach the Unity log callback.", error)); }
        }
        if (!_unityDetachPending && ActiveWrites == 0 && _unityCallbackRoot != 0)
        {
            try
            {
                IL2CPP.il2cpp_gchandle_free(_unityCallbackRoot);
                _unityCallbackRoot = 0;
            }
            catch (Exception error) { errors.Add(new InvalidOperationException("Could not release the Unity log callback root.", error)); }
        }
        if (!_unityDetachPending && _unityCallbackRoot == 0) _unityCallback = null;
        if (_listener != null)
        {
            try { BepInExLogger.Listeners.Remove(_listener); _listener = null; }
            catch (Exception error) { errors.Add(new InvalidOperationException("Could not detach the BepInEx log listener.", error)); }
        }
        if (_unhandled != null)
        {
            try { AppDomain.CurrentDomain.UnhandledException -= _unhandled; _unhandled = null; }
            catch (Exception error) { errors.Add(new InvalidOperationException("Could not detach the process exception handler.", error)); }
        }
    }

    private static void Report(List<Exception> errors)
    {
        // Reporting runs after cleanup and cannot replace its failure or prevent another release.
        var failures = errors.Count;
        for (var i = 0; i < failures; i++)
        {
            try { RecDiag.Error("Forge recorder log lifecycle failed: " + errors[i]); }
            catch (Exception error) { errors.Add(new InvalidOperationException("Could not report recorder log lifecycle failure.", error)); }
        }
    }

    /// <summary>Unity reports engine errors and exceptions on whatever thread produced them, which is why the
    /// threaded callback is used and why nothing here touches a Unity object.</summary>
    private static void UnityLog(long generation, string message, string stack, LogType level)
    {
        if (!Accepts(generation)) return;
        Write(generation, "unity", level.ToString(), message, stack, null);
    }

    private static void OnUnhandled(long generation, object sender, UnhandledExceptionEventArgs args)
    {
        if (!Accepts(generation)) return;
        var error = args.ExceptionObject as Exception;
        Write(generation, "process", args.IsTerminating ? "terminating" : "unhandled", error?.Message ?? args.ExceptionObject?.ToString() ?? "unknown",
            error?.ToString() ?? "", error?.GetType().FullName);
    }

    private static bool Accepts(long generation) => _listening && !_inside && generation == Volatile.Read(ref _generation);

    // Runs on whichever thread logged, including the game thread inside Unity's callback. It takes no lock: the
    // announcement is interlocked and the session queue is lock-free, so a log line can never block or throw because
    // another thread is logging or the recorder is writing.
    private static void Write(long generation, string source, string level, string message, string stack, string? type)
    {
        Interlocked.Increment(ref _activeWrites);
        try
        {
            // Active ownership prevents Stop from releasing the callback, and a restart from accepting this old
            // generation, while this line is composed.
            if (!Accepts(generation)) return;
            _inside = true;
            RecSession.Write("log", "line", json =>
            {
                json.WriteString("source", source);
                json.WriteString("level", level);
                if (type != null) json.WriteString("exception", type);
                json.WriteString("message", RecReflect.Truncate(message ?? "", 4096));
                if (!string.IsNullOrEmpty(stack)) json.WriteString("stack", RecReflect.Truncate(stack!, 16384));
                json.WriteNumber("thread", Environment.CurrentManagedThreadId);
            });
        }
        finally
        {
            _inside = false;
            Interlocked.Decrement(ref _activeWrites);
        }
    }

    /// <summary>The BepInEx listener. It records everything at Info and above: the user asked for every point that
    /// could ever matter, and a session with a smaller log is a session that cannot answer a later question.</summary>
    private sealed class Listener : ILogListener
    {
        private readonly long _generation;
        internal Listener(long generation) { _generation = generation; }
        public LogLevel LogLevelFilter => LogLevel.Info | LogLevel.Warning | LogLevel.Error | LogLevel.Fatal | LogLevel.Message;

        public void LogEvent(object sender, LogEventArgs args)
        {
            if (!Accepts(_generation)) return;
            if ((args.Level & LogLevelFilter) == 0) return;
            var text = args.Data?.ToString() ?? "";
            if (text.Length == 0) return;
            var source = args.Source?.SourceName ?? "unknown";
            Write(_generation, "bepinex", args.Level.ToString(), source + ": " + text, "", null);
        }

        public void Dispose() { }
    }
}

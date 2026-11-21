using System;
using System.Threading;

namespace ForgeDevelopment.Native;

/// <summary>Diagnostic producers never wait for another thread. Only a background owner may
/// request a short bounded retry; an unavailable gate is explicit incomplete evidence.</summary>
internal readonly struct RecGate : IDisposable
{
    private readonly object _gate;
    internal static int GameThreadId { get; set; }
    internal static void UseGameThread() => GameThreadId = Environment.CurrentManagedThreadId;
    private RecGate(object gate) { _gate = gate; }
    internal static RecGate Enter(object gate, string code, int milliseconds = -1)
    {
        if (milliseconds < 0) milliseconds = Environment.CurrentManagedThreadId == GameThreadId ? 0 : 5000;
        if (!Monitor.TryEnter(gate, milliseconds)) throw new TimeoutException(code);
        return new RecGate(gate);
    }
    internal static RecGate Writer(object gate)
    {
        // The dedicated recorder owner may wait for an in-flight producer. It is never the game thread.
        if (!Thread.CurrentThread.IsBackground) throw new InvalidOperationException("recorder.writer-thread-required");
        Monitor.Enter(gate);
        return new RecGate(gate);
    }
    public void Dispose() => Monitor.Exit(_gate);
}

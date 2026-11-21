using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeDevelopment.Native;

// Real installation attempts, including an attempt that throws after changing the backend,
// remain owned until removal succeeds. No native/Harmony dependency: exercised offline.
internal sealed class CaptureHookLifecycle
{
    private sealed record Attempt(string Name, bool Bridge, Action Remove);
    private readonly List<Attempt> _attempts = new();
    private int? _thread;
    private bool _installing;
    private volatile bool _installed;
    internal bool Installed => _installed;
    internal bool HasPending => _attempts.Count != 0;
    internal string? Fault { get; private set; }
    internal IReadOnlyList<string> Pending => _attempts.Select(entry => entry.Name).ToArray();

    internal void Install(Action install)
    {
        CheckThread();
        if (Installed) return;
        if (HasPending) throw new InvalidOperationException("Capture cleanup is pending: " + Fault);
        Fault = null;
        _installing = true;
        try { install(); _installed = true; }
        catch (Exception error)
        {
            _installing = false;
            Stop();
            Fault = error.Message + (Fault == null ? "" : "; cleanup: " + Fault);
            throw new InvalidOperationException("Capture group installation failed: " + Fault, error);
        }
        finally { _installing = false; }
    }

    internal void AttemptInstall(string name, Action install, Action remove, bool bridge = false)
    {
        CheckThread();
        if (!_installing) throw new InvalidOperationException("Capture hooks need an installation transaction.");
        _attempts.Add(new(name, bridge, remove)); // Before install: the failing attempt may own a detour.
        install();
    }

    internal bool Stop()
    {
        CheckThread();
        _installed = false;
        var errors = new List<string>();
        Remove(false);
        // A bridge must remain available if removing a target failed and a retry will rebuild it.
        if (!_attempts.Any(entry => !entry.Bridge)) Remove(true);
        Fault = errors.Count == 0 ? null : string.Join("; ", errors);
        return !HasPending;

        void Remove(bool bridge)
        {
            for (var index = _attempts.Count - 1; index >= 0; index--)
            {
                var entry = _attempts[index];
                if (entry.Bridge != bridge) continue;
                try { entry.Remove(); _attempts.RemoveAt(index); }
                catch (Exception error) { errors.Add(entry.Name + ": " + error.Message); }
            }
        }
    }

    private void CheckThread()
    {
        var current = Environment.CurrentManagedThreadId;
        _thread ??= current;
        if (_thread != current) throw new InvalidOperationException("Capture hook lifecycle must remain on its owning main thread.");
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ForgeDevelopment.Native;

// Ownership only. The backend must use an already-reviewed adapter for this exact
// signature. This class neither generates a bridge nor invokes the original method.
internal sealed class DevelopmentHookCoordinator
{
    // Prepare must not mutate hooks. Install must throw unless installation AND its
    // provenance receipt are verified. Uninstall must also undo a partially failed Install.
    internal sealed record Installation(Action Install, Action Uninstall, bool RetainUntilStopAll = false);
    // Contexts are adapter-owned read-only observations. There is no return value or
    // ref/out channel through which this dispatcher can replace game results/errors.
    internal sealed record Observer(Func<object?, object?>? Before = null,
        Action<object?, object?>? After = null, Action<object?, object?>? Finally = null);
    internal sealed record Failure(MethodBase Target, string Consumer, string Phase, Exception Error);
    internal sealed record Status(MethodBase Target, bool Installed, int Consumers, Exception? Fault, bool CleanupPending);

    internal sealed class Entry
    {
        internal readonly MethodBase Target;
        internal readonly Dictionary<string, Subscription> Consumers = new(StringComparer.Ordinal);
        internal Installation? Installation;
        internal bool Installed;
        internal bool NeedsCleanup;
        internal Exception? Fault;
        internal Entry(MethodBase target) { Target = target; }
    }
    internal sealed class Subscription
    {
        internal readonly string Name;
        internal readonly Observer Observer;
        internal int References = 1;
        internal bool Active = true;
        internal Subscription(string name, Observer observer) { Name = name; Observer = observer; }
    }
    private readonly object _gate = new();
    private readonly Func<MethodBase, bool> _supports;
    private readonly Func<MethodBase, Installation> _prepare;
    private readonly Action<Failure>? _report;
    private readonly Dictionary<MethodBase, Entry> _entries = new();
    private readonly List<Failure> _failures = new();
    // StopAll hands back what failed during that call; the list is bounded because a throwing observer can fail on every game call.
    private void Record(Failure failure) { if (_failures.Count < 256) _failures.Add(failure); }
    internal DevelopmentHookCoordinator(Func<MethodBase, bool> supports, Func<MethodBase, Installation> prepare, Action<Failure>? report = null)
    { _supports = supports; _prepare = prepare; _report = report; }
    internal Status[] Inspect() { lock (_gate) return _entries.Values.Select(e => new Status(e.Target, e.Installed, e.Consumers.Count, e.Fault, e.NeedsCleanup)).ToArray(); }

    // A backend transaction that cannot restore its original physical set must
    // retire the current observers while retaining a real cleanup obligation.
    internal void MarkFault(MethodBase target, Exception error, bool cleanupPending)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(target, out var entry)) throw new InvalidOperationException("Unknown hook target.", error);
            foreach (var subscription in entry.Consumers.Values) subscription.Active = false;
            entry.Consumers.Clear();
            entry.Fault = error;
            entry.NeedsCleanup = cleanupPending;
            Record(new Failure(target, "", "reconfigure", error));
        }
    }

    internal Lease Acquire(MethodBase target, string consumer, Observer observer)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(observer);
        if (string.IsNullOrWhiteSpace(consumer)) throw new ArgumentException("A consumer identity is required.", nameof(consumer));
        lock (_gate)
        {
            if (_entries.TryGetValue(target, out var entry))
            {
                if (entry.Fault != null) throw new InvalidOperationException("Hook transition failed; explicit recovery evidence is required before retry.", entry.Fault);
                if (entry.Consumers.TryGetValue(consumer, out var existing))
                {
                    if (!ReferenceEquals(existing.Observer, observer)) throw new InvalidOperationException("An active consumer cannot change its observer.");
                    existing.References++;
                    return new Lease(this, entry, existing);
                }
            }
            else
            {
                if (!_supports(target)) throw new NotSupportedException("No reviewed hook adapter for " + target);
                entry = new Entry(target);
                _entries.Add(target, entry);
            }
            var subscription = new Subscription(consumer, observer);
            entry.Consumers.Add(consumer, subscription);
            if (!entry.Installed)
            {
                try
                {
                    entry.Installation = _prepare(target) ?? throw new InvalidOperationException("Missing hook installation.");
                    entry.NeedsCleanup = true;
                    entry.Installation.Install();
                    entry.Installed = true;
                }
                catch (Exception error)
                {
                    subscription.Active = false;
                    entry.Consumers.Remove(consumer);
                    Exception failure = error;
                    if (entry.Installation != null)
                    {
                        try { entry.Installation.Uninstall(); entry.NeedsCleanup = false; }
                        catch (Exception rollback) { failure = new AggregateException("Hook install and rollback failed.", error, rollback); }
                    }
                    // Even successful rollback does not authorize an implicit retry of
                    // a native failure. Retain the failed generation and its evidence.
                    entry.Fault = failure;
                    Record(new Failure(target, consumer, "install", failure));
                    throw new InvalidOperationException("Hook installation failed.", failure);
                }
            }
            return new Lease(this, entry, subscription);
        }
    }

    internal sealed class Lease : IDisposable
    {
        private DevelopmentHookCoordinator? _owner;
        private readonly Entry _entry;
        private readonly Subscription _subscription;
        internal Lease(DevelopmentHookCoordinator owner, Entry entry, Subscription subscription)
        { _owner = owner; _entry = entry; _subscription = subscription; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_entry, _subscription);
    }
    private void Release(Entry entry, Subscription subscription)
    {
        lock (_gate)
        {
            if (!subscription.Active || !entry.Consumers.TryGetValue(subscription.Name, out var current)
                || !ReferenceEquals(current, subscription)) return;
            if (--subscription.References != 0) return;
            subscription.Active = false;
            entry.Consumers.Remove(subscription.Name);
            if (entry.Consumers.Count != 0) return;
            // IL2CPP unpatching does not prove that the native entry is restored.
            // Keep its verified physical installation, with no logical observers,
            // until the backend's explicit shutdown instead of detouring it again.
            if (entry.Installation!.RetainUntilStopAll) return;
            try
            {
                entry.Installation!.Uninstall();
                entry.Installed = false;
                entry.NeedsCleanup = false;
                _entries.Remove(entry.Target);
            }
            catch (Exception error)
            {
                // Installed means removal is not verified. Retain the installation,
                // error and target, and never silently try to install over it again.
                entry.Fault = error;
                Record(new Failure(entry.Target, subscription.Name, "uninstall", error));
                throw;
            }
        }
    }
    internal Invocation? Capture(MethodBase target)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(target, out var entry) || !entry.Installed || entry.Fault != null
                || entry.Consumers.Count == 0) return null;
            return new Invocation(this, target, entry.Consumers.Values.ToArray());
        }
    }
    private bool Active(Subscription subscription) { lock (_gate) return subscription.Active; }
    private void Note(MethodBase method, Subscription subscription, string phase, Exception error)
    {
        var failure = new Failure(method, subscription.Name, phase, error);
        lock (_gate) Record(failure);
        try { _report?.Invoke(failure); } catch (Exception) { /* Reporting cannot alter a game call. */ }
    }
    // Explicit cleanup retry only. A retired fault remains terminal even when
    // cleanup succeeds: this API never converts failed native evidence to admission.
    internal bool RetryCleanup(MethodBase target)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(target, out var entry)) return true;
            if (entry.Consumers.Count != 0) throw new InvalidOperationException("Active consumers still own this hook.");
            if (!entry.NeedsCleanup) return true;
            try
            {
                entry.Installation!.Uninstall();
                entry.NeedsCleanup = false; entry.Installed = false;
                return true;
            }
            catch (Exception error)
            {
                entry.Fault = new AggregateException("Hook cleanup retry failed.", entry.Fault ?? error, error);
                Record(new Failure(target, "", "cleanup", error));
                return false;
            }
        }
    }
    internal Failure[] StopAll()
    {
        lock (_gate)
        {
            _failures.Clear();
            foreach (var entry in _entries.Values.ToArray())
            {
                foreach (var subscription in entry.Consumers.Values) subscription.Active = false;
                entry.Consumers.Clear();
                if (RetryCleanup(entry.Target) && entry.Fault == null) _entries.Remove(entry.Target);
            }
            return _failures.ToArray();
        }
    }

    internal sealed class Invocation
    {
        private sealed class Frame
        {
            internal readonly Subscription Subscription;
            internal object? State;
            internal bool Began;
            internal Frame(Subscription subscription) { Subscription = subscription; }
        }
        private readonly DevelopmentHookCoordinator _owner;
        private readonly MethodBase _target;
        private readonly Frame[] _frames;
        private bool _began, _returned, _finished;
        internal Invocation(DevelopmentHookCoordinator owner, MethodBase target, Subscription[] subscriptions)
        { _owner = owner; _target = target; _frames = subscriptions.Select(s => new Frame(s)).ToArray(); }
        // One invocation belongs to one calling thread, like Harmony __state. Each
        // callback gets its own state; nested invocations never share a state slot.
        internal void Begin(object? context)
        {
            if (_began || _finished) return;
            _began = true;
            foreach (var frame in _frames)
            {
                if (!_owner.Active(frame.Subscription)) continue;
                frame.Began = true;
                try { frame.State = frame.Subscription.Observer.Before?.Invoke(context); }
                catch (Exception error) { _owner.Note(_target, frame.Subscription, "before", error); }
            }
        }
        internal void Returned(object? context)
        {
            if (!_began || _returned || _finished) return;
            _returned = true;
            foreach (var frame in _frames)
            {
                if (!frame.Began || !_owner.Active(frame.Subscription)) continue;
                try { frame.Subscription.Observer.After?.Invoke(context, frame.State); }
                catch (Exception error) { _owner.Note(_target, frame.Subscription, "after", error); }
            }
        }
        internal void Finish(object? context)
        {
            if (!_began || _finished) return;
            _finished = true;
            foreach (var frame in _frames)
            {
                if (!frame.Began) continue;
                // Cleanup is owed to the OLD invocation even after its subscription
                // was removed. Never substitute a re-enabled observer or its state.
                try { frame.Subscription.Observer.Finally?.Invoke(context, frame.State); }
                catch (Exception error) { _owner.Note(_target, frame.Subscription, "finally", error); }
            }
        }
    }
}

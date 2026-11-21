using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ForgeDevelopment.Native;

// One coordinated callback set per target, installed as one ForgeCore observation.
internal sealed class DevelopmentNativeHooks
{
    internal enum Lane { Postfix, EarlyPostfix, FinalizerReturned }
    internal sealed record Order(int Prefix, int Postfix, int Finalizer, string[] Before, string[] After)
    {
        internal static Order Trace => new(Priority.First, Priority.Last, Priority.Last, Array.Empty<string>(), Array.Empty<string>());
        internal static Order Normal => new(Priority.Normal, Priority.Normal, Priority.Normal, Array.Empty<string>(), Array.Empty<string>());
        internal static Order Shared => new(Priority.First, Priority.First, Priority.Last, Array.Empty<string>(), Array.Empty<string>());
        internal string[] PrefixBefore { get; init; } = Before;
        internal string[] PrefixAfter { get; init; } = After;
        internal string[] PostfixBefore { get; init; } = Before;
        internal string[] PostfixAfter { get; init; } = After;
        internal string[] FinalizerBefore { get; init; } = Before;
        internal string[] FinalizerAfter { get; init; } = After;
        internal Order Freeze() => this with
        {
            Before = Copy(Before),
            After = Copy(After),
            PrefixBefore = Copy(PrefixBefore),
            PrefixAfter = Copy(PrefixAfter),
            PostfixBefore = Copy(PostfixBefore),
            PostfixAfter = Copy(PostfixAfter),
            FinalizerBefore = Copy(FinalizerBefore),
            FinalizerAfter = Copy(FinalizerAfter)
        };
        private static string[] Copy(string[] values) => values.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        internal bool Same(Order other) => Prefix == other.Prefix && Postfix == other.Postfix && Finalizer == other.Finalizer
            && PrefixBefore.SequenceEqual(other.PrefixBefore) && PrefixAfter.SequenceEqual(other.PrefixAfter)
            && PostfixBefore.SequenceEqual(other.PostfixBefore) && PostfixAfter.SequenceEqual(other.PostfixAfter)
            && FinalizerBefore.SequenceEqual(other.FinalizerBefore) && FinalizerAfter.SequenceEqual(other.FinalizerAfter);
        internal bool Contains(Order other) => Prefix == other.Prefix && Postfix == other.Postfix && Finalizer == other.Finalizer
            && !other.PrefixBefore.Except(PrefixBefore).Any() && !other.PrefixAfter.Except(PrefixAfter).Any()
            && !other.PostfixBefore.Except(PostfixBefore).Any() && !other.PostfixAfter.Except(PostfixAfter).Any()
            && !other.FinalizerBefore.Except(FinalizerBefore).Any() && !other.FinalizerAfter.Except(FinalizerAfter).Any();
    }
    internal sealed record PatchSet(MethodInfo Prefix, MethodInfo Postfix, MethodInfo Finalizer, Order Order,
        bool DamageWriter = false);
    internal sealed record ReviewedAdapter(string Identity, Func<MethodBase, string?> RejectionReason,
        Func<MethodBase, Order, PatchSet> Select, bool SupportsDamageWriter = false);
    internal sealed record Operations(Func<MethodBase, string?> Admission, Action<MethodBase, PatchSet> Install,
        Action<MethodBase, PatchSet> Remove, Func<MethodBase, PatchSet, bool> Installed, Func<MethodBase, bool> Removed,
        Action<MethodBase, PatchSet>? Replace = null, bool RetainUntilStopAll = false,
        Func<MethodBase, string?>? EvidenceScope = null);
    internal sealed record Refusal(MethodBase Target, string Consumer, string Reason);
    internal sealed record ConsumerManifest(string Name, DevelopmentHookCallbacks.Callback[] Callbacks, Lane Lane);
    internal sealed record Manifest(MethodBase Target, PatchSet Patches, ConsumerManifest[] Consumers, string EvidenceScope, bool DamageWriterActive);
    private sealed class InstallationState
    {
        internal bool NativeAttempted;
        internal bool NativeRemoved;
    }
    private sealed record TargetState(PatchSet Patches, ReviewedAdapter? Adapter)
    {
        // Record copies used for ordering reconfiguration keep this same lifetime.
        internal readonly InstallationState Installation = new();
    }
    private sealed class Completion
    {
        internal readonly DevelopmentHookCallbacks.Context Context;
        internal readonly bool Returned;
        private readonly List<Action> _returns = new(), _finalizers = new();
        private readonly Action<Exception> _report;
        internal Completion(DevelopmentHookCallbacks.Context context, bool returned, Action<Exception> report) { Context = context; Returned = returned; _report = report; }
        internal void Queue(Consumer consumer, DevelopmentHookCallbacks.Invocation state)
        {
            if (consumer.Lane == Lane.FinalizerReturned && Returned && Context.Exception == null)
                _returns.Add(() => { if (consumer.Active) consumer.Plan.After(Context, state); });
            _finalizers.Add(() => consumer.Plan.Finally(Context, state));
        }
        internal void Dispatch()
        {
            // All late returned observations precede every consumer's cleanup.
            foreach (var returned in _returns) { try { returned(); } catch (Exception error) { _report(error); } }
            foreach (var finalizer in _finalizers) { try { finalizer(); } catch (Exception error) { _report(error); } }
        }
    }
    private sealed class Consumer
    {
        internal readonly DevelopmentHookCallbacks.Callback[] Callbacks;
        internal readonly DevelopmentHookCallbacks.Plan Plan;
        internal readonly DevelopmentHookCoordinator.Observer Observer;
        internal readonly Lane Lane;
        internal volatile bool Active = true;
        internal int References;
        internal Consumer(DevelopmentHookCallbacks.Callback[] callbacks, DevelopmentHookCallbacks.Plan plan, Lane lane)
        {
            Callbacks = callbacks; Plan = plan; Lane = lane;
            Observer = new DevelopmentHookCoordinator.Observer(
                context => Plan.Before((DevelopmentHookCallbacks.Context)context!),
                (context, state) => { if (Lane != Lane.FinalizerReturned) Plan.After((DevelopmentHookCallbacks.Context)context!, (DevelopmentHookCallbacks.Invocation)state!); },
                (context, state) => ((Completion)context!).Queue(this, (DevelopmentHookCallbacks.Invocation)state!));
        }
    }
    internal sealed class Frame
    {
        internal readonly DevelopmentNativeHooks Owner;
        internal readonly MethodBase Target;
        internal readonly DevelopmentHookCoordinator.Invocation Invocation;
        internal bool Returned, Finished;
        internal Frame(DevelopmentNativeHooks owner, MethodBase target, DevelopmentHookCoordinator.Invocation invocation)
        { Owner = owner; Target = target; Invocation = invocation; }
    }
    private static readonly object RoutesGate = new();
    private static readonly Dictionary<MethodBase, DevelopmentNativeHooks> Routes = new();
    private static readonly Lazy<DevelopmentNativeHooks> Production = new(CreateProduction);
    internal static DevelopmentNativeHooks Shared => Production.Value;
    private const string ObservationWriter = "ForgeDevelopment.Native.SharedHooks";
    private readonly object _gate = new();
    private readonly Operations _operations;
    private readonly DevelopmentHookCoordinator _coordinator;
    private readonly Dictionary<MethodBase, TargetState> _targets = new();
    private readonly Dictionary<(MethodBase, string), Consumer> _consumers = new();
    private readonly Dictionary<string, ReviewedAdapter> _adapters = new(StringComparer.Ordinal);
    private readonly Dictionary<MethodBase, Order> _declaredOrders = new();
    private readonly Action<Refusal>? _refusal;
    private readonly Action<Exception>? _error;

    internal DevelopmentNativeHooks(Operations operations, Action<Refusal>? refusal = null, Action<Exception>? error = null)
    {
        _operations = operations; _refusal = refusal; _error = error;
        _coordinator = new DevelopmentHookCoordinator(
            target => _operations.Admission(target) == null, Prepare, failure => Report(failure.Error));
    }

    // Callers must supply their existing effective ordering. A subscriber is never
    // silently moved to another phase/priority just to share an installed detour.
    internal IDisposable Subscribe(MethodBase target, string consumer,
        IEnumerable<DevelopmentHookCallbacks.Callback> callbacks, Order order, Lane lane = Lane.Postfix, ReviewedAdapter? adapter = null)
    {
        var list = callbacks.ToArray();
        DevelopmentHookCallbacks.Plan plan;
        try { plan = DevelopmentHookCallbacks.Plan.Create(target, list, failure => Report(failure.Error)); }
        catch (ArgumentException error) { throw Refuse(target, consumer, "callback-binding-refused:" + error.Message); }
        var frozen = Normalize(order, lane);
        lock (_gate)
        {
            var key = (target, consumer);
            var addedConsumer = !_consumers.TryGetValue(key, out var entry);
            if (!addedConsumer && (!entry!.Callbacks.SequenceEqual(list) || entry.Lane != lane))
                throw Refuse(target, consumer, "active-consumer-callback-change");
            adapter ??= FindAdapter(target);
            var reason = adapter?.RejectionReason(target) ?? (adapter == null ? SignatureRejection(target) : null);
            if (reason != null) throw Refuse(target, consumer, reason);
            if (_declaredOrders.TryGetValue(target, out var declared)) frozen = Merge(declared, frozen);
            _targets.TryGetValue(target, out var existingTarget);
            if (existingTarget != null && existingTarget.Adapter?.Identity != adapter?.Identity)
                throw Refuse(target, consumer, "target-adapter-identity-conflict");
            var addedTarget = existingTarget == null;
            if (addedTarget)
            {
                reason = _operations.Admission(target);
                if (reason != null) throw Refuse(target, consumer, reason);
            }
            else if (_coordinator.Inspect().Any(s => s.Target.Equals(target) && s.Installed && s.Fault == null)
                && (!OwnsRoute(target) || !_operations.Installed(target, existingTarget!.Patches)))
                throw Refuse(target, consumer, "native-installation-generation-unverified");
            if (!addedTarget && !existingTarget!.Patches.Order.Contains(frozen))
            {
                var merged = Merge(existingTarget.Patches.Order, frozen);
                Reconfigure(target, existingTarget, merged);
                existingTarget = _targets[target];
            }
            if (addedTarget) _targets.Add(target, new TargetState(ValidateSet(adapter?.Select(target, frozen) ?? Select(target, frozen), frozen)
                with
            { DamageWriter = adapter?.SupportsDamageWriter == true }, adapter));
            if (addedConsumer)
            {
                entry = new Consumer(list, plan, lane); _consumers.Add(key, entry);
            }
            try
            {
                var lease = _coordinator.Acquire(target, consumer, entry!.Observer);
                entry.References++;
                return new Lease(this, key, entry, lease);
            }
            catch
            {
                if (addedConsumer) _consumers.Remove(key);
                // Keep a failed target's metadata for explicit cleanup, never retry installation implicitly.
                if (addedTarget && !_coordinator.Inspect().Any(s => s.Target.Equals(target))) _targets.Remove(target);
                throw;
            }
        }
    }

    internal delegate void DamageWriter(object instance, ref float damage);
    private sealed class Writer
    {
        internal readonly string Consumer;
        internal readonly DamageWriter Apply;
        internal int References;
        internal Writer(string consumer, DamageWriter apply) { Consumer = consumer; Apply = apply; }
    }
    private readonly Dictionary<MethodBase, Writer> _writers = new();
    internal IDisposable SubscribeDamageWriter(MethodBase target, string consumer, DamageWriter writer)
    {
        lock (_gate)
        {
            var adapter = FindAdapter(target);
            if (adapter?.SupportsDamageWriter != true) throw Refuse(target, consumer, "reviewed-damage-writer-adapter-required");
            if (_writers.TryGetValue(target, out var current) && (current.Consumer != consumer || !Equals(current.Apply, writer)))
                throw Refuse(target, consumer, "one-damage-writer-per-target");
            var lease = Subscribe(target, consumer, Array.Empty<DevelopmentHookCallbacks.Callback>(), Order.Shared, Lane.EarlyPostfix, adapter);
            if (current == null) { current = new Writer(consumer, writer); _writers.Add(target, current); }
            current.References++;
            return new WriterLease(this, target, current, lease);
        }
    }
    private sealed class WriterLease : IDisposable
    {
        private readonly DevelopmentNativeHooks _owner;
        private readonly MethodBase _target;
        private readonly Writer _writer;
        private IDisposable? _lease;
        private bool _disabled;
        internal WriterLease(DevelopmentNativeHooks owner, MethodBase target, Writer writer, IDisposable lease)
        { _owner = owner; _target = target; _writer = writer; _lease = lease; }
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_lease == null) return;
                if (!_disabled)
                {
                    _disabled = true;
                    if (--_writer.References == 0 && _owner._writers.TryGetValue(_target, out var active) && ReferenceEquals(active, _writer))
                        _owner._writers.Remove(_target);
                }
                _lease.Dispose(); // Keep lease on failure for a real retry.
                _lease = null;
            }
        }
    }
    internal static void ApplyDamageWriter(object instance, MethodBase target, ref float damage)
    {
        DevelopmentNativeHooks? owner;
        lock (RoutesGate) Routes.TryGetValue(target, out owner);
        if (owner == null) return;
        var original = damage;
        try
        {
            lock (owner._gate)
            {
                if (owner._writers.TryGetValue(target, out var writer)) writer.Apply(instance, ref damage);
            }
        }
        catch (Exception error) { damage = original; owner.Report(error); }
    }

    internal void RegisterAdapter(ReviewedAdapter adapter)
    {
        if (string.IsNullOrWhiteSpace(adapter.Identity)) throw new ArgumentException("Adapter identity is required.");
        lock (_gate)
        {
            if (_adapters.TryGetValue(adapter.Identity, out var previous))
            { if (!Equals(previous, adapter)) throw new InvalidOperationException("adapter-identity-already-registered"); return; }
            if (_targets.Any(pair => adapter.RejectionReason(pair.Key) == null && pair.Value.Adapter?.Identity != adapter.Identity))
                throw new InvalidOperationException("adapter-registration-after-target-installation");
            _adapters.Add(adapter.Identity, adapter);
        }
    }
    private ReviewedAdapter? FindAdapter(MethodBase target)
    {
        var candidates = _adapters.Values.Where(a => a.RejectionReason(target) == null).ToArray();
        if (candidates.Length > 1) throw new NotSupportedException("ambiguous-reviewed-target-adapter");
        return candidates.SingleOrDefault();
    }
    internal void DeclareOrder(MethodBase target, Order order, Lane lane)
    {
        lock (_gate)
        {
            var normalized = Normalize(order, lane);
            if (_declaredOrders.TryGetValue(target, out var previous)) normalized = Merge(previous, normalized);
            if (_targets.TryGetValue(target, out var active) && !active.Patches.Order.Contains(normalized))
                Reconfigure(target, active, Merge(active.Patches.Order, normalized));
            _declaredOrders[target] = normalized;
        }
    }
    internal string? Preflight(MethodBase target)
    {
        lock (_gate)
        {
            if (_targets.TryGetValue(target, out var active))
            {
                var status = _coordinator.Inspect().FirstOrDefault(s => s.Target.Equals(target));
                return status is { Installed: true, Fault: null } && OwnsRoute(target) && _operations.Installed(target, active.Patches)
                    ? null : "native-installation-generation-unverified";
            }
            var adapter = FindAdapter(target);
            return (adapter == null ? SignatureRejection(target) : adapter.RejectionReason(target)) ?? _operations.Admission(target);
        }
    }
    internal Manifest? TryGetManifest(MethodBase target)
    {
        lock (_gate)
        {
            if (!_targets.TryGetValue(target, out var state) || Preflight(target) != null) return null;
            return new Manifest(target, state.Patches with { Order = state.Patches.Order.Freeze() },
                _consumers.Where(p => p.Key.Item1.Equals(target) && p.Value.Active).Select(p =>
                    new ConsumerManifest(p.Key.Item2, (DevelopmentHookCallbacks.Callback[])p.Value.Callbacks.Clone(), p.Value.Lane)).ToArray(),
                EvidenceScope(target)!, _writers.ContainsKey(target));
        }
    }
    private void Reconfigure(MethodBase target, TargetState old, Order order)
    {
        if (Preflight(target) != null) throw Refuse(target, "reconfigure", "native-installation-generation-unverified");
        var replacement = old.Patches with { Order = order.Freeze() };
        try
        {
            if (_operations.Replace == null) throw new NotSupportedException("native-observation-replacement-unavailable");
            _operations.Replace(target, replacement);
            if (!_operations.Installed(target, replacement)) throw new InvalidOperationException("native-reconfigure-installation-unverified");
        }
        catch (Exception error)
        {
            foreach (var consumer in _consumers.Where(pair => pair.Key.Item1.Equals(target)).Select(pair => pair.Value)) consumer.Active = false;
            _writers.Remove(target);
            _coordinator.MarkFault(target, error, true);
            Report(error);
            throw;
        }
        _targets[target] = old with { Patches = replacement };
    }
    private static Order Normalize(Order order, Lane lane)
    {
        if (!Enum.IsDefined(typeof(Lane), lane)) throw new ArgumentException("Unknown observation lane.");
        if (lane == Lane.Postfix) return Merge(order.Freeze(), order.Freeze());
        var shared = Order.Shared;
        var normalized = (lane == Lane.EarlyPostfix
            ? shared with { PrefixBefore = order.Before, PrefixAfter = order.After, PostfixBefore = order.Before, PostfixAfter = order.After }
            : shared with { FinalizerBefore = order.Before, FinalizerAfter = order.After }).Freeze();
        return Merge(normalized, normalized);
    }
    private static Order Merge(Order left, Order right)
    {
        if (left.Prefix != right.Prefix || left.Postfix != right.Postfix || left.Finalizer != right.Finalizer)
            throw new NotSupportedException("incompatible-subscriber-order");
        string[] Union(string[] a, string[] b) => a.Concat(b).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var merged = left with
        {
            PrefixBefore = Union(left.PrefixBefore, right.PrefixBefore),
            PrefixAfter = Union(left.PrefixAfter, right.PrefixAfter),
            PostfixBefore = Union(left.PostfixBefore, right.PostfixBefore),
            PostfixAfter = Union(left.PostfixAfter, right.PostfixAfter),
            FinalizerBefore = Union(left.FinalizerBefore, right.FinalizerBefore),
            FinalizerAfter = Union(left.FinalizerAfter, right.FinalizerAfter)
        };
        if (merged.PrefixBefore.Intersect(merged.PrefixAfter).Any() || merged.PostfixBefore.Intersect(merged.PostfixAfter).Any()
            || merged.FinalizerBefore.Intersect(merged.FinalizerAfter).Any()) throw new NotSupportedException("contradictory-phase-owner-constraints");
        return merged.Freeze();
    }
    private static PatchSet ValidateSet(PatchSet set, Order expected)
    {
        if (!set.Order.Same(expected) || set.Prefix.DeclaringType != set.Postfix.DeclaringType || set.Prefix.DeclaringType != set.Finalizer.DeclaringType
            || !set.Prefix.IsStatic || !set.Postfix.IsStatic || !set.Finalizer.IsStatic
            || set.Prefix.ReturnType != typeof(void) || set.Postfix.ReturnType != typeof(void) || set.Finalizer.ReturnType != typeof(void))
            throw new NotSupportedException("invalid-reviewed-physical-callback-set");
        return set;
    }

    private sealed class Lease : IDisposable
    {
        private DevelopmentNativeHooks? _owner;
        private readonly (MethodBase, string) _key;
        private readonly Consumer _consumer;
        private readonly IDisposable _lease;
        private bool _released;
        internal Lease(DevelopmentNativeHooks owner, (MethodBase, string) key, Consumer consumer, IDisposable lease)
        { _owner = owner; _key = key; _consumer = consumer; _lease = lease; }
        public void Dispose()
        {
            var owner = _owner;
            if (owner == null) return;
            lock (owner._gate)
            {
                if (_owner == null) return;
                if (!_released)
                {
                    _released = true;
                    try { _lease.Dispose(); }
                    finally
                    {
                        if (--_consumer.References == 0)
                        {
                            _consumer.Active = false;
                            if (owner._consumers.TryGetValue(_key, out var current) && ReferenceEquals(current, _consumer)) owner._consumers.Remove(_key);
                        }
                    }
                }
                var status = owner._coordinator.Inspect().FirstOrDefault(s => s.Target.Equals(_key.Item1));
                if (status is { CleanupPending: true, Consumers: 0, Fault: not null } && !owner._coordinator.RetryCleanup(_key.Item1))
                    throw new InvalidOperationException("native-cleanup-still-pending", owner._coordinator.Inspect().First(s => s.Target.Equals(_key.Item1)).Fault);
                if (!owner._coordinator.Inspect().Any(s => s.Target.Equals(_key.Item1))) owner._targets.Remove(_key.Item1);
                // Failure keeps this lease alive so a caller's second Stop/Dispose
                // must retry the pending physical cleanup rather than falsely succeed.
                _owner = null;
            }
        }
    }
    internal DevelopmentHookCoordinator.Status[] Inspect() => _coordinator.Inspect();
    internal bool RetryCleanup(MethodBase target) => _coordinator.RetryCleanup(target);
    internal void StopAll()
    {
        lock (_gate)
        {
            _writers.Clear();
            foreach (var failure in _coordinator.StopAll()) Report(failure.Error);
            foreach (var consumer in _consumers.Values) consumer.Active = false;
            _consumers.Clear();
            foreach (var target in _targets.Keys.ToArray())
                if (!_coordinator.Inspect().Any(s => s.Target.Equals(target))) _targets.Remove(target);
            var pending = _coordinator.Inspect().Where(s => s.CleanupPending).ToArray();
            if (pending.Length != 0) throw new AggregateException("native-cleanup-still-pending",
                pending.Select(s => s.Fault ?? new InvalidOperationException("Unverified removal: " + s.Target.Name)));
        }
    }
    private bool OwnsRoute(MethodBase target)
    { lock (RoutesGate) return Routes.TryGetValue(target, out var owner) && ReferenceEquals(owner, this); }
    private DevelopmentHookCoordinator.Installation Prepare(MethodBase target)
    {
        var targetState = _targets[target];
        var patches = targetState.Patches;
        var installation = targetState.Installation;
        return new DevelopmentHookCoordinator.Installation(() =>
        {
            lock (RoutesGate)
            {
                if (Routes.TryGetValue(target, out var owner) && !ReferenceEquals(owner, this))
                    throw new InvalidOperationException("native-target-already-owned");
                Routes[target] = this;
            }
            var adapter = _targets[target].Adapter;
            // Subscribe admitted this target under the same lock and Install below is Runtime's final admission, so the
            // Runtime admission is not asked a third time here; the metadata checks are cheap and stay.
            var refusal = adapter == null ? SignatureRejection(target) : adapter.RejectionReason(target);
            if (refusal != null) throw new NotSupportedException(refusal);
            installation.NativeAttempted = true;
            installation.NativeRemoved = false;
            _operations.Install(target, patches);
            if (!_operations.Installed(target, patches)) throw new InvalidOperationException("native-installation-unverified");
        }, () =>
        {
            // Another coordinator's failure cannot remove the active owner's set.
            lock (RoutesGate)
                if (!Routes.TryGetValue(target, out var owner) || !ReferenceEquals(owner, this)) return;
            if (!installation.NativeRemoved)
            {
                if (installation.NativeAttempted) _operations.Remove(target, patches);
                if (!_operations.Removed(target)) throw new InvalidOperationException("native-removal-unverified");
                installation.NativeRemoved = true;
            }
            lock (RoutesGate) Routes.Remove(target);
        }, _operations.RetainUntilStopAll);
    }

    internal static string? SignatureRejection(MethodBase target)
    {
        if (target is not MethodInfo method || method.IsAbstract || method.ContainsGenericParameters
            || (method.CallingConvention & CallingConventions.VarArgs) != 0) return "unsupported-native-method-kind";
        if (!method.IsStatic && RecNativePatchSafety.ValueReceiver(method.DeclaringType)) return "unsupported-native-value-type-instance";
        try
        {
            if (method.GetParameters().Any(p => !ObservedType(p.ParameterType))) return "unsupported-native-parameter:typed-adapter-required";
            if (method.ReturnType != typeof(void) && !ObservedType(method.ReturnType)) return "unsupported-native-return:typed-adapter-required";
        }
        catch (Exception) { return "unreadable-native-signature"; } // The explicit refusal code "unreadable-native-signature" is returned to the caller.
        return null;
    }
    // By-value observation uses the generated metadata. Runtime admits the native ABI;
    // by-reference signatures still require an exact reviewed observer adapter here.
    private static bool ObservedType(Type type) => !type.IsByRef && !type.IsPointer && !type.IsByRefLike && !type.ContainsGenericParameters;
    private static DevelopmentNativeHooks CreateProduction()
    {
        var observations = new Dictionary<MethodBase, (PatchSet Set, IDisposable? Lease)>();
        IDisposable Observe(MethodBase target, PatchSet set) => DevelopmentHookInstall.Observe(ObservationWriter, (MethodInfo)target,
            set.Prefix, set.Postfix, set.Finalizer, set.Order.Prefix, set.Order.Postfix, set.Order.Finalizer,
            set.Order.PrefixBefore, set.Order.PrefixAfter, set.Order.PostfixBefore, set.Order.PostfixAfter,
            set.Order.FinalizerBefore, set.Order.FinalizerAfter);
        return new DevelopmentNativeHooks(new Operations(
            NativeAdmission,
            (target, set) =>
            {
                observations.Add(target, (set, null));
                try { observations[target] = (set, Observe(target, set)); }
                catch { observations.Remove(target); throw; }
            },
            (target, _) =>
            {
                if (!observations.TryGetValue(target, out var observation)) return;
                observation.Lease?.Dispose();
                observations.Remove(target);
            },
            (target, set) => observations.TryGetValue(target, out var observation) && observation.Lease != null && observation.Set == set,
            target => !observations.ContainsKey(target),
            (target, set) =>
            {
                var observation = observations[target];
                if (observation.Lease == null) throw new InvalidOperationException("native-observation-replacement-unavailable:lease-not-returned");
                observation.Lease.Dispose();
                observations[target] = (observation.Set, null);
                observations[target] = (set, Observe(target, set));
            },
            RetainUntilStopAll: true,
            EvidenceScope: target => observations.TryGetValue(target, out var observation) && observation.Lease != null
                ? "core-observation" : null),
            ReportRefusal,
            error => RecDiag.Error("development.shared-hook-observer-failed: " + error));
    }    /// <summary>Every refusal is an anomaly and stays a per-target Error.</summary>
    internal static void ReportRefusal(Refusal refusal)
    {
        RecDiag.Record(true, "hooks.shared-refused",
            "development.shared-hook-refused target=" + refusal.Target.Name + " consumer=" + refusal.Consumer + " reason=" + refusal.Reason,
            new RecDiag.Field("target", refusal.Target.Name), new RecDiag.Field("consumer", refusal.Consumer),
            new RecDiag.Field("reason", refusal.Reason));
    }
    private static string? NativeAdmission(MethodBase target)
    {
        try
        {
            if (RecNativePatchSafety.ObservationScope(target) != "native-method") return "native-metadata-required:no-managed-wrapper-fallback";
            var reason = RecNativePatchSafety.RejectionReason(target);
            if (reason != null) return reason;
            return ForgeCore.Hooks.Unsupported(target);
        }
        catch (Exception error) { return "native-admission-unavailable:" + error.GetType().Name; }
    }
    internal string? EvidenceScope(MethodBase target)
    {
        lock (_gate)
        {
            if (!_targets.TryGetValue(target, out var state) || !_operations.Installed(target, state.Patches)
                || !_coordinator.Inspect().Any(s => s.Target.Equals(target) && s.Installed && s.Fault == null)) return null;
            return _operations.EvidenceScope == null ? "test-backend-registration" : _operations.EvidenceScope(target);
        }
    }
    internal static PatchSet Select(MethodBase target, Order order)
    {
        var instance = target.IsStatic ? "Static" : "Instance";
        var result = target is MethodInfo method && method.ReturnType != typeof(void) ? "Result" : "Void";
        MethodInfo Find(string name) => typeof(DevelopmentNativeHooks).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        return new PatchSet(Find("Prefix" + instance), Find("Postfix" + instance + result), Find("Finalizer" + instance + result), order);
    }
    private NotSupportedException Refuse(MethodBase target, string consumer, string reason)
    { try { _refusal?.Invoke(new Refusal(target, consumer, reason)); } catch { } return new NotSupportedException(reason); } // Refusal callback failed; the NotSupportedException that carries the reason is still returned.
    private void Report(Exception error) { try { _error?.Invoke(error); } catch { } } // Error callback failed; reporting cannot alter a game call.
    internal static void Begin(object? instance, object?[] args, MethodBase target, bool runOriginal, out Frame? state)
    {
        state = null;
        DevelopmentNativeHooks? owner;
        lock (RoutesGate) Routes.TryGetValue(target, out owner);
        if (owner == null) return;
        try
        {
            var invocation = owner._coordinator.Capture(target);
            if (invocation == null) return;
            state = new Frame(owner, target, invocation);
            invocation.Begin(new DevelopmentHookCallbacks.Context(instance, args, runOriginal: runOriginal));
        }
        catch (Exception error) { owner.Report(error); }
    }
    internal static void Return(object? instance, object?[] args, object? result, bool runOriginal, Frame? state)
    {
        if (state == null || state.Returned || state.Finished) return;
        state.Returned = true;
        try { state.Invocation.Returned(new DevelopmentHookCallbacks.Context(instance, args, result, runOriginal)); }
        catch (Exception error) { state.Owner.Report(error); }
    }
    internal static void Finish(object? instance, object?[] args, object? result, bool runOriginal, Exception? exception, Frame? state)
    {
        if (state == null || state.Finished) return;
        state.Finished = true;
        try
        {
            var completion = new Completion(new DevelopmentHookCallbacks.Context(instance, args, result, runOriginal, exception), state.Returned, state.Owner.Report);
            state.Invocation.Finish(completion);
            completion.Dispatch();
        }
        catch (Exception error) { state.Owner.Report(error); }
    }
    private static void PrefixInstance(object? __instance, object?[] __args, MethodBase __originalMethod, bool __runOriginal, out Frame? __state)
        => Begin(__instance, __args, __originalMethod, __runOriginal, out __state);
    private static void PrefixStatic(object?[] __args, MethodBase __originalMethod, bool __runOriginal, out Frame? __state)
        => Begin(null, __args, __originalMethod, __runOriginal, out __state);
    private static void PostfixInstanceResult(object? __instance, object?[] __args, object? __result, bool __runOriginal, Frame? __state)
        => Return(__instance, __args, __result, __runOriginal, __state);
    private static void PostfixInstanceVoid(object? __instance, object?[] __args, bool __runOriginal, Frame? __state)
        => Return(__instance, __args, null, __runOriginal, __state);
    private static void PostfixStaticResult(object?[] __args, object? __result, bool __runOriginal, Frame? __state)
        => Return(null, __args, __result, __runOriginal, __state);
    private static void PostfixStaticVoid(object?[] __args, bool __runOriginal, Frame? __state)
        => Return(null, __args, null, __runOriginal, __state);
    private static void FinalizerInstanceResult(object? __instance, object?[] __args, object? __result, bool __runOriginal, Exception? __exception, Frame? __state)
        => Finish(__instance, __args, __result, __runOriginal, __exception, __state);
    private static void FinalizerInstanceVoid(object? __instance, object?[] __args, bool __runOriginal, Exception? __exception, Frame? __state)
        => Finish(__instance, __args, null, __runOriginal, __exception, __state);
    private static void FinalizerStaticResult(object?[] __args, object? __result, bool __runOriginal, Exception? __exception, Frame? __state)
        => Finish(null, __args, __result, __runOriginal, __exception, __state);
    private static void FinalizerStaticVoid(object?[] __args, bool __runOriginal, Exception? __exception, Frame? __state)
        => Finish(null, __args, null, __runOriginal, __exception, __state);
}

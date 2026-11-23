using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Common;

namespace ForgeEngine.GameBindings;

// patch-targets: run time (the managed declarations are installed here; each target is checked at its DeclareManaged call)
public static partial class NativeHookRegistration
{
    private sealed record ManagedDefinition(string Writer, MethodInfo Target, MethodInfo? Prefix,
        MethodInfo? Postfix, MethodInfo? Finalizer, NativeHookOrder Order, bool IlWrapper);

    private sealed class ManagedRecord
    {
        internal readonly ManagedDefinition Definition;
        internal IDisposable? Installation;
        internal int References;
        internal bool Attempted, CleanupPending;
        internal ManagedRecord(ManagedDefinition definition) => Definition = definition;
    }

    private sealed class ManagedLease : IDisposable
    {
        private ManagedRecord? _record;
        private bool _referenceReleased;
        internal ManagedLease(ManagedRecord record) => _record = record;
        public void Dispose()
        {
            lock (ManagedGate)
            {
                var record = _record;
                if (record == null) return;
                if (!ManagedRecords.TryGetValue(record.Definition.Target, out var retained) || !ReferenceEquals(retained, record))
                { _record = null; return; }
                if (!_referenceReleased)
                {
                    if (record.References <= 0) throw new InvalidOperationException("managed-hook-reference-underflow");
                    record.References--;
                    _referenceReleased = true;
                }
                if (record.References != 0) { _record = null; return; }
                try { CleanupManaged(record); ManagedRecords.Remove(record.Definition.Target); _record = null; }
                catch { record.CleanupPending = true; throw; }
            }
        }
    }

    private static readonly object ManagedGate = new();
    private static readonly Dictionary<MethodInfo, ManagedRecord> ManagedRecords = new();
    static partial void InitializeManagedHooks()
    {
        _declareManaged = DeclareManagedCore;
        _releaseManaged = ReleaseManagedCore;
    }

    private static IDisposable DeclareManagedCore(string writer, MethodInfo target, MethodInfo? prefix,
        MethodInfo? postfix, MethodInfo? finalizer, NativeHookOrder order, bool ilWrapper)
    {
        var definition = ManagedDefinitionOf(writer, target, prefix, postfix, finalizer, order, ilWrapper);
        lock (ManagedGate)
        {
            if (ManagedRecords.TryGetValue(target, out var existing))
            {
                if (!SameManaged(existing.Definition, definition))
                    throw new NotSupportedException(ManagedReason("managed-hook-declaration-conflict", definition));
                if (existing.CleanupPending || existing.Installation == null)
                    throw new NotSupportedException(ManagedReason("managed-hook-cleanup-pending", definition));
                checked { existing.References++; }
                return new ManagedLease(existing);
            }
            var record = new ManagedRecord(definition);
            ManagedRecords.Add(target, record);
            try
            {
                record.Attempted = true;
                InstallManaged(record);
                record.References = 1;
                return new ManagedLease(record);
            }
            catch (Exception original)
            {
                try { CleanupManaged(record); ManagedRecords.Remove(target); }
                catch (Exception cleanup)
                {
                    record.CleanupPending = true;
                    throw new AggregateException(ManagedReason("managed-hook-installation-cleanup-failed", definition), original, cleanup);
                }
                throw;
            }
        }
    }

    private static void ReleaseManagedCore(string writer, MethodInfo target)
    {
        lock (ManagedGate)
        {
            if (!ManagedRecords.TryGetValue(target, out var record)) return;
            if (record.Definition.Writer != writer)
                throw new NotSupportedException(ManagedReason("managed-hook-writer-conflict", record.Definition));
            if (record.References != 0)
                throw new InvalidOperationException(ManagedReason("managed-hook-active-leases", record.Definition));
            try { CleanupManaged(record); ManagedRecords.Remove(target); }
            catch { record.CleanupPending = true; throw; }
        }
    }

    private static ManagedDefinition ManagedDefinitionOf(string writer, MethodInfo target, MethodInfo? prefix,
        MethodInfo? postfix, MethodInfo? finalizer, NativeHookOrder order, bool ilWrapper)
    {
        if (string.IsNullOrWhiteSpace(writer) || writer.Length > 128 || target.IsAbstract || target.ContainsGenericParameters
            || prefix == null && postfix == null && finalizer == null)
            throw new ArgumentException("managed-hook-declaration-invalid");
        foreach (var callback in new[] { prefix, postfix, finalizer })
            if (callback != null && (!callback.IsStatic || callback.IsAbstract || callback.ContainsGenericParameters))
                throw new ArgumentException("managed-hook-callback-invalid");
        FieldInfo? native;
        try { native = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target); }
        catch (Exception error) { throw new NotSupportedException("managed-hook-target-metadata-unavailable", error); }
        if (!ilWrapper && native != null)
            throw new NotSupportedException("managed-hook-native-target-requires-native-declaration");
        if (ilWrapper && native == null)
            throw new NotSupportedException("managed-hook-ilwrapper-target-required");
        return new(writer, target, prefix, postfix, finalizer, NormalizeManaged(order), ilWrapper);
    }

    private static NativeHookOrder NormalizeManaged(NativeHookOrder order)
    {
        string[] Values(string[]? values, string phase)
        {
            if (values == null || values.Length == 0) return Array.Empty<string>();
            if (values.Length > 64 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128))
                throw new ArgumentException("managed-hook-order-invalid phase=" + phase);
            return values.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
        var prefixBefore = Values(order.PrefixBefore, "prefix-before"); var prefixAfter = Values(order.PrefixAfter, "prefix-after");
        var postfixBefore = Values(order.PostfixBefore, "postfix-before"); var postfixAfter = Values(order.PostfixAfter, "postfix-after");
        var finalizerBefore = Values(order.FinalizerBefore, "finalizer-before"); var finalizerAfter = Values(order.FinalizerAfter, "finalizer-after");
        if (prefixBefore.Intersect(prefixAfter, StringComparer.Ordinal).Any()
            || postfixBefore.Intersect(postfixAfter, StringComparer.Ordinal).Any()
            || finalizerBefore.Intersect(finalizerAfter, StringComparer.Ordinal).Any())
            throw new ArgumentException("managed-hook-order-contradictory");
        return new(order.PrefixPriority, order.PostfixPriority, order.FinalizerPriority,
            prefixBefore, prefixAfter, postfixBefore, postfixAfter, finalizerBefore, finalizerAfter);
    }

    /// <summary>ForgeCore installs it: an ordinary managed target is the writer's owned set, an interop wrapper an IL hook.</summary>
    private static void InstallManaged(ManagedRecord record)
    {
        using var timing = ForgeCore.StartupOperationTiming.Measure(ForgeCore.StartupOperationTiming.Operation.PatchInstallation);
        var d = record.Definition;
        var part = new ForgeCore.HookPart(Method(d.Prefix, d.Order.PrefixPriority, d.Order.PrefixBefore, d.Order.PrefixAfter),
            Method(d.Postfix, d.Order.PostfixPriority, d.Order.PostfixBefore, d.Order.PostfixAfter),
            Method(d.Finalizer, d.Order.FinalizerPriority, d.Order.FinalizerBefore, d.Order.FinalizerAfter));
        if (d.IlWrapper) { record.Installation = ForgeCore.Hooks.Wrap(d.Writer, d.Target, part); return; }
        ForgeCore.Hooks.Own(d.Writer, d.Target, new[] { part });
        record.Installation = new Owned(d.Writer, d.Target);
    }

    private sealed class Owned : IDisposable
    {
        private readonly string _writer;
        private readonly MethodInfo _target;
        internal Owned(string writer, MethodInfo target) { _writer = writer; _target = target; }
        public void Dispose() => ForgeCore.Hooks.Disown(_writer, _target);
    }

    private static void CleanupManaged(ManagedRecord record)
    {
        if (!record.Attempted) return;
        record.Installation?.Dispose();
        record.Installation = null;
        record.Attempted = record.CleanupPending = false;
    }
    private static string ManagedReason(string reason, ManagedDefinition definition)
        => reason + " declaration=" + definition.Writer + " target=" + ManagedTargetIdentity(definition.Target);

    private static string ManagedTargetIdentity(MethodInfo target)
    {
        try
        {
            return (target.DeclaringType?.FullName ?? "<unknown>") + "." + target.Name + "("
                + string.Join(",", target.GetParameters().Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)) + ")";
        }
        catch { return "<metadata-unavailable>"; } // The marker is the recorded reason: reflection on the target failed.
    }

    private static bool SameManaged(ManagedDefinition left, ManagedDefinition right)
        => left.Writer == right.Writer && left.Target == right.Target && left.Prefix == right.Prefix
            && left.Postfix == right.Postfix && left.Finalizer == right.Finalizer && left.IlWrapper == right.IlWrapper
            && left.Order.PrefixPriority == right.Order.PrefixPriority
            && left.Order.PostfixPriority == right.Order.PostfixPriority
            && left.Order.FinalizerPriority == right.Order.FinalizerPriority
            && (left.Order.PrefixBefore ?? Array.Empty<string>()).SequenceEqual(right.Order.PrefixBefore ?? Array.Empty<string>())
            && (left.Order.PrefixAfter ?? Array.Empty<string>()).SequenceEqual(right.Order.PrefixAfter ?? Array.Empty<string>())
            && (left.Order.PostfixBefore ?? Array.Empty<string>()).SequenceEqual(right.Order.PostfixBefore ?? Array.Empty<string>())
            && (left.Order.PostfixAfter ?? Array.Empty<string>()).SequenceEqual(right.Order.PostfixAfter ?? Array.Empty<string>())
            && (left.Order.FinalizerBefore ?? Array.Empty<string>()).SequenceEqual(right.Order.FinalizerBefore ?? Array.Empty<string>())
            && (left.Order.FinalizerAfter ?? Array.Empty<string>()).SequenceEqual(right.Order.FinalizerAfter ?? Array.Empty<string>());
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ForgeEngine.GameBindings;

public enum NativeObservationWrite { None, ReviewedDamagePrefix }

public sealed record NativeObservationDefinition(string Writer, MethodInfo Target, MethodInfo? Prefix,
    MethodInfo? Postfix, MethodInfo? Finalizer, NativeHookOrder Order, NativeObservationWrite Write = NativeObservationWrite.None);

public static partial class NativeHookRegistration
{
    /// <summary>Targets that carry an observation allowed to write (the reviewed damage prefix); no declaration may own them.</summary>
    private static readonly HashSet<MethodInfo> WritingObservations = new();
    private static bool ObservationWriter(MethodInfo target) => WritingObservations.Contains(target);

    /// <summary>Puts one writer's read-only observation on a native entry through ForgeCore (<see cref="ForgeCore.Hooks.Observe"/>):
    /// several writers may observe one entry beside its owner, each once. The lease spans worlds and takes exactly this
    /// observation off when disposed.</summary>
    public static NativeHookLease DeclareObservation(NativeObservationDefinition d)
    {
        d = NativeHookObservationRules.Validate(d);
        var writes = d.Write != NativeObservationWrite.None;
        if (writes && (Installations.HasDeclaration(d.Target) || WritingObservations.Contains(d.Target)))
            throw new NotSupportedException("native-hook-writer-conflict");
        IDisposable observation;
        using (ForgeCore.StartupOperationTiming.Measure(ForgeCore.StartupOperationTiming.Operation.PatchInstallation))
            observation = ForgeCore.Hooks.Observe(d.Writer, d.Target, new ForgeCore.HookPart(
                Method(d.Prefix, d.Order.PrefixPriority, d.Order.PrefixBefore, d.Order.PrefixAfter),
                Method(d.Postfix, d.Order.PostfixPriority, d.Order.PostfixBefore, d.Order.PostfixAfter),
                Method(d.Finalizer, d.Order.FinalizerPriority, d.Order.FinalizerBefore, d.Order.FinalizerAfter)));
        if (writes) WritingObservations.Add(d.Target);
        return new NativeHookLease(d.Writer, 0, static () => 0, () =>
        {
            observation.Dispose();
            if (writes) WritingObservations.Remove(d.Target);
        });
    }

    /// <summary>Checks whether Runtime could observe this native entry, without installing anything.</summary>
    public static string? ObservationRejection(MethodInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (RuntimeSharedWriter(target) is { } shared)
            return "native-hook-runtime-shared-target subscribe-via=" + shared;
        return ForgeCore.Hooks.Unsupported(target);
    }

    /// <summary>Takes <paramref name="old"/> off and puts <paramref name="d"/> on in its place, for the same writer.</summary>
    public static NativeHookLease ReplaceObservation(NativeHookLease old, NativeObservationDefinition d)
    {
        ArgumentNullException.ThrowIfNull(old);
        d = NativeHookObservationRules.Validate(d);
        if (!old.IsActive || old.Consumer != d.Writer) throw new NotSupportedException("native-hook-observation-lease-stale");
        old.End("replaced");
        return DeclareObservation(d);
    }
}
internal static class NativeHookObservationRules
{
    internal static NativeObservationDefinition Validate(NativeObservationDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d); ArgumentNullException.ThrowIfNull(d.Target); ArgumentNullException.ThrowIfNull(d.Order);
        if (string.IsNullOrWhiteSpace(d.Writer) || d.Writer.Length > 128 || d.Target.IsAbstract || d.Target.ContainsGenericParameters
            || d.Prefix == null && d.Postfix == null && d.Finalizer == null)
            throw new ArgumentException("native-hook-observation-invalid");
        if (NativeHookRegistration.RuntimeSharedWriter(d.Target) is { } shared)
            throw new NotSupportedException("native-hook-runtime-shared-target subscribe-via=" + shared);
        if (ForgeCore.Hooks.Unsupported(d.Target) is { } unsupported) throw new NotSupportedException(unsupported);
        if (d.Write != NativeObservationWrite.None && (d.Write != NativeObservationWrite.ReviewedDamagePrefix || !ReviewedDamage(d.Target)))
            throw Writes();
        foreach (var callback in new[] { d.Prefix, d.Postfix, d.Finalizer })
        {
            if (callback == null) continue;
            // A returning postfix/finalizer can replace a result/exception even without a by-ref parameter.
            if (!callback.IsStatic || callback.ContainsGenericParameters || callback.ReturnType != typeof(void)) throw Writes();
            foreach (var parameter in callback.GetParameters())
            {
                if (!parameter.ParameterType.IsByRef) continue;
                // Harmony's per-call state is private to the callback class, not a write to native state.
                if (parameter.Name == "__state") continue;
                var args = d.Target.GetParameters();
                var index = Array.FindIndex(args, arg => arg.Name == parameter.Name);
                if (parameter.Name?.StartsWith("__", StringComparison.Ordinal) == true)
                    index = int.TryParse(parameter.Name.Substring(2), out var position) ? position : -1;
                if (index < 0 || index >= args.Length) throw Writes();
                var native = args[index];
                var damage = d.Write == NativeObservationWrite.ReviewedDamagePrefix && callback == d.Prefix
                    && native.Name == "dam" && native.ParameterType == typeof(float) && parameter.ParameterType == typeof(float).MakeByRefType();
                if (!damage && (!native.ParameterType.IsByRef || native.ParameterType != parameter.ParameterType)) throw Writes();
            }
        }
        string[] Values(string[]? values)
        {
            if (values == null) return Array.Empty<string>();
            if (values.Length > 64 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128))
                throw new ArgumentException("native-hook-observation-order-invalid");
            return values.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
        var order = d.Order;
        order = new(order.PrefixPriority, order.PostfixPriority, order.FinalizerPriority,
            Values(order.PrefixBefore), Values(order.PrefixAfter), Values(order.PostfixBefore), Values(order.PostfixAfter),
            Values(order.FinalizerBefore), Values(order.FinalizerAfter));
        if (order.PrefixBefore!.Intersect(order.PrefixAfter!).Any() || order.PostfixBefore!.Intersect(order.PostfixAfter!).Any()
            || order.FinalizerBefore!.Intersect(order.FinalizerAfter!).Any())
            throw new ArgumentException("native-hook-observation-order-contradictory");
        return d with { Order = order };
    }

    private static bool ReviewedDamage(MethodInfo target)
    {
        if (target.DeclaringType?.FullName != "Dam_SyncedDamageBase" || target.IsStatic || !target.IsPublic
            || target.ReturnType != typeof(void) || target.IsGenericMethod || (target.CallingConvention & CallingConventions.VarArgs) != 0
            || target.Name is not ("BulletDamage" or "MeleeDamage" or "ExplosionDamage" or "FireDamage" or "PushDamage" or "FallDamage")) return false;
        var args = target.GetParameters();
        return args.Count(arg => arg.Name == "dam" && arg.ParameterType == typeof(float)) == 1
            && args.All(arg => !arg.IsOut && !arg.ParameterType.IsByRef && !arg.ParameterType.IsPointer
                && !arg.ParameterType.IsByRefLike && !arg.ParameterType.ContainsGenericParameters)
            && target.DeclaringType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Count(method => method.Name == target.Name) == 1;
    }
    private static NotSupportedException Writes() => new("native-hook-observer-writes");
}


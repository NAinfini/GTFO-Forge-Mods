using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ForgeEngine.GameBindings;

/// <summary>One declaration Runtime refused: the callback class and the explicit native-hook-* reason.</summary>
public readonly record struct NativeHookRefusal(Type Callbacks, string Reason);

public static partial class NativeHookRegistration
{
    /// <summary>Declare every callback class of one writer. Classes of this writer that name the same target are one
    /// callback set installed once, ordered as Harmony orders patches (priority, then declaration order); each class
    /// keeps its own callbacks and state. Each target is admitted or refused on its own evidence: a class whose own
    /// declaration is invalid is refused alone, a refused target refuses exactly the classes declared on it, and a
    /// refusal never skips, relaxes or retries another target. Every refusal is returned so the caller can withdraw
    /// the features it belongs to. Unexpected installer failures still propagate.</summary>
    public static IReadOnlyList<NativeHookRefusal> Declare(string writer, IEnumerable<Type> callbacks)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        var declared = callbacks.ToArray();
        var refused = new List<NativeHookRefusal>();
        var targets = new List<(MethodInfo Target, List<(Type Callbacks, NativeHookDefinition Definition)> Parts)>();
        var targetIndices = new Dictionary<MethodInfo, int>();
        foreach (var declaration in declared)
        {
            NativeHookDefinition definition;
            try { definition = Definition(writer, declaration); }
            catch (Exception error) when (error is NotSupportedException or ArgumentException)
            { refused.Add(new(declaration, error.Message)); continue; }
            // Preserve first-target and callback order without scanning every previous target per declaration.
            if (!targetIndices.TryGetValue(definition.Target, out var group))
            {
                targetIndices.Add(definition.Target, targets.Count);
                targets.Add((definition.Target, new() { (declaration, definition) }));
            }
            else targets[group].Parts.Add((declaration, definition));
        }
        foreach (var (_, parts) in targets)
        {
            try { Declare(parts.Select(part => part.Definition).ToArray()); }
            catch (Exception error) when (error is NotSupportedException or ArgumentException)
            { refused.AddRange(parts.Select(part => new NativeHookRefusal(part.Callbacks, error.Message))); }
        }
        try { _declared?.Invoke(new NativeHookDeclared(writer, declared.Length, targets.Count, refused.Count)); }
        catch (Exception error) { ObserverFailed("native-hook-declaration-log", error); }
        return refused;
    }

    private static void Declare(IReadOnlyList<NativeHookDefinition> parts)
    {
        using var declaration = ForgeCore.StartupOperationTiming.Measure(ForgeCore.StartupOperationTiming.Operation.Declaration);
        if (ObservationWriter(parts[0].Target)) throw new NotSupportedException(Refusal("native-hook-writer-conflict", parts[0]));
        Installations.Ensure(parts);
    }

    /// <summary>Resolve a declarative callback class without creating a Harmony processor or native state.
    /// Only a single, exact method target and static prefix/postfix/finalizer callbacks are admitted; whether the target itself
    /// can be patched (<see cref="ForgeCore.Hooks.Unsupported"/>) is decided when it is installed.</summary>
    public static NativeHookDefinition Definition(string writer, Type callbacks)
    {
        using var timing = ForgeCore.StartupOperationTiming.Measure(ForgeCore.StartupOperationTiming.Operation.MethodResolution);
        ArgumentNullException.ThrowIfNull(callbacks);
        var declarations = callbacks.GetCustomAttributes<HarmonyPatch>(false).ToArray();
        if (declarations.Length != 1) throw new NotSupportedException("native-hook-target-declaration-invalid");
        var target = declarations[0].info;
        if (target.declaringType == null || string.IsNullOrWhiteSpace(target.methodName)
            || target.methodType.HasValue && target.methodType.Value != MethodType.Normal)
            throw new NotSupportedException("native-hook-target-declaration-invalid");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        // Harmony's own lookup: the named type's declared members first, then each base type in turn. A type's own
        // member is not ambiguous with overloads it inherits (PlayerLocomotion.ChangeState(PLOC_State, bool) beside
        // StateMachine's ChangeState(int) / (byte) / (Enum)); overloads declared on one level still are.
        MethodInfo[] candidates = Array.Empty<MethodInfo>();
        for (var type = target.declaringType; type != null && candidates.Length == 0; type = type.BaseType)
            candidates = type.GetMethods(flags | BindingFlags.DeclaredOnly).Where(method => method.Name == target.methodName
                && (target.argumentTypes == null || method.GetParameters().Select(p => p.ParameterType).SequenceEqual(target.argumentTypes))).ToArray();
        if (candidates.Length != 1) throw new NotSupportedException("native-hook-target-signature-unverified");
        var methods = callbacks.GetMethods(flags | BindingFlags.DeclaredOnly);
        MethodInfo? Callback<T>() where T : Attribute
        {
            var matches = methods.Where(method => method.IsDefined(typeof(T), false)).ToArray();
            if (matches.Length > 1 || matches.Any(method => !method.IsStatic || method.ContainsGenericParameters))
                throw new NotSupportedException("native-hook-callback-declaration-invalid");
            return matches.SingleOrDefault();
        }
        var prefix = Callback<HarmonyPrefix>(); var postfix = Callback<HarmonyPostfix>(); var finalizer = Callback<HarmonyFinalizer>();
        if (methods.Any(method => method.IsDefined(typeof(HarmonyTranspiler), false)
                || method.IsDefined(typeof(HarmonyTargetMethod), false) || method.IsDefined(typeof(HarmonyTargetMethods), false)
                || method.IsDefined(typeof(HarmonyPrepare), false) || method.IsDefined(typeof(HarmonyCleanup), false)))
            throw new NotSupportedException("native-hook-callback-kind-unsupported");
        int Order(MethodInfo? method, int fallback)
            => method?.GetCustomAttribute<HarmonyPriority>(false)?.info.priority ?? fallback;
        return new(writer, candidates[0], prefix, postfix, finalizer,
            Order(prefix, Priority.Normal), Order(postfix, Priority.Normal), Order(finalizer, Priority.Normal));
    }
}

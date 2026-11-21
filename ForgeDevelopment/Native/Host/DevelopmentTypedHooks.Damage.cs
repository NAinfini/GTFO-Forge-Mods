using System;
using System.Linq;
using System.Reflection;

namespace ForgeDevelopment.Native;

/// <summary>The existing explicitly armed damage experiment is the sole argument writer. Its named
/// float dam prefix is preserved; the shared observation path never writes __args or a result.</summary>
internal static class DevelopmentDamageHooks
{
    private static readonly string[] Names =
        { "BulletDamage", "MeleeDamage", "ExplosionDamage", "FireDamage", "PushDamage", "FallDamage" };
    private static readonly DevelopmentNativeHooks.ReviewedAdapter Adapter = new(
        "development.explicit-damage-writer", Rejection,
        (_, order) => new(typeof(DevelopmentDamageHooks).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)!,
            typeof(DevelopmentDamageHooks).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)!,
            typeof(DevelopmentDamageHooks).GetMethod(nameof(Finalizer), BindingFlags.NonPublic | BindingFlags.Static)!, order),
        SupportsDamageWriter: true);

    internal static DevelopmentNativeHooks.ReviewedAdapter? For(MethodBase target)
        => Rejection(target) == null ? Adapter : null;
    internal static void Register(DevelopmentNativeHooks hooks) => hooks.RegisterAdapter(Adapter);

    private static string? Rejection(MethodBase target)
    {
        if (target is not MethodInfo method || method.DeclaringType != typeof(Dam_SyncedDamageBase)
            || method.IsStatic || !method.IsPublic || method.ReturnType != typeof(void)
            || method.IsGenericMethod || method.ContainsGenericParameters || !Names.Contains(method.Name, StringComparer.Ordinal)
            || (method.CallingConvention & CallingConventions.VarArgs) != 0)
            return "not-reviewed-damage-writer-target";
        // Existing ExperimentDamage selected these declared public instance entries by name and bound only dam.
        // Do not invent the rest of their signatures; they use the unchanged ordinary by-value __args boundary.
        // Every ref/out/native pointer is rejected here; only this adapter's float dam binding can write back.
        var parameters = method.GetParameters();
        if (parameters.Count(p => p.Name == "dam" && p.ParameterType == typeof(float)) != 1
            || parameters.Any(p => p.IsOut || p.ParameterType.IsByRef || p.ParameterType.IsPointer
                || p.ParameterType.IsByRefLike || p.ParameterType.ContainsGenericParameters))
            return "not-reviewed-damage-writer-signature";
        if (typeof(Dam_SyncedDamageBase).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Count(candidate => candidate.Name == method.Name) != 1)
            return "ambiguous-damage-writer-target";
        return null;
    }

    private static void Prefix(object __instance, object?[] __args, ref float dam, MethodBase __originalMethod,
        bool __runOriginal, out DevelopmentNativeHooks.Frame? __state)
    {
        DevelopmentNativeHooks.Begin(__instance, __args, __originalMethod, __runOriginal, out __state);
        DevelopmentNativeHooks.ApplyDamageWriter(__instance, __originalMethod, ref dam);
    }
    // Harmony's __args may still contain the prefix's pre-write snapshot. Read the actual named scalar
    // and correct only an observer-owned copy; mutating Harmony's array could write arguments back.
    private static object?[] ReturnedArguments(object?[] arguments, float damage, MethodBase target)
    {
        var copy = (object?[])arguments.Clone();
        var parameters = target.GetParameters();
        for (var index = 0; index < parameters.Length; index++)
            if (parameters[index].Name == "dam" && parameters[index].ParameterType == typeof(float))
            { copy[index] = damage; break; }
        return copy;
    }
    private static void Postfix(object __instance, object?[] __args, float dam, MethodBase __originalMethod,
        bool __runOriginal, DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Return(__instance, ReturnedArguments(__args, dam, __originalMethod), null, __runOriginal, __state);
    private static void Finalizer(object __instance, object?[] __args, float dam, MethodBase __originalMethod, bool __runOriginal,
        Exception? __exception, DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Finish(__instance, ReturnedArguments(__args, dam, __originalMethod), null, __runOriginal, __exception, __state);
}

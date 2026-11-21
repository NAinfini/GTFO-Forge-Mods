using System;
using System.Linq;
using System.Reflection;
using CullingSystem;
using GameData;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>Reviewed adapters for the two fixed generation/culling observation points whose game methods carry
/// ref/out parameters. Without them the backend refuses the target and Plugin.Load rolls the whole plugin back.
/// Every callback only copies the current values: no ref/out is ever assigned and no result or exception is replaced.</summary>
internal static class DevelopmentGenerationHooks
{
    private static readonly DevelopmentNativeHooks.ReviewedAdapter MarkerSpawn = new(
        "development.marker-spawn-ref-out", MarkerSpawnRejection,
        (_, order) => DevelopmentTypedHooks.Patches(typeof(MarkerSpawnCallbacks), order));
    private static readonly DevelopmentNativeHooks.ReviewedAdapter ShadowData = new(
        "development.shadow-rendering-out", ShadowDataRejection,
        (_, order) => DevelopmentTypedHooks.Patches(typeof(ShadowDataCallbacks), order));

    internal static DevelopmentNativeHooks.ReviewedAdapter? For(MethodBase target)
        => MarkerSpawnRejection(target) == null ? MarkerSpawn : ShadowDataRejection(target) == null ? ShadowData : null;

    internal static void Register(DevelopmentNativeHooks hooks)
    {
        hooks.RegisterAdapter(MarkerSpawn);
        hooks.RegisterAdapter(ShadowData);
    }

    // IL2CPP dump, build 20403457, RVA 0x12B4A00:
    // public bool LG_MarkerSpawner.TrySpawnRandomPrefab(ExpeditionFunction func, float randomSel, float randomRot,
    //     float randomRotNoise, ref bool functionWasSpawned, out GameObject spawnedGO, bool debug = false)
    private static string? MarkerSpawnRejection(MethodBase target)
    {
        if (target is not MethodInfo method || method.IsStatic || method.ReturnType != typeof(bool)
            || method.IsGenericMethod || method.ContainsGenericParameters
            || method.DeclaringType != typeof(LG_MarkerSpawner) || method.Name != "TrySpawnRandomPrefab")
            return "not-reviewed-marker-spawn-target";
        var parameters = method.GetParameters();
        return parameters.Length == 7
            && parameters[0].ParameterType == typeof(ExpeditionFunction)
            && parameters[1].ParameterType == typeof(float) && parameters[2].ParameterType == typeof(float)
            && parameters[3].ParameterType == typeof(float)
            && parameters[4].ParameterType == typeof(bool).MakeByRefType()
            && parameters[5].ParameterType == typeof(GameObject).MakeByRefType()
            && parameters[6].ParameterType == typeof(bool)
            && !parameters[0].IsOut && !parameters[1].IsOut && !parameters[2].IsOut && !parameters[3].IsOut
            && !parameters[4].IsOut && parameters[5].IsOut && !parameters[6].IsOut
            ? null : "not-reviewed-marker-spawn-signature";
    }

    // IL2CPP dump, build 20403457, C_CullingCluster override (RVA 0x17BD2A0):
    // public override void GetShadowRenderingData(out bool allowedToRenderInstanced,
    //     out C_ShadowRenderGroup[] shadowRenderGroups, out Renderer[] shadowRenderers)
    private static string? ShadowDataRejection(MethodBase target)
    {
        if (target is not MethodInfo method || method.IsStatic || method.ReturnType != typeof(void)
            || method.IsGenericMethod || method.ContainsGenericParameters
            || method.DeclaringType != typeof(C_CullingCluster) || method.Name != "GetShadowRenderingData")
            return "not-reviewed-shadow-data-target";
        var parameters = method.GetParameters();
        return parameters.Length == 3 && parameters.All(p => p.IsOut)
            && parameters[0].ParameterType == typeof(bool).MakeByRefType()
            && parameters[1].ParameterType == typeof(Il2CppReferenceArray<C_ShadowRenderGroup>).MakeByRefType()
            && parameters[2].ParameterType == typeof(Il2CppReferenceArray<Renderer>).MakeByRefType()
            ? null : "not-reviewed-shadow-data-signature";
    }

    // The out reference is not assigned on entry, so the prefix reports a placeholder. The postfix copies the
    // current ref and out values; the finalizer does so only when the original completed normally.
    private static class MarkerSpawnCallbacks
    {
        private static void Prefix(LG_MarkerSpawner __instance, ExpeditionFunction __0, float __1, float __2, float __3,
            ref bool __4, ref GameObject __5, bool __6, MethodBase __originalMethod, bool __runOriginal,
            out DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Begin(__instance, new object?[] { __0, __1, __2, __3, __4, null, __6 },
                __originalMethod, __runOriginal, out __state);
        private static void Postfix(LG_MarkerSpawner __instance, ExpeditionFunction __0, float __1, float __2, float __3,
            ref bool __4, ref GameObject __5, bool __6, bool __result, bool __runOriginal, DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Return(__instance, new object?[] { __0, __1, __2, __3, __4, __5, __6 },
                __result, __runOriginal, __state);
        private static void Finalizer(LG_MarkerSpawner __instance, ExpeditionFunction __0, float __1, float __2, float __3,
            ref bool __4, ref GameObject __5, bool __6, bool __result, bool __runOriginal, Exception? __exception,
            DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Finish(__instance, new object?[] { __0, __1, __2, __3, __4, __exception == null ? __5 : null, __6 },
                __result, __runOriginal, __exception, __state);
    }

    private static class ShadowDataCallbacks
    {
        private static void Prefix(C_CullingCluster __instance, ref bool __0, ref Il2CppReferenceArray<C_ShadowRenderGroup> __1,
            ref Il2CppReferenceArray<Renderer> __2, MethodBase __originalMethod, bool __runOriginal,
            out DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Begin(__instance, new object?[] { false, null, null }, __originalMethod, __runOriginal, out __state);
        private static void Postfix(C_CullingCluster __instance, ref bool __0, ref Il2CppReferenceArray<C_ShadowRenderGroup> __1,
            ref Il2CppReferenceArray<Renderer> __2, bool __runOriginal, DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Return(__instance, new object?[] { __0, __1, __2 }, null, __runOriginal, __state);
        private static void Finalizer(C_CullingCluster __instance, ref bool __0, ref Il2CppReferenceArray<C_ShadowRenderGroup> __1,
            ref Il2CppReferenceArray<Renderer> __2, bool __runOriginal, Exception? __exception, DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Finish(__instance, __exception == null ? new object?[] { __0, __1, __2 }
                : new object?[] { false, null, null }, null, __runOriginal, __exception, __state);
    }
}

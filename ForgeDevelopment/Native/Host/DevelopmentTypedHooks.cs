using System;
using System.Linq;
using System.Reflection;
using Enemies;

namespace ForgeDevelopment.Native;

/// <summary>Target-owned reviewed adapters. Selection depends on the original declaration, never on
/// which subscriber arrived first. Ordinary by-value signatures keep the backend's existing path.</summary>
internal static class DevelopmentTypedHooks
{
    private static readonly DevelopmentNativeHooks.ReviewedAdapter Noise = new(
        "development.noise-ref", NoiseRejection, (_, order) => Patches(typeof(NoiseCallbacks), order));
    private static readonly DevelopmentNativeHooks.ReviewedAdapter MakeNoise = new(
        "development.noise-value", MakeNoiseRejection, (_, order) => Patches(typeof(MakeNoiseCallbacks), order));
    private static readonly DevelopmentNativeHooks.ReviewedAdapter ProcessNoise = new(
        "development.process-noise-value", ProcessNoiseRejection, (_, order) => Patches(typeof(MakeNoiseCallbacks), order));
    private static readonly DevelopmentNativeHooks.ReviewedAdapter ProcessNode = new(
        "development.process-node-ref", ProcessNodeRejection, (_, order) => Patches(typeof(ProcessNodeCallbacks), order));

    internal static DevelopmentNativeHooks.ReviewedAdapter? For(MethodBase target)
    {
        if (NoiseRejection(target) == null) return Noise;
        if (MakeNoiseRejection(target) == null) return MakeNoise;
        if (ProcessNoiseRejection(target) == null) return ProcessNoise;
        if (ProcessNodeRejection(target) == null) return ProcessNode;
        return DevelopmentPopulationHooks.For(target) ?? DevelopmentWaveHooks.For(target) ?? DevelopmentDamageHooks.For(target) ?? DevelopmentGenerationHooks.For(target);
    }

    internal static void Register(DevelopmentNativeHooks hooks)
    {
        hooks.RegisterAdapter(Noise);
        hooks.RegisterAdapter(MakeNoise);
        hooks.RegisterAdapter(ProcessNoise);
        hooks.RegisterAdapter(ProcessNode);
        DevelopmentPopulationHooks.Register(hooks);
        DevelopmentWaveHooks.Register(hooks);
        DevelopmentDamageHooks.Register(hooks);
        DevelopmentGenerationHooks.Register(hooks);
    }

    private static string? NoiseRejection(MethodBase target)
    {
        if (target is not MethodInfo method || method.IsStatic || method.ReturnType != typeof(void)
            || method.IsGenericMethod || method.ContainsGenericParameters
            || !((method.Name == "OnNoiseDetected"
                    && (method.DeclaringType == typeof(EB_Hibernating) || method.DeclaringType == typeof(EB_Patrolling)))
                || (method.Name == "ListenerDetectNoise" && method.DeclaringType == typeof(EnemyAgent))))
            return "not-reviewed-noise-ref-target";
        var parameters = method.GetParameters();
        return parameters.Length == 2 && !parameters[0].IsOut && !parameters[1].IsOut
            && parameters[0].ParameterType == typeof(NM_NoiseData).MakeByRefType()
            && parameters[1].ParameterType == typeof(float) ? null : "not-reviewed-noise-ref-signature";
    }

    private static string? ProcessNoiseRejection(MethodBase target)
    {
        if (target is not MethodInfo method || !method.IsStatic || method.ReturnType != typeof(void)
            || method.IsGenericMethod || method.ContainsGenericParameters
            || method.DeclaringType != typeof(NoiseManager) || method.Name != "ProcessNoise")
            return "not-reviewed-process-noise-target";
        var parameters = method.GetParameters();
        return parameters.Length == 1 && !parameters[0].IsOut && parameters[0].ParameterType == typeof(NM_NoiseData)
            ? null : "not-reviewed-process-noise-signature";
    }

    private static string? ProcessNodeRejection(MethodBase target)
    {
        if (target is not MethodInfo method || !method.IsStatic || method.ReturnType != typeof(void)
            || method.IsGenericMethod || method.ContainsGenericParameters
            || method.DeclaringType != typeof(NoiseManager) || method.Name != "ProcessNode")
            return "not-reviewed-process-node-target";
        var parameters = method.GetParameters();
        return parameters.Length == 4 && parameters.All(p => !p.IsOut)
            && parameters[0].ParameterType == typeof(AIGraph.AIG_CourseNode)
            && parameters[1].ParameterType == typeof(NM_NoiseData).MakeByRefType()
            && parameters[2].ParameterType == typeof(bool)
            && parameters[3].ParameterType == typeof(int).MakeByRefType()
            ? null : "not-reviewed-process-node-signature";
    }

    private static string? MakeNoiseRejection(MethodBase target)
    {
        if (target is not MethodInfo method || !method.IsStatic || method.ReturnType != typeof(void)
            || method.IsGenericMethod || method.ContainsGenericParameters
            || method.DeclaringType != typeof(NoiseManager) || method.Name != nameof(NoiseManager.MakeNoise))
            return "not-reviewed-make-noise-target";
        var parameters = method.GetParameters();
        return parameters.Length == 1 && !parameters[0].IsOut && parameters[0].ParameterType == typeof(NM_NoiseData)
            ? null : "not-reviewed-make-noise-signature";
    }

    internal static DevelopmentNativeHooks.PatchSet Patches(Type owner, DevelopmentNativeHooks.Order order)
        => new(owner.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!,
            owner.GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)!,
            owner.GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic)!, order);

    // Native body/signature evidence: h10-noise-bodies-20403457.json,
    // RVAs 0x166bed0, 0x17f2050 and ListenerDetectNoise 0x1561690.
    // Keep the generated typed ref bridge already used by RecNoiseTrace; do not box a generic __args ref.
    // These callbacks never assign the ref or read its fields. Each consumer retains its own read/rate gate.
    private static class NoiseCallbacks
    {
        private static void Prefix(object __instance, ref NM_NoiseData __0, float __1,
            MethodBase __originalMethod, bool __runOriginal, out DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Begin(__instance, new object?[] { __0, __1 }, __originalMethod, __runOriginal, out __state);
        private static void Postfix(object __instance, ref NM_NoiseData __0, float __1,
            bool __runOriginal, DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Return(__instance, new object?[] { __0, __1 }, null, __runOriginal, __state);
        private static void Finalizer(object __instance, ref NM_NoiseData __0, float __1,
            bool __runOriginal, Exception? __exception, DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Finish(__instance, new object?[] { __0, __1 }, null, __runOriginal, __exception, __state);
    }

    // Build 20403457 MakeNoise (0x16ff830) and ProcessNoise (0x16fff10) are static
    // void(NM_NoiseData), not refs or struct receivers. Admission remains separate and exact.
    // Copying the argument wrapper here is not native-field observation or a lifetime guarantee.
    private static class MakeNoiseCallbacks
    {
        private static void Prefix(NM_NoiseData __0, MethodBase __originalMethod, bool __runOriginal,
            out DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Begin(null, new object?[] { __0 }, __originalMethod, __runOriginal, out __state);
        private static void Postfix(NM_NoiseData __0, bool __runOriginal, DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Return(null, new object?[] { __0 }, null, __runOriginal, __state);
        private static void Finalizer(NM_NoiseData __0, bool __runOriginal, Exception? __exception, DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Finish(null, new object?[] { __0 }, null, __runOriginal, __exception, __state);
    }

    // Build 20403457 ProcessNode (0x16ffbb0) has two physical refs. Copy their current
    // values in every phase, including after foreign postfixes, without assigning either ref.
    private static class ProcessNodeCallbacks
    {
        private static void Prefix(AIGraph.AIG_CourseNode __0, ref NM_NoiseData __1, bool __2, ref int __3,
            MethodBase __originalMethod, bool __runOriginal, out DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Begin(null, new object?[] { __0, __1, __2, __3 }, __originalMethod, __runOriginal, out __state);
        private static void Postfix(AIGraph.AIG_CourseNode __0, ref NM_NoiseData __1, bool __2, ref int __3,
            bool __runOriginal, DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Return(null, new object?[] { __0, __1, __2, __3 }, null, __runOriginal, __state);
        private static void Finalizer(AIGraph.AIG_CourseNode __0, ref NM_NoiseData __1, bool __2, ref int __3,
            bool __runOriginal, Exception? __exception, DevelopmentNativeHooks.Frame? __state)
            => DevelopmentNativeHooks.Finish(null, new object?[] { __0, __1, __2, __3 }, null, __runOriginal, __exception, __state);
    }
}

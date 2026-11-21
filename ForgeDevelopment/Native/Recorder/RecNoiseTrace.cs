using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using AIGraph;
using Enemies;

namespace ForgeDevelopment.Native;

/// <summary>Exact, read-only noise adapters. Body evidence is build 20403457 provenance, not runtime
/// binary verification. Native installation/ABI and gameplay still require same-build game validation.
/// Only an admitted callback may copy the reviewed value payload; referenced objects remain unverified.</summary>
internal static class RecNoiseTrace
{
    internal static MethodInfo PrefixMethod { get; } = Callback(nameof(Prefix));
    internal static MethodInfo PostfixMethod { get; } = Callback(nameof(Postfix));
    internal static MethodInfo FinalizerMethod { get; } = Callback(nameof(Finalizer));
    internal static MethodInfo MakeNoisePrefixMethod { get; } = Callback(nameof(MakeNoisePrefix));
    internal static MethodInfo MakeNoisePostfixMethod { get; } = Callback(nameof(MakeNoisePostfix));
    internal static MethodInfo ProcessNodePrefixMethod { get; } = Callback(nameof(ProcessNodePrefix));
    internal static MethodInfo ProcessNodePostfixMethod { get; } = Callback(nameof(ProcessNodePostfix));
    private static MethodInfo Callback(string name) => typeof(RecNoiseTrace).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static bool Shape(MethodInfo method, Type owner, string name, bool isStatic, params Type[] types)
    {
        if (method.DeclaringType != owner || method.Name != name || method.IsStatic != isStatic
            || method.ReturnType != typeof(void) || method.IsGenericMethod || method.ContainsGenericParameters) return false;
        var parameters = method.GetParameters();
        if (parameters.Length != types.Length) return false;
        for (var index = 0; index < types.Length; index++)
            if (parameters[index].IsOut || parameters[index].ParameterType != types[index]) return false;
        return true;
    }
    internal static bool SupportsMakeNoise(MethodInfo method)
        => Shape(method, typeof(NoiseManager), "MakeNoise", true, typeof(NM_NoiseData));
    internal static bool SupportsProcessNoise(MethodInfo method)
        => Shape(method, typeof(NoiseManager), "ProcessNoise", true, typeof(NM_NoiseData));
    internal static bool SupportsProcessNode(MethodInfo method)
        => Shape(method, typeof(NoiseManager), "ProcessNode", true, typeof(AIG_CourseNode),
            typeof(NM_NoiseData).MakeByRefType(), typeof(bool), typeof(int).MakeByRefType());
    internal static bool SupportsValue(MethodInfo method) => SupportsMakeNoise(method) || SupportsProcessNoise(method);
    internal static bool SupportsStatic(MethodInfo method) => SupportsValue(method) || SupportsProcessNode(method);
    internal static bool Supports(MethodInfo method)
        => Shape(method, typeof(EB_Hibernating), "OnNoiseDetected", false, typeof(NM_NoiseData).MakeByRefType(), typeof(float))
            || Shape(method, typeof(EB_Patrolling), "OnNoiseDetected", false, typeof(NM_NoiseData).MakeByRefType(), typeof(float))
            || Shape(method, typeof(EnemyAgent), "ListenerDetectNoise", false, typeof(NM_NoiseData).MakeByRefType(), typeof(float));

    private static void MakeNoisePrefix(NM_NoiseData __0, MethodBase __originalMethod, out RecTracerPatches.Frame __state)
        => RecTracerPatches.Begin(null!, Array.Empty<object?>(), __originalMethod, out __state,
            () => MakeNoiseArguments(__0, __originalMethod));
    private static void MakeNoisePostfix(NM_NoiseData __0, MethodBase __originalMethod,
        RecTracerPatches.Frame __state, bool __runOriginal)
        => RecTracerPatches.EndVoid(null!, __originalMethod, __state, __runOriginal,
            () => MakeNoiseArguments(__0, __originalMethod));

    private static void Prefix(object __instance, ref NM_NoiseData noiseData, float distance01, MethodBase __originalMethod,
        out RecTracerPatches.Frame __state)
    {
        var noise = noiseData; // Preserve the current ref; getters run only after Begin admits the call.
        RecTracerPatches.Begin(__instance, Array.Empty<object?>(), __originalMethod, out __state,
            () => Arguments(noise, distance01, __originalMethod));
    }
    private static void Postfix(object __instance, ref NM_NoiseData noiseData, float distance01, MethodBase __originalMethod,
        RecTracerPatches.Frame __state, bool __runOriginal)
    {
        var noise = noiseData;
        RecTracerPatches.EndVoid(__instance, __originalMethod, __state, __runOriginal,
            () => Arguments(noise, distance01, __originalMethod));
    }
    private static void ProcessNodePrefix(AIG_CourseNode __0, ref NM_NoiseData __1, bool __2, ref int __3,
        MethodBase __originalMethod, out RecTracerPatches.Frame __state)
    {
        var noise = __1; var rays = __3;
        RecTracerPatches.Begin(null!, Array.Empty<object?>(), __originalMethod, out __state,
            () => ProcessNodeArguments(__0, noise, __2, rays, __originalMethod));
    }
    private static void ProcessNodePostfix(AIG_CourseNode __0, ref NM_NoiseData __1, bool __2, ref int __3,
        MethodBase __originalMethod, RecTracerPatches.Frame __state, bool __runOriginal)
    {
        var noise = __1; var rays = __3;
        RecTracerPatches.EndVoid(null!, __originalMethod, __state, __runOriginal,
            () => ProcessNodeArguments(__0, noise, __2, rays, __originalMethod));
    }
    private static void Finalizer(Exception? __exception, RecTracerPatches.Frame __state)
        => RecTracerPatches.Finalizer(__exception, __state);

    internal static object?[] Arguments(NM_NoiseData noise, float distance, MethodBase? method = null)
        => new object?[] { ReadNoise(noise, "typed-ref-NM_NoiseData", Reviewed(method, false)), distance };
    internal static object?[] MakeNoiseArguments(NM_NoiseData noise, MethodBase? method = null)
    {
        var value = ReadNoise(noise, "typed-value-NM_NoiseData", Reviewed(method, true));
        if (method is MethodInfo typed && SupportsProcessNoise(typed))
            value["nativeLocalMutation"] = "unobserved-by-value-copy";
        return new object?[] { value };
    }
    internal static object?[] ProcessNodeArguments(AIG_CourseNode node, NM_NoiseData noise, bool raycast, int maxRays,
        MethodBase? method = null)
    {
        var reviewed = RecObservation.Active && method is MethodInfo typed && SupportsProcessNode(typed);
        return new object?[] { reviewed ? PointerIdentity(node) : Unknown("unreviewed-noise-callback"),
            ReadNoise(noise, "typed-ref-NM_NoiseData", reviewed), raycast, maxRays };
    }
    private static bool Reviewed(MethodBase? method, bool byValue)
        => RecObservation.Active && method is MethodInfo typed && (byValue ? SupportsValue(typed)
            : Supports(typed));

    // h10-noise-bodies-20403457.json: MakeNoise copies 0x30 bytes synchronously; ProcessNode reads
    // position/radii/yScale; ProcessNoise reads booleans; listener bodies read type/position.
    // This exception is local to the live typed payload. No native-object lifetime lease is created.
    private static Dictionary<string, object?> ReadNoise(NM_NoiseData noise, string reader, bool reviewed)
    {
        var values = new Dictionary<string, object?> { ["reader"] = reader };
        if (!reviewed)
        {
            values["$nativeRead"] = noise is null ? "noise-payload-unavailable"
                : RecReflect.Probe.NativeReadUnavailable(noise) ?? "unreviewed-noise-callback";
            return values;
        }
        values["evidenceBuild"] = "20403457";
        values["readScope"] = "admitted-typed-payload; referenced-object-lifetime-unverified";
        Read(values, "position", () =>
        {
            var position = noise.position;
            return new Dictionary<string, object?> { ["x"] = Finite(position.x), ["y"] = Finite(position.y), ["z"] = Finite(position.z) };
        });
        Read(values, "radiusMin", () => Finite(noise.radiusMin));
        Read(values, "radiusMax", () => Finite(noise.radiusMax));
        Read(values, "yScale", () => Finite(noise.yScale));
        Read(values, "type", () => (int)noise.type);
        Read(values, "includeToNeightbourAreas", () => noise.includeToNeightbourAreas);
        Read(values, "raycastFirstNode", () => noise.raycastFirstNode);
        Read(values, "node", () => PointerIdentity(noise.node));
        Read(values, "noiseMaker", () => PointerIdentity(noise.noiseMaker));
        // Nested sentinels keep valid scalar siblings selectable while whole-observation completeness
        // remains false. A pointer is not a NodeID, player identity, location or membership proof.
        values["courseNode"] = values["node"] == null ? null : Unknown("node-id-unverified");
        values["sourceLocation"] = values["noiseMaker"] == null ? null : Unknown("maker-location-unverified");
        values["noiseOriginLocation"] = Unknown("origin-membership-unverified; position-observed-separately");
        return values;
    }
    private static object Finite(float value) => float.IsFinite(value) ? value
        : new Dictionary<string, object?> { ["$error"] = "nonfinite-noise-scalar", ["observed"] = value.ToString("R", CultureInfo.InvariantCulture) };
    private static Dictionary<string, object?> Unknown(string reason) => new() { ["$nativeRead"] = reason };
    private static object? PointerIdentity(object? value)
    {
        if (value == null) return null;
        try
        {
            if (!RecReflect.Probe.TryInstanceId(value, out var pointer) || pointer == 0)
                return Unknown("native-pointer-unavailable");
            return new Dictionary<string, object?>
            {
                ["kind"] = "native-pointer",
                ["pointer"] = "0x" + pointer.ToString("X", CultureInfo.InvariantCulture),
                ["lifetime"] = "unverified"
            };
        }
        catch (Exception error) { return new Dictionary<string, object?> { ["$error"] = "pointer-read-error:" + error.GetType().Name }; }
    }
    private static void Read(Dictionary<string, object?> values, string field, Func<object?> read)
    {
        try { values[field] = read(); }
        catch (Exception error)
        {
            values[field] = new Dictionary<string, object?> { ["$error"] = error.GetType().Name + ": " + RecReflect.Truncate(error.Message, 200) };
        }
    }
}

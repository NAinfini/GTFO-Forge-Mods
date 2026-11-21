using System;
using System.Linq;
using System.Reflection;
using AIGraph;
using UnityEngine;

namespace ForgeDevelopment.Native;

// The one reviewed wave signature: the positioned Mastermind.TriggerSurvivalWave, whose fourth parameter is `out ushort eventID`.
// The out storage is read only after a successful return; never on entry or failure. ForgeMap already patches the same
// overload with the same by-reference spelling.
internal static class DevelopmentWaveHooks
{
    private static readonly DevelopmentNativeHooks.ReviewedAdapter Adapter = new("survival-wave-out", RejectionReason, Select);
    internal static DevelopmentNativeHooks.ReviewedAdapter? For(MethodBase target) => RejectionReason(target) == null ? Adapter : null;
    internal static void Register(DevelopmentNativeHooks hooks) => hooks.RegisterAdapter(Adapter);

    private static readonly Type[] Signature =
    {
        typeof(AIG_CourseNode), typeof(uint), typeof(uint), typeof(ushort).MakeByRefType(), typeof(SurvivalWaveSpawnType),
        typeof(float), typeof(float), typeof(Vector3), typeof(bool), typeof(bool), typeof(Vector3), typeof(string)
    };

    internal static string? RejectionReason(MethodBase target)
    {
        if (target is not MethodInfo method || method.IsStatic || method.IsGenericMethod || method.DeclaringType != typeof(Mastermind)
            || method.Name != nameof(Mastermind.TriggerSurvivalWave) || method.ReturnType != typeof(bool))
            return "not-reviewed-wave-signature";
        var parameters = method.GetParameters();
        return parameters.Select(parameter => parameter.ParameterType).SequenceEqual(Signature) && parameters[3].IsOut
            ? null : "not-reviewed-wave-signature";
    }

    private static DevelopmentNativeHooks.PatchSet Select(MethodBase target, DevelopmentNativeHooks.Order order)
    {
        if (RejectionReason(target) != null) throw new NotSupportedException("Wave adapter signature changed.");
        MethodInfo Method(string name) => typeof(DevelopmentWaveHooks).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        return new(Method(nameof(Before)), Method(nameof(After)), Method(nameof(Finally)), order);
    }

    private static void Before(Mastermind __instance, AIG_CourseNode __0, uint __1, uint __2, SurvivalWaveSpawnType __4,
        float __5, float __6, Vector3 __7, bool __8, bool __9, Vector3 __10, string __11,
        MethodBase __originalMethod, bool __runOriginal, out DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Begin(__instance, new object?[] { __0, __1, __2, (ushort)0, __4, __5, __6, __7, __8, __9, __10, __11 },
            __originalMethod, __runOriginal, out __state);

    private static void After(Mastermind __instance, AIG_CourseNode __0, uint __1, uint __2, ushort __3, SurvivalWaveSpawnType __4,
        float __5, float __6, Vector3 __7, bool __8, bool __9, Vector3 __10, string __11,
        bool __result, bool __runOriginal, DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Return(__instance, new object?[] { __0, __1, __2, __3, __4, __5, __6, __7, __8, __9, __10, __11 },
            __result, __runOriginal, __state);

    private static void Finally(Mastermind __instance, AIG_CourseNode __0, uint __1, uint __2, ref ushort __3, SurvivalWaveSpawnType __4,
        float __5, float __6, Vector3 __7, bool __8, bool __9, Vector3 __10, string __11,
        bool __result, bool __runOriginal, Exception? __exception, DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Finish(__instance, new object?[] { __0, __1, __2, __exception == null ? __3 : (ushort)0, __4, __5, __6, __7, __8, __9, __10, __11 },
            __result, __runOriginal, __exception, __state);
}

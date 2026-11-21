using System;
using System.Linq;
using System.Reflection;
using Agents;
using Enemies;
using GameData;
using LevelGeneration;

namespace ForgeDevelopment.Native;

// Three reviewed population signatures; Runtime owns their by-reference bridge.
// Ref storage is read only after a successful return; never read out storage on entry or failure.
internal static class DevelopmentPopulationHooks
{
    private static readonly DevelopmentNativeHooks.ReviewedAdapter Adapter = new("population-three-signatures", RejectionReason, Select);
    internal static DevelopmentNativeHooks.ReviewedAdapter? For(MethodBase target) => RejectionReason(target) == null ? Adapter : null;
    internal static void Register(DevelopmentNativeHooks hooks) => hooks.RegisterAdapter(Adapter);

    private static string? RejectionReason(MethodBase target)
    {
        if (target is not MethodInfo method || method.IsStatic || method.IsGenericMethod) return "not-reviewed-population-signature";
        var parameters = method.GetParameters();
        var types = parameters.Select(parameter => parameter.ParameterType).ToArray();
        if (method.DeclaringType == typeof(EnemyPopulationDataBlock) && method.Name == "TryGetEnemyData"
            && method.ReturnType == typeof(bool) && types.SequenceEqual(new[] { typeof(eEnemyRole), typeof(eEnemyRoleDifficulty), typeof(EnemyDataBlock).MakeByRefType(), typeof(float).MakeByRefType() })
            && parameters[2].IsOut && parameters[3].IsOut) return null;
        if (method.DeclaringType == typeof(LG_PopulateZone) && method.Name == "TryPlaceGroup"
            && method.ReturnType == typeof(bool) && types.SequenceEqual(new[] { typeof(EnemyGroupRandomizer), typeof(float).MakeByRefType(), typeof(bool) })
            && !parameters[1].IsOut) return null;
        if (method.DeclaringType == typeof(EnemyGroupScoreSpawnData) && method.Name == "AddEnemyData"
            && method.ReturnType == typeof(void) && types.SequenceEqual(new[] { typeof(AgentMode), typeof(eEnemyRole), typeof(eEnemyRoleDifficulty), typeof(float), typeof(bool), typeof(int).MakeByRefType() })
            && !parameters[5].IsOut) return null;
        return "not-reviewed-population-signature";
    }
    private static DevelopmentNativeHooks.PatchSet Select(MethodBase target, DevelopmentNativeHooks.Order order)
    {
        if (RejectionReason(target) != null) throw new NotSupportedException("Population adapter signature changed.");
        var name = target.Name == "TryGetEnemyData" ? "Selection" : target.Name == "TryPlaceGroup" ? "Placement" : "Adding";
        MethodInfo Method(string suffix) => typeof(DevelopmentPopulationHooks).GetMethod(name + suffix, BindingFlags.Static | BindingFlags.NonPublic)!;
        return new(Method("Before"), Method("After"), Method("Finally"), order);
    }
    private static void SelectionBefore(EnemyPopulationDataBlock __instance, eEnemyRole __0, eEnemyRoleDifficulty __1,
        MethodBase __originalMethod, bool __runOriginal, out DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Begin(__instance, new object?[] { __0, __1, null, 0f }, __originalMethod, __runOriginal, out __state);
    private static void SelectionAfter(EnemyPopulationDataBlock __instance, eEnemyRole __0, eEnemyRoleDifficulty __1,
        EnemyDataBlock __2, float __3, bool __result, bool __runOriginal, DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Return(__instance, new object?[] { __0, __1, __2, __3 }, __result, __runOriginal, __state);
    private static void SelectionFinally(EnemyPopulationDataBlock __instance, eEnemyRole __0, eEnemyRoleDifficulty __1,
        ref EnemyDataBlock __2, ref float __3, bool __result, bool __runOriginal, Exception? __exception, DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Finish(__instance, __exception == null ? new object?[] { __0, __1, __2, __3 }
            : new object?[] { __0, __1, null, 0f }, __result, __runOriginal, __exception, __state);
    private static void PlacementBefore(LG_PopulateZone __instance, EnemyGroupRandomizer __0, float __1, bool __2,
        MethodBase __originalMethod, bool __runOriginal, out DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Begin(__instance, new object?[] { __0, __1, __2 }, __originalMethod, __runOriginal, out __state);
    private static void PlacementAfter(LG_PopulateZone __instance, EnemyGroupRandomizer __0, float __1, bool __2,
        bool __result, bool __runOriginal, DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Return(__instance, new object?[] { __0, __1, __2 }, __result, __runOriginal, __state);
    private static void PlacementFinally(LG_PopulateZone __instance, EnemyGroupRandomizer __0, ref float __1, bool __2,
        bool __result, bool __runOriginal, Exception? __exception, DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Finish(__instance, __exception == null ? new object?[] { __0, __1, __2 }
            : new object?[] { __0, 0f, __2 }, __result, __runOriginal, __exception, __state);
    private static void AddingBefore(EnemyGroupScoreSpawnData __instance, AgentMode __0, eEnemyRole __1, eEnemyRoleDifficulty __2,
        float __3, bool __4, int __5, MethodBase __originalMethod, bool __runOriginal, out DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Begin(__instance, new object?[] { __0, __1, __2, __3, __4, __5 }, __originalMethod, __runOriginal, out __state);
    private static void AddingAfter(EnemyGroupScoreSpawnData __instance, AgentMode __0, eEnemyRole __1, eEnemyRoleDifficulty __2,
        float __3, bool __4, int __5, bool __runOriginal, DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Return(__instance, new object?[] { __0, __1, __2, __3, __4, __5 }, null, __runOriginal, __state);
    private static void AddingFinally(EnemyGroupScoreSpawnData __instance, AgentMode __0, eEnemyRole __1, eEnemyRoleDifficulty __2,
        float __3, bool __4, ref int __5, bool __runOriginal, Exception? __exception, DevelopmentNativeHooks.Frame? __state)
        => DevelopmentNativeHooks.Finish(__instance, __exception == null ? new object?[] { __0, __1, __2, __3, __4, __5 }
            : new object?[] { __0, __1, __2, __3, __4, 0 }, null, __runOriginal, __exception, __state);
}

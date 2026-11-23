using System;
using System.Reflection;
using Enemies;
using ForgeEngine.GameBindings;

namespace ForgeEnemy.Native;

/// <summary>Publishes this package's reviewed private noise callback without making consumers discover peer types.</summary>
internal static class EnemyNoiseInspectionService
{
    /// <summary>The Harmony owner of ForgeCore's brackets: `EB_Hibernating.OnNoiseDetected` is patched once by ForgeCore
    /// (`GameHooks.HibernatingNoiseHeard`), whose postfix reaches <see cref="EnemyActionHooks.Noise"/> through this package's subscription.</summary>
    private const string CoreOwner = "NAinfini.ForgeCore";
    private static string TypeName(Exception error) => error.GetType().Name;
    private static readonly EnemyNoiseInspectionResult Result = Resolve();

    internal static EnemyNoiseInspectionResult ResolveNoiseTrace() => Result;

    private static EnemyNoiseInspectionResult Resolve()
    {
        try
        {
            var helper = Exact(typeof(EnemyActionHooks), nameof(EnemyActionHooks.Noise),
                typeof(EB_StateBase), typeof(NM_NoiseData).MakeByRefType(), typeof(float));
            var hibernating = Hook(typeof(EB_Hibernating), typeof(ForgeCore.GameHooks).Assembly.GetType("ForgeCore.HibernatingNoiseHeardHook"),
                "After", CoreOwner, typeof(bool));
            var patrolling = Hook(typeof(EB_Patrolling), typeof(EnemyPatrollingNoiseHook), "Postfix", EnemyHost.ModId);
            if (helper == null || hibernating == null || patrolling == null)
                return EnemyNoiseInspectionResult.Unresolved(EnemyNoiseInspectionContracts.InvalidSourceCode);
            return EnemyNoiseInspectionResult.ResolvedSource(new EnemyNoiseTraceSource(helper,
                new[] { hibernating, patrolling }));
        }
        catch (Exception error)
        {
            EnemyLog.Contained(error);
            return EnemyNoiseInspectionResult.Unresolved(
                EnemyNoiseInspectionContracts.InvalidSourceCode + ":" + TypeName(error));
        }
    }

    private static EnemyNoiseHookSource? Hook(Type state, Type? hook, string name, string owner, params Type[] after)
    {
        var target = state.GetMethod("OnNoiseDetected", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            null, new[] { typeof(NM_NoiseData).MakeByRefType(), typeof(float) }, null);
        var postfix = hook == null ? null : Exact(hook, name, [state, typeof(NM_NoiseData).MakeByRefType(), typeof(float), .. after]);
        return target == null || postfix == null ? null
            : new EnemyNoiseHookSource(target, postfix, owner);
    }

    private static MethodInfo? Exact(Type owner, string name, params Type[] parameters)
    {
        var method = owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null, parameters, null);
        return method != null && method.ReturnType == typeof(void) && !method.IsGenericMethod
            && !method.ContainsGenericParameters && Array.TrueForAll(method.GetParameters(), parameter => !parameter.IsOut)
            ? method : null;
    }
}

using Agents;
using Enemies;
using HarmonyLib;

namespace ForgeEnemy.Native;

/// <summary>The wake-rule hooks (`EnemyModule.Wake.cs`, `forge.enemyWake`): the hibernation noise window
/// (`EnemyDetection.DetectOnNoiseDistance_Conditional_AnimatedWindow`, 0x1569270), the damage detection
/// (`EnemyAI.SetDetectedAgent`, 0x155DB70) and the hit-reaction gate (`ES_Hitreact.CanHitreact`, 0x16E2900), each
/// reviewed in evidence/enemy-behavior-hooks.json.</summary>
internal static class EnemyWakeHooks
{
    /// <summary>The noise window's inputs and answer, passed by reference through the closure-free guard.</summary>
    internal struct NoiseCall
    {
        internal EnemyDetection? Detection;
        internal AgentTarget? Target;
        internal float Output;
    }

    internal struct StateCall
    {
        internal EnemyAgent? Enemy;
        internal EnemyBehaviour? Behaviour;
        internal EnemyLocomotion? Locomotion;
        internal EB_States BehaviourState;
        internal ES_StateEnum LocomotionState;
        internal bool Allowed;
    }

    /// <summary>A damage hook's inputs and verdict.</summary>
    internal struct DamageCall
    {
        internal EnemyAI? AI;
        internal EnemyAgent? Enemy;
        internal AgentTargetDetectionType Detection;
        internal ES_HitreactType Hitreact;
        internal Agent? Agent;
        internal EnemyModule.DamageDetectionScope Scope;
        internal bool Allowed;
    }
}

// Every hook returns before touching the game when no loaded profile authors its rule, and none allocates: the guard
// callbacks are static lambdas the compiler caches, and the call data is a struct passed by reference.
[HarmonyPatch(typeof(EnemyDetection), "DetectOnNoiseDistance_Conditional_AnimatedWindow")]
internal static class EnemyWakeNoiseWindow
{
    [HarmonyPostfix]
    private static void Postfix(EnemyDetection __instance, AgentTarget agentTarget, ref float output)
    {
        if (EnemyHost.Offer(EnemyFeature.WakeHearing) is not { } session || !(session.Module.WakeHearingAuthored || session.Module.WakeLockActive)) return;
        var call = new EnemyWakeHooks.NoiseCall { Detection = __instance, Target = agentTarget, Output = output };
        session.Guard(ref call, static (EnemyModule module, ref EnemyWakeHooks.NoiseCall c) => c.Output = module.HeardNoise(c.Detection!, c.Target, c.Output));
        output = call.Output;
    }
}

[HarmonyPatch(typeof(ES_Hitreact), nameof(ES_Hitreact.CanHitreact))]
internal static class EnemyWakeHitreact
{
    [HarmonyPostfix]
    private static void Postfix(ES_Hitreact __instance, ES_HitreactType suggestedType, ref bool __result)
        => Filter(__instance, suggestedType, ref __result);
    internal static void Filter(ES_HitreactBase __instance, ES_HitreactType suggestedType, ref bool __result)
    {
        // Build 20403457 RVA 0x16E2900 is a state/timer predicate; it performs no reaction or propagation.
        // Let that predicate run, then narrow only an accepted non-death reaction.
        if (!__result || EnemyHost.Offer(EnemyFeature.Wake) is not { } session || !(session.Module.WakeDamageAuthored || session.Module.WakeLockActive)) return;
        var call = new EnemyWakeHooks.DamageCall { Enemy = __instance.m_enemyAgent, Hitreact = suggestedType, Allowed = true };
        session.Guard(ref call, static (EnemyModule module, ref EnemyWakeHooks.DamageCall c) => c.Allowed = module.HitreactAsked(c.Enemy, c.Hitreact));
        if (!call.Allowed) __result = false;
    }
}

// A sleeper the wake lock holds is not offered a detection result at all, and the wake-up state refuses to start; the
// behaviour machine's own guard (`EnemyBehaviourStateEntry`) is the third door. No damage receive entry is skipped.
[HarmonyPatch(typeof(EnemyDetection), nameof(EnemyDetection.UpdateHibernationDetection))]
internal static class EnemyWakeHibernationDetection
{
    [HarmonyPrefix]
    private static bool Prefix(EnemyDetection __instance, ref AgentTarget target, ref bool __result)
    {
        if (EnemyHost.Offer(EnemyFeature.Wake) is not { } session || !session.Module.WakeLockActive) return true;
        var call = new EnemyWakeHooks.StateCall { Enemy = __instance.m_ai?.m_enemyAgent, Allowed = true };
        session.Guard(ref call, static (EnemyModule module, ref EnemyWakeHooks.StateCall c) => c.Allowed = !module.IsWakeLockedAsleep(c.Enemy));
        if (call.Allowed) return true;
        target = null!; __result = false; return false;
    }
}

// `ES_HibernateWakeUp.ActivateState` is patched once by ForgeCore (`GameHooks.HibernateWakeActivated`); this answers its skip for a
// wake-locked enemy (`IsWakeLockedAsleep`), which keeps the body from running.
internal static class EnemyWakeActivation
{
    internal static void Subscribe() => ForgeCore.GameHooks.HibernateWakeActivated.Skip(static call => !Prefix(call.State));

    private static bool Prefix(ES_HibernateWakeUp __instance)
    {
        if (EnemyHost.Offer(EnemyFeature.Wake) is not { } session || !session.Module.WakeLockActive) return true;
        var call = new EnemyWakeHooks.StateCall { Enemy = __instance.m_enemyAgent, Allowed = true };
        session.Guard(ref call, static (EnemyModule module, ref EnemyWakeHooks.StateCall c) => c.Allowed = !module.IsWakeLockedAsleep(c.Enemy));
        return call.Allowed;
    }
}

[HarmonyPatch(typeof(ES_HitreactFlyer), nameof(ES_HitreactFlyer.CanHitreact))]
internal static class EnemyWakeHitreactFlyer
{
    [HarmonyPostfix]
    private static void Postfix(ES_HitreactFlyer __instance, ES_HitreactType suggestedType, ref bool __result)
        => EnemyWakeHitreact.Filter(__instance, suggestedType, ref __result);
}

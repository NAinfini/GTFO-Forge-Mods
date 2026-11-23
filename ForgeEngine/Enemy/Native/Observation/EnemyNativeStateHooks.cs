using Agents;
using Enemies;
using HarmonyLib;

namespace ForgeEnemy.Native;

// One callback class per native target for the entries that three functions read (the `StateEntries` function): the
// keep-enemy-still gate (`Wake`), the combat / knockdown state readback (`NativeState`) and the behaviour facts
// (`Behavior`: the wake-up edge and the target lock, sampled when the AI's state or target changes instead of on every
// awake enemy's every frame). Each section asks its own function first and returns before it touches the game when
// that function is withdrawn or has no consumer.

[HarmonyPatch(typeof(EnemyBehaviour), nameof(EnemyBehaviour.ChangeState), new[] { typeof(EB_States) })]
internal static class EnemyBehaviourStateEntry
{
    [HarmonyPrefix]
    private static bool Prefix(EnemyBehaviour __instance, EB_States state, out EnemyNativeStateObservation __state)
    {
        __state = default;
        if (EnemyHost.Offer(EnemyFeature.Wake) is { } wake && wake.Module.WakeLockActive)
        {
            var call = new EnemyWakeHooks.StateCall { Behaviour = __instance, BehaviourState = state, Allowed = true };
            wake.Guard(ref call, static (EnemyModule module, ref EnemyWakeHooks.StateCall c)
                => c.Allowed = module.WakeLockBehaviourAllows(c.Behaviour!, c.BehaviourState));
            if (!call.Allowed) return false;
        }
        if (EnemyHost.Offer(EnemyFeature.NativeState) is { } session && session.Module.CombatStateObserved)
        {
            var begin = (Behaviour: __instance, Observation: default(EnemyNativeStateObservation));
            session.Guard(ref begin, static (EnemyModule module, ref (EnemyBehaviour Behaviour, EnemyNativeStateObservation Observation) c)
                => c.Observation = module.BeforeNativeState(c.Behaviour.m_ai?.m_enemyAgent, c.Behaviour.Pointer, (int)c.Behaviour.m_currentStateName));
            __state = begin.Observation;
        }
        return true;
    }

    [HarmonyPostfix]
    private static void Postfix(EnemyBehaviour __instance, EnemyNativeStateObservation __state, bool __runOriginal)
    {
        if (!__runOriginal) return;
        if (__state.Enemy != null && EnemyHost.Offer(EnemyFeature.NativeState) is { } session)
        {
            var call = new CombatCall { Behaviour = __instance, Observation = __state };
            session.Guard(ref call, static (EnemyModule module, ref CombatCall c) => module.ObserveCombatChange(c.Behaviour, c.Observation));
        }
        EnemyBehaviorSampling.Sample(__instance.m_ai);
    }

    private struct CombatCall { internal EnemyBehaviour Behaviour; internal EnemyNativeStateObservation Observation; }
}

[HarmonyPatch(typeof(EnemyLocomotion), nameof(EnemyLocomotion.ChangeState), new[] { typeof(ES_StateEnum) })]
internal static class EnemyLocomotionStateEntry
{
    [HarmonyPrefix]
    private static bool Prefix(EnemyLocomotion __instance, ES_StateEnum state, out EnemyNativeStateObservation __state)
    {
        __state = default;
        if (EnemyHost.Offer(EnemyFeature.Wake) is { } wake && wake.Module.StationaryActive)
        {
            var call = new EnemyWakeHooks.StateCall { Locomotion = __instance, LocomotionState = state, Allowed = true };
            wake.Guard(ref call, static (EnemyModule module, ref EnemyWakeHooks.StateCall c)
                => c.Allowed = module.StationaryLocomotionAllows(c.Locomotion!, c.LocomotionState));
            if (!call.Allowed) return false;
        }
        __state = Capture(__instance);
        return true;
    }

    [HarmonyPostfix]
    private static void Postfix(EnemyLocomotion __instance, EnemyNativeStateObservation __state, bool __runOriginal)
    {
        if (__runOriginal) Close(__instance, __state);
    }

    internal static EnemyNativeStateObservation Capture(EnemyLocomotion locomotion)
    {
        if (EnemyHost.Offer(EnemyFeature.NativeState) is not { } session || !session.Module.KnockdownStateObserved) return default;
        var begin = (Locomotion: locomotion, Observation: default(EnemyNativeStateObservation));
        session.Guard(ref begin, static (EnemyModule module, ref (EnemyLocomotion Locomotion, EnemyNativeStateObservation Observation) c)
            => c.Observation = module.BeforeNativeState(c.Locomotion.m_agent, c.Locomotion.Pointer, (int)c.Locomotion.CurrentStateEnum));
        return begin.Observation;
    }

    internal static void Close(EnemyLocomotion locomotion, EnemyNativeStateObservation observation)
    {
        if (observation.Enemy == null || EnemyHost.Offer(EnemyFeature.NativeState) is not { } session) return;
        var call = new KnockdownCall { Locomotion = locomotion, Observation = observation };
        session.Guard(ref call, static (EnemyModule module, ref KnockdownCall c) => module.ObserveKnockdownChange(c.Locomotion, c.Observation));
    }

    private struct KnockdownCall { internal EnemyLocomotion Locomotion; internal EnemyNativeStateObservation Observation; }
}

// ForceState uses StateMachine.ChangeState directly, rather than EnemyLocomotion.ChangeState.
[HarmonyPatch(typeof(EnemyLocomotion), nameof(EnemyLocomotion.ForceState))]
internal static class EnemyLocomotionForceEntry
{
    [HarmonyPrefix]
    private static bool Prefix(EnemyLocomotion __instance, Enemies.ES_Base state, out EnemyNativeStateObservation __state)
    {
        __state = default;
        if (state != null && EnemyHost.Offer(EnemyFeature.Wake) is { } wake && wake.Module.StationaryActive)
        {
            var call = new EnemyWakeHooks.StateCall { Locomotion = __instance, LocomotionState = state.m_stateEnum, Allowed = true };
            wake.Guard(ref call, static (EnemyModule module, ref EnemyWakeHooks.StateCall c)
                => c.Allowed = module.StationaryLocomotionAllows(c.Locomotion!, c.LocomotionState));
            if (!call.Allowed) return false;
        }
        __state = EnemyLocomotionStateEntry.Capture(__instance);
        return true;
    }

    [HarmonyPostfix]
    private static void Postfix(EnemyLocomotion __instance, EnemyNativeStateObservation __state, bool __runOriginal)
    {
        if (__runOriginal) EnemyLocomotionStateEntry.Close(__instance, __state);
    }
}

// The damage detection (`EnemyAI.SetDetectedAgent`, 0x155DB70): the keep-asleep / gunfire-wakes window, and the target
// lock the AI holds once the call returns.
[HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.SetDetectedAgent))]
internal static class EnemyDetectedAgentEntry
{
    [HarmonyPrefix]
    private static void Prefix(EnemyAI __instance, Agent agent, AgentTargetDetectionType detectionType,
        out EnemyModule.DamageDetectionScope __state)
    {
        __state = default;
        if (EnemyHost.Offer(EnemyFeature.Wake) is not { } session
            || !(session.Module.WakeDamageAuthored || session.Module.WakeLockActive)) return;
        var call = new EnemyWakeHooks.DamageCall { AI = __instance, Agent = agent, Detection = detectionType };
        session.Guard(ref call, static (EnemyModule module, ref EnemyWakeHooks.DamageCall c)
            => c.Scope = module.BeginDamageDetection(c.AI!, c.Agent, c.Detection));
        __state = call.Scope;
    }

    // The native receiver always runs: target registration, propagation and attack buildup belong to the game.
    // Apply this call's wake decision immediately; later observers/writers see (and may change) its actual result.
    [HarmonyPostfix, HarmonyPriority(Priority.First)]
    private static void Postfix(EnemyAI __instance, EnemyModule.DamageDetectionScope __state)
    {
        if (__state.Owner != null && EnemyHost.Offer(EnemyFeature.Wake) is { } session)
        {
            var call = new EnemyWakeHooks.DamageCall { AI = __instance, Scope = __state };
            session.Guard(ref call, static (EnemyModule module, ref EnemyWakeHooks.DamageCall c)
                => module.EndDamageDetection(c.AI!, c.Scope));
        }
        EnemyBehaviorSampling.Sample(__instance);
    }
}

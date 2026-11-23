using Enemies;
using ForgeEnemy.Profile;
using HarmonyLib;

namespace ForgeEnemy.Native;

/// <summary>
/// The per-ability field hooks (`EnemyModule.AbilityFields.cs`): the scout feeler's layout and tendrils, the scout
/// scream's colours and immortality, and the state entries each bio-tracker colour belongs to. The fog sphere rides
/// the existing `EnemyFogReleasedHook`.
///
/// PERF: every body reads the module's ability flags first and returns before building anything when no loaded type
/// authors that group; the work then goes through the closure-free guard with a `static` lambda and its inputs in a
/// struct passed by reference. Only `ES_ScoutScream.CommonUpdate` runs per frame, and only while a scout screams.
/// Every patched member is named with its dump.cs line of build 20403457; a private member is reached by the name
/// the interop wrapper exposes, and Harmony's native detour covers the game's own direct calls and tail jumps.
/// </summary>
internal static class EnemyAbilityFieldHooks
{
    /// <summary>The inputs of one hook, carried to the guarded module call by reference.</summary>
    internal struct Call
    {
        internal ScoutAntennaDetection? Detection;
        internal ScoutAntenna? Antenna;
        internal ES_ScoutScream? Scream;
        internal EnemyAgent? Enemy;
        internal EnemyScannerState State;
        internal eDetectionState AntennaState;
    }

    internal static bool Wanted(EnemyAbilityHooks flag, out EnemyPluginSession session)
    {
        session = EnemyHost.Offer(EnemyHookFeatures.Of(flag))!;
        return session != null && (session.Module.AbilityHooks & flag) != 0;
    }

    /// <summary>Whether some loaded type authors a scanner colour, read before a scanner hook touches its instance.</summary>
    internal static bool ScannerOn => EnemyHost.Offer(EnemyFeature.ScannerAbilities) is { } session && (session.Module.AbilityHooks & EnemyAbilityHooks.Scanner) != 0;

    /// <summary>One scanner state entry of the life that owns <paramref name="enemy"/>.</summary>
    internal static void Scanner(EnemyAgent? enemy, EnemyScannerState state)
    {
        if (!Wanted(EnemyAbilityHooks.Scanner, out var session)) return;
        var call = new Call { Enemy = enemy, State = state };
        session.Guard(ref call, static (EnemyModule module, ref Call c) => module.ScannerEntered(c.Enemy, c.State));
    }
}

// `ScoutAntennaDetection.Setup(Vector3, int, float)` (dump.cs 659577), called from its `OnSpawn` after the owner is
// read, lays the tendril directions out.
[HarmonyPatch(typeof(ScoutAntennaDetection), nameof(ScoutAntennaDetection.Setup))]
internal static class EnemyFeelerLayoutHook
{
    [HarmonyPostfix]
    private static void Postfix(ScoutAntennaDetection __instance)
    {
        if (!EnemyAbilityFieldHooks.Wanted(EnemyAbilityHooks.FeelerLayout, out var session)) return;
        var call = new EnemyAbilityFieldHooks.Call { Detection = __instance };
        session.Guard(ref call, static (EnemyModule module, ref EnemyAbilityFieldHooks.Call c) => module.FeelerLaidOut(c.Detection));
    }
}

// `ScoutAntenna.Init(int, Vector3, Vector3, ScoutAntennaDetection, float, float, float, float, float)` (659398) builds
// one tendril for its detection.
[HarmonyPatch(typeof(ScoutAntenna), nameof(ScoutAntenna.Init))]
internal static class EnemyFeelerTendrilHook
{
    [HarmonyPostfix]
    private static void Postfix(ScoutAntenna __instance, ScoutAntennaDetection detection)
    {
        if (!EnemyAbilityFieldHooks.Wanted(EnemyAbilityHooks.FeelerTendril, out var session)) return;
        var call = new EnemyAbilityFieldHooks.Call { Antenna = __instance, Detection = detection };
        session.Guard(ref call, static (EnemyModule module, ref EnemyAbilityFieldHooks.Call c) => module.FeelerTendrilBuilt(c.Antenna, c.Detection));
    }
}

// `ES_ScoutScream.CommonUpdate()` (664702, private) reads the two static scream colours while the state runs.
[HarmonyPatch(typeof(ES_ScoutScream), nameof(ES_ScoutScream.CommonUpdate))]
internal static class EnemyScreamUpdateHook
{
    [HarmonyPrefix]
    private static void Prefix(ES_ScoutScream __instance, out EnemyColorWindow<EnemyModule.ScreamColors>.Scope __state)
    {
        __state = default;
        if (!EnemyAbilityFieldHooks.Wanted(EnemyAbilityHooks.ScreamColors, out var session))
        {
            if (EnemyModule.ScreamWindow.Pending) __state = EnemyModule.ScreamWindow.Enter(null);
            return;
        }
        var call = (__instance, Scope: default(EnemyColorWindow<EnemyModule.ScreamColors>.Scope));
        session.Guard(ref call, static (EnemyModule module, ref (ES_ScoutScream State, EnemyColorWindow<EnemyModule.ScreamColors>.Scope Scope) c) => c.Scope = module.ScreamUpdating(c.State));
        __state = call.Scope;
    }

    [HarmonyPostfix]
    private static void Postfix(ES_ScoutScream __instance, EnemyColorWindow<EnemyModule.ScreamColors>.Scope __state)
    {
        __state.Dispose();
        // The state advances through its phases inside this update, so the phase fact is sampled here, for the scouts that scream.
        EnemyBehaviorSampling.SampleScream(__instance.m_enemyAgent, began: false);
    }

    [HarmonyFinalizer]
    private static void Finalizer(EnemyColorWindow<EnemyModule.ScreamColors>.Scope __state) => __state.Dispose();
}

// `ES_ScoutScream.ActivateState(AgentTarget)` (664687) starts the scream on the host and sets `IsImortal`. ForgeCore patches it once
// (`GameHooks.ScoutScreamActivated`); this reads the scream after the body ran.
internal static class EnemyScreamActivateHook
{
    internal static void Subscribe() => ForgeCore.GameHooks.ScoutScreamActivated.After(static (state, _) => EnemyScreamStart.Started(state));
}

// `ES_ScoutScream.RecieveScream(pES_ScoutScreamData)` (664690, private) starts it on a client and sets `IsImortal`.
[HarmonyPatch(typeof(ES_ScoutScream), nameof(ES_ScoutScream.RecieveScream))]
internal static class EnemyScreamReceiveHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_ScoutScream __instance) => EnemyScreamStart.Started(__instance);
}

internal static class EnemyScreamStart
{
    internal static void Started(ES_ScoutScream state)
    {
        EnemyBehaviorSampling.SampleScream(state.m_enemyAgent, began: true);
        if (!EnemyAbilityFieldHooks.Wanted(EnemyAbilityHooks.ScreamMortal, out var session)) return;
        var call = new EnemyAbilityFieldHooks.Call { Scream = state };
        session.Guard(ref call, static (EnemyModule module, ref EnemyAbilityFieldHooks.Call c) => module.ScreamStarted(c.Scream));
    }
}

// `ES_PathMove.CommonEnter()` (537698) and `ES_PathMoveFlyer.CommonEnter()` (537829, private): two of the game's
// three own `set_ScannerColor` callers, both writing the default colour.
[HarmonyPatch(typeof(ES_PathMove), nameof(ES_PathMove.CommonEnter))]
internal static class EnemyScannerPathMoveHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_PathMove __instance) { if (EnemyAbilityFieldHooks.ScannerOn) EnemyAbilityFieldHooks.Scanner(__instance.m_enemyAgent, EnemyScannerState.Default); }
}

[HarmonyPatch(typeof(ES_PathMoveFlyer), nameof(ES_PathMoveFlyer.CommonEnter))]
internal static class EnemyScannerPathMoveFlyerHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_PathMoveFlyer __instance) { if (EnemyAbilityFieldHooks.ScannerOn) EnemyAbilityFieldHooks.Scanner(__instance.m_enemyAgent, EnemyScannerState.Default); }
}

// `ES_Hibernate.CommonEnter()` (664399, private): the sleeper's heartbeat state.
[HarmonyPatch(typeof(ES_Hibernate), nameof(ES_Hibernate.CommonEnter))]
internal static class EnemyScannerHibernateEnterHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_Hibernate __instance) { if (EnemyAbilityFieldHooks.ScannerOn) EnemyAbilityFieldHooks.Scanner(__instance.m_enemyAgent, EnemyScannerState.Heartbeat); }
}

// `ES_Hibernate.CommonExit()` (664447, private): the game's third own `set_ScannerColor` caller, back to default.
[HarmonyPatch(typeof(ES_Hibernate), nameof(ES_Hibernate.CommonExit))]
internal static class EnemyScannerHibernateExitHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_Hibernate __instance) { if (EnemyAbilityFieldHooks.ScannerOn) EnemyAbilityFieldHooks.Scanner(__instance.m_enemyAgent, EnemyScannerState.Default); }
}

// `ES_Hibernate.DoStartDetection()` (664423, private), reached from `MasterStartDetection` on the host and
// `RecieveDetectData` on a client: the sleeper starts sensing a player. ForgeCore patches it once
// (`GameHooks.HibernateDetectionStarted`); this reads the state after the body ran.
internal static class EnemyScannerDetectionStartHook
{
    internal static void Subscribe() => ForgeCore.GameHooks.HibernateDetectionStarted.After(static (state, _) =>
    {
        if (EnemyAbilityFieldHooks.ScannerOn) EnemyAbilityFieldHooks.Scanner(state.m_enemyAgent, EnemyScannerState.Detection);
    });
}

// `ES_Hibernate.DoEndDetection()` (664426, private): the sensing stopped, back to the heartbeat. ForgeCore patches it once
// (`GameHooks.HibernateDetectionEnded`); this reads the state after the body ran.
internal static class EnemyScannerDetectionEndHook
{
    internal static void Subscribe() => ForgeCore.GameHooks.HibernateDetectionEnded.After(static (state, _) =>
    {
        if (EnemyAbilityFieldHooks.ScannerOn) EnemyAbilityFieldHooks.Scanner(state.m_enemyAgent, EnemyScannerState.Heartbeat);
    });
}

// `ES_HibernateWakeUp.Enter()` (664497): the wake-up state.
[HarmonyPatch(typeof(ES_HibernateWakeUp), nameof(ES_HibernateWakeUp.Enter))]
internal static class EnemyScannerWakeupHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_HibernateWakeUp __instance) { if (EnemyAbilityFieldHooks.ScannerOn) EnemyAbilityFieldHooks.Scanner(__instance.m_enemyAgent, EnemyScannerState.Wakeup); }
}

// `ES_ScoutDetection.CommonEnter()` (537941, private), reached from `Enter` and `SyncEnter`: the scout patrols.
// ForgeCore patches it once (`GameHooks.ScoutDetectionEntered`); this reads the state after the body ran.
internal static class EnemyScannerScoutHook
{
    internal static void Subscribe() => ForgeCore.GameHooks.ScoutDetectionEntered.After(static (state, _) =>
    {
        if (EnemyAbilityFieldHooks.ScannerOn) EnemyAbilityFieldHooks.Scanner(state.m_enemyAgent, EnemyScannerState.ScoutPatrol);
    });
}

// `ScoutAntennaDetection.SetState(eDetectionState, bool)` (659592): the tendrils go out or come home.
[HarmonyPatch(typeof(ScoutAntennaDetection), nameof(ScoutAntennaDetection.SetState))]
internal static class EnemyScannerAntennaHook
{
    [HarmonyPostfix]
    private static void Postfix(ScoutAntennaDetection __instance, eDetectionState state)
    {
        if (!EnemyAbilityFieldHooks.Wanted(EnemyAbilityHooks.Scanner, out var session)) return;
        var call = new EnemyAbilityFieldHooks.Call { Detection = __instance, AntennaState = state };
        session.Guard(ref call, static (EnemyModule module, ref EnemyAbilityFieldHooks.Call c) => module.ScannerAntennaState(c.Detection, c.AntennaState));
    }
}

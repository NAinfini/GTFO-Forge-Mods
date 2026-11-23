using System;
using Enemies;
using HarmonyLib;

namespace ForgeEnemy.Native;

// The native members that bound a survival wave in build 20403457, verified entry points in
// `ForgeMap/evidence/encounter-wave-hooks.json` and `h10-wave-bodies-20403457.json` (RVA in parentheses):
//
//   * `SurvivalWave.OnSpawn` (0x13F4810) — the replicator spawn callback; the one point where a wave becomes
//     live. Patched before the body for the observation token and after it for the fact, because the wave's own
//     EventID is what its spawn body registers with the Mastermind.
//   * `SurvivalWave.SpawnGroup` (0x13F4FB0) — the private per-group spawn step. Its call is the batch boundary
//     the game itself does not have: groups registered inside its prefix/finalizer scope belong to that batch,
//     whose publication waits for all original group request loops to complete.
//   * `SurvivalWave.RegisterGroup` (0x13F4C30) — the wave producing one group: the delivered side.
//   * `EnemyGroup.RegisterMember` (0x1575930), `SurvivalSpawning` (0x1576610) — actual member-list
//     registration and normal-host request-loop completion, from the decoded synchronous replication chain.
//   * `SurvivalWave.MastermindUpdate` (0x13F4360), `StopEvent` (0x13F5BE0) — the inline native
//     exhaustion branch is observed before StopEvent invokes its despawn callback; body evidence is in
//     `ForgeMap/evidence/h10-wave-bodies-20403457.json`.
//   * `SurvivalWave.TryEndEvent` (0x13F5E90) is not patched: it has no caller in this build (MastermindUpdate inlines its
//     total > 0 && spent >= total test and calls StopEvent itself), and its own body ends in the same StopEvent.
//   * `SurvivalWave.OnDespawn` (0x13F4780) — the teardown callback, which fires for level cleanup and
//     cancellation too, so it only retires bookkeeping and publishes nothing.
//   * `Mastermind.GameStateUpdate` (0x14F70B0) — the per-tick update that carries `m_activeGroups`
//     maintenance. `MaintainGroups` (0x14F75B0) has no caller: the compiler inlined its body into
//     GameStateUpdate (same m_activeGroups loop and allocator reset, master-only), so a patch on MaintainGroups
//     never runs. GameStateUpdate itself has a real call site (0x181434E9B), which a patch does intercept. The
//     group list after it is the native answer to whether a wave still has anything alive.
//
// Every hook only reads native state and publishes facts; none of them writes the world. A wave hook whose
// bindings no plan subscribes to returns before reading any game state.

// One patch class for the target, with two halves that run in this order: the wave observation (Waves), then the child
// wave half (ChildSpawning, `EnemyChildWaveCalls.Created`). ChildSpawning requires Waves in the feature table, so a
// refusal of this class withdraws both.
[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.OnSpawn))]
internal static class EnemyWaveSpawn
{
    [HarmonyPrefix]
    private static void Prefix(SurvivalWave __instance, out EnemyWaveFacts.WaveSpawnObservation? __state)
    {
        __state = null;
        if (EnemyHost.Offer(EnemyFeature.Waves) is not { } session || !session.Module.WavesSubscribed) return;
        var call = new WaveHookCall<EnemyWaveFacts.WaveSpawnObservation> { Wave = __instance };
        session.Guard(ref call, static (EnemyModule module, ref WaveHookCall<EnemyWaveFacts.WaveSpawnObservation> c) => c.Result = module.BeforeWaveSpawn(c.Wave));
        __state = call.Result;
    }

    [HarmonyPostfix]
    private static void Postfix(SurvivalWave __instance, EnemyWaveFacts.WaveSpawnObservation? __state)
    {
        if (__state != null && EnemyHost.Offer(EnemyFeature.Waves) is { } session)
        {
            var call = new WaveHookCall<EnemyWaveFacts.WaveSpawnObservation> { Wave = __instance, Result = __state };
            session.Guard(ref call, static (EnemyModule module, ref WaveHookCall<EnemyWaveFacts.WaveSpawnObservation> c) => module.AfterWaveSpawn(c.Wave, c.Result));
        }
        EnemyChildWaveCalls.Created(__instance);
    }
}
/// <summary>The wave hooks' call data, passed to the closure-free guard by reference: the native wave, and the
/// observation a prefix produces or a postfix hands back.</summary>
internal struct WaveHookCall<TResult> where TResult : class
{
    internal SurvivalWave Wave;
    internal TResult? Result;
    internal bool Flag;
}

// The private per-batch spawn step. Prefix/finalizer bound the original call; successful batches remain
// pending until each group's request loop completes. Outside registrations cannot join this batch.
[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.SpawnGroup))]
internal static class EnemyWaveGroupStep
{
    [HarmonyPrefix]
    private static void Prefix(SurvivalWave __instance, out EnemyWaveFacts.WaveGroupObservation? __state)
    {
        __state = null;
        if (EnemyHost.Offer(EnemyFeature.Waves) is not { } session || !session.Module.WavesSubscribed) return;
        var call = new WaveHookCall<EnemyWaveFacts.WaveGroupObservation> { Wave = __instance };
        session.Guard(ref call, static (EnemyModule module, ref WaveHookCall<EnemyWaveFacts.WaveGroupObservation> c) => c.Result = module.BeforeWaveGroupStep(c.Wave));
        __state = call.Result;
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(SurvivalWave __instance, Exception? __exception, EnemyWaveFacts.WaveGroupObservation? __state)
    {
        if (__state != null && EnemyHost.Offer(EnemyFeature.Waves) is { } session)
        {
            var call = new WaveHookCall<EnemyWaveFacts.WaveGroupObservation> { Wave = __instance, Result = __state, Flag = __exception == null };
            session.Guard(ref call, static (EnemyModule module, ref WaveHookCall<EnemyWaveFacts.WaveGroupObservation> c) => module.AfterWaveGroupStep(c.Wave, c.Result, c.Flag));
        }
        return __exception;
    }
}

// A produced group joins the wave's live set and, inside a batch, that batch's delivered set.
[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.RegisterGroup))]
internal static class EnemyWaveGroup
{
    [HarmonyPostfix]
    private static void Postfix(SurvivalWave __instance, EnemyGroup group)
    {
        if (EnemyHost.Offer(EnemyFeature.Waves) is not { } session || !session.Module.WavesSubscribed) return;
        var call = (Wave: __instance, Group: group);
        session.Guard(ref call, static (EnemyModule module, ref (SurvivalWave Wave, EnemyGroup Group) c) => module.AfterWaveGroup(c.Wave, c.Group));
    }
}

// Successful normal-host members are registered synchronously inside each original SurvivalSpawning call.
// The final true result closes the request loop; actual delivery still comes only from the native member list.
[HarmonyPatch(typeof(EnemyGroup), nameof(EnemyGroup.RegisterMember))]
internal static class EnemyWaveMember
{
    [HarmonyPostfix]
    private static void Postfix(EnemyGroup __instance, EnemyAgent enemyAgent)
    {
        if (EnemyHost.Offer(EnemyFeature.Waves) is not { } session || !session.Module.WavesSubscribed) return;
        var call = (Group: __instance, Member: enemyAgent);
        session.Guard(ref call, static (EnemyModule module, ref (EnemyGroup Group, EnemyAgent Member) c) => module.AfterGroupMember(c.Group, c.Member));
    }
}

// One patch class for the target, with two halves that run in this order: the wave observation (Waves), then the child
// wave group scope (ChildSpawning, `EnemyChildWaveCalls`), whose prefix may skip the original body. ChildSpawning requires
// Waves in the feature table, so a refusal of this class withdraws both.
[HarmonyPatch(typeof(EnemyGroup), nameof(EnemyGroup.SurvivalSpawning))]
internal static class EnemyWaveGroupSpawning
{
    [HarmonyPrefix]
    private static bool Prefix(EnemyGroup __instance, ref bool __result, out State __state)
    {
        __state = default;
        if (EnemyHost.Offer(EnemyFeature.Waves) is { } session && session.Module.WavesSubscribed)
        {
            var call = new GroupCall { Group = __instance };
            session.Guard(ref call, static (EnemyModule module, ref GroupCall c) => c.Result = module.BeforeGroupSpawning(c.Group));
            __state.Wave = call.Result;
        }
        return EnemyChildWaveCalls.BeforeGroup(__instance, ref __result, out __state.Child);
    }

    [HarmonyPostfix]
    private static void Postfix(EnemyGroup __instance, ref bool __result, State __state)
    {
        if (__state.Wave != null && EnemyHost.Offer(EnemyFeature.Waves) is { } session)
        {
            var call = new GroupCall { Group = __instance, Result = __state.Wave, Completed = __result };
            session.Guard(ref call, static (EnemyModule module, ref GroupCall c) => module.AfterGroupSpawning(c.Group, c.Result, c.Completed));
        }
        EnemyChildWaveCalls.AfterGroup(__state.Child, ref __result);
    }

    [HarmonyFinalizer]
    private static void Finalizer(State __state) => __state.Child?.Dispose();

    internal struct State
    {
        internal EnemyWaveFacts.GroupSpawningObservation? Wave;
        internal EnemyModule.ChildGroupScope? Child;
    }

    private struct GroupCall
    {
        internal EnemyWaveFacts.GroupSpawningObservation? Result;
        internal EnemyGroup Group;
        internal bool Completed;
    }
}
// Scope the original calls; these void prefixes never suppress their original bodies. The finalizer removes
// the scope on both success and exception and returns the original exception unchanged.
[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.MastermindUpdate))]
internal static class EnemyWaveUpdate
{
    [HarmonyPrefix]
    private static void Prefix(SurvivalWave __instance, out EnemyWaveFacts.WaveEndObservation? __state)
        => __state = EnemyWaveEndScope.Open(__instance);

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, EnemyWaveFacts.WaveEndObservation? __state)
    {
        EnemyWaveEndScope.Close(__state);
        return __exception;
    }
}

/// <summary>The end-check scope the wave update hook opens and closes. It opens only while a plan subscribes and a wave is
/// tracked, which is all `BeforeWaveEndCheck` ever answers a token for; a wave that ticks every frame builds nothing
/// otherwise.</summary>
internal static class EnemyWaveEndScope
{
    internal static EnemyWaveFacts.WaveEndObservation? Open(SurvivalWave wave)
    {
        if (EnemyHost.Offer(EnemyFeature.Waves) is not { } session || !session.Module.WaveEndWatched) return null;
        var call = new WaveHookCall<EnemyWaveFacts.WaveEndObservation> { Wave = wave };
        session.Guard(ref call, static (EnemyModule module, ref WaveHookCall<EnemyWaveFacts.WaveEndObservation> c) => c.Result = module.BeforeWaveEndCheck(c.Wave));
        return call.Result;
    }

    internal static void Close(EnemyWaveFacts.WaveEndObservation? observation)
    {
        if (observation == null || EnemyHost.Offer(EnemyFeature.Waves) is not { } session) return;
        var call = new WaveHookCall<EnemyWaveFacts.WaveEndObservation> { Result = observation };
        session.Guard(ref call, static (EnemyModule module, ref WaveHookCall<EnemyWaveFacts.WaveEndObservation> c) => module.AfterWaveEndCheck(c.Result));
    }
}

// `SurvivalWave.StopEvent` is patched once by ForgeCore (`GameHooks.WaveStopped`): the observation opens before the body and
// closes once, as completed when the body returned and as not completed when it threw.
internal static class EnemyWaveStop
{
    internal static void Subscribe() => ForgeCore.GameHooks.WaveStopped.Around(
        static (SurvivalWave wave) => Open(wave),
        static (_, observation, _) => Close(observation, true),
        static (_, observation, _) => Close(observation, false));

    private static EnemyWaveFacts.WaveStopObservation? Open(SurvivalWave wave)
    {
        if (EnemyHost.Offer(EnemyFeature.Waves) is not { } session || !session.Module.WaveEndWatched) return null;
        var call = new WaveHookCall<EnemyWaveFacts.WaveStopObservation> { Wave = wave };
        session.Guard(ref call, static (EnemyModule module, ref WaveHookCall<EnemyWaveFacts.WaveStopObservation> c) => c.Result = module.BeforeWaveStop(c.Wave));
        return call.Result;
    }

    private static void Close(EnemyWaveFacts.WaveStopObservation? observation, bool completed)
    {
        if (observation == null || EnemyHost.Offer(EnemyFeature.Waves) is not { } session) return;
        var call = new WaveHookCall<EnemyWaveFacts.WaveStopObservation> { Result = observation, Flag = completed };
        session.Guard(ref call, static (EnemyModule module, ref WaveHookCall<EnemyWaveFacts.WaveStopObservation> c) => module.AfterWaveStop(c.Result, c.Flag));
    }
}

// `SurvivalWave.OnDespawn` is a replicator teardown callback: level cleanup, event cancellation and a spent wave
// all reach it, so it retires this provider's wave state and publishes no fact.
[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.OnDespawn))]
internal static class EnemyWaveDespawn
{
    [HarmonyPrefix]
    private static void Prefix(SurvivalWave __instance)
    {
        if (EnemyHost.Offer(EnemyFeature.Waves) is not { } session || !session.Module.WaveEndWatched) return;
        var call = new WaveHookCall<object> { Wave = __instance };
        session.Guard(ref call, static (EnemyModule module, ref WaveHookCall<object> c) => module.BeforeWaveDespawn(c.Wave));
    }
}

[HarmonyPatch(typeof(Mastermind), nameof(Mastermind.GameStateUpdate))]
internal static class EnemyGroupMaintenance
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (EnemyHost.Offer(EnemyFeature.Waves) is { } session && session.Module.WaveEndWatched)
            session.Guard(static module => module.AfterGroupMaintenance());
    }
}

using System.Collections.Generic;
using HarmonyLib;
using LevelGeneration;
using Player;
using ForgeEngine.Framework;
using ForgeCore;

namespace ForgeMap.Native;

internal sealed partial class MapPluginSession
{
    private RuntimeSubscriptionGate[]? _lifecycleFactGates;
    private long _lifecycleFactGeneration = -1;
    private bool _lifecycleFactsObserved;

    internal bool NativeLifecycleFactsObserved
    {
        get
        {
            if (MapObjects is not { } maps) return false;
            var generation = _kernel.SubscriptionGeneration;
            if (_lifecycleFactGeneration == generation) return _lifecycleFactsObserved;
            _lifecycleFactGates ??= new List<RuntimeSubscriptionGate>(maps.Registration.SubscriptionGates().Values).ToArray();
            _lifecycleFactsObserved = false;
            foreach (var gate in _lifecycleFactGates)
                if (gate.HasSubscribers) { _lifecycleFactsObserved = true; break; }
            _lifecycleFactGeneration = generation;
            return _lifecycleFactsObserved;
        }
    }
}

/// <summary>Every Harmony patch class the level-event rows install. Each one is a postfix on the game's own
/// entry for the transition the row is about, so the fact is published after the native body decided it rather
/// than from a guess about what the body was going to do. The classes are listed in `MapNativeHooks.Types`, which
/// is the list the plugin installs, so a hook is installed exactly when it is listed there and nowhere else.
///
/// Only the master publishes: every one of these transitions is a decision the host's world made — the objective
/// machine, the recall and the portal all run on the master, and a client's copy of the same reading would be a
/// second publisher of a session fact the kernel answers per host. The zone row reads a local agent's own field,
/// so it is the only one whose report a client makes; it publishes through the same host guard because the
/// kernel's replay ledger is per host world and a client-side fact for the same zone would double the row.
///
/// Evidence for every target below is in `evidence/level-event-hooks.json`, with the member signatures, the
/// call edges and the build they were read from.</summary>

[HarmonyPatch(typeof(WardenObjectiveManager), nameof(WardenObjectiveManager.OnLocalPlayerStartExpedition))]
internal static class ExpeditionStartedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(bool __runOriginal)
    {
        if (!__runOriginal) return;
        MapHost.Session?.GlobalLight?.StartLevel();
        MapHost.Session?.SpawnRules?.ReportCounts();
        if (!Session.IsHost) return;
        var session = MapHost.Session;
        if (session is not { NativeLifecycleFactsObserved: true }) return;
        // A plan this row starts may read who stands in a trigger zone, and the members read refuses a level the
        // host has not judged yet. The clock's first beat comes after that plan runs, so the host judges once here.
        if (session.TriggerZones is { Pending: true } zones)
            session.Run(zones, static half => half.Tick(), "trigger-zone judgment");
        // The level's own identity is already read for the `level` attachment matcher; the row publishes the same
        // string, so a plan mounted on a level and a plan reading this row agree about which level they are in.
        session.Run(session.LevelEvents, module => module.ExpeditionStarted(session.LevelReference()),
            "level-event observation", wake: true);
    }
}

// The reactor's own replicated state change: `pReactorState.stateCount` is the wave counter the reactor advances
// through its `Increase_stateCount` interaction, so a change that raises it is a wave boundary. A drop-in state is
// a late joiner catching up with a count the world already had, not a transition, and is skipped.
// `WardenObjective.OnStartInChain` is not a usable entry: its body is the empty stub 2102 dump entries share.
// Subscribed to `ForgeCore.GameHooks.ReactorState` (see `MapGameHooks`); ForgeCore patches the game method.
internal static class ReactorWaveReadback
{
    internal static void Changed(LG_WardenObjective_Reactor reactor, pReactorState oldState, pReactorState newState,
        bool isDropinState, bool ranOriginal)
    {
        if (!ranOriginal) return;
        if (!Session.IsHost || isDropinState || newState.stateCount <= oldState.stateCount
            || MapHost.Session is not { NativeLifecycleFactsObserved: true }) return;
        string layer = LevelEventObservation.LayerName(reactor.OriginLayer);
        int wave = newState.stateCount, previous = oldState.stateCount;
        if (MapHost.Session is { } session)
            session.Run(session.LevelEvents, module => module.ReactorWaveAdvanced(layer, wave, previous),
                "level-event observation", wake: true);
    }
}

// The HSU's own transition: the objective item has been taken out of the activator. The class is the objective
// component the vanilla `ActivateHSU_Events` list belongs to, so the row fires exactly where the mount point does.
[HarmonyPatch(typeof(WO_HSUFindTakeSample), nameof(WO_HSUFindTakeSample.OnLocalPlayerSolvedObjectiveItem))]
internal static class HsuSampledReadback
{
    // Harmony 2.10.1 only provides this postfix's __runOriginal when this patch owns a prefix.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(WO_HSUFindTakeSample __instance) { }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(WO_HSUFindTakeSample __instance, bool __runOriginal)
    {
        if (!__runOriginal) return;
        if (!Session.IsHost || MapHost.Session is not { NativeLifecycleFactsObserved: true }) return;
        string layer = LevelEventObservation.LayerName(__instance.Layer);
        var hsu = __instance.m_hsu;
        string? container = hsu == null
            ? null
            : LevelEventObservation.ItemReference(hsu.m_serialNumber, hsu.m_itemKey);
        if (MapHost.Session is { } session)
            session.Run(session.LevelEvents, module => module.HsuSampled(layer, container),
                "level-event observation", wake: true);
    }
}

// The objective machine's own replicated state change: the callback carries the state before and after, so the
// objective trigger row's facts are the difference between the two readings and not a memory this package keeps.
// A recall restores checkpoint state rather than advancing objective progress, so it publishes nothing here.
// `WardenObjectiveManager.CheckWardenObjectiveStatus` and `OnObjectiveChainUpdated` are reached from this
// one body, so a single hook covers every status change, chain step and item change the machine applies.
[HarmonyPatch(typeof(WardenObjectiveManager), nameof(WardenObjectiveManager.OnStateChange))]
internal static class ObjectiveChangedReadback
{
    // Harmony 2.10.1 only provides this postfix's __runOriginal when this patch owns a prefix.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(WardenObjectiveManager __instance) { }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(WardenObjectiveManager __instance, pWardenObjectiveState oldState,
        pWardenObjectiveState newState, bool isRecall, bool __runOriginal)
    {
        if (!__runOriginal) return;
        if (!Session.IsHost || isRecall || MapHost.Session is not { NativeLifecycleFactsObserved: true }) return;
        if (MapHost.Session is { } session)
            session.Run(session.LevelEvents,
                module => ObjectiveTriggerObservation.Changed(__instance, oldState, newState, module),
                "level-event observation", wake: true);
    }
}

// The recall's own completion. `isRecall` is the game's own flag on the objective callback, and this is the entry
// the game calls once the reloaded world is in place, so the row fires after the state is back and not when the
// recall was requested.
[HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.OnRecallComplete))]
internal static class CheckpointRestoredReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(bool __runOriginal)
    {
        if (!__runOriginal) return;
        MapHost.Session?.ObjectiveRounds?.Recalled();
        if (!Session.IsHost || MapHost.Session is not { NativeLifecycleFactsObserved: true }) return;
        if (MapHost.Session is { } session)
            session.Run(session.LevelEvents, module => module.CheckpointRestored(),
                "level-event observation", wake: true);
    }
}

// The native course-node setter belongs to the actual player, including peer agents on the host. Reading its
// old/new Zone avoids the local-player-only Warden callback and emits nothing for movement within one Zone.
[HarmonyPatch(typeof(PlayerAgent), "set_CourseNode")]
internal static class ZoneEnteredReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(PlayerAgent __instance, out LG_Zone? __state)
        => __state = Session.IsHost && MapHost.Session is { NativeLifecycleFactsObserved: true }
            ? __instance.CourseNode?.m_zone : null;

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerAgent __instance, LG_Zone? __state, bool __runOriginal)
    {
        if (!__runOriginal) return;
        MapHost.Session?.GlobalLight?.LocationChanged(__instance);
        if (!Session.IsHost || MapHost.Session is not { NativeLifecycleFactsObserved: true }) return;
        if (LevelEventObservation.ZoneAfterChange(__instance, __state) is not { } zone) return;
        if (MapHost.Session is not { } session) return;
        if (!session.TryOptionalPlayerService(static (s, value) => s.ReferenceOf(value), __instance,
                "zone-entered-player", out EntityReference? reference) || reference == null) return;
        session.Run(session.LevelEvents, module => module.ZoneEntered(reference, zone),
            "level-event observation", wake: true);
    }
}

// Observe the actual execution of the portal's Setup-created Type 8 event, including its native delay.
// IWarpableObject.OnWarp is broadcast to unrelated portals too and cannot establish the source.
// On every machine, before the body reads it, a team warp's zone landing object is made (ZoneLandingObjects).
[HarmonyPatch(typeof(WorldEventManager._DoExcecuteEvent_d__41), nameof(WorldEventManager._DoExcecuteEvent_d__41.MoveNext))]
internal static class PortalWarpedReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(WorldEventManager._DoExcecuteEvent_d__41 __instance,
        out PortalWarpObservation.Reading? __state)
    {
        ZoneLandingObjects.Ensure(__instance.eData);
        __state = Session.IsHost && MapHost.Session is { NativeLifecycleFactsObserved: true }
            ? PortalWarpObservation.Capture(__instance.eData) : null;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(WorldEventManager._DoExcecuteEvent_d__41 __instance,
        PortalWarpObservation.Reading? __state, bool __runOriginal)
    {
        if (!__runOriginal) return;
        if (!Session.IsHost || MapHost.Session is not { NativeLifecycleFactsObserved: true }
            || PortalWarpObservation.Completed(__instance.eData, __state) is not { } warp) return;
        if (MapHost.Session is { } session)
            session.Run(session.LevelEvents, module => module.PortalWarped(warp.PortalId, warp.To, warp.From),
                "level-event observation", wake: true);
    }
}

using System;
using HarmonyLib;
using LevelGeneration;
using SNetwork;
using ForgeCore;

namespace ForgeMap.Native;

// The native readback points for map objects. Each hook is a postfix on the method the game itself runs when
// the object's state changed, and each one only chooses when to read: the callback's own arguments are never
// turned into fact values, because the instance is re-read afterwards and reports the state it is really in.
//
// The three entry points are frozen in `evidence/door-terminal-hooks.json`: `LG_SecurityDoor_Locks.OnDoorState`
// is the door's own lock/state callback and the door scan's transition point as well, `LG_ComputerTerminal.OnStateChange`
// is the terminal's own state replication callback. Terminal command receipt and password results are observed
// on the interpreter itself (DoorTerminalHooks), after native validation and before command execution.
// The generator category's two readbacks are declared with the generator reader in `GeneratorSource.cs`. Each
// class below is listed in `MapNativeHooks.Types`, which is the list the plugin installs.

/// <summary>The scan stage one door status is, or null for a status that is not a scan transition. The lock
/// component announces a chained-puzzle activation and its solution through two `Action` members that this
/// build's interop exposes as delegate properties with no method behind either, so no Harmony patch can reach
/// them; the same component's real `OnDoorState` callback carries the transitions instead.
/// `ChainedPuzzleActivated` is written while the scan runs and `Unlocked` when the door's own lock released,
/// which for a locked-with-chained-puzzle door is the solution. Every other status is a state the door holds,
/// which is why this answers null rather than the nearest stage.</summary>
internal static class DoorScanStatus
{
    internal static DoorScanStage? Stage(int status) => status switch
    {
        8 => DoorScanStage.Activated,
        9 => DoorScanStage.Solved,
        _ => null
    };
}

[HarmonyPatch(typeof(LG_SecurityDoor_Locks), nameof(LG_SecurityDoor_Locks.OnDoorState))]
internal static class DoorStateReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_SecurityDoor_Locks __instance, pDoorState state)
    {
        // SetupDoor calls the door's initial state callback before BuildGate assigns
        // LG_Gate.SpawnedDoor (RVA 0x1274990 -> 0x12749a8). It cannot have an entrance
        // address yet. Observe gameplay transitions only after the native build boundary.
        // The authored door interaction policy is replayed on every machine after the game's own body has set the
        // local active flags; the fact observation below stays host-only.
        if (MapHost.Session is { } policy) policy.Run(policy.MapState, state => state.DoorStateChanged(__instance), "map-state door replay");
        if (!Session.IsHost || !LG_Factory.BuildDone) return;
        var door = DoorObservation.Door(__instance);
        if (door == null) return;
        var instigator = Instigator(state);
        if (MapHost.Session is { } session)
        {
            session.Run(session.MapObjects, module => module.DoorStateChanged(door, instigator), "map-object observation");
            DoorObservation.WithCallbackStatus(door, state.status,
                () => session.Run(session.MapObjects, module => module.DoorLockChanged(door, instigator), "map-object observation"));
        }
        // The scan transitions ride the same callback: the door's own status is what the lock component wrote,
        // and a status that is not one of the two scan transitions publishes no scan fact at all.
        if (DoorScanStatus.Stage((int)state.status) is not { } stage) return;
        DoorTerminalFacts.Current?.Guard(facts => facts.ScanTransition(__instance, stage, LevelObjectObservation.ActorOf(state.instigator)));
    }

    internal static SNet_Player? Instigator(pDoorState state)
    {
        try { return state.instigator.TryGetPlayer(out var player) ? player : null; }
        catch (Exception containedError) { MapLog.Contained("map-object-hooks.instigator", containedError); return null; }
    }
}

[HarmonyPatch(typeof(LG_ComputerTerminal), nameof(LG_ComputerTerminal.OnStateChange))]
internal static class TerminalStateReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_ComputerTerminal __instance)
    {
        // The terminal's initial state callback runs inside the Markers batch, while the level is still being built
        // and before any zone lists the terminal among its own placements, so its address cannot be read yet (QA
        // runs of every level that has a terminal reported that refusal at Error level). Only transitions after the
        // native build boundary are observed, the same boundary DoorStateReadback keeps.
        if (!Session.IsHost || !LG_Factory.BuildDone) return;
        // A reactor owns its console; it is not a Zone terminal placement. Only a reciprocal native ownership
        // excludes it from this observer. Unlisted ordinary terminals still report their missing address.
        if (IsReactorConsole(__instance)) return;
        if (MapHost.Session is { } session)
            session.Run(session.MapObjects,
                module => module.TerminalStateChanged(__instance, Actor(__instance)), "map-object observation");
    }

    /// <summary>The player at the terminal, read from the terminal's own interaction source. The state
    /// callback does not carry the acting player, so a terminal with no current interaction reports the actor
    /// port absent instead of the last player that touched it.</summary>
    private static object? Actor(LG_ComputerTerminal terminal)
        => terminal.m_hasInteractingPlayer ? terminal.m_syncedInteractionSource : null;

    internal static bool IsReactorConsole(LG_ComputerTerminal terminal)
        => terminal.ConnectedReactor is { WasCollected: false } reactor
            && reactor.m_terminal is { WasCollected: false } console && console.Pointer == terminal.Pointer;
}

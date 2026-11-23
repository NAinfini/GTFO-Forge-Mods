using GameEvent;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgePlayer.Native;

/// <summary>Observes the shared native event funnel only after its original body returned normally. A posted
/// request, a skipped body and a throwing listener publish no player fact here. Ping position is read only for
/// a subscribed row; the native marker callback below is its second entry. A client's own player travels through
/// <see cref="PlayerInputReport"/> at the hook seam in <see cref="PlayerEventHooks"/>.</summary>
internal static class PlayerGameEventPosted
{
    internal static DomainWorldObservation Subscribe(RuntimeModuleHandle owner, IForgeEngine kernel)
        => new("Player", owner, kernel, null, static epoch => GamePostEvents.Subscribe("forge.player.events", epoch,
            static observation => Returned(observation.Event, observation.Player), GamePostEventPhase.Returned));

    private static void Returned(eGameEvent e, PlayerAgent? player)
    {
        // The six named events are tested before the wrapper is, so every other posted event costs one switch.
        if (e is not (eGameEvent.player_low_health or eGameEvent.player_apply_medikit or eGameEvent.player_apply_ammokit
            or eGameEvent.player_apply_disinfection or eGameEvent.player_apply_toolRefill or eGameEvent.player_ping)) return;
        if (player == null) return;
        Publish(e, player);
    }

    /// <summary>One event's own transition, run on the session that owns the registration: the game-event
    /// funnel carries all of these rows, so one guard per event is what keeps a failed session from publishing
    /// through a registration that never carried the bindings. An event this half does not name publishes
    /// nothing.</summary>
    private static void Publish(eGameEvent e, PlayerAgent player)
    {
        // Every branch passes its player (and, for a ping, the position read here) as state rather than
        // capturing it, so this prefix — which the game's own event funnel reaches for every posted event,
        // subscribed or not — allocates no closure to publish through.
        switch (e)
        {
            case eGameEvent.player_low_health:
                PlayerHost.Session?.GuardState(static (state, p) => PlayerEventHooks.LowHealth(state, p), player);
                break;
            case eGameEvent.player_apply_medikit:
                PlayerHost.Session?.GuardEvents(static (events, p) => PlayerEventHooks.SupplyUsed(events, p, "medikit"), player);
                break;
            case eGameEvent.player_apply_ammokit:
                PlayerHost.Session?.GuardEvents(static (events, p) => PlayerEventHooks.SupplyUsed(events, p, "ammokit"), player);
                break;
            case eGameEvent.player_apply_disinfection:
                PlayerHost.Session?.GuardEvents(static (events, p) => PlayerEventHooks.SupplyUsed(events, p, "disinfection"), player);
                break;
            case eGameEvent.player_apply_toolRefill:
                PlayerHost.Session?.GuardEvents(static (events, p) => PlayerEventHooks.SupplyUsed(events, p, "tool_refill"), player);
                break;
            case eGameEvent.player_ping:
                if (PlayerEventFacts.Installed is not { PingObserved: true }) return;
                if (player.TryCast<LocalPlayerAgent>() is { } local)
                {
                    var state = (player, position: PlayerNativeValues.Coordinates(local.m_pingPos));
                    PlayerHost.Session?.GuardEvents(static (events, s) => PlayerEventHooks.Ping(events, s.player, s.position), state);
                }
                break;
        }
    }
}

/// <summary>The marker call the game makes for a ping, with the pinging agent and the world position. Only the
/// visible edge is a ping; the same body is called to hide a marker again.</summary>
[HarmonyPatch(typeof(GuiManager), nameof(GuiManager.AttemptSetPlayerPingStatus),
    new[] { typeof(PlayerAgent), typeof(bool), typeof(Vector3), typeof(eNavMarkerStyle) })]
internal static class PlayerPingMarkerSet
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerAgent sourceAgent, bool visible, Vector3 worldPos, bool __runOriginal)
    {
        if (!__runOriginal || !visible || sourceAgent == null || PlayerEventFacts.Installed is not { PingObserved: true }) return;
        if (PlayerNativeValues.Coordinates(worldPos) is not { } position) return;
        var state = (sourceAgent, position);
        PlayerHost.Session?.GuardEvents(static (events, s) => PlayerEventHooks.Ping(events, s.sourceAgent, s.position), state);
    }
}

/// <summary>The player's own locomotion machine changing state. The patched entry is
/// `PlayerLocomotion.ChangeState`, the one member of that machine that moves it from one `PLOC_State` to the
/// next (`catalog/native/native-runtime.json`, type `PlayerLocomotion`, method `ChangeState`, field
/// `m_currentStateEnum`).
///
/// Neither member of the published pair is taken from the call's argument. The prefix records the member the
/// machine is in before the body runs and the postfix reads the member it is in afterwards, so the fact reports
/// what the machine did and not what it was asked to do: a call the body declined publishes nothing, because the
/// two readings are equal. The agent is the machine's own owner, which is the player the posture belongs to.
///
/// The publication itself is host-only, exactly as the other rows of this family are: a client reports its own
/// locomotion through <see cref="PlayerInputReport"/>, and the host publishes it for that client's player.</summary>
[HarmonyPatch(typeof(PlayerLocomotion), nameof(PlayerLocomotion.ChangeState))]
internal static class PlayerLocomotionStateChanged
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(PlayerLocomotion __instance, out int __state)
        => __state = __instance == null || PlayerEventFacts.Installed is not { MovementObserved: true }
            ? -1 : (int)__instance.m_currentStateEnum;

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerLocomotion __instance, int __state, bool __runOriginal)
    {
        if (!__runOriginal || __instance == null || __state < 0) return;
        int current = (int)__instance.m_currentStateEnum;
        if (current == __state) return;
        var owner = __instance.m_owner;
        if (owner == null) return;
        var state = (owner, current, previous: __state);
        PlayerHost.Session?.GuardEvents(static (events, s) => PlayerEventHooks.MovementStateChanged(events, s.owner, s.current, s.previous), state);
    }
}

/// <summary>A timed interaction a player held to completion. The patched entry is the base
/// `Interact_Timed.TriggerInteractionAction`, the member the component runs once when the local hold reaches the end
/// (it is what raises the component's own triggered callback). `OnInteractorCompleted` is not that member: a recorded
/// session that completed plain timed points, pickups and callback interactions never called it once, while the base
/// `TriggerInteractionAction` ran for every one of them, the overrides of the pickup and callback interactions included.
/// An override that does not call the base is not reached; that is the gap this entry leaves.
///
/// The actor is the member's own `source` argument - the interactor the component names - and not a field
/// read back off the component afterwards, so the fact names the player the game named. A call that hands no
/// player publishes nothing rather than naming one this hook picked.
///
/// `elapsed` is `m_interactDuration`, the interaction's own hold time in seconds: a hold that reached this member
/// lasted exactly that long. It is converted to ticks once, in the observation half.
///
/// It is a postfix because the fact is "the hold completed": a body that threw before doing anything completed
/// nothing.</summary>
[HarmonyPatch(typeof(Interact_Timed), "TriggerInteractionAction")]
internal static class TimedInteractionCompleted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Interact_Timed __instance, PlayerAgent source, bool __runOriginal)
    {
        if (!__runOriginal || __instance == null || source == null || PlayerEventFacts.Installed is not { HoldObserved: true }) return;
        var state = (sourceAgent: source, seconds: (double)__instance.m_interactDuration);
        PlayerHost.Session?.GuardEvents(static (events, s) => PlayerEventHooks.InputHeld(events, s.sourceAgent, s.seconds), state);
    }
}

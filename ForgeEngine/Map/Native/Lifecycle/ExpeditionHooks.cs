using ForgeCore;

namespace ForgeMap.Native;

// The native readback point for the end of an expedition. The game itself reports the end through this one call
// and names which of its three ends it was (`ExpeditionEndState`), so the hook is a postfix that only chooses
// when to publish: the argument names the outcome, and nothing is inferred from a game state, a player count or
// a level teardown order.
//
// ForgeCore patches the entry once (`GameHooks.ExpeditionEnded`, RVA 0x13E2100); this class is its subscriber.
//
// Only the host publishes: the outcome is one fact about one expedition, and a client's own copy of it would be
// a second publisher of a session fact the kernel answers per host.
internal static class ExpeditionEndedReadback
{
    internal static void Ended(ExpeditionEndState endState, bool ran)
    {
        if (!ran) return;
        if (!Session.IsHost || MapHost.Session is not { NativeLifecycleFactsObserved: true }) return;
        if (MapHost.Session is { } session)
            session.Run(session.MapObjects, module => module.Expeditions.Ended((int)endState),
                "map-object observation");
    }
}

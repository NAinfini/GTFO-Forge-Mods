using HarmonyLib;
using ForgeCore;

namespace ForgeMap.Native;

// The game-bound half of the three level-world trigger rows (`MapWorldTriggerContract`): the native
// entries they are read from, each a postfix that only chooses when the publisher is asked.
//
// - `InfectionSpitter.DoExplode(float speedMulti)` (dump.cs 559339, RVA 0x1311CB0): the spitter's own burst body,
//   reached from its explode packet (`SendExplode`, dump.cs 559327). The position is the spitter's own transform.
// - `EnvironmentStateManager.AttemptSetExpeditionLightModeInZone(GlobalZoneIndex zone, bool mode,
//   pLightSequenceData)` (dump.cs 581603, RVA 0x1645D30): the one static every zone light switch goes through — the
//   level's own events and this package's `forge.action.map.power_state` alike. The zone and the mode are the entry's
//   own arguments.
// - `EnvironmentStateManager.AttemptStartFogTransition(uint fogDataId, float transitionDuration, eDimensionIndex)`
//   (dump.cs 581612, RVA 0x1645F30): the one static a fog change goes through. The fog preset and the length are the
//   entry's own arguments.
//
// **Gate first, then authority.** Each postfix reads the publisher's cached subscription gate before anything else
// and returns at once when no plan listens, so an unsubscribed row costs one static read and one field read and
// allocates nothing. Only then is the peer asked whether it is the master: the rows are host-published.
//
// The arguments are copied into locals before the guarded callback is built, so the callback holds values and never
// a native argument across the guard.

[HarmonyPatch(typeof(InfectionSpitter), nameof(InfectionSpitter.DoExplode))]
internal static class SpitterExplodedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(InfectionSpitter __instance)
    {
        if (MapWorldFacts.Current is not { SpitterObserved: true }) return;
        if (!Session.IsHost || __instance == null || __instance.WasCollected) return;
        var transform = __instance.transform;
        if (transform == null) return;
        var position = transform.position;
        double x = position.x, y = position.y, z = position.z;
        if (MapHost.Session is { } session)
            session.Run(session.World, facts => facts.SpitterExploded(x, y, z), "level-world observation");
    }
}

[HarmonyPatch(typeof(EnvironmentStateManager), nameof(EnvironmentStateManager.AttemptSetExpeditionLightModeInZone))]
internal static class ZoneLightsChangedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(GlobalZoneIndex zone, bool mode)
    {
        if (MapWorldFacts.Current is not { ZoneLightsObserved: true }) return;
        if (!Session.IsHost) return;
        int dimension = (int)zone.Dimension, layer = (int)zone.Layer, local = (int)zone.Zone;
        if (MapHost.Session is { } session)
            session.Run(session.World, facts => facts.ZoneLightsChanged(dimension, layer, local, mode),
                "level-world observation");
    }
}

[HarmonyPatch(typeof(EnvironmentStateManager), nameof(EnvironmentStateManager.AttemptStartFogTransition))]
internal static class FogTransitionStartedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(uint fogDataId, float transitionDuration)
    {
        if (MapWorldFacts.Current is not { FogObserved: true }) return;
        if (!Session.IsHost) return;
        long fog = fogDataId;
        double seconds = transitionDuration;
        if (MapHost.Session is { } session)
            session.Run(session.World, facts => facts.FogChanged(fog, seconds), "level-world observation");
    }
}

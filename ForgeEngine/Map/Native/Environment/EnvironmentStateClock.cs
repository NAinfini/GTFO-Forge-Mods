using HarmonyLib;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>Retimes a replicated fog transition to the clock of the machine that receives it.
///
/// <c>EnvironmentStateManager.AttemptStartFogTransition</c> stamps the <c>FogState.StartTime</c> field from
/// <c>Clock.ExpeditionProgressionTime</c> of the machine that calls it (dump.cs: `Clock.ExpeditionProgressionTime`,
/// offset 0x48; the same static <c>FogState.GetFogBlend</c> reads, RVA 0x1422EC0). The state replicator copies that
/// absolute time to every peer (dump.cs 442787, <c>SNet_StateReplicator&lt;pEnvironmentState,
/// pEnvironmentInteraction&gt;.OnStateChangeReceive</c>, RVA 0x1CD18E0) and the receiver's
/// <c>EnvironmentStateManager.UpdateFogSettingsForState</c> (RVA 0x1647E40) turns it into a blend. A peer whose
/// expedition clock is behind the sender's therefore computes a negative blend and keeps showing the previous fog
/// until its own clock reaches the sender's start time instead of starting the transition now.
///
/// This prefix runs before that application on a non-host peer. A start still in the peer's future is moved back to
/// its own expedition clock, so the transition starts on receipt and lasts the replicated duration; the host and a
/// peer already past the start keep the state they were sent. It writes no replicated state and changes no fog id or
/// duration: the game's own state remains the one source of truth.</summary>
[HarmonyPatch(typeof(EnvironmentStateManager), "UpdateFogSettingsForState")]
internal static class EnvironmentStateClock
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(pEnvironmentState state)
    {
        if (Session.IsHost) return;
        var fog = state.FogStates;
        if (fog == null) return;
        float now = Clock.ExpeditionProgressionTime;
        for (int index = 0; index < fog.Length; index++)
        {
            var entry = fog[index];
            if (entry.StartTime <= now) continue;
            entry.StartTime = now;
            fog[index] = entry;
        }
    }
}

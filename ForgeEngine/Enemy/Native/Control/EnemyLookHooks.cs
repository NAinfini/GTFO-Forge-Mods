using Enemies;
using HarmonyLib;

namespace ForgeEnemy.Native;

// The look family's hooks: the tag marker being placed, and the hibernation wake-up whose propagated colour a type may author. Every hook reads its own instance and hands the owner to the
// module; none of them patches an update method, and the pulses and marker blinks need no hook at all (they run on
// the kernel's own tick).

// `ES_HibernateWakeUp.DoWakeup(bool, int, float, bool isPropagatedWakeup)` (dump.cs 664518, private void) starts a
// hibernating enemy's wake-up on every peer; on the propagated path it hands the static `m_propagatedWakeupColor`
// (664488) to the glow within the call. ForgeCore patches it once (`GameHooks.HibernateWakeupStarted`): before the body
// this swaps that static for the owner's authored colour, and after it (returned or thrown) puts the game's back, so the
// swap covers exactly this one enemy's wake-up.
internal static class EnemyWakeupGlow
{
    internal static void Subscribe() => ForgeCore.GameHooks.HibernateWakeupStarted.Around(Before,
        static (_, scope, _) => scope.Dispose(), static (_, scope, _) => scope.Dispose());

    private static EnemyColorWindow<UnityEngine.Vector4>.Scope Before((ES_HibernateWakeUp State, bool Turn, int Index, float Delay, bool Propagated) wakeup)
    {
        if (EnemyHost.Offer(EnemyFeature.Look) is not { } session || !session.Module.WakeupLookConfigured)
            return EnemyModule.WakeupWindow.Pending ? EnemyModule.WakeupWindow.Enter(null) : default;
        var call = (wakeup.State, wakeup.Propagated, Scope: default(EnemyColorWindow<UnityEngine.Vector4>.Scope));
        session.Guard(ref call, static (EnemyModule module, ref (ES_HibernateWakeUp State, bool Propagated, EnemyColorWindow<UnityEngine.Vector4>.Scope Scope) c) =>
            c.Scope = module.WakeupStarting(c.State, c.Propagated));
        return call.Scope;
    }
}

// `EnemyAgent.SyncPlaceNavMarkerTag` (dump.cs 662718) places the tag marker (`m_tagMarker`, 662421) the marker's icon,
// colour, size and blinks are written to.
[HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.SyncPlaceNavMarkerTag))]
internal static class EnemyTagMarkerPlaced
{
    [HarmonyPostfix]
    private static void Postfix(EnemyAgent __instance)
    {
        if (EnemyHost.Offer(EnemyFeature.Look) is not { } session || !session.Module.MarkerConfigured) return;
        var enemy = __instance;
        session.Guard(ref enemy, static (EnemyModule module, ref EnemyAgent instance) => module.MarkerPlaced(instance));
    }
}

internal sealed partial class EnemyModule
{
    // Pending swaps must still restore the game's static colour, including nested unconfigured wakeups.
    internal bool WakeupLookConfigured => _propagateLooks || WakeupWindow.Pending;
}

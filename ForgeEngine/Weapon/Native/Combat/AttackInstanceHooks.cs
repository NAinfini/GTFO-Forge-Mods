using ForgeEngine.Framework;
using Gear;
using HarmonyLib;

namespace ForgeWeapon.Native;

/// <summary>The attack-instance hook set: the game's own empty-clip paths and the two ends of a burst sequence.
/// Each hook chooses when to read; a burst start first confirms the archetype's native Firing state.
///
/// The empty-clip fact comes from the game's own empty-clip paths rather than from a clip readback: `BWA_Auto`
/// declares its own `OnFireShotEmptyClip`, and `BWA_Burst` and `BWA_Semi` share one folded body at RVA 0x145B540
/// (`BWA_SemiBurst` inherits it). The burst sequence ends are `BWA_Burst` and `BWA_Auto`.
///
/// Every body below is inert unless <see cref="WeaponNativeSession.Offer"/> returns a live session for its feature, which is what
/// keeps a hook from running over a half-built one.</summary>

// The game's own empty-clip paths. The fact is published from the game's decision and never from comparing a
// clip readback against zero, which is a value that can be replenished between any two observations.
// ForgeCore patches both bodies once (`GameHooks.AutoDryFired`, `GameHooks.SemiDryFired`); these read after every other patch of them.
internal static class AttackAutoFiredEmptyClip
{
    internal static void Subscribe() => ForgeCore.GameHooks.AutoDryFired.After(Fired);

    private static void Fired(BWA_Auto instance, bool ran)
    {
        WeaponNativeSession.Current?.Log.Skipped(WeaponBurstContract.DryFireBinding, "empty-clip-callback-entered", "auto");
        WeaponNativeSession.Offer(WeaponFeature.Automatic)?.Guard(static (session, archetype) =>
        {
            if (!session.ClientFacts.DryFire(archetype.m_weapon)) session.Attack.DryFire(archetype.m_weapon);
        }, instance);
    }
}

// `BWA_Burst.OnFireShotEmptyClip` and `BWA_Semi.OnFireShotEmptyClip` are one folded native body at RVA 0x145B540
// (dump: BWA_Burst line 617359, BWA_Semi line 617380). Runtime declares one target per native entry, so the hook is
// declared once on the BWA_Semi method (ForgeCore's `SemiDryFired`), and the weapon is attributed by the instance's
// actual type. `BWA_SemiBurst` derives from `BWA_Semi` and runs the same body. Whether the body reaches this hook for
// `BWA_Burst` instances is a runtime fact that the static evidence cannot prove.
internal static class AttackSemiBurstFiredEmptyClip
{
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.SemiDryFired.Before(Entered);
        ForgeCore.GameHooks.SemiDryFired.After(Fired);
    }

    // Diagnostic only: ForgeCore's prefix of the shared body ran for this instance, whatever the session or the feature says after.
    // Written at Trace (once per class and instance address), so it shows whether the patch is reached at all.
    private static void Entered(BWA_Semi instance)
    {
        // The native cast and the text are made only when the trace gate is open.
        if (WeaponNativeSession.Current?.Log is not { } log || !log.Logs(RuntimeLogLevel.Trace)) return;
        log.Skipped(WeaponBurstContract.DryFireBinding, "empty-clip-prefix-entered",
            ArchetypeName(instance) + " 0x" + instance.Pointer.ToString("x"));
    }

    private static string ArchetypeName(BWA_Semi instance)
        => instance.TryCast<BWA_Burst>() != null ? "burst" : instance.TryCast<BWA_Semi>() != null ? "semi" : "other";

    private static void Fired(BWA_Semi instance, bool ran)
    {
        // The native type behind the shared body: the bounded counter keys on it, so which archetype reached the entry is on record.
        if (WeaponNativeSession.Current?.Log is { } log && log.Logs(RuntimeLogLevel.Trace))
            log.Skipped(WeaponBurstContract.DryFireBinding, "empty-clip-callback-entered", ArchetypeName(instance));
        WeaponNativeSession.Offer(WeaponFeature.Automatic)?.Guard(static (session, archetype) =>
        {
            // The shared native body may be wrapped as its declared base type. Managed `is` cannot
            // inspect the IL2CPP class behind that wrapper.
            if (archetype.TryCast<BWA_Burst>() != null || archetype.TryCast<BWA_Semi>() != null)
            {
                if (!session.ClientFacts.DryFire(archetype.m_weapon)) session.Attack.DryFire(archetype.m_weapon);
            }
            else session.Log.Skipped(WeaponBurstContract.DryFireBinding, "native-archetype-unmatched");
        }, instance);
    }
}

// Both ends of the burst sequence. `count` is the weapon's own `m_burstMax`, read from the weapon the archetype
// was set up on; the archetype is the instance the hook already holds.
[HarmonyPatch(typeof(Gear.BWA_Burst), nameof(BWA_Burst.OnStartFiring))]
internal static class AttackBurstStarted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BWA_Burst __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Automatic)?.Guard(static (session, archetype) =>
        {
            if (archetype.Firing && !session.ClientFacts.Burst(archetype.m_weapon, true)) session.Attack.Burst(archetype.m_weapon, started: true);
        }, __instance);
}

[HarmonyPatch(typeof(Gear.BWA_Burst), nameof(BWA_Burst.OnStopFiring))]
internal static class AttackBurstEnded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BWA_Burst __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Automatic)?.Guard(static (session, archetype) => { if (!session.ClientFacts.Burst(archetype.m_weapon, false)) session.Attack.Burst(archetype.m_weapon, started: false); }, __instance);
}

[HarmonyPatch(typeof(Gear.BWA_Auto), nameof(BWA_Auto.OnStartFiring))]
internal static class AttackAutoStarted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BWA_Auto __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Automatic)?.Guard(static (session, archetype) =>
        {
            if (archetype.Firing && !session.ClientFacts.Burst(archetype.m_weapon, true)) session.Attack.Burst(archetype.m_weapon, started: true);
        }, __instance);
}

[HarmonyPatch(typeof(Gear.BWA_Auto), nameof(BWA_Auto.OnStopFiring))]
internal static class AttackAutoEnded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BWA_Auto __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Automatic)?.Guard(static (session, archetype) => { if (!session.ClientFacts.Burst(archetype.m_weapon, false)) session.Attack.Burst(archetype.m_weapon, started: false); }, __instance);
}

using Enemies;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The enemy's death, subscribed to the game's `EnemyAgent.OnDead` that ForgeCore patches once for every package: the
/// observation opened before the body travels to the close after it.</summary>
internal static class EnemyDeathStarted
{
    internal static void Subscribe() => ForgeCore.GameHooks.EnemyDead.Around(Before, After);

    internal static EnemyModule.LifecycleObservation? Before(EnemyAgent enemy)
    {
        // Before the game turns the body into a ragdoll: the hit parts' non-convex colliders must be off by then.
        if (EnemyModelHitParts.HasParts && EnemyHost.Stands(EnemyFeature.ModelLimbs))
        {
            try { EnemyModelHitParts.Died(enemy); }
            catch (System.Exception error) { NativeFault.Note("enemy.limb.part-died", error); }
        }
        if (EnemyHost.Offer(EnemyFeature.Death) is not { } session) return null;
        (EnemyAgent Enemy, EnemyModule.LifecycleObservation? Observation) call = (enemy, null);
        session.Guard(ref call, static (EnemyModule module, ref (EnemyAgent Enemy, EnemyModule.LifecycleObservation? Observation) c) =>
            c.Observation = module.BeforeDeath(c.Enemy));
        return call.Observation;
    }

    internal static void After(EnemyAgent enemy, EnemyModule.LifecycleObservation? observation, bool ran)
    {
        if (EnemyHost.Offer(EnemyFeature.Death) is not { } session) return;
        (EnemyAgent Enemy, EnemyModule.LifecycleObservation? Observation) call = (enemy, observation);
        session.Guard(ref call, static (EnemyModule module, ref (EnemyAgent Enemy, EnemyModule.LifecycleObservation? Observation) c) =>
        {
            if (c.Observation != null) module.AfterDeath(c.Enemy, c.Observation);
            // What lives only while the enemy is alive (modifier contributions, action states) ends with the death.
            module.ReleaseDeadEnemy(c.Enemy);
        });
    }
}

/// <summary>The limb break, subscribed to the game's `Dam_EnemyDamageLimb.DestroyLimb` that ForgeCore patches once for every package.</summary>
internal static class EnemyLimbBroken
{
    internal static void Subscribe() => ForgeCore.GameHooks.EnemyLimbDestroyed.Around(
        limb => { Prefix(limb, out var state); return state; }, (limb, state, _) => Postfix(limb, state));

    private static void Prefix(Dam_EnemyDamageLimb __instance, out EnemyModule.LifecycleObservation? __state)
    {
        __state = null;
        if (EnemyHost.Offer(EnemyFeature.LimbBreak) is not { } session) return;
        (Dam_EnemyDamageLimb Limb, EnemyModule.LifecycleObservation? Observation) call = (__instance, null);
        session.Guard(ref call, static (EnemyModule module, ref (Dam_EnemyDamageLimb Limb, EnemyModule.LifecycleObservation? Observation) c) =>
            c.Observation = module.BeforeLimbBreak(c.Limb));
        __state = call.Observation;
    }
    private static void Postfix(Dam_EnemyDamageLimb __instance, EnemyModule.LifecycleObservation? __state)
    {
        if (EnemyModelHitParts.HasParts && EnemyHost.Stands(EnemyFeature.ModelLimbs))
        {
            try { EnemyModelHitParts.Broke(__instance); }
            catch (System.Exception error) { NativeFault.Note("enemy.limb.part-broke", error); }
        }
        if (__state == null || EnemyHost.Offer(EnemyFeature.LimbBreak) is not { } session) return;
        (Dam_EnemyDamageLimb Limb, EnemyModule.LifecycleObservation? Observation) call = (__instance, __state);
        session.Guard(ref call, static (EnemyModule module, ref (Dam_EnemyDamageLimb Limb, EnemyModule.LifecycleObservation? Observation) c) =>
            module.AfterLimbBreak(c.Limb, c.Observation));
    }
}

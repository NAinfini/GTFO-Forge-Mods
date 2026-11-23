using System;
using Agents;
using Enemies;
using HarmonyLib;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The native members behind `forge.trigger.enemy.projectile_hit_world` and `forge.trigger.enemy.projectile_expired`
/// (`EnemyActionTriggerContract`, module half `EnemyModule.ProjectileWorldFacts.cs`). The game's projectile never records
/// who fired it, so the shooter is attributed by call: `EAB_ProjectileShooter.FireAtAgent` and `FireChaos` are the only
/// shooter members that call `ProjectileManager.WantToFireTargeting` (build 20403457), each spawning one projectile whose
/// `ProjectileTargeting.OnFire` runs inside that call, so a projectile fired inside it belongs to that shooter.
///
/// No hook here runs per frame: the shooter frame opens once per shot, and a projectile's end is read once, from
/// `ProjectileTargeting.OnDestroy`, the member every projectile life ends through.
///
/// PERF: every body reads its cached subscription gate first and returns before it builds anything; the work goes through
/// the closure-free guard with a `static` lambda and a struct passed by reference. Nothing here writes the world.</summary>
internal static class EnemyProjectileWorldHooks
{
    /// <summary>The inputs of one projectile hook, carried to the guarded module call by reference.</summary>
    internal struct Call
    {
        internal EnemyAgent? Shooter;
        internal ProjectileBase? Base;
        internal ProjectileTargeting? Projectile;
        internal RaycastHit Hit;
    }

    /// <summary>Opens the shooter frame for one firing call.</summary>
    internal static void EnterShooterFrame(EAB_ProjectileShooter shooter)
    {
        if (EnemyHost.Offer(EnemyFeature.ProjectileWorld) is not { } session || !session.Module.ProjectileWorldObserved) return;
        var call = new Call { Shooter = shooter.m_owner };
        session.Guard(ref call, static (EnemyModule module, ref Call c) => module.EnterShooterFrame(c.Shooter));
    }

    /// <summary>Always closes the frame, so a throw inside the native call cannot leave a later projectile attributed
    /// to this shooter.</summary>
    internal static Exception? LeaveShooterFrame(Exception? exception)
    {
        if (EnemyHost.Offer(EnemyFeature.ProjectileWorld) is { } session) session.Module.LeaveShooterFrame();
        return exception;
    }
}

/// <summary>`EAB_ProjectileShooter.FireAtAgent(Agent)` (RVA 0x16DAB40): one aimed shot. One patch class for the target, in
/// call order: the prefix opens the shooter frame (ProjectileWorld), the postfix publishes the projectile-fired fact
/// (Projectile), the finalizer closes the frame. Projectile requires ProjectileWorld in the feature table, so a refusal of
/// this class withdraws both.</summary>
[HarmonyPatch(typeof(EAB_ProjectileShooter), "FireAtAgent")]
internal static class EnemyProjectileAimedShotFrameHook
{
    [HarmonyPrefix]
    private static void Prefix(EAB_ProjectileShooter __instance) => EnemyProjectileWorldHooks.EnterShooterFrame(__instance);

    [HarmonyPostfix]
    private static void Postfix(EAB_ProjectileShooter __instance, Agent agent)
    {
        if (EnemyHost.Offer(EnemyFeature.Projectile) is not { } session || !session.Module.ProjectileFiredObserved) return;
        var call = new EnemyActionHooks.Call { Enemy = __instance.m_owner, Target = agent };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) => module.AfterProjectileFired(c.Enemy, c.Target));
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception) => EnemyProjectileWorldHooks.LeaveShooterFrame(__exception);
}

/// <summary>`EAB_ProjectileShooter.FireChaos()` (RVA 0x16DB320): one unaimed shot.</summary>
[HarmonyPatch(typeof(EAB_ProjectileShooter), "FireChaos")]
internal static class EnemyProjectileChaosShotFrameHook
{
    [HarmonyPrefix]
    private static void Prefix(EAB_ProjectileShooter __instance) => EnemyProjectileWorldHooks.EnterShooterFrame(__instance);

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception) => EnemyProjectileWorldHooks.LeaveShooterFrame(__exception);
}

/// <summary>`ProjectileTargeting.OnFire(Agent)`: the spawned projectile instance, which `DoFireTargeting` hands to this
/// member once per shot. It is attributed to the shooter frame that is open, if any.</summary>
[HarmonyPatch(typeof(ProjectileTargeting), nameof(ProjectileTargeting.OnFire))]
internal static class EnemyProjectileOwnerHook
{
    [HarmonyPostfix]
    private static void Postfix(ProjectileTargeting __instance)
    {
        if (EnemyHost.Offer(EnemyFeature.ProjectileWorld) is not { } session || !session.Module.ProjectileWorldObserved) return;
        var call = new EnemyProjectileWorldHooks.Call { Projectile = __instance };
        session.Guard(ref call, static (EnemyModule module, ref EnemyProjectileWorldHooks.Call c) => module.AttributeProjectile(c.Projectile!));
    }
}

/// <summary>`ProjectileBase.Collision(Ray, RaycastHit)`: the one entry every projectile hit goes through. The module
/// publishes only a `ProjectileTargeting` that struck a collider with no `IDamageable`.</summary>
[HarmonyPatch(typeof(ProjectileBase), nameof(ProjectileBase.Collision))]
internal static class EnemyProjectileWorldHitHook
{
    [HarmonyPostfix]
    private static void Postfix(ProjectileBase __instance, RaycastHit hit)
    {
        if (EnemyHost.Offer(EnemyFeature.ProjectileWorld) is not { } session || !session.Module.ProjectileHitWorldObserved) return;
        var call = new EnemyProjectileWorldHooks.Call { Base = __instance, Hit = hit };
        session.Guard(ref call, static (EnemyModule module, ref EnemyProjectileWorldHooks.Call c) => module.AfterProjectileCollision(c.Base!, c.Hit));
    }
}

/// <summary>`ProjectileTargeting.OnDestroy()` (RVA 0x13CECA0): runs once when a projectile's life ends. The module
/// publishes the expiry when the life ran out, and forgets the projectile's shooter either way.</summary>
[HarmonyPatch(typeof(ProjectileTargeting), nameof(ProjectileTargeting.OnDestroy))]
internal static class EnemyProjectileEndHook
{
    [HarmonyPrefix]
    private static void Prefix(ProjectileTargeting __instance)
    {
        if (EnemyHost.Offer(EnemyFeature.ProjectileWorld) is not { } session || !session.Module.ProjectileEndObserved) return;
        var call = new EnemyProjectileWorldHooks.Call { Projectile = __instance };
        session.Guard(ref call, static (EnemyModule module, ref EnemyProjectileWorldHooks.Call c) => module.ProjectileEnded(c.Projectile!));
    }
}

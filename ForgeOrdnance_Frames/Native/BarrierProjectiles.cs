using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>A raised barrier stops the enemy projectiles that come into its dome from outside (shooters, big shooters: every aimed
/// projectile in the game's own list, <c>ProjectileManager.m_projectiles</c>). A projectile's hit carries no attacker
/// (<c>ShooterProjectileDamage(dam, position)</c>), so it cannot be judged by the attacker's place the way a melee hit is
/// (<see cref="PilotDamage"/>); the same rule (<see cref="FrameRules.Shelters"/>: the target inside the dome, the attack coming from
/// outside it) is asked of the projectile's own flight instead. Each frame, on every peer while a barrier stands, a live projectile
/// that was outside a raised dome the last frame and is inside it now came from outside: its life is ended through its own clock
/// (<c>m_endLifeTime</c>; <c>ProjectileTargeting.Update</c> destroys it the next frame, the game's own end), so it reaches no one
/// inside. Every peer flies its own copy of each projectile and ends it at the same crossing, so the peer that would apply its hit
/// (the hit player's own) never does. The host charges the barrier with the projectile's damage (<c>m_maxDamage</c>) and tells the
/// pilot when the projectile was aimed at them. A projectile fired inside the dome never crosses into it and is not stopped.</summary>
internal static class BarrierProjectiles
{
    // Where each live projectile was the last frame, and this frame (swapped every frame, so a projectile that ended drops out).
    private static Dictionary<IntPtr, Vector3> _last = new(), _now = new();

    internal static void Tick()
    {
        _now.Clear();
        try
        {
            if (AnyBarrier() && ProjectileManager.Current?.m_projectiles is { } list)
                for (var i = 0; i < list.Count; i++)
                {
                    var projectile = list[i];
                    if (projectile == null || !projectile.m_active) continue;
                    var at = projectile.m_myPos;
                    if (_last.TryGetValue(projectile.Pointer, out var was) && FrameWorld.ShelterFor(at, was) is { } unit)
                    {
                        projectile.m_endLifeTime = float.MinValue;
                        if (ForgeCore.Session.IsHost) Charge(unit, projectile, was);
                        continue;
                    }
                    _now[projectile.Pointer] = at;
                }
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("frames-barrier-projectiles-failed", "exosuit barrier projectile check failed: " + error); }
        (_last, _now) = (_now, _last);
    }

    private static bool AnyBarrier()
    {
        var units = FrameWorld.All;
        for (var i = 0; i < units.Count; i++)
            if (FrameRules.BarrierUp(units[i].Record)) return true;
        return false;
    }

    private static void Charge(FrameUnit unit, ProjectileTargeting projectile, Vector3 from)
    {
        var pilot = FrameWorld.DomePilot(unit);
        var target = projectile.m_targetAgent;
        var onPilot = pilot != null && target != null && target.Pointer == pilot.Pointer;
        FrameWorld.HostShield(unit, projectile.m_maxDamage, from, onPilot);
    }

    internal static void Clear()
    {
        _last.Clear();
        _now.Clear();
    }
}

using System;
using Agents;
using Gear;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>The hooks that sit on the game's busiest paths and matter only while a suit is worn. They are put on the first time the
/// local player puts a suit on (<see cref="LocalPilot"/>) and stay: under Il2CppInterop a removed patch leaves the forwarding detour
/// and the generated method behind, so taking them off would save no call and leak one method each time. Each hook returns at once
/// while no suit is worn. The suit's walk speed, jump, stamina and flinch are <see cref="ForgeCore.PlayerStats"/> sources set here; the reload
/// time is a ForgeCore handling lease (<see cref="ForgeCore.Gear.LeaseReload"/>).</summary>
internal static class PilotLazyHooks
{
    internal static readonly ForgeCore.LazyPatches Hooks = ForgeCore.Patches.Lazy(Plugin.Id, harmony =>
    {
        ForgeCore.PlayerStats.Walk(static (owner, velocity) => LocalPilot.Unit == null ? velocity : LocalPilot.Velocity(owner, velocity));
        ForgeCore.PlayerStats.CarryUnslowed(static () => LocalPilot.Unit != null && LocalPilot.CarriesHeavy);
        ForgeCore.PlayerStats.Jump(static owner => LocalPilot.Unit == null ? 1f : LocalPilot.JumpSpeed(owner));
        ForgeCore.PlayerStats.NoStamina(static owner => LocalPilot.NoStamina(owner));
        ForgeCore.PlayerStats.NoFlinch(static () => LocalPilot.Unit != null);
        harmony.Patch(AccessTools.Method(typeof(PlayerInventoryLocal), nameof(PlayerInventoryLocal.CheckAndWield)),
            prefix: new HarmonyMethod(typeof(PilotGunSwap), nameof(PilotGunSwap.Prefix)));
        ForgeCore.Gear.LeaseReload(Plugin.Id, PilotReload.Factor);
        ForgeCore.GameHooks.WieldedSlotBlocked.Release(PilotCarry.Releases);
        ForgeCore.GameHooks.LocalItemHeld.Hide(PilotCarry.OnBack);
    });
}

/// <summary>The game puts a third-person body's hands on its item's grips with FinalIK limbs in <c>PlayerSyncIK.UpdateIK</c>, run from
/// a game <c>LateUpdate</c> whose order against Forge's own is not fixed. A worn suit that already copied its pilot's body this frame
/// (<see cref="FrameVisual.LateTick"/>) copies it again once that body's hands are solved, so the suit's hands land on the grips. ForgeCore
/// patches the method once (<see cref="ForgeCore.GameHooks.SyncIkSolved"/>); this subscribes the first time a suit takes a pilot and returns at once while none is worn.</summary>
internal static class PoseAfterIK
{
    private static bool _subscribed;

    internal static void Ensure()
    {
        if (_subscribed) return;
        _subscribed = true;
        ForgeCore.GameHooks.SyncIkSolved.After(static (ik, _) =>
        {
            try { FrameWorld.HandsSolved(ik.Pointer); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("frames-pose-after-ik-failed", "exosuit pose after the hands' IK failed: " + error); }
        });
    }
}

/// <summary>A raised barrier (tank model) takes every hit an attacker outside its dome lands on a player inside it, the pilot
/// included; the attacker's position decides, because vanilla melee and projectiles damage what they reach without a line-of-sight
/// test against colliders (see DEVELOPERS.md). A projectile's hit names no attacker, so a projectile from outside is stopped in
/// flight at the dome instead (<see cref="BarrierProjectiles"/>). Every hit on a player funnels through <c>Dam_PlayerDamageBase.OnIncomingDamage</c> (its receive entries call it on
/// whichever peer receives the damage packet; it then sends the new health). For a pilot the damage goes to the suit's armour
/// on the host and the player's own damage is set to zero on every peer, so a pilot never loses health or goes down while the
/// suit stands. The game changes a player's health only on the host, and a peer that applies a hit itself also sends it there (a
/// projectile is applied by the hit player's own peer), so the host sees every hit. Hits on the suit's shell outside the person reach
/// this through <see cref="PilotHull"/>. ForgeCore patches the method once
/// (<see cref="ForgeCore.PlayerHits.Redirect"/>); this is the redirect it calls.</summary>
internal static class PilotDamage
{
    internal static float Redirect(Dam_PlayerDamageBase receiver, float damage, Agent? sourceAgent)
    {
        try
        {
            if (!(damage > 0f) || FrameWorld.All.Count == 0) return damage;
            var agent = receiver.GetBaseAgent()?.TryCast<PlayerAgent>();
            if (agent == null || agent.Owner == null) return damage;
            var unit = FrameWorld.PilotUnit(agent.Owner.Lookup);
            var shelter = FrameWorld.ShelterFor(agent.Position, sourceAgent);
            if (unit == null && shelter == null) return damage;
            if (!ForgeCore.Session.IsHost) return 0f;
            if (shelter != null) FrameWorld.HostShield(shelter, damage, FrameWorld.From(sourceAgent), unit == shelter);
            else FrameWorld.HostDamage(unit!, agent, damage, sourceAgent);
            return 0f;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Exosuit damage redirect failed: " + error); return damage; }
    }
}

/// <summary>A pilot gains no infection while inside the suit (spitters, infected hits); cures still apply. ForgeCore patches the
/// method once (<see cref="ForgeCore.PlayerHits.GateInfection"/>); this is the gate it calls.</summary>
internal static class PilotInfection
{
    internal static bool Allow(Dam_PlayerDamageBase receiver, pInfection data)
    {
        if (!(data.amount > 0f) || FrameWorld.All.Count == 0) return true;
        var agent = receiver.GetBaseAgent()?.TryCast<PlayerAgent>();
        if (agent == null || agent.Owner == null || FrameWorld.PilotUnit(agent.Owner.Lookup) == null) return true;
        return false;
    }
}

/// <summary>The special weapon's select key pressed while that slot is already held swaps the suit gun and the pilot's own special
/// weapon (read before the game's own check, which does nothing for the slot already held). Special ammunition pickups are
/// ForgeCore's (<see cref="ForgeCore.Gear.SpecialSlot"/>): the suit gun is never refilled.</summary>
internal static class PilotGunSwap
{
    internal static void Prefix(PlayerInventoryLocal __instance, InputAction action, InventorySlot slot)
    {
        if (action != InputAction.SelectSpecial || slot != InventorySlot.GearSpecial || LocalPilot.Unit == null) return;
        try
        {
            if (__instance.WieldedSlot != InventorySlot.GearSpecial || InputMapper.GetButtonDown == null || !InputMapper.GetButtonDown.Invoke(action, eFocusState.FPS)) return;
            MechGun.ToggleKey(Time.time);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Exosuit gun swap hook failed: " + error); }
    }
}

/// <summary>Hits of the local player's weapon: the suit gun's damage multiplier (restored afterwards; the hit data is the
/// caller's), and the shot shown to other peers from the suit's gun hand.</summary>
internal static class PilotBullet
{
    internal static void Subscribe() => ForgeCore.GameHooks.BulletHit.Around(
        call => Prefix(call.Data),
        (call, original, _) => Restore(call.Data, original),
        (call, original, _) => Restore(call.Data, original));

    private static float Prefix(Weapon.WeaponHitData weaponRayData)
    {
        float state = 0f;
        if (LocalPilot.Unit == null || weaponRayData == null) return state;
        try
        {
            var owner = weaponRayData.owner;
            if (owner == null || !owner.IsLocallyOwned) return state;
            var multiplier = MechGun.DamageMultiplier(owner.Inventory?.WieldedItem);
            if (multiplier != 1f)
            {
                state = weaponRayData.damage;
                weaponRayData.damage *= multiplier;
            }
            LocalPilot.Shot(weaponRayData.rayHit.point);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Exosuit bullet hook failed: " + error); }
        return state;
    }

    private static void Restore(Weapon.WeaponHitData weaponRayData, float original)
    {
        if (original > 0f && weaponRayData != null) weaponRayData.damage = original;
    }
}

/// <summary>The planted suit gun's faster fire: after the game's own shot sets the next-shot timer (<c>PostFireCheck</c> runs after
/// <c>OnFireShot</c> on every archetype), <see cref="MechGun.AfterShot"/> keeps the planted rate exact and fires the shots already due. Only the local pilot's lent gun is touched.</summary>
[HarmonyPatch(typeof(BulletWeaponArchetype), nameof(BulletWeaponArchetype.PostFireCheck))]
internal static class SiegeFireRate
{
    [HarmonyPostfix]
    private static void Postfix(BulletWeaponArchetype __instance)
    {
        if (LocalPilot.Unit == null || __instance == null) return;
        try { MechGun.AfterShot(__instance); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Exosuit siege fire-rate hook failed: " + error); }
    }
}

/// <summary>The suit's faster reload, for any gun the local pilot holds: the gun instance's own reload duration
/// (<c>BulletWeapon.get_ReloadTime</c>, what the game's reload animation and ammunition transfer run under) times
/// <see cref="GunHandling.Reload"/> (the model's handling factor, times the siege's while planted). Shared data blocks are never written.
/// The duration and the factor are kept for the reload bar and the reload log. ForgeCore applies it as a handling lease.</summary>
internal static class PilotReload
{
    internal static float Factor(BulletWeapon weapon, float duration)
    {
        if (LocalPilot.Unit == null || weapon == null) return 1f;
        try
        {
            var factor = MechGun.Reload(weapon);
            if (factor != 1f) MechGun.ReloadAsked(weapon, duration, factor);
            return factor;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Exosuit reload hook failed: " + error); return 1f; }
    }
}

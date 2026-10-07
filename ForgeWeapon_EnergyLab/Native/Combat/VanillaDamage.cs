using System;
using Gear;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

internal enum TargetRelationFilter { Enemies }

// Native damage is submitted by the shooter's fire entry or by the host, never by a presentation packet.
internal static class VanillaDamage
{
    internal static bool IsShooter(Collider collider, PlayerAgent shooter)
        => collider.GetComponentInParent<IDamageable>()?.GetBaseAgent()?.Pointer == shooter.Pointer
            || collider.GetComponentInParent<PlayerAgent>()?.Pointer == shooter.Pointer;

    // Deduplicate actors across limbs and non-actor receivers across their colliders.
    internal static long Identity(IDamageable receiver)
        => (receiver.GetBaseDamagable() ?? receiver).Pointer.ToInt64();

    internal static bool DirectReceiver(PlayerAgent shooter, Collider collider, out long identity)
    {
        identity = 0;
        if (IsShooter(collider, shooter)) return false;
        var receiver = collider.GetComponentInParent<IDamageable>();
        if (receiver == null) return false;
        var agent = receiver.GetBaseAgent();
        if (agent != null && (!agent.Alive || agent.DimensionIndex != shooter.DimensionIndex)) return false;
        identity = Identity(receiver);
        return true;
    }

    /// <summary>Deals one energy hit. Inside a fire entry it is the shooter's own hit, exactly as a stock bullet's; outside
    /// one only the host calls it, for projectiles, the gravity field and bot shots.</summary>
    internal static void Hit(Weapon.WeaponHitData hit)
    {
        var shooter = hit.owner ?? throw new InvalidOperationException("A native energy hit requires its shooter.");
        var collider = hit.rayHit.collider;
        if (collider == null || IsShooter(collider, shooter)) return;
        // Every resolved limb uses the stock bullet's directional, weakspot and armor rules.
        // Prepared hits keep the stock packet category (0), including hits outside Fire.
        var frame = EnergyNativeFire.Current;
        var applying = frame?.ApplyingHit == true;
        if (frame != null) frame.ApplyingHit = true;
        try { BulletWeapon.BulletHit(hit, true, 0f, 0u, true); }
        finally
        {
            if (frame != null) frame.ApplyingHit = applying;
        }
    }
}

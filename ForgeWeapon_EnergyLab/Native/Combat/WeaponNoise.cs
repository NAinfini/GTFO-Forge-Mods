using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>
/// The game's own gameplay noise for energy shots. Vanilla gunfire never posts a world noise event: BulletWeapon.Fire
/// and Shotgun.Fire on the shooter, and BulletWeaponSynced.Fire and ShotgunSynced.Fire on every other peer, set the
/// shooter's <c>PlayerAgent.Noise = Agent.NoiseType.Shoot</c> once per shot. Sleeping enemies read that value in
/// EnemyDetection.UpdateHibernationDetection on the host. Every energy mode keeps those Fire bodies.
/// Vanilla explosions (mine detonation) post <c>NoiseManager.MakeNoise</c> instead; exploding energy projectiles (blast gun and
/// charge orb) make that noise on the host only, through ForgeCore's <c>Combat.Noise</c> (the mine's parameters, with the gear's
/// radius).
/// </summary>
internal static class WeaponNoise
{
    /// <summary>An energy projectile's explosion. Call only where the explosion's damage is authoritative (the host).</summary>
    internal static void Explosion(PlayerAgent owner, EnergyGear gear, Vector3 position)
    {
        if (!ForgeCore.Level.CourseNodeAt(position, 1f, out var node, out _, (int)owner.DimensionIndex) || node == null) node = owner.CourseNode;
        // Outside every area (out of bounds, falling) there are no enemies to alert.
        if (node == null) return;
        ForgeCore.Combat.Noise(position, gear.Numbers.NoiseRadius, node);
    }
}

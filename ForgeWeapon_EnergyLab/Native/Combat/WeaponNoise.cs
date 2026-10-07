using AIGraph;
using Agents;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

/// <summary>
/// The game's own gameplay noise for energy shots. Vanilla gunfire never posts a world noise event: BulletWeapon.Fire
/// and Shotgun.Fire on the shooter, and BulletWeaponSynced.Fire and ShotgunSynced.Fire on every other peer, set the
/// shooter's <c>PlayerAgent.Noise = Agent.NoiseType.Shoot</c> once per shot. Sleeping enemies read that value in
/// EnemyDetection.UpdateHibernationDetection on the host. Every energy mode keeps those Fire bodies.
/// Vanilla explosions (mine detonation) post
/// <c>NoiseManager.MakeNoise</c> instead; exploding energy projectiles (blast gun and charge orb) use the mine's values
/// on the host only.
/// </summary>
internal static class WeaponNoise
{
    // MineDeployerInstance_Detonate_Explosive.DoExplode: radiusMin 0, radiusMax 20, yScale 1, InstaDetect,
    // includeToNeightbourAreas true, raycastFirstNode false, no noise maker.
    private const float ExplosionRadiusMin = 0f;
    private const float ExplosionYScale = 1f;
    private const NM_NoiseType ExplosionType = NM_NoiseType.InstaDetect;

    /// <summary>An energy projectile's explosion. Call only where the explosion's damage is authoritative (the host).</summary>
    internal static void Explosion(PlayerAgent owner, EnergyGear gear, Vector3 position)
    {
        if (!Dimension.TryGetCourseNodeFromPos(position, out var node) || node == null) node = owner.CourseNode;
        // Outside every area (out of bounds, falling) there are no enemies to alert.
        if (node == null) return;
        // The generated nine-argument ctor reads this.Pointer without first boxing the native value.
        // The parameterless ctor boxes it correctly; initialize every field with the vanilla mine's values.
        var noise = new NM_NoiseData()
        {
            noiseMaker = null,
            node = node,
            position = position,
            radiusMin = ExplosionRadiusMin,
            radiusMax = gear.Numbers.NoiseRadius,
            yScale = ExplosionYScale,
            type = ExplosionType,
            includeToNeightbourAreas = true,
            raycastFirstNode = false
        };
        NoiseManager.MakeNoise(noise);
    }
}

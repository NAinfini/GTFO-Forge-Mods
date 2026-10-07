using System;
using Enemies;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

// Enemy explosions use real limb hits; the native helper retains every other receiver and rigidbody.
internal sealed class EnergyExplosion
{
    private const int Capacity = 512;
    private const float BlockerToleranceSqr = 0.1f * 0.1f;
    private struct Target
    {
        internal long Identity;
        internal EnemyAgent Enemy;
        internal Collider Collider;
        internal Vector3 Point;
        internal float Distance;
        internal RaycastHit Hit;
        internal bool Visible;
        internal Vector3 ForceOrigin;
        internal float NativeForce;
        internal bool HasNativeForce;
    }

    private readonly Il2CppReferenceArray<Collider> _overlap = new(Capacity);
    private readonly Target[] _targets = new Target[Capacity];
    private readonly NativeShot _shot = new();

    internal void Apply(PlayerAgent owner, EnergyGear gear, Vector3 position, float radius, float damage, uint sequence = 0,
        ShotTuning? nativeTuning = null)
    {
        var enemyMask = LayerManager.MASK_EXPLOSION_TARGETS & LayerManager.MASK_ENEMY_DAMAGABLE;
        var nativeMask = LayerManager.MASK_EXPLOSION_TARGETS & ~LayerManager.MASK_ENEMY_DAMAGABLE;
        var count = Physics.OverlapSphereNonAlloc(position, radius, _overlap, enemyMask, QueryTriggerInteraction.UseGlobal);
        if (count == _overlap.Length)
            throw new InvalidOperationException("Energy explosion enemy budget exhausted; truncated damage refused.");
        var targets = 0;
        try
        {
            for (var index = 0; index < count; index++)
            {
                var collider = _overlap[index];
                var enemy = collider == null ? null : VisibleEnemies.SelectEnemy(owner, collider, TargetRelationFilter.Enemies);
                if (enemy == null) continue;
                var receiver = collider!.GetComponent<IDamageable>();
                if (receiver?.TryCast<Dam_EnemyDamageLimb>() == null) continue;
                var point = collider.ClosestPoint(position);
                var distance = (point - position).magnitude;
                if (distance > radius) continue;
                var identity = VanillaDamage.Identity(receiver);
                var slot = 0;
                while (slot < targets && _targets[slot].Identity != identity) slot++;
                var first = slot == targets;
                if (first) { _targets[slot] = new Target { Identity = identity, Enemy = enemy }; targets++; }
                ref var target = ref _targets[slot];
                if (first || distance < target.Distance)
                {
                    target.Collider = collider;
                    target.Point = point;
                    target.Distance = distance;
                }
                // Retain the native helper's first visible receiver for death force, independent of the closest damage limb.
                if (!target.HasNativeForce)
                {
                    var forcePoint = receiver.DamageTargetPos;
                    var forceOffset = forcePoint - position;
                    var forceDistance = forceOffset.magnitude;
                    var forceDirection = forceOffset.normalized;
                    if (Visible(collider, position, forcePoint, forceDirection, forceDistance))
                    {
                        var force = new LowResVector3();
                        force.Set(forceDirection * (gear.Numbers.ExplosionForce * (1f - Mathf.Clamp01(forceDistance / radius))), 10f);
                        target.NativeForce = force.Get(10f).magnitude;
                        var origin = new LowResVector3();
                        origin.Set(position - enemy.Position, 10f);
                        target.ForceOrigin = enemy.Position + origin.Get(10f);
                        target.HasNativeForce = true;
                    }
                }
            }
            // Resolve every limb and refuse either full query before the first damage or force write.
            for (var index = 0; index < targets; index++)
            {
                ref var target = ref _targets[index];
                if (!_shot.RayTarget(position, target.Point, target.Collider, out target.Hit))
                {
                    Plugin.Error("Energy explosion could not resolve an enemy's radial limb ray; that enemy took no blast damage: " +
                        new InvalidOperationException("The enemy had no visible native damage limb."));
                    continue;
                }
                var offset = target.Hit.point - position;
                if (!Visible(target.Hit.collider, position, target.Hit.point, offset.normalized, offset.magnitude)) continue;
                target.Visible = true;
            }
            var nativeCount = Physics.OverlapSphereNonAlloc(position, radius, _overlap, nativeMask, QueryTriggerInteraction.UseGlobal);
            if (nativeCount == _overlap.Length || nativeCount >= DamageUtil.s_tempColliders.Length)
                throw new InvalidOperationException("Native explosion target budget exhausted; truncated damage refused.");
            // Run this query before enemy destruction can spawn new physics objects into its target volume.
            DamageUtil.DoExplosionDamage_Capsule(position, Vector3.zero, radius, 0f, radius, 0f, damage,
                nativeMask, LayerManager.MASK_EXPLOSION_BLOCKERS, SNet.IsMaster, gear.Numbers.ExplosionForce,
                null, DamageUtil.ExplosionDamageType.Damage, gear.CategoryId);
            for (var index = 0; index < targets; index++)
            {
                var target = _targets[index];
                if (!target.Visible || target.Collider == null || target.Enemy == null || !target.Enemy.Alive || target.Enemy.DimensionIndex != owner.DimensionIndex) continue;
                var falloff = 1f - Mathf.Clamp01(target.Distance / radius);
                var direction = (target.Hit.point - position).normalized;
                // Explosion falloff is already applied, even when an authored blast exceeds the gun's ray range.
                var tuning = (nativeTuning ?? gear.Numbers.Shot) with { Damage = damage * falloff, Range = MathF.Max(radius, gear.Numbers.Shot.Range) };
                _shot.ResolveHit(owner, tuning, position, direction, target.Hit);
                _shot.Apply();
                if (!target.Enemy.Alive && target.HasNativeForce)
                {
                    if (SNet.IsMaster) target.Enemy.Damage.SendExplosionForce(target.ForceOrigin, target.NativeForce);
                    else EnergyBlastForces.Request(sequence, target.Enemy, target.NativeForce);
                }
            }
        }
        finally { Array.Clear(_targets, 0, targets); }
    }

    // Clients submit damage once. The host copies only the helper's non-damageable rigidbody branch.
    internal void ApplyRigidbodyForce(EnergyGear gear, Vector3 position)
    {
        if (!SNet.IsMaster) throw new InvalidOperationException("Explosion rigidbody force requires the host.");
        var radius = gear.Numbers.BlastRadius;
        var mask = LayerManager.MASK_EXPLOSION_TARGETS & ~LayerManager.MASK_ENEMY_DAMAGABLE;
        var count = Physics.OverlapSphereNonAlloc(position, radius, _overlap, mask, QueryTriggerInteraction.UseGlobal);
        if (count == _overlap.Length) throw new InvalidOperationException("Explosion rigidbody budget exhausted; truncated force refused.");
        for (var index = 0; index < count; index++)
        {
            var collider = _overlap[index];
            if (collider == null) continue;
            if (collider.GetComponent<IDamageable>() == null && collider.GetComponent<PlayerPingTarget>() is { } ping &&
                ping.m_pingTargetStyle == eNavMarkerStyle.PlayerPingDoor && collider.GetComponentInParent<iLG_WeakDoor_Destruction>() is { } door)
                collider = door.FindCollider(position);
            if (collider == null || collider.GetComponent<IDamageable>() != null || collider.attachedRigidbody == null) continue;
            var body = collider.attachedRigidbody;
            var offset = collider.transform.position - position;
            var falloff = 1f - Mathf.Clamp01(offset.magnitude / radius);
            body.AddForce(offset.normalized * MathF.Min(gear.Numbers.ExplosionForce * falloff, body.mass * 20f), ForceMode.Impulse);
        }
    }

    private static bool Visible(Collider collider, Vector3 origin, Vector3 point, Vector3 direction, float distance)
        // DoExplosionDamage_Capsule also accepts the target collider itself, or a blocker within 0.1 m of the target.
        => !Physics.Raycast(origin, direction, out var blocker, distance, LayerManager.MASK_EXPLOSION_BLOCKERS,
            QueryTriggerInteraction.UseGlobal) || blocker.collider == collider || (blocker.point - point).sqrMagnitude < BlockerToleranceSqr;
}

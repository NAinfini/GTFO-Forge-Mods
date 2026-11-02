using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

// The Blast Gun's shells. A shell leaves the muzzle along the aim, moves by its speed every frame and explodes at the first thing its
// sweep touches (terrain, enemy or player); the host deals the explosion, every other peer only draws it. Nothing here allocates per frame.
internal sealed class BlastShells
{
    internal const int Capacity = 64;
    private const int QueryCapacity = 128;
    /// <summary>The shell's collision radius in metres.</summary>
    private const float BodyRadius = 0.05f;
    /// <summary>The shell never ends by distance: this is how far it may fly (metres), beyond any level sightline.</summary>
    private const float Reach = 1000f;
    private sealed class Shell
    {
        internal PlayerAgent? Owner;
        internal EnergyGear Gear = null!;
        internal Vector3 Position, Direction;
        internal float Until, Damage;
        internal int Dimension;
        internal bool Fresh;
    }

    private readonly Shell[] _shells = new Shell[Capacity];
    private readonly Il2CppStructArray<RaycastHit> _sweep = new(QueryCapacity);
    private readonly ForgeCore.ColliderQuery _overlap = new(QueryCapacity);
    private readonly EnergyExplosion _explosion = new();
    private readonly NativeShot _contact = new();
    private readonly Presentation.SpecialEffects _visuals;
    private readonly bool _damageEnabled;
    private int _active;

    internal BlastShells(bool damageEnabled, Presentation.SpecialEffects visuals)
    {
        _damageEnabled = damageEnabled;
        _visuals = visuals;
        for (var index = 0; index < Capacity; index++) _shells[index] = new Shell();
    }

    internal bool CanLaunch => _active < Capacity;
    internal bool Idle => _active == 0;

    internal void Launch(PlayerAgent owner, EnergyGear gear, Vector3 position, Vector3 direction, float damage, float now)
    {
        if (direction.sqrMagnitude < 0.0001f) throw new ArgumentException("Blast shell direction is empty.");
        for (var index = 0; index < Capacity; index++)
        {
            var shell = _shells[index];
            if (shell.Owner != null) continue;
            shell.Owner = owner; shell.Gear = gear; shell.Dimension = (int)owner.DimensionIndex;
            shell.Position = position; shell.Direction = direction.normalized;
            shell.Until = now + Reach / gear.Numbers.Speed;
            shell.Damage = damage;
            shell.Fresh = true;
            _active++;
            _visuals.Shell(index, position, now);
            return;
        }
        throw new BudgetExhausted("All blast shell slots are occupied.");
    }

    internal void Tick(float delta, float now)
    {
        if (_active == 0 || EnergyNativeFire.InFireCall) return;
        for (var index = 0; index < Capacity; index++)
        {
            var shell = _shells[index];
            if (shell.Owner == null) continue;
            // A query that outgrew its budget drops this shell only, never the gun.
            try { Advance(index, shell, delta, now); }
            catch (BudgetExhausted error)
            {
                Plugin.Error("A blast shell was dropped: " + error);
                End(index);
            }
        }
    }

    private void Advance(int index, Shell shell, float delta, float now)
    {
        var owner = shell.Owner!;
        if (owner.WasCollected || !owner.Alive || (int)owner.DimensionIndex != shell.Dimension || now >= shell.Until) { End(index); return; }
        // Even a shell launched inside a receiver waits until Fire has returned before it explodes.
        if (shell.Fresh)
        {
            shell.Fresh = false;
            var count = _overlap.Sphere(shell.Position, BodyRadius, LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
            if (_overlap.Truncated) throw new BudgetExhausted("Blast shell spawn overlap budget exhausted.");
            for (var row = 0; row < count; row++)
            {
                var collider = _overlap[row];
                if (collider == null || ForgeCore.Combat.IsPartOf(collider, owner)) continue;
                Explode(index, shell, now, NativeShot.OverlapContact(collider, shell.Position, shell.Direction));
                return;
            }
        }
        var distance = shell.Gear.Numbers.Speed * delta;
        var hits = Physics.SphereCastNonAlloc(shell.Position, BodyRadius, shell.Direction, _sweep, distance,
            LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
        if (hits == _sweep.Length) throw new BudgetExhausted("Blast shell sweep budget exhausted; no truncated collision result used.");
        var stop = -1;
        for (var row = 0; row < hits; row++)
        {
            var collider = _sweep[row].collider;
            if (collider == null || ForgeCore.Combat.IsPartOf(collider, owner)) continue;
            if (stop < 0 || _sweep[row].distance < _sweep[stop].distance) stop = row;
        }
        if (stop < 0)
        {
            shell.Position += shell.Direction * distance;
            _visuals.Shell(index, shell.Position, now);
            return;
        }
        var hit = _sweep[stop];
        // A sweep that starts inside a receiver can report a zero-distance placeholder point.
        if (hit.distance <= 0f) hit.point = hit.collider.ClosestPoint(shell.Position);
        shell.Position += shell.Direction * MathF.Max(0f, hit.distance - 0.01f);
        Explode(index, shell, now, hit);
    }

    private void Explode(int index, Shell shell, float now, RaycastHit hit)
    {
        var owner = shell.Owner!;
        var gear = shell.Gear;
        var position = shell.Position;
        // Claim the end of this shell before any native write; a partial explosion is never retried.
        End(index);
        _visuals.GrenadeExplosion(position, gear.Numbers.BlastRadius, now);
        if (!_damageEnabled) return;
        // The contact itself carries no damage (the explosion does); it registers the shot on the enemy limb it struck.
        if (_contact.ContactTarget(position, shell.Direction, hit, out var limb))
        {
            _contact.ResolveHit(owner, gear.Numbers.Shot with { Damage = 0f, Stagger = 0f }, position, shell.Direction, limb);
            if (_contact.Receiver?.TryCast<Dam_EnemyDamageLimb>() != null) _contact.Apply(0f, 0f);
        }
        _explosion.Apply(owner, gear, position, gear.Numbers.BlastRadius, shell.Damage);
        WeaponNoise.Explosion(owner, gear, position);
    }

    private void End(int index)
    {
        var shell = _shells[index];
        if (shell.Owner == null) return;
        shell.Owner = null;
        _active--;
        _visuals.EndShell(index);
    }

    internal void Clear()
    {
        for (var index = 0; index < Capacity; index++) End(index);
    }
}

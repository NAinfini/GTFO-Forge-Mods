using System;
using Gear;
using SNetwork;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

// Every discharge resolves inside the native Fire ray callback. Delayed modes only resolve their launch aim here.
// Helper queries use physics, never CastWeaponRay.
internal sealed class EnergyDischarge
{
    private readonly NativeShot[] _shots = new NativeShot[EnergyLimits.ArcTargets];
    private readonly ChainLightning _chain = new();
    private readonly EnergyExplosion _explosion = new();

    internal EnergyDischarge()
    {
        for (var index = 0; index < _shots.Length; index++) _shots[index] = new NativeShot();
    }

    internal void ApplyRemoteForce(EnergyGear gear, Vector3 position) => _explosion.ApplyRigidbodyForce(gear, position);

    internal FireVisual Resolve(BulletWeapon weapon, EnergyGear gear, Vector3 origin, Vector3 direction,
        SpecialWeapons special, float now)
    {
        if (!float.IsFinite(direction.sqrMagnitude) || direction.sqrMagnitude < 0.000001f)
            throw new InvalidOperationException("Native energy fire has no resolved direction.");
        var owner = weapon.Owner;
        var hit = EnergyNativeFire.Current?.Hit ?? throw new InvalidOperationException("The energy discharge has no native hit data.");
        // Keep the native shooter's damage, stagger and precision preparation, including lower-layer modifiers.
        var tuning = gear.Numbers.Shot with { Damage = hit.damage, Stagger = hit.staggerMulti, Precision = hit.precisionMulti };
        if (weapon.IsFirstPerson) _shots[0].Trace(weapon, tuning, origin, direction);
        else _shots[0].TraceRemote(owner, tuning, weapon.MuzzleAlign.position, direction);
        var start = _shots[0].Start;
        direction = (_shots[0].End - start).sqrMagnitude > 0.000001f
            ? (_shots[0].End - start).normalized : direction.normalized;
        var visual = new FireVisual
        {
            Epoch = EnergyNetwork.Epoch, Sequence = EnergyNetwork.NextSequence(), Shooter = owner.Owner.Lookup,
            CategoryId = gear.CategoryId, Mode = (int)gear.Mode, Segments = 1,
            X = start.x, Y = start.y, Z = start.z, Dx = direction.x, Dy = direction.y, Dz = direction.z,
            Power = EnergyNativeFire.Current!.Power
        };
        // Launches use the native direction and our muzzle trace, but contacts and pulses wait for a later Tick.
        if (EnergyNativeFire.Delayed(gear.Mode)) { visual.Segments = 0; return visual; }
        if (gear.Mode == EnergyMode.ArcChain) visual.Segments = _chain.Continue(owner, gear.Numbers, tuning, _shots);
        for (var index = 0; index < visual.Segments; index++)
        {
            visual.SetEnd(index, _shots[index].End);
            if (_shots[index].HasHit) visual.HitMask |= 1 << index;
        }
        switch (gear.Mode)
        {
            case EnergyMode.Beam:
            case EnergyMode.ArcChain:
                for (var index = 0; index < visual.Segments; index++) _shots[index].Apply();
                break;
            case EnergyMode.Flame:
                visual.Power = special.FlameRange(gear, start, direction);
                special.FlameTargets(owner, gear, tuning, start, direction, visual.Power, now);
                special.DamageFlame();
                break;
            case EnergyMode.Blast:
                if (_shots[0].HasHit)
                {
                    // The contact has no separate damage budget; the blast retains its existing damage and falloff.
                    if (_shots[0].Receiver?.TryCast<Dam_EnemyDamageLimb>() != null) _shots[0].Apply(0f, 0f);
                    var position = visual.End(0) - direction * 0.04f;
                    _explosion.Apply(owner, gear, position, gear.Numbers.BlastRadius, tuning.Damage, visual.Sequence, tuning);
                    if (SNet.IsMaster) WeaponNoise.Explosion(owner, gear, position);
                }
                break;
            default: throw new InvalidOperationException("This mode has no energy discharge: " + gear.Mode);
        }
        return visual;
    }
}

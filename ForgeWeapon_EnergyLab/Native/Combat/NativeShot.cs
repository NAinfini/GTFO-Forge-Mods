using Gear;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>ForgeCore's shot from a point at a collider (<c>Combat.Shot</c>: the ray, the receiver check and the stock bullet
/// resolution) with the two things that are Energy Lab's own: the gun's <see cref="ShotTuning"/> as the shot's numbers, and the mark on the
/// current native fire entry while a hit is submitted, so this gun's own bullet patch can tell its hit from other bullets. Presentation
/// receives only positions and never applies damage.</summary>
internal sealed class NativeShot : ForgeCore.Combat.Shot
{
    private static ForgeCore.Combat.BulletProfile Profile(in ShotTuning tuning)
        => new(tuning.Damage, tuning.Stagger, tuning.Precision, tuning.Range);

    internal void Trace(BulletWeapon weapon, in ShotTuning tuning) => Trace(weapon, Profile(tuning));

    internal void Trace(BulletWeapon weapon, in ShotTuning tuning, Vector3 origin, Vector3 direction)
        => Trace(weapon, Profile(tuning), origin, direction);

    internal void TraceRemote(PlayerAgent owner, in ShotTuning tuning, Vector3 start, Vector3 direction)
        => Trace(owner, Profile(tuning), start, direction);

    internal void ResolveHit(PlayerAgent owner, in ShotTuning tuning, Vector3 start, Vector3 direction, RaycastHit hit)
        => ResolveHit(owner, Profile(tuning), start, direction, hit);

    // Inside a fire entry it is the shooter's own hit, exactly as a stock bullet's; outside one only the host calls it, for
    // projectiles, the gravity field and bot shots.
    protected override void Submit(Weapon.WeaponHitData hit)
    {
        var frame = EnergyNativeFire.Current;
        var applying = frame?.ApplyingHit == true;
        if (frame != null) frame.ApplyingHit = true;
        try { base.Submit(hit); }
        finally
        {
            if (frame != null) frame.ApplyingHit = applying;
        }
    }
}

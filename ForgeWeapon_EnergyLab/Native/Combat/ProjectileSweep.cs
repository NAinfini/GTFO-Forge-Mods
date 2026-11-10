using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>Which colliders a projectile's sweep flies through (does not stop on) besides its owner's own.</summary>
internal enum SweepPass : byte
{
    /// <summary>None besides the owner's: it stops on anything else.</summary>
    Owner,
    /// <summary>Every enemy's and player's.</summary>
    Actors,
    /// <summary>Every agent's (enemy, player, other): it stops on scenery only.</summary>
    Agents,
    /// <summary>Players' only: it stops on anything else.</summary>
    Players,
}

/// <summary>The sphere sweep every flying projectile of this package makes each frame, with its one result buffer: the casts of the
/// Blast Gun's shells, the plasma flights, the ricochet discs and the gravity projectile all come through here. Nothing allocates.</summary>
internal sealed class ProjectileSweep
{
    private readonly ForgeCore.SweepQuery _hits;

    internal ProjectileSweep(int capacity) => _hits = new ForgeCore.SweepQuery(capacity);

    /// <summary>The <paramref name="row"/>th nearest hit of the last <see cref="Cast"/>.</summary>
    internal RaycastHit this[int row] => _hits[row];

    /// <summary>Sweeps a sphere of <paramref name="radius"/> from <paramref name="start"/> along <paramref name="direction"/> for
    /// <paramref name="distance"/> metres against the game's bullet-weapon ray layers; returns how many hits it holds, nearest first
    /// and complete (the shared query grows its buffer instead of cutting the answer short).</summary>
    internal int Cast(Vector3 start, float radius, Vector3 direction, float distance)
        => _hits.Cast(start, radius, direction, distance, LayerManager.MASK_BULLETWEAPON_RAY);

    /// <summary>The row of the nearest of the first <paramref name="count"/> hits that stops the projectile (a hit on <paramref name="owner"/>,
    /// when given, and the colliders <paramref name="pass"/> names never do), or -1 when nothing does.</summary>
    internal int Nearest(int count, PlayerAgent? owner, SweepPass pass = SweepPass.Owner)
    {
        for (var row = 0; row < count; row++)
        {
            var collider = _hits[row].collider;
            if (collider == null || (owner != null && ForgeCore.Combat.IsPartOf(collider, owner))) continue;
            var skip = pass switch
            {
                SweepPass.Actors => ColliderKinds.IsActor(collider),
                SweepPass.Agents => ColliderKinds.Of(collider) != ColliderKind.None,
                SweepPass.Players => ColliderKinds.Of(collider) == ColliderKind.Player,
                _ => false,
            };
            if (!skip) return row;
        }
        return -1;
    }
}

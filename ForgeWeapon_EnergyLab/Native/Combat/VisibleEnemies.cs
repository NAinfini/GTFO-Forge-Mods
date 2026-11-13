using System;
using Enemies;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

// Shared by the hand-held chain and the moving orb; all candidates require a resolved visible hit.
internal sealed class VisibleEnemies
{
    private readonly ForgeCore.SweepQuery _blockers = new(64);
    private ArcCandidate[] _candidates = new ArcCandidate[64];
    private readonly NativeShot _shot = new();

    /// <summary>The nearest enemy in <paramref name="context"/>'s dimension that is hit by a clear segment from <paramref name="origin"/>.
    /// The candidates are every enemy limb in range (<c>ForgeCore.Enemies.Limbs</c>), so the answer is never cut short.</summary>
    internal bool Nearest(PlayerAgent context, bool hitInterveningReceiver,
        Vector3 origin, float range, ReadOnlySpan<long> visited, long ignore,
        out RaycastHit hit, out long identity)
    {
        hit = default;
        identity = 0;
        var limbs = ForgeCore.Enemies.Limbs(origin, range, QueryTriggerInteraction.Ignore);
        if (_candidates.Length < limbs.Length) _candidates = new ArcCandidate[limbs.Length];
        for (var index = 0; index < limbs.Length; index++)
        {
            var (collider, enemy) = limbs[index];
            _candidates[index] = enemy.DimensionIndex == context.DimensionIndex
                ? new ArcCandidate(enemy.Pointer.ToInt64(), (collider.bounds.center - origin).sqrMagnitude, true) : default;
        }
        while (true)
        {
            var chosen = ArcSelection.Nearest(_candidates.AsSpan(0, limbs.Length), visited, range);
            if (chosen < 0) return false;
            var collider = limbs[chosen].Collider;
            // Unity rays omit a collider containing their origin; a moving orb may be inside an enemy.
            var containsOrigin = (collider.ClosestPoint(origin) - origin).sqrMagnitude < 0.00000001f;
            if (containsOrigin)
            {
                if (_shot.RayTarget(origin, collider.bounds.center, collider, out hit))
                {
                    identity = _candidates[chosen].Identity;
                    return true;
                }
            }
            // A physical segment can hit a different enemy before its candidate; dedupe the actual receiver too.
            if (Visible(context, hitInterveningReceiver, origin, collider.bounds.center, ignore, _candidates[chosen].Identity,
                out hit, out identity) && !visited.Contains(identity)) return true;
            _candidates[chosen] = _candidates[chosen] with { Eligible = false };
        }
    }

    private bool Visible(PlayerAgent context, bool hitInterveningReceiver, Vector3 start, Vector3 target,
        long ignore, long wanted, out RaycastHit hit, out long recipient)
    {
        hit = default;
        recipient = 0;
        var direction = target - start;
        var distance = direction.magnitude;
        if (distance < 0.001f) return false;
        // Nearest first; the first hit that is not the shooter's own body or the ignored agent is what the segment meets.
        var count = _blockers.Cast(start, 0f, direction / distance, distance + 0.025f,
            LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
        var nearestIsReceiver = false;
        for (var index = 0; index < count; index++)
        {
            var candidate = _blockers[index];
            var collider = candidate.collider;
            if (collider == null || ForgeCore.Combat.IsPartOf(collider, context)) continue;
            var receiver = collider.GetComponentInParent<IDamageable>();
            var identity = receiver?.GetBaseAgent()?.Pointer.ToInt64() ?? 0L;
            if (ignore != 0 && identity == ignore) continue;
            recipient = identity;
            hit = candidate;
            nearestIsReceiver = receiver != null;
            break;
        }
        // Only enemies are selected. A chain's physical segment can hit a different receiver en route;
        // that collision ends the chain. Pulses do not select that blocker.
        return recipient == wanted || (hitInterveningReceiver && hit.collider != null && nearestIsReceiver);
    }
}

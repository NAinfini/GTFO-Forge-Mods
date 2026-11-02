using System;
using Enemies;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>Whom an energy effect may select, relative to its explicit context actor (the shooter).</summary>
internal enum TargetRelationFilter { Enemies }

// Shared by the hand-held chain and the moving orb; all candidates require a resolved visible hit.
internal sealed class VisibleEnemies
{
    private const int Capacity = 512;
    private readonly ForgeCore.ColliderQuery _overlap = new(Capacity);
    private readonly Il2CppStructArray<RaycastHit> _blockers = new(64);
    private readonly ArcCandidate[] _candidates = new ArcCandidate[Capacity];
    private readonly NativeShot _shot = new();

    internal static EnemyAgent? SelectEnemy(PlayerAgent context, Collider collider, TargetRelationFilter relation)
    {
        if (relation != TargetRelationFilter.Enemies) throw new ArgumentOutOfRangeException(nameof(relation));
        return ForgeCore.Enemies.FromCollider(collider, context);
    }

    /// <summary>The nearest enemy that is hit by a clear segment from <paramref name="origin"/>. A search that outgrew its fixed query
    /// budget finds nobody (and logs once): no damage is ever dealt from a truncated answer.</summary>
    internal bool Nearest(PlayerAgent context, TargetRelationFilter relation, bool hitInterveningReceiver,
        Vector3 origin, float range, ReadOnlySpan<long> visited, long ignore,
        out RaycastHit hit, out long identity)
    {
        try { return NearestChecked(context, relation, hitInterveningReceiver, origin, range, visited, ignore, out hit, out identity); }
        catch (BudgetExhausted error)
        {
            Plugin.Error("An arc target search was dropped: " + error);
            hit = default; identity = 0;
            return false;
        }
    }

    private bool NearestChecked(PlayerAgent context, TargetRelationFilter relation, bool hitInterveningReceiver,
        Vector3 origin, float range, ReadOnlySpan<long> visited, long ignore,
        out RaycastHit hit, out long identity)
    {
        hit = default;
        identity = 0;
        var count = _overlap.Sphere(origin, range, LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.Ignore);
        if (_overlap.Truncated) throw new BudgetExhausted("Arc target query budget exhausted; no damage submitted.");
        for (var index = 0; index < count; index++)
        {
            var collider = _overlap[index];
            var enemy = collider == null ? null : SelectEnemy(context, collider, relation);
            _candidates[index] = enemy != null
                ? new ArcCandidate(enemy.Pointer.ToInt64(), (collider!.bounds.center - origin).sqrMagnitude, true) : default;
        }
        while (true)
        {
            var chosen = ArcSelection.Nearest(_candidates.AsSpan(0, count), visited, range);
            if (chosen < 0) return false;
            var collider = _overlap[chosen];
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
        var count = Physics.RaycastNonAlloc(start, direction / distance, _blockers, distance + 0.025f,
            LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
        if (count == _blockers.Length) throw new BudgetExhausted("Arc visibility budget exhausted; no damage submitted.");
        var nearest = float.PositiveInfinity;
        var nearestIsReceiver = false;
        for (var index = 0; index < count; index++)
        {
            var candidate = _blockers[index];
            var collider = candidate.collider;
            if (collider == null || ForgeCore.Combat.IsPartOf(collider, context)) continue;
            var receiver = collider.GetComponentInParent<IDamageable>();
            var identity = receiver?.GetBaseAgent()?.Pointer.ToInt64() ?? 0L;
            if ((ignore != 0 && identity == ignore) || candidate.distance >= nearest) continue;
            nearest = candidate.distance;
            recipient = identity;
            hit = candidate;
            nearestIsReceiver = receiver != null;
        }
        // Only enemies are selected. A chain's physical segment can hit a different receiver en route;
        // that collision ends the chain. Pulses do not select that blocker.
        return recipient == wanted || (hitInterveningReceiver && hit.collider != null && nearestIsReceiver);
    }
}

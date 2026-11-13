using System;
using Agents;
using AIGraph;
using Enemies;
using Player;
using UnityEngine;

namespace ForgeOrdnance.Arsenal;

/// <summary>Combat requests only this package makes, built on ForgeCore's own: they move into ForgeCore whole when a second package needs them.</summary>
internal static class ArsenalCombat
{
    private const int AimSlots = 64;

    /// <summary>The living enemy with the highest maximum health within <paramref name="radius"/> of <paramref name="centre"/> (a point on the
    /// floor), the nearer one on a tie, or null for none. An enemy in sight of the centre (<see cref="ForgeCore.Level.HasSight"/>) always beats one
    /// that is not. Only enemies in <paramref name="room"/> (the game's course node the strike landed in) count; a null room finds none, so a
    /// strike never reaches into another room or picks by a guess.</summary>
    internal static EnemyAgent? Largest(Vector3 centre, float radius, AIG_CourseNode? room)
    {
        if (room == null) return null;
        EnemyAgent? best = null;
        var bestSeen = false;
        float bestHealth = 0f, bestDistance = 0f;
        foreach (var found in ForgeCore.Enemies.Near(centre, radius))
        {
            var enemy = found!;
            if (enemy.CourseNode != room) continue;
            var seen = ForgeCore.Level.HasSight(centre, ForgeCore.Enemies.AimPoint(enemy));
            var health = enemy.Damage.HealthMax;
            var distance = Vec.SqrLength(enemy.Position - centre);
            if (best != null && (bestSeen != seen ? bestSeen : health != bestHealth ? health < bestHealth : distance >= bestDistance)) continue;
            best = enemy;
            bestSeen = seen;
            bestHealth = health;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>Host only: throws <paramref name="count"/> pellets of the game's own C-foam from <paramref name="origin"/> in directions spread
    /// evenly over the whole sphere (a turned, jittered Fibonacci spiral, so the same <paramref name="seed"/> gives the same spread on any
    /// machine), each at a speed between <paramref name="minSpeed"/> and <paramref name="maxSpeed"/> metres a second. Every pellet is the manager's
    /// own complete request (<c>ProjectileManager.WantToFireGlue</c>): the game replicates it, lands it, merges it into balls and sticks what it
    /// touches. <paramref name="owner"/> is the player the game credits it to.</summary>
    internal static void SprayGlue(PlayerAgent owner, Vector3 origin, int count, float minSpeed, float maxSpeed, int seed)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (count < 1 || minSpeed <= 0f || maxSpeed < minSpeed) throw new ArgumentOutOfRangeException(nameof(count));
        var rng = new System.Random(seed);
        var turn = (float)(rng.NextDouble() * Math.PI * 2.0);
        for (var i = 0; i < count; i++)
        {
            var height = 1f - 2f * (i + 0.5f) / count;
            var ring = Mathf.Sqrt(Mathf.Max(0f, 1f - height * height));
            var azimuth = turn + i * 2.399963f;
            var speed = minSpeed + (maxSpeed - minSpeed) * (float)rng.NextDouble();
            ProjectileManager.WantToFireGlue(owner, origin, new Vector3(Mathf.Cos(azimuth) * ring, height, Mathf.Sin(azimuth) * ring) * speed);
        }
    }

    /// <summary>The living enemy whose aim point is nearest the unit direction <paramref name="aim"/> from <paramref name="origin"/>: within
    /// <paramref name="reach"/> metres (at least half a metre) and no more than <paramref name="maxAngle"/> degrees off, with a clear
    /// <see cref="ForgeCore.Combat.ShooterRay"/> to it (the shooter's own colliders are stepped over). The enemy nearest the line that fails the
    /// ray gives way to the next nearest, three tries in all; null when none. Considers at most 64 enemies in reach.</summary>
    internal static EnemyAgent? PickAimed(Agent shooter, Vector3 origin, Vector3 aim, float maxAngle, float reach)
    {
        if (shooter == null) throw new ArgumentNullException(nameof(shooter));
        var found = ForgeCore.Enemies.Near(origin + aim * (reach * 0.5f), reach * 0.5f + 1f);
        var n = Math.Min(found.Length, AimSlots);
        if (found.Length > n) Plugin.Logger.ErrorOnce("arsenal-aim-candidates", "A tracking shot looked at " + n + " of the " + found.Length + " enemies in reach; the rest were not considered.");
        ulong skipped = 0;
        var minCos = Mathf.Cos(maxAngle * Mathf.Deg2Rad);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var best = -1;
            var bestCos = minCos;
            for (var i = 0; i < n; i++)
            {
                if ((skipped >> i & 1UL) != 0 || found[i] == null) continue;
                var offset = ForgeCore.Enemies.AimPoint(found[i]!) - origin;
                var distance = Vec.Length(offset);
                if (distance < 0.5f || distance > reach) continue;
                var cos = Vec.Dot(offset, aim) / distance;
                if (cos < bestCos) continue;
                bestCos = cos;
                best = i;
            }
            if (best < 0) return null;
            skipped |= 1UL << best;
            var enemy = found[best]!;
            var toward = ForgeCore.Enemies.AimPoint(enemy) - origin;
            if (ForgeCore.Combat.ShooterRay(shooter, origin, toward.normalized, Vec.Length(toward) + 0.5f, out var hit) && hit.collider != null
                && ForgeCore.Combat.IsPartOf(hit.collider, enemy)) return enemy;
        }
        return null;
    }

}

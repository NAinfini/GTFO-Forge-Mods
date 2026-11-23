using System;
using System.Collections.Generic;
using Enemies;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The module half of `forge.trigger.enemy.projectile_hit_world` and `forge.trigger.enemy.projectile_expired`
/// (`EnemyProjectileWorldHooks.cs`). Both are facts about a projectile, not about an enemy life, so the shooter is an
/// optional port: the game's projectile does not remember who fired it (`pFireTargeting` carries no shooter), so this
/// module attributes a projectile to a shooter only when the projectile came to life inside that shooter's own
/// firing call (`EAB_ProjectileShooter.FireAtAgent` or `FireChaos`, the host-local calls that spawn one projectile each).
/// A projectile spawned anywhere else, or whose shooter life is gone, names no shooter. The projectile has no Forge
/// entity identity and the shooter is only a related role, so neither row declares a trigger subject: two hits by
/// one shooter stay two plays instead of one replaying and cutting off the other. Nothing here writes the world.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The tracked shooter whose firing call (`FireAtAgent` or `FireChaos`) is running right now, or null outside one.</summary>
    private EntityReference? _shooterFrame;

    /// <summary>Native projectile pointer to the shooter life that fired it. Entries are removed when the projectile is
    /// destroyed and the table is cleared with the world.</summary>
    private readonly Dictionary<IntPtr, EntityReference> _projectileShooters = new();

    internal bool ProjectileWorldObserved => _hitWorldGate.HasSubscribers || _expiredGate.HasSubscribers;
    internal bool ProjectileHitWorldObserved => _hitWorldGate.HasSubscribers;
    internal bool ProjectileOwnersTracked => _projectileShooters.Count != 0;
    internal bool ProjectileEndObserved => _expiredGate.HasSubscribers || ProjectileOwnersTracked;

    /// <summary>A shooter's firing call begins. Only a current tracked life on the host opens the frame.</summary>
    internal void EnterShooterFrame(EnemyAgent? shooter)
    {
        CheckThread();
        _shooterFrame = ActionEntry(shooter, _hitWorldGate.HasSubscribers ? _hitWorldGate : _expiredGate)?.Reference;
    }

    internal void LeaveShooterFrame() => _shooterFrame = null;

    /// <summary>`ProjectileTargeting.OnFire` ran: inside a shooter's frame the projectile belongs to that shooter, and a
    /// pooled pointer that an earlier life left behind is forgotten otherwise.</summary>
    internal void AttributeProjectile(ProjectileTargeting projectile)
    {
        CheckThread();
        var pointer = projectile.Pointer;
        if (_shooterFrame is { } shooter) _projectileShooters[pointer] = shooter;
        else if (_projectileShooters.Count != 0) _projectileShooters.Remove(pointer);
    }


    private void ClearProjectileOwners()
    {
        _shooterFrame = null;
        _projectileShooters.Clear();
    }

    /// <summary>The shooter a projectile was fired by, only while that exact life still stands: a shooter that died or
    /// was replaced since never stands in for the projectile's owner.</summary>
    private EntityReference? ShooterOf(IntPtr projectile)
    {
        if (!_projectileShooters.TryGetValue(projectile, out var shooter)) return null;
        if (Resolve(shooter) != null) return shooter;
        _projectileShooters.Remove(projectile);
        return null;
    }

    /// <summary>`ProjectileBase.Collision(Ray, RaycastHit)` ran for a projectile. The native body asks the hit collider for
    /// an `IDamageable`; a collider without one is the world (a wall, the floor), and a player is damageable, so a hit
    /// on a player is not published here.</summary>
    internal void AfterProjectileCollision(ProjectileBase projectile, RaycastHit hit)
    {
        CheckThread();
        if (!CanPublishNodeFacts(_hitWorldGate)) return;
        ProjectileTargeting? targeting;
        Vector3 point, normal;
        try
        {
            targeting = projectile.TryCast<ProjectileTargeting>();
            var collider = hit.collider;
            if (targeting == null || collider == null || collider.GetComponent<IDamageable>() != null) return;
            point = hit.point;
            normal = hit.normal;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return; }
        if (!IsFinitePoint(point) || !IsFinitePoint(normal)) return;
        var shooter = ShooterOf(targeting.Pointer);
        var fact = _hitWorldGate.Begin(_registration);
        fact.Values.Vector(Blocks.TriggerEnemyProjectileHitWorld.OutPosition, point.x, point.y, point.z);
        fact.Values.Vector(Blocks.TriggerEnemyProjectileHitWorld.OutNormal, normal.x, normal.y, normal.z);
        if (shooter != null) fact.Values.Entity(Blocks.TriggerEnemyProjectileHitWorld.OutShooter, shooter);
        RaiseNodeFact(fact);
    }

    /// <summary>`ProjectileTargeting.OnDestroy` ran: the projectile's life is over. Its own `Update` (RVA 0x13CF370) destroys
    /// it once `Clock.Time` is past `m_endLifeTime`, before it moves that frame, and the destroy lands at the end of the same
    /// frame, so a projectile whose end time has passed when it is destroyed ran out of life; one destroyed by a hit or by
    /// level cleanup still has time left, and one never fired has no end time. The shooter entry goes either way.</summary>
    internal void ProjectileEnded(ProjectileTargeting projectile)
    {
        CheckThread();
        var pointer = projectile.Pointer;
        if (CanPublishNodeFacts(_expiredGate))
        {
            Vector3 point;
            bool expired;
            try
            {
                var end = projectile.m_endLifeTime;
                expired = end > 0f && Clock.Time > end;
                point = expired ? projectile.transform.position : default;
            }
            catch (Exception caught) { EnemyLog.Contained(caught); expired = false; point = default; }
            if (expired && IsFinitePoint(point))
            {
                var shooter = ShooterOf(pointer);
                var fact = _expiredGate.Begin(_registration);
                fact.Values.Vector(Blocks.TriggerEnemyProjectileExpired.OutPosition, point.x, point.y, point.z);
                if (shooter != null) fact.Values.Entity(Blocks.TriggerEnemyProjectileExpired.OutShooter, shooter);
                RaiseNodeFact(fact);
            }
        }
        if (_projectileShooters.Count != 0) _projectileShooters.Remove(pointer);
    }
}

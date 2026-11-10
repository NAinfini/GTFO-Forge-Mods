using System;
using Enemies;
using ForgeWeaponEnergyLab.Presentation;
using Player;
using UnityEngine;
using UnityEngine.AI;
using NVector3 = System.Numerics.Vector3;

namespace ForgeWeaponEnergyLab.Native;

// Host simulation or presentation-only peer. Delayed damage never enters Fire.
internal sealed class SpecialWeapons : IDisposable
{
    private const int Capacity = 512;
    private sealed class Disc
    {
        internal PlayerAgent? Owner;
        internal EnergyGear Gear = null!;
        internal Vector3 Position, Direction;
        internal float Until;
        internal int Bounces, Dimension;
        internal readonly PlasmaContacts Contacts = new();
        /// <summary>The shot's number; when a machine that only draws the disc found its last stop (it then waits for the host's word
        /// until this time, standing there); whether its end is settled; its damage factor; when it was launched.</summary>
        internal uint Sequence;
        internal float Hold, Scale, Born;
        internal bool Confirmed;
    }
    private struct HoleTarget
    {
        internal EnemyAgent Enemy;
        internal Collider Collider;
        internal float Distance;
    }
    private readonly Disc[] _discs = { new(), new(), new() };
    private readonly ProjectileSweep _sweep = new(Capacity);
    private readonly ForgeCore.ColliderQuery _overlap = new(Capacity);
    private readonly RaycastHit[] _flameHits = new RaycastHit[Capacity];
    private readonly long[] _identities = new long[Capacity];
    private readonly HoleTarget[] _victims = new HoleTarget[Capacity];
    private const float PullInterval = 0.05f;
    private double _nextHolePulse;
    private float _pullTime;
    private readonly NativeShot _hit = new();
    private readonly bool _damageEnabled;
    private PlayerAgent? _flameOwner;
    private ShotTuning _flameTuning;
    private Vector3 _flameStart;
    private int _flameCount;
    private PlayerAgent? _holeOwner;
    private EnergyGear _holeGear = null!;
    private Vector3 _holePosition, _holeVelocity;
    private float _holeUntil, _holeBorn, _nextQuery, _holeHold, _holeScale = 1f;
    private uint _holeSequence;
    private bool _holeConfirmed;
    private int _victimCount, _holeDimension;
    private bool _field, _collapsing;
    internal SpecialEffects Visuals { get; }
    private readonly BlastShells _shells;

    internal SpecialWeapons(bool damageEnabled = true, SpecialEffects? preparedVisuals = null)
    {
        _damageEnabled = damageEnabled;
        Visuals = preparedVisuals ?? new SpecialEffects();
        _shells = new BlastShells(damageEnabled, Visuals);
    }

    internal bool CanLaunchShell => _shells.CanLaunch;

    internal void LaunchShell(PlayerAgent owner, EnergyGear gear, Vector3 position, Vector3 direction, float damage, float now, uint sequence)
    {
        if (!CanLaunchShell) throw new BudgetExhausted("Blast shell limit reached for " + gear.Definition.Id);
        _shells.Launch(owner, gear, position, direction, damage, now, sequence);
    }

    /// <summary>The host's word on where a projectile of shot <paramref name="sequence"/> ended (<see cref="FlightEnds"/>).</summary>
    internal void Finish(FlightKind kind, uint sequence, Vector3 at, float now)
    {
        switch (kind)
        {
            case FlightKind.Shell: _shells.Finish(sequence, at, now); break;
            case FlightKind.Disc:
                for (var index = 0; index < _discs.Length; index++)
                {
                    var disc = _discs[index];
                    if (disc.Owner is null || disc.Sequence != sequence) continue;
                    disc.Position = at; disc.Hold = 0f; disc.Confirmed = true;
                    EndDisc(index);
                    break;
                }
                break;
            case FlightKind.Hole:
                if (_holeOwner is not null && !_field && _holeSequence == sequence)
                {
                    _holePosition = at; _holeHold = 0f; _holeConfirmed = true;
                    StartField(now);
                }
                break;
        }
    }

    internal bool CanLaunchDisc(EnergyGear gear)
    {
        var active = 0; var free = false;
        foreach (var disc in _discs)
            if (disc.Owner is null) free = true;
            else if (disc.Gear.CategoryId == gear.CategoryId) active++;
        return free && active < gear.Numbers.ActiveProjectiles;
    }
    internal bool CanLaunchHole => _holeOwner is null;

    /// <summary>No disc or gravity projectile exists, so a tick has nothing to move.</summary>
    internal bool Idle
    {
        get
        {
            if (_holeOwner is not null || !_shells.Idle) return false;
            foreach (var disc in _discs) if (disc.Owner is not null) return false;
            return true;
        }
    }

    /// <summary><paramref name="admitted"/>: the shooter's own gate already let this shot go (its round is spent), so when no room is left
    /// the oldest disc is dropped instead of this shot. <paramref name="scale"/>: the factor on the gun's damage the shot's native hit carried.</summary>
    internal void LaunchDisc(PlayerAgent owner, EnergyGear gear, Vector3 position, Vector3 direction, float now, uint sequence, float scale, bool admitted = false)
    {
        if (admitted && !CanLaunchDisc(gear)) DropOldestDisc(gear);
        if (!CanLaunchDisc(gear)) throw new BudgetExhausted("Ricochet disc limit reached for " + gear.Definition.Id);
        for (var index = 0; index < _discs.Length; index++)
        {
            var disc = _discs[index];
            if (disc.Owner is not null) continue;
            disc.Owner = owner; disc.Gear = gear; disc.Dimension = (int)owner.DimensionIndex; disc.Position = position; disc.Direction = direction.normalized;
            disc.Sequence = sequence; disc.Scale = scale; disc.Born = now; disc.Hold = 0f; disc.Confirmed = false;
            disc.Bounces = 0; disc.Until = now + gear.Numbers.Lifetime; disc.Contacts.Reset();
            Visuals.Disc(index, position, disc.Direction, now, true);
            return;
        }
        throw new BudgetExhausted("All ricochet disc slots are occupied.");
    }

    // The oldest disc of this gun (any disc when none is this gun's) leaves, with no end of its own: its slot goes to the shot just admitted.
    private void DropOldestDisc(EnergyGear gear)
    {
        var oldest = -1; var sameGun = false;
        for (var index = 0; index < _discs.Length; index++)
        {
            var disc = _discs[index];
            if (disc.Owner is null) continue;
            var same = disc.Gear.CategoryId == gear.CategoryId;
            if (oldest < 0 || (same && !sameGun) || (same == sameGun && disc.Born < _discs[oldest].Born)) { oldest = index; sameGun = same; }
        }
        if (oldest >= 0) EndDisc(oldest);
    }

    /// <summary><paramref name="admitted"/>: the shooter's own gate already let this shot go (its round is spent), so a gravity projectile or
    /// field still standing is ended instead of this shot being dropped.</summary>
    internal void LaunchHole(PlayerAgent owner, EnergyGear gear, Vector3 position, Vector3 direction, float now, uint sequence, float scale, bool admitted = false)
    {
        if (admitted && !CanLaunchHole) EndHole();
        if (!CanLaunchHole) throw new BudgetExhausted("A gravity projectile or field is already active.");
        _holeSequence = sequence; _holeScale = scale; _holeHold = 0f; _holeConfirmed = false;
        _holeOwner = owner; _holeGear = gear; _holeDimension = (int)owner.DimensionIndex; _holePosition = position; _holeVelocity = direction.normalized * gear.Numbers.Speed;
        _holeUntil = now + gear.Numbers.FlightLifetime; _field = false; _collapsing = false;
        Visuals.HoleLaunch(position, direction);
        Visuals.HoleProjectile(position, gear.Numbers.BodyRadius);
    }

    internal void Tick(float delta, float now, Quaternion viewRotation)
    {
        if (EnergyNativeFire.InFireCall) return;
        Visuals.ViewRotation = viewRotation;
        var dt = Math.Clamp(delta, 0f, 0.05f);
        _shells.Tick(delta, now);
        for (var index = 0; index < _discs.Length; index++)
        {
            var disc = _discs[index];
            if (disc.Owner is null) continue;
            if (disc.Owner == null || !disc.Owner.Alive || (int)disc.Owner.DimensionIndex != disc.Dimension) { EndDisc(index); continue; }
            if (disc.Hold > 0f)
            {
                // Stopped here, waiting for the host's word on where it ended; none came in time.
                if (now >= disc.Hold) { disc.Hold = 0f; disc.Confirmed = true; EndDisc(index); }
                continue;
            }
            if (now >= disc.Until) { SettleDisc(index, now); continue; }
            // A query that outgrew its budget ends this disc only, never the gun.
            try { MoveDisc(index, disc.Gear.Numbers.Speed * dt, now); }
            catch (BudgetExhausted error) { Plugin.Logger.ErrorOnce("A special projectile was dropped: " + error); EndDisc(index); }
        }
        try { TickHole(dt, now); }
        catch (BudgetExhausted error) { Plugin.Logger.ErrorOnce("A special projectile was dropped: " + error); EndHole(); }
    }

    private void TickHole(float dt, float now)
    {
        if (_holeOwner is not null)
        {
            if (_holeOwner == null || !_holeOwner.Alive || (int)_holeOwner.DimensionIndex != _holeDimension) EndHole();
            else if (!_field)
            {
                // Stopped here, waiting for the host's word on where it came down; none came in time.
                if (_holeHold <= 0f) MoveHole(dt, now);
                else if (now >= _holeHold) { _holeHold = 0f; _holeConfirmed = true; StartField(now); }
            }
            else if (now >= _holeUntil) EndHole();
            else
            {
                var remaining = _holeUntil - now;
                if (remaining <= _holeGear.Numbers.CollapseDuration && !_collapsing) { _collapsing = true; Visuals.CollapseHole(); }
                // The collapse shrinks the field to its core, not to a point: the core stays visible for the collapse burst.
                var core = _holeGear.Numbers.CoreRadius / _holeGear.Numbers.Radius;
                var collapse = Math.Clamp(remaining / _holeGear.Numbers.CollapseDuration, 0f, 1f);
                var scale = MathF.Min(Math.Clamp((now - _holeBorn) / _holeGear.Numbers.GrowthDuration, 0f, 1f), core + (1f - core) * collapse);
                Visuals.Hole(_holePosition, scale, _holeGear.Numbers.Radius, false);
                if (!_collapsing)
                {
                    var damage = now + 1e-7 >= _nextHolePulse;
                    if (damage)
                    {
                        var interval = _holeGear.Numbers.Pulse.Interval;
                        _nextHolePulse = now - _nextHolePulse >= interval ? now + interval : _nextHolePulse + interval;
                    }
                    if (_damageEnabled && ForgeCore.Session.IsHost && (damage || now >= _nextQuery)) QueryHoleTargets(now);
                    if (_damageEnabled && ForgeCore.Session.IsHost)
                    {
                        if (damage) DamageHole();
                        // The pull is applied at a fixed rate with the time gathered since its last step.
                        _pullTime += dt;
                        if (_pullTime >= PullInterval) { Pull(_pullTime); _pullTime = 0f; }
                    }
                }
            }
        }
    }

    private void MoveDisc(int index, float distance, float now)
    {
        var disc = _discs[index];
        // A stalled frame never tunnels through an unchecked remainder after exhausting ricochets.
        for (var step = 0; step < 4 && distance > 0.001f; step++)
        {
            DiscOverlap(disc, now);
            // A door may close around a flying disc between frames. Do not escape through it.
            if (MuzzleBlocked(disc.Position, 0.02f)) { Visuals.Sparks(disc.Position, -disc.Direction, now); SettleDisc(index, now); return; }
            var count = _sweep.Cast(disc.Position, disc.Gear.Numbers.BodyRadius, disc.Direction, distance);
            var stop = _sweep.Nearest(count, disc.Owner, SweepPass.Agents);
            var limit = stop < 0 ? distance : _sweep[stop].distance;
            for (var row = 0; row < count; row++)
            {
                var collision = _sweep[row];
                if (collision.collider == null || collision.distance > limit) continue;
                if (collision.distance <= 0f) collision = NativeShot.OverlapContact(collision.collider, disc.Position, disc.Direction);
                DiscContact(disc, collision, now);
            }
            disc.Position += disc.Direction * MathF.Max(0f, limit - (stop >= 0 ? 0.01f : 0f));
            if (stop < 0) break;
            var hit = _sweep[stop];
            Visuals.Sparks(disc.Position, hit.normal, now);
            if (++disc.Bounces > disc.Gear.Numbers.Bounces) { SettleDisc(index, now); return; }
            disc.Direction = U(SpecialWeaponMath.Reflect(N(disc.Direction), N(hit.normal)));
            disc.Contacts.Reset();
            disc.Position += hit.normal * 0.015f;
            distance -= MathF.Max(0.025f, limit);
        }
        Visuals.Disc(index, disc.Position, disc.Direction, now);
    }

    private int Overlap(Vector3 point, float radius)
    {
        var count = _overlap.Sphere(point, radius, LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
        if (_overlap.Truncated) throw new BudgetExhausted("Special projectile overlap budget exhausted; truncated contacts refused.");
        return count;
    }

    private void DiscOverlap(Disc disc, float now)
    {
        var count = Overlap(disc.Position, disc.Gear.Numbers.BodyRadius);
        for (var row = 0; row < count; row++)
        {
            var collider = _overlap[row];
            // Only a receiver can be hit, so the level's own (possibly non-convex) colliders never reach ClosestPoint or the blocking query.
            if (collider == null || !ForgeCore.Combat.DirectReceiver(disc.Owner!, collider, out _) || Blocked(disc.Position, collider.ClosestPoint(disc.Position))) continue;
            DiscContact(disc, NativeShot.OverlapContact(collider, disc.Position, disc.Direction), now);
        }
    }

    private void DiscContact(Disc disc, RaycastHit collision, float now)
    {
        if (!_hit.ContactTarget(disc.Position, disc.Direction, collision, out collision)
            || !ForgeCore.Combat.DirectReceiver(disc.Owner!, collision.collider, out var identity)
            || !disc.Contacts.Remember(identity)) return;
        _hit.ResolveHit(disc.Owner!, disc.Gear.Numbers.Shot with { Damage = disc.Gear.Numbers.Shot.Damage * disc.Scale }, disc.Position, disc.Direction, collision);
        if (_damageEnabled) _hit.Apply();
        Visuals.DiscImpact(collision.point, disc.Direction, now);
    }


    internal float FlameRange(EnergyGear gear, Vector3 start, Vector3 direction)
        => MuzzleBlocked(start, 0.025f) ? 0f : Physics.Raycast(start, direction, out var hit, gear.Numbers.Shot.Range,
            LayerManager.MASK_EXPLOSION_BLOCKERS, QueryTriggerInteraction.UseGlobal)
                ? MathF.Max(0f, hit.distance) : gear.Numbers.Shot.Range;

    /// <summary>Finds every visible receiver in the cone for <see cref="DamageFlame"/>.</summary>
    internal void FlameTargets(PlayerAgent owner, EnergyGear gear, in ShotTuning tuning, Vector3 start, Vector3 direction, float range, float now)
    {
        _flameCount = 0;
        if (range <= 0f) return;
        var center = start + direction * (range * 0.5f);
        var radius = MathF.Sqrt(range * range * 0.25f + MathF.Pow(gear.Numbers.StartRadius + range * gear.Numbers.RadiusPerMetre, 2));
        var count = _overlap.Sphere(center, radius, LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
        if (_overlap.Truncated)
        {
            Plugin.Logger.ErrorOnce("A flame tick was dropped: its target budget is exhausted.");
            return;
        }
        var used = 0;
        for (var row = 0; row < count; row++)
        {
            var collider = _overlap[row];
            if (collider == null) continue;
            if (!ForgeCore.Combat.DirectReceiver(owner, collider, out var identity)) continue;
            if (_identities.AsSpan(0, used).Contains(identity)) continue;
            var point = collider.ClosestPoint(start + direction * Vector3.Dot(collider.bounds.center - start, direction));
            if (!SpecialWeaponMath.InFlame(N(start), N(direction), N(point), range, gear.Numbers)
                || !_hit.VisibleTarget(owner, start, point, collider, out var collision)) continue;
            _identities[used] = identity;
            _flameHits[used++] = collision;
        }
        _flameCount = used; _flameOwner = owner; _flameStart = start; _flameTuning = tuning;
        if (used > 0) Visuals.FlameImpact(_flameHits[0].point, now, wall: false);
        if (range < gear.Numbers.Shot.Range) Visuals.FlameImpact(start + direction * range, now, wall: true);
    }

    /// <summary>Deals the flame tick last found by <see cref="FlameTargets"/> to each of its receivers.</summary>
    internal void DamageFlame()
    {
        for (var row = 0; row < _flameCount; row++)
        {
            _hit.ResolveHit(_flameOwner!, _flameTuning, _flameStart,
                (_flameHits[row].point - _flameStart).normalized, _flameHits[row]);
            _hit.Apply();
        }
        _flameCount = 0;
    }

    private void MoveHole(float delta, float now)
    {
        var overlaps = Overlap(_holePosition, _holeGear.Numbers.BodyRadius);
        for (var row = 0; row < overlaps; row++)
            if (_overlap[row] != null && !PlayerCollider(_overlap[row])) { SettleHole(now); return; }
        var offset = _holeVelocity * delta;
        var direction = offset.normalized;
        var distance = offset.magnitude;
        var stop = _sweep.Nearest(_sweep.Cast(_holePosition, _holeGear.Numbers.BodyRadius, direction, distance), null, SweepPass.Players);
        if (stop >= 0)
        {
            _holePosition += direction * MathF.Max(0f, _sweep[stop].distance - 0.025f);
            SettleHole(now); return;
        }
        _holePosition += offset;
        _holeVelocity += Vector3.down * (_holeGear.Numbers.Gravity * delta);
        if (now >= _holeUntil) { SettleHole(now); return; }
        Visuals.HoleProjectile(_holePosition, _holeGear.Numbers.BodyRadius);
    }

    // The gravity projectile came down here. A machine that only draws it waits where it stopped: the host says where it came down.
    private void SettleHole(float now)
    {
        if (!_damageEnabled && !_holeConfirmed) { _holeHold = now + EnergyProtocol.FlightEndWaitSeconds; return; }
        if (_damageEnabled) FlightEnds.Announce(_holeOwner!, FlightKind.Hole, _holeSequence, _holePosition);
        StartField(now);
    }

    // The disc's last stop (its bounces or its time are spent, or a door closed on it); the same wait for the host's word.
    private void SettleDisc(int index, float now)
    {
        var disc = _discs[index];
        if (!_damageEnabled && !disc.Confirmed) { disc.Hold = now + EnergyProtocol.FlightEndWaitSeconds; return; }
        if (_damageEnabled) FlightEnds.Announce(disc.Owner!, FlightKind.Disc, disc.Sequence, disc.Position);
        EndDisc(index);
    }

    private void StartField(float now)
    {
        _field = true; _holeBorn = now; _holeUntil = now + _holeGear.Numbers.Lifetime; _nextQuery = now;
        _nextHolePulse = double.NegativeInfinity;
        _pullTime = 0f;
        // A well that came down stands its core clear of the floor. The field itself moves there, so what pulls and what shows stay one point on every machine.
        var core = _holeGear.Numbers.CoreRadius;
        if (ForgeCore.Level.FloorBelow(_holePosition, core, out var floor) &&
            !Physics.Raycast(floor.point, Vector3.up, out _, core, LayerManager.MASK_EXPLOSION_BLOCKERS, QueryTriggerInteraction.Ignore))
            _holePosition.y = floor.point.y + core;
        // The well opens from nothing and grows (TickHole), so its size only ever rises and then falls.
        Visuals.Hole(_holePosition, 0f, _holeGear.Numbers.Radius, true);
    }

    private void QueryHoleTargets(float now)
    {
        _nextQuery = now + _holeGear.Numbers.QueryInterval;
        Array.Clear(_victims, 0, _victimCount); _victimCount = 0;
        var count = _overlap.Sphere(_holePosition, _holeGear.Numbers.Radius, LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
        if (_overlap.Truncated) throw new BudgetExhausted("Gravity target budget exhausted; truncated damage/pull refused.");
        for (var row = 0; row < count; row++)
        {
            var collider = _overlap[row];
            var enemy = collider == null ? null : VisibleEnemies.SelectEnemy(_holeOwner!, collider, TargetRelationFilter.Enemies);
            if (enemy == null) continue;
            var point = collider!.ClosestPoint(_holePosition);
            var distance = (point - _holePosition).magnitude;
            if (SpecialWeaponMath.HoleDamageAt(distance, _holeGear.Numbers) <= 0f || Blocked(_holePosition, point)) continue;
            var id = enemy.Pointer.ToInt64();
            var existing = _identities.AsSpan(0, _victimCount).IndexOf(id);
            if (existing >= 0)
            {
                if (distance < _victims[existing].Distance)
                    _victims[existing] = new() { Enemy = enemy, Collider = collider, Distance = distance };
                continue;
            }
            _identities[_victimCount] = id;
            _victims[_victimCount++] = new() { Enemy = enemy, Collider = collider, Distance = distance };
        }
    }

    private void DamageHole()
    {
        for (var row = 0; row < _victimCount; row++)
        {
            var target = _victims[row];
            if (target.Enemy == null || !target.Enemy.Alive || target.Collider == null
                || target.Enemy.DimensionIndex != _holeOwner!.DimensionIndex) continue;
            if (!_hit.RayTarget(_holePosition, target.Collider.ClosestPoint(_holePosition), target.Collider, out var collision)
                || Blocked(_holePosition, collision.point)) continue;
            var direction = (collision.point - _holePosition).normalized;
            var tuning = _holeGear.Numbers.Pulse with { Damage = SpecialWeaponMath.HoleDamageAt(target.Distance, _holeGear.Numbers) * _holeScale };
            _hit.ResolveHit(_holeOwner!, tuning, _holePosition, direction, collision);
            _hit.Apply();
        }
    }

    private void Pull(float delta)
    {
        for (var row = 0; row < _victimCount; row++)
        {
            var enemy = _victims[row].Enemy;
            if (enemy == null || !enemy.Alive || enemy.DimensionIndex != _holeOwner!.DimensionIndex) continue;
            var ai = enemy.AI;
            var navigation = ai?.m_navMeshAgent;
            // Enemies without active navigation still take damage; they are never moved by transform alone.
            if (navigation == null || !navigation.enabled || !navigation.isOnNavMesh) continue;
            var position = enemy.Position;
            if ((_holePosition - position).sqrMagnitude > _holeGear.Numbers.Radius * _holeGear.Numbers.Radius) continue;
            var goal = _holePosition;
            var ground = navigation.TryCast<NavMeshAgentExtention.NavMeshAgentProxy>()?.m_agent;
            if (ground != null) goal.y = position.y;
            var step = U(SpecialWeaponMath.PullStep(N(position), N(goal), delta, _holeGear.Numbers));
            if (step.sqrMagnitude < 0.000001f) continue;
            var next = position + step;
            if (!navigation.IsPositionOnGraph(next) || navigation.Raycast(next, out _)) continue;
            var bodyRadius = ground == null ? 0.25f : MathF.Max(0.15f, ground.radius);
            var center = position + Vector3.up * (ground == null ? 0f : MathF.Max(bodyRadius, ground.height * 0.5f));
            if (Blocked(_holePosition, center) || Physics.SphereCast(center, bodyRadius, step.normalized, out _,
                step.magnitude + 0.025f, LayerManager.MASK_EXPLOSION_BLOCKERS, QueryTriggerInteraction.Ignore)) continue;
            ai!.NavmeshAgentWarp(next);
            // When the game does not accept the warp, that enemy stays where it is this frame; it is never moved by transform alone.
            if ((navigation.nextPosition - next).sqrMagnitude > 0.04f) continue;
            enemy.Position = next;
        }
    }

    private static bool Blocked(Vector3 origin, Vector3 target)
    {
        if (MuzzleBlocked(origin, 0.005f)) return true;
        var delta = target - origin;
        return delta.sqrMagnitude > 0.0025f && Physics.Raycast(origin, delta.normalized, out _,
            delta.magnitude - 0.025f, LayerManager.MASK_EXPLOSION_BLOCKERS, QueryTriggerInteraction.Ignore);
    }

    internal static bool MuzzleBlocked(Vector3 position, float radius)
        => Physics.CheckSphere(position, radius, LayerManager.MASK_EXPLOSION_BLOCKERS, QueryTriggerInteraction.Ignore);

    private static bool PlayerCollider(Collider collider)
        => ColliderKinds.Of(collider) == ColliderKind.Player;
    private static NVector3 N(Vector3 v) => new(v.x, v.y, v.z);
    private static Vector3 U(NVector3 v) => new(v.X, v.Y, v.Z);

    private void EndDisc(int index) { _discs[index].Owner = null; Visuals.HideDisc(index); }
    private void EndHole()
    {
        _holeOwner = null; _field = false; _holeHold = 0f; _pullTime = 0f; Array.Clear(_victims, 0, _victimCount); _victimCount = 0; Visuals.HideHole();
    }
    internal void Clear()
    {
        for (var index = 0; index < _discs.Length; index++) EndDisc(index);
        _shells.Clear();
        _flameCount = 0; _flameOwner = null;
        EndHole(); Visuals.Clear();
    }
    public void Dispose()
    {
        foreach (var disc in _discs) disc.Owner = null;
        _shells.Clear();
        _flameCount = 0; _flameOwner = null;
        _holeOwner = null; _field = false;
        Array.Clear(_victims, 0, _victimCount); _victimCount = 0;
        // Disposal must not replay live-world Stop/Hide calls after Unity destroys the scene.
        Visuals.Dispose();
    }
}

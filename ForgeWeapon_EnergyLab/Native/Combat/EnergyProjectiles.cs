using System;
using Enemies;
using ForgeWeaponEnergyLabExperimental.Presentation;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

// Bounded projectile state and native damage. Presentation never queries or damages the world.
// The host deals every flight's damage outside Fire; other peers simulate presentation only.
internal sealed class EnergyProjectiles : IDisposable
{
    internal const int Capacity = 4;
    private const int QueryCapacity = 512;
    private enum Phase { Free, Launched, Flying, Fading, Exploding }
    private sealed class Flight
    {
        internal Phase Phase;
        internal EnergyMode Mode;
        internal EnergyGear Gear = null!;
        internal int Dimension;
        internal PlayerAgent? Owner;
        internal Vector3 Position;
        internal Vector3 Direction;
        internal PlasmaPayload Payload;
        internal float Until;
        internal readonly PlasmaContacts Contacts = new();
        internal double NextPulse;
    }

    private readonly Flight[] _flights = new Flight[Capacity];
    private readonly Il2CppStructArray<RaycastHit> _sweep = new(QueryCapacity);
    private readonly Il2CppReferenceArray<Collider> _overlap = new(QueryCapacity);
    private readonly VisibleEnemies _enemies = new();
    private readonly EnergyExplosion _explosion = new();
    private readonly NativeShot _contact = new();
    private readonly NativeShot[] _pulseShots = new NativeShot[EnergyLimits.PulseTargets];
    private readonly Vector3[] _pulseEnds = new Vector3[EnergyLimits.PulseTargets];
    private readonly long[] _pulseTargets = new long[EnergyLimits.PulseTargets];
    private readonly bool _damageEnabled;
    internal PlasmaEffects Visuals { get; }

    internal EnergyProjectiles(bool damageEnabled = true, PlasmaEffects? preparedVisuals = null)
    {
        _damageEnabled = damageEnabled;
        Visuals = preparedVisuals ?? new PlasmaEffects(Capacity + 1);
        for (var index = 0; index < Capacity; index++) _flights[index] = new Flight();
        for (var index = 0; index < _pulseShots.Length; index++) _pulseShots[index] = new NativeShot();
    }

    internal bool CanLaunch(EnergyGear gear)
    {
        var active = 0; var free = false;
        foreach (var flight in _flights)
            if (flight.Phase == Phase.Free) free = true;
            else if (flight.Gear.CategoryId == gear.CategoryId) active++;
        return free && active < gear.Numbers.ActiveProjectiles;
    }

    internal void Launch(PlayerAgent owner, Vector3 position, Vector3 direction, float power, EnergyGear gear, float now)
    {
        var mode = gear.Mode;
        if (!CanLaunch(gear)) throw new InvalidOperationException("Energy projectile limit reached for " + gear.Definition.Id);
        if (mode is not (EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (direction.sqrMagnitude < 0.0001f) throw new ArgumentException("Plasma launch direction is empty.");
        for (var index = 0; index < Capacity; index++)
        {
            var flight = _flights[index];
            if (flight.Phase != Phase.Free) continue;
            flight.Owner = owner;
            flight.Dimension = (int)owner.DimensionIndex;
            flight.Mode = mode; flight.Gear = gear;
            flight.Position = position;
            flight.Direction = direction.normalized;
            flight.Payload = PlasmaPayload.FromCharge(power, gear.Numbers);
            flight.Until = now + flight.Payload.Lifetime;
            flight.Phase = Phase.Launched;
            flight.Contacts.Reset();
            flight.NextPulse = double.NegativeInfinity;
            Visuals.Orb(index, position, flight.Payload.BodyRadius, now, mode == EnergyMode.PlasmaArc, flying: true);
            return;
        }
        throw new InvalidOperationException("All energy projectile slots are occupied.");
    }

    internal void Tick(float delta, float now)
    {
        if (EnergyNativeFire.InFireCall) return;
        for (var index = 0; index < Capacity; index++)
        {
            var flight = _flights[index];
            if (flight.Phase == Phase.Free) continue;
            if (flight.Owner == null || !flight.Owner.Alive || (int)flight.Owner.DimensionIndex != flight.Dimension) { Clear(index); continue; }
            if (flight.Phase == Phase.Launched)
            {
                SpawnContacts(index, now);
                if (flight.Phase != Phase.Flying) continue;
            }
            if (flight.Phase is Phase.Exploding or Phase.Fading)
            {
                var lifetime = flight.Phase == Phase.Exploding ? ExplosionLifetime(flight) : flight.Gear.Numbers.FadeLifetime;
                var progress = 1f - (flight.Until - now) / lifetime;
                if (progress >= 1f) { Clear(index); continue; }
                if (flight.Phase == Phase.Fading)
                    Visuals.Dissipate(index, flight.Position, flight.Payload.BodyRadius, progress, now);
                continue;
            }
            var remainingTime = MathF.Max(0f, flight.Until - (now - delta));
            var distance = flight.Payload.Speed * Math.Clamp(delta, 0f, remainingTime);
            var count = Physics.SphereCastNonAlloc(flight.Position, flight.Payload.BodyRadius, flight.Direction,
                _sweep, distance, LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
            if (count == _sweep.Length) throw new InvalidOperationException("Plasma sweep budget exhausted; no truncated collision result used.");
            var stop = -1;
            for (var hit = 0; hit < count; hit++)
            {
                var collider = _sweep[hit].collider;
                if (collider == null || VanillaDamage.IsShooter(collider, flight.Owner)
                    || (flight.Mode == EnergyMode.PlasmaArc && IsActor(collider))) continue;
                if (stop < 0 || _sweep[hit].distance < _sweep[stop].distance) stop = hit;
            }
            if (flight.Mode == EnergyMode.PlasmaArc)
            {
                var stopDistance = stop < 0 ? float.PositiveInfinity : _sweep[stop].distance;
                for (var hit = 0; hit < count; hit++)
                {
                    var collision = _sweep[hit];
                    if (collision.collider == null || collision.distance > stopDistance) continue;
                    // A sphere sweep that starts inside a receiver can report a zero-distance placeholder point.
                    if (collision.distance <= 0f) collision.point = collision.collider.ClosestPoint(flight.Position);
                    Contact(flight, collision);
                }
            }
            if (stop >= 0)
            {
                flight.Position += flight.Direction * MathF.Max(0f, _sweep[stop].distance - 0.01f);
                EndFlight(index, now);
                continue;
            }
            flight.Position += flight.Direction * distance;
            if (now >= flight.Until) { EndFlight(index, now); continue; }
            Visuals.Orb(index, flight.Position, flight.Payload.BodyRadius, now, flight.Mode == EnergyMode.PlasmaArc, flying: true);
            if (flight.Mode == EnergyMode.PlasmaArc) Pulse(index, now);
        }
    }

    // Even a projectile launched inside a receiver waits until Fire has returned before contact or explosion damage.
    private void SpawnContacts(int index, float now)
    {
        var flight = _flights[index];
        flight.Phase = Phase.Flying;
        var count = Physics.OverlapSphereNonAlloc(flight.Position, flight.Payload.BodyRadius, _overlap,
            LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
        if (count == QueryCapacity) throw new InvalidOperationException("Plasma spawn overlap budget exhausted.");
        for (var hit = 0; hit < count; hit++)
        {
            var collider = _overlap[hit];
            if (collider == null || VanillaDamage.IsShooter(collider, flight.Owner!) ||
                flight.Mode == EnergyMode.PlasmaArc && IsActor(collider)) continue;
            if (flight.Mode == EnergyMode.PlasmaArc)
                Contact(flight, NativeShot.OverlapContact(collider, flight.Position, flight.Direction));
            EndFlight(index, now);
            return;
        }
        if (flight.Mode != EnergyMode.PlasmaArc) return;
        for (var hit = 0; hit < count; hit++)
            if (_overlap[hit] != null) Contact(flight, NativeShot.OverlapContact(_overlap[hit], flight.Position, flight.Direction));
        Pulse(index, now);
    }

    private void Contact(Flight flight, RaycastHit hit)
    {
        if (!_contact.ContactTarget(flight.Position, flight.Direction, hit, out hit)
            || !VanillaDamage.DirectReceiver(flight.Owner!, hit.collider, out var identity)
            || !flight.Contacts.Remember(identity)) return;
        var tuning = flight.Gear.Numbers.Shot with { Damage = flight.Payload.Damage };
        _contact.ResolveHit(flight.Owner!, tuning, flight.Position, flight.Direction, hit);
        if (_damageEnabled) _contact.Apply();
        Visuals.Contact(hit.point);
    }

    private void Pulse(int index, float now)
    {
        var flight = _flights[index];
        if (now + 1e-7 < flight.NextPulse) return;
        var interval = flight.Gear.Numbers.Pulse.Interval;
        flight.NextPulse = now - flight.NextPulse >= interval ? now + interval : flight.NextPulse + interval;
        var count = 0;
        var tuning = flight.Gear.Numbers.Pulse with { Range = flight.Payload.ArcRadius, Damage = flight.Payload.ArcDamage };
        while (count < flight.Gear.Numbers.PulseTargets && _enemies.Nearest(flight.Owner!, TargetRelationFilter.Enemies,
            hitInterveningReceiver: false, flight.Position, tuning.Range,
            _pulseTargets.AsSpan(0, count), flight.Owner!.Pointer.ToInt64(), out var hit, out var identity))
        {
            _pulseShots[count].ResolveHit(flight.Owner, tuning, flight.Position,
                (hit.point - flight.Position).normalized, hit);
            _pulseTargets[count] = identity;
            _pulseEnds[count++] = hit.point;
        }
        // Resolve every recipient first. Each pulse may hit an enemy once; a later pulse may hit it again.
        if (count == 0) return;
        if (_damageEnabled)
            for (var shot = 0; shot < count; shot++) _pulseShots[shot].Apply();
        Visuals.Pulse(index, _pulseEnds.AsSpan(0, count), now);
    }

    private void EndFlight(int index, float now)
    {
        var flight = _flights[index];
        if (flight.Mode == EnergyMode.PlasmaBlast) { Explode(index, now); return; }
        flight.Phase = Phase.Fading;
        flight.Until = now + flight.Gear.Numbers.FadeLifetime;
        Visuals.Fade(index, flight.Position);
        Visuals.Dissipate(index, flight.Position, flight.Payload.BodyRadius, 0f, now);
    }

    private static float BlastRadius(Flight flight) => flight.Payload.BlastRadius;
    private static float ExplosionLifetime(Flight flight) => flight.Gear.Numbers.ExplosionLifetime;

    private void Explode(int index, float now)
    {
        var flight = _flights[index];
        // Claim the terminal transition before native writes; partial damage is never retried.
        flight.Phase = Phase.Exploding;
        flight.Until = now + ExplosionLifetime(flight);
        if (_damageEnabled)
        {
            _explosion.Apply(flight.Owner!, flight.Gear, flight.Position, BlastRadius(flight), flight.Payload.Damage);
            WeaponNoise.Explosion(flight.Owner!, flight.Gear, flight.Position);
        }
        Visuals.Explosion(index, flight.Position, BlastRadius(flight), now);
        Visuals.Boom(flight.Position);
    }

    private static bool IsActor(Collider collider)
    {
        var agent = collider.GetComponentInParent<IDamageable>()?.GetBaseAgent();
        return agent?.TryCast<EnemyAgent>() != null || agent?.TryCast<PlayerAgent>() != null
            || collider.GetComponentInParent<PlayerAgent>() != null;
    }

    private void Clear(int index)
    {
        var flight = _flights[index];
        flight.Phase = Phase.Free;
        flight.Owner = null;
        Visuals.Hide(index);
    }

    internal void Clear()
    {
        for (var index = 0; index < Capacity; index++) Clear(index);
        Visuals.Hide(Capacity);
    }

    public void Dispose()
    {
        foreach (var flight in _flights) { flight.Phase = Phase.Free; flight.Owner = null; }
        Visuals.Dispose();
    }
}

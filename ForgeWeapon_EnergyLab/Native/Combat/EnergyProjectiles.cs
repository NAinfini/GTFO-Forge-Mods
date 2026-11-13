using System;
using ForgeWeaponEnergyLab.Presentation;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

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
        /// <summary>The shot's number, when a machine that only draws it stopped (it then waits for the host's word until this time),
        /// whether its end is settled, and when it was launched.</summary>
        internal uint Sequence;
        internal float Hold, Born;
        internal bool Confirmed;
    }

    private readonly Flight[] _flights = new Flight[Capacity];
    private readonly ProjectileSweep _sweep = new(QueryCapacity);
    private readonly ForgeCore.ColliderQuery _overlap = new(QueryCapacity);
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

    /// <summary>Launches a flight. <paramref name="admitted"/>: the shooter's own gate already let this shot go (its round is spent), so
    /// when no room is left the oldest flight of the same gun is dropped instead of this shot. <paramref name="scale"/>: the factor on the
    /// gun's damage the shot's native hit carried.</summary>
    internal void Launch(PlayerAgent owner, Vector3 position, Vector3 direction, float power, EnergyGear gear, float now, uint sequence, float scale, bool admitted = false)
    {
        var mode = gear.Mode;
        if (admitted && !CanLaunch(gear)) DropOldest(gear);
        if (!CanLaunch(gear)) throw new BudgetExhausted("Energy projectile limit reached for " + gear.Definition.Id);
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
            var payload = PlasmaPayload.FromCharge(power, gear.Numbers);
            flight.Payload = payload with { Damage = payload.Damage * scale, ArcDamage = payload.ArcDamage * scale };
            flight.Until = now + flight.Payload.Lifetime;
            flight.Sequence = sequence; flight.Born = now; flight.Hold = 0f; flight.Confirmed = false;
            flight.Phase = Phase.Launched;
            flight.Contacts.Reset();
            flight.NextPulse = double.NegativeInfinity;
            Visuals.Orb(index, position, flight.Payload.BodyRadius, mode == EnergyMode.PlasmaArc, flying: true);
            return;
        }
        throw new BudgetExhausted("All energy projectile slots are occupied.");
    }

    // The oldest flight of this gun that has not finished leaves, with no burst: its slot goes to the shot just admitted.
    private void DropOldest(EnergyGear gear)
    {
        var oldest = -1;
        for (var index = 0; index < Capacity; index++)
            if (_flights[index].Phase != Phase.Free && _flights[index].Gear.CategoryId == gear.CategoryId && (oldest < 0 || _flights[index].Born < _flights[oldest].Born)) oldest = index;
        if (oldest >= 0) Clear(oldest);
    }

    /// <summary>The host's word: shot <paramref name="sequence"/> ended at <paramref name="at"/>. The flight, if it is still flying here, ends there.</summary>
    internal void Finish(uint sequence, Vector3 at, float now)
    {
        for (var index = 0; index < Capacity; index++)
        {
            var flight = _flights[index];
            if (flight.Phase is not (Phase.Launched or Phase.Flying) || flight.Sequence != sequence) continue;
            flight.Phase = Phase.Flying; flight.Position = at; flight.Hold = 0f; flight.Confirmed = true;
            EndFlight(index, now);
            return;
        }
    }

    internal void Tick(float delta, float now)
    {
        if (EnergyNativeFire.InFireCall) return;
        for (var index = 0; index < Capacity; index++)
        {
            if (_flights[index].Phase == Phase.Free) continue;
            // A query that outgrew its budget drops this projectile only, never the gun.
            try { Advance(index, delta, now); }
            catch (BudgetExhausted error)
            {
                Plugin.Logger.ErrorOnce("A plasma projectile was dropped: " + error);
                Clear(index);
            }
        }
    }

    private void Advance(int index, float delta, float now)
    {
        var flight = _flights[index];
        if (flight.Owner == null || !flight.Owner.Alive || (int)flight.Owner.DimensionIndex != flight.Dimension) { Clear(index); return; }
        if (flight.Phase == Phase.Launched)
        {
            SpawnContacts(index, now);
            if (flight.Phase != Phase.Flying) return;
        }
        if (flight.Hold > 0f)
        {
            // Stopped here, waiting for the host's word on where it ended; none came in time.
            if (now < flight.Hold) return;
            flight.Hold = 0f; flight.Confirmed = true;
            EndFlight(index, now);
            return;
        }
        if (flight.Phase is Phase.Exploding or Phase.Fading)
        {
            var lifetime = flight.Phase == Phase.Exploding ? flight.Gear.Numbers.ExplosionLifetime : flight.Gear.Numbers.FadeLifetime;
            var progress = 1f - (flight.Until - now) / lifetime;
            if (progress >= 1f) { Clear(index); return; }
            if (flight.Phase == Phase.Fading)
                Visuals.Dissipate(index, flight.Position, flight.Payload.BodyRadius, progress);
            return;
        }
        var remainingTime = MathF.Max(0f, flight.Until - (now - delta));
        var distance = flight.Payload.Speed * Math.Clamp(delta, 0f, remainingTime);
        var count = _sweep.Cast(flight.Position, flight.Payload.BodyRadius, flight.Direction, distance);
        var stop = _sweep.Nearest(count, flight.Owner, flight.Mode == EnergyMode.PlasmaArc ? SweepPass.Actors : SweepPass.Owner);
        if (flight.Mode == EnergyMode.PlasmaArc)
        {
            var stopDistance = stop < 0 ? float.PositiveInfinity : _sweep[stop].distance;
            for (var hit = 0; hit < count; hit++)
            {
                var collision = _sweep[hit];
                if (collision.collider == null || collision.distance > stopDistance) continue;
                Contact(flight, collision);
            }
        }
        if (stop >= 0)
        {
            flight.Position += flight.Direction * MathF.Max(0f, _sweep[stop].distance - 0.01f);
            EndFlight(index, now);
            return;
        }
        flight.Position += flight.Direction * distance;
        if (now >= flight.Until) { EndFlight(index, now); return; }
        Visuals.Orb(index, flight.Position, flight.Payload.BodyRadius, flight.Mode == EnergyMode.PlasmaArc, flying: true);
        if (flight.Mode == EnergyMode.PlasmaArc) Pulse(index, now);
    }
    // Even a projectile launched inside a receiver waits until Fire has returned before contact or explosion damage.
    private void SpawnContacts(int index, float now)
    {
        var flight = _flights[index];
        flight.Phase = Phase.Flying;
        var count = _overlap.Sphere(flight.Position, flight.Payload.BodyRadius, LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
        if (_overlap.Truncated) throw new BudgetExhausted("Plasma spawn overlap budget exhausted.");
        for (var hit = 0; hit < count; hit++)
        {
            var collider = _overlap[hit];
            if (collider == null || ForgeCore.Combat.IsPartOf(collider, flight.Owner!) ||
                flight.Mode == EnergyMode.PlasmaArc && ColliderKinds.IsActor(collider)) continue;
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
            || !ForgeCore.Combat.DirectReceiver(flight.Owner!, hit.collider, out var identity)
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
        while (count < flight.Gear.Numbers.PulseTargets && _enemies.Nearest(flight.Owner!,
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
        Visuals.Pulse(index, _pulseEnds.AsSpan(0, count));
    }

    private void EndFlight(int index, float now)
    {
        var flight = _flights[index];
        // A machine that only draws the flight waits where it stopped: the host says where it ended.
        if (!_damageEnabled && !flight.Confirmed) { flight.Hold = now + EnergyProtocol.FlightEndWaitSeconds; return; }
        if (_damageEnabled) FlightEnds.Announce(flight.Owner!, FlightKind.Plasma, flight.Sequence, flight.Position);
        if (flight.Mode == EnergyMode.PlasmaBlast) { Explode(index, now); return; }
        flight.Phase = Phase.Fading;
        flight.Until = now + flight.Gear.Numbers.FadeLifetime;
        Visuals.Fade(index, flight.Position);
        Visuals.Dissipate(index, flight.Position, flight.Payload.BodyRadius, 0f);
    }

    private void Explode(int index, float now)
    {
        var flight = _flights[index];
        // Claim the terminal transition before native writes; partial damage is never retried.
        flight.Phase = Phase.Exploding;
        flight.Until = now + flight.Gear.Numbers.ExplosionLifetime;
        if (_damageEnabled)
        {
            _explosion.Apply(flight.Owner!, flight.Gear, flight.Position, flight.Payload.BlastRadius, flight.Payload.Damage);
            // The game's world noise of an explosion, as the blast shell makes it (host only).
            ForgeCore.Combat.Noise(flight.Position, flight.Gear.Numbers.NoiseRadius, (int)flight.Owner!.DimensionIndex);
        }
        Visuals.Explosion(index, flight.Position, flight.Payload.BlastRadius);
        Visuals.Boom(flight.Position);
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

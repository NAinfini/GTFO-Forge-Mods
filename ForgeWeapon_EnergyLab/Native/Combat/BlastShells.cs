using System;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

// The Blast Gun's shells. A shell leaves the muzzle along the aim, moves by its speed every frame and explodes at the first thing its
// sweep touches (terrain, enemy or player); the host deals the explosion, every other peer only draws it. Nothing here allocates per frame.
internal sealed class BlastShells
{
    internal const int Capacity = 64;
    private const int QueryCapacity = 128;
    /// <summary>The shell's collision radius in metres.</summary>
    private const float BodyRadius = 0.05f;
    private sealed class Shell
    {
        internal PlayerAgent? Owner;
        internal EnergyGear Gear = null!;
        internal Vector3 Position, Direction;
        internal float Until, Damage;
        internal int Dimension;
        internal bool Fresh;
        /// <summary>The shot's number (the host's word on where it burst names it), and: when a machine that only draws it stopped (it then
        /// waits for the host until this time), and whether the burst place is settled.</summary>
        internal uint Sequence;
        internal float Hold;
        internal bool Confirmed;
    }

    private readonly Shell[] _shells = new Shell[Capacity];
    private readonly ProjectileSweep _sweep = new(QueryCapacity);
    private readonly ForgeCore.ColliderQuery _overlap = new(QueryCapacity);
    private readonly EnergyExplosion _explosion = new();
    private readonly NativeShot _contact = new();
    private readonly Presentation.SpecialEffects _visuals;
    private readonly bool _damageEnabled;
    private int _active;

    internal BlastShells(bool damageEnabled, Presentation.SpecialEffects visuals)
    {
        _damageEnabled = damageEnabled;
        _visuals = visuals;
        for (var index = 0; index < Capacity; index++) _shells[index] = new Shell();
    }

    internal bool CanLaunch => _active < Capacity;
    internal bool Idle => _active == 0;

    internal void Launch(PlayerAgent owner, EnergyGear gear, Vector3 position, Vector3 direction, float damage, float now, uint sequence)
    {
        if (direction.sqrMagnitude < 0.0001f) throw new ArgumentException("Blast shell direction is empty.");
        for (var index = 0; index < Capacity; index++)
        {
            var shell = _shells[index];
            if (shell.Owner is not null) continue;
            shell.Owner = owner; shell.Gear = gear; shell.Dimension = (int)owner.DimensionIndex;
            shell.Position = position; shell.Direction = direction.normalized;
            // The shell never ends by distance: it may fly as far as a shot without a tuned range reaches.
            shell.Until = now + EnergyLimits.Reach / gear.Numbers.Speed;
            shell.Damage = damage;
            shell.Fresh = true;
            shell.Sequence = sequence; shell.Hold = 0f; shell.Confirmed = false;
            _active++;
            _visuals.Shell(index, position, now);
            return;
        }
        throw new BudgetExhausted("All blast shell slots are occupied.");
    }

    internal void Tick(float delta, float now)
    {
        if (_active == 0 || EnergyNativeFire.InFireCall) return;
        for (var index = 0; index < Capacity; index++)
        {
            var shell = _shells[index];
            // A slot is in use while it holds an owner object: a shooter Unity destroyed still reads == null, so slots are tested by
            // reference and Advance ends that shell.
            if (shell.Owner is null) continue;
            // A query that outgrew its budget drops this shell only, never the gun.
            try { Advance(index, shell, delta, now); }
            catch (BudgetExhausted error)
            {
                Plugin.Logger.ErrorOnce("energylab-blast-shell-dropped", "A blast shell was dropped: " + error);
                End(index);
            }
        }
    }

    private void Advance(int index, Shell shell, float delta, float now)
    {
        var owner = shell.Owner!;
        if (owner == null || owner.WasCollected || !owner.Alive || (int)owner.DimensionIndex != shell.Dimension || now >= shell.Until) { End(index); return; }
        if (shell.Hold > 0f)
        {
            // Stopped here, waiting for the host's word on where it burst; none came in time.
            if (now < shell.Hold) return;
            shell.Hold = 0f; shell.Confirmed = true;
            Explode(index, shell, now, default);
            return;
        }
        // Even a shell launched inside a receiver waits until Fire has returned before it explodes.
        if (shell.Fresh)
        {
            shell.Fresh = false;
            var count = _overlap.Sphere(shell.Position, BodyRadius, LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
            if (_overlap.Truncated) throw new BudgetExhausted("Blast shell spawn overlap budget exhausted.");
            for (var row = 0; row < count; row++)
            {
                var collider = _overlap[row];
                if (collider == null || ForgeCore.Combat.IsPartOf(collider, owner)) continue;
                Explode(index, shell, now, NativeShot.OverlapContact(collider, shell.Position, shell.Direction));
                return;
            }
        }
        var distance = shell.Gear.Numbers.Speed * delta;
        var stop = _sweep.Nearest(_sweep.Cast(shell.Position, BodyRadius, shell.Direction, distance), owner);

        if (stop < 0)
        {
            shell.Position += shell.Direction * distance;
            _visuals.Shell(index, shell.Position, now);
            return;
        }
        var hit = _sweep[stop];
        shell.Position += shell.Direction * MathF.Max(0f, hit.distance - 0.01f);
        Explode(index, shell, now, hit);
    }

    private void Explode(int index, Shell shell, float now, RaycastHit hit)
    {
        // A machine that only draws the shell waits where it stopped: the host says where the shell burst.
        if (!_damageEnabled && !shell.Confirmed) { shell.Hold = now + EnergyProtocol.FlightEndWaitSeconds; return; }
        var owner = shell.Owner!;
        var gear = shell.Gear;
        var position = shell.Position;
        // Its trail runs on to the point it burst at, then the shell's end is claimed before any native write; a partial explosion is never retried.
        _visuals.Shell(index, position, now);
        End(index);
        _visuals.GrenadeExplosion(position);
        if (!_damageEnabled) return;
        FlightEnds.Announce(owner, FlightKind.Shell, shell.Sequence, position);
        // The contact itself carries no damage (the explosion does); it registers the shot on the enemy limb it struck.
        if (_contact.ContactTarget(position, shell.Direction, hit, out var limb))
        {
            _contact.ResolveHit(owner, gear.Numbers.Shot with { Damage = 0f, Stagger = 0f }, position, shell.Direction, limb);
            if (_contact.Receiver?.TryCast<Dam_EnemyDamageLimb>() != null) _contact.Apply(0f, 0f);
        }
        _explosion.Apply(owner, gear, position, gear.Numbers.BlastRadius, shell.Damage);
        // A vanilla explosion (a mine) alerts sleepers with the game's world noise, made on the host only; gunfire sets only the shooter's noise.
        ForgeCore.Combat.Noise(position, gear.Numbers.NoiseRadius, (int)owner.DimensionIndex);
    }

    /// <summary>The host's word: shot <paramref name="sequence"/> burst at <paramref name="at"/>. The shell, if it is still flying here, bursts there.</summary>
    internal void Finish(uint sequence, Vector3 at, float now)
    {
        for (var index = 0; index < Capacity; index++)
        {
            var shell = _shells[index];
            if (shell.Owner is null || shell.Sequence != sequence) continue;
            shell.Position = at; shell.Hold = 0f; shell.Confirmed = true;
            Explode(index, shell, now, default);
            return;
        }
    }

    private void End(int index)
    {
        var shell = _shells[index];
        if (shell.Owner is null) return;
        shell.Owner = null;
        _active--;
        _visuals.EndShell(index);
    }

    internal void Clear()
    {
        for (var index = 0; index < Capacity; index++) End(index);
    }
}

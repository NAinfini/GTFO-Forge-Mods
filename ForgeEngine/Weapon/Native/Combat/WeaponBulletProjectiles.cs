using System;
using System.Buffers.Binary;
using System.Globalization;
using Agents;
using Enemies;
using ForgeEngine.Framework;
using Gear;
using HarmonyLib;
using Player;
using UnityEngine;
using ForgeCore;

namespace ForgeWeapon.Native;

/// <summary>
/// The `forge.action.combat.bullet_projectile` body: one flying weapon projectile, stepped by this provider on the
/// host and resolved through the game's own bullet hit entry.
///
/// <b>The body.</b> The profile names one of the game's own <c>ProjectileType</c> members (dump.cs 586589), and the
/// prefab is read from <c>ProjectileManager.m_projectilePrefabs</c> (dump.cs 586630). An inactive owned copy
/// retains only presentation components before activation; no native projectile is spawned or disabled.
///
/// <b>The flight.</b> Each frame of the manager's own <c>Update</c> (dump.cs 586677) moves every live flight by its
/// velocity times <c>Time.deltaTime</c>, bends the velocity down by `gravity` and toward `homing_target` by at most
/// `homing_rate`, and sweeps the step with <c>Physics.RaycastNonAlloc</c> (dump.cs 279814) or, for a `hit_size`, with
/// <c>Physics.SphereCastNonAlloc</c> (dump.cs 279882) against <c>LayerManager.MASK_BULLETWEAPON_RAY</c> (dump.cs
/// 583217) — the mask the weapon's own ray uses. `speed` absent is the prefab's own <c>ProjectileTargeting.Speed</c>
/// (dump.cs 535249).
///
/// <b>The hits.</b> A swept collider that belongs to an enemy or a player (the limb climb the adapter uses) is
/// submitted through <c>BulletWeapon.BulletHit</c> (dump.cs 617296) with a <c>Weapon.WeaponHitData</c> (dump.cs
/// 572373) whose owner is the source's player, whose damage is `amount` and whose ray hit is the swept hit — the same
/// entry the hitscan row resolves through, so limb, armor, backstab and replication stay the game's own. The shooter,
/// the `ignore` agents and an agent this flight already pierced are passed. `pierce_count` agents are passed through
/// before the flight ends; `ricochet_count` world surfaces reflect the flight before one ends it. With
/// `run_hit_triggers` the hit is published as this provider's hit candidate, billed to this flight.
///
/// <b>What the result says.</b> The row is the launch: `committed` means the body was spawned and the flight is
/// running. Its hits are facts that follow, and its end is logged. The other players see copies the host's table
/// packets make, move and take away (<see cref="SendTable"/>). Copies contain no combat components and never sweep,
/// spend ammunition or submit hits.
///
/// <b>The flight's handle.</b> A committed launch also hands out a `projectile` handle on
/// <see cref="BulletProjectileContract.ProjectilePort"/>, minted in <see cref="ProjectileStates"/> for the flight's
/// own slot. Every step writes the placed point and heading into that slot (an index write, no allocation) and the
/// flight's end marks it gone, which is what `forge.query.projectile.state` answers from.
/// </summary>
internal sealed class WeaponBulletProjectiles
{
    /// <summary>How many flights one host keeps in the air at once.</summary>
    internal const int Capacity = 64;
    /// <summary>How many `ignore` agents one flight carries.</summary>
    internal const int MaximumIgnore = 32;
    internal const double MaximumAmount = float.MaxValue;
    internal const double MaximumSpeed = float.MaxValue;
    internal const double MaximumGravity = float.MaxValue;
    internal const double MaximumRange = float.MaxValue;
    private const double DefaultRange = 1000d;
    internal const double MaximumHitSize = float.MaxValue;
    internal const double MaximumHomingRate = float.MaxValue;
    /// <summary>How many hits one sweep holds before the buffer grows.</summary>
    internal const int SweepBuffer = 32;
    /// <summary>The clearance a ricochet continues from, the same one the hitscan row's ricochet uses.</summary>
    internal const float WallHitBuffer = 0.03f;

    internal static WeaponBulletProjectiles? Current { get; private set; }

    /// <summary>Whether the frame hook has anything to do: a flight stands, or the table is still being sent after the last one.</summary>
    internal bool Flying => _live != 0 || _tableUntil != 0f;

    private sealed class Flight
    {
        /// <summary>This flight's slot, which is also its entry in <see cref="ProjectileStates"/>.</summary>
        internal int Index;
        internal bool Live;
        internal long Sequence;
        internal string Profile = "";
        internal int ProfileIndex;
        internal GameObject? Body;
        internal Vector3 Position;
        internal Vector3 Heading;
        internal Vector3 Velocity;
        internal float Gravity;
        internal float Radius;
        internal float RangeLeft;
        internal long ExpiryTick;
        internal Agent? Homing;
        internal float HomingRadians;
        internal bool HomingLost;
        internal int RicochetsLeft;
        internal int PiercesLeft;
        internal bool Publish;
        internal PlayerAgent? Owner;
        internal IntPtr OwnerAgent;
        internal EntityReference Source = null!;
        internal EntityReference? Equipment;
        internal EntityReference? OwnerReference;
        internal float Damage;
        internal readonly IntPtr[] Ignore = new IntPtr[MaximumIgnore];
        internal int IgnoreCount;
        internal readonly IntPtr[] Pierced = new IntPtr[BulletProjectileContract.MaximumBounces + 1];
        internal int PiercedCount;
        internal int Hits;
        /// <summary>What the flight reached, tallied the way the native shot tallies its rays: an impact whose collider
        /// names a target (an agent or a map object) counts as reaching it, any other surface as world, and the first
        /// impact's reading is the one the resolution reports.</summary>
        internal int Targets, Worlds;
        internal EntityReference? Target;
        internal Vector3 ImpactPoint, ImpactNormal;
    }

    private readonly IForgeEngine _kernel;
    private readonly EquipmentNativeAdapter _adapter;
    private readonly Func<bool> _canExecute;
    private readonly WeaponLog _log;
    private readonly Flight[] _flights = new Flight[Capacity];
    private readonly ProjectileType[] _types;
    private readonly bool[] _typeKnown;
    private readonly ForgeCore.SweepQuery _sweep = new(SweepBuffer);
    private readonly Weapon.WeaponHitData _hit = new();
    // WHY: P5 — the candidate and resolution rows' gates, taken once in Bind: this body is a handler of the registration
    // the adapter attaches to, so it is built before the adapter is attached.
    private RuntimeSubscriptionGate _hitGate = null!, _shotResolvedGate = null!;
    /// <summary>The flights' states and handles, created once the registration the handles are minted through
    /// exists.</summary>
    private ProjectileStates? _states;
    private long _sequence;
    private long _events;
    private int _live;
    private readonly GameObject?[] _replicas = new GameObject?[Capacity];
    private readonly uint[] _replicaSequences = new uint[Capacity];

    // The other players' copies live on one unreliable message of ForgeWeapon's ForgeCore channel, which stamps it with the world and
    // drops one that arrives after a newer one. Every packet is the whole table: 8 bytes with one bit per slot that holds a flight,
    // then per flight its slot (1), sequence (4), profile (1), position (12) and heading (12). A table above one packet (1 KiB) goes
    // out one share per interval (SendTable), each packet with the whole mask and its own share of the flights.
    private const int MaskBytes = 8, EntryBytes = 30, PacketFlights = (1024 - MaskBytes) / EntryBytes;
    private const float TableInterval = 0.05f, TableFarewell = 1f;
    private static NetBytes? _table;
    private float _nextTable, _tableUntil;
    private int _tableCursor;

    private WeaponBulletProjectiles(IForgeEngine kernel, EquipmentNativeAdapter adapter, Func<bool> canExecute, WeaponLog log)
    {
        _kernel = kernel;
        _adapter = adapter;
        _canExecute = canExecute;
        _log = log;
        for (var index = 0; index < _flights.Length; index++) _flights[index] = new Flight { Index = index };
        var members = BulletProjectileContract.NativeMembers;
        _types = new ProjectileType[members.Count];
        _typeKnown = new bool[members.Count];
        for (var index = 0; index < members.Count; index++)
            _typeKnown[index] = ProjectileSpawner.TryType(members[index], out _types[index]);
    }

    internal static WeaponBulletProjectiles Attach(IForgeEngine kernel, EquipmentNativeAdapter adapter,
        Func<bool> canExecute, WeaponLog log)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(canExecute); ArgumentNullException.ThrowIfNull(log);
        Detach();
        Current = new WeaponBulletProjectiles(kernel, adapter, canExecute, log);
        return Current;
    }

    /// <summary>The registration the flights' handles are minted through, handed over once it exists: the flight
    /// table is created and attached for `forge.query.projectile.state` on this thread.</summary>
    internal void Bind(RuntimeModuleHandle registration)
    {
        _hitGate = _adapter.GateOf(ModuleDefinition.HitCandidateBinding);
        _shotResolvedGate = _adapter.GateOf(CombatPrimitiveContract.ShotResolvedBinding);
        if (_states != null) ProjectileStates.Detach(_states);
        _states = ProjectileStates.Attach(new ProjectileStates(_kernel, registration, Capacity));
        if (_table != null) return;
        var channel = ForgeEngine.GameBindings.NetworkBinding.Channel;
        _table = channel.Bytes("weapon/projectiles", 1, (sender, data) => { if (channel.FromHost(sender)) Current?.ReceiveTable(data); },
            NetDelivery.Lossy, MaskBytes + PacketFlights * EntryBytes);
    }

    internal static void Detach()
    {
        var current = Current;
        Current = null;
        if (current == null) return;
        current.EndAll("session-ended");
        current.ClearReplicas();
        if (current._states != null) ProjectileStates.Detach(current._states);
        current._states = null;
    }

    /// <summary>
    /// `forge.action.combat.bullet_projectile`, on the host. Refusals, in the order they are checked: a request with
    /// no source, a machine that may not write, a structural parameter outside its vocabulary, a source this machine
    /// cannot resolve to a player, a hit-trigger flight whose equipment cannot be named, a missing or malformed
    /// vector, an amount, speed, gravity, lifetime, range or hit size outside its bounds, a homing target this machine
    /// cannot resolve or one without a positive rate, too many ignored entities, no free flight slot, a build without
    /// the profile's member, a body whose speed the plan left to a prefab that has none, and a
    /// spawn that returned nothing.
    /// </summary>
    internal CommandResult Launch(CommandContext context)
    {
        if (!context.TryEntity(BulletProjectileContract.SourceSlot, out var source))
            return Outcome.Rejected(ResultCode.SourceMissing).WithDetail("source");
        if (!_canExecute() || !_kernel.Lifecycle.IsReadyHost) return Refused(context, ResultCode.AuthorityOrPhase);
        if (!_kernel.IsEntityCurrent(source)) return Refused(context, ResultCode.BulletProjectileSourceUnresolved);
        if (!BulletProjectileContract.TryRead(context, out var parameters, out var parameterCode))
            return Refused(context, parameterCode);

        PlayerAgent? owner = null;
        EntityReference? equipment = null;
        if (_adapter.EquippableOf(source) is { } equippable)
        {
            owner = equippable.Item.Owner;
            equipment = source;
        }
        else if (_adapter.PlayerOf(source) is { HasPlayerAgent: true } player
                 && player.PlayerAgent?.TryCast<PlayerAgent>() is { } agent)
        {
            owner = agent;
            var wielded = agent.Inventory == null ? null : agent.Inventory.WieldedItem;
            equipment = wielded == null ? null : _adapter.CurrentEntityOf(wielded);
        }
        if (owner == null || owner.Pointer == IntPtr.Zero) return Refused(context, ResultCode.BulletProjectileSourceUnresolved);
        // A hit candidate belongs to an equipment life; a flight asked to feed hit triggers with no equipment to
        // bill them to is refused rather than published under another item.
        if (parameters.RunHitTriggers && equipment == null) return Refused(context, ResultCode.BulletProjectileEquipmentUnresolved);

        if (!ShotVectors.TryRead(context, BulletProjectileContract.OriginSlot, out var origin)
            || !ShotVectors.TryRead(context, BulletProjectileContract.DirectionSlot, out var direction)
            || !ShotVectors.Unit(direction, out direction))
            return Refused(context, ResultCode.VectorInvalid);
        var amount = context.Number(BulletProjectileContract.AmountSlot)
                     ?? (context.Present(BulletProjectileContract.AmountSlot) ? double.NaN : 0d);
        if (!double.IsFinite(amount) || Math.Abs(amount) > MaximumAmount) return Refused(context, ResultCode.AmountOutOfRange);
        var speedDeclared = context.Present(BulletProjectileContract.SpeedSlot);
        var speed = context.Number(BulletProjectileContract.SpeedSlot) ?? double.NaN;
        if (speedDeclared && (!double.IsFinite(speed) || Math.Abs(speed) > MaximumSpeed))
            return Refused(context, ResultCode.SpeedOutOfRange);
        var gravity = context.Number(BulletProjectileContract.GravitySlot)
                      ?? (context.Present(BulletProjectileContract.GravitySlot) ? double.NaN : 0d);
        if (!double.IsFinite(gravity) || Math.Abs(gravity) > MaximumGravity) return Refused(context, ResultCode.BulletProjectileGravityOutOfRange);
        long expiry = -1;
        if (context.Present(BulletProjectileContract.LifetimeSlot))
        {
            if (context.Integer(BulletProjectileContract.LifetimeSlot) is not { } lifetime || lifetime <= 0)
                return Refused(context, ResultCode.BulletProjectileLifetimeInvalid);
            var now = Math.Max(0, context.SimulationTick);
            if (lifetime > long.MaxValue - now) return Refused(context, ResultCode.BulletProjectileLifetimeInvalid);
            expiry = now + lifetime;
        }
        var range = context.Number(BulletProjectileContract.RangeSlot)
                    ?? (context.Present(BulletProjectileContract.RangeSlot) ? double.NaN : DefaultRange);
        if (!double.IsFinite(range) || Math.Abs(range) > MaximumRange) return Refused(context, ResultCode.RangeOutOfRange);
        var hitSize = context.Number(BulletProjectileContract.HitSizeSlot)
                      ?? (context.Present(BulletProjectileContract.HitSizeSlot) ? double.NaN : 0d);
        if (!double.IsFinite(hitSize) || Math.Abs(hitSize) > MaximumHitSize) return Refused(context, ResultCode.BulletProjectileHitSizeOutOfRange);

        Agent? homing = null;
        var homingRate = 0d;
        if (context.TryEntity(BulletProjectileContract.HomingTargetSlot, out var homingTarget))
        {
            homing = AgentOf(context, homingTarget);
            if (homing == null) return Refused(context, ResultCode.BulletProjectileHomingUnresolved);
            homingRate = context.Number(BulletProjectileContract.HomingRateSlot) ?? double.NaN;
            if (!double.IsFinite(homingRate) || Math.Abs(homingRate) > MaximumHomingRate)
                return Refused(context, ResultCode.BulletProjectileHomingRateInvalid);
        }

        var slot = Free();
        if (slot == null) return Refused(context, ResultCode.BulletProjectileCapacity);
        slot.IgnoreCount = 0;
        var ignoredCount = context.Count(BulletProjectileContract.IgnoreSlot);
        if (ignoredCount < 0 && context.Present(BulletProjectileContract.IgnoreSlot)) return Refused(context, ResultCode.BulletProjectileIgnoreUnresolved);
        if (ignoredCount > MaximumIgnore) return Refused(context, ResultCode.BulletProjectileIgnoreTooMany);
        for (var index = 0; index < ignoredCount; index++)
        {
            var resolved = context.TryEntity(BulletProjectileContract.IgnoreSlot, index, out var reference);
            var agent = resolved ? AgentOf(context, reference) : null;
            if (agent == null)
            {
                return Refused(context, ResultCode.BulletProjectileIgnoreUnresolved);
            }
            slot.Ignore[slot.IgnoreCount++] = agent.Pointer;
        }

        var profile = parameters.ProfileIndex;
        if (!_typeKnown[profile]) return Refused(context, ResultCode.BulletProjectileSpawnUnavailable);
        // The manager's per-frame update is what steps a flight, so it goes on with the first launch; a flight that
        // nothing would step is refused rather than left standing.
        if (WeaponLazyHooks.Ensure(WeaponLazyHooks.Unit.ProjectileStep) != null) return Refused(context, ResultCode.BulletProjectileSpawnUnavailable);
        GameObject? body;
        string spawnReason;
        try
        {
            body = ForgeVisual.ProjectileLooks.Copy(_types[profile], origin, Quaternion.LookRotation(direction),
                !speedDeclared, out var prefabSpeed, out spawnReason);
            if (!speedDeclared) speed = prefabSpeed;
        }
        catch (Exception error)
        {
            Failed("spawn", Kind(error), null);
            return Unconfirmed(context);

        }
        if (body == null)
        {
            Failed("spawn", spawnReason, null);
            return Refused(context, !speedDeclared && !double.IsFinite(speed) ? ResultCode.BulletProjectileSpeedRequired : ResultCode.BulletProjectileSpawnUnavailable);
        }
        // The flight's handle, minted for its own slot. A machine whose registration is not bound yet, or a kernel at
        // its live-handle ceiling, refuses the launch rather than run a flight the plan could not name.
        RuntimeHandle handle;
        try
        {
            if (_states == null) throw new RuntimeContractException(ResultCode.AuthorityOrPhase, "registration-unbound");
            handle = _states.Open(slot.Index, origin.x, origin.y, origin.z, direction.x, direction.y, direction.z);
        }
        catch (RuntimeContractException error)
        {
            UnityEngine.Object.Destroy(body);
            return Refused(context, error.Code == ResultCode.AuthorityOrPhase ? ResultCode.AuthorityOrPhase : ResultCode.HandleBudget);
        }
        catch (Exception error)
        {
            UnityEngine.Object.Destroy(body);
            Failed("spawn", Kind(error), null);
            return Unconfirmed(context);

        }

        slot.Live = true;
        slot.Sequence = ++_sequence;
        slot.Profile = BulletProjectileContract.Profiles[profile];
        slot.ProfileIndex = profile;
        slot.Body = body;
        slot.Position = origin;
        slot.Heading = direction;
        slot.Velocity = new Vector3((float)(direction.x * speed), (float)(direction.y * speed), (float)(direction.z * speed));
        slot.Gravity = (float)gravity;
        slot.Radius = (float)hitSize;
        slot.RangeLeft = (float)range;
        slot.ExpiryTick = expiry;
        slot.Homing = homing;
        slot.HomingRadians = (float)(homingRate * Math.PI / 180d);
        slot.HomingLost = false;
        slot.RicochetsLeft = parameters.RicochetCount;
        slot.PiercesLeft = parameters.PierceCount;
        slot.Publish = parameters.RunHitTriggers;
        slot.Owner = owner;
        slot.OwnerAgent = owner.Pointer;
        slot.Source = source;
        slot.Equipment = equipment;
        slot.OwnerReference = _adapter.OwnerOf(owner);
        slot.Damage = (float)amount;
        slot.PiercedCount = 0;
        slot.Hits = 0;
        slot.Targets = 0;
        slot.Worlds = 0;
        slot.Target = null;
        _live++;
        if (_log.Logs(RuntimeLogLevel.Trace))
            _log.Write(RuntimeLogLevel.Trace, WeaponLogCodes.ProjectileFlight, new RuntimeLogFacts
            {
                Entity = source.Id,
                Values = new[] { RuntimeLogValue.Of("phase", "launched"), RuntimeLogValue.Of("flight", slot.Sequence),
                    RuntimeLogValue.Of("profile", slot.Profile), RuntimeLogValue.Of("speed", speed),
                    RuntimeLogValue.Of("ricochet", slot.RicochetsLeft), RuntimeLogValue.Of("pierce", slot.PiercesLeft),
                    RuntimeLogValue.Of("homing", homing != null), RuntimeLogValue.Of("triggers", slot.Publish) }
            });
        context.Rows.Row(0, Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
        context.Outputs.Handle(Blocks.ActionCombatBulletProjectile.OutProjectile, handle);
        return Outcome.Succeeded();
    }

    /// <summary>A world boundary, a checkpoint reload or a runtime that stopped ends every flight: the agents and
    /// the level the flights were sweeping went with it.</summary>
    internal void Observe(RuntimeLifecycleEvent value)
    {
        if (value.Kind is RuntimeLifecycleKind.Snapshot or RuntimeLifecycleKind.WorldChanged
            or RuntimeLifecycleKind.CheckpointRestored
            || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
        {
            if (_live > 0) EndAll("world-boundary");
            // Every player's copies go with the world, so no table follows the flights that ended here.
            _tableUntil = 0f;
            ClearReplicas();
            // The handles name flights of the world that ended; each is given back so none answers for it again.
            _states?.ReleaseAll();
        }
    }

    /// <summary>One frame of every live flight, from the projectile manager's own update on the host.</summary>
    internal void Step()
    {
        if (!_canExecute() || !_kernel.Lifecycle.IsReadyHost)
        {
            EndAll("authority-lost");
            // The empty table still goes out: it takes the copies away while this machine is the host, and from any other machine it is ignored.
            SendTable();
            return;
        }
        var delta = Time.deltaTime;
        if (!(delta > 0f)) return;
        var tick = _kernel.CurrentTick;
        for (var index = 0; index < _flights.Length; index++)
        {
            var flight = _flights[index];
            if (!flight.Live) continue;
            try
            {
                if (flight.ExpiryTick >= 0 && tick >= flight.ExpiryTick) { End(flight, "lifetime"); continue; }
                if (flight.Body == null) { End(flight, "body-gone"); continue; }
                Advance(flight, delta);
            }
            catch (Exception error)
            {
                Failed("step", Kind(error), flight.Sequence);
                End(flight, "step-failed");
            }
        }
        SendTable();
    }

    private void Advance(Flight flight, float delta)
    {
        var velocity = flight.Velocity;
        if (flight.Homing != null)
        {
            var target = flight.Homing;
            if (target.Pointer == IntPtr.Zero || !target.Alive)
            {
                flight.Homing = null;
                if (!flight.HomingLost && _log.Logs(RuntimeLogLevel.Trace))
                    _log.Write(RuntimeLogLevel.Trace, WeaponLogCodes.ProjectileFlight, new RuntimeLogFacts
                    {
                        Values = new[] { RuntimeLogValue.Of("phase", "homing-lost"), RuntimeLogValue.Of("flight", flight.Sequence) }
                    });
                flight.HomingLost = true;
            }
            else
            {
                var aim = target.AimTarget;
                var point = aim != null ? aim.position : target.Position;
                var toward = point - flight.Position;
                if (toward.sqrMagnitude > 1e-6f)
                    velocity = Vector3.RotateTowards(velocity, toward, flight.HomingRadians * delta, 0f);
            }
        }
        if (flight.Gravity != 0f) velocity = new Vector3(velocity.x, velocity.y - flight.Gravity * delta, velocity.z);
        if (!float.IsFinite(velocity.x) || !float.IsFinite(velocity.y) || !float.IsFinite(velocity.z))
        {
            Failed("step", "numeric-overflow", flight.Sequence);
            End(flight, "step-failed");
            return;
        }
        flight.Velocity = velocity;
        var speed = Math.Sqrt((double)velocity.x * velocity.x + (double)velocity.y * velocity.y + (double)velocity.z * velocity.z);
        if (speed == 0d) { End(flight, "stalled"); return; }
        var heading = new Vector3((float)(velocity.x / speed), (float)(velocity.y / speed), (float)(velocity.z / speed));
        var remaining = (float)Math.Min(speed * delta, flight.RangeLeft);
        var position = flight.Position;
        // One sweep per segment: a ricochet starts a new segment from the surface it reflected off, so a step makes at
        // most one sweep more than the ricochets it still has.
        for (var segment = 0; segment <= BulletProjectileContract.MaximumBounces + 1 && remaining > 0f; segment++)
        {
            var count = _sweep.Cast(position, flight.Radius, heading, remaining, LayerManager.MASK_BULLETWEAPON_RAY);
            var reflected = false;
            for (var rank = 0; rank < count; rank++)
            {
                var hit = _sweep[rank];
                var collider = hit.collider;
                if (collider == null) continue;
                var agent = AgentOf(collider);
                if (agent != null)
                {
                    var pointer = agent.Pointer;
                    if (pointer == flight.OwnerAgent || Contains(flight.Ignore, flight.IgnoreCount, pointer)
                        || Contains(flight.Pierced, flight.PiercedCount, pointer)) continue;
                    Submit(flight, hit, heading);
                    if (flight.PiercesLeft > 0)
                    {
                        flight.PiercesLeft--;
                        flight.Pierced[flight.PiercedCount++] = pointer;
                        continue;
                    }
                    Place(flight, hit.point, heading);
                    End(flight, "hit");
                    return;
                }
                // World geometry: a ricochet reflects the flight off it, and the last surface ends the flight. The
                // surface can still be a map object (a door, a shootable model): it is named the same way the native
                // shot's tally names it, so a resolution reports the object a plan attached to it listens on.
                if (Resolving(flight)) Impact(flight, hit, ColliderTarget(hit));
                if (flight.RicochetsLeft <= 0)
                {
                    Place(flight, hit.point, heading);
                    End(flight, "world");
                    return;
                }
                flight.RicochetsLeft--;
                var normal = hit.normal;
                var travelled = hit.distance;
                flight.RangeLeft -= travelled;
                remaining -= travelled;
                position = new Vector3(hit.point.x + normal.x * WallHitBuffer, hit.point.y + normal.y * WallHitBuffer,
                    hit.point.z + normal.z * WallHitBuffer);
                heading = Vector3.Reflect(heading, normal);
                flight.Velocity = new Vector3((float)(heading.x * speed), (float)(heading.y * speed), (float)(heading.z * speed));
                reflected = true;
                break;
            }
            if (reflected) continue;
            position += heading * remaining;
            flight.RangeLeft -= remaining;
            remaining = 0f;
        }
        Place(flight, position, heading);
        if (flight.RangeLeft <= 0f) End(flight, "range");
    }

    /// <summary>One swept hit submitted through the game's own hit entry, and published as this flight's hit
    /// candidate when the flight feeds hit triggers. The provider's own hit observer, which runs on the same entry,
    /// is told not to publish, because the candidate it would raise is billed to the weapon's last shot and not to
    /// this flight.</summary>
    private void Submit(Flight flight, RaycastHit hit, Vector3 heading)
    {
        var data = _hit;
        data.owner = flight.Owner;
        data.fireAtPos = flight.Position;
        data.fireDir = heading;
        data.hasFireDir = true;
        data.maxRayDist = hit.distance;
        data.damage = flight.Damage;
        data.damageFalloff = new UnityEngine.Vector2((float)MaximumRange, (float)MaximumRange);
        data.randomSpread = 0f;
        data.angOffsetX = 0f;
        data.angOffsetY = 0f;
        data.rayHit = hit;
        var target = _adapter.HitTarget(data);
        if (Resolving(flight)) Impact(flight, hit, target);
        var saved = WeaponCombatObserver.SuppressTriggers;
        WeaponCombatObserver.SuppressTriggers = true;
        bool resolved;
        try { resolved = BulletWeapon.BulletHit(data, true, 0f, 0u, true); }
        finally { WeaponCombatObserver.SuppressTriggers = saved; }
        // Only a hit the game itself resolved counts and raises a candidate.
        if (!resolved) return;
        flight.Hits++;
        if (!flight.Publish || flight.Equipment == null) return;
        if (!_hitGate.HasSubscribers) return;
        var point = hit.point;
        var epoch = _kernel.WorldEpoch;
        var id = "gtfo.weapon.projectile-hit:" + Number(epoch) + ":" + Number(checked(++_events));
        var fact = _adapter.Begin(_hitGate, epoch);
        WeaponCombatObserver.HitCandidateOutputs(fact.Values, flight.OwnerReference, flight.Equipment, target, point.x, point.y, point.z, null);
        // The kernel records the outcome itself (`trigger.fired` / `event.rejected`), so no second line is written.
        fact.Raise(id, epoch, Math.Max(0, _kernel.CurrentTick), "gtfo.weapon.projectile:" + Number(flight.Sequence));
    }

    /// <summary>Whether this flight's resolution will be published: it feeds hit triggers, it bills an equipment
    /// life, and a loaded plan listens on the shot resolution row. Nothing about the impacts is read otherwise.</summary>
    private bool Resolving(Flight flight)
        => flight.Publish && flight.Equipment != null && _shotResolvedGate.HasSubscribers;

    /// <summary>The Forge reference a swept surface's collider belongs to, through the same reader the native shot's
    /// tally uses; null for plain world geometry.</summary>
    private EntityReference? ColliderTarget(RaycastHit hit)
    {
        var data = _hit;
        data.rayHit = hit;
        return _adapter.HitTarget(data);
    }

    private static void Impact(Flight flight, RaycastHit hit, EntityReference? target)
    {
        var first = flight.Targets == 0 && flight.Worlds == 0;
        if (target == null) flight.Worlds++;
        else flight.Targets++;
        if (!first) return;
        flight.Target = target;
        flight.ImpactPoint = hit.point;
        flight.ImpactNormal = hit.normal;
    }

    /// <summary>
    /// The flight's own `shot_resolved`: a bullet this row flies is the shot's bullet, so when it feeds hit triggers
    /// its ending is the shot's resolution, with the same outcome, target and impact reading the native shot's tally
    /// reports. Only an ending the flight reached on its own resolves it — a hit, a surface, its range, its lifetime
    /// or a stall; a world boundary, lost authority, a lost body or a failed step ends the flight without a fact,
    /// because the bullet did not finish its trajectory.
    /// </summary>
    private void Resolve(Flight flight, string reason)
    {
        if (reason is not ("hit" or "world" or "range" or "lifetime" or "stalled") || !Resolving(flight)) return;
        var equipment = flight.Equipment!;
        var hits = flight.Targets + flight.Worlds;
        var outcome = flight.Targets > 0 ? CombatPrimitiveContract.Hits
            : flight.Worlds > 0 ? CombatPrimitiveContract.World : CombatPrimitiveContract.Misses;
        var point = flight.ImpactPoint;
        var normal = flight.ImpactNormal;
        var epoch = _kernel.WorldEpoch;
        var id = "gtfo.weapon.projectile-shot:" + Number(epoch) + ":" + Number(checked(++_events));
        var fact = _adapter.Begin(_shotResolvedGate, epoch);
        CombatFactsObserver.ShotResolvedOutputs(fact.Values, flight.OwnerReference, equipment, outcome, flight.Target, hits,
            point.x, point.y, point.z, normal.x, normal.y, normal.z);
        fact.Raise(id, epoch, Math.Max(0, _kernel.CurrentTick), equipment.Id);
    }

    private void Place(Flight flight, Vector3 position, Vector3 heading)
    {
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
        {
            Failed("step", "numeric-overflow", flight.Sequence);
            End(flight, "step-failed");
            return;
        }
        flight.Position = position;
        flight.Heading = heading;
        _states?.Move(flight.Index, position.x, position.y, position.z, heading.x, heading.y, heading.z);
        var body = flight.Body;
        if (body == null) return;
        var transform = body.transform;
        transform.position = position;
        transform.rotation = Quaternion.LookRotation(heading);
    }

    private void End(Flight flight, string reason)
    {
        if (!flight.Live) return;
        Resolve(flight, reason);
        flight.Live = false;
        _live--;
        if (_live == 0) _tableUntil = Clock.Time + TableFarewell;
        _states?.End(flight.Index);
        var body = flight.Body;
        flight.Body = null;
        flight.Homing = null;
        flight.Owner = null;
        flight.Equipment = null;
        flight.OwnerReference = null;
        try { if (body != null) UnityEngine.Object.Destroy(body); }
        catch (Exception error)
        {
            if (_log.Logs(RuntimeLogLevel.Error))
                _log.Write(RuntimeLogLevel.Error, WeaponLogCodes.ReleaseFailed, new RuntimeLogFacts
                {
                    Values = new[] { RuntimeLogValue.Of("what", "projectile-body"), RuntimeLogValue.Of("flight", flight.Sequence),
                        RuntimeLogValue.Of("reason", Kind(error)) }
                });
        }
        if (_log.Logs(RuntimeLogLevel.Trace))
            _log.Write(RuntimeLogLevel.Trace, WeaponLogCodes.ProjectileFlight, new RuntimeLogFacts
            {
                Values = new[] { RuntimeLogValue.Of("phase", "ended"), RuntimeLogValue.Of("flight", flight.Sequence),
                    RuntimeLogValue.Of("reason", reason), RuntimeLogValue.Of("hits", flight.Hits) }
            });
    }

    /// <summary>Ends every live flight and destroys its body; the world boundary does it, and so does a check that must leave none behind.</summary>
    internal void EndAll(string reason)
    {
        for (var index = 0; index < _flights.Length; index++)
            if (_flights[index].Live) End(_flights[index], reason);
    }

    /// <summary>Host: the whole table to every other player, every <see cref="TableInterval"/> seconds while a flight lives and for
    /// <see cref="TableFarewell"/> seconds after the last one ended. Nothing is sent again on request: a lost packet costs a copy one
    /// step of its path, a player who joins gets the flights with the next packet, and the empty tables after the last flight keep a
    /// lost one from leaving a copy standing.</summary>
    private void SendTable()
    {
        var now = Clock.Time;
        if (now < _nextTable) return;
        _nextTable = now + TableInterval;
        if (_live == 0 && now >= _tableUntil) _tableUntil = 0f;
        ulong mask = 0;
        for (var index = 0; index < _flights.Length; index++)
            if (_flights[index].Live) mask |= 1UL << index;
        // One packet per interval. The channel keeps one sequence per message route and sender and drops a packet that arrives
        // after a newer one, so two packets of one table sent together could lose the first share whenever they swap. A table
        // above one packet therefore sends the next share of its flights each interval, every packet with the whole mask.
        var count = Math.Min(_live, PacketFlights);
        var data = new byte[MaskBytes + count * EntryBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(data, mask);
        var slot = _live > PacketFlights ? _tableCursor : 0;
        for (var written = 0; written < count; slot = (slot + 1) % Capacity)
        {
            var flight = _flights[slot];
            if (!flight.Live) continue;
            var entry = data.AsSpan(MaskBytes + written++ * EntryBytes);
            entry[0] = (byte)slot;
            BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(1), (uint)flight.Sequence);
            entry[5] = (byte)flight.ProfileIndex;
            var p = flight.Position; var h = flight.Heading;
            BinaryPrimitives.WriteSingleLittleEndian(entry.Slice(6), p.x); BinaryPrimitives.WriteSingleLittleEndian(entry.Slice(10), p.y); BinaryPrimitives.WriteSingleLittleEndian(entry.Slice(14), p.z);
            BinaryPrimitives.WriteSingleLittleEndian(entry.Slice(18), h.x); BinaryPrimitives.WriteSingleLittleEndian(entry.Slice(22), h.y); BinaryPrimitives.WriteSingleLittleEndian(entry.Slice(26), h.z);
        }
        _tableCursor = slot;
        _table!.Broadcast(data);
    }

    /// <summary>A client, for one packet of the host's table: the copy of a slot the mask leaves out is destroyed, a flight this
    /// client has no copy of gets one, and the others move. An entry that does not parse is skipped.</summary>
    private void ReceiveTable(byte[] data)
    {
        if (data.Length < MaskBytes || (data.Length - MaskBytes) % EntryBytes != 0) return;
        var span = data.AsSpan();
        var mask = BinaryPrimitives.ReadUInt64LittleEndian(span);
        for (var index = 0; index < Capacity; index++)
            if ((mask >> index & 1) == 0) ReleaseReplica(index);
        for (var at = MaskBytes; at < data.Length; at += EntryBytes)
        {
            var entry = span.Slice(at, EntryBytes);
            int slot = entry[0], profile = entry[5];
            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(1));
            var position = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(entry.Slice(6)), BinaryPrimitives.ReadSingleLittleEndian(entry.Slice(10)),
                BinaryPrimitives.ReadSingleLittleEndian(entry.Slice(14)));
            var heading = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(entry.Slice(18)), BinaryPrimitives.ReadSingleLittleEndian(entry.Slice(22)),
                BinaryPrimitives.ReadSingleLittleEndian(entry.Slice(26)));
            if (slot >= Capacity || (mask >> slot & 1) == 0 || profile >= _types.Length || !_typeKnown[profile]
                || !float.IsFinite(position.x + position.y + position.z) || !float.IsFinite(heading.x + heading.y + heading.z)
                || Math.Abs(heading.sqrMagnitude - 1f) > 0.001f) continue;
            var rotation = Quaternion.LookRotation(heading);
            // A slot is taken once per sequence: a copy that could not be made is not tried again with every packet.
            if (_replicaSequences[slot] != sequence)
            {
                ReleaseReplica(slot);
                _replicaSequences[slot] = sequence;
                string reason;
                try { _replicas[slot] = ForgeVisual.ProjectileLooks.Copy(_types[profile], position, rotation, false, out _, out reason); }
                catch (Exception error) { reason = Kind(error); }
                if (_replicas[slot] == null) Failed("replica-spawn", reason, sequence);
                continue;
            }
            var body = _replicas[slot];
            if (body == null) continue;
            body.transform.position = position;
            body.transform.rotation = rotation;
        }
    }

    private void ReleaseReplica(int index)
    {
        var body = _replicas[index];
        try { if (body != null) UnityEngine.Object.Destroy(body); }
        catch (Exception error) { Failed("replica-release", Kind(error), _replicaSequences[index]); throw; }
        _replicas[index] = null;
        _replicaSequences[index] = 0;
    }

    private void ClearReplicas()
    {
        for (var index = 0; index < _replicas.Length; index++) ReleaseReplica(index);
    }

    /// <summary>A slot with no flight in it. Among those, the one whose ended flight's handle has been answering
    /// `alive = false` longest is reused first, so a plan still reading a recently ended flight keeps its answer.</summary>
    private Flight? Free()
    {
        Flight? free = null;
        var order = long.MaxValue;
        for (var index = 0; index < _flights.Length; index++)
        {
            if (_flights[index].Live) continue;
            var ended = _states?.EndedOrder(index) ?? 0;
            if (ended >= order) continue;
            free = _flights[index];
            order = ended;
            if (ended == 0) break;
        }
        return free;
    }

    /// <summary>The enemy or player one swept collider belongs to: the same limb climb the adapter's hit target
    /// read makes.</summary>
    private static Agent? AgentOf(Collider collider) => EquipmentNativeAdapter.Climb(collider).Agent;

    /// <summary>The live agent one reference names on this machine: an enemy through its owning domain's instance
    /// lookup, a player through the adapter's own player resolution.</summary>
    private Agent? AgentOf(CommandContext context, EntityReference reference)
    {
        if (!_kernel.IsEntityCurrent(reference)) return null;
        if (context.EntityInstance(reference) is EnemyAgent enemy && enemy.Pointer != IntPtr.Zero) return enemy;
        if (_adapter.PlayerOf(reference) is { HasPlayerAgent: true } player
            && player.PlayerAgent?.TryCast<PlayerAgent>() is { } agent) return agent;
        return null;
    }

    private static bool Contains(IntPtr[] values, int count, IntPtr value)
    {
        for (var index = 0; index < count; index++)
            if (values[index] == value) return true;
        return false;
    }

    /// <summary>A launch refused before anything was spawned: the one result row, about the source the request
    /// carried, says why.</summary>
    private static CommandResult Refused(CommandContext context, string code)
    {
        context.Rows.Row(0, Status.Rejected, Commit.None, code);
        return Outcome.Rejected(code);
    }

    /// <summary>A spawn that may or may not have happened: the row and the command both leave the commit unknown.</summary>
    private static CommandResult Unconfirmed(CommandContext context)
    {
        context.Rows.Row(0, Status.Failed, Commit.Unknown, ResultCode.BulletProjectileSpawnUnconfirmed);
        return Outcome.FailedUnknown(ResultCode.BulletProjectileSpawnUnconfirmed);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The failure's own type. The message is not logged: it is free text the game or the runtime
    /// composed, and the log carries only this package's own vocabulary.</summary>
    private static string Kind(Exception error) => error.GetType().Name;

    /// <summary>A flight's native call failed: an error record naming the phase and the failure's type.</summary>
    private void Failed(string phase, string detail, long? flight)
    {
        if (!_log.Logs(RuntimeLogLevel.Error)) return;
        _log.Write(RuntimeLogLevel.Error, WeaponLogCodes.ProjectileFailed, new RuntimeLogFacts
        {
            Values = flight is long sequence
                ? new[] { RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("reason", detail), RuntimeLogValue.Of("flight", sequence) }
                : new[] { RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("reason", detail) }
        });
    }

    internal static void Fault(Exception error) => Current?.Failed("hook", Kind(error), null);
}

// The projectile manager's own frame (dump.cs 586677), the one place every flight is stepped: a lifecycle observer
// may not publish, and this is the game's own frame for its own projectiles.
[HarmonyPatch(typeof(ProjectileManager), "Update")]
internal static class BulletProjectileStep
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        var current = WeaponBulletProjectiles.Current;
        if (current is not { Flying: true }) return;
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.WeaponProjectilePump);
        if (WeaponNativeSession.Offer(WeaponFeature.Hit) is not { Faulted: false }) return;
        try { current.Step(); }
        catch (Exception error) { WeaponBulletProjectiles.Fault(error); }
    }
}

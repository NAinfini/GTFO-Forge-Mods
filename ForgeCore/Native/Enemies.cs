using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeCore;

/// <summary>A hold on an enemy's slowed speed taken with <see cref="Enemies.Slow"/>: dispose it to end the slowing.</summary>
public sealed class SlowLease : IDisposable
{
    internal SlowLease(EnemyAgent enemy, string owner) { Enemy = enemy; Owner = owner; }
    internal int Id { get; set; }
    public EnemyAgent Enemy { get; }
    public string Owner { get; }
    /// <summary>False when the last speed write did not read back from the animator (an error line was written).</summary>
    public bool ReadBack { get; internal set; }
    /// <summary>True until the lease is disposed or the enemy dies or is reused.</summary>
    public bool Active => Enemies.SlowHeld(this);
    /// <summary>Re-applies the slowing after the game may have changed the animator speed (that value becomes the enemy's own).</summary>
    public void Refresh() => Enemies.SlowRefresh(this);
    public void Dispose() => Enemies.SlowEnd(this);
}

/// <summary>A slowing of every enemy inside a sphere, taken with <see cref="Enemies.SlowZone"/>: dispose it to end it early.</summary>
public sealed class SlowZoneLease : IDisposable
{
    internal sealed class Held
    {
        internal SlowLease Lease = null!;
        internal int Pass;
    }

    internal SlowZoneLease(Vector3 centre, float radius, float factor, float ends, string owner)
    {
        Centre = centre; Radius = radius; Factor = factor; Ends = ends; Owner = owner;
    }

    public Vector3 Centre { get; }
    public float Radius { get; }
    /// <summary>The share of its own speed an enemy inside keeps (0..1).</summary>
    public float Factor { get; }
    public string Owner { get; }
    /// <summary>The time (<c>Time.time</c>) the zone ends by itself.</summary>
    public float Ends { get; }
    /// <summary>True until the zone is disposed, runs out, or the level is left.</summary>
    public bool Active { get; internal set; } = true;
    /// <summary>How many different enemies the zone slowed with a speed write that read back, over its whole life.</summary>
    public int Slowed => SlowedSet.Count;
    /// <summary>How many speed writes did not read back from the animator over its whole life (an error line was written for each kind).</summary>
    public int Mismatches { get; internal set; }
    internal int Pass { get; set; }
    internal readonly Dictionary<IntPtr, Held> Leases = new();
    internal readonly HashSet<IntPtr> SlowedSet = new();
    public void Dispose() => Enemies.ZoneEnd(this);
}
/// <summary>Enemies as the game holds them: which enemy a collider belongs to, every living enemy near a point (each once, all of
/// them), every living enemy in the level (<see cref="Live"/>) and how alert an enemy is. Main thread only.</summary>
public static class Enemies
{
    private const int InitialCapacity = 256;
    private static Il2CppReferenceArray<Collider> _overlap = new(InitialCapacity);
    private static EnemyAgent?[] _found = new EnemyAgent?[InitialCapacity];
    private static float[] _distances = new float[InitialCapacity];
    private static readonly HashSet<IntPtr> FoundPointers = new(InitialCapacity);
    private static readonly Dictionary<int, (Collider Collider, EnemyAgent Enemy)> Owners = new(InitialCapacity);
    private static readonly List<int> InvalidOwners = new(InitialCapacity);
    private static readonly Dictionary<IntPtr, int> SlotOf = new(InitialCapacity);
    private static readonly Stack<int> Vacant = new();
    private static EnemyAgent?[] _live = new EnemyAgent?[InitialCapacity];
    private static int[] _generations = new int[InitialCapacity];
    private static int _slotCount, _liveCount, _liveVersion;
    private static bool _seeded;
    private static readonly SlowBook<IntPtr> Slows = new();
    private const float ZonePassSeconds = 0.1f;
    private static readonly List<SlowZoneLease> Zones = new();
    private static readonly List<IntPtr> ZoneGone = new();
    private static float _nextZonePass;

    /// <summary>Every living enemy in the level, whatever its distance: the slot index is the position in the span, a vacant slot is null,
    /// and a slot keeps its enemy for as long as the enemy lives (so a slot can index a mod's own per-enemy arrays). A vacated slot is
    /// reused by a later enemy; <see cref="Generation"/> tells the two apart. Enemies are tracked from the game's own spawn, death,
    /// despawn and object destruction from the first read of this property, and the enemies already in the level at that moment are
    /// found then; a read itself checks nothing. The span is valid until the next enemy spawns.</summary>
    public static ReadOnlySpan<EnemyAgent?> Live
    {
        get
        {
            EnemyLifeHooks.Ensure();
            if (!_seeded)
            {
                _seeded = true;
                foreach (var enemy in UnityEngine.Object.FindObjectsOfType<EnemyAgent>()) if (enemy != null && enemy.Alive) Register(enemy);
            }
            return _live.AsSpan(0, _slotCount);
        }
    }

    /// <summary>How many slots <see cref="Live"/> has holding an enemy.</summary>
    public static int LiveCount => _liveCount;

    /// <summary>Changes whenever an enemy takes or leaves a slot, so a reader can tell that its picture of <see cref="Live"/> is stale.</summary>
    public static int Version => _liveVersion;

    /// <summary>Starts at 1 and goes up each time <paramref name="slot"/> is vacated: the same slot with the same generation is the same enemy.</summary>
    public static int Generation(int slot) => (uint)slot < (uint)_slotCount ? _generations[slot] : 0;

    /// <summary>Every living enemy with a damageable limb inside the sphere, each once however many limbs are inside. Never
    /// truncated: the query grows until it holds every collider. The span is valid until the next call.</summary>
    public static ReadOnlySpan<EnemyAgent?> Near(Vector3 position, float radius)
    {
        EnemyLifeHooks.Ensure();
        var hits = Physics.OverlapSphereNonAlloc(position, radius, _overlap, LayerManager.MASK_ENEMY_DAMAGABLE, QueryTriggerInteraction.Collide);
        while (hits == _overlap.Length)
        {
            _overlap = new Il2CppReferenceArray<Collider>(checked(_overlap.Length * 2));
            hits = Physics.OverlapSphereNonAlloc(position, radius, _overlap, LayerManager.MASK_ENEMY_DAMAGABLE, QueryTriggerInteraction.Collide);
        }
        if (_found.Length < hits) Array.Resize(ref _found, hits);
        var count = 0;
        FoundPointers.Clear();
        for (var i = 0; i < hits; i++)
        {
            var collider = _overlap[i];
            if (collider == null) continue;
            var id = collider.GetInstanceID();
            EnemyAgent? enemy;
            if (Owners.TryGetValue(id, out var cached) && cached.Collider == collider && cached.Enemy != null
                && !cached.Enemy.WasCollected && collider.transform.IsChildOf(cached.Enemy.transform))
                enemy = cached.Enemy;
            else
            {
                Owners.Remove(id);
                enemy = collider.GetComponentInParent<IDamageable>()?.GetBaseAgent()?.TryCast<EnemyAgent>();
                // Misses are not cached: a pooled collider may get its damage receiver later.
                if (enemy != null) Owners[id] = (collider, enemy);
            }
            if (enemy == null || !enemy.Alive) continue;
            if (!FoundPointers.Add(enemy.Pointer)) continue;
            _found[count++] = enemy;
        }
        return _found.AsSpan(0, count);
    }

    /// <summary>The living enemies of <see cref="Near"/> nearest first, at most <paramref name="limit"/> of them (the nearest ones when
    /// more are in range; distance from <paramref name="position"/> to each enemy's position). The span is valid until the next call.</summary>
    public static ReadOnlySpan<EnemyAgent?> Nearest(Vector3 position, float radius, int limit)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var count = Near(position, radius).Length;
        var keep = Math.Min(limit, count);
        if (_distances.Length < count) Array.Resize(ref _distances, Math.Max(count, _distances.Length * 2));
        // Insertion into a sorted prefix of at most `keep` entries; an entry is only written at an index below its own.
        var sorted = 0;
        for (var i = 0; i < count; i++)
        {
            var enemy = _found[i]!;
            var distance = (enemy.Position - position).sqrMagnitude;
            if (sorted == keep && (keep == 0 || distance >= _distances[keep - 1])) continue;
            var at = sorted < keep ? sorted++ : keep - 1;
            while (at > 0 && _distances[at - 1] > distance)
            {
                _found[at] = _found[at - 1];
                _distances[at] = _distances[at - 1];
                at--;
            }
            _found[at] = enemy;
            _distances[at] = distance;
        }
        return _found.AsSpan(0, keep);
    }

    /// <summary>The living enemy <paramref name="collider"/> belongs to, or null.</summary>
    public static EnemyAgent? FromCollider(Collider? collider)
    {
        var enemy = collider == null ? null : collider.GetComponentInParent<IDamageable>()?.GetBaseAgent()?.TryCast<EnemyAgent>();
        return enemy != null && enemy.Alive ? enemy : null;
    }

    /// <summary>The living enemy <paramref name="collider"/> belongs to, when it is in <paramref name="context"/>'s dimension.</summary>
    public static EnemyAgent? FromCollider(Collider? collider, Agent context)
    {
        var enemy = FromCollider(collider);
        return enemy != null && enemy.DimensionIndex == context.DimensionIndex ? enemy : null;
    }

    /// <summary>Slows <paramref name="agent"/> to <paramref name="factor"/> (0..1) of its own speed until the lease is disposed. The game's
    /// enemies are driven by their animator (root motion; the C-foam state stops them the same way), so this scales the animator speed: on
    /// the host that slows the real movement and attack timing, which the game replicates, and on other peers it keeps the animation in step
    /// with the host's positions. Several leases on one enemy never compound: the slowest wins. The speed the enemy had is put back when the
    /// last lease ends or the enemy dies or is reused; a speed the game set meanwhile (foam, ladders) is left alone. Call
    /// <see cref="SlowLease.Refresh"/> on a lease still wanted to re-apply it after the game changed the speed. <paramref name="owner"/> names
    /// the caller in error lines. An enemy without a live animator gets an inactive lease and an error line. Main thread only.</summary>
    public static SlowLease Slow(EnemyAgent agent, float factor, string owner)
    {
        var lease = new SlowLease(agent, owner);
        var anim = agent == null || agent.WasCollected ? null : agent.Anim;
        if (anim == null || anim.WasCollected) return lease;
        EnemyLifeHooks.Ensure();
        var want = Slows.Add(agent!.Pointer, anim.speed, factor, out var id);
        lease.Id = id;
        WriteSpeed(lease, anim, want);
        return lease;
    }

    internal static bool SlowHeld(SlowLease lease) => lease.Id != 0 && !lease.Enemy.WasCollected && Slows.Has(lease.Enemy.Pointer, lease.Id);

    internal static void SlowRefresh(SlowLease lease)
    {
        var anim = lease.Enemy.WasCollected ? null : lease.Enemy.Anim;
        if (anim == null || anim.WasCollected || lease.Id == 0 || !Slows.Refresh(lease.Enemy.Pointer, lease.Id, anim.speed, out var want)) return;
        if (Math.Abs(anim.speed - want) > SlowBook<IntPtr>.Tolerance) WriteSpeed(lease, anim, want);
    }

    internal static void SlowEnd(SlowLease lease)
    {
        if (lease.Id == 0 || lease.Enemy.WasCollected) return;
        var anim = lease.Enemy.Anim;
        if (anim == null || anim.WasCollected) { Slows.RemoveAll(lease.Enemy.Pointer, 0f, out _); return; }
        if (Slows.Remove(lease.Enemy.Pointer, lease.Id, anim.speed, out var speed)) anim.speed = speed;
    }

    private static void WriteSpeed(SlowLease lease, Animator anim, float want)
    {
        anim.speed = want;
        lease.ReadBack = Math.Abs(anim.speed - want) <= SlowBook<IntPtr>.Tolerance;
        if (!lease.ReadBack) Plugin.Logger.ErrorOnce(lease.Owner + " set an enemy's animator speed to " + want + " but read " + anim.speed + " back; that enemy is not slowed.");
    }

    /// <summary>Slows every living enemy inside the sphere around <paramref name="centre"/> to <paramref name="factor"/> of its own speed
    /// for <paramref name="seconds"/>, through <see cref="Slow"/>: one lease per enemy while it is inside, taken the pass after it enters
    /// and ended the pass after it leaves (a pass runs every 0.1 s from ForgeCore's own frame update), so overlapping zones and other
    /// leases never compound (the slowest wins). An enemy the game holds in foam (<c>ES_StateEnum.StuckInGlue</c>) is skipped: the foam
    /// owns its speed until it wears off. A lease the game ended (the enemy died or was reused) is taken afresh. The zone ends by itself
    /// after <paramref name="seconds"/> (<c>Time.time</c>), on <see cref="SlowZoneLease.Dispose"/> and at level cleanup, and every lease
    /// ends with it. <see cref="SlowZoneLease.Slowed"/> and <see cref="SlowZoneLease.Mismatches"/> report what was actually written and
    /// read back. Call it on every peer that should see the enemies slowed: the speed is the animator's on that peer. Main thread only.</summary>
    public static SlowZoneLease SlowZone(Vector3 centre, float radius, float factor, float seconds, string owner)
    {
        if (!(radius > 0f)) throw new ArgumentOutOfRangeException(nameof(radius));
        if (!(seconds > 0f)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!(factor >= 0f)) throw new ArgumentOutOfRangeException(nameof(factor));
        var zone = new SlowZoneLease(centre, radius, factor, Time.time + seconds, owner);
        Zones.Add(zone);
        _nextZonePass = 0f;
        return zone;
    }

    /// <summary>One pass of every zone, no more than every 0.1 s; zones past their end are ended first.</summary>
    internal static void TickZones(float now)
    {
        if (Zones.Count == 0 || now < _nextZonePass) return;
        _nextZonePass = now + ZonePassSeconds;
        for (var i = Zones.Count - 1; i >= 0; i--)
            if (now >= Zones[i].Ends) ZoneEnd(Zones[i]);
        foreach (var zone in Zones)
        {
            zone.Pass++;
            foreach (var found in Near(zone.Centre, zone.Radius))
            {
                var enemy = found!;
                var anim = enemy.Anim;
                if (anim == null || anim.WasCollected) continue;
                var locomotion = enemy.Locomotion;
                if (locomotion != null && locomotion.CurrentStateEnum == ES_StateEnum.StuckInGlue) continue;
                var key = enemy.Pointer;
                if (zone.Leases.TryGetValue(key, out var held))
                {
                    if (held.Lease.Active) held.Lease.Refresh();
                    else held.Lease = Slow(enemy, zone.Factor, zone.Owner);
                }
                else zone.Leases[key] = held = new SlowZoneLease.Held { Lease = Slow(enemy, zone.Factor, zone.Owner) };
                held.Pass = zone.Pass;
                if (held.Lease.Active && held.Lease.ReadBack) zone.SlowedSet.Add(key);
                else if (!held.Lease.ReadBack) zone.Mismatches++;
            }
            ZoneGone.Clear();
            foreach (var pair in zone.Leases)
                if (pair.Value.Pass != zone.Pass) ZoneGone.Add(pair.Key);
            foreach (var key in ZoneGone)
            {
                zone.Leases.Remove(key, out var left);
                left!.Lease.Dispose();
            }
            ZoneGone.Clear();
        }
    }

    internal static void ZoneEnd(SlowZoneLease zone)
    {
        if (!zone.Active) return;
        zone.Active = false;
        Zones.Remove(zone);
        foreach (var held in zone.Leases.Values) held.Lease.Dispose();
        zone.Leases.Clear();
    }
    /// <summary>How alert <paramref name="enemy"/> is; <see cref="EnemyAlert.Off"/> for a dead enemy, one without an AI or one in the
    /// game's <c>Off</c> mode.</summary>
    public static EnemyAlert Alert(EnemyAgent enemy) => !enemy.Alive || enemy.AI == null ? EnemyAlert.Off : enemy.AI.Mode switch
    {
        AgentMode.Off => EnemyAlert.Off,
        AgentMode.Hibernate => EnemyAlert.Sleeping,
        AgentMode.Agressive => EnemyAlert.Hunting,
        _ => EnemyAlert.Awake,
    };

    /// <summary>Takes a slot for a spawned enemy. Whether it lives is not asked here: the game's alive flag may not be set yet when its
    /// setup returns, so a spawn is registered whatever it reads then, and <see cref="Alert"/> says <see cref="EnemyAlert.Off"/> for one that is not alive.</summary>
    internal static void Register(EnemyAgent enemy)
    {
        if (enemy == null || SlotOf.ContainsKey(enemy.Pointer)) return;
        int slot;
        if (Vacant.Count > 0) slot = Vacant.Pop();
        else
        {
            slot = _slotCount++;
            if (slot >= _live.Length)
            {
                Array.Resize(ref _live, _live.Length * 2);
                Array.Resize(ref _generations, _live.Length);
            }
            _generations[slot] = 1;
        }
        _live[slot] = enemy;
        SlotOf.Add(enemy.Pointer, slot);
        _liveCount++;
        _liveVersion++;
    }

    internal static void Unregister(EnemyAgent enemy)
    {
        if (enemy == null || !SlotOf.Remove(enemy.Pointer, out var slot)) return;
        _live[slot] = null;
        _generations[slot]++;
        Vacant.Push(slot);
        _liveCount--;
        _liveVersion++;
    }

    /// <summary>Setup also runs when the game reuses an enemy, and death ends its limbs' ownership: forget its cached colliders.</summary>
    internal static void Forget(EnemyAgent enemy)
    {
        if (Slows.Count > 0)
        {
            var anim = enemy.WasCollected ? null : enemy.Anim;
            var live = anim != null && !anim.WasCollected;
            if (Slows.RemoveAll(enemy.Pointer, live ? anim!.speed : 0f, out var speed) && live) anim!.speed = speed;
        }
        InvalidOwners.Clear();
        foreach (var entry in Owners)
            if (entry.Value.Enemy == null || entry.Value.Collider == null || entry.Value.Enemy.Pointer == enemy.Pointer)
                InvalidOwners.Add(entry.Key);
        foreach (var id in InvalidOwners) Owners.Remove(id);
        InvalidOwners.Clear();
    }

    internal static void Clear()
    {
        // A zone's leases are entries of Slows, which is dropped below; the zones only need to stop.
        foreach (var zone in Zones) { zone.Active = false; zone.Leases.Clear(); }
        Zones.Clear();
        _nextZonePass = 0f;
        Slows.Clear();
        Array.Clear(_found, 0, _found.Length);
        Array.Clear(_live, 0, _live.Length);
        _slotCount = _liveCount = 0;
        _seeded = false;
        SlotOf.Clear();
        Vacant.Clear();
        FoundPointers.Clear();
        Owners.Clear();
        InvalidOwners.Clear();
    }
}

/// <summary>Subscribed by <see cref="Enemies.Near"/> and <see cref="Enemies.Live"/> the first time either runs, to the enemy hooks of
/// <see cref="GameHooks"/>.</summary>
internal static class EnemyLifeHooks
{
    private static bool _on;

    internal static void Ensure()
    {
        if (_on) return;
        _on = true;
        GameHooks.EnemySetup.Before(static enemy =>
        {
            try { Enemies.Forget(enemy); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Forgetting a reused enemy's limbs failed: " + error); }
        });
        GameHooks.EnemySetup.After(static (enemy, _) =>
        {
            try { Enemies.Register(enemy); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Registering a spawned enemy failed: " + error); }
        });
        GameHooks.EnemyDead.Before(static enemy => Gone(enemy));
        // An enemy the game removes without a death (a despawn, or its object destroyed) leaves the registry here, so no
        // read of Enemies.Live has to look for collected enemies.
        GameHooks.EnemyDespawned.Before(static enemy => Gone(enemy));
        GameHooks.EnemyDestroyed.Before(static enemy => Gone(enemy));
    }

    private static void Gone(EnemyAgent enemy)
    {
        try { Enemies.Forget(enemy); Enemies.Unregister(enemy); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Forgetting an enemy that left the level failed: " + error); }
    }
}
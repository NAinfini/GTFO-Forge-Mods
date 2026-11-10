using System;
using System.Collections.Generic;
using Enemies;
using UnityEngine;
using ForgeCore;
using Foes = ForgeCore.Enemies;

namespace ForgeReplay;

/// <summary>Records every living enemy: entity number 4 + the slot ForgeCore's <c>Foes.Live</c> gives it (a slot keeps its enemy until
/// it dies or despawns, a reused slot has a new generation). Spawn and the end of an enemy are events; position, rotation and a state byte
/// (alert in the low two bits, health in 32 steps above) are samples taken per enemy: 20 Hz while it hunts, 10 Hz awake, once a second
/// asleep (<see cref="Hot(int)"/> brings a sleeper that woke, was hurt or senses someone back at once). A sample that did not change is not
/// written by the encoder, so a sleeping enemy costs nothing after its first sample.</summary>
internal static class EnemyRecorder
{
    internal const int FirstEntity = 4, MaxSlots = 1024;
    private const uint HuntingIntervalMs = 50, AwakeIntervalMs = 100, SleepingIntervalMs = 1000;

    private static readonly EnemyAgent?[] Agents = new EnemyAgent?[MaxSlots];
    private static readonly IntPtr[] Pointers = new IntPtr[MaxSlots];
    private static readonly int[] Generations = new int[MaxSlots];
    private static readonly int[] LocomotionStates = new int[MaxSlots];
    // Every property that returns a game object makes a new interop wrapper on each read, so the ones sampled every tick are read once per enemy.
    private static readonly Transform?[] Transforms = new Transform?[MaxSlots];
    private static readonly Dam_EnemyDamageBase?[] Damages = new Dam_EnemyDamageBase?[MaxSlots];
    private static readonly EnemyLocomotion?[] Locomotions = new EnemyLocomotion?[MaxSlots];
    private static readonly Dictionary<IntPtr, int> EntityOf = new();
    // The values that do not change while an enemy lives, read once when it is taken up: every keyframe writes them again for every enemy.
    private static readonly ulong[] DataIds = new ulong[MaxSlots], HealthTenths = new ulong[MaxSlots], SizeMilli = new ulong[MaxSlots];
    private static readonly int[] Dimensions = new int[MaxSlots], Seeds = new int[MaxSlots];
    // When each enemy is next sampled; one above the highest slot ever tracked (it bounds every loop); the ForgeCore list version last tracked.
    private static readonly uint[] NextMs = new uint[MaxSlots], HotUntil = new uint[MaxSlots];
    private const uint HotMs = 2000;
    private static int _high, _trackedVersion = -1;
    private static readonly long[] LastMind = new long[MaxSlots];
    private static readonly int[] LastMode = new int[MaxSlots];
    private static readonly EnemyAI?[] Minds = new EnemyAI?[MaxSlots];
    // Reading what an enemy has in mind costs a few interop reads: at most this many every tick, round the slots.
    private const int MindsPerTick = 24;
    private static int _mindCursor;

    // Enemies the game declared dead (EnemyAgent.OnDead), by object: an enemy whose object is already gone when it is looked at (a big one whose corpse is
    // removed at once, a kill that despawns) still ended in a death, and a replay that read it as a despawn leaves nothing to play the death with.
    private static readonly HashSet<IntPtr> Died = new();

    internal static void NoteDead(EnemyAgent enemy)
    {
        if (Rec.On && enemy != null && !enemy.WasCollected && EntityOf.ContainsKey(enemy.Pointer)) Died.Add(enemy.Pointer);
    }

    // The replicated id of every tracked enemy (EnemyAgent.GlobalID, the same number on every machine) to its entity here, and the entities of enemies that ended
    // in the last <see cref="GoneKeepMs"/>: a host record about a kill arrives a moment after the enemy is gone.
    private const uint GoneKeepMs = 3000;
    private static readonly Dictionary<ushort, int> EntityOfKey = new();
    private static readonly ushort[] Keys = new ushort[MaxSlots];
    private static readonly Dictionary<ushort, (int Entity, uint At)> GoneKeys = new();

    /// <summary>The entity of the enemy the host calls <paramref name="key"/>, or -1 when this recording has none (or it ended long ago).</summary>
    internal static int EntityOfGlobal(ushort key)
    {
        if (EntityOfKey.TryGetValue(key, out var entity)) return entity;
        return GoneKeys.TryGetValue(key, out var gone) && PlayerRecorder.Now - gone.At <= GoneKeepMs ? gone.Entity : -1;
    }

    /// <summary>The replicated id of the enemy with <paramref name="entity"/> in this recording, false when there is none.</summary>
    internal static bool KeyOf(int entity, out ushort key)
    {
        var slot = entity - FirstEntity;
        key = (uint)slot < MaxSlots ? Keys[slot] : (ushort)0;
        return (uint)slot < MaxSlots && Agents[slot] != null;
    }

    internal static void Reset()
    {
        EntityOfKey.Clear(); GoneKeys.Clear();
        Died.Clear();
        Array.Clear(Agents); Array.Clear(Transforms); Array.Clear(Damages); Array.Clear(Locomotions); Array.Clear(Pointers); Array.Clear(Generations); Array.Fill(LocomotionStates, -1); EntityOf.Clear(); Array.Clear(Minds); Array.Fill(LastMind, -1L); _mindCursor = 0;
        Array.Clear(NextMs); Array.Clear(HotUntil); _high = 0; _trackedVersion = -1;
    }

    /// <summary>Samples <paramref name="enemy"/> on the next tick whatever its alert: it woke, was hurt or senses someone, and a sleeper is
    /// otherwise sampled only once a second.</summary>
    internal static void Hot(EnemyAgent? enemy)
    {
        if (enemy != null && EntityOf.TryGetValue(enemy.Pointer, out var entity)) Hot(entity);
    }

    /// <summary><see cref="Hot(EnemyAgent?)"/> for an entity number the recording already gave. For the next <see cref="HotMs"/> the enemy
    /// is sampled at the awake rate at least: the game changes its alert and starts it moving a moment after the call that woke it.</summary>
    internal static void Hot(int entity)
    {
        var slot = entity - FirstEntity;
        if ((uint)slot >= MaxSlots) return;
        NextMs[slot] = 0; HotUntil[slot] = PlayerRecorder.Now + HotMs;
    }

    private static uint IntervalOf(EnemyAlert alert, int slot, uint now)
    {
        var interval = alert switch { EnemyAlert.Hunting => HuntingIntervalMs, EnemyAlert.Awake => AwakeIntervalMs, _ => SleepingIntervalMs };
        if (now < HotUntil[slot]) interval = Math.Min(interval, AwakeIntervalMs);
        // Spread the sleepers over the second so they are not all read in one frame.
        return interval + (uint)(slot * 37 % (interval / 4 + 1));
    }

    /// <summary>The entity number of a live enemy, or -1 when the recording does not know it (yet).</summary>
    internal static int Entity(EnemyAgent? enemy)
    {
        if (enemy == null) return -1;
        if (EntityOf.TryGetValue(enemy.Pointer, out var entity)) return entity;
        Track(PlayerRecorder.Pipeline, PlayerRecorder.Now);
        return EntityOf.TryGetValue(enemy.Pointer, out entity) ? entity : -1;
    }

    internal static void Tick(BlockPipeline pipeline, uint now)
    {
        Track(pipeline, now);
        for (var slot = 0; slot < _high; slot++)
        {
            var agent = Agents[slot];
            if (NativeObjects.Gone(agent) || now < NextMs[slot]) continue;
            SampleOne(pipeline, now, slot, agent);
        }
        ReadMinds();
        Hooks.Projectiles.Sample();
    }

    // 69 EnemyMind: U entity, U the player it hunts: slot + 1 (0 none; on a client it is mostly none), U 1 tagged by a tracker + 2 a sleeper sensing someone, U glue percent.
    private static void ReadMinds()
    {
        var done = 0;
        try
        {
            for (var read = 0; read < _high && done < MindsPerTick; read++)
            {
                var slot = _mindCursor % _high;
                _mindCursor = (slot + 1) % _high;
                var agent = Agents[slot];
                if (NativeObjects.Gone(agent)) continue;
                WriteMind(slot, agent);
                done++;
            }
        }
        catch (Exception e) { Rec.Fail("enemy-mind", e); }
    }

    /// <summary>Writes what <paramref name="enemy"/> has in mind now, when it changed (a sleeper's sensing starting or ending).</summary>
    internal static void Mind(EnemyAgent? enemy)
    {
        var entity = Entity(enemy);
        if (entity < FirstEntity || Agents[entity - FirstEntity] is not { } agent) return;
        // A sleeper that senses someone (or calms down) may wake next: it is sampled at once.
        Hot(entity);
        try { WriteMind(entity - FirstEntity, agent); }
        catch (Exception e) { Rec.Fail("enemy-mind", e); }
    }

    // No try/catch here: the two per-frame callers guard a whole batch, which kept this writer out of one guarded call per enemy per frame.
    private static void WriteMind(int slot, EnemyAgent agent)
    {
        var ai = Minds[slot] ??= agent.AI;
        // The AI mode changes while the enemy lives (asleep, hunting, scouting): written when it does, not left to the next keyframe.
        if ((ai == null ? 0 : (int)ai.Mode) != LastMode[slot]) WriteLook(slot, agent);
        var target = ai?.Target?.m_agent?.TryCast<Player.PlayerAgent>();
        var flags = (agent.IsTagged ? 1 : 0) | (agent.IsHibernationDetecting ? 2 : 0);
        var glue = Mathf.RoundToInt(Mathf.Clamp01(Damages[slot]?.AttachedGlueRel ?? 0f) * 100f);
        var mind = (long)(target == null ? 0 : target.PlayerSlotIndex + 1) | (long)flags << 8 | (long)glue << 16;
        if (mind == LastMind[slot]) return;
        LastMind[slot] = mind;
        Rec.Begin().U((ulong)(FirstEntity + slot)).U((ulong)(mind & 255)).U((ulong)flags).U((ulong)glue);
        Rec.Emit(EventKind.EnemyMind);
    }

    // Called at the start of every keyframe block: every present enemy is announced again and sampled in full.
    internal static void Full(BlockPipeline pipeline, uint now)
    {
        try
        {
            for (var slot = 0; slot < _high; slot++)
            {
                var agent = Agents[slot];
                if (NativeObjects.Gone(agent)) continue;
                WriteSpawn(slot, agent);
                LocomotionStates[slot] = -1;
                LastMind[slot] = -1;
                WriteMind(slot, agent);
                SampleOne(pipeline, now, slot, agent);
            }
        }
        catch (Exception e) { Rec.Fail("enemy-keyframe", e); }
    }

    // Compares ForgeCore's live list with what was announced: new enemies are spawned, vacated or reused slots are ended. ForgeCore changes
    // the list's version whenever an enemy takes or leaves a slot, and an enemy that dies leaves it at its death, so nothing is read until
    // the version moves; a spawned enemy the game has not yet marked alive is looked at again on the next tick.
    private static void Track(BlockPipeline? pipeline, uint now)
    {
        if (pipeline == null || Foes.Version == _trackedVersion) return;
        _trackedVersion = Foes.Version;
        var live = Foes.Live;
        var count = Math.Min(live.Length, MaxSlots);
        var upto = Math.Max(count, _high);
        for (var slot = 0; slot < upto; slot++)
        {
            var agent = slot < count ? live[slot] : null;
            var known = Agents[slot];
            // A tracked enemy whose object is already destroyed ends too (Alive reads false for it): skipping it left the slot looking free with no end
            // written, so a replay never took that enemy out (a big enemy whose corpse is removed at once stayed standing in the replay).
            if (known != null && (NativeObjects.Gone(agent) || Foes.Generation(slot) != Generations[slot] || !Alive(known))) End(pipeline, slot, known);
            if (NativeObjects.Gone(agent) || Agents[slot] != null) continue;
            if (!Alive(agent)) { _trackedVersion = -1; continue; }
            Died.Remove(agent.Pointer);
            Agents[slot] = agent; Pointers[slot] = agent.Pointer; Transforms[slot] = agent.transform; Damages[slot] = agent.Damage; Locomotions[slot] = agent.Locomotion; Generations[slot] = Foes.Generation(slot); LocomotionStates[slot] = -1;
            Minds[slot] = agent.AI;
            DataIds[slot] = agent.EnemyDataID; Dimensions[slot] = (int)agent.DimensionIndex; HealthTenths[slot] = (ulong)Math.Max(0, (int)Math.Round((Damages[slot]?.HealthMax ?? 0f) * 10f));
            SizeMilli[slot] = (ulong)Mathf.Max(0, Mathf.RoundToInt(agent.SizeMultiplier * 1000f)); Seeds[slot] = agent.Seed;
            EntityOf[agent.Pointer] = FirstEntity + slot;
            Keys[slot] = agent.GlobalID; EntityOfKey[Keys[slot]] = FirstEntity + slot;
            _high = Math.Max(_high, slot + 1);
            WriteSpawn(slot, agent);
            LastMind[slot] = -1;
            try { WriteMind(slot, agent); }
            catch (Exception e) { Rec.Fail("enemy-mind", e); }
            SampleOne(pipeline, now, slot, agent);
        }
        if (live.Length > MaxSlots) Plugin.Logger.ErrorOnce("More than " + MaxSlots + " enemy slots are in use; the rest are not recorded.");
    }

    // A dead enemy leaves ForgeCore's list when it dies; this also tells a spawn the game has not yet marked alive.
    private static bool Alive(EnemyAgent agent)
    {
        return !NativeObjects.Gone(agent) && agent.Alive;
    }

    private static void End(BlockPipeline pipeline, int slot, EnemyAgent agent)
    {
        var died = Died.Remove(Pointers[slot]) || !NativeObjects.Gone(agent) && !agent.Alive; // an object gone with no death declared counts as a despawn
        Rec.Begin().U((ulong)(FirstEntity + slot)).U(died ? 1UL : 0UL);
        Rec.Emit(EventKind.EnemyGone);
        pipeline.Forget(FirstEntity + slot);
        EntityOf.Remove(Pointers[slot]);
        var key = Keys[slot];
        var now = PlayerRecorder.Now;
        if (EntityOfKey.TryGetValue(key, out var mine) && mine == FirstEntity + slot) EntityOfKey.Remove(key);
        if (GoneKeys.Count > 256) { var old = new List<ushort>(); foreach (var pair in GoneKeys) if (now - pair.Value.At > GoneKeepMs) old.Add(pair.Key); foreach (var k in old) GoneKeys.Remove(k); }
        GoneKeys[key] = (FirstEntity + slot, now);
        Agents[slot] = null; Pointers[slot] = IntPtr.Zero; Transforms[slot] = null; Damages[slot] = null; Locomotions[slot] = null; Minds[slot] = null; LastMind[slot] = -1;
    }

    private static void WriteSpawn(int slot, EnemyAgent agent)
    {
        var p = agent.Position;
        Rec.Begin().U((ulong)(FirstEntity + slot)).U(DataIds[slot]).U((ulong)Dimensions[slot]).Pos(p.x, p.y, p.z).U(HealthTenths[slot]);
        Rec.Emit(EventKind.EnemySpawn);
        WriteLook(slot, agent);
    }

    // 71 EnemyLook: U entity, U size x 1000, S seed, U AgentMode it is in.
    private static void WriteLook(int slot, EnemyAgent agent)
    {
        var ai = Minds[slot] ??= agent.AI;
        LastMode[slot] = ai == null ? 0 : (int)ai.Mode;
        Rec.Begin().U((ulong)(FirstEntity + slot)).U(SizeMilli[slot]).S(Seeds[slot]).U((ulong)LastMode[slot]);
        Rec.Emit(EventKind.EnemyLook);
    }

    private static void SampleOne(BlockPipeline pipeline, uint now, int slot, EnemyAgent agent)
    {
        var p = agent.Position;
        var r = Transforms[slot]!.rotation;
        var damage = Damages[slot];
        var health = damage == null || damage.HealthMax <= 0f ? 0 : Mathf.Clamp(Mathf.RoundToInt(31f * damage.Health / damage.HealthMax), 0, 31);
        // The enemy's own AI and locomotion, read once at its spawn, say how alert it is: nothing is made here, and the alert sets when it is sampled next.
        var alert = Foes.Alert(true, Minds[slot] ??= agent.AI, Locomotions[slot]);
        NextMs[slot] = now + IntervalOf(alert, slot, now);
        pipeline.Sample(now, FirstEntity + slot, p.x, p.y, p.z, r.x, r.y, r.z, r.w, (byte)((int)alert | (health << 2)));
        // What the enemy is doing (walking, climbing, jumping, stuck in glue, knocked down ...): its locomotion state, written when it changes.
        var state = (int)Locomotions[slot]!.CurrentStateEnum;
        if (state == LocomotionStates[slot]) return;
        LocomotionStates[slot] = state;
        Rec.Begin().U((ulong)(FirstEntity + slot)).U((ulong)state);
        Rec.Emit(EventKind.EnemyState);
    }
}


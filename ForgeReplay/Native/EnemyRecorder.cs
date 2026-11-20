using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Agents;
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
    // The native objects behind the agent's parts (each held by its wrapper above, so the address stays): a sample reads their fields in
    // place (Fields) and turns through the engine's own call, so it calls nothing in the runtime and boxes nothing. Zero when absent.
    private static readonly IntPtr[] TransformAt = new IntPtr[MaxSlots], DamageAt = new IntPtr[MaxSlots], LocomotionAt = new IntPtr[MaxSlots], MindAt = new IntPtr[MaxSlots];
    // False when the engine lacks its rotation call (logged once): the samples read the rotation through the wrapper.
    private static bool _engineCalls;

    // The byte offsets of the fields a sample reads, resolved once. Each read is what the getter it replaces returns (the game's own
    // bodies: EnemyAgent.Position is m_position, AgentAI.Mode is m_mode, Dam_SyncedDamageBase.Health is its backing field,
    // EnemyLocomotion.CurrentStateEnum is m_currentState.m_stateEnum, AgentAI.Target is m_target).
    private static class Fields
    {
        internal static readonly int Position = NativeObjects.FieldOffset<Agent>("m_position");
        internal static readonly int Mode = NativeObjects.FieldOffset<AgentAI>("m_mode");
        internal static readonly int Health = NativeObjects.FieldOffset<Dam_SyncedDamageBase>("<Health>k__BackingField");
        internal static readonly int State = NativeObjects.FieldOffset<EnemyLocomotion>("m_currentState");
        internal static readonly int StateEnum = NativeObjects.FieldOffset<ES_Base>("m_stateEnum");
        internal static readonly int Target = NativeObjects.FieldOffset<AgentAI>("m_target");
        internal static readonly int TargetAgent = NativeObjects.FieldOffset<AgentTarget>("m_agent");
    }

    private static float ReadFloat(IntPtr at, int offset) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(at, offset));

    // The locomotion state (ES_StateEnum, a byte); 0 when the enemy has no locomotion or no state yet.
    private static int LocomotionState(int slot)
    {
        var locomotion = LocomotionAt[slot];
        var state = locomotion == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(locomotion, Fields.State);
        return state == IntPtr.Zero ? 0 : Marshal.ReadByte(state, Fields.StateEnum);
    }

    // The AI mode (AgentMode, a byte), from the AI's object.
    private static AgentMode ModeAt(IntPtr mind) => (AgentMode)Marshal.ReadByte(mind, Fields.Mode);

    private static IntPtr PointerOf(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase? o) => o is null ? IntPtr.Zero : o.Pointer;
    private static readonly Dictionary<IntPtr, int> EntityOf = new();
    // The values that do not change while an enemy lives, read once when it is taken up: every keyframe writes them again for every enemy.
    private static readonly ulong[] DataIds = new ulong[MaxSlots], HealthTenths = new ulong[MaxSlots], SizeMilli = new ulong[MaxSlots];
    private static readonly int[] Dimensions = new int[MaxSlots], Seeds = new int[MaxSlots];
    // The health an enemy has when whole, read once when it is taken up (the spawn event writes it as HealthTenths): every sample divides by it.
    private static readonly float[] MaxHealth = new float[MaxSlots];
    // When each enemy is next sampled; one above the highest slot ever tracked (it bounds every loop); the ForgeCore list version last tracked.
    private static readonly uint[] NextMs = new uint[MaxSlots], HotUntil = new uint[MaxSlots];
    private const uint HotMs = 2000;
    private static int _high, _trackedVersion = -1;
    private static readonly long[] LastMind = new long[MaxSlots];
    private static readonly int[] LastMode = new int[MaxSlots];
    private static readonly EnemyAI?[] Minds = new EnemyAI?[MaxSlots];

    // The enemy's AI, read once: taken up with the enemy, or at the first read after a spawn that had none yet; cleared when the slot ends.
    private static EnemyAI? MindOf(int slot, EnemyAgent agent)
    {
        if (Minds[slot] is { } mind) return mind;
        mind = Minds[slot] = agent.AI;
        MindAt[slot] = PointerOf(mind);
        return mind;
    }
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
        Array.Clear(TransformAt); Array.Clear(DamageAt); Array.Clear(LocomotionAt); Array.Clear(MindAt);
        Array.Clear(NextMs); Array.Clear(HotUntil); _high = 0; _trackedVersion = -1;
        _engineCalls = EngineCalls.Prepare(out var why);
        if (!_engineCalls) Plugin.Logger.ErrorOnce("replay-enemy-rotation-call-missing", "The engine's rotation call is missing (" + why + "); enemy samples read rotations through the slower wrapper.");
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
        // Most slots are not due this frame: the time is tested first, the object (one read in place) only for a due one.
        for (var slot = 0; slot < _high; slot++)
        {
            if (now < NextMs[slot] || NativeObjects.GoneAt(Pointers[slot])) continue;
            SampleOne(pipeline, now, slot, Agents[slot]!);
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
                if (NativeObjects.GoneAt(Pointers[slot])) continue;
                WriteMind(slot, Agents[slot]!);
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
        var ai = MindOf(slot, agent);
        var aiAt = MindAt[slot];
        // The AI mode changes while the enemy lives (asleep, hunting, scouting): written when it does, not left to the next keyframe.
        if ((ai is null ? 0 : (int)ModeAt(aiAt)) != LastMode[slot]) WriteLook(slot, agent);
        var flags = (agent.IsTagged ? 1 : 0) | (agent.IsHibernationDetecting ? 2 : 0);
        var glue = (int)MathF.Round(Math.Clamp(Damages[slot]?.AttachedGlueRel ?? 0f, 0f, 1f) * 100f);
        var mind = (long)HuntedSlot(ai, aiAt) | (long)flags << 8 | (long)glue << 16;
        if (mind == LastMind[slot]) return;
        LastMind[slot] = mind;
        Rec.Begin().U((ulong)(FirstEntity + slot)).U((ulong)(mind & 255)).U((ulong)flags).U((ulong)glue);
        Rec.Emit(EventKind.EnemyMind);
    }

    // The slot + 1 of the player the AI hunts (0 none): its target's agent read in place and matched against the players this tick read;
    // an agent that is none of theirs is looked at through its wrapper, as every read was before.
    private static int HuntedSlot(EnemyAI? ai, IntPtr aiAt)
    {
        if (ai is null) return 0;
        var target = Marshal.ReadIntPtr(aiAt, Fields.Target);
        var agent = target == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(target, Fields.TargetAgent);
        if (agent == IntPtr.Zero) return 0;
        var slot = PlayerRecorder.SlotOfAgent(agent);
        if (slot >= 0) return slot + 1;
        var player = ai.Target?.m_agent?.TryCast<Player.PlayerAgent>();
        return NativeObjects.Gone(player) ? 0 : player.PlayerSlotIndex + 1;
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
            TransformAt[slot] = PointerOf(Transforms[slot]); DamageAt[slot] = PointerOf(Damages[slot]); LocomotionAt[slot] = PointerOf(Locomotions[slot]); MindAt[slot] = PointerOf(Minds[slot]);
            DataIds[slot] = agent.EnemyDataID; Dimensions[slot] = (int)agent.DimensionIndex; MaxHealth[slot] = Damages[slot]?.HealthMax ?? 0f; HealthTenths[slot] = (ulong)Math.Max(0, (int)Math.Round(MaxHealth[slot] * 10f));
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
        if (live.Length > MaxSlots) Plugin.Logger.ErrorOnce("replay-enemy-slots-exhausted", "More than " + MaxSlots + " enemy slots are in use; the rest are not recorded.");
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
        // Removing while enumerating a Dictionary is allowed (.NET Core 3.0 on), so the sweep makes nothing.
        if (GoneKeys.Count > 256) foreach (var pair in GoneKeys) if (now - pair.Value.At > GoneKeepMs) GoneKeys.Remove(pair.Key);
        GoneKeys[key] = (FirstEntity + slot, now);
        Agents[slot] = null; Pointers[slot] = IntPtr.Zero; Transforms[slot] = null; Damages[slot] = null; Locomotions[slot] = null; Minds[slot] = null; LastMind[slot] = -1;
        TransformAt[slot] = DamageAt[slot] = LocomotionAt[slot] = MindAt[slot] = IntPtr.Zero;
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
        var ai = MindOf(slot, agent);
        LastMode[slot] = ai == null ? 0 : (int)ai.Mode;
        Rec.Begin().U((ulong)(FirstEntity + slot)).U(SizeMilli[slot]).S(Seeds[slot]).U((ulong)LastMode[slot]);
        Rec.Emit(EventKind.EnemyLook);
    }

    // Every value is read in place from the objects taken up with the enemy (Fields), the rotation through the engine's own call, and the
    // maths is managed: the interop's getters, Unity's == and Mathf are calls into the game, and these run for every due enemy every frame.
    private static void SampleOne(BlockPipeline pipeline, uint now, int slot, EnemyAgent agent)
    {
        var at = Pointers[slot];
        float px = ReadFloat(at, Fields.Position), py = ReadFloat(at, Fields.Position + 4), pz = ReadFloat(at, Fields.Position + 8);
        var r = _engineCalls ? EngineCalls.Rotation(TransformAt[slot]) : Transforms[slot]!.rotation;
        var damage = DamageAt[slot];
        var max = MaxHealth[slot];
        var health = damage == IntPtr.Zero || max <= 0f ? 0 : Math.Clamp((int)MathF.Round(31f * ReadFloat(damage, Fields.Health) / max), 0, 31);
        // The enemy's own AI and locomotion, read once at its spawn, say how alert it is: nothing is made here, and the alert sets when it is sampled next.
        var state = LocomotionState(slot);
        var alert = MindOf(slot, agent) is null ? EnemyAlert.Off : Foes.Alert(ModeAt(MindAt[slot]), state);
        NextMs[slot] = now + IntervalOf(alert, slot, now);
        pipeline.Sample(now, FirstEntity + slot, px, py, pz, r.x, r.y, r.z, r.w, (byte)((int)alert | (health << 2)));
        // What the enemy is doing (walking, climbing, jumping, stuck in glue, knocked down ...): its locomotion state, written when it changes.
        if (state == LocomotionStates[slot]) return;
        LocomotionStates[slot] = state;
        Rec.Begin().U((ulong)(FirstEntity + slot)).U((ulong)state);
        Rec.Emit(EventKind.EnemyState);
    }
}


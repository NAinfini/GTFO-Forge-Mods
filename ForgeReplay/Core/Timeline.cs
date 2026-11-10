using System;
using System.Collections.Generic;
using ForgeCore;

namespace ForgeReplay;

/// <summary>One recorded ItemSpawn: when, which item (<c>ItemDataBlock</c> id or gear checksum, as the game spawned it), where, and the game's
/// <c>ItemMode</c> number it was spawned in.</summary>
internal readonly record struct ItemSpawned(uint TimeMs, uint ItemId, float X, float Y, float Z, uint Mode)
{
    /// <summary>The game's <c>ItemMode.Pickup</c>: an item lying in the level. The other modes (in hand, a placed deployable) are shown by the
    /// player's and the deployable's own events, never as a pickup on the ground.</summary>
    internal const uint PickupMode = 2;

    internal bool IsGroundPickup => Mode == PickupMode;
}

/// <summary>The latest sample of one entity and the sample after it, enough to place it between them. No next sample means none comes
/// within <see cref="Timeline.MaxStepMs"/>: an unchanged sample is not written, so the entity stands still.</summary>
internal struct Pose
{
    internal uint TimeMs, NextMs;
    internal float X, Y, Z, Qx, Qy, Qz, Qw, NextX, NextY, NextZ, NextQx, NextQy, NextQz, NextQw;
    internal byte State;
    internal bool Present, HasNext;
}

/// <summary>What is true at one moment of the recording: where every entity is, which enemies live, the latest state of every level object
/// and each player's status. Everything a jump has to restore; one-shot events (shots, hits, chat) are not state and are only reported
/// while playing.</summary>
internal sealed class WorldState
{
    internal const int MaxEntities = 4096;
    internal readonly Pose[] Poses = new Pose[MaxEntities];
    internal readonly Dictionary<int, (ulong DataId, ulong Dimension, float FullHealth)> Enemies = new();
    internal readonly Dictionary<(ushort Kind, ulong Key), byte[]> Objects = new();
    /// <summary>The latest payload of each event of <see cref="EventKind.IsExtraState"/>; read through <see cref="ReplayExtras"/>.</summary>
    internal readonly Dictionary<(ushort Kind, ulong Key), byte[]> Extras = new();
    internal readonly byte[]?[] PlayerStatus = new byte[]?[4];
    /// <summary>What each player carries as last recorded (pack, consumable, carried item, flashlight, pockets).</summary>
    internal readonly PlayerKit[] PlayerKits = new PlayerKit[4];
    /// <summary>Each player's walk animation values as last recorded (the game's <c>m_animVelFwd</c> and <c>m_animVelRight</c>).</summary>
    internal readonly float[] PlayerForward = new float[4], PlayerRight = new float[4];
    /// <summary>The step those values are recorded in.</summary>
    internal const float MotionStep = 0.05f;
    /// <summary>The enemy's locomotion state (ES_StateEnum) as last recorded; 0 when none has been recorded.</summary>
    internal readonly byte[] EnemyLocomotion = new byte[MaxEntities];
    /// <summary>The custom limbs each living enemy has lost so far, one bit per limb id (<see cref="MaxLimbId"/> and below); cleared when the
    /// enemy is gone, since the next enemy in that slot comes with the same entity number.</summary>
    internal readonly ulong[] LimbsGone = new ulong[MaxEntities];
    internal const int MaxLimbId = 63;
    /// <summary>Every item spawned in the level up to this moment (ItemSpawn events), in the order the recording has them: the index is
    /// the spawn's number across the whole recording.</summary>
    internal readonly List<ItemSpawned> ItemSpawns = new();

    internal void Clear()
    {
        Array.Clear(ShownOn);
        Array.Clear(Poses); Enemies.Clear(); Objects.Clear(); Extras.Clear(); Array.Clear(PlayerStatus); Array.Clear(PlayerKits); Array.Clear(PlayerForward); Array.Clear(PlayerRight); Array.Clear(EnemyLocomotion); Array.Clear(LimbsGone); ItemSpawns.Clear();
    }

    /// <summary>The bit of <paramref name="limb"/> in <see cref="LimbsGone"/>; 0 for a limb id above <see cref="MaxLimbId"/>.</summary>
    internal static ulong LimbBit(ulong limb) => limb <= MaxLimbId ? 1UL << (int)limb : 0UL;

    /// <summary>The position of an entity at <paramref name="timeMs"/>: interpolated between its latest sample and the next, held at the latest
    /// when no next one comes. False when the entity is not in the recording at this moment.</summary>
    internal bool Place(int entity, uint timeMs, out float x, out float y, out float z, out float qx, out float qy, out float qz, out float qw)
    {
        ref var p = ref Poses[entity];
        if (!p.Present) { x = y = z = qx = qy = qz = qw = 0; return false; }
        if (!p.HasNext || timeMs <= p.TimeMs)
        {
            x = p.X; y = p.Y; z = p.Z; qx = p.Qx; qy = p.Qy; qz = p.Qz; qw = p.Qw;
            return true;
        }
        var t = MathF.Min(1f, (timeMs - p.TimeMs) / (float)(p.NextMs - p.TimeMs));
        x = p.X + (p.NextX - p.X) * t; y = p.Y + (p.NextY - p.Y) * t; z = p.Z + (p.NextZ - p.Z) * t;
        // Normalized linear blend of the two rotations along the shorter arc: exact enough between samples 33 to 100 ms apart.
        var dot = p.Qx * p.NextQx + p.Qy * p.NextQy + p.Qz * p.NextQz + p.Qw * p.NextQw;
        var sign = dot < 0f ? -1f : 1f;
        qx = p.Qx + (p.NextQx * sign - p.Qx) * t; qy = p.Qy + (p.NextQy * sign - p.Qy) * t;
        qz = p.Qz + (p.NextQz * sign - p.Qz) * t; qw = p.Qw + (p.NextQw * sign - p.Qw) * t;
        var length = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        if (length > 1e-6f) { qx /= length; qy /= length; qz /= length; qw /= length; }
        return true;
    }

    // Where each entity is shown: its place eased towards Place. The recorded samples carry the time they were taken, not the time the game
    // moved the entity (the recorder's own player moves in the game's fixed steps and is sampled every frame; the game moves an enemy when its
    // update comes round), so the speed between two samples swings (over half again from one segment to the next for 26% of the enemies'
    // and 32% of the recorder's segments in the 38-A2 recording) and the line through them jerks. Eased over SmoothSeconds of recording time
    // (Unity's SmoothDamp), the step from one 60 fps frame to the next stays within half of the median of the nine frames round it for
    // 99.7% of the enemies' moving frames (95.3% unsmoothed), 99.9% of the recorder's (61.0%) and 98.5-99.7% of the bots' (96.3-97.9%),
    // 6 to 9 cm behind a moving player on average.
    internal const float SmoothSeconds = 0.06f, SnapMeters = 2f;
    private const uint SmoothGapMs = 250;
    private readonly float[] ShownX = new float[MaxEntities], ShownY = new float[MaxEntities], ShownZ = new float[MaxEntities];
    private readonly float[] ShownVx = new float[MaxEntities], ShownVy = new float[MaxEntities], ShownVz = new float[MaxEntities];
    private readonly uint[] ShownAt = new uint[MaxEntities];
    private readonly bool[] ShownOn = new bool[MaxEntities];

    /// <summary><see cref="Place"/> with the position eased (rotation as Place has it): what puppets and the camera use, so the two agree.
    /// The same <paramref name="timeMs"/> twice gives the same answer; a time before the last one, a gap over a quarter second or a place over
    /// <see cref="SnapMeters"/> away is taken at once. <see cref="Clear"/> (every jump) starts afresh.</summary>
    internal bool Shown(int entity, uint timeMs, out float x, out float y, out float z, out float qx, out float qy, out float qz, out float qw)
    {
        if (!Place(entity, timeMs, out x, out y, out z, out qx, out qy, out qz, out qw)) { ShownOn[entity] = false; return false; }
        var dx = x - ShownX[entity]; var dy = y - ShownY[entity]; var dz = z - ShownZ[entity];
        if (!ShownOn[entity] || timeMs < ShownAt[entity] || timeMs - ShownAt[entity] > SmoothGapMs || dx * dx + dy * dy + dz * dz > SnapMeters * SnapMeters)
        {
            ShownOn[entity] = true; ShownAt[entity] = timeMs;
            ShownX[entity] = x; ShownY[entity] = y; ShownZ[entity] = z; ShownVx[entity] = ShownVy[entity] = ShownVz[entity] = 0f;
            return true;
        }
        var dt = (timeMs - ShownAt[entity]) / 1000f;
        if (dt > 0f)
        {
            ShownAt[entity] = timeMs;
            ShownX[entity] = Ease(ShownX[entity], x, ref ShownVx[entity], dt);
            ShownY[entity] = Ease(ShownY[entity], y, ref ShownVy[entity], dt);
            ShownZ[entity] = Ease(ShownZ[entity], z, ref ShownVz[entity], dt);
        }
        x = ShownX[entity]; y = ShownY[entity]; z = ShownZ[entity];
        return true;
    }

    // Unity's Mathf.SmoothDamp, with no speed limit.
    private static float Ease(float current, float target, ref float velocity, float dt)
    {
        var omega = 2f / SmoothSeconds;
        var x = omega * dt;
        var exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        var change = current - target;
        var temp = (velocity + omega * change) * dt;
        velocity = (velocity - omega * temp) * exp;
        return target + (change + temp) * exp;
    }
}

/// <summary>Plays a recording's data blocks: <see cref="Seek"/> restores the world at a moment from the nearest keyframe before it and the
/// blocks after, <see cref="Advance"/> moves forward and reports the one-shot events it passes. A jump never reports events. Main thread
/// only; a block is decoded when first needed.</summary>
internal sealed class Timeline
{
    private readonly ChunkReader _reader;
    private readonly List<int> _data = new();
    private readonly SampleDecoder _decoder = new();
    private int _next;               // index into _data of the first block not yet decoded
    private List<Record>? _records;  // the decoded block being played
    private int _cursor;
    // One-shot events whose effect lasts (a lost limb, an enemy's end that clears it, a spawned item) from every block read so far, with the
    // block they are in: a keyframe block does not write them again, so a jump applies the ones before its keyframe from here.
    private readonly List<(int Block, Record Record)> _lasting = new();
    private int _scanned;            // blocks [0, _scanned) have their lasting events in _lasting
    // The decoded blocks last used, so a jump back to a place seen a moment ago (and the next block read early to find an entity's next sample)
    // decodes nothing; the lists of the blocks that leave are kept to be filled again.
    private const int CachedBlocks = 20;
    private readonly Dictionary<int, List<Record>> _blocks = new();
    private readonly List<int> _recent = new();   // cached block indexes, least recently used first
    private readonly Stack<List<Record>> _spare = new();
    private int _decodedTo = -1;                  // the block the decoder decoded last: a delta block continues from the one before it

    /// <summary>A sample further ahead than this is not interpolated towards: the entity stood still in between (250 ms is several times
    /// the longest interval the recorder samples a moving entity at).</summary>
    internal const uint MaxStepMs = 250;

    internal WorldState State { get; } = new();

    /// <summary>The data block id of the enemy whose end event is being reported (it is already gone from <see cref="State"/>); 0 if unknown.</summary>
    internal ulong GoneData { get; private set; }

    /// <summary>The end of the last complete block.</summary>
    internal uint DurationMs { get; }

    /// <summary>The time everything in <see cref="State"/> is applied up to.</summary>
    internal uint PositionMs { get; private set; }

    internal Timeline(ChunkReader reader)
    {
        _reader = reader;
        for (var i = 0; i < reader.Chunks.Count; i++)
        {
            var chunk = reader.Chunks[i];
            if (chunk.Type != DataChunks.Delta && chunk.Type != DataChunks.Keyframe) continue;
            _data.Add(i);
            DurationMs = Math.Max(DurationMs, chunk.EndMs);
        }
        Seek(0);
    }

    internal static bool IsStateEvent(ushort kind) => kind is EventKind.Door or EventKind.Scan or EventKind.Puzzle or EventKind.Generator or EventKind.ItemState
        or EventKind.Sentry or EventKind.Mine or EventKind.ResourceBox or EventKind.Reactor or EventKind.Environment or EventKind.WeaponSound
        or EventKind.Objective or EventKind.Disinfection or EventKind.Music or EventKind.Foam;

    /// <summary>The jump: clears the world, restores the keyframe block at or before <paramref name="timeMs"/> and applies every record up to it.</summary>
    internal void Seek(uint timeMs)
    {
        timeMs = Math.Min(timeMs, DurationMs);
        var start = 0;
        for (var i = 0; i < _data.Count; i++)
        {
            var chunk = _reader.Chunks[_data[i]];
            if (chunk.StartMs > timeMs) break;
            if (chunk.Type == DataChunks.Keyframe) start = i;
        }
        State.Clear();
        // The blocks never read so far give only their lasting events, read on their own: the decoder may stand anywhere after a jump back.
        for (; _scanned < start; _scanned++)
        {
            var chunk = _reader.Chunks[_data[_scanned]];
            SampleDecoder.Events(_reader.Read(chunk), chunk.RawLength, chunk.StartMs, record => { if (Lasting(record.EventType)) _lasting.Add((_scanned, record)); });
        }
        foreach (var (block, record) in _lasting)
        {
            if (block >= start) break;
            Apply(record, null);
        }
        _next = start; _records = null; _cursor = 0; PositionMs = _data.Count == 0 ? 0 : _reader.Chunks[_data[start]].StartMs;
        Advance(timeMs, null);
    }

    // The latest payload per object; an object whose payload ends it is dropped, as the recorder drops it from the keyframes (the record itself still reaches onEvent).
    private static void Keep(Dictionary<(ushort Kind, ulong Key), byte[]> table, ushort kind, byte[] payload)
    {
        var id = (kind, PayloadReader.Over(payload).U());
        if (EventKind.Ends(kind, payload)) table.Remove(id); else table[id] = payload;
    }

    private static bool Lasting(ushort kind) => kind is EventKind.EnemyLimb or EventKind.EnemyGone or EventKind.ItemSpawn;

    /// <summary>The takeover of the lasting events another timeline over the same recording has found by reading every block (the analysis
    /// the replay opens with does, on a worker thread): the first jump past blocks never read here then reads none of them. Call it with the
    /// other timeline at rest; it is the same list the jump would have built, since both go through <see cref="Lasting"/>.</summary>
    internal void AdoptLasting(Timeline read)
    {
        if (read._scanned <= _scanned) return;
        _lasting.Clear(); _lasting.AddRange(read._lasting); _scanned = read._scanned;
    }

    /// <summary>How many decoded blocks are held (at most <see cref="CachedBlocks"/>), for the tests.</summary>
    internal int CachedCount => _blocks.Count;

    // The decoded records of a block, from the cache when it is there. The decoder only goes forward from a keyframe, so a block that is
    // not a keyframe is decoded right after the block before it: when the decoder stands elsewhere (blocks came from the cache) the
    // blocks from the keyframe up to it are run through first.
    private List<Record> Decode(int index)
    {
        if (_blocks.TryGetValue(index, out var hit)) { Touch(index); return hit; }
        if (_reader.Chunks[_data[index]].Type != DataChunks.Keyframe && _decodedTo != index - 1)
        {
            var from = index - 1;
            while (from > 0 && _reader.Chunks[_data[from]].Type != DataChunks.Keyframe) from--;
            for (; from < index; from++)
            {
                var passed = DecodeBlock(from);
                if (_blocks.ContainsKey(from)) Recycle(passed); else Store(from, passed);
            }
        }
        var records = DecodeBlock(index);
        Store(index, records);
        return records;
    }

    private List<Record> DecodeBlock(int index)
    {
        var chunk = _reader.Chunks[_data[index]];
        var records = _spare.Count > 0 ? _spare.Pop() : new List<Record>();
        _decoder.Decode(_reader.Read(chunk), chunk.RawLength, chunk.StartMs, chunk.Type == DataChunks.Keyframe, records.Add);
        _decodedTo = index;
        return records;
    }

    private void Touch(int index) { _recent.Remove(index); _recent.Add(index); }

    private void Store(int index, List<Record> records)
    {
        _blocks[index] = records;
        _recent.Add(index);
        // The oldest leave, never the block being played (its list is in use) and never the one just kept.
        for (var i = 0; _blocks.Count > CachedBlocks && i < _recent.Count - 1;)
        {
            var old = _blocks[_recent[i]];
            if (ReferenceEquals(old, _records)) { i++; continue; }
            _blocks.Remove(_recent[i]); _recent.RemoveAt(i);
            Recycle(old);
        }
    }

    private void Recycle(List<Record> records) { records.Clear(); _spare.Push(records); }

    // The lasting events of block `index`, kept the first time the block is read.
    private void Note(int index, List<Record> records)
    {
        if (index != _scanned) return;
        foreach (var record in records)
            if (record.Kind == RecordKind.Event && Lasting(record.EventType)) _lasting.Add((index, record));
        _scanned++;
    }

    /// <summary>Applies every record up to <paramref name="timeMs"/>; <paramref name="onEvent"/> gets the one-shot events among them and
    /// <paramref name="onSample"/> every position sample once it is applied.</summary>
    internal void Advance(uint timeMs, Action<Record>? onEvent, Action<Record>? onSample = null)
    {
        timeMs = Math.Min(timeMs, DurationMs);
        while (true)
        {
            if (_records == null || _cursor >= _records.Count)
            {
                if (_next >= _data.Count) break;
                var chunk = _reader.Chunks[_data[_next]];
                if (chunk.StartMs > timeMs && _records != null) break; // the next block starts after the target
                var records = Decode(_next);
                Note(_next, records);
                _records = records; _cursor = 0; _next++;
                continue;
            }
            var record = _records[_cursor];
            if (record.TimeMs > timeMs) break;
            _cursor++;
            Apply(record, onEvent);
            if (record.Kind == RecordKind.Sample)
            {
                FindNext(record);
                onSample?.Invoke(record);
            }
        }
        PositionMs = Math.Max(PositionMs, timeMs);
    }

    // Records in the sample that follows the one just applied. Blocks are decoded in order, so the block after the current one can be decoded now and kept.
    private void FindNext(in Record sample)
    {
        ref var pose = ref State.Poses[sample.Entity];
        pose.HasNext = false;
        var until = sample.TimeMs + MaxStepMs;
        if (Scan(_records!, _cursor, sample.Entity, until, ref pose) || _next >= _data.Count || _reader.Chunks[_data[_next]].StartMs > until) return;
        // The next block is decoded now and kept (the cache): the play reaches it a moment later.
        Scan(Decode(_next), 0, sample.Entity, until, ref pose);
    }

    // True when the search is over: the next sample was found, or the time limit or the entity's end was reached.
    private static bool Scan(List<Record> records, int from, int entity, uint until, ref Pose pose)
    {
        var list = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(records);
        for (var i = from; i < list.Length; i++)
        {
            ref readonly var r = ref list[i];
            if (r.TimeMs > until) return true;
            if (r.Kind == RecordKind.Sample)
            {
                if (r.Entity != entity) continue;
                pose.NextMs = r.TimeMs; pose.NextX = r.X; pose.NextY = r.Y; pose.NextZ = r.Z;
                pose.NextQx = r.Qx; pose.NextQy = r.Qy; pose.NextQz = r.Qz; pose.NextQw = r.Qw;
                pose.HasNext = r.TimeMs > pose.TimeMs;
                return true;
            }
            // The next enemy in a freed slot comes with the same number: it is not this one's next place.
            if (r.EventType == EventKind.EnemyGone && (int)PayloadReader.Over(r.Payload!).U() == entity) return true;
        }
        return false;
    }

    private void Apply(Record r, Action<Record>? onEvent)
    {
        if (r.Kind == RecordKind.Sample)
        {
            ref var p = ref State.Poses[r.Entity];
            p.TimeMs = r.TimeMs; p.X = r.X; p.Y = r.Y; p.Z = r.Z; p.Qx = r.Qx; p.Qy = r.Qy; p.Qz = r.Qz; p.Qw = r.Qw; p.State = r.State; p.Present = true;
            return;
        }
        var payload = r.Payload!;
        switch (r.EventType)
        {
            case EventKind.EnemySpawn:
                {
                    var read = PayloadReader.Over(payload);
                    var entity = (int)read.U();
                    var data = read.U(); var dimension = read.U(); read.Pos();
                    var first = !State.Enemies.ContainsKey(entity);
                    State.Enemies[entity] = (data, dimension, read.U() / 10f);
                    // Reported when it first comes (a keyframe writes the living ones again): the moment the analysis sees an enemy appear.
                    if (first) onEvent?.Invoke(r);
                    return;
                }
            case EventKind.EnemyGone:
                {
                    var entity = (int)PayloadReader.Over(payload).U();
                    GoneData = State.Enemies.TryGetValue(entity, out var gone) ? gone.DataId : 0;
                    // Reported first: a death sound needs the enemy's family and last place.
                    onEvent?.Invoke(r);
                    State.Enemies.Remove(entity);
                    // What it had in mind and how it looked end with it, as a jump past a keyframe (which no longer holds it) has them.
                    State.Extras.Remove((EventKind.EnemyMind, (ulong)entity));
                    State.Extras.Remove((EventKind.EnemyLook, (ulong)entity));
                    State.Extras.Remove((EventKind.WakeUp, (ulong)entity));
                    State.Poses[entity] = default;
                    State.EnemyLocomotion[entity] = 0;
                    State.LimbsGone[entity] = 0;
                    return;
                }
            case EventKind.EnemyLimb:
                {
                    var read = PayloadReader.Over(payload);
                    var entity = (int)read.U();
                    if ((uint)entity < WorldState.MaxEntities) State.LimbsGone[entity] |= WorldState.LimbBit(read.U());
                    onEvent?.Invoke(r);
                    return;
                }
            case EventKind.ItemSpawn:
                {
                    var read = PayloadReader.Over(payload);
                    var item = (uint)read.U();
                    var (x, y, z) = read.Pos();
                    State.ItemSpawns.Add(new ItemSpawned(r.TimeMs, item, x, y, z, (uint)read.U()));
                    onEvent?.Invoke(r);
                    return;
                }
            case EventKind.EnemyState:
                {
                    var read = PayloadReader.Over(payload);
                    var entity = (int)read.U();
                    if ((uint)entity < WorldState.MaxEntities) State.EnemyLocomotion[entity] = (byte)read.U();
                    onEvent?.Invoke(r);
                    return;
                }
            case EventKind.PlayerStatus:
                State.PlayerStatus[(int)PayloadReader.Over(payload).U() & 3] = payload;
                onEvent?.Invoke(r);
                return;
            case EventKind.PlayerKit:
                {
                    var read = PayloadReader.Over(payload);
                    var slot = (int)read.U() & 3;
                    State.PlayerKits[slot] = PlayerKit.Read(read);
                    onEvent?.Invoke(r);
                    return;
                }
            case EventKind.PlayerMotion:
                {
                    var read = PayloadReader.Over(payload);
                    var slot = (int)read.U() & 3;
                    State.PlayerForward[slot] = read.S() * WorldState.MotionStep;
                    State.PlayerRight[slot] = read.S() * WorldState.MotionStep;
                    return;
                }
            case EventKind.Presence:
                {
                    // A player who left takes the place and the walk with them: what reads the poses (the wipe check, the director) must not count a seat that is empty.
                    var read = PayloadReader.Over(payload);
                    var slot = (int)read.U();
                    if (slot <= 3 && read.U() != 1) { State.Poses[slot] = default; State.PlayerForward[slot] = 0; State.PlayerRight[slot] = 0; }
                    goto default;
                }
            default:
                if (IsStateEvent(r.EventType)) Keep(State.Objects, r.EventType, payload);
                else if (EventKind.IsExtraState(r.EventType)) Keep(State.Extras, r.EventType, payload);
                onEvent?.Invoke(r);
                return;
        }
    }
}

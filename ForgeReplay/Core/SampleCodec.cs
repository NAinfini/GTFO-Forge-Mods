using System;
using ForgeCore;

namespace ForgeReplay;

/// <summary>What one decoded record of a data block is.</summary>
internal enum RecordKind : byte { Sample = 1, Event = 2 }

/// <summary>The first byte of each record in a block body. A sample writes its rotation in full, not at all (the same as the entity's last
/// written one), or as the differences of the three packed 15-bit components from the last written ones (the same component is the largest).</summary>
internal static class BlockRecord
{
    internal const byte FullTurn = 1, Event = 2, SameTurn = 3, Turn = 4;

    /// <summary>The 6 bytes of a smallest-three quaternion as one number: bits 0-1 the largest component, then three 15-bit fields.</summary>
    internal static ulong Packed(byte[] bytes)
    {
        ulong packed = 0;
        for (var i = 0; i < Compact.QuaternionBytes; i++) packed |= (ulong)bytes[i] << (8 * i);
        return packed;
    }

    internal static void Unpack(ulong packed, byte[] bytes)
    {
        for (var i = 0; i < Compact.QuaternionBytes; i++) bytes[i] = (byte)(packed >> (8 * i));
    }

    internal static long Field(ulong packed, int k) => (long)((packed >> (2 + 15 * k)) & 0x7FFF);
}

/// <summary>One decoded record. For a sample: the entity, its position in metres, rotation, and the owner-defined state byte. For an event: its
/// type and payload.</summary>
internal readonly record struct Record(RecordKind Kind, uint TimeMs, int Entity, float X, float Y, float Z, float Qx, float Qy, float Qz, float Qw, byte State,
    ushort EventType, byte[]? Payload);

/// <summary>Encodes samples and events into the body of one data block, without allocating: positions are quantized to centimetres and
/// written as variable-length differences from the entity's last written position (absolute in a keyframe block), rotations as 6-byte
/// smallest-three quaternions written in full, left out when unchanged or written as the differences of their packed components
/// (<see cref="BlockRecord"/>; the same rotation is decoded either way), and a sample that changed neither position, rotation nor state since
/// it was last written is not written. Main thread only. Layout in DEVELOPERS.md.</summary>
internal sealed class SampleEncoder
{
    internal const float Step = 0.01f;
    private const int MaxEntities = 4096;
    private const int SampleMaxBytes = 1 + 5 + 3 + 3 * Compact.MaxVarBytes + Compact.QuaternionBytes + 1;
    // Room only events may use: a block that is nearly full stops taking samples so the events that matter still fit.
    private const int EventReserve = 8192;

    private readonly long[] _x = new long[MaxEntities], _y = new long[MaxEntities], _z = new long[MaxEntities];
    private readonly ulong[] _rot = new ulong[MaxEntities]; // the packed rotation last written per entity
    private readonly byte[] _state = new byte[MaxEntities];
    private readonly bool[] _known = new bool[MaxEntities];
    private readonly byte[] _scratch = new byte[Compact.QuaternionBytes];
    private byte[] _buffer = Array.Empty<byte>();
    private int _position;
    private uint _lastMs;

    /// <summary>Bytes written to the current block so far.</summary>
    internal int Length => _position;

    /// <summary>Starts a block in <paramref name="buffer"/> beginning at <paramref name="startMs"/>. A keyframe block writes every sample
    /// in full and every entity it samples is written even if unchanged; a delta block writes differences from what was last written.</summary>
    internal void Begin(byte[] buffer, uint startMs, bool keyframe)
    {
        _buffer = buffer; _position = 0; _lastMs = startMs;
        if (keyframe) Array.Clear(_known, 0, _known.Length);
    }

    /// <summary>Writes a sample unless it is unchanged or the buffer has no room. Returns false only when the buffer is full (the sample is lost).</summary>
    internal bool Sample(uint timeMs, int entity, float x, float y, float z, float qx, float qy, float qz, float qw, byte state)
    {
        if ((uint)entity >= MaxEntities) throw new ArgumentOutOfRangeException(nameof(entity));
        if (state > 127) throw new ArgumentOutOfRangeException(nameof(state), "The state shares a byte with the absolute flag: 0 to 127.");
        var ix = (long)MathF.Round(x / Step); var iy = (long)MathF.Round(y / Step); var iz = (long)MathF.Round(z / Step);
        var at = 0;
        Compact.WriteQuaternion(_scratch, ref at, qx, qy, qz, qw);
        var rot = BlockRecord.Packed(_scratch);
        var last = _rot[entity];
        var known = _known[entity];
        if (known && Math.Abs(ix - _x[entity]) <= 1 && Math.Abs(iy - _y[entity]) <= 1 && Math.Abs(iz - _z[entity]) <= 1 && state == _state[entity] && rot == last) return true;
        if (_buffer.Length - _position < SampleMaxBytes + EventReserve) return false;
        var absolute = !known;
        var kind = absolute ? BlockRecord.FullTurn : rot == last ? BlockRecord.SameTurn
            : (rot & 3) == (last & 3) && TurnBytes(last, rot) < Compact.QuaternionBytes ? BlockRecord.Turn : BlockRecord.FullTurn;
        _buffer[_position++] = kind;
        Compact.WriteVar(_buffer, ref _position, timeMs - _lastMs);
        Compact.WriteVar(_buffer, ref _position, (ulong)entity);
        _buffer[_position++] = (byte)((absolute ? 1 : 0) | (state << 1));
        if (absolute) { Compact.WriteVarSigned(_buffer, ref _position, ix); Compact.WriteVarSigned(_buffer, ref _position, iy); Compact.WriteVarSigned(_buffer, ref _position, iz); }
        else
        {
            Compact.WriteDelta(_buffer, ref _position, _x[entity], ix); Compact.WriteDelta(_buffer, ref _position, _y[entity], iy); Compact.WriteDelta(_buffer, ref _position, _z[entity], iz);
        }
        if (kind == BlockRecord.FullTurn) for (var i = 0; i < Compact.QuaternionBytes; i++) _buffer[_position++] = _scratch[i];
        else if (kind == BlockRecord.Turn)
            for (var k = 0; k < 3; k++) Compact.WriteVarSigned(_buffer, ref _position, BlockRecord.Field(rot, k) - BlockRecord.Field(last, k));
        _x[entity] = ix; _y[entity] = iy; _z[entity] = iz; _state[entity] = state; _known[entity] = true; _rot[entity] = rot;
        _lastMs = timeMs;
        return true;
    }

    // The bytes the three component differences take as zig-zag variable-length integers.
    private static int TurnBytes(ulong from, ulong to)
    {
        var bytes = 0;
        for (var k = 0; k < 3; k++)
        {
            var zigzag = Compact.ZigZag(BlockRecord.Field(to, k) - BlockRecord.Field(from, k));
            bytes += zigzag < 1UL << 7 ? 1 : zigzag < 1UL << 14 ? 2 : 3;
        }
        return bytes;
    }

    /// <summary>Writes an event with its payload. Returns false when the buffer has no room (the event is lost; the caller reports it).</summary>
    internal bool Event(uint timeMs, ushort type, ReadOnlySpan<byte> payload)
    {
        if (_buffer.Length - _position < 1 + 5 + 3 + 5 + payload.Length) return false;
        _buffer[_position++] = BlockRecord.Event;
        Compact.WriteVar(_buffer, ref _position, timeMs - _lastMs);
        Compact.WriteUInt16(_buffer, ref _position, type);
        Compact.WriteVar(_buffer, ref _position, (ulong)payload.Length);
        payload.CopyTo(new Span<byte>(_buffer, _position, payload.Length));
        _position += payload.Length;
        _lastMs = timeMs;
        return true;
    }

    /// <summary>Forgets an entity (it left the level), so a later entity reusing the number starts from an absolute sample.</summary>
    internal void Forget(int entity) { if ((uint)entity < MaxEntities) _known[entity] = false; }
}

/// <summary>Decodes a block body written by <see cref="SampleEncoder"/>. A keyframe block starts every entity afresh; a delta block continues
/// from the entities' last decoded state, so decode blocks in order starting at a keyframe. Anything malformed throws
/// <see cref="FormatException"/>.</summary>
internal sealed class SampleDecoder
{
    private const int MaxEntities = 4096;
    private readonly long[] _x = new long[MaxEntities], _y = new long[MaxEntities], _z = new long[MaxEntities];
    private readonly ulong[] _rot = new ulong[MaxEntities];
    private readonly bool[] _known = new bool[MaxEntities];
    private readonly byte[] _scratch = new byte[Compact.QuaternionBytes];

    internal void Decode(byte[] body, int length, uint startMs, bool keyframe, Action<Record> visit)
    {
        if (keyframe) Array.Clear(_known, 0, _known.Length);
        if (body.Length != length) body = body.AsSpan(0, length).ToArray(); // reads must stop at the block end, not at the buffer end
        var p = 0; var ms = startMs;
        while (p < length)
        {
            var kind = body[p++];
            ms += checked((uint)Compact.ReadVar(body, ref p));
            if (kind is BlockRecord.FullTurn or BlockRecord.SameTurn or BlockRecord.Turn)
            {
                var entityValue = Compact.ReadVar(body, ref p);
                if (entityValue >= MaxEntities) throw new FormatException("Entity " + entityValue + " is out of range.");
                var entity = (int)entityValue;
                if (p >= length) throw new FormatException("A sample is cut off.");
                var flags = body[p++];
                if ((flags & 1) != 0)
                {
                    if (kind != BlockRecord.FullTurn) throw new FormatException("An absolute sample of entity " + entity + " leaves out its rotation.");
                    _x[entity] = Compact.ReadVarSigned(body, ref p); _y[entity] = Compact.ReadVarSigned(body, ref p); _z[entity] = Compact.ReadVarSigned(body, ref p); _known[entity] = true;
                }
                else
                {
                    if (!_known[entity]) throw new FormatException("A difference sample for entity " + entity + " has nothing to continue from.");
                    _x[entity] = Compact.ReadDelta(body, ref p, _x[entity]); _y[entity] = Compact.ReadDelta(body, ref p, _y[entity]); _z[entity] = Compact.ReadDelta(body, ref p, _z[entity]);
                }
                if (kind == BlockRecord.FullTurn)
                {
                    if (p + Compact.QuaternionBytes > length) throw new FormatException("A rotation runs past the end of the block.");
                    Buffer.BlockCopy(body, p, _scratch, 0, Compact.QuaternionBytes);
                    p += Compact.QuaternionBytes;
                    _rot[entity] = BlockRecord.Packed(_scratch);
                }
                else if (kind == BlockRecord.Turn)
                {
                    var last = _rot[entity];
                    var rot = last & 3;
                    for (var k = 0; k < 3; k++)
                    {
                        var field = BlockRecord.Field(last, k) + Compact.ReadVarSigned(body, ref p);
                        if (field is < 0 or > 0x7FFF) throw new FormatException("A turn of entity " + entity + " leaves the rotation range.");
                        rot |= (ulong)field << (2 + 15 * k);
                    }
                    _rot[entity] = rot;
                }
                BlockRecord.Unpack(_rot[entity], _scratch);
                var at = 0;
                Compact.ReadQuaternion(_scratch, ref at, out var qx, out var qy, out var qz, out var qw);
                visit(new Record(RecordKind.Sample, ms, entity, _x[entity] * SampleEncoder.Step, _y[entity] * SampleEncoder.Step, _z[entity] * SampleEncoder.Step,
                    qx, qy, qz, qw, (byte)(flags >> 1), 0, null));
            }
            else if (kind == BlockRecord.Event) visit(ReadEvent(body, length, ref p, ms));
            else throw new FormatException("Unknown record kind " + kind + ".");
        }
    }

    /// <summary>Reports only the events of a block body and steps over its samples without any entity's state, so a block reads on its own
    /// in any order and no decoder is disturbed.</summary>
    internal static void Events(byte[] body, int length, uint startMs, Action<Record> visit)
    {
        if (body.Length != length) body = body.AsSpan(0, length).ToArray();
        var p = 0; var ms = startMs;
        while (p < length)
        {
            var kind = body[p++];
            ms += checked((uint)Compact.ReadVar(body, ref p));
            if (kind is BlockRecord.FullTurn or BlockRecord.SameTurn or BlockRecord.Turn)
            {
                Compact.ReadVar(body, ref p);
                if (p >= length) throw new FormatException("A sample is cut off.");
                p++;
                var numbers = kind == BlockRecord.Turn ? 6 : 3;
                for (var i = 0; i < numbers; i++) Compact.ReadVar(body, ref p);
                if (kind == BlockRecord.FullTurn) p += Compact.QuaternionBytes;
                if (p > length) throw new FormatException("A rotation runs past the end of the block.");
            }
            else if (kind == BlockRecord.Event) visit(ReadEvent(body, length, ref p, ms));
            else throw new FormatException("Unknown record kind " + kind + ".");
        }
    }

    private static Record ReadEvent(byte[] body, int length, ref int p, uint ms)
    {
        var type = Compact.ReadUInt16(body, ref p);
        var size = Compact.ReadVar(body, ref p);
        if (size > (ulong)(length - p)) throw new FormatException("An event payload runs past the end.");
        var payload = new byte[size];
        Buffer.BlockCopy(body, p, payload, 0, (int)size);
        p += (int)size;
        return new Record(RecordKind.Event, ms, -1, 0, 0, 0, 0, 0, 0, 1, 0, type, payload);
    }
}

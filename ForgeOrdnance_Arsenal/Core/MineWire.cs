using System;
using System.Buffers.Binary;

namespace ForgeOrdnanceArsenal;

/// <summary>The mine field of one <c>Mines</c> device, sent with <c>SendState</c> (<see cref="DataTag.Mines"/>): the whole field each time, so a
/// player who joins later sees the mines still lying, and each peer finds what changed by comparing with what it shows. Layout (little
/// endian): count (1), still-lying mask (8), burst mask (8), the device's place as three floats (12), the hash of its definition's id (4: a peer that joined later looks the definition up by it), then count offsets from it of 4 bytes each, which is
/// how the most mines fit the 192 bytes of a message: x and z in 12 bits (2 cm over +-<see cref="ReachMetres"/>), height in 8 (2 cm over +-<see cref="ClimbMetres"/>).
/// A mine in the lying mask is shown; one that left it in the burst mask is shown bursting, one that left it otherwise (the device's time ran out) just goes.
/// The host bursts a mine at its exact place, so a peer's draws within 1 cm of it.</summary>
internal static class MineWire
{
    public const int HeaderBytes = 33;
    public const float ReachMetres = 41f, ClimbMetres = 2.55f;

    /// <summary><paramref name="positions"/> are world places; a mine that found no floor (not in <paramref name="lying"/>) may hold anything, it only clamps.</summary>
    public static byte[] Encode(float[] positions, int count, ulong lying, ulong burst, float originX, float originY, float originZ, uint definition)
    {
        var bytes = new byte[HeaderBytes + count * 4];
        bytes[0] = (byte)count;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1), lying);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(9), burst);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(17), originX);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(21), originY);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(25), originZ);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(29), definition);
        for (var i = 0; i < count; i++)
        {
            var packed = Pack(positions[i * 3] - originX, ReachMetres, 12) | Pack(positions[i * 3 + 2] - originZ, ReachMetres, 12) << 12 | Pack(positions[i * 3 + 1] - originY, ClimbMetres, 8) << 24;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(HeaderBytes + i * 4), packed);
        }
        return bytes;
    }

    /// <summary>False for a record of the wrong size or count, or with a non-finite place.</summary>
    public static bool TryDecode(byte[] bytes, out float[] positions, out int count, out ulong lying, out ulong burst, out uint definition)
    {
        positions = Array.Empty<float>();
        count = 0;
        lying = burst = 0;
        definition = 0;
        if (bytes.Length < HeaderBytes) return false;
        count = bytes[0];
        if (count < 1 || count > DeployableSpec.MaxMines || bytes.Length != HeaderBytes + count * 4) return false;
        lying = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(1));
        burst = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(9));
        var x = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(17));
        var y = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(21));
        var z = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(25));
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        definition = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(29));
        positions = new float[count * 3];
        for (var i = 0; i < count; i++)
        {
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(HeaderBytes + i * 4));
            positions[i * 3] = x + Unpack(packed & 0xFFF, ReachMetres, 12);
            positions[i * 3 + 2] = z + Unpack(packed >> 12 & 0xFFF, ReachMetres, 12);
            positions[i * 3 + 1] = y + Unpack(packed >> 24, ClimbMetres, 8);
        }
        return true;
    }

    private static uint Pack(float offset, float reach, int bits)
    {
        var top = (1u << bits) - 1u;
        return (uint)Math.Clamp((int)MathF.Round((offset + reach) / (2f * reach) * top), 0, (int)top);
    }

    private static float Unpack(uint packed, float reach, int bits) => packed / (float)((1u << bits) - 1u) * 2f * reach - reach;
}

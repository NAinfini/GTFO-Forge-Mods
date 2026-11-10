using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>The size a PNG declares in its IHDR, read before anything decodes it (a decoder allocates whatever the header claims). No game or Unity type.</summary>
public static class Png
{
    /// <summary>False unless <paramref name="bytes"/> start with the PNG signature and an IHDR chunk of a non-zero size.</summary>
    public static bool TrySize(IReadOnlyList<byte> bytes, out int width, out int height)
    {
        width = 0; height = 0;
        ReadOnlySpan<byte> head = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        if (bytes.Count < 24) return false;
        for (int index = 0; index < head.Length; index++) if (bytes[index] != head[index]) return false;
        uint w = BigEndian(bytes, 16), h = BigEndian(bytes, 20);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return false;
        width = (int)w; height = (int)h;
        return true;
    }

    private static uint BigEndian(IReadOnlyList<byte> value, int offset)
        => (uint)value[offset] << 24 | (uint)value[offset + 1] << 16 | (uint)value[offset + 2] << 8 | value[offset + 3];
}

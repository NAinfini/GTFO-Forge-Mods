using System;
using System.Buffers.Binary;

namespace ForgeOrdnance;

/// <summary>One player's lobby picks as their ForgeCore lobby value (<see cref="Messages.LobbyPicks"/>): a count, then each key
/// (little-endian). Display only: the host still checks the loadout submitted on entering a level.</summary>
internal static class LobbyPicks
{
    /// <summary>The first <see cref="Messages.MaxLoadout"/> keys at most.</summary>
    internal static byte[] Encode(ReadOnlySpan<uint> keys)
    {
        var count = Math.Min(keys.Length, Messages.MaxLoadout);
        var bytes = new byte[1 + count * sizeof(uint)];
        bytes[0] = (byte)count;
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1 + i * sizeof(uint)), keys[i]);
        return bytes;
    }

    /// <summary>The keys into <paramref name="into"/> (at least <see cref="Messages.MaxLoadout"/> long) and how many; -1 for a
    /// malformed value (a count past the capacity or a length that does not match it). Allocates nothing.</summary>
    internal static int Decode(ReadOnlySpan<byte> bytes, Span<uint> into)
    {
        if (bytes.Length == 0) return -1;
        var count = bytes[0];
        if (count > Messages.MaxLoadout || count > into.Length || bytes.Length != 1 + count * sizeof(uint)) return -1;
        for (var i = 0; i < count; i++) into[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(1 + i * sizeof(uint)));
        return count;
    }
}

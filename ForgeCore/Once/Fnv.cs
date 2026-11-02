using System;
using System.Text;

namespace ForgeCore;

/// <summary>The one FNV-1a hash (32 and 64 bit). The output is part of wire formats and saved keys, so the algorithm never changes:
/// start from the offset basis, then for every byte XOR it in and multiply by the prime. Strings are hashed as their UTF-8 bytes; the
/// <c>Mix</c> methods feed a value one byte at a time for callers that hash a record rather than text. No game or Unity type.</summary>
public static class Fnv
{
    public const uint Offset32 = 2166136261u, Prime32 = 16777619u;
    public const ulong Offset64 = 14695981039346656037ul, Prime64 = 1099511628211ul;

    /// <summary>Feeds one byte into a 32-bit hash that started at <see cref="Offset32"/>.</summary>
    public static uint Mix32(uint hash, byte value) => unchecked((hash ^ value) * Prime32);

    /// <summary>Feeds one byte into a 64-bit hash that started at <see cref="Offset64"/>.</summary>
    public static ulong Mix64(ulong hash, byte value) => unchecked((hash ^ value) * Prime64);

    public static uint Hash32(ReadOnlySpan<byte> bytes)
    {
        var hash = Offset32;
        foreach (var b in bytes) hash = Mix32(hash, b);
        return hash;
    }

    public static ulong Hash64(ReadOnlySpan<byte> bytes)
    {
        var hash = Offset64;
        foreach (var b in bytes) hash = Mix64(hash, b);
        return hash;
    }

    /// <summary>FNV-1a 32 over the UTF-8 bytes of <paramref name="text"/>.</summary>
    public static uint Hash32(string text) => Hash32(Encoding.UTF8.GetBytes(text));

    /// <summary>FNV-1a 64 over the UTF-8 bytes of <paramref name="text"/>.</summary>
    public static ulong Hash64(string text) => Hash64(Encoding.UTF8.GetBytes(text));
}

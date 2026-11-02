using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeOrdnanceArsenal;

/// <summary>The ForgeVisual model ids (<c>Pack:item</c>) of the heavy weapons this install has accepted. A model travels as the FNV-1a 32
/// hash of its id (in the rack record) and marks the lent gear by its public name; this table turns either back into the id.</summary>
public static class HeavyModels
{
    /// <summary>The public gear name of a lent heavy weapon is this followed by the model hash in 8 hex digits (20 characters).</summary>
    public const string MarkerPrefix = "Forge Heavy ";

    private static readonly Dictionary<uint, string> Known = new();
    private static readonly Dictionary<string, (uint Hash, string Id)> ByMarker = new(StringComparer.Ordinal);

    public static uint Hash(string id) => ForgeCore.Fnv.Hash32(id);

    public static string Marker(uint hash) => MarkerPrefix + hash.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>The hash of the accepted model a marker name stands for; false for any other name. A table lookup, no formatting.</summary>
    public static bool TryMarker(string? name, out uint hash)
    {
        hash = 0;
        if (name == null || !ByMarker.TryGetValue(name, out var found)) return false;
        hash = found.Hash;
        return true;
    }

    /// <summary>The model id of the accepted model a marker name stands for; false for any other name. One table lookup, no formatting.</summary>
    public static bool TryMarkerId(string? name, out string id)
    {
        id = "";
        if (name == null || !ByMarker.TryGetValue(name, out var found)) return false;
        id = found.Id;
        return true;
    }

    /// <summary>Whether <paramref name="id"/> is <c>Pack:item</c>: two non-empty names without spaces around one colon.</summary>
    public static bool IsId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var colon = id.IndexOf(':');
        return colon > 0 && colon < id.Length - 1 && id.IndexOf(':', colon + 1) < 0 && id.IndexOf(' ') < 0;
    }

    public static bool TryId(uint hash, out string id) => Known.TryGetValue(hash, out id!);

    /// <summary>Adds <paramref name="id"/> to the table; null when it is added or already there, else the reason: another model id has the same hash.</summary>
    public static string? Register(string id)
    {
        var hash = Hash(id);
        if (Known.TryAdd(hash, id)) { ByMarker[Marker(hash)] = (hash, id); return null; }
        if (Known[hash] == id) return null;
        return "its hash " + hash.ToString("x8", CultureInfo.InvariantCulture) + " is the same as the hash of the model " + Known[hash] + " (" + id + " is refused; rename one of them).";
    }

    /// <summary>Forgets every id (tests).</summary>
    public static void Clear() { Known.Clear(); ByMarker.Clear(); }
}

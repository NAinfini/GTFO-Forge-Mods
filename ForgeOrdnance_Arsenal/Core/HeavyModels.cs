using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeOrdnanceArsenal;

/// <summary>The ForgeVisual model ids (<c>Pack:item</c>) of the heavy weapons this install has accepted. A model travels as the FNV-1a 32
/// hash of its id (in the rack record) and marks the lent gear by its public name; this table turns either back into the id.</summary>
internal static class HeavyModels
{
    /// <summary>The public gear name of a lent heavy weapon is this followed by the model hash in 8 hex digits (20 characters).</summary>
    public const string MarkerPrefix = "Forge Heavy ";

    private static readonly Dictionary<uint, string> Known = new();
    private static readonly Dictionary<string, (uint Hash, string Id)> ByMarker = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, HeavyMuzzle> Muzzles = new(StringComparer.Ordinal);

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

    /// <summary>Whether <paramref name="id"/> is a ForgeVisual model id as a definition writes it (heavy weapon, dispenser, munition): <c>Pack:item</c>,
    /// two non-empty names without spaces around one colon.</summary>
    public static bool IsId(string? id) => ForgeCore.PackItem.TrySplit(id, out _, out _) && id!.IndexOf(' ') < 0;

    public static bool TryId(uint hash, out string id) => Known.TryGetValue(hash, out id!);

    /// <summary>The muzzle flash an accepted model draws on every peer (the definitions load on every peer); <see cref="HeavyMuzzle.Mg"/> for an unknown id.</summary>
    public static HeavyMuzzle MuzzleOf(string id) => Muzzles.TryGetValue(id, out var muzzle) ? muzzle : HeavyMuzzle.Mg;

    /// <summary>Adds <paramref name="id"/> to the table with its muzzle flash; null when it is added or already there, else the reason: another model id has the same hash.</summary>
    public static string? Register(string id, HeavyMuzzle muzzle = HeavyMuzzle.Mg)
    {
        var hash = Hash(id);
        if (Known.TryAdd(hash, id)) { ByMarker[Marker(hash)] = (hash, id); Muzzles[id] = muzzle; return null; }
        if (Known[hash] == id) { Muzzles[id] = muzzle; return null; }
        return "its hash " + hash.ToString("x8", CultureInfo.InvariantCulture) + " is the same as the hash of the model " + Known[hash] + " (" + id + " is refused; rename one of them).";
    }

    /// <summary>Forgets every id (tests).</summary>
    public static void Clear() { Known.Clear(); ByMarker.Clear(); Muzzles.Clear(); }
}

/// <summary>The muzzle flash a heavy weapon draws when it fires: none (its own effect is drawn from the host's message or accepted shot), the machine-gun slot or the anti-materiel slot.</summary>
internal enum HeavyMuzzle { None, Mg, Amr }

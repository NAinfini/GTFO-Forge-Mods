using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeMap;

/// <summary>
/// The identity a placed fixed item carries in the level-object facts (contract §4.2.11, item alarms): a level item's
/// key is normally `item/&lt;d&gt;/&lt;l&gt;/&lt;z&gt;/&lt;serial&gt;`, and a serial is a per-run instance value no plan can be written with.
/// An item this package placed is keyed by its placement instead — `item/&lt;d&gt;/&lt;l&gt;/&lt;z&gt;/&lt;token&gt;`, where the zone is
/// the authored room's zone and the token is the author's own id without its `item:` prefix — so a plan can name
/// "this item" before the level exists, and the pickup trigger's `address` parameter scopes to it by the kernel's own
/// address match.
///
/// The table is game-independent: the native session registers the instance serial of each item it spawned, and the
/// observation half asks it before it falls back to the serial key. It is per world: a new world clears it, so an
/// instance id of a previous level is never answered as a placement of this one.
/// </summary>
internal static class FixedItemIdentity
{
    /// <summary>The level-object category the key starts with, the same one every level item is keyed under.</summary>
    public const string Category = "item";

    /// <summary>The author id prefix the placement documents carry.</summary>
    public const string IdPrefix = "item:";

    private static readonly Dictionary<int, string> Keys = new();
    private static long _epoch = long.MinValue;

    /// <summary>The address token of one authored id: the id without its prefix, when what remains is a token an
    /// address can carry (ASCII letters, digits and `-`, as a UUID is). Anything else has no token and no key.</summary>
    public static string? Token(string placementId)
    {
        if (placementId is null || !placementId.StartsWith(IdPrefix, StringComparison.Ordinal)) return null;
        var token = placementId[IdPrefix.Length..];
        if (token.Length is 0 or > 64) return null;
        foreach (var c in token)
            if (!(c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' || c == '-')) return null;
        return token;
    }

    /// <summary>The level-object key of one placement in the given zone, or null when the id has no token or a
    /// coordinate is negative.</summary>
    public static string? Key(int dimension, int layer, int zone, string placementId)
    {
        if (dimension < 0 || layer is < 0 or > 2 || zone < 0 || Token(placementId) is not { } token) return null;
        return Category + "/" + dimension.ToString(CultureInfo.InvariantCulture) + "/"
            + layer.ToString(CultureInfo.InvariantCulture) + "/" + zone.ToString(CultureInfo.InvariantCulture) + "/" + token;
    }

    /// <summary>Remembers the instance serial of one spawned placement in this world. A registration of another world
    /// first forgets the previous world's rows.</summary>
    public static void Register(long worldEpoch, int serial, string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (Keys)
        {
            if (worldEpoch != _epoch) { Keys.Clear(); _epoch = worldEpoch; }
            Keys[serial] = key;
        }
    }

    /// <summary>The placement key of one instance serial in this world, or null when the instance is not a placement
    /// this package spawned (or belongs to another world).</summary>
    public static string? KeyOf(long worldEpoch, int serial)
    {
        lock (Keys) return worldEpoch == _epoch && Keys.TryGetValue(serial, out var key) ? key : null;
    }

    /// <summary>Forgets every row: a released session or a test.</summary>
    public static void Clear()
    {
        lock (Keys) { Keys.Clear(); _epoch = long.MinValue; }
    }
}

using System.Globalization;

namespace ForgeMap;

/// <summary>
/// The address of one shuttlebox: the game's own `Dumbwaiter` function object (`ExpeditionFunction.Dumbwaiter`),
/// placed by a zone's `ExpeditionZoneData.DumbwaiterPlacements`. The key is the zero-based ordinal of the shuttlebox
/// among the shuttleboxes the level built in that zone, in the order the game built them; the website writes a zone's
/// placements in project order and names the n-th one `shuttlebox/&lt;d&gt;/&lt;l&gt;/&lt;z&gt;/&lt;n&gt;`. Whether the game builds
/// them in placement order is a point this package asks the game to confirm, exactly as the terminal placement index
/// is: a zone that holds one shuttlebox has only one reading either way.
/// </summary>
internal static class MapObjectShuttleboxAddress
{
    public static readonly string Category = "shuttlebox";

    /// <summary>Reads a shuttlebox address: this category, three zone coordinates and a decimal ordinal.</summary>
    public static MapObjectReference? TryParse(string? text)
        => MapObjectReference.TryParse(text, Category, MapObjectReference.IsCoordinate);

    /// <summary>The address of the n-th shuttlebox of one zone. A coordinate or ordinal that did not read yields no
    /// address at all.</summary>
    public static MapObjectReference? Create(int? dimension, int? layer, int? zone, int? ordinal)
        => dimension is not { } d || layer is not { } l || zone is not { } z || ordinal is not { } n || d < 0 || l < 0 || z < 0 || n < 0
            ? null
            : new MapObjectReference(Category, Text(d), Text(l), Text(z), Text(n));

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The shuttlebox reader: an <see cref="IMapObjectSource"/> that can also say whether this world holds the
/// shuttlebox an address names, because a shuttlebox is found by the level build and not resolved back through a
/// native lookup the way a door or a terminal is.</summary>
internal interface IShuttleboxSource : IMapObjectSource
{
    /// <summary>Whether this world built the shuttlebox the address names.</summary>
    bool Holds(MapObjectReference address);
}

/// <summary>
/// The shuttlebox half of the map-object provider. A shuttlebox is one more category of `gtfo.map_object`: a plan is
/// mounted on it through the same `map-object` attachment matcher as a door, and the fact that reaches it is the
/// generic `forge.trigger.interaction.item_placed`, whose `host` is the shuttlebox's own address when the item went
/// into a shuttlebox. There is no shuttlebox card and no second publisher: this half only names the object.
/// </summary>
internal sealed partial class MapObjectModule
{
    /// <summary>The shuttleboxes this module addresses. A module composed without the native half answers for none
    /// of them; the category stays declared so a mount on one is refused by the address it names.</summary>
    public IShuttleboxSource Shuttleboxes { get; init; } = EmptyShuttleboxSource.Instance;
}

/// <summary>The reader of a module composed without a native shuttlebox half: it addresses, reads and holds
/// nothing.</summary>
internal sealed class EmptyShuttleboxSource : IShuttleboxSource
{
    internal static readonly EmptyShuttleboxSource Instance = new();

    public string Category => MapObjectShuttleboxAddress.Category;

    public MapObjectReference? TryAddress(object instance) => null;

    public MapObjectReference? Parse(string text) => MapObjectShuttleboxAddress.TryParse(text);

    public MapObjectObservation? Read(object instance) => null;

    public double[]? Position(object instance) => null;

    public bool IsCurrentAddress(object instance, MapObjectReference address) => false;

    public bool Holds(MapObjectReference address) => false;

    public void Report(string message) { }
}

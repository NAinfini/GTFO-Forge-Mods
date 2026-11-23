using System.Collections.Generic;

namespace ForgeEngine.Framework;

/// <summary>I-DIAG level-layout observation (contract §3.2, `map.layout-generated`). The authoring layer reads
/// the layout the level generator actually built and hands it to the kernel as this typed value; the kernel writes
/// it and the website reads it back. The zones and connections here are facts read from the built level, not a
/// plan: nothing in this structure is derived from what the author asked for.</summary>
public readonly struct RuntimeLogLayoutZone
{
    /// <summary>The `dimension/layer/localIndex` locator the authoring side addresses zones by.</summary>
    public string Zone { get; init; }
    public int Dimension { get; init; }
    public int Layer { get; init; }
    public int LocalIndex { get; init; }
    /// <summary>The zone's floor id, or -1 when the floor could not be read.</summary>
    public int Floor { get; init; }
    /// <summary>The geomorph prefab names actually built inside the zone, in build order.</summary>
    public IReadOnlyList<string>? Tiles { get; init; }
}

/// <summary>One traversable connection between two generated zones. `From` is the parent zone's locator and
/// `To` is the zone that was expanded through the gate, so a zone's own direction is read off the connection
/// that enters it.</summary>
public readonly struct RuntimeLogLayoutConnection
{
    public string From { get; init; }
    public string To { get; init; }
    /// <summary>One of <see cref="RuntimeLogLayoutDirections"/>; `unknown` when the two zone centres gave no axis.</summary>
    public string Direction { get; init; }
}

/// <summary>The closed direction vocabulary the website reader also validates: the dominant world axis from the
/// parent zone's centre to the child zone's centre, plus `same` (no offset) and `unknown` (not measurable).</summary>
public static class RuntimeLogLayoutDirections
{
    public const string North = "north";
    public const string South = "south";
    public const string East = "east";
    public const string West = "west";
    public const string Up = "up";
    public const string Down = "down";
    public const string Same = "same";
    public const string Unknown = "unknown";

    public static bool IsKnown(string? direction) => direction is North or South or East or West or Up or Down or Same or Unknown;
}

/// <summary>One observation of a level generation attempt. `Complete` is false for the attempt's own start
/// record, which carries no zone and no connection; a completed observation carries the whole layout, and
/// <see cref="ElevatorLandedTick"/> is the tick the ride reached its landing state — null when the landing was
/// never observed, which is exactly the failure the website reports.</summary>
public readonly struct RuntimeLogLayout
{
    public bool Complete { get; init; }
    public long? ElevatorLandedTick { get; init; }
    public IReadOnlyList<RuntimeLogLayoutZone>? Zones { get; init; }
    public IReadOnlyList<RuntimeLogLayoutConnection>? Connections { get; init; }
}

/// <summary>Per-record budgets, mirroring the website reader's: a layout beyond them is refused instead of
/// written, because a line the other repository rejects is worse than no line at all.</summary>
public static class RuntimeLogLayoutLimits
{
    public const int Zones = 128;
    public const int Connections = 512;
    public const int TilesPerZone = 64;
}

/// <summary>Refuses a layout the contract does not allow, at the record point rather than at the reader. Every
/// refusal is a thrown <c>log-record</c>, so a caller that built an unreadable observation hears about it.</summary>
internal static class RuntimeLogLayoutCheck
{
    public static void Require(in RuntimeLogLayout layout)
    {
        var zones = layout.Zones;
        var connections = layout.Connections;
        RuntimeJson.Require(layout.Complete || (zones == null || zones.Count == 0) && (connections == null || connections.Count == 0),
            "log-record", "An incomplete layout observation carries no zone and no connection.");
        RuntimeJson.Require(layout.ElevatorLandedTick is null or >= 0, "log-record", "A layout landing tick is a simulation tick.");
        RuntimeJson.Require(zones == null || zones.Count <= RuntimeLogLayoutLimits.Zones, "log-record", "Layout zone budget exceeded.");
        RuntimeJson.Require(connections == null || connections.Count <= RuntimeLogLayoutLimits.Connections, "log-record", "Layout connection budget exceeded.");
        if (zones != null)
        {
            foreach (var zone in zones)
            {
                RuntimeJson.Require(zone.Zone is { Length: > 0 } and { Length: <= 256 }, "log-record", "A layout zone needs its locator.");
                RuntimeJson.Require(zone.Dimension >= 0 && zone.Layer >= 0 && zone.LocalIndex >= 0 && zone.Floor >= -1,
                    "log-record", "Layout zone coordinates are non-negative (floor -1 means unread).");
                RuntimeJson.Require(zone.Tiles == null || zone.Tiles.Count <= RuntimeLogLayoutLimits.TilesPerZone, "log-record", "Layout tile budget exceeded.");
                if (zone.Tiles == null) continue;
                foreach (var tile in zone.Tiles)
                    RuntimeJson.Require(tile is { Length: > 0 } and { Length: <= 256 }, "log-record", "A layout tile needs its geomorph name.");
            }
        }
        if (connections == null) return;
        foreach (var connection in connections)
        {
            RuntimeJson.Require(connection.From is { Length: > 0 } and { Length: <= 256 }, "log-record", "A layout connection needs both zone locators.");
            RuntimeJson.Require(connection.To is { Length: > 0 } and { Length: <= 256 }, "log-record", "A layout connection needs both zone locators.");
            RuntimeJson.Require(RuntimeLogLayoutDirections.IsKnown(connection.Direction), "log-record", "Unknown layout connection direction.");
        }
    }
}

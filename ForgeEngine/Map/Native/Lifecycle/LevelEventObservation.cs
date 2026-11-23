using System;
using System.Globalization;
using LevelGeneration;
using Player;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>The game-bound half of the level-event rows: every native member each row reads, and the one native
/// entry each of the three actions reaches. Nothing here decides whether something is news — the reports are
/// handed to <see cref="LevelEventModule"/>, which owns the identity of a fact and its transition.
///
/// Every member below is read from the build's own interop assemblies (20403457):
///
/// - `LG_WardenObjective_Reactor.OnStateChange` and its `OriginLayer` for a reactor wave, whose counter is the
///   replicated `pReactorState.stateCount`; `WardenObjectiveManager.OnLocalPlayerStartExpedition` for the landing.
///   The objective machine keeps three fixed layer slots, which is why a row about one layer carries the layer
///   name and not an instance id.
/// - `WO_HSUFindTakeSample.OnLocalPlayerSolvedObjectiveItem` is the HSU's own "the sample is in" transition.
/// - `CheckpointManager.OnRecallComplete` is the end of a recall, which is the row's "read a checkpoint back".
/// - `PlayerAgent.set_CourseNode` changes the actual player's location, including peer agents on the host;
///   `ZoneIndex.Coordinates` resolves the zone to the `zone` resource every zone reader publishes.
/// - `LG_DimensionPortal.m_portalEventData` is the Type 8 event created by Setup. Its execution and the player's
///   completed native relocation establish the source; IWarpableObject.OnWarp alone does not.
/// - `WorldEventManager.ExecuteEvent(WardenObjectiveEventData)` is the game's own level-event executor, which is
///   the one native entry the three actions use: the event struct's type member selects the effect and its own
///   replicated fields carry it to every client.
///
/// Nothing in this file writes the state an event owns. An action builds the struct and hands it over, so the
/// game's own replication path stays the only writer, exactly as a vanilla mount point does it.</summary>
internal static class LevelEventObservation
{
    // ---- the objective machine ----------------------------------------------------------------------


    /// <summary>The layer members this provider names. `LG_LayerType` declares exactly these three, so a layer
    /// this table cannot spell is a layer the game does not have rather than one that gets folded into the
    /// nearest.</summary>
    internal static LG_LayerType? LayerType(string layer) => layer switch
    {
        "main" => LG_LayerType.MainLayer,
        "secondary" => LG_LayerType.SecondaryLayer,
        "third" => LG_LayerType.ThirdLayer,
        _ => null
    };

    internal static string LayerName(LG_LayerType type) => type switch
    {
        LG_LayerType.SecondaryLayer => "secondary",
        LG_LayerType.ThirdLayer => "third",
        _ => "main"
    };

    // ---- the HSU ------------------------------------------------------------------------------------

    /// <summary>The container a solved objective item came out of. The HSU's own objective component keeps the
    /// item it handed over, so the reference is the item's placement identity when the world can answer one and
    /// null when it cannot — a port whose value is unknown is left out rather than invented.</summary>
    internal static string? ItemReference(int serialNumber, string? key)
    {
        if (string.IsNullOrEmpty(key) && serialNumber == 0) return null;
        return string.IsNullOrEmpty(key)
            ? "hsu/" + serialNumber.ToString(CultureInfo.InvariantCulture)
            : "hsu/" + key;
    }

    // ---- the zone a player entered ------------------------------------------------------------------

    /// <summary>The three-layer address of one native zone, computed from the zone's own members: the dimension
    /// index it belongs to, the layer it was built on and its authored `LocalIndex`. This is the same address
    /// <see cref="ZoneIndex"/> resolves for every other level-scope row, so a plan that mounts on a zone by
    /// address compares one spelling. `IDinLayer` is the build order inside the layer, which differs from the
    /// authored index whenever the layout lists its zones out of index order.</summary>
    internal static string? ZoneAddress(LG_Zone? zone)
    {
        if (zone == null || zone.m_layer is not { } zoneLayer) return null;
        string layer = LayerName(zoneLayer.m_type);
        return "dimension:" + ((int)zone.m_dimensionIndex).ToString(CultureInfo.InvariantCulture)
            + "/layer:" + layer
            + "/zone:" + ((int)zone.LocalIndex).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The zone a player's course-node change moved them into, as the `zone` resource every zone reader
    /// answers with (`gtfo.zone:&lt;d&gt;:&lt;l&gt;:&lt;z&gt;`), or null when the zone did not change or has no coordinates.</summary>
    internal static ResourceRef? ZoneAfterChange(PlayerAgent? agent, LG_Zone? previous)
    {
        var current = agent?.CourseNode?.m_zone;
        if (current == null || previous != null && current.Pointer == previous.Pointer) return null;
        return ZoneIndex.Coordinates(current) is { } at
            ? new ResourceRef(RuntimeZones.ResourceKind, RuntimeZones.Id(at.Dimension, at.Layer, at.Zone))
            : null;
    }

    // ---- the dimension portal -----------------------------------------------------------------------

    /// <summary>One built portal's native serial, source Zone and generated room instance, scoped by the event's
    /// world epoch. Missing native identity is never substituted with another door or an authored pose.</summary>
    internal static string? PortalId(LG_DimensionPortal? portal)
    {
        var node = portal?.SpawnNode;
        var room = node?.m_area?.m_geomorph;
        var zone = ZoneAddress(node?.m_zone);
        if (portal == null || room == null || zone == null) return null;
        return EntityKinds.MapObject + ":portal/" + portal.m_serialNumber.ToString(CultureInfo.InvariantCulture) + "/" + zone
            + "/room:" + room.GetInstanceID().ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The dimension a course node belongs to. `AIG_CourseNode.m_dimension` is the node's own dimension
    /// object and `Dimension.DimensionIndex` is the member this vocabulary spells, so the "from" side of a portal
    /// warp is the dimension the player's previous node was built in — read, not remembered.</summary>
    internal static int NodeDimension(AIGraph.AIG_CourseNode? node)
        => node == null ? 0 : (int)(node.m_dimension
            ?? throw new InvalidOperationException("The course node has no dimension.")).DimensionIndex;
}

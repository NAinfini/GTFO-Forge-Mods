using System;
using System.Collections.Generic;
using AIGraph;
using ChainedPuzzles;
using LevelGeneration;
using Player;
using UnityEngine;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The owned member set of one room- or zone-covered scan, resolved from the generated level and nothing else.
/// §4.2.9 fixes the rule: a room scan's members are the players and required items standing on the scan's own
/// course node, and a zone scan's are those standing on any node of the zone that node belongs to. A member is
/// resolved by the node it is on — the player's own `CourseNode`, a required item's own node — because a node is
/// the level's own answer to where a member is, and a distance to the scan is exactly what the ownership rule
/// replaces.
///
/// The set is computed only where it is published: the host, inside the scan core's own counting entry. A client
/// never computes members; it rebuilds them from the replicated player slots, which is what keeps one state and
/// not two.
///
/// Two limits are the replicated state's own: four player slots and eight required-item bits. A member set larger
/// than either cannot be published, so the scan is refused instead of being published truncated.
/// </summary>
internal sealed class ScanMembership
{
    private readonly IReadOnlyList<AIG_CourseNode> _nodes;
    private readonly HashSet<IntPtr> _owned;
    // The counting entry reads the member set on every beat; its answers are consumed before the next beat, so the
    // list and the flag array are this set's own buffers and not a fresh allocation per beat.
    private readonly List<PlayerAgent> _players = new();
    private bool[] _flags = Array.Empty<bool>();

    private ScanMembership(ScanCoverageMode mode, List<AIG_CourseNode> nodes, string detail)
    {
        Mode = mode;
        _nodes = nodes.AsReadOnly();
        _owned = new HashSet<IntPtr>();
        foreach (var node in nodes) _owned.Add(node.Pointer);
        Detail = detail;
    }

    /// <summary>Which ownership rule this set is: the scan's own room or the zone it stands in.</summary>
    internal ScanCoverageMode Mode { get; }
    /// <summary>The owned nodes as a diagnostic names them, so a refusal and an applied row read the same way.</summary>
    internal string Detail { get; }

    /// <summary>The room rule: the scan core's own course node, which is the node the level registered the scan
    /// on. A core the level gave no node is refused by name, because an ownership rule with no node owns nobody.</summary>
    internal static bool TryRoom(CP_Bioscan_Core core, out ScanMembership? membership, out string? reason)
    {
        membership = null;
        reason = null;
        var node = core.m_courseNode;
        if (node == null || node.WasCollected)
        {
            reason = "the scan core carries no course node";
            return false;
        }
        membership = new ScanMembership(ScanCoverageMode.Room, new List<AIG_CourseNode> { node }, "node " + Describe(node));
        return true;
    }

    /// <summary>The zone rule: every course node of the zone the scan's own node stands in. A zone that holds no
    /// readable node is refused, because a zone with no nodes would own nobody while claiming to own the zone.</summary>
    internal static bool TryZone(CP_Bioscan_Core core, out ScanMembership? membership, out string? reason)
    {
        membership = null;
        reason = null;
        var node = core.m_courseNode;
        var zone = node == null || node.WasCollected ? null : node.m_zone;
        if (zone == null || zone.WasCollected)
        {
            reason = "the scan core's course node carries no zone";
            return false;
        }
        var nodes = zone.m_courseNodes;
        if (nodes == null || nodes.Count == 0)
        {
            reason = "the zone holds no course nodes";
            return false;
        }
        var owned = new List<AIG_CourseNode>(nodes.Count);
        for (var index = 0; index != nodes.Count; index++)
        {
            var candidate = nodes[index];
            if (candidate == null || candidate.WasCollected) continue;
            owned.Add(candidate);
        }
        if (owned.Count == 0)
        {
            reason = "none of the zone's course nodes read";
            return false;
        }
        membership = new ScanMembership(ScanCoverageMode.Zone, owned,
            owned.Count + " node(s) of zone " + Describe(zone));
        return true;
    }

    /// <summary>
    /// The players of this level that stand in the owned node set, in the level's own player order. The order is
    /// the level's because the member list is published as it is read, and two machines reading the same level
    /// must produce the same list. The list is a buffer of this set, valid until the next call.
    /// </summary>
    internal bool TryPlayers(out List<PlayerAgent> players, out string? reason)
    {
        players = _players;
        _players.Clear();
        reason = null;
        var readable = Players.Readable;
        if (!readable)
        {
            reason = "the level's own player table does not read";
            return false;
        }
        for (var index = 0; index != Players.Count; index++)
        {
            var player = Players.At(index);
            if (player == null || player.WasCollected) continue;
            if (Owns(player.CourseNode)) players.Add(player);
        }
        return true;
    }

    /// <summary>
    /// One flag per required item of the scan, in the core's own item order: whether that item stands in the
    /// owned node set. An item whose own node cannot be read refuses the whole set, because a required item that
    /// is neither in nor out would be published as a status nobody decided. The count is not checked here: how
    /// many items the replicated state can carry is the row's own limit, and the caller is the one that knows it
    /// is about to publish. The array is a buffer of this set, valid until the next call.
    /// </summary>
    internal bool TryRequiredItems(CP_Bioscan_Core core, out bool[] status, out string? reason)
    {
        status = Array.Empty<bool>();
        reason = null;
        var items = core.m_reqItems;
        if (items == null) return true;
        if (_flags.Length != items.Count) _flags = new bool[items.Count];
        var flags = _flags;
        for (var index = 0; index != items.Count; index++)
        {
            var item = items[index];
            if (item == null || item.WasCollected)
            {
                reason = "required item " + index + " does not read";
                return false;
            }
            if (Node(item) is not { } node)
            {
                reason = "required item " + index + " is of no kind this build can read a node from";
                return false;
            }
            flags[index] = Owns(node);
        }
        status = flags;
        return true;
    }

    /// <summary>
    /// The radius the host's own distance gate is enlarged to, so the scan's counting entry keeps being called
    /// while its members stand anywhere in the owned rooms. It is the greatest distance from the scan's own
    /// position to a corner of an owned room's own bounds, which is the level's own extent for the room and not a
    /// number this layer invents. The radius is a coarse gate only: the published members are still the owned
    /// node set, and the scan's ring is left at the preset size.
    /// </summary>
    internal bool TryGateRadius(CP_Bioscan_Core core, out double radius, out string? reason)
    {
        radius = 0;
        reason = null;
        var origin = core.m_position;
        var found = false;
        foreach (var node in _nodes)
        {
            var area = node.m_area;
            if (area == null || area.WasCollected) continue;
            found = true;
            radius = Math.Max(radius, Distance(origin, area.m_bounds));
        }
        if (!found)
        {
            reason = "none of the owned nodes carries a readable area to measure";
            return false;
        }
        return true;
    }

    internal bool Owns(AIG_CourseNode? node)
        => node != null && !node.WasCollected && _owned.Contains(node.Pointer);
    internal bool OwnsItem(iWardenObjectiveItem item)
    {
        var node = Node(item);
        if (node == null || node.WasCollected) throw new InvalidOperationException("a required item's native course node is unreadable");
        return Owns(node);
    }

    /// <summary>
    /// The node one required item stands on, through the concrete item kind it really is: a carry item's own
    /// course node, and the node an HSU or a terminal was spawned on. An item of another kind has no node this
    /// build reads, which the caller refuses rather than guessing.
    /// </summary>
    private static AIG_CourseNode? Node(iWardenObjectiveItem item)
    {
        if (item.TryCast<CarryItemPickup_Core>() is { } pickup && !pickup.WasCollected) return pickup.CourseNode;
        if (item.TryCast<LG_HSU>() is { } hsu && !hsu.WasCollected) return hsu.SpawnNode;
        if (item.TryCast<LG_ComputerTerminal>() is { } terminal && !terminal.WasCollected) return terminal.SpawnNode;
        return null;
    }

    /// <summary>The greatest distance from one point to a box's own corners: the component-wise offset from the
    /// centre plus the box's own extent, which is the farthest a point inside that box can be.</summary>
    private static double Distance(Vector3 origin, Bounds bounds)
    {
        var center = bounds.center;
        var extents = bounds.extents;
        var x = Math.Abs((double)origin.x - center.x) + extents.x;
        var y = Math.Abs((double)origin.y - center.y) + extents.y;
        var z = Math.Abs((double)origin.z - center.z) + extents.z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>One course node as a diagnostic names it: the level's own coordinates of the zone it stands in,
    /// which are the same three numbers an address is written from.</summary>
    private static string Describe(AIG_CourseNode node) => Describe(node.m_zone);

    private static string Describe(LG_Zone? zone)
        => zone == null || zone.WasCollected || zone.m_layer is not { } layer
            ? "<no-zone>"
            : ((int)zone.m_dimensionIndex).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"
                + ((int)layer.m_type).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"
                + ((int)zone.LocalIndex).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

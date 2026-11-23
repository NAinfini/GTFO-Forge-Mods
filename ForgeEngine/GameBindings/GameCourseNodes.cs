using System;
using AIGraph;
using Agents;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeEngine.GameBindings;

/// <summary>The one native read of where an agent stands: the zone and the area its own course node belongs to. The
/// Player and Enemy providers answer the kernel's zone reads for their own lives through it, each with its own refusal
/// code, so a trigger row that compares a player's place with an enemy's compares one read rather than two.
///
/// Every way the read can fail is the caller's one refusal, never a bare null: a course node, zone, layer or area that
/// is missing or was collected mid-read, an area the zone's own list does not hold, and a chain that throws. A caller
/// that read any of them as "stands in no zone" or "area zero" would exclude a life it simply could not place. The
/// level objects can be collected at any point of the chain, so each read is one guarded step; a native failure is
/// handed to <c>contained</c> before it becomes the refusal.</summary>
internal static class GameCourseNodes
{
    /// <summary>The zone <paramref name="agent"/> stands in, named by the level's three coordinates — the same text
    /// the Map provider names a zone with.</summary>
    internal static EntityReference Zone(Agent agent, long worldEpoch, string code, Action<Exception>? contained = null)
    {
        try
        {
            var (_, zone) = Placed(agent, code);
            var layer = zone.m_layer;
            if (layer == null) throw new RuntimeContractException(code, "This life's zone names no layer.");
            if (layer.WasCollected) throw new RuntimeContractException(code, "This life's zone layer was collected before it could be read.");
            return RuntimeZones.Reference(worldEpoch, (int)zone.m_dimensionIndex, (int)layer.m_type, (int)zone.LocalIndex);
        }
        catch (RuntimeContractException) { throw; }
        catch (Exception error)
        {
            contained?.Invoke(error);
            throw new RuntimeContractException(code, "This life's zone does not read: " + error.GetType().Name);
        }
    }

    /// <summary>Which area of its own zone <paramref name="agent"/> stands in, as the index the zone's own list holds
    /// that area at — the number the author writes on the zone-members row's `area` port. It walks the same course
    /// node as <see cref="Zone"/>.</summary>
    internal static int Area(Agent agent, string code, Action<Exception>? contained = null)
    {
        try
        {
            var (courseNode, zone) = Placed(agent, code);
            var area = courseNode.m_area;
            if (area == null) throw new RuntimeContractException(code, "This life's course node names no area.");
            var areas = zone.m_areas;
            if (areas == null) throw new RuntimeContractException(code, "This life's zone holds no area list.");
            for (var index = 0; index < areas.Count; index++)
                if (areas[index] != null && areas[index].Pointer == area.Pointer) return index;
            throw new RuntimeContractException(code, "This life's area is not one of its zone's own areas.");
        }
        catch (RuntimeContractException) { throw; }
        catch (Exception error)
        {
            contained?.Invoke(error);
            throw new RuntimeContractException(code, "This life's zone area does not read: " + error.GetType().Name);
        }
    }

    /// <summary>The agent's own course node and the zone it belongs to, both still live.</summary>
    private static (AIG_CourseNode Node, LG_Zone Zone) Placed(Agent agent, string code)
    {
        var courseNode = agent.CourseNode;
        if (courseNode == null) throw new RuntimeContractException(code, "This life's course node does not read.");
        var zone = courseNode.m_zone;
        if (zone == null) throw new RuntimeContractException(code, "This life's course node names no zone.");
        if (zone.WasCollected) throw new RuntimeContractException(code, "This life's zone was collected before it could be read.");
        return (courseNode, zone);
    }
}

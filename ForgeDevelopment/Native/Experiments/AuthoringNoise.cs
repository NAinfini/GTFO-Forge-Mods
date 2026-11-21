using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

// Authoring drive steps for sound readbacks: a read-only dump of the course graph and one noise
// through the game's own noise entry. Nothing here changes level state; the noise only wakes what the game itself would wake.
internal static class AuthoringNoise
{
    private const int MaxNodes = 160;

    /// <summary>Every course node: area, graph source (world), and each portal's gate type, traversable flag and opposite node.</summary>
    internal static string Graph()
    {
        var rows = new List<object>();
        foreach (var area in UnityEngine.Object.FindObjectsOfType<LG_Area>().Where(a => a != null && !a.WasCollected && a.m_courseNode != null).Take(MaxNodes))
        {
            var node = area.m_courseNode;
            var portals = new List<object>();
            if (node.m_portals != null)
                for (int i = 0; i < node.m_portals.Count; i++)
                {
                    var portal = node.m_portals[i];
                    if (portal == null) continue;
                    var other = portal.m_nodeA != null && portal.m_nodeA.Pointer == node.Pointer ? portal.m_nodeB : portal.m_nodeA;
                    var gate = portal.m_gate;
                    portals.Add(new
                    {
                        gate = gate?.name,
                        type = gate?.Type.ToString(),
                        traversable = portal.IsTraversable,
                        fromStart = gate?.m_isTraversableFromStart,
                        door = gate?.SpawnedDoor != null,
                        other = other?.NodeID
                    });
                }
            var source = area.m_groupSource != null ? area.m_groupSource.transform.position : area.transform.position;
            var cluster = node.m_nodeCluster; var volume = area.m_geomorph?.m_nodeVolume;
            rows.Add(new
            {
                node = node.NodeID,
                area = area.name,
                room = area.m_geomorph?.name,
                native = area.m_geomorph?.m_geoPrefab?.name,
                clusterOwn = cluster?.m_courseNode != null && cluster.m_courseNode.Pointer == node.Pointer,
                clusterVolumeOwn = cluster?.m_nodeVolume != null && volume != null && cluster.m_nodeVolume.Pointer == volume.Pointer,
                zone = area.m_zone?.Alias,
                source = new[] { MathF.Round(source.x, 2), MathF.Round(source.y, 2), MathF.Round(source.z, 2) },
                portals
            });
        }
        return JsonSerializer.Serialize(new { schemaVersion = "1.0.0", rows });
    }

    /// <summary>{"position":[x,y,z],"radiusMin":0,"radiusMax":100,"type":"insta|detectable|investigate","neighbours":true,"raycast":false}: one noise
    /// through ForgeCore.Combat.Noise (the game's NoiseManager.MakeNoise) at the course node under the position.</summary>
    internal static string Make(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var p = root.GetProperty("position");
        var position = new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
        if (!Dimension.TryGetCourseNodeFromPos(position, out var node) || node == null) throw new InvalidOperationException("noise.no-course-node");
        var type = root.GetProperty("type").GetString() switch
        {
            "insta" => NM_NoiseType.InstaDetect,
            "detectable" => NM_NoiseType.Detectable,
            "investigate" => NM_NoiseType.Investigate,
            _ => throw new ArgumentException("noise.type")
        };
        bool made = ForgeCore.Combat.Noise(position, root.GetProperty("radiusMin").GetSingle(), root.GetProperty("radiusMax").GetSingle(), 1f, node, type,
            root.GetProperty("neighbours").GetBoolean(), root.GetProperty("raycast").GetBoolean());
        return JsonSerializer.Serialize(new { schemaVersion = "1.0.0", made, node = node.NodeID });
    }
}

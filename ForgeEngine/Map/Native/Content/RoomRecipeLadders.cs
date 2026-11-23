using System.Collections.Generic;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>The room's vanilla ladders. Native LG_LateGeomorphScanJob finds each one and queues LG_BuildLadderJob and LG_BuildLadderAIGNodeJob;
/// those build the rungs, the collider and the two graph nodes that join floors the walkable mesh does not join, so the area's cluster scan
/// reaches a doorway a ladder leads to. A room without them has floors its doorways never connect to.</summary>
internal static class RoomRecipeLadders
{
    internal static void Build(Transform room, IReadOnlyList<LG_Area> areas, RoomBuildRecipe recipe)
    {
        foreach (var row in recipe.Ladders)
        {
            var node = new GameObject("Forge ladder " + row.Id);
            node.transform.SetParent(areas[recipe.AreaIndex(row.Area)].transform, false);
            node.transform.position = room.TransformPoint(V(row.Position));
            node.transform.rotation = room.rotation * Quaternion.Euler(V(row.Rotation));
            var ladder = node.AddComponent<LG_Ladder>();
            ladder.m_width = row.Width; ladder.m_enemyClimbingOnly = row.EnemyOnly;
            if (row.Top == null) continue;
            var top = new GameObject("Top"); top.transform.SetParent(node.transform, false);
            top.transform.position = room.TransformPoint(V(row.Top));
            ladder.m_topFloor = top.transform;
        }
    }
    private static Vector3 V(float[] value) => new(value[0], value[1], value[2]);
}

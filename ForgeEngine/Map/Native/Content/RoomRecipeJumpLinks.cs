using UnityEngine;
using UnityEngine.AI;

namespace ForgeMap.Native;

/// <summary>The recipe's Unity OffMeshLinks, built the way vanilla geomorphs carry their OffLink_Jump objects: one object per link
/// holding the OffMeshLink, with child transforms Start and End as its two ends. Room-local points, so every link hangs under the room root.</summary>
internal static class RoomRecipeJumpLinks
{
    internal const string GroupName = "Jump links";

    internal static void Build(Transform root, RoomBuildRecipe recipe)
    {
        if (recipe.JumpLinks.Count == 0) return;
        var group = new GameObject(GroupName);
        group.transform.SetParent(root, false);
        foreach (var row in recipe.JumpLinks)
        {
            var entry = new GameObject(row.Id);
            entry.transform.SetParent(group.transform, false);
            var start = End(entry.transform, "Start", row.Start);
            var end = End(entry.transform, "End", row.End);
            var link = entry.AddComponent<OffMeshLink>();
            link.startTransform = start; link.endTransform = end;
            link.area = row.Area; link.biDirectional = row.Bidirectional;
            link.costOverride = -1f; link.activated = true;
            // The built room is cloned and moved into place after this: the link follows its end transforms there.
            link.autoUpdatePositions = true;
        }
    }
    private static Transform End(Transform parent, string name, float[] point)
    {
        var end = new GameObject(name);
        end.transform.SetParent(parent, false);
        end.transform.localPosition = new Vector3(point[0], point[1], point[2]);
        return end.transform;
    }
}

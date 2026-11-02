using System;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>Where the built-in <c>Item</c> payload stands each pickup: one per pod bay tier (two tiers in each of the two bays), centred on the tier floor, upright, its
/// longer side across the bay and its back against the divider when it is deeper than the bay. Without a pod (a direct arrival,
/// or no pod model) the pickups are spread on the ground around the landing point.</summary>
internal static class PodPacking
{
    /// <summary>Ground layout without a pod, one offset per pickup, in the pod's frame.</summary>
    private static readonly Vector3[] Spread = { new(0.35f, 0.05f, 0.35f), new(0.35f, 0.05f, -0.35f), new(-0.35f, 0.05f, 0.35f), new(-0.35f, 0.05f, -0.35f) };

    internal static (Vector3 Position, Quaternion Rotation) Pose(uint itemId, Vector3 landing, Quaternion podRotation, int index, bool inPod)
    {
        if (!inPod || !OrdnanceAssets.TryBay(landing, podRotation, index, false, out var bay))
            return (landing + podRotation * Spread[index], podRotation);
        if (!PrefabBounds(itemId, out var bounds))
        {
            return (bay.Position, bay.Rotation);
        }
        var turned = bounds.size.z > bounds.size.x;
        var rotation = bay.Rotation * (turned ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity);
        var across = turned ? bounds.size.z : bounds.size.x;
        var depth = turned ? bounds.size.x : bounds.size.z;
        var outward = Math.Max(0f, (depth - bay.Size.z) / 2f);
        var foot = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        return (bay.Position + bay.Rotation * (Vector3.forward * outward) - rotation * foot, rotation);
    }

    /// <summary>The pickup prefab's mesh bounds in its own frame, over every piece the game assembles into the pickup.</summary>
    private static bool PrefabBounds(uint itemId, out Bounds bounds)
    {
        bounds = default;
        var prefabs = ItemSpawnManager.GetItemPrefabs(itemId, ItemMode.Pickup);
        if (prefabs == null || prefabs.Count == 0 || prefabs[0] == null) return false;
        var found = false;
        foreach (var prefab in prefabs)
        {
            if (prefab == null) continue;
            // The pieces after the first are attached at the pickup's origin, so each piece's own frame is the pickup's.
            var toPickup = prefab.transform.worldToLocalMatrix;
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || filter.GetComponent<MeshRenderer>() == null) continue;
                var matrix = toPickup * filter.transform.localToWorldMatrix;
                var b = mesh.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var p = matrix.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents,
                        new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f)));
                    if (!found) { bounds = new Bounds(p, Vector3.zero); found = true; }
                    else bounds.Encapsulate(p);
                }
            }
        }
        return found && bounds.size.sqrMagnitude > 1e-6f;
    }
}

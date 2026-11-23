using System;
using System.Collections.Generic;
using LevelGeneration;
using UnityEngine;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>The room's own colliders (RoomBuildCollider): each a node built by ForgeCore.StaticColliders under its area's props,
/// so the navigation build takes the box and mesh ones on navigation layers as walkable ground, as it takes vanilla geometry. A vanilla
/// collision mesh is the very mesh of the vanilla MeshCollider its donor names, read from the loaded prefab; a package mesh is built
/// from its own geometry. A mesh that cannot be found or does not match refuses the room by name.</summary>
internal static class RoomRecipeColliders
{
    internal const string Code = "room.build-colliders";

    internal static void Build(GameObject[] props, RoomBuildRecipe recipe)
    {
        if (recipe.Colliders.Count == 0) return;
        var meshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        foreach (var mesh in recipe.CollisionMeshes) meshes.Add(mesh.Id, Resolve(recipe, mesh));
        var layers = new Dictionary<string, int>(StringComparer.Ordinal);
        int navigation = 0;
        foreach (var row in recipe.Colliders)
        {
            if (!layers.TryGetValue(row.Layer, out var layer)) layers.Add(row.Layer, layer = ForgeCore.StaticColliders.Layer(row.Layer));
            var node = ForgeCore.StaticColliders.Node(props[recipe.AreaIndex(row.Area)].transform, row.Id, layer, V(row.Position), Quaternion.Euler(V(row.Rotation)), V(row.Scale));
            switch (row.Shape)
            {
                case "box": ForgeCore.StaticColliders.Box(node, RoomRecipeAirWalls.Center, RoomRecipeAirWalls.Size); break;
                case "mesh": ForgeCore.StaticColliders.MeshCollider(node, meshes[row.Mesh!]); break;
                case "sphere": ForgeCore.StaticColliders.Sphere(node, V(row.Center!), row.Radius); break;
                case "capsule": ForgeCore.StaticColliders.Capsule(node, V(row.Center!), row.Radius, row.Height, row.Direction); break;
                default: throw new InvalidOperationException("recipe-collider-shape: " + row.Id);
            }
            if (row.Shape is "box" or "mesh" && ForgeCore.StaticColliders.Navigable(layer)) navigation++;
        }
        if (MapLog.Logs(RuntimeLogLevel.Info)) MapLog.Record(RuntimeLogLevel.Info, Code,
            "Authored room colliders built; the navigation build takes the box and mesh ones on navigation layers.",
            RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("colliders", recipe.Colliders.Count),
            RuntimeLogValue.Of("meshes", meshes.Count), RuntimeLogValue.Of("navigationSources", navigation),
            RuntimeLogValue.Of("navigationMask", (long)(uint)LayerManager.MASK_UNITY_NAVMESH_GENERATION));
    }

    private static Mesh Resolve(RoomBuildRecipe recipe, RoomBuildCollisionMesh row)
    {
        if (row.Positions != null) return ForgeCore.StaticColliders.Mesh(recipe.Id + "/" + row.Id, row.Positions, row.Indices!);
        return Vanilla(row) ?? throw new InvalidOperationException("recipe-collision-mesh-missing: " + row.Id);
    }

    /// <summary>The donor's MeshCollider mesh: the prefab, through the spawner at its name path to that spawner's prefab, then child indices.</summary>
    private static Mesh? Vanilla(RoomBuildCollisionMesh row)
    {
        var prefab = ForgeCore.GameAssets.Get<GameObject>(row.Prefab!) ?? throw new InvalidOperationException("recipe-collision-mesh-donor-not-loaded: " + row.Prefab);
        var at = prefab.transform;
        if (row.Spawner != null)
        {
            var spawner = at.Find(row.Spawner)?.GetComponent<LG_PrefabSpawner>();
            if (spawner == null || spawner.m_prefab == null) return null;
            at = spawner.m_prefab.transform;
        }
        foreach (var index in row.Node!)
        {
            if (index >= at.childCount) return null;
            at = at.GetChild(index);
        }
        return ForgeCore.StaticColliders.ColliderMesh(at, row.Name!, row.Vertices);
    }

    private static Vector3 V(float[] values) => new(values[0], values[1], values[2]);
}

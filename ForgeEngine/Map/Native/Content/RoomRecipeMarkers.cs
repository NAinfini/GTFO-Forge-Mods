using System;
using System.Collections.Generic;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

internal static class RoomRecipeMarkers
{
    /// <summary>Every placement point is built here from what the recipe names: a function point gets the marker producer class and
    /// datablock id the website wrote for it, an event point a world event object named by its key. Nothing is cloned from a vanilla room.</summary>
    internal static void Build(GameObject room, IReadOnlyList<LG_Area> areas, RoomBuildRecipe recipe)
    {
        var points = new HashSet<(string, uint, float, float, float)>();
        foreach (var marker in recipe.Markers)
        {
            if (marker.Source is { } source)
            {
                // A vanilla marker offering several functions is written once per function; the game holds one producer there, which offers them all.
                if (points.Add((source.Producer, source.Block, marker.Position[0], marker.Position[1], marker.Position[2])))
                    BuildProducer(room, areas, recipe, marker, source);
                continue;
            }
            var holder = Holder(room, areas, recipe, marker, marker.Key!);
            holder.AddComponent<LG_WorldEventObject>();
            RoomRecipeHost.MarkerStaged(recipe, marker, recipe.ResourceKey, nameof(LG_WorldEventObject), null, 0, 0);
        }
    }

    private static GameObject Holder(GameObject room, IReadOnlyList<LG_Area> areas, RoomBuildRecipe recipe, RoomPlacementMarker marker, string name)
    {
        int areaIndex = recipe.AreaIndex(marker.Area);
        if (areaIndex < 0 || areaIndex >= areas.Count) throw new InvalidOperationException("recipe-marker-area: " + marker.Id);
        var holder = new GameObject(name);
        holder.transform.SetParent(areas[areaIndex].transform, false);
        SetRoomPose(holder.transform, room.transform, areas[areaIndex].transform, marker.Position, marker.Yaw);
        return holder;
    }

    /// <summary>The marker producer class the recipe names, with its datablock: native rolls that block's compositions at this spot.</summary>
    private static void BuildProducer(GameObject room, IReadOnlyList<LG_Area> areas, RoomBuildRecipe recipe, RoomPlacementMarker marker, RoomMarkerSource own)
    {
        var holder = Holder(room, areas, recipe, marker, marker.Category);
        if (AddProducer(holder, own.Producer, own.Block) is not { } producer) throw new InvalidOperationException("recipe-marker-producer: " + marker.Id);
        RoomRecipeHost.MarkerStaged(recipe, marker, recipe.ResourceKey, own.Producer, producer.MarkerDataBlockType, own.Block, 0);
    }

    /// <summary>Adds the named marker producer class to the holder with this datablock id, or returns null when the class is
    /// not a producer or does not take the id. The game only hands out a marker point that has a producer
    /// (LG_MarkerSpawnerGroup.GetRandomSpawner returns null for a point whose m_producerSource is null, build 20403457).</summary>
    internal static LG_MarkerProducer? AddProducer(GameObject holder, string producerClass, uint block)
    {
        // The concrete producers live in the prefab scripts assembly, not beside LG_MarkerProducer.
        var type = System.Reflection.Assembly.Load("AssetPrefabs-ASM").GetType(producerClass);
        var blockField = type?.GetProperty("m_markerDataBlockID");
        if (type == null || blockField == null || !typeof(LG_MarkerProducer).IsAssignableFrom(type)) return null;
        var component = holder.AddComponent(Il2CppInterop.Runtime.Il2CppType.From(type));
        blockField.SetValue(Activator.CreateInstance(type, component.Pointer), block);
        var producer = component.Cast<LG_MarkerProducer>();
        producer.m_allowFunction = true;
        return producer.MarkerDataBlockID == block ? producer : null;
    }

    private static void SetRoomPose(Transform target, Transform room, Transform area, float[] position, float yaw)
    {
        target.localPosition = area.InverseTransformPoint(room.TransformPoint(new Vector3(position[0], position[1], position[2])));
        target.localRotation = Quaternion.Inverse(area.rotation) * room.rotation * Quaternion.Euler(0, yaw, 0);
        target.localScale = Vector3.one;
    }
}

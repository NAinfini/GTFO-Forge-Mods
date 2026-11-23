using System;
using AIGraph;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>The size override of a recipe room's native node volume, covering <see cref="RoomBuildRecipe.NavigationExtent"/>.
/// The game reads it in AIG_NodeVolume.PrepareBuild, through AIG_GeomorphNodeVolume.SetSize (RVA 0x1A0BC60: the override's
/// localScale becomes the world size of an axis-aligned box) and get_Position (RVA 0x1A0CFE0: the override's world position is
/// the box centre); the override's rotation is never applied. The box is therefore sized once in room axes when the template is
/// built and again in world axes when the native generator places a rotated copy, before any node volume is prepared.</summary>
internal static class RoomRecipeNavigationBounds
{
    internal const string Name = "Navigation bounds";

    /// <summary>Adds the override under the room's first area of a template still at the origin and hands it to the volume.</summary>
    internal static void Build(LG_Geomorph geo, AIG_GeomorphNodeVolume volume, LG_Area firstArea, RoomBuildRecipe recipe)
    {
        var bounds = new GameObject(Name); bounds.transform.SetParent(firstArea.transform, false);
        var (min, max) = recipe.NavigationExtent();
        // The first area is a direct child of the room root: its local pose maps room space into the override's parent space.
        var area = firstArea.transform;
        var centre = new Vector3((min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2);
        bounds.transform.localPosition = Quaternion.Inverse(area.localRotation) * (centre - area.localPosition);
        bounds.transform.localRotation = Quaternion.identity;
        volume.m_sizeOverrideTransform = bounds.transform;
        Orient(geo, recipe, Quaternion.identity);
    }

    /// <summary>Sizes the override as the world-axis box around the room-space extent turned by <paramref name="roomRotation"/>.
    /// A copy turned a quarter turn swaps its plan width and depth; any other turn takes the enclosing box.</summary>
    internal static void Orient(LG_Geomorph geo, RoomBuildRecipe recipe, Quaternion roomRotation)
    {
        // The volume is the component on the room's root: the geomorph's own m_nodeVolume field is only filled in by the game later,
        // and an Il2Cpp property read hands back the declared base type, so neither `as` nor that field can be relied on here.
        var bounds = geo.GetComponent<AIG_GeomorphNodeVolume>()?.m_sizeOverrideTransform
            ?? throw new InvalidOperationException("recipe-navigation-bounds-missing");
        var (min, max) = recipe.NavigationExtent();
        Vector3 x = roomRotation * new Vector3(max[0] - min[0], 0, 0), y = roomRotation * new Vector3(0, max[1] - min[1], 0),
            z = roomRotation * new Vector3(0, 0, max[2] - min[2]);
        bounds.localScale = new Vector3(Math.Abs(x.x) + Math.Abs(y.x) + Math.Abs(z.x), Math.Abs(x.y) + Math.Abs(y.y) + Math.Abs(z.y),
            Math.Abs(x.z) + Math.Abs(y.z) + Math.Abs(z.z));
    }
}

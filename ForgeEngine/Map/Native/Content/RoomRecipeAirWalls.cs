using System;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>An air wall built the way vanilla builds its invisible walls (catalog/native/invisible-wall-facts.json): a GameObject
/// with one non-trigger BoxCollider and nothing else, no renderer and no script. What it blocks is only its physics layer.
/// <c>InvisibleWall</c> is vanilla's players-only layer: its collision-matrix row meets PlayerMover alone, so enemies, which move on
/// the navigation graph and never collide with it, walk through. For a wall enemies must also respect, vanilla's
/// <c>Default_BlockGraph</c> layer is used (the one vanilla <c>InvisWall (1)</c> stands on): it meets PlayerMover and blocks the
/// AI graph.</summary>
internal static class RoomRecipeAirWalls
{
    /// <summary>Vanilla's own layer names (TagManager), resolved by name because LayerManager has no constant for the first.</summary>
    internal const string PlayersOnlyLayer = "InvisibleWall", EveryoneLayer = "Default_BlockGraph";
    /// <summary>The unit box: 4 m cubed standing on the pivot, so a part's scale sizes it like any piece.</summary>
    internal static readonly Vector3 Size = new(4, 4, 4), Center = new(0, 2, 0);

    internal static void Build(Transform parent, RoomBuildAirWall wall)
    {
        var entry = new GameObject(wall.Id);
        entry.transform.SetParent(parent, false);
        entry.transform.localPosition = new Vector3(wall.Position[0], wall.Position[1], wall.Position[2]);
        entry.transform.localRotation = Quaternion.Euler(wall.Rotation[0], wall.Rotation[1], wall.Rotation[2]);
        entry.transform.localScale = new Vector3(wall.Scale[0], wall.Scale[1], wall.Scale[2]);
        var layer = LayerMask.NameToLayer(wall.PlayersOnly ? PlayersOnlyLayer : EveryoneLayer);
        if (layer < 0) throw new InvalidOperationException("recipe-air-wall-layer-missing: " + (wall.PlayersOnly ? PlayersOnlyLayer : EveryoneLayer));
        entry.layer = layer;
        var box = entry.AddComponent<BoxCollider>();
        box.isTrigger = false; box.size = Size; box.center = Center;
    }
}

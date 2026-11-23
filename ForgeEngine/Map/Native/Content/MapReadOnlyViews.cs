using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>The read-only views of Map's generation receipts that ForgeDevelopment compiles against. Each member forwards to the
/// internal member that records the receipt; nothing here writes, and Map does not depend on the caller.</summary>
internal static class MapReadOnlyViews
{
    /// <summary>The recipe JSON of a placed recipe room and the native OnBuild result of each authored part, or null for a room without a recipe.
    /// A part whose build did not run is absent from the dictionary, so the observer can name its authored id.</summary>
    public static (string Recipe, IReadOnlyDictionary<string, GameObject> Parts)? ReadBuiltParts(LG_Geomorph room)
        => RoomRecipeSpatialReadback.ReadBuiltParts(room);

    /// <summary>Raised after Map places a room; a handler must not throw.</summary>
    public static event Action<LG_Geomorph> RoomPlaced
    {
        add => RoomRecipeHost.RoomPlaced += value;
        remove => RoomRecipeHost.RoomPlaced -= value;
    }
}

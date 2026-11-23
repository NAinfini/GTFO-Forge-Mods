using System;
using ForgeCore;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Hands ForgeCore the world extent and node volume of every placed recipe room, as soon as the room is placed and before
/// any gate of it is connected: the game finds the volume of a position by the 64 m grid cell alone, so a room stacked above another
/// or reaching into a neighbour's cell is otherwise answered with the wrong volume while its gates are linked. The extent comes from
/// the recipe's own cells and part heights (<see cref="RoomBuildRecipe.LocalExtent"/>), moved to the room's world pose.</summary>
internal static class RoomRecipeStackedVolumes
{
    internal static void Register(LG_Geomorph room, RoomBuildRecipe recipe)
    {
        // A diagnosis and nothing else may go wrong here: the level is never stopped for a missing extent or zone.
        try
        {
            var local = recipe.LocalExtent();
            // PlaceRoot's own start room is placed before it has a zone; the start room is always in reality.
            StackedVolumes.Register(room.m_zone?.DimensionIndex ?? eDimensionIndex.Reality, new(local.Min[0], local.Min[1], local.Min[2]), new(local.Max[0], local.Max[1], local.Max[2]), room);
            // The room's pose of this call and the world box its node volume was sized to (centre and axis-aligned size), so a log
            // shows the registered box and the navigation bounds at the same pose.
            var bounds = room.GetComponent<AIGraph.AIG_GeomorphNodeVolume>()?.m_sizeOverrideTransform;
            // log-values-max: 9
            if (MapLog.Logs(RuntimeLogLevel.Info)) MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.NavigationReadback, "Room registered for the height-aware position lookup.",
                RuntimeLogValue.Of("phase", "stack-register"), RuntimeLogValue.Of("room", room.name), RuntimeLogValue.Of("resource", recipe.ResourceKey),
                RuntimeLogValue.Of("localMin", Format(new(local.Min[0], local.Min[1], local.Min[2]))), RuntimeLogValue.Of("localMax", Format(new(local.Max[0], local.Max[1], local.Max[2]))),
                RuntimeLogValue.Of("roomOrigin", Format(room.transform.position)), RuntimeLogValue.Of("yaw", FormattableString.Invariant($"{room.transform.eulerAngles.y:0.#}")),
                RuntimeLogValue.Of("navCentre", bounds == null ? "unavailable" : Format(bounds.position)), RuntimeLogValue.Of("navSize", bounds == null ? "unavailable" : Format(bounds.localScale)));
        }
        catch (Exception error) { Refuse(room, recipe, error.GetType().Name + ":" + error.Message); }
    }

    private static void Refuse(LG_Geomorph room, RoomBuildRecipe recipe, string reason)
    {
        // log-values-max: 4
        if (MapLog.Logs(RuntimeLogLevel.Error)) MapLog.Record(RuntimeLogLevel.Error, RoomRecipeCodes.NavigationReadback, "Room could not be registered for the height-aware position lookup; its gates use the game's lookup.",
            RuntimeLogValue.Of("phase", "stack-register-skipped"), RuntimeLogValue.Of("room", room.name), RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Quote("reason", reason));
    }
    private static string Format(Vector3 v) => FormattableString.Invariant($"({v.x:0.#},{v.y:0.#},{v.z:0.#})");
}

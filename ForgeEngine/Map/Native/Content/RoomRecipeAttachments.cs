using System.Linq;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>Parts that hang on another part (an effect on a lamp): once native has built both, the child's object becomes a child
/// of the host's object, keeping its authored pose, so it moves and turns with the host. A spawner part's object only exists after
/// native built it (the cloned spawner is destroyed), which is why this runs after the factory, not while the clones are made.</summary>
internal static class RoomRecipeAttachments
{
    /// <summary>False when some hanging part or its host was never built; each such part is logged.</summary>
    internal static bool Attach(LG_Geomorph room, RoomBuildRecipe recipe)
    {
        // A part hung on a panel its room left out stays where it stands, unattached; one left out itself is gone.
        var hanging = recipe.Parts.Where(part => part.Parent != null && !RoomRecipeSpatialReadback.IsRemoved(room, part.Id) && !RoomRecipeSpatialReadback.IsRemoved(room, part.Parent)).ToArray();
        if (hanging.Length == 0) return true;
        var objects = RoomRecipeSpatialReadback.InstancesOf(room, recipe);
        int attached = 0;
        bool valid = true;
        foreach (var part in hanging)
        {
            if (!objects.TryGetValue(part.Id, out var child) || child == null || !objects.TryGetValue(part.Parent!, out var host) || host == null)
            {
                valid = false;
                MapLog.Record(RuntimeLogLevel.Error, RoomRecipeCodes.Parts, "A part that hangs on another part could not be attached: it or its host was never built.",
                    RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Quote("part", part.Id), RuntimeLogValue.Quote("host", part.Parent!));
                continue;
            }
            child.transform.SetParent(host.transform, true);
            attached++;
        }
        MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.Parts, "Parts attached to the parts they hang on.",
            RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("phase", "attach"),
            RuntimeLogValue.Of("hanging", hanging.Length), RuntimeLogValue.Of("attached", attached), RuntimeLogValue.Of("valid", valid));
        return valid;
    }
}

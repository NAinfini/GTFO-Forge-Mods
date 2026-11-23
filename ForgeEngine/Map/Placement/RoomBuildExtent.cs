using System;
using System.Linq;

namespace ForgeMap;

internal sealed partial record RoomBuildRecipe
{
    /// <summary>How far the navigation volume reaches below the lowest floor and above the room's top. The game's own volume of a
    /// geomorph without a size override (AIG_GeomorphNodeVolume.SetSize) is 64 m high and centred on the room pivot, which stands
    /// on the floor: 32 m either side of it.</summary>
    public const float NavigationVerticalMargin = 32;

    /// <summary>The room's own solid extent in room-local metres, from the authored data alone (the same cells and part heights the
    /// website exports): every 4 m cell of every area with half a cell around it in plan, one metre below the lowest floor, and from
    /// the lowest floor up to one metre above the higher of the highest cell floor and the highest part pivot.</summary>
    public (float[] Min, float[] Max) LocalExtent() => Span(1);

    /// <summary>The box the room's native node volume covers, in room-local metres. In plan it is exactly the walkable cells with half
    /// a cell around them (the room's spawn space and the floor enemies path on), as a vanilla geomorph's volume ends at the edge of
    /// its footprint; vertically it runs <see cref="NavigationVerticalMargin"/> below the lowest floor and above the higher of the
    /// highest cell floor and the highest part pivot. Nothing in it depends on a grid cell of the level.</summary>
    public (float[] Min, float[] Max) NavigationExtent() => Span(NavigationVerticalMargin);

    /// <summary>The reader admits a recipe only with at least one area of at least one cell, so the span always exists.</summary>
    private (float[] Min, float[] Max) Span(float vertical)
    {
        var cells = Areas.SelectMany(area => area.Cells).ToArray();
        var top = Math.Max(Height, cells.Max(cell => cell.Y));
        return (new[] { cells.Min(cell => cell.X) - Cell / 2, cells.Min(cell => cell.Y) - vertical, cells.Min(cell => cell.Z) - Cell / 2 },
            new[] { cells.Max(cell => cell.X) + Cell / 2, top + vertical, cells.Max(cell => cell.Z) + Cell / 2 });
    }
}

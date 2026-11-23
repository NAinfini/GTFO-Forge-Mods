using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

/// <summary>A floor sample of an area in room space (4 m apart, as the website samples the floor), with its floor height. It says where the area's floor is; it does not divide anything.</summary>
internal sealed record RoomBuildCell(float X, float Y, float Z);
/// <summary>One LG_Area as the website exported it: its floor samples and its AIGraph source position.</summary>
internal sealed record RoomBuildArea(string Id, IReadOnlyList<RoomBuildCell> Cells, float[] GraphSource);
/// <summary>A door at a free pose (Euler degrees, y of <c>Position</c> is its floor), <c>From</c> behind it and <c>To</c> in front. <c>DoorKind</c> is the
/// author's choice for what stands in the doorway (<c>none</c> for an open passage, <c>weak</c>, <c>weak-locked</c>, <c>weak-melee</c> or <c>weak-hack</c>; a door always
/// starts closed); null leaves it to the level's door balance, as for a vanilla interior gate.</summary>
internal sealed record BlankRoomInteriorDoor(string Id, float[] Position, float[] Rotation, string Size, string From, string To, string? DoorKind = null);
internal sealed partial record RoomBuildRecipe
{
    private const float Cell = 4;
    /// <summary>The most floor samples one area may list.</summary>
    private const int MaximumAreaCells = 1 << 16;
    private static IReadOnlyList<RoomBuildArea> ReadAreas(JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 256) throw new FormatException("blank room areas");
        var areas = new List<RoomBuildArea>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        var owned = new HashSet<(float, float)>();
        foreach (var row in rows.EnumerateArray())
        {
            Fields(row, "id", "cells", "graphSource");
            var id = Text(row, "id");
            if (!Token(id) || id.Length > 64 || !ids.Add(id)) throw new FormatException("blank room area id");
            var list = row.GetProperty("cells");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > MaximumAreaCells) throw new FormatException("blank room area cells");
            var cells = new List<RoomBuildCell>();
            foreach (var element in list.EnumerateArray())
            {
                var v = VectorOf(element, "cells");
                var cell = new RoomBuildCell(v[0], v[1], v[2]);
                if (!owned.Add((cell.X, cell.Z))) throw new FormatException("blank room cell owned by two areas");
                cells.Add(cell);
            }
            var graph = Vector(row, "graphSource");
            if (!cells.Any(c => Math.Abs(c.X - graph[0]) <= Cell / 2 && Math.Abs(c.Z - graph[2]) <= Cell / 2))
                throw new FormatException("blank room graph source outside its area: " + id);
            areas.Add(new(id, cells.AsReadOnly(), graph));
        }
        return areas.AsReadOnly();
    }
    private static IReadOnlyList<BlankRoomInteriorDoor> ReadInteriorDoors(JsonElement rows, HashSet<string> areas, HashSet<string> ids)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 1024) throw new FormatException("blank room interior doors");
        var doors = new List<BlankRoomInteriorDoor>();
        foreach (var row in rows.EnumerateArray())
        {
            OptionalFields(row, new[] { "id", "position", "rotation", "size", "from", "to" }, "kind");
            var d = new BlankRoomInteriorDoor(Text(row, "id"), Vector(row, "position"), Vector(row, "rotation"), Text(row, "size"), Text(row, "from"), Text(row, "to"),
                row.TryGetProperty("kind", out _) ? Text(row, "kind") : null);
            if (!Token(d.Id) || d.Id.Length > 64 || !ids.Add(d.Id)) throw new FormatException("blank room door id");
            if (d.Size is not ("4x4" or "8x4")) throw new FormatException("blank room interior door");
            if (d.DoorKind is not (null or "none" or "weak" or "weak-locked" or "weak-melee" or "weak-hack")) throw new FormatException("blank room interior door kind");
            if (!areas.Contains(d.From) || !areas.Contains(d.To) || d.From == d.To) throw new FormatException("blank room interior door areas");
            doors.Add(d);
        }
        return doors.AsReadOnly();
    }
    /// <summary>`none` is a free passage (<see cref="RoomBuildDoor.Kind"/> `passage`): no door to choose, so no door kind.</summary>
    private static RoomBuildDoor InteriorDoorway(BlankRoomInteriorDoor door) => new("Interior " + door.Id, (float[])door.Position.Clone(), Facing(door.Rotation[1], door.Rotation[0]), door.Rotation[1], door.Size)
    {
        Pitch = door.Rotation[0],
        Roll = door.Rotation[2],
        FromArea = door.From,
        ToArea = door.To,
        Kind = door.DoorKind == "none" ? "passage" : "door",
        DoorKind = door.DoorKind == "none" ? null : door.DoorKind
    };

}

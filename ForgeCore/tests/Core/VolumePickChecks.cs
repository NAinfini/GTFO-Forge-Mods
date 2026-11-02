namespace ForgeCore.Tests;

/// <summary>Which room volume a point belongs to when volumes share a plan position (<see cref="VolumePick.Choose"/>).</summary>
internal static class VolumePickChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // Stacked: a lower and an upper room over the same plan, 12 m apart.
        var stacked = new[]
        {
            new VolumeBox(0, 0, 0, 0, 20, 5, 20),
            new VolumeBox(0, 0, 12, 0, 20, 17, 20),
        };
        check(VolumePick.Choose(stacked, 0, 10, 2, 10) == 0, "pick: a point in the lower room's height is the lower room");
        check(VolumePick.Choose(stacked, 0, 10, 14, 10) == 1, "pick: a point in the upper room's height is the upper room");
        check(VolumePick.Choose(stacked, 0, 10, 8, 10) == 0, "pick: a point between two stacked rooms is the nearer in height (lower)");
        check(VolumePick.Choose(stacked, 0, 10, 10, 10) == 1, "pick: a point between two stacked rooms is the nearer in height (upper)");
        check(VolumePick.Choose(stacked, 0, 10, 2, 30) == -1, "pick: a point off both plans leaves the game's answer");
        check(VolumePick.Choose(stacked, 1, 10, 2, 10) == -1, "pick: another dimension leaves the game's answer");

        // Reaching into the neighbour's cell: the hall's box covers x 0..90, the room's x 64..80; the game answers the room for x > 64.
        var reach = new[]
        {
            new VolumeBox(0, 64, 0, 0, 80, 5, 20),
            new VolumeBox(0, 0, 20, 0, 90, 25, 20),
        };
        check(VolumePick.Choose(reach, 0, 70, 22, 10) == 1, "pick: a point of the hall inside the neighbour's cell is the hall");
        check(VolumePick.Choose(reach, 0, 70, 2, 10) == 0, "pick: a point of the neighbour room is the neighbour room");
        check(VolumePick.Choose(reach, 0, 85, 12, 10) == -1, "pick: one box over the plan and a height outside it leaves the game's answer");

        // The hall (two storeys, 90 x 24 x 60) holds small rooms of 16 x 6 x 16 m; stacked pairs, and neighbours that only touch.
        var hall = new VolumeBox(0, 0, 0, 0, 90, 24, 60);
        var lower = new VolumeBox(0, 20, 0, 20, 36, 6, 36);
        var upper = new VolumeBox(0, 20, 12, 20, 36, 18, 36);
        var next = new VolumeBox(0, 36, 0, 20, 52, 6, 36);
        var rooms = new[] { hall, lower, upper, next };
        check(VolumePick.Choose(rooms, 0, 28, 3, 28) == 1, "pick: a point in a small room inside the hall's box is the small room, not the hall");
        check(VolumePick.Choose(rooms, 0, 28, 15, 28) == 2, "pick: a point in the room stacked above is the upper room");
        check(VolumePick.Choose(rooms, 0, 28, 9, 28) == 0, "pick: a point between the stacked rooms but inside the hall is the hall");
        check(VolumePick.Choose(rooms, 0, 44, 3, 28) == 3, "pick: a point in the neighbouring small room is that room");

        // A small room turned 30 degrees off the grid, its doorway at local z 16, beside a hall whose box covers the whole area.
        var turnedHall = new VolumeBox(0, 60, 0, 100, 140, 10, 170);
        var turned = new VolumeBox(0, 100, 100, 0.5235988f, -8, 0, 0, 8, 5, 16);
        var turnedRooms = new[] { turnedHall, turned };
        check(VolumePick.Choose(turnedRooms, 0, 104f, 2, 106.93f) == 1, "pick: a point inside a turned room's footprint is the turned room");
        check(VolumePick.Choose(turnedRooms, 0, 108.75f, 2, 115.16f) == 0, "pick: a point 1.5 m past a turned room's doorway is the hall, though inside the room's axis-aligned bounds");
        check(VolumePick.Choose(rooms, 0, 10, 3, 10) == 0, "pick: a point in the hall away from every small room is the hall");
        check(VolumePick.Choose(rooms, 0, 100, 3, 10) == -1, "pick: a point outside every box leaves the game's answer");

        // No registration, and a single box that does not contain the point.
        check(VolumePick.Choose(ReadOnlySpan<VolumeBox>.Empty, 0, 1, 1, 1) == -1, "pick: nothing registered leaves the game's answer");
        var single = new[] { new VolumeBox(0, 0, 0, 0, 10, 5, 10) };
        check(VolumePick.Choose(single, 0, 5, 3, 5) == 0, "pick: a point inside the only box is that box");
        check(VolumePick.Choose(single, 0, 5, 30, 5) == -1, "pick: a point above the only box leaves the game's answer");

        // A room registered before the game has made its volume takes no part until the volume exists.
        var set = new VolumeBoxes();
        set.Add(new VolumeBox(0, 0, 0, 0, 10, 5, 10));
        var exists = false;
        check(set.Choose(0, (_, _) => exists, 0, 5, 2, 5) == -1, "pick: a registered room without a volume is skipped");
        exists = true;
        check(set.Choose(0, (_, _) => exists, 0, 5, 2, 5) == 0, "pick: the same room is chosen once its volume exists");
        exists = false;
        check(set.Choose(0, (_, _) => exists, 0, 5, 2, 5) == 0, "pick: a room stays chosen after it became ready");    }
}

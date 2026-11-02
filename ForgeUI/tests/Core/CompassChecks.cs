using ForgeUI;

/// <summary>The game's compass points: a glyph of its own per kind, and bearings and distances from the eye.</summary>
internal static class CompassChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var kept = true;
        for (var a = 0; a < CompassPoints.Kinds; a++) kept &= CompassPoints.Shown((CompassPointKind)a, true);
        check(kept && !CompassPoints.Shown(CompassPointKind.Teammate, false) && CompassPoints.Shown(CompassPointKind.Here, false)
            && CompassPoints.Shown(CompassPointKind.Locator, false) && CompassPoints.Shown(CompassPointKind.TerminalPing, false),
            "compass: without teammates the teammate arrow is dropped and the beacons stay");

        var distinct = true;
        for (var a = 0; a < CompassPoints.Kinds; a++)
        {
            var glyph = CompassPoints.Glyph((CompassPointKind)a);
            distinct &= glyph.Length >= 4 && glyph.Length % 4 == 0;
            foreach (var v in glyph) distinct &= v >= -1f && v <= 1f;
            for (var b = a + 1; b < CompassPoints.Kinds; b++) distinct &= !glyph.SequenceEqual(CompassPoints.Glyph((CompassPointKind)b));
        }
        check(distinct, "compass: every kind has its own glyph of whole segments inside its box");

        var reading = new CompassReading(3);
        reading.Begin();
        check(reading.Add(10f, 2f, 0f, CompassPointKind.Enemy, 1, true, 0.2f, 0.4f, 0.6f), "compass: a point is added");
        check(reading.Add(0f, 2f, -5f, CompassPointKind.Locator, -1, false, 0.1f, 0.1f, 0.1f), "compass: a beacon is added");
        check(!reading.Add(float.NaN, 0f, 0f, CompassPointKind.Here, -1, false, 0f, 0f, 0f), "compass: a position that is not a number is refused");
        check(reading.Add(0f, 2f, 0f, CompassPointKind.Teammate, 2, true, 1f, 0f, 0f) && !reading.Add(1f, 1f, 1f, CompassPointKind.Here, -1, false, 0f, 0f, 0f),
            "compass: a full reading refuses more");
        // Eye 2 m up at the origin looking north (+z).
        reading.Locate(0f, 2f, 0f, 0f, 1f);
        var right = reading.Get(0);
        check(MathF.Abs(right.Azimuth - 90f) < 1e-3f && MathF.Abs(right.Distance - 10f) < 1e-3f, "compass: 10 m east of a north view is at 90, 10 m");
        check(right.Kind == CompassPointKind.Enemy && right.Slot == 1 && right.PlayerColour && right.B == 0.6f, "compass: kind, slot and colour kept");
        var behind = reading.Get(1);
        check(MathF.Abs(MathF.Abs(behind.Azimuth) - 180f) < 1e-3f && MathF.Abs(behind.Distance - 5f) < 1e-3f, "compass: 5 m south is behind, 5 m");
        check(!behind.PlayerColour && behind.R == 1f && behind.G == 1f && behind.B == 1f && behind.Slot == -1, "compass: a beacon is white and has no slot");
        check(float.IsNaN(reading.Get(2).Azimuth) && reading.Get(2).Distance == 0f, "compass: a point at the eye has no bearing");
        // The same points seen facing east: the east point is ahead, the south one to the right; height counts in the distance.
        reading.Locate(0f, 8f, 0f, 1f, 0f);
        check(MathF.Abs(reading.Get(0).Azimuth) < 1e-3f && MathF.Abs(reading.Get(1).Azimuth - 90f) < 1e-3f, "compass: bearings follow the view");
        check(MathF.Abs(reading.Get(0).Distance - MathF.Sqrt(136f)) < 1e-3f, "compass: the distance is straight from the eye, height included");
        reading.Begin();
        check(reading.Count == 0, "compass: a new reading starts empty");
        reading.Add(5f, 0f, 0f, CompassPointKind.Door, -1, false, 0.3f, 0.5f, 0.7f, mod: true);
        reading.Locate(0f, 0f, 0f, 0f, 1f);
        var placed = reading.Get(0);
        check(placed.Mod && !placed.PlayerColour && placed.R == 0.3f && placed.B == 0.7f && placed.Kind == CompassPointKind.Door,
            "compass: a mod's point keeps its kind and colour");
        reading.Begin();
        var threw = false;
        try { reading.Get(0); } catch (ArgumentOutOfRangeException) { threw = true; }
        check(threw, "compass: reading past the points is refused");
    }
}

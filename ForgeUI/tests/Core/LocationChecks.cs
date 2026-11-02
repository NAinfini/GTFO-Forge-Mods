using ForgeUI;

/// <summary>The location readouts: zone, heading and altitude texts from real values, hidden when the game gives none, and made
/// again only when the shown value changes.</summary>
internal static class LocationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var m = new LocationReadout();
        check(m.Zone == "" && m.Heading == "" && m.Altitude == "", "location readout: nothing shown before a reading");

        m.SetZone(49);
        m.SetHeading(0f, 1f);
        m.SetAltitude(-211.6f);
        check(m.Zone == "ZONE 49" && m.Heading == "000° N" && m.Altitude == "-212 M", "location readout: ZONE 49, north, world Y rounded to whole metres");

        check(LocationReadout.HeadingDegrees(1f, 0f) == 90 && LocationReadout.HeadingDegrees(0f, -1f) == 180 && LocationReadout.HeadingDegrees(-1f, 0f) == 270
              && LocationReadout.Cardinal(44) == 'N' && LocationReadout.Cardinal(45) == 'E' && LocationReadout.Cardinal(315) == 'N' && LocationReadout.Cardinal(225) == 'W',
            "location readout: heading runs clockwise from north with the nearest cardinal letter");

        var v = m.Version;
        var zone = m.Zone;
        m.SetZone(49);
        m.SetHeading(0.0001f, 1f);
        m.SetAltitude(-212.2f);
        check(m.Version == v, "location readout: an unchanged shown value changes nothing");

        m.SetHeading(0f, 0f);
        check(m.Heading == "000° N", "location readout: looking straight up or down keeps the last heading");

        m.SetZone(50);
        m.SetZone(49);
        check(ReferenceEquals(zone, m.Zone), "location readout: a text is made once per value and reused");

        m.SetZone(null);
        m.SetAltitude(float.NaN);
        m.SetHeading(float.NaN, float.NaN);
        check(m.Zone == "" && m.Altitude == "" && m.Heading == "", "location readout: a value the game does not give hides its line (nothing made up)");

        m.SetZone(3);
        m.Clear();
        check(m.Zone == "" && m.Heading == "" && m.Altitude == "", "location readout: clearing hides every line");
    }
}

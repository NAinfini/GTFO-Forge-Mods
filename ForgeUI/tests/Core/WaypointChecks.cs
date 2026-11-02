using ForgeUI;

/// <summary>World points a mod places: the fixed pool (oldest dropped past the cap, stale ids find nothing) and the projection onto the HUD
/// (on the point, on the edge with an arrow, behind, faded with distance).</summary>
internal static class WaypointChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Book(check);
        Place(check);
    }

    private static void Book(Action<bool, string> check)
    {
        var book = new WaypointBook(2);
        var a = book.Add(out var dropped);
        var b = book.Add(out _);
        check(dropped.IsNone && book.Count == 2 && book.Contains(a) && book.Contains(b), "waypoints: points up to the cap");
        var c = book.Add(out dropped);
        check(dropped.Equals(a) && !book.Contains(a) && book.Contains(b) && book.Contains(c) && book.Count == 2,
            "waypoints: past the cap the oldest is dropped");
        check(c.Slot == a.Slot && book.SlotOf(a) < 0, "waypoints: an id of a dropped point never reaches the point that took its slot");
        check(book.Remove(b) && !book.Remove(b) && book.Count == 1 && !book.Used(b.Slot), "waypoints: a point is removed once");
        var d = book.Add(out dropped);
        check(dropped.IsNone && d.Slot == b.Slot && book.Count == 2, "waypoints: a freed slot is reused before anything is dropped");
        // c is now the oldest.
        book.Add(out dropped);
        check(dropped.Equals(c), "waypoints: the oldest is by when it was added, not by slot");
        book.Clear();
        check(book.Count == 0 && !book.Contains(d) && !book.Used(0) && !book.Used(1), "waypoints: clear frees every point");
        check(!book.Contains(WaypointId.None) && WaypointId.None.IsNone && !book.Used(-1) && !book.Used(9), "waypoints: no point and out-of-range slots find nothing");
        check(new WaypointBook(0).Capacity == 1, "waypoints: a pool holds at least one point");
    }

    private static void Place(Action<bool, string> check)
    {
        // A camera at the origin looking down -z, 90° vertical field, square, near 1, far 100.
        float n = 1f, f = 100f;
        var clip = ClipTransform.Combine(new ClipTransform(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
            new ClipTransform(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -(f + n) / (f - n), -2f * f * n / (f - n), 0, 0, -1, 0));
        const float hw = 960f, hh = 540f, inset = 40f;

        var at = WaypointMath.Place(clip, 0f, 0f, -10f, hw, hh, inset, 10f, 0f, 0f, true, out var x, out var y, out _, out var alpha);
        check(at == WaypointPlace.OnScreen && Near(x, 0f) && Near(y, 0f) && alpha == 1f, "waypoints: straight ahead is on the middle, unfaded");
        at = WaypointMath.Place(clip, 2f, 1f, -10f, hw, hh, inset, 10f, 0f, 0f, true, out x, out y, out _, out _);
        check(at == WaypointPlace.OnScreen && Near(x, 0.2f * hw) && Near(y, 0.1f * hh), "waypoints: a point ahead lands where the camera shows it");

        at = WaypointMath.Place(clip, 30f, 0f, -10f, hw, hh, inset, 32f, 0f, 0f, true, out x, out y, out var angle, out _);
        check(at == WaypointPlace.Edge && Near(x, hw - inset) && Near(y, 0f) && Near(angle, 0f), "waypoints: off to the right is an arrow on the right edge pointing right");
        at = WaypointMath.Place(clip, 30f, 0f, -10f, hw, hh, inset, 32f, 0f, 0f, false, out _, out _, out _, out _);
        check(at == WaypointPlace.Hidden, "waypoints: without an edge arrow a point off screen is not drawn");

        at = WaypointMath.Place(clip, 10f, 0f, 10f, hw, hh, inset, 14f, 0f, 0f, true, out x, out _, out angle, out _);
        check(at == WaypointPlace.Edge && Near(x, hw - inset) && Near(angle, 0f), "waypoints: behind and to the right points right, not left");
        at = WaypointMath.Place(clip, 0f, 0f, 10f, hw, hh, inset, 10f, 0f, 0f, true, out _, out y, out angle, out _);
        check(at == WaypointPlace.Edge && Near(y, -(hh - inset)) && Near(angle, -90f), "waypoints: straight behind is an arrow at the bottom pointing down");
        check(WaypointMath.Viewport(clip, 0f, 0f, 10f, out _, out _, out var behind) && behind, "waypoints: a point behind is flagged behind");
        check(WaypointMath.Place(clip, 0f, 0f, 0f, hw, hh, inset, 0f, 0f, 0f, true, out _, out _, out _, out _) == WaypointPlace.Hidden,
            "waypoints: a point on the camera's plane is not drawn");

        WaypointMath.Place(clip, 0f, 0f, -30f, hw, hh, inset, 30f, 20f, 40f, true, out _, out _, out _, out alpha);
        check(Near(alpha, 0.5f), "waypoints: halfway through the fade is half faded");
        check(WaypointMath.Place(clip, 0f, 0f, -50f, hw, hh, inset, 50f, 20f, 40f, true, out _, out _, out _, out _) == WaypointPlace.Hidden,
            "waypoints: past the fade's end a point is not drawn");
        WaypointMath.Place(clip, 0f, 0f, -50f, hw, hh, inset, 50f, 0f, 0f, true, out _, out _, out _, out alpha);
        check(alpha == 1f, "waypoints: no fade end turns fading off");
        check(WaypointMath.Place(clip, 0f, 0f, -10f, 0f, hh, inset, 10f, 0f, 0f, true, out _, out _, out _, out _) == WaypointPlace.Hidden,
            "waypoints: no screen size draws nothing");
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-2f;
}

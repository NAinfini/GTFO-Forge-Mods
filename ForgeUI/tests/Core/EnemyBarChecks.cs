using System;
using ForgeUI;

/// <summary>The enemy health bars' state: projection, the bar limit and who gives way, fading and the line-of-sight round robin.</summary>
internal static class EnemyBarChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Projection(check);
        Limit(check);
        Life(check);
        Sight(check);
        check(new EnemyHealthBarStyle().Problem() == null && new EnemyHealthBarStyle { MaxBars = 0 }.Problem() != null &&
              new EnemyHealthBarStyle { MaxBars = EnemyBarBook.Capacity + 1 }.Problem() != null && new EnemyHealthBarStyle { Linger = 0.1f }.Problem() != null,
            "bar style: the limit and linger are bounded");
        check(NumberText.Damage(4.46f) == "4.5" && NumberText.Damage(12.6f) == "13" && NumberText.Damage(9.96f) == "10", "damage text: tenths below 10, whole from 10");
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;

    private static void Projection(Action<bool, string> check)
    {
        // A camera at the origin looking down -z (Unity's view space), 90° vertical field, square, near 1, far 100.
        var view = new ClipTransform(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        float n = 1f, f = 100f;
        var projection = new ClipTransform(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -(f + n) / (f - n), -2f * f * n / (f - n), 0, 0, -1, 0);
        var clip = ClipTransform.Combine(view, projection);
        check(clip.ToViewport(0f, 0f, -10f, out var cx, out var cy) && Near(cx, 0.5f) && Near(cy, 0.5f), "projection: straight ahead is the centre");
        check(clip.ToViewport(10f, 10f, -10f, out var ex, out var ey) && Near(ex, 1f) && Near(ey, 1f), "projection: 45° up and right is the corner");
        check(!clip.ToViewport(0f, 0f, 10f, out _, out _), "projection: behind the camera is refused");
        // A view that moves the world 5 along +x (the camera stands at x = -5).
        var moved = ClipTransform.Combine(new ClipTransform(1, 0, 0, 5, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1), projection);
        check(moved.ToViewport(-5f, 0f, -10f, out var mx, out _) && Near(mx, 0.5f), "projection: the view's translation is applied");
    }

    private static void Limit(Action<bool, string> check)
    {
        var book = new EnemyBarBook();
        long evicted;
        for (var i = 1; i <= 3; i++) book.Hit(i, i, 100f, 90f, 100f, 3, out evicted);
        check(book.Find(1) >= 0 && book.Find(3) >= 0, "limit: bars up to the limit");
        book.Hit(1, 4f, 90f, 80f, 100f, 3, out evicted);
        book.Hit(4, 5f, 100f, 50f, 100f, 3, out evicted);
        check(evicted == 2 && book.Find(2) < 0 && book.Find(4) >= 0 && book.Find(1) >= 0, "limit: a new enemy takes the place of the bar hit longest ago");
        var slot = book.Find(4);
        check(Near(book.Bars[slot].Fraction, 0.5f) && Near(book.Bars[slot].Trail.Trail, 1f) && EnemyBarBook.Flashing(book.Bars[slot], 5.05f),
            "a hit: the fill drops at once, the lost piece trails and flashes");
    }

    private static void Life(Action<bool, string> check)
    {
        var book = new EnemyBarBook();
        book.Hit(1, 0f, 100f, 60f, 100f, 6, out _);
        book.Tick(2f, 0.016f, 3f);
        check(book.Find(1) >= 0 && EnemyBarBook.Alpha(book.Bars[book.Find(1)], 2f, 3f) == 1f, "life: a bar stays fully while it lingers");
        check(EnemyBarBook.Alpha(book.Bars[book.Find(1)], 3.2f, 3f) < 1f, "life: then fades");
        book.Tick(3f + EnemyBarBook.FadeSeconds + 0.01f, 0.016f, 3f);
        check(book.Find(1) < 0, "life: and goes");
        book.Hit(3, 20f, 100f, 90f, 100f, 6, out _);
        book.Remove(3);
        check(book.Find(3) < 0, "death: the bar goes at once");
        book.Hit(4, 30f, 100f, 90f, 100f, 6, out _);
        book.Sync(book.Find(4), 0.4f);
        check(Near(book.Bars[book.Find(4)].Fraction, 0.4f), "a teammate's damage: the synced health shows");
        book.Clear();
        check(book.Find(4) < 0, "level change: everything clears");
    }

    private static void Sight(Action<bool, string> check)
    {
        var book = new EnemyBarBook();
        for (var i = 1; i <= 5; i++) book.Hit(i, 0f, 100f, 90f, 100f, 6, out _);
        var due = new int[3];
        var cursor = 0;
        var first = book.DueSight(1f, due, 3, ref cursor, 0.1f);
        var second = book.DueSight(1f, due, 3, ref cursor, 0.1f);
        var third = book.DueSight(1f, due, 3, ref cursor, 0.1f);
        check(first == 3 && second == 2 && third == 0, "sight: a few checks a frame, each bar once per interval");
        check(book.DueSight(1.11f, due, 3, ref cursor, 0.1f) == 3, "sight: due again after the interval");
    }
}

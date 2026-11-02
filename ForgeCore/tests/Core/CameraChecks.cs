namespace ForgeCore.Tests;

internal static class CameraChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var book = new CameraLeaseBook();
        check(book.Lift == 0f && float.IsNaN(book.NearClip) && book.Owner == null, "camera: with no lease the game's own view stands");
        var first = book.Take("mod.a");
        first.Lift = 0.4f; first.NearClip = 0.02f;
        check(book.Owns(first) && book.Lift == 0.4f && book.NearClip == 0.02f, "camera: a lease's lift and near clip show");
        var second = book.Take("mod.b");
        second.Lift = 1f;
        check(book.Owns(second) && !book.Owns(first) && book.Lift == 1f && float.IsNaN(book.NearClip), "camera: only the newest lease owns the view");
        book.Release(second);
        check(book.Owns(first) && book.Lift == 0.4f && book.NearClip == 0.02f, "camera: releasing the newer lease brings the older lease's view back");
        check(book.ReleaseAll() == 1 && book.Lift == 0f && !book.Holds(first) && book.Count == 0, "camera: releasing every lease restores the game's view");
        var threw = false;
        try { book.Take(" "); } catch (ArgumentException) { threw = true; }
        check(threw, "camera: a lease names its mod");
    }
}

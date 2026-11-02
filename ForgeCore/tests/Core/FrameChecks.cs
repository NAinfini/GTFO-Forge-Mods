namespace ForgeCore.Tests;

/// <summary>The per-frame callback book behind <c>Frame</c>, and the log formatting helpers.</summary>
internal static class FrameChecks
{
    internal static void Run(Action<bool, string> check, List<string> errors)
    {
        errors.Clear();
        var book = new FrameBook("update");
        var order = new List<string>();
        book.Add("mod.a", () => order.Add("a"));
        book.Add("mod.b", () => throw new InvalidOperationException("boom"));
        book.Add("mod.c", () => order.Add("c"));
        book.Run();
        check(order.SequenceEqual(new[] { "a", "c" }), "frame: a callback that throws does not stop the callbacks after it");
        check(errors.Count == 1 && errors[0].Contains("mod.b") && errors[0].Contains("update") && errors[0].Contains("boom"), "frame: the failure is reported under the mod that registered it");
        for (var i = 0; i < 8; i++) book.Run();
        check(errors.Count == 1 && order.Count == 18, "frame: a repeating failure is reported once and the others keep running every frame");
        book.Run();
        check(errors.Count == 1, "frame: the same failure is never written twice");
        var added = 0;
        book.Add("mod.d", () => { added++; book.Add("mod.e", () => added += 100); });
        book.Run();
        check(added == 1, "frame: a callback registered while the frame runs waits for the next frame");
        try { book.Add(" ", () => { }); check(false, "frame: a mod ID is required"); }
        catch (ArgumentException) { check(true, "frame: a mod ID is required"); }
        try { book.Add("mod.f", null!); check(false, "frame: a callback is required"); }
        catch (ArgumentNullException) { check(true, "frame: a callback is required"); }

        check(Log.Num(1.236) == "1.24", "log: a number keeps at most two decimals");
        check(Log.Num(2.5) == "2.5" && Log.Num(3) == "3" && Log.Num(-0.126) == "-0.13", "log: no trailing zeros, a dot whatever the culture");
        check(Log.Pos(1f, -2.5f, 3.456f) == "(1.00, -2.50, 3.46)", "log: a position has two decimals on each axis");
        errors.Clear();
    }
}

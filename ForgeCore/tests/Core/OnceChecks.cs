namespace ForgeCore.Tests;

internal static class OnceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var written = new List<string>();
        var once = new OnceReporter(written.Add);
        for (var i = 0; i < 100; i++) once.Report("a", "broken");
        check(written.SequenceEqual(new[] { "broken", "broken (10 times)", "broken (100 times)" }) && once.Count("a") == 100,
            "once: a key is written the 1st, 10th and 100th time, with the count, and no other time");
        written.Clear();
        for (var i = 0; i < 12; i++) once.Report("b", "other");
        check(written.Count == 2 && once.Count("b") == 12, "once: keys count independently");
        once.Reset();
        written.Clear();
        once.Report("a", "again");
        check(written.SequenceEqual(new[] { "again" }) && once.Count("a") == 1 && once.Count("b") == 0, "once: Reset starts every key at 1 again");
        check(once.Report("c", "x") && !once.Report("c", "x"), "once: Report says whether it wrote");
        var gate = new OnceReporter();
        var gated = 0;
        for (var i = 0; i < 100; i++) if (gate.ShouldReport("g")) gated++;
        check(gated == 3 && gate.Count("g") == 100, "once: a gate without an output target admits the 1st, 10th and 100th occurrence");
    }
}

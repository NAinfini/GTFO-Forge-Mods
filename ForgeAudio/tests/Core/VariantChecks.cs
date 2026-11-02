namespace ForgeAudio.Tests;

internal static class VariantChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var random = new Random(12345);
        foreach (var count in new[] { 2, 3, 5, 16 })
        {
            var last = -1; var previous = -1; var repeats = 0; var seen = new bool[count]; var outOfRange = false;
            for (var i = 0; i < 5000; i++)
            {
                var pick = VariantPick.Next(count, ref last, random);
                if (pick < 0 || pick >= count) outOfRange = true; else seen[pick] = true;
                if (pick == previous) repeats++;
                previous = pick;
            }
            check(!outOfRange && repeats == 0, "a random play never repeats the previous variant (" + count + " variants)");
            check(Array.TrueForAll(seen, s => s), "every variant is reachable (" + count + " variants)");
        }
        var single = -1; var always0 = true;
        for (var i = 0; i < 100; i++) always0 &= VariantPick.Next(1, ref single, random) == 0;
        check(always0, "a cue with one variant always picks variant 0");
    }
}

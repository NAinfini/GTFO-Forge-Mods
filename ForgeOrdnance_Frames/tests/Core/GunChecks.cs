using ForgeOrdnanceFrames;

/// <summary>The planted gun's exact fire rate at any frame rate, and the shipped suits' toughness against the game's own numbers.</summary>
internal static class GunChecks
{
    /// <summary>A player's full health (the game's PlayerDataBlock <c>health</c>).</summary>
    private const float PlayerHealth = 25f;

    internal static void Run(Action<bool, string> check, IReadOnlyDictionary<string, FrameSpec> models)
    {
        // The gun's own delay 0.08 s at twice the rate: 0.04 s, shorter than a frame at 60 fps and not a whole number of frames at 144.
        const float interval = 0.08f / 2f;
        foreach (var fps in new[] { 30f, 60f, 144f, 240f })
        {
            var shots = FireClock.Simulate(1f / fps, interval, 10f, out var most);
            var expected = 10f / interval + 1f;
            check(Math.Abs(shots - expected) <= 1f, "planted fire reaches the target rate at " + fps + " fps (" + shots + " shots in 10 s, expected " + expected + ")");
            check(most <= FireClock.MaxPerFrame, "planted fire fires at most " + FireClock.MaxPerFrame + " shots in one frame at " + fps + " fps");
        }
        // Without the clock the game rounds the 0.059 s delay up to four frames at 60 fps: the quantisation this fixes.
        check(FireClock.Simulate(1f / 60f, 0.08f / 1.35f, 10f, out _) >= 168, "a delay between whole frames is not rounded up to the next frame");
        var slow = FireClock.Simulate(1f / 10f, 0.01f, 1f, out var capped);
        check(capped == FireClock.MaxPerFrame && slow <= 11 * FireClock.MaxPerFrame, "a long frame fires no more than the cap and drops the rest");

        var clock = new FireClock();
        clock.AfterShot(0f, 0.05f, out var next);
        check(Math.Abs(next - 0.05f) < 1e-5f, "the next shot is one interval after the first");
        var late = clock.AfterShot(2f, 0.05f, out next);
        check(late == 0 && Math.Abs(next - 2.05f) < 1e-5f, "a shot after a pause starts a new stream with no backlog");
        var due = clock.AfterShot(2.051f + 0.06f, 0.05f, out next);
        check(due == 1 && Math.Abs(next - 2.15f) < 1e-4f, "a frame that is past the next ideal shot fires it at once and keeps the ideal times");

        // Toughness: armour over (1 - damage reduction) is 3 to 6 player healths, the tank most and the recon suit least, in the same
        // order as before; a barrier holds 1 to 1.5 player healths and refills from empty in about ten seconds after a 5 to 7 s pause.
        var order = new[] { "frame-m18", "frame-m15", "frame-m17", "frame-m16" };
        var last = 0f;
        foreach (var model in order)
        {
            var s = models[model];
            var ehp = s.Armor / (1f - s.DamageReduction);
            check(ehp >= 3f * PlayerHealth - 0.5f && ehp <= 6f * PlayerHealth + 0.5f, model + " effective health " + ehp + " is 3 to 6 player healths");
            check(ehp > last, model + " is tougher than the suit before it");
            last = ehp;
            if (s.Shield == null) continue;
            check(s.Shield.Capacity >= PlayerHealth && s.Shield.Capacity <= 1.5f * PlayerHealth, model + " barrier holds 1 to 1.5 player healths");
            var share = s.Shield.RegenRate / s.Shield.Capacity;
            check(share >= 0.08f && share <= 0.125f && s.Shield.RegenDelay >= 5f && s.Shield.RegenDelay <= 7f, model + " barrier refills in about ten seconds after a 5 to 7 s pause");
        }
        var siege = models["frame-m17"].Siege!;
        check(siege.FireRateMultiplier >= 2f && siege.ReloadMultiplier <= 0.5f && siege.DamageMultiplier * siege.FireRateMultiplier >= 2.3f && siege.DamageReduction <= 0.1f,
            "siege doubles the fire rate, halves the reload, at least 2.3 times the output, and adds little armour on top of its barrier");
    }
}

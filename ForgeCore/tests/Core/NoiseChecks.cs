namespace ForgeCore.Tests;

/// <summary>The player noise multipliers behind <c>PlayerNoise.Scale</c> (the pure <c>NoiseScaleBook</c>): a registered player's movement noise
/// is scaled and nothing else is, an unregistered player and a noise that is not movement (a shot) keep the game's distance, leases end
/// cleanly and never compound, and bad input is refused.</summary>
internal static class NoiseChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var book = new NoiseScaleBook(64);
        check(book.Count == 0 && book.Factor(5, true) == 1f && book.Factor(5, false) == 1f, "noise: with no lease every noise keeps the game's distance");

        book.Add(5, 2f, out var lease);
        check(book.Count == 1 && book.Has(5, lease) && book.Factor(5, true) == 2f, "noise: a leased player's movement noise is doubled");
        check(book.Factor(5, false) == 1f, "noise: a shot (not movement noise) is not scaled");
        check(book.Factor(6, true) == 1f && book.Factor(-1, true) == 1f && book.Factor(64, true) == 1f, "noise: another player, or no player, is not scaled");

        book.Add(5, 3f, out var larger);
        book.Add(5, 1.5f, out var smaller);
        check(book.Factor(5, true) == 3f, "noise: several leases on one player never compound, the largest wins");
        check(book.Remove(5, larger) && book.Factor(5, true) == 2f, "noise: ending the largest falls back to the next largest");
        check(!book.Remove(5, larger), "noise: ending a lease twice does nothing");
        book.Remove(5, lease); book.Remove(5, smaller);
        check(book.Count == 0 && book.Factor(5, true) == 1f && !book.Has(5, lease), "noise: the last lease ending restores the game's distance");

        book.Add(1, 2f, out _); book.Add(2, 4f, out _);
        check(book.Count == 2 && book.Factor(1, true) == 2f && book.Factor(2, true) == 4f, "noise: players are scaled independently");
        book.Clear();
        check(book.Count == 0 && book.Factor(1, true) == 1f && book.Factor(2, true) == 1f, "noise: leaving the level clears every lease");

        // Il2CppInterop's trampoline throws before any patch runs for a by-ref primitive parameter (the 1240-line owner log): never patch one.
        check(TrampolineSafety.Why(typeof(Probe).GetMethod(nameof(Probe.OutFloat))!) is { } why && why.Contains("by-ref Single")
            && TrampolineSafety.Why(typeof(Probe).GetMethod(nameof(Probe.RefInt))!) != null
            && TrampolineSafety.Why(typeof(Probe).GetMethod(nameof(Probe.Plain))!) == null
            && TrampolineSafety.Why(typeof(Probe).GetMethod(nameof(Probe.RefObject))!) == null,
            "patch safety: a method with an out/ref primitive parameter is refused, plain and by-ref object parameters are not");
        check(Array.TrueForAll(Modules.Patched, m => Array.TrueForAll(m.Targets, t => t.Method != "DetectOnNoiseDistance_Conditional_AnimatedWindow")),
            "patch safety: the noise check with the out float parameter is not a patch target");
        check(Throws(() => book.Add(1, 0f, out _)) && Throws(() => book.Add(1, -2f, out _)) && Throws(() => book.Add(1, float.NaN, out _))
            && Throws(() => book.Add(1, float.PositiveInfinity, out _)) && Throws(() => book.Add(64, 2f, out _)) && book.Count == 0,
            "noise: a multiplier that is not above 0, or an unknown player, is refused");
    }

    internal static class Probe
    {
        public static void OutFloat(object target, float a, out float output) { output = a; }
        public static void RefInt(ref int value) { }
        public static void Plain(object target, float a, float b) { }
        public static void RefObject(ref object target) { }
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentOutOfRangeException) { return true; }
    }
}

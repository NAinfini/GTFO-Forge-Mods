namespace ForgeEngine.Trigger.Pure;

/// <summary>Explicit one-sample seed inputs; no global stream or frame-time randomness. The draw is a function of
/// what the plan wrote, which is what lets the host evaluate it once per activation and sync the result with the
/// execution that produced it.</summary>
internal static class SeededNodes
{
    public static double Uniform(double minimum, double maximum, long seed)
    {
        PureNumbers.Bounds(minimum, maximum);
        var stream = new SeededStream(seed);
        // Seed and endpoints are validated even when the interval is degenerate.
        if (minimum == maximum) return PureNumbers.Result(minimum);
        var weight = stream.NextUnit();
        return PureNumbers.Result(minimum * (1d - weight) + maximum * weight);
    }
}

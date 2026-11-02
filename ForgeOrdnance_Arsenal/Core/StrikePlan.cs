using System;

namespace ForgeOrdnanceArsenal;

/// <summary>Which model a falling shell shows.</summary>
public enum ShellLook : byte { Shell = 1, Bomblet = 2, Canister = 3, Emitter = 4 }

/// <summary>One shell of a strike in the arrival frame: <see cref="Right"/> and <see cref="Forward"/> are metres from the
/// beacon along the arrival's right and forward (thrower toward beacon) axes, <see cref="Time"/> seconds after the arrival.</summary>
public readonly struct PlannedShell
{
    public PlannedShell(float right, float forward, float time) { Right = right; Forward = forward; Time = time; }
    public readonly float Right, Forward, Time;
}

/// <summary>Where and when the shells of the fixed-pattern strikes fall. Built on the host only; every peer learns each shell
/// from the host's per-shell message, so the pattern is not part of the protocol. Deterministic for a seed, so it is testable.</summary>
public static class StrikePlan
{
    public static ShellLook Look(StrikeKind kind) => kind switch
    {
        StrikeKind.Cluster => ShellLook.Bomblet,
        StrikeKind.Gas => ShellLook.Canister,
        StrikeKind.Stasis => ShellLook.Emitter,
        _ => ShellLook.Shell,
    };

    /// <summary>The shells of <paramref name="spec"/>. Beam and Hunter have none: they target live enemies at run time.</summary>
    public static PlannedShell[] Build(StrikeSpec spec, uint seed)
    {
        var random = new Lcg(seed);
        switch (spec.Kind)
        {
            case StrikeKind.Precision:
            case StrikeKind.Incendiary:
            case StrikeKind.Gas:
            case StrikeKind.Stasis:
                return new[] { new PlannedShell(0f, 0f, 0f) };
            case StrikeKind.Area:
            {
                var shells = new PlannedShell[spec.Shells];
                for (var i = 0; i < shells.Length; i++)
                {
                    // Uniform over the disc; times spread evenly with a little jitter, the first at the arrival.
                    var angle = random.Next() * MathF.PI * 2f;
                    var distance = spec.Spread * MathF.Sqrt(random.Next());
                    var slot = shells.Length == 1 ? 0f : spec.Duration * i / (shells.Length - 1);
                    var jitter = shells.Length == 1 || i == 0 ? 0f : (random.Next() - 0.5f) * spec.Duration / shells.Length * 0.6f;
                    shells[i] = new PlannedShell(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance, Math.Clamp(slot + jitter, 0f, spec.Duration));
                }
                return shells;
            }
            case StrikeKind.Creeping:
            {
                var shells = new PlannedShell[spec.Rows * spec.Shells];
                for (var row = 0; row < spec.Rows; row++)
                for (var k = 0; k < spec.Shells; k++)
                {
                    var across = spec.Shells == 1 ? 0f : spec.Width * ((float)k / (spec.Shells - 1) - 0.5f);
                    var wobble = (random.Next() - 0.5f) * 1.2f;
                    shells[row * spec.Shells + k] = new PlannedShell(across + wobble, row * spec.Length + (random.Next() - 0.5f), row * spec.RowInterval + random.Next() * 0.25f);
                }
                return shells;
            }
            case StrikeKind.Cluster:
            {
                var shells = new PlannedShell[spec.Shells];
                for (var i = 0; i < shells.Length; i++)
                {
                    var along = shells.Length == 1 ? 0f : spec.Length * ((float)i / (shells.Length - 1) - 0.5f);
                    // The line runs across the throw direction, left to right.
                    shells[i] = new PlannedShell(along, (random.Next() - 0.5f) * 1.5f, shells.Length == 1 ? 0f : spec.Duration * i / (shells.Length - 1));
                }
                return shells;
            }
            default:
                return Array.Empty<PlannedShell>();
        }
    }

    /// <summary>A small deterministic generator (numerical recipes LCG); values in [0, 1).</summary>
    internal struct Lcg
    {
        private uint _state;
        internal Lcg(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;
        internal float Next()
        {
            _state = _state * 1664525u + 1013904223u;
            return (_state >> 8) / 16777216f;
        }
    }

    /// <summary>The host's seed for one call, identical for the same call on any host.</summary>
    public static uint Seed(ulong summoner, uint throwId)
    {
        var h = 2166136261u;
        for (var i = 0; i < 8; i++) { h ^= (byte)(summoner >> (i * 8)); h *= 16777619u; }
        for (var i = 0; i < 4; i++) { h ^= (byte)(throwId >> (i * 8)); h *= 16777619u; }
        return h;
    }
}

/// <summary>Choosing targets, on plain numbers so it is testable.</summary>
public static class Targeting
{
    /// <summary>Index of the candidate with the highest maximum health (ties: the nearer one), or -1 for none.</summary>
    public static int Largest(ReadOnlySpan<float> healthMax, ReadOnlySpan<float> distance)
    {
        var best = -1;
        for (var i = 0; i < healthMax.Length; i++)
            if (best < 0 || healthMax[i] > healthMax[best] || (healthMax[i] == healthMax[best] && distance[i] < distance[best])) best = i;
        return best;
    }

    /// <summary>Index of the nearest candidate, or -1 for none.</summary>
    public static int Nearest(ReadOnlySpan<float> distance)
    {
        var best = -1;
        for (var i = 0; i < distance.Length; i++)
            if (best < 0 || distance[i] < distance[best]) best = i;
        return best;
    }

    /// <summary>Index of the nearest candidate the probe accepts, or -1. Candidates are offered nearest first and at most
    /// <paramref name="budget"/> of them are tried (a path search each); the rest are not looked at. <paramref name="scratch"/>
    /// must hold at least as many floats as there are candidates.</summary>
    public static int NearestReachable<T>(ReadOnlySpan<float> distance, Span<float> scratch, int budget, ref T probe) where T : IReach
    {
        distance.CopyTo(scratch);
        var all = scratch[..distance.Length];
        for (var tried = 0; tried < budget; tried++)
        {
            var best = Nearest(all);
            if (best < 0 || float.IsPositiveInfinity(all[best])) return -1;
            if (probe.Reaches(best)) return best;
            all[best] = float.PositiveInfinity;
        }
        return -1;
    }
}

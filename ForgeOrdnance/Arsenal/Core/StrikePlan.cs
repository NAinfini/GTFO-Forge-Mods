using System;

namespace ForgeOrdnance.Arsenal;

/// <summary>Which model a falling shell shows.</summary>
internal enum ShellLook : byte { Shell = 1, Bomblet = 2, Canister = 3, Emitter = 4, Precision = 5, Hunter = 6, Incendiary = 7 }

/// <summary>One shell of a strike in the arrival frame: <see cref="Right"/> and <see cref="Forward"/> are metres from the
/// beacon along the arrival's right and forward (thrower toward beacon) axes, <see cref="Time"/> seconds after the arrival.</summary>
internal readonly struct PlannedShell
{
    public PlannedShell(float right, float forward, float time) { Right = right; Forward = forward; Time = time; }
    public readonly float Right, Forward, Time;
}

/// <summary>The ground a strike can hurt, in the terms it is drawn in: a disc of <see cref="Radius"/> metres around the beacon, or (radius 0) a strip
/// along the throw from <see cref="Back"/> to <see cref="Front"/> metres, <see cref="HalfWidth"/> to each side. <see cref="Seconds"/> from now it is over: the last
/// shell is down or the area stands (the area shows its own mark from then on).</summary>
internal readonly record struct StrikeFootprint(float Radius, float Back, float Front, float HalfWidth, float Seconds)
{
    /// <summary>What <paramref name="spec"/> reaches when it arrives <paramref name="delay"/> seconds from now; null for the two that have no fixed
    /// ground (the beam walks after enemies, the hunter follows one). <paramref name="length"/> is a strafing run's length after the walls cut it.</summary>
    public static StrikeFootprint? Of(StrikeSpec spec, float length, float delay)
    {
        var lead = delay + StrikeTiming.Pace(spec.Kind).Lead;
        return spec.Kind switch
        {
            StrikeKind.Precision => new StrikeFootprint(spec.BlastRadius, 0f, 0f, 0f, lead),
            StrikeKind.Incendiary => new StrikeFootprint(Math.Max(spec.ZoneRadius, spec.BlastRadius), 0f, 0f, 0f, lead + StrikeTiming.ZoneStartDelay(spec.Kind)),
            StrikeKind.Gas or StrikeKind.Stasis => new StrikeFootprint(spec.ZoneRadius, 0f, 0f, 0f, lead + StrikeTiming.ZoneStartDelay(spec.Kind)),
            StrikeKind.Area => new StrikeFootprint(spec.Spread + spec.BlastRadius, 0f, 0f, 0f, lead + spec.Duration),
            StrikeKind.Creeping => new StrikeFootprint(0f, -spec.BlastRadius, (spec.Rows - 1) * spec.Length + spec.BlastRadius, spec.Width * 0.5f + spec.BlastRadius, lead + (spec.Rows - 1) * spec.RowInterval + 0.25f),
            StrikeKind.Cluster => new StrikeFootprint(0f, -spec.BlastRadius - 0.75f, spec.BlastRadius + 0.75f, spec.Length * 0.5f + spec.BlastRadius, lead + spec.Duration),
            StrikeKind.Strafe => new StrikeFootprint(0f, 0f, length, spec.Width * 0.5f, delay + StrikeTiming.StrafeLeadSeconds + length / spec.Speed),
            _ => null,
        };
    }
}

/// <summary>Where and when the shells of the fixed-pattern strikes fall. Built on the host only; every peer learns each shell
/// from the host's per-shell message, so the pattern is not part of the protocol. Deterministic for a seed, so it is testable.</summary>
internal static class StrikePlan
{
    public static ShellLook Look(StrikeKind kind) => kind switch
    {
        StrikeKind.Cluster => ShellLook.Bomblet,
        StrikeKind.Gas => ShellLook.Canister,
        StrikeKind.Stasis => ShellLook.Emitter,
        StrikeKind.Precision => ShellLook.Precision,
        StrikeKind.Hunter => ShellLook.Hunter,
        StrikeKind.Incendiary => ShellLook.Incendiary,
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

    /// <summary>The unit direction (x, z) a call's fired shells come in from, the same on every peer: derived from the call, so nothing is sent for it.</summary>
    public static (float X, float Z) ApproachSide(ulong summoner, uint throwId)
    {
        var angle = new Lcg(Seed(summoner, throwId)).Next() * MathF.PI * 2f;
        return (MathF.Cos(angle), MathF.Sin(angle));
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
        => ForgeCore.Fnv.Mix32(ForgeCore.Fnv.Mix32(ForgeCore.Fnv.Offset32, summoner), throwId);
}

/// <summary>Choosing targets, on plain numbers so it is testable.</summary>
internal static class Targeting
{
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

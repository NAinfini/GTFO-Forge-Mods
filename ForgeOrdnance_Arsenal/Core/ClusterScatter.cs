using System;

namespace ForgeOrdnanceArsenal;

/// <summary>Where the sparklets of a cluster bomblet's airburst land, as a pure function of the call and the bomblet's own impact
/// point. Every peer computes the same points from data it already has (the call's summoner and throw id and the host's shell
/// record), so the scatter needs no message of its own. Presentation only: the damage is the host's single blast at the impact.</summary>
public static class ClusterScatter
{
    /// <summary>Sparklets per bomblet.</summary>
    public const int Sparklets = 3;

    /// <summary>The seed of one bomblet: the call's seed mixed with its impact point to the centimetre.</summary>
    public static uint Seed(ulong summoner, uint throwId, float x, float z)
    {
        var h = StrikePlan.Seed(summoner, throwId);
        h = unchecked((h ^ (uint)(int)MathF.Round(x * 100f)) * 16777619u);
        h = unchecked((h ^ (uint)(int)MathF.Round(z * 100f)) * 16777619u);
        return h;
    }

    /// <summary>Fills <paramref name="offsets"/> with <paramref name="count"/> pairs (metres to the east and to the north of the
    /// impact), spread around it: a seeded turn, equal angular steps with a little jitter, and a seeded distance between half
    /// and all of <paramref name="radius"/>.</summary>
    public static void Offsets(uint seed, int count, float radius, float[] offsets)
    {
        if (count < 0 || offsets.Length < count * 2) throw new ArgumentOutOfRangeException(nameof(count));
        var random = new StrikePlan.Lcg(seed);
        var phase = random.Next() * MathF.PI * 2f;
        for (var i = 0; i < count; i++)
        {
            var angle = phase + i * MathF.PI * 2f / count + (random.Next() - 0.5f) * 0.7f;
            var distance = radius * (0.5f + 0.5f * random.Next());
            offsets[2 * i] = MathF.Cos(angle) * distance;
            offsets[2 * i + 1] = MathF.Sin(angle) * distance;
        }
    }
}

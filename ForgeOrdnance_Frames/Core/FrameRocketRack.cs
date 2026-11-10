using System;

namespace ForgeOrdnanceFrames;

/// <summary>Which tube of the suit's launcher each rocket of a salvo leaves from, and which tubes hold a rocket at a given moment.
/// Rocket <c>i</c> leaves tube <c>i % tubes</c>, <see cref="FrameRules.RocketInterval"/> after the one before. A tube is empty from
/// the moment its first rocket of the salvo leaves until the last of them is reloaded; the salvo's rockets are reloaded in firing
/// order, evenly over the cooldown, rocket <c>i</c> at <c>(i + 1) / salvo</c> of it, so the last tube fills the moment the next salvo
/// is ready. Every peer computes this from the salvo's launch time; nothing is sent per tube.</summary>
internal static class FrameRocketRack
{
    /// <summary>A launcher's tubes are its children <c>muzzle_1</c>, <c>muzzle_2</c>, ... (no gaps), each at a tube's mouth.</summary>
    public const string MuzzlePrefix = "muzzle_", FirstMuzzle = MuzzlePrefix + "1";

    public static int Tube(int rocket, int tubes) => rocket % tubes;

    /// <summary>Whether tube <paramref name="tube"/> holds a rocket at <paramref name="now"/>, for a salvo of <paramref name="salvo"/>
    /// launched at <paramref name="salvoAt"/> (no salvo yet: <see cref="float.NegativeInfinity"/>).</summary>
    public static bool Loaded(int tube, int tubes, int salvo, float cooldown, float salvoAt, float now)
    {
        if (tubes < 1 || salvo < 1) throw new ArgumentOutOfRangeException(nameof(salvo), "A launcher has at least one tube and a salvo at least one rocket.");
        if (float.IsNegativeInfinity(salvoAt)) return true;
        var emptied = float.PositiveInfinity;
        var refilled = float.NegativeInfinity;
        for (var i = 0; i < salvo; i++)
        {
            if (Tube(i, tubes) != tube) continue;
            emptied = Math.Min(emptied, salvoAt + i * FrameRules.RocketInterval);
            refilled = Math.Max(refilled, salvoAt + cooldown * (i + 1) / salvo);
        }
        // A tube this salvo never used stays loaded.
        return float.IsPositiveInfinity(emptied) || now < emptied || now >= refilled;
    }
}

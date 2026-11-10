using System;

namespace ForgeOrdnanceArsenal;

/// <summary>Where a dispenser's moving parts are at a moment: plates <see cref="Open"/> 0 (shut) to 1 (open), column <see cref="Lift"/> 0 (lowered
/// out of sight in the base) to 1 (standing), and the column's turn in degrees.</summary>
internal readonly record struct DispenserPose(float Open, float Lift, float SpinDegrees);

/// <summary>The one timeline of a dispenser, the same on every peer (seconds since the device has risen): per round the plates open, the column
/// spins throwing one munition after another and stops, the plates shut; between rounds the column lowers, is loaded and rises again.</summary>
internal static class DispenserPlan
{
    private static float Ease(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }

    /// <summary>The pose <paramref name="t"/> seconds after the device has risen.</summary>
    public static DispenserPose Pose(DeployableSpec spec, float t)
    {
        if (t < 0f) return new DispenserPose(0f, 1f, 0f);
        var round = Math.Min(spec.Rounds - 1, (int)(t / spec.CycleSeconds));
        var local = t - round * spec.CycleSeconds;
        var full = spec.SpinTurns * 360f;
        var half = spec.ReloadSeconds * 0.5f;
        if (local < spec.OpenSeconds) return new DispenserPose(Ease(local / spec.OpenSeconds), 1f, 0f);
        local -= spec.OpenSeconds;
        if (local < spec.SpinSeconds) return new DispenserPose(1f, 1f, full * Ease(local / spec.SpinSeconds));
        local -= spec.SpinSeconds + DeployableSpec.StopSeconds;
        if (local < 0f) return new DispenserPose(1f, 1f, full);
        if (local < spec.OpenSeconds) return new DispenserPose(1f - Ease(local / spec.OpenSeconds), 1f, full);
        local -= spec.OpenSeconds;
        if (round == spec.Rounds - 1) return new DispenserPose(0f, 1f, full);
        if (local < half) return new DispenserPose(0f, 1f - Ease(local / half), full);
        return new DispenserPose(0f, Ease((local - half) / half), full);
    }

    /// <summary>When munition <paramref name="index"/> (round by round, <see cref="DeployableSpec.Count"/> a round) leaves its hole. Each round
    /// throws at its own phase of the slots, so a later round's headings fall between an earlier one's.</summary>
    public static float LaunchAt(DeployableSpec spec, int index)
    {
        var round = index / spec.Count;
        var slot = index % spec.Count;
        return round * spec.CycleSeconds + spec.OpenSeconds + spec.SpinSeconds * (0.08f + 0.84f * (slot + (round + 0.5f) / spec.Rounds) / spec.Count);
    }

    /// <summary>When it lands.</summary>
    public static float LandAt(DeployableSpec spec, int index) => LaunchAt(spec, index) + spec.FlightSeconds;

    /// <summary>When round <paramref name="round"/> is loaded into the holes (the column is lowered, the first round is loaded from the start).</summary>
    public static float LoadedAt(DeployableSpec spec, int round) => round == 0 ? float.NegativeInfinity : round * spec.CycleSeconds - spec.ReloadSeconds * 0.5f;

    /// <summary>The column's turn when munition <paramref name="index"/> leaves, in degrees.</summary>
    public static float LaunchSpin(DeployableSpec spec, int index) => Pose(spec, LaunchAt(spec, index)).SpinDegrees;

    /// <summary>The direction, in degrees about up from the device's front, the hole of munition <paramref name="index"/> faces before the column turns: its plate's
    /// (consecutive munitions take consecutive plates, the first the plate at the device's front, going clockwise from above).</summary>
    public static float SlotDegrees(DeployableSpec spec, int index) => index % spec.Plates * 360f / spec.Plates;
}

namespace ForgeOrdnanceArsenal;

/// <summary>The pacing of a strike from the host's planning to the last sound, one timeline for the host (damage, areas, the
/// beam) and every peer (shells, blasts, areas, the beam): all of it counts from the same moments, so what is heard, seen and
/// hurt line up.</summary>
public static class StrikeTiming
{
    /// <summary>Seconds a shell takes from above the ceiling to the floor.</summary>
    public const float FallSeconds = 1.8f;

    /// <summary>Seconds after a shell's blast that its debris is heard coming down.</summary>
    public const float DebrisDelay = 0.5f;

    /// <summary>Seconds after the incendiary shell lands that the ignition is heard.</summary>
    public const float IgniteDelay = 0.2f;

    /// <summary>Seconds the sky glow charges before the beam is drawn and starts to cut and move.</summary>
    public const float BeamChargeSeconds = 1.5f;

    /// <summary>Seconds from a gas, stasis or incendiary shell's landing to its area starting: the damage, the slow, the cloud, the
    /// light and the loop. The landing's own release, pulse or ignition plays first.</summary>
    public static float ZoneStartDelay(StrikeKind kind) => kind switch
    {
        StrikeKind.Gas or StrikeKind.Incendiary => 0.8f,
        StrikeKind.Stasis => 1f,
        _ => 0f,
    };
}

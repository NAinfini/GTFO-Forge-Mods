namespace ForgeOrdnance;

/// <summary>What the call-in beacon animates each frame, from its age and the time left: the numbers it hands ForgeVisual's ground mark.
/// Pure arithmetic: the tightening over the last three seconds and the flash at arrival.</summary>
internal readonly struct BeaconFrame
{
    /// <summary>0 calm to 1 at arrival, eased over the last <see cref="BeaconShow.UrgentSeconds"/>.</summary>
    internal float Urgency { get; init; }
    /// <summary>How far the mark's hot colour is turned toward white.</summary>
    internal float HotToWhite { get; init; }
    /// <summary>The ground mark: brightness, time left as a fraction of the whole countdown, and pings per second.</summary>
    internal float MarkOpacity { get; init; }
    internal float MarkRemaining { get; init; }
    internal float PingRate { get; init; }
}

internal static class BeaconShow
{
    internal const float FadeIn = 0.3f, UrgentSeconds = 3f, FlashSeconds = 0.15f;
    /// <summary>The mark's brightness while it waits: it is additive with a hot centre, and at full brightness the game's bloom burned it to a white disc.</summary>
    internal const float MarkLevel = 0.6f;
    /// <summary>Pings per second before the last seconds, and at the start and the end of them.</summary>
    internal const float CalmPingRate = 0.62f, UrgentPingFrom = 1.1f, UrgentPingTo = 3f;

    internal static BeaconFrame At(float age, float remaining, float total)
    {
        var grow = ForgeUI.Ease.Clamp01(age / FadeIn);
        var urgency = ForgeUI.Ease.Smoothstep(1f - remaining / UrgentSeconds);
        var flash = remaining < FlashSeconds ? 1f - remaining / FlashSeconds : 0f;
        return new BeaconFrame
        {
            Urgency = urgency,
            HotToWhite = 0.4f * urgency + flash,
            MarkOpacity = MarkLevel * grow * (1f + flash),
            MarkRemaining = ForgeUI.Ease.Clamp01(remaining / total),
            PingRate = remaining > UrgentSeconds ? CalmPingRate : UrgentPingFrom + (UrgentPingTo - UrgentPingFrom) * urgency,
        };
    }
}

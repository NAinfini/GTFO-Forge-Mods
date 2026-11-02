using System;

namespace ForgeOrdnance;

/// <summary>What the call-in beacon animates each frame, from its age and the time left: the numbers it hands ForgeVisual's beam and
/// ground mark. Pure arithmetic, so the look (calm breathing, the tightening over the last three seconds, the flash at arrival) is
/// checked offline.</summary>
internal readonly struct BeaconFrame
{
    /// <summary>0 to 1 over the first <see cref="BeaconShow.FadeIn"/> seconds.</summary>
    internal float Grow { get; init; }
    /// <summary>0 calm to 1 at arrival, eased over the last <see cref="BeaconShow.UrgentSeconds"/>.</summary>
    internal float Urgency { get; init; }
    /// <summary>1 at arrival, fading in over the last <see cref="BeaconShow.FlashSeconds"/>.</summary>
    internal float Flash { get; init; }
    /// <summary>How far the beam's hot colour is turned toward white.</summary>
    internal float HotToWhite { get; init; }
    /// <summary>Beam width multiplier (core), the extra width multiplier of the glow, and the flare's size multiplier.</summary>
    internal float Width { get; init; }
    internal float GlowWidthScale { get; init; }
    internal float FlareScale { get; init; }
    /// <summary>Brightness of the core, the glow and the flare.</summary>
    internal float CoreLevel { get; init; }
    internal float GlowLevel { get; init; }
    internal float FlareLevel { get; init; }
    /// <summary>The ground mark: brightness, time left as a fraction of the whole countdown, and pings per second.</summary>
    internal float MarkOpacity { get; init; }
    internal float MarkRemaining { get; init; }
    internal float PingRate { get; init; }
}

internal static class BeaconShow
{
    internal const float FadeIn = 0.3f, RiseSeconds = 0.3f, UrgentSeconds = 3f, FlashSeconds = 0.15f;
    /// <summary>Pings per second before the last seconds, and at the start and the end of them.</summary>
    internal const float CalmPingRate = 0.62f, UrgentPingFrom = 1.1f, UrgentPingTo = 3f;

    /// <summary>The fraction of its height the beam has risen <paramref name="age"/> seconds after the ball landed (a beacon replayed for a
    /// late joiner is already up).</summary>
    internal static float Rise(float age, bool fresh)
    {
        if (!fresh) return 1f;
        var rise = Clamp01(age / RiseSeconds);
        return 1f - (1f - rise) * (1f - rise);
    }

    internal static BeaconFrame At(float age, float remaining, float total)
    {
        var grow = Clamp01(age / FadeIn);
        var breathe = 0.5f + 0.5f * MathF.Sin(age * 2.6f);
        var urgency = Clamp01(1f - remaining / UrgentSeconds);
        urgency = urgency * urgency * (3f - 2f * urgency);
        var flash = remaining < FlashSeconds ? 1f - remaining / FlashSeconds : 0f;
        return new BeaconFrame
        {
            Grow = grow,
            Urgency = urgency,
            Flash = flash,
            HotToWhite = 0.4f * urgency + flash,
            Width = grow * (1f + 3f * flash),
            GlowWidthScale = (0.94f + 0.12f * breathe) * (1f - 0.25f * urgency) * (1f + 2f * flash) / (1f + 3f * flash),
            FlareScale = grow * (0.92f + 0.16f * breathe) * (1f + 0.4f * urgency) * (1f + 2f * flash),
            CoreLevel = MathF.Min(1.6f, (0.85f + 0.15f * breathe) * grow * (1f + 0.35f * urgency) + flash),
            GlowLevel = (0.40f + 0.10f * breathe) * grow * (1f + 0.4f * urgency) + 0.6f * flash,
            FlareLevel = 0.6f * grow + flash,
            MarkOpacity = grow * (1f + flash),
            MarkRemaining = Clamp01(remaining / total),
            PingRate = remaining > UrgentSeconds ? CalmPingRate : UrgentPingFrom + (UrgentPingTo - UrgentPingFrom) * urgency,
        };
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
namespace ForgeOrdnance;

/// <summary>The drop pod's timeline in seconds from the arrival moment, the same on every peer: when it touches down, when its
/// doors start to open and when the host stocks its bays. The host stocks the bays right after the pod has settled, well before
/// the doors open, so the pickups have long been replicated to every peer when the doors show them.</summary>
internal static class PodTimeline
{
    internal const float FallSeconds = 1.6f;
    internal const float SinkSeconds = 0.3f;
    /// <summary>The pod settles (rubble, dust and the impact's tail) before the doors start to open.</summary>
    internal const float PodOpenAfterImpact = 3.4f;
    /// <summary>The host stocks the bays this long after touchdown: once the drill has bored in and the pod is at rest.</summary>
    internal const float StockAfterImpact = SinkSeconds + 0.1f;
    /// <summary>The least time between stocking and the doors opening that the replicated spawn is given to reach every peer.</summary>
    internal const float NetworkLead = 2f;

    /// <summary>Seconds from the arrival until the host spawns the pod's pickups.</summary>
    internal const float StockDelay = FallSeconds + StockAfterImpact;
    /// <summary>Seconds from the arrival until the doors start to open: from then on the pickups can be taken.</summary>
    internal const float OpenDelay = FallSeconds + PodOpenAfterImpact;

    /// <summary>Whether a pod's pickups may be taken: only once its doors have started to open. A pod that is gone no longer holds them.</summary>
    internal static bool MayTake(bool podFound, bool podOpen) => !podFound || podOpen;
}

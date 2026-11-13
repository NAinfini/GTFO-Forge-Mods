using ForgeCore;

namespace ForgeOrdnance.Frames;

/// <summary>The suits' own messages, on the mod's one ForgeCore channel (<see cref="OrdnanceNetwork"/> opens it), whose format
/// fingerprint refuses a player with other messages, and which drops a message sent in another level or before a checkpoint reload. The suits' states
/// travel as the carrier's kept per-call state, and the suits of the last checkpoint in the mod's one checkpoint copy
/// (<see cref="OrdnanceSession.CheckpointContent"/>), not here. Handlers run on the main thread; the network layer does not
/// authenticate senders, so every handler checks the sender itself.</summary>
internal static class FrameNetwork
{
    /// <summary>A player to the host, answered with one <see cref="FrameRejection"/> byte: board, leave, report ammunition, use a
    /// special (<see cref="FrameRequestMessage"/>); a rocket salvo at a point (<see cref="FrameRocketMessage"/>).</summary>
    internal static NetRequest Request { get; private set; } = null!;
    internal static NetRequest Rockets { get; private set; } = null!;
    /// <summary>Host to every other peer: a salvo it granted.</summary>
    internal static NetEvent<FrameRocketMessage> RocketLaunch { get; private set; } = null!;

    /// <summary>Shown only, so sent lossy: the pilot's shots and the charge's shoulder hit to every other peer, and the host's
    /// word to a pilot where a hit came from (its outcome is in the suit's state).</summary>
    internal static NetEvent<FrameFireMessage> Fire { get; private set; } = null!;
    internal static NetEvent<FrameBumpMessage> Bump { get; private set; } = null!;
    internal static NetEvent<FrameHitMessage> Hit { get; private set; } = null!;

    internal static void Register()
    {
        var channel = OrdnanceNetwork.Channel;
        Request = channel.Request(FrameMessages.RequestEvent, 1, FrameWorld.ServeRequest, 64);
        Rockets = channel.Request(FrameMessages.RocketRequestEvent, 1, FrameWorld.ServeRockets, 64);
        RocketLaunch = channel.Event<FrameRocketMessage>(FrameMessages.RocketLaunchEvent, FrameWorld.OnRocketLaunch);
        Fire = channel.Event<FrameFireMessage>(FrameMessages.FireEvent, FrameWorld.OnFire, NetDelivery.Lossy);
        Bump = channel.Event<FrameBumpMessage>(FrameMessages.BumpEvent, FrameWorld.OnBump, NetDelivery.Lossy);
        Hit = channel.Event<FrameHitMessage>(FrameMessages.HitEvent, FrameWorld.OnHit, NetDelivery.Lossy);
    }
}

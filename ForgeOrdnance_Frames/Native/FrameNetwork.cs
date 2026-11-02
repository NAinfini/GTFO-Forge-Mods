using System;
using ForgeCore;
using SNetwork;

namespace ForgeOrdnanceFrames;

/// <summary>This plugin's own messages, through ForgeCore's channel for this plugin, whose message format
/// fingerprint refuses a player with other Frames messages explicitly. Handlers run on the main thread from <see cref="Pump"/>; the
/// network layer does not authenticate senders, so every handler checks the sender itself.</summary>
internal static class FrameNetwork
{
    /// <summary>Lossy messages (shots, hit directions) that may wait for the next <see cref="Pump"/>; the excess is dropped and logged.
    /// Reliable messages (requests, answers, checkpoints) are not limited by it.</summary>
    private const int InboxLimit = 256;
    private static NetChannel? _channel;

    internal static void Register()
    {
        if (_channel != null) return;
        _channel = Net.Open(Plugin.Id, InboxLimit);
        _channel.On<FrameRequestMessage>(FrameMessages.RequestEvent, FrameWorld.OnRequest);
        _channel.On<FrameRejectMessage>(FrameMessages.RejectEvent, FrameWorld.OnReject);
        _channel.On<FrameFireMessage>(FrameMessages.FireEvent, FrameWorld.OnFire, NetDelivery.Lossy);
        _channel.On<FrameRocketMessage>(FrameMessages.RocketRequestEvent, FrameWorld.OnRocketRequest);
        _channel.On<FrameRocketMessage>(FrameMessages.RocketLaunchEvent, FrameWorld.OnRocketLaunch);
        _channel.On<FrameCheckpointMessage>(FrameMessages.CheckpointEvent, FrameWorld.OnCheckpoint);
        _channel.On<FrameHitMessage>(FrameMessages.HitEvent, FrameWorld.OnHit, NetDelivery.Lossy);
        _channel.PeerSynced += FrameWorld.PeerSynced;
    }

    internal static void Pump() => _channel?.Pump();

    /// <summary>Recovery and cleanup: drops every message still waiting.</summary>
    internal static void Reset() => _channel?.Clear();

    private static NetChannel Channel => _channel ?? throw new InvalidOperationException("The exosuit network channel is not open.");

    /// <summary>Requests go to the host; the host handles its own directly.</summary>
    internal static void ToHost(FrameRequestMessage message)
    {
        if (SNet.IsMaster) { FrameWorld.OnRequest(SNet.LocalPlayer.Lookup, message); return; }
        // No host (the session is ending or the host left): the request has nowhere to go and the local state is released anyway.
        Channel.TryToHost(FrameMessages.RequestEvent, message);
    }

    internal static void Reject(ulong to, FrameRejectMessage message)
    {
        if (!SNet.IsMaster) return;
        if (to == SNet.LocalPlayer.Lookup) { FrameWorld.OnReject(SNet.LocalPlayer.Lookup, message); return; }
        if (SNet.TryGetPlayer(to, out _)) Channel.To(to, FrameMessages.RejectEvent, message);
    }

    internal static void RocketToHost(FrameRocketMessage message)
    {
        if (SNet.IsMaster) { FrameWorld.OnRocketRequest(SNet.LocalPlayer.Lookup, message); return; }
        Channel.TryToHost(FrameMessages.RocketRequestEvent, message);

    }

    /// <summary>Host: a granted salvo to every other peer.</summary>
    internal static void RocketLaunch(FrameRocketMessage message)
    {
        if (!SNet.IsMaster) return;
        FrameWorld.OnRocketLaunch(SNet.LocalPlayer.Lookup, message);
        Channel.Broadcast(FrameMessages.RocketLaunchEvent, message);
    }

    internal static void Checkpoint(FrameCheckpointMessage message, ulong to = 0)
    {
        if (to == 0) Channel.Broadcast(FrameMessages.CheckpointEvent, message);
        else if (SNet.TryGetPlayer(to, out _)) Channel.To(to, FrameMessages.CheckpointEvent, message);
    }

    /// <summary>Host: a hit the suit took, to its pilot (direction for the cockpit; not needed for the outcome, which the state carries); sent lossy.</summary>
    internal static void Hit(ulong to, FrameHitMessage message)
    {
        if (SNet.TryGetPlayer(to, out _)) Channel.To(to, FrameMessages.HitEvent, message);
    }

    /// <summary>Shots, for every other peer's sound and tracer: presentation only, sent lossy.</summary>
    internal static void Fire(FrameFireMessage message) => Channel.Broadcast(FrameMessages.FireEvent, message);
}

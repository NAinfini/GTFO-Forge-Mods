using System;
using System.Collections.Generic;
using ForgeCore;
using SNetwork;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>Energy Lab's messages through its ForgeCore channel, whose message formats ForgeCore fingerprints, with this build's
/// fingerprint as the channel content: a player with other message formats or another build is refused by ForgeCore's handshake (an error in
/// both logs, a chat line here) and their messages are dropped. The world epoch (minted by the host when the world is ready, adopted by
/// clients, dropped on a host change, checkpoint reload or level end), which player is ready, and dropping messages from another epoch
/// are ForgeCore's channel (<see cref="NetChannel.FollowWorld"/>); this class only decides when its gear is built and what each message means.</summary>
internal static class EnergyNetwork
{
    private const string FireEvent = "fire";
    private const string VisualEvent = "visual";
    // A held beam's or flame's shots: ten a second of presentation only, so they stay off the reliable channel. A lost one is bridged by the stream's own window, a late one refused by its sequence.
    private const string StreamVisualEvent = "stream-visual";
    private const string ChargeEvent = "charge";
    private const string ChargeVisualEvent = "charge-visual";
    private const string ModeEvent = "mode";
    private const string ModeVisualEvent = "mode-visual";
    private const string FlightEndEvent = "flight-end";
    private enum PacketKind { Fire, Visual, Charge, ChargeVisual, Mode, ModeVisual, FlightEnd }
    private struct Inbound
    {
        internal PacketKind Kind;
        internal ulong Sender;
        internal FireCommand Command;
        internal FireVisual Visual;
        internal ChargePacket Charge;
        internal ModePacket Mode;
        internal FlightEnd End;
    }
    private static readonly HashSet<ulong> Mismatched = new();
    private static NetChannel? _channel;
    private static EnergyController? _pumping;
    // The channel dropped the world since the last pump: the controller's session state is void.
    private static bool _worldDropped;
    private static uint _nextSequence;
    internal static ulong Epoch => _channel?.Epoch ?? 0;
    internal static BuildFingerprint Fingerprint { get; private set; }
    internal static bool Ready => Epoch != 0 && (Session.IsHost || Channel.HostReady);
    internal static uint NextSequence() { if (++_nextSequence == 0) ++_nextSequence; return _nextSequence; }

    internal static void Register()
    {
        if (_channel != null) return;
        var channel = Net.Open(Plugin.Id, EnergyProtocol.InboxLimit);
        channel.OnEpoch<FireCommand>(FireEvent, (s, p) => Handle(new() { Kind = PacketKind.Fire, Sender = s, Command = p }));
        channel.OnEpoch<FireVisual>(VisualEvent, (s, p) => Handle(new() { Kind = PacketKind.Visual, Sender = s, Visual = p }));
        channel.OnEpoch<FireVisual>(StreamVisualEvent, (s, p) => Handle(new() { Kind = PacketKind.Visual, Sender = s, Visual = p }), NetDelivery.Lossy);
        channel.OnEpoch<ChargePacket>(ChargeEvent, (s, p) => Handle(new() { Kind = PacketKind.Charge, Sender = s, Charge = p }));
        channel.OnEpoch<ChargePacket>(ChargeVisualEvent, (s, p) => Handle(new() { Kind = PacketKind.ChargeVisual, Sender = s, Charge = p }));
        channel.OnEpoch<ModePacket>(ModeEvent, (s, p) => Handle(new() { Kind = PacketKind.Mode, Sender = s, Mode = p }));
        channel.OnEpoch<ModePacket>(ModeVisualEvent, (s, p) => Handle(new() { Kind = PacketKind.ModeVisual, Sender = s, Mode = p }));
        channel.OnEpoch<FlightEnd>(FlightEndEvent, (s, p) => Handle(new() { Kind = PacketKind.FlightEnd, Sender = s, End = p }));
        channel.Refused += Refused;
        channel.EpochChanged += () => _pumping?.SessionChanged();
        channel.WorldReset += () => { _worldDropped = true; Mismatched.Clear(); };
        channel.PeerSynced += _ => EnergyController.PeerSynced();
        channel.FollowWorld(() => EnergyGearRegistry.Ready);
        _channel = channel;
    }

    /// <summary>The definitions are final: the build fingerprint becomes the channel's content, which every player must share.</summary>
    internal static void SealDefinitions()
    {
        Fingerprint = BuildFingerprint.Compute();
        Channel.Content = Fingerprint.ToString();
    }

    private static NetChannel Channel => _channel ?? throw new InvalidOperationException("Energy Lab's network channel is not open.");

    internal static void Pump(EnergyController controller)
    {
        if (_worldDropped) { _worldDropped = false; controller.SessionChanged(); }
        // ForgeCore announces Live, Ready and the epoch when they change and to every player who syncs; nothing repeats on a timer.
        Channel.Ready = controller.PresentationReady;
        _pumping = controller;
        try { Channel.Pump(); }
        finally { _pumping = null; }
    }

    private static void Handle(Inbound item)
    {
        var controller = _pumping ?? throw new InvalidOperationException("Energy messages are handled only inside Pump.");
        // Other worlds and authorities were dropped by the channel; this world must be up and presenting.
        if (!Channel.Live || !controller.PresentationReady) return;
        if (item.Kind == PacketKind.Fire && Session.IsHost)
        {
            if (PeerReady(item.Sender)) controller.ReceiveCommand(item.Sender, item.Command);
            return;
        }
        if (item.Kind == PacketKind.Visual && Session.IsHost)
        {
            if (PeerReady(item.Sender)) controller.ReceiveNativeVisual(item.Sender, item.Visual);
            return;
        }
        switch (item.Kind)
        {
            case PacketKind.Charge or PacketKind.Mode when Session.IsHost && !PeerReady(item.Sender): break;
            case PacketKind.Charge: if (Session.IsHost) controller.ReceiveCharge(item.Sender, item.Charge); break;
            case PacketKind.Mode: if (Session.IsHost) controller.ReceiveMode(item.Sender, item.Mode); break;
            case PacketKind.ChargeVisual: if (Channel.FromHost(item.Sender)) controller.ReceiveChargeVisual(item.Charge); break;
            case PacketKind.Visual: if (Channel.FromHost(item.Sender)) controller.ReceiveVisual(item.Visual); break;
            case PacketKind.ModeVisual: if (Channel.FromHost(item.Sender)) controller.ReceiveModeVisual(item.Mode); break;
            case PacketKind.FlightEnd: if (Channel.FromHost(item.Sender)) controller.ReceiveFlightEnd(item.End); break;
        }
    }

    private static bool PeerReady(ulong sender) => Channel.PeerReady(sender);

    /// <summary>ForgeCore refused a player's copy (and logged why). Players cannot read logs: once per player, say in chat (on this
    /// machine only) why energy guns stay off.</summary>
    private static void Refused(ulong lookup, string reason)
    {
        if (!Mismatched.Add(lookup) || !SNet.TryGetPlayer(lookup, out var player) || player == null) return;
        ForgeUI.UIKit.PostChatNotice(EnergyText.Mismatch(player.NickName));
    }

    internal static void Send(FireCommand command)
    {
        if (SNet.Master == null) return;
        if (!Ready || command.Epoch != Epoch) throw new InvalidOperationException("Energy host/session is unavailable.");
        Channel.ToHost(FireEvent, command);
    }
    private static string VisualName(FireVisual visual)
        => (EnergyMode)visual.Mode is EnergyMode.Beam or EnergyMode.Flame ? StreamVisualEvent : VisualEvent;
    internal static void Broadcast(FireVisual visual) => Channel.Broadcast(VisualName(visual), visual);
    internal static void SendVisual(FireVisual visual)
    {
        if (!Session.IsHost && SNet.Master == null) return;
        if (!Ready || visual.Epoch != Epoch) throw new InvalidOperationException("Energy visual session is unavailable.");
        if (Session.IsHost) Broadcast(visual);
        else Channel.ToHost(VisualName(visual), visual);
    }
    internal static void SendCharge(ChargePacket packet) { if (SNet.Master != null) Channel.ToHost(ChargeEvent, packet); }
    internal static void BroadcastCharge(ChargePacket packet) => Channel.Broadcast(ChargeVisualEvent, packet);
    internal static void SendMode(ModePacket packet) { if (SNet.Master != null) Channel.ToHost(ModeEvent, packet); }
    internal static void BroadcastMode(ModePacket packet) => Channel.Broadcast(ModeVisualEvent, packet);
    /// <summary>Host: where a projectile ended, to every other player.</summary>
    internal static void BroadcastFlightEnd(FlightEnd end) { if (Ready && Session.IsHost) Channel.Broadcast(FlightEndEvent, end); }
}

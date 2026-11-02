using System;
using System.Collections.Generic;
using ForgeCore;
using SNetwork;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>Energy Lab's messages through its ForgeCore channel, whose message formats ForgeCore fingerprints, with this build's
/// fingerprint as the channel content: a player with other message formats or another build is refused by ForgeCore's handshake (an error in
/// both logs, a chat line here) and their messages are dropped. The world epoch (the host's, adopted by clients), which player is ready,
/// and dropping messages from another epoch are ForgeCore's channel readiness; this class only decides when the world is up and
/// what each message means.</summary>
internal static class EnergyNetwork
{
    private const string FireEvent = "fire";
    private const string VisualEvent = "visual";
    private const string ChargeEvent = "charge";
    private const string ChargeVisualEvent = "charge-visual";
    private const string ModeEvent = "mode";
    private const string ModeVisualEvent = "mode-visual";
    private enum PacketKind { Fire, Visual, Charge, ChargeVisual, Mode, ModeVisual }
    private struct Inbound
    {
        internal PacketKind Kind;
        internal ulong Sender;
        internal FireCommand Command;
        internal FireVisual Visual;
        internal ChargePacket Charge;
        internal ModePacket Mode;
    }
    private static readonly HashSet<ulong> Mismatched = new();
    private static NetChannel? _channel;
    private static EnergyController? _pumping;
    private static bool _pumpWorld;
    private static bool _worldActive;
    // Raised by the game's host and level-end events: while the world is active and this is clear, nothing about it has changed.
    // A level end is followed by the game leaving the level a moment later; the world is read every frame until it is seen gone.
    private static bool _sessionEvent, _levelEnded;
    private static ulong _master, _generation = (ulong)DateTime.UtcNow.Ticks;
    private static uint _nextSequence;
    internal static ulong Epoch => _channel?.Epoch ?? 0;
    internal static BuildFingerprint Fingerprint { get; private set; }
    internal static bool Ready => Epoch != 0 && (SNet.IsMaster || Channel.HostReady);
    internal static uint NextSequence() { if (++_nextSequence == 0) ++_nextSequence; return _nextSequence; }

    internal static void Register()
    {
        if (_channel != null) return;
        var channel = Net.Open(Plugin.Id, EnergyProtocol.InboxLimit);
        channel.EnableReadiness();
        channel.OnEpoch<FireCommand>(FireEvent, (s, p) => Handle(new() { Kind = PacketKind.Fire, Sender = s, Command = p }));
        channel.OnEpoch<FireVisual>(VisualEvent, (s, p) => Handle(new() { Kind = PacketKind.Visual, Sender = s, Visual = p }));
        channel.OnEpoch<ChargePacket>(ChargeEvent, (s, p) => Handle(new() { Kind = PacketKind.Charge, Sender = s, Charge = p }));
        channel.OnEpoch<ChargePacket>(ChargeVisualEvent, (s, p) => Handle(new() { Kind = PacketKind.ChargeVisual, Sender = s, Charge = p }));
        channel.OnEpoch<ModePacket>(ModeEvent, (s, p) => Handle(new() { Kind = PacketKind.Mode, Sender = s, Mode = p }));
        channel.OnEpoch<ModePacket>(ModeVisualEvent, (s, p) => Handle(new() { Kind = PacketKind.ModeVisual, Sender = s, Mode = p }));
        channel.Refused += Refused;
        channel.EpochChanged += () => _pumping?.SessionChanged();
        channel.PeerSynced += _ => EnergyController.PeerSynced();
        GameEvents.MasterChanged += NoteSessionEvent;
        GameEvents.LevelEnded += () => _levelEnded = true;
        _channel = channel;
    }

    /// <summary>The definitions are final: the build fingerprint becomes the channel's content, which every player must share.</summary>
    internal static void SealDefinitions()
    {
        Fingerprint = BuildFingerprint.Compute();
        Channel.Content = Fingerprint.ToString();
    }

    private static NetChannel Channel => _channel ?? throw new InvalidOperationException("Energy Lab's network channel is not open.");

    internal static bool WorldReady => GameStateManager.CurrentStateName == eGameStateName.InLevel &&
        SNet.MasterManagement != null && !SNet.MasterManagement.IsMigrating && SNet.Master != null;

    private static void NoteSessionEvent() => _sessionEvent = true;

    internal static void Pump(EnergyController controller)
    {
        var world = _worldActive && !_sessionEvent && !_levelEnded || WorldReady;
        _sessionEvent = false;
        if (!world) _levelEnded = false;
        var master = GameEvents.Master;
        if (_worldActive && (!world || master != _master))
        { Reset(); controller.SessionChanged(); }
        if (world && !_worldActive)
        {
            _worldActive = true; _master = master;
            if (SNet.IsMaster) Channel.Epoch = ++_generation;
        }
        // ForgeCore announces when these change and to every player who syncs; nothing repeats on a timer.
        Channel.Live = world && EnergyGearRegistry.Ready;
        Channel.Ready = controller.PresentationReady;
        _pumping = controller;
        _pumpWorld = world;
        try { Channel.Pump(); }
        finally { _pumping = null; }
    }

    private static void Handle(Inbound item)
    {
        var controller = _pumping ?? throw new InvalidOperationException("Energy messages are handled only inside Pump.");
        // Other worlds and authorities were dropped by the channel; this world must be up and presenting.
        if (!_pumpWorld || !controller.PresentationReady) return;
        if (item.Kind == PacketKind.Fire && SNet.IsMaster)
        {
            if (PeerReady(item.Sender)) controller.ReceiveCommand(item.Sender, item.Command);
            return;
        }
        if (item.Kind == PacketKind.Visual && SNet.IsMaster)
        {
            if (PeerReady(item.Sender)) controller.ReceiveNativeVisual(item.Sender, item.Visual);
            return;
        }
        switch (item.Kind)
        {
            case PacketKind.Charge or PacketKind.Mode when SNet.IsMaster && !PeerReady(item.Sender): break;
            case PacketKind.Charge: if (SNet.IsMaster) controller.ReceiveCharge(item.Sender, item.Charge); break;
            case PacketKind.Mode: if (SNet.IsMaster) controller.ReceiveMode(item.Sender, item.Mode); break;
            case PacketKind.ChargeVisual: if (IsMaster(item.Sender)) controller.ReceiveChargeVisual(item.Charge); break;
            case PacketKind.Visual: if (IsMaster(item.Sender)) controller.ReceiveVisual(item.Visual); break;
            case PacketKind.ModeVisual: if (IsMaster(item.Sender)) controller.ReceiveModeVisual(item.Mode); break;
        }
    }

    private static bool IsMaster(ulong sender) => !SNet.IsMaster && SNet.Master?.Lookup == sender;
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
    internal static void Broadcast(FireVisual visual) => Channel.Broadcast(VisualEvent, visual);
    internal static void SendVisual(FireVisual visual)
    {
        if (!SNet.IsMaster && SNet.Master == null) return;
        if (!Ready || visual.Epoch != Epoch) throw new InvalidOperationException("Energy visual session is unavailable.");
        if (SNet.IsMaster) Broadcast(visual);
        else Channel.ToHost(VisualEvent, visual);
    }
    internal static void SendCharge(ChargePacket packet) { if (SNet.Master != null) Channel.ToHost(ChargeEvent, packet); }
    internal static void BroadcastCharge(ChargePacket packet) => Channel.Broadcast(ChargeVisualEvent, packet);
    internal static void SendMode(ModePacket packet) { if (SNet.Master != null) Channel.ToHost(ModeEvent, packet); }
    internal static void BroadcastMode(ModePacket packet) => Channel.Broadcast(ModeVisualEvent, packet);
    internal static void Reset()
    {
        Channel.ResetWorld();
        _worldActive = false;
        Mismatched.Clear();
        // Pump drops queued old epochs; Reset never retargets their payloads.
    }
}
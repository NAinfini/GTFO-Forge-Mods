using System;
using System.Collections.Generic;
using GTFO.API;
using SNetwork;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

internal static class EnergyNetwork
{
    internal const string Protocol = EnergyProtocol.Version;
    private const string Prefix = "forge.weapon-energy-lab.experimental.";
    private const string HelloEvent = Prefix + "hello." + Protocol;
    private const string FireEvent = Prefix + "fire." + Protocol;
    private const string VisualEvent = Prefix + "visual." + Protocol;
    private const string ChargeEvent = Prefix + "charge." + Protocol;
    private const string ChargeVisualEvent = Prefix + "charge-visual." + Protocol;
    private const string ModeEvent = Prefix + "mode." + Protocol;
    private const string ModeVisualEvent = Prefix + "mode-visual." + Protocol;
    private const string BlastForceEvent = Prefix + "blast-force." + Protocol;
    private const SNet_ChannelType Channel = SNet_ChannelType.GameOrderCritical;
    private enum PacketKind { Hello, Fire, Visual, Charge, ChargeVisual, Mode, ModeVisual, BlastForce }
    private struct Inbound
    {
        internal PacketKind Kind;
        internal ulong Sender;
        internal HelloPacket Hello;
        internal FireCommand Command;
        internal FireVisual Visual;
        internal ChargePacket Charge;
        internal ModePacket Mode;
        internal BlastForcePacket BlastForce;
        internal ulong Epoch => Kind switch
        {
            PacketKind.Hello => Hello.Epoch, PacketKind.Fire => Command.Epoch, PacketKind.Visual => Visual.Epoch,
            PacketKind.Charge or PacketKind.ChargeVisual => Charge.Epoch,
            PacketKind.BlastForce => BlastForce.Epoch,
            PacketKind.Mode or PacketKind.ModeVisual => Mode.Epoch, _ => throw new InvalidOperationException("Unknown energy packet kind.")
        };
    }
    private static readonly object Gate = new();
    private static readonly Queue<Inbound> Inbox = new();
    private static readonly Dictionary<ulong, (float At, bool Ready)> Peers = new();
    private static readonly List<ulong> DepartedPeers = new(4);
    private static readonly Dictionary<ulong, ulong> EpochHighWater = new();
    private static readonly HashSet<ulong> Mismatched = new();
    private static bool _registered, _worldActive, _hostReady;
    private static ulong _master, _generation = (ulong)DateTime.UtcNow.Ticks;
    private static float _hostSeenAt = float.NegativeInfinity, _nextHello;
    private static uint _nextSequence;
    private static bool _overflowed;
    internal static ulong Epoch { get; private set; }
    internal static BuildFingerprint Fingerprint { get; private set; }
    internal static bool Ready => Epoch != 0 && (SNet.IsMaster || _hostReady && Time.time - _hostSeenAt < EnergyProtocol.HelloLifetime);
    internal static uint NextSequence() { if (++_nextSequence == 0) ++_nextSequence; return _nextSequence; }

    internal static void Register()
    {
        if (_registered) return;
        NetworkAPI.RegisterEvent<HelloPacket>(HelloEvent, (s, p) => Enqueue(new() { Kind = PacketKind.Hello, Sender = s, Hello = p }));
        NetworkAPI.RegisterEvent<FireCommand>(FireEvent, (s, p) => Enqueue(new() { Kind = PacketKind.Fire, Sender = s, Command = p }));
        NetworkAPI.RegisterEvent<FireVisual>(VisualEvent, (s, p) => Enqueue(new() { Kind = PacketKind.Visual, Sender = s, Visual = p }));
        NetworkAPI.RegisterEvent<ChargePacket>(ChargeEvent, (s, p) => Enqueue(new() { Kind = PacketKind.Charge, Sender = s, Charge = p }));
        NetworkAPI.RegisterEvent<ChargePacket>(ChargeVisualEvent, (s, p) => Enqueue(new() { Kind = PacketKind.ChargeVisual, Sender = s, Charge = p }));
        NetworkAPI.RegisterEvent<ModePacket>(ModeEvent, (s, p) => Enqueue(new() { Kind = PacketKind.Mode, Sender = s, Mode = p }));
        NetworkAPI.RegisterEvent<ModePacket>(ModeVisualEvent, (s, p) => Enqueue(new() { Kind = PacketKind.ModeVisual, Sender = s, Mode = p }));
        NetworkAPI.RegisterEvent<BlastForcePacket>(BlastForceEvent, (s, p) => Enqueue(new() { Kind = PacketKind.BlastForce, Sender = s, BlastForce = p }));
        _registered = true;
    }

    internal static void SealDefinitions() => Fingerprint = BuildFingerprint.Compute();

    private static void Enqueue(Inbound item)
    {
        lock (Gate)
        {
            if (Inbox.Count < EnergyProtocol.InboxLimit) { Inbox.Enqueue(item); return; }
            _overflowed = true;
        }
    }

    internal static bool WorldReady => GameStateManager.CurrentStateName == eGameStateName.InLevel &&
        SNet.MasterManagement != null && !SNet.MasterManagement.IsMigrating && SNet.Master != null;

    internal static void Pump(PrototypeController controller)
    {
        var world = WorldReady;
        var master = SNet.Master?.Lookup ?? 0;
        if (_worldActive && (!world || master != _master))
        { Reset(); controller.SessionChanged(); }
        if (world && !_worldActive)
        {
            _worldActive = true; _master = master;
            if (SNet.IsMaster) Epoch = ++_generation;
        }
        foreach (var peer in Peers.Keys)
            if (!SNet.TryGetPlayer(peer, out var member) || member == null) DepartedPeers.Add(peer);
        foreach (var peer in DepartedPeers) Peers.Remove(peer);
        DepartedPeers.Clear();
        bool overflowed;
        lock (Gate) { overflowed = _overflowed; _overflowed = false; }
        if (overflowed) Plugin.Error("Energy receive budget exhausted; queued overflow was dropped: " +
            new InvalidOperationException("The reliable energy channel exceeded its receive buffer."));
        if (world && EnergyGearRegistry.Ready && Time.time >= _nextHello && (SNet.IsMaster || Epoch != 0))
        {
            var hello = Hello(controller.PresentationReady);
            if (SNet.IsMaster) NetworkAPI.InvokeEvent(HelloEvent, hello, Channel);
            else NetworkAPI.InvokeEvent(HelloEvent, hello, SNet.Master, Channel);
            _nextHello = Time.time + EnergyProtocol.HelloInterval;
        }
        while (true)
        {
            Inbound item;
            lock (Gate) { if (Inbox.Count == 0) break; item = Inbox.Dequeue(); }
            // A Hello before the registry is ready is answered by the next one, a second later.
            if (item.Kind == PacketKind.Hello)
            {
                if (world && EnergyGearRegistry.Ready) ReceiveHello(controller, item.Sender, item.Hello);
                continue;
            }
            // Old worlds and authorities cannot launch or present effects in this world.
            if (!world || item.Epoch == 0 || item.Epoch != Epoch || !controller.PresentationReady) continue;
            if (item.Kind == PacketKind.Fire && SNet.IsMaster)
            {
                if (PeerReady(item.Sender)) controller.ReceiveCommand(item.Sender, item.Command);
                continue;
            }
            if (item.Kind == PacketKind.Visual && SNet.IsMaster)
            {
                if (PeerReady(item.Sender)) controller.ReceiveNativeVisual(item.Sender, item.Visual);
                continue;
            }
            switch (item.Kind)
            {
                case PacketKind.BlastForce:
                    if (SNet.IsMaster && PeerReady(item.Sender))
                        try { EnergyBlastForces.Receive(item.Sender, item.BlastForce); }
                        catch (Exception error) { Plugin.Error("Energy blast force request refused: " + error); }
                    break;
                case PacketKind.Charge or PacketKind.Mode when SNet.IsMaster && !PeerReady(item.Sender): break;
                case PacketKind.Charge: if (SNet.IsMaster) controller.ReceiveCharge(item.Sender, item.Charge); break;
                case PacketKind.Mode: if (SNet.IsMaster) controller.ReceiveMode(item.Sender, item.Mode); break;
                case PacketKind.ChargeVisual: if (IsMaster(item.Sender)) controller.ReceiveChargeVisual(item.Charge); break;
                case PacketKind.Visual: if (IsMaster(item.Sender)) controller.ReceiveVisual(item.Visual); break;
                case PacketKind.ModeVisual: if (IsMaster(item.Sender)) controller.ReceiveModeVisual(item.Mode); break;
            }
        }
        if (SNet.IsMaster) EnergyBlastForces.Tick();
    }

    private static bool IsMaster(ulong sender) => !SNet.IsMaster && SNet.Master?.Lookup == sender;
    private static bool PeerReady(ulong sender) => Peers.TryGetValue(sender, out var peer) && peer.Ready && Time.time - peer.At < EnergyProtocol.HelloLifetime;
    private static HelloPacket Hello(bool ready, bool rejected = false) => new()
    {
        Major = checked((ushort)EnergyProtocol.Number.Major), Minor = checked((ushort)EnergyProtocol.Number.Minor), Patch = checked((ushort)EnergyProtocol.Number.Build),
        Epoch = Epoch, Fingerprint = Fingerprint, Ready = ready ? 1 : 0, Rejected = rejected ? 1 : 0
    };
    private static void ReceiveHello(PrototypeController controller, ulong sender, HelloPacket packet)
    {
        // A player still joining or leaving, or a client's Hello reaching another client, is not a handshake.
        if (!SNet.TryGetPlayer(sender, out var player) || player == null || player.IsLocal || player.IsBot) return;
        if (!SNet.IsMaster && !IsMaster(sender)) return;
        if (packet.Major != EnergyProtocol.Number.Major || packet.Minor != EnergyProtocol.Number.Minor || packet.Patch != EnergyProtocol.Number.Build ||
            !packet.Fingerprint.Equals(Fingerprint) || packet.Rejected != 0)
        {
            Plugin.Error($"Energy Hello from {player.NickName} ({sender}) rejected: " + new InvalidOperationException(
                $"Local fingerprint={Fingerprint}, peer fingerprint={packet.Fingerprint}; protocol local={Protocol}, peer={packet.Major}.{packet.Minor}.{packet.Patch}."));
            if (SNet.IsMaster) Peers.Remove(sender);
            else { _hostReady = false; _hostSeenAt = float.NegativeInfinity; }
            // Answer a plain Hello with a rejection so both sides know; a rejection is never answered, so they cannot loop.
            if (packet.Rejected == 0) NetworkAPI.InvokeEvent(HelloEvent, Hello(false, true), player, Channel);
            NoticeMismatch(player);
            return;
        }
        if (SNet.IsMaster)
        {
            if (packet.Epoch != Epoch) return;
            if (Peers.Count < 4 || Peers.ContainsKey(sender)) Peers[sender] = (Time.time, packet.Ready == 1);
            else Plugin.Error($"Energy Hello from {sender} ignored: " + new InvalidOperationException("Four peers are already tracked."));
            return;
        }
        var highest = EpochHighWater.TryGetValue(sender, out var value) ? value : 0;
        // A Hello from a level this client already left.
        if (packet.Epoch == 0 || packet.Epoch < highest || Epoch == 0 && packet.Epoch == highest) return;
        if (Epoch != packet.Epoch)
        {
            if (EpochHighWater.Count >= 32 && !EpochHighWater.ContainsKey(sender))
            { Plugin.Error("Energy authority epoch history exhausted; unknown authority refused: " +
                new InvalidOperationException("The authority epoch budget is exhausted.")); return; }
            controller.SessionChanged(); Epoch = packet.Epoch;
            EpochHighWater[sender] = Epoch;
        }
        _hostReady = packet.Ready == 1; _hostSeenAt = Time.time;
        NetworkAPI.InvokeEvent(HelloEvent, Hello(controller.PresentationReady), player, Channel);
    }

    // Players cannot read logs: once per level and player, say in chat (on this machine only) why energy guns stay off.
    private static void NoticeMismatch(SNet_Player player)
    {
        if (!Mismatched.Add(player.Lookup)) return;
        try
        {
            PlayerChatManager.PostChatMessageLocaly(SNet.LocalPlayer, $"[Energy Lab] {player.NickName} has a different Energy Lab build or tuning. " +
                "Energy weapons stay off between you until every player uses the same mod files and tuning.");
        }
        catch (Exception error) { Plugin.Error("Energy mismatch chat notice failed: " + error); }
    }

    internal static void Send(FireCommand command)
    {
        if (!Ready || SNet.Master == null || command.Epoch != Epoch) throw new InvalidOperationException("Energy host/session is unavailable.");
        NetworkAPI.InvokeEvent(FireEvent, command, SNet.Master, Channel);
    }
    internal static void Broadcast(FireVisual visual) => NetworkAPI.InvokeEvent(VisualEvent, visual, Channel);
    internal static void SendVisual(FireVisual visual)
    {
        if (!Ready || visual.Epoch != Epoch) throw new InvalidOperationException("Energy visual session is unavailable.");
        if (SNet.IsMaster) Broadcast(visual);
        else NetworkAPI.InvokeEvent(VisualEvent, visual, SNet.Master, Channel);
    }
    internal static void SendBlastForce(BlastForcePacket packet)
    {
        if (!Ready || SNet.IsMaster || SNet.Master == null || packet.Epoch != Epoch || packet.Sequence == 0)
            throw new InvalidOperationException("Energy blast force session is unavailable.");
        NetworkAPI.InvokeEvent(BlastForceEvent, packet, SNet.Master, Channel);
    }
    internal static void SendCharge(ChargePacket packet) => NetworkAPI.InvokeEvent(ChargeEvent, packet, SNet.Master, Channel);
    internal static void BroadcastCharge(ChargePacket packet) => NetworkAPI.InvokeEvent(ChargeVisualEvent, packet, Channel);
    internal static void SendMode(ModePacket packet) => NetworkAPI.InvokeEvent(ModeEvent, packet, SNet.Master, Channel);
    internal static void BroadcastMode(ModePacket packet) => NetworkAPI.InvokeEvent(ModeVisualEvent, packet, Channel);
    internal static void Reset()
    {
        if (_master != 0 && Epoch != 0) EpochHighWater[_master] = Epoch;
        Epoch = 0; _worldActive = _hostReady = false; _hostSeenAt = float.NegativeInfinity;
        _nextHello = 0f; Peers.Clear(); Mismatched.Clear();
        EnergyBlastForces.Clear();
        // Pump drops queued old epochs; Reset never retargets their payloads.
    }
}

using System;
using System.Collections.Generic;
using Enemies;
using ForgeCore;

namespace ForgeReplay;

/// <summary>What the host settled that the clients that also run Forge Replay do not: its hits on enemies, the wake-ups it named the cause of, the hits
/// its machine settled on a player (its own player and the bots), the shots fired on it, the resource packs applied and the resources handed over on it
/// (PLAN 5.7 host records). The host adds a record where it settles one; every 0.1 s (or when the packet is full) the packet goes to each synced client,
/// with nothing sent while nothing happened. A client writes what arrives into its own recording as events 104 to 112, so its statistics read the host's
/// numbers and its rating finds the host's packs, gifts and hurt sources. The host sends a snapshot of what it has settled so far to a client that just
/// synced, after a checkpoint reload or a host change and every <see cref="SnapshotEveryMs"/>, so a client that joined late, started recording late or
/// missed packets is brought level. Everything goes through ForgeCore <see cref="Net"/>: its handshake already tells a client with the same protocol from
/// one without. Main thread only.</summary>
internal static class HostSync
{
    private const uint SendEveryMs = 100, SnapshotEveryMs = 20000;
    private const string DataName = "records";

    private static NetChannel? _net;
    private static readonly HostLink.Writer Out = new();
    private static readonly HashSet<ulong> Known = new(), Fresh = new(), Live = new();
    private static readonly HostTotals[] Totals = new HostTotals[4];
    private static uint _sequence, _openMs, _snapshotMs;
    private static bool _resnap;

    // Client side: the last packet number taken, and what the sync did, for the log line at the end of the level.
    private static uint _lastSequence;
    private static bool _got, _linked;
    private static int _packets, _snapshots, _rejected, _unknownEnemy, _sentPackets, _sentBytes, _sentRecords;

    private static uint Clock => (uint)Environment.TickCount64;

    internal static void Install()
    {
        var net = Net.Open(Plugin.Id);
        net.Content = HostLink.Content((int)ReplayHeader.FormatVersion);
        net.OnBytes(DataName, OnData, NetDelivery.Reliable, HostLink.MaxPacketBytes);
        net.PeerSynced += lookup => { Known.Add(lookup); if (net.IsHost) { Live.Remove(lookup); Fresh.Add(lookup); } };
        _net = net;
        GameEvents.MasterChanged += HostChanged;
        GameEvents.MasterMigrated += HostChanged;
        GameEvents.CheckpointReloaded += () => _resnap = true;
        GameEvents.LevelEnded += Report;
    }

    // ---- Host: add records ---------------------------------------------------------------------------------------------------------

    /// <summary>True when there is someone to send to: this is the host, it records, and a client runs the same protocol.</summary>
    internal static bool Sending => _net is { IsHost: true } && (Live.Count > 0 || Fresh.Count > 0) && Rec.On;

    private static uint Offset()
    {
        var now = Clock;
        if (Out.Empty) _openMs = now;
        return Math.Min(now - _openMs, HostLink.MaxAgeMs);
    }

    internal static void Hit(EnemyAgent enemy, int kind, int slot, int limbType, bool lethal, bool behind, float taken, uint gear, float distanceMetres)
    {
        if (!Sending || (uint)slot > 3 || (uint)kind > 3) return;
        var offset = Offset();
        Out.Hit(new HostHit(enemy.GlobalID, kind, slot, Math.Clamp(limbType, 0, 2), lethal, behind, (uint)Math.Clamp((int)MathF.Round(taken * 10f), 0, (int)HostLink.MaxTakenTenths), gear,
            (uint)Math.Clamp((int)MathF.Round(distanceMetres * 100f), 0, (int)HostLink.MaxDistanceCm), offset));
        _sentRecords++;
        if (Out.Full) Flush();
    }

    internal static void Wake(EnemyAgent enemy, bool propagated, bool heard, int detection, int player, int action, int propagatorEntity)
    {
        if (!Sending) return;
        var offset = Offset();
        Out.Wake(new HostWake(enemy.GlobalID, propagated, heard, detection is >= 0 and < 7 ? detection : 7, Math.Clamp(player, 0, 4), Math.Clamp(action, 0, 15), EnemyRecorder.KeyOf(propagatorEntity, out var propagator), propagator, offset));
        _sentRecords++;
        if (Out.Full) Flush();
    }

    /// <summary>A hit this machine settled on a player: its own player and, on the host, the bots. <paramref name="fromPlayer"/> is the attacking player's
    /// slot (-1 none) and <paramref name="enemyKey"/> an enemy's replicated id (0 none).</summary>
    internal static void Hurt(int slot, int kind, float damage, int fromPlayer, ushort enemyKey)
    {
        if (!Sending || (uint)slot > 3 || (uint)kind > 6 || fromPlayer > 3) return;
        var offset = Offset();
        Out.Hurt(new HostHurt(slot, kind, fromPlayer, enemyKey, (uint)Math.Clamp((int)MathF.Round(damage * 10f), 0, (int)HostLink.MaxPlayerTenths), offset));
        _sentRecords++;
        if (Out.Full) Flush();
    }

    internal static void Supply(int giver, int receiver, int kind)
    {
        if (!Sending || (uint)giver > 3 || (uint)receiver > 3 || (uint)kind > 2) return;
        var offset = Offset();
        Out.Supply(new HostSupply(giver, receiver, kind, offset));
        _sentRecords++;
        if (Out.Full) Flush();
    }

    internal static void Pack(int slot)
    {
        if (!Sending || (uint)slot > 3) return;
        var offset = Offset();
        Out.Pack(new HostPack(slot, offset));
        _sentRecords++;
        if (Out.Full) Flush();
    }

    /// <summary>A shot fired on the host's machine: its own player and the bots. <paramref name="hit"/> when the ray ended on an enemy limb.</summary>
    internal static void Ray(int slot, bool hit)
    {
        if (!Sending || (uint)slot > 3) return;
        var offset = Offset();
        Out.Ray(new HostRay(slot, hit, offset));
        _sentRecords++;
        if (Out.Full) Flush();
    }

    // ---- Host: send ----------------------------------------------------------------------------------------------------------------

    private static void Send(byte[] packet, HashSet<ulong> to)
    {
        var net = _net!;
        foreach (var peer in to) net.SendBytes(DataName, packet, peer);
        _sentPackets++; _sentBytes += packet.Length * to.Count;
    }

    private static void Flush()
    {
        if (Out.Empty) return;
        // The records belong to the hits the clients already know of; a client that is still waiting for its snapshot gets them inside it.
        Send(Out.Seal(++_sequence, Clock), Live);
    }

    private static void Snapshot(HashSet<ulong> to)
    {
        if (to.Count == 0) return;
        PlayerRecorder.SnapshotTotals(Totals);
        Out.Snapshot(15, Totals);
        Send(Out.Seal(++_sequence, Clock), to);
    }

    private static void HostTick(uint now)
    {
        if (!Rec.On) { if (!Out.Empty) Out.Reset(); return; }
        if (_net!.IsHost && (_resnap || now - _snapshotMs >= SnapshotEveryMs))
        {
            _resnap = false; _snapshotMs = now;
            foreach (var peer in Known) if (_net.State(peer) == PeerState.Compatible && !Live.Contains(peer)) Fresh.Add(peer);
            Flush();
            foreach (var peer in Fresh) Live.Add(peer);
            Fresh.Clear();
            Snapshot(Live);
            return;
        }
        if (!Out.Empty && now - _openMs >= SendEveryMs) Flush();
        if (Fresh.Count == 0) return;
        Flush();
        foreach (var peer in Fresh) Live.Add(peer);
        Snapshot(Fresh);
        Fresh.Clear();
    }

    private static void HostChanged()
    {
        Out.Reset(); Live.Clear(); Fresh.Clear();
        _lastSequence = 0; _got = false;
        if (_net == null) return;
        // The new host starts over: a client that had the old host's records is on its own estimate until the new host's snapshot arrives.
        if (_linked && Rec.On) { HostLink.WriteLink(Rec.Begin(), false, 0, default); Rec.Emit(EventKind.HostLink); }
        _linked = false;
        _resnap = true;
    }

    // ---- Per frame ------------------------------------------------------------------------------------------------------------------

    internal static void Tick()
    {
        var net = _net;
        if (net == null) return;
        net.Pump();
        if (net.IsHost) HostTick(Clock);
    }

    /// <summary>A recording starts: nothing of the one before is waited for any more, and every synced client gets a snapshot.</summary>
    internal static void RecordingStarted()
    {
        Out.Reset(); _resnap = true; _snapshotMs = 0; _lastSequence = 0; _got = false; _linked = false;
        _packets = _snapshots = _rejected = _unknownEnemy = _sentPackets = _sentBytes = _sentRecords = 0;
    }

    private static void Report()
    {
        if (_net == null) return;
        if (_net.IsHost) { if (_sentPackets > 0) Plugin.Diagnostic.LogInfo($"[sync] host: {_sentRecords} records in {_sentPackets} packets, {_sentBytes} bytes sent to {Live.Count} client(s)"); }
        else if (_packets > 0 || Known.Count > 0)
            Plugin.Diagnostic.LogInfo($"[sync] client: {_packets} packets ({_snapshots} snapshots), {_rejected} rejected, {_unknownEnemy} records of enemies not in this recording, host records {(_linked ? "in force" : "not in force: statistics are estimated")}");
        _packets = _snapshots = _rejected = _unknownEnemy = _sentPackets = _sentBytes = _sentRecords = 0;
    }

    // ---- Client: receive ------------------------------------------------------------------------------------------------------------

    private static void OnData(ulong sender, byte[] data)
    {
        var net = _net;
        if (net == null || !net.FromHost(sender) || !Rec.On) return;
        try
        {
            if (data.Length >= HostLink.HeaderBytes && _got && HostLink.ReadU32(data, 1) <= _lastSequence) return;
            var sink = Sink;
            sink.Snapshots = 0;
            if (!HostLink.Read(data, sink, out var sequence, out _)) { _rejected++; Net.Drop("replay-sync-malformed"); Plugin.Logger.ErrorOnce("A malformed host record packet was dropped."); return; }
            _got = true; _lastSequence = sequence; _packets++; _snapshots += sink.Snapshots;
            if (_packets == 1) Plugin.Diagnostic.LogInfo("[sync] client: first host packet received");
        }
        catch (Exception e) { Rec.Fail("host-sync", e); }
    }

    private static readonly Receiver Sink = new();

    private sealed class Receiver : HostLink.IRecords
    {
        internal int Snapshots;

        public void OnHit(in HostHit hit)
        {
            var entity = EnemyRecorder.EntityOfGlobal(hit.Key);
            if (entity < 0) { _unknownEnemy++; return; }
            HostLink.WriteHit(Rec.Begin(), entity, hit);
            Rec.Emit(EventKind.HostHit);
        }

        public void OnWake(in HostWake wake)
        {
            var entity = EnemyRecorder.EntityOfGlobal(wake.Key);
            if (entity < 0) { _unknownEnemy++; return; }
            HostLink.WriteWake(Rec.Begin(), entity, wake.HasPropagator ? EnemyRecorder.EntityOfGlobal(wake.PropagatorKey) : -1, wake);
            Rec.Emit(EventKind.HostWake);
        }

        public void OnSnapshot(int mask, ReadOnlySpan<HostTotals> totals)
        {
            Snapshots++;
            if (!_linked) { _linked = true; Plugin.Diagnostic.LogInfo("[sync] client: host snapshot taken, host records in force"); }
            HostLink.WriteLink(Rec.Begin(), true, mask, totals);
            Rec.Emit(EventKind.HostLink);
        }

        public void OnHurt(in HostHurt hurt)
        {
            var entity = hurt.EnemyKey == 0 ? -1 : EnemyRecorder.EntityOfGlobal(hurt.EnemyKey);
            HostLink.WriteHurt(Rec.Begin(), hurt, entity);
            Rec.Emit(EventKind.HostHurt);
        }

        public void OnSupply(in HostSupply supply)
        {
            HostLink.WriteSupply(Rec.Begin(), supply);
            Rec.Emit(EventKind.HostSupply);
        }

        public void OnPack(in HostPack pack)
        {
            HostLink.WritePack(Rec.Begin(), pack);
            Rec.Emit(EventKind.HostPack);
        }

        public void OnRay(in HostRay ray)
        {
            HostLink.WriteRay(Rec.Begin(), ray);
            Rec.Emit(EventKind.HostRay);
        }
    }
}

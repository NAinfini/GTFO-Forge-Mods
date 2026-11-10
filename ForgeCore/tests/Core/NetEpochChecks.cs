using System.Runtime.InteropServices;

namespace ForgeCore.Tests;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Stamped : INetEpoch
{
    public ulong Epoch;
    public uint Value;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
}

internal static class NetEpochChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var hub = new Hub();
        var host = new NetChannel(hub.Join(1), "epoch.mod", 4);
        var client = new NetChannel(hub.Join(2), "epoch.mod", 4);
        var atHost = new List<uint>();
        host.OnEpoch<Stamped>("stamped", (_, m) => atHost.Add(m.Value));
        client.OnEpoch<Stamped>("stamped", (_, _) => { });
        host.EnableReadiness(); client.EnableReadiness();
        var changes = 0;
        client.EpochChanged += () => changes++;
        for (var t = 0; t < 3; t++) { host.Tick(t, true); client.Tick(t, true); }

        host.Epoch = 5;
        client.ToHost("stamped", new Stamped { Epoch = 5, Value = 1 });
        client.ToHost("stamped", new Stamped { Epoch = 4, Value = 2 });
        client.ToHost("stamped", new Stamped { Epoch = 0, Value = 3 });
        host.Pump();
        check(atHost.SequenceEqual(new uint[] { 1 }), "net epoch: only a message of the current epoch reaches the handler, older and zero ones are dropped");
        host.Epoch = 0;
        client.ToHost("stamped", new Stamped { Epoch = 0, Value = 4 });
        host.Pump();
        check(atHost.Count == 1, "net epoch: while the epoch is zero (no world) every message is dropped");

        var clock = 3;
        // Nothing is sent on a timer: the host announces when its epoch, readiness or liveness changes, or on request here (a toggle pair).
        void Announce(NetChannel c) { c.Ready = !c.Ready; c.Ready = !c.Ready; c.Tick(clock++, false); }
        host.Epoch = 10; host.Ready = true; client.Ready = true; host.Live = true; client.Live = true;
        host.Tick(clock++, false);
        client.Pump(); host.Pump();
        check(client.Epoch == 10 && changes == 1 && client.HostReady && host.PeerReady(2), "net readiness: a client adopts the host's epoch and both sides learn the other is ready");
        Announce(host); client.Pump();
        check(changes == 1, "net readiness: the same epoch again is not a change");
        client.Ready = false; client.Tick(clock++, false); host.Pump();
        check(!host.PeerReady(2) && client.HostReady, "net readiness: the host sees the client's current state");

        client.ResetWorld();
        Announce(host); client.Pump();
        check(client.Epoch == 0 && changes == 1, "net readiness: after a world ends, the same epoch from its host is refused");
        host.Epoch = 11; host.Tick(clock++, false); client.Pump();
        check(client.Epoch == 11 && changes == 2, "net readiness: a newer epoch from the host is adopted");

        client.Live = false; host.Epoch = 12; host.Tick(clock++, false); client.Pump();
        check(client.Epoch == 11, "net readiness: a mod that is not live takes no part");
        client.Live = true; client.ToHost("stamped", new Stamped { Epoch = 11, Value = 5 });
        host.Live = false; host.Epoch = 12; host.Ready = true;
        client.Tick(clock++, false); host.Pump();
        check(!host.PeerReady(2), "net readiness: a host that is not live records nothing");

        // A late joiner is told by the sync event alone: no timer, no announce call.
        var lateHub = new Hub();
        var lateHost = new NetChannel(lateHub.Join(1), "late.mod", 4);
        lateHost.EnableReadiness(); lateHost.Live = true; lateHost.Ready = true; lateHost.Epoch = 7;
        var seenSynced = new List<ulong>();
        lateHost.PeerSynced += seenSynced.Add;
        lateHost.Tick(0, true);
        var lateEnd = lateHub.Join(3);
        var lateClient = new NetChannel(lateEnd, "late.mod", 4);
        lateClient.EnableReadiness(); lateClient.Live = true; lateClient.Ready = true;
        var clientSynced = new List<ulong>();
        lateClient.PeerSynced += clientSynced.Add;
        for (var t = 1; t < 5; t++) { lateHost.Tick(t, true); lateClient.Tick(t, true); lateClient.Pump(); lateHost.Pump(); }
        check(lateClient.Epoch == 7 && lateClient.HostReady && lateHost.PeerReady(3) && seenSynced.Contains(3) && clientSynced.Contains(1),
            "net sync: a joiner learns the host's epoch and readiness, and the host the joiner's, from the greeting alone");
        var sent = lateHub.Players[0].Sent.Count + lateEnd.Sent.Count;
        for (var t = 5; t < 60; t++) { lateHost.Tick(t, false); lateClient.Tick(t, false); lateClient.Pump(); lateHost.Pump(); }
        check(lateHub.Players[0].Sent.Count + lateEnd.Sent.Count == sent, "net sync: a quiet minute sends nothing");
        seenSynced.Clear();
        lateHost.Content = "changed"; lateClient.Content = "changed";
        for (var t = 61; t < 65; t++) { lateHost.Tick(t, false); lateClient.Tick(t, false); lateClient.Pump(); lateHost.Pump(); }
        check(seenSynced.Count >= 1, "net sync: a content change syncs the player again");
        FollowWorldChecks(check);
    }

    /// <summary>The channel follows the game's world: the host mints an epoch when the world is ready, and a host change, migration,
    /// checkpoint reload or level end drops it (a client takes the host's next one, never an older one).</summary>
    private static void FollowWorldChecks(Action<bool, string> check)
    {
        GameEvents.Reset();
        var inLevel = false;
        var built = true;
        Net.WorldProbe = () => inLevel;
        var hub = new Hub();
        var host = new NetChannel(hub.Join(1), "follow.mod", 4);
        var client = new NetChannel(hub.Join(2), "follow.mod", 4);
        var atHost = new List<uint>();
        host.OnEpoch<Stamped>("stamped", (_, m) => atHost.Add(m.Value));
        client.OnEpoch<Stamped>("stamped", (_, _) => { });
        var slotNow = 10f;
        var hostSlot = host.PlayerState<Stamped>("doing", () => slotNow);
        var clientSlot = client.PlayerState<Stamped>("doing", () => slotNow);
        host.FollowWorld(() => built); client.FollowWorld(() => built);
        var resets = 0;
        host.WorldReset += () => resets++;
        double clock = 0;
        void Spin()
        {
            for (var i = 0; i < 3; i++) { host.Pump(); client.Pump(); host.Tick(clock++, i == 0); client.Tick(clock++, i == 0); }
        }
        Spin();
        check(host.Epoch == 0 && !host.Live && client.Epoch == 0, "net world: with no world ready nothing is minted and the channel is not live");
        inLevel = true; Spin();
        var first = host.Epoch;
        check(first != 0 && host.Live && client.Live && client.Epoch == first, "net world: the host mints an epoch when the world is ready and the client adopts it");

        client.ToHost("stamped", new Stamped { Epoch = first, Value = 1 });
        GameEvents.NoteMaster(3);
        var second = host.Epoch;
        check(second > first && resets == 1 && client.Epoch == 0, "net world: a host change drops the world; the host mints a higher epoch at once, the client waits for it");
        Spin();
        client.ToHost("stamped", new Stamped { Epoch = first, Value = 2 });
        client.ToHost("stamped", new Stamped { Epoch = second, Value = 3 });
        host.Pump();
        check(client.Epoch == second && atHost.SequenceEqual(new uint[] { 3 }), "net world: the client adopts the new epoch and a message of the old one is refused");

        GameEvents.NoteMasterMigrated(); Spin();
        var third = host.Epoch;
        GameEvents.NoteCheckpointReloaded(); Spin();
        check(third > second && host.Epoch > third && client.Epoch == host.Epoch, "net world: a migration and a checkpoint reload each start a higher epoch");

        built = false; Spin();
        check(!host.Live && host.Epoch != 0, "net world: when the mod's own readiness drops the channel is not live but the world stays");
        built = true;
        GameEvents.NoteLevelEnded();
        check(host.Epoch == 0 && !host.Live && client.Epoch == 0, "net world: a level end drops the world");
        Spin();
        check(host.Epoch == 0, "net world: the world does not restart while the game still reports the ended level");
        inLevel = false; Spin(); inLevel = true; Spin();
        check(host.Epoch > third && client.Epoch == host.Epoch, "net world: once the game left and entered a level the world starts again with a higher epoch");

        // A client whose checkpoint reload comes after it adopted the epoch the host minted for it is not stuck, and the old epoch stays refused.
        var old = host.Epoch;
        host.Restart(); Spin();
        var minted = host.Epoch;
        check(minted > old && client.Epoch == minted, "net world: the client adopted the epoch the host minted for a reload before its own reload event");
        client.Restart(); Spin();
        var seen = atHost.Count;
        client.ToHost("stamped", new Stamped { Epoch = old, Value = 8 });
        client.ToHost("stamped", new Stamped { Epoch = minted, Value = 9 });
        host.Pump();
        check(client.Epoch == minted && atHost.Skip(seen).SequenceEqual(new uint[] { 9 }),
            "net world: a reload event after that adoption leaves the client on the host's epoch, and the older epoch stays refused");

        // A peer between worlds (no epoch yet): a state an earlier world sent never shows, and the current world's waits for the epoch, because it is sent only once.
        client.Restart();
        host.Epoch = old; hostSlot.Publish(new Stamped { Value = 1 }, 7f); host.Epoch = minted;
        client.Pump();
        check(client.Epoch == 0 && !clientSlot.TryGet(1, out _, out _), "player state: with no epoch a stamped state does not show yet");
        Spin();
        check(client.Epoch == minted && !clientSlot.TryGet(1, out _, out _), "player state: a state of an earlier world that came between worlds is dropped");
        client.Restart();
        hostSlot.Publish(new Stamped { Value = 2 }, 7f);
        client.Pump();
        check(client.Epoch == 0 && !clientSlot.TryGet(1, out _, out _), "player state: the current world's state waits while this peer has no epoch");
        slotNow = 20f; Spin();
        check(clientSlot.TryGet(1, out var kept, out var keptStart) && kept.Value == 2 && Math.Abs(keptStart - 7f) < 0.001f, "player state: it goes in when the epoch is taken, as of when it arrived");
        GameEvents.Reset();
        Net.WorldProbe = static () => false;
    }
}

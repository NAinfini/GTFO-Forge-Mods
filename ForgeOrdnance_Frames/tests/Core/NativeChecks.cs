using ForgeOrdnance;
using ForgeOrdnanceFrames;
using GTFO.API;
using Player;
using SNetwork;
using UnityEngine;

internal static class NativeChecks
{
    internal static void Run(Action<bool, string> check, FrameSpec assault, FrameSpec tank)
    {
        FrameNetwork.Register();
        check(ForgeCore.NetChannel.Registered[FrameMessages.FireEvent] == ForgeCore.NetDelivery.Lossy && ForgeCore.NetChannel.Registered[FrameMessages.HitEvent] == ForgeCore.NetDelivery.Lossy
            && new[] { FrameMessages.RequestEvent, FrameMessages.RejectEvent, FrameMessages.RocketRequestEvent, FrameMessages.RocketLaunchEvent, FrameMessages.CheckpointEvent }.All(n => ForgeCore.NetChannel.Registered[n] == ForgeCore.NetDelivery.Reliable),
            "shots and hit directions travel lossy; requests, answers and checkpoints stay reliable");
        FrameWorld.LevelCleanup();
        SNet.LocalPlayer = new() { Lookup = 1 };
        SNet.Master = SNet.LocalPlayer;
        var initialPilot = new PlayerAgent { Owner = new() { Lookup = 7 }, Position = new(30, 0, 0) };
        PlayerManager.PlayerAgentsInLevel = new() { initialPilot };
        Time.time = 10;
        Time.deltaTime = 0;
        Time.frameCount = 10;
        FrameWorld.LevelEntered();
        FrameWorld.Known(assault);
        FrameWorld.Known(tank);
        FrameWorld.HostArrive(new() { SummonerLookup = 1, ThrowId = 10, Parameters = tank });
        var saved = FrameWorld.All.Single();
        saved.Record.State = FrameState.Piloted;
        saved.Record.Pilot = 7;
        saved.Record.Sieged = true;
        saved.Record.Armor = 65;
        saved.Record.Magazine = 11;
        FrameWorld.Tick();
        check(ReferenceEquals(saved.HostPilot, initialPilot), "host suit caches its resolved pilot agent");
        check(ForgeCore.PlayerNoise.Held.Count == 1 && ReferenceEquals(ForgeCore.PlayerNoise.Held[0].Player, initialPilot) && ForgeCore.PlayerNoise.Held[0].Multiplier == FrameNoise.PilotMultiplier && FrameNoise.PilotMultiplier == 2f, "the pilot's movement noise is registered at twice an ordinary player's");
        var replacementPilot = new PlayerAgent { Owner = new() { Lookup = 7 }, Position = new(30, 0, 0) };
        PlayerManager.PlayerAgentsInLevel![0] = replacementPilot;
        FrameWorld.Tick();
        check(ReferenceEquals(saved.HostPilot, replacementPilot), "host suit detects a replaced player-list slot and re-resolves the pilot");
        check(ForgeCore.PlayerNoise.Held.Count == 1 && ReferenceEquals(ForgeCore.PlayerNoise.Held[0].Player, replacementPilot), "a replaced pilot agent moves the one lease to the new agent");
        PlayerManager.PlayerAgentsInLevel!.Insert(0, new PlayerAgent { Owner = new() { Lookup = 9 } });
        FrameWorld.Tick();
        check(ReferenceEquals(saved.HostPilot, replacementPilot) && saved.HostPilotIndex == 1, "host suit re-resolves its cached pilot after the player list is reordered");
        replacementPilot.WasCollected = true;
        var refreshedPilot = new PlayerAgent { Owner = new() { Lookup = 7 }, Position = new(30, 0, 0) };
        PlayerManager.PlayerAgentsInLevel!.Add(refreshedPilot);
        FrameWorld.Tick();
        check(ReferenceEquals(saved.HostPilot, refreshedPilot) && saved.HostPilotIndex == 2, "host suit replaces a collected cached pilot with the current agent");
        FrameWorld.MigrationCompleted();
        FrameWorld.LateTick();
        check(saved.HostPilot == null && saved.HostPilotIndex == -1, "authority migration invalidates the host pilot cache");
        check(ForgeCore.PlayerNoise.Held.Count == 0, "no pilot, no noise multiplier");
        FrameWorld.Tick();
        check(ReferenceEquals(saved.HostPilot, refreshedPilot), "host suit re-resolves its pilot after migration invalidation");
        NetworkAPI.Sent.Clear();
        FrameWorld.CheckpointReached();
        var checkpoint = NetworkAPI.Sent.Where(e => e.Event == FrameMessages.CheckpointEvent).Select(e => (FrameCheckpointMessage)e.Message).ToArray();
        check(checkpoint.Length == 3 && checkpoint[1].State.X == 30, "native save broadcasts begin/entry/commit with the current pilot position");
        check(checkpoint[1].State.Flags == FrameStateMessage.FlagShieldUp, "native save retains the full tank shield flag and clears siege");
        saved.Record.Sequence = 100;
        FrameWorld.HostDamage(saved, refreshedPilot, 1000, null);
        check(saved.HostPilot == null, "wrecking a suit invalidates its host pilot cache");
        check(ForgeCore.PlayerNoise.Held.Count == 0, "a wrecked suit ends its pilot's noise multiplier");
        var destroyedSequence = saved.Record.Sequence;
        Time.time += FrameRules.WreckSeconds;
        FrameWorld.Tick();
        check(FrameWorld.All.Count == 0, "saved suit is really removed after its wreck expires");
        FrameWorld.HostArrive(new() { SummonerLookup = 1, ThrowId = 11, Parameters = assault });
        FrameRockets.Flying = 4;
        // A queued launch must not recreate a salvo after recovery cleared the in-flight rockets.
        ((Action<ulong, FrameRocketMessage>)NetworkAPI.Handlers[FrameMessages.RocketLaunchEvent])(1, new() { Thrower = 1, ThrowId = 10 });
        FrameWorld.CheckpointReloaded();
        FrameWorld.Tick();
        var recovered = FrameWorld.All.Single();
        check(recovered.Record.ThrowId == 10 && recovered.Record.Armor == 65 && recovered.Record.Magazine == 11, "native recovery recreates the destroyed saved suit and removes the post-save suit");
        check(recovered.Record.Sequence > destroyedSequence, "recreated suit supersedes the removed wreck's latest sequence");
        check(recovered.Record.State == FrameState.Idle && recovered.Record.Pilot == 0 && recovered.Record.ShieldUp && !recovered.Record.Sieged, "native recovery restores an unoccupied tank with its barrier intact");
        check(recovered.Visual!.Replayed, "native recovery initializes the restored visual without historical effects");
        check(FrameRockets.Flying == 0, "native recovery cancels both in-flight rockets and queued launches");
        OrdnanceApi.Retained.Clear();
        FrameWorld.LateTick();
        check(OrdnanceApi.Retained.Count == 0, "recovery publication waits for the framework Update");
        Time.frameCount++;
        FrameWorld.LateTick();
        check(OrdnanceApi.Retained.ContainsKey((1, 10)) && !OrdnanceApi.Retained.ContainsKey((1, 11)), "recovery retains exactly the saved population for late joiners");

        FrameWorld.LevelCleanup();
        SNet.LocalPlayer = new() { Lookup = 2 };
        FrameWorld.LevelEntered();
        FrameWorld.OnData(new() { SummonerLookup = 1, ThrowId = 10, Replayed = true, Data = FrameMessages.ToBytes(checkpoint[1].State) });
        foreach (var m in checkpoint) FrameWorld.OnCheckpoint(99, m);
        Plugin.ThrowOnError = false;
        FrameWorld.CheckpointReloaded();
        Plugin.ThrowOnError = true;
        check(FrameWorld.All.Count == 1, "forged checkpoint data cannot clear suits while the client's snapshot is unknown");
        foreach (var m in checkpoint) FrameWorld.OnCheckpoint(1, m);
        FrameWorld.CheckpointReloaded();
        check(FrameWorld.All.Count == 1 && FrameWorld.All[0].Record.Armor == 65, "peer recovers from the host-replicated checkpoint, without the original host's memory");
        FrameWorld.LateTick();
        Time.frameCount++;
        NetworkAPI.Sent.Clear();
        FrameWorld.LateTick();
        check(NetworkAPI.Sent.Any(e => e.To == 1 && e.Message is FrameCheckpointMessage m && m.Kind == FrameCheckpointMessage.Request), "late joining peer requests the checkpoint from the current host");
        var idleMirror = new FrameRecord(1, 20, assault) { State = FrameState.Idle, Sequence = 3, Authority = 1 };
        FrameWorld.OnData(new() { SummonerLookup = 1, ThrowId = 20, Replayed = true, Data = FrameMessages.ToBytes(FrameStateMessage.From(idleMirror, Time.time, FrameEjection.None)) });
        SNet.Master = SNet.LocalPlayer;
        FrameWorld.AuthorityChanged(); // ForgeCore's MasterChanged, which the plugin forwards
        FrameWorld.LateTick();
        // Simulate the framework clearing retained states during the next Update.
        OrdnanceApi.Retained.Clear();
        Time.frameCount++;
        FrameWorld.LateTick();
        check(OrdnanceApi.Retained.Count == 2 && OrdnanceApi.Retained.ContainsKey((1, 10)) && OrdnanceApi.Retained.ContainsKey((1, 20)), "new host republishes every unchanged idle suit after the framework migration reset");
        FrameWorld.CheckpointReloaded();
        check(FrameWorld.All.Count == 1 && FrameWorld.All[0].Record.ThrowId == 10, "new host retains the old host's checkpoint");
        Time.frameCount++;
        FrameWorld.LateTick();
        FrameWorld.MigrationCompleted();
        FrameWorld.LateTick();
        OrdnanceApi.Retained.Clear();
        Time.frameCount++;
        FrameWorld.LateTick();
        check(OrdnanceApi.Retained.Count == 1, "same-identity migration also republishes idle suits");
        NetworkAPI.Sent.Clear();
        FrameWorld.OnCheckpoint(3, new() { Kind = FrameCheckpointMessage.Request });
        check(NetworkAPI.Sent.Count == 3 && NetworkAPI.Sent.All(e => e.To == 3), "new host sends its saved snapshot to a late peer");

        var unit = FrameWorld.All.Single();
        unit.Record.State = FrameState.Piloted;
        unit.Record.Pilot = 7;
        unit.Record.X = 0; // Deliberately stale on this peer.
        var enemy = new Enemies.EnemyAgent { Position = new(40, 1, 0) };
        check(FrameWorld.ShelterFor(new(30, 1, 0), enemy) == unit, "host shelter follows the current pilot, ignoring stale record coordinates");
        check(FrameWorld.ShelterFor(new(0, 1, 0), enemy) == null, "old dome location provides no protection");
        check(FrameWorld.ShelterFor(new(30, 1, 0), new PlayerAgent { Position = enemy.Position }) == null, "teammate friendly fire is never dome-protected");
        check(FrameWorld.ShelterFor(new(30, 1, 0), null) == null, "environmental damage is never dome-protected");
        SNet.Master = new() { Lookup = 1 };
        check(FrameWorld.ShelterFor(new(30, 1, 0), enemy) == unit, "client shelter also follows the current pilot");
        SNet.Master = SNet.LocalPlayer;
        LocalPilot.Refusals = 0;
        FrameWorld.OnReject(1, default);
        check(LocalPilot.Refusals == 0, "host rejects a forged refusal from a client");
        FrameNetwork.Reject(2, default);
        check(LocalPilot.Refusals == 1, "host's direct refusal dispatch carries the real host sender");
        FrameRockets.Launches = 0;
        FrameWorld.OnRocketLaunch(1, new() { Thrower = 1, ThrowId = 10 });
        check(FrameRockets.Launches == 0, "host rejects a forged rocket grant");
        FrameNetwork.RocketLaunch(new() { Thrower = 1, ThrowId = 10 });
        check(FrameRockets.Launches == 1, "host's direct rocket dispatch passes the same sender check exactly once");
        SNet.Master = new() { Lookup = 1 };
        FrameWorld.OnReject(3, default);
        check(LocalPilot.Refusals == 1, "client rejects a forged refusal too");
        FrameWorld.OnReject(1, default);
        check(LocalPilot.Refusals == 2, "client accepts the current host's refusal");

        FrameWorld.LevelCleanup();
    }
}

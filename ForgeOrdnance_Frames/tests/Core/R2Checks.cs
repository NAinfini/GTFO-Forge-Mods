using ForgeOrdnance;
using ForgeOrdnanceFrames;
using GTFO.API;
using Player;
using SNetwork;
using UnityEngine;

internal static class R2Checks
{
    internal static void Run(Action<bool, string> check, FrameSpec assault, FrameSpec tank)
    {
        var combined = assault with { Shield = tank.Shield, Rockets = new(8, 40, 4, 1.5f, 1, 300, 5, new RocketLauncherSpec("pod", "round", "chest", 0.3f, 1.9f, 0f, 0f, 0f, 0f)), Siege = new(1.2f, 2, 0.4f, 1f, 1f, 1f) };
        var record = new FrameRecord(1, 1, combined) { State = FrameState.Piloted, Pilot = 7, Authority = 1, Sequence = 10 };
        FrameRules.Boost(record, 10);
        FrameRules.Rockets(record, 7, 10);
        FrameRules.ShieldHit(record, 5, 10);
        var mirror = new FrameRecord(1, 1, combined);
        var state = FrameStateMessage.From(record, 10, FrameEjection.None);
        state.ApplyTo(mirror, 100);
        check(FrameRules.CanBoost(mirror, 7, 104) == FrameRejection.Cooling && FrameRules.CanBoost(mirror, 7, 113) == FrameRejection.None,
            "replication preserves the boost cooldown on another game clock");
        check(FrameRules.Rockets(mirror, 7, 100.9f) == FrameRejection.Cooling && FrameRules.Rockets(mirror, 7, 101) == FrameRejection.None,
            "replication preserves rocket admission cooldown");
        check(!FrameRules.RegenShield(mirror, 105.9f, 0.1f) && FrameRules.RegenShield(mirror, 106, 0.1f),
            "replication preserves the barrier quiet time");
        var gun = new FrameGunReading(true, 30, 60, false);
        var hud = new FrameHudModel(LangChecks.Words()); hud.Update(mirror, gun, 0, false, 104, 0, 0);
        check(!hud.SpecialActive && Math.Abs(hud.SpecialRemaining - 9f) < 1e-3f,"mirrored boost HUD remains cooling after the active charge ends");

        var snapshot = new FrameCheckpoint();
        snapshot.Capture(new[] { record }, 10);
        var restored = snapshot.Restore(new Dictionary<ulong, FrameSpec> { [record.SpecKey] = combined }, 200, 20).Single();
        check(restored.BoostReadyAt == 213 && restored.RocketsReadyAt == 201 && restored.ShieldLastHit == 200,
            "checkpoint restores remaining special and barrier cooldowns");
        check(!restored.Boosting(200) && !restored.Sieged && restored.SiegeDeployEndsAt == 0,
            "checkpoint cancels active modes without resetting cooldowns");
        FrameRules.ToggleSiege(mirror, 110);
        check(!FrameRules.Tick(mirror, 111.19f, out _) && !mirror.Sieged, "siege bonuses are withheld throughout DeploySeconds");
        var deploying = FrameStateMessage.From(mirror, 111, FrameEjection.None);
        var deployingPeer = new FrameRecord(1, 1, combined);
        deploying.ApplyTo(deployingPeer, 500);
        check(!deployingPeer.Sieged && Math.Abs(deployingPeer.SiegeDeployEndsAt - 500.2f) < 0.001f,
            "migration preserves remaining siege deployment time");
        hud.Update(mirror, gun, 0, false, 111, 0, 0); // Boost has priority on a combined definition; use a siege-only definition below.
        var sieger = combined with { Boost = null };
        var siegeRecord = new FrameRecord(1, 2, sieger) { State = FrameState.Piloted, Pilot = 7 };
        FrameRules.ToggleSiege(siegeRecord, 10);
        hud.Update(siegeRecord, gun, 0, false, 10, 0, 0);
        check(hud.Special == FrameSpecialKind.Siege && hud.SpecialProgress >= 0f && hud.SpecialProgress < 1f && !hud.SpecialActive, "siege HUD exposes the real deployment stage");
        FrameRules.ToggleSiege(siegeRecord, 10.5f);
        FrameRules.Tick(siegeRecord, 12, out _);
        check(!siegeRecord.Sieged && FrameRules.SpeedMultiplier(siegeRecord, 12) > 0, "cancelled deployment cannot complete later");
        FrameRules.ToggleSiege(siegeRecord, 20);
        FrameRules.Exit(siegeRecord, 20.5f, 0, 0, 0, 0);
        FrameRules.Tick(siegeRecord, 30, out _);
        check(!siegeRecord.Sieged && siegeRecord.SiegeDeployEndsAt == 0, "exiting cancels a pending deployment");
        var instant = new FrameRecord(1, 3, sieger with { Siege = sieger.Siege! with { DeploySeconds = 0 } }) { State = FrameState.Piloted, Pilot = 7 };
        FrameRules.ToggleSiege(instant, 0);
        check(instant.Sieged && instant.SiegeDeployEndsAt == 0, "zero-second siege deploys immediately even at clock zero");

        Reset(2, 1);
        FrameWorld.Known(assault);
        var oldHost = new FrameRecord(1, 10, assault) { Authority = 1, Sequence = 10, State = FrameState.Entering, Pilot = 2 };
        Data(oldHost);
        SNet.Master = new() { Lookup = 3 };
        var newHost = new FrameRecord(1, 10, assault) { Authority = 3, Sequence = 9, State = FrameState.Idle };
        Data(newHost);
        check(FrameWorld.All.Single().Record.State == FrameState.Idle && !FrameWorld.All.Single().Record.HasPilot,
            "lower sequence from a new host releases a stale boarding lock");
        Data(oldHost);
        check(FrameWorld.All.Single().Record.State == FrameState.Idle, "old-host packets cannot undo a migrated state");
        newHost.State = FrameState.Entering; Data(newHost);
        check(FrameWorld.All.Single().Record.State == FrameState.Idle, "duplicate sequences remain rejected within the new authority");

        Reset(1, 1);
        OrdnanceApi.Capacity = 0;
        FrameWorld.HostArrive(new() { SummonerLookup = 1, ThrowId = 20, Parameters = assault });
        var idle = FrameWorld.All.Single(); idle.Record.State = FrameState.Idle;
        check(idle.RetainPending && OrdnanceApi.Retained.Count == 0, "retention refusal stays pending for an idle suit");
        Time.time += 1; FrameWorld.Tick();
        OrdnanceApi.Capacity = 256;
        Time.time += 1; FrameWorld.Tick();
        check(!idle.RetainPending && OrdnanceApi.Retained.ContainsKey((1, 20)), "idle suit retries and becomes replayable after capacity frees");
        idle.Record.State = FrameState.Piloted; idle.Record.Pilot = 1;
        LocalPilot.Unit = idle; LocalPilot.Ejections = 0;
        FrameWorld.HostDamage(idle, PlayerManager.PlayerAgentsInLevel![0], 1000, null);
        check(LocalPilot.Ejections == 1, "host pilot receives the same destroyed notification as a remote pilot");
        idle.Record.State = FrameState.Piloted; idle.Record.Pilot = 1;
        PlayerManager.PlayerAgentsInLevel!.Clear(); FrameWorld.Tick();
        check(LocalPilot.Ejections == 2, "host pilot also receives a forced-out notification");
        LocalPilot.Unit = null;

        Reset(1, 1);
        FrameWorld.HostArrive(new() { SummonerLookup = 1, ThrowId = 30, Parameters = combined });
        var rocketUnit = FrameWorld.All.Single(); rocketUnit.Record.State = FrameState.Piloted; rocketUnit.Record.Pilot = 1;
        FrameRockets.Launches = 0;
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            FrameWorld.OnRocketRequest(1, new() { Thrower = 1, ThrowId = 30, TargetX = invalid });
        check(FrameRockets.Launches == 0 && rocketUnit.Record.RocketsReadyAt == 0, "invalid float targets are rejected before cooldown or flight allocation");
        FrameWorld.OnRocketRequest(1, new() { Thrower = 1, ThrowId = 30, TargetX = 20 });
        check(FrameRockets.Launches == 1 && FrameMessages.TryRead(OrdnanceApi.Retained[(1, 30)], out FrameStateMessage rocketState)
            && rocketState.RocketsCooldownRemaining == 1, "granted salvo immediately replicates its authoritative cooldown");

        // Deliver a checkpoint larger than the whole normal inbox after saturating it with cosmetic traffic.
        var bulk = new FrameCheckpoint();
        bulk.Capture(Enumerable.Range(0, 300).Select(i => new FrameRecord(1, (uint)(100 + i), assault) { State = FrameState.Idle }), 0);
        var messages = bulk.Messages().ToArray();
        Reset(2, 1); FrameWorld.Known(assault);
        var fire = (Action<ulong, FrameFireMessage>)NetworkAPI.Handlers[FrameMessages.FireEvent];
        var checkpoint = (Action<ulong, FrameCheckpointMessage>)NetworkAPI.Handlers[FrameMessages.CheckpointEvent];
        for (var i = 0; i < 256; i++) fire(1, default);
        foreach (var message in messages) checkpoint(1, message);
        FrameNetwork.Pump();
        FrameWorld.CheckpointReloaded();
        check(FrameWorld.All.Count == 300, "checkpoint survives a saturated inbox and a transaction exceeding 256 entries");

        Reset(2, 1); FrameWorld.Known(assault);
        Data(oldHost);
        FrameWorld.OnCheckpoint(1, messages[0]);
        FrameWorld.OnCheckpoint(1, messages[1]);
        FrameWorld.OnCheckpoint(1, messages[^1]);
        Plugin.ThrowOnError = false;
        FrameWorld.CheckpointReloaded();
        Plugin.ThrowOnError = true;
        check(FrameWorld.All.Count == 1, "an incomplete checkpoint defers recovery and preserves current suits");
        NetworkAPI.Sent.Clear(); FrameWorld.Tick();
        check(NetworkAPI.Sent.Any(e => e.To == 1 && e.Message is FrameCheckpointMessage m && m.Kind == FrameCheckpointMessage.Request),
            "incomplete transaction triggers a resynchronization request");
        NetworkAPI.Sent.Clear(); Time.time += 20f; FrameWorld.Tick();
        check(!NetworkAPI.Sent.Any(e => e.Message is FrameCheckpointMessage),
            "a checkpoint request is not repeated on a timer");
        ForgeCore.NetChannel.Last!.Sync(1); FrameWorld.Tick();
        check(NetworkAPI.Sent.Any(e => e.To == 1 && e.Message is FrameCheckpointMessage m && m.Kind == FrameCheckpointMessage.Request),
            "the host's channel syncing asks again, so a lost request leaves no permanent unknown snapshot");
        SNet.Master = SNet.LocalPlayer;
        FrameWorld.AuthorityChanged();
        FrameWorld.LateTick(); Time.frameCount++; NetworkAPI.Sent.Clear(); FrameWorld.LateTick();
        check(!NetworkAPI.Sent.Any(e => e.Message is FrameCheckpointMessage m && m.Kind == FrameCheckpointMessage.Begin),
            "unready new host cannot publish an empty or stale authoritative checkpoint");
        NetworkAPI.Sent.Clear(); FrameWorld.Tick();
        check(NetworkAPI.Sent.Any(e => e.To == 0 && e.Message is FrameCheckpointMessage m && m.Kind == FrameCheckpointMessage.Request),
            "unready new host asks surviving peers for the missing checkpoint");
        foreach (var message in messages) FrameWorld.OnCheckpoint(3, message);
        FrameWorld.Tick();
        check(FrameWorld.All.Count == 300, "new host completes deferred recovery from a surviving peer's complete snapshot");
        NetworkAPI.Sent.Clear(); ForgeCore.NetChannel.Last!.Sync(7);
        check(NetworkAPI.Sent.Any(e => e.To == 7 && e.Message is FrameCheckpointMessage m && m.Kind == FrameCheckpointMessage.Begin),
            "a host whose channel syncs with a player sends that player the checkpoint");

        // Lose the single terminal transition, then deliver the next real host publication.
        Reset(1, 1);
        FrameWorld.HostArrive(new() { SummonerLookup = 1, ThrowId = 50, Parameters = assault });
        var transition = FrameWorld.All.Single().Record;
        transition.State = FrameState.Entering; transition.Pilot = 2; transition.StateEndsAt = Time.time + 1;
        var delayed = new FrameRecord(1, 50, assault);
        FrameStateMessage.From(transition, Time.time, FrameEjection.None).ApplyTo(delayed, Time.time);
        PlayerManager.PlayerAgentsInLevel!.Add(new() { Owner = new() { Lookup = 2 } });
        // A peer that missed a transition gets the newest state from the retained store when its channel syncs; the host does not repeat it on a timer.
        Time.time += 1.1f; FrameWorld.Tick();
        check(FrameMessages.TryRead(OrdnanceApi.Retained[(1, 50)], out FrameStateMessage refreshed)
            && refreshed.ApplyTo(delayed, Time.time) && delayed.State == FrameState.Piloted,
            "the retained state holds the boarding-complete state a peer replays on joining");
        FrameRules.Exit(transition, Time.time, 0, 0, 0, 0);
        FrameStateMessage.From(transition, Time.time, FrameEjection.None).ApplyTo(delayed, Time.time);
        Time.time += FrameSequence.ExitSeconds + 0.1f; FrameWorld.Tick();
        check(FrameMessages.TryRead(OrdnanceApi.Retained[(1, 50)], out refreshed) && refreshed.ApplyTo(delayed, Time.time)
            && delayed.State == FrameState.Idle && !delayed.HasPilot,
            "the retained state holds the exit-complete state and releases pilot ownership");
        OrdnanceApi.Retained.Clear(); NetworkAPI.Sent.Clear(); Time.time += 20f; FrameWorld.Tick();
        check(OrdnanceApi.Retained.Count == 0 && NetworkAPI.Sent.Count == 0, "an unchanged suit sends nothing on a timer");
        // The game moves or holds the pilot (a snatcher's grab and its warp, a dimension change, a teleport).
        Reset(1, 1);
        FrameWorld.HostArrive(new() { SummonerLookup = 1, ThrowId = 70, Parameters = assault });
        var taken = FrameWorld.All.Single(); taken.Record.State = FrameState.Idle;
        var pilot = PlayerManager.PlayerAgentsInLevel![0];
        pilot.Locomotion = new PlayerLocomotion { m_currentStateEnum = PlayerLocomotion.PLOC_State.GrabbedByPouncer };
        LocalPilot.Unit = taken; LocalPilot.Refusals = LocalPilot.Ejections = 0;
        FrameWorld.OnRequest(1, new() { Thrower = 1, ThrowId = 70, Kind = (byte)FrameRequestKind.Enter });
        check(taken.Record.State == FrameState.Idle && LocalPilot.Refusals == 1 && LocalPilot.LastRefusal == FrameRejection.Seized,
            "a player the game holds cannot board");
        pilot.Locomotion.m_currentStateEnum = PlayerLocomotion.PLOC_State.Normal;
        FrameWorld.OnRequest(1, new() { Thrower = 1, ThrowId = 70, Kind = (byte)FrameRequestKind.Enter });
        pilot.Position = new Vector3(taken.Record.X + 200f, taken.Record.Y, taken.Record.Z);
        FrameWorld.Tick();
        check(taken.Record.State == FrameState.Idle && !taken.Record.HasPilot && taken.Record.X == 0f
            && LocalPilot.Ejections == 1 && LocalPilot.LastEjection == FrameEjection.PilotTaken,
            "a pilot warped away mid-boarding ends the boarding; the suit stays empty where it stood");
        pilot.Position = new Vector3(0f, 0f, 0f);
        FrameWorld.OnRequest(1, new() { Thrower = 1, ThrowId = 70, Kind = (byte)FrameRequestKind.Enter });
        pilot.Locomotion.m_currentStateEnum = PlayerLocomotion.PLOC_State.StandStill;
        FrameWorld.Tick();
        check(taken.Record.State == FrameState.Idle && LocalPilot.LastEjection == FrameEjection.PilotTaken,
            "a pilot whose controls the game takes mid-boarding is let go the same way");
        pilot.Locomotion.m_currentStateEnum = PlayerLocomotion.PLOC_State.Normal;
        FrameWorld.OnRequest(1, new() { Thrower = 1, ThrowId = 70, Kind = (byte)FrameRequestKind.Enter });
        Time.time += FrameSequence.EnterSeconds + 0.1f; FrameWorld.Tick();
        pilot.Locomotion.m_currentStateEnum = PlayerLocomotion.PLOC_State.GrabbedByPouncer;
        pilot.Position = new Vector3(500f, -40f, 0f); pilot.DimensionIndex = eDimensionIndex.Dimension1;
        FrameWorld.Tick();
        check(taken.Record.State == FrameState.Piloted && taken.Record.Pilot == 1 && taken.Record.X == 500f && taken.Record.Y == -40f,
            "a worn suit goes wherever the game puts its pilot (a snatcher's warp included)");
        LocalPilot.Refusals = 0;
        FrameWorld.OnRequest(1, new() { Thrower = 1, ThrowId = 70, Kind = (byte)FrameRequestKind.Exit });
        check(taken.Record.State == FrameState.Piloted && LocalPilot.Refusals == 1 && LocalPilot.LastRefusal == FrameRejection.Seized,
            "a held pilot cannot leave the suit until the game lets go");
        pilot.Locomotion.m_currentStateEnum = PlayerLocomotion.PLOC_State.Normal;
        FrameWorld.OnRequest(1, new() { Thrower = 1, ThrowId = 70, Kind = (byte)FrameRequestKind.Exit });
        check(taken.Record.State == FrameState.Exiting && taken.Record.X == 500f, "once let go the pilot leaves where it now stands");
        LocalPilot.Unit = null;

        const string payloadJson = "{\"Model\":\"Frames:frame-m15\",\"Armor\":100,\"Weapon\":{\"Gear\":38,\"Magazine\":30}}";
        var payload = new FramesPayload();
        using var good = System.Text.Json.JsonDocument.Parse(payloadJson);
        check(payload.Parse(good.RootElement) is FrameSpec, "payload admits a model the registered pack lists");
        using var bad = System.Text.Json.JsonDocument.Parse(payloadJson.Replace("frame-m15", "frame-typo"));
        var rejected = false;
        try { payload.Parse(bad.RootElement); }
        catch (ForgeCore.StrictJsonException error) { rejected = error.Field == "Model"; }
        check(rejected, "missing model fails definition admission before any chargeable call exists");
        foreach (var model in new[] { "unskinned", "absent", "no-chest" })
        {
            using var invalidArt = System.Text.Json.JsonDocument.Parse(payloadJson.Replace("Frames:frame-m15", "Frames:" + model));
            rejected = false;
            try { payload.Parse(invalidArt.RootElement); }
            catch (ForgeCore.StrictJsonException error) { rejected = error.Field == "Model"; }
            check(rejected, "an unlisted or unusable prefab fails admission before any chargeable call exists: " + model);
        }
        var errorsBefore = Plugin.Errors.Count;
        check(payload.Parse(System.Text.Json.JsonDocument.Parse(payloadJson.Replace("Frames:", "Other:")).RootElement) is FrameSpec, "a model of another mod\u0027s registered pack is admitted");
        check(Plugin.Errors.Count == errorsBefore && FrameHudArt.UiLog.Count == 0,
            "admission looks up no mark: a third-party icon pack may not have registered yet, so nothing is reported");
        FrameHudArt.Settle();
        FrameHudArt.Settle();
        payload.Parse(System.Text.Json.JsonDocument.Parse(payloadJson.Replace("Frames:", "Other:")).RootElement);
        check(Plugin.Errors.Count == errorsBefore && FrameHudArt.UiLog.Count == 1 && FrameHudArt.UiLog[0].Contains("Other:mark-frame-m15"),
            "once every pack is registered a missing mark is one line (ForgeUI's, written once per icon) and Frames writes none");
        payload.Parse(good.RootElement);
        check(Plugin.Errors.Count == errorsBefore && FrameHudArt.UiLog.Count == 1, "a model with its mark writes no error");
        FrameHudArt.Unsettle();
        OrdnanceApi.Loaded[0xBEEFu] = new OrdnanceDefinition { Key = 0xBEEFu, Name = "Walker" };
        check(FrameWorld.DefinitionName(0xBEEFu) == "WALKER" && FrameWorld.DefinitionName(0xBEF0u) == null,
            "a peer that never saw a call (client, late joiner, reloaded checkpoint) names the suit from its record's definition key");
        OrdnanceApi.Loaded.Clear();
        foreach (var model in new[] { "Nowhere:frame-m15", "frame-m15" })
        {
            using var wrongPack = System.Text.Json.JsonDocument.Parse(payloadJson.Replace("Frames:frame-m15", model));
            rejected = false;
            try { payload.Parse(wrongPack.RootElement); }
            catch (ForgeCore.StrictJsonException error) { rejected = error.Field == "Model"; }
            check(rejected, "a model of an unregistered pack, or without a pack, fails admission on Model: " + model);
        }
        string Rocketeer(string pod, string round) => payloadJson.Replace("\"Armor\":100", "\"Armor\":100,\"Rockets\":{\"Damage\":40,\"Launcher\":{\"Pod\":\"" + pod
            + "\",\"Round\":\"" + round + "\",\"Bone\":\"chest\",\"Position\":[0.3,1.9,0],\"Rotation\":[0,0,0]}}");
        using var armed = System.Text.Json.JsonDocument.Parse(Rocketeer("Frames:pod", "Frames:round"));
        check(payload.Parse(armed.RootElement) is FrameSpec { Rockets: not null }, "a rocket suit is admitted when its launcher has a tube and its rocket is in the bundle");
        foreach (var (pod, round, field) in new[] { ("Frames:bare-pod", "Frames:round", "Rockets.Launcher.Pod"), ("Frames:absent-pod", "Frames:round", "Rockets.Launcher.Pod"), ("Frames:pod", "Frames:frame-m15", "Rockets.Launcher.Round"), ("pod", "Frames:round", "Rockets.Launcher.Pod") })
        {
            using var invalidLauncher = System.Text.Json.JsonDocument.Parse(Rocketeer(pod, round));
            rejected = false;
            try { payload.Parse(invalidLauncher.RootElement); }
            catch (ForgeCore.StrictJsonException error) { rejected = error.Field == field; }
            check(rejected, "a launcher without a tube, a missing launcher or a rocket without its mesh fails admission on " + field + ": " + pod + ", " + round);
        }
        FrameWorld.LevelCleanup();
    }

    private static void Data(FrameRecord record) => FrameWorld.OnData(new() { SummonerLookup = record.Thrower, ThrowId = record.ThrowId,
        Replayed = true, Data = FrameMessages.ToBytes(FrameStateMessage.From(record, Time.time, FrameEjection.None)) });

    private static void Reset(ulong local, ulong master)
    {
        FrameWorld.LevelCleanup();
        SNet.LocalPlayer = new() { Lookup = local }; SNet.Master = new() { Lookup = master };
        Time.time = 10; Time.frameCount = 10; Time.deltaTime = 0;
        OrdnanceApi.Retained.Clear(); OrdnanceApi.Capacity = 256; NetworkAPI.Sent.Clear();
        PlayerManager.PlayerAgentsInLevel = new() { new PlayerAgent { Owner = SNet.LocalPlayer } };
        FrameWorld.LevelEntered();
    }
}

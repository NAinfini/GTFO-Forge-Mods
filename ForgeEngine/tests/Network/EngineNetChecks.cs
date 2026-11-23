using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEngine.Network.Tests;

/// <summary>What the engine says through its ForgeCore channel, one path each: the state stream, the world step, presentation
/// requests gathered into one result, the migration image and the owner channel. The exactly-once ledger, the epoch rules,
/// splitting under the frame budget and the host table are ForgeCore's and are checked there; a client's domain command is
/// checked by the cross-package contract (Tests/Architecture/Contracts/F2Network).</summary>
internal static class EngineNetChecks
{
    internal static void State(Suite suite)
    {
        suite.Case("state: the whole document to a player owed it, what changed to everyone else");
        var session = new Session();
        var host = session.Join(1);
        var client = session.Join(2);
        session.Hub.Settle();
        void Sync(Machine machine) { machine.Net.SyncState(); session.Hub.Flush(); }
        void Change(int value) { host.Native = value; host.Kernel.MarkNativeStateChanged(); }

        host.Native = 5;
        Sync(host);
        suite.True(client.States.Count == 1 && client.States[0] == (true, host.Kernel.WorldEpoch) && client.Native == 5,
            "a player who joined is handed the whole document of the host's world");
        Sync(host);
        suite.Equal(1, client.States.Count, "an advance that changed nothing sends nothing");

        var joiner = session.Join(3);
        session.Hub.Settle();
        Change(6);
        var toClient = session.Sent("state", 2);
        Sync(host);
        suite.True(client.States.Count == 2 && !client.States[1].Complete && client.Native == 6 && session.Sent("state", 2) == toClient + 1,
            "a player that holds the document gets only what changed");
        suite.True(session.Sent("state", 3) == 2 && joiner.States.Count == 1 && joiner.States[0].Complete && joiner.Native == 6,
            "the joiner gets the whole document, and takes no change before it holds one");

        // ForgeCore's world moves (here a checkpoint generation): every copy is of another generation.
        host.Endpoint.Node.NoteWorld(NetWorldReason.Checkpoint);
        session.Hub.Flush();
        Change(7);
        Sync(host);
        suite.True(client.States.Count == 3 && client.States[2].Complete && client.Native == 7
            && joiner.States.Count == 2 && joiner.States[1].Complete && joiner.Native == 7,
            "after the world changed everyone is owed the whole document again");

        // A document that could not be applied leaves a client with nothing to apply changes to: it waits, then asks.
        client.Net.StateRefused();
        Change(8);
        Sync(host);
        suite.True(client.States.Count == 3 && client.Native == 7 && joiner.Native == 8, "a client without a document takes no change");
        Sync(client);
        client.Kernel.Advance(200, false);
        Sync(client);
        Sync(host);
        suite.True(client.States.Count == 4 && client.States[3].Complete && client.Native == 8 && joiner.States.Count == 3,
            "a client still waiting asks the host, and only that client gets the document");

        suite.Case("state: a change of rows travels as those rows, and a change that does not fit is refused");
        var before = RuntimeJson.Parse("{\"v\":1,\"rows\":[{\"id\":1},{\"id\":2},{\"id\":3}],\"by\":{\"a\":{\"x\":1}},\"gone\":true}");
        var after = RuntimeJson.Parse("{\"v\":1,\"rows\":[{\"id\":1},{\"id\":3},{\"id\":4}],\"by\":{\"a\":{\"x\":2},\"b\":{}}}");
        var buffer = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(buffer, System.Text.Encoding.UTF8, true)) StatePatch.Write(writer, before, after);
        var patch = buffer.ToArray();
        JsonElement Applied(JsonElement to, byte[] body) => StatePatch.Apply(to, new System.IO.BinaryReader(new System.IO.MemoryStream(body)));
        suite.True(Applied(before, patch).GetRawText() == after.GetRawText(), "the change applied to the earlier state is the later state");
        var text = System.Text.Encoding.UTF8.GetString(patch);
        suite.True(!text.Contains("\"id\":1") && !text.Contains("\"id\":3") && text.Contains("\"id\":4"), "only the rows that changed travel");
        var misfit = "";
        try { Applied(RuntimeJson.Parse("{\"rows\":[]}"), patch); } catch (RuntimeContractException error) { misfit = error.Code; }
        var unknown = "";
        try { Applied(before, new byte[] { 9 }); } catch (RuntimeContractException error) { unknown = error.Code; }
        suite.True(misfit == StatePatch.Code && unknown == StatePatch.Code, "a change written against another state, or of an unknown form, is refused whole");

        suite.Case("state: a variable change reads back as it was written, and a cut one is refused whole");
        VariableDeclaration Count() => new("rig.count", VariableScopeKinds.Level, VariableValueTypes.Integer, RuntimeJson.Parse("0"));
        var written = new RuntimeVariableStore();
        var held = new RuntimeVariableStore();
        var sent = Count();
        var taken = Count();
        written.Declare("rig", new[] { sent });
        held.Declare("rig", new[] { taken });
        written.Set(sent, RuntimeVariableScope.Level(), RuntimeJson.Parse("3"));
        var delta = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(delta, System.Text.Encoding.UTF8, true)) { written.WriteDelta(writer); writer.Write(false); }
        var body = delta.ToArray();
        held.ApplyDelta(held.PrepareDelta(new System.IO.BinaryReader(new System.IO.MemoryStream(body), RuntimeVariableStore.Text)));
        suite.Equal("3", held.Read(taken, RuntimeVariableScope.Level()).GetRawText(), "the value the host wrote is the value the client holds");
        var cut = "";
        try { joiner.Kernel.ApplyVariableDelta(joiner.Kernel.WorldEpoch, new ArraySegment<byte>(body, 0, body.Length - 2)); }
        catch (RuntimeContractException error) { cut = error.Code; }
        suite.Equal(NetworkCodes.FormatInvalid, cut, "a delta that is cut short is not read");

        suite.Case("state: a checkpoint reload on the host puts every registered source back, and a source that refuses is named");
        var solo = new Session();
        var owner = solo.Join(9);
        solo.Hub.Settle();
        // The real channel in place of the rig's one source: two domain sources, restored in registration order.
        DomainStateChannel.BindNativeState(owner.Kernel);
        int first = 1, second = 10;
        var refuses = 0;
        using var firstSource = DomainStateChannel.RegisterNativeState(owner.Kernel, "rig.first",
            () => RuntimeJson.From(new { value = first }), state => first = state.GetProperty("value").GetInt32());
        using var secondSource = DomainStateChannel.RegisterNativeState(owner.Kernel, "rig.second",
            () => RuntimeJson.From(new { value = second }), state =>
            {
                var value = state.GetProperty("value").GetInt32();
                // 1 refuses the saved value only; 2 refuses its own earlier value as well, so nothing can put it back.
                if (refuses == 2 || refuses == 1 && value == 10) throw new RuntimeContractException("rig-refused", "the source refuses this state");
                second = value;
            });
        var saved = owner.Kernel.CaptureCheckpoint();
        string Reload()
        {
            first = 2; second = 20;
            firstSource.Changed(); secondSource.Changed();
            try { owner.Kernel.RestoreCheckpoint(saved); return ""; }
            catch (RuntimeContractException error) { return error.Code; }
        }
        var startup = owner.Kernel.StartupState;
        suite.True(Reload() == "" && first == 1 && second == 10, "the host's reload puts both sources back to what the checkpoint saved");
        owner.Kernel.Advance(1, true);
        suite.True(owner.Kernel.StartupState == startup && !owner.Kernel.HostMigrationPaused && owner.Kernel.VariablesPending,
            "the kernel advances after the reload and owes the restored sources to the session");
        refuses = 1;
        suite.True(Reload() == "rig-refused" && first == 2 && second == 20,
            "a source that refuses the saved state is reported by its own code and the sources already restored are put back");
        refuses = 2;
        suite.Equal("native-state-rollback-failed", Reload(), "a source that also refuses its own earlier state is reported as a failed rollback");

        DoorAlarms(suite);
    }

    /// <summary>The door-alarm key (`forge.action.map.door_alarm`) on the real channel: the host's record reaches the
    /// players, a checkpoint reload puts it back, and after a host change the new host holds and sends the same record.</summary>
    private static void DoorAlarms(Suite suite)
    {
        suite.Case("state: the door-alarm record reaches every player, comes back on a checkpoint reload and survives a host change");
        var session = new Session();
        var machines = new[] { session.Join(1), session.Join(2), session.Join(3) };
        session.Hub.Settle();
        var records = new Dictionary<Machine, ForgeMap.DoorAlarmRecord>();
        var sources = new Dictionary<Machine, DomainStateChannel.NativeStateSource>();
        foreach (var machine in machines)
        {
            var record = records[machine] = new ForgeMap.DoorAlarmRecord();
            DomainStateChannel.BindNativeState(machine.Kernel);
            sources[machine] = DomainStateChannel.RegisterNativeState(machine.Kernel, ForgeMap.DoorAlarmRecord.StateKey,
                record.Capture, state => record.Replace(ForgeMap.DoorAlarmRecord.Parse(state)));
        }
        var (host, client, successor) = (machines[0], machines[1], machines[2]);
        void Sync(Machine from) { from.Net.SyncState(); session.Hub.Flush(); }
        string Text(Machine machine) => records[machine].Capture().GetRawText();
        const string door = "door/0/0/2/security", easy = "4013";

        records[host].Set(door, easy);
        sources[host].Changed();
        Sync(host);
        suite.True(Text(client) == Text(host) && Text(successor) == Text(host) && records[client].Doors.TryGetValue(door, out var held) && held == easy,
            "a replaced door reaches every player with the scan it now carries");

        var saved = host.Kernel.CaptureCheckpoint();
        records[host].Remove(door);
        sources[host].Changed();
        Sync(host);
        suite.True(!records[client].Contains(door), "putting the alarm back removes the door from every player's record");
        host.Kernel.RestoreCheckpoint(saved);
        Sync(host);
        suite.True(records[host].Contains(door) && Text(client) == Text(host),
            "a checkpoint reload puts the door saved as replaced back, on the host and on the players");

        var before = Text(host);
        session.Hub.Leave(host.Endpoint, successor.Player);
        session.Hub.Settle();
        var promoted = Text(successor);
        records[successor].Remove(door);
        sources[successor].Changed();
        Sync(successor);
        suite.True(promoted == before && before.Contains(door) && !records[client].Contains(door) && Text(client) == Text(successor),
            "after a host change the new host held the same record and its next change reaches the remaining player");

        var refused = "";
        try { ForgeMap.DoorAlarmRecord.Parse(RuntimeJson.Parse("{\"doors\":{\"" + door + "\":7}}")); }
        catch (FormatException error) { refused = error.Message; }
        suite.Equal("door-alarm-state", refused, "a record whose scan is not a resource id is refused whole");
        foreach (var source in sources.Values) source.Dispose();
    }

    internal static void Step(Suite suite)
    {
        suite.Case("step: the host's step inside a world number reaches its clients and a later joiner");
        var session = new Session();
        var host = session.Join(1);
        var client = session.Join(2);
        session.Hub.Settle();
        var epoch = host.Kernel.WorldEpoch + 1;
        host.Kernel.BeginWorld(epoch);
        host.Net.AnnounceStep(epoch);
        session.Hub.Flush();
        suite.True(client.Steps.SequenceEqual(new[] { epoch }), "a client is told the epoch the host stepped to");
        var joiner = session.Join(3);
        session.Hub.Settle();
        suite.True(joiner.Steps.SequenceEqual(new[] { epoch }) && client.Steps.Count == 1, "a player who joins later is told it too, and only that player");
    }

    internal static void Presentation(Suite suite)
    {
        suite.Case("presentation: every recipient's answer is gathered into one result");
        var session = new Session();
        var host = session.Join(1);
        var client = session.Join(2);
        var leaver = session.Join(3);
        session.Hub.Settle();
        var settled = new List<CommandResult>();
        host.Net.PresentationSettled = (_, result) => settled.Add(result);
        var sequence = 0;
        var shown = CommandResult.Create(CommandStatuses.Succeeded, CommitStates.None, "shown", "", RuntimeJson.EmptyObject);
        // One step for the host's own player and one remote player, each answering as given.
        CommandResult Present(CommandResult own, CommandResult remote, Machine? to = null)
        {
            host.Presents = () => own;
            (to ?? client).Presents = () => remote;
            host.Net.Present(new[]
            {
                new PresentationOutput("rig.provider", "rig.plan", "node", "command-" + ++sequence, "rig.binding", 0,
                    new[] { host.Address, (to ?? client).Address }, "event", "scope", host.Kernel.WorldEpoch, 1, RuntimeJson.EmptyObject)
                { RequestSequence = sequence }
            });
            var before = settled.Count;
            if (to != null) session.Hub.Leave(to.Endpoint);
            session.Hub.Step();
            return settled.Count == before + 1 ? settled[^1] : CommandResult.Failed("not-settled-once");
        }
        bool Complete(CommandResult result) => result.Outputs.GetProperty("presentationRecipientsComplete").GetBoolean();

        var all = Present(shown, shown);
        suite.True(all.Status == CommandStatuses.Succeeded && all.CommitState == CommitStates.None && all.Code == "shown"
            && all.Outputs.GetProperty("rows").GetArrayLength() == 2 && Complete(all), "every recipient succeeded: succeeded, one row each");
        var some = Present(shown, CommandResult.Rejected("hud-hidden"));
        suite.True(some.Status == CommandStatuses.Partial && some.Code == "hud-hidden" && Complete(some), "one succeeded, one did not: partial, with the failure's code");
        var none = Present(CommandResult.Expired("too-late"), CommandResult.Rejected("hud-hidden"));
        suite.True(none.Status == CommandStatuses.Failed && none.Code == "too-late", "nobody's write landed and they refused differently: failed, with the first that was not a plain refusal");

        // Two machines and two target players, the device disable's shape: each machine writes only its own player and ends
        // the other player's row as that machine's (a placeholder). The host's write lands; the client's device refuses.
        CommandResult Machine(params (Status Status, string Code, bool Elsewhere)[] rows)
        {
            var tally = new PresentedTally();
            foreach (var row in rows) tally.Add(row.Status, Commit.None, row.Code, row.Elsewhere);
            var own = tally.Conclude();
            return CommandResult.Create(Outcomes.StatusNames[(int)own.Status], CommitStates.None, own.Code, "", RuntimeJson.EmptyObject);
        }
        var hostOwn = Machine((Status.Partial, "device-player-remote", true), (Status.Partial, "device-disabled", false));
        var clientOwn = Machine((Status.Rejected, "device-unavailable", false), (Status.Partial, "device-player-remote", true));
        suite.True(hostOwn.Status == CommandStatuses.Partial && hostOwn.Code == "device-disabled"
            && clientOwn.Status == CommandStatuses.Rejected && clientOwn.Code == "device-unavailable",
            "a machine's placeholder row for another machine's player never becomes that machine's conclusion");
        var refused = Present(hostOwn, clientOwn);
        suite.True(refused.Status == CommandStatuses.Partial && refused.Code == "device-unavailable" && Complete(refused),
            "the host's write landed and the client's device refused: partial, with the client's refusal, not the host's write");
        var unknown = Present(shown, CommandResult.FailedUnknown("handler-exception"));
        suite.True(unknown.Status == CommandStatuses.Failed && unknown.CommitState == CommitStates.Unknown, "a recipient whose commit is unknown makes the whole step failed");

        // A presentation changes no world state: an answer that claims a commit is refused, not trusted.
        var claimed = Present(shown, CommandResult.Succeeded(RuntimeJson.EmptyObject));
        suite.True(claimed.Status == CommandStatuses.Partial && claimed.CommitState == CommitStates.None && claimed.Code == "invalid-presentation-result"
            && !Complete(claimed), "an answer that claims a commit is refused");

        var left = Present(shown, shown, leaver);
        suite.True(left.Status == CommandStatuses.Partial && left.Code == "presentation-recipient-left" && !Complete(left),
            "a recipient who left before answering is refused by name, and the step still settles");
    }

    private const string EmptyWorldImage = "{\"cancelled\":[],\"capturedTick\":-1,\"deferredResults\":{\"active\":[],\"completed\":[],\"receipts\":[]},\"effectSequence\":0,\"effects\":[],\"handleGeneration\":0,\"handles\":[],\"history\":[],\"historyFloor\":null,"
        + "\"owner\":{\"controlSequence\":0,\"earlyEnds\":[],\"effects\":[],\"flights\":[],\"issued\":{\"entries\":[],\"results\":[],\"templates\":[]},\"leases\":[],\"live\":false,\"receipts\":[],"
        + "\"receiver\":{\"cleanup\":[],\"descriptors\":[],\"endings\":[],\"ledger\":{\"entries\":[],\"results\":[],\"templates\":[]}}},\"pending\":[],\"providerHandover\":[],\"providers\":[],\"scheduleSequence\":0,\"schedules\":[],"
        + "\"schemaVersion\":\"1.0.0\",\"sequence\":0,\"tickUsage\":{\"commands\":0,\"events\":0,\"plans\":[],\"scheduled\":0},"
        + "\"variables\":{\"entries\":[],\"gates\":{\"rows\":[],\"seed\":0},\"nativeDomains\":{\"rig\":{\"value\":0}},\"once\":[],\"worldEpoch\":256},\"waits\":[],\"worldEpoch\":256}";

    internal static void Migration(Suite suite)
    {
        suite.Case("migration: the host's image is held by every player, one part per row");
        var session = new Session();
        var host = session.Join(1);
        var client = session.Join(2);
        var successor = session.Join(3);
        session.Hub.Settle();
        int Bytes() => session.Hub.Delivered.Where(packet => packet.To == 2 && packet.Name == NetworkProtocol.ChannelId + "/migration").Sum(packet => packet.Data.Length);
        string Publish()
        {
            var captured = host.Kernel.CaptureHostMigration();
            host.Net.PublishMigration(0, captured, null);
            session.Hub.Settle();
            return captured;
        }

        var image = Publish();
        var whole = Bytes();
        suite.True(client.Net.TryReadMigration(0, out var runtime, out var checkpoint) && runtime == image && checkpoint == null,
            "a client reassembles the image the host captured");
        // The image is the body of the route's current version: every part and every field of an empty world, in this order. The
        // gate store's seed is the one value drawn per kernel.
        suite.Equal(EmptyWorldImage, System.Text.RegularExpressions.Regex.Replace(image, "\"seed\":\\d+", "\"seed\":0"),
            "the image of an empty world is the published body, byte for byte");
        host.Kernel.Advance(1, true);
        var next = Publish();
        var changed = Bytes() - whole;
        suite.True(next != image && client.Net.TryReadMigration(0, out runtime, out _) && runtime == next, "the next capture replaces it");
        suite.True(changed > 0 && changed < 64 && changed < whole / 4, "and only the part that changed travelled (" + changed + " of " + whole + " bytes)");
        host.Net.DropMigration(0);
        session.Hub.Settle();
        suite.True(!client.Net.TryReadMigration(0, out _, out _) && !client.Net.HoldsMigration, "a dropped buffer reads as missing");

        suite.Case("command: a client's teleport report runs once on the host, as the sender's, and a malformed body is refused");
        var reports = new List<(ulong Sender, double[] From, double[] To)>();
        var verdicts = new List<DomainCommandResponse>();
        using (DomainCommands.Register("rig.teleport", request =>
        {
            if (request.ResourceId != ForgePlayer.PlayerTeleportReport.Resource
                || !ForgePlayer.PlayerTeleportReport.TryDecode(request.Payload, out var from, out var to))
                return new DomainCommandResult(CommandStatuses.Rejected, CommitStates.None, "invalid-payload", "", 0);
            reports.Add((request.SenderSession, from, to));
            return new DomainCommandResult(CommandStatuses.Succeeded, CommitStates.Confirmed, "ok", "", 1);
        }))
        using (DomainCommands.RegisterResults("rig.teleport", verdicts.Add))
        {
            var body = ForgePlayer.PlayerTeleportReport.Encode(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 });
            client.Net.SendCommand("rig.teleport", ForgePlayer.PlayerTeleportReport.Resource, 0, body);
            var unreadable = (byte[])body.Clone();
            BitConverter.TryWriteBytes(unreadable.AsSpan(12), float.NaN);
            client.Net.SendCommand("rig.teleport", ForgePlayer.PlayerTeleportReport.Resource, 0, unreadable);
            client.Net.SendCommand("rig.teleport", ForgePlayer.PlayerTeleportReport.Resource, 0, body[..23]);
            session.Hub.Settle();
            suite.True(reports.Count == 1 && reports[0].Sender == client.Player
                && reports[0].From.SequenceEqual(new double[] { 1, 2, 3 }) && reports[0].To.SequenceEqual(new double[] { 4, 5, 6 }),
                "the host ran the report once, for the player that sent it, with both ends as sent");
            suite.True(verdicts.Count == 3 && verdicts.Count(each => each.Result.Code == "invalid-payload"
                && each.Result.Status == CommandStatuses.Rejected && each.Result.Commit == CommitStates.None) == 2,
                "a body with a value that is not finite or of another length is refused as invalid-payload and commits nothing");
        }

        suite.Case("migration: the commands the host ran travel with the image");
        var runs = 0;
        var answers = new List<DomainCommandResponse>();
        using var executor = DomainCommands.Register("rig.migration", _ => { runs++; return new DomainCommandResult(CommandStatuses.Succeeded, CommitStates.Confirmed, "done", "", 1); });
        using var observer = DomainCommands.RegisterResults("rig.migration", answers.Add);
        client.Net.SendCommand("rig.migration", "door", 1, new byte[] { 1 });
        // The host runs the command and leaves before its answer travels.
        var ask = session.Hub.Wire.Single();
        session.Hub.Wire.Clear();
        session.Hub.Deliver(ask);
        session.Hub.Wire.Clear();
        Publish();
        // The sender asks the promoted host again at once, before that host restored anything.
        session.Hub.Leave(host.Endpoint, successor.Player);
        session.Hub.Settle();
        suite.True(runs == 1 && answers.Count == 1 && answers[0].Result.Code == NetworkCodes.MigrationCommitUnknown
            && answers[0].Result.Commit == CommitStates.Unknown,
            "the promoted host does not run it again, and the sender is told its commit is not known");

        suite.Case("migration: a card that let its first run through stays spent for the client, the promoted host and a checkpoint reload");
        var latched = new Session();
        var first = latched.Join(1);
        var watcher = latched.Join(2);
        var heir = latched.Join(3);
        latched.Hub.Settle();
        // A latch is one card of one plan, whichever trigger card led to it: the key names no entrypoint.
        var card = RuntimeVariableStore.LatchKeyOf("rig.plan", "Once");
        var other = RuntimeVariableStore.LatchKeyOf("rig.plan", "Other");
        bool Spent(Machine machine, string key) => !machine.Kernel.kernelVariables.variables.Fork().ClaimOnce(key);
        suite.True(first.Kernel.kernelVariables.variables.ClaimOnce(card) && Spent(first, card) && !Spent(first, other), "the first run claims the card and no other");
        first.Kernel.MarkNativeStateChanged();
        first.Net.SyncState();
        latched.Hub.Flush();
        suite.True(Spent(watcher, card) && !Spent(watcher, other), "a client holds the host's latch under the same key");
        first.Kernel.Advance(1, true);
        var reload = first.Kernel.CaptureCheckpoint();
        first.Kernel.kernelVariables.variables.ClaimOnce(other);
        first.Kernel.RestoreCheckpoint(reload);
        suite.True(Spent(first, card) && !Spent(first, other), "a checkpoint reload puts back the latches it saved and no later one");
        var handed = first.Kernel.CaptureHostMigration();
        first.Net.PublishMigration(0, handed, null);
        latched.Hub.Settle();
        var read = heir.Net.TryReadMigration(0, out var held, out _);
        suite.True(handed.Contains("\"once\":[\"rig.plan:Once\"]") && read && held == handed, "the image carries the latch as plan and card");
        heir.Kernel.RestoreHostMigration(handed, heir.Kernel.WorldEpoch + 1, true);
        suite.True(Spent(heir, card) && !Spent(heir, other), "the promoted host does not let the card's first run through again");
    }

    internal static void Owner(Suite suite)
    {
        suite.Case("owner: a step travels to its holder and its result back to the host");
        var session = new Session();
        var host = session.Join(1);
        var holder = session.Join(2);
        var stranger = session.Join(3);
        session.Hub.Settle();
        var request = new OwnerOutput("rig.provider", "rig.plan", "node", "owner-1", "rig.binding", 0, holder.Address, "event", "scope",
            host.Kernel.WorldEpoch, 1, RuntimeJson.EmptyObject)
        {
            HolderIdentity = holder.Player,
            Data = OwnerCommandLedger.Data(RuntimeJson.EmptyObject, new[] { "cancel-command" })
        };
        static byte[] Apply(OwnerOutput step) => EngineMessages.Encode(new OwnerChannelMessage("apply", step, null, ""));
        static OwnerChannelMessage Read(byte[] body) => EngineMessages.TryDecode(body, out OwnerChannelMessage message) ? message : throw new FormatException("owner message");

        // The channel is one per process, so each half runs in turn: the holder's first, fed what the host's half sends below.
        var applied = new List<OwnerOutput>();
        var replies = new List<byte[]>();
        OwnerCommandChannel.Attach(holder.Net, message =>
        {
            applied.Add(message.Request);
            OwnerCommandChannel.Reply(message.Request, CommandResult.Succeeded(RuntimeJson.EmptyObject));
        });
        host.Net.OwnerReceived = (_, body) => replies.Add(body);
        host.Net.SendOwner(holder.Player, Apply(request with { WorldEpoch = request.WorldEpoch - 1 }));
        session.Hub.Flush();
        suite.True(applied.Count == 0 && replies.Count == 1 && Read(replies[0]).Result!.Code == NetworkCodes.StaleWorld,
            "a step of a world the holder left is not applied and is answered as stale");
        host.Net.SendOwner(holder.Player, Apply(request));
        session.Hub.Flush();
        suite.True(applied.Count == 1 && applied[0].CommandId == request.CommandId && replies.Count == 2, "the holder applies the host's step and answers it");
        OwnerCommandChannel.Detach();

        var results = new List<OwnerChannelMessage>();
        byte[]? sent = null;
        OwnerCommandChannel.Attach(host.Net, results.Add);
        holder.Net.OwnerReceived = (_, body) => sent = body;
        OwnerCommandChannel.Send(request);
        session.Hub.Flush();
        suite.True(results.Count == 0 && session.Sent("owner", stranger.Player) == 0 && sent != null && sent.AsSpan().SequenceEqual(Apply(request)),
            "the host sends the step to its holder alone");
        stranger.Net.SendOwnerToHost(replies[1]);
        session.Hub.Flush();
        suite.Equal(0, results.Count, "a player who was not sent the step cannot answer it");
        holder.Net.SendOwnerToHost(replies[1]);
        session.Hub.Flush();
        suite.True(results.Count == 1 && results[0].Kind == "result" && results[0].Request.CommandId == request.CommandId
            && results[0].Result!.Status == CommandStatuses.Succeeded, "the holder's result reaches the host once, for the step it was sent");
        suite.True(Read(replies[1]).Request.PlanId == "" && results[0].Request.PlanId == request.PlanId,
            "the answer names only the step; the host takes the request it sent");
        OwnerCommandChannel.Detach();

        suite.Case("owner: a team step is split by holder, each holder is sent its own recipients only, and the rows merge back in order");
        var team = RuntimeJson.Parse("{\"players\":[{\"id\":\"gtfo.player:1\",\"worldEpoch\":1,\"lifeEpoch\":1},{\"id\":\"gtfo.player:2\",\"worldEpoch\":1,\"lifeEpoch\":2},"
            + "{\"id\":\"gtfo.player:3\",\"worldEpoch\":1,\"lifeEpoch\":3}],\"dimension\":1}");
        var holders = RuntimeKernel.SplitHolders(team.GetProperty("players"), "0", entity => entity.Id == "gtfo.player:2" ? "1" : "0", out var rowHolder);
        var clientPart = RuntimeKernel.OwnRecipients(team, "players", rowHolder, "1");
        suite.True(holders is ["0", "1"] && clientPart.GetProperty("players").GetArrayLength() == 1
            && clientPart.GetProperty("players")[0].GetProperty("id").GetString() == "gtfo.player:2" && clientPart.GetProperty("dimension").GetInt32() == 1,
            "the client is sent only the player it simulates, with every other input unchanged");
        static CommandResult Rows(params string[] codes) => CommandResult.Succeeded(RuntimeJson.From(new
        {
            rows = codes.Select(code => new { status = CommandStatuses.Succeeded, committed = CommitStates.Confirmed, code }).ToArray()
        }));
        var merged = RuntimeKernel.MergeOwnerRows("rows", rowHolder, holders!, new[] { Rows("host-a", "host-b"), Rows("client") });
        suite.True(merged.Outputs.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("code").GetString()).SequenceEqual(new[] { "host-a", "client", "host-b" }),
            "each recipient's row comes from its own holder, in recipient order");
        suite.Equal("owner-split-incomplete", RuntimeKernel.MergeOwnerRows("rows", rowHolder, holders!, new[] { Rows("host-a", "host-b"), Rows("client", "extra") }).Code,
            "a holder that answers rows for recipients it was not sent is not merged");

        suite.Case("owner: a body is read back as it was written, and one that is not a whole message is refused");
        var body = Apply(request);
        suite.True(EngineMessages.TryDecode(body, out OwnerChannelMessage back) && back.Kind == "apply" && back.Result == null
            && back.Request.CommandId == request.CommandId && back.Request.HolderIdentity == request.HolderIdentity
            && EngineMessages.Encode(back).AsSpan().SequenceEqual(body), "an owner message reads back as it was written");
        var answer = EngineMessages.Encode(CommandResult.FailedUnknown(RuntimeJson.Parse("{\"rows\":[1]}"), "holder-code", "why"));
        suite.True(EngineMessages.TryDecode(answer, out CommandResult read) && read.Status == CommandStatuses.Failed
            && read.CommitState == CommitStates.Unknown && read.Code == "holder-code" && read.Detail == "why"
            && read.Outputs.GetProperty("rows").GetArrayLength() == 1, "a result reads back as it was written");
        suite.True(!EngineMessages.TryDecode(body.AsSpan(0, body.Length - 1).ToArray(), out OwnerChannelMessage _)
            && !EngineMessages.TryDecode(body.Append((byte)0).ToArray(), out OwnerChannelMessage _)
            && !EngineMessages.TryDecode(new byte[] { 9 }, out OwnerChannelMessage _)
            && !EngineMessages.TryDecode(answer.AsSpan(0, answer.Length - 2).ToArray(), out CommandResult _),
            "a cut, padded or unknown body is not read");
        // One fact per recipient of a full-width set travels; one more is refused where it is made and where it is read.
        RuntimeFact Fact(int index) => new("rig.binding", RuntimeJson.Parse("{\"amount\":" + index + "}"));
        var full = EngineMessages.Encode(CommandResult.Succeeded(RuntimeJson.EmptyObject,
            Enumerable.Range(0, CommandResult.MaximumFacts).Select(Fact).ToArray()));
        suite.True(EngineMessages.TryDecode(full, out CommandResult wide) && wide.Facts.Count == Wire.MaxSetWidth,
            "a result with one fact per recipient of a full-width set reads back whole");
        string? made = null;
        try { CommandResult.Succeeded(RuntimeJson.EmptyObject, Enumerable.Range(0, CommandResult.MaximumFacts + 1).Select(Fact).ToArray()); }
        catch (RuntimeContractException error) { made = error.Code; }
        using var over = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(over, new System.Text.UTF8Encoding(false), true))
        {
            writer.Write((byte)0); writer.Write((byte)1); writer.Write("committed"); writer.Write("");
            writer.Write(2); writer.Write("{}"u8.ToArray());
            writer.Write(CommandResult.MaximumFacts + 1);
            for (var index = 0; index <= CommandResult.MaximumFacts; index++)
            { writer.Write("rig.binding"); var json = System.Text.Encoding.UTF8.GetBytes("{\"amount\":" + index + "}"); writer.Write(json.Length); writer.Write(json); }
        }
        suite.True(made == "fact-budget" && !EngineMessages.TryDecode(over.ToArray(), out CommandResult _),
            "a fact past the budget is refused as fact-budget when made and is not read off the wire [" + made + "]");
        var refused = ForgeCore.Net.Dropped(NetworkCodes.FormatInvalid);
        OwnerCommandChannel.Attach(holder.Net, _ => { });
        host.Net.SendOwner(holder.Player, new byte[] { 9 });
        session.Hub.Flush();
        suite.Equal(refused + 1, ForgeCore.Net.Dropped(NetworkCodes.FormatInvalid), "and the channel counts it as a format refusal");
        OwnerCommandChannel.Detach();
    }
}

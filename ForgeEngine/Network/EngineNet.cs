using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEngine.Network;

/// <summary>The host took a world step that has no new ForgeCore world number (it suspended itself): the kernel epoch to move to.</summary>
internal struct WorldStep
{
    public long Epoch;
}

/// <summary>One presentation step the host asks one player's machine to run: the plan step, the kernel's request sequence and the
/// inputs the host resolved.</summary>
internal sealed record PresentationOrder(string PlanId, int StepIndex, string CommandId, long Sequence, string Recipient,
    long WorldEpoch, long Tick, JsonElement Inputs, long Prediction = 0);

/// <summary>The first row of one migration buffer: the format and the names of the image's parts, in the image's order.</summary>
internal sealed record MigrationHead(string SchemaVersion, IReadOnlyList<string> Parts);

/// <summary>
/// What the engine sends, all of it through its one ForgeCore channel. ForgeCore owns the transport, the member list, the
/// handshake, the world number, exactly-once requests and the frame budget; this class owns what the engine says with them:
/// <list type="bullet">
/// <item>the host's variable state, as one ordered stream: what changed to everyone (a domain's native state as the rows
/// that changed), the whole document only to a player who is owed it (it joined, or the world, the checkpoint generation
/// or the host changed, or it could not take a change);</item>
/// <item>presentation steps, one request per recipient, gathered into the one result the kernel records;</item>
/// <item>domain commands a client sends the host, and the owner steps the host sends the one machine that holds what they change;</item>
/// <item>the host's migration image, kept in a host table one part per row, so a part that did not change is not sent again.</item>
/// </list>
/// It reads no game type: the game-facing half (<c>NetworkBinding</c>) supplies who the players are and where the kernel may run.
/// </summary>
internal sealed class EngineNet
{
    private const double PresentationSeconds = 12;
    private const long StateAskTicks = 100;
    private const byte StateDelta = 0, StateComplete = 1, StateAsk = 2;
    private const int MigrationRows = 4096, HeadRow = 0, CheckpointRow = 1, PartRows = 2, LedgerRow = -1;

    private readonly NetChannel _channel;
    private readonly NetBytes _state, _owner;
    private readonly NetRequest _present, _command;
    private readonly NetEvent<WorldStep> _step;
    private readonly NetByteTable<int> _migration;
    private readonly HashSet<ulong> _synced = new(), _owed = new(), _silent = new();
    private readonly Predicate<ulong> _gone, _compatible;
    private readonly List<int> _rows = new();
    private bool _have;
    private long _askedAt = long.MinValue;

    internal EngineNet(NetChannel channel)
    {
        _channel = channel;
        _gone = player => _channel.State(player) != PeerState.Compatible;
        _compatible = player => _channel.State(player) == PeerState.Compatible;
        _bodyWriter = new BinaryWriter(_body, RuntimeVariableStore.Text, true);
        _state = channel.Bytes("state", 3, ReceiveState, NetDelivery.Reliable, 9 + NetworkProtocol.MaximumStateBytes);
        // Version 4 of the three bodies that carry a result: a result may carry up to 256 facts (was 128).
        _owner = channel.Bytes("owner", 4, (sender, body) => OwnerReceived?.Invoke(sender, body), NetDelivery.Reliable, NetworkProtocol.MaximumMessageBytes);
        // `present` version 5: an order carries the token of the recipient's own prediction of the step (0 for none).
        _present = channel.Request("present", 5, ServePresentation, NetworkProtocol.MaximumMessageBytes);
        // `command` also covers `forge.player.input` carrying the `teleported` report (a client's own warp, both ends).
        // Version 5: the interaction point's request has no identity phase (a client reads its own life from the identity
        // table) and a finish that comes before the hold time ran out on the host is answered `completion-held`.
        // Version 6: the weapon report's shot record always carries the shot's number (the token of its prediction).
        _command = channel.Request("command", 6, ServeCommand);
        _step = channel.Event<WorldStep>("step", (sender, step) => { if (_channel.FromHost(sender)) StepReceived?.Invoke(step.Epoch); });
        // Version 3: the image's frames carry enum members by position and its variable part carries the `once`
        // latches by key. `object_kind` and `deployable_removal_reason` made two text slots enums, the status
        // ending's `reason` and the enemy group read's `group_type` slots are enums too (`status_end_reason`,
        // `enemy_group_kind`), and a latch key is now the plan and the card, without the entrypoint.
        _migration = channel.Table<int>("migration", 3, 2 * NetworkProtocol.MaximumMigrationBytes + 64 * 1024);
        // Every player keeps the ledger of the commands the host had run at its last capture. A promoted player is asked the
        // unanswered ones again as soon as the host is gone, before any image is restored: it answers them from this ledger.
        _migration.Changed += (row, ledger) => { if (row == LedgerRow && ledger != null && !_channel.IsHost) _command.RestoreLedger(ledger); };
        channel.PeerSynced += Synced;
        channel.PeerSilent += player => _silent.Add(player);
    }

    /// <summary>The engine's network while the runtime runs; what <c>DomainCommands.Send</c> and the owner channel send through.</summary>
    internal static EngineNet? Current { get; set; }

    // ---- what the game-facing half supplies -----------------------------------------------------------------------------------

    internal RuntimeKernel? Kernel
    {
        get => _kernel;
        // The kernel names this machine's player by the same address a presentation order does.
        set { _kernel = value; if (value != null) value.LocalSession = () => LocalAddress(); }
    }
    private RuntimeKernel? _kernel;
    /// <summary>This machine's player and the session's host, as the transport names them; 0 while there is none.</summary>
    internal Func<ulong> Local { get; set; } = static () => 0;
    internal Func<ulong> Host { get; set; } = static () => 0;
    /// <summary>The address a requested step names to reach this machine's player.</summary>
    internal Func<string> LocalAddress { get; set; } = static () => "";
    /// <summary>The player an address names; 0 when no player or more than one holds it.</summary>
    internal Func<string, ulong> SessionOf { get; set; } = static _ => 0;
    /// <summary>Runs a presentation step inside the bridge's dispatch boundary.</summary>
    internal Func<Func<CommandResult>, CommandResult> Boundary { get; set; } = static run => run();
    /// <summary>Runs a callback into the kernel under the bridge's failure guard.</summary>
    internal Action<Action> Guard { get; set; } = static action => action();
    internal NetworkLogPoints? Log { get; set; }

    internal bool IsHost => _channel.IsHost;
    internal SessionRole Role => Local() == 0 ? SessionRole.Unknown : _channel.IsHost ? SessionRole.Host : SessionRole.Client;
    internal bool FromHost(ulong sender) => _channel.FromHost(sender);

    /// <summary>Why nothing may be sent to <paramref name="player"/>, or null while it runs the same engine and content.</summary>
    internal string? Refusal(ulong player) => _channel.State(player) switch
    {
        PeerState.Compatible => null,
        PeerState.Refused => NetworkCodes.HostUnmatched,
        _ => NetworkCodes.HelloUnsent
    };

    /// <summary>The host refused this engine or answered none of its greetings; false while there is no host, while it is
    /// compatible, and while the greeting is still under way.</summary>
    internal bool HostUnreachable
    {
        get
        {
            var host = Host();
            if (host == 0 || _channel.IsHost) return false;
            var state = _channel.State(host);
            return state == PeerState.Refused || state == PeerState.Unknown && _silent.Contains(host);
        }
    }

    /// <summary>The kernel epoch a ForgeCore world number begins at.</summary>
    internal static long WorldBase(NetWorld world) => world.Number * NetworkProtocol.WorldSpan;

    private void Synced(ulong player)
    {
        _synced.Add(player);
        _silent.Remove(player);
        if (!_channel.IsHost) return;
        _owed.Add(player);
        // A player who joins a world the host has stepped inside is told the step the announcement of the number cannot carry.
        if (Kernel is { } kernel && kernel.WorldEpoch % NetworkProtocol.WorldSpan != 0) _step.To(player, new WorldStep { Epoch = kernel.WorldEpoch });
    }

    /// <summary>ForgeCore's world moved (a new number, a checkpoint generation or a new host): every player's copy of the state
    /// is of another world or generation, so each is owed the whole document and a client takes no change before it.</summary>
    internal void WorldChanged()
    {
        _have = false;
        _askedAt = long.MinValue;
        _synced.RemoveWhere(_gone);
        _owed.Clear();
        _owed.UnionWith(_synced);
    }

    // ---- world step -----------------------------------------------------------------------------------------------------------

    /// <summary>A client is told the host's kernel moved to <c>epoch</c> inside the current world number.</summary>
    internal Action<long>? StepReceived { get; set; }

    internal void AnnounceStep(long epoch)
    {
        if (_channel.IsHost) _step.Broadcast(new WorldStep { Epoch = epoch });
    }

    // ---- variable state -------------------------------------------------------------------------------------------------------

    /// <summary>A client received a state body: whether it is the whole document, the kernel epoch it was exported in, and the
    /// body after its header (<see cref="ApplyState"/> applies it).</summary>
    internal Action<bool, long, ArraySegment<byte>>? StateReceived { get; set; }

    /// <summary>Applies one received state body to the kernel: the whole document is the kernel's JSON text, a change is the
    /// kernel's binary delta (<see cref="RuntimeKernel.ExportVariableDelta"/>).</summary>
    internal static void ApplyState(RuntimeKernel kernel, bool complete, long epoch, ArraySegment<byte> body)
    {
        if (complete) kernel.ApplyVariables(Encoding.UTF8.GetString(body));
        else kernel.ApplyVariableDelta(epoch, body);
    }

    /// <summary>One advance's state work. The host sends what changed to everyone and the whole document to each player owed
    /// it; with nobody to send to it exports nothing, and the next player owed a document gets everything in it. A client that
    /// still waits for its document asks the host for it.</summary>
    internal void SyncState()
    {
        var kernel = Kernel;
        if (kernel == null) return;
        if (!_channel.IsHost)
        {
            if (_have || _channel.State(Host()) != PeerState.Compatible) return;
            // The host owes the document by itself: the wait starts here, and it is asked only when the document does not come.
            if (_askedAt == long.MinValue) { _askedAt = kernel.CurrentTick; return; }
            if (kernel.CurrentTick - _askedAt < StateAskTicks) return;
            _askedAt = kernel.CurrentTick;
            _state.ToHost(new[] { StateAsk });
            return;
        }
        if (_synced.Count == 0) return;
        if (kernel.VariablesPending)
        {
            _body.SetLength(0);
            _bodyWriter.Write(StateDelta);
            _bodyWriter.Write(kernel.WorldEpoch);
            kernel.ExportVariableDelta(_bodyWriter);
            _bodyWriter.Flush();
            _state.Broadcast(_body.ToArray());
        }
        if (_owed.Count == 0) return;
        byte[]? document = null;
        foreach (var player in _owed)
        {
            if (_channel.State(player) != PeerState.Compatible) continue;
            document ??= StateDocument(kernel.WorldEpoch, kernel.ExportVariables());
            _state.To(player, document);
            Log?.Variables("sent", "document", kernel.WorldEpoch, document.Length);
        }
        // Only the players just sent to are paid; one not compatible yet stays owed instead of waiting for its own ask.
        if (document == null) return;
        _owed.RemoveWhere(_compatible);
    }

    /// <summary>A state body is a kind byte, the kernel epoch (eight bytes, little-endian) and the body: the whole document
    /// as the kernel's JSON text, or a change as the kernel's binary delta written straight into the body.</summary>
    private readonly MemoryStream _body = new(256);
    private readonly BinaryWriter _bodyWriter;

    private static byte[] StateDocument(long epoch, string document)
    {
        var body = new byte[9 + Encoding.UTF8.GetByteCount(document)];
        body[0] = StateComplete;
        BitConverter.TryWriteBytes(body.AsSpan(1), epoch);
        Encoding.UTF8.GetBytes(document, body.AsSpan(9));
        return body;
    }

    private void ReceiveState(ulong sender, byte[] body)
    {
        if (body.Length == 1 && body[0] == StateAsk)
        {
            if (_channel.IsHost) { _synced.Add(sender); _owed.Add(sender); }
            return;
        }
        if (!_channel.FromHost(sender)) { Net.Drop(NetworkCodes.SenderNotHost); return; }
        if (body.Length < 9 || body[0] > StateComplete) { Net.Drop(NetworkCodes.FormatInvalid); return; }
        var complete = body[0] == StateComplete;
        if (!complete && !_have) return;
        _have = true;
        StateReceived?.Invoke(complete, BitConverter.ToInt64(body, 1), new ArraySegment<byte>(body, 9, body.Length - 9));
    }

    /// <summary>The whole document a client received could not be applied: it holds no state to apply changes to, and asks again.</summary>
    internal void StateRefused() => _have = false;

    // ---- presentation ---------------------------------------------------------------------------------------------------------

    /// <summary>The host gathered every recipient's answer to one presentation step.</summary>
    internal Action<PresentationOutput, CommandResult>? PresentationSettled { get; set; }

    private sealed class PresentationRow
    {
        internal string Address = "";
        internal ulong Session;
        internal CommandResult? Result;
        internal bool Answered;
    }

    private sealed class PresentationGroup
    {
        internal readonly PresentationOutput Request;
        internal readonly PresentationRow[] Rows;
        internal bool Settled;
        internal PresentationGroup(PresentationOutput request, Func<string, ulong> sessionOf)
        {
            Request = request;
            Rows = new PresentationRow[request.Recipients.Count];
            for (var index = 0; index < Rows.Length; index++)
                Rows[index] = new PresentationRow { Address = request.Recipients[index], Session = sessionOf(request.Recipients[index]) };
        }

        /// <summary>Whether another recipient of the step names the same player.</summary>
        internal bool Shared(PresentationRow row)
        {
            foreach (var other in Rows)
                if (other != row && other.Session == row.Session) return true;
            return false;
        }
    }

    /// <summary>The kernel's admission of a presentation step: every recipient is a real address.</summary>
    internal static CommandResult? AdmitPresentation(PresentationOutput output)
    {
        foreach (var recipient in output.Recipients)
            if (string.IsNullOrEmpty(recipient))
                return CommandResult.Rejected(RuntimeAbiCodes.PresentationRecipient, "A presentation recipient must be a real address.");
        return null;
    }

    /// <summary>The presentation steps one advance decided, each asked of every player its provider named: this machine's own
    /// player directly, every other through one request. The recipients' answers are gathered into one result.</summary>
    internal void Present(IReadOnlyList<PresentationOutput> outputs)
    {
        if (!_channel.IsHost) return;
        foreach (var output in outputs)
        {
            if (output.Recipients.Count == 0) continue;
            var group = new PresentationGroup(output, SessionOf);
            foreach (var row in group.Rows) Ask(group, row);
        }
    }

    private void Ask(PresentationGroup group, PresentationRow row)
    {
        var output = group.Request;
        if (row.Session == 0) { Settle(group, row, CommandResult.Rejected("presentation-recipient-missing", "No player in the session holds the declared slot."), false); return; }
        if (group.Shared(row))
        { Settle(group, row, CommandResult.Rejected("presentation-recipient-ambiguous", "The declared slot names a player another recipient names too."), false); return; }
        var order = new PresentationOrder(output.PlanId, output.StepIndex, output.CommandId, output.RequestSequence, row.Address,
            output.WorldEpoch, output.SimulationTick, output.Inputs,
            output.PredictedBy == row.Address ? output.PredictionToken : 0);
        if (row.Session == Local()) { Settle(group, row, RunPresentation(order), true); return; }
        if (Refusal(row.Session) is { } refusal)
        { Settle(group, row, CommandResult.Rejected(refusal, "The recipient does not run the same engine and content."), false); return; }
        var body = EngineMessages.Encode(order);
        if (body.Length > NetworkProtocol.MaximumMessageBytes)
        { Settle(group, row, CommandResult.Rejected(NetworkCodes.PayloadTooLarge, "The presentation inputs exceed one request."), false); return; }
        _present.Ask(row.Session, body, answer => Settle(group, row, answer.Status switch
        {
            NetAnswerStatus.Answered => ReadResult(answer.Body),
            NetAnswerStatus.AlreadyRan => CommandResult.FailedUnknown(NetworkCodes.MigrationCommitUnknown, "The recipient ran this step for an earlier host; its result is not known."),
            NetAnswerStatus.TimedOut => CommandResult.Expired("presentation-result-timeout", Unreturned),
            NetAnswerStatus.Left => CommandResult.Rejected("presentation-recipient-left", Unreturned),
            _ => CommandResult.Cancelled(NetworkCodes.StaleWorld, Unreturned)
        }, answer.Status == NetAnswerStatus.Answered), PresentationSeconds);
    }

    private const string Unreturned = "The declared recipient did not return its presentation result.";

    private static CommandResult ReadResult(byte[] body)
        => EngineMessages.TryDecode(body, out CommandResult result) ? result
            : CommandResult.Rejected("invalid-presentation-result", "The recipient reply is not a result.");

    /// <summary>Runs one presentation step on this machine, for its own player.</summary>
    private CommandResult RunPresentation(PresentationOrder order)
    {
        var kernel = Kernel;
        if (kernel == null) return CommandResult.Rejected(NetworkCodes.NoConsumer, "The runtime is not running.");
        if (order.Recipient != LocalAddress())
            return CommandResult.Rejected("presentation-recipient-changed", "The declared player slot belongs to another session.");
        if (order.WorldEpoch != kernel.WorldEpoch) return CommandResult.Rejected(NetworkCodes.StaleWorld);
        // A step this machine already played when its own player reported the fact that started it is answered
        // with the result it had then.
        if (order.Prediction != 0 && kernel.TakePrediction(order.Prediction, order.PlanId, order.StepIndex) is { } shown)
            return shown;
        try
        {
            return Boundary(() => kernel.ExecutePresentationCommand(order.PlanId, order.StepIndex, order.CommandId,
                order.Sequence.ToString(CultureInfo.InvariantCulture), order.Recipient, order.WorldEpoch, order.Tick, order.Inputs));
        }
        catch (Exception error)
        { return CommandResult.Rejected("presentation-send-failed", CommandResult.TruncateDetail(error.GetType().Name + ": " + error.Message)); }
    }

    private void ServePresentation(NetAsk ask)
    {
        var result = !_channel.FromHost(ask.From)
            ? CommandResult.Rejected(NetworkCodes.SenderNotHost, "A presentation request comes from the host.")
            : !EngineMessages.TryDecode(ask.Body, out PresentationOrder order)
                ? CommandResult.Rejected(NetworkCodes.FormatInvalid, "A presentation request names a plan step.")
                : RunPresentation(order);
        var body = EngineMessages.Encode(result);
        if (body.Length > NetworkProtocol.MaximumMessageBytes)
            body = EngineMessages.Encode(CommandResult.FailedUnknown("presentation-result-budget", "The presentation result exceeds one answer."));
        Log?.Request(Role, "presentation", result.Status, result.Code);
        ask.Answer(body);
    }

    /// <summary>Records one recipient's result. A presentation changes no world state, so a result that claims a commit, carries a
    /// fact or is not a valid result is refused rather than trusted.</summary>
    private void Settle(PresentationGroup group, PresentationRow row, CommandResult result, bool answered)
    {
        if (group.Settled || row.Result != null) return;
        if (!CommandResultRules.TryValidate(result, out _, presentation: true)
            || !(result.CommitState == CommitStates.None || result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown)
            || result.Facts.Count != 0 || result.Outputs.ValueKind != JsonValueKind.Object)
        {
            result = CommandResult.Rejected("invalid-presentation-result", "The recipient reply did not carry a valid complete presentation result.");
            answered = false;
        }
        row.Result = result;
        row.Answered = answered;
        foreach (var other in group.Rows)
            if (other.Result == null) return;
        group.Settled = true;
        // The rows keep their anonymous shape (it is what the gathered result serializes); each recipient machine's answer
        // is one row of the one presentation rule (ResultAggregation.Presented), so a machine's real refusal is never
        // hidden behind another machine's write.
        var rows = new object[group.Rows.Length];
        var complete = true;
        for (var index = 0; index < rows.Length; index++)
        {
            var each = group.Rows[index];
            var settled = each.Result!;
            rows[index] = new
            {
                recipient = each.Address,
                session = each.Session,
                worldEpoch = group.Request.WorldEpoch,
                status = settled.Status,
                commit = settled.CommitState,
                code = settled.Code,
                outputs = settled.Outputs,
                answered = each.Answered
            };
            complete &= each.Answered;
        }
        CommandResult gathered;
        try
        {
            var concluded = ResultAggregation.Presented(group.Rows, static row => row.Result!.Status, static row => row.Result!.Code,
                RuntimeJson.From(new
                {
                    execution = "presentation",
                    rows,
                    presentationRecipients = group.Request.Recipients,
                    presentationRecipientsComplete = complete
                }), static row => row.Result!.CommitState);
            // The detail is the one the deciding machine gave with that code.
            var detail = Array.Find(group.Rows, row => row.Result!.Code == concluded.Code)?.Result!.Detail ?? "";
            gathered = detail.Length == 0 ? concluded
                : CommandResult.Create(concluded.Status, concluded.CommitState, concluded.Code, detail, concluded.Outputs);
        }
        catch (RuntimeContractException error)
        {
            var unknown = Array.Exists(group.Rows, row => row.Result!.CommitState == CommitStates.Unknown);
            gathered = CommandResult.Create(CommandStatuses.Failed, unknown ? CommitStates.Unknown : CommitStates.None,
                "presentation-result-budget", error.Message, RuntimeJson.EmptyObject);
        }
        Log?.Request(Role, "presentation", gathered.Status, gathered.Code);
        PresentationSettled?.Invoke(group.Request, gathered);
    }

    // ---- domain commands ------------------------------------------------------------------------------------------------------

    /// <summary>Submits a domain observation to the host: run at once when this machine is the host, asked of the host otherwise.
    /// False when there is no host to reach.</summary>
    internal bool SendCommand(string endpoint, string resourceId, long lifeEpoch, byte[] payload)
    {
        var kernel = Kernel;
        var local = Local();
        if (kernel == null || local == 0) return false;
        var world = kernel.WorldEpoch;
        var request = DomainCommands.NextRequest();
        if (_channel.IsHost)
        {
            var result = DomainCommandRouting.Execute(endpoint, new DomainCommandRequest(local, world, lifeEpoch, resourceId, payload));
            Guard(() => DomainCommands.Observe(endpoint, new DomainCommandResponse(request, world, resourceId, result)));
            return true;
        }
        if (_channel.State(Host()) != PeerState.Compatible) return false;
        _command.AskHost(DomainCommandRouting.Encode(endpoint, resourceId, lifeEpoch, payload), answer =>
        {
            // The answer belongs to the world that asked: a later world's observers never see it.
            if (Kernel?.WorldEpoch != world) return;
            var result = answer.Status switch
            {
                NetAnswerStatus.Answered => DomainCommandRouting.ReadResult(answer.Body),
                NetAnswerStatus.AlreadyRan => new DomainCommandResult(CommandStatuses.Failed, CommitStates.Unknown, NetworkCodes.MigrationCommitUnknown,
                    "The host that ran this command left before its answer arrived.", 0),
                NetAnswerStatus.WorldChanged => new DomainCommandResult(CommandStatuses.Failed, CommitStates.Unknown, NetworkCodes.StaleWorld, "", 0),
                _ => new DomainCommandResult(CommandStatuses.Failed, CommitStates.Unknown, NetworkCodes.Unanswered, "The host did not answer.", 0)
            };
            Guard(() => DomainCommands.Observe(endpoint, new DomainCommandResponse(request, world, resourceId, result)));
        });
        return true;
    }

    private void ServeCommand(NetAsk ask)
    {
        var kernel = Kernel;
        DomainCommandResult result;
        if (kernel == null || !_channel.IsHost)
            result = DomainCommandRouting.Refused(NetworkCodes.NoConsumer, "This machine is not the running host.");
        else if (!DomainCommandRouting.TryDecode(ask.Body, out var endpoint, out var resourceId, out var lifeEpoch, out var payload))
            result = DomainCommandRouting.Refused(NetworkCodes.FormatInvalid, "The domain request is malformed.");
        else result = DomainCommandRouting.Execute(endpoint, new DomainCommandRequest(ask.From, kernel.WorldEpoch, lifeEpoch, resourceId, payload));
        Log?.Request(Role, "domain", result.Status, result.Code);
        ask.Answer(DomainCommandRouting.Encode(result));
    }

    // ---- owner steps ----------------------------------------------------------------------------------------------------------

    /// <summary>An owner message arrived (<c>OwnerCommandChannel</c> reads it).</summary>
    internal Action<ulong, byte[]>? OwnerReceived { get; set; }

    internal void SendOwner(ulong to, byte[] body) => _owner.To(to, body);

    internal bool SendOwnerToHost(byte[] body) => _owner.ToHost(body);

    // ---- migration image ------------------------------------------------------------------------------------------------------

    /// <summary>The host keeps its image of one native migration buffer in the table: the head, the checkpoint and one row per
    /// part of the kernel's image, so the next capture sends only the parts that changed. The ledger of the commands it has run
    /// travels with every capture.</summary>
    internal void PublishMigration(byte buffer, string runtime, string? checkpoint)
    {
        var names = new List<string>();
        var parts = new List<byte[]>();
        var held = Encoding.UTF8.GetBytes(checkpoint ?? "null");
        long size = held.Length;
        foreach (var part in RuntimeJson.Parse(runtime).EnumerateObject())
        {
            names.Add(part.Name);
            parts.Add(Encoding.UTF8.GetBytes(part.Value.GetRawText()));
            size += parts[^1].Length;
        }
        if (size > NetworkProtocol.MaximumMigrationBytes || parts.Count > MigrationRows - PartRows)
            throw new RuntimeContractException(NetworkCodes.PayloadTooLarge, "The migration image of " + size + " bytes in " + parts.Count + " parts exceeds what one buffer carries.");
        var first = buffer * MigrationRows;
        _migration.Set(first + HeadRow, JsonSerializer.SerializeToUtf8Bytes(new MigrationHead(FormatVersion.Current, names)));
        _migration.Set(first + CheckpointRow, held);
        _migration.Set(LedgerRow, _command.CaptureLedger());
        for (var index = 0; index < parts.Count; index++) _migration.Set(first + PartRows + index, parts[index]);
        for (var index = PartRows + parts.Count; _migration.Remove(first + index); index++) { }
    }

    /// <summary>Whether this machine holds any migration image at all.</summary>
    internal bool HoldsMigration => _migration.Rows.Keys.Any(static row => row != LedgerRow);

    /// <summary>The host has no image of this buffer any more: a migration that recalls it is refused as missing its state.</summary>
    internal void DropMigration(byte buffer)
    {
        if (!_channel.IsHost) return;
        foreach (var key in _migration.Rows.Keys)
            if (key != LedgerRow && key / MigrationRows == buffer) _rows.Add(key);
        foreach (var key in _rows) _migration.Remove(key);
        _rows.Clear();
    }

    /// <summary>The image of one buffer as this machine holds it; false when it is missing or not complete.</summary>
    internal bool TryReadMigration(byte buffer, out string runtime, out string? checkpoint)
    {
        runtime = "";
        checkpoint = null;
        var first = buffer * MigrationRows;
        if (!_migration.TryGet(first + HeadRow, out var head) || !_migration.TryGet(first + CheckpointRow, out var held)) return false;
        MigrationHead? parsed;
        try { parsed = JsonSerializer.Deserialize<MigrationHead>(head); }
        catch (JsonException) { return false; }
        if (parsed?.Parts == null || parsed.SchemaVersion != FormatVersion.Current) return false;
        var text = new StringBuilder("{");
        for (var index = 0; index < parsed.Parts.Count; index++)
        {
            if (!_migration.TryGet(first + PartRows + index, out var part)) return false;
            if (index != 0) text.Append(',');
            text.Append(JsonSerializer.Serialize(parsed.Parts[index])).Append(':').Append(Encoding.UTF8.GetString(part));
        }
        runtime = text.Append('}').ToString();
        var saved = Encoding.UTF8.GetString(held);
        checkpoint = saved == "null" ? null : saved;
        return true;
    }
}

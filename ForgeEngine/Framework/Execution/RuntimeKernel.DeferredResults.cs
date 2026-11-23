using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>A stable address for one target's terminal result after its action handler returned.</summary>
public sealed record DeferredCommandToken(string CommandId, long WorldEpoch, EntityReference Target);

/// <summary>Pre-resolves the same entity currency read exposed by a registered resolver. The resolver delegate's
/// target implements this interface; binding is read-only and runs at admission and lifecycle boundaries, never per tick. The
/// returned probe must observe the exact reference's current life, including removal and replacement.</summary>
internal interface IRuntimeEntityLifetimeResolver
{
    Func<bool> BindLifetime(EntityReference reference);
}

public sealed partial class RuntimeModuleHandle
{
    /// <summary>Publishes one terminal result. False means this exact target already reached a terminal result.</summary>
    public bool CompleteDeferredResult(DeferredCommandToken token, CommandResult result)
        => kernel.CompleteDeferredResult(this, token, result);

    /// <summary>Publishes one terminal result whose row carries the four fixed columns only: the row's conclusion is
    /// the command's. False means this exact target already reached a terminal result.</summary>
    public bool CompleteDeferredResult(DeferredCommandToken token, Status status, Commit commit, string code)
    {
        var conclusion = new Outcome(status, commit, code);
        return kernel.CompleteDeferredResult(this, token, ref conclusion, RuntimeKernel.ConcludeDeferredRow);
    }

    /// <summary>Publishes one terminal result through the typed row writer: <paramref name="row"/> is handed the
    /// terminal row, already begun for the token's target, writes its own columns by the block's <c>Row…</c>
    /// constants and ends it. The row's status, commit state and code are the command's conclusion, which has to be
    /// terminal (`succeeded`, `failed`, `cancelled` or `expired`); a row the writer refuses is thrown to the caller
    /// with the row ABI's code and completes nothing. False means this exact target already reached a terminal
    /// result.</summary>
    public bool CompleteDeferredResult<TState>(DeferredCommandToken token, ref TState state, TargetStep<TState> row)
        => kernel.CompleteDeferredResult(this, token, ref state, row);
}

public sealed partial class RuntimeKernel
{
    private long commandInvocationSequence;

    private void BeginCommandInvocation(ref CommandContext context, bool setCurrent = true)
    {
        RuntimeJson.Require(commandInvocationSequence < long.MaxValue, "command-context-capacity",
            "The command invocation identity space is exhausted.");
        var stamp = ++commandInvocationSequence;
        context.BeginInvocation(executor.Invocation, stamp);
        if (setCurrent) currentCommand = context;
    }

    private void EndCommandInvocation(in CommandContext context)
    {
        executor.Invocation.Expire(context.InvocationStamp);
        if (currentCommand is { } active && active.InvocationStamp == context.InvocationStamp) currentCommand = null;
    }

    internal readonly record struct DeferredKey(string CommandId, long WorldEpoch, string TargetId,
        long TargetWorldEpoch, long TargetLifeEpoch);

    internal sealed class DeferredCommand
    {
        internal DeferredCommandToken Token = null!;
        internal string Provider = "";
        internal long ProviderGeneration;
        internal RuntimeEvent Origin = null!;
        internal RuntimeLogPlan Plan;
        internal string Entry = "";
        internal string Node = "";
        internal string Binding = "";
        internal string ResultPort = "";
        internal EntityReference Target = null!;
        internal JsonElement InitialRow;
        internal ResultRowDescriptor Descriptor = null!;
        internal JsonElement DetachedContract;
        internal bool RowSet;
        internal bool Started;
        internal bool Detached;
        internal Func<EntityReference, bool>? ResolveTarget;
        internal Func<bool>? TargetCurrent;
        /// <summary>Set when the target's kind reports its ends: the sweep then reads the end reports, and neither the
        /// resolver nor a bound probe is asked per tick.</summary>
        internal EntityCheck? ReportedTarget;
        internal string? BindingEndCode;
    }

    internal static DeferredKey Key(DeferredCommandToken token)
        => new(token.CommandId, token.WorldEpoch, token.Target.Id, token.Target.WorldEpoch, token.Target.LifeEpoch);

    internal DeferredCommandToken BeginDeferredResult(CommandContext context, EntityReference target)
    {
        ReadThread();
        RuntimeJson.Require(ReferenceEquals(context.DeferredKernel, this)
            && (context.DetachedDeferredContext || context.InvocationActive
                && currentCommand is { } active && active.InvocationStamp == context.InvocationStamp),
            "deferred-result-context", "A deferred result must start in its own action handler.");
        RuntimeJson.Require(context.IsHost && world.worldHost != false, "not-host", context.CommandId);
        RuntimeJson.Require(registry.IsRegistered(context.DeferredProvider, context.DeferredProviderGeneration),
            "module-unregistered", context.DeferredProvider);
        entityLives.CheckEntity(target);
        var token = new DeferredCommandToken(context.CommandId, WorldEpoch, target);
        var key = Key(token);
        RuntimeJson.Require(!kernelScheduler.deferredCommands.ContainsKey(key) && !kernelScheduler.deferredCompleted.ContainsKey(key),
            "deferred-result-duplicate", context.CommandId);
        RuntimeJson.Require(kernelScheduler.deferredCommands.Count + kernelScheduler.deferredCompleted.Count < MaximumEventHistory,
            "deferred-result-budget", context.CommandId);
        var entry = new DeferredCommand
        {
            Token = token,
            Provider = context.DeferredProvider,
            ProviderGeneration = context.DeferredProviderGeneration,
            Origin = context.DeferredOrigin,
            Plan = new RuntimeLogPlan
            {
                PlanId = context.PlanId,
                ResourceId = context.ResourceId,
                ResourceRevision = context.ResourceRevision
            },
            Entry = context.DeferredEntry,
            Node = context.NodeId,
            Binding = context.DeferredBinding,
            ResultPort = context.DeferredResultPort,
            Target = target,
            InitialRow = BasicRow(target, CommandStatuses.Partial, CommitStates.Confirmed, ResultCode.Partial),
            Descriptor = context.DeferredResultDescriptor
                ?? throw new RuntimeContractException("deferred-result-schema", context.CommandId),
            DetachedContract = context.DeferredContract,
            RowSet = true,
            Started = false,
            Detached = context.DetachedDeferredContext
        };
        kernelScheduler.deferredCommands.Add(key, entry);
        kernelScheduler.deferredEnding.EnsureCapacity(kernelScheduler.deferredCommands.Count);
        BindDeferredResult(entry);
        context.AddDeferredResult(token);
        return token;
    }

    internal bool CompleteDeferredResult(RuntimeModuleHandle owner, DeferredCommandToken token, CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (AdmitDeferredCompletion(owner, token) is not { } entry) return false;
        var normalized = NormalizeDeferredResult(entry, result);
        FinishDeferred(entry, normalized);
        return true;
    }

    /// <summary>The pending obligation one completion is for, or null where the target already reached a terminal
    /// result — either earlier, or just now because its life, binding or world ended first.</summary>
    private DeferredCommand? AdmitDeferredCompletion(RuntimeModuleHandle owner, DeferredCommandToken token)
    {
        ReadThread(); ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(token);
        ValidateToken(token);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        var key = Key(token);
        if (kernelScheduler.deferredCompleted.TryGetValue(key, out var completedProvider))
        {
            RuntimeJson.Require(completedProvider == owner.ProviderId, "deferred-result-owner", token.CommandId);
            return null;
        }
        RuntimeJson.Require(token.WorldEpoch == WorldEpoch, "stale-world", token.CommandId);
        AcceptRuntimeWork();
        RuntimeJson.Require(!migrationPaused, "migration-paused", token.CommandId);
        RuntimeJson.Require(world.worldHost == true, "not-host", token.CommandId);
        RuntimeJson.Require(kernelScheduler.deferredCommands.TryGetValue(key, out var entry),
            "deferred-result-unknown", token.CommandId);
        RuntimeJson.Require(entry!.Provider == owner.ProviderId && entry.ProviderGeneration == owner.Generation,
            "deferred-result-owner", token.CommandId);
        RuntimeJson.Require(entry.Started, "deferred-result-not-started", token.CommandId);
        if (DeferredEndCode(entry) is { } ended)
        {
            FinishDeferred(entry, AutomaticResult(entry, ended));
            return null;
        }
        return entry;
    }

    internal static readonly TargetStep<Outcome> ConcludeDeferredRow = static (ref Outcome conclusion, EntityReference target, int index, RowWriter rows)
        => rows.End(conclusion.Status, conclusion.Commit, conclusion.Code);

    /// <summary>The terminal row of a deferred result is written into a region of its own — a completion may run
    /// inside another command's handler, whose rows are in the activation's — through an invocation of its own.</summary>
    private FrameSpace? deferredRows;
    private readonly CommandInvocationState deferredInvocation = new();
    private long deferredInvocationSequence;

    /// <summary>
    /// The typed completion: the terminal row is written by the same writer and the same rules a handler's rows are,
    /// straight into a row region, and the receipt's document is rendered from that region once. Nothing is parsed,
    /// and no row object or intermediate document is made. Where nothing outside the kernel reads a receipt — the
    /// host's per-frame advance takes none and no behavior trace is observed — the document is not rendered at all:
    /// the terminal result carries its status, commit and code, and its log level is read from the region.
    /// </summary>
    internal bool CompleteDeferredResult<TState>(RuntimeModuleHandle owner, DeferredCommandToken token, ref TState state, TargetStep<TState> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (AdmitDeferredCompletion(owner, token) is not { } entry) return false;
        RuntimeJson.Require(deferredInvocation.Stamp == 0, "deferred-result-context", "A deferred result is completed outside another completion's row.");
        var row = entry.Descriptor;
        var space = deferredRows ??= new FrameSpace(64, MaximumEventPayloadBytes);
        if (row.Slot + row.Stride > space.SlotCapacity) space.Grow(row.Slot + row.Stride);
        space.Strings.Reset();
        var stamp = ++deferredInvocationSequence;
        deferredInvocation.Reset(stamp);
        var write = deferredInvocation.Rows;
        write.ArmSingle(space, row.Slot, row, entry.Target, WorldEpoch);
        CommandResult result;
        var elided = !kernelScheduler.collectReceipts && behaviorTraceObservers.Count == 0;
        try
        {
            var rows = new RowWriter(deferredInvocation, stamp);
            rows.Begin(0);
            step(ref state, entry.Target, 0, rows);
            if (write.ErrorCode == null && write.Open >= 0) write.Fail(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row 0 was begun and never ended.");
            if (write.ErrorCode != null) throw new RuntimeContractException(write.ErrorCode, write.ErrorDetail ?? "");
            RuntimeJson.Require(write.Count == 1, RuntimeAbiCodes.ResultRowIncomplete, row.Port + " carries no terminal row.");
            RuntimeJson.Require(write.LastStatus is Status.Succeeded or Status.Failed or Status.Cancelled or Status.Expired,
                "deferred-result-terminal", entry.Token.CommandId);
            if (elided) result = Outcomes.Result(new Outcome(write.LastStatus, write.LastCommit, write.LastCode!));
            else
            {
                executor.renderBuffer.Clear();
                executor.renderWriter.Reset(executor.renderBuffer);
                executor.renderWriter.WriteStartObject();
                executor.renderWriter.WritePropertyName(row.PortUtf8);
                ResultRowAbi.WriteSet(executor.renderWriter, new Frame(space.Slots, row.Slot, space.Strings.Memory), row, 1, entry.RowSet, settled: false);
                executor.renderWriter.WriteEndObject();
                executor.renderWriter.Flush();
                result = CommandResult.Create(Outcomes.StatusNames[(int)write.LastStatus], Outcomes.CommitNames[(int)write.LastCommit],
                    write.LastCode!, "", RuntimeJson.ParseWritten(executor.renderBuffer.WrittenMemory));
            }
        }
        finally { deferredInvocation.Expire(stamp); }
        FinishDeferred(entry, result, elided ? new ResultRowSet(row, 1, entry.RowSet) { Space = space } : null);
        return true;
    }

    private static void ValidateToken(DeferredCommandToken token)
    {
        RuntimeJson.CommandId(token.CommandId); RuntimeJson.Integer(token.WorldEpoch);
        var target = token.Target ?? throw new RuntimeContractException("deferred-result-token", "Deferred target is missing.");
        RuntimeEntityReferences.Validate(target);
        RuntimeJson.Require(token.WorldEpoch == target.WorldEpoch,
            "deferred-result-token", "Deferred command and target worlds differ.");
    }

    private string? DeferredEndCode(DeferredCommand entry)
    {
        if (entry.Target.WorldEpoch != WorldEpoch) return "stale-world";
        if (entry.BindingEndCode != null) return entry.BindingEndCode;
        if (entry.ReportedTarget is { } reported) return entityLives.EntityLifeCode(in reported);
        try { return (entry.TargetCurrent?.Invoke() ?? entry.ResolveTarget!(entry.Target)) ? null : "stale-entity"; }
        catch (Exception error)
        {
            log.LogCallbackFailed("entity-resolver-failed target=" + entry.Target.Id, error);
            return "entity-resolver-failed";
        }
    }

    private void BindDeferredResult(DeferredCommand entry)
    {
        entry.ResolveTarget = null; entry.TargetCurrent = null; entry.ReportedTarget = null; entry.BindingEndCode = null;
        if (!registry.IsRegistered(entry.Provider, entry.ProviderGeneration))
        { entry.BindingEndCode = "binding-lifecycle"; return; }
        if (!entry.Detached && (!plans.Loaded.TryGetValue(entry.Plan.PlanId, out var loaded)
            || loaded.Plan.ResourceId != entry.Plan.ResourceId || loaded.Plan.ResourceRevision != entry.Plan.ResourceRevision))
        { entry.BindingEndCode = "binding-lifecycle"; return; }
        if (!entityLives.TryEntityCheck(entry.Target, out var target))
        { entry.BindingEndCode = "entity-resolver"; return; }
        var resolver = target.Resolve;
        entry.ResolveTarget = resolver;
        if (target.Kind.Reported) { entry.ReportedTarget = target; return; }
        if (resolver.Target is not IRuntimeEntityLifetimeResolver lifetime) return;
        bool wasObserving = observingEntities;
        observingEntities = true;
        try
        {
            entry.TargetCurrent = lifetime.BindLifetime(entry.Target)
            ?? throw new InvalidOperationException("Entity lifetime resolver returned no probe.");
        }
        catch (Exception error)
        {
            log.LogCallbackFailed("entity-resolver-failed target=" + entry.Target.Id, error);
            entry.BindingEndCode = "entity-resolver-failed";
        }
        finally { observingEntities = wasObserving; }
    }

    private void BindDeferredResults()
    {
        foreach (var pair in kernelScheduler.deferredCommands) BindDeferredResult(pair.Value);
    }

    private static bool Terminal(CommandResult result)
        => result.Status is CommandStatuses.Succeeded or CommandStatuses.Failed
            or CommandStatuses.Cancelled or CommandStatuses.Expired;

    private CommandResult NormalizeDeferredResult(DeferredCommand entry, CommandResult result)
    {
        RuntimeJson.Require(CommandResultRules.TryValidate(result, out _) && Terminal(result),
            "deferred-result-terminal", entry.Token.CommandId);
        RuntimeJson.Require(result.Facts.Count == 0, "deferred-result-facts",
            "Deferred terminal results cannot publish facts outside their original dispatch.");
        RuntimeJson.Require(TryTargetRow(result.Outputs, entry.ResultPort, entry.Token.Target, out var row, out var set),
            "deferred-result-target", entry.Token.CommandId);
        RuntimeJson.Require(RuntimeJson.Text(row, "status") == result.Status
            && RuntimeJson.Text(row, "committed") == result.CommitState
            && RuntimeJson.Text(row, "code") == result.Code,
            "deferred-result-target", "The result row and command conclusion differ.");
        ValidateDeferredRow(entry.Descriptor, row, entry.Token.Target);
        var outputs = DeferredOutputs(entry.ResultPort, row, set, entry.Target, result.Status, result.CommitState, result.Code);
        var normalized = CommandResult.Create(result.Status, result.CommitState, result.Code, result.Detail,
            outputs, result.Facts.ToArray());
        normalized.CostSettlementLogDetail = result.CostSettlementLogDetail;
        return normalized;
    }

    private CommandResult AutomaticResult(DeferredCommand entry, string code)
    {
        var failed = code is "entity-resolver" or "entity-resolver-failed";
        var status = failed ? CommandStatuses.Failed : CommandStatuses.Cancelled;
        var commit = failed ? CommitStates.Unknown : CommitStates.None;
        var outputs = DeferredOutputs(entry.ResultPort, entry.InitialRow, entry.RowSet,
            entry.Target, status, commit, code);
        return CommandResult.Create(status, commit, code, "", outputs);
    }

    private void FinishDeferred(DeferredCommand entry, CommandResult result, ResultRowSet? rows = null)
    {
        var key = Key(entry.Token);
        if (!kernelScheduler.deferredCommands.Remove(key)) return;
        kernelScheduler.deferredCompleted[key] = entry.Provider;
        kernelScheduler.deferredReceipts.Enqueue(new CommandReceipt(entry.Token.CommandId, entry.Origin.EventId,
            entry.Origin.CauseId, entry.Origin.RootEventId ?? entry.Origin.EventId,
            entry.Plan.PlanId, entry.Plan.ResourceId, entry.Plan.ResourceRevision,
            entry.Node, entry.Binding, WorldEpoch, CurrentTick, result));
        var origin = new KernelLog.StepOrigin(in entry.Origin, in entry.Plan, entry.Entry); var command = StepCommand.Of(entry.Token.CommandId);
        log.LogStepFinished(in origin, null, entry.Provider, entry.Node, entry.Binding,
            ref command, "action", in result, rows: rows);
        // The behavior trace saw the dispatch with a partial row; this is the same command's terminal row, so an observer
        // that waits for the real outcome receives it instead of waiting for a record that is never published.
        TraceStep("after", in origin, entry.Provider, entry.Node, entry.Binding, ref command, "action",
            outputs: result.Outputs, result: TraceResult(in result));
    }

    private void CleanDeferredResults()
    {
        if (kernelScheduler.deferredCommands.Count == 0) return;
        kernelScheduler.deferredEnding.Clear();
        foreach (var pair in kernelScheduler.deferredCommands)
            if (pair.Value.Started && DeferredEndCode(pair.Value) is { } code) kernelScheduler.deferredEnding.Add((pair.Value, code));
        for (var i = 0; i < kernelScheduler.deferredEnding.Count; i++)
        {
            var (entry, code) = kernelScheduler.deferredEnding[i];
            FinishDeferred(entry, AutomaticResult(entry, code));
        }
        kernelScheduler.deferredEnding.Clear();
    }

    private void EndDeferredResults(string? provider, string code)
    {
        foreach (var entry in kernelScheduler.deferredCommands.Values.Where(entry => entry.Started
            && (provider == null || entry.Provider == provider)).ToArray())
            FinishDeferred(entry, AutomaticResult(entry, code));
        foreach (var entry in kernelScheduler.deferredCommands.Where(pair => !pair.Value.Started
            && (provider == null || pair.Value.Provider == provider)).Select(pair => pair.Key).ToArray())
            kernelScheduler.deferredCommands.Remove(entry);
    }

    private static JsonElement BasicRow(EntityReference target, string status, string commit, string code)
        => RuntimeJson.From(new { target, status, committed = commit, code });

    private static void ValidateDeferredRow(ResultRowDescriptor descriptor, JsonElement row, EntityReference target)
    {
        var slots = new FrameValue[descriptor.Stride];
        var strings = new FrameStrings(MaximumEventPayloadBytes);
        var writer = new FrameWriter(slots, 0, descriptor.Stride, strings);
        var recipient = RuntimeJson.From(target);
        ResultRowAbi.Write(ref writer, descriptor, 0, row, recipient);
        ResultRowAbi.ValidatePairing(descriptor, 0, row);
    }

    private static bool Same(EntityReference left, EntityReference right)
        => left.Id == right.Id && left.WorldEpoch == right.WorldEpoch && left.LifeEpoch == right.LifeEpoch;

    internal static bool TryTargetRow(JsonElement outputs, string port, EntityReference target,
        out JsonElement row, out bool set)
    {
        row = default; set = false;
        if (port.Length == 0 || outputs.ValueKind != JsonValueKind.Object
            || !outputs.TryGetProperty(port, out var carried)) return false;
        if (carried.ValueKind == JsonValueKind.Array)
        {
            set = true;
            foreach (var item in carried.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("target", out var value)
                    && Same(RuntimeJson.EntityRow(value), target))
                { if (row.ValueKind != JsonValueKind.Undefined) return false; row = item; }
            return row.ValueKind != JsonValueKind.Undefined;
        }
        if (carried.ValueKind != JsonValueKind.Object || !carried.TryGetProperty("target", out var one)
            || !Same(RuntimeJson.EntityRow(one), target)) return false;
        row = carried; return true;
    }

    private static JsonElement DeferredOutputs(string port, JsonElement source, bool set, EntityReference target,
        string status, string commit, string code)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WritePropertyName(port);
            if (set) writer.WriteStartArray();
            writer.WriteStartObject();
            var wroteTarget = false; var wroteStatus = false; var wroteCommit = false; var wroteCode = false;
            if (source.ValueKind == JsonValueKind.Object)
                foreach (var field in source.EnumerateObject())
                {
                    writer.WritePropertyName(field.Name);
                    switch (field.Name)
                    {
                        case "target": RuntimeJson.From(target).WriteTo(writer); wroteTarget = true; break;
                        case "status": writer.WriteStringValue(status); wroteStatus = true; break;
                        case "committed": writer.WriteStringValue(commit); wroteCommit = true; break;
                        case "code": writer.WriteStringValue(code); wroteCode = true; break;
                        default: field.Value.WriteTo(writer); break;
                    }
                }
            if (!wroteTarget) { writer.WritePropertyName("target"); RuntimeJson.From(target).WriteTo(writer); }
            if (!wroteStatus) writer.WriteString("status", status);
            if (!wroteCommit) writer.WriteString("committed", commit);
            if (!wroteCode) writer.WriteString("code", code);
            writer.WriteEndObject();
            if (set) writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return RuntimeJson.Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    internal static object TokenValue(DeferredCommandToken token) => new
    { commandId = token.CommandId, worldEpoch = token.WorldEpoch, target = token.Target };

    internal sealed record DeferredImage(IReadOnlyList<DeferredCommand> Active,
        IReadOnlyDictionary<DeferredKey, string> Completed, IReadOnlyList<CommandReceipt> Receipts);

    private DeferredImage ReadDeferredResults(JsonElement state, long sourceEpoch, long targetEpoch)
    {
        RuntimeJson.Shape(state, "active completed receipts");
        var active = new List<DeferredCommand>();
        var completed = new Dictionary<DeferredKey, string>();
        var keys = new HashSet<DeferredKey>();
        foreach (var row in RuntimeJson.Rows(state, "active", MaximumEventHistory))
        {
            RuntimeJson.Shape(row, "token provider origin planId resourceId resourceRevision entry node binding resultPort target initialRow rowSet detached contract");
            var sourceToken = ReadDeferredToken(row.GetProperty("token"));
            RuntimeJson.Require(sourceToken.WorldEpoch == sourceEpoch, "migration-deferred-result",
                "Deferred token belongs to another authority world.");
            var provider = RuntimeJson.Text(row, "provider");
            if (!(registry.IsRegistered(provider, registry.Modules.TryGetValue(provider, out var generation) ? generation : -1))) throw new RuntimeContractException("migration-deferred-result", "Deferred provider is not registered: " + provider);
            var sourceTarget = RuntimeJson.Entity(row.GetProperty("target"));
            RuntimeJson.Require(sourceTarget.WorldEpoch == sourceEpoch && Same(sourceToken.Target, sourceTarget),
                "migration-deferred-result", "Deferred token and target do not name the same source life.");
            var target = targetEpoch == sourceEpoch ? sourceTarget
                : sourceTarget with { WorldEpoch = targetEpoch };
            var token = targetEpoch == sourceEpoch ? sourceToken
                : new DeferredCommandToken(sourceToken.CommandId, targetEpoch, target);
            var detached = row.GetProperty("detached").GetBoolean();
            var resultPort = RuntimeJson.Text(row, "resultPort");
            var plan = new RuntimeLogPlan
            {
                PlanId = RuntimeJson.Text(row, "planId"),
                ResourceId = RuntimeJson.Text(row, "resourceId"),
                ResourceRevision = RuntimeJson.Text(row, "resourceRevision")
            };
            JsonElement contract = default;
            ResultRowDescriptor descriptor;
            if (detached)
            {
                contract = row.GetProperty("contract");
                RuntimeJson.Require(contract.ValueKind == JsonValueKind.Object,
                    "migration-deferred-result", "Detached deferred result lost its contract.");
                var port = RuntimeGraphContracts.PortOf(contract, "outputs", resultPort);
                RuntimeJson.Require(port != null, "migration-deferred-result", "Deferred result port is missing.");
                descriptor = ResultRowAbi.Compile(port!.Value, contract, 0, 1);
            }
            else
            {
                RuntimeJson.Require(plans.Loaded.TryGetValue(plan.PlanId, out var loaded)
                    && loaded.Plan.ResourceId == plan.ResourceId && loaded.Plan.ResourceRevision == plan.ResourceRevision,
                    "migration-deferred-result", "Deferred result lost its loaded plan.");
                var entryId = RuntimeJson.Text(row, "entry");
                var node = RuntimeJson.Text(row, "node");
                var binding = RuntimeJson.Text(row, "binding");
                var step = loaded!.Plan.Entries.SingleOrDefault(candidate => candidate.NodeId == entryId)?.Steps
                    .SingleOrDefault(candidate => candidate.NodeId == node);
                RuntimeJson.Require(step?.ProviderId == provider && step.BindingId == binding
                    && step.Frames.Row?.Port == resultPort, "migration-deferred-result",
                    "Deferred result no longer names the same action step.");
                descriptor = step!.Frames.Row!;
            }
            var origin = ReadMigrationEvent(row.GetProperty("origin"));
            RuntimeJson.Require(origin.WorldEpoch == sourceEpoch, "migration-deferred-result",
                "Deferred origin belongs to another authority world.");
            if (targetEpoch != sourceEpoch) origin = RebaseEvent(origin, sourceEpoch, targetEpoch);
            var initial = row.GetProperty("initialRow");
            if (targetEpoch != sourceEpoch) initial = RebaseWorldEpoch(initial, sourceEpoch, targetEpoch);
            ValidateDeferredRow(descriptor, initial, target);
            var key = Key(token);
            RuntimeJson.Require(keys.Add(key), "migration-deferred-result", "Duplicate active deferred token.");
            active.Add(new DeferredCommand
            {
                Token = token,
                Provider = provider,
                ProviderGeneration = generation,
                Origin = origin,
                Plan = plan,
                Entry = RuntimeJson.Text(row, "entry"),
                Node = RuntimeJson.Text(row, "node"),
                Binding = RuntimeJson.Text(row, "binding"),
                ResultPort = resultPort,
                Target = target,
                InitialRow = initial.Clone(),
                RowSet = row.GetProperty("rowSet").GetBoolean(),
                Started = true,
                Detached = detached,
                DetachedContract = contract,
                Descriptor = descriptor
            });
        }
        foreach (var row in RuntimeJson.Rows(state, "completed", MaximumEventHistory))
        {
            RuntimeJson.Shape(row, "token provider");
            var sourceToken = ReadDeferredToken(row.GetProperty("token"));
            RuntimeJson.Require(sourceToken.WorldEpoch == sourceEpoch, "migration-deferred-result",
                "Completed deferred token belongs to another authority world.");
            var token = targetEpoch == sourceEpoch ? sourceToken : new DeferredCommandToken(sourceToken.CommandId,
                targetEpoch, sourceToken.Target with { WorldEpoch = targetEpoch });
            var key = Key(token); var provider = RuntimeJson.Text(row, "provider");
            RuntimeJson.Require(keys.Add(key), "migration-deferred-result",
                "Duplicate completed deferred token.");
            completed.Add(key, provider);
        }
        RuntimeJson.Require(active.Count + completed.Count <= MaximumEventHistory,
            "migration-deferred-result-budget", "Deferred result ledger exceeds its bound.");
        var receipts = new List<CommandReceipt>();
        foreach (var row in RuntimeJson.Rows(state, "receipts", MaximumEventHistory))
        {
            var receipt = ReadMigrationOwnerReceipt(row, sourceEpoch);
            if (targetEpoch != sourceEpoch)
                receipt = receipt with
                {
                    WorldEpoch = targetEpoch,
                    Result = RebaseDeferredResult(receipt.Result, sourceEpoch, targetEpoch)
                };
            receipts.Add(receipt);
        }
        return new DeferredImage(active, completed, receipts);
    }

    private static DeferredCommandToken ReadDeferredToken(JsonElement value)
    {
        RuntimeJson.Shape(value, "commandId worldEpoch target");
        var token = new DeferredCommandToken(RuntimeJson.Text(value, "commandId"),
            RuntimeJson.Integer(value.GetProperty("worldEpoch")), RuntimeJson.Entity(value.GetProperty("target")));
        ValidateToken(token);
        return token;
    }

    private static CommandResult RebaseDeferredResult(CommandResult result, long sourceEpoch, long targetEpoch)
    {
        var facts = result.Facts.Select(fact => new RuntimeFact(fact.BindingId,
            RebaseWorldEpoch(fact.Outputs, sourceEpoch, targetEpoch))).ToArray();
        return CommandResult.Create(result.Status, result.CommitState, result.Code, result.Detail,
            RebaseWorldEpoch(result.Outputs, sourceEpoch, targetEpoch), facts);
    }

    private void AdoptDeferredResults(DeferredImage image)
    {
        var priorCompleted = kernelScheduler.deferredCompleted.Where(entry => entry.Key.WorldEpoch != WorldEpoch).ToArray();
        var priorReceipts = kernelScheduler.deferredReceipts.Where(entry => entry.WorldEpoch != WorldEpoch).ToArray();
        kernelScheduler.deferredCommands.Clear(); kernelScheduler.deferredCompleted.Clear(); kernelScheduler.deferredReceipts.Clear(); kernelScheduler.deferredEnding.Clear();
        foreach (var entry in priorCompleted) kernelScheduler.deferredCompleted.Add(entry.Key, entry.Value);
        foreach (var receipt in priorReceipts) kernelScheduler.deferredReceipts.Enqueue(receipt);
        foreach (var entry in image.Active) kernelScheduler.deferredCommands.Add(Key(entry.Token), entry);
        kernelScheduler.deferredEnding.EnsureCapacity(kernelScheduler.deferredCommands.Count);
        foreach (var entry in image.Completed) kernelScheduler.deferredCompleted.Add(entry.Key, entry.Value);
        foreach (var receipt in image.Receipts) kernelScheduler.deferredReceipts.Enqueue(receipt);
        BindDeferredResults();
    }

    private static string DeferredCheckpointCore(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        RuntimeJson.Require(values.Remove("deferredResults"), "variable-checkpoint",
            "Checkpoint is missing deferred result state.");
        return JsonSerializer.Serialize(values);
    }
}

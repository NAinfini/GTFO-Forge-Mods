using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>Host decisions are sent to the mapped holder. Advance resumes only after a complete result;
/// native writes, identity imports and effect endings share the owner channel.</summary>
public sealed partial class RuntimeKernel
{
    /// <summary>The refusal for an owner step whose holder cannot be named. The tier refuses it by name, the same
    /// way the presentation tier refuses a step whose recipients reach no session: an action nobody can be found
    /// for is not a broadcast, and a silently accepted one would report a write that never happened. A resolved
    /// empty recipient set never gets here: the walk answers it `no-targets` before any tier is entered.</summary>
    public const string OwnerHolderCode = "owner-holder";

    /// <summary>A detached holder spends each application's already reserved terminal slot. The receipt stays
    /// in its receiver ledger, including unknown cleanup; reconnect and migration replay data, never cleanup.</summary>
    internal void DisconnectOwnerReceiver()
    {
        ReadThread();
        if (!world.worldStarted || StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        Thread(); SyncOwnerWorld();
        OwnerEndReporter = null;
        foreach (var entry in kernelOwner.ownerReceived.Entries.Where(row => row.IsApplication && !row.Application.Terminal
            && row.Result != null && row.Result.CommitState != CommitStates.None).ToArray())
        {
            var request = entry.Request;
            var control = request with
            {
                CommandId = request.CommandId + ":disconnect",
                EventId = request.CommandId,
                Operation = "cancel-command",
                Identities = RuntimeJson.EmptyObject,
                Data = OwnerCommandLedger.Data(RuntimeJson.EmptyObject, entry.Application.Slots, "cancel-command")
            };
            ExecuteOwnerOutput(control, request.Holder);
        }
    }

    internal IReadOnlyList<(OwnerOutput Request, CommandResult Result)> DisconnectedOwnerReceipts()
    {
        ReadThread();
        return kernelOwner.ownerReceived.Entries.Where(row => row.IsApplication && row.Application.Terminal
            && row.Request.WorldEpoch == WorldEpoch && row.Application.Controls.TryGetValue("cancel-command", out var control)
            && control.Request.CommandId == row.Request.CommandId + ":disconnect" && control.Result != null)
            .Select(row => (row.Request, row.Application.Controls["cancel-command"].Result!)).ToArray();
    }

    internal (OwnerOutput Request, CommandResult Result)? DisconnectedOwnerReceipt(string commandId)
    {
        ReadThread();
        return kernelOwner.ownerReceived.TryGet(commandId, out var row) && row.IsApplication && row.Application.Terminal
            && row.Request.WorldEpoch == WorldEpoch && row.Application.Controls.TryGetValue("cancel-command", out var control)
            && control.Request.CommandId == row.Request.CommandId + ":disconnect" && control.Result != null
                ? (row.Request, control.Result) : null;
    }

    internal void ReceiveDisconnectedOwnerEnd(OwnerOutput request, CommandResult result)
    {
        Thread();
        if (request.WorldEpoch != WorldEpoch || !kernelOwner.ownerIssued.TryGet(request.CommandId, out var application)
            || !application.IsApplication || !OwnerCommandLedger.Same(application.Request, request)) return;
        var control = request with
        {
            CommandId = request.CommandId + ":disconnect",
            EventId = request.CommandId,
            Operation = "cancel-command",
            Identities = RuntimeJson.EmptyObject,
            Data = OwnerCommandLedger.Data(RuntimeJson.EmptyObject, application.Application.Slots, "cancel-command")
        };
        if (application.Application.Controls.TryGetValue("cancel-command", out var prior)) control = prior.Request;
        else if (kernelOwner.ownerIssued.Admit(control, application.Application.Slots, out _, out _) != null) return;
        // ReceiveOwnerEnd's control branch preserves the first confirmed/unknown result and ignores duplicates.
        ReceiveOwnerEnd(control, EffectEndReasons.Reclaimed, result);
        if (kernelOwner.ownerFlights.ContainsKey(request.CommandId)) kernelOwner.ownerEarlyEnds.TryAdd(request.CommandId, result);
        EndDisconnectedOwnerEffects(request.CommandId);
    }

    private void EndDisconnectedOwnerEffects(string commandId)
    {
        foreach (var slot in kernelEffects.effects.Values.ToArray())
        {
            for (var index = slot.Layers.Count - 1; index >= 0; index--)
            {
                var layer = slot.Layers[index];
                if (layer.OwnerApplication?.CommandId == commandId)
                {
                    // A receipt settles only the application it names. Other accepted refreshes of this layer
                    // still own their cleanup reservations, even when they share its native writer identity.
                    layer.OwnerRestoreQueued = true;
                    layer.RestorePending = true;
                }
                for (var extra = layer.AdditionalOwnerApplications.Count - 1; extra >= 0; extra--)
                {
                    var application = layer.AdditionalOwnerApplications[extra].Request;
                    if (application.CommandId != commandId) continue;
                    if (OwnerEffectApplicationId(application) == layer.CommandId) layer.RestorePending = true;
                    layer.AdditionalOwnerApplications.RemoveAt(extra);
                }
                if (layer.OwnerRestoreQueued && layer.AdditionalOwnerApplications.Count == 0)
                    EndEffectLayer(slot, index, EffectEndReasons.Reclaimed, CurrentTick, alreadyRestored: true);
            }
            if (slot.Layers.Count == 0) kernelEffects.ownerEffects.Remove(slot.HandleId);
        }
    }

    /// <summary>Whether this step is the `owner` tier. Like the presentation tier, the tier is a registration
    /// fact of the capability the step's binding implements, read once when the plan loaded rather than inferred
    /// from a port or an id.</summary>
    private static bool IsOwnerStep(ResolvedStep step) => step.OwnerTier;

    /// <summary>
    /// The host's half of an owner step: the holder's session, then the intent the network layer sends. The
    /// step's own inputs are already resolved by the walk and are what travels, so the holder executes exactly
    /// what the host decided, on the same plan and the same step.
    /// </summary>
    private string? EnterOwner(LoadedPlan plan, ResolvedStep step, Pending pending, string commandId,
        JsonElement inputs, out CommandResult result)
    {
        result = null!;
        kernelOwner.ownerSplit = null;
        if (kernelOwner.ownerFlights.Count >= Limits.MaxLiveHandles)
        { result = CommandResult.Rejected("owner-pending-budget"); return "owner-pending-budget"; }
        var provider = step.ProviderId;
        var stage = "registration";
        RuntimeContractException? failure;
        string holder;
        try { failure = ResolveHolder(provider, step, inputs, out holder, out stage); }
        catch (Exception error)
        {
            TraceOwnerHolderRefusal(plan, step, pending, inputs,
                error is RuntimeContractException refused ? refused.Code : "precondition-failed", stage + "-threw", error);
            throw;
        }
        if (failure != null)
        {
            TraceOwnerHolderRefusal(plan, step, pending, inputs, failure.Code, stage);
            result = CommandResult.Rejected(failure.Code, failure.Message);
            return failure.Code;
        }
        var payload = RawJson.Bytes(inputs);
        if (payload.Length > MaximumPresentationInputBytes)
        {
            result = CommandResult.Rejected(RuntimeAbiCodes.PresentationInputBudget, step.NodeId);
            return RuntimeAbiCodes.PresentationInputBudget;
        }
        JsonElement effectData;
        try { effectData = OwnerEffectData(step, plan.Plan.Id, inputs, commandId, pending.Provider, pending.Event.ScopeId); }
        catch (RuntimeContractException error)
        { result = CommandResult.Rejected(error.Code); return error.Code; }
        // A many-recipient step whose recipients live on several machines is one request per machine: each holder
        // receives the inputs with the recipient port narrowed to the recipients it simulates, in port order, and
        // answers one row for each; the walk resumes on the merge. A holder is handed the identities of its own
        // recipients only, so it is never asked to admit a player it neither simulates nor was told about.
        var holders = SplitOwnerHolders(step, inputs, holder, out var rowHolder, out var recipientPort);
        if (holders != null && kernelOwner.ownerFlights.Count + holders.Length > Limits.MaxLiveHandles)
        { result = CommandResult.Rejected("owner-pending-budget"); return "owner-pending-budget"; }
        var requests = new List<OwnerOutput>(holders?.Length ?? 1);
        foreach (var target in holders ?? new[] { holder })
        {
            var identity = kernelOwner.OwnerSession(target);
            var own = holders == null ? inputs : OwnRecipients(inputs, recipientPort!, rowHolder, target);
            var request = new OwnerOutput(provider, plan.Plan.Id, step.NodeId, requests.Count == 0 ? commandId : commandId + ":m" + requests.Count,
                step.BindingId, step.Frames.Index, target, pending.Event.EventId, pending.Event.ScopeId, WorldEpoch,
                CurrentTick, holders == null ? RawJson.Snapshot(inputs, payload) : RawJson.Snapshot(own, RawJson.Bytes(own)))
            {
                Identities = kernelOwner.ExportOwnerIdentities(identity, own),
                HolderIdentity = identity,
                Data = OwnerCommandLedger.Data(effectData, kernelOwner.OwnerCleanupSlots(step))
            };
            var admission = kernelOwner.ownerIssued.Admit(request, kernelOwner.OwnerCleanupSlots(step), out _, out _);
            if (admission != null) { result = admission; return admission.Code; }
            requests.Add(request);
        }
        kernelOwner.ownerOutputs.AddRange(requests);
        if (holders != null) kernelOwner.ownerSplit = new OwnerSplit(holders, rowHolder, requests);
        // This internal admission value is never a command receipt. The activation waits for the holder.
        result = Dispatched;
        return null;
    }

    /// <summary>The distinct holders of an owner step whose many-recipient port names players on more than one
    /// machine, the step's own holder first, or null when one machine holds every recipient (or the step is not a
    /// plain row-writing one: an effect or a returned handle stays with the one holder that owns its cleanup).
    /// <paramref name="rowHolder"/> is the holder of each recipient in port order and <paramref name="port"/> that
    /// input port. A recipient no holder can be named for stays with the step's own holder, which refuses it by name.</summary>
    private string[]? SplitOwnerHolders(ResolvedStep step, JsonElement inputs, string primary, out string[] rowHolder, out string? port)
    {
        rowHolder = Array.Empty<string>();
        port = null;
        if (step.Frames.Row == null || step.Effect != null || !registry.Tables.OwnerSessions.TryGetValue(step.CapabilityId, out var registered)
            || registered.Holder == null || step.ReturnsHandle) return null;
        RoutingSubject(step, inputs, out port);
        if (port == null || !inputs.TryGetProperty(port, out var recipients) || recipients.ValueKind != JsonValueKind.Array) return null;
        return SplitHolders(recipients, primary, registered.Holder, out rowHolder);
    }

    /// <summary>The step's inputs as one holder receives them: the recipient port keeps only the recipients
    /// <paramref name="holder"/> simulates, in port order, and every other input is unchanged.</summary>
    internal static JsonElement OwnRecipients(JsonElement inputs, string port, string[] rowHolder, string holder)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in inputs.EnumerateObject()) fields[field.Name] = field.Value;
        var own = new List<JsonElement>();
        var index = 0;
        foreach (var item in inputs.GetProperty(port).EnumerateArray())
            if (rowHolder[index++] == holder) own.Add(item);
        fields[port] = RuntimeJson.From(own);
        return RuntimeJson.From(fields);
    }

    internal static string[]? SplitHolders(JsonElement recipients, string primary, Func<EntityReference, string?> holderOf, out string[] rowHolder)
    {
        var holders = new List<string> { primary };
        var assigned = new string[recipients.GetArrayLength()];
        var index = 0;
        var routed = false;
        foreach (var item in recipients.EnumerateArray())
        {
            var holder = primary;
            if (item.ValueKind == JsonValueKind.Object)
            {
                var entity = RuntimeJson.EntityRow(item);
                // The first named recipient is the one the step's own holder was resolved for.
                if (string.IsNullOrEmpty(entity.Id)) { }
                else if (!routed) routed = true;
                else if (holderOf(entity) is { Length: > 0 } answer) holder = answer;
            }
            assigned[index++] = holder;
            if (!holders.Contains(holder)) holders.Add(holder);
        }
        rowHolder = assigned;
        return holders.Count == 1 ? null : holders.ToArray();
    }

    /// <summary>The rows of the holders' answers in recipient order, each from the holder that simulates that
    /// recipient (a holder answers its own recipients only, in port order), concluded by the one multi-recipient
    /// rule. A holder that answered no complete row set leaves the command unreported as a clean refusal when
    /// another holder may have written.</summary>
    internal static CommandResult MergeOwnerRows(string port, string[] rowHolder, string[] holders, CommandResult[] results)
    {
        var arrays = new JsonElement[results.Length];
        var owned = new int[holders.Length];
        foreach (var held in rowHolder) owned[Array.IndexOf(holders, held)]++;
        var missing = -1;
        for (var i = 0; i < results.Length; i++)
        {
            var outputs = results[i].Outputs;
            if (outputs.ValueKind == JsonValueKind.Object && outputs.TryGetProperty(port, out var rows)
                && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() == owned[i]) arrays[i] = rows;
            else if (missing < 0) missing = i;
        }
        if (missing >= 0)
            return results.Any(result => result.CommitState is CommitStates.Confirmed or CommitStates.Unknown)
                ? CommandResult.FailedUnknown("owner-split-incomplete", "Holder " + holders[missing] + " answered " + results[missing].Code + ".")
                : results[missing];
        var merged = new List<JsonElement>(rowHolder.Length);
        var taken = new int[holders.Length];
        for (var row = 0; row < rowHolder.Length; row++)
        {
            var from = Array.IndexOf(holders, rowHolder[row]);
            merged.Add(arrays[from][taken[from]++]);
        }
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [port] = RuntimeJson.From(merged) };
        foreach (var field in results[0].Outputs.EnumerateObject()) fields.TryAdd(field.Name, field.Value);
        var concluded = ResultAggregation.Conclude(merged, row => RuntimeJson.Text(row, "committed"), row => RuntimeJson.Text(row, "code"),
            RuntimeJson.From(fields), row => RuntimeJson.Text(row, "status") == CommandStatuses.Succeeded);
        var facts = results.SelectMany(result => result.Facts).ToArray();
        // The holders committed, so facts past the budget make the merged command's commit unknown, never a cut.
        if (facts.Length > CommandResult.MaximumFacts)
            return CommandResult.FailedUnknown(concluded.Outputs, "fact-budget", CommandResult.FactBudgetDetail);
        return facts.Length == 0 ? concluded
            : CommandResult.Create(concluded.Status, concluded.CommitState, concluded.Code, concluded.Detail, concluded.Outputs, facts);
    }

    private void TraceOwnerHolderRefusal(LoadedPlan plan, ResolvedStep step, Pending pending, JsonElement inputs, string reason,
        string stage, Exception? error = null)
    {
        if (!log.RuntimeLogs(RuntimeLogLevel.Trace)) return;
        var subject = RoutingSubject(step, inputs, out var port);
        var current = "unavailable";
        var entityResolver = false;
        if (subject != null && registry.TryResolver(subject.Id, out var resolve))
        {
            entityResolver = true;
            if (subject.WorldEpoch != WorldEpoch) current = "stale-world";
            else
            {
                try { current = resolve(subject) ? "current" : "stale-entity"; }
                catch (Exception probeError) { current = probeError.GetType().Name; }
            }
        }
        static string Bounded(string value) => value.Length <= KernelLog.MaxLogText ? value : value.Substring(0, KernelLog.MaxLogText - 1) + "…";
        var actual = JsonSerializer.Serialize(inputs);
        var values = new[] {
            RuntimeLogValue.Of("capability", step.CapabilityId), RuntimeLogValue.Of("inputPort", port ?? "<none>"),
            RuntimeLogValue.Of("inputs", Bounded(actual)), RuntimeLogValue.Of("node", step.NodeId),
            RuntimeLogValue.Of("subject", subject == null ? "<none>" : Bounded(RuntimeJson.From(subject).GetRawText())),
            RuntimeLogValue.Of("entityKind", subject == null ? "<none>" : RuntimeJson.KindOf(subject.Id)),
            RuntimeLogValue.Of("current", current), RuntimeLogValue.Of("entityResolver", entityResolver),
            RuntimeLogValue.Of("ownerResolver", registry.Tables.OwnerSessions.TryGetValue(step.CapabilityId, out var owner) && owner.Holder != null),
            RuntimeLogValue.Of("reason", reason), RuntimeLogValue.Of("truncated", actual.Length > KernelLog.MaxLogText),
            RuntimeLogValue.Of("stage", stage), RuntimeLogValue.Of("errorType", error?.GetType().Name ?? "<none>")
        };
        var record = new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Trace,
            Code = "runtime.owner-holder-refused",
            Provider = Identity.Id,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            Binding = step.BindingId,
            EventId = KernelLog.Redact(pending.Event.EventId),
            Plan = new RuntimeLogPlan { PlanId = plan.Plan.Id, ResourceId = plan.Plan.ResourceId, ResourceRevision = plan.Plan.ResourceRevision },
            Step = step.NodeId,
            Values = KernelLog.SnapshotLogValues(values),
            Detail = error == null ? null : Bounded(error.Message)
        };
        log.WriteRepeatedRefusal(in record, new KernelLog.RefusalSite(StepPlan: plan.Plan.Id, Step: step.NodeId, Reason: reason, Stage: stage, Current: current,
            Generation: SubscriptionGeneration));
    }

    private void TraceOwnerResultRefusal(OwnerFlight flight, CommandResult result)
    {
        if (!log.RuntimeLogs(RuntimeLogLevel.Trace)) return;
        var request = flight.Request;
        var plan = flight.Work?.Plan.Plan;
        static string Bounded(string value) => value.Length <= KernelLog.MaxLogText ? value : value.Substring(0, KernelLog.MaxLogText - 1) + "…";
        var inputs = request.Inputs.GetRawText();
        var stage = result.Detail.StartsWith(OwnerImportDetailPrefix, StringComparison.Ordinal) ? "identity-import"
            : request.HolderIdentity == 0 ? "channel-holder-identity" : "owner-result";
        var values = new[] {
            RuntimeLogValue.Of("holder", request.Holder),
            RuntimeLogValue.Of("holderIdentity", request.HolderIdentity.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            RuntimeLogValue.Of("holderIdentityResolved", request.HolderIdentity != 0),
            RuntimeLogValue.Of("sessionProvider", flight.SessionProvider), RuntimeLogValue.Of("stage", stage),
            RuntimeLogValue.Of("reason", result.Code), RuntimeLogValue.Of("status", result.Status),
            RuntimeLogValue.Of("commitState", result.CommitState), RuntimeLogValue.Of("operation", request.Operation),
            RuntimeLogValue.Of("inputs", Bounded(inputs)), RuntimeLogValue.Of("truncated", inputs.Length > KernelLog.MaxLogText)
        };
        var record = new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Trace,
            Code = "runtime.owner-holder-refused",
            Provider = Identity.Id,
            SubjectProvider = request.Provider,
            Tick = CurrentTick,
            WorldEpoch = request.WorldEpoch,
            Binding = request.BindingId,
            EventId = KernelLog.Redact(request.EventId),
            CommandId = request.CommandId,
            Plan = plan == null ? null : new RuntimeLogPlan
            {
                PlanId = request.PlanId,
                ResourceId = plan.ResourceId,
                ResourceRevision = plan.ResourceRevision
            },
            Step = request.NodeId,
            Values = KernelLog.SnapshotLogValues(values),
            Detail = Bounded(result.Detail)
        };
        log.WriteRepeatedRefusal(in record, new KernelLog.RefusalSite(StepPlan: request.PlanId, Step: request.NodeId, Reason: result.Code, Stage: stage));
    }

    // The explicit layer identity is carried with each owner invocation. It is never reconstructed from an
    // event ID: one event may run the same node more than once, and every period has its own command ID.
    private JsonElement OwnerEffectData(ResolvedStep step, string planId, JsonElement inputs, string commandId,
        string sourceProvider, string scopeId)
    {
        if (step.Effect is not { } options) return RuntimeJson.EmptyObject;
        var applicationId = commandId;
        var identity = EffectIdentity(planId, step.NodeId, EffectSubject(step, inputs));
        if (kernelEffects.effectIdentity.TryGetValue(identity, out var id) && kernelEffects.effects.TryGetValue(id, out var slot))
        {
            RuntimeJson.Require(!slot.Layers.Any(layer => layer.RestorePending), "effect-restore-pending",
                "The previous native restoration has not been confirmed.");
            if (slot.Layers.Count != 0 && (!options.StacksOnReapply || slot.Layers.Count >= options.MaxStacks)
                && slot.Layers[^1].SourceProvider == sourceProvider && slot.Layers[^1].ScopeId == scopeId)
                applicationId = slot.Layers[^1].CommandId;
        }
        return RuntimeJson.From(new { effectApplicationId = applicationId });
    }

    internal static string OwnerEffectApplicationId(OwnerOutput application)
        => RuntimeJson.Text(application.Data, "effectApplicationId");

    /// <summary>Admission only; no native commit has been reported yet.</summary>
    private static readonly CommandResult Dispatched = CommandResult.Partial(RuntimeJson.EmptyObject, CommitStates.None, Array.Empty<RuntimeFact>());

    /// <summary>The entity an owner step routes by: the first entity of its first entity input that names one. A one-receiver port
    /// carries the entity as an object and a many-receiver port (`targets`) as an array of them; the first named entity routes the
    /// step, as the row's resolver documents.</summary>
    private static EntityReference? RoutingSubject(ResolvedStep step, JsonElement inputs, out string? port)
    {
        port = null;
        if (inputs.ValueKind != JsonValueKind.Object) return null;
        for (var i = 0; i < step.Inputs.Count; i++)
        {
            var input = step.Inputs[i];
            if (input.View.Type != "entity" || !inputs.TryGetProperty(input.Name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Object)
            {
                var candidate = RuntimeJson.EntityRow(value);
                if (!string.IsNullOrEmpty(candidate.Id)) { port = input.Name; return candidate; }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var candidate = RuntimeJson.EntityRow(item);
                    if (!string.IsNullOrEmpty(candidate.Id)) { port = input.Name; return candidate; }
                }
        }
        return null;
    }

    /// <summary>
    /// Which player session holds what this step is about, answered by the module that owns the step's binding.
    /// There is no implicit holder: a provider that declares an owner capability registers a resolver for it, and
    /// a step whose resolver answers null — or a capability no resolver answers for — is refused by name.
    /// </summary>
    private RuntimeContractException? ResolveHolder(string provider, ResolvedStep step, JsonElement inputs,
        out string holder, out string stage)
    {
        holder = "";
        stage = "registration";
        if (!registry.Modules.ContainsKey(provider))
            return new RuntimeContractException("binding-lifecycle", step.BindingId);
        var capabilityId = step.CapabilityId;
        if (!registry.Tables.OwnerSessions.TryGetValue(capabilityId, out var registered) || registered.Holder == null)
            return new RuntimeContractException(OwnerUnsupportedCode,
                "No owner-session resolver is registered for " + capabilityId + " (registered: "
                + string.Join(",", registry.Tables.OwnerSessions.Keys) + ").");
        // The subject is the step's own entity input: an owner step changes one instance, and the instance is
        // named by the port the plan wired. A step whose inputs carry no entity at all has no subject to route by.
        stage = "subject";
        var subject = RoutingSubject(step, inputs, out _);
        if (subject == null)
            return new RuntimeContractException(OwnerHolderCode, step.NodeId + " names no entity to route by.");
        stage = "holder-resolver";
        var answer = registered.Holder(subject!);
        if (string.IsNullOrEmpty(answer))
        {
            stage = "holder-resolver-returned-null";
            return new RuntimeContractException(OwnerHolderCode, step.NodeId + " names no holder this build can reach.");
        }
        holder = answer!;
        return null;
    }

    /// <summary>Invokes the admitted step on this holder. Complete outputs and facts return to the host;
    /// only the host publishes facts and continues the graph. Durations start at this side's application tick.</summary>
    internal CommandResult ExecuteOwnerCommand(string planId, int stepIndex, string commandId, string eventId,
        string scopeId, long simulationTick, JsonElement inputs, JsonElement effect = default)
    {
        Thread();
        kernelOwner.ownerDeferralTaken = null;
        CommandContext? invocationContext = null;
        try
        {
            RuntimeJson.Text(planId);
            RuntimeJson.CommandId(commandId); RuntimeJson.Text(scopeId); RuntimeJson.Text(eventId);
            RuntimeJson.Integer(simulationTick, 0);
            RuntimeJson.Require(world.worldStarted, "dispatch-lifecycle", "An owner command needs a started world.");
            RuntimeJson.Require(inputs.ValueKind == JsonValueKind.Object, "invalid-owner-input",
                "Owner command inputs must be an object keyed by input port id.");
            RuntimeJson.Require(plans.Loaded.ContainsKey(planId), "plan-unloaded", planId);
            var plan = plans.Loaded[planId];
            var step = plan.Step(stepIndex)
                ?? throw new RuntimeContractException("owner-step", planId + "." + stepIndex);
            RuntimeJson.Require(IsOwnerStep(step), "owner-step", step.NodeId);
            RuntimeJson.Require(plans.ModulesLive(plan), "binding-lifecycle", step.BindingId);
            var handler = step.Handler!;
            var inputFrame = executor.ResolveBoundaryStepInputs(step, inputs, out var parameters);
            var origin = new RuntimeEvent(eventId, step.BindingId, WorldEpoch, simulationTick, scopeId, RuntimeJson.EmptyObject);
            var context = new CommandContext(origin, CurrentTick, commandId, plan.Names,
                step.NodeId, parameters, inputFrame, false, entityInstance, ReportedTickRate);
            if (step.Effect != null)
                context.EffectApplicationId = RuntimeJson.Text(effect, "effectApplicationId");
            if (effect.ValueKind == JsonValueKind.Object && effect.TryGetProperty("effectHandle", out var effectHandle))
            {
                context.EffectHandleId = RuntimeHandle.TryRead(effectHandle, out var effectId) ? effectId : null;
                context.EffectStacks = effect.GetProperty("effectStacks").GetInt32();
                context.EffectAddLayer = false;
            }
            // Answering later needs no cleanup of its own: a command with an effect or a returned handle owes
            // restores and cancels that a pending apply cannot yet name.
            if (step.Effect == null && !step.ReturnsHandle)
                context.OwnerDeferralKernel = this;
            kernelOwner.OwnerInvocation = true;
            BeginCommandInvocation(ref context); invocationContext = context;
            executor.ArmTypedWrite(step, inputFrame, isHost: false, boundary: true);
            var returned = handler!(context);
            if (ReferenceEquals(returned, CommandResult.OwnerDeferred))
            {
                if (context.OwnerDeferral is not { } deferral)
                    return CommandResult.FailedUnknown("invalid-owner-result", "The deferral marker was returned without a deferral.");
                if (!KeepOwnerDeferralRows(deferral))
                    return CommandResult.FailedUnknown("owner-deferral-unsupported", "Rows can be written later only for a step whose recipients are entities.");
                kernelOwner.ownerDeferralTaken = deferral;
                return returned;
            }
            var result = executor.RenderBoundaryRows(step, NormalizeInvokedResult(returned));
            if (!CommandResultRules.TryValidate(result, out var violation))
                return CommandResult.FailedUnknown(result.Outputs, "invalid-owner-result",
                    "An owner handler returned an invalid result (" + violation + ").");
            return result;
        }
        catch (RuntimeContractException error)
        {
            return CommandResult.Rejected(error.Code, CommandResult.TruncateDetail(error.Message));
        }
        catch (Exception error)
        {
            return CommandResult.FailedUnknown("handler-exception", CommandResult.TruncateDetail(error.GetType().Name + ": " + error.Message));
        }
        finally
        {
            kernelOwner.OwnerInvocation = false;
            if (invocationContext is { } completed) EndCommandInvocation(in completed);
            else currentCommand = null;
        }
    }

}

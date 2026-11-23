using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>The owner channel's books: the commands issued to an owning client and the ones received from the host, the flights waiting on an answer and the dispatches they resume, the split and identity tables, the cleanup, lease and early-end records of owner-run effects, and the outputs queued for the wire. It queues, settles and imports its own rows; applying a received command and reporting a result run above, where handlers are invoked.</summary>
internal sealed partial class KernelOwner
{
    private readonly KernelWorld world;
    private readonly KernelRegistry registry;
    private readonly KernelPlans plans;
    private readonly KernelExecutor executor;
    private readonly EventQueue eventQueue;
    private readonly KernelEffects kernelEffects;
    private readonly RuntimeLimits limits;

    internal KernelOwner(KernelWorld world, KernelRegistry registry, KernelPlans plans, KernelExecutor executor, EventQueue eventQueue, KernelEffects kernelEffects, RuntimeLimits limits)
    {
        this.world = world;
        this.registry = registry;
        this.plans = plans;
        this.executor = executor;
        this.eventQueue = eventQueue;
        this.kernelEffects = kernelEffects;
        this.limits = limits;
    }

    internal List<OwnerOutput> ownerOutputs = new();

    internal bool OwnerApplicationDisconnected(string commandId)
        => ownerIssued.TryGet(commandId, out var application)
            && application.Application.Controls.TryGetValue("cancel-command", out var control)
            && control.Request.CommandId == application.Request.CommandId + ":disconnect" && control.Result != null;

    internal void QueueOwnerLayerRestore(EffectSlot slot, RuntimeEffectContext context, OwnerOutput application)
    {
        if (OwnerApplicationDisconnected(application.CommandId)) return;
        if (!kernelEffects.effects.ContainsKey(slot.Id) || slot.Layers.Count == 0) kernelEffects.ownerEffects.Remove(slot.HandleId);
        foreach (var flight in ownerFlights.Values)
        {
            if (!ReferenceEquals(flight.Pulse?.Effect, slot) || flight.CancelSent
                || OwnerEffectApplicationId(flight.Request) != OwnerEffectApplicationId(application)) continue;
            QueueOwnerControl(flight.Request, "cancel-command", RuntimeJson.EmptyObject);
            flight.CancelSent = true;
        }
        QueueOwnerControl(application, "restore", RuntimeJson.From(new
        { handle = context.Handle, reason = context.Reason, stacks = context.Stacks, subject = context.Subject, status = context.StatusKind }));
    }

    internal string[] OwnerCleanupSlots(ResolvedStep step)
        => OwnerCommandLedger.Slots(step.Contract, step.Effect != null && registry.Tables.EffectRestores.ContainsKey(step.BindingId));

    internal bool OwnerInvocation { get; set; }

    internal readonly Dictionary<string, IdentityBinding> ownerIdentities = new(StringComparer.Ordinal);

    internal Dictionary<string, OwnerFlight> ownerFlights = new(StringComparer.Ordinal);

    internal readonly List<OwnerFlight> ownerFlightScratch = new();

    internal Dictionary<Pending, OwnerFlight> ownerResumes = new(ReferenceEqualityComparer.Instance);

    internal List<OwnerOutput> ownerControls = new();

    // Outgoing lifecycle accounting: keeps admitted cleanup obligations visible until their holder replies.
    // Transport correlation and receiver execution dedup use the same budget rule at their own boundaries.
    internal OwnerCommandLedger ownerIssued = new();

    internal long ownerWorld = -1, ownerControlSequence;

    internal OwnerSplit? ownerSplit;

    internal readonly Dictionary<string, (OwnerSplit Split, int Index)> ownerSplits = new(StringComparer.Ordinal);

    internal bool SplitComplete(OwnerFlight flight)
        => !ownerSplits.TryGetValue(flight.Request.CommandId, out var member) || member.Split.Complete;

    internal void ForgetOwnerSplit(OwnerFlight flight)
    {
        if (!ownerSplits.TryGetValue(flight.Request.CommandId, out var member)) return;
        foreach (var request in member.Split.Requests) ownerSplits.Remove(request.CommandId);
    }

    /// <summary>The cancel of the other machines' requests of a step that ended while they were out.</summary>
    internal void CancelOwnerSplit(OwnerFlight lead)
    {
        if (!ownerSplits.TryGetValue(lead.Request.CommandId, out var member)) return;
        var split = member.Split;
        for (var index = 1; index < split.Requests.Count; index++)
        {
            if (split.CancelSent[index]) continue;
            QueueOwnerControl(split.Requests[index], "cancel-command", RuntimeJson.EmptyObject);
            split.CancelSent[index] = true;
        }
    }

    internal CommandResult? CaptureOwnerPulse(EffectSlot slot, ResolvedStep step, string commandId, KernelExecutor.CostHold? hold)
    {
        if (!kernelEffects.ownerEffects.TryGetValue(slot.HandleId, out var original))
            return CommandResult.Rejected("owner-command-missing");
        if (ownerFlights.Count >= limits.MaxLiveHandles)
            return CommandResult.Rejected("owner-pending-budget");
        // Reuse the admitted address, native identities and inputs. A later inventory lookup must never
        // redirect this effect to another player or another equipment life.
        var request = original.Request with
        {
            CommandId = commandId,
            SimulationTick = world.Tick,
            Data = OwnerCommandLedger.Data(RuntimeJson.From(new
            {
                effectHandle = slot.Handle,
                effectStacks = slot.Layers.Count,
                effectApplicationId = slot.CommandId
            }),
                OwnerCleanupSlots(step))
        };
        var admission = ownerIssued.Admit(request, OwnerCleanupSlots(step), out _, out _);
        if (admission != null) return admission;
        var inputs = executor.ResolveBoundaryStepInputs(step, request.Inputs, out var parameters);
        ownerFlightScratch.EnsureCapacity(ownerFlights.Count + 1);
        ownerFlights.Add(commandId, new OwnerFlight
        {
            Request = request,
            Pending = original.Cause,
            Inputs = inputs,
            Parameters = parameters,
            Hold = hold,
            Pulse = new(slot, step)
        });
        ownerOutputs.Add(request);
        return null;
    }

    internal JsonElement ExportOwnerIdentities(ulong holder, JsonElement inputs) => RuntimeJson.From(ownerIdentities.Where(pair => pair.Value.Handle.IsRegistered)
        .ToDictionary(pair => pair.Key, pair => pair.Value.Export(holder, inputs), StringComparer.Ordinal));

    // EnterOwner and CaptureOwner are adjacent in the serial entry walk. Retain the actual resolver's
    // provenance with the flight; diagnostics must not call a native identity resolver a second time.
    internal string ownerSessionProvider = "<none>";

    internal ulong OwnerSession(string address)
    {
        ownerSessionProvider = "<none>";
        foreach (var binding in ownerIdentities.Values)
        {
            if (!binding.Handle.IsRegistered || binding.Session == null) continue;
            ownerSessionProvider = binding.Handle.ProviderId;
            if (binding.Session(address) is { } session && session != 0) return session;
        }
        return 0;
    }

    internal void SettleOwnerLedger(string commandId, CommandResult result)
    {
        if (!ownerIssued.TryGet(commandId, out var issued)) return;
        var firstResult = issued.Result == null;
        issued.Result = result;
        if (firstResult) ResolveEarlyOwnerEndings(issued);
    }

    internal bool CompleteOwnerSplit(OwnerFlight lead, OwnerSplit split)
    {
        if (!split.Complete) return true;
        lead.Result = MergeOwnerRows(lead.Work.Entry.Steps[lead.Step].Frames.Row!.Port, split.RowHolder, split.Holders, split.Results!);
        EnqueueOwnerResume(lead);
        return true;
    }

    internal void EnqueueOwnerResume(OwnerFlight flight)
    {
        var resume = new Pending(flight.Pending.Provider, flight.Pending.Generation, flight.Pending.Event, new[] { flight.Work },
            Sequence: flight.Pending.Sequence, Tabled: flight.Pending.Tabled);
        flight.Pending = resume; ownerResumes.Add(resume, flight);
        eventQueue.queue.Enqueue(resume, (world.Tick, ++eventQueue.sequence));
    }

    // Receiver boundary: cached native execution results, distinct from outgoing lifecycle accounting.
    internal OwnerCommandLedger ownerReceived = new();

    internal Dictionary<string, OwnerCleanupContext> ownerCleanup = new(StringComparer.Ordinal);

    internal Dictionary<string, OwnerLease> ownerLeases = new(StringComparer.Ordinal);

    internal Queue<(OwnerOutput Request, string Reason, CommandResult Result)> ownerEndResults = new();

    internal List<CommandReceipt> ownerEndReceipts = new();

    internal Dictionary<string, CommandResult> ownerEarlyEnds = new(StringComparer.Ordinal);

    internal CommandResult? ownerRestoreResult;

    internal OwnerCleanupCapture? ownerCleanupCapture;

    internal bool ownerFlushingEndResults;

    internal readonly List<OwnerDeferred> ownerDeferrals = new();

    internal OwnerDeferralRequest? ownerDeferralTaken;

    internal CommandResult ImportOwnerHandles(OwnerFlight flight, ResolvedStep step, CommandResult result, PendingEffect? effect)
    {
        // An uncertain write still needs its original holder for later restoration.
        if (effect != null && EffectKept(result))
        {
            effect.OwnerApplication = flight.Request;
            kernelEffects.ownerEffects.TryAdd(effect.Slot.HandleId, new(flight.Request, flight.Pending, effect.Slot));
        }
        if (result.CommitState != CommitStates.Confirmed) return result;
        var fields = result.Outputs.EnumerateObject().ToDictionary(field => field.Name, field => field.Value);
        foreach (var port in RuntimeJson.Rows(step.Contract, "outputs"))
        {
            if (!port.TryGetProperty("handleKind", out var kind) || !fields.TryGetValue(RuntimeJson.Text(port, "id"), out var remote)
                || remote.ValueKind != JsonValueKind.Object) continue;
            RuntimeJson.Handle(remote);
            var key = flight.Request.CommandId + "\n" + remote.GetRawText();
            if (ownerLeases.TryGetValue(key, out var alias))
            {
                fields[RuntimeJson.Text(port, "id")] = alias.Local;
                continue;
            }
            var local = kernelEffects.CreateHandle(step.ProviderId, kind.GetString()!, RuntimeJson.Text(port, "lifetime"), out var address);
            ownerLeases[key] = new(flight.Request, remote, local);
            kernelEffects.handleSlots[address]!.Cancel = () => QueueOwnerControl(flight.Request, "cancel", RuntimeJson.From(new { handle = remote }), "handle:" + RuntimeJson.Text(port, "id"));
            fields[RuntimeJson.Text(port, "id")] = local;
        }
        if (ownerEarlyEnds.Remove(flight.Request.CommandId, out var ending) && ending.Status == CommandStatuses.Succeeded
            && ending.CommitState == CommitStates.Confirmed)
            ReleaseOwnerLeases(flight.Request.CommandId);
        if (ownerIssued.TryGet(flight.Request.CommandId, out var issued))
            foreach (var ended in issued.Application.Endings.Values)
                if (ended.Result.CommitState == CommitStates.Confirmed && OwnerCommandLedger.EndingMatches(issued, ended.Slot, ended.Handle))
                    ReleaseOwnerLeases(flight.Request.CommandId, ended.Slot == "cancel-command" ? null : ended.Handle);
        return CommandResult.Create(result.Status, result.CommitState, result.Code, result.Detail, RuntimeJson.From(fields), result.Facts.ToArray());
    }

    // A proxy is a receipt-bearing remote obligation, not a locally completed cancel hook. Keep its
    // slot until ReceiveOwnerEnd accepts the holder's confirmed result. The existing owner-control
    // CommandReceipt in TickResult.Commands is the final completion surface: CommandId identifies the
    // control, EventId/RootEventId identify its application. No cancelled graph is resumed by this receipt.
    internal void RequireOwnerCancelReceipt(int address, HandleSlot slot)
    {
        var lease = ownerLeases.Values.FirstOrDefault(value => value.Local.GetProperty("local").GetInt32() == address
            && value.Local.GetProperty("lifeEpoch").GetInt32() == slot.Generation);
        if (lease == null) return;
        RuntimeJson.Require(ownerIssued.TryGet(lease.Request.CommandId, out var application),
            "owner-command-missing", lease.Request.CommandId);
        var control = application.Application.Controls.Values.FirstOrDefault(value => value.Request.Operation == "cancel-command"
            || value.Request.Operation == "cancel"
                && value.Request.Data.GetProperty("handle").GetRawText() == lease.Remote.GetRawText());
        if (control == null)
        {
            // The imported hook consumes the existing reserved output slot once, including after migration.
            // Leave it attached: repeated calls consult this reservation instead of sending another control.
            RuntimeJson.Require(slot.Cancel != null, "owner-cleanup-contract", lease.Request.CommandId);
            slot.Cancel!();
        }
        var result = control?.Result;
        throw new RuntimeContractException(result == null ? "owner-cancel-pending"
            : result.CommitState == CommitStates.Unknown ? "owner-cancel-failed"
            : result.CommitState == CommitStates.None ? "owner-cancel-rejected" : RuntimeAbiCodes.StaleHandle,
            result?.Code ?? lease.Request.CommandId);
    }

    internal void QueueOwnerControl(OwnerOutput request, string operation, JsonElement data, string? slot = null)
    {
        var control = request with
        {
            Operation = operation,
            Data = OwnerCommandLedger.Data(data, OwnerCommandLedger.Declaration(request), slot ?? operation),
            CommandId = request.CommandId + ":end:" + (++ownerControlSequence),
            EventId = request.CommandId,
            Identities = RuntimeJson.EmptyObject
        };
        var refusal = ownerIssued.Admit(control, OwnerCommandLedger.Declaration(request), out _, out _);
        RuntimeJson.Require(refusal == null, refusal?.Code ?? "owner-cleanup-contract", request.CommandId);
        ownerControls.Add(control);
    }

    internal bool RestoreOwnerEffect(RuntimeEffectContext context)
    {
        if (OwnerInvocation || !kernelEffects.ownerEffects.TryGetValue(context.HandleId, out var original)) return false;
        if (!kernelEffects.effects.ContainsKey(original.Slot.Id) || original.Slot.Layers.Count == 0)
            kernelEffects.ownerEffects.Remove(context.HandleId);
        foreach (var flight in ownerFlights.Values)
        {
            if (!ReferenceEquals(flight.Pulse?.Effect, original.Slot) || flight.CancelSent) continue;
            QueueOwnerControl(flight.Request, "cancel-command", RuntimeJson.EmptyObject);
            flight.CancelSent = true;
        }
        QueueOwnerControl(original.Slot.RestoringOwnerApplication ?? original.Request, "restore", RuntimeJson.From(new
        {
            handle = context.Handle,
            reason = context.Reason,
            stacks = context.Stacks,
            subject = context.Subject,
            status = context.StatusKind
        }));
        return true;
    }

    // Cached notifications can be repeated after a lost packet without repeating any native cleanup.
    internal IEnumerable<(OwnerOutput Request, string Reason, CommandResult Result)> OwnerEndingReceipts(string? commandId = null)
    {
        foreach (var application in ownerReceived.Entries)
            if (application.IsApplication && application.Request.WorldEpoch == world.WorldEpoch
                && (commandId == null || application.Request.CommandId == commandId))
                foreach (var ended in application.Application.Endings.Values)
                    yield return (OwnerCommandLedger.WithEnding(application.Request, ended.Slot, ended.Handle), ended.Reason, ended.Result);
    }

    internal void ResolveEarlyOwnerEndings(OwnerCommandLedger.Entry application)
    {
        foreach (var ended in application.Application.Endings.Values.ToArray())
        {
            var result = ended.Result;
            if (!OwnerCommandLedger.EndingMatches(application, ended.Slot, ended.Handle))
            {
                // Before the application reply there is no returned handle to compare. A conflicting early
                // claim never releases a proxy or occupies the real handle's future notification identity.
                application.Application.Endings.Remove(ended.Slot);
                result = CommandResult.Rejected("owner-command-conflict", "Early ending did not name the returned handle.");
            }
            AddSpontaneousOwnerReceipt(application.Request, result);
        }
    }

    internal void AddSpontaneousOwnerReceipt(OwnerOutput application, CommandResult result)
        => ownerEndReceipts.Add(new CommandReceipt(application.CommandId, application.EventId, null, application.EventId,
            application.PlanId, "", "", application.NodeId, application.BindingId, world.WorldEpoch, world.Tick, result));

    internal void ReleaseOwnerLeases(string commandId, JsonElement? remote = null)
    {
        foreach (var pair in ownerLeases.Where(pair => pair.Value.Request.CommandId == commandId
            && (remote == null || pair.Value.Remote.GetRawText() == remote.Value.GetRawText())).ToArray())
        {
            var local = pair.Value.Local;
            var address = local.GetProperty("local").GetInt32();
            if (address < kernelEffects.handleSlots.Count && kernelEffects.handleSlots[address]?.Generation == local.GetProperty("lifeEpoch").GetInt32()) kernelEffects.ReleaseHandle(address);
            ownerLeases.Remove(pair.Key);
        }
    }

    /// <summary>Exchanges the state a host migration swaps with the staged image's. Nothing runs between the
    /// assignments; the image keeps the prior state for rollback.</summary>
    internal void SwapWith(KernelOwner other)
    {
        (ownerIssued, other.ownerIssued) = (other.ownerIssued, ownerIssued);
        (ownerReceived, other.ownerReceived) = (other.ownerReceived, ownerReceived);
        (ownerFlights, other.ownerFlights) = (other.ownerFlights, ownerFlights);
        (ownerResumes, other.ownerResumes) = (other.ownerResumes, ownerResumes);
        (ownerControls, other.ownerControls) = (other.ownerControls, ownerControls);
        (ownerLeases, other.ownerLeases) = (other.ownerLeases, ownerLeases);
        (ownerCleanup, other.ownerCleanup) = (other.ownerCleanup, ownerCleanup);
        (ownerEndResults, other.ownerEndResults) = (other.ownerEndResults, ownerEndResults);
        (ownerEndReceipts, other.ownerEndReceipts) = (other.ownerEndReceipts, ownerEndReceipts);
        (ownerEarlyEnds, other.ownerEarlyEnds) = (other.ownerEarlyEnds, ownerEarlyEnds);
        (ownerOutputs, other.ownerOutputs) = (other.ownerOutputs, ownerOutputs);
        (ownerWorld, other.ownerWorld) = (other.ownerWorld, ownerWorld);
        (ownerControlSequence, other.ownerControlSequence) = (other.ownerControlSequence, ownerControlSequence);
    }
}

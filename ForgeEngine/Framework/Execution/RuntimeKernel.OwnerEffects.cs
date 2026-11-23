using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    internal sealed record OwnerLease(OwnerOutput Request, JsonElement Remote, JsonElement Local);
    // An admitted application's cleanup contract outlives its plan, but never its provider generation or world.
    // Only data needed to validate and undo that application is retained: no plan, handler or native object.
    // Entries share ownerReceived's admission budget and are cleared with that ledger at the world boundary.
    internal sealed record OwnerCleanupContext(long ProviderGeneration, string CapabilityId, string[] Slots,
        JsonElement[] HandlePorts, int MaxStacks, string StatusKind, IReadOnlyList<EntityReference> Subject);
    internal sealed record OwnerEffect(OwnerOutput Request, Pending Cause, EffectSlot Slot);
    internal sealed record OwnerCleanupCapture(OwnerOutput Application, JsonElement Handle);
    internal Func<OwnerOutput, CommandResult, string, bool>? OwnerEndReporter { get; set; }

    /// <summary>The longest an owner command may stay unanswered on its holder. The host gives up on a silent holder
    /// at 600 ticks, so a deferral always ends well before the host's own timeout.</summary>
    public const int MaximumOwnerDeferralTicks = 300;

    /// <summary>One owner command whose handler answered later: the admitted receipt, the tick it ends at, and the
    /// handler's own check. Pending ones are few and short-lived, and the list is walked by index.</summary>
    internal sealed class OwnerDeferred
    {
        internal OwnerCommandLedger.Entry Entry = null!;
        internal long Deadline;
        internal OwnerDeferralRequest Request = null!;
    }
    // The terminal results the holder owes the host, sent in order and kept until the channel accepts each one.
    private readonly Queue<(OwnerOutput Request, CommandResult Result)> ownerSettlements = new();

    /// <summary>Whether the holder still owes the host this command's terminal result: it answered later and has not
    /// settled, or it settled and the result has not been sent yet. The channel sends no reply for such a command.</summary>
    internal bool OwnerSettlementPending(string commandId)
    {
        ReadThread();
        foreach (var deferral in kernelOwner.ownerDeferrals)
            if (deferral.Entry.Request.CommandId == commandId) return true;
        foreach (var settlement in ownerSettlements)
            if (settlement.Request.CommandId == commandId) return true;
        return false;
    }

    /// <summary>One pass over the pending commands. Each asks its handler once; an answer settles the receipt and
    /// queues exactly one result for the host. A world change has already cleared the ledger and the list.</summary>
    private void SettleOwnerDeferrals()
    {
        SyncOwnerWorld();
        for (var index = kernelOwner.ownerDeferrals.Count - 1; index >= 0; index--)
        {
            var pending = kernelOwner.ownerDeferrals[index];
            if (pending.Entry.Application.Terminal || pending.Entry.Request.WorldEpoch != WorldEpoch)
            { kernelOwner.ownerDeferrals.RemoveAt(index); continue; }
            var expired = CurrentTick >= pending.Deadline;
            CommandResult? answer;
            try { answer = SettleOwnerRows(pending.Request, expired); }
            catch (Exception error)
            {
                answer = CommandResult.FailedUnknown("owner-deferral-exception", CommandResult.TruncateDetail(error.GetType().Name + ": " + error.Message));
            }
            if (answer == null)
            {
                if (!expired) continue;
                answer = CommandResult.FailedUnknown("owner-deferral-expired");
            }
            if (ReferenceEquals(answer, CommandResult.OwnerDeferred) || answer.Status == CommandStatuses.Partial
                || !CommandResultRules.TryValidate(answer, out _))
                answer = CommandResult.FailedUnknown("invalid-owner-result", "A deferred owner command settled with a non-terminal result.");
            kernelOwner.ownerDeferrals.RemoveAt(index);
            pending.Entry.Result = answer;
            ownerSettlements.Enqueue((pending.Entry.Request, answer));
        }
    }

    /// <summary>Asks one row-writing check. The writer is armed for the recipients the step had, in a region of its
    /// own, exactly as a deferred result's terminal row is; the rows are checked by the rule a handler's rows are —
    /// one ended row per recipient — and rendered once, into the result the host receives.</summary>
    internal CommandResult? SettleOwnerRows(OwnerDeferralRequest request, bool expired)
    {
        RuntimeJson.Require(deferredInvocation.Stamp == 0, "deferred-result-context", "An owner result is settled outside another completion's row.");
        var row = request.Row!;
        var space = deferredRows ??= new FrameSpace(64, MaximumEventPayloadBytes);
        if (row.Slot + row.Span > space.SlotCapacity) space.Grow(row.Slot + row.Span);
        space.Strings.Reset();
        var stamp = ++deferredInvocationSequence;
        deferredInvocation.Reset(stamp);
        var write = deferredInvocation.Rows;
        write.ArmTargets(space, row.Slot, row, request.Targets!, request.Set, request.HostTier, WorldEpoch);
        try
        {
            if (request.Rows.Invoke(expired, new RowWriter(deferredInvocation, stamp)) is not { } outcome) return null;
            var code = write.ErrorCode; var detail = write.ErrorDetail;
            if (code == null && write.Open >= 0)
            { code = RuntimeAbiCodes.ResultRowIncomplete; detail = row.Port + " row " + write.Open + " was begun and never ended."; }
            if (code == null && write.Count != write.Recipients)
            { code = RuntimeAbiCodes.ResultRowIncomplete; detail = row.Port + " carries " + write.Count + " rows for " + write.Recipients + " explicit recipients."; }
            if (code != null) return CommandResult.FailedUnknown(code, CommandResult.TruncateDetail(detail));
            return executor.WithRows(outcome, new ResultRowSet(row, write.Count, write.Set) { Space = space }, settled: false);
        }
        finally { deferredInvocation.Expire(stamp); }
    }

    /// <summary>Keeps what a row-writing deferral's rows are written against, read from the writer the step's own
    /// invocation armed. False where the step has no rows or a recipient is not an entity.</summary>
    internal bool KeepOwnerDeferralRows(OwnerDeferralRequest deferral)
    {
        var typed = executor.TypedWrite;
        if (!typed.Armed || typed.Row is not { TargetKind: ValueKind.Entity } row) return false;
        var targets = new EntityReference[typed.Recipients];
        for (var index = 0; index < targets.Length; index++)
            if (!typed.TryRecipient(index, out targets[index])) return false;
        deferral.Row = row; deferral.Targets = targets; deferral.Set = typed.Set; deferral.HostTier = typed.HostTier;
        return true;
    }

    private void FlushOwnerSettlements()
    {
        if (OwnerEndReporter is not { } report) return;
        while (ownerSettlements.TryPeek(out var settlement))
        {
            // An empty reason is the ordinary reply for the application, so the host receives it as the command's result.
            if (!report(settlement.Request, settlement.Result, "")) return;
            if (ownerSettlements.TryPeek(out var front) && ReferenceEquals(front.Request, settlement.Request)) ownerSettlements.Dequeue();
        }
    }

    private bool HasLiveOwnerState()
    {
        if (kernelOwner.ownerDeferrals.Count != 0 || ownerSettlements.Count != 0) return true;
        // Emission is not settlement. An omitted, rejected or unknown cleanup reply cannot make migration safe.
        if (kernelOwner.ownerIssued.Entries.Any(entry => entry.Request.WorldEpoch == WorldEpoch
            && (!entry.IsApplication && entry.Result?.CommitState != CommitStates.Confirmed
                || entry.IsApplication && entry.Application.Endings.Values.Any(ended => ended.Result.CommitState == CommitStates.Unknown)))) return true;
        if (kernelOwner.ownerFlights.Values.Any(value => value.Request.WorldEpoch == WorldEpoch)
            || kernelOwner.ownerControls.Any(value => value.WorldEpoch == WorldEpoch)
            || kernelEffects.ownerEffects.Values.Any(value => kernelEffects.effects.ContainsKey(value.Slot.Id))) return true;
        bool Live(JsonElement handle)
        {
            if (handle.ValueKind != JsonValueKind.Object || !handle.TryGetProperty("worldEpoch", out var world)
                || !world.TryGetInt64(out var epochWorld) || epochWorld != WorldEpoch || !handle.TryGetProperty("local", out var local)
                || !local.TryGetInt32(out var address) || address < 0 || address >= kernelEffects.handleSlots.Count) return false;
            return kernelEffects.handleSlots[address] is { Active: true } slot
                && handle.TryGetProperty("lifeEpoch", out var epoch) && epoch.TryGetInt32(out var generation)
                && slot.Generation == generation;
        }
        if (kernelOwner.ownerLeases.Values.Any(value => Live(value.Local))) return true;
        foreach (var entry in kernelOwner.ownerReceived.Entries)
        {
            if (!entry.IsApplication || entry.Request.WorldEpoch != WorldEpoch || entry.Result == null || !EffectKept(entry.Result)) continue;
            if (entry.Application.Endings.Values.Any(ended => ended.Result.CommitState == CommitStates.Unknown)) return true;
            if (entry.Application.Slots.Length > 1
                && entry.Application.Controls.Values.Any(control => control.Result?.CommitState == CommitStates.Unknown)) return true;
            if (entry.Result.Outputs.ValueKind == JsonValueKind.Object
                && entry.Result.Outputs.EnumerateObject().Any(field => Live(field.Value))) return true;
            // The host's existing effects own its clocks. A holder-only kernel can also have a native effect
            // whose host clock is elsewhere; a completed one-shot receipt alone is never live owner state.
            if (world.worldHost != true && entry.Application.Slots.Contains("restore", StringComparer.Ordinal)
                && !(entry.Application.Controls.TryGetValue("restore", out var restore) && restore.Result?.CommitState == CommitStates.Confirmed)
                && !(entry.Application.Controls.TryGetValue("cancel-command", out var cancel) && cancel.Result?.CommitState == CommitStates.Confirmed)) return true;
        }
        return false;
    }

    internal CommandResult ExecuteOwnerOutput(OwnerOutput request, string localAddress)
    {
        Thread(); SyncOwnerWorld();
        if (request.WorldEpoch != WorldEpoch) return CommandResult.Rejected("stale-world");
        if (request.Holder != localAddress) return CommandResult.Rejected(ResultCode.OwnerNotAddressed);
        // Cached receipts survive plan teardown. Looking one up performs no native work.
        if (kernelOwner.ownerReceived.TryGet(request.CommandId, out var prior))
        {
            if (prior.IsApplication && prior.Application.Suppressed && request.Operation == "apply")
                return OwnerCommandLedger.SameApplication(prior.Application.Request, request)
                    ? CommandResult.Rejected("owner-command-cancelled") : CommandResult.Rejected("owner-command-conflict");
            return OwnerCommandLedger.Same(prior.Request, request)
                ? prior.Result ?? CommandResult.FailedUnknown("owner-command-pending")
                : CommandResult.Rejected("owner-command-conflict");
        }
        OwnerCommandLedger.Entry? admitted = null;
        CommandResult result;
        try
        {
            OwnerCommandLedger.Entry? application = null;
            if (request.Operation != "apply") kernelOwner.ownerReceived.TryGet(request.EventId, out application!);
            OwnerCleanupContext cleanup;
            if (application != null && kernelOwner.ownerCleanup.TryGetValue(request.EventId, out var retained))
            {
                RuntimeJson.Require(application.IsApplication && OwnerCommandLedger.SameApplication(application.Request, request),
                    "owner-command-conflict", request.EventId);
                RuntimeJson.Require(registry.IsRegistered(request.Provider, retained.ProviderGeneration), "binding-lifecycle", request.BindingId);
                cleanup = retained;
            }
            else
            {
                // A new application (including one sent after unload) still needs the live plan. Only controls
                // for an already admitted application may use the retained cleanup contract above.
                RuntimeJson.Require(plans.Loaded.TryGetValue(request.PlanId, out var plan), "plan-unloaded", request.PlanId);
                var step = plan!.Step(request.StepIndex);
                RuntimeJson.Require(step != null && IsOwnerStep(step) && step.ProviderId == request.Provider
                    && step.BindingId == request.BindingId && step.NodeId == request.NodeId, "owner-command-conflict", request.CommandId);
                RuntimeJson.Require(plans.ModulesLive(plan), "binding-lifecycle", request.BindingId);
                cleanup = new OwnerCleanupContext(plan.ProviderGenerations[step!.Frames.Index], step.CapabilityId, kernelOwner.OwnerCleanupSlots(step),
                    RuntimeJson.Rows(step.Contract, "outputs").Where(port => port.TryGetProperty("handleKind", out _))
                        .Select(port => port.Clone()).ToArray(),
                    step.Effect?.MaxStacks ?? 0, step.Effect?.StatusKind ?? "", EffectSubject(step, request.Inputs));
            }
            var slots = cleanup.Slots;
            if (request.Operation == "apply" && cleanup.MaxStacks > 0)
            {
                RuntimeJson.Require(request.Data.ValueKind == JsonValueKind.Object
                    && request.Data.TryGetProperty("effectApplicationId", out var applicationIdentity)
                    && applicationIdentity.ValueKind == JsonValueKind.String
                    && RuntimeJson.IsText(applicationIdentity.GetString()), "owner-cleanup-contract",
                    "A managed effect must carry its explicit application identity.");
                var identity = OwnerEffectApplicationId(request);
                if (identity != request.CommandId)
                {
                    RuntimeJson.Require(kernelOwner.ownerReceived.TryGet(identity, out var original) && original.IsApplication
                        && original.Request.Provider == request.Provider && original.Request.PlanId == request.PlanId
                        && original.Request.NodeId == request.NodeId && original.Request.BindingId == request.BindingId
                        && original.Request.StepIndex == request.StepIndex && original.Request.WorldEpoch == request.WorldEpoch
                        && original.Request.Holder == request.Holder && original.Request.HolderIdentity == request.HolderIdentity
                        && original.Request.ScopeId == request.ScopeId
                        && OwnerEffectApplicationId(original.Request) == identity
                        && kernelOwner.ownerCleanup.TryGetValue(identity, out var originalCleanup)
                        && originalCleanup.Subject.SequenceEqual(cleanup.Subject), "owner-command-conflict",
                        "The effect application identity does not name this admitted layer.");
                    RuntimeJson.Require(!original!.Application.Terminal
                        && !original.Application.Controls.ContainsKey("restore"), "owner-command-cancelled", identity);
                }
            }
            // Validate control payloads before they can consume any application's reservation.
            if (request.Operation != "apply")
            {
                var slot = OwnerCommandLedger.ControlSlot(request);
                RuntimeJson.Require(slots.Contains(slot, StringComparer.Ordinal), "owner-cleanup-contract", slot);
                if (request.Operation == "cancel")
                {
                    var handle = request.Data.GetProperty("handle");
                    RuntimeJson.Handle(handle);
                    var portId = slot[7..];
                    RuntimeJson.Require(application is { IsApplication: true, Result: not null }
                        && application.Result.Outputs.TryGetProperty(portId, out var returned)
                        && returned.GetRawText() == handle.GetRawText(), "owner-command-conflict", portId);
                }
                else if (request.Operation == "restore")
                {
                    var effectHandle = request.Data.GetProperty("handle");
                    RuntimeJson.Handle(effectHandle);
                    RuntimeJson.Require(effectHandle.GetProperty("worldEpoch").GetInt64() == request.WorldEpoch,
                        "stale-world", "Restore handle belongs to another world.");
                    RuntimeJson.Integer(request.Data.GetProperty("stacks"), 1, cleanup.MaxStacks);
                    RuntimeJson.Text(request.Data, "reason");
                    RuntimeJson.Require(request.Data.GetProperty("status").ValueKind == JsonValueKind.String
                        && request.Data.GetProperty("status").GetString() == cleanup.StatusKind,
                        "owner-cleanup-contract", "Restore status differs from the admitted step.");
                    var subject = request.Data.GetProperty("subject").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
                    RuntimeJson.Require(subject.SequenceEqual(cleanup.Subject),
                        "owner-command-conflict", "Restore subject differs from the application.");
                }
            }
            var refusal = kernelOwner.ownerReceived.Admit(request, slots, out admitted, out var duplicate);
            if (refusal != null) return refusal;
            if (duplicate) return admitted!.Result ?? CommandResult.FailedUnknown("owner-command-pending");
            if (request.Operation == "apply")
            {
                // Reserve the descriptor before native execution: an unknown write still needs its cleanup.
                kernelOwner.ownerCleanup.Add(request.CommandId, cleanup);
                if (world.worldHost != true) ImportOwnerIdentities(request.Identities);
                result = ExecuteOwnerCommand(request.PlanId, request.StepIndex, request.CommandId, request.EventId,
                    request.ScopeId, request.SimulationTick, request.Inputs, request.Data);
            }
            else if (admitted!.Application.Suppressed)
                result = CommandResult.Succeeded(RuntimeJson.EmptyObject);
            else
            {
                kernelOwner.ownerRestoreResult = null;
                kernelOwner.OwnerInvocation = true;
                var failures = new List<string>();
                CommandResult? unconfirmed = null;
                void Attempt(Action callback, JsonElement handle, RuntimeEffectContext? effectContext = null)
                {
                    kernelOwner.ownerRestoreResult = null;
                    var previousCapture = kernelOwner.ownerCleanupCapture;
                    kernelOwner.ownerCleanupCapture = new(application!.Request, handle);
                    var threw = false;
                    if (effectContext != null) log.LogEffectRestore(request.Provider, effectContext, "attempt");
                    try { callback(); }
                    catch (Exception error)
                    {
                        threw = true;
                        failures.Add(error is RuntimeContractException contract ? contract.Code : error.GetType().Name);
                    }
                    finally
                    {
                        kernelOwner.ownerCleanupCapture = previousCapture;
                        if (kernelOwner.ownerRestoreResult is { } reported && (reported.Status != CommandStatuses.Succeeded
                            || reported.CommitState != CommitStates.Confirmed)) unconfirmed ??= reported;
                        if (effectContext != null)
                            log.LogEffectRestore(request.Provider, effectContext, threw ? "threw"
                                : kernelOwner.ownerRestoreResult is { } outcome && (outcome.Status != CommandStatuses.Succeeded
                                    || outcome.CommitState != CommitStates.Confirmed) ? "unconfirmed" : "returned");
                    }
                }
                if (request.Operation == "cancel-command")
                {
                    // A terminal command attempts every distinct still-open handle even when another callback
                    // fails. Two returned ports can alias the same physical handle; an earlier control already
                    // attempted that handle and must not be repeated through its other output name.
                    foreach (var priorControl in admitted.Application.Controls.Values)
                        if (!ReferenceEquals(priorControl, admitted) && priorControl.Result?.CommitState != CommitStates.Confirmed)
                            unconfirmed ??= priorControl.Result ?? CommandResult.FailedUnknown("owner-command-pending");
                    var handled = admitted.Application.Controls.Values.Where(control => control.Request.Operation == "cancel")
                        .Select(control => control.Request.Data.GetProperty("handle").GetRawText()).ToHashSet(StringComparer.Ordinal);
                    foreach (var port in cleanup.HandlePorts)
                    {
                        var portId = RuntimeJson.Text(port, "id");
                        if (!port.TryGetProperty("handleKind", out _) || admitted.Application.Controls.ContainsKey("handle:" + portId)
                            || application?.Result == null || !application.Result.Outputs.TryGetProperty(portId, out var handle)
                            || handle.ValueKind != JsonValueKind.Object) continue;
                        if (!handled.Add(handle.GetRawText()) || OwnerHandleEnded(application!, "handle:" + portId, handle)) continue;
                        Attempt(() => CancelHandle(handle, port, "owner-command-cancel"), handle);
                    }
                    if (slots.Contains("restore", StringComparer.Ordinal) && !admitted.Application.Controls.ContainsKey("restore"))
                    {
                        var context = new RuntimeEffectContext(RuntimeJson.EmptyObject, EffectEndReasons.Cancelled, 1, CurrentTick,
                            request.WorldEpoch, request.PlanId, request.NodeId, request.BindingId, cleanup.CapabilityId, request.EventId,
                            cleanup.Subject, "", new[] { OwnerEffectApplicationId(application!.Request) });
                        Attempt(() => registry.Tables.EffectRestores[request.BindingId].Restore(context), RuntimeJson.EmptyObject, context);
                    }
                }
                else if (request.Operation == "cancel")
                {
                    var portId = OwnerCommandLedger.ControlSlot(request)[7..];
                    var port = cleanup.HandlePorts.First(value => RuntimeJson.Text(value, "id") == portId);
                    var handle = request.Data.GetProperty("handle");
                    if (!OwnerHandleEnded(application!, "handle:" + portId, handle))
                        Attempt(() => CancelHandle(handle, port, "owner-cancel"), handle);
                }
                else if (request.Operation == "restore")
                {
                    var data = request.Data;
                    var context = new RuntimeEffectContext(data.GetProperty("handle"), data.GetProperty("reason").GetString()!,
                        data.GetProperty("stacks").GetInt32(), CurrentTick, request.WorldEpoch, request.PlanId, request.NodeId,
                        request.BindingId, cleanup.CapabilityId, request.EventId,
                        data.GetProperty("subject").EnumerateArray().Select(RuntimeJson.Entity).ToArray(),
                        data.GetProperty("status").GetString()!, new[] { OwnerEffectApplicationId(application!.Request) });
                    Attempt(() => registry.Tables.EffectRestores[request.BindingId].Restore(context), data.GetProperty("handle"), context);
                }
                result = failures.Count != 0 ? CommandResult.FailedUnknown("owner-exception", CommandResult.TruncateDetail(string.Join(", ", failures)))
                    : unconfirmed != null ? CommandResult.FailedUnknown(unconfirmed.Code, unconfirmed.Detail)
                    : CommandResult.Succeeded(RuntimeJson.EmptyObject);
            }
        }
        catch (RuntimeContractException error)
        {
            var detail = CommandResult.TruncateDetail(error.Message);
            result = admitted == null || request.Operation == "apply"
                ? CommandResult.Rejected(error.Code, detail) : CommandResult.FailedUnknown(error.Code, detail);
        }
        catch (Exception error)
        {
            result = admitted == null ? CommandResult.Rejected("owner-cleanup-contract", CommandResult.TruncateDetail(error.Message))
            : CommandResult.FailedUnknown("owner-exception", CommandResult.TruncateDetail(error.Message));
        }
        finally { kernelOwner.OwnerInvocation = false; }
        if (ReferenceEquals(result, CommandResult.OwnerDeferred))
        {
            // The handler answers later: the receipt stays open (a replay reads owner-command-pending) and the
            // kernel asks the handler once per advance until it settles or the deadline passes.
            if (admitted is { IsApplication: true } && request.Operation == "apply" && kernelOwner.ownerDeferralTaken is { } deferral)
            {
                kernelOwner.ownerDeferrals.Add(new OwnerDeferred
                {
                    Entry = admitted,
                    Request = deferral,
                    Deadline = CurrentTick + deferral.TimeoutTicks
                });
                kernelOwner.ownerDeferralTaken = null;
                return result;
            }
            result = CommandResult.FailedUnknown("invalid-owner-result", "Only an admitted application can answer later.");
        }
        if (admitted != null) admitted.Result = result;
        return result;
    }

    private static CommandResult OwnerCleanupResult(CommandResult result)
    {
        if (!CommandResultRules.TryValidate(result, out _)) return CommandResult.FailedUnknown("invalid-owner-result");
        // Partial confirmed means some work committed, not that the entire cleanup obligation ended.
        return result.CommitState == CommitStates.Confirmed && result.Status != CommandStatuses.Succeeded
            ? CommandResult.FailedUnknown(result.Code, result.Detail) : result;
    }

    private static bool OwnerHandleEnded(OwnerCommandLedger.Entry application, string slot, JsonElement handle)
        => application.Application.Endings.TryGetValue(slot, out var ending)
            && ending.Result.CommitState == CommitStates.Confirmed && ending.Handle.GetRawText() == handle.GetRawText();

    internal void ReportOwnerEnd(RuntimeHandle handle, string reason, CommandResult result)
    {
        // Only the exact handle of the current cleanup attempt contributes its outcome. OwnerInvocation is
        // also true during apply, and a callback can end another handle while one control is executing.
        var capture = kernelOwner.ownerCleanupCapture;
        var captured = capture != null && RuntimeHandle.TryRead(capture.Handle, out var cleaned) && cleaned == handle;
        if (captured) kernelOwner.ownerRestoreResult ??= result;
        foreach (var item in kernelOwner.ownerReceived.Entries)
        {
            if (!item.IsApplication || item.Result == null || item.Result.Outputs.ValueKind != JsonValueKind.Object
                || captured && item.Request.CommandId == capture!.Application.CommandId) continue;
            foreach (var field in item.Result.Outputs.EnumerateObject())
                if (RuntimeHandle.TryRead(field.Value, out var returned) && returned == handle
                    && item.Application.Slots.Contains("handle:" + field.Name, StringComparer.Ordinal))
                    ReportSpontaneousOwnerEnd(item, "handle:" + field.Name, field.Value, reason, result);
        }
    }
    internal void ReportOwnerWriterEnd(EntityReference equipment, string plan, string node, CommandResult result)
    {
        foreach (var item in kernelOwner.ownerReceived.Entries)
            if (item.IsApplication && item.Result != null && item.Request.PlanId == plan && item.Request.NodeId == node
                && item.Request.Inputs.TryGetProperty("equipment", out var target) && RuntimeJson.Entity(target) == equipment)
            {
                if (kernelOwner.ownerCleanupCapture?.Application.CommandId == item.Request.CommandId) kernelOwner.ownerRestoreResult ??= result;
                // The native writer sweep ended the whole matched group. Even when one control caused it,
                // other applications and returned handles in that group must retain their notification.
                ReportSpontaneousOwnerEnd(item, "cancel-command", default, EffectEndReasons.Expired, result);
            }
    }

    private void ReportSpontaneousOwnerEnd(OwnerCommandLedger.Entry application, string slot, JsonElement handle,
        string reason, CommandResult result)
    {
        if (application.Application.Endings.ContainsKey(slot)) return;
        reason = RuntimeJson.Text(reason);
        result = OwnerCleanupResult(result);
        var request = OwnerCommandLedger.WithEnding(application.Request, slot, handle);
        OwnerCommandLedger.ReadEnding(request, out _, out var endedHandle);
        application.Application.Endings.Add(slot, new(slot, endedHandle.Clone(), reason, result));
        kernelOwner.ownerEndResults.Enqueue((request, reason, result));
        FlushOwnerEndResults();
    }

    internal void ReplayOwnerEndings(string? commandId = null)
    {
        var queued = new HashSet<(string Command, string Slot)>();
        foreach (var notice in kernelOwner.ownerEndResults)
        {
            OwnerCommandLedger.ReadEnding(notice.Request, out var slot, out _);
            queued.Add((notice.Request.CommandId, slot));
        }
        foreach (var notice in kernelOwner.OwnerEndingReceipts(commandId))
        {
            OwnerCommandLedger.ReadEnding(notice.Request, out var slot, out _);
            if (queued.Add((notice.Request.CommandId, slot))) kernelOwner.ownerEndResults.Enqueue(notice);
        }
        FlushOwnerEndResults();
    }

    internal void FlushOwnerEndResults()
    {
        if (kernelOwner.ownerDeferrals.Count != 0 || ownerSettlements.Count != 0)
        {
            SettleOwnerDeferrals();
            FlushOwnerSettlements();
        }
        if (kernelOwner.ownerFlushingEndResults || OwnerEndReporter is not { } report) return;
        kernelOwner.ownerFlushingEndResults = true;
        try
        {
            while (kernelOwner.ownerEndResults.TryPeek(out var notice))
            {
                // Retain the exact cached notice when this tick has no wire budget. A local callback may
                // reenter or retire the world, so only remove the same front item after a successful send.
                if (!report(notice.Request, notice.Result, notice.Reason)) return;
                if (kernelOwner.ownerEndResults.TryPeek(out var front) && ReferenceEquals(front.Request, notice.Request))
                    kernelOwner.ownerEndResults.Dequeue();
            }
        }
        finally { kernelOwner.ownerFlushingEndResults = false; }
    }

    internal void ReceiveOwnerEnd(OwnerOutput request, string reason, CommandResult result)
    {
        if (request.WorldEpoch != WorldEpoch) return;
        if (request.Operation == "apply")
        {
            // The application alone cannot distinguish two returned handles. Require the sender's precise
            // reserved output and preserve the original application bytes for the normal identity comparison.
            if (!OwnerCommandLedger.IsEnding(request) || !RuntimeJson.IsText(reason)) return;
            OwnerOutput application;
            string slot; JsonElement handle;
            try { application = OwnerCommandLedger.ReadEnding(request, out slot, out handle); }
            catch (RuntimeContractException) { return; }
            if (!kernelOwner.ownerIssued.TryGet(application.CommandId, out var admitted) || !admitted.IsApplication
                || !OwnerCommandLedger.Same(admitted.Request, application)
                || !OwnerCommandLedger.EndingMatches(admitted, slot, handle)
                || admitted.Application.Endings.ContainsKey(slot)) return;
            result = OwnerCleanupResult(result);
            admitted.Application.Endings.Add(slot, new(slot, handle.Clone(), reason, result));
            if (result.CommitState == CommitStates.Confirmed)
                kernelOwner.ReleaseOwnerLeases(application.CommandId, slot == "cancel-command" ? null : handle);
            if (admitted.Result != null) kernelOwner.AddSpontaneousOwnerReceipt(application, result);
            return;
        }
        if (!kernelOwner.ownerIssued.TryGet(request.CommandId, out var issued) || !OwnerCommandLedger.Same(issued.Request, request)) return;
        if (issued.Result is { } previous)
        {
            // A native unknown result is not retried or rewritten as success by a conflicting duplicate.
            if (previous.CommitState != CommitStates.None || previous.Status == result.Status
                && previous.Code == result.Code && previous.Detail == result.Detail) return;
        }
        issued.Result = OwnerCleanupResult(result);
        result = issued.Result;
        if (result.CommitState == CommitStates.Confirmed)
        {
            if (request.Operation == "cancel-command") kernelOwner.ReleaseOwnerLeases(request.EventId);
            else if (request.Operation == "cancel") kernelOwner.ReleaseOwnerLeases(request.EventId, request.Data.GetProperty("handle"));
            // Restoring a timed effect alone does not assert that unrelated returned handles were cancelled.
        }
        kernelOwner.ownerEndReceipts.Add(new CommandReceipt(request.CommandId, request.EventId, null, request.EventId,
            request.PlanId, "", "", request.NodeId, request.BindingId, WorldEpoch, CurrentTick, result));
    }
}

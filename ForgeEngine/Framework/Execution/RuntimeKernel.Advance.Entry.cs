using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    // Walk one activation, including control routing, action execution and paired entry/step receipts.
    private void DispatchEntry(Pending pending, Work item, long simulationTick, bool isHost,
        List<CommandReceipt> commands, List<EventReceipt> events, ref int executed,
        ref bool failed, ref string? dispatchFailure)
    {
        var stepPlan = new RuntimeLogPlan { PlanId = item.Plan.Plan.Id, ResourceId = item.Plan.Plan.ResourceId, ResourceRevision = item.Plan.Plan.ResourceRevision };
        var stepOrigin = new KernelLog.StepOrigin(pending.Event, in stepPlan, item.Entry.NodeId);
        var entryGate = pending.LogGate!;
        log.LogEntryStarted(in stepOrigin, entryGate, pending.Provider, pending.Event.BindingId);
        TraceEntry("before", in stepOrigin, pending.Provider, pending.Event.BindingId);
        // This entry's summary of succeeded control/pure/query steps (see IsSummarizedKind); an enclosing entry's is restored below.
        var outerStepSummary = log.stepSummary; var outerStepDepth = log.stepStartDepth;
        log.stepSummary = default;
        string? entryStatus = null, entryReason = null;
        // An activation's memo is `pure`/`query` results; `query` and `pure` steps are never dispatched
        // directly, they are read on demand by whichever step's input names them via `fromStepSlot`.
        // The same table holds what a `control` step wrote when it was entered and an `action`'s value
        // outputs, so one table answers every value a `fromStepSlot` read names in the activation. A
        // result row is not in it: rows live in the activation's typed region and are read through the
        // descriptor the plan compiled. Every dispatch, loop round and schedule resumption is its own
        // activation.
        kernelFlow.BeginActivation();
        var continuation = pending.Schedule?.Resume is { } resume && ReferenceEquals(resume.Work, item) ? resume : null;
        // The action group this activation belongs to is the owner of the suspension it resumes; the restored walk
        // names its group frames from that owner outwards (RestoreWalk), so it is settled before any walk is restored.
        var outerGroup = kernelFlow.activationGroup; var outerSchedule = kernelFlow.walkingSchedule;
        kernelFlow.activationGroup = pending.Wait is { } waited && ReferenceEquals(waited.Item, item) ? waited.Owner
            : continuation != null ? pending.Schedule!.Owner : null;
        kernelFlow.walkingSchedule = continuation != null ? pending.Schedule : null;
        kernelFlow.groupFailurePropagates = false;
        var ownerReply = kernelFlow.ResumeOwner(pending);
        // A `g-wait` resumption is not a schedule: the walk re-enters the wait step itself, with the
        // received event's own frame, which is what the steps after it read.
        int? cursor = ownerReply != null ? ownerReply.Step : pending.Wait is { } waiting && ReferenceEquals(waiting.Item, item)
            ? ResumeWait(item.Entry, waiting, pending)
            : continuation == null ? item.Entry.Start
            : pending.Schedule!.Group is { } resumedGroup ? ResumeGroup(item.Entry, resumedGroup)
            : kernelFlow.ResumeActivation(item.Entry, continuation, pending.Schedule?.Index ?? 0);
        // A resumed flow keeps the play origin it was suspended with; a new one takes it from its own event on demand.
        if (ownerReply != null) kernelFlow.ResumePlay(ownerReply.Play);
        else if (pending.Wait is { } playWait && ReferenceEquals(playWait.Item, item)) kernelFlow.ResumePlay(playWait.Play);
        else if (continuation != null) kernelFlow.ResumePlay(continuation.Play);
        // A group exit nothing is wired to fails the line the group stood on: the walk stops here, as a failed action
        // nothing handled would stop it.
        var walkEnded = false;
        var propagated = kernelFlow.groupFailurePropagates;
        kernelFlow.groupFailurePropagates = false;
        if (propagated) { cursor = null; kernelFlow.controlWalk.Clear(); }
        var controlFailure = (string?)null;
        // An unsuccessful action is only provisionally allowed into its decision chain. A connected selected
        // handling exit discharges it; a null selected exit must stop the whole entry, not resume an enclosing
        // sequence or loop. A present-known branch remains provisional until its boolean branch selects an exit.
        int? pendingResultDecision = null;
        (string Node, StepCommand Command, string Binding, string Status, string Reason)? pendingResultFailure = null;
        while (!propagated)
        {
            if (cursor is not int stepIndex)
            {
                // The walked path ended: the innermost control region advances, or the walk is over.
                if (!kernelFlow.ResumeControl(item.Entry, out var resumed)) { walkEnded = true; break; }
                cursor = resumed;
                continue;
            }
            var step = item.Entry.Steps[stepIndex];
            // The identity is the four names it always was, composed from the two halves that are
            // already quoted: this dispatch's publisher and event, and this step's own plan and node.
            var command = StepCommandOf(pending, step.CommandSuffix, kernelFlow.WalkRound());
            string? effectApplicationId = null;
            CommandResult? result = null; CommandContext? commandContext = null;
            var invoked = false; int? next = null; var stopped = false;
            // The reservation this step holds and has not yet handed to a settlement or to an owner flight: a throw
            // anywhere between must settle it, or the resource stays held with no code naming why.
            KernelExecutor.CostHold? openHold = null;
            // The step's resolved inputs, kept outside the guarded region because a refusal the kernel
            // decided there still has to report the recipients those inputs named.
            StepInputs? resolvedInputs = null, traceInputs = null;
            JsonElement? traceParameters = step.Parameters;
            var deepTraceStarted = false;
            // `step.*` belongs to the provider of the binding that executes the step, which is not the entry's
            // trigger binding: a trigger may route into another package's action.
            var stepProvider = step.ProviderId;
            // One activation may execute this many steps before the plan is refused by name. The control
            // dispatch below is charged the same as an action: a control is a step too.
            kernelFlow.stepExecutions++;
            if (kernelFlow.StepBudgetSpent(item.Plan.Plan.Limits)) { controlFailure = RuntimeAbiCodes.DispatchStepBudget; break; }
            var isAction = step.Kind == StepKind.Action;
            if (isAction) executor.BeginActionResult(item.Entry, stepIndex, step);
            var presentationTier = step.PresentationTier;
            var ownerTier = isAction && !presentationTier && IsOwnerStep(step);
            var traceKind = presentationTier ? "presentation" : ownerTier ? "owner" : step.NodeKind;
            var stepGate = step.LogGate;
            log.LogStepStarted(in stepOrigin, stepGate, stepProvider, step.NodeId, step.BindingId, ref command, traceKind);
            try
            {
                RuntimeJson.Require(plans.ModulesLive(item.Plan), "binding-lifecycle", step.BindingId);
                RuntimeJson.Require(pending.Schedule == null || pending.Schedule.Handle.Active, "schedule-cancelled", pending.Event.EventId);
                RuntimeJson.Require(world.cancelled.Count == 0 || !world.cancelled.Contains((pending.Provider, pending.Event.ScopeId)), "scope-cancelled", pending.Event.ScopeId);
                var inputFrame = ownerReply != null
                    ? ownerReply.Inputs
                    : ResolveTestedInputs(item, step, pending);
                resolvedInputs = inputFrame;
                // The recipient bound is charged before the step's own work: a set the row region
                // cannot hold is refused here, so no handler runs and no over-budget row is built.
                RequireRecipientBudget(step, inputFrame);
                traceInputs = inputFrame;
                traceParameters = behaviorTraceObservers.Count == 0 ? null : inputFrame.Parameters;
                TraceStep("before", in stepOrigin, stepProvider, step.NodeId, step.BindingId, ref command,
                    traceKind, TraceFrame(inputFrame), traceParameters);
                deepTraceStarted = true;
                if (step.Kind == StepKind.Control)
                {
                    // Control routing is internal to the kernel: no handler lookup, no command receipt —
                    // only `action` steps ever produce one. Every control kind is dispatched in that one
                    // place, so `next`, `then`, `pulse` and `body` share one walker. A control that refuses
                    // — a stale handle, an exhausted budget — rejects the event; it never invents a command.
                    int? controlCursor = null;
                    try
                    {
                        controlFailure = step.StoredOutputs != null
                        ? EnterStoredValueControl(item, step, inputFrame, pending.Event.ScopeId, out controlCursor)
                        : EnterControl(item, stepIndex, step, pending, inputFrame, inputFrame.Parameters, out controlCursor);
                    }
                    catch (RuntimeContractException ex) { controlFailure = ex.Code; }
                    if (controlFailure != null)
                    {
                        log.LogStepFinished(in stepOrigin, stepGate, stepProvider, step.NodeId, step.BindingId, ref command,
                            traceKind, CommandStatuses.Rejected, null, controlFailure);
                        TraceStep("after", in stepOrigin, stepProvider, step.NodeId, step.BindingId, ref command,
                            traceKind, TraceFrame(inputFrame), traceParameters, null,
                            new RuntimeLogResult { Status = CommandStatuses.Rejected, Reason = controlFailure });
                        entryStatus = CommandStatuses.Rejected; entryReason = controlFailure;
                        break;
                    }
                    log.LogStepFinished(in stepOrigin, stepGate, stepProvider, step.NodeId, step.BindingId, ref command,
                        traceKind, CommandStatuses.Succeeded, null, "routed");
                    JsonElement? controlOutputs = behaviorTraceObservers.Count == 0 ? null : step.StoredOutputs != null
                        ? StoredControlFrame(step) : executor.stepFrames.TryGetValue(stepIndex, out var routedFrame) ? routedFrame : null;
                    TraceStep("after", in stepOrigin, stepProvider, step.NodeId, step.BindingId, ref command,
                        traceKind, TraceFrame(inputFrame), traceParameters, controlOutputs,
                        new RuntimeLogResult { Status = CommandStatuses.Succeeded, Reason = "routed" });
                    if (pendingResultDecision == stepIndex)
                    {
                        if (controlCursor == null)
                        {
                            if (pendingResultFailure is { } failedResult)
                            {
                                var failedCommand = failedResult.Command;
                                log.LogEntryStopped(in stepOrigin, failedResult.Node, ref failedCommand, failedResult.Binding, failedResult.Status, failedResult.Reason);
                            }
                            break;
                        }
                        pendingResultDecision = step.Control == Blocks.ControlFlowPresent.Id
                            && controlCursor == step.Successors[0] ? controlCursor : null;
                        if (pendingResultDecision == null) pendingResultFailure = null;
                    }
                    // A control routes the walk itself: it writes no receipt and produces no command, so
                    // it leaves the step body exactly where the walk continues.
                    cursor = controlCursor;
                    goto continueWalk;
                }
                // A stop that names a playback handle is about that playback and not about the step's viewers,
                // so it is answered before the recipient set is looked at.
                if (StopsSound(step) && TryStopSound(step, inputFrame.Json, out var soundStopped)) result = soundStopped;
                // An action whose many-recipient set resolved empty has nobody to change, on any tier: the kernel
                // answers `no-targets` — succeeded, confirmed, no rows — before a cost is reserved, an effect is
                // opened, a holder or an audience is resolved or a handler runs. The step is not invoked, so the
                // refusal path below records the proven empty row set a later count decision reads.
                else if (KernelExecutor.ResolvedEmptyRecipients(step, inputFrame)) result = CommandResult.NoTargets();
                else if (presentationTier)
                {
                    // A `presentation` step is dispatched, not invoked here. What it writes is the game's
                    // own presentation, so the advance decides the timing and the recipient set and records
                    // the intent the network layer hands to each recipient; this is also the step's own
                    // receipt, and the code says so instead of claiming a command nobody ran on this
                    // machine. The recorded result carries no commit and no fact, so an entrypoint that
                    // continues past a presentation step never sees a committed write it did not make.
                    var presented = EnterPresentation(item.Plan, step, pending, command.Id, inputFrame,
                        in stepOrigin, inputFrame.Parameters, out var presentationResult);
                    if (presented != null)
                    {
                        result = presentationResult;
                        log.LogStepFinished(in stepOrigin, stepGate, stepProvider, step.NodeId, step.BindingId, ref command, traceKind, in presentationResult);
                        TraceStep("after", in stepOrigin, stepProvider, step.NodeId, step.BindingId, ref command,
                            traceKind, TraceFrame(inputFrame), traceParameters, presentationResult.Outputs, TraceResult(in presentationResult));
                        entryStatus = presentationResult.Status; entryReason = presentationResult.Code;
                        controlFailure = presented; break;
                    }
                    result = presentationResult;
                }
                else if (ownerTier && ownerReply == null)
                {
                    // An owner step waits for another machine outside the one scheduler, so it cannot be a line of an
                    // action group: refused by name before anything is reserved or sent.
                    RuntimeJson.Require(kernelFlow.CurrentGroup() == null, "group-owner-step", step.NodeId);
                    var ownerHold = executor.ReserveCost(step);
                    if (ownerHold is { Reserved: false })
                        result = executor.SettleCost(CommandResult.Rejected(RuntimeAbiCodes.InsufficientCost, step.NodeId), ownerHold, null);
                    else
                    {
                        openHold = ownerHold;
                        // An `owner` step is dispatched to the one machine that holds what it changes: the
                        // host resolved the inputs and decided the timing, and the holder runs the same
                        // handler through the owner entry point. The walk continues on a confirmed commit,
                        // so an owner step may sit in the middle of an entry.
                        var owned = EnterOwner(item.Plan, step, pending, command.Id, inputFrame.Json, out var ownerResult);
                        if (owned != null)
                        {
                            if (ownerHold != null) executor.ReleaseCost(ownerHold);
                            openHold = null;
                            result = ownerResult;
                            log.LogStepFinished(in stepOrigin, stepGate, stepProvider, step.NodeId, step.BindingId, ref command, traceKind, in ownerResult);
                            TraceStep("after", in stepOrigin, stepProvider, step.NodeId, step.BindingId, ref command,
                                traceKind, TraceFrame(inputFrame), traceParameters, ownerResult.Outputs, TraceResult(in ownerResult));
                            entryStatus = ownerResult.Status; entryReason = ownerResult.Code;
                            controlFailure = owned; break;
                        }
                        kernelFlow.CaptureOwner(item, stepIndex, pending, inputFrame, inputFrame.Parameters, ownerHold);
                        openHold = null;
                        entryStatus = CommandStatuses.Partial; entryReason = "owner-awaiting-result";
                        break;
                    }
                }
                else
                {
                    // The step's binding decides who runs what: a handler the provider registered runs
                    // here, and nothing about the walk is special to an action — except the one action
                    // the kernel answers for itself, whose instances are the kernel's own records.
                    if (StatusContract.IsDispel(step.CapabilityId))
                    {
                        executed++; invoked = true;
                        result = NormalizeInvokedResult(DispelStatuses(step, inputFrame.Json, pending.Event));
                        executor.PublishRows(step, inputFrame, ref result);
                    }
                    else
                    {
                        // A step that declares a cost pays it before anything of the step is written:
                        // the items are reserved in the order the card wrote them, and a shortfall
                        // rejects the whole step — no effect, no handler, no state — with everything
                        // already reserved given back.
                        var hold = ownerReply == null ? executor.ReserveCost(step) : ownerReply.Hold;
                        if (hold is { Reserved: false })
                            result = executor.SettleCost(CommandResult.Rejected(RuntimeAbiCodes.InsufficientCost, step.NodeId), hold, null);
                        else
                        {
                            openHold = hold;
                            var handler = step.Handler!;
                            // A step whose card carries a duration option opens its effect before the handler
                            // runs: the handle is the kernel's, and the clock and the layer count are only made
                            // real once the command says it committed. A `handle-budget` refusal here is a
                            // pre-invocation failure like any other, so nothing is written for it.
                            PendingEffect? effect = null;
                            string? effectEstablishmentFailure = null;
                            if (step.Effect is { } duration)
                            {
                                try
                                {
                                    effect = BeginEffect(step, duration, item.Plan.Plan, inputFrame, pending.Event,
                                    CommandPrefixOf(pending), item.Entry.NodeId);
                                }
                                catch (RuntimeContractException error) when (ownerReply != null && EffectKept(ownerReply.Result!))
                                {
                                    // This is a delayed result of an already invoked native action. Retiring the
                                    // previous layer cannot turn that write into a pre-invocation no-write rejection.
                                    if (!ownerReply.CancelSent)
                                    {
                                        if (!kernelOwner.ownerIssued.TryGet(ownerReply.Request.CommandId, out var admittedOwner)
                                            || !admittedOwner.Application.Controls.ContainsKey("cancel-command"))
                                            kernelOwner.QueueOwnerControl(ownerReply.Request, "cancel-command", RuntimeJson.EmptyObject);
                                        ownerReply.CancelSent = true;
                                    }
                                    effectEstablishmentFailure = error.Code;
                                    effectApplicationId = OwnerEffectApplicationId(ownerReply.Request);
                                }
                            }
                            var context = new CommandContext(pending.Event, simulationTick, null!,
                                item.Plan.Names, step.NodeId, default, inputFrame, isHost, entityInstance, ReportedTickRate,
                                this, step.ProviderId, item.Plan.ProviderGenerations[step.Frames.Index], item.Entry.NodeId, step.BindingId,
                                step.Frames.Row?.Port ?? "", step.Frames.Row, step.Contract);
                            context.DeferCommandId(in command);
                            if (effect != null)
                            {
                                effect.ApplicationId = ownerReply != null ? OwnerEffectApplicationId(ownerReply.Request)
                                    : effect.AddLayer ? command.Id : effect.Slot.Layers[^1].CommandId;
                                effectApplicationId = effect.ApplicationId;
                                context.EffectApplicationId = effectApplicationId;
                                context.EffectHandleId = effect.Slot.HandleId.IsCast ? effect.Slot.HandleId : null;
                                // The count is what this application leaves behind, not what stands before it:
                                // a fresh instance is its first layer even though the layer is only committed
                                // once the command says it committed, and an application at the cap adds none.
                                context.EffectStacks = effect.Slot.Layers.Count + (effect.AddLayer ? 1 : 0);
                                context.EffectAddLayer = effect.AddLayer;
                            }
                            executed++; invoked = true;
                            effectCancellationProvider = step.CapabilityId == Blocks.ActionEffectCancel.Id ? step.ProviderId : null;
                            if (ownerReply == null)
                            {
                                BeginCommandInvocation(ref context); commandContext = context;
                                executor.ArmTypedWrite(step, inputFrame, isHost);
                                result = NormalizeInvokedResult(handler(context));
                                // Rows written through the typed writer are the step's result as they stand in their
                                // region. The document a receipt, a trace, an effect handle or a deferred result
                                // would read is rendered only for a step that has one of those readers.
                                executor.elideRowDocument = executor.TypedWrite.Used && !kernelScheduler.collectReceipts && behaviorTraceObservers.Count == 0
                                    && effect == null && executor.Invocation.DeferredResults.Count == 0;
                            }
                            else result = kernelOwner.ImportOwnerHandles(ownerReply, step, ownerReply.Result!, effect);
                            ownerReply = null;
                            // The rows are validated and written before anything else reads the result: the
                            // effect is finalized, the charge is settled and the facts are published only
                            // for a result the row ABI accepted, so a malformed row set commits nothing.
                            var rows = executor.PublishRows(step, inputFrame, ref result);
                            if (effect != null) CommitEffect(effect, result, simulationTick, command.Id, ref result);
                            // The charge is settled against what the command wrote, and the result rows
                            // carry the two amounts the settlement produced.
                            openHold = null;
                            result = executor.SettleCost(result, hold, rows);
                            if (effectEstablishmentFailure != null)
                            {
                                result = CommandResult.FailedUnknown(result.Outputs, effectEstablishmentFailure,
                                    "The owner action ran, but its effect lifetime could not be established; exact application cleanup is tracked separately.",
                                    result.Facts.ToArray());
                                if (rows is { } written)
                                {
                                    var writer = new FrameWriter(executor.resultArena.Slots, written.Row.Slot, written.Row.Span, executor.resultArena.Strings);
                                    for (var row = 0; row < written.Count; row++)
                                        ResultRowAbi.SetCostFailure(ref writer, written.Row, row, effectEstablishmentFailure);
                                    result = executor.WithRows(result, written, settled: hold != null);
                                }
                            }
                            if (result.Facts.Count > 0)
                                PublishConfirmedFacts(result, step.ProviderId,
                                    item.Plan.ProviderGenerations[step.Frames.Index], pending.Event.ScopeId,
                                    simulationTick, events);
                        }
                    }
                }
            }
            catch (RuntimeContractException ex) when (!invoked)
            { result = executor.SettleOpenHold(PreInvocationFailure(ex), ref openHold); }
            catch (Exception ex) when (!invoked)
            { result = executor.SettleOpenHold(CommandResult.Rejected("precondition-failed", CommandResult.TruncateDetail(ex.GetType().Name + ": " + ex.Message)), ref openHold); }
            catch (RuntimeContractException ex)
            { executor.resultRows.Remove(step.Frames.Index); result = executor.SettleOpenHold(CommandResult.FailedUnknown("handler-exception", CommandResult.TruncateDetail(ex.GetType().Name + ": " + ex.Message)), ref openHold); }
            catch (Exception ex)
            { executor.resultRows.Remove(step.Frames.Index); result = executor.SettleOpenHold(CommandResult.FailedUnknown("handler-exception", CommandResult.TruncateDetail(ex.GetType().Name + ": " + ex.Message)), ref openHold); }
            finally { currentCommand = null; effectCancellationProvider = null; executor.rowDocumentElided = executor.elideRowDocument; executor.elideRowDocument = false; }
            var commandResult = result ?? (invoked
                ? CommandResult.FailedUnknown("null-result", "Handler returned null after invocation.")
                : CommandResult.Rejected("precondition-failed", "Precondition failed before handler invocation."));
            // Input resolution can reject a control before EnterControl. It has no command of its own:
            // close the step and reject the entry/event explicitly, just as a routing refusal does.
            if (step.Kind == StepKind.Control)
            {
                controlFailure = commandResult.Code;
                entryStatus = commandResult.Status; entryReason = controlFailure;
                log.LogStepFinished(in stepOrigin, stepGate, stepProvider, step.NodeId, step.BindingId, ref command,
                    traceKind, commandResult.Status, commandResult.CommitState, controlFailure, commandResult.Detail);
                if (!deepTraceStarted)
                    TraceStep("before", in stepOrigin, stepProvider, step.NodeId, step.BindingId, ref command,
                        traceKind, TraceFrame(traceInputs), traceParameters);
                TraceStep("after", in stepOrigin, stepProvider, step.NodeId, step.BindingId, ref command,
                    traceKind, TraceFrame(traceInputs), traceParameters, null, TraceResult(in commandResult));
                break;
            }
            // A refusal the kernel decided before the handler ran still reports the recipients the step
            // named: the same descriptor writes one row per explicit recipient, carrying this command's
            // own conclusion and code, so a refused target is a row rather than a missing one.
            if (!invoked) commandResult = executor.RefuseWithRows(step, resolvedInputs, pending.Event, commandResult);
            if (commandContext is { } completedContext)
            {
                try { kernelScheduler.FinalizeDeferredResults(completedContext, commandResult); }
                finally { EndCommandInvocation(in completedContext); }
            }
            if (!deepTraceStarted)
            {
                TraceStep("before", in stepOrigin, stepProvider, step.NodeId, step.BindingId, ref command,
                    traceKind, TraceFrame(traceInputs), traceParameters);
                deepTraceStarted = true;
            }
            TraceStep("after", in stepOrigin, stepProvider, step.NodeId, step.BindingId, ref command,
                traceKind, TraceFrame(traceInputs), traceParameters, commandResult.Outputs, TraceResult(in commandResult));
            // An action's own result row is a value a later step reads through `fromStepSlot`, so it is
            // published into the same activation table a `pure`/`query`/`control` step's frame goes into:
            // one slot table per activation, and one place a step's outputs are read from. Its rows are
            // not part of that frame: they live in the activation's typed region and are read through
            // the descriptor, so the receipt's rendering of them never becomes an execution frame.
            if (isAction) executor.stepFrames[stepIndex] = executor.ValueFrame(commandResult, step.Frames.Row);
            if (kernelScheduler.collectReceipts)
                commands.Add(new CommandReceipt(command.Id, pending.Event.EventId, pending.Event.CauseId, pending.Event.RootEventId ?? pending.Event.EventId, item.Plan.Plan.Id, item.Plan.Plan.ResourceId, item.Plan.Plan.ResourceRevision, step.NodeId, step.BindingId, WorldEpoch, simulationTick, commandResult));
            // A presentation step's host receipt is the dispatch (partial, nothing committed), so the walk continues past it.
            // An unsuccessful action still stops the entry unless its immediate successor explicitly decides
            // on this action's current, ABI-accepted result. That path handles the failure; it never turns the
            // command, its receipt, its facts or its settlement into a success. An absent row region grants no
            // continuation, including malformed or unwritten results and those of an earlier activation.
            if (commandResult.Status != CommandStatuses.Succeeded
                && !(commandResult.Status == CommandStatuses.Partial && (commandResult.CommitState == CommitStates.Confirmed
                    || (StartsSound(step) || StopsSound(step) || presentationTier) && commandResult.CommitState == CommitStates.None)))
            {
                entryStatus = commandResult.Status; entryReason = commandResult.Code;
                if (step.HasResultDecision && executor.resultRows.ContainsKey(step.Frames.Index))
                {
                    next = step.Successors[0]; pendingResultDecision = next;
                    pendingResultFailure = (step.NodeId, command, step.BindingId, commandResult.Status, commandResult.Code);
                }
                else { cursor = null; stopped = true; }
            }
            else next = step.Successors.Count > 0 ? step.Successors[0] : null;
            log.LogStepFinished(in stepOrigin, stepGate, stepProvider, step.NodeId, step.BindingId, ref command, traceKind, in commandResult,
                effectApplicationId: effectApplicationId,
                rows: executor.rowDocumentElided && executor.resultRows.TryGetValue(step.Frames.Index, out var finishedRows) ? finishedRows : null);
            if (stopped) { entryStatus = commandResult.Status; entryReason = commandResult.Code; }
            // The stop shares the code table's level line with step.finished, and is written only when the entry
            // really had a step left: the last step of an entry stops nothing.
            if (stopped && step.Successors.Count > 0) log.LogEntryStopped(in stepOrigin, step.NodeId, ref command, step.BindingId, commandResult.Status, commandResult.Code);
            // `stop-entrypoint` means the walk is over, not that the region ended: the enclosing control is
            // not advanced, so a sequence's next exit and a loop's next round both
            // stay unwalked after a rejected, failed, cancelled or expired command. Falling through to
            // `ResumeControl` here is what let a sibling step run after an unconfirmed failure.
            if (stopped) break;
            cursor = next;
        continueWalk:;
        }
        // The groups this walk opened or stood in learn how it ended: a region still open keeps what it interrupted,
        // and a walk that stopped fails the attempt of the group it stopped in.
        if (kernelFlow.activationGroup != null || kernelFlow.controlWalk.Count > 0) kernelFlow.SettleGroups(item.Entry, stopped: !walkEnded);
        kernelFlow.activationGroup = outerGroup; kernelFlow.walkingSchedule = outerSchedule;
        if (controlFailure != null && entryStatus == null)
        { entryStatus = CommandStatuses.Rejected; entryReason = controlFailure; }
        var finalEntryStatus = entryStatus ?? CommandStatuses.Succeeded;
        var finalEntryReason = entryReason ?? "completed";
        log.LogEntryFinished(in stepOrigin, entryGate, pending.Provider, pending.Event.BindingId,
            finalEntryStatus, finalEntryReason);
        log.stepSummary = outerStepSummary; log.stepStartDepth = outerStepDepth;
        TraceEntry("after", in stepOrigin, pending.Provider, pending.Event.BindingId,
            new RuntimeLogResult { Status = finalEntryStatus, Reason = finalEntryReason });
        // A control that refused — a stale handle, an exhausted iteration or step budget — stops this
        // entry's walk and is reported as the event's rejection instead of an invented command result.
        if (controlFailure != null)
        {
            // A walk that reached a schedule or scope its own flow had already stopped — a countdown that was
            // halted — is cancelled the way a pre-dispatch drop is, not refused as a fault.
            var cancelled = entryStatus == CommandStatuses.Cancelled;
            if (cancelled) log.LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, controlFailure, pending.Provider);
            else if (controlFailure.EndsWith("-budget", StringComparison.Ordinal))
                log.LogBudgetExceeded(pending.Event.BindingId, pending.Event.EventId, controlFailure);
            else log.LogEventRejected(pending.Event.BindingId, pending.Event.EventId, controlFailure, pending.Provider);
            kernelScheduler.AddReceipt(events, pending.Event.EventId, cancelled ? "cancelled" : "rejected", controlFailure);
            failed = true;
            dispatchFailure ??= controlFailure;
        }
    }
}

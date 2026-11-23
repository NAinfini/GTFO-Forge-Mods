using System.Collections.Generic;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    internal enum DuePendingDecision { Dispatch, Skip, Defer }

    // One host tick drains due dispatches in queue order, stopping at the first deferred budget.
    private void DispatchDueEvents(long simulationTick, bool isHost, List<CommandReceipt> commands,
        List<EventReceipt> events, List<ScheduleReceipt>? scheduleReports, List<TriggerGateReceipt> gateRefusals,
        ref int processed, ref int executed)
    {
        while (true)
        {
            // A group decides at a safe point: no queue entry is half taken here.
            FlushGroupChecks();
            if (!eventQueue.queue.TryPeek(out var pending, out var priority) || priority.Tick > simulationTick) break;
            var decision = TakeDuePending(pending, scheduleReports);
            if (decision == DuePendingDecision.Skip) continue;
            if (decision == DuePendingDecision.Defer) break;
            DispatchPending(pending, simulationTick, isHost, commands, events, scheduleReports, gateRefusals, ref processed, ref executed);
            // A resumed wait leaves its group only once the line it resumed has been walked.
            if (pending.Wait is { Resuming: true } resumed)
            {
                resumed.Resuming = false;
                if (resumed.Owner is { } owner) { owner.Waits.Remove(resumed); kernelFlow.QueueGroupCheck(owner); }
            }
        }
        FlushGroupChecks();
    }

    // Decides whether the head event is dispatched, discarded or left queued for the next budget.
    private DuePendingDecision TakeDuePending(Pending pending, List<ScheduleReceipt>? scheduleReports)
    {
        if (pending.Schedule != null && !PreparePulse(pending, scheduleReports)) return DuePendingDecision.Skip;
        // A release the card no longer waits for — a later arrival moved the quiet window past it, or its
        // set went with a released plan or an ended world — is dropped here rather than dispatched. It is
        // not a refusal the author has to read: the arrival that moved the window is already reported as
        // `gate-debounce`, and this entry is the same firing moved to a later tick.
        if (pending.Release is { } queued && !kernelVariables.triggerGates.ReleaseCurrent(queued.PlanId, queued.NodeId, queued.Subject, queued.Generation))
        { eventQueue.queue.Dequeue(); return DuePendingDecision.Skip; }
        if (pending.Schedule != null && kernelFlow.scheduledThisTick >= MaximumScheduledPulsesPerTick)
        {
            scheduleReports?.Add(kernelFlow.ScheduleReport(pending.Schedule, "deferred", "scheduled-tick-budget"));
            if (!kernelScheduler.deferredLogged)
            {
                kernelScheduler.deferredLogged = true;
                log.LogEventDeferred(pending.Event.BindingId, pending.Event.EventId, "scheduled-tick-budget");
            }
            return DuePendingDecision.Defer;
        }
        // Reserve the occurrence before GateWork can arm debounce or charge a counter. Checking capacity alone
        // would race reentrant publishes from a gate resolver or an effect's restore callback.
        if (pending.Schedule is { Resume: null } && kernelFlow.ClaimPulse(pending, scheduleReports) is { } refused)
        {
            eventQueue.queue.Dequeue();
            kernelScheduler.AddReceipt(kernelScheduler.tickEvents, pending.Event.EventId, "rejected", refused);
            return DuePendingDecision.Skip;
        }
        var budget = kernelScheduler.BudgetRefusal(pending);
        if (budget != null)
        {
            if (pending.Schedule != null) scheduleReports?.Add(kernelFlow.ScheduleReport(pending.Schedule, "deferred", budget));
            if (!kernelScheduler.deferredLogged)
            {
                kernelScheduler.deferredLogged = true;
                log.LogEventDeferred(pending.Event.BindingId, pending.Event.EventId, budget);
            }
            return DuePendingDecision.Defer;
        }
        // Control continuations never charge entry gates. Keep their original missed-pulse policy while they
        // wait for budget, and claim their occurrence only once that budget has accepted it.
        if (pending.Schedule?.Resume != null && kernelFlow.ClaimPulse(pending, scheduleReports) is { } late)
        {
            eventQueue.queue.Dequeue();
            kernelScheduler.AddReceipt(kernelScheduler.tickEvents, pending.Event.EventId, "rejected", late);
            return DuePendingDecision.Skip;
        }
        eventQueue.queue.Dequeue();
        // A pending that carries a suspended `g-wait` resumes the walk only if it is the outcome that won:
        // the event that woke it, or a deadline no event beat. The loser is dropped without a receipt.
        if (pending.Wait != null && !AdmitWait(pending)) return DuePendingDecision.Skip;
        return DuePendingDecision.Dispatch;
    }

    // Dispatch one admitted event, including its entries and result receipts.
    private void DispatchPending(Pending pending, long simulationTick, bool isHost, List<CommandReceipt> commands,
        List<EventReceipt> events, List<ScheduleReceipt>? scheduleReports, List<TriggerGateReceipt> gateRefusals,
        ref int processed, ref int executed)
    {
        if (!TryBeginDispatch(pending, events, scheduleReports, gateRefusals, ref processed, out var active)) return;
        var failed = false;
        string? dispatchFailure = null;
        for (var activeIndex = 0; activeIndex < active.Count; activeIndex++)
        {
            var item = active[activeIndex];
            DispatchEntry(pending, item, simulationTick, isHost, commands, events, ref executed, ref failed, ref dispatchFailure);
        }
        CompleteDispatch(pending, events, failed, dispatchFailure);
    }

    private bool TryBeginDispatch(Pending pending, List<EventReceipt> events,
        List<ScheduleReceipt>? scheduleReports, List<TriggerGateReceipt> gateRefusals,
        ref int processed, out IReadOnlyList<Work> active)
    {
        active = null!;
        // The dispatch's own event table: row 0 is this event and any event a handler of it publishes is
        // appended while it is walked, so an event value is only ever a row of the dispatch reading it.
        Walking(pending);
        entityLives.OpenEntityMemo(pending.Event.RenderedOutputs, pending.EntityAt, pending.Entity);
        // A source that unregistered and a scope its owner cancelled end the event the same way.
        if (!registry.IsRegistered(pending.Provider, pending.Generation)
            || world.cancelled.Count > 0 && world.cancelled.Contains((pending.Provider, pending.Event.ScopeId)))
        {
            if (pending.Schedule != null) kernelFlow.EndSchedule(pending.Schedule, "cancelled", "source-lifecycle");
            log.LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "source-lifecycle", pending.Provider);
            kernelScheduler.AddReceipt(events, pending.Event.EventId, "cancelled", "source-lifecycle"); return false;
        }
        // The `end_on` abilities are read before the walk: an effect a receiver's own event ends is over by
        // the time the same dispatch could apply it again, and nothing is dispatched for the ending event.
        if (!kernelOwner.ownerResumes.ContainsKey(pending)) EndEffectOn(pending);
        var groups = kernelScheduler.PendingPlans(pending, out active);
        if (pending.GateRefusals is { } refusals)
        {
            // The gate's own answer, recorded before the walk: which entry was refused and which of its
            // options refused it is the whole of what a plan author has to debug with.
            gateRefusals.AddRange(refusals);
            foreach (var refusal in refusals) LogGateRefused(refusal, pending.Event.BindingId, pending.Event.EventId);
        }
        var count = 0;
        for (var i = 0; i < groups.Count; i++) count += groups[i].Steps;
        var gated = active.Count == 0 && pending.GateRefusals is { Count: > 0 };
        // A provider's pulse is a new event even when every entry's gate refuses it. Consume that occurrence
        // and keep its next pulse: cooldown, thresholds and debounce decide entries, not the source's lifetime.
        if (pending.Schedule != null && (active.Count > 0 || gated)) kernelFlow.AdmitPulse(pending, scheduleReports);
        if (active.Count == 0)
        {
            if (pending.Work.Count == 0)
            {
                // The dispatch was queued for the `end_on` abilities it ends and nothing else: no card
                // claimed it, so it is an event nobody consumed rather than a plan that went away.
                kernelScheduler.AddReceipt(events, pending.Event.EventId, "ignored", "no-consumer"); return false;
            }
            // A dispatch every claimed entry refused is not a dispatch with nothing left to do: the plans
            // are loaded and their cards matched, and the gate is the reason nothing ran.
            var code = gated ? "gate-refused" : "plan-unloaded";
            if (pending.Schedule is { } schedule)
            {
                if (!gated) kernelFlow.EndSchedule(schedule, "cancelled", code);
                else if (schedule.TotalPulses is { } completedPulses && schedule.Index >= completedPulses)
                    kernelFlow.EndSchedule(schedule, "completed", "all-pulses-dispatched");
            }
            log.LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, code, pending.Provider);
            kernelScheduler.AddReceipt(events, pending.Event.EventId, gated ? "rejected" : "cancelled", code); return false;
        }
        kernelScheduler.eventsThisTick++; kernelScheduler.commandsThisTick += count; processed++;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            group.Plan.TickUsage.Charge(1, group.Steps);
        }
        // The publisher's gate is looked up once here; the dispatch and entry record points of this dispatch read it.
        pending.LogGate = log.DispatchGate(pending.Provider);
        log.LogDispatchStarted(pending.LogGate, pending.Provider, pending.Event);
        TraceTrigger("before", pending.Provider, pending.Event);
        return true;
    }

    private void CompleteDispatch(Pending pending, List<EventReceipt> events, bool failed, string? dispatchFailure)
    {
        // The dispatch boundary speaks the command status vocabulary the log reads: the receipt's own
        // `processed` / `dispatched` disposition is a tick result, never a log reason (§3.2 excluded codes).
        var finalDispatchStatus = failed ? CommandStatuses.Rejected : CommandStatuses.Succeeded;
        var finalDispatchReason = failed ? (dispatchFailure ?? "rejected") : "completed";
        log.LogDispatchFinished(pending.LogGate!, pending.Provider, pending.Event, finalDispatchStatus, finalDispatchReason);
        TraceTrigger("after", pending.Provider, pending.Event,
            new RuntimeLogResult { Status = finalDispatchStatus, Reason = finalDispatchReason });
        if (pending.Schedule != null && pending.Schedule.Handle.Active
            && pending.Schedule.TotalPulses is { } pulses && pending.Schedule.Index >= pulses)
            kernelFlow.EndSchedule(pending.Schedule, "completed", "all-pulses-dispatched");
        // A group whose exit was just walked — or whose retry finished in the same walk — ends with that dispatch.
        if (pending.Schedule is { Group: { Ending: true, Queued: false } group } schedule && schedule.Handle.Active)
            kernelFlow.EndSchedule(schedule, "completed", group.EndCode);
        if (!failed) kernelScheduler.AddReceipt(events, pending.Event.EventId, "processed", "dispatched");
    }
}

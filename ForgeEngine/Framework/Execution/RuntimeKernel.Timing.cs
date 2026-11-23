using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    /// <summary>Live schedules the registered providers may hold together (their timers and pulse series). A plan's
    /// own suspensions are not counted here: they are charged to the plan that holds them
    /// (<see cref="MaximumPlanSuspensions"/>), so no behavior can spend a provider's timers.</summary>
    public const int MaximumSchedules = 256;
    /// <summary>Suspensions one plan — one behavior — may hold at once: its live `delay`s, pulse series (`repeat`
    /// with an interval, `ramp`), action groups and `wait_event`s together. The count is the plan's own, so a
    /// behavior that fills it is refused `plan-suspension-budget` by itself and every other behavior keeps its own
    /// room. Each suspension also holds one handle of the kernel's handle pool, so one plan holds at most a quarter
    /// of that pool.</summary>
    public const int MaximumPlanSuspensions = 256;
    /// <summary>The most schedules one world can hold, which is what a migration image may carry: the providers'
    /// own, plus the plans' continuations, each of which holds one handle of the pool.</summary>
    internal static int MaximumLiveSchedules(RuntimeLimits limits) => MaximumSchedules + limits.MaxLiveHandles;
    public const int MaximumSchedulePulses = 65536;
    public const int MaximumScheduledPulsesPerTick = 64;
    public const int MaximumScheduleEntityReferences = 16;
    internal long ModuleRandomSeed { get; private set; }
    internal sealed class ScheduledJob
    {
        internal readonly RuntimeScheduleHandle Handle;
        internal readonly long Generation;
        internal readonly RuntimeEvent Template;
        internal readonly PulseSchedule Spec;
        internal readonly IReadOnlyList<Work> Work;
        internal readonly string Identity;
        /// <summary>The tick this schedule's pulse series is anchored at. A `restart` moves the anchor while the
        /// pulse index keeps counting, so the next pulse lands one interval later without the schedule ever
        /// replaying an occurrence the ledger already recorded.</summary>
        internal long FirstTick;
        internal readonly long? EndTick;
        /// <summary>The pulse limit, or <c>null</c> for a series that only cancellation, its scope's end or its
        /// world's end stops: an `interval` control whose `count` input the plan omitted (ruling 160.2). Every
        /// provider-facing schedule is bounded — <see cref="Schedule"/> refuses one that is not — so this is null
        /// only on a control continuation.</summary>
        internal readonly int? TotalPulses;
        internal int Index;
        /// <summary>Set while a re-armed schedule still has the pulse it had already placed sitting in the queue:
        /// the compaction drops that entry, which is the only way a live schedule's stale pulse is removed.</summary>
        internal bool ReArmed;
        /// <summary>The lifecycle generation this job's capture was last validated at, and the entities that
        /// validation resolved. A capture cannot change, and the registry and the plan table cannot change without
        /// the generation moving, so while the number stands still the only thing left to ask is whether the
        /// entities the template names are still alive — which is what <see cref="Liveness"/> is for.</summary>
        internal long ValidatedGeneration = -1;
        // Loading an additional plan changes subject consumption without invalidating the old schedule's work.
        // Its descriptor cache follows subscriptions as well as source lifecycle; neither counter is a clock.
        internal long ValidatedSubscriptionGeneration = -1;
        internal EntityCheck[] Liveness = Array.Empty<EntityCheck>();
        /// <summary>The actual event template's selected subject, decoded with its validation. A control
        /// continuation has no new event and therefore keeps this empty.</summary>
        internal EventSubject EndSubject;
        /// <summary>Set only on a plan's own control continuation: the pulse re-enters the plan's entry instead of
        /// publishing an event. Set once on creation, and moved only by an action group, whose own schedule re-aims its
        /// pulse at the exit it chose (RuntimeKernel.Groups.ArmGroup) and keeps the region it interrupted here.</summary>
        internal Continuation? Resume { get; set; }
        /// <summary>The action group this suspension is a line of, and — on a group's own schedule — the group.</summary>
        internal FlowGroup? Owner;
        internal FlowGroup? Group;
        internal IRuntimeModuleContinuation? Module { get; init; }
        internal Pending? ModulePending;
        internal long ModuleDueTick;
        /// <summary>The pool slot a control continuation's timer occupies, so a schedule ending for any reason —
        /// expiry, cancel, world end — frees it in one place; -1 when it holds none.</summary>
        internal int HandleSlot { get; init; } = -1;
        internal ScheduledJob(RuntimeScheduleHandle handle, long generation, RuntimeEvent template, PulseSchedule spec,
            IReadOnlyList<Work> work, string identity, long firstTick, long? endTick, int? total)
        { Handle = handle; Generation = generation; Template = template; Spec = spec; Work = work; Identity = identity; FirstTick = firstTick; EndTick = endTick; TotalPulses = total; }
        internal long NextTick => Module != null ? ModuleDueTick : FirstTick + Index * Spec.IntervalTicks;
    }
    private static string Fingerprint(object value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(RuntimeJson.StableBytes(RuntimeJson.From(value)), hash);
        return Convert.ToHexString(hash);
    }
    private RuntimeEvent CaptureScheduledEvent(RuntimeModuleHandle handle, RuntimeEvent value, out EventSubject endSubject)
    {
        RuntimeJson.Require(handle.IsRegistered, "module-unregistered", handle.ProviderId);
        RuntimeJson.Require(world.worldStarted && value.WorldEpoch == WorldEpoch, "stale-world", value.EventId);
        RuntimeJson.Require(world.worldHost != false, "not-host", "This world is not authoritative.");
        RuntimeJson.Text(value.EventId); RuntimeJson.Text(value.ScopeId); RuntimeJson.Integer(value.SimulationTick);
        RuntimeJson.Require(value.SimulationTick >= Math.Max(0, CurrentTick), "schedule-in-past", value.EventId);
        RuntimeJson.Require(!world.cancelled.Contains((handle.ProviderId, value.ScopeId)), "scope-cancelled", value.ScopeId);
        RuntimeJson.Require(registry.BindingViews.TryGetValue(value.BindingId, out var binding) && binding!.ProviderId == handle.ProviderId, "binding-owner", value.BindingId);
        var text = RawJson.Text(value.Outputs);
        RuntimeJson.Require(text.Length <= MaximumEventPayloadBytes, "event-payload-budget", value.EventId);
        var snapshot = value with { Outputs = RawJson.Snapshot(value.Outputs, text) };
        if (currentCommand is { } command)
            snapshot = snapshot with { CauseId = command.CommandId, RootEventId = command.RootEventId, CausalDepth = command.CausalDepth + 1 };
        else RuntimeJson.Require(snapshot.CausalDepth == 0 && snapshot.CauseId == null && (snapshot.RootEventId == null || snapshot.RootEventId == snapshot.EventId), "external-causality", "Root schedules cannot invent causes.");
        endSubject = executor.AdmitDocument(snapshot, binding!);
        var references = 0;
        var cells = snapshot.Cells!;
        for (var i = 0; i < cells.Length; i++)
        {
            if (binding!.Outputs[i].Type != "entity") continue;
            if (cells[i].Reference is EntityReference) references++;
            else if (cells[i].Reference is EntityReference[] many) references += many.Length;
        }
        RuntimeJson.Require(references <= MaximumScheduleEntityReferences, "schedule-target-budget", "Fixed schedule targets exceed the explicit limit; no truncation is performed.");
        return snapshot;
    }
    internal ScheduleResult Schedule(RuntimeModuleHandle owner, RuntimeEvent template, PulseSchedule spec)
    {
        ReadThread();
        if (RefuseFactAdmission(owner, template) is { } admissionCode)
            return new ScheduleResult("rejected", admissionCode, null);
        Thread();
        try
        {
            var snapshot = CaptureScheduledEvent(owner, template, out var endSubject);
            RuntimeJson.Require(spec != null && spec.FirstPulse is FirstPulse.Immediate or FirstPulse.AfterInterval
                && spec.MissedPulsePolicy is MissedPulsePolicy.CatchUp or MissedPulsePolicy.SkipMissed, "schedule-policy", "Explicit first/missed pulse policies are required.");
            RuntimeJson.Integer(spec!.IntervalTicks, 1);
            RuntimeJson.Require(spec.MaxPulses != null || spec.LifetimeTicks != null, "unbounded-schedule", "A finite pulse count or lifetime is required.");
            if (spec.MaxPulses != null) RuntimeJson.Integer(spec.MaxPulses.Value, 1, MaximumSchedulePulses);
            if (spec.LifetimeTicks != null) RuntimeJson.Integer(spec.LifetimeTicks.Value, 1);
            var first = snapshot.SimulationTick + (spec.FirstPulse == FirstPulse.Immediate ? 0 : spec.IntervalTicks);
            RuntimeJson.Integer(first);
            long? end = spec.LifetimeTicks == null ? null : snapshot.SimulationTick + spec.LifetimeTicks.Value;
            if (end != null) RuntimeJson.Integer(end.Value);
            var total = (long)(spec.MaxPulses ?? MaximumSchedulePulses);
            if (end != null) total = Math.Min(total, end.Value <= first ? 0 : (end.Value - first - 1) / spec.IntervalTicks + 1);
            if (spec.MaxPulses == null && end != null) RuntimeJson.Require(end.Value <= first || (end.Value - first - 1) / spec.IntervalTicks + 1 <= MaximumSchedulePulses, "schedule-pulse-budget", "Lifetime contains too many pulses.");
            if (total > 0) RuntimeJson.Require(spec.IntervalTicks <= (RuntimeJson.MaxSafeInteger - first) / Math.Max(1, total - 1) || total == 1, "schedule-overflow", template.EventId);
            RuntimeJson.Require(plans.Subscriptions.TryGetValue(snapshot.BindingId, out var subscribed), "no-consumer", template.EventId);
            // A domain schedule reaches the same plans a direct publish would: the mount filter is applied here too,
            // so a plan attached to another map object never runs off someone else's timer.
            var work = ClaimedWork(subscribed!.ForEvent(snapshot), snapshot, endSubject);
            RuntimeJson.Require(work.Count > 0, "attachment-mismatch", template.EventId);
            RuntimeJson.Require(snapshot.CausalDepth >= 0 && snapshot.CausalDepth <= Limits.MaxCausalDepth && work.All(w => snapshot.CausalDepth <= w.Plan.Plan.Limits.MaxCausalDepth), "causal-depth", template.EventId);
            if (spec.MissedPulsePolicy == MissedPulsePolicy.CatchUp)
            {
                foreach (var bindingId in work.SelectMany(w => w.Entry.Steps.Where(s => s.NodeKind != "pure").Select(s => s.BindingId)).Append(snapshot.BindingId).Distinct(StringComparer.Ordinal))
                {
                    var capability = registry.Tables.Capabilities[RuntimeJson.Text(registry.Tables.Bindings[bindingId], "capabilityId")];
                    RuntimeJson.Require(capability.GetProperty("parameters").TryGetProperty("scheduleReplay", out var replay) && replay.ValueKind == JsonValueKind.String && replay.GetString() == "fixed-inputs", "schedule-history-required", bindingId);
                }
            }
            var key = new LedgerKey(owner.ProviderId, LedgerSpace.Schedule, snapshot.EventId);
            var fingerprint = LedgerFingerprint(RuntimeJson.StableText(RuntimeJson.From(new { snapshot, spec })));
            if (eventQueue.history.TryGetValue(key, out var previous))
            {
                if (previous.Fingerprint == fingerprint) return new ScheduleResult("duplicate", "duplicate-schedule", null);
                log.LogEventRejected(snapshot.BindingId, snapshot.EventId, "schedule-id-conflict", owner.ProviderId);
                return new ScheduleResult("rejected", "schedule-id-conflict", null);
            }
            RuntimeJson.Require(kernelFlow.LedgerHasRoom(), "event-history-budget", "Replay ledger capacity reached.");
            RuntimeJson.Require(ProviderSchedules() < MaximumSchedules, "schedule-budget", "Active schedule capacity reached.");
            RuntimeJson.Require(total == 0 || eventQueue.queue.Count < Limits.MaxQueuedEvents, "queue-budget", snapshot.EventId);
            foreach (var group in work.GroupBy(w => w.Plan))
            {
                RuntimeJson.Require(group.Sum(w => w.Entry.DispatchableStepCount) <= group.Key.Plan.Limits.MaxCommandsPerTick, "event-command-budget", group.Key.Plan.Id);
                RuntimeJson.Require(eventQueue.queue.UnorderedItems.Count(x => x.Element.Work.Any(w => ReferenceEquals(w.Plan, group.Key))) < group.Key.Plan.Limits.MaxQueuedEvents, "plan-queue-budget", group.Key.Plan.Id);
            }
            RuntimeJson.Require(work.Sum(w => w.Entry.DispatchableStepCount) <= Limits.MaxCommandsPerTick, "event-command-budget", snapshot.EventId);
            var handle = new RuntimeScheduleHandle(this, owner.ProviderId, snapshot.EventId, WorldEpoch);
            var identity = Fingerprint(new { provider = owner.ProviderId, generation = owner.Generation, world = WorldEpoch, scope = snapshot.ScopeId, schedule = snapshot.EventId });
            var job = new ScheduledJob(handle, owner.Generation, snapshot, spec, work, identity, first, end, (int)total)
            {
                // The capture was validated on the way in, so the job starts out already checked at this generation
                // and carries the entities that check resolved.
                ValidatedGeneration = world.lifecycleGeneration,
                ValidatedSubscriptionGeneration = SubscriptionGeneration,
                EndSubject = endSubject,
                Liveness = executor.LivenessOf(snapshot, registry.BindingViews[snapshot.BindingId])
            };
            eventQueue.history.Add(key, new LedgerRow(fingerprint, Math.Max(0, CurrentTick)));
            if (total == 0) { handle.Status = "completed"; handle.Code = "no-pulses-before-end"; }
            else { kernelFlow.schedules.Add((owner.ProviderId, handle.ScheduleId), job); eventQueue.QueuePulse(job); }
            return new ScheduleResult("scheduled", handle.Code, handle);
        }
        // A refused schedule is an event that never entered the queue, recorded once under the same split as a publish.
        catch (RuntimeContractException ex)
        {
            if (ex.Code.EndsWith("-budget", StringComparison.Ordinal)) log.LogBudgetExceeded(template.BindingId, template.EventId, ex.Code);
            else log.LogEventRejected(template.BindingId, template.EventId, ex.Code, owner.ProviderId);
            return new ScheduleResult("rejected", ex.Code, null);
        }
        catch (ObjectDisposedException)
        {
            log.LogEventRejected(template.BindingId, template.EventId, "disposed-payload", owner.ProviderId);
            return new ScheduleResult("rejected", "disposed-payload", null);
        }
    }
    internal ScheduleResult Schedule(RuntimeModuleHandle owner, string scope, long dueTick,
        IRuntimeModuleContinuation work, EntityReference? lifetime)
    {
        Thread();
        try
        {
            RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
            RuntimeJson.Require(world.worldStarted, "stale-world", scope);
            RuntimeJson.Require(world.worldHost != false, "not-host", scope);
            RuntimeJson.Text(scope); RuntimeJson.Integer(dueTick);
            RuntimeJson.Require(dueTick >= Math.Max(0, CurrentTick), "schedule-in-past", scope);
            RuntimeJson.Require(work != null, "schedule-work", scope);
            RuntimeJson.Require(!world.cancelled.Contains((owner.ProviderId, scope)), "scope-cancelled", scope);
            EntityCheck[] checks = Array.Empty<EntityCheck>();
            if (currentCommand != null && executor.currentPending != null)
                checks = executor.LivenessOf(executor.currentPending.Event, registry.BindingViews[executor.currentPending.Event.BindingId]);
            if (lifetime != null)
            {
                RuntimeJson.Require(lifetime.WorldEpoch == WorldEpoch && IsEntityCurrent(lifetime), "stale-entity", lifetime.Id);
                RuntimeJson.Require(entityLives.TryEntityCheck(lifetime, out var check), "entity-resolver-unavailable", lifetime.Id);
                var capture = new EntityCheck[checks.Length + 1];
                checks.CopyTo(capture, 0); capture[^1] = check; checks = capture;
            }
            RuntimeJson.Require(checks.Length <= MaximumScheduleEntityReferences, "schedule-target-budget", scope);
            RuntimeJson.Require(ProviderSchedules() < MaximumSchedules, "schedule-budget", scope);
            RuntimeJson.Require(eventQueue.queue.Count < Limits.MaxQueuedEvents, "queue-budget", scope);
            RuntimeJson.Require(kernelFlow.LedgerHasRoom(), "event-history-budget", scope);
            var id = "module:" + ++kernelFlow.scheduleSequence;
            var handle = new RuntimeScheduleHandle(this, owner.ProviderId, id, WorldEpoch);
            // This envelope is queue ownership, never an event sent to plans. It is allocated once and reused.
            var template = new RuntimeEvent(id, "", WorldEpoch, dueTick, scope, RuntimeJson.EmptyObject);
            var job = new ScheduledJob(handle, owner.Generation, template,
                new PulseSchedule(1, FirstPulse.Immediate, MissedPulsePolicy.CatchUp, MaximumSchedulePulses),
                Array.Empty<Work>(), id, dueTick, null, MaximumSchedulePulses)
            {
                Module = work,
                ModuleDueTick = dueTick,
                Liveness = checks,
                ValidatedGeneration = world.lifecycleGeneration,
                ValidatedSubscriptionGeneration = SubscriptionGeneration
            };
            job.ModulePending = new Pending(owner.ProviderId, owner.Generation, template, job.Work, job);
            eventQueue.history.Add(new LedgerKey(owner.ProviderId, LedgerSpace.Schedule, id),
                new LedgerRow(LedgerFingerprint(id), Math.Max(0, CurrentTick)));
            kernelFlow.schedules.Add((owner.ProviderId, id), job); eventQueue.QueuePulse(job);
            return new ScheduleResult("scheduled", "scheduled", handle);
        }
        catch (RuntimeContractException error) { return new ScheduleResult("rejected", error.Code, null); }
    }
    /// <summary>The live schedules the providers hold: every schedule that is not a plan's own continuation.</summary>
    private int ProviderSchedules()
    {
        var held = 0;
        foreach (var job in kernelFlow.schedules.Values)
            if (job.Resume == null) held++;
        return held;
    }

    /// <summary>Refuses one more suspension of <paramref name="plan"/> once the plan already holds its whole share:
    /// its pulse series, delays and groups in the schedule table and its waits that have not retired yet. The plan
    /// that asks is the one refused, by its own code, so a behavior that fills its share stops only itself. The two
    /// tables are walked at the moment a step suspends, never per tick, and the walk allocates nothing.</summary>
    private void RequirePlanSuspensionRoom(LoadedPlan plan, string nodeId)
    {
        var held = 0;
        foreach (var job in kernelFlow.schedules.Values)
            if (job.Resume is { } resume && ReferenceEquals(resume.Work.Plan, plan)) held++;
        foreach (var waits in kernelVariables.waits.Values)
            foreach (var wait in waits)
                if (ReferenceEquals(wait.Item.Plan, plan)) held++;
        RuntimeJson.Require(held < MaximumPlanSuspensions, "plan-suspension-budget", nodeId);
    }

    internal bool CancelSchedule(RuntimeScheduleHandle handle)
    {
        // Terminal handle disposal is cleanup, not new runtime work.
        ReadThread(); NoLifecycleMutation();
        if (!kernelFlow.schedules.TryGetValue((handle.ProviderId, handle.ScheduleId), out var job) || !ReferenceEquals(job.Handle, handle)) return false;
        kernelFlow.EndSchedule(job, "cancelled", "explicit-cancel"); kernelFlow.PruneQueue(); return true;
    }

    /// <summary>Whether one queued entry still belongs to a live schedule. The one entry a re-armed schedule had
    /// already placed is stale although its handle is live, so the compaction clears the flag as it drops it.</summary>
    internal static bool Live(ScheduledJob job)
    {
        if (!job.Handle.Active) return false;
        if (!job.ReArmed) return true;
        job.ReArmed = false;
        return false;
    }
    /// <summary>
    /// Why a live schedule cannot dispatch any more, or null while it can. The answer has two halves, and only one
    /// of them can change from frame to frame: the source, the scope and the plans behind the capture are the
    /// lifecycle generation's business — a module or plan cannot come or go without it moving — while an entity's
    /// life ends under the kernel without any registration changing at all. So a frame at a standing generation
    /// asks the capture's resolvers, and the full check runs only where the number moved.
    /// </summary>
    private string? ScheduleEndCode(ScheduledJob job)
    {
        if (job.ValidatedGeneration != world.lifecycleGeneration || job.ValidatedSubscriptionGeneration != SubscriptionGeneration)
        {
            var code = RevalidateSchedule(job);
            if (code != null) return code;
        }
        return entityLives.EntityLivenessCode(job.Liveness);
    }
    /// <summary>The full check of one capture against the tables it was resolved from.</summary>
    private string? RevalidateSchedule(ScheduledJob job)
    {
        if (!registry.IsRegistered(job.Handle.ProviderId, job.Generation) || job.Handle.WorldEpoch != WorldEpoch) return "source-lifecycle";
        if (world.cancelled.Count > 0 && world.cancelled.Contains((job.Handle.ProviderId, job.Template.ScopeId))) return "scope-cancelled";
        for (var i = 0; i < job.Work.Count; i++)
            if (!plans.Loaded.TryGetValue(job.Work[i].Plan.Plan.Id, out var live) || !ReferenceEquals(live, job.Work[i].Plan)) return "plan-unloaded";
        if (job.Module != null)
        {
            job.ValidatedGeneration = world.lifecycleGeneration;
            job.ValidatedSubscriptionGeneration = SubscriptionGeneration;
            return null;
        }
        try
        {
            var binding = registry.BindingViews[job.Template.BindingId];
            var subject = executor.AdmitDocument(job.Template, binding);
            job.EndSubject = job.Resume == null ? subject : default;
            job.Liveness = executor.LivenessOf(job.Template, binding);
        }
        catch (RuntimeContractException ex) { return ex.Code; }
        job.ValidatedGeneration = world.lifecycleGeneration;
        job.ValidatedSubscriptionGeneration = SubscriptionGeneration;
        return null;
    }
    private bool PreparePulse(Pending pending, List<ScheduleReceipt>? reports)
    {
        var job = pending.Schedule!;
        if (!job.Handle.Active)
        {
            eventQueue.queue.Dequeue();
            log.LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "source-lifecycle", pending.Provider);
            return false;
        }
        // A handler earlier in this tick can end the life of the captured source/target. Revalidating the template before every pulse keeps the remaining catch-up from reaching an object that no longer exists and removes the whole timer on its next step instead of leaving it to reject one pulse at a time.
        var invalid = ScheduleEndCode(job);
        if (invalid != null)
        {
            eventQueue.queue.Dequeue(); kernelFlow.EndSchedule(job, "cancelled", invalid);
            reports?.Add(kernelFlow.ScheduleReport(job, "cancelled", invalid));
            log.LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, invalid, pending.Provider);
            return false;
        }
        if (job.Module != null)
        {
            // The normal TakeDuePending budget gate will defer this same queue head when all 64 slots are spent.
            if (kernelFlow.scheduledThisTick >= MaximumScheduledPulsesPerTick) return true;
            eventQueue.queue.Dequeue(); kernelFlow.scheduledThisTick++; job.Index++; job.Handle.DispatchedPulses++;
            try
            {
                var next = job.Module.Resume(CurrentTick);
                if (!job.Handle.Active) return false;
                if (next == null) kernelFlow.EndSchedule(job, "completed", "completed");
                else if (job.Index >= MaximumSchedulePulses) kernelFlow.EndSchedule(job, "rejected", "schedule-pulse-budget");
                else
                {
                    RuntimeJson.Integer(next.Value);
                    RuntimeJson.Require(next.Value >= CurrentTick, "schedule-in-past", job.Handle.ScheduleId);
                    job.ModuleDueTick = next.Value; eventQueue.QueuePulse(job);
                }
            }
            catch (RuntimeContractException error) { kernelFlow.EndSchedule(job, "rejected", error.Code); }
            catch (Exception error)
            {
                log.LogObserverFailed(job.Handle.ProviderId, "module-continuation-failed: " + RuntimeDiagnostics.Describe(error));
                kernelFlow.EndSchedule(job, "failed", "module-continuation-failed");
            }
            return false;
        }
        // Once an occurrence owns a ledger row and a gate decision, budget delay cannot turn it into a missed
        // occurrence. Dropping it here would spend once/threshold state without ever consuming that decision.
        if (!pending.PulseClaimed && job.Spec.MissedPulsePolicy == MissedPulsePolicy.SkipMissed)
        {
            if (job.EndTick != null && CurrentTick >= job.EndTick)
            {
                // A lifetime bound only ever comes with a finite pulse count, so this schedule is bounded here.
                eventQueue.queue.Dequeue(); job.Handle.SkippedPulses += (job.TotalPulses ?? 0) - job.Index;
                kernelFlow.EndSchedule(job, "expired", "lifetime-ended"); reports?.Add(kernelFlow.ScheduleReport(job, "expired", "lifetime-ended"));
                log.LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "lifetime-ended", pending.Provider);
                return false;
            }
            var missed = (CurrentTick - job.NextTick) / job.Spec.IntervalTicks;
            var skipped = (int)(job.TotalPulses is { } limit ? Math.Min(limit - job.Index - 1L, missed) : missed);
            if (skipped > 0)
            {
                eventQueue.queue.Dequeue(); job.Index += skipped; job.Handle.SkippedPulses += skipped;
                log.LogEventCancelled(pending.Event.BindingId, pending.Event.EventId, "missed-pulses", pending.Provider);
                reports?.Add(kernelFlow.ScheduleReport(job, "skipped", "missed-pulses")); eventQueue.QueuePulse(job); return false;
            }
        }
        return true;
    }
    private void CleanScheduledLifetimes(List<ScheduleReceipt>? reports)
    {
        if (kernelFlow.schedules.Count == 0) return;
        kernelFlow.endedSchedules.Clear();
        foreach (var job in kernelFlow.schedules.Values)
        {
            var code = ScheduleEndCode(job);
            if (code != null) kernelFlow.endedSchedules.Add((job, code));
        }
        for (var i = 0; i < kernelFlow.endedSchedules.Count; i++)
        {
            var (job, code) = kernelFlow.endedSchedules[i];
            kernelFlow.EndSchedule(job, "cancelled", code);
            reports?.Add(kernelFlow.ScheduleReport(job, "cancelled", code));
        }
        kernelFlow.PruneQueue();
    }
}

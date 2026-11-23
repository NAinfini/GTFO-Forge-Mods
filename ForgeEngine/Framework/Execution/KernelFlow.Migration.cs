using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>The schedules and waits a host-migration image carries, and the handle slots that travel or stay behind
/// with them. Unloading a plan does not walk the queue, so the image captures the ownership the next dispatch would
/// see; the flow decides it once and every component writes its part against the same answer.</summary>
internal sealed record MigrationSurvivors(HashSet<ScheduledJob> Schedules, HashSet<WaitJob> Waits,
    HashSet<(string Provider, string Id)> ScheduleKeys, HashSet<int> WaitHandles, HashSet<int> OmittedWaitHandles);

internal sealed partial class KernelFlow
{
    /// <summary>Which schedules and waits are still owed, without asking gates or invoking lifecycle callbacks just to
    /// take a read-only image.</summary>
    internal MigrationSurvivors SurvivingMigration()
    {
        var liveSchedules = schedules.Values.Where(job => job.Handle.Active
            && registry.IsRegistered(job.Handle.ProviderId, job.Generation) && job.Handle.WorldEpoch == world.WorldEpoch
            && !world.cancelled.Contains((job.Handle.ProviderId, job.Template.ScopeId)) && job.Work.All(plans.WorkLive)).ToHashSet();
        // A module continuation is native code with no data form: no snapshot can carry it, so none is taken while one is pending.
        // The refusal leaves the other buffer's earlier image standing, where a snapshot the new host could not preflight would replace it.
        RuntimeJson.Require(!liveSchedules.Any(job => job.Module != null), "migration-provider-data",
            "A native module continuation cannot be captured as portable active state.");
        var liveScheduleKeys = liveSchedules.Select(job => (job.Handle.ProviderId, job.Handle.ScheduleId)).ToHashSet();
        var allWaits = kernelVariables.waits.Values.SelectMany(list => list)
            .Concat(eventQueue.queue.UnorderedItems.Where(row => row.Element.Wait != null).Select(row => row.Element.Wait!)).Distinct().ToArray();
        var liveWaits = allWaits.Where(job => !job.Retired && job.Handle.IsRegistered
            && !world.cancelled.Contains((job.Handle.ProviderId, job.Scope)) && kernelEffects.SlotHoldsWaitHandle(job) && plans.WorkLive(job.Item)).ToHashSet();
        // A subscription wait also stores its identity in ScheduleId, but owns no ScheduledJob. Its retained
        // slot is proven by the wait's full handle identity above, including a resolved wait queued to resume.
        var liveWaitHandles = liveWaits.Select(job => job.HandleSlot).ToHashSet();
        var omittedWaitHandles = allWaits.Where(job => kernelEffects.SlotHoldsWaitHandle(job) && !liveWaits.Contains(job))
            .Select(job => job.HandleSlot).ToHashSet();
        return new MigrationSurvivors(liveSchedules, liveWaits, liveScheduleKeys, liveWaitHandles, omittedWaitHandles);
    }

    /// <summary>This component's parts of a host-migration image: the schedule sequence, the surviving schedules with
    /// the flow each resumes, the surviving waits, and the queued events that would still be dispatched.</summary>
    internal void CaptureMigration(Dictionary<string, object?> image, MigrationSurvivors survivors)
    {
        // A woken wait is no longer in the subscription table, but its queued continuation still owns its handle.
        // Both sources are needed; retired waits and losing timeouts must never be resurrected.
        image["waits"] = survivors.Waits.Select(SerializeMigrationWait).ToArray();
        image["schedules"] = survivors.Schedules.Select(SerializeMigrationSchedule).ToArray();
        image["pending"] = eventQueue.queue.UnorderedItems
            .Where(entry => registry.IsRegistered(entry.Element.Provider, entry.Element.Generation)
                && !kernelOwner.ownerResumes.ContainsKey(entry.Element)
                && !world.cancelled.Contains((entry.Element.Provider, entry.Element.Event.ScopeId))
                && (entry.Element.Schedule is not { } job || survivors.Schedules.Contains(job)
                    && entry.Priority.Tick == job.NextTick)
                && (entry.Element.Wait is not { } wait || survivors.Waits.Contains(wait)
                    && (!entry.Element.WaitTimeout || !wait.Resolved))
                && (entry.Element.Release is not { } release
                    || kernelVariables.triggerGates.ReleaseCurrent(release.PlanId, release.NodeId, release.Subject, release.Generation)))
            .OrderBy(entry => entry.Priority.Tick).ThenBy(entry => entry.Priority.Sequence)
            .Select(entry => new
            {
                provider = entry.Element.Provider,
                @event = entry.Element.Event,
                remainingTicks = entry.Priority.Tick - world.Tick,
                order = entry.Priority.Sequence,
                sequence = entry.Element.Sequence,
                tabled = entry.Element.Tabled,
                schedule = entry.Element.Schedule?.Handle.ScheduleId,
                wait = entry.Element.Wait?.HandleSlot,
                waitTimeout = entry.Element.WaitTimeout,
                decision = entry.Element.Admitted == null ? null : new
                {
                    work = entry.Element.Admitted.Where(plans.WorkLive).Select(SerializeMigrationWork).ToArray(),
                    refusals = entry.Element.GateRefusals?.Where(refusal => entry.Element.Work.Any(item => plans.WorkLive(item)
                        && item.Plan.Plan.Id == refusal.PlanId && item.Entry.NodeId == refusal.NodeId)).ToArray()
                            ?? Array.Empty<TriggerGateReceipt>()
                },
                release = entry.Element.Release == null ? null : new
                {
                    planId = entry.Element.Release.PlanId,
                    nodeId = entry.Element.Release.NodeId,
                    subject = entry.Element.Release.Subject,
                    generation = entry.Element.Release.Generation
                },
                work = entry.Element.Schedule != null || entry.Element.Wait != null ? Array.Empty<object>()
                    : entry.Element.Work.Where(plans.WorkLive).Select(SerializeMigrationWork).ToArray()
            }).ToArray();
        image["scheduleSequence"] = scheduleSequence;
    }

    private object SerializeMigrationSchedule(ScheduledJob job) => new
    {
        provider = job.Handle.ProviderId,
        id = job.Handle.ScheduleId,
        template = job.Template,
        spec = job.Spec,
        identity = job.Identity,
        remainingTicks = job.NextTick - world.Tick,
        endTicks = job.EndTick - world.Tick,
        total = job.TotalPulses,
        index = job.Index,
        dispatched = job.Handle.DispatchedPulses,
        skipped = job.Handle.SkippedPulses,
        work = job.Work.Select(SerializeMigrationWork).ToArray(),
        handleSlot = job.HandleSlot,
        resume = job.Resume is not { } resume ? null : new
        {
            step = resume.Step,
            output = resume.Output,
            handle = resume.HandleValue,
            walk = SerializeMigrationWalk(resume.Walk),
            indexed = resume.Indexed,
            flow = SerializeMigrationFlow(resume.Flow, resume.Work),
            play = SerializeMigrationPlay(resume.Play),
            loopValues = SerializeMigrationWalk(resume.LoopValues),
            ramp = resume.Ramp is not { } ramp ? null : new
            {
                inputs = new
                {
                    from = ramp.From,
                    to = ramp.To,
                    duration = ramp.DurationTicks,
                    release_duration = ramp.ReleaseTicks,
                    release_delay = ramp.ReleaseDelayTicks,
                    exponent = ramp.Exponent
                },
                parameters = new { mode = ramp.Stepped ? "stepped" : "continuous" }
            }
        },
        // An action group's membership and its own state travel with the schedule that holds them; the member
        // counts are not written, because the restored links are the members.
        group = MigrationGroupOwner(job.Owner),
        flowGroup = job.Group is not { } own ? null : new
        {
            timeLimit = own.TimeLimit,
            attempts = own.Attempts,
            attempt = own.Attempt,
            retryDelay = own.RetryDelay,
            suspended = own.Suspended,
            queued = own.Queued,
            ending = own.Ending,
            lineFailed = own.LineFailed,
            endCode = own.EndCode
        }
    };

    private static object? MigrationGroupOwner(FlowGroup? owner)
        => owner == null ? null : new { provider = owner.Job.Handle.ProviderId, id = owner.Job.Handle.ScheduleId };

    private static object SerializeMigrationWait(WaitJob job) => new
    {
        provider = job.Handle.ProviderId,
        work = SerializeMigrationWork(job.Item),
        step = job.Step,
        receivedOutput = job.ReceivedOutput,
        timeoutOutput = job.TimeoutOutput,
        target = job.Target,
        scope = job.Scope,
        message = job.Message,
        handle = job.HandleValue,
        handleSlot = job.HandleSlot,
        walk = SerializeMigrationWalk(job.Walk),
        flow = SerializeMigrationFlow(job.Flow, job.Item),
        resolved = job.Resolved,
        resumeOutput = job.ResumeOutput,
        play = SerializeMigrationPlay(job.Play),
        group = MigrationGroupOwner(job.Owner)
    };

    /// <summary>Reads the flow's parts of an image into this staged component. The schedule sequence only moves
    /// forward. Only the machine that becomes host rebuilds the schedules, the waits and the queue; the handles they
    /// hand out belong to <paramref name="destination"/>, the live kernel the image is committed into. The queue
    /// already passed admission and is rebuilt directly: publishing again would wake waits twice and restart a
    /// continuation at its entry.</summary>
    internal void RestoreMigration(JsonElement image, RuntimeKernel destination, bool isHost)
    {
        scheduleSequence = Math.Max(scheduleSequence, RuntimeJson.Integer(image.GetProperty("scheduleSequence"), 0, RuntimeJson.MaxSafeInteger));
        if (!isHost) return;
        migrationGroupLinks.Clear();
        foreach (var entry in RuntimeJson.Rows(image, "schedules", MaximumLiveSchedules(limits))) RestoreMigrationSchedule(entry, destination);
        // Every wait holds a live handle of its own, so the handle pool is the bound the image is read against.
        var restoredWaits = RuntimeJson.Rows(image, "waits", limits.MaxLiveHandles)
            .Select(row => RestoreMigrationWait(row, destination)).ToDictionary(job => job.HandleSlot);
        LinkMigrationGroups();
        foreach (var entry in RuntimeJson.Rows(image, "pending", limits.MaxQueuedEvents))
            RestoreMigrationPending(entry, restoredWaits);
    }

    private void RestoreMigrationSchedule(JsonElement row, RuntimeKernel destination)
    {
        RuntimeJson.Shape(row, "provider id template spec identity remainingTicks endTicks total index dispatched skipped work handleSlot resume", "group flowGroup");
        var provider = RuntimeJson.Text(row, "provider");
        var work = RuntimeJson.Rows(row, "work", limits.MaxTotalSteps).Select(RequireMigrationWork).ToArray();
        RuntimeJson.Require(work.Length > 0, "migration-work-plan", "A migrated schedule needs its claimed work.");
        var specValue = row.GetProperty("spec");
        RuntimeJson.Shape(specValue, "intervalTicks firstPulse missedPulsePolicy maxPulses lifetimeTicks");
        var spec = new PulseSchedule(RuntimeJson.Integer(specValue.GetProperty("intervalTicks"), 1),
            (FirstPulse)RuntimeJson.Integer(specValue.GetProperty("firstPulse"), 0, 1),
            (MissedPulsePolicy)RuntimeJson.Integer(specValue.GetProperty("missedPulsePolicy"), 0, 1),
            specValue.GetProperty("maxPulses").ValueKind == JsonValueKind.Null ? null
                : (int)RuntimeJson.Integer(specValue.GetProperty("maxPulses"), 1, MaximumSchedulePulses),
            specValue.GetProperty("lifetimeTicks").ValueKind == JsonValueKind.Null ? null
                : RuntimeJson.Integer(specValue.GetProperty("lifetimeTicks"), 1));
        var total = row.GetProperty("total").ValueKind == JsonValueKind.Null ? (int?)null
            : (int)RuntimeJson.Integer(row.GetProperty("total"), 1, MaximumSchedulePulses);
        var index = (int)RuntimeJson.Integer(row.GetProperty("index"), 0, total is { } count ? count - 1 : int.MaxValue - 1);
        var next = MigrationTick(row.GetProperty("remainingTicks"));
        var handle = new RuntimeScheduleHandle(destination, provider, RuntimeJson.Text(row, "id"), world.WorldEpoch)
        {
            DispatchedPulses = (int)RuntimeJson.Integer(row.GetProperty("dispatched"), 0, index),
            SkippedPulses = (int)RuntimeJson.Integer(row.GetProperty("skipped"), 0, index),
            NextTick = next
        };
        RuntimeJson.Require((long)handle.DispatchedPulses + handle.SkippedPulses == index,
            "migration-schedule", "The migrated pulse cursor must retain every dispatched or skipped occurrence.");
        Continuation? resume = null;
        var rawResume = row.GetProperty("resume");
        if (rawResume.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Shape(rawResume, "step output handle walk indexed ramp flow play loopValues");
            RuntimeJson.Require(work.Length == 1, "migration-work-plan", "A control continuation owns exactly one work item.");
            var item = work[0];
            var step = MigrationStep(item, rawResume.GetProperty("step"));
            var output = (int)RuntimeJson.Integer(rawResume.GetProperty("output"), 0, item.Entry.Steps[step].Successors.Count - 1);
            var ramp = rawResume.GetProperty("ramp");
            resume = new Continuation(item, step, output, rawResume.GetProperty("handle"),
                RestoreMigrationFlow(rawResume, item), RestoreMigrationPlay(rawResume),
                ramp.ValueKind == JsonValueKind.Null ? null : RampEnvelope.Read(ramp.GetProperty("inputs"), ramp.GetProperty("parameters"), handle.ScheduleId),
                RestoreMigrationWalk(rawResume, item), rawResume.GetProperty("indexed").GetBoolean(),
                RestoreMigrationWalk(rawResume, item, "loopValues") is { Count: > 0 } loops ? loops : null);
        }
        RuntimeJson.Require(total != null || resume != null, "unbounded-schedule", handle.ScheduleId);
        var job = new ScheduledJob(handle, MigrationGeneration(provider), ReadMigrationEvent(row.GetProperty("template")), spec,
            work, RuntimeJson.Text(row, "identity"), checked(next - index * spec.IntervalTicks),
            row.GetProperty("endTicks").ValueKind == JsonValueKind.Null ? null : MigrationTick(row.GetProperty("endTicks")), total)
        {
            Index = index,
            Resume = resume,
            HandleSlot = (int)RuntimeJson.Integer(row.GetProperty("handleSlot"), -1, limits.MaxLiveHandles - 1)
        };
        if (resume == null) job.EndSubject = ReadMigrationEndSubject(provider, job.Template);
        if (row.TryGetProperty("flowGroup", out var flowGroup) && flowGroup.ValueKind == JsonValueKind.Object)
        {
            RuntimeJson.Require(resume != null && work[0].Entry.Steps[resume.Step].Control == GroupControl,
                "migration-group", "A migrated group must be the continuation of a group step.");
            RuntimeJson.Shape(flowGroup, "timeLimit attempts attempt retryDelay suspended queued ending lineFailed endCode");
            var attempts = RuntimeJson.Integer(flowGroup.GetProperty("attempts"), 1);
            job.Group = new FlowGroup
            {
                Job = job,
                Step = work[0].Entry.Steps[resume!.Step],
                TimeLimit = RuntimeJson.Integer(flowGroup.GetProperty("timeLimit")),
                Attempts = attempts,
                Attempt = RuntimeJson.Integer(flowGroup.GetProperty("attempt"), 1, attempts),
                RetryDelay = RuntimeJson.Integer(flowGroup.GetProperty("retryDelay")),
                Suspended = flowGroup.GetProperty("suspended").GetBoolean(),
                Queued = flowGroup.GetProperty("queued").GetBoolean(),
                Ending = flowGroup.GetProperty("ending").GetBoolean(),
                LineFailed = flowGroup.GetProperty("lineFailed").GetBoolean(),
                EndCode = RuntimeJson.Text(flowGroup, "endCode")
            };
        }
        if (row.TryGetProperty("group", out var owner) && owner.ValueKind == JsonValueKind.Object) migrationGroupLinks.Add((job, null, owner));
        // Keep the predecessor's occurrence identity and cursor. Re-keying or resetting either can replay a fact.
        schedules.Add((provider, handle.ScheduleId), job);
    }

    /// <summary>The group memberships a migration image named, linked once every schedule and wait of it exists. A
    /// member whose group the image does not carry is refused: a line that lost its group would join nothing.</summary>
    private readonly List<(ScheduledJob? Job, WaitJob? Wait, JsonElement Owner)> migrationGroupLinks = new();
    private void LinkMigrationGroups()
    {
        foreach (var (job, wait, owner) in migrationGroupLinks)
        {
            RuntimeJson.Shape(owner, "provider id");
            RuntimeJson.Require(schedules.TryGetValue((RuntimeJson.Text(owner, "provider"), RuntimeJson.Text(owner, "id")), out var holder)
                && holder.Group != null, "migration-group", "A migrated group member names a group the image does not carry.");
            if (job != null) JoinGroup(holder!.Group, job);
            if (wait != null) JoinGroup(holder!.Group, wait);
        }
        migrationGroupLinks.Clear();
    }

    private WaitJob RestoreMigrationWait(JsonElement row, RuntimeKernel destination)
    {
        RuntimeJson.Shape(row, "provider work step receivedOutput timeoutOutput target scope message handle handleSlot walk flow resolved resumeOutput play", "group");
        var provider = RuntimeJson.Text(row, "provider");
        var item = RequireMigrationWork(row.GetProperty("work"));
        var step = MigrationStep(item, row.GetProperty("step"));
        var job = new WaitJob
        {
            Handle = new RuntimeModuleHandle(destination, provider, MigrationGeneration(provider)),
            Item = item,
            Step = step,
            ReceivedOutput = (int)RuntimeJson.Integer(row.GetProperty("receivedOutput"), 0, 1),
            TimeoutOutput = (int)RuntimeJson.Integer(row.GetProperty("timeoutOutput"), 0, 1),
            Target = RuntimeJson.Text(row, "target"),
            Scope = RuntimeJson.Text(row, "scope"),
            Message = row.GetProperty("message").GetString(),
            HandleValue = row.GetProperty("handle"),
            HandleSlot = (int)RuntimeJson.Integer(row.GetProperty("handleSlot"), 0, limits.MaxLiveHandles - 1),
            Walk = RestoreMigrationWalk(row, item),
            Flow = RestoreMigrationFlow(row, item),
            Resolved = row.GetProperty("resolved").GetBoolean(),
            ResumeOutput = (int)RuntimeJson.Integer(row.GetProperty("resumeOutput"), -1, 1),
            Play = RestoreMigrationPlay(row)
        };
        RuntimeJson.Require(registry.Tables.IsWaitable(job.Target) && kernelEffects.SlotHoldsWaitHandle(job),
            "migration-wait", "A migrated wait must retain its subscription and live handle.");
        if (row.TryGetProperty("group", out var owner) && owner.ValueKind == JsonValueKind.Object) migrationGroupLinks.Add((null, job, owner));
        if (!job.Resolved)
        {
            if (!kernelVariables.waits.TryGetValue(job.Target, out var list)) kernelVariables.waits.Add(job.Target, list = new List<WaitJob>());
            list.Add(job);
        }
        return job;
    }
}

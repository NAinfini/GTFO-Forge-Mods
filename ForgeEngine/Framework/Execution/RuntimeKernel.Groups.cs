using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// The action group (`forge.control.flow.group`, contract §3.2 I-PLAN): Join, bounded concurrency, the cancel scope,
/// retry and the time limit, as one control of the one dispatcher. A group is a control continuation like a `delay`'s:
/// its own <see cref="ScheduledJob"/> holds the handle a `cancel` ends it with, the region it interrupted and the flow
/// that region had, and every pulse it ever dispatches (the time limit, a retry, its exit) is a pulse of that one job —
/// so there is no second scheduler, no second status table and no second handle pool.
///
/// The members of a group are the suspensions its lines left behind: a `delay`, a pulse series (`repeat`, `ramp`), a
/// `wait_event` and a nested group, each entered while the group's body region was the innermost one — or inside an
/// activation one of those members resumed. A line is finished when its last suspension ended; the group is done when
/// its body walk ended and no member is left. Effects an action applied are not members: ending a group never undoes a
/// committed fact. Everything that changes a group is decided at a safe point (<see cref="FlushGroupChecks"/>), never
/// inside the step that ended a member, so a line that cancels its own group still finishes the steps it is on.
/// </summary>
public sealed partial class RuntimeKernel
{
    internal const string GroupControl = Blocks.ControlFlowGroup.Id;
    /// <summary>The execution outputs in the order the row declares them (`RuntimePlan.ControlShapes`).</summary>
    internal const int GroupBody = 0, GroupDone = 1, GroupFailed = 2, GroupTimedOut = 3, GroupFull = 4;
    private static readonly FlowSnapshot EmptyFlow = new(Array.Empty<StoredValueSnapshot>(),
        Array.Empty<ActionFrameSnapshot>(), Array.Empty<ResultRowSnapshot>());

    /// <summary>One running group. <see cref="Job"/> is its own schedule; the two member lists are the suspensions its
    /// lines hold. <see cref="Suspended"/> says the current attempt's body walk has ended, <see cref="Queued"/> that a
    /// pulse of <see cref="Job"/> sits in the queue, and <see cref="Ending"/> that an exit has been chosen.</summary>
    internal sealed class FlowGroup
    {
        internal ScheduledJob Job = null!;
        internal ResolvedStep Step = null!;
        internal readonly List<ScheduledJob> Jobs = new();
        internal readonly List<WaitJob> Waits = new();
        internal long TimeLimit;
        internal long Attempts = 1;
        internal long Attempt = 1;
        internal long RetryDelay;
        internal bool Suspended;
        internal bool Queued;
        internal bool Ending;
        internal bool LineFailed;
        internal string EndCode = "group-done";
        internal int Members => Jobs.Count + Waits.Count;
    }

    /// <summary>A suspension may only join a group that is still running: one that was cancelled, timed out, failed
    /// or chose its exit takes no new lines, and the step that tried is refused by name.</summary>
    private static void RequireOpenGroup(FlowGroup? group, string nodeId)
        => RuntimeJson.Require(group == null || group.Job.Handle.Active && !group.Ending, "group-cancelled", nodeId);

    internal static void JoinGroup(FlowGroup? group, ScheduledJob job)
    {
        if (group == null) return;
        job.Owner = group;
        group.Jobs.Add(job);
    }

    internal static void JoinGroup(FlowGroup? group, WaitJob wait)
    {
        if (group == null) return;
        wait.Owner = group;
        group.Waits.Add(wait);
    }

    /// <summary>One optional count input, refused by name when the wired value is outside the card's domain. The
    /// domain is the integer's own (a safe integer at or above <paramref name="minimum"/>): nothing is narrowed or
    /// clamped, so a value the author chose is either used as written or refused.</summary>
    private static long? GroupCount(ResolvedStep step, StepInputs inputs, ControlPort port, long minimum, string nodeId)
    {
        if (!TryControlInput(step, inputs, port, out var raw) || raw.ValueKind == JsonValueKind.Null) return null;
        if (!(raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out var value)
            && value >= minimum && value <= RuntimeJson.MaxSafeInteger)) throw new RuntimeContractException("group-input", nodeId + "." + ControlPortNames[(int)port]);
        return raw.GetInt64();
    }

    /// <summary>
    /// Enters a group: refuses a new one where this card already runs `max_active` groups (the `full` exit), and
    /// otherwise opens the group's schedule — not queued unless it has a time limit — publishes its handle and its
    /// attempt number, and walks `body` with the group as the innermost region.
    /// </summary>
    private string? EnterGroup(Work item, int stepIndex, ResolvedStep step, Pending pending, StepInputs inputs, out int? cursor)
    {
        cursor = null;
        var limit = GroupCount(step, inputs, ControlPort.MaxActive, 1, step.NodeId);
        var timeLimit = GroupCount(step, inputs, ControlPort.TimeLimit, 1, step.NodeId) ?? 0;
        var attempts = GroupCount(step, inputs, ControlPort.Attempts, 1, step.NodeId) ?? 1;
        // Every attempt walks the body again, so the attempts are rounds under the same ceiling a counted loop has.
        if (attempts > Limits.MaxControlIterations) throw new RuntimeContractException("group-input", step.NodeId + ".attempts");
        var retryDelay = GroupCount(step, inputs, ControlPort.RetryDelay, 0, step.NodeId) ?? 0;
        if (!(timeLimit <= RuntimeJson.MaxSafeInteger - CurrentTick
            && retryDelay <= RuntimeJson.MaxSafeInteger - CurrentTick)) throw new RuntimeContractException("group-input", step.NodeId + ".time_limit");
        if (limit is { } most && kernelFlow.LiveGroupsOf(step) >= most)
        {
            cursor = Successor(step, GroupFull);
            return null;
        }
        var owner = kernelFlow.CurrentGroup();
        RequireOpenGroup(owner, step.NodeId);
        var plan = item.Plan;
        RequirePlanSuspensionRoom(plan, step.NodeId);
        var scheduleId = plan.Plan.Id + ".control." + stepIndex + "." + (++kernelFlow.scheduleSequence);
        var handleValue = kernelEffects.CreateHandle(pending.Provider, "timer", "encounter", out var handleSlot, scheduleId);
        var work = new Work(plan, item.Entry, item.Subjects);
        var handle = new RuntimeScheduleHandle(this, pending.Provider, scheduleId, WorldEpoch);
        // The interval is one tick so a pulse's tick is its anchor plus its index; the group places every pulse
        // itself (ArmGroup) and the series has no count, because only an exit, a cancel or a lifecycle end stops it.
        var job = new ScheduledJob(handle, pending.Generation, pending.Event,
            new PulseSchedule(1, FirstPulse.Immediate, MissedPulsePolicy.CatchUp, null), new[] { work },
            Fingerprint(new { provider = pending.Provider, world = WorldEpoch, plan = plan.Plan.Id, entry = item.Entry.NodeId, step = stepIndex, output = "group", instance = scheduleId }),
            CurrentTick, null, null)
        {
            Resume = new Continuation(work, stepIndex, GroupTimedOut, handleValue, EmptyFlow, kernelFlow.PlayOf(pending), Walk: Array.Empty<ControlFrame>()),
            HandleSlot = handleSlot
        };
        var group = new FlowGroup
        {
            Job = job,
            Step = step,
            TimeLimit = timeLimit,
            Attempts = attempts,
            RetryDelay = retryDelay
        };
        job.Group = group;
        kernelFlow.schedules.Add((handle.ProviderId, handle.ScheduleId), job);
        JoinGroup(owner, job);
        if (timeLimit > 0) kernelFlow.ArmGroup(group, CurrentTick + timeLimit, GroupTimedOut);
        executor.WriteGroupFrame(stepIndex, group);
        kernelFlow.controlWalk.Add(new ControlFrame
        {
            Descriptor = step,
            Step = stepIndex,
            IsGroup = true,
            Group = group,
            Successors = step.ControlSuccessors
        });
        cursor = Successor(step, GroupBody);
        return null;
    }

    /// <summary>
    /// The body region of a group frame ended in this activation. Where every line already finished the group is done
    /// here and `done` continues the same walk; otherwise the group keeps the region it interrupted and the flow it had,
    /// and `done` is entered by its own pulse once the last member ends. Null ends the walk, <see cref="GroupWalkOn"/>
    /// goes on to the enclosing region (an unwired `done`), and anything else is the step `done` starts from.
    /// </summary>
    internal const int GroupWalkOn = -2;

    /// <summary>Decides every group something happened to since the last safe point: a member ended, a body walk
    /// ended, a line failed, the group itself ended. Called only where no queue entry is half taken.</summary>
    private void FlushGroupChecks()
    {
        while (kernelFlow.groupChecks.Count > 0)
        {
            var group = kernelFlow.groupChecks[^1];
            kernelFlow.groupChecks.RemoveAt(kernelFlow.groupChecks.Count - 1);
            CheckGroup(group);
        }
    }

    private void CheckGroup(FlowGroup group)
    {
        if (!group.Job.Handle.Active)
        {
            // Cancelled, ended by its source's lifecycle, or finished: whatever is left of its lines ends with it.
            CancelGroupMembers(group, "group-cancelled");
            return;
        }
        if (group.Ending) return;
        if (group.LineFailed) { AttemptFailed(group, timedOut: false); return; }
        if (group.Suspended && group.Members == 0) kernelFlow.Finish(group, GroupDone, "group-done");
    }

    /// <summary>Ends every member: a nested group's own members follow through its check, and a member that
    /// dispatches nothing more leaves the list from its own end.</summary>
    private void CancelGroupMembers(FlowGroup group, string code)
    {
        if (group.Members == 0) return;
        foreach (var job in group.Jobs.ToArray()) kernelFlow.EndSchedule(job, "cancelled", code);
        foreach (var wait in group.Waits.ToArray()) RetireWait(wait);
        kernelFlow.PruneQueue();
    }

    /// <summary>One attempt failed or ran out of time: its remaining lines stop, and the body runs again after the
    /// retry delay while attempts are left; otherwise the group leaves by `failed` or `timed_out`.</summary>
    private void AttemptFailed(FlowGroup group, bool timedOut)
    {
        CancelGroupMembers(group, timedOut ? "group-timed-out" : "group-failed");
        group.Suspended = false; group.LineFailed = false;
        if (group.Attempt < group.Attempts)
        {
            group.Attempt++;
            kernelFlow.ArmGroup(group, CurrentTick + group.RetryDelay, GroupBody);
            return;
        }
        if (timedOut) kernelFlow.Finish(group, GroupTimedOut, "group-timed-out");
        else kernelFlow.Finish(group, GroupFailed, "group-failed");
    }

    /// <summary>
    /// A pulse of a group's own schedule. A retry walks the body again with a fresh time limit; the time limit fails
    /// the attempt and walks nothing; an exit restores the region the group interrupted and walks the chosen output.
    /// An unwired `failed` or `timed_out` is the enclosing line's failure (<see cref="groupFailurePropagates"/>).
    /// </summary>
    private int? ResumeGroup(ResolvedEntry entry, FlowGroup group)
    {
        kernelFlow.BeginActivation();
        var job = group.Job;
        var resume = job.Resume!;
        var step = entry.Steps[resume.Step];
        if (resume.Output == GroupTimedOut && !group.Ending)
        {
            AttemptFailed(group, timedOut: true);
            return null;
        }
        kernelFlow.RestoreFlow(entry, resume.Flow);
        kernelFlow.RestoreWalk(resume.Walk);
        executor.WriteGroupFrame(resume.Step, group);
        if (resume.Output == GroupBody)
        {
            if (group.TimeLimit > 0) kernelFlow.ArmGroup(group, CurrentTick + group.TimeLimit, GroupTimedOut);
            kernelFlow.controlWalk.Add(new ControlFrame
            {
                Descriptor = step,
                Step = resume.Step,
                IsGroup = true,
                Group = group,
                Successors = step.ControlSuccessors
            });
            return Successor(step, GroupBody);
        }
        var next = Successor(step, resume.Output);
        if (next == null && resume.Output != GroupDone) kernelFlow.groupFailurePropagates = true;
        return next;
    }
}

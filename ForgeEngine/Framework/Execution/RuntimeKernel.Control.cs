using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// The kernel's one control dispatcher. Every control step the plan declares — branch, sequence, delay, repeat
/// (counted or with an interval), for_each (over candidates or positions), enum_switch, cancel, restart, present — is
/// walked here, in the same place the plan's successor table is followed, so there is
/// no second router and no control-specific execution path. A control's execution outputs are ordinary forward
/// successors: `next` continues, `body` names the step the next activation starts from, and the kernel
/// holds the continuation (plan, control step, output index, frame snapshot, handle, and the region the step
/// suspended) itself. Nothing rewinds the plan and no region or `iterations` field exists in it.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>One control region being walked, or one loop being run. The successor table and the step
    /// descriptor are copied in because resuming must not look either up again; `Round`/`Rounds` only mean
    /// something on a loop frame.</summary>
    internal struct ControlFrame
    {
        internal ResolvedStep Descriptor;
        internal int Step;
        internal int Output;
        internal int Round;
        internal int Rounds;
        internal bool Loop;
        internal bool ForEach;
        /// <summary>Whether this loop's list is a table of points rather than a candidate set, which is what makes its
        /// rounds publish `position` instead of `item`.</summary>
        internal bool Positions;
        internal int[] Successors;
        internal JsonElement Items;
        internal int RowStringsStart;
        /// <summary>Set on an action group's frame: the region its `body` opened. <see cref="Group"/> is the group,
        /// and <see cref="Detached"/> marks a frame a resumed activation put back rather than one this activation
        /// pushed — the end of a detached group region ends the line, because what follows the group is the group's
        /// own exit to walk (see RuntimeKernel.Groups).</summary>
        internal bool IsGroup;
        internal bool Detached;
        internal FlowGroup? Group;
        /// <summary>Set on a loop an interval `repeat`/`ramp` pulse runs inside (<see cref="Continuation.LoopValues"/>): the
        /// pulse is its own region, so it reads the loop's round but never advances it — the walk ends when it reaches the
        /// frame. Being on the walk is what lets every suspension inside the pulse carry the round on, the way it carries
        /// any other enclosing frame.</summary>
        internal bool Held;
    }

    /// <summary>What a schedule resumes: the work item it belongs to, the control step it re-enters, the output
    /// whose successor is the first step of the new activation, and the handle value that step's frame carries.
    /// <see cref="Ramp"/> is set on the one continuation whose frame is not a fixed timer frame: a ramp republishes
    /// its own value on every pulse, so the activation's frame is computed from the pulse number instead.
    /// <see cref="Walk"/> is the enclosing control region the suspended step sat in, outermost first: a `delay` and
    /// a `g-wait` leave by one exit that continues the flow they were part of, so the activation their continuation
    /// enters has to go on walking that region — a sequence's next exit, a loop's next round — instead of the region
    /// having been left behind when the step suspended. <see cref="Indexed"/> is set on a `repeat` pulse series, whose
    /// every pulse publishes the round it is (`index`) beside the timer. It is empty for a `repeat`/`ramp` pulse, whose exit is
    /// entered by the schedule itself rather than by the step's own entry. The frames are copied structs over
    /// immutable successor tables and item lists, which is what makes the capture safe once the live walk is
    /// dropped at the end of the activation.</summary>
    internal sealed record Continuation(Work Work, int Step, int Output, JsonElement HandleValue,
        FlowSnapshot Flow, PlayOrigin Play, RampEnvelope? Ramp = null,
        IReadOnlyList<ControlFrame>? Walk = null, bool Indexed = false, IReadOnlyList<ControlFrame>? LoopValues = null);
    internal sealed record StoredValueSnapshot(int Step, JsonElement[] Values, ResourceRef?[] Resolved);
    internal sealed record ActionFrameSnapshot(int Step, JsonElement Frame);
    /// <summary>One action's result rows as they stood when the flow suspended: the typed slots of the row region
    /// (<see cref="Count"/> rows of the descriptor's stride) and, for each string slot, the text it named, because
    /// the string area is rewound with the activation and an offset would name a stranger's bytes.</summary>
    internal sealed record ResultRowSnapshot(int Step, int Count, bool Set, FrameValue[] Slots, string?[] Strings);
    /// <summary>What a suspended flow carries across the suspension: the values it stored, the value frames its
    /// actions published and the typed result rows they wrote. A `delay`, a `g-wait` and a pulse series all suspend
    /// the same way, so they capture and restore the same three things through the same two methods.</summary>
    internal sealed record FlowSnapshot(StoredValueSnapshot[] StoredValues, ActionFrameSnapshot[] ActionFrames,
        ResultRowSnapshot[] Rows);
    internal static JsonElement TimerFrame(JsonElement handle) => RuntimeJson.From(new { timer = handle });

    /// <summary>The step one output of a control step starts from, or null when the plan wired none.</summary>
    internal static int? Successor(ResolvedStep step, int output)
        => output >= 0 && output < step.Successors.Count ? step.Successors[output] : null;

    /// <summary>
    /// Enters one control step. A return value other than null is the code the dispatch is rejected with; a null
    /// return leaves the walk in <paramref name="cursor"/>, which is null when this control suspended the event
    /// (a `delay` waits for its timer) and the path is finished for now.
    /// </summary>
    private string? EnterControl(Work item, int stepIndex, ResolvedStep step, Pending pending,
        StepInputs inputs, JsonElement parameters, out int? cursor)
    {
        cursor = null;
        var plan = item.Plan;
        var successors = step.ControlSuccessors;
        switch (step.Control)
        {
            case Blocks.ControlFlowBranch.Id:
                cursor = Successor(step, Flag(step, inputs, ControlPort.Condition) ? 0 : 1);
                return null;
            case Blocks.ControlFlowSequence.Id:
                // Depth first, in output order: each exit is walked to its end before the next one starts.
                kernelFlow.controlWalk.Add(new ControlFrame { Descriptor = step, Step = stepIndex, Output = 0, Successors = successors });
                cursor = Successor(step, 0);
                return null;
            case Blocks.ControlFlowDelay.Id:
                {
                    // The timer is written on entry; `next` is entered when it fires, not now. The region the step
                    // sits in travels with the continuation: the delay's one exit continues the flow it interrupted,
                    // so the sequence's next exit and the loop's next round run after the timer, not before it.
                    var failure = ScheduleContinuation(item, stepIndex, step, pending, resumeOutput: 0,
                        new PulseSchedule(Ticks(step, inputs, ControlPort.Duration), FirstPulse.AfterInterval, MissedPulsePolicy.SkipMissed, 1),
                        out _, walk: kernelFlow.EnclosingWalk());
                    if (failure == null) kernelFlow.walkSuspended = true;
                    return failure;
                }
            case Blocks.ControlFlowRamp.Id:
                {
                    // The envelope is the pulse source and there is no separate `pulse` exit: every pulse re-enters
                    // `next`, so the region after a ramp runs once per tick of the envelope and reads that tick's
                    // value. The walk therefore suspends on entry — the first pulse, which is immediate, is what
                    // enters `next` — and the handle is the schedule's own, so a later `cancel` on it ends the
                    // envelope. See RampEnvelope for the value each pulse publishes. Every pulse enters the ramp's own
                    // per-tick region, so no enclosing region travels with the continuation and the region the step
                    // sits in is not held open by it: an envelope is not a join point (contract §3.2 I-PLAN leaves
                    // joins to the second step), and this is the one suspension that carries no walk.
                    var ramp = RampEnvelope.Read(inputs.Json, parameters, step.NodeId);
                    return ScheduleContinuation(item, stepIndex, step, pending, resumeOutput: 0,
                        new PulseSchedule(1, FirstPulse.Immediate, MissedPulsePolicy.SkipMissed, ramp.TotalPulses), out _, ramp);
                }
            case Blocks.ControlFlowRepeat.Id when (step.Loop & LoopShape.Scheduled) != 0:
                {
                    // A repeat with an interval: the schedule is the pulse source. `next` runs now and every pulse
                    // re-enters the `body` successor as its own activation, publishing its round beside the timer. An
                    // omitted `count` is an unbounded pulse series: it ends only where its handle is cancelled, its scope
                    // ends or its world ends, so the job carries no pulse limit at all (ruling 160.2). A count the author
                    // did write is still bounded by the control-iteration limit, the same ceiling the counted form uses.
                    // No enclosing region travels with it: the pulse is the repeat's own region, entered by its own
                    // schedule, and the step's `next` — which is the flow's continuation — already ran on entry. Whether
                    // the first pulse waits was resolved at load (LoopShape.AfterInterval).
                    int? count = TryControlInput(step, inputs, ControlPort.Count, out var raw) ? (int)RuntimeJson.Integer(raw, 0) : null;
                    RuntimeJson.Require(count == null || (count >= 1 && count <= Limits.MaxControlIterations), RuntimeAbiCodes.IterationBudget, step.NodeId);
                    var first = (step.Loop & LoopShape.AfterInterval) != 0 ? FirstPulse.AfterInterval : FirstPulse.Immediate;
                    var failure = ScheduleContinuation(item, stepIndex, step, pending, resumeOutput: 1,
                        new PulseSchedule(Ticks(step, inputs, ControlPort.Interval), first, MissedPulsePolicy.SkipMissed, count), out _, indexed: true);
                    if (failure != null) return failure;
                    cursor = Successor(step, 0);
                    return null;
                }
            case Blocks.ControlFlowRepeat.Id:
            case Blocks.ControlFlowForEach.Id:
                {
                    // One loop, three sources: a count, the candidates a selector picked, or the points an upstream step
                    // published. Which list a `for_each` walks was fixed at load (LoopShape.Positions), so a round
                    // publishes `item` for a candidate and `position` for a point, with `index` beside either.
                    var forEach = step.Control == Blocks.ControlFlowForEach.Id;
                    var positions = (step.Loop & LoopShape.Positions) != 0;
                    var source = positions ? ControlPort.Positions : ControlPort.Candidates;
                    var rounds = forEach ? CandidateCount(inputs, step, source, (step.Loop & LoopShape.Cycle) != 0) : Count(inputs, ControlPort.Count, step);
                    if (rounds == 0) { cursor = Successor(step, 0); return null; }
                    var frame = new ControlFrame
                    {
                        Descriptor = step,
                        Step = stepIndex,
                        Output = 1,
                        Round = 0,
                        Rounds = rounds,
                        Loop = true,
                        ForEach = forEach,
                        Positions = positions,
                        Successors = successors,
                        Items = forEach && TryControlInput(step, inputs, source, out var items) ? items : default,
                        RowStringsStart = executor.resultArena.Strings.Count
                    };
                    kernelFlow.controlWalk.Add(frame);
                    kernelFlow.BeginLoopRound(frame, item.Entry);
                    executor.WriteLoopFrame(frame);
                    cursor = Successor(step, 1);
                    return null;
                }
            case Blocks.ControlFlowEnumSwitch.Id:
                {
                    // The member the resolved set lists at index i is the value `case_(i+1)` stands for — the set's own
                    // declaration order is its compiled value basis, so no second table is consulted. A value that is
                    // absent (the port is nullable), unknown to the set, or past the authored case count leaves through
                    // `otherwise`, which is the last execution output; exactly one exit is entered, never two, and a
                    // value the set does not know is not an error the event is rejected for.
                    var otherwise = successors.Length - 1;
                    RuntimeJson.Require(otherwise >= 2, RuntimeAbiCodes.ControlShape, step.NodeId);
                    var schema = RuntimeJson.Text(RuntimeJson.Rows(step.Contract, "inputs").First(port => RuntimeJson.Text(port, "id") == "value"), "schema");
                    var value = TryControlInput(step, inputs, ControlPort.Value, out var raw) && raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
                    var exit = otherwise;
                    if (value != null)
                    {
                        var members = RuntimeEnumSets.Members(schema);
                        for (var index = 0; index < members.Count && index < otherwise; index++)
                            if (string.Equals(members[index], value, StringComparison.Ordinal)) { exit = index; break; }
                    }
                    cursor = Successor(step, exit);
                    return null;
                }
            case GroupControl:
                return EnterGroup(item, stepIndex, step, pending, inputs, out cursor);
            case VariableContracts.LogRecordCapability:
                return EnterLogRecord(step, out cursor);
            case Blocks.ControlFlowCancel.Id:
                {
                    // The count is what actually happened: zero until a live schedule is really ended, so a handle whose
                    // schedule already died reports 0 and fails by name instead of claiming a cancellation.
                    var input = step.Inputs.First(port => port.Name == "task");
                    executor.stepFrames[stepIndex] = RuntimeJson.From(new { cancelled = 0 });
                    var cancelled = TryControlInput(step, inputs, ControlPort.Task, out var handle) ? CancelHandle(handle, input.Port, step.NodeId) : 0;
                    executor.stepFrames[stepIndex] = RuntimeJson.From(new { cancelled });
                    cursor = Successor(step, 0);
                    return null;
                }
            case Blocks.ControlFlowRestart.Id:
                {
                    // The rolling window: the same live timer is re-armed from now, so the handle the delay/repeat
                    // step published keeps working and the author needs no place to keep it. The answer says whether a
                    // live schedule was really re-armed; a handle whose schedule already ended fails the slot check.
                    var input = step.Inputs.First(port => port.Name == "task");
                    executor.stepFrames[stepIndex] = RuntimeJson.From(new { restarted = false });
                    var restarted = TryControlInput(step, inputs, ControlPort.Task, out var handle) && kernelFlow.RestartHandle(handle, input.Port, step.NodeId);
                    executor.stepFrames[stepIndex] = RuntimeJson.From(new { restarted });
                    cursor = Successor(step, 0);
                    return null;
                }
            default:
                // The variable, object, message and wait rows are the second half of the kernel's own control
                // vocabulary: they route the same walk from the same place, which is why they are dispatched here
                // rather than through a handler.
                return IsVariableControl(step.Control)
                    ? EnterVariableControl(item, stepIndex, step, pending, inputs, parameters, out cursor)
                    : RuntimeAbiCodes.ControlUnsupported;
        }
    }

    /// <summary>The rounds a `for_each` may run: one per entry of the list it walks, whichever list that is, bounded
    /// by its own budget. A list larger than the budget is refused rather than truncated, because the plan asked for
    /// all of it. A list shorter than the budget stops at its end, unless the walk cycles (`shortfall=repeat`): then it
    /// fills the budget from the top of the list, and an empty list still runs no round.</summary>
    private int CandidateCount(StepInputs inputs, ResolvedStep step, ControlPort port, bool cycle)
    {
        var listed = TryControlInput(step, inputs, port, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;
        var budget = TryControlInput(step, inputs, ControlPort.Budget, out var limit) ? (int)RuntimeJson.Integer(limit, 0) : listed;
        RuntimeJson.Require(budget <= Limits.MaxControlIterations, RuntimeAbiCodes.IterationBudget, step.NodeId);
        RuntimeJson.Require(listed <= budget, RuntimeAbiCodes.IterationBudget, step.NodeId);
        return cycle && listed > 0 ? budget : listed;
    }

    /// <summary>The inputs the kernel's own control dispatch reads. Each control step resolves the cell of every one it
    /// has at plan load (<see cref="ResolvedStep.ControlInputs"/>), so dispatch reads its reused frame by position and
    /// no input document is written; JSON is left to the boundaries that persist or send a frame.</summary>
    internal enum ControlPort
    {
        Condition, Duration, Count, Interval, Candidates, Positions, Budget, Value, Task, Wave,
        MaxActive, TimeLimit, Attempts, RetryDelay
    }

    private static readonly string[] ControlPortNames =
    {
        "condition", "duration", "count", "interval", "candidates", "positions", "budget", "value", "task", "wave",
        "max_active", "time_limit", "attempts", "retry_delay"
    };

    internal static int[] ControlInputSlots(ResolvedStep step)
    {
        var slots = new int[ControlPortNames.Length];
        for (var port = 0; port < slots.Length; port++)
        {
            var name = Array.IndexOf(step.HandlerInputs, ControlPortNames[port]);
            var physical = name < 0 || name >= step.HandlerInputOffsets.Length ? -1 : step.HandlerInputOffsets[name];
            slots[port] = physical >= 0 && physical < step.HandlerInputDenseMap.Length ? step.HandlerInputDenseMap[physical] : -1;
        }
        return slots;
    }

    /// <summary>One control input as the frame carries it; false where the step has no such input or none arrived.</summary>
    private static bool TryControlInput(ResolvedStep step, StepInputs inputs, ControlPort port, out JsonElement value)
    {
        value = step.ControlInputs.Length == 0 ? default : inputs.InputAt(step.ControlInputs[(int)port]);
        return value.ValueKind != JsonValueKind.Undefined;
    }

    private int Count(StepInputs inputs, ControlPort port, ResolvedStep step)
    {
        var count = TryControlInput(step, inputs, port, out var value) ? (int)RuntimeJson.Integer(value, 0) : 0;
        RuntimeJson.Require(count <= Limits.MaxControlIterations, RuntimeAbiCodes.IterationBudget, step.NodeId);
        return count;
    }

    private static long Ticks(ResolvedStep step, StepInputs inputs, ControlPort port)
        => TryControlInput(step, inputs, port, out var value) ? RuntimeJson.Integer(value, 1) : 1;
    private static bool Flag(ResolvedStep step, StepInputs inputs, ControlPort port)
        => TryControlInput(step, inputs, port, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Registers the continuation a `delay`, a `repeat` with an interval or a `ramp` control leaves behind and writes
    /// its timer handle into the control step's frame. The pulse re-enters the plan's own entry, so the schedule is the
    /// kernel's, not the provider's: it dispatches no new event and triggers no new subscription. `resumeOutput` is the
    /// execution output the next activation starts from — `next` for a delay and for a ramp, `body` for a repeat —
    /// and <paramref name="ramp"/> is the one continuation whose frame is not a bare timer: a ramp republishes the
    /// value its envelope holds on every pulse, so the frame it writes carries both. <paramref name="walk"/> is the
    /// region the resumed activation goes on walking after the step's own exit; it is empty for an exit the
    /// schedule itself enters, and it is never written into the plan. <paramref name="indexed"/> marks a `repeat`
    /// series, whose every pulse publishes its round beside the timer.
    /// </summary>
    private string? ScheduleContinuation(Work item, int stepIndex, ResolvedStep step, Pending pending,
        int resumeOutput, PulseSchedule spec, out int handleSlot, RampEnvelope? ramp = null,
        IReadOnlyList<ControlFrame>? walk = null, bool indexed = false)
    {
        handleSlot = -1;
        try
        {
            var plan = item.Plan;
            // A suspension inside an action group's body is one of that group's lines; a group that already ended
            // takes no new line.
            var owner = kernelFlow.CurrentGroup();
            RequireOpenGroup(owner, step.NodeId);
            RequirePlanSuspensionRoom(plan, step.NodeId);
            // A delay that ends its exit inside a region (a sequence exit, a loop round, a group line) still holds
            // that region: the resumed walk advances the region where nothing follows the delay. Only a
            // continuation that would resume into nothing at all is malformed.
            RuntimeJson.Require(Successor(step, resumeOutput) is not null || walk is { Count: > 0 },
                RuntimeAbiCodes.ControlShape, step.NodeId);
            // One entry of the step is one continuation, so the identity counts the entries: a control step may be
            // entered again while an earlier entry's timer is still live — a `delay` inside a `repeat` pulse, a
            // delay a loop round runs a second time — and each entry holds its own handle, its own pulse series and
            // its own place in the replay ledger. A step-index-only identity would make the second entry collide
            // with the first: the schedule table would refuse the second job and the ledger would refuse its pulses.
            var scheduleId = plan.Plan.Id + ".control." + stepIndex + "." + (++kernelFlow.scheduleSequence);
            // A `delay` resumes the play it interrupted. An interval `repeat` or a `ramp` starts a series of its own: every
            // pulse runs under the same origin, and a second entry of the step — the same ability triggered again while
            // the first series still runs — is a series, and so a set of plays, of its own.
            var play = ramp != null || indexed ? kernelFlow.PlayOf(pending).Series(kernelFlow.scheduleSequence) : kernelFlow.PlayOf(pending);
            // The handle names the source provider, which is what the schedule registry is keyed by and what a
            // scope cancellation matches on: `cancel` resolves the timer through the same pair. A ramp's handle is
            // the kind and lifetime its own row declares — `effect`/`entity_life` — and it names the same schedule,
            // so ending it through the kernel's one cancel path ends the envelope; a `delay`/`repeat` keeps the
            // `timer`/`encounter` pair the flow rows that consume them declare.
            var handleValue = ramp == null
                ? kernelEffects.CreateHandle(pending.Provider, "timer", "encounter", out handleSlot, scheduleId)
                : kernelEffects.CreateHandle(pending.Provider, "effect", "entity_life", out handleSlot, scheduleId);
            executor.stepFrames[stepIndex] = ramp == null
                ? TimerFrame(handleValue)
                : RuntimeJson.From(new { value = ramp.ValueAt(0), handle = handleValue });
            // The resumed activation is the same plan on the same dispatch, so it keeps the subjects that dispatch's
            // mount accepted rather than matching again. The clock the schedule is anchored on is the tick this step
            // is running in, not the tick the event it is answering was published for: a dispatch deferred past its
            // own tick, and a continuation re-entered under the event that started the flow, both run the step now,
            // so "N ticks from here" is counted from here.
            var work = new Work(plan, item.Entry, item.Subjects);
            var first = CurrentTick + (spec.FirstPulse == FirstPulse.Immediate ? 0 : spec.IntervalTicks);
            var handle = new RuntimeScheduleHandle(this, pending.Provider, scheduleId, WorldEpoch);
            var job = new ScheduledJob(handle, pending.Generation, pending.Event, spec, new[] { work },
                Fingerprint(new { provider = pending.Provider, world = WorldEpoch, plan = plan.Plan.Id, entry = item.Entry.NodeId, step = stepIndex, output = resumeOutput, instance = scheduleId }),
                first, null, spec.MaxPulses)
            {
                Resume = new Continuation(work, stepIndex, resumeOutput, handleValue,
                    kernelFlow.CaptureFlow(item.Entry), play, ramp, walk, indexed, walk == null ? kernelFlow.EnclosingLoops() : null),
                HandleSlot = handleSlot
            };
            kernelFlow.schedules.Add((handle.ProviderId, handle.ScheduleId), job);
            JoinGroup(owner, job);
            eventQueue.QueuePulse(job);
            return null;
        }
        catch (RuntimeContractException error) { return error.Code; }
    }

    /// <summary>
    /// Evaluates one on-demand step — `pure` or `query` — and memoizes it for the current activation. A `query`
    /// step is handed the budgeted world read and its refusal is reported as `query-budget` instead of being
    /// answered with a partial frame; a `pure` step is handed a session that refuses every read.
    ///
    /// The table this memoizes into is the activation's one value table, so it is also where a `control` step writes
    /// its own value outputs and where an `action` step's value outputs go when it ran: those are read back out of the
    /// table, and a step this activation produced no value for — a `pure` step of an earlier activation, an action the
    /// path never ran — is refused by name instead of answered with a stranger's frame. An action's result row
    /// is not in that table: a row is not a value, and it is read through the descriptor from the activation's typed
    /// row region (<c>StepRows</c>).
    /// </summary>
    private JsonElement EvaluateStep(Work item, int stepIndex, Pending pending, out EvaluationOutputs? typed)
    {
        var step = item.Entry.Steps[stepIndex];
        // A `pure`/`query` step answers in its own typed cells; the slot table says whether they are this
        // activation's. A frame restored as JSON (an owner resumption) or a control's or action's frame is the slot's.
        typed = step.Evaluated;
        if (typed != null && step.EvaluatedActivation == kernelFlow.valueActivation && executor.validatedSlots.ContainsKey(stepIndex)) return default;
        typed = null;
        if (executor.stepFrames.TryGetValue(stepIndex, out var cached)) return cached;
        // A frame a control or an action published is always in the slot table by the time a later step reads it,
        // so a slot this table does not hold names a step no activation produced a value for; the refusal is the
        // slot's own, not a claim about the source's kind.
        RuntimeJson.Require(step.NodeKind is "pure" or "query", "from-step-slot", step.NodeId);
        var stepPlan = new RuntimeLogPlan
        {
            PlanId = item.Plan.Plan.Id,
            ResourceId = item.Plan.Plan.ResourceId,
            ResourceRevision = item.Plan.Plan.ResourceRevision
        };
        var traceOrigin = new KernelLog.StepOrigin(pending.Event, in stepPlan, item.Entry.NodeId);
        var command = StepCommandOf(pending, step.CommandSuffix);
        log.LogStepStarted(in traceOrigin, step.LogGate, step.ProviderId, step.NodeId, step.BindingId, ref command, step.NodeKind);
        JsonElement? traceInputs = null, traceParameters = step.Parameters;
        var deepTraceStarted = false;
        EvaluationOutputs outputs;
        try
        {
            // The same input read the dispatch walk makes, for the same plan: literals in their loaded form, frames
            // and rows of this activation, and the dispatch's own payload, each checked against its typed port.
            var inputs = ResolveStepInputs(item, step, pending, dispatched: false);
            traceInputs = TraceFrame(inputs);
            traceParameters = behaviorTraceObservers.Count == 0 ? null : inputs.Parameters;
            TraceStep("before", in traceOrigin, step.ProviderId, step.NodeId, step.BindingId, ref command,
                step.NodeKind, traceInputs, traceParameters);
            deepTraceStarted = true;
            var evaluator = step.Evaluator ?? throw new RuntimeContractException("missing-evaluator", step.NodeId);
            var session = step.NodeKind == "query" ? BeginQuery(step.NodeId) : NoQuery(step.NodeId);
            // The event being dispatched is the whole context a step is evaluated in: its actor roles come from the
            // event's own payload — `self` from the entities this plan's mount accepted, carried on the work item —
            // and the world's faction relations are the ones the current world published. A role the contract does
            // not allow to be absent refuses the step by name before the handler ever runs, and `self` is refused
            // as `actor-ambiguous` rather than `actor-missing` when the mount accepted more than one entity.
            var context = step.Context = EvaluationContext.For(step.Context, step.NodeId, session, this, pending.Event,
                item.Subjects, relations, CurrentTick, entityInstance, ReportedTickRate, inputs);
            var required = step.RequiredActorRoles;
            for (var role = 0; role < required.Count; role++)
                RuntimeJson.Require(context.Actors.Get(required[role]) != null, "actor-missing", required[role]);
            outputs = step.Evaluated!;
            outputs.Begin();
            evaluator(context, outputs);
            // A refused read is a rejected step, never a frame that silently holds fewer candidates.
            RuntimeJson.Require(queryRefusal == null, step.NodeKind == "query" ? queryRefusal! : RuntimeAbiCodes.PureWorldPort, step.NodeId);
            var ports = step.OutputViews!;
            for (var i = 0; i < ports.Length; i++)
            {
                var cell = outputs.Cells[i];
                if (!cell.Carried) throw new RuntimeContractException("missing-field", ports[i].Id);
                outputs.Cells[i] = executor.CheckOutput(cell, ports[i], step, i);
            }
        }
        catch (RuntimeContractException ex) when (queryRefusal != null)
        {
            // The first refusal the session recorded outranks whatever the evaluator reported, so an exhausted
            // budget reads as the one `query-budget` code whether the evaluator threw or returned a short frame.
            var code = step.NodeKind == "query" ? queryRefusal! : RuntimeAbiCodes.PureWorldPort;
            if (!deepTraceStarted)
                TraceStep("before", in traceOrigin, step.ProviderId, step.NodeId, step.BindingId, ref command,
                    step.NodeKind, traceInputs, traceParameters);
            log.LogStepFinished(in traceOrigin, step.LogGate, step.ProviderId, step.NodeId, step.BindingId, ref command,
                step.NodeKind, CommandStatuses.Rejected, null, code);
            TraceStep("after", in traceOrigin, step.ProviderId, step.NodeId, step.BindingId, ref command,
                step.NodeKind, traceInputs, traceParameters, null,
                new RuntimeLogResult { Status = CommandStatuses.Rejected, Reason = code });
            throw new RuntimeContractException(code, ex.Code + ": " + ex.Message);
        }
        // A refusal the evaluator raised keeps its own code: the more specific the code, the closer it points the
        // author at the node to repair. Only a failure with no code at all is encoded here.
        catch (RuntimeContractException ex)
        {
            if (!deepTraceStarted)
                TraceStep("before", in traceOrigin, step.ProviderId, step.NodeId, step.BindingId, ref command,
                    step.NodeKind, traceInputs, traceParameters);
            log.LogStepFinished(in traceOrigin, step.LogGate, step.ProviderId, step.NodeId, step.BindingId, ref command,
                step.NodeKind, CommandStatuses.Rejected, null, ex.Code,
                ex.Code is "from-step-slot" or "from-step-value" ? ex.Message : null);
            TraceStep("after", in traceOrigin, step.ProviderId, step.NodeId, step.BindingId, ref command,
                step.NodeKind, traceInputs, traceParameters, null,
                new RuntimeLogResult { Status = CommandStatuses.Rejected, Reason = ex.Code });
            throw;
        }
        catch (Exception ex)
        {
            if (!deepTraceStarted)
                TraceStep("before", in traceOrigin, step.ProviderId, step.NodeId, step.BindingId, ref command,
                    step.NodeKind, traceInputs, traceParameters);
            log.LogStepFinished(in traceOrigin, step.LogGate, step.ProviderId, step.NodeId, step.BindingId, ref command,
                step.NodeKind, CommandStatuses.Failed, null, "pure-evaluation-failed");
            TraceStep("after", in traceOrigin, step.ProviderId, step.NodeId, step.BindingId, ref command,
                step.NodeKind, traceInputs, traceParameters, null,
                new RuntimeLogResult { Status = CommandStatuses.Failed, Reason = "pure-evaluation-failed" });
            throw new RuntimeContractException("pure-evaluation-failed", ex.GetType().Name + ": " + ex.Message);
        }
        log.LogStepFinished(in traceOrigin, step.LogGate, step.ProviderId, step.NodeId, step.BindingId, ref command,
            step.NodeKind, CommandStatuses.Succeeded, null, "evaluated");
        if (behaviorTraceObservers.Count != 0)
            TraceStep("after", in traceOrigin, step.ProviderId, step.NodeId, step.BindingId, ref command,
                step.NodeKind, traceInputs, traceParameters, outputs.ToJson(),
                new RuntimeLogResult { Status = CommandStatuses.Succeeded, Reason = "evaluated" });
        step.EvaluatedActivation = kernelFlow.valueActivation;
        executor.validatedSlots[stepIndex] = true;
        typed = outputs;
        return default;
    }

    /// <summary>
    /// One value out of a frame a step published, addressed by the port id the plan declared. A declared port the
    /// publishing step did not actually produce is refused by name — a step that asked for a value nothing wrote is
    /// refused where it is read rather than surfacing as a missing JSON property out of the dispatch. A result-row
    /// port is not one of these values: it is read from the activation's typed region through the compiled
    /// descriptor (<c>StepRows</c>), so a frame never carries a row.
    /// </summary>
    private static JsonElement StepSlotValue(JsonElement frame, PortView port, string nodeId, string inputName)
    {
        if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty(port.IdUtf8, out var value))
            throw new RuntimeContractException("from-step-value", nodeId + "." + inputName + "." + port.Id);
        return value;
    }

    /// <summary>
    /// The work of one dispatch: every candidate plan whose own mount targets claim this event, each carrying the
    /// entities those targets accepted — the plan's `self` for this dispatch. A plan no target claims is left out
    /// entirely, so an event costs nothing until it belongs to that plan. An entrypoint whose trigger is attached to
    /// one map object is judged on that object first, so the mount targets and their resolvers are never asked about
    /// an event the trigger was not attached to.
    /// </summary>
    /// <remarks>A template claimed without a subject is its own claim — it names no subject either — so a dispatch
    /// every template claims that way carries the subscription's own array, and only a dispatch that drops a
    /// template or names a subject builds a list of its own.</remarks>
    private IReadOnlyList<Work> ClaimedWork(Subscription subscription, RuntimeEvent value, EventSubject eventSubject)
    {
        var candidates = subscription.Work;
        List<Work>? claimed = null;
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            Work? taken = null;
            if (EventScopeMatches(candidate.Entry, value) && EnumFilterMatches(candidate.Entry, value)
                && MatchesAttachments(value, candidate.Entry, eventSubject, out var subjects))
                taken = subjects.Count == 0 ? candidate : candidate with { Subjects = subjects };
            if (claimed == null)
            {
                if (ReferenceEquals(taken, candidate)) continue;
                claimed = new List<Work>(candidates.Length);
                for (var j = 0; j < i; j++) claimed.Add(candidates[j]);
            }
            if (taken != null) claimed.Add(taken);
        }
        return claimed ?? (IReadOnlyList<Work>)candidates;
    }

    /// <summary>
    /// Whether an entrypoint's trigger receives this event under its own structural enum filters. A trigger that
    /// declares a structural enum parameter and an output port of the same name is narrowed to one member: the
    /// event must carry that member on that port, or the entry is never claimed and its own mount targets are
    /// never asked about it. The member index was resolved at load, in the port's own set, so this compares the
    /// integer the payload carries and never a member name. A parameter whose trigger declares no output of the
    /// same name takes part in no filter, and an event that does not carry the port carries no member either.
    /// </summary>
    private bool EnumFilterMatches(ResolvedEntry entry, RuntimeEvent value)
    {
        if (entry.EnumFilters is not { Count: > 0 } filters) return true;
        for (var i = 0; i < filters.Count; i++)
        {
            // The member is the index the wire carries: the cell's own where it was written by position, the
            // document's number where the event arrived as one.
            var cell = executor.EventCell(value, filters[i].Cell);
            var wire = cell.WireJson;
            int member;
            if (wire.ValueKind == JsonValueKind.Number) { if (!wire.TryGetInt32(out member)) return false; }
            else if (wire.ValueKind == JsonValueKind.Undefined && cell.Typed && cell.Kind == ValueKind.Integer && cell.Integer is >= int.MinValue and <= int.MaxValue) member = (int)cell.Integer;
            else return false;
            if (member != filters[i].Member) return false;
        }
        return true;
    }

    /// <summary>
    /// Whether an entrypoint's trigger receives this event. Its resource scope, pre-resolved at load, must first
    /// equal the fact's canonical resource identity. An object address additionally requires that object's subject
    /// (rule 143.11); an entry with neither filter receives every event of its binding.
    /// The trigger's object is the map object address the plan carries; the event's is the address segment
    /// of the first subject entity a registered namespace owns, so the two texts compared are one spelling of one
    /// object. An event that resolves to no such entity never matches a bound trigger: a filter can only narrow the
    /// events of a binding, so a trigger that named an object it cannot see stays silent instead of firing for every
    /// object of that category. A fact whose own scope is that very address names its object too: a fixed level item
    /// publishes its authored address as the fact's scope while the entity it carries is its live network instance.
    /// The gate's `source` list narrows the same way: the event must name one of its objects.
    /// </summary>
    private bool EventScopeMatches(ResolvedEntry entry, RuntimeEvent value)
    {
        if (entry.ResourceScope != null && !string.Equals(entry.ResourceScope, value.ScopeId, StringComparison.Ordinal)) return false;
        if (entry.Scope is { } scope && !EventNames(entry, value, scope)) return false;
        if (entry.Gate?.Source is not { } sources) return true;
        for (var i = 0; i < sources.Length; i++)
            if (EventNames(entry, value, sources[i])) return true;
        return false;
    }

    /// <summary>Whether this event is about the map object at <paramref name="scope"/>: its own scope is that address,
    /// or one entity it carries has that address.</summary>
    private bool EventNames(ResolvedEntry entry, RuntimeEvent value, string scope)
    {
        if (string.Equals(scope, value.ScopeId, StringComparison.Ordinal)) return true;
        var ports = entry.TriggerOutputs;
        var cells = executor.CellsOf(value);
        for (var i = 0; i < ports.Length; i++)
        {
            if (ports[i].Type != "entity" || (uint)entry.TriggerCells[i] >= (uint)cells.Length) continue;
            var carried = cells[entry.TriggerCells[i]].Reference;
            if (carried is EntityReference single)
            {
                if (IsSubjectAddress(single, scope)) return true;
            }
            else if (carried is EntityReference[] many)
                for (var j = 0; j < many.Length; j++)
                    if (IsSubjectAddress(many[j], scope)) return true;
        }
        return false;
    }

    /// <summary>Whether one entity reference's address segment is <paramref name="scope"/>, for a reference owned by
    /// a registered namespace. The namespace is the text before the first colon and the address is everything after
    /// it, which is the one identity shape every provider writes (<c>gtfo.map_object:door/0/0/3/security</c>); a
    /// reference whose namespace nothing registered is passed over rather than read as an address of its own.</summary>
    private bool IsSubjectAddress(EntityReference entity, string scope)
    {
        var split = entity.Id.IndexOf(':');
        if (split <= 0 || split == entity.Id.Length - 1) return false;
        return registry.TryResolver(entity.Id, out _) && entity.Id.AsSpan(split + 1).SequenceEqual(scope);
    }

    /// <summary>
    /// Whether a plan's behaviour applies to this event at all, and which entities its own mount targets accepted.
    /// Each mount target asks the provider that registered its kind: a subject-free kind is answered from the target
    /// alone, so the events that carry no subject are judged too, and a subject-matched kind is asked about every
    /// entity the trigger payload carries. The accepted entities are what the plan's steps read `self` from — the
    /// same entity on two ports is one subject, two different entities are none.
    /// </summary>
    private bool MatchesAttachments(RuntimeEvent value, ResolvedEntry entry, EventSubject eventSubject, out IReadOnlyList<EntityReference> subjects)
    {
        var matched = false;
        List<EntityReference>? accepted = null;
        EquipmentHolderMatch? held = null;
        var attachments = entry.Attachments;
        for (var i = 0; i < attachments.Length; i++)
        {
            var attachment = attachments[i];
            if (attachment.Scope is { } constraint && !constraint(null, attachment.Target.Level!)) continue;
            if (attachment.Matcher.Scope is { } scope)
            {
                if (scope(attachment.Target.Category, attachment.Target.Reference)) matched = true;
                continue;
            }
            if (attachment.Holder is { } holder)
            {
                EntityReference? equipment = null;
                EquipmentHolderSubject player;
                if (attachment.EquipmentAttachment is { } roles)
                {
                    if (CellEntity(executor.EventCell(value, attachment.HolderCell)) is not { } source
                        || CellEntity(executor.EventCell(value, attachment.EquipmentCell)) is not { } gear) continue;
                    equipment = gear;
                    player = new EquipmentHolderSubject(source);
                }
                else
                {
                    if (eventSubject.Count != 1) continue;
                    player = eventSubject.Holder;
                }
                if (player.PlayerNumber == 0) continue;
                var match = holder(player);
                if (match == null) continue;
                CheckHolderIdentity(match.Equipment, match.EquipmentCurrent);
                CheckHolderIdentity(match.Holder, match.HolderCurrent);
                if (equipment != null && match.Equipment != equipment) continue;
                if (match.Subject.PlayerNumber != player.PlayerNumber || match.Holder.WorldEpoch != player.Player.WorldEpoch
                    || match.Holder.LifeEpoch != player.Player.LifeEpoch) continue;
                if (held == null) held = match;
                else if (held.Equipment != match.Equipment)
                {
                    if (accepted == null) accepted = new List<EntityReference> { held.Equipment };
                    accepted.Add(match.Equipment);
                }
                matched = true;
                continue;
            }
            if (Matches(attachment.Target, attachment.Matcher.Subject!, value, attachment.PortCells, ref accepted)) matched = true;
        }
        if (held != null && accepted != null && !accepted.Contains(held.Equipment)) accepted.Add(held.Equipment);
        subjects = accepted ?? held?.Subjects ?? (IReadOnlyList<EntityReference>)Array.Empty<EntityReference>();
        return matched;
    }

    private void CheckHolderIdentity(EntityReference entity, Func<bool> current)
    {
        RuntimeJson.Require(entity.WorldEpoch == WorldEpoch, "stale-world", entity.Id);
        RuntimeJson.Require(current(), "stale-entity", entity.Id);
    }

    private bool Matches(PlanAttachment attachment, AttachmentMatcher matcher, RuntimeEvent value, int[] ports,
        ref List<EntityReference>? accepted)
    {
        var matched = false;
        var cells = executor.CellsOf(value);
        for (var i = 0; i < ports.Length; i++)
        {
            if ((uint)ports[i] >= (uint)cells.Length) continue;
            var carried = cells[ports[i]].Reference;
            if (carried is EntityReference single)
            {
                if (Accept(matcher, attachment, single, ref accepted)) matched = true;
            }
            else if (carried is EntityReference[] many)
                for (var j = 0; j < many.Length; j++)
                    if (Accept(matcher, attachment, many[j], ref accepted)) matched = true;
        }
        return matched;
    }

    /// <summary>One port value the matcher accepted: it both claims the plan and becomes one of the plan's subjects.</summary>
    private static bool Accept(AttachmentMatcher matcher, PlanAttachment attachment, EntityReference entity, ref List<EntityReference>? accepted)
    {
        if (!matcher(attachment.Category, attachment.Reference, entity)) return false;
        (accepted ??= new List<EntityReference>()).Add(entity);
        return true;
    }
}

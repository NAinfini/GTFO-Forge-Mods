using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text;
using System;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>Where a walk stands and what it left scheduled: the control frames of the activation in hand, its loop, group and play state, the captured flow a suspended walk resumes from, and the schedule table with its create, stop, restart and report bookkeeping, plus the ledger sweep that reads it. The walk itself, which invokes handlers with the kernel in hand, is driven from above.</summary>
internal sealed partial class KernelFlow
{
    private readonly KernelWorld world;
    private readonly KernelRegistry registry;
    private readonly KernelLog log;
    private readonly KernelPlans plans;
    private readonly KernelExecutor executor;
    private readonly EventQueue eventQueue;
    private readonly KernelVariables kernelVariables;
    private readonly KernelEffects kernelEffects;
    private readonly KernelOwner kernelOwner;
    private readonly RuntimeLimits limits;

    internal KernelFlow(KernelWorld world, KernelRegistry registry, KernelLog log, KernelPlans plans, KernelExecutor executor, EventQueue eventQueue, KernelVariables kernelVariables, KernelEffects kernelEffects, KernelOwner kernelOwner, RuntimeLimits limits)
    {
        this.world = world;
        this.registry = registry;
        this.log = log;
        this.plans = plans;
        this.executor = executor;
        this.eventQueue = eventQueue;
        this.kernelVariables = kernelVariables;
        this.kernelEffects = kernelEffects;
        this.kernelOwner = kernelOwner;
        this.limits = limits;
    }

    internal FlowSnapshot CaptureFlow(ResolvedEntry entry)
        => new(CaptureStoredValues(entry), executor.CaptureActionFrames(entry), CaptureResultRows(entry));

    internal void RestoreFlow(ResolvedEntry entry, FlowSnapshot flow)
    {
        RestoreStoredValues(entry, flow.StoredValues);
        executor.RestoreActionFrames(flow.ActionFrames);
        RestoreResultRows(entry, flow.Rows);
    }

    // A timer starts another activation, but values already read/forwarded by this invocation belong to its
    // suspended flow. Capture the immutable wire values, not the shared descriptor buffers another event reuses.
    // Queries/pure evaluations remain activation-local and are deliberately not captured here.
    // An action's value outputs (an effect or timer handle it published) belong to the flow too: a step after a
    // `delay` that cancels what an earlier action started reads that action's frame, so the frame crosses the
    // suspension as the handle value it is and is checked for liveness where it is read.
    // Its result rows (the handle a `weapon_property` publishes is a row port, not a frame value) cross the same
    // way: only the rows this activation wrote are captured, so the rule that a stranger activation's rows are not
    // readable still holds, and the row is read through the descriptor where a later step names it.
    internal ResultRowSnapshot[] CaptureResultRows(ResolvedEntry entry)
    {
        var rows = new List<ResultRowSnapshot>();
        for (var index = 0; index < entry.Steps.Count; index++)
        {
            var step = entry.Steps[index];
            if (step.Frames.Row is not { } row || !executor.resultRows.TryGetValue(step.Frames.Index, out var set)) continue;
            var slots = new FrameValue[row.Stride * set.Count];
            Array.Copy(executor.resultArena.Slots, row.Slot, slots, 0, slots.Length);
            var strings = new string?[slots.Length];
            var memory = executor.resultArena.Strings.Memory.Span;
            for (var slot = 0; slot < slots.Length; slot++)
                if (slots[slot].Kind == ValueKind.String)
                    strings[slot] = Encoding.UTF8.GetString(memory.Slice(slots[slot].StringOffset, slots[slot].StringLength));
            rows.Add(new ResultRowSnapshot(index, set.Count, set.Set, slots, strings));
        }
        return rows.ToArray();
    }

    internal void RestoreResultRows(ResolvedEntry entry, ResultRowSnapshot[] snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var step = entry.Steps[snapshot.Step];
            var row = step.Frames.Row!;
            for (var slot = 0; slot < snapshot.Slots.Length; slot++)
            {
                var value = snapshot.Slots[slot];
                if (snapshot.Strings[slot] is { } text)
                {
                    var added = executor.resultArena.Strings.Add(text);
                    value.StringOffset = added.Offset; value.StringLength = added.Length;
                }
                executor.resultArena.Slots[row.Slot + slot] = value;
            }
            executor.resultRows[step.Frames.Index] = executor.RowSet(row, snapshot.Count, snapshot.Set);
        }
    }

    internal StoredValueSnapshot[] CaptureStoredValues(ResolvedEntry entry)
    {
        var snapshots = new List<StoredValueSnapshot>();
        for (var index = 0; index < entry.Steps.Count; index++)
        {
            var step = entry.Steps[index];
            if (step.StoredOutputs == null || step.OutputActivation != valueActivation) continue;
            snapshots.Add(new StoredValueSnapshot(index, (JsonElement[])step.StoredOutputs.Clone(),
                (ResourceRef?[])step.StoredResolved!.Clone()));
        }
        return snapshots.ToArray();
    }

    internal void RestoreStoredValues(ResolvedEntry entry, StoredValueSnapshot[] snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var step = entry.Steps[snapshot.Step];
            Array.Copy(snapshot.Values, step.StoredOutputs!, snapshot.Values.Length);
            Array.Copy(snapshot.Resolved, step.StoredResolved!, snapshot.Resolved.Length);
            step.OutputActivation = valueActivation;
        }
    }

    internal readonly List<ControlFrame> controlWalk = new();

    /// <summary>Whether this activation's walk stopped at a control step that suspended it — a `delay` or a
    /// `g-wait` — rather than at the end of the innermost region. The two are not the same thing: a region that
    /// ended hands the walk back to the control that encloses it, while a suspended step owns the rest of the path
    /// through the continuation it left behind, so the enclosing region must not be advanced here. A `ramp` is not
    /// one of these: its exit is entered by its own schedule, so its entry leaves the walk where it was.
    /// Consumed by <see cref="ResumeControl"/>, which is the only reader.</summary>
    internal bool walkSuspended;

    /// <summary>How many continuations this kernel has scheduled. It is the second half of a continuation's own
    /// identity, so a control step entered twice never names one schedule from two entries; it is not reset with the
    /// world, because a name a previous world used must never be handed out again while a handle could still spell
    /// it.</summary>
    internal long scheduleSequence;

    internal long stepExecutions;

    internal long valueActivation;

    /// <summary>Starts an activation: `pure` and `query` results live inside one, so each dispatch, loop round and
    /// schedule resumption re-evaluates on demand instead of reusing what an earlier activation observed. The walk
    /// starts empty with it. Synchronous rounds refresh only their own snapshots through BeginLoopRound.</summary>
    internal void BeginActivation()
    {
        valueActivation++;
        executor.stepFrames.Clear();
        executor.validatedSlots.Clear();
        executor.memoEntities.Clear();
        // The activation's typed result rows go with it: the rows a step wrote are readable only inside the
        // activation that wrote them, the string area is rewound here, and every row write clears the row's own
        // slots first — so a later dispatch can never read a row an earlier one wrote. The one exception is the
        // flow a suspension captured (see FlowSnapshot): its resumption restores exactly the rows that flow wrote.
        executor.resultRows.Clear();
        executor.resultArena.Strings.Reset();
        stepExecutions = 0;
        walkSuspended = false;
        controlWalk.Clear();
        activationPlayKnown = false;
    }

    /// <summary>A round refreshes observations and its own execution snapshots, not values published before
    /// the loop. The entry identity stays fixed, so stored control outputs outside the body remain readable.</summary>
    internal void BeginLoopRound(in ControlFrame frame, ResolvedEntry entry)
    {
        foreach (var index in frame.Descriptor.RoundResetSteps)
        {
            var step = entry.Steps[index];
            step.OutputActivation = -1;
            executor.stepFrames.Remove(index);
            executor.validatedSlots.Remove(index);
            executor.resultRows.Remove(step.Frames.Index);
            if (step.OutputViews is not { } ports) continue;
            for (var port = 0; port < ports.Length; port++) executor.memoEntities.Remove(step.EntityMemoBase + port);
        }
        executor.resultArena.Strings.Reset(frame.RowStringsStart);
        stepExecutions = 0;
        walkSuspended = false;
    }

    /// <summary>True once this activation has spent its step executions; the dispatch is then refused by name
    /// instead of running a plan that looped further than one activation may.</summary>
    internal bool StepBudgetSpent(RuntimeLimits limits) => stepExecutions > limits.MaxStepExecutionsPerDispatch;

    /// <summary>Re-enters a control region after a schedule fired: the control step writes its timer frame again —
    /// the handle a `cancel` later in the pulse body reads — and the walk starts where the continuation said. A
    /// `repeat` pulse also publishes its round: the scheduler has already counted the pulse it is dispatching, so the
    /// 0-based `index` is that count less one (a pulse the scheduler skipped as missed keeps its number). A ramp
    /// writes its own frame instead, because the value it publishes is the whole point of the pulse and the activation
    /// that reads it is this one.</summary>
    internal int? ResumeActivation(ResolvedEntry entry, Continuation continuation, int pulse)
    {
        BeginActivation();
        RestoreFlow(entry, continuation.Flow);
        RestoreWalk(continuation.Walk);
        if (continuation.LoopValues is { } loops)
            for (var index = 0; index < loops.Count; index++)
            {
                var loop = loops[index];
                loop.Held = true;
                loop.RowStringsStart = executor.resultArena.Strings.Count;
                controlWalk.Add(loop);
                executor.WriteLoopFrame(loop);
            }
        executor.stepFrames[continuation.Step] = continuation.Ramp is { } ramp
            ? RuntimeJson.From(new { value = ramp.ValueAt(pulse), handle = continuation.HandleValue })
            : continuation.Indexed ? RuntimeJson.From(new { index = pulse - 1, timer = continuation.HandleValue })
            : TimerFrame(continuation.HandleValue);
        return Successor(entry.Steps[continuation.Step], continuation.Output);
    }

    /// <summary>
    /// Puts back the enclosing region a suspended step left behind, and republishes the value frame of every loop
    /// in it. The region is what the resumed activation keeps walking after the suspended step's own exit is
    /// walked, and a loop's `index`/`item` are the round it is still in rather than a value of the activation that
    /// ended — a step after the suspension reads them exactly as the same step read them before it. A control whose
    /// exit is entered by its own schedule (a `repeat` pulse, a ramp tick) passes no walk at all, so this is a
    /// no-op for it.
    /// </summary>
    internal void RestoreWalk(IReadOnlyList<ControlFrame>? walk)
    {
        if (walk is not { Count: > 0 }) return;
        controlWalk.AddRange(walk);
        for (var index = 0; index < controlWalk.Count; index++)
        {
            var frame = controlWalk[index];
            // Resumption starts a fresh arena; offsets captured by the previous activation no longer apply.
            frame.RowStringsStart = executor.resultArena.Strings.Count;
            controlWalk[index] = frame;
            if (frame.Loop) executor.WriteLoopFrame(frame);
        }
        // The group frames a suspension carried are the groups it sits in, innermost last: the owner of the
        // suspension that resumed and that owner's own owners, in that order. Naming them from the chain rather than
        // from the captured frame is what lets a migrated walk, which carries only the mark, find its groups again,
        // and the frames republish each group's handle and attempt for the steps after the suspension.
        var chain = activationGroup;
        for (var index = controlWalk.Count - 1; index >= 0; index--)
        {
            var frame = controlWalk[index];
            if (!frame.IsGroup) continue;
            frame.Group = chain; frame.Detached = true;
            controlWalk[index] = frame;
            if (chain != null) { executor.WriteGroupFrame(frame.Step, chain); chain = chain.Job.Owner; }
        }
    }

    /// <summary>A step walked inside a loop runs once per round under the same event, and every round is its own
    /// command: the rounds of the enclosing loops, outermost first, are folded into one number (FNV-1a, as a play
    /// identity is) that closes the step's identity, so an owner step's second round is a second application instead of
    /// a conflicting replay of the first. A suspension inside a round carries the walk, so the resumed step folds the same
    /// number (62 bits). Null outside a loop: the identity is unchanged. Reads the live walk only; nothing is allocated.</summary>
    internal long? WalkRound()
    {
        long? rounds = null;
        for (var index = 0; index < controlWalk.Count; index++)
            if (controlWalk[index].Loop)
                rounds = Fnv(rounds ?? unchecked((long)14695981039346656037UL), controlWalk[index].Round);
        // Never negative, so a command context can keep it beside the sequence a pulse takes (CommandContext.DeferCommandId).
        return rounds & long.MaxValue / 2;
    }

    /// <summary>Advances the innermost control whose region just ended: the next exit of a sequence, the next round
    /// of a loop, or — when none is left — whatever encloses it. False means the whole walk is done, which is also
    /// the answer for a step that suspended it: a `delay` and a `g-wait` own the rest of their path through the
    /// continuation they left behind, so the region that encloses them is not advanced past them.</summary>
    internal bool ResumeControl(ResolvedEntry entry, out int cursor)
    {
        if (walkSuspended)
        {
            walkSuspended = false;
            cursor = -1;
            return false;
        }
        while (controlWalk.Count > 0)
        {
            var frame = controlWalk[^1];
            // A loop the pulse only runs inside: the pulse's region is over, and the loop is not its to advance.
            if (frame.Held) break;
            if (frame.IsGroup)
            {
                controlWalk.RemoveAt(controlWalk.Count - 1);
                var after = EndGroupBody(entry, frame);
                if (after == null) { cursor = -1; return false; }
                if (after == GroupWalkOn) continue;
                cursor = after.Value;
                return true;
            }
            if (frame.Loop)
            {
                var round = frame.Round + 1;
                if (round >= frame.Rounds)
                {
                    controlWalk.RemoveAt(controlWalk.Count - 1);
                    // A loop's exit is `next`; a null one means the enclosing control continues instead.
                    if (frame.Successors[0] >= 0) { cursor = frame.Successors[0]; return true; }
                    continue;
                }
                frame.Round = round;
                controlWalk[^1] = frame;
                // The new observation activation belongs to the same entry; retain its enclosing snapshots.
                BeginLoopRound(frame, entry);
                executor.WriteLoopFrame(frame);
                cursor = frame.Successors[1];
                if (cursor >= 0) return true;
                continue;
            }
            var next = frame.Output + 1;
            while (next < frame.Successors.Length && frame.Successors[next] < 0) next++;
            if (next >= frame.Successors.Length)
            {
                controlWalk.RemoveAt(controlWalk.Count - 1);
                continue;
            }
            frame.Output = next;
            controlWalk[^1] = frame;
            cursor = frame.Successors[next];
            return true;
        }
        cursor = -1;
        return false;
    }

    /// <summary>
    /// The region a suspending step sits in, captured for the continuation that will re-enter it: empty where the
    /// step stands at the top of its entrypoint, and otherwise the frames of the enclosing controls, outermost
    /// first. The copy is shallow and made only when a step actually suspends — the frames hold immutable successor
    /// tables and item lists — so nothing here is on the per-tick path.
    /// </summary>
    internal IReadOnlyList<ControlFrame> EnclosingWalk()
        => controlWalk.Count == 0 ? Array.Empty<ControlFrame>() : controlWalk.ToArray();

    /// <summary>The loops a pulse series (`repeat`, `ramp`) sits inside. The pulse is its own region and does not resume them,
    /// but the body reads the round each of them was in when the series started, so their `item`/`index` frames travel with it.</summary>
    internal IReadOnlyList<ControlFrame>? EnclosingLoops()
    {
        List<ControlFrame>? loops = null;
        for (var index = 0; index < controlWalk.Count; index++)
            if (controlWalk[index].Loop) (loops ??= new List<ControlFrame>()).Add(controlWalk[index]);
        return loops?.ToArray();
    }

    internal RuntimeContractException MissingStepSlot(ResolvedEntry entry, ResolvedStep consumer, StepSlotRef source,
        string code = "from-step-slot")
    {
        var loop = -1; var round = 0;
        for (var index = controlWalk.Count - 1; index >= 0; index--)
            if (controlWalk[index].Loop)
            { loop = controlWalk[index].Step; round = controlWalk[index].Round + 1; break; }
        return new RuntimeContractException(code,
            $"slot={source.Port}; producerStep={source.Step}; loopStep={loop}; round={round}; "
            + $"producer={entry.Steps[source.Step].NodeId}; consumer={consumer.NodeId}");
    }

    internal void AdoptEffectLayerSource(EffectSlot slot, EffectLayer layer)
    {
        slot.Origin = layer.Origin; slot.Inputs = layer.Inputs; slot.Parameters = layer.Parameters; slot.InputFrame = layer.InputFrame;
        slot.InputLiveness = layer.InputLiveness;
        slot.CommandPrefix = layer.CommandPrefix;
        slot.CommandId = layer.CommandId;
        if (layer.OwnerApplication != null && kernelEffects.ownerEffects.TryGetValue(slot.HandleId, out var original))
            kernelEffects.ownerEffects[slot.HandleId] = new OwnerEffect(layer.OwnerApplication,
                new Pending(layer.SourceProvider, MigrationGeneration(layer.SourceProvider), layer.Origin, original.Cause.Work), slot);
    }

    /// <summary>The group the activation being walked belongs to: the owner of the suspension it resumed. A group
    /// frame the walk pushes or restores is more specific and wins (<see cref="CurrentGroup"/>).</summary>
    internal FlowGroup? activationGroup;

    /// <summary>The schedule whose pulse the current activation is, so a group's own exit is not ended under it.</summary>
    internal ScheduledJob? walkingSchedule;

    /// <summary>Set when a group's `failed` or `timed_out` exit is entered and nothing is wired to it: the failure is
    /// then the enclosing line's, and the enclosing group sees it as one (an unhandled failure propagates).</summary>
    internal bool groupFailurePropagates;

    internal readonly List<FlowGroup> groupChecks = new();

    internal FlowGroup? CurrentGroup()
    {
        for (var index = controlWalk.Count - 1; index >= 0; index--)
            if (controlWalk[index].IsGroup) return controlWalk[index].Group;
        return activationGroup;
    }

    internal void QueueGroupCheck(FlowGroup group)
    {
        if (!groupChecks.Contains(group)) groupChecks.Add(group);
    }

    /// <summary>How many groups this card has running right now, across every activation of the plan.</summary>
    internal int LiveGroupsOf(ResolvedStep step)
    {
        var count = 0;
        foreach (var job in schedules.Values)
            if (job.Group is { } group && ReferenceEquals(group.Step, step)) count++;
        return count;
    }

    /// <summary>Places the group's next pulse at <paramref name="tick"/>, to enter <paramref name="output"/>. A pulse
    /// already queued for it is dropped by the compaction, the same way a `restart` re-arms a timer.</summary>
    internal void ArmGroup(FlowGroup group, long tick, int output)
    {
        var job = group.Job;
        job.Resume = job.Resume! with { Output = output };
        DisarmGroup(group);
        job.FirstTick = tick - job.Index;
        group.Queued = true;
        eventQueue.QueuePulse(job);
    }

    internal void DisarmGroup(FlowGroup group)
    {
        if (!group.Queued) return;
        group.Job.ReArmed = true; eventQueue.queueDirty = true; PruneQueue();
        group.Queued = false;
    }

    internal int? EndGroupBody(ResolvedEntry entry, in ControlFrame frame)
    {
        var group = frame.Group;
        if (group == null) { controlWalk.Clear(); return null; }
        if (frame.Detached || !group.Job.Handle.Active || group.Ending)
        {
            // A resumed line reached the end of its group's body, or the group already ended: the line is over, and
            // the flow after the group belongs to the group's own exit, never to this line.
            controlWalk.Clear();
            return null;
        }
        if (group.Members == 0 && !group.LineFailed)
        {
            group.Ending = true; group.EndCode = "group-done";
            DisarmGroup(group);
            // The group's own pulse (a retry) is the activation being walked: it ends when that dispatch completes.
            if (!ReferenceEquals(walkingSchedule, group.Job)) EndSchedule(group.Job, "completed", "group-done");
            var done = frame.Successors[GroupDone];
            return done >= 0 ? done : GroupWalkOn;
        }
        SuspendGroup(entry, group, controlWalk.Count);
        return null;
    }

    /// <summary>Keeps the region beneath one live group frame and the flow it had: what the group's exits continue.</summary>
    internal void SuspendGroup(ResolvedEntry entry, FlowGroup group, int depth)
    {
        var beneath = depth == 0 ? Array.Empty<ControlFrame>() : controlWalk.GetRange(0, depth).ToArray();
        group.Job.Resume = group.Job.Resume! with { Walk = beneath, Flow = CaptureFlow(entry) };
        group.Suspended = true;
        QueueGroupCheck(group);
    }

    /// <summary>
    /// The end of one activation's walk. Every group whose body region is still open on the walk — its lines left a
    /// suspension behind, or the walk stopped inside it — keeps the region beneath it. A walk that stopped (a failed
    /// action nothing handled, a refused control) failed the line it was on, which fails the innermost group's attempt.
    /// </summary>
    internal void SettleGroups(ResolvedEntry entry, bool stopped)
    {
        var failing = stopped ? CurrentGroup() : null;
        for (var index = controlWalk.Count - 1; index >= 0; index--)
            if (controlWalk[index] is { IsGroup: true, Detached: false, Group: { } group }
                && group.Job.Handle.Active && !group.Ending && !group.Suspended)
                SuspendGroup(entry, group, index);
        if (failing == null || !failing.Job.Handle.Active || failing.Ending) return;
        failing.LineFailed = true;
        QueueGroupCheck(failing);
    }

    internal void Finish(FlowGroup group, int output, string code)
    {
        group.Ending = true; group.EndCode = code;
        ArmGroup(group, world.Tick, output);
    }

    /// <summary>A `restart` on a group re-arms its time limit from now; a group with no time limit, or one that
    /// already chose its exit, has nothing to restart and answers false.</summary>
    internal bool RestartGroup(FlowGroup group)
    {
        if (group.TimeLimit == 0 || group.Ending || !group.Queued || group.Job.Resume!.Output != GroupTimedOut) return false;
        ArmGroup(group, world.Tick + group.TimeLimit, GroupTimedOut);
        return true;
    }

    /// <summary>Re-arms the schedule one timer handle holds from now — the rolling window a `restart` asks for.
    /// Unlike cancel the handle is not spent: it names the same schedule for its whole life. A handle with no
    /// schedule behind it is refused by name, and a handle whose schedule already ended fails the slot check as
    /// `stale-handle` before this is reached: a restart never quietly creates a second timer.</summary>
    internal bool RestartHandle(JsonElement value, JsonElement port, string detail)
    {
        var slot = kernelEffects.CheckHandle(value, port, detail);
        if (slot.ScheduleId == null || !schedules.TryGetValue((slot.ProviderId, slot.ScheduleId), out var job))
            throw new RuntimeContractException("timer-without-schedule", detail);
        return RestartSchedule(job);
    }

    /// <summary>Whether one more identity fits. A full ledger first forgets what the horizon allows and then, if
    /// that is not enough, the oldest event ticks (<see cref="EvictOldestEvents"/>) — once per tick, so a saturated
    /// tick does not rescan it for every refusal.</summary>
    internal bool LedgerHasRoom()
    {
        if (eventQueue.history.Count < MaximumEventHistory) return true;
        if (eventQueue.ledgerFullSweepTick != world.Tick)
        {
            eventQueue.ledgerFullSweepTick = world.Tick;
            SweepLedger();
            if (eventQueue.history.Count >= MaximumEventHistory) eventQueue.EvictOldestEvents();
        }
        return eventQueue.history.Count < MaximumEventHistory;
    }

    /// <summary>
    /// Forgets every row whose tick is more than <see cref="EventHistoryHorizonTicks"/> behind the current tick.
    /// An active lease or schedule is stamped with the current tick first, so its identity is remembered for at
    /// least one horizon after it stops being active. A forgotten event identity cannot be replayed: its tick is
    /// older than the horizon, and `Publish` refuses such an event as `event-stale` before it reads the ledger.
    /// </summary>
    internal void SweepLedger()
    {
        eventQueue.nextLedgerSweepTick = world.Tick + EventHistorySweepTicks;
        var now = Math.Max(0, world.Tick);
        foreach (var lease in kernelVariables.stateLeases) eventQueue.Restamp(new LedgerKey(lease.Key.Provider, LedgerSpace.Lease, lease.Key.Id), now);
        foreach (var job in schedules) eventQueue.Restamp(new LedgerKey(job.Key.Provider, LedgerSpace.Schedule, job.Key.Id), now);
        // A provider occurrence that already charged its gates owns this row until it leaves the queue. The
        // queue and ledger keep their existing bounds; cancelled/completed schedules and consumed occurrences
        // stop pinning immediately, then age out through the same horizon as every other completed fact.
        foreach (var item in eventQueue.queue.UnorderedItems)
            if (item.Element.PulseClaimed && item.Element.Schedule?.Handle.Active == true)
                eventQueue.Restamp(new LedgerKey(item.Element.Provider, LedgerSpace.Event, item.Element.Event.EventId), now);
        var cutoff = world.Tick - EventHistoryHorizonTicks;
        // Dictionary.Remove preserves active enumerators on the supported .NET runtime. Retiring in place
        // needs no scratch list whose first large expiry would otherwise allocate during Advance.
        foreach (var entry in eventQueue.history) if (entry.Value.Tick < cutoff) eventQueue.history.Remove(entry.Key);
    }

    internal Pending ReadMigrationOwnerCause(JsonElement row)
    {
        RuntimeJson.Shape(row, "provider event sequence tabled work");
        var provider = RuntimeJson.Text(row, "provider");
        return new Pending(provider, MigrationGeneration(provider), ReadMigrationEvent(row.GetProperty("event")),
            RuntimeJson.Rows(row, "work", limits.MaxTotalSteps).Select(RequireMigrationWork).ToArray(),
            Sequence: RuntimeJson.Integer(row.GetProperty("sequence")), Tabled: row.GetProperty("tabled").GetBoolean());
    }

    /// <summary>Rebuilds the selected subject of an already-admitted event under the replacement host's own
    /// descriptor. Historical facts are not re-published or rechecked for current liveness; their captured entity
    /// identities are shape-checked and rebased, then compared exactly when the queued fact is dispatched.</summary>
    internal EventSubject ReadMigrationEndSubject(string provider, RuntimeEvent value)
    {
        RuntimeJson.Require(registry.BindingViews.TryGetValue(value.BindingId, out var binding) && binding.ProviderId == provider,
            "migration-binding", value.BindingId);
        if (binding!.EndSubjectIndex < 0) return default;
        var port = binding.Outputs[binding.EndSubjectIndex];
        ShapePorts(value.Outputs, binding.Outputs, optionalAllowed: true);
        if (!value.Outputs.TryGetProperty(port.IdUtf8, out var data)) return default;
        port.Validate(data, null);
        if (data.ValueKind == JsonValueKind.Null) return default;
        EntityReference Read(JsonElement row)
        {
            var reference = RuntimeJson.EntityRow(row);
            RuntimeJson.Require(reference.WorldEpoch == world.WorldEpoch, "migration-world", value.EventId);
            return reference;
        }
        if (!port.Many) return new EventSubject(Read(data));
        var subjects = new EntityReference[data.GetArrayLength()];
        var index = 0;
        foreach (var row in data.EnumerateArray())
        {
            var reference = Read(row);
            RequireDistinct(subjects, index, reference);
            subjects[index++] = reference;
        }
        return new EventSubject(subjects);
    }

    internal object? DeserializeMigrationGateRelease(JsonElement value)
    {
        RuntimeJson.Shape(value, "provider event work");
        var provider = RuntimeJson.Text(value, "provider");
        if (!registry.Modules.TryGetValue(provider, out var generation)) return null;
        var item = DeserializeMigrationWork(value.GetProperty("work"));
        if (item == null) return null;
        var origin = new Pending(provider, generation, ReadMigrationEvent(value.GetProperty("event")), new[] { item });
        return new GateReleasePayload(origin, item);
    }

    internal Work? DeserializeMigrationWork(JsonElement value)
    {
        RuntimeJson.Shape(value, "planId nodeId subjects");
        var planId = RuntimeJson.Text(value, "planId");
        var nodeId = RuntimeJson.Text(value, "nodeId");
        if (!plans.Loaded.TryGetValue(planId, out var plan)) return null;
        var entry = plan.Plan.Entries.SingleOrDefault(candidate => candidate.NodeId == nodeId);
        if (entry == null) return null;
        var subjects = RuntimeJson.Rows(value, "subjects", 256).Select(RuntimeJson.Entity).ToArray();
        return new Work(plan, entry, subjects);
    }

    internal Work RequireMigrationWork(JsonElement value)
        => DeserializeMigrationWork(value) ?? throw new RuntimeContractException("migration-work-plan",
            "The loaded plan no longer contains the captured continuation.");

    internal long MigrationGeneration(string provider)
        => registry.Modules.TryGetValue(provider, out var generation) ? generation
            : throw new RuntimeContractException("migration-provider", "The captured provider is unavailable: " + provider);

    // Signed offsets preserve overdue work and the original catch-up/skip policy as well as future deadlines.
    internal long MigrationTick(JsonElement offset)
        => RuntimeJson.Integer(checked(world.Tick + RuntimeJson.Integer(offset, -RuntimeJson.MaxSafeInteger, RuntimeJson.MaxSafeInteger)));

    internal void RestoreMigrationPending(JsonElement row, IReadOnlyDictionary<int, WaitJob> restoredWaits)
    {
        RuntimeJson.Shape(row, "provider event remainingTicks order sequence tabled schedule wait waitTimeout decision release work");
        var provider = RuntimeJson.Text(row, "provider");
        var rawSchedule = row.GetProperty("schedule");
        var rawWait = row.GetProperty("wait");
        var schedule = rawSchedule.ValueKind == JsonValueKind.Null ? null : schedules[(provider, RuntimeJson.Text(row, "schedule"))];
        var wait = rawWait.ValueKind == JsonValueKind.Null ? null : restoredWaits[(int)RuntimeJson.Integer(rawWait, 0, limits.MaxLiveHandles - 1)];
        // The dispatcher identifies a continuation by its owned Work instance, not just by equal plan/node text.
        var work = schedule?.Work ?? (wait != null ? new[] { wait.Item }
            : RuntimeJson.Rows(row, "work", limits.MaxTotalSteps).Select(RequireMigrationWork).ToArray());
        foreach (var plan in work.Select(item => item.Plan).Distinct())
            RuntimeJson.Require(eventQueue.QueuedFor(plan) < plan.Plan.Limits.MaxQueuedEvents, "plan-queue-budget", plan.Plan.Id);
        var release = row.GetProperty("release");
        GateReleaseTicket? ticket = null;
        if (release.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Shape(release, "planId nodeId subject generation");
            ticket = new GateReleaseTicket(RuntimeJson.Text(release, "planId"), RuntimeJson.Text(release, "nodeId"),
                release.GetProperty("subject").GetString() ?? "",
                RuntimeJson.Integer(release.GetProperty("generation"), 0, RuntimeJson.MaxSafeInteger));
        }
        var pending = new Pending(provider, MigrationGeneration(provider), ReadMigrationEvent(row.GetProperty("event")), work,
            schedule,
            RuntimeJson.Integer(row.GetProperty("sequence")), row.GetProperty("tabled").GetBoolean(),
            wait,
            row.GetProperty("waitTimeout").GetBoolean(), ticket);
        // A continuation owns its original event as context, not a second publication of that old fact.
        if (schedule == null && wait == null && ticket == null)
            pending.EndSubject = ReadMigrationEndSubject(provider, pending.Event);
        var decision = row.GetProperty("decision");
        if (decision.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Shape(decision, "work refusals");
            // Control continuations need their owned Work instance. A provider pulse is an event whose gates
            // may have admitted only a subset of its claimed entries before the snapshot was captured.
            pending.Admitted = schedule?.Resume != null || wait != null ? work
                : RuntimeJson.Rows(decision, "work", limits.MaxTotalSteps).Select(RequireMigrationWork).ToArray();
            pending.PlanUsage = PlanUsageOf(pending.Admitted);
            pending.PlanUsageGeneration = world.lifecycleGeneration;
            pending.DecidedTick = world.Tick;
            if (schedule is { Resume: null })
            {
                pending.PulseClaimed = true;
                var key = new LedgerKey(provider, LedgerSpace.Event, pending.Event.EventId);
                // The predecessor's row was verified before reset. Rebase this unconsumed occurrence's
                // fingerprint too, so a subsequent migration can validate the same durable decision again.
                eventQueue.history[key] = eventQueue.history[key] with
                {
                    Fingerprint = LedgerFingerprint(RuntimeJson.StableText(RuntimeJson.From(pending.Event)))
                };
            }
            pending.GateRefusals = RuntimeJson.Rows(decision, "refusals", limits.MaxTotalSteps).Select(refusal =>
            {
                RuntimeJson.Shape(refusal, "planId nodeId code accumulated fired");
                return new TriggerGateReceipt(RuntimeJson.Text(refusal, "planId"), RuntimeJson.Text(refusal, "nodeId"),
                    RuntimeJson.Text(refusal, "code"), RuntimeJson.Integer(refusal.GetProperty("accumulated")),
                    RuntimeJson.Integer(refusal.GetProperty("fired")));
            }).ToList();
        }
        eventQueue.queue.Enqueue(pending, (MigrationTick(row.GetProperty("remainingTicks")), RuntimeJson.Integer(row.GetProperty("order"))));
    }

    internal void CaptureOwner(Work work, int step, Pending pending, StepInputs inputs, JsonElement parameters, KernelExecutor.CostHold? hold)
    {
        var request = kernelOwner.ownerSplit?.Requests[0] ?? kernelOwner.ownerOutputs[^1];
        RuntimeJson.Require(kernelOwner.ownerFlights.Count < limits.MaxLiveHandles, "owner-pending-budget", request.CommandId);
        // The arena reuses slots between activations while rewinding its strings. Only written rows belong
        // to this activation; cloning unused slots would revive string offsets from an earlier command.
        var slots = new FrameValue[executor.resultArena.Slots.Length];
        foreach (var row in executor.resultRows.Values)
            Array.Copy(executor.resultArena.Slots, row.Row.Slot, slots, row.Row.Slot, row.Row.Stride * row.Count);
        kernelOwner.ownerFlightScratch.EnsureCapacity(kernelOwner.ownerFlights.Count + 1);
        // The flight resumes in a later activation, after the step cells were refilled by other evaluations; what
        // this activation evaluated travels with it as the frames it answered.
        var frames = new Dictionary<int, JsonElement>(executor.stepFrames);
        for (var index = 0; index < work.Entry.Steps.Count; index++)
        {
            var evaluated = work.Entry.Steps[index];
            if (evaluated.Evaluated != null && evaluated.EvaluatedActivation == valueActivation && executor.validatedSlots.ContainsKey(index))
                frames[index] = evaluated.Evaluated.ToJson();
        }
        kernelOwner.ownerFlights.Add(request.CommandId, new OwnerFlight
        {
            Request = request,
            SessionProvider = kernelOwner.ownerSessionProvider,
            // The step's frame is refilled by its next execution; the flight keeps its own copy.
            Pending = pending,
            Work = work,
            Step = step,
            Inputs = inputs.Snapshot(),
            Parameters = parameters,
            Hold = hold,
            Frames = frames,
            Stored = CaptureStoredValues(work.Entry),
            Rows = new(executor.resultRows),
            Slots = slots,
            Strings = executor.resultArena.Strings.Memory.ToArray(),
            Walk = controlWalk.ToArray(),
            Play = PlayOf(pending)
        });
        if (kernelOwner.ownerSplit is { } split)
            for (var index = 0; index < split.Requests.Count; index++) kernelOwner.ownerSplits.Add(split.Requests[index].CommandId, (split, index));
        kernelOwner.ownerSplit = null;
    }

    internal OwnerFlight? ResumeOwner(Pending pending)
    {
        if (!kernelOwner.ownerResumes.Remove(pending, out var flight)) return null;
        kernelOwner.ownerFlights.Remove(flight.Request.CommandId);
        kernelOwner.ForgetOwnerSplit(flight);
        foreach (var pair in flight.Frames) executor.stepFrames[pair.Key] = pair.Value;
        RestoreStoredValues(flight.Work.Entry, flight.Stored);
        foreach (var pair in flight.Rows) executor.resultRows[pair.Key] = pair.Value;
        if (executor.resultArena.SlotCapacity < flight.Slots.Length) executor.resultArena.Grow(flight.Slots.Length);
        Array.Copy(flight.Slots, executor.resultArena.Slots, flight.Slots.Length);
        for (var i = 0; i < flight.Slots.Length; i++)
        {
            ref var slot = ref executor.resultArena.Slots[i];
            if (slot.Kind != ValueKind.String || slot.StringLength == 0) continue;
            var value = Encoding.UTF8.GetString(flight.Strings, slot.StringOffset, slot.StringLength);
            var entry = executor.resultArena.Strings.Add(value);
            slot.StringOffset = entry.Offset; slot.StringLength = entry.Length;
        }
        RestoreWalk(flight.Walk);
        return flight;
    }

    /// <summary>This activation's play origin, once something asked for it (<see cref="PlayOf"/>), or the one the
    /// suspended flow it resumes carried (a schedule's continuation, a wait, an owner reply).</summary>
    internal PlayOrigin activationPlay;

    internal bool activationPlayKnown;

    internal void ResumePlay(PlayOrigin origin) { activationPlay = origin; activationPlayKnown = true; }

    /// <summary>The activation's play origin, computed from the event the first time a step of this activation asks:
    /// the scope's text and the subject's ids are hashed once per activation, not once per command.</summary>
    internal PlayOrigin PlayOf(Pending pending)
    {
        if (activationPlayKnown) return activationPlay;
        const long seed = unchecked((long)14695981039346656037UL);
        var root = Fnv(seed, pending.Event.ScopeId);
        var subjects = pending.EndSubject;
        long subject;
        if (subjects.Count == 0) subject = Fnv(root, pending.Event.EventId);
        else
        {
            // The entities' own hashes are summed, so the order the event lists them in names no other play.
            long entities = 0;
            for (var i = 0; i < subjects.Count; i++)
            {
                var entity = subjects.At(i);
                entities = unchecked(entities + Fnv(Fnv(seed, entity.Id), entity.LifeEpoch));
            }
            subject = Fnv(root, entities);
        }
        ResumePlay(new PlayOrigin(root, subject));
        return activationPlay;
    }

    /// <summary>
    /// Which play one dispatch of a step that holds plays is: the activation's origin (<see cref="PlayOrigin"/>), then —
    /// outermost first — the current round of every synchronous loop it runs inside (<see cref="ResolvedStep.PlayLoops"/>).
    /// A `for_each` round over candidates is named by the candidate itself (its id and life epoch), so the play that
    /// follows one enemy stays that enemy's play when another target leaves the list or the list is ordered differently;
    /// a round with no entity (a counted `repeat`, a walk over points) is named by its index. The rounds are read from
    /// the walk's own loop frames, not from the published value frames, so nothing is parsed or looked up by name. FNV-1a
    /// over those numbers: a 64-bit identity, so two plays share one only by a 2^-64 collision. A recipient that sees the
    /// same identity twice on one tick (the same candidate listed twice, one event dispatching the step twice) tells the
    /// two apart by their order on that tick. A loop the step is inside whose frame this activation does not hold is
    /// refused rather than left out: two different rounds would otherwise be one play.
    /// </summary>
    internal long PlayIdentity(ResolvedStep step, Pending pending)
    {
        var origin = PlayOf(pending);
        var loops = step.PlayLoops;
        var hash = loops.Length == 0 ? origin.Subject : origin.Root;
        for (var i = 0; i < loops.Length; i++)
        {
            if (!TryLoopFrame(loops[i], out var frame))
                throw new RuntimeContractException("presentation-step", step.NodeId + " runs inside a loop whose round this activation does not hold.");
            if (frame.ForEach && !frame.Positions)
            {
                var item = frame.Items[frame.Round % frame.Items.GetArrayLength()];
                hash = Fnv(FnvText(hash, item.GetProperty(EntityIdUtf8)), item.GetProperty(EntityLifeUtf8).GetInt64());
            }
            else hash = Fnv(hash, frame.Round);
        }
        return hash;
    }

    /// <summary>The round one loop of the step's own region is in, from the live walk — which, in a pulse of an interval
    /// series, also holds the loops the series started inside (<see cref="ControlFrame.Held"/>).</summary>
    internal bool TryLoopFrame(int loop, out ControlFrame frame)
    {
        for (var i = controlWalk.Count - 1; i >= 0; i--)
            if (controlWalk[i].Loop && controlWalk[i].Step == loop) { frame = controlWalk[i]; return true; }
        frame = default;
        return false;
    }

    internal Dictionary<(string Provider, string Id), ScheduledJob> schedules = new();

    /// <summary>The schedules this advance ended, collected while the table is scanned and removed after it: the
    /// scan may not mutate the collection it walks.</summary>
    internal readonly List<(ScheduledJob Job, string Code)> endedSchedules = new(MaximumSchedules);

    internal int scheduledThisTick;

    internal void EndSchedule(ScheduledJob job, string status, string code)
    {
        if (!job.Handle.Active) return;
        job.Handle.Status = status; job.Handle.Code = code; job.Handle.NextTick = null;
        schedules.Remove((job.Handle.ProviderId, job.Handle.ScheduleId));
        // A group decides what an ended member or its own end means at the next safe point, never here: this may be
        // the step of the very line that ended it.
        if (job.Group is { } own) QueueGroupCheck(own);
        if (job.Owner is { } owner) { owner.Jobs.Remove(job); QueueGroupCheck(owner); }
        // A plan continuation's timer dies with its schedule, so the handle it wrote into a step frame can never
        // be read as live again — the generation is what makes that visible to a `cancel` that still names it.
        kernelEffects.ReleaseHandle(job.HandleSlot);
        // The queue still holds this job's waiting pulses; they are dropped by the next compaction, which is the
        // only thing this flag schedules.
        eventQueue.queueDirty = true;
        if (job.Module != null)
        {
            try { job.Module.End(status, code); }
            // The exception names the module's own failure; game run 4 logged only the bare code, so the cause was unrecoverable.
            catch (Exception error) { log.LogObserverFailed(job.Handle.ProviderId, "module-continuation-end-failed: " + error.GetType().Name + ": " + error.Message); }
        }
    }

    /// <summary>
    /// Drops the queue entries something has made stale: a pulse of a schedule that has ended, and a `debounce`
    /// release a later arrival moved past. Compaction is the only way such an entry stops occupying queue capacity —
    /// a cancelled timer releases its slot immediately, which is what the callers that end a schedule and then
    /// publish rely on — so it runs exactly when one of the two happened and never on a frame that had neither.
    /// </summary>
    internal void PruneQueue()
    {
        if (!eventQueue.queueDirty) return;
        eventQueue.queueDirty = false;
        // Retain the scratch storage across advances; completing a module item must not allocate a LINQ array.
        prunedQueue.Clear();
        foreach (var entry in eventQueue.queue.UnorderedItems)
            if ((entry.Element.Schedule == null || Live(entry.Element.Schedule))
                && (entry.Element.Release is not { } release
                    || kernelVariables.triggerGates.ReleaseCurrent(release.PlanId, release.NodeId, release.Subject, release.Generation)))
                prunedQueue.Add(entry);
        if (prunedQueue.Count == eventQueue.queue.Count) return;
        eventQueue.queue.Clear(); foreach (var entry in prunedQueue) eventQueue.queue.Enqueue(entry.Element, entry.Priority);
    }

    internal readonly List<(Pending Element, (long Tick, long Sequence) Priority)> prunedQueue = new(1024);

    /// <summary>Re-arms one live schedule from now: its next pulse lands one interval later, the handle keeps naming
    /// the same schedule for its whole life, and the pulse index is not reset, so no occurrence identity is ever
    /// replayed. A schedule whose pulse already fired has ended and released its slot, so a `restart` that still
    /// names it is refused there rather than here.</summary>
    internal bool RestartSchedule(ScheduledJob job)
    {
        if (!job.Handle.Active) return false;
        if (job.Group is { } group) return RestartGroup(group);
        var next = world.Tick + job.Spec.IntervalTicks;
        RuntimeJson.Integer(next);
        job.FirstTick = next - job.Index * job.Spec.IntervalTicks;
        RuntimeJson.Integer(job.FirstTick);
        job.ReArmed = true; eventQueue.queueDirty = true; PruneQueue();
        eventQueue.QueuePulse(job);
        return true;
    }

    internal ScheduleReceipt ScheduleReport(ScheduledJob job, string status, string code)
        => new(job.Handle.ProviderId, job.Handle.ScheduleId, job.Handle.WorldEpoch, world.Tick, status, code, job.Handle.DispatchedPulses, job.Handle.SkippedPulses);

    internal string? ClaimPulse(Pending pending, List<ScheduleReceipt>? reports)
    {
        if (pending.PulseClaimed) return null;
        var job = pending.Schedule!;
        var key = new LedgerKey(pending.Provider, LedgerSpace.Event, pending.Event.EventId);
        // A different work item claiming the same occurrence identity is a real conflict; only a ledger still full after
        // forgetting what the horizon allows is a budget refusal. Both stop this schedule without retrying.
        var refusal = eventQueue.history.ContainsKey(key) ? "event-id-conflict"
            : !LedgerHasRoom() ? "event-history-budget" : null;
        if (refusal != null)
        {
            EndSchedule(job, "rejected", refusal); reports?.Add(ScheduleReport(job, "rejected", refusal));
            if (refusal.EndsWith("-budget", StringComparison.Ordinal)) log.LogBudgetExceeded(pending.Event.BindingId, pending.Event.EventId, refusal);
            else log.LogEventRejected(pending.Event.BindingId, pending.Event.EventId, refusal, pending.Provider);
            return refusal;
        }
        eventQueue.history.Add(key, new LedgerRow(PulseLedgerFingerprint(pending.Event), pending.Event.SimulationTick));
        pending.PulseClaimed = true;
        return null;
    }

    internal void AdmitPulse(Pending pending, List<ScheduleReceipt>? reports)
    {
        var job = pending.Schedule!;
        RuntimeJson.Require(pending.PulseClaimed, "schedule-ledger", "An occurrence must own its ledger row before dispatch.");
        job.Index++; job.Handle.DispatchedPulses++; scheduledThisTick++;
        // A group places each of its pulses itself; the one just taken is no longer queued.
        if (job.Group is { } group) { group.Queued = false; job.Handle.NextTick = null; }
        else if (job.TotalPulses is not { } pulses || job.Index < pulses) eventQueue.QueuePulse(job);
        else job.Handle.NextTick = null;
        log.LogTriggerFired(pending.Provider, pending.Event.BindingId, pending.Event.EventId, pending.Event.CauseId,
            pending.Event.RootEventId ?? job.Template.EventId);
        reports?.Add(ScheduleReport(job, "dispatched", "pulse-dispatched"));
    }

    internal void StopScheduledSource(string? provider, string? scope, string code)
    {
        // A client advance asks every frame, and its table is empty: no closure, iterator or array for nothing.
        if (schedules.Count == 0 && !eventQueue.queueDirty) return;
        foreach (var job in schedules.Values.Where(j => (provider == null || j.Handle.ProviderId == provider) && (scope == null || j.Template.ScopeId == scope)).ToArray()) EndSchedule(job, "cancelled", code);
        PruneQueue();
    }


    /// <summary>Exchanges the state a host migration swaps with the staged image's. Nothing runs between the
    /// assignments; the image keeps the prior state for rollback.</summary>
    internal void SwapWith(KernelFlow other)
    {
        (schedules, other.schedules) = (other.schedules, schedules);
        (scheduleSequence, other.scheduleSequence) = (other.scheduleSequence, scheduleSequence);
        (scheduledThisTick, other.scheduledThisTick) = (other.scheduledThisTick, scheduledThisTick);
    }
}

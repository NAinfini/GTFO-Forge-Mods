using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>What one capture of the registered native sources is for. <see cref="Complete"/> is every source, for a
/// checkpoint, a migration or a rollback image; <see cref="Export"/> is every source for the session's complete body
/// and settles what the session stream owes. A session delta is not a capture: <see cref="RuntimeKernel.NativeStateWriteDelta"/>
/// writes it.</summary>
internal enum NativeCaptureMode { Complete, Export }

/// <summary>
/// The kernel's variable, level-object and cross-module message half. Everything here hangs off the one store and
/// the one dispatch walk: `g-var`/`g-object` read and write <see cref="RuntimeVariableStore"/>, `g-once` latches in
/// it, `g-message` publishes through <see cref="RuntimeKernel.Publish"/>, and `g-wait` suspends the walk and lets
/// the publish that wakes it re-enter the same entry.
///
/// Host authority is not a policy here, it is the dispatch path: every one of these steps runs inside an advance
/// that only the host performs, so a client never computes a value. What a client has is the snapshot the host
/// sends (<see cref="ExportVariables"/>), which is also what a late joiner is handed.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>The registered domain sources' state for one purpose (<see cref="NativeCaptureMode"/>).</summary>
    internal Func<NativeCaptureMode, JsonElement>? NativeStateCapture { get; set; }
    /// <summary>Checks a complete native image (every required source present) without applying it.</summary>
    internal Action<JsonElement>? NativeStateValidate { get; set; }
    /// <summary>Applies a complete native image atomically.</summary>
    internal Action<JsonElement>? NativeStateRestore { get; set; }
    /// <summary>Writes the session delta's native part: only the sources changed since the stream last carried them, each
    /// as its whole state or as the rows that differ from the state last sent, and settles those.</summary>
    internal Action<BinaryWriter>? NativeStateWriteDelta { get; set; }
    /// <summary>Reads a session delta's native part of the given number of sources, validates it and applies every patch to
    /// the state it was written against, changing nothing; the step it returns applies it atomically.</summary>
    internal Func<BinaryReader, int, Action>? NativeStateReadDelta { get; set; }
    /// <summary>Some source changed since the session stream last carried it, so the next delta has a native part.</summary>
    private bool _nativeStateDirty;
    internal void MarkNativeStateChanged() { ReadThread(); _nativeStateDirty = true; }
    private string WithNativeState(string json, NativeCaptureMode mode)
    {
        if (NativeStateCapture == null) return json;
        var state = NativeStateCapture(mode);
        if (state.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("native-state-object");
        if (!state.EnumerateObject().MoveNext()) return json;
        // The document is the store's own compact text in the serializer's spelling, so the native part is appended
        // as its last property instead of reading the whole document back and writing it again.
        return string.Concat(json.AsSpan(0, json.Length - 1), ",\"nativeDomains\":", JsonSerializer.Serialize(state), "}");
    }
    private static string SplitNativeState(string json, out JsonElement? state)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        if (!values.Remove("nativeDomains", out var captured)) { state = null; return json; }
        state = captured.Clone();
        return JsonSerializer.Serialize(values);
    }
    internal sealed record NativeStateRestorePlan(JsonElement State);
    private NativeStateRestorePlan? PrepareNativeState(JsonElement? state)
    {
        if (state == null) return null;
        if (NativeStateCapture == null || NativeStateRestore == null)
            throw new RuntimeContractException("native-state-source-missing", "No native state source is registered.");
        if (NativeStateValidate == null)
            throw new RuntimeContractException("native-state-validator-missing", "The native state source has no read-only validator.");
        ValidateNativeState(state.Value);
        return new NativeStateRestorePlan(state.Value.Clone());
    }
    private JsonElement CaptureNativeRestoreBaseline()
    {
        var original = NativeStateCapture!(NativeCaptureMode.Complete);
        if (original.ValueKind != JsonValueKind.Object)
            throw new RuntimeContractException("native-state-object", "Native state capture must be an object.");
        ValidateNativeState(original);
        return original.Clone();
    }
    private void ValidateNativeState(JsonElement state)
    {
        try { NativeStateValidate!(state); }
        catch (RuntimeContractException) { throw; }
        catch (Exception error)
        {
            throw new RuntimeContractException("native-state-validation-failed",
            error.GetType().Name + ":" + error.Message);
        }
    }
    private void RestoreNativeState(NativeStateRestorePlan? plan)
    {
        if (plan == null) return;
        // A migration's WorldChanged observers replace native instances after preflight. Capture the rollback
        // image at the actual commit boundary; an image of the previous world cannot restore these instances.
        var original = CaptureNativeRestoreBaseline();
        try { NativeStateRestore!(plan.State); }
        catch (Exception error)
        {
            try { NativeStateRestore!(original); }
            catch (Exception rollback)
            {
                throw new RuntimeContractException("native-state-rollback-failed",
                "Native restore failed (" + error.GetType().Name + ":" + error.Message + ") and rollback failed ("
                + rollback.GetType().Name + ":" + rollback.Message + ").");
            }
            if (error is RuntimeContractException) throw;
            throw new RuntimeContractException("native-state-restore-failed", error.GetType().Name + ":" + error.Message);
        }
    }
    /// <summary>One suspended `g-wait`. It carries the work item and step to re-enter, the two outputs it may leave
    /// by, the region it suspended, and its own handle so a `cancel` on that handle ends it. <see cref="HandleSlot"/>
    /// is the pool slot that handle occupies: the wait's own frame publishes no handle, so nothing in a plan can
    /// name it, and the slot is given back the moment the wait leaves by either exit — a wait that resumed without
    /// returning its slot would spend one of the world's handles per activation for as long as the world lasts.
    /// <see cref="Resolved"/> is which outcome won, and <see cref="Retired"/> is whether the wait has already left
    /// the table and returned its slot: the two are separate because the event that wakes a wait takes it out of the
    /// table immediately — it must not be woken twice — while its handle stays live until the resumption that reads
    /// it, and because either terminal path may be the one that finds the other has already been there.</summary>
    internal sealed class WaitJob
    {
        internal RuntimeModuleHandle Handle = null!;
        internal Work Item = null!;
        internal int Step;
        internal int ReceivedOutput;
        internal int TimeoutOutput;
        internal string Target = "";
        /// <summary>The scope the flow that entered this wait was running in, as its own event reported it. A scope
        /// cancellation is the source's own fact, and a wait belongs to the source that started it: this is the
        /// identity that retirement matches on, so an endless wait cannot outlive the scope or provider it came
        /// from.</summary>
        internal string Scope = "";
        internal string? Message;
        internal JsonElement HandleValue = RuntimeJson.EmptyObject;
        internal int HandleSlot = -1;
        /// <summary>The enclosing control region this wait suspended, outermost first, so the exit it leaves by
        /// continues the flow it interrupted (see <see cref="Continuation"/>).</summary>
        internal IReadOnlyList<ControlFrame> Walk = Array.Empty<ControlFrame>();
        /// <summary>The values, action frames and result rows the suspended flow had when it entered the wait, put
        /// back by the resumption the same way a `delay`'s continuation puts them back.</summary>
        internal FlowSnapshot Flow = null!;
        /// <summary>The play origin of the flow that entered the wait: the plays after it are the plays of that flow.</summary>
        internal PlayOrigin Play;
        /// <summary>Set by whichever of the two outcomes happened first; the other pending is then skipped.</summary>
        internal bool Resolved;
        /// <summary>Set by <see cref="RetireWait"/>, which is the only writer: a wait leaves the table and gives its
        /// slot back exactly once, however many terminal paths reach it.</summary>
        internal bool Retired;
        internal int ResumeOutput = -1;
        /// <summary>The action group this wait is a line of. <see cref="Resuming"/> holds it in the group from the
        /// moment it is admitted until the line it resumes has been walked, so the group is not done in between.</summary>
        internal FlowGroup? Owner;
        internal bool Resuming;
    }

    /// <summary>Forgets a plan's declarations and its module-scope values. Values of the other scopes stay: they
    /// belong to the world, not to the file that declared them. A wait the plan left suspended retires with it — its
    /// place in the wait budget, its binding's subscription gate and its handle slot all go back — because nothing
    /// will ever resume a wait whose plan is gone, and a pending already queued for it is dropped when it arrives.</summary>
    private void ForgetVariables(string planId)
    {
        kernelVariables.variables.ForgetPlan(planId);
        kernelVariables.triggerGates.ForgetPlan(planId);
        kernelVariables.variables.ClearModuleScope(planId);
        foreach (var key in kernelVariables.waits.Keys.ToArray())
            foreach (var job in kernelVariables.waits[key].ToArray())
                if (job.Item.Plan.Plan.Id == planId) RetireWait(job);
    }

    /// <summary>Every value and latch the world holds, dropped when the world ends. Declarations stay: the plans
    /// are still loaded and re-declare the same names in the next world. The gate store is begun with the world's
    /// die seed for the same reason: its counters belong to the world, and the seed is drawn once per world.</summary>
    private void BeginVariableWorld(long gateSeed)
    {
        _nativeStateDirty = false;
        kernelVariables.variables.ClearValues();
        kernelVariables.triggerGates.BeginWorld(gateSeed);
        kernelVariables.waits.Clear();
        RefreshSubscriptionGates();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Control steps
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Whether one control capability id is dispatched here rather than by the first-step walker.</summary>
    private static bool IsVariableControl(string? control) => control is
        VariableContracts.ReadCapability or VariableContracts.WriteCapability or VariableContracts.OnceCapability
        or VariableContracts.WaitCapability or VariableContracts.EmitCapability;

    /// <summary>
    /// One variable, object, latch, message or wait step. A null return leaves the walk where the step sent it: a
    /// value step continues at `next`, a `once` at `first` or `later`, and a `g-wait` suspends it until the event
    /// it named arrives or its deadline passes. A branch with no successor ends naturally.
    /// </summary>
    private string? EnterVariableControl(Work item, int stepIndex, ResolvedStep step, Pending pending,
        StepInputs inputs, JsonElement parameters, out int? cursor)
    {
        cursor = null;
        switch (step.Control)
        {
            case VariableContracts.OnceCapability:
                {
                    // The latch is keyed by the card: this plan and this node, whichever entrypoint the walk came in
                    // by. A re-load of the same behaviour therefore reaches the same latch, and the checkpoint and the
                    // migration image restore it with the values. The key's one spelling lives in the store that owns
                    // the latch set, which is also the only place that reads it back when a plan unloads.
                    var key = RuntimeVariableStore.LatchKeyOf(item.Plan.Plan.Id, step.NodeId);
                    cursor = Successor(step, kernelVariables.variables.ClaimOnce(key) ? 0 : 1);
                    return null;
                }
            case VariableContracts.EmitCapability:
                {
                    var message = RuntimeJson.Text(parameters, "message");
                    RuntimeJson.Require(MessageName(message), "message-name", message);
                    // A message whose one value the sender did not wire is a message that carries no value, which is
                    // what the receive row's nullable port already means; the absent input is published as the null it
                    // stands for rather than handed on as the undefined value a missing property leaves behind.
                    var payload = TryControlInput(step, inputs, ControlPort.Value, out var raw) ? raw : NullValue;
                    PublishMessage(message, payload, pending.Event.ScopeId);
                    cursor = Successor(step, 0);
                    return null;
                }
            case VariableContracts.WaitCapability:
                return EnterWait(item, stepIndex, step, pending, parameters, out cursor);
            default:
                return RuntimeAbiCodes.ControlUnsupported;
        }
    }

    internal static readonly JsonElement NullValue = RuntimeJson.Parse("null");

    private static JsonElement Value(ResolvedStep step, StepInputs inputs, ControlPort port, VariableDeclaration declaration)
    {
        var value = TryControlInput(step, inputs, port, out var raw) ? raw : default;
        if (!(value.ValueKind != JsonValueKind.Undefined)) throw new RuntimeContractException("variable-value", declaration.Name + "." + ControlPortNames[(int)port]);
        return value;
    }

    private static readonly JsonElement WrittenTrue = RuntimeJson.Parse("true"), WrittenFalse = RuntimeJson.Parse("false");

    private static JsonElement StoredControlFrame(ResolvedStep step)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var ports = RuntimeJson.Rows(step.Contract, "outputs");
        for (var i = 0; i < ports.Length; i++)
            if (step.StoredOutputs![i].ValueKind != JsonValueKind.Undefined)
                fields.Add(RuntimeJson.Text(ports[i], "id"), step.StoredOutputs[i]);
        return RuntimeJson.From(fields);
    }

    /// <summary>Moves stored values positionally. Declarations, input/output slots and buffers were resolved at
    /// load; the only dynamic address part is the complete host accepted by the attachment matcher.</summary>
    private string? EnterStoredValueControl(Work item, ResolvedStep step, StepInputs inputs, string scopeId, out int? cursor)
    {
        var outputs = step.StoredOutputs!;
        var resolved = step.StoredResolved!;
        Array.Clear(outputs, 0, outputs.Length);
        Array.Clear(resolved, 0, resolved.Length);
        step.OutputActivation = kernelFlow.valueActivation;
        if (step.Variable == null)
        {
            // A present control forwards the compiled value. The handler's enum member text is only
            // a view; publishing it would fail the next input's member-index validation.
            var value = inputs.InputAt(step.VariableInput, wire: true);
            if (value.ValueKind == JsonValueKind.Undefined) value = NullValue;
            outputs[step.ValueOutput] = value;
            // A forwarded resource keeps the owner's answer it arrived with.
            resolved[step.ValueOutput] = inputs.ResolvedAt(step.VariableInput);
            cursor = Successor(step, value.ValueKind == JsonValueKind.Null ? 1 : 0);
            return null;
        }
        var declaration = step.Variable;
        var scope = Scope(item, step, inputs);
        var previous = kernelVariables.variables.Read(declaration, scope, out var previousResolved);
        if (step.Control == VariableContracts.ReadCapability)
        {
            // A resource the address holds is resolved against its kind's owner once, when it is first read after it
            // was written, restored or declared; the cell keeps the answer. An owner that no longer has the resource
            // is the explicit stale-resource refusal of this step — the walk stops there, no other resource is
            // substituted and no default is used. (A write never needs the old choice to be valid.)
            if (declaration.ResourceKind != null && previous.ValueKind != JsonValueKind.Null && previousResolved == null)
            {
                previousResolved = executor.ResolveCompiledResource(declaration.ResourceKind, RuntimeJson.Text(previous, "resourceId"))
                    ?? throw new RuntimeContractException(RuntimeAbiCodes.StaleResource, declaration.Name);
                kernelVariables.variables.Remember(declaration, scope, previousResolved);
            }
            outputs[step.ValueOutput] = previous;
            resolved[step.ValueOutput] = previousResolved;
        }
        else
        {
            var value = inputs.InputAt(step.VariableInput);
            RuntimeVariableContract.Validate(value, declaration.Type, declaration.Name);
            var written = inputs.ResolvedAt(step.VariableInput);
            // A written reference arrives resolved by whichever step produced it (a literal at load, a wired port when
            // it was read); only a value that somehow arrives without that answer is resolved here.
            if (declaration.ResourceKind != null && value.ValueKind != JsonValueKind.Null && written == null)
                written = executor.ResolveCompiledResource(declaration.ResourceKind, RuntimeJson.Text(value, "resourceId"))
                    ?? throw new RuntimeContractException(RuntimeAbiCodes.StaleResource, declaration.Name);
            outputs[step.PreviousOutput] = previous;
            resolved[step.PreviousOutput] = previousResolved;
            var changed = kernelVariables.variables.Set(declaration, scope, value, written);
            outputs[step.WrittenOutput] = changed ? WrittenTrue : WrittenFalse;
            // A first write of the declared initial value opens an address but changes nothing a reader sees.
            if (changed && !kernelVariables.variables.Same(previous, value)) PublishVariableChanged(declaration.Name, scopeId, Owner(item, step, inputs));
        }
        cursor = Successor(step, 0);
        return null;
    }

    /// <summary>
    /// Which address a declaration is read at. The scope kind decides the subject, and a scope whose subject the
    /// dispatch cannot supply is refused by name instead of being keyed under an invented one: a level variable has
    /// no subject, a module variable belongs to the plan and full host identity, and every remaining
    /// scope takes the step's `subject` input or the single entity the plan's mount accepted for this event.
    /// </summary>
    private VariableScopeAddress Scope(Work item, ResolvedStep step, StepInputs inputs)
    {
        var declaration = step.Variable!;
        switch (declaration.Scope)
        {
            case VariableScopeKinds.Level: return new(VariableScopeKinds.Level, "", -1);
            case VariableScopeKinds.Module:
                RuntimeJson.Require(item.Subjects.Count <= 1, "variable-subject", declaration.Name);
                return VariableScopeAddress.Module(item.Plan.Plan.Id, item.Subjects.Count == 1 ? item.Subjects[0] : null);
            case VariableScopeKinds.Weapon:
                {
                    var raw = inputs.InputAt(step.WeaponSlotInput);
                    var slot = raw.ValueKind == JsonValueKind.Number ? (int)RuntimeJson.Integer(raw, 0, 64) : -1;
                    RuntimeJson.Require(slot >= 0, "variable-subject", declaration.Name);
                    return new(VariableScopeKinds.Weapon, Subject(item, step, inputs).Id, slot);
                }
            default:
                return new(declaration.Scope, Subject(item, step, inputs).Id, -1);
        }
    }

    /// <summary>The entity a written variable belongs to: nobody for a level variable, the mount's entity
    /// for a module variable that has one, otherwise the address's own subject.</summary>
    private static EntityReference? Owner(Work item, ResolvedStep step, StepInputs inputs) => step.Variable!.Scope switch
    {
        VariableScopeKinds.Level => null,
        VariableScopeKinds.Module => item.Subjects.Count == 1 ? item.Subjects[0] : null,
        _ => Subject(item, step, inputs)
    };

    private static EntityReference Subject(Work item, ResolvedStep step, StepInputs inputs)
    {
        var value = inputs.InputAt(step.SubjectInput);
        if (value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            return RuntimeJson.Entity(value);
        RuntimeJson.Require(item.Subjects.Count == 1, "variable-subject", step.Variable!.Name);
        return item.Subjects[0];
    }

    // ---------------------------------------------------------------------------------------------------------
    // Messages
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>A message name is one identifier, so the sender and the receiver spell it the same way.</summary>
    internal static bool MessageName(string message) => VariableContracts.IsMessageName(message);

    /// <summary>
    /// Sends one custom message. It is an ordinary event on the one queue: it is dispatched to every plan mounted
    /// on `forge.trigger.status.event_received`, it wakes the `g-wait` steps that named it, and it is refused by
    /// the same budgets as any other event. The kernel is its publisher, which is the provider that owns the row.
    /// </summary>
    internal string PublishMessage(string message, JsonElement value, string scopeId, bool requireQueued = false)
    {
        RuntimeJson.Require(registry.Modules.TryGetValue(VariableContracts.ProviderId, out var generation) && registry.IsRegistered(VariableContracts.ProviderId, generation),
            "module-unregistered", VariableContracts.ProviderId);
        var handle = new RuntimeModuleHandle(this, VariableContracts.ProviderId, generation);
        var eventId = "message:" + WorldEpoch + ":" + (++eventQueue.sequence);
        var gate = BeginOwnEvent(handle, VariableContracts.MessageReceivedBinding, out var values);
        values.Text(Blocks.TriggerStatusEventReceived.OutMessage, message);
        values.Json(Blocks.TriggerStatusEventReceived.OutValue, value.Clone());
        var dispatch = Raise(handle, gate, eventId, WorldEpoch, CurrentTick, scopeId, null, null, 0);
        RuntimeJson.Require(dispatch.Status is "queued" or "ignored", "message-refused", dispatch.Code);
        RuntimeJson.Require(!requireQueued || dispatch.Status == "queued", "runtime-test-message-unclaimed", dispatch.Code);
        return eventId;
    }

    /// <summary>
    /// Announces that a write changed one variable's value. It is an ordinary event on the one queue, dispatched to the
    /// entries on `forge.trigger.status.variable_changed` that named this variable — the same name index a custom
    /// message uses — so a sign that shows the value refreshes through an ordinary trigger rather than a second
    /// reactive channel. Nothing is published while no loaded plan listens.
    /// </summary>
    internal string? PublishVariableChanged(string name, string scopeId, EntityReference? owner = null)
    {
        if (!Consumes(VariableContracts.VariableChangedBinding)) return null;
        RuntimeJson.Require(registry.Modules.TryGetValue(VariableContracts.ProviderId, out var generation) && registry.IsRegistered(VariableContracts.ProviderId, generation),
            "module-unregistered", VariableContracts.ProviderId);
        var handle = new RuntimeModuleHandle(this, VariableContracts.ProviderId, generation);
        var eventId = "variable:" + WorldEpoch + ":" + (++eventQueue.sequence);
        var gate = BeginOwnEvent(handle, VariableContracts.VariableChangedBinding, out var values);
        values.Text(Blocks.TriggerStatusVariableChanged.OutName, name);
        if (owner != null) values.Entity(Blocks.TriggerStatusVariableChanged.OutOwner, owner);
        var dispatch = Raise(handle, gate, eventId, WorldEpoch, CurrentTick, scopeId, null, null, 0);
        RuntimeJson.Require(dispatch.Status is "queued" or "ignored", "variable-changed-refused", dispatch.Code);
        return eventId;
    }

    // ---------------------------------------------------------------------------------------------------------
    // g-wait
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Suspends the walk until the named message or engine event arrives, or until the deadline passes. The step
    /// leaves by `received` or by `timeout`; both outcomes re-enter the same entrypoint at the same step, so the
    /// flow after a wait is one path and the two exits are what tells them apart. A deadline of zero waits forever.
    /// The region the step sits in travels with the wait for the same reason a `delay`'s does: the exit it leaves by
    /// continues the flow it interrupted, so a sequence's next exit and a loop's next round wait for it too.
    /// </summary>
    private string? EnterWait(Work item, int stepIndex, ResolvedStep step, Pending pending, JsonElement parameters, out int? cursor)
    {
        cursor = null;
        var target = RuntimeJson.Text(parameters, "target");
        RuntimeJson.Require(registry.Tables.IsWaitable(target), "wait-event-unsupported", target);
        var message = parameters.TryGetProperty("message", out var raw) && raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
        if (VariableContracts.IsMessageEvent(target))
        {
            RuntimeJson.Require(message != null && MessageName(message), "wait-message-missing", step.NodeId);
            // A client receives the host's messages, but it does not suspend its own walk on one: only the host
            // dispatches, and only the host's wait is the one the message resumes.
        }
        var timeout = parameters.TryGetProperty("timeout", out var limit) && limit.ValueKind == JsonValueKind.Number
            ? RuntimeJson.Integer(limit, 1) : 0;
        RequirePlanSuspensionRoom(item.Plan, step.NodeId);
        RuntimeJson.Require(Successor(step, 0) is not null, RuntimeAbiCodes.ControlShape, step.NodeId);
        var owner = kernelFlow.CurrentGroup();
        RequireOpenGroup(owner, step.NodeId);
        // The deadline is counted from the tick the wait really starts in — this advance's — not from the tick of the
        // event that carried the flow here: a wait re-entered under the event that started a longer flow would
        // otherwise count its own window from a tick already past and time out on the spot. It is settled before the
        // handle is cast, so nothing that can refuse this wait is left for after its slot is taken.
        var deadline = timeout > 0 ? CurrentTick + timeout : 0;
        if (timeout > 0) RuntimeJson.Integer(deadline);
        var walk = kernelFlow.EnclosingWalk();
        var handleValue = kernelEffects.CreateHandle(pending.Provider, "subscription", "encounter", out var slot, item.Plan.Plan.Id + ".wait." + stepIndex);
        var job = new WaitJob
        {
            Handle = new RuntimeModuleHandle(this, pending.Provider, pending.Generation),
            Item = item,
            Step = stepIndex,
            ReceivedOutput = 0,
            TimeoutOutput = 1,
            Target = target,
            Message = message,
            HandleValue = handleValue,
            HandleSlot = slot,
            Walk = walk,
            Flow = kernelFlow.CaptureFlow(item.Entry),
            Play = kernelFlow.PlayOf(pending),
            // The scope the flow was running in. It is what a scope cancellation matches on, and it is read here
            // because the event that started the flow is the only thing that ever knows it: a wait with no deadline
            // is never reached by a pending again, so nothing later could discover it.
            Scope = pending.Event.ScopeId
        };
        if (!kernelVariables.waits.TryGetValue(target, out var list)) kernelVariables.waits.Add(target, list = new List<WaitJob>());
        list.Add(job);
        JoinGroup(owner, job);
        // A waiting flow is a consumer of the binding it named, so a publisher's gate is open from here until the
        // wait leaves: the message it waits for is exactly the publish the gate must not swallow.
        RefreshSubscriptionGates();
        executor.stepFrames[stepIndex] = RuntimeJson.From(new { message = message ?? "", payload = NullValue });
        if (timeout > 0)
        {
            // The deadline is a dispatch of its own, queued like any other pending: the walk resumes from it only
            // if no event woke the wait first.
            eventQueue.queue.Enqueue(new Pending(pending.Provider, pending.Generation, pending.Event, new[] { item }, null, ++eventQueue.sequence, false, job, true),
                (deadline, eventQueue.sequence));
        }
        // The wait owns the rest of the path: the region it suspended is the continuation's to walk, so the control
        // that encloses it is not advanced while the wait is outstanding.
        kernelFlow.walkSuspended = true;
        return null;
    }

    /// <summary>
    /// Takes one wait out of the table it was registered in, without touching its handle: the event that wakes a
    /// wait stops it being wakeable a second time, while the resumption that follows still needs the live handle the
    /// liveness check reads. Removing the last job of a binding also drops the key and closes the subscription gate
    /// that wait was holding open, so a binding nothing waits on any more stops reporting a subscriber.
    /// </summary>
    private void DetachWait(WaitJob job)
    {
        if (!kernelVariables.waits.TryGetValue(job.Target, out var list)) return;
        list.Remove(job);
        if (list.Count == 0) { kernelVariables.waits.Remove(job.Target); RefreshSubscriptionGates(); }
    }

    /// <summary>
    /// Retires one wait: it leaves the table and gives its handle slot back, exactly once. Every terminal path ends
    /// here — the deadline that expired, the event that woke it, a plan that unloaded, and a pending nothing can
    /// resume any more — so a wait that has left holds no place in the wait budget, keeps no binding reporting a
    /// subscriber and spends no handle for the rest of the world. The guard is what makes the timeout/event race
    /// safe: whichever path retires first is the one that releases the slot, and the other one is a no-op.
    /// </summary>
    private void RetireWait(WaitJob job)
    {
        if (job.Retired) return;
        job.Retired = true;
        DetachWait(job);
        if (job.Owner is { } owner && !job.Resuming) { owner.Waits.Remove(job); kernelFlow.QueueGroupCheck(owner); }
        // Only the slot this wait's own handle still names is released. The index alone is not an identity: a slot
        // this wait used to own can be freed — by its provider unregistering, which sweeps that provider's slots —
        // and cast again by another provider while this job is still queued, and clearing it then would end a
        // handle this wait never held.
        if (kernelEffects.SlotHoldsWaitHandle(job)) kernelEffects.ReleaseHandle(job.HandleSlot);
        job.HandleSlot = -1;
    }

    /// <summary>
    /// Admits or drops one pending that carries a suspended wait. A pending whose wait already left by the other
    /// exit, or whose handle was cancelled, is dropped without a receipt: it is the timer of a step that no longer
    /// exists, not an event anyone published.
    ///
    /// Whichever outcome is admitted here owns the wait's resources from that point: the table place and the handle
    /// slot go back <em>before</em> the caller can still drop the pending, because everything after this call — a
    /// cancelled scope, an unloaded plan, a vanished provider, an exhausted budget — is an early exit that would
    /// otherwise leave the slot occupied for the rest of the world. The loser of the event/timeout race never
    /// retires: the winner already did, and the guard makes the second attempt a no-op rather than a double release.
    /// </summary>
    private bool AdmitWait(Pending pending)
    {
        var job = pending.Wait!;
        if (pending.WaitTimeout)
        {
            // The deadline lost the race when the event already woke this wait: that pending is the winner and owns
            // the retirement, so this one leaves the table alone and only drops itself.
            if (job.Resolved) return false;
            job.Resolved = true; job.ResumeOutput = job.TimeoutOutput;
        }
        else job.ResumeOutput = job.ReceivedOutput;
        // A wait nothing can resume any more retires exactly like one that leaves by an exit: the slot goes back,
        // the table place goes back, and the pending itself is dropped without a receipt.
        var live = job.Handle.IsRegistered && RuntimeHandle.TryRead(job.HandleValue, out var waited) && IsHandleLive(job.Handle, waited);
        // Admitted, the wait stays its group's line until the dispatch that resumes it is walked.
        job.Resuming = live && job.Owner != null && !job.Retired;
        RetireWait(job);
        return live;
    }

    /// <summary>
    /// Retires every wait that belongs to one source: all of them where <paramref name="provider"/> is null, and
    /// otherwise the waits that provider started, narrowed to one scope where <paramref name="scope"/> is given.
    /// A wait has no clock of its own when its card declared no deadline, so nothing else would ever reach it: this
    /// is what a scope cancellation and a module unregistration call, so an endless wait cannot hold a budget place,
    /// a subscription gate and a handle for the rest of the world.
    /// </summary>
    private void StopWaitSource(string? provider, string? scope)
    {
        foreach (var key in kernelVariables.waits.Keys.ToArray())
            foreach (var job in kernelVariables.waits[key].ToArray())
                if ((provider == null || job.Handle.ProviderId == provider) && (scope == null || job.Scope == scope))
                    RetireWait(job);
    }

    /// <summary>Re-enters the entry at the wait step: the frame carries what the wait received, so the steps after
    /// it read the message and its payload as the wait node's own outputs. The region the wait suspended is put back
    /// first, so the exit it leaves by runs inside the flow that was interrupted, and the wait retires as it leaves:
    /// its table place and its handle slot are given back, and nothing can name a handle whose step publishes no
    /// handle port.</summary>
    private int? ResumeWait(ResolvedEntry entry, WaitJob job, Pending pending)
    {
        kernelFlow.BeginActivation();
        kernelFlow.RestoreFlow(entry, job.Flow);
        kernelFlow.RestoreWalk(job.Walk);
        RetireWait(job);
        var cells = executor.CellsOf(pending.Event);
        var binding = pending.Event.CellBinding;
        var carried = binding != null && binding.MessageValueCell >= 0 && cells[binding.MessageValueCell].Carried;
        executor.stepFrames[job.Step] = RuntimeJson.From(new
        {
            message = binding != null && binding.MessageCell >= 0 ? CellText(cells[binding.MessageCell]) ?? "" : "",
            payload = carried ? executor.EventCellJson(pending.Event, binding!.MessageValueCell) : NullValue
        });
        return Successor(entry.Steps[job.Step], job.ResumeOutput);
    }

    /// <summary>
    /// Wakes every suspended wait that named this event. Called by the publish that accepted it, before the tick
    /// that accepted it ends, so a message resumes its waiter in the same advance it was sent in. The waiting work
    /// item is enqueued with the received event as its own event: the resumed steps read the event's frame. The wait
    /// leaves the table here — it must not be woken twice — while its handle stays live for the resumption, which is
    /// what retires it.
    /// </summary>
    private void WakeWaiters(RuntimeEvent value)
    {
        if (kernelVariables.waits.Count == 0 || !kernelVariables.waits.TryGetValue(value.BindingId, out var list)) return;
        foreach (var job in list.ToArray())
        {
            if (job.Resolved) continue;
            if (job.Message != null && (executor.CellsOf(value).Length == 0 || value.CellBinding is not { MessageCell: >= 0 } binding
                || CellText(value.Cells![binding.MessageCell]) != job.Message)) continue;
            job.Resolved = true; job.ResumeOutput = job.ReceivedOutput;
            DetachWait(job);
            eventQueue.queue.Enqueue(new Pending(job.Handle.ProviderId, job.Handle.Generation, value, new[] { job.Item }, null, ++eventQueue.sequence,
                true, job), (value.SimulationTick, eventQueue.sequence));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Host-side state: clients, late joiners, checkpoints and host migration
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The host's whole replicable state in one document: every variable value, every `g-once` latch and the trigger
    /// gate rows with their die seed. It is what a late joiner and a reconnecting player are handed, and it is the
    /// same document a checkpoint carries, because both answer the one question "what does this world hold now".
    /// <para>
    /// The gate rows travel here and nowhere else: a client never evaluates a gate, so what it needs is the state
    /// the host's decisions produced, not the counters' own arithmetic. Sending them makes the reconnecting player's
    /// window continue instead of restarting — a half-reached threshold stays half reached.
    /// </para>
    /// <para>
    /// The document carries every value the world holds, so framing it is what settles the touch set: the addresses
    /// it contains are not owed to anyone any more. A write made after this call is not in the document and stays
    /// owed, which is what lets the body that follows a document too large for one tick be one delta instead of the
    /// whole document again.
    /// </para>
    /// </summary>
    internal string ExportVariables()
    {
        ReadThread();
        var snapshot = WithNativeState(kernelVariables.variables.Save(WorldEpoch, kernelVariables.triggerGates.Save()), NativeCaptureMode.Export);
        _nativeStateDirty = false;
        kernelVariables.variables.Settle();
        return snapshot;
    }

    /// <summary>
    /// The entries the world wrote and the addresses it removed that no body has carried yet, for the delta a client
    /// applies between snapshots. The touch set is what is still owed rather than what this advance did: reading it
    /// does not consume it, and the export that framed the delta is what settles it. A change made while a complete
    /// document was being handed over is therefore still in it, and travels as one delta once that document has gone.
    /// <para>The delta is binary (<see cref="RuntimeVariableStore.WriteDelta"/>), then a flag byte and, when it is 1, the
    /// native part (<see cref="NativeStateWriteDelta"/>). The world it belongs to travels in the body's header.</para>
    /// </summary>
    internal void ExportVariableDelta(BinaryWriter writer)
    {
        ReadThread();
        kernelVariables.variables.WriteDelta(writer);
        var native = _nativeStateDirty && NativeStateWriteDelta != null;
        writer.Write(native);
        if (native) NativeStateWriteDelta!(writer);
        _nativeStateDirty = false;
        kernelVariables.variables.Settle();
    }

    /// <summary>Whether anything is still owed to the session: a write or a removal no body has carried. A host
    /// sends no body for a tick that owes nothing, which is what keeps an idle level's bandwidth at zero instead of
    /// one message per tick; a change the last document could not contain keeps this true until a delta carries it.
    /// </summary>
    internal bool VariablesPending => kernelVariables.variables.Dirty || _nativeStateDirty;

    /// <summary>
    /// Applies the host's whole state on a client. Values, latches and gate rows are the host's; a client never
    /// computes one, and an entry the document omits is gone — which is how a cleared object or a dead enemy
    /// propagates. Unlike a checkpoint this drops nothing: the client is mirroring the world the host is in, so
    /// every row the host holds is a row the client holds too.
    /// </summary>
    internal void ApplyVariables(string json)
    {
        Mutable();
        var document = RuntimeJson.Parse(json);
        RuntimeJson.Shape(document, "worldEpoch entries once gates", "nativeDomains");
        RuntimeJson.Require(RuntimeJson.Integer(document.GetProperty("worldEpoch")) == WorldEpoch, "variable-snapshot", "Snapshot belongs to another world.");
        var core = SplitNativeState(json, out var state);
        var preparedVariables = kernelVariables.variables.Fork(); preparedVariables.Restore(core);
        var preparedGates = kernelVariables.NewTriggerGateStore(); preparedGates.Restore(document.GetProperty("gates").GetRawText());
        var preparedNative = PrepareNativeState(state);
        RestoreNativeState(preparedNative);
        kernelVariables.variables = preparedVariables; kernelVariables.triggerGates = preparedGates;
    }

    /// <summary>Applies one advance's delta over the client's state. Only the values travel: the latches and the gate
    /// rows are the level's progress and arrive with the next complete body rather than with every tick.</summary>
    internal void ApplyVariableDelta(long worldEpoch, ArraySegment<byte> body)
    {
        Mutable();
        RuntimeJson.Require(worldEpoch == WorldEpoch, "variable-snapshot", "Delta belongs to another world.");
        // The body is read once and applied in place: everything is validated before the native part is restored,
        // and the values are written last, by a step that cannot be refused.
        using var reader = new BinaryReader(new MemoryStream(body.Array!, body.Offset, body.Count, false), RuntimeVariableStore.Text);
        RuntimeVariableStore.PreparedDelta prepared;
        Action? restoreNative = null;
        try
        {
            prepared = kernelVariables.variables.PrepareDelta(reader);
            if (reader.ReadBoolean())
            {
                var sources = reader.Read7BitEncodedInt();
                if (NativeStateReadDelta == null)
                    throw new RuntimeContractException("native-state-source-missing", "No native state source is registered.");
                restoreNative = NativeStateReadDelta(reader, sources);
            }
            RuntimeJson.Require(reader.BaseStream.Position == reader.BaseStream.Length, "format-invalid", "Bytes are left after the delta.");
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException)
        { throw new RuntimeContractException("format-invalid", error.GetType().Name + ":" + error.Message); }
        // A delta names only the sources that changed; the restore captures and rolls back exactly those.
        if (restoreNative != null)
            try { restoreNative(); }
            catch (RuntimeContractException) { throw; }
            catch (Exception error) { throw new RuntimeContractException("native-state-restore-failed", error.GetType().Name + ":" + error.Message); }
        kernelVariables.variables.ApplyDelta(prepared);
    }

    /// <summary>What a checkpoint has to carry: every value and every `g-once` latch of the world being saved, and
    /// the trigger options' own counters — a threshold half reached is progress the level made.</summary>
    internal string CaptureCheckpoint()
    {
        ReadThread();
        return kernelScheduler.CaptureDeferredCheckpoint(WithNativeState(kernelVariables.variables.Save(WorldEpoch, kernelVariables.triggerGates.Save()), NativeCaptureMode.Complete));
    }

    /// <summary>How many effect instances this host is currently holding: the counters a caller reporting what a
    /// checkpoint does not carry — a duration is re-derived from the card — and the readable window onto an effect a
    /// test or a diagnostic asks about when its module registered no callback worth counting.</summary>
    public int LiveEffects { get { ReadThread(); return kernelEffects.effects.Count; } }

    /// <summary>How many gate rows this host holds, and how many booked sets inside them. A caller that wants to
    /// know what a checkpoint carries without reading it.</summary>
    public (int Gates, int Scopes) GateStateCounts() => (kernelVariables.triggerGates.Count, kernelVariables.triggerGates.ScopeRows);

    /// <summary>What one gated entry point has fired and accumulated for one scope subject, or null where the plan
    /// declares no gate for it. <paramref name="subject"/> is the entity id a per-player or per-instance gate is
    /// booked against, and the empty string — the default — is the level's own set. The one readable window onto
    /// the gate state: an author asking why a card is silent reads these.</summary>
    public (long Fired, long Accumulated)? GateStateOf(string planId, string nodeId, string subject = "")
    {
        ArgumentNullException.ThrowIfNull(planId); ArgumentNullException.ThrowIfNull(nodeId); ArgumentNullException.ThrowIfNull(subject);
        ReadThread();
        return kernelVariables.triggerGates.StateOf(planId, nodeId, subject);
    }

    /// <summary>
    /// Restores a checkpoint's values and latches over the plans that are loaded. Enemy-scoped values are dropped
    /// rather than restored: reloading a checkpoint re-creates the level's enemies, so a saved per-enemy value names
    /// an entity that no longer exists, while a door is the same object the checkpoint was
    /// taken in. A checkpoint whose world epoch is not this world's is refused by name.
    ///
    /// The restore is announced as its own lifecycle boundary once it has happened: the epoch does not move — the
    /// same expedition continues — but the level's own objects are rebuilt from the game's data, so a package
    /// holding something derived from an instance has to give it up without waiting for a world change that will
    /// never come. For the same reason the event identities the world admitted before the restore are forgotten
    /// (<see cref="EventQueue.ForgetEvents"/>): an identity is unique inside a world between two restores.
    /// </summary>
    internal void RestoreCheckpoint(string json)
    {
        Mutable();
        var checkpoint = RuntimeJson.Parse(json);
        RuntimeJson.Shape(checkpoint, "worldEpoch entries once deferredResults", "gates nativeDomains");
        RuntimeJson.Require(RuntimeJson.Integer(checkpoint.GetProperty("worldEpoch")) == WorldEpoch, "variable-checkpoint",
            "Checkpoint belongs to another world.");
        var deferred = ReadDeferredResults(checkpoint.GetProperty("deferredResults"), WorldEpoch, WorldEpoch);
        var core = SplitNativeState(DeferredCheckpointCore(json), out var state);
        var preparedVariables = kernelVariables.variables.Fork(); preparedVariables.Restore(core);
        preparedVariables.ClearScope(VariableScopeKinds.Enemy);
        // The gate rows are restored after the values, and only where the loaded plans still declare them: a reload
        // of a checkpoint re-arms the same plans, so the counters come back to the entry points that own them.
        var preparedGates = kernelVariables.NewTriggerGateStore();
        preparedGates.Restore(checkpoint.TryGetProperty("gates", out var gates) ? gates.GetRawText() : "null");
        // The level's own objects were rebuilt by the restore, so a per-instance counter names an entity that is
        // gone: those sets are dropped after the restore rather than carried back with the checkpoint.
        var instanceNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loaded in plans.Loaded.Values)
            foreach (var entry in loaded.Plan.Entries)
                if (entry.Gate?.Scope == TriggerGateContract.InstanceScope) instanceNodes.Add(entry.NodeId);
        preparedGates.PruneInstances(instanceNodes);
        var preparedNative = PrepareNativeState(state);
        RestoreNativeState(preparedNative);
        kernelVariables.variables = preparedVariables; kernelVariables.triggerGates = preparedGates;
        eventQueue.ForgetEvents();
        // Domain native state is restored above. Only now install the matching token ledger, so its target
        // generations are checked against the instances the checkpoint actually adopted.
        AdoptDeferredResults(deferred);
        NotifyLifecycle(RuntimeLifecycleKind.CheckpointRestored);
        BindDeferredResults();
    }

    /// <summary>
    /// Re-arms behaviour from the host's supplied immutable reading, then restores values and latches. The kernel
    /// performs no discovery: ordinary checkpoint callbacks supply the admitted snapshot; only an explicit
    /// author command can supply a new reading. The same expedition and world epoch continue.
    /// </summary>
    internal IReadOnlyList<PlanLoadOutcome> RestoreCheckpoint(string json, IReadOnlyList<PlanCandidate> files)
    {
        Mutable();
        var reusable = new Dictionary<string, LoadedPlan>(plans.Loaded, StringComparer.Ordinal);
        foreach (var planId in plans.Loaded.Keys.ToArray()) ForgetVariables(planId);
        plans.Loaded.Clear();
        RebuildSubscriptions();
        var outcomes = LoadPlans(files, reusable);
        RestoreCheckpoint(json);
        return outcomes;
    }

    /// <summary>
    /// Drops one subject's variables. An enemy that died and a deployed item that returned to its owner are the two
    /// cases the scope table names, and both are the producer's own fact: the kernel cannot tell a dead enemy from
    /// a live one, so the package that observed it says so here instead of the value being read later as if the
    /// entity were still there.
    /// </summary>
    // A native end of life can land inside a dispatch (a removal step despawns an enemy and the game answers
    // synchronously). The release is then queued and applied when the tick ends, once per subject, and the call
    // answers 0 because nothing was dropped yet.
    public int ReleaseVariableScope(string scopeKind, EntityReference subject)
    {
        ReadThread(); NoLifecycleMutation();
        RuntimeJson.Text(subject.Id); RuntimeJson.Integer(subject.WorldEpoch); RuntimeJson.Integer(subject.LifeEpoch);
        RuntimeJson.Require(subject.WorldEpoch == WorldEpoch, "stale-world", subject.Id);
        RuntimeJson.Require(VariableScopeKinds.All.Contains(scopeKind), "variable-scope", scopeKind);
        if (!advancing) return kernelVariables.variables.ClearScope(scopeKind, subject.Id);
        if (kernelVariables.queuedScopeReleaseKeys.Add((scopeKind, subject.Id))) kernelVariables.queuedScopeReleases.Add((scopeKind, subject.Id));
        return 0;
    }
}

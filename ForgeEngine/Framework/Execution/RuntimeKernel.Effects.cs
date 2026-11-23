using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>Why one kernel-managed effect ended. The reason travels with the restore callback because the module's
/// own answer can differ: an expired effect and a cancelled one are the same revert, while a world that ended is
/// often a revert nothing has to do — the native state went with the world.</summary>
internal static class EffectEndReasons
{
    /// <summary>The duration the card asked for ran out.</summary>
    public const string Expired = "expired";
    /// <summary>A `cancel` step, or the module's own holder, named the effect's handle.</summary>
    public const string Cancelled = "cancelled";
    /// <summary>A dispel action ended the instance because the write declared the status dispellable.</summary>
    public const string Dispelled = "dispelled";
    /// <summary>The world ended, the plan was released, the module unregistered, or the life the effect belonged
    /// to is no longer current.</summary>
    public const string Reclaimed = "reclaimed";
}

/// <summary>
/// What one ending hands back to the module that applied the effect. The handle names the effect instance the
/// kernel cast — the same value the step published when the card asked for a cancel handle; the subject is the
/// recipient set the application was about; the stacks are how many layers the kernel counted, so a module that
/// wrote one native layer per layer knows how many it has to undo.
///
/// The callback runs inside the kernel's own turn: it may not dispatch, register or unregister, and it must not
/// assume the world is still there — `reclaimed` is written for a world that already ended. It also has to be
/// idempotent, because a module may have undone the effect itself through its own removal action while the kernel
/// still holds the instance until its duration runs out.
/// </summary>
public sealed class RuntimeEffectContext
{
    internal RuntimeEffectContext(JsonElement handle, string reason, int stacks, long tick, long worldEpoch,
        string planId, string nodeId, string bindingId, string capabilityId, string commandId,
        IReadOnlyList<EntityReference> subject, string statusKind, IReadOnlyList<string>? applicationIds = null)
    {
        Handle = handle; Reason = reason; Stacks = stacks; Tick = tick; WorldEpoch = worldEpoch;
        HandleId = RuntimeHandle.TryRead(handle, out var id) ? id : default;
        PlanId = planId; NodeId = nodeId; BindingId = bindingId; CapabilityId = capabilityId;
        CommandId = commandId; Subject = subject; StatusKind = statusKind;
        ApplicationIds = applicationIds ?? new[] { commandId };
    }

    /// <summary>The effect handle the kernel cast for this instance, as the step published it.</summary>
    public JsonElement Handle { get; }
    /// <summary>The same handle as its value: what a module keys the state it bound to the effect by.</summary>
    public RuntimeHandle HandleId { get; }
    /// <summary>One of <see cref="EffectEndReasons"/>.</summary>
    public string Reason { get; }
    /// <summary>How many layers the kernel counted for this instance, at least one.</summary>
    public int Stacks { get; }
    /// <summary>The tick the effect ended in.</summary>
    public long Tick { get; }
    /// <summary>The world the effect was applied in.</summary>
    public long WorldEpoch { get; }
    public string PlanId { get; }
    public string NodeId { get; }
    public string BindingId { get; }
    public string CapabilityId { get; }
    /// <summary>The command that last applied the effect, for diagnostics.</summary>
    public string CommandId { get; }
    /// <summary>The stable application identities of exactly the layers this callback must restore.</summary>
    public IReadOnlyList<string> ApplicationIds { get; }
    /// <summary>The recipients the application was about, in the order the step's own recipients port carried
    /// them. Empty when that port carries no entity — a modifier named by a handle, for example.</summary>
    public IReadOnlyList<EntityReference> Subject { get; }
    /// <summary>The declared status the ended instance was, or empty for an ordinary effect: the kind the write
    /// named in its `effect` block, which is also what the handle it cast carries.</summary>
    public string StatusKind { get; }
}

/// <summary>What a module does when the kernel ends an effect: undo what the application wrote. Keyed by the
/// handler name of the binding that applied it, exactly as the handler table is.</summary>
public delegate void EffectRestoreHandler(RuntimeEffectContext context);

/// <summary>One end_on binding and the compiler-resolved subject index, checked against its registered graph at load.</summary>
internal sealed record EffectEndTrigger(string BindingId, int SubjectOutput, PortView Subject);

/// <summary>
/// One action step's duration options, read once when the plan loads (plan §3.4 动作卡). These are structural: the
/// kernel owns the handle, the clock, the period, the refresh, the layer count and the early ending, and calls the
/// module's own restore callback when an instance ends, so a card never writes a duration into a port a handler
/// would have to keep a clock for.
///
/// <see cref="Reapply"/> is what a second application of the same effect instance does, spelled with the shared
/// `stack_policy` set the compiler writes and nothing else: `refresh` restarts the duration over the one layer the
/// instance has, `extend` adds the new duration to what the layer has left, `add` adds a layer and restarts the
/// duration of them all, up to <see cref="MaxStacks"/> — at the cap a further application only refreshes, so the
/// layer count never exceeds what the module was told to write — and `independent` adds a layer with a clock of its
/// own, so each layer ends by itself and is restored by itself.
///
/// <see cref="Interval"/> is the period: every layer runs the action again every that many ticks while it lasts,
/// the first run being immediate where the card asked for it. A sustained one-shot action (damage, healing) needs
/// no restore callback, which is why an action without one is allowed exactly when it carries a period.
///
/// <see cref="EndOn"/> names the trigger rows whose events end the effect early — the receiver's own abilities,
/// resolved to their bindings at load. <see cref="Cancel"/> is whether the step publishes the handle a later
/// `cancel` step holds.
///
/// <see cref="StatusKind"/> is the declared status the write is: an application that names one is a status instance
/// of that kind on its receivers, which is what makes it addressable by (receiver, status kind) — the event the
/// kernel publishes when it ends, and the dispel action that ends it early. An application that names none is an
/// ordinary effect and neither is about it. <see cref="Removal"/> is the status profile's own removal policy,
/// resolved into the step by the compiler: `until-expiry` is the default every profile in the catalog declares, and
/// only a `dispellable` instance may be ended by `forge.action.status.dispel`.
/// </summary>
internal sealed record EffectOptions(long Duration, string Reapply, int MaxStacks, bool Cancel,
    long? Interval = null, bool IntervalImmediate = false, IReadOnlyList<string>? EndOn = null,
    string StatusKind = "", string Removal = StatusContract.RemovalUntilExpiry, int StatusIndex = -1)
{
    internal const string Refresh = "refresh";
    internal const string Extend = "extend";
    internal const string Add = "add";
    internal const string Independent = "independent";
    private static readonly string[] Fields =
        { "duration", "reapply", "max_stacks", "cancel", "interval", "interval_immediate", "end_on", "status", "removal" };

    /// <summary>Whether a second application adds a layer rather than refreshing the one there is.</summary>
    internal bool StacksOnReapply => Reapply is Add or Independent;

    /// <summary>Whether each layer keeps a clock of its own rather than sharing the instance's.</summary>
    internal bool IndependentLayers => Reapply == Independent;

    /// <summary>Whether the new duration is added to the remaining time of the layer already there instead of
    /// replacing it: `extend` is a refresh that does not cut the window short.</summary>
    internal bool ExtendsWindow => Reapply == Extend;

    /// <summary>The trigger bindings that end this effect early, empty where the card named none.</summary>
    internal IReadOnlyList<string> EndsOn => EndOn ?? Array.Empty<string>();

    /// <summary>The same endings resolved to their one declared entity output at plan load. These descriptors
    /// are checked against I-PLAN's subject index; a restored effect uses its currently loaded plan's descriptors.</summary>
    internal IReadOnlyList<EffectEndTrigger> EndTriggers { get; init; } = Array.Empty<EffectEndTrigger>();

    /// <summary>The step's own `effect` block, or null where the card asked for none. Every refusal names the
    /// option that is wrong: a block that cannot be carried out is a plan that does not load, not an action that
    /// silently lasts forever. `unknown-field` stays the shared code for a field nothing reads.</summary>
    internal static EffectOptions? Parse(JsonElement step)
    {
        if (!step.TryGetProperty("effect", out var block) || block.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(block.ValueKind == JsonValueKind.Object, "effect-shape", "effect");
        RuntimeJson.Shape(block, "", string.Join(' ', Fields));
        RuntimeJson.Require(block.TryGetProperty("duration", out var durationValue) && Whole(durationValue, 1),
            "effect-duration", "effect.duration must be a whole number of ticks of at least one.");
        var reapply = Refresh;
        if (block.TryGetProperty("reapply", out var reapplyValue) && reapplyValue.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Require(reapplyValue.ValueKind == JsonValueKind.String, "effect-reapply", "effect.reapply");
            reapply = reapplyValue.GetString()!;
            RuntimeJson.Require(reapply is Refresh or Extend or Add or Independent, "effect-reapply",
                "effect.reapply must be `refresh`, `extend`, `add` or `independent`.");
        }
        var maxStacks = 1;
        if (block.TryGetProperty("max_stacks", out var stacksValue) && stacksValue.ValueKind != JsonValueKind.Null)
        {
            // A cap is the one thing a stacking reapply cannot work without: without it the count has no ceiling
            // and the module is asked for layers nobody bounded.
            RuntimeJson.Require(reapply is Add or Independent, "effect-stacks",
                "effect.max_stacks belongs to a stacking reapply.");
            RuntimeJson.Require(Whole(stacksValue, 1), "effect-stacks", "effect.max_stacks must be at least one.");
            maxStacks = (int)stacksValue.GetDouble();
        }
        RuntimeJson.Require(reapply is not (Add or Independent) || block.TryGetProperty("max_stacks", out _), "effect-stacks",
            "a stacking effect.reapply needs effect.max_stacks.");
        // The period: a sustained action re-runs itself every `interval` ticks while it lasts, with an optional
        // first run in the tick it was applied in.
        long? interval = null;
        if (block.TryGetProperty("interval", out var intervalValue) && intervalValue.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Require(Whole(intervalValue, 1), "effect-interval", "effect.interval must be at least one tick.");
            interval = (long)intervalValue.GetDouble();
        }
        var immediate = false;
        if (block.TryGetProperty("interval_immediate", out var immediateValue) && immediateValue.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Require(immediateValue.ValueKind is JsonValueKind.True or JsonValueKind.False, "effect-interval",
                "effect.interval_immediate");
            immediate = immediateValue.ValueKind == JsonValueKind.True;
            RuntimeJson.Require(interval != null, "effect-interval", "effect.interval_immediate needs effect.interval.");
        }
        // The events that end the effect early, as trigger binding ids of this plan: the receiver's own abilities,
        // which is why the row they name is looked up at load.
        var endOn = Array.Empty<string>();
        if (block.TryGetProperty("end_on", out var endValue) && endValue.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Require(endValue.ValueKind == JsonValueKind.Array, "effect-end-on", "effect.end_on must be an array.");
            var rows = endValue.EnumerateArray().ToArray();
            RuntimeJson.Require(rows.Length <= 8, "effect-end-on", "effect.end_on carries at most eight abilities.");
            var ids = new string[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                RuntimeJson.Require(rows[i].ValueKind == JsonValueKind.String, "effect-end-on", "effect.end_on");
                ids[i] = rows[i].GetString()!;
            }
            RuntimeJson.Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "effect-end-on",
                "effect.end_on repeats an ability.");
            endOn = ids;
        }
        var cancel = false;
        if (block.TryGetProperty("cancel", out var cancelValue) && cancelValue.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Require(cancelValue.ValueKind is JsonValueKind.True or JsonValueKind.False, "effect-cancel",
                "effect.cancel");
            cancel = cancelValue.ValueKind == JsonValueKind.True;
        }
        // The declared status the write is, and the removal policy its profile declares: the two facts that make an
        // application addressable as a status instance rather than as an anonymous effect. The member sets are the
        // shared tables' own, so a card cannot spell a status no profile declares or a policy no set carries.
        var status = "";
        if (block.TryGetProperty("status", out var statusValue) && statusValue.ValueKind != JsonValueKind.Null)
        {
            RuntimeJson.Require(statusValue.ValueKind == JsonValueKind.String, "effect-status", "effect.status");
            status = statusValue.GetString()!;
            RuntimeJson.Require(RuntimeGraphContracts.IsEnumMember(StatusContract.StatusKindSchema, status),
                "effect-status", "effect.status must be a member of the shared status_kind set.");
        }
        var removal = StatusContract.RemovalUntilExpiry;
        if (block.TryGetProperty("removal", out var removalValue) && removalValue.ValueKind != JsonValueKind.Null)
        {
            // A removal policy is a property of the status that was written, so it means nothing without one: a
            // policy on an anonymous effect would be a field nothing could ever read.
            RuntimeJson.Require(status.Length > 0, "effect-removal",
                "effect.removal needs effect.status: only a declared status has a removal policy.");
            RuntimeJson.Require(removalValue.ValueKind == JsonValueKind.String, "effect-removal", "effect.removal");
            removal = removalValue.GetString()!;
            RuntimeJson.Require(RuntimeGraphContracts.IsEnumMember("status_removal", removal), "effect-removal",
                "effect.removal must be `dispellable` or `until-expiry`.");
        }
        return new EffectOptions((long)durationValue.GetDouble(), reapply, maxStacks, cancel, interval, immediate, endOn,
            status, removal, status.Length == 0 ? -1 : StatusContract.IndexOf(StatusContract.StatusKindSchema, status));
    }

    private static bool Whole(JsonElement value, long minimum)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
           && double.IsFinite(number) && number == Math.Truncate(number)
           && number >= minimum && number <= RuntimeJson.MaxSafeInteger;
}

public sealed partial class RuntimeKernel
{
    /// <summary>
    /// Whether a trigger named by `end_on` can end this action, judged from the two rows' declared entity kinds: a
    /// trigger whose declared subject kinds do not cover every kind the action receives is refused at load. Only
    /// the explicit recipient input participates; source/owner/context and result outputs cannot prove coverage.
    /// Missing kinds mean ANY on either side, with exactly the ordinary graph's subtype assignability rule.
    /// </summary>
    internal static bool EndOnCovers(JsonElement trigger, JsonElement action)
    {
        var subject = RuntimeGraphContracts.SubjectPort(trigger);
        if (subject.ValueKind != JsonValueKind.Object || RuntimeJson.Text(subject, "type") != "entity"
            || action.ValueKind != JsonValueKind.Object || !action.TryGetProperty("recipients", out var recipients)
            || recipients.ValueKind != JsonValueKind.Object
            || RuntimeGraphContracts.OptionalText(recipients, "target") != "entity") return false;
        var input = RuntimeGraphContracts.OptionalText(recipients, "input");
        var recipient = RuntimeJson.Rows(action, "inputs").FirstOrDefault(port => RuntimeJson.Text(port, "id") == input);
        return recipient.ValueKind == JsonValueKind.Object && RuntimeJson.Text(recipient, "type") == "entity"
            && RuntimeGraphContracts.EntityKindsNarrow(recipient, subject);
    }

    /// <summary>
    /// The kernel's own dispel: ends the instances of one declared status on the recipients the step names, and
    /// only the ones whose write declared them dispellable. One row is written per recipient, in the recipients'
    /// own order, after the four fixed columns: a receiver whose status did not end is `status-not-dispellable` —
    /// nothing of that kind is on it, or what is there only ends at its own expiry — and a receiver whose instances
    /// all ended is committed. An instance ends through the same <see cref="EndEffect"/> every other ending uses, so
    /// the module is called back and the status-ended fact is published with `dispelled` as its reason.
    /// </summary>
    private CommandResult DispelStatuses(ResolvedStep step, JsonElement inputs, RuntimeEvent origin)
    {
        var targets = EffectSubject(step, inputs);
        var status = "";
        foreach (var input in step.Inputs)
        {
            if (OptionalPort(input.Port, "schema") != StatusContract.StatusKindSchema) continue;
            if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(input.Name, out var value)) continue;
            // The step addresses the instance by its compiled member index, exactly as every enum input arrives in a
            // frame; the name is what the instance itself recorded, so the index is resolved through the shared set
            // before the two are compared.
            var resolved = RuntimeJson.EnumPortToHandlerValue(value, input.Port);
            status = resolved.ValueKind == JsonValueKind.String ? resolved.GetString()! : "";
        }
        if (status.Length == 0) return CommandResult.Rejected(RuntimeAbiCodes.StatusDispelShape, step.NodeId);
        var rows = new List<object>(targets.Count);
        var outcomes = new List<(string Commit, string Code)>(targets.Count);
        var statusIndex = StatusContract.IndexOf(StatusContract.StatusKindSchema, status);
        if (statusIndex < 0) return CommandResult.Rejected(RuntimeAbiCodes.StatusDispelShape, step.NodeId);
        // EndEffect removes every receiver key of the instance it retires. Keep one reusable snapshot so a restore
        // callback may end a sibling without invalidating this pass, while a many-target dispel allocates no
        // target-local match lists and never scans effects belonging to another receiver or status.
        var matches = kernelEffects.endingScratchPool.Count == 0 ? new List<EffectSlot>() : kernelEffects.endingScratchPool.Pop();
        try
        {
            foreach (var target in targets)
            {
                matches.Clear();
                // A preceding receiver's restore callback may have opened another status instance. Rebuild before
                // every receiver when creation marked the derived address dirty, matching the old fresh table scan.
                kernelEffects.EnsureStatusEffectIndex();
                if (kernelEffects.statusEffectIndex.TryGetValue(new StatusEffectKey(target.Id, target.WorldEpoch, statusIndex), out var indexed))
                    for (var index = 0; index < indexed.Count; index++)
                        if (indexed[index].Removal == StatusContract.RemovalDispellable) matches.Add(indexed[index]);
                if (matches.Count == 0)
                {
                    // Nothing of that status on this receiver, or something that only its own expiry ends: the row's
                    // one refusal covers both, because the answer the author reads is the same — this receiver's status
                    // did not end, and the row is the record of that.
                    outcomes.Add((CommitStates.None, StatusContract.NotDispellable));
                    rows.Add(new { target, status = CommandStatuses.Rejected, committed = CommitStates.None, code = StatusContract.NotDispellable, target_count = targets.Count });
                    continue;
                }
                var remoteRestore = false;
                for (var index = 0; index < matches.Count && !remoteRestore; index++)
                    for (var layer = 0; layer < matches[index].Layers.Count; layer++)
                        if (matches[index].Layers[layer].OwnerApplication != null
                            || matches[index].Layers[layer].AdditionalOwnerApplications.Count != 0)
                        { remoteRestore = true; break; }
                var restored = true;
                for (var index = 0; index < matches.Count; index++)
                    restored &= EndEffect(matches[index], EffectEndReasons.Dispelled, CurrentTick);
                if (!restored || remoteRestore)
                {
                    var code = !restored ? "effect-restore-failed" : "owner-cancel-pending";
                    outcomes.Add((CommitStates.Unknown, code));
                    rows.Add(new
                    {
                        target,
                        status = CommandStatuses.Failed,
                        committed = CommitStates.Unknown,
                        code,
                        target_count = targets.Count
                    });
                    continue;
                }
                outcomes.Add((CommitStates.Confirmed, ResultCode.Committed));
                rows.Add(new { target, status = CommandStatuses.Succeeded, committed = CommitStates.Confirmed, code = ResultCode.Committed, target_count = targets.Count });
            }
        }
        finally { matches.Clear(); kernelEffects.endingScratchPool.Push(matches); }
        return ResultAggregation.Conclude(outcomes, static row => row.Commit, static row => row.Code, RuntimeJson.From(new { result = rows }));
    }

    /// <summary>Whether one live instance is about one receiver. The instance's subject is the recipient set its
    /// own write carried, and a status is addressed by the receiver it is on, so the question is identity and not
    /// kind: two enemies are two statuses even when they are the same kind of enemy.</summary>
    private static string? OptionalPort(JsonElement port, string key)
        => port.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal sealed record AdditionalOwnerApplication(OwnerOutput Request, string SourceProvider, string ScopeId);

    /// <summary>One layer of a live effect: the clock the kernel owns, and the period it re-runs the action on.
    /// A `refresh` instance has exactly one layer and an `add` instance shares one clock across its layers, so a
    /// layer per application is what makes `independent` the same code with one clock each.</summary>
    internal sealed class EffectLayer
    {
        internal string SourceProvider = "";
        internal string ScopeId = "";
        internal RuntimeEvent Origin = null!;
        internal JsonElement Inputs;
        internal JsonElement Parameters;
        internal StepInputs? InputFrame;
        internal EntityCheck[] InputLiveness = Array.Empty<EntityCheck>();
        internal string CommandPrefix = "";
        internal string CommandId = "";
        // A kept owner application pays for this layer's eventual restore before its native write.
        internal OwnerOutput? OwnerApplication;
        // Concurrent admitted holder writes may settle after this layer reached its stack cap. Each keeps the
        // cleanup reservation it paid for; the world-wide owner ledger bounds this list.
        internal readonly List<AdditionalOwnerApplication> AdditionalOwnerApplications = new();
        internal bool OwnerRestoreQueued;
        // A failed ending is retained for explicit cleanup retry, never pulsed or refreshed as an active layer.
        internal bool RestorePending;
        internal bool RestoreInProgress;
        internal long RestoreAttempt;
        internal long ExpiresAtTick;
        /// <summary>The next tick this layer's action runs again, or -1 for a layer that carries no period. Each
        /// layer keeps its own clock so a long gap in the advance skips missed pulses — the next one is always one
        /// interval after the pulse that ran — instead of replaying ticks nobody simulated.</summary>
        internal long NextPulseTick = -1;
    }

    /// <summary>One live effect instance: the card that applied it, the recipients it is about, its layers, and the
    /// handle that names it. The instance is the kernel's, not the module's — the module holds native state and
    /// answers the restore callback, and neither side keeps a clock for the other.</summary>
    internal sealed class EffectSlot
    {
        internal int Id;
        internal ResolvedStep? Step;
        internal long ProviderGeneration;
        internal string ProviderId = "";
        internal string BindingId = "";
        internal string CapabilityId = "";
        internal string PlanId = "";
        internal string ResourceId = "";
        internal string ResourceRevision = "";
        internal string EntryId = "";
        internal string NodeId = "";
        internal string Identity = "";
        /// <summary>The declared status this instance is, or empty for an ordinary effect. The handle carries the
        /// same name, so an instance is addressable by (receiver, status kind) from both halves of the record.</summary>
        internal string StatusKind = "";
        /// <summary>The removal policy the write declared: only a `dispellable` instance is one a dispel action may
        /// end, and everything else ends at its own expiry.</summary>
        internal string Removal = StatusContract.RemovalUntilExpiry;
        internal IReadOnlyList<EntityReference> Subject = Array.Empty<EntityReference>();
        internal EntityCheck[] SubjectLiveness = Array.Empty<EntityCheck>();
        internal EffectOptions Options = null!;
        internal readonly List<EffectLayer> Layers = new();
        internal OwnerOutput? RestoringOwnerApplication;
        // Keep the exact callback after a failed restore, even if its provider unregisters before cleanup retries.
        internal EffectRestoreHandler? RetryRestore;
        internal bool RestoreInProgress;
        /// <summary>The historical high-water mark retained by the migration snapshot. Cleanup uses only the
        /// layers still present: independent layers may already have been restored before the instance ends.</summary>
        internal int Peak = 1;
        internal RuntimeEvent Origin = null!;
        internal JsonElement Inputs;
        internal JsonElement Parameters;
        internal StepInputs? InputFrame;
        internal EntityCheck[] InputLiveness = Array.Empty<EntityCheck>();
        internal string CommandPrefix = "";
        internal string PulseCommandSuffix = "";
        internal long WorldEpoch;
        internal string CommandId = "";
        /// <summary>The handle in its wire form and as its value; the value is read once, where the form is set.</summary>
        internal JsonElement Handle { get => handle; set { handle = value; RuntimeHandle.TryRead(value, out HandleId); } }
        private JsonElement handle;
        internal RuntimeHandle HandleId;
        internal int HandleSlot = -1;
        /// <summary>How many times this instance's action has been re-run by its period, so a pulse's command
        /// identity is its own and a replay ledger can never mistake two pulses for one work item.</summary>
        internal long Pulses;
    }

    /// <summary>One application in flight: what the handler is told, and what is rolled back when it refuses. The
    /// instance's own clock and layer count are only written once the command says it committed, so a refused
    /// application neither refreshes a running effect nor spends a stack.</summary>
    internal sealed class PendingEffect
    {
        internal EffectSlot Slot = null!;
        internal string SourceProvider = "";
        internal string ScopeId = "";
        internal RuntimeEvent Origin = null!;
        internal StepInputs Inputs = null!;
        internal string CommandPrefix = "";
        internal OwnerOutput? OwnerApplication;
        internal EffectOptions Options = null!;
        internal string ApplicationId = "";
        internal bool Created;
        internal bool AddLayer;
        internal string Port = "";
    }
    /// <summary>A derived address into <see cref="effects"/> for status removal. The target identity intentionally
    /// matches the established status-addressing rule (id + world); the dispatch boundary has already rejected a
    /// stale life. Each rebuild appends slots in the current <see cref="effects"/> traversal order, so a restore
    /// observes the same order as the former table walk, including dictionary entry reuse. The index owns no effect
    /// state and is rebuilt from restored slots during migration.</summary>
    internal readonly record struct StatusEffectKey(string TargetId, long WorldEpoch, int Status);

    /// <summary>The effect instance one application belongs to: the card and the recipients it is about. Two
    /// dispatches of the same node about the same recipients are the same instance — that is what makes a refresh
    /// a refresh — and a different recipient is its own instance with its own clock. A card whose recipients port
    /// carries no entity keys on the node alone.</summary>
    private static string EffectIdentity(string planId, string nodeId, IReadOnlyList<EntityReference> subject)
    {
        if (subject.Count == 0) return planId + "\n" + nodeId;
        var builder = new StringBuilder(planId).Append('\n').Append(nodeId);
        foreach (var entity in subject)
            builder.Append('\n').Append(entity.Id).Append('@').Append(entity.WorldEpoch).Append(':').Append(entity.LifeEpoch);
        return builder.ToString();
    }

    /// <summary>The recipients one application is about, read from the step's own `recipients.input` port. A port
    /// the plan left unwired, or one that carries a handle rather than an entity, answers with none: the effect
    /// still has its own instance, keyed by the node.</summary>
    private static IReadOnlyList<EntityReference> EffectSubject(ResolvedStep step, JsonElement inputs)
    {
        if (!step.Contract.TryGetProperty("recipients", out var recipients)) return Array.Empty<EntityReference>();
        if (!recipients.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.String)
            return Array.Empty<EntityReference>();
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(input.GetString()!, out var value)
            || value.ValueKind == JsonValueKind.Null) return Array.Empty<EntityReference>();
        if (value.ValueKind == JsonValueKind.Array)
        {
            var entities = new List<EntityReference>(value.GetArrayLength());
            foreach (var element in value.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) return Array.Empty<EntityReference>();
                entities.Add(RuntimeJson.Entity(element));
            }
            return entities;
        }
        return value.ValueKind == JsonValueKind.Object
            ? new[] { RuntimeJson.Entity(value) }
            : Array.Empty<EntityReference>();
    }

    /// <summary>
    /// Opens, refreshes or stacks the effect one action step is about and hands the handler what it needs. The
    /// handle is cast here — before the handler runs, so an application that cannot name its own effect writes
    /// nothing — and the layer count is decided here but committed in <see cref="CommitEffect"/>.
    ///
    /// The instance also captures what a later period needs to run the same action again: the origin event, the
    /// resolved inputs and parameters, and this dispatch's own command prefix. A pulse is not a new event, so this
    /// is the only place the frame it re-runs can come from (plan §3.2: 不新派发事件).
    /// </summary>
    private PendingEffect BeginEffect(ResolvedStep step, EffectOptions options, ResolvedPlan plan, StepInputs inputs,
        RuntimeEvent origin, string commandPrefix, string entryId)
    {
        // The effect outlives this execution of the step, whose frame its next execution refills: it keeps a copy.
        inputs = inputs.Snapshot();
        var planId = plan.Id;
        var subject = EffectSubject(step, inputs.Json);
        var identity = EffectIdentity(planId, step.NodeId, subject);
        EffectSlot? slot = null;
        var created = !kernelEffects.effectIdentity.TryGetValue(identity, out var id) || !kernelEffects.effects.TryGetValue(id, out slot);
        if (created)
        {
            var fresh = new EffectSlot
            {
                Id = ++kernelEffects.effectSequence,
                ProviderId = step.ProviderId,
                Step = step,
                ProviderGeneration = registry.Modules[step.ProviderId],
                BindingId = step.BindingId,
                CapabilityId = step.CapabilityId,
                PlanId = planId,
                ResourceId = plan.ResourceId,
                ResourceRevision = plan.ResourceRevision,
                EntryId = entryId,
                NodeId = step.NodeId,
                Identity = identity,
                Subject = subject,
                SubjectLiveness = kernelEffects.BindEffectSubjectLiveness(subject),
                Options = options,
                StatusKind = options.StatusKind,
                Removal = options.Removal,
                Origin = origin,
                Inputs = inputs.Json,
                Parameters = inputs.WireParameters,
                InputFrame = inputs,
                InputLiveness = inputs.BindEntityLiveness(this),
                CommandPrefix = commandPrefix,
                PulseCommandSuffix = PulseCommandSuffix(step)
            };
            fresh.Handle = kernelEffects.CreateHandle(step.ProviderId, "effect", EffectLifetime(step.Contract), out var handleSlot,
                effectId: fresh.Id, statusKind: options.StatusKind);
            fresh.HandleSlot = handleSlot;
            slot = fresh;
        }
        RuntimeJson.Require(!slot!.Layers.Any(layer => layer.RestorePending), "effect-restore-pending", step.NodeId);
        return new PendingEffect
        {
            Slot = slot!,
            SourceProvider = registry.BindingViews[origin.BindingId].ProviderId,
            ScopeId = origin.ScopeId,
            Origin = origin,
            Inputs = inputs,
            CommandPrefix = commandPrefix,
            Options = options,
            Created = created,
            // A fresh instance is its first layer; a running one adds a layer only where the card stacks and the
            // cap has not been reached, so an application at the cap is a pure refresh.
            AddLayer = created || options.StacksOnReapply && slot!.Layers.Count < options.MaxStacks,
            Port = options.Cancel ? EffectHandlePort(step.Contract) ?? "" : ""
        };
    }

    /// <summary>Whether the command proved that nothing was written. A refused or failed command with no commit
    /// leaves no effect; anything that may have written keeps its instance, because a module that did write has to
    /// be called back for it.</summary>
    internal static bool EffectKept(CommandResult result)
        => result.Status == CommandStatuses.Succeeded || result.CommitState != CommitStates.None;

    /// <summary>
    /// Makes one application real, or rolls it back. A kept application takes the layer the kernel decided, restarts
    /// the duration from this tick — of the layer it refreshed, of every layer an `add` share one clock with, or,
    /// for `extend`, from what each layer has left plus the new duration — and — where the card asked for a cancel
    /// handle — has the handle published into the step's own handle port, so a later `cancel` step or removal action
    /// holds exactly what this step wrote. A refused application leaves a running instance exactly as it was: no
    /// refreshed clock, no spent stack, and a brand-new instance that never existed gives its handle back to the
    /// pool.
    /// </summary>
    private void CommitEffect(PendingEffect effect, CommandResult result, long tick, string commandId,
        ref CommandResult commandResult)
    {
        var slot = effect.Slot;
        if (!EffectKept(result))
        {
            if (effect.Created)
            {
                kernelEffects.ReleaseHandle(slot.HandleSlot);
                slot.HandleSlot = -1;
            }
            return;
        }
        if (effect.Created)
        {
            kernelEffects.lifetimeScratch.EnsureCapacity(kernelEffects.effects.Count + 1);
            kernelEffects.effects.Add(slot.Id, slot);
            kernelEffects.effectIdentity[slot.Identity] = slot.Id;
            kernelEffects.IndexEffectEndings(slot);
        }
        var options = effect.Options;
        var layer = new EffectLayer
        {
            ExpiresAtTick = tick + options.Duration,
            OwnerApplication = effect.OwnerApplication,
            SourceProvider = effect.SourceProvider,
            ScopeId = effect.ScopeId,
            Origin = effect.Origin,
            Inputs = effect.Inputs.Json,
            Parameters = effect.Inputs.WireParameters,
            InputFrame = effect.Inputs.Snapshot(),
            InputLiveness = effect.Inputs.BindEntityLiveness(this),
            CommandPrefix = effect.CommandPrefix,
            CommandId = effect.ApplicationId.Length == 0 ? commandId : effect.ApplicationId
        };
        // An immediate first run is the application itself — the command already in flight is the first period run —
        // so the next one is one interval after this tick either way, and a pulse never fires twice in one tick.
        if (options.Interval is { } period) layer.NextPulseTick = tick + period;
        if (effect.Created) slot.Layers.Add(layer);
        else if (effect.AddLayer)
        {
            slot.Layers.Add(layer);
            slot.Peak = Math.Max(slot.Peak, slot.Layers.Count);
            // A stacking reapply shares one duration and one period across its layers: the layer just added does
            // not start a clock of its own, the whole stack ends — and pulses — on the refreshed one.
            // `independent` is the reapply that keeps a clock per layer and is left alone.
            if (!options.IndependentLayers)
                foreach (var running in slot.Layers)
                { running.ExpiresAtTick = layer.ExpiresAtTick; running.NextPulseTick = layer.NextPulseTick; }
        }
        // `extend` adds the new duration to what the layer already has left instead of replacing its clock, so a
        // second application never cuts the window it was asked to lengthen.
        else if (options.ExtendsWindow)
            foreach (var running in slot.Layers) running.ExpiresAtTick += options.Duration;
        else foreach (var running in slot.Layers) running.ExpiresAtTick = tick + options.Duration;
        if (!effect.Created && !effect.AddLayer && effect.OwnerApplication is { } refreshed)
        {
            var retained = slot.Layers[^1];
            if (retained.OwnerApplication?.CommandId != refreshed.CommandId
                && !retained.AdditionalOwnerApplications.Any(application => application.Request.CommandId == refreshed.CommandId))
                retained.AdditionalOwnerApplications.Add(new(refreshed, effect.SourceProvider, effect.ScopeId));
        }
        slot.WorldEpoch = WorldEpoch;
        slot.CommandId = commandId;
        // A holder can disconnect before its application confirmation arrives. Native cleanup already has its
        // durable terminal receipt; accepting the original commit must not restart the host's effect clock.
        if (effect.OwnerApplication is { } application && kernelOwner.OwnerApplicationDisconnected(application.CommandId))
            EndDisconnectedOwnerEffects(application.CommandId);
        if (effect.Port.Length == 0) return;
        commandResult = CommandResult.Create(commandResult.Status, commandResult.CommitState, commandResult.Code,
            commandResult.Detail, WithPort(commandResult.Outputs, effect.Port, slot.Handle), commandResult.Facts.ToArray());
    }

    /// <summary>The port a step's effect handle is published into: the graph's declared `recipients.handle`. Null
    /// where the row declares none, which is what makes `effect.cancel` a refusal at load rather than a handle that
    /// goes nowhere. The argument is a resolved graph — what a step carries — not a capability row's whole text.</summary>
    internal static string? EffectHandlePort(JsonElement graph)
    {
        if (!graph.TryGetProperty("recipients", out var recipients)) return null;
        if (!recipients.TryGetProperty("handle", out var handle) || handle.ValueKind != JsonValueKind.String) return null;
        var id = handle.GetString()!;
        foreach (var port in RuntimeJson.Rows(graph, "outputs"))
            if (RuntimeJson.Text(port, "id") == id && RuntimeJson.Text(port, "type") == "handle") return id;
        return null;
    }

    /// <summary>The lifetime that port declares; a row that declares an effect handle carries one, and the tag is
    /// what a consuming port's own `handle-lifetime` check reads.</summary>
    internal static string EffectLifetime(JsonElement graph)
    {
        if (EffectHandlePort(graph) is not { } id) return "entity_life";
        foreach (var port in RuntimeJson.Rows(graph, "outputs"))
            if (RuntimeJson.Text(port, "id") == id && port.TryGetProperty("lifetime", out var lifetime)
                && lifetime.ValueKind == JsonValueKind.String) return lifetime.GetString()!;
        return "entity_life";
    }

    /// <summary>A handler's own outputs with one more port added. The rest of the frame is the handler's answer
    /// untouched, so a later step reads one output object exactly as it reads any other.</summary>
    private static JsonElement WithPort(JsonElement outputs, string port, JsonElement value)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (outputs.ValueKind == JsonValueKind.Object)
            foreach (var field in outputs.EnumerateObject()) fields[field.Name] = field.Value;
        fields[port] = value;
        return RuntimeJson.From(fields);
    }

    /// <summary>Ends an instance only after its native restoration returns. Failed layers retain their handle
    /// and cleanup obligation, but no longer run periods or accept reapplies. Independent siblings still get
    /// their cleanup attempt; an explicit retry only visits the layers that remain.</summary>
    private bool EndEffect(EffectSlot slot, string reason, long tick)
    {
        if (!kernelEffects.effects.ContainsKey(slot.Id)) return true;
        if (slot.RestoreInProgress) return false;
        if ((slot.Options.IndependentLayers || slot.Layers.Any(layer => layer.OwnerApplication != null || layer.AdditionalOwnerApplications.Count != 0)) && slot.Layers.Count != 0)
        {
            var restored = true;
            foreach (var entry in slot.Layers.Select(layer => (Layer: layer, Attempt: layer.RestoreAttempt)).Reverse().ToArray())
            {
                var index = slot.Layers.IndexOf(entry.Layer);
                if (index < 0) continue;
                // A sibling callback may already have attempted this layer. Keep its real failed outcome rather
                // than retrying unknown native cleanup twice in this same ending.
                if (entry.Layer.RestoreAttempt != entry.Attempt) restored &= !entry.Layer.RestorePending;
                else restored &= EndEffectLayer(slot, index, reason, tick);
            }
            return restored;
        }
        if (slot.Layers.Any(layer => layer.RestoreInProgress)) return false;
        foreach (var layer in slot.Layers) layer.RestorePending = true;
        if (slot.Layers.Count != 0 && !RestoreEffect(slot, reason, slot.Layers.Count)) return false;
        slot.Layers.Clear();
        RetireRestoredEffect(slot, reason, tick);
        return true;
    }

    /// <summary>Retires one independent layer after its restore returns. A failure leaves just that layer for a
    /// later explicit retry. Only the last confirmed restoration publishes the status-ended fact.</summary>
    private bool EndEffectLayer(EffectSlot slot, int index, string reason, long tick, bool alreadyRestored = false)
    {
        if (slot.RestoreInProgress) return false;
        var layer = slot.Layers[index];
        if (layer.RestoreInProgress) return false;
        layer.RestoreAttempt++;
        layer.RestorePending = true;
        if (!alreadyRestored)
        {
            var restored = true;
            if (layer.OwnerApplication is { } primary)
            {
                if (!layer.OwnerRestoreQueued)
                {
                    layer.OwnerRestoreQueued = RestoreEffect(slot, reason, 1, primary, OwnerEffectApplicationId(primary), layer);
                    restored &= layer.OwnerRestoreQueued;
                }
            }
            else restored = RestoreEffect(slot, reason, 1, commandId: layer.CommandId, restoringLayer: layer);
            for (var extra = layer.AdditionalOwnerApplications.Count - 1; extra >= 0; extra--)
            {
                var application = layer.AdditionalOwnerApplications[extra].Request;
                if (RestoreEffect(slot, reason, 1, application, OwnerEffectApplicationId(application), layer))
                    layer.AdditionalOwnerApplications.RemoveAt(extra);
                else restored = false;
            }
            if (!restored) return false;
        }
        slot.Layers.Remove(layer);
        if (slot.Layers.Count == 0) RetireRestoredEffect(slot, reason, tick);
        else kernelFlow.AdoptEffectLayerSource(slot, slot.Layers[0]);
        return true;
    }

    private void RetireRestoredEffect(EffectSlot slot, string reason, long tick)
    {
        kernelEffects.UnindexEffectEndings(slot);
        kernelEffects.effects.Remove(slot.Id);
        kernelEffects.ownerEffects.Remove(slot.HandleId);
        if (kernelEffects.effectIdentity.TryGetValue(slot.Identity, out var identity) && identity == slot.Id)
            kernelEffects.effectIdentity.Remove(slot.Identity);
        PublishStatusEnded(slot, reason, tick);
        // Teardown can already have released the old handle; never release a later occupant of that address.
        if (slot.HandleSlot >= 0 && slot.HandleSlot < kernelEffects.handleSlots.Count
            && kernelEffects.handleSlots[slot.HandleSlot]?.EffectId == slot.Id) kernelEffects.ReleaseHandle(slot.HandleSlot);
        slot.HandleSlot = -1;
        slot.RetryRestore = null;
    }

    /// <summary>
    /// The bindings that carry a status ending: one per binding the registry holds for the status-ended row, with
    /// the provider that owns it. Rebuilt where the registry's own binding table changes — a module registering or
    /// unregistering — so publishing an ending is a walk over rows that exist and nothing else, and a world where
    /// no module binds the row publishes nothing at all.
    /// </summary>
    private readonly List<(string BindingId, string Provider, long Generation)> statusEndedBindings = new();
    private void RebuildStatusEndedBindings()
    {
        statusEndedBindings.Clear();
        foreach (var binding in registry.Tables.Bindings)
        {
            if (RuntimeJson.Text(binding.Value, "capabilityId") != StatusContract.EndedCapability) continue;
            var provider = RuntimeJson.Text(binding.Value, "providerId");
            if (!registry.Modules.TryGetValue(provider, out var generation)) continue;
            statusEndedBindings.Add((binding.Key, provider, generation));
        }
    }

    /// <summary>
    /// Reports one declared status ending, once per receiver the instance was about. The fact is the row's own
    /// three answers — the receiver, the status kind and the reason — and it is published from the one place an
    /// instance ends, so an expiry, a cancellation, a dispel and a reclaim are the same fact with different reasons.
    ///
    /// A receiver that is no longer part of the world is skipped rather than reported: an event about an entity that
    /// is gone is one no card could read, and the ending is not less true for it. An effect that writes no declared
    /// status publishes nothing, which is what keeps the row about statuses and not about every effect this kernel
    /// ends. A publish that the kernel itself refuses is recorded against the binding, never thrown at the ending.
    /// </summary>
    private void PublishStatusEnded(EffectSlot slot, string reason, long tick)
    {
        if (slot.StatusKind.Length == 0 || statusEndedBindings.Count == 0) return;
        if (!world.worldStarted || world.worldHost == false) return;
        // The status travels as its compiled member index, exactly as it does on any other enum port: the name is
        // what the instance recorded and what a reader resolves it back to at the handler boundary.
        var status = StatusContract.IndexOf(StatusContract.StatusKindSchema, slot.StatusKind);
        var why = StatusContract.IndexOf(StatusContract.EndReasonSchema, reason);
        if (status < 0 || why < 0) return;
        foreach (var target in slot.Subject)
        {
            var kind = RuntimeJson.KindOf(target.Id);
            if (registry.Tables.Resolvers.TryGetValue(kind, out var resolver))
            {
                bool current;
                try { current = resolver.Resolve(target); }
                catch (Exception error)
                {
                    log.LogCallbackFailed("entity-resolver-failed target=" + target.Id, error);
                    current = false;
                }
                if (!current) continue;
            }
            foreach (var (binding, provider, generation) in statusEndedBindings)
            {
                if (!registry.IsRegistered(provider, generation)) continue;
                var eventId = "status:" + WorldEpoch + ":" + (++eventQueue.sequence);
                try
                {
                    var handle = new RuntimeModuleHandle(this, provider, generation);
                    var gate = BeginOwnEvent(handle, binding, out var values);
                    values.Entity(Blocks.TriggerStatusEnded.OutTarget, target);
                    values.Enum(Blocks.TriggerStatusEnded.OutStatus, status);
                    values.Enum(Blocks.TriggerStatusEnded.OutReason, why);
                    Raise(handle, gate, eventId, WorldEpoch, tick, slot.Origin.ScopeId, null, null, 0);
                }
                catch (RuntimeContractException error) { log.LogEventRejected(binding, eventId, error.Code, provider); }
            }
        }
    }

    /// <summary>One callback into the module that applied an effect, for the layers that just ended. A module that
    /// registered no restore callback is one whose effect was allowed without one — a periodic one-shot — and there
    /// is nothing to undo.</summary>
    private bool RestoreEffect(EffectSlot slot, string reason, int layers, OwnerOutput? ownerApplication = null, string? commandId = null,
        EffectLayer? restoringLayer = null)
    {
        var callback = slot.RetryRestore;
        if (callback == null && registry.Tables.EffectRestores.TryGetValue(slot.BindingId, out var restore)) callback = restore.Restore;
        if (callback == null || slot.Handle.ValueKind == JsonValueKind.Undefined) return true;
        var context = new RuntimeEffectContext(slot.Handle, reason, layers, CurrentTick, slot.WorldEpoch,
            slot.PlanId, slot.NodeId, slot.BindingId, slot.CapabilityId, commandId ?? slot.CommandId, slot.Subject, slot.StatusKind,
            commandId == null ? slot.Layers.Select(layer => layer.CommandId).ToArray() : new[] { commandId });
        if (restoringLayer == null) slot.RestoreInProgress = true;
        else restoringLayer.RestoreInProgress = true;
        var previousOwnerApplication = slot.RestoringOwnerApplication;
        log.LogEffectRestore(slot.ProviderId, context, "attempt");
        slot.RestoringOwnerApplication = ownerApplication ?? slot.Layers.FirstOrDefault(layer => layer.OwnerApplication != null)?.OwnerApplication;
        try
        {
            if (ownerApplication != null)
            {
                if (world.worldHost == true)
                {
                    kernelOwner.QueueOwnerLayerRestore(slot, context, ownerApplication);
                    log.LogEffectRestore(slot.ProviderId, context, "queued");
                }
                else log.LogEffectRestore(slot.ProviderId, context, "not-dispatched");
            }
            else
            {
                callback(context);
                log.LogEffectRestore(slot.ProviderId, context, "returned");
            }
            return true;
        }
        catch (Exception error)
        {
            slot.RetryRestore = callback;
            log.LogEffectRestore(slot.ProviderId, context, "threw");
            log.LogObserverFailed(log.HasGate(slot.ProviderId) ? slot.ProviderId : Identity.Id,
                "effect-restore-failed retained " + reason + " handle=" + slot.Handle.GetRawText()
                + " command=" + (commandId ?? slot.CommandId) + " " + error.GetType().Name + ": " + error.Message);
            return false;
        }
        finally
        {
            slot.RestoringOwnerApplication = previousOwnerApplication;
            if (restoringLayer == null) slot.RestoreInProgress = false;
            else restoringLayer.RestoreInProgress = false;
        }
    }

    /// <summary>
    /// Ends one effect early because its own receiver did something the card named in `end_on`. Nothing is
    /// dispatched for the event that ends it: the trigger the card named is read for its subject and its binding
    /// alone, and an effect ends as its own kind of ending rather than as a cancellation the author did not ask
    /// for. Called once per dispatch, before the walk, so a dispatch that both ends an effect and fires the very
    /// entry point that applied it sees an effect that has already ended.
    /// </summary>
    private void EndEffectOn(Pending pending)
    {
        // A schedule may have revalidated its load-time binding since it queued this pulse. Its current typed
        // capture is authoritative; a control continuation carries no new event subject at all.
        var subject = pending.Schedule is { } schedule ? schedule.EndSubject : pending.EndSubject;
        if (kernelEffects.effects.Count == 0 || subject.Count == 0) return;
        var index = kernelEffects.EndOnIndex(pending.Event.BindingId);
        if (index < 0) return;
        // The copy is the pass's snapshot: an instance a restore callback removes is still in this list and is
        // skipped by EndEffect's own liveness check, while an instance added during the pass is not visited.
        var scratch = kernelEffects.endingScratchPool.Count == 0 ? new List<EffectSlot>() : kernelEffects.endingScratchPool.Pop();
        try
        {
            var slots = kernelEffects.endOnIndexSlots[index];
            for (var i = 0; i < slots.Count; i++) scratch.Add(slots[i]);
            for (var i = 0; i < scratch.Count; i++)
            {
                var slot = scratch[i];
                if (!EndsOnSubject(slot, subject)) continue;
                EndEffect(slot, EffectEndReasons.Expired, CurrentTick);
            }
        }
        finally { scratch.Clear(); kernelEffects.endingScratchPool.Push(scratch); }
    }

    /// <summary>Only the event's declared subject is compared, decoded once at admission. A single member of a
    /// subject set may end the whole effect slot, exactly as a single recipient's ending does; this does not split
    /// one application into per-recipient effects or alter its layer cleanup semantics.</summary>
    private static bool EndsOnSubject(EffectSlot slot, EventSubject subject)
    {
        var entities = slot.Subject;
        for (var index = 0; index < subject.Count; index++)
        {
            var reference = subject.At(index);
            for (var entityIndex = 0; entityIndex < entities.Count; entityIndex++)
            {
                var entity = entities[entityIndex];
                if (entity.Id == reference.Id && entity.WorldEpoch == reference.WorldEpoch
                    && entity.LifeEpoch == reference.LifeEpoch) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Runs one effect layer's action again, inside the kernel's own advance and without dispatching an event: the
    /// frame the card was applied with is re-run through the same handler, with a command identity of its own so a
    /// pulse is a work item the replay ledger can tell apart from the application that opened it. A handler that
    /// refuses leaves the layer to end on its duration alone, which is what a period that nobody has to hold is.
    /// </summary>
    private void PulseEffect(EffectSlot slot)
    {
        var step = slot.Step ?? throw new RuntimeContractException("migration-effect", slot.NodeId);
        var handler = step.Handler ?? throw new RuntimeContractException("missing-handler", slot.BindingId);
        var generation = slot.ProviderGeneration;
        // A period re-runs the same action, so it pays the same cost: the charge is reserved here exactly as the
        // application reserved it, and a pulse that cannot pay it is skipped rather than run for free.
        var hold = executor.ReserveCost(step);
        if (hold is { Reserved: false, Unreleased: 0 }) return;
        var plan = new RuntimeLogPlan { PlanId = slot.PlanId, ResourceId = slot.ResourceId, ResourceRevision = slot.ResourceRevision };
        var inputFrame = slot.InputFrame
            ?? throw new RuntimeContractException("migration-effect-frame", slot.NodeId);
        var applicationId = slot.CommandId;
        var context = new CommandContext(slot.Origin, CurrentTick, slot.CommandPrefix,
            slot.PulseCommandSuffix, ++slot.Pulses,
            new CommandPlan(slot.PlanId, slot.ResourceId, slot.ResourceRevision), slot.NodeId, inputFrame,
            entityInstance, ReportedTickRate, this, slot.ProviderId, generation, slot.EntryId, step.BindingId,
            step.Frames.Row?.Port ?? "", step.Frames.Row, step.Contract);
        context.EffectHandleId = slot.HandleId.IsCast ? slot.HandleId : null;
        context.EffectApplicationId = applicationId;
        context.EffectStacks = slot.Layers.Count;
        context.EffectAddLayer = false;
        var origin = new KernelLog.StepOrigin(slot.Origin, in plan, slot.EntryId);
        CommandResult result;
        if (hold is { Reserved: false })
        {
            result = executor.SettleCost(CommandResult.Rejected(RuntimeAbiCodes.InsufficientCost, slot.NodeId), hold, null);
            result = executor.RefuseWithRows(step, inputFrame, slot.Origin, result);
            LogPulseStepFinished(in origin, step.LogGate, slot.ProviderId, slot.NodeId, slot.BindingId, context, "action", in result,
                effectApplicationId: applicationId);
            if (kernelScheduler.pulseReceipts != null)
                kernelScheduler.pulseReceipts.Add(new CommandReceipt(context.CommandId, slot.Origin.EventId, slot.Origin.CauseId,
                    slot.Origin.RootEventId ?? slot.Origin.EventId, slot.PlanId, slot.ResourceId, slot.ResourceRevision,
                    slot.NodeId, slot.BindingId, WorldEpoch, CurrentTick, result));
            return;
        }
        LogPulseStepStarted(in origin, step.LogGate, slot.ProviderId, slot.NodeId, slot.BindingId, context, "action");
        if (IsOwnerStep(step))
        {
            var refused = kernelOwner.CaptureOwnerPulse(slot, step, context.CommandId, hold);
            if (refused != null) CompleteEffectPulse(slot, step, inputFrame, hold, refused, applicationId, context);
            return;
        }
        var previousCommand = currentCommand;
        BeginCommandInvocation(ref context);
        executor.ArmTypedWrite(step, inputFrame, isHost: true);
        try { inputFrame.RecheckReferences(this, slot.InputLiveness); result = NormalizeInvokedResult(handler(context)); }
        catch (RuntimeContractException error) { result = CommandResult.Rejected(error.Code, CommandResult.TruncateDetail(error.Message)); }
        catch (Exception error) { result = CommandResult.FailedUnknown("handler-exception", CommandResult.TruncateDetail(error.GetType().Name + ": " + error.Message)); }
        finally { currentCommand = previousCommand; }
        try { CompleteEffectPulse(slot, step, inputFrame, hold, result, applicationId, context); }
        finally { EndCommandInvocation(in context); }
    }

    // Both local periods and full holder replies settle in the host advance. Sending a period neither writes
    // a result row nor spends its held cost; ending its effect does not erase a write already confirmed remotely.
    private void CompleteEffectPulse(EffectSlot slot, ResolvedStep step, StepInputs inputFrame,
        KernelExecutor.CostHold? hold, CommandResult result, string applicationId, CommandContext? context = null,
        string? commandId = null)
    {
        var plan = new RuntimeLogPlan { PlanId = slot.PlanId, ResourceId = slot.ResourceId, ResourceRevision = slot.ResourceRevision };
        var origin = new KernelLog.StepOrigin(slot.Origin, in plan, slot.EntryId);
        // A pulse re-runs the same action, so its rows go through the same descriptor the application's did: the
        // receipt reports the rows the ABI accepted, and a malformed row set is the pulse's own `failed + unknown`.
        var rows = executor.PublishRows(step, inputFrame, ref result);
        result = executor.SettleCost(result, hold, rows);
        kernelScheduler.FinalizeDeferredResults(context, result);
        if (context is { } invocation)
            LogPulseStepFinished(in origin, step.LogGate, slot.ProviderId, slot.NodeId, slot.BindingId, invocation, "action", in result,
                effectApplicationId: applicationId);
        else
        {
            var command = StepCommand.Of(commandId!);
            log.LogStepFinished(in origin, step.LogGate, slot.ProviderId, slot.NodeId, slot.BindingId, ref command, "action", in result,
                effectApplicationId: applicationId);
        }
        if (kernelScheduler.pulseReceipts != null)
            kernelScheduler.pulseReceipts.Add(new CommandReceipt(context?.CommandId ?? commandId!, slot.Origin.EventId, slot.Origin.CauseId,
                slot.Origin.RootEventId ?? slot.Origin.EventId, slot.PlanId, slot.ResourceId, slot.ResourceRevision,
                slot.NodeId, slot.BindingId, WorldEpoch, CurrentTick, result));
        if (result.Facts.Count > 0)
        {
            PublishConfirmedFacts(result, slot.ProviderId, slot.ProviderGeneration, slot.Origin.ScopeId,
                CurrentTick, kernelScheduler.pulseEvents!);
        }
    }

    private static string PulseCommandSuffix(ResolvedStep step)
        => step.CommandSuffix[..^1] + ",pulse:";

    private void LogPulseStepStarted(in KernelLog.StepOrigin origin, RuntimeLogGate gate, string provider, string step, string bindingId,
        CommandContext context, string nodeKind)
    {
        if (!log.HasSink || !gate.IsEnabled(RuntimeLogLevel.Trace)) return;
        var command = StepCommand.Of(context.CommandId);
        log.LogStepStarted(in origin, gate, provider, step, bindingId, ref command, nodeKind);
    }

    private void LogPulseStepFinished(in KernelLog.StepOrigin origin, RuntimeLogGate gate, string provider, string step, string bindingId,
        CommandContext context, string nodeKind, in CommandResult result, string? effectApplicationId = null)
    {
        if (!log.HasSink || !gate.IsEnabled(log.StepLevel(result.Status, result.CommitState, result.Code, result.Outputs))) return;
        var command = StepCommand.Of(context.CommandId);
        log.LogStepFinished(in origin, gate, provider, step, bindingId, ref command, nodeKind, in result, effectApplicationId);
    }

    /// <summary>The step a live effect re-runs: its own plan and node, read from the plan the instance belongs to.
    /// The owning entry is retained alongside the action's identity for deferred results and diagnostics. A plan
    /// that unloaded between two ticks ends its effects first, so this answers for every live instance.</summary>
    private ResolvedStep? FindEffectStep(EffectSlot slot)
    {
        if (!plans.Loaded.TryGetValue(slot.PlanId, out var loaded)) return null;
        foreach (var entry in loaded.Plan.Entries)
        {
            if (entry.NodeId != slot.EntryId) continue;
            foreach (var step in entry.Steps)
                if (step.NodeId == slot.NodeId && step.BindingId == slot.BindingId)
                {
                    slot.PulseCommandSuffix = PulseCommandSuffix(step);
                    return step;
                }
        }
        return null;
    }

    /// <summary>Ends every effect one plan owns: a released plan's effects are undone and their handles spent, so
    /// nothing keeps running for a card that is no longer loaded.</summary>
    private void ReclaimEffectPlan(string planId, long tick)
    {
        foreach (var slot in kernelEffects.effects.Values.Where(x => x.PlanId == planId).ToArray())
            EndEffect(slot, EffectEndReasons.Reclaimed, tick);
    }

    private void ReclaimEffectScope(string provider, string scope, long tick)
    {
        foreach (var slot in kernelEffects.effects.Values.ToArray())
            foreach (var entry in slot.Layers.Select(layer => (Layer: layer, Attempt: layer.RestoreAttempt)).Reverse().ToArray())
            {
                var layer = entry.Layer;
                var index = slot.Layers.IndexOf(layer);
                if (index < 0 || layer.RestoreAttempt != entry.Attempt) continue;
                if (layer.SourceProvider == provider && layer.ScopeId == scope)
                {
                    EndEffectLayer(slot, index, EffectEndReasons.Cancelled, tick);
                    continue;
                }
                if (slot.RestoreInProgress || layer.RestoreInProgress) continue;
                for (var extra = layer.AdditionalOwnerApplications.Count - 1; extra >= 0; extra--)
                {
                    var contribution = layer.AdditionalOwnerApplications[extra];
                    if (contribution.SourceProvider != provider || contribution.ScopeId != scope) continue;
                    if (RestoreEffect(slot, EffectEndReasons.Cancelled, 1, contribution.Request,
                        OwnerEffectApplicationId(contribution.Request), layer))
                        layer.AdditionalOwnerApplications.RemoveAt(extra);
                    else layer.RestorePending = true;
                }
            }
    }

    /// <summary>Ends every effect one provider owns: the module is going away, so its native state is restored
    /// before its registration is.</summary>
    private void ReclaimEffectProvider(string provider, long tick)
    {
        foreach (var slot in kernelEffects.effects.Values.Where(x => x.ProviderId == provider).ToArray())
            EndEffect(slot, EffectEndReasons.Reclaimed, tick);
    }

    /// <summary>Ends every effect, at a world boundary or a stop: the handles die with the world either way, and
    /// the modules are told before the table they were cast from is cleared.</summary>
    private void ReclaimEffects(long tick)
    {
        foreach (var slot in kernelEffects.effects.Values.ToArray()) EndEffect(slot, EffectEndReasons.Reclaimed, tick);
        // A new world closes these native lifetimes even if the old provider could not confirm cleanup. This is
        // an explicit abandoned old-world obligation, never a successful cancellation or status-ended fact.
        foreach (var slot in kernelEffects.effects.Values)
            log.LogObserverFailed(log.HasGate(slot.ProviderId) ? slot.ProviderId : Identity.Id, "effect-restore-abandoned world-ended " + slot.CommandId);
        kernelEffects.ClearEffectEndings();
        kernelEffects.effects.Clear(); kernelEffects.effectIdentity.Clear();
    }

    /// <summary>
    /// The per-tick effect pass, run where the other lifetime passes run. Three things happen in one pass, in this
    /// order: an instance whose handle or recipients are gone ends as `reclaimed`, because a native write onto a
    /// life that ended has nothing left to undo; a layer whose period has come runs the action again; and a layer
    /// whose duration has run out ends — on its own for an instance whose layers each keep a clock, and with the
    /// whole instance for the one clock the other reapplies share. The comparison is the same rule the trigger
    /// card's cooldown uses — the charge is a tick and the window is exactly its length — so an effect of N ticks
    /// applied in tick T is over in tick T+N.
    /// </summary>
    private void CleanEffectLifetimes()
    {
        if (kernelEffects.effects.Count == 0) return;
        // The copy is the pass's snapshot: an instance added by a pulse is not cleaned in the pass that started
        // before it existed, and an instance the pass removes is skipped by the same liveness check as before.
        var scratch = kernelEffects.lifetimeScratch;
        try
        {
            foreach (var pair in kernelEffects.effects) scratch.Add(pair.Value);
            for (var index = 0; index < scratch.Count; index++)
            {
                var slot = scratch[index];
                if (!kernelEffects.effects.ContainsKey(slot.Id)) continue;
                var allLayersPending = true;
                for (var layerIndex = 0; layerIndex < slot.Layers.Count; layerIndex++)
                    if (!slot.Layers[layerIndex].RestorePending) { allLayersPending = false; break; }
                if (allLayersPending) continue;
                if (slot.HandleSlot < 0 || kernelEffects.handleSlots.Count <= slot.HandleSlot
                    || kernelEffects.handleSlots[slot.HandleSlot] is not { Active: true } handle || handle.EffectId != slot.Id)
                {
                    // The handle went away under the effect — a provider released it, or the pool dropped it with
                    // the life its native object belonged to. The instance ends with it rather than staying as a
                    // record of something nothing can cancel.
                    EndEffect(slot, EffectEndReasons.Reclaimed, CurrentTick);
                    continue;
                }
                if (slot.Subject.Count != 0 && entityLives.EntityLivenessCode(slot.SubjectLiveness) != null)
                { EndEffect(slot, EffectEndReasons.Reclaimed, CurrentTick); continue; }
                for (var layerIndex = slot.Layers.Count - 1; layerIndex >= 0; layerIndex--)
                {
                    // A restore callback may synchronously cancel another layer in this same scope.
                    if (layerIndex >= slot.Layers.Count) continue;
                    var layer = slot.Layers[layerIndex];
                    if (layer.RestorePending) continue;
                    if (layer.NextPulseTick >= 0 && CurrentTick >= layer.NextPulseTick)
                    {
                        // A period that was slept through is skipped rather than replayed: the next pulse is one
                        // interval after the one that ran, so a long gap costs one re-run and not a burst.
                        layer.NextPulseTick = CurrentTick + slot.Options.Interval!.Value;
                        kernelFlow.AdoptEffectLayerSource(slot, layer);
                        PulseEffect(slot);
                    }
                    if (CurrentTick < layer.ExpiresAtTick) continue;
                    if (slot.Options.IndependentLayers)
                    {
                        var layerIndexNow = slot.Layers.IndexOf(layer);
                        if (layerIndexNow >= 0) EndEffectLayer(slot, layerIndexNow, EffectEndReasons.Expired, CurrentTick);
                        continue;
                    }
                    EndEffect(slot, EffectEndReasons.Expired, CurrentTick);
                    break;
                }
            }
        }
        finally { scratch.Clear(); }
    }

    /// <summary>Migration reconstructs frames at the boundary, then calls this once before the image is committed.
    /// A restored effect therefore enters its first pulse with the same pre-bound liveness cells as a fresh one.</summary>
    private void BindMigratedEffectHotPath(EffectSlot slot, EffectLayer layer)
    {
        if (slot.SubjectLiveness.Length == 0 && slot.Subject.Count != 0)
            slot.SubjectLiveness = kernelEffects.BindEffectSubjectLiveness(slot.Subject);
        layer.InputLiveness = (layer.InputFrame
            ?? throw new RuntimeContractException("migration-effect-frame", slot.NodeId)).BindEntityLiveness(this,
                validate: false);
    }
}

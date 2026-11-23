using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ForgeEngine.Framework;

public sealed record RuntimeIdentity(string Id, string Version, string ApiVersion, string GameBuild);

public sealed record RuntimeLimits
{
    public int MaxEntrypoints { get; init; } = Wire.Limits.MaxEntrypoints;
    public int MaxStepsPerEntrypoint { get; init; } = Wire.Limits.MaxStepsPerEntrypoint;
    public int MaxTotalSteps { get; init; } = Wire.Limits.MaxTotalSteps;
    public int MaxEventsPerTick { get; init; } = Wire.Limits.MaxEventsPerTick;
    public int MaxCommandsPerTick { get; init; } = Wire.Limits.MaxCommandsPerTick;
    public int MaxQueuedEvents { get; init; } = Wire.Limits.MaxQueuedEvents;
    public int MaxCausalDepth { get; init; } = Wire.Limits.MaxCausalDepth;
    /// <summary>Value slots one step's frame may span (its inputs and its result row together), counting one slot
    /// per value the step reads, an event's row index included. A collection port reserves its whole declared
    /// element width on top of this budget instead, and a handle, a resource and a result row are charged to the
    /// plan's frame bytes rather than to this number at all. A plan that needs more is refused with
    /// `frame-slot-budget`. The default 66 is the canonical 32-modifier action's 64 enum/number members plus its
    /// single source and cap values; changing that public declaration and changing this ABI are one contract.</summary>
    public int MaxValueSlotsPerStep { get; init; } = Wire.Limits.MaxValueSlotsPerStep;
    /// <summary>Peak frame storage of one plan: the widest entry's slots at 24 bytes, plus the shared constant
    /// pool's allocated slots and string bytes. Entries execute sequentially and reuse the activation arena;
    /// suspended activations own separate snapshots. A larger peak is refused with `frame-bytes-budget`.
    /// Default pending measurement against game frame time and simultaneous suspended activations.</summary>
    public int MaxDispatchFrameBytes { get; init; } = Wire.Limits.MaxDispatchFrameBytes;
    /// <summary>Rounds one control step may run in a single dispatch (`repeat`, `for_each`, an `interval`
    /// schedule's pulse count). A plan that asks for more is refused with `iteration-budget`.</summary>
    public int MaxControlIterations { get; init; } = Wire.Limits.MaxControlIterations;
    /// <summary>Steps one activation may execute. An activation is one dispatch, one loop round or one schedule
    /// resumption; exceeding this is refused with `dispatch-step-budget`.</summary>
    public int MaxStepExecutionsPerDispatch { get; init; } = Wire.Limits.MaxStepExecutionsPerDispatch;
    /// <summary>Attachments one plan may declare, against `attachment-budget`.</summary>
    public int MaxAttachmentsPerPlan { get; init; } = Wire.Limits.MaxAttachmentsPerPlan;
    /// <summary>Live handles one plan may hold at once, against `handle-budget`.</summary>
    public int MaxLiveHandles { get; init; } = Wire.Limits.MaxLiveHandles;
}

/// <summary>
/// The three answers one reference can get from the session's presence read: it is current, the kernel proved it
/// is not, or the observation could not be made at all. Only `forge.condition.predicate.exists` reads through that
/// read, because only that contract treats "proven gone" as `false`; every other consumer of a reference refuses a
/// stale identity exactly like an unreadable one.
/// </summary>
public enum EntityPresence { Current, Absent, Unknown }

/// <summary>
/// The budgeted world read an `evaluate` handler of a `query` step is given. It is the only way a step reaches
/// the world, it is unavailable to a `pure` step, and every refusal is reported with a code instead of an empty
/// answer, so a caller can never mistake "exhausted" for "nothing there". The session itself owns no counters:
/// the kernel's single query budget does.
/// </summary>
public readonly struct RuntimeQuerySession
{
    private readonly RuntimeKernel? kernel;
    private readonly bool available;
    internal RuntimeQuerySession(RuntimeKernel kernel, string nodeId, bool available)
    { this.kernel = kernel; NodeId = nodeId; this.available = available; }
    /// <summary>False inside a `pure` step, whose contract forbids world access.</summary>
    public bool Available => available && kernel != null;
    public string NodeId { get; }
    /// <summary>The world this session reads, as the kernel's own epoch. A provider that resolves a reference to a
    /// native instance of a level needs it for the same reason every other read does: an instance of another world
    /// is not the instance the reference names. Zero when no kernel is behind the session, which is also when no
    /// read succeeds.</summary>
    public long WorldEpoch => kernel?.WorldEpoch ?? 0;
    /// <summary>Reads one entity's current observation through the kernel's query budget. The returned code is
    /// the kernel's own reason (`entity-observer-unavailable`, `entity-query-tick-budget`, a stale identity) or
    /// `query-authority` when this step may not read the world at all.</summary>
    public bool TrySnapshot(EntityReference entity, out RuntimeEntitySnapshot? snapshot, out string code)
    {
        snapshot = null;
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        if (kernel!.TryInspectEntitySnapshot(entity, out snapshot, out code)) return true;
        kernel.QueryRefused(code);
        return false;
    }
    /// <summary>
    /// Reads a whole explicit candidate set in one budgeted call, for a selector that ranks or filters the set it
    /// was handed instead of answering about a single reference. This is <see cref="TrySnapshot"/> over many
    /// references: the same kernel read, the same refusal code recorded through <see cref="RuntimeKernel.QueryRefused"/>,
    /// and a partial answer is still a refusal — a selector that ranked half a candidate set would silently drop the
    /// targets it never saw, so the code the kernel answered with is reported and the caller rejects the step. The
    /// caller decides what a partial result means for its own contract; nothing here filters, truncates or reorders.
    /// </summary>
    public bool TrySnapshots(IReadOnlyList<EntityReference> references, out RuntimeEntityQueryResult result, out string code)
    {
        result = null!;
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var query = kernel!.InspectEntities(references);
        if (query.Status == "rejected") { code = query.Code; kernel.QueryRefused(code); return false; }
        result = query; code = query.Code; return true;
    }
    /// <summary>
    /// Answers whether a reference still points at something real, in three states instead of two. A live
    /// snapshot is <see cref="EntityPresence.Current"/>; a world that has ended or a life the owning resolver no
    /// longer knows is <see cref="EntityPresence.Absent"/> — the proof the reference is gone, reported as an
    /// answer rather than a refusal, because `forge.condition.predicate.exists` is defined as `false` there; and
    /// an observation the kernel could not make is <see cref="EntityPresence.Unknown"/>, recorded as a refusal
    /// exactly like <see cref="TrySnapshot"/>'s, so the step is rejected and never handed a false that looks
    /// observed. The read is charged to the same per-tick query budget as every other read.
    /// </summary>
    public EntityPresence TryPresence(EntityReference entity, out string code)
    {
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return EntityPresence.Unknown; }
        if (kernel!.TryInspectEntitySnapshot(entity, out _, out code)) return EntityPresence.Current;
        // Two codes mean the kernel proved the reference is not current: the world epoch has moved on, or the
        // resolver that owns the kind says the instance is gone. Every other failure — no observer for the kind, a
        // callback that threw, an observation that came back null or about another life — is a read that could not
        // be made, and stays a refusal.
        if (code is "stale-world" or "stale-entity") return EntityPresence.Absent;
        kernel.QueryRefused(code);
        return EntityPresence.Unknown;
    }
    /// <summary>
    /// Enumerates every entity of one kind the provider that owns that kind currently tracks. This is the only way
    /// a step learns about entities it was not handed: there is no world scan behind it, no truncation when the
    /// answer is longer than the budget (the call is refused instead), and a kind no registered provider exposes
    /// is refused with `entity-candidates-unavailable` rather than answered with an empty list. The read is charged
    /// to the same per-tick query budget as <see cref="TrySnapshot"/>, and the returned code is either the
    /// provider's own refusal or `query-budget` once the budget is spent.
    /// </summary>
    public bool TryCandidates(string kind, out IReadOnlyList<EntityReference> candidates, out string code)
        => TryCandidates(kind, default, out candidates, out code);
    /// <summary><see cref="TryCandidates(string, out IReadOnlyList{EntityReference}, out string)"/> narrowed to one
    /// zone, one sphere or both before the answer is counted, so the per-query ceiling is held against the
    /// entities inside the scope and not against the whole level.</summary>
    public bool TryCandidates(string kind, EntityCandidateScope scope, out IReadOnlyList<EntityReference> candidates, out string code)
    {
        candidates = Array.Empty<EntityReference>();
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var result = kernel!.EnumerateEntityCandidates(kind, scope);
        if (result.Status == "rejected") { code = result.Code; kernel.QueryRefused(code); return false; }
        candidates = Array.AsReadOnly(result.Items.Select(item => item.Reference).ToArray());
        code = result.Code; return true;
    }
    /// <summary>
    /// Reads the zone one entity stands in, from the provider that owns the entity's kind — the region half of
    /// <see cref="TrySnapshot"/>, which carries no zone of its own. An entity the provider answered about is a
    /// successful read even when no zone came back: "this entity stands outside every zone" is a fact about the
    /// world, and a selector that read it as a failure would drop the entity instead of excluding it from the zone
    /// it named. A read that could not be made — a kind whose owner registered no responder, an entity the owner
    /// cannot place right now, a responder that failed — is refused with the kernel's own code or the provider's,
    /// so an unreadable entity is never answered as one that stands nowhere. The read is charged to the same
    /// per-tick query budget as every other read.
    /// </summary>
    public bool TryZone(EntityReference entity, out EntityReference? zone, out string code)
    {
        zone = null;
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var answer = kernel!.ZoneOfEntity(entity);
        zone = answer.Zone; code = answer.Code;
        if (answer.Answered) return true;
        kernel.QueryRefused(code);
        return false;
    }
    /// <summary>
    /// <see cref="TryZone"/> over one explicit set, charged to the tick budget as one query of the set's size, the
    /// way <see cref="TrySnapshots"/> charges a snapshot set. True only when every entity was answered; otherwise
    /// <paramref name="code"/> is the set's refusal or the first entity's, recorded as the step's refusal, so a
    /// filter never answers about the part of the set it could read. With <paramref name="skipGone"/> an entity
    /// that is provably gone (`stale-world`, `stale-entity`) is not a refusal: its answer stays unanswered, which
    /// places it in no zone, as a selector's candidate set reads it.
    /// </summary>
    internal bool TryZones(IReadOnlyList<EntityReference> entities, List<EntityZoneResolution> answers, out string code,
        bool skipGone = false)
    {
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        if (kernel!.TryZonesOfEntities(entities, answers, out code))
        {
            var refused = answers.Find(answer => !answer.Answered && !(skipGone && answer.Code is "stale-world" or "stale-entity"));
            if (refused == null) return true;
            code = refused.Code;
        }
        kernel.QueryRefused(code);
        return false;
    }
    /// <summary><see cref="TryZoneArea"/> over one explicit set, under the same rules as <see cref="TryZones"/>.</summary>
    internal bool TryZoneAreas(IReadOnlyList<EntityReference> entities, List<EntityAreaResolution> answers, out string code)
    {
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        if (kernel!.TryAreasOfEntities(entities, answers, out code))
        {
            var refused = answers.Find(answer => !answer.Answered);
            if (refused == null) return true;
            code = refused.Code;
        }
        kernel.QueryRefused(code);
        return false;
    }
    /// <summary>
    /// Reads which area of its own zone one entity stands in, from the provider that owns the entity's kind. It is
    /// the second half of <see cref="TryZone"/> and follows the same rules: the answer is the index the level's
    /// zone lists the area under, an entity a provider cannot place is refused with the kernel's own code or the
    /// provider's, and a kind whose owner registered no area responder is refused by name. The read is charged to
    /// the same per-tick query budget as every other read.
    /// </summary>
    public bool TryZoneArea(EntityReference entity, out int? area, out string code)
    {
        area = null;
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var answer = kernel!.AreaOfEntity(entity);
        area = answer.Area; code = answer.Code;
        if (answer.Answered) return true;
        kernel.QueryRefused(code);
        return false;
    }
    /// <summary>
    /// Enumerates every resource of one kind the provider that owns that kind currently holds. This is the resource
    /// half of <see cref="TryCandidates"/> and follows the same rules: no world scan behind it, a kind no provider
    /// owns refused with `resource-unavailable` instead of answered with an empty list, and the read charged to the
    /// same per-tick query budget. A provider that cannot answer for something it listed is a refusal too, reported
    /// with the code it refused by.
    /// </summary>
    public bool TryResources(string kind, out IReadOnlyList<ResourceRef> resources, out string code)
    {
        resources = Array.Empty<ResourceRef>();
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var result = kernel!.EnumerateResources(kind);
        if (result.Status == "rejected") { code = result.Code; kernel.QueryRefused(code); return false; }
        resources = result.References;
        code = result.Code; return true;
    }
    /// <summary>Reads one named resource through the owner of its kind. The id is resolved by the provider that
    /// owns the kind and by nobody else; an id it does not have — and a kind no provider owns at all — is
    /// `resource-unavailable`, never a null handed back as if the world had answered.</summary>
    public bool TryResource(string kind, string resourceId, out ResourceRef? reference, out string code)
    {
        reference = null;
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var result = kernel!.ResolveResources(kind, new[] { resourceId });
        if (result.Status == "rejected") { code = result.Code; kernel.QueryRefused(code); return false; }
        reference = result.References[0];
        code = result.Code; return true;
    }
    /// <summary>
    /// Resolves an explicit set of references in one budgeted call, for a selector that ranks or filters resources
    /// it was handed instead of asking about one. Every reference must still be there: a partial answer is a
    /// refusal, reported with its code, because a list that quietly dropped what had gone away would be read as a
    /// world that never held it.
    /// </summary>
    public bool TryResourceRefs(IReadOnlyList<ResourceRef> references, out IReadOnlyList<ResourceRef> resolved, out string code)
    {
        resolved = Array.Empty<ResourceRef>();
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        ArgumentNullException.ThrowIfNull(references);
        var result = kernel!.ResolveResourceRefs(references);
        if (result.Status == "rejected") { code = result.Code; kernel.QueryRefused(code); return false; }
        resolved = result.References;
        code = result.Code; return true;
    }
    /// <summary>
    /// Whether one effect handle still names an effect instance the kernel is running. This is the one read that
    /// looks at the kernel's own record rather than at the world: an instance whose duration ran out, whose
    /// cancellation was asked for, whose receiver's status was dispelled, whose plan was released or whose world
    /// ended has given its slot back, so the handle answers `false` — that is the question the row asks, and it is
    /// never refused for being stale. A value of another handle kind, or of a lifetime other than the one the
    /// consuming port declares, is not this row's value at all and is refused by name exactly as any other port
    /// refuses one. A `pure` step has no session and is refused `query-authority` like every other read here.
    /// </summary>
    public bool EffectActive(RuntimeHandle effectHandle, string handleKind, string lifetime)
    {
        if (!Available) { kernel?.QueryRefused(RuntimeAbiCodes.QueryAuthority); return false; }
        return kernel!.EffectHandleActive(effectHandle, handleKind, lifetime);
    }
}

/// <summary>Registration-time mount matcher: given one `attachments[]` target and the entity an event is about,
/// answer whether the plan's behaviour applies to that entity. A provider registers one per attachment kind it
/// owns; the kernel calls it before dispatching a trigger, never after a command has started.</summary>
public delegate bool AttachmentMatcher(string? category, string reference, EntityReference subject);

/// <summary>Registration-time mount matcher for a kind whose target is not an entity: given one `attachments[]`
/// target, answer whether the behaviour applies in this world. The kernel asks it once per dispatch, before any
/// subject is read, so it judges the events that carry no subject at all (a world, timer or pulse event) exactly
/// like the ones that do. A provider registers one per such kind it owns.</summary>
public delegate bool AttachmentScopeMatcher(string? category, string reference);

/// <summary>
/// One attachment kind's matcher, as the provider that owns the kind registers it. A kind whose target is the
/// event's own subject registers <see cref="BySubject"/>; a kind whose target names no entity registers
/// <see cref="ByScope"/> instead, and the kernel then never looks for a subject to hand it. Exactly one of the
/// two is set, which the factories are the only way to build: a kind that declared both would leave the kernel
/// guessing which question to ask, and one that declared neither could never match anything.
/// </summary>
public sealed record AttachmentMatcherRegistration
{
    private AttachmentMatcherRegistration(AttachmentMatcher? subject, AttachmentScopeMatcher? scope,
        Func<string, EquipmentHolderMatcher>? holder = null)
    { Subject = subject; Scope = scope; Holder = holder; }

    /// <summary>The matcher of a kind that is matched against the event's own subjects.</summary>
    public static AttachmentMatcherRegistration BySubject(AttachmentMatcher matcher,
        Func<string, EquipmentHolderMatcher>? holder = null)
        => new(matcher ?? throw new ArgumentNullException(nameof(matcher)), null, holder);

    /// <summary>The matcher of a kind that is judged from the mount target alone, with no event subject.</summary>
    public static AttachmentMatcherRegistration ByScope(AttachmentScopeMatcher matcher)
        => new(null, matcher ?? throw new ArgumentNullException(nameof(matcher)));

    /// <summary>Set only for a subject-matched kind; the kernel asks a subject registration this and never
    /// <see cref="Scope"/>.</summary>
    public AttachmentMatcher? Subject { get; }
    /// <summary>Set only for a subject-free kind; the kernel asks a scope registration this once per dispatch,
    /// including the dispatches of events that carry no subject.</summary>
    public AttachmentScopeMatcher? Scope { get; }
    /// <summary>Optional equipment-holder relation, bound to the mount reference once during plan loading.
    /// Direct equipment events continue to use <see cref="Subject"/>.</summary>
    public Func<string, EquipmentHolderMatcher>? Holder { get; }
}

public sealed record EntityReference(string Id, long WorldEpoch, long LifeEpoch);
public sealed record BindingSupport(string BindingId, string Verification, IReadOnlyList<string> RequiredPermissions);
public delegate CommandResult CommandHandler(CommandContext context);

/// <summary>One recipient's step of a multi-target action (<see cref="CommandContext.EachTarget"/>): the row at
/// <paramref name="index"/> is already begun for <paramref name="target"/>; the step commits, reads back, writes the
/// row's columns and ends it through <paramref name="rows"/>.</summary>
public delegate void TargetStep<TState>(ref TState state, EntityReference target, int index, RowWriter rows);

/// <summary>The read-only boundary for an `evaluate` binding. Deliberately narrower than
/// <see cref="CommandContext"/> — no world, causal or authority information — because a node
/// (selector/condition/modifier) can be re-evaluated on demand and must stay side-effect free. The world itself is
/// only ever reached through <see cref="Query"/>; <see cref="Actors"/> and <see cref="Relations"/> are the frozen
/// context of the trigger event being dispatched, delivered whole instead of being looked up again.</summary>
public sealed class EvaluationContext
{
    internal EvaluationContext(string nodeId, JsonElement parameters, JsonElement inputs, RuntimeQuerySession query,
        RuntimeActorContext actors, RuntimeFactionRelations relations, long tick,
        Func<EntityReference, object?> entityInstance, double? ticksPerSecond = null, RuntimeKernel.StepInputs? values = null)
    {
        typedInputs = values;
        NodeId = nodeId; this.parameters = parameters; this.inputDocument = inputs; Query = query;
        this.actors = actors; Relations = relations; Tick = tick; this.entityInstance = entityInstance;
        this.ticksPerSecond = ticksPerSecond;
    }

    /// <summary>The kernel's own evaluation of one step, with the actor roles read from the dispatched event only if
    /// the evaluator asks for them. An evaluator does not keep its context, so the kernel hands one step the same
    /// context on every evaluation, refilled here.</summary>
    internal static EvaluationContext For(EvaluationContext? reused, string nodeId, RuntimeQuerySession query,
        RuntimeKernel kernel, RuntimeEvent dispatched, IReadOnlyList<EntityReference> subjects, RuntimeFactionRelations relations,
        long tick, Func<EntityReference, object?> entityInstance, double? ticksPerSecond, RuntimeKernel.StepInputs values)
    {
        var context = reused ?? new(nodeId, default, default, query, null!, relations, tick, entityInstance, ticksPerSecond, values);
        context.NodeId = nodeId; context.Query = query; context.Relations = relations; context.Tick = tick;
        context.entityInstance = entityInstance; context.ticksPerSecond = ticksPerSecond; context.typedInputs = values;
        context.actors = null; context.actorKernel = kernel; context.actorEvent = dispatched; context.actorSubjects = subjects;
        return context;
    }

    /// <summary>The identity of the trigger event this step is evaluated for; null for a context built without one.</summary>
    internal string? EventId => actorEvent?.EventId;
    private Func<EntityReference, object?> entityInstance;
    private double? ticksPerSecond;
    private RuntimeKernel.StepInputs? typedInputs;
    private RuntimeActorContext? actors;
    private RuntimeKernel? actorKernel;
    private RuntimeEvent? actorEvent;
    private IReadOnlyList<EntityReference>? actorSubjects;
    private RuntimeKernel.StepInputs Values => typedInputs ?? throw new RuntimeContractException("query-frame-unavailable", "The query has no resolved value frame.");
    public long? Integer(HandlerSlot slot) => Values.Integer(slot);
    public double? Number(HandlerSlot slot) => Values.Number(slot);
    public string? Text(HandlerSlot slot) => Values.Text(slot);
    public (double X, double Y, double Z)? Vector(HandlerSlot slot) => Values.Vector(slot);
    public bool TryEntity(HandlerSlot slot, out EntityReference entity) => Values.TryEntity(slot, -1, out entity);
    public bool TryResource(HandlerSlot slot, out ResourceRef resource) => Values.TryResource(slot, out resource);
    /// <summary>A collection's element count, or -1 for a missing, null, malformed or single value.</summary>
    public int Count(HandlerSlot slot) => Values.Count(slot);
    /// <summary>One element of a resource collection.</summary>
    public bool TryResource(HandlerSlot slot, int index, out ResourceRef resource) => Values.TryResource(slot, index, out resource);
    /// <summary>A handle input as its value; false where the slot carries no handle.</summary>
    public bool TryRuntimeHandle(HandlerSlot slot, out RuntimeHandle handle) => Values.TryHandle(slot, -1, out handle);
    /// <summary>One input or parameter cell by name as the kernel holds it, for the kernel's own rows: false when the
    /// context was built from documents or carries nothing under that name.</summary>
    internal bool TryInputCell(string port, out CommandValue cell)
    {
        cell = default;
        return typedInputs != null && typedInputs.TryCell(port, out cell);
    }
    internal bool TryParameterCell(string parameter, out CommandValue cell)
    {
        cell = default;
        return typedInputs != null && typedInputs.TryParameterCell(parameter, out cell);
    }
    /// <summary>How many input positions the step's wiring has, in the order the input document lists them; -1 when the
    /// context was built from documents.</summary>
    internal int InputPortCount => typedInputs?.PortCount ?? -1;
    /// <summary>The input cell at one wiring position; false when that input carries nothing.</summary>
    internal bool TryInputCellAt(int position, out CommandValue cell)
    {
        cell = default;
        return typedInputs != null && typedInputs.TryCellAt(position, out cell);
    }
    public string NodeId { get; private set; }
    private JsonElement parameters;
    private JsonElement inputDocument;
    public JsonElement Parameters => typedInputs?.Parameters ?? parameters;
    public JsonElement Inputs => typedInputs?.Json ?? inputDocument;
    /// <summary>The input on <paramref name="port"/>, which a plan must have wired: an absent or null input refuses the
    /// read as `missing-field`.</summary>
    public JsonElement RequiredInput(string port)
        => Inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new RuntimeContractException("missing-field", port);
    /// <summary>The simulation tick this evaluation belongs to: the advancing machine's own current tick, the same
    /// value the scheduler books a dispatch at. A row that answers with a duration answers in ticks of this clock
    /// and never reads a game clock of its own, so a host and a replica describe the same span in the same unit —
    /// which is the unit the catalog declares (`unit: tick`) and the only one a plan can compare against.</summary>
    public long Tick { get; private set; }
    /// <summary>
    /// The rate the running world's ticks are counted at, in ticks per second: the host read
    /// <c>1/Time.fixedDeltaTime</c> when it began the world (contract §3.2 时间单位) and the kernel handed the
    /// number over with the rest of the frozen context. A unit declared in ticks converts through this one value,
    /// because a row that carried its own rate would answer in a second time base. An evaluation of a world whose
    /// rate was never reported refuses the read by name instead of converting at a rate the world never declared.
    /// </summary>
    public double TicksPerSecond => ticksPerSecond ?? throw new RuntimeContractException("runtime-not-ready",
        "The world's tick rate is only known once the host reports it for the running world.");
    /// <summary>The budgeted world read of a `query` step; unavailable — every read refused with
    /// `query-authority` — inside a `pure` step, whose contract keeps it out of the world entirely.</summary>
    public RuntimeQuerySession Query { get; private set; }
    /// <summary>The explicit actor roles the dispatched trigger event carries, read through the one
    /// <see cref="RuntimeActorRoles"/> table. A role the event does not carry is absent; a contract that does not
    /// allow that absence is refused with `actor-missing` before the handler runs.</summary>
    public RuntimeActorContext Actors => actors ??= actorKernel!.ActorContextOf(actorEvent!, actorSubjects!);
    /// <summary>The bounded directed faction relations the world currently exposes. Reading a relation over an
    /// actor the event does not carry is refused by name — no role is inferred from another, and no rule is
    /// invented for a faction pair the world says nothing about.</summary>
    public RuntimeFactionRelations Relations { get; private set; }
    /// <summary>
    /// The native object one live reference names, asked of the provider that owns the reference's kind. This is
    /// how a step reaches an entity another module owns — an enemy's damage limb, a map object's own component —
    /// without holding that module's table: the owner registered the lookup with its resolver, the kernel checks
    /// the reference is current, and an entity that is gone, a kind nobody registered or a reference the owner
    /// does not recognize answers null rather than an object that is not there.
    /// </summary>
    public object? EntityInstance(EntityReference reference) => entityInstance(reference);
}

/// <summary>Writes every output port of the resolved contract into <paramref name="outputs"/>; the kernel checks each
/// written value against its port before a consumer reads it. Neither argument may be kept after the call.</summary>
public delegate void EvaluatorHandler(EvaluationContext context, EvaluationOutputs outputs);

/// <summary>
/// One value a handler reads from its command frame. A shape creates the token while the module is being declared
/// and fills its numeric address when that module is registered. A handler keeps the token and hands it directly to
/// <see cref="CommandContext"/>; dispatch therefore reads an integer offset and never looks a port name up.
/// </summary>
public sealed class HandlerSlot
{
    internal enum Sources : byte { Input, Parameter }
    internal readonly record struct OffsetAdjustment(int CountParameter, int MinimumCount, int Stride);

    internal HandlerSlot(HandlerShape owner, string name, Sources source, string? group = null, string? member = null)
    {
        Owner = owner;
        Name = name;
        Source = source;
        Group = group;
        Member = member;
    }

    internal HandlerShape Owner { get; }
    internal string Name { get; }
    internal Sources Source { get; }
    internal string? Group { get; }
    internal string? Member { get; }
    internal int Offset { get; private set; } = -1;
    internal int Stride { get; private set; }
    internal int CountParameter { get; private set; } = -1;
    internal int MinimumCount { get; private set; }
    internal ValueKind ValueKind { get; private set; } = ValueKind.Missing;
    internal bool Many { get; private set; }
    internal OffsetAdjustment[] OffsetAdjustments { get; private set; } = Array.Empty<OffsetAdjustment>();
    internal bool Resolved { get; private set; }
    internal bool IsGroup => Group != null;

    internal void Resolve(int offset, ValueKind kind, bool many = false, int stride = 0,
        int countParameter = -1, int minimumCount = 0, OffsetAdjustment[]? offsetAdjustments = null)
    {
        var adjustments = offsetAdjustments ?? Array.Empty<OffsetAdjustment>();
        if (Resolved && (Offset != offset || ValueKind != kind || Many != many || Stride != stride
            || CountParameter != countParameter || MinimumCount != minimumCount
            || !OffsetAdjustments.SequenceEqual(adjustments)))
            throw new RuntimeContractException("shape-port", "One slot cannot describe two layouts: " + Name);
        Offset = offset;
        ValueKind = kind;
        Many = many;
        Stride = stride;
        CountParameter = countParameter;
        MinimumCount = minimumCount;
        OffsetAdjustments = adjustments;
        Resolved = true;
    }
}

/// <summary>
/// One handler's own port set, declared next to the handler and resolved once at registration against the
/// capability its binding implements. Resolution turns each declared name into the integer the frame path uses —
/// an input's offset inside the request frame, an output's position in the capability's output list, a parameter's
/// declaration index — so nothing reads a value by name. The capability graph is the other half of the same
/// contract: a name only one half knows is a registration error, never a silent misread.
/// </summary>
public sealed class HandlerShape
{
    private readonly List<string> inputs = new();
    private readonly List<string> outputs = new();
    private readonly List<string> parameters = new();
    private Dictionary<string, int> resolvedInputs = new(StringComparer.Ordinal);
    private Dictionary<string, int> resolvedOutputs = new(StringComparer.Ordinal);
    private Dictionary<string, int> resolvedParameters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandlerSlot> inputSlots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandlerSlot> parameterSlots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HandlerSlot> groupSlots = new(StringComparer.Ordinal);
    private JsonElement resolvedGraph;
    private bool resolved;

    /// <summary>The value inputs the handler reads, in the order it names them; an `execution` input is not one.</summary>
    public HandlerShape Inputs(params string[] portIds) => Declare(inputs, portIds, "input");

    /// <summary>The capability outputs the handler writes or reads through its result.</summary>
    public HandlerShape Outputs(params string[] portIds) => Declare(outputs, portIds, "output");

    /// <summary>The parameters the handler reads, whether the plan compiled them as constants or promoted them.</summary>
    public HandlerShape Parameters(params string[] parameterIds) => Declare(parameters, parameterIds, "parameter");

    public IReadOnlyList<string> InputPorts => inputs;
    public IReadOnlyList<string> OutputPorts => outputs;
    public IReadOnlyList<string> ParameterIds => parameters;

    /// <summary>The handler keeps something per play of its `presentation` step (<see cref="CommandContext.Play"/>), so
    /// the host names the play on each of its commands. A presentation handler that keeps nothing per play declares
    /// nothing, and its commands carry no play. Declared with the shape itself, before it is registered: plan load reads
    /// it, on the host and on every recipient alike, so a shape cannot be marked after it was resolved. Registration
    /// refuses the mark (<c>shape-port</c>) on a handler whose capability is not a presentation one.</summary>
    public HandlerShape KeysByPlay()
    {
        if (resolved && !keyedByPlay)
            throw new RuntimeContractException("shape-port", "A handler keeps plays from its declaration, not after it registered.");
        keyedByPlay = true;
        return this;
    }
    internal bool KeyedByPlay => keyedByPlay;
    private bool keyedByPlay;

    /// <summary>A stable input token that may be captured before registration. Registration writes its frame
    /// offset once; command dispatch then reads through the token without a name or dictionary lookup.</summary>
    public HandlerSlot InputSlot(string portId)
    {
        var id = RuntimeJson.Text(portId!);
        if (!(inputs.Contains(id))) throw new RuntimeContractException("shape-port", "input " + id);
        if (inputSlots.TryGetValue(id, out var slot)) return slot;
        slot = new HandlerSlot(this, id, HandlerSlot.Sources.Input);
        inputSlots.Add(id, slot);
        if (resolved) ResolveInputSlot(slot, resolvedGraph, RuntimeJson.Rows(resolvedGraph, "inputs"),
            RuntimeJson.Rows(resolvedGraph, "parameters"), resolvedInputs);
        return slot;
    }

    /// <summary>A stable structural/value-parameter token. Parameter declaration indices do not move when a plan
    /// expands variable ports, so this token is valid for both fixed and expanded handler shapes.</summary>
    public HandlerSlot ParameterSlot(string parameterId)
    {
        var id = RuntimeJson.Text(parameterId!);
        if (!(parameters.Contains(id))) throw new RuntimeContractException("shape-port", "parameter " + id);
        if (parameterSlots.TryGetValue(id, out var slot)) return slot;
        slot = new HandlerSlot(this, id, HandlerSlot.Sources.Parameter);
        parameterSlots.Add(id, slot);
        if (resolved) ResolveParameterSlot(slot, RuntimeJson.Rows(resolvedGraph, "parameters"), resolvedParameters);
        return slot;
    }

    /// <summary>
    /// One member of a repeated input port group. The capability fixes the member widths and order at registration;
    /// a plan fixes only the repetition count. The resolved token therefore carries a base, stride and member offset,
    /// and <see cref="CommandContext"/> reaches occurrence N with integer arithmetic alone.
    /// </summary>
    public HandlerSlot InputGroupSlot(string groupId, string memberId)
    {
        var group = RuntimeJson.Text(groupId);
        var member = RuntimeJson.Text(memberId);
        var key = group + "\n" + member;
        if (groupSlots.TryGetValue(key, out var slot)) return slot;
        slot = new HandlerSlot(this, member, HandlerSlot.Sources.Input, group, member);
        groupSlots.Add(key, slot);
        if (resolved) ResolveGroupSlot(slot, resolvedGraph, RuntimeJson.Rows(resolvedGraph, "inputs"),
            RuntimeJson.Rows(resolvedGraph, "parameters"));
        return slot;
    }

    /// <summary>The request-frame offset of a declared input: every value port before it in the resolved contract
    /// occupies its own reserved width, so the offset is fixed by the contract, never by the plan's wiring.</summary>
    public int Input(string portId) => Resolved(resolvedInputs, portId, "input");
    /// <summary>The declared output's position in the capability's output list. A `result` output's row fields are
    /// declared on the port itself (I-CATALOG) and are not addressed here: this is the port's position, not a slot
    /// inside a row.</summary>
    public int Output(string portId) => Resolved(resolvedOutputs, portId, "output");
    /// <summary>The declared parameter's index in the capability's parameter list; the plan's own parameter
    /// descriptor says whether that parameter is a compiled constant or a promoted input.</summary>
    public int Parameter(string parameterId) => Resolved(resolvedParameters, parameterId, "parameter");

    private HandlerShape Declare(List<string> declared, string[] ids, string kind)
    {
        RuntimeJson.Require(!resolved, "shape-port", "A shape is declared before it is resolved.");
        foreach (var id in ids ?? Array.Empty<string>())
        {
            if (!(id != null && !declared.Contains(id))) throw new RuntimeContractException("shape-port", kind + " " + id);
            declared.Add(RuntimeJson.Text(id!));
        }
        return this;
    }

    private static int Resolved(Dictionary<string, int> table, string id, string kind)
    {
        if (id == null || !table.TryGetValue(id, out var value)) throw new RuntimeContractException("shape-port", kind + " " + id);
        return value;
    }

    /// <summary>
    /// Resolves every declared name against one capability graph, once per binding, at registration. A capability
    /// whose ports are expanded per plan (`portGroups`) resolves its fixed ports against the minimum
    /// layout and records any group-width adjustment that precedes them. Group member tokens carry their own
    /// base/stride. Both forms therefore remain integer-only reads in the command path.
    /// </summary>
    internal void Resolve(JsonElement graph)
    {
        // Keep the graph for a token whose static field is first touched after module registration. Returning an
        // already-created token is always legal too; neither case adds work to command dispatch.
        if (!resolved) resolvedGraph = graph.Clone();
        if (inputs.Count + outputs.Count + parameters.Count + groupSlots.Count == 0) { resolved = true; return; }
        var nextInputs = new Dictionary<string, int>(StringComparer.Ordinal);
        var offset = 0;
        foreach (var port in RuntimeJson.Rows(graph, "inputs"))
        {
            var (_, width) = RuntimeGraphContracts.FramePort(port);
            var id = RuntimeJson.Text(port, "id");
            if (inputs.Contains(id))
            {
                if (!(width > 0)) throw new RuntimeContractException("shape-port", "An execution input carries no value: " + id);
                nextInputs[id] = offset;
            }
            offset += width;
        }
        var nextOutputs = Index(RuntimeJson.Rows(graph, "outputs"));
        var nextParameters = Index(RuntimeJson.Rows(graph, "parameters"));
        foreach (var id in inputs) RuntimeJson.Require(nextInputs.ContainsKey(id), "shape-port", "input " + id);
        foreach (var id in outputs) RuntimeJson.Require(nextOutputs.ContainsKey(id), "shape-port", "output " + id);
        foreach (var id in parameters) RuntimeJson.Require(nextParameters.ContainsKey(id), "shape-port", "parameter " + id);
        // One instance may be shared by two bindings; that is only sound while both capabilities place the same
        // declared names at the same addresses, so a second, different resolution is refused rather than applied.
        RuntimeJson.Require(!resolved || Same(resolvedInputs, nextInputs) && Same(resolvedOutputs, nextOutputs)
            && Same(resolvedParameters, nextParameters), "shape-port", "One shape cannot describe two layouts.");
        resolvedInputs = nextInputs; resolvedOutputs = nextOutputs; resolvedParameters = nextParameters; resolved = true;
        var inputRows = RuntimeJson.Rows(graph, "inputs");
        var parameterRows = RuntimeJson.Rows(graph, "parameters");
        foreach (var pair in inputSlots) ResolveInputSlot(pair.Value, graph, inputRows, parameterRows, nextInputs);
        foreach (var pair in parameterSlots) ResolveParameterSlot(pair.Value, parameterRows, nextParameters);
        foreach (var pair in groupSlots) ResolveGroupSlot(pair.Value, graph, inputRows, parameterRows);
    }

    private static void ResolveInputSlot(HandlerSlot token, JsonElement graph, JsonElement[] inputRows,
        JsonElement[] parameterRows, Dictionary<string, int> inputOffsets)
    {
        var inputIndex = Array.FindIndex(inputRows, row => RuntimeJson.Text(row, "id") == token.Name);
        if (inputIndex < 0 || !inputOffsets.TryGetValue(token.Name, out var offset))
            throw new RuntimeContractException("shape-port", "input " + token.Name);
        var adjustments = new List<HandlerSlot.OffsetAdjustment>();
        foreach (var group in PortGroups(graph))
        {
            if (RuntimeJson.Text(group, "side") != "inputs") continue;
            var members = RuntimeJson.Rows(group, "slots", 8);
            var first = RuntimeGraphContracts.GroupFirstPortId(group, members);
            var firstIndex = Array.FindIndex(inputRows, row => RuntimeJson.Text(row, "id") == first);
            var minimum = (int)RuntimeJson.Integer(group.GetProperty("minimum"));
            if (firstIndex < 0 || inputIndex < firstIndex + minimum * members.Length) continue;
            var parameter = Array.FindIndex(parameterRows,
                row => RuntimeJson.Text(row, "id") == RuntimeJson.Text(group, "parameter"));
            if (!(parameter >= 0)) throw new RuntimeContractException("shape-port", "input group count " + RuntimeJson.Text(group, "parameter"));
            adjustments.Add(new HandlerSlot.OffsetAdjustment(parameter, minimum,
                members.Sum(member => RuntimeGraphContracts.FramePort(member).Width)));
        }
        var port = inputRows[inputIndex];
        token.Resolve(offset, HandlerValueKind(port), RuntimeGraphContracts.Many(port),
            offsetAdjustments: adjustments.Count == 0 ? null : adjustments.ToArray());
    }

    private static void ResolveParameterSlot(HandlerSlot token, JsonElement[] parameterRows,
        Dictionary<string, int> parameterOffsets)
    {
        if (!(parameterOffsets.TryGetValue(token.Name, out var index))) throw new RuntimeContractException("shape-port", "parameter " + token.Name);
        token.Resolve(index, HandlerValueKind(parameterRows[index]));
    }

    private static void ResolveGroupSlot(HandlerSlot token, JsonElement graph, JsonElement[] inputs,
        JsonElement[] parameters)
    {
        var group = PortGroups(graph).FirstOrDefault(row =>
            RuntimeJson.Text(row, "id") == token.Group && RuntimeJson.Text(row, "side") == "inputs");
        if (!(group.ValueKind == JsonValueKind.Object)) throw new RuntimeContractException("shape-port", "input group " + token.Group);
        var members = RuntimeJson.Rows(group, "slots", 8);
        var memberIndex = Array.FindIndex(members, row => RuntimeJson.Text(row, "id") == token.Member);
        if (!(memberIndex >= 0)) throw new RuntimeContractException("shape-port", "input group member " + token.Group + "." + token.Member);
        var first = RuntimeGraphContracts.GroupFirstPortId(group, members);
        var firstIndex = Array.FindIndex(inputs, row => RuntimeJson.Text(row, "id") == first);
        if (!(firstIndex >= 0)) throw new RuntimeContractException("shape-port", "input group " + token.Group);
        var baseOffset = 0;
        for (var index = 0; index < firstIndex; index++) baseOffset += RuntimeGraphContracts.FramePort(inputs[index]).Width;
        var memberOffset = 0;
        for (var index = 0; index < memberIndex; index++) memberOffset += RuntimeGraphContracts.FramePort(members[index]).Width;
        var stride = members.Sum(member => RuntimeGraphContracts.FramePort(member).Width);
        var countName = RuntimeJson.Text(group, "parameter");
        var countParameter = Array.FindIndex(parameters, row => RuntimeJson.Text(row, "id") == countName);
        if (!(countParameter >= 0)) throw new RuntimeContractException("shape-port", "input group count " + countName);
        token.Resolve(baseOffset + memberOffset, HandlerValueKind(members[memberIndex]),
            stride: stride, countParameter: countParameter,
            minimumCount: (int)RuntimeJson.Integer(group.GetProperty("minimum")));
    }

    /// <summary>`portGroups` is an optional graph field: most capability rows repeat nothing, and those resolve
    /// against their fixed ports alone.</summary>
    private static JsonElement[] PortGroups(JsonElement graph)
        => graph.TryGetProperty("portGroups", out _) ? RuntimeJson.Rows(graph, "portGroups", 2) : Array.Empty<JsonElement>();

    private static ValueKind HandlerValueKind(JsonElement declaration)
    {
        var type = RuntimeGraphContracts.FrameValueType(RuntimeJson.Text(declaration, "type"));
        return type == "enum" ? RuntimeGraphContracts.Many(declaration) ? ValueKind.Integer : ValueKind.String
            : RuntimeFrames.KindOf(type);
    }

    private static Dictionary<string, int> Index(JsonElement[] ports)
    {
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < ports.Length; index++) table[RuntimeJson.Text(ports[index], "id")] = index;
        return table;
    }

    private static bool Same(Dictionary<string, int> a, Dictionary<string, int> b)
        => a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var value) && value == x.Value);
}

/// <summary>A provider declaration and its executable bodies. Registration metadata has one owner.</summary>
public sealed record RuntimeModule(string ApiVersion, string ApiFingerprint, ProviderDeclaration Declaration,
    IReadOnlyDictionary<string, CommandHandler> Handlers,
    IReadOnlyDictionary<string, Func<EntityReference, bool>>? EntityResolvers = null)
{
    /// <summary>Each handler's own port set, keyed by handler name — command handlers and evaluators alike. The
    /// registry resolves every declared shape once and rejects a supplied handler without one (`missing-shape`), the
    /// same exact-set rule the handler and evaluator tables already follow.</summary>
    public IReadOnlyDictionary<string, HandlerShape> Shapes => Declaration.Shapes;
    public IReadOnlyList<BindingSupport> BindingSupport => Declaration.BindingSupport;
    /// <summary>The Thunderstore package this provider ships in (`Owner-Name`, no version). A provider row's
    /// `dependencies` pins (`Owner-Name-x.y.z`) are checked at registration against the packages of the providers
    /// already registered, so this is the one place a package is named; null for a provider no pin can name.</summary>
    public string? Package => Declaration.Package;
    /// <summary>The same registration without the bindings named in <paramref name="withheld"/> (a binding's id or its
    /// capability id), and without any handler, evaluator or effect restore that only those bindings named: the kernel
    /// accepts exactly the bodies the kept bindings use. Every capability row stays declared, so the contract keeps its
    /// one owner while no binding implements it here and a plan that needs one is refused as `binding-unavailable`.
    /// An empty set returns this registration itself.</summary>
    public RuntimeModule Without(IReadOnlyCollection<string> withheld)
    {
        if (withheld.Count == 0) return this;
        var source = Declaration;
        bool Withheld(ProviderBinding binding) => withheld.Contains(binding.Id) || withheld.Contains(binding.CapabilityId);
        var kept = source.Bindings.Where(binding => !Withheld(binding)).ToArray();
        var dropped = source.Bindings.Where(Withheld).Select(binding => binding.Handler)
            .Except(kept.Select(binding => binding.Handler), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, T> Keep<T>(IReadOnlyDictionary<string, T> bodies) => bodies.Where(pair => !dropped.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var declaration = new ProviderDeclaration(source.Id, source.Version, source.Package, source.Dependencies.ToArray()) { Kind = source.Kind }
            .Declare(source.Capabilities.Cast<object>()).Bind(kept);
        return this with
        {
            Declaration = declaration,
            Handlers = Keep(Handlers),
            Evaluators = Keep(Evaluators),
            EffectRestores = EffectRestores is { } restores ? Keep(restores) : null
        };
    }
    /// <summary>Optional read-only targeting snapshots, owned by the same registered entity namespace.</summary>
    public IReadOnlyDictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>? EntityObservers { get; init; }
    /// <summary>Optional allocation-free position and life reads, one per kind this provider also observes in full;
    /// a kind without one is answered from its snapshot observer instead.</summary>
    public IReadOnlyDictionary<string, EntityPoseObserver>? EntityPoseObservers { get; init; }
    /// <summary>Optional native-instance lookups, owned by the same registered entity namespace. The input is whatever
    /// native object the owning provider documents; any other object must return null rather than guess.</summary>
    public IReadOnlyDictionary<string, Func<object, EntityReference?>>? EntityInstanceResolvers { get; init; }
    /// <summary>Optional native-instance lookups by reference, owned by the same registered entity namespace: the
    /// one object a live reference of this kind names, or null where that entity is gone or the reference is not
    /// this provider's. It is the only door a handler of another module has onto a module's own entities — the
    /// owner registers it beside the resolver it already owns for the kind, and no private table is exposed.</summary>
    public IReadOnlyDictionary<string, Func<EntityReference, object?>>? EntityInstances { get; init; }
    /// <summary>`evaluate` binding handlers, keyed by handler name. Most modules register none; this
    /// is a normal empty default, not a compatibility shim.</summary>
    public IReadOnlyDictionary<string, EvaluatorHandler> Evaluators { get; init; } = EmptyEvaluators;
    private static readonly IReadOnlyDictionary<string, EvaluatorHandler> EmptyEvaluators = new Dictionary<string, EvaluatorHandler>();
    /// <summary>What a module does when a kernel-managed effect ends, keyed by the handler name of the binding that
    /// applied it — the same key the handler table uses. Registering one is what allows a plan to put an `effect`
    /// block on that action at all: a duration nobody would be called back for is refused when the plan loads, so
    /// an effect can never outlive the state it was applied to. Most modules register none.</summary>
    public IReadOnlyDictionary<string, EffectRestoreHandler>? EffectRestores { get; init; }
    /// <summary>The candidate entities this module's own entity kinds expose to a `query` step, keyed by the kind
    /// its resolver already owns (`gtfo.enemy`). Discovery belongs to the provider that tracks the instances, so
    /// the kernel never scans a world: it hands the provider's own list back under the query budget. One kind has
    /// exactly one candidate source, and the source answers only with references of its own kind.</summary>
    public IReadOnlyDictionary<string, Func<IReadOnlyList<EntityReference>>>? EntityCandidates { get; init; }
    /// <summary>The entity kinds whose ends this module reports itself, each one a kind its own
    /// <see cref="EntityResolvers"/> already owns. For a reported kind the kernel stops asking the resolver every tick
    /// whether a held entity is still current: a schedule, numeric lease, owned handle, native handle, effect or
    /// deferred result that names an entity of the kind ends when the module calls
    /// <see cref="RuntimeModuleHandle.EntityEnded"/> for that entity's life, and only then.
    ///
    /// The declaration is a promise about every way a life of the kind can end. Death, despawn, destruction,
    /// collection by the game, leaving the session and the end of the world each have to reach
    /// <see cref="RuntimeModuleHandle.EntityEnded"/>, and the resolver has to answer false for that life from the same
    /// moment on. A path that ends a life without reporting it leaves everything that holds the entity running until
    /// its own time limit or the world ends, because nothing asks again. A life that continues (a revive that keeps the
    /// same life epoch, a reload that keeps the reference) is not an end and is not reported. Kinds not listed here
    /// keep the per-tick resolver poll.</summary>
    public IReadOnlyCollection<string>? EntityEndReports { get; init; }
    /// <summary>The zone one entity of a kind is in, keyed by the entity kind whose resolver this provider already
    /// owns (`gtfo.player` for the package that tracks players, `gtfo.enemy` for the one that tracks enemies). It
    /// is the region half of what a candidate source answers for a kind: the owner of a kind is the only package
    /// that can resolve its references to the native instance the level places, so it is also the only one that can
    /// say which zone that instance stands in. The answer is a zone reference, or null when this provider cannot
    /// place the entity it owns right now — unknown, never "no zone", which is why a reader refuses instead of
    /// treating the entity as outside every zone. Zone identity itself is the level's own coordinates, formatted by
    /// <see cref="RuntimeZones.Reference"/>.</summary>
    public IReadOnlyDictionary<string, Func<EntityReference, EntityReference?>>? EntityZones { get; init; }
    /// <summary>Which area of its own zone one entity of a kind stands in, keyed by the same entity kind the zone
    /// responder above is keyed by and registered by the same provider. The answer is the index the level's zone
    /// lists that area under — the number the author writes on a row's `area` port — or null when this provider
    /// cannot place the entity it owns right now. A provider that answers a zone but not an area leaves every
    /// area question refused, never answered as area zero.</summary>
    public IReadOnlyDictionary<string, Func<EntityReference, int?>>? EntityZoneAreas { get; init; }
    /// <summary>Attachment matchers this module owns, keyed by attachment kind (`map-object`, `gear-block`,
    /// `enemy-type`, `level`). A kind has exactly one owner, and each registration says whether its kind is
    /// matched against the event's own subjects or judged from the mount target alone.</summary>
    public IReadOnlyDictionary<string, AttachmentMatcherRegistration>? AttachmentMatchers { get; init; }
    /// <summary>The resource kinds this module owns, keyed by the kind's own name in the shared resource-kind
    /// table. Discovery follows ownership the same way entity candidates do: one kind has exactly one owner, the
    /// owner answers only with references of its own kind, and a kind nobody registered leaves every read of it
    /// refused rather than empty.</summary>
    public IReadOnlyDictionary<string, RuntimeResourceProvider>? ResourceProviders { get; init; }
    /// <summary>Native-instance lookups that are asked about objects rather than about a kind the caller already
    /// knows: each is handed one native object and answers the reference it currently owns, or null when this
    /// provider does not recognize it. They are asked in registration order, and the first non-null answer wins,
    /// which is what lets three packages that each know their own native types share one entry point
    /// (<see cref="RuntimeKernel.ResolveEntity(object)"/>) without knowing about each other. An object nobody
    /// recognizes is null — the same "not current" answer an absent optional package gives — never an exception.
    /// </summary>
    public IReadOnlyList<ObjectEntityResolver>? ObjectEntityResolvers { get; init; }
    /// <summary>The sessions one `presentation` step of this module's own bindings is addressed to, keyed by the
    /// module's provider id. Presentation writes a screen, so the set is a routing decision only the side that
    /// knows its session and its entities can make: the provider is handed the entity references the step's own
    /// `recipients` port carries and answers the sessions they resolve to. Null — or a step it does not answer for
    /// — is refused with `presentation-recipient` rather than sent to an implicit everyone, which is what keeps one
    /// player's value frame off every other player's machine. An empty list says the recipients are real but none
    /// has a screen (bots): the step is sent to nobody and finishes `presentation-no-viewer`.</summary>
    public IReadOnlyDictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>? PresentationSessions { get; init; }
    /// <summary>The `owner` tier's own routing: which player session holds the equipment one `owner` step of this
    /// module's own bindings is about, keyed by the capability the step implements. A `presentation` step writes a
    /// screen, so its recipient list is answerable before the step runs; an `owner` step writes one instance's
    /// state on the machine that holds it, so the question is about the step's own subject and the provider answers
    /// it from that subject's owner. A null answer means this build cannot name the holder, and the dispatch
    /// refuses the step with `owner-holder` rather than sending it to an implicit everyone.</summary>
    public IReadOnlyDictionary<string, Func<EntityReference, string?>>? OwnerSessions { get; init; }
}

/// <summary>One event a provider publishes: its own payload ports and nothing about an entity behind them. Who the
/// event is about is a payload port like any other value — `self` is the entity the plan's own mount matched at
/// dispatch — so there is no second, event-level subject channel to keep in step with it.</summary>
public sealed record RuntimeEvent(string EventId, string BindingId, long WorldEpoch, long SimulationTick,
    string ScopeId, JsonElement Outputs,
    string? CauseId = null, string? RootEventId = null, int CausalDepth = 0)
{
    private JsonElement outputs = Outputs;
    /// <summary>The payload as the kernel reads it: one cell per value output of the trigger, in the registered
    /// binding's own order (<see cref="RuntimeKernel.BindingView.Outputs"/>). An event raised through
    /// <see cref="EventWriter"/> carries only these; one published as a document gets them when it is admitted.</summary>
    internal CommandValue[]? Cells;
    internal RuntimeKernel.BindingView? CellBinding;
    /// <summary>Set on a fact one client reported about its own player: that player, and the token its machine keeps
    /// the presentation steps it already played under. The host hands the token back with the orders it sends that
    /// player for this event, and to nobody else.</summary>
    internal EntityReference? Reporter;
    internal long PredictionToken;
    /// <summary>The payload as a document. An event written by position has none until a reader asks for it — a
    /// trace, a migration image, a handler that reads the event itself — and is then rendered once, from the cells.</summary>
    public JsonElement Outputs
    {
        get
        {
            if (outputs.ValueKind == JsonValueKind.Undefined && Cells != null) outputs = CellBinding!.RenderEvent(Cells);
            return outputs;
        }
        // A replaced document is another payload: the cells read from the old one do not describe it.
        init { outputs = value; Cells = null; CellBinding = null; }
    }
    /// <summary>The payload document where one exists already, undefined where the event still is its cells only.</summary>
    internal JsonElement RenderedOutputs => outputs;
}

/// <summary>
/// One committed fact of a command: the trigger binding it is published on and its payload. A fact a handler wrote
/// (<see cref="CommandContext.BeginFact"/>) is kept as the values it wrote, by output position, and published from
/// them; <see cref="Outputs"/> is the payload's document, written once and only where one is asked for — the packet
/// that carries an owner's result to the host, a migration image, a trace. A fact that arrived in one of those is
/// its document.
/// </summary>
public sealed class RuntimeFact
{
    private JsonElement outputs;

    /// <summary>A fact as a network packet or a migration image carries it.</summary>
    internal RuntimeFact(string bindingId, JsonElement outputs) { BindingId = bindingId; this.outputs = outputs; }

    /// <summary>A fact as its handler wrote it: the gate of its binding, the sheet's output ids, a copy of the
    /// written cells and the size of the text they render to (what the fact budgets judge).</summary>
    internal RuntimeFact(RuntimeSubscriptionGate gate, string[] ids, CommandValue[] cells, int textBytes)
    { BindingId = gate.BindingId; Gate = gate; Ids = ids; Cells = cells; TextBytes = textBytes; }

    public string BindingId { get; }
    public JsonElement Outputs
        => Cells == null || outputs.ValueKind != JsonValueKind.Undefined ? outputs : outputs = EvaluationOutputs.Render(Ids!, Cells);

    internal readonly RuntimeSubscriptionGate? Gate;
    internal readonly string[]? Ids;
    internal readonly CommandValue[]? Cells;
    internal readonly int TextBytes;
    /// <summary>The size of the payload's text, without writing a document for a fact that has none yet.</summary>
    internal int PayloadBytes => Cells != null ? TextBytes : RawJson.Bytes(outputs).Length;
}

/// <summary>The plan identity every command of one loaded plan carries, held once per plan rather than per command.</summary>
internal readonly record struct CommandPlan(string PlanId, string ResourceId, string ResourceRevision);

/// <summary>The check an owner handler left behind when it answered later, asked once per advance: false until the
/// deadline, true at it. It writes the command's rows through <paramref name="rows"/> — one per explicit recipient,
/// by the rules a handler's own rows follow — and returns their conclusion, or null to keep waiting; rows written
/// before a null are dropped.</summary>
public delegate Outcome? OwnerSettlement<TState>(ref TState state, bool expired, RowWriter rows);

internal abstract class OwnerSettlementCall
{
    internal abstract Outcome? Invoke(bool expired, RowWriter rows);
}

internal sealed class OwnerSettlementCall<TState> : OwnerSettlementCall
{
    private TState state;
    private readonly OwnerSettlement<TState> settle;
    internal OwnerSettlementCall(TState state, OwnerSettlement<TState> settle) { this.state = state; this.settle = settle; }
    internal override Outcome? Invoke(bool expired, RowWriter rows) => settle(ref state, expired, rows);
}

/// <summary>One deferral a handler took. <see cref="Rows"/> is the row-writing check; the kernel that ran the
/// handler then keeps what the rows are written against — the step's row shape and its recipients.</summary>
internal sealed class OwnerDeferralRequest
{
    internal OwnerDeferralRequest(int timeoutTicks, OwnerSettlementCall rows)
    { TimeoutTicks = timeoutTicks; Rows = rows; }
    internal readonly int TimeoutTicks;
    internal readonly OwnerSettlementCall Rows;
    internal ResultRowDescriptor? Row;
    internal EntityReference[]? Targets;
    internal bool Set, HostTier;
}

internal sealed class CommandInvocationState
{
    internal readonly List<DeferredCommandToken> DeferredResults = new();
    internal long Stamp;
    internal OwnerDeferralRequest? OwnerDeferral;
    /// <summary>The loaded step of a presentation command, set by the presentation path after it began the invocation.</summary>
    internal ResolvedStep? Step;
    /// <summary>The play the host named for that presentation command (RuntimeKernel.PlayEnvelope): its identity and the
    /// tick its step ran on.</summary>
    internal long Play;
    internal long PlayTick;
    /// <summary>The typed row and output writing of the invocation in progress, armed by the kernel for its step.</summary>
    internal readonly RowWriteState Rows = new();

    internal void Reset(long stamp)
    {
        Stamp = stamp; OwnerDeferral = null; Step = null; Play = 0; PlayTick = 0; DeferredResults.Clear();
        Rows.Disarm();
    }

    internal void Expire(long stamp)
    {
        if (Stamp != stamp) return;
        Stamp = 0;
        Rows.Disarm();
    }
}

public struct CommandContext
{
    /// <summary>The kernel's own dispatch: the inputs are the values the walk resolved, and the JSON object
    /// <see cref="Inputs"/> reads is written from them the first time a handler asks for it.</summary>
    internal CommandContext(RuntimeEvent origin, long tick, string commandId, CommandPlan plan, string nodeId,
        JsonElement parameters, RuntimeKernel.StepInputs inputs, bool isHost,
        Func<EntityReference, object?> entityInstance, double? ticksPerSecond,
        RuntimeKernel? deferredKernel = null, string deferredProvider = "", long deferredProviderGeneration = 0,
        string deferredEntry = "", string deferredBinding = "", string deferredResultPort = "",
        ResultRowDescriptor? deferredResultDescriptor = null, JsonElement deferredContract = default,
        bool detachedDeferredContext = false)
    {
        this = default;
        this.origin = origin; SimulationTick = tick; this.commandId = commandId; this.plan = plan;
        NodeId = nodeId;
        if (parameters.ValueKind != JsonValueKind.Undefined) inputs.SetParameterDocuments(parameters, default);
        this.inputs = inputs; IsHost = isHost; this.entityInstance = entityInstance;
        this.ticksPerSecond = ticksPerSecond ?? double.NaN;
        DeferredKernel = deferredKernel; DeferredProvider = deferredProvider;
        DeferredProviderGeneration = deferredProviderGeneration; DeferredEntry = deferredEntry;
        DeferredBinding = deferredBinding; DeferredResultPort = deferredResultPort;
        DeferredResultDescriptor = deferredResultDescriptor; DeferredContract = deferredContract;
        DetachedDeferredContext = detachedDeferredContext;
        if (detachedDeferredContext)
        {
            invocation = new CommandInvocationState(); invocationStamp = 1; invocation.Reset(invocationStamp);
        }
    }

    internal CommandContext(RuntimeEvent origin, long tick, string commandPrefix, string commandSuffix, long pulse,
        CommandPlan plan, string nodeId, RuntimeKernel.StepInputs inputs,
        Func<EntityReference, object?> entityInstance, double? ticksPerSecond, RuntimeKernel deferredKernel,
        string deferredProvider, long deferredProviderGeneration, string deferredEntry, string deferredBinding,
        string deferredResultPort, ResultRowDescriptor? deferredResultDescriptor, JsonElement deferredContract)
        : this(origin, tick, "", plan, nodeId, default, inputs, true, entityInstance, ticksPerSecond,
            deferredKernel, deferredProvider, deferredProviderGeneration, deferredEntry, deferredBinding,
            deferredResultPort, deferredResultDescriptor, deferredContract)
    {
        commandId = null; this.commandPrefix = commandPrefix; this.commandSuffix = commandSuffix;
        commandSequence = pulse;
    }

    private Func<EntityReference, object?> entityInstance;
    /// <summary>The handler's own state for this step of this loaded plan (<see cref="ResolvedStep.HandlerState"/>): one object per
    /// step, kept as long as the plan is loaded, so a handler that needs per-step identity reads a field instead of looking the
    /// plan and node up. Only a presentation command carries its step (the invocation the presentation path began); any other
    /// context refuses by name.</summary>
    internal object? StepState
    {
        get => LoadedStep.HandlerState;
        set => LoadedStep.HandlerState = value;
    }
    /// <summary>Which play of this presentation step the command is, as the host's walk named it: the trigger scope and
    /// the candidate (or round) of every loop the step runs in, folded into one number. The same target's play keeps its
    /// number from one command to the next whatever the order of the list it came from.</summary>
    internal long Play { get { _ = PlayingStep; return invocation!.Play; } }
    /// <summary>The host tick this presentation step ran on — later than <see cref="SimulationTick"/> when the step was
    /// resumed after a wait.</summary>
    internal long PlayTick { get { _ = PlayingStep; return invocation!.PlayTick; } }
    /// <summary>Only a step that holds plays (<see cref="ResolvedStep.HoldsPlay"/>) was handed one.</summary>
    private ResolvedStep PlayingStep => LoadedStep is { HoldsPlay: true } step ? step
        : throw new RuntimeContractException("presentation-step", "This step's shape keeps nothing per play.");
    private ResolvedStep LoadedStep => (InvocationActive ? invocation!.Step : null)
        ?? throw new RuntimeContractException("presentation-step", "This command carries no loaded step.");
    /// <summary>The reported rate, or NaN for a world that never reported one (a reported rate is always finite).</summary>
    private double ticksPerSecond;
    private RuntimeKernel.StepInputs inputs;
    private CommandPlan plan;
    /// <summary>The event this command answers; every one of its identity fields is read from it.</summary>
    private RuntimeEvent origin;
    private string? commandId;
    private string? commandPrefix;
    private string? commandSuffix;
    private long commandSequence;
    /// <summary>A dispatched step's identity, composed only when something reads <see cref="CommandId"/>: the
    /// publisher and the step's own half are kept in the two identity fields a pulse command already has, and the
    /// sequence a pulse never takes marks the form, so the context's layout stays the published one.</summary>
    internal void DeferCommandId(in RuntimeKernel.StepCommand command)
    { commandId = null; commandPrefix = command.Provider; commandSuffix = command.Suffix; commandSequence = command.Round is { } round ? DeferredStepSequence - 1 - round : DeferredStepSequence; }
    /// <summary>A deferred step identity, kept in the sequence a pulse (always 1 or more) never takes: -1 outside a loop, and below it
    /// the step's folded loop rounds (`RuntimeKernel.WalkRound`, never negative), so the context's layout stays the published one.</summary>
    private const long DeferredStepSequence = -1;
    private CommandInvocationState? invocation;
    private long invocationStamp;
    internal long InvocationStamp => invocationStamp;
    /// <summary>The row writing of a detached context's own invocation, for the kernel to arm before a read.</summary>
    internal RowWriteState? DetachedRows => DetachedDeferredContext ? invocation?.Rows : null;
    internal bool InvocationActive => invocation != null && invocation.Stamp == invocationStamp && invocationStamp != 0;
    internal void BeginInvocation(CommandInvocationState state, long stamp)
    {
        state.Reset(stamp); invocation = state; invocationStamp = stamp;
    }
    private RuntimeKernel.StepInputs ActiveInputs
        => InvocationActive || DetachedDeferredContext ? inputs
            : throw new RuntimeContractException("command-context-expired", "A command context is valid only for its handler invocation.");
    internal RuntimeEvent DeferredOrigin => origin;
    internal RuntimeKernel? DeferredKernel { get; }
    internal string DeferredProvider { get; }
    internal long DeferredProviderGeneration { get; }
    internal string DeferredEntry { get; }
    internal string DeferredBinding { get; }
    internal string DeferredResultPort { get; }
    internal ResultRowDescriptor? DeferredResultDescriptor { get; }
    internal JsonElement DeferredContract { get; }
    internal bool DetachedDeferredContext { get; }
    internal List<DeferredCommandToken>? DeferredResults => InvocationActive ? invocation!.DeferredResults : null;
    internal void AddDeferredResult(DeferredCommandToken token)
    {
        if (!InvocationActive) throw new RuntimeContractException("deferred-result-context", "A deferred result must start in its own action handler.");
        invocation!.DeferredResults.Add(token);
    }
    public string EventId => origin.EventId;
    public string? CauseId => origin.CauseId;
    public string RootEventId => origin.RootEventId ?? origin.EventId;
    public long WorldEpoch => origin.WorldEpoch;
    public long SimulationTick { get; }
    /// <summary>
    /// The rate the running world's ticks are counted at, in ticks per second: the host read
    /// <c>1/Time.fixedDeltaTime</c> when it began the world (contract §3.2 时间单位) and the kernel handed the
    /// number over with the rest of the command's own context. Every time port a row declares in ticks converts
    /// through this one value, because a row that carried its own rate would write the world in a second time base.
    /// A command of a world whose rate was never reported refuses the read by name instead of converting at a rate
    /// the world never declared.
    /// </summary>
    public double TicksPerSecond => !double.IsNaN(ticksPerSecond) ? ticksPerSecond : throw new RuntimeContractException("runtime-not-ready",
        "The world's tick rate is only known once the host reports it for the running world.");
    public long ScheduledTick => origin.SimulationTick;
    public string ScopeId => origin.ScopeId;
    public int CausalDepth => origin.CausalDepth;
    public string CommandId => commandId ??= MaterializeCommandId();
    public string PlanId => plan.PlanId;
    public string ResourceId => plan.ResourceId;
    public string ResourceRevision => plan.ResourceRevision;
    public string NodeId { get; }
    /// <summary>Whether the advance that dispatched this command runs the authoritative world: the fact
    /// <see cref="RuntimeKernel.Advance"/> was called with, recorded at construction. A handler that writes game
    /// state asks this before writing; a presentation handler on a replica reads false and presents only.</summary>
    public bool IsHost { get; }
    public JsonElement Parameters => ActiveInputs.Parameters;
    public JsonElement Inputs => ActiveInputs.Json;

    private string MaterializeCommandId()
    {
        if (commandSequence <= DeferredStepSequence)
            return RuntimeKernel.CommandIdOf(commandPrefix!, origin.EventId, commandSuffix!,
                commandSequence == DeferredStepSequence ? null : DeferredStepSequence - 1 - commandSequence);
        var prefix = commandPrefix ?? throw new InvalidOperationException("The command identity is unavailable.");
        var suffix = commandSuffix ?? "";
        var digits = 1;
        for (var value = commandSequence; value >= 10; value /= 10) digits++;
        return string.Create(prefix.Length + suffix.Length + digits, (prefix, suffix, commandSequence),
            static (destination, state) =>
            {
                state.prefix.AsSpan().CopyTo(destination);
                state.suffix.AsSpan().CopyTo(destination[state.prefix.Length..]);
                state.commandSequence.TryFormat(destination[(state.prefix.Length + state.suffix.Length)..], out _);
            });
    }
    /// <summary>The host-assigned playback identity, shared by every addressed client.</summary>
    public string? SoundInstanceId { get; internal set; }
    internal Action<Action>? SoundRegistration { get; set; }
    /// <summary>Records this machine's stop operation for precisely this playback.</summary>
    public void RegisterSoundPlayback(Action stop)
    {
        _ = ActiveInputs;
        ArgumentNullException.ThrowIfNull(stop);
        if (SoundRegistration == null) throw new RuntimeContractException("presentation-step", "No sound instance was assigned.");
        SoundRegistration(stop);
    }
    /// <summary>The carried value kind, including distinct Missing and Null states.</summary>
    public ValueKind Kind(HandlerSlot slot) => ActiveInputs.Kind(slot ?? throw new ArgumentNullException(nameof(slot)));
    /// <summary>Whether a slot carries a non-null value. Use <see cref="Kind"/> where Missing and Null differ.</summary>
    public bool Present(HandlerSlot slot) => ActiveInputs.Present(slot ?? throw new ArgumentNullException(nameof(slot)));
    /// <summary>A collection's element count, or -1 for a missing, null, malformed or single value. A valid empty
    /// collection returns zero. A port-group member returns the group's resolved repetition count.</summary>
    public int Count(HandlerSlot slot) => ActiveInputs.Count(slot ?? throw new ArgumentNullException(nameof(slot)));
    public bool? Boolean(HandlerSlot slot) => ActiveInputs.Boolean(slot ?? throw new ArgumentNullException(nameof(slot)));
    public bool? Boolean(HandlerSlot slot, int index) => ActiveInputs.Boolean(slot ?? throw new ArgumentNullException(nameof(slot)), index);
    public string? Text(HandlerSlot slot) => ActiveInputs.Text(slot ?? throw new ArgumentNullException(nameof(slot)));
    public string? Text(HandlerSlot slot, int index) => ActiveInputs.Text(slot ?? throw new ArgumentNullException(nameof(slot)), index);
    public long? Integer(HandlerSlot slot) => ActiveInputs.Integer(slot ?? throw new ArgumentNullException(nameof(slot)));
    public long? Integer(HandlerSlot slot, int index) => ActiveInputs.Integer(slot ?? throw new ArgumentNullException(nameof(slot)), index);
    public double? Number(HandlerSlot slot) => ActiveInputs.Number(slot ?? throw new ArgumentNullException(nameof(slot)));
    public double? Number(HandlerSlot slot, int index) => ActiveInputs.Number(slot ?? throw new ArgumentNullException(nameof(slot)), index);
    public (double X, double Y, double Z)? Vector(HandlerSlot slot)
        => ActiveInputs.Vector(slot ?? throw new ArgumentNullException(nameof(slot)));
    public (double X, double Y, double Z)? Vector(HandlerSlot slot, int index)
        => ActiveInputs.Vector(slot ?? throw new ArgumentNullException(nameof(slot)), index);
    public bool TryEntity(HandlerSlot slot, out EntityReference entity)
        => ActiveInputs.TryEntity(slot ?? throw new ArgumentNullException(nameof(slot)), -1, out entity);
    public bool TryEntity(HandlerSlot slot, int index, out EntityReference entity)
        => ActiveInputs.TryEntity(slot ?? throw new ArgumentNullException(nameof(slot)), index, out entity);
    /// <summary>One entity collection of the resolved frame, in the plan's own order and with no dedupe: a repeated reference is two
    /// recipients. A slot with no rows, or a row that is not a valid entity reference, answers an empty array. The kernel
    /// answers a resolved empty set before the handler runs, so a handler that sees none refuses the input.</summary>
    public EntityReference[] Entities(HandlerSlot slot)
    {
        var count = Count(slot);
        if (count <= 0) return Array.Empty<EntityReference>();
        var entities = new EntityReference[count];
        for (var index = 0; index < count; index++)
            if (!TryEntity(slot, index, out entities[index])) return Array.Empty<EntityReference>();
        return entities;
    }
    public bool TryResource(HandlerSlot slot, out ResourceRef resource)
        => ActiveInputs.TryResource(slot ?? throw new ArgumentNullException(nameof(slot)), out resource);
    /// <summary>A resource input's `resourceKind` and `resourceId` as the request carried them, including a
    /// reference that is not a valid one (an unknown kind, an empty id): a handler refuses those with its own code
    /// and still names the recipient in its row. False only where the slot carries no object with a text kind.</summary>
    public bool TryResource(HandlerSlot slot, out string kind, out string id)
        => ActiveInputs.TryResourceFields(slot ?? throw new ArgumentNullException(nameof(slot)), out kind, out id);
    /// <summary>A handle input as its value: comparable, hashable and handed back to the kernel as it is. False
    /// where the slot carries no handle.</summary>
    public bool TryRuntimeHandle(HandlerSlot slot, out RuntimeHandle handle)
        => ActiveInputs.TryHandle(slot ?? throw new ArgumentNullException(nameof(slot)), -1, out handle);
    public bool TryRuntimeHandle(HandlerSlot slot, int index, out RuntimeHandle handle)
        => ActiveInputs.TryHandle(slot ?? throw new ArgumentNullException(nameof(slot)), index, out handle);
    /// <summary>The effect handle the kernel cast for this command, or null where the step's card declared no
    /// `effect` block. The kernel owns the handle, the clock and the cancellation; the handler binds its own native
    /// state to this value and does not mint a handle of its own, which is what keeps one effect from having two
    /// names. It is the handle's value: what a handler keys the native state it binds to the effect by, with nothing
    /// written or allocated to compare it.</summary>
    public RuntimeHandle? EffectHandleId { get; internal set; }
    /// <summary>The layers this effect has after this command: one for a fresh application, up to the card's cap for
    /// a stacking one. Null where the step declared no effect.</summary>
    public int? EffectStacks { get; internal set; }
    /// <summary>Whether this command should write a new layer. False on a refresh of a running effect — including an
    /// application at the stacking cap — where the handler leaves what it wrote alone and the kernel restarts the
    /// clock.</summary>
    public bool EffectAddLayer { get; internal set; }
    /// <summary>The command that opened this effect layer. Refreshes and periodic re-executions keep this
    /// identity while each invocation retains its own <see cref="CommandId"/>. Null for an ordinary action.
    /// Native providers use this identity to update and restore exactly one application, never the origin event.</summary>
    public string? EffectApplicationId { get; internal set; }
    /// <summary>One entity input by its port name: the value the kernel resolved for that port, read without
    /// writing the whole input object where the kernel still holds the values.</summary>
    public EntityReference GetEntityInput(string input)
        => RuntimeJson.EntityRow(ActiveInputs.TryGet(input, out var value) ? value : Inputs.GetProperty(input));
    /// <summary>
    /// The native object one live reference names, asked of the provider that owns the reference's kind: the same
    /// door <see cref="EvaluationContext.EntityInstance"/> opens for an evaluation, offered to a command handler
    /// because an action that writes another module's entity — a push on an enemy limb, a heal on a player — has
    /// to reach that object and must not reach the module's table.
    /// </summary>
    public object? EntityInstance(EntityReference reference)
    {
        _ = ActiveInputs;
        return entityInstance(reference);
    }
    internal RuntimeKernel? OwnerDeferralKernel { get; set; }
    internal OwnerDeferralRequest? OwnerDeferral => InvocationActive ? invocation!.OwnerDeferral : null;

    /// <summary>
    /// Lets an owner handler answer later. The handler has already started a native request whose outcome only the
    /// game shows after the call returns; it returns this value instead of a result, and the holder's kernel then
    /// asks <paramref name="settle"/> once per advance while the command is pending, with a row writer armed for
    /// this step's recipients. <paramref name="settle"/> gets <c>false</c> until the deadline and returns the rows'
    /// conclusion as soon as it can read one, or null to keep waiting; at <paramref name="timeoutTicks"/> it gets
    /// <c>true</c> and must answer, and a null answer is settled as <c>owner-deferral-expired</c>. The terminal
    /// result goes to the host exactly once, the host's graph continues only then, and a world change drops the
    /// command unanswered. The check runs only while a command is pending, for a bounded number of ticks; it exists
    /// because the holder is usually a client, where the game's change callbacks are not observed, so nothing else
    /// reports the outcome. No row object and no document is built by the handler; the kernel renders the rows
    /// once, for the packet that carries the result to the host. The recipients must be entities.
    /// </summary>
    public CommandResult DeferOwnerResult<TState>(int timeoutTicks, TState state, OwnerSettlement<TState> settle)
    {
        var rows = new OwnerSettlementCall<TState>(state, settle ?? throw new ArgumentNullException(nameof(settle)));
        if (OwnerDeferralKernel == null)
            throw new RuntimeContractException("owner-deferral-unsupported", "Only an owner action without handles or effects can answer later.");
        if (!(timeoutTicks >= 1 && timeoutTicks <= RuntimeKernel.MaximumOwnerDeferralTicks)) throw new RuntimeContractException("owner-deferral-timeout", "The deferral timeout must be 1 to " + RuntimeKernel.MaximumOwnerDeferralTicks + " ticks.");
        RuntimeJson.Require(InvocationActive, "command-context-expired", CommandId);
        RuntimeJson.Require(invocation!.OwnerDeferral == null, "owner-deferral-duplicate", CommandId);
        invocation.OwnerDeferral = new OwnerDeferralRequest(timeoutTicks, rows);
        return CommandResult.OwnerDeferred;
    }

    /// <summary>The typed writer for this command's result rows: one row per explicit recipient, written straight
    /// into the step's row region. A handler that writes its rows here returns an <see cref="Outcome"/> and builds
    /// no row object and no document.</summary>
    public RowWriter Rows => InvocationActive ? new RowWriter(invocation!, invocationStamp)
        : throw new RuntimeContractException("command-context-expired", "A command context is valid only for its handler invocation.");

    /// <summary>Where this command writes the outputs it publishes beside its rows — a count, a solved point — by
    /// the output's position in the block's contract (the generated <c>Out…</c> constants): the same answer sheet an
    /// evaluator writes on. An enum output is written by its member name.</summary>
    public EvaluationOutputs Outputs
    {
        get
        {
            if (!InvocationActive || invocation!.Rows.Sheet is not { } sheet)
                throw new RuntimeContractException("command-context-expired", "A command context is valid only for its handler invocation.");
            if (!invocation.Rows.OutputsUsed) { sheet.Begin(); invocation.Rows.OutputsUsed = true; }
            return sheet;
        }
    }

    /// <summary>Begins one committed fact of this command on a trigger binding of the handler's own module, named
    /// by the gate the module already holds for it: the outputs are written by position, exactly as an event's are
    /// (<see cref="RuntimeModuleHandle.BeginEvent"/>), and <see cref="FactWriter.Add"/> hands the fact to the
    /// command. Facts are published after the handler returns, and only for a result that may carry them.</summary>
    public FactWriter BeginFact(RuntimeSubscriptionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (!InvocationActive || !invocation!.Rows.Armed)
            throw new RuntimeContractException("command-context-expired", "A command context is valid only for its handler invocation.");
        if (gate.EventValues is not { } sheet) throw new RuntimeContractException("not-trigger", gate.BindingId);
        sheet.Begin();
        return new FactWriter(invocation, invocationStamp, gate);
    }

    /// <summary>
    /// The one skeleton of a multi-target action: it refuses a host-tier action on a machine that is not the host
    /// (`authority-or-phase`) and a recipient set above <paramref name="maximum"/> (`too-many-targets`) without
    /// writing a row; then, for each explicit recipient in order, it writes a rejected row for a recipient of
    /// another kind (`unsupported-recipient`) or of another world (`stale-entity`), and otherwise begins the row and
    /// calls <paramref name="step"/>, which commits, reads back, writes the row's own columns and ends it. A step
    /// that throws before ending its row leaves that row `failed + unknown` with <paramref name="exceptionCode"/>
    /// and the other recipients still run. The conclusion is the one aggregation rule's
    /// (<see cref="RowWriter.Conclude"/>). <paramref name="state"/> is what the step needs from the handler, passed
    /// by reference so no closure is made; keep the delegate in a field for the same reason.
    /// <paramref name="countColumn"/> names the row's integer column that carries the recipient count (the block's
    /// <c>RowTargetCount</c>): the skeleton then writes it on every row, the ones it ends itself included, and the
    /// step leaves that column alone.
    /// </summary>
    public Outcome EachTarget<TState>(ref TState state, TargetStep<TState> step, string? kind = null,
        int maximum = int.MaxValue, string exceptionCode = ResultCode.NativeCommitException, bool doneWhenSucceeded = false,
        Action<EntityReference, Exception>? report = null, int countColumn = -1)
        => EachTarget(ref state, step, kind, maximum, exceptionCode, doneWhenSucceeded, report, countColumn, presented: false);

    /// <summary>
    /// <see cref="EachTarget"/> for a presentation action, whose rows commit nothing: the skeleton's own rows and
    /// the conclusion follow the presentation rule (<see cref="RowWriter.ConcludePresented"/>), so a step that
    /// throws before ending its row leaves it `failed + none` with <paramref name="exceptionCode"/>. The step ends a
    /// row it presented as `partial + none` (shown, not confirmed) or `succeeded + none`.
    /// </summary>
    public Outcome EachTargetPresented<TState>(ref TState state, TargetStep<TState> step, string? kind = null,
        int maximum = int.MaxValue, string exceptionCode = ResultCode.NativeCommitException,
        Action<EntityReference, Exception>? report = null, int countColumn = -1)
        => EachTarget(ref state, step, kind, maximum, exceptionCode, false, report, countColumn, presented: true);

    private Outcome EachTarget<TState>(ref TState state, TargetStep<TState> step, string? kind, int maximum,
        string exceptionCode, bool doneWhenSucceeded, Action<EntityReference, Exception>? report, int countColumn, bool presented)
    {
        ArgumentNullException.ThrowIfNull(step);
        var rows = Rows;
        var write = invocation!.Rows;
        if (write.HostTier && !IsHost) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var count = rows.Count;
        if (count > maximum) return Outcome.Rejected(ResultCode.TooManyTargets);
        for (var index = 0; index < count && write.ErrorCode == null; index++)
        {
            rows.Begin(index);
            if (!rows.TryTarget(index, out var target))
            {
                write.Fail(RuntimeAbiCodes.ResultRowIncomplete, "The recipient at position " + index + " is not an entity the action skeleton can hand on.");
                break;
            }
            if (kind != null && !EntityKinds.Is(target, kind))
            { rows.EndSynthesized(Status.Rejected, Commit.None, ResultCode.UnsupportedRecipient, countColumn); continue; }
            if (target.WorldEpoch != WorldEpoch)
            { rows.EndSynthesized(Status.Rejected, Commit.None, ResultCode.StaleEntity, countColumn); continue; }
            if (countColumn >= 0) rows.Integer(countColumn, count);
            try { step(ref state, target, index, rows); }
            catch (Exception error) when (write.Open == index)
            {
                rows.EndSynthesized(Status.Failed, presented ? Commit.None : Commit.Unknown, exceptionCode, countColumn);
                report?.Invoke(target, error);
            }
            if (write.Open == index)
                write.Fail(RuntimeAbiCodes.ResultRowIncomplete, "The row at position " + index + " was begun and never ended.");
        }
        return presented ? rows.ConcludePresented() : rows.Conclude(doneWhenSucceeded);
    }

    /// <summary>Starts one target's terminal-result obligation. The returned value is safe to persist.</summary>
    public DeferredCommandToken BeginDeferredResult(EntityReference target)
        => DeferredKernel?.BeginDeferredResult(this, target)
            ?? throw new RuntimeContractException("deferred-result-unsupported",
                "This command is not an authoritative host action invocation.");
}

public static class CommitStates
{
    public const string None = "none";
    public const string Confirmed = "confirmed";
    public const string Unknown = "unknown";
}

public sealed class CommandResult
{
    /// <summary>The committed facts one command may publish: one per recipient of the widest recipient set
    /// (<see cref="Wire.MaxSetWidth"/>). More is a `fact-budget` refusal, never a cut.</summary>
    public const int MaximumFacts = Wire.MaxSetWidth;
    internal static readonly string FactBudgetDetail = "A command can publish at most " + MaximumFacts + " committed facts.";
    public const int MaximumDetailBytes = 64 * 1024;
    private const int MaximumResultBytes = 256 * 1024;

    private CommandResult(string status, string commitState, string code, string detail, JsonElement outputs, IReadOnlyList<RuntimeFact> facts)
    {
        Status = status;
        CommitState = commitState;
        Code = code;
        Detail = detail;
        Outputs = outputs;
        Facts = facts;
    }

    /// <summary>The one value an owner handler returns from <see cref="CommandContext.DeferOwnerResult"/>: the holder
    /// has not settled this command yet, and the kernel will send the terminal result later. It is a marker the
    /// kernel compares by reference and never a result anything publishes.</summary>
    internal static CommandResult OwnerDeferred => ownerDeferred ??= Partial(RuntimeJson.EmptyObject, CommitStates.None);
    private static CommandResult? ownerDeferred;

    public string Status { get; }
    public string CommitState { get; }
    public string Code { get; }
    public string Detail { get; }
    // Set only by kernel settlement; a provider using the same result code cannot inject log detail.
    internal string? CostSettlementLogDetail { get; set; }
    public JsonElement Outputs { get; }
    public IReadOnlyList<RuntimeFact> Facts { get; }

    public static CommandResult Succeeded(JsonElement outputs, params RuntimeFact[] facts)
        => Create(CommandStatuses.Succeeded, CommitStates.Confirmed, ResultCode.Committed, "", outputs, facts);

    public static CommandResult Partial(JsonElement outputs, string commitState, params RuntimeFact[] facts)
        => Create(CommandStatuses.Partial, commitState, ResultCode.Partial, "", outputs, facts);

    public static CommandResult Partial(JsonElement outputs, params RuntimeFact[] facts)
        => Partial(outputs, CommitStates.Confirmed, facts);

    public static CommandResult Rejected(string code, string detail = "")
        => Create(CommandStatuses.Rejected, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    /// <summary>Every multi-recipient action's answer to an explicit recipient set that named nobody (what
    /// <see cref="CommandContext.Entities"/> answers for an empty collection): nobody needed the change, so nothing
    /// failed. Nothing was read and nothing was written; the result is succeeded and confirmed, carries the shared
    /// `no-targets` word as the command code, and writes an empty row set. The word is a command-level reason and never
    /// a row's code.</summary>
    public static CommandResult NoTargets()
        => Create(CommandStatuses.Succeeded, CommitStates.Confirmed, ResultCode.NoTargets, "", ResultRows.Of(Array.Empty<object>()));

    public static CommandResult Failed(string code, string detail = "")
        => Create(CommandStatuses.Failed, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    public static CommandResult FailedUnknown(string code, string detail = "", params RuntimeFact[] facts)
        => Create(CommandStatuses.Failed, CommitStates.Unknown, code, detail, RuntimeJson.EmptyObject, facts);

    public static CommandResult FailedUnknown(JsonElement outputs, string code, string detail = "", params RuntimeFact[] facts)
        => Create(CommandStatuses.Failed, CommitStates.Unknown, code, detail, outputs, facts);

    public static CommandResult Cancelled(string code, string detail = "")
        => Create(CommandStatuses.Cancelled, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    public static CommandResult Expired(string code, string detail = "")
        => Create(CommandStatuses.Expired, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    public static CommandResult Create(string status, string commitState, string code, string detail,
        JsonElement outputs, params RuntimeFact[] facts)
    {
        var safeStatus = RuntimeJson.Text(status);
        var safeCommitState = RuntimeJson.Text(commitState);
        var safeCode = RuntimeJson.Text(code);
        var safeDetail = ValidatedDetail(detail);
        var safeOutputs = SnapshotOutputs(outputs);
        var safeFacts = SnapshotFacts(safeOutputs, facts);
        return new CommandResult(safeStatus, safeCommitState, safeCode, safeDetail, safeOutputs, safeFacts);
    }

    /// <summary>One result with the facts its handler wrote through <see cref="CommandContext.BeginFact"/> after the
    /// ones it returned. The written payloads are the kernel's own renderings, so only the two fact budgets are
    /// checked here.</summary>
    internal static CommandResult WithFacts(CommandResult result, List<RuntimeFact> written)
    {
        RuntimeJson.Require(result.Facts.Count + written.Count <= MaximumFacts, "fact-budget", FactBudgetDetail);
        var facts = new List<RuntimeFact>(result.Facts.Count + written.Count);
        long bytes = RawJson.Bytes(result.Outputs).Length;
        for (var index = 0; index < result.Facts.Count; index++) { facts.Add(result.Facts[index]); bytes += result.Facts[index].PayloadBytes; }
        for (var index = 0; index < written.Count; index++)
        {
            var size = written[index].PayloadBytes;
            RuntimeJson.Require(size <= RuntimeKernel.MaximumEventPayloadBytes, "result-fact-payload-budget", "Committed fact exceeds 64 KiB.");
            bytes += size;
            facts.Add(written[index]);
        }
        RuntimeJson.Require(bytes <= MaximumResultBytes, "result-facts-budget", "Committed facts exceed the 256 KiB result budget.");
        return new CommandResult(result.Status, result.CommitState, result.Code, result.Detail, result.Outputs, facts.AsReadOnly())
        { CostSettlementLogDetail = result.CostSettlementLogDetail };
    }

    private static string ValidatedDetail(string? detail)
    {
        detail ??= "";
        RuntimeJson.Require(Encoding.UTF8.GetByteCount(detail) <= MaximumDetailBytes, "result-detail-budget", "Command detail exceeds 64 KiB.");
        return detail;
    }

    /// <summary>The outputs as the result keeps them: the same text and the same checks a parse of that text makes,
    /// read from the value's own UTF-8 bytes, and a value its handler can no longer dispose.</summary>
    private static JsonElement SnapshotOutputs(JsonElement outputs)
    {
        RuntimeJson.Require(outputs.ValueKind != JsonValueKind.Undefined, "invalid-result-output", "Command outputs must be a JSON value.");
        var text = RawJson.Text(outputs);
        RuntimeJson.Require(text.Length <= RuntimeKernel.MaximumEventPayloadBytes, "result-payload-budget", "Command outputs exceed 64 KiB.");
        return RawJson.Snapshot(outputs, text);
    }

    private static readonly IReadOnlyList<RuntimeFact> NoFacts = new List<RuntimeFact>(0).AsReadOnly();

    private static IReadOnlyList<RuntimeFact> SnapshotFacts(JsonElement outputs, RuntimeFact[] facts)
    {
        if (facts == null) throw new RuntimeContractException("invalid-result-facts", "Fact collection cannot be null.");
        RuntimeJson.Require(facts.Length <= MaximumFacts, "fact-budget", FactBudgetDetail);
        if (facts.Length == 0) return NoFacts;
        var snapshot = new List<RuntimeFact>(facts.Length);
        long bytes = RawJson.Bytes(outputs).Length;
        foreach (var fact in facts)
        {
            if (fact == null) throw new RuntimeContractException("invalid-result-fact", "Committed fact cannot be null.");
            var bindingId = RuntimeJson.Text(fact.BindingId);
            if (fact.Cells != null)
            {
                // A written fact was measured where it was added and is already its own copy.
                bytes += fact.TextBytes;
                RuntimeJson.Require(bytes <= MaximumResultBytes, "result-facts-budget", "Committed facts exceed the 256 KiB result budget.");
                snapshot.Add(fact);
                continue;
            }
            RuntimeJson.Require(fact.Outputs.ValueKind != JsonValueKind.Undefined, "invalid-fact-output", "Committed fact outputs must be a JSON value.");
            var text = RawJson.Text(fact.Outputs);
            var size = text.Length;
            RuntimeJson.Require(size <= RuntimeKernel.MaximumEventPayloadBytes, "result-fact-payload-budget", "Committed fact exceeds 64 KiB.");
            bytes += size;
            RuntimeJson.Require(bytes <= MaximumResultBytes, "result-facts-budget", "Committed facts exceed the 256 KiB result budget.");
            snapshot.Add(new RuntimeFact(bindingId, RawJson.Snapshot(fact.Outputs, text)));
        }
        return snapshot.AsReadOnly();
    }

    internal static string TruncateCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "unknown";
        var text = code!.Trim();
        if (text.Length > 256) text = text[..256];
        foreach (var c in text) if (c <= 31 || c == 127) return "invalid-code";
        return text;
    }

    internal static string TruncateDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return "";
        return detail!.Length <= 4096 ? detail : detail[..4096];
    }
}

internal static class CommandResultRules
{
    internal static bool TryValidate(CommandResult result, out string violation, bool presentation = false)
    {
        violation = "";
        if (result.Status is not (CommandStatuses.Succeeded or CommandStatuses.Partial or CommandStatuses.Rejected
            or CommandStatuses.Failed or CommandStatuses.Cancelled or CommandStatuses.Expired))
        {
            violation = "unknown-status";
            return false;
        }

        switch (result.Status)
        {
            case CommandStatuses.Succeeded:
                if (presentation && result.CommitState == CommitStates.None && result.Facts.Count == 0) return true;
                if (result.CommitState != CommitStates.Confirmed) { violation = "succeeded-requires-confirmed"; return false; }
                return true;
            case CommandStatuses.Partial:
                // A partial result may report an unknown commit — it branches on whether any row committed at all,
                // not on fact count — and it may report `none`, which is what a `presentation` handler reports:
                // presenting is work the handler really did, and none of it commits world state.
                if (result.CommitState is not (CommitStates.Confirmed or CommitStates.Unknown or CommitStates.None))
                { violation = "partial-commit-state"; return false; }
                return true;
            case CommandStatuses.Rejected:
            case CommandStatuses.Cancelled:
            case CommandStatuses.Expired:
                if (result.CommitState != CommitStates.None) { violation = "terminal-status-requires-none"; return false; }
                if (result.Facts.Count != 0) { violation = "terminal-status-cannot-carry-facts"; return false; }
                return true;
            case CommandStatuses.Failed:
                if (result.CommitState is not (CommitStates.None or CommitStates.Unknown)) { violation = "failed-commit-state"; return false; }
                if (result.CommitState == CommitStates.None && result.Facts.Count != 0) { violation = "failed-none-cannot-carry-facts"; return false; }
                return true;
            default:
                violation = "unknown-status";
                return false;
        }
    }
}

public sealed record DispatchResult(string Status, string Code, string EventId);
/// <summary>
/// One `presentation` step the host walked, ready to be executed on each recipient. Presentation writes game
/// visuals, not world state, so the host decides when the step runs and who runs it, and the step's own inputs
/// travel resolved where dynamic; constants are referenced through this exact plan/step and read from the
/// recipient's pre-resolved plan. A recipient executes the same handler on inputs the host already validated, without
/// re-evaluating a step, re-matching a mount or committing anything. <see cref="Recipients"/> is a non-empty set
/// of player sessions; an empty one is refused at dispatch with `presentation-recipient` rather than accepted as
/// a broadcast to nobody.
/// </summary>
public sealed record PresentationOutput(string Provider, string PlanId, string NodeId, string CommandId,
    string BindingId, int StepIndex, IReadOnlyList<string> Recipients, string EventId, string ScopeId,
    long WorldEpoch, long SimulationTick, JsonElement Inputs)
{
    internal KernelLog.StepOrigin Origin { get; init; }
    internal JsonElement? TraceInputs { get; init; }
    internal JsonElement? TraceParameters { get; init; }
    internal bool IsCleanup { get; init; }
    internal long RequestSequence { get; init; }
    /// <summary>The recipient whose own reported fact started this step, and the token it reported the fact under;
    /// null and zero for a step no client's report started.</summary>
    internal string? PredictedBy { get; init; }
    internal long PredictionToken { get; init; }
}
/// <summary>
/// One `owner` step the host walked, ready to be executed on the one machine that holds the thing it changes.
/// An owner write is a real world write whose correct machine is not the host — a weapon's magazine belongs to
/// the inventory that holds it — so the host decides the timing, the inputs and the plan step, and the holder
/// executes the same handler on the same inputs. <see cref="Holder"/> is exactly one player session, or the
/// dispatch would have refused the step with `owner-holder`; there is no broadcast form of this tier.
/// </summary>
public sealed record OwnerOutput(string Provider, string PlanId, string NodeId, string CommandId,
    string BindingId, int StepIndex, string Holder, string EventId, string ScopeId,
    long WorldEpoch, long SimulationTick, JsonElement Inputs)
{
    public ulong HolderIdentity { get; init; }
    public JsonElement Identities { get; init; } = RuntimeJson.EmptyObject;
    public string Operation { get; init; } = "apply";
    public JsonElement Data { get; init; } = RuntimeJson.EmptyObject;
}
public sealed record CommandReceipt(string CommandId, string EventId, string? CauseId, string RootEventId,
    string PlanId, string ResourceId, string ResourceRevision, string NodeId, string BindingId,
    long WorldEpoch, long SimulationTick, CommandResult Result);
public sealed record EventReceipt(string EventId, string Status, string Code);
public sealed record TickResult(int EventsProcessed, int CommandsExecuted, int DeferredEvents,
    IReadOnlyList<CommandReceipt> Commands, IReadOnlyList<EventReceipt> Events)
{
    public IReadOnlyList<ScheduleReceipt> Schedules { get; init; } = Array.Empty<ScheduleReceipt>();
    public IReadOnlyList<StateLeaseReceipt> StateLeases { get; init; } = Array.Empty<StateLeaseReceipt>();
    /// <summary>The trigger options this advance refused, one row per refused entry point. Empty on a replica: a
    /// client runs what the host decided and asks no gate of its own.</summary>
    public IReadOnlyList<TriggerGateReceipt> GateRefusals { get; init; } = Array.Empty<TriggerGateReceipt>();
    /// <summary>The `presentation` steps this advance decided to present, each addressed to the player sessions
    /// the runtime's own recipient resolver named. Empty on a replica, which presents what the host sends rather
    /// than what its own walk decides.</summary>
    public IReadOnlyList<PresentationOutput> Presentations { get; init; } = Array.Empty<PresentationOutput>();
    /// <summary>The `owner` steps this advance decided to dispatch, each addressed to the one player session that
    /// holds the equipment the step names. Empty on a replica, which executes what the host sends rather than what
    /// its own walk decides.</summary>
    public IReadOnlyList<OwnerOutput> OwnerCommands { get; init; } = Array.Empty<OwnerOutput>();
}

public sealed class RuntimeContractException : Exception
{
    public RuntimeContractException(string code, string message) : base(message) { Code = code; }
    public string Code { get; }
}


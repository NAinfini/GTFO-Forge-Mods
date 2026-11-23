using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// One event's envelope, in the fixed order the shared event-envelope fields are declared in. The envelope is
/// the kernel's: a handler never assembles one. Every field has a source — the event identity and its binding
/// as the kernel received them, the tick, scope and world the dispatch runs in, and the causality the queue
/// already recorded when the event was accepted — so nothing here is invented while a value is carried.
///
/// Two envelope fields have no source today, and both stay null rather than carrying a substitute:
/// `correlationId` belongs to the request/reply pairing the event bus owns (R-E1), and `lifeEpoch` is stamped only
/// when the dispatch is about exactly one entity, because a dispatch whose mount accepted none or several has no
/// single life to name. They read as absent, and the bus fills the two where the source appears.
/// </summary>
public sealed class RuntimeEventRow
{
    private static readonly IReadOnlyList<EntityReference> NoTargets = Array.AsReadOnly(Array.Empty<EntityReference>());

    /// <summary><paramref name="targets"/> becomes the row's own: the caller hands over an array nothing else holds.</summary>
    internal RuntimeEventRow(RuntimeEvent value, long sequence, EntityReference[] targets)
    {
        EventId = value.EventId;
        EventType = value.BindingId;
        SchemaVersion = ForgeCore.FormatVersion.Current;
        SimulationTick = value.SimulationTick;
        Sequence = sequence;
        // An admitted event names its roles in its cells; a row made of a handler's own document reads the document.
        var admitted = value.CellBinding != null;
        SourceRef = admitted ? RuntimeActorRoles.Actor(value, "source") : PayloadEntity(value, "source");
        InstigatorRef = admitted ? RuntimeActorRoles.Actor(value, "instigator") : PayloadEntity(value, "instigator");
        TargetRefs = targets.Length == 0 ? NoTargets : Array.AsReadOnly(targets);
        ScopeRef = value.ScopeId;
        WorldEpoch = value.WorldEpoch;
        LifeEpoch = TargetRefs.Count == 1 ? TargetRefs[0].LifeEpoch : null;
        CorrelationId = null;
        CausationId = value.CauseId;
        origin = value;
    }

    private readonly RuntimeEvent origin;

    public string EventId { get; }
    /// <summary>The event's own kind: the binding it was published as, which is what an event port's `schema`
    /// has to name for a value carrying this row to reach that port.</summary>
    public string EventType { get; }
    /// <summary>The payload envelope's shape version, `ForgeCore.FormatVersion.Current`.</summary>
    public string SchemaVersion { get; }
    public long SimulationTick { get; }
    public long Sequence { get; }
    public EntityReference? SourceRef { get; }
    public EntityReference? InstigatorRef { get; }
    /// <summary>The entities this dispatch is about: what the plans that claimed the event accepted as their own
    /// subject, which is the `self` a step reads — not a payload port. One of them is the event's subject, several
    /// are several, and none is a dispatch that matched a mount target without naming an entity.</summary>
    public IReadOnlyList<EntityReference> TargetRefs { get; }
    public string ScopeRef { get; }
    public long WorldEpoch { get; }
    /// <summary>The life epoch of the one subject this dispatch is about, or null where it has none or several:
    /// an episode stamp must not be invented for a dispatch that names nobody in particular.</summary>
    public long? LifeEpoch { get; }
    public string? CorrelationId { get; }
    public string? CausationId { get; }
    /// <summary>The event's own payload ports, unchanged: the payload is a sub-frame of its own, not a value the
    /// envelope flattens. It is the event's document, made when a reader first asks for it.</summary>
    public JsonElement Payload => origin.Outputs;

    /// <summary>One payload port read as an entity, or null where the event does not name it. A port the event
    /// answers null for is the same absence: the envelope never resolves a role a payload did not carry.</summary>
    private static EntityReference? PayloadEntity(RuntimeEvent value, string port)
        => value.Outputs.ValueKind == JsonValueKind.Object && value.Outputs.TryGetProperty(port, out var data)
            && data.ValueKind != JsonValueKind.Null ? RuntimeJson.Entity(data) : null;
}

/// <summary>
/// The event rows of one dispatch: row 0 is the event the dispatch is running for, and every event a handler of
/// that dispatch publishes is appended once the kernel accepted it. A slot carries the row index and nothing
/// else, so carrying an event between steps costs one slot and the envelope is never copied.
///
/// The table belongs to one dispatch, which is what makes its indices mean something: a value naming a row of
/// another dispatch names nothing here and is refused (<c>event-port-kind</c>) rather than resolved against
/// whatever happens to sit at that index — the same rule a stale entity or handle reference follows. Nothing
/// here replays, subscribes or correlates an event; those belong to the event bus.
/// </summary>
public sealed class RuntimeEventRows
{
    /// <summary>The envelope fields in their fixed order: the module-side spelling of the shared declaration,
    /// with the payload last and one event's payload frame a sub-frame of its own.</summary>
    public static IReadOnlyList<string> EnvelopeFields { get; } = Array.AsReadOnly(new[]
    {
        "eventId", "eventType", "schemaVersion", "simulationTick", "sequence", "sourceRef", "instigatorRef",
        "targetRefs", "scopeRef", "worldEpoch", "lifeEpoch", "correlationId", "causationId", "payload"
    });
    private readonly List<RuntimeEventRow> rows = new();

    internal void Append(RuntimeEventRow row) => rows.Add(row);
    public int Count => rows.Count;
    /// <summary>The row one value names, or null where the index names no row of this dispatch.</summary>
    public RuntimeEventRow? Row(long index) => index >= 0 && index < rows.Count ? rows[(int)index] : null;
    /// <summary>True when the index names a row of this dispatch whose own type is the port's schema: an event
    /// value is a reference to one dispatch's event, and the port says which event that has to be.</summary>
    internal bool Matches(long index, string schema) => Row(index) is { } row && row.EventType == schema;
}

/// <summary>
/// One world's reverse parent index: which observed entities hang from which parent. It is built from the
/// `parent` reading of the snapshots the kernel observes — an observer publishes the parent of its own entity,
/// and the kernel derives the other direction — so no observer keeps a second children list that could drift.
/// Every entry is an observation of this world, so none of it outlives one.
/// </summary>
internal sealed class RuntimeChildren
{
    private readonly Dictionary<EntityReference, List<EntityReference>> byParent = new();

    internal void Clear() => byParent.Clear();
    /// <summary>Records one observed edge. A child repeated for the same parent is one child, and a child
    /// observed under a new parent is moved: an entity has one parent, and the newer reading is the live one.</summary>
    internal void Record(EntityReference parent, EntityReference child)
    {
        Unparent(child);
        if (!byParent.TryGetValue(parent, out var children)) byParent[parent] = children = new List<EntityReference>(1);
        if (!children.Contains(child)) children.Add(child);
    }
    /// <summary>Drops a child's edge, for an observation that answers no parent.</summary>
    internal void Unparent(EntityReference child)
    {
        foreach (var children in byParent.Values)
            for (var index = children.Count - 1; index >= 0; index--)
                if (children[index] == child) children.RemoveAt(index);
    }
    /// <summary>The children one parent was observed with, in observation order.</summary>
    internal IReadOnlyList<EntityReference> Children(EntityReference parent)
        => byParent.TryGetValue(parent, out var children) ? children : Array.Empty<EntityReference>();
}

public sealed partial class RuntimeKernel
{

    /// <summary>Makes one dispatch (or none) the one being walked.</summary>
    private void Walking(Pending? pending)
    {
        executor.currentPending = pending;
        executor.currentRows = null;
    }
    private readonly RuntimeChildren children = new();
    /// <summary>
    /// The row table one queued event will be walked with: row 0 is the event itself, stamped from the identity,
    /// binding, tick, scope and causality the kernel accepted it with, and the distinct subjects its claimed work
    /// carries in claim order. Nothing here is read from a handler's output, and nothing is invented: a field with no
    /// source stays absent in the envelope (<see cref="RuntimeEventRow"/>).
    /// </summary>
    internal static RuntimeEventRows EventRowsOf(IReadOnlyList<Work> work, RuntimeEvent value, long sequence)
    {
        List<EntityReference>? subjects = null;
        for (var i = 0; i < work.Count; i++)
            for (var j = 0; j < work[i].Subjects.Count; j++)
            {
                var subject = work[i].Subjects[j];
                if (!(subjects ??= new List<EntityReference>()).Contains(subject)) subjects.Add(subject);
            }
        var rows = new RuntimeEventRows();
        rows.Append(new RuntimeEventRow(value, sequence, subjects == null ? Array.Empty<EntityReference>() : subjects.ToArray()));
        return rows;
    }
    /// <summary>The rows of the dispatch being walked, or null outside one.</summary>
    public RuntimeEventRows? EventRows => executor.EventRows;
    /// <summary>One row of the current dispatch by index, or null where this dispatch has no such row.</summary>
    public RuntimeEventRow? EventRow(long index) => EventRows?.Row(index);
    /// <summary>The envelope of one row as the runtime writes it out: the shared envelope fields in their declared
    /// order, with `targetRefs` an entity array and `payload` the event's own payload frame.</summary>
    public static JsonElement EventRowJson(RuntimeEventRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return RuntimeJson.From(new
        {
            eventId = row.EventId,
            eventType = row.EventType,
            schemaVersion = row.SchemaVersion,
            simulationTick = row.SimulationTick,
            sequence = row.Sequence,
            sourceRef = row.SourceRef,
            instigatorRef = row.InstigatorRef,
            targetRefs = row.TargetRefs,
            scopeRef = row.ScopeRef,
            worldEpoch = row.WorldEpoch,
            lifeEpoch = row.LifeEpoch,
            correlationId = row.CorrelationId,
            causationId = row.CausationId,
            payload = row.Payload
        });
    }
    /// <summary>One snapshot's parent edge, recorded where the snapshot is observed: an observation is the only
    /// thing that says who hangs from whom, so it is also the only thing that may extend the index.</summary>
    private void RecordParent(RuntimeEntitySnapshot snapshot)
    {
        if (snapshot.Parent is { } parent) children.Record(parent, snapshot.Ref);
        else children.Unparent(snapshot.Ref);
    }
    /// <summary>
    /// The children one parent was observed with, each read the way every other entity read is: same currency
    /// check, same per-tick budget, same all-or-nothing answer. A parent with no recorded children is refused
    /// (<c>entity-children-unavailable</c>) rather than answered with an empty set, because nothing observed
    /// says the entity has none; a child that is no longer current makes the answer partial, which a caller that
    /// needs the whole set refuses instead of walking a truncated one.
    /// </summary>
    public RuntimeEntityQueryResult InspectChildren(EntityReference parent)
    {
        ReadThread(); AcceptRuntimeWork();
        var reference = RuntimeEntityReferences.Validate(parent);
        if (StartupState != RuntimeStartupState.Ready || !world.worldStarted) return RejectedEntityQuery("runtime-not-ready");
        try { entityLives.CheckEntity(reference); }
        catch (RuntimeContractException error) { return RejectedEntityQuery(error.Code); }
        var recorded = children.Children(reference).ToArray();
        if (recorded.Length == 0) return RejectedEntityQuery("entity-children-unavailable");
        ResetEntityBudget();
        if (!ChargeEntityQuery(recorded.Length)) return RejectedEntityQuery("entity-query-tick-budget", recorded.Length);
        var context = Lifecycle;
        // Observing a child refreshes its parent edge, so preserve the exact old-parent set this query admitted.
        // A moved child remains in Items as evidence but makes the answer partial; it must never be silently
        // retargeted to the replacement parent or reported as a complete child of the old one.
        var items = new List<RuntimeEntityInspection>(recorded.Length);
        observingEntities = true;
        try { foreach (var child in recorded) items.Add(InspectEntity(child)); }
        finally { observingEntities = false; }
        string? parentCode = null;
        try { entityLives.CheckEntity(reference); }
        catch (RuntimeContractException error) { parentCode = error.Code; }
        var complete = parentCode == null && items.All(item => item.Snapshot?.Parent == reference);
        return new(complete ? "complete" : "partial", complete ? "entities-observed" : parentCode ?? "entity-query-incomplete",
            context, recorded.Length, recorded.Distinct().Count(), items.ToArray());
    }
}

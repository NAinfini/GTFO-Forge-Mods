using System;
using System.Collections.Generic;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    public const int MaximumEntityObservers = 256;
    public const int MaximumEntityReferencesPerQuery = 256;
    /// <summary>Entities one enumeration of a kind may answer with. A provider that tracks more than this is not
    /// truncated to fit: the read is refused with `entity-query-budget`, because a short list would be
    /// indistinguishable from a world that really holds that many fewer.</summary>
    public const int MaximumEntityCandidatesPerQuery = 256;
    public const int MaximumEntityQueriesPerTick = 64;
    public const int MaximumEntityReferencesPerTick = 1024;
    private bool observingEntities;
    private long entityBudgetWorld = -1, entityBudgetTick = -2;
    private int entityQueriesThisTick, entityReferencesThisTick;
    private readonly List<EntityReference> entityQueryReferences = new();
    private readonly HashSet<EntityReference> entityQuerySeen = new();
    private void NoEntityObservationMutation()
        => RuntimeJson.Require(!observingEntities, "entity-observer-mutation",
            "Entity observers and their resolvers cannot mutate or recursively query the kernel.");
    private RuntimeEntityQueryResult RejectedEntityQuery(string code, int requested = 0, int distinct = 0)
        => new("rejected", code, Lifecycle, requested, distinct, Array.Empty<RuntimeEntityInspection>());

    /// <summary>Every entity of one kind the provider that owns that kind currently tracks. The kernel itself
    /// discovers nothing: a kind is answerable only because its own provider registered the candidate source next
    /// to the resolver it already owns for that kind, and every reference that comes back is checked to be of that
    /// kind. A kind nobody exposes is `entity-candidates-unavailable` — an explicit refusal, never an empty world.</summary>
    public RuntimeEntityQueryResult EnumerateEntityCandidates(string kind) => EnumerateEntityCandidates(kind, default);

    /// <summary><see cref="EnumerateEntityCandidates(string)"/> narrowed before it is counted: the provider's whole
    /// table is read, the entities outside <paramref name="scope"/> leave it, and only what is left is held against
    /// the per-query ceiling and charged to the tick. A level that holds more of a kind than one query may answer
    /// with is therefore still answerable for one zone or one sphere of it. Nothing is truncated: a narrowed set
    /// past the ceiling is refused with `entity-query-budget` exactly as an unnarrowed one is, and an entity the
    /// owning provider cannot place or observe refuses the read instead of leaving the set.</summary>
    public RuntimeEntityQueryResult EnumerateEntityCandidates(string kind, EntityCandidateScope scope)
    {
        var code = EnumerateCandidates(kind, out var requested, out var distinct, scope);
        if (code != "entities-enumerated") return RejectedEntityQuery(code, requested, distinct);
        var items = new RuntimeEntityInspection[distinct];
        for (var i = 0; i < distinct; i++) items[i] = new RuntimeEntityInspection(entityCandidateDistinct[i], null, "entity-enumerated");
        entityCandidateDistinct.Clear();
        return new("complete", code, Lifecycle, requested, distinct, items);
    }

    /// <summary>
    /// <see cref="EnumerateEntityCandidates"/> without the result object: the same refusals, validation and budget,
    /// with the distinct references, first occurrence first, written into <paramref name="candidates"/>. The buffer
    /// is replaced only when the enumeration is complete; a refused read leaves it as the caller last saw it, so a
    /// per-frame reader can keep its previous answer and allocate nothing either way.
    /// </summary>
    public bool TryEnumerateEntityCandidates(string kind, List<EntityReference> candidates, out string code)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        code = EnumerateCandidates(kind, out _, out var distinct, default);
        if (code != "entities-enumerated") return false;
        candidates.Clear();
        for (var i = 0; i < distinct; i++) candidates.Add(entityCandidateDistinct[i]);
        entityCandidateDistinct.Clear();
        return true;
    }

    private readonly List<EntityReference> entityCandidateCapture = new(), entityCandidateDistinct = new();
    private readonly HashSet<EntityReference> entityCandidateSeen = new();

    /// <summary>The one enumeration both entries share. On success the distinct references are left in
    /// <see cref="entityCandidateDistinct"/> for the caller to copy out; any other answer is a refusal code.</summary>
    private string EnumerateCandidates(string kind, out int requested, out int distinct, in EntityCandidateScope scope)
    {
        requested = distinct = 0;
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(kind);
        if (!RuntimeJson.IsId(kind)) throw new RuntimeContractException("entity-kind", kind);
        if (StartupState != RuntimeStartupState.Ready || !world.worldStarted) return "runtime-not-ready";
        if (!registry.Tables.EntityCandidates.TryGetValue(kind, out var registered))
            return "entity-candidates-unavailable";
        var source = registered.Candidates;
        ResetEntityBudget();
        if (!EntityBudgetAvailable(0)) return "entity-query-tick-budget";
        var captured = entityCandidateCapture;
        captured.Clear(); entityCandidateDistinct.Clear(); entityCandidateSeen.Clear();
        observingEntities = true;
        try
        {
            // A provider's own table is untyped input like any other: it is copied into kernel storage, and a source
            // that throws, returns nothing or answers with a reference of another kind is refused by name with a
            // result rather than reaching the query step as a short answer or unwinding as an internal failure.
            IReadOnlyList<EntityReference> answered;
            try { answered = source(); }
            catch (Exception error)
            {
                log.LogCallbackFailed("entity-candidates-failed kind=" + kind, error);
                return "entity-candidates-failed";
            }
            if (answered == null) return "entity-candidates-failed";
            for (var i = 0; i < answered.Count; i++) captured.Add(answered[i]);
            for (var i = 0; i < captured.Count; i++)
            {
                var reference = captured[i];
                try
                {
                    RuntimeEntityReferences.Validate(reference);
                    RuntimeJson.Require(OfKind(reference.Id, kind), "entity-candidate-kind", reference.Id);
                }
                catch (RuntimeContractException error) { captured.Clear(); return error.Code; }
            }
            if (NarrowCandidates(kind, captured, scope) is { } refusal) { captured.Clear(); return refusal; }
        }
        finally { observingEntities = false; }
        requested = captured.Count;
        if (requested > MaximumEntityCandidatesPerQuery || !EntityBudgetAvailable(requested))
        { captured.Clear(); return "entity-query-budget"; }
        for (var i = 0; i < requested; i++)
            if (entityCandidateSeen.Add(captured[i])) entityCandidateDistinct.Add(captured[i]);
        distinct = entityCandidateDistinct.Count;
        captured.Clear(); entityCandidateSeen.Clear();
        if (!ChargeEntityQuery(requested)) { entityCandidateDistinct.Clear(); return "entity-query-tick-budget"; }
        return "entities-enumerated";
    }

    /// <summary>Removes, in place and in order, the captured candidates outside <paramref name="scope"/>; null when
    /// the narrowing is complete, otherwise the refusal. The zone is asked of the kind's own zone responder and the
    /// position of its own observer, both looked up once for the whole set. A candidate that ended between the
    /// provider's answer and this read is no candidate and leaves the set; one that exists and cannot be placed or
    /// observed refuses the read, because dropping it would answer a shorter world than the one that is there.</summary>
    private string? NarrowCandidates(string kind, List<EntityReference> captured, in EntityCandidateScope scope)
    {
        if (scope.Zone is { } wanted)
        {
            if (!registry.Tables.EntityZones.TryGetValue(kind, out var responder)) return EntityZoneResolution.UnavailableCode;
            var kept = 0;
            for (var i = 0; i < captured.Count; i++)
            {
                var reference = captured[i];
                EntityReference? zone;
                try { entityLives.CheckEntity(reference); zone = responder.Zone(reference); }
                catch (RuntimeContractException error)
                {
                    if (error.Code is "stale-world" or "stale-entity") continue;
                    return error.Code;
                }
                catch (Exception error)
                {
                    log.LogCallbackFailed(EntityZoneResolution.FailedCode + " kind=" + kind, error);
                    return EntityZoneResolution.FailedCode;
                }
                if (zone == null) return EntityZoneResolution.UnknownCode;
                if (zone == wanted) captured[kept++] = reference;
            }
            captured.RemoveRange(kept, captured.Count - kept);
        }
        if (scope.Within)
        {
            var kept = 0;
            for (var i = 0; i < captured.Count; i++)
            {
                var reference = captured[i];
                if (!TryInspectPose(reference, out var pose)) return "entity-query-incomplete";
                // The shape selectors' own measurement: a sphere the compiler moved here from an "in range" filter
                // keeps exactly what that filter keeps, so moving it changes nothing but where the count happens.
                double distance;
                try { distance = PointMeasure.Distance(pose.X, pose.Y, pose.Z, scope.X, scope.Y, scope.Z); }
                catch (RuntimeContractException error) { return error.Code; }
                if (distance <= scope.Radius) captured[kept++] = reference;
            }
            captured.RemoveRange(kept, captured.Count - kept);
        }
        return null;
    }

    /// <summary>Whether one entity id is routed under <paramref name="kind"/> — <c>RuntimeJson.KindOf(id) == kind</c>
    /// — read in place (<see cref="EntityKinds.Is(string?, string)"/>): a whole-kind enumeration asks it once per
    /// candidate. The empty kind is the one an id without a namespace has, and a kind never holds the separator.</summary>
    internal static bool OfKind(string entityId, string kind)
        => kind.Length == 0 ? entityId.IndexOf(':') <= 0 : kind.IndexOf(':') < 0 && EntityKinds.Is(entityId, kind);

    /// <summary>The query tick's counters, reset at a new world as well as a new tick. The per-query and per-tick
    /// ceilings are both read before anything is charged, so the same call has one answer wherever it is checked.
    /// The resource table's own reads are reset here too: an entity read and a resource read are the same metered
    /// world read and spend the same per-tick budget.</summary>
    private void ResetEntityBudget()
    {
        if (entityBudgetWorld == WorldEpoch && entityBudgetTick == CurrentTick) return;
        entityBudgetWorld = WorldEpoch; entityBudgetTick = CurrentTick;
        entityQueriesThisTick = entityReferencesThisTick = 0;
    }

    private bool EntityBudgetAvailable(int requested)
        => entityQueriesThisTick < MaximumEntityQueriesPerTick
           && entityReferencesThisTick + requested <= MaximumEntityReferencesPerTick;

    /// <summary>Whether one more resource read of <paramref name="requested"/> references fits in this tick's
    /// remaining budget. The count is shared with the entity table's references, so the two can never sum past the
    /// ceiling even when both are read in the same tick.</summary>
    private bool ResourceBudgetAvailable(int requested)
        => entityReferencesThisTick + requested <= MaximumEntityReferencesPerTick;

    private bool ChargeEntityQuery(int requested)
    {
        if (!EntityBudgetAvailable(requested)) return false;
        entityQueriesThisTick++; entityReferencesThisTick += requested;
        return true;
    }

    /// <summary>The explicit actor roles of the trigger event a step is being evaluated for. Delivery, not a second
    /// lookup: four roles come from the event's own payload through the one <see cref="RuntimeActorRoles"/> table,
    /// and `self` is the entity this plan's own mount accepted for the dispatch, carried on the work item.</summary>
    internal RuntimeActorContext ActorContextOf(RuntimeEvent value, IReadOnlyList<EntityReference> subjects)
    {
        // Every step of one dispatch reads the same roles: the context of the event and subjects last read is kept.
        if (ReferenceEquals(value, actorContextEvent) && ReferenceEquals(subjects, actorContextSubjects)) return actorContextValue!;
        executor.CellsOf(value);
        var context = RuntimeActorRoles.FromTriggerEvent(value, subjects);
        (actorContextEvent, actorContextSubjects, actorContextValue) = (value, subjects, context);
        return context;
    }
    private RuntimeEvent? actorContextEvent;
    private IReadOnlyList<EntityReference>? actorContextSubjects;
    private RuntimeActorContext? actorContextValue;

    /// <summary>The bounded directed faction relations the world exposes. The kernel holds one table per world; a
    /// world whose providers published none answers `unknown` for every pair, which is a refusal to claim a
    /// relationship, not a neutral one.</summary>
    private RuntimeFactionRelations relations = new(Array.Empty<RuntimeFactionRelation>());

    /// <summary>Publishes the faction relations of the current world, the way an entity resolver is published: the
    /// package that knows how its own factions relate (player teams, enemy packs) hands the kernel one frozen
    /// bounded table instead of every selector asking it separately. A world change and an unregistering owner both
    /// drop it back to empty, because rules about a world that has ended are not rules about this one.</summary>
    public void SetFactionRelations(RuntimeFactionRelations value)
    {
        Mutable(); ArgumentNullException.ThrowIfNull(value); relations = value;
    }

    public RuntimeEntityQueryResult InspectActor(RuntimeActorContext actors, string role)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(actors);
        RuntimeActorContext.ValidateRole(role);
        var reference = actors.Get(role);
        return reference == null ? RejectedEntityQuery("actor-missing") : InspectEntities(new[] { reference });
    }

    /// <summary>Current observations for explicit refs only; no discovery, truncation or permission grant.</summary>
    public RuntimeEntityQueryResult InspectEntities(IReadOnlyList<EntityReference> references)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(references);
        int requested = references.Count;
        if (requested > MaximumEntityReferencesPerQuery) return RejectedEntityQuery("entity-query-budget", requested);
        // Preserve the caller's input-reading boundary before borrowing kernel scratch storage:
        // a custom IReadOnlyList may itself issue an ordinary query while materializing a reference.
        var captured = new EntityReference[requested];
        for (int i = 0; i < requested; i++) captured[i] = RuntimeEntityReferences.Validate(references[i]);
        try
        {
            if (OpenEntityRead(captured) is { } refused)
                return RejectedEntityQuery(refused, requested, entityQueryReferences.Count);
            int distinct = entityQueryReferences.Count;
            var context = Lifecycle;
            var items = new RuntimeEntityInspection[distinct];
            bool complete = true;
            for (int i = 0; i < distinct; i++)
            {
                items[i] = InspectEntity(entityQueryReferences[i]);
                complete &= items[i].Snapshot != null;
            }
            return new(complete ? "complete" : "partial", complete ? "entities-observed" : "entity-query-incomplete",
                context, requested, distinct, items);
        }
        finally { CloseEntityRead(); }
    }

    /// <summary>The shared middle of every explicit multi-reference read, entered once the caller's references are
    /// copied and validated. The first occurrence wins, in input order, and lands in the kernel's de-duplication
    /// scratch; duplicate requests still spend the original per-query and per-tick reference budget. Answers the
    /// refusal code, or null once the query is charged. The caller closes it with <see cref="CloseEntityRead"/> in
    /// a finally, whatever this answered.</summary>
    private string? OpenEntityRead(IReadOnlyList<EntityReference> captured)
    {
        observingEntities = true;
        for (int i = 0; i < captured.Count; i++)
        {
            var reference = captured[i];
            if (entityQuerySeen.Add(reference)) entityQueryReferences.Add(reference);
        }
        if (StartupState != RuntimeStartupState.Ready || !world.worldStarted) return "runtime-not-ready";
        ResetEntityBudget();
        return ChargeEntityQuery(captured.Count) ? null : "entity-query-tick-budget";
    }

    private void CloseEntityRead()
    {
        entityQueryReferences.Clear(); entityQuerySeen.Clear();
        observingEntities = false;
    }

    /// <summary>Copies and validates the caller's references into kernel scratch before any provider is called. The
    /// caller's list is read before the scratch is borrowed, as in <see cref="InspectEntities"/>; a custom list that
    /// queries again while it is being read gets storage of its own instead of this call's. The caller clears the
    /// answer in its finally.</summary>
    private List<EntityReference> CaptureEntityReferences(IReadOnlyList<EntityReference> references)
    {
        int requested = references.Count;
        var nested = capturingEntitySnapshots;
        var captured = nested ? new List<EntityReference>(requested) : entitySnapshotCapture;
        captured.Clear();
        capturingEntitySnapshots = true;
        try { for (int i = 0; i < requested; i++) captured.Add(RuntimeEntityReferences.Validate(references[i])); }
        finally { capturingEntitySnapshots = nested; }
        return captured;
    }

    /// <summary>
    /// <see cref="InspectEntities"/> without the result object: the same validation, first-occurrence de-duplication,
    /// readiness refusal and budget (one query charged with every requested reference, duplicates included), with
    /// one provider snapshot per distinct reference, in request order, written into <paramref name="snapshots"/> —
    /// null where that entity could not be observed. True only when every distinct reference was observed; otherwise
    /// <paramref name="code"/> is the refusal or <c>entity-query-incomplete</c>, exactly as the result would say.
    /// The buffer is rewritten only once the query is charged. A per-frame reader allocates nothing here.
    /// </summary>
    public bool TryInspectEntitySnapshots(IReadOnlyList<EntityReference> references, List<RuntimeEntitySnapshot?> snapshots,
        out string code)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(references); ArgumentNullException.ThrowIfNull(snapshots);
        int requested = references.Count;
        if (requested > MaximumEntityReferencesPerQuery) { code = "entity-query-budget"; return false; }
        var captured = CaptureEntityReferences(references);
        try
        {
            if (OpenEntityRead(captured) is { } refused) { code = refused; return false; }
            snapshots.Clear();
            bool complete = true;
            for (int i = 0; i < entityQueryReferences.Count; i++)
            {
                complete &= TryInspectEntity(entityQueryReferences[i], out var snapshot, out _);
                snapshots.Add(snapshot);
            }
            code = complete ? "entities-observed" : "entity-query-incomplete";
            return complete;
        }
        finally { captured.Clear(); CloseEntityRead(); }
    }
    private readonly List<EntityReference> entitySnapshotCapture = new();
    private bool capturingEntitySnapshots;

    /// <summary>
    /// <see cref="TryInspectEntitySnapshots"/> for a reader that needs only where each entity stands and whether it
    /// lives: the same validation, de-duplication, refusals, budget and per-entity checks, with one
    /// <see cref="EntityPose"/> per distinct reference written into <paramref name="poses"/> (the default value where
    /// that entity could not be observed). A kind whose provider registered an <see cref="EntityPoseObserver"/> is
    /// read through it and allocates nothing; any other kind is read from its snapshot observer.
    /// </summary>
    public bool TryInspectEntityPoses(IReadOnlyList<EntityReference> references, List<EntityPose> poses, out string code)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(references); ArgumentNullException.ThrowIfNull(poses);
        int requested = references.Count;
        if (requested > MaximumEntityReferencesPerQuery) { code = "entity-query-budget"; return false; }
        var captured = CaptureEntityReferences(references);
        try
        {
            if (OpenEntityRead(captured) is { } refused) { code = refused; return false; }
            poses.Clear();
            bool complete = true;
            for (int i = 0; i < entityQueryReferences.Count; i++)
            {
                complete &= TryInspectPose(entityQueryReferences[i], out var pose);
                poses.Add(pose);
            }
            code = complete ? "entities-observed" : "entity-query-incomplete";
            return complete;
        }
        finally { captured.Clear(); CloseEntityRead(); }
    }

    /// <summary>The typed single-reference read: the provider's already-immutable snapshot directly, so a hot query
    /// does not allocate an inspection, result array or read-only wrapper. Validation, readiness, the observer
    /// reentrancy guard and the budget (one query of one reference) are the ones <see cref="InspectEntities"/> uses;
    /// <paramref name="code"/> is the per-entity code an inspection would carry, or the query's refusal.</summary>
    public bool TryInspectEntitySnapshot(EntityReference reference, out RuntimeEntitySnapshot? snapshot, out string code)
    {
        ReadThread(); AcceptRuntimeWork();
        RuntimeEntityReferences.Validate(reference);
        snapshot = null;
        if (StartupState != RuntimeStartupState.Ready || !world.worldStarted)
        { code = "runtime-not-ready"; return false; }
        ResetEntityBudget();
        if (!ChargeEntityQuery(1))
        { code = "entity-query-tick-budget"; return false; }
        observingEntities = true;
        try { return TryInspectEntity(reference, out snapshot, out code); }
        finally { observingEntities = false; }
    }
    /// <summary>
    /// The zone one entity stands in, answered by the provider that owns the entity's kind. The kernel itself
    /// tests no volume and reads no level: a kind whose owner registered no zone responder is
    /// <see cref="EntityZoneResolution.UnavailableCode"/>, and a responder that answered nothing has answered
    /// that it cannot place the entity it owns right now — unknown, which is
    /// <see cref="EntityZoneResolution.UnknownCode"/> and a refusal, never an entity standing outside every zone.
    /// A read that could not be made is refused instead: a responder that throws a
    /// <see cref="RuntimeContractException"/> is reported with its own code, any other failure as
    /// <see cref="EntityZoneResolution.FailedCode"/>. The answer is checked to be of the zone kind before it is
    /// handed back, because a responder is a provider's own table like any other and a reference of another kind
    /// would route a later read to the wrong owner.
    /// </summary>
    public EntityZoneResolution ZoneOfEntity(EntityReference entity)
    {
        ReadThread(); AcceptRuntimeWork();
        return ZoneOf(entity, charged: true);
    }

    /// <summary>
    /// <see cref="ZoneOfEntity"/> over one explicit set: the set is charged as one query of its own size, as
    /// <see cref="InspectEntities"/> charges a snapshot set, and every entity is then read exactly as a single read
    /// would read it. Reading the set one entity at a time charged one query per entity, so a zone filter over a
    /// level's enemies spent the whole tick's queries on its own. A set past the per-query bound or the tick's
    /// remaining budget is refused before any responder is asked; otherwise <paramref name="answers"/> holds one
    /// resolution per entity, in input order, refusals included.
    /// </summary>
    internal bool TryZonesOfEntities(IReadOnlyList<EntityReference> entities, List<EntityZoneResolution> answers, out string code)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(entities); ArgumentNullException.ThrowIfNull(answers);
        if (!ChargeEntitySet(entities.Count, out code)) return false;
        answers.Clear();
        for (var i = 0; i < entities.Count; i++) answers.Add(ZoneOf(entities[i], charged: false));
        return true;
    }

    /// <summary><see cref="AreaOfEntity"/> over one explicit set, charged once as <see cref="TryZonesOfEntities"/> is.</summary>
    internal bool TryAreasOfEntities(IReadOnlyList<EntityReference> entities, List<EntityAreaResolution> answers, out string code)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(entities); ArgumentNullException.ThrowIfNull(answers);
        if (!ChargeEntitySet(entities.Count, out code)) return false;
        answers.Clear();
        for (var i = 0; i < entities.Count; i++) answers.Add(AreaOf(entities[i], charged: false));
        return true;
    }

    /// <summary>Charges one set read of <paramref name="count"/> references, with the same bounds and codes as a snapshot set.</summary>
    private bool ChargeEntitySet(int count, out string code)
    {
        code = "entities-read";
        if (count > MaximumEntityReferencesPerQuery) { code = "entity-query-budget"; return false; }
        if (StartupState != RuntimeStartupState.Ready || !world.worldStarted) { code = "runtime-not-ready"; return false; }
        ResetEntityBudget();
        if (!ChargeEntityQuery(count)) { code = "entity-query-tick-budget"; return false; }
        return true;
    }

    private EntityZoneResolution ZoneOf(EntityReference entity, bool charged)
    {
        EntityZoneResolution Refused(string code) => EntityZoneResolution.Refused(code);
        try { entityLives.CheckEntity(entity); }
        catch (RuntimeContractException error) { return Refused(error.Code); }
        // A single zone read is one more step of a query rather than a world read of its own — the responder reads
        // the provider's own table — but it is still a query and is counted. A set read was charged as a whole.
        if (charged)
        {
            ResetEntityBudget();
            if (!EntityBudgetAvailable(0)) return Refused("entity-query-tick-budget");
        }
        var kind = RuntimeJson.KindOf(entity.Id);
        if (!registry.Tables.EntityZones.TryGetValue(kind, out var responder)) return Refused(EntityZoneResolution.UnavailableCode);
        EntityReference? zone;
        try { zone = responder.Zone(entity); }
        // A provider that cannot place an entity it owns says so in its own words: the code travels as the
        // provider spelled it, so "not current", "no course node" and "collected mid-read" stay tellable apart
        // from the one answer that is not a failure. A responder that answered nothing said the same thing
        // without its own code, so the kernel names it: the entity is unknown, not outside every zone.
        catch (RuntimeContractException error) { return Refused(error.Code); }
        catch (Exception error)
        {
            log.LogCallbackFailed(EntityZoneResolution.FailedCode + " kind=" + kind, error);
            return Refused(EntityZoneResolution.FailedCode);
        }
        if (zone == null) return Refused(EntityZoneResolution.UnknownCode);
        if (RuntimeJson.KindOf(zone.Id) != ForgeEngine.Framework.EntityKinds.Zone) return Refused(EntityZoneResolution.KindCode);
        if (charged && !ChargeEntityQuery(0)) return Refused("entity-query-tick-budget");
        return EntityZoneResolution.InZone(zone);
    }

    /// <summary>
    /// The area one entity stands in inside its own zone, answered by the provider that owns the entity's kind —
    /// the second half of <see cref="ZoneOfEntity"/>, and the same owner answers both because the course node that
    /// places an entity is the one fact behind either answer. A kind whose owner registered no area responder is
    /// <see cref="EntityAreaResolution.UnavailableCode"/>, a responder that answered nothing has answered that it
    /// cannot place the entity it owns right now — unknown, which is a refusal — and a responder that throws is
    /// reported by its own code or as <see cref="EntityAreaResolution.FailedCode"/>. The answer is checked to be a
    /// non-negative index before it is handed back, because an index is a position in the level's own list.
    /// </summary>
    public EntityAreaResolution AreaOfEntity(EntityReference entity)
    {
        ReadThread(); AcceptRuntimeWork();
        return AreaOf(entity, charged: true);
    }

    private EntityAreaResolution AreaOf(EntityReference entity, bool charged)
    {
        EntityAreaResolution Refused(string code) => EntityAreaResolution.Refused(code);
        try { entityLives.CheckEntity(entity); }
        catch (RuntimeContractException error) { return Refused(error.Code); }
        if (charged)
        {
            ResetEntityBudget();
            if (!EntityBudgetAvailable(0)) return Refused("entity-query-tick-budget");
        }
        var kind = RuntimeJson.KindOf(entity.Id);
        if (!registry.Tables.EntityZoneAreas.TryGetValue(kind, out var responder)) return Refused(EntityAreaResolution.UnavailableCode);
        int? area;
        try { area = responder.ZoneArea(entity); }
        catch (RuntimeContractException error) { return Refused(error.Code); }
        catch (Exception error)
        {
            log.LogCallbackFailed(EntityAreaResolution.FailedCode + " kind=" + kind, error);
            return Refused(EntityAreaResolution.FailedCode);
        }
        if (area == null) return Refused(EntityAreaResolution.UnknownCode);
        if (area.Value < 0) return Refused(EntityAreaResolution.IndexCode);
        if (charged && !ChargeEntityQuery(0)) return Refused("entity-query-tick-budget");
        return EntityAreaResolution.InArea(area.Value);
    }

    /// <summary>
    /// The native object one live reference names, asked of the provider that owns the reference's kind and of
    /// nobody else. It is the one door a handler has onto another module's entities: the owner registers the lookup
    /// beside the resolver it already owns for that kind — one owner per kind, as every other table here — and no
    /// module's private table is exposed. An entity that is gone, a kind nobody registered or a reference this
    /// provider does not recognize answers null, exactly as an absent optional package does; only a lookup that
    /// throws is a failure, because the caller cannot tell a broken table from an entity that is not there.
    /// </summary>
    public object? EntityInstance(EntityReference reference)
    {
        ReadThread(); AcceptRuntimeQuery(); ArgumentNullException.ThrowIfNull(reference);
        var kind = RuntimeJson.KindOf(reference.Id);
        RuntimeJson.Require(kind.Length > 0, "entity-instance", "An entity reference is required.");
        if (StartupState != RuntimeStartupState.Ready || !world.worldStarted) return null;
        if (!registry.Tables.EntityInstances.TryGetValue(kind, out var registered)) return null;
        // The reference is judged current before its object is handed out: a lookup that answered for a previous
        // life would hand a handler a native instance the world no longer has.
        if (!registry.Tables.Resolvers.TryGetValue(kind, out var resolver)) return null;
        bool live;
        try { live = resolver.Resolve(reference); }
        catch (Exception error)
        {
            log.LogCallbackFailedTypeOnly("entity-resolver-failed entity=" + reference.Id, error);
            throw new RuntimeContractException("entity-resolver-failed", reference.Id);
        }
        if (!live) return null;
        try { return registered.Instance(reference); }
        catch (Exception error)
        {
            log.LogCallbackFailedTypeOnly("entity-instance-failed entity=" + reference.Id, error);
            throw new RuntimeContractException("entity-instance-failed", reference.Id);
        }
    }

    /// <summary>Asks only the provider owning <paramref name="kind"/> for the current reference of a native instance.
    /// No enumeration and no other provider is consulted; an instance that is unknown or no longer current is null.
    /// A kind no provider registered is unresolved for the same reason: every domain package is optional, so the
    /// caller drops the port instead of treating a missing package as a contract violation. Only a malformed kind
    /// name is refused, by the same rule registration applies to every registered kind.</summary>
    public EntityReference? ResolveEntityInstance(string kind, object instance)
    {
        ReadThread(); AcceptRuntimeQuery(); ArgumentNullException.ThrowIfNull(kind); ArgumentNullException.ThrowIfNull(instance);
        RuntimeJson.Require(RuntimeJson.IsId(kind), "entity-resolver", kind);
        if (StartupState != RuntimeStartupState.Ready || !world.worldStarted) return null;
        observingEntities = true;
        try
        {
            var reference = EntityInstanceOf(kind, instance);
            if (reference == null) return null;
            // The owner's answer is only trusted after the same routed check every published reference passes.
            try { entityLives.CheckEntity(reference); }
            catch (RuntimeContractException) { return null; }
            return reference;
        }
        finally { observingEntities = false; }
    }

    private long entityResolverFaultLoggedTick = long.MinValue;
    private const long EntityResolverFaultLogIntervalTicks = 600;

    /// <summary>A resolver that throws leaves its object unanswered and the remaining resolvers still ask, but the
    /// failure is a fault of that package's table and is never silent: it is logged once per interval, with the
    /// exception type only (a native exception message can carry account-level identity).</summary>
    private void ReportEntityResolverFault(string site, Exception error)
    {
        if (entityResolverFaultLoggedTick != long.MinValue && CurrentTick - entityResolverFaultLoggedTick < EntityResolverFaultLogIntervalTicks) return;
        entityResolverFaultLoggedTick = CurrentTick;
        log.LogRuntime(RuntimeLogLevel.Error, "runtime.entity-resolver-failed",
            new RuntimeLogFacts { Detail = "entity-resolver-failed " + site + ": " + error.GetType().Name });
    }

    /// <summary>
    /// The reference one provider's own native lookup answers for <paramref name="kind"/>, with no currency check:
    /// the caller decides whether the answer is about the current life. Only the owning provider is asked, and a
    /// provider that throws — native tables can carry account-level identity — is reported as a resolver failure
    /// without its message. An answer that names another namespace is not that provider's to give and is dropped.
    /// </summary>
    private EntityReference? EntityInstanceOf(string kind, object instance)
    {
        if (!registry.Tables.EntityInstanceResolvers.TryGetValue(kind, out var resolver)) return null;
        EntityReference? reference;
        try { reference = resolver.Resolve(instance); }
        catch (Exception error)
        {
            log.LogCallbackFailedTypeOnly("entity-resolver-failed kind=" + kind, error);
            throw new RuntimeContractException("entity-resolver-failed", kind);
        }
        if (reference == null) return null;
        return EntityKinds.Is(reference, kind) ? reference : null;
    }

    /// <summary>
    /// The one entry point for "which entity is this native object": each registered object resolver is asked in
    /// registration order and the first non-null answer wins. It exists because the caller often holds an object
    /// whose kind it cannot know — a raycast hit is a collider, not an enemy or a door — and the packages that do
    /// know their own native types each answer for their own and return null for everything else, so no package
    /// needs to know about another. A kind no resolver recognizes is null for the same reason an absent optional
    /// package is: the port that would have carried it is left missing rather than filled with a placeholder. The
    /// answer is the same routed, currency-checked reference every other lookup returns, so a resolver that names a
    /// stale life is unanswered rather than handed back.
    /// </summary>
    public EntityReference? ResolveEntity(object instance)
    {
        ReadThread(); AcceptRuntimeQuery(); ArgumentNullException.ThrowIfNull(instance);
        if (StartupState != RuntimeStartupState.Ready || !world.worldStarted) return null;
        observingEntities = true;
        try
        {
            foreach (var (_, resolver) in registry.Tables.ObjectEntityResolvers)
            {
                EntityReference? reference;
                // A resolver that throws is a failure of that package's own table, not a reason to stop asking:
                // the remaining packages still answer for the objects they know, and the object stays unanswered
                // exactly as if the failing one had returned null.
                try { reference = resolver.Resolve(instance); }
                catch (Exception error) { ReportEntityResolverFault("object-resolver", error); continue; }
                if (reference?.Id == null) continue;
                var kind = RuntimeJson.KindOf(reference.Id);
                if (!registry.Tables.Resolvers.TryGetValue(kind, out var owner)) continue;
                try { if (!owner.Resolve(reference)) continue; }
                catch (Exception error) { ReportEntityResolverFault(kind + "-owner", error); continue; }
                if (reference.WorldEpoch != WorldEpoch) continue;
                return reference;
            }
            return null;
        }
        finally { observingEntities = false; }
    }

    /// <summary>The kind one native object belongs to, answered by the same object resolvers
    /// <see cref="ResolveEntity"/> asks, in the same order. A handle that holds a native object is released when
    /// the life of the object it holds has ended, which needs the object's kind before it can ask the owner
    /// whether that life is still current.</summary>
    private string ResolveEntityObject(object instance)
    {
        foreach (var (_, resolver) in registry.Tables.ObjectEntityResolvers)
        {
            EntityReference? reference;
            try { reference = resolver.Resolve(instance); }
            catch (Exception error) { ReportEntityResolverFault("object-resolver", error); continue; }
            if (reference?.Id == null) continue;
            var kind = RuntimeJson.KindOf(reference.Id);
            if (kind != "" && registry.Tables.Resolvers.ContainsKey(kind)) return kind;
        }
        return "";
    }

    public bool IsEntityCurrent(EntityReference reference)
    {
        ReadThread(); AcceptRuntimeQuery(); ArgumentNullException.ThrowIfNull(reference);
        if (!RuntimeJson.IsText(reference.Id) || reference.WorldEpoch != WorldEpoch
            || reference.WorldEpoch < 0 || reference.WorldEpoch > RuntimeJson.MaxSafeInteger
            || reference.LifeEpoch < 0 || reference.LifeEpoch > RuntimeJson.MaxSafeInteger
            || !registry.TryResolver(reference.Id, out var resolve)) return false;
        try { return resolve(reference); }
        catch (Exception error)
        {
            log.LogCallbackFailed("entity-resolver-failed entity=" + reference.Id, error);
            return false;
        }
    }

    private RuntimeEntityInspection InspectEntity(EntityReference reference)
    {
        TryInspectEntity(reference, out var snapshot, out var code);
        return new(reference, snapshot, code);
    }

    /// <summary>One pose, checked as <see cref="TryInspectEntity"/> checks a snapshot: the reference is live before
    /// and after the read, a throwing observer is logged and refused, and the answer must be finite coordinates and
    /// one of the three life words.</summary>
    private bool TryInspectPose(EntityReference reference, out EntityPose pose)
    {
        pose = default;
        if (!registry.TryEntityObserver(reference.Id, out _, out var observe)) return false;
        if (observe == null)
        {
            if (!TryInspectEntity(reference, out var snapshot, out _)) return false;
            pose = new(snapshot!.Position[0], snapshot.Position[1], snapshot.Position[2], snapshot.LifeState);
            return true;
        }
        try { entityLives.CheckEntity(reference); }
        catch (RuntimeContractException) { return false; }
        bool observed;
        try { observed = observe(reference, out pose); }
        catch (Exception error)
        {
            log.LogCallbackFailed("entity-observer-failed entity=" + reference.Id, error);
            pose = default; return false;
        }
        if (!observed || !double.IsFinite(pose.X) || !double.IsFinite(pose.Y) || !double.IsFinite(pose.Z)
            || pose.LifeState is not ("alive" or "downed" or "dead")) { pose = default; return false; }
        try { entityLives.CheckEntity(reference); }
        catch (RuntimeContractException) { pose = default; return false; }
        return true;
    }

    private bool TryInspectEntity(EntityReference reference, out RuntimeEntitySnapshot? snapshot, out string code)
    {
        snapshot = null;
        try { entityLives.CheckEntity(reference); }
        catch (RuntimeContractException error) { code = error.Code; return false; }
        if (!registry.TryEntityObserver(reference.Id, out var observe))
        { code = "entity-observer-unavailable"; return false; }
        try { snapshot = observe(reference); }
        catch (Exception error)
        {
            log.LogCallbackFailed("entity-observer-failed entity=" + reference.Id, error);
            code = "entity-observer-failed"; return false;
        }
        if (snapshot == null) { code = "entity-observation-unavailable"; return false; }
        if (snapshot.Ref != reference) { snapshot = null; code = "entity-observation-mismatch"; return false; }
        try { entityLives.CheckEntity(reference); }
        catch (RuntimeContractException error) { snapshot = null; code = error.Code; return false; }
        // A successful observation is what tells the kernel who hangs from whom: the observer publishes its own
        // entity's parent, and the reverse index is derived here so nothing else has to keep a children list.
        RecordParent(snapshot);
        code = "entity-observed";
        return true;
    }
}

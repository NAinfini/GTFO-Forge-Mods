using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using static ForgeWeapon.EquipmentIdentityIndex;

namespace ForgeWeapon;

/// <summary>Explicit managed W1 registration, not an auto-loaded GTFO plugin.
/// Trusted adapters must provide current native/owner probes and observed references.
/// No attack, inventory write, timer, network protocol or resource reservation is installed.</summary>
internal sealed class EquipmentIdentitySession : IDisposable
{
    private readonly IForgeEngine kernel;
    private readonly EquipmentIdentityIndex index;
    private readonly Func<EquipmentObservation, bool> nativeIsCurrent;
    private readonly Func<EntityReference, bool> ownerIsCurrent;
    /// <summary>Whether one deployed reference is still a placement the owning adapter holds. It is a separate
    /// answer from the deployed observer because a snapshot has to carry a world point and a placement
    /// does not.</summary>
    private readonly Func<EntityReference, bool>? deployedCurrent;
    private readonly Action<RuntimeLifecycleEvent>? observe;
    private readonly RuntimeModuleHandle registration;
    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> gates;
    private readonly RuntimeSubscriptionGate equippedGate, unequippedGate;
    private readonly RuntimeLifecycleSubscription lifecycle;
    private readonly object ticketOwner = new();
    private bool disposed, probing;
    private bool? previousHost;
    public EquipmentIdentitySession(IForgeEngine kernel, RuntimeLogLevel logLevel, Func<EquipmentObservation, bool> nativeIsCurrent,
        Func<EntityReference, bool> ownerIsCurrent, int maxActive = 1024, int maxHistory = 8192,
        Func<EntityReference, RuntimeEntitySnapshot?>? equipmentObserver = null,
        Func<EntityReference, RuntimeEntitySnapshot?>? deployedObserver = null,
        Func<EntityReference, bool>? deployedCurrent = null,
        Func<string, EntityReference, bool>? gearBlockMatcher = null,
        RuntimeModule? module = null,
        Action<RuntimeLifecycleEvent>? observe = null,
        Func<string, EquipmentHolderMatcher>? holderResolver = null,
        Func<object, EntityReference?>? equipmentInstanceResolver = null,
        Func<EntityReference, object?>? equipmentInstance = null)
    {
        this.kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        this.nativeIsCurrent = nativeIsCurrent ?? throw new ArgumentNullException(nameof(nativeIsCurrent));
        this.ownerIsCurrent = ownerIsCurrent ?? throw new ArgumentNullException(nameof(ownerIsCurrent));
        this.deployedCurrent = deployedCurrent;
        this.observe = observe;
        index = new EquipmentIdentityIndex(maxActive, maxHistory);
        // `module` is the weapon provider declaration the native half built, already carrying the bodies this
        // machine supplies; a session that was handed none registers the observation surface alone. This session
        // adds the equipment identity tables to that one declaration and owns the registration.
        registration = kernel.RegisterModule((module ?? ModuleDefinition.Create()) with
        {
            // Both shapes belong to this provider and share the one registry entry ruling 31 gives it: no second
            // world registry, and no second namespace for the world instances one equipment life places.
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
            {
                [EquipmentIdentityId.Namespace] = IsCurrent
            },
            // Other domains are deliberately handed native objects rather than this package's tables. Publish the
            // inverse half beside the resolver that owns currency so Runtime.ResolveEntity can route a wielded Item
            // back to the exact equipment life this session already recorded.
            EntityInstanceResolvers = equipmentInstanceResolver == null ? null
                : new Dictionary<string, Func<object, EntityReference?>>(StringComparer.Ordinal)
                {
                    [EntityKinds.Equipment] = equipmentInstanceResolver
                },
            // The forward half: the native item one current equipment life names, for an action of another domain whose
            // native entry needs the item or its holder (a heal whose source is the medical needle).
            EntityInstances = equipmentInstance == null ? null
                : new Dictionary<string, Func<EntityReference, object?>>(StringComparer.Ordinal)
                {
                    [EntityKinds.Equipment] = equipmentInstance
                },
            // Player's tool query holds an Item but does not know which domain kind owns it, so it enters the
            // untyped ResolveEntity route. Publish the same exact table lookup there; it returns null for every
            // object this provider does not own and still passes Runtime's routed currency check.
            ObjectEntityResolvers = equipmentInstanceResolver == null ? null
                : new[] { ObjectEntityResolver.Of(equipmentInstanceResolver) },
            EntityObservers = Observers(equipmentObserver, deployedObserver),
            AttachmentMatchers = Matchers(gearBlockMatcher, holderResolver)
        }, logLevel);
        try
        {
            // I-MANIFEST: the Forge block fields this package's own profile appliers write. The list is the
            // applier's one vocabulary (`WeaponDefinitionFields`), declared on the registration that owns every
            // writer of it — the native field hooks and the gear-part applier both live behind this provider — so
            // the manifest reports what is really applied instead of a second hand-written list.
            registration.DeclareDefinitionFields(WeaponDefinitionFields.All);
            lifecycle = registration.ObserveLifecycle(ObserveLifecycle);
            Check(lifecycle.IsActive, "equipment.lifecycle-unavailable");
        }
        catch { registration.Dispose(); throw; }
        // The subscription gates of this provider's own bindings, taken here because this is the one moment the
        // registration exists and its bindings are already facts of the registry.
        gates = registration.SubscriptionGates();
        equippedGate = GateOf(ModuleDefinition.EquippedBinding);
        unequippedGate = GateOf(ModuleDefinition.UnequippedBinding);
    }
    /// <summary>The kernel-refreshed gate of one of this provider's own bindings. The kernel precomputes
    /// <c>HasSubscribers</c> and refreshes it where the subscription table changes, so a native callback with nothing
    /// to say to anyone skips the event value instead of building one the kernel would refuse as `no-consumer`; the
    /// same gate is what a fact is begun on (<see cref="Begin"/>). Every row this package publishes is declared
    /// unconditionally by <see cref="ModuleDefinition.Create"/>, so a binding without a gate is a defect and refused.</summary>
    // WHY: P5 — callers take their gates once, at attach, and read one field per call instead of a string lookup.
    public RuntimeSubscriptionGate GateOf(string binding)
        => gates.TryGetValue(binding, out var gate) ? gate : throw new RuntimeContractException("binding-owner", binding);
    /// <summary>The registration this session owns, for the package's own native halves that cast a handle
    /// through it: the whole-profile row's effect handle is minted by the same module its binding is declared in,
    /// so a handle can never outlive the declaration that names it and no second registration exists for it.</summary>
    public RuntimeModuleHandle Registration => registration;
    /// <summary>One subscription carries the whole weapon session's level lifecycle: the identity index is what
    /// this class watches for itself, and the session's other halves that hold something a world boundary
    /// invalidates are handed the same event through <c>observe</c>, so one world change reaches every table that
    /// has to give something up rather than a second subscription per table.</summary>
    private void ObserveLifecycle(RuntimeLifecycleEvent value)
    {
        if (value.Kind is RuntimeLifecycleKind.Snapshot or RuntimeLifecycleKind.WorldChanged)
            index.BeginWorld(value.Current.WorldEpoch);
        if (previousHost == true && value.Current.IsHost != true) index.Clear();
        previousHost = value.Current.IsHost;
        if (value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) index.Clear();
        observe?.Invoke(value);
    }
    /// <summary>One namespace, one observer: which table answers a reference is decided by the id shape the
    /// resolver uses too, so a query can never read an equipment life out of the placement table or the reverse.</summary>
    private static IReadOnlyDictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>? Observers(
        Func<EntityReference, RuntimeEntitySnapshot?>? equipment, Func<EntityReference, RuntimeEntitySnapshot?>? deployed)
    {
        if (equipment == null && deployed == null) return null;
        return new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>(StringComparer.Ordinal)
        {
            [EquipmentIdentityId.Namespace] = reference
                => EquipmentIdentityId.ShapeOf(reference?.Id) is EquipmentEntityShape.DeployedInstance or EquipmentEntityShape.LevelSentryInstance
                    ? deployed?.Invoke(reference!) : equipment?.Invoke(reference!)
        };
    }
    /// <summary>The mount kinds this provider owns. A gear-block reference is read against the native instance
    /// the recorded life currently points at, so the matcher is the adapter's own readback rather than a table
    /// kept here: the session owns the reference, the adapter owns what the game says about it.</summary>
    private static IReadOnlyDictionary<string, AttachmentMatcherRegistration>? Matchers(Func<string, EntityReference, bool>? gearBlock,
        Func<string, EquipmentHolderMatcher>? holder)
        => gearBlock == null ? null : new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
        {
            // A gear block names a whole block, so there is no category to carry; the framework refuses one. The
            // kind is matched against the event's own subject: the equipment instance the gear block was read from.
            [ModuleDefinition.GearBlockAttachmentKind] =
                AttachmentMatcherRegistration.BySubject((_, reference, subject) => gearBlock(reference, subject), holder)
        };
    /// <summary>Deployed instances are not in the W1 index, so this answers from the owning adapter's own placement
    /// table through the currency predicate it supplies; an already-ended placement is not current, which is what
    /// refuses a stale identity. The snapshot observer is not asked here: a placement whose object cannot report a
    /// world point is unobservable, not ended.</summary>
    private bool IsDeployedInstanceCurrent(EntityReference reference)
        => deployedCurrent != null && reference != null && deployedCurrent(reference);

    public int Count { get { _ = kernel.Lifecycle; return index.Count; } }
    /// <summary>The world the recorded lives belong to. It only moves when the index observes a world change, so a
    /// caller comparing it with the kernel's epoch can tell that everything it held belongs to a gone world.</summary>
    public long WorldEpoch => index.WorldEpoch;
    public int IdentityHistoryCount { get { _ = kernel.Lifecycle; return index.HistoryCount; } }
    private RuntimeLifecycleSnapshot Ready(bool requireHost = false)
    {
        var state = kernel.Lifecycle; // Also enforces the owning simulation thread.
        Check(!disposed && registration.IsRegistered && lifecycle.IsActive, "equipment.session-detached");
        Check(!probing, "equipment.probe-reentrancy");
        Check(state.StartupState == RuntimeStartupState.Ready && state.IsHost.HasValue && (!requireHost || state.IsHost == true),
            "equipment.not-authoritative-ready");
        Check(state.WorldEpoch == index.WorldEpoch, "equipment.stale-world");
        return state;
    }
    private void Probe(EquipmentObservation value)
    {
        probing = true;
        try
        {
            Check(nativeIsCurrent(value), "equipment.native-not-current");
            if (value.Owner != null) Check(ownerIsCurrent(value.Owner), "equipment.owner-not-current");
        }
        catch (RuntimeContractException) { throw; }
        catch (Exception error)
        { throw new RuntimeContractException("equipment.probe-failed", error.GetType().Name); }
        finally { probing = false; }
    }
    /// <summary>Records one verified observation. Returns the wield fact dispatch only when this observation
    /// is a directly observed wield flip of the same life, owner and inventory slot; otherwise null.
    /// First sightings, owner or slot changes and location changes never synthesize wield history.</summary>
    public DispatchResult? Record(EquipmentObservation value)
    {
        var before = Ready(true); index.Validate(value); Probe(value);
        Check(Ready(true).WorldEpoch == before.WorldEpoch, "equipment.stale-world");
        var previous = index.Get(value.Entity)?.Value;
        var entry = index.Record(value);
        if (previous == null || previous.IsWielded == value.IsWielded || previous.Owner != value.Owner
            || previous.Slot != value.Slot || previous.Location != EquipmentLocation.Inventory
            || value.Location != EquipmentLocation.Inventory) return null;
        var world = before.WorldEpoch;
        var kind = value.IsWielded ? "equipped" : "unequipped";
        // Nothing is listening on the wield row: the kernel would answer `no-consumer` for the event this call is
        // about to build, so the event value is never built. The flip above is still recorded, because a wield
        // observed while nobody listened is one this session has already recorded.
        var gate = value.IsWielded ? equippedGate : unequippedGate;
        if (!gate.HasSubscribers) return null;
        var fact = registration.BeginEvent(gate);
        fact.Values.Entity(value.IsWielded ? Blocks.TriggerInputEquipped.OutActor : Blocks.TriggerInputUnequipped.OutActor, value.Owner);
        fact.Values.Entity(value.IsWielded ? Blocks.TriggerInputEquipped.OutEquipment : Blocks.TriggerInputUnequipped.OutEquipment, value.Entity);
        return fact.Raise("gtfo.equipment." + kind + ":" + world + ":" + value.Entity.Id + ":" + entry.Revision,
            world, Math.Max(0, kernel.CurrentTick), WieldScope(value.Entity));
    }
    /// <summary>A wield fact belongs to the one equipment life it names. The game unwields a gun a moment before it
    /// replaces the slot, so the fact is queued while the life is current and dispatched after it ended; scoping it
    /// to the life lets <see cref="Remove"/> withdraw it explicitly (`event.cancelled`) instead of the kernel finding
    /// the subject stale mid-dispatch and rejecting the event at Error.</summary>
    private static string WieldScope(EntityReference equipment) => "gtfo.equipment-wield:" + equipment.Id;
    public bool Remove(EntityReference reference)
    {
        _ = kernel.Lifecycle; Check(!probing, "equipment.probe-reentrancy"); Reference(reference);
        if (disposed || !registration.IsRegistered) return false;
        var removed = index.Remove(reference);
        if (removed) registration.CancelScope(WieldScope(reference));
        return removed;
    }
    /// <summary>Imports the host's identity without producing an inventory fact on this replica.</summary>
    public void ImportOwnerObservation(EquipmentObservation value)
    {
        var state = Ready();
        Check(state.IsHost == false, "equipment.not-client");
        index.Validate(value); Probe(value);
        Check(Ready().WorldEpoch == state.WorldEpoch && kernel.Lifecycle.IsHost == false, "equipment.stale-world");
        index.Record(value);
    }
    /// <summary>Begins one equipment, deployable or combat fact of world <paramref name="worldEpoch"/> under this
    /// provider's own registration, on the gate the publisher took from <see cref="GateOf"/>: the publisher writes
    /// the outputs by position and raises it with that same world. Facts travel through the kernel like the wield
    /// facts do; this provider never queues or duplicates an event.</summary>
    public EventWriter Begin(RuntimeSubscriptionGate gate, long worldEpoch)
    {
        // The owning-thread read comes first. A fact captured before suspension can belong to the invalidated
        // world, so explicit closed admission must reach the kernel before live readiness or world checks.
        if (kernel.FactAdmissionClosure != null)
        {
            Check(!disposed && registration.IsRegistered, "equipment.session-detached");
            Check(!probing, "equipment.probe-reentrancy");
            return registration.BeginEvent(gate);
        }
        Ready(true);
        Check(worldEpoch == index.WorldEpoch, "equipment.stale-world");
        return registration.BeginEvent(gate);
    }
    /// <summary>The sheet of an event a client writes only to predict what it shows that client
    /// (<see cref="EventWriter.Predict"/>); nothing is published from it, so the host is not required.</summary>
    internal EventWriter BeginPredicted(RuntimeSubscriptionGate gate)
    {
        Ready();
        return registration.BeginEvent(gate);
    }
    internal void PredictionFailed(RuntimeSubscriptionGate gate, Exception error) => registration.PredictionFailed(gate, error);
    private Entry Verified(EntityReference reference)
    {
        var before = Ready(); Reference(reference);
        var entry = index.Get(reference);
        Check(entry != null, "equipment.stale-instance");
        Probe(entry!.Value);
        Check(Ready().WorldEpoch == before.WorldEpoch && ReferenceEquals(entry, index.Get(reference)),
            "equipment.observation-changed");
        return entry;
    }
    public bool TryResolve(EntityReference reference, out EquipmentObservation? observation, out string code)
    {
        _ = kernel.Lifecycle; observation = null; code = "equipment.current";
        if (reference == null) { code = "equipment.invalid-reference"; return false; }
        try { observation = Verified(reference).Value; return true; }
        catch (RuntimeContractException error) { code = error.Code; return false; }
    }
    /// <summary>Every reference this provider answers for, routed by the id shape it issued: a deployed world
    /// instance lives in the adapter's placement table, a backpack life in the W1 index, and neither table ever
    /// answers for the other shape. Both routes end in this one resolver of the one `gtfo.equipment` namespace.</summary>
    public bool IsCurrent(EntityReference reference)
    {
        if (reference == null) return false;
        return EquipmentIdentityId.ShapeOf(reference.Id) switch
        {
            EquipmentEntityShape.DeployedInstance or EquipmentEntityShape.LevelSentryInstance => IsDeployedInstanceCurrent(reference),
            EquipmentEntityShape.Life => TryResolve(reference, out _, out _),
            _ => false
        };
    }
    public EquipmentUseTicket CaptureOwnedUse(EntityReference reference, EntityReference expectedOwner,
        bool requireWielded)
    {
        Reference(expectedOwner); var entry = Verified(reference);
        Check(entry.Value.Owner == expectedOwner, "equipment.owner-mismatch");
        RequireUsable(entry.Value, requireWielded);
        return new EquipmentUseTicket(ticketOwner, entry.Revision, entry.Value, requireWielded);
    }
    public EquipmentObservation RequireCurrent(EquipmentUseTicket ticket)
    {
        _ = kernel.Lifecycle; ArgumentNullException.ThrowIfNull(ticket);
        Check(ReferenceEquals(ticket.Session, ticketOwner), "equipment.foreign-ticket");
        var entry = Verified(ticket.Observation.Entity);
        Check(entry.Revision == ticket.Revision, "equipment.observation-changed");
        RequireUsable(entry.Value, ticket.RequireWielded); return entry.Value;
    }
    private static void RequireUsable(EquipmentObservation value, bool requireWielded)
    {
        Check(value.Owner != null, "equipment.owner-required");
        Check(value.IsReady, "equipment.not-loaded");
        Check(!requireWielded || value.IsWielded, "equipment.not-wielded");
    }
    public void Dispose()
    {
        _ = kernel.Lifecycle; Check(!probing, "equipment.probe-reentrancy");
        if (disposed) return;
        registration.Dispose(); // If dispatch rejects disposal, keep the live session intact.
        lifecycle.Dispose(); index.Clear(); disposed = true;
    }
}

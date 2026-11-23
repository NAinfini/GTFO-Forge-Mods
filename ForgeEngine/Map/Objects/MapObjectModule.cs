using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The `gtfo.map_object` half of the Map provider: the one map-object entity namespace, its read-only
/// observer, the `map-object` attachment matcher, and the one publisher every door and terminal hook goes
/// through. The provider half that knows both domains composes the one registration and this half declares
/// its own surface on it.
///
/// Publication is deliberately narrow. A hook reports only the kind of fact its own native callback carried;
/// the module reads the instance back, refuses the report when the instance no longer matches the address the
/// reference was built from, and publishes only a value that read produced and that differs from the last one
/// it published for that address. A repeated native sync of one state is therefore not a second event. Every
/// binding here observes native state, so each is registered with role `observe`, carries no result row, and
/// is published by the host only.
/// </summary>
internal sealed partial class MapObjectModule : IDisposable
{
    // The entity namespace this provider registers is the SDK's `EntityKinds.MapObject`: one constant every package
    // names a kind by, so this half declares none of its own. Everything else about the namespace lives here.
    private const string Prefix = EntityKinds.MapObject + ":";
    private const string AddressTag = "map-object.address=";
    private const string CategoryTag = "map-object.category=";
    private const string Faction = "map-object";
    // A map object is addressed, not alive: the shared snapshot's life state has no other honest value for a
    // door or a terminal, and inventing `entity.despawned` facts is not this provider's business.
    private const string LifeState = "alive";
    private sealed record Published(string StateKey, long Transition);
    private readonly IForgeEngine _kernel;
    private readonly RuntimeLogLevel _logLevel;
    private readonly IMapObjectSource _doors;
    private readonly IMapObjectSource _terminals;
    private readonly Func<string, string, object?> _resolve;
    private readonly Func<object, EntityReference?>? _players;
    private readonly Func<bool> _authority;
    private readonly Action<string> _report;
    /// <summary>The reader of the current level's own identity, or null when this half has none (a fixture with
    /// no game-bound side). A `level` mount is the one kind judged from the mount target alone, so its reader is
    /// the only world read this module makes outside an observed instance.</summary>
    private readonly Func<MapLevelReference?>? _currentLevel;
    /// <summary>The game-bound half's climb from a hit object to the map object it belongs to, or null when this
    /// half has no such reader. A hit reaches this kind as whatever native object the bullet was resolved
    /// against — a collider on a door blade, a terminal screen — and only the half that owns the native types
    /// can say which map object that is. The module still owns the decision: it classifies the instance this
    /// answers with and addresses it exactly as one handed over directly, so a climb that names nothing leaves
    /// the kind unresolved instead of inventing an address.</summary>
    private readonly Func<object, object?>? _hit;
    private RuntimeModuleHandle? _registration;
    /// <summary>The subscription gate of every binding this module publishes, taken once at registration so a
    /// callback reads a field instead of looking a binding up by name. A null gate is a binding this registration
    /// does not declare: not this half's to skip, so the kernel still decides it.</summary>
    private RuntimeSubscriptionGate? _doorStateGate, _lockStateGate, _terminalResultGate, _terminalSessionGate,
        _generatorCellGate, _generatorClusterGate, _zoneEnteredGate, _zoneExitedGate, _zoneMembersGate;
    /// <summary>The two trigger-zone bindings, spelled once: the membership facts are published every tick a body
    /// crosses a volume, so their binding ids are not re-derived from the row table on each one.</summary>
    private static readonly string ZoneEnteredBinding = TriggerZoneContract.BindingOf(TriggerZoneContract.EnteredFact);
    private static readonly string ZoneExitedBinding = TriggerZoneContract.BindingOf(TriggerZoneContract.ExitedFact);
    private RuntimeLifecycleSubscription? _lifecycle;
    /// <summary>The provider's session half, composed on the one registration this module owns. A session fact is
    /// not a map object, so its publishing rules live in their own half; it is created here because this module
    /// is where the registration and the one native-callback path for this provider already exist.</summary>
    private ExpeditionModule? _expeditions;
    private readonly Dictionary<string, Published> _published = new(StringComparer.Ordinal);
    /// <summary>This world's level identity, read at most once per world: unreadable and absent are different
    /// answers, and both are worth remembering for the world they were read in.</summary>
    private MapLevelReference? _level;
    private bool _levelRead;
    // Only a command received by this terminal may own its next result. Remember it even when nobody
    // subscribes to command events, and consume it once rather than borrowing the last command forever.
    private sealed record TerminalCommand(MapObjectSubject Subject, int Value, string Name, long Sequence);
    private readonly Dictionary<string, TerminalCommand> _accepted = new(StringComparer.Ordinal);
    private long _terminalCommandSequence;
    private readonly OnceReporter _once;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    public MapObjectModule(IForgeEngine kernel, RuntimeLogLevel logLevel, IMapObjectSource doors,
        IMapObjectSource terminals, Func<string, string, object?> resolve, Func<object, EntityReference?>? players,
        Func<bool> authority, Action<string> report, Action<string> log, Func<MapLevelReference?>? currentLevel = null,
        Func<object, object?>? hit = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _logLevel = logLevel;
        _doors = doors ?? throw new ArgumentNullException(nameof(doors));
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _players = players;
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _report = report ?? throw new ArgumentNullException(nameof(report)); _once = new OnceReporter(_report);
        _currentLevel = currentLevel;
        _hit = hit;
        if (currentLevel != null) { _zoneSource.CurrentLevel = Level; _interactionPoints.CurrentLevel = Level; }
    }

    /// <summary>Registers this half on the one Map provider definition. The definition is composed by the
    /// caller that knows both halves — the game-bound session — because one registration has to declare every
    /// Map entity namespace and the runtime accepts exactly one provider of one; this half only adds its own
    /// category, its own instance lookup, its own observer and the `map-object` attachment matcher.</summary>
    public void Register(RuntimeModule definition)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(definition);
        if (_registration != null) throw new InvalidOperationException("Map objects are already registered.");
        // A subscription that cannot be taken leaves no registration behind: an unobservable module would keep
        // a world's table across a level change, which is worse than a package that failed to load.
        _registration = _kernel.RegisterModule(Declare(definition), _logLevel);
        try { _lifecycle = _registration.ObserveLifecycle(OnLifecycle); }
        catch { _registration.Dispose(); _registration = null; throw; }
        // The session half publishes through this registration and nothing else, so it is born exactly when the
        // registration is and dies with it: an event published after the provider is gone would be a report about
        // a world nothing is left to answer for.
        _expeditions = new ExpeditionModule(_registration, _kernel, _authority, _report);
        // The subscription gates of this provider's own bindings, taken here because this is the one moment the
        // registration exists and its bindings are already facts of the registry. The kernel refreshes each gate in
        // place wherever the subscription table changes, so a field read answers for the world as it is now.
        var gates = _registration.SubscriptionGates();
        RuntimeSubscriptionGate? Gate(string binding) => gates.TryGetValue(binding, out var gate) ? gate : null;
        _doorStateGate = Gate(MapObjectContract.BindingFor(MapObjectContract.DoorStateFact));
        _lockStateGate = Gate(MapObjectContract.BindingFor(MapObjectContract.LockStateFact));
        _terminalResultGate = Gate(MapObjectContract.BindingFor(MapObjectContract.TerminalResultFact));
        _terminalSessionGate = Gate(MapObjectContract.BindingFor(MapObjectContract.TerminalSessionFact));
        _generatorCellGate = Gate(GeneratorContract.GeneratorCellBinding);
        _generatorClusterGate = Gate(GeneratorContract.GeneratorClusterBinding);
        _zoneEnteredGate = Gate(ZoneEnteredBinding);
        _zoneExitedGate = Gate(ZoneExitedBinding);
        _zoneMembersGate = Gate(TriggerZoneContract.MembersBinding);
        _interactionStartedGate = Gate(InteractionPointContract.StartedBinding);
        _interactionProgressGate = Gate(InteractionPointContract.ProgressBinding);
        _interactionCancelledGate = Gate(InteractionPointContract.CancelledBinding);
        _interactionCompletedGate = Gate(InteractionPointContract.CompletedBinding);
    }

    /// <summary>Adds this half's own runtime surface to the caller's definition. The definition is a record
    /// whose dictionaries are shared, so each one is copied before this half's entry is added rather than the
    /// caller's definition being mutated.</summary>
    /// <summary>The level-object half of the same registration, set once it exists and cleared when it is released.</summary>
    internal LevelObjectModule? LevelObjects { get; set; }

    private RuntimeModule Declare(RuntimeModule definition) => definition with
    {
        // The namespace includes objects held by this module and native event sources (such as built portals)
        // held by the composing host. Both use their own real instance tables; stale worlds match neither.
        EntityResolvers = Merge(Merge(definition.EntityResolvers, EntityKinds.MapObject,
            new MapEntityResolver(this, definition).IsCurrent),
            EntityKinds.LevelObject, reference => LevelObjectModule.IsCapturedInstance(reference)
                ? LevelObjects?.IsCurrent(reference) == true
                : definition.EntityResolvers is { } resolvers && resolvers.TryGetValue(EntityKinds.LevelObject, out var current)
                    ? current(reference) : LevelObjects?.IsCurrent(reference) == true),
        EntityInstanceResolvers = Merge(definition.EntityInstanceResolvers, EntityKinds.MapObject, ResolveInstance),
        EntityObservers = Merge(Merge(definition.EntityObservers, EntityKinds.MapObject, Observe),
            EntityKinds.LevelObject, reference => LevelObjectModule.IsCapturedInstance(reference)
                ? LevelObjects?.ObserveInstance(reference)
                : definition.EntityObservers is { } observers && observers.TryGetValue(EntityKinds.LevelObject, out var observe) ? observe(reference) : null),
        EntityInstances = Merge(definition.EntityInstances, EntityKinds.LevelObject,
            reference => LevelObjectModule.IsCapturedInstance(reference) ? LevelObjects?.NativeInstance(reference)
                : definition.EntityInstances is { } instances && instances.TryGetValue(EntityKinds.LevelObject, out var instance) ? instance(reference) : null),
        AttachmentMatchers = Attachments(definition)
    };

    /// <summary>The mount kinds this half owns. A map object is matched against the event's own subject; a level
    /// is not an entity at all, so its kind is registered as a scope and judged from the reference alone — which
    /// is also what lets it judge the events that carry no subject. A player mount is matched against the player
    /// the event names and judged by the level too. A half without a level reader declares neither `level` nor
    /// `player`: a kind nothing can compare is better refused at load than matched loosely.
    private IReadOnlyDictionary<string, AttachmentMatcherRegistration> Attachments(RuntimeModule definition)
    {
        var owned = new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
        {
            ["map-object"] = AttachmentMatcherRegistration.BySubject(MatchesAttachment)
        };
        if (_currentLevel != null)
        {
            owned["level"] = AttachmentMatcherRegistration.ByScope(MatchesLevel);
            owned["player"] = AttachmentMatcherRegistration.BySubject(MatchesPlayer);
        }
        return Merge(definition.AttachmentMatchers, owned);
    }

    private static Dictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T>? declared, string key, T handler)
    {
        var merged = declared == null
            ? new Dictionary<string, T>(StringComparer.Ordinal)
            : new Dictionary<string, T>(declared, StringComparer.Ordinal);
        merged[key] = handler;
        return merged;
    }

    /// <summary>The caller's declarations with this half's own kinds added; an entry the caller already declared
    /// is replaced, because one kind has exactly one owner and this half is the one registering here.</summary>
    private static Dictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T>? declared, IReadOnlyDictionary<string, T> owned)
    {
        var merged = declared == null
            ? new Dictionary<string, T>(StringComparer.Ordinal)
            : new Dictionary<string, T>(declared, StringComparer.Ordinal);
        foreach (var entry in owned) merged[entry.Key] = entry.Value;
        return merged;
    }
    public bool IsRegistered => !_disposed && _registration is { IsRegistered: true };

    /// <summary>Whether a loaded plan or a suspended wait is listening on either trigger-zone membership binding, or
    /// a loaded plan reads the members of a zone through a query step — the one question the zone clock asks every
    /// tick before it judges a volume. A members read is answered from the judgment's own table, so a plan that only
    /// reads it still needs the zones judged; each edge is still built only for its own binding's listeners. The
    /// gates are the fields taken at registration, so the answer is three field reads for the world as it is now; a
    /// module not yet registered has no listener.</summary>
    internal bool TriggerZonesObserved
        => _zoneEnteredGate is { HasSubscribers: true } || _zoneExitedGate is { HasSubscribers: true }
            || _zoneMembersGate is { HasSubscribers: true };

    /// <summary>The one Map registration. The package's other domains attach to it instead of registering a
    /// second provider of the same identity namespace.</summary>
    public RuntimeModuleHandle Registration
        => _registration ?? throw new InvalidOperationException("The map-object module is not registered yet.");

    /// <summary>The one session half of this provider. A native callback reports an expedition end here; the
    /// half answers whether the event reached the kernel and never dispatches anything itself.</summary>
    public ExpeditionModule Expeditions
        => _expeditions ?? throw new InvalidOperationException("The map-object module is not registered yet.");

    /// <summary>The world this provider publishes in: the kernel's own epoch, which every entity id, event scope
    /// and reference this module answers with carries. A game-bound reader that keeps its own table of native
    /// instances keys that table on this, so an address read in one level is never answered from the next.</summary>
    public long WorldEpoch => _kernel.WorldEpoch;

    /// <summary>Drops the module's own per-world tables at a world transition. The kernel's world epoch is what
    /// invalidates earlier ids; this only releases the memory of a level that no longer exists, and drops the
    /// level identity read in the world that just ended so the next dispatch reads the new level's own.</summary>
    public void BeginWorld()
    {
        CheckThread();
        _placedModels.BeginWorld(_kernel.WorldEpoch);
        _published.Clear();
        _accepted.Clear();
        _terminalCommandSequence = 0;
        _generators.Clear();
        _level = null;
        _levelRead = false;
    }

    /// <summary>One door status the door's own sync callback carried. The status is re-read from the instance
    /// after the callback returned, so the fact reports the door's state and not the callback's argument.</summary>
    public void DoorStateChanged(object door, object? instigator = null)
    {
        CheckThread();
        if (!TrySubject(_doors, door, out var subject) || !Read(subject, out var observation)) return;
        var snapshot = observation.Door!;
        if (!Admit(subject, MapObjectContract.DoorStateFact, _doorStateGate, snapshot.StateKey, out var raised, out var transition)) return;
        var values = raised.Values;
        values.Entity(Blocks.TriggerInteractionDoorState.OutDoor, subject.Reference);
        // `state` is a `door_state` member: the set lists eDoorStatus in its own order, so the status is its index.
        values.Enum(Blocks.TriggerInteractionDoorState.OutState, snapshot.Status);
        values.Entity(Blocks.TriggerInteractionDoorState.OutActor, Actor(instigator));
        // A door status that is not an interaction transition has no phase, and the port is declared
        // optional: the port is left out instead of publishing a phase the door never reported.
        if (snapshot.Phase is { } phase) values.Enum(Blocks.TriggerInteractionDoorState.OutPhase, phase);
        Raise(raised, subject, MapObjectContract.DoorStateFact, transition);
    }

    /// <summary>One door state the lock component reported. The lock callback carries the same door state as
    /// the door's own sync callback and both facts are read from the same instance, so a native lock callback
    /// republishing one status is refused by the lock fact's own state key.</summary>
    public void DoorLockChanged(object door, object? instigator = null)
    {
        CheckThread();
        if (!TrySubject(_doors, door, out var subject) || !Read(subject, out var observation)) return;
        var snapshot = observation.Door!;
        if (!Admit(subject, MapObjectContract.LockStateFact, _lockStateGate,
            "lock:" + (snapshot.Locked ? "locked" : "unlocked") + ":" + snapshot.Key, out var raised, out var transition)) return;
        var values = raised.Values;
        values.Entity(Blocks.TriggerInteractionLockState.OutDoor, subject.Reference);
        values.Boolean(Blocks.TriggerInteractionLockState.OutLocked, snapshot.Locked);
        values.Text(Blocks.TriggerInteractionLockState.OutKey, snapshot.Key);
        values.Entity(Blocks.TriggerInteractionLockState.OutActor, Actor(instigator));
        Raise(raised, subject, MapObjectContract.LockStateFact, transition);
    }

    /// <summary>The replicated terminal callback refreshes its session only. Display states include pending
    /// operations and valid initial modes; they do not prove that a command has completed.</summary>
    public void TerminalStateChanged(object terminal, object? actor)
    {
        CheckThread();
        if (!TrySubject(_terminals, terminal, out var subject) || !Read(subject, out var observation)) return;
        var snapshot = observation.Terminal!;
        if (!Admit(subject, MapObjectContract.TerminalSessionFact, _terminalSessionGate,
            "session:" + (snapshot.SessionActive ? "active" : "inactive"), out var raised, out var transition)) return;
        var values = raised.Values;
        values.Entity(Blocks.TriggerInteractionTerminalSession.OutTerminal, subject.Reference);
        if (Actor(actor) is { } player) values.Entity(Blocks.TriggerInteractionTerminalSession.OutActor, player);
        values.Boolean(Blocks.TriggerInteractionTerminalSession.OutActive, snapshot.SessionActive);
        Raise(raised, subject, MapObjectContract.TerminalSessionFact, transition);
    }

    /// <summary>Called before the interpreter executes a received command, never at the send request.
    /// The native execution can synchronously change the terminal state before its caller returns.</summary>
    public void TerminalCommandStarted(object terminal, int command)
    {
        CheckThread();
        if (_disposed || _registration is not { IsRegistered: true } registration
            || _kernel.StartupState != RuntimeStartupState.Ready || !_authority()) return;
        if (!TrySubject(_terminals, terminal, out var subject) || !Read(subject, out _)) return;
        _accepted.Remove(subject.Address.ToString());
        string? registeredName = MapObjectTerminalCommand.RegisteredName(terminal, command);
        if (MapObjectTerminalCommand.IsUnique(command) && string.IsNullOrEmpty(registeredName))
        {
            _report("compat." + MapObjectTerminalCommand.NameUnavailable + ": " + subject.Address + "; command=" + command);
            return;
        }
        string name = MapObjectTerminalCommand.Name(command, registeredName);
        _accepted[subject.Address.ToString()] = new(subject, command, name, ++_terminalCommandSequence);
    }

    /// <summary>An actual command return, observed separately from terminal session/prompt states.</summary>
    public void TerminalCommandFinished(object terminal, int command, int outcome)
    {
        CheckThread();
        if (outcome is not (MapObjectOutcomes.Succeeded or MapObjectOutcomes.Failed))
            throw new RuntimeContractException("map-terminal-result-invalid", "Unknown native terminal result.");
        if (TrySubject(_terminals, terminal, out var subject) && Read(subject, out _))
            CompleteTerminalCommand(subject, command, outcome);
    }

    private void CompleteTerminalCommand(MapObjectSubject subject, int command, int outcome)
    {
        var key = subject.Address.ToString();
        if (!_accepted.TryGetValue(key, out var pending)) return;
        if (!pending.Subject.Source.IsCurrentAddress(pending.Subject.Instance, pending.Subject.Address))
        { _accepted.Remove(key); return; }
        if (pending.Value != command) return;
        _accepted.Remove(key);
        if (!Admit(subject, MapObjectContract.TerminalResultFact, _terminalResultGate,
            "result:" + pending.Sequence.ToString(CultureInfo.InvariantCulture), out var raised, out var transition)) return;
        var values = raised.Values;
        values.Entity(Blocks.TriggerInteractionTerminalResult.OutTerminal, subject.Reference);
        values.Text(Blocks.TriggerInteractionTerminalResult.OutCommand, pending.Name);
        values.Enum(Blocks.TriggerInteractionTerminalResult.OutOutcome, outcome);
        Raise(raised, subject, MapObjectContract.TerminalResultFact, transition);
    }

    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        if (value.Kind == RuntimeLifecycleKind.WorldChanged && !value.IsMigration) BeginWorld();
        if (value.Kind == RuntimeLifecycleKind.CheckpointRestored)
        {
            // The native objects were recalled; keep transition numbers so replay protection cannot
            // mistake the next real state callback for an event from before the checkpoint.
            foreach (var key in new List<string>(_published.Keys)) _published[key] = _published[key] with { StateKey = "" };
            // The generator table stays: the level's generators are placed once per world and a restore reloads
            // state without placing them again, so clearing it would leave their addresses unreadable.
            _accepted.Clear();
        }
    }

    /// <summary>Admits one fact of one subject and begins its event on the row's own gate, which names the binding
    /// the row carries. The instance was re-read after the native callback returned: an instance whose address no
    /// longer reads the same way publishes nothing, and neither does one whose members cannot be read in full.
    /// The caller writes the event's outputs by position and publishes it with <see cref="Raise"/>.</summary>
    private bool Admit(MapObjectSubject subject, string fact, RuntimeSubscriptionGate? gate, string stateKey,
        out EventWriter raised, out long transition)
    {
        raised = default; transition = 0;
        if (_disposed || _registration is not { IsRegistered: true } registration) return false;
        if (_kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (!_authority())
        {
            ReportOnce("client:" + subject.Address,
                "map-object fact observed on a non-authoritative peer: the host publishes map-object state, a client does not.");
            return false;
        }
        string address = subject.Address.ToString();
        // The record is keyed by the fact as well as the address: one map object publishes more than one row —
        // a generator's cell and its run state come from one native callback — and a table keyed by the address
        // alone would let one row's state refuse the other's. Each row therefore carries its own last state and
        // its own transition number, which is what makes a repeated sync of one state a non-event per row.
        string factKey = fact + "|" + address;
        if (_published.TryGetValue(factKey, out var last) && last.StateKey == stateKey) return false;
        transition = last == null ? 1 : last.Transition + 1;
        _published[factKey] = new Published(stateKey, transition);
        // Nothing is listening on this binding: the kernel would answer `no-consumer` for the very event this
        // call is about to build, so the event value is never built. The state above is still recorded, because a
        // fact observed while nobody listened is a fact this module has already reported.
        if (Unsubscribed(gate)) return false;
        raised = registration.BeginEvent(gate!);
        return true;
    }

    /// <summary>Publishes one admitted fact as its caller wrote it.</summary>
    private void Raise(EventWriter raised, MapObjectSubject subject, string fact, long transition)
    {
        raised.Raise(FactId(subject, fact, transition), _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), Scope());
        // Runtime owns publication refusals and their shared repeated-diagnostic summary.
    }

    /// <summary>Whether no loaded plan is mounted on one of this half's own bindings. The kernel precomputes the
    /// answer and refreshes it where the subscription table changes, so a callback with nothing to say to anyone
    /// skips the event value instead of building one the kernel would refuse as `no-consumer`. A binding this
    /// registration does not declare has no gate here: it is not this half's to skip, and the kernel still decides it.</summary>
    private static bool Unsubscribed(RuntimeSubscriptionGate? gate) => gate is not { HasSubscribers: true };

    /// <summary>The event id is the fact's own identity: world, address, fact kind and the subject's own
    /// transition number. A repeated sync of one state produces neither a new transition nor a new id.</summary>
    private string FactId(MapObjectSubject subject, string fact, long transition)
        => EntityKinds.MapObject + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + ":" + subject.Address + ":" + transition.ToString(CultureInfo.InvariantCulture);

    private string Scope() => "gtfo.world:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture);

    private bool TrySubject(IMapObjectSource source, object instance, out MapObjectSubject subject)
    {
        subject = null!;
        if (instance == null) return false;
        if (source.TryAddress(instance) is not { } address) return false;
        subject = new MapObjectSubject(EntityId(address), address, source, instance);
        return true;
    }

    /// <summary>Re-reads the instance a hook reported, once, and refuses the report when the instance no longer
    /// reads as the address it was addressed with or no longer carries this fact's own members.</summary>
    private bool Read(MapObjectSubject subject, out MapObjectObservation observation)
    {
        observation = null!;
        var read = subject.Source.Read(subject.Instance);
        if (read != null && read.AddressMatches && subject.Source.IsCurrentAddress(subject.Instance, subject.Address)
            && ReadsAsCategory(read, subject))
        {
            observation = read;
            return true;
        }
        subject.Source.Report("map-object observation refused for " + subject.Address
            + ": the native instance no longer reads as it was addressed.");
        return false;
    }

    /// <summary>Whether one re-read instance carries the section its category promises: a door's own snapshot, a
    /// terminal's, the zone record, or — for the generator category — the generator's own reading or its group's,
    /// which the address's own key form tells apart. A read that answers with another category's section is not
    /// this fact's reading.</summary>
    private static bool ReadsAsCategory(MapObjectObservation read, MapObjectSubject subject)
    {
        bool generator = subject.Category == MapObjectGeneratorAddress.Category;
        bool group = generator && MapObjectGeneratorAddress.IsGroupKey(subject.Address.Key);
        bool interactionPoint = subject.Category == InteractionPointAddress.Category;
        bool placedModel = subject.Category == PlacedModelAddress.Category;
        return (read.Door != null) == (subject.Category == MapObjectCategories.Door)
            && (read.Terminal != null) == (subject.Category is "terminal" or MapObjectCategories.Uplink)
            && (read.Zone != null) == (subject.Category == TriggerZoneAddress.Category)
            && (read.Generator != null) == (generator && !group)
            && (read.Group != null) == group
            && (interactionPoint || placedModel || read.Door != null || read.Terminal != null || read.Zone != null
                || read.Generator != null || read.Group != null);
    }

    /// <summary>The entity id of one address in one world: the one map-object namespace, then the address
    /// text. The id is derived from the address alone, so it is stable across readings and reverses without a
    /// table, and a world change invalidates it through the epoch it carries.</summary>
    private EntityReference EntityId(MapObjectReference address)
        => new(Prefix + address, _kernel.WorldEpoch,
            address.Category == PlacedModelAddress.Category ? _placedModels.ById(address.Key)?.LifeEpoch ?? 0 : 1);

    /// <summary>
    /// The entity an address text names in this world, or null when this level holds no such object. This is the
    /// one address-to-entity read, and it is the route a mount is already judged by: the category's own reader
    /// parses the text, the category's own world read says whether the object is there, and the identity is the
    /// one this namespace derives from the address. Nothing here parses an address or names an object a second
    /// way, so a behaviour that names a door by address and a plan mounted on that door cannot disagree about
    /// which object they mean.
    ///
    /// A null is reserved for an address this provider does not answer for at all; an address of one of its own
    /// categories whose object this world cannot read is also null here, because this method answers what the
    /// world holds and never a guess — the caller that must report the difference names the refusal.
    /// </summary>
    public EntityReference? AddressReference(string text)
    {
        CheckThread();
        if (text == null) return null;
        if (text.StartsWith(FixedItemIdentity.Category + "/", StringComparison.Ordinal)
            || LevelEndpointAddress.Parse(text) != null) return LevelObjectAddressReader?.Invoke(text);
        if (text.StartsWith("enemy/", StringComparison.Ordinal)) return EnemyAddressReader?.Invoke(text);
        // A level zone is addressed in the same grammar but is not a map object: its identity is the zone kind's
        // own and whether it is there is the zone resource owner's answer (`LevelZoneReference` below).
        if (text.StartsWith(MapObjectLevelZoneAddress.Category + "/", StringComparison.Ordinal)) return LevelZoneReference(text);
        if (!Address(text, out var source, out var address)) return null;
        return Holds(source, address, text) ? EntityId(address) : null;
    }

    public Func<string, EntityReference?>? LevelObjectAddressReader { get; init; }
    public Func<string, EntityReference?>? EnemyAddressReader { get; init; }
    public Func<string, EntityReference, bool>? EnemyAddressMatcher { get; init; }
    /// <summary>The equipment provider's exact authored-address match for a spawned level sentry; null answers that no
    /// equipment provider is running.</summary>
    public Func<string, EntityReference, bool?>? LevelSentryMatcher { get; init; }

    /// <summary>The instance ledger of the random spawn rules. A rule mount (`item-rule`, `enemy-rule`, `model-rule`)
    /// is judged only against it; without a ledger nothing matches.</summary>
    public SpawnRuleRegistry? SpawnRules { get; init; }

    /// <summary>Whether this level holds the object one address of one category names. Each category is read
    /// through its own table, because the three are three different kinds of record: a door and a terminal are
    /// resolved back to the native instance they name — the lookup the mount matcher makes — a generator is held
    /// by the table this world's own generator facts filled (a generator that never reported is not one this
    /// provider can name, and a group address names no single object), and a trigger zone is an authored record
    /// this provider holds rather than one the level generated.</summary>
    private bool Holds(IMapObjectSource source, MapObjectReference address, string text)
    {
        if (ReferenceEquals(source, _zoneSource)) return ZoneOfAddress(address.Key) != null;
        if (ReferenceEquals(source, _interactionPoints)) return InteractionPointOfAddress(address.Key) != null;
        if (ReferenceEquals(source, _placedModels)) return _placedModels.ById(address.Key) != null;
        if (ReferenceEquals(source, _generatorSource)) return ResolveGenerator(text) != null;
        if (ReferenceEquals(source, Shuttleboxes)) return Shuttleboxes.Holds(address);
        return Resolve(source, address) is { } instance && source.IsCurrentAddress(instance, address);
    }

    /// <summary>The trigger zone one authored id names in this world, or null when this install declares no such
    /// zone or the zone belongs to another level. The package document holds every level's zones in one table —
    /// which is why a zone address is not level-scoped by itself — so the filter is the judging tick's own
    /// (`TriggerZoneContract.ForLevel`): only the zones of the level this world is in are objects of this world.
    /// A half with no level reader, and a world whose level cannot be read, names no zone rather than a zone of a
    /// level nothing here could name.</summary>
    private TriggerZone? ZoneOfAddress(string zoneId)
        => _zoneSource.ById(zoneId) is { } zone && _currentLevel != null && Level() is { } level && zone.Level == level
            ? zone : null;

    private InteractionPoint? InteractionPointOfAddress(string pointId)
        => _interactionPoints.ById(pointId) is { Placed: true } point && _currentLevel != null
            && Level() is { } level && point.Level == level ? point : null;

    /// <summary>Whether a reference is still current. A category that can hand back the instance it names is
    /// checked against that instance; a category that cannot — this level's zone table is built while the level
    /// is being built, so a door and a terminal both have windows where nothing can be resolved back — is
    /// answered from the world the reference carries, which is what the entity validation itself already did.
    /// A rejection here is reserved for an instance that positively contradicts the reference.</summary>
    private bool IsCurrent(EntityReference reference)
    {
        if (reference.WorldEpoch != _kernel.WorldEpoch) return false;
        if (!Read(reference, out var source, out var address)) return false;
        return IsCurrent(reference, source, address);
    }

    private bool IsCurrent(EntityReference reference, IMapObjectSource source, MapObjectReference address)
    {
        if (ReferenceEquals(source, _placedModels) && reference.LifeEpoch != _placedModels.ById(address.Key)?.LifeEpoch)
            return false;
        var instance = Resolve(source, address);
        return instance == null ? address.Category != MapObjectCategories.Uplink && MapObjectDoorAddress.ParseWeakRoom(address.Key) == null
            : source.IsCurrentAddress(instance, address);
    }

    private sealed class MapEntityResolver : IRuntimeEntityLifetimeResolver
    {
        private readonly MapObjectModule module;
        private readonly Func<EntityReference, bool>? native;
        internal MapEntityResolver(MapObjectModule module, RuntimeModule definition)
        {
            this.module = module;
            if (definition.EntityResolvers != null)
                definition.EntityResolvers.TryGetValue(EntityKinds.MapObject, out native);
        }
        internal bool IsCurrent(EntityReference reference)
            => reference.WorldEpoch == module._kernel.WorldEpoch
                && (module.IsCurrent(reference) || native?.Invoke(reference) == true);
        public Func<bool> BindLifetime(EntityReference reference)
            => new MapLifeProbe(module, reference, native).IsCurrent;
    }

    private sealed class MapLifeProbe
    {
        private readonly MapObjectModule module;
        private readonly EntityReference reference;
        private readonly Func<EntityReference, bool>? native;
        private readonly IMapObjectSource? source;
        private readonly MapObjectReference address = null!;
        private readonly PlacedModelSource.LifeSlot? modelLife;
        internal MapLifeProbe(MapObjectModule module, EntityReference reference, Func<EntityReference, bool>? native)
        {
            this.module = module; this.reference = reference; this.native = native;
            if (!module.Read(reference, out var found, out address)) return;
            source = found;
            if (ReferenceEquals(source, module._placedModels)) modelLife = module._placedModels.BindLife(address.Key);
        }
        internal bool IsCurrent()
        {
            if (reference.WorldEpoch != module._kernel.WorldEpoch) return false;
            bool own = modelLife != null
                ? modelLife.Instance is { } instance && instance.LifeEpoch == reference.LifeEpoch
                : source != null && module.IsCurrent(reference, source, address);
            return own || native?.Invoke(reference) == true;
        }
    }

    /// <summary>The native instance an address names right now, or null when the category cannot resolve one.
    /// A terminal is resolved through the terminal manager's own registry; a door is resolved through the
    /// category's instance resolver, which the kernel routes to the provider that registered it.</summary>
    private object? Resolve(IMapObjectSource source, MapObjectReference address)
        => ReferenceEquals(source, _placedModels) ? _placedModels.ById(address.Key)
            // Authored records are held by this module's own tables; the native resolver knows only doors and
            // terminals, so asking it for these made every observation of them unavailable.
            : ReferenceEquals(source, _interactionPoints) ? InteractionPointOfAddress(address.Key)
            : ReferenceEquals(source, _zoneSource) ? ZoneOfAddress(address.Key)
            : _resolve(source.Category, address.ToString());

    /// <summary>The current reference of a native object this kind owns: the object's own address, read from its
    /// own members through the category that answers for its type. A hit reaches the same entry with the object
    /// the bullet was resolved against, which is one of the map object's own components rather than the map
    /// object itself, so the game-bound half first climbs to the map object that component belongs to — and a
    /// hit object with no map object above it names nothing and leaves the kind unresolved.</summary>
    private EntityReference? ResolveInstance(object instance)
    {
        if (_kernel.StartupState != RuntimeStartupState.Ready) return null;
        var mapObject = _hit?.Invoke(instance) ?? instance;
        return Source(mapObject) is { } source && source.TryAddress(mapObject) is { } address
            ? EntityId(address) : null;
    }

    private RuntimeEntitySnapshot? Observe(EntityReference reference)
    {
        if (reference.WorldEpoch != _kernel.WorldEpoch) return null;
        if (!Read(reference, out var source, out var address)) return null;
        if (ReferenceEquals(source, _placedModels) && reference.LifeEpoch != _placedModels.ById(address.Key)?.LifeEpoch)
            return null;
        if (Resolve(source, address) is not { } instance) return null;
        if (!source.IsCurrentAddress(instance, address)) return null;
        var observation = source.Read(instance);
        if (observation == null || !observation.AddressMatches) return null;
        // The snapshot carries the object's own position and nothing invented: a position that did not read
        // leaves the whole observation unavailable, exactly as the framework's three-coordinate contract asks.
        if (source.Position(instance) is not { Length: 3 } position) return null;
        return new RuntimeEntitySnapshot(reference, EntityKinds.MapObject, Faction, LifeState,
            Tags(address, observation), Array.Empty<string>(), position);
    }

    /// <summary>The address an entity id was built from, with the category's own reader: the category is the
    /// address's first segment and the reader is looked up by it, so an id that names another category or
    /// another shape is not this provider's and is refused rather than reinterpreted.</summary>
    private bool Read(EntityReference reference, out IMapObjectSource source, out MapObjectReference address)
    {
        source = null!; address = null!;
        if (!reference.Id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        return Address(reference.Id[Prefix.Length..], out source, out address);
    }

    /// <summary>One address text read the one way: its first segment is the category, whose own reader parses the
    /// rest. Both the entity id above and the address-to-entity read go through this, so an address means the same
    /// thing whether it arrived as an id or as an author's literal.</summary>
    private bool Address(string text, out IMapObjectSource source, out MapObjectReference address)
    {
        source = null!; address = null!;
        int slash = text.IndexOf('/');
        if (slash <= 0 || Source(text[..slash]) is not { } found) return false;
        if (found.Parse(text) is not { } parsed) return false;
        source = found; address = parsed;
        return true;
    }

    /// <summary>The `map-object` attachment matcher: a plan attached to an address is dispatched only for the
    /// event's own subject, and only for the subject's own category. The subject's id is the address this
    /// provider read off the instance before it published, so an id that names the mounted address is a
    /// positive reading of that object; a category that can also resolve an address back through native state
    /// must additionally agree, which is what keeps a stale mount from firing for a replaced object.</summary>
    private bool MatchesAttachment(string? category, string reference, EntityReference subject)
    {
        if (category == null || reference.Length == 0) return false;
        if (category is MapObjectCategories.Sentry or MapObjectCategories.Reactor)
        {
            if (MapObjectHostAddress.TryParse(reference) is not { } host || host.Category != category) return false;
            if (category == MapObjectCategories.Sentry)
            {
                try
                {
                    if (LevelSentryMatcher?.Invoke(reference, subject) is not { } matches)
                    {
                        ReportOnce("attachment-source:sentry:" + reference,
                            "map-object sentry attachment rejected: level-sentry-provider-unavailable; " + reference);
                        return false;
                    }
                    return matches && _kernel.IsEntityCurrent(subject);
                }
                catch (RuntimeContractException refusal)
                {
                    ReportOnce("attachment-source:sentry:" + refusal.Code + ":" + reference,
                        "map-object sentry attachment rejected: " + refusal.Code + "; " + reference);
                    return false;
                }
            }
            ReportOnce("attachment-source:" + category,
                "map-object reactor attachment rejected: forge.trigger.objective.reactor_wave has no reactor entity output (only wave and previous).");
            return false;
        }
        // A fixed enemy is a level-placed agent, not a map-object source: the placement registry the game-bound half
        // holds answers whether this subject is the very enemy the address names (never a re-pointed or reused one).
        if (category == "enemy") return EnemyAddressMatcher?.Invoke(reference, subject) == true;
        // A spawn rule hosts "the attached instances this rule produced": the ledger the spawn hooks fill decides,
        // exactly for the world and life the instance was registered with.
        if (category is MapObjectCategories.ItemRule or MapObjectCategories.EnemyRule or MapObjectCategories.ModelRule)
            return MatchesRule(category, reference, subject);
        if (Source(category) is not { } source || source.Parse(reference) is not { } address) return false;
        if (!Read(subject, out var subjectSource, out var subjectAddress)) return false;
        if (!ReferenceEquals(subjectSource, source) || address != subjectAddress) return false;
        var instance = Resolve(source, address);
        return instance == null ? category != MapObjectCategories.Uplink : source.IsCurrentAddress(instance, address);
    }

    /// <summary>A rule mount matches an instance the ledger recorded, with the world and life it was recorded with.
    /// A model rule's interaction key is not a second instance: the hold facts name the interaction point the key is,
    /// and that point is bound to exactly one slot model, so the key is judged as the slot model the ledger holds. A
    /// key whose world is gone, or whose model is not placed in this world, is refused and reported; a key is never
    /// re-pointed to another model, and a point bound to no model is simply not a rule's key.</summary>
    internal bool MatchesRule(string category, string reference, EntityReference subject)
    {
        if (MapObjectCategories.RuleId(category, reference) is not { } ruleId || SpawnRules is not { } rules) return false;
        var instance = subject;
        if (category == MapObjectCategories.ModelRule && Read(subject, out var source, out var address)
            && ReferenceEquals(source, _interactionPoints))
        {
            if (ModelRuleKeyModel(ruleId, subject, address) is not { } model) return false;
            if (rules.RecordedUnderAnotherLife(ruleId, model))
            {
                ReportOnce("model-rule-key:model-life-stale:" + ruleId + ":" + model.Id + ":" + model.LifeEpoch,
                    "model-rule interaction key rejected: model-life-stale; rule=" + ruleId + " key=" + subject.Id
                    + " model=" + model.Id + " life=" + model.LifeEpoch);
                return false;
            }
            instance = model;
        }
        return rules.Matches(ruleId, instance) && _kernel.IsEntityCurrent(instance);
    }

    private EntityReference? ModelRuleKeyModel(string ruleId, EntityReference key, MapObjectReference address)
    {
        if (_interactionPoints.ById(address.Key)?.Appearance is not { } appearance) return null;
        string? refusal = key.WorldEpoch != _kernel.WorldEpoch ? "world-epoch-stale"
            : InteractionPointOfAddress(address.Key) == null || !_kernel.IsEntityCurrent(key) ? "key-not-current"
            : null;
        EntityReference? model = null;
        if (refusal == null)
        {
            model = PlacedModelAddress.Of(appearance.PlacedModelId) is { } modelAddress ? AddressReference(modelAddress.ToString()) : null;
            if (model == null) refusal = "model-not-placed";
            else if (model.WorldEpoch != key.WorldEpoch) refusal = "world-epoch-stale";
        }
        if (refusal == null) return model;
        ReportOnce("model-rule-key:" + refusal + ":" + ruleId + ":" + key.Id + ":" + key.WorldEpoch,
            "model-rule interaction key rejected: " + refusal + "; rule=" + ruleId + " key=" + key.Id
            + " model=" + appearance.PlacedModelId + " world=" + key.WorldEpoch);
        return null;
    }

    /// <summary>The `player` attachment matcher: a plan mounted on "every player of this level" is dispatched for the
    /// player the event names on the mount's own host output (the kernel asks only that output), and only in the
    /// level the reference names — plans are loaded for the whole Rundown, so a player mount without its level would
    /// fire in every level of it. The player is the Player package's identity and must still be current in this
    /// world; the level half is the same strict comparison the `level` matcher makes. This half owns the kind because
    /// it owns the level identity, and it reads the player only through the kernel.</summary>
    private bool MatchesPlayer(string? category, string reference, EntityReference subject)
    {
        if (!EntityKinds.Is(subject, EntityKinds.Player)) return false;
        return _kernel.IsEntityCurrent(subject) && MatchesLevel(category, reference);
    }

    /// <summary>The `level` attachment matcher: a plan attached to one level's identity is dispatched only in
    /// that level. The reference is parsed strictly, and a reference that is not one is reported once and
    /// answers false — never "this must be my level then". A world with no readable expedition answers false the
    /// same way, and says so once, so a behaviour that never fired can be told from one that never matched.</summary>
    private bool MatchesLevel(string? category, string reference)
    {
        if (category != null || MapLevelReference.TryParse(reference) is not { } mounted)
        {
            ReportOnce("level-reference:" + reference, "level attachment reference is not `<rundown block id>:<tier A-E>:<tier index>` "
                + "in decimal without leading zeros: " + reference);
            return false;
        }
        if (Level() is not { } current)
        {
            ReportOnce("level-identity", "level attachment could not be compared: this world reports no readable "
                + "expedition, so no level mount matches in it.");
            return false;
        }
        return mounted == current;
    }

    /// <summary>This world's level identity, read once through the reader the game-bound half handed over and
    /// kept until the next world begins. The reader is the one that records what it read, where the game's own
    /// key for the same expedition is available.</summary>
    private MapLevelReference? Level()
    {
        if (_levelRead) return _level;
        _levelRead = true;
        _level = _currentLevel!();
        return _level;
    }

    /// <summary>The reader of one category. A native instance is classified by the source that answers for its
    /// type, so a door is never read through the terminal reader or the other way round. The trigger zones are the
    /// third category this provider addresses, answered by `TriggerZoneMapObject.cs`, and the power generators
    /// are the fourth, answered by `GeneratorMapObject.cs`.</summary>
    private IMapObjectSource? Source(string category)
        => _doors.Category == category ? _doors
            : _terminals.Category == category || category == MapObjectCategories.Uplink ? _terminals
            : _zoneSource.Category == category ? _zoneSource
            : _interactionPoints.Category == category ? _interactionPoints
            : _placedModels.Category == category ? _placedModels
            : _generatorSource.Category == category ? _generatorSource
            : Shuttleboxes.Category == category ? Shuttleboxes : null;

    private IMapObjectSource? Source(object instance)
        => _doors.TryAddress(instance) != null ? _doors
            : _terminals.TryAddress(instance) != null ? _terminals
            : _zoneSource.TryAddress(instance) != null ? _zoneSource
            : _interactionPoints.TryAddress(instance) != null ? _interactionPoints
            : _placedModels.TryAddress(instance) != null ? _placedModels
            : _generatorSource.TryAddress(instance) != null ? _generatorSource
            : Shuttleboxes.TryAddress(instance) != null ? Shuttleboxes : null;

    /// <summary>The actor of an interaction fact, resolved through the player domain's own instance lookup so a
    /// map object never carries a player identity of its own. A caller that cannot name the actor publishes the
    /// port absent rather than inventing one.</summary>
    private EntityReference? Actor(object? instance)
        => instance == null || _players == null ? null : _players(instance);

    private IReadOnlyList<string> Tags(MapObjectReference address, MapObjectObservation observation)
    {
        var tags = new List<string>(4) { CategoryTag + address.Category, AddressTag + address.ToString() };
        if (observation.Door != null) tags.Add("map-object.door.status=" + observation.Door.State);
        if (observation.Terminal != null) tags.Add("map-object.terminal.status=" + observation.Terminal.State);
        if (observation.Generator != null) tags.Add("map-object.generator.state=" + (observation.Generator.Powered ? "powered" : "unpowered"));
        return tags;
    }

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Map objects require the runtime's own simulation thread.");
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        _lifecycle?.Dispose();
        _expeditions?.Dispose();
        _expeditions = null;
        _registration?.Dispose();
        _published.Clear();
        _accepted.Clear();
        _generators.Clear();
    }

    /// <summary>The level zone one address names in this world, or null when the address is not a level-zone address
    /// or this world holds no zone at its coordinates. Whether the zone is there is asked of the `zone` resource
    /// kind's owner through the kernel's own budgeted resource read — the same owner a plan's compiled zone
    /// reference and the zone selector are answered by — so this provider keeps no second zone table and a world
    /// with no level answers no zone.</summary>
    private EntityReference? LevelZoneReference(string text)
    {
        if (MapObjectLevelZoneAddress.TryParse(text) is not { } address) return null;
        var zone = MapObjectLevelZoneAddress.ZoneReference(address, _kernel.WorldEpoch);
        var answer = _kernel.ResolveResources(RuntimeZones.ResourceKind, new[] { zone.Id });
        return answer.IsComplete && answer.References.Count == 1 && answer.References[0].ResourceId == zone.Id ? zone : null;
    }
}

/// <summary>
/// The address of one level zone itself: `zone/&lt;dimension&gt;/&lt;layer&gt;/&lt;zone&gt;/zone`. It is the same five-segment
/// grammar a door, a terminal or a shuttlebox is written in, so the `forge.selector.target.map_object` row can name
/// the zone an effect such as fog or lighting acts on with the address the website already renders for that zone.
/// The key is the fixed token `zone`: a zone is its own coordinates, and the token only keeps the grammar's five
/// segments.
///
/// A level zone is not a map object. Its identity is the zone kind's own (`gtfo.zone:&lt;d&gt;:&lt;l&gt;:&lt;z&gt;`,
/// <see cref="RuntimeZones"/>), never `gtfo.map_object:zone/...`; it is not a category of the mount matcher, so no plan
/// is mounted on a zone address; and whether this world holds it is the `zone` resource kind's owner's answer, not a
/// table of this provider.
/// </summary>
internal static class MapObjectLevelZoneAddress
{
    public const string Category = "zone";

    /// <summary>The one key a level-zone address carries.</summary>
    public const string Key = "zone";

    /// <summary>Reads a level-zone address: this category, three decimal coordinates and the fixed key.</summary>
    public static MapObjectReference? TryParse(string? text)
        => MapObjectReference.TryParse(text, Category, key => key == Key);

    /// <summary>The address of one level zone. A coordinate that did not read yields no address at all.</summary>
    public static MapObjectReference? Create(int? dimension, int? layer, int? zone)
        => dimension is not { } d || layer is not { } l || zone is not { } z || d < 0 || l < 0 || z < 0
            ? null
            : new MapObjectReference(Category, Text(d), Text(l), Text(z), Key);

    /// <summary>The zone one level-zone address names, in the zone kind's own spelling and in the given world.</summary>
    public static EntityReference ZoneReference(MapObjectReference address, long worldEpoch)
        => RuntimeZones.Reference(worldEpoch, Coordinate(address.Dimension), Coordinate(address.Layer), Coordinate(address.Zone));

    private static int Coordinate(string text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

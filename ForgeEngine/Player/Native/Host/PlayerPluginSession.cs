using ForgeEngine.GameBindings;
using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgePlayer.Native;

/// <summary>Domain-owned lifetime for one Player registration; the host remains the only runtime, clock and
/// authority gate. One registration carries every Player surface — the `gtfo.player` identity, the player selector,
/// the player facts and values, the player commands and the bot orders — because the runtime accepts exactly one
/// provider of an identity namespace and a second registration would be a second provider of it. Map and Weapon
/// name a player through the <see cref="IPlayerDirectory"/> this session publishes on that registration, and route
/// their presentation rows through it; they never keep a second player table.</summary>
internal sealed partial class PlayerPluginSession : IDisposable
{
    /// <summary>The refusal this provider's zone read makes when it cannot place a life it owns. The kernel reports
    /// the code as this provider spelled it, so an unreadable life stays tellable apart from one that stands outside
    /// every zone.</summary>
    internal const string ZoneUnknownCode = "player-zone-unknown";

    private readonly IForgeEngine _kernel;
    private readonly Action<string> _report;
    private readonly Action _releaseNativeWork;
    private bool _disposed, _faulted, _nativeWorkReleased;
    /// <summary>A failure in the identity, life or frame work stops Player for that world only (the world state is cleared, nothing is
    /// disposed); the next level starts clean. Startup failure, shutdown and a failure that disposed a half are <c>_faulted</c>, for good.</summary>
    private readonly global::ForgeEngine.GameBindings.WorldFault _worldFault = new();
    private bool Down => _faulted || _worldFault.Holds(_kernel.WorldEpoch);
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    /// <summary>The one Player registration every half below publishes through.</summary>
    internal RuntimeModuleHandle? Registration { get; private set; }
    internal PlayerIdentityModule Module { get; private set; } = null!;
    /// <summary>The player directory this session attaches its identity half to, which is how every other domain names
    /// a player. It answers nothing once the registration it was published on is gone.</summary>
    internal PlayerDirectory? Directory { get; private set; }
    internal PlayerTextLanguageSession? TextLanguage { get; private set; }
    /// <summary>The action-lock half: it mints one effect handle per lock and holds the players it locked, which
    /// only a live registration can carry.</summary>
    private PlayerActionLockWrites? _actionLock;
    /// <summary>The attribute-modifier half: the one adapter the two sourced-modifier rows and the movement preset
    /// write through. It holds a lifecycle subscription on the registration, so the session owns it and releases it
    /// with the other halves.</summary>
    private AgentModifierAdapter? _agentModifiers;
    /// <summary>The one device hold table of this machine, used by Map and Weapon, with this package's own
    /// three device writers (flashlight, interface, map) registered on it.</summary>
    internal DeviceSwitchboard? Devices { get; private set; }
    private IDisposable[] _deviceWriters = Array.Empty<IDisposable>();
    /// <summary>The native teammate markers and the text attached to them; the HUD value row writes through it.</summary>
    internal PlayerTeammateOverhead? Overhead { get; private set; }
    /// <summary>The native player value source and the halves the player hooks publish through. A hook reads the
    /// installed half rather than a session, so a session that failed before attaching them publishes nothing.</summary>
    private NativePlayerVitals? _vitals;
    private PlayerIncomingDamageRules? _damageRules;
    private PlayerBackpackFacts? _backpackFacts;
    internal PlayerStateFacts? State { get; private set; }
    internal PlayerEventFacts? Events { get; private set; }
    /// <summary>The local player's own key edges: the polled half of the player-event family, which reads only the
    /// keys the loaded plans subscribe to.</summary>
    internal PlayerKeyFacts? Keys { get; private set; }
    internal PlayerLifeFacts? Lives { get; private set; }
    /// <summary>The saved bot rosters of the installed Rundown packages, driven in the host lobby. Null when no package ships one.</summary>
    internal BotSlotProgram? BotSlots { get; private set; }
    /// <summary>The levels' carry-on-back rules. Null when no package ships one or the game refused its hooks.</summary>
    private CarryOnBack? _carryOnBack;
    internal void UseCarryOnBack(CarryOnBack? rules) => _carryOnBack = rules;
    internal bool Faulted => Down;
    /// <summary>Whether the session still serves the world: not disposed and not faulted.</summary>
    internal bool IsLive => !_disposed && !Down;

    internal string? LastFault { get; private set; }
    internal string? LastReporterFailure { get; private set; }
    private PlayerPluginSession(IForgeEngine kernel, Action<string> report, Action releaseNativeWork)
    { _kernel = kernel; _report = report; _releaseNativeWork = releaseNativeWork; }

    /// <summary>The native targets this session's declaration pass refused and the features they took down.</summary>
    internal PlayerHookAvailability Hooks { get; private set; } = PlayerHookAvailability.Complete;

    internal static PlayerPluginSession Start(IForgeEngine kernel, RuntimeLogLevel logLevel, Action<string>? report,
        Action<string>? log, Func<PlayerHookAvailability> declareHooks, Action releaseNativeWork)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(declareHooks);
        ArgumentNullException.ThrowIfNull(releaseNativeWork);
        if (!kernel.IsRegistrationOpen)
            throw new InvalidOperationException("Player must register during dependent plugin Load, before Runtime startup.");
        PlayerPluginSession? session = null;
        report ??= message => session!.WriteLog(RuntimeLogLevel.Error, "player.native-refused", message);
        log ??= message => session!.WriteLog(RuntimeLogLevel.Info, "player.native-observed", message);
        session = new PlayerPluginSession(kernel, report, releaseNativeWork);
        bool declarationsAttempted = false;
        try
        {
            // The one registration is created before every half, because the handle is what a half publishes
            // through. Each entry of the definition reads the half that is current when the kernel asks, so the
            // halves below are built afterwards. Duplicate provider or entity namespace throws atomically before
            // any native callback is declared.
            session.Registration = kernel.RegisterModule(session.Definition(PlayerHookAvailability.Complete), logLevel);
            // Callbacks read PlayerHost.Session, which is set only after Start returns, so a declared target is inert
            // until every half below exists. A refused target takes down only the features it supports: this
            // registration is replaced by one without their bindings, so a plan needing one is refused as
            // binding-unavailable, and their services and device writers are not published. The replacement
            // happens before any half publishes on the registration, so nothing outlives the first one.
            declarationsAttempted = true;
            var hooks = session.Hooks = declareHooks();
            if (hooks.Unavailable.Count != 0)
            {
                session.Registration.Dispose();
                session.Registration = kernel.RegisterModule(session.Definition(hooks), logLevel);
            }
            var registration = session.Registration;
            foreach (var feature in hooks.Unavailable)
                session.WriteLog(RuntimeLogLevel.Error, "player.capability-unavailable", hooks.Describe(feature));
            PlayerCaughtFailures.Attach(report);
            session._actionLock = PlayerActionLockWrites.Attach(registration, () => session.Module, report);
            // The one adapter both sourced-modifier rows write through, and the row the movement preset writes
            // through beside them: one write path onto the native synced-modifier table, never two.
            session._agentModifiers = new AgentModifierAdapter(registration, () => session.Module, report);
            session.Module = new PlayerIdentityModule(registration, kernel, () => !session.Down, log, report);
            session.Module.LifeEnded += session._agentModifiers.OnEntityLost;
            PlayerHost.Directory.Attach(session.Module);
            session.Directory = PlayerHost.Directory;
            session.TextLanguage = PlayerTextLanguageSession.Start(kernel, session.Directory, registration);
            // The directory is the engine's player-resolution entry for other plugins; the bot slots are ForgeDevelopment's.
            registration.PublishService<IPlayerDirectory>(session.Directory);
            // The explicit request the development package makes; the saved roster of a Rundown package takes the same
            // NativeBotSlotRequest path through BotSlotProgram.
            registration.PublishService<PlayerBotSlotBegin>((slot, enabled, character, loadout) =>
            {
                if (session._disposed || session._faulted)
                    throw new RuntimeContractException("player-bot-slots-session-unavailable", "The Player session is released or faulted.");
                ArgumentNullException.ThrowIfNull(loadout);
                return NativeBotSlotRequest.Begin(slot, enabled, character, loadout);
            });
            session.Overhead = new PlayerTeammateOverhead(session.Directory);
            // The device hold table: this machine's own clock, the kernel's tick taken only while something is
            // held, and this package's three writers. Map and Weapon reach it through this session.
            session.Devices = new DeviceSwitchboard(() => UnityEngine.Time.time, report);
            session.Devices.Attach(registration);
            session._deviceWriters = PlayerDeviceWriters.Create(session.Directory)
                .Where(writer => writer.Category switch
                {
                    DeviceCategories.Flashlight => hooks.Offers(PlayerHookService.FlashlightWriter),
                    DeviceCategories.Map => hooks.Offers(PlayerHookService.MapWriter),
                    _ => true
                })
                .Select(session.Devices.Register).ToArray();
            session._vitals = new NativePlayerVitals(session.Module);
            if (hooks.Offers(PlayerHookService.IncomingDamage))
                session._damageRules = new PlayerIncomingDamageRules(session.Module, kernel, registration);
            // The backpack half reads the gate of its one binding, which a refused backpack target leaves out.
            if (!hooks.WithdrawnBindings.Contains(PlayerBackpackContract.ChangedBinding, StringComparer.Ordinal))
                session._backpackFacts = new PlayerBackpackFacts(registration, kernel, session.Module, report);
            session.State = PlayerStateFacts.Attach(registration, kernel, session._vitals, log, report);
            session.Events = PlayerEventFacts.Attach(registration, kernel, session._vitals, log, report);
            // The key poller reads through the same event half it publishes with: one host-only gate, one
            // transition ledger and one event identity serve the whole family.
            session.Keys = PlayerKeyFacts.Attach(registration, kernel, session.Events, report);
            session.Lives = new PlayerLifeFacts(registration, kernel, () => !session.Down, report, log) { World = session.Module };
            // The one value source the query rows read through: the identity half says which life a reference is,
            // this source says what that life holds.
            PlayerValueReads.Attach(new NativePlayerValues(kernel, session.Module));
            return session;
        }
        catch (Exception original)
        {
            try
            {
                session.WriteLog(RuntimeLogLevel.Error, "player.native-refused",
                "Player startup refused: " + original.GetType().Name + ": " + original.Message);
            }
            catch (Exception reporting) { original.Data["ForgePlayer.StartupReportingFailure"] = reporting; }
            // Any residual registration is inert even if rollback itself fails.
            session._faulted = true;
            var errors = new List<Exception>();
            if (declarationsAttempted) session.ReleaseNativeWork(errors);
            session.DetachHalves(errors);
            PlayerHost.Directory.Attach(null);
            if (session.Module != null) Cleanup(session.Module.Dispose, errors);
            if (session.Registration != null) Cleanup(session.Registration.Dispose, errors);
            session._disposed = true;
            if (errors.Count != 0) DomainSession.PreserveCleanupFailure(original, "ForgePlayer.CleanupFailures", new AggregateException(errors), report);
            throw;
        }
    }

    /// <summary>Releases the halves that hold a static installation point or a per-world table of their own,
    /// before the registration they published on is disposed. A half that was never created is skipped.</summary>
    private void DetachHalves(List<Exception> errors)
    {
        var botSlots = BotSlots; BotSlots = null;
        if (botSlots != null) Cleanup(botSlots.Dispose, errors);
        var overhead = Overhead; Overhead = null;
        if (overhead != null) Cleanup(() => overhead.Clear(), errors);
        var textLanguage = TextLanguage; TextLanguage = null;
        if (textLanguage != null) Cleanup(textLanguage.Dispose, errors);
        var carryOnBack = _carryOnBack; _carryOnBack = null;
        if (carryOnBack != null) Cleanup(carryOnBack.Stop, errors);
        var damageRules = _damageRules; _damageRules = null;
        var backpackFacts = _backpackFacts; _backpackFacts = null;
        if (backpackFacts != null) Cleanup(backpackFacts.Dispose, errors);
        if (damageRules != null) Cleanup(damageRules.Dispose, errors);
        var state = State; State = null;
        if (state != null) Cleanup(() => PlayerStateFacts.Detach(state), errors);
        // The key poller publishes through the event half, so it goes before it: a released registration must
        // never be polled or published to.
        var keys = Keys; Keys = null;
        if (keys != null) Cleanup(keys.Dispose, errors);
        var events = Events; Events = null;
        if (events != null) Cleanup(() => PlayerEventFacts.Detach(events), errors);
        var lives = Lives; Lives = null;
        if (lives != null) Cleanup(lives.Dispose, errors);
        // Releasing the action-lock half lifts every lock this provider still holds, before the registration those
        // handles were minted on is gone.
        var actionLock = _actionLock; _actionLock = null;
        if (actionLock != null) Cleanup(() => PlayerActionLockWrites.Detach(actionLock), errors);
        if (PlayerValueReads.Installed is { } reads) Cleanup(() => PlayerValueReads.Detach(reads), errors);
        // The movement preset and the two attribute rows are one adapter, so it goes last of the halves that
        // write: releasing it revokes every modification this provider still holds an id for.
        var agentModifiers = _agentModifiers; _agentModifiers = null;
        if (agentModifiers != null)
        {
            if (Module != null) Module.LifeEnded -= agentModifiers.OnEntityLost;
            Cleanup(agentModifiers.Dispose, errors);
        }
        // Every device this machine still holds is put back before the table goes, and the flashlight and map-page gates open.
        foreach (var writer in _deviceWriters) Cleanup(writer.Dispose, errors);
        _deviceWriters = Array.Empty<IDisposable>();
        var devices = Devices; Devices = null;
        if (devices != null) Cleanup(devices.Dispose, errors);
        Cleanup(FlashlightHoldGate.Clear, errors);
        Cleanup(PlayerDeviceWriters.MapPage.Clear, errors);
        PlayerCaughtFailures.Detach(_report);
    }

    /// <summary>The one Player provider definition: the rows, the support lines and the shape table
    /// <see cref="ModuleDefinition"/> declares — the same registration the release export builds — completed with
    /// the bodies and the runtime surface of every half. Each entry reads the half that is current when the kernel
    /// asks, so the definition can be built before the halves attach to the registration it is declared on. The
    /// bindings of features a refused native target supports are left out.</summary>
    private RuntimeModule Definition(PlayerHookAvailability hooks)
    {
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            [PlayerHealthContract.HandlerName] = context => PlayerHealthAction.Execute(Module, context),
            [PlayerActionContract.TeleportHandlerName] = context => PlayerActions.Teleport(Module, context),
            [PlayerActionContract.InfectionHandlerName] = context => PlayerActions.Infection(Module, context),
            // The one bot-policy row: one native action request per named bot, submitted through the game's own
            // `PlayerAIBot.RequestAction` entry.
            [BotOrderContract.HandlerName] = context => BotOrders.Order(Module, context),
            // The sourced-modifier rows write the one native modification table through the one adapter this
            // session created: a handler table is built before the half that owns the state, so it is a static
            // facade over that half. Their capabilities are declared by `forge.contract.combat`, and this
            // registration binds them here.
            [AgentModifierContract.ApplyHandlerName] = AgentModifierAdapter.ApplyHandler,
            [AgentModifierContract.CancelHandlerName] = AgentModifierAdapter.CancelHandler,
            [PlayerCommandContract.DamageHandlerName] = context => PlayerCommandActions.Damage(Module, context,
                reference => ForgeEnemy.Native.EnemyHost.Session?.Module.Resolve(reference)?.Enemy),
            [PlayerCommandContract.ReviveHandlerName] = context => PlayerCommandActions.Revive(Module, context),
            [PlayerCommandContract.DownHandlerName] = context => PlayerCommandActions.Down(Module, context),
            [PlayerActionLockContract.HandlerName] = PlayerActionLockWrites.Execute,
            [CombatImpulseContract.HandlerName] = context => GenericPlayerActions.Impulse(Module, context),
            [PlayerStaminaContract.HandlerName] = context => GenericPlayerActions.StaminaChange(Module, context),
            [PresentationActionContract.CameraShakeHandlerName] = context => GenericPlayerActions.CameraShake(Module, context),
            [PresentationActionContract.ScreenLiquidHandlerName] = context => GenericPlayerActions.ScreenLiquid(Module, context),
            [PlayerTextLanguageContract.SetHandlerName] = context => TextLanguage?.Set(context)
                ?? Outcome.Rejected(ResultCode.TextLanguageUnavailable)
        };
        var evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [PlayerSelectorContract.HandlerName] = PlayerSelector.Evaluate
        }
            .Concat(PlayerValueReads.Evaluators())
            // The movement-state read, built from this package's own player table.
            .Concat(GenericPlayerActions.Evaluators(() => Module))
            .Append(new KeyValuePair<string, EvaluatorHandler>(PlayerTextLanguageContract.QueryHandlerName,
                PlayerTextLanguageContract.Evaluator(new PlayerTextLanguageContract.Readers(
                    reference => Directory?.PlayerOf(reference) != null,
                    reference => TextLanguage?.Read(reference)))))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        var module = ModuleDefinition.Create(handlers, evaluators) with
        {
            // The sourced-modifier row's duration is the plan's own `effect` block, so its restoration is the
            // kernel's to call: this is the callback it ends one through, releasing every member the command wrote.
            EffectRestores = new Dictionary<string, EffectRestoreHandler>(StringComparer.Ordinal)
            {
                [AgentModifierContract.ApplyHandlerName] = AgentModifierAdapter.RestoreApply
            },
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                [EntityKinds.Player] = reference => Module is { } half && half.IsCurrent(reference)
            },
            // The identity reports every end of a player life (death, despawn, replacement, key conflict, import
            // replacement, world clear), so the kernel does not ask the resolver about players on each tick.
            EntityEndReports = new[] { EntityKinds.Player },
            EntityInstances = new Dictionary<string, Func<EntityReference, object?>>(StringComparer.Ordinal)
            {
                [EntityKinds.Player] = reference => Module?.CurrentAgent(reference)
            },
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>(StringComparer.Ordinal)
            {
                [EntityKinds.Player] = instance => Module?.ResolveInstance(instance)
            },
            ObjectEntityResolvers = new[] { ObjectEntityResolver.Of(instance => Module?.ResolveInstance(instance)) },
            EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>(StringComparer.Ordinal)
            {
                [EntityKinds.Player] = reference => Module?.Observe(reference)
            },
            // Zone judgement reads only where a player stands and whether it lives: the pose read builds no snapshot.
            EntityPoseObservers = new Dictionary<string, EntityPoseObserver>(StringComparer.Ordinal)
            {
                [EntityKinds.Player] = (EntityReference reference, out EntityPose pose) =>
                {
                    pose = default;
                    return Module is { } module && module.ObservePose(reference, out pose);
                }
            },
            // The stamina row runs on the machine that simulates the player it names, so the kernel resolves that
            // player's session; a player this directory cannot answer for has no holder and the step is refused.
            OwnerSessions = OwnerSessionTable(),
            // The player half is the only owner of `gtfo.player`, so it is also the only source of a player
            // candidate set: a step that needs every player asks the kernel for the kind.
            EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>>(StringComparer.Ordinal)
            {
                [EntityKinds.Player] = PlayerCandidates
            },
            // Which zone and which area of it a player stands in are read from the player's own course node, named
            // by the level's three coordinates — the same text the Map provider names a zone with — so a row that
            // filters a candidate set by zone compares one place rather than two.
            EntityZones = new Dictionary<string, Func<EntityReference, EntityReference?>>(StringComparer.Ordinal)
            {
                [EntityKinds.Player] = ZoneOfPlayer
            },
            EntityZoneAreas = new Dictionary<string, Func<EntityReference, int?>>(StringComparer.Ordinal)
            {
                [EntityKinds.Player] = AreaOfPlayer
            },
            // The one player-session table every presentation row of this provider is routed by: the entity a plan
            // named, the player it resolves to and that player's own session id.
            PresentationSessions = new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>(StringComparer.Ordinal)
            {
                [ModuleDefinition.ProviderId] = recipients => Directory?.SessionsOf(recipients)
            }
        };
        return module.Without(hooks.WithdrawnBindings);
    }

    /// <summary>The owner-tier rows of the Player half (stamina, impulse and teleport run on the machine that simulates
    /// the player they name). Each resolves the session of the player the step's first entity names; a player this
    /// directory cannot answer for has no holder and the step is refused. The impulse row also pushes enemies,
    /// which the host simulates, so an enemy recipient resolves to the host's session. A bot is simulated by the host
    /// too and has no machine of its own, so a bot recipient resolves to the host's session as well.</summary>
    private IReadOnlyDictionary<string, Func<EntityReference, string?>> OwnerSessionTable()
    {
        string? Holder(EntityReference reference)
            => Directory is { } directory && directory.PlayerOf(reference) is { } player
                ? directory.SessionOf(player.IsBot ? SNetwork.SNet.Master : player) : null;
        string? ImpulseHolder(EntityReference reference)
            => EntityKinds.Is(reference, EntityKinds.Enemy) ? Directory?.SessionOf(SNetwork.SNet.Master) : Holder(reference);
        var table = new Dictionary<string, Func<EntityReference, string?>>(StringComparer.Ordinal);
        foreach (var entry in PlayerStaminaContract.OwnerSessions(Holder)) table[entry.Key] = entry.Value;
        foreach (var entry in CombatImpulseContract.OwnerSessions(ImpulseHolder)) table[entry.Key] = entry.Value;
        table[Blocks.ActionPlayerTeleport.Id] = Holder;
        return table;
    }

    /// <summary>The player candidate set: the lives the player half still resolves right now, ordered by `id`
    /// ordinal so the answer does not depend on the module's dictionary layout. A half that was never attached or
    /// has already been released is refused by name — the kernel reports `entity-candidates-failed` — never
    /// answered with an empty world. An empty list means the half is registered and tracks no live player.</summary>
    private IReadOnlyList<EntityReference> PlayerCandidates()
    {
        if (Module is not { } players || !players.IsRegistered)
            throw new RuntimeContractException("player-module-unavailable", "No Player identity is registered.");
        var current = players.CurrentPlayers();
        var ordered = new List<EntityReference>(current);
        ordered.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        return ordered;
    }

    /// <summary>The zone one tracked player life stands in: the zone its own course node belongs to, read through the
    /// shared <see cref="GameCourseNodes"/> chain the Enemy provider uses too. Every way this read can fail is the one
    /// refusal <see cref="ZoneUnknownCode"/>, a life this provider no longer tracks included.</summary>
    private EntityReference? ZoneOfPlayer(EntityReference reference)
        => GameCourseNodes.Zone(TrackedAgent(reference), _kernel.WorldEpoch, ZoneUnknownCode);

    /// <summary>Which area of its own zone one tracked player life stands in. A life this provider cannot place
    /// refuses both reads with the same code.</summary>
    private int? AreaOfPlayer(EntityReference reference) => GameCourseNodes.Area(TrackedAgent(reference), ZoneUnknownCode);

    private global::Player.PlayerAgent TrackedAgent(EntityReference reference)
        => Module is { } players && players.CurrentAgent(reference) is { } agent ? agent
            : throw new RuntimeContractException(ZoneUnknownCode, "This provider holds no current life for " + reference.Id + ".");

    /// <summary>The gate every native callback passes: the owning thread, a live session, no fault held for this world.</summary>
    private bool Ready(object callback)
    {
        DomainSession.CheckThread(_threadId, "Player");
        ArgumentNullException.ThrowIfNull(callback);
        return !(_disposed || Down || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped);
    }

    /// <summary>A callback that fails stops the session for this world only and drops the half's per-world tables; the half stays
    /// attached and resets itself on the next world change, so one bad hook never silences every player card until restart.</summary>
    private void WorldFailed(string half, Exception error, Action? clear)
    {
        _worldFault.Mark(_kernel.WorldEpoch);
        var errors = new List<Exception>();
        if (clear != null) Cleanup(clear, errors);
        Fault(half, error, errors);
    }

    internal void Guard(Action<PlayerIdentityModule> callback)
    {
        if (!Ready(callback)) return;
        try { callback(Module); }
        catch (Exception error) { WorldFailed("Player identity", error, Module.ClearWorld); }
    }

    /// <summary>Same as <see cref="Guard(Action{PlayerIdentityModule})"/>, but the callback takes its argument as
    /// state instead of a captured variable, so a native hook that runs every frame — regardless of who is
    /// listening — calls in here without allocating a closure to do it.</summary>
    internal void Guard<T>(Action<PlayerIdentityModule, T> callback, T state)
    {
        if (!Ready(callback)) return;
        try { callback(Module, state); }
        catch (Exception error) { WorldFailed("Player identity", error, Module.ClearWorld); }
    }

    /// <summary>Runs one player-life transition on the owning thread. The half is read late and answers nothing
    /// while it is not attached, because a session that failed before it created the half must not publish a fact
    /// through a registration that never carried its bindings.</summary>
    internal void GuardLifeFacts(Action<PlayerLifeFacts> callback)
    {
        if (!Ready(callback) || Lives is not { } facts) return;
        try { callback(facts); }
        catch (Exception error) { WorldFailed("Player life observation", error, Module.ClearWorld); }
    }

    /// <summary>Runs one player-state transition on the owning thread, with the same failure contract.</summary>
    internal void GuardState(Action<PlayerStateFacts> callback)
    {
        if (!Ready(callback) || State is not { } facts) return;
        try { callback(facts); }
        catch (Exception error) { WorldFailed("Player state observation", error, facts.BeginWorld); }
    }

    /// <summary>Same as <see cref="GuardState(Action{PlayerStateFacts})"/>, but the callback takes its argument as
    /// state instead of a captured variable, so a hook the game's own event funnel reaches for every player event
    /// calls in here without allocating a closure to do it.</summary>
    internal void GuardState<T>(Action<PlayerStateFacts, T> callback, T state)
    {
        if (!Ready(callback) || State is not { } facts) return;
        try { callback(facts, state); }
        catch (Exception error) { WorldFailed("Player state observation", error, facts.BeginWorld); }
    }

    /// <summary>Runs one player-event transition on the owning thread, with the same failure contract.</summary>
    internal void GuardEvents(Action<PlayerEventFacts> callback)
    {
        if (!Ready(callback) || Events is not { } facts) return;
        try { callback(facts); }
        catch (Exception error) { WorldFailed("Player event observation", error, facts.BeginWorld); }
    }

    /// <summary>Same as <see cref="GuardEvents(Action{PlayerEventFacts})"/>, but the callback takes its argument as
    /// state instead of a captured variable, so a hook the game's own event funnel reaches for every player event
    /// calls in here without allocating a closure to do it.</summary>
    internal void GuardEvents<T>(Action<PlayerEventFacts, T> callback, T state)
    {
        if (!Ready(callback) || Events is not { } facts) return;
        try { callback(facts, state); }
        catch (Exception error) { WorldFailed("Player event observation", error, facts.BeginWorld); }
    }

    /// <summary>The one polled readback: the local player's update asks this half to read the keys the loaded
    /// plans subscribe to, with the same failure contract.</summary>
    internal void GuardKeys(Action<PlayerKeyFacts> callback)
    {
        if (!Ready(callback) || Keys is not { } keys) return;
        try { callback(keys); }
        catch (Exception error) { WorldFailed("Player key observation", error, null); }
    }

    internal void WriteLog(RuntimeLogLevel level, string code, string message)
    {
        if (Registration is { } handle && handle.Logs(level))
            handle.Log(level, code, new RuntimeLogFacts { Detail = message });
    }

    private long _frameEpoch = -1, _frameTick = long.MinValue;

    /// <summary>The in-level frame driver. <see cref="ForgeEngine.GameBindings.DomainWorkPhases"/> runs it in Runtime's
    /// native work phase, outside every kernel notification: kernel lifecycle observers are read-only
    /// (<c>lifecycle-observer-mutation</c>), and each step here calls a native body whose hook may publish.
    /// Every frame it polls keys, text language, stamina and carried items, and restores a finished world's device
    /// holds. Once per kernel tick of a world (the cadence the lifecycle <c>TickAdvanced</c> notification gave) it
    /// expires timed device holds and lifts a due action lock. A throw disables the session for this level and is
    /// reported; the lobby roster keeps running.</summary>
    internal void InFrame()
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.PlayerFrame);
        DomainSession.CheckThread(_threadId, "Player");
        if (_disposed || _faulted) return;
        // The lobby roster moves on the lobby's own state; outside the lobby this is one bool.
        using (ForgeCore.HookCounters.Measure(ForgeCore.HookSection.PlayerBotRoster)) BotSlots?.Frame();
        if (_kernel.StartupState != RuntimeStartupState.Ready || Down) return;
        var epoch = _kernel.WorldEpoch;
        var tick = _kernel.CurrentTick;
        try
        {
            using (ForgeCore.HookCounters.Measure(ForgeCore.HookSection.PlayerKeyPoll))
                if (Keys is { Subscribed: true } keys) GuardKeys(keys.PollCallback);
            TextLanguage?.Frame();
            Keys?.Input.StaminaFrame();
            _carryOnBack?.Frame(global::ForgeCore.Frame.GameTime);
            // A finished world's holds are restored as soon as the frame runs, even before the new world's first tick.
            Devices?.RestoreEnded();
            if (tick < 0 || (epoch == _frameEpoch && tick == _frameTick)) return;
            _frameEpoch = epoch; _frameTick = tick;
            Devices?.Expire();
            _actionLock?.Frame(tick);
        }
        catch (Exception error)
        {
            _worldFault.Mark(_kernel.WorldEpoch);
            Fault("Player frame", error, new List<Exception>());
        }
    }

    /// <summary>Starts the saved bot rosters the installed packages ship. A session with none keeps no program.</summary>
    internal void UseBotRoster(IReadOnlyList<BotSlotLevel> levels)
    {
        if (levels.Count == 0 || BotSlots != null) return;
        BotSlots = new BotSlotProgram(levels, new RegistrationBotSlotLog(Registration!));
    }

    /// <summary>Records one native callback failure once: the session is disabled for this level (until restart when a half was
    /// disposed) and the reason is reported through the one reporter this session was given.</summary>
    private void Fault(string half, Exception error, List<Exception> errors)
    {
        string detail = RuntimeDiagnostics.Describe(error);
        LastFault = detail;
        try { _report(half + (_faulted ? " disabled until restart" : " disabled for this level") + " after native callback failure: " + detail + "; cleanupErrors=" + errors.Count); }
        catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
    }

    public void Dispose()
    {
        DomainSession.CheckThread(_threadId, "Player");
        if (_disposed)
        {
            if (_nativeWorkReleased) return;
            var retryErrors = new List<Exception>();
            ReleaseNativeWork(retryErrors);
            if (retryErrors.Count != 0)
                throw new AggregateException("Player shutdown cleanup is still incomplete.", retryErrors);
            return;
        }
        var errors = new List<Exception>();
        DetachHalves(errors);
        PlayerHost.Directory.Attach(null);
        Cleanup(Module.Dispose, errors);
        if (Registration != null) Cleanup(Registration.Dispose, errors);
        _faulted = true;
        ReleaseNativeWork(errors);
        _disposed = true;
        if (errors.Count != 0) throw new AggregateException("Player shutdown cleanup failed.", errors);
    }

    private void ReleaseNativeWork(List<Exception> errors)
    {
        if (_nativeWorkReleased) return;
        try { _releaseNativeWork(); _nativeWorkReleased = true; }
        catch (Exception error) { errors.Add(error); }
    }

    internal float GuardIncomingDamageRule(global::Dam_PlayerDamageBase receiver, float incoming, global::Agents.Agent? source)
    {
        DomainSession.CheckThread(_threadId, "Player");
        if (_disposed || Down || _damageRules == null) return incoming;
        try { return _damageRules.Apply(receiver, incoming, source, PlayerDamageKindToken.Current); }
        catch (Exception error)
        {
            WorldFailed("Player damage rule", error, null);
            return incoming;
        }
    }

    private static void Cleanup(Action action, List<Exception> errors) => DomainSession.Cleanup(action, errors);

}

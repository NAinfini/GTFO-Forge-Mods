using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Player;
using SNetwork;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>Domain-owned lifetime for one Map registration; the host remains the only runtime, clock and
/// authority gate. One registration carries every Map surface — the map-object namespaces this session owns, the
/// environment presentation rows and the level objects — because the runtime accepts exactly one provider of an
/// identity namespace and a second registration would be a second provider of it. A player is named through the
/// <see cref="IPlayerDirectory"/> a player provider publishes through Runtime, never through a table of this package.</summary>
internal sealed partial class MapPluginSession : IDisposable
{
    internal MapTextLanguage TextLanguage { get; private set; } = null!;
    private readonly IForgeEngine _kernel;
    private readonly IPlayerDirectory _players;
    private readonly Action<string> _report;
    // (operation, code) pairs already reported; the pair is the key, so a repeated refusal builds no string.
    private readonly HashSet<(string Operation, string Code)> _optionalPlayerServiceRefusals = new();
    private InteriorDoorObservation? _interiorDoors;
    private bool _disposed, _faulted;
    /// <summary>A failure of a native callback (<see cref="Run{T}"/>), the in-level frame, the clock wake or a failure rule stops Map
    /// for that world only; the next level starts clean. Startup failure and shutdown are <c>_faulted</c>, for good.</summary>
    private readonly global::ForgeEngine.GameBindings.WorldFault _worldFault = new();
    // Two gates. Broken: this session failed, for good or for this world; nothing of Map runs. Down: Broken, or a level that only shows a recording
    // (ForgeCore Puppets.Playback), which runs no behaviour: every device and object action and every behaviour callback reads it. The level's own
    // content (placed models and their text, task objects, fixed placements other than enemies) is built in such a level all the same, through
    // Build, which reads Broken only: a replay shows the level as it was built and sets what the recording says changed on it.
    private bool Broken => _faulted || _worldFault.Holds(_kernel.WorldEpoch);
    private bool Down => Broken || ForgeCore.Puppets.Playback;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    internal IDisposable? ContentStartupRegistration;
    /// <summary>The packages captured at Map Load: the first content generation's Map read.</summary>
    internal MapPackageAdmission? CapturedPackages => _packages;
    internal MapObjectModule? MapObjects { get; private set; }
    /// <summary>The level-object half: scans, generators, containers and the items standing in the level. It is
    /// the only publisher of `gtfo.level_object`, and it is declared on the one registration every other Map
    /// surface is declared on.</summary>
    internal LevelObjectModule? Levels { get; private set; }
    /// <summary>The level-event half: the eight session/objective/zone/portal observation rows and the three
    /// actions. It holds a lifecycle subscription, so it is released with the other halves rather than left to the
    /// registration's own disposal.</summary>
    internal LevelEventModule? LevelEvents { get; private set; }
    /// <summary>The trigger-zone half: the authored volumes this install declared, the judging tick that reads
    /// them and the blocking bodies of the level being played. It publishes through the map-object path the doors
    /// and terminals already use.</summary>
    internal TriggerZoneSession? TriggerZones { get; private set; }
    /// <summary>The fixed-placement half: the authored items and enemies this install declared, placed into a
    /// level once its own generation finished. It publishes nothing — a placement is a native object, not a row —
    /// so it is held only for the lifetime of the documents it read.</summary>
    internal FixedPlacementSession? Placements { get; private set; }
    /// <summary>The random spawn rules (contract M16): the instance ledger the rule hosts are judged against and the
    /// spawn hooks that fill it.</summary>
    internal SpawnRuleSession? SpawnRules { get; private set; }
    /// <summary>The wave-anchor half: the authored settings-id anchors this install declared, read once and
    /// resolved against the level being played by the one survival-wave prefix. It holds no native object, so it
    /// is kept for the lifetime of the documents it read and the slot the hook answers through.</summary>
    internal WaveAnchorSession? WaveAnchors { get; private set; }
    internal WaveResourceSession? WaveResources { get; private set; }
    /// <summary>The scan-coverage half: the authored coverage rows this install declared, applied to each level's
    /// own chained-puzzle instances once that level's generation finished and again for an instance created later.
    /// It holds no native object beyond the plan table of the level it applied, so it lives with the documents it
    /// read.</summary>
    internal ScanCoverageSession? ScanCoverage { get; private set; }
    internal ExtractionPointSession? ExtractionPoints { get; private set; }
    internal ObjectiveRoundsNative? ObjectiveRounds { get; private set; }
    /// <summary>The session's one kernel-clock registration. Idle callbacks only test pending flags; work producers
    /// announce through <see cref="MapClock.Wake"/> without registering during host dispatch.</summary>
    private MapClock? _clock;
    private RuntimeLifecycleSubscription? _endpointLifecycle;
    private MapContentHost? _mapContent;
    // The world signs' answer to a level that only shows a recording (MapLabels draws recorded signs itself).
    private IDisposable? _labelRecording;
    private MapPackageAdmission? _packages;
    private TeleportAreaTable? _teleportAreas;
    private DoorLocksNative? _doorLocks;
    private MapRecipientPresentationState? _recipientPresentation;
    internal MapStateActions? MapState { get; private set; }
    internal DeviceActions? Devices { get; private set; }
    private readonly RundownTextLanguageDefaults _textLanguageDefaults = new();
    internal GlobalLightSession? GlobalLight { get; private set; }
    internal ObjectivePresentationSession? ObjectiveText { get; private set; }
    /// <summary>The replicated record of terminal commands, logs and printed lines; the host writes it, every
    /// machine applies it from the in-level frame.</summary>
    internal TerminalContentLedger? TerminalLedger { get; private set; }
    private RuntimeLifecycleSubscription? _terminalLedgerLifecycle;
    /// <summary>The replicated record of ordinary zone light colours; the host writes it, every machine applies it.</summary>
    internal ZoneLightColorLedger? ZoneLightColors { get; private set; }
    private RuntimeLifecycleSubscription? _zoneLightLifecycle;
    internal ExtractionMoveLedger? ExtractionMoves { get; private set; }
    private RuntimeLifecycleSubscription? _extractionMoveLifecycle;
    /// <summary>The alarm, scan and wave half's attachment: the five execute rows are static handlers, and this is
    /// the registration and kernel they mint and resolve their effect handles through.</summary>
    private AlarmWaveActions? _alarmWave;
    /// <summary>The alarm half: it mints one effect handle per alarm it raises and holds the binding between each
    /// handle and the puzzle it names, which is what `forge.query.map.alarm_state` reads back through.</summary>
    private AlarmActions? _alarms;

    /// <summary>The door and terminal observation half. The two door execute rows and the door value row answer
    /// from the tables it fills, which is why the session owns it and not the hook list.</summary>
    private DoorTerminalFacts? _doorTerminal;
    /// <summary>The terminal log read half: the terminal command interpreter's two log-read entries publish
    /// through it. It holds a lifecycle subscription on the registration, so the session owns it and releases it
    /// with the other halves.</summary>
    private TerminalLogFacts? _terminalLogs;
    /// <summary>The world-event half: the two trigger components a world event object carries publish through it,
    /// and the condition action's one native call is answered by its static body. It holds its own Harmony
    /// instance, so it is released with the other halves rather than left patched.</summary>
    private WorldEventFacts? _worldEvents;
    /// <summary>The session's failure-rule ledger: the policies this world has recorded, the read that answers
    /// them, and the decision the game's own failure check is handed back. It holds a lifecycle subscription on
    /// the registration, so the session owns it and releases it with the other halves.</summary>
    internal FailRuleActions? FailRules { get; private set; }
    /// <summary>The doors whose alarm was changed at runtime, replicated through its own native state key.</summary>
    internal DoorAlarmSwaps? DoorAlarms { get; private set; }
    /// <summary>The item placement half: the two insertion entries publish through it. It holds a lifecycle
    /// subscription on the registration, so the session owns it and releases it with the other halves.</summary>
    internal ItemSlotFacts? ItemSlots { get; private set; }
    /// <summary>The level-world half: a spitter's burst, a zone light switch and a fog transition start publish
    /// through it, and its three patches read its cached gates through `MapWorldFacts.Current`. It holds a lifecycle
    /// subscription on the registration, so the session owns it and releases it with the other halves.</summary>
    internal MapWorldFacts? World { get; private set; }
    /// <summary>The success-page half: it holds the page's original title and music of this world and puts them
    /// back when the world changes, so the session owns it and releases (and restores) it with the other halves.</summary>
    private SuccessScreenActions? _successScreen;
    /// <summary>The world the native address tables belong to. The kernel owns the epoch; the zone table reads
    /// it here so every native instance it holds is invalidated with the world instead of outliving it.</summary>
    internal long WorldEpoch => _kernel.WorldEpoch;
    /// <summary>The runtime this session is declared on, for the native readers that resolve an address outside
    /// this file.</summary>
    internal IForgeEngine Kernel => _kernel;
    /// <summary>The player directory this session reads every player through.</summary>
    internal IPlayerDirectory Players => _players;
    internal bool Faulted => Down;
    /// <summary>What the host's shared sound rows ask the Map about its own objects, text and door alarms.</summary>
    internal MapSoundPlaces SoundPlaces { get; } = new();

    /// <summary>A named map area's dimension index in the current world, or null when the area or the world is unknown.</summary>
    internal int? AreaDimension(string id, long epoch)
        => epoch == _kernel.WorldEpoch && _teleportAreas is { } areas ? areas.Dimension(id, LevelIdentity.Read()) : null;

    /// <summary>Whether a point lies inside a named map area of the current world, or null when the area or the world is unknown.</summary>
    internal bool? AreaContains(string id, long epoch, double[] point)
        => epoch == _kernel.WorldEpoch && _teleportAreas is { } areas ? areas.Contains(id, epoch, LevelIdentity.Read(), point, RoomResolver.Lookup) : null;

    /// <summary>A named map area's authored landing in the current world (world `x y z` then a unit forward vector), or null.</summary>
    internal double[]? AreaLanding(string id, long epoch)
        => epoch == _kernel.WorldEpoch && _teleportAreas is { } areas ? areas.Landing(id, epoch, LevelIdentity.Read(), RoomResolver.Lookup) : null;

    internal string? LastFault { get; private set; }
    internal string? LastReporterFailure { get; private set; }
    internal MapPluginSession(IForgeEngine kernel, IPlayerDirectory players, Action<string> report)
    { _kernel = kernel; _players = players; _report = report; }

    /// <summary>The native targets this session's declaration pass refused and the features they took down.</summary>
    internal MapHookAvailability Hooks { get; private set; } = MapHookAvailability.Complete;

    /// <summary>Starts the session. Runtime retains the declared native installations for the process; disposing
    /// this session releases only its state. A session without a declaration pass offers every feature.</summary>
    internal static MapPluginSession Start(IForgeEngine kernel, RuntimeLogLevel logLevel, Action<string> report,
        MapPackageAdmission packages, Func<MapHookAvailability>? declareHooks = null)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(packages);
        if (!kernel.IsRegistrationOpen)
            throw new InvalidOperationException("Map must register during dependent plugin Load, before Runtime startup.");
        var players = ForgePlayer.Native.PlayerHost.Directory;
        var session = new MapPluginSession(kernel, players, report) { _packages = packages };
        session.SpawnRules = new SpawnRuleSession(() => kernel.WorldEpoch, LevelIdentity.Read, () => session.Levels,
            agent => kernel.ResolveEntityInstance(EntityKinds.Enemy, agent), report);
        try
        {
            // The map-object module owns the one Map registration, because it declares every Map surface: the
            // map-object namespaces, the `map-object` and `level` attachment matchers, and the provider entries
            // every other half of this package answers through. Duplicate provider or entity namespace throws
            // atomically before any native hook is declared, and each half attaches to the handle only after
            // that registration exists. The level reader is the game-bound half of the level matcher: the module
            // compares, this session reads.
            MapObjectModule NewMapObjects() => new MapObjectModule(kernel, logLevel, new DoorSource(report), new TerminalSource(report),
                Resolve, instance => session.TryOptionalPlayerService(static (s, value) => value switch
                    {
                        SNet_Player player => s.ReferenceOf(player),
                        PlayerAgent agent => s.ReferenceOf(agent),
                        _ => null
                    }, instance, "map-object-player-actor", out EntityReference? reference) ? reference : null,
                () => Session.IsHost, report, static _ => { }, LevelIdentity.Read, MapObjectHit.Instance,
                generators: new GeneratorSource(report))
            {
                // The generator category's own value row reads through the game-bound half that holds the
                // instances: the module owns the shape and the resource table, and this is the one reader that can
                // read a native generator into them.
                GeneratorStateReader = GeneratorObservation.ReadState,
                // The shuttlebox category: the objects the level built for its zones' dumbwaiter placements.
                Shuttleboxes = new ShuttleboxSource(report),
                LevelObjectAddressReader = address => address.StartsWith(FixedItemIdentity.Category + "/", StringComparison.Ordinal)
                    ? session.Levels is { } levels ? session.Placements?.ItemReference(address, levels) : null
                    : LevelEndpointAddress.Parse(address)?.Category switch
                    {
                        LevelEndpointAddress.FogRepeller => FogRepellerObservation.Reference(address, kernel.WorldEpoch),
                        LevelEndpointAddress.Extraction => ExtractionObservation.Reference(address, kernel.WorldEpoch),
                        _ => null
                    },
                EnemyAddressReader = address => session.Placements?.EnemyReference(address),
                EnemyAddressMatcher = (address, subject) => session.Placements?.EnemyMatches(address, subject) == true,
                // Weapon's level-sentry table is the one record of which spawned sentry an authored address became.
                LevelSentryMatcher = (address, subject) => ForgeWeapon.Native.WeaponNativeSession.Current?.Adapter.LevelSentries.Matches(address, subject),
                SpawnRules = session.SpawnRules!.Registry
            };
            // The one registration is created before every half, because the handle is what a half publishes
            // through and one registration has to declare all of their rows at once. Each entry of the definition
            // reads the half that is current when the kernel asks, so the halves below are built afterwards, and
            // a half that was never built refuses by name instead of publishing through rows nothing declared.
            session.MapObjects = NewMapObjects();
            session.MapObjects.Register(session.WithTriggerZoneCommands(session.Definition(MapHookAvailability.Complete)));
            // Session callbacks read MapHost.Session after Start returns; content callbacks read rows activated
            // during content startup. A refused target takes down only the features it supports: when a
            // withdrawn feature owns bindings, this registration is replaced by one without them, so a plan that
            // needs one is refused as binding-unavailable, and a service or applier of a withdrawn feature is not
            // offered. The replacement happens before any half publishes on the registration.
            if (declareHooks != null)
            {
                var hooks = session.Hooks = declareHooks();
                if (hooks.WithdrawnBindings.Count != 0)
                {
                    session.MapObjects.Dispose();
                    session.MapObjects = NewMapObjects();
                    session.MapObjects.Register(session.WithTriggerZoneCommands(session.Definition(hooks)));
                }
            }
            // The hooks that carry more than one callback class answer only while their whole feature stands.
            ElevatorLandingObservation.Enabled = session.Hooks.Offers(MapHookFeatures.ElevatorLandingObservationFeature);
            EnemyGroupPool.Start();
            session.MapObjects.Registration.PublishService<IMapInspectionService>(new MapInspectionService(session));
            session.MapObjects.Registration.PublishService<IMapCaptureService>(new MapCaptureService(session));
            session.TextLanguage = new();
            session.MapObjects.Registration.PublishService<MapGenerationReadback>(CompiledMapGenerator.Readback);
            session.MapObjects.Registration.PublishService<IPlayerTextLanguageDefaults>(session._textLanguageDefaults);
            session._endpointLifecycle = session.MapObjects.Registration.ObserveLifecycle(value =>
            {
                var timing = ForgeCore.GenerationAttempt.Timing;
                var timedAdmission = timing.Active && value.Kind == RuntimeLifecycleKind.WorldChanged;
                var admissionCompleted = false;
                if (timedAdmission)
                    ForgeCore.GenerationAttempt.MarkBoundary(timing.Generation, "map-admission-started",
                        "MapPluginSession.RuntimeLifecycle.WorldChanged");
                try
                {
                    if (value.IsMigration)
                    {
                        // The level stays and so does everything built in it: only the world number moves.
                        session.FollowMigration(value);
                        admissionCompleted = true;
                        return;
                    }
                    if (value.Kind == RuntimeLifecycleKind.WorldChanged)
                        session.ReportPendingLevelTuning(value.Current.WorldEpoch, ending: false);
                    else if (value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
                        session.ReportPendingLevelTuning(value.Current.WorldEpoch, ending: true);
                    if (value.Kind == RuntimeLifecycleKind.WorldChanged
                        || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
                    {
                        FogRepellerObservation.Clear();
                        // A world the bridge began under a level that stays keeps the build placing that level.
                        if (value.Kind == RuntimeLifecycleKind.WorldChanged)
                            CompiledMapGenerator.WorldBegan(GameRuntimeBridge.KeepsBuiltLevel ? value.PreviousWorldEpoch : null, value.Current.WorldEpoch);
                        else CompiledMapGenerator.Clear();
                        // The ended world's global wave numbers go back to what the game held before this package wrote them.
                        GlobalWaveApplier.Restore();
                        // The dimension outside environments this world wrote go back to their values before it.
                        DimensionEnvironmentActions.Restore();
                        session.ObserveGenerationWorld(value.Current.WorldEpoch,
                            value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped);
                        ExtractionObservation.Clear();
                        MapLabels.Clear();
                        session._recipientPresentation?.Clear();
                        session.MapState?.ClearDoorState();
                        session._doorTerminal?.BeginWorld();
                    }
                    else if (value.Kind == RuntimeLifecycleKind.CheckpointRestored)
                    {
                        MapLabels.Clear("checkpoint-restored");
                        WorldEventFacts.Current?.CheckpointRestored();
                        session.PlacedModels?.ResetPrivateText();
                        session._recipientPresentation?.CheckpointRestored();
                        // The game keeps the failed attempt's wave heat through a reload; the host, who draws, puts it back.
                        if (value.Current.IsHost == true) GlobalWaveApplier.CheckpointRestored();
                        // The restore respawned the saved enemies as new agents: the authored fixed enemies are bound to them again.
                        session.Run(session.Placements, placements =>
                        {
                            var live = new List<Enemies.EnemyAgent>();
                            foreach (var enemy in ForgeCore.Enemies.Live) if (enemy != null) live.Add(enemy);
                            placements.RebindEnemiesAfterCheckpoint(live);
                        }, "fixed enemy checkpoint rebind");
                    }
                    admissionCompleted = true;
                }
                finally
                {
                    if (timedAdmission)
                        ForgeCore.GenerationAttempt.MarkBoundary(timing.Generation,
                            admissionCompleted ? "map-admission-returned" : "map-admission-failed",
                            "MapPluginSession.RuntimeLifecycle.WorldChanged");
                }
            });
            // Every record of this package goes to the registration's own log, so the log is attached the moment the
            // registration exists: every half built below may already write through it.
            MapLog.Attach(session.MapObjects.Registration);
            session._recipientPresentation = new MapRecipientPresentationState(kernel, players);
            // What native parts reported before the registration existed, then one explicit record per withdrawn
            // feature: the package starts, and every plan or authored row that needed a withdrawn feature is refused.
            MapReport.Flush();
            foreach (var (feature, text) in session.Hooks.Records())
                MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.CapabilityUnavailable, text, RuntimeLogValue.Quote("feature", feature));
            session._teleportAreas = TeleportAreaTable.ReadDocuments(Array.Empty<(string, string)>(), report);
            session._mapContent = new MapContentHost(kernel, session.MapObjects.Registration, report);
            session.MapObjects.Registration.RegisterAuthoringRoomResolver((worldEpoch, sourcePrefab, scope) =>
            {
                if (scope.Authored is { } authored)
                {
                    var locator = new RoomLocator(authored.ZoneAuthorId, scope.Dimension, scope.Layer,
                        scope.LocalZoneIndex, authored.RoomId, authored.RoomRevision, sourcePrefab, authored.InstanceId);
                    if (!RoomResolver.TryPlace(worldEpoch, locator, out var room, out var refusal))
                        return new AuthoringRoomResolution(refusal, Array.Empty<AuthoringRoomHit>());
                    return new AuthoringRoomResolution(null,
                        new[] { new AuthoringRoomHit(room!.GeomorphInstanceId, room.ZoneInstanceId) });
                }
                var answer = RoomResolver.Resolve(worldEpoch, sourcePrefab,
                    new RoomScope(scope.Dimension, scope.Layer, scope.LocalZoneIndex));
                var rooms = answer.Rooms.Select(room =>
                    new AuthoringRoomHit(room.GeomorphInstanceId, room.ZoneInstanceId)).ToArray();
                return new AuthoringRoomResolution(answer.Refusal, rooms);
            });
            session.Levels = new LevelObjectModule(kernel, session.MapObjects.Registration, report)
            {
                // The scan value row reads through the game-bound half: the module owns the shape and the table,
                // and this is the one reader that can read a native scan into them.
                ScanStateReader = LevelObjectObservation.ReadScanState
            };
            session.MapObjects.LevelObjects = session.Levels;
            session.LevelEvents = new LevelEventModule(kernel, session.MapObjects.Registration, report);
            // The item placement half publishes through the same registration; the host is the authority.
            session.ItemSlots = new ItemSlotFacts(kernel, session.MapObjects.Registration, () => Session.IsHost, report);
            // The level-world half publishes through the same registration; the host is the authority.
            session.World = new MapWorldFacts(kernel, session.MapObjects.Registration, () => Session.IsHost, report);
            MapWorldFacts.Current = session.World;
            // The success page's half: it restores the page's originals when the world changes.
            // An earlier session's page restore that failed is retried before this world writes anything.
            try { SuccessScreenActions.RetryUnrestored(); }
            catch (Exception error) { report("map.success-screen-restore-retry-failed: " + error.GetType().Name); }
            session._successScreen = SuccessScreenActions.For(session.MapObjects.Registration, players, report);
            var terminalLedger = new TerminalContentLedger(kernel, session.TextLanguage, TerminalContentLedger.ResolveByAddress, report);
            session.TerminalLedger = terminalLedger;
            // A new world or a stopped runtime ends the record: the terminals it names are gone.
            session._terminalLedgerLifecycle = session.MapObjects.Registration.ObserveLifecycle(value =>
            {
                if (value.Kind == RuntimeLifecycleKind.WorldChanged && !value.IsMigration
                    || value.Current.StartupState is RuntimeStartupState.Stopped or RuntimeStartupState.Failed) terminalLedger.Clear();
            }, replayCurrent: false);
            var zoneLights = new ZoneLightColorLedger(kernel, ZoneLightColorLedger.ZoneLights, report);
            session.ZoneLightColors = zoneLights;
            session._zoneLightLifecycle = session.MapObjects.Registration.ObserveLifecycle(value =>
            {
                if (value.Kind == RuntimeLifecycleKind.WorldChanged && !value.IsMigration
                    || value.Current.StartupState is RuntimeStartupState.Stopped or RuntimeStartupState.Failed) zoneLights.Clear();
            }, replayCurrent: false);
            var extractionMoves = new ExtractionMoveLedger(kernel, () => !session.Down, report);
            session.ExtractionMoves = extractionMoves;
            session._extractionMoveLifecycle = session.MapObjects.Registration.ObserveLifecycle(value =>
            {
                if (value.Kind == RuntimeLifecycleKind.WorldChanged && !value.IsMigration
                    || value.Current.StartupState is RuntimeStartupState.Stopped or RuntimeStartupState.Failed) extractionMoves.Clear();
            }, replayCurrent: false);
            session.ObjectiveText = new ObjectivePresentationSession(kernel, session.MapObjects.Registration,
                () => LevelIdentity.Read()?.ToString(), () => !session.Down, report);
            // The door and terminal half answers the definition's two door execute rows and its door value row
            // through the tables it fills, and the patch classes reach it through `DoorTerminalFacts.Current`.
            session._doorTerminal = new DoorTerminalFacts(kernel, session.MapObjects.Registration, () => !session.Down,
                report, TerminalObservation.BySyncId, TerminalObservation.Address, DoorTerminalFacts.DoorOwner,
                session.WeakDoorPlacement,
                player => session.TryOptionalPlayerService(static (s, value) => s.ReferenceOf(value), player,
                    "door-terminal-player-actor", out EntityReference? reference) ? reference : null,
                enemy => kernel.ResolveEntityInstance(EntityKinds.Enemy, enemy) is { } reference && kernel.IsEntityCurrent(reference)
                    ? reference : null);
            DoorTerminalFacts.Current = session._doorTerminal;
            session._interiorDoors = new InteriorDoorObservation(kernel, LevelIdentity.Read);
            // The terminal log read half, whose two patches reach it through `TerminalLogFacts.Current`. It reads
            // the file through the terminal's own log table and the reader through the terminal's own synced
            // interaction source, and it takes the terminal address from the same observation half the door and
            // terminal rows are addressed through.
            session._terminalLogs = new TerminalLogFacts(kernel, session.MapObjects.Registration,
                () => !session.Down, report, TerminalObservation.Address,
                instance => session.TryOptionalPlayerService(
                    static (s, value) => value is PlayerAgent agent ? s.ReferenceOf(agent) : null,
                    instance, "terminal-log-player-actor", out EntityReference? reference) ? reference : null);
            TerminalLogFacts.Current = session._terminalLogs;
            // The world-event half, attached after the registration exists because it patches against the
            // registration's own gates: its four patches reach it through `WorldEventFacts.Current`, and the one
            // condition action is a static body that reads nothing but the request.
            session._worldEvents = WorldEventFacts.Attach(kernel, session.MapObjects.Registration,
                () => Session.IsHost, report);
            // The zones are handed to the map-object module before any tick judges them, and the tick patch
            // reaches this one session: the registration exists by now, which is what the judging module publishes
            // through. The room resolver is the one that reads the level the game generated — it names an authored
            // room by the prefab object its `Assets/` path loads, so a zone whose room this level does not hold
            // exactly once is refused by name and left unjudged instead of being placed at a guessed world pose.
            session.TriggerZones = TriggerZoneSession.Start(kernel, session.MapObjects, () => Session.IsHost && !ForgeCore.Puppets.Playback,
                LevelIdentity.Read, room => RoomResolver.Lookup(kernel.WorldEpoch, room), report,
                Array.Empty<TriggerZone>());
            session.StartInteractionPoints(Array.Empty<InteractionPoint>());
            // Fixed placements remain absent until the Runtime's post-content callback admits their owning
            // Rundowns. Native hooks and this session's nullable readers are already safe before that happens.
            // Wave anchors keep their existing startup snapshot and native wave path.
            session.WaveAnchors = WaveAnchorSession.Start(() => kernel.WorldEpoch, LevelIdentity.Read, report,
                Array.Empty<WaveAnchor>());
            session.WaveResources = WaveResourceSession.Start(kernel, session.MapObjects.Registration, report,
                new WaveResourceRegistry());
            // Scan startup waits for the same prepared package snapshot as every Map sidecar (CommitContent).
            // The one clock. Zones and interaction points are asked for work every time it looks; the light table and
            // the label table have no way to be asked — new work is announced from inside the command
            // handler that makes it, which is not a stage that could call this half — so each announces itself
            // through its own hook below. These jobs share the one registration,
            // which is taken once in this safe construction window; idle ticks do no native work.
            session._clock = new MapClock(session.MapObjects.Registration,
                () =>
                {
                    session.TriggerZones?.Refresh();
                    session.TryOptionalPlayerService(static s => s.RefreshInteractionPoints(), "interaction-point-refresh");
                },
                () => (session.TriggerZones?.Pending ?? false) || session.InteractionPointsPending(),
                () =>
                {
                    session.TriggerZones?.Tick();
                    session.TryOptionalPlayerService(static s => s.TickInteractionPoints(), "interaction-point-tick");
                },
                () => LightColorFades.Count > 0
                    || MapLabels.Active || (session.TriggerZones?.PresentationPending ?? false)
                    || (session.ObjectiveText?.Pending ?? false) || EntryLandingApplier.Pending,
                seconds =>
                {
                    session.TryOptionalPlayerService(static s => s.ObjectiveText?.Refresh(), "objective-text-refresh");
                    if (EntryLandingApplier.Pending) EntryLandingApplier.Tick(seconds);
                    LightColorFades.Tick(kernel.WorldEpoch, seconds);
                    session.TryOptionalPlayerService(static s => MapLabels.Tick(s.WorldEpoch), "map-label-language");
                    session.TryOptionalPlayerService(static s => s.TriggerZones?.Advance(), "trigger-zone-label-language");
                },
                // The frame runs once per kernel tick, so one engine frame's delta would drop the frames in between:
                // the clock takes the game time since its own previous frame (capped, so an idle gap is not one huge step).
                session.SincePreviousFrame, LightColorFades.Clear);
            session.AttachInteractionPointClock();
            session._labelRecording = MapLabels.ProvideRecording();
            session.TriggerZones.WorkScheduled = session._clock.Wake;
            LightColorFades.WorkScheduled = session._clock.Wake;
            // The five alarm/scan/wave rows are static handlers that mint and resolve their handles through the
            // kernel, so this is the one line that hands them their registration; it runs on the thread the kernel
            // was built on, which is the thread every half of this session is created on.
            session._alarmWave = AlarmWaveActions.Attach(kernel, session.MapObjects.Registration);
            // The alarm half is attached on the same line and for the same reason: it mints an effect handle per
            // alarm it raises and can only do that on a live registration, so it is created after
            // `RegisterModule` returned. Its bindings are dropped with the world that minted them.
            session._alarms = AlarmActions.Attach(session.MapObjects.Registration, () => !session.Down,
                report);
            // The two encounter query rows resolve their handles through the same registration.
            EncounterStateActions.Attach(session.MapObjects.Registration);
            // A host migration rebinds the puzzle handles those halves minted to the same puzzles on the new host.
            MapHandleHandover.Register(session.MapObjects.Registration, session.Levels!);
            // The session's failure-rule ledger. Its forced policies are judged from the life the Player
            // package's identity records: one table for one world, reached by the hook and by the read row alike.
            session.FailRules = new FailRuleActions(session.MapObjects.Registration, kernel,
                    reference => session.TryOptionalPlayerService(static (s, value) => s.LifeOf(value), reference,
                        "failure-rule-player-life", out RuntimePlayerLife? life) ? life : null, report);
            session.DoorAlarms = new DoorAlarmSwaps(session.MapObjects.Registration, kernel);
            return session;
        }
        catch (Exception original)
        {
            // Any residual registration is inert even if rollback itself fails.
            session._faulted = true;
            var errors = new List<Exception>();
            session.DetachHalves(errors);
            if (session.MapObjects is { IsRegistered: true } registered) MapLog.Detach(registered.Registration);
            if (session.MapObjects != null) DomainSession.Cleanup(session.MapObjects.Dispose, errors);
            session._disposed = true;
            if (errors.Count != 0) DomainSession.PreserveCleanupFailure(original, "ForgeMap.CleanupFailures", new AggregateException(errors), report);
            throw;
        }
    }

    /// <summary>Releases the halves that hold a static installation point or a per-world table of their own,
    /// before the registration they published on is disposed. A half that was never created is skipped, so a
    /// session that failed while starting releases exactly what it managed to build.</summary>
    private void DetachHalves(List<Exception> errors)
    {
        if (ObjectiveText is { } objectiveText) { ObjectiveText = null; DomainSession.Cleanup(objectiveText.Dispose, errors); }
        var terminalLedgerLifecycle = _terminalLedgerLifecycle; _terminalLedgerLifecycle = null;
        if (terminalLedgerLifecycle != null) DomainSession.Cleanup(terminalLedgerLifecycle.Dispose, errors);
        var terminalLedger = TerminalLedger; TerminalLedger = null;
        if (terminalLedger != null) DomainSession.Cleanup(terminalLedger.Dispose, errors);
        var recipientPresentation = _recipientPresentation; _recipientPresentation = null;
        if (recipientPresentation != null) DomainSession.Cleanup(recipientPresentation.Dispose, errors);
        var mapState = MapState; MapState = null;
        if (mapState != null) DomainSession.Cleanup(mapState.Dispose, errors);
        var devices = Devices; Devices = null;
        if (devices != null) DomainSession.Cleanup(devices.Dispose, errors);
        var extractionMoveLifecycle = _extractionMoveLifecycle; _extractionMoveLifecycle = null;
        if (extractionMoveLifecycle != null) DomainSession.Cleanup(extractionMoveLifecycle.Dispose, errors);
        var extractionMoves = ExtractionMoves; ExtractionMoves = null;
        if (extractionMoves != null) DomainSession.Cleanup(extractionMoves.Dispose, errors);
        var zoneLightLifecycle = _zoneLightLifecycle; _zoneLightLifecycle = null;
        if (zoneLightLifecycle != null) DomainSession.Cleanup(zoneLightLifecycle.Dispose, errors);
        var zoneLights = ZoneLightColors; ZoneLightColors = null;
        if (zoneLights != null) DomainSession.Cleanup(zoneLights.Dispose, errors);
        if (_doorLocks is { } doorLocks) { _doorLocks = null; DomainSession.Cleanup(doorLocks.Dispose, errors); }
        var doorTerminal = _doorTerminal;
        _doorTerminal = null;
        if (doorTerminal != null) DomainSession.Cleanup(() => DoorTerminalFacts.Current = null, errors);
        var terminalLogs = _terminalLogs;
        _terminalLogs = null;
        if (terminalLogs != null) DomainSession.Cleanup(() => { TerminalLogFacts.Current = null; terminalLogs.Dispose(); }, errors);
        // The world-event half unpatches itself, so it goes before the registration it published on is disposed.
        var worldEvents = _worldEvents; _worldEvents = null;
        if (worldEvents != null) DomainSession.Cleanup(() => WorldEventFacts.Detach(worldEvents), errors);
        // The failure-rule ledger releases its lifecycle subscription with the world's other halves, before the
        // registration it was taken on is gone; no rule of a dead world survives into the next one.
        var failRules = FailRules; FailRules = null;
        if (failRules != null) DomainSession.Cleanup(failRules.Dispose, errors);
        var doorAlarms = DoorAlarms; DoorAlarms = null;
        if (doorAlarms != null) DomainSession.Cleanup(doorAlarms.Dispose, errors);
        var levels = Levels; Levels = null;
        if (MapObjects != null) MapObjects.LevelObjects = null;
        if (levels != null) DomainSession.Cleanup(levels.Dispose, errors);
        var levelEvents = LevelEvents; LevelEvents = null;
        if (levelEvents != null) DomainSession.Cleanup(levelEvents.Dispose, errors);
        var itemSlots = ItemSlots; ItemSlots = null;
        if (itemSlots != null) DomainSession.Cleanup(itemSlots.Dispose, errors);
        // Disposing the level-world half also clears `MapWorldFacts.Current`, so its patches go quiet first.
        var world = World; World = null;
        if (world != null) DomainSession.Cleanup(world.Dispose, errors);
        // Releasing the success-page half puts the page's originals back before its registration is gone.
        // The field is only given up once the page is really restored: a failed restore keeps the half, in the
        // static retry list the next session drains, so the original title and music are not lost with the session.
        var successScreen = _successScreen;
        if (successScreen != null)
        {
            DomainSession.Cleanup(successScreen.Dispose, errors);
            if (!successScreen.IsDisposed) SuccessScreenActions.RetainUnrestored(successScreen);
            _successScreen = null;
        }
        // The zones go before the registration they publish through: the half releases this world's blocking
        // bodies and its lifecycle subscription, so a released registration is never judged or published to.
        var zones = TriggerZones; TriggerZones = null;
        if (zones != null) DomainSession.Cleanup(zones.Dispose, errors);
        CleanupInteractionPoints(errors);
        CleanupPlacedModels(errors);
        var interiorDoors = _interiorDoors; _interiorDoors = null;
        if (interiorDoors != null) DomainSession.Cleanup(interiorDoors.Dispose, errors);
        // The placements hold the documents they read and nothing of the registration, but they go with the other
        // halves so a session that is released releases every table it read.
        var placements = Placements; Placements = null;
        if (placements != null) DomainSession.Cleanup(placements.Dispose, errors);
        var spawnRules = SpawnRules; SpawnRules = null;
        if (spawnRules != null) DomainSession.Cleanup(spawnRules.Dispose, errors);
        // The wave anchors hold the documents they read and nothing of the registration, but they go with the
        // other halves so a session that is released releases every table it read.
        var anchors = WaveAnchors; WaveAnchors = null;
        if (anchors != null) DomainSession.Cleanup(anchors.Dispose, errors);
        // The scan coverage holds the plan table of the level it applied and nothing of the registration, but it
        // goes with the other halves so a released session leaves no coverage applied through a dead half.
        var coverage = ScanCoverage; ScanCoverage = null;
        EntryLandingApplier.Forget();
        var objectiveRounds = ObjectiveRounds; ObjectiveRounds = null;
        if (objectiveRounds != null) DomainSession.Cleanup(objectiveRounds.Dispose, errors);
        var extractionPoints = ExtractionPoints; ExtractionPoints = null;
        if (extractionPoints != null) DomainSession.Cleanup(extractionPoints.Dispose, errors);
        var waveResources = WaveResources; WaveResources = null;
        if (waveResources != null) DomainSession.Cleanup(waveResources.Dispose, errors);
        if (coverage != null) DomainSession.Cleanup(coverage.Dispose, errors);
        // A released registration must never be minted through again: the static rows read this attachment, and a
        // session that can restart would otherwise hand the next registration's commands a dead one.
        var alarmWave = _alarmWave; _alarmWave = null;
        if (alarmWave != null) DomainSession.Cleanup(() => AlarmWaveActions.Detach(alarmWave), errors);
        // The alarm half goes the same way: its handle bindings point at a level, so they are dropped before the
        // registration the handles were minted on is released.
        var alarms = _alarms; _alarms = null;
        if (alarms != null) DomainSession.Cleanup(() => AlarmActions.Detach(alarms), errors);
        // The encounter-state rows drop the registration before it is released.
        DomainSession.Cleanup(EncounterStateActions.Detach, errors);
    }

    /// <summary>Runs one disposable native half on the owning thread. Every observer uses this one failure
    /// boundary: a callback failure stops the session for the rest of this world and reports the same bounded diagnostic.
    /// The half is kept: each half drops its world state on the world change, so the next level runs it again.
    /// Level-event callbacks additionally wake the shared clock after their observation lands.</summary>
    internal void Run<T>(T? owner, Action<T> callback, string label, bool wake = false) where T : class
        => Guarded(owner, callback, label, wake, Down);

    /// <summary>Builds part of the level's own content (<see cref="Run{T}"/>'s failure boundary), also in a level that only shows a recording.</summary>
    internal void Build<T>(T? owner, Action<T> callback, string label) where T : class => Guarded(owner, callback, label, false, Broken);

    private void Guarded<T>(T? owner, Action<T> callback, string label, bool wake, bool closed) where T : class
    {
        DomainSession.CheckThread(_threadId, "Map");
        ArgumentNullException.ThrowIfNull(callback);
        if (string.IsNullOrEmpty(label)) throw new ArgumentException("A callback boundary needs a label.", nameof(label));
        if (_disposed || closed || owner == null) return;
        if (_kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try
        {
            callback(owner);
            if (wake) _clock?.Wake();
        }
        catch (Exception error)
        {
            _worldFault.Mark(_kernel.WorldEpoch);
            string detail = RuntimeDiagnostics.Describe(error);
            LastFault = detail;
            try { _report("Map " + label + " disabled for this level after native callback failure: " + detail); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    /// <summary>An optional player service may be absent or register after Map. That refuses only this read and is
    /// reported once per operation; it never faults the Map session or escapes into the patched game callback.</summary>
    internal bool TryOptionalPlayerService(Action<MapPluginSession> callback, string operation)
    {
        try { callback(this); return true; }
        catch (RuntimeContractException error) when (IsOptionalPlayerServiceMissing(error))
        { ReportOptionalPlayerService(error, operation); return false; }
    }

    internal bool TryOptionalPlayerService<TState, T>(Func<IPlayerDirectory, TState, T> callback, TState state, string operation, out T result)
    {
        if (ForgePlayer.Native.PlayerHost.Players is not { } players)
        {
            result = default!;
            if (_optionalPlayerServiceRefusals.Add((operation, PlayerDirectoryService.MissingCode)))
            {
                try { _report("Map optional player service unresolved: operation=" + operation + "; code=" + PlayerDirectoryService.MissingCode); }
                catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
            }
            return false;
        }
        try { result = callback(players, state); return true; }
        catch (RuntimeContractException error) when (IsOptionalPlayerServiceMissing(error))
        { result = default!; ReportOptionalPlayerService(error, operation); return false; }
    }

    internal static bool IsOptionalPlayerServiceMissing(RuntimeContractException error)
        => error.Code is PlayerDirectoryService.MissingCode or PlayerTextLanguageService.MissingCode;

    private void ReportOptionalPlayerService(RuntimeContractException error, string operation)
    {
        if (!_optionalPlayerServiceRefusals.Add((operation, error.Code))) return;
        try { _report("Map optional player service unresolved: operation=" + operation + "; code=" + error.Code + "; " + error.Message); }
        catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
    }

    /// <summary>The identity string the `level` attachment matcher compares, read once per world. The
    /// expedition-start row publishes the same string, so a plan mounted on a level and a plan waiting for that
    /// level's own start agree about which level they are in.</summary>
    internal string? LevelReference() => LevelIdentity.Read()?.ToString();

    /// <summary>The level's global wave numbers (`forge.globalWave.Settings`), applied once for each world the
    /// session sees. They are fields the level loads with and not a step a plan runs, so they have no handler and no
    /// registration: the map field documents were read at load, and the numbers land on the two components the game
    /// keeps them on when the level is there. Only the host applies them — the draw and the spawn it decides are
    /// the host's. Player arrival tries first; the existing in-level frame retries the same epoch until the level
    /// identity and population manager are ready.</summary>
    internal void ApplyLevelTuning()
    {
        var epoch = _kernel.WorldEpoch;
        if (Down || _kernel.Lifecycle.IsHost != true) return;
        if (_tuningEpoch != epoch)
        {
            try
            {
                if (GlobalWaveApplier.Apply(LevelIdentity.Read()) == GlobalWaveApplyResult.NotReady)
                    _tuningPendingEpoch = epoch;
                else { _tuningEpoch = epoch; _tuningPendingEpoch = -1; }
            }
            catch (Exception error) { _tuningEpoch = epoch; _tuningPendingEpoch = -1; FieldApplyFailed("global-wave", error); }
        }
    }

    /// <summary>Closes one retryable tuning attempt when its world can no longer become ready. The lifecycle
    /// observer calls this once while the provider log is still attached; repeated stop/dispose signals see the
    /// cleared epoch and cannot duplicate the diagnostic.</summary>
    internal void ReportPendingLevelTuning(long currentEpoch, bool ending)
    {
        if (_tuningPendingEpoch < 0 || !ending && _tuningPendingEpoch == currentEpoch) return;
        var pending = _tuningPendingEpoch;
        _tuningPendingEpoch = -1;
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyFailed,
            "group=global-wave epoch=" + pending + " reason=the level identity or population manager never became ready",
            RuntimeLogValue.Of("group", "global-wave"), RuntimeLogValue.Of("epoch", pending),
            RuntimeLogValue.Of("code", "not-ready"));
    }

    /// <summary>The level's authored sentries (`forge.eosext.LevelSpawnedSentry`), spawned once per world by the host
    /// through the game's own gear spawn, which replicates them to every peer. The spawn runs the game's backpack and
    /// deploy bodies, whose weapon-package hooks write to the kernel, so it cannot run inside a lifecycle
    /// notification (observers are read-only, `lifecycle-observer-mutation`). It is asked from
    /// <see cref="InLevelFrame"/> on every kernel tick, and the spawner answers only once the level is built and in
    /// play (the rooms a sentry stands in are generated after the first player spawns) and the host's own agent
    /// exists, so a world not ready yet is simply asked again.</summary>
    private void SpawnLevelSentries()
    {
        var epoch = _kernel.WorldEpoch;
        if (_sentryEpoch == epoch || Down || _kernel.Lifecycle.IsHost != true) return;
        var level = LevelIdentity.Read();
        if (!LevelSentrySpawner.Pending(level)) { _sentryEpoch = epoch; return; }
        try
        {
            if (LevelSentrySpawner.Spawn(epoch, level)) _sentryEpoch = epoch;
        }
        catch (Exception error)
        {
            _sentryEpoch = epoch;
            FieldApplyFailed("level-sentry", error);
        }
    }

    /// <summary>One field group whose native write threw, as the Map provider's own error record: the group and the
    /// exception type are the values a reader filters on, and the message stays in the detail. The log never throws
    /// into the level build that called it.</summary>
    private static void FieldApplyFailed(string group, Exception error)
        => MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyFailed,
            "group " + group + " threw " + error.GetType().Name + ": " + error.Message,
            RuntimeLogValue.Quote("group", group), RuntimeLogValue.Quote("exception", error.GetType().Name));

    private long _tuningEpoch = -1, _tuningPendingEpoch = -1;
    private long _sentryEpoch = -1;

    /// <summary>A host migration keeps the level, so nothing built in it is cleared, destroyed or placed again: every
    /// table stamped with the old world number moves to the new one. The new host also marks the once-per-world work
    /// (sentries, global wave numbers) done, because the previous host already did it.</summary>
    private void FollowMigration(RuntimeLifecycleEvent value)
    {
        var to = value.Current.WorldEpoch;
        if (value.PreviousWorldEpoch is not { } from) return;
        CompiledMapGenerator.FollowWorld(from, to);
        GeneratorObservation.Follow(from, to);
        ExtractionObservation.Follow(from, to);
        VanillaWorldObjects.Follow(from, to);
        KeyPlacementAudit.FollowWorld(from, to);
        WorldEventFacts.Current?.Follow(from, to);
        Placements?.FollowWorld(from, to);
        SpawnRules?.FollowWorld(from, to);
        _interiorDoors?.Follow(from, to);
        ScanCoverage?.FollowWorld(from, to);
        LightColorFades.FollowWorld(from, to);
        ObjectiveText?.FollowWorld(from, to);
        _tuningEpoch = _sentryEpoch = to;
        ObserveGenerationWorld(to, stopped: false);
    }

    private float _clockTime = -1f;
    private float SincePreviousFrame()
    {
        var now = UnityEngine.Time.time;
        var step = _clockTime < 0f ? UnityEngine.Time.deltaTime : Math.Min(now - _clockTime, 0.1f);
        _clockTime = now;
        return Math.Max(step, 0f);
    }

    /// <summary>The map's in-level frame driver: the one place the Map package's per-tick native work runs. Kernel
    /// lifecycle observers are read-only (<c>lifecycle-observer-mutation</c>), and this work is not: placing the
    /// level's zones and interaction points, judging zones (which queries entities and publishes facts) and
    /// spawning the level's sentries (whose game bodies reach the weapon package's hooks, which write to the
    /// kernel). Its per-tick <see cref="DomainWorkPhases"/> entry (<see cref="Tick"/>) calls it from Runtime's native
    /// frame phase, outside every kernel notification, at most once per kernel tick of a world and only while the
    /// Runtime is ready, so a paused or migrating kernel, which does not advance, is not driven. Each step keeps its
    /// own host, readiness and once-per-world checks.</summary>
    private void InLevelFrame()
    {
        DomainSession.CheckThread(_threadId, "Map");
        if (_disposed || Broken) return;
        try
        {
            // A level that only shows a recording keeps its placed models' own presentation (pending models, language, labels facing the camera); no behaviour runs.
            if (ForgeCore.Puppets.Playback) { TryOptionalPlayerService(static s => s.PlacedModels?.Tick(), "placed-model-language"); return; }
            ApplyLevelTuning();
            TriggerZones?.Frame();
            TryOptionalPlayerService(static s => s.InteractionPoints?.Frame(), "interaction-point-frame");
            TryOptionalPlayerService(static s => s.PlacedModels?.Tick(), "placed-model-language");
            TryOptionalPlayerService(static s => s.ObjectiveText?.TickLanguage(), "objective-text-language");
            TryOptionalPlayerService(static s => s.TerminalLedger?.Frame(), "terminal-ledger-frame");
            TryOptionalPlayerService(static s => s._recipientPresentation?.Frame(), "private-presentation-frame");
            MapState?.Frame();
            TryOptionalPlayerService(static s => s.Devices?.Frame(), "device-hold-frame");
            TryOptionalPlayerService(static s => s.WaveResources?.RelayFrame(), "wave-relay-frame");
            TryOptionalPlayerService(static s => s.ZoneLightColors?.Frame(), "zone-light-ledger-frame");
            TryOptionalPlayerService(static s => s.ExtractionMoves?.Frame(), "extraction-move-frame");
            TryOptionalPlayerService(static s => s._successScreen?.TickLanguage(), "success-title-language");
            _interiorDoors?.Frame();
            SpawnLevelSentries();
            TryOptionalPlayerService(static s => s._clock?.Frame(), "map-clock-player-presentation");
        }
        catch (Exception error)
        {
            _worldFault.Mark(_kernel.WorldEpoch);
            string detail = RuntimeDiagnostics.Describe(error);
            LastFault = detail;
            try { _report("Map in-level frame disabled for this level after native callback failure: " + detail); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    /// <summary>Announces that the level's players changed: the Player package reconciled its identity after a
    /// native spawn or despawn, and the zone half may now have a placed zone with somebody to judge. It is the one
    /// wake a player change owes the clock, taken on the owning thread with the same failure contract as every
    /// other native callback.</summary>
    internal void PlayersChanged()
    {
        DomainSession.CheckThread(_threadId, "Map");
        if (_disposed || Down || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try { _recipientPresentation?.Wake(); _clock?.Wake(); }
        catch (Exception error)
        {
            _worldFault.Mark(_kernel.WorldEpoch);
            string detail = RuntimeDiagnostics.Describe(error);
            LastFault = detail;
            try { _report("Map clock disabled for this level after native callback failure: " + detail); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    /// <summary>Runs one failure-rule decision on the owning thread. It is reached from the game's own per-frame
    /// failure check, so the half is read late and answers nothing while it is not attached — and a failure here
    /// is reported and disables the session exactly as every other native callback's is, because a rule that
    /// cannot be applied is not a rule the level may keep running under.</summary>
    internal void ApplyFailRules(ref bool failed)
    {
        DomainSession.CheckThread(_threadId, "Map");
        var rules = FailRules;
        if (_disposed || Down || rules == null) return;
        if (_kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try { rules.Apply(ref failed); }
        catch (Exception error)
        {
            _worldFault.Mark(_kernel.WorldEpoch);
            var errors = new List<Exception>();
            DomainSession.Cleanup(rules.BeginWorld, errors);
            string detail = RuntimeDiagnostics.Describe(error);
            LastFault = detail;
            try { _report("Map failure rule disabled for this level after native callback failure: " + detail + "; cleanupErrors=" + errors.Count); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    internal IDisposable RegisterContentStartup(Action<AdmittedContentSnapshot> activation,
        IReadOnlyList<RundownPack> discovered, Action<Exception> rollback)
        => (_mapContent ?? throw new InvalidOperationException("Map content host is unavailable."))
            .RegisterStartup(activation, discovered, rollback);

    /// <summary>The Map content of the committed content generation (null before the first one).</summary>
    internal MapContentGeneration? Content => _content;
    private MapContentGeneration? _content;

    /// <summary>Commits one content generation's Map content (contract I-RELOAD tiers B and C), at startup and at a reload alike:
    /// the packages are validated against the accepted content entries, each published Rundown's native rows are written in place of
    /// the previous generation's (same slot when a row changed, removed when no package has it any more), the menu is installed in
    /// place of the previous one, and every Map half takes this generation's sidecar rows. A redo (the game rebuilt its data tables
    /// and lost Forge's rows) writes the same input's rows and menu again and keeps the sidecar halves, which lost nothing. Anything
    /// that fails is undone before the exception leaves, and the previous generation stays as it was.</summary>
    internal MapContentGeneration CommitContent(MapPackageAdmission packages, AdmittedContentSnapshot content, bool redo)
    {
        DomainSession.CheckThread(_threadId, "Map");
        if (_disposed || Down) throw new InvalidOperationException("Map content requires a live Map session.");
        ArgumentNullException.ThrowIfNull(packages); ArgumentNullException.ThrowIfNull(content);
        var previous = _content;
        var menu = new RundownMenu(packages.CapturedPacks);
        var occupied = RundownMenu.ObserveTables(MapRundownRows.Owner);
        MapContentGeneration generation;
        if (redo && previous != null)
        {
            // The same read: the previous generation's validated packages and accepted rows, written again.
            generation = new MapContentGeneration(packages, previous.Prepared, menu, new MapRundownRows(previous.Rows))
            { Accepted = previous.Accepted, KeptSidecars = true };
            try
            {
                generation.Published = menu.Publish(content, previous.Prepared.Packs, occupied, packages.Report,
                    (pack, rows) => generation.Rows.Publish(pack.Package, rows));
                generation.Rows.RemoveStale();
                menu.Install();
            }
            catch (Exception error) { Undo(generation, previous, error); throw; }
        }
        else
        {
            // A reload never installs a hook: a plan needs the generator hooks Authoring put on with the first generation.
            var prepared = _mapContent!.PreparePackages(packages, content, menu.Installed, occupied, packages.Report,
                previous == null ? CompiledMapGenerator.PrepareProgram : CompiledMapGenerator.RequireInstalled);
            generation = new MapContentGeneration(packages, prepared, menu, new MapRundownRows(previous?.Rows));
            try
            {
                generation.Published = menu.Publish(content, prepared.Packs, occupied, packages.Report,
                    (pack, rows) => prepared.PublishModels(pack, () => generation.Rows.Publish(pack.Package, rows)));
                generation.Rows.RemoveStale();
                generation.Accepted = prepared.Published(generation.Published.Packs);
                menu.Install();
                ActivateSidecars(generation);
            }
            catch (Exception error) { Undo(generation, previous, error); throw; }
        }
        _content = generation;
        _mapContent!.Committed(generation.Prepared);
        return generation;
    }

    private void Undo(MapContentGeneration attempted, MapContentGeneration? previous, Exception error)
    {
        try { RevertContent(attempted, previous); }
        catch (Exception undo) { DomainSession.PreserveCleanupFailure(error, "ForgeMap.ContentUndoFailure", undo, _report); throw; }
    }

    /// <summary>A committed generation that did not stand (another participant's commit failed): every Map half takes the previous
    /// generation's rows again, the previous menu is installed again, this generation's model registrations and table rows are taken
    /// back. Throws when any of that failed (the tables are then in a state no generation describes).</summary>
    internal void RevertContent(MapContentGeneration attempted, MapContentGeneration? previous)
    {
        DomainSession.CheckThread(_threadId, "Map");
        var failures = new List<Exception>();
        if (!attempted.KeptSidecars)
            try { ActivateSidecars(previous); } catch (Exception error) { failures.Add(error); }
        try { attempted.Menu.Uninstall(); previous?.Menu.Install(); } catch (Exception error) { failures.Add(error); }
        if (!ReferenceEquals(attempted.Prepared, previous?.Prepared))
            try { attempted.RollbackModels(); } catch (Exception error) { failures.Add(error); }
        try { attempted.Rows.Revert(); } catch (Exception error) { failures.Add(error); }
        _content = previous;
        _mapContent?.Committed(previous?.Prepared);
        if (failures.Count != 0) throw new AggregateException("Map content could not be reverted to the previous generation.", failures);
    }

    /// <summary>The generation stands: the objects its rows replaced are let go and the previous generation's model registrations
    /// (and the Unity objects their placements made) are disposed.</summary>
    internal void ReleaseContent(MapContentGeneration committed, MapContentGeneration? previous)
    {
        committed.Rows.Release();
        if (previous != null && !ReferenceEquals(previous.Prepared, committed.Prepared)) MapContentHost.Dispose(previous.Prepared);
    }

    /// <summary>The committed generation's sidecar rows are taken out of every Map half (its sidecar lease).</summary>
    internal void WithdrawSidecars()
    {
        DomainSession.CheckThread(_threadId, "Map");
        ActivateSidecars(null);
    }

    /// <summary>After the committed generation's leases were revoked (its table lease, the last): no Map content stands.</summary>
    internal void ForgetContent()
    {
        _content = null;
        _mapContent?.Committed(null);
    }

    /// <summary>Every Map half takes one generation's sidecar rows (none for null). The halves built per generation (scan coverage,
    /// extraction points, door locks, objective rounds, global light, fixed placements, placed models) are disposed and built again
    /// from these rows; the long-lived ones take the rows in place. Runs in a clean lobby or at startup, when no world holds them.
    /// A hook group a row needs is already on in Authoring (the first generation put every group on), so this installs none.</summary>
    private void ActivateSidecars(MapContentGeneration? generation)
    {
        var errors = new List<Exception>();
        DisposeGenerationHalves(errors);
        if (errors.Count != 0) throw new AggregateException("The previous generation's Map halves could not be released.", errors);
        var accepted = generation?.Accepted ?? new MapPreparedContent(Array.Empty<MapPreparedContent>());
        var publishedPacks = generation?.Published.Packs ?? Array.Empty<RundownPack>();
        _textLanguageDefaults.Admit(publishedPacks);
        (generation?.Packages ?? _packages!).ActivateFields(accepted);
        _mapContent!.Activate(accepted);
        _teleportAreas!.Admit(accepted.Areas);
        TriggerZones!.AdmitRows(accepted.TriggerZones);
        AdmitInteractionPoints(accepted.InteractionPoints);
        StartPlacedModels(accepted.PlacedModels, accepted.Images, accepted.ModelAssemblies);
        _interiorDoors!.Admit(accepted.InteriorDoors);
        WaveAnchors!.AdmitRows(accepted.WaveAnchors);
        WaveResources!.AdmitRegistry(accepted.Waves);
        // A half whose hook Runtime refused is not built: its authored rows are refused by name, once, instead of
        // standing in a session whose native entry never calls them.
        // The scan-step callbacks go on with the first authored coverage row; a level without one never carries them.
        if (Hooks.Offers(ObjectHookContent.ScanCoverage)
            && (accepted.ScanCoverage.Count == 0 || MapLazyHooks.Ensure(MapLazyHooks.Group.ScanCoverage) == null))
        {
            ScanCoverage = ScanCoverageSession.Start(() => _kernel.WorldEpoch, LevelIdentity.Read, _report, accepted, out var activateScans);
            activateScans(accepted);
        }
        else RefuseWithdrawn("scan-coverage", accepted.ScanCoverage.Count != 0);
        if (Hooks.Offers(ObjectHookContent.ExtractionPoint))
            ExtractionPoints = new ExtractionPointSession(accepted.ExtractionPoints, () => _kernel.WorldEpoch, LevelIdentity.Read, _report);
        else RefuseWithdrawn("extraction-point", accepted.ExtractionPoints.Count != 0);
        if (Hooks.Offers(MapHookFeatures.ElevatorCageEndAlignmentFeature)) EntryLandingApplier.Activate(accepted.EntryLandings, () => _kernel.WorldEpoch);
        else RefuseWithdrawn("entry-landing", accepted.EntryLandings.Count != 0);
        if (Hooks.Offers(ObjectHookContent.DoorLocks))
            _doorLocks = new DoorLocksNative(_kernel, accepted.DoorLocks, accepted.GeneratorSources);
        else RefuseWithdrawn("door-locks", accepted.DoorLocks.Count != 0);
        if (accepted.ObjectiveRounds.Count == 0 || MapLazyHooks.Ensure(MapLazyHooks.Group.ObjectiveRounds) == null)
            ObjectiveRounds = new ObjectiveRoundsNative(accepted.ObjectiveRounds, LevelIdentity.Read);
        else RefuseWithdrawn("objective-rounds", true);
        GlobalLight = new GlobalLightSession(_kernel, MapObjects!.Registration, accepted.GlobalLight);
        if (generation == null) return;
        var published = generation.Published;
        if (Hooks.Offers(ObjectHookContent.FixedPlacements))
            Placements = FixedPlacementSession.Start(() => _kernel.WorldEpoch, LevelIdentity.Read,
                agent => _kernel.ResolveEntityInstance(EntityKinds.Enemy, agent), _report,
                _players, published, accepted);
        else RefuseWithdrawn("fixed-placements", accepted.Items.Count + accepted.Enemies.Count + accepted.Sacks.Count
            + accepted.Landings.Count + accepted.Controllers.Count + accepted.Keys.Count != 0);
        SpawnRules?.Admit(published);
        if (SpawnRules is { } rules && (rules.AnyOf(SpawnRuleKind.Item) && !Hooks.Offers(MapHookFeatures.SpawnRuleItemsFeature)
            || rules.AnyOf(SpawnRuleKind.Enemy) && !Hooks.Offers(MapHookFeatures.SpawnRuleEnemiesFeature))) RefuseWithdrawn("spawn-rules", true);
    }

    /// <summary>Releases the halves one generation's rows built (see <see cref="ActivateSidecars"/>).</summary>
    private void DisposeGenerationHalves(List<Exception> errors)
    {
        var coverage = ScanCoverage; ScanCoverage = null;
        if (coverage != null) DomainSession.Cleanup(coverage.Dispose, errors);
        var extractionPoints = ExtractionPoints; ExtractionPoints = null;
        if (extractionPoints != null) DomainSession.Cleanup(extractionPoints.Dispose, errors);
        if (_doorLocks is { } doorLocks) { _doorLocks = null; DomainSession.Cleanup(doorLocks.Dispose, errors); }
        var objectiveRounds = ObjectiveRounds; ObjectiveRounds = null;
        if (objectiveRounds != null) DomainSession.Cleanup(objectiveRounds.Dispose, errors);
        if (GlobalLight is { } globalLight) { GlobalLight = null; DomainSession.Cleanup(globalLight.Dispose, errors); }
        var placements = Placements; Placements = null;
        if (placements != null) DomainSession.Cleanup(placements.Dispose, errors);
        CleanupPlacedModels(errors);
    }

    /// <summary>A content kind whose native hook was refused is not applied. The withdrawal itself was recorded once
    /// at startup (`map.capability-unavailable`); this is the matching refusal at the point the rows would have
    /// been applied, so a reader of the log sees which authored group was left out.</summary>
    private static void RefuseWithdrawn(string group, bool hasRows)
    {
        if (!hasRows) return;
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyRefused,
            "group=" + group + " code=native-hook-unavailable",
            RuntimeLogValue.Of("group", group), RuntimeLogValue.Of("code", "native-hook-unavailable"));
    }

    private DomainWorkPhases? _work, _tickWork;
    private long _frameFailureEpoch = -1;
    private int _timedPostFactoryFrameGeneration;

    /// <summary>Takes the frame phase of this session: Runtime calls <see cref="Frame"/> every frame and then
    /// <see cref="Tick"/> once per kernel tick, from its own update, per world, outside every kernel notification. Both
    /// end with the registration they hang on.</summary>
    internal void StartFrame()
    {
        if (_work != null) throw new InvalidOperationException("The Map frame phase is already started.");
        _work = new DomainWorkPhases("forge.map.frame", 1, MapObjects!.Registration, _kernel, Frame);
        _tickWork = new DomainWorkPhases("forge.map.tick", 1, MapObjects!.Registration, _kernel, Tick, perTick: true);
        MapCamera.Attach();
    }

    private DomainWorldObservation? _arrivals;

    /// <summary>Takes this session's world-bound lease on Runtime's shared player-arrival entry. A lease Runtime
    /// refuses withdraws the arrival half of the level-players feature: one `map.capability-unavailable` record names
    /// it, and the package keeps running without the arrival signal.</summary>
    internal void StartPlayerArrivals()
    {
        if (_arrivals != null) throw new InvalidOperationException("The Map player-arrival lease is already taken.");
        const string Withdrawn = "feature=" + MapHookFeatures.LevelPlayersFeature
            + " withdrawn=authored wave tuning is not applied at the first player and the global light is not refreshed on arrival refused=PlayerManager.OnPlayerSpawned=";
        try
        {
            _arrivals = LevelPlayerArrival.Subscribe(MapObjects!.Registration, _kernel, reason =>
                MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.CapabilityUnavailable, Withdrawn + reason,
                    RuntimeLogValue.Quote("feature", MapHookFeatures.LevelPlayersFeature)));
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.CapabilityUnavailable, Withdrawn + error.Message,
                RuntimeLogValue.Quote("feature", MapHookFeatures.LevelPlayersFeature));
        }
    }

    /// <summary>A player arrived in the level (Runtime's shared membership entry, after the native body returned).</summary>
    internal void PlayerArrived()
    {
        var timing = ForgeCore.GenerationAttempt.Timing;
        var timedArrival = timing.Active && timing.FactoryDone && !timing.Entered;
        var arrivalCompleted = false;
        if (timedArrival)
            ForgeCore.GenerationAttempt.MarkBoundary(timing.Generation, "map-player-arrival-started",
                "MapPluginSession.PlayerArrived");
        try
        {
            ApplyLevelTuning();
            PlayersChanged();
            GlobalLight?.PlayersChanged();
            arrivalCompleted = true;
        }
        finally
        {
            if (timedArrival)
                ForgeCore.GenerationAttempt.MarkBoundary(timing.Generation,
                    arrivalCompleted ? "map-player-arrival-returned" : "map-player-arrival-failed",
                    "MapPluginSession.PlayerArrived");
        }
    }

    /// <summary>Once per kernel tick: the in-level work (<see cref="InLevelFrame"/>). A failure is recorded as a frame's is.</summary>
    internal void Tick()
    {
        if (_disposed) return;
        try
        {
            using (HookCounters.Measure(HookSection.MapFrame)) InLevelFrame();
        }
        catch (Exception error) { FrameFailed(error); }
    }

    /// <summary>The first frame failure of each world is recorded; later ones in the same world are not, and the next
    /// world records its own.</summary>
    private void FrameFailed(Exception error)
    {
        if (_frameFailureEpoch == _kernel.WorldEpoch) return;
        _frameFailureEpoch = _kernel.WorldEpoch;
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FrameFailed,
            "Map frame failed: " + RuntimeDiagnostics.Describe(error),
            RuntimeLogValue.Quote("exception", error.GetType().Name));
    }

    /// <summary>Every frame: the room-refusal exit. A failure is recorded once per world and the next frame is asked again.</summary>
    internal void Frame()
    {
        if (_disposed) return;
        var timing = ForgeCore.GenerationAttempt.Timing;
        var timedFrame = timing.Active && timing.FactoryDone && !timing.Entered
            && _timedPostFactoryFrameGeneration != timing.Generation;
        if (timedFrame)
        {
            _timedPostFactoryFrameGeneration = timing.Generation;
            ForgeCore.GenerationAttempt.MarkBoundary(timing.Generation, "map-first-post-factory-frame-started",
                "MapPluginSession.Frame");
        }
        var frameCompleted = false;
        try
        {
            // A room refusal made after the factory finished has no LG_Factory.Update left to leave through.
            RoomGenerationFailure.LeaveAfterFactory();
            frameCompleted = true;
        }
        catch (Exception error) { FrameFailed(error); }
        finally
        {
            if (timedFrame)
                ForgeCore.GenerationAttempt.MarkBoundary(timing.Generation,
                    frameCompleted ? "map-first-post-factory-frame-returned" : "map-first-post-factory-frame-failed",
                    "MapPluginSession.Frame");
        }
    }

    public void Dispose()
    {
        DomainSession.CheckThread(_threadId, "Map");
        if (_disposed) return;
        ReportPendingLevelTuning(_kernel.WorldEpoch, ending: true);
        GlobalWaveApplier.Restore();
        // Unregister first: Runtime rejects dispatch/lifecycle re-entry or in-use providers
        // before native detours or session flags can be changed.
        var errors = new List<Exception>();
        // Cancel write-capable startup before a later cleanup can throw and leave the callback live.
        var contentStartup = ContentStartupRegistration; ContentStartupRegistration = null;
        if (contentStartup != null) DomainSession.Cleanup(contentStartup.Dispose, errors);
        var work = _work; _work = null;
        if (work != null) DomainSession.Cleanup(work.Dispose, errors);
        var tickWork = _tickWork; _tickWork = null;
        if (tickWork != null) DomainSession.Cleanup(tickWork.Dispose, errors);
        DomainSession.Cleanup(MapCamera.Detach, errors);
        var arrivals = _arrivals; _arrivals = null;
        if (arrivals != null) DomainSession.Cleanup(arrivals.Dispose, errors);
        var endpointLifecycle = _endpointLifecycle; _endpointLifecycle = null;
        if (endpointLifecycle != null) DomainSession.Cleanup(endpointLifecycle.Dispose, errors);
        DomainSession.Cleanup(ReleaseGenerationBoundary, errors);
        DomainSession.Cleanup(CompiledMapGenerator.Stop, errors);
        if (GlobalLight is { } globalLight) { GlobalLight = null; DomainSession.Cleanup(globalLight.Dispose, errors); }
        DetachHalves(errors);
        // The clock is given back before the registration it hangs on, so no callback of this session's world is
        // left to run against a disposed provider. The light transitions this session scheduled are frames of this
        // session's world: a clock that somehow still ran for one frame after teardown must find nothing to write
        // into, and the table's own announcement must not reach a wake of the next session's clock.
        if (_clock is { } clock) DomainSession.Cleanup(clock.Dispose, errors);
        if (_mapContent is { } mapContent) DomainSession.Cleanup(mapContent.Dispose, errors);
        _mapContent = null;
        if (_labelRecording is { } labelRecording) { _labelRecording = null; DomainSession.Cleanup(labelRecording.Dispose, errors); }
        _clock = null;
        _textLanguageDefaults.Clear();
        LightColorFades.WorkScheduled = null;
        MapLabels.Clear();
        LightColorFades.Clear();
        FogRepellerObservation.Clear();
        ExtractionObservation.Clear();
        // The log goes with the registration it writes through, so nothing of this session writes after it.
        if (MapObjects is { IsRegistered: true } module) MapLog.Detach(module.Registration);
        MapObjects?.Dispose();
        _faulted = true;
        _disposed = true;
        if (errors.Count != 0) throw new AggregateException("Map shutdown cleanup failed.", errors);
    }

}

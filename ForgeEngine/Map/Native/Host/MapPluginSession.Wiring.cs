using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using LevelGeneration;

namespace ForgeMap.Native;

internal sealed partial class MapPluginSession
{
    /// <summary>The one Map provider definition: the rows, the support lines and the shape table
    /// <see cref="ModuleDefinition"/> declares — the same registration the release export builds — completed
    /// with the bodies and the runtime surface of every half. This session supplies the bodies because it is the
    /// only place that knows the map objects, the environment and the level together, and because the runtime
    /// accepts exactly one provider of one identity namespace. Each entry reads the half that is current when the
    /// kernel asks, so the definition can be built before the halves attach to the registration it is declared on;
    /// nothing is dispatched before the host starts the runtime.
    ///
    /// Every row's declaration — its capability, its binding, its registration row and its port shape — is the
    /// game-independent contract's, and only the handler bodies are native, so the declaration and the
    /// implementation cannot drift into two descriptions of one binding.</summary>
    private RuntimeModule Definition(MapHookAvailability hooks)
    {
        var actions = new TerminalObjectActions(_kernel, () => !Down, TerminalFor, _report,
            () => TerminalLedger);
        var objectives = ObjectiveActionHandler.For(() => !Down, _report);
        // The progression action writes the objective machine through the level-event
        // executor. It holds no state a world owns, so the session only hands it the readiness gate and the log.
        var objectiveEvents = ObjectiveEventActions.For(() => !Down, _report);
        // The three door execute rows' own half: it resolves a recipient through the same address grammar the
        // observation half records doors by, so a door a plan names is the door that was observed.
        var doorActions = new DoorActionCommands(_kernel, () => !Down, DoorActionCommands.ResolveByAddress, _report);
        var doors = new DoorTerminalActions(doorActions, _report);
        // The foam row's door half: the same recipient resolution the door actions use, so a door a plan foams
        // is the door that layer would open, and the manager's own door-glue spawn entry behind it.
        var doorFoam = new DoorFoamActions(doorActions, () => !Down);
        // The one map-object state row: the interaction switch writes a terminal, security door or authored
        // interaction point through that object's own native state entry and reads the same state back.
        var mapState = MapState ??= new MapStateActions(_kernel, () => !Down, TerminalObjectActions.ResolveByAddress,
            reference => reference == null ? null : DoorFor(reference) as LG_SecurityDoor,
            reference => reference == null || !reference.Id.StartsWith(EntityKinds.MapObject + ":" + InteractionPointAddress.Category + "/", StringComparison.Ordinal)
                ? null : InteractionPoints?.InspectInteraction(reference.Id[(EntityKinds.MapObject.Length + 1)..], reference.WorldEpoch),
            reference => reference == null ? null
                : PlacedModelSession.LabelReceiver(reference)?.gameObject ?? InteractionPoints?.VisibilityRoot(reference), _report);
        // The interaction-prompt half owns the per-object presentation rules the two interaction-prompt patches
        // read; the terminal-content half writes a terminal's own command table and log files.
        var interactionText = new InteractionTextActions(_kernel, () => !Down, _report, () => InteractionPoints);
        var terminalContent = new TerminalContentActions(_kernel, () => !Down, _report, () => TerminalLedger);
        var environment = EnvironmentActions.For(() => !Down, _report, () => ZoneLightColors,
            () => MapObjects?.Registration);
        // The dimension's outside environment: the level's own per-dimension record, written on each addressed
        // client. It resolves the record late, so a dimension torn down under the request is refused.
        var dimensionEnvironment = DimensionEnvironmentActions.For(_report);
        var sessionActions = new SessionActions(() => !Down, _report,
            reference => TryOptionalPlayerService(static (s, value) => s.LifeOf(value), reference,
                "checkpoint-save-player-life", out RuntimePlayerLife? life) ? life : null);
        // The EMP row: dispatched by device category — the zone lights are written by this package, the player and
        // gear side goes through the device switch service Runtime exposes.
        var devices = Devices ??= new DeviceActions(_kernel, () => !Down, DeviceActions.Board, _report);
        // A refused light body leaves the lights to the native flow: the zone lights are not held at all.
        devices.LightsAvailable = hooks.Offers(MapHookFeatures.ZoneLightHoldFeature);
        // The power switch: the per-zone expedition light mode the environment state manager replicates, over the
        // same zone table the lighting rows resolve a zone through.
        var power = new PowerStateActions(() => !Down, _report);
        // The fog repeller switch: the two native entries a placed `FogRepeller_Sphere` exposes for its own state
        // machine, over the table its own state-change readback fills.
        var fogRepellers = new FogRepellerActions(() => !Down, _report);
        // The item spawn: the game's own item spawn entry, with the level's course nodes as the placement check
        // and this world's epoch as what invalidates the container it parents loose items under.
        var spacePoints = new SpacePointActions(_kernel, () => !Down);
        var itemSpawn = new ItemSpawnActions(() => !Down, () => _kernel.WorldEpoch, _report);
        var modelText = new ModelTextActions(_players);
        var itemRemove = new ItemRemoveActions(() => !Down, _report);
        var generatorReset = new GeneratorResetActions(() => !Down, _report);
        var textStyle = new TextStyleActions(_players);
        // The addressed-object selector's evaluator: the game-independent contract's own table, built from the
        // module's address-to-entity read. The module is the one half that owns the address grammar, the category
        // readers and the identity, so this adds no second resolution — it reads the half late, exactly as every
        // other entry of the table below reads its own half.
        var objectSelector = MapObjectSelectorContract.Evaluators(
            new MapObjectSelectorContract.AddressReader(address => MapObjectHalf().AddressReference(address)));
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            [TerminalObjectContract.CommandHandlerName] = actions.HandleCommand,
            [TerminalObjectContract.OutputHandlerName] = actions.HandleOutput,
            [ObjectiveActionContract.StateHandlerName] = objectives.HandleState,
            [ObjectiveActionContract.PhaseHandlerName] = objectives.HandlePhase,
            [ObjectiveRoundContract.ReactorVerifyHandler] = objectives.HandleReactorVerify,
            [ObjectiveRoundContract.TimedRoundTimeHandler] = objectives.HandleTimedRoundTime,
            [ObjectiveActionContract.ExtractionHandlerName] = objectives.HandleExtraction,
            [ObjectiveActionContract.ExtractionMoveHandlerName] = context => ExtractionMoves?.Handle(context) ?? Outcome.Rejected(ResultCode.ObjectiveUnavailable),
            [ObjectivePresentationContract.MainHandler] = context => ObjectiveText?.Main(context) ?? Outcome.Rejected(ResultCode.ObjectiveLevelUnavailable),
            [ObjectivePresentationContract.ListHandler] = context => ObjectiveText?.List(context) ?? Outcome.Rejected(ResultCode.ObjectiveLevelUnavailable),
            [ObjectiveEventContract.ProgressHandlerName] = objectiveEvents.HandleProgress,
            [DoorTerminalActionContract.LockHandlerName] = doors.HandleLock,
            // The door alarm row reads the session's swap record late: the definition is built before the half exists.
            [DoorTerminalActionContract.AlarmHandlerName] = context => doors.HandleAlarm(context, DoorAlarms),
            // The two door rows and the map-object state row: the contract's own handler names, so the
            // declaration and its body are one fact.
            [DoorActionContract.OpenHandlerName] = doorActions.HandleOpen,
            [DoorActionContract.DamageHandlerName] = doorActions.HandleDamage,
            // The one combat row this provider binds for a door; the enemy package answers the same capability
            // for its own recipient kind.
            [FoamingContract.HandlerName] = doorFoam.Foaming,
            [MapStateContract.InteractionHandlerName] = mapState.HandleInteraction,
            [MapStateContract.VisibilityHandlerName] = mapState.HandleVisibility,
            [EnvironmentContract.LightingHandler] = environment.HandleLighting,
            [EnvironmentContract.LightColorHandler] = environment.HandleLightColor,
            [EnvironmentContract.FogHandler] = environment.HandleFog,
            [EnvironmentContract.FogCycleHandler] = environment.HandleFogCycle,
            [EnvironmentContract.NavMarkerHandler] = context => MapLabels.HudAudience(context, _players) ?? environment.HandleNavMarker(context),
            [WorldLabelContract.Handler] = context => AcknowledgePrivate(context,
                !Down ? MapLabels.WorldLabel(context, _players) : Outcome.Rejected(ResultCode.AuthorityOrPhase)),
            [ModelTextContract.HandlerName] = context => AcknowledgePrivate(context, modelText.Handle(context)),
            [TextStyleContract.HandlerName] = context => AcknowledgePrivate(context, textStyle.Handle(context)),
            [PlacedModelTransformContract.PositionHandler] = context => PlacedModels?.Transform(context, PlacedModelTransformKind.Position)
                ?? Outcome.Rejected(ResultCode.ModelTransformTargetMissing),
            [PlacedModelTransformContract.RotationHandler] = context => PlacedModels?.Transform(context, PlacedModelTransformKind.Rotation)
                ?? Outcome.Rejected(ResultCode.ModelTransformTargetMissing),
            [PlacedModelTransformContract.ScaleHandler] = context => PlacedModels?.Transform(context, PlacedModelTransformKind.Scale)
                ?? Outcome.Rejected(ResultCode.ModelTransformTargetMissing),
            [EnvironmentContract.AnimationHandler] = environment.HandleAnimation,
            [EnvironmentContract.PlayerVoiceHandler] = context => environment.PlayerVoice(context, _players),
            // The dimension's outside environment: the card's own dimension address, written straight onto the
            // level's dimension record on this client.
            [DimensionEnvironmentContract.HandlerName] = dimensionEnvironment.Handle,
            [EnvironmentContract.DeviceDisableHandler] = devices.Handle,
            // The four scan/wave/alarm rows are static facades: each turns its request into the native entry point
            // its capability names, and the rows that start something mint their handle through the attachment this
            // session took in Start — the wave row one handle for the wave, the alarm row one per recipient.
            [AlarmWaveContract.ScanStateHandler] = AlarmWaveActions.ExecuteScanState,
            [AlarmWaveContract.WaveStartHandler] = AlarmWaveActions.ExecuteStartWave,
            [AlarmWaveContract.WaveStopHandler] = AlarmWaveActions.ExecuteStopWave,
            [AlarmWaveContract.AlarmHandler] = AlarmActions.Handle,
            // The power switch and the item spawn: this session's own bodies, keyed by the names their contracts
            // declare.
            [PowerStateContract.HandlerName] = power.Handle,
            [EnemySpawnContract.Handler] = spacePoints.Spawn,
            [ItemSpawnContract.HandlerName] = itemSpawn.Handle,
            [ItemRemoveContract.HandlerName] = itemRemove.Handle,
            [GeneratorResetContract.HandlerName] = generatorReset.Handle,
            [FogRepellerContract.HandlerName] = fogRepellers.Handle,
            // The three level-event actions, through the same static facade: each builds the
            // `WardenObjectiveEventData` the engine's own event manager executes.
            [LevelEventContract.ObjectiveTimerHandlerName] = LevelEventActions.Timer,
            [LevelEventContract.DimensionHandlerName] = LevelEventActions.Dimension,
            [LevelEventContract.ExpeditionEndHandlerName] = LevelEventActions.ExpeditionEnd,
            // The world-event condition action, through the same kind of static facade.
            [WorldEventContract.WorldEventConditionHandlerName] = WorldEventFacts.Condition,
            // The two folded EOS rows: the interaction prompt that replaces a door's text, and the terminal's own
            // content. Both are this package's body, keyed by the contract name.
            [InteractionTextContract.HandlerName] = interactionText.Handle,
            [TerminalContentContract.HandlerName] = terminalContent.Handle,
            [SessionActionContract.CheckpointSaveHandler] = sessionActions.CheckpointSave,
            // fail-rule: the session's own ledger, read late because the definition is composed before the half
            // exists. A session that never built it refuses by name instead of writing a rule nothing answers.
            [FailRuleContract.HandlerName] = context => FailRuleHalf().Handle(context),
            // The success page: the half holds the page's originals of this world, so the entry reads the half
            // that is current when the kernel asks.
            [SuccessScreenContract.HandlerName] = context => SuccessScreenHalf().Handle(context)
        };
        var evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [SpacePointContract.ContainsHandler] = spacePoints.Contains,
            [SpacePointContract.ForwardHandler] = spacePoints.Forward,
            // The zone row's evaluator is the game-independent contract's, built from two reads: the zone the
            // anchor stands in, which is the kernel's own zone read answered by the anchor's provider, and the
            // zone the level's own coordinates name, which only this half can make.
            [ZoneSelectorContract.HandlerName] =
                ZoneSelectorContract.Evaluators(new ZoneSelectorContract.ZoneReaders(ZoneOfAnchor, ZoneOfCoordinates))[ZoneSelectorContract.HandlerName],
            // The addressed-object row's evaluator is the game-independent contract's, built from the module's own
            // address-to-entity read: the same category readers, native lookups and identity the mount matcher
            // judges a plan's mount by, so an address means one object here and there.
            [MapObjectSelectorContract.HandlerName] = objectSelector[MapObjectSelectorContract.HandlerName],
            [MapObjectSelectorContract.GroupHandlerName] = (context, outputs) => outputs.Entities(0, ObjectGroup(context)),
            [DoorQueryContract.HandlerName] =
                DoorQueryContract.Evaluator(new DoorQueryContract.DoorReaders(DoorStateSample)),
            [EnvironmentContract.EnvironmentStateHandler] = EnvironmentQuery.Evaluate,
            [EnvironmentContract.FogAtHandler] = EnvironmentQuery.EvaluateFogAt,
            // The value rows answer through the module that holds the tables the facts fill, so a read of a
            // scan or a generator is a read of the same instance the fact named and not a second lookup of
            // it. Each half is read late, for the same reason every other entry here is.
            [LevelObjectContract.ScanStateHandler] = (context, outputs) => LevelObjects().ReadScanState(context, outputs),
            [GeneratorContract.GeneratorStateHandler] = (context, outputs) => MapObjectHalf().ReadGeneratorState(context, outputs),
            // The two encounter query rows: whether the alarm a handle names is still running, and whether the
            // wave a handle names is still in the event manager's own table.
            [EncounterStateContract.AlarmStateHandler] = EncounterStateActions.AlarmState,
            [EncounterStateContract.WaveStateHandler] = EncounterStateActions.WaveState,
            // The `v-obj` row: one objective layer's live state, read through the game-bound reader.
            [LevelObjectiveValueContract.HandlerName] = LevelObjectiveValueContract.Evaluator(
                new LevelObjectiveValueContract.LayerReader(LevelObjectiveValueReader.Read, LevelObjectiveValueReader.ReadTerminal,
                    (layer, world) => LevelObjectiveValueReader.ReadConnectedTerminal(_kernel, layer, world))),
            // query-readers: the condition read of the world-event family, built from the same replicated state
            // the condition action writes through.
            [WorldEventContract.WorldEventConditionQueryHandlerName] =
                WorldEventContract.ConditionQueryEvaluator(
                    new WorldEventContract.ConditionReader(WorldEventFacts.ConditionSolved)),
            // query-readers: the five reads whose native tables this session already owns. Each is the same half
            // the write row beside it answers through, so a read and a write of one object cannot resolve it
            // twice.
            [TerminalContentContract.QueryHandlerName] =
                TerminalContentContract.QueryEvaluator(
                    new TerminalContentContract.TerminalContentReader(terminalContent.ReadContent)),
            [MapStateContract.QueryHandlerName] =
                MapStateContract.QueryEvaluator(new MapStateContract.InteractionReader(mapState.ReadInteraction)),
            [FogRepellerContract.QueryHandlerName] =
                FogRepellerContract.QueryEvaluator(new FogRepellerContract.FogRepellerReader(FogRepellerActions.ReadEnabled)),
            // The extraction read is the mirror of the write the handler above confirms, so it is the handler's
            // own reader rather than a second table.
            [ObjectiveActionContract.ExtractionQueryHandlerName] =
                ObjectiveActionContract.ExtractionQueryEvaluator(
                    new ObjectiveActionContract.ExtractionReader(objectives.ReadExtraction)),
            // The trigger-zone members read answers from the judging half's own table, read late like every other
            // half here: a session whose zone half was never built refuses by name.
            [TriggerZoneContract.MembersHandlerName] = TriggerZoneContract.MembersEvaluator(zoneId => TriggerZones?.Members(zoneId)),
            [EntityDimensionContract.HandlerName] =
                EntityDimensionContract.Evaluator(
                    new EntityDimensionContract.DimensionReader((reference, instance, epoch) => DimensionQuery.ReadDimension(_players, reference, instance, epoch))),
            [ItemTypeContract.HandlerName] = ItemTypeContract.Evaluator(new ItemTypeContract.ItemTypeReader(ItemTypeQuery.Read))
        };
        // The rows, the support lines and the shape table are the one game-independent registration both this
        // session and the release export build; only the bodies above and the native tables below are this half's.
        var module = ModuleDefinition.Create(handlers, evaluators) with
        {
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                // A weak door is a `gtfo.map_object` door like any other, so the namespace answers for it from the
                // one table that recorded it, and the kernel refuses a reference of a world this session no
                // longer holds.
                [EntityKinds.MapObject] = reference => IsCurrentMapObject(reference) || VanillaWorldObjects.IsCurrent(reference),
                // The one level-object namespace: the scans, containers, level items and fog repellers this
                // provider publishes and switches. A registration that publishes entities of a kind and does not
                // resolve them refuses every one of them at the kernel with `entity-resolver`, so the namespace
                // is answered by the module that owns its ids. The half is read late, because this definition is
                // composed before it exists: a session with no level-object half resolves nothing rather than
                // accepting a reference no table stands behind.
                [EntityKinds.LevelObject] = reference => Levels is { } levels && levels.IsCurrent(reference),
                // The level zone namespace: this provider owns the `zone` resource kind and the zone table behind
                // it, so a `gtfo.zone:<d>:<l>:<z>` reference — the zone selector's answer, a zone address's answer,
                // an enemy's zone — is answered from the same table the resource kind reads. Without it the kernel
                // refuses every zone an action receives (`entity-resolver`). The kernel checks the world epoch.
                [EntityKinds.Zone] = reference => reference.LifeEpoch == 0 && ZoneCoordinatesOf(reference.Id) is { } coordinates
                    && ZoneTable()?.ZoneAt(coordinates) != null
            },
            // A level zone is a place, not an object with a life of its own: it is the same zone for as long as the
            // world lasts and every held zone reference ends with the world (the kernel clears its end set at
            // `BeginWorld`). Nothing inside a world ends a zone, so the kernel does not ask the resolver above about
            // held zones on every tick. Map objects and level objects are not declared: their lives end through
            // many sources (doors and terminals with the level, placed models by command, items by despawn and
            // pickup, scans and containers by state), not one reported path.
            EntityEndReports = new[] { EntityKinds.Zone },
            EntityZones = new Dictionary<string, Func<EntityReference, EntityReference?>>(StringComparer.Ordinal)
            {
                // A level zone is its own placement. The kernel checks its epoch and the live zone resolver
                // above before calling this reader, just as it does for enemy and player placement readers.
                [EntityKinds.Zone] = reference => reference,
                // A map object is placed by the zone coordinates its address spells.
                [EntityKinds.MapObject] = ZoneOfMapObject
            },
            ResourceProviders = new Dictionary<string, RuntimeResourceProvider>(StringComparer.Ordinal)
            {
                ["map"] = RuntimeResourceProvider.Of(
                    () => LevelIdentity.Read() is { } level
                        ? new[] { new ResourceRef("map", level.ToString()) }
                        : Array.Empty<ResourceRef>(),
                    id => MapLevelReference.TryParse(id) is { } requested
                        && LevelIdentity.Read() == requested
                            ? new ResourceRef("map", id) : null),
                [RuntimeZones.ResourceKind] = RuntimeResourceProvider.Of(ZoneResources, ResolveZoneResource),
                // The kinds the native world owns are read late, through the half that holds their tables: a
                // map-object half that was never created is refused by name instead of answering an empty world,
                // and the objective machine's three slots are the level's own answer.
                [GeneratorContract.GeneratorResourceKind] = RuntimeResourceProvider.Of(
                    () => MapObjectHalf().EnumerateGenerators(), id => MapObjectHalf().ResolveGenerator(id)),
                [LevelObjectiveValueContract.ObjectiveResourceKind] =
                    RuntimeResourceProvider.Of(ObjectiveLayerResources, ResolveObjectiveLayerResource),
                // The alarm/scan and wave kinds: the native tables the five execute rows act on, taken from the
                // same attachment the handlers read, so a plan naming one resolves the instance it will start.
                [AlarmWaveContract.ChainedPuzzleKind] = RuntimeResourceProvider.Of(
                    ChainedPuzzleLookup.Enumerate, ChainedPuzzleLookup.Resolve),
                [AlarmWaveContract.WaveKind] = RuntimeResourceProvider.Of(
                    AlarmWaveActions.EnumerateWaves, AlarmWaveActions.ResolveWave),
                // The `item` kind: the item data blocks this install loaded. Only the game-bound half can read
                // them, and the spawn row names one of them as the thing it puts into the world.
                [ItemSpawnContract.ItemResourceKind] = RuntimeResourceProvider.Of(
                    ItemSpawnActions.EnumerateItems, ItemSpawnActions.ResolveItem),
                // The `text` kind: the original Text data blocks this install loaded, addressed by each block's
                // own `persistentID`. The block is the game's own per-language carrier, so the reference a plan
                // compiles is the whole of what Forge holds; the text itself is read where it is shown.
                [TextResourceContract.ResourceKind] = TextResourceContract.Provider(
                    TextResourceTable.Enumerate, TextResourceTable.Resolve)
            },
            // The routing every presentation row of this provider is addressed by. The kernel hands the step's
            // own recipient references to this one function; a player is converted by the Player package's own
            // session table, so a Map row and a Player row are routed by the same conversion and no second list
            // of sessions exists anywhere in this package.
            PresentationSessions = new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>(StringComparer.Ordinal)
            {
                [ModuleDefinition.ProviderId] = recipients => MapPresentationSessions.SessionsOf(_players, recipients)
            }
        };
        return module.Without(hooks.WithdrawnBindings);
    }

    /// <summary>Every placed trigger zone, placed model and interaction point of this level in the selector's group. An
    /// authored member that was not placed already wrote its own placement refusal, so it is not repeated here.</summary>
    private EntityReference[] ObjectGroup(EvaluationContext context)
    {
        var group = context.Parameters.TryGetProperty("group", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()! : throw new RuntimeContractException("missing-field", "group");
        var members = new List<EntityReference>();
        TriggerZones?.GroupMembers(group, members);
        PlacedModels?.GroupMembers(group, members);
        InteractionPoints?.GroupMembers(group, members);
        return members.Count > 0 ? members.ToArray()
            : throw new RuntimeContractException(MapObjectSelectorContract.GroupMissingCode, "This level has no placed object in group " + group + ".");
    }

    private CommandResult AcknowledgePrivate(CommandContext context, CommandResult result)
    {
        _recipientPresentation?.Acknowledge(context, result);
        return result;
    }


}

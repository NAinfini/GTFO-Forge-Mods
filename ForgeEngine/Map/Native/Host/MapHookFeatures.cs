using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>One Map feature and the native targets it stands on. A refused target withdraws exactly the features
/// that name it; every other feature, and the package itself, keeps working. <see cref="Withdrawal"/> names what the
/// package stops offering, for the one `map.capability-unavailable` record, and <see cref="Bindings"/> are the
/// bindings the registration is replaced without, so a plan that needs one is refused as binding-unavailable.</summary>
internal sealed record MapHookFeature(string Name, Type[] Hooks, string Withdrawal) : HookFeature(Name, Hooks)
{
    /// <summary>The bindings only this feature's hooks can publish for.</summary>
    internal string[] Bindings { get; init; } = Array.Empty<string>();
}

/// <summary>The Fields, Lifecycle and Content features of the declared hooks.</summary>
internal static class MapHookFeatures
{
    internal const string ElevatorCargoFieldsFeature = "elevator-cargo-fields";
    internal const string ItemSpawnFieldFeature = "item-spawn-field";
    internal const string ElevatorLandingObservationFeature = "elevator-landing-observation";
    internal const string ElevatorCageEndAlignmentFeature = "elevator-cage-end-alignment";
    internal const string RundownMenuFeature = "rundown-menu";
    internal const string RundownProgressionFeature = "rundown-progression";
    internal const string LevelNamesPerLanguageFeature = "level-names-per-language";
    /// <summary>The environment table's light-hold feature (<see cref="EnvironmentHookFeatures"/>).</summary>
    internal const string ZoneLightHoldFeature = "zone-light-hold";
    internal const string LevelPlayersFeature = "level-players";
    internal const string ExpeditionStartedFeature = "level-event-expedition-started";
    internal const string HsuSampledFeature = "level-event-hsu-sampled";
    internal const string CheckpointRestoredFeature = "level-event-checkpoint-restored";
    internal const string ZoneEnteredFeature = "level-event-zone-entered";
    internal const string PortalWarpedFeature = "level-event-portal-warped";
    internal const string ObjectiveChangedFeature = "level-event-objective-changed";
    internal const string ObjectiveRoundFactsFeature = "objective-round-facts";
    internal const string ScanProgressFeature = "level-object-scan-progress";
    internal const string LevelItemInstancesFeature = "level-object-item-instances";
    internal const string ExtractionWaveNodeFeature = "extraction-wave-node";
    internal const string SpawnRuleItemsFeature = "spawn-rule-items";
    internal const string SpawnRuleEnemiesFeature = "spawn-rule-enemies";
    internal const string EnemyGroupPoolFeature = "enemy-group-pool";
    internal const string RoomResourcesFeature = "room-resources";
    internal const string RoomRecipesFeature = "room-recipes";
    internal const string ModelBuildWindowFeature = "model-build-window";
    internal const string DoorBuildFieldsFeature = "door-build-fields";
    internal const string CompiledZoneExpansionFeature = "compiled-zone-expansion";

    internal static readonly IReadOnlyList<MapHookFeature> All = Array.AsReadOnly(new[]
    {
        new MapHookFeature(ElevatorCargoFieldsFeature,
            new[]
            {
                typeof(ElevatorCargoFields), typeof(ElevatorCargoLandingFields),
                typeof(ElevatorCargoMovementFields), typeof(ElevatorCargoResetFields), typeof(ElevatorCargoCleanupFields)
            },
            "authored elevator cargo rows are no longer applied (each is refused by name)"),
        new MapHookFeature(ItemSpawnFieldFeature, new[] { typeof(ItemSpawnDistribution) },
            "authored level item-spawn rows are no longer applied (each is refused by name)"),
        new MapHookFeature(ElevatorLandingObservationFeature,
            new[]
            {
                typeof(ElevatorRideStartObserved), typeof(ElevatorRideEndObserved), typeof(PlayerRecoveredFromFall),
                typeof(ElevatorPlayerExited), typeof(ElevatorPlayerSyncExited)
            },
            "the elevator release and fall-recovery observations are no longer recorded"),
        new MapHookFeature(ElevatorCageEndAlignmentFeature,
            new[] { typeof(ElevatorRideEndObserved), typeof(ElevatorCageEndAlignment), typeof(ElevatorPlayerExited) },
            "the authored endpoint is no longer applied before native End interpolation or committed before player release, and the seat exit releases players at eye height"),
        new MapHookFeature("pickup-marker-observation", new[] { typeof(PickupMarkerObservation) },
            "failed native pickup marker requests can no longer be attributed to their distribution request"),
        // The menu callbacks are one surface: a Rundown package shown without its title, slot or player values would
        // be a wrong menu, so the packs are not installed at all when any of them is refused.
        new MapHookFeature(RundownMenuFeature,
            new[]
            {
                typeof(RundownPageSetup), typeof(RundownPageSelectionButton), typeof(RundownPageText), typeof(RundownPagePlaced),
                typeof(RundownAchievementName), typeof(RundownAchievementKey), typeof(RundownExpeditionDataSet)
            },
            "Rundown packages are no longer installed into the main menu"),
        new MapHookFeature(RundownProgressionFeature, new[] { typeof(RundownPageProgressionReceived), typeof(RundownPagePlaced), typeof(RundownPageProgressionUpdate) },
            "a Rundown progression that arrives before the page and native vanity data are ready is no longer replayed"),
        new MapHookFeature(LevelNamesPerLanguageFeature, new[] { typeof(LevelNameChecksum) },
            "level names show their primary language on every machine instead of each player's game language"),
        new MapHookFeature(ExpeditionStartedFeature, new[] { typeof(ExpeditionStartedReadback) },
            "forge.trigger.session.expedition_started is no longer published")
        { Bindings = new[] { LevelEventContract.Binding(Blocks.TriggerSessionExpeditionStarted.Id) } },
        new MapHookFeature(HsuSampledFeature, new[] { typeof(HsuSampledReadback) },
            "forge.trigger.objective.hsu_sampled is no longer published")
        { Bindings = new[] { LevelEventContract.Binding(Blocks.TriggerObjectiveHsuSampled.Id) } },
        new MapHookFeature(CheckpointRestoredFeature, new[] { typeof(CheckpointRestoredReadback) },
            "forge.trigger.session.checkpoint_restored is no longer published")
        { Bindings = new[] { LevelEventContract.Binding(Blocks.TriggerSessionCheckpointRestored.Id) } },
        new MapHookFeature(ZoneEnteredFeature, new[] { typeof(ZoneEnteredReadback) },
            "forge.trigger.map.zone_entered is no longer published")
        { Bindings = new[] { LevelEventContract.Binding(Blocks.TriggerMapZoneEntered.Id) } },
        new MapHookFeature(PortalWarpedFeature, new[] { typeof(PortalWarpedReadback) },
            "forge.trigger.map.portal_warped is no longer published")
        { Bindings = new[] { LevelEventContract.Binding(Blocks.TriggerMapPortalWarped.Id) } },
        new MapHookFeature(ObjectiveChangedFeature, new[] { typeof(ObjectiveChangedReadback) },
            "forge.trigger.objective.state_changed is no longer published")
        { Bindings = new[] { ObjectiveTriggerContract.Binding(Blocks.TriggerObjectiveStateChanged.Id) } },
        new MapHookFeature(ObjectiveRoundFactsFeature, new[] { typeof(UplinkRoundAdvance), typeof(TimedRoundAdvance), typeof(TimedRoundStarted), typeof(TimedRoundReset) },
            "the round facts of uplink and timed-terminal objectives are no longer published")
        {
            Bindings = new[]
            {
                ObjectiveRoundContract.Binding(Blocks.TriggerObjectiveRoundCompleted.Id),
                ObjectiveRoundContract.Binding(Blocks.TriggerObjectiveTimedRoundStarted.Id),
                ObjectiveRoundContract.Binding(Blocks.TriggerObjectiveTimedRoundFailed.Id)
            }
        },
        new MapHookFeature(ScanProgressFeature, new[] { typeof(ScanProgressReadback) }, "scan progress is no longer published")
        { Bindings = new[] { LevelObjectContract.ScanProgressBinding } },
        new MapHookFeature(ExtractionWaveNodeFeature, new[] { typeof(ExtractionWaveNode) },
            "an exit scan moved by a plan starts its enemy wave from the node it was built in, not from where the scan stands"),
        // The spawn side is ForgeCore's Items.Spawned; without the despawn hook the table would hold dead items, so
        // the feature is withdrawn with it.
        new MapHookFeature(LevelItemInstancesFeature,
            new[] { typeof(NetworkItemDespawnReadback) },
            "level items are no longer addressable as level objects"),
        // The two mark and read one distribution spawn; one without the other would record nothing or the wrong pickups.
        new MapHookFeature(SpawnRuleItemsFeature, new[] { typeof(SpawnRuleDistributionCall), typeof(BigPickupSetupHooks) },
            "random item rules can no longer tell which pickups they produced, so behaviours mounted on them never fire"),
        new MapHookFeature(SpawnRuleEnemiesFeature, new[] { typeof(SpawnRuleEnemySpawned) },
            "random enemy rules can no longer tell which enemies they produced, so behaviours mounted on them never fire"),
        new MapHookFeature(EnemyGroupPoolFeature, new[] { typeof(EnemyGroupPoolScope) },
            "the random enemy pool is no longer scoped to the level's Rundown package, so a group of another installed package with the same type and difficulty can be drawn"),
        new MapHookFeature(RoomResourcesFeature, new[] { typeof(RoomRecipeResourceCapture) },
            "authored room and placed-model sources are no longer captured"),
        new MapHookFeature(RoomRecipesFeature, new[]
        {
            typeof(RoomRecipeLookup), typeof(RoomRecipeSetupFloor), typeof(RoomRecipeElevatorSelection),
            typeof(RoomRecipeTransitionPlaced), typeof(RoomRecipePlaced), typeof(UnityGraphBuildHooks),
            typeof(RoomRecipeClusterScan), typeof(RoomRecipeClusterSource),
            typeof(RoomGenerationFailureBoundary), typeof(RoomRecipeConnectionSpawners), typeof(RoomRecipePlugBuild),
            typeof(RoomRecipePlugSpawn), typeof(GateBuildHooks), typeof(RoomRecipePlugStraight),
            typeof(RoomRecipePlugSingleDrop), typeof(RoomRecipePlugDoubleDrop), typeof(WeakLockSetupHooks)
        }, "authored room recipes are refused before Rundown publication"),
        new MapHookFeature(ModelBuildWindowFeature, new[] { typeof(UnityGraphBuildHooks) },
            "authored solid models are refused before Rundown publication"),
        new MapHookFeature(DoorBuildFieldsFeature,
            new[] { typeof(GateBuildHooks), typeof(WeakLockSetupHooks) },
            "authored door fields are refused before Rundown publication"),
        new MapHookFeature(CompiledZoneExpansionFeature, new[] { typeof(CompiledMapGenerator), typeof(RoomGenerationFailureBoundary), typeof(RoomGenerationFailureBuild), typeof(CompiledPlugCap), typeof(CompiledLoopStatus), typeof(GenericFunctionItemsHooks) },
            "compiled level generation is refused before Rundown publication")
    });

    /// <summary>The features a declaration pass took down: those with at least one refused target.</summary>
    internal static IReadOnlyList<MapHookFeature> Unavailable(IReadOnlyDictionary<Type, string> refused)
        => HookFeature.Withdrawn(All, refused);

    internal static string Describe(MapHookFeature feature, IReadOnlyDictionary<Type, string> refused)
        => "feature=" + feature.Name + " withdrawn=" + feature.Withdrawal
            + (feature.Bindings.Length == 0 ? "" : " bindings=" + string.Join(",", feature.Bindings))
            + " refused=" + feature.Refusals(refused);
}

/// <summary>The outcome of one declaration pass: the refused targets with Runtime's reasons, and the features they
/// take down in every feature table of the package.</summary>
internal sealed class MapHookAvailability
{
    internal static readonly MapHookAvailability Complete = new(new Dictionary<Type, string>());

    private readonly HashSet<string> _withdrawn;

    internal MapHookAvailability(IReadOnlyDictionary<Type, string> refused)
    {
        Refused = refused;
        Unavailable = MapHookFeatures.Unavailable(refused);
        UnavailableEnvironment = EnvironmentHookFeatures.Unavailable(refused);
        Objects = refused.Count == 0 ? ObjectHookAvailability.Complete : new ObjectHookAvailability(refused);
        _withdrawn = Unavailable.Select(feature => feature.Name).Concat(UnavailableEnvironment.Select(feature => feature.Name))
            .ToHashSet(StringComparer.Ordinal);
        // The bindings the registration is replaced without: this table's by binding id, the world-object table's
        // by capability id (`ProviderBinding.CapabilityId`), which the replacement matches either way.
        WithdrawnBindings = Unavailable.SelectMany(feature => feature.Bindings)
            .Concat(Objects.WithdrawnCapabilities).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal IReadOnlyDictionary<Type, string> Refused { get; }
    internal IReadOnlyList<MapHookFeature> Unavailable { get; }
    internal IReadOnlyList<MapHookFeature> UnavailableEnvironment { get; }

    /// <summary>The world-object families' own availability: the content kinds their halves are built for.</summary>
    internal ObjectHookAvailability Objects { get; }
    internal IReadOnlyList<string> WithdrawnBindings { get; }

    /// <summary>Whether a feature is offered. One comparison when nothing was refused.</summary>
    internal bool Offers(string feature) => _withdrawn.Count == 0 || !_withdrawn.Contains(feature);

    /// <summary>Whether the authored content a world-object hook supports is offered.</summary>
    internal bool Offers(ObjectHookContent content) => Objects.Offers(content);

    /// <summary>The `map.capability-unavailable` sentence of every withdrawn feature, one per feature.</summary>
    internal IEnumerable<(string Feature, string Text)> Records()
        => Unavailable.Select(feature => (feature.Name, MapHookFeatures.Describe(feature, Refused)))
            .Concat(UnavailableEnvironment.Select(feature => (feature.Name, MapHookFeatures.Describe(feature, Refused))))
            .Concat(Objects.Unavailable.Select(feature => (feature.Name, Objects.Describe(feature))));
}

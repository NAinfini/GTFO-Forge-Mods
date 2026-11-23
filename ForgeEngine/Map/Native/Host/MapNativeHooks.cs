using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeMap.Native;

/// <summary>Every native hook class the Map package owns, handed once to Runtime
/// (`NativeHookRegistration.Declare`), which resolves, admits and physically installs each class on its own
/// evidence and is the only installer of them. Each class there declares exactly one native target and static
/// prefix/postfix/finalizer callbacks; callbacks read the live session or the content state it activates. A refused
/// class takes down only the features that name it
/// (<see cref="MapHookFeatures"/>, <see cref="EnvironmentHookFeatures"/>, <see cref="ObjectNativeHooks"/>); the
/// package itself always starts.
///
/// A native target that several features of this package need is one class that calls their parts in a written
/// order (the factory-done stage, raised by GTFO-API, is <see cref="FactoryDoneHooks"/>); Runtime composes the declarations of one writer
/// on one target into one entry. A target that another Forge package also uses is declared the same way and waits
/// for Runtime's shared entry.
///
/// A game method that more than one package reads is patched once by ForgeCore (<c>GameHooks</c>); Map subscribes to it
/// in <see cref="MapGameHooks"/> and declares no class for it, so such readbacks are in no feature table and no evidence
/// row of this package.
///
/// The zone and light ticks ride the kernel's frame phase (`DomainWorkPhases`), not Harmony. Content and generation
/// callbacks are declared here too; accepting authored rows never installs a second patch. Targets, RVAs and call
/// edges are frozen in evidence/map-hooks.json.</summary>
internal static class MapNativeHooks
{
    private static readonly Type[] Own =
    {
        // ---- Fields: the elevator cargo rows' bodies, the cage alignment at the end of the ride, the item-spawn
        // distribution stage and the landing/recovery observations.
        typeof(ElevatorCargoFields), typeof(ElevatorCargoLandingFields),
        typeof(ElevatorCargoMovementFields), typeof(ElevatorCargoResetFields), typeof(ElevatorCargoCleanupFields),
        typeof(ElevatorCageEndAlignment),
        typeof(ItemSpawnDistribution), typeof(PickupMarkerObservation), typeof(GenericFunctionItemsHooks),
        typeof(ElevatorRideStartObserved), typeof(ElevatorRideEndObserved), typeof(PlayerRecoveredFromFall),
        typeof(ElevatorPlayerExited), typeof(ElevatorPlayerSyncExited),
        // ---- Lifecycle: the Rundown menu callbacks, the level-event rows and the level-object readbacks.
        typeof(RundownPageProgressionReceived), typeof(RundownPagePlaced), typeof(RundownPageProgressionUpdate), typeof(RundownPageSetup),
        typeof(RundownPageSelectionButton), typeof(RundownPageText), typeof(RundownAchievementName),
        typeof(RundownAchievementKey), typeof(RundownExpeditionDataSet), typeof(LevelNameChecksum),
        typeof(ExpeditionStartedReadback), typeof(HsuSampledReadback),
        typeof(ObjectiveChangedReadback), typeof(CheckpointRestoredReadback), typeof(ZoneEnteredReadback),
        typeof(PortalWarpedReadback),
        typeof(ScanProgressReadback), typeof(NetworkItemDespawnReadback), typeof(ExtractionWaveNode),
        typeof(UplinkRoundAdvance), typeof(TimedRoundAdvance), typeof(TimedRoundStarted), typeof(TimedRoundReset),
        typeof(UplinkVerifyGate), typeof(TimedVerifyGate), typeof(TimedSequenceStart), typeof(TimedConfirmationStarted),
        typeof(ReactorVerifyGate),
        typeof(SpawnRuleDistributionCall), typeof(BigPickupSetupHooks), typeof(SpawnRuleEnemySpawned), typeof(EnemyGroupPoolScope),
        // ---- Environment: a later native light decision relinquishes only that light's authored restore receipt
        // (one declaration per light body), and the guard that keeps a repeated exterior close from aborting spawn.
        typeof(LightBaseEnabledReadback), typeof(PointLightEnabledReadback), typeof(PointLightEnabledReasonReadback),
        typeof(SpotLightEnabledReadback), typeof(SpotLightEnabledReasonReadback), typeof(AmbientLightEnabledCommonReadback),
        typeof(EnvironmentStateClock), typeof(ExteriorSceneGuard),
        // ---- Content and generation: the recipe, room-field, model-window and room-transform hooks, including the
        // navigation, gate-build, weak-lock and factory-done entries they share (one class per target).
        typeof(RoomRecipeResourceCapture), typeof(RoomRecipeLookup),
        typeof(RoomRecipeSetupFloor), typeof(RoomRecipeElevatorSelection), typeof(RoomRecipeTransitionPlaced),
        typeof(RoomRecipePlaced), typeof(UnityGraphBuildHooks), typeof(RoomRecipeClusterScan),
        typeof(RoomRecipeClusterSource), typeof(RoomGenerationFailureBoundary), typeof(RoomRecipeConnectionSpawners),
        typeof(RoomRecipePlugBuild), typeof(RoomRecipePlugSpawn), typeof(GateBuildHooks),
        typeof(RoomRecipePlugStraight), typeof(RoomRecipePlugSingleDrop), typeof(RoomRecipePlugDoubleDrop), typeof(WeakLockSetupHooks),
        typeof(CompiledPlugCap), typeof(CompiledLoopStatus),
        typeof(RoomGenerationFailureBuild), typeof(CompiledMapGenerator)
    };

    /// <summary>The hooks Runtime installs: this file's own, then the world-object families
    /// (<see cref="ObjectNativeHooks.Types"/>, the one list of them), each class once.</summary>
    internal static IReadOnlyList<Type> Declared { get; } = Array.AsReadOnly(Own.Concat(ObjectNativeHooks.Types).Distinct().ToArray());

    /// <summary>The classes the declaration pass at plugin start hands over: <see cref="Declared"/> without the groups of
    /// <see cref="MapLazyHooks"/>, which the first authored content that needs them declares.</summary>
    internal static IReadOnlyList<Type> Startup { get; } = Array.AsReadOnly(Declared.Except(MapLazyHooks.All).ToArray());

    /// <summary>Every hook class of the package. The test observation hooks (intel line, sound post) belong to the development assembly
    /// (ForgeMap.Native.Testing), which declares them itself; they are not part of the player package.</summary>
    internal static IReadOnlyList<Type> Types => Declared;

    /// <summary>The declaration pass: every class is attempted so one run names every target that could not be
    /// proven, and the refusals are returned with the features they take down. No Map target is the package's
    /// identity, so a refusal never stops startup.</summary>
    internal static MapHookAvailability Declare()
    {
        var refused = ForgeEngine.GameBindings.NativeHookRegistration.Declare(ModuleDefinition.ProviderId, Startup)

            .ToDictionary(refusal => refusal.Callbacks, refusal => refusal.Reason);
        return refused.Count == 0 ? MapHookAvailability.Complete : new MapHookAvailability(refused);
    }
}

/// <summary>The hooks that stand on a body the game runs every frame (or every scan step, or every light switch) and
/// that only authored content needs: the light-hold observation, the fail-rule verdict and the scan-coverage
/// callbacks. They are members of <see cref="MapNativeHooks.Declared"/> like every other class, but startup leaves
/// them out; the first authored rule, light hold or scan-coverage row declares its group, once, as one set per
/// target, and it stays: under Il2CppInterop taking a hook off again keeps a pass-through detour and leaks its
/// trampoline, so a level without that content never carries the hook and one with it never pays to remove it.
/// A group Runtime refuses is kept refused and the content that needed it is refused by name.</summary>
internal static class MapLazyHooks
{
    internal enum Group { ZoneLights, FailRules, ScanCoverage, ObjectiveRounds }

    private static readonly Type[][] Groups =
    {
        new[]
        {
            typeof(LightBaseEnabledReadback), typeof(PointLightEnabledReadback), typeof(PointLightEnabledReasonReadback),
            typeof(SpotLightEnabledReadback), typeof(SpotLightEnabledReasonReadback), typeof(AmbientLightEnabledCommonReadback)
        },
        // The failure verdict is a subscription to ForgeCore's hook (ExpeditionFailedDecision.Subscribe), made when the group is asked for.
        Array.Empty<Type>(),
        // The factory-done entry and the bioscan check-progress entry (ScanMembershipInjection, beside ScanProgressReadback) stay at
        // startup: a writer's declaration on one target is whole, so a class declared later on a declared target is refused.
        new[]
        {
            typeof(ScanCoverageInstanceSetup), typeof(ScanCoverageInstanceCreated),
            typeof(ScanCoverageNativeBeat), typeof(ScanCoverageGraphicsSetup), typeof(ScanCoverageBioscanSetup), typeof(ScanCoverageClusterSetup),
            typeof(ScanCoverageHudText)
        },
        // Only authored round rows change anything here, and the reactor message stands on a body the game runs every frame.
        new[]
        {
            typeof(UplinkVerifyGate), typeof(TimedVerifyGate), typeof(TimedSequenceStart), typeof(TimedConfirmationStarted),
            typeof(ReactorVerifyGate)
        }
    };

    internal static IReadOnlyList<Type> All { get; } = Array.AsReadOnly(Groups.SelectMany(group => group).ToArray());

    // null: not asked yet; "": on; anything else: Runtime's refusal, kept so a refused group is not declared again.
    private static readonly string?[] States = new string?[Groups.Length];

    /// <summary>Test seam: the offline suites install through their own double.</summary>
    internal static Func<string, IEnumerable<Type>, IReadOnlyList<ForgeEngine.GameBindings.NativeHookRefusal>> Installer { get; set; }
        = ForgeEngine.GameBindings.NativeHookRegistration.Declare;

    /// <summary>Puts one group on if it is not on yet. Null when it is on; otherwise the reason it cannot be.</summary>
    internal static string? Ensure(Group group)
    {
        var state = States[(int)group];
        if (state != null) return state.Length == 0 ? null : state;
        if (group == Group.FailRules) ExpeditionFailedDecision.Subscribe();
        var refused = Groups[(int)group].Length == 0 ? Array.Empty<ForgeEngine.GameBindings.NativeHookRefusal>() : Installer(ModuleDefinition.ProviderId, Groups[(int)group]);
        string reason = refused.Count == 0 ? "" : string.Join("; ", refused.Select(r => r.Callbacks.Name + "=" + r.Reason));
        States[(int)group] = reason;
        if (reason.Length != 0)
            MapLog.Record(ForgeEngine.Framework.RuntimeLogLevel.Error, MapLogCodes.CapabilityUnavailable,
                "group=" + group + " refused=" + reason);
        return reason.Length == 0 ? null : reason;
    }

    /// <summary>Puts every group on (Authoring, at the first content generation, so a reload never installs a hook). Returns
    /// the refusal of each group that could not go on.</summary>
    internal static IReadOnlyList<string> EnsureAll()
        => Enum.GetValues<Group>().Select(group => (group, reason: Ensure(group))).Where(row => row.reason != null)
            .Select(row => row.group + ": " + row.reason).ToArray();

    /// <summary>Offline suites start each case from nothing put on.</summary>
    internal static void ResetForTest() => Array.Clear(States);
}

/// <summary>The level half's read of a player arriving. `PlayerManager.OnPlayerSpawned` is a target Runtime owns as a
/// shared install, so Map does not declare it: it holds a world-bound lease on Runtime's membership entry (after the
/// native body returned) and the callback below only chooses when the level half acts. The first player in a level
/// is this session's signal that the level is there, which is when the authored wave tuning is applied; that step
/// runs once per world inside the session, so a spawn that follows a respawn applies nothing twice.</summary>
internal static class LevelPlayerArrival
{
    internal const string Consumer = "forge.map.level-players";

    internal static ForgeEngine.GameBindings.DomainWorldObservation Subscribe(ForgeEngine.Framework.RuntimeModuleHandle owner,
        ForgeEngine.Framework.IForgeEngine kernel, Action<string> refused)
    {
        var once = new ForgeCore.OnceReporter(refused);
        return new("Map", owner, kernel, message => once.Report("refused", message),
            static epoch => ForgeEngine.GameBindings.GamePlayerMembership.Subscribe(Consumer, epoch,
                ForgeEngine.GameBindings.GamePlayerMembershipEntry.PlayerSpawned,
                ForgeEngine.GameBindings.GameEntryPhase.Returned, static _ => MapHost.Session?.PlayerArrived()));
    }
}

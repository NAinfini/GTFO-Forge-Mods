using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>The authored content a world-object hook supports that is not a published binding: rows the session
/// builds a half for. A refused hook withdraws exactly the content kinds of the features that stand on it.</summary>
[Flags]
internal enum ObjectHookContent
{
    None = 0,
    FixedPlacements = 1 << 0,
    TaskObjects = 1 << 1,
    UplinkLinking = 1 << 2,
    ObjectiveCargo = 1 << 3,
    DimensionLandings = 1 << 4,
    BulkheadControllers = 1 << 6,
    GeneratorPlacement = 1 << 7,
    TaskContainers = 1 << 8,
    Shuttleboxes = 1 << 9,
    DoorLocks = 1 << 10,
    ScanCoverage = 1 << 12,
    ExtractionPoint = 1 << 14
}

/// <summary>One world-object feature and the native targets it stands on. A refused target withdraws exactly the
/// bindings (named by capability id, which is how a provider binding is identified) and the content kinds of the
/// features that need it; every other feature keeps working. A hook that several features need lists in each.</summary>
internal sealed record ObjectHookFeature(string Name, Type[] Hooks, string[] Capabilities) : ForgeEngine.Framework.HookFeature(Name, Hooks)
{
    internal ObjectHookContent Content { get; init; }
}

/// <summary>The outcome of one declaration pass over this lane's hooks: the refused callback classes with Runtime's
/// reasons, and the features they take down.</summary>
internal sealed class ObjectHookAvailability
{
    internal static readonly ObjectHookAvailability Complete = new(new Dictionary<Type, string>());

    internal IReadOnlyDictionary<Type, string> Refused { get; }
    internal IReadOnlyList<ObjectHookFeature> Unavailable { get; }

    internal ObjectHookAvailability(IReadOnlyDictionary<Type, string> refused)
    {
        Refused = refused ?? throw new ArgumentNullException(nameof(refused));
        Unavailable = ForgeEngine.Framework.HookFeature.Withdrawn(ObjectNativeHooks.Features, refused);
    }

    internal bool Offers(ObjectHookContent content) => !Unavailable.Any(feature => (feature.Content & content) != 0);

    /// <summary>Capability ids whose provider bindings the registration must leave out.</summary>
    internal IReadOnlyList<string> WithdrawnCapabilities => Unavailable.SelectMany(feature => feature.Capabilities)
        .Distinct(StringComparer.Ordinal).ToArray();

    internal string Describe(ObjectHookFeature feature) => "feature=" + feature.Name
        + " capabilities=" + (feature.Capabilities.Length == 0 ? "none" : string.Join(",", feature.Capabilities))
        + " content=" + feature.Content
        + " refused=" + feature.Refusals(Refused);
}

/// <summary>The feature table of the world-object, door, terminal, interaction, objective, scan and wave hooks. Every
/// callback class of those families belongs to at least one feature, so a refusal can never leave a half that
/// silently stands on a missing target.</summary>
internal static class ObjectNativeHooks
{
    internal static readonly IReadOnlyList<ObjectHookFeature> Features = Array.AsReadOnly(new[]
    {
        // Doors: one status echo feeds three bindings, so each binding is its own feature over the hooks it reads.
        new ObjectHookFeature("door-state", new[] { typeof(DoorStateReadback) },
            new[] { Blocks.TriggerInteractionDoorState.Id }),
        new ObjectHookFeature("door-lock-state", new[] { typeof(DoorStateReadback) },
            new[] { Blocks.TriggerInteractionLockState.Id }),
        new ObjectHookFeature("door-scan", new[] { typeof(DoorStateReadback) },
            new[] { Blocks.TriggerInteractionDoorScan.Id }),
        new ObjectHookFeature("weak-lock-broken", new[] { typeof(WeakLockBrokenReadback) },
            new[] { Blocks.TriggerInteractionLockBroken.Id }),
        // The attacked and broken phases are one binding; a missing phase would mislabel the door's history.
        new ObjectHookFeature("weak-door-broken", new[] { typeof(WeakDoorAttackedReadback), typeof(WeakDoorBrokenReadback),
            typeof(WeakDoorEnemyDamageScope) },
            new[] { Blocks.TriggerInteractionDoorBroken.Id }),
        new ObjectHookFeature("weak-door-noise", new[] { typeof(WeakDoorExplosionNoiseScope) },
            new[] { Blocks.TriggerInteractionDoorNoise.Id }),
        new ObjectHookFeature("door-locks",
            new[] { typeof(DoorLockKeyDistribution), typeof(DoorLockKeyBuilt), typeof(DoorLockStateReceived),
                typeof(DoorLockKeyCompleted) },
            Array.Empty<string>()) { Content = ObjectHookContent.DoorLocks },

        // Terminals.
        new ObjectHookFeature("terminal-state", new[] { typeof(TerminalStateReadback) },
            new[] { Blocks.TriggerInteractionTerminalSession.Id }),
        new ObjectHookFeature("terminal-result", new[] { typeof(TerminalPasswordResultReadback),
            typeof(TerminalDownloadResultReadback) },
            new[] { Blocks.TriggerInteractionTerminalResult.Id }),
        new ObjectHookFeature("terminal-log-read", new[] { typeof(TerminalLogReadReadback), typeof(TerminalLogReceivedReadback) },
            new[] { Blocks.TriggerInteractionTerminalLogRead.Id }),

        // The terminal prompt the interaction-text rule rewrites; the other prompt owners are not Harmony targets.
        new ObjectHookFeature("terminal-prompt-text", new[] { typeof(TerminalInteractionTextPromptReadback) },
            new[] { Blocks.ActionPresentationInteractionText.Id }),

        // Generators and item slots.
        new ObjectHookFeature("generator-group", new[] { typeof(GeneratorClusterStateReadback) },
            new[] { Blocks.TriggerMapGeneratorCluster.Id }),
        new ObjectHookFeature("item-placed", new[] { typeof(CarryItemInserted) },
            new[] { Blocks.TriggerInteractionItemPlaced.Id }),
        new ObjectHookFeature("item-removed", new[] { typeof(CarryItemRemoved), typeof(PowerGeneratorInteraction) },
            new[] { Blocks.TriggerInteractionItemRemoved.Id }),
        new ObjectHookFeature("carry-item-restrictions", new[] { typeof(CarryItemWielded), typeof(CarryItemUnwielded) }, Array.Empty<string>()),
        new ObjectHookFeature("shuttlebox-slots", new[] { typeof(ShuttleboxBuilt) }, Array.Empty<string>())
            { Content = ObjectHookContent.Shuttleboxes },

        // The level world and the world-event components. The interact and look-at rows both read the vanilla
        // world-object table the factory-done and cleanup hooks fill.
        new ObjectHookFeature("spitter-exploded", new[] { typeof(SpitterExplodedReadback) },
            new[] { Blocks.TriggerMapSpitterExploded.Id }),
        new ObjectHookFeature("zone-lights-changed", new[] { typeof(ZoneLightsChangedReadback) },
            new[] { Blocks.TriggerMapZoneLightsChanged.Id }),
        new ObjectHookFeature("fog-changed", new[] { typeof(FogTransitionStartedReadback) },
            new[] { Blocks.TriggerMapFogChanged.Id }),
        new ObjectHookFeature("world-event-interact",
            new[] { typeof(InteractTriggeredReadback), typeof(InteractResetReadback) },
            new[] { Blocks.TriggerInteractionWorldEventInteract.Id }),
        new ObjectHookFeature("world-event-lookat",
            new[] { typeof(LookatTriggeredReadback), typeof(LookatResetReadback) },
            new[] { Blocks.TriggerInteractionWorldEventLookat.Id }),

        // Objectives, extraction and scans.
        new ObjectHookFeature("scan-started", new[] { typeof(ScanBioscanActivatedReadback), typeof(ScanClusterActivatedReadback) },
            new[] { Blocks.TriggerObjectiveScanStarted.Id }),
        new ObjectHookFeature("scan-coverage",
            new[] { typeof(ScanMembershipInjection), typeof(ScanCoverageInstanceSetup),
                typeof(ScanCoverageInstanceCreated), typeof(ScanCoverageNativeBeat), typeof(ScanCoverageGraphicsSetup), typeof(ScanCoverageBioscanSetup), typeof(ScanCoverageClusterSetup),
                typeof(ScanCoverageHudText) },
            Array.Empty<string>()) { Content = ObjectHookContent.ScanCoverage },
        // Both endpoint kinds are remembered by the same readings, so the extraction query needs all six.
        new ObjectHookFeature("extraction-state",
            new[] { typeof(ExtractionLandingBuilt), typeof(ExtractionExitBuilt), typeof(ExtractionLandingActivated),
                typeof(ExtractionExitActivated), typeof(ExtractionLandingDeactivated), typeof(ExtractionExitDeactivated) },
            new[] { Blocks.QueryMapExtraction.Id }),
        // The authored extraction point: the native exit host starts only when it is the level's own host.
        new ObjectHookFeature("extraction-point", new[] { typeof(ExtractionHostStart), typeof(ExtractionElevatorRegistered),
            typeof(ExtractionCustomExitRegistered), typeof(ExtractionLandingActivated), typeof(ExtractionExitActivated) }, Array.Empty<string>())
            { Content = ObjectHookContent.ExtractionPoint },
        new ObjectHookFeature("objective-panel",
            new[] { typeof(ObjectiveMainTextOverride), typeof(ObjectivePanelTextRefreshed), typeof(ObjectivePanelTextsCleared) },
            new[] { Blocks.ActionPresentationObjectiveMain.Id, Blocks.ActionPresentationObjectiveList.Id }),

        // Authored placements.
        // Placed at the factory-done stage, which GTFO-API raises (FactoryDoneHooks): no patch of Map's to withdraw it.
        new ObjectHookFeature("fixed-placements", Type.EmptyTypes, Array.Empty<string>())
            { Content = ObjectHookContent.FixedPlacements },
        // Task objects are built at their authored points (FixedFunctionPoints): node pins, claim and marker handover.
        new ObjectHookFeature("task-objects", new[] { typeof(PlacementBeforeCulling), typeof(GeneratorPlacementRequests),
                typeof(GeneratorPlacementNodeResult), typeof(GeneratorClusterZoneNode), typeof(GenericFunctionItemsHooks),
                typeof(FixedFunctionPointHandover), typeof(FixedObjectiveRequest), typeof(FixedObjectiveExistingDistribution),
                typeof(DoorLockKeyDistribution), typeof(FixedDoorGeneratorNode) },
            Array.Empty<string>()) { Content = ObjectHookContent.TaskObjects },
        new ObjectHookFeature("uplink-linking", new[] { typeof(AuthoredUplinkLinking) }, Array.Empty<string>())
            { Content = ObjectHookContent.UplinkLinking },
        new ObjectHookFeature("objective-cargo", new[] { typeof(ObjectiveCargoSetup) }, Array.Empty<string>())
            { Content = ObjectHookContent.ObjectiveCargo },
        new ObjectHookFeature("dimension-landings", new[] { typeof(DimensionLandingPointResult) }, Array.Empty<string>())
            { Content = ObjectHookContent.DimensionLandings },
        new ObjectHookFeature("bulkhead-controllers", new[] { typeof(GeneratorPlacementRequests), typeof(GeneratorPlacementNodeResult),
                typeof(GenericFunctionItemsHooks), typeof(FixedFunctionPointHandover) }, Array.Empty<string>())
            { Content = ObjectHookContent.BulkheadControllers },
        new ObjectHookFeature("generator-placement",
            new[] { typeof(GeneratorPlacementRequests), typeof(GeneratorPlacementNodeResult), typeof(GeneratorClusterZoneNode),
                typeof(GenericFunctionItemsHooks), typeof(GeneratorPlacementCore), typeof(FixedFunctionPointHandover),
                typeof(FixedObjectiveRequest), typeof(FixedObjectiveExistingDistribution), typeof(DoorLockKeyDistribution),
                typeof(FixedDoorGeneratorNode) },
            Array.Empty<string>()) { Content = ObjectHookContent.GeneratorPlacement },
        new ObjectHookFeature("task-containers",
            new[] { typeof(TaskKeyContainerSelection), typeof(TaskSmallContainerSelection), typeof(BigPickupSetupHooks) },
            Array.Empty<string>()) { Content = ObjectHookContent.TaskContainers },

        // Waves.
        // A native wave that was never registered cannot be named by a stop or a state query.
        new ObjectHookFeature("wave-handles", new[] { typeof(SurvivalWaveResourceRegistration) },
            new[] { Blocks.ActionMapWaveStop.Id, Blocks.QueryMapWaveState.Id })
    });

    /// <summary>Every callback class of this lane, once, in feature order.</summary>
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(Features.SelectMany(feature => feature.Hooks)
        .Distinct().ToArray());
}

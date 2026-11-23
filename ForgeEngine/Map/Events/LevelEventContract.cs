using System;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The level-lifecycle, objective-transition and dimension rows of the checklist: the elevator landing that
/// starts an expedition, the objective chain step a reactor wave is, the HSU's own "the sample is in" transition, the
/// checkpoint reload, the zone a player walks into, the dimension a portal sends them to — and the two actions that
/// ride the same native entry: the objective's own countdown, and the team's dimension moves.
///
/// One runtime entry carries all of them. `WorldEventManager.ExecuteEvent(WardenObjectiveEventData)` is the
/// game's own level-event executor — the same call every vanilla mount point uses — so an action this file
/// declares builds the event struct with its own type member and hands it over, instead of writing the state the
/// event owns. That is what keeps a client's world consistent: the call runs on the host and the struct's own
/// replicated fields do the rest.
///
/// The event types this file names are `GameData.eWardenObjectiveEventType` members, read from the build
/// (20403457): `AddToTimer` 24, `ResetTimer` 25, `WinOnDeath` 26, `ForceInstantWin` 27, `DimensionFlashTeam` 7,
/// `ClearDimension` 30.
///
/// Every capability row this family observes is the runtime's own: the six `forge.trigger.*` ids below are
/// declared by the trigger contract (`forge.contract.trigger`, `ForgeEngine/Framework/Policy/TriggerContracts.cs`,
/// ruling 148.3), because the catalog row for these transitions carries no port this row cannot be bound to and
/// an id has exactly one owner. This file therefore declares no trigger capability row at all — it names the
/// canonical id, its own binding, its registration row and the fact that travels through them. The three
/// `forge.action.*` capability rows come from the generated block table; this file keeps their bindings, handler
/// shapes and vocabularies.</summary>
internal static class LevelEventContract
{
    /// <summary>The provider this family's rows belong to, spelled once here: the declaration and every binding id
    /// below are built from it, and the provider declaration's own `ProviderId` is the same constant — a focused
    /// case asserts the two agree, so this contract compiles without the declaration beside it.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    // ---- triggers -----------------------------------------------------------------------------------

    /// <summary>The checklist's "level started (elevator landed)": the fact carries the level it stands in as the
    /// reference the `level` attachment matcher already compares.</summary>
    public const string ExpeditionStartedFact = "session.expedition_started";
    /// <summary>The reactor objective's own chain step. A reactor's waves are the objective's event chain, so what
    /// makes a new wave is the chain index the objective machine already reports; there is no author-defined wave
    /// instance to name and publishing one would invent an identity the game does not have.</summary>
    public const string ReactorWaveFact = "objective.reactor_wave";
    /// <summary>The HSU's own activation: the objective item has been taken out of the activator. The mount point
    /// is the HSU objective's `OnLocalPlayerSolvedObjectiveItem`, which is the transition the vanilla
    /// `ActivateHSU_Events` list is attached to.</summary>
    public const string HsuSampledFact = "objective.hsu_sampled";
    /// <summary>The session's checkpoint reload, fired once the recall has finished.</summary>
    public const string CheckpointRestoredFact = "session.checkpoint_restored";
    /// <summary>A player entering a zone. The native player's course-node setter supplies the actual agent and
    /// its old/new Zone, including peer agents on the host; the row reports the zone reference, so
    /// the three-layer address is the same one every other level-scope row uses.</summary>
    public const string ZoneEnteredFact = "map.zone_entered";
    /// <summary>A completed dimension traversal from the portal's Setup-created Type 8 event. The source
    /// includes native serial, source Zone and room instance; unrelated IWarpableObject notifications are not facts.</summary>
    public const string PortalWarpedFact = "map.portal_warped";

    // ---- actions ------------------------------------------------------------------------------------

    // The three actions: the objective countdown (`forge.action.map.objective_timer`: `reset` is its own native
    // event, `ResetTimer`, not the generic objective row's `SetExtraTime`), the team's dimension flash and the
    // dimension clear (`forge.action.player.dimension`: two members of one native event family) and the expedition end
    // (`forge.action.map.expedition_end`: the native events that say how the expedition ends).
    public const string ObjectiveTimerHandlerName = "gtfo.map.objective_timer";
    public const string DimensionHandlerName = "gtfo.map.player_dimension";
    public const string ExpeditionEndHandlerName = "gtfo.map.expedition_end";

    public static string Binding(string capabilityId) => ProviderId + ".binding." + Suffix(capabilityId);

    // ---- permissions --------------------------------------------------------------------------------

    /// <summary>The permission the trigger rows read under: what they publish is the objective machine's and the
    /// level session's own decision, so the permission names the domain rather than an instance.</summary>
    public const string ObjectiveReadPermission = "objective.read";
    /// <summary>The permission the session rows read under — the checkpoint and the expedition start.</summary>
    public const string SessionReadPermission = "session.read";
    /// <summary>The permission the zone and portal rows read under: both are level objects the map owns, so the
    /// permission is the map-object namespace's read permission, spelled as the constant value the map-object
    /// rows declare rather than by reading their contract — this contract declares its rows' permissions on its
    /// own so it compiles without a second family's contract beside it, and a case asserts the two spellings
    /// agree.</summary>
    public const string MapObjectReadPermission = "gtfo.map_object.read";
    /// <summary>The permission the countdown action writes under. It moves the state of an objective this map's
    /// own data blocks declare, so it is the same namespace the objective action rows already write under.</summary>
    public const string ObjectiveWritePermission = "objective.timer";
    /// <summary>The permission the dimension action writes under: it moves the players the same way
    /// `forge.action.player.teleport` does, so it names the same namespace.</summary>
    public const string PlayerWarpPermission = "player.warp";
    /// <summary>The permission the expedition-end action writes under.</summary>
    public const string ExpeditionEndPermission = "expedition.end";

    // ---- vocabularies -------------------------------------------------------------------------------

    /// <summary>`LG_LayerType`'s members as the rows index them, in the enum's own order.</summary>
    public static readonly string[] Layers = { "main", "secondary", "third" };
    /// <summary>The two things the countdown row can do, which are the two native events it can reach.</summary>
    public static readonly string[] TimerOperations = { "add", "reset" };
    /// <summary>The two things the dimension row can do with an index: the native team flash and `clear`. Moving
    /// players into a dimension is `forge.action.player.teleport` with its `dimension` input.</summary>
    public static readonly string[] DimensionModes = { "flash", "clear" };
    /// <summary>The ways the expedition-end row can end it: `instant_win` ends it now, `win_on_death` makes the next
    /// wipe the win the objective's own completion check looks for, and `fail` ends it now as a failure.</summary>
    public static readonly string[] ExpeditionOutcomes = { "instant_win", "win_on_death", "fail" };
    /// <summary>The countdown command: the seconds it adds and the operation it performs. The catalog's own
    /// `objectives` resource input is declared on the row because the framework requires an action's recipient
    /// port, and the handler refuses a plan that supplies one: no provider in this runtime answers a resource
    /// reference the way an action target would have to be resolved, so serving it would be a promise nothing
    /// keeps.</summary>
    public static readonly HandlerShape TimerShape = new HandlerShape()
        .Inputs("objectives", "seconds").Outputs("result").Parameters("operation");
    public static readonly HandlerSlot TimerObjectives = TimerShape.InputSlot("objectives");
    public static readonly HandlerSlot TimerSeconds = TimerShape.InputSlot("seconds");
    public static readonly HandlerSlot TimerOperation = TimerShape.ParameterSlot("operation");
    /// <summary>The dimension command: the mode and the dimension index. Flash uses native landings and requires
    /// the complete current team; clear validates the actor lives and addresses the explicit dimension,
    /// without moving players.</summary>
    public static readonly HandlerShape DimensionShape = new HandlerShape()
        .Inputs("players", "dimension").Outputs("result").Parameters("mode");
    public static readonly HandlerSlot DimensionPlayers = DimensionShape.InputSlot("players");
    public static readonly HandlerSlot DimensionIndex = DimensionShape.InputSlot("dimension");
    public static readonly HandlerSlot DimensionMode = DimensionShape.ParameterSlot("mode");
    /// <summary>The expedition-end command: the outcome. The expedition ends for everyone in it, so `participants`
    /// is optional and only names the result row's target, never who the ending reaches.</summary>
    public static readonly HandlerShape ExpeditionEndShape = new HandlerShape()
        .Inputs("participants").Outputs("result").Parameters("ending");
    public static readonly HandlerSlot ExpeditionParticipants = ExpeditionEndShape.InputSlot("participants");
    public static readonly HandlerSlot ExpeditionEnding = ExpeditionEndShape.ParameterSlot("ending");

    // ---- rows ---------------------------------------------------------------------------------------

    public static string Suffix(string capabilityId)
        => capabilityId.StartsWith("forge.", StringComparison.Ordinal)
            ? capabilityId["forge.".Length..].Replace('.', '_')
            : throw new RuntimeContractException("level-event-capability", capabilityId);

    /// <summary>The level reference an expedition-start fact carries: the same identity string the `level`
    /// attachment matcher compares, read from the live expedition and never invented.</summary>
    public static ResourceRef MapReference(string levelReference) => new("map", levelReference);

    /// <summary>The objective reference a per-layer row carries. An objective has no per-instance native identity
    /// — the machine keeps three fixed layer slots — so the id is the layer's own name, which is what the plan's
    /// structural `layer` parameter already spells. The id's grammar is <see cref="ObjectiveLayerReference"/>'s,
    /// which is also what the objective action rows read it back with when they check that the reference a plan
    /// wired names the layer they write. The three layers' references are made once.</summary>
    public static ResourceRef ObjectiveReference(string layer)
    {
        for (var index = 0; index < Layers.Length; index++)
            if (string.Equals(Layers[index], layer, StringComparison.Ordinal)) return LayerReferences[index];
        return new(ObjectiveLayerReference.Kind, ObjectiveLayerReference.Id(layer));
    }

    private static readonly ResourceRef[] LayerReferences =
        Array.ConvertAll(Layers, layer => new ResourceRef(ObjectiveLayerReference.Kind, ObjectiveLayerReference.Id(layer)));

    /// <summary>The handler name a trigger's binding publishes under, so a hook and the registration spell the
    /// same name.</summary>
    public static string HandlerName(string capabilityId) => "gtfo.map." + Suffix(capabilityId);

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.trigger_session_expedition_started", Blocks.TriggerSessionExpeditionStarted.Id, "gtfo.map.trigger_session_expedition_started", "observe", new[] { SessionReadPermission }) { Waitable = true },
        new ProviderBinding("forge.module.gtfo.map.binding.trigger_objective_reactor_wave", Blocks.TriggerObjectiveReactorWave.Id, "gtfo.map.trigger_objective_reactor_wave", "observe", new[] { ObjectiveReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.trigger_objective_hsu_sampled", Blocks.TriggerObjectiveHsuSampled.Id, "gtfo.map.trigger_objective_hsu_sampled", "observe", new[] { ObjectiveReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.trigger_session_checkpoint_restored", Blocks.TriggerSessionCheckpointRestored.Id, "gtfo.map.trigger_session_checkpoint_restored", "observe", new[] { SessionReadPermission }) { Waitable = true },
        new ProviderBinding("forge.module.gtfo.map.binding.trigger_map_zone_entered", Blocks.TriggerMapZoneEntered.Id, "gtfo.map.trigger_map_zone_entered", "observe", new[] { MapObjectReadPermission }) { Waitable = true },
        new ProviderBinding("forge.module.gtfo.map.binding.trigger_map_portal_warped", Blocks.TriggerMapPortalWarped.Id, "gtfo.map.trigger_map_portal_warped", "observe", new[] { MapObjectReadPermission }) { Waitable = true },
        new ProviderBinding("forge.module.gtfo.map.binding.action_map_objective_timer", Blocks.ActionMapObjectiveTimer.Id, ObjectiveTimerHandlerName, "execute", new[] { ObjectiveWritePermission }, TimerShape),
        new ProviderBinding("forge.module.gtfo.map.binding.action_player_dimension", Blocks.ActionPlayerDimension.Id, DimensionHandlerName, "execute", new[] { PlayerWarpPermission }, DimensionShape),
        new ProviderBinding("forge.module.gtfo.map.binding.action_map_expedition_end", Blocks.ActionMapExpeditionEnd.Id, ExpeditionEndHandlerName, "execute", new[] { ExpeditionEndPermission }, ExpeditionEndShape)
    };
}

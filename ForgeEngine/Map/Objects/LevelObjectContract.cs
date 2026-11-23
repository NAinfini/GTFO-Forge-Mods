using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The level-object rows this slice owns, game-independent: the three scan facts, the container row and the level
/// item row, plus the read-only value row the same scan answers (<c>v-scan</c> of the node list). A wave row is
/// deliberately absent — the wave facts, the wave actions and the wave resource kind all belong to ForgeEnemy,
/// and a second declaration of the same binding here is how two descriptions of one row drift apart. The
/// generator rows that used to live here are the map-object generator category's own now
/// (<see cref="GeneratorContract"/>, ruling 148.4): a generator is an object the level places, so it is addressed
/// and published like a door and a terminal instead of through a level-object key.
///
/// A scan fact is read from native state and published by the host only, so each is registered with role
/// `observe` and carries no result row:
/// <list type="bullet">
/// <item><description>A scan is a `ChainedPuzzles.ChainedPuzzleInstance` — the same instance kind the alarm
/// is — and its own `Master_OnPlayerScanChanged(float scanProgress, List&lt;PlayerAgent&gt; playersInScan, int
/// inScanMax, bool[] reqObjsInScan)` callback is the one place the host learns the current progress. The
/// callback is called <b>before</b> the instance's own body, which is the only point at which the progress
/// belongs to the pulse being reported rather than to the state the body is about to write.</description></item>
/// </list>
/// </summary>
internal static class LevelObjectContract
{
    /// <summary>The Map provider these rows belong to. The spelling is the one `ModuleDefinition.ProviderId`
    /// carries, restated here so this file compiles against the framework alone — which is what lets the
    /// focused test project exercise it without the game-independent assembly's other halves — and the
    /// integration batch's row insertion is what keeps the two spellings equal.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    /// <summary>The container statuses this provider publishes, as the game's own `eResourceContainerStatus`
    /// readings: `NotSetup`, `Locked`, `Closed`, `Open`, `PlayerClose`, `PlayerFar`. A status outside the set is
    /// not published at all, so a future enum member cannot be reported as one of these.</summary>
    public static readonly string[] ContainerStateNames =
    {
        "not-setup", "locked", "closed", "open", "player-close", "player-far"
    };

    /// <summary>The scan states the value row tests, as the game's own `eChainedPuzzleStatus` readings. These are
    /// the members the catalog's scan predicate declares; any other author value is refused at registration rather
    /// than answered with a guess.
    /// </summary>
    public static readonly string[] ScanStateNames = { "disabled", "active", "solved" };

    // ---- handler and binding names -------------------------------------------------------------------
    /// <summary>The handler names the native half supplies, one per row. A fact arrives as one whole frame, so
    /// an `observe` binding resolves no shape; the names are the rows' own discriminators and are what the
    /// native publisher spells when it reports.</summary>
    public const string ScanStartedHandler = "gtfo.map.scan_started";
    public const string ScanProgressHandler = "gtfo.map.scan_progress";
    public const string ScanCompletedHandler = "gtfo.map.scan_completed";
    public const string ContainerStateHandler = "gtfo.map.container_state";
    public const string ItemPickupHandler = "gtfo.map.item_pickup";
    public const string AlarmStoppedHandler = "gtfo.map.alarm_stopped";
    public const string AlarmStartedHandler = "gtfo.map.alarm_started";

    /// <summary>The evaluator name the value row carries. A `query` row is dispatched to an evaluator,
    /// which is handed the budgeted query session and answers with the row's output ports.</summary>
    public const string ScanStateHandler = "gtfo.map.scan_state_query";

    /// <summary>The permission every row here reads through: a plan that wants to know what a scan is doing
    /// reads the map object state and nothing else. The control side stays with the action rows, which declare
    /// their own `scan.control`.</summary>
    public const string ReadPermission = "gtfo.map_object.read";

    /// <summary>The binding id of one row: the capability's own suffix under the Map provider, so either side
    /// of a row names the counterpart of the other without a second table. The suffix is everything after the
    /// capability's kind segment (`forge.&lt;kind&gt;.`), which is what lets a trigger and a condition of this
    /// family produce their two distinctive binding ids from one rule.</summary>
    public static string Binding(string capabilityId)
    {
        int kind = capabilityId.IndexOf('.', "forge.".Length);
        if (kind < 0) throw new ArgumentException("capability id has no kind segment", nameof(capabilityId));
        return ProviderId + ".binding." + capabilityId[(kind + 1)..];
    }

    /// <summary>The binding ids the native half publishes under, in the order the rows are declared.</summary>
    public static readonly string ScanStartedBinding = Binding(Blocks.TriggerObjectiveScanStarted.Id);
    public static readonly string ScanProgressBinding = Binding(Blocks.TriggerObjectiveScanMembership.Id);
    public static readonly string ScanCompletedBinding = Binding(Blocks.TriggerObjectiveScanCompleted.Id);
    public static readonly string AlarmStoppedBinding = Binding(Blocks.TriggerObjectiveAlarmStopped.Id);
    public static readonly string AlarmStartedBinding = Binding(Blocks.TriggerObjectiveAlarmStarted.Id);
    public static readonly string ContainerStateBinding = Binding(Blocks.TriggerInteractionContainerState.Id);
    public static readonly string ItemPickupBinding = Binding(Blocks.TriggerMapItemPickup.Id);
    /// <summary>The value row's own binding: a `condition` row is answered by an evaluator, so its binding
    /// carries role `evaluate` and names the reader the registration must hold.</summary>
    public static readonly string ScanStateBinding = Binding(Blocks.ConditionPredicateScan.Id);

    /// <summary>The resource kind the scan actions and the scan resource table carry. A scan is addressed by the
    /// chained puzzle's own `m_puzzleUID`, which the instance assigns itself while it is set up and which the
    /// alarm actions already use.</summary>
    public const string ChainedPuzzleKind = "chained-puzzle";

    /// <summary>Every binding this family declares, in the order the registry rows list them.</summary>
    public static readonly string[] Bindings =
    {
        ScanStartedBinding, ScanProgressBinding, ScanCompletedBinding,
        AlarmStoppedBinding, AlarmStartedBinding,
        ContainerStateBinding, ItemPickupBinding, ScanStateBinding
    };

    /// <summary>The value row's port shape: a value row has no execution ports at all — its input is the
    /// resource it reads and its outputs are its readings.</summary>
    public static readonly HandlerShape ScanStateShape = new HandlerShape()
        .Inputs("scan").Outputs("value", "state", "progress", "count");

    /// <summary>The reader of the value row, as the native half hands it over. This assembly declares the row
    /// and holds no game type, so the evaluator is built from this delegate and the game-bound registration is
    /// the only caller that supplies it. There is no static slot here: two registrations in one process would
    /// otherwise answer each other's reads.
    /// </summary>
    public sealed record LevelObjectReaders(EvaluatorHandler ScanState);

    /// <summary>The one evaluator, keyed by handler name: what the integration batch adds to the
    /// registration's evaluator table beside the player selector's.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators(LevelObjectReaders readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        return new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [ScanStateHandler] = readers.ScanState
        };
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.objective.scan_started", Blocks.TriggerObjectiveScanStarted.Id, ScanStartedHandler, "observe", new[] { ReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.objective.scan_membership", Blocks.TriggerObjectiveScanMembership.Id, ScanProgressHandler, "observe", new[] { ReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.objective.scan_completed", Blocks.TriggerObjectiveScanCompleted.Id, ScanCompletedHandler, "observe", new[] { ReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.objective.alarm_stopped", Blocks.TriggerObjectiveAlarmStopped.Id, AlarmStoppedHandler, "observe", new[] { ReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.objective.alarm_started", Blocks.TriggerObjectiveAlarmStarted.Id, AlarmStartedHandler, "observe", new[] { ReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.container_state", Blocks.TriggerInteractionContainerState.Id, ContainerStateHandler, "observe", new[] { ReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.map.item_pickup", Blocks.TriggerMapItemPickup.Id, ItemPickupHandler, "observe", new[] { ReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.predicate.scan", Blocks.ConditionPredicateScan.Id, ScanStateHandler, "evaluate", new[] { ReadPermission }, ScanStateShape)
    };
}

using System;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The world-event rows of the level: the two mount points a level's own world-event objects offer — the
/// interact trigger and the look-at trigger — and the one action that sets a world event condition, which is what
/// those mount points' authored condition lists are checked against.
///
/// The three rows are one family because they meet at one native type. A `LG_WorldEventObject` carries a stable
/// `WorldEventObjectKey`, and a `WardenObjectiveEventData` names its target through the very same text
/// (`WorldEventObjectFilter`); the two trigger rows publish that key as their subject, and the action writes the
/// `SetWorldEventCondition` member of the struct whose executor — `WorldEventManager.ExecuteEvent` — is the entry
/// every vanilla mount point already uses. Nothing here writes the state an event owns.
///
/// The capability ids the two trigger rows carry are **new** canonical ids: the catalog's `forge.trigger.map.*`
/// family has no world-event row (its members are generator_cell, generator_cluster, item_pickup, zone_entered and
/// portal_warped), and the two rows are distinguishable native types rather than one row with a `kind` parameter,
/// because a plan reads a different port set from each. This is the same rule the door and terminal interaction
/// rows already follow (`DoorTerminalEventContract` declares `forge.trigger.interaction.door_scan` and its two
/// siblings for the same reason).
///
/// The action row keeps an id of its own rather than folding into `forge.action.map.objective_state`: a world
/// event condition is not objective state — it is the flag a world event object's own condition list reads — and
/// the native entry is a different member of the same executor.
///
/// Every row is `host` execution. The two trigger rows publish only on the authoritative peer, because a world
/// event object's activation is replicated state and the host is the peer that decides it; the action writes
/// through the executor, which replicates the struct's fields the way a vanilla event list does.</summary>
internal static class WorldEventContract
{
    /// <summary>The provider this family's rows belong to, spelled once here: the declaration and every binding id
    /// below are built from it, and it is the same constant `LevelEventContract`, `DoorActionContract` and every
    /// other family of this package build theirs from. A focused case asserts the two agree.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    // ---- triggers -----------------------------------------------------------------------------------

    /// <summary>The two fact kinds these rows carry. A binding or a fact key names one of these, never the
    /// capability again, so the two cannot drift apart.
    ///
    /// The interact fact is the checklist's "world event object interacted": the level's own
    /// `LG_InteractWorldEventTrigger`, whose `Trigger(SNet_Player, Item)` is the game's own activation entry — the
    /// same body the sync replicator's master side calls. The look-at fact is `LG_LookatWorldEventTrigger`, the
    /// second trigger component a world event object can carry; it is its own row because the two native types
    /// carry different fields and a plan that mounts on one must not also receive the other's facts.</summary>
    public const string WorldEventInteractFact = "world_event_interact";
    public const string WorldEventLookatFact = "world_event_lookat";

    /// <summary>The two moments of one trigger row, in the order the native entry reaches them: `triggered` is the
    /// activation and `reset` is the trigger's own reusable reset, which a level's own `TriggerEventsOnReset` flag
    /// decides whether to run the authored list again — a plan sees the reset either way, because the fact is the
    /// trigger's state and not the list's decision. They are this contract's own two moments and travel as the
    /// native boolean `pSyncTriggerState.HasBeenTriggered` really is (`true` when triggered, `false` when reset),
    /// so the port needs no vocabulary the catalog would have to carry a new enum set for.</summary>
    internal const string TriggeredMoment = "triggered";
    internal const string ResetMoment = "reset";

    // ---- action -------------------------------------------------------------------------------------

    /// <summary>The checklist's "set a world event condition" (`forge.action.map.world_event_condition`):
    /// `eWardenObjectiveEventType.SetWorldEventCondition` (19) through `WorldEventManager.ExecuteEvent`. A
    /// condition index is one slot of the machine's own `pWorldEventManagerState.WorldEventConditions`, and that
    /// state declares `WORLD_EVENT_CONDITION_COUNT` = 16 slots, which is where the index bound below comes from.</summary>
    public const string WorldEventConditionHandlerName = "gtfo.map.world_event_condition";

    /// <summary>The read of the same condition slot the action above writes
    /// (`forge.query.map.world_event_condition`). It is declared here and not in a second file because one native
    /// member family is spelled once: the slot, its bound and the replicated state that carries it are the
    /// action's own, and splitting the read out would let two files disagree about what a condition slot is.</summary>
    public const string WorldEventConditionQueryHandlerName = "gtfo.map.world_event_condition_query";

    /// <summary>The provider-side binding id of one capability: this provider's own namespace plus the
    /// capability's own suffix, which is the rule `LevelEventContract` and `DoorTerminalEventContract` already
    /// spell, so a binding id that disagrees with the capability it serves fails registration.</summary>
    public static string Binding(string capabilityId)
        => capabilityId.StartsWith("forge.", StringComparison.Ordinal)
            ? ProviderId + ".binding." + capabilityId["forge.".Length..].Replace('.', '_')
            : throw new RuntimeContractException("world-event-capability", capabilityId);

    // ---- permissions --------------------------------------------------------------------------------

    /// <summary>The permission the two trigger rows read under: what they publish is a level object's own
    /// activation, so it is the map-object namespace's read permission, spelled as the constant value the
    /// map-object rows declare rather than by reading their contract — this file declares its permissions on its
    /// own so it compiles without a second family's contract beside it, and a case asserts the two spellings
    /// agree.</summary>
    public const string MapObjectReadPermission = "gtfo.map_object.read";
    /// <summary>The permission the condition action writes under. It sets the flag a world event object's own
    /// condition list reads, which is objective machine state and not a map object's, so the namespace is the
    /// objective one the level-event actions already write under.</summary>
    public const string WorldEventWritePermission = "objective.condition";
    /// <summary>The permission the condition read needs. It is the objective machine's read permission rather
    /// than its write one — the same namespace the write above acts in, read instead of written, which is why a
    /// plan that may ask whether a condition holds is not thereby allowed to set one.</summary>
    public const string WorldEventReadPermission = "objective.read";

    // ---- vocabularies -------------------------------------------------------------------------------

    /// <summary>One published moment as the boolean the native trigger state is: an activation is the trigger
    /// having been triggered and a reset is it having been cleared. A state key of one moment is therefore the
    /// boolean itself, which is what makes a repeated activation of an already-triggered object not news.</summary>
    internal static bool IsTriggered(string moment) => moment == TriggeredMoment;

    public static string TriggerCapability(string fact) => fact switch
    {
        WorldEventInteractFact => Blocks.TriggerInteractionWorldEventInteract.Id,
        WorldEventLookatFact => Blocks.TriggerInteractionWorldEventLookat.Id,
        _ => throw new RuntimeContractException("world-event-trigger-fact", "Unknown world event trigger fact.")
    };

    // ---- the handler's shape and its one decision ----------------------------------------------------

    /// <summary>One shared level slot, named by an objective resource (`world-event-condition:&lt;index&gt;`), which
    /// is the only place the slot is written; there is no entity recipient or per-entity write.</summary>
    public static readonly HandlerShape ConditionShape = new HandlerShape()
        .Inputs("condition_slot").Outputs("result").Parameters("solved");
    public static readonly HandlerSlot ConditionSlot = ConditionShape.InputSlot("condition_slot");
    public static readonly HandlerSlot Solved = ConditionShape.ParameterSlot("solved");

    // ---- the condition read -------------------------------------------------------------------------

    /// <summary>The read's ports: the slot is the same objective resource the action writes and the one boolean is the answer.</summary>
    public static readonly HandlerShape ConditionQueryShape = new HandlerShape()
        .Inputs("condition_slot").Outputs("solved");
    private static readonly HandlerSlot QuerySlot = ConditionQueryShape.InputSlot("condition_slot");

    /// <summary>The one read this row needs from the world: the slot's replicated value, or null when this
    /// machine's world event manager cannot answer for it. A slot reported as unsolved reads exactly like a slot
    /// the world never had, so a read that could not be made is refused instead of answered `false`.</summary>
    public sealed record ConditionReader(Func<int, bool?> Read);

    /// <summary>The evaluator, keyed by handler name: the slot is checked against the same bound the action
    /// enforces — `pWorldEventManagerState.WORLD_EVENT_CONDITION_COUNT` — and the answer is the replicated
    /// value, never a second copy of it. Its two refusals are the same words the condition action uses for a slot
    /// this build does not declare and for a machine whose replicated condition state cannot be read, so both rows
    /// of this family say one thing about each.</summary>
    public static EvaluatorHandler ConditionQueryEvaluator(ConditionReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return (context, outputs) =>
        {
            if (ConditionIndex(ObjectiveLayerReference.InputId(context, QuerySlot, "condition_slot", ResultCode.WorldEventConditionInvalid) ?? "")
                is not { } index)
                throw new RuntimeContractException(ResultCode.WorldEventConditionInvalid, "The condition slot is not one this build declares.");
            bool solved = reader.Read(index)
                ?? throw new RuntimeContractException(ResultCode.WorldEventConditionUnavailable,
                    "No replicated world event condition state is available.");
            outputs.Boolean(0, solved);
        };
    }

    // ---- the condition slot -------------------------------------------------------------------------

    /// <summary>The number of condition slots the machine's own state declares
    /// (`pWorldEventManagerState.WORLD_EVENT_CONDITION_COUNT`, read from the build 20403457). An index outside
    /// `[0, 16)` is one the replicated state has no slot for, so it is refused rather than truncated.</summary>
    public const int ConditionSlots = 16;

    /// <summary>One condition request, already validated: the slot it names and the value it writes. It is the
    /// only shape the native executor accepts, so a value that reached the executor is known to be legal.</summary>
    public readonly struct ConditionRequest
    {
        internal ConditionRequest(int index, bool solved) { Index = index; Solved = solved; }
        public int Index { get; }
        public bool Solved { get; }
    }

    /// <summary>The one decision the condition action makes, taken before the native call and in the
    /// game-independent assembly so a case can drive every refusal without a game. A refusal names the one check
    /// that refused it; anything else is a request the native executor may run.</summary>
    public static bool TryCondition(CommandContext context, out ConditionRequest request, out string? code)
    {
        ArgumentNullException.ThrowIfNull(context);
        request = default;
        code = null;
        if (!context.IsHost) { code = ResultCode.AuthorityOrPhase; return false; }
        if (!context.TryResource(ConditionSlot, out string kind, out string id) || kind != ConditionResourceKind
            || !id.StartsWith(ConditionResourcePrefix, StringComparison.Ordinal))
        {
            code = ResultCode.WorldEventConditionTargetInvalid;
            return false;
        }
        // A condition reference whose slot this build does not declare names no slot of the replicated state.
        if (ConditionIndex(id) is not { } condition)
        {
            code = ResultCode.WorldEventConditionInvalid;
            return false;
        }
        // A structural boolean arrives as the JSON boolean a plan writes or the `true`/`false` member name a
        // structural enum resolves to; anything else is refused.
        if ((context.Boolean(Solved) ?? (bool.TryParse(context.Text(Solved), out var member) ? member : null))
            is not { } solved)
        {
            code = ResultCode.WorldEventSolvedInvalid;
            return false;
        }
        request = new ConditionRequest(condition, solved);
        return true;
    }

    public const string ConditionResourceKind = "objective";
    public const string ConditionResourcePrefix = "world-event-condition:";

    /// <summary>A canonical shared slot of the current level. Reject aliases, malformed IDs and unknown slots.</summary>
    public static string ConditionResourceId(int index)
    {
        if (index < 0 || index >= ConditionSlots) throw new ArgumentOutOfRangeException(nameof(index));
        return ConditionResourcePrefix + index.ToString(CultureInfo.InvariantCulture);
    }

    public static int? ConditionIndex(string id)
    {
        if (!id.StartsWith(ConditionResourcePrefix, StringComparison.Ordinal)
            || !int.TryParse(id[ConditionResourcePrefix.Length..], NumberStyles.None,
                CultureInfo.InvariantCulture, out int index)
            || index < 0 || index >= ConditionSlots || id != ConditionResourceId(index)) return null;
        return index;
    }

    /// <summary>The resource recipient is preserved in the existing result ABI, including refusals. A missing
    /// or malformed recipient has no row; the command still carries the explicit refusal.</summary>
    public static Outcome ConditionResult(CommandContext context, Status status, Commit committed, string code)
    {
        var rows = context.Rows;
        if (rows.Count > 0)
            rows.Begin(0).Integer(Blocks.ActionMapWorldEventCondition.RowCondition,
                    context.TryResource(ConditionSlot, out _, out string id) && ConditionIndex(id) is { } index ? index : -1)
                .Boolean(Blocks.ActionMapWorldEventCondition.RowSolved, context.Boolean(Solved)
                    ?? (bool.TryParse(context.Text(Solved), out var solved) && solved))
                .End(status, committed, code);
        return new Outcome(status, committed, code);
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.trigger_interaction_world_event_interact", Blocks.TriggerInteractionWorldEventInteract.Id, "forge.module.gtfo.map.observe.world_event_interact", "observe", new[] { MapObjectReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.trigger_interaction_world_event_lookat", Blocks.TriggerInteractionWorldEventLookat.Id, "forge.module.gtfo.map.observe.world_event_lookat", "observe", new[] { MapObjectReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.action_map_world_event_condition", Blocks.ActionMapWorldEventCondition.Id, WorldEventConditionHandlerName, "execute", new[] { WorldEventWritePermission }, ConditionShape),
        new ProviderBinding("forge.module.gtfo.map.binding.query.map.world_event_condition", Blocks.QueryMapWorldEventCondition.Id, WorldEventConditionQueryHandlerName, "evaluate", new[] { WorldEventReadPermission }, ConditionQueryShape)
    };
}

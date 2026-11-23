using System;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The one map-object state row this provider owns: the interaction switch
/// (`forge.action.map.interaction_state`). It is the row the EOS feature's stopped rows fold into
/// (`object_state`, `terminal_set_active`, `door_interaction_state`): one row that turns a map object's own
/// interaction on or off. A terminal requests `ChangeState(TERM_State.Sleeping/Awake)` through the native
/// authority chain and reads `CurrentStateName` after the transition. A security door uses only its four own
/// `Interact_Base.SetActive` / `IsActive` pairs (`m_intOpenDoor`, `m_intUseKeyItem`, `m_intHack`,
/// `m_intCustomMessage`); switching availability never changes an interaction's placement.
///
/// There is no alarm row here. The rulings deleted `forge.action.map.alarm` (the row `door_alarm` had been
/// renamed into): a door's alarm is the chained puzzle instance its lock component holds, the very instance
/// `forge.action.map.scan_state` writes, and one native write keeps one card. A plan that wants the puzzle a
/// door carries reads it from `forge.query.map.door_state`'s `puzzle` output.
///
/// The row is host-authoritative this round: a usable object is world state. The capability rows come from the
/// generated block table; this file owns the bindings, the handler shapes and the read's evaluator, and the native
/// half supplies only the body.</summary>
internal static class MapStateContract
{
    // ---- forge.action.map.interaction_state ---------------------------------------------------------------

    public const string InteractionBindingId = ModuleDefinition.ProviderId + ".binding.map.interaction_state";
    public const string InteractionHandlerName = "gtfo.map.interaction_state";

    // ---- the interaction read (forge.query.map.interaction_state) -------------------------------------

    /// <summary>The read of the interaction state the row above switches: whether one interaction of one map
    /// object is available right now. It is declared beside the write because one native member family is spelled
    /// once — both halves dispatch a terminal's `CurrentStateName` and a security door's four `Interact_Base`
    /// parts, and splitting them would let two files disagree about what "usable" means for an object.</summary>
    public const string QueryHandlerName = "gtfo.map.interaction_state_query";

    public static readonly string QueryBindingId = ModuleDefinition.ProviderId + ".binding.query.map.interaction_state";

    /// <summary>The read's ports: the map object and the one boolean, with the same optional `interaction`
    /// parameter the write row carries.</summary>
    public static readonly HandlerShape QueryShape = new HandlerShape()
        .Inputs("target").Outputs("enabled").Parameters("interaction");

    /// <summary>The refusal an input that is not a map object of this provider gets.</summary>
    public const string QueryInputKindCode = "interaction-state-query-input-kind";
    /// <summary>The refusal a map object this world no longer holds, or one whose own interaction state did not
    /// read, gets. A disabled interaction and an object that could not be read are different facts.</summary>
    public const string QueryInputStaleCode = "interaction-state-query-input-stale";
    /// <summary>The refusal a request whose structural `interaction` names a member this row does not carry
    /// gets.</summary>
    public const string QueryInteractionCode = "interaction-state-query-interaction";

    /// <summary>The permission the interaction write needs: it decides whether a player may touch an object at
    /// all.</summary>
    public const string InteractionPermission = "map.interaction_state";

    /// <summary>The `interaction` members: which interaction the request is about. `all` is the whole object —
    /// a terminal's own state, or every one of a door's four interactables — and the other four name the one door
    /// part the ledger row's own switches address. A part is refused by name for a terminal, which has no
    /// parts.</summary>
    public static readonly string[] InteractionTargets = { "all", "open", "use_key", "hacking", "custom_message" };

    public static HandlerShape InteractionShape { get; } = new HandlerShape()
        .Inputs("targets").Outputs("result").Parameters("operation", "interaction");
    public static readonly HandlerSlot InteractionRecipients = InteractionShape.InputSlot("targets");
    public static readonly HandlerSlot InteractionOperation = InteractionShape.ParameterSlot("operation");
    public static readonly HandlerSlot InteractionKind = InteractionShape.ParameterSlot("interaction");

    // ---- forge.action.map.object_visibility ---------------------------------------------------------------

    /// <summary>The show/hide switch of a placed model or interaction point: Unity's own `GameObject.SetActive` on the
    /// root Forge built for the object. It shares the interaction switch's host ledger, so one state document carries
    /// both and every machine replays both.</summary>
    public const string VisibilityHandlerName = "gtfo.map.object_visibility";
    public const string VisibilityPermission = "map.object_visibility";
    public static HandlerShape VisibilityShape { get; } = new HandlerShape()
        .Inputs("targets").Outputs("result").Parameters("operation");
    public static readonly HandlerSlot VisibilityRecipients = VisibilityShape.InputSlot("targets");
    public static readonly HandlerSlot VisibilityOperation = VisibilityShape.ParameterSlot("operation");

    /// <summary>One interaction's availability, as the object's own members answer it.</summary>
    public readonly record struct InteractionReading(bool Enabled);

    /// <summary>The one native read this row needs: the map object a reference names, the interaction member the
    /// request chose, and whether it is available. Null means this world does not answer for the reference.</summary>
    public sealed record InteractionReader(Func<EntityReference, string, InteractionReading?> Read);

    /// <summary>The evaluator, keyed by handler name: the input is refused by name unless it names a map object
    /// of this provider's namespace, the optional `interaction` parameter defaults to the whole object and is
    /// refused when it names a member the row does not carry, and the answer is the object's own reading.</summary>
    public static EvaluatorHandler QueryEvaluator(InteractionReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return (context, outputs) =>
        {
            if (!context.Inputs.TryGetProperty("target", out var value) || value.ValueKind == JsonValueKind.Null)
                throw new RuntimeContractException("missing-field", "target");
            var target = RuntimeJson.Entity(value);
            if (!EntityKinds.Is(target, EntityKinds.MapObject))
                throw new RuntimeContractException(QueryInputKindCode,
                    "This provider reads the interactions of a map object, not of " + target.Id);
            string interaction = InteractionTargets[0];
            if (context.Parameters.ValueKind == JsonValueKind.Object
                && context.Parameters.TryGetProperty("interaction", out var declared)
                && declared.ValueKind != JsonValueKind.Null)
            {
                if (declared.ValueKind != JsonValueKind.String)
                    throw new RuntimeContractException(QueryInteractionCode, "The interaction member is not a name.");
                interaction = declared.GetString()!;
                if (Array.IndexOf(InteractionTargets, interaction) < 0)
                    throw new RuntimeContractException(QueryInteractionCode, "The interaction is not one this row carries: " + interaction);
            }
            var reading = reader.Read(target, interaction)
                ?? throw new RuntimeContractException(QueryInputStaleCode, "The map object is not current: " + target.Id);
            outputs.Boolean(0, reading.Enabled);
        };
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(InteractionBindingId, Blocks.ActionMapInteractionState.Id, InteractionHandlerName, "execute", new[] { InteractionPermission }, InteractionShape),
        new ProviderBinding(ModuleDefinition.ProviderId + ".binding.map.object_visibility", Blocks.ActionMapObjectVisibility.Id, VisibilityHandlerName, "execute", new[] { VisibilityPermission }, VisibilityShape),
        new ProviderBinding("forge.module.gtfo.map.binding.query.map.interaction_state", Blocks.QueryMapInteractionState.Id, QueryHandlerName, "evaluate", new[] { "gtfo.map_object.read" }, QueryShape)
    };
}

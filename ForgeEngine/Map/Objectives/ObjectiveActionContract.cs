using System;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>Reviewed objective and extraction contracts. Objective actions address a layer resource;
/// extraction actions address the actual elevator or exit item.</summary>
internal static class ObjectiveActionContract
{
    /// <summary>The last observed or confirmed native enable/disable call for the named endpoint.
    /// It witnesses that call, not the puzzle status. An endpoint with no call is unreadable.</summary>
    public const string ExtractionQueryHandlerName = "gtfo.map.extraction_query";

    /// <summary>The read's ports: the exit the plan names and the one boolean the mirror answers.</summary>
    public static readonly HandlerShape ExtractionQueryShape = new HandlerShape()
        .Inputs("extraction").Outputs("enabled");

    /// <summary>The refusal an exit reference of another world gets: the mirror records a write per world, so a
    /// reference of a level that is gone is refused rather than answered from the level standing now.</summary>
    public const string ExtractionQueryStaleCode = "extraction-state-input-stale";
    /// <summary>The refusal a world with no confirmed extraction write gets. A level whose exit nobody armed and
    /// a level whose write nobody recorded are different facts, and only the second is this row's business.</summary>
    public const string ExtractionStateCode = "extraction-state-unrecorded";
    /// <summary>The permission the read needs: it answers a write the extraction row performed, so a plan
    /// declares that it may read the exit's armed state before it can.</summary>
    public const string ExtractionReadPermission = "extraction.read";

    public const string StateBindingId = ModuleDefinition.ProviderId + ".binding.objective_state";
    public const string PhaseBindingId = ModuleDefinition.ProviderId + ".binding.objective_phase";
    public const string ExtractionBindingId = ModuleDefinition.ProviderId + ".binding.extraction_enable";
    public const string ExtractionMoveBindingId = ModuleDefinition.ProviderId + ".binding.extraction_move";

    public const string StateHandlerName = "gtfo.map.objective_state";
    public const string PhaseHandlerName = "gtfo.map.objective_phase";
    public const string ExtractionHandlerName = "gtfo.map.extraction_enable";
    public const string ExtractionMoveHandlerName = "gtfo.map.extraction_move";

    /// <summary>The permission the objective rows write under: a request moves the state of an objective this
    /// map's own data blocks declare, so the plan must be able to act on the map it is attached to.</summary>
    public const string ObjectiveWritePermission = "objective.state";

    /// <summary>The permission the extraction row writes under: it arms the exit item the level itself built.</summary>
    public const string ExtractionWritePermission = "extraction.control";

    /// <summary>The layer members an objective reference (`layer:&lt;name&gt;`) names, in the order `LG_LayerType`
    /// declares them: the same three layers the native objective state is kept per.</summary>
    public static readonly string[] Layers = { "main", "secondary", "third" };

    // One shape per handler, resolved once at registration against the capability its binding implements. Every
    // one names exactly the ports and parameters its row declares, so the shape and the row cannot drift.
    public static readonly HandlerShape StateShape = new HandlerShape()
        .Inputs("objectives", "state").Outputs("result")
        .Parameters("chain", "sub_objective", "item_id", "extra_time");
    public static readonly HandlerSlot StateObjectives = StateShape.InputSlot("objectives");
    public static readonly HandlerSlot StateInput = StateShape.InputSlot("state");
    public static readonly HandlerSlot StateChain = StateShape.ParameterSlot("chain");
    public static readonly HandlerSlot StateSubObjective = StateShape.ParameterSlot("sub_objective");
    public static readonly HandlerSlot StateItemId = StateShape.ParameterSlot("item_id");
    public static readonly HandlerSlot StateExtraTime = StateShape.ParameterSlot("extra_time");
    public static readonly HandlerShape PhaseShape = new HandlerShape()
        .Inputs("objectives", "phase", "wave_index", "wave_progress").Outputs("result")
        .Parameters("chain", "event_break_index", "event_index");
    public static readonly HandlerSlot PhaseObjectives = PhaseShape.InputSlot("objectives");
    public static readonly HandlerSlot PhaseInput = PhaseShape.InputSlot("phase");
    public static readonly HandlerSlot WaveIndex = PhaseShape.InputSlot("wave_index");
    public static readonly HandlerSlot WaveProgress = PhaseShape.InputSlot("wave_progress");
    public static readonly HandlerSlot PhaseChain = PhaseShape.ParameterSlot("chain");
    public static readonly HandlerSlot EventBreakIndex = PhaseShape.ParameterSlot("event_break_index");
    public static readonly HandlerSlot EventIndex = PhaseShape.ParameterSlot("event_index");
    public static readonly HandlerShape ExtractionShape = new HandlerShape()
        .Inputs("extractions", "enabled").Outputs("result");
    public static readonly HandlerSlot Extractions = ExtractionShape.InputSlot("extractions");
    public static readonly HandlerSlot ExtractionEnabled = ExtractionShape.InputSlot("enabled");
    public static readonly HandlerShape ExtractionMoveShape = new HandlerShape().Inputs("extraction", "position").Outputs("result");
    public static readonly HandlerSlot MoveExtraction = ExtractionMoveShape.InputSlot("extraction");
    public static readonly HandlerSlot MovePosition = ExtractionMoveShape.InputSlot("position");

    /// <summary>The one mirror the read needs: the last `enabled` value this provider confirmed for the level's
    /// own exit, read for the world epoch the reference belongs to. Null means this world has no confirmed write,
    /// which is refused by name rather than answered `false`.</summary>
    public sealed record ExtractionReader(Func<EntityReference, long, bool?> Read);

    /// <summary>The evaluator, keyed by handler name: the exit input is required, and the answer is the mirror of
    /// the confirmed write for the world the query session reads. The reference is the plan's own identity for the
    /// exit; the reader refuses one that belongs to another world instead of borrowing the standing level's
    /// write.</summary>
    public static EvaluatorHandler ExtractionQueryEvaluator(ExtractionReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return (context, outputs) =>
        {
            if (!context.Inputs.TryGetProperty("extraction", out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null)
                throw new RuntimeContractException("missing-field", "extraction");
            var reference = RuntimeJson.Entity(value);
            bool enabled = reader.Read(reference, context.Query.WorldEpoch)
                ?? throw new RuntimeContractException(ExtractionStateCode,
                    "No extraction write is recorded for this world: " + reference.Id);
            outputs.Boolean(0, enabled);
        };
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(StateBindingId, Blocks.ActionMapObjectiveState.Id, StateHandlerName, "execute", new[] { ObjectiveWritePermission }, StateShape),
        new ProviderBinding(PhaseBindingId, Blocks.ActionMapObjectivePhase.Id, PhaseHandlerName, "execute", new[] { ObjectiveWritePermission }, PhaseShape),
        new ProviderBinding(ExtractionBindingId, Blocks.ActionMapExtractionEnable.Id, ExtractionHandlerName, "execute", new[] { ExtractionWritePermission }, ExtractionShape),
        new ProviderBinding(ExtractionMoveBindingId, Blocks.ActionMapExtractionMove.Id, ExtractionMoveHandlerName, "execute", new[] { ExtractionWritePermission }, ExtractionMoveShape),
        new ProviderBinding("forge.module.gtfo.map.binding.query.map.extraction", Blocks.QueryMapExtraction.Id, ExtractionQueryHandlerName, "evaluate", new[] { ExtractionReadPermission }, ExtractionQueryShape)
    };
}

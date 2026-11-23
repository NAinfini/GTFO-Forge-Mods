using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Pure;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>One row of the Trigger vocabulary as this provider binds it: the block it answers for, the binding that
/// implements it and the role that binding is registered under — `observe` for a `query` row, `evaluate` for a `pure`
/// one — the handler the binding resolves against, and the ports that handler reads. The row itself (its kind, text
/// and graph) is the generated block table's.</summary>
internal sealed record ObservedNode(string CapabilityId, string Role, string BindingId, string HandlerName,
    HandlerShape Shape, EvaluatorHandler Evaluate);

/// <summary>One family of observation rows. A family declares rows and nothing else, and a module composes the families it registers —
/// so a batch adds one file and one composition line, and two batches never edit one shared table.</summary>
internal sealed class ObservedFamily
{
    private ObservedFamily(ObservedNode[] nodes)
    {
        Nodes = Array.AsReadOnly(nodes);
    }

    /// <summary>The declared rows in registration order. Public because the contract tests iterate the same table
    /// the module registers instead of restating it.</summary>
    public IReadOnlyList<ObservedNode> Nodes { get; }

    /// <summary>One family from the rows one batch declares. A handler name two rows share is a construction error
    /// here, the same way the registry refuses it at registration: one name is one handler.</summary>
    public static ObservedFamily Declare(params ObservedNode[] nodes) => new(nodes);

    /// <summary>The families one module registers, joined in composition order. The tables are joined rather than
    /// registered as separate modules, so the registry resolves one shape per binding across the whole provider.</summary>
    public static ObservedFamily Compose(params ObservedFamily[] families)
        => new(families.SelectMany(family => family.Nodes).ToArray());
}

/// <summary>The one row factory every family declares through. The binding and the handler are named after the
/// block: its id's last segment (or <c>name</c>, for the one row whose last segment says too little) names both, and
/// the handler also carries the block's kind. The role follows the block's own tier, so neither is spelled a second
/// time beside the table.</summary>
internal static class ObservedDeclaration
{
    internal static ObservedNode Row(string capabilityId, HandlerShape shape, EvaluatorHandler evaluate, string? name = null)
    {
        var index = Blocks.IndexOf(capabilityId);
        RuntimeJson.Require(index >= 0 && Blocks.Rows[index].Provider == ModuleDefinition.ProviderId, "capability-owner",
            ModuleDefinition.ProviderId + " declares no " + capabilityId + ".");
        var block = Blocks.Rows[index];
        name ??= capabilityId[(capabilityId.LastIndexOf('.') + 1)..];
        return new ObservedNode(capabilityId, block.Execution == "query" ? "observe" : "evaluate",
            ModuleDefinition.ProviderId + ".binding." + name, "trigger." + block.Kind + "." + name, shape, evaluate);
    }
}

/// <summary>The reads every observation handler shares: a required port, a declared enum member, the structural
/// `empty` policy, and the one budgeted session read. Each of them refuses by name instead of answering a default,
/// because an absent port answered with a zero or an empty set would read exactly like an observed world.</summary>
internal static class ObservedEvaluation
{
    internal static EntityReference Entity(EvaluationContext context, string port)
        => context.TryInputCell(port, out var cell) && cell.Kind == ValueKind.Entity && cell.Reference is EntityReference single
            ? single : RuntimeJson.Entity(context.RequiredInput(port));

    /// <summary>The references a held entity collection cell carries, as the producer published them, or null when
    /// the cell is not one; the caller then reads the port's document.</summary>
    private static IReadOnlyList<EntityReference>? Held(EvaluationContext context, string port)
    {
        if (!context.TryInputCell(port, out var cell) || cell.Kind != ValueKind.Entity || !cell.Valid) return null;
        return cell.Reference as EntityReference[];
    }

    /// <summary>A structural member as the plan holds it, when it is held as text.</summary>
    private static string? HeldText(EvaluationContext context, string parameter)
        => context.TryParameterCell(parameter, out var cell) && cell.Kind == ValueKind.String ? cell.Reference as string : null;

    internal static string PortText(EvaluationContext context, string port) => context.RequiredInput(port).GetString()!;

    /// <summary>One required structural parameter of the resolved frame, spelled as its member name. A parameter
    /// the row declares is never an input, so it is read from the frame's parameter bag and never from a port.</summary>
    internal static string ParameterText(EvaluationContext context, string parameter)
        => HeldText(context, parameter) ?? (context.Parameters.TryGetProperty(parameter, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()!
            : throw new RuntimeContractException("missing-field", parameter));

    /// <summary>One optional structural parameter of the resolved frame, spelled as its member name, or null when
    /// the plan left it out. The row's own rule reads an unwritten member as the first member of its list, which is
    /// the row's business and not this reader's.</summary>
    internal static string? OptionalParameterText(EvaluationContext context, string parameter)
        => HeldText(context, parameter) ?? (context.Parameters.TryGetProperty(parameter, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null);

    /// <summary>One optional structural number of the resolved frame, or null when the plan left it out.</summary>
    internal static int? OptionalParameterInteger(EvaluationContext context, string parameter)
        => context.Parameters.TryGetProperty(parameter, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)value.GetDouble() : null;

    /// <summary>One optional value input of the resolved frame, or null when the plan left it unwired or wrote a
    /// null. A port the row declares optional is the author's own answer, so an unwired one reads as absent here
    /// and never as a zero the row invented.</summary>
    internal static int? OptionalInputInteger(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)value.GetDouble() : null;

    /// <summary>One optional vector input of the resolved frame, or null when the plan left it unwired.</summary>
    internal static double[]? OptionalInputVector(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetDouble()).ToArray() : null;

    /// <summary>One optional structural flag, or <paramref name="whenAbsent"/> when the plan left it out: a
    /// parameter the compiler wrote is the author's answer, and one it did not write is nobody's.</summary>
    internal static bool OptionalParameterFlag(EvaluationContext context, string parameter, bool whenAbsent)
        => context.Parameters.TryGetProperty(parameter, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : whenAbsent;

    /// <summary>The seed a step draws with: the number the author wrote on its own `seed` port under `fixed`, or
    /// the trigger event's own number under `session` (<see cref="SeededStream.OfEvent"/>): a new draw for every
    /// trigger event, the same draw wherever that one event's step is read.</summary>
    internal static long Seed(EvaluationContext context, string port, string mode)
        => mode == "session" ? SeededStream.OfEvent(context) : context.Inputs.GetProperty(port).GetInt64();

    /// <summary>The references one collection port carries. A port the plan left empty is an empty set; a port that
    /// is not there at all is refused, so an absent candidate collection never reads as an observed empty world.</summary>
    internal static IReadOnlyList<EntityReference> Candidates(EvaluationContext context, string port)
    {
        if (Held(context, port) is { } held) return held;
        var value = context.RequiredInput(port);
        if (value.ValueKind != JsonValueKind.Array)
            throw new RuntimeContractException("missing-field", "Required entity collection is not an array: " + port);
        return Array.AsReadOnly(value.EnumerateArray().Select(RuntimeJson.Entity).ToArray());
    }

    /// <summary>The references one event-sourced collection port carries. A collection a trigger does not carry is
    /// empty here, which the row's own `empty` policy then answers for.</summary>
    internal static IReadOnlyList<EntityReference> Collection(EvaluationContext context, string port)
        => Held(context, port) ?? (!context.Inputs.TryGetProperty(port, out var value) || value.ValueKind == JsonValueKind.Null
            ? Array.Empty<EntityReference>()
            : Array.AsReadOnly(value.EnumerateArray().Select(RuntimeJson.Entity).ToArray()));

    /// <summary>A requested count as <see cref="ReferenceCollections"/> itself bounds it, so an out-of-range size
    /// is refused with the collection's own code rather than by an integer conversion.</summary>
    internal static int RequestedCount(JsonElement value, int minimum, int maximum)
    {
        var count = value.GetInt64();
        if (count < minimum || count > maximum)
            throw new RuntimeContractException("pure-selection-count",
                "Requested selection must be in [" + minimum + ", " + maximum + "].");
        return (int)count;
    }

    /// <summary>One row's selection count (`count` on the catalog's random row, `max_targets` on the limit and
    /// ranking rows), bounded exactly as the collection algebra bounds a selection.</summary>
    internal static int Selection(EvaluationContext context, string port)
        => RequestedCount(context.RequiredInput(port), 1, ReferenceCollections.MaximumSelection);

    /// <summary>One reference's current observation. A read the kernel could not complete rejects the step with
    /// the session's own code; there is no partial answer to fall back on.</summary>
    internal static RuntimeEntitySnapshot Observed(EvaluationContext context, string port)
    {
        if (!context.Query.TrySnapshot(RuntimeJson.Entity(context.RequiredInput(port)), out var snapshot, out var code))
            throw new RuntimeContractException(code, "Entity observation failed: " + code);
        return snapshot!;
    }

    /// <summary>The catalog's structural empty policy, read from one row's resolved parameter frame: `fail` refuses
    /// an empty answer, `emit-empty` passes it on.</summary>
    internal static EmptySelectionPolicy EmptyPolicy(JsonElement parameters) => Policy(parameters, "empty");

    /// <summary>One structural `empty_policy` parameter by its own name. The area row carries two — `empty` for a
    /// field that was measured and held nothing, `unloaded` for a field this world could not measure at all — and
    /// the two are read the same way, so neither can drift into a second member spelling.</summary>
    internal static EmptySelectionPolicy Policy(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException("missing-field", name);
        if (value.ValueEquals("emit-empty")) return EmptySelectionPolicy.EmitEmpty;
        if (value.ValueEquals("fail")) return EmptySelectionPolicy.Fail;
        throw new RuntimeContractException("pure-operation", "Unknown empty selection policy: " + value.GetString());
    }

    /// <summary>One collection answer: the selected references with the row's own structural `empty` policy
    /// applied, which is the set policy of the catalog and never a policy of the world.</summary>
    internal static void Targets(EvaluationContext context, EvaluationOutputs outputs, IReadOnlyList<EntityReference> selected)
        => outputs.Entities(0, ReferenceCollections.ApplyEmptyPolicy(selected, EmptyPolicy(context.Parameters)));

    /// <summary>The catalog's comparison member names, in the order the C# enum declares them.</summary>
    internal static ScalarComparison Comparison(string member) => member switch
    {
        "eq" => ScalarComparison.Equal,
        "ne" => ScalarComparison.NotEqual,
        "lt" => ScalarComparison.Less,
        "lte" => ScalarComparison.LessOrEqual,
        "gt" => ScalarComparison.Greater,
        "gte" => ScalarComparison.GreaterOrEqual,
        var other => throw new RuntimeContractException("pure-operation", "Unknown operator member: " + other)
    };

    internal static void Value(EvaluationOutputs outputs, bool value) => outputs.Boolean(0, value);
    internal static void Value(EvaluationOutputs outputs, EntityReference? value) => outputs.Entity(0, value);
}

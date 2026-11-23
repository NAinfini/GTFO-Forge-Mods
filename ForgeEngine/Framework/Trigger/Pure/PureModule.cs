using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Targeting;

namespace ForgeEngine.Trigger.Pure;

/// <summary>The production Trigger nodes that compute a value without reading the world, published from one
/// declaration table per family. Every handler calls the existing stateless helpers in this namespace and reads its
/// inputs by the block's own port ids; the shapes are resolved against the block table's rows at registration,
/// so a name only one half knows is a registration error.
///
/// The vocabulary is the authoring node list's own: a fixed number, the one calculation card whose operation
/// picks add, subtract, multiply, divide, minimum, maximum, absolute or round, the clamp, the host-evaluated random
/// draw, the two text rows, the two-point rows that measure the gap between two positions, and the one logic card
/// whose operation picks all, any or not. The comparison is not here: the catalog declares its typed row `query` because one `value_type`
/// member is an entity, so it is declared with the other evaluated conditions and compares through the same
/// <see cref="PureConditions"/> helpers. Rows the list does not have — the remaining vector rows (dot, cross,
/// normalize, angle), curve, falloff, remap, by-* and collection families, `chance`, `lerp`, `select_value` and
/// `power` — are deleted rather than left declared: a declared row nobody can author is a second vocabulary, and
/// the random draw merged the probability gate into itself (a probability is that draw compared through
/// `forge.condition.predicate.compare`).
///
/// `forge.modifier.value.random_range` is evaluated by the host only, because only the host runs a plan: the pure
/// tier is never evaluated on a client, and the draw's result reaches clients the way every other step result does,
/// with the execution that produced it. A wired or written seed is the draw's whole seed; a seed the plan left out
/// is the trigger event's own number (<see cref="SeededStream.OfEvent"/>), so the pure tier stays re-evaluable: the
/// draw is a function of what the plan wrote and the event it is evaluated for, and reads no level seed.</summary>
internal static class PureModule
{
    /// <summary>Member order is the wire: an enum's compiled value is its index into the catalog's own set (or its
    /// inline `values`), so each array below is declared in the order of the C# enum it maps onto.</summary>
    internal static readonly string[] RoundingModes = { "floor", "ceil", "nearest", "truncate" };
    internal static readonly string[] ZeroPolicies = { "reject", "zero", "passthrough" };
    internal static readonly string[] CalculateOperations =
        { "add", "subtract", "multiply", "divide", "minimum", "maximum", "absolute", "round" };
    internal static readonly string[] LogicOperations = { "all", "any", "not" };
    /// <summary>The range row's own two inline sets: what its input value is measured in, and what it does with an
    /// input outside the window.</summary>
    internal static readonly string[] RangeInputUnits = { "absolute", "relative", "normalized" };
    internal static readonly string[] RangeBounds = { "clamp", "allow", "reject" };

    /// <summary>One family per table, in catalog order: the value-only rows a `pure` step evaluates. Public
    /// because the contract tests iterate the same tables the module registers instead of restating them.</summary>
    public static IReadOnlyList<ObservedNode> Nodes { get; } = ScalarDeclarations.Nodes
        .Concat(VariadicDeclarations.Nodes)
        .Concat(TextDeclarations.Nodes)
        .Concat(VectorDeclarations.Nodes)
        .Concat(ListDeclarations.Nodes)
        .ToArray();

    /// <summary>One row of this module, named the way every Trigger row is. A row declared here must be `pure`.</summary>
    internal static ObservedNode Row(string capabilityId, HandlerShape shape, EvaluatorHandler evaluate)
    {
        var node = ObservedDeclaration.Row(capabilityId, shape, evaluate);
        if (node.Role != "evaluate")
            throw new RuntimeContractException("operator-tier", capabilityId + " is not a pure row.");
        return node;
    }

    // ---- handlers ------------------------------------------------------------------------------------------
    // Each handler reads its inputs by the catalog's port ids and returns one property per declared output.
    // The declaration tables above name these by method group; the handler name a binding publishes is derived
    // from the capability id, so a row and its implementation cannot drift apart.

    internal static void Constant(EvaluationContext context, EvaluationOutputs outputs)
        => Value(outputs, ScalarNodes.Constant(Parameter(context, "value")));
    /// <summary>The calculation card: the `operation` member picks the computation, and an unwritten optional
    /// member is the set's first one, exactly as the contract resolves it.</summary>
    internal static void Calculate(EvaluationContext context, EvaluationOutputs outputs)
        => Value(outputs, VariadicNodes.Calculate(
            (ScalarOperation)OptionalMember(context, "operation", CalculateOperations), Numbers(context),
            (DivisionZeroPolicy)OptionalMember(context, "zero_policy", ZeroPolicies),
            (ScalarRounding)OptionalMember(context, "mode", RoundingModes)));
    internal static void Clamp(EvaluationContext context, EvaluationOutputs outputs)
        => Value(outputs, ScalarNodes.Clamp(Input(context, "value"), Input(context, "minimum"), Input(context, "maximum")));
    internal static void RandomRange(EvaluationContext context, EvaluationOutputs outputs)
        => Value(outputs, SeededNodes.Uniform(Input(context, "minimum"), Input(context, "maximum"),
            Written(context, "seed") ? Whole(context, "seed") : SeededStream.OfEvent(context)));
    /// <summary>Whether the plan gave an optional input a value, wired or written.</summary>
    private static bool Written(EvaluationContext context, string port)
        => context.TryInputCell(port, out _) || context.InputPortCount < 0 && context.Inputs.ValueKind == JsonValueKind.Object
            && context.Inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null;
    /// <summary>The one range row: where the input sits in its own window, curved and optionally flipped, placed in
    /// the output window. Every number it reads is an input — the two windows, the two thresholds the author counts
    /// the value against and the curve's exponent — because each of them is a value an upstream step may compute;
    /// only the two vocabularies are structural. `flip` is an input like the rest, so a plan that leaves it out is
    /// refused at the port rather than read as "not flipped".</summary>
    internal static void MapRange(EvaluationContext context, EvaluationOutputs outputs)
        => Value(outputs, ScalarNodes.MapRange(Input(context, "value"), Input(context, "input_min"), Input(context, "input_max"),
            Input(context, "input_floor"), Input(context, "input_ceiling"), Input(context, "output_min"),
            Input(context, "output_max"),
            (RangeInputUnit)Member(RangeInputUnits, EnumParameter(context, "input_unit"), "input_unit"),
            Input(context, "exponent"), Flag(context, "flip"),
            (RangeBounds)Member(RangeBounds, EnumParameter(context, "bounds"), "bounds")));
    /// <summary>The members a template slot's `type_k` offers, in the catalog's order: the first is what an
    /// unwritten parameter resolves to.</summary>
    internal static readonly string[] TextSlotTypes = { "number", "integer", "string", "boolean", "enum" };

    /// <summary>The one text row: each wired slot is packed by the type its `type_k` names — an option value by its
    /// member's index in the set `set_k` names, which is the order the template's option names are written in.</summary>
    internal static void Text(EvaluationContext context, EvaluationOutputs outputs)
    {
        var values = new TextTemplates.Value?[TextTemplates.SlotCount];
        for (var slot = 1; slot <= TextTemplates.SlotCount; slot++)
        {
            if (!context.Inputs.TryGetProperty(TextDeclarations.TextInputs[slot], out var raw) || raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                continue;
            values[slot - 1] = OptionalMember(context, TextDeclarations.TextParameters[(slot - 1) * 2], TextSlotTypes) switch
            {
                0 or 1 => TextTemplates.Value.Number(PureNumbers.Input(raw.GetDouble())),
                2 => TextTemplates.Value.Text(raw.GetString()!),
                3 => TextTemplates.Value.Flag(raw.GetBoolean()),
                _ => TextTemplates.Value.Option(OptionIndex(context, TextDeclarations.TextParameters[(slot - 1) * 2 + 1], raw.GetString()!))
            };
        }
        Value(outputs, TextNodes.Compose(PortText(context, "template"), values));
    }

    private static int OptionIndex(EvaluationContext context, string setParameter, string member)
    {
        var set = context.Parameters.TryGetProperty(setParameter, out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : null;
        if (set == null || !RuntimeGraphContracts.EnumSets.TryGetValue(set, out var members))
            throw new RuntimeContractException("pure-text-template", "An option value slot names no member set: " + setParameter);
        return Member(members, member, setParameter);
    }
    /// <summary>The one fragment row: the template with every occurrence of the chosen native fragment's marker
    /// filled from the value. The marker is the structural member's own name in square brackets, and the member set
    /// is the native `eWardenTextFragment` the catalog froze, so the row resolves no level, chain or fragment table
    /// and answers the same replacement wherever it runs.</summary>
    internal static void TextFragment(EvaluationContext context, EvaluationOutputs outputs)
        => Value(outputs, TextNodes.ReplaceFragment(PortText(context, "template"), EnumParameter(context, "fragment"),
            PortText(context, "value")));
    internal static void Logic(EvaluationContext context, EvaluationOutputs outputs)
        => Value(outputs, VariadicNodes.Logic((LogicOperation)OptionalMember(context, "operation", LogicOperations), Flags(context)));
    /// <summary>The two two-point rows: both read the same pair of metre ports, one answers the gap in metres and
    /// the other the unit direction the gap points in.</summary>
    internal static void Distance(EvaluationContext context, EvaluationOutputs outputs)
        => Value(outputs, VectorNodes.Distance(Position(context, "from"), Position(context, "to")));
    internal static void Direction(EvaluationContext context, EvaluationOutputs outputs)
        => Value(outputs, VectorNodes.Direction(Position(context, "from"), Position(context, "to")));
    /// <summary>Every input port of a variadic row is one slot of the same computation. The frame keeps the
    /// contract's own port order, which is the order the author wired, so the values are read in that order; the
    /// 2..32 count bound lives in <see cref="VariadicNodes"/>, not here.</summary>
    // The frame's own cells are read in place: no input document is written and no list or array is built per
    // evaluation. A cell that is not the plain value the port names (or a context built from a document, as a test
    // does) takes the document path, which answers exactly what it always did.
    private static readonly double[] NumberScratch = new double[VariadicNodes.MaximumInputs];
    private static readonly bool[] FlagScratch = new bool[VariadicNodes.MaximumInputs];

    private static ReadOnlySpan<double> Numbers(EvaluationContext context)
    {
        var ports = context.InputPortCount;
        var count = 0;
        for (var position = 0; position < ports; position++)
        {
            if (!context.TryInputCellAt(position, out var cell)) continue;
            if (count == NumberScratch.Length || !TryNumber(cell, out NumberScratch[count])) return NumbersOfDocument(context);
            count++;
        }
        return ports < 0 ? NumbersOfDocument(context) : NumberScratch.AsSpan(0, count);
    }
    private static double[] NumbersOfDocument(EvaluationContext context)
    {
        var values = new List<double>();
        foreach (var port in context.Inputs.EnumerateObject()) values.Add(port.Value.GetDouble());
        return values.ToArray();
    }
    private static ReadOnlySpan<bool> Flags(EvaluationContext context)
    {
        var ports = context.InputPortCount;
        var count = 0;
        for (var position = 0; position < ports; position++)
        {
            if (!context.TryInputCellAt(position, out var cell)) continue;
            if (count == FlagScratch.Length || !cell.Valid || cell.Count >= 0 || cell.Kind != ValueKind.Boolean) return FlagsOfDocument(context);
            FlagScratch[count++] = cell.Boolean;
        }
        return ports < 0 ? FlagsOfDocument(context) : FlagScratch.AsSpan(0, count);
    }
    private static bool[] FlagsOfDocument(EvaluationContext context)
    {
        var values = new List<bool>();
        foreach (var port in context.Inputs.EnumerateObject()) values.Add(port.Value.GetBoolean());
        return values.ToArray();
    }
    private static bool TryNumber(CommandValue cell, out double value)
    {
        value = 0;
        if (!cell.Valid || cell.Count >= 0) return false;
        if (cell.Kind == ValueKind.Number) { value = cell.Number; return true; }
        if (cell.Kind == ValueKind.Integer) { value = cell.Integer; return true; }
        return false;
    }

    private static double Input(EvaluationContext context, string port)
        => context.TryInputCell(port, out var cell) && TryNumber(cell, out var number) ? number : context.Inputs.GetProperty(port).GetDouble();
    private static bool Flag(EvaluationContext context, string port)
        => context.TryInputCell(port, out var cell) && cell.Valid && cell.Count < 0 && cell.Kind == ValueKind.Boolean
            ? cell.Boolean : context.Inputs.GetProperty(port).GetBoolean();
    private static long Whole(EvaluationContext context, string port)
        => context.TryInputCell(port, out var cell) && cell.Valid && cell.Count < 0 && cell.Kind == ValueKind.Integer
            ? cell.Integer : context.Inputs.GetProperty(port).GetInt64();
    private static string PortText(EvaluationContext context, string port) => context.Inputs.GetProperty(port).GetString()!;
    /// <summary>A position port as its three components. The frame has already checked the vector's shape and
    /// that every component is finite, so the row reads the value it was handed and adds no second rule.</summary>
    private static double[] Position(EvaluationContext context, string port)
        => context.Inputs.GetProperty(port).EnumerateArray().Select(component => component.GetDouble()).ToArray();
    private static double Parameter(EvaluationContext context, string name) => context.Parameters.GetProperty(name).GetDouble();
    private static string EnumParameter(EvaluationContext context, string name) => context.Parameters.GetProperty(name).GetString()!;

    /// <summary>An optional enum parameter: the member the plan wrote, or the set's first member when it wrote none.
    /// </summary>
    private static int OptionalMember(EvaluationContext context, string name, string[] members)
        => context.Parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Member(members, value.GetString()!, name) : 0;

    /// <summary>Resolves a member name the kernel already mapped back from its compiled index. The C# enums are
    /// declared in the catalog set's own order, so the member's position in the array is the enum value.</summary>
    private static int Member(string[] members, string name, string parameter)
    {
        var index = Array.IndexOf(members, name);
        if (index < 0) throw new RuntimeContractException("pure-operation", "Unknown " + parameter + " member: " + name);
        return index;
    }

    private static void Value(EvaluationOutputs outputs, double value) => outputs.Number(0, value);
    private static void Value(EvaluationOutputs outputs, bool value) => outputs.Boolean(0, value);
    private static void Value(EvaluationOutputs outputs, string value) => outputs.Text(0, value);
    private static void Value(EvaluationOutputs outputs, double[] value) => outputs.Vector(0, value[0], value[1], value[2]);
}

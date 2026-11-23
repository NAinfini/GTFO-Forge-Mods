using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

internal readonly struct CommandVector
{
    internal CommandVector(double x, double y, double z) { X = x; Y = y; Z = z; }
    internal double X { get; }
    internal double Y { get; }
    internal double Z { get; }
}

/// <summary>One handler value after the JSON boundary has been validated and decoded. The raw value stays beside
/// the typed fields solely for the legacy <see cref="CommandContext.Inputs"/> view and handle/result ABIs; typed
/// accessors never inspect it.</summary>
internal readonly struct CommandValue
{
    private CommandValue(ValueKind kind, JsonElement json, object? reference = null, long integer = 0,
        double number = 0, bool boolean = false, double y = 0, double z = 0, int count = -1, bool valid = true,
        JsonElement wireJson = default, Func<string, ResourceRef?>? resourceResolver = null, bool typed = false)
    {
        Kind = kind; Json = json; Reference = reference; Integer = integer; Number = number; Boolean = boolean;
        Y = y; Z = z; Count = count; Valid = valid; Typed = typed;
        WireJson = wireJson.ValueKind == JsonValueKind.Undefined ? json : wireJson;
        ResourceResolver = resourceResolver;
    }

    internal bool Valid { get; }
    internal ValueKind Kind { get; }
    internal JsonElement Json { get; }
    /// <summary>The compiled/wire spelling beside the handler spelling. Enum inputs and parameters differ.</summary>
    internal JsonElement WireJson { get; }
    internal Func<string, ResourceRef?>? ResourceResolver { get; }
    internal object? Reference { get; }
    internal long Integer { get; }
    internal double Number { get; }
    internal bool Boolean { get; }
    internal double Y { get; }
    internal double Z { get; }
    /// <summary>-1 for a single value; otherwise the collection's element count.</summary>
    internal int Count { get; }

    /// <summary>A value a control step moves without decoding it. <paramref name="resolved"/> is the owner-resolved
    /// resource reference it carries, kept so a variable write stores it without resolving it again.</summary>
    internal static CommandValue Raw(JsonElement value, ResourceRef? resolved = null) => new(ValueKind.Invalid, value, reference: resolved, valid: false);

    internal CommandValue WithWire(JsonElement value) => new(Kind, Json, Reference, Integer, Number, Boolean, Y, Z,
        Count, Valid, value, ResourceResolver, Typed);

    /// <summary>A value an evaluator wrote through <see cref="EvaluationOutputs"/>: decoded already, and with no JSON
    /// until a boundary asks for one (<see cref="Rendered"/>). Booleans, nulls and enum members carry their shared
    /// spellings from the start, so they never need one written.</summary>
    internal bool Typed { get; }
    /// <summary>Whether the cell holds a value at all, decoded or as JSON.</summary>
    internal bool Carried => Kind != ValueKind.Missing || Json.ValueKind != JsonValueKind.Undefined;

    internal static readonly JsonElement TrueJson = RuntimeJson.Parse("true");
    internal static readonly JsonElement FalseJson = RuntimeJson.Parse("false");
    internal static readonly JsonElement NullJson = RuntimeJson.Parse("null");
    internal static CommandValue OfBoolean(bool value) => new(ValueKind.Boolean, value ? TrueJson : FalseJson, boolean: value, typed: true);
    internal static CommandValue OfNull() => new(ValueKind.Null, NullJson, typed: true);
    internal static CommandValue OfInteger(long value) => new(ValueKind.Integer, default, integer: value, typed: true);
    internal static CommandValue OfNumber(double value) => new(ValueKind.Number, default, number: value, typed: true);
    internal static CommandValue OfText(string value) => new(ValueKind.String, default, reference: value, typed: true);
    /// <summary>An enum member: the handler reads its name, a frame or the wire carries its index.</summary>
    internal static CommandValue OfEnum(string name, int index, JsonElement nameJson, JsonElement indexJson)
        => new(ValueKind.String, nameJson, reference: name, integer: index, wireJson: indexJson, typed: true);
    internal static CommandValue OfVector(double x, double y, double z) => new(ValueKind.Vector3, default, number: x, y: y, z: z, typed: true);
    internal static CommandValue OfEntity(EntityReference value) => new(ValueKind.Entity, default, reference: value, typed: true);
    internal static CommandValue OfResource(ResourceRef value) => new(ValueKind.Resource, default, reference: value, typed: true);
    /// <summary>A collection of references. The array is never written again once it is in a cell: a later
    /// evaluation that answers differently hands over a new one, so a frame that kept this one keeps its value.</summary>
    internal static CommandValue OfEntities(EntityReference[] values) => new(ValueKind.Entity, default, reference: values, count: values.Length, typed: true);
    /// <summary>The rows another step of this activation wrote, as the region they live in: a reader takes them by
    /// column, and they are written as JSON only for a handler that reads its inputs as a document. The region is the
    /// activation's, so the cell is a value of this activation only.</summary>
    internal static CommandValue OfRows(RuntimeKernel.ResultRowSet rows) => new(ValueKind.Result, default, reference: rows, typed: true);
    /// <summary>A value an evaluator handed over as JSON, checked by the port's own rule after the evaluator returns.</summary>
    internal static CommandValue OfJson(JsonElement value) => new(ValueKind.Missing, value, valid: false);

    /// <summary>The cell with its JSON written, the handler spelling and the wire spelling alike. A cell that has JSON
    /// already is itself.</summary>
    internal CommandValue Rendered()
    {
        if (Json.ValueKind != JsonValueKind.Undefined) return this;
        var json = RuntimeJson.Write(this, static (writer, cell) => cell.WriteValue(writer));
        return new(Kind, json, Reference, Integer, Number, Boolean, Y, Z, Count, Valid,
            WireJson.ValueKind == JsonValueKind.Undefined ? json : WireJson, ResourceResolver, Typed);
    }

    /// <summary>Writes the value as the handler reads it (<paramref name="wire"/> false) or as a frame and the wire
    /// carry it, without building a document.</summary>
    internal void WriteValue(Utf8JsonWriter writer, bool wire = false)
    {
        var written = wire ? WireJson : Json;
        if (written.ValueKind != JsonValueKind.Undefined) { written.WriteTo(writer); return; }
        switch (Kind)
        {
            case ValueKind.Null: writer.WriteNullValue(); return;
            case ValueKind.Boolean: writer.WriteBooleanValue(Boolean); return;
            case ValueKind.Integer: writer.WriteNumberValue(Integer); return;
            case ValueKind.Number: writer.WriteNumberValue(Number); return;
            case ValueKind.String: writer.WriteStringValue((string)Reference!); return;
            case ValueKind.Resource:
                var resource = (ResourceRef)Reference!;
                writer.WriteStartObject();
                writer.WriteString("resourceKind"u8, resource.ResourceKind);
                writer.WriteString("resourceId"u8, resource.ResourceId);
                writer.WriteEndObject();
                return;
            case ValueKind.Vector3:
                writer.WriteStartArray(); writer.WriteNumberValue(Number); writer.WriteNumberValue(Y); writer.WriteNumberValue(Z);
                writer.WriteEndArray(); return;
            case ValueKind.Entity:
                if (Reference is EntityReference single) { WriteEntity(writer, single); return; }
                writer.WriteStartArray();
                if (Reference is EntityReference[] many) for (var i = 0; i < many.Length; i++) WriteEntity(writer, many[i]);
                else if (Reference is RuntimeKernel.EntityCheck[] checks) for (var i = 0; i < checks.Length; i++) WriteEntity(writer, checks[i].Entity);
                writer.WriteEndArray(); return;
            case ValueKind.Result:
                var rows = (RuntimeKernel.ResultRowSet)Reference!;
                ResultRowAbi.WriteSet(writer, new Frame(rows.Space.Slots, rows.Row.Slot, rows.Space.Strings.Memory),
                    rows.Row, rows.Count, rows.Set, settled: true);
                return;
            default: throw new InvalidOperationException("A cell without JSON holds a decoded value.");
        }
    }

    internal static void WriteEntity(Utf8JsonWriter writer, EntityReference entity)
    {
        writer.WriteStartObject();
        writer.WriteString("id"u8, entity.Id);
        writer.WriteNumber("worldEpoch"u8, entity.WorldEpoch);
        writer.WriteNumber("lifeEpoch"u8, entity.LifeEpoch);
        writer.WriteEndObject();
    }

    internal static CommandValue Read(JsonElement value, ValueKind kind, bool many, object? decoded = null,
        Func<string, ResourceRef?>? resourceResolver = null)
    {
        if (value.ValueKind == JsonValueKind.Null) return new CommandValue(ValueKind.Null, value);
        try
        {
            if (many)
            {
                if (value.ValueKind != JsonValueKind.Array) return Invalid(value);
                var count = value.GetArrayLength();
                switch (kind)
                {
                    case ValueKind.Boolean:
                        return new CommandValue(ValueKind.Boolean, value,
                        value.EnumerateArray().Select(item => item.GetBoolean()).ToArray(), count: count);
                    case ValueKind.Integer:
                        return new CommandValue(ValueKind.Integer, value,
                        value.EnumerateArray().Select(item => item.GetInt64()).ToArray(), count: count);
                    case ValueKind.Number:
                        return new CommandValue(ValueKind.Number, value,
                        value.EnumerateArray().Select(item => item.GetDouble()).ToArray(), count: count);
                    case ValueKind.String:
                        return new CommandValue(ValueKind.String, value,
                        value.EnumerateArray().Select(item => item.GetString() ?? "").ToArray(), count: count);
                    case ValueKind.Vector3:
                        return new CommandValue(ValueKind.Vector3, value, value.EnumerateArray().Select(Vector).ToArray(), count: count);
                    case ValueKind.Entity:
                        // A single reference wired into a collection input was decoded before the walk wrapped it.
                        return new CommandValue(ValueKind.Entity, value, decoded is EntityReference single
                            ? count == 1 ? new[] { single } : throw new InvalidOperationException()
                            : decoded ?? value.EnumerateArray().Select(RuntimeJson.EntityRow).ToArray(), count: count);
                    case ValueKind.Handle:
                        return new CommandValue(ValueKind.Handle, value, count: count);
                    case ValueKind.Resource:
                        return new CommandValue(ValueKind.Resource, value, value.EnumerateArray().Select(RuntimeJson.ResourceRefOf).ToArray(),
                            count: count, resourceResolver: resourceResolver);
                    default: return Invalid(value);
                }
            }
            return kind switch
            {
                ValueKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    => new CommandValue(ValueKind.Boolean, value, boolean: value.GetBoolean()),
                ValueKind.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer)
                    => new CommandValue(ValueKind.Integer, value, integer: integer),
                ValueKind.Number when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
                    => new CommandValue(ValueKind.Number, value, number: number),
                ValueKind.String when value.ValueKind == JsonValueKind.String
                    => new CommandValue(ValueKind.String, value, reference: value.GetString() ?? ""),
                ValueKind.Vector3 when value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 3
                    => SingleVector(value),
                ValueKind.Entity when value.ValueKind == JsonValueKind.Object
                    => new CommandValue(ValueKind.Entity, value, reference: decoded as EntityReference ?? RuntimeJson.EntityRow(value)),
                ValueKind.Resource when value.ValueKind == JsonValueKind.Object
                    => new CommandValue(ValueKind.Resource, value,
                        reference: decoded as ResourceRef ?? RuntimeJson.ResourceRefOf(value),
                        resourceResolver: resourceResolver),
                ValueKind.Handle when value.ValueKind == JsonValueKind.Object
                    => new CommandValue(ValueKind.Handle, value),
                ValueKind.Event when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var row)
                    => new CommandValue(ValueKind.Event, value, integer: row),
                _ => Invalid(value)
            };
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException
            or RuntimeContractException or JsonException)
        { return Invalid(value); }

        static CommandValue Invalid(JsonElement value) => new(ValueKind.Invalid, value, valid: false);

        static CommandVector Vector(JsonElement vector)
        {
            if (vector.ValueKind != JsonValueKind.Array || vector.GetArrayLength() != 3) throw new InvalidOperationException();
            return new CommandVector(vector[0].GetDouble(), vector[1].GetDouble(), vector[2].GetDouble());
        }
        static CommandValue SingleVector(JsonElement vector)
        {
            var read = Vector(vector);
            return new CommandValue(ValueKind.Vector3, vector, number: read.X, y: read.Y, z: read.Z);
        }
    }
}

/// <summary>
/// The registry and the subscription table as dispatch reads them. A binding's provider, capability and trigger ports,
/// an entity kind's resolver and a binding's mounted entries are facts of a registration or a plan load; they are read
/// into typed tables exactly when those change, so a publish and a walk read fields and indices instead of asking a
/// JSON document for names.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>The resource kind's current owner lookup, captured once with a loaded input descriptor.</summary>
    internal Func<string, ResourceRef?>? BindResourceResolver(string kind)
        => registry.Tables.ResourceProviders.TryGetValue(kind, out var registered) ? registered.Provider.Resolve : null;

    /// <summary>One registered binding as a publish reads it: who owns it, what it implements, and — for a trigger —
    /// the value ports its payload is checked against.</summary>
    internal sealed class BindingView
    {
        internal BindingView(string bindingId, JsonElement binding, JsonElement capability)
        {
            ProviderId = RuntimeJson.Text(binding, "providerId");
            CapabilityId = RuntimeJson.Text(binding, "capabilityId");
            Capability = capability;
            IsTrigger = RuntimeGraphContracts.OptionalText(capability, "kind") == "trigger";
            if (!IsTrigger) return;
            var graph = capability.GetProperty("graph");
            var outputs = RuntimeJson.Rows(graph, "outputs");
            var values = new List<PortView>(outputs.Length);
            var sheet = new List<int>(outputs.Length);
            SheetCells = new int[outputs.Length];
            for (var position = 0; position < outputs.Length; position++)
            {
                SheetCells[position] = -1;
                if (RuntimeJson.Text(outputs[position], "type") == "execution") continue;
                SheetCells[position] = values.Count;
                values.Add(PortView.Of(outputs[position]));
                sheet.Add(position);
            }
            Outputs = values.ToArray();
            CellSheet = sheet.ToArray();
            OutputIds = Array.ConvertAll(outputs, static port => RuntimeJson.Text(port, "id"));
            ActorCells = RuntimeActorRoles.PayloadCells(Outputs);
            NameFilterCell = VariableContracts.NameFilterPort(bindingId) is { } named ? CellOf(named) : -1;
            MessageCell = CellOf("message");
            MessageValueCell = CellOf(VariableContracts.MessageValuePort);
            CarriesEquipment = Array.Exists(Outputs, static port => port.Kind == PortView.Kinds.Entity && port.Id == "equipment"
                && port.Contract.TryGetProperty("entityKinds", out var kinds) && kinds.ValueKind == JsonValueKind.Array
                && kinds.EnumerateArray().Any(static kind => kind.ValueKind == JsonValueKind.String && kind.GetString() == EntityKinds.Equipment));
            var subject = RuntimeGraphContracts.SubjectPort(graph);
            if (subject.ValueKind != JsonValueKind.Undefined)
            {
                var subjectId = RuntimeJson.Text(subject, "id");
                EndSubjectIndex = Array.FindIndex(Outputs, port => port.Id == subjectId);
            }
        }

        internal string ProviderId { get; }
        internal string CapabilityId { get; }
        internal JsonElement Capability { get; }
        internal bool IsTrigger { get; }
        /// <summary>Whether this trigger declares an `equipment` output of the equipment entity kind. Resolved with the
        /// registry, never per event: a fact whose payload can name equipment is what the attribution gate is about.</summary>
        internal bool CarriesEquipment { get; }
        /// <summary>The trigger's value outputs, in contract order; empty for every other capability kind.</summary>
        internal PortView[] Outputs { get; } = Array.Empty<PortView>();
        /// <summary>Every declared output's id, execution ports included: the positions the generated
        /// <c>Out…</c> constants of the trigger's block address.</summary>
        internal string[] OutputIds { get; } = Array.Empty<string>();
        /// <summary>The value-output index declared by graph.subject, never by payload order or port-name
        /// conventions. Capture it even before an end_on consumer loads, so queued facts retain their subject.</summary>
        internal int EndSubjectIndex { get; } = -1;
        /// <summary>An event's cells are one per value output, in <see cref="Outputs"/> order. These relate that
        /// order to the positions a publisher writes at (<see cref="OutputIds"/>, execution ports included): the
        /// sheet position of each cell, and the cell of each sheet position (-1 for an execution port).</summary>
        internal int[] CellSheet { get; } = Array.Empty<int>();
        internal int[] SheetCells { get; } = Array.Empty<int>();
        /// <summary>The cell each actor role is read from, in <see cref="RuntimeActorRoles"/>'s own order; -1 where
        /// the trigger declares no such port.</summary>
        internal int[] ActorCells { get; } = Array.Empty<int>();
        /// <summary>The cell a name-indexed binding's entries are claimed by, and the two a `g-wait` hands on.</summary>
        internal int NameFilterCell { get; } = -1;
        internal int MessageCell { get; } = -1;
        internal int MessageValueCell { get; } = -1;

        /// <summary>The cell one value output is carried in, by the output's id; -1 where the trigger declares none.
        /// Asked where a plan is loaded, never per event.</summary>
        internal int CellOf(string portId)
        {
            for (var i = 0; i < Outputs.Length; i++) if (Outputs[i].Id == portId) return i;
            return -1;
        }

        /// <summary>The payload document of one event's cells: every carried output under its id, in contract order,
        /// as a frame and the wire carry it.</summary>
        internal void WriteEvent(Utf8JsonWriter writer, CommandValue[] cells)
        {
            writer.WriteStartObject();
            for (var i = 0; i < cells.Length; i++)
            {
                if (!cells[i].Carried) continue;
                writer.WritePropertyName(Outputs[i].Id);
                cells[i].WriteValue(writer, wire: true);
            }
            writer.WriteEndObject();
        }
        internal JsonElement RenderEvent(CommandValue[] cells)
            => RuntimeJson.Write((View: this, Cells: cells), static (writer, state) => state.View.WriteEvent(writer, state.Cells));
    }

    /// <summary>A validated event subject captured at the publish boundary. Single values need no singleton
    /// array; sets reuse the references already decoded by ResolveInput. Missing/null/empty all contain nobody.</summary>
    internal readonly struct EventSubject
    {
        private readonly EntityReference? single;
        private readonly EntityReference[]? many;
        internal EventSubject(object? decoded)
        {
            single = decoded as EntityReference;
            many = decoded as EntityReference[];
            Holder = single == null ? default : new EquipmentHolderSubject(single);
        }
        internal int Count => many?.Length ?? (single == null ? 0 : 1);
        internal EntityReference At(int index) => many == null ? single! : many[index];
        internal EquipmentHolderSubject Holder { get; }
    }

    /// <summary>The entries mounted on one binding: the template work items in plan order, and their grouping by plan
    /// with each plan's command bound. A dispatch every template claims without a subject is that whole set, so it
    /// carries these two arrays as they are.</summary>
    internal sealed class Subscription
    {
        internal Subscription(Work[] work) : this(work, indexByMessage: true) { }
        private Subscription(Work[] work, bool indexByMessage)
        {
            Work = work;
            Usage = PlanUsageOf(work).ToArray();
            if (!indexByMessage) return;
            // The entries on the custom-message binding each name the one message they listen for (the loader refuses
            // an entry that does not), so the binding's work is indexed by that name once, where the subscription
            // is built. Every other binding keeps its flat set.
            Dictionary<string, List<Work>>? byName = null;
            foreach (var item in work)
            {
                if (item.Entry.MessageName is not { } name) continue;
                byName ??= new Dictionary<string, List<Work>>(StringComparer.Ordinal);
                if (!byName.TryGetValue(name, out var listeners)) byName.Add(name, listeners = new List<Work>());
                listeners.Add(item);
            }
            if (byName != null)
                ByMessage = byName.ToDictionary(row => row.Key, row => new Subscription(row.Value.ToArray(), indexByMessage: false), StringComparer.Ordinal);
        }
        internal Work[] Work { get; }
        internal PendingPlan[] Usage { get; }
        private Dictionary<string, Subscription>? ByMessage { get; }

        /// <summary>The entries this event can be claimed by: for a custom message or a variable change, the listeners
        /// that named it (none when nobody did); for every other event, the binding's whole set.</summary>
        internal Subscription ForEvent(RuntimeEvent value)
        {
            if (ByMessage == null) return this;
            var cell = value.CellBinding!.NameFilterCell;
            return cell >= 0 && CellText(value.Cells![cell]) is { } name
                && ByMessage.TryGetValue(name, out var listeners) ? listeners : NoSubscription;
        }
    }

    private static readonly Subscription NoSubscription = new(Array.Empty<Work>());

    /// <summary>What one advance wrote into a receipt list, as the result's own array.</summary>
    internal static T[] Copy<T>(List<T> written) => written.Count == 0 ? Array.Empty<T>() : written.ToArray();
    /// <summary>Re-reads the typed registry tables after the registry changed: views for the bindings that arrived,
    /// none for the ones that left, and the resolver table as a whole.</summary>
    private void RefreshRegistryViews(IEnumerable<string>? removedBindings)
    {
        registry.RefreshBindingViews(removedBindings);
        foreach (var gate in subscriptionGates.Values) BindGate(gate);
        registry.RefreshResolverTable();
        registry.RefreshEntityObserverTable();
        equipmentBindings = registry.BindingViews.Where(row => row.Value.CarriesEquipment).Select(row => row.Key).ToArray();
        equipmentAttributionGate.HasSubscribers = EquipmentConsumed();
        BindDeferredResults();
    }

    internal static void RequireDistinct(EntityReference[] read, int count, EntityReference reference)
    {
        for (var i = 0; i < count; i++)
            if (read[i] == reference)
                throw new RuntimeContractException("duplicate-entity", "Repeated recipients require explicit semantics.");
    }

    /// <summary>
    /// <c>RuntimeJson.Shape</c> against typed ports: an object whose every field is one of the ports and which
    /// carries every port that is not optional (or every port, where <paramref name="optionalAllowed"/> is false).
    /// The unknown field is refused before the missing one, and field names are compared as the UTF-8 text the
    /// document holds.
    /// </summary>
    internal static void ShapePorts(JsonElement value, PortView[] ports, bool optionalAllowed)
    {
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Object, "object-required", "Expected JSON object.");
        foreach (var property in value.EnumerateObject())
        {
            var known = false;
            for (var i = 0; i < ports.Length; i++) if (property.NameEquals(ports[i].IdUtf8)) { known = true; break; }
            if (!known) throw new RuntimeContractException("unknown-field", property.Name);
        }
        for (var i = 0; i < ports.Length; i++)
            if ((!optionalAllowed || !ports[i].Optional) && !value.TryGetProperty(ports[i].IdUtf8, out _))
                throw new RuntimeContractException("missing-field", ports[i].Id);
    }

    /// <summary>
    /// The replay ledger's value for one publish: the binding, the world, the tick, the scope, the causality and the
    /// payload's own text, each mixed as the UTF-16 code units of its invariant spelling and closed by a unit
    /// separator. The payload is read from its UTF-8 bytes and mixed as the code units the transcoded text holds, so
    /// the value is the one the string form of the same fields always produced — the migration image carries it, and
    /// a host that migrates compares it with rows the previous host wrote.
    /// </summary>
    private LedgerHash EventLedgerFingerprint(RuntimeEvent value)
    {
        var low = 14695981039346656037UL; var high = 1099511628211UL;
        RawJson.Mix(ref low, ref high, value.BindingId); Separate(ref low, ref high);
        RawJson.Mix(ref low, ref high, value.WorldEpoch); Separate(ref low, ref high);
        RawJson.Mix(ref low, ref high, value.SimulationTick); Separate(ref low, ref high);
        RawJson.Mix(ref low, ref high, value.ScopeId); Separate(ref low, ref high);
        RawJson.Mix(ref low, ref high, value.CauseId ?? ""); Separate(ref low, ref high);
        RawJson.Mix(ref low, ref high, value.RootEventId ?? ""); Separate(ref low, ref high);
        RawJson.Mix(ref low, ref high, value.CausalDepth); Separate(ref low, ref high);
        RawJson.MixUtf8(ref low, ref high, executor.EventPayloadText(value)); Separate(ref low, ref high);
        return new LedgerHash(low, high);

        static void Separate(ref ulong low, ref ulong high)
        { low = (low ^ '\u001f') * 1099511628211UL; high = (high ^ '\u001f') * 14695981039346656037UL; }
    }

    /// <summary>
    /// One step's inputs and parameters as its handler reads them, resolved in wiring order: a literal as the loader
    /// resolved it, a wired value read out of the activation's frames or rows, and an event output read out of the
    /// dispatch's own payload, each checked against its own typed port. A dispatched step moves a handle rather
    /// than decoding it (<paramref name="dispatched"/>); an evaluated one checks it as a value. A promoted
    /// parameter's value is written directly into its pre-resolved typed slot. Its input port checks type and enum
    /// membership, and its pre-resolved parameter rule checks required presence and bounds; no parameter document is
    /// assembled unless a handler or external boundary later asks for one.
    /// </summary>
    /// <summary>The walk's input frame for one dispatched step. `present` asks whether its one value is there, and a
    /// reference whose owner proved its life or world over is not: the step gets the frame an unwired value leaves and
    /// takes `missing`, an explicit answer instead of cancelling the entry. Every other step, and every other refusal of
    /// the tested value, is refused as before.</summary>
    private StepInputs ResolveTestedInputs(Work item, ResolvedStep step, Pending pending)
    {
        if (step.Control != Blocks.ControlFlowPresent.Id) return ResolveStepInputs(item, step, pending, dispatched: true);
        try { return ResolveStepInputs(item, step, pending, dispatched: true); }
        catch (RuntimeContractException gone) when (gone.Code is "stale-entity" or "stale-world")
        { return step.StoredInputs?.Reset() ?? new StepInputs(step); }
    }

    private StepInputs ResolveStepInputs(Work item, ResolvedStep step, Pending pending, bool dispatched)
    {
        var frame = step.StoredInputs?.Reset() ?? new StepInputs(step);
        var inputs = step.Inputs;
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var port = input.View;
            JsonElement value;
            object? decoded = null;
            // A literal is the value the plan compiled; where the document's spelling of it is not the value a
            // handler reads, the loader resolved that form once at load and this is the one place the walk picks it up.
            ResourceRef? held = null;
            if (input.HandlerLiteral is { } literal) { value = literal; decoded = input.ResolvedLiteral; }
            else if (input.FromStep is { } from)
            {
                // A result row is read positionally from the activation's typed region through the descriptor the
                // plan compiled — never from a receipt — and the rendering this read makes is the handler boundary's
                // own conversion, the one place a typed row becomes JSON again.
                if (input.ResultPort)
                {
                    if (!executor.resultRows.TryGetValue(StepIndexOf(item, from.Step), out var rows))
                        throw kernelFlow.MissingStepSlot(item.Entry, step, from, "from-step-value");
                    // The rows are handed on as the region itself: they were validated when they were written, and
                    // a reader takes them by column. Only a handler that reads its inputs as a document has them
                    // written as JSON, the first time it asks.
                    if (step.InputSlots[i] >= 0) { frame.SetTyped(step.InputSlots[i], CommandValue.OfRows(rows)); continue; }
                    value = executor.StepRows(StepIndexOf(item, from.Step), step.NodeId, input.Name);
                }
                else
                {
                    var producer = item.Entry.Steps[from.Step];
                    if (producer.StoredOutputs != null ? producer.OutputActivation != kernelFlow.valueActivation
                        : producer.NodeKind is not ("pure" or "query") && !executor.stepFrames.ContainsKey(from.Step))
                        throw kernelFlow.MissingStepSlot(item.Entry, step, from);
                    if (producer.StoredOutputs != null)
                    {
                        value = producer.StoredOutputs[from.Port];
                        // A variable read (or a when-present step that forwarded it) publishes the reference its cell
                        // already holds resolved against the kind's owner: that is the answer this read would ask
                        // for, so it is handed on as it is.
                        held = producer.StoredResolved?[from.Port];
                    }
                    else
                    {
                        var published = EvaluateStep(item, from.Step, pending, out var evaluated);
                        if (evaluated == null) value = StepSlotValue(published, port, step.NodeId, input.Name);
                        else
                        {
                            // A typed answer was checked against this very port when it was written; the walk hands
                            // the cell on as it is and re-asks only whether the entities it carries still live. A
                            // value widened into a collection, a promoted parameter or one kept as JSON takes the
                            // JSON road below.
                            var cell = evaluated.Cells[from.Port];
                            if (cell.Typed && !input.Wrap && step.InputSlots[i] >= 0)
                            {
                                if (executor.memoEntities.TryGetValue(producer.EntityMemoBase + from.Port, out var live)) entityLives.RecheckEntities(live);
                                frame.SetTyped(step.InputSlots[i], cell);
                                continue;
                            }
                            value = cell.Rendered().WireJson;
                        }
                    }
                    // A `pure`/`query` frame was validated against its own contract when it was produced and cannot
                    // change inside the activation, so a later read of it only re-asks what a handler of this same
                    // activation can still invalidate: whether the entities it carries are alive. Every other frame
                    // — and every read that widens a single value into a collection — is validated in full.
                    if (held != null) decoded = held;
                    else if (!input.Wrap && executor.validatedSlots.ContainsKey(from.Step))
                    {
                        if (executor.memoEntities.TryGetValue(producer.EntityMemoBase + from.Port, out var entities))
                        {
                            entityLives.RecheckEntities(entities);
                            if (port.Type == "entity") decoded = entities;
                        }
                    }
                    else decoded = executor.ResolveInput(value, port);
                    // A non-nullable "one" output wired into a "many" input was validated above against its own
                    // (origin) cardinality; it is wrapped into the one-element collection the "many" input expects
                    // only now, after that validation has passed.
                    if (input.Wrap) value = RuntimeJson.From(new[] { value });
                }
            }
            else
            {
                var cell = executor.EventCell(pending.Event, input.EventCell);
                if (!cell.Carried) continue;
                // The admission checked the value against the trigger's own port and decoded it as this input's
                // handler reads it, so the cell is handed on as it is; what a handler of an earlier step can still
                // have changed — whether the entities it names live — is asked again. A handle, a resource, a value
                // widened into a collection, a promoted parameter and a control step's frame take the document road.
                if (input.EventCellDecoded && !input.Wrap && step.InputSlots[i] >= 0 && frame.Typed)
                {
                    if (cell.Kind == ValueKind.Entity)
                    {
                        if (cell.Reference is EntityReference single) entityLives.CheckEntity(single);
                        else if (cell.Reference is EntityReference[] many) for (var j = 0; j < many.Length; j++) entityLives.CheckEntity(many[j]);
                    }
                    frame.SetTyped(step.InputSlots[i], cell);
                    continue;
                }
                value = executor.EventCellJson(pending.Event, input.EventCell);
                // A handle is moved, not decoded: only the step that consumes it checks it, so a value passing
                // through several steps is validated exactly once.
                if (dispatched && port.Type == "handle" && !(StopsSound(step) && input.Name == "instance"))
                {
                    if (kernelEffects.HandleRefusal(ReadHandle(value), port.HandleKind, port.Lifetime, out _) is { } refusal)
                        throw new RuntimeContractException(refusal, step.NodeId + "." + input.Name);
                }
                else decoded = executor.ResolveInput(value, port);
                if (input.Wrap) value = RuntimeJson.From(new[] { value });
            }
            // A promoted value stays a compiled index until the re-validation below runs on indices throughout; a
            // genuine input has no further index-based check, so it resolves now.
            var slot = step.InputSlots[i];
            var handlerValue = port.HandlerValue(value);
            var cached = step.InputConstantValues[i];
            if (slot < 0)
            {
                frame.SetParameter(-1 - slot, handlerValue, value, step.HandlerParameterKinds[-1 - slot], decoded,
                    input.ResourceResolver, cached);
            }
            else frame.Set(slot, handlerValue, port, decoded, input.ResourceResolver, cached, input.Wrap, value);
        }
        frame.ValidatePromoted(step.PromotedParameterRules);
        return frame;
    }

    /// <summary>The input object a trace record carries, written only where a trace observer will read it.</summary>
    private JsonElement? TraceFrame(StepInputs? inputs) => behaviorTraceObservers.Count == 0 || inputs is not { } frame ? null : frame.Json;

    /// <summary>
    /// The inputs one step hands its handler, by the step's own input order: the values the walk resolved, held as
    /// they are. The JSON object a handler or a boundary reads is written from them the first time one asks for it —
    /// the same object, field for field, the walk used to serialize for every step — so a handler that never reads
    /// its inputs as a document costs no document.
    /// </summary>
    internal sealed class StepInputs
    {
        /// <summary>Set while a step's boundary frame is lent to one synchronous read.</summary>
        internal bool Leased;
        internal StepInputs Reset()
        {
            if (values != null) Array.Clear(values, 0, values.Length);
            only = second = default; json = parameters = wireParameters = default;
            return this;
        }
        internal JsonElement InputAt(int index, bool wire = false)
            => index < 0 ? default : wire ? JsonCell(index).WireJson : JsonCell(index).Json;
        /// <summary>The input names and their physical offsets.</summary>
        private readonly string[] names;
        private readonly int[]? offsets;
        private readonly int[]? denseMap;
        /// <summary>Dynamic input cells followed only by promoted parameter overrides.</summary>
        private readonly CommandValue[]? values;
        private CommandValue only, second;
        private readonly int inputCount;
        private readonly int cellCount;
        private readonly CommandValue[]? parameterValues;
        private readonly int[]? parameterOverrideMap;
        private readonly string[] parameterNames;
        private readonly int[] parameterJsonOrder;
        private readonly JsonElement staticParameters;
        private readonly JsonElement staticWireParameters;
        private readonly bool typed;
        /// <summary>Whether the frame's cells are decoded handler values (an action's, a query's) rather than the raw
        /// values a control step moves.</summary>
        internal bool Typed => typed;
        /// <summary>The written object: undefined until something asks for it, or the value handed over.</summary>
        private JsonElement json;
        private readonly bool[]? presentationConstants;
        private JsonElement parameters;
        private JsonElement wireParameters;

        internal EntityCheck[] BindEntityLiveness(RuntimeKernel kernel, bool validate = true)
        {
            var live = new List<EntityCheck>();
            for (var i = 0; i < cellCount; i++)
            {
                var value = At(i);
                if (value.Kind != ValueKind.Entity) continue;
                if (value.Reference is EntityReference single) Bind(single);
                else if (value.Reference is EntityReference[] many)
                    for (var j = 0; j < many.Length; j++) Bind(many[j]);
            }
            return live.Count == 0 ? Array.Empty<EntityCheck>() : live.ToArray();

            void Bind(EntityReference entity)
            {
                if (validate) { kernel.entityLives.CheckEntity(entity, live); return; }
                RuntimeJson.Require(kernel.entityLives.TryEntityCheck(entity, out var check), "entity-resolver", entity.Id);
                live.Add(check);
            }
        }

        internal void RecheckReferences(RuntimeKernel kernel)
        {
            for (var i = 0; i < cellCount; i++)
            {
                var value = At(i);
                if (value.Kind == ValueKind.Entity)
                {
                    if (value.Reference is EntityReference single) kernel.entityLives.CheckEntity(single);
                    else if (value.Reference is EntityReference[] many)
                        for (var j = 0; j < many.Length; j++) kernel.entityLives.CheckEntity(many[j]);
                    continue;
                }
                RecheckResource(kernel, value);
            }
        }

        /// <summary>An effect frame reuses the resolver delegates captured when the layer was admitted. Only
        /// resource ownership remains on the frame itself; its resolver was already captured in each value cell.</summary>
        internal void RecheckReferences(RuntimeKernel kernel, EntityCheck[] entityLiveness)
        {
            kernel.entityLives.RecheckEntities(entityLiveness);
            for (var i = 0; i < cellCount; i++) RecheckResource(kernel, At(i));
        }

        /// <summary>A resource cell is still the resource it was admitted as only while its captured resolver answers
        /// for it; a resolver that throws is logged and read as no answer. Any other cell passes.</summary>
        private static void RecheckResource(RuntimeKernel kernel, CommandValue value)
        {
            if (value.Kind != ValueKind.Resource || value.Reference is not ResourceRef resource) return;
            ResourceRef? current;
            try { current = value.ResourceResolver?.Invoke(resource.ResourceId); }
            catch (Exception error)
            {
                kernel.LogCallbackFailed("resource-resolver-failed kind=" + resource.ResourceKind + " id=" + resource.ResourceId, error);
                current = null;
            }
            if (current == null) throw new RuntimeContractException(RuntimeAbiCodes.StaleResource, resource.ResourceKind + " " + resource.ResourceId);
        }
        internal StepInputs Snapshot() => new(this);

        private StepInputs(StepInputs source)
        {
            names = source.names; offsets = source.offsets; denseMap = source.denseMap;
            inputCount = source.inputCount; cellCount = source.cellCount; parameterValues = source.parameterValues;
            parameterOverrideMap = source.parameterOverrideMap; parameterNames = source.parameterNames;
            parameterJsonOrder = source.parameterJsonOrder; staticParameters = source.staticParameters;
            staticWireParameters = source.staticWireParameters; typed = source.typed;
            presentationConstants = source.presentationConstants;
            values = source.values == null ? null : (CommandValue[])source.values.Clone();
            only = source.only; second = source.second; json = source.json;
            parameters = source.parameters;
            wireParameters = source.wireParameters;
            // A row cell names the activation's own region, which the next activation reuses: a frame kept past its
            // activation keeps the rows as the document they are at this moment.
            for (var slot = 0; slot < cellCount; slot++)
                if (At(slot).Kind == ValueKind.Result) JsonCell(slot);
        }

        /// <summary>One input cell by its dense index, as it was published; an empty cell for no index.</summary>
        internal CommandValue CellAt(int index) => index < 0 || index >= cellCount ? default : At(index);

        /// <summary>How many explicit recipients one input carries, without writing its document: the element count
        /// of a collection (<paramref name="set"/>), one for a single value, and false where the input carries nothing.</summary>
        internal bool TryRecipientCount(int index, out int count, out bool set)
        {
            count = 0; set = false;
            if (index < 0 || index >= cellCount) return false;
            var cell = At(index);
            if (cell.Json.ValueKind != JsonValueKind.Undefined)
            {
                set = cell.Json.ValueKind == JsonValueKind.Array;
                count = set ? cell.Json.GetArrayLength() : 1;
                return true;
            }
            if (!cell.Typed || cell.Kind == ValueKind.Missing) return false;
            set = cell.Count >= 0;
            count = set ? cell.Count : 1;
            return true;
        }

        internal StepInputs(ResolvedStep step)
        {
            names = step.HandlerInputs;
            offsets = step.HandlerInputOffsets;
            denseMap = step.HandlerInputDenseMap;
            inputCount = step.HandlerInputCount;
            parameterValues = step.ParameterValues;
            parameterOverrideMap = step.ParameterOverrideMap;
            parameterNames = step.HandlerParameterNames;
            parameterJsonOrder = step.HandlerParameterJsonOrder;
            staticParameters = step.HandlerParameters;
            staticWireParameters = step.Parameters;
            typed = step.NodeKind is "action" or "query" or "pure";
            if (step.Execution == "presentation") presentationConstants = step.PresentationConstants;
            cellCount = inputCount + step.ParameterOverrideCount;
            if (cellCount > 2) values = new CommandValue[cellCount];
        }

        /// <summary>Creates the same physical cell storage from already decoded values for SDK test contexts.</summary>
        internal StepInputs(string[] names, int[] offsets, int[] denseMap, CommandValue[] inputs,
            CommandValue[] parameters)
        {
            this.names = names; this.offsets = offsets; this.denseMap = denseMap;
            inputCount = inputs.Length;
            cellCount = inputCount;
            values = inputs.Length > 2 ? inputs : null;
            only = inputs.Length > 0 ? inputs[0] : default;
            second = inputs.Length > 1 ? inputs[1] : default;
            parameterValues = parameters;
            parameterOverrideMap = Enumerable.Repeat(-1, parameters.Length).ToArray();
            parameterNames = Array.Empty<string>();
            parameterJsonOrder = Array.Empty<int>();
            staticParameters = staticWireParameters = RuntimeJson.EmptyObject;
            typed = true;
        }

        /// <summary>One input cell. <paramref name="port"/> is the wired origin's view, so a single value the walk
        /// wrapped for a collection input is read as the collection it now is (<paramref name="wrapped"/>).</summary>
        internal void Set(int slot, JsonElement value, PortView port, object? decoded,
            Func<string, ResourceRef?>? resourceResolver = null, CommandValue cached = default, bool wrapped = false,
            JsonElement wireValue = default)
        {
            var cell = typed ? cached.Json.ValueKind != JsonValueKind.Undefined ? cached
                : CommandValue.Read(value, port.HandlerKind, port.Many || wrapped, decoded, resourceResolver)
                : CommandValue.Raw(value, decoded as ResourceRef);
            SetCell(slot, wireValue.ValueKind == JsonValueKind.Undefined ? cell : cell.WithWire(wireValue));
        }

        /// <summary>The owner-resolved resource reference an input carries, for a control step that moves it.</summary>
        internal ResourceRef? ResolvedAt(int index) => index < 0 ? null : At(index).Reference as ResourceRef;

        internal void SetParameter(int slot, JsonElement value, JsonElement wireValue, ValueKind kind,
            object? decoded = null, Func<string, ResourceRef?>? resourceResolver = null,
            CommandValue cached = default)
        {
            var mapped = parameterOverrideMap![slot];
            if (mapped < 0) throw new InvalidOperationException("A non-promoted parameter cannot be overwritten.");
            SetCell(inputCount + mapped, typed ? cached.Json.ValueKind != JsonValueKind.Undefined ? cached
                : CommandValue.Read(value, kind, many: false, decoded, resourceResolver).WithWire(wireValue)
                : CommandValue.Raw(value).WithWire(wireValue));
        }

        internal void SetParameterDocuments(JsonElement handler, JsonElement wire)
        {
            parameters = handler;
            if (wire.ValueKind != JsonValueKind.Undefined) wireParameters = wire;
        }

        internal void ValidatePromoted(PromotedParameterRule[] rules)
        {
            for (var index = 0; index < rules.Length; index++)
            {
                var rule = rules[index];
                rule.Validate(ParameterAt(rule.Slot).WireJson);
            }
        }

        private CommandValue At(int slot) => values == null ? slot == 0 ? only : second : values[slot];
        /// <summary>One cell with its JSON written: a value an evaluator handed over decoded is written the first time
        /// a reader asks for its document, and kept, so a frame no reader asks that of writes none.</summary>
        private CommandValue JsonCell(int slot)
        {
            var cell = At(slot);
            if (slot < 0 || cell.Json.ValueKind != JsonValueKind.Undefined || !cell.Typed) return cell;
            cell = cell.Rendered();
            SetCell(slot, cell);
            return cell;
        }
        /// <summary>One input cell exactly as a producer published it, already checked against the producer's port.</summary>
        internal void SetTyped(int slot, CommandValue cell) => SetCell(slot, cell);
        private void SetCell(int slot, CommandValue value)
        {
            if (values == null)
            {
                if (slot == 0) only = value;
                else second = value;
            }
            else values[slot] = value;
        }
        private CommandValue ParameterAt(int slot)
        {
            var mapped = parameterOverrideMap![slot];
            return mapped >= 0 ? At(inputCount + mapped) : parameterValues![slot];
        }
        private int DenseInput(int physical)
            => physical >= 0 && denseMap != null && physical < denseMap.Length ? denseMap[physical] : -1;

        /// <summary>One input by name, or false where the step's wiring carried none under it.</summary>
        internal bool TryGet(string name, out JsonElement value)
        {
            for (var i = 0; i < names.Length; i++)
            {
                var candidate = JsonCell(DenseInput(offsets![i])).Json;
                if (candidate.ValueKind != JsonValueKind.Undefined && string.Equals(names[i], name, StringComparison.Ordinal))
                { value = candidate; return true; }
            }
            value = default;
            return false;
        }

        internal int PortCount => names.Length;

        /// <summary>The input cell at one wiring position, in the order <see cref="Json"/> lists them.</summary>
        internal bool TryCellAt(int position, out CommandValue cell)
        {
            var slot = DenseInput(offsets![position]);
            cell = slot < 0 ? default : At(slot);
            return cell.Carried;
        }

        /// <summary>One input cell by name, as it was published, without writing any document.</summary>
        internal bool TryCell(string name, out CommandValue cell)
        {
            for (var i = 0; i < names.Length; i++)
                if (string.Equals(names[i], name, StringComparison.Ordinal))
                {
                    var slot = DenseInput(offsets![i]);
                    cell = slot < 0 ? default : At(slot);
                    return cell.Carried;
                }
            cell = default;
            return false;
        }

        /// <summary>One parameter cell by name, without writing the parameter document.</summary>
        internal bool TryParameterCell(string name, out CommandValue cell)
        {
            var count = parameterOverrideMap == null || parameterValues == null ? 0 : Math.Min(parameterNames.Length, parameterValues.Length);
            for (var i = 0; i < count; i++)
                if (string.Equals(parameterNames[i], name, StringComparison.Ordinal))
                {
                    cell = ParameterAt(i);
                    return cell.Carried;
                }
            cell = default;
            return false;
        }

        internal JsonElement Parameters => ParameterDocument(wire: false);
        internal JsonElement WireParameters => ParameterDocument(wire: true);
        internal bool ParameterDocumentsMaterialized
            => parameters.ValueKind != JsonValueKind.Undefined || wireParameters.ValueKind != JsonValueKind.Undefined;

        private JsonElement ParameterDocument(bool wire)
        {
            ref var cached = ref (wire ? ref wireParameters : ref parameters);
            if (cached.ValueKind != JsonValueKind.Undefined) return cached;
            if (parameterOverrideMap == null || !parameterOverrideMap.Any(static value => value >= 0))
                return cached = wire ? staticWireParameters : staticParameters;
            var fields = new Dictionary<string, JsonElement>(parameterNames.Length, StringComparer.Ordinal);
            for (var index = 0; index < parameterJsonOrder.Length; index++)
            {
                var parameter = parameterJsonOrder[index];
                var value = ParameterAt(parameter);
                var jsonValue = wire ? value.WireJson : value.Json;
                if (jsonValue.ValueKind != JsonValueKind.Undefined) fields.Add(parameterNames[parameter], jsonValue);
            }
            return cached = fields.Count == 0 ? RuntimeJson.EmptyObject : RuntimeJson.From(fields);
        }

        internal ValueKind Kind(HandlerSlot slot, int index = -1)
        {
            if (!TryCell(slot, index, out var value, out _)) return ValueKind.Missing;
            return value.Kind;
        }

        internal bool Present(HandlerSlot slot)
        {
            if (!TryCell(slot, -1, out var value, out _)) return false;
            return value.Json.ValueKind == JsonValueKind.Undefined ? value.Typed && value.Kind != ValueKind.Null
                : value.Json.ValueKind != JsonValueKind.Null;
        }

        internal int Count(HandlerSlot slot)
        {
            if (slot.IsGroup) return GroupCount(slot);
            return TryCell(slot, -1, out var value, out _) && value.Valid ? value.Count : -1;
        }

        internal bool? Boolean(HandlerSlot slot, int index = -1)
        {
            if (!TryCell(slot, index, out var value, out var element) || !value.Valid || value.Kind != ValueKind.Boolean) return null;
            return element < 0 ? value.Count < 0 ? value.Boolean : null
                : value.Reference is bool[] set && element < set.Length ? set[element] : null;
        }

        internal string? Text(HandlerSlot slot, int index = -1)
        {
            if (!TryCell(slot, index, out var value, out var element) || !value.Valid || value.Kind != ValueKind.String) return null;
            return element < 0 ? value.Count < 0 ? value.Reference as string : null
                : value.Reference is string[] set && element < set.Length ? set[element] : null;
        }

        internal long? Integer(HandlerSlot slot, int index = -1)
        {
            if (!TryCell(slot, index, out var value, out var element) || !value.Valid) return null;
            if (value.Kind == ValueKind.Integer)
                return element < 0 ? value.Count < 0 ? value.Integer : null
                    : value.Reference is long[] set && element < set.Length ? set[element] : null;
            // A number port whose value is written as a whole number reads as an integer, exactly as the JSON
            // reader it replaces did; a fractional value is not one.
            if (value.Kind != ValueKind.Number) return null;
            if (element < 0 && value.Count < 0 && value.Json.ValueKind == JsonValueKind.Undefined)
                return value.Number == Math.Truncate(value.Number) && Math.Abs(value.Number) <= RuntimeJson.MaxSafeInteger ? (long)value.Number : null;
            if (element < 0) return value.Count < 0 && value.Json.TryGetInt64(out var whole) ? whole : null;
            return element < value.Count && value.Json[element].TryGetInt64(out var member) ? member : null;
        }

        internal double? Number(HandlerSlot slot, int index = -1)
        {
            if (!TryCell(slot, index, out var value, out var element) || !value.Valid) return null;
            if (value.Kind == ValueKind.Number)
                return element < 0 ? value.Count < 0 ? value.Number : null
                    : value.Reference is double[] set && element < set.Length ? set[element] : null;
            // An integer port widens to a number, as the JSON reader it replaces did.
            if (value.Kind != ValueKind.Integer) return null;
            return element < 0 ? value.Count < 0 ? value.Integer : null
                : value.Reference is long[] integers && element < integers.Length ? integers[element] : null;
        }

        internal (double X, double Y, double Z)? Vector(HandlerSlot slot, int index = -1)
        {
            if (!TryCell(slot, index, out var value, out var element) || !value.Valid || value.Kind != ValueKind.Vector3) return null;
            if (element < 0) return value.Count < 0 ? (value.Number, value.Y, value.Z) : null;
            return value.Reference is CommandVector[] set && element < set.Length
                ? (set[element].X, set[element].Y, set[element].Z) : null;
        }

        internal bool TryEntity(HandlerSlot slot, int index, out EntityReference entity)
        {
            if (TryCell(slot, index, out var value, out var element) && value.Valid && value.Kind == ValueKind.Entity)
            {
                if (element < 0 && value.Count < 0 && value.Reference is EntityReference single) { entity = single; return true; }
                if (element >= 0 && value.Reference is EntityReference[] set && element < set.Length) { entity = set[element]; return true; }
                if (element >= 0 && value.Reference is EntityCheck[] checkedSet && element < checkedSet.Length)
                { entity = checkedSet[element].Entity; return true; }
            }
            entity = null!; return false;
        }

        internal bool TryResource(HandlerSlot slot, out ResourceRef resource) => TryResource(slot, -1, out resource);

        /// <summary>One reference of a resource input, or one element of a resource collection when
        /// <paramref name="index"/> is not negative.</summary>
        internal bool TryResource(HandlerSlot slot, int index, out ResourceRef resource)
        {
            if (TryCell(slot, index, out var value, out var element) && value.Valid && value.Kind == ValueKind.Resource)
            {
                if (element < 0 && value.Count < 0 && value.Reference is ResourceRef read) { resource = read; return true; }
                if (element >= 0 && value.Reference is ResourceRef[] set && element < set.Length) { resource = set[element]; return true; }
            }
            resource = null!; return false;
        }

        /// <summary>A resource input's two wire fields, including a reference that is not a valid one (an unknown
        /// kind, an empty id), so a handler can refuse it with its own code and still echo it as its row target. A
        /// decoded reference is read without a lookup; only a cell that failed decoding reads its own object.</summary>
        internal bool TryResourceFields(HandlerSlot slot, out string kind, out string id)
        {
            if (!TryCell(slot, -1, out var value, out _)) { kind = id = ""; return false; }
            if (value.Valid && value.Kind == ValueKind.Resource && value.Reference is ResourceRef read)
            { kind = read.ResourceKind; id = read.ResourceId; return true; }
            var wire = value.Json;
            if (wire.ValueKind == JsonValueKind.Object && wire.TryGetProperty("resourceKind", out var kindField)
                && kindField.ValueKind == JsonValueKind.String)
            {
                kind = kindField.GetString() ?? "";
                id = wire.TryGetProperty("resourceId", out var idField) && idField.ValueKind == JsonValueKind.String
                    ? idField.GetString() ?? "" : "";
                return true;
            }
            kind = id = ""; return false;
        }

        internal bool TryHandle(HandlerSlot slot, int index, out JsonElement handle)
        {
            if (TryCell(slot, index, out var value, out var element) && value.Valid && value.Kind == ValueKind.Handle)
            {
                if (element < 0 && value.Count < 0) { handle = value.Json; return true; }
                if (element >= 0 && element < value.Count) { handle = value.Json[element]; return true; }
            }
            handle = default; return false;
        }

        internal bool TryHandle(HandlerSlot slot, int index, out RuntimeHandle handle)
        {
            handle = default;
            return TryHandle(slot, index, out JsonElement wire) && RuntimeHandle.TryRead(wire, out handle);
        }

        private bool TryCell(HandlerSlot slot, int index, out CommandValue value, out int element)
        {
            if (!slot.Resolved) throw new InvalidOperationException("Handler slot was used before its shape was registered: " + slot.Name);
            element = -1;
            var at = slot.Source == HandlerSlot.Sources.Parameter ? -1 : slot.Offset;
            if (slot.IsGroup)
            {
                if (index < 0 || index >= GroupCount(slot)) { value = default; return false; }
                at += index * slot.Stride;
            }
            else
            {
                foreach (var adjustment in slot.OffsetAdjustments)
                    at += (ParameterCount(adjustment.CountParameter, adjustment.MinimumCount) - adjustment.MinimumCount)
                        * adjustment.Stride;
                if (index >= 0) element = index;
            }
            if (slot.Source == HandlerSlot.Sources.Parameter)
            {
                if (slot.Offset < 0 || parameterValues == null || slot.Offset >= parameterValues.Length)
                    throw new InvalidOperationException("Handler parameter is outside this command frame: " + slot.Name);
                value = ParameterAt(slot.Offset);
                return true;
            }
            var dense = DenseInput(at);
            if (dense < 0 || dense >= inputCount)
                throw new InvalidOperationException("Handler slot is outside this command frame: " + slot.Name);
            value = At(dense);
            return true;
        }

        private int GroupCount(HandlerSlot slot) => ParameterCount(slot.CountParameter, slot.MinimumCount);

        private int ParameterCount(int parameter, int minimum)
        {
            if (parameter >= 0 && parameterValues != null && parameter < parameterValues.Length)
            {
                var value = ParameterAt(parameter);
                if (value.Valid && value.Kind == ValueKind.Integer && value.Integer >= minimum && value.Integer <= int.MaxValue)
                    return (int)value.Integer;
            }
            return minimum;
        }

        /// <summary>The inputs a presentation command carries, written as members of the object the caller has open: every
        /// carried input under its own name, in wiring order, except the constants the recipient's own loaded plan holds.</summary>
        internal void WritePresentationInputs(Utf8JsonWriter writer)
        {
            for (var i = 0; i < names.Length; i++)
            {
                var slot = DenseInput(offsets![i]);
                if (presentationConstants != null && presentationConstants[slot]) continue;
                var value = At(slot);
                if (!value.Carried) continue;
                writer.WritePropertyName(names[i]);
                // The receiving machine reads an enum as its member index, which is the wire spelling.
                value.WriteValue(writer, wire: true);
            }
        }

        internal JsonElement Json
        {
            get
            {
                if (json.ValueKind != JsonValueKind.Undefined) return json;
                if (names.Length == 0) return json = RuntimeJson.EmptyObject;
                return json = RuntimeJson.Write(this, static (writer, frame) =>
                {
                    writer.WriteStartObject();
                    for (var i = 0; i < frame.names.Length; i++)
                    {
                        var value = frame.At(frame.DenseInput(frame.offsets![i]));
                        if (!value.Carried) continue;
                        writer.WritePropertyName(frame.names[i]);
                        value.WriteValue(writer);
                    }
                    writer.WriteEndObject();
                });
            }
        }
    }
}

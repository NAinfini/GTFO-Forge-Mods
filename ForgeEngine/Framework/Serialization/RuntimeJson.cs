using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeEngine.Framework;

public static class RuntimeJson
{
    public const long MaxSafeInteger = 9007199254740991;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = Wire.PlanJson.MaxJsonDepth };
    public static JsonElement EmptyObject { get; } = Parse("{}");
    /// <summary>One published payload, port by port. A port whose value the native side could not read is left out
    /// instead of being published as a JSON null: an absent port is how this framework says "not observable", where a
    /// null would claim the port was written.</summary>
    public static JsonElement Payload(params (string Id, JsonElement? Value)[] ports)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, value) in ports) if (value is { } present) payload[id] = present;
        return From(payload);
    }

    /// <summary>One required JSON integer member inside <paramref name="minimum"/>..<paramref name="maximum"/>; false
    /// when it is absent, not a number, not an integer, or out of range.</summary>
    public static bool TryInteger(JsonElement row, string field, long minimum, long maximum, out long value)
    {
        value = 0;
        if (!row.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var parsed) || parsed < minimum || parsed > maximum) return false;
        value = parsed;
        return true;
    }

    /// <summary>A plain decimal number of up to <see cref="uint.MaxValue"/> with no sign and no leading zero. The
    /// accumulator is bounded before it can overflow, so an over-long number is refused rather than wrapped.</summary>
    public static bool TryDecimal(string text, out long value)
    {
        value = 0;
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0')) return false;
        foreach (var digit in text)
        {
            if (digit is < '0' or > '9') return false;
            value = value * 10 + (digit - '0');
            if (value > uint.MaxValue) return false;
        }
        return true;
    }
    /// <summary>The value as one canonical text: object members in ordinal key order, everything else as written, so two
    /// documents that differ only in member order compare equal.</summary>
    public static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(row => row.Name, StringComparer.Ordinal)
            .Select(row => JsonSerializer.Serialize(row.Name) + ":" + Canonical(row.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        _ => value.GetRawText()
    };
    public static JsonElement Parse(string json)
    {
        Require(json != null && Encoding.UTF8.GetByteCount(json) <= Wire.PackageLimits.MaximumPlanBytes, "json-size", "JSON exceeds 4 MiB.");
        try
        {
            using var document = JsonDocument.Parse(json!, new JsonDocumentOptions { MaxDepth = Wire.PlanJson.MaxJsonDepth });
            ValidateJson(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (JsonException ex) { throw new RuntimeContractException("invalid-json", ex.Message); }
    }
    /// <summary>
    /// One value as the runtime's JSON: the serializer's own text for it, read back as an element no caller can
    /// dispose. The bytes are exactly the ones <c>JsonSerializer.SerializeToElement</c> writes with the same options
    /// — the same serializer writes them into the same kind of writer — and only what holds them differs: the
    /// element owns a copy of its text, so a kernel boundary that snapshots it keeps it as it is instead of copying
    /// it again. One writer and one buffer per thread do the writing; a converter that serializes through this
    /// method while it is already writing gets a writer of its own.
    /// </summary>
    public static JsonElement From(object? value)
    {
        if (fromBusy) return FromFresh(value);
        fromBusy = true;
        try
        {
            var buffer = fromBuffer ??= new ArrayBufferWriter<byte>(256);
            buffer.Clear();
            var writer = fromWriter ??= new Utf8JsonWriter(buffer, WriterOptions);
            writer.Reset(buffer);
            JsonSerializer.Serialize(writer, value, Options);
            writer.Flush();
            return Owned(buffer.WrittenMemory);
        }
        finally { fromBusy = false; }
    }
    private static JsonElement FromFresh(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) JsonSerializer.Serialize(writer, value, Options);
        return Owned(buffer.WrittenMemory);
    }
    /// <summary>Writes the common action envelope directly, without an intermediate row document or dictionary.</summary>
    internal static JsonElement WriteResultRows(object rows, (string Port, JsonElement Value)? beside = null)
    {
        if (fromBusy)
        {
            var nestedBuffer = new ArrayBufferWriter<byte>(256);
            using var nestedWriter = new Utf8JsonWriter(nestedBuffer, WriterOptions);
            WriteRows(nestedWriter, rows, beside);
            nestedWriter.Flush();
            return Owned(nestedBuffer.WrittenMemory);
        }
        fromBusy = true;
        try
        {
            var buffer = fromBuffer ??= new ArrayBufferWriter<byte>(256);
            buffer.Clear();
            var writer = fromWriter ??= new Utf8JsonWriter(buffer, WriterOptions);
            writer.Reset(buffer);
            WriteRows(writer, rows, beside);
            writer.Flush();
            return Owned(buffer.WrittenMemory);
        }
        finally { fromBusy = false; }
    }

    /// <summary>One document the caller writes token by token, on the same per-thread writer and buffer
    /// <see cref="From"/> uses: no intermediate object graph, dictionary or reflection pass is built for it.</summary>
    internal static JsonElement Write<TState>(TState state, Action<Utf8JsonWriter, TState> body)
    {
        if (fromBusy)
        {
            var nestedBuffer = new ArrayBufferWriter<byte>(256);
            using var nestedWriter = new Utf8JsonWriter(nestedBuffer, WriterOptions);
            body(nestedWriter, state);
            nestedWriter.Flush();
            return Owned(nestedBuffer.WrittenMemory);
        }
        fromBusy = true;
        try
        {
            var buffer = fromBuffer ??= new ArrayBufferWriter<byte>(256);
            buffer.Clear();
            var writer = fromWriter ??= new Utf8JsonWriter(buffer, WriterOptions);
            writer.Reset(buffer);
            body(writer, state);
            writer.Flush();
            return Owned(buffer.WrittenMemory);
        }
        finally { fromBusy = false; }
    }

    private static void WriteRows(Utf8JsonWriter writer, object rows, (string Port, JsonElement Value)? beside)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(ResultRows.Port);
        // Match the envelope's existing last-write behavior if the extra port is the result port itself.
        if (beside is { Port: ResultRows.Port } replacement) replacement.Value.WriteTo(writer);
        else
        {
            JsonSerializer.Serialize(writer, rows, Options);
            if (beside is { } other)
            {
                writer.WritePropertyName(other.Port);
                other.Value.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }

    private static JsonElement Owned(ReadOnlyMemory<byte> written)
    {
        using var document = JsonDocument.Parse(written);
        return document.RootElement.Clone();
    }
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = Options.Encoder, Indented = false, SkipValidation = true };
    [ThreadStatic] private static ArrayBufferWriter<byte>? fromBuffer;
    [ThreadStatic] private static Utf8JsonWriter? fromWriter;
    [ThreadStatic] private static bool fromBusy;
    /// <summary>One JSON document the runtime itself wrote, read back as an independent element. The bytes are a
    /// rendering of values that already passed their own validation, so only the reader's own bounds apply here;
    /// foreign text still goes through <see cref="Parse"/>.</summary>
    internal static JsonElement ParseWritten(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = Wire.PlanJson.MaxJsonDepth });
            return document.RootElement.Clone();
        }
        catch (JsonException ex) { throw new RuntimeContractException("invalid-json", ex.Message); }
    }
    /// <summary>One text as the JSON writer spells it, without the surrounding quotes. The encoder is the one the
    /// whole-document spelling uses, so a text composed from quoted pieces is character for character the text
    /// `StableText` produced for the same array — which is what lets an identity be assembled from parts computed
    /// once instead of being serialized again on every dispatch.</summary>
    internal static string Quote(string text)
    {
        // A text of characters the default encoder writes as themselves is spelled as itself between two quotes;
        // every other text is spelled by the encoder itself.
        if (Plain(text)) return string.Concat("\"", text, "\"");
        var raw = From(new[] { text }).GetRawText(); return raw.Substring(1, raw.Length - 2);
    }
    /// <summary>Whether the default JSON encoder writes every character of one text unchanged: printable ASCII
    /// other than the characters it escapes (<c>" \ &lt; &gt; &amp; ' + `</c>).</summary>
    internal static bool Plain(string text)
    {
        foreach (var c in text)
            if (c < 0x20 || c > 0x7E || c is '"' or '\\' or '<' or '>' or '&' or '\'' or '+' or '`') return false;
        return true;
    }
    internal static string StableText(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteStable(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static void WriteStable(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            var count = 0;
            foreach (var property in value.EnumerateObject()) count++;
            var properties = ArrayPool<StableProperty>.Shared.Rent(count);
            try
            {
                var at = 0;
                foreach (var property in value.EnumerateObject())
                    properties[at] = new StableProperty(property.Name, property.Value, at++);
                Array.Sort(properties, 0, count, StablePropertyOrder);
                for (var i = 0; i < count; i++)
                { writer.WritePropertyName(properties[i].Name); WriteStable(writer, properties[i].Value); }
            }
            finally { ArrayPool<StableProperty>.Shared.Return(properties, clearArray: true); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteStable(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    private readonly record struct StableProperty(string Name, JsonElement Value, int Order);
    private static readonly IComparer<StableProperty> StablePropertyOrder = Comparer<StableProperty>.Create((left, right) =>
    {
        var name = string.CompareOrdinal(left.Name, right.Name);
        return name == 0 ? left.Order.CompareTo(right.Order) : name;
    });
    [ThreadStatic] private static ArrayBufferWriter<byte>? stableBuffer;
    [ThreadStatic] private static Utf8JsonWriter? stableWriter;
    private static Utf8JsonWriter BeginStableBytes()
    {
        var buffer = stableBuffer ??= new ArrayBufferWriter<byte>(256);
        buffer.Clear();
        var writer = stableWriter ??= new Utf8JsonWriter(buffer);
        writer.Reset(buffer);
        return writer;
    }
    // These writers traverse already built JSON only, so they cannot invoke a converter or re-enter themselves.
    internal static ReadOnlySpan<byte> StableBytes(JsonElement value)
    {
        var writer = BeginStableBytes();
        WriteStable(writer, value);
        writer.Flush();
        return stableBuffer!.WrittenSpan;
    }
    internal static ReadOnlySpan<byte> StableEventBytes(RuntimeEvent value)
    {
        var writer = BeginStableBytes();
        writer.WriteStartObject();
        writer.WriteString("bindingId", value.BindingId);
        writer.WriteNumber("causalDepth", value.CausalDepth);
        writer.WriteString("causeId", value.CauseId);
        writer.WriteString("eventId", value.EventId);
        writer.WritePropertyName("outputs");
        WriteStable(writer, value.Outputs);
        writer.WriteString("rootEventId", value.RootEventId);
        writer.WriteString("scopeId", value.ScopeId);
        writer.WriteNumber("simulationTick", value.SimulationTick);
        writer.WriteNumber("worldEpoch", value.WorldEpoch);
        writer.WriteEndObject();
        writer.Flush();
        return stableBuffer!.WrittenSpan;
    }
    internal static void Require(bool condition, string code, string message)
    { if (!condition) throw new RuntimeContractException(code, message); }
    internal static void Shape(JsonElement value, string required, string optional = "")
    {
        Require(value.ValueKind == JsonValueKind.Object, "object-required", "Expected JSON object.");
        var shape = Shapes.GetOrAdd((required, optional), static key =>
        {
            var keys = key.Required.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new FieldShape(keys, new HashSet<string>(keys.Concat(key.Optional.Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparer.Ordinal));
        });
        foreach (var property in value.EnumerateObject()) Require(shape.Allowed.Contains(property.Name), "unknown-field", property.Name);
        foreach (var key in shape.Required) Require(value.TryGetProperty(key, out _), "missing-field", key);
    }
    // The field lists are string literals at the call sites, so the split and the set are built once per distinct list.
    private sealed record FieldShape(string[] Required, HashSet<string> Allowed);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Required, string Optional), FieldShape> Shapes = new();
    /// <summary>The same rule against a field list that is already known: no split, no set built per call. The field
    /// list is the whole shape — there are no optional fields in this form — and the violation order is the one
    /// above, unknown field before missing field.</summary>
    internal static void Shape(JsonElement value, string[] fields)
    {
        Require(value.ValueKind == JsonValueKind.Object, "object-required", "Expected JSON object.");
        foreach (var property in value.EnumerateObject())
        {
            var known = false;
            foreach (var field in fields) if (property.NameEquals(field)) { known = true; break; }
            if (!known) throw new RuntimeContractException("unknown-field", property.Name);
        }
        foreach (var field in fields) Require(value.TryGetProperty(field, out _), "missing-field", field);
    }
    internal static string Text(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String, "string-required", "Expected string.");
        return Text(value.GetString()!);
    }
    internal const int MaxTextLength = 256;
    /// <summary>A command id is composed from four texts (provider, event, plan, node) plus its framing and an optional
    /// round suffix, so its bound is the four texts' bound, not one text's: an event id that carries a long object id
    /// already uses most of a single text's length, and the same step with a longer node name must not be refused for it.</summary>
    internal const int MaxCommandIdLength = 4 * MaxTextLength + 64;
    internal static string Text(string text) => Text(text, MaxTextLength);
    internal static string CommandId(string commandId) => Text(commandId, MaxCommandIdLength);
    private static string Text(string text, int maximum)
    {
        Require(text != null && text.Length > 0 && text.Length <= maximum && text.Trim() == text, "invalid-string",
            maximum == MaxTextLength ? "Text must contain 1..256 trimmed characters without controls."
                : "A command id must contain 1..1088 trimmed characters without controls.");
        foreach (var c in text!) Require(c > 31 && c != 127, "invalid-string", "Control character in text.");
        return text;
    }
    /// <summary>Whether <see cref="Text(string)"/> accepts one text.</summary>
    internal static bool IsText(string? text)
    {
        if (text == null || text.Length is 0 or > 256 || text.Trim() != text) return false;
        foreach (var c in text) if (c <= 31 || c == 127) return false;
        return true;
    }
    internal static long Integer(long value, long minimum = 0, long maximum = MaxSafeInteger)
    { Require(value >= minimum && value <= maximum, "invalid-integer", "Expected bounded safe integer."); return value; }
    internal static string Text(JsonElement value, string key) => Text(value.GetProperty(key));
    internal static string Id(JsonElement value, string key)
    {
        var text = Text(value, key); Require(IsId(text), "invalid-id", text); return text;
    }
    internal static bool IsId(string value) => IdPattern.IsMatch(value);
    private static readonly Regex IdPattern = new(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant);
    /// <summary>The namespace an entity reference is routed by: everything before its first colon. The kernel, the
    /// registry and the providers all split a reference this one way, so a kind is read, not re-derived — which is
    /// also why a game-independent contract that compares two references of its own provider reads it here.</summary>
    public static string KindOf(string entityId)
    {
        var split = entityId.IndexOf(':');
        return split > 0 ? entityId[..split] : "";
    }
    internal static string Version(JsonElement value, string key)
    {
        var text = Text(value, key);
        Require(VersionPattern.IsMatch(text), "invalid-version", text); return text;
    }
    internal static long Integer(JsonElement value, long minimum = 0, long maximum = MaxSafeInteger)
    {
        // The exact integer is read where the JSON carries one: a value past 2^53 is not a double any more, and a
        // whole-number check against a rounded double would quietly accept a number this side never received. A
        // value that is not a JSON integer at all still goes through the double path, which is what bounds it and
        // refuses a fraction, an infinity or a value outside the caller's range by name.
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var exact))
        {
            Require(exact >= minimum && exact <= maximum, "invalid-integer", "Expected bounded safe integer.");
            return exact;
        }
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n)
            && n == Math.Truncate(n) && n >= minimum && n <= maximum, "invalid-integer", "Expected bounded safe integer.");
        return (long)value.GetDouble();
    }
    internal static string[] Strings(JsonElement value, bool unique = true)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 2048, "array-required", "Expected bounded string array.");
        var result = new string[value.GetArrayLength()];
        var at = 0;
        foreach (var item in value.EnumerateArray()) result[at++] = Text(item);
        if (unique && result.Length > 1)
        {
            var seen = new HashSet<string>(result.Length, StringComparer.Ordinal);
            foreach (var text in result) Require(seen.Add(text), "duplicate-value", "Duplicate array entry.");
        }
        return result;
    }
    /// <summary>The elements of one JSON array in one exactly sized allocation; the enumerator does not report its
    /// length, so ToArray would grow a buffer in steps.</summary>
    internal static JsonElement[] Elements(JsonElement array)
    {
        var result = new JsonElement[array.GetArrayLength()];
        var at = 0;
        foreach (var item in array.EnumerateArray()) result[at++] = item;
        return result;
    }
    internal static JsonElement[] Rows(JsonElement value, string name, int maximum = 2048)
    {
        var rows = value.GetProperty(name);
        Require(rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() <= maximum, "invalid-array", name);
        return Elements(rows);
    }
    internal static void ExactSet(IEnumerable<string> actual, IEnumerable<string> expected, string code)
        => Require(actual.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)), code, "Exact set mismatch.");
    public static EntityReference Entity(JsonElement value)
    {
        Shape(value, "id worldEpoch lifeEpoch");
        return new EntityReference(Text(value, "id"), Integer(value.GetProperty("worldEpoch")), Integer(value.GetProperty("lifeEpoch")));
    }
    internal static readonly string[] EntityFields = { "id", "worldEpoch", "lifeEpoch" };
    /// <summary>One entity reference read on the kernel's own hot path, where the shape is already a fixed list: the
    /// same three fields and the same two violations, without rebuilding the field set for every reference of a
    /// candidate list.</summary>
    internal static EntityReference EntityRow(JsonElement value)
    {
        Shape(value, EntityFields);
        return new EntityReference(Text(value, "id"), Integer(value.GetProperty("worldEpoch")), Integer(value.GetProperty("lifeEpoch")));
    }
    internal static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { Require(keys.Add(property.Name), "duplicate-key", property.Name); ValidateJson(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidateJson(item);
        else if (value.ValueKind == JsonValueKind.Number)
            Require(value.TryGetDouble(out var n) && double.IsFinite(n), "invalid-number", "Non-finite JSON number.");
        else Require(value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null,
            "invalid-json", "Undefined JSON value.");
    }
    internal static bool Flag(JsonElement value, string key) => value.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True;
    /// <summary>
    /// One value against the port that will carry it. A port's own type decides the value's form, and the two
    /// reference kinds are the reason this is a boundary at all: a resource value is the kind and id a frame's two
    /// slots hold, and an event value is the row index of one dispatch's event. <paramref name="events"/> is the
    /// table those indices are counted in — null where no dispatch is running, which is also where no event value
    /// can be read.
    /// </summary>
    [ThreadStatic] private static HashSet<EntityReference>? distinctScratch;

    /// <summary>Refuses an entity set that names one entity twice (`duplicate-entity`), by <see cref="EntityReference"/>'s
    /// identity: id, world and life epoch. One insert per entity into a set sized for the 256-entity cap; up to eight
    /// inserted entries are removed one by one, more by Clear (which wipes every bucket, costlier than a small set's check).
    /// Staleness is not judged here.</summary>
    internal static void RequireDistinct(EntityReference[] refs)
    {
        if (refs.Length < 2) return;
        var scratch = distinctScratch ??= new HashSet<EntityReference>(256);
        var added = 0;
        while (added < refs.Length && scratch.Add(refs[added])) added++;
        if (added > 8) scratch.Clear();
        else for (var i = 0; i < added; i++) scratch.Remove(refs[i]);
        Require(added == refs.Length, "duplicate-entity", "Repeated recipients require explicit semantics.");
    }

    internal static void ValidateValue(JsonElement value, JsonElement port, RuntimeEventRows? events = null)
    {
        if (value.ValueKind == JsonValueKind.Null)
        { Require(Flag(port, "nullable"), "null-input", Text(port, "id")); return; }
        // A parameter-only type is validated as the value type the frame table carries it as.
        var type = RuntimeGraphContracts.FrameValueType(Text(port, "type"));
        // A collection is the same value repeated: only the kinds with an element form have a collection form,
        // and every element is validated exactly as a single value of that kind is. An entity set keeps its own
        // rule because its members are references a later step resolves, not plain values.
        if (RuntimeGraphContracts.Many(port))
        {
            if (!(RuntimeGraphContracts.Segmented(type))) throw new RuntimeContractException("unsupported-port", type + " many");
            if (!(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= Wire.MaxSetWidth)) throw new RuntimeContractException("invalid-array", type + " many");
            if (type != "entity")
            {
                var fields = port.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                fields["cardinality"] = From("one");
                var element = RuntimeJson.From(fields);
                foreach (var item in value.EnumerateArray()) ValidateValue(item, element, events);
                return;
            }
        }
        switch (type)
        {
            case "entity" when RuntimeGraphContracts.Many(port):
                Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= Wire.MaxSetWidth, "invalid-entities", "Entity set exceeds 256.");
                RequireDistinct(value.EnumerateArray().Select(Entity).ToArray()); break;
            case "entity": Entity(value); break;
            case "boolean": Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "invalid-boolean", "Expected bool."); break;
            case "integer": Integer(value, -MaxSafeInteger); break;
            case "number": Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n), "invalid-number", "Expected finite number."); break;
            case "string": Require(value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= Wire.PlanJson.MaxStringValueLength, "invalid-string", "Expected bounded string."); break;
            case "vector3":
                Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 3 && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n)), "invalid-vector", "Expected finite vector3."); break;
            case "enum":
                // Q3: the wire value is a member-set index, never the member name. A port always indexes its
                // whole named set (ports carry no inline subset), so the port's own schema is the index basis.
                var count = RuntimeGraphContracts.EnumSets[Text(port, "schema")].Length;
                Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var member) && double.IsFinite(member)
                    && member == Math.Truncate(member) && member >= 0 && member < count, "invalid-enum", "Expected enum member index."); break;
            case "handle": Handle(value); break;
            // A resource value is the two-field reference a frame carries in two slots, written out: the kind it
            // belongs to and the id its owner answers with. The port's own `resourceKind` is the authority on the
            // kind, so a value that names another one is refused here and never reaches a handler as a reference
            // the port did not declare. A plan's own compiled reference is not this value: it is written in the
            // document's `{id, revision}` form, translated by the loader, and validated there.
            case "resource":
                var resource = ResourceRefOf(value);
                Require(port.TryGetProperty("resourceKind", out var declared) && declared.GetString() == resource.ResourceKind,
                    RuntimeAbiCodes.ResourceKind, resource.ResourceId);
                break;
            // An event value is one dispatch's event row, written out as its index. Two things make it a value of
            // the port it arrives on and neither is a property of the number: the row has to exist in the table of
            // the dispatch that is reading it, and that row's own type has to be the event the port declares. A
            // row index of another dispatch, or one past the table, names no event and is refused rather than
            // resolved against whatever sits at that index.
            case "event":
                Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var index)
                    && double.IsFinite(index) && index == Math.Truncate(index) && index >= 0 && index < (events?.Count ?? 0),
                    RuntimeAbiCodes.EventPortKind, Text(port, "id"));
                Require(events!.Matches((long)value.GetDouble(), Text(port, "schema")), RuntimeAbiCodes.EventPortKind, Text(port, "id"));
                break;
            default: throw new RuntimeContractException("unsupported-port", type);
        }
    }
    /// <summary>One handle at the JSON dispatch boundary: the same four components the frame slot carries —
    /// the identity triple plus the registration index of the provider that created it. Nothing here says what
    /// the handle is worth; whether it is live, of the right kind and within its lifetime is decided by the
    /// kernel when the step that consumes it runs (`CheckHandle`).</summary>
    internal static void Handle(JsonElement value)
    {
        Shape(value, "worldEpoch lifeEpoch local provider");
        Integer(value.GetProperty("worldEpoch")); Integer(value.GetProperty("lifeEpoch"));
        Integer(value.GetProperty("local")); Integer(value.GetProperty("provider"));
    }
    /// <summary>One resource reference at the JSON boundary: the kind and the id, and nothing else. The shape is
    /// the frame's own two slots written out, so a resource value round-trips between a payload, a handler and a
    /// frame without a second spelling for any of them.</summary>
    public static ResourceRef ResourceRefOf(JsonElement value)
    {
        Shape(value, "resourceKind resourceId");
        return new ResourceRef(Text(value, "resourceKind"), Text(value, "resourceId"));
    }
    /// <summary>
    /// One resource reference as a compiled plan writes it: the document names the instance by its own id and the
    /// revision it was authored against, and the port it feeds names the kind. That is the shape the constant pool
    /// encoder reads into two slots, so it is also the shape every load-time check of a compiled reference reads
    /// here; the runtime value a handler sees afterwards is <see cref="ResourceRefOf"/>'s. The two fields are the
    /// document's whole identity and neither is optional — the same `{id, revision}` pair the website's own
    /// `validateResourceLiteral` requires and the same pair a plan's own `resource` carries.
    /// </summary>
    internal static string ResourceLiteral(JsonElement value)
    {
        Shape(value, "id revision");
        Text(value, "revision");
        return Text(value, "id");
    }
    /// <summary>
    /// The one translation between those two spellings, applied where a compiled reference becomes the input a
    /// handler reads: the port is the kind's authority, the literal is the instance's, and the result is exactly the
    /// value <see cref="ResourceRefOf"/> reads back. The revision was validated with the literal and belongs to the
    /// document, not to the runtime reference, so it is not carried over. A caller resolves the reference through
    /// its kind's owner before asking for this, and asks once per literal at load: nothing here re-reads a plan
    /// document, and dispatch never calls it.
    /// </summary>
    internal static JsonElement ResourceRuntimeValue(JsonElement literal, JsonElement port)
    {
        Require(port.TryGetProperty("resourceKind", out var declared) && declared.ValueKind == JsonValueKind.String,
            RuntimeAbiCodes.ResourceKind, Text(port, "id"));
        return From(new { resourceKind = declared.GetString()!, resourceId = ResourceLiteral(literal) });
    }
    internal static void Parameters(JsonElement parameters, JsonElement capability, IReadOnlySet<string>? promoted = null)
    {
        var definitions = Rows(capability.GetProperty("graph"), "parameters").Where(p => promoted == null || !promoted.Contains(Text(p, "id"))).ToArray();
        Shape(parameters, string.Join(" ", definitions.Where(p => Flag(p, "required")).Select(p => Text(p, "id"))),
            string.Join(" ", definitions.Where(p => !Flag(p, "required")).Select(p => Text(p, "id"))));
        foreach (var definition in definitions)
        {
            if (!parameters.TryGetProperty(Text(definition, "id"), out var value)) continue;
            ValidateParameter(value, definition);
        }
    }
    /// <summary>One compiled parameter value against its own definition: the type, the member set it indexes, and
    /// its declared bounds. Shared by the load-time frame encoder, which writes the value into the plan's constant
    /// pool, and by the dispatch-time re-validation of a value that arrived through a promoted input port.</summary>
    internal static void ValidateParameter(JsonElement value, JsonElement definition)
    {
        var type = Text(definition, "type");
        RuntimeJson.Require(type != "recipient-policy", "unsupported-parameter", "The runtime cannot evaluate recipient-policy.");
        if (type == "enum")
        {
            // Q3: the compiled/wire value is a member-set index, never the member name. Inline `values` narrow
            // the index basis to that list; a promotable enum always names a whole set instead.
            var members = RuntimeGraphContracts.EnumMembers(definition);
            Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var index) && double.IsFinite(index)
                && index == Math.Truncate(index) && index >= 0 && index < members.Length, "invalid-enum", Text(definition, "id"));
        }
        else ValidateValue(value, definition);
        if (value.ValueKind != JsonValueKind.Number) return;
        var number = value.GetDouble();
        if (definition.TryGetProperty("minimum", out var min)) Require(number >= min.GetDouble(), "parameter-minimum", Text(definition, "id"));
        if (definition.TryGetProperty("maximum", out var max)) Require(number <= max.GetDouble(), "parameter-maximum", Text(definition, "id"));
    }
    /// <summary>Handler boundary (Q3): a wired value already validated against `port` keeps its compiled index
    /// representation everywhere in the kernel; only here, right before a handler reads it, an enum index is
    /// resolved to its member name so existing handlers keep comparing strings.</summary>
    internal static JsonElement EnumPortToHandlerValue(JsonElement value, JsonElement port)
        => Text(port, "type") == "enum" && value.ValueKind == JsonValueKind.Number
            ? From(RuntimeGraphContracts.EnumSets[Text(port, "schema")][(int)value.GetDouble()]) : value;
    /// <summary>Handler boundary (Q3) for parameters: every enum-typed definition on `capability` (constants and,
    /// once merged back in, promoted values alike) is resolved from its compiled index to its member name.
    /// Called once, after every other parameter validation for this dispatch has already run on the indices.</summary>
    internal static JsonElement ResolveEnumParameters(JsonElement parameters, JsonElement capability)
    {
        List<JsonElement>? definitions = null;
        foreach (var p in Rows(capability.GetProperty("graph"), "parameters"))
            if (Text(p, "type") == "enum") (definitions ??= new()).Add(p);
        if (definitions == null) return parameters;
        var fields = parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var name = Text(definition, "id");
            if (fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number)
                fields[name] = From(RuntimeGraphContracts.EnumMembers(definition)[(int)value.GetDouble()]);
        }
        return From(fields);
    }
}

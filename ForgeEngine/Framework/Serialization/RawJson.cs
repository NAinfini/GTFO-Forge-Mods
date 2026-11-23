using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// The kernel's reading of a value's own UTF-8 text. A JSON value the kernel receives already exists as bytes inside
/// its document, and every question the boundary asks about it — how long it is, whether it is well formed within the
/// runtime's bounds, what its replay fingerprint is — is a question about those bytes. They are read in place: nothing
/// is transcoded to a string and nothing is parsed a second time.
///
/// The span comes from <c>JsonDocument.GetRawValue</c>, the same internal member <see cref="JsonElement.GetRawText"/>
/// transcodes from, bound once through an expression over the element's own <c>_parent</c>/<c>_idx</c>. That binding is
/// only a shortcut: the member set is the one System.Text.Json 6.0 ships with the game's runtime, and a runtime that
/// has others leaves it unbound (<see cref="FastPath"/> false), after which every answer comes from the public
/// <see cref="JsonElement.GetRawText"/> (the same bytes, at the price of a string and an array per question) and the
/// range of one value inside another is not known. Nothing fails and nothing reads the wrong bytes.
/// </summary>
internal static class RawJson
{
    private static readonly Func<JsonElement, ReadOnlyMemory<byte>>? RawValue;
    private static readonly Func<JsonElement, bool>? Disposable;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal const int MaximumDocumentBytes = Wire.PackageLimits.MaximumPlanBytes;

    static RawJson()
    {
        try
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            var parent = typeof(JsonElement).GetField("_parent", Private);
            var index = typeof(JsonElement).GetField("_idx", Private);
            var raw = typeof(JsonDocument).GetMethod("GetRawValue", Private, null, new[] { typeof(int), typeof(bool) }, null);
            var disposable = typeof(JsonDocument).GetProperty("IsDisposable", Private);
            if (parent == null || index == null || raw == null || raw.ReturnType != typeof(ReadOnlyMemory<byte>)
                || disposable == null || disposable.PropertyType != typeof(bool)) return;
            var element = Expression.Parameter(typeof(JsonElement), "element");
            var document = Expression.Field(element, parent);
            RawValue = Expression.Lambda<Func<JsonElement, ReadOnlyMemory<byte>>>(
                Expression.Call(document, raw, Expression.Field(element, index), Expression.Constant(true)), element).Compile();
            Disposable = Expression.Lambda<Func<JsonElement, bool>>(Expression.Property(document, disposable), element).Compile();
        }
        catch (Exception)
        {
            // Another System.Text.Json: the shortcut is left unbound and the public reads answer.
            RawValue = null;
            Disposable = null;
        }
    }

    /// <summary>True while the shortcut over System.Text.Json's internals is bound; false means every read goes through
    /// <see cref="JsonElement.GetRawText"/>.</summary>
    internal static bool FastPath => RawValue != null && Disposable != null;

    /// <summary>Whether <see cref="JsonElement.Clone"/> of this value answers the value itself: a value whose
    /// document no caller can dispose already is its own independent copy. Asked only of a defined value. Without
    /// the shortcut nothing is known, so the answer is no and the caller clones.</summary>
    internal static bool Owned(JsonElement value) => FastPath && !Disposable!(value);

    /// <summary>The UTF-8 text of a value this kernel already validated: no transcoding check, because the value
    /// passed one when it crossed the boundary.</summary>
    internal static ReadOnlySpan<byte> Bytes(JsonElement value)
        => FastPath ? RawValue!(value).Span : Encoding.UTF8.GetBytes(value.GetRawText());

    /// <summary>Where one value's text stands inside another's, or -1 where it is not part of it (and always -1 without
    /// the shortcut): two values of one document share its bytes, so a value inside a payload is a range of the
    /// payload's own text.</summary>
    internal static int OffsetIn(JsonElement outer, JsonElement inner)
    {
        if (!FastPath) return -1;
        var whole = Bytes(outer);
        var part = Bytes(inner);
        return whole.Overlaps(part, out var offset) && offset >= 0 && offset + part.Length <= whole.Length ? offset : -1;
    }

    /// <summary>
    /// The value's own UTF-8 text, exactly the bytes <see cref="JsonElement.GetRawText"/> transcodes. It fails where
    /// that read fails and with the same exception types: a default element is an <see cref="InvalidOperationException"/>,
    /// a disposed document an <see cref="ObjectDisposedException"/>, and bytes that are not UTF-8 the same
    /// <see cref="InvalidOperationException"/> the transcoding raises.
    /// </summary>
    internal static ReadOnlySpan<byte> Text(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("The requested operation requires an element of type 'Undefined'.");
        if (!FastPath) return Encoding.UTF8.GetBytes(value.GetRawText());
        var raw = RawValue!(value).Span;
        try { StrictUtf8.GetCharCount(raw); }
        catch (DecoderFallbackException error)
        { throw new InvalidOperationException("Cannot transcode invalid UTF-8 JSON text to UTF-16 string.", error); }
        return raw;
    }

    /// <summary>
    /// The checks <see cref="RuntimeJson.Parse"/> makes of a document, asked of text that is already one value: at most
    /// 4 MiB, a well-formed value at most 32 deep (<c>invalid-json</c>), no repeated key in any object
    /// (<c>duplicate-key</c>) and no number without a finite double (<c>invalid-number</c>). A structural failure outranks
    /// every other one, because the parse ran before the key and number walk did; the walk's own refusals come in
    /// document order, which is the order that walk visited them in.
    /// </summary>
    internal static void Validate(ReadOnlySpan<byte> raw)
    {
        RuntimeJson.Require(raw.Length <= MaximumDocumentBytes, "json-size", "JSON exceeds 4 MiB.");
        RuntimeContractException? first = null;
        var names = NameScratch ??= new NameTable();
        names.Reset();
        try
        {
            var reader = new Utf8JsonReader(raw, new JsonReaderOptions { MaxDepth = Wire.PlanJson.MaxJsonDepth });
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: names.Open(); break;
                    case JsonTokenType.EndObject: names.Close(); break;
                    case JsonTokenType.PropertyName:
                        if (first == null && !names.Add(raw, ref reader, out var repeated))
                            first = new RuntimeContractException("duplicate-key", repeated!);
                        break;
                    case JsonTokenType.Number:
                        if (first == null && !(reader.TryGetDouble(out var number) && double.IsFinite(number)))
                            first = new RuntimeContractException("invalid-number", "Non-finite JSON number.");
                        break;
                }
            }
        }
        catch (JsonException error) { throw new RuntimeContractException("invalid-json", error.Message); }
        if (first != null) throw first;
    }

    /// <summary>
    /// <c>RuntimeJson.Parse(value.GetRawText())</c> without the text: the same validation of the same bytes, and the
    /// value itself kept. <see cref="JsonElement.Clone"/> is what makes the kept value independent of a document the
    /// caller may still dispose, and it copies nothing where the document is already one no caller can dispose — which
    /// is every document the runtime's own writers produce.
    /// </summary>
    internal static JsonElement Snapshot(JsonElement value, ReadOnlySpan<byte> raw)
    {
        Validate(raw);
        return value.Clone();
    }

    /// <summary>The replay ledger's 128-bit mix over one text's UTF-16 code units, read from its UTF-8 bytes. The code
    /// units are the ones the transcoded string would hold, so the value is the one the string form of the same text
    /// mixes to.</summary>
    internal static void MixUtf8(ref ulong low, ref ulong high, ReadOnlySpan<byte> utf8)
    {
        Span<char> units = stackalloc char[2];
        while (!utf8.IsEmpty)
        {
            var first = utf8[0];
            if (first < 0x80)
            {
                low = (low ^ first) * 1099511628211UL; high = (high ^ first) * 14695981039346656037UL;
                utf8 = utf8[1..];
                continue;
            }
            Rune.DecodeFromUtf8(utf8, out var rune, out var consumed);
            var written = rune.EncodeToUtf16(units);
            for (var i = 0; i < written; i++)
            { low = (low ^ units[i]) * 1099511628211UL; high = (high ^ units[i]) * 14695981039346656037UL; }
            utf8 = utf8[consumed..];
        }
    }

    internal static void Mix(ref ulong low, ref ulong high, string text)
    {
        foreach (var character in text)
        { low = (low ^ character) * 1099511628211UL; high = (high ^ character) * 14695981039346656037UL; }
    }

    /// <summary>The invariant decimal spelling of one integer, mixed as the string <c>ToString(InvariantCulture)</c>
    /// would be.</summary>
    internal static void Mix(ref ulong low, ref ulong high, long value)
    {
        Span<char> digits = stackalloc char[24];
        value.TryFormat(digits, out var written, default, CultureInfo.InvariantCulture);
        for (var i = 0; i < written; i++)
        { low = (low ^ digits[i]) * 1099511628211UL; high = (high ^ digits[i]) * 14695981039346656037UL; }
    }

    [ThreadStatic] private static NameTable? NameScratch;

    /// <summary>The keys of every object still open during one validation walk, as ranges of the raw text. A key with
    /// no escape compares by its bytes, which is its value; a key with one compares by the text it unescapes to, which
    /// is what the repeated-key rule is about. An object with more keys than a pairwise scan is fit for keeps its own
    /// set of names instead.</summary>
    private sealed class NameTable
    {
        private const int PairwiseLimit = 32;
        private (int Start, int Length, bool Escaped)[] names = new (int, int, bool)[64];
        private int[] frames = new int[33];
        private readonly List<HashSet<string>?> wide = new();
        private int count, depth;

        internal void Reset() { count = 0; depth = 0; wide.Clear(); }

        internal void Open()
        {
            if (depth == frames.Length) Array.Resize(ref frames, frames.Length * 2);
            frames[depth++] = count;
            wide.Add(null);
        }

        internal void Close()
        {
            count = frames[--depth];
            wide.RemoveAt(wide.Count - 1);
        }

        internal bool Add(ReadOnlySpan<byte> raw, ref Utf8JsonReader reader, out string? repeated)
        {
            repeated = null;
            var span = reader.ValueSpan;
            var start = (int)reader.TokenStartIndex + 1;
            var escaped = span.IndexOf((byte)'\\') >= 0;
            var frame = frames[depth - 1];
            if (wide[depth - 1] is { } set)
            {
                var name = reader.GetString()!;
                if (!set.Add(name)) { repeated = name; return false; }
                return true;
            }
            for (var i = frame; i < count; i++)
            {
                var other = names[i];
                bool same;
                if (!escaped && !other.Escaped) same = raw.Slice(other.Start, other.Length).SequenceEqual(span);
                else same = Unescaped(raw, other) == reader.GetString();
                if (same) { repeated = reader.GetString(); return false; }
            }
            if (count - frame >= PairwiseLimit)
            {
                // Past the pairwise bound the object keeps a set of its own names, seeded with the ones already read.
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var i = frame; i < count; i++) names.Add(Unescaped(raw, this.names[i]));
                names.Add(reader.GetString()!);
                wide[depth - 1] = names;
                return true;
            }
            if (count == names.Length) Array.Resize(ref names, names.Length * 2);
            names[count++] = (start, span.Length, escaped);
            return true;
        }

        private static string Unescaped(ReadOnlySpan<byte> raw, (int Start, int Length, bool Escaped) name)
        {
            var quoted = raw.Slice(name.Start - 1, name.Length + 2);
            var reader = new Utf8JsonReader(quoted);
            reader.Read();
            return reader.GetString()!;
        }
    }
}

/// <summary>
/// One port contract as dispatch reads it: its id, its type as a closed kind, and the flags a value check asks, read
/// once where the contract becomes a fact of the kernel (a binding's registration, a plan's load) instead of by name on
/// every value. A contract this view cannot type — a kind with no fast rule, an unreadable field — is kept as
/// <see cref="Kinds.Other"/> and validated by <see cref="RuntimeJson.ValidateValue"/> over the contract itself, so the
/// view never answers a question the contract would have answered differently.
/// </summary>
internal sealed class PortView
{
    internal enum Kinds : byte { Other, Execution, Entity, Boolean, Integer, Number, String, Enum, Handle, Resource }

    private PortView(JsonElement contract)
    {
        Contract = contract;
        Id = RuntimeGraphContracts.OptionalText(contract, "id") ?? "";
        var type = RuntimeGraphContracts.OptionalText(contract, "type");
        Type = type ?? "";
        HandlerKind = type switch
        {
            "boolean" => ValueKind.Boolean,
            "integer" => ValueKind.Integer,
            "number" => ValueKind.Number,
            "string" or "object-address" => ValueKind.String,
            // A scalar enum is decoded to its member name at the handler boundary; a collection retains
            // the shared set's member indices (for example, device_filter).
            "enum" => RuntimeGraphContracts.Many(contract) ? ValueKind.Integer : ValueKind.String,
            "vector3" => ValueKind.Vector3,
            "entity" => ValueKind.Entity,
            "handle" => ValueKind.Handle,
            "resource" => ValueKind.Resource,
            "event" => ValueKind.Event,
            _ => ValueKind.Missing
        };
        Nullable = RuntimeJson.Flag(contract, "nullable");
        Optional = RuntimeJson.Flag(contract, "optional");
        var cardinality = contract.TryGetProperty("cardinality", out var declared) ? declared : default;
        var readable = RuntimeJson.IsText(Id) && type != null
            && (cardinality.ValueKind is JsonValueKind.Undefined or JsonValueKind.String);
        Many = cardinality.ValueKind == JsonValueKind.String && cardinality.GetString() == "many";
        Kind = !readable ? Kinds.Other : type switch
        {
            "execution" => Kinds.Execution,
            "entity" => Kinds.Entity,
            "boolean" => Kinds.Boolean,
            "integer" => Kinds.Integer,
            "number" => Kinds.Number,
            "string" => Kinds.String,
            "enum" => Kinds.Enum,
            "handle" => Kinds.Handle,
            "resource" => Kinds.Resource,
            _ => Kinds.Other
        };
        if (Kind == Kinds.Enum)
        {
            // An enum port indexes its whole named set; the handler reads the member name, so the names are built
            // once as values here.
            var schema = RuntimeGraphContracts.OptionalText(contract, "schema");
            if (schema != null && RuntimeGraphContracts.EnumSets.TryGetValue(schema, out var members))
            {
                EnumMembers = new JsonElement[members.Length];
                EnumNames = members;
                EnumIndices = new JsonElement[members.Length];
                for (var i = 0; i < members.Length; i++) { EnumMembers[i] = RuntimeJson.From(members[i]); EnumIndices[i] = RuntimeJson.From(i); }
            }
            else Kind = Kinds.Other;
        }
        // A collection of anything but entities is checked element by element against a derived contract; that rule
        // stays the contract's own.
        if (Many && Kind is not (Kinds.Entity or Kinds.Other)) Kind = Kinds.Other;
        IdUtf8 = Encoding.UTF8.GetBytes(Id);
        HandleKind = RuntimeGraphContracts.OptionalText(contract, "handleKind");
        Lifetime = RuntimeGraphContracts.OptionalText(contract, "lifetime");
        ActorRole = RuntimeGraphContracts.OptionalText(contract, "actorRole");
    }

    /// <summary>The actor role a trigger output of another name declares itself to be, or null.</summary>
    internal string? ActorRole { get; }

    /// <summary>The kind and lifetime a handle port declares, which a handle consumed there must have been cast with.</summary>
    internal string? HandleKind { get; }
    internal string? Lifetime { get; }

    internal JsonElement Contract { get; }
    internal string Id { get; }
    internal byte[] IdUtf8 { get; }
    internal string Type { get; }
    /// <summary>The handler value kind: scalar enums use member text, enum collections use member indices.</summary>
    internal ValueKind HandlerKind { get; }
    internal Kinds Kind { get; }
    internal bool Many { get; }
    internal bool Nullable { get; }
    internal bool Optional { get; }
    /// <summary>The member names of an enum port, as the values a handler is handed, by member index.</summary>
    internal JsonElement[]? EnumMembers { get; }
    /// <summary>The same members as text, and each member's index as the value a frame and the wire carry.</summary>
    internal string[]? EnumNames { get; }
    internal JsonElement[]? EnumIndices { get; }

    internal static PortView Of(JsonElement contract) => new(contract);

    internal static PortView[] Of(JsonElement[] contracts)
    {
        var views = new PortView[contracts.Length];
        for (var i = 0; i < contracts.Length; i++) views[i] = new PortView(contracts[i]);
        return views;
    }

    /// <summary>The handler's reading of one validated value of this port: an enum's member index becomes its member
    /// name, every other value is itself (<see cref="RuntimeJson.EnumPortToHandlerValue"/>).</summary>
    internal JsonElement HandlerValue(JsonElement value)
    {
        if (Kind == Kinds.Other) return RuntimeJson.EnumPortToHandlerValue(value, Contract);
        return Kind == Kinds.Enum && value.ValueKind == JsonValueKind.Number ? EnumMembers![(int)value.GetDouble()] : value;
    }

    /// <summary>
    /// <see cref="RuntimeJson.ValidateValue"/> for this port, by kind. The entity, scalar, enum and handle rules are the
    /// same checks in the same order with the same codes; every other kind is the contract's own rule.
    /// </summary>
    internal void Validate(JsonElement value, RuntimeEventRows? events)
    {
        if (Kind is Kinds.Other or Kinds.Resource or Kinds.Execution) { RuntimeJson.ValidateValue(value, Contract, events); return; }
        if (value.ValueKind == JsonValueKind.Null) { RuntimeJson.Require(Nullable, "null-input", Id); return; }
        switch (Kind)
        {
            case Kinds.Entity when Many:
                RuntimeJson.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= Wire.MaxSetWidth, "invalid-array", "entity many");
                EntityScratch ??= new HashSet<EntityReference>();
                EntityScratch.Clear();
                var distinct = true;
                foreach (var item in value.EnumerateArray()) distinct &= EntityScratch.Add(RuntimeJson.EntityRow(item));
                EntityScratch.Clear();
                RuntimeJson.Require(distinct, "duplicate-entity", "Repeated recipients require explicit semantics.");
                break;
            case Kinds.Entity: RuntimeJson.EntityRow(value); break;
            case Kinds.Boolean: RuntimeJson.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "invalid-boolean", "Expected bool."); break;
            case Kinds.Integer: RuntimeJson.Integer(value, -RuntimeJson.MaxSafeInteger); break;
            case Kinds.Number: RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n), "invalid-number", "Expected finite number."); break;
            case Kinds.String: RuntimeJson.Require(value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= Wire.PlanJson.MaxStringValueLength, "invalid-string", "Expected bounded string."); break;
            case Kinds.Enum:
                RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var member) && double.IsFinite(member)
                    && member == Math.Truncate(member) && member >= 0 && member < EnumMembers!.Length, "invalid-enum", "Expected enum member index."); break;
            case Kinds.Handle: RuntimeJson.Handle(value); break;
        }
    }

    [ThreadStatic] private static HashSet<EntityReference>? EntityScratch;
}

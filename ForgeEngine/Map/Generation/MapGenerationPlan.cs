using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeEngine.Framework;

namespace ForgeMap.Generation;

internal sealed class GenerationRefusal : Exception
{
    public string Code { get; }
    public string Path { get; }
    public GenerationRefusal(string code, string path) : this(code, path, null) { }
    public GenerationRefusal(string code, string path, string? detail)
        : base($"{code}: {path}" + (detail == null ? "" : " " + detail))
    {
        Code = code;
        Path = path;
    }
}

/// <summary>Owns a cloned, validated JSON value. No game objects or mutable source bytes are retained.</summary>
internal sealed class MapGenerationPlan
{
    public JsonElement Root { get; }
    internal MapGenerationPlan(JsonElement root) => Root = root.Clone();
    public JsonElement NativeInputs => Root.GetProperty("nativeInputs");
    public JsonElement Resources => Root.GetProperty("resources");
    public JsonElement Levels => Root.GetProperty("levels");
    public JsonElement Dimensions => Root.GetProperty("dimensions");
    public JsonElement Zones => Root.GetProperty("zones");
    public JsonElement Rooms => Root.GetProperty("rooms");
    public JsonElement Connections => Root.GetProperty("connections");
    public JsonElement UnpairedExits => Root.GetProperty("unpairedExits");
    public JsonElement AreaRules => Root.GetProperty("areaRules");
    public JsonElement Sequences => Root.GetProperty("sequences");
    public JsonElement Operations => Root.GetProperty("operations");
    public JsonElement AuthorMap => Root.GetProperty("authorMap");
}

/// <summary>Canonical JSON bytes use ordinal keys, preserve table order and emit normalized float32 values.</summary>
internal static class MapGenerationPlanCanonicalWriter
{
    public static byte[] Write(MapGenerationPlan plan)
    {
        var text = new StringBuilder();
        WriteValue(text, plan.Root, MapGenerationPlanReader.Shape);
        return new UTF8Encoding(false, true).GetBytes(text.ToString());
    }

    private static void WriteValue(StringBuilder output, JsonElement value, PlanShape shape)
    {
        if (value.ValueKind == JsonValueKind.Null) { output.Append("null"); return; }
        shape = MapGenerationPlanReader.Resolve(value, shape, "$");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                bool first = true;
                foreach (var field in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) output.Append(',');
                    first = false;
                    WriteString(output, field.Name);
                    output.Append(':');
                    WriteValue(output, field.Value, shape.Fields![field.Name]);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                for (int i = 0; i < value.GetArrayLength(); i++)
                {
                    if (i > 0) output.Append(',');
                    WriteValue(output, value[i], shape.Item!);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(output, value.GetString()!);
                break;
            case JsonValueKind.Number:
                double number = value.GetDouble();
                if (shape.Kind == "float") number = (double)(float)number;
                output.Append(Number(number));
                break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            default: throw new GenerationRefusal("gen-format", "$");
        }
    }

    private static void WriteString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c < 0x20) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }

    internal static string Number(double value)
    {
        if (value == 0) return "0";
        string raw = value.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        bool negative = raw[0] == '-';
        if (negative) raw = raw.Substring(1);
        int exponentAt = raw.IndexOf('e');
        int exponent = exponentAt < 0 ? 0 : int.Parse(raw.Substring(exponentAt + 1), CultureInfo.InvariantCulture);
        string mantissa = exponentAt < 0 ? raw : raw.Substring(0, exponentAt);
        int point = mantissa.IndexOf('.');
        int decimalPosition = (point < 0 ? mantissa.Length : point) + exponent;
        string digits = mantissa.Replace(".", "");
        int leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0') leading++;
        digits = digits.Substring(leading);
        decimalPosition -= leading;
        digits = digits.TrimEnd('0');
        string result;
        double absolute = Math.Abs(value);
        if (absolute >= 1e-6 && absolute < 1e21)
        {
            result = decimalPosition <= 0 ? "0." + new string('0', -decimalPosition) + digits
                : decimalPosition >= digits.Length ? digits + new string('0', decimalPosition - digits.Length)
                : digits.Substring(0, decimalPosition) + "." + digits.Substring(decimalPosition);
        }
        else
        {
            int scientificExponent = decimalPosition - 1;
            result = digits.Substring(0, 1) + (digits.Length > 1 ? "." + digits.Substring(1) : "")
                + "e" + (scientificExponent >= 0 ? "+" : "") + scientificExponent.ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + result : result;
    }
}

internal sealed class PlanShape
{
    public string Kind { get; }
    public string[]? Values { get; }
    public Dictionary<string, PlanShape>? Fields { get; }
    public PlanShape? Item { get; }
    public PlanShape[]? Choices { get; }
    public PlanShape(string kind, string[]? values = null, Dictionary<string, PlanShape>? fields = null,
        PlanShape? item = null, PlanShape[]? choices = null)
    {
        Kind = kind;
        Values = values;
        Fields = fields;
        Item = item;
        Choices = choices;
    }
}


internal static class MapGenerationPlanReader
{
    private static PlanShape S(string kind) => new(kind);
    private static PlanShape E(params string[] values) => new("enum", values);
    private static PlanShape A(PlanShape item) => new("array", item: item);
    private static PlanShape N(PlanShape item) => new("nullable", item: item);
    /// <summary>A field a plan may leave out; plans written before it existed never have it.</summary>
    private static PlanShape Opt(PlanShape item) => new("optional", item: item);
    private static PlanShape C(params PlanShape[] choices) => new("choice", choices: choices);
    private static PlanShape O(params (string Name, PlanShape Shape)[] fields) =>
        new("object", fields: fields.ToDictionary(f => f.Name, f => f.Shape, StringComparer.Ordinal));
    private static readonly PlanShape Index = S("index"), Text = S("text"), Float = S("float"), Int32 = S("int32");
    private static readonly PlanShape Range = O(("start", Index), ("count", Index));
    private static readonly PlanShape Identity = O(("id", Text), ("revision", Text));
    /// <summary>The resource key the website gives a Forge blank room; RoomRecipeHost registers its template under it.</summary>
    private static readonly Regex ForgeRoomResourcePath = new(@"^Assets/Forge/Rooms/[a-zA-Z0-9._-]+/[a-f0-9]{64}\.prefab$", RegexOptions.CultureInvariant);
    private static readonly PlanShape Block = O(("input", Index), ("block", Index));
    private static readonly PlanShape Seed = C(O(("mode", E("native"))),
        O(("mode", E("fixed")), ("type", E("uint32")), ("value", S("uint32"))),
        O(("mode", E("fixed")), ("type", E("int32")), ("value", Int32)));
    internal static readonly PlanShape Shape = O(
        ("kind", E("forge-map-generation")), ("schemaVersion", E(FormatVersion.Current)),
        ("package", O(("id", Text), ("version", S("version")))), ("source", Identity),
        ("runtime", O(("id", Text), ("major", S("uint32")), ("minMinor", S("uint32")))),
        ("generationFormat", O(("id", E("forge-map-generation")), ("version", E(FormatVersion.Current)))),
        ("nativeInputs", A(O(("index", Index), ("id", Text), ("path", S("path")), ("byteLength", S("safe")),
            ("sha256", S("hash")), ("ownerPackage", Text),
            ("blocks", A(O(("index", Index), ("kind", E("Rundown", "LevelLayout", "Dimension", "LevelGenSettings", "ComplexResourceSet", "Coverage")), ("id", S("uint32")))))))),
        ("resources", A(O(("index", Index), ("id", Text), ("revision", Text), ("origin", E("vanilla", "forge-room")),
            ("complexResourceSet", Block), ("subComplex", Text), ("shard", Text), ("prefabPath", S("assetPath")),
            ("category", E("room", "custom-geomorph", "elevator", "connection", "cap", "divider")),
            ("descriptor", O(("id", Text), ("revision", Text), ("sha256", S("hash")),
                ("footprint", A(O(("x", Int32), ("z", Int32)))),
                ("exits", A(O(("index", Index), ("id", Text), ("hierarchyPath", S("path")),
                    ("position", O(("x", Float), ("y", Float), ("z", Float))), ("yaw", S("yaw"))))),
                ("areas", A(Identity))))))),
        ("levels", A(O(("index", Index), ("id", Text), ("reference", S("level")), ("nativeInputs", Range),
            ("dimensions", Range), ("zones", Range), ("rooms", Range), ("connections", Range), ("operations", Range)))),
        ("dimensions", A(O(("index", Index), ("id", Text), ("level", Index), ("nativeAddress", Int32),
            ("dimensionBlock", N(Block)), ("levelGenConfig", O(("input", Index), ("owner", O(("kind", E("ComplexResourceSet")), ("id", S("uint32")))), ("fieldPath", E("LevelGenConfig")))), ("rootRoom", Index), ("resourceSet", Block),
            ("extraResources", A(Index)), ("mode", E("native", "arena", "static")), ("enabled", S("bool")), ("zones", Range)))),
        ("zones", A(O(("index", Index), ("id", Text), ("dimension", Index), ("layer", S("layer")), ("localIndex", Int32),
            ("layout", Block), ("rootRoom", Index), ("rooms", Range), ("connections", Range), ("areaRules", Range),
            ("source", N(O(("room", Index), ("area", Index), ("exit", Index)))),
            ("stop", O(("areaCount", N(Int32)), ("coverage", N(O(("minimum", Float), ("maximum", Float)))), ("expansionCount", N(Int32)))),
            ("seeds", O(("zone", Seed), ("marker", Seed), ("light", Seed)))))),
        ("rooms", A(O(("index", Index), ("id", Text), ("room", Identity), ("zone", Index), ("resource", Index),
            ("rootRole", E("none", "zone", "dimension", "elevator")),
            ("placement", N(O(("space", E("zone-root", "dimension-grid")), ("x", Int32), ("z", Int32),
                ("offsetX", Float), ("offsetZ", Float)))),
            ("rotation", C(O(("mode", E("native"))), O(("mode", E("absolute")), ("yaw", S("yaw"))),
                O(("mode", E("relative")), ("yaw", S("yaw")), ("sourceRoom", Index)),
                O(("mode", E("random")), ("candidates", A(S("yaw")))))),
            ("height", N(Float)), ("parentConnection", N(Index)), ("sourceExit", N(Index)), ("areaRules", Range), ("removedParts", Opt(A(Text)))))),
        ("connections", A(O(("index", Index), ("id", Text), ("fromRoom", Index), ("fromExit", Index), ("toRoom", Index), ("toExit", Index),
            ("door", E("native", "none", "weak", "security", "apex", "bulkhead")), ("resource", N(Index)),
            ("directions", N(O(("forward", N(Index)), ("right", N(Index)), ("backward", N(Index)), ("left", N(Index))))),
            ("selection", E("native", "explicit")), ("heightFamily", E("same", "single-drop", "double-drop")),
            ("edge", E("internal", "zone-entry")), ("cap", E("native", "none")),
            ("noDoorProbability", N(Float))))),
        ("unpairedExits", A(O(("index", Index), ("id", Text), ("room", Index), ("exit", Index),
            ("treatment", E("cap", "divider", "open")), ("resource", N(Index))))),
        // Loops: two free doorways that meet on the grid. Left out when a plan has none.
        ("links", Opt(A(O(("index", Index), ("id", Text), ("fromRoom", Index), ("fromExit", Index), ("toRoom", Index), ("toExit", Index),
            ("door", E("none", "weak")), ("resource", N(Index)))))),
        ("areaRules", A(O(("index", Index), ("id", Text), ("room", Index), ("sourceAreas", A(Identity)), ("order", A(Index)), ("seeds", A(Seed))))),
        ("sequences", A(C(
            O(("index", Index), ("id", Text), ("zone", Index), ("kind", E("room")), ("cycle", S("bool")),
                ("elements", A(O(("resource", Index), ("addresses", A(O(("source", Identity),
                    ("areas", A(O(("source", Identity), ("target", Identity)))),
                    ("exits", A(O(("source", Text), ("target", Text))))))))))),
            O(("index", Index), ("id", Text), ("zone", Index), ("kind", E("connection")), ("cycle", S("bool")), ("elements", A(Index))),
            O(("index", Index), ("id", Text), ("zone", Index), ("kind", E("height")), ("cycle", S("bool")), ("elements", A(Float)))))),
        ("operations", A(O(("index", Index), ("id", Text),
            ("kind", E("load-resource", "create-dimension", "place-room", "order-areas", "connect", "finish-exit", "expand", "verify-cells")),
            ("table", E("resources", "dimensions", "rooms", "areaRules", "connections", "unpairedExits", "zones")), ("input", Index),
            ("stage", E("resources", "dimensions", "rooms", "areas", "connections", "expansion", "ownership")),
            ("ordinal", S("uint32")), ("prerequisites", Range), ("author", Index)))),
        ("authorMap", A(O(("index", Index), ("id", Text), ("work", Identity), ("dimensionId", N(Text)), ("zoneId", N(Text)),
            ("roomId", N(Text)), ("connectionId", N(Text)), ("exitId", N(Text)), ("areaId", N(Text)), ("field", Text)))));

    public static MapGenerationPlan Read(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            // GetString with strict UTF-8 first: JsonDocument alone is not a UTF-8 admission check.
            string text = new UTF8Encoding(false, true).GetString(bytes);
            document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 65 });
        }
        catch (Exception error) when (error is JsonException || error is DecoderFallbackException || error is ArgumentException)
        {
            throw new GenerationRefusal("gen-format", "$");
        }
        using (document)
        {
            try
            {
                CheckDuplicateProperties(document.RootElement);
                ReadShape(document.RootElement, Shape, "$");
            }
            catch (InvalidOperationException) { throw new GenerationRefusal("gen-format", "$"); }
            var plan = new MapGenerationPlan(document.RootElement);
            Validate(plan);
            // Canonicalization is also the single float32 normalization boundary.
            using var normalized = JsonDocument.Parse(MapGenerationPlanCanonicalWriter.Write(plan));
            return new MapGenerationPlan(normalized.RootElement);
        }
    }

    private static void CheckDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) Fail("$");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicateProperties(item);
    }

    internal static PlanShape Resolve(JsonElement value, PlanShape shape, string path)
    {
        if (shape.Kind is "nullable" or "optional") return Resolve(value, shape.Item!, path);
        if (shape.Kind != "choice") return shape;
        foreach (var choice in shape.Choices!)
        {
            bool match = value.ValueKind == JsonValueKind.Object;
            foreach (var field in choice.Fields!)
            {
                if (field.Value.Kind != "enum") continue;
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(field.Key, out var discriminator) ||
                    discriminator.ValueKind != JsonValueKind.String || !field.Value.Values!.Contains(discriminator.GetString(), StringComparer.Ordinal))
                { match = false; break; }
            }
            if (match) return choice;
        }
        throw new GenerationRefusal("gen-format", path);
    }

    private static void ReadShape(JsonElement value, PlanShape shape, string path)
    {
        if (shape.Kind == "nullable" && value.ValueKind == JsonValueKind.Null) return;
        shape = Resolve(value, shape, path);
        if (shape.Kind == "object")
        {
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !shape.Fields!.ContainsKey(p.Name))) Fail(path);
            foreach (var field in shape.Fields!)
            {
                bool present = value.TryGetProperty(field.Key, out var member);
                if (field.Value.Kind == "optional") { if (present) ReadShape(member, field.Value.Item!, path + "." + field.Key); }
                else if (present) ReadShape(member, field.Value, path + "." + field.Key);
                else Fail(path);
            }
        }
        else if (shape.Kind == "array")
        {
            if (value.ValueKind != JsonValueKind.Array) Fail(path);
            for (int i = 0; i < value.GetArrayLength(); i++) ReadShape(value[i], shape.Item!, $"{path}[{i}]");
        }
        else if (shape.Kind == "enum")
        {
            if (value.ValueKind != JsonValueKind.String || !shape.Values!.Contains(value.GetString(), StringComparer.Ordinal)) Fail(path);
        }
        else ReadScalar(value, shape.Kind, path);
    }

    private static void ReadScalar(JsonElement value, string kind, string path)
    {
        if (kind == "bool")
        {
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False) Fail(path);
            return;
        }
        if (new[] { "text", "hash", "version", "path", "assetPath", "level" }.Contains(kind))
        {
            if (value.ValueKind != JsonValueKind.String) Fail(path);
            string text;
            try { text = value.GetString()!; }
            catch (InvalidOperationException) { throw new GenerationRefusal("gen-format", path); }
            if (!ValidText(text)) Fail(path);
            if (kind == "hash" && !Regex.IsMatch(text, "^[a-f0-9]{64}$")) Fail(path);
            if (kind == "version" && !Regex.IsMatch(text, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")) Fail(path);
            if ((kind == "path" || kind == "assetPath") && !ValidPath(text)) Fail(path);
            if (kind == "assetPath" && !text.StartsWith("Assets/", StringComparison.Ordinal)) Fail(path);
            if (kind == "level")
            {
                var parts = text.Split(':');
                if (!Regex.IsMatch(text, @"^(0|[1-9][0-9]*):[A-E]:(0|[1-9][0-9]*)$") ||
                    !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
                    !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)) Fail(path);
            }
            return;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number)) Fail(path);
        number = value.GetDouble();
        if (kind == "float")
        {
            if (!float.IsFinite((float)number)) Fail(path);
            return;
        }
        if (kind == "yaw")
        {
            // Yaw in degrees about +Y, any real in [0, 360).
            if (number < 0 || number >= 360) Fail(path, "gen-rotation-invalid");
            return;
        }
        (double Minimum, double Maximum) bounds = kind switch
        {
            "index" => (0, int.MaxValue),
            "safe" => (0, 9007199254740991),
            "uint32" => (0, uint.MaxValue),
            "int32" => (int.MinValue, int.MaxValue),
            "one" => (1, 1),
            "layer" => (0, 2),
            _ => throw new GenerationRefusal("gen-format", path)
        };
        if (Math.Truncate(number) != number || number < bounds.Minimum || number > bounds.Maximum)
            Fail(path, "gen-format");
    }

    private static bool ValidText(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 32 || c == 127) return false;
            if (char.IsHighSurrogate(c)) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
            else if (char.IsLowSurrogate(c)) return false;
        }
        // ECMAScript trim treats FEFF as whitespace and does not treat U+0085 as whitespace.
        return text.Length > 0 && !JsWhitespace(text[0]) && !JsWhitespace(text[^1]) && text.IsNormalized(NormalizationForm.FormC);
    }

    private static bool JsWhitespace(char c) => c is '\u0009' or '\u000a' or '\u000b' or '\u000c' or '\u000d' or '\u0020' or '\u00a0' or '\u1680'
        or >= '\u2000' and <= '\u200a' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';

    private static bool ValidPath(string text) => ForgeCore.PosixPath.IsRelativeFile(text) && text.IndexOfAny(new[] { '%', '#' }) < 0;

    // Frozen protocol collision keys fold ASCII only; physical aliases remain the safe package reader's responsibility.
    private static string AsciiPathKey(string path)
    {
        char[] key = path.ToCharArray();
        for (int i = 0; i < key.Length; i++) if (key[i] >= 'A' && key[i] <= 'Z') key[i] = (char)(key[i] + 32);
        return new string(key);
    }
    private static void Fail(string path, string code = "gen-format") => throw new GenerationRefusal(code, path);
    private static JsonElement P(JsonElement row, string field) => row.GetProperty(field);
    private static int I(JsonElement row, string field) => (int)P(row, field).GetDouble();
    private static string T(JsonElement row, string field) => P(row, field).GetString()!;
    private static bool Null(JsonElement value) => value.ValueKind == JsonValueKind.Null;
    private static bool Contains(JsonElement range, int index) => index >= I(range, "start") && index - I(range, "start") < I(range, "count");
    private static JsonElement Ref(JsonElement rows, int index, string path, string code = "gen-reference-missing")
    {
        if (index < 0 || index >= rows.GetArrayLength()) Fail(path, code);
        return rows[index];
    }
    private static IEnumerable<int> Indexes(JsonElement range, int length, string path)
    {
        int start = I(range, "start"), count = I(range, "count");
        if (start > length || count > length - start) Fail(path);
        return Enumerable.Range(start, count);
    }
    private static void Unique(JsonElement rows, string path)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (!ids.Add(T(row, "id"))) Fail($"{path}[{index}].id");
            index++;
        }
    }
    private static bool Category(JsonElement resource, params string[] values) => values.Contains(T(resource, "category"), StringComparer.Ordinal);
    private static bool IdentityEquals(JsonElement a, JsonElement b) => T(a, "id") == T(b, "id") && T(a, "revision") == T(b, "revision");
    private static void NoCycles(Func<int, int?> next, int count, Func<int, string> path)
    {
        var done = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            var visiting = new HashSet<int>();
            int? n = i;
            while (n.HasValue && !done.Contains(n.Value))
            {
                if (!visiting.Add(n.Value)) Fail(path(n.Value), "gen-source-cycle");
                n = next(n.Value);
            }
            done.UnionWith(visiting);
        }
    }

    public static void Validate(MapGenerationPlan plan)
    {
        JsonElement root = plan.Root;
        try
        {
            CheckDuplicateProperties(root);
            ReadShape(root, Shape, "$");
        }
        catch (InvalidOperationException) { throw new GenerationRefusal("gen-format", "$"); }
        JsonElement nativeInputs = plan.NativeInputs, resources = plan.Resources, levels = plan.Levels,
            dimensions = plan.Dimensions, zones = plan.Zones, rooms = plan.Rooms, connections = plan.Connections, unpairedExits = plan.UnpairedExits,
            areaRules = plan.AreaRules, sequences = plan.Sequences, operations = plan.Operations, authorMap = plan.AuthorMap;
        // Zones, rooms and their connection, exit, Area and sequence rows keep the author's identity, which is unique
        // inside one level only: a Rundown plan merges levels without renaming them (contract §4.5 M12), so those
        // tables are checked per level once their owner references are known to be in bounds.
        string[] levelOwned = { "zones", "rooms", "connections", "unpairedExits", "areaRules", "sequences" };
        foreach (string name in new[] { "nativeInputs", "resources", "levels", "dimensions", "zones", "rooms", "connections", "unpairedExits", "areaRules", "sequences", "operations", "authorMap" })
        {
            var rows = P(root, name);
            if (!levelOwned.Contains(name)) Unique(rows, name);
            for (int i = 0; i < rows.GetArrayLength(); i++) if (I(rows[i], "index") != i) Fail($"{name}[{i}].index");
        }
        JsonElement BlockRef(JsonElement block, string kind, string path)
        {
            var input = Ref(nativeInputs, I(block, "input"), path);
            var item = Ref(P(input, "blocks"), I(block, "block"), path);
            if (T(item, "kind") != kind) Fail(path, "gen-layout-owner");
            return item;
        }
        void ParentRange(string child, string parent, JsonElement range, JsonElement rows, Func<int, bool> belongs)
        {
            foreach (int index in Indexes(range, rows.GetArrayLength(), $"{parent}.{child}"))
                if (!belongs(index)) Fail($"{parent}.{child}", "gen-layout-owner");
        }
        // Bounds are established before any cross-table dereference, including combined corruptions.
        foreach (var dimension in dimensions.EnumerateArray())
        {
            int index = I(dimension, "index");
            Ref(levels, I(dimension, "level"), $"dimensions[{index}].level");
            Ref(rooms, I(dimension, "rootRoom"), $"dimensions[{index}].rootRoom");
            if (T(dimension, "mode") == "arena") Fail($"dimensions[{index}].mode", "gen-dimension-unsupported");
            if (I(dimension, "nativeAddress") == 22) Fail($"dimensions[{index}].nativeAddress", "gen-dimension-unsupported");
        }
        foreach (var zone in zones.EnumerateArray())
        {
            int index = I(zone, "index");
            Ref(dimensions, I(zone, "dimension"), $"zones[{index}].dimension");
            Ref(rooms, I(zone, "rootRoom"), $"zones[{index}].rootRoom");
            if (!Null(P(zone, "source"))) Ref(rooms, I(P(zone, "source"), "room"), $"zones[{index}].source.room", "gen-source-room");
        }
        foreach (var room in rooms.EnumerateArray())
        {
            int index = I(room, "index");
            Ref(zones, I(room, "zone"), $"rooms[{index}].zone");
            Ref(resources, I(room, "resource"), $"rooms[{index}].resource", "gen-resource-missing");
            if (!Null(P(room, "parentConnection"))) Ref(connections, I(room, "parentConnection"), $"rooms[{index}].parentConnection");
            var rotation = P(room, "rotation");
            if (T(rotation, "mode") == "relative") Ref(rooms, I(rotation, "sourceRoom"), $"rooms[{index}].rotation.sourceRoom", "gen-source-room");
        }
        foreach (var connection in connections.EnumerateArray())
        {
            int index = I(connection, "index");
            Ref(rooms, I(connection, "fromRoom"), $"connections[{index}].fromRoom");
            Ref(rooms, I(connection, "toRoom"), $"connections[{index}].toRoom");
            if (!Null(P(connection, "resource"))) Ref(resources, I(connection, "resource"), $"connections[{index}].resource", "gen-resource-missing");
            if (!Null(P(connection, "directions"))) foreach (string direction in new[] { "forward", "right", "backward", "left" })
                if (!Null(P(P(connection, "directions"), direction))) Ref(resources, I(P(connection, "directions"), direction), $"connections[{index}].directions.{direction}", "gen-resource-missing");
        }
        foreach (var exit in unpairedExits.EnumerateArray())
        {
            int index = I(exit, "index");
            Ref(rooms, I(exit, "room"), $"unpairedExits[{index}].room");
            if (!Null(P(exit, "resource"))) Ref(resources, I(exit, "resource"), $"unpairedExits[{index}].resource", "gen-resource-missing");
        }
        foreach (var rule in areaRules.EnumerateArray()) Ref(rooms, I(rule, "room"), $"areaRules[{I(rule, "index")}].room");
        foreach (var sequence in sequences.EnumerateArray()) Ref(zones, I(sequence, "zone"), $"sequences[{I(sequence, "index")}].zone");
        foreach (var operation in operations.EnumerateArray())
        {
            int index = I(operation, "index");
            Ref(P(root, T(operation, "table")), I(operation, "input"), $"operations[{index}].input");
            Ref(authorMap, I(operation, "author"), $"operations[{index}].author");
        }
        int ZoneLevel(int zone) => I(dimensions[I(zones[zone], "dimension")], "level");
        int RoomLevel(int room) => ZoneLevel(I(rooms[room], "zone"));
        int OwnerLevel(string table, int index) => table switch
        {
            "dimensions" => I(dimensions[index], "level"),
            "zones" => ZoneLevel(index),
            "rooms" => RoomLevel(index),
            "connections" => RoomLevel(I(connections[index], "toRoom")),
            "unpairedExits" => RoomLevel(I(unpairedExits[index], "room")),
            "areaRules" => RoomLevel(I(areaRules[index], "room")),
            _ => ZoneLevel(I(sequences[index], "zone"))
        };
        foreach (string name in levelOwned)
        {
            var seen = new HashSet<(int Level, string Id)>();
            var rows = P(root, name);
            for (int index = 0; index < rows.GetArrayLength(); index++)
                if (!seen.Add((OwnerLevel(name, index), T(rows[index], "id")))) Fail($"{name}[{index}].id");
        }
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        var locators = new HashSet<(uint ResourceSet, string SubComplex, string Shard, string PrefabPath)>();
        foreach (var input in nativeInputs.EnumerateArray())
        {
            int inputIndex = I(input, "index");
            if (!paths.Add(AsciiPathKey(T(input, "path")))) Fail($"nativeInputs[{inputIndex}].path");
            var blocks = P(input, "blocks");
            for (int i = 0; i < blocks.GetArrayLength(); i++)
            {
                var block = blocks[i];
                if (I(block, "index") != i) Fail($"nativeInputs[{inputIndex}].blocks[{i}].index");
                if (!blockIds.Add(T(block, "kind") + ":" + P(block, "id").GetDouble().ToString(CultureInfo.InvariantCulture))) Fail($"nativeInputs[{inputIndex}].blocks[{i}].id");
            }
        }
        foreach (var resource in resources.EnumerateArray())
        {
            int resourceIndex = I(resource, "index");
            string resourcePath = $"resources[{resourceIndex}]";
            var set = BlockRef(P(resource, "complexResourceSet"), "ComplexResourceSet", resourcePath + ".complexResourceSet");
            if (!locators.Add(((uint)P(set, "id").GetDouble(), T(resource, "subComplex"), T(resource, "shard"), T(resource, "prefabPath")))) Fail(resourcePath + ".locator");
            var descriptor = P(resource, "descriptor");
            var descriptorExits = P(descriptor, "exits");
            Unique(descriptorExits, resourcePath + ".descriptor.exits");
            Unique(P(descriptor, "areas"), resourcePath + ".descriptor.areas");
            for (int i = 0; i < descriptorExits.GetArrayLength(); i++) if (I(descriptorExits[i], "index") != i) Fail($"{resourcePath}.descriptor.exits[{i}].index");
            var cells = new HashSet<(int X, int Z)>();
            int cellIndex = 0;
            foreach (var cell in P(descriptor, "footprint").EnumerateArray())
            {
                if (!cells.Add((I(cell, "x"), I(cell, "z")))) Fail($"{resourcePath}.descriptor.footprint[{cellIndex}]", "gen-footprint-ambiguous");
                cellIndex++;
            }
            if (Category(resource, "room", "custom-geomorph", "elevator") && cells.Count == 0) Fail(resourcePath + ".descriptor.footprint", "gen-footprint-ambiguous");
            // A Forge room recipe is named only by its room resource key. A blank room is a custom geomorph, or the
            // elevator start of the level when the author's room is the starting room; a changed original room keeps the
            // category of the vanilla room it is rebuilt from.
            bool forgeRoom = T(resource, "origin") == "forge-room";
            if (forgeRoom != ForgeRoomResourcePath.IsMatch(T(resource, "prefabPath")) || forgeRoom && !Category(resource, "room", "custom-geomorph", "elevator"))
                Fail(resourcePath + ".origin");
        }
        var levelRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in levels.EnumerateArray())
        {
            int index = I(level, "index");
            if (!levelRefs.Add(T(level, "reference"))) Fail($"levels[{index}].reference");
            foreach (string name in new[] { "nativeInputs", "dimensions", "zones", "rooms", "connections", "operations" })
                _ = Indexes(P(level, name), P(root, name).GetArrayLength(), $"levels[{index}].{name}");
        }
        foreach (var level in levels.EnumerateArray())
            if (dimensions.EnumerateArray().Count(d => I(d, "level") == I(level, "index") && I(d, "nativeAddress") == 0) != 1)
                Fail($"levels[{I(level, "index")}].mainDimension", "gen-dimension-mismatch");
        foreach (var dimension in dimensions.EnumerateArray())
        {
            int index = I(dimension, "index"), levelIndex = I(dimension, "level");
            string path = $"dimensions[{index}]";
            var level = Ref(levels, levelIndex, path + ".level");
            if (!Contains(P(level, "dimensions"), index)) Fail(path + ".level", "gen-layout-owner");
            var dimensionBlock = P(dimension, "dimensionBlock");
            if ((I(dimension, "nativeAddress") == 0) != Null(dimensionBlock)) Fail(path + ".dimensionBlock", "gen-dimension-mismatch");
            if (!Null(dimensionBlock)) BlockRef(dimensionBlock, "Dimension", path + ".dimensionBlock");
            BlockRef(P(dimension, "resourceSet"), "ComplexResourceSet", path + ".resourceSet");
            var config = P(dimension, "levelGenConfig");
            var configInput = Ref(nativeInputs, I(config, "input"), path + ".levelGenConfig");
            var owner = P(configInput, "blocks").EnumerateArray().Where(b => T(b, "kind") == T(P(config, "owner"), "kind") && P(b, "id").GetDouble() == P(P(config, "owner"), "id").GetDouble()).ToArray();
            if (owner.Length != 1) Fail(path + ".levelGenConfig.owner", "gen-reference-missing");
            if (I(config, "input") != I(P(dimension, "resourceSet"), "input") || I(owner[0], "index") != I(P(dimension, "resourceSet"), "block")) Fail(path + ".levelGenConfig", "gen-layout-owner");
            foreach (string name in new[] { "dimensionBlock", "levelGenConfig", "resourceSet" })
                if (!Null(P(dimension, name)) && !Contains(P(level, "nativeInputs"), I(P(dimension, name), "input"))) Fail(path + ".nativeInputs", "gen-layout-owner");
            var room = Ref(rooms, I(dimension, "rootRoom"), path + ".rootRoom");
            var zone = Ref(zones, I(room, "zone"), path + ".rootRoom");
            if (I(zone, "dimension") != index || !new[] { "dimension", "elevator" }.Contains(T(room, "rootRole"))) Fail(path + ".rootRoom", "gen-root-invalid");
            if (dimensions.EnumerateArray().Any(other => I(other, "index") < index && I(other, "level") == levelIndex && I(other, "nativeAddress") == I(dimension, "nativeAddress"))) Fail(path + ".nativeAddress");
            var extras = P(dimension, "extraResources").EnumerateArray().Select(v => (int)v.GetDouble()).ToArray();
            if (extras.Distinct().Count() != extras.Length) Fail(path + ".extraResources");
            foreach (int extra in extras) Ref(resources, extra, path + ".extraResources");
            ParentRange("zones", path, P(dimension, "zones"), zones, i => I(zones[i], "dimension") == index);
        }
        var zoneAddresses = new HashSet<(int Dimension, int Layer, int Local)>();
        foreach (var zone in zones.EnumerateArray())
        {
            int index = I(zone, "index"), dimensionIndex = I(zone, "dimension");
            string path = $"zones[{index}]";
            var dimension = Ref(dimensions, dimensionIndex, path + ".dimension");
            if (!Contains(P(levels[I(dimension, "level")], "zones"), index)) Fail(path + ".level", "gen-layout-owner");
            if (!Contains(P(dimension, "zones"), index)) Fail(path + ".dimension", "gen-layout-owner");
            if (!zoneAddresses.Add((dimensionIndex, I(zone, "layer"), I(zone, "localIndex")))) Fail(path + ".address");
            BlockRef(P(zone, "layout"), "LevelLayout", path + ".layout");
            var level = levels[I(dimension, "level")];
            if (!Contains(P(level, "nativeInputs"), I(P(zone, "layout"), "input"))) Fail(path + ".layout", "gen-layout-owner");
            var room = Ref(rooms, I(zone, "rootRoom"), path + ".rootRoom");
            if (I(room, "zone") != index || T(room, "rootRole") == "none") Fail(path + ".rootRoom", "gen-root-invalid");
            if (index == I(rooms[I(dimension, "rootRoom")], "zone"))
            {
                if (I(room, "index") != I(dimension, "rootRoom") || !Null(P(room, "parentConnection"))) Fail(path + ".rootRoom", "gen-root-invalid");
            }
            // A zone whose first room has no door attached has no entry connection: it grows from the parent's free exit the game picks,
            // and the room stands at its own place.
            else if (!Null(P(room, "parentConnection")))
            {
                var parent = connections[I(room, "parentConnection")];
                if (I(parent, "toRoom") != I(room, "index") || T(parent, "edge") != "zone-entry" || I(rooms[I(parent, "fromRoom")], "zone") == index) Fail(path + ".rootRoom", "gen-root-invalid");
            }
            ParentRange("rooms", path, P(zone, "rooms"), rooms, i => I(rooms[i], "zone") == index);
            ParentRange("connections", path, P(zone, "connections"), connections, i =>
                I(connections[i], "toRoom") < rooms.GetArrayLength() && I(rooms[I(connections[i], "toRoom")], "zone") == index);
            ParentRange("areaRules", path, P(zone, "areaRules"), areaRules, i =>
                I(areaRules[i], "room") < rooms.GetArrayLength() && I(rooms[I(areaRules[i], "room")], "zone") == index);
            var stop = P(zone, "stop");
            if (!Null(P(stop, "coverage")) && (P(P(stop, "coverage"), "minimum").GetSingle() < 0 || P(P(stop, "coverage"), "maximum").GetSingle() < P(P(stop, "coverage"), "minimum").GetSingle())) Fail(path + ".stop.coverage");
            if ((!Null(P(stop, "areaCount")) && I(stop, "areaCount") < 0) || (!Null(P(stop, "expansionCount")) && I(stop, "expansionCount") < 0)) Fail(path + ".stop");
            var source = P(zone, "source");
            if (!Null(source))
            {
                var sourceRoom = Ref(rooms, I(source, "room"), path + ".source.room", "gen-source-room");
                var resource = Ref(resources, I(sourceRoom, "resource"), path + ".source.resource");
                Ref(P(P(resource, "descriptor"), "areas"), I(source, "area"), path + ".source.area", "gen-source-area");
                Ref(P(P(resource, "descriptor"), "exits"), I(source, "exit"), path + ".source.exit", "gen-source-expander");
                if (I(sourceRoom, "zone") >= zones.GetArrayLength() || I(zones[I(sourceRoom, "zone")], "dimension") != dimensionIndex) Fail(path + ".source", "gen-dimension-mismatch");
            }
        }
        NoCycles(i => Null(P(zones[i], "source")) ? null : I(rooms[I(P(zones[i], "source"), "room")], "zone"), zones.GetArrayLength(), i => $"zones[{i}].source");
        foreach (var room in rooms.EnumerateArray())
        {
            int index = I(room, "index");
            string path = $"rooms[{index}]";
            var zone = Ref(zones, I(room, "zone"), path + ".zone");
            if (!Contains(P(levels[I(dimensions[I(zone, "dimension")], "level")], "rooms"), index)) Fail(path + ".level", "gen-layout-owner");
            if (!Contains(P(zone, "rooms"), index)) Fail(path + ".zone", "gen-layout-owner");
            var resource = Ref(resources, I(room, "resource"), path + ".resource", "gen-resource-missing");
            if (!Category(resource, "room", "custom-geomorph", "elevator")) Fail(path + ".resource", "gen-resource-missing");
            var rotation = P(room, "rotation");
            if (T(rotation, "mode") == "relative")
            {
                var sourceRoom = Ref(rooms, I(rotation, "sourceRoom"), path + ".rotation.sourceRoom", "gen-source-room");
                if (I(zones[I(sourceRoom, "zone")], "dimension") != I(zone, "dimension")) Fail(path + ".rotation", "gen-dimension-mismatch");
            }
            if (T(rotation, "mode") == "random")
            {
                var candidates = P(rotation, "candidates").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (candidates.Length == 0 || candidates.Distinct().Count() != candidates.Length) Fail(path + ".rotation", "gen-rotation-invalid");
            }
            if (!Null(P(room, "parentConnection")))
            {
                var connection = Ref(connections, I(room, "parentConnection"), path + ".parentConnection");
                if (I(connection, "toRoom") != index) Fail(path + ".parentConnection", "gen-connection-mismatch");
                if (Null(P(room, "sourceExit")) || I(room, "sourceExit") != I(connection, "toExit")) Fail(path + ".sourceExit", "gen-connection-mismatch");
            }
            else if (!Null(P(room, "sourceExit"))) Fail(path + ".sourceExit", "gen-connection-mismatch");
            ParentRange("areaRules", path, P(room, "areaRules"), areaRules, i => I(areaRules[i], "room") == index);
        }
        NoCycles(i => T(P(rooms[i], "rotation"), "mode") == "relative" ? I(P(rooms[i], "rotation"), "sourceRoom") : null, rooms.GetArrayLength(), i => $"rooms[{i}].rotation");
        var exits = new HashSet<(int Room, int Exit)>();
        var incoming = new HashSet<int>();
        foreach (var connection in connections.EnumerateArray())
        {
            int index = I(connection, "index");
            string path = $"connections[{index}]";
            var from = Ref(rooms, I(connection, "fromRoom"), path + ".fromRoom");
            var to = Ref(rooms, I(connection, "toRoom"), path + ".toRoom");
            var toZone = zones[I(to, "zone")];
            if (!Contains(P(levels[I(dimensions[I(toZone, "dimension")], "level")], "connections"), index) ||
                !Contains(P(toZone, "connections"), index)) Fail(path + ".owner", "gen-layout-owner");
            if (I(from, "index") == I(to, "index")) Fail(path + ".rooms", "gen-connection-mismatch");
            var a = Ref(zones, I(from, "zone"), path + ".fromZone");
            var b = Ref(zones, I(to, "zone"), path + ".toZone");
            if (I(a, "dimension") != I(b, "dimension")) Fail(path + ".dimension", "gen-dimension-mismatch");
            foreach (var endpoint in new[] { (Room: from, Exit: I(connection, "fromExit"), Field: "fromExit"), (Room: to, Exit: I(connection, "toExit"), Field: "toExit") })
            {
                Ref(P(P(resources[I(endpoint.Room, "resource")], "descriptor"), "exits"), endpoint.Exit, path + "." + endpoint.Field);
                if (!exits.Add((I(endpoint.Room, "index"), endpoint.Exit))) Fail(path + "." + endpoint.Field, "gen-connection-mismatch");
            }
            if (!incoming.Add(I(connection, "toRoom")) || Null(P(to, "parentConnection")) || I(to, "parentConnection") != index) Fail(path + ".toRoom", "gen-connection-mismatch");
            if (I(from, "zone") == I(to, "zone") ? T(connection, "edge") != "internal" : T(connection, "edge") != "zone-entry" || I(b, "rootRoom") != I(to, "index")) Fail(path + ".edge", "gen-connection-mismatch");
            if (T(connection, "selection") == "explicit" && Null(P(connection, "resource"))) Fail(path + ".selection", "gen-connection-mismatch");
            if (!Null(P(connection, "resource")) && !Category(Ref(resources, I(connection, "resource"), path + ".resource", "gen-resource-missing"), "connection")) Fail(path + ".resource", "gen-connection-mismatch");
            if (T(connection, "edge") == "zone-entry" && !Null(P(connection, "noDoorProbability"))) Fail(path + ".noDoorProbability", "gen-connection-mismatch");
            if (!Null(P(connection, "directions")))
            {
                var directional = new[] { "forward", "right", "backward", "left" }.Select(name => P(P(connection, "directions"), name)).Where(value => !Null(value)).ToArray();
                if (directional.Length == 0 || directional.Any(value => !Category(Ref(resources, (int)value.GetDouble(), path + ".directions", "gen-resource-missing"), "connection"))) Fail(path + ".directions", "gen-connection-mismatch");
            }
        }
        foreach (var exit in unpairedExits.EnumerateArray())
        {
            int index = I(exit, "index"); string path = $"unpairedExits[{index}]";
            var room = Ref(rooms, I(exit, "room"), path + ".room");
            Ref(P(P(resources[I(room, "resource")], "descriptor"), "exits"), I(exit, "exit"), path + ".exit");
            if (!exits.Add((I(exit, "room"), I(exit, "exit")))) Fail(path + ".exit", "gen-connection-mismatch");
            bool open = T(exit, "treatment") == "open";
            if (!Null(P(exit, "resource"))) Ref(resources, I(exit, "resource"), path + ".resource", "gen-resource-missing");
            if (open ? !Null(P(exit, "resource")) : Null(P(exit, "resource")) || !Category(resources[I(exit, "resource")], T(exit, "treatment")))
                Fail(path + ".resource", "gen-connection-mismatch");
        }
        if (root.TryGetProperty("links", out var links))
        {
            var linkIds = new HashSet<(int Level, string Id)>();
            for (int index = 0; index < links.GetArrayLength(); index++)
            {
                var link = links[index]; string path = $"links[{index}]";
                if (I(link, "index") != index) Fail(path + ".index");
                var from = Ref(rooms, I(link, "fromRoom"), path + ".fromRoom");
                var to = Ref(rooms, I(link, "toRoom"), path + ".toRoom");
                if (I(from, "index") == I(to, "index")) Fail(path + ".rooms", "gen-connection-mismatch");
                if (I(zones[I(from, "zone")], "dimension") != I(zones[I(to, "zone")], "dimension")) Fail(path + ".dimension", "gen-dimension-mismatch");
                if (!linkIds.Add((RoomLevel(I(to, "index")), T(link, "id")))) Fail(path + ".id");
                foreach (var end in new[] { (Room: from, Exit: I(link, "fromExit"), Field: "fromExit"), (Room: to, Exit: I(link, "toExit"), Field: "toExit") })
                {
                    Ref(P(P(resources[I(end.Room, "resource")], "descriptor"), "exits"), end.Exit, path + "." + end.Field);
                    if (!exits.Add((I(end.Room, "index"), end.Exit))) Fail(path + "." + end.Field, "gen-connection-mismatch");
                }
                if (!Null(P(link, "resource")) && !Category(Ref(resources, I(link, "resource"), path + ".resource", "gen-resource-missing"), "connection")) Fail(path + ".resource", "gen-connection-mismatch");
            }
        }
        NoCycles(i => Null(P(rooms[i], "parentConnection")) ? null : I(connections[I(rooms[i], "parentConnection")], "fromRoom"), rooms.GetArrayLength(), i => $"connections[{I(rooms[i], "parentConnection")}].fromRoom");
        foreach (var rule in areaRules.EnumerateArray())
        {
            int index = I(rule, "index"); string path = $"areaRules[{index}]";
            var room = Ref(rooms, I(rule, "room"), path + ".room");
            var zone = zones[I(room, "zone")];
            if (!Contains(P(room, "areaRules"), index) || !Contains(P(zone, "areaRules"), index)) Fail(path + ".room", "gen-area-identity");
            var actual = P(P(resources[I(room, "resource")], "descriptor"), "areas");
            var sources = P(rule, "sourceAreas");
            if (sources.GetArrayLength() != actual.GetArrayLength() || sources.EnumerateArray().Where((value, i) => !IdentityEquals(value, actual[i])).Any()) Fail(path + ".sourceAreas", "gen-area-identity");
            var order = P(rule, "order").EnumerateArray().Select(v => (int)v.GetDouble()).ToArray();
            if (order.Length != actual.GetArrayLength() || order.Distinct().Count() != actual.GetArrayLength() || order.Any(i => i >= actual.GetArrayLength())) Fail(path + ".order", "gen-area-order");
            if (P(rule, "seeds").GetArrayLength() != actual.GetArrayLength()) Fail(path + ".seeds", "gen-area-seed-count");
        }
        var sequenceKeys = new HashSet<(int Zone, string Kind)>();
        foreach (var sequence in sequences.EnumerateArray())
        {
            int sequenceIndex = I(sequence, "index"); string path = $"sequences[{sequenceIndex}]";
            Ref(zones, I(sequence, "zone"), path + ".zone");
            string kind = T(sequence, "kind");
            var elements = P(sequence, "elements");
            if (!sequenceKeys.Add((I(sequence, "zone"), kind)) || elements.GetArrayLength() == 0) Fail(path);
            if (kind == "room") for (int elementIndex = 0; elementIndex < elements.GetArrayLength(); elementIndex++)
            {
                var element = elements[elementIndex]; string elementPath = $"{path}.elements[{elementIndex}]";
                var target = Ref(resources, I(element, "resource"), elementPath + ".resource", "gen-resource-missing");
                if (!Category(target, "room", "custom-geomorph", "elevator")) Fail(elementPath + ".resource", "gen-resource-missing");
                var targetDescriptor = P(target, "descriptor");
                var sourceKeys = new HashSet<(string Id, string Revision)>();
                int sequenceZone = I(sequence, "zone");
                var zoneResources = rooms.EnumerateArray().Where(room => I(room, "zone") == sequenceZone).Select(room => I(room, "resource")).ToHashSet();
                var addresses = P(element, "addresses");
                for (int addressIndex = 0; addressIndex < addresses.GetArrayLength(); addressIndex++)
                {
                    var address = addresses[addressIndex]; string addressPath = $"{elementPath}.addresses[{addressIndex}]";
                    var sourceMatches = resources.EnumerateArray().Where(resource => zoneResources.Contains(I(resource, "index")) && IdentityEquals(P(resource, "descriptor"), P(address, "source"))).ToArray();
                    if (sourceMatches.Length != 1 || !sourceKeys.Add((T(P(address, "source"), "id"), T(P(address, "source"), "revision")))) Fail(addressPath + ".source", "gen-sequence-address-unsupported");
                    var sourceDescriptor = P(sourceMatches[0], "descriptor");
                    var sourceAreas = P(sourceDescriptor, "areas"); var targetAreas = P(targetDescriptor, "areas"); var areas = P(address, "areas");
                    var areaSources = new HashSet<(string Id, string Revision)>(); var areaTargets = new HashSet<(string Id, string Revision)>();
                    if (areas.GetArrayLength() != sourceAreas.GetArrayLength() || areas.GetArrayLength() != targetAreas.GetArrayLength()) Fail(addressPath + ".areas", "gen-sequence-address-unsupported");
                    for (int mapIndex = 0; mapIndex < areas.GetArrayLength(); mapIndex++)
                    {
                        var map = areas[mapIndex];
                        var source = P(map, "source"); var mapped = P(map, "target");
                        if (!areaSources.Add((T(source, "id"), T(source, "revision"))) || !areaTargets.Add((T(mapped, "id"), T(mapped, "revision"))) ||
                            !sourceAreas.EnumerateArray().Any(row => IdentityEquals(row, source)) || !targetAreas.EnumerateArray().Any(row => IdentityEquals(row, mapped))) Fail($"{addressPath}.areas[{mapIndex}]", "gen-sequence-address-unsupported");
                    }
                    var sourceExits = P(sourceDescriptor, "exits"); var targetExits = P(targetDescriptor, "exits"); var mappedExits = P(address, "exits");
                    var exitSources = new HashSet<string>(StringComparer.Ordinal); var exitTargets = new HashSet<string>(StringComparer.Ordinal);
                    if (mappedExits.GetArrayLength() != sourceExits.GetArrayLength() || mappedExits.GetArrayLength() != targetExits.GetArrayLength()) Fail(addressPath + ".exits", "gen-sequence-address-unsupported");
                    for (int mapIndex = 0; mapIndex < mappedExits.GetArrayLength(); mapIndex++)
                    {
                        var map = mappedExits[mapIndex];
                        if (!exitSources.Add(T(map, "source")) || !exitTargets.Add(T(map, "target")) || !sourceExits.EnumerateArray().Any(row => T(row, "id") == T(map, "source")) || !targetExits.EnumerateArray().Any(row => T(row, "id") == T(map, "target"))) Fail($"{addressPath}.exits[{mapIndex}]", "gen-sequence-address-unsupported");
                    }
                }
            }
            else if (kind == "connection") for (int elementIndex = 0; elementIndex < elements.GetArrayLength(); elementIndex++)
            {
                string elementPath = $"{path}.elements[{elementIndex}]";
                if (!Category(Ref(resources, (int)elements[elementIndex].GetDouble(), elementPath, "gen-resource-missing"), "connection")) Fail(elementPath, "gen-resource-missing");
            }
        }
        var kinds = new Dictionary<string, (string Table, string Stage)>(StringComparer.Ordinal)
        {
            ["load-resource"] = ("resources", "resources"),
            ["create-dimension"] = ("dimensions", "dimensions"),
            ["place-room"] = ("rooms", "rooms"),
            ["order-areas"] = ("areaRules", "areas"),
            ["connect"] = ("connections", "connections"),
            ["finish-exit"] = ("unpairedExits", "connections"),
            ["expand"] = ("zones", "expansion"),
            ["verify-cells"] = ("rooms", "ownership")
        };
        string[] stages = { "resources", "dimensions", "rooms", "areas", "connections", "expansion", "ownership" };
        var ordinals = new HashSet<(string Kind, double Ordinal)>();
        var previousByLevel = new Dictionary<int, int>();
        foreach (var operation in operations.EnumerateArray())
        {
            int index = I(operation, "index"); string path = $"operations[{index}]";
            var operationOwners = levels.EnumerateArray().Where(l => Contains(P(l, "operations"), index)).ToArray();
            if (operationOwners.Length != 1) Fail(path + ".owner", "gen-layout-owner");
            var expected = kinds[T(operation, "kind")];
            if (T(operation, "table") != expected.Table || T(operation, "stage") != expected.Stage) Fail(path + ".stage", "gen-stage-invalid");
            int now = Array.IndexOf(stages, expected.Stage);
            int levelIndex = I(operationOwners[0], "index");
            if (now < previousByLevel.GetValueOrDefault(levelIndex, -1)) Fail(path + ".stage", "gen-stage-invalid");
            previousByLevel[levelIndex] = now;
            Ref(P(root, expected.Table), I(operation, "input"), path + ".input");
            int input = I(operation, "input");
            int ownerIndex = expected.Table switch
            {
                "dimensions" => I(dimensions[input], "level"),
                "zones" => I(dimensions[I(zones[input], "dimension")], "level"),
                "rooms" => I(dimensions[I(zones[I(rooms[input], "zone")], "dimension")], "level"),
                "connections" => I(dimensions[I(zones[I(rooms[I(connections[input], "toRoom")], "zone")], "dimension")], "level"),
                "unpairedExits" => I(dimensions[I(zones[I(rooms[I(unpairedExits[input], "room")], "zone")], "dimension")], "level"),
                "areaRules" => I(dimensions[I(zones[I(rooms[I(areaRules[input], "room")], "zone")], "dimension")], "level"),
                _ => I(operationOwners[0], "index")
            };
            if (ownerIndex != I(operationOwners[0], "index")) Fail(path + ".owner", "gen-layout-owner");
            Ref(authorMap, I(operation, "author"), path + ".author");
            if (!ordinals.Add((T(operation, "kind"), P(operation, "ordinal").GetDouble()))) Fail(path + ".ordinal");
            foreach (int prerequisite in Indexes(P(operation, "prerequisites"), operations.GetArrayLength(), path + ".prerequisites"))
                if (prerequisite >= index) Fail(path + ".prerequisites", "gen-source-cycle");
        }
        foreach (var level in levels.EnumerateArray())
        {
            int index = I(level, "index"); string path = $"levels[{index}]";
            ParentRange("zones", path, P(level, "zones"), zones, i => I(dimensions[I(zones[i], "dimension")], "level") == index);
            ParentRange("rooms", path, P(level, "rooms"), rooms, i => Contains(P(level, "zones"), I(rooms[i], "zone")));
            ParentRange("connections", path, P(level, "connections"), connections, i => Contains(P(level, "rooms"), I(connections[i], "toRoom")));
        }
        foreach (var author in authorMap.EnumerateArray())
        {
            int index = I(author, "index"); string path = $"authorMap[{index}]";
            if (!IdentityEquals(P(author, "work"), P(root, "source"))) Fail(path + ".work", "gen-pin-mismatch");
            // A row that names its dimension resolves its zone, room and connection inside that dimension's level.
            int? level = Null(P(author, "dimensionId")) ? null
                : dimensions.EnumerateArray().Where(row => T(row, "id") == T(author, "dimensionId")).Select(row => (int?)I(row, "level")).FirstOrDefault();
            bool Owned(string table, int row) => level == null || OwnerLevel(table, row) == level;
            foreach (var pair in new[] { (Name: "dimensionId", Table: "dimensions"), (Name: "zoneId", Table: "zones"), (Name: "roomId", Table: "rooms"), (Name: "connectionId", Table: "connections") })
                if (!Null(P(author, pair.Name)) && !P(root, pair.Table).EnumerateArray().Any(row => T(row, "id") == T(author, pair.Name) && Owned(pair.Table, I(row, "index")))) Fail(path + "." + pair.Name, "gen-reference-missing");
            if (!Null(P(author, "exitId")) || !Null(P(author, "areaId")))
            {
                var matches = rooms.EnumerateArray().Where(row => !Null(P(author, "roomId")) && T(row, "id") == T(author, "roomId") && Owned("rooms", I(row, "index"))).ToArray();
                if (matches.Length == 0) Fail(path + ".roomId", "gen-reference-missing");
                var descriptor = P(resources[I(matches[0], "resource")], "descriptor");
                if ((!Null(P(author, "exitId")) && !P(descriptor, "exits").EnumerateArray().Any(e => T(e, "id") == T(author, "exitId"))) ||
                    (!Null(P(author, "areaId")) && !P(descriptor, "areas").EnumerateArray().Any(e => T(e, "id") == T(author, "areaId")))) Fail(path + ".descriptor", "gen-reference-missing");
            }
        }
    }
}

/// <summary>The runtime a plan may run on: its id and installed version (contract 1.4: the plan's major must match and its lowest minor must not be above it).</summary>
internal sealed record GenerationAdmissionPins(string RuntimeId, string RuntimeVersion);

/// <summary>The package reader supplies these captured bytes and link evidence. No filesystem access occurs at admission.</summary>
internal sealed class GenerationNativeFile
{
    private readonly byte[] bytes;
    public string Path { get; }
    public bool IsLink { get; }
    public string OwnerPackage { get; }
    public ReadOnlyMemory<byte> Bytes => bytes;
    public GenerationNativeFile(string path, byte[] bytes, bool isLink, string ownerPackage)
    {
        Path = path;
        this.bytes = (byte[])bytes.Clone();
        IsLink = isLink;
        OwnerPackage = ownerPackage;
    }
}

internal sealed record GenerationLayoutOwner(int Input, int Block, string LevelReference, int DimensionAddress, string OwnerPackage);

internal sealed class GenerationAdmissionContext
{
    public GenerationAdmissionPins Pins { get; }
    public IReadOnlyList<GenerationNativeFile> Files { get; }
    public IReadOnlyList<GenerationLayoutOwner> LayoutOwners { get; }
    public GenerationAdmissionContext(GenerationAdmissionPins pins, IEnumerable<GenerationNativeFile> files, IEnumerable<GenerationLayoutOwner> layoutOwners)
    {
        Pins = pins;
        Files = Array.AsReadOnly(files.ToArray());
        LayoutOwners = Array.AsReadOnly(layoutOwners.ToArray());
    }
}

internal static class MapGenerationPlanAdmission
{
    public static void Admit(MapGenerationPlan plan, GenerationAdmissionContext context)
    {
        MapGenerationPlanReader.Validate(plan);
        var root = plan.Root;
        var pins = context.Pins;
        var runtime = root.GetProperty("runtime");
        if (runtime.GetProperty("id").GetString() != pins.RuntimeId ||
            !ForgeVersion.Meets(pins.RuntimeVersion, runtime.GetProperty("major").GetInt32(), runtime.GetProperty("minMinor").GetInt32()))
            throw new GenerationRefusal("gen-pin-mismatch", "pins");
        foreach (var input in plan.NativeInputs.EnumerateArray())
        {
            string path = input.GetProperty("path").GetString()!;
            var matches = context.Files.Where(f => f.Path == path).ToArray();
            if (matches.Length != 1) throw new GenerationRefusal("gen-reference-missing", path);
            var file = matches[0];
            if (file.IsLink) throw new GenerationRefusal("gen-format", path);
            if (file.OwnerPackage != input.GetProperty("ownerPackage").GetString()) throw new GenerationRefusal("gen-layout-owner", path);
            string hash = ForgeCore.Hash.Sha256Hex(file.Bytes.Span);
            if (file.Bytes.Length != input.GetProperty("byteLength").GetDouble() || hash != input.GetProperty("sha256").GetString())
                throw new GenerationRefusal("gen-pin-mismatch", path);
        }
        foreach (var zone in plan.Zones.EnumerateArray())
        {
            var dimension = plan.Dimensions[zone.GetProperty("dimension").GetInt32()];
            var level = plan.Levels[dimension.GetProperty("level").GetInt32()];
            var layout = zone.GetProperty("layout");
            int input = layout.GetProperty("input").GetInt32(), block = layout.GetProperty("block").GetInt32();
            string? ownerPackage = plan.NativeInputs[input].GetProperty("ownerPackage").GetString();
            int count = context.LayoutOwners.Count(owner => owner.Input == input && owner.Block == block &&
                owner.LevelReference == level.GetProperty("reference").GetString() &&
                owner.DimensionAddress == dimension.GetProperty("nativeAddress").GetInt32() && owner.OwnerPackage == ownerPackage);
            if (count != 1) throw new GenerationRefusal("gen-layout-owner", "layoutOwners");
        }
    }
}








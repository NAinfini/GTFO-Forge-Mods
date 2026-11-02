using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ForgeOrdnance;

/// <summary>Definitions that passed validation plus one message per entry that did not.</summary>
internal sealed class ParseResult
{
    public List<OrdnanceDefinition> Definitions { get; } = new();
    public List<string> Errors { get; } = new();
}

/// <summary>Parses one definition file: a JSON array of definition objects. An invalid entry is reported with the file name,
/// its index and the field, and skipped; the other entries still load.</summary>
internal static class DefinitionParser
{
    public const int MaxCodeLength = 8;
    public const int MaxIdLength = 64;
    public const double MaxCooldown = 3600;
    public const int MaxUses = 99;
    public const double MaxCallInDelay = 120;
    public const int MaxDescriptionLength = 400;
    public const int MaxStats = 6;
    public const int MaxStatNameLength = 16;
    public const int MaxStatBoxes = 10;
    public const int DefaultStatBoxes = 5;

    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly string[] Fields = { "Id", "Name", "Callable", "Category", "Icon", "Description", "Stats", "Code", "Cooldown", "Uses", "CallInDelay", "BeamColour", "Payload" };
    /// <summary>What only a player-called definition has; a definition with <c>"Callable": false</c> must not give any of these.</summary>
    private static readonly string[] CallerFields = { "Category", "Code", "Cooldown", "Uses", "CallInDelay", "Stats", "BeamColour" };
    private static readonly string[] StatFields = { "Name", "Value", "Max" };

    public static ParseResult ParseFile(string fileName, string json, PayloadRegistry payloads, CategoryRegistry categories)
    {
        var result = new ParseResult();
        JsonDocument document;
        try { document = JsonDocument.Parse(json, Options); }
        catch (JsonException error) { result.Errors.Add(fileName + ": invalid JSON: " + error.Message); return result; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                result.Errors.Add(fileName + ": the file must be a JSON array of definitions.");
                return result;
            }
            var index = 0;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var where = fileName + " [" + index++ + "]";
                try { result.Definitions.Add(ParseEntry(fileName, entry, payloads, categories)); }
                catch (FieldException error) { result.Errors.Add(where + " " + error.Field + ": " + error.Message); }
            }
        }
        return result;
    }

    private sealed class FieldException : Exception
    {
        internal FieldException(string field, string message) : base(message) => Field = field;
        internal string Field { get; }
    }

    /// <summary>"author.name" or longer: at least two dot-separated segments of ASCII letters, digits, '_' or '-'. The first
    /// segment is the author's namespace, so packs from different authors cannot pick the same Id by accident.</summary>
    public static bool IsNamespacedId(string id)
    {
        var segments = 1;
        var length = 0;
        foreach (var c in id)
        {
            if (c == '.')
            {
                if (length == 0) return false;
                segments++;
                length = 0;
                continue;
            }
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')) return false;
            length++;
        }
        return length > 0 && segments >= 2;
    }

    private static OrdnanceDefinition ParseEntry(string fileName, JsonElement entry, PayloadRegistry payloads, CategoryRegistry categories)
    {
        if (entry.ValueKind != JsonValueKind.Object) throw new FieldException("(entry)", "must be an object.");
        foreach (var member in entry.EnumerateObject())
            if (Array.IndexOf(Fields, member.Name) < 0) throw new FieldException(member.Name, "unknown field.");

        var id = RequiredString(entry, "Id");
        if (id.Trim() != id || id.Any(char.IsWhiteSpace)) throw new FieldException("Id", "must not contain spaces.");
        if (id.Length > MaxIdLength) throw new FieldException("Id", "is longer than " + MaxIdLength + " characters.");
        if (!IsNamespacedId(id)) throw new FieldException("Id", "'" + id + "' must start with the author's namespace: segments of letters, digits, '_' or '-' joined by dots, at least two (author.name).");
        var name = entry.TryGetProperty("Name", out _) ? RequiredString(entry, "Name") : id;
        var callable = true;
        if (entry.TryGetProperty("Callable", out var callableElement))
        {
            if (callableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FieldException("Callable", "must be true or false.");
            callable = callableElement.GetBoolean();
        }
        if (!callable)
            foreach (var field in CallerFields)
                if (entry.TryGetProperty(field, out _)) throw new FieldException(field, "must not be given when Callable is false: players never call this entry, only a level's Drops or Place do.");
        var categoryId = "";
        var categoryBeam = 0u;
        if (callable)
        {
            var categoryText = RequiredString(entry, "Category");
            if (!categories.TryGet(categoryText, out var category))
                throw new FieldException("Category", "'" + categoryText + "' is not one of " + string.Join(", ", categories.Ordered.Select(c => c.Id)) + ".");
            categoryId = category.Id;
            categoryBeam = category.BeamColour;
        }
        var icon = entry.TryGetProperty("Icon", out _) ? RequiredString(entry, "Icon") : "";
        if (icon.Length != 0 && !IsIconFileName(icon))
            throw new FieldException("Icon", "must be the file name of a .png next to this definition file, without folders.");
        var description = entry.TryGetProperty("Description", out _) ? RequiredString(entry, "Description") : "";
        if (description.Length > MaxDescriptionLength) throw new FieldException("Description", "is longer than " + MaxDescriptionLength + " characters.");
        var stats = ParseStats(entry);
        var code = callable ? ParseCode(entry) : Array.Empty<Direction>();
        var cooldown = callable ? Number(entry, "Cooldown", 0, MaxCooldown) : 0;
        var usesNumber = entry.TryGetProperty("Uses", out _) ? Number(entry, "Uses", 1, MaxUses) : OrdnanceDefinition.UnlimitedUses;
        if (usesNumber != Math.Floor(usesNumber)) throw new FieldException("Uses", "must be a whole number.");
        var delay = callable ? Number(entry, "CallInDelay", 0, MaxCallInDelay) : 0;
        uint? beamColour = null;
        if (entry.TryGetProperty("BeamColour", out _))
        {
            var text = RequiredString(entry, "BeamColour");
            if (!TryParseColour(text, out var rgb)) throw new FieldException("BeamColour", "'" + text + "' must be a colour written #RRGGBB (six hex digits).");
            beamColour = rgb;
        }

        if (!entry.TryGetProperty("Payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new FieldException("Payload", "is required and must be an object.");
        var type = RequiredString(payload, "Type", "Payload.Type");
        if (!payloads.TryGet(type, out var parser))
            throw new FieldException("Payload.Type", "'" + type + "' is not a registered payload type (known: " + string.Join(", ", payloads.Types.OrderBy(t => t, StringComparer.Ordinal)) + ").");
        object parameters;
        try { parameters = parser.Parse(payload); }
        catch (ForgeCore.StrictJsonException error) { throw new FieldException("Payload." + error.Field, error.Message); }
        if (parameters == null) throw new FieldException("Payload", "the '" + type + "' parser returned no parameters.");
        return new OrdnanceDefinition(id, name, categoryId, categoryBeam, icon, description, code, cooldown, (int)usesNumber, delay, type, payload.Clone(), parameters, fileName, beamColour, stats, callable);
    }

    /// <summary>Optional <c>Stats</c>: up to <see cref="MaxStats"/> rows of <c>{ "Name", "Value", "Max" }</c>; <c>Max</c> defaults
    /// to <see cref="DefaultStatBoxes"/>.</summary>
    private static OrdnanceStat[] ParseStats(JsonElement entry)
    {
        if (!entry.TryGetProperty("Stats", out var stats)) return Array.Empty<OrdnanceStat>();
        if (stats.ValueKind != JsonValueKind.Array) throw new FieldException("Stats", "must be an array of { \"Name\", \"Value\", \"Max\" } rows.");
        var rows = new List<OrdnanceStat>();
        foreach (var row in stats.EnumerateArray())
        {
            var label = "Stats[" + rows.Count + "]";
            if (rows.Count == MaxStats) throw new FieldException("Stats", "has more than " + MaxStats + " rows.");
            if (row.ValueKind != JsonValueKind.Object) throw new FieldException(label, "must be an object.");
            foreach (var member in row.EnumerateObject())
                if (Array.IndexOf(StatFields, member.Name) < 0) throw new FieldException(label + "." + member.Name, "unknown field.");
            var name = RequiredString(row, "Name", label + ".Name");
            if (name.Length > MaxStatNameLength) throw new FieldException(label + ".Name", "is longer than " + MaxStatNameLength + " characters.");
            var max = row.TryGetProperty("Max", out _) ? WholeNumber(row, "Max", label + ".Max", 1, MaxStatBoxes) : DefaultStatBoxes;
            var value = WholeNumber(row, "Value", label + ".Value", 0, max);
            rows.Add(new OrdnanceStat(name, value, max));
        }
        return rows.ToArray();
    }

    private static int WholeNumber(JsonElement owner, string field, string label, int min, int max)
    {
        if (!owner.TryGetProperty(field, out var value)) throw new FieldException(label, "is required.");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new FieldException(label, "must be a whole number.");
        if (number < min || number > max) throw new FieldException(label, number + " is outside " + min + ".." + max + ".");
        return number;
    }

    /// <summary><c>#RRGGBB</c>, hex digits in either case, to 0xRRGGBB.</summary>
    public static bool TryParseColour(string text, out uint rgb)
    {
        rgb = 0;
        return text.Length == 7 && text[0] == '#'
               && uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }

    /// <summary>A plain file name ending in <c>.png</c>: no folder parts, no surrounding spaces.</summary>
    public static bool IsIconFileName(string icon)
        => icon.Trim() == icon && icon.Length > 4 && icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
           && icon.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && !icon.StartsWith("..", StringComparison.Ordinal);

    private static string RequiredString(JsonElement owner, string field, string? label = null)
    {
        label ??= field;
        if (!owner.TryGetProperty(field, out var value)) throw new FieldException(label, "is required.");
        if (value.ValueKind != JsonValueKind.String) throw new FieldException(label, "must be a string.");
        var text = value.GetString()!;
        if (text.Length == 0) throw new FieldException(label, "must not be empty.");
        return text;
    }

    private static double Number(JsonElement owner, string field, double min, double max)
    {
        if (!owner.TryGetProperty(field, out var value)) throw new FieldException(field, "is required.");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number))
            throw new FieldException(field, "must be a finite number.");
        if (number < min || number > max)
            throw new FieldException(field, number.ToString(CultureInfo.InvariantCulture) + " is outside " + min.ToString(CultureInfo.InvariantCulture) + ".." + max.ToString(CultureInfo.InvariantCulture) + ".");
        return number;
    }

    private static Direction[] ParseCode(JsonElement entry)
    {
        if (!entry.TryGetProperty("Code", out var code)) throw new FieldException("Code", "is required.");
        if (code.ValueKind != JsonValueKind.Array) throw new FieldException("Code", "must be an array of Up, Down, Left, Right.");
        var steps = new List<Direction>();
        foreach (var step in code.EnumerateArray())
        {
            var text = step.ValueKind == JsonValueKind.String ? step.GetString() : null;
            switch (text)
            {
                case "Up": steps.Add(Direction.Up); break;
                case "Down": steps.Add(Direction.Down); break;
                case "Left": steps.Add(Direction.Left); break;
                case "Right": steps.Add(Direction.Right); break;
                default: throw new FieldException("Code", "step " + steps.Count + " must be exactly Up, Down, Left or Right.");
            }
        }
        if (steps.Count == 0 || steps.Count > MaxCodeLength) throw new FieldException("Code", "needs 1.." + MaxCodeLength + " steps, has " + steps.Count + ".");
        return steps.ToArray();
    }
}

/// <summary>The definitions every peer must agree on: unique Ids, collision-free keys and one set hash.</summary>
internal sealed class DefinitionSet
{
    private readonly Dictionary<uint, OrdnanceDefinition> _byKey = new();
    private readonly Dictionary<string, OrdnanceDefinition> _byId = new(StringComparer.Ordinal);

    private DefinitionSet() { }

    public IReadOnlyList<OrdnanceDefinition> Definitions { get; private set; } = Array.Empty<OrdnanceDefinition>();
    public ulong Hash { get; private set; }
    public static DefinitionSet Empty { get; } = Build(Array.Empty<OrdnanceDefinition>(), new List<string>());

    public bool TryGet(uint key, out OrdnanceDefinition definition) => _byKey.TryGetValue(key, out definition!);
    public bool TryGetById(string id, out OrdnanceDefinition definition) => _byId.TryGetValue(id, out definition!);

    /// <summary>Later duplicates and key collisions are reported and left out. Input order decides which duplicate wins, so
    /// callers pass files in a fixed order (sorted by name).</summary>
    public static DefinitionSet Build(IEnumerable<OrdnanceDefinition> definitions, List<string> errors)
    {
        var set = new DefinitionSet();
        foreach (var d in definitions)
        {
            if (set._byId.TryGetValue(d.Id, out var same))
            {
                errors.Add(d.SourceFile + ": Id '" + d.Id + "' is already defined in " + same.SourceFile + "; this entry is skipped.");
                continue;
            }
            if (set._byKey.TryGetValue(d.Key, out var clash))
            {
                errors.Add(d.SourceFile + ": Id '" + d.Id + "' has the same network hash as '" + clash.Id + "' (" + clash.SourceFile + "); rename one. This entry is skipped.");
                continue;
            }
            set._byId.Add(d.Id, d);
            set._byKey.Add(d.Key, d);
        }
        set.Definitions = set._byId.Values.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        set.Hash = Hashing.SetHash(set.Definitions);
        return set;
    }
}

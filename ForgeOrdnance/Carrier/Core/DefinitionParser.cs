using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ForgeCore;

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
                catch (StrictJsonException error) { result.Errors.Add(where + " " + error.Field + ": " + error.Message); }
                // A payload parser or gear lookup of another mod that throws something else costs only this entry, never the file's other entries.
                catch (Exception error) { result.Errors.Add(where + ": could not be read, skipped: " + error.Message); }
            }
        }
        return result;
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
        if (entry.ValueKind != JsonValueKind.Object) throw new StrictJsonException("(entry)", "must be an object.");
        foreach (var member in entry.EnumerateObject())
            if (Array.IndexOf(Fields, member.Name) < 0) throw new StrictJsonException(member.Name, "unknown field.");

        var id = StrictJson.String(entry, "", "Id", null);
        if (id.Trim() != id || id.Any(char.IsWhiteSpace)) throw new StrictJsonException("Id", "must not contain spaces.");
        if (id.Length > MaxIdLength) throw new StrictJsonException("Id", "is longer than " + MaxIdLength + " characters.");
        if (!IsNamespacedId(id)) throw new StrictJsonException("Id", "'" + id + "' must start with the author's namespace: segments of letters, digits, '_' or '-' joined by dots, at least two (author.name).");
        var name = StrictJson.String(entry, "", "Name", id);
        var callable = StrictJson.Bool(entry, "", "Callable", true);
        if (!callable)
            foreach (var field in CallerFields)
                if (entry.TryGetProperty(field, out _)) throw new StrictJsonException(field, "must not be given when Callable is false: players never call this entry, only a level's Drops or Place do.");
        var categoryId = "";
        var categoryBeam = 0u;
        if (callable)
        {
            var categoryText = StrictJson.String(entry, "", "Category", null);
            if (!categories.TryGet(categoryText, out var category))
                throw new StrictJsonException("Category", "'" + categoryText + "' is not one of " + string.Join(", ", categories.Ordered.Select(c => c.Id)) + ".");
            categoryId = category.Id;
            categoryBeam = category.BeamColour;
        }
        var icon = entry.TryGetProperty("Icon", out _) ? StrictJson.String(entry, "", "Icon", null) : "";
        if (icon.Length != 0 && !IsIconFileName(icon))
            throw new StrictJsonException("Icon", "must be the file name of a .png next to this definition file, without folders.");
        var description = entry.TryGetProperty("Description", out _) ? StrictJson.String(entry, "", "Description", null) : "";
        if (description.Length > MaxDescriptionLength) throw new StrictJsonException("Description", "is longer than " + MaxDescriptionLength + " characters.");
        var stats = ParseStats(entry);
        var code = callable ? ParseCode(entry) : Array.Empty<Direction>();
        var cooldown = callable ? StrictJson.Number(entry, "", "Cooldown", null, 0, MaxCooldown) : 0;
        var usesNumber = StrictJson.Integer(entry, "", "Uses", OrdnanceDefinition.UnlimitedUses, 1, MaxUses);
        var delay = callable ? StrictJson.Number(entry, "", "CallInDelay", null, 0, MaxCallInDelay) : 0;
        uint? beamColour = null;
        if (entry.TryGetProperty("BeamColour", out _))
        {
            var text = StrictJson.String(entry, "", "BeamColour", null);
            if (!TryParseColour(text, out var rgb)) throw new StrictJsonException("BeamColour", "'" + text + "' must be a colour written #RRGGBB (six hex digits).");
            beamColour = rgb;
        }

        if (!entry.TryGetProperty("Payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new StrictJsonException("Payload", "is required and must be an object.");
        var type = StrictJson.String(payload, "Payload.", "Type", null);
        if (!payloads.TryGet(type, out var parser))
            throw new StrictJsonException("Payload.Type", "'" + type + "' is not a registered payload type (known: " + string.Join(", ", payloads.Types.OrderBy(t => t, StringComparer.Ordinal)) + ").");
        object parameters;
        try { parameters = parser.Parse(payload); }
        catch (StrictJsonException error) { throw new StrictJsonException("Payload." + error.Field, error.Message); }
        if (parameters == null) throw new StrictJsonException("Payload", "the '" + type + "' parser returned no parameters.");
        return new OrdnanceDefinition(id, name, categoryId, categoryBeam, icon, description, code, cooldown, (int)usesNumber, delay, type, payload.Clone(), parameters, fileName, beamColour, stats, callable);
    }

    /// <summary>Optional <c>Stats</c>: up to <see cref="MaxStats"/> rows of <c>{ "Name", "Value", "Max" }</c>; <c>Max</c> defaults
    /// to <see cref="DefaultStatBoxes"/>.</summary>
    private static OrdnanceStat[] ParseStats(JsonElement entry)
    {
        if (!entry.TryGetProperty("Stats", out var stats)) return Array.Empty<OrdnanceStat>();
        if (stats.ValueKind != JsonValueKind.Array) throw new StrictJsonException("Stats", "must be an array of { \"Name\", \"Value\", \"Max\" } rows.");
        var rows = new List<OrdnanceStat>();
        foreach (var row in stats.EnumerateArray())
        {
            var label = "Stats[" + rows.Count + "]";
            if (rows.Count == MaxStats) throw new StrictJsonException("Stats", "has more than " + MaxStats + " rows.");
            if (row.ValueKind != JsonValueKind.Object) throw new StrictJsonException(label, "must be an object.");
            foreach (var member in row.EnumerateObject())
                if (Array.IndexOf(StatFields, member.Name) < 0) throw new StrictJsonException(label + "." + member.Name, "unknown field.");
            var at = label + ".";
            var name = StrictJson.String(row, at, "Name", null);
            if (name.Length > MaxStatNameLength) throw new StrictJsonException(label + ".Name", "is longer than " + MaxStatNameLength + " characters.");
            var max = (int)StrictJson.Integer(row, at, "Max", DefaultStatBoxes, 1, MaxStatBoxes);
            var value = (int)StrictJson.Integer(row, at, "Value", null, 0, max);
            rows.Add(new OrdnanceStat(name, value, max));
        }
        return rows.ToArray();
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

    private static Direction[] ParseCode(JsonElement entry)
    {
        if (!entry.TryGetProperty("Code", out var code)) throw new StrictJsonException("Code", "is required.");
        if (code.ValueKind != JsonValueKind.Array) throw new StrictJsonException("Code", "must be an array of Up, Down, Left, Right.");
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
                default: throw new StrictJsonException("Code", "step " + steps.Count + " must be exactly Up, Down, Left or Right.");
            }
        }
        if (steps.Count == 0 || steps.Count > MaxCodeLength) throw new StrictJsonException("Code", "needs 1.." + MaxCodeLength + " steps, has " + steps.Count + ".");
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

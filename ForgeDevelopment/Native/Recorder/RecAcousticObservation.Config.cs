using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ForgeDevelopment.Native;

// Kept separate so offline trace-profile validation needs no native reader, session or hook dependencies.
internal static partial class RecAcousticObservation
{
    internal sealed record Endpoint(string? From, string? Actor, string? Position, string? Node, string? Role);
    internal sealed record Config(string Layer, Endpoint? Source, Endpoint? Receiver, IReadOnlyDictionary<string, string> Fields);

    /// <summary>Accepts the acoustic object, not the whole trace entry. Omitted endpoints remain unknown.
    /// A selector is instance[.field...] or argN[.field...]. Only from permits parent.source.
    /// Fields are bounded scalar observations, not arbitrary object serialization or method invocations.</summary>
    internal static Config Parse(JsonElement json)
    {
        RequireObject(json, "acoustic");
        AllowOnly(json, "layer", "source", "receiver", "fields");
        var layer = String(json, "layer", true)!;
        if (layer is not ("player-detection" or "noise-manager" or "wakeup-propagation"))
            throw new FormatException("acoustic.layer must be player-detection, noise-manager or wakeup-propagation");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json.TryGetProperty("fields", out var configured))
        {
            RequireObject(configured, "acoustic.fields");
            foreach (var pair in configured.EnumerateObject())
            {
                if (fields.Count >= 32 || pair.Name.Length == 0 || pair.Name.Length > 80
                    || pair.Value.ValueKind != JsonValueKind.String || !fields.TryAdd(pair.Name, Selector(pair.Value.GetString()!)))
                    throw new FormatException("acoustic.fields must contain at most 32 distinct named scalar selectors");
            }
        }
        return new(layer, EndpointConfig(json, "source"), EndpointConfig(json, "receiver"), fields);
    }

    private static Endpoint? EndpointConfig(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value)) return null;
        RequireObject(value, "acoustic." + name);
        AllowOnly(value, "from", "actor", "position", "node", "role");
        var from = String(value, "from"); var actor = String(value, "actor");
        var position = String(value, "position"); var node = String(value, "node");
        var role = String(value, "role");
        if (from != null && (actor != null || position != null || node != null))
            throw new FormatException("acoustic endpoint from cannot be combined with actor, position or node");
        if (from != null && from != "parent.source") Selector(from);
        if (actor != null) Selector(actor);
        if (position != null) Selector(position);
        if (node != null) Selector(node);
        if (role != null && role is not ("player" or "enemy" or "noise-origin" or "unknown"))
            throw new FormatException("acoustic endpoint role must be player, enemy, noise-origin or unknown");
        return new(from, actor, position, node, role);
    }

    private static void RequireObject(JsonElement json, string name)
    { if (json.ValueKind != JsonValueKind.Object) throw new FormatException(name + " must be an object"); }
    private static void AllowOnly(JsonElement json, params string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in json.EnumerateObject())
            if (!names.Contains(item.Name, StringComparer.Ordinal) || !seen.Add(item.Name))
                throw new FormatException("Unsupported or repeated acoustic option: " + item.Name);
    }
    private static string? String(JsonElement json, string name, bool required = false)
    {
        if (!json.TryGetProperty(name, out var value))
        { if (required) throw new FormatException("Missing acoustic." + name); return null; }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException("acoustic." + name + " must be a nonempty string");
        return value.GetString();
    }
    private static string Selector(string text)
    {
        var steps = text.Split('.');
        if (text.Length > 256 || steps.Length > 12 || steps.Any(s => s.Length == 0
            || !(char.IsLetter(s[0]) || s[0] == '_') || s.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
            || !(steps[0] == "instance" || steps[0].StartsWith("arg", StringComparison.Ordinal)
                && int.TryParse(steps[0][3..], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0))
            throw new FormatException("Invalid acoustic selector: " + text);
        return text;
    }

}

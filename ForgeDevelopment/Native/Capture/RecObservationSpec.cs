using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ForgeDevelopment.Native;

internal sealed record RecObservationSpec(string Id, string Hash, string Trigger, string Profile, string Entry,
    IReadOnlyList<string> Fields, bool Hierarchy, bool Materials, bool ShaderProperties,
    int MaximumRows, int MaximumDepth, int MaximumSteps, int MaximumBytes)
{
    internal static IReadOnlyDictionary<string, RecObservationSpec> Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024) throw new InvalidDataException("observation-catalog-size");
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, RecObservationSpec>(StringComparer.Ordinal);
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "forge-capture-readbacks.v1") throw new InvalidDataException("observation-catalog-format");
        if (!root.TryGetProperty("observations", out var definitions)) return result;
        if (definitions.ValueKind != JsonValueKind.Object) throw new InvalidDataException("observations-object-required");
        foreach (var property in definitions.EnumerateObject())
        {
            if (result.Count >= 64 || property.Name.Length is < 1 or > 128 || result.ContainsKey(property.Name))
                throw new InvalidDataException("observation-id");
            var spec = property.Value;
            Keys(spec, "schemaVersion", "observationId", "trigger", "selector", "projection", "limits");
            if (spec.GetProperty("schemaVersion").GetString() != ForgeCore.FormatVersion.Current || spec.GetProperty("observationId").GetString() != property.Name)
                throw new InvalidDataException("observation-schema-or-id");
            var trigger = spec.GetProperty("trigger");
            var kind = trigger.GetProperty("kind").GetString();
            string profile = "", entry = "";
            if (kind == "manual") Keys(trigger, "kind");
            else if (kind == "after-returned")
            {
                Keys(trigger, "kind", "profileId", "traceEntryId");
                profile = Text(trigger, "profileId"); entry = Text(trigger, "traceEntryId");
            }
            else throw new InvalidDataException("observation-trigger-not-admitted");
            var selector = spec.GetProperty("selector");
            Keys(selector, "kind");
            if (selector.GetProperty("kind").GetString() != "entity") throw new InvalidDataException("observation-selector-not-admitted");
            var projection = spec.GetProperty("projection");
            Keys(projection, "adapter", "fields", "hierarchy", "materials", "shaderProperties");
            if (projection.GetProperty("adapter").GetString() != "unity-model") throw new InvalidDataException("observation-adapter-not-admitted");
            var fields = projection.GetProperty("fields").EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
            if (fields.Length is < 1 or > 3 || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length
                || fields.Any(field => field is not ("name" or "activeSelf" or "layer")))
                throw new InvalidDataException("observation-field-not-admitted");
            var limits = spec.GetProperty("limits");
            Keys(limits, "rows", "depth", "steps", "outputBytes");
            var hierarchy = projection.GetProperty("hierarchy").GetBoolean();
            var materials = projection.GetProperty("materials").GetBoolean();
            var shaderProperties = projection.GetProperty("shaderProperties").GetBoolean();
            if (shaderProperties && !materials) throw new InvalidDataException("observation-shader-requires-materials");
            var hash = ForgeCore.Hash.Sha256Hex(spec.GetRawText());
            result.Add(property.Name, new(property.Name, hash, kind!, profile, entry, Array.AsReadOnly(fields),
                hierarchy, materials, shaderProperties, Limit(limits, "rows", 4096),
                Limit(limits, "depth", 64), Limit(limits, "steps", 16384), Limit(limits, "outputBytes", 1024 * 1024)));
        }
        return result;
    }

    private static int Limit(JsonElement value, string name, int maximum)
    {
        var number = value.GetProperty(name).GetInt32();
        if (number < 1 || number > maximum) throw new InvalidDataException("observation-limit: " + name);
        return number;
    }

    private static string Text(JsonElement value, string name)
    {
        var text = value.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256) throw new InvalidDataException("observation-trigger-identity");
        return text;
    }

    private static void Keys(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("observation-object-required");
        var remaining = new HashSet<string>(keys, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new InvalidDataException("observation-unknown-or-duplicate: " + property.Name);
        if (remaining.Count != 0) throw new InvalidDataException("observation-missing: " + string.Join(",", remaining));
    }
}

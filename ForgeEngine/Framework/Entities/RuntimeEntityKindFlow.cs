using System;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>Load-time proof that forwarding glue does not erase its producer's entity kind, and that a kind a
/// written map object address carries is declared where only an address can type a port.
///
/// Both checks are declaration rules, not readings: which kind a category's address names is the provider's own
/// grammar — this assembly holds no category token, exactly as it holds no coordinate rule for an address
/// (`RuntimePlan.TriggerAddress`) — so a port whose address was written stays deferred here and the consumer's
/// declared kinds, plus the provider's own refusal of a reference it does not own, are what hold at run time; a port
/// whose address was not written is unbound (`RuntimeGraphContracts.EntityKindsNarrow`).</summary>
internal static class RuntimeEntityKindFlow
{
    internal static void Validate(JsonElement graph, string id)
    {
        var inputs = RuntimeJson.Rows(graph, "inputs");
        foreach (var input in inputs)
        {
            RuntimeJson.Require(!input.TryGetProperty("entityKindsFrom", out _), "port-kind-flow", id);
            RuntimeJson.Require(!input.TryGetProperty("entityKindsFromParameter", out _), "port-kind-flow", id);
        }
        var parameters = RuntimeJson.Rows(graph, "parameters");
        foreach (var output in RuntimeJson.Rows(graph, "outputs"))
        {
            if (output.TryGetProperty("entityKindsFrom", out var field))
            {
                RuntimeJson.Require(RuntimeJson.Text(output, "type") == "entity" && field.ValueKind == JsonValueKind.String
                    && !output.TryGetProperty("entityKinds", out _), "port-kind-flow", id);
                var name = field.GetString();
                RuntimeJson.Require(inputs.Any(p => RuntimeJson.Text(p, "id") == name && RuntimeJson.Text(p, "type") == "entity"), "port-kind-flow", id);
            }
            // A kind named by an address parameter is the same deferral one step further: the port declares the
            // parameter whose category types it and no bound of its own, only an `object-address` parameter may be
            // named, and the parameter has to exist. Mirrors Web/src/forge/graph-schema.ts validateGraphMetadata.
            if (!output.TryGetProperty("entityKindsFromParameter", out var kindParameter)) continue;
            RuntimeJson.Require(RuntimeJson.Text(output, "type") == "entity" && kindParameter.ValueKind == JsonValueKind.String
                && !output.TryGetProperty("entityKinds", out _), "port-kind-flow", id);
            var parameterName = kindParameter.GetString();
            RuntimeJson.Require(RuntimeGraphContracts.IsName(parameterName!), "port-kind-flow", id);
            RuntimeJson.Require(parameters.Any(p => RuntimeJson.Text(p, "id") == parameterName
                && RuntimeJson.Text(p, "type") == "object-address"), "port-kind-flow", id);
        }
    }

    /// <summary>The kind a target filter's `kind` member narrows its forwarded `targets` to, or null when the row is
    /// not a filter or keeps every kind. The narrowing is proved by the handler, not assumed: a candidate of another
    /// kind refuses the whole query (`recipient-kind`) instead of being passed on. Mirrors
    /// Web/src/forge/entity-kind-flow.ts resolveEntityKindFlow, which lowered the wires this loader judges.</summary>
    internal static string? FilterKind(string capabilityId, JsonElement parameters)
        => capabilityId == Blocks.SelectorTargetFilter.Id && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
            ? ForgeEngine.Trigger.Targeting.RecipientFilterRequest.EntityKindOf(kind.GetString())
            : null;

    internal static JsonElement Resolve(JsonElement graph, Func<string, JsonElement?> producer, string? narrowed = null)
    {
        if (!RuntimeJson.Rows(graph, "outputs").Any(port => port.TryGetProperty("entityKindsFrom", out _))) return graph;
        var fields = graph.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        fields["outputs"] = RuntimeJson.From(RuntimeJson.Rows(graph, "outputs").Select(port =>
        {
            if (!port.TryGetProperty("entityKindsFrom", out var source)) return port;
            var resolved = port.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            resolved.Remove("entityKindsFrom"); resolved.Remove("entityKinds");
            if (narrowed != null && RuntimeJson.Text(port, "type") == "entity")
            {
                resolved["entityKinds"] = RuntimeJson.From(new[] { narrowed });
                return RuntimeJson.From(resolved);
            }
            var origin = producer(source.GetString()!);
            if (RuntimeJson.Text(port, "type") == "entity" && origin is JsonElement value)
            {
                if (value.TryGetProperty("entityKinds", out var kinds)) resolved["entityKinds"] = kinds.Clone();
                // A producer whose kind a written address names forwards that deferral, not "every kind": the glue
                // carries exactly what its producer carries, and the producer's kind is the provider's to name.
                // On a resolved contract the field marks the deferral only; its parameter is the producer's.
                else if (RuntimeGraphContracts.KindsDeferred(value))
                    resolved["entityKindsFromParameter"] = value.GetProperty("entityKindsFromParameter").Clone();
            }
            return RuntimeJson.From(resolved);
        }).ToArray());
        return RuntimeJson.From(fields);
    }
}

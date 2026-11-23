using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The one place this package reads and writes the `objective` resource id grammar: the reference that names one
/// objective layer.
///
/// An objective has no per-instance native identity — the objective machine keeps three fixed layer slots and no
/// id of its own — so a layer's own name is the whole identity, and the id is `layer:<name>`. The grammar lives
/// here rather than in each contract that publishes or reads an objective reference because the objective action
/// rows compare the reference a plan wired against the layer they write: comparing two spellings of one identity
/// is only meaningful while there is one spelling, and a second parser would be a second answer to "which layer
/// is this".
///
/// <see cref="LayerOf"/> reads the grammar and nothing else. Whether the name it returns is a layer a row carries
/// is the caller's own vocabulary, matched against the layer list that caller declares, so no second copy of that
/// list lives here.
/// </summary>
internal static class ObjectiveLayerReference
{
    /// <summary>The resource kind every objective reference belongs to: the shared vocabulary's own name, which
    /// the level's objective resource provider registers and the objective rows declare as their recipient
    /// kind.</summary>
    public const string Kind = "objective";

    /// <summary>The prefix every objective reference id starts with.</summary>
    public const string Prefix = "layer:";

    /// <summary>The id one layer is named by.</summary>
    public static string Id(string layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return Prefix + layer;
    }

    /// <summary>The layer name one objective reference id names, or null when the id is not one this package
    /// writes. An id that carries the prefix and no name answers the empty text, which no layer list matches, so
    /// a caller comparing it against its own vocabulary refuses it rather than folding it into a layer.</summary>
    public static string? LayerOf(string? referenceId)
        => referenceId != null && referenceId.StartsWith(Prefix, StringComparison.Ordinal)
            ? referenceId[Prefix.Length..]
            : null;

    /// <summary>The index of the layer one objective reference names in a row's own layer list, or -1. Every
    /// objective action reads its layer from its recipient this way, so the row carries one spelling of it.</summary>
    public static int LayerIndex(ResourceRef reference, IReadOnlyList<string> layers)
    {
        if (reference.ResourceKind != Kind || LayerOf(reference.ResourceId) is not { } layer) return -1;
        for (var index = 0; index < layers.Count; index++)
            if (string.Equals(layers[index], layer, StringComparison.Ordinal)) return index;
        return -1;
    }

    /// <summary>The id an evaluator's objective input carries. A frame that has resolved the reference hands over
    /// the `resourceKind`/`resourceId` pair and a compiled frame hands over the id alone; a pair of another kind is
    /// refused by the caller's own code so a map reference is not read as an objective.</summary>
    public static string? InputId(EvaluationContext context, HandlerSlot slot, string port, string kindCode)
    {
        if (context.TryResource(slot, out var resolved)) return KindChecked(resolved, kindCode);
        var value = context.RequiredInput(port);
        if (value.ValueKind != JsonValueKind.Object) throw new RuntimeContractException(kindCode, "The input is not a resource reference.");
        if (value.TryGetProperty("resourceKind", out _)) return KindChecked(RuntimeJson.ResourceRefOf(value), kindCode);
        return value.TryGetProperty("id", out var compiled) && compiled.ValueKind == JsonValueKind.String
            ? compiled.GetString()
            : throw new RuntimeContractException("missing-field", port);
    }

    private static string KindChecked(ResourceRef reference, string kindCode)
        => reference.ResourceKind == Kind ? reference.ResourceId
            : throw new RuntimeContractException(kindCode, "This row reads an objective, not a " + reference.ResourceKind);
}

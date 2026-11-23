using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The two reads every `condition` value row of this provider makes before it answers: the resource id the row's
/// own input port carries, and one port of the reading the game-bound reader answered with. The level-object
/// scan row and the map-object generator row read their subject exactly the same way, so both go through this
/// one class instead of each half carrying its own copy of the two checks.
/// </summary>
internal static class ValueRowReading
{
    /// <summary>The resource id one input port of an evaluation context carries, or null when the port is
    /// absent, is not an object, or names no string id.</summary>
    internal static string? ResourceId(EvaluationContext context, string port)
        => context.Inputs.ValueKind == JsonValueKind.Object && context.Inputs.TryGetProperty(port, out var value)
            && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("resourceId", out var id)
            && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    /// <summary>One port the reader's answer must carry. A reading that is not an object, or one that left the
    /// port out, is a refusal by name rather than an empty value: a row that cannot be read must not look like a
    /// row that read nothing.</summary>
    internal static JsonElement Require(JsonElement reading, string port, string id)
    {
        if (reading.ValueKind != JsonValueKind.Object || !reading.TryGetProperty(port, out var value))
            throw new RuntimeContractException("reading-incomplete", id + "." + port);
        return value;
    }
}

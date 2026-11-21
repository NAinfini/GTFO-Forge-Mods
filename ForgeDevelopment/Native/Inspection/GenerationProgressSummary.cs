using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>The progress of the map generator as the Development report records it: a fixed, bounded set of scalars read
/// from the generator's own readback (status and counts against the plan). This is what lets a report written while the
/// level is still generating, or after it was cut off, say how far the generator got. A readback that is too large to parse, or that is
/// missing a field, is named incomplete and is never read as a partial success.</summary>
internal static class GenerationProgressSummary
{
    internal const int MaximumReadbackChars = 16 * 1024 * 1024;

    private static readonly string[] Counts = { "Rooms", "Dimensions", "Zones", "Connections", "Exits", "Expansions" };

    /// <summary>Whether the generator reported this status as a final one (nothing more will happen in this world).</summary>
    internal static bool Final(string status) => status is "complete" or "interrupted" or "refused";

    /// <summary>The summary line and the status, or a line starting with <c>status=unreadable</c> that names why the readback
    /// could not be summarized. A world without a generation plan answers "vanilla" and has no progress to record.</summary>
    internal static (string Status, string Line) Summarize(string readback)
    {
        if (readback.Length > MaximumReadbackChars)
            return ("unreadable", "status=unreadable;reason=readback-over-capacity;chars=" + readback.Length.ToString(CultureInfo.InvariantCulture));
        try
        {
            using var document = JsonDocument.Parse(readback);
            var root = document.RootElement;
            string status = Text(root, "status") ?? throw new JsonException("status");
            var line = new StringBuilder("status=").Append(status);
            if (status is "vanilla" or "unavailable")
                return (status, line.Append(";reason=").Append(Text(root, "reason") ?? "none").ToString());
            Add(line, "factoryDone", root, "factoryDone");
            Add(line, "level", root, "level");
            Add(line, "worldEpoch", root, "worldEpoch");
            foreach (var name in Counts)
                line.Append(';').Append(name.ToLowerInvariant()).Append('=')
                    .Append(Number(root, "actual" + name)).Append('/').Append(Number(root, "expected" + name));
            int rows = root.TryGetProperty("readbacks", out var readbacks) && readbacks.ValueKind == JsonValueKind.Array ? readbacks.GetArrayLength() : 0;
            line.Append(";readbackRows=").Append(rows.ToString(CultureInfo.InvariantCulture));
            return (status, line.ToString());
        }
        catch (JsonException error)
        {
            return ("unreadable", "status=unreadable;reason=readback-malformed;field=" + error.Message);
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "?";

    private static void Add(StringBuilder line, string key, JsonElement element, string name)
    {
        line.Append(';').Append(key).Append('=');
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) line.Append("none");
        else line.Append(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
    }
}

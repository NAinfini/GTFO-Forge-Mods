using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeDevelopment.Native;

// Development-only: read Forge Engine's own variable store (its author read AuthorReads.Variables), so a behavior's final variable
// value is observed, not inferred from step results. Without the Engine the read answers forge-engine-not-installed.
internal static partial class AuthoringGameDrive
{
    private const int SummaryValues = 16, SummaryValueLength = 64;

    private static ExperimentCallResult ReadVariables(SpatialRecord record, JsonElement spec)
    {
        SpatialFields(spec, "schemaVersion", "prefix");
        RequireHostLevel();
        string prefix = spec.GetProperty("prefix").GetString() ?? "";
        using var document = JsonDocument.Parse(EngineLookup.Call<string>("Variables", prefix));
        int matched = 0;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            matched++;
            string name = row.GetProperty("name").GetString()!, subject = row.GetProperty("subject").GetString()!;
            var valueNode = row.GetProperty("value");
            string value = valueNode.ValueKind == JsonValueKind.String ? valueNode.GetString()! : valueNode.GetRawText();
            record.Samples.Add(new { name, scope = row.GetProperty("scope").GetString(), subject, type = row.GetProperty("type").GetString(), value });
            // The receipt carries the first SummaryValues values (each cut to SummaryValueLength) so a script can judge them; a
            // per-player row is keyed by name@subject. The record keeps every row whole.
            string key = subject.Length == 0 ? name : name + "@" + subject;
            if (values.Count < SummaryValues && !values.ContainsKey(key))
                values[key] = value.Length <= SummaryValueLength ? value : value.Substring(0, SummaryValueLength);
        }
        record.After = new { world = RecTime.WorldEpoch, frame = RecTime.Frame, entries = document.RootElement.GetProperty("entries").GetInt32(), matched };
        record.Summary = new { matched, shown = values.Count, values };
        record.NativePath = "ForgeEngine.Authoring.AuthorReads.Variables";
        return record.Result("drive.variables-readback");
    }
}


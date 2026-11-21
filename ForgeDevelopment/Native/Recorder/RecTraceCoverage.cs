using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;

namespace ForgeDevelopment.Native;

// Admission evidence is independent of rate-limited call records. The complete
// list is written as one document through the recorder's existing writer.
internal static class RecTraceCoverage
{
    internal const string FileName = "trace-coverage.forge";
    private sealed record Row(string Profile, string Type, string Method, string Signature, string Reason,
        string Category, string AlternativeObservation, string ObservationScope, string[] Owners, int Attempts);
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Profile, MethodBase Method, string Reason), Row> Rows = new();
    private static bool _changed;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static string Signature(MethodBase method)
    {
        try { return method.ToString() ?? method.Name; }
        catch (TypeLoadException) { return method.Name + " [unreadable-signature]"; }
    }

    internal static bool Note(string profile, MethodBase method, string reason, string scope = "unavailable")
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            var key = (profile, method, reason);
            Rows.TryGetValue(key, out var previous);
            var category = Category(reason);
            Rows[key] = new Row(profile, method.DeclaringType?.FullName ?? "unknown", method.Name, Signature(method), reason,
                category, Alternative(category), scope, Harmony.GetPatchInfo(method)?.Owners.ToArray() ?? Array.Empty<string>(),
                (previous?.Attempts ?? 0) + 1);
            _changed = true;
            return previous == null;
        }
    }

    internal static void ClearTraces()
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            foreach (var key in Rows.Keys.Where(k => k.Profile != "fixed-hooks").ToArray()) Rows.Remove(key);
            _changed = true;
        }
    }

    internal static string Report
    {
        get { using (RecGate.Enter(Gate, "recorder.lock-timeout")) return JsonSerializer.Serialize(Snapshot(), JsonOptions); }
    }

    private static Row[] Snapshot() => Rows.Values.OrderBy(r => r.Profile, StringComparer.Ordinal)
        .ThenBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Signature, StringComparer.Ordinal)
        .ThenBy(r => r.Reason, StringComparer.Ordinal).ToArray();

    private static string Category(string reason)
    {
        var separator = reason.IndexOfAny(new[] { ':', ' ', '=' });
        return separator < 0 ? reason : reason.Substring(0, separator);
    }

    private static string Alternative(string category) => category switch
    {
        "native-hook-runtime-shared-target" => "subscribe-to-existing-runtime-observation",
        "shared-native-method-body" => "observe-the-canonical-method-owner",
        "unsupported-native-prologue" => "use-reviewed-supported-caller-or-existing-runtime-event",
        "unsupported-parameter" or "unsupported-native-return" => "add-reviewed-typed-observer-or-use-existing-runtime-event",
        _ => "use-reviewed-supported-caller-or-existing-runtime-event"
    };

    // Refusals that follow from the game binary and the wildcard profiles, not from a fault: bodies the compiler folded
    // together, prologues the detour cannot copy, abstract or stripped bodies, signature-only entries the tracer declines
    // to guess, and targets another owner already observes. The same build refuses the same targets on every start and
    // trace-coverage.forge lists each one. Any other reason (a replaced detour, a failed installation, an unreadable method)
    // is still an Error.
    private static readonly HashSet<string> Inherent = new(StringComparer.Ordinal)
    {
        "shared-native-method-body", "unsupported-native-prologue", "short-or-branch-native-body",
        "native-hook-runtime-shared-target", "native-body-unavailable", "native-signature-review-required"
    };

    /// <summary>Writes the refusal list as it stands into a session that was just renewed. The diagnostics for it already
    /// went out when it was published; a list not published yet is written by its own <see cref="Publish"/>.</summary>
    internal static void Rewrite()
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (_changed || Rows.Count == 0) return;
            RecSession.WriteDocument(FileName, JsonSerializer.SerializeToUtf8Bytes(Snapshot(), JsonOptions));
        }
    }

    internal static void Publish(string phase)
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (!_changed) return;
            var rows = Snapshot();
            var reasons = rows.GroupBy(r => r.Category).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key + ":" + g.Count());
            var queued = RecSession.WriteDocument(FileName, JsonSerializer.SerializeToUtf8Bytes(rows, JsonOptions));
            var expectedOnly = rows.Length != 0 && rows.All(r => Inherent.Contains(r.Category));
            var report = "development.trace-coverage phase=" + phase + " refused=" + rows.Length
                    + " wrapper=" + rows.Count(r => r.ObservationScope == "managed-wrapper")
                    + " unavailable=" + rows.Count(r => r.ObservationScope == "unavailable")
                    + " reasons=" + string.Join(",", reasons) + " report=" + FileName + " reportQueued=" + queued;
            var reasonText = string.Join(",", reasons);
            var fields = new[]
            {
                new RecDiag.Field("phase", phase), new RecDiag.Field("refused", (long)rows.Length),
                new RecDiag.Field("wrapper", (long)rows.Count(r => r.ObservationScope == "managed-wrapper")),
                new RecDiag.Field("unavailable", (long)rows.Count(r => r.ObservationScope == "unavailable")),
                new RecDiag.Field("reasons", reasonText.Length <= 240 ? reasonText : reasonText.Substring(0, 237) + "..."),
                new RecDiag.Field("report", FileName), new RecDiag.Field("reportQueued", queued)
            };
            if (!queued)
            {
                RecDiag.Record(true, "trace.coverage", report + "; recorder document unavailable; complete list retained in trace report", fields);
                return;
            }
            if (rows.Length != 0) RecDiag.Record(!expectedOnly, "trace.coverage", report, fields);
            _changed = false;
        }
    }
}

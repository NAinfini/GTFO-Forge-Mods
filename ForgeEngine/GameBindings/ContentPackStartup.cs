using System;
using System.Diagnostics;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.Logging;

namespace ForgeEngine.GameBindings;

/// <summary>The content entries' part of the first content generation (contract I-RELOAD): <see cref="Scan"/> is its discovery,
/// <see cref="Admit"/> its admission and <see cref="Dispatch"/> its commit, after provider registration closed. Parsed content
/// without a consumer is refused rather than advertised as game-loaded.</summary>
internal static class ContentPackStartup
{
    private static long _started, _scanned, _dispatched;
    private static int _entries, _accepted, _rejected;

    internal static ContentCatalog Scan(string bepInExRoot)
    {
        _started = Stopwatch.GetTimestamp();
        var catalog = ContentPackDiscovery.Scan(bepInExRoot);
        _scanned = Stopwatch.GetTimestamp();
        return catalog;
    }

    /// <summary>The package identity preflight against the Rundowns the host callbacks captured (startup only).</summary>
    internal static ContentCatalog Admit(RuntimeKernel kernel, ContentCatalog catalog) => ContentPackHostStartup.CheckPackageIdentities(kernel, catalog);

    /// <summary>One generation's dispatch of the admitted entries to the kernel's domain consumers: the plan admission over the
    /// accepted entries, and the accepted-content snapshot the host callbacks are dispatched with.</summary>
    internal static (ContentPlanAdmission Admission, AdmittedContentSnapshot Snapshot) Dispatch(RuntimeKernel kernel, string bepInExRoot, ContentCatalog catalog)
    {
        foreach (var rejection in catalog.Rejections)
            Rejected(kernel, rejection.Code, rejection.Entry, rejection.Other, rejection.Path, rejection.Detail);
        var accepted = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        // The startup line counts every refusal this run wrote, not only the discovery-stage rows: a dispatch-stage
        // rejection is a rejected entry too, and accepted + rejected must describe the discovered set.
        var rejected = catalog.Rejections.Count();
        var entries = catalog.Accepted.Select(row => new RuntimeContentEntry(row.Entry.Id, row.Entry.Domain,
            row.Entry.Name, row.Entry.References, row.Entry.ContentJson, row.Entry.Path, row.PersistentId, row.BlockIds)).ToArray();
        kernel.BeginContentDiscovery();
        kernel.DispatchContent(entries, (entry, result) =>
        {
            if (!result.Accepted)
            {
                rejected++;
                Rejected(kernel, result.Code, entry.Id, result.Other, entry.Path);
                if (result.Code == "content-native-state-unknown")
                    throw new InvalidOperationException("Native content rollback failed; restart before loading plans.");
                return;
            }
            accepted.Add(entry.Id);
            if (!kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
            kernel.LogRuntime(RuntimeLogLevel.Info, RuntimeHostLogCodes.ContentLoaded, new RuntimeLogFacts
            {
                Values = new[]
                {
                    RuntimeLogValue.Of("code", result.Code), RuntimeLogValue.Quote("entry", entry.Id),
                    RuntimeLogValue.Of("entryHash", Hash(entry.Id)), RuntimeLogValue.Quote("path", entry.Path),
                    RuntimeLogValue.Of("pathHash", Hash(entry.Path)), RuntimeLogValue.Of("domain", entry.Domain),
                    RuntimeLogValue.Of("persistentId", (long)entry.PersistentId)
                }
            });
        });
        _dispatched = Stopwatch.GetTimestamp();
        _entries = entries.Length; _accepted = accepted.Count; _rejected = rejected;
        // Freeze only consumer-accepted primary Item records after strict native installation. The host callbacks
        // (the Map participant's commit) may publish Rundowns with it; lifecycle observers remain read-only.
        return (new ContentPlanAdmission(bepInExRoot, kernel.Identity, catalog, accepted), new AdmittedContentSnapshot(catalog, accepted));
    }

    /// <summary>`content.startup`: the first generation's entry counts and how long its scan, its dispatch and the participants
    /// after the dispatch (the host callbacks and the plan index) took.</summary>
    internal static void LogStartup(RuntimeKernel kernel)
    {
        if (!kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        var finished = Stopwatch.GetTimestamp();
        static long Milliseconds(long from, long to) => (to - from) * 1000 / Stopwatch.Frequency;
        kernel.LogRuntime(RuntimeLogLevel.Info, RuntimeHostLogCodes.ContentStartup, new RuntimeLogFacts
        {
            Values = new[]
            {
                RuntimeLogValue.Of("entries", (long)_entries),
                RuntimeLogValue.Of("accepted", (long)_accepted),
                RuntimeLogValue.Of("rejected", (long)_rejected),
                RuntimeLogValue.Of("scanMs", Milliseconds(_started, _scanned)),
                RuntimeLogValue.Of("dispatchMs", Milliseconds(_scanned, _dispatched)),
                RuntimeLogValue.Of("hostMs", Milliseconds(_dispatched, finished)),
                RuntimeLogValue.Of("elapsedMs", Milliseconds(_started, finished))
            }
        });
    }

    // Native reasons need more space than identity previews. Keep both ends within the SDK value limit.
    // Reuse Quote's account detection before and after bounding; controls never reach the message.
    private static string RejectionReason(string reason)
    {
        if (RuntimeLogValue.AccountShaped(reason)) return "account-shaped";
        const int limit = 256;
        const string marker = " ...[truncated]... ";
        if (reason.Length > limit)
            reason = reason.Substring(0, (limit - marker.Length) / 2) + marker
                + reason.Substring(reason.Length - (limit - marker.Length + 1) / 2);
        if (RuntimeLogValue.AccountShaped(reason)) return "account-shaped";
        return string.Create(reason.Length, reason, static (span, source) =>
        {
            for (int index = 0; index < source.Length; index++)
                span[index] = source[index] < ' ' || source[index] == '\u007f' ? ' ' : source[index];
        });
    }

    internal static void Rejected(RuntimeKernel kernel, string code, string entry, string? other, string path, string? detail = null)
    {
        other ??= "";
        if (!kernel.RuntimeLogs(RuntimeLogLevel.Error)) return;
        kernel.LogRuntime(RuntimeLogLevel.Error, RuntimeHostLogCodes.ContentRejected, new RuntimeLogFacts
        {
            Detail = detail,
            Values = new[]
            {
                RuntimeLogValue.Of("code", code), RuntimeLogValue.Quote("entry", entry), RuntimeLogValue.Of("entryHash", Hash(entry)),
                RuntimeLogValue.Of("other", RejectionReason(other)), RuntimeLogValue.Of("otherHash", Hash(other)),
                RuntimeLogValue.Quote("path", path), RuntimeLogValue.Of("pathHash", Hash(path))
            }
        });
    }

    // Display quotes are bounded and sanitized; these content addresses identify the complete original text.
    private static string Hash(string text)
    {
        if (text.Length == 0) return "";
        var hex = ForgeCore.Hash.Sha256Hex(text);
        // Eight hex digits per group retain the exact digest without ever producing an account-shaped digit run.
        return string.Join("-", Enumerable.Range(0, 8).Select(index => hex.Substring(index * 8, 8)));
    }
}

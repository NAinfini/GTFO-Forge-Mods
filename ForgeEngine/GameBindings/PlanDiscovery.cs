using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ForgeEngine.Framework;
using Microsoft.Win32.SafeHandles;

namespace ForgeEngine.GameBindings;

/// <summary>Startup's plan index: every readable plan file's head and digest, and the files discovery refused.</summary>
internal sealed record PlanIndex(IReadOnlyList<PlanIndexEntry> Entries, IReadOnlyList<PlanCandidate> Refused)
{
    /// <summary>The digest each indexed file had at startup, by path.</summary>
    internal IReadOnlyDictionary<string, string> Digests { get; } = Entries.ToDictionary(entry => entry.Path, entry => entry.Digest, StringComparer.Ordinal);

    /// <summary>The files one level loads: the plans restricted to it and the unrestricted ones, plus every other copy
    /// of a plan identity among them, so admission still sees an identity conflict that crosses levels.</summary>
    internal HashSet<string> LevelFiles(string? level)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
            if (entry.Loads(level) && entry.PlanId != null) identities.Add(entry.PlanId);
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
            if (entry.Loads(level) || entry.PlanId != null && identities.Contains(entry.PlanId)) files.Add(entry.Path);
        return files;
    }
}

/// <summary>I-PACK plan discovery. Snapshot each plan independently; admission checks the complete batch.</summary>
internal static class PlanDiscovery
{
    /// <summary>A plan that cannot be read is refused with the plan-path code when its path is unsafe and with the
    /// invalid-json code for every other failure, the size cap having its own.</summary>
    private static readonly PublishedTextCodes Codes = new("plan-path", "invalid-json", "json-size", "invalid-json", "invalid-json");

    /// <summary>Startup's read: every plan file hashed and its head indexed, nothing decoded or kept.</summary>
    internal static PlanIndex Index(string bepInExRoot, RuntimeKernel? kernel = null)
    {
        var refused = new List<PlanCandidate>();
        var entries = new List<PlanIndexEntry>();
        Read<PlanIndexEntry>(Find(bepInExRoot, null), kernel, "index", refused, hit =>
        {
            PublishedResourceFailure? failure = null;
            if (!PublishedResources.TryReadPlanIndex(hit, FrameworkFiles.MaximumPlanBytes, Codes, refusal => failure = refusal, out var entry))
                return (PlanCandidate.Rejected(hit.Relative, failure!.Value.Code, failure.Value.Reason), null);
            return (default, entry);
        }, (candidate, entry) => { if (entry != null) entries.Add(entry); else refused.Add(candidate); });
        entries.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return new PlanIndex(entries, refused);
    }

    /// <summary>The full read of the plan files <paramref name="include"/> names (every file when null): each file is
    /// decoded, parsed, validated and hashed here and nowhere again.</summary>
    internal static IReadOnlyList<PlanCandidate> Scan(string bepInExRoot, RuntimeKernel? kernel = null, IReadOnlySet<string>? include = null)
        => Scan(Find(bepInExRoot, include), kernel, include == null ? "all" : "level");

    /// <summary>A level's read: the files are found on the calling thread, which owns the Runtime and so may write the
    /// discovery's <c>content.read</c> record, and only the pure byte work (decode, parse, validate, hash) runs off it,
    /// where nothing touches the kernel and nothing is logged.</summary>
    internal static Task<IReadOnlyList<PlanCandidate>> ReadLevel(string bepInExRoot, IReadOnlySet<string> include)
    {
        PlanFiles files;
        try { files = Find(bepInExRoot, include); }
        catch (Exception error) { return Task.FromException<IReadOnlyList<PlanCandidate>>(error); }
        return Task.Run(() => Scan(files, null, "level"));
    }

    private static IReadOnlyList<PlanCandidate> Scan(PlanFiles files, RuntimeKernel? kernel, string stage)
    {
        var candidates = new List<PlanCandidate>();
        Read<PlanDocument>(files, kernel, stage, candidates, hit =>
        {
            PublishedResourceFailure? failure = null;
            // One read: the file is decoded, parsed, validated and hashed here and nowhere again.
            if (!PublishedResources.TryReadPlan(hit, FrameworkFiles.MaximumPlanBytes, Codes, refusal => failure = refusal, out var plan))
                return (PlanCandidate.Rejected(hit.Relative, failure!.Value.Code, failure.Value.Reason), null);
            return (PlanCandidate.FromDocument(hit.Relative, plan!), plan);
        }, (candidate, _) => candidates.Add(candidate));
        return candidates;
    }

    /// <summary>The plan files of one read, found by the install's discovery, and the paths it refused.</summary>
    private sealed record PlanFiles(string Root, IReadOnlyList<PublishedResource> Hits, IReadOnlyList<PlanCandidate> Refused);

    // The discovery hand-out writes `content.read`, a Runtime record, so it runs on the thread that owns the kernel.
    private static PlanFiles Find(string bepInExRoot, IReadOnlySet<string>? include)
    {
        var refused = new List<PlanCandidate>();
        IReadOnlyList<PublishedResource> hits = ContentPackages.Read(bepInExRoot, ContentPackages.EngineReader, "forge/plans", "*.plan.forge", Codes,
            (PublishedResourceFailure failure) =>
            {
                if (include == null || include.Contains(failure.Relative))
                    refused.Add(PlanCandidate.Rejected(failure.Relative, failure.Code, failure.Reason));
            });
        if (include != null) hits = hits.Where(hit => include.Contains(hit.Relative)).ToArray();
        return new PlanFiles(bepInExRoot, hits, refused);
    }

    // The root pinned once, each file read on its own and side by side. The outputs are handed over in discovery order
    // and the refusals sorted with them by path, which fixes the order whatever order the reads finished in.
    private static void Read<T>(PlanFiles files, RuntimeKernel? kernel, string stage,
        List<PlanCandidate> refused, Func<PublishedResource, (PlanCandidate Candidate, T? Read)> read, Action<PlanCandidate, T?> output)
        where T : class
    {
        var timer = Stopwatch.StartNew();
        long readableBytes = 0;
        refused.AddRange(files.Refused);
        var hits = files.Hits;
        var bepInExRoot = files.Root;
        SafeFileHandle? openedRoot = null;
        string? rootCode = null, rootReason = null;
        try { if (hits.Count != 0) openedRoot = PublishedFileIdentity.OpenRoot(bepInExRoot); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        { rootCode = error is InvalidDataException ? Codes.PathCode : Codes.UnreadableCode; rootReason = error.Message; }
        using var pinnedRoot = openedRoot;
        var batch = new PublishedReadBudget
        {
            Unlimited = true,
            Verdicts = new PublishedPathVerdicts(),
            RootIdentity = pinnedRoot,
            RootPath = hits.Count == 0 ? null : hits[0].Root
        };
        var results = new (PlanCandidate Candidate, T? Read)[hits.Count];
        (PlanCandidate, T?) One(PublishedResource hit)
        {
            if (rootCode != null) return (PlanCandidate.Rejected(hit.Relative, rootCode, rootReason!), null);
            if (hit.Length > FrameworkFiles.MaximumPlanBytes) return (PlanCandidate.Rejected(hit.Relative, "json-size", "Plan file exceeds 4 MiB."), null);
            // The plan parser's own per-document bound applies to every opened stream, including growth.
            // The resource/settings aggregate ledger is not a plan-set memory ceiling.
            var result = read(hit with { ReadBudget = batch });
            if (result.Read != null) Interlocked.Add(ref readableBytes, hit.Length);
            return result;
        }
        try { Parallel.For(0, hits.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, i => results[i] = One(hits[i])); }
        catch (AggregateException error) { ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw(); }
        var before = refused.Count;
        foreach (var (candidate, value) in results) output(candidate, value);
        refused.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        timer.Stop();
        if (kernel == null || !kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        var readable = results.Count(result => result.Read != null);
        kernel.LogRuntime(RuntimeLogLevel.Info, "plan.discovery", new RuntimeLogFacts
        {
            Values = new[]
            {
                RuntimeLogValue.Of("stage", stage),
                RuntimeLogValue.Of("files", (long)(hits.Count + before)),
                RuntimeLogValue.Of("readable", (long)readable),
                RuntimeLogValue.Of("rejected", (long)(hits.Count + before - readable)),
                RuntimeLogValue.Of("discoveredBytes", hits.Sum(hit => hit.Length)),
                RuntimeLogValue.Of("readableBytes", readableBytes),
                RuntimeLogValue.Of("maxPlanBytes", (long)FrameworkFiles.MaximumPlanBytes),
                RuntimeLogValue.Of("totalByteLimit", "none"),
                RuntimeLogValue.Of("elapsedMs", timer.ElapsedMilliseconds)
            }
        });
    }
}

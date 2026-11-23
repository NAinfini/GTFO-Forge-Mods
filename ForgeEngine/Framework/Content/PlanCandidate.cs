using System.Runtime.CompilerServices;

namespace ForgeEngine.Framework;

/// <summary>One discovered `.plan.forge` file, already read or already rejected by the host's filesystem
/// scan. The kernel treats the content as untrusted input regardless of origin; it never touches a filesystem itself, so
/// this is the only way work reaches <see cref="RuntimeKernel.LoadPlans"/>.</summary>
public readonly struct PlanCandidate
{
    // Candidates are the SDK's public value type and the layout of its five string fields is part of the API
    // fingerprint every plan's runtime lock names. The parsed document is therefore not a field: it lives in this table,
    // keyed by the identity of the digest string the candidate carries (a new string for every candidate), and goes
    // away with it.
    private static readonly ConditionalWeakTable<string, PlanDocument> Documents = new();

    /// <summary>The BepInEx-relative path, `/` separated. Carried on every plan.loaded/plan.rejected record for this file.</summary>
    public string Path { get; }
    // The text a caller handed to <see cref="Loaded"/>. A file read keeps no text: its document is the one reading.
    internal string? Json { get; }
    // Captured from the same read as the document. Only this digest survives admission; no handshake reads the file again.
    internal string? ContentDigest { get; }
    internal string? RejectedCode { get; }
    internal string? RejectedDetail { get; }

    private PlanCandidate(string path, string? json, string? contentDigest, string? rejectedCode, string? rejectedDetail)
    { Path = path; Json = json; ContentDigest = contentDigest; RejectedCode = rejectedCode; RejectedDetail = rejectedDetail; }

    private static PlanCandidate Of(string path, string? json, PlanDocument document)
    {
        Documents.Add(document.Digest, document);
        return new PlanCandidate(path, json, document.Digest, null, null);
    }

    /// <summary>A file the host read successfully; the kernel still validates its content from scratch.</summary>
    public static PlanCandidate Loaded(string path, string json)
    {
        RuntimeJson.Text(path);
        RuntimeJson.Require(json != null, "invalid-json", "Plan content cannot be null.");
        return Of(path, json, PlanDocument.FromText(json!));
    }

    /// <summary>A file the host's discovery already read, decoded, parsed and hashed in its one pass.</summary>
    internal static PlanCandidate FromDocument(string path, PlanDocument document)
    {
        RuntimeJson.Text(path);
        return Of(path, null, document);
    }

    /// <summary>A file the host's own scan already rejected (oversized, linked/escaping path, or discovery budget) before
    /// reading its content. <paramref name="code"/> is the reason recorded in the plan.rejected log line.</summary>
    public static PlanCandidate Rejected(string path, string code, string detail)
    {
        RuntimeJson.Text(path); RuntimeJson.Text(code); RuntimeJson.Text(detail);
        return new PlanCandidate(path, null, null, code, detail);
    }

    internal bool IsHostRejected => ContentDigest == null;

    /// <summary>The one read of this candidate's file; absent for a candidate the host refused before reading.</summary>
    internal PlanDocument? Document => ContentDigest != null && Documents.TryGetValue(ContentDigest, out var document) ? document : null;
}

/// <summary>Per-file result of <see cref="RuntimeKernel.LoadPlans"/>. The kernel has already written the matching
/// plan.loaded/plan.rejected record (when a log sink is configured) by the time this is returned; callers do not need it
/// to observe the log, only to inspect or test the outcome directly.</summary>
public readonly record struct PlanLoadOutcome(string Path, bool Loaded, string? PlanId, string? Code, string? Detail);

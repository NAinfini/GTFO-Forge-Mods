using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace ForgeDevelopment.Native;

// Reads the single current Forge project manifest, publishes its object reference receipt and
// verifies every declared source. There is no schema version and no older manifest shape: one
// unknown, duplicate or missing field rejects the whole document instead of being migrated.
internal static class ProjectChecks
{
    internal const string ManifestFormat = ProjectObjectReferences.ProjectFormat;

    // The manifest is one bounded read. Parsing and hashing share those exact bytes, so the
    // published projectManifestHash can never describe a different file than the parsed one.
    private const int MaximumManifestBytes = ProjectObjectReferences.MaximumManifestBytes;
    private const int MaximumDeclaredSources = 256;
    private const int MaximumRequiredPlugins = 128;
    private const int MaximumGuidChars = 128;
    private const int MaximumProjectIdChars = 128;
    private const int MaximumDependencyListChars = 4096;
    private const int MaximumDirectories = 256;
    private const int MaxEntries = 4096;
    private const int MaxFiles = 256;
    private const long MaximumSourceBytes = ProjectObjectReferences.MaximumManifestBytes;
    private const long MaximumTotalBytes = ProjectObjectReferences.MaximumManifestBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 32 };

    private sealed record DeclaredSource(string FullPath, string RelativePath, string Sha256);
    private sealed record Dependency(string Guid, SemanticVersioning.Version Version);
    private sealed record Manifest(string ProjectId, IReadOnlyList<(string Key, string Value)> Experiment,
        IReadOnlyList<Dependency> Dependencies, IReadOnlyList<DeclaredSource> Sources, IReadOnlyList<ProjectObjectDeclaration> References);

    private sealed class SourceCandidate
    {
        internal readonly string FullPath, RelativePath, ExpectedHash;
        internal readonly bool Declared;
        internal SourceCandidate(string fullPath, string relativePath, string expectedHash, bool declared)
        { FullPath = fullPath; RelativePath = relativePath; ExpectedHash = expectedHash; Declared = declared; }
    }

    // One manifest run owns one scan and one request. The request captures the scan it belongs to,
    // so a late worker can only ever write the run it was created for.
    private sealed class SourceRequest
    {
        private int _finished;
        internal readonly DiagnosticsReport Report;
        internal readonly ProjectObjectReferenceScan Scan;
        internal readonly DeclaredSource[] Declared;
        internal readonly string Root, Config, Plugins;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly long Started = Stopwatch.GetTimestamp();
        internal int Files;
        internal long Bytes;
        internal SourceRequest(DiagnosticsReport report, ProjectObjectReferenceScan scan, DeclaredSource[] declared, string root, string config, string plugins)
        { Report = report; Scan = scan; Declared = declared; Root = root; Config = config; Plugins = plugins; }

        // Exactly one of cancellation and completion wins; the loser must not publish anything.
        internal bool Claim() => Interlocked.CompareExchange(ref _finished, 1, 0) == 0;

        // The worker disposes the source when it ends, which can land between a caller's Claim and its Cancel; a disposed source has nothing left to cancel.
        internal void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        internal void Finish(ProjectSourceVerification verification, string coverage, string detail)
        {
            Scan.SetSourceVerification(verification);
            var elapsed = (Stopwatch.GetTimestamp() - Started) * 1000.0 / Stopwatch.Frequency;
            Report.SetMetadata("sourceSnapshotCostMs", RuntimeDiagnostics.Number(elapsed));
            Report.SetMetadata("sourceCoverageStatus", coverage);
            Report.SetMetadata("configurationFileCount", Volatile.Read(ref Files).ToString(CultureInfo.InvariantCulture));
            Report.SetMetadata("configurationBytesHashed", Interlocked.Read(ref Bytes).ToString(CultureInfo.InvariantCulture));
            Report.Check("source_coverage", "files", coverage, detail);
            Report.Event("source", "snapshot_finished", "files", new()
            {
                ["status"] = coverage,
                ["sourceVerification"] = ProjectObjectReferences.Wire(verification),
                ["files"] = Volatile.Read(ref Files).ToString(CultureInfo.InvariantCulture),
                ["bytes"] = Interlocked.Read(ref Bytes).ToString(CultureInfo.InvariantCulture)
            }, elapsed);
        }
    }

    private static readonly object SourceGate = new();
    private static SourceRequest? ActiveRequest;
    private static Task? SourceWorker;

    /// <param name="rooms">The room resolver this build installed, or null when it installed none. It is a
    /// parameter rather than a constant so the game-independent scan names no native type; production passes the
    /// one resolver, and a scan without one refuses every authored room.</param>
    internal static ProjectObjectReferenceScan? Load(DiagnosticsReport report, long worldEpoch, long? simulationTick,
        ProjectRoomResolver? rooms = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        // An invalid epoch or tick is a caller contract violation, never an unscanned-but-valid world.
        ProjectObjectReferences.ValidateEpoch(worldEpoch, simulationTick);
        // Supersede the previous run before reading anything: its request stops sampling and its
        // scan can no longer publish a result. A late worker cannot write the new run.
        CancelSourceSnapshot("Superseded by a new generation run.");
        if (string.IsNullOrWhiteSpace(Settings.ProjectManifest.Value))
        {
            report.SetMetadata("projectManifest", "none");
            return null;
        }
        var path = "";
        Manifest manifest;
        byte[] bytes;
        try
        {
            path = ManifestPath();
            bytes = ReadBounded(path);
            manifest = Validate(Parse(bytes));
        }
        catch (Exception error)
        {
            report.Issue("project_manifest_invalid", Subject(path), error.Message, error.ToString());
            var rejected = RejectScan(report, worldEpoch, simulationTick);
            report.Check("project_manifest", "configuration", "failed",
                "Manifest rejected as a whole; no field, dependency or source of it is published.");
            return rejected;
        }
        // Commit: the receipt is published first, then the already-validated checks. Nothing above
        // this line survives a rejection. One run owns exactly one scan; the source worker below
        // carries this same scan, so its result can never land on a different run's receipt.
        var scan = new ProjectObjectReferenceScan(manifest.References, worldEpoch, simulationTick,
            ProjectSourceVerification.NotProvided, rooms);
        report.AttachObjectReferences(scan);
        report.SetMetadata("projectManifest", path);
        report.SetMetadata("projectManifestHash", ForgeCore.Hash.Sha256Hex(bytes));
        report.SetMetadata("projectId", manifest.ProjectId);
        foreach (var (key, value) in manifest.Experiment) report.SetMetadata(key, value);
        report.Check("project_manifest", "configuration", "parsed",
            $"Format {ManifestFormat}; {manifest.Dependencies.Count} required plugins, {manifest.Sources.Count} declared sources, {manifest.References.Count} object references. No game generation changed.");
        foreach (var dependency in manifest.Dependencies)
        {
            var installed = IL2CPPChainloader.Instance.Plugins.TryGetValue(dependency.Guid, out var plugin);
            var version = installed ? plugin!.Metadata.Version : null;
            var satisfied = version != null && version.CompareTo(dependency.Version) == 0;
            report.Check("dependency_version", dependency.Guid, satisfied ? "version_satisfied" : "failed",
                "Required exactly " + dependency.Version + "; installed " + (version?.ToString() ?? "none") +
                ". Version satisfaction does not prove behavior compatibility.");
        }
        BeginSourceSnapshot(report, scan, manifest.Sources, simulationTick);
        return scan;
    }

    // The receipt of a rejected manifest: no declarations and no source result that could be read
    // as a success. The scan's own state machine only leaves Pending for a terminal result, and
    // Reject converts a pending source result into unavailable.
    private static ProjectObjectReferenceScan RejectScan(DiagnosticsReport report, long worldEpoch, long? simulationTick)
    {
        var scan = new ProjectObjectReferenceScan(Array.Empty<ProjectObjectDeclaration>(), worldEpoch, simulationTick,
            ProjectSourceVerification.NotProvided);
        scan.SetSourceVerification(ProjectSourceVerification.Pending);
        scan.Reject();
        report.AttachObjectReferences(scan);
        return scan;
    }

    private static string ManifestPath()
    {
        var configured = Settings.ProjectManifest.Value;
        try
        {
            var full = Path.GetFullPath(configured, Paths.BepInExRootPath);
            RejectReparseRoot(full, configured);
            return full;
        }
        catch (Exception error) when (error is not InvalidDataException)
        { throw new InvalidDataException("Manifest path is invalid: " + error.Message, error); }
    }

    private static string Subject(string path)
    {
        if (path.Length == 0) return "configuration";
        try { return Path.GetFileName(path) ?? "configuration"; }
        catch (ArgumentException) { return "configuration"; }
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
        if (stream.Length > MaximumManifestBytes)
            throw new InvalidDataException($"Project manifest exceeds the {MaximumManifestBytes / 1024 / 1024} MiB limit.");
        var bytes = new byte[stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read <= 0) throw new InvalidDataException("Project manifest ended before its recorded length; the file changed while being read.");
            offset += read;
        }
        if (stream.ReadByte() >= 0) throw new InvalidDataException("Project manifest grew while being read.");
        return bytes;
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text;
        try { text = StrictUtf8.GetString(bytes, start, bytes.Length - start); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Project manifest is not valid UTF-8.", error); }
        try { return JsonDocument.Parse(text, DocumentOptions); }
        catch (JsonException error) { throw new InvalidDataException("Project manifest is not strict JSON: " + error.Message, error); }
    }

    // Everything is validated into local values first; nothing is published until the entire
    // document has been accepted.
    private static Manifest Validate(JsonDocument document)
    {
        using (document)
        {
            var root = document.RootElement;
            Require(root, "format", "projectId", "experiment", "requiredPlugins", "sources", "objectReferences");
            var format = ProjectObjectReferences.ReadText(root.GetProperty("format"), 64, 64);
            if (!string.Equals(format, ManifestFormat, StringComparison.Ordinal))
                throw new InvalidDataException($"Unsupported project format: {format}.");
            var projectId = ReadProjectId(root.GetProperty("projectId"));
            var dependencies = ReadDependencies(root.GetProperty("requiredPlugins"));
            var sources = ReadSources(root.GetProperty("sources"));
            var references = ProjectObjectReferences.Parse(root.GetProperty("objectReferences"));
            return new Manifest(projectId, ReadExperiment(root.GetProperty("experiment")), dependencies, sources, references);
        }
    }

    // A project ID is an authored identity, not free text: single line, no control characters.
    private static string ReadProjectId(JsonElement value)
    {
        var projectId = ProjectObjectReferences.ReadText(value, MaximumProjectIdChars, MaximumProjectIdChars * 4);
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidDataException("projectId must not be blank.");
        return projectId;
    }

    // The exporter joins the exact dependency revisions into one string and exports "" when the
    // project has none. It is recorded verbatim and never re-split: requiredPlugins is the only
    // source of runnable dependency identity.
    private static IReadOnlyList<(string Key, string Value)> ReadExperiment(JsonElement experiment)
    {
        Require(experiment, "packageVersion", "authoringSha256", "dependencies");
        var packageVersion = ProjectObjectReferences.ReadText(experiment.GetProperty("packageVersion"), 64, 64);
        var authoringSha256 = ProjectObjectReferences.ReadSha256(experiment.GetProperty("authoringSha256"));
        var dependencies = ReadStringAllowEmpty(experiment.GetProperty("dependencies"),
            MaximumDependencyListChars, MaximumDependencyListChars * 4);
        return new List<(string, string)>(3)
        {
            ("experiment:packageVersion", packageVersion),
            ("experiment:authoringSha256", authoringSha256),
            ("experiment:dependencies", dependencies)
        }.AsReadOnly();
    }

    private static IReadOnlyList<Dependency> ReadDependencies(JsonElement required)
    {
        if (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > MaximumRequiredPlugins)
            throw new InvalidDataException($"requiredPlugins must be an array of at most {MaximumRequiredPlugins} entries.");
        var result = new List<Dependency>(required.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in required.EnumerateArray())
        {
            Require(entry, "guid", "version");
            var guid = ProjectObjectReferences.ReadText(entry.GetProperty("guid"), MaximumGuidChars, MaximumGuidChars * 4);
            if (string.IsNullOrWhiteSpace(guid)) throw new InvalidDataException("A required plugin GUID must not be blank.");
            var version = ProjectObjectReferences.ReadText(entry.GetProperty("version"), 64, 64);
            if (!seen.Add(guid)) throw new InvalidDataException("Duplicate required plugin: " + guid + ".");
            result.Add(new Dependency(guid, StrictVersion(version)));
        }
        return result.AsReadOnly();
    }

    // Strictly x.y.z. A prerelease, build metadata, a leading "v" or a shortened version is not a
    // plugin version this contract accepts.
    private static SemanticVersioning.Version StrictVersion(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 3) throw new InvalidDataException("Required plugin version must be x.y.z: " + value + ".");
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Any(character => character is < '0' or > '9'))
                throw new InvalidDataException("Required plugin version must be numeric x.y.z: " + value + ".");
            if (part.Length > 1 && part[0] == '0')
                throw new InvalidDataException("Required plugin version components must not have leading zeros: " + value + ".");
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var component) && component <= 999_999) continue;
            throw new InvalidDataException("Required plugin version component is out of range: " + value + ".");
        }
        return new SemanticVersioning.Version(value);
    }

    private static IReadOnlyList<DeclaredSource> ReadSources(JsonElement sources)
    {
        if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > MaximumDeclaredSources)
            throw new InvalidDataException($"sources must be an array of at most {MaximumDeclaredSources} entries.");
        var result = new List<DeclaredSource>(sources.GetArrayLength());
        var root = Path.GetFullPath(Paths.BepInExRootPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.EnumerateArray())
        {
            Require(source, "path", "sha256", "kind");
            var relative = ProjectObjectReferences.ReadText(source.GetProperty("path"), 1024, 4096);
            var hash = ProjectObjectReferences.ReadSha256(source.GetProperty("sha256"));
            var kind = ProjectObjectReferences.ReadText(source.GetProperty("kind"), 16, 16);
            if (kind is not ("DataBlock" or "ModConfig"))
                throw new InvalidDataException("Unsupported source kind: " + kind + ".");
            if (!seen.Add(relative)) throw new InvalidDataException("Duplicate declared source path: " + relative + ".");
            var full = CanonicalPath(root, relative);
            RejectReparsePath(full, root, relative);
            result.Add(new DeclaredSource(full, relative, hash));
        }
        return result.AsReadOnly();
    }

    // Declared source paths are authored identities: canonical, relative to BepInEx, forward
    // slashes only, and never able to leave the BepInEx root.
    private static string CanonicalPath(string root, string relative)
    {
        if (relative[0] is '/' or '\\') throw new InvalidDataException("Source path must be relative to BepInEx: " + relative + ".");
        if (relative.Contains('\\')) throw new InvalidDataException("Source path must use forward slashes: " + relative + ".");
        if (relative.Contains(':')) throw new InvalidDataException("Source path must not contain a drive or stream separator: " + relative + ".");
        if (relative.Contains("//")) throw new InvalidDataException("Source path must not contain an empty segment: " + relative + ".");
        foreach (var segment in relative.Split('/'))
            if (segment is "" or "." or "..") throw new InvalidDataException("Source path must not contain empty, current or parent segments: " + relative + ".");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source path escapes the BepInEx root: " + relative + ".");
        return full;
    }

    private static void RejectReparseRoot(string fullPath, string displayPath)
    {
        var root = Path.GetFullPath(Paths.BepInExRootPath);
        if (!Directory.Exists(root)) return;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("BepInEx root cannot be a reparse point when reading the project manifest: " + displayPath);
    }

    private static void RejectReparsePath(string fullPath, string rootPath, string displayPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) return;
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("BepInEx root cannot be a reparse point when validating project sources: " + displayPath);
        foreach (var segment in Path.GetRelativePath(root, fullPath).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Project source cannot traverse a reparse point: " + displayPath);
        }
    }

    private static void Require(JsonElement value, params string[] fields) => ProjectObjectReferences.RequireObject(value, fields);

    // The authoritative reader rejects empty strings; the exporter legitimately writes "" for a
    // project without dependencies, so this field alone also accepts an empty string.
    private static string ReadStringAllowEmpty(JsonElement value, int maximumChars, int maximumUtf8Bytes)
    {
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Expected a string.");
        var text = value.GetString()!;
        if (text.Length > maximumChars || text.Any(char.IsControl))
            throw new InvalidDataException("String is too long or contains control characters.");
        try
        {
            if (StrictUtf8.GetByteCount(text) > maximumUtf8Bytes) throw new InvalidDataException("String byte budget exceeded.");
        }
        catch (EncoderFallbackException error) { throw new InvalidDataException("Invalid Unicode string.", error); }
        return text;
    }

    // Cancels the run that owns the current request and its scan. A request that already finished
    // stays finished, and a late worker cannot revive it.
    internal static void CancelSourceSnapshot(string reason = "Runtime diagnostics stopped.")
    {
        SourceRequest? request;
        lock (SourceGate) request = ActiveRequest;
        if (request == null || !request.Claim()) return;
        request.Cancel();
        request.Scan.Cancel(null);
        request.Report.Check("source_coverage", "files", "cancelled", reason);
    }

    // The request carries the run's own scan: the reference declarations and the source result are
    // two states of one receipt, never two receipts. Pending is entered only when a source result
    // is actually in flight, because the scan's state machine cannot leave Pending for
    // not_provided again.
    private static void BeginSourceSnapshot(DiagnosticsReport report, ProjectObjectReferenceScan scan,
        IReadOnlyList<DeclaredSource> sources, long? simulationTick)
    {
        if (sources.Count > 0) scan.SetSourceVerification(ProjectSourceVerification.Pending);
        var request = new SourceRequest(report, scan, sources.ToArray(), Paths.BepInExRootPath, Paths.ConfigPath, Paths.PluginPath);
        SourceRequest? displaced;
        lock (SourceGate)
        {
            displaced = ActiveRequest;
            ActiveRequest = request;
            if (SourceWorker == null) SourceWorker = Task.Run(SourceWorkerLoop);
        }
        if (displaced == null || !displaced.Claim()) return;
        displaced.Cancel();
        displaced.Scan.Cancel(simulationTick);
    }

    // One worker serves the newest run until nothing newer is pending, then exits. A worker that
    // lost its run never loops on stale state.
    private static void SourceWorkerLoop()
    {
        while (true)
        {
            SourceRequest request;
            lock (SourceGate)
            {
                request = ActiveRequest!;
                if (request == null) { SourceWorker = null; return; }
            }
            (ProjectSourceVerification Verification, string Coverage, string Detail) result;
            try { result = CaptureSourceHashes(request); }
            catch (OperationCanceledException)
            { result = (ProjectSourceVerification.Cancelled, "cancelled", "Source sampling was cancelled before it finished."); }
            catch (Exception error)
            {
                request.Report.Issue("source_hash_failed", "files", error.Message, error.ToString());
                result = (ProjectSourceVerification.Unavailable, "partial", "Source sampling stopped after an unexpected file-system error.");
            }
            if (request.Claim()) request.Finish(result.Verification, result.Coverage, result.Detail);
            request.Cancellation.Dispose();
            lock (SourceGate)
            {
                if (ActiveRequest == request)
                {
                    ActiveRequest = null;
                    SourceWorker = null;
                    return;
                }
            }
        }
    }

    private static (ProjectSourceVerification Verification, string Coverage, string Detail) CaptureSourceHashes(SourceRequest request)
    {
        var token = request.Cancellation.Token;
        var candidates = new Dictionary<string, SourceCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in request.Declared)
            candidates[source.FullPath] = new SourceCandidate(source.FullPath, source.RelativePath, source.Sha256, true);
        var limitations = new List<string>();
        Discover(request, candidates, limitations, token);

        var declared = 0;
        var verified = 0;
        var mismatch = false;
        var unavailable = false;
        foreach (var candidate in candidates.Values.OrderByDescending(value => value.Declared).ThenBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Declared) declared++;
            try
            {
                var before = new FileInfo(candidate.FullPath);
                var length = before.Length;
                var modified = before.LastWriteTimeUtc;
                if (length > MaximumSourceBytes)
                {
                    unavailable = true;
                    if (candidate.Declared) request.Report.Check("source_verification", candidate.RelativePath, "unavailable",
                        $"Source exceeds the {MaximumSourceBytes / 1024 / 1024} MiB per-file sampling limit.");
                    continue;
                }
                var remaining = MaximumTotalBytes - Interlocked.Read(ref request.Bytes);
                if (remaining <= 0 || length > remaining)
                {
                    unavailable = true;
                    limitations.Add("total byte limit reached");
                    if (candidate.Declared) request.Report.Check("source_verification", candidate.RelativePath, "unavailable",
                        $"The {MaximumTotalBytes / 1024 / 1024} MiB total sampling limit was reached.");
                    continue;
                }
                var hash = Hash(candidate.FullPath, token, remaining, out var bytes);
                token.ThrowIfCancellationRequested();
                Interlocked.Add(ref request.Bytes, bytes);
                Interlocked.Increment(ref request.Files);
                request.Report.Event("source", "file_snapshot", candidate.RelativePath, new()
                {
                    ["sha256"] = hash,
                    ["bytes"] = bytes.ToString(CultureInfo.InvariantCulture),
                    ["declared"] = candidate.Declared.ToString()
                });
                var after = new FileInfo(candidate.FullPath);
                if (after.Length != length || after.LastWriteTimeUtc != modified)
                {
                    unavailable = true;
                    if (candidate.Declared) request.Report.Check("source_verification", candidate.RelativePath, "unavailable",
                        "The file changed while it was being read; no hash of it can be trusted for this run.");
                }
                else if (candidate.Declared)
                {
                    if (string.Equals(hash, candidate.ExpectedHash, StringComparison.Ordinal))
                    { verified++; request.Report.Check("source_verification", candidate.RelativePath, "matched", hash); }
                    else
                    {
                        mismatch = true;
                        request.Report.Check("source_verification", candidate.RelativePath, "mismatch",
                            $"Authored {candidate.ExpectedHash}; observed {hash}.");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                unavailable = true;
                request.Report.Issue("source_hash_failed", candidate.RelativePath, error.Message, error.ToString());
                if (candidate.Declared) request.Report.Check("source_verification", candidate.RelativePath, "unavailable",
                    "The declared source could not be read: " + error.Message);
            }
        }
        var verification = declared == 0 ? ProjectSourceVerification.NotProvided
            : mismatch ? ProjectSourceVerification.Mismatch
            : unavailable ? ProjectSourceVerification.Unavailable
            : ProjectSourceVerification.Matched;
        var coverage = limitations.Count == 0 && verification is not (ProjectSourceVerification.Unavailable or ProjectSourceVerification.Cancelled)
            ? "complete" : "partial";
        var limits = limitations.Count == 0 ? "none" : string.Join(", ", limitations.Distinct(StringComparer.Ordinal));
        return (verification, coverage,
            $"Sampled {Volatile.Read(ref request.Files)} files and {Interlocked.Read(ref request.Bytes)} bytes as bounded coverage; limits={limits}. Coverage is a sampling result, not source verification. Of {declared} declared sources {verified} matched and the verification result is {ProjectObjectReferences.Wire(verification)}. In-memory changes and files modified after reading are not represented.");
    }

    private static void Discover(SourceRequest request, Dictionary<string, SourceCandidate> candidates, List<string> limitations, CancellationToken token)
    {
        var pending = new Queue<(string Path, bool Plugins)>();
        pending.Enqueue((request.Config, false));
        pending.Enqueue((request.Plugins, true));
        // MTFO reads each package's data tables from BepInEx/GameData/<package>, beside plugins.
        var gameData = Path.Combine(request.Root, "GameData");
        if (Directory.Exists(gameData)) pending.Enqueue((gameData, true));
        var directories = 0;
        var entries = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            if (++directories > MaximumDirectories) { limitations.Add("directory limit reached"); return; }
            var (directory, plugins) = pending.Dequeue();
            if (!Directory.Exists(directory))
            {
                limitations.Add("missing source root");
                request.Report.Issue("source_hash_failed", Subject(directory), "Source directory does not exist: " + directory);
                continue;
            }
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(directory); }
            catch (Exception error)
            {
                limitations.Add("directory enumeration failed");
                request.Report.Issue("source_hash_failed", Relative(request, directory), error.Message, error.ToString());
                continue;
            }
            using var enumerator = children.GetEnumerator();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string child;
                try
                {
                    if (!enumerator.MoveNext()) break;
                    child = enumerator.Current;
                }
                catch (Exception error)
                {
                    limitations.Add("directory enumeration failed");
                    request.Report.Issue("source_hash_failed", Relative(request, directory), error.Message, error.ToString());
                    break;
                }
                if (++entries > MaxEntries) { limitations.Add("entry limit reached"); return; }
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (Exception error)
                {
                    limitations.Add("file attributes unavailable");
                    request.Report.Issue("source_hash_failed", Relative(request, child), error.Message, error.ToString());
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0) { limitations.Add("reparse points skipped"); continue; }
                if ((attributes & FileAttributes.Directory) != 0) { pending.Enqueue((child, plugins)); continue; }
                var extension = Path.GetExtension(child);
                var eligible = !plugins
                    ? extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase)
                    : extension.Equals(".json", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(child).StartsWith("GameData_", StringComparison.Ordinal);
                if (!eligible || candidates.ContainsKey(child)) continue;
                if (candidates.Count >= MaxFiles) { limitations.Add("file limit reached"); return; }
                candidates.Add(child, new SourceCandidate(child, Relative(request, child), "", false));
            }
        }
    }

    // Discovery degrades to a coverage limit: an unusable source root is a sampling limitation,
    // never a reason to abort declared source verification.
    private static string Relative(SourceRequest request, string path)
    {
        try
        {
            var relative = ForgeCore.PosixPath.ToPosix(Path.GetRelativePath(request.Root, path));
            return relative.StartsWith("../", StringComparison.Ordinal) ? Path.GetFileName(path) : relative;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return "unusable-source-root"; }
    }

    private static string Hash(string path, CancellationToken token, long maximumBytes, out long bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        bytes = 0;
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                bytes += read;
                if (bytes > maximumBytes) throw new InvalidDataException("Source exceeded the remaining byte sampling limit while being read.");
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

}

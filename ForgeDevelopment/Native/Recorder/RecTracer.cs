using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace ForgeDevelopment.Native;

internal sealed record RecTraceInvocation(string Profile, string EntryId, string CatalogHash, string Signature,
    string InstallationId, long InvocationId, RecPatch Patch);

internal sealed record RecTraceTarget(string DeclaringType, string Method, IReadOnlyList<string> ParameterTypes,
    string ReturnType, bool Static)
{
    internal string Signature => (Static ? "static " : "instance ") + ReturnType + " " + DeclaringType + "." + Method
        + "(" + string.Join(",", ParameterTypes) + ")";
}

/// <summary>One trace configuration entry: which methods of one type to record, how much of each call to keep, and
/// which instance fields make two calls different. It is parsed from <c>probes/trace/*.forge</c> and never holds a
/// MethodInfo, so it can be parsed and matched without a game assembly present.</summary>
internal sealed class RecTraceEntry
{
    internal RecTraceEntry(string profile, string type, IReadOnlyList<string> methods, IReadOnlyList<string> exclude,
        string record, IReadOnlyList<string> instanceFields, bool firstStack, int maxPerSecond, string onOverflow,
        string phases = "after", RecAcousticObservation.Config? acoustic = null, bool signatureOnly = false,
        RecTraceTarget? target = null, string? entryId = null, string? catalogHash = null)
    {
        Profile = profile;
        Type = type;
        Methods = methods;
        Exclude = exclude;
        Record = record;
        InstanceFields = instanceFields;
        FirstStack = firstStack;
        MaxPerSecond = maxPerSecond;
        OnOverflow = onOverflow;
        Phases = phases;
        Acoustic = acoustic;
        SignatureOnly = signatureOnly;
        Target = target;
        EntryId = entryId;
        CatalogHash = catalogHash;
    }

    internal string Profile { get; }
    internal RecTraceTarget? Target { get; }
    internal string? EntryId { get; }
    internal string? CatalogHash { get; }
    internal bool Observes => Record == "observe";
    internal string Type { get; }
    internal IReadOnlyList<string> Methods { get; }
    internal IReadOnlyList<string> Exclude { get; }
    /// <summary><c>count</c>, <c>args</c>, <c>args+result</c> or <c>changes</c>.</summary>
    internal string Record { get; }
    internal IReadOnlyList<string> InstanceFields { get; }
    internal bool FirstStack { get; }
    internal int MaxPerSecond { get; }
    /// <summary><c>count</c> or <c>drop</c>: what a call beyond <see cref="MaxPerSecond"/> costs.</summary>
    internal string OnOverflow { get; }

    internal RecAcousticObservation.Config? Acoustic { get; }
    internal bool SignatureOnly { get; }
    internal string Phases { get; }
    internal bool RecordsPair => Phases == "before+after";

    internal bool RecordsArgs => Record is "args" or "args+result" or "changes";
    internal bool RecordsResult => Record is "args+result" or "changes";
    internal bool RecordsChanges => Record == "changes";

    /// <summary>The name a record carries for this entry: the profile and the type, so a record can be attributed to
    /// the configuration line that asked for it.</summary>
    internal string Subject => Profile + ":" + Type;

    // A pattern without wildcards is plain string equality under RecGlob, so it is answered from a set; only the
    // wildcard patterns still run the glob. Built on first use from the entry's immutable lists.
    private sealed class MethodPatterns
    {
        private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
        private readonly string[] _globs;
        internal MethodPatterns(IReadOnlyList<string> patterns)
        {
            var globs = new List<string>();
            foreach (var pattern in patterns)
            {
                if (pattern.IndexOfAny(new[] { '*', '?' }) < 0) _exact.Add(pattern);
                else globs.Add(pattern);
            }
            _globs = globs.ToArray();
        }
        internal bool Matches(string method)
        {
            if (_exact.Contains(method)) return true;
            foreach (var glob in _globs) if (RecGlob.IsMatch(glob, method)) return true;
            return false;
        }
    }
    private MethodPatterns? _included, _excluded;

    internal bool MatchesMethod(string method)
    {
        var included = _included ??= new MethodPatterns(Methods);
        if (!included.Matches(method)) return false;
        return !(_excluded ??= new MethodPatterns(Exclude)).Matches(method);
    }
}

/// <summary>One profile as parsed from a trace file, with the outcome of its own parse kept so a rejected file is
/// reported rather than silently ignored.</summary>
internal sealed class RecTraceProfile
{
    internal RecTraceProfile(string name, bool enabledByDefault, string source, IReadOnlyList<RecTraceEntry> entries,
        string? error, string? catalogHash = null, IReadOnlyList<RecTraceSkip>? rejectedObservations = null)
    {
        Name = name;
        EnabledByDefault = enabledByDefault;
        Source = source;
        Entries = entries;
        Error = error;
        CatalogHash = catalogHash;
        RejectedObservations = rejectedObservations ?? Array.Empty<RecTraceSkip>();
    }

    internal string Name { get; }
    internal bool EnabledByDefault { get; }
    internal string Source { get; }
    internal IReadOnlyList<RecTraceEntry> Entries { get; }
    internal string? Error { get; }
    internal string? CatalogHash { get; }
    internal IReadOnlyList<RecTraceSkip> RejectedObservations { get; }
}

/// <summary>Why one configured method was not patched. The reason is a code so the startup report can be counted.</summary>
internal sealed record RecTraceSkip(string Profile, string Type, string Method, string Reason,
    string? CatalogHash = null, string? EntryId = null, string? Signature = null, string? InstallationId = null);

/// <summary>
/// The data-driven native tracer. Everything in this file is decided without Harmony and without a game assembly:
/// what a profile means, whether a method is refused, whether a call is over its rate and whether a `changes` entry
/// has anything new. <see cref="RecTracerRuntime"/> installs the patches this file decides on, which is what makes the
/// rules below testable in a plain process.
/// </summary>
internal static class RecTracer
{
    internal const string Section = "ForgeDevelopment";
    internal const string ConfigFolder = "trace";
    internal const int DefaultMaxPerSecond = 200;
    private const int RecReflectFieldLimit = 64;
    internal const int OverflowSummarySeconds = 5;
    // Only these fixed observers replace receiver-field reflection with callback-scoped
    // scalar readback. Signature/ABI, installation and call identity are checked at runtime.
    internal static bool Y4ReadbackEntry(string profile, string type, IReadOnlyList<string> methods)
        => methods.Count != 0 && (profile == "cov4-stamina" &&
            (type == "PlayerStamina" && methods.All(method => method is "UseStamina" or "UseWeaponLightSwingStamina"
                or "UseWeaponChargedSwingStamina" or "UsePushStamina")
             || type == "DramaManager" && methods.Count == 1 && methods[0] == "get_InActionState")
            || profile == "cov4-mine-foam" &&
            (type == "MineDeployerInstance_Detonate_Glue+_ExplosionSequence_d__25" && methods.Count == 1 && methods[0] == "MoveNext"
             || type == "ProjectileManager" && methods.All(method => method is "WantToFireGlue" or "GetNextSyncID"
                or "DoFireGlue" or "SpawnGlueGunProjectileIfNeeded" or "DoMergeGlue" or "DoDestroyGlue")));

    private static readonly string ProcessIdentity = Guid.NewGuid().ToString("N");

    internal static string InstallationIdentity(long patch, long installation)
        => ProcessIdentity + ":" + patch.ToString(CultureInfo.InvariantCulture) + ":"
            + installation.ToString(CultureInfo.InvariantCulture);

    /// <summary>Methods this tracer refuses whatever a profile says, and the reason it reports for each. The list is
    /// the per-frame and per-render hot path: recording them would measure the recorder, not the game. A profile that
    /// names one of these explicitly gets an override, because that is a deliberate authoring choice.</summary>
    private static readonly Dictionary<string, string> Rejected = new(StringComparer.Ordinal)
    {
        ["Update"] = "frame-loop",
        ["LateUpdate"] = "frame-loop",
        ["FixedUpdate"] = "frame-loop",
        ["OnGUI"] = "immediate-mode-ui",
        ["OnPreRender"] = "render-loop",
        ["OnPostRender"] = "render-loop",
        ["OnRenderObject"] = "render-loop",
        ["OnDrawGizmos"] = "editor-gizmo",
        ["OnDrawGizmosSelected"] = "editor-gizmo"
    };

    /// <summary>A property getter is refused unless the profile asks for it by name: they are called from everywhere,
    /// and a recorder inside a getter is how a game acquires a new stall.</summary>
    private const string GetterReason = "property-getter";

    /// <summary>Patterns that must be spelled out to be accepted. They are matched against the method name.</summary>
    internal static bool IsExplicitOverride(IReadOnlyList<string> patterns, string method)
        => patterns.Any(pattern => string.Equals(pattern, method, StringComparison.Ordinal));

    internal static string? RejectionReason(RecTraceEntry entry, string method, bool hasGetter)
    {
        if (Rejected.TryGetValue(method, out var reason) && !IsExplicitOverride(entry.Methods, method)) return reason;
        if (hasGetter && !IsExplicitOverride(entry.Methods, method)) return GetterReason;
        return null;
    }

    /// <summary>Parses every trace file. A file that is not valid JSON, or an entry missing its type, produces a
    /// profile with an error and no entries; it is never silently dropped.</summary>
    internal static List<RecTraceProfile> Parse(IEnumerable<string> files)
    {
        var profiles = ReadProfiles(files).ToList();
        RejectDuplicateObservations(profiles);
        return profiles;
    }

    // One file is an indivisible parse; startup advances this iterator inside Runtime's diagnostic slice.
    internal static IEnumerable<RecTraceProfile> ReadProfiles(IEnumerable<string> files)
    {
        foreach (var file in files) yield return ParseFile(System.IO.Path.GetFullPath(file));
    }

    /// <summary>Profiles are strict JSON: no comments, no trailing commas, nesting bounded by the parser's own depth.</summary>
    private static readonly JsonDocumentOptions JsonOptions = new()
    { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false };
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    private static RecTraceProfile ParseFile(string path)
    {
        var source = System.IO.Path.GetFileName(path);
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        string? catalogHash = null;
        var entries = new List<RecTraceEntry>();
        var rejected = new List<RecTraceSkip>();
        try
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
            { throw new InvalidDataException("development-trace-unreadable: " + unreadable.Message, unreadable); }
            catalogHash = ForgeCore.Hash.Sha256Hex(bytes);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            string text;
            try { text = StrictUtf8.GetString(bytes, bom, bytes.Length - bom); }
            catch (System.Text.DecoderFallbackException invalid)
            { throw new InvalidDataException("development-trace-utf8: " + invalid.Message, invalid); }
            using var document = JsonDocument.Parse(text, JsonOptions);
            var root = document.RootElement;
            name = root.TryGetProperty("profile", out var profileName) && profileName.ValueKind == JsonValueKind.String
                ? profileName.GetString()! : System.IO.Path.GetFileNameWithoutExtension(path);
            var enabled = !root.TryGetProperty("enabledByDefault", out var enabledValue) || enabledValue.ValueKind != JsonValueKind.False;
            string? error = null;
            if (root.TryGetProperty("entries", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.TryGetProperty("target", out _) || Text(row, "record") == "observe")
                    {
                        try { entries.Add(ParseObservation(row, name, catalogHash)); }
                        catch (Exception invalid)
                        {
                            RecTraceTarget? requested = null;
                            try
                            {
                                if (row.TryGetProperty("target", out var requestedTarget))
                                    requested = ParseTarget(requestedTarget, strict: false);
                            }
                            catch (Exception) { /* The refusal remains explicit even when no signature can be recovered. */ }
                            rejected.Add(new RecTraceSkip(name, requested?.DeclaringType ?? "", requested?.Method ?? "",
                                invalid.Message, catalogHash, Text(row, "entryId"), requested?.Signature));
                            throw;
                        }
                        continue;
                    }
                    var type = Text(row, "type");
                    if (string.IsNullOrEmpty(type))
                    {
                        // One unusable entry costs the whole file: a profile that silently drops a line would
                        // report coverage it does not have.
                        error = source + ": an entry has no type";
                        break;
                    }
                    var phases = Text(row, "phases") ?? "after";
                    if (row.TryGetProperty("signatureOnly", out var signatureOnlyValue)
                        && signatureOnlyValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new FormatException("signatureOnly must be a boolean");
                    if (row.TryGetProperty("acoustic", out _) && phases != "before+after")
                        throw new FormatException("Acoustic observations require before+after paired trace");
                    var methods = Strings(row, "methods", new[] { "*" });
                    // Only these three exact single-method static entries have no receiver fields.
                    // Runtime admission separately verifies each full reviewed signature.
                    var staticNoise = type == "NoiseManager" && row.TryGetProperty("methods", out var staticMethods)
                        && staticMethods.ValueKind == JsonValueKind.Array && staticMethods.GetArrayLength() == 1
                        && staticMethods[0].ValueKind == JsonValueKind.String
                        && staticMethods[0].GetString() is "MakeNoise" or "ProcessNoise" or "ProcessNode";
                    var fields = Strings(row, "instance", Array.Empty<string>());
                    var maxPerSecond = Integer(row, "maxPerSecond", DefaultMaxPerSecond);
                    var overflow = Text(row, "onOverflow") ?? "count";
                    if (row.TryGetProperty("phases", out var phaseValue) && phaseValue.ValueKind != JsonValueKind.String
                        || phases is not ("after" or "before+after")
                        || phases == "before+after" && (Text(row, "record") != "args+result"
                            || !row.TryGetProperty("instance", out var instanceValue) || instanceValue.ValueKind != JsonValueKind.Array
                            || instanceValue.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)
                            || row.TryGetProperty("maxPerSecond", out var rateValue) && (rateValue.ValueKind != JsonValueKind.Number || !rateValue.TryGetInt32(out _))
                            || row.TryGetProperty("onOverflow", out var overflowValue) && overflowValue.ValueKind != JsonValueKind.String
                            || fields.Length == 0 && !staticNoise && !Y4ReadbackEntry(name, type!, methods)
                            || fields.Length > RecReflectFieldLimit || fields.Any(string.IsNullOrWhiteSpace)
                            || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length || maxPerSecond <= 0
                            || overflow is not ("count" or "drop")))
                    {
                        error = source + ": invalid phases or unsupported paired trace configuration";
                        break;
                    }
                    entries.Add(new RecTraceEntry(name, type!, methods, Strings(row, "exclude", Array.Empty<string>()),
                        Record(row), fields, Flag(row, "firstStack"), maxPerSecond, overflow, phases,
                        row.TryGetProperty("acoustic", out var acoustic) ? RecAcousticObservation.Parse(acoustic) : null,
                        Flag(row, "signatureOnly"), catalogHash: catalogHash));
                }
            }
            if (error != null)
            {
                rejected.AddRange(entries.Where(entry => entry.Observes).Select(entry => new RecTraceSkip(name,
                    entry.Type, entry.Target!.Method, "profile-invalid", catalogHash, entry.EntryId,
                    entry.Target.Signature)));
                entries.Clear();
            }
            return new RecTraceProfile(name, enabled, source, entries, error, catalogHash, rejected);
        }
        catch (Exception error)
        {
            rejected.AddRange(entries.Where(entry => entry.Observes).Select(entry => new RecTraceSkip(name,
                entry.Type, entry.Target!.Method, "profile-invalid", catalogHash, entry.EntryId,
                entry.Target.Signature)));
            return new RecTraceProfile(name, false, source, Array.Empty<RecTraceEntry>(),
                source + ": " + error.GetType().Name + ": " + error.Message, catalogHash, rejected);
        }
    }

    internal static void RejectDuplicateObservations(List<RecTraceProfile> profiles)
    {
        foreach (var _ in RejectDuplicateObservationSteps(profiles)) { }
    }

    internal static IEnumerable<bool> RejectDuplicateObservationSteps(List<RecTraceProfile> profiles)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            foreach (var entry in profile.Entries)
            {
                if (profile.Error == null && entry.Observes && !seen.Add(entry.EntryId!)) duplicateIds.Add(entry.EntryId!);
                yield return true;
            }
            yield return true;
        }
        if (duplicateIds.Count != 0)
        {
            for (var index = 0; index < profiles.Count; index++)
            {
                var profile = profiles[index];
                var rejected = new List<RecTraceSkip>();
                foreach (var skip in profile.RejectedObservations) { rejected.Add(skip); yield return true; }
                var collided = false;
                foreach (var entry in profile.Entries)
                {
                    if (entry.Observes && duplicateIds.Contains(entry.EntryId!))
                    {
                        collided = true;
                        rejected.Add(new RecTraceSkip(profile.Name, entry.Type, entry.Target!.Method,
                            "duplicate-observation-entryId", entry.CatalogHash, entry.EntryId, entry.Target.Signature));
                    }
                    yield return true;
                }
                if (!collided) continue;
                profiles[index] = new RecTraceProfile(profile.Name, false, profile.Source,
                    Array.Empty<RecTraceEntry>(), profile.Source + ": duplicate observation entryId",
                    profile.CatalogHash, rejected);
                yield return true;
            }
        }
    }

    private static RecTraceEntry ParseObservation(JsonElement row, string profile, string catalogHash)
    {
        RequireOnlyKeys(row, "entryId", "target", "record", "phases", "maxPerSecond");
        if (Text(row, "record") != "observe" || Text(row, "phases") != "after")
            throw new FormatException("Observations require record=observe and phases=after");
        var entryId = ExactName(row, "entryId");
        if (!row.TryGetProperty("target", out var target)) throw new FormatException("Observation requires target");
        var signature = ParseTarget(target);
        var rate = Integer(row, "maxPerSecond", DefaultMaxPerSecond);
        if (row.TryGetProperty("maxPerSecond", out var rateValue)
            && (rateValue.ValueKind != JsonValueKind.Number || !rateValue.TryGetInt32(out _) || rate <= 0))
            throw new FormatException("maxPerSecond must be a positive integer");
        return new RecTraceEntry(profile, signature.DeclaringType, new[] { signature.Method },
            Array.Empty<string>(), "observe", Array.Empty<string>(), false, rate, "drop", target: signature,
            entryId: entryId, catalogHash: catalogHash);
    }

    private static RecTraceTarget ParseTarget(JsonElement target, bool strict = true)
    {
        if (strict) RequireOnlyKeys(target, "declaringType", "method", "parameterTypes", "returnType", "static");
        var declaring = ExactName(target, "declaringType");
        var method = ExactName(target, "method");
        var returned = ExactName(target, "returnType");
        if (!target.TryGetProperty("static", out var isStatic)
            || isStatic.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException("target.static must be a boolean");
        if (!target.TryGetProperty("parameterTypes", out var parameters)
            || parameters.ValueKind != JsonValueKind.Array)
            throw new FormatException("target.parameterTypes must be an explicit array");
        var types = parameters.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new FormatException("parameterTypes must contain exact type names")).ToArray();
        if (types.Any(value => !IsExactName(value)))
            throw new FormatException("parameterTypes must contain exact type names");
        return new RecTraceTarget(declaring, method, Array.AsReadOnly(types), returned, isStatic.GetBoolean());
    }

    private static void RequireOnlyKeys(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new FormatException("Observation configuration must be an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!keys.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new FormatException("Unknown or duplicate observation field: " + property.Name);
    }

    private static bool IsExactName(string? value) => !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && value.IndexOfAny(new[] { '*', '?', '&' }) < 0;

    private static string ExactName(JsonElement value, string name)
    {
        var text = Text(value, name);
        return IsExactName(text) ? text!
            : throw new FormatException("Missing or invalid exact signature field: " + name);
    }

    /// <summary>The per-second gate every patched method owns. A call beyond the rate is counted for the summary and
    /// then either dropped or recorded as a count, which is the entry's own `onOverflow`. It is lock-free: the call
    /// path is one atomic read, a compare-exchange at a second boundary and one atomic increment.</summary>
    internal sealed class RateGate
    {
        private readonly int _maxPerSecond;
        private long _second = -1;
        private int _count;
        private int _dropped;
        private long _totalDropped;
        private int _sealed;

        internal RateGate(int maxPerSecond) { _maxPerSecond = Math.Max(1, maxPerSecond); }

        internal bool Admit(long nowMilliseconds)
        {
            // Shutdown/profile disable rejects late holders of a patch before they can consume a rate
            // slot or add drops after the terminal summary. This does not count as rate overflow.
            if (Volatile.Read(ref _sealed) != 0) return false;
            var second = nowMilliseconds / 1000;
            var observed = Interlocked.Read(ref _second);
            if (observed != second && Interlocked.CompareExchange(ref _second, second, observed) == observed)
                Interlocked.Exchange(ref _count, 0);
            if (Interlocked.Increment(ref _count) <= _maxPerSecond) return true;
            Interlocked.Increment(ref _dropped);
            Interlocked.Increment(ref _totalDropped);
            return false;
        }

        // Lifetime accounting is committed with the refusal, not later while a summary may be in flight.
        internal long TotalDropped => Interlocked.Read(ref _totalDropped);

        /// <summary>Takes the overflow count, so exactly one summary reports it.</summary>
        internal int TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

        internal void StopAdmission() => Volatile.Write(ref _sealed, 1);
        // Reinstallation preserves the existing per-second rate window; only the shutdown seal is lifted.
        internal void ResumeAdmission() => Volatile.Write(ref _sealed, 0);
    }

    /// <summary>The `changes` memory of one entry: the record it wrote last, as text. A call that produces the same
    /// text is not recorded at all, which is what makes a `changes` entry cheap on a method that runs every frame.</summary>
    internal sealed class ChangeTracker
    {
        private string? _last;

        internal bool Changed(string current)
        {
            if (string.Equals(_last, current, StringComparison.Ordinal)) return false;
            _last = current;
            return true;
        }

        internal void Reset() => _last = null;
    }

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int Integer(JsonElement row, string name, int fallback)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;

    private static string Record(JsonElement row)
    {
        var record = Text(row, "record") ?? "count";
        return record is "count" or "args" or "args+result" or "changes" ? record : "count";
    }

    private static string[] Strings(JsonElement row, string name, string[] fallback)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : fallback;
}

/// <summary>Glob matching for type and method patterns: <c>*</c> for any run, <c>?</c> for one character. It is
/// iterative with backtracking rather than recursive, so a pathological pattern costs time, not stack.</summary>
internal static class RecGlob
{
    internal static bool IsMatch(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern)) return text.Length == 0;
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { p++; t++; continue; }
            if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; continue; }
            if (star < 0) return false;
            p = star + 1;
            t = ++mark;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>Anchored type matching: a profile may name the bare type, the namespace-qualified name, or a
    /// leading/trailing wildcard over either.</summary>
    internal static bool MatchesType(string pattern, string fullName, string shortName)
        => IsMatch(pattern, fullName) || IsMatch(pattern, shortName);
}

/// <summary>Formats the startup report the tracer writes into the `session` channel and into index.forge.</summary>
internal readonly record struct RecTraceInstallProgress(int Attempted, int Total, int Installed, bool Complete,
    double DiscoveryMs = 0, double InstallMs = 0, double ElapsedMs = 0, int Batches = 0,
    int TypeEnumerations = 0, int MethodEnumerations = 0, bool DiscoveryPending = false,
    double MaxDiscoveryStepMs = 0, double MaxInstallStepMs = 0,
    double MaxProfileParseMs = 0, double MaxGetTypesMs = 0, double MaxGetMethodsMs = 0,
    double MaxCooperativeStepMs = 0, string? MaxCooperativeStepStage = null)
{
    // Attempted is cumulative. Report publication advances discovery timing without attempting another patch.
    internal bool HasNewProgressSince(RecTraceInstallProgress previous)
        => Attempted != 0 && (Attempted > previous.Attempted || DiscoveryMs > previous.DiscoveryMs
            || DiscoveryPending != previous.DiscoveryPending);
}

internal static class RecTraceReport
{
    internal static string WithCoverage(string report, string coverage, long observationCallbackFailures = 0,
        RecTraceInstallProgress? installation = null)
    {
        // The stored body is produced by JsonSteps and contains no live installation metadata.
        // Append the small live suffix directly; reparsing and rewriting every skip here would
        // repeat the entire sliced serialization when the caller publishes the finished report.
        using var stream = new System.IO.MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            if (installation is { } timing) WriteInstallation(json, timing);
            json.WritePropertyName("nativeCoverage"); json.WriteRawValue(coverage);
            json.WriteNumber("observationCallbackFailures", observationCallbackFailures);
            RecTraceBudget.WriteTo(json);
            json.WriteEndObject();
        }
        var suffix = System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
        return string.Concat(report.AsSpan(0, report.Length - 1), report.Length > 2 ? ",".AsSpan() : ReadOnlySpan<char>.Empty,
            suffix.AsSpan(1));
    }
    private static void WriteInstallation(Utf8JsonWriter json, RecTraceInstallProgress timing)
    {
        json.WriteStartObject("installation");
        json.WriteNumber("discoveryMs", timing.DiscoveryMs);
        json.WriteNumber("installMs", timing.InstallMs);
        json.WriteNumber("elapsedMs", timing.ElapsedMs);
        json.WriteNumber("batches", timing.Batches);
        json.WriteNumber("attempted", timing.Attempted);
        json.WriteNumber("total", timing.Total);
        json.WriteNumber("installed", timing.Installed);
        json.WriteBoolean("complete", timing.Complete);
        json.WriteNumber("typeEnumerations", timing.TypeEnumerations);
        json.WriteNumber("methodEnumerations", timing.MethodEnumerations);
        json.WriteBoolean("discoveryPending", timing.DiscoveryPending);
        json.WriteNumber("maxDiscoveryStepMs", timing.MaxDiscoveryStepMs);
        json.WriteNumber("maxInstallStepMs", timing.MaxInstallStepMs);
        json.WriteNumber("maxProfileParseMs", timing.MaxProfileParseMs);
        json.WriteNumber("maxGetTypesMs", timing.MaxGetTypesMs);
        json.WriteNumber("maxGetMethodsMs", timing.MaxGetMethodsMs);
        json.WriteNumber("maxCooperativeStepMs", timing.MaxCooperativeStepMs);
        json.WriteString("maxCooperativeStepStage", timing.MaxCooperativeStepStage);
        json.WriteEndObject();
    }
    internal static string ToJson(IEnumerable<RecTraceProfile> profiles, IReadOnlyList<RecTraceSkip> skips, IReadOnlyList<string> installed, IReadOnlyList<string> errors,
        IReadOnlyList<RecPatch>? matched = null, Func<string, bool>? enabled = null,
        RecTraceInstallProgress? installation = null)
    {
        string report = "";
        foreach (var _ in JsonSteps(profiles, skips, installed, errors, matched ?? Array.Empty<RecPatch>(),
            enabled, installation, value => report = value)) { }
        return report;
    }

    internal static IEnumerable<bool> JsonSteps(IEnumerable<RecTraceProfile> profiles, IReadOnlyList<RecTraceSkip> skips,
        IReadOnlyList<string> installed, IReadOnlyList<string> errors, IEnumerable<RecPatch> matched,
        Func<string, bool>? enabled, RecTraceInstallProgress? installation, Action<string> completed, Action<string>? stage = null)
    {
        stage?.Invoke("report-ordering");
        // Insert one row per step, preserving LINQ's former stable ordinal ordering without a whole-list sort.
        var methods = new SortedSet<(RecPatch Value, int Index)>(Comparer<(RecPatch Value, int Index)>.Create((a, b) =>
        {
            var order = StringComparer.Ordinal.Compare(a.Value.Subject, b.Value.Subject);
            if (order == 0) order = StringComparer.Ordinal.Compare(a.Value.Method.ToString(), b.Value.Method.ToString());
            return order == 0 ? a.Index.CompareTo(b.Index) : order;
        }));
        var orderedSkips = new SortedSet<(RecTraceSkip Value, int Index)>(Comparer<(RecTraceSkip Value, int Index)>.Create((a, b) =>
        {
            var order = StringComparer.Ordinal.Compare(a.Value.Type, b.Value.Type);
            if (order == 0) order = StringComparer.Ordinal.Compare(a.Value.Method, b.Value.Method);
            return order == 0 ? a.Index.CompareTo(b.Index) : order;
        }));
        var index = 0;
        foreach (var method in matched) { methods.Add((method, index++)); yield return true; }
        index = 0;
        foreach (var skip in skips) { orderedSkips.Add((skip, index++)); yield return true; }
        foreach (var profile in profiles)
        {
            foreach (var skip in profile.RejectedObservations) { orderedSkips.Add((skip, index++)); yield return true; }
            yield return true;
        }
        using var stream = new System.IO.MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            stage?.Invoke("report-encoding");
            json.WriteStartObject();
            if (installation is { } timing) WriteInstallation(json, timing);
            json.WriteStartArray("profiles");
            foreach (var profile in profiles)
            {
                json.WriteStartObject();
                json.WriteString("profile", profile.Name);
                json.WriteString("source", profile.Source);
                json.WriteString("catalogHash", profile.CatalogHash);
                json.WriteBoolean("enabledByDefault", profile.EnabledByDefault);
                var profileEnabled = enabled?.Invoke(profile.Name) ?? profile.EnabledByDefault;
                json.WriteBoolean("enabled", profileEnabled);
                // An off profile is matched by name only; its targets were not admitted, which is not the same as none matching.
                json.WriteString("admission", profileEnabled ? "evaluated" : "not-evaluated");
                json.WriteNumber("entries", profile.Entries.Count);
                if (profile.Error != null) json.WriteString("error", profile.Error);
                json.WriteEndObject();
                yield return true;
            }
            json.WriteEndArray();
            // `matched` counts every method a profile selected, installed or not: a process without HarmonyX still
            // made the decision, and the report has to say which decision it made.
            json.WriteNumber("matched", methods.Count);
            json.WriteNumber("installed", installed.Count);
            json.WriteNumber("skipped", orderedSkips.Count);
            json.WriteStartArray("methods");
            foreach (var row in methods) { json.WriteStringValue(row.Value.Subject); yield return true; }
            json.WriteEndArray();
            json.WriteStartArray("patches");
            foreach (var row in methods)
            {
                var method = row.Value;
                json.WriteStartObject();
                json.WriteString("profile", method.Entry.Profile);
                json.WriteString("type", method.Method.DeclaringType?.FullName);
                json.WriteString("method", method.Method.Name);
                json.WriteString("signature", method.Entry.Target?.Signature ?? method.Method.ToString());
                if (method.Entry.Observes)
                {
                    json.WriteString("catalogHash", method.Entry.CatalogHash);
                    json.WriteString("entryId", method.Entry.EntryId);
                    json.WriteString("installationId",
                        RecTracer.InstallationIdentity(method.Id, method.Installation));
                    json.WriteString("reason", method.Installed ? null
                        : method.Removed ?? method.NativeReason ?? "not-installed");
                }
                json.WriteNumber("patch", method.Id);
                json.WriteBoolean("installed", method.Installed);
                json.WriteNumber("installation", method.Installation);
                json.WriteString("observationScope", method.ObservationScope);
                json.WriteString("nativeReason", method.NativeReason);
                json.WriteString("removed", method.Removed);
                json.WriteNumber("failures", method.Failures);
                json.WriteBoolean("cleanupPending", method.CleanupPending);
                json.WriteNumber("rateDropped", method.RateDropped);
                json.WriteNumber("budgetDropped", method.BudgetDropped);
                json.WriteEndObject();
                yield return true;
            }
            json.WriteEndArray();
            json.WriteStartArray("skip");
            foreach (var row in orderedSkips)
            {
                var skip = row.Value;
                json.WriteStartObject();
                json.WriteString("profile", skip.Profile);
                json.WriteString("type", skip.Type);
                json.WriteString("method", skip.Method);
                json.WriteString("reason", skip.Reason);
                if (skip.CatalogHash != null || skip.EntryId != null)
                {
                    json.WriteString("catalogHash", skip.CatalogHash);
                    json.WriteString("entryId", skip.EntryId);
                    json.WriteString("signature", skip.Signature);
                    json.WriteString("installationId", skip.InstallationId);
                    json.WriteBoolean("installed", false);
                }
                json.WriteEndObject();
                yield return true;
            }
            json.WriteEndArray();
            json.WriteStartArray("errors");
            foreach (var error in errors) { json.WriteStringValue(error); yield return true; }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        stage?.Invoke("report-materialization");
        // The immutable string requires one contiguous allocation. Give it a separate cooperative slice,
        // including any GC it triggers, instead of appending it to a partly spent encoding frame.
        yield return false;
        completed(System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length)));
        yield return false;
    }

    internal static string Counted(IEnumerable<string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in values) counts[value] = counts.TryGetValue(value, out var count) ? count + 1 : 1;
        return string.Join(", ", counts.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value.ToString(CultureInfo.InvariantCulture)));
    }
}

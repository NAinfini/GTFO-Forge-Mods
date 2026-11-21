using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using HarmonyLib;

namespace ForgeDevelopment.Native;

/// <summary>One patch the tracer installed, with everything the prefix and postfix need to decide what to write.
/// The counters are the ones a `maxPerSecond` gate and an overflow summary report.</summary>
internal sealed class RecPatch
{
    private static long _nextId;
    private static long _nextCallId;

    internal RecPatch(RecTraceEntry entry, MethodBase method)
    {
        Entry = entry;
        Method = method;
        ObservationSignature = entry.Target?.Signature ?? "";
        Id = Interlocked.Increment(ref _nextId);
        Gate = new RecTracer.RateGate(entry.MaxPerSecond);
        Changes = new RecTracer.ChangeTracker();
        // Built once: MethodBase.ToString allocates a new string on every call, and every traced call writes both.
        Signature = method.ToString() ?? method.Name;
        Declaring = method.DeclaringType?.FullName ?? "unknown";
    }

    internal string Signature { get; }
    internal string Declaring { get; }

    // Only a `changes` entry builds a key, so the 64 KB buffer exists only for those patches.
    private RecJsonBuffer? _key;

    internal long Id { get; }
    internal RecTraceEntry Entry { get; }
    internal MethodBase Method { get; }
    /// <summary>True only while this method's hook is installed. A disabled profile may be re-enabled;
    /// a failed patch retains its failure and is never retried implicitly.</summary>
    private int _installed;
    internal bool Installed { get => Volatile.Read(ref _installed) != 0; set => Volatile.Write(ref _installed, value ? 1 : 0); }
    internal volatile bool Enabled;
    internal long Installation { get; set; }
    internal string InstallationId { get; set; } = "";
    internal string ObservationSignature { get; }
    internal string ObservationScope { get; set; } = "uninstalled";
    internal string? NativeReason { get; set; }
    internal bool AdmissionRefused { get; set; }
    internal IDisposable? WrapperHook { get; set; }
    internal IDisposable? SharedHook { get; set; }
    internal bool CleanupPending { get; set; }
    internal string ObservationMoment { get; set; } = "managed-postfix";
    internal RecTracer.RateGate Gate { get; }
    internal RecTracer.ChangeTracker Changes { get; }
    internal string Subject => Entry.Subject + "." + Method.Name;
    internal long Calls { get; set; }
    internal long Recorded { get; set; }
    internal long Failures { get; set; }
    internal long LastOverflowReport { get; set; }
    internal long RateDropped => Gate.TotalDropped;
    internal long BudgetDropped;
    internal string? Removed { get; private set; }

    /// <summary>The patch is uninstalled the first time it throws. A configuration line that breaks the game is a
    /// bug in the configuration, and the tracer's contract is that it costs one record, not the session.</summary>
    internal void Fail(Exception error, bool report = true)
    {
        Failures++;
        if (report) RecDiag.Error("development.trace-patch-failed method=" + Subject + " error=" + error);
        if (Removed != null) return;
        Removed = error.GetType().Name + ": " + RecReflect.Truncate(error.Message, 200);
        try
        {
            RecTracerRuntime.Uninstall(this);
        }
        catch (Exception uninstall)
        {
            Removed += " (uninstall failed: " + uninstall.GetType().Name + ")";
        }
    }

    /// <summary>Writes one call. The instance fields, the arguments and the result each go through RecReflect, so
    /// depth, element and budget caps apply to a trace record exactly as they do to a snapshot.</summary>
    internal void Record(object? instance, object?[]? arguments, object? result, bool hasResult)
    {
        Calls++;
        var adepth = 0;
        RecSession.Write("tracer", Entry.RecordsChanges ? "change" : "call", json =>
        {
            json.WriteString("profile", Entry.Profile);
            json.WriteString("type", Entry.Type);
            json.WriteString("method", Method.Name);
            json.WriteNumber("patch", Id);
            json.WriteString("observationScope", ObservationScope);
            json.WriteString("observationMoment", ObservationMoment);
            json.WriteString("declaring", Declaring);
            if (instance != null)
            {
                json.WriteString("instance", RecReflect.Describe(instance));
                if (Entry.InstanceFields.Count != 0)
                {
                    json.WritePropertyName("fields");
                    json.WriteStartObject();
                    foreach (var (path, value) in RecReflect.ReadPaths(instance, Entry.InstanceFields))
                    {
                        json.WritePropertyName(path);
                        RecReflect.WriteValue(json, value, adepth);
                    }
                    json.WriteEndObject();
                }
            }
            if (Entry.RecordsArgs && arguments is { Length: > 0 })
            {
                json.WritePropertyName("args");
                json.WriteStartArray();
                foreach (var argument in arguments) RecReflect.WriteValue(json, argument, adepth);
                json.WriteEndArray();
            }
            if (Entry.RecordsResult && hasResult)
            {
                json.WritePropertyName("result");
                RecReflect.WriteValue(json, result, adepth);
            }
            json.WriteNumber("call", Calls);
        });
        Recorded++;
    }

    // One invocation owns one ID, including recursive calls and calls that never return.
    // It is process-local; consumers must also match the recorder session and patch.
    internal long BeginPair() { Calls++; return Interlocked.Increment(ref _nextCallId); }

    internal void RecordPhase(object? instance, object?[]? arguments, object? result, bool hasResult,
        long callId, string phase, string completion, Exception? exception = null, RecAcousticObservation.Scope? acousticScope = null, RecStaminaTrace.Scope? staminaScope = null, RecMineFoamTrace.Scope? mineFoamScope = null)
    {
        // Copy callback state before entering the recorder writer. The writer takes its
        // session lock; observers also validate installations and must never run under it.
        var stamina = RecStaminaTrace.Capture(staminaScope, instance, result, hasResult, phase, completion);
        var mineFoam = RecMineFoamTrace.Capture(mineFoamScope, instance, arguments, result, hasResult, phase, completion);
        var frozenArguments = mineFoamScope == null ? arguments : RecMineFoamTrace.Arguments(Method, arguments);
        var frozenResult = mineFoamScope == null ? result : RecMineFoamTrace.Result(Method, result);
        RecSession.Write("tracer", "call", json =>
        {
            json.WriteString("profile", Entry.Profile);
            json.WriteString("type", Entry.Type);
            json.WriteString("method", Method.Name);
            json.WriteString("declaring", Declaring);
            json.WriteString("signature", Signature);
            json.WriteString("observationScope", ObservationScope);
            json.WriteString("observationMoment", phase == "before" ? "before-original"
                : phase == "exception" ? "exception-finalizer" : ObservationMoment);
            json.WriteNumber("patch", Id);
            json.WriteNumber("callId", callId);
            json.WriteString("phase", phase);
            json.WriteString("completion", completion);
            var acoustic = Entry.Acoustic == null ? null
                : RecAcousticObservation.Capture(Entry.Acoustic, instance, arguments, acousticScope, phase);
            if (acoustic != null)
            {
                json.WritePropertyName("acoustics");
                RecReflect.WriteValue(json, acoustic, 0);
            }
            if (stamina != null)
            {
                json.WritePropertyName("stamina");
                JsonSerializer.Serialize(json, stamina);
            }
            if (mineFoam != null)
            {
                json.WritePropertyName("mineFoam");
                JsonSerializer.Serialize(json, mineFoam);
            }
            json.WriteString("instance", instance == null ? null : RecReflect.Describe(instance));
            // The three reviewed static noise pairs have no receiver. Never invent an instance or admit an
            // unrelated static target merely because its profile omitted fields.
            var noReceiver = Entry.InstanceFields.Count == 0 && Method is MethodInfo noiseMethod
                && (RecNoiseTrace.SupportsStatic(noiseMethod) || RecStaminaTrace.SupportsStatic(noiseMethod) || RecMineFoamTrace.SupportsStatic(noiseMethod));
            var complete = (instance != null || noReceiver) && completion is "entered" or "returned";
            // Values go straight into the record; only their read outcomes are kept until `fieldReads`.
            var reads = Entry.InstanceFields.Count == 0 ? Array.Empty<RecTraceValue>() : new RecTraceValue[Entry.InstanceFields.Count];
            json.WriteStartObject("fields");
            for (var index = 0; index < reads.Length; index++)
            {
                var path = Entry.InstanceFields[index];
                var read = instance == null ? new VanillaValueReading.ReadResult("instance-unavailable", null)
                    : VanillaValueReading.ReadMember(instance, path);
                // Missing fields have a read outcome, never a fabricated null.
                reads[index] = read.Outcome == "explicit-null" && read.NullTarget != path
                    ? new RecTraceValue("null-before-leaf", read.NullTarget)
                    : read.Available ? RecTraceValue.Write(json, path, read.Value) : new RecTraceValue(read.Outcome, read.Error);
                complete &= reads[index].Complete;
            }
            json.WriteEndObject();
            json.WriteStartObject("fieldReads");
            for (var index = 0; index < reads.Length; index++) reads[index].WriteStatus(json, Entry.InstanceFields[index]);
            json.WriteEndObject();
            var args = RecTraceValue.Write(json, "args", frozenArguments);
            args.WriteStatus(json, "argsRead");
            complete &= arguments != null && args.Complete;
            if (hasResult)
            {
                var returned = RecTraceValue.Write(json, "result", frozenResult);
                returned.WriteStatus(json, "resultRead");
                complete &= returned.Complete;
            }
            if (exception != null) json.WriteString("error", exception.GetType().FullName + ": " + RecReflect.Truncate(exception.Message, 200));
            // This only qualifies this snapshot. A matching before + returned after is still required,
            // and neither proves gameplay, native-only patch order, or probability semantics.
            complete &= acoustic == null || acoustic.TryGetValue("observationComplete", out var acousticComplete) && acousticComplete is true;
            complete &= stamina == null || stamina["observationComplete"] is true;
            complete &= mineFoam == null || mineFoam["observationComplete"] is true;
            json.WriteBoolean("observationComplete", complete);
        });
        Recorded++;
    }

    /// <summary>Builds the `changes` key: the same text a record would carry for the fields and arguments that define
    /// the change. The key is built into this patch's own bounded buffer, so a `changes` entry on a hot method still
    /// costs a fixed amount of memory.</summary>
    internal string? ChangeKey(object? instance, object?[]? arguments)
    {
        try
        {
            var key = _key ??= new RecJsonBuffer(RecSession.DefaultRecordBudget);
            key.Reset();
            using var json = new Utf8JsonWriter(key, new JsonWriterOptions { SkipValidation = true });
            json.WriteStartArray();
            if (instance != null)
            {
                foreach (var (_, value) in RecReflect.ReadPaths(instance, Entry.InstanceFields)) RecReflect.WriteValue(json, value, 0);
            }
            if (arguments != null) foreach (var argument in arguments) RecReflect.WriteValue(json, argument, 0);
            json.WriteEndArray();
            json.Flush();
            return System.Text.Encoding.UTF8.GetString(key.WrittenSpan);
        }
        catch (RecValueTooLargeException) { return "(over-budget)"; }
        catch (Exception error) { return "(" + error.GetType().Name + ")"; }
    }

    /// <summary>The overflow summary: a call the rate refused is counted and the count is written on its own record,
    /// so a trace of a hot method says what it did not keep.</summary>
    internal void ReportOverflow(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - LastOverflowReport < RecTracer.OverflowSummarySeconds * 1000L) return;
        var dropped = Gate.TakeDropped();
        if (dropped == 0) return;
        LastOverflowReport = now;
        RecSession.Write("tracer", "overflow", json =>
        {
            json.WriteString("profile", Entry.Profile);
            json.WriteString("type", Entry.Type);
            json.WriteString("method", Method.Name);
            json.WriteNumber("patch", Id);
            json.WriteNumber("dropped", dropped);
            json.WriteNumber("maxPerSecond", Entry.MaxPerSecond);
            json.WriteString("onOverflow", Entry.OnOverflow);
            json.WriteNumber("calls", Calls);
        });
    }
}

/// <summary>The candidate types of one assembly for one trace load, built once: generic definitions, interfaces and
/// framework namespaces are filtered out a single time instead of once per profile entry, and a pattern without wildcards
/// is answered from name dictionaries. A pattern with wildcards still runs the same <see cref="RecGlob"/> match over the
/// filtered list, and every answer keeps the assembly's own type order.</summary>
internal sealed class RecTypeIndex
{
    private readonly List<Type> _types = new();
    private readonly List<string> _full = new(), _short = new();
    private readonly Dictionary<string, List<int>> _byFull = new(StringComparer.Ordinal), _byShort = new(StringComparer.Ordinal);

    internal RecTypeIndex(IEnumerable<Type> all, Func<string, bool> frameworkType)
    {
        foreach (var type in all) Append(type, frameworkType);
    }

    internal RecTypeIndex() { }
    internal void Append(Type type, Func<string, bool> frameworkType)
    {
        if (type.IsGenericTypeDefinition || type.IsInterface || frameworkType(type.FullName ?? "")) return;
        var fullName = type.FullName ?? type.Name;
        Add(_byFull, fullName, _types.Count); Add(_byShort, type.Name, _types.Count);
        _types.Add(type); _full.Add(fullName); _short.Add(type.Name);
    }

    private static void Add(Dictionary<string, List<int>> map, string key, int index)
    {
        if (!map.TryGetValue(key, out var list)) map.Add(key, list = new List<int>());
        list.Add(index);
    }

    internal IEnumerable<Type> Select(string pattern)
    {
        foreach (var type in SelectSteps(pattern)) if (type != null) yield return type;
    }

    internal IEnumerable<Type?> SelectSteps(string pattern)
    {
        if (pattern.Length > 0 && pattern.IndexOfAny(Wildcards) < 0)
        {
            var hits = new SortedSet<int>();
            if (_byFull.TryGetValue(pattern, out var byFull)) hits.UnionWith(byFull);
            if (_byShort.TryGetValue(pattern, out var byShort)) hits.UnionWith(byShort);
            foreach (var index in hits) yield return _types[index];
            yield break;
        }
        for (var index = 0; index < _types.Count; index++)
            yield return RecGlob.MatchesType(pattern, _full[index], _short[index]) ? _types[index] : null;
    }

    private static readonly char[] Wildcards = { '*', '?' };
}
/// <summary>
/// Finds profile targets and subscribes native observations to the shared hook owner.
/// Native returned snapshots use the normal-return finalizer lane; managed helper
/// and wrapper observations retain their explicitly separate managed postfix boundary.
/// </summary>
internal static class RecTracerRuntime
{
    /// <summary>Read-only open-addressed snapshot for the per-call method lookup. Installation adds one patch at a
    /// time under its own small write lock and publishes a fresh bucket; a patched call reads the bucket slot with no
    /// lock and no dictionary probe.</summary>
    private sealed class PatchIndex
    {
        private const int BucketCount = 1024;
        private sealed class Bucket
        {
            internal static readonly Bucket Empty = new(Array.Empty<RecPatch>());
            internal Bucket(RecPatch[] items) => Items = items;
            internal readonly RecPatch[] Items;
        }

        private readonly Bucket[] _buckets = new Bucket[BucketCount];
        private readonly object _write = new();

        internal PatchIndex() { for (var index = 0; index < BucketCount; index++) _buckets[index] = Bucket.Empty; }

        private static int Slot(MethodBase method) => RuntimeHelpers.GetHashCode(method) & (BucketCount - 1);

        internal bool TryGet(MethodBase method, out RecPatch patch)
        {
            var items = Volatile.Read(ref _buckets[Slot(method)]).Items;
            for (var index = 0; index < items.Length; index++)
                if (items[index].Method.Equals(method)) { patch = items[index]; return true; }
            patch = null!;
            return false;
        }

        internal void Add(RecPatch patch)
        {
            lock (_write)
            {
                var slot = Slot(patch.Method);
                var items = _buckets[slot].Items;
                var next = new RecPatch[items.Length + 1];
                items.CopyTo(next, 0);
                next[items.Length] = patch;
                Volatile.Write(ref _buckets[slot], new Bucket(next));
            }
        }
    }

    /// <summary>Identity for managed trace hooks. Native hooks are owned by the
    /// shared Development backend and this tracer holds only its consumer lease.</summary>
    internal const string RecorderGuid = "NAinfini.ForgeDevelopment.Recorder";

    private static readonly object Gate = new();
    private static readonly Dictionary<MethodBase, RecPatch> Patches = new();
    /// <summary>The per-call lookup. It is rebuilt bucket by bucket as patches are matched, never read under
    /// <see cref="Gate"/>: a patched game method finds its patch in a read-only array slot.</summary>
    private static readonly PatchIndex Index = new();
    private static readonly Dictionary<string, RecPatch> ObservationPatches = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> ProfileEnabled = new(StringComparer.Ordinal);
    private static readonly List<RecTraceSkip> Skips = new();
    private static readonly List<string> Installed = new();
    private static readonly List<string> Errors = new();
    private static readonly Dictionary<string, List<RecPatch>> Candidates = new(StringComparer.Ordinal);

    private static bool _released;
    private static volatile bool _stopping;
    private static Queue<RecPatch>? _installQueue;
    private static IEnumerator<bool>? _discovery;
    private static IEnumerator<bool>? _publication;
    private static int _installAttempted, _installTotal;
    private static long _installStarted, _installEnded, _discoveryTicks, _installTicks;
    private static long _maxDiscoveryStepTicks, _maxInstallStepTicks;
    private static long _maxProfileParseTicks, _maxGetTypesTicks, _maxGetMethodsTicks;
    private static long _atomicStepTicks, _maxCooperativeStepTicks;
    private static string? _startupStage, _maxCooperativeStepStage;
    private static int _installBatches, _typeEnumerations, _methodEnumerations;
    private static string _report = "{\"profiles\":[],\"matched\":0,\"skipped\":0,\"methods\":[],\"skip\":[],\"errors\":[]}";

    internal static IReadOnlyList<RecTraceSkip> Skipped => Skips;
    internal static IReadOnlyList<string> InstalledMethods => Installed;
    internal static string Report => RecTraceReport.WithCoverage(_report, RecTraceCoverage.Report,
        ObservationCallbackFailures, CurrentInstallation());
    internal static bool Active => !_stopping && Patches.Count != 0;
    internal static bool InstallationPending { get { using (RecGate.Enter(Gate, "recorder.lock-timeout")) return _installQueue != null; } }
    internal static bool CleanupPending { get { using (RecGate.Enter(Gate, "recorder.lock-timeout")) return Patches.Values.Any(patch => patch.CleanupPending); } }
    internal static Action<RecTraceInvocation>? ObservationReturned { get; set; }
    private static long _nextObservationInvocation;
    private static long _observationCallbackFailures;

    internal static long ObservationCallbackFailures => Interlocked.Read(ref _observationCallbackFailures);

    internal static bool IsObservationInstallationCurrent(string entryId, string installationId)
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) return !_stopping && ObservationPatches.TryGetValue(entryId, out var patch)
            && patch.InstallationId == installationId && patch.Installed && patch.Removed == null
            && ProfileEnabled.TryGetValue(patch.Entry.Profile, out var enabled) && enabled;
    }

    internal static RecTraceInvocation? BeginObservation(MethodBase method)
    {
#if FORGE_DEVELOPMENT_TIMING
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentObservationBegin);
#endif
        if (_stopping || RecObservation.Active) return null;
        if (!Index.TryGet(method, out var patch) || !patch.Installed || patch.Removed != null || !patch.Entry.Observes
            || !patch.Enabled || !patch.Gate.Admit(Environment.TickCount64)) return null;
        if (!RecTraceBudget.TryAdmit()) { Interlocked.Increment(ref patch.BudgetDropped); return null; }
        using var cost = RecTraceBudget.Measure();
        return new RecTraceInvocation(patch.Entry.Profile, patch.Entry.EntryId!, patch.Entry.CatalogHash!,
            patch.ObservationSignature, patch.InstallationId,
            Interlocked.Increment(ref _nextObservationInvocation), patch);
    }

    internal static void ReturnObservation(RecTraceInvocation invocation)
    {
#if FORGE_DEVELOPMENT_TIMING
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentObservationComplete);
#endif
        using var cost = RecTraceBudget.Measure();
        if (!IsActive(invocation.Patch) || invocation.Patch.InstallationId != invocation.InstallationId) return;
        // Capture rechecks installation while it admits and reads the queued work. Do not call it under Gate.
        try { ObservationReturned?.Invoke(invocation); }
        catch (Exception) { Interlocked.Increment(ref _observationCallbackFailures); } // Counted in _observationCallbackFailures.
    }

    /// <summary>The report the session's index.forge carries, so a session says which patches it ran with.</summary>
    internal static string InstalledJson() => Report;

    /// <summary>Parses the configured profiles, matches them against the loaded assemblies and installs the patches.
    /// A profile that is disabled by default is still matched and reported, because a later
    /// <see cref="SetProfileEnabled"/> may turn it on without a restart.</summary>
    internal static void LoadProfiles(IEnumerable<string> files) => LoadProfiles(files, null);

    /// <summary>Synchronous entry point for offline suites. An explicit stand-in type list lets the whole decision
    /// path — include, exclude, reject, match and install — run without a game assembly present. Production startup
    /// uses <see cref="BeginLoadProfiles(IEnumerable{string}, IReadOnlyList{Type}?, Func{RecTraceProfile, bool}, Action{Action}?)"/>.</summary>
    internal static void LoadProfiles(IEnumerable<string> files, IReadOnlyList<Type>? types, bool enableAllProfiles = false)
        => LoadProfiles(RecTracer.Parse(files), types, profile => profile.EnabledByDefault || enableAllProfiles);

    // Offline suites need a complete installation before they invoke the patched stand-ins. Production startup
    // calls BeginLoadProfiles and advances the same queue from RecRuntime.Tick instead.
    private static void LoadProfiles(IReadOnlyList<RecTraceProfile> profiles, IReadOnlyList<Type>? types,
        Func<RecTraceProfile, bool> enabled)
    {
        BeginLoadProfiles(profiles, types, enabled);
        while (InstallationPending) InstallNextBatch(int.MaxValue);
    }

    internal static void BeginLoadProfiles(IEnumerable<string> files, IReadOnlyList<Type>? types,
        bool enableAllProfiles = false)
        => BeginLoadProfiles(files, types, profile => profile.EnabledByDefault || enableAllProfiles);

    internal static void BeginLoadProfiles(IEnumerable<string> files, IReadOnlyList<Type>? types,
        Func<RecTraceProfile, bool> enabled, Action<Action>? bindBatch = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(enabled);
        BeginLoadProfiles(RecTracer.ReadProfiles(files), types, enabled, bindBatch);
    }

    private static void BeginLoadProfiles(IEnumerable<RecTraceProfile> profiles, IReadOnlyList<Type>? types,
        Func<RecTraceProfile, bool> enabled, Action<Action>? bindBatch = null)
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (_installQueue != null)
                throw new InvalidOperationException("Trace installation is still pending.");
            if (_released)
            {
                if (Patches.Values.Any(patch => patch.CleanupPending))
                    throw new InvalidOperationException("Trace cleanup is still pending; retry stop before loading profiles.");
                Patches.Clear(); ObservationPatches.Clear(); Candidates.Clear(); ProfileEnabled.Clear(); Known.Clear();
                Skips.Clear(); Errors.Clear();
                _report = "{\"profiles\":[],\"matched\":0,\"skipped\":0,\"methods\":[],\"skip\":[],\"errors\":[]}";
                RecTraceCoverage.ClearTraces();
                Interlocked.Exchange(ref _observationCallbackFailures, 0);
                _released = false;
                _stopping = false;
            }
            _installQueue = new Queue<RecPatch>();
            _installAttempted = _installTotal = 0;
            _installStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            _installEnded = _discoveryTicks = _installTicks = 0;
            _installBatches = _typeEnumerations = _methodEnumerations = 0;
            _maxDiscoveryStepTicks = _maxInstallStepTicks = 0;
            _maxProfileParseTicks = _maxGetTypesTicks = _maxGetMethodsTicks = 0;
            _atomicStepTicks = _maxCooperativeStepTicks = 0;
            _startupStage = _maxCooperativeStepStage = null;
            _discovery = DiscoverProfiles(profiles, types, enabled, bindBatch).GetEnumerator();
        }
    }

    private static IEnumerable<bool> DiscoverProfiles(IEnumerable<RecTraceProfile> source, IReadOnlyList<Type>? types,
        Func<RecTraceProfile, bool> enabled, Action<Action>? bindBatch)
    {
        var profiles = new List<RecTraceProfile>();
        _startupStage = "profile-parsing";
        using (var reader = source.GetEnumerator())
            while (true)
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                bool more;
                try { more = reader.MoveNext(); }
                finally { RecordAtomic(ref _maxProfileParseTicks, started); }
                if (!more) break;
                var profile = reader.Current;
                profiles.Add(profile); Known[profile.Name] = profile; yield return true;
            }
        _startupStage = "observation-duplicate-scan";
        foreach (var step in RecTracer.RejectDuplicateObservationSteps(profiles)) yield return step;
        var selected = new Dictionary<string, bool>(StringComparer.Ordinal);
        _startupStage = "profile-binding";
        foreach (var profile in profiles)
        {
            Known[profile.Name] = profile;
            void SelectProfile() => selected[profile.Name] = profile.Error == null && enabled(profile);
            if (bindBatch == null) SelectProfile(); else bindBatch(SelectProfile);
            using (RecGate.Enter(Gate, "recorder.lock-timeout"))
            {
                ProfileEnabled[profile.Name] = selected[profile.Name];
                if (profile.Error != null) Errors.Add(profile.Error);
            }
            yield return true;
        }
        _startupStage = "profile-error-reporting";
        foreach (var profile in profiles)
        {
            if (profile.Error != null)
            {
                RecDiag.Error("development.trace-profile-disabled profile=" + profile.Name + " error=" + profile.Error);
                RecSession.Write("session", "trace_profile_failed", json =>
                {
                    json.WriteString("profile", profile.Name);
                    json.WriteString("source", profile.Source);
                    json.WriteString("error", profile.Error);
                });
            }
            yield return true;
        }
        // A disabled catch-all must not reserve methods ahead of enabled, specific profiles.
        // Enabled profiles retain their declared first-claim order; disabled profiles may
        // still match otherwise unclaimed methods for later activation.
        // Reflection metadata is immutable for a loaded, non-dynamic assembly/type. Keep these caches local
        // to this load: later loads and the module-noise source still resolve their current inputs normally.
        var assemblyTypes = new Dictionary<Assembly, RecTypeIndex>();
        var typeMethods = new Dictionary<Type, MethodInfo[]>();
        _startupStage = "method-matching";
        // Two stable passes avoid sorting/materializing all profiles in one step.
        foreach (var enabledPass in new[] { true, false })
            foreach (var profile in profiles)
            {
                yield return true;
                if (profile.Error != null || selected[profile.Name] != enabledPass) continue;
                foreach (var entry in profile.Entries)
                {
                    yield return true;
                    foreach (var step in MatchSteps(entry, types ?? FindTypes(entry.Type, assemblyTypes), typeMethods))
                        yield return step;
                }
            }
        _startupStage = "install-queue";
        RecPatch[] ordered;
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            // The process hook budget is finite and the tracer stops at the product reserve, so who gets a slot is decided here:
            // small, specific profiles first, the bulk profiles (hundreds to a thousand method names each) after them; within one
            // profile the declared first-claim order stands. A bulk profile can then no longer starve a targeted one by name order.
            var size = Patches.Values.GroupBy(p => p.Entry.Profile).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            ordered = Patches.Values.OrderBy(p => size[p.Entry.Profile]).ToArray();
        }
        foreach (var patch in ordered)
        {
            using (RecGate.Enter(Gate, "recorder.lock-timeout"))
            {
                if (!IsProfileEnabled(patch.Entry.Profile))
                {
                    patch.NativeReason = patch.Entry.Observes ? "profile-disabled" : "not-evaluated";
                }
                else if (CanInstall(patch)) _installQueue!.Enqueue(patch);
            }
            yield return true;
        }
        _installTotal = _installQueue!.Count;
        // Installation changes the same rows immediately. Publish the complete body once, after that queue;
        // pending reads still receive live installation progress, and cancellation publishes all parsed evidence.
    }

    /// <summary>Consumes at most <paramref name="maxAttempts"/> candidates. Refusals and failures advance the
    /// cursor too, so a bad patch cannot silently stall startup.</summary>
    internal static RecTraceInstallProgress InstallNextBatch(int maxAttempts, Func<bool>? canContinue = null)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (_installQueue == null || _stopping)
                return InstallProgress(_installQueue == null && !_stopping);
            while (_discovery != null && (canContinue == null || canContinue()))
            {
                var stepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                _atomicStepTicks = 0;
                bool more;
                try { more = _discovery.MoveNext(); }
                finally
                {
                    var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - stepStarted;
                    _discoveryTicks += ticks;
                    _maxDiscoveryStepTicks = Math.Max(_maxDiscoveryStepTicks, ticks);
                    RecordCooperativeStep(ticks);
                }
                if (!more) { _discovery.Dispose(); _discovery = null; }
            }
            if (_discovery != null) return InstallProgress(false);
            // Stop cancels under this same gate. Holding it through this bounded batch prevents a dequeued patch
            // from installing after Stop has taken its cleanup snapshot.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                for (var count = 0; count < maxAttempts && _installQueue.Count != 0
                    && (canContinue == null || canContinue()); count++)
                {
                    var patch = _installQueue.Dequeue();
                    var stepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    try { if (IsProfileEnabled(patch.Entry.Profile) && CanInstall(patch)) InstallPatch(patch); }
                    finally
                    {
                        _maxInstallStepTicks = Math.Max(_maxInstallStepTicks,
                        System.Diagnostics.Stopwatch.GetTimestamp() - stepStarted);
                    }
                    _installAttempted++;
                }
            }
            finally { _installTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; _installBatches++; }
            var complete = _installQueue.Count == 0;
            if (complete)
            {
                _publication ??= PublishSteps().GetEnumerator();
                while (canContinue == null || canContinue())
                {
                    var stepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool more;
                    _atomicStepTicks = 0;
                    try { more = _publication.MoveNext(); }
                    finally
                    {
                        var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - stepStarted;
                        _discoveryTicks += ticks;
                        _maxDiscoveryStepTicks = Math.Max(_maxDiscoveryStepTicks, ticks);
                        RecordCooperativeStep(ticks);
                    }
                    if (!more) { _publication.Dispose(); _publication = null; break; }
                    if (canContinue != null && !_publication.Current) return InstallProgress(false);
                }
                if (_publication != null) return InstallProgress(false);
                _installQueue = null;
                _installEnded = System.Diagnostics.Stopwatch.GetTimestamp();
                RecTraceCoverage.Publish("profiles-loaded");
            }
            return InstallProgress(complete);
        }
    }

    private static RecTraceInstallProgress InstallProgress(bool complete)
    {
        double Ms(long ticks) => ticks * 1000d / System.Diagnostics.Stopwatch.Frequency;
        return new(_installAttempted, _installTotal, Installed.Count, complete, Ms(_discoveryTicks), Ms(_installTicks),
            _installStarted == 0 ? 0 : Ms((_installEnded == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : _installEnded) - _installStarted),
            _installBatches, _typeEnumerations, _methodEnumerations,
            _discovery != null, Ms(_maxDiscoveryStepTicks), Ms(_maxInstallStepTicks),
            Ms(_maxProfileParseTicks), Ms(_maxGetTypesTicks), Ms(_maxGetMethodsTicks), Ms(_maxCooperativeStepTicks), _maxCooperativeStepStage);
    }

    private static void RecordAtomic(ref long maximum, long started)
    {
        var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        maximum = Math.Max(maximum, ticks);
        _atomicStepTicks += ticks;
    }

    private static void RecordCooperativeStep(long ticks)
    {
        var cooperative = Math.Max(0, ticks - _atomicStepTicks);
        if (cooperative <= _maxCooperativeStepTicks) return;
        _maxCooperativeStepTicks = cooperative;
        _maxCooperativeStepStage = _startupStage;
    }

    private static RecTraceInstallProgress? CurrentInstallation()
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) return _installStarted == 0 ? null : InstallProgress(_installQueue == null && !_stopping);
    }

    private static IEnumerable<bool> MatchSteps(RecTraceEntry entry, IEnumerable<Type?> types,
        Dictionary<Type, MethodInfo[]>? typeMethods = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(types);
        var exactMatches = 0;
        foreach (var type in types)
        {
            yield return true;
            if (type == null) continue;
            if (entry.Target != null && type.FullName != entry.Target.DeclaringType) continue;
            if (type.ContainsGenericParameters || type.IsInterface) continue;
            var methodCache = type.Assembly.IsDynamic ? null : typeMethods;
            if (methodCache == null || !methodCache.TryGetValue(type, out var methods))
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                finally { RecordAtomic(ref _maxGetMethodsTicks, started); }
                _methodEnumerations++;
                methodCache?.Add(type, methods);
            }
            foreach (var method in methods)
            {
                yield return true;
                if (!entry.MatchesMethod(method.Name)) continue;
                if (entry.Target != null && !MatchesTarget(entry.Target, method)) continue;
                exactMatches++;
                // Collect the actual interop declaration without installing anything when the
                // repository has only a method name, not reviewed parameter/marshalling evidence.
                if (entry.SignatureOnly)
                {
                    Skip(entry, method, "native-signature-review-required");
                    continue;
                }
                var reason = RejectionReason(entry, method);
                if (reason != null)
                {
                    Skip(entry, method, reason);
                    continue;
                }
                using (RecGate.Enter(Gate, "recorder.lock-timeout"))
                {
                    if (entry.Observes && ObservationPatches.ContainsKey(entry.EntryId!))
                    {
                        Skip(entry, method, "duplicate-observation-entryId");
                        continue;
                    }
                    // A second profile that names the same method keeps the first patch: two patches on one method
                    // would record the same call twice and rate-limit each other.
                    if (Patches.ContainsKey(method))
                    {
                        Skip(entry, method, "method-already-claimed");
                        continue;
                    }
                    var patch = new RecPatch(entry, method);
                    patch.Enabled = IsProfileEnabled(entry.Profile);
                    Patches.Add(method, patch);
                    Index.Add(patch);
                    if (entry.Observes) ObservationPatches.Add(entry.EntryId!, patch);
                    if (!Candidates.TryGetValue(entry.Profile, out var list)) Candidates.Add(entry.Profile, list = new List<RecPatch>());
                    list.Add(patch);
                }
            }
        }
        if (entry.Observes && exactMatches == 0)
            using (RecGate.Enter(Gate, "recorder.lock-timeout")) Skips.Add(new RecTraceSkip(entry.Profile, entry.Type, entry.Target!.Method,
                "exact-signature-not-found", entry.CatalogHash, entry.EntryId, entry.Target.Signature));
    }

    private static bool MatchesTarget(RecTraceTarget target, MethodInfo method)
        => method.DeclaringType?.FullName == target.DeclaringType && method.Name == target.Method
            && method.IsStatic == target.Static && method.ReturnType.FullName == target.ReturnType
            && method.GetParameters().Select(parameter => parameter.ParameterType.FullName)
                .SequenceEqual(target.ParameterTypes, StringComparer.Ordinal);

    /// <summary>Every method the tracer matched for a profile, installed or not. The session's startup report and a
    /// test read the same list.</summary>
    internal static IReadOnlyList<RecPatch> Matched(string profile)
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) return Candidates.TryGetValue(profile, out var list) ? list.ToArray() : Array.Empty<RecPatch>();
    }

    /// <summary>Offline lifecycle helper for exercising disable/re-enable identity and cleanup. Production profile
    /// settings are restart-required and never install synchronously through this entry point.</summary>
    internal static void SetProfileEnabled(string profile, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (_installQueue != null) throw new InvalidOperationException("Trace installation is still pending.");
            ProfileEnabled[profile] = enabled;
            if (Candidates.TryGetValue(profile, out var candidates)) foreach (var patch in candidates) patch.Enabled = enabled;
        }
        if (profile == "cov4-stamina") RecStaminaTrace.Reset();
        if (profile == RecMineFoamTrace.Profile) RecMineFoamTrace.Reset();
        if (enabled) InstallProfile(profile, null);
        else foreach (var patch in Patches.Values.Where(p => p.Entry.Profile == profile).ToArray()) Uninstall(patch);
        RecDiag.Info("development.trace-profile profile=" + profile + " enabled=" + enabled + "; reason="
            + (enabled ? "profile-enabled" : "profile-disabled"));
        RecSession.Write("session", "profile", json =>
        {
            json.WriteString("profile", profile);
            json.WriteBoolean("enabled", enabled);
            json.WriteNumber("patches", Patches.Count(p => p.Value.Entry.Profile == profile));
        });
        Publish(null);
        RecTraceCoverage.Publish("profile-changed");
    }

    internal static bool IsProfileEnabled(string profile)
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) return ProfileEnabled.TryGetValue(profile, out var enabled) && enabled;
    }

    /// <summary>Removes a patch. Called by the patch itself when it throws, and by <see cref="SetProfileEnabled"/>.</summary>
    internal static void Uninstall(RecPatch patch, string reason = "profile-disabled", bool publishReport = true)
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (!patch.Installed && !patch.CleanupPending) return;
            patch.Installed = false;
            patch.Gate.StopAdmission();
            Installed.Remove(patch.Subject);
        }
        string? removalError = null;
        var cause = patch.Removed ?? reason;
        try
        {
            if (patch.SharedHook != null) { patch.SharedHook.Dispose(); patch.SharedHook = null; }
            else if (patch.WrapperHook != null)
            {
                patch.WrapperHook.Dispose();
                if (patch.Method is MethodInfo managed) ForgeCore.Hooks.Disown(RecorderGuid, managed);
                patch.WrapperHook = null;
            }
            else if (patch.ObservationScope == "native-method")
            {
                if (!DevelopmentNativeHooks.Shared.RetryCleanup(patch.Method))
                    throw new InvalidOperationException("Shared native trace cleanup remains pending.");
            }
            else if (patch.Method is MethodInfo managed) ForgeCore.Hooks.Disown(RecorderGuid, managed);
            patch.CleanupPending = false;
        }
        catch (Exception error)
        {
            patch.CleanupPending = true;
            removalError = error.ToString();
            using (RecGate.Enter(Gate, "recorder.lock-timeout")) Errors.Add("unpatch " + patch.Subject + ": " + removalError);
        }
        RecSession.Write("session", removalError == null ? "patch_removed" : "patch_remove_failed", json =>
        {
            json.WriteString("profile", patch.Entry.Profile);
            json.WriteString("type", patch.Entry.Type);
            json.WriteString("method", patch.Method.Name);
            json.WriteString("reason", cause);
            json.WriteNumber("failures", patch.Failures);
            json.WriteBoolean("cleanupPending", patch.CleanupPending);
            json.WriteString("observationScope", patch.ObservationScope);
            json.WriteString("observationMoment", patch.ObservationMoment);
            if (removalError != null) json.WriteString("error", removalError);
        });
        // Successful cleanup is already retained per patch in the recorder and final index. It is not a
        // warning. A broken patch or cleanup still produces its own full error, without deduplication.
        if (removalError != null)
            RecDiag.Error("Forge recorder failed to remove trace patch " + patch.Subject + ": reason=" + cause
                + "; error=" + removalError + "; utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (publishReport) Publish(null);
    }

    /// <summary>Releases every patch. The plugin's failure path calls this, and a process that is leaving does not
    /// need the patches removed; a failed startup does.</summary>
    internal static void UninstallAll(string reason = "recorder-stop")
    {
        RecPatch[] patches;
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            _stopping = true;
            _discovery?.Dispose(); _discovery = null;
            _publication?.Dispose(); _publication = null;
            _installQueue = null;
            if (_installStarted != 0 && _installEnded == 0) _installEnded = System.Diagnostics.Stopwatch.GetTimestamp();
            patches = Patches.Values.ToArray();
            // Seal every gate before the first physical removal. A late prefix holding a previously
            // returned patch cannot add rate drops after the forced terminal summary.
            foreach (var patch in patches) patch.Gate.StopAdmission();
        }
        RecAcousticObservation.Reset();
        RecStaminaTrace.Reset();
        RecMineFoamTrace.Reset();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        RecDiag.Info("development.trace-stop-start utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            + "; patches=" + patches.Count(patch => patch.Installed) + "; reason=" + reason);
        foreach (var patch in patches)
        {
            try { Uninstall(patch, reason, publishReport: false); }
            catch (Exception error)
            {
                patch.CleanupPending = true;
                using (RecGate.Enter(Gate, "recorder.lock-timeout")) Errors.Add("unpatch " + patch.Subject + ": " + error);
            }
            // Keep overflow accounting while the session still accepts teardown evidence. The count is
            // retained in the terminal patch report even when the ordinary record budget refuses it.
            patch.ReportOverflow(true);
        }
        RecTraceCoverage.Publish("trace-stopped");
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            Installed.Clear(); ProfileEnabled.Clear(); _released = true;
            // Retain matched methods and errors for the session's final index. The next load starts fresh.
        }
        Publish(null);
        RecDiag.Info("development.trace-stop-complete utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            + "; elapsedMs=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)
            + "; reason=" + reason);
    }

    /// <summary>The process is ending, so its native hooks end with it and removing 3000+ of them one by one only delays
    /// the exit (about 4 ms each). This seals every patch's admission and writes the same terminal evidence
    /// <see cref="UninstallAll"/> leaves: the per-patch overflow summaries, the coverage list and the final report.
    /// It removes nothing: the report keeps `installed` and `removed` as they were, the session records that the
    /// hooks were left in place, and <see cref="RecRuntime"/> refuses a later start so their leases are never orphaned.</summary>
    internal static void ReleaseForProcessExit()
    {
        RecPatch[] patches;
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            _stopping = true;
            _discovery?.Dispose(); _discovery = null;
            _publication?.Dispose(); _publication = null;
            _installQueue = null;
            if (_installStarted != 0 && _installEnded == 0) _installEnded = System.Diagnostics.Stopwatch.GetTimestamp();
            patches = Patches.Values.ToArray();
            foreach (var patch in patches) patch.Gate.StopAdmission();
        }
        RecAcousticObservation.Reset();
        RecStaminaTrace.Reset();
        RecMineFoamTrace.Reset();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var installed = patches.Count(patch => patch.Installed);
        RecDiag.Info("development.trace-stop-start utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            + "; patches=" + installed + "; reason=process-exit; physicalRemoval=skipped");
        foreach (var patch in patches) patch.ReportOverflow(true);
        RecTraceCoverage.Publish("trace-stopped");
        RecSession.Write("session", "trace_process_exit", json =>
        {
            json.WriteNumber("patches", installed);
            json.WriteBoolean("physicalRemoval", false);
            json.WriteString("reason", "process-exit; the hooks end with the process");
        });
        Publish(null);
        RecDiag.Info("development.trace-stop-complete utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            + "; elapsedMs=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)
            + "; reason=process-exit; physicalRemoval=skipped");
    }

    /// <summary>The patch a Harmony prefix or postfix belongs to. A method that is no longer patched answers null, so
    /// a call already inside the patched body when the patch is removed writes nothing. The lookup is the install-time
    /// snapshot array: no global lock and no dictionary probe on the patched call.</summary>
    internal static RecPatch? Get(MethodBase method)
        => !_stopping && Index.TryGet(method, out var patch) && patch.Installed && patch.Removed == null && patch.Enabled
            ? patch : null;

    internal static bool IsActive(RecPatch patch) => !_stopping && patch.Enabled && patch.Installed && patch.Removed == null;

    private static IDisposable DeclareManaged(MethodBase method, MethodInfo prefix, MethodInfo postfix, MethodInfo? finalizer)
    {
        if (method is not MethodInfo target) throw new NotSupportedException("Managed trace requires a method target.");
        return DevelopmentHookInstall.Managed(RecorderGuid, target, prefix, postfix, finalizer,
            Priority.First, Priority.Last, Priority.Last);
    }

    private static bool CanInstall(RecPatch patch) => !patch.Installed && patch.Removed == null
        && !patch.AdmissionRefused && !patch.CleanupPending && !patch.Method.IsPatched();

    private static void InstallPatch(RecPatch patch) => InstallProfile(patch.Entry.Profile, patch.Entry, patch);

    private static void InstallProfile(string profile, RecTraceEntry? only, RecPatch? requested = null)
    {
        if (!IsProfileEnabled(profile))
        {
            foreach (var patch in Patches.Values.Where(patch => patch.Entry.Profile == profile
                && patch.Entry.Observes))
                patch.NativeReason = "profile-disabled";
            return;
        }
        var candidates = requested == null
            ? Patches.Values.Where(p => p.Entry.Profile == profile
                && (only == null || ReferenceEquals(p.Entry, only)) && CanInstall(p)).ToArray()
            : requested.Entry.Profile == profile && (only == null || ReferenceEquals(requested.Entry, only))
                && CanInstall(requested) ? new[] { requested } : Array.Empty<RecPatch>();
        foreach (var patch in candidates)
        {
            var managedInstallAttempted = false;
            var sharedInstallAttempted = false;
            try
            {
                var native = RecNativePatchSafety.ObservationScope(patch.Method) == "native-method";
                // Discovery already ran the native preflight for this method. ForgeCore admits the physical observation
                // when it is installed, so a native target is not preflighted a second time here: its refusal, if the
                // state changed since discovery, arrives from the subscription below and is reported the same way.
                var reason = native ? null : RecNativePatchSafety.RejectionReason(patch.Method);
                patch.NativeReason = reason;
                if (reason != null)
                {
                    patch.AdmissionRefused = true;
                    Skip(patch.Entry, patch.Method, reason);
                    continue;
                }
                if (patch.Entry.Observes)
                {
                    if (native)
                    {
                        sharedInstallAttempted = true;
                        patch.SharedHook = DevelopmentNativeHooks.Shared.Subscribe(patch.Method, "tracer:" + patch.Id,
                            new[]
                            {
                                new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Prefix,
                                    RecTracerPatches.ObservationPrefixMethod),
                                new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Postfix,
                                    RecTracerPatches.ObservationReturnedMethod)
                            }, DevelopmentNativeHooks.Order.Trace,
                            DevelopmentNativeHooks.Lane.FinalizerReturned);
                        patch.ObservationScope = "native-method";
                        patch.ObservationMoment = "after-native-postfixes-in-finalizer";
                    }
                    else
                    {
                        managedInstallAttempted = true;
                        patch.WrapperHook = DeclareManaged(patch.Method, RecTracerPatches.ObservationManagedPrefixMethod,
                            RecTracerPatches.ObservationManagedReturnedMethod, RecTracerPatches.ObservationFinalizerMethod);
                        patch.ObservationScope = "managed-method";
                        patch.ObservationMoment = "after-returned-finalizer";
                    }
                    patch.Installation++;
                    patch.InstallationId = RecTracer.InstallationIdentity(patch.Id, patch.Installation);
                    patch.Gate.ResumeAdmission();
                    patch.Installed = true;
                    using (RecGate.Enter(Gate, "recorder.lock-timeout")) Installed.Add(patch.Subject);
                    continue;
                }
                var noise = patch.Method is MethodInfo noiseMethod && RecNoiseTrace.Supports(noiseMethod);
                var count = patch.Entry.Record == "count";
                var staticNoise = !count && patch.Method is MethodInfo staticNoiseMethod && RecNoiseTrace.SupportsValue(staticNoiseMethod);
                var processNode = !count && patch.Method is MethodInfo processNodeMethod && RecNoiseTrace.SupportsProcessNode(processNodeMethod);
                var prefix = new HarmonyMethod(processNode ? RecNoiseTrace.ProcessNodePrefixMethod : staticNoise ? RecNoiseTrace.MakeNoisePrefixMethod : noise ? RecNoiseTrace.PrefixMethod
                    : count ? RecTracerPatches.PrefixCountMethod : RecTracerPatches.PrefixMethod)
                { priority = Priority.First };
                var postfix = new HarmonyMethod(processNode ? RecNoiseTrace.ProcessNodePostfixMethod : staticNoise ? RecNoiseTrace.MakeNoisePostfixMethod : noise ? RecNoiseTrace.PostfixMethod
                    : count ? RecTracerPatches.PostfixCountMethod
                    : patch.Method is MethodInfo typed && typed.ReturnType == typeof(void)
                    ? RecTracerPatches.PostfixVoidMethod : RecTracerPatches.PostfixMethod)
                { priority = Priority.Last };
                var finalizer = patch.Entry.RecordsPair ? new HarmonyMethod(
                    noise || staticNoise || processNode ? RecNoiseTrace.FinalizerMethod : RecTracerPatches.FinalizerMethod)
                { priority = Priority.Last } : null;
                if (native)
                {
                    patch.ObservationScope = "native-method";
                    patch.ObservationMoment = "after-native-postfixes-in-finalizer";
                    var isStatic = patch.Method.IsStatic;
                    var returnsVoid = ((MethodInfo)patch.Method).ReturnType == typeof(void);
                    var before = processNode ? RecTracerPatches.SharedProcessNodePrefixMethod
                        : staticNoise ? RecTracerPatches.SharedMakeNoisePrefixMethod
                        : noise ? RecTracerPatches.SharedNoisePrefixMethod
                        : isStatic ? count ? RecTracerPatches.SharedStaticCountPrefixMethod : RecTracerPatches.SharedStaticPrefixMethod
                        : count ? RecTracerPatches.PrefixCountMethod : RecTracerPatches.PrefixMethod;
                    var after = processNode ? RecTracerPatches.SharedProcessNodeReturnedMethod
                        : staticNoise ? RecTracerPatches.SharedMakeNoiseReturnedMethod
                        : noise ? RecTracerPatches.SharedNoiseReturnedMethod
                        : isStatic ? count ? RecTracerPatches.SharedStaticCountReturnedMethod
                            : returnsVoid ? RecTracerPatches.SharedStaticVoidMethod : RecTracerPatches.SharedStaticReturnedMethod
                        : count ? RecTracerPatches.PostfixCountMethod
                        : returnsVoid ? RecTracerPatches.PostfixVoidMethod : RecTracerPatches.SharedReturnedMethod;
                    var callbacks = new[]
                    {
                        new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Prefix, before),
                        new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Postfix, after),
                        new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Finalizer, RecTracerPatches.FinalizerMethod)
                    };
                    sharedInstallAttempted = true;
                    patch.SharedHook = DevelopmentNativeHooks.Shared.Subscribe(patch.Method, "tracer:" + patch.Id,
                        callbacks, DevelopmentNativeHooks.Order.Trace, DevelopmentNativeHooks.Lane.FinalizerReturned);
                }
                else
                {
                    // Explicitly managed targets. This branch may never install a native game entry.
                    if (RecNativePatchSafety.ObservationScope(patch.Method) != "managed-method")
                        throw new InvalidOperationException("Managed trace installation requires a managed method.");
                    managedInstallAttempted = true;
                    patch.WrapperHook = DeclareManaged(patch.Method, prefix.method, postfix.method, finalizer?.method);
                    patch.ObservationScope = "managed-method";
                    patch.ObservationMoment = "managed-postfix";
                }
                patch.Installation++;
                patch.Gate.ResumeAdmission();
                patch.Installed = true;
                using (RecGate.Enter(Gate, "recorder.lock-timeout")) Installed.Add(patch.Subject);
            }
            catch (NotSupportedException refusal) when (sharedInstallAttempted && !managedInstallAttempted)
            {
                // The shared owner's admission refused the target: the same outcome the removed second preflight reported.
                using (RecGate.Enter(Gate, "recorder.lock-timeout"))
                {
                    patch.NativeReason = refusal.Message;
                    patch.AdmissionRefused = true;
                    Skip(patch.Entry, patch.Method, refusal.Message);
                }
            }
            catch (Exception error)
            {
                using (RecGate.Enter(Gate, "recorder.lock-timeout"))
                {
                    Errors.Add("patch " + patch.Subject + ": " + error);
                    var reason = sharedInstallAttempted && error.GetBaseException() is NotSupportedException refusal
                        ? refusal.Message : "trace-installation-failed:" + error.GetBaseException().GetType().Name;
                    Skip(patch.Entry, patch.Method, reason);
                    if (!reason.Contains("native", StringComparison.Ordinal))
                        RecTraceCoverage.Note(patch.Entry.Profile, patch.Method, reason, patch.ObservationScope);
                    if (sharedInstallAttempted)
                        patch.CleanupPending = DevelopmentNativeHooks.Shared.Inspect().Any(status => status.Target.Equals(patch.Method) && status.CleanupPending);
                    try
                    {
                        if (patch.WrapperHook != null) { patch.WrapperHook.Dispose(); patch.WrapperHook = null; }
                        if (managedInstallAttempted && patch.Method is MethodInfo managed)
                            ForgeCore.Hooks.Disown(RecorderGuid, managed);
                    }
                    catch (Exception cleanup)
                    {
                        patch.CleanupPending = true;
                        Errors.Add("partial patch cleanup " + patch.Subject + ": " + cleanup);
                    }
                    patch.Fail(error, report: false);
                }
            }
        }
    }

    private static void Skip(RecTraceEntry entry, MethodBase method, string reason)
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) Skips.Add(new RecTraceSkip(entry.Profile,
            method.DeclaringType?.FullName ?? entry.Type, method.Name, reason, entry.CatalogHash,
            entry.EntryId, entry.Target?.Signature));
        if (!IsProfileEnabled(entry.Profile) || !reason.Contains("native", StringComparison.Ordinal)) return;
        if (!RecTraceCoverage.Note(entry.Profile, method, reason)) return;
        RecSession.Write("session", "trace_refused", json =>
        {
            json.WriteString("profile", entry.Profile);
            json.WriteString("type", method.DeclaringType?.FullName ?? entry.Type);
            // A refused signature may be precisely what CLR cannot materialize.
            // Recording that refusal must not ask reflection to parse it again.
            json.WriteString("method", method.Name);
            json.WriteString("reason", reason);
            json.WriteString("signature", RecTraceCoverage.Signature(method));
            json.WriteString("observationScope", "unavailable");
        });
    }

    /// <summary>Methods the tracer refuses whatever a profile says, and the reason it reports. Generic methods and
    /// methods with a pointer, by-ref or open generic parameter are refused by signature: Harmony cannot bind them
    /// generically, and a tracer that half-installs one is worse than one that says it did not.</summary>
    internal static string? RejectionReason(RecTraceEntry entry, MethodInfo method)
    {
        if (!IsGameAssembly(method.DeclaringType?.Assembly.GetName().Name)) return "non-game-assembly";
        var owner = method.DeclaringType?.FullName ?? "";
        if (owner.StartsWith("UnityEngine.", StringComparison.Ordinal)) return "unity-engine-type";
        if (FrameworkType(owner)) return "reflection-or-serialization-framework";
        // Native struct instance methods receive an unboxed value pointer. The
        // generic Harmony __instance object adapter is only valid for reference
        // receivers; boxing this pointer changes the ABI and may corrupt calls.
        if (!method.IsStatic && RecNativePatchSafety.ValueReceiver(method.DeclaringType)) return "unsupported-native-value-type-instance";
        if (method.IsGenericMethodDefinition || method.ContainsGenericParameters) return "generic";
        try
        {
            if (method.GetParameters().Any(p => p.ParameterType.IsPointer || p.ParameterType.IsByRef
                || p.ParameterType.IsGenericParameter || p.ParameterType.ContainsGenericParameters)
                && !(entry.RecordsPair && (RecNoiseTrace.Supports(method) || RecNoiseTrace.SupportsProcessNode(method)))) return "unsupported-parameter";
            var result = method.ReturnType;
            if (result.IsPointer || result.IsByRef || result.IsByRefLike || result.ContainsGenericParameters)
                return "unsupported-native-return";
        }
        catch (TypeLoadException) { return "unreadable-native-signature"; }
        if (entry.RecordsPair && entry.InstanceFields.Count == 0 && !RecNoiseTrace.SupportsStatic(method) && !RecStaminaTrace.IsTarget(entry, method) && !RecMineFoamTrace.IsTarget(entry, method))
            return "unsupported-static-noise-pair";
        // A property accessor is refused by its name: the interop assemblies project a property as a `get_` method
        // whose `IsSpecialName` is not set, so the name is the only thing that identifies one.
        var getter = method.Name.StartsWith("get_", StringComparison.Ordinal);
        var policy = RecTracer.RejectionReason(entry, method.Name, getter);
        if (policy != null) return policy;
        // A profile that is off is parsed and matched by name, but its targets are not admitted: nothing will be
        // installed, so the metadata and Runtime readbacks would be spent on a hook that never exists. The patch
        // reports "not-evaluated" and is admitted for real if the profile is ever turned on.
        if (!IsProfileEnabled(entry.Profile)) return null;
        return RecNativePatchSafety.ObservationScope(method) == "native-method"
            ? DevelopmentNativeHooks.Shared.Preflight(method) : RecNativePatchSafety.RejectionReason(method);
    }

    private static IEnumerable<Type?> FindTypes(string pattern, Dictionary<Assembly, RecTypeIndex>? assemblyTypes = null)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name ?? "";
            if (!IsGameAssembly(name)) continue;
            if (assemblyTypes == null || !assemblyTypes.TryGetValue(assembly, out var index))
            {
                Type[] all;
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                try { all = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException error)
                {
                    RecDiag.Error("development.trace-discovery-failed assembly=" + name + ": " + error);
                    continue;
                }
                finally { RecordAtomic(ref _maxGetTypesTicks, started); }
                _typeEnumerations++;
                index = new RecTypeIndex();
                foreach (var type in all) { index.Append(type, FrameworkType); yield return null; }
                if (!assembly.IsDynamic) assemblyTypes?.Add(assembly, index);
            }
            foreach (var type in index.SelectSteps(pattern)) yield return type;
        }
    }

    // Only generated game interop assemblies. Managed plugins, CLR/Unity support,
    // reflection and JSON implementations never become tracer targets, even for '*'.
    internal static bool IsGameAssembly(string? name) => name is "Modules-ASM" or "GameData-ASM" or "SNet_ASM"
        or "Utils-ASM" or "GlobalFramework-ASM" or "Shards-ASM" or "CellSound-ASM" or "DropServerSDK-ASM";

    private static readonly string[] FrameworkNamespaces = { "System.", "Il2CppSystem.", "Newtonsoft.", "Il2CppNewtonsoft.",
        "BepInEx.", "HarmonyLib.", "Il2CppInterop.", "MonoMod.", "Mono.Cecil.", "Iced.", "ForgeDevelopment.", "ForgeEngine." };
    private static bool FrameworkType(string name) => FrameworkNamespaces.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsPatched(this MethodBase method)
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) return !_stopping && Patches.TryGetValue(method, out var patch) && patch.Installed;
    }

    private static void Publish(IEnumerable<RecTraceProfile>? profiles)
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            // The report is cumulative: a profile matched later never erases the one matched before it, so the
            // session's startup facts and index.forge describe every configuration file that was read.
            if (profiles != null)
                foreach (var profile in profiles) Known[profile.Name] = profile;
            foreach (var _ in PublishSteps()) { }
        }
    }

    private static IEnumerable<bool> PublishSteps()
        => RecTraceReport.JsonSteps(Known.Values, Skips, Installed, Errors,
            Candidates.Values.SelectMany(x => x), profile => ProfileEnabled.GetValueOrDefault(profile),
            null, report => _report = report, stage => _startupStage = stage);

    private static readonly Dictionary<string, RecTraceProfile> Known = new(StringComparer.Ordinal);
}

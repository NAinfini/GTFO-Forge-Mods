using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ForgeDevelopment.Native;

internal delegate void DiagnosticIssueObserver(DiagnosticsReport report, string type, string source,
    string message, string stack, string severity);

public sealed class DiagnosticsReport
{
    private const int MaxMetadata = 128;
    private const int MaxEvents = 4096;
    private const int MaxDetailedEvents = 3072, MaxAggregates = 1024;
    private const int MaxChecks = 2048;
    private const int MaxIssues = 1024;
    private const int MaxFieldsPerEvent = 32;
    private const int MaxKeyLength = 128;
    private const int MaxValueLength = 4096;
    private const int MaxStackLength = 16384;

    // These identities are never dropped by the byte budget: without them a report cannot be
    // associated with an exact project, manifest and authoring revision.
    private static readonly HashSet<string> CriticalMetadata = new(StringComparer.Ordinal)
    {
        "projectId", "projectManifestHash", "experiment:authoringSha256", "experiment:packageVersion"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Compact, so the measured budget and the published file are exactly the same bytes.
        WriteIndented = false
    };

    private static readonly object IssueObserverGate = new();
    private static DiagnosticIssueObserver? _issueObserver;

    internal static void ObserveIssues(DiagnosticIssueObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (IssueObserverGate)
        {
            if (_issueObserver == observer) return;
            if (_issueObserver != null)
                throw new InvalidOperationException("A diagnostics issue observer is already active.");
            _issueObserver = observer;
        }
    }

    internal static void StopObservingIssues(DiagnosticIssueObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (IssueObserverGate)
        {
            if (_issueObserver == null) return;
            if (_issueObserver != observer)
                throw new InvalidOperationException("The active diagnostics issue observer has another owner.");
            _issueObserver = null;
        }
    }

    private readonly object _gate = new();
    private readonly string _runId;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, string> _metadata = new(StringComparer.Ordinal);
    private readonly List<EventEntry> _events = new();
    private readonly Dictionary<(string Category, string Stage, string Context), AggregateEntry> _aggregates = new();
    private int _detailedEvents;
    private long _sampledEvents, _droppedAggregates;
    private readonly List<CheckEntry> _checks = new();
    // Native object identity is part of the aggregation key: two failures at the same source
    // are different findings when they happened on different native objects.
    private readonly Dictionary<(string Type, string Source, DiagnosticNativeObject? NativeObject, string Severity), IssueEntry> _issues = new();
    private ProjectObjectReferenceScan? _objectReferences;
    private long _droppedMetadata;
    private long _droppedEvents;
    private long _droppedChecks;
    private long _droppedIssues;
    private long _droppedEventFields;
    private long _truncatedStrings;
    // Bumped under the gate by every call that can change what an export would contain. A periodic export
    // compares it with the last exported value and skips the freeze and the file when nothing moved.
    private long _revision;

    // Every report carries an object reference receipt. Until a real scan is attached it is the
    // authoritative document for an unscanned world: epoch 0 stays rejected, so an absent scan
    // can never be read as a successful one.
    private static readonly ProjectObjectReferenceScan UnattachedReferences =
        new(Array.Empty<ProjectObjectDeclaration>(), 0, null, ProjectSourceVerification.NotProvided);

    public DiagnosticsReport(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Run ID is required.", nameof(runId));
        _runId = Trim(runId, MaxValueLength);
        if (_runId.Length != runId.Length) _truncatedStrings = 1;
    }

    // The report performs no matching of its own. It serializes the scan's latest immutable
    // snapshot on every export, so a file written earlier is never rewritten by later
    // observations and a scan that already produced a snapshot keeps that snapshot's state.
    internal void AttachObjectReferences(ProjectObjectReferenceScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        lock (_gate) { _objectReferences = scan; _revision++; }
    }

    /// <summary>What an export would contain, as one comparable value: this report's own revision plus the attached
    /// scan's, because the scan changes on its own while the report does not.</summary>
    internal ReportStamp Stamp()
    {
        ProjectObjectReferenceScan? scan;
        long revision;
        lock (_gate) { scan = _objectReferences; revision = _revision; }
        return new ReportStamp(revision, scan?.Revision ?? 0);
    }

    internal readonly record struct ReportStamp(long Revision, long ScanRevision);

    /// <summary>What the last queued export contained. The periodic export asks it whether this report, in this state
    /// and with this outcome, was already exported; every export that an event asks for ignores it.</summary>
    internal sealed class ExportMemo
    {
        private DiagnosticsReport? _report;
        private ReportStamp _stamp;
        private string _outcome = "";

        internal bool Exported(DiagnosticsReport report, ReportStamp stamp, string outcome)
            => ReferenceEquals(_report, report) && _stamp == stamp && _outcome == outcome;

        internal void Remember(DiagnosticsReport report, ReportStamp stamp, string outcome)
        { _report = report; _stamp = stamp; _outcome = outcome; }

        internal void Forget() => _report = null;
    }

    public void SetMetadata(string key, string value)
    {
        lock (_gate)
        {
            key = NormalizeRequired(key, nameof(key), MaxKeyLength);
            var truncatedBefore = _truncatedStrings;
            value = Normalize(value, MaxValueLength);
            var known = _metadata.TryGetValue(key, out var previous);
            if (!known && _metadata.Count >= MaxMetadata)
            {
                Increment(ref _droppedMetadata);
                _revision++;
                return;
            }

            // The same value written again changes nothing, unless it was cut short again and the cut is counted.
            if (known && previous == value && _truncatedStrings == truncatedBefore) return;
            _metadata[key] = value;
            _revision++;
        }
    }

    public void Event(
        string category,
        string stage,
        string subject,
        Dictionary<string, string>? fields = null,
        double elapsedMs = 0)
    {
        if (!double.IsFinite(elapsedMs) || elapsedMs < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMs), "Elapsed time must be finite and non-negative.");

        lock (_gate)
        {
            _revision++;
            category = NormalizeRequired(category, nameof(category), MaxKeyLength);
            stage = NormalizeRequired(stage, nameof(stage), MaxKeyLength);
            subject = NormalizeRequired(subject, nameof(subject), MaxValueLength);
            bool detailed = IsDetailedEvent(category);
            var now = DateTimeOffset.UtcNow;
            AggregateEntry? aggregate = null;
            if (detailed)
            {
                // Aggregate by source type and spatial context, never by display name or pointer.
                string context = Normalize(string.Join("|", fields?.GetValueOrDefault("jobType") ?? "",
                    fields?.GetValueOrDefault("zone") ?? "", fields?.GetValueOrDefault("geomorph") ?? ""), MaxValueLength);
                var key = (category, stage, context);
                if (!_aggregates.TryGetValue(key, out aggregate))
                {
                    if (_aggregates.Count < MaxAggregates)
                    {
                        aggregate = new AggregateEntry
                        {
                            Category = category,
                            Stage = stage,
                            Context = context,
                            FirstSeenUtc = now,
                            FirstSubject = subject,
                            FirstFields = CopyFields(fields)
                        };
                        _aggregates.Add(key, aggregate);
                    }
                    else Increment(ref _droppedAggregates);
                }
                if (aggregate != null)
                {
                    if (aggregate.Count < long.MaxValue) aggregate.Count++;
                    aggregate.LastSeenUtc = now; aggregate.LastSubject = subject;
                    aggregate.TotalElapsedMs += elapsedMs; aggregate.MaximumElapsedMs = Math.Max(aggregate.MaximumElapsedMs, elapsedMs);
                    aggregate.LastRandomBefore = Normalize(fields?.GetValueOrDefault("randomBefore"), MaxValueLength);
                    aggregate.LastRandomAfter = Normalize(fields?.GetValueOrDefault("randomAfter"), MaxValueLength);
                }
                if (_detailedEvents >= MaxDetailedEvents)
                { Increment(ref _sampledEvents); return; }
            }
            if (_events.Count >= MaxEvents) { Increment(ref _droppedEvents); return; }
            if (detailed) _detailedEvents++;
            var copiedFields = CopyFields(fields);

            _events.Add(new EventEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Category = category,
                Stage = stage,
                Subject = subject,
                ElapsedMs = elapsedMs,
                Fields = copiedFields
            });
        }
    }

    private SortedDictionary<string, string> CopyFields(Dictionary<string, string>? fields)
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (fields == null) return copy;
        foreach (var pair in fields)
        {
            var key = NormalizeRequired(pair.Key, nameof(fields), MaxKeyLength);
            if (!copy.ContainsKey(key) && copy.Count >= MaxFieldsPerEvent) { Increment(ref _droppedEventFields); continue; }
            copy[key] = Normalize(pair.Value, MaxValueLength);
        }
        return copy;
    }

    /// <param name="origin">Who the finding belongs to: <c>forge</c> (this package and the Forge packages running it; every finding Forge raises itself),
    /// <c>game</c> (the game and Unity), or <c>mod</c> (another installed mod). The website keeps the three apart and counts each.</param>
    public void Issue(string type, string source, string message, string stack = "", DiagnosticNativeObject? nativeObject = null, string severity = "error", string origin = "forge")
    {
        if (severity is not ("info" or "warning" or "error")) throw new ArgumentException("Unknown diagnostic severity.", nameof(severity));
        if (origin is not ("forge" or "game" or "mod")) throw new ArgumentException("Unknown diagnostic origin.", nameof(origin));
        DiagnosticIssueObserver? observer;
        lock (IssueObserverGate) observer = _issueObserver;
        observer?.Invoke(this, type, source, message, stack, severity);
        lock (_gate)
        {
            _revision++;
            type = NormalizeRequired(type, nameof(type), MaxKeyLength);
            source = NormalizeRequired(source, nameof(source), MaxValueLength);
            var key = (type, source, nativeObject, severity);
            var now = DateTimeOffset.UtcNow;
            if (_issues.TryGetValue(key, out var existing))
            {
                if (existing.Count < long.MaxValue) existing.Count++;
                existing.LastSeenUtc = now;
                if (existing.RepresentativeStack.Length == 0 && !string.IsNullOrWhiteSpace(stack))
                    existing.RepresentativeStack = Normalize(stack, MaxStackLength);
                return;
            }

            if (_issues.Count >= MaxIssues)
            {
                Increment(ref _droppedIssues);
                return;
            }

            _issues.Add(key, new IssueEntry
            {
                Type = type,
                Severity = severity,
                Origin = origin,
                Source = source,
                NativeObject = nativeObject,
                Message = NormalizeRequired(message, nameof(message), MaxValueLength),
                RepresentativeStack = Normalize(stack, MaxStackLength),
                Count = 1,
                FirstSeenUtc = now,
                LastSeenUtc = now
            });
        }
    }

    public void Check(string kind, string subject, string status, string detail, DiagnosticNativeObject? nativeObject = null)
    {
        lock (_gate)
        {
            _revision++;
            if (_checks.Count >= MaxChecks)
            {
                Increment(ref _droppedChecks);
                return;
            }

            _checks.Add(new CheckEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = NormalizeRequired(kind, nameof(kind), MaxKeyLength),
                Subject = NormalizeRequired(subject, nameof(subject), MaxValueLength),
                Status = string.IsNullOrWhiteSpace(status) ? "not_checked" : Normalize(status, MaxKeyLength),
                Detail = Normalize(detail, MaxValueLength),
                NativeObject = nativeObject
            });
        }
    }

    public string Export(string path, string outcome) => Freeze(outcome).Export(path);

    // Captures managed evidence only. Serialization, byte-budget reduction and IO
    // can run later without retaining the live report or its mutable scan.
    internal FrozenReport Freeze(string outcome) => new(this, outcome);

    internal sealed class FrozenReport
    {
        private readonly ReportDocument document;
        internal FrozenReport(DiagnosticsReport owner, string outcome)
            => document = owner.Capture(outcome);
        internal string Export(string path) => ExportSnapshot(path, ApplyByteBudget(document));
    }

    private static string ExportSnapshot(string path, ReportDocument snapshot)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Export path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (Path.GetDirectoryName(fullPath) == null) throw new ArgumentException("Export path has no directory.", nameof(path));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        // Second enforcement point: an over-budget file is refused instead of being published, and the
        // existing target keeps its bytes.
        if (bytes.Length > ProjectObjectReferences.MaximumReportBytes)
            throw new InvalidDataException(
                $"Refusing to write a report of {bytes.Length} UTF-8 bytes; the budget is {ProjectObjectReferences.MaximumReportBytes}.");
        ForgeCore.Store.WriteBytes(fullPath, bytes);
        return fullPath;
    }

    // The count limits alone cannot bound the file: 4096 events with 32 fields of 4096 escaped
    // characters are several GiB of JSON. The document is assembled outside the lock, measured as
    // exact UTF-8 bytes and reduced in one fixed order until it fits the report byte budget. Entries
    // are dropped whole and counted; the object reference receipt, the overflow object and the
    // critical metadata identities are never dropped.
    private ReportDocument Capture(string outcome)
    {
        string normalizedOutcome;
        SortedDictionary<string, string> metadata;
        EventEntry[] events;
        AggregateEntry[] aggregates;
        CheckEntry[] checks;
        IssueEntry[] issues;
        object references;
        long droppedMetadata, droppedEvents, sampledDetailEvents, droppedAggregateEvents;
        long droppedChecks, droppedIssues, droppedEventFields, truncatedStrings;
        lock (_gate)
        {
            normalizedOutcome = Normalize(outcome, MaxValueLength);
            metadata = new SortedDictionary<string, string>(_metadata, StringComparer.Ordinal);
            events = _events.ToArray();
            aggregates = _aggregates.Values.Select(value => value.Copy()).ToArray();
            checks = _checks.ToArray();
            issues = _issues.Values.OrderBy(issue => issue.Type, StringComparer.Ordinal)
                .ThenBy(issue => issue.Source, StringComparer.Ordinal)
                .ThenBy(issue => issue.NativeObject?.Kind ?? "", StringComparer.Ordinal)
                .ThenBy(issue => issue.NativeObject?.InstanceId ?? 0)
                .Select(issue => issue.Copy())
                .ToArray();
            references = (_objectReferences ?? UnattachedReferences).Snapshot().Document;
            droppedMetadata = _droppedMetadata;
            droppedEvents = _droppedEvents;
            sampledDetailEvents = _sampledEvents;
            droppedAggregateEvents = _droppedAggregates;
            droppedChecks = _droppedChecks;
            droppedIssues = _droppedIssues;
            droppedEventFields = _droppedEventFields;
            truncatedStrings = _truncatedStrings;
        }

        return new ReportDocument
        {
            RunId = _runId,
            StartedAtUtc = _startedAtUtc,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Outcome = normalizedOutcome,
            Playability = "not_assessed",
            Metadata = metadata,
            Events = events,
            EventAggregates = aggregates,
            Checks = checks,
            Issues = issues,
            ObjectReferences = references,
            Overflow = new OverflowEntry
            {
                DroppedMetadata = droppedMetadata,
                DroppedEvents = droppedEvents,
                SampledDetailEvents = sampledDetailEvents,
                DroppedAggregateEvents = droppedAggregateEvents,
                DroppedChecks = droppedChecks,
                DroppedIssues = droppedIssues,
                DroppedEventFields = droppedEventFields,
                TruncatedStrings = truncatedStrings
            }
        };
    }

    private static ReportDocument ApplyByteBudget(ReportDocument snapshot)
    {
        var normalizedOutcome = snapshot.Outcome;
        var metadata = snapshot.Metadata;
        var events = snapshot.Events;
        var aggregates = snapshot.EventAggregates;
        var checks = snapshot.Checks;
        var issues = snapshot.Issues;
        var references = snapshot.ObjectReferences;
        var exportedAtUtc = snapshot.ExportedAtUtc;
        var droppedMetadata = snapshot.Overflow.DroppedMetadata;
        var droppedEvents = snapshot.Overflow.DroppedEvents;
        var sampledDetailEvents = snapshot.Overflow.SampledDetailEvents;
        var droppedAggregateEvents = snapshot.Overflow.DroppedAggregateEvents;
        var droppedChecks = snapshot.Overflow.DroppedChecks;
        var droppedIssues = snapshot.Overflow.DroppedIssues;
        var droppedEventFields = snapshot.Overflow.DroppedEventFields;
        var truncatedStrings = snapshot.Overflow.TruncatedStrings;
        var optionalMetadata = metadata.Where(pair => !CriticalMetadata.Contains(pair.Key)).ToArray();
        var detailed = 0;
        foreach (var entry in events) if (IsDetailedEvent(entry.Category)) detailed++;
        var aggregateCounts = CountPrefix(aggregates.Select(aggregate => aggregate.Count).ToArray());
        var issueCounts = CountPrefix(issues.Select(issue => issue.Count).ToArray());
        // One fixed reduction order, first dropped first: detailed events, event aggregates, ordinary
        // events, non-critical metadata, checks, issues. Each stage is measured while every earlier
        // stage is already reduced and every later stage is still intact, so dropping a lower
        // priority entry never removes a higher priority one.
        var keep = new[] { detailed, aggregates.Length, events.Length - detailed,
            optionalMetadata.Length, checks.Length, issues.Length };
        var size = Measure(Build(keep));
        if (size > ProjectObjectReferences.MaximumReportBytes)
        {
            // The core is the report without any droppable entry: receipt, overflow, critical
            // identities and the fixed fields. If even that is too large, nothing may be cut.
            if (Measure(Build(new int[keep.Length])) > ProjectObjectReferences.MaximumReportBytes)
                throw new InvalidDataException(
                    $"The report receipt, overflow and critical identities cannot fit {ProjectObjectReferences.MaximumReportBytes} UTF-8 bytes.");
            // Stages are measured, entries never are: each stage is bisected to its largest prefix
            // that still fits.
            for (var stage = 0; stage < keep.Length && size > ProjectObjectReferences.MaximumReportBytes; stage++)
            {
                if (keep[stage] == 0) continue;
                var low = 0;
                var lowSize = SizeWith(stage, 0);
                if (lowSize <= ProjectObjectReferences.MaximumReportBytes)
                    for (var high = keep[stage]; high - low > 1;)
                    {
                        var middle = low + (high - low) / 2;
                        var middleSize = SizeWith(stage, middle);
                        if (middleSize <= ProjectObjectReferences.MaximumReportBytes) { low = middle; lowSize = middleSize; }
                        else high = middle;
                    }
                keep[stage] = low;
                size = lowSize;
            }
        }
        if (size > ProjectObjectReferences.MaximumReportBytes)
            throw new InvalidDataException(
                $"The report cannot fit {ProjectObjectReferences.MaximumReportBytes} UTF-8 bytes.");
        return Build(keep);

        long SizeWith(int stage, int count)
        {
            var probe = (int[])keep.Clone();
            probe[stage] = count;
            return Measure(Build(probe));
        }

        SortedDictionary<string, string> SelectMetadata(int optional)
        {
            var selected = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in metadata)
                if (CriticalMetadata.Contains(pair.Key)) selected.Add(pair.Key, pair.Value);
            for (var index = 0; index < optional; index++) selected.Add(optionalMetadata[index].Key, optionalMetadata[index].Value);
            return selected;
        }

        EventEntry[] SelectEvents(int detailedKeep, int ordinaryKeep)
        {
            if (detailedKeep == detailed && ordinaryKeep == events.Length - detailed) return events;
            var selected = new List<EventEntry>(detailedKeep + ordinaryKeep);
            var keptDetailed = 0;
            var keptOrdinary = 0;
            foreach (var entry in events)
                if (IsDetailedEvent(entry.Category))
                {
                    if (keptDetailed++ < detailedKeep) selected.Add(entry);
                }
                else if (keptOrdinary++ < ordinaryKeep) selected.Add(entry);
            return selected.ToArray();
        }

        ReportDocument Build(int[] kept) => new()
        {
            RunId = snapshot.RunId,
            StartedAtUtc = snapshot.StartedAtUtc,
            ExportedAtUtc = exportedAtUtc,
            Outcome = normalizedOutcome,
            Playability = "not_assessed",
            Metadata = SelectMetadata(kept[3]),
            Events = SelectEvents(kept[0], kept[2]),
            EventAggregates = Trimmed(aggregates, kept[1]),
            Checks = Trimmed(checks, kept[4]),
            Issues = Trimmed(issues, kept[5]),
            Overflow = new OverflowEntry
            {
                DroppedMetadata = droppedMetadata + optionalMetadata.Length - kept[3],
                DroppedEvents = droppedEvents + events.Length - kept[0] - kept[2],
                SampledDetailEvents = sampledDetailEvents,
                // A dropped aggregate or issue stands for every observation it counted.
                DroppedAggregateEvents = Sum(droppedAggregateEvents, aggregateCounts[aggregates.Length] - aggregateCounts[kept[1]]),
                DroppedChecks = droppedChecks + checks.Length - kept[4],
                DroppedIssues = Sum(droppedIssues, issueCounts[issues.Length] - issueCounts[kept[5]]),
                DroppedEventFields = droppedEventFields,
                TruncatedStrings = truncatedStrings
            },
            ObjectReferences = references
        };
    }

    private static bool IsDetailedEvent(string category) =>
        category is "generation_job" or "placement" or "prefab_spawner" or "culling_lifecycle";

    private static long[] CountPrefix(long[] counts)
    {
        var prefix = new long[counts.Length + 1];
        for (var index = 0; index < counts.Length; index++) prefix[index + 1] = Sum(prefix[index], counts[index]);
        return prefix;
    }

    private static long Sum(long total, long count) => total > long.MaxValue - count ? long.MaxValue : total + count;

    private static T[] Trimmed<T>(T[] source, int keep) => keep == source.Length ? source : source[..keep];

    // Exact UTF-8 byte count of the document, escapes and multi-byte characters included, without
    // buffering it in memory.
    private static long Measure(ReportDocument document)
    {
        using var counter = new ByteCounter();
        JsonSerializer.Serialize(counter, document, JsonOptions);
        return counter.Count;
    }

    private sealed class ByteCounter : Stream
    {
        internal long Count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
        public override void WriteByte(byte value) => Count++;
    }

    private string NormalizeRequired(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", parameterName);
        return Normalize(value, maximumLength);
    }

    private string Normalize(string? value, int maximumLength)
    {
        value ??= string.Empty;
        if (value.Length <= maximumLength) return value;
        Increment(ref _truncatedStrings);
        return value[..maximumLength];
    }

    private static string Trim(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static void Increment(ref long value)
    {
        if (value < long.MaxValue) value++;
    }

    private sealed class ReportDocument
    {
        // One current format. There is no version field and no branch that could emit another one.
        public string Format { get; } = ProjectObjectReferences.ReportFormat;
        public string RunId { get; init; } = "";
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset ExportedAtUtc { get; init; }
        public string Outcome { get; init; } = "";
        public string Playability { get; init; } = "not_assessed";
        public SortedDictionary<string, string> Metadata { get; init; } = new();
        public EventEntry[] Events { get; init; } = Array.Empty<EventEntry>();
        public AggregateEntry[] EventAggregates { get; init; } = Array.Empty<AggregateEntry>();
        public CheckEntry[] Checks { get; init; } = Array.Empty<CheckEntry>();
        public IssueEntry[] Issues { get; init; } = Array.Empty<IssueEntry>();
        public OverflowEntry Overflow { get; init; } = new();
        // Document of a ProjectReferenceSnapshot; declared as object because the document type
        // belongs to ProjectObjectReferences and must not be duplicated here. The default is the
        // same rejected epoch-0 receipt the report falls back to, never an empty fake success.
        public object ObjectReferences { get; init; } = UnattachedReferences.Snapshot().Document;
    }

    private sealed class EventEntry
    {
        public DateTimeOffset TimestampUtc { get; init; }
        public string Category { get; init; } = "";
        public string Stage { get; init; } = "";
        public string Subject { get; init; } = "";
        public double ElapsedMs { get; init; }
        public SortedDictionary<string, string> Fields { get; init; } = new();
    }

    private sealed class AggregateEntry
    {
        public string Category { get; init; } = "";
        public string Stage { get; init; } = "";
        public string Context { get; init; } = "";
        public long Count { get; set; }
        public DateTimeOffset FirstSeenUtc { get; init; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public string FirstSubject { get; init; } = "";
        public string LastSubject { get; set; } = "";
        public double TotalElapsedMs { get; set; }
        public double MaximumElapsedMs { get; set; }
        public SortedDictionary<string, string> FirstFields { get; init; } = new();
        public string LastRandomBefore { get; set; } = "";
        public string LastRandomAfter { get; set; } = "";
        public AggregateEntry Copy() => (AggregateEntry)MemberwiseClone();
    }

    private sealed class CheckEntry
    {
        public DateTimeOffset TimestampUtc { get; init; }
        public string Kind { get; init; } = "";
        public string Subject { get; init; } = "";
        public string Status { get; init; } = "";
        public string Detail { get; init; } = "";
        // Always serialized, including null: a check without a native object must not look like
        // one that has an identity nobody recorded.
        public DiagnosticNativeObject? NativeObject { get; init; }
    }

    private sealed class IssueEntry
    {
        public string Type { get; init; } = "";
        public string Severity { get; init; } = "error";
        public string Origin { get; init; } = "forge";
        public string Source { get; init; } = "";
        public string Message { get; init; } = "";
        public string RepresentativeStack { get; set; } = "";
        public long Count { get; set; }
        public DateTimeOffset FirstSeenUtc { get; init; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public DiagnosticNativeObject? NativeObject { get; init; }

        public IssueEntry Copy() => new()
        {
            Type = Type,
            Severity = Severity,
            Origin = Origin,
            Source = Source,
            Message = Message,
            RepresentativeStack = RepresentativeStack,
            Count = Count,
            FirstSeenUtc = FirstSeenUtc,
            LastSeenUtc = LastSeenUtc,
            NativeObject = NativeObject
        };
    }

    private sealed class OverflowEntry
    {
        public long DroppedMetadata { get; init; }
        public long DroppedEvents { get; init; }
        public long SampledDetailEvents { get; init; }
        public long DroppedAggregateEvents { get; init; }
        public long DroppedChecks { get; init; }
        public long DroppedIssues { get; init; }
        public long DroppedEventFields { get; init; }
        public long TruncatedStrings { get; init; }
    }
}

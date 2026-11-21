using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ForgeDevelopment.Native;

/// <summary>Conservative admission accounting for detached DTOs, not a CLR heap measurement. Includes
/// backing-array capacity, UTF-16 strings and per-object/field padding; shared references count once per
/// submission. Never invokes getters or native APIs. External reference objects are refused, so Unity
/// wrappers cannot accidentally be handed to a deferred writer. Serialization scratch is separate.</summary>
internal static class RecFrozenBytes
{
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new();
    internal static long Measure(object? root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long Visit(object? value)
        {
            if (value == null) return 0;
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is decimal or IntPtr or UIntPtr) return 16;
            if (!type.IsValueType && !seen.Add(value)) return 0;
            if (value is string text) return 32L + 2L * text.Length;
            if (value is JsonElement json)
                return json.ValueKind == JsonValueKind.Undefined ? 32 : 8192L + 16L * json.GetRawText().Length;
            if (ReferenceEquals(value, StringComparer.Ordinal) || ReferenceEquals(value, StringComparer.OrdinalIgnoreCase))
                return 32; // Only these exact standard instances are process-rooted and stateless here.
            if (value is Array array)
            {
                if (type.GetElementType()!.IsPrimitive) return 32L + System.Buffer.ByteLength(array);
                long bytes = 32L + 16L * array.LongLength;
                foreach (var item in array) bytes = checked(bytes + Visit(item));
                return bytes;
            }
            var fields = Fields.GetOrAdd(type, item =>
            {
                var hierarchy = new List<FieldInfo>();
                for (var current = item; current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
                {
                    if (!current.IsValueType && !(current.Assembly == typeof(RecFrozenBytes).Assembly
                            && current.Namespace == typeof(RecFrozenBytes).Namespace)
                        && !(current.Assembly == typeof(List<>).Assembly && current.Namespace == "System.Collections.Generic"))
                        throw new ArgumentException("Recorder requires detached managed data throughout its hierarchy: " + current.FullName);
                    hierarchy.AddRange(current.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                }
                return hierarchy.ToArray();
            });
            long size = 32L + 16L * fields.Length;
            foreach (var field in fields) size = checked(size + Visit(field.GetValue(value)));
            return size;
        }
        return checked(256 + Visit(root)); // Closure, delegate and scalar captures.
    }
}

// Captured outside hooks. Observation admission and rejected receipts reuse this managed envelope.
internal sealed record RecFrozenContext(string Session, JsonElement Header);

/// <summary>One complete record payload, or an immutable managed body frozen on its calling thread.
/// Deferred serialization must never read a game object or Unity API on the writer thread.</summary>
internal sealed class RecQueuedRecord
{
    internal RecQueuedRecord(string channel, string kind, byte[] header,
        Action<Utf8JsonWriter> frozenBody)
    {
        Channel = channel; Kind = kind; Payload = header; FrozenBody = frozenBody;
    }
    internal RecQueuedRecord(string channel, string kind, byte[] payload)
    {
        Channel = channel;
        Kind = kind;
        Payload = payload;
    }

    internal string Channel { get; }
    internal string Kind { get; }
    internal byte[] Payload { get; }
    internal Action<Utf8JsonWriter>? FrozenBody { get; }
    internal long RetainedBytes { get; set; }
    internal long FrozenBytes { get; set; }
}

/// <summary>One standalone document waiting for the writer thread. It is not a channel record: it becomes a named JSON
/// file in the session directory, so a reader can open it without parsing the JSONL stream. Payload factories
/// may use only immutable managed data; all Unity reads must have been frozen on the calling thread.</summary>
internal sealed class RecQueuedDocument
{
    internal RecQueuedDocument(string name, Func<byte[]?> frozenPayload, Action<bool>? completed)
    { Name = name; Payload = Array.Empty<byte>(); FrozenPayload = frozenPayload; Completed = completed; }
    internal RecQueuedDocument(string name, byte[] payload)
    {
        Name = name;
        Payload = payload;
    }

    internal string Name { get; }
    internal byte[] Payload { get; }
    internal Func<byte[]?>? FrozenPayload { get; }
    internal Action<bool>? Completed { get; }
    internal long RetainedBytes { get; set; }
    internal long FrozenBytes { get; set; }
}

/// <summary>
/// The recorder session: one directory per process, one JSONL record per line, written by one background thread.
/// A producer (the game thread, a Unity log callback on an engine worker, any thread that logs) composes its record
/// into its own bounded buffer and hands the finished bytes to a lock-free queue; it never takes a lock, so it can
/// never wait for, or fail on, another thread's serialization, disk write or log listener. Sequence numbers,
/// segmentation, gzip, the total budget and the index file all belong to the writer. A session that reaches its budget
/// stops writing and counts what it dropped, and says so on the `session` channel and in the screen corner rather than
/// silently truncating.
/// </summary>
internal enum RecDrainResult { Completed, Incomplete, Failed, TimedOut, WriterThread, ProducerThread }

internal static class RecSession
{
    internal const string SchemaVersion = "forge.rec";
    internal const int DefaultRecordBudget = 64 * 1024;
    internal const int DefaultSegmentBytes = 8 * 1024 * 1024;
    internal const long DefaultQueueBytes = 16L * 1024 * 1024;

    // Lifecycle only: Open, Stop and Flush serialize against each other here. Producers and the writer never take it.
    private static readonly object Gate = new();
    private static RecGate EnterGate() => IsWriterThread ? RecGate.Writer(Gate) : RecGate.Enter(Gate, "recorder.lock-timeout");
    // Records, documents and flush requests share one admission order. A barrier cannot overtake an item admitted
    // before it, and the single writer drains in that order.
    private static readonly ConcurrentQueue<object> Pending = new();
    private sealed class FlushRequest
    {
        internal readonly TaskCompletionSource<RecDrainResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal RecMark? Since;
    }
    // Admission accounting is lock-free: counts and byte reservations are claimed with interlocked operations.
    private static int _pendingItems, _pendingRecords, _pendingDocuments, _producers;
    private static long _pendingBytes, _pendingFrozenBytes, _pendingPayloadBytes, _peakPendingBytes, _peakPayloadBytes, _peakFrozenBytes, _queueRefusals;
    private static long _queueBytes = DefaultQueueBytes;
    private static readonly ConcurrentQueue<string> PersistenceErrors = new();
    private static volatile bool _recordIoFailed;
    private static volatile RecDrainResult _lastStopResult = RecDrainResult.Completed;
    private static TaskCompletionSource<RecDrainResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly ManualResetEventSlim Signal = new(false);

    private static volatile Thread? _writer;
    // Writer-owned; read by the writer's own index.
    private static Dictionary<string, long> _channels = new(StringComparer.Ordinal);
    private static List<RecSegmentInfo> _segments = new();
    private static volatile bool _running, _stop, _capped;
    // _sequence and _bytes are advanced only by the writer; _dropped by any thread. All are read with interlocked reads.
    private static long _sequence, _dropped, _bytes, _budget, _segmentBytes, _segmentIndex;
    private static int _maxQueue = 65536;
    private static volatile string _directory = "", _sessionId = "";
    // The session this one continues after a budget renewal. A capture frozen on it keeps streaming here instead of
    // being refused until it times out mid-level.
    private static volatile string? _renewedFrom;
    private static volatile string _role = "";
    private static string _slot = "unavailable", _level = "unavailable";
    private static long _lastCapNotice;
    private static Action<string> _notice = _ => { };
    private static volatile int _recordBudget = DefaultRecordBudget;
    [ThreadStatic] private static int _compositionDepth;
    // Each producing thread owns its outermost composition buffer and JSON writer.
    [ThreadStatic] private static RecJsonBuffer? _threadBuffer;
    [ThreadStatic] private static Utf8JsonWriter? _threadComposer;
    [ThreadStatic] private static int _threadBudget;
    private static readonly byte[] EnvelopePrefix = Encoding.UTF8.GetBytes("{\"v\":\"" + SchemaVersion + "\",\"seq\":");

    internal static bool IsComposing => _compositionDepth != 0;
    internal static bool IsWriterThread => Thread.CurrentThread == _writer;
    internal static bool Active => _running;
    internal static string SessionId => _sessionId;
    /// <summary>The session's recorded role. It starts as the recorder's own context value and follows the network
    /// layer's `net.session` record once that layer reports one, so the session header, the index and the records
    /// that carry a role cannot disagree with the session that was actually joined.</summary>
    internal static string Role => _role;
    internal static void AdoptRole(string role)
    {
        if (role is not ("host" or "client") || role == _role) return;
        _role = role;
        if (_running) Write("session", "role", json => json.WriteString("role", role));
    }
    /// <summary>The session this one continues after a byte-budget renewal, or null.</summary>
    internal static string? RenewedFrom => _renewedFrom;
    internal static string Directory => _directory;
    internal static long Dropped => Interlocked.Read(ref _dropped);
    // Process totals: a budget renewal opens a new session with zero counts, but loss before it is still loss.
    private static long _processDropped, _processQueueRefusals;
    internal static long DroppedInProcess => Interlocked.Read(ref _processDropped);
    internal static long QueueRefusalsInProcess => Interlocked.Read(ref _processQueueRefusals);
    // Between a full session's close and the next one's open no session admits records. A record produced then is composed
    // for the next session (its id is chosen before the close) and held, and the held work enters that session as soon as
    // it opens. Only what cannot be held (more than a queue's worth, or a failed reopen) is lost: it counts in the process
    // total and in the gap the next session records (session.renewal_gap).
    private static volatile bool _renewing;
    private static long _renewalDropped;
    private static volatile string? _nextSessionId;
    private static readonly object HoldGate = new();
    private static readonly Queue<object> Held = new();
    internal static bool Renewing => _renewing;
    private static void RefusedWhileClosed()
    {
        if (!_renewing) return; // The recorder is off, not between sessions: nothing was meant to be kept.
        Interlocked.Increment(ref _renewalDropped);
        Interlocked.Increment(ref _processDropped);
    }

    // The session a producer composes for: the open one, or while a full one is being replaced, the next one. Null when
    // nothing is open and nothing is about to open.
    private static string? AdmittingSession(out bool held)
    {
        held = !_running;
        return held ? _nextSessionId : _sessionId;
    }

    // Work composed for the next session while it is not open yet waits here; once it has opened, the work enters it directly.
    private static bool Hold(object work, string session, bool countDrop)
    {
        lock (HoldGate)
        {
            if (_nextSessionId == session && Held.Count < _maxQueue) { Held.Enqueue(work); return true; }
            if (_running && _sessionId == session) return Admit(work, countDrop);
        }
        if (countDrop) { Interlocked.Increment(ref _renewalDropped); Interlocked.Increment(ref _processDropped); }
        return false;
    }

    private static bool Admit(object work, bool countDrop)
        => work is RecQueuedRecord record ? Enqueue(record, countDrop) : PublishDocument((RecQueuedDocument)work);

    // After the reopen (or a failed one): every held item enters the new session, or counts as lost when there is none.
    private static void ReleaseHeld()
    {
        lock (HoldGate)
        {
            var next = _nextSessionId;
            _nextSessionId = null;
            while (Held.Count != 0)
            {
                var work = Held.Dequeue();
                // A refusal by the open session's own queue is counted there as a drop.
                if (_running && _sessionId == next)
                {
                    if (!Admit(work, true) && work is RecQueuedDocument refused) CompleteDocument(refused, false);
                    continue;
                }
                if (work is RecQueuedDocument document) CompleteDocument(document, false);
                Interlocked.Increment(ref _renewalDropped);
                Interlocked.Increment(ref _processDropped);
            }
        }
    }
    internal static long BytesWritten => Interlocked.Read(ref _bytes);
    internal static bool BudgetReached => _capped;
    /// <summary>True when the budget is reached or less than 1/32 of it remains after the queued records; the point at which
    /// <see cref="RenewAtBudget"/> opens the next session before any record is dropped.</summary>
    internal static bool BudgetNear => _running && (_capped
        || Interlocked.Read(ref _bytes) + Interlocked.Read(ref _pendingBytes) + _budget / 32 >= _budget);
    internal static long PendingRecords => Volatile.Read(ref _pendingRecords);
    internal static long PendingBytes => Interlocked.Read(ref _pendingBytes);
    internal static long PendingFrozenBytes => Interlocked.Read(ref _pendingFrozenBytes);
    internal static long PendingPayloadBytes => Interlocked.Read(ref _pendingPayloadBytes);
    internal static long PeakPendingBytes => Interlocked.Read(ref _peakPendingBytes);
    internal static RecDrainResult LastStopResult => _lastStopResult;
    internal static bool WriterAlive => _writer is { IsAlive: true };
    /// <summary>The largest record body this session accepts, so a writer with a large payload can split it or move
    /// it to a document before the buffer refuses it.</summary>
    internal static int RecordBudget => _recordBudget;

    internal static (string Session, long WorldEpoch) ObservationIdentity => (SessionId, RecTime.WorldEpoch);

    /// <summary>Starts the session for this process. Called once from the authoring startup path; every later call is
    /// answered with false rather than opening a second writer.</summary>
    internal static bool Start(string bepInExRoot, string pluginVersion, string forgeVersion)
    {
        var id = NewSessionId();
        var directory = System.IO.Path.Combine(bepInExRoot, "ForgeReports", "rec-" + id);
        return Open(directory, id,
            Settings.RecorderBudgetGiB.Value * 1024L * 1024 * 1024,
            Settings.RecorderSegmentMiB.Value * 1024L * 1024,
            Math.Max(1024, Settings.RecorderRecordKiB.Value * 1024),
            message => { try { Plugin.PluginLog.LogWarning(message); } catch (Exception) { } }, // Last resort: the plugin log itself failed, and there is no other sink to report to.
            new[]
            {
                ("plugin", Plugin.PluginName + " " + pluginVersion),
                ("forgeEngine", forgeVersion),
                ("runtimeMode", EngineLookup.Mode),
                ("gameVersion", RecTime.GameVersion),
                ("unityVersion", RecTime.UnityVersion),
                ("bepInEx", typeof(BepInEx.Paths).Assembly.GetName().Version?.ToString() ?? "unavailable"),
                ("host", Environment.MachineName),
                ("process", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)),
                ("startedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            },
            "session.start");
    }

    // A clean restart in the same second must never overwrite the previous session's index.
    private static string NewSessionId() => DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
        + "-" + RecTime.Role + "-" + RecTime.Slot + "-" + Guid.NewGuid().ToString("N");

    /// <summary>Closes a session that reached its byte budget and opens the next one beside it with the same limits.
    /// A full session drops every later record, so nothing started on it could persist its evidence; the producers keep
    /// running and write into the new session. The closed session's index keeps its budgetReached flag and drop count,
    /// and the new one opens with a <c>session.renewed</c> record naming it. Answers the closed session's outcome, or
    /// null when the session is not at its budget.</summary>
    internal static RecDrainResult? RenewAtBudget(TimeSpan timeout)
    {
        if (!BudgetNear) return null;
        Interlocked.Exchange(ref _renewalDropped, 0);
        _nextSessionId = NewSessionId();
        _renewing = true;
        try { return Renew(timeout); }
        finally
        {
            ReleaseHeld();
            _renewing = false;
            var gap = Interlocked.Exchange(ref _renewalDropped, 0);
            if (gap != 0) Write("session", "renewal_gap", json => json.WriteNumber("dropped", gap));
        }
    }

    /// <summary><see cref="RenewAtBudget"/> for the game thread, which never waits for the writer: the close, drain and
    /// reopen run on a pool thread and the caller collects the outcome from a later frame. Records refused in between are
    /// counted, never waited for.</summary>
    internal static Task<RecDrainResult?> RenewAtBudgetInBackground(TimeSpan timeout)
        => BudgetNear ? Task.Run(() => RenewAtBudget(timeout)) : Task.FromResult<RecDrainResult?>(null);

    private static RecDrainResult Renew(TimeSpan timeout)
    {
        var previous = _sessionId;
        var root = System.IO.Path.GetDirectoryName(_directory) ?? throw new InvalidOperationException("recorder.renew-root-missing");
        long budget = _budget, segment = _segmentBytes, queue = Interlocked.Read(ref _queueBytes);
        int record = _recordBudget; var notice = _notice;
        var closed = Stop(timeout);
        if (closed is not (RecDrainResult.Completed or RecDrainResult.Incomplete or RecDrainResult.Failed))
            throw new InvalidOperationException("recorder.renew-close-" + closed.ToString().ToLowerInvariant());
        var id = _nextSessionId ?? NewSessionId();
        // The closed session's last sequence number and drop count let a reader check the two sessions join without a hole.
        var lastSequence = Interlocked.Read(ref _sequence).ToString(CultureInfo.InvariantCulture);
        var dropped = Interlocked.Read(ref _dropped).ToString(CultureInfo.InvariantCulture);
        if (!Open(System.IO.Path.Combine(root, "rec-" + id), id, budget, segment, record, notice,
                new[] { ("previousSession", previous), ("previousOutcome", closed.ToString()), ("previousLastSequence", lastSequence),
                    ("previousDropped", dropped) }, "session.renewed", queue, previous))
            throw new InvalidOperationException("recorder.renew-open-failed");
        return closed;
    }

    /// <summary>Opens a session at an explicit directory with explicit limits. The game path passes its configured
    /// values; a test passes a temporary directory and small numbers, which is how segmentation, the budget and the
    /// drop accounting are exercised without a game.</summary>
    internal static bool Open(string directory, string sessionId, long budgetBytes, long segmentBytes,
        int recordBudget, Action<string> notice, IEnumerable<(string Key, string Value)>? startFacts, string startKind,
        long queueBytes = DefaultQueueBytes, string? renewedFrom = null)
    {
        using (EnterGate())
        {
            // A timed-out or concurrent Stop still owns the shared session state until its writer exits.
            if (_running || _writer is { IsAlive: true }) return false;
            _stop = false;
            _capped = false;
            _directory = System.IO.Path.GetFullPath(directory);
            _sessionId = sessionId;
            _renewedFrom = renewedFrom;
            _budget = Math.Max(recordBudget, budgetBytes);
            _segmentBytes = Math.Max(1, segmentBytes);
            _recordBudget = recordBudget;
            _notice = notice ?? (_ => { });
            Interlocked.Exchange(ref _sequence, 0); Interlocked.Exchange(ref _dropped, 0); Interlocked.Exchange(ref _bytes, 0);
            _segmentIndex = 0;
            _channels = new Dictionary<string, long>(StringComparer.Ordinal);
            _segments = new List<RecSegmentInfo>();
            _role = RecTime.Role;
            _slot = RecTime.Slot;
            _level = RecTime.Level;
            _maxQueue = Math.Max(64, Settings.RecorderQueue.Value);
            Interlocked.Exchange(ref _queueBytes, Math.Max(256, queueBytes));
            Interlocked.Exchange(ref _pendingBytes, 0); Interlocked.Exchange(ref _pendingFrozenBytes, 0);
            Interlocked.Exchange(ref _pendingPayloadBytes, 0); Interlocked.Exchange(ref _peakPendingBytes, 0);
            Interlocked.Exchange(ref _peakPayloadBytes, 0); Interlocked.Exchange(ref _peakFrozenBytes, 0);
            Interlocked.Exchange(ref _queueRefusals, 0);
            _lastDropCause = "";
            Pending.Clear();
            Interlocked.Exchange(ref _pendingItems, 0); Interlocked.Exchange(ref _pendingRecords, 0); Interlocked.Exchange(ref _pendingDocuments, 0);
            PersistenceErrors.Clear();
            _recordIoFailed = false;
            _lastStopResult = RecDrainResult.Completed;
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Signal.Reset();
            _running = true;
            try
            {
                // Publish and start under Gate so Stop cannot miss a writer that is about to start.
                _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Forge Development Recorder" };
                _writer.Start();
            }
            catch (Exception error)
            {
                _running = false;
                _writer = null;
                _lastStopResult = RecDrainResult.Failed;
                _completion.TrySetResult(_lastStopResult);
                PersistenceFailure("could not start in " + _directory, error);
                return false;
            }
        }
        if (startFacts != null)
        {
            Write("session", startKind, json =>
            {
                foreach (var fact in startFacts) json.WriteString(fact.Key, fact.Value);
            });
        }
        return true;
    }

    /// <summary>The one entry point every channel uses. The body runs on the calling thread and composes into that
    /// thread's bounded buffer; the finished bytes enter the lock-free queue and nothing else happens here. A record
    /// the queue cannot take is counted as dropped, never waited for.</summary>
    internal static void Write(string channel, string kind, Action<Utf8JsonWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (AdmittingSession(out _) == null) { RefusedWhileClosed(); return; }
        Interlocked.Increment(ref _producers);
        try
        {
            if (AdmittingSession(out var held) is not { } session) { RefusedWhileClosed(); return; }
            if (!held && _capped) { CountDrop(BudgetCause); return; }
            byte[] payload;
            // Logging/diagnostics during a body may reenter Write on this same thread; resetting the outer
            // composition's buffer would corrupt both records, so a nested one gets its own.
            var outermost = _compositionDepth == 0;
            var budget = _recordBudget;
            RecJsonBuffer buffer;
            if (!outermost) buffer = new RecJsonBuffer(budget);
            else
            {
                if (_threadBuffer == null || _threadBudget != budget) { _threadBuffer = new RecJsonBuffer(budget); _threadBudget = budget; _threadComposer = null; }
                buffer = _threadBuffer;
            }
            _compositionDepth++;
            try
            {
                buffer.Reset();
                Utf8JsonWriter json;
                if (outermost)
                {
                    if (_threadComposer == null) _threadComposer = new Utf8JsonWriter(buffer, ComposeOptions);
                    else _threadComposer.Reset(buffer);
                    json = _threadComposer;
                }
                else json = new Utf8JsonWriter(buffer, ComposeOptions);
                json.WriteStartObject();
                RecTime.WriteContext(json, session, channel, kind);
                json.WritePropertyName("body");
                json.WriteStartObject();
                body(json);
                json.WriteEndObject();
                json.WriteEndObject();
                json.Flush();
                payload = buffer.WrittenSpan.ToArray();
            }
            catch (Exception error)
            {
                // A body that throws, or one record that does not fit its own budget (RecValueTooLargeException), must not take the
                // game thread or the session with it. The record is lost and counted; a small error record of the same channel and
                // kind stands in its place, so a reader sees which record was lost and why.
                CountDrop("record " + channel + "/" + kind + " failed: " + error.GetType().Name);
                payload = ErrorRecord(channel, kind, error);
            }
            finally { _compositionDepth--; }
            var record = new RecQueuedRecord(channel, kind, payload);
            if (held) Hold(record, session, true); else Enqueue(record);
        }
        finally { Interlocked.Decrement(ref _producers); }
    }

    private static readonly JsonWriterOptions ComposeOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };

    internal static void Write(string channel, string kind, Action<Utf8JsonWriter> body, string sessionId)
    {
        // The session writes under its own id; the parameter exists so a caller that follows the shared interface
        // cannot accidentally open a second session by passing one.
        if (sessionId != SessionId) return;
        Write(channel, kind, body);
    }

    /// <summary>Only detached, immutable managed data may use this path. Freeze the game's context here;
    /// serialization runs on the existing disk writer, so native callers never wait for it.</summary>
    internal static RecFrozenContext? FreezeContext()
    {
        if (!_running) return null;
        var session = _sessionId;
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject(); RecTime.WriteContext(json, session, "state", "snapshot");
            json.WriteEndObject(); json.Flush();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return new RecFrozenContext(session, document.RootElement.Clone());
    }

    internal static bool WriteFrozen(string channel, string kind, Action<Utf8JsonWriter> body, long retainedBytes, RecFrozenContext? context = null)
        => WriteFrozenCore(channel, kind, body, retainedBytes, context, true);

    // The producer still owns a refused batch. Only an abandoned/timed-out batch is a drop.
    internal static bool TryWriteFrozen(string channel, string kind, Action<Utf8JsonWriter> body, long retainedBytes, RecFrozenContext? context = null)
        => WriteFrozenCore(channel, kind, body, retainedBytes, context, false);

    private static bool WriteFrozenCore(string channel, string kind, Action<Utf8JsonWriter> body, long retainedBytes,
        RecFrozenContext? context, bool countDrop)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (retainedBytes < 0) throw new ArgumentOutOfRangeException(nameof(retainedBytes));
        if (AdmittingSession(out _) == null) { if (countDrop) RefusedWhileClosed(); return false; }
        Interlocked.Increment(ref _producers);
        try
        {
            if (AdmittingSession(out var held) is not { } session) { if (countDrop) RefusedWhileClosed(); return false; }
            // A producer that keeps its batch retries on a later frame, once the next session is open.
            if (held && !countDrop) return false;
            // Held for the next session, a context still belongs to the closing one.
            if (context != null && !Continues(context, session) && !(held && context.Session == _sessionId)) return false;
            if (!held && _capped) { if (countDrop) CountDrop(BudgetCause); return false; }
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                if (context == null) RecTime.WriteContext(json, session, channel, kind);
                else
                {
                    // The frozen game context stays; only the session it is persisted in is the current one.
                    json.WriteString("session", session);
                    foreach (var field in context.Header.EnumerateObject())
                        if (field.Name is not ("v" or "seq" or "session" or "channel" or "kind")) field.WriteTo(json);
                    json.WriteString("channel", channel); json.WriteString("kind", kind);
                }
                json.WriteEndObject(); json.Flush();
            }
            var record = new RecQueuedRecord(channel, kind, stream.ToArray(), body) { FrozenBytes = retainedBytes };
            return held ? Hold(record, session, countDrop) : Enqueue(record, countDrop);
        }
        finally { Interlocked.Decrement(ref _producers); }
    }

    internal static void DropFrozen(string reason, RecFrozenContext? context = null)
    {
        if (!_running) RefusedWhileClosed();
        else if (context == null || Continues(context, _sessionId)) CountDrop("frozen record dropped: " + reason);
        Notify(reason);
    }

    // A context belongs to the current session, or to the one it renewed at the byte budget.
    private static bool Continues(RecFrozenContext context, string session)
        => context.Session == session || context.Session == _renewedFrom;

    private static byte[] SerializeFrozen(RecQueuedRecord request, long sequence, MemoryStream stream, Utf8JsonWriter json)
    {
        using var header = JsonDocument.Parse(request.Payload);
        {
            // The row-sized parts avoid throwing away an entire level at the ordinary record limit.
            // A single exceptionally large row is preserved as a named document, with an explicit reference.
            stream.SetLength(0); json.Reset(stream);
            {
                json.WriteStartObject();
                WriteContextFields(json, header.RootElement);
                json.WritePropertyName("body"); json.WriteStartObject();
                request.FrozenBody!(json);
                json.WriteEndObject(); json.WriteEndObject(); json.Flush();
            }
            if (stream.Length <= _recordBudget) return stream.ToArray();
            // The document is the whole record as a reader rebuilds it, so it stands alone beside the segments.
            var row = stream.ToArray();
            var name = "snapshot-row-" + sequence.ToString(CultureInfo.InvariantCulture) + ".forge";
            if (!WriteDocumentFile(new RecQueuedDocument(name, Complete(sequence, row))))
                throw new IOException("Could not preserve oversized snapshot row: " + name);
            using var reference = new MemoryStream();
            using (var referenceJson = new Utf8JsonWriter(reference))
            {
                referenceJson.WriteStartObject();
                WriteContextFields(referenceJson, header.RootElement);
                referenceJson.WritePropertyName("body"); referenceJson.WriteStartObject();
                referenceJson.WriteString("document", name); referenceJson.WriteString("reason", "row-exceeds-record-budget");
                referenceJson.WriteEndObject(); referenceJson.WriteEndObject(); referenceJson.Flush();
            }
            return reference.ToArray();
        }
    }

    private static void WriteContextFields(Utf8JsonWriter json, JsonElement header)
    {
        foreach (var field in header.EnumerateObject())
            if (field.Name is not ("v" or "seq")) field.WriteTo(json);
    }

    /// <summary>A record object with the schema version and sequence in front: the shape a reader rebuilds from a
    /// segment header and one line.</summary>
    private static byte[] Complete(long sequence, byte[] payload)
    {
        var digits = Encoding.ASCII.GetBytes(sequence.ToString(CultureInfo.InvariantCulture));
        var empty = payload.Length <= 2;
        var line = new byte[EnvelopePrefix.Length + digits.Length + (empty ? 1 : payload.Length)];
        EnvelopePrefix.CopyTo(line, 0);
        digits.CopyTo(line, EnvelopePrefix.Length);
        var at = EnvelopePrefix.Length + digits.Length;
        if (empty) line[at] = (byte)'}';
        else
        {
            line[at++] = (byte)',';
            payload.AsSpan(1).CopyTo(line.AsSpan(at));
        }
        return line;
    }

    /// <summary>
    /// Queues one named JSON document for the session directory. It rides the same writer thread as the channel
    /// records, so the game thread only hands over finished bytes and the writer performs the atomic file write.
    /// Whether the document is new is the caller's decision: only the caller knows which part of it is the result.
    /// Returns whether the document was queued.
    /// </summary>
    internal static bool WriteDocument(string name, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(name)) return false;
        return EnqueueDocument(new RecQueuedDocument(name, payload));
    }

    internal static bool WriteFrozenDocument(string name, Func<byte[]?> payload, long retainedBytes, Action<bool>? completed = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (retainedBytes < 0) throw new ArgumentOutOfRangeException(nameof(retainedBytes));
        if (string.IsNullOrWhiteSpace(name)) return false;
        return EnqueueDocument(new RecQueuedDocument(name, payload, completed) { FrozenBytes = retainedBytes });
    }

    // The reservation includes the in-flight item until its callback finishes; dequeueing a large frozen document
    // must not admit another budget of retained objects. Claimed with compare-exchange, never under a lock.
    // A producer that keeps its item and retries next frame (countDrop false: the budgeted snapshot cursor) may fill
    // only half the queue. Left the whole queue, a level snapshot kept it full for minutes and every record that cannot
    // wait (calls, changes, log lines) was refused and dropped. An empty queue still takes one oversized waiting item.
    private static bool Reserve(long bytes, long frozen, long payload, bool countDrop)
    {
        var waiting = !countDrop;
        if (Interlocked.Increment(ref _pendingItems) > (waiting ? _maxQueue / 2 : _maxQueue))
        {
            Interlocked.Decrement(ref _pendingItems);
            return Refuse(countDrop);
        }
        long current, total;
        var queue = Interlocked.Read(ref _queueBytes);
        do
        {
            current = Interlocked.Read(ref _pendingBytes);
            total = current + bytes;
            var limit = waiting && current != 0 ? queue / 2 : queue;
            if (bytes < 0 || bytes > limit - current)
            {
                Interlocked.Decrement(ref _pendingItems);
                return Refuse(countDrop);
            }
        } while (Interlocked.CompareExchange(ref _pendingBytes, total, current) != current);
        Peak(ref _peakPendingBytes, total);
        Peak(ref _peakFrozenBytes, Interlocked.Add(ref _pendingFrozenBytes, frozen));
        Peak(ref _peakPayloadBytes, Interlocked.Add(ref _pendingPayloadBytes, payload));
        return true;
    }

    private static bool Refuse(bool countDrop)
    {
        Interlocked.Increment(ref _queueRefusals);
        Interlocked.Increment(ref _processQueueRefusals);
        if (countDrop) CountDrop("queue full");
        return false;
    }

    private static void Peak(ref long peak, long value)
    {
        for (var seen = Interlocked.Read(ref peak); value > seen; seen = Interlocked.Read(ref peak))
            if (Interlocked.CompareExchange(ref peak, value, seen) == seen) return;
    }

    private static bool Enqueue(RecQueuedRecord record, bool countDrop = true)
    {
        record.RetainedBytes = record.Payload.LongLength + record.FrozenBytes + 192 + 2L * (record.Channel.Length + record.Kind.Length);
        if (!Reserve(record.RetainedBytes, record.FrozenBytes, record.Payload.LongLength, countDrop)) return false;
        Interlocked.Increment(ref _pendingRecords);
        Publish(record);
        return true;
    }

    private static bool EnqueueDocument(RecQueuedDocument document)
    {
        if (AdmittingSession(out _) == null) { RefusedWhileClosed(); return false; }
        Interlocked.Increment(ref _producers);
        try
        {
            if (AdmittingSession(out var held) is not { } session) { RefusedWhileClosed(); return false; }
            if (held) return Hold(document, session, true);
            if (_capped) { CountDrop(BudgetCause); return false; }
            return PublishDocument(document);
        }
        finally { Interlocked.Decrement(ref _producers); }
    }

    private static bool PublishDocument(RecQueuedDocument document)
    {
        document.RetainedBytes = document.Payload.LongLength + document.FrozenBytes + 128 + 2L * document.Name.Length;
        if (!Reserve(document.RetainedBytes, document.FrozenBytes, document.Payload.LongLength, true)) return false;
        Interlocked.Increment(ref _pendingDocuments);
        Publish(document);
        return true;
    }

    // The writer looks at the queue again before it ever sleeps, so only the item that makes the queue non-empty
    // needs to wake it (and it wakes on its own every 50 ms regardless).
    private static void Publish(object work)
    {
        var wake = Pending.IsEmpty;
        Pending.Enqueue(work);
        if (wake) Signal.Set();
    }

    private static void Release(object work)
    {
        if (work is RecQueuedRecord record) Release(record.RetainedBytes, record.FrozenBytes, record.Payload.LongLength);
        if (work is RecQueuedDocument document) Release(document.RetainedBytes, document.FrozenBytes, document.Payload.LongLength);
    }

    private static void Release(long retained, long frozen, long payload)
    {
        Interlocked.Add(ref _pendingBytes, -retained);
        Interlocked.Add(ref _pendingFrozenBytes, -frozen);
        Interlocked.Add(ref _pendingPayloadBytes, -payload);
    }

    private static void Dequeued(object work)
    {
        if (work is RecQueuedRecord) { Interlocked.Decrement(ref _pendingRecords); Interlocked.Decrement(ref _pendingItems); }
        if (work is RecQueuedDocument) { Interlocked.Decrement(ref _pendingDocuments); Interlocked.Decrement(ref _pendingItems); }
    }

    /// <summary>The player marked "it just happened". The bookmark is a record of its own so a later query can ask for
    /// records within N seconds of it.</summary>
    internal static void Bookmark(string label)
    {
        var text = string.IsNullOrWhiteSpace(label) ? "bookmark" : RecReflect.Truncate(label, 200);
        Write("mark", "bookmark", json =>
        {
            json.WriteString("label", text);
            if (RecTime.Aim() is { } row)
            {
                json.WritePropertyName("aim");
                json.WriteStartObject();
                foreach (var (name, value) in row) json.WriteString(name, value);
                json.WriteEndObject();
            }
        });
        Notify("Forge recorder bookmark: " + text);
    }

    /// <summary>Writes the screen to a PNG inside the session directory and records its relative path. A screenshot
    /// that cannot be taken is reported as a record with no path, never as a success with an empty file.</summary>
    internal static string Screenshot(string label)
    {
        var relative = "shots/" + DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture) + "-" + (++_shotIndex).ToString("D3", CultureInfo.InvariantCulture) + ".png";
        string? error = null;
        try
        {
            var written = RecTime.WriteScreenshot(_directory, relative);
            if (written.Length == 0) { error = "no screenshot was produced"; relative = ""; }
            else relative = written;
        }
        catch (Exception failure)
        {
            error = failure.GetType().Name + ": " + failure.Message;
            relative = "";
        }
        Write("shot", "screenshot", json =>
        {
            json.WriteString("reason", label);
            if (error == null) json.WriteString("path", relative);
            else json.WriteString("error", error);
        });
        return relative;
    }

    private static long _shotIndex;

    internal const string PatchesDocument = "trace-patches.forge";

    /// <summary>Records what the tracer installed: the profiles it resolved, the methods it patched and everything it
    /// refused. The report lists every patch and refusal, so it grows with the profiles and exceeds any record
    /// budget; it is kept whole as a document and the session channel only points at it.</summary>
    internal static void WritePatches(string report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var bytes = Encoding.UTF8.GetByteCount(report);
        var queued = WriteFrozenDocument(PatchesDocument, () => Encoding.UTF8.GetBytes(report), RecFrozenBytes.Measure(report));
        Write("session", "patches", json =>
        {
            json.WriteString("document", PatchesDocument);
            json.WriteNumber("bytes", bytes);
            json.WriteBoolean("queued", queued);
        });
    }


    /// <summary>Posts a barrier behind all previously accepted records/documents and their completion callbacks; the
    /// writer flushes the actual stream when it reaches it and completes the task. Later admissions do not extend this
    /// barrier. Nothing waits here: the game thread polls the task across frames. A writer callback cannot wait for
    /// itself; it receives WriterThread instead. With <paramref name="since"/> the result answers only for what was lost
    /// after that mark.</summary>
    internal static Task<RecDrainResult> RequestFlush(RecMark? since = null)
    {
        if (Thread.CurrentThread == _writer) return Task.FromResult(RecDrainResult.WriterThread);
        if (_compositionDepth != 0) return Task.FromResult(RecDrainResult.ProducerThread);
        using (EnterGate())
        {
            if (_writer == null || !_writer.IsAlive) return Task.FromResult(Outcome(since));
            // An already-stopping writer owns the final flush and close; its completion answers.
            if (!_running) return _completion.Task;
            // Stop closes admission under this same lifecycle gate, so a barrier published while running is
            // always drained by the writer before it exits.
            var request = new FlushRequest { Since = since };
            Publish(request);
            Signal.Set();
            return request.Completion.Task;
        }
    }

    /// <summary>Closes admission and returns at once; the writer drains, flushes, closes and writes the terminal index
    /// on its own thread. <see cref="LastStopResult"/> and <see cref="WriterAlive"/> report the outcome afterwards.</summary>
    internal static void RequestStop()
    {
        using (EnterGate())
        {
            if (_writer == null) return;
            _running = false;
            _stop = true;
            Signal.Set();
        }
    }

    /// <summary>Closes admission before the writer drains, flushes, closes and writes its terminal index.
    /// Timeouts keep ownership and may be retried. Completion means the writer has actually exited.</summary>
    internal static RecDrainResult Stop(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "Recorder drains require a finite timeout.");
        Thread? writer;
        Task<RecDrainResult> completion;
        using (EnterGate())
        {
            writer = _writer;
            completion = _completion.Task;
            if (writer == null) return _lastStopResult;
            // Producers re-check _running after announcing themselves; the writer exits only once every announced
            // producer has published or withdrawn, so no admitted item is stranded after the final drain.
            _running = false;
            _stop = true;
            Signal.Set();
            if (Thread.CurrentThread == writer) return _lastStopResult = RecDrainResult.WriterThread;
            if (_compositionDepth != 0) return _lastStopResult = RecDrainResult.ProducerThread;
        }
        if (writer.IsAlive && !writer.Join(timeout))
        {
            // The notice may block or reenter. Never let an old timeout overwrite a completed/new session.
            if (ReferenceEquals(_completion.Task, completion) && !completion.IsCompleted)
                _lastStopResult = RecDrainResult.TimedOut;
            Notify("Forge recorder writer did not stop within " + timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                + " s; persistence is still pending and the writer remains owned.");
            return RecDrainResult.TimedOut;
        }
        using (EnterGate())
        {
            if (ReferenceEquals(_writer, writer)) _writer = null;
            if (ReferenceEquals(_completion.Task, completion)) _lastStopResult = completion.Result;
            return completion.Result;
        }
    }

    /// <summary>The session's evidence counters at one moment. A drain judged against a mark answers only for what was lost
    /// after it, so one earlier drop does not fail every later command of the session.</summary>
    internal readonly record struct RecMark(string Session, long Dropped, int Errors);
    internal static RecMark Mark() => new(_sessionId, Interlocked.Read(ref _dropped), PersistenceErrors.Count);

    // A completed drain can still have explicit refusal/drop evidence. A full session is incomplete whatever the mark: it
    // persists nothing more, and a mark of another session has no evidence here at all.
    private static RecDrainResult Outcome(RecMark? since = null)
    {
        if (since is { } mark && mark.Session != _sessionId) return RecDrainResult.Incomplete;
        return PersistenceErrors.Count > (since?.Errors ?? 0) ? RecDrainResult.Failed
            : _capped || Interlocked.Read(ref _dropped) != (since?.Dropped ?? 0) ? RecDrainResult.Incomplete : RecDrainResult.Completed;
    }

    private static void Notify(string message)
    {
        try { _notice(message); }
        catch (Exception error)
        {
            RecDiag.Fallback(message + "; recorder notice failed: " + error);
        }
    }

    private static void PersistenceFailure(string stage, Exception error)
    {
        var detail = stage + ": " + error;
        PersistenceErrors.Enqueue(detail);
        Notify("Forge recorder " + detail);
    }

    /// <summary>Records one line on the `session` channel that a summary was produced. Used by the level-end path.</summary>
    internal static void Note(string kind, Action<Utf8JsonWriter> body) => Write("session", kind, body);

    // A producer-side record without schema version and sequence; the writer completes the envelope.
    private static byte[] ErrorRecord(string channel, string kind, Exception error)
    {
        var text = "{\"session\":\"" + JsonEscape(_sessionId) + "\",\"channel\":\"" + JsonEscape(channel) + "\",\"kind\":\"" + JsonEscape(kind)
            + "\",\"role\":\"" + JsonEscape(_role) + "\",\"slot\":\"" + JsonEscape(_slot) + "\",\"level\":\"" + JsonEscape(_level)
            + "\",\"body\":{\"$error\":\"" + JsonEscape(error.GetType().Name + ": " + RecReflect.Truncate(error.Message, 200)) + "\"}}";
        return Encoding.UTF8.GetBytes(text);
    }

    private static string JsonEscape(string text) => JsonEncodedText.Encode(text).ToString();

    private const string BudgetCause = "byte budget reached";
    private static volatile string _lastDropCause = "";

    // Every drop is counted with its cause; the log is told at most once a minute, with the running total and the latest cause, and
    // Losses() reads both back at any time. The notice runs outside any lock, and the claim on the notice slot keeps a logging notice
    // from re-announcing itself.
    private static void CountDrop(string cause)
    {
        _lastDropCause = cause;
        var dropped = Interlocked.Increment(ref _dropped);
        Interlocked.Increment(ref _processDropped);
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastCapNotice);
        if (now - last < 60000 || Interlocked.CompareExchange(ref _lastCapNotice, now, last) != last) return;
        Notify("Forge recorder dropped " + dropped.ToString(CultureInfo.InvariantCulture) + " records (latest: " + cause + "; "
            + Interlocked.Read(ref _queueRefusals).ToString(CultureInfo.InvariantCulture) + " queue refusals).");
    }

    /// <summary>The console's readback of what this session lost (<c>recorder</c>): drops, queue refusals, the byte budget and the latest
    /// cause, plus the process totals across budget renewals.</summary>
    internal static string Losses() => "session=" + _sessionId + "; dropped=" + Interlocked.Read(ref _dropped).ToString(CultureInfo.InvariantCulture)
        + "; queueRefusals=" + Interlocked.Read(ref _queueRefusals).ToString(CultureInfo.InvariantCulture)
        + "; budgetReached=" + _capped.ToString().ToLowerInvariant() + "; lastDrop=" + (_lastDropCause.Length == 0 ? "none" : _lastDropCause)
        + "; processDropped=" + DroppedInProcess.ToString(CultureInfo.InvariantCulture) + "; processQueueRefusals=" + QueueRefusalsInProcess.ToString(CultureInfo.InvariantCulture);

    private static void WriteLoop()
    {
        SegmentWriter? segment = null;
        using var frozenBuffer = new FrozenBuffer((int)Math.Min(int.MaxValue, Interlocked.Read(ref _queueBytes)));
        using var frozenJson = new Utf8JsonWriter(frozenBuffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            while (true)
            {
                if (!Pending.TryDequeue(out var work))
                {
                    // A full fence on the producer count: any producer that announced itself before this read
                    // publishes before withdrawing, and any later one sees admission closed.
                    if (_stop && Interlocked.CompareExchange(ref _producers, 0, 0) == 0 && Pending.IsEmpty) break;
                    // Without this the last records before a quiet spell (an experiment's result and cleanup rows) stay in
                    // the stream buffer until later records fill it, and the driver reading them back times out.
                    try { segment?.Publish(); }
                    catch (Exception error) { _recordIoFailed = true; PersistenceFailure("record publish failed", error); }
                    Signal.Wait(50);
                    Signal.Reset();
                    continue;
                }
                Dequeued(work);
                if (work is FlushRequest barrier)
                {
                    try { segment?.Flush(); }
                    catch (Exception error) { PersistenceFailure("flush failed", error); }
                    barrier.Completion.TrySetResult(Outcome(barrier.Since));
                    continue;
                }
                try
                {
                    if (work is RecQueuedDocument document) { WriteDocumentFile(document); continue; }
                    var record = (RecQueuedRecord)work;
                    if (_capped || _recordIoFailed) { CountDrop(_capped ? BudgetCause : "an earlier record write failed"); continue; }
                    var sequence = Interlocked.Read(ref _sequence) + 1;
                    byte[] line;
                    try
                    {
                        line = record.FrozenBody != null ? SerializeFrozen(record, sequence, frozenBuffer, frozenJson) : record.Payload;
                    }
                    catch (Exception error)
                    {
                        frozenJson.Reset(frozenBuffer); // Discard a failed buffered value before later work or Dispose.
                        PersistenceFailure("record serialization failed", error);
                        CountDrop("record " + record.Channel + "/" + record.Kind + " serialization failed");
                        // Keep later accepted work alive; no half-composed record ever reaches the stream.
                        continue;
                    }
                    // The number is spent once serialized, so an oversized row's document name is never reused.
                    Interlocked.Exchange(ref _sequence, sequence);
                    try
                    {
                        segment ??= new SegmentWriter();
                        if (!segment.Write(sequence, line, record.FrozenBody != null))
                        {
                            _capped = true; CountDrop(BudgetCause);
                            continue;
                        }
                        _channels[record.Channel] = _channels.TryGetValue(record.Channel, out var count) ? count + 1 : 1;
                    }
                    catch (Exception error)
                    {
                        // A write may have left a prefix on disk. Preserve it and stop appending; this is an
                        // I/O failure, never a budget cap or a repaired/complete JSONL claim.
                        _recordIoFailed = true;
                        CountDrop("record write failed");
                        PersistenceFailure("record write failed", error);
                    }
                }
                finally { Release(work); }
            }
        }
        catch (Exception error) { PersistenceFailure("writer failed", error); }
        finally
        {
            // Close every resource and release every waiter even if another cleanup or error sink fails.
            try { segment?.Dispose(); }
            catch (Exception error) { PersistenceFailure("segment close failed", error); }
            _running = false;
            while (Pending.TryDequeue(out var work))
            {
                Dequeued(work);
                if (work is RecQueuedRecord or RecQueuedDocument) { Interlocked.Increment(ref _dropped); Interlocked.Increment(ref _processDropped); }
                try
                {
                    if (work is RecQueuedDocument document) CompleteDocument(document, false);
                    if (work is FlushRequest barrier) barrier.Completion.TrySetResult(RecDrainResult.Failed);
                }
                finally { Release(work); }
            }
            WriteIndex();
            _lastStopResult = Outcome();
            _completion.TrySetResult(_lastStopResult);
            Signal.Reset();
        }
    }

    // Bound the existing writer's reusable JSON scratch independently of queued DTOs. A factory that
    // returns its own byte[] still owns that single serialization allocation, checked before writing.
    private sealed class FrozenBuffer : MemoryStream
    {
        private readonly int _limit;
        internal FrozenBuffer(int limit) : base(Math.Min(64 * 1024, limit)) { _limit = limit; }
        public override int Capacity { get => base.Capacity; set => base.Capacity = Math.Min(value, _limit); }
        private void Check(int count)
        {
            if (count > _limit - Position) throw new IOException("Frozen record exceeds recorder serialization budget.");
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
    }

    /// <summary>
    /// Writes one queued document beside the segments. The write is atomic — a unique temporary file is filled and
    /// then moved over the target — so a reader never sees a half-written inventory. A failure is announced.
    /// </summary>
    private static bool WriteDocumentFile(RecQueuedDocument document)
    {
        var successful = false;
        try
        {
            var root = System.IO.Path.GetFullPath(_directory) + System.IO.Path.DirectorySeparatorChar;
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(_directory, document.Name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("recorder.document-path-invalid");
            var parent = System.IO.Path.GetDirectoryName(target)!;
            for (var current = parent; current.Length >= root.Length; current = System.IO.Path.GetDirectoryName(current)!)
                if (System.IO.Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("recorder.document-link-refused");
            var payload = document.FrozenPayload == null ? document.Payload : document.FrozenPayload();
            if (payload == null) { successful = true; return true; }
            if (document.FrozenPayload != null && payload.LongLength > Interlocked.Read(ref _queueBytes))
                throw new IOException("Frozen document exceeds recorder serialization budget: " + document.Name);
            if (_capped || Interlocked.Read(ref _bytes) + payload.Length > _budget) { _capped = true; CountDrop(BudgetCause); return false; }
            ForgeCore.Store.WriteBytes(target, payload);
            Interlocked.Add(ref _bytes, payload.Length);
            successful = true; return true;
        }
        catch (Exception error)
        {
            PersistenceFailure("could not write " + document.Name, error);
            Interlocked.Increment(ref _dropped);
            Interlocked.Increment(ref _processDropped);
            return false;
        }
        finally { CompleteDocument(document, successful); }
    }

    private static void CompleteDocument(RecQueuedDocument document, bool successful)
    {
        try { document.Completed?.Invoke(successful); }
        catch (Exception error) { PersistenceFailure("document completion failed for " + document.Name, error); }
    }

    private static void WriteIndex()
    {
        try
        {
            var payload = new StringBuilder();
            payload.Append("{\"v\":\"").Append(SchemaVersion).Append("\",\"session\":\"").Append(JsonEscape(_sessionId)).Append("\"");
            payload.Append(",\"role\":\"").Append(JsonEscape(_role)).Append("\",\"slot\":\"").Append(JsonEscape(_slot)).Append("\"");
            payload.Append(",\"level\":\"").Append(JsonEscape(_level)).Append("\"");
            payload.Append(",\"budgetBytes\":").Append(_budget.ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"bytesWritten\":").Append(Interlocked.Read(ref _bytes).ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"gzip\":true"); // Every closed segment is packed; only a session still open has a plain one.
            payload.Append(",\"budgetReached\":").Append(_capped ? "true" : "false");
            payload.Append(",\"dropped\":").Append(Interlocked.Read(ref _dropped).ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"records\":").Append(Interlocked.Read(ref _sequence).ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"queueBudgetBytes\":").Append(Interlocked.Read(ref _queueBytes).ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"peakRetainedBytes\":").Append(Interlocked.Read(ref _peakPendingBytes).ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"peakPayloadBytes\":").Append(Interlocked.Read(ref _peakPayloadBytes).ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"peakFrozenBytes\":").Append(Interlocked.Read(ref _peakFrozenBytes).ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"queueRefusals\":").Append(Interlocked.Read(ref _queueRefusals).ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"processDropped\":").Append(DroppedInProcess.ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"processQueueRefusals\":").Append(QueueRefusalsInProcess.ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"persistence\":{\"outcome\":\"").Append(Outcome().ToString()).Append("\",\"errors\":[");
            var firstError = true;
            foreach (var error in PersistenceErrors)
            {
                if (!firstError) payload.Append(',');
                firstError = false;
                payload.Append('"').Append(JsonEscape(error)).Append('"');
            }
            payload.Append("]}");
            payload.Append(",\"endedUtc\":\"").Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)).Append("\"");
            payload.Append(",\"channels\":{");
            var first = true;
            foreach (var channel in _channels)
            {
                if (!first) payload.Append(',');
                first = false;
                payload.Append('"').Append(JsonEscape(channel.Key)).Append("\":").Append(channel.Value.ToString(CultureInfo.InvariantCulture));
            }
            payload.Append("},\"segments\":[");
            first = true;
            foreach (var info in _segments)
            {
                if (!first) payload.Append(',');
                first = false;
                payload.Append("{\"file\":\"").Append(JsonEscape(info.File)).Append("\",\"bytes\":").Append(info.Bytes.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"records\":").Append(info.Records.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            payload.Append("],\"patches\":");
            var trace = RecTracerRuntime.InstalledJson();
            payload.Append(trace);
            payload.Append('}');
            ForgeCore.Store.WriteText(System.IO.Path.Combine(_directory, "index.forge"), payload.ToString());
        }
        catch (Exception error)
        {
            PersistenceFailure("could not write index.forge", error);
        }
    }

    internal sealed record RecSegmentInfo(string File, long Bytes, long Records);

    /// <summary>
    /// One open segment. The writer thread owns it exclusively, so the session's counters are reached without a lock
    /// here; the file is opened lazily so a session that records nothing leaves no empty file behind.
    /// A segment starts with one header line, <c>{"v":"forge.rec","segment":{...}}</c>, holding the context fields its
    /// records share (<see cref="SegmentFields"/>). A record line leaves out every such field equal to the header's,
    /// keeps a different value as its own, and names a header field it does not have in <c>$absent</c>; a reader
    /// rebuilds each record as the header's fields (less <c>$absent</c>) overlaid with the line's, with <c>v</c> from the
    /// header. A record from the live context with a changed or new shared value (a level or world change) starts the
    /// next segment, so the header follows the session.
    /// The open segment is plain JSONL, read live by byte offset; a closed one is packed to <c>.jsonl.gz</c>.
    /// </summary>
    private sealed class SegmentWriter : IDisposable
    {
        private static readonly HashSet<string> SegmentFields = new(StringComparer.Ordinal)
            { "session", "role", "slot", "level", "generationSessionId", "generationId", "worldEpoch" };
        private static readonly byte[] SequencePrefix = Encoding.UTF8.GetBytes("{\"seq\":");
        private FileStream? _file;
        private string _name = "";
        private long _bytes, _records;
        private bool _disposed, _buffered;
        private Header? _header;
        private readonly List<Field> _fields = new();
        private readonly MemoryStream _line = new();
        // Packed segments whose plain file a reader still held open; deletion is retried at every later close.
        private readonly List<string> _packedPlain = new();

        private readonly record struct Field(string Name, int Start, int ValueStart, int End);

        private sealed class Header
        {
            internal readonly List<(string Name, byte[] Value)> Fields = new();
            internal readonly byte[] Line;

            internal Header(byte[] payload, List<Field>? fields)
            {
                using var line = new MemoryStream();
                line.Write(Encoding.UTF8.GetBytes("{\"v\":\"" + SchemaVersion + "\",\"segment\":{"));
                foreach (var field in fields ?? new List<Field>())
                {
                    if (!SegmentFields.Contains(field.Name) || Fields.Exists(item => item.Name == field.Name)) continue;
                    if (Fields.Count > 0) line.WriteByte((byte)',');
                    line.Write(payload, field.Start, field.End - field.Start);
                    Fields.Add((field.Name, payload.AsSpan(field.ValueStart, field.End - field.ValueStart).ToArray()));
                }
                line.Write(Encoding.UTF8.GetBytes("}}\n"));
                Line = line.ToArray();
            }

            internal byte[]? Value(string name)
            {
                foreach (var field in Fields) if (field.Name == name) return field.Value;
                return null;
            }

            // A record that only lacks some shared fields (an error record, one written before the game context was
            // installed) stays in this segment and names them in $absent; a changed or new shared value does not.
            internal bool Covers(byte[] payload, List<Field> fields)
            {
                foreach (var field in fields)
                {
                    if (!SegmentFields.Contains(field.Name)) continue;
                    var value = Value(field.Name);
                    if (value == null || !payload.AsSpan(field.ValueStart, field.End - field.ValueStart).SequenceEqual(value)) return false;
                }
                return true;
            }
        }

        // The context fields in front of "body", as byte ranges of the producer's object. False for anything that is
        // not a JSON object, which is then written whole.
        private static bool Split(byte[] payload, List<Field> fields, out int body)
        {
            fields.Clear();
            body = -1;
            try
            {
                var reader = new Utf8JsonReader(payload);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) return true;
                    if (reader.TokenType != JsonTokenType.PropertyName) return false;
                    var start = (int)reader.TokenStartIndex;
                    var name = reader.GetString()!;
                    if (name == "body") { body = start; return true; }
                    if (!reader.Read()) return false;
                    var valueStart = (int)reader.TokenStartIndex;
                    reader.Skip();
                    fields.Add(new Field(name, start, valueStart, (int)reader.BytesConsumed));
                }
                return false;
            }
            catch (JsonException) { return false; }
        }

        private byte[] Compose(long sequence, byte[] payload, bool parsed, int body, Header header)
        {
            _line.SetLength(0);
            _line.Write(SequencePrefix);
            _line.Write(Encoding.ASCII.GetBytes(sequence.ToString(CultureInfo.InvariantCulture)));
            if (!parsed)
            {
                if (payload.Length > 2) { _line.WriteByte((byte)','); _line.Write(payload, 1, payload.Length - 1); }
                else _line.WriteByte((byte)'}');
                _line.WriteByte((byte)'\n');
                return _line.ToArray();
            }
            foreach (var field in _fields)
            {
                if (SegmentFields.Contains(field.Name) && header.Value(field.Name) is { } shared
                    && payload.AsSpan(field.ValueStart, field.End - field.ValueStart).SequenceEqual(shared)) continue;
                _line.WriteByte((byte)',');
                _line.Write(payload, field.Start, field.End - field.Start);
            }
            var absent = header.Fields.FindAll(item => !_fields.Exists(field => field.Name == item.Name));
            if (absent.Count > 0)
                _line.Write(Encoding.UTF8.GetBytes(",\"$absent\":[\"" + string.Join("\",\"", absent.ConvertAll(item => item.Name)) + "\"]"));
            if (body >= 0) { _line.WriteByte((byte)','); _line.Write(payload, body, payload.Length - body); }
            else _line.WriteByte((byte)'}');
            _line.WriteByte((byte)'\n');
            return _line.ToArray();
        }

        internal bool Write(long sequence, byte[] payload, bool frozen)
        {
            if (_capped) return false;
            var parsed = Split(payload, _fields, out var body);
            // A frozen capture keeps the context it was taken in; it never moves the live segment on.
            var rotate = _file != null && _records > 0 && parsed && !frozen && !_header!.Covers(payload, _fields);
            var header = _file == null || rotate ? new Header(payload, parsed ? _fields : null) : _header!;
            var line = Compose(sequence, payload, parsed, body, header);
            if (!rotate && _file != null && _records > 0 && _bytes + line.Length > _segmentBytes)
            {
                rotate = true;
                header = new Header(payload, parsed ? _fields : null);
                line = Compose(sequence, payload, parsed, body, header);
            }
            var opening = _file == null || rotate;
            // The budget is checked before the bytes reach the disk, so the session stops exactly at its cap
            // instead of writing over it and reporting afterwards.
            if (Interlocked.Read(ref RecSession._bytes) + line.Length + (opening ? header.Line.Length : 0) > _budget) return false;
            if (rotate) { Close(); _segmentIndex++; }
            if (opening) Open(header);
            try
            {
                _file!.Write(line, 0, line.Length);
                _buffered = true;
                _bytes += line.Length;
                _records++;
                Interlocked.Add(ref RecSession._bytes, line.Length);
                return true;
            }
            catch (Exception error) { throw new IOException("Write failed in " + _name, error); }
        }

        internal void Flush() { _buffered = false; _file?.Flush(flushToDisk: true); }

        /// <summary>Hands the stream buffer to the OS once the queue is drained, so a reader sees every accepted record
        /// without waiting for later records to fill the buffer; no disk sync.</summary>
        internal void Publish()
        {
            if (!_buffered) return;
            _buffered = false;
            _file?.Flush(flushToDisk: false);
        }

        private void Open(Header header)
        {
            try
            {
                var name = "rec-" + _sessionId + "-" + _segmentIndex.ToString("D3", CultureInfo.InvariantCulture) + ".jsonl";
                _file = new FileStream(System.IO.Path.Combine(_directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _name = name;
                _bytes = 0;
                _records = 0;
                _segments.Add(new RecSegmentInfo(name, 0, 0));
                _file.Write(header.Line, 0, header.Line.Length);
                _header = header;
                _bytes = header.Line.Length;
                Interlocked.Add(ref RecSession._bytes, header.Line.Length);
            }
            catch (Exception error) { throw new IOException("Could not open a segment in " + _directory, error); }
        }

        private void Close()
        {
            var file = _file;
            // Release ownership before cleanup, including on failure or a repeated Dispose.
            _file = null;
            if (file == null) return;
            var closed = false;
            try
            {
                try { file.Flush(flushToDisk: true); }
                catch (Exception error) { CloseFailure("flush", error); }
                try { _bytes = file.Length; }
                catch (Exception error) { CloseFailure("measure", error); }
            }
            finally
            {
                try { file.Dispose(); closed = true; }
                catch (Exception error) { CloseFailure("dispose", error); }
                finally { UpdateInfo(_name); }
            }
            if (closed) Pack();
        }

        // Packs the closed segment beside itself and then removes the plain file. The packed file appears whole (it is
        // renamed into place), so a reader takes the plain file while it exists and the packed one after. Failing to
        // pack loses nothing: the plain segment stays and the index keeps naming it.
        private void Pack()
        {
            var plain = System.IO.Path.Combine(_directory, _name);
            var packed = plain + ".gz";
            var temporary = packed + ".tmp";
            try
            {
                using (var source = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var gzip = new GZipStream(target, CompressionLevel.Optimal, leaveOpen: true)) source.CopyTo(gzip);
                    target.Flush(flushToDisk: true);
                }
                File.Move(temporary, packed);
                var previous = _name;
                _name += ".gz";
                _bytes = new FileInfo(packed).Length;
                UpdateInfo(previous);
                _packedPlain.Add(plain);
            }
            catch (Exception error)
            {
                // File.Delete fails only with these two; a temporary left behind is named in the same notice.
                var stray = "";
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException cleanup) { stray = "; temporary left: " + cleanup.Message; }
                catch (UnauthorizedAccessException cleanup) { stray = "; temporary left: " + cleanup.Message; }
                Notify("Forge recorder kept " + _name + " unpacked: " + error.Message + stray);
            }
            RemovePackedPlain();
        }

        private void RemovePackedPlain()
        {
            _packedPlain.RemoveAll(path =>
            {
                try { File.Delete(path); return true; }
                catch (IOException) { return false; } // A reader still has it open; its packed copy is complete.
                catch (UnauthorizedAccessException) { return false; }
            });
        }

        private void CloseFailure(string stage, Exception error) =>
            PersistenceFailure("could not close " + _name + " (" + stage + ")", error);

        private void UpdateInfo(string name)
        {
            for (var i = 0; i < _segments.Count; i++)
                if (_segments[i].File == name) _segments[i] = new RecSegmentInfo(_name, _bytes, _records);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            if (_packedPlain.Count > 0)
                Notify("Forge recorder could not remove " + _packedPlain.Count.ToString(CultureInfo.InvariantCulture)
                    + " plain segment(s) a reader held open; their packed copies are complete.");
            _line.Dispose();
        }
    }
}

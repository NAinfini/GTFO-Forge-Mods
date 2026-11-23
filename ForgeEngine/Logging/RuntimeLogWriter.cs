using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using BepInEx.Logging;
using ForgeEngine.Framework;

namespace ForgeEngine.Logging;

// Acceptance is distinct from confirmed file delivery. The public sink ABI remains void.
internal enum RuntimeLogWriteResult { Accepted, Dropped, Failed, Stopped }

/// <summary>Constructor inputs only so tests can inject small values; the host always passes <see cref="Default"/>.</summary>
internal sealed record RuntimeLogLimits(int PlayerLinesPerTick, int PlayerQueue, int ElevatedLinesPerTick, int ElevatedQueue,
    long MaximumFileBytes, long RetainedBytes)
{
    // Patrol21's ten-level session delivered 60,494,332 bytes and lost 62,066 records (about 103 MB in full).
    // 256 MiB leaves about 188 MB for trace as well as the larger info/protected tails, in the same session file.
    // The folder keeps 1 GiB in all: the new session's full cap plus the newest earlier sessions that fit beside it.
    internal static readonly RuntimeLogLimits Default = new(256, 8192, 4096, 65536, 256L * 1024 * 1024, 1024L * 1024 * 1024);
}

/// <summary>forge.log JSONL writer. BepInEx logging never happens on the background thread: <see cref="Write"/> and
/// <see cref="Dispose"/> run on the kernel thread and mirror the console, while the one background thread created by the
/// first record only opens the file, serializes, writes and prunes. File truncation is announced on the next producer
/// call; <see cref="Dispose"/> reports the final loss once the writer has drained its queue and closed its file.</summary>
internal sealed partial class RuntimeLogWriter : IRuntimeLogSink, IRuntimeLogSummarySink, IRuntimePlanLoadLogSink, IDisposable
{
    internal const string Schema = "forge.log";
    internal const string DirectoryName = "forge-logs";
    // Stop runs from Unity quit/destroy on the main thread: a longer wait visibly hangs shutdown, while a working disk
    // drains even a full elevated queue far sooner. The thread is a background thread, so a stuck disk cannot keep the process alive.
    internal static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    // Keeps room for the final log.dropped line under the file cap.
    private const int DroppedLineReserve = 768;
    // Share of the file (after the reserve) a level may fill: trace stops at 70%, info at 90%, errors use all of it.
    private const int TraceBudgetPercent = 70, InfoBudgetPercent = 90;

    private readonly string directory;
    private readonly ManualLogSource console;
    private readonly RuntimeLogLimits limits;
    private BlockingCollection<Entry>? queue;
    private Thread? thread;
    private volatile string? filePath;
    private RuntimeLogLevels? announced;
    private RuntimeLogTier? announcedTier;
    private readonly string runtimeProvider;
    private long seq, tick = long.MinValue, epoch = long.MinValue, dropped, droppedTick, droppedEpoch;
    // What the writer thread could not write: the records a file failure cost and the reason. It publishes both so the
    // kernel thread can report them, and reads them nowhere itself. BepInEx listeners must not run on the writer thread —
    // a listener that touches IL2CPP objects there takes the process down — so the final report happens in Dispose, after
    // the join that both publishes the fields and ends the run whose loss they describe.
    private long unwritten;
    // Records the file budget refused, by level; published by the writer thread's run and read after the join in Dispose.
    private long budgetLostTrace, budgetLostInfo, budgetLostError;
    // The consumer publishes file loss immediately; the next producer call reports it while a test is still active.
    // Only that producer may invoke BepInEx listeners. The footer retains the exact final count, without duplication.
    private long fileBudgetLost;
    private bool fileBudgetNotice;
    // Records the rate and queue limits refused, by level; counted with `dropped` and reported with it.
    private long droppedTrace, droppedInfo, droppedError;
    private volatile string? unwrittenReason;
    private int linesThisTick, summaryLinesThisWindow;
    private long summaryWindow = long.MinValue;
    private bool disposed;
    private readonly object queueGate = new();
    // Provider facts accepted but not yet covered by a successful Flush; control reports never enter this count.
    private long outstanding;
    private bool consumerFailed;
    // Measure the existing queue and synchronous listeners without changing per-event console framing.
    private bool planLoadBatch;
    private long planRecords, planMirrorCalls, planQueueTicks, planMirrorTicks;

    void IRuntimePlanLoadLogSink.BeginPlanLoad()
    {
        if (planLoadBatch) throw new InvalidOperationException("Plan log batch is already active.");
        planLoadBatch = true;
        planRecords = planMirrorCalls = planQueueTicks = planMirrorTicks = 0;
    }
    RuntimePlanLoadLogMetrics? IRuntimePlanLoadLogSink.EndPlanLoad()
    {
        planLoadBatch = false;
        return new RuntimePlanLoadLogMetrics(planRecords, planMirrorCalls, planQueueTicks, planMirrorTicks);
    }

    internal RuntimeLogWriter(string directory, string runtimeProvider, ManualLogSource console, RuntimeLogLimits limits)
    {
        this.directory = Path.GetFullPath(directory);
        this.runtimeProvider = runtimeProvider;
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    internal bool Started => thread != null;
    /// <summary>Set by the background thread once the session file exists; read it after Dispose.</summary>
    internal string? FilePath { get => filePath; private set => filePath = value; }

    void IRuntimeLogSink.Write(in RuntimeLogRecord record, RuntimeLogLevels levels) => Write(in record, levels);
    public RuntimeLogWriteResult Write(in RuntimeLogRecord record, RuntimeLogLevels levels) => Write(in record, levels, null);

    // Counted refusal summaries need a bounded wall-clock allowance when simulation ticks have stopped. This
    // internal route neither resets nor spends the ordinary tick allowance and still obeys queue/file caps.
    void IRuntimeLogSummarySink.WriteSummary(in RuntimeLogRecord record, RuntimeLogLevels levels, long monotonicWindow)
        => Write(in record, levels, monotonicWindow);

    private RuntimeLogWriteResult Write(in RuntimeLogRecord record, RuntimeLogLevels levels, long? countedSummaryWindow)
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.RuntimeLogWrite);
        ReportFileBudgetLoss(record.Tick, record.WorldEpoch);
        var planRecord = planLoadBatch && record.Code is RuntimeLogCodes.PlanLoaded or RuntimeLogCodes.PlanRejected;
        var queueStarted = planRecord ? Stopwatch.GetTimestamp() : 0;
        Entry? levelReport = null, droppedReport = null;
        Entry entry;
        RuntimeLogWriteResult result;
        lock (queueGate)
        {
            if (disposed) return RuntimeLogWriteResult.Stopped;
            if (queue == null) Start();
            if (consumerFailed)
            {
                unwritten++;
                entry = new Entry(++seq, record, null, 0);
                result = RuntimeLogWriteResult.Failed;
            }
            else
            {
                var elevated = levels.Tier == RuntimeLogTier.Elevated;
                if (countedSummaryWindow is { } window)
                {
                    if (window > summaryWindow) { summaryWindow = window; summaryLinesThisWindow = 0; }
                }
                else if (record.Tick != tick || record.WorldEpoch != epoch)
                { tick = record.Tick; epoch = record.WorldEpoch; linesThisTick = 0; }
                var used = countedSummaryWindow.HasValue ? summaryLinesThisWindow : linesThisTick;
                // Load work runs outside the simulation (tick < 0) as one long accounting tick: registration, plan and placement records
                // are bounded by the content and would otherwise fill the per-tick budget before the level even starts.
                if (used >= (elevated || record.Tick < 0 ? limits.ElevatedLinesPerTick : limits.PlayerLinesPerTick)
                    || queue!.Count >= (elevated ? limits.ElevatedQueue : limits.PlayerQueue))
                {
                    dropped++; droppedTick = record.Tick; droppedEpoch = record.WorldEpoch;
                    switch (LevelIndex(record.Level)) { case 0: droppedTrace++; break; case 1: droppedInfo++; break; default: droppedError++; break; }
                    return RuntimeLogWriteResult.Dropped;
                }
                if (countedSummaryWindow.HasValue) summaryLinesThisWindow++;
                else linesThisTick++;
                // Only the initial table and later elevation are announced; registration adds no extra line.
                if (announcedTier != levels.Tier)
                {
                    announcedTier = levels.Tier;
                    announced = levels;
                    var level = new Entry(++seq, new RuntimeLogRecord
                    {
                        Level = RuntimeLogLevel.Info,
                        Code = RuntimeLogCodes.LogLevel,
                        Provider = levels.RuntimeProvider,
                        Tick = record.Tick,
                        WorldEpoch = record.WorldEpoch
                    }, levels, 0);
                    queue.Add(level); levelReport = level;
                }
                if (dropped != 0)
                {
                    var report = DroppedEntry(dropped, droppedTick, droppedEpoch, null, droppedTrace, droppedInfo, droppedError);
                    dropped = droppedTrace = droppedInfo = droppedError = 0; queue.Add(report); droppedReport = report;
                }
                entry = new Entry(++seq, record, null, 0);
                // Queue the complete admitted group before any synchronous listener can retire the host.
                outstanding++;
                queue.Add(entry);
                result = RuntimeLogWriteResult.Accepted;
            }
        }
        if (planRecord)
        {
            if (result == RuntimeLogWriteResult.Accepted) planRecords++;
            planQueueTicks += Stopwatch.GetTimestamp() - queueStarted;
        }
        // BepInEx listeners may synchronously stop the host. They must never run under the consumer lock.
        if (levelReport is { } levelLine) Mirror(levelLine);
        if (droppedReport is { } droppedLine) Mirror(droppedLine);
        Mirror(entry);
        return result;
    }

    private void ReportFileBudgetLoss(long atTick, long atEpoch)
    {
        if (disposed || fileBudgetNotice) return;
        var count = Interlocked.Read(ref fileBudgetLost);
        if (count == 0) return;
        fileBudgetNotice = true;
        // Console-only early signal: the current result captures this classified diagnostic before it is sealed.
        // Adding it to JSONL would count the same losses twice when the final footer is read.
        Mirror(DroppedEntry(count, atTick, atEpoch, "File budget exhausted; final lost count follows at session close.", 0, 0, 0));
    }

    public void Dispose()
    {
        Entry? droppedReport = null;
        lock (queueGate)
        {
            if (disposed) return;
            disposed = true;
            if (queue == null) return;
            if (dropped != 0)
            {
                var entry = DroppedEntry(dropped, droppedTick, droppedEpoch, null, droppedTrace, droppedInfo, droppedError);
                dropped = droppedTrace = droppedInfo = droppedError = 0;
                if (!consumerFailed) queue.Add(entry);
                droppedReport = entry;
            }
            queue.CompleteAdding();
        }
        if (droppedReport is { } droppedLine) Mirror(droppedLine);
        bool finished = thread!.Join(StopTimeout);
        if (!finished) FailConsumer("shutdown timed out; in-flight and buffered records are unconfirmed");
        // The session's file loss is reported here, exactly once: the join above both ends the run that counts it and
        // publishes the count, and this is the kernel thread, the only place BepInEx may be called from. The queue is
        // closed by now, so this report reaches the console and no file.
        long lost;
        lock (queueGate) { lost = unwritten; unwritten = 0; }
        if (lost != 0 || unwrittenReason != null)
        {
            var entry = DroppedEntry(lost, tick, epoch, "Records were not confirmed written to " + (filePath ?? directory) + ": "
                + (unwrittenReason ?? "the session file reached its size limit"), budgetLostTrace, budgetLostInfo, budgetLostError);
            Mirror(entry);
        }
        if (!finished && global::ForgeCore.Log.Developer)
            console.LogWarning("Forge log writer did not finish within " + StopTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                + " s; unconfirmed records were counted and the pending queue was drained.");
    }

    private void Start()
    {
        // Admission reserves two control lines and Dispose can append one final loss report.
        // Add never waits for a consumer that may itself be waiting on queueGate.
        queue = new BlockingCollection<Entry>(new ConcurrentQueue<Entry>(), limits.ElevatedQueue + 3);
        thread = new Thread(Run) { IsBackground = true, Name = "Forge log writer" };
        thread.Start();
    }

    private void ConfirmWrites(long count)
    {
        lock (queueGate) { if (!consumerFailed) outstanding -= count; }
    }

    private void FailConsumer(string reason)
    {
        lock (queueGate)
        {
            if (consumerFailed) return;
            consumerFailed = true;
            unwrittenReason = reason;
            SettleOutstanding();
            queue!.CompleteAdding();
            while (queue.TryTake(out _)) { }
        }
    }

    // Called only under queueGate: failure, file cap and timeout all settle the same unconfirmed facts once.
    private void SettleOutstanding()
    {
        unwritten += outstanding;
        outstanding = 0;
    }

    /// <summary>One `log.dropped` report: how many records a provider logged and the writer did not carry, with the reason
    /// when a file failure caused it. A report is never one of the records it accounts for.</summary>
    private Entry DroppedEntry(long count, long atTick, long atEpoch, string? detail, long trace, long info, long error) => new(++seq,
        // A session whose very first record was already dropped has no announced table yet, so the Runtime provider is
        // carried from construction rather than from the level line. `values` splits the count by the dropped records' level.
        new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Error,
            Code = RuntimeLogCodes.LogDropped,
            Provider = announced?.RuntimeProvider ?? runtimeProvider,
            Tick = atTick,
            WorldEpoch = atEpoch,
            Values = LevelCounts(trace, info, error)
        },
        null, count, detail, isReport: true);

    // Runs on the kernel thread only: BepInEx dispatches synchronously to every ILogListener in the calling thread.
    // Trace has no console mirror, so leave its message formatting to the background writer.
    private void Mirror(in Entry entry)
    {
        // A plan batch's per-file `plan.loaded` receipts go to forge.log only; the batch's one `plan.load` summary
        // (files, loaded, rejected, timings) is the console line. A rejection is an Error and stays on the console.
        if (planLoadBatch && entry.Record.Code == RuntimeLogCodes.PlanLoaded) return;
        var measure = planLoadBatch && entry.Record.Code is RuntimeLogCodes.PlanLoaded or RuntimeLogCodes.PlanRejected;
        var at = measure ? Stopwatch.GetTimestamp() : 0;
        var level = entry.Record.Level;
        // forge.log is the complete stream. Repeating ordinary Info receipts in BepInEx
        // also feeds Development's log listener and formats them on the game thread.
        // Keep every Error synchronous, including loss/failure/stall signals; the startup
        // summaries and level table remain visible without opening the structured stream.
        // A failed file keeps the progress mirror too, rather than leaving it with no output. Info reaches the console only with developer logs.
        if (level == RuntimeLogLevel.Error) console.LogError(Message(entry));
        else if (level == RuntimeLogLevel.Info && global::ForgeCore.Log.Developer && (consumerFailed || entry.Levels != null || entry.Record.Code is "plan.load" or "content.startup"))
            console.LogInfo(Message(entry));
        if (measure && level is RuntimeLogLevel.Error or RuntimeLogLevel.Info)
        { planMirrorCalls++; planMirrorTicks += Stopwatch.GetTimestamp() - at; }
    }

    // Runs on the background thread: no BepInEx call, no Unity call, no console mirror. It counts what the file did not
    // receive and records why, for the one report Dispose writes on the kernel thread once this run has ended.
    private void Run()
    {
        FileStream? file = null;
        long lost = 0, buffered = 0;
        try
        {
            var buffer = new ArrayBufferWriter<byte>(1024);
            using var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            file = Open();
            if (file == null) { FailConsumer(unwrittenReason ?? "the session file could not be opened"); return; }
            // One file, one budget per level (not a rolling set of volumes): the website reader, retention and FilePath all
            // assume a session is one file, and the evidence that matters in a session that outgrew its cap is the
            // errors, which a single reserved tail protects without any of that changing. Trace stops first, then info;
            // errors, failed/refused info records and the writer's own lines keep the whole room.
            long written = 0, room = limits.MaximumFileBytes - DroppedLineReserve;
            Span<long> budget = stackalloc long[3] { room * TraceBudgetPercent / 100, room * InfoBudgetPercent / 100, room };
            Span<bool> full = stackalloc bool[3];
            Span<long> lostByLevel = stackalloc long[3];
            Entry last = default;
            foreach (var entry in queue!.GetConsumingEnumerable())
            {
                last = entry;
                var tier = Tier(in entry);
                if (full[tier])
                {
                    if (IsRecord(entry)) { lost++; lostByLevel[LevelIndex(entry.Record.Level)]++; Interlocked.Increment(ref fileBudgetLost); }
                    continue;
                }
                Serialize(json, buffer, entry, Message(entry));
                if (written + buffer.WrittenCount > budget[tier])
                {
                    full[tier] = true;
                    unwrittenReason ??= "the session file reached its size limit";
                    if (IsRecord(entry)) { lost++; lostByLevel[LevelIndex(entry.Record.Level)]++; Interlocked.Increment(ref fileBudgetLost); }
                    continue;
                }
                Append(file, buffer, ref written);
                if (IsRecord(entry)) buffered++;
                if (queue.Count == 0) { file.Flush(); ConfirmWrites(buffered); buffered = 0; }
            }
            budgetLostTrace = lostByLevel[0]; budgetLostInfo = lostByLevel[1]; budgetLostError = lostByLevel[2];
            // The run closes the file with one footer holding every record it never received, so a capped session cannot
            // be mistaken for a complete one. The footer is no record of its own: if it cannot be written either, the
            // reason the report carries is the write failure that stopped it.
            if (lost != 0)
            {
                var footer = new Entry(last.Seq + 1, new RuntimeLogRecord
                {
                    Level = RuntimeLogLevel.Error,
                    Code = RuntimeLogCodes.LogDropped,
                    Provider = last.Record.Provider,
                    Tick = last.Record.Tick,
                    WorldEpoch = last.Record.WorldEpoch,
                    Values = LevelCounts(lostByLevel[0], lostByLevel[1], lostByLevel[2])
                }, null, lost, null, isReport: true);
                Serialize(json, buffer, footer, Message(footer));
                Append(file, buffer, ref written);
            }
            file.Flush(); ConfirmWrites(buffered);
        }
        catch (Exception error)
        {
            FailConsumer(error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            try { file?.Dispose(); }
            catch (Exception error) { FailConsumer(error.GetType().Name + ": " + error.Message); }
            lock (queueGate) { if (!consumerFailed) SettleOutstanding(); }
        }
    }

    private FileStream? Open()
    {
        FileStream? file = null;
        try
        {
            PrepareFile(ref file);
            if (file != null) { FilePath = file.Name; return file; }
            Directory.CreateDirectory(directory);
            var name = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-"
                + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant() + ".jsonl";
            var path = Path.Combine(directory, name);
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            FilePath = path;
            Retain(path);
            return file;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            file?.Dispose();
            // Reason only: the records this costs are counted by the run and reported with it at Dispose.
            unwrittenReason = "no file could be created in " + directory + ": " + error.Message;
            return null;
        }
    }

    // Source-linked fault injection only; the declaration and call compile away in the shipped host.
    partial void PrepareFile(ref FileStream? file);

    // Earlier sessions are kept newest first while they fit, with the new session counted at its full cap, in the
    // folder's byte budget; the first one that does not fit and every older one are deleted.
    private void Retain(string current)
    {
        var kept = limits.MaximumFileBytes;
        var stale = new DirectoryInfo(directory).GetFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(f => string.Equals(f.Extension, ".jsonl", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(f.FullName, current, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .SkipWhile(f => (kept += f.Length) <= limits.RetainedBytes).ToArray();
        int failures = 0; string? first = null;
        foreach (var file in stale)
        {
            try { file.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { failures++; first ??= file.Name + ": " + error.Message; }
        }
        // Only when a deletion actually failed, and only until the loss of a record gives the report a better reason:
        // retention runs on the writer thread every session, while a disk refuses rarely.
        if (failures != 0) unwrittenReason ??= "retention could not delete " + failures.ToString(CultureInfo.InvariantCulture)
            + " of " + stale.Length.ToString(CultureInfo.InvariantCulture) + " old session files (" + first + ")";
    }

    private static int LevelIndex(RuntimeLogLevel level) => level switch
    { RuntimeLogLevel.Trace => 0, RuntimeLogLevel.Info => 1, _ => 2 };

    /// <summary>Which budget an entry draws on: 0 trace, 1 info, 2 protected. The protected tier is every error, the
    /// writer's own lines (`log.level`, `log.dropped`) and every info record whose result is rejected, failed, cancelled or
    /// expired, so a refused or failed step is never the evidence a full file loses.</summary>
    private static int Tier(in Entry entry)
    {
        if (entry.Levels != null || entry.IsReport) return 2;
        var record = entry.Record;
        // Partial is not a failure: a presentation step's host receipt is partial by design and stays an ordinary info record.
        if (record.Level == RuntimeLogLevel.Info && record.Result is
            {
                Status: CommandStatuses.Rejected or CommandStatuses.Failed
            or CommandStatuses.Cancelled or CommandStatuses.Expired
            }) return 2;
        return LevelIndex(record.Level);
    }

    /// <summary>Per-level counts of dropped records as typed values; a level that lost nothing is not written.</summary>
    private static RuntimeLogValue[]? LevelCounts(long trace, long info, long error)
    {
        var values = new List<RuntimeLogValue>(3);
        if (trace != 0) values.Add(RuntimeLogValue.Of("trace", trace));
        if (info != 0) values.Add(RuntimeLogValue.Of("info", info));
        if (error != 0) values.Add(RuntimeLogValue.Of("error", error));
        return values.Count == 0 ? null : values.ToArray();
    }

    /// <summary>A record a provider logged, as opposed to the `log.level` table or a `log.dropped` report: the writer's own
    /// lines are how a session describes itself, so nothing counts them as records a provider lost.</summary>
    private static bool IsRecord(in Entry entry) => entry.Levels == null && !entry.IsReport;

    // Success returns normally; any failed write reaches the one terminal failure/drain path in Run.
    private static void Append(FileStream file, ArrayBufferWriter<byte> buffer, ref long written)
    {
        file.Write(buffer.WrittenSpan); written += buffer.WrittenCount;
    }

    private static void Serialize(Utf8JsonWriter json, ArrayBufferWriter<byte> buffer, in Entry entry, string message)
    {
        buffer.Clear(); json.Reset();
        var r = entry.Record;
        json.WriteStartObject();
        json.WriteString("schema", Schema);
        json.WriteString("origin", "game");
        json.WriteNumber("seq", entry.Seq);
        json.WriteNumber("tick", r.Tick);
        json.WriteNumber("worldEpoch", r.WorldEpoch);
        if (r.Frame is long frame) json.WriteNumber("frame", frame);
        json.WriteString("level", Name(r.Level));
        json.WriteString("code", r.Code);
        json.WriteString("provider", r.Provider);
        Optional(json, "subjectProvider", r.SubjectProvider);
        Optional(json, "commandId", r.CommandId);
        Optional(json, "eventId", r.EventId);
        Optional(json, "causeId", r.CauseId);
        Optional(json, "rootEventId", r.RootEventId);
        if (r.Plan is RuntimeLogPlan plan)
        {
            json.WriteStartObject("plan");
            json.WriteString("planId", plan.PlanId);
            json.WriteStartObject("resource");
            json.WriteString("id", plan.ResourceId);
            json.WriteString("revision", plan.ResourceRevision);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        Optional(json, "path", r.Path);
        if (r.Permissions is { } permissions)
        {
            json.WriteStartArray("permissions");
            foreach (var permission in permissions) json.WriteStringValue(permission);
            json.WriteEndArray();
        }
        Optional(json, "entry", r.Entry);
        Optional(json, "step", r.Step);
        Optional(json, "nodeKind", r.NodeKind);
        Optional(json, "binding", r.Binding);
        Optional(json, "entity", r.Entity);
        if (r.Values is { } values)
        {
            json.WriteStartObject("values");
            foreach (var value in values) WriteValue(json, in value);
            json.WriteEndObject();
        }
        if (r.GateAccumulated is long gateAccumulated) json.WriteNumber("gateAccumulated", gateAccumulated);
        if (r.GateFired is long gateFired) json.WriteNumber("gateFired", gateFired);
        if (r.Result is RuntimeLogResult result)
        {
            json.WriteStartObject("result");
            json.WriteString("status", result.Status);
            Optional(json, "commit", result.Commit);
            json.WriteString("reason", result.Reason);
            json.WriteEndObject();
        }
        if (r.Layout is RuntimeLogLayout layout) WriteLayout(json, in layout);
        if (r.FixedDeltaTime is double fixedDeltaTime) json.WriteNumber("fixedDeltaTime", fixedDeltaTime);
        if (r.TicksPerSecond is double ticksPerSecond) json.WriteNumber("ticksPerSecond", ticksPerSecond);
        if (r.MeasuredTicksPerSecond is double measured) json.WriteNumber("measuredTicksPerSecond", measured);
        if (r.ExpectedTicksPerSecond is double expected) json.WriteNumber("expectedTicksPerSecond", expected);
        if (r.Code == RuntimeLogCodes.LogDropped) json.WriteNumber("count", entry.Count);
        if (entry.Levels is { } levels)
        {
            json.WriteStartArray("levels");
            foreach (var provider in levels.Providers)
            {
                json.WriteStartObject();
                json.WriteString("provider", provider.Provider);
                json.WriteString("level", Name(provider.Level));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteBoolean("elevated", levels.Tier == RuntimeLogTier.Elevated);
        }
        json.WriteString("message", FileMessage(message));
        json.WriteEndObject();
        json.Flush();
        buffer.Write("\n"u8);
    }

    private static void Optional(Utf8JsonWriter json, string name, string? value)
    { if (value != null) json.WriteString(name, value); }

    /// <summary>One member of a domain record's `values` object, written as the JSON scalar its kind names.</summary>
    private static void WriteValue(Utf8JsonWriter json, in RuntimeLogValue value)
    {
        switch (value.Kind)
        {
            case RuntimeLogValueKind.Text: json.WriteString(value.Name, value.Text); break;
            case RuntimeLogValueKind.Integer: json.WriteNumber(value.Name, value.Integer); break;
            case RuntimeLogValueKind.Number: json.WriteNumber(value.Name, value.Number); break;
            case RuntimeLogValueKind.Flag: json.WriteBoolean(value.Name, value.Integer != 0); break;
        }
    }

    private static string Text(in RuntimeLogValue value) => value.Kind switch
    {
        RuntimeLogValueKind.Text => value.Text ?? "",
        RuntimeLogValueKind.Integer => value.Integer.ToString(CultureInfo.InvariantCulture),
        RuntimeLogValueKind.Number => value.Number.ToString("R", CultureInfo.InvariantCulture),
        _ => value.Integer != 0 ? "true" : "false"
    };

    /// <summary>The layout object of `map.layout-generated`. Every array is written even when empty: the website
    /// reader requires the four keys, and an attempt that built nothing says so with `complete:false` rather than
    /// with a missing field.</summary>
    private static void WriteLayout(Utf8JsonWriter json, in RuntimeLogLayout layout)
    {
        json.WriteStartObject("layout");
        json.WriteBoolean("complete", layout.Complete);
        if (layout.ElevatorLandedTick is long landed) json.WriteNumber("elevatorLandedTick", landed);
        else json.WriteNull("elevatorLandedTick");
        json.WriteStartArray("zones");
        foreach (var zone in layout.Zones ?? Array.Empty<RuntimeLogLayoutZone>())
        {
            json.WriteStartObject();
            json.WriteString("zone", zone.Zone);
            json.WriteNumber("dimension", zone.Dimension);
            json.WriteNumber("layer", zone.Layer);
            json.WriteNumber("localIndex", zone.LocalIndex);
            json.WriteNumber("floor", zone.Floor);
            json.WriteStartArray("tiles");
            foreach (var tile in zone.Tiles ?? Array.Empty<string>()) json.WriteStringValue(tile);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("connections");
        foreach (var connection in layout.Connections ?? Array.Empty<RuntimeLogLayoutConnection>())
        {
            json.WriteStartObject();
            json.WriteString("from", connection.From);
            json.WriteString("to", connection.To);
            json.WriteString("direction", connection.Direction);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static string Message(in Entry entry)
    {
        var r = entry.Record;
        if (r.Code == RuntimeLogCodes.LogLevel)
            return "Forge log levels: " + string.Join(", ", entry.Levels!.Providers.Select(p => p.Provider + "=" + Name(p.Level)))
                + (entry.Levels.Tier == RuntimeLogTier.Elevated ? " (elevated)" : "");
        if (r.Code == RuntimeLogCodes.LogDropped)
            return "Forge log.dropped provider=" + r.Provider + " log truncated: dropped "
                + entry.Count.ToString(CultureInfo.InvariantCulture) + " records."
                + (entry.Detail == null ? "" : " " + entry.Detail)
                + (r.Values is { } byLevel ? " by level:" + string.Concat(byLevel.Select(v => " " + v.Name + "=" + Text(in v))) : "");
        var text = new StringBuilder("Forge ").Append(r.Code).Append(" provider=").Append(r.Provider);
        Field(text, "subject", r.SubjectProvider);
        if (r.Plan is RuntimeLogPlan plan) text.Append(" plan=").Append(plan.PlanId).Append(" resource=").Append(plan.ResourceId).Append('@').Append(plan.ResourceRevision);
        Field(text, "path", r.Path);
        Field(text, "detail", r.Detail);
        Field(text, "entry", r.Entry);
        Field(text, "step", r.Step);
        Field(text, "binding", r.Binding);
        Field(text, "event", r.EventId);
        Field(text, "command", r.CommandId);
        Field(text, "entity", r.Entity);
        if (r.Values is { } values)
            foreach (var value in values) Field(text, value.Name, Text(in value));
        if (r.FixedDeltaTime is double fixedDeltaTime) Field(text, "fixedDeltaTime", fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture));
        if (r.TicksPerSecond is double ticksPerSecond) Field(text, "ticksPerSecond", ticksPerSecond.ToString("R", CultureInfo.InvariantCulture));
        if (r.MeasuredTicksPerSecond is double measured) Field(text, "measuredTicksPerSecond", measured.ToString("R", CultureInfo.InvariantCulture));
        if (r.ExpectedTicksPerSecond is double expected) Field(text, "expectedTicksPerSecond", expected.ToString("R", CultureInfo.InvariantCulture));
        if (r.Result is RuntimeLogResult result)
        {
            text.Append(" result=").Append(result.Status);
            if (result.Commit != null) text.Append('/').Append(result.Commit);
            text.Append(':').Append(result.Reason);
        }
        if (r.Layout is RuntimeLogLayout layout)
            text.Append(" layout=").Append(layout.Complete ? "complete" : "started")
                .Append(" zones=").Append((layout.Zones?.Count ?? 0).ToString(CultureInfo.InvariantCulture))
                .Append(" connections=").Append((layout.Connections?.Count ?? 0).ToString(CultureInfo.InvariantCulture))
                .Append(" elevator=").Append(layout.ElevatorLandedTick?.ToString(CultureInfo.InvariantCulture) ?? "not-observed");
        return text.ToString();
    }

    /// <summary>The file's `message` is for people and never parsed, and a reader holds every text field to one printable
    /// line of at most 256 characters: control characters become spaces and a longer line ends in an ellipsis. The console
    /// mirror keeps the full text.</summary>
    internal static string FileMessage(string message)
    {
        var text = new StringBuilder(Math.Min(message.Length, MessageLimit));
        foreach (var character in message) text.Append(character < ' ' || character == '\u007f' ? ' ' : character);
        var line = text.ToString().Trim();
        if (line.Length == 0) return "Forge";
        return line.Length <= MessageLimit ? line : line.Substring(0, MessageLimit - 1).TrimEnd() + "…";
    }
    private const int MessageLimit = 256;

    private static void Field(StringBuilder text, string name, string? value)
    { if (value != null) text.Append(' ').Append(name).Append('=').Append(value); }

    private static string Name(RuntimeLogLevel level) => level switch
    {
        RuntimeLogLevel.Off => "off",
        RuntimeLogLevel.Error => "error",
        RuntimeLogLevel.Info => "info",
        RuntimeLogLevel.Trace => "trace",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown log level.")
    };

    private readonly struct Entry
    {
        internal Entry(long seq, in RuntimeLogRecord record, RuntimeLogLevels? levels, long count, string? detail = null, bool isReport = false)
        { Seq = seq; Record = record; Levels = levels; Count = count; Detail = detail; IsReport = isReport; }
        internal long Seq { get; }
        internal RuntimeLogRecord Record { get; }
        internal RuntimeLogLevels? Levels { get; }
        internal long Count { get; }
        /// <summary>Extra human-readable text for log.dropped when the cause was the file, not rate limiting.</summary>
        internal string? Detail { get; }
        /// <summary>True for a `log.dropped` report about records the writer did not carry, false for a logged record. A
        /// report accounts for lost records without being one of them, so it never counts towards its own total, neither in
        /// the console report nor in the footer that closes the file.</summary>
        internal bool IsReport { get; }
    }
}

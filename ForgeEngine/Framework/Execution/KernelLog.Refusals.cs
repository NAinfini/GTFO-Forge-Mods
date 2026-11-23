using System;
using System.Collections.Generic;

namespace ForgeEngine.Framework;

internal sealed partial class KernelLog
{
    // One shared refusal counter, independent of domains and simulation time. The first full record reaches the
    // ordinary sink; repeats are represented only by these counted summaries, never by a second domain warning.
    internal const long DiagnosticSummaryWindowMs = 5_000;
    internal const int MaximumDiagnosticSummaryKeys = 256;
    // Where a refusal came from, as typed parts. A record point fills only the parts it has and never joins them into
    // one text: equality over the parts is the counter identity, and a summary carries each part as its own typed value,
    // for ForgeDevelopment to format when it reads or shows the record.
    internal readonly record struct RefusalSite(string? Cause = null, string? StepPlan = null, string? StepEntry = null, string? Step = null,
        bool StepFailed = false, string? Reason = null, string? Stage = null, string? Current = null, string? Port = null,
        long Generation = -1, int Index = -1, int ValueKind = -1, string? Targets = null)
    {
        internal bool Oversize => Cause is { Length: > MaxLogText } || StepPlan is { Length: > MaxLogText } || StepEntry is { Length: > MaxLogText }
            || Step is { Length: > MaxLogText } || Stage is { Length: > MaxLogText } || Current is { Length: > MaxLogText } || Port is { Length: > MaxLogText }
            || Targets is { Length: > MaxLogText };

        internal RuntimeLogValue[] SummaryValues(long repeats, long total, long windowMs)
        {
            var values = new List<RuntimeLogValue>(13) { RuntimeLogValue.Of("repeatCount", repeats), RuntimeLogValue.Of("totalCount", total),
                RuntimeLogValue.Of("windowMs", windowMs) };
            if (Cause != null) values.Add(RuntimeLogValue.Of("cause", Cause));
            if (StepPlan != null) values.Add(RuntimeLogValue.Of("stepPlan", StepPlan));
            if (StepEntry != null) values.Add(RuntimeLogValue.Of("stepEntry", StepEntry));
            if (Step != null) values.Add(RuntimeLogValue.Of("step", Step));
            if (StepFailed) values.Add(RuntimeLogValue.Of("failed", true));
            if (Port != null) values.Add(RuntimeLogValue.Of("port", Port));
            if (Stage != null) values.Add(RuntimeLogValue.Of("stage", Stage));
            if (Current != null) values.Add(RuntimeLogValue.Of("current", Current));
            if (Generation >= 0) values.Add(RuntimeLogValue.Of("subscription", Generation));
            if (Index >= 0) values.Add(RuntimeLogValue.Of("attachmentIndex", (long)Index));
            if (ValueKind >= 0) values.Add(RuntimeLogValue.Of("valueKind", (long)ValueKind));
            // A step's per-recipient outcomes are part of the counter's identity, so one summary never counts two different outcomes.
            if (Targets != null) values.Add(RuntimeLogValue.Of("targets", Targets));
            return values.ToArray();
        }
    }
    private readonly record struct RefusalKey(string Code, string Reason, string Binding, string Provider, string? SubjectProvider, RefusalSite Site);
    private sealed class RefusalWindow
    {
        internal RefusalWindow(RefusalKey key, in RuntimeLogRecord record, long now, bool terminal = false, bool overflow = false)
        {
            Key = key;
            // Do not retain arbitrary event/payload text. The first line already contains the full event identity;
            // a summary describes a binding/reason window, not another individual event.
            Record = new RuntimeLogRecord
            {
                Level = record.Level,
                Code = record.Code,
                Provider = record.Provider,
                SubjectProvider = record.SubjectProvider,
                Binding = record.Binding,
                Result = record.Result,
                Tick = record.Tick,
                WorldEpoch = record.WorldEpoch,
                Plan = terminal ? record.Plan : null,
                Entry = terminal ? record.Entry : null,
                Values = terminal || record.Code == RuntimeLogCodes.PresentationHandlerException ? record.Values : null
            };
            Started = now;
            Terminal = terminal; Overflow = overflow;
        }
        internal readonly RefusalKey Key;
        internal readonly RuntimeLogRecord Record;
        internal readonly bool Terminal, Overflow;
        internal long Started, Repeats, Total = 1;
    }
    // Resolved candidate/port/value-kind slots address counters directly. Normal filtering never competes with
    // error windows, evicts a first sample, or formats another payload on a repeat. The final slot accounts for
    // every overflow candidate explicitly; it is not a claim that those candidates have the sample's identity.
    internal struct BoundedRefusalSlot { internal long Generation; internal int Index; }
    private List<RefusalWindow>? boundedRefusals;
    private long boundedRefusalGeneration = 1;
    internal int BoundedDiagnosticSummaryKeyCount => boundedRefusals?.Count ?? 0;

    internal bool CountBoundedRefusal(ref BoundedRefusalSlot slot)
    {
        if (slot.Generation != boundedRefusalGeneration)
        {
            if (boundedRefusals?.Count != MaximumDiagnosticSummaryKeys) return false;
            slot = new BoundedRefusalSlot { Generation = boundedRefusalGeneration, Index = MaximumDiagnosticSummaryKeys - 1 };
        }
        var window = boundedRefusals![slot.Index];
        // Split only at JSON's exact integer ceiling, never at a time/capacity eviction boundary.
        if (window.Total == MaxSafeInteger) { FlushRefusalWindow(window, DiagnosticMilliseconds); window.Total = 0; }
        window.Total++; window.Repeats++;
        return true;
    }

    internal void WriteBoundedRefusal(ref BoundedRefusalSlot slot, in RuntimeLogRecord record, RuntimeLogValue[] summaryFacts)
    {
        boundedRefusals ??= new List<RefusalWindow>(MaximumDiagnosticSummaryKeys);
        slot = new BoundedRefusalSlot { Generation = boundedRefusalGeneration, Index = boundedRefusals.Count };
        var overflow = slot.Index == MaximumDiagnosticSummaryKeys - 1;
        var summary = record with { Values = summaryFacts };
        boundedRefusals.Add(new RefusalWindow(default, in summary, DiagnosticMilliseconds, terminal: true, overflow: overflow));
        var sample = overflow ? record with { Detail = "Overflow sample; subsequent unsampled candidates share this counter, not this candidate identity." } : record;
        WriteLog(in sample);
    }
    private Dictionary<RefusalKey, RefusalWindow>? refusalWindows;
    private Queue<RefusalWindow>? pendingDiagnosticFlush;
    private Func<long>? diagnosticClock;
    private long nextDiagnosticPoll;
    private long DiagnosticMilliseconds => diagnosticClock?.Invoke() ?? Environment.TickCount64;
    internal int DiagnosticSummaryKeyCount => refusalWindows?.Count ?? 0;

    // Deterministic host-harness clock; production uses the monotonic process clock, never a game tick.
    internal void SetDiagnosticClock(Func<long> monotonicMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(monotonicMilliseconds);
        RuntimeJson.Require(DiagnosticSummaryKeyCount == 0 && BoundedDiagnosticSummaryKeyCount == 0,
            "log-record", "Set the diagnostic clock before recording refusals.");
        diagnosticClock = monotonicMilliseconds;
    }

    internal void WriteRepeatedRefusal(in RuntimeLogRecord record, in RefusalSite site = default)
    {
        // Invalid keys still get their ordinary complete diagnostic. Never truncate two different invalid keys
        // into one counter, and never hold an unbounded string behind the bounded key-count claim.
        // Native diagnostic envelopes carry no command result; their record point supplies the reason explicitly.
        var reason = site.Reason ?? record.Result!.Value.Reason;
        if (record.Binding is not { Length: > 0 and <= MaxLogText } binding
            || record.Code.Length > MaxLogText || reason.Length > MaxLogText || record.Provider.Length > MaxLogText
            || record.SubjectProvider is { Length: > MaxLogText } || site.Oversize)
        { WriteLog(in record); return; }
        var now = DiagnosticMilliseconds;
        refusalWindows ??= new Dictionary<RefusalKey, RefusalWindow>();
        var windows = refusalWindows;
        var key = new RefusalKey(record.Code, reason, binding, record.Provider, record.SubjectProvider, site);
        if (refusalWindows.TryGetValue(key, out var window))
        {
            if (window.Total < MaxSafeInteger)
            {
                window.Total++; window.Repeats++;
                if (now - window.Started >= DiagnosticSummaryWindowMs) FlushRefusalWindow(window, now);
                return;
            }
            // Exact JSON integers have a ceiling. End this window explicitly before starting another one.
            refusalWindows.Remove(key);
            FlushRefusalWindow(window, now);
            if (SessionEnded) return;
            if (!ReferenceEquals(refusalWindows, windows)) { WriteLog(in record); return; }
        }
        if (refusalWindows.Count == MaximumDiagnosticSummaryKeys)
        {
            RefusalWindow? oldest = null;
            foreach (var candidate in refusalWindows.Values)
                if (oldest == null || candidate.Started < oldest.Started) oldest = candidate;
            refusalWindows.Remove(oldest!.Key);
            FlushRefusalWindow(oldest, now);
        }
        // A summary sink may synchronously finish the session or advance the world. Never recreate an old
        // window after that boundary; a completed session has already retired its writer.
        if (SessionEnded) return;
        if (!ReferenceEquals(refusalWindows, windows)) { WriteLog(in record); return; }
        refusalWindows!.Add(key, new RefusalWindow(key, in record, now));
        if (refusalWindows.Count == 1) nextDiagnosticPoll = now + DiagnosticSummaryWindowMs;
        WriteLog(in record);
    }

    internal void PollDiagnosticSummaries()
    {
        if (refusalWindows == null || refusalWindows.Count == 0) return;
        var now = DiagnosticMilliseconds;
        if (now < nextDiagnosticPoll) return;
        nextDiagnosticPoll = now + DiagnosticSummaryWindowMs;
        // Snapshot only on a due poll: sinks can synchronously report another refusal without invalidating this walk.
        foreach (var window in new List<RefusalWindow>(refusalWindows.Values))
            if (now - window.Started >= DiagnosticSummaryWindowMs) FlushRefusalWindow(window, now);
    }

    internal void FlushDiagnosticSummaries()
    {
        if (boundedRefusals is { Count: > 0 } bounded)
        {
            boundedRefusals = null;
            boundedRefusalGeneration++;
            pendingDiagnosticFlush ??= new Queue<RefusalWindow>(bounded.Count);
            foreach (var window in bounded) pendingDiagnosticFlush.Enqueue(window);
        }
        if (refusalWindows is { Count: > 0 } retiring)
        {
            refusalWindows = null;
            pendingDiagnosticFlush ??= new Queue<RefusalWindow>(retiring.Count);
            foreach (var window in retiring.Values) pendingDiagnosticFlush.Enqueue(window);
        }
        var now = DiagnosticMilliseconds;
        // Keep remaining windows visible to a synchronous Stop/world callback. A nested terminal flush drains
        // them before the caller retires its sink; the outer walk then has nothing left to emit into that sink.
        while (pendingDiagnosticFlush is { Count: > 0 } pending)
            FlushRefusalWindow(pending.Dequeue(), now);
        pendingDiagnosticFlush = null;
    }

    private void FlushRefusalWindow(RefusalWindow window, long now)
    {
        if (window.Repeats == 0) return;
        var count = window.Repeats;
        var elapsed = Math.Clamp(now - window.Started, 0, MaxSafeInteger);
        window.Repeats = 0; window.Started = now;
        var record = window.Record with
        {
            Values = window.Terminal
                ? new[] { RuntimeLogValue.Of("repeatCount", count), RuntimeLogValue.Of("totalCount", window.Total),
                    RuntimeLogValue.Of("windowMs", elapsed), RuntimeLogValue.Of("overflow", window.Overflow),
                    window.Record.Values![0], window.Record.Values[1], window.Record.Values[2] }
                : window.Record.Code == RuntimeLogCodes.PresentationHandlerException
                ? new[] { window.Record.Values![0], window.Record.Values[1], window.Record.Values[2], window.Record.Values[3],
                    RuntimeLogValue.Of("repeatCount", count),
                    RuntimeLogValue.Of("totalCount", window.Total), RuntimeLogValue.Of("windowMs", elapsed) }
                : window.Key.Site.SummaryValues(count, window.Total, elapsed),
            Detail = window.Overflow ? "Overflow summary; counts include unsampled candidates, not only the sample identity."
                : "Repeated refusal summary; repeatCount excludes the first full record."
        };
        if (logSink is IRuntimeLogSummarySink summaries)
        {
            var wasNotifying = NotifyingSink;
            NotifyingSink = true;
            try { summaries.WriteSummary(in record, logLevels!, now / DiagnosticSummaryWindowMs); }
            finally { NotifyingSink = wasNotifying; }
        }
        else WriteLog(in record);
    }
}

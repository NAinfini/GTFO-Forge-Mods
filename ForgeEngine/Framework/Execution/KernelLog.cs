using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

// Startup transport measurements. Every file passes WriteLog, the bounded JSONL queue and its own console event.
internal interface IRuntimePlanLoadLogSink
{
    void BeginPlanLoad();
    RuntimePlanLoadLogMetrics? EndPlanLoad();
}
internal readonly record struct RuntimePlanLoadLogMetrics(long Records, long MirrorCalls, long QueueTicks, long MirrorTicks);

// Only the kernel's counted summaries use this route; Values never grant access to a separate allowance.
internal interface IRuntimeLogSummarySink
{
    void WriteSummary(in RuntimeLogRecord record, RuntimeLogLevels levels, long monotonicWindow);
}

/// <summary>The kernel's log: the sink, every provider's level gate, the record points and the repeated-refusal
/// windows. It reads the clock it was constructed with and nothing above it; the facade checks the thread before a
/// call reaches here.</summary>
internal sealed partial class KernelLog
{
    private readonly KernelWorld world;
    private readonly string runtimeId;
    private IRuntimeLogSink? logSink;
    private readonly Dictionary<string, RuntimeLogGate> logGates = new(StringComparer.Ordinal);
    private RuntimeLogLevel runtimeLogLevel;
    private RuntimeLogLevels? logLevels;
    /// <summary>Whether a sink callback is running: the facade refuses a lifecycle mutation made from inside one.</summary>
    internal bool NotifyingSink;
    private long CurrentTick => world.Tick;
    private long WorldEpoch => world.WorldEpoch;

    internal KernelLog(KernelWorld world, string runtimeId) { this.world = world; this.runtimeId = runtimeId; }

    /// <summary>The host constructor's half. The sink is fixed for this kernel's lifetime. <paramref name="level"/> is the host cfg
    /// level of the Runtime's own provider (<see cref="RuntimeIdentity.Id"/>); a domain provider has no level until its package
    /// hands one over with <see cref="RuntimeKernel.RegisterModule"/>, so looking one up earlier is a contract violation
    /// rather than a default.</summary>
    internal void Attach(IRuntimeLogSink sink, RuntimeLogLevel level)
    {
        logSink = sink;
        runtimeLogLevel = level;
        logGates.Add(runtimeId, new RuntimeLogGate(level));
        PublishLogLevels(RuntimeLogTier.Player);
    }

    internal bool HasSink => logSink != null;
    internal IRuntimeLogSink? Sink => logSink;
    /// <summary>The level the Runtime's own built-in providers register with.</summary>
    internal RuntimeLogLevel RuntimeLevel => runtimeLogLevel;
    internal bool HasGate(string providerId) => logGates.ContainsKey(providerId);
    /// <summary>Republishes the level table after a registration added its gate; a kernel without a sink keeps none.</summary>
    internal void RepublishLogLevels() { if (logSink != null) PublishLogLevels(logLevels!.Tier); }

    /// <summary>Adds the entry a registering provider's level needs, without republishing the table: registration publishes
    /// the new table and then emits its <c>binding.registered</c> records against it, so every record carries a table that
    /// already lists its own provider and one registration never leaves two tables behind. A provider registered after
    /// elevation is Trace like every other one, never its cfg value; a kernel without a sink keeps no table at all. The
    /// Runtime's own entry belongs to the host constructor, so a module claiming that provider id can neither replace nor
    /// drop it.</summary>
    internal void RegisterLogGate(string providerId, RuntimeLogLevel level)
    {
        if (logSink == null || providerId == runtimeId) return;
        logGates[providerId] = new RuntimeLogGate(logLevels!.Tier == RuntimeLogTier.Elevated ? RuntimeLogLevel.Trace : level);
    }

    /// <summary>Drops the entry of an unregistered provider and republishes the table. The Runtime's own entry belongs to the
    /// host constructor, not to a module, so a module claiming that provider id can never take Runtime logging down with it.</summary>
    internal void UnregisterLogGate(string providerId)
    {
        if (logSink == null || providerId == runtimeId) return;
        // A handle that cached this gate keeps the object, so the gate is closed rather than only forgotten: the
        // provider's record point stops writing the moment it is no longer a registered provider.
        if (logGates.Remove(providerId, out var removed)) removed.Level = RuntimeLogLevel.Off;
        PublishLogLevels(logLevels!.Tier);
    }

    /// <summary>The gate a module handle caches for its own record point. A kernel without a sink, or a handle whose
    /// registration is already gone, gets the closed gate: such a record point never writes.</summary>
    internal RuntimeLogGate ModuleLogGate(string providerId, bool registered)
    {
        if (logSink == null || !registered) return closedLogGate;
        return logGates.TryGetValue(providerId, out var gate) ? gate : closedLogGate;
    }

    /// <summary>The same provider cell captured by a loaded step. Plan admission already proved the provider is
    /// registered; a kernel without a sink captures the permanently closed cell.</summary>
    internal RuntimeLogGate PlanLogGate(string providerId)
        => logSink == null ? closedLogGate : logGates[providerId];

    /// <summary>A gate no level ever opens. It is never in <see cref="logGates"/>, so elevation cannot reach it.</summary>
    private readonly RuntimeLogGate closedLogGate = new(RuntimeLogLevel.Off);

    /// <summary>The Runtime provider's own gate, cached so the host's record points (<see cref="RuntimeLogs"/>) read one
    /// field; the closed gate for a kernel without a sink. The constructor's entry is never replaced, so the cache
    /// stays the object elevation raises.</summary>
    private RuntimeLogGate RuntimeGate => runtimeGate ??= logSink == null ? closedLogGate : logGates[runtimeId];
    private RuntimeLogGate? runtimeGate;

    /// <summary>Whether the host's own record point (<see cref="LogRuntime"/>) writes at <paramref name="level"/>. Host
    /// assembly only: a domain package writes through its own <see cref="RuntimeModuleHandle.Log"/>.</summary>
    internal bool RuntimeLogs(RuntimeLogLevel level) => RuntimeGate.Writes(level);

    /// <summary>The host's structured record point: the same path as a module's, attributed to the Runtime provider.
    /// The host's game binding and network layer use it for the facts only they can observe (the execution gate,
    /// checkpoints, the handshake and the world epoch).</summary>
    internal void LogRuntime(RuntimeLogLevel level, string code, in RuntimeLogFacts facts)
    {
        if (!RuntimeLogs(level)) return;
        WriteDomainLog(runtimeId, level, code, in facts);
    }

    /// <summary>A record point's line: its code and facts checked, then stamped with the provider and the kernel's clock.
    /// The kernel's own event codes are refused here, because a package that could write `trigger.fired` or
    /// `event.rejected` could forge the kernel's account of what ran.</summary>
    internal void WriteDomainLog(string provider, RuntimeLogLevel level, string code, in RuntimeLogFacts facts)
    {
        RuntimeJson.Require(DomainCode(code), "log-record",
            "A domain log code is a dotted lower-case name of at least two segments and never a kernel event code.");
        RuntimeJson.Require(OptionalText(facts.Binding) && OptionalText(facts.EventId) && OptionalText(facts.Entity),
            "log-record", "A domain log record's binding, eventId and entity are non-empty when present.");
        // The log never carries an account identity (contract §3.2): a Steam64-shaped number in any text a package
        // hands over is replaced by `account-shaped`, whichever field it arrived in. The record is still written: the
        // diagnostic it carries matters more than the one field that cannot be shown.
        var values = facts.Values == null ? null : SnapshotLogValues(facts.Values);
        var record = new RuntimeLogRecord
        {
            Level = level,
            Code = code,
            Provider = provider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            Binding = facts.Binding,
            EventId = Redact(facts.EventId),
            Entity = Redact(facts.Entity),
            Values = values,
            Detail = Redact(facts.Detail)
        };
        // A domain opts into the shared refusal window with an explicit rejected status, reason and binding.
        // An optional cause separates different failures that use the same public result code. Ordinary errors
        // keep their existing write path; no status is inferred from their level or human-readable message.
        if (level == RuntimeLogLevel.Error && facts.Binding != null && values != null)
        {
            string? status = null, reason = null, cause = null;
            foreach (var value in values)
            {
                if (value.Kind != RuntimeLogValueKind.Text) continue;
                if (value.Name == "status") status = value.Text;
                else if (value.Name == "reason") reason = value.Text;
                else if (value.Name == "cause") cause = value.Text;
            }
            if (status == CommandStatuses.Rejected && !string.IsNullOrEmpty(reason))
            {
                record = record with { Result = new RuntimeLogResult { Status = status, Commit = CommitStates.None, Reason = reason } };
                WriteRepeatedRefusal(in record, new RefusalSite(Cause: cause));
                return;
            }
        }
        WriteLog(in record);
    }

    internal const int MaxLogText = 256;
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    private static readonly HashSet<string> KernelLogCodes = typeof(RuntimeLogCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);

    private static bool OptionalText(string? value) => value == null || (value.Length != 0 && value.Length <= MaxLogText);

    private static bool DomainCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 96 || KernelLogCodes.Contains(code)) return false;
        var segments = 1;
        var segmentStart = true;
        foreach (var character in code)
        {
            if (character == '.')
            {
                if (segmentStart) return false;
                segments++;
                segmentStart = true;
                continue;
            }
            var letterOrDigit = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (segmentStart ? !letterOrDigit : !(letterOrDigit || character is '-' or '_')) return false;
            segmentStart = false;
        }
        return !segmentStart && segments >= 2;
    }

    internal static IReadOnlyList<RuntimeLogValue> SnapshotLogValues(IReadOnlyList<RuntimeLogValue> source)
    {
        RuntimeLogValue[] values;
        try
        {
            var count = source.Count;
            values = new RuntimeLogValue[count];
            for (var index = 0; index < count; index++)
            {
                var value = source[index];
                values[index] = value.Kind == RuntimeLogValueKind.Text && AccountShaped(value.Text) ? RuntimeLogValue.Of(value.Name, "account-shaped") : value;
            }
            RuntimeJson.Require(source.Count == count, "log-record", "A domain log values list changed during admission.");
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidOperationException)
        { throw new RuntimeContractException("log-record", "A domain log values list changed during admission."); }
        RequireLogValues(values);
        return Array.AsReadOnly(values);
    }
    private static void RequireLogValues(IReadOnlyList<RuntimeLogValue> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            RuntimeJson.Require(ValueName(value.Name), "log-record", "A domain log value name is lower camelCase.");
            for (var earlier = 0; earlier < index; earlier++)
                RuntimeJson.Require(!string.Equals(values[earlier].Name, value.Name, StringComparison.Ordinal), "log-record",
                    "A domain log record names each value once.");
            RuntimeJson.Require(value.Kind switch
            {
                RuntimeLogValueKind.Text => value.Text != null && value.Text.Length <= MaxLogText && !RuntimeLogValue.HasControl(value.Text),
                RuntimeLogValueKind.Number => double.IsFinite(value.Number),
                RuntimeLogValueKind.Integer or RuntimeLogValueKind.Flag => true,
                _ => false
            }, "log-record", "A domain log value is a text of at most 256 characters with no control character, an integer, a finite number or a flag.");
            // A JSON reader keeps an integer exact only up to 2^53 - 1, and every Steam64 id lies above it.
            RuntimeJson.Require(value.Kind != RuntimeLogValueKind.Integer || value.Integer is >= -MaxSafeInteger and <= MaxSafeInteger,
                "log-record", "A domain log record never carries an account-shaped number.");
        }
    }

    private static bool AccountShaped(string? text) => RuntimeLogValue.AccountShaped(text);

    internal static string? Redact(string? text) => AccountShaped(text) ? "account-shaped" : text;

    private static bool ValueName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 48 || name[0] is < 'a' or > 'z') return false;
        foreach (var character in name)
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')) return false;
        return true;
    }

    /// <summary>Raises every provider to Trace for the rest of the session. Accepted once, only while registration is open,
    /// so the whole session is recorded under one tier and one set of limits.</summary>
    internal void RequireConfigured()
        => RuntimeJson.Require(logLevels != null, "log-unconfigured", "This kernel was constructed without a log sink.");

    internal void ElevateLogging()
    {
        RuntimeJson.Require(logLevels!.Tier == RuntimeLogTier.Player, "log-elevation-rejected", "Log elevation is accepted once and cannot be undone.");
        foreach (var gate in logGates.Values) gate.Level = RuntimeLogLevel.Trace;
        PublishLogLevels(RuntimeLogTier.Elevated);
    }

    internal RuntimeLogGate LogGate(string providerId)
    {
        RuntimeJson.Require(logGates.TryGetValue(providerId, out var gate), "log-provider-unregistered", providerId);
        return gate!;
    }

    /// <summary>The publisher's gate, resolved once when a dispatch starts and read by its dispatch and entry record
    /// points; the closed gate for a kernel without a sink.</summary>
    internal RuntimeLogGate DispatchGate(string providerId) => logSink == null ? closedLogGate : LogGate(providerId);

    /// <summary>A step record point's gate: the cell the loaded step captured, or — for a late result whose step is no
    /// longer at hand — the provider's current cell, closed once that provider unregistered.</summary>
    private RuntimeLogGate StepGate(RuntimeLogGate? captured, string providerId)
        => captured ?? (logGates.TryGetValue(providerId, out var gate) ? gate : closedLogGate);

    /// <summary>The only path from a record point to the sink. Call sites compare their gate first; reaching here with a
    /// disabled level means that branch was skipped.</summary>
    internal void WriteLog(in RuntimeLogRecord record)
    {
        RuntimeJson.Require(logSink != null, "log-unconfigured", "This kernel was constructed without a log sink.");
        RuntimeJson.Require(record.Code != null && record.Provider != null
            && (record.Plan is not { } plan || (plan.PlanId != null && plan.ResourceId != null && plan.ResourceRevision != null))
            && (record.Result is not { } result || (result.Status != null && result.Reason != null)),
            "log-record", "A log record needs code, provider, complete plan and result fields.");
        RuntimeJson.Require(record.Level != RuntimeLogLevel.Off && LogGate(record.Provider!).IsEnabled(record.Level),
            "log-level-disabled", record.Code!);
        var wasNotifying = NotifyingSink;
        NotifyingSink = true;
        try { logSink!.Write(in record, logLevels!); }
        finally { NotifyingSink = wasNotifying; }
    }

    private void PublishLogLevels(RuntimeLogTier tier)
        => logLevels = new RuntimeLogLevels(runtimeId,
            logGates.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new RuntimeLogProviderLevel(x.Key, x.Value.Level)).ToArray(), tier);

    // ---- Kernel record points (the event code and reason code tables). ------------------------------------------
    // Every point below is one gate comparison while disabled: the level of a code is fixed by the table, so a site never
    // inspects a status to decide whether to look. The record is a readonly struct handed to the sink by `in`, and no site
    // concatenates a string — the writer composes the message off the simulation thread.


    /// <summary>Event, plan and entry behind one executed step. Read-only and passed by `in`, so a record point adds no
    /// allocation to the dispatch loop; the entry node id is the `entry` field of `step.*` and `entry.stopped`.</summary>
    internal readonly struct StepOrigin
    {
        internal StepOrigin(in RuntimeEvent origin, in RuntimeLogPlan plan, string entry)
        {
            EventId = origin.EventId; CauseId = origin.CauseId; RootEventId = origin.RootEventId ?? origin.EventId;
            Plan = plan; Entry = entry;
        }
        internal string EventId { get; }
        internal string? CauseId { get; }
        internal string RootEventId { get; }
        internal RuntimeLogPlan Plan { get; }
        internal string Entry { get; }
    }

    /// <summary>Set once the diagnostic session ended: the Runtime's own error points stop writing.</summary>
    internal bool SessionEnded;
    private bool RuntimeErrorEnabled => !SessionEnded && logSink != null && LogGate(runtimeId).IsEnabled(RuntimeLogLevel.Error);

    internal void LogRegistrationRejected(string provider, string code, string detail)
    {
        if (!RuntimeErrorEnabled) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Error,
            Code = RuntimeLogCodes.RegistrationRejected,
            Provider = runtimeId,
            SubjectProvider = provider == runtimeId ? null : provider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            Detail = detail,
            Result = new RuntimeLogResult { Status = CommandStatuses.Rejected, Reason = code }
        });
    }

    internal void LogBindingRegistered(string provider, string bindingId)
    {
        if (logSink == null || !LogGate(provider).IsEnabled(RuntimeLogLevel.Info)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Info,
            Code = RuntimeLogCodes.BindingRegistered,
            Provider = provider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            Binding = bindingId
        });
    }

    internal void LogWorldBegan()
    {
        if (logSink == null || !LogGate(runtimeId).IsEnabled(RuntimeLogLevel.Info)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Info,
            Code = RuntimeLogCodes.WorldBegan,
            Provider = runtimeId,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch
        });
    }

    /// <summary>Enqueue of any event: a domain publish, a forwarded committed fact or an admitted schedule pulse. The record
    /// belongs to the provider that owns the trigger binding, and `rootEventId` falls back to the event's own id at a root.</summary>
    internal void LogTriggerFired(string provider, string bindingId, string eventId, string? causeId, string rootEventId)
    {
        if (logSink == null || !LogGate(provider).IsEnabled(RuntimeLogLevel.Info)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Info,
            Code = RuntimeLogCodes.TriggerFired,
            Provider = provider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            EventId = eventId,
            CauseId = causeId,
            RootEventId = rootEventId,
            Binding = bindingId
        });
    }

    /// <summary>An event refused outside the dispatch budget checks: a publish, a schedule or the non-host queue drain.
    /// <paramref name="subjectProvider"/> is the publisher when the kernel still knows it, and null when the refusal
    /// happened before that was resolved.</summary>
    internal void LogEventRejected(string bindingId, string eventId, string code, string? subjectProvider = null)
    {
        if (!RuntimeErrorEnabled) return;
        // The per-tick query ceilings refuse by design and say so (nothing is truncated); that is a limit working, not a fault.
        // A player who holds nothing has no weapon pool to name (`no-wielded-gear`): a state, which ForgeWeapon's own record already treats as info.
        var level = code is RuntimeAbiCodes.QueryBudget or "entity-query-budget" or "entity-query-tick-budget" or "no-wielded-gear"
            ? RuntimeLogLevel.Info : RuntimeLogLevel.Error;
        if (level != RuntimeLogLevel.Error && !(logGates.ContainsKey(runtimeId) ? LogGate(runtimeId) : closedLogGate).IsEnabled(level)) return;
        WriteRepeatedRefusal(new RuntimeLogRecord
        {
            Level = level,
            Code = RuntimeLogCodes.EventRejected,
            Provider = runtimeId,
            SubjectProvider = subjectProvider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            EventId = eventId,
            Binding = bindingId,
            Result = new RuntimeLogResult { Status = CommandStatuses.Rejected, Reason = code }
        });
    }

    /// <summary>A dispatch-phase budget refusal. The code table keeps it apart from `event.rejected`, so one refusal is
    /// recorded once.</summary>
    internal void LogBudgetExceeded(string bindingId, string eventId, string code)
    {
        if (!RuntimeErrorEnabled) return;
        WriteRepeatedRefusal(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Error,
            Code = RuntimeLogCodes.BudgetExceeded,
            Provider = runtimeId,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            EventId = eventId,
            Binding = bindingId,
            Result = new RuntimeLogResult { Status = CommandStatuses.Rejected, Reason = code }
        });
    }

    /// <summary>At most one per tick: an event left at the queue head by an exhausted per-tick or per-plan budget.</summary>
    internal void LogEventDeferred(string bindingId, string eventId, string code)
    {
        if (logSink == null || !LogGate(runtimeId).IsEnabled(RuntimeLogLevel.Trace)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Trace,
            Code = RuntimeLogCodes.EventDeferred,
            Provider = runtimeId,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            EventId = eventId,
            Binding = bindingId,
            Result = new RuntimeLogResult { Status = "deferred", Reason = code }
        });
    }

    /// <summary>A queued event dropped before dispatch because its publisher or scope is gone, or its plan was unloaded.
    /// The publisher is still known here, so it is written as the subject provider.</summary>
    internal void LogEventCancelled(string bindingId, string eventId, string code, string subjectProvider)
    {
        if (logSink == null || !LogGate(runtimeId).IsEnabled(RuntimeLogLevel.Trace)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Trace,
            Code = RuntimeLogCodes.EventCancelled,
            Provider = runtimeId,
            SubjectProvider = subjectProvider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            EventId = eventId,
            Binding = bindingId,
            Result = new RuntimeLogResult { Status = CommandStatuses.Cancelled, Reason = code }
        });
    }

    /// <summary>One entry point a dispatch reached whose own trigger options refused it. Written per refusal rather
    /// than per event, because the three numbers a card's options are debugged with — which entry, which check, how
    /// far it had got — belong to the entry and not to the event that happened to arrive.</summary>
    internal void LogGateRefused(TriggerGateReceipt refusal, RuntimeLogPlan? plan, string bindingId, string eventId)
    {
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Trace,
            Code = RuntimeLogCodes.GateRefused,
            Provider = runtimeId,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            EventId = eventId,
            Binding = bindingId,
            Entry = refusal.NodeId,
            Plan = plan,
            Result = new RuntimeLogResult { Status = CommandStatuses.Rejected, Reason = refusal.Code },
            GateAccumulated = refusal.Accumulated,
            GateFired = refusal.Fired
        });
    }

    /// <summary>One accepted event immediately before its claimed entries begin walking.</summary>
    internal void LogDispatchStarted(RuntimeLogGate gate, string provider, RuntimeEvent origin)
    {
        if (logSink == null || !gate.IsEnabled(RuntimeLogLevel.Trace)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Trace,
            Code = RuntimeLogCodes.DispatchStarted,
            Provider = provider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            EventId = origin.EventId,
            CauseId = origin.CauseId,
            RootEventId = origin.RootEventId ?? origin.EventId,
            Binding = origin.BindingId
        });
    }

    /// <summary>The matching dispatch boundary after every claimed entry has either completed or refused. A boundary is
    /// trace structure only: a refusal it closes over is already recorded once by `event.rejected`, `budget.exceeded`
    /// or `step.finished`, so it is never escalated to a player-tier level.</summary>
    internal void LogDispatchFinished(RuntimeLogGate gate, string provider, RuntimeEvent origin, string status, string reason)
    {
        if (logSink == null || !gate.IsEnabled(RuntimeLogLevel.Trace)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Trace,
            Code = RuntimeLogCodes.DispatchFinished,
            Provider = provider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            EventId = origin.EventId,
            CauseId = origin.CauseId,
            RootEventId = origin.RootEventId ?? origin.EventId,
            Binding = origin.BindingId,
            Result = new RuntimeLogResult { Status = status, Reason = reason }
        });
    }

    /// <summary>One plan entry immediately before its activation begins.</summary>
    internal void LogEntryStarted(in StepOrigin origin, RuntimeLogGate gate, string provider, string bindingId)
    {
        if (logSink == null || !gate.IsEnabled(RuntimeLogLevel.Trace)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Trace,
            Code = RuntimeLogCodes.EntryStarted,
            Provider = provider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            EventId = origin.EventId,
            CauseId = origin.CauseId,
            RootEventId = origin.RootEventId,
            Plan = origin.Plan,
            Entry = origin.Entry,
            Binding = bindingId
        });
    }

    /// <summary>The matching entry boundary; terminal command and control refusals are carried as the reason. Trace only,
    /// for the same reason as the dispatch boundary: the stop itself is `step.finished` / `entry.stopped`.</summary>
    internal void LogEntryFinished(in StepOrigin origin, RuntimeLogGate gate, string provider, string bindingId, string status, string reason)
    {
        if (logSink == null || !gate.IsEnabled(RuntimeLogLevel.Trace)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Trace,
            Code = RuntimeLogCodes.EntryFinished,
            Provider = provider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            EventId = origin.EventId,
            CauseId = origin.CauseId,
            RootEventId = origin.RootEventId,
            Plan = origin.Plan,
            Entry = origin.Entry,
            Binding = bindingId,
            Result = new RuntimeLogResult { Status = status, Reason = reason },
            Values = StepSummaryValues()
        });
    }

    // A succeeded control, pure or query step is bookkeeping: a counter display evaluates ~78 of them per refresh, and
    // two file lines each filled the 64 MiB session file (A3 lost 12149 records, errors included). Their session
    // evidence is one summary on the entry's `entry.finished`: how many ran per kind and the slowest one. Every other
    // step (action, owner, presentation) and every non-succeeded step stays a `step.started` / `step.finished` pair, so
    // a refusal or failure keeps its own record. The in-memory behavior trace (TraceStep) never passes through here and
    // still sees every step.
    private static bool IsSummarizedKind(string nodeKind) => nodeKind is "control" or "pure" or "query";

    internal struct StepSummary
    {
        internal int Control, Pure, Query;
        internal long SlowestTicks;
        internal string? SlowestStep;
    }
    internal StepSummary stepSummary;
    // Start timestamps of the summarized steps still open; control, pure and query steps nest, so this is a stack.
    private long[] stepStarts = new long[16];
    internal int stepStartDepth;

    private RuntimeLogValue[]? StepSummaryValues()
    {
        var summary = stepSummary;
        var total = summary.Control + summary.Pure + summary.Query;
        if (total == 0) return null;
        var values = new List<RuntimeLogValue>(6) { RuntimeLogValue.Of("steps", (long)total) };
        if (summary.Control != 0) values.Add(RuntimeLogValue.Of("control", (long)summary.Control));
        if (summary.Pure != 0) values.Add(RuntimeLogValue.Of("pure", (long)summary.Pure));
        if (summary.Query != 0) values.Add(RuntimeLogValue.Of("query", (long)summary.Query));
        values.Add(RuntimeLogValue.Of("slowestStep", summary.SlowestStep ?? ""));
        values.Add(RuntimeLogValue.Of("slowestMicros", summary.SlowestTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency));
        return values.ToArray();
    }

    internal void LogStepStarted(in StepOrigin origin, RuntimeLogGate? gate, string provider, string step, string bindingId, ref StepCommand command, string nodeKind)
    {
        if (logSink == null) return;
        if (IsSummarizedKind(nodeKind))
        {
            if (stepStartDepth == stepStarts.Length) Array.Resize(ref stepStarts, stepStarts.Length * 2);
            stepStarts[stepStartDepth++] = System.Diagnostics.Stopwatch.GetTimestamp();
            return;
        }
        WriteStepStarted(in origin, StepGate(gate, provider), provider, step, bindingId, ref command, nodeKind);
    }

    private void WriteStepStarted(in StepOrigin origin, RuntimeLogGate gate, string provider, string step, string bindingId, ref StepCommand command, string nodeKind)
    {
        if (!gate.IsEnabled(RuntimeLogLevel.Trace)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Trace,
            Code = RuntimeLogCodes.StepStarted,
            Provider = provider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            CommandId = command.Id,
            EventId = origin.EventId,
            CauseId = origin.CauseId,
            RootEventId = origin.RootEventId,
            Plan = origin.Plan,
            Entry = origin.Entry,
            Step = step,
            NodeKind = nodeKind,
            Binding = bindingId
        });
    }

    internal void LogStepFinished(in StepOrigin origin, RuntimeLogGate? gate, string provider, string step, string bindingId, ref StepCommand command,
        string nodeKind, string status, string? commit, string reason, string? detail = null, string? effectApplicationId = null,
        string? exceptionDetail = null, JsonElement outputs = default, ResultRowSet? rows = null)
    {
        if (logSink == null) return;
        var stepGate = StepGate(gate, provider);
        if (IsSummarizedKind(nodeKind))
        {
            var elapsed = stepStartDepth > 0 ? System.Diagnostics.Stopwatch.GetTimestamp() - stepStarts[--stepStartDepth] : 0;
            if (status == CommandStatuses.Succeeded && exceptionDetail == null && effectApplicationId == null)
            {
                switch (nodeKind)
                {
                    case "control": stepSummary.Control++; break;
                    case "pure": stepSummary.Pure++; break;
                    default: stepSummary.Query++; break;
                }
                if (stepSummary.SlowestStep == null || elapsed > stepSummary.SlowestTicks)
                { stepSummary.SlowestTicks = elapsed; stepSummary.SlowestStep = step; }
                return;
            }
            // Not a clean success: the step keeps its own started/finished pair, written together at its end.
            WriteStepStarted(in origin, stepGate, provider, step, bindingId, ref command, nodeKind);
        }
        var level = StepLevel(status, commit, reason, outputs, rows);
        // A late result can settle after its provider unregistered; that provider's record point is closed, not an error.
        if (!stepGate.IsEnabled(level)) return;
        List<RuntimeLogValue>? valueList = null;
        if (effectApplicationId != null) (valueList ??= new(4)).Add(RuntimeLogValue.Of("effectApplicationId", effectApplicationId));
        // Keep exception evidence outside the composed message's 256-character identity prefix.
        // Quote bounds untrusted native text and applies the existing account/control-character protection.
        if (exceptionDetail != null) (valueList ??= new(4)).Add(RuntimeLogValue.Quote("exception", exceptionDetail));
        // A step that did not succeed for every recipient names each recipient with its own code: the step's one reason
        // (`partial`, or the first failing row's code) cannot say which recipient failed or why the others did not.
        string? targets = null;
        if (status != CommandStatuses.Succeeded && TargetOutcomes(rows, outputs, out var targetCount, out var targetsTruncated) is { } outcomes)
        {
            targets = outcomes;
            valueList ??= new(4);
            valueList.Add(RuntimeLogValue.Of("targets", outcomes));
            valueList.Add(RuntimeLogValue.Of("targetCount", (long)targetCount));
            if (targetsTruncated) valueList.Add(RuntimeLogValue.Of("targetsTruncated", true));
        }
        var values = valueList?.ToArray();
        var record = new RuntimeLogRecord
        {
            Level = level,
            Code = RuntimeLogCodes.StepFinished,
            Provider = provider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            CommandId = command.Id,
            EventId = origin.EventId,
            CauseId = origin.CauseId,
            RootEventId = origin.RootEventId,
            Plan = origin.Plan,
            Entry = origin.Entry,
            Step = step,
            NodeKind = nodeKind,
            Binding = bindingId,
            Result = new RuntimeLogResult { Status = status, Commit = commit, Reason = reason },
            Detail = detail,
            Values = values
        };
        // A step refused or failed for the same reason on every event (a per-frame trigger, or a test that repeats one
        // stimulus) is one full record and then counted summaries: the plan, entry and step name the step, and a failure
        // adds its own suffix to the cause, so two different steps or outcomes never share a counter.
        // A failure with exception evidence stays a full record each time.
        if (level == RuntimeLogLevel.Error && status is CommandStatuses.Rejected or CommandStatuses.Failed && reason.Length != 0 && bindingId.Length != 0 && exceptionDetail == null)
            WriteRepeatedRefusal(in record, new RefusalSite(StepPlan: origin.Plan.PlanId, StepEntry: origin.Entry, Step: step, StepFailed: status == CommandStatuses.Failed, Targets: targets));
        else WriteLog(in record);
    }

    /// <summary>Each recipient row of a step as `id=code`, in recipient order, joined by commas: the row's target id (an
    /// entity's or resource's id, a handle's triple) and its own code. The rows are read where the step left them — the
    /// typed region when the row document was elided, otherwise the result port of the outputs. Null for a step that
    /// wrote no rows. A log text holds at most <see cref="MaxLogText"/> characters, so a longer list keeps the rows that
    /// fit and says so through <paramref name="truncated"/>; <paramref name="count"/> is always the full row count.</summary>
    private static string? TargetOutcomes(ResultRowSet? rows, JsonElement outputs, out int count, out bool truncated)
    {
        count = 0; truncated = false;
        System.Text.StringBuilder? text = null;
        if (rows != null)
        {
            var row = rows.Row;
            var frame = new Frame(rows.Space.Slots, row.Slot, rows.Space.Strings.Memory);
            var codeOffset = row.Columns[3].Offset;
            for (var index = 0; index < rows.Count; index++)
            {
                var slot = row.RowSlot(index);
                var id = row.TargetKind switch
                {
                    ValueKind.Entity => frame.String(slot),
                    ValueKind.Resource => frame.Resource(slot).Id,
                    ValueKind.Handle => frame.Handle(slot, out _).ToString(),
                    _ => "?"
                };
                var code = frame.Kind(slot + codeOffset) == ValueKind.String ? frame.String(slot + codeOffset) : "";
                AppendOutcome(ref text, id, code, ref count, ref truncated);
            }
        }
        else if (outputs.ValueKind == JsonValueKind.Object && outputs.TryGetProperty(ResultRows.Port, out var document))
        {
            if (document.ValueKind == JsonValueKind.Array)
                foreach (var element in document.EnumerateArray()) AppendOutcome(ref text, element, ref count, ref truncated);
            else AppendOutcome(ref text, document, ref count, ref truncated);
        }
        return text?.ToString();
    }

    private static void AppendOutcome(ref System.Text.StringBuilder? text, JsonElement row, ref int count, ref bool truncated)
    {
        if (row.ValueKind != JsonValueKind.Object) return;
        // The target is the row's first property (the row writer puts it first under the target column's own name).
        string id = "?";
        foreach (var property in row.EnumerateObject())
        {
            var target = property.Value;
            if (target.ValueKind == JsonValueKind.Object)
            {
                if (target.TryGetProperty("id", out var entity) && entity.ValueKind == JsonValueKind.String) id = entity.GetString()!;
                else if (target.TryGetProperty("resourceId", out var resource) && resource.ValueKind == JsonValueKind.String) id = resource.GetString()!;
                else if (target.TryGetProperty("local", out var local)) id = local.ToString();
            }
            break;
        }
        var code = row.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        AppendOutcome(ref text, id, code, ref count, ref truncated);
    }

    private static void AppendOutcome(ref System.Text.StringBuilder? text, string id, string code, ref int count, ref bool truncated)
    {
        count++;
        if (truncated) return;
        text ??= new System.Text.StringBuilder(64);
        var added = (text.Length == 0 ? 0 : 1) + id.Length + 1 + code.Length;
        if (text.Length + added > MaxLogText) { truncated = true; return; }
        if (text.Length != 0) text.Append(',');
        text.Append(id).Append('=').Append(code);
    }

    internal void LogStepFinished(in StepOrigin origin, RuntimeLogGate? gate, string provider, string step, string bindingId, ref StepCommand command,
        string nodeKind, in CommandResult result, string? effectApplicationId = null, ResultRowSet? rows = null)
        => LogStepFinished(in origin, gate, provider, step, bindingId, ref command, nodeKind,
            result.Status, result.CommitState, result.Code,
            result.Code is "from-step-slot" or "from-step-value" ? result.Detail : result.CostSettlementLogDetail, effectApplicationId,
            result.Code == "handler-exception" ? result.Detail : null, result.Outputs, rows);

    /// <summary>Step result codes are the kernel's own: nothing is normalized here, and a code the reason table does not list
    /// is a table gap to report rather than something the kernel invents. A cancellation that waits for the owner's
    /// receipt is an operation in flight, not a failure: its commit is unknown by definition until the receipt arrives,
    /// and a receipt that fails or never settles is reported under its own code (`owner-cancel-failed`).</summary>
    internal RuntimeLogLevel StepLevel(string status, string? commit, string reason, JsonElement outputs = default, ResultRowSet? rows = null)
        => (status == CommandStatuses.Failed || commit == CommitStates.Unknown)
                && !(rows != null ? AwaitsOwnerOnly(reason, rows) : AwaitsOwnerOnly(reason, outputs))
            || status == CommandStatuses.Rejected && reason is "from-step-slot" or "from-step-value"
            ? RuntimeLogLevel.Error : RuntimeLogLevel.Info;

    /// <summary>The same question asked of the step's rows where they stand in their region: the status and commit
    /// columns are read as the member indices the writer stored and the code is compared in place, so the answer
    /// needs no rendered document.</summary>
    private bool AwaitsOwnerOnly(string reason, ResultRowSet rows)
    {
        if (reason is not (ResultCode.OwnerCancelPending or ResultCode.Partial)) return false;
        var row = rows.Row;
        var frame = new Frame(rows.Space.Slots, row.Slot, rows.Space.Strings.Memory);
        var failed = row.StatusMembers[(int)Status.Failed]; var unknown = row.CommitMembers[(int)Commit.Unknown];
        var pending = false;
        for (var index = 0; index < rows.Count; index++)
        {
            var slot = row.RowSlot(index);
            var unsettled = frame.Kind(slot + row.Columns[1].Offset) == ValueKind.Enum && frame.EnumIndex(slot + row.Columns[1].Offset) == failed
                || frame.Kind(slot + row.Columns[2].Offset) == ValueKind.Enum && frame.EnumIndex(slot + row.Columns[2].Offset) == unknown;
            if (!unsettled) continue;
            if (!frame.StringEquals(slot + row.Columns[3].Offset, OwnerCancelPendingUtf8)) return false;
            pending = true;
        }
        return pending;
    }
    private static ReadOnlySpan<byte> OwnerCancelPendingUtf8 => "owner-cancel-pending"u8;

    /// <summary>Whether an unsettled step is unsettled only because cancellations wait for the owner's receipt. A result
    /// without recipient rows answers by its own code; a row result (`owner-cancel-pending` names its first unknown row,
    /// `partial` names none) answers by its rows: every failed or unknown row must be `owner-cancel-pending`, so a real
    /// failure beside a pending one still reads as an error.</summary>
    private static bool AwaitsOwnerOnly(string reason, JsonElement outputs)
    {
        if (reason is not (ResultCode.OwnerCancelPending or ResultCode.Partial)) return false;
        if (outputs.ValueKind != JsonValueKind.Object || !outputs.TryGetProperty(ResultRows.Port, out var rows))
            return reason == ResultCode.OwnerCancelPending;
        bool pending = false;
        if (rows.ValueKind != JsonValueKind.Array) return AwaitsOwner(rows, ref pending) && pending;
        foreach (var row in rows.EnumerateArray()) if (!AwaitsOwner(row, ref pending)) return false;
        return pending;
    }

    /// <summary>False for a row that failed or whose commit is unknown for any reason but the owner's pending receipt.</summary>
    private static bool AwaitsOwner(JsonElement row, ref bool pending)
    {
        if (row.ValueKind != JsonValueKind.Object) return true;
        bool unsettled = row.TryGetProperty("status", out var status) && status.ValueEquals(CommandStatuses.Failed)
            || row.TryGetProperty("committed", out var committed) && committed.ValueEquals(CommitStates.Unknown);
        if (!unsettled) return true;
        if (!row.TryGetProperty("code", out var code) || !code.ValueEquals(ResultCode.OwnerCancelPending)) return false;
        pending = true;
        return true;
    }

    /// <summary>Low-frequency cleanup evidence, never an additional effect scheduler. One row per actual
    /// application keeps identities bounded instead of truncating a joined layer list. A returned callback is
    /// still only callback evidence; native readback proves the game result. Queuing an owner restore is not its receipt.</summary>
    private static readonly (string Property, string ValueName)[] EffectRestoreHandleFields =
    {
        ("worldEpoch", "handleWorldEpoch"), ("lifeEpoch", "handleLifeEpoch"),
        ("local", "handleLocal"), ("provider", "handleProvider")
    };

    internal void LogEffectRestore(string provider, RuntimeEffectContext context, string outcome)
    {
        try
        {
            const RuntimeLogLevel level = RuntimeLogLevel.Trace;
            if (logSink == null || !logGates.TryGetValue(provider, out var gate) || !gate.IsEnabled(level)) return;
            var handle = context.Handle;
            for (var index = 0; index < context.ApplicationIds.Count; index++)
            {
                var values = new List<RuntimeLogValue>(14)
                {
                    RuntimeLogValue.Of("phase", outcome == "attempt" ? "attempt" : "outcome"),
                    RuntimeLogValue.Of("outcome", outcome),
                    RuntimeLogValue.Of("effectApplicationId", context.ApplicationIds[index]),
                    RuntimeLogValue.Of("applicationIndex", index),
                    RuntimeLogValue.Of("applicationCount", context.ApplicationIds.Count),
                    RuntimeLogValue.Of("planId", context.PlanId),
                    RuntimeLogValue.Of("nodeId", context.NodeId),
                    RuntimeLogValue.Of("commit", outcome == "returned" ? CommitStates.Confirmed
                        : outcome == "attempt" ? CommitStates.None : CommitStates.Unknown),
                    RuntimeLogValue.Of("sourceWorldEpoch", context.WorldEpoch),
                    RuntimeLogValue.Of("reason", context.Reason)
                };
                if (handle.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var field in EffectRestoreHandleFields)
                        if (handle.TryGetProperty(field.Property, out var value) && value.TryGetInt64(out var number))
                            values.Add(RuntimeLogValue.Of(field.ValueName, number));
                WriteLog(new RuntimeLogRecord
                {
                    Level = level,
                    Code = "effect.restore",
                    Provider = provider,
                    Tick = CurrentTick,
                    WorldEpoch = WorldEpoch,
                    CommandId = context.CommandId,
                    Binding = context.BindingId,
                    Values = values
                });
            }
        }
        catch (Exception)
        {
            // A diagnostic sink failure must not retry or discard a native cleanup. Missing evidence remains unproven.
        }
    }

    internal void LogEntryStopped(in StepOrigin origin, string step, ref StepCommand command, string bindingId, string status, string reason)
    {
        if (logSink == null || !LogGate(runtimeId).IsEnabled(RuntimeLogLevel.Info)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Info,
            Code = RuntimeLogCodes.EntryStopped,
            Provider = runtimeId,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            CommandId = command.Id,
            EventId = origin.EventId,
            Plan = origin.Plan,
            Entry = origin.Entry,
            Step = step,
            Binding = bindingId,
            Result = new RuntimeLogResult { Status = status, Reason = reason }
        });
    }

    /// <summary>An observer callback threw and the kernel removed it. The record belongs to the observer's provider.</summary>
    internal void LogObserverFailed(string provider, string detail)
    {
        if (logSink == null || !LogGate(provider).IsEnabled(RuntimeLogLevel.Error)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Error,
            Code = RuntimeLogCodes.ObserverFailed,
            Provider = provider,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            Detail = detail,
            Result = new RuntimeLogResult { Status = CommandStatuses.Failed, Reason = RuntimeLogReasonCodes.LifecycleObserverFailed }
        });
    }

    /// <summary>A provider-supplied callback threw and the kernel answered with its own named result instead of letting
    /// the exception out. The record carries <paramref name="what"/> (which callback, for which subject) and the
    /// exception's type and message, so the named result never stands alone. It is attributed to Runtime: the provider
    /// that threw may have unregistered since, and a record point must not throw inside a catch.</summary>
    internal void LogCallbackFailed(string what, Exception error)
    {
        if (logSink == null || !LogGate(runtimeId).IsEnabled(RuntimeLogLevel.Error)) return;
        LogObserverFailed(runtimeId, what + ": " + RuntimeDiagnostics.Describe(error));
    }

    /// <summary>The same record for a callback whose message is withheld from every report because native tables can
    /// carry account-level identity: only the exception type is written.</summary>
    internal void LogCallbackFailedTypeOnly(string what, Exception error)
    {
        if (logSink == null || !LogGate(runtimeId).IsEnabled(RuntimeLogLevel.Error)) return;
        LogObserverFailed(runtimeId, what + ": " + error.GetType().Name);
    }

    /// <summary>One record per pause, including a failed startup and a stop. <paramref name="detail"/> is the host's own text.</summary>
    internal void LogSuspended(string code, string? detail = null)
    {
        if (logSink == null || !LogGate(runtimeId).IsEnabled(RuntimeLogLevel.Error)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Error,
            Code = RuntimeLogCodes.RuntimeSuspended,
            Provider = runtimeId,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            Detail = detail,
            Result = new RuntimeLogResult { Status = CommandStatuses.Failed, Reason = code }
        });
    }

    /// <summary>The record point for the level-layout observation (`map.layout-generated`, contract §3.2). The
    /// caller is the authoring layer that watched the level generator; it owns the facts, the kernel owns the
    /// provider, the level gate, the single-thread rule and the sink. A disabled or unreadable observation writes
    /// nothing and throws nothing: a level that generated is not a runtime error, and an unreadable layout is
    /// refused by <see cref="RuntimeLogLayoutCheck"/> with a <c>log-record</c> instead of reaching the file.</summary>
    internal void ReportGeneratedLayout(in RuntimeLogLayout layout)
    {
        if (logSink == null || !LogGate(runtimeId).IsEnabled(RuntimeLogLevel.Info)) return;
        RuntimeLogLayoutCheck.Require(in layout);
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Info,
            Code = RuntimeLogCodes.MapLayoutGenerated,
            Provider = runtimeId,
            Tick = CurrentTick < 0 ? 0 : CurrentTick,
            WorldEpoch = WorldEpoch,
            Layout = layout
        });
    }

    /// <summary>The record point for the tick rate the whole session converts by (contract §3.2, 时间单位: one tick is
    /// one host <c>FixedUpdate</c>, the rate is <c>1/Time.fixedDeltaTime</c>). The caller is the host's game binding,
    /// because it is the only side that may read Unity; it reads the fixed step and computes the rate, while the kernel
    /// owns the provider, the level gate, the single-thread rule and the sink. A rate a tick cannot be counted with —
    /// a step or rate that is not finite and positive — is refused with the same <c>log-record</c> an unreadable layout
    /// gets rather than recorded or written as a number no conversion may use. A disabled level writes no line; the
    /// rate is still the running world's own answer to <see cref="TicksPerSecond"/>, because recording the world's
    /// rate and logging it are two different jobs and a run that does not log its rate is not a run without one. A rate
    /// that disagrees with the one source (<see cref="RuntimeTickRate"/>) also writes the error
    /// <c>runtime.tick-rate-mismatch</c>.</summary>
    internal void ReportTickRate(double fixedDeltaTime, double ticksPerSecond)
    {
        if (logSink == null) return;
        // The host computes the rate in single precision, so a matching step lands within a rounding error of the source.
        var expected = RuntimeTickRate.TicksPerSecond;
        if (Math.Abs(ticksPerSecond - expected) > 1e-3 && LogGate(runtimeId).IsEnabled(RuntimeLogLevel.Error))
            WriteLog(new RuntimeLogRecord
            {
                Level = RuntimeLogLevel.Error,
                Code = RuntimeLogCodes.RuntimeTickRateMismatch,
                Provider = runtimeId,
                Tick = CurrentTick < 0 ? 0 : CurrentTick,
                WorldEpoch = WorldEpoch,
                FixedDeltaTime = fixedDeltaTime,
                MeasuredTicksPerSecond = ticksPerSecond,
                ExpectedTicksPerSecond = expected
            });
        if (!LogGate(runtimeId).IsEnabled(RuntimeLogLevel.Info)) return;
        WriteLog(new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Info,
            Code = RuntimeLogCodes.RuntimeTickRate,
            Provider = runtimeId,
            Tick = CurrentTick < 0 ? 0 : CurrentTick,
            WorldEpoch = WorldEpoch,
            FixedDeltaTime = fixedDeltaTime,
            TicksPerSecond = ticksPerSecond
        });
    }
}

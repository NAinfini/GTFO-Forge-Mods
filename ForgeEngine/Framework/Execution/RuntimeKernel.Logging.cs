using System;

namespace ForgeEngine.Framework;

// The facade's log entries: the thread check, the checks that need registration or plan state, and the names the
// host and the packages call. The sink, the gates and every record point live in KernelLog.
public sealed partial class RuntimeKernel
{
    private readonly KernelLog log;

    internal const long DiagnosticSummaryWindowMs = KernelLog.DiagnosticSummaryWindowMs;
    internal const int MaximumDiagnosticSummaryKeys = KernelLog.MaximumDiagnosticSummaryKeys;

    /// <summary>Host constructor. The sink is fixed for this kernel's lifetime. <paramref name="runtimeLogLevel"/> is the host cfg
    /// level of the Runtime's own provider (<see cref="RuntimeIdentity.Id"/>); a domain provider has no level until its package
    /// hands one over with <see cref="RuntimeKernel.RegisterModule"/>, so looking one up earlier is a contract violation
    /// rather than a default.</summary>
    public RuntimeKernel(RuntimeIdentity identity, RuntimeLimits limits, IRuntimeLogSink logSink, RuntimeLogLevel runtimeLogLevel)
        : this(identity, limits)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        RuntimeJson.Require(runtimeLogLevel is RuntimeLogLevel.Off or RuntimeLogLevel.Error or RuntimeLogLevel.Info,
            "log-level", "Configured log levels are off, error or info; trace is only reachable through elevation.");
        log.Attach(logSink, runtimeLogLevel);
    }

    /// <summary>The gate a module handle caches for its own record point. A kernel without a sink, or a handle whose
    /// registration is already gone, gets the closed gate: such a record point never writes.</summary>
    internal RuntimeLogGate ModuleLogGate(string providerId, long generation)
    {
        ReadThread();
        return log.ModuleLogGate(providerId, registry.IsRegistered(providerId, generation));
    }

    /// <summary>The same provider cell captured by a loaded step.</summary>
    internal RuntimeLogGate PlanLogGate(string providerId) => log.PlanLogGate(providerId);

    /// <summary>Whether the host's own record point (<see cref="LogRuntime"/>) writes at <paramref name="level"/>. Host
    /// assembly only: a domain package writes through its own <see cref="RuntimeModuleHandle.Log"/>.</summary>
    internal bool RuntimeLogs(RuntimeLogLevel level) => log.RuntimeLogs(level);

    /// <summary>The host's structured record point: the same path as a module's, attributed to the Runtime provider.</summary>
    internal void LogRuntime(RuntimeLogLevel level, string code, in RuntimeLogFacts facts)
    {
        if (!log.RuntimeLogs(level)) return;
        WriteDomainLog(Identity.Id, level, code, in facts);
    }

    internal void WriteDomainLog(string provider, RuntimeLogLevel level, string code, in RuntimeLogFacts facts)
    {
        ReadThread();
        log.WriteDomainLog(provider, level, code, in facts);
    }

    /// <summary>Raises every provider to Trace for the rest of the session. Accepted once, only while registration is open,
    /// so the whole session is recorded under one tier and one set of limits.</summary>
    internal void ElevateLogging()
    {
        Mutable();
        log.RequireConfigured();
        RuntimeJson.Require(IsRegistrationOpen, "log-elevation-rejected", "Log elevation is only accepted before StartRuntime closes registration.");
        log.ElevateLogging();
    }

    internal RuntimeLogGate LogGate(string providerId) { ReadThread(); return log.LogGate(providerId); }

    internal void WriteLog(in RuntimeLogRecord record) { ReadThread(); log.WriteLog(in record); }

    internal void LogObserverFailed(string provider, string detail) { if (log.HasSink) ReadThread(); log.LogObserverFailed(provider, detail); }

    internal void LogCallbackFailed(string what, Exception error) { if (log.HasSink) ReadThread(); log.LogCallbackFailed(what, error); }

    internal void LogSuspended(string code, string? detail = null) { ReadThread(); log.LogSuspended(code, detail); }

    /// <summary>The provider a binding belongs to. `step.*` and `trigger.fired` are attributed to it rather than to Runtime.</summary>
    private string BindingProvider(string bindingId) => RuntimeJson.Text(registry.Tables.Bindings[bindingId], "providerId");

    /// <summary>One entry point a dispatch reached whose own trigger options refused it. The refused entry's plan is
    /// loaded (the gate only runs for a claimed entry), so its resource identity is the one the rest of the dispatch's
    /// records carry; a plan unloaded in between is written without one.</summary>
    private void LogGateRefused(TriggerGateReceipt refusal, string bindingId, string eventId)
    {
        if (!log.HasSink || !log.LogGate(Identity.Id).IsEnabled(RuntimeLogLevel.Trace)) return;
        var plan = plans.Loaded.TryGetValue(refusal.PlanId, out var loaded)
            ? new RuntimeLogPlan { PlanId = refusal.PlanId, ResourceId = loaded.Plan.ResourceId, ResourceRevision = loaded.Plan.ResourceRevision }
            : (RuntimeLogPlan?)null;
        log.LogGateRefused(refusal, plan, bindingId, eventId);
    }

    internal int BoundedDiagnosticSummaryKeyCount => log.BoundedDiagnosticSummaryKeyCount;
    internal int DiagnosticSummaryKeyCount => log.DiagnosticSummaryKeyCount;

    // Deterministic host-harness clock; production uses the monotonic process clock, never a game tick.
    internal void SetDiagnosticClock(Func<long> monotonicMilliseconds) { ReadThread(); log.SetDiagnosticClock(monotonicMilliseconds); }

    internal void PollDiagnosticSummaries() { ReadThread(); log.PollDiagnosticSummaries(); }

    internal void FlushDiagnosticSummaries() { ReadThread(); log.FlushDiagnosticSummaries(); }

    /// <summary>The record point for the level-layout observation (`map.layout-generated`, contract §3.2). The
    /// caller is the authoring layer that watched the level generator; it owns the facts, the kernel owns the
    /// provider, the level gate, the single-thread rule and the sink.</summary>
    public void ReportGeneratedLayout(in RuntimeLogLayout layout) { ReadThread(); log.ReportGeneratedLayout(in layout); }

    /// <summary>Records the running world's tick rate and hands it to the log's record point. A rate a tick cannot be
    /// counted with is refused with <c>log-record</c>; a disabled level writes no line and the rate is still the
    /// running world's own answer to <see cref="TicksPerSecond"/>.</summary>
    internal void ReportTickRate(double fixedDeltaTime, double ticksPerSecond)
    {
        ReadThread();
        RuntimeJson.Require(double.IsFinite(fixedDeltaTime) && fixedDeltaTime > 0
            && double.IsFinite(ticksPerSecond) && ticksPerSecond > 0, "log-record",
            "A tick rate needs a finite positive fixed step and a finite positive rate.");
        world.tickRate = ticksPerSecond;
        log.ReportTickRate(fixedDeltaTime, ticksPerSecond);
    }
}

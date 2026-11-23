using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>One authoring-only execution observation. Payload values are cloned only when a subscriber exists, so
/// ordinary Play mode pays no frame-copy cost. A callback receives immutable snapshots and must not mutate Runtime.</summary>
public readonly struct RuntimeBehaviorTraceRecord
{
    public string Scope { get; init; }
    public string Phase { get; init; }
    public long Tick { get; init; }
    public long WorldEpoch { get; init; }
    public string Provider { get; init; }
    public string Binding { get; init; }
    public string EventId { get; init; }
    public string? CauseId { get; init; }
    public string RootEventId { get; init; }
    public RuntimeLogPlan? Plan { get; init; }
    public string? Entry { get; init; }
    public string? Step { get; init; }
    public string? NodeKind { get; init; }
    public string? CommandId { get; init; }
    public JsonElement? Inputs { get; init; }
    public JsonElement? Parameters { get; init; }
    public JsonElement? Outputs { get; init; }
    public RuntimeLogResult? Result { get; init; }
}

/// <summary>Disposable authoring trace subscription. It owns no provider or gameplay ability.</summary>
public sealed class RuntimeBehaviorTraceSubscription : IDisposable
{
    private RuntimeKernel? kernel;
    private readonly long id;
    internal RuntimeBehaviorTraceSubscription(RuntimeKernel kernel, long id) { this.kernel = kernel; this.id = id; }
    public bool IsActive => kernel?.IsBehaviorTraceObserverActive(id) == true;
    public void Dispose()
    {
        var current = kernel;
        if (current == null) return;
        kernel = null;
        current.RemoveBehaviorTraceObserver(id);
    }
}
public sealed partial class RuntimeKernel
{
    public const int MaximumBehaviorTraceObservers = 8;
    internal sealed record BehaviorTraceObserver(long Id, Action<RuntimeBehaviorTraceRecord> Callback);
    private readonly Dictionary<long, BehaviorTraceObserver> behaviorTraceObservers = new();
    /// <summary>The observers in subscription order, rebuilt only when the set changes, so a record is handed out without
    /// sorting or copying the set again.</summary>
    private BehaviorTraceObserver[] behaviorTraceOrder = Array.Empty<BehaviorTraceObserver>();
    private void ReorderBehaviorTraceObservers() => behaviorTraceOrder = behaviorTraceObservers.Values.OrderBy(x => x.Id).ToArray();
    private long behaviorTraceSequence;
    internal bool notifyingBehaviorTrace;

    /// <summary>Subscribes to behavior-editor execution facts. This is a read-only diagnostics surface; it does not
    /// register a provider, alter plan semantics or change logging levels.</summary>
    public RuntimeBehaviorTraceSubscription ObserveBehaviorTrace(Action<RuntimeBehaviorTraceRecord> callback)
    {
        ReadThread();
        ArgumentNullException.ThrowIfNull(callback);
        RuntimeJson.Require(!advancing && !notifyingBehaviorTrace, "behavior-trace-mutation",
            "Behavior trace subscriptions cannot change during dispatch or a trace callback.");
        RuntimeJson.Require(behaviorTraceObservers.Count < MaximumBehaviorTraceObservers,
            "behavior-trace-budget", "Behavior trace observer capacity reached.");
        var id = checked(++behaviorTraceSequence);
        behaviorTraceObservers.Add(id, new BehaviorTraceObserver(id, callback));
        ReorderBehaviorTraceObservers();
        return new RuntimeBehaviorTraceSubscription(this, id);
    }

    internal bool IsBehaviorTraceObserverActive(long id)
    { ReadThread(); return behaviorTraceObservers.ContainsKey(id); }

    internal void RemoveBehaviorTraceObserver(long id)
    { ReadThread(); if (behaviorTraceObservers.Remove(id)) ReorderBehaviorTraceObservers(); }

    internal static JsonElement? TraceValue(JsonElement? value)
        => value is { ValueKind: not JsonValueKind.Undefined } v ? v.Clone() : null;

    private void PublishBehaviorTrace(in RuntimeBehaviorTraceRecord record)
    {
        if (behaviorTraceObservers.Count == 0) return;
        notifyingBehaviorTrace = true;
        try
        {
            foreach (var observer in behaviorTraceOrder)
            {
                if (!behaviorTraceObservers.ContainsKey(observer.Id)) continue;
                try { observer.Callback(record); }
                catch (Exception error)
                {
                    behaviorTraceObservers.Remove(observer.Id);
                    ReorderBehaviorTraceObservers();
                    log.LogObserverFailed(Identity.Id, "behavior-trace-observer-failed: " + RuntimeDiagnostics.Describe(error));
                }
            }
        }
        finally { notifyingBehaviorTrace = false; }
    }
    private void TraceTrigger(string phase, string provider, RuntimeEvent origin, RuntimeLogResult? result = null)
    {
        if (behaviorTraceObservers.Count == 0) return;
        PublishBehaviorTrace(new RuntimeBehaviorTraceRecord
        {
            Scope = "trigger",
            Phase = phase,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            Provider = provider,
            Binding = origin.BindingId,
            EventId = origin.EventId,
            CauseId = origin.CauseId,
            RootEventId = origin.RootEventId ?? origin.EventId,
            Inputs = TraceValue(origin.Outputs),
            Result = result
        });
    }

    private void TraceEntry(string phase, in KernelLog.StepOrigin origin, string provider, string binding,
        RuntimeLogResult? result = null)
    {
        if (behaviorTraceObservers.Count == 0) return;
        PublishBehaviorTrace(new RuntimeBehaviorTraceRecord
        {
            Scope = "behavior-entry",
            Phase = phase,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            Provider = provider,
            Binding = binding,
            EventId = origin.EventId,
            CauseId = origin.CauseId,
            RootEventId = origin.RootEventId,
            Plan = origin.Plan,
            Entry = origin.Entry,
            Result = result
        });
    }

    private static RuntimeLogResult TraceResult(in CommandResult result)
        => new() { Status = result.Status, Commit = result.CommitState, Reason = result.Code };

    private void TraceStep(string phase, in KernelLog.StepOrigin origin, string provider, string step, string binding,
        ref StepCommand command, string nodeKind, JsonElement? inputs = null, JsonElement? parameters = null,
        JsonElement? outputs = null, RuntimeLogResult? result = null)
    {
        if (behaviorTraceObservers.Count == 0) return;
        PublishBehaviorTrace(new RuntimeBehaviorTraceRecord
        {
            Scope = "behavior-node",
            Phase = phase,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            Provider = provider,
            Binding = binding,
            EventId = origin.EventId,
            CauseId = origin.CauseId,
            RootEventId = origin.RootEventId,
            Plan = origin.Plan,
            Entry = origin.Entry,
            Step = step,
            NodeKind = nodeKind,
            CommandId = command.Id,
            Inputs = TraceValue(inputs),
            Parameters = TraceValue(parameters),
            Outputs = TraceValue(outputs),
            Result = result
        });
    }

    private void ClearBehaviorTraceObservers() { behaviorTraceObservers.Clear(); ReorderBehaviorTraceObservers(); }

    private void TracePresentationResult(PresentationOutput request, CommandResult result)
    {
        if (behaviorTraceObservers.Count == 0) return;
        var origin = request.Origin;
        PublishBehaviorTrace(new RuntimeBehaviorTraceRecord
        {
            Scope = "behavior-node",
            Phase = request.IsCleanup ? "ended" : "after",
            Tick = CurrentTick,
            WorldEpoch = request.WorldEpoch,
            Provider = request.Provider,
            Binding = request.BindingId,
            EventId = request.EventId,
            CauseId = origin.CauseId,
            RootEventId = origin.RootEventId ?? request.EventId,
            Plan = origin.Plan,
            Entry = origin.Entry,
            Step = request.NodeId,
            NodeKind = "action",
            CommandId = request.CommandId,
            Inputs = TraceValue(request.TraceInputs),
            Parameters = TraceValue(request.TraceParameters),
            Outputs = TraceValue(result.Outputs),
            Result = TraceResult(in result)
        });
    }
}

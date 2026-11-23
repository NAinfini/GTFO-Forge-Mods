using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>One admitted trigger event as an observer sees it: the provider's payload exactly as plans receive it.
/// The payload is the kernel's own immutable snapshot; copy what you keep, change nothing.</summary>
public readonly record struct RuntimeEventView(string EventId, string BindingId, long WorldEpoch, long SimulationTick,
    string ScopeId, JsonElement Outputs, string? CauseId, string? RootEventId);

/// <summary>Disposable read-only trigger subscription. It owns no provider, binding or gameplay ability.</summary>
public sealed class RuntimeTriggerSubscription : IDisposable
{
    private RuntimeKernel? kernel;
    private readonly long id;
    internal RuntimeTriggerSubscription(RuntimeKernel kernel, long id) { this.kernel = kernel; this.id = id; }
    public bool IsActive => kernel?.IsTriggerObserverActive(id) == true;
    public void Dispose()
    {
        var current = kernel;
        if (current == null) return;
        kernel = null;
        current.RemoveTriggerObserver(id);
    }
}

public sealed partial class RuntimeModuleHandle
{
    /// <summary>Watches one registered trigger binding, of any provider, without taking part in scheduling: the
    /// callback sees each event a provider's <c>Publish</c> admitted on that binding (valid payload, not a duplicate),
    /// whether or not a plan consumes it, and cannot change it, answer it or publish from inside the callback. The
    /// observer counts as a consumer in <see cref="RuntimeSubscriptionGate"/>, so the provider builds the event. A
    /// callback that throws is removed and logged; the dispatch is unaffected. Events a schedule pulses are not
    /// observed. The subscription ends on dispose and when this module unregisters.</summary>
    public RuntimeTriggerSubscription ObserveTrigger(string bindingId, Action<RuntimeEventView> callback)
        => kernel.ObserveTrigger(this, bindingId, callback);
}

public sealed partial class RuntimeKernel
{
    public const int MaximumTriggerObservers = 64;
    internal sealed class TriggerObserver
    {
        internal TriggerObserver(long id, string provider, long generation, string binding, Action<RuntimeEventView> callback)
        { Id = id; Provider = provider; Generation = generation; Binding = binding; Callback = callback; }
        internal long Id { get; }
        internal string Provider { get; }
        internal long Generation { get; }
        internal string Binding { get; }
        internal Action<RuntimeEventView> Callback { get; }
        internal bool Active = true;
    }
    private readonly Dictionary<long, TriggerObserver> triggerObservers = new();
    /// <summary>The observers of one binding in subscription order, rebuilt only when the set changes, so a publish
    /// walks a ready array and allocates nothing.</summary>
    private readonly Dictionary<string, TriggerObserver[]> observedByBinding = new(StringComparer.Ordinal);
    private long triggerObserverSequence;
    private bool notifyingTriggers;

    private bool IsObserved(string bindingId) => triggerObservers.Count != 0 && observedByBinding.ContainsKey(bindingId);

    internal RuntimeTriggerSubscription ObserveTrigger(RuntimeModuleHandle owner, string bindingId, Action<RuntimeEventView> callback)
    {
        ReadThread(); AcceptRuntimeWork(true); ArgumentNullException.ThrowIfNull(callback);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        RuntimeJson.Require(registry.BindingViews.TryGetValue(bindingId, out var view) && view!.IsTrigger, "not-trigger", bindingId);
        RuntimeJson.Require(triggerObservers.Count < MaximumTriggerObservers, "trigger-observer-budget",
            "Trigger observer capacity reached.");
        var id = checked(++triggerObserverSequence);
        triggerObservers.Add(id, new TriggerObserver(id, owner.ProviderId, owner.Generation, bindingId, callback));
        RebuildTriggerObservers(bindingId);
        return new RuntimeTriggerSubscription(this, id);
    }

    internal bool IsTriggerObserverActive(long id) { ReadThread(); return triggerObservers.ContainsKey(id); }

    internal void RemoveTriggerObserver(long id)
    {
        ReadThread();
        if (!triggerObservers.Remove(id, out var observer)) return;
        observer.Active = false;
        RebuildTriggerObservers(observer.Binding);
    }

    private void RemoveTriggerObservers(string provider, long generation)
    {
        foreach (var observer in triggerObservers.Values.Where(o => o.Provider == provider && o.Generation == generation).ToArray())
            RemoveTriggerObserver(observer.Id);
    }

    private void ClearTriggerObservers()
    {
        foreach (var observer in triggerObservers.Values) observer.Active = false;
        triggerObservers.Clear(); observedByBinding.Clear();
        RefreshSubscriptionGates();
    }

    private void RebuildTriggerObservers(string bindingId)
    {
        var list = triggerObservers.Values.Where(o => o.Binding == bindingId).OrderBy(o => o.Id).ToArray();
        if (list.Length == 0) observedByBinding.Remove(bindingId); else observedByBinding[bindingId] = list;
        RefreshSubscriptionGates();
    }

    private void NotifyTriggerObservers(RuntimeEvent value)
    {
        if (!observedByBinding.TryGetValue(value.BindingId, out var list)) return;
        var view = new RuntimeEventView(value.EventId, value.BindingId, value.WorldEpoch, value.SimulationTick,
            value.ScopeId, value.Outputs, value.CauseId, value.RootEventId);
        var outer = notifyingTriggers;
        notifyingTriggers = true;
        try
        {
            foreach (var observer in list)
            {
                if (!observer.Active) continue;
                try { observer.Callback(view); }
                catch (Exception error)
                {
                    RemoveTriggerObserver(observer.Id);
                    log.LogObserverFailed(observer.Provider, "trigger-observer-failed: " + RuntimeDiagnostics.Describe(error));
                }
            }
        }
        finally { notifyingTriggers = outer; }
    }
}

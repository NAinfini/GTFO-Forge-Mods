using System;
using System.Collections.Generic;
using System.Linq;
using ForgeCore;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeModuleHandle
{
    public RuntimeLifecycleSubscription ObserveLifecycle(Action<RuntimeLifecycleEvent> observer,
        bool replayCurrent = true) => kernel.ObserveLifecycle(this, observer, replayCurrent);
}

public sealed partial class RuntimeKernel
{
    public const int MaximumLifecycleObservers = 256;
    /// <summary>What one module may hold. The budget that matters is the runtime-wide one above: it is what stops
    /// a module from starving the others. This per-module number only stops a single module from taking the whole
    /// runtime's worth by itself, so it is a fraction of that total and not a statement about how many halves a
    /// module is allowed to have. It was 8, which the map provider alone reached: that provider subscribes once
    /// per half it attaches — the object table, the clock, the terminal logs, the agent modifiers, the identity,
    /// the lives, the encounter state, the action lock — and every one of those is a real observer that must be
    /// removed on its own when its half detaches. Folding them into one subscription inside the module would only
    /// rebuild this dispatcher one level down, so the number is the thing that was wrong.</summary>
    public const int MaximumLifecycleObserversPerModule = 32;
    /// <summary>How many times one observer may throw before it is removed. A single transient error (a native object
    /// already destroyed at a level change) must not silence a package's half for the rest of the session; an observer
    /// that keeps failing is still removed, so it cannot fail on every tick forever.</summary>
    public const int MaximumLifecycleObserverFaults = 8;
    /// <summary>One subscription. <see cref="Active"/> is cleared when it is removed, so the dispatch loop, which
    /// walks an array captured before a callback may dispose a subscription, reads one field instead of asking the
    /// table again. A module that unregisters removes its observers here too, so an active observer's module is
    /// always registered.</summary>
    internal sealed class LifecycleObserver
    {
        internal LifecycleObserver(long id, string provider, long generation, Action<RuntimeLifecycleEvent> callback)
        { Id = id; Provider = provider; Generation = generation; Callback = callback; }
        internal long Id { get; }
        internal string Provider { get; }
        internal long Generation { get; }
        internal Action<RuntimeLifecycleEvent> Callback { get; }
        internal bool Active = true;
        internal int Faults;
    }
    private readonly Dictionary<long, LifecycleObserver> lifecycleObservers = new();
    private LifecycleObserver[]? lifecycleOrder;
    private long lifecycleObserverSequence;
    internal bool notifyingLifecycle;
    public RuntimeStartupState StartupState { get => world.StartupState; private set => world.StartupState = value; }
    public long LifecycleFaultCount { get; private set; }
    public RuntimeLifecycleFault? LastLifecycleFault { get; private set; }
    /// <summary>Hooks read this once per frame. The snapshot is a value built from four fields, so a read allocates
    /// nothing and can never be stale.</summary>
    public RuntimeLifecycleSnapshot Lifecycle
    {
        get
        {
            ReadThread();
            return new(StartupState, WorldEpoch, CurrentTick, world.worldHost);
        }
    }
    public bool IsRegistrationOpen => StartupState == RuntimeStartupState.Registering;
    // Host fault admission is separate from startup and host migration. Migration keeps its existing buffered
    // arrival path; a bridge fault cannot reach any publication queue or ledger, even before deferred cleanup.
    private string? factAdmissionClosure;
    internal void SetFactAdmissionSuspended(bool suspended)
    {
        ReadThread();
        if (suspended) { if (factAdmissionClosure != "runtime-stopped") factAdmissionClosure = "runtime-suspended"; }
        else if (factAdmissionClosure == "runtime-suspended")
        {
            factAdmissionClosure = null;
            FlushDiagnosticSummaries();
        }
    }
    internal void StopFactAdmission() { ReadThread(); factAdmissionClosure = "runtime-stopped"; }
    /// <inheritdoc cref="IForgeEngine.FactAdmissionClosure" />
    public string? FactAdmissionClosure
    {
        get
        {
            ReadThread();
            return StartupState == RuntimeStartupState.Stopped ? "runtime-stopped"
                : StartupState == RuntimeStartupState.Failed ? "runtime-suspended" : factAdmissionClosure;
        }
    }

    internal void ReadThread()
        => RuntimeJson.Require(Environment.CurrentManagedThreadId == world.threadId, "wrong-thread",
            "Runtime APIs must run on the owning simulation thread.");
    private void NoLifecycleObserverMutation()
    {
        NoEntityObservationMutation();
        RuntimeJson.Require(!notifyingLifecycle, "lifecycle-observer-mutation",
            "Lifecycle observers are read-only; only their subscription may be disposed.");
    }
    internal void NoLifecycleMutation()
    {
        NoLifecycleObserverMutation();
        RuntimeJson.Require(!notifyingTriggers, "trigger-observer-mutation",
            "Trigger observers are read-only; only their subscription may be disposed.");
        RuntimeJson.Require(!notifyingBehaviorTrace, "behavior-trace-observer-mutation",
            "Behavior trace observers are read-only; only their subscription may be disposed.");
    }
    private void RequireRuntimeReady(bool allowStarting)
        => RuntimeJson.Require(StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            && (allowStarting || StartupState != RuntimeStartupState.Starting),
            "runtime-not-ready", "Runtime startup failed, stopped or is still initializing.");
    private void AcceptRuntimeWork(bool allowStarting = false)
    {
        NoLifecycleMutation();
        RequireRuntimeReady(allowStarting);
    }
    /// <summary>Admission for a pure entity query. A behavior trace observer is read-only but may ask whether an entity is
    /// current or which entity a native object is; lifecycle observers, entity resolvers and a stopped runtime stay refused.</summary>
    private void AcceptRuntimeQuery()
    {
        NoLifecycleObserverMutation();
        RequireRuntimeReady(false);
    }

    /// <summary>Freezes registration before initialization. Failure is latched until a new host instance.</summary>
    internal bool StartRuntime(Action initialization)
    {
        Mutable(); ArgumentNullException.ThrowIfNull(initialization);
        if (StartupState != RuntimeStartupState.Registering) return false;
        StartupState = RuntimeStartupState.Starting;
        NotifyLifecycle(RuntimeLifecycleKind.StartupChanged);
        try { initialization(); StartupState = RuntimeStartupState.Ready; }
        catch (Exception error)
        {
            // The failed state silences the host's later suspension record, so this is the only place the cause is kept.
            StartupState = RuntimeStartupState.Failed; ClearLifecycleWork("startup-failed");
            log.LogSuspended("startup-failed", error.GetType().FullName + ": " + error.Message
                + (error.GetBaseException() is { } root && root != error ? " <- " + root.GetType().FullName + ": " + root.Message : "")
                + " @ " + string.Join(" | ", (error.GetBaseException().StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4)));
            NotifyLifecycle(RuntimeLifecycleKind.StartupChanged); throw;
        }
        NotifyLifecycle(RuntimeLifecycleKind.StartupChanged);
        return true;
    }

    internal void StopRuntime()
    {
        Mutable();
        RuntimeJson.Require(StartupState != RuntimeStartupState.Starting, "startup-in-progress",
            "Runtime cannot stop inside its initialization callback.");
        if (StartupState == RuntimeStartupState.Stopped) { FlushDiagnosticSummaries(); return; }
        StopFactAdmission();
        StartupState = RuntimeStartupState.Stopped;
        ClearLifecycleWork("runtime-stopped");
        NotifyLifecycle(RuntimeLifecycleKind.StartupChanged);
        foreach (var observer in lifecycleObservers.Values) observer.Active = false;
        lifecycleObservers.Clear();
        lifecycleOrder = null;
        ClearBehaviorTraceObservers();
        ClearTriggerObservers();
        FlushDiagnosticSummaries();
        log.SessionEnded = true;
    }
    private void ClearLifecycleWork(string code)
    {
        ClearContentConsumers();
        ClearSounds(broadcast: false);
        kernelFlow.StopScheduledSource(null, null, code); kernelVariables.StopStateSource(null, null, code);
        kernelEffects.StopOwnedHandleSource(null, null, code);
        EndDeferredResults(null, code);
        eventQueue.queue.Clear(); plans.Loaded.Clear(); plans.Subscriptions.Clear();
        // The gates carry the mounted entries; with none left they answer so.
        RefreshSubscriptionGates();
    }
    internal RuntimeLifecycleSubscription ObserveLifecycle(RuntimeModuleHandle owner,
        Action<RuntimeLifecycleEvent> callback, bool replayCurrent)
    {
        Mutable(); AcceptRuntimeWork(true); ArgumentNullException.ThrowIfNull(callback);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        int held = lifecycleObservers.Values.Count(o => o.Provider == owner.ProviderId);
        // The refusal names which of the two budgets was reached and what the caller already holds: a module that
        // subscribed once per half cannot tell those apart from the message alone, and that is the only thing
        // this refusal is read for.
        if (!(lifecycleObservers.Count < MaximumLifecycleObservers
            && held < MaximumLifecycleObserversPerModule)) throw new RuntimeContractException("lifecycle-observer-budget", "Lifecycle observer capacity reached: " + owner.ProviderId + " holds "
            + held + " of " + MaximumLifecycleObserversPerModule + ", runtime holds " + lifecycleObservers.Count
            + " of " + MaximumLifecycleObservers + ".");
        var id = checked(++lifecycleObserverSequence);
        var observer = new LifecycleObserver(id, owner.ProviderId, owner.Generation, callback);
        lifecycleObservers.Add(id, observer);
        lifecycleOrder = null;
        if (replayCurrent)
        {
            notifyingLifecycle = true;
            try { InvokeLifecycle(observer, new(RuntimeLifecycleKind.Snapshot, Lifecycle)); }
            finally { notifyingLifecycle = false; }
        }
        return new RuntimeLifecycleSubscription(this, id);
    }
    internal bool IsLifecycleObserverActive(long id)
    { ReadThread(); return lifecycleObservers.ContainsKey(id); }
    internal void RemoveLifecycleObserver(long id)
    {
        ReadThread(); NoEntityObservationMutation();
        if (!lifecycleObservers.Remove(id, out var observer)) return;
        observer.Active = false; lifecycleOrder = null;
    }
    private void RemoveLifecycleObservers(string provider, long moduleGeneration)
    {
        foreach (var observer in lifecycleObservers.Values
            .Where(o => o.Provider == provider && o.Generation == moduleGeneration).ToArray())
        { lifecycleObservers.Remove(observer.Id); observer.Active = false; }
        lifecycleOrder = null;
    }
    /// <summary>
    /// The dispatch list is one array ordered by subscription id, rebuilt only when the observer set changes. The
    /// loop still reads each observer's own <see cref="LifecycleObserver.Active"/> flag, because a callback may
    /// remove observers — its own or another's — while the loop walks the array it captured. The notification is a
    /// value, so a tick with any number of observers allocates nothing here.
    /// </summary>
    private void NotifyLifecycle(RuntimeLifecycleKind kind, long? previousWorldEpoch = null, bool isMigration = false)
    {
        if (lifecycleObservers.Count == 0) return;
        var value = new RuntimeLifecycleEvent(kind, Lifecycle, previousWorldEpoch, isMigration);
        var order = lifecycleOrder ??= lifecycleObservers.Values.OrderBy(o => o.Id).ToArray();
        notifyingLifecycle = true;
        try
        {
            foreach (var observer in order)
                if (observer.Active) InvokeLifecycle(observer, value);
        }
        finally { notifyingLifecycle = false; }
    }
    private void InvokeLifecycle(LifecycleObserver observer, RuntimeLifecycleEvent value)
    {
        try { observer.Callback(value); }
        catch (Exception error)
        {
            if (++observer.Faults >= MaximumLifecycleObserverFaults)
            {
                lifecycleObservers.Remove(observer.Id);
                observer.Active = false;
                lifecycleOrder = null;
            }
            if (LifecycleFaultCount < long.MaxValue) LifecycleFaultCount++;
            LastLifecycleFault = new(observer.Provider, RuntimeLogReasonCodes.LifecycleObserverFailed,
                CommandResult.TruncateDetail(error.GetType().Name + ": " + error.Message));
            log.LogObserverFailed(observer.Provider, error.Message);
        }
    }

    /// <summary>One host tick: the dispatch, then the lifecycle notification of the tick it moved to. The two halves
    /// are measured apart (<see cref="HookSection.TickAdvance"/>, <see cref="HookSection.TickLifecycle"/>) while the
    /// author layer has the counters on.</summary>
    internal TickResult Advance(long simulationTick, bool isHost, bool receipts = true)
    {
        Thread();
        var previousTick = CurrentTick;
        TickResult result;
        using (HookCounters.Measure(HookSection.TickAdvance)) result = AdvanceCore(simulationTick, isHost, receipts);
        if (CurrentTick != previousTick)
            using (HookCounters.Measure(HookSection.TickLifecycle)) NotifyLifecycle(RuntimeLifecycleKind.TickAdvanced);
        return result;
    }
}

using System;

namespace ForgeEngine.Framework;

public enum RuntimeStartupState { Registering, Starting, Ready, Failed, Stopped }

/// <summary>The boundaries an observer is told about. <c>Snapshot</c> is the state a subscription starts from,
/// <c>StartupChanged</c> a move of the startup state, <c>WorldChanged</c> a new world epoch, <c>TickAdvanced</c>
/// one advance that moved the simulation tick, and <c>CheckpointRestored</c> a save put back over the running
/// world: the same expedition and the same epoch, with the level's own objects rebuilt from the game's data.</summary>
public enum RuntimeLifecycleKind { Snapshot, StartupChanged, WorldChanged, TickAdvanced, CheckpointRestored }

/// <summary>Managed observation only; no Unity objects, wall clock or gameplay authority grant. A value, so a read
/// and a tick's notification allocate nothing.</summary>
public readonly record struct RuntimeLifecycleSnapshot(RuntimeStartupState StartupState,
    long WorldEpoch, long SimulationTick, bool? IsHost)
{
    /// <summary>Whether startup completed and this peer is the authoritative host.</summary>
    public bool IsReadyHost => StartupState == RuntimeStartupState.Ready && IsHost == true;
}

/// <summary>One notification, handed to every observer by value. <c>IsMigration</c> is true on the <c>WorldChanged</c>
/// of a host migration: the same level and the same objects continue, only the epoch moved from
/// <c>PreviousWorldEpoch</c> to <c>Current.WorldEpoch</c>. A provider that owns native objects follows the epoch
/// and keeps its tables instead of treating the event as a new world.</summary>
public readonly record struct RuntimeLifecycleEvent(RuntimeLifecycleKind Kind,
    RuntimeLifecycleSnapshot Current, long? PreviousWorldEpoch = null, bool IsMigration = false);

public sealed record RuntimeLifecycleFault(string ProviderId, string Code, string Detail);

public sealed class RuntimeLifecycleSubscription : IDisposable
{
    private readonly RuntimeKernel kernel;
    internal RuntimeLifecycleSubscription(RuntimeKernel kernel, long id)
    { this.kernel = kernel; Id = id; }
    internal long Id { get; }
    public bool IsActive => kernel.IsLifecycleObserverActive(Id);
    public void Dispose() => kernel.RemoveLifecycleObserver(Id);
}

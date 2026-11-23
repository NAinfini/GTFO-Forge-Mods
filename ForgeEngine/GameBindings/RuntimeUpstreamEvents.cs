using System;
using System.Linq;

namespace ForgeEngine.GameBindings;

/// <summary><c>HostMigrated</c> is published after Runtime has adopted the replacement host's world (the postfix of
/// <c>SNet_SyncManager.OnFoundNewMasterDuringMigration</c>), under the new world epoch. <c>FactoryDone</c> is GTFO-API's <c>LevelAPI.OnFactoryDone</c>, the postfix of <c>LG_Factory.FactoryDone</c>: the
/// generated floor is complete. It is distinct from <c>BuildDone</c>, which is the postfix of <c>Builder.BuildDone</c>.</summary>
public enum RuntimeUpstreamKind { BuildStarted, BuildDone, LevelEntered, LevelCleanup, CheckpointReached, CheckpointReloaded, GameDataReady, FactoryDone, HostMigrated }
public readonly record struct RuntimeUpstreamEvent(RuntimeUpstreamKind Kind, long WorldEpoch);

/// <summary>Read-only GTFO-API observations after Runtime has applied its lifecycle boundary.</summary>
public static class RuntimeUpstreamEvents
{
    private sealed record Observer(RuntimeUpstreamKind Kind, RuntimeWorkLease Lease, Action<RuntimeUpstreamEvent> Callback);
    private static Observer[] _observers = Array.Empty<Observer>();
    internal static bool DataReady;
    public static RuntimeWorkLease Subscribe(RuntimeUpstreamKind kind, string consumer, long worldEpoch,
        Action<RuntimeUpstreamEvent> callback, bool replayCurrent = true)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!Enum.IsDefined(typeof(RuntimeUpstreamKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(consumer) || consumer.Length > 128) throw new ArgumentException("upstream-consumer-invalid");
        if (worldEpoch < 0 || GameRuntimeBridge.Kernel?.WorldEpoch != worldEpoch) throw new InvalidOperationException("upstream-world-epoch-stale");
        _observers = _observers.Where(observer => observer.Lease.Active(worldEpoch)).ToArray();
        if (_observers.Any(observer => observer.Kind == kind && observer.Lease.Consumer == consumer)) throw new InvalidOperationException("upstream-consumer-duplicate");
        if (_observers.Length >= 256) throw new InvalidOperationException("upstream-subscriber-budget");
        var lease = new RuntimeWorkLease(consumer, worldEpoch);
        lease.Release = () => _observers = _observers.Where(item => !ReferenceEquals(item.Lease, lease)).ToArray();
        var observer = new Observer(kind, lease, callback);
        _observers = _observers.Append(observer).ToArray();
        if (replayCurrent && DataReady && kind == RuntimeUpstreamKind.GameDataReady)
        {
            try { GameRuntimeBridge.Kernel!.RunNativeObservation(() => Invoke(observer, new(kind, worldEpoch))); }
            catch { lease.Dispose(); throw; }
        }
        return lease;
    }
    internal static void Publish(RuntimeUpstreamKind kind)
    {
        if (kind == RuntimeUpstreamKind.GameDataReady) DataReady = true;
        var kernel = GameRuntimeBridge.Kernel;
        if (kernel == null) return;
        var value = new RuntimeUpstreamEvent(kind, kernel.WorldEpoch);
        kernel.RunNativeObservation(() =>
        {
            foreach (var observer in _observers)
                if (observer.Kind == kind && observer.Lease.Active(value.WorldEpoch)) Invoke(observer, value);
        });
    }
    private static void Invoke(Observer observer, RuntimeUpstreamEvent value)
    {
        try { observer.Callback(value); }
        catch (Exception error)
        { observer.Lease.End("failed"); GameRuntimeBridge.ReportNativeObserverFailure(observer.Lease.Consumer, error); }
    }
    internal static void Stop()
    {
        foreach (var observer in _observers) observer.Lease.Dispose();
        _observers = Array.Empty<Observer>(); DataReady = false;
    }
}

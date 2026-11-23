using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap;

internal sealed partial class LevelObjectModule
{
    public static bool IsCapturedInstance(EntityReference reference)
        => reference.Id.StartsWith(EntityKinds.LevelObject + ":container/network/", StringComparison.Ordinal)
            || reference.Id.StartsWith(EntityKinds.LevelObject + ":item/network/", StringComparison.Ordinal);
    private sealed class Instance
    {
        internal Instance(EntityReference reference, object native, Func<object, bool> live, long identity,
            Func<object, EntityReference, RuntimeEntitySnapshot?>? observe)
        { Reference = reference; Native = native; Live = live; Identity = identity; Observe = observe; }
        internal readonly EntityReference Reference;
        internal readonly object Native;
        internal readonly Func<object, bool> Live;
        internal readonly long Identity;
        internal readonly Func<object, EntityReference, RuntimeEntitySnapshot?>? Observe;
        internal bool Invalidated;
        internal Action? Cleanup;
        internal Func<object, bool>? PresentationLive;
    }

    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private readonly List<Instance> _presentations = new();
    private readonly RuntimeLifecycleSubscription _instanceLifecycle;

    /// <summary>An observed native network identity, never an address an author can invent. A reused native key
    /// gets a new life epoch. The provider keeps the native object and its current-lifetime reader together.</summary>
    public EntityReference? CaptureInstance(long worldEpoch, string key, object native, Func<object, bool> live,
        long nativeIdentity = 0, Func<object, EntityReference, RuntimeEntitySnapshot?>? observe = null)
    {
        CheckThread();
        if (_disposed || worldEpoch != WorldEpoch || !live(native)
            || !(key.StartsWith("container/network/", StringComparison.Ordinal)
                || key.StartsWith("item/network/", StringComparison.Ordinal))) return null;
        string id = EntityKinds.LevelObject + ":" + key;
        long life = 1;
        if (_instances.TryGetValue(id, out var previous))
        {
            if (!previous.Invalidated && (ReferenceEquals(previous.Native, native)
                || nativeIdentity != 0 && previous.Identity == nativeIdentity)) return previous.Reference;
            life = checked(previous.Reference.LifeEpoch + 1);
            CleanupInstance(previous);
        }
        else if (_instances.Count >= RuntimeKernel.MaximumEventHistory)
        {
            ReportOnce("instance-budget", "map.instance-refused: instance-budget");
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.InstanceRefused, "Native instance capture refused.",
                RuntimeLogValue.Of("reason", "instance-budget"));
            return null;
        }
        var reference = new EntityReference(id, worldEpoch, life);
        _instances[id] = new Instance(reference, native, live, nativeIdentity, observe);
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.InstanceCaptured, "Native instance captured.",
            RuntimeLogValue.Of("entity", id), RuntimeLogValue.Of("life", life));
        return reference;
    }

    public object? NativeInstance(EntityReference reference)
    {
        CheckThread();
        if (_disposed || reference.WorldEpoch != WorldEpoch || !_instances.TryGetValue(reference.Id, out var item)
            || item.Reference != reference || item.Invalidated) return null;
        bool current;
        try { current = item.Live(item.Native); }
        catch (Exception containedError) { MapLog.Contained("level-object-instances.native-instance", containedError); current = false; }
        if (current) return item.Native;
        item.Invalidated = true;
        CleanupInstance(item);
        return null;
    }

    public RuntimeEntitySnapshot? ObserveInstance(EntityReference reference)
        => NativeInstance(reference) is { } native && _instances[reference.Id].Observe is { } observe
            ? observe(native, reference) : null;

    public void InvalidateInstance(EntityReference reference)
    {
        CheckThread();
        if (reference.WorldEpoch != WorldEpoch || !_instances.TryGetValue(reference.Id, out var item)
            || item.Reference != reference) return;
        item.Invalidated = true;
        CleanupInstance(item);
    }

    public void InvalidateNativeInstance(long worldEpoch, long nativeIdentity)
    {
        CheckThread();
        if (worldEpoch != WorldEpoch || nativeIdentity == 0) return;
        // Despawn may follow native key unregistration. Match the callback's exact local object in this
        // same binding ledger, independent of whatever free/reused key the native registry now reports.
        foreach (var item in _instances.Values)
            if (item.Identity == nativeIdentity) InvalidateInstance(item.Reference);
    }

    /// <summary>A local native presentation owned by this same observed instance. Replacing or losing the
    /// instance removes it; this is cleanup of a GUI object, not a second gameplay state or scheduler.</summary>
    public bool OwnInstancePresentation(EntityReference reference, Action remove, Func<object, bool>? presentationLive = null)
    {
        CheckThread();
        if (NativeInstance(reference) == null) return false;
        var item = _instances[reference.Id];
        CleanupInstance(item);
        item.Cleanup = remove;
        item.PresentationLive = presentationLive;
        _presentations.Add(item);
        return true;
    }

    public void RemoveInstancePresentation(EntityReference reference)
    {
        CheckThread();
        if (reference.WorldEpoch == WorldEpoch && _instances.TryGetValue(reference.Id, out var item)
            && item.Reference == reference) CleanupInstance(item);
    }

    internal bool HasInstancePresentations => _presentations.Count != 0;

    /// <summary>The native GUI update only releases presentations whose observed receiver no longer exists.</summary>
    public void SweepInstancePresentations()
    {
        CheckThread();
        for (int i = _presentations.Count - 1; i >= 0; i--)
        {
            var item = _presentations[i];
            bool live;
            try { live = !item.Invalidated && item.Live(item.Native); }
            catch (Exception containedError) { MapLog.Contained("level-object-instances.sweep-instance-presentations", containedError); live = false; }
            if (!live) item.Invalidated = true;
            else
            {
                try { live = item.PresentationLive?.Invoke(item.Native) ?? true; }
                catch (Exception containedError) { MapLog.Contained("level-object-instances.sweep-instance-presentations", containedError); live = false; }
            }
            if (live) continue;
            CleanupInstance(item);
        }
    }

    private void CleanupInstance(Instance item)
    {
        var cleanup = item.Cleanup;
        item.Cleanup = null;
        item.PresentationLive = null;
        if (cleanup == null) return;
        _presentations.Remove(item);
        try { cleanup(); }
        catch (Exception error)
        {
            ReportOnce("instance-cleanup", "map.instance-refused: instance-cleanup-failed: " + error.GetType().Name);
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.InstanceRefused, "Native presentation cleanup failed.",
                RuntimeLogValue.Of("reason", "instance-cleanup-failed"), RuntimeLogValue.Of("exception", error.GetType().Name));
        }
    }

    private void ClearInstances()
    {
        foreach (var item in _instances.Values) CleanupInstance(item);
        _instances.Clear();
        _presentations.Clear();
    }
}

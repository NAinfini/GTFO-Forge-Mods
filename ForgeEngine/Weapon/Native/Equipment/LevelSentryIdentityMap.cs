using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon.Native;

/// <summary>The producer's exact author address and spawned item, never a match by owner, gear or position.
/// Only the initial host can establish this association. Replica recovery needs a proven replicated association;
/// until then clients, checkpoint recovery and promotion of a client explicitly refuse it.</summary>
internal sealed class LevelSentryIdentityMap
{
    private sealed record Binding(string Address, EntityReference Entity, SentryGunInstance Instance, IntPtr Pointer);
    private readonly Dictionary<string, Binding> _addresses = new(StringComparer.Ordinal);
    private readonly Dictionary<IntPtr, Binding> _instances = new();
    private readonly Dictionary<string, Binding> _entities = new(StringComparer.Ordinal);
    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _attached;
    private readonly Action<EntityReference> _release;
    private long _world = -1, _sequence;
    private bool? _previousHost;
    private bool _replicaObserved;
    private string? _refusal;

    internal LevelSentryIdentityMap(IForgeEngine kernel, Func<bool> attached, Action<EntityReference> release)
    { _kernel = kernel; _attached = attached; _release = release; }

    internal EntityReference Bind(string address, long world, object? spawned)
    {
        var state = Refresh();
        Check(world == state.WorldEpoch, "level-sentry-stale-world");
        RequireReady(state);
        Check(AddressValid(address), "level-sentry-invalid-address");
        Check(spawned != null, "level-sentry-null-instance");
        Check(spawned is ISyncedItem, "level-sentry-wrong-type");
        Item? item;
        try { item = ((ISyncedItem)spawned!).GetItem(); }
        catch (Exception error) { throw new RuntimeContractException("level-sentry-instance-unreadable", error.GetType().Name); }
        Check(item != null, "level-sentry-null-instance");
        var instance = item!.TryCast<SentryGunInstance>();
        Check(instance != null && instance.Pointer != IntPtr.Zero, "level-sentry-wrong-type");
        var pointer = instance!.Pointer;
        if (_addresses.TryGetValue(address, out var atAddress))
        {
            if (!Live(atAddress)) Remove(atAddress);
            else throw new RuntimeContractException(atAddress.Pointer == pointer
                ? "level-sentry-duplicate-binding" : "level-sentry-address-conflict", address);
        }
        if (_instances.TryGetValue(pointer, out var atInstance))
        {
            if (!Live(atInstance)) Remove(atInstance);
            else throw new RuntimeContractException("level-sentry-instance-conflict", address);
        }
        // A callback from an old spawn cannot be restamped into the world a read happened to observe.
        Check(_kernel.WorldEpoch == world, "level-sentry-stale-world");
        var sequence = checked(++_sequence);
        var entity = new EntityReference(EquipmentIdentityId.LevelSentryInstance(world, sequence), world, sequence);
        var binding = new Binding(address, entity, instance, pointer);
        _addresses.Add(address, binding); _instances.Add(pointer, binding); _entities.Add(entity.Id, binding);
        return entity;
    }

    internal bool Matches(string address, EntityReference subject)
    {
        var state = Refresh();
        Check(subject != null && subject.WorldEpoch == state.WorldEpoch, "level-sentry-stale-world");
        RequireReady(state);
        if (!_addresses.TryGetValue(address, out var binding))
            throw new RuntimeContractException("level-sentry-unbound", address);
        if (!Live(binding))
        {
            Remove(binding, false);
            throw new RuntimeContractException("level-sentry-unbound", address);
        }
        return binding.Entity == subject;
    }

    internal EntityReference? EntityOf(IntPtr pointer)
    {
        var state = Refresh();
        if (!Ready(state) || !_instances.TryGetValue(pointer, out var binding)) return null;
        if (Live(binding)) return binding.Entity;
        Remove(binding, false); return null;
    }

    internal SentryGunInstance? ItemOf(EntityReference entity)
    {
        var state = Refresh();
        if (!Ready(state) || entity == null || entity.WorldEpoch != state.WorldEpoch
            || !_entities.TryGetValue(entity.Id, out var binding) || binding.Entity != entity) return null;
        if (Live(binding)) return binding.Instance;
        Remove(binding, false); return null;
    }

    internal bool Holds(EntityReference entity)
    {
        var state = Refresh();
        if (!Ready(state) || entity == null || entity.WorldEpoch != state.WorldEpoch) return false;
        if (ItemOf(entity) != null) return true;
        // Runtime claimed the mount and captured its exact subject while this life was still valid. Only the
        // ending event being consumed may read that fact after removal; native access still uses ItemOf, and
        // outside this dispatch a retired identity has no event row and remains refused.
        var ending = _kernel.EventRow(0);
        return ending != null
            && ending.EventType is ModuleDefinition.RecallCompletedBinding or ModuleDefinition.DespawnedBinding
            && ending.WorldEpoch == entity.WorldEpoch && ending.LifeEpoch == entity.LifeEpoch
            && ending.TargetRefs.Count == 1 && ending.TargetRefs[0] == entity;
    }

    internal void End(IntPtr pointer)
    {
        if (_instances.TryGetValue(pointer, out var binding)) Remove(binding);
    }

    internal void ObserveLifecycle(RuntimeLifecycleEvent value)
    {
        Observe(value.Current);
        if (value.Kind == RuntimeLifecycleKind.CheckpointRestored)
        { Clear(false); _refusal = "level-sentry-checkpoint-unbound"; }
    }

    internal void Clear() => Clear(true);

    // Lifecycle callbacks and entity probes may drop native associations, but cannot mutate kernel variables.
    private void Clear(bool release)
    {
        if (_addresses.Count == 0) return;
        var ended = release ? new List<Binding>(_addresses.Values) : null;
        _addresses.Clear(); _instances.Clear(); _entities.Clear();
        if (ended != null) foreach (var binding in ended) _release(binding.Entity);
        // The sequence never rewinds within a world, even if the native pointer is reused.
    }

    private RuntimeLifecycleSnapshot Refresh()
    {
        var state = _kernel.Lifecycle;
        Observe(state); return state;
    }

    private void Observe(RuntimeLifecycleSnapshot state)
    {
        if (_world != state.WorldEpoch)
        {
            Clear(false); _world = state.WorldEpoch; _sequence = 0; _previousHost = null;
            _refusal = _replicaObserved ? "level-sentry-client-unbound" : null;
        }
        if (state.IsHost == false)
        { _replicaObserved = true; Clear(false); _refusal ??= "level-sentry-client-unbound"; }
        else if (state.IsHost == true && (_previousHost == false || _replicaObserved))
        { Clear(false); _refusal = "level-sentry-migration-unbound"; }
        else if (state.IsHost != true && _previousHost == true)
        { Clear(false); _refusal = "level-sentry-authority-lost-unbound"; }
        if (state.IsHost.HasValue) _previousHost = state.IsHost;
        if (state.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) Clear(false);
    }

    private bool Ready(RuntimeLifecycleSnapshot state) => _attached() && state.IsReadyHost && _refusal == null;
    private void RequireReady(RuntimeLifecycleSnapshot state)
    {
        Check(_attached(), "level-sentry-provider-detached");
        Check(_refusal == null, _refusal ?? "level-sentry-unbound");
        Check(state.IsReadyHost, "level-sentry-not-ready");
    }
    private static bool Live(Binding binding)
        => binding.Instance != null && binding.Instance.Pointer == binding.Pointer;
    private void Remove(Binding binding, bool release = true)
    {
        _addresses.Remove(binding.Address); _instances.Remove(binding.Pointer); _entities.Remove(binding.Entity.Id);
        if (release) _release(binding.Entity);
    }

    /// <summary>A level sentry's address, in the one grammar the Map domain writes it with.</summary>
    private static bool AddressValid(string? address)
        => ForgeMap.MapObjectHostAddress.TryParse(address) is { Category: ForgeMap.MapObjectCategories.Sentry };
    private static void Check(bool condition, string code)
    { if (!condition) throw new RuntimeContractException(code, code); }
}

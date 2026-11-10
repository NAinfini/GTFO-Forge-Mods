using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using WindVolume;
using AffectorSet = Il2CppSystem.Collections.Generic.HashSet<WindVolume.WindVolumeAffector>;

namespace ForgePerformance.LoadShedding;

// No Update component. Called only at the vanilla UpdateVolume boundary.
internal sealed class WindCapacityGuard
{
    internal const int RefreshBudget = 64;
    private readonly int _limit;
    private readonly Dictionary<IntPtr, CameraEntry> _cameras = new();
    private readonly Scope[] _scopes = { new(), new(), new(), new() };
    private Il2CppReferenceArray<WindVolumeAffector> _snapshot = new(SheddingPolicy.InitialAffectorCapacity);
    private Entry[] _entries = new Entry[SheddingPolicy.InitialAffectorCapacity];
    private readonly Dictionary<IntPtr, int> _indices = new(SheddingPolicy.InitialAffectorCapacity);
    // Keep Transform wrappers across enable/disable; release them with the world.
    private readonly Dictionary<IntPtr, Transform> _transforms = new(SheddingPolicy.InitialAffectorCapacity);
    private AffectorSet? _registry;
    private AffectorSet? _currentRegistry;
    private int _count, _snapshotVersion;
    internal long Calls, SnapshotCopies, DistanceReads, SelectionWrites, Refused, Restored;
    internal int Peak;
    internal int Limit => _limit;

    internal WindCapacityGuard(SheddingSettings settings)
    {
        _limit = settings.WindNearest ? settings.WindLimit : SheddingPolicy.WindCapacity;
        if (_limit < 1 || _limit > SheddingPolicy.WindCapacity) throw new ArgumentOutOfRangeException(nameof(settings));
    }

    internal void Register(WindVolumeAffector affector)
    {
        // OnEnable ran against the temporary view. Keep canonical sets in sync.
        foreach (var scope in _scopes)
            if (scope.Instances != null) scope.Instances.Add(affector);
        if (!OwnsCurrentRegistry()) return;
        if (_indices.TryGetValue(affector.Pointer, out var existing))
        {
            if (_entries[existing].Id != affector.GetInstanceID())
                _entries[existing] = CreateEntry(affector);
            return;
        }
        // A new population high-water mark is cold work at the next Prefix.
        // A previously unseen Transform is cold work; warmed lifecycle calls reuse it.
        if (_count == _entries.Length) return;
        _indices.Add(affector.Pointer, _count);
        _entries[_count++] = CreateEntry(affector);
    }

    internal void Unregister(WindVolumeAffector affector)
    {
        foreach (var scope in _scopes)
            if (scope.Instances != null) scope.Instances.Remove(affector);
        if (!OwnsCurrentRegistry() || !_indices.Remove(affector.Pointer, out var index)) return;
        var last = --_count;
        if (index != last)
        {
            _entries[index] = _entries[last];
            _indices[_entries[index].Affector.Pointer] = index;
        }
        _entries[last] = default;
    }

    private bool OwnsCurrentRegistry()
    {
        if (_registry == null) return false;
        if (_registry.Pointer == WindNativePointers.Registry()) return true;
        foreach (var scope in _scopes)
            if (scope.Instances?.Pointer == _registry.Pointer) return true;
        return false;
    }

    internal bool Prefix(WindVolumeCamera camera, out Scope? state)
    {
        state = null;
        if (camera == null)
        {
            if (!ReferenceEquals(camera, null)) _cameras.Remove(camera.Pointer);
            Refused++; return false;
        }
        var registryPointer = WindNativePointers.Registry();
        AffectorSet? instances = null;
        if (_currentRegistry?.Pointer == registryPointer) instances = _currentRegistry;
        else if (_registry?.Pointer == registryPointer) instances = _registry;
        foreach (var scope in _scopes)
            if (scope.Selection.Pointer == registryPointer) { instances = scope.Selection; break; }
        if (instances == null && registryPointer != IntPtr.Zero) instances = WindNativePointers.WrapRegistry(registryPointer);
        if (instances == null) { Refused++; return false; }
        _currentRegistry = instances;
        var count = instances.Count;
        // Within the vanilla capacity nothing is selected, so no camera view is built or checked.
        if (count <= _limit) return true;
        _cameras.TryGetValue(camera.Pointer, out var view);
        var cameraPointer = WindNativePointers.Camera(camera);
        var nativeCamera = view?.NativeCamera;
        if (nativeCamera?.Pointer != cameraPointer)
            nativeCamera = cameraPointer == IntPtr.Zero ? null : WindNativePointers.WrapCamera(camera, cameraPointer);
        if (nativeCamera == null)
        {
            _cameras.Remove(camera.Pointer); Refused++; return false;
        }
        var transformPointer = WindNativePointers.Transform(nativeCamera);
        var transform = view?.Transform;
        if (transform?.Pointer != transformPointer)
            transform = transformPointer == IntPtr.Zero ? null : WindNativePointers.WrapTransform(nativeCamera, transformPointer);
        if (transform == null) { _cameras.Remove(camera.Pointer); Refused++; return false; }
        if (view != null && !view.Matches(camera, nativeCamera, transform))
        { _cameras.Remove(camera.Pointer); view = null; }
        if (view == null)
        {
            view = new CameraEntry(camera, nativeCamera, transform);
            _cameras[camera.Pointer] = view;
        }
        Calls++;
        Peak = Math.Max(Peak, count);
        Scope? available = null;
        foreach (var scope in _scopes)
            if (scope.Instances == null) { available = scope; break; }
        if (available == null) { Refused++; return false; }
        // Unity wrappers can outlive destroyed native objects, and native addresses
        // can be recycled. Validate both object identities before using a cache.
        RefreshSnapshot(instances, count);
        if (view.Version != _snapshotVersion)
        {
            // Only cold population / registry replacement rebuilds. Steady calls never copy
            // the registry and read at most RefreshBudget transforms.
            view.Cursor = view.Length = 0;
            view.Position = view.Transform.position;
            Scan(view, _count);
            view.Version = _snapshotVersion;
        }
        else
        {
            view.Cursor = Math.Min(view.Cursor, _count);
            if (view.Cursor == 0) { view.Length = 0; view.Position = view.Transform.position; }
            // ponytail: bounded sweeps trade moving-nearest latency for fixed work;
            // exact moving-nearest needs a maintained spatial index.
            Scan(view, Math.Min(RefreshBudget, _count - view.Cursor));
        }
        // Reuse a tiny native view; never prune/re-add the canonical registry.
        available.Select(view.Selected, view.SelectedCount, this);
        state = available;
        state.Instances = instances;
        WindVolumeAffector.instances = state.Selection;
        return true;
    }

    private void RefreshSnapshot(AffectorSet instances, int count)
    {
        if (_registry?.Pointer == instances.Pointer && _count == count) return;
        EnsureCapacity(count);
        instances.CopyTo(_snapshot);
        SnapshotCopies++;
        _indices.Clear();
        for (var i = 0; i < count; i++)
        {
            var affector = _snapshot[i];
            if (affector == null) throw new InvalidOperationException("The WindVolume set holds a dead object.");
            _entries[i] = CreateEntry(affector);
            _indices.Add(affector.Pointer, i);
            _snapshot[i] = null!;
        }
        for (var i = count; i < _count; i++) { _entries[i] = default; _snapshot[i] = null!; }
        _registry = instances; _count = count;
        _snapshotVersion++;
    }

    private Entry CreateEntry(WindVolumeAffector affector)
    {
        var pointer = WindNativePointers.Transform(affector);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("The WindVolume object has no Transform.");
        if (!_transforms.TryGetValue(pointer, out var transform) || transform == null)
        {
            transform = WindNativePointers.WrapTransform(affector, pointer);
            _transforms[pointer] = transform;
        }
        return new Entry(affector, transform, affector.GetInstanceID());
    }

    private void EnsureCapacity(int count)
    {
        if (count <= _entries.Length) return;
        var size = Math.Max(count, _entries.Length * 2);
        Array.Resize(ref _entries, size);
        _snapshot = new(size);
        _indices.EnsureCapacity(size);
        _transforms.EnsureCapacity(size);
    }

    private bool IsMember(Entry entry) => entry.Affector != null && entry.Affector.isActiveAndEnabled
        && _indices.TryGetValue(entry.Affector.Pointer, out var index) && _entries[index].Id == entry.Id;

    private void Scan(CameraEntry view, int budget)
    {
        var end = view.Cursor + budget;
        for (; view.Cursor < end; view.Cursor++)
        {
            var entry = _entries[view.Cursor];
            if (!IsMember(entry)) continue;
            var distance = (entry.Transform.position - view.Position).sqrMagnitude;
            if (!float.IsFinite(distance)) distance = float.PositiveInfinity;
            DistanceReads++;
            // A remove/swap/add during a sweep can visit the same member twice.
            // Deduplicate only the bounded candidate list, never the registry.
            for (var i = 0; i < view.Length; i++)
                if (view.Candidates[i].Affector.Pointer == entry.Affector.Pointer)
                {
                    for (var j = i + 1; j < view.Length; j++)
                    { view.Candidates[j - 1] = view.Candidates[j]; view.Distances[j - 1] = view.Distances[j]; }
                    view.Length--; break;
                }
            var index = view.Length;
            while (index > 0 && (distance < view.Distances[index - 1]
                || distance == view.Distances[index - 1] && entry.Id < view.Candidates[index - 1].Id)) index--;
            if (index >= _limit) continue;
            var last = Math.Min(view.Length, _limit - 1);
            for (var i = last; i > index; i--)
            { view.Candidates[i] = view.Candidates[i - 1]; view.Distances[i] = view.Distances[i - 1]; }
            view.Candidates[index] = entry; view.Distances[index] = distance;
            view.Length = Math.Min(view.Length + 1, _limit);
        }
        if (view.Cursor != _count) return;
        view.SelectedCount = 0;
        for (var i = 0; i < view.Length; i++)
            if (IsMember(view.Candidates[i])) view.Selected[view.SelectedCount++] = view.Candidates[i];
        view.Cursor = 0;
    }

    internal Exception? Finalizer(Scope? state, Exception? original)
    {
        if (state?.Instances == null) return original;
        try
        {
            // A new world's registry takes precedence over our temporary view.
            if (WindNativePointers.Registry() == state.Selection.Pointer)
            {
                WindVolumeAffector.instances = state.Instances;
                Restored++;
            }
            state.Instances = null;
        }
        catch (Exception error)
        {
            Refused++;
            return original == null ? error : new AggregateException(original, error);
        }
        return original;
    }

    internal void Clear()
    {
        Exception? failure = null;
        for (var i = _scopes.Length - 1; i >= 0; i--) failure = Finalizer(_scopes[i], failure);
        if (failure != null) throw failure;
        _cameras.Clear(); _registry = _currentRegistry = null; _indices.Clear(); _snapshotVersion++;
        _transforms.Clear();
        Array.Clear(_entries, 0, _count);
        for (var i = 0; i < _count; i++) _snapshot[i] = null!;
        _count = 0;
        foreach (var scope in _scopes) scope.Reset();
    }

    internal sealed class Scope
    {
        internal AffectorSet? Instances;
        internal readonly AffectorSet Selection = new();
        private readonly IntPtr[] _selected = new IntPtr[SheddingPolicy.WindCapacity];
        private readonly Entry[] _valid = new Entry[SheddingPolicy.WindCapacity];
        private int _length;
        internal void Select(Entry[] entries, int count, WindCapacityGuard owner)
        {
            var valid = 0;
            for (var i = 0; i < count; i++)
                if (owner.IsMember(entries[i])) _valid[valid++] = entries[i];
            count = valid;
            entries = _valid;
            var same = count == _length && Selection.Count == count;
            for (var i = 0; same && i < count; i++) same = _selected[i] == entries[i].Affector.Pointer;
            if (same) return;
            Selection.Clear(); _length = count;
            for (var i = 0; i < count; i++)
            { Selection.Add(entries[i].Affector); _selected[i] = entries[i].Affector.Pointer; }
            owner.SelectionWrites++;
        }
        internal void Reset() { Selection.Clear(); _length = 0; }
    }

    internal readonly record struct Entry(WindVolumeAffector Affector, Transform Transform, int Id);
    private sealed class CameraEntry
    {
        private readonly WindVolumeCamera _owner;
        private readonly Camera _camera;
        internal Camera NativeCamera => _camera;
        private readonly int _ownerId, _cameraId, _transformId;
        internal readonly Transform Transform;
        internal readonly Entry[] Candidates = new Entry[SheddingPolicy.WindCapacity];
        internal readonly Entry[] Selected = new Entry[SheddingPolicy.WindCapacity];
        internal readonly float[] Distances = new float[SheddingPolicy.WindCapacity];
        internal int Cursor, Length, SelectedCount, Version = -1;
        internal Vector3 Position;
        internal CameraEntry(WindVolumeCamera owner, Camera camera, Transform transform)
        {
            _owner = owner; _camera = camera; Transform = transform;
            _ownerId = owner.GetInstanceID(); _cameraId = camera.GetInstanceID(); _transformId = transform.GetInstanceID();
        }
        internal bool Matches(WindVolumeCamera owner, Camera camera, Transform transform)
            => _owner != null && _camera != null && Transform != null
                && _owner == owner && _ownerId == owner.GetInstanceID()
                && _camera == camera && _cameraId == camera.GetInstanceID()
                && Transform == transform && _transformId == transform.GetInstanceID();
    }
}

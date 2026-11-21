using System;
using System.Collections.Generic;

namespace ForgeDevelopment.Native;

/// <summary>Lifecycle-maintained slots. Removals and spawns between frames do not invalidate an in-flight cursor;
/// a new object placed behind that cursor is observed on its next complete sweep.</summary>
internal sealed class CaptureObjectIndex<T> where T : class
{
    private sealed record Entry(int Id, T Value, string? Identity);
    private readonly Dictionary<int, int> _indices = new();
    private readonly Dictionary<string, int> _identities = new(StringComparer.Ordinal);
    private readonly List<Entry?> _slots = new();
    private readonly Stack<int> _free = new();
    private readonly Func<T, string>? _identity;
    private readonly Func<T, T, bool> _sameInstance;
    private int _generation;

    internal CaptureObjectIndex(Func<T, string>? identity = null, Func<T, T, bool>? sameInstance = null)
    { _identity = identity; _sameInstance = sameInstance ?? ReferenceEquals; }

    internal void Add(int id, T value)
    {
        var identity = _identity?.Invoke(value);
        // The latest lifecycle owns a reused diagnostic ID. Retire the old slot as well, so a cursor cannot emit
        // two lives under that same ID. A later despawn of the retired instance has no entry to remove.
        if (identity != null && _identities.TryGetValue(identity, out var owner) && _slots[owner]!.Id != id)
            RemoveSlot(owner);
        if (_indices.TryGetValue(id, out var existing)) RemoveSlot(existing);
        var index = _free.Count == 0 ? _slots.Count : _free.Pop();
        var entry = new Entry(id, value, identity);
        if (index == _slots.Count) _slots.Add(entry); else _slots[index] = entry;
        _indices.Add(id, index);
        if (identity != null) _identities.Add(identity, index);
    }
    internal void Remove(int id)
    { if (_indices.TryGetValue(id, out var index)) RemoveSlot(index); }

    internal void Remove(int id, T expected)
    {
        if (_indices.TryGetValue(id, out var index) && _sameInstance(_slots[index]!.Value, expected)) RemoveSlot(index);
    }
    private void RemoveSlot(int index)
    {
        var entry = _slots[index]!;
        _indices.Remove(entry.Id);
        if (entry.Identity != null) _identities.Remove(entry.Identity);
        _slots[index] = null; _free.Push(index);
    }
    internal bool TryGet(string identity, out T? value)
    {
        if (_identities.TryGetValue(identity, out var index)) { value = _slots[index]!.Value; return true; }
        value = null; return false;
    }
    internal IEnumerable<T?> Read()
    {
        var count = _slots.Count; var generation = _generation;
        for (var index = 0; index < count && generation == _generation; index++) yield return _slots[index]?.Value;
    }
    internal void Clear() { _generation++; _indices.Clear(); _identities.Clear(); _slots.Clear(); _free.Clear(); }
}

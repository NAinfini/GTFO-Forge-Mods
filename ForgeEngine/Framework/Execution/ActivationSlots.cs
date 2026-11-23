using System;
using System.Collections;
using System.Collections.Generic;

namespace ForgeEngine.Framework;

/// <summary>Dense compiled addresses with the existing receipt iteration order. Storage grows only at plan load.</summary>
internal sealed class ActivationSlots<T> : IEnumerable<KeyValuePair<int, T>>
{
    private int[] entries = Array.Empty<int>();
    private int[] keys = Array.Empty<int>();
    private T[] values = Array.Empty<T>();
    private int count;
    private int free = -1;

    internal void Reserve(int capacity)
    {
        if (capacity <= entries.Length) return;
        var previous = entries.Length;
        Array.Resize(ref entries, capacity);
        Array.Fill(entries, -1, previous, capacity - previous);
        Array.Resize(ref keys, capacity);
        Array.Resize(ref values, capacity);
    }

    internal T this[int key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
        set
        {
            var entry = entries[key];
            if (entry < 0)
            {
                if (free >= 0) { entry = free; free = -3 - keys[entry]; }
                else entry = count++;
                entries[key] = entry;
                keys[entry] = key;
            }
            values[entry] = value;
        }
    }

    internal bool ContainsKey(int key) => (uint)key < (uint)entries.Length && entries[key] >= 0;
    internal bool TryGetValue(int key, out T value)
    {
        if (ContainsKey(key)) { value = values[entries[key]]; return true; }
        value = default!;
        return false;
    }

    internal void Remove(int key)
    {
        if (!ContainsKey(key)) return;
        var entry = entries[key];
        entries[key] = -1;
        keys[entry] = -3 - free;
        free = entry;
        values[entry] = default!;
    }

    internal void Clear()
    {
        for (var i = 0; i < count; i++)
            if (keys[i] >= 0) entries[keys[i]] = -1;
        Array.Clear(values, 0, count);
        count = 0;
        free = -1;
    }

    internal IEnumerable<T> Values
    {
        get
        {
            for (var i = 0; i < count; i++) if (keys[i] >= 0) yield return values[i];
        }
    }

    public IEnumerator<KeyValuePair<int, T>> GetEnumerator()
    {
        for (var i = 0; i < count; i++)
            if (keys[i] >= 0) yield return new KeyValuePair<int, T>(keys[i], values[i]);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

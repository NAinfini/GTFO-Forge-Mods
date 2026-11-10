using System;

namespace ForgePerformance.LoadShedding;

// Which items of one native list a filter removes depends only on the list's
// content and on the set of eligible entries. The list's version counter moves
// whenever its content does, so a recorded answer stays valid until the list
// identity, version or count differs, or the owner drops the plan because the
// eligible set changed.
internal sealed class RemovalPlan<T> where T : class
{
    private T?[] _items = new T[16];
    private int[] _indices = new int[16];
    private IntPtr _list;
    private int _version, _count;
    private bool _valid;

    // Items and indices in the order the scan found them (descending index), so
    // removing them in that order never shifts a pending index.
    internal int Length { get; private set; }
    internal int IndexAt(int position) => _indices[position];
    internal T ItemAt(int position) => _items[position]!;

    internal bool Matches(IntPtr list, int version, int count)
        => _valid && _list == list && _version == version && _count == count;

    internal void Begin(IntPtr list, int version, int count)
    {
        Invalidate();
        _list = list; _version = version; _count = count;
    }

    internal void Add(int index, T item)
    {
        if (Length == _items.Length)
        {
            Array.Resize(ref _items, Length * 2);
            Array.Resize(ref _indices, Length * 2);
        }
        _items[Length] = item; _indices[Length] = index; Length++;
    }

    // The scan between Begin and Commit visited the whole list.
    internal void Commit() => _valid = true;

    internal void Invalidate()
    {
        Array.Clear(_items, 0, Length);
        Length = 0; _valid = false;
    }

    // Called once the removals were undone by reinserting every item. Each
    // removal and each reinsertion bumps the version by one, so an untouched
    // list ends exactly 2 * Length above the recorded version; anything else
    // means the native code changed the list meanwhile and the plan is dropped.
    internal void Reverted(int versionNow)
    {
        if (!_valid) return;
        if (versionNow == _version + 2 * Length) _version = versionNow;
        else Invalidate();
    }
}

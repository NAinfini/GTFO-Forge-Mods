using System;

namespace ForgeOrdnanceFrames;

/// <summary>Reusable in-flight rocket storage. Slots grow to preserve every granted rocket; each peer retires its copy,
/// and the peer that is authoritative at impact owns the damage.</summary>
internal sealed class FrameRocketFlights<T> where T : struct
{
    private const int InitialCapacity = 32;

    private struct Slot
    {
        internal bool Live;
        internal T Value;
    }

    private Slot[] _slots = new Slot[InitialCapacity];

    internal int Capacity => _slots.Length;
    internal int Count { get; private set; }

    internal int Add(T value)
    {
        for (var i = 0; i < _slots.Length; i++)
            if (!_slots[i].Live) return Activate(i, value);

        var index = _slots.Length;
        Array.Resize(ref _slots, checked(index * 2));
        return Activate(index, value);
    }

    internal bool IsLive(int index) => _slots[index].Live;

    internal ref T At(int index) => ref _slots[index].Value;

    /// <summary>Retires one live flight and says whether this peer owns its damage at impact.</summary>
    internal bool Settle(int index, bool currentHost)
    {
        if (!_slots[index].Live) return false;
        _slots[index].Live = false;
        Count--;
        return currentHost;
    }

    internal void Reset()
    {
        if (_slots.Length == InitialCapacity) Array.Clear(_slots, 0, _slots.Length);
        else _slots = new Slot[InitialCapacity];
        Count = 0;
    }

    private int Activate(int index, T value)
    {
        _slots[index].Value = value;
        _slots[index].Live = true;
        Count++;
        return index;
    }
}

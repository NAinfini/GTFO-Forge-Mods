using System;

namespace ForgePerformance.LoadShedding;

// Pure rules for the extra presentation cuts. No game types, so they run offline.
internal static class ExtraPolicy
{
    internal const float ShadowDistanceFloor = 20f;
    internal const float DecayClearDelay = 5f;
    internal const float DebrisFreezeMinimum = 3f;
    internal const float DebrisFreezeMaximum = 5f;
    internal const float DebrisRetryDelay = 1f;
    internal const int DebrisMaximumRetries = 3;
    internal const float DebrisRestSpeedSquared = 0.25f;
    internal const float MinimumRecycleDelay = 0.05f;

    // Never raises the distance and never goes below the floor.
    internal static float ShadowDistance(float original, float factor)
    {
        if (!float.IsFinite(original) || original <= ShadowDistanceFloor) return original;
        if (!float.IsFinite(factor) || factor <= 0 || factor > 1) throw new ArgumentOutOfRangeException(nameof(factor));
        return Math.Min(original, Math.Max(ShadowDistanceFloor, original * factor));
    }

    // Shortens the native corpse timer so vanilla despawns the body earlier.
    // The total never grows and the dissolve keeps a positive duration.
    internal static void RecycleTimer(float delay, float duration, float cap, out float newDelay, out float newDuration)
    {
        if (!float.IsFinite(delay) || delay < 0) throw new ArgumentOutOfRangeException(nameof(delay));
        if (!float.IsFinite(duration) || duration < 0) throw new ArgumentOutOfRangeException(nameof(duration));
        if (!float.IsFinite(cap) || cap <= 0) throw new ArgumentOutOfRangeException(nameof(cap));
        if (delay + duration <= cap) { newDelay = delay; newDuration = duration; return; }
        newDelay = Math.Min(delay, Math.Max(0, cap - MinimumRecycleDelay));
        newDuration = Math.Min(duration, Math.Max(MinimumRecycleDelay, cap - newDelay));
    }

    // Debris still moving is looked at again a few times before it is frozen.
    internal static bool DebrisShouldWait(bool awake, float speedSquared, int attempts)
        => awake && speedSquared > DebrisRestSpeedSquared && attempts < DebrisMaximumRetries;
}

// Fixed-capacity set of (item, due time, tag). Scans at most Capacity entries; never allocates after construction.
internal sealed class DueQueue<T> where T : class
{
    private readonly T?[] _items;
    private readonly float[] _due;
    private readonly int[] _tags;

    internal DueQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T?[capacity]; _due = new float[capacity]; _tags = new int[capacity];
    }

    internal int Count { get; private set; }
    internal int Capacity => _items.Length;

    internal bool Enqueue(T item, float due, int tag = 0)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (Count == _items.Length) return false;
        _items[Count] = item; _due[Count] = due; _tags[Count] = tag; Count++;
        return true;
    }

    internal bool TryTakeDue(float now, out T item, out int tag)
    {
        for (var i = 0; i < Count; i++)
        {
            if (_due[i] > now) continue;
            item = _items[i]!; tag = _tags[i];
            var last = --Count;
            _items[i] = _items[last]; _due[i] = _due[last]; _tags[i] = _tags[last];
            _items[last] = null;
            return true;
        }
        item = null!; tag = 0;
        return false;
    }

    internal void Clear()
    {
        Array.Clear(_items, 0, Count);
        Count = 0;
    }
}

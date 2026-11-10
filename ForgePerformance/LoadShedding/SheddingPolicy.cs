using System;

namespace ForgePerformance.LoadShedding;

internal static class SheddingPolicy
{
    internal const float AmbientIntervalSeconds = 1f / 15;
    internal const int RagdollLimit = 12;
    internal const int WindCapacity = 64;
    internal const int InitialAffectorCapacity = 4096;
    internal static bool ConsumeShell(ref int used, int limit)
    {
        if (used >= limit) return false;
        used++; return true;
    }
    internal static float CorpseLifetime(float originalDelay, float originalDuration, float maximum)
    {
        if (!float.IsFinite(originalDelay) || originalDelay < 0)
            throw new ArgumentOutOfRangeException(nameof(originalDelay));
        if (!float.IsFinite(originalDuration) || originalDuration < 0)
            throw new ArgumentOutOfRangeException(nameof(originalDuration));
        if (!float.IsFinite(maximum) || maximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        return (float)Math.Min((double)originalDelay + originalDuration, maximum);
    }

    internal static bool AmbientStep(ref float accumulated, float deltaTime, out float grantedDelta,
        float interval = AmbientIntervalSeconds)
    {
        if (!float.IsFinite(deltaTime) || deltaTime < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaTime));
        if (!float.IsFinite(accumulated) || accumulated < 0)
            throw new ArgumentOutOfRangeException(nameof(accumulated));
        if (!float.IsFinite(interval) || interval < AmbientIntervalSeconds)
            throw new ArgumentOutOfRangeException(nameof(interval));
        accumulated += deltaTime;
        grantedDelta = 0;
        if (accumulated < interval) return false;
        // Native integration supports at most this delta even at a lower rate.
        grantedDelta = Math.Min(accumulated, AmbientIntervalSeconds);
        accumulated = 0;
        return true;
    }
}

internal sealed class RagdollOrder
{
    private readonly IntPtr[] _values;
    internal int Count { get; private set; }

    internal RagdollOrder(int capacity = SheddingPolicy.RagdollLimit)
    {
        if (capacity <= 0 || capacity > SheddingPolicy.RagdollLimit)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _values = new IntPtr[capacity];
    }

    internal IntPtr Enabled(IntPtr value)
    {
        if (value == IntPtr.Zero) throw new ArgumentException("A ragdoll must have a nonzero native identity.", nameof(value));
        for (var i = 0; i < Count; i++)
            if (_values[i] == value) return IntPtr.Zero;
        var evicted = IntPtr.Zero;
        if (Count == _values.Length)
        {
            evicted = _values[0];
            Array.Copy(_values, 1, _values, 0, --Count);
        }
        _values[Count++] = value;
        return evicted;
    }

    internal void Disabled(IntPtr value)
    {
        for (var i = 0; i < Count; i++)
        {
            if (_values[i] != value) continue;
            Array.Copy(_values, i + 1, _values, i, Count - i - 1);
            _values[--Count] = IntPtr.Zero;
            return;
        }
    }

    internal void Clear()
    {
        Array.Clear(_values, 0, Count);
        Count = 0;
    }
}

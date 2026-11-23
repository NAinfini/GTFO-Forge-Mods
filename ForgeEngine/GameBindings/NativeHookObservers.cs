using System;
using System.Threading;

namespace ForgeEngine.GameBindings;

/// <summary>One Runtime subscription or physical observation, bound to the world it was taken in: it ends by itself
/// ("world-epoch-ended") the first time it is asked about after the world moved on, so a subscriber of an old world is never
/// called in a new one; a lease that spans worlds passes a constant world. A release that throws leaves the lease active so its
/// owner can release it again.</summary>
public sealed class NativeHookLease : IDisposable
{
    private Action? _release;
    private int _ending;
    private readonly Func<long> _world;
    private readonly Action<NativeHookLease>? _ended;
    private long _callbacks;

    /// <summary>A lease of <paramref name="consumer"/> taken in world <paramref name="epoch"/>; <paramref name="world"/> answers the
    /// current world, <paramref name="release"/> unsubscribes and <paramref name="ended"/> hears that it ended.</summary>
    public NativeHookLease(string consumer, long epoch, Func<long> world, Action release, Action<NativeHookLease>? ended = null)
    {
        Consumer = consumer; WorldEpoch = epoch;
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _ended = ended;
    }

    public string Consumer { get; }
    public long WorldEpoch { get; }
    /// <summary>"active", or why it ended: "released", "world-epoch-ended", "observer-failed", "replaced" ...</summary>
    public string Status { get; private set; } = "active";
    /// <summary>Calls delivered to this lease's callback.</summary>
    public long Callbacks => Interlocked.Read(ref _callbacks);
    internal void CallbackEntered() => Interlocked.Increment(ref _callbacks);

    public bool IsActive
    {
        get
        {
            if (Status == "active" && _world() != WorldEpoch) End("world-epoch-ended");
            return Status == "active";
        }
    }

    public void End(string status)
    {
        if (Interlocked.CompareExchange(ref _ending, 1, 0) != 0) return;
        try
        {
            if (_release == null) return;
            _release.Invoke();
            _release = null;
            Status = status;
            _ended?.Invoke(this);
        }
        finally { Volatile.Write(ref _ending, 0); }
    }

    public void Dispose() => End("released");
}

/// <summary>The subscribers of one Runtime-owned shared entry, each holding a lease of the host's current world: the one
/// subscription list every shared entry (<see cref="NativeHookChannel{T}"/>, <see cref="GameEnemyDamage"/>) keeps over its single
/// ForgeCore bracket subscription. Walked by descending priority, ties in subscription order (equal priorities keep subscription
/// order). A subscriber whose callback throws is ended ("observer-failed") and reported once; the others still run. Adding or
/// removing one replaces the array, so a call walks a stable snapshot and nothing allocates per call. Main thread only.</summary>
internal sealed class NativeHookObservers<T> where T : class
{
    internal readonly struct Entry
    {
        internal readonly NativeHookLease Lease;
        internal readonly T Observer;
        internal readonly int Priority;
        internal Entry(NativeHookLease lease, T observer, int priority) { Lease = lease; Observer = observer; Priority = priority; }
    }

    private readonly Func<long> _world;
    private readonly Action<string, Exception> _failed;
    private readonly int _budget;
    private Entry[] _entries = Array.Empty<Entry>();

    /// <summary>A list over <paramref name="world"/> that reports a failing subscriber through <paramref name="failed"/> and takes at
    /// most <paramref name="budget"/> subscribers.</summary>
    internal NativeHookObservers(Func<long> world, Action<string, Exception> failed, int budget)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _failed = failed ?? throw new ArgumentNullException(nameof(failed));
        _budget = budget;
    }

    /// <summary>The subscribers in walking order; the array is replaced, never changed.</summary>
    internal Entry[] Items => _entries;

    /// <summary>Subscribes <paramref name="consumer"/> for the world <paramref name="worldEpoch"/>. Refuses an invalid consumer
    /// (ArgumentException native-hook-consumer-invalid), a world that is not the current one, a consumer already subscribed and a full
    /// list (InvalidOperationException with the native-hook-* reason); <paramref name="refusal"/> turns the reason into the message.</summary>
    internal NativeHookLease Subscribe(string consumer, long worldEpoch, int priority, T observer, Func<string, string> refusal,
        Action<NativeHookLease>? ended = null)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));
        if (string.IsNullOrWhiteSpace(consumer) || consumer.Length > 128) throw new ArgumentException(refusal("native-hook-consumer-invalid"));
        if (worldEpoch < 0 || _world() != worldEpoch) throw new InvalidOperationException(refusal("native-hook-world-epoch-stale"));
        // Asking each lease ends one of an old world, which takes it off the list.
        foreach (var entry in _entries)
            if (entry.Lease.IsActive && entry.Lease.Consumer == consumer)
                throw new InvalidOperationException(refusal("native-hook-consumer-duplicate"));
        var entries = _entries;
        if (entries.Length >= _budget) throw new InvalidOperationException(refusal("native-hook-subscriber-budget"));
        NativeHookLease? lease = null;
        lease = new NativeHookLease(consumer, worldEpoch, _world, () => Remove(lease!), ended);
        var at = 0;
        while (at < entries.Length && entries[at].Priority >= priority) at++;
        var next = new Entry[entries.Length + 1];
        Array.Copy(entries, next, at);
        next[at] = new Entry(lease, observer, priority);
        Array.Copy(entries, at, next, at + 1, entries.Length - at);
        _entries = next;
        return lease;
    }

    /// <summary>Ends the subscriber whose callback threw and reports it once.</summary>
    internal void Failed(NativeHookLease lease, Exception error)
    {
        lease.End("observer-failed");
        _failed(lease.Consumer, error);
    }

    private void Remove(NativeHookLease lease)
    {
        var entries = _entries;
        var at = 0;
        while (at < entries.Length && !ReferenceEquals(entries[at].Lease, lease)) at++;
        if (at == entries.Length) return;
        var left = new Entry[entries.Length - 1];
        Array.Copy(entries, left, at);
        Array.Copy(entries, at + 1, left, at, left.Length - at);
        _entries = left;
    }
}

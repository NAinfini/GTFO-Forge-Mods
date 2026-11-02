using System.Collections.Generic;

namespace ForgeOrdnance;

/// <summary>Host side: the latest per-call state each payload type asked to keep (one message per payload type, call and tag),
/// replayed to players who join after it was sent. Bounded; changed only when a payload sends or clears state, never per frame.</summary>
internal sealed class RetainedData
{
    public const int Capacity = 256;

    private readonly Dictionary<(uint Type, ulong Thrower, uint ThrowId, byte Tag), PayloadDataMessage> _latest = new();
    private readonly List<(uint Type, ulong Thrower, uint ThrowId, byte Tag)> _order = new();

    public int Count => _latest.Count;

    /// <summary>Keeps <paramref name="message"/> as the state of its call and tag, replacing the previous one. False when the
    /// store is full and the key is new (nothing is kept).</summary>
    public bool Set(PayloadDataMessage message)
    {
        var key = (message.Type, message.Thrower, message.ThrowId, message.Tag);
        if (_latest.ContainsKey(key)) { _latest[key] = message; return true; }
        if (_latest.Count >= Capacity) return false;
        _latest.Add(key, message);
        _order.Add(key);
        return true;
    }

    /// <summary>Forgets every tag of one call of one payload type; returns how many were dropped.</summary>
    public int Clear(uint type, ulong thrower, uint throwId)
    {
        var dropped = _order.RemoveAll(k => k.Type == type && k.Thrower == thrower && k.ThrowId == throwId);
        if (dropped == 0) return 0;
        foreach (var key in new List<(uint, ulong, uint, byte)>(_latest.Keys))
            if (key.Item1 == type && key.Item2 == thrower && key.Item3 == throwId) _latest.Remove(key);
        return dropped;
    }

    public void Clear() { _latest.Clear(); _order.Clear(); }

    /// <summary>The kept messages in the order their call and tag were first kept.</summary>
    public IEnumerable<PayloadDataMessage> InOrder()
    {
        foreach (var key in _order) yield return _latest[key];
    }
}

using System.Collections.Generic;

namespace ForgeOrdnance;

/// <summary>A bounded out-of-order window per admitted peer, retaining the high-water mark for reconnects.</summary>
internal sealed class ThrowHistory
{
    internal const int Window = 256;
    private sealed class Peer
    {
        internal uint Floor;
        internal readonly uint[] Seen = new uint[Window];
    }
    private readonly Dictionary<ulong, Peer> _peers = new();
    internal uint Floor(ulong player) => _peers.TryGetValue(player, out var peer) ? peer.Floor : 0;
    internal bool Add(ulong player, uint id)
    {
        if (id == 0) return false;
        if (!_peers.TryGetValue(player, out var peer)) _peers.Add(player, peer = new Peer());
        if (id <= peer.Floor && peer.Floor - id >= Window) return false;
        var slot = id % Window;
        if (peer.Seen[slot] == id) return false;
        peer.Seen[slot] = id;
        if (id > peer.Floor) peer.Floor = id;
        return true;
    }
    internal void Clear() => _peers.Clear();
}

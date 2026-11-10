using System;
using System.Collections.Generic;

namespace ForgeReplay;

/// <summary>The latest state payload of every live object, per kind and key, written again at the start of each keyframe block so a jump can start from it.
/// An object whose payload <see cref="EventKind.Ends"/> leaves the table: its end stays in the delta record, and the keyframes after it no longer carry it.</summary>
internal sealed class KeyframeStates
{
    private readonly Dictionary<(ushort Kind, ulong Key), byte[]> _live = new();

    internal int Count => _live.Count;

    internal void Set(ushort kind, ulong key, ReadOnlySpan<byte> payload)
    {
        var id = (kind, key);
        if (EventKind.Ends(kind, payload)) _live.Remove(id);
        // The same size is the usual update (a state repeats with new numbers): the held bytes are written over, nothing is made.
        else if (_live.TryGetValue(id, out var held) && held.Length == payload.Length) payload.CopyTo(held);
        else _live[id] = payload.ToArray();
    }

    internal void Write(BlockPipeline pipeline, uint nowMs)
    {
        foreach (var (id, payload) in _live) pipeline.Event(nowMs, id.Kind, payload);
    }

    internal void Clear() => _live.Clear();
}

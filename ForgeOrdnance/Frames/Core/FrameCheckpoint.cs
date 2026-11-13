using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ForgeOrdnance.Frames;

/// <summary>One suit in the checkpoint copy: the call it came with and its state as saved.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FrameCheckpointEntry
{
    public ulong Thrower;
    public uint ThrowId;
    public FrameStateMessage State;
}

/// <summary>The suits as they stood at the last checkpoint, as the bytes of the host's checkpoint copy (ForgeCore keeps it on every
/// peer): one <see cref="FrameCheckpointEntry"/> after another. A reload rebuilds exactly that population, including suits destroyed
/// since the save.</summary>
internal static class FrameCheckpoint
{
    /// <summary>Host: the copy of <paramref name="records"/> as they stand now, each suit saved empty and idle.</summary>
    internal static byte[] Capture(IEnumerable<FrameRecord> records, float now)
    {
        var entries = new List<FrameCheckpointEntry>();
        foreach (var r in records)
        {
            if (r.State == FrameState.Destroyed) continue;
            var state = FrameStateMessage.From(r, now, FrameEjection.None);
            state.State = (byte)FrameState.Idle;
            state.Pilot = 0;
            state.Flags &= FrameStateMessage.FlagShieldUp;
            state.BoostRemaining = state.StateRemaining = 0f;
            state.SiegeDeployRemaining = 0f;
            entries.Add(new FrameCheckpointEntry { Thrower = r.Thrower, ThrowId = r.ThrowId, State = state });
        }
        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(entries)).ToArray();
    }

    /// <summary>The saved suits as fresh records, numbered above <paramref name="sequence"/>. Throws, replacing nothing, when this
    /// peer holds no copy, the copy is not whole entries, or a saved model or state is not usable here.</summary>
    internal static unsafe List<FrameRecord> Restore(byte[]? copy, IReadOnlyDictionary<ulong, FrameSpec> specs, float now, uint sequence)
    {
        if (copy == null || copy.Length % sizeof(FrameCheckpointEntry) != 0)
            throw new InvalidOperationException("Cannot restore exosuits: this player holds no usable checkpoint copy from the host.");
        var entries = MemoryMarshal.Cast<byte, FrameCheckpointEntry>(copy);
        var records = new List<FrameRecord>();
        foreach (var entry in entries) sequence = Math.Max(sequence, entry.State.Sequence);
        foreach (var entry in entries)
        {
            if (!specs.TryGetValue(entry.State.Spec, out var spec))
                throw new InvalidOperationException("Cannot restore exosuit " + entry.ThrowId + ": checkpoint model " + entry.State.Spec + " is unavailable.");
            var r = new FrameRecord(entry.Thrower, entry.ThrowId, spec);
            if (!entry.State.ApplyTo(r, now)) throw new InvalidOperationException("Invalid checkpoint state for exosuit " + entry.ThrowId + ".");
            r.Sequence = ++sequence;
            records.Add(r);
        }
        return records;
    }
}

using System;
using System.Collections.Generic;

namespace ForgeOrdnanceFrames;

/// <summary>The last complete checkpoint, retained on every peer even after a saved suit is removed.</summary>
internal sealed class FrameCheckpoint
{
    private List<FrameCheckpointMessage> _entries = new();
    private List<FrameCheckpointMessage>? _incoming;
    private int _expected;
    private uint _incomingRevision;

    internal int Count => _entries.Count;
    internal uint Revision { get; private set; }
    internal bool IsComplete { get; private set; }

    internal void Capture(IEnumerable<FrameRecord> records, float now)
    {
        Revision++;
        IsComplete = true;
        CancelIncoming();
        _entries.Clear();
        foreach (var r in records)
        {
            if (r.State == FrameState.Destroyed) continue;
            var state = FrameStateMessage.From(r, now, FrameEjection.None);
            state.State = (byte)FrameState.Idle;
            state.Pilot = 0;
            state.Flags &= FrameStateMessage.FlagShieldUp;
            state.BoostRemaining = state.StateRemaining = 0f;
            state.SiegeDeployRemaining = 0f;
            _entries.Add(new FrameCheckpointMessage { Kind = FrameCheckpointMessage.Entry, Revision = Revision, Thrower = r.Thrower, ThrowId = r.ThrowId, State = state });
        }
    }

    internal IEnumerable<FrameCheckpointMessage> Messages()
    {
        if (!IsComplete) yield break;
        yield return new FrameCheckpointMessage { Kind = FrameCheckpointMessage.Begin, Revision = Revision, Count = Count };
        foreach (var entry in _entries) yield return entry;
        yield return new FrameCheckpointMessage { Kind = FrameCheckpointMessage.Commit, Revision = Revision };
    }

    internal void Receive(FrameCheckpointMessage message)
    {
        switch (message.Kind)
        {
            case FrameCheckpointMessage.Begin:
                if (message.Revision < Revision) break;
                IsComplete = false;
                Revision = message.Revision;
                _incomingRevision = message.Revision;
                _expected = message.Count;
                _incoming = message.Count >= 0 ? new List<FrameCheckpointMessage>() : null;
                break;
            case FrameCheckpointMessage.Entry:
                if (_incoming == null || message.Revision != _incomingRevision) break;
                if (_incoming.Count >= _expected || _incoming.Exists(e => e.Thrower == message.Thrower && e.ThrowId == message.ThrowId))
                { _incoming = null; break; }
                _incoming.Add(message);
                break;
            case FrameCheckpointMessage.Commit:
                if (message.Revision != _incomingRevision) break;
                if (_incoming != null && _incoming.Count == _expected)
                {
                    _entries = _incoming;
                    Revision = _incomingRevision;
                    IsComplete = true;
                }
                _incoming = null;
                break;
        }
    }

    internal List<FrameRecord> Restore(IReadOnlyDictionary<ulong, FrameSpec> specs, float now, uint sequence)
    {
        if (!IsComplete) throw new InvalidOperationException("Cannot restore exosuits: the checkpoint snapshot is incomplete; awaiting resynchronization.");
        var records = new List<FrameRecord>();
        foreach (var entry in _entries) sequence = Math.Max(sequence, entry.State.Sequence);
        foreach (var entry in _entries)
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

    internal void CancelIncoming() => _incoming = null;
    internal void Invalidate() { IsComplete = false; CancelIncoming(); }
    internal void Clear() { _entries.Clear(); Revision = 0; Invalidate(); }
}

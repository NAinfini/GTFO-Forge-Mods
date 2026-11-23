using System.Collections.Generic;

namespace ForgeMap;

/// <summary>One subject of one fact: the fact kind, the subject's own text and, for a fact that tracks several
/// readings of one subject, the part the reading belongs to. It is a value, so looking a subject up builds no
/// key text.</summary>
internal readonly record struct FactSubject(string Fact, string Subject, string? Part = null);

/// <summary>One reading of one subject, as the values the native side reported rather than a text built from them:
/// a named state with an optional qualifier, and up to two numbers. Two readings are the same state exactly when
/// these fields are equal.</summary>
internal readonly record struct FactReading(string? Name, double First = 0, double Second = 0, string? Detail = null);

/// <summary>
/// The per-world memory of what each subject last reported and the transition number that reading was given. A
/// repeated reading has no new transition and therefore no new event id, which is what keeps a per-frame caller
/// from flooding the kernel. The memory is kept whether or not anyone listens, because a state observed while
/// nobody listened is a state the provider has already reported; it is value-keyed so remembering a reading
/// allocates nothing once the subject has been seen.
/// </summary>
internal sealed class FactTransitions
{
    private readonly Dictionary<FactSubject, (FactReading Reading, long Transition)> _last = new();

    /// <summary>Whether one reading is new for its subject. A new reading consumes the subject's next transition
    /// number and is remembered; a repeat answers false and changes nothing.</summary>
    internal bool Advance(in FactSubject subject, in FactReading reading, out long transition)
    {
        if (_last.TryGetValue(subject, out var last))
        {
            if (last.Reading == reading) { transition = last.Transition; return false; }
            transition = last.Transition + 1;
        }
        else transition = 1;
        _last[subject] = (reading, transition);
        return true;
    }

    /// <summary>Replaces a subject's remembered reading without consuming a transition, so the next report of the
    /// reading it replaced is news again. A subject never seen stays unseen.</summary>
    internal void Settle(in FactSubject subject, in FactReading reading)
    {
        if (_last.TryGetValue(subject, out var last) && last.Reading != reading) _last[subject] = (reading, last.Transition);
    }

    internal int Count => _last.Count;

    internal void Clear()
    {
        if (_last.Count != 0) _last.Clear();
    }
}

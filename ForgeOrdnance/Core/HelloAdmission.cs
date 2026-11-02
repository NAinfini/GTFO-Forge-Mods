using System.Collections.Generic;

namespace ForgeOrdnance;

internal enum HelloDecision { Ignore, Challenge, Accept }

/// <summary>Bootstrap only asks for the host epoch. State is admitted/replayed once after the peer proves that epoch.</summary>
internal sealed class HelloAdmission
{
    private readonly HashSet<(ulong Player, ulong Greeting)> _completed = new();
    internal HelloDecision Evaluate(ulong player, ulong greeting, ulong receivedEpoch, ulong currentEpoch)
    {
        if (greeting == 0) return HelloDecision.Ignore;
        if (receivedEpoch == 0) return HelloDecision.Challenge;
        if (receivedEpoch != currentEpoch || !_completed.Add((player, greeting))) return HelloDecision.Ignore;
        return HelloDecision.Accept;
    }
    internal void Clear() => _completed.Clear();
}

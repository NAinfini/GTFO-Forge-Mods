using System;

namespace ForgeEmote;

/// <summary>
/// The two-player actions (a handshake, a fist bump). One player offers: their hand goes out and stays out (<see cref="Phase.Offering"/>) until a teammate
/// within <see cref="Range"/> answers or <see cref="WaitSeconds"/> pass. The teammate answers by interacting (<see cref="Phase.Accepting"/>: they step to <see cref="Gap"/> and face the offerer,
/// their hand out too); the offerer then starts the shared moment (<see cref="Phase.Paired"/>) and the teammate confirms it with the same start, so both play the same stretch of the
/// clip at the same time. A side whose partner's state goes away, or whose confirmation does not come within <see cref="AcceptSeconds"/>, stops: never one player shaking hands alone.
/// All of it travels as the ordinary action state (the action, a sequence number, this phase and the partner), so nothing new goes on the wire.
/// </summary>
internal sealed class EmotePair
{
    internal const float Range = 1.5f, Gap = 1.2f, MaxStep = 0.6f, WaitSeconds = 15f, AcceptSeconds = 3f;
    /// <summary>How much farther than <see cref="Range"/> the offerer still takes an answer: the answerer judged the distance on their screen, a network trip earlier.</summary>
    internal const float Slack = 0.4f;
    /// <summary>The held clasp: seconds the hands stay together (moving a little) before the clip goes on to let go; <see cref="WobbleSpan"/> is how far into the clip the hands move, <see cref="WobbleHz"/> how fast.</summary>
    internal const float ClaspSeconds = 1.5f, WobbleSpan = 0.3f, WobbleHz = 2f;

    /// <summary>The numbers are what goes on the wire.</summary>
    internal enum Phase : byte { None, Offering, Accepting, Paired }

    internal Phase Now { get; private set; }
    internal ulong Partner { get; private set; }
    private float _since;

    /// <summary>Whether a player <paramref name="distance"/> metres away answers: the same dimension and within <see cref="Range"/>.</summary>
    internal static bool InRange(float distance, bool sameDimension) => sameDimension && distance <= Range;

    /// <summary>This player offered and no one has answered.</summary>
    internal void Wait(float now)
    {
        Now = Phase.Offering;
        Partner = 0;
        _since = now;
    }

    /// <summary>This player answered <paramref name="partner"/>'s offer and waits for it to start.</summary>
    internal void Accept(ulong partner, float now)
    {
        Now = Phase.Accepting;
        Partner = partner;
        _since = now;
    }

    /// <summary><paramref name="partner"/> answered this player's offer; false when out of range (by <see cref="Slack"/> more than an answer needs) or not offering.</summary>
    internal bool Pair(ulong partner, float distance, bool sameDimension)
    {
        if (!InRange(distance - Slack, sameDimension) || Now != Phase.Offering) return false;
        Now = Phase.Paired;
        Partner = partner;
        return true;
    }

    /// <summary>The offerer started it and this player, who answered, takes the same moment.</summary>
    internal void Seal()
    {
        if (Now == Phase.Accepting) Now = Phase.Paired;
    }

    /// <summary>Nobody answered in time, or the offerer did not confirm in time.</summary>
    internal bool Expired(float now) => Now switch
    {
        Phase.Offering => now - _since >= WaitSeconds,
        Phase.Accepting => now - _since >= AcceptSeconds,
        _ => false,
    };

    internal void Clear()
    {
        Now = Phase.None;
        Partner = 0;
    }

    /// <summary>Where in the clip a two-player action is, <paramref name="elapsed"/> seconds after its start: <paramref name="hold"/> (the hand out) while offered or answered; once paired the
    /// clip runs to <paramref name="hold"/>, stays there for <see cref="ClaspSeconds"/> with the hands moving a little (the clip's own poses just after it), then goes on. Both players get the same answer
    /// from the same start, so both hands move together.</summary>
    internal static float ClipTime(Phase phase, float elapsed, float hold)
    {
        if (phase is Phase.Offering or Phase.Accepting) return hold;
        if (elapsed <= hold) return elapsed;
        var held = elapsed - hold;
        if (held >= ClaspSeconds) return elapsed - ClaspSeconds;
        return hold + WobbleSpan * (0.5f - 0.5f * MathF.Cos(2f * MathF.PI * WobbleHz * held));
    }
}

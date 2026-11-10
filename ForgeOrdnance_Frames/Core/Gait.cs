namespace ForgeOrdnanceFrames;

/// <summary>The suit's footsteps: the pilot's own game footsteps (claimed through ForgeAudio's body sounds), so their cadence is the
/// game's, and the guard that lets one step sound through per footfall.</summary>
internal static class Gait
{
    /// <summary>Two footsteps of one suit closer than this are one (a step is never faster than a sprint's, about 0.25 s). A step's sound
    /// plays out its whole tail (<see cref="StepSeconds"/>), so this gap also bounds how many steps sound at once
    /// (<see cref="MaxStepsSounding"/>): the step bus in <c>audio/cues.forge</c> has exactly that many seats, so no step is cut by the next.</summary>
    public const float MinStepGap = 0.2f;
    /// <summary>The longest footstep sound (a clip's 0.9 s at the lowest pitch variation).</summary>
    public const float StepSeconds = 0.94f;
    /// <summary>The most footstep sounds alive at once of one suit at the fastest allowed step rate.</summary>
    public const int MaxStepsSounding = 5;
}

/// <summary>Lets one footstep sound through per footfall, whichever of the game's footstep calls (movement state or animation
/// event) reports it.</summary>
internal struct StepGate
{
    private float _last;
    private bool _any;

    /// <summary>Whether a footstep at game time <paramref name="now"/> is a new step.</summary>
    public bool Admit(float now)
    {
        if (_any && now - _last < Gait.MinStepGap && now >= _last) return false;
        _any = true;
        _last = now;
        return true;
    }
}

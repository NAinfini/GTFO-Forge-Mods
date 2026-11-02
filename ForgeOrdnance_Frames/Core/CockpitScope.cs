namespace ForgeOrdnanceFrames;

/// <summary>Where the local player is with a suit, as far as the screen is concerned.</summary>
public enum CockpitPhase : byte
{
    /// <summary>No suit: the game's HUD is whole and nothing of the cockpit shows.</summary>
    Off,
    /// <summary>Walking in, the camera still outside the shell: the game's HUD stays, the visor slides in.</summary>
    Boarding,
    /// <summary>The camera is inside the shell (black, then the power-on) or the suit is worn: the game's HUD is hidden, the chat moved,
    /// the cockpit shows.</summary>
    Worn,
    /// <summary>Leaving, before the power-down has gone black: the cockpit powers down over a still hidden game HUD.</summary>
    Leaving,
    /// <summary>Leaving, after the power-down went black (or a leave that came before the camera got inside): the game's HUD is back,
    /// the cockpit is gone.</summary>
    Out,
    /// <summary>The suit is worn but ForgeUI cannot hold the game's HUD now (the local player is not alive, or not in a level): the game's
    /// HUD is whole and the cockpit waits; it comes back by itself the frame ForgeUI can hold the HUD again.</summary>
    Suspended,
}

/// <summary>
/// The switch between the game's HUD and the suit's cockpit for the local player, read every frame from what the local player is now
/// (<see cref="For"/>), never from a sequence of events, so a state reached by any path (a late or merged record from the host, an
/// interrupted boarding, a release by ForgeUI, a fault) is corrected on the next frame. The game's HUD is hidden (and its chat moved to
/// the cockpit's bottom right) only while the camera is inside the shell: from the black of boarding to the black of leaving, so both
/// changes happen under a black screen. The native side mirrors <see cref="HideGameHud"/>, <see cref="MoveChat"/> and
/// <see cref="Cockpit"/> onto ForgeUI and takes the game's HUD again whenever it is wanted and not held.
/// </summary>
public sealed class CockpitScope
{
    public CockpitPhase Phase { get; private set; } = CockpitPhase.Off;

    /// <summary>The game's HUD is hidden (ForgeUI's hide scope is active).</summary>
    public bool HideGameHud => Phase is CockpitPhase.Worn or CockpitPhase.Leaving;

    /// <summary>The game's chat sits in the cockpit's corner; it moves with the hidden HUD so the two never disagree.</summary>
    public bool MoveChat => HideGameHud;

    /// <summary>The cockpit's own elements (bars, readouts, skills) are drawn.</summary>
    public bool Cockpit => Phase is CockpitPhase.Worn or CockpitPhase.Leaving;

    /// <summary>The visor shows (sliding in while boarding, steady while worn, until the leaving goes black).</summary>
    public bool Visor => Phase is CockpitPhase.Boarding or CockpitPhase.Worn or CockpitPhase.Leaving;

    /// <summary>Takes <paramref name="phase"/>; returns whether anything the screen shows changed.</summary>
    public bool Set(CockpitPhase phase)
    {
        if (phase == Phase) return false;
        var before = (HideGameHud, Cockpit, Visor);
        Phase = phase;
        return before != (HideGameHud, Cockpit, Visor);
    }

    /// <summary>The phase for the local player now. <paramref name="piloting"/>: the host's record names this player as a suit's pilot
    /// (<paramref name="state"/> is that suit's state); <paramref name="inside"/>: the camera has reached the cockpit eye (the boarding's
    /// black, or a suit worn at once); <paramref name="dark"/>: the leaving's power-down has gone black; <paramref name="holds"/>: ForgeUI
    /// can hold the game's HUD now (in a level, the local player alive).</summary>
    public static CockpitPhase For(bool piloting, FrameState state, bool inside, bool dark, bool holds)
    {
        if (!piloting) return CockpitPhase.Off;
        var phase = state switch
        {
            FrameState.Entering => inside ? CockpitPhase.Worn : CockpitPhase.Boarding,
            FrameState.Piloted => CockpitPhase.Worn,
            // A leave before the camera got inside has nothing to power down: the HUD was never hidden.
            FrameState.Exiting => !inside || dark ? CockpitPhase.Out : CockpitPhase.Leaving,
            _ => CockpitPhase.Off,
        };
        return !holds && phase is CockpitPhase.Worn or CockpitPhase.Leaving ? CockpitPhase.Suspended : phase;
    }
}

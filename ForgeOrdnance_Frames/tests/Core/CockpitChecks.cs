using ForgeOrdnanceFrames;

/// <summary>Every way into and out of the cockpit: the game's HUD is hidden only while the camera is inside the shell, both switches
/// happen under black, every release gives the HUD back, and the phase is read from the pilot's state now, so whatever happened on the
/// way (ForgeUI's own release, a revived pilot, a merged record) the next frame shows the cockpit again whenever the suit is worn.</summary>
internal static class CockpitChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static CockpitPhase P(bool piloting, FrameState state, bool inside = false, bool dark = false, bool holds = true) =>
            CockpitScope.For(piloting, state, inside, dark, holds);

        // The ride, frame by frame as the local player sees it.
        var s = new CockpitScope();
        check(!s.HideGameHud && !s.MoveChat && !s.Cockpit && !s.Visor, "no suit: the game's HUD is whole");
        check(s.Set(P(true, FrameState.Entering)) && !s.HideGameHud && s.Visor && !s.Cockpit, "boarding: the visor slides in, the game's HUD still shows while the camera is outside");
        check(s.Set(P(true, FrameState.Entering, inside: true)) && s.HideGameHud && s.MoveChat && s.Cockpit, "the camera inside the shell (black): the game's HUD hides, the chat moves, the cockpit shows");
        check(!s.Set(P(true, FrameState.Piloted, inside: true)) && s.Phase == CockpitPhase.Worn, "worn after boarding changes nothing on screen");
        check(!s.Set(P(true, FrameState.Exiting, inside: true)) && s.HideGameHud && s.Cockpit && s.Phase == CockpitPhase.Leaving, "leaving: the cockpit powers down over a still hidden game HUD");
        check(s.Set(P(true, FrameState.Exiting, inside: true, dark: true)) && !s.HideGameHud && !s.MoveChat && !s.Cockpit && !s.Visor, "the power-down black: the game's HUD is back, under the black");
        check(!s.Set(P(false, FrameState.Idle)) && s.Phase == CockpitPhase.Off, "the release after a normal exit changes nothing more");

        // Worn at once (a late joiner, a reload, a boarding whose black this player never saw): hidden straight away.
        check(P(true, FrameState.Piloted, inside: true) == CockpitPhase.Worn, "a suit worn without the boarding view hides the game's HUD at once");

        // The reported fault: ForgeUI gave the game's HUD back by itself while the suit stayed worn. The old event machine held that
        // until the next boarding (the cockpit gone for the whole ride); now the cockpit waits only while ForgeUI cannot hold the HUD.
        var ride = new CockpitScope();
        ride.Set(P(true, FrameState.Piloted, inside: true));
        ride.Set(P(true, FrameState.Piloted, inside: true, holds: false));
        check(ride.Phase == CockpitPhase.Suspended && !ride.HideGameHud && !ride.Cockpit, "while ForgeUI cannot hold the game's HUD (pilot not alive, not in a level) the game's HUD is whole and the cockpit waits");
        ride.Set(P(true, FrameState.Piloted, inside: true));
        check(ride.Phase == CockpitPhase.Worn && ride.HideGameHud && ride.Cockpit, "the frame ForgeUI can hold the HUD again with the suit still worn, the cockpit is back without a new boarding");
        check(P(true, FrameState.Exiting, inside: true, holds: false) == CockpitPhase.Suspended && P(true, FrameState.Entering, holds: false) == CockpitPhase.Boarding,
            "leaving or boarding while ForgeUI cannot hold: nothing hidden, the visor still slides in outside the shell");

        // Out of order or partial states never hide the HUD outside the shell.
        check(P(true, FrameState.Exiting) == CockpitPhase.Out, "a leave that arrives before the camera got inside hides nothing and takes the visor away");
        check(P(true, FrameState.Entering) == CockpitPhase.Boarding, "boarding again straight after leaving starts over");

        // Exhaustive: every combination of the pilot's state. Hidden only while worn or leaving, inside the shell, with ForgeUI able to
        // hold; never without a suit; and from any phase the next frame's state alone decides (no history).
        var exhaustive = true;
        foreach (FrameState state in Enum.GetValues(typeof(FrameState)))
            for (var bits = 0; bits < 16; bits++)
            {
                bool piloting = (bits & 1) != 0, inside = (bits & 2) != 0, dark = (bits & 4) != 0, holds = (bits & 8) != 0;
                var phase = CockpitScope.For(piloting, state, inside, dark, holds);
                var w = new CockpitScope();
                w.Set(phase);
                var hidden = piloting && holds && (state == FrameState.Piloted || inside && (state == FrameState.Entering || state == FrameState.Exiting && !dark));
                exhaustive &= w.HideGameHud == hidden && w.Cockpit == hidden && w.MoveChat == hidden;
                exhaustive &= piloting || phase == CockpitPhase.Off;
                foreach (CockpitPhase from in Enum.GetValues(typeof(CockpitPhase)))
                {
                    var again = new CockpitScope();
                    again.Set(from);
                    again.Set(phase);
                    exhaustive &= again.Phase == phase && again.HideGameHud == hidden;
                }
            }
        check(exhaustive, "every pilot state: hidden only inside the shell while worn or leaving and ForgeUI can hold, and the state now decides from any earlier phase");
    }
}

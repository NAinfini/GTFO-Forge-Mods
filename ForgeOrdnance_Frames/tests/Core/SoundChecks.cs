using ForgeOrdnanceFrames;

internal static class SoundChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var table = FrameSounds.Table;
        check(table.Length == Enum.GetValues<FrameSound>().Length && table.Select((entry, i) => entry.Sound == (FrameSound)i).All(same => same),
            "every sound has exactly one table row, in enum order");
        check(table.Select(entry => entry.Cue).Distinct().Count() == table.Length, "no two sounds share a cue");

        // Siege used to play the barrier shield sound for both directions.
        check(FrameSounds.Siege(true) == FrameSound.SiegeOn && FrameSounds.Siege(false) == FrameSound.SiegeOff, "siege deploy and release have their own sounds");
        check(table[(int)FrameSound.SiegeOn].Cue == "exo.siege_on" && table[(int)FrameSound.SiegeOff].Cue == "exo.siege_off", "siege sounds are named siege_on and siege_off");

        // The opening and closing make only a few sounds, all of the whole suit: unseal at the first unlatch, steam at the seam, the
        // slide when half of the parts have started moving; closing, one hatch sound when the last part is shut.
        float[] Row(float a, float b) => new[] { a, b };
        var metres = new[] { Row(0, 0), Row(0, 0), Row(0.003f, 0), Row(0.005f, 0), Row(0.01f, 0.01f), Row(0.05f, 0.03f), Row(0.05f, 0.03f), Row(0.05f, 0.03f) };
        var degrees = new[] { Row(0, 0), Row(0, 0), Row(0, 0), Row(0, 0), Row(5, 0.5f), Row(90, 60), Row(90, 60), Row(90, 60) };
        var open = PlateTimeline.Opening(metres, degrees, new[] { true, false }, 0.1f);
        bool Has(PlateEvent[] events, FrameSound sound, float at) => events.Any(e => e.Sound == sound && Math.Abs(e.At - at) < 1e-4f);
        check(open.Length == 3 && Has(open, FrameSound.Unseal, 0.1f) && Has(open, FrameSound.PlateSlide, 0.3f)
            && open.Count(e => e.Steam && e.Sound == null && Math.Abs(e.At - 0.1f) < 1e-4f) == 1 && open.Zip(open.Skip(1), (a, b) => a.At <= b.At).All(ordered => ordered),
            "the opening unseals, vents at the seam and slides the main body open once each, in time order");
        var close = PlateTimeline.Closing(Enumerable.Reverse(metres).ToArray(), Enumerable.Reverse(degrees).ToArray(), 0.1f);
        check(close.Length == 1 && Has(close, FrameSound.HatchClose, 0.6f), "the closing makes one hatch sound, when the last part is shut");
        check(open.Count(e => e.Sound != null) + close.Length <= 3, "boarding makes at most three plate sounds");
        // A peer that joins mid-sequence never plays a moment that already passed.
        check(PlateTimeline.Passed(open, -1f) == 0 && PlateTimeline.Passed(open, 0.1f) == 2 && PlateTimeline.Passed(open, 10f) == open.Length,
            "a late joiner skips the opening's sounds already past and keeps the rest");
        // Footsteps are the pilot's game footsteps taken over (one sound per game step, whichever call reports it).
        var gate = new StepGate();
        check(gate.Admit(1.00f) && !gate.Admit(1.05f) && gate.Admit(1.00f + Gait.MinStepGap + 0.01f), "two reports of one footfall make one step; the next footfall is a new step");
        check(gate.Admit(0.2f), "a clock that went back (new level) is not held off by the last step");
        check(Gait.MaxStepsSounding >= (int)Math.Ceiling(Gait.StepSeconds / Gait.MinStepGap) && gate.Admit(5f) && !gate.Admit(5f + Gait.MinStepGap * 0.9f) && gate.Admit(5f + Gait.MinStepGap + 0.01f),
            "steps come no closer than the gap, so no more than the step bus's seats sound at once and each plays out whole");
        // Power-on tones: one as each block comes on, in order, inside the panel stage.
        var plan = BootPlan.Default;
        check(Enumerable.Range(0, FrameBootScreen.Groups).All(g => plan.ToneAt(g) == plan.Timeline.GroupStart(g)) && Enumerable.Range(1, FrameBootScreen.Groups - 1).All(g => plan.ToneAt(g) > plan.ToneAt(g - 1))
            && plan.ToneAt(FrameBootScreen.Groups - 1) < plan.Timeline.LastLit, "power-on tones come with the blocks, in order");
    }
}

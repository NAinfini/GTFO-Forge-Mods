using ForgeUI;

/// <summary>The settled screen, steady occupied boxes and the shared progress bar.</summary>
internal static class ScreenChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Sampler(check);
        Boxes(check);
        Bar(check);
        Track(check);
    }

    private static void Sampler(Action<bool, string> check)
    {
        var key = new ScreenKey(3840, 2160, 60f, 1f);
        var sampler = new ScreenSampler();
        for (var i = 0; i < 30; i++) sampler.Add(key, new ScreenSample(0f, 0f, 0f, 0f));
        check(!sampler.Ready && sampler.Version == 0, "a screen measured as zero is never taken: nothing is laid out");
        sampler.Add(key, null);
        check(!sampler.Ready, "an unmeasurable frame is not taken either");

        // A swaying HUD: each measurement wanders by up to 1.3 %, the median settles once and then holds.
        var settledAt = -1;
        for (var i = 0; i < ScreenSampler.Samples; i++)
        {
            var wobble = (i % 2 == 0 ? 1f : -1f) * (i % 5) * 4f;
            if (sampler.Add(key, new ScreenSample(wobble, -wobble, 1280f + wobble, 720f - wobble))) settledAt = i;
        }
        check(sampler.Ready && settledAt == ScreenSampler.Samples - 1 && sampler.Version == 1, "the screen settles after the set number of samples");
        check(Math.Abs(sampler.Settled.HalfWidth - 1280f) <= 4f && Math.Abs(sampler.Settled.HalfHeight - 720f) <= 4f, "the settled size is the median of the wobble");
        var settled = sampler.Settled;
        for (var i = 0; i < 200; i++) sampler.Add(key, new ScreenSample(i % 7, 0f, 1270f + i % 20, 715f));
        check(sampler.Settled == settled && sampler.Version == 1 && !sampler.Sampling, "jitter after settling changes nothing: no new layout");
        check(sampler.Add(new ScreenKey(3840, 2160, 60.01f, 1.0001f), null) == false && !sampler.Sampling, "rounding in the field of view or scale is not a change");

        // A new resolution samples again while the old screen stays in use; the new one replaces it once settled.
        var smaller = new ScreenKey(1920, 1080, 60f, 1f);
        sampler.Add(smaller, null);
        check(sampler.Sampling && sampler.Ready && sampler.Settled == settled, "a new resolution samples again and keeps the old screen meanwhile");
        for (var i = 0; i < ScreenSampler.Samples; i++) sampler.Add(smaller, new ScreenSample(0f, 0f, 1280f, 720f));
        check(sampler.Version == 2 && !sampler.Sampling, "the new resolution settles once: one more layout");
        sampler.Reset();
        check(!sampler.Ready && sampler.Sampling, "a new HUD forgets the screen");

    }

    private static void Boxes(Action<bool, string> check)
    {
        var kept = new HudBox(-100f, -50f, 100f, 50f);
        check(HudBoxes.Settle(kept, new HudBox(-103f, -47f, 104f, 55f)).Equals(kept), "a box wobbling within the tolerance keeps its place");
        check(HudBoxes.Settle(kept, new HudBox(-100f, -50f, 100f, 50f + HudBoxes.Tolerance + 1f))!.Value.Top == 57f, "a box that moved past the tolerance moves");
        check(HudBoxes.Settle(kept, null) == null && HudBoxes.Settle(null, kept).Equals(kept), "a box that disappears or appears follows at once");
        check(HudBoxes.Step(0f) == 0f && HudBoxes.Step(1f) == 8f && HudBoxes.Step(8f) == 8f && HudBoxes.Step(8.1f) == 16f, "drawn sizes step by eight units");
    }

    private static void Bar(Action<bool, string> check)
    {
        var shapes = new HudShape[ProgressBar.MaxShapes];
        var colour = new HudColour(0.3f, 0.8f, 0.9f, 1f);
        var n = ProgressBar.Build(shapes, 10f, 20f, 200f, 8f, 0.5f, colour, -1f, 0.3f);
        check(n > 12 && n <= ProgressBar.MaxShapes, "a running bar writes its track, fill, sweep, ticks, edge and caps");
        var inside = true;
        var cells = 0;
        for (var i = 0; i < n; i++)
        {
            var s = shapes[i];
            if (s.Kind == HudShapeKind.Line) inside &= s.X >= 10f && s.W <= 210f && Math.Abs((s.Y + s.H) / 2f - 20f) < 1e-3f;
            else inside &= s.X - s.W / 2f >= 10f - 1e-3f && s.X + s.W / 2f <= 210f + 1e-3f && Math.Abs(s.Y - 20f) < 1e-3f;
            if (s.Kind == HudShapeKind.Cell) cells++;
        }
        check(inside, "every piece of a bar stays inside its box and on its middle line");
        check(cells == 2 && shapes[n - 2].Kind == HudShapeKind.Cell && shapes[n - 1].Kind == HudShapeKind.Cell, "a bar has two slanted caps");
        var fill = shapes[1];
        check(fill.Kind == HudShapeKind.Rect && Math.Abs(fill.W - (200f - 2f * 7.2f) * 0.5f) < 1e-3f && fill.R == colour.R, "the fill covers its fraction of the track in its colour");
        n = ProgressBar.Build(shapes, 10f, 20f, 200f, 8f, 1f, colour, 0.05f, 0.3f);
        check(n > 0 && shapes[1].R > 0.6f, "a completed bar flashes towards white");
        n = ProgressBar.Build(shapes, 10f, 20f, 200f, 8f, 1f, colour, ProgressBar.FlashSeconds + ProgressBar.FoldSeconds / 2f, 0.3f);
        check(n > 0 && shapes[0].H < 8f && shapes[0].A < ProgressBar.Track.A, "then folds away");
        check(ProgressBar.Build(shapes, 10f, 20f, 200f, 8f, 1f, colour, ProgressBar.FlashSeconds + ProgressBar.FoldSeconds, 0.3f) == 0, "and is gone after the fold");
        check(ProgressBar.Build(shapes, 10f, 20f, 0f, 8f, 0.5f, colour, -1f, 0f) == 0, "a bar with no width draws nothing");
    }

    private static void Track(Action<bool, string> check)
    {
        var track = new ProgressGauge();
        track.Fraction(-1f, 0f);
        check(!track.Visible, "nothing running: no bar");
        track.Fraction(0.4f, 1f);
        check(track.Visible && track.Fill == 0.4f && track.DoneAge < 0f && track.Sweep == 0.4f && track.Text == "40%", "a running process shows its share, sweep and percentage");
        track.Fraction(0.95f, 1.5f);
        track.Fraction(-1f, 1.52f);
        check(track.Visible && track.Fill == 1f && track.DoneAge == 0f && track.Text == "", "a process ending within its last tenth completes and flashes");
        track.Fraction(-1f, 1.52f + ProgressBar.FlashSeconds + ProgressBar.FoldSeconds + 0.01f);
        check(!track.Visible, "the completed bar is gone after its flash and fold");
        track.Fraction(0.3f, 3f);
        track.Fraction(-1f, 3.1f);
        check(!track.Visible, "a process abandoned early disappears at once without a flash");
        track.Fraction(0.5f, 4f);
        track.Fraction(1f, 4.2f);
        check(track.Visible && track.DoneAge == 0f, "a process that reached one completes");
        track.Fraction(1f, 9f);
        check(!track.Visible, "a share of one that never ran shows nothing");

        // A cooldown fills towards ready, writes the seconds left and flashes when the time is up.
        var cooldown = new ProgressGauge();
        cooldown.Cooldown(0f, 12f, 0f);
        check(!cooldown.Visible && cooldown.Seconds < 0f, "no cooldown: nothing shows");
        cooldown.Cooldown(9f, 12f, 1f);
        check(cooldown.Running && Math.Abs(cooldown.Fill - 0.25f) < 1e-5f && Math.Abs(cooldown.Sweep - 0.75f) < 1e-5f && cooldown.Text == "9.0" && cooldown.Seconds == 9f,
            "a cooldown's fill is the time passed, its ring the time left, its text the seconds left");
        check(cooldown.PieStep == CooldownMath.PieStep(0.75f), "the ring's sectors follow the time left");
        cooldown.Cooldown(14.2f, 12f, 1.1f);
        check(cooldown.Fill == 0f && cooldown.Text == "15", "more than the full length clamps the fill; whole seconds from ten on");
        cooldown.Cooldown(-0.01f, 12f, 2f);
        check(cooldown.Visible && !cooldown.Running && cooldown.DoneAge == 0f, "the time running out completes the cooldown with a flash");
        cooldown.Cooldown(3f, 0f, 3f);
        check(cooldown.Running && cooldown.Fill == 1f && cooldown.Sweep == 0f, "a cooldown of unknown length shows its seconds without a sweep");

        // A hold fills while held, fires once, keeps full while still held and drains when let go.
        var hold = new ProgressGauge { HoldSeconds = 1f };
        check(!hold.Hold(true, 0.5f, 0f) && hold.Running && Math.Abs(hold.Fill - 0.5f) < 1e-5f && hold.Mode == ProgressMode.Hold, "holding half way fills half");
        check(hold.Hold(true, 0.6f, 0.6f) && !hold.Running && hold.DoneAge == 0f, "completing the hold fires once and flashes");
        check(!hold.Hold(true, 0.5f, 0.7f) && hold.Held == 1f, "holding on does not fire again and stays full");
        hold.Hold(false, 0.1f, 0.8f);
        check(hold.Held < 1f && !hold.Running, "letting go drains it without running again");
        hold.Reset();
        check(!hold.Visible && hold.Held == 0f, "reset clears it");
        hold.Hold(true, 0.3f, 5f);
        hold.Hold(false, 1f, 5.5f);
        check(!hold.Visible, "a hold let go before the end fades out without a flash");
    }
}
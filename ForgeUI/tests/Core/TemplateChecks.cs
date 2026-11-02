using ForgeUI;

/// <summary>The arithmetic behind the ready-made HUD templates: easing, bars, cooldowns, hold prompts, the notice queue and the
/// screen geometry of world markers. How the pieces look in the game is in VALIDATION.md.</summary>
internal static class TemplateChecks
{
    private static bool Near(float a, float b, float tolerance = 1e-3f) => Math.Abs(a - b) <= tolerance;

    internal static void Run(Action<bool, string> check)
    {
        Easing(check);
        Meters(check);
        Cooldowns(check);
        Holds(check);
        Notices(check);
        Screen(check);
        Commands(check);
    }

    private static void Commands(Action<bool, string> check)
    {
        var code = new[] { 1, 2, 3 };
        check(CommandCode.StartsWith(code, Array.Empty<int>()), "an empty input begins every code");
        check(CommandCode.StartsWith(code, new[] { 1, 2 }) && CommandCode.StartsWith(code, code), "a code begins with its own steps and with itself");
        check(!CommandCode.StartsWith(code, new[] { 1, 3 }) && !CommandCode.StartsWith(code, new[] { 1, 2, 3, 4 }), "a wrong or too long input no longer fits");
        check(new CommandState(-1, 0).Ready && new CommandState(2, 0).Ready, "a command with no limit or uses left and no cooldown is ready");
        check(new CommandState(2, 5).Cooling && !new CommandState(2, 5).Ready && new CommandState(0, 0).Empty && !new CommandState(0, 0).Ready, "a cooling or used up command is not ready");
    }

    private static void Easing(Action<bool, string> check)
    {
        check(Ease.Clamp01(-1f) == 0f && Ease.Clamp01(2f) == 1f && Ease.Clamp01(float.NaN) == 0f, "ease clamps its input");
        foreach (var (name, curve) in new (string, Func<float, float>)[] { ("OutCubic", Ease.OutCubic), ("InCubic", Ease.InCubic), ("InOutCubic", Ease.InOutCubic), ("OutBack", Ease.OutBack) })
            check(Near(curve(0f), 0f) && Near(curve(1f), 1f), "ease " + name + " runs from 0 to 1");
        check(Ease.OutBack(0.7f) > 1f, "out-back overshoots before settling");
        check(Ease.OutCubic(0.5f) > 0.5f && Ease.InCubic(0.5f) < 0.5f, "out eases fast first, in slowly first");
        check(Ease.Blink(0.1f, 2f) == 1f && Ease.Blink(0.3f, 2f) == 0f, "blink alternates");
        // Damping covers the same ground at any frame rate.
        var one = Ease.Damp(0f, 1f, 6f, 0.016f);
        var two = Ease.Damp(Ease.Damp(0f, 1f, 6f, 0.008f), 1f, 6f, 0.008f);
        check(Near(one, two, 1e-4f) && Ease.Damp(0f, 1f, 6f, 0f) == 0f && Ease.Damp(0.5f, 1f, 6f, 100f) > 0.999f, "damping is independent of the frame rate");
    }

    private static void Meters(Action<bool, string> check)
    {
        check(MeterMath.Fraction(50f, 200f) == 0.25f && MeterMath.Fraction(5f, 0f) == 0f && MeterMath.Fraction(300f, 200f) == 1f && MeterMath.Fraction(-1f, 10f) == 0f, "meter fraction is clamped");
        check(MeterMath.Tone(0.9f, 0.4f, 0.2f, false) == MeterTone.Normal && MeterMath.Tone(0.4f, 0.4f, 0.2f, false) == MeterTone.Warn
              && MeterMath.Tone(0.1f, 0.4f, 0.2f, false) == MeterTone.Critical, "a bar turns warn then critical as it runs low");
        check(MeterMath.Tone(0.1f, 0.7f, 0.9f, true) == MeterTone.Normal && MeterMath.Tone(0.75f, 0.7f, 0.9f, true) == MeterTone.Warn
              && MeterMath.Tone(0.95f, 0.7f, 0.9f, true) == MeterTone.Critical, "a heat bar turns warn then critical as it fills");
        check(MeterMath.Cells(0f, 10) == 0 && MeterMath.Cells(0.01f, 10) == 1 && MeterMath.Cells(0.5f, 10) == 5 && MeterMath.Cells(1f, 10) == 10 && MeterMath.Cells(0.51f, 10) == 6, "cells light from any fill up to a full bar");
        var trail = new MeterTrail();
        trail.Reset(1f);
        trail.Update(0.4f, 0.016f);
        check(trail.Trail == 1f, "the chip stays where the fill was just after a drop");
        for (var i = 0; i < 10; i++) trail.Update(0.4f, 0.016f);
        check(trail.Trail == 1f, "and holds for its delay (" + MeterTrail.HoldSeconds + " s)");
        for (var i = 0; i < 40; i++) trail.Update(0.4f, 0.016f);
        check(trail.Trail < 1f && trail.Trail >= 0.4f, "then eases down");
        for (var i = 0; i < 600; i++) trail.Update(0.4f, 0.016f);
        check(trail.Trail == 0.4f, "and lands on the fill");
        trail.Update(0.9f, 0.016f);
        check(trail.Trail == 0.9f, "when the fill rises the chip rises with it");
    }

    private static void Cooldowns(Action<bool, string> check)
    {
        check(CooldownMath.Remaining01(3f, 12f) == 0.25f && CooldownMath.Remaining01(5f, 0f) == 0f && CooldownMath.Remaining01(20f, 12f) == 1f, "cooldown share is clamped");
        check(CooldownMath.PieStep(0f) == 0 && CooldownMath.PieStep(1f) == CooldownMath.PieSteps && CooldownMath.PieStep(0.001f) == 1 && CooldownMath.PieStep(0.5f) == CooldownMath.PieSteps / 2, "the circle shows any time left and fills only when just started");
        check(NumberText.Left(0f) == "0" && NumberText.Left(9.41f) == "9.5" && NumberText.Left(0.05f) == "0.1" && NumberText.Left(10f) == "10" && NumberText.Left(10.2f) == "11" && NumberText.Left(65f) == "65", "countdown text: tenths below ten, whole seconds rounded up from ten");
        check(ReferenceEquals(NumberText.Left(3.3f), NumberText.Left(3.25f)) && ReferenceEquals(NumberText.Count(42), NumberText.Count(42)) && ReferenceEquals(NumberText.Percent(73), NumberText.Percent(73))
              && ReferenceEquals(NumberText.Meters(18), NumberText.Meters(18)), "number texts are made once and handed out again");
        check(NumberText.Percent(73) == "73%" && NumberText.Percent(250) == "100%" && NumberText.Meters(42) == "42 m" && NumberText.Count(1234) == "1234" && NumberText.Meters(-5) == "0 m", "number texts read right at the edges");
        check(Near(TextWidth.Estimate("ABCDE", 10f), 30f) && Near(TextWidth.Estimate("爆炸", 10f), 20f), "text width counts 0.6 em per character and a full em per CJK character");
    }

    private static void Holds(Action<bool, string> check)
    {
        var hold = new HoldTimer(1f, 0.5f);
        var fired = 0;
        for (var i = 0; i < 50; i++) if (hold.Update(true, 0.016f)) fired++;
        check(fired == 0 && Near(hold.Progress, 0.8f, 0.01f), "a hold fills with time and does not fire early");
        for (var i = 0; i < 20; i++) if (hold.Update(true, 0.016f)) fired++;
        check(fired == 1 && hold.Progress == 1f && hold.Completed, "it fires once when complete");
        for (var i = 0; i < 100; i++) if (hold.Update(true, 0.016f)) fired++;
        check(fired == 1, "and not again while the key stays down");
        for (var i = 0; i < 10; i++) hold.Update(false, 0.016f);
        check(hold.Progress < 1f && hold.Progress > 0f, "releasing drains it");
        for (var i = 0; i < 60; i++) hold.Update(false, 0.016f);
        check(hold.Progress == 0f && !hold.Completed, "to nothing");
        for (var i = 0; i < 80; i++) if (hold.Update(true, 0.016f)) fired++;
        check(fired == 2, "after letting go it can fire again");
        hold.Update(true, 0.016f);
        hold.Reset();
        check(hold.Progress == 0f, "reset empties it");
    }

    private static void Notices(Action<bool, string> check)
    {
        var queue = new NoticeQueue(3);
        queue.Push("A", NoticeTone.Info, 2f, 0f);
        queue.Push("B", NoticeTone.Warn, 2f, 0.5f);
        check(queue.Count == 2 && queue.Text(0) == "B" && queue.Text(1) == "A" && queue.Tone(0) == NoticeTone.Warn, "the newest notice is first");
        var version = queue.Version;
        queue.Push("A", NoticeTone.Good, 2f, 1f);
        check(queue.Count == 2 && queue.Text(0) == "A" && queue.Tone(0) == NoticeTone.Good && queue.Version != version, "pushing a showing text refreshes it instead of stacking a copy");
        queue.Push("C", NoticeTone.Info, 2f, 1f);
        queue.Push("D", NoticeTone.Info, 2f, 1f);
        check(queue.Count == 3 && queue.Text(0) == "D" && queue.Text(2) == "A", "a full list drops the oldest");
        check(queue.Alpha(0, 1f) == 0f && queue.Alpha(0, 1f + NoticeQueue.FadeIn) == 1f && Near(queue.Alpha(0, 1f + NoticeQueue.FadeIn + 2f + NoticeQueue.FadeOut / 2f), 0.5f, 0.01f), "a notice fades in, holds and fades out");
        check(queue.Slide(0, 1f) == 0f && queue.Slide(0, 1f + NoticeQueue.FadeIn) == 1f, "and slides in");
        version = queue.Version;
        queue.Tick(1.5f);
        check(queue.Count == 3 && queue.Version == version, "nothing expires early");
        queue.Tick(1f + 2f + NoticeQueue.FadeIn + NoticeQueue.FadeOut + 0.01f);
        check(queue.Count == 0 && queue.Version != version, "expired notices are taken away");
        queue.Push("", NoticeTone.Info, 1f, 0f);
        check(queue.Count == 0, "empty text is ignored");
        queue.Push("E", NoticeTone.Info, 1f, 0f);
        queue.Clear();
        check(queue.Count == 0, "clear empties the list");
        // Every mod pushes into the one shared list; a mod clears only its own lines.
        object first = new(), second = new();
        queue.Push("MINE", NoticeTone.Info, 4f, 0f, first);
        queue.Push("THEIRS", NoticeTone.Warn, 4f, 0f, second);
        queue.Push("MINE TOO", NoticeTone.Good, 4f, 0f, first);
        queue.Clear(first);
        check(queue.Count == 1 && queue.Text(0) == "THEIRS", "clearing one owner's notices leaves the other mods' notices");    }

    private static void Screen(Action<bool, string> check)
    {
        check(HudRegions.Flow(HudRegion.Notices) == (1, -1) && HudRegions.Flow(HudRegion.BottomRight) == (-1, 1) && HudRegions.Flow(HudRegion.CenterBanner) == (0, 0) && HudRegions.Flow(HudRegion.TopCenter) == (0, -1) && HudRegions.Flow(HudRegion.WeaponList) == (-1, -1) && HudRegions.Flow(HudRegion.Vitals) == (0, 1), "content grows away from its region's anchor");
        check(ScreenMath.Span(-1, 100f) == -100f && ScreenMath.Span(1, 100f) == 0f && ScreenMath.Span(0, 100f) == -50f, "a block ends at, starts at or is centred on its anchor");
        var (x, y) = ScreenMath.ViewportToHud(0.5f, 0.5f, 960f, 540f);
        check(x == 0f && y == 0f, "the middle of the viewport is the middle of the screen");
        (x, y) = ScreenMath.ViewportToHud(1f, 1f, 960f, 540f);
        check(x == 960f && y == 540f, "and its top right corner is the screen's");

        var on = ScreenMath.EdgeMarker(100f, -50f, false, 960f, 540f, 60f, out var mx, out var my, out _);
        check(on && mx == 100f && my == -50f, "a target on screen keeps its place");
        on = ScreenMath.EdgeMarker(2000f, 0f, false, 960f, 540f, 60f, out mx, out my, out var angle);
        check(!on && Near(mx, 900f) && Near(my, 0f) && Near(angle, 0f), "a target far to the right goes to the right edge, arrow pointing right");
        on = ScreenMath.EdgeMarker(0f, 700f, false, 960f, 540f, 60f, out mx, out my, out angle);
        check(!on && Near(mx, 0f) && Near(my, 480f) && Near(angle, 90f), "above goes to the top edge, arrow pointing up");
        on = ScreenMath.EdgeMarker(1800f, 900f, false, 960f, 540f, 60f, out mx, out my, out _);
        check(!on && Math.Abs(mx) <= 900f + 1e-2f && Math.Abs(my) <= 480f + 1e-2f && (Near(Math.Abs(mx), 900f) || Near(Math.Abs(my), 480f)) && Near(my / mx, 900f / 1800f), "a diagonal target lands on the inset rectangle along its direction");
        on = ScreenMath.EdgeMarker(300f, 100f, true, 960f, 540f, 60f, out mx, out my, out angle);
        check(!on && mx < 0f && my < 0f && angle < -90f && angle > -180f, "a target behind the camera points the opposite way, even when its mirrored projection is inside the screen");
        on = ScreenMath.EdgeMarker(0f, 0f, true, 960f, 540f, 60f, out mx, out my, out angle);
        check(!on && Near(mx, 0f) && Near(my, -480f) && Near(angle, -90f), "directly behind points down");
        on = ScreenMath.EdgeMarker(0f, 0f, false, 100f, 100f, 200f, out mx, out my, out _);
        check(on || (!float.IsNaN(mx) && !float.IsNaN(my)), "an inset larger than the screen still gives a finite place");

    }
}

using ForgeOrdnanceFrames;

internal static class BootChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const int n = FrameBootScreen.PanelCount;
        var plan = BootPlan.Default;
        var tl = plan.Timeline;
        var beats = FrameBootMessage.Beats;
        // Ten convex panels tile the screen exactly and mirror about the middle (pairs 4/5, 6/7, 8/9; the window, top and bottom mirror themselves).
        var area = 0f; var convex = true; var mirrored = true;
        for (var p = 0; p < n; p++)
        {
            var m = FrameBootScreen.VertexCount(p);
            area += Area(p);
            for (var k = 0; k < m; k++)
            {
                var k1 = (k + 1) % m; var k2 = (k + 2) % m;
                var cross = (FrameBootScreen.X(p, k1) - FrameBootScreen.X(p, k)) * (FrameBootScreen.Y(p, k2) - FrameBootScreen.Y(p, k1))
                          - (FrameBootScreen.Y(p, k1) - FrameBootScreen.Y(p, k)) * (FrameBootScreen.X(p, k2) - FrameBootScreen.X(p, k1));
                convex &= cross < 1e-6f;
            }
        }
        for (var p = 4; p < n; p += 2) mirrored &= Math.Abs(Area(p) - Area(p + 1)) < 1e-5f && FrameBootScreen.GroupOf(p) == FrameBootScreen.GroupOf(p + 1);
        check(n == 10 && Math.Abs(area - 1f) < 1e-4f && convex && mirrored, "ten convex panels tile the screen exactly and mirror about the middle");
        var groups = new HashSet<int>();
        for (var p = 0; p < n; p++) groups.Add(FrameBootScreen.GroupOf(p));
        check(groups.Count == FrameBootScreen.Groups && groups.Count == 5 && FrameBootScreen.GroupOf(0) == FrameBootScreen.GroupOf(1), "the panels make five switch-on blocks (the window's two parts are one), nine blocks counting left and right");

        // The window first, then the rest one after another, not evenly spaced.
        var increasing = true; var gaps = new HashSet<int>();
        for (var g = 1; g < FrameBootScreen.Groups; g++)
        {
            increasing &= tl.GroupStart(g) > tl.GroupStart(g - 1);
            gaps.Add((int)MathF.Round((tl.GroupStart(g) - tl.GroupStart(g - 1)) * 1000f));
        }
        check(increasing && tl.StartOf(0) == tl.GroupStart(0) && gaps.Count > 1, "the window comes on first and the other blocks follow one after another at uneven gaps");
        check(plan.ControlsAt == tl.GroupStart(FrameBootScreen.Groups - 1) && Math.Abs(FrameSequence.EnterSeconds - 2.85f) < 1e-4f
              && Math.Abs(FrameSequence.Black + plan.Assembled - 3.53f) < 1e-4f && In(plan.Assembled, 1.5f, 1.8f) && tl.GroupStart(0) >= 0.15f,
            "the screen holds black a moment, the blocks come into view one by one; the boarding ends and the pilot moves 2.85 s in, as the last block starts to come on, and the picture is whole at 3.53 s");
        check(plan.Settled == BootPlan.Duration && plan.Settled > plan.Assembled && BootPlan.LinkLock > plan.Assembled && BootPlan.MessageAt(BootPlan.MessageStart) == 0f,
            "the message runs on past the boarding: NEURAL LINK OK locks and folds while the pilot already walks");
        // The controls open on the very frame the last block starts to come on, on the state's clock alone; the last two blocks settling,
        // the seams and the message still running (its lock, its self-test, the ready voice) are not inputs to that decision.
        var moves = FrameSequence.Black + plan.ControlsAt;
        var lastStarts = true;
        for (var p = 0; p < n; p++) lastStarts &= !tl.Black(p, plan.ControlsAt);
        var blackBefore = false;
        for (var p = 0; p < n; p++) blackBefore |= tl.Black(p, plan.ControlsAt - 0.001f);
        var settling = false;
        for (var p = 0; p < n; p++) settling |= !tl.Plain(p, plan.ControlsAt);
        check(!FrameSequence.ControlsOpen(moves - 0.001f) && FrameSequence.ControlsOpen(moves) && FrameSequence.ControlsOpen(FrameSequence.EnterSeconds)
              && Math.Abs(moves - FrameSequence.EnterSeconds) < 1e-4f && lastStarts && blackBefore && settling && !tl.IsAssembled(plan.ControlsAt),
            "the movement opens on the frame the last block starts to come on (2.85 s), neither a frame before nor after, with blocks still settling and the seams still up");
        var linkAt = FrameSequence.Black + BootPlan.LinkLock;
        var whole = FrameSequence.Black + plan.Assembled;
        check(BootPlan.MessageAt(moves - FrameSequence.Black) > 0f && linkAt > whole && whole > moves && FrameSequence.ControlsOpen(whole)
              && FrameSequence.ControlsOpen(linkAt - 0.001f),
            "while the last blocks settle and the NEURAL LINK message is still playing, the movement is already open");

        // Each panel: black until its block comes on, then a strong glitch that settles to the plain picture in about 0.3 s; blocks run on their own clocks.
        var own = true;
        for (var p = 0; p < n; p++)
        {
            var start = tl.StartOf(p);
            own &= tl.Black(p, 0f) && tl.Shift(p, 0f) == 0f && !tl.Plain(p, 0f);
            own &= !tl.Black(p, start) && tl.Shift(p, start + 0.001f) > 0.95f && !tl.Plain(p, start + 0.001f);
            own &= tl.Shift(p, start + FrameBootScreen.ShiftSeconds * 0.5f) is > 0.1f and < 0.9f && tl.Shift(p, start + FrameBootScreen.ShiftSeconds) == 0f
                   && tl.Plain(p, start + FrameBootScreen.ShiftSeconds) && !tl.Black(p, start + FrameBootScreen.ShiftSeconds);
        }
        check(own && In(FrameBootScreen.ShiftSeconds, 0.4f, 0.6f), "a panel is black, comes on with a strong glitch and settles to the plain picture in about half a second");
        var t = tl.GroupStart(2) + 0.1f;
        var mix = false; var plain = false; var glitch = false; var dark = false;
        for (var p = 0; p < n; p++)
        {
            plain |= tl.Plain(p, t); glitch |= tl.Shift(p, t) > 0f; dark |= tl.Black(p, t);
        }
        mix = plain && glitch && dark;
        check(mix, "at one moment some panels are plain, some still glitching and some still black");
        check(tl.Seed(0) != tl.Seed(4) && tl.Seed(4) == tl.Seed(5), "each block glitches differently, the two sides of a pair alike");

        // The seams: thin dark lines that stay a moment after the last block settled, then fade; gone when the picture is whole.
        var seamsOk = true;
        for (var i = 0; i < FrameBootScreen.SeamCount; i++)
        {
            FrameBootScreen.Seam(i, out var x0, out var y0, out var x1, out var y1);
            seamsOk &= Math.Abs(x1 - x0) + Math.Abs(y1 - y0) > 0.1f && x0 is >= 0f and <= 1f && x1 is >= 0f and <= 1f && y0 is >= 0f and <= 1f && y1 is >= 0f and <= 1f;
        }
        check(seamsOk && tl.SeamAlpha(tl.LastLit + FrameBootScreen.SeamHold - 0.01f) == 1f && tl.SeamAlpha(tl.Assembled) == 0f
              && tl.SeamAlpha(tl.LastLit + FrameBootScreen.SeamHold + FrameBootScreen.SeamFade * 0.5f) is > 0f and < 1f && In(FrameBootScreen.SeamHold, 0.1f, 0.2f),
            "the dark seams stay briefly after the last block, then fade");

        // The cockpit's parts come online one after another while the panels settle (each with ForgeUI's reveal): the top bars first, the
        // crosshair arcs last, their reveal locking on the message's lock beat.
        var reportOk = true;
        var order = new[] { CockpitPart.Bars, CockpitPart.Objectives, CockpitPart.Radar, CockpitPart.Weapon, CockpitPart.Skills, CockpitPart.Teammates, CockpitPart.Arcs };
        for (var k = 0; k < order.Length; k++)
        {
            var at = plan.ReportAt(order[k]);
            reportOk &= (k == 0 || at > plan.ReportAt(order[k - 1])) && !plan.Shown(order[k], at - 0.01f) && plan.Shown(order[k], at) && plan.Shown(order[k], plan.Settled);
        }
        check(reportOk && CockpitParts.Count == order.Length && plan.ReportAt(CockpitPart.Teammates) < tl.Assembled && Enum.GetValues<CockpitPart>().All(p => p == CockpitPart.Arcs || plan.ReportAt(p) > tl.GroupStart(CockpitParts.DownGroup(p)))
              && Math.Abs(plan.ReportAt(CockpitPart.Arcs) + ForgeUI.HudReveal.LockStart - BootPlan.LinkLock) < 1e-4f,
            "the cockpit's parts come online in order once the block over them has come on, all but the crosshair arcs before the picture is whole, the arcs locking on the message's lock");

        // The middle group: the header decodes and the bar fills with a stall, the self-test types on line by line with an OK each, the bar
        // is full before the message decodes, the message locks (the ready voice), holds and folds by the end.
        var stall = beats.BarFill(beats.StallEnd) - beats.BarFill(beats.StallStart);
        check(beats.BarFill(beats.BarStart) == 0f && beats.BarFill(beats.BarFull) == 1f && stall < 0.15f && beats.BarFill(1.0f) > beats.BarFill(beats.StallEnd) + 0.2f,
            "the bar fills from empty to full with a short stall in the middle");
        var linesOk = true;
        for (var i = 1; i < beats.CheckStarts.Length; i++) linesOk &= beats.CheckStarts[i] > beats.CheckStarts[i - 1];
        check(linesOk && beats.CheckStarts[0] > beats.HeaderEnd && beats.CheckStarts[beats.CheckStarts.Length - 1] + beats.CheckOk < beats.BarFull
              && beats.BarGone > beats.MessageStart && plan.LinkSteady == BootPlan.LinkLock
              && Math.Abs(BootPlan.MessageStart + beats.MessageLock - BootPlan.LinkLock) < 1e-5f && beats.MessageLock < beats.FoldStart
              && Math.Abs(BootPlan.MessageStart + beats.Duration - BootPlan.Duration) < 1e-5f && beats.ChecksGone < beats.FoldStart,
            "the self-test finishes before the bar is full, the message decodes as the bar flashes, locks, holds and folds by the end");
        // The power-on starts at the boarding's black; the first block comes on after a short black hold.
        check(tl.GroupStart(0) <= 0.3f && Enumerable.Range(1, FrameBootScreen.Groups - 1).All(g => tl.GroupStart(g) > tl.GroupStart(g - 1)),
            "the first block lights a moment after the black, the others follow in order");

        // Leaving: each part goes dark with its block, outer blocks first, before that block turns black.
        var downOk = true;
        foreach (CockpitPart part in Enum.GetValues(typeof(CockpitPart)))
        {
            var g = CockpitParts.DownGroup(part);
            var blackAt = 0f;
            while (tl.DownGroup(g, blackAt) < 0.6f) blackAt += 0.005f;
            var flicker = new HashSet<bool>();
            for (var d = 0f; d < blackAt; d += 0.002f) flicker.Add(CockpitParts.ShownLeaving(part, d));
            downOk &= CockpitParts.ShownLeaving(part, 0f) && !CockpitParts.ShownLeaving(part, blackAt) && !CockpitParts.ShownLeaving(part, FrameBootScreen.DownDark) && flicker.Count == 2;
        }
        check(downOk && CockpitParts.DownGroup(CockpitPart.Skills) == 4 && CockpitParts.DownGroup(CockpitPart.Arcs) == 0,
            "leaving, each part flickers and goes dark with the panel over it (the corners first, the crosshair last), before it turns black");

        // Leaving: the same blocks in reverse, the outer pair first, each glitching and going black; the pilot pulls out the moment every
        // block is black, the black lasts only while the head is in the shell and clears around the landing.
        var down = Math.Abs(tl.DownDark - FrameBootScreen.DownDark) < 1e-5f && Math.Abs(tl.DownSeconds - FrameBootScreen.DownSeconds) < 1e-5f
                   && FrameSequence.ExitClipStart == FrameBootScreen.DownDark && FrameBootScreen.DownClearStart - FrameBootScreen.DownDark <= 0.85f
                   && FrameBootScreen.DownSeconds < FrameSequence.ExitSeconds + 0.2f && In(FrameBootScreen.DownDark, 0.6f, 0.9f)
                   && tl.DownShift(8, 0.02f) > 0f && tl.DownShift(0, 0.02f) == 0f && tl.DownShift(8, 0f) == 0f;
        var allDark = true;
        for (var p = 0; p < n; p++) allDark &= tl.DownShift(p, FrameBootScreen.DownDark + 0.001f) == 1f && tl.DownBlack(p, FrameBootScreen.DownDark + 0.001f) > 0.999f;
        check(down && allDark && tl.DownBlack(8, 0.01f) == 0f && tl.DownAlpha(FrameBootScreen.DownClearStart) == 1f && tl.DownAlpha(FrameBootScreen.DownSeconds + 0.001f) == 0f
              && tl.DownAlpha((FrameBootScreen.DownDark + FrameBootScreen.DownClearStart) * 0.5f) == 1f && tl.DownAlpha(FrameBootScreen.DownClearStart + FrameBootScreen.DownClear * 0.5f) is > 0f and < 1f,
            "the power-down blackens the outer pair first, every panel is dark, the black lasts under 0.85 s and clears around the landing");
        Layout(check);
    }

    private static bool In(float v, float lo, float hi) => v >= lo && v <= hi;

    /// <summary>The power-on message in crosshair space at 1080p and 4K: the header, bar and self-test, NEURAL LINK OK and the status words
    /// never overlap the crosshair and its arcs (their keep-out box), the message never overlaps the lines under it, all of it stays on
    /// the screen, and each line's OK is green while its name keeps the accent.</summary>
    private static void Layout(Action<bool, string> check)
    {
        var look = FrameBootMessage.Look;
        var scene = new ForgeUI.BootMessage(FrameBootMessage.Beats, look);
        var words = new ForgeUI.BootMessageWords
        {
            Header = "NEURAL LINK", Stall = "SYNC", Ok = "OK", Message = "NEURAL LINK OK", Down = "SHUTDOWN",
            Checks = new[] { "SEAL", "ARMOR 80%", "SERVOS", "WEAPON LINK", "SENSORS" },
        };
        var cn = new ForgeUI.BootMessageWords
        {
            Header = "神经连接", Stall = "同步", Ok = "正常", Message = "神经连接正常", Down = "关机",
            Checks = new[] { "密封", "装甲 80%", "伺服", "武器链路", "传感器" },
        };
        var clear = true; var onScreen = true; var apart = true; var green = true; var names = true; var seen = 0;
        foreach (var w in new[] { words, cn })
        {
            scene.Prepare(w);
            foreach (var scale in new[] { 1f, 2f })
            {
                var keep = FrameBootMessage.Keepout;
                for (var s = 0f; s < FrameBootMessage.Beats.Duration; s += 0.02f)
                {
                    scene.Build(s, -1f);
                    var message = new List<(float L, float B, float R, float T)>();
                    var rest = new List<(float L, float B, float R, float T)>();
                    for (var i = 0; i < scene.Count; i++)
                    {
                        var item = scene.Items[i];
                        var (l, b, r, tp) = Box(item);
                        b += ForgeUI.HudRegions.BannerRise; tp += ForgeUI.HudRegions.BannerRise;
                        clear &= !(l < keep.Right && keep.Left < r && b < keep.Top && keep.Bottom < tp);
                        onScreen &= tp * scale <= 540f * scale - 30f * scale && b * scale >= -540f * scale;
                        if (item.Kind == ForgeUI.BootMessageItemKind.Text && item.Px >= look.MessagePx - 0.1f || item.Kind == ForgeUI.BootMessageItemKind.Line && s >= FrameBootMessage.Beats.MessageLock && item.T > 2f)
                            message.Add((l, b, r, tp));
                        else if (item.Kind is ForgeUI.BootMessageItemKind.Text or ForgeUI.BootMessageItemKind.Plate or ForgeUI.BootMessageItemKind.Cell)
                            rest.Add((l, b, r, tp));
                        if (item.Kind == ForgeUI.BootMessageItemKind.Text && item.Text == w.Ok)
                        {
                            seen++;
                            var c = item.Colour;
                            green &= c.R == look.Accent.R && c.G == look.Accent.G && c.B == look.Accent.B && c.A < 0.01f || c.A >= 0f && (c.R == 1f && c.G == 1f && c.B == 1f || c.R == FrameBootMessage.StatusGreen.R && c.G == FrameBootMessage.StatusGreen.G && c.B == FrameBootMessage.StatusGreen.B);
                        }
                        if (item.Kind == ForgeUI.BootMessageItemKind.Text && Array.IndexOf(w.Checks, item.Text) >= 0 || item.Kind == ForgeUI.BootMessageItemKind.Text && item.Px == look.CheckPx && item.Text!.Length > 0 && w.Checks.Any(k => k.StartsWith(item.Text, StringComparison.Ordinal)))
                            names &= item.Colour.R == look.Accent.R && item.Colour.G == look.Accent.G && item.Colour.B == look.Accent.B;
                    }
                    foreach (var m in message)
                        foreach (var g in rest)
                            apart &= !(m.L < g.R && g.L < m.R && m.B < g.T && g.B < m.T);
                }
            }
        }
        check(seen > 0 && clear, "the power-on message, bar and self-test never overlap the crosshair and its arcs, in English and Chinese");
        check(onScreen, "the power-on message stays on the screen at 1080p and 4K");
        check(apart, "NEURAL LINK OK and its frame never overlap the header, bar or self-test lines");
        check(green && names, "each self-test line's OK is green (white while it flashes) and the line's name keeps the accent colour");
    }

    private static (float L, float B, float R, float T) Box(ForgeUI.BootMessageItem item)
    {
        switch (item.Kind)
        {
            case ForgeUI.BootMessageItemKind.Line:
                var half = item.T / 2f;
                return (Math.Min(item.X, item.W) - half, Math.Min(item.Y, item.H) - half, Math.Max(item.X, item.W) + half, Math.Max(item.Y, item.H) + half);
            case ForgeUI.BootMessageItemKind.Text:
                var w = ForgeUI.BootMessage.TextWidth(item.Text!, item.Px);
                var left = item.Align == 0 ? item.X : item.Align == 1 ? item.X - w / 2f : item.X - w;
                return (left, item.Y - item.Px / 2f, left + w, item.Y + item.Px / 2f);
            default:
                return (item.X - item.W / 2f, item.Y - item.H / 2f, item.X + item.W / 2f, item.Y + item.H / 2f);
        }
    }

    private static float Area(int p)
    {
        var a = 0f; var m = FrameBootScreen.VertexCount(p);
        for (var k = 0; k < m; k++)
        {
            var k1 = (k + 1) % m;
            a += FrameBootScreen.X(p, k) * FrameBootScreen.Y(p, k1) - FrameBootScreen.X(p, k1) * FrameBootScreen.Y(p, k);
        }
        return Math.Abs(a) / 2f;
    }
}

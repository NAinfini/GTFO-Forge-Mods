using System;
using System.Collections.Generic;
using System.Linq;
using ForgeUI;

/// <summary>The cockpit mechanisms any mod drives with its own content: the screen power-on timeline, the power-on message, the segment
/// bar's easing, readout and alarm, the arc gauges' flare and the damage display's per-side shares.</summary>
internal static class PowerOnChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Timeline(check);
        Message(check);
        Segments(check);
        Flare(check);
        Shares(check);
    }

    private static void Timeline(Action<bool, string> check)
    {
        // Four panels in three blocks, the middle block's two panels alike.
        var tl = new PowerOnTimeline(new[] { 0, 1, 1, 2 }, new PowerOnRhythm { Starts = new[] { 0.1f, 0.2f, 0.35f }, ShiftSeconds = 0.25f, DownClearStart = 1f, DownClear = 0.2f });
        var own = true;
        for (var p = 0; p < tl.Panels; p++)
        {
            var start = tl.StartOf(p);
            own &= tl.Black(p, 0f) && tl.Shift(p, 0f) == 0f && !tl.Plain(p, 0f);
            own &= !tl.Black(p, start) && tl.Shift(p, start + 0.001f) > 0.95f && tl.Shift(p, start + 0.125f) is > 0.1f and < 0.9f && tl.Plain(p, start + 0.25f);
        }
        check(own && tl.Groups == 3 && Math.Abs(tl.LastLit - 0.6f) < 1e-5f && tl.Assembled > tl.LastLit && !tl.IsAssembled(tl.Assembled - 0.001f) && tl.IsAssembled(tl.Assembled),
            "each panel is black until its block comes on, glitches strongly and settles to the plain picture on its block's own clock");
        check(tl.Seed(1) == tl.Seed(2) && tl.Seed(0) != tl.Seed(3), "the panels of a block glitch alike, blocks differently");
        check(tl.SeamAlpha(tl.LastLit + tl.Rhythm.SeamHold - 0.01f) == 1f && tl.SeamAlpha(tl.Assembled) == 0f
              && tl.SeamAlpha(tl.LastLit + tl.Rhythm.SeamHold + tl.Rhythm.SeamFade * 0.5f) is > 0f and < 1f,
            "the seams stay briefly after the last block settles, then fade");
        var dark = true;
        for (var p = 0; p < tl.Panels; p++) dark &= tl.DownShift(p, tl.DownDark + 0.001f) == 1f && tl.DownBlack(p, tl.DownDark + 0.001f) > 0.999f;
        check(dark && tl.DownShift(3, 0.02f) > 0f && tl.DownShift(0, 0.02f) == 0f && tl.DownBlack(3, 0.01f) == 0f
              && tl.DownAlpha(1f) == 1f && tl.DownAlpha(tl.DownSeconds + 0.001f) == 0f && tl.DownAlpha(1.1f) is > 0f and < 1f,
            "powering down runs the blocks in reverse to black, holds and clears");
        var threw = false;
        try { _ = new PowerOnTimeline(new[] { 0, 3 }, new PowerOnRhythm { Starts = new[] { 0f, 0.1f } }); } catch (ArgumentException) { threw = true; }
        check(threw, "a panel in a block without a start is refused");
    }

    private static BootMessage Scene(float boot, float down = -1f)
    {
        var scene = new BootMessage(new BootMessageBeats(), new BootMessageLook());
        scene.Prepare(Words);
        scene.Build(boot, down);
        return scene;
    }

    private static readonly BootMessageWords Words = new()
    {
        Header = "SYSTEM LINK", Stall = "SYNC", Ok = "OK", Message = "ONLINE", Down = "OFFLINE",
        Checks = new[] { "SEAL", "ARMOR 080%", "SERVOS", "WEAPON", "SENSORS" },
    };

    private static IEnumerable<BootMessageItem> Items(BootMessage scene) => scene.Items.Take(scene.Count);

    private static List<string?> Texts(BootMessage scene) => Items(scene).Where(i => i.Kind == BootMessageItemKind.Text).Select(i => i.Text).ToList();

    private static void Message(Action<bool, string> check)
    {
        var beats = new BootMessageBeats();
        check(Scene(-0.01f).Count == 0 && Scene(beats.Duration + 0.01f).Count == 0 && Scene(beats.Duration + 1f).Width == 0f,
            "the message draws nothing before it starts and nothing once it is over (it asks for no room)");
        var ignite = Scene(0.06f);
        check(ignite.Count > 0 && Items(ignite).All(i => i.Kind is BootMessageItemKind.Rect or BootMessageItemKind.Line) && Texts(ignite).Count == 0,
            "first only the ignition line and the bar's brackets draw");
        var decoding = Scene(0.2f);
        check(Texts(decoding).Any(t => t != null && t.Length == Words.Header.Length && t != Words.Header) && Items(decoding).Count(i => i.Kind == BootMessageItemKind.Cell) == new BootMessageLook().Cells,
            "the header decodes from scrambled glyphs over the bar's cells");
        var stall = beats.BarFill(beats.StallEnd) - beats.BarFill(beats.StallStart);
        check(beats.BarFill(beats.BarStart) == 0f && beats.BarFill(beats.BarFull) == 1f && stall < 0.15f
              && Enumerable.Range(0, 28).Any(k => Texts(Scene(beats.StallStart + 0.005f + k * 0.01f)).Contains(Words.Stall)) && !Texts(Scene(1.0f)).Contains(Words.Stall),
            "the bar fills from empty to full with a short stall, its word blinking only then");
        var testing = Scene(1.0f);
        var texts = Texts(testing);
        var plate = Items(testing).Where(i => i.Kind == BootMessageItemKind.Plate).ToList();
        var ok = Items(testing).Where(i => i.Kind == BootMessageItemKind.Text && i.Text == Words.Ok).ToList();
        check(texts.Contains("SEAL") && texts.Contains("ARMOR 080%") && ok.Count >= 3 && ok.All(i => i.Bold && i.Y < -30f) && !texts.Contains(Words.Message)
              && plate.Count == 1 && plate[0].Colour.A < 0.8f && plate[0].T > 0f,
            "the self-test types on line by line under the bar (so under the message that locks on its line) with a bold OK stamp each, on one dark cut-corner plate, while the message waits");
        var full = Scene(beats.BarFull + 0.01f);
        check(Texts(full).Contains("100%") && Items(full).Where(i => i.Kind == BootMessageItemKind.Cell).All(i => i.Colour.R > 0.9f && i.Colour.G > 0.9f), "the bar fills to 100 % and flashes bright");
        var split = Items(Scene((beats.MessageStart + beats.MessageLock) / 2f)).Where(i => i.Kind == BootMessageItemKind.Text && i.Colour.R > 0.9f && i.Colour.G < 0.5f).ToList();
        check(split.Count == 1 && split[0].Bold && Math.Abs(split[0].X) > 1f, "the message decodes split in two colours");
        var flash = Items(Scene(beats.MessageLock + 0.01f)).First(i => i.Kind == BootMessageItemKind.Text && i.Text == Words.Message && i.Colour.A >= 0.99f);
        var locked = Items(Scene(beats.MessageLock + 0.2f)).Where(i => i.Kind == BootMessageItemKind.Text && i.Text == Words.Message).ToList();
        check(flash.Colour.R > 0.95f && flash.Colour.G > 0.95f && locked.Count >= 2 && locked.All(i => i.Bold && Math.Abs(i.Y) < 1e-3f),
            "it locks with a white flash and stands bold, centred, with no split");
        var fold = Scene(beats.FoldStart + 0.2f);
        check(Texts(fold).Count == 0 && Items(fold).Count(i => i.Kind == BootMessageItemKind.Rect) == 1, "it folds into a line and shrinks to a dot");
        var decodingDown = Texts(Scene(99f, (beats.DownStart + beats.DownDecoded) / 2f));
        var downWord = Items(Scene(99f, beats.DownDecoded + 0.02f)).Where(i => i.Kind == BootMessageItemKind.Text && i.Text == Words.Down).ToList();
        check(Scene(99f, beats.DownStart - 0.01f).Count == 0 && decodingDown.Count > 0 && decodingDown.All(t => t != Words.Down && t != Words.Message)
              && downWord.Count == 3 && downWord.Any(i => i.Colour.R > 0.95f && i.Colour.G < 0.3f && Math.Abs(i.X) < 1e-3f && Math.Abs(i.Y) < 1e-3f)
              && Scene(99f, beats.DownEnd).Count == 0,
            "powering down, the power-down word decodes in the middle in its colour, holds and is gone at its end; the power-on draws nothing then");
        var few = new BootMessage(beats, new BootMessageLook());
        few.Prepare(new BootMessageWords { Message = "GO", Checks = new[] { "A", "B", "C", "D", "E", "F", "G" } });
        few.Build(1.2f, -1f);
        check(Texts(few).Count(t => t is "A" or "B" or "C" or "D" or "E" or "F" or "G") == beats.CheckStarts.Length, "self-test lines beyond the beats are not shown");

        var reused = Scene(0.5f);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var k = 0; k < 230; k++) reused.Build(k * 0.01f, -1f);
        check(GC.GetAllocatedBytesForCurrentThread() - before < 1024, "building the message each frame allocates nothing once prepared");
    }

    private static void Segments(Action<bool, string> check)
    {
        var bar = new SegmentBarMotion();
        bar.Update(1f, 0.016f, 0f, 7f, 10, false, 0.1f, 100f);
        check(bar.Shown == 1f && bar.Readout == "100" && bar.Alarm == 0f, "a new bar shows its value at once, numbered on its maximum");
        bar.Update(0.6f, 0.05f, 0.05f, 7f, 10, false, 0.1f, 100f);
        var between = bar.Shown is > 0.6f and < 1f;
        for (var t = 0.05f; t < 1.5f; t += 0.016f) bar.Update(0.6f, 0.016f, t, 7f, 10, false, 0.1f, 100f);
        var readout = bar.Readout;
        bar.Update(0.6f, 0.016f, 1.6f, 7f, 10, false, 0.1f, 100f);
        check(between && bar.Shown == 0.6f && readout == "60" && ReferenceEquals(readout, bar.Readout), "the fill eases to a hit and lands on it; an unchanged number keeps its readout");
        bar.RollUp();
        bar.Update(0.6f, 0.016f, 1.62f, 7f, 10, false, 0.1f, 100f);
        var rolling = bar.Shown < 0.2f;
        for (var t = 1.62f; t < 3f; t += 0.016f) bar.Update(0.6f, 0.016f, t, 7f, 10, false, 0.1f, 100f);
        check(rolling && bar.Shown == 0.6f, "rolling up starts from 0 and settles on the value");

        // Critical: a flash on going critical and on each segment lost, steady between, without pause under the fail line.
        var low = new SegmentBarMotion();
        low.Update(0.3f, 0.016f, 10f, 7f, 10, false, 0.1f, 0f);
        low.Update(0.24f, 0.016f, 10.05f, 7f, 10, true, 0.1f, 0f);
        var flashes = new HashSet<bool>();
        for (var t = 10.05f; t < 10.05f + GaugeMath.FlashSeconds; t += 0.02f) { low.Update(0.24f, 0.016f, t, 7f, 10, true, 0.1f, 0f); flashes.Add(low.Alarm > 0.5f); }
        low.Update(0.24f, 0.016f, 12f, 7f, 10, true, 0.1f, 0f);
        var steady = low.Alarm == 0f;
        low.Update(0.23f, 0.016f, 13f, 7f, 10, true, 0.1f, 0f);
        var same = low.Alarm == 0f;
        low.Update(0.18f, 0.016f, 14f, 7f, 10, true, 0.1f, 0f);
        var next = low.Alarm > 0f;
        var failing = new HashSet<bool>();
        for (var t = 20f; t < 22f; t += 0.03f) { low.Update(0.08f, 0.016f, t, 7f, 10, true, 0.1f, 0f); failing.Add(low.Alarm > 0.5f); }
        check(flashes.Count == 2 && steady && same && next && failing.Count == 2 && low.Readout == "",
            "critical flashes briefly, then holds; flashes again per segment lost; keeps flashing under the fail line; no readout without a maximum");
        low.Restart();
        low.Update(0.5f, 0.016f, 30f, 7f, 10, false, 0.1f, 0f);
        check(low.Shown == 0.5f && low.Alarm == 0f, "restarting shows a new subject's value at once");
    }

    private static void Flare(Action<bool, string> check)
    {
        var flare = new ArcFlare();
        check(flare.Alpha(MeterTone.Normal, false, 0f, 0.7f, 0.8f) == 0.7f, "an arc rests at its steady opacity");
        check(flare.Alpha(MeterTone.Warn, false, 1f, 0.7f, 0.8f) == 1f && flare.Alpha(MeterTone.Warn, false, 1.5f, 0.7f, 0.8f) == 1f
              && flare.Alpha(MeterTone.Warn, false, 1.9f, 0.7f, 0.8f) == 0.7f, "a worse tone flares it to full for a moment");
        check(flare.Alpha(MeterTone.Warn, true, 2f, 0.7f, 0.8f) == 1f && flare.Alpha(MeterTone.Normal, true, 3f, 0.7f, 0.8f) == 0.7f, "starting a refill flares it once");
        check(flare.Alpha(MeterTone.Critical, false, 9f, 0.7f, 0.8f) == 1f && flare.Alpha(MeterTone.Critical, false, 20f, 0.7f, 0.8f) == 1f, "critical stays at full");
        flare.Flare(30f, 0.8f);
        check(flare.Alpha(MeterTone.Normal, false, 30.5f, 0.7f, 0.8f) == 1f, "the owner can flare it");
    }

    private static void Shares(Action<bool, string> check)
    {
        var shares = new DamageShares(4);
        check(shares.Shown(0, 0.5f) == 0f, "nothing hit shows nothing");
        shares.Add(0, 30f);
        shares.Add(2, 10f);
        shares.Add(9, 99f);
        check(Math.Abs(shares.Shown(0, 0.6f) - 0.6f) < 1e-5f && Math.Abs(shares.Shown(2, 0.6f) - 0.2f) < 1e-5f && shares.Shown(1, 0.6f) == 0f,
            "the most hit side shows the whole loss, the others their share of it");
        shares.Reset();
        check(shares.Shown(0, 0.6f) == 0f, "a reset forgets every hit");
    }
}

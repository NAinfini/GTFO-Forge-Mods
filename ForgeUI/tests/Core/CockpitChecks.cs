using EnemyAlert = ForgeCore.EnemyAlert;
using System;
using System.Collections.Generic;
using ForgeUI;

/// <summary>The cockpit look's pure parts: shared glow and fringe, sliced shapes, curved and slanted bars, cell gauges, contacts in
/// and out of view, the visor's struts, shade, heat and crack layers, and the power-up timeline.</summary>
internal static class CockpitChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Effects(check);
        Reveal(check);
        Shapes(check);
        Bars(check);
        Contacts(check);
        Visor(check);
        Sequence(check);
    }

    private static bool Near(float a, float b, float e = 1e-4f) => Math.Abs(a - b) < e;

    /// <summary>An element coming online: brackets trace, a scan line sweeps along the flow (from the middle for a centred one), a lock flash ends it.</summary>
    private static void Reveal(Action<bool, string> check)
    {
        check(HudReveal.Running(0f) && !HudReveal.Running(HudReveal.Seconds) && !HudReveal.Running(-0.01f) && HudReveal.Trace(HudReveal.TraceEnd) == 1f
              && HudReveal.Sweep(HudReveal.SweepStart) == 0f && HudReveal.Sweep(HudReveal.SweepEnd) == 1f && HudReveal.Lock(HudReveal.LockStart - 0.01f) == 0f
              && HudReveal.Lock(HudReveal.LockStart) == 1f && HudReveal.Lock(HudReveal.Seconds) == 0f && HudReveal.Seconds <= 0.45f,
            "coming online takes under half a second: trace, sweep, then the lock flash");
        HudReveal.Extent(1, 100f, out var l1, out var h1);
        HudReveal.Extent(-1, 100f, out var l2, out var h2);
        HudReveal.Extent(0, 100f, out var l3, out var h3);
        check(l1 == 0f && h1 == 100f && l2 == -100f && h2 == 0f && l3 == -50f && h3 == 50f, "the element's box follows its region's flow from the root");
        check(HudReveal.Passed(1, 0f, 100f, 0.5f, 10f, 20f) == 1f && HudReveal.Passed(1, 0f, 100f, 0.5f, 60f, 70f) == 0f
              && HudReveal.Passed(-1, -100f, 0f, 0.5f, -20f, -10f) == 1f && HudReveal.Passed(-1, -100f, 0f, 0.5f, -70f, -60f) == 0f
              && Near(HudReveal.Passed(1, 0f, 100f, 0.5f, 40f, 60f), 0.5f) && HudReveal.Passed(0, -50f, 50f, 0.5f, -5f, 5f) == 1f
              && HudReveal.Passed(0, -50f, 50f, 0.5f, 40f, 45f) == 0f && HudReveal.Passed(1, 0f, 100f, 1f, 90f, 99f) == 1f,
            "the scan line shows what it passed: from the root's side, from the middle out for a centred element, a text typing on as it passes");
    }

    private static void Effects(Action<bool, string> check)
    {
        var glow = new HudGlowSettings();
        check(Near(glow.Effective(1f), 0.55f) && glow.Effective(0f) == 0f && Near(glow.Effective(2f), 1.1f), "the glow follows the player's multiplier");
        glow.Enabled = false;
        check(glow.Effective(1f) == 0f, "a disabled glow draws nothing");
        var fringe = new HudFringeSettings();
        check(Near(fringe.Effective(true, 0f), 0.16f) && fringe.Effective(false, 0f) == 0f, "the fringe is 16 % and the player can turn it off");
        fringe.Pulse(0.2f);
        check(Near(fringe.Boost(10f), 3f) && Near(fringe.Boost(10.1f), 2f) && Near(fringe.Boost(10.2f), 1f, 1e-3f) && fringe.Boost(11f) == 1f,
            "a pulse starts at three times and falls back over its seconds");
    }

    private static void Shapes(Action<bool, string> check)
    {
        const int s = ShapePatterns.Size, b = ShapePatterns.Border;
        check(ShapePatterns.ChamferFill(s / 2f, s / 2f) == 1f && ShapePatterns.ChamferFill(0.5f, s - 0.5f) == 0f && ShapePatterns.ChamferFill(s - 0.5f, 0.5f) == 0f
              && ShapePatterns.ChamferFill(s - 0.5f, s - 0.5f) == 1f && ShapePatterns.ChamferFill(0.5f, 0.5f) == 1f,
            "a chamfered plate is cut at its top left and bottom right only");
        check(ShapePatterns.ChamferLine(s / 2f, s / 2f) == 0f && ShapePatterns.ChamferLine(s / 2f, 0.5f) == 1f, "a chamfered outline is hollow");
        check(ShapePatterns.SkewFill(0.5f, s - 0.5f) == 0f && ShapePatterns.SkewFill(b + 1.5f, s - 0.5f) == 1f && ShapePatterns.SkewFill(s - 0.5f, 0.5f) == 0f
              && ShapePatterns.SkewFill(s / 2f, s / 2f) == 1f, "a slanted segment leans right inside its borders");
        const int g = ShapePatterns.GlowSize, e = ShapePatterns.GlowEdge;
        check(ShapePatterns.GlowFill(g / 2f, g / 2f) == 1f && ShapePatterns.GlowFill(0.5f, g / 2f) < 0.05f && ShapePatterns.GlowFill(e - 3f, g / 2f) > 0.3f,
            "a soft box is solid inside and falls off outside its edge");
        check(ShapePatterns.GlowRing(g / 2f, g / 2f) == 0f && ShapePatterns.GlowRing(e, g / 2f) > 0.9f, "a soft ring glows on the edge and leaves the middle clear");
    }

    private static void Bars(Action<bool, string> check)
    {
        // A bar's shield layer: a drop flashes it, a drop to empty flashes it brighter and longer; nothing flashes while it is hidden
        // or on its first reading; its number is the drawn fill times its points, rounded up.
        var hits = new LayerHits();
        hits.Update(1f, true, 0f);
        var first = hits.Flash(0f);
        hits.Update(0.6f, true, 1f);
        var hit = hits.Flash(1f);
        var faded = hits.Flash(1f + LayerHits.HitSeconds + 0.01f);
        hits.Update(0f, true, 2f);
        var broke = hits.Flash(2f);
        var breakLasts = hits.Flash(2f + LayerHits.HitSeconds) > 0f && hits.Flash(2f + LayerHits.BreakSeconds + 0.01f) == 0f;
        hits.Update(1f, false, 3f);
        hits.Update(0.2f, false, 4f);
        check(first == 0f && hit > 0f && faded == 0f && broke > hit && breakLasts && hits.Flash(4f) == 0f,
            "the shield layer flashes on a hit, brighter and longer on a break, never while hidden or on its first reading");
        var points = new SegmentBarMotion();
        points.Update(0.48f, 0f, 0f, 14f, 10, false, -1f, 25f);
        var twelve = points.Readout;
        points.Update(0f, 0f, 1f, 14f, 10, false, -1f, 25f);
        check(twelve == "12" && points.Readout == "12", "the shield number reads its points (12 of 25 at 48 %) and rolls down with the drawn layer");
        check(GaugeMath.Droop(0.5f, 7f) == 0f && Near(GaugeMath.Droop(0f, 7f), 7f) && Near(GaugeMath.Droop(1f, 7f), 7f) && GaugeMath.Droop(0.25f, 7f) < 7f,
            "a curved bar is highest in the middle and 7 lower at both ends");
        check(GaugeMath.ArcCells(30, 40) == 30 && GaugeMath.ArcCells(120, 40) == 40 && GaugeMath.ArcCells(120, 0) == 120 && GaugeMath.ArcCells(0, 40) == 0,
            "an arc has a cell per round up to its limit, merged above it");
        // The teammate ammunition gauge: four cells of 25 %, lit from the bottom, the partial cell in proportion.
        foreach (var (value, lit, partial, state) in new[] { (0f, 0, 0f, 2), (0.12f, 0, 0.48f, 1), (0.25f, 1, 0f, 0), (0.6f, 2, 0.4f, 0), (1f, 4, 0f, 0) })
        {
            var full = 0;
            var part = 0f;
            for (var i = 0; i < 4; i++)
            {
                var level = GaugeMath.Segment(value, i, 4);
                if (level >= 1f) full++;
                else if (level > 0f) part = level;
            }
            check(full == lit && Near(part, partial) && GaugeMath.CellState(value, 0.25f) == state,
                "ammunition cells at " + value * 100f + " %: " + lit + " lit, the next at " + partial + ", state " + state);
        }
        check(Near(TeammateAmmo.Rel(150f, 600f, 0f, 0f), 0.25f) && GaugeMath.CellState(TeammateAmmo.Rel(150f, 600f, 0f, 0f), 0.25f) == 0,
            "without a special weapon the main weapon alone fills the gauge");
    }

    private static void Contacts(Action<bool, string> check)
    {
        var half = Bearing.HalfHorizontalFov(70f, 16f / 9f);
        check(half > 50f && half < 60f, "a 70° camera on 16:9 sees about 55° either side");
        check(Bearing.InView(30f, half) && Bearing.InView(-30f, half) && !Bearing.InView(90f, half) && !Bearing.InView(180f, half), "contacts ahead are in view, beside and behind are not");
        var contacts = new BearingContacts(8);
        contacts.Add(180f, 1f, EnemyAlert.Hunting);
        contacts.Add(150f, 1f, EnemyAlert.Hunting);
        contacts.Add(-145f, 1f, EnemyAlert.Hunting);
        contacts.Add(175f, 1f, EnemyAlert.Awake);
        contacts.Add(90f, 1f, EnemyAlert.Hunting);
        check(contacts.Behind(EnemyAlert.Hunting, 40f) == 3 && contacts.Behind(EnemyAlert.Awake, 40f) == 1 && contacts.State(3) == EnemyAlert.Awake,
            "hunting contacts within 40° of straight behind are counted by state");
    }

    private static void Visor(Action<bool, string> check)
    {
        check(VisorMath.CracksFor(0.5f) == 0f && Near(VisorMath.CracksFor(0.4f), 1f / 3f) && Near(VisorMath.CracksFor(0.2f), 2f / 3f) && VisorMath.CracksFor(0.05f) == 1f,
            "crack layers come in at 40, 20 and 10 % armour");
        check(VisorMath.CrackLayer(1f / 3f, 0) > 0.99f && VisorMath.CrackLayer(1f / 3f, 1) < 0.01f && VisorMath.CrackLayer(1f, 2) == 1f, "each third of the cracks brings in one layer");
        check(ScreenPatterns.CrackLayer(0.98f, 0.02f, 0) == 1f && ScreenPatterns.CrackLayer(0.98f, 0.02f, 2) == 0f && ScreenPatterns.CrackLayer(0.02f, 0.98f, 2) == 1f,
            "the first crack layer is at the impact corner, the last far from it");
        var cover = 0f;
        for (var u = 0.05f; u < 1f; u += 0.1f)
            for (var v = 0.05f; v < 1f; v += 0.1f)
            {
                var sum = ScreenPatterns.CrackLayer(u, v, 0) + ScreenPatterns.CrackLayer(u, v, 1) + ScreenPatterns.CrackLayer(u, v, 2);
                cover = Math.Max(cover, Math.Abs(sum - 1f));
            }
        check(cover < 0.6f, "the crack layers together cover the picture");
        check(ScreenPatterns.Vignette(0.5f, 0.5f) == 0f && ScreenPatterns.Vignette(0.6f, 0.6f) == 0f && ScreenPatterns.Vignette(0f, 0f) > 0.95f, "the shade keeps the middle clear and darkens the corners");
        var (u0, v0) = VisorMath.At1080(392f, 420f, 16f / 9f);
        check(Near(u0, 392f / 1920f) && Near(v0, 1f - 420f / 1080f), "a 1080p point lands on the same place of a 16:9 screen");
        var (wide, _) = VisorMath.At1080(392f, 420f, 32f / 9f);
        check(Near((0.5f - wide) * 32f / 9f, (0.5f - 392f / 1920f) * 16f / 9f), "the middle window keeps its width on an ultrawide screen");
        check(VisorMath.StrutDrop(1f) == 0f && VisorMath.StrutDrop(0f) > 0.2f, "the struts slide down into place");
    }

    private static void Sequence(Action<bool, string> check)
    {
        var a = -1f;
        var b = -1f;
        var finished = 0;
        var sequence = new HudSequence().Add(0f, 0.6f, v => a = v).Add(0.6f, 1.2f, v => b = v);
        sequence.Finished += () => finished++;
        check(Near(sequence.Length, 1.8f), "a timeline is as long as its last track");
        sequence.Play(10f);
        check(a == 0f && b == 0f && sequence.Playing, "playing starts every track at 0");
        sequence.Tick(10.3f);
        check(a > 0f && a < 1f && b == 0f, "a track runs in its own span");
        sequence.Tick(11f);
        check(a == 1f && b > 0f && b < 1f, "earlier tracks hold their end");
        sequence.Skip();
        check(a == 1f && b == 1f && !sequence.Playing && finished == 1, "skipping jumps to the end and finishes once");
        sequence.Play(20f, backwards: true);
        check(a == 1f && b == 1f, "playing backwards starts from the end");
        sequence.Tick(20.9f);
        check(a == 1f && b < 1f, "backwards, the last track goes first");
        sequence.Tick(30f);
        check(a == 0f && b == 0f && !sequence.Playing && finished == 2, "backwards ends with everything off");
        check(HudSequence.Progress(5f, 5f, 0f, true) == 1f && HudSequence.Progress(4.9f, 5f, 0f, true) == 0f, "a zero-length track is a step");
    }
}

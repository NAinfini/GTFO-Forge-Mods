using EnemyAlert = ForgeCore.EnemyAlert;
using ForgeUI;

/// <summary>The cockpit capabilities' logic: bearings, hit flashes, the visor, gauges, contacts, fading prompts, game HUD claims,
/// screen patterns, the top-right region, text wrapping.</summary>
internal static class CapabilityChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Bearings(check);
        Flashes(check);
        Gauges(check);
        Prompts(check);
        Claims(check);
        Patterns(check);
        Wrapping(check);
        Regions(check);
    }

    private static void Regions(Action<bool, string> check)
    {
        var screen = new HudBox(-960f, -540f, 960f, 540f);
        var layout = new HudLayout();
        var gun = new LayoutEntry("F/Gun", new HudSlot(HudRegion.TopRight, 0, 300f, 64f)) { Visible = true };
        layout.Add(gun);
        layout.Run(new HudScene { Screen = screen, Margin = 48f });
        check(gun.State == LayoutState.Placed && Math.Abs(gun.Box.Right - 912f) < 1e-3f && Math.Abs(gun.Box.Top - 492f) < 1e-3f,
            "the top right region sits in the corner inside the safe margin");
        // A game weapon list moved into that corner: occupied, the corner element has to give way; hidden by a mod, it is free room.
        var list = new HudBox(700f, 300f, 950f, 500f);
        layout.Run(new HudScene { Screen = screen, Margin = 48f, WeaponList = list });
        var blocked = gun.State != LayoutState.Placed || gun.Box.Top <= 300f;
        layout.Run(new HudScene { Screen = screen, Margin = 48f, WeaponList = list, WeaponListFree = true });
        check(blocked && gun.State == LayoutState.Placed && Math.Abs(gun.Box.Top - 492f) < 1e-3f, "a hidden weapon list frees its room");
    }

    private static bool Near(float a, float b, float e = 1e-3f) => Math.Abs(a - b) <= e;

    private static void Bearings(Action<bool, string> check)
    {
        check(Near(Bearing.Relative(0f, 1f, 1f, 0f), 90f), "a point to the right is at 90");
        check(Near(Bearing.Relative(0f, 1f, -1f, 0f), -90f), "a point to the left is at -90");
        check(Near(Math.Abs(Bearing.Relative(0f, 1f, 0f, -1f)), 180f), "a point behind is at 180");
        check(Near(Bearing.Wrap(540f), 180f) || Near(Bearing.Wrap(540f), -180f), "angles wrap");
        check(Near(Bearing.Between(170f, -170f), 20f), "the gap across the back is short");
        var (u, v, _) = Bearing.EdgePoint(0f, 16f / 9f);
        check(Near(u, 0.5f) && Near(v, 1f), "ahead is the middle of the top edge");
        (u, v, _) = Bearing.EdgePoint(180f, 16f / 9f);
        check(Near(u, 0.5f) && Near(v, 0f), "behind is the middle of the bottom edge");
        (u, _, _) = Bearing.EdgePoint(90f, 21f / 9f);
        check(Near(u, 1f), "right is the right edge on an ultrawide screen too");
        check(Bearing.Quadrant(10f) == 0 && Bearing.Quadrant(170f) == 1 && Bearing.Quadrant(-90f) == 2 && Bearing.Quadrant(90f) == 3,
            "hit quadrants: front, back, left, right");
        var (_, _, inside) = Bearing.ArcPoint(120f, 100f, 180f);
        check(!inside, "a contact outside a half arc is pressed to its end");
    }

    private static void Flashes(Action<bool, string> check)
    {
        var flashes = new EdgeFlashes { Seconds = 0.3f };
        flashes.Hit(10f, 0.5f, 0f);
        flashes.Hit(25f, 0.8f, 0.01f);
        var live = 0;
        for (var i = 0; i < EdgeFlashes.Slots; i++) if (flashes.Live(i, 0.02f)) live++;
        check(live == 1, "nearby hits join");
        flashes.Hit(205f, 0.8f, 0.02f);
        live = 0;
        for (var i = 0; i < EdgeFlashes.Slots; i++) if (flashes.Live(i, 0.03f)) live++;
        check(live == 2, "a hit from another side is its own flash");
        check(!flashes.Any(1f), "a short flash is gone well within a second");
        for (var i = 0; i < 10; i++) flashes.Hit(i * 36f, 1f, 2f);
        live = 0;
        for (var i = 0; i < EdgeFlashes.Slots; i++) if (flashes.Live(i, 2.01f)) live++;
        check(live == EdgeFlashes.Slots, "at most six flashes at once");
        flashes.Hit(float.NaN, 1f, 3f);
        flashes.Clear();
        check(!flashes.Any(3f), "clear turns everything off");
        foreach (var aspect in new[] { 4f / 3f, 16f / 9f, 32f / 9f })
        {
            var inside = true;
            foreach (var set in new[] { VisorMath.Struts(aspect), VisorMath.Brackets(aspect) })
                foreach (var s in set)
                    inside &= s[0] >= 0f && s[0] <= 1f && s[1] >= 0f && s[1] <= 1f && s[2] >= 0f && s[2] <= 1f && s[3] >= 0f && s[3] <= 1f;
            check(inside && VisorMath.Struts(aspect).Length == 6 && VisorMath.Brackets(aspect).Length == 12, "the visor struts and brackets stay on the screen at aspect " + aspect);
        }
    }

    private static void Gauges(Action<bool, string> check)
    {
        check(GaugeMath.Segment(0.45f, 0, 10) == 1f && Near(GaugeMath.Segment(0.45f, 4, 10), 0.5f) && GaugeMath.Segment(0.45f, 5, 10) == 0f,
            "segments fill in order, the last one in part");
        check(GaugeMath.CriticalAlpha(MeterPulse.Pulse, -1f, 3f) == 1f, "not critical draws at full strength");
        check(GaugeMath.CriticalAlpha(MeterPulse.FlashThenSteady, 2f, 5f) == 1f, "flash then steady settles");
        check(GaugeMath.CriticalAlpha(MeterPulse.None, 0.1f, 0.1f) == 1f, "no pulse never blinks");
        check(Near(GaugeMath.ArcAngle(ArcSide.Right, 100f, 0f), -50f) && Near(GaugeMath.ArcAngle(ArcSide.Right, 100f, 1f), 50f),
            "the right arc fills from the bottom up");
        check(Near(GaugeMath.ArcAngle(ArcSide.Left, 100f, 0f), 230f) && Near(GaugeMath.ArcAngle(ArcSide.Left, 100f, 1f), 130f),
            "the left arc fills from the bottom up");
        check(Near(GaugeMath.Refill(0.2f, 0f), 0.2f) && Near(GaugeMath.Refill(0.2f, 1f), 1f), "a refill sweeps from the value to full");
        check(GaugeMath.DamageBand(0.33f) == 0 && GaugeMath.DamageBand(0.34f) == 1 && GaugeMath.DamageBand(0.66f) == 1 && GaugeMath.DamageBand(0.67f) == 2,
            "damage bands at a third and two thirds");
        check(GaugeMath.DistanceFade(30f, 20f, 40f) == 0.5f && GaugeMath.DistanceFade(100f, 0f, 0f) == 1f, "markers fade with distance");
        var contacts = new BearingContacts(2);
        check(contacts.Add(400f, 2f, EnemyAlert.Awake) && Near(contacts.Azimuth(0), 40f) && contacts.Strength(0) == 1f, "contacts are wrapped and clamped");
        check(!contacts.Add(float.NaN, 1f, EnemyAlert.Sleeping) && contacts.Add(0f, 0f, EnemyAlert.Sleeping) && !contacts.Add(0f, 0f, EnemyAlert.Sleeping), "contacts stop when full");
        contacts.Begin();
        check(contacts.Count == 0, "a new set starts empty");
    }

    private static void Prompts(Action<bool, string> check)
    {
        var fade = new FamiliarFade(10f, 0.3f);
        var alpha = fade.Update(0f, 0.016f, false);
        check(alpha == 1f, "a new prompt shows at full strength");
        for (var t = 0f; t < 13f; t += 0.1f) alpha = fade.Update(t, 0.1f, false);
        check(Near(alpha, 0.3f, 0.02f), "an unused prompt fades after its idle time");
        alpha = fade.Update(13.1f, 0.1f, true);
        for (var t = 13.2f; t < 14f; t += 0.1f) alpha = fade.Update(t, 0.1f, true);
        check(alpha > 0.9f, "holding the key brings it back");
        fade.Hidden();
        check(fade.Update(20f, 0.1f, false) == 1f, "showing it again starts over");
        fade.Used(20f);
        for (var t = 20.1f; t < 23f; t += 0.1f) alpha = fade.Update(t, 0.1f, false);
        check(alpha < 0.35f, "a used prompt fades soon after");
        fade.Attention(23f);
        for (var t = 23.1f; t < 24f; t += 0.1f) alpha = fade.Update(t, 0.1f, false);
        check(alpha > 0.9f, "attention brings it back");
    }

    private static void IconFolderChecks(Action<bool, string> check)
    {
        var files = new[] { "skill-charge.png", "skill-charge@2x.png", "damage-front.png", "mark-b.png", "mark-a@2x.png", "mark-a.png", "readme.txt" };
        check(IconFolder.Pick(files, "skill-charge", true) == "skill-charge@2x.png", "a tall screen reads the @2x file");
        check(IconFolder.Pick(files, "skill-charge", false) == "skill-charge.png", "a normal screen reads the plain file");
        check(IconFolder.Pick(files, "mark-b", true) == "mark-b.png", "only a plain file is read on a tall screen");
        check(IconFolder.Pick(new[] { "x@2x.png" }, "x", false) == "x@2x.png", "only an @2x file is read on a normal screen");
        check(IconFolder.Pick(files, "damage-back", false) == null, "no file, no pick");
        check(!IconFolder.UseDouble(1440) && IconFolder.UseDouble(1441), "@2x above 1440 lines");
        var names = IconFolder.Names(files, new[] { "skill-charge", "damage-back", "mark-*", "skill-charge" });
        check(names.SequenceEqual(new[] { "skill-charge", "damage-back", "mark-a", "mark-b" }), "plain names first and once, then the prefix matches sorted with @2x folded in");
    }

    private static void Claims(Action<bool, string> check)
    {
        var claims = new GameHudClaims();
        var a = claims.AddHide("A", GameHudPart.NavMarkers | GameHudPart.Chat);
        var b = claims.AddHide("B", GameHudPart.All & ~GameHudPart.Crosshair);
        check(claims.Hidden == GameHudPart.None && !claims.AnyActive, "nothing is hidden before a claim is active");
        claims.SetActive(b, true);
        check(claims.Hidden == GameHudPart.Crosshair, "a claim hides what it does not keep");
        claims.SetActive(a, true);
        check(claims.Hidden == (GameHudPart.All & ~(GameHudPart.NavMarkers | GameHudPart.Chat)), "claims add up");
        claims.SetActive(a, false);
        check(claims.Hidden == GameHudPart.Crosshair, "releasing one leaves the other's");
        var low = claims.AddChatMove("Z", 10);
        var high = claims.AddChatMove("Y", 20);
        claims.SetActive(low, true);
        claims.SetActive(high, true);
        check(ReferenceEquals(claims.ChatWinner, high) && claims.Hidden == GameHudPart.Crosshair, "the higher chat move wins and hides nothing");
        var released = new List<string>();
        claims.ReleaseAll(c => released.Add(c.Id));
        check(released.Count == 3 && !claims.AnyActive && claims.Hidden == GameHudPart.None && claims.ChatWinner == null,
            "releasing all restores the game and tells every active claim");
        claims.Remove(b);
        claims.SetActive(a, true);
        check(claims.Hidden == (GameHudPart.All & ~(GameHudPart.NavMarkers | GameHudPart.Chat)), "a removed claim no longer counts");
        check((int)GameHudPart.All == 65535, "every part is in All");
        var pilot = new GameHudClaims();
        var hide = pilot.AddHide("Pilot", GameHudClaims.DefaultKeep);
        pilot.SetActive(hide, true);
        // A cockpit-style HUD (the exosuit's) hides with the default keep: what the game draws on the world and at the crosshair stays
        // the game's, every flat screen part is redrawn by the mod from the readouts.
        check((pilot.Hidden & (GameHudPart.NavMarkers | GameHudPart.CrosshairFeedback | GameHudPart.Chat)) == GameHudPart.None,
            "the default hide keeps the world markers (pings, name tags, beacons), the crosshair's hit and ping marks and the chat");
        var redrawn = GameHudPart.Interaction | GameHudPart.Objectives | GameHudPart.Vitals | GameHudPart.WeaponList | GameHudPart.Crosshair
            | GameHudPart.Subtitles | GameHudPart.Teammates | GameHudPart.Compass;
        check((pilot.Hidden & redrawn) == redrawn, "the default hide takes the prompt and message line, objectives and timer, vitals, weapons, crosshair, subtitles, squad and compass");
        check((pilot.Hidden & GameHudPart.Stamina) != GameHudPart.None, "the default hide takes the stamina read-out");
        pilot.SetKeep(hide, GameHudPart.Stamina);
        check((pilot.Hidden & GameHudPart.Stamina) == GameHudPart.None, "a claim can keep it");
        var only = new GameHudClaims();
        var vitals = only.AddHideOnly("Vitals", GameHudPart.Vitals);
        var sweep = only.AddHide("Sweep", GameHudPart.All & ~GameHudPart.Compass); // keeps all but the compass: hides the compass
        only.SetActive(vitals, true);
        check(only.Hidden == GameHudPart.Vitals, "a hide-only claim hides just the parts it names");
        only.SetActive(sweep, true);
        check(only.Hidden == (GameHudPart.Vitals | GameHudPart.Compass), "hide-only and keep-list claims add up through one mask");
        only.SetActive(sweep, false);
        check(only.Hidden == GameHudPart.Vitals && vitals.Keep == (GameHudPart.All & ~GameHudPart.Vitals), "releasing the other leaves the named part hidden; Keep is the rest");
        only.SetActive(vitals, false);
        check(only.Hidden == GameHudPart.None, "releasing the last claim of a part gives it back");
        IconFolderChecks(check);
    }

    private static void Patterns(Action<bool, string> check)
    {
        check(ScreenPatterns.CentreClear(0.5f, 0.5f) == 0f && ScreenPatterns.CentreClear(0.6f, 0.55f) == 0f, "the middle 40 % stays clear");
        check(ScreenPatterns.CentreClear(0.97f, 0.97f) == 1f && ScreenPatterns.CentreClear(0.5f, 0.99f) > 0.5f, "the edges are not masked");
        check(ScreenPatterns.HeatGlow(0.5f, 0.5f) == 0f && ScreenPatterns.HeatGlow(0.5f, 0.01f) > 0.5f && ScreenPatterns.HeatGlow(0.005f, 0.5f) == 0f
              && ScreenPatterns.HeatGlow(0.5f, 0.99f) == 0f, "heat glows up from the bottom edge only");
        var centreLines = 0f;
        for (var row = 0; row < 60; row++) centreLines = Math.Max(centreLines, ScreenPatterns.Interference(0.5f, 0.5f, row));
        var edgeLines = 0f;
        for (var row = 0; row < 60; row++) edgeLines = Math.Max(edgeLines, ScreenPatterns.Interference(0.02f, 0.5f, row));
        check(centreLines == 0f && edgeLines > 0.3f, "interference shows on the edges, never in the middle");
        check(ScreenPatterns.SweepTrail(0f) == 1f && ScreenPatterns.SweepTrail(0.5f) < 0.1f && ScreenPatterns.SweepTrail(1f) == 0f, "the sweep line is bright, its trail faint");
        check(ScreenPatterns.GlowBand(0.5f) == 1f && ScreenPatterns.GlowBand(0f) < 1e-4f && ScreenPatterns.GlowBand(1f) < 1e-4f &&
              ScreenPatterns.GlowBand(0.2f) > 0f && ScreenPatterns.GlowBand(0.2f) < 1f, "a band glow is full in the middle and gone at both edges");
        check(ScreenPatterns.GlowRing(0.5f + 0.39f, 0.5f) > 0.99f && ScreenPatterns.GlowRing(0.5f, 0.5f) == 0f && ScreenPatterns.GlowRing(1f, 0.5f) == 0f &&
              ScreenPatterns.GlowRing(0f, 0f) == 0f, "a ring glow is brightest on its circle, dark inside and at the edge");
        check(ScreenPatterns.GlowBlob(0.5f, 0.5f) == 1f && ScreenPatterns.GlowBlob(1f, 0.5f) == 0f && ScreenPatterns.GlowBlob(0f, 1f) == 0f,
              "a blob glow fades from the middle to nothing at the sides and corners");
        check(NumberText.Tabular("42") == "<mspace=0.6em>42</mspace>", "tabular digits are monospaced");
        var edge = 0f;
        for (var x = 0; x < 64; x++) edge = Math.Max(edge, ScreenPatterns.EdgeFlash(x, 1, 64, 16));
        check(edge > 0.5f && ScreenPatterns.EdgeFlash(32, 15, 64, 16) < 0.2f, "a hit edge is bright at the edge and fades inwards");
    }

    private static void Wrapping(Action<bool, string> check)
    {
        var lines = new List<string>();
        check(TextWrap.Lines("", 16f, 200f, 3, lines) == 0, "no text, no lines");
        TextWrap.Lines("Reach the extraction point in zone forty five before the alarm ends", 16f, 200f, 10, lines);
        var fits = true;
        foreach (var line in lines) fits &= TextWidth.Estimate(line, 16f) <= 200f && line.Length > 0;
        check(lines.Count > 1 && fits, "long text breaks into lines that fit");
        check(string.Join(" ", lines) == "Reach the extraction point in zone forty five before the alarm ends", "breaking keeps every word");
        TextWrap.Lines("前往四十五区的撤离点并在警报结束前到达那里完成任务", 16f, 120f, 2, lines);
        check(lines.Count == 2 && lines[1].EndsWith("…"), "text without spaces breaks anywhere and is cut with an ellipsis");
        TextWrap.Lines("A\nB", 16f, 200f, 5, lines);
        check(lines.Count == 2 && lines[0] == "A" && lines[1] == "B", "line breaks are kept");
    }
}

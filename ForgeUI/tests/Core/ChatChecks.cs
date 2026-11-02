using ForgeUI;

/// <summary>The chat dock's geometry, text size and fade, the cockpit's bottom corners with the docked chat at 1080p and 4K, and the cockpit's top
/// screen as the layout builds it: the readings row right above the armour bar in the top centre, the objectives in the top left corner.</summary>
internal static class ChatChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Plate(check);
        Text(check);
        Fade(check);
        Idle(check);
        Corners(check);
        Top(check);
    }

    private const float Row = ChatDockMath.InputRow, Line = ChatDockMath.Line, Pad = ChatDockMath.Pad;

    /// <summary>The plate of a docked chat with <paramref name="lines"/> lines shown: the highest line's top is the input row plus that
    /// many lines above the chat's bottom.</summary>
    private static HudBox Shown(HudBox slot, int lines) =>
        ChatDockMath.Plate(slot, ChatDockMath.Corner(slot).Y + Row + lines * Line, Row);

    private static void Plate(Action<bool, string> check)
    {
        var height = ChatDockMath.SlotHeight(6, Line, Row);
        check(Math.Abs(height - (2f * Pad + Row + 6f * Line)) < 1e-3f, "a slot holds its lines over the input row with a pad above and below");
        var slot = new HudBox(-500f, -480f, -30f, -480f + height);
        var none = ChatDockMath.Plate(slot, float.MinValue, Row);
        check(Math.Abs(none.Height - (Row + 2f * Pad)) < 1e-3f && none.Bottom == slot.Bottom, "with no line the plate is the input row and its pads, on the slot's bottom");
        var heights = new float[7];
        var grows = true;
        for (var n = 0; n <= 6; n++)
        {
            var plate = Shown(slot, n);
            heights[n] = plate.Height;
            grows &= plate.Bottom == slot.Bottom && plate.Left == slot.Left && plate.Right == slot.Right
                     && Math.Abs(plate.Height - (2f * Pad + Row + n * Line)) < 1e-3f && (n == 0 || heights[n] > heights[n - 1]);
        }
        check(grows, "the plate's height follows the lines shown, one line height each, standing on the slot's bottom");
        check(Math.Abs(heights[6] - slot.Height) < 1e-3f, "with all its lines shown the plate fills the slot");
        check(Math.Abs(Shown(slot, 12).Height - slot.Height) < 1e-3f, "and never grows past it");
        var (x, y) = ChatDockMath.Corner(slot);
        check(x == slot.Left + Pad && y == slot.Bottom + Pad, "the chat's lower left corner sits a pad inside the slot's");
        var inside = true;
        for (var n = 0; n <= 6; n++)
        {
            var plate = Shown(slot, n);
            inside &= y >= plate.Bottom && y + Row <= plate.Top - Pad + 1e-3f && x >= plate.Left && x < plate.Right;
        }
        check(inside, "the input row is inside the plate, under the newest line, whatever is shown");
        var ceiling = ChatDockMath.Ceiling(slot);
        check(!ChatDockMath.Overflows(ceiling, y + Row + 6f * Line) && ChatDockMath.Overflows(ceiling, y + Row + 7f * Line)
              && Math.Abs(ceiling - (slot.Top - Pad)) < 1e-3f, "the sixth line fits, a seventh reaches above the slot and is not drawn");
        check(ChatDockMath.Lines(0) == 1 && ChatDockMath.Lines(99) == ChatDockMath.MaxLines, "a move holds 1 to MaxLines lines");
        var idle = ChatDockMath.IdleCeiling(slot, Row, Line, 3);
        check(!ChatDockMath.Overflows(idle, y + Row + 3f * Line) && ChatDockMath.Overflows(idle, y + Row + 4f * Line),
            "not typed in, the newest three lines are drawn and the fourth is not");
        check(Math.Abs(ChatDockMath.IdleCeiling(slot, Row, Line, 12) - ceiling) < 1e-3f, "and never more than the slot holds");
    }

    /// <summary>The docked lines not typed in: at their opacity for the idle time, then out over the fade time, and back on a new line.</summary>
    private static void Idle(Action<bool, string> check)
    {
        check(ChatDockMath.IdleAlpha(0f, 10f, 0.85f) == 0.85f && ChatDockMath.IdleAlpha(10f, 10f, 0.85f) == 0.85f, "the lines stay at their opacity for the idle time");
        check(Math.Abs(ChatDockMath.IdleAlpha(10f + ChatDockMath.IdleFadeSeconds / 2f, 10f, 0.8f) - 0.4f) < 1e-4f, "then fade out over the fade time");
        check(ChatDockMath.IdleAlpha(10f + ChatDockMath.IdleFadeSeconds, 10f, 0.85f) == 0f && ChatDockMath.IdleAlpha(1e6f, 10f, 0.85f) == 0f, "and are gone after it");
        check(ChatDockMath.IdleAlpha(-1f, 10f, 2f) == 1f && ChatDockMath.IdleAlpha(0f, -5f, 0.85f) == 0.85f, "opacity is kept to 0 .. 1 and a negative time counts as none");
    }

    /// <summary>The docked chat's text in the HUD look: the same 1080p pixels whatever the chat's units, and never taller than a line.</summary>
    private static void Text(Action<bool, string> check)
    {
        // At 4K the measured chat line is 32 HUD units for the game's 20, so a chat unit is 20 / 32 of a HUD unit.
        const float hud = 18f, scale = 20f / 32f;
        var lines = ChatDockMath.TextSize(hud, 18f, scale, Line);
        check(Math.Abs(lines / scale - hud * 18f / 20f) < 1e-3f, "a chat line's text is the kit's 18 px HUD text in the chat's units");
        check(ChatDockMath.TextSize(hud, 19f, scale, Line) > lines, "the typed line is a little larger");
        check(Math.Abs(ChatDockMath.TextSize(hud, 60f, 1f, Line) - 0.95f * Line) < 1e-3f, "a size taller than a line is kept inside it");
        check(ChatDockMath.TextSize(0f, 18f, scale, Line) == 0f && ChatDockMath.TextSize(hud, 18f, 0f, Line) == 0f, "without a HUD size or a scale the game's size stays");
    }

    private static void Fade(Action<bool, string> check)
    {
        var a = ChatDockMath.Fade(0f, true, ChatDockMath.FadeInSeconds / 2f);
        check(Math.Abs(a - 0.5f) < 1e-4f, "typing lights the plate in the fade-in time");
        check(ChatDockMath.Fade(a, true, 1f) == 1f, "it never goes past full");
        check(ChatDockMath.Fade(1f, false, ChatDockMath.FadeOutSeconds / 2f) is > 0.49f and < 0.51f, "closing the chat dims it over the fade-out time");
        check(ChatDockMath.Fade(0.1f, false, 1f) == 0f, "and not below nothing");
        check(ChatDockMath.Fade(0.3f, true, -1f) == 0.3f, "a negative frame time moves nothing");
    }

    /// <summary>The cockpit's bottom corners at 1080p and 4K (the weapon list's space in 1080p units and in pixels): the docked chat
    /// (470 wide, six lines) under the inventory at the bottom right; the damage disc row, key hints and squad at the bottom left.
    /// Nothing overlaps, the chat stands on the corner's bottom edge clear of the middle, and its plate stays inside its slot.</summary>
    private static void Corners(Action<bool, string> check)
    {
        foreach (var (w, h, name) in new[] { (1920f, 1080f, "1080p"), (3840f, 2160f, "4K") })
        {
            var layout = new HudLayout();
            var chat = new LayoutEntry("Frames/PilotChat", new HudSlot(HudRegion.BottomRight, 20, 470f, ChatDockMath.SlotHeight(6, Line, Row))) { Visible = true };
            var loadout = new LayoutEntry("Kit/Loadout", new HudSlot(HudRegion.BottomRight, 10, 340f, 330f)) { Visible = true, WeaponListStandIn = true };
            var disc = new LayoutEntry("Frames/Damage", new HudSlot(HudRegion.BottomLeft, 52, 170f, 190f, row: 1)) { Visible = true };
            var special = new LayoutEntry("Frames/Special", new HudSlot(HudRegion.BottomLeft, 51, 90f, 112f, row: 1)) { Visible = true };
            var rockets = new LayoutEntry("Frames/Rockets", new HudSlot(HudRegion.BottomLeft, 50, 90f, 112f, row: 1)) { Visible = true };
            var lamp = new LayoutEntry("Frames/Lamp", new HudSlot(HudRegion.BottomLeft, 45, 160f, 30f, row: 2)) { Visible = true };
            var exit = new LayoutEntry("Frames/Exit", new HudSlot(HudRegion.BottomLeft, 44, 160f, 30f, row: 2)) { Visible = true };
            var mates = new LayoutEntry("Frames/Mates", new HudSlot(HudRegion.BottomLeft, 30, 300f, 150f)) { Visible = true };
            var all = new[] { chat, loadout, disc, special, rockets, lamp, exit, mates };
            foreach (var e in all) layout.Add(e);
            var screen = new HudBox(-w / 2f, -h / 2f, w / 2f, h / 2f);
            const float margin = 48f;
            layout.Run(new HudScene { Screen = screen, Margin = margin, WeaponListFree = true });
            var placed = true;
            foreach (var e in all) placed &= e.State == LayoutState.Placed;
            check(placed, name + ": every bottom corner element is placed");
            var clear = true;
            for (var i = 0; i < all.Length; i++)
                for (var j = i + 1; j < all.Length; j++) clear &= !all[i].Box.Overlaps(all[j].Box);
            check(clear, name + ": nothing overlaps");
            check(Math.Abs(chat.Box.Bottom - (screen.Bottom + margin + HudRegions.BuildLabel)) < 1e-3f && Math.Abs(chat.Box.Right - (screen.Right - margin)) < 1e-3f,
                name + ": the chat stands on the bottom right corner's safe edge");
            check(loadout.Box.Bottom >= chat.Box.Top + HudRegions.Gap - 1e-3f && Math.Abs(loadout.Box.Right - chat.Box.Right) < 1e-3f, name + ": the inventory stands right above it");
            // The crosshair arcs and the subtitles' line (390 under the middle, centred) are drawn round the middle.
            check(chat.Box.Left > 260f, name + ": the chat stays right of the middle, clear of the arcs and the subtitles");
            var inside = true;
            for (var n = 0; n <= 6; n++)
            {
                var plate = Shown(chat.Box, n);
                inside &= plate.Bottom >= chat.Box.Bottom && plate.Top <= chat.Box.Top + 1e-3f;
            }
            check(inside, name + ": the lit plate stays inside the chat's slot");
        }
    }

    private static void Top(Action<bool, string> check)
    {
        var layout = new HudLayout();
        var readings = new LayoutEntry("Frames/Readings", new HudSlot(HudRegion.TopCenter, 60, 480f, 28f)) { Visible = true };
        var armour = new LayoutEntry("Frames/Armor", new HudSlot(HudRegion.TopCenter, 50, 600f, 44f)) { Visible = true };
        var goals = new LayoutEntry("Frames/Objectives", new HudSlot(HudRegion.TopLeft, 50, 400f, 84f)) { Visible = true };
        var notices = new LayoutEntry("Kit/Notices", new HudSlot(HudRegion.Notices, 0, 420f, 96f)) { Visible = true };
        layout.Add(readings);
        layout.Add(armour);
        layout.Add(goals);
        layout.Add(notices);
        var screen = new HudBox(-960f, -540f, 960f, 540f);
        layout.Run(new HudScene { Screen = screen, Margin = 48f });
        var centre = (screen.Left + screen.Right) / 2f;
        check(readings.State == LayoutState.Placed && armour.State == LayoutState.Placed && readings.Box.Bottom >= armour.Box.Top,
            "the readings row stands right above the armour bar");
        check(Math.Abs(readings.Box.Top - (screen.Top - 48f)) < 1e-3f && Math.Abs(armour.Box.Top - (readings.Box.Bottom - HudRegions.Gap)) < 1e-3f,
            "the row takes the top edge and the bar follows it with the gap");
        check(Math.Abs((readings.Box.Left + readings.Box.Right) / 2f - centre) < 1e-3f && Math.Abs((armour.Box.Left + armour.Box.Right) / 2f - centre) < 1e-3f,
            "both are centred on the same line");
        check(goals.State == LayoutState.Placed && Math.Abs(goals.Box.Left - (screen.Left + 48f)) < 1e-3f && Math.Abs(goals.Box.Top - (screen.Top - 48f)) < 1e-3f,
            "the objectives sit in the top left corner");
        check(!goals.Box.Overlaps(readings.Box) && !goals.Box.Overlaps(notices.Box), "and clear of the readings and the notices");
        readings.Visible = false;
        layout.Run(new HudScene { Screen = screen, Margin = 48f });
        check(Math.Abs(armour.Box.Top - (screen.Top - 48f)) < 1e-3f, "without the readings the bar takes the top edge");
    }
}

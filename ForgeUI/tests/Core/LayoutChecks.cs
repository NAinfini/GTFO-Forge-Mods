using ForgeUI;

/// <summary>The shared screen: region stacking by priority, avoiding the game's HUD and other mods' elements, collapsing when a region
/// is full, the single banner slot, the banner queue and the exclusive full-screen arbiter.</summary>
internal static class LayoutChecks
{
    private static readonly HudBox Screen = new(-960f, -540f, 960f, 540f);

    internal static void Run(Action<bool, string> check)
    {
        Stacking(check);
        Avoidance(check);
        Collapse(check);
        Rows(check);
        Space(check);
        Cockpit(check);
        StandIn(check);
        Dock(check);
        Busy(check);
        Banners(check);
        Arbiter(check);
        Names(check);
    }

    private static LayoutEntry Entry(HudLayout layout, string id, HudRegion region, int priority, float width, float height, bool visible = true)
    {
        var entry = new LayoutEntry(id, new HudSlot(region, priority, width, height)) { Visible = visible };
        layout.Add(entry);
        return entry;
    }

    private static bool Same(HudBox box, float left, float bottom, float right, float top) =>
        Math.Abs(box.Left - left) < 1e-3f && Math.Abs(box.Bottom - bottom) < 1e-3f && Math.Abs(box.Right - right) < 1e-3f && Math.Abs(box.Top - top) < 1e-3f;

    private static void Stacking(Action<bool, string> check)
    {
        var layout = new HudLayout();
        var low = Entry(layout, "B.Mod/Low", HudRegion.WeaponList, 5, 200f, 30f);
        var high = Entry(layout, "A.Mod/High", HudRegion.WeaponList, 10, 300f, 40f);
        var hidden = Entry(layout, "C.Mod/Hidden", HudRegion.WeaponList, 50, 300f, 400f, visible: false);
        var scene = new HudScene { Screen = Screen, Margin = 48f, WeaponList = new HudBox(600f, -100f, 900f, 0f) };
        layout.Run(scene);
        check(high.State == LayoutState.Placed && high.RootX == 900f && high.RootY == -100f - HudRegions.Gap && Same(high.Box, 600f, -152f, 900f, -112f),
            "the highest priority element sits right under the game's weapon list, right-aligned");
        check(low.State == LayoutState.Placed && Same(low.Box, 700f, -194f, 900f, -164f), "the next one stacks under it with the gap, whatever the registration order");
        check(hidden.State == LayoutState.Hidden, "a hidden element takes no room even with the highest priority");
        low.DrawnHeight = 90f;
        high.DrawnWidth = 320f;
        layout.Run(scene);
        check(Same(high.Box, 580f, -152f, 900f, -112f) && Same(low.Box, 700f, -254f, 900f, -164f), "an element drawing more than it declared is laid out at what it draws");
        scene.OffsetX = 5f;
        layout.Run(scene);
        check(high.RootX == 905f, "the player's HUD offset moves the weapon list region");

        var vitalsLayout = new HudLayout();
        var suit = Entry(vitalsLayout, "Frames/Vitals", HudRegion.Vitals, 0, 360f, 80f);
        var bar = new HudScene { Screen = Screen, Margin = 48f, Vitals = new HudBox(-200f, -500f, 200f, -440f), BarCentre = -10f };
        vitalsLayout.Run(bar);
        check(suit.State == LayoutState.Placed && suit.RootX == -10f && suit.RootY == -440f + HudRegions.VitalsGap && Same(suit.Box, -190f, -440f + HudRegions.VitalsGap, 170f, -360f + HudRegions.VitalsGap),
            "a vitals element is centred on the health bar just above everything the vitals draw");
        vitalsLayout.Run(new HudScene { Screen = Screen, Margin = 48f });
        check(suit.State == LayoutState.Unavailable, "without the game's health bar a vitals element waits");
        var world = Entry(vitalsLayout, "M/Marker", HudRegion.World, 0, 0f, 0f);
        vitalsLayout.Run(bar);
        check(world.State == LayoutState.Free && world.RootX == 0f && world.RootY == 0f, "a world element is free, rooted at the middle of the screen");
    }

    private static void Avoidance(Action<bool, string> check)
    {
        // The game's weapon list reaches into the right column: the column's element moves on past it and past the weapon list region's element.
        var layout = new HudLayout();
        var list = Entry(layout, "Mod/Gauge", HudRegion.WeaponList, 10, 300f, 40f);
        var column = Entry(layout, "Frames/Abilities", HudRegion.RightColumn, 5, 200f, 50f);
        var corner = Entry(layout, "Arsenal/Heat", HudRegion.BottomRight, 1, 250f, 40f);
        var scene = new HudScene { Screen = Screen, Margin = 48f, WeaponList = new HudBox(700f, 0f, 950f, 100f), Vitals = new HudBox(-150f, -540f, 150f, -470f) };
        layout.Run(scene);
        check(Same(list.Box, 650f, -52f, 950f, -12f), "the weapon list element sits under the game's weapon list");
        check(column.State == LayoutState.Placed && Same(column.Box, 712f, -114f, 912f, -64f),
            "the right column element moves down past the game's weapon list and then past the other mod's element");
        check(corner.State == LayoutState.Placed && Same(corner.Box, 662f, -492f + HudRegions.BuildLabel, 912f, -452f + HudRegions.BuildLabel), "the bottom right element stacks up from its corner, clear of the game's build label");
        var boxes = new List<HudBox> { scene.WeaponList!.Value, scene.Vitals!.Value, list.Box, column.Box, corner.Box };
        var overlap = false;
        for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++) overlap |= boxes[i].Overlaps(boxes[j]);
        check(!overlap, "no element overlaps another or the game's HUD");

        // Many elements of many mods in every region: still nothing overlaps.
        var crowd = new HudLayout();
        var regions = new[] { HudRegion.WeaponList, HudRegion.Vitals, HudRegion.TopCenter, HudRegion.Notices, HudRegion.RightColumn, HudRegion.BottomLeft, HudRegion.BottomRight, HudRegion.CenterBanner };
        var entries = new List<LayoutEntry>();
        for (var i = 0; i < 40; i++)
            entries.Add(Entry(crowd, "Mod" + (i % 4) + "/E" + i, regions[i % regions.Length], (i * 7) % 11, 120f + (i * 37) % 300, 20f + (i * 13) % 90));
        crowd.Run(scene);
        var placed = entries.Where(e => e.State == LayoutState.Placed).Select(e => e.Box).Concat(boxes.Take(2)).ToList();
        overlap = false;
        for (var i = 0; i < placed.Count; i++)
            for (var j = i + 1; j < placed.Count; j++) overlap |= placed[i].Overlaps(placed[j]);
        check(!overlap && entries.Count(e => e.State == LayoutState.Placed) >= 20 && entries.All(e => e.State is LayoutState.Placed or LayoutState.Collapsed),
            "forty elements of four mods in every region: each placed or collapsed, none overlapping");
        var inside = entries.Where(e => e.State == LayoutState.Placed).All(e => e.Box.Left >= Screen.Left && e.Box.Right <= Screen.Right && e.Box.Bottom >= Screen.Bottom && e.Box.Top <= Screen.Top);
        check(inside, "every placed element is on the screen");
    }

    private static void Collapse(Action<bool, string> check)
    {
        var layout = new HudLayout();
        var big = Entry(layout, "A/Big", HudRegion.BottomLeft, 10, 300f, 300f);
        var small = Entry(layout, "B/Small", HudRegion.BottomLeft, 1, 300f, 200f);
        var scene = new HudScene { Screen = Screen, Margin = 48f };
        layout.Run(scene);
        // The bottom left region reaches from -492 up to -54 (a tenth of the half height under the middle).
        check(big.State == LayoutState.Placed && small.State == LayoutState.Collapsed,
            "when a region is full the lower priority element collapses");
        big.Visible = false;
        layout.Run(scene);
        check(small.State == LayoutState.Placed, "it comes back once there is room");

        var banner = new HudLayout();
        var kit = Entry(banner, "Kit/Banner", HudRegion.CenterBanner, 0, 600f, 80f);
        var link = Entry(banner, "Frames/Link", HudRegion.CenterBanner, 100, 500f, 80f);
        banner.Run(scene);
        check(link.State == LayoutState.Placed && kit.State == LayoutState.Collapsed,
            "the centre banner slot shows one element: the highest priority one");
        check(link.RootX == 0f && link.RootY == HudRegions.BannerRise && Same(link.Box, -250f, 110f, 250f, 190f), "centred a little above the middle of the screen");
    }

    private static LayoutEntry Row(HudLayout layout, string id, int priority, float width, float height, int row)
    {
        var entry = new LayoutEntry(id, new HudSlot(HudRegion.BottomLeft, priority, width, height, row)) { Visible = true };
        layout.Add(entry);
        return entry;
    }

    private static bool Clear(params LayoutEntry[] entries)
    {
        for (var i = 0; i < entries.Length; i++)
            for (var j = i + 1; j < entries.Length; j++)
                if (entries[i].Box.Overlaps(entries[j].Box)) return false;
        return true;
    }

    private static void Rows(Action<bool, string> check)
    {
        // A cockpit's bottom left: a disc and two skill slots on one line, two hints on the line above, the squad on its own line.
        var layout = new HudLayout();
        var disc = Row(layout, "F/Damage", 52, 170f, 190f, 1);
        var special = Row(layout, "F/Special", 51, 90f, 112f, 1);
        var rockets = Row(layout, "F/Rockets", 50, 90f, 112f, 1);
        var lamp = Row(layout, "F/Lamp", 45, 160f, 30f, 2);
        var exit = Row(layout, "F/Exit", 45, 160f, 30f, 2);
        var mates = Row(layout, "F/Mates", 30, 300f, 150f, 0);
        var scene = new HudScene { Screen = Screen, Margin = 48f };
        layout.Run(scene);
        var all = new[] { disc, special, rockets, lamp, exit, mates };
        check(all.All(e => e.State == LayoutState.Placed) && Clear(all), "three lines of six elements fit where one column of six would not, none overlapping");
        check(Math.Abs(disc.Box.Left + 912f) < 1e-3f && Math.Abs(special.Box.Left - disc.Box.Right - HudRegions.Gap) < 1e-3f
              && Math.Abs(rockets.Box.Left - special.Box.Right - HudRegions.Gap) < 1e-3f,
            "a row runs from the region's corner outwards, highest priority nearest the corner, with the gap between");
        check(disc.Box.Bottom == special.Box.Bottom && special.Box.Bottom == rockets.Box.Bottom && lamp.Box.Bottom == exit.Box.Bottom,
            "a row's elements stand on the edge the region grows from");
        check(Math.Abs(lamp.Box.Bottom - disc.Box.Top - HudRegions.Gap) < 1e-3f && Math.Abs(mates.Box.Bottom - lamp.Box.Top - HudRegions.Gap) < 1e-3f,
            "the next line stacks past the tallest element of the row before");
        check(disc.RootX == disc.Box.Left && disc.RootY == disc.Box.Bottom && special.RootX == special.Box.Left, "each element is rooted at its own corner");

        var crowded = new HudLayout();
        var big = Row(crowded, "A/Big", 10, 300f, 400f, 0);
        var one = Row(crowded, "B/One", 5, 100f, 60f, 1);
        var two = Row(crowded, "B/Two", 4, 100f, 60f, 1);
        crowded.Run(scene);
        check(big.State == LayoutState.Placed && one.State == LayoutState.Collapsed && two.State == LayoutState.Collapsed,
            "a row that does not fit collapses whole, never half shown");
        big.Visible = false;
        two.Visible = false;
        crowded.Run(scene);
        check(one.State == LayoutState.Placed && Math.Abs(one.Box.Left + 912f) < 1e-3f, "a row closes up over its hidden members");
    }

    private static void Space(Action<bool, string> check)
    {
        var layout = new HudLayout();
        var hints = new LayoutEntry("F/Hints", new HudSlot(HudRegion.BottomLeft, 45, 160f, 30f, row: 2, space: 4f)) { Visible = true };
        var near = new LayoutEntry("F/Near", new HudSlot(HudRegion.BottomLeft, 40, 100f, 30f, row: 2)) { Visible = true };
        var mates = new LayoutEntry("F/Mates", new HudSlot(HudRegion.BottomLeft, 30, 300f, 150f, space: 16f)) { Visible = true };
        layout.Add(hints);
        layout.Add(near);
        layout.Add(mates);
        var blocker = new LayoutEntry("G/Blocker", new HudSlot(HudRegion.BottomLeft, 35, 300f, 40f)) { Visible = true };
        layout.Run(new HudScene { Screen = Screen, Margin = 48f });
        check(Math.Abs(hints.Box.Bottom - (-492f + 4f)) < 1e-3f && hints.Box.Bottom == near.Box.Bottom, "a line keeps the largest space of its elements, even from the region's anchor");
        check(Math.Abs(mates.Box.Bottom - hints.Box.Top - HudRegions.Gap - 16f) < 1e-3f, "an element's space is kept on top of the gap to what it stacks on");
        check(mates.Slot.With(31, 320f, 160f).Space == 16f, "resizing or reprioritising a slot keeps its space");
        layout.Add(blocker);
        layout.Run(new HudScene { Screen = Screen, Margin = 48f });
        check(Math.Abs(mates.Box.Bottom - blocker.Box.Top - HudRegions.Gap - 16f) < 1e-3f && Clear(hints, near, blocker, mates), "the space is kept to whatever comes to stand before it");
    }

    /// <summary>The cockpit's corners as Frames lays them out. The HUD's size in its own units follows the HUD camera and the HUD's
    /// scale, not the resolution (the game logs 1280 x 720 HUD units at 4K); that screen and a 960 x 540 one, where a unit is a 1080p
    /// pixel, are both checked. Elements draw 1080p pixels times (half height / 540) HUD units and are measured at that; the slots ask
    /// for the 1080p pixel size, so on the larger screen the measured size rules.</summary>
    private static void Cockpit(Action<bool, string> check)
    {
        foreach (var half in new[] { 720f, 540f })
        {
            var k = half / 540f;
            var width = half * 16f / 9f;
            var scene = new HudScene { Screen = new HudBox(-width, -half, width, half), Margin = 48f };
            var layout = new HudLayout();
            LayoutEntry Add(string id, HudSlot slot, float drawnWidth, float drawnHeight)
            {
                var e = new LayoutEntry(id, slot) { Visible = true, DrawnWidth = drawnWidth, DrawnHeight = drawnHeight };
                layout.Add(e);
                return e;
            }
            // Top: the objectives (a 370 x 101 px block at 1080p) at the left; centred, the readings row and under it the armour bar
            // with its end readouts (954 px wide at 1080p).
            var goals = Add("Frames/Objectives", new HudSlot(HudRegion.TopLeft, 50, 370f, 100f), 370f * k, 101f * k);
            var readings = Add("Frames/Readings", new HudSlot(HudRegion.TopCenter, 60, 480f, 28f), 0f, 0f);
            var armour = Add("Frames/Armor", new HudSlot(HudRegion.TopCenter, 50, 600f, 44f), 954f * k, 34f * k);
            // Bottom left: the hull disc and two skill slots, the lamp and exit hints (drawn in HUD units), the squad of three.
            var disc = Add("Frames/Damage", new HudSlot(HudRegion.BottomLeft, 52, 170f, 190f, row: 1), 170f * k, 190f * k);
            var special = Add("Frames/Special", new HudSlot(HudRegion.BottomLeft, 51, 90f, 112f, row: 1), 90f * k, 112f * k);
            var rockets = Add("Frames/Rockets", new HudSlot(HudRegion.BottomLeft, 50, 90f, 112f, row: 1), 90f * k, 112f * k);
            var lamp = Add("Frames/Lamp", new HudSlot(HudRegion.BottomLeft, 45, 160f, 30f, row: 2), 120f, 22f);
            var exit = Add("Frames/Exit", new HudSlot(HudRegion.BottomLeft, 44, 160f, 30f, row: 2), 120f, 22f);
            var mates = Add("Frames/Mates", new HudSlot(HudRegion.BottomLeft, 30, 290f, 142f, space: 16f), 290f * k, 142f * k);
            layout.Run(scene);
            var all = new[] { goals, readings, armour, disc, special, rockets, lamp, exit, mates };
            var name = half + " HUD units high";
            check(all.All(e => e.State == LayoutState.Placed) && Clear(all), name + ": every cockpit element is placed and none overlaps another");
            check(goals.Box.Right + 60f * k < Math.Min(readings.Box.Left, armour.Box.Left),
                name + ": the objectives end well short of the readings and the armour bar, in their own corner");
            check(mates.Box.Bottom - Math.Max(lamp.Box.Top, exit.Box.Top) >= HudRegions.Gap + 16f - 1e-3f, name + ": the squad stands clear of the lamp and exit hints");
        }
    }

    private static void StandIn(Action<bool, string> check)
    {
        // A mod hides the game's weapon list (top right) and draws its own inventory in the bottom right: the weapon list elements of
        // other mods stack on past that inventory instead of keeping the top right corner, which stays free for the objectives.
        var layout = new HudLayout();
        var loadout = Entry(layout, "Kit/Loadout", HudRegion.BottomRight, 30, 340f, 300f);
        loadout.WeaponListStandIn = true;
        var gauge = Entry(layout, "Mod/Gauge", HudRegion.WeaponList, 50, 340f, 160f);
        var goals = Entry(layout, "Frames/Objectives", HudRegion.TopRight, 50, 420f, 120f);
        var scene = new HudScene { Screen = Screen, Margin = 48f, WeaponList = new HudBox(620f, 380f, 960f, 492f), WeaponListFree = true };
        layout.Run(scene);
        check(gauge.State == LayoutState.Placed && gauge.Home == HudRegion.BottomRight && gauge.BesideStandIn
              && Math.Abs(gauge.Box.Bottom - loadout.Box.Top - HudRegions.Gap) < 1e-3f && gauge.Box.Right == loadout.Box.Right,
            "the weapon list element stacks right above the inventory standing in for the hidden weapon list, on its right edge");
        check(gauge.RootX == gauge.Box.Right && gauge.RootY == gauge.Box.Top, "and keeps its own root corner, so it draws the same way");
        check(goals.State == LayoutState.Placed && Math.Abs(goals.Box.Top - 492f) < 1e-3f && Clear(loadout, gauge, goals),
            "the top right corner is free for the objectives");
        // A taller weapon list element (224 needed where 194 was left under a limit a quarter above the middle) still fits beside it.
        var typedLayout = new HudLayout();
        var tall = Entry(typedLayout, "Kit/Loadout", HudRegion.BottomRight, 30, 340f, 390f);
        tall.WeaponListStandIn = true;
        var taller = Entry(typedLayout, "Mod/Gauge", HudRegion.WeaponList, 50, 340f, 224f);
        typedLayout.Run(scene);
        check(taller.State == LayoutState.Placed && taller.BesideStandIn && Clear(tall, taller), "a taller weapon list element beside the inventory still has room on the right");
        loadout.Visible = false;
        layout.Run(scene);
        check(gauge.Home == HudRegion.WeaponList && !gauge.BesideStandIn, "with no inventory shown it goes back under where the weapon list was");
        loadout.Visible = true;
        layout.Run(scene with { WeaponListFree = false });
        check(gauge.Home == HudRegion.WeaponList, "a weapon list the game still shows keeps its elements");
    }

    /// <summary>One command list run frame by frame at 60 fps against fixed or moving obstacles, as the template places it: the column
    /// from the largest size, the middle from the current height. Returns the largest move of an edge in one frame and whether it ever
    /// overlapped an obstacle.</summary>
    private static (float Largest, bool Overlap, float Right, float Bottom, float Top) Run(CommandMotion motion, HudBox[] obstacles, int count, bool table,
        int frames, float widest, Func<int, HudBox[]>? move = null, float lastBottom = float.NaN, float lastTop = float.NaN)
    {
        const float right0 = 936f, bottom = -516f, top = 516f;
        float largest = 0f, r = 0f, b = lastBottom, t = lastTop;
        var overlap = false;
        for (var f = 0; f < frames; f++)
        {
            if (move != null) obstacles = move(f);
            motion.Step(table, count, -1, table ? CommandGeometry.Codes(5) : 0f, false, 1f / 60f);
            r = CommandDock.Right(obstacles, right0, bottom, top, widest, CommandMotion.TallestHeight(count));
            CommandDock.Limits(obstacles, r - widest, r, bottom, top, out var floor, out var ceiling, out _, out _);
            var h = motion.Height;
            var c = CommandDock.Centre(h, floor, ceiling);
            var box = new HudBox(r - CommandGeometry.Width(motion.Codes, 150f, motion.Table), c - h / 2f, r, c + h / 2f);
            foreach (var o in obstacles) overlap |= o.Overlaps(box);
            if (!float.IsNaN(b)) largest = Math.Max(largest, Math.Max(Math.Abs(box.Bottom - b), Math.Abs(box.Top - t)));
            b = box.Bottom;
            t = box.Top;
        }
        return (largest, overlap, r, b, t);
    }

    private static void Dock(Action<bool, string> check)
    {
        // The cockpit with every slot full: chat at the bottom right, the loadout above it reaching well over the middle, the objectives
        // at the top right. The list cannot stand at the right edge (no room between loadout and objectives), so it stands just left of
        // the loadout, its middle on the screen's middle, and opening the table changes neither its column nor jumps its edges.
        var cockpit = new[] { new HudBox(416f, -500f, 936f, -280f), new HudBox(360f, -268f, 936f, 310f), new HudBox(536f, 380f, 936f, 516f) };
        var motion = new CommandMotion(4);
        var widest = CommandGeometry.Width(CommandGeometry.Codes(7), 170f, 1f);
        // An eased change moves an edge at most about a quarter of the way in one 60 fps frame; a jump would move it all the way.
        var step = 0.27f * (CommandMotion.TallestHeight(4) - (4 * (CommandGeometry.IdleRow + CommandGeometry.IdleGap) - CommandGeometry.IdleGap)) + 0.5f;
        var idle = Run(motion, cockpit, 4, false, 10, widest);
        check(idle.Right <= 360f - CommandDock.Gap && !idle.Overlap && Math.Abs(idle.Bottom + idle.Top) < 1e-3f,
            "full cockpit: the idle list stands left of the loadout, its middle on the screen's middle, clear of everything");
        motion.Step(true, 4, -1, CommandGeometry.Codes(5), false, 0f);
        check(Math.Abs(motion.TargetHeight - CommandMotion.TallestHeight(4)) < 1e-3f, "the table's final height is known on the frame it opens");
        var open = Run(motion, cockpit, 4, true, 60, widest, null, idle.Bottom, idle.Top);
        check(Math.Abs(open.Right - idle.Right) < 1e-3f, "opening the table never moves the list sideways");
        check(!open.Overlap && open.Largest < step, "while it opens no edge jumps (largest move " + open.Largest + " in a frame) and it overlaps nothing");
        check(motion.Settled && Math.Abs(open.Top - open.Bottom - CommandMotion.TallestHeight(4)) < 0.01f && Math.Abs(open.Bottom + open.Top) < 0.01f,
            "it settles at the table's height, still centred on the middle");
        var shut = Run(motion, cockpit, 4, false, 60, widest, null, open.Bottom, open.Top);
        check(!shut.Overlap && shut.Largest < step && Math.Abs(shut.Right - idle.Right) < 1e-3f, "closing again is as smooth and stays in its column");

        // The game's own HUD with a tall weapon list under the right edge: the list stays at the edge, raised over the weapon list only as
        // far as the table needs, continuously.
        var vanilla = new[] { new HudBox(660f, -500f, 936f, -80f) };
        var edge = new CommandMotion(4);
        var low = Run(edge, vanilla, 4, false, 5, widest);
        check(Math.Abs(low.Right - 936f) < 1e-3f && Math.Abs(low.Bottom + low.Top) < 1e-3f && !low.Overlap, "with room at the right edge the idle list stands there, centred");
        var raised = Run(edge, vanilla, 4, true, 60, widest, null, low.Bottom, low.Top);
        check(!raised.Overlap && raised.Largest < step && Math.Abs(raised.Bottom - (-80f + CommandDock.Gap)) < 0.01f,
            "the table rises over the weapon list as it opens, never through it, without a jump");

        // Something under the list grows (a loadout opening a card): the list is pushed up with it, frame by frame, never overlapped.
        var grow = new CommandMotion(4);
        var top = -150f;
        var pushed = Run(grow, vanilla, 4, false, 90, widest, f => new[] { new HudBox(600f, -500f, 936f, top = Ease.Damp(top, -30f, 28f, 1f / 60f)) });
        check(!pushed.Overlap && pushed.Largest <= 120f * (1f - MathF.Exp(-28f / 60f)) + 0.01f && pushed.Bottom >= -30f + CommandDock.Gap - 0.01f, "a growing neighbour pushes the list up no faster than it grows and never into it");

        check(CommandDock.Centre(400f, -100f, 200f) == 100f, "a list that cannot fit keeps clear of what is below it");
    }

    private static void Busy(Action<bool, string> check)
    {
        // Laying out allocates nothing per frame once sorted, with rows and a weapon list stand-in too.
        var layout = new HudLayout();
        for (var i = 0; i < 12; i++) Entry(layout, "M/E" + i, (HudRegion)(i % 8), i, 100f, 20f);
        for (var i = 0; i < 4; i++) Row(layout, "M/R" + i, 40 + i, 60f, 30f, 1 + i % 2);
        Entry(layout, "M/Stand", HudRegion.BottomRight, 3, 200f, 60f).WeaponListStandIn = true;
        var scene = new HudScene { Screen = Screen, Margin = 48f, WeaponList = new HudBox(600f, -100f, 900f, 0f), WeaponListFree = true, Vitals = new HudBox(-150f, -540f, 150f, -470f) };
        layout.Run(scene);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) layout.Run(scene);
        check(GC.GetAllocatedBytesForCurrentThread() - before == 0, "a steady layout allocates nothing");
    }

    private static void Banners(Action<bool, string> check)
    {
        var queue = new BannerQueue();
        object a = new(), b = new(), c = new();
        check(queue.Show(a, "A/Banner", 0, "ONE", NoticeTone.Info, 2f, "", 0f) == BannerOutcome.Shown && queue.Current!.Text == "ONE", "the first banner shows at once");
        check(queue.Show(b, "B/Banner", 0, "TWO", NoticeTone.Info, 2f, "", 0.5f) == BannerOutcome.Queued && queue.Current!.Text == "ONE" && queue.Waiting == 1,
            "a second mod's banner of the same priority waits instead of stacking");
        check(queue.Show(c, "C/Banner", 5, "THREE", NoticeTone.Alert, 2f, "", 1f) == BannerOutcome.Preempted && queue.Current!.Text == "THREE" && queue.Displaced!.OwnerId == "A/Banner",
            "a higher priority banner takes the slot and the one showing is dropped");
        check(queue.Show(c, "C/Banner", 5, "THREE B", NoticeTone.Alert, 2f, "", 1.5f) == BannerOutcome.Shown && queue.Current!.Text == "THREE B" && queue.Waiting == 1,
            "a mod's new banner replaces its own");
        queue.Tick(1.5f + 2f + BannerQueue.FadeSeconds - 0.01f);
        check(queue.Current!.OwnerId == "C/Banner", "a banner keeps the slot through its time and fade");
        queue.Tick(1.5f + 2f + BannerQueue.FadeSeconds);
        check(queue.Current!.OwnerId == "B/Banner" && queue.Started == 4f, "then the waiting banner shows");
        queue.Hide(b, 4.5f);
        check(queue.Current == null, "a mod can take its own banner away");

        var full = new BannerQueue();
        full.Show(new object(), "Holder/Banner", 10, "HOLD", NoticeTone.Info, 30f, "", 0f);
        for (var i = 0; i < BannerQueue.Capacity; i++) full.Show(new object(), "W" + i + "/Banner", 3, "W", NoticeTone.Info, 1f, "", 0f);
        check(full.Show(new object(), "Late/Banner", 1, "LATE", NoticeTone.Info, 1f, "", 0f) == BannerOutcome.Dropped, "a full queue drops a newcomer that is outranked by every waiting banner");
        check(full.Show(new object(), "Urgent/Banner", 5, "URGENT", NoticeTone.Info, 1f, "", 0f) == BannerOutcome.Queued && full.Displaced!.OwnerId == "W3/Banner",
            "a stronger newcomer drops the weakest, latest waiting banner");
        var expired = new List<BannerQueue.Request>();
        full.Tick(BannerQueue.MaxWait + 0.1f, expired);
        check(expired.Count == BannerQueue.Capacity && full.Waiting == 0 && full.Current!.OwnerId == "Holder/Banner", "banners that waited too long are dropped, stale moments never show");
    }

    private static void Arbiter(Action<bool, string> check)
    {
        var arbiter = new ScreenArbiter();
        object boot = new(), fade = new(), alarm = new();
        var bootPutAway = 0;
        check(arbiter.Request(boot, "Frames/Boot", 50, 0f, () => bootPutAway++) == ScreenGrant.Granted && arbiter.Holder == "Frames/Boot", "a free screen is granted");
        check(arbiter.Request(boot, "Frames/Boot", 50, 0.1f, () => bootPutAway++) == ScreenGrant.Granted, "the holder keeps it while it asks");
        check(arbiter.Request(fade, "Other/Fade", 50, 0.1f, null) == ScreenGrant.Waiting && arbiter.Other == "Frames/Boot", "an equal priority layer waits for the holder");
        check(arbiter.Request(alarm, "Other/Alarm", 90, 0.2f, null) == ScreenGrant.Preempted && arbiter.Other == "Frames/Boot" && bootPutAway == 1 && arbiter.Holder == "Other/Alarm",
            "a higher priority layer takes the screen and the holder is told to put itself away");
        check(arbiter.Request(boot, "Frames/Boot", 50, 0.25f, () => bootPutAway++) == ScreenGrant.Waiting, "the put away layer waits when it asks again");
        arbiter.Release(alarm);
        check(arbiter.Request(boot, "Frames/Boot", 50, 0.3f, null) == ScreenGrant.Waiting && arbiter.Other == "Other/Fade",
            "when the screen frees, the layer that has waited longest at the same priority goes first");
        check(arbiter.Request(fade, "Other/Fade", 50, 0.35f, null) == ScreenGrant.Granted, "and gets it when it asks");
        arbiter.Release(fade);
        check(arbiter.Request(boot, "Frames/Boot", 50, 0.4f, null) == ScreenGrant.Granted, "then the next one");
        check(arbiter.Request(alarm, "Other/Alarm", 70, 0.5f, null) == ScreenGrant.Preempted, "a stronger layer preempts again");
        check(arbiter.Request(fade, "Other/Fade", 60, 0.5f, null) == ScreenGrant.Waiting, "a layer of middle priority waits once");
        arbiter.Release(alarm);
        check(arbiter.Request(boot, "Frames/Boot", 50, 2f, null) == ScreenGrant.Granted, "a waiter that stopped asking does not block the screen");
    }

    private static void Names(Action<bool, string> check)
    {
        check(ModIds.Problem("NAinfini.ForgeOrdnance", "mod ID") == null && ModIds.Problem("Call-Ins", "name") == null, "plugin GUIDs and plain names are valid");
        check(ModIds.Problem("a/b", "name") != null && ModIds.Problem("has space", "name") != null && ModIds.Problem("", "name") != null, "a slash, a space or nothing is refused");
        check(ModIds.Join("NAinfini.ForgeOrdnance", "CallIns") == "NAinfini.ForgeOrdnance/CallIns" && ModIds.Split("NAinfini.ForgeOrdnance/CallIns") == ("NAinfini.ForgeOrdnance", "CallIns"),
            "a full ID is mod/name");
    }
}

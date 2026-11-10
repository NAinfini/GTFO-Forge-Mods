using System;
using ForgeUI;

namespace ForgeOrdnanceFrames;

/// <summary>The pilot's power-on from the boarding's black (Titan cockpit style) and the power-down when leaving, as pure geometry and
/// timing. Thin dark seams, like the frame of a cockpit window, cut the black screen into ten convex panels that make five blocks and
/// mirror about the middle: the central window (an upper and a lower part), the panel above it and the one below it, and three
/// left-right pairs around it. A block that comes on shows the game's own picture with a strong glitch in the cockpit's colours (cyan-white
/// monochrome, banding, scanlines, the picture split into red, green and blue copies with tears and noise bars in those colours) that settles
/// to the plain picture in <see cref="ShiftSeconds"/>; each block runs on its own clock. When each block comes on is the
/// <see cref="BootPlan"/>; the pilot moves once the last block starts to come on, and the picture is whole once the last seam is gone. Leaving runs the blocks in reverse (outer pair first) under the red power-down word: glitch up, black, then the black
/// clears once the pilot's head is out of the shell. The native overlay only draws what these functions return; nothing here allocates.</summary>
internal static class FrameBootScreen
{
    public const int PanelCount = 10, SeamCount = 16, Groups = 5;
    /// <summary>How long a block's colour glitch takes to settle to the normal picture.</summary>
    public const float ShiftSeconds = 0.40f;
    /// <summary>The seams stay this long after the last block has settled, then fade over <see cref="SeamFade"/>.</summary>
    public const float SeamHold = 0.12f, SeamFade = 0.16f;
    /// <summary>Leaving: block by block (outer pair first, one every <see cref="DownStep"/>) the picture glitches for
    /// <see cref="DownShiftSeconds"/> and goes black; every block is black at <see cref="DownDark"/>, when the pilot starts pulling out; the
    /// black stays while the pilot lands outside the suit and the camera eases to the player's own eye, then clears over
    /// <see cref="DownClear"/>: never while the camera still stands inside the shell.</summary>
    public const float DownStep = 0.12f, DownShiftSeconds = 0.30f, DownClear = 0.15f;
    public const float DownDark = (Groups - 1) * DownStep + DownShiftSeconds;
    public const float DownClearStart = DownDark + PilotMotion.Land + FrameSequence.HandBackSeconds;
    public const float DownSeconds = DownClearStart + DownClear;

    // Panels, clockwise from the top left, in the unit square (x to the right, y up). Window (0 upper, 1 lower), 2 top, 3 bottom,
    // then left and right of: 4/5 middle, 6/7 upper, 8/9 lower.
    private static readonly float[][] Poly =
    {
        new[] { 0.25f, 0.76f, 0.75f, 0.76f, 0.757f, 0.62f, 0.645f, 0.38f, 0.355f, 0.38f, 0.243f, 0.62f },
        new[] { 0.355f, 0.38f, 0.645f, 0.38f, 0.645f, 0.14f, 0.355f, 0.14f },
        new[] { 0f, 1f, 1f, 1f, 0.75f, 0.76f, 0.25f, 0.76f },
        new[] { 0.355f, 0.14f, 0.645f, 0.14f, 0.66f, 0f, 0.34f, 0f },
        new[] { 0f, 0.50f, 0.243f, 0.62f, 0.355f, 0.38f, 0f, 0.12f },
        new[] { 1f, 0.12f, 0.645f, 0.38f, 0.757f, 0.62f, 1f, 0.50f },
        new[] { 0f, 1f, 0.25f, 0.76f, 0.243f, 0.62f, 0f, 0.50f },
        new[] { 1f, 0.50f, 0.757f, 0.62f, 0.75f, 0.76f, 1f, 1f },
        new[] { 0f, 0.12f, 0.355f, 0.38f, 0.355f, 0.14f, 0.34f, 0f, 0f, 0f },
        new[] { 1f, 0f, 0.66f, 0f, 0.645f, 0.14f, 0.645f, 0.38f, 1f, 0.12f },
    };

    // The seams: window outline (0..7), then the lines from the window to the screen corners and edges.
    private static readonly float[][] Seams =
    {
        new[] { 0.25f, 0.76f, 0.75f, 0.76f }, new[] { 0.75f, 0.76f, 0.757f, 0.62f }, new[] { 0.757f, 0.62f, 0.645f, 0.38f }, new[] { 0.645f, 0.38f, 0.645f, 0.14f },
        new[] { 0.645f, 0.14f, 0.355f, 0.14f }, new[] { 0.355f, 0.14f, 0.355f, 0.38f }, new[] { 0.355f, 0.38f, 0.243f, 0.62f }, new[] { 0.243f, 0.62f, 0.25f, 0.76f },
        new[] { 0.25f, 0.76f, 0f, 1f }, new[] { 0.75f, 0.76f, 1f, 1f }, new[] { 0.243f, 0.62f, 0f, 0.50f }, new[] { 0.757f, 0.62f, 1f, 0.50f },
        new[] { 0.355f, 0.38f, 0f, 0.12f }, new[] { 0.645f, 0.38f, 1f, 0.12f }, new[] { 0.355f, 0.14f, 0.34f, 0f }, new[] { 0.645f, 0.14f, 0.66f, 0f },
    };

    // The block of each panel.
    private static readonly int[] Group = { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4 };

    /// <summary>Number of corners of panel <paramref name="p"/> (clockwise from the top left).</summary>
    public static int VertexCount(int p) => Poly[p].Length / 2;

    public static float X(int p, int k) => Poly[p][k * 2];

    public static float Y(int p, int k) => Poly[p][k * 2 + 1];

    /// <summary>The block (0 window, 1 top and bottom, 2 middle pair, 3 upper pair, 4 lower pair) panel <paramref name="p"/> belongs to.</summary>
    public static int GroupOf(int p) => Group[p];

    /// <summary>Seam <paramref name="i"/> from (x0, y0) to (x1, y1) in the unit square.</summary>
    public static void Seam(int i, out float x0, out float y0, out float x1, out float y1)
    {
        var s = Seams[i];
        x0 = s[0]; y0 = s[1]; x1 = s[2]; y1 = s[3];
    }

    /// <summary>The power-on and power-down timeline (ForgeUI's <see cref="PowerOnTimeline"/>) of these panels with block starts
    /// <paramref name="starts"/> and this suit's rhythm. The power-down runs the blocks in reverse (the outer pair first).</summary>
    public static PowerOnTimeline Timeline(float[] starts) => new(Group, new PowerOnRhythm
    {
        Starts = starts,
        ShiftSeconds = ShiftSeconds,
        SeamHold = SeamHold,
        SeamFade = SeamFade,
        DownStep = DownStep,
        DownShiftSeconds = DownShiftSeconds,
        DownClearStart = DownClearStart,
        DownClear = DownClear,
    });
}

/// <summary>
/// The power-on, on its own clock from the boarding's black (<see cref="FrameSequence.Black"/>). The screen holds black for a moment, then
/// the five panel blocks come into view one by one from <see cref="DefaultStarts"/>, each settling over <see cref="FrameBootScreen.ShiftSeconds"/>;
/// the boarding ends and the pilot moves the moment the last block starts to come on (<see cref="ControlsAt"/>), and the last two blocks
/// settle and the seams fade while the pilot already walks, until the picture is whole (<see cref="Assembled"/>, ForgeUI's power-on
/// player). Meanwhile the cockpit's parts come online one after another (<see cref="ReportAt"/>: each runs ForgeUI's
/// reveal) and the message over the crosshair (<see cref="FrameBootMessage"/>, from <see cref="MessageStart"/>) runs on past the boarding:
/// it locks at <see cref="LinkLock"/> with the ready voice and the crosshair arcs, and the whole power-on is over at <see cref="Settled"/>.
/// </summary>
internal sealed class BootPlan
{
    /// <summary>The block starts: window, top and bottom, middle pair, upper pair, lower pair; not evenly spaced.</summary>
    public static readonly float[] DefaultStarts = { 0.20f, 0.36f, 0.56f, 0.74f, 0.95f };
    /// <summary>When each cockpit part comes online, in <see cref="CockpitPart"/> order, each once the block over it has come on: the top
    /// bars first, then the objectives, the bearing ring, the inventory, the skills and the squad, the crosshair arcs last (their lock on
    /// <see cref="LinkLock"/>).</summary>
    public static readonly float[] DefaultReports = { 0.48f, 0.86f, 1.17f, 1.26f, 1.08f, 0.94f, LinkLock - HudReveal.LockStart };
    /// <summary>The message starts on the power-on's clock (its own beats count from here).</summary>
    public const float MessageStart = 0.85f;
    /// <summary>NEURAL LINK OK locks.</summary>
    public const float LinkLock = MessageStart + 1.50f;
    /// <summary>The message has folded away: the whole power-on is over.</summary>
    public const float Duration = MessageStart + 2.2f;

    public static readonly BootPlan Default = new(DefaultStarts, DefaultReports);

    private readonly float[] _reports;

    private BootPlan(float[] starts, float[] reports)
    {
        _reports = reports;
        Timeline = FrameBootScreen.Timeline(starts);
        var last = 0f;
        foreach (var s in starts) last = Math.Max(last, s);
        ControlsAt = last;
    }

    /// <summary>The panels' power-on and power-down (when each block comes on, glitches, settles; the seams; leaving in reverse).</summary>
    public PowerOnTimeline Timeline { get; }
    /// <summary>The last block starts to come on: the boarding ends here and the pilot moves.</summary>
    public float ControlsAt { get; }
    /// <summary>The picture is whole: every block has settled and the seams are gone (the pilot has been walking since <see cref="ControlsAt"/>).</summary>
    public float Assembled => Timeline.Assembled;
    /// <summary>The message locks; the ready voice plays now.</summary>
    public float LinkSteady => LinkLock;
    /// <summary>The whole power-on is over.</summary>
    public float Settled => Duration;

    /// <summary>The message's own clock at power-on second <paramref name="boot"/> (negative before it starts).</summary>
    public static float MessageAt(float boot) => boot - MessageStart;

    /// <summary>When block <paramref name="group"/>'s tone plays: the moment it comes on.</summary>
    public float ToneAt(int group) => Timeline.GroupStart(group);

    /// <summary>When cockpit part <paramref name="part"/> comes online.</summary>
    public float ReportAt(CockpitPart part) => _reports[(int)part];

    /// <summary>Whether <paramref name="part"/> shows at <paramref name="s"/>: from the moment it comes online (its reveal plays then).</summary>
    public bool Shown(CockpitPart part, float s) => s >= ReportAt(part);
}

/// <summary>The suit's power-on message, played by ForgeUI's <see cref="BootMessage"/>: its beats (counted from
/// <see cref="BootPlan.MessageStart"/>; the power-down word on the leaving clock, gone as the window block turns black), its colours
/// (cyan-white, orange only on the SYNC stall, a red-blue split while NEURAL LINK OK decodes, red for SHUTDOWN) and its words (the NEURAL
/// LINK header, five self-test lines under the bar, NEURAL LINK OK, SHUTDOWN).</summary>
internal static class FrameBootMessage
{
    public static readonly BootMessageBeats Beats = new()
    {
        IgniteEnd = 0.12f,
        BracketsStart = 0.04f,
        BracketsEnd = 0.20f,
        HeaderStart = 0.08f,
        HeaderEnd = 0.32f,
        FillTimes = new[] { 0.15f, 0.50f, 0.75f, 1.15f, 1.30f },
        FillValues = new[] { 0f, 0.35f, 0.48f, 0.92f, 1f },
        StallStart = 0.50f,
        StallEnd = 0.78f,
        BarFlash = 0.08f,
        CheckStarts = new[] { 0.42f, 0.58f, 0.74f, 0.90f, 1.06f },
        CheckType = 0.10f,
        CheckOk = 0.12f,
        CheckStamp = 0.08f,
        ChecksFade = 1.60f,
        ChecksGone = 1.80f,
        MessageStart = 1.32f,
        MessageLock = BootPlan.LinkLock - BootPlan.MessageStart,
        LockFlash = 0.07f,
        LockBurst = 0.25f,
        LockShake = 0.12f,
        FoldStart = 1.90f,
        Duration = BootPlan.Duration - BootPlan.MessageStart,
        DownStart = 0.06f,
        DownDecoded = 0.28f,
        DownEnd = FrameBootScreen.DownDark,
    };

    /// <summary>The crosshair and the two arcs either side of it (the magazine's, the heat's with its OVERHEAT label), the box the
    /// message, bar and self-test stay clear of: ForgeUI's arc extent at the cockpit's arc size, both sides in one.</summary>
    public static readonly HudBox Keepout = ArcKeepout();

    /// <summary>The arcs' size is ForgeUI's default (radius 96, sweep 72, line 3.2); the magazine's number is at most four digits wide
    /// and the heat label about ninety units.</summary>
    private static HudBox ArcKeepout()
    {
        var left = GaugeMath.ArcExtent(ArcSide.Left, 96f, 72f, 3.2f, 44f, 0f);
        var right = GaugeMath.ArcExtent(ArcSide.Right, 96f, 72f, 3.2f, 44f, 90f);
        return new HudBox(Math.Min(left.Left, right.Left), Math.Min(left.Bottom, right.Bottom), Math.Max(left.Right, right.Right), Math.Max(left.Top, right.Top));
    }

    /// <summary>The self-test's status word (OK) in green.</summary>
    public static readonly HudColour StatusGreen = new(0.30f, 0.95f, 0.48f, 1f);
    public static readonly BootMessageLook Look = new()
    {
        Accent = new(0.80f, 0.94f, 1f, 1f),
        Bright = new(0.957f, 0.965f, 0.957f, 1f),
        White = new(1f, 1f, 1f, 1f),
        SplitLeft = new(1f, 0.29f, 0.23f, 1f),
        SplitRight = new(0.25f, 0.85f, 1f, 1f),
        Stall = new(1f, 0.56f, 0.17f, 1f),
        Backing = new(0.016f, 0.03f, 0.047f, 0.62f),
        Down = new(1f, 0.25f, 0.2f, 1f),
        MessagePx = 50f,
        HeaderPx = 16f,
        CheckPx = 16f,
        CheckRow = 20f,
        BarWidth = 420f,
        Cells = 24,
        CellHeight = 6f,
        CellGap = 3f,
        HeaderY = 18f,
        CheckBase = 44f,
        LineY = 80f,
        MessageLift = 86f,
        Status = StatusGreen,
        Keepout = Keepout,
        DownPx = 64f,
        Width = 500f,
        Height = 320f,
    };

    /// <summary>The words for a boarding of a suit with <paramref name="armorPercent"/> armour.</summary>
    public static BootMessageWords Words(FrameWords words, int armorPercent) => new()
    {
        Header = words.LinkHeader,
        Stall = words.BootSync,
        Ok = words.BootOk,
        Message = words.Link,
        Down = words.Shutdown,
        Checks = new[] { words.BootSeal, FrameWords.Format(words.BootArmor, armorPercent), words.BootServos, words.BootWeapon, words.BootSensors },
    };
}
/// <summary>The cockpit's parts as the power-on and power-down see them: each ForgeUI element (or group of elements) of the cockpit
/// belongs to one. They report in one after another (<see cref="CockpitParts.Order"/>: the top bars first, the crosshair arcs last) and power down with
/// the panel block that covers them, the outer blocks first (<see cref="CockpitParts.DownGroup"/>), so each goes dark just before its block turns black.</summary>
internal enum CockpitPart : byte { Bars, Objectives, Skills, Teammates, Weapon, Radar, Arcs }

internal static class CockpitParts
{
    public const int Count = 7;

    /// <summary>The report-in position of <paramref name="part"/> (0 first).</summary>
    public static int Order(CockpitPart part) => (int)part;

    /// <summary>The panel block over <paramref name="part"/>'s place on the screen: the top bars under the top panel, the objectives under
    /// the upper pair, the bottom corners under the lower pair, the crosshair's arcs and the bearing ring under the window.</summary>
    public static int DownGroup(CockpitPart part) => part switch
    {
        CockpitPart.Bars => 1,
        CockpitPart.Objectives => 3,
        CockpitPart.Skills or CockpitPart.Teammates or CockpitPart.Weapon => 4,
        _ => 0,
    };

    /// <summary>Leaving, <paramref name="d"/> seconds in: whether <paramref name="part"/> still shows. It flickers off once as its block starts
    /// to glitch and is gone before the block turns black.</summary>
    public static bool ShownLeaving(CockpitPart part, float d)
    {
        var shift = BootPlan.Default.Timeline.DownGroup(DownGroup(part), d);
        return shift < 0.15f || shift is >= 0.3f and < 0.55f;
    }
}

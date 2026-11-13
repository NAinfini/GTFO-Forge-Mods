using System;
using ForgeUI;

namespace ForgeReplay;

/// <summary>The replay screen's fixed zones on the current screen, none over another, all along the top, right and bottom edges so the middle
/// of the picture stays clear: the bottom panel across the whole width (the time bar at its foot, above it the control rows); above it the
/// mission card top left, the kill feed top right with the four players' cards under it, and the middle between them for the game's own lines,
/// scans and notices. The panels stand left of the cards, from the bottom panel up to under the mission card (<see cref="Panels"/>); an open
/// panel makes the middle give way (ReplayScreen).</summary>
internal readonly record struct ReplayZones(HudBox Work, HudBox Bottom, HudBox Control, HudBox Mission, HudBox Feed, HudBox Cards, HudBox Middle)
{
    private const float SideShare = 0.26f, SideMin = 420f, SideMax = 760f;

    /// <param name="screen">The screen's edges.</param>
    /// <param name="margin">The player's safe margin.</param>
    /// <param name="bar">The time bar's height from the bottom edge (safe margin included).</param>
    /// <param name="control">The height of the control rows over the time bar, in the same panel.</param>
    /// <param name="gap">The space between zones.</param>
    /// <param name="cardWidth">How wide the cards want to be (never wider than the side share of the screen).</param>
    /// <param name="feedHeight">The kill feed's height.</param>
    internal static ReplayZones Of(HudBox screen, float margin, float bar, float control, float gap, float cardWidth, float feedHeight)
    {
        var left = screen.Left + margin;
        var right = screen.Right - margin;
        var controlBottom = screen.Bottom + bar;
        var controlBox = new HudBox(left, controlBottom, right, controlBottom + control);
        var bottom = new HudBox(left, screen.Bottom + margin, right, controlBox.Top);
        var work = new HudBox(left, bottom.Top + gap, right, screen.Top - margin);
        var side = Math.Clamp(work.Width * SideShare, Math.Min(SideMin, work.Width / 3f), SideMax);
        var feed = new HudBox(right - side, work.Top - feedHeight, right, work.Top);
        var cards = new HudBox(right - Math.Min(cardWidth, side), work.Bottom, right, feed.Bottom - gap);
        var mission = new HudBox(left, work.Bottom, left + side, work.Top);
        var middle = new HudBox(mission.Right + gap, work.Bottom, feed.Left - gap, work.Top);
        return new ReplayZones(work, bottom, controlBox, mission, feed, cards, middle);
    }

    /// <summary>The room for a panel when the mission card ends at <paramref name="top"/>: above the bottom panel, left of the cards.</summary>
    internal HudBox Panels(float top, float gap) => new(Work.Left, Work.Bottom, Cards.Left - gap, top);

    /// <summary>The height of each of the four cards, all the same: <paramref name="most"/>, or less to fit the four in the cards' zone.</summary>
    internal float CardHeight(float most, float gap) => Math.Max(0f, Math.Min(most, (Cards.Height - 3f * gap) / 4f));

    /// <summary>Card <paramref name="index"/> (0 to 3, top down) of height <paramref name="height"/>, at a fixed place from the top of the cards' zone.</summary>
    internal HudBox Card(int index, float height, float gap)
    {
        var top = Cards.Top - index * (height + gap);
        return new HudBox(Cards.Left, top - height, Cards.Right, top);
    }
}

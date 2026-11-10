using System;
using ForgeUI;

namespace ForgeReplay;

/// <summary>The replay screen's fixed zones on the current screen, none over another: along the bottom the time bar and above it the control row;
/// in what is left the mission card top left, the kill feed top right with the players' cards under it, and the middle between the two for the
/// game's own lines, scans and notices. The panels stand left of the cards, from the control row up to under the mission card
/// (<see cref="Panels"/>); an open panel makes the middle give way (ReplayScreen).</summary>
internal readonly record struct ReplayZones(HudBox Work, HudBox Control, HudBox Mission, HudBox Feed, HudBox Cards, HudBox Middle)
{
    private const float SideShare = 0.26f, SideMin = 420f, SideMax = 760f;

    /// <param name="screen">The screen's edges.</param>
    /// <param name="margin">The player's safe margin.</param>
    /// <param name="bar">The time bar's height from the bottom edge.</param>
    /// <param name="control">The control row's height.</param>
    /// <param name="gap">The space between zones.</param>
    /// <param name="cardWidth">How wide the cards want to be (never wider than the side share of the screen).</param>
    /// <param name="feedHeight">The kill feed's height.</param>
    internal static ReplayZones Of(HudBox screen, float margin, float bar, float control, float gap, float cardWidth, float feedHeight)
    {
        var left = screen.Left + margin;
        var right = screen.Right - margin;
        var controlBottom = screen.Bottom + bar + gap;
        var controlBox = new HudBox(left, controlBottom, right, controlBottom + control);
        var work = new HudBox(left, controlBox.Top + gap, right, screen.Top - margin);
        var side = Math.Clamp(work.Width * SideShare, Math.Min(SideMin, work.Width / 3f), SideMax);
        var feed = new HudBox(right - side, work.Top - feedHeight, right, work.Top);
        var cards = new HudBox(right - Math.Min(cardWidth, side), work.Bottom, right, feed.Bottom - gap);
        var mission = new HudBox(left, work.Bottom, left + side, work.Top);
        var middle = new HudBox(mission.Right + gap, work.Bottom, feed.Left - gap, work.Top);
        return new ReplayZones(work, controlBox, mission, feed, cards, middle);
    }

    /// <summary>The room for a panel when the mission card ends at <paramref name="top"/>: above the control row, left of the cards.</summary>
    internal HudBox Panels(float top, float gap) => new(Work.Left, Work.Bottom, Cards.Left - gap, top);
}

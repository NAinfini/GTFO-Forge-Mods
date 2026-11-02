using ForgeUI;

/// <summary>The power-on message's keep-out (the stack rises until it clears a box in crosshair space), its lifted message and its
/// coloured status word.</summary>
internal static class BootLayoutChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var words = new BootMessageWords { Header = "H", Stall = "S", Ok = "OK", Message = "READY", Down = "OFF", Checks = new[] { "A", "B", "C" } };
        var keep = new HudBox(-150f, -80f, 150f, 90f);
        var free = new BootMessage(new BootMessageBeats(), new BootMessageLook { LineY = 20f });
        free.Prepare(words);
        var kept = new BootMessage(new BootMessageBeats(), new BootMessageLook { LineY = 20f, MessageLift = 80f, Keepout = keep, KeepoutGap = 10f });
        kept.Prepare(words);
        check(free.LineY == 20f, "boot message: without a keep-out the bar's line stays where the look puts it");
        var extent = kept.Extent(kept.LineY);
        var inCrosshair = extent.Bottom + HudRegions.BannerRise;
        check(kept.LineY > 20f && Math.Abs(inCrosshair - (keep.Top + 10f)) < 1e-3f, "boot message: overlapping the keep-out, the whole stack rises until it clears it by the gap");
        var away = new BootMessage(new BootMessageBeats(), new BootMessageLook { LineY = 20f, OriginRise = 900f, Keepout = keep });
        away.Prepare(words);
        check(away.LineY == 20f, "boot message: a stack that never meets the keep-out is not moved");
        check(kept.Extent(0f).Top - 30f >= 80f + 0f && kept.Extent(0f).Top > free.Extent(0f).Top, "boot message: the lifted message stands over the header and the bar and is part of its extent");

        kept.Build(1.0f, -1f);
        check(kept.Height >= 2f * Math.Max(-kept.Extent(kept.LineY).Bottom, kept.Extent(kept.LineY).Top) - 1e-3f, "boot message: it asks for room on both sides of its centre for everything it can draw");

        var green = new HudColour(0.2f, 0.9f, 0.3f, 1f);
        var coloured = new BootMessage(new BootMessageBeats(), new BootMessageLook { Status = green });
        coloured.Prepare(words);
        coloured.Build(1.0f, -1f);
        var ok = Enumerable.Range(0, coloured.Count).Select(i => coloured.Items[i]).Where(i => i.Kind == BootMessageItemKind.Text && i.Text == "OK").ToList();
        var name = Enumerable.Range(0, coloured.Count).Select(i => coloured.Items[i]).First(i => i.Kind == BootMessageItemKind.Text && i.Text == "A");
        check(ok.Count > 0 && ok.All(i => i.Colour.G < 1f && (i.Colour.R == 1f || Math.Abs(i.Colour.G - 0.9f) < 1e-3f)) && ok.Any(i => Math.Abs(i.Colour.G - 0.9f) < 1e-3f) && name.Colour.R > 0.7f,
            "boot message: the status word ending a line takes its own colour after its flash, the line's name keeps the accent");
    }
}
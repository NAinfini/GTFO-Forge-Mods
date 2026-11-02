using ForgeUI;

/// <summary>The held magazine (which slot's rounds the crosshair arc reads, no magazine = unlimited, an empty magazine still shown)
/// and when the crosshair stand-in is drawn (hidden while aiming, back after).</summary>
internal static class HeldMagazineChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var pistol = new SlotFacts(true, 15, 15, false);
        var rifle = new SlotFacts(false, 60, 60, false);
        var scanner = new SlotFacts(false, -1, -1, false);
        var hammer = new SlotFacts(true, -1, -1, false);
        var held = HeldMagazine.Pick(new[] { rifle, pistol, scanner });
        check(held is { Limited: true, Clip: 15, Size: 15 } && held.Fraction == 1f, "held magazine: the selected slot's rounds, not the first slot's");
        held = HeldMagazine.Pick(new[] { rifle, scanner, hammer });
        check(!held.Limited && held.Fraction == 1f, "held magazine: a tool or melee weapon without a magazine is unlimited");
        check(!HeldMagazine.Pick(new[] { rifle, pistol with { Selected = false } }).Limited && !HeldMagazine.Pick(ReadOnlySpan<SlotFacts>.Empty).Limited,
            "held magazine: nothing selected (a carried item, an empty hand) is unlimited");
        check(!HeldMagazine.Of(new SlotFacts(true, 30, 60, true)).Limited, "held magazine: an infinite supply is unlimited");

        var empty = HeldMagazine.Pick(new[] { new SlotFacts(true, 0, 60, false), rifle });
        check(empty is { Limited: true, Clip: 0, Size: 60 } && empty.Fraction == 0f, "held magazine: an emptied magazine is still a magazine, 0 of 60");
        check(HeldMagazine.Pick(new[] { new SlotFacts(true, 20, 60, false) }).Fraction == 20f / 60f && new HeldMagazine(true, 3, 0).Fraction == 1f && new HeldMagazine(true, 0, 0).Fraction == 0f,
            "held magazine: the fraction is rounds over size, clamped, 1 for rounds without a known size");

        check(CrosshairRules.Drawn(true, true, false) && !CrosshairRules.Drawn(true, true, true) && CrosshairRules.Drawn(true, true, false),
            "crosshair: hidden while aiming, drawn again once the aim is released");
        check(CrosshairRules.Graphics(true, true, true, true, true, 4, true) == 0 && CrosshairRules.Graphics(true, true, false, false, true, 4, true) == 6 && CrosshairRules.Graphics(true, true, true, false, true, 4, true) == 0,
            "crosshair: while aiming no graphic is drawn in the middle (ring, dot, ticks and the landing mark), after the release they all return");
        check(CrosshairRules.Drawn(true, false, true) && !CrosshairRules.Drawn(false, true, false), "crosshair: a mark that does not stand in for it ignores the aim; an invisible one stays hidden");
    }
}
using ForgeUI;

/// <summary>The readouts' pure parts: a teammate's ammunition fraction and which counts a weapon list slot shows.</summary>
internal static class ReadoutChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Ammo(check);
        Shown(check);
        Interaction(check);
    }

    /// <summary>The interaction layer as the game writes it: its five styles, the scan's player rule and the bar's fill from the sprite.</summary>
    private static void Interaction(Action<bool, string> check)
    {
        check(InteractionReading.Style(0) == InteractionStyle.Usable && InteractionReading.Style(1) == InteractionStyle.Message
              && InteractionReading.Style(2) == InteractionStyle.Scan && InteractionReading.Style(3) == InteractionStyle.Warning
              && InteractionReading.Style(4) == InteractionStyle.ScanAlarm && InteractionReading.Style(9) == InteractionStyle.Message,
            "interaction: the game's Default, Message, Bioscan, Warning and BioscanAlarm styles, an unknown one a plain message");
        check(InteractionReading.Requirement(0) == ScanRequirement.Any && InteractionReading.Requirement(1) == ScanRequirement.All
              && InteractionReading.Requirement(2) == ScanRequirement.Solo && InteractionReading.Requirement(7) == ScanRequirement.Any,
            "scan: the game's None, All and Solo player rules");
        check(Near(InteractionReading.Fill(true, 50f, 200f), 0.25f) && Near(InteractionReading.Fill(true, 300f, 200f), 1f),
            "interaction: the bar's fill is its sprite's width over the full width, at most 1");
        check(InteractionReading.Fill(false, 50f, 200f) < 0f && InteractionReading.Fill(true, 0f, 200f) < 0f && InteractionReading.Fill(true, 50f, 0f) < 0f,
            "interaction: no bar while the game hides it, draws it at no width or has no full width");
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-5f;

    private static void Ammo(Action<bool, string> check)
    {
        // Standard cap 600, special cap 100, as the game's ammo storage reports them.
        foreach (var (std, spc, expected) in new[] { (0f, 0f, 0f), (72f, 12f, 0.12f), (150f, 25f, 0.25f), (360f, 60f, 0.6f), (600f, 100f, 1f) })
            check(Near(TeammateAmmo.Rel(std, 600f, spc, 100f), expected), "teammate ammo " + expected * 100f + " percent");
        check(Near(TeammateAmmo.Rel(300f, 600f, 0f, 0f), 0.5f), "no special weapon: only the main weapon counts");
        check(Near(TeammateAmmo.Rel(300f, 600f, 50f, -1f), 0.5f), "a negative special cap counts as none");
        check(TeammateAmmo.Rel(0f, 0f, 0f, 0f) == -1f, "no cap at all gives -1");
        check(TeammateAmmo.Rel(5f, 0f, 5f, 0f) == -1f, "zero caps give -1 whatever is in the pack");
        check(TeammateAmmo.Rel(0f, 0f, 30f, 60f) == 0.5f, "a special cap alone is enough to compare against");
        check(TeammateAmmo.Rel(900f, 600f, 0f, 0f) == 1f, "over the cap is clamped to 1");
        check(TeammateAmmo.Rel(-5f, 600f, 0f, 0f) == 0f, "below zero is clamped to 0");
        check(TeammateAmmo.Rel(float.NaN, 600f, 0f, 0f) == -1f, "an unreadable count gives -1");
    }

    private static void Shown(Action<bool, string> check)
    {
        check(InventoryAmmo.Shown(true, 12) == 12 && InventoryAmmo.Shown(false, 12) == -1, "a clip count the list does not show is -1");
        check(InventoryAmmo.Shown(true, 0) == 0, "a shown empty clip stays 0");
        check(Near(InventoryAmmo.Shown(true, 0.4f), 0.4f) && InventoryAmmo.Shown(false, 0.4f) == -1f, "a relative reserve the list does not show is -1");
    }
}

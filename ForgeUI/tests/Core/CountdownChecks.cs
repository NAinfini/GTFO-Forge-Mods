using ForgeUI;

/// <summary>The countdown marker's pure parts: the tenth of a second it shows, the label's text and where it stands beside the thing
/// it marks.</summary>
internal static class CountdownChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(Countdown.Tenth(1.15f) == 1.2m && Countdown.Tenth(1.149f) == 1.1m && Countdown.Tenth(0.04f) == 0.0m && Countdown.Tenth(-1f) == 0.0m && Countdown.Tenth(4.95f) == 5.0m,
            "countdown: the tenth rounds half away from zero on the seven digits the label shows, and never goes below zero");

        check(Countdown.Urgent(3f) && !Countdown.Urgent(3.01f) && Countdown.Urgent(0f), "countdown: urgent over the last three seconds");
        var calm = Countdown.Digits(12.34f);
        var urgent = Countdown.Digits(2.5f);
        check(calm.Contains("<mspace=0.58em>12</mspace>.<mspace=0.58em>3</mspace>") && calm.Contains("size=128%"), "countdown: monospaced digits around a narrow point");
        check(urgent.Contains("<mspace=0.58em>2</mspace>.<mspace=0.58em>5</mspace>") && urgent.Contains("size=150%"), "countdown: the digits grow when urgent");
        check(Countdown.Digits(-1f).Contains("<mspace=0.58em>0</mspace>.<mspace=0.58em>0</mspace>"), "countdown: a passed moment reads 0.0");

        var name = Countdown.NamePart("a<b>");
        check(name.Contains("<noparse>A<B></noparse>"), "countdown: the name is upper case and shown as written (no markup from it)");
        var label = Countdown.Label(name, 5f, "88CCFF", "5.0s");
        var late = Countdown.Label(name, 2f, "88CCFF", "2.0s");
        check(label.Contains("<color=#88CCFF>") && !label.Contains(Countdown.UrgentHex) && late.Contains("<color=#" + Countdown.UrgentHex + ">") && !late.Contains("88CCFF"),
            "countdown: the unit follows the accent colour, red when urgent");

        // Eye at the origin looking north (+Z), the marked thing 10 m ahead on the floor.
        var far = Countdown.Beside(0f, 1.7f, 0f, 0f, 1f, 0f, 0f, 10f, 0.24f);
        check(Near(far.X, 2.4f) && Near(far.Z, 10f) && Near(far.Y, 1.55f + 0.2f), "countdown: far away, beside the thing to the eye's right, a clear gap in proportion to distance");
        var near = Countdown.Beside(0f, 1.7f, 0f, 0f, 1f, 0f, 0f, 1f, 0.24f);
        check(Near(near.Z, Countdown.NearReach) && near.X >= 0.5f, "countdown: closer than the near reach it stays that far ahead");
        var on = Countdown.Beside(0f, 1.7f, 0f, 1f, 0f, 0f, 0f, 0f, 0.24f);
        check(Near(on.X, Countdown.NearReach) && Near(on.Z, -Countdown.NearReach * 0.24f), "countdown: standing on it, it goes ahead along the view, right of it");
        var up = Countdown.Beside(0f, 1.7f, 0f, 0f, 0f, 0f, 0f, 0f, 0.24f);
        check(Near(up.Z, Countdown.NearReach) && Near(up.X, Countdown.NearReach * 0.24f), "countdown: looking straight up or down falls back to north");
        var high = Countdown.Beside(0f, 10f, 0f, 0f, 1f, 0f, 0f, 10f, 0.24f);
        check(Near(high.Y, 2.4f + 10f * 0.02f), "countdown: the marker never stands more than 2.4 m above the floor point");
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-3f;
}

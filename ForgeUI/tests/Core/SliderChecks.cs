using ForgeUI;

internal static class SliderChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(SliderMath.ValueAt(-2, -2, 2) == 0 && SliderMath.ValueAt(2, -2, 2) == 1 && SliderMath.ValueAt(0, -2, 2) == .5f, "slider row: the handle's ends are 0 and full");
        check(SliderMath.ValueAt(9, -2, 2) == 1 && SliderMath.ValueAt(float.NaN, -2, 2) == 0 && SliderMath.ValueAt(1, 2, 2) == 0, "slider row: a value is always 0..1");
        check(SliderMath.PositionOf(.25f, -2, 2) == -1 && SliderMath.PositionOf(3, -2, 2) == 2, "slider row: a value places the handle between the ends");
        check(SliderMath.Step(0, -.5f, -2, 2) == .5f && SliderMath.Step(0, .5f, -2, 2) == -.5f, "slider row: a drag moves the handle by minus its delta, as the game's slider");
        check(SliderMath.Step(1.8f, -1, -2, 2) == 2 && SliderMath.Step(-1.8f, 1, -2, 2) == -2, "slider row: the handle stops at the ends");
        var round = true;
        for (var i = 0; i <= 100; i++) round &= Math.Abs(SliderMath.ValueAt(SliderMath.PositionOf(i / 100f, -1.3f, 4.2f), -1.3f, 4.2f) - i / 100f) <= 1e-5f;
        check(round, "slider row: a value shown on the handle reads back the same");
        check(SliderMath.Text(.854f) == "85" && SliderMath.Text(1) == "100" && SliderMath.Text(-1) == "0", "slider row: the number shows whole percent");
    }
}

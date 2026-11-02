using ForgeUI;

internal static class TimelineChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(TimelineMath.Fraction(500, 1000) == 0.5f && TimelineMath.Fraction(2000, 1000) == 1f && TimelineMath.Fraction(5, 0) == 0f, "time bar: a moment's place is 0..1, empty duration at 0");
        check(TimelineMath.At(-100f, -100f, 200f, 60000) == 0 && TimelineMath.At(0f, -100f, 200f, 60000) == 30000 && TimelineMath.At(500f, -100f, 200f, 60000) == 60000, "time bar: a point stands for a moment, clamped to the bar");
        var marks = new uint[] { 10000, 30000, 31000 };
        check(TimelineMath.NearestMark(0f, -100f, 200f, 60000, marks.Length, i => marks[i], 6f) == 1, "time bar: the nearest mark within reach is picked");
        check(TimelineMath.NearestMark(50f, -100f, 200f, 60000, marks.Length, i => marks[i], 6f) == -1, "time bar: no mark out of reach");
        check(TimelineMath.Clock(0) == "0:00" && TimelineMath.Clock(65400) == "1:05" && TimelineMath.Clock(3_725_000) == "1:02:05", "time bar: clock text");
        check(TimelineMath.Speed(0.25f) == "0.25×" && TimelineMath.Speed(8f) == "8×", "time bar: speed text");
    }
}

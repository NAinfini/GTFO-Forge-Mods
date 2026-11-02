namespace ForgeCore.Tests;

internal static class LevelIdentityChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var id = new LevelId(37, 4, 0);
        check(id.Coordinate == "37-D1" && LevelId.TryParse("37-D1", out var back) && back == id && LevelId.TryParse("37-d1", out back) && back == id,
            "levelid: the coordinate form writes and reads back, in either letter case");
        var bad = new[] { "", "37", "-D1", "37-", "37-F1", "37-D0", "37-D", "x-D1", "0-A1", "37-D1x", "37--D1" };
        var rejected = true;
        foreach (var text in bad) rejected &= !LevelId.TryParse(text, out var none) && none == default;
        check(rejected && !LevelId.TryParse(null, out _), "levelid: malformed text is refused");
    }
}
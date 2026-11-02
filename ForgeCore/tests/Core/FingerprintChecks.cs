namespace ForgeCore.Tests;

internal static class FingerprintChecks
{
    private sealed class Block { public uint Id; public string Name = ""; public float Scale; public int[] Rolls = Array.Empty<int>(); public Block? Parent; public List<Block> Children { get; } = new(); }

    private struct Vec { public float X, Y; public Vec(float x, float y) { X = x; Y = y; } public Vec Normalized => new(X / 2f, Y / 2f); }
    private sealed class Holder { public Vec Point; }

    private static Block Make(string name, float scale, params int[] rolls) => new() { Id = 7, Name = name, Scale = scale, Rolls = rolls };

    internal static void Run(Action<bool, string> check)
    {
        var a = new Fingerprint().Add("x", 1L).Add("y", "z").Add("f", 1.2345678, 2).Hex();
        var b = new Fingerprint().Add("x", 1L).Add("y", "z").Add("f", 1.2349, 2).Hex();
        var c = new Fingerprint().Add("y", "z").Add("x", 1L).Add("f", 1.23, 2).Hex();
        check(a == b && a.Length == 32 && a != c, "fingerprint: the same labelled values give one hash, floats round to the stated places, order matters");
        var neg = new Fingerprint().Add("f", -0.0001, 2).Hex();
        check(neg == new Fingerprint().Add("f", 0.0, 2).Hex(), "fingerprint: a value rounding to negative zero equals zero");
        var running = new Fingerprint().Add("a", 1L);
        var mid = running.Hex();
        running.Add("b", 2L);
        check(mid == new Fingerprint().Add("a", 1L).Hex() && running.Hex() != mid, "fingerprint: reading the hash mid-run does not end the run");
        check(new Fingerprint().Add("n", (string?)null).Hex() != new Fingerprint().Add("n", "").Hex(), "fingerprint: null and empty text differ");

        var h1 = ContentHash.Of(Make("a", 1f, 1, 2), out var t1);
        var h2 = ContentHash.Of(Make("a", 1.00001f, 1, 2), out _);
        var h3 = ContentHash.Of(Make("a", 1f, 2, 1), out _);
        var h4 = ContentHash.Of(Make("b", 1f, 1, 2), out _);
        check(h1 == h2 && h1 != h3 && h1 != h4 && !t1, "contenthash: equal contents hash equally across instances, an item order or a value change differs");
        var loop = Make("loop", 1f); loop.Parent = loop; loop.Children.Add(loop);
        var hl = ContentHash.Of(loop, out var tl);
        check(hl.Length == 32 && !tl, "contenthash: a reference cycle is cut and does not hang");
        var deep = Make("deep", 1f); var tip = deep;
        for (var i = 0; i < 30; i++) { var next = Make("n" + i, 1f); tip.Parent = next; tip = next; }
        ContentHash.Of(deep, out var truncated);
        check(truncated, "contenthash: a graph deeper than the limit is reported as truncated");
        check(ContentHash.Of(null, out _) == ContentHash.Of(null, out _), "contenthash: null hashes");
        var v1 = ContentHash.Of(new Holder { Point = new Vec(1f, 2f) }, out var vTruncated);
        check(!vTruncated && v1 == ContentHash.Of(new Holder { Point = new Vec(1f, 2f) }, out _) && v1 != ContentHash.Of(new Holder { Point = new Vec(1f, 2.5f) }, out _),
            "contenthash: a value type is read by its fields, so a property that returns the same type (like a vector's normalized) does not recurse");
    }
}
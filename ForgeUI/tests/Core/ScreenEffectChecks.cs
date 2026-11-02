using ForgeUI;

/// <summary>The screen effect stack's merge, envelopes, handles and capacity, its per-frame path, and that the shipped bundle holds the
/// current stack shader with both of its variants.</summary>
internal static class ScreenEffectChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;
        var book = new ScreenEffectBook();
        int Push(ScreenChannel c, float s, float r, float seconds = 0f, float fadeIn = 0f, float fadeOut = 0f, int priority = 0, float now = 0f, float rate = 1f)
            => book.Push(c, s, r, 0f, 0f, 1f, seconds, fadeIn, fadeOut, priority, rate, now);

        check(!book.Evaluate(0f) && book.Strength(ScreenChannel.Flash) == 0f, "screen effects: nothing pushed, nothing shown");

        // Same priority: the strongest sets the strength, colours blend by strength.
        var weak = Push(ScreenChannel.Flash, 0.2f, 1f);
        var strong = Push(ScreenChannel.Flash, 0.6f, 0f);
        check(book.Evaluate(0f) && Near(book.Strength(ScreenChannel.Flash), 0.6f), "screen effects: the strongest effect sets the channel's strength");
        check(Near(book.R(ScreenChannel.Flash), 0.2f / 0.8f), "screen effects: colours blend weighted by strength");
        check(book.Strength(ScreenChannel.Vignette) == 0f, "screen effects: channels do not leak into each other");

        // A higher priority present: only it counts, however weak.
        var urgent = Push(ScreenChannel.Flash, 0.1f, 0.5f, priority: 5);
        book.Evaluate(0f);
        check(Near(book.Strength(ScreenChannel.Flash), 0.1f) && Near(book.R(ScreenChannel.Flash), 0.5f), "screen effects: only the highest priority present counts");
        check(book.Withdraw(urgent, book.Generation(urgent), 0f) && !book.Withdraw(urgent, book.Generation(urgent), 0f), "screen effects: withdraw once");
        book.Evaluate(0f);
        check(Near(book.Strength(ScreenChannel.Flash), 0.6f) && !book.Live(urgent, book.Generation(urgent) - 1), "screen effects: a withdrawn effect with no fade ends at once and the others count again");
        book.Clear();
        check(book.Count == 0 && !book.Live(weak, 0) && !book.Live(strong, 0), "screen effects: clear ends everything");

        // Envelope: fade in, hold, fade out; a timed effect frees itself.
        var timed = Push(ScreenChannel.Vignette, 0.8f, 1f, seconds: 1f, fadeIn: 0.2f, fadeOut: 0.5f, now: 10f);
        check(Near(book.Level(timed, 10.1f), 0.4f) && Near(book.Level(timed, 10.5f), 0.8f), "screen effects: rises over its fade-in, then holds");
        check(Near(book.Level(timed, 11.25f), 0.4f) && book.Level(timed, 11.5f) == 0f, "screen effects: fades out after its seconds");
        var generation = book.Generation(timed);
        book.Evaluate(11.6f);
        check(!book.Live(timed, generation) && book.Count == 0, "screen effects: a faded effect frees its slot");
        check(!book.Withdraw(timed, generation, 12f) && !book.Set(timed, generation, 1f, 1f, 1f, 1f, 1f), "screen effects: an old handle does nothing");

        // A held effect driven every frame, then withdrawn with a fade from where it is.
        var held = Push(ScreenChannel.Vignette, 0.3f, 1f, fadeOut: 1f, now: 0f);
        check(book.Set(held, book.Generation(held), 0.62f, 0.05f, 0.08f, 0.11f, 1f) && Near(book.Level(held, 100f), 0.62f), "screen effects: a held effect follows its new strength");
        book.Evaluate(100f);
        check(Near(book.B(ScreenChannel.Vignette), 0.11f), "screen effects: a held effect follows its new colour");
        book.Withdraw(held, book.Generation(held), 100f);
        check(Near(book.Level(held, 100.5f), 0.31f) && !book.Set(held, book.Generation(held), 1f, 1f, 1f, 1f, 1f), "screen effects: withdrawing fades from the current level and stops changes");
        book.Clear();

        // The low-health pulse rate comes from the strongest effect; the pulse stays between 0.6 and 1.
        Push(ScreenChannel.LowHealth, 0.3f, 1f, rate: 0.5f);
        Push(ScreenChannel.LowHealth, 0.9f, 1f, rate: 2f);
        book.Evaluate(0f);
        check(book.Rate(ScreenChannel.LowHealth) == 2f, "screen effects: pulse rate of the strongest");
        var low = 2f; var high = 0f;
        for (var t = 0f; t < 1f; t += 0.01f) { var p = ScreenEffectBook.Pulse(2f, t); low = Math.Min(low, p); high = Math.Max(high, p); }
        check(low >= 0.6f - 1e-4f && high <= 1f + 1e-4f && high - low > 0.35f && ScreenEffectBook.Pulse(0f, 3f) == 1f, "screen effects: pulse between 0.6 and 1, steady at rate 0");
        book.Clear();

        // Bad values are refused with a reason; a full book refuses instead of dropping an effect.
        check(ScreenEffectBook.Problem(ScreenChannel.Blur, 1f, 1f, 1f, 1f, 1f, 0f, 0f, 0f, 0f) == null, "screen effects: good values pass");
        check(ScreenEffectBook.Problem((ScreenChannel)9, 1f, 1f, 1f, 1f, 1f, 0f, 0f, 0f, 0f) != null
            && ScreenEffectBook.Problem(ScreenChannel.Flash, float.NaN, 1f, 1f, 1f, 1f, 0f, 0f, 0f, 0f) != null
            && ScreenEffectBook.Problem(ScreenChannel.Flash, 1f, 1f, 1f, 1f, 2f, 0f, 0f, 0f, 0f) != null
            && ScreenEffectBook.Problem(ScreenChannel.Flash, 1f, 1f, 1f, 1f, 1f, -1f, 0f, 0f, 0f) != null
            && ScreenEffectBook.Problem(ScreenChannel.Flash, 1f, 1f, 1f, 1f, 1f, 0f, -1f, 0f, 0f) != null, "screen effects: bad channel, strength, alpha, seconds or fade refused");
        for (var i = 0; i < ScreenEffectBook.Capacity; i++) Push(ScreenChannel.Blur, 0.5f, 0f);
        check(Push(ScreenChannel.Blur, 0.5f, 0f) == -1 && book.Count == ScreenEffectBook.Capacity, "screen effects: a full book refuses the next effect");
        book.Clear();

        // The per-frame path allocates nothing.
        for (var i = 0; i < 20; i++) Push((ScreenChannel)(i % ScreenEffectBook.Channels), 0.5f, 1f, seconds: 0.5f + i * 0.1f, fadeIn: 0.1f, fadeOut: 0.2f, priority: i % 3);
        book.Evaluate(0.3f);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 0; f < 1000; f++)
        {
            book.Evaluate(f * 0.002f);
            var slot = book.Push(ScreenChannel.Flash, 0.3f, 1f, 0f, 0f, 1f, 0.05f, 0f, 0.01f, 0, 1f, f * 0.002f);
            if (slot >= 0) book.Set(slot, book.Generation(slot), 0.4f, 1f, 1f, 1f, 1f);
            ScreenEffectBook.Pulse(1.2f, f * 0.002f);
        }
        check(GC.GetAllocatedBytesForCurrentThread() == before, "screen effects: evaluating, pushing and setting allocate nothing");

        // The shipped bundle is built from the current stack shader; it has the grabbing and the plain variant and draws the layers in
        // the order the separate overlay quads had (hit bands under the shade).
        var kit = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));
        var source = File.ReadAllText(Path.Combine(kit, "tools", "screen", "ForgeUIScreenEffects.shader"));
        static string Sha(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var lf = source.Replace("\r\n", "\n");
        var generated = File.ReadAllText(Path.Combine(kit, "Native", "ScreenEffectsShader.Generated.cs"));
        check(generated.Contains("SourceSha256 = \"" + Sha(lf) + "\"") || generated.Contains("SourceSha256 = \"" + Sha(lf.Replace("\n", "\r\n")) + "\""),
            "screen effects: bundle built from the current shader");
        check(lf.Contains("LOD 200") && lf.Contains("GrabPass { \"_ForgeScreenGrab\" }") && lf.Contains("LOD 100") && lf.IndexOf("LOD 200") < lf.IndexOf("LOD 100"),
            "screen effects: the grabbing variant first, the plain one after it");
        var order = new[] { "band(_Band0, p)", "vignette(uv)", "heatGlow(uv)", "_Interference.a", "sweepTrail(sweep)", "lowHealth(uv)", "_Flash.a" }
            .Select(token => lf.IndexOf(token, StringComparison.Ordinal)).ToArray();
        check(order.All(i => i > 0) && order.Zip(order.Skip(1), (a, b) => a < b).All(x => x), "screen effects: layers in order (hit bands, shade, heat, interference, sweep, low health, flash)");
    }

    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}

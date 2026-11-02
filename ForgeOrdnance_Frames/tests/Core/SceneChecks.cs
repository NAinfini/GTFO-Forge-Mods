using System.Linq;
using ForgeOrdnanceFrames;
using ForgeUI;

/// <summary>The suit's power-on message as ForgeUI's power-on message plays it with the suit's beats, colours and words (the message's
/// own steps are checked in ForgeUI): the suit's words in the player's language, its cockpit colours, its room over the crosshair and
/// the red power-down word on the suit's timing.</summary>
internal static class SceneChecks
{
    private static IEnumerable<BootMessageItem> Items(BootMessage scene) => scene.Items.Take(scene.Count);

    private static BootMessage Scene(float boot, float down = -1f, string language = "en")
    {
        var scene = new BootMessage(FrameBootMessage.Beats, FrameBootMessage.Look);
        scene.Prepare(FrameBootMessage.Words(LangChecks.Words(language), 80));
        scene.Build(boot, down);
        return scene;
    }

    private static List<string?> Texts(BootMessage scene) => Items(scene).Where(i => i.Kind == BootMessageItemKind.Text).Select(i => i.Text).ToList();

    internal static void Run(Action<bool, string> check)
    {
        var words = LangChecks.Words();
        var beats = FrameBootMessage.Beats;

        var testing = Texts(Scene(1.0f));
        check(testing.Contains(words.BootSeal) && testing.Contains(FrameWords.Format(words.BootArmor, 80)) && testing.Contains(words.BootOk) && !testing.Contains(words.Link)
              && Enumerable.Range(0, 28).Any(k => Texts(Scene(beats.StallStart + 0.005f + k * 0.01f)).Contains(words.BootSync)),
            "the self-test lists the suit's checks with its armour share, the bar stalls on SYNC, NEURAL LINK OK waits");

        var locked = Items(Scene(BootPlan.MessageAt(BootPlan.LinkLock) + 0.2f)).Where(i => i.Kind == BootMessageItemKind.Text && i.Text == words.Link).ToList();
        var accent = FrameBootMessage.Look.Accent;
        check(locked.Count >= 2 && locked.Any(i => i.Colour.A >= 0.99f && Math.Abs(i.Colour.R - accent.R) < 1e-3f && Math.Abs(i.Colour.B - 1f) < 1e-3f),
            "NEURAL LINK OK locks in the cockpit's cyan-white on the beat the ready voice plays");
        var split = Items(Scene((beats.MessageStart + beats.MessageLock) / 2f)).Count(i => i.Kind == BootMessageItemKind.Text && i.Colour.R > 0.9f && i.Colour.G < 0.5f);
        check(split == 1, "it decodes with the suit's red-blue split");

        // The slot's centre sits 150 over the crosshair (ForgeUI's HudRegions.BannerRise); the arcs beside the crosshair reach about 76 over it (radius 96,
        // 72 degrees, the icon at the end). Over the whole message: everything inside the slot, nothing low enough to touch the arcs, and
        // at the lock the self-test lines all under NEURAL LINK OK.
        float low = float.MaxValue, high = float.MinValue;
        for (var b = 0f; b < beats.Duration; b += 0.02f)
            foreach (var i in Items(Scene(b)))
            {
                var h = i.Kind switch { BootMessageItemKind.Text => i.Px / 2f, BootMessageItemKind.Line => 0f, _ => i.H / 2f };
                var y0 = i.Kind == BootMessageItemKind.Line ? Math.Min(i.Y, i.H) : i.Y - h;
                var y1 = i.Kind == BootMessageItemKind.Line ? Math.Max(i.Y, i.H) : i.Y + h;
                low = Math.Min(low, y0); high = Math.Max(high, y1);
            }
        var atLock = Items(Scene(beats.MessageLock + 0.05f)).Where(i => i.Kind == BootMessageItemKind.Text).ToList();
        var message = atLock.First(i => i.Text == words.Link);
        var checks = atLock.Where(i => i.Text == words.BootSeal || i.Text == words.BootSensors).ToList();
        var room = Scene(1.0f).Height;
        check(high <= room / 2f && low >= -room / 2f && HudRegions.BannerRise + low > FrameBootMessage.Keepout.Top
              && checks.Count == 2 && checks.All(i => i.Y < message.Y - message.Px / 2f),
            "the message fits its slot, clears the crosshair's arcs, and its self-test stands under NEURAL LINK OK");
        var decoding = Texts(Scene(99f, (beats.DownStart + beats.DownDecoded) / 2f));
        var shutdown = Items(Scene(99f, beats.DownDecoded + 0.05f)).Where(i => i.Kind == BootMessageItemKind.Text && i.Text == words.Shutdown).ToList();
        check(decoding.Count > 0 && !decoding.Contains(words.Shutdown) && shutdown.Any(i => i.Colour.R > 0.95f && i.Colour.G < 0.3f && i.Colour.A > 0.99f)
              && Scene(99f, FrameBootScreen.DownDark).Count == 0 && words.Shutdown == "SHUTDOWN",
            "leaving, a red SHUTDOWN decodes in the middle and is gone once every panel is black");

        check(Texts(Scene(BootPlan.MessageAt(BootPlan.LinkLock) + 0.2f, -1f, "zh")).Contains("神经链接已建立") && Texts(Scene(1.0f, -1f, "zh")).Contains("密封"),
            "the message follows the language");
    }
}

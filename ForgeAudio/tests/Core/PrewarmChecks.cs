namespace ForgeAudio.Tests;

internal static class PrewarmChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var none = new PrewarmProgress("Mod", Array.Empty<string>());
        check(none.Done && none.Current == null, "a library with no one-shot cue has nothing to prewarm");

        var cues = CueCatalog.Build(new[] { "boom.ogg", "hum.ogg", "step.ogg" },
            "{\"buses\":{\"b\":{\"category\":\"weapon\"}},\"cues\":{\"boom\":{\"bus\":\"b\"},\"hum\":{\"bus\":\"b\",\"loop\":true},\"step\":{\"bus\":\"b\"}}}");
        check(cues.Error == null && string.Join(",", PrewarmProgress.OneShots(cues.Cues.Values.OrderBy(c => c.Name))) == "boom,step", "the prewarm starts every cue that does not loop and no loop");

        var run = new PrewarmProgress("Mod", new[] { "a", "b", "c" });
        check(run.Current == "a" && !run.Done, "the prewarm starts at the first cue");
        check(run.Report(PrewarmStep.Waiting) == null && run.Current == "a", "waiting for a listener tries the same cue again");
        check(run.Report(PrewarmStep.Started) == null && run.Current == "b", "a started cue moves to the next one");
        check(run.Report(PrewarmStep.Started) == null && run.Current == "c", "the next started cue moves on");
        check(run.Report(PrewarmStep.Started) == null && run.Done && run.Current == null, "the last started cue ends the run without a line");
        check(run.Report(PrewarmStep.Started) == null, "a finished run reports nothing more");

        var refused = new PrewarmProgress("Mod", new[] { "a", "b" });
        string? ending = null;
        for (var i = 0; i < PrewarmProgress.MaxRefused - 1; i++) ending ??= refused.Report(PrewarmStep.Refused, "busy");
        check(ending == null && !refused.Done && refused.Current == "a", "a refused cue is tried again until the limit");
        ending = refused.Report(PrewarmStep.Refused, "busy");
        check(refused.Done && ending != null && ending.Contains("a was refused " + PrewarmProgress.MaxRefused + " times (busy)"), "the limit ends the run with an error line naming the cue: " + ending);

        var recovered = new PrewarmProgress("Mod", new[] { "a", "b" });
        for (var i = 0; i < PrewarmProgress.MaxRefused - 1; i++) recovered.Report(PrewarmStep.Refused);
        recovered.Report(PrewarmStep.Started);
        for (var i = 0; i < PrewarmProgress.MaxRefused - 1; i++) recovered.Report(PrewarmStep.Refused);
        check(!recovered.Done, "the refusal count restarts after a cue started");

        var have = new HashSet<string> { "x", "y" };
        check(CueRequirement.Missing(have.Contains, new[] { "x", "y" }) == null, "no cue is missing when the library has them all");
        check(CueRequirement.Missing(have.Contains, new[] { "x", "q", "y", "r" }) == "q, r", "every missing cue is named, in order");
    }
}

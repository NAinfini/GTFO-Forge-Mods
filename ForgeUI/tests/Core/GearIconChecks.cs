using ForgeUI;
using Gear;
using UnityEngine;

/// <summary>A supplied gear's icon job is loaded by GearIcons instead of the game's LoadGearForIconJob, so it must leave what that native
/// method leaves (read from GameAssembly.dll: one IconRenderJobResult per requested picture naming the gear, then the loading mark) and
/// what the assembly callback leaves (the loaded object, the loaded mark). RenderIconJob reads results[currentIconIndex] for every
/// picture; a job without them threw there on every frame and stopped the renderer's queue, so no later icon of any gear was drawn.</summary>
internal static class GearIconChecks
{
    private static GearIconRendering.IconRenderJob Job(GearIDRange gear, int pictures) =>
        new() { gearToRender = gear, datas = new IconRenderJobData[pictures] };

    internal static void Run(Action<bool, string> check)
    {
        // The writes the renderer reads.
        foreach (var pictures in new[] { 1, 2 })
        {
            var gear = new GearIDRange();
            var job = Job(gear, pictures);
            IconJobLoad.Begin(job);
            check(job.asyncLoadingStarted && !job.asyncLoadingDone, pictures + " picture(s): the job is loading, not loaded");
            var readable = job.results != null && job.results.Length == pictures;
            for (var i = 0; readable && i < pictures; i++) readable = job.results![i] != null && ReferenceEquals(job.results[i].gearID, gear);
            check(readable, pictures + " picture(s): the renderer finds a result naming the gear for every picture");
            var model = new GameObject();
            IconJobLoad.Complete(job, model);
            check(job.asyncLoadingDone && ReferenceEquals(job.loadedGearGO, model), pictures + " picture(s): the job is loaded with the model");
        }

        // Which jobs are taken over.
        var ready = new GameObject("ready");
        GameObject? answer = null;
        string? reason = null;
        GearIconModel provider = (GearIDRange gear, out string? failure) => { failure = reason; return answer; };
        GearIcons.Supply(7001, provider);
        check(!GearIcons.Load(new GearIconRendering.IconRenderJob { gearToRender = null! }), "a job without gear is left to the game");
        var other = Job(new GearIDRange { Category = 9 }, 1);
        check(!GearIcons.Load(other) && other.results == null && !other.asyncLoadingStarted, "an unsupplied category's job is left to the game, untouched");
        var mine = Job(new GearIDRange { Category = 7001 }, 2);
        check(GearIcons.Load(mine) && mine.results.Length == 2 && mine.asyncLoadingStarted && !mine.asyncLoadingDone && GearIcons.Pending,
            "a supplied category's job gets its results and loading mark and waits");

        // The model is asked every frame; nothing yet keeps the job waiting.
        GearIcons.Tick(1f);
        check(!mine.asyncLoadingDone && GearIcons.Pending, "no model yet: the job keeps waiting");
        answer = ready;
        GearIcons.Tick(2f);
        check(mine.asyncLoadingDone && ReferenceEquals(mine.loadedGearGO, ready) && !GearIcons.Pending, "the model is ready: the job is loaded with it");

        // A failure completes the job with an empty picture and one logged error.
        Plugin.Errors.Clear();
        answer = null; reason = "the model failed to load";
        var failed = Job(new GearIDRange { Category = 7001 }, 1);
        GearIcons.Load(failed);
        GearIcons.Tick(3f);
        check(failed.asyncLoadingDone && failed.loadedGearGO != null && !ReferenceEquals(failed.loadedGearGO, ready) && !GearIcons.Pending,
            "a failed model completes the job with an empty object, never the donor gun");
        check(Plugin.Errors.Count == 1 && Plugin.Errors[0].Contains("7001") && Plugin.Errors[0].Contains("the model failed to load"), "the failure is logged once with its reason");

        // A model that never comes is given up on after 20 seconds.
        Plugin.Errors.Clear();
        answer = null; reason = null;
        var slow = Job(new GearIDRange { Category = 7001 }, 1);
        GearIcons.Load(slow);
        var start = Time.realtimeSinceStartup;
        GearIcons.Tick(start + 19.9f);
        check(!slow.asyncLoadingDone, "a model still loading at 19.9 s keeps the job waiting");
        GearIcons.Tick(start + 20f);
        check(slow.asyncLoadingDone && slow.loadedGearGO != null && Plugin.Errors.Count == 1 && Plugin.Errors[0].Contains("20"), "at 20 s the job completes empty with one logged error");

        // A provider that throws is a failure of that job only; the next job still completes.
        Plugin.Errors.Clear();
        GearIcons.Supply(7002, (GearIDRange gear, out string? failure) => throw new InvalidOperationException("boom"));
        var thrown = Job(new GearIDRange { Category = 7002 }, 1);
        var after = Job(new GearIDRange { Category = 7001 }, 1);
        answer = ready;
        GearIcons.Load(thrown); GearIcons.Load(after);
        GearIcons.Tick(5f);
        check(thrown.asyncLoadingDone && after.asyncLoadingDone && ReferenceEquals(after.loadedGearGO, ready) && Plugin.Errors.Count == 1 && Plugin.Errors[0].Contains("boom") && !GearIcons.Pending,
            "a provider that throws empties its own job, logs the error and does not stop the queue");

        // Supplying: the same provider again keeps the first, another provider is refused and logged.
        Plugin.Errors.Clear();
        GearIcons.Supply(7001, provider);
        check(Plugin.Errors.Count == 0, "the same provider supplying a category again is harmless");
        GearIcons.Supply(7001, (GearIDRange gear, out string? failure) => { failure = null; return null; });
        check(Plugin.Errors.Count == 1 && Plugin.Errors[0].Contains("7001"), "another provider for a supplied category is refused and logged");
        var again = Job(new GearIDRange { Category = 7001 }, 1);
        GearIcons.Load(again); GearIcons.Tick(6f);
        check(ReferenceEquals(again.loadedGearGO, ready), "the first provider keeps the category");

        // The cached picture file is removed once per checksum and run.
        var file = Path.Combine(Path.GetTempPath(), "forgeui-gear-icon-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllText(file, "old");
        GearIconRendering.IconFile = file;
        var range = new GearIDRange { Checksum = 424242 };
        GearIcons.ClearCached(range);
        check(!File.Exists(file), "the cached icon file of a supplied gear is removed");
        File.WriteAllText(file, "new");
        GearIcons.ClearCached(range);
        check(File.Exists(file), "the file is removed once per run: a later picture of the same checksum is kept");
        File.Delete(file);
        GearIconRendering.IconFile = null;
        GearIcons.ClearCached(new GearIDRange { Checksum = 424243 });
    }
}

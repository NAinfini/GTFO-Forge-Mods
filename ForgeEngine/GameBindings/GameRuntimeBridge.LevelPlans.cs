using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using ForgeEngine.Framework;
using ForgeEngine.Logging;

namespace ForgeEngine.GameBindings;

/// <summary>
/// Per-level plan loading (contract P8). Startup reads only the plan index. Choosing an expedition starts reading that
/// level's plan files — decoding, parsing and hashing, pure managed bytes — off the game thread; the game thread adopts
/// the result in one step: in the lobby once the read is done, and at the latest when the level's generation begins,
/// which waits for the read, so a level never starts with another level's plans.
/// </summary>
internal static partial class GameRuntimeBridge
{
    private static PlanIndex? _planIndex;
    private static string? _selectedLevel;
    private static string? _adoptedLevel;
    private static bool _levelAdopted;
    private static Task<IReadOnlyList<PlanCandidate>>? _levelRead;
    private static string? _levelReadKey;

    /// <summary>The expedition chosen in the lobby, as a `level` mount spells it, or null where it names no level.</summary>
    internal static void SelectLevel(string? level)
    {
        _selectedLevel = level;
        ReadLevelPlans(level);
    }

    private static void ReadLevelPlans(string? level)
    {
        if (_planIndex == null || Suspension != null) return;
        if (_levelRead != null ? _levelReadKey == level : _levelAdopted && _adoptedLevel == level) return;
        var files = _planIndex.LevelFiles(level);
        var root = Paths.BepInExRootPath;
        _levelReadKey = level;
        _levelRead = PlanDiscovery.ReadLevel(root, files);
    }

    /// <summary>Adopts the chosen level's plans on the game thread. <paramref name="wait"/> is the generation boundary:
    /// it starts the read if nothing did and waits for it; otherwise only a finished read is adopted.</summary>
    private static void AdoptLevelPlans(RuntimeKernel kernel, bool wait)
    {
        if (_planIndex == null || _dispatching) return;
        // A level that only shows a recording (ForgeCore Puppets.Playback) runs no behaviour: it holds no plan, so no trigger of any
        // package has a subscriber and nothing is dispatched. The plans it unloads are read again for the next level.
        if (ForgeCore.Puppets.Playback)
        {
            if (!_levelAdopted) return;
            using var unload = BeginDispatchBoundary();
            var none = Array.Empty<PlanCandidate>();
            _admittedPlans = AdmittedPlans(none, kernel.AdoptPlanSet(none));
            _adoptedLevel = null; _levelAdopted = false;
            unload.Complete();
            return;
        }
        var level = _selectedLevel;
        if (wait) ReadLevelPlans(level);
        if (_levelRead == null || !wait && !_levelRead.IsCompleted) return;
        var waiting = Stopwatch.GetTimestamp();
        var read = _levelRead;
        var readLevel = _levelReadKey;
        _levelRead = null;
        using var boundary = BeginDispatchBoundary();
        try
        {
            var files = read.GetAwaiter().GetResult();
            var adopting = Stopwatch.GetTimestamp();
            var digests = _planIndex.Digests;
            // The index is startup's snapshot, and the peers compared it: a file whose bytes changed since then is
            // refused here like any other change that did not go through a restart or an authoring reload.
            var current = files.Select(file => file.IsHostRejected || digests.TryGetValue(file.Path, out var digest) && digest == file.ContentDigest
                ? file : PlanCandidate.Rejected(file.Path, ContentPackDiscovery.CodeInvalid, "The plan file changed after startup; restart or reload plans.")).ToArray();
            var admitted = Admit(kernel, current);
            var outcomes = kernel.AdoptPlanSet(admitted);
            _admittedPlans = AdmittedPlans(admitted, outcomes);
            _adoptedLevel = readLevel; _levelAdopted = true;
            var finished = Stopwatch.GetTimestamp();
            // `level=none`: no expedition was chosen before this level began, so only plans without a level mount load.
            if (kernel.RuntimeLogs(RuntimeLogLevel.Info))
                kernel.LogRuntime(RuntimeLogLevel.Info, RuntimeHostLogCodes.PlanAdopted, new RuntimeLogFacts
                {
                    Values = new[]
                    {
                        RuntimeLogValue.Of("level", readLevel ?? "none"),
                        RuntimeLogValue.Of("files", (long)files.Count),
                        RuntimeLogValue.Of("plans", (long)kernel.LoadedPlans),
                        RuntimeLogValue.Of("waitedMs", (adopting - waiting) * 1000.0 / Stopwatch.Frequency),
                        RuntimeLogValue.Of("elapsedMs", (finished - adopting) * 1000 / Stopwatch.Frequency)
                    }
                });
            boundary.Complete();
        }
        catch (Exception error)
        {
            Suspend("plan-reload-failed", error.Message);
        }
    }

    /// <summary>The host's own admission of a batch of plan files: the owning Rundown's refusal, then the content
    /// package prerequisites, judged against every plan file the index holds. The custom sounds the admitted plans name
    /// are then found by hash in each plan's own package (I-RESOLVE), before the kernel resolves their references.</summary>
    private static IReadOnlyList<PlanCandidate> Admit(RuntimeKernel kernel, IReadOnlyList<PlanCandidate> files)
    {
        var admitted = (_contentPlans ?? throw new InvalidOperationException("Content startup snapshot is unavailable."))
            .Check(RundownPlanAdmission.Check(kernel, files),
                _planIndex!.Entries.Select(entry => entry.Path).Concat(_planIndex.Refused.Select(file => file.Path)));
        global::ForgeEngine.Presentation.CustomSoundLibrary.UsePlans(Paths.BepInExRootPath,
            admitted.Where(plan => plan.Document is { SyntaxError: null }).Select(plan => (plan.Path, plan.Document!.Syntax)),
            failure => ContentPackStartup.Rejected(kernel, failure.Code, failure.Relative, null, failure.Relative, failure.Reason));
        return admitted;
    }
}

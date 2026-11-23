using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The game-independent half of Map package admission: the Rundown packs, every sidecar read through the
/// bounded reader, the base-version gate, per-document parsing and map-generation capture. It touches no native table:
/// the game (<c>MapPackageAdmission.Capture</c>, fed by ForgeEngine's one discovery) and the offline package-admission
/// runner (fed by a walk of the exported package) execute exactly this code.</summary>
internal sealed record MapPackageIntakeResult(IReadOnlyList<MapPackageStage> Stages, IReadOnlyList<RundownPack> Packs,
    IReadOnlyList<RundownDiagnostic> RundownFailures);

internal static class MapPackageIntake
{
    /// <summary>The offline runner's intake of an exported package: its own walk of <paramref name="root"/>.</summary>
    internal static MapPackageIntakeResult Run(string root)
        => Run(root, (directory, pattern, codes, rejected) => PublishedResources.Discover(root, directory, pattern, codes, rejected),
            report => RundownPacks.Discover(root, report));

    internal static MapPackageIntakeResult Run(string root, MapDocumentReader read,
        Func<Action<RundownDiagnostic>, IReadOnlyList<RundownPack>> rundowns)
    {
        var failures = new List<RundownDiagnostic>();
        var packs = rundowns(failures.Add);
        var stages = MapPackageDocuments.Capture(read).Select(source => MapPackageStage.Parse(source))
            .ToDictionary(stage => stage.Root, StringComparer.Ordinal);
        MapPackageStage Stage(string owner)
        {
            if (!stages.TryGetValue(owner, out var stage))
                stages.Add(owner, stage = MapPackageStage.Parse(MapPackageDocuments.Empty(owner)));
            return stage;
        }
        void Reject(MapPackageStage stage, string path, string code, string reason)
            => stage.Reject(new(Path.GetFileName(stage.Root), Path.GetFileName(path), path, code, reason));
        // An unreadable rundown.forge is the Rundown reader's refusal, not a version question.
        foreach (var rundown in read("forge", "rundown.forge", MapPackageDocuments.Codes, _ => { }))
        {
            if (!PublishedResources.TryRead(rundown, PublishedResources.MaximumFileBytes, MapPackageDocuments.Codes, _ => { }, out var text, out _)) continue;
            var refusal = PackageVersionGate.Check(text!);
            if (refusal is null) continue;
            string owner = PublishedResources.PackageRoot(rundown.Relative);
            Reject(Stage(owner), rundown.Relative, refusal.Value.Code, refusal.Value.Detail);
        }
        foreach (var pack in packs)
        {
            var stage = Stage(PublishedResources.PackageRoot(pack.Path));
            if (stage.IsRejected) continue;
            string generationPath = stage.Root + "/forge/map-generation.forge";
            try
            {
                if (stage.GenerationPlan != null)
                    stage.Generation = Generation.GenerationPackage.Capture(root, stage, pack);
            }
            catch (Generation.GenerationRefusal error) { Reject(stage, generationPath, error.Code, error.Path); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                or ArgumentException or InvalidOperationException)
            { Reject(stage, generationPath, "gen-reference-missing", error.Message); }
        }
        foreach (var failure in failures)
        {
            string owner = PublishedResources.PackageRoot(failure.Entry);
            if (owner != "plugins") { Reject(Stage(owner), failure.Entry, failure.Code, failure.Detail); continue; }
            var roots = failure.Other.Split(',').Select(value => value.Trim())
                .Where(value => value.StartsWith("plugins/", StringComparison.Ordinal))
                .Select(PublishedResources.PackageRoot).Distinct(StringComparer.Ordinal).ToArray();
            if (roots.Length == 0 && failure.Package.Length != 0)
                roots = new[] { failure.Package.StartsWith("plugins/", StringComparison.Ordinal) ? failure.Package : "plugins/" + failure.Package };
            if (roots.Length == 0)
                throw new InvalidDataException("Rundown discovery failed before an owning package could be identified: " + failure.Detail);
            foreach (string rejected in roots) Reject(Stage(rejected), rejected + "/forge/rundown.forge", failure.Code, failure.Detail);
        }
        return new(Array.AsReadOnly(stages.Values.OrderBy(stage => stage.Root, StringComparer.Ordinal).ToArray()), packs, failures);
    }
}

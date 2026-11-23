using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEngine.Framework;

namespace ForgeMap.Generation;

/// <summary>Only the shared bounded reader opens package inputs. The captured bytes are never reopened at generation.</summary>
internal static class GenerationPackage
{
    /// <summary>The release this engine was built into, read once from the embedded release record: its runtime API and the
    /// ForgeEngine version, which installed engines are at or above (the host's release check).</summary>
    private static readonly Lazy<GenerationAdmissionPins> ReleasePins = new(() =>
    {
        using var releaseStream = typeof(GenerationPackage).Assembly.GetManifestResourceStream("ForgeMap.Generation.Release")!;
        using var release = JsonDocument.Parse(releaseStream);
        var engine = release.RootElement.GetProperty("packages").EnumerateArray().Single(row => GenerationProgram.Text(row, "packageName") == "NAinfini-ForgeEngine");
        return new GenerationAdmissionPins(GenerationProgram.Id(release.RootElement.GetProperty("runtimeApi")), GenerationProgram.Text(engine, "version"));
    });

    internal static GenerationProgram Capture(string root, MapPackageStage stage, RundownPack pack)
    {
        var plan = stage.GenerationPlan!;
        if (GenerationProgram.Text(plan.Root.GetProperty("package"), "id") != pack.Package
            || GenerationProgram.Text(plan.Root.GetProperty("package"), "version") != pack.Version)
            throw new GenerationRefusal("gen-layout-owner", stage.Root);
        var pins = ReleasePins.Value;
        var files = new List<GenerationNativeFile>();
        var bytesByPath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long remaining = PublishedResources.MaximumFileBytes;
        foreach (var input in plan.NativeInputs.EnumerateArray())
        {
            string relative = GenerationProgram.Text(input, "path");
            if (GenerationProgram.Text(input, "ownerPackage") != pack.Package)
                throw new GenerationRefusal("gen-layout-owner", relative);
            string path = PublishedResources.ResolveWithinRoot(root, stage.Root + "/" + relative);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > remaining) throw new GenerationRefusal("gen-work-budget", relative);
            var resource = new PublishedResource(stage.Root + "/" + relative, path, info.Length, root);
            string readFailure = "";
            if (!PublishedResources.TryRead(resource, (int)remaining, reason => readFailure = reason, out var text))
                throw new GenerationRefusal("gen-reference-missing", relative, readFailure);
            byte[] bytes = Encoding.UTF8.GetBytes(text!);
            remaining -= bytes.Length;
            files.Add(new(relative, bytes, false, pack.Package));
            bytesByPath.Add(relative, bytes);
        }
        var program = new GenerationProgram(plan, bytesByPath);
        try { MapGenerationPlanAdmission.Admit(plan, new(pins, files, VerifyOwnership(program, pack))); }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
        { throw new GenerationRefusal("gen-layout-owner", stage.Root + "/native-owner"); }
        program.RequireSupportedRequests();
        return program;
    }
    internal static IReadOnlyList<GenerationLayoutOwner> VerifyOwnership(GenerationProgram program, RundownPack pack)
    {
        var plan = program.Plan;
        var owners = new List<GenerationLayoutOwner>();
        var rundown = pack.Blocks.Single(block => block.Type == "Rundown" && block.Id == pack.Rundown).Data;
        uint Resolve(JsonNode? value)
        {
            if (value is JsonValue literal && literal.TryGetValue<uint>(out var number)) return number;
            if (value is JsonObject reference && reference.Count == 1 && reference["$ref"] is JsonValue target)
            {
                string id = target.GetValue<string>();
                var matches = pack.Blocks.Where(block => block.Id == id).ToArray();
                if (matches.Length == 1) return matches[0].PersistentId;
            }
            throw new GenerationRefusal("gen-layout-owner", pack.Package + "/native-reference");
        }
        foreach (var zone in plan.Zones.EnumerateArray())
        {
            var dimension = plan.Dimensions[GenerationProgram.Index(zone, "dimension")];
            var level = plan.Levels[GenerationProgram.Index(dimension, "level")];
            var reference = MapLevelReference.TryParse(GenerationProgram.Text(level, "reference"))
                ?? throw new GenerationRefusal("gen-layout-owner", GenerationProgram.Id(level));
            if (reference.RundownId != pack.RundownId) throw new GenerationRefusal("gen-layout-owner", GenerationProgram.Id(level));
            var layout = zone.GetProperty("layout");
            // Layout membership is checked against the owning native Rundown/Dimension below, not copied from the plan.
            var entry = rundown["Tier" + reference.Tier]![reference.Index]!;
            uint actualLayout;
            if (GenerationProgram.Index(dimension, "nativeAddress") == 0)
            {
                int layer = GenerationProgram.Index(zone, "layer");
                // Vanilla ExpeditionInTierData keeps each layer's layout beside LevelLayoutData; the *LayerData blocks hold no layout.
                actualLayout = Resolve(entry[layer switch { 0 => "LevelLayoutData", 1 => "SecondaryLayout", _ => "ThirdLayout" }]);
                if (Resolve(entry["Expedition"]!["ComplexResourceData"]) != program.BlockId(dimension.GetProperty("resourceSet")))
                    throw new GenerationRefusal("gen-layout-owner", GenerationProgram.Id(dimension) + "/resourceSet");
            }
            else
            {
                var declaration = entry["DimensionDatas"]!.AsArray().Single(row => row!["DimensionIndex"]!.GetValue<int>() == GenerationProgram.Index(dimension, "nativeAddress"))!;
                uint id = Resolve(declaration["DimensionData"]);
                if (id != program.BlockId(dimension.GetProperty("dimensionBlock")))
                    throw new GenerationRefusal("gen-layout-owner", GenerationProgram.Id(dimension));
                var block = pack.Blocks.Single(row => row.Type == "Dimension" && row.PersistentId == id).Data["DimensionData"]!;
                actualLayout = Resolve(block["LevelLayoutData"]);
                if (Resolve(block["DimensionResourceSetID"]) != program.BlockId(dimension.GetProperty("resourceSet")))
                    throw new GenerationRefusal("gen-layout-owner", GenerationProgram.Id(dimension) + "/resourceSet");
            }
            if (actualLayout != program.BlockId(layout)) throw new GenerationRefusal("gen-layout-owner", GenerationProgram.Id(zone));
            var owner = new GenerationLayoutOwner(GenerationProgram.Index(layout, "input"), GenerationProgram.Index(layout, "block"),
                reference.ToString(), GenerationProgram.Index(dimension, "nativeAddress"), pack.Package);
            if (!owners.Contains(owner)) owners.Add(owner);
        }
        return owners;
    }
}

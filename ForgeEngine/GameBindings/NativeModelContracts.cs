using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>The locked identity of one native model root used by an authored assembly.</summary>
internal sealed record NativeModelResourceReference(string Id, string Revision)
{
    public string Key => Id + "\0" + Revision;
}

/// <summary>The exact GTFO loader address. The shard is deliberately absent: it is probe evidence, not identity.</summary>
internal sealed record NativeModelLocator(string Namespace, string Loader, string? Bundle, string ResourceName);

/// <summary>An exact child below one native root. Indices disambiguate equal sibling names.</summary>
internal sealed record NativeModelSourceObject(string File, string PathId);

/// <summary>The exact offline Unity object identity retained by a probed child selection.</summary>
internal sealed record NativeModelSelection(string SourceRevision, IReadOnlyList<string> ChildPath,
    IReadOnlyList<int>? ChildIndices, NativeModelSourceObject? Object,
    IReadOnlyList<NativeModelSourceObject> MaterialSlots);

/// <summary>One authored TRS in a flattened parent-to-leaf chain.</summary>
internal sealed record NativeModelTransform(float[] Position, float[] Rotation, float[] Scale);

/// <summary>
/// The enemy-only hit role of one placed assembly: the whole placement is one native damage limb. Health null keeps
/// the enemy's vanilla limb health. Multiplier null keeps the vanilla weakspot / armor multiplier and is always null
/// for a normal part. CrackAt holds the remaining-health percentages (strictly decreasing, inside 0-100) at which
/// the part visibly cracks.
/// </summary>
internal sealed record NativeModelHit(string Type, float? Health, bool Breakable, float? Multiplier,
    IReadOnlyList<float> CrackAt);

/// <summary>
/// The surface an enemy hit part is hit on: the model's own mesh, simplified by the website to at most
/// <see cref="NativeModelContent.MaximumHitTriangles"/> triangles, in the placement root's own frame (metres, game axes,
/// faces wound outward). The game builds one non-convex MeshCollider from it as given and simplifies nothing.
/// </summary>
internal sealed record NativeModelHitMesh(float[] Positions, int[] Indices);

/// <summary>The load route of a native model that has a spawn entry: its shard and the game build the route was read from.</summary>
internal sealed record NativeModelBinding(NativeModelResourceReference Resource, string Shard, string GameBuild);

/// <summary>One native leaf in a composed model. Matrix is a checked compiler readback; Transforms is what Unity applies.</summary>
internal sealed record NativeModelPart(IReadOnlyList<string> AuthorPath, NativeModelResourceReference Resource,
    NativeModelLocator Locator, NativeModelSelection? Selection, float[] Matrix,
    IReadOnlyList<NativeModelTransform> Transforms, ForgeVisual.SurfaceAppearance? Appearance = null);

/// <summary>One leaf that is a player-imported model: its identity only (the descriptor and the mesh and PNG files travel as
/// sidecars of the package) and the same pose and look fields as a native leaf.</summary>
internal sealed record NativeModelImportedPart(IReadOnlyList<string> AuthorPath, NativeModelResourceReference Resource,
    float[] Matrix, IReadOnlyList<NativeModelTransform> Transforms, ForgeVisual.SurfaceAppearance? Appearance = null);

/// <summary>A rigid attachment or placement host. Only a weapon host names a WeaponPart.</summary>
internal sealed record NativeModelHost(string Kind, string? WeaponPart, IReadOnlyList<string> ChildPath,
    IReadOnlyList<int>? ChildIndices);

/// <summary>One verified bone pair of a deforming enemy placement: a bone of the placed model's own skeleton (below its
/// prefab root) and the host enemy's bone it follows (below <c>EnemyAgent.MainModelTransform</c>), each as names and
/// sibling indices the way <see cref="NativeModelRuntime.ResolveChild"/> resolves them.</summary>
internal sealed record NativeModelDeformBone(IReadOnlyList<string> SourcePath, IReadOnlyList<int> SourceIndices,
    IReadOnlyList<string> TargetPath, IReadOnlyList<int> TargetIndices);

/// <summary>An enemy placement whose skinned meshes follow the host enemy's bones. The pairs are the website's one
/// verified mapping between the two vanilla skeletons (Web/src/forge/enemy/enemy-skin-families.json); both machines read
/// them from the same package.</summary>
internal sealed record NativeModelDeform(IReadOnlyList<NativeModelDeformBone> Bones);

/// <summary>One bone link of a skinned imported model on an enemy: the model bone named <c>Model</c> copies the turn of the host bone at
/// <c>HostPath</c>/<c>HostIndices</c> (below <c>EnemyAgent.MainModelTransform</c>); <c>Body</c> is the humanoid bone (Unity
/// <c>HumanBodyBones</c> name) it stands for, <c>Toward</c>/<c>From</c> the link that gives it a direction (-1 none), <c>Offset</c> the author's
/// position offset of the model bone (metres, model axes), <c>HitMesh</c> its hit piece in that bone's own rest frame.</summary>
internal sealed record NativeModelSkinLink(string Model, string Body, IReadOnlyList<string> HostPath, IReadOnlyList<int> HostIndices,
    int Toward, int From, float[] Offset, NativeModelHitMesh? HitMesh);

/// <summary>A skinned imported model riding the whole enemy (Web/src/forge/enemy/enemy-skin-binding.ts): the first link is the hips.</summary>
internal sealed record NativeModelSkin(IReadOnlyList<NativeModelSkinLink> Links);

/// <summary>One exported composed-model use, already flattened to leaves: native ones and imported ones. Only enemy assemblies carry a hit,
/// its mesh (present exactly when the hit is) and a deform. An enemy or weapon assembly may name the vanilla part it replaces (an enemy
/// part, or for a weapon a `GearPartSlot` name, or `BaseItem` for the whole gun).</summary>
internal sealed record NativeModelAssembly(string Id, NativeModelHost Host, IReadOnlyList<NativeModelPart> Parts,
    NativeModelHit? Hit, IReadOnlyList<NativeModelImportedPart> ImportedParts, NativeModelDeform? Deform = null,
    NativeModelHitMesh? HitMesh = null, string? Replaces = null, NativeModelSkin? Skin = null);

/// <summary>The shared model portion of a weapon, enemy, room or map content entry.</summary>
internal sealed class NativeModelContent
{
    private const int MaximumAssemblies = 256, MaximumParts = 4096, MaximumPath = 64, MaximumText = 2048;
    private const int MaximumCrackStages = 4;
    /// <summary>The id prefix of a player-imported model (`imported:&lt;uuid&gt;`); a native resource id never starts with it.</summary>
    public const string ImportedIdPrefix = "imported:";
    private const string NativeNamespace = "gtfo";
    private const string NativeLoader = "AssetShards.AssetShardManager.GetLoadedAsset";
    private readonly IReadOnlyDictionary<string, NativeModelBinding> _bindings;
    private sealed record ResourceCompilation(JsonElement Assembly, int RootCount);

    private NativeModelContent(IReadOnlyList<NativeModelAssembly> assemblies,
        IReadOnlyDictionary<string, NativeModelBinding> bindings)
    { Assemblies = assemblies; _bindings = bindings; }

    public IReadOnlyList<NativeModelAssembly> Assemblies { get; }
    public IReadOnlyList<NativeModelBinding> Bindings => _bindings.Values.OrderBy(row => row.Resource.Id, StringComparer.Ordinal)
        .ThenBy(row => row.Resource.Revision, StringComparer.Ordinal).ToArray();
    public static NativeModelContent Empty { get; } = new(Array.Empty<NativeModelAssembly>(),
        new Dictionary<string, NativeModelBinding>(StringComparer.Ordinal));
    public NativeModelBinding Binding(NativeModelPart part) => _bindings[part.Resource.Key];

    /// <summary>Every imported model the assemblies use, once each: the packages a registration must be given.</summary>
    public IReadOnlyList<NativeModelResourceReference> ImportedResources => Assemblies.SelectMany(row => row.ImportedParts)
        .Select(row => row.Resource).DistinctBy(row => row.Key, StringComparer.Ordinal).ToArray();

    /// <summary>Reads only the shared root fields. The owning domain reader remains responsible for its other fields.</summary>
    public static NativeModelContent Read(JsonElement root, string domain)
    {
        try { return ReadCore(root, domain); }
        catch (ResourceDefinitionException error) { throw new RuntimeContractException(error.Code, error.Message); }
    }

    private static NativeModelContent ReadCore(JsonElement root, string domain)
    {
        if (domain is not ("weapon" or "enemy" or "room" or "map")) throw Reject("model-domain", domain);
        bool hasAssemblies = root.TryGetProperty("modelAssemblies", out var assemblyValue);
        bool hasBindings = root.TryGetProperty("nativeBindings", out var bindingValue);
        bool hasDefinitions = root.TryGetProperty("resourceDefinitions", out var definitionValue);
        bool hasScope = root.TryGetProperty("resourceScope", out var scopeValue);
        bool hasPackages = root.TryGetProperty("resourcePackages", out var packageValue);
        bool hasLegacyDependencies = root.TryGetProperty("modelDependencies", out _);
        if (!hasAssemblies && !hasBindings && !hasDefinitions && !hasScope && !hasPackages && !hasLegacyDependencies)
            return Empty;
        if (hasLegacyDependencies || !hasAssemblies || !hasBindings || !hasDefinitions || !hasScope || !hasPackages
            || assemblyValue.ValueKind != JsonValueKind.Array || bindingValue.ValueKind != JsonValueKind.Array
            || definitionValue.ValueKind != JsonValueKind.Array || scopeValue.ValueKind != JsonValueKind.Object
            || packageValue.ValueKind != JsonValueKind.Array)
            throw Reject("model-shape", "models require modelAssemblies, nativeBindings, resourceDefinitions, resourceScope and resourcePackages");
        if (assemblyValue.GetArrayLength() > MaximumAssemblies || bindingValue.GetArrayLength() > MaximumParts)
            throw Reject("model-budget", "model assembly or binding budget exceeded");

        var resources = CompileResources(definitionValue, scopeValue, packageValue, domain);

        var bindings = new Dictionary<string, NativeModelBinding>(StringComparer.Ordinal);
        foreach (var value in bindingValue.EnumerateArray())
        {
            var binding = ReadBinding(value);
            if (!bindings.TryAdd(binding.Resource.Key, binding))
                throw Reject("model-binding-duplicate", binding.Resource.Id);
        }

        int parts = 0;
        var assemblies = new List<NativeModelAssembly>(assemblyValue.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var usedBindings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in assemblyValue.EnumerateArray())
        {
            if (domain == "enemy") KeysOptional(value, new[] { "id", "host", "parts", "hit" }, "importedParts", "deform", "hitMesh", "replaces", "skin");
            else if (domain == "weapon") KeysOptional(value, new[] { "id", "host", "parts" }, "importedParts", "replaces");
            else KeysOptional(value, new[] { "id", "host", "parts" }, "importedParts");
            string id = Text(value, "id");
            if (!ids.Add(id)) throw Reject("model-assembly-duplicate", id);
            var host = Host(value.GetProperty("host"), domain);
            var hit = domain == "enemy" ? Hit(value.GetProperty("hit")) : null;
            bool hasMesh = value.TryGetProperty("hitMesh", out var meshValue);
            var skin = value.TryGetProperty("skin", out var skinValue) ? Skin(skinValue, host) : null;
            // A skinned model's hit pieces ride its bone links, so it carries a hit role and no mesh of its own.
            // Its pieces neither break nor crack: they are regions of one drawn model, never taken out of it.
            if (skin != null && hit is { } skinHit && (skinHit.Breakable || skinHit.CrackAt.Count != 0)) throw Reject("model-skin-hit", id);
            if (skin != null ? hit == null || hasMesh : hasMesh != (hit != null)) throw Reject("model-hit-mesh", id + " needs a hit mesh exactly when it has a hit");
            var mesh = hasMesh ? HitMesh(meshValue) : null;
            string? replaces = value.TryGetProperty("replaces", out _) ? Text(value, "replaces") : null;
            var deform = value.TryGetProperty("deform", out var deformValue) ? Deform(deformValue, host) : null;
            var partValue = value.GetProperty("parts");
            if (partValue.ValueKind != JsonValueKind.Array) throw Reject("model-parts", id + " has no parts");
            // An assembly made only of imported models has no native leaf; a present importedParts array is never empty.
            bool hasImported = value.TryGetProperty("importedParts", out var importedValue);
            if (hasImported && (importedValue.ValueKind != JsonValueKind.Array || importedValue.GetArrayLength() == 0))
                throw Reject("model-parts", id + " has an empty importedParts");
            int importedCount = hasImported ? importedValue.GetArrayLength() : 0;
            if (partValue.GetArrayLength() + importedCount == 0) throw Reject("model-parts", id + " has no parts");
            if (parts + partValue.GetArrayLength() + importedCount > MaximumParts) throw Reject("model-budget", "part budget exceeded");
            var rows = new List<NativeModelPart>(partValue.GetArrayLength());
            var imported = new List<NativeModelImportedPart>(importedCount);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in partValue.EnumerateArray())
            {
                rows.Add(Part(part, bindings, usedBindings));
                string path = string.Join("\0", rows[^1].AuthorPath);
                if (!paths.Add(path)) throw Reject("model-author-path-duplicate", path.Replace('\0', '/'));
            }
            if (hasImported)
                foreach (var part in importedValue.EnumerateArray())
                {
                    imported.Add(ImportedPart(part));
                    string path = string.Join("\0", imported[^1].AuthorPath);
                    if (!paths.Add(path)) throw Reject("model-author-path-duplicate", path.Replace('\0', '/'));
                }
            parts += rows.Count + imported.Count;
            if (deform != null && (rows.Count != 1 || imported.Count != 0 || rows[0].Selection != null))
                throw Reject("model-deform-shape", id + " deforms only one whole native model");
            if (skin != null && (rows.Count != 0 || imported.Count != 1 || deform != null))
                throw Reject("model-skin-shape", id + " skins exactly one imported model");
            assemblies.Add(new NativeModelAssembly(id, host, rows, hit, imported, deform, mesh, replaces, skin));
        }
        if (bindings.Keys.Any(key => !usedBindings.Contains(key)))
            throw Reject("model-binding-unused", bindings.Keys.First(key => !usedBindings.Contains(key)));
        VerifyResourceAssembly(resources, assemblies);
        AssertRuntimeResourcesReady(resources, bindings);
        return new NativeModelContent(assemblies, bindings);
    }

    private static ResourceCompilation CompileResources(JsonElement definitionsValue, JsonElement scope,
        JsonElement packagesValue, string domain)
    {
        if (definitionsValue.GetArrayLength() > 16384 || packagesValue.GetArrayLength() > 16384)
            throw new ResourceDefinitionException("resource-budget-exceeded", "Production resource catalog budget exceeded");
        var definitions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var definition in definitionsValue.EnumerateArray())
        {
            if (definition.ValueKind != JsonValueKind.Object || !definition.TryGetProperty("reference", out var reference))
                throw new ResourceDefinitionException("resource-invalid", "Invalid production resource definition");
            string key = ReferenceKey(reference);
            if (!definitions.TryAdd(key, definition.Clone()))
                throw new ResourceDefinitionException("resource-definition-conflict", "Duplicate locked resource definition");
        }
        var packages = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var package in packagesValue.EnumerateArray())
        {
            if (package.ValueKind != JsonValueKind.Object || package.EnumerateObject().Count() != 2
                || !package.TryGetProperty("pin", out var pinValue) || pinValue.ValueKind != JsonValueKind.String
                || !package.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array)
                throw new ResourceDefinitionException("resource-invalid", "Invalid production resource package");
            string pin = pinValue.GetString()!;
            if (!packages.TryAdd(pin, package.Clone()))
                throw new ResourceDefinitionException("resource-definition-conflict", "Duplicate pinned resource package");
        }
        string expectedKind = domain == "weapon" ? "weapon" : "level";
        if (!scope.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || kind.GetString() != expectedKind || !scope.TryGetProperty("roots", out var roots)
            || roots.ValueKind != JsonValueKind.Array || roots.GetArrayLength() > MaximumAssemblies)
            throw new ResourceDefinitionException("resource-invalid", "Resource scope does not match its model host");
        foreach (var root in roots.EnumerateArray())
            if (!root.TryGetProperty("tier", out var tier) || tier.ValueKind != JsonValueKind.String
                || tier.GetString() != "original")
                throw new ResourceDefinitionException("resource-invalid", "Native model roots require the original tier");

        JsonElement? Read(JsonElement reference) => definitions.TryGetValue(ReferenceKey(reference), out var value) ? value : null;
        JsonElement? ReadPackage(string pin) => packages.TryGetValue(pin, out var value) ? value : null;
        var assembly = ResourceDefinitions.CompileAssembly(scope, Read, ReadPackage);
        var closure = assembly.GetProperty("closure");
        var usedDefinitions = closure.GetProperty("definitions").EnumerateArray()
            .Select(value => ReferenceKey(value.GetProperty("reference"))).ToHashSet(StringComparer.Ordinal);
        var usedPackages = closure.GetProperty("packagePins").EnumerateArray()
            .Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        if (!usedDefinitions.SetEquals(definitions.Keys) || !usedPackages.SetEquals(packages.Keys))
            throw new ResourceDefinitionException("resource-invalid", "Production resources must exactly match the resolved closure");
        return new ResourceCompilation(assembly, roots.GetArrayLength());
    }

    private static void AssertRuntimeResourcesReady(ResourceCompilation resources,
        IReadOnlyDictionary<string, NativeModelBinding> bindings)
    {
        const string BindingUnverified = "native-resource-binding-unverified";
        var closure = resources.Assembly.GetProperty("closure");
        var definitions = closure.GetProperty("definitions").EnumerateArray()
            .ToDictionary(value => ReferenceKey(value.GetProperty("reference")), value => value, StringComparer.Ordinal);
        var blockers = new List<object>();
        foreach (var blocker in closure.GetProperty("blockers").EnumerateArray())
        {
            var reference = blocker.GetProperty("reference");
            var definition = definitions[ReferenceKey(reference)];
            string kind = definition.GetProperty("description").GetProperty("kind").GetString()!;
            bool nativeModel = kind == "model" && definition.GetProperty("source").GetProperty("kind").GetString() == "native";
            bool admitted = kind == "composition" || nativeModel && bindings.ContainsKey(ReferenceKey(reference));
            var reasons = blocker.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()!)
                .Where(reason => reason != BindingUnverified || !admitted).ToArray();
            if (reasons.Length != 0) blockers.Add(new { reference, reasons });
        }
        ResourceDefinitions.AssertRuntimeBlockersCleared(JsonSerializer.SerializeToElement(new { blockers }));
    }

    private static void VerifyResourceAssembly(ResourceCompilation resources,
        IReadOnlyList<NativeModelAssembly> assemblies)
    {
        if (assemblies.Count != resources.RootCount)
            throw Reject("model-resource-mismatch", "resource roots and model assemblies differ");
        var compiled = resources.Assembly;
        var references = compiled.GetProperty("resources").EnumerateArray().ToArray();
        var parts = compiled.GetProperty("parts").EnumerateArray().ToArray();
        var traces = compiled.GetProperty("traces").EnumerateArray().ToArray();
        var definitions = compiled.GetProperty("closure").GetProperty("definitions").EnumerateArray()
            .ToDictionary(value => ReferenceKey(value.GetProperty("reference")), value => value, StringComparer.Ordinal);
        for (int assemblyIndex = 0; assemblyIndex < assemblies.Count; assemblyIndex++)
        {
            var candidates = parts.Select((value, index) => (Value: value, Index: index))
                .Where(row => traces[row.Value.GetProperty("traceIndex").GetInt32()].GetProperty("rootIndex").GetInt32() == assemblyIndex)
                .ToArray();
            var assembly = assemblies[assemblyIndex];
            if (candidates.Length != assembly.Parts.Count)
                throw Reject("model-resource-mismatch", assembly.Id + " part count differs from its resource assembly");
            for (int partIndex = 0; partIndex < assembly.Parts.Count; partIndex++)
            {
                var part = assembly.Parts[partIndex];
                var candidate = candidates[partIndex].Value;
                int traceIndex = candidate.GetProperty("traceIndex").GetInt32();
                int resourceIndex = candidate.GetProperty("resourceIndex").GetInt32();
                if (candidate.GetProperty("tier").GetString() != "original" || resourceIndex < 0
                    || resourceIndex >= references.Length || ReferenceKey(references[resourceIndex]) != part.Resource.Key
                    || !MatrixEquals(part.Matrix, candidate.GetProperty("matrix"))
                    || !TraceMatches(part.AuthorPath, traces[traceIndex]))
                    throw Reject("model-resource-mismatch", assembly.Id + " part " + partIndex + " differs from its compiled resource part");
                var definition = definitions[part.Resource.Key];
                var original = definition.GetProperty("description").GetProperty("variants").GetProperty("original");
                if (original.GetProperty("payload").GetProperty("kind").GetString() != "native")
                    throw new ResourceDefinitionException("resource-type-mismatch", "Native model readback requires a native original payload");
                var definedSelection = original.GetProperty("selection");
                if (definedSelection.ValueKind != JsonValueKind.Null && !SelectionMatches(part.Selection, definedSelection))
                    throw new ResourceDefinitionException("resource-definition-conflict", "Model readback selection differs from its resource definition");
                var locatorValue = definition.GetProperty("locator");
                if (locatorValue.ValueKind == JsonValueKind.Null)
                    throw new ResourceDefinitionException("resource-locator-missing", "Compiled native model has no locator");
                var locator = ResourceDefinitions.ReadLocator(locatorValue);
                if (locator.Namespace != part.Locator.Namespace || locator.Loader != part.Locator.Loader
                    || locator.Bundle != part.Locator.Bundle || locator.ResourceName != part.Locator.ResourceName)
                    throw new ResourceDefinitionException("resource-locator-conflict", "Model readback locator differs from its resource definition");
            }
        }
    }

    private static string ReferenceKey(JsonElement reference)
    {
        var value = ResourceDefinitions.ReadReference(reference);
        return value.Id + "\0" + value.Revision;
    }

    private static bool MatrixEquals(IReadOnlyList<float> expected, JsonElement actual)
    {
        if (actual.ValueKind != JsonValueKind.Array || actual.GetArrayLength() != expected.Count) return false;
        for (int index = 0; index < expected.Count; index++)
        {
            double value = actual[index].GetDouble();
            double tolerance = 0.00001d * Math.Max(1d, Math.Max(Math.Abs(value), Math.Abs(expected[index])));
            if (!double.IsFinite(value) || Math.Abs(value - expected[index]) > tolerance) return false;
        }
        return true;
    }

    private static bool SelectionMatches(NativeModelSelection? actual, JsonElement expected)
    {
        if (actual == null) return false;
        var value = ReadSelection(expected, actual.SourceRevision);
        return value != null && value.SourceRevision == actual.SourceRevision
            && value.ChildPath.SequenceEqual(actual.ChildPath, StringComparer.Ordinal)
            && (value.ChildIndices == null && actual.ChildIndices == null || value.ChildIndices != null
                && actual.ChildIndices != null && value.ChildIndices.SequenceEqual(actual.ChildIndices))
            && value.Object == actual.Object && value.MaterialSlots.SequenceEqual(actual.MaterialSlots);
    }

    private static bool TraceMatches(IReadOnlyList<string> authorPath, JsonElement trace)
    {
        var chain = trace.GetProperty("chain").EnumerateArray().ToArray();
        if (authorPath.Count != chain.Length * 2 - 1) return false;
        for (int index = 0; index < chain.Length; index++)
        {
            if (chain[index].GetProperty("instanceId").GetString() != authorPath[index * 2]) return false;
            if (index != 0 && chain[index].GetProperty("definition").GetProperty("id").GetString()
                != "forge.model:" + authorPath[index * 2 - 1]) return false;
        }
        return true;
    }

    private static NativeModelHost Host(JsonElement value, string domain)
    {
        if (domain is "room" or "map")
        {
            Keys(value, "kind");
            string hostKind = Text(value, "kind");
            if (hostKind != domain) throw Reject("model-host-kind", hostKind);
            return new NativeModelHost(hostKind, null, Array.Empty<string>(), null);
        }
        if (domain == "weapon") KeysOptional(value, new[] { "kind", "part", "childPath" }, "childIndices");
        else KeysOptional(value, new[] { "kind", "childPath" }, "childIndices");
        string kind = Text(value, "kind");
        if (kind != domain) throw Reject("model-host-kind", kind);
        string? part = domain == "weapon" ? Text(value, "part") : null;
        var path = Path(value.GetProperty("childPath"), allowEmpty: true);
        var indices = Indices(value, path.Count);
        return new NativeModelHost(kind, part, path, indices);
    }

    /// <summary>
    /// The enemy assembly hit role. Health must be positive because crack stages divide by it; a normal part has no
    /// weakspot or armor multiplier to override.
    /// </summary>
    private static NativeModelHit? Hit(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        Keys(value, "type", "health", "breakable", "multiplier", "crackAt");
        string type = Text(value, "type");
        if (type is not ("armor" or "weakpoint" or "normal")) throw Reject("model-hit-type", type);
        float? health = null;
        var healthValue = value.GetProperty("health");
        if (healthValue.ValueKind != JsonValueKind.Null)
        {
            health = Number(healthValue, "model-hit-health");
            if (health <= 0f) throw Reject("model-hit-health", healthValue.GetRawText());
        }
        var breakable = value.GetProperty("breakable");
        if (breakable.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Reject("model-hit-breakable", breakable.GetRawText());
        float? multiplier = null;
        var multiplierValue = value.GetProperty("multiplier");
        if (multiplierValue.ValueKind != JsonValueKind.Null)
        {
            if (type == "normal") throw Reject("model-hit-multiplier", "a normal part has no multiplier");
            multiplier = Number(multiplierValue, "model-hit-multiplier");
            if (multiplier < 0f) throw Reject("model-hit-multiplier", multiplierValue.GetRawText());
        }
        var crackValue = value.GetProperty("crackAt");
        if (crackValue.ValueKind != JsonValueKind.Array || crackValue.GetArrayLength() > MaximumCrackStages)
            throw Reject("model-hit-crack", crackValue.GetRawText());
        var crackAt = new List<float>(crackValue.GetArrayLength());
        foreach (var stage in crackValue.EnumerateArray())
        {
            float percent = Number(stage, "model-hit-crack");
            if (percent <= 0f || percent >= 100f || crackAt.Count != 0 && percent >= crackAt[^1])
                throw Reject("model-hit-crack", crackValue.GetRawText());
            crackAt.Add(percent);
        }
        return new NativeModelHit(type, health, breakable.GetBoolean(), multiplier, crackAt);
    }

    /// <summary>The most triangles one hit mesh may carry (the website's HIT_MESH_TRIANGLES).</summary>
    public const int MaximumHitTriangles = 1024;
    private const int MaximumHitMillimetres = 1_000_000;

    /// <summary>A hit mesh: whole millimetres within 1 km, whole triangles, every index in range, at most
    /// <see cref="MaximumHitTriangles"/> triangles. Positions come back in metres.</summary>
    private static NativeModelHitMesh HitMesh(JsonElement value)
    {
        Keys(value, "positions", "indices");
        var positions = value.GetProperty("positions");
        var indices = value.GetProperty("indices");
        if (positions.ValueKind != JsonValueKind.Array || indices.ValueKind != JsonValueKind.Array) throw Reject("model-hit-mesh", "arrays");
        int coordinates = positions.GetArrayLength(), corners = indices.GetArrayLength();
        if (coordinates < 9 || coordinates % 3 != 0 || corners < 3 || corners % 3 != 0 || corners > 3 * MaximumHitTriangles)
            throw Reject("model-hit-mesh", "size");
        var metres = new float[coordinates];
        int at = 0;
        foreach (var item in positions.EnumerateArray())
        {
            if (!item.TryGetInt32(out int millimetres) || Math.Abs(millimetres) > MaximumHitMillimetres) throw Reject("model-hit-mesh", "position");
            metres[at++] = millimetres / 1000f;
        }
        var triangles = new int[corners];
        at = 0;
        foreach (var item in indices.EnumerateArray())
        {
            if (!item.TryGetInt32(out int index) || index < 0 || index >= coordinates / 3) throw Reject("model-hit-mesh", "index");
            triangles[at++] = index;
        }
        return new NativeModelHitMesh(metres, triangles);
    }

    /// <summary>A deforming placement sits on the whole enemy and names at least one bone pair.</summary>
    private static NativeModelDeform Deform(JsonElement value, NativeModelHost host)
    {
        Keys(value, "bones");
        if (host.ChildPath.Count != 0) throw Reject("model-deform-shape", "a deforming placement is hosted on the whole enemy");
        var bones = value.GetProperty("bones");
        if (bones.ValueKind != JsonValueKind.Array || bones.GetArrayLength() is 0 or > 256) throw Reject("model-deform-bones", bones.GetRawText());
        var rows = new List<NativeModelDeformBone>(bones.GetArrayLength());
        foreach (var pair in bones.EnumerateArray())
        {
            Keys(pair, "source", "target");
            var (sourcePath, sourceIndices) = DeformBone(pair.GetProperty("source"));
            var (targetPath, targetIndices) = DeformBone(pair.GetProperty("target"));
            rows.Add(new NativeModelDeformBone(sourcePath, sourceIndices, targetPath, targetIndices));
        }
        return new NativeModelDeform(rows);
    }

    /// <summary>A skinned placement sits on the whole enemy: 1 to 64 links, the hips first with no offset, unique model bones, offsets within a metre.</summary>
    private static NativeModelSkin Skin(JsonElement value, NativeModelHost host)
    {
        Keys(value, "links");
        if (host.ChildPath.Count != 0) throw Reject("model-skin-shape", "a skinned placement is hosted on the whole enemy");
        var links = value.GetProperty("links");
        if (links.ValueKind != JsonValueKind.Array || links.GetArrayLength() is 0 or > 64) throw Reject("model-skin-links", links.GetRawText());
        int count = links.GetArrayLength();
        var rows = new List<NativeModelSkinLink>(count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links.EnumerateArray())
        {
            KeysOptional(link, new[] { "model", "body", "host", "toward", "from", "offset" }, "hitMesh");
            string model = Text(link, "model"), body = Text(link, "body");
            if (!names.Add(model)) throw Reject("model-skin-links", model);
            var hostValue = link.GetProperty("host");
            Keys(hostValue, "childPath", "childIndices");
            var (path, indices) = DeformBone(hostValue, "childPath", "childIndices");
            if (!link.GetProperty("toward").TryGetInt32(out int toward) || !link.GetProperty("from").TryGetInt32(out int from)
                || toward < -1 || from < -1 || toward >= count || from >= count || toward == rows.Count || from == rows.Count || toward >= 0 && from >= 0)
                throw Reject("model-skin-links", model);
            var offsetValue = link.GetProperty("offset");
            if (offsetValue.ValueKind != JsonValueKind.Array || offsetValue.GetArrayLength() != 3) throw Reject("model-skin-links", model);
            var offset = offsetValue.EnumerateArray().Select(item => item.TryGetSingle(out float number) && float.IsFinite(number) && Math.Abs(number) <= 1f
                ? number : throw Reject("model-skin-links", model)).ToArray();
            if (rows.Count == 0 && (body != "Hips" || offset.Any(number => number != 0f))) throw Reject("model-skin-links", model);
            rows.Add(new NativeModelSkinLink(model, body, path, indices, toward, from, offset,
                link.TryGetProperty("hitMesh", out var mesh) ? HitMesh(mesh) : null));
        }
        if (rows.All(row => row.HitMesh == null)) throw Reject("model-hit-mesh", "a skinned placement needs a hit piece");
        return new NativeModelSkin(rows);
    }

    private static (IReadOnlyList<string> Path, IReadOnlyList<int> Indices) DeformBone(JsonElement value) => DeformBone(value, "path", "indices");
    private static (IReadOnlyList<string> Path, IReadOnlyList<int> Indices) DeformBone(JsonElement value, string pathKey, string indicesKey)
    {
        if (pathKey == "path") Keys(value, "path", "indices");
        var path = Path(value.GetProperty(pathKey), allowEmpty: false);
        var indicesValue = value.GetProperty(indicesKey);
        if (indicesValue.ValueKind != JsonValueKind.Array || indicesValue.GetArrayLength() != path.Count)
            throw Reject("model-deform-bone", indicesValue.GetRawText());
        var indices = new List<int>(path.Count);
        foreach (var index in indicesValue.EnumerateArray())
        {
            if (!index.TryGetInt32(out int number) || number < 0) throw Reject("model-deform-bone", index.GetRawText());
            indices.Add(number);
        }
        return (path, indices);
    }

    private static NativeModelPart Part(JsonElement value,
        IReadOnlyDictionary<string, NativeModelBinding> bindings, ISet<string> usedBindings)
    {
        KeysOptional(value, PartKeys, "appearance");
        var authorPath = Path(value.GetProperty("authorPath"), allowEmpty: false);
        var referenceValue = value.GetProperty("resource");
        var locatorValue = value.GetProperty("locator");
        ResourceDefinitions.VerifyNativeReference(referenceValue, locatorValue);
        var reference = ReadReference(referenceValue);
        var locator = ReadLocator(locatorValue);
        var selection = ReadSelection(value.GetProperty("selection"), reference.Revision);
        var (matrix, transforms) = Pose(value);
        if (!bindings.ContainsKey(reference.Key)) throw Reject("model-binding-missing", reference.Id);
        usedBindings.Add(reference.Key);
        var appearance = value.TryGetProperty("appearance", out var appearanceValue) ? ReadAppearance(appearanceValue) : null;
        return new NativeModelPart(authorPath, reference, locator, selection, matrix, transforms, appearance);
    }

    private static readonly string[] PartKeys = { "authorPath", "resource", "locator", "selection", "matrix", "transforms" };
    private static readonly string[] ImportedPartKeys = { "authorPath", "resource", "matrix", "transforms" };

    /// <summary>An imported model leaf: the resource is an `imported:` id locked at a revision, with no native locator, selection or binding.</summary>
    private static NativeModelImportedPart ImportedPart(JsonElement value)
    {
        KeysOptional(value, ImportedPartKeys, "appearance");
        var authorPath = Path(value.GetProperty("authorPath"), allowEmpty: false);
        var reference = ReadReference(value.GetProperty("resource"));
        if (!reference.Id.StartsWith(ImportedIdPrefix, StringComparison.Ordinal)) throw Reject("model-imported-identity", reference.Id);
        var (matrix, transforms) = Pose(value);
        var appearance = value.TryGetProperty("appearance", out var appearanceValue) ? ReadAppearance(appearanceValue) : null;
        return new NativeModelImportedPart(authorPath, reference, matrix, transforms, appearance);
    }

    /// <summary>The checked pose every leaf carries: the exact parent-to-leaf chain Unity applies and the matrix it must produce.</summary>
    private static (float[] Matrix, List<NativeModelTransform> Transforms) Pose(JsonElement value)
    {
        var matrix = Floats(value.GetProperty("matrix"), 16, "model-matrix");
        var transformValue = value.GetProperty("transforms");
        if (transformValue.ValueKind != JsonValueKind.Array || transformValue.GetArrayLength() is 0 or > MaximumPath)
            throw Reject("model-transforms", "a part needs 1-" + MaximumPath + " transforms");
        var transforms = new List<NativeModelTransform>(transformValue.GetArrayLength());
        foreach (var transform in transformValue.EnumerateArray()) transforms.Add(Transform(transform));
        MatrixMatches(transforms, matrix);
        return (matrix, transforms);
    }

    /// <summary>The authored look of one leaf (ForgeVisual's <see cref="ForgeVisual.SurfaceAppearance"/>, sRGB), same rules as the website's
    /// ModelPartAppearance: at least one item; no unknown or duplicate fields; `slots` lists distinct ascending slot indices, each with at
    /// least one item of its own.</summary>
    public static ForgeVisual.SurfaceAppearance ReadAppearance(JsonElement value) => ReadLook(value, true);

    private static ForgeVisual.SurfaceAppearance ReadLook(JsonElement value, bool leaf)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Any())
            throw Reject("model-appearance", value.GetRawText());
        float[]? tint = null, glow = null;
        float brightness = 0f;
        bool? invisible = null;
        int? index = null;
        List<ForgeVisual.SurfaceSlot>? slots = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Reject("model-appearance", property.Name);
            if (property.Name == "tint") tint = Colour(property.Value);
            else if (property.Name == "glow")
            {
                Keys(property.Value, "color", "brightness");
                glow = Colour(property.Value.GetProperty("color"));
                if (glow.Any(channel => channel > 1f)) throw Reject("model-appearance", "glow color");
                brightness = Number(property.Value.GetProperty("brightness"), "model-appearance");
                if (brightness < 0f) throw Reject("model-appearance", "brightness");
            }
            else if (property.Name == "invisible")
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Reject("model-appearance", "invisible");
                invisible = property.Value.GetBoolean();
            }
            else if (!leaf && property.Name == "index")
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int slot)
                    || slot is < 0 or >= ForgeVisual.SurfaceAppearance.MaximumSlots) throw Reject("model-appearance-slot", property.Value.GetRawText());
                index = slot;
            }
            else if (leaf && property.Name == "slots")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() is 0 or > ForgeVisual.SurfaceAppearance.MaximumSlots)
                    throw Reject("model-appearance-slot", "slots");
                slots = new List<ForgeVisual.SurfaceSlot>(property.Value.GetArrayLength());
                foreach (var row in property.Value.EnumerateArray())
                {
                    var look = ReadLook(row, false);
                    var slot = look.Slots![0];
                    if (slots.Count != 0 && slot.Index <= slots[^1].Index) throw Reject("model-appearance-slot", "order");
                    slots.Add(slot);
                }
            }
            else throw Reject("model-appearance", property.Name);
        }
        if (!leaf)
        {
            if (index == null || tint == null && glow == null && invisible == null) throw Reject("model-appearance-slot", value.GetRawText());
            return new(null, null, 0f, null, new[] { new ForgeVisual.SurfaceSlot(index.Value, tint, glow, brightness, invisible) });
        }
        return new ForgeVisual.SurfaceAppearance(tint, glow, brightness, invisible, slots);
    }

    private static float[] Colour(JsonElement value)
    {
        var colour = Floats(value, 3, "model-appearance");
        if (colour.Any(channel => channel < 0f)) throw Reject("model-appearance", value.GetRawText());
        return colour;
    }

    private static NativeModelTransform Transform(JsonElement value)
    {
        Keys(value, "position", "rotation", "scale");
        var position = Floats(value.GetProperty("position"), 3, "model-position");
        var rotation = Floats(value.GetProperty("rotation"), 4, "model-rotation");
        var scale = Floats(value.GetProperty("scale"), 3, "model-scale");
        double length = Math.Sqrt(rotation.Sum(number => (double)number * number));
        if (Math.Abs(length - 1d) > 0.0001d) throw Reject("model-rotation", "quaternion is not normalized");
        return new NativeModelTransform(position, rotation, scale);
    }

    private static void MatrixMatches(IReadOnlyList<NativeModelTransform> transforms, float[] expected)
    {
        var actual = Identity();
        foreach (var transform in transforms) actual = Multiply(actual, Compose(transform));
        for (int index = 0; index < 16; index++)
        {
            double tolerance = 0.0002d * Math.Max(1d, Math.Abs(expected[index]));
            if (Math.Abs(actual[index] - expected[index]) > tolerance)
                throw Reject("model-matrix-mismatch", "element " + index);
        }
    }

    private static double[] Identity() => new[] { 1d, 0, 0, 0, 0, 1d, 0, 0, 0, 0, 1d, 0, 0, 0, 0, 1d };

    // Three.js and Unity both serialize these matrices column-major. The chain remains TRS-by-TRS, so a product
    // containing shear is validated here and later reproduced by parent transforms instead of decomposed.
    private static double[] Compose(NativeModelTransform value)
    {
        double x = value.Rotation[0], y = value.Rotation[1], z = value.Rotation[2], w = value.Rotation[3];
        double x2 = x + x, y2 = y + y, z2 = z + z;
        double xx = x * x2, xy = x * y2, xz = x * z2;
        double yy = y * y2, yz = y * z2, zz = z * z2;
        double wx = w * x2, wy = w * y2, wz = w * z2;
        double sx = value.Scale[0], sy = value.Scale[1], sz = value.Scale[2];
        return new[]
        {
            (1 - (yy + zz)) * sx, (xy + wz) * sx, (xz - wy) * sx, 0,
            (xy - wz) * sy, (1 - (xx + zz)) * sy, (yz + wx) * sy, 0,
            (xz + wy) * sz, (yz - wx) * sz, (1 - (xx + yy)) * sz, 0,
            (double)value.Position[0], value.Position[1], value.Position[2], 1
        };
    }

    private static double[] Multiply(double[] left, double[] right)
    {
        var result = new double[16];
        for (int column = 0; column < 4; column++)
            for (int row = 0; row < 4; row++)
                for (int inner = 0; inner < 4; inner++)
                    result[column * 4 + row] += left[inner * 4 + row] * right[column * 4 + inner];
        return result;
    }

    internal static NativeModelBinding ReadBinding(JsonElement value)
    {
        Keys(value, "resource", "shard", "gameBuild");
        return new NativeModelBinding(ReadReference(value.GetProperty("resource")), Text(value, "shard"),
            Text(value, "gameBuild"));
    }

    internal static NativeModelSelection? ReadSelection(JsonElement value, string revision)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        KeysOptional(value, new[] { "sourceRevision", "childPath", "object", "materialSlots" }, "childIndices");
        string selectedRevision = Text(value, "sourceRevision");
        if (selectedRevision != revision) throw Reject("model-selection-revision", selectedRevision);
        var childPath = Path(value.GetProperty("childPath"), allowEmpty: true);
        NativeModelSourceObject? selectedObject = null;
        var objectValue = value.GetProperty("object");
        if (objectValue.ValueKind != JsonValueKind.Null) selectedObject = SourceObject(objectValue);
        var materialValue = value.GetProperty("materialSlots");
        if (materialValue.ValueKind != JsonValueKind.Array)
            throw Reject("model-selection-materials", materialValue.GetRawText());
        var materials = new List<NativeModelSourceObject>(materialValue.GetArrayLength());
        foreach (var material in materialValue.EnumerateArray()) materials.Add(SourceObject(material));
        return new NativeModelSelection(selectedRevision, childPath, Indices(value, childPath.Count), selectedObject, materials);
    }

    private static NativeModelSourceObject SourceObject(JsonElement value)
    {
        Keys(value, "file", "pathId");
        string file = Text(value, "file"), pathId = Text(value, "pathId");
        if (file.Contains('/') || file.Contains('\\')) throw Reject("model-selection-object", file);
        if (!long.TryParse(pathId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number)
            || number.ToString(CultureInfo.InvariantCulture) != pathId)
            throw Reject("model-selection-object", pathId);
        return new NativeModelSourceObject(file, pathId);
    }

    private static NativeModelResourceReference ReadReference(JsonElement value)
    {
        var reference = ResourceDefinitions.ReadReference(value);
        return new NativeModelResourceReference(reference.Id, reference.Revision);
    }

    internal static NativeModelLocator ReadLocator(JsonElement value)
    {
        var locator = ResourceDefinitions.ReadLocator(value);
        if (locator.Namespace != NativeNamespace || locator.Loader != NativeLoader || locator.Bundle != null)
            throw Reject("model-locator", locator.Namespace + "/" + locator.Loader);
        return new NativeModelLocator(locator.Namespace, locator.Loader, locator.Bundle, locator.ResourceName);
    }

    private static IReadOnlyList<string> Path(JsonElement value, bool allowEmpty)
    {
        if (value.ValueKind != JsonValueKind.Array || !allowEmpty && value.GetArrayLength() == 0
            || value.GetArrayLength() > MaximumPath) throw Reject("model-path", value.GetRawText());
        var result = new List<string>(value.GetArrayLength());
        foreach (var segment in value.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.String) throw Reject("model-path", segment.GetRawText());
            result.Add(Scalar(segment.GetString()!, "path"));
        }
        return result;
    }

    private static IReadOnlyList<int>? Indices(JsonElement value, int count)
    {
        if (!value.TryGetProperty("childIndices", out var indices)) return null;
        if (indices.ValueKind != JsonValueKind.Array || indices.GetArrayLength() != count)
            throw Reject("model-child-indices", indices.GetRawText());
        var result = new List<int>(count);
        foreach (var index in indices.EnumerateArray())
        {
            if (!index.TryGetInt32(out int number) || number < 0) throw Reject("model-child-indices", index.GetRawText());
            result.Add(number);
        }
        return result;
    }

    private static float[] Floats(JsonElement value, int count, string code)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count) throw Reject(code, value.GetRawText());
        var result = new float[count]; int index = 0;
        foreach (var item in value.EnumerateArray()) result[index++] = Number(item, code);
        return result;
    }

    private static float Number(JsonElement value, string code)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number)
            || number < -float.MaxValue || number > float.MaxValue) throw Reject(code, value.GetRawText());
        float result = (float)number;
        if (!float.IsFinite(result)) throw Reject(code, value.GetRawText());
        return result;
    }

    private static string Text(JsonElement value, string property) => Scalar(value.GetProperty(property).ValueKind == JsonValueKind.String
        ? value.GetProperty(property).GetString()! : throw Reject("model-text", property), property);
    private static string Scalar(string value, string property)
    {
        if (value.Length is 0 or > MaximumText || value.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
            throw Reject("model-text", property);
        return value;
    }

    private static void Keys(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != expected.Length)
            throw Reject("model-shape", value.ValueKind.ToString());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal))
                throw Reject("model-shape", property.Name);
        foreach (var name in expected) if (!seen.Contains(name)) throw Reject("model-shape", name);
    }

    private static void KeysOptional(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Reject("model-shape", value.ValueKind.ToString());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !optional.Contains(property.Name, StringComparer.Ordinal) && !required.Contains(property.Name, StringComparer.Ordinal))
                throw Reject("model-shape", property.Name);
        foreach (var name in required) if (!seen.Contains(name)) throw Reject("model-shape", name);
    }

    private static InvalidDataException Reject(string code, string detail) => new(code + ": " + detail);
}

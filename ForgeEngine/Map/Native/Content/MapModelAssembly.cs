using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>One package-owned, probe-admitted map assembly. Geometry, selection and TRS execution belong to Runtime.</summary>
internal sealed class MapModelAssembly : ActiveModelAssembly<MapModelAssembly>, IDisposable
{
    private static BuildStage? buildStage;
    internal PlacedModel Model { get; }

    private MapModelAssembly(PlacedModel model, string owner, NativeModelContent content,
        NativeModelRuntime.NativeModelRegistration registration) : base(owner, content, registration) => Model = model;

    /// <summary>Every imported model the package's placed models name, by reference key, found by hash in the package or its dependencies
    /// (I-RESOLVE). A model whose file is missing or fails its check is dropped alone and named in an Error (`resource-missing` names the
    /// package, the hash and who needs it); its placements draw without it. A placement that cannot be read is refused later by Prepare.</summary>
    internal static IReadOnlyDictionary<string, ImportedModelPackage> ImportedPackages(string bepInExRoot, MapPackageStage stage, Action<string> report)
    {
        var references = new List<NativeModelResourceReference>();
        foreach (var model in stage.PlacedModels.Where(model => model.Content.HasValue))
            try { references.AddRange(NativeModelContent.Read(model.Content!.Value, "map").ImportedResources); }
            catch (Exception error) when (error is InvalidDataException or RuntimeContractException or JsonException or InvalidOperationException) { }
        return ImportedModelFiles.ForReferences(bepInExRoot, stage.Root + "/forge/" + PlacedModelManifest.ComposedFileNameOnly, references,
            failure => report(failure.Code + ": " + failure.Relative + " " + failure.Reason));
    }

    internal static IReadOnlyList<MapModelAssembly> Prepare(string package, IReadOnlyList<PlacedModel> models,
        Func<MapLevelReference?> readLevel, IReadOnlyDictionary<string, ImportedModelPackage> imported)
    {
        var prepared = new List<MapModelAssembly>();
        string path = package + "/forge/" + PlacedModelManifest.ComposedFileNameOnly;
        foreach (var model in models.Where(model => model.Content.HasValue))
        {
            try
            {
                var root = model.Content!.Value;
                if (!DocumentReading.Known(root, "modelAssemblies", "nativeBindings", "resourceDefinitions", "resourceScope", "resourcePackages"))
                    throw new InvalidDataException("model-content-shape");
                var content = NativeModelContent.Read(root, "map");
                // A model is bound once per room that builds it: its placement id is the assembly id plus that room's suffix.
                if (content.Assemblies.Count != 1 || content.Assemblies[0].Id != model.Id && !model.Id.StartsWith(content.Assemblies[0].Id + "@", StringComparison.Ordinal))
                    throw new InvalidDataException("model-placement-assembly-mismatch");
                if (model.RequiresBuildWindow && !MapFieldHost.Hooks.Offers(MapHookFeatures.ModelBuildWindowFeature))
                    throw new MapPackageDocumentException("model-build-hooks-unavailable",
                        "id=" + model.Id + "; room=" + model.Room.RoomId + "; native pre-navigation build hook unavailable.", path);
                string owner = "map:" + package + ":" + model.Id;
                var registration = NativeModelRuntime.Prepare(owner, content, () =>
                    (readLevel() ?? throw new InvalidOperationException("model-level-unresolved")) == model.Level, imported);
                prepared.Add(new(model, owner, content, registration));
            }
            catch (MapPackageDocumentException) { throw; }
            catch (RuntimeContractException error)
            {
                throw new MapPackageDocumentException(error.Code,
                    "id=" + model.Id + "; room=" + model.Room.RoomId + "; " + error.Message, path);
            }
            catch (Exception error)
            {
                throw new MapPackageDocumentException("placed-model-content-invalid",
                    "id=" + model.Id + "; room=" + model.Room.RoomId + "; " + error.GetBaseException().Message, path);
            }
        }
        return Array.AsReadOnly(prepared.ToArray());
    }

    /// <summary>The world box of this placement's imported draws; false when it has none or is not placed.</summary>
    internal bool TryImportedBounds(out Bounds bounds)
        => NativeModelRuntime.TryImportedBounds(Owner, Model.Id, Assembly.Id, out bounds);

    /// <summary>The look materials this placement holds now; what the registration record reads the author's colours back from.</summary>
    internal (float[] Srgb, float Alpha)[] PaintedTints() => NativeModelRuntime.PaintedTints(Owner, Model.Id, Assembly.Id);

    /// <summary>True while the runtime's asynchronous shard load still owns a resource of this model.</summary>
    internal bool Loading => Committed && NativeModelRuntime.Loading(Assembly);

    internal string? Place(Transform host)
    {
        if (!Committed) return "model-assembly-unregistered";
        var stage = buildStage;
        if (Model.RequiresBuildWindow && (stage == null || !stage.Expects(this))) return "model-build-stage-required";
        var refusal = NativeModelRuntime.Place(Owner, Assembly, host, Model.Id, collision: Model.NativeCollision);
        if (refusal == null && Model.RequiresBuildWindow) stage!.Placed(this);
        return refusal;
    }
    internal void ClearWorld()
    {
        if (Committed) NativeModelRuntime.RemoveCopies(Owner, "");
        buildStage?.Cleared(this);
    }
    public void Dispose()
    {
        try { ClearWorld(); }
        finally { Rollback(); }
    }

    /// <summary>One navigation-build window. Every solid assembly selected for the level must place inside it;
    /// appearance-only assemblies keep using the existing pre-culling retry path.</summary>
    internal sealed class BuildStage : IDisposable
    {
        private readonly HashSet<MapModelAssembly> expected;
        private readonly HashSet<MapModelAssembly> pending;
        private readonly bool levelMissing;
        private bool disposed;

        internal BuildStage(MapLevelReference? level)
        {
            levelMissing = level == null;
            expected = new(Active.Values.Where(value => value.Model.RequiresBuildWindow
                && (level == null || value.Model.Level == level.Value)));
            pending = new(expected);
        }

        internal bool Required => pending.Count != 0;
        internal bool Expects(MapModelAssembly assembly) => !disposed && pending.Contains(assembly);
        internal void Placed(MapModelAssembly assembly) => pending.Remove(assembly);
        internal void Cleared(MapModelAssembly assembly) { if (!disposed && expected.Contains(assembly)) pending.Add(assembly); }
        internal string? Complete(Func<MapModelAssembly, bool> published)
        {
            ArgumentNullException.ThrowIfNull(published);
            foreach (var assembly in expected)
                if (published(assembly)) pending.Remove(assembly); else pending.Add(assembly);
            if (levelMissing && pending.Count != 0) return "model-build-level-unresolved";
            return pending.Count == 0 ? null : "model-build-stage-incomplete:"
                + string.Join(",", pending.Select(value => value.Model.Id).OrderBy(value => value, StringComparer.Ordinal));
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (ReferenceEquals(buildStage, this)) buildStage = null;
        }
    }

    internal static BuildStage OpenBuildStage(MapLevelReference? level)
    {
        if (buildStage != null) throw new InvalidOperationException("model-build-stage-reentered");
        return buildStage = new(level);
    }
}

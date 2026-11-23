using System;
using System.Collections.Generic;
using System.Linq;
using ForgeVisual;
using UnityEngine;

namespace ForgeEngine.GameBindings;

/// <summary>
/// Imported models in a level (FORGE-FRAMEWORK §8): ForgeVisual builds each model once per level from the verified package bytes
/// (<see cref="ImportedModels"/>, one cache per level, destroyed with the level), and a placement does not make a GameObject for them -
/// <see cref="NativeModelRuntime"/> hands the draws returned here to <c>InstancedMeshBatches</c> below the placement root.
/// This class decides what a level may hold (<see cref="LevelBudgetBytes"/>); a placement's tint, glow and invisibility are painted by the
/// same material path as a native model's (<see cref="NativeModelRuntime"/>). A skinned model (descriptor format 2) cannot be batched: it is
/// spawned once per placement as an object with its own skeleton below the root (<see cref="ImportedModel.Spawn"/>), for the owning domain
/// to pose. A placement that asks for collision gets the model's own collision shapes as colliders (see <see cref="Build"/>).
/// </summary>
internal static class ImportedModelPlacement
{
    /// <summary>The most all imported models of one level may hold at once (ForgeVisual's estimate: vertices, indices and mipped RGBA textures).
    /// A model that would pass it is refused with `imported-model-over-budget` and nothing is built for it.</summary>
    internal const long LevelBudgetBytes = 512L * 1024 * 1024;

    /// <summary>The refusal prefix a caller retries on instead of failing: the game's gear shader loads with the first built level.</summary>
    internal const string LoadingPrefix = "model-resource-loading:";

    private static ImportedModels? _cache;
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal);
    // Models asked for collision without carrying any shape, each named once per level.
    private static readonly HashSet<string> NoShapes = new(StringComparer.Ordinal);
    // Facing parts whose look hides only some slots (a renderer hides all or none), each named once per level.
    private static readonly HashSet<string> PartlyHidden = new(StringComparer.Ordinal);

    // The preloaded packages whose textures were still queued; emptied as their decodes finish, so a level past its build answers at once.
    private static readonly List<ImportedModelPackage> Queued = new();

    internal static void BeginLevel()
    {
        Skipped.Clear();
        NoShapes.Clear();
        PartlyHidden.Clear();
        Queued.Clear();
        _cache?.Dispose();
        _cache = new ImportedModels(LevelBudgetBytes);
    }

    /// <summary>Queues the texture decoding of the level's imported models while the level builds (ForgeVisual decodes them on a worker
    /// thread and uploads them within its frame budget), so the first placement of a model only builds its mesh and materials.</summary>
    internal static void Preload(IEnumerable<ImportedModelPackage> packages)
    {
        var cache = _cache;
        if (cache == null) return;
        foreach (var package in packages) if (!cache.Prepare(package.Descriptor, package.Textures)) Queued.Add(package);
    }

    /// <summary>True while a texture the level preloaded is still being decoded. Only the still-queued packages are asked again; nothing
    /// is allocated, and once all are done the answer is a count check.</summary>
    internal static bool Decoding()
    {
        var cache = _cache;
        if (cache == null) return false;
        for (int index = Queued.Count - 1; index >= 0; index--)
            if (cache.Prepare(Queued[index].Descriptor, Queued[index].Textures)) Queued.RemoveAt(index);
        return Queued.Count != 0;
    }

    internal static void EndLevel()
    {
        Skipped.Clear();
        NoShapes.Clear();
        PartlyHidden.Clear();
        Queued.Clear();
        _cache?.Dispose();
        _cache = null;
    }

    /// <summary>The batched draws of every imported part of one placement, appended to <paramref name="draws"/> in the root's frame; null when
    /// all were built, otherwise the named refusal of the first that was not. A part whose model the package lost a file of (it has no
    /// entry in <paramref name="packages"/>) is skipped alone and named once per level through <paramref name="report"/>: the other parts
    /// and the placement carry on. A part with a look gets the materials <paramref name="paint"/> returns for the model's own, with the
    /// submeshes that only cast their shadow (invisible); a slot of a submesh is its descriptor material. A skinned model is spawned below
    /// <paramref name="root"/> instead and added to <paramref name="skins"/>; it casts only its shadow when every slot does. A model's
    /// camera-facing parts are drawn by renderers of their own (see <see cref="Face"/>), added to <paramref name="skins"/> too, so they draw on the
    /// layer a batch would.</summary>
    /// <remarks>With <paramref name="colliders"/> (a placement that asked for collision) every part whose model carries collision shapes gets,
    /// below <paramref name="root"/> at the part's authored pose, one node per shape holding a MeshCollider on the model's shared collision
    /// mesh, on the game's default world layer (<c>LayerManager.LAYER_DEFAULT</c>: player movement, bullets and the navigation build all
    /// take it, as they take the room's own geometry); each node is appended with that layer. A model without shapes is named once per level
    /// (`model-collision-imported-skipped`, reason `no-collision-shapes`) and only decorates.</remarks>
    internal static string? Build(IReadOnlyDictionary<string, ImportedModelPackage> packages, IReadOnlyList<NativeModelImportedPart> parts,
        Transform root, List<GameObject> skins, ForgeVisual.SurfaceAppearance? appearance,
        Func<IReadOnlyList<Material>, Func<int, int>, ForgeVisual.SurfaceAppearance, (Material?[] Materials, bool[]? ShadowOnly)> paint,
        List<ForgeVisual.InstancedDraw> draws, Action<string>? report = null, List<(GameObject Node, int Layer)>? colliders = null)
    {
        var cache = _cache;
        if (cache == null) return "model-level-inactive";
        foreach (var part in parts)
        {
            if (!packages.TryGetValue(part.Resource.Key, out var package))
            {
                if (Skipped.Add(part.Resource.Key)) report?.Invoke("model-imported-missing: id=" + part.Resource.Id + " revision=" + part.Resource.Revision);
                continue;
            }
            // A texture still queued by Preload is not decoded a second time here: the placement waits like a loading shard.
            if (!cache.Prepare(package.Descriptor, package.Textures)) return LoadingPrefix + "imported-textures-decoding:" + part.Resource.Id;
            var result = cache.Create(package.Descriptor, package.Mesh, package.Textures);
            if (!result.Ok)
                return (result.Failure.Code == ImportedModelCodes.ShaderUnavailable ? LoadingPrefix : "") + result.Failure.Code + ":" + part.Resource.Id;
            var model = result.Model!;
            var look = ForgeVisual.SurfaceAppearance.Over(appearance, part.Appearance);
            var slots = model.Descriptor.Submeshes;
            var (materials, shadowOnly) = look == null ? (model.Materials.ToArray(), null)
                : paint(model.Materials, submesh => slots[submesh].Material, look);
            var local = Local(part.Transforms);
            if (colliders != null) Collide(model, part, root, colliders, report);
            if (!model.Skinned)
            {
                draws.Add(new(model.Mesh, materials, local, shadowOnly));
                for (int index = 0; index < model.FacingParts.Count; index++) skins.Add(Face(model.FacingParts[index], index, part, root, look, paint, report));
                continue;
            }
            var skin = model.Spawn(root, "ForgeModel.Skin." + string.Join(".", part.AuthorPath));
            skin.transform.localPosition = local.GetColumn(3);
            skin.transform.localRotation = local.rotation;
            skin.transform.localScale = local.lossyScale;
            var renderer = skin.GetComponent<SkinnedMeshRenderer>();
            renderer.sharedMaterials = materials!;
            if (shadowOnly != null && shadowOnly.All(only => only)) renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            skins.Add(skin);
        }
        return null;
    }

    /// <summary>One camera-facing part (a <c>FACE_</c> node): a node at its pivot below the part's pose chain with a mesh renderer of its own,
    /// kept turned about the world's vertical axis toward the local camera (<see cref="CameraFacing"/>, which turns it just before the cameras
    /// render; each machine turns its own copy, nothing is sent). A renderer, not a batched draw: a batch submits its matrix in LateUpdate, before
    /// the turn. It casts only its shadow when every slot does; a look that hides only some of its slots draws them (named once per level).
    /// It gets no collider: the model's collision stays still.</summary>
    private static GameObject Face(ImportedModelPart facingPart, int index, NativeModelImportedPart part, Transform root, ForgeVisual.SurfaceAppearance? look,
        Func<IReadOnlyList<Material>, Func<int, int>, ForgeVisual.SurfaceAppearance, (Material?[] Materials, bool[]? ShadowOnly)> paint,
        Action<string>? report)
    {
        var name = "ForgeModel.Facing." + string.Join(".", part.AuthorPath);
        var anchor = ForgeCore.StaticColliders.Node(Chain(root, part, name, LayerManager.LAYER_DEFAULT), name + "." + index, LayerManager.LAYER_DEFAULT,
            facingPart.Pivot, facingPart.Stand, Vector3.one).transform;
        var (materials, shadowOnly) = look == null ? (facingPart.Materials.ToArray(), null)
            : paint(facingPart.Materials, submesh => facingPart.Submeshes[submesh].Material, look);
        anchor.gameObject.AddComponent<MeshFilter>().sharedMesh = facingPart.Mesh;
        var renderer = anchor.gameObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = materials!;
        if (shadowOnly != null && shadowOnly.All(only => only)) renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        else if (shadowOnly != null && shadowOnly.Any(only => only) && PartlyHidden.Add(part.Resource.Key))
            report?.Invoke("model-facing-partly-hidden: id=" + part.Resource.Id + " reason=renderer-hides-all-or-none");
        CameraFacing.Follow(anchor, Facing.YawToCamera);
        return anchor.gameObject;
    }

    /// <summary>The authored transform chain of a part as bare nodes below <paramref name="root"/> (exact under any scale, as a native part's
    /// pose chain); its last node, or the root for a part with no transform.</summary>
    private static Transform Chain(Transform root, NativeModelImportedPart part, string name, int layer)
    {
        var parent = root;
        for (int index = 0; index < part.Transforms.Count; index++)
        {
            var pose = part.Transforms[index];
            parent = ForgeCore.StaticColliders.Node(parent, name + ".Pose." + index, layer,
                new Vector3(pose.Position[0], pose.Position[1], pose.Position[2]),
                new Quaternion(pose.Rotation[0], pose.Rotation[1], pose.Rotation[2], pose.Rotation[3]),
                new Vector3(pose.Scale[0], pose.Scale[1], pose.Scale[2])).transform;
        }
        return parent;
    }

    /// <summary>The colliders of one part: one MeshCollider node per shared collision mesh below the part's pose chain.</summary>
    private static void Collide(ImportedModel model, NativeModelImportedPart part, Transform root, List<(GameObject Node, int Layer)> colliders, Action<string>? report)
    {
        if (model.CollisionMeshes.Count == 0)
        {
            if (NoShapes.Add(part.Resource.Key)) report?.Invoke("model-collision-imported-skipped: id=" + part.Resource.Id + " revision=" + part.Resource.Revision
                + " reason=no-collision-shapes");
            return;
        }
        int layer = LayerManager.LAYER_DEFAULT;
        string name = string.Join(".", part.AuthorPath);
        var parent = Chain(root, part, "ForgeModel.Collision." + name, layer);
        for (int index = 0; index < model.CollisionMeshes.Count; index++)
        {
            var node = ForgeCore.StaticColliders.Node(parent, "ForgeModel.Collision." + name + "." + index, layer, Vector3.zero, Quaternion.identity, Vector3.one);
            ForgeCore.StaticColliders.MeshCollider(node, model.CollisionMeshes[index]);
            colliders.Add((node, layer));
        }
    }

    /// <summary>The pose Unity applies: the chain of authored transforms, parent first, as one matrix in the root's frame.</summary>
    private static Matrix4x4 Local(IReadOnlyList<NativeModelTransform> transforms)
    {
        var matrix = Matrix4x4.identity;
        foreach (var pose in transforms)
            matrix *= Matrix4x4.TRS(new Vector3(pose.Position[0], pose.Position[1], pose.Position[2]),
                new Quaternion(pose.Rotation[0], pose.Rotation[1], pose.Rotation[2], pose.Rotation[3]),
                new Vector3(pose.Scale[0], pose.Scale[1], pose.Scale[2]));
        return matrix;
    }

    /// <summary>The box of the draws below <paramref name="root"/>, from each mesh's bounds under its pose, in <paramref name="frame"/>'s own
    /// frame (null: the world).</summary>
    internal static bool TryBounds(Transform root, List<ForgeVisual.InstancedDraw> draws, Transform? frame, out Bounds bounds)
    {
        bounds = default;
        int boxes = 0;
        var toFrame = frame == null ? root.localToWorldMatrix : frame.worldToLocalMatrix * root.localToWorldMatrix;
        foreach (var draw in draws)
            if (draw.Mesh != null) ForgeVisual.Models.Encapsulate(ref bounds, ref boxes, toFrame * draw.Local, draw.Mesh.bounds);
        return boxes != 0;
    }
}

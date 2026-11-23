using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>The outcome of <see cref="ImportedModels.Create"/>: the model, or why there is none (a failure is also logged, once).</summary>
public sealed class ImportedModelResult
{
    internal ImportedModelResult(ImportedModel? model, ImportedModelFailure failure)
    {
        Model = model;
        Failure = failure;
    }

    public ImportedModel? Model { get; }
    public ImportedModelFailure Failure { get; }
    public bool Ok => Model != null;
}

/// <summary>A camera-facing part of an imported model (<see cref="ImportedFacingPart"/>): its own mesh, origin at its pivot, front along +Z.</summary>
public sealed class ImportedModelPart
{
    internal ImportedModelPart(Mesh mesh, Vector3 pivot, Quaternion stand, IReadOnlyList<ImportedSubmesh> runs)
    {
        Mesh = mesh;
        Pivot = pivot;
        Stand = stand;
        Submeshes = runs;
    }

    public Mesh Mesh { get; }

    /// <summary>Where the part turns, in the model's frame.</summary>
    public Vector3 Pivot { get; }

    /// <summary>How the part stands as authored, before anything turns it (the descriptor's yaw).</summary>
    public Quaternion Stand { get; }

    /// <summary>The materials in this part's submesh order; the same objects the model built for its descriptor's materials.</summary>
    public IReadOnlyList<Material> Materials => Slots;

    /// <summary>This part's submeshes (indices into its own mesh's runs as stored in the descriptor); a slot's descriptor material is its <c>Material</c>.</summary>
    public IReadOnlyList<ImportedSubmesh> Submeshes { get; }
    internal Material[] Slots = Array.Empty<Material>();
}

/// <summary>A built imported model: one Unity mesh with a submesh per run of its descriptor and one lit material per submesh. Every
/// instance made by <see cref="Spawn"/> shares them. The cache that made it owns it; do not destroy its parts.</summary>
public sealed class ImportedModel
{
    private readonly Mesh _mesh;
    private readonly Material[] _slots;
    private readonly Matrix4x4[]? _rest;
    private readonly List<GameObject> _spawned = new();

    internal ImportedModel(ImportedModelDescriptor descriptor, Mesh mesh, Material[] slots, List<Object> owned, Matrix4x4[]? rest, Mesh[] collision, ImportedModelPart[] facing)
    {
        Descriptor = descriptor;
        CollisionMeshes = collision;
        FacingParts = facing;
        _mesh = mesh;
        _slots = slots;
        _rest = rest;
        Owned = owned;
    }

    /// <summary>Whether the model is skinned to its own skeleton (descriptor format 2): <see cref="Spawn"/> then builds the bones too.</summary>
    public bool Skinned => _rest != null;

    public ImportedModelDescriptor Descriptor { get; }

    /// <summary>The mesh, for bounds and counts. Shared and no longer readable.</summary>
    public Mesh Mesh => _mesh;

    /// <summary>The materials in submesh order; shared by every instance. <c>Glow</c> and tints are written on copies.</summary>
    public IReadOnlyList<Material> Materials => _slots;

    /// <summary>One readable collision mesh per descriptor collision shape (the source's <c>COL_</c> nodes), in the model's own frame, built
    /// once with the model (<c>ForgeCore.StaticColliders.Mesh</c>) and shared by every placement: hand each to
    /// <c>StaticColliders.MeshCollider</c> on a node posed like the drawn model. Empty for a model that only decorates. Never drawn.</summary>
    public IReadOnlyList<Mesh> CollisionMeshes { get; }

    /// <summary>The parts that turn to face the camera (the source's <c>FACE_</c> nodes), each its own mesh in its own frame; not part of
    /// <see cref="Mesh"/>. Draw each on a node at <see cref="ImportedModelPart.Pivot"/> in the model's frame and keep that node turned with
    /// <see cref="CameraFacing.Follow(Transform, Facing, Transform?)"/> and <see cref="Facing.YawToCamera"/>. <see cref="Spawn"/> does both.</summary>
    public IReadOnlyList<ImportedModelPart> FacingParts { get; }

    internal List<Object> Owned { get; }

    /// <summary>A new object (under <paramref name="parent"/> when given) that draws the model, no collider: a mesh filter and a mesh renderer,
    /// or for a skinned model a skinned mesh renderer on the object and its skeleton below it, each bone a child object named as in the
    /// descriptor and standing at its rest pose (so the model stands as it was built; a caller poses the bones from there).</summary>
    public GameObject Spawn(Transform? parent = null, string? name = null)
    {
        var o = new GameObject(name ?? "Imported " + Descriptor.MeshSha256[..8]);
        if (parent != null) o.transform.SetParent(parent, false);
        if (_rest == null)
        {
            o.AddComponent<MeshFilter>().sharedMesh = _mesh;
            o.AddComponent<MeshRenderer>().sharedMaterials = new Il2CppReferenceArray<Material>(_slots);
            foreach (var part in FacingParts)
            {
                var turning = new GameObject(o.name + " facing");
                turning.transform.SetParent(o.transform, false);
                turning.transform.localPosition = part.Pivot;
                turning.transform.localRotation = part.Stand;
                turning.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
                turning.AddComponent<MeshRenderer>().sharedMaterials = new Il2CppReferenceArray<Material>(part.Slots);
                CameraFacing.Follow(turning.transform, Facing.YawToCamera);
            }
        }
        else
        {
            var specs = Descriptor.Bones!;
            var bones = new Transform[specs.Count];
            for (var i = 0; i < bones.Length; i++)
            {
                var bone = new GameObject(specs[i].Name).transform;
                var up = specs[i].Parent;
                bone.SetParent(up < 0 ? o.transform : bones[up], false);
                var local = up < 0 ? _rest[i] : _rest[up].inverse * _rest[i];
                bone.localPosition = local.GetColumn(3);
                bone.localRotation = local.rotation;
                bone.localScale = local.lossyScale;
                bones[i] = bone;
            }
            var skin = o.AddComponent<SkinnedMeshRenderer>();
            skin.sharedMesh = _mesh;
            skin.bones = new Il2CppReferenceArray<Transform>(bones);
            skin.rootBone = bones[0];
            skin.sharedMaterials = new Il2CppReferenceArray<Material>(_slots);
        }
        _spawned.RemoveAll(s => s == null || s.WasCollected);
        _spawned.Add(o);
        return o;
    }

    internal void DestroySpawned()
    {
        foreach (var s in _spawned) if (s != null && !s.WasCollected) Object.Destroy(s);
        _spawned.Clear();
    }
}

/// <summary>
/// Imported models (a player's uploaded <c>.glb</c> or a geomorph's own model, converted to the Forge model package) built from the
/// bytes a caller read and checked: the mesh as a Unity mesh with a submesh per material, the PNGs as textures with mip chains, one
/// lit material (<see cref="LitMaterial"/>) per material record. One cache is one level's models: the same descriptor (<see cref="ImportedModelDescriptor.Key"/>,
/// so two models sharing a mesh with other materials stay apart) is built once, a bad one is refused the same way every time, and <see cref="Dispose"/> destroys every instance, mesh, material and texture it made.
/// The library reads no file and computes no hash: the caller (ForgeEngine) found the bytes, matched them to the descriptor's hashes and
/// decided what a level may hold. The draw and the shared collision meshes are here; colliders are the placing caller's.
/// </summary>
public sealed class ImportedModels : IDisposable
{
    private readonly Dictionary<string, ImportedModel> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImportedModelFailure> _failures = new(StringComparer.Ordinal);
    private readonly ImageTextures _images;
    private readonly bool _keep;

    /// <param name="budgetBytes">The most <see cref="ImportedModelDescriptor.EstimatedBytes"/> the cache may hold at once; 0 for no limit. A model
    /// that would pass it is refused with <see cref="ImportedModelCodes.OverBudget"/> and nothing is built.</param>
    /// <param name="keep">For a cache that outlives a level: everything it builds is marked with <see cref="ForgeCore.Assets.Keep"/>, so the game's
    /// unused-asset sweep at a scene change does not unload a mesh, material or texture no instance happens to draw at that moment.</param>
    public ImportedModels(long budgetBytes = 0, bool keep = false)
    {
        BudgetBytes = budgetBytes;
        _keep = keep;
        _images = new ImageTextures(surface: true, keep: keep);
    }

    public long BudgetBytes { get; set; }

    /// <summary>The estimated bytes of the models built so far.</summary>
    public long ResidentBytes { get; private set; }

    public int Count => _models.Count;

    /// <summary>The longest main-thread step of a model this cache built over frames, in milliseconds: one mesh made in the image queue, or
    /// <see cref="Create"/> (the materials and the model). 0 before the first.</summary>
    public double LongestStepMs { get; private set; }

    // A model on its way (Prepare with its mesh bytes): the mesh file read and checked, and its bone weights laid out, on a worker; then each
    // Unity mesh (the still part, each facing part, each collision shape) made in its own step of ForgeCore's image queue, within that queue's
    // frame budget and after the pictures queued before it; Create then makes only the materials.
    private sealed class Staged
    {
        internal System.Threading.Tasks.Task<(ImportedMesh? Data, ImportedModelFailure Failure, BoneWeight[]? Weights)> Read = null!;
        internal Mesh? Still;
        internal Matrix4x4[]? Rest;
        internal ImportedModelPart[] Facing = Array.Empty<ImportedModelPart>();
        internal Mesh[] Collision = Array.Empty<Mesh>();
        internal readonly List<Object> Owned = new();
        internal readonly List<ForgeCore.ImageJob> Jobs = new();
        internal int Waiting = -1; // steps queued and not yet run; -1: none queued
        internal string Thrown = "";
    }

    private readonly Dictionary<string, Staged> _staged = new(StringComparer.Ordinal);

    /// <summary><see cref="Prepare(ImportedModelDescriptor, byte[], IReadOnlyList{byte[]})"/> for the pictures only: the mesh is then read and built in <see cref="Create"/>'s frame.</summary>
    public bool Prepare(ImportedModelDescriptor descriptor, IReadOnlyList<byte[]> textures) => Prepare(descriptor, null, textures);

    /// <summary>Spreads the work of <see cref="Create"/> over later frames, so building a model takes no one long frame: the PNGs decode in
    /// ForgeCore's image queue (<see cref="ForgeCore.Images.Queue(byte[], System.Action{UnityEngine.Texture2D, string}, bool, bool, int, bool, bool)"/>), the <paramref name="mesh"/> file is read and checked on a worker, and then each
    /// of its Unity meshes is made in its own step of the same queue and frame budget (<see cref="ForgeCore.Images.QueueStep"/>). True once all of it is
    /// done or failed (a failure is then refused by <see cref="Create"/> as before), false while some is still under way. Call it again each frame until it
    /// is true, then <see cref="Create"/>; skipping it only moves the work into <see cref="Create"/>'s frame.</summary>
    public bool Prepare(ImportedModelDescriptor descriptor, byte[]? mesh, IReadOnlyList<byte[]> textures)
    {
        if (_models.ContainsKey(descriptor.Key) || _failures.ContainsKey(descriptor.Key) || textures.Count != descriptor.Textures.Count) return true;
        var ready = Pictures(descriptor, textures);
        return mesh == null || Meshes(descriptor, mesh, textures) && ready;
    }

    // The pictures' part of Prepare: true once each is decoded or failed.
    private bool Pictures(ImportedModelDescriptor descriptor, IReadOnlyList<byte[]> textures)
    {
        var ready = true;
        bool One(int index, bool linear, bool normal = false)
        {
            var t = descriptor.Textures[index];
            return _images.Prepare(t.Sha256, textures[index], t.Width, t.Height, linear, normal);
        }
        foreach (var spec in descriptor.Materials)
        {
            if (spec.Glass) continue;
            if (spec.Texture >= 0) ready &= One(spec.Texture, false);
            if (spec.MetalTexture >= 0) ready &= One(spec.MetalTexture, true);
            if (spec.NormalTexture >= 0) ready &= One(spec.NormalTexture, true, normal: true);
            if (spec.Emissive && spec.EmissiveTexture >= 0) ready &= One(spec.EmissiveTexture, false);
        }
        return ready;
    }

    // The mesh's part of Prepare: true once the worker's read failed or every mesh step ran.
    private bool Meshes(ImportedModelDescriptor d, byte[] mesh, IReadOnlyList<byte[]> textures)
    {
        if (!_staged.TryGetValue(d.Key, out var s))
        {
            _staged[d.Key] = s = new Staged();
            s.Read = System.Threading.Tasks.Task.Run(() =>
            {
                // Managed only: the one file check (ImportedModelDescriptor.CheckFiles) and the weights' layout, no Unity call.
                var data = d.ReadFiles(mesh, textures, out var failure);
                return (data, failure, data?.BoneIndices != null ? Weights(data) : null);
            });
        }
        if (!s.Read.IsCompleted) return false;
        if (s.Read.IsFaulted || s.Read.Result.Data is not { } data) return true; // Create refuses it
        if (s.Waiting < 0)
        {
            // Each step is one Unity mesh. The still part draws the vertices before the facing parts; each facing part is its own mesh over its
            // own vertex run, so it can turn. Collision meshes stay readable: the physics engine cooks a MeshCollider from the CPU copy.
            var weights = s.Read.Result.Weights;
            s.Facing = new ImportedModelPart[d.Facing.Count];
            s.Collision = new Mesh[data.Collision.Length];
            s.Waiting = 1 + s.Facing.Length + s.Collision.Length;
            Step(s, () => s.Still = Kept(s, StillMesh(d, data, weights, out s.Rest)));
            for (var f = 0; f < s.Facing.Length; f++)
            {
                var index = f;
                Step(s, () => { s.Facing[index] = FacingPart(d, data, index); Kept(s, s.Facing[index].Mesh); });
            }
            for (var i = 0; i < s.Collision.Length; i++)
            {
                var index = i;
                Step(s, () => s.Collision[index] = Kept(s, CollisionMesh(d, data, index)));
            }
        }
        return s.Waiting == 0;
    }

    private void Step(Staged s, Action make)
    {
        s.Jobs.Add(ForgeCore.Images.QueueStep(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            make();
            LongestStepMs = Math.Max(LongestStepMs, watch.Elapsed.TotalMilliseconds);
        }, thrown =>
        {
            s.Waiting--;
            if (thrown.Length != 0 && s.Thrown.Length == 0) s.Thrown = thrown;
        }));
    }

    // What a step made, owned by the model on its way and kept from a scene change's unused-asset sweep while it waits (a cache that keeps).
    private T Kept<T>(Staged s, T made) where T : Object
    {
        s.Owned.Add(made);
        if (_keep) ForgeCore.Assets.Keep(made);
        return made;
    }

    // Drops a model on its way: its queued steps and what they made.
    private static void Drop(Staged s)
    {
        foreach (var job in s.Jobs) job.Cancel();
        foreach (var o in s.Owned) if (o != null && !o.WasCollected) Object.Destroy(o);
    }

    /// <summary>The model of <paramref name="descriptor"/> from its mesh bytes and its PNG textures (in the descriptor's order).</summary>
    public ImportedModelResult Create(ImportedModelDescriptor descriptor, byte[] mesh, IReadOnlyList<byte[]> textures)
    {
        var key = descriptor.Key;
        if (_models.TryGetValue(key, out var built)) return new ImportedModelResult(built, default);
        if (_failures.TryGetValue(key, out var prior)) return new ImportedModelResult(null, prior);
        if (BudgetBytes != 0 && ResidentBytes + descriptor.EstimatedBytes > BudgetBytes)
            return Refuse(descriptor, new ImportedModelFailure(ImportedModelCodes.OverBudget, descriptor.EstimatedBytes + " bytes on top of " + ResidentBytes + " pass the budget of " + BudgetBytes), false);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // What Prepare built over earlier frames; one still under way (Create called before Prepare said true) is dropped and built here.
        _staged.TryGetValue(key, out var staged);
        if (staged != null && (staged.Waiting != 0 || !staged.Read.IsCompleted))
        {
            Drop(staged);
            _staged.Remove(key);
            staged = null;
        }
        // The one file check an exporter runs too (ImportedModelDescriptor.CheckFiles).
        ImportedMesh? data;
        ImportedModelFailure failure;
        if (staged != null) (data, failure) = staged.Read.IsFaulted
            ? (null, new ImportedModelFailure(ImportedModelCodes.Build, staged.Read.Exception!.GetBaseException().Message))
            : (staged.Read.Result.Data, staged.Read.Result.Failure);
        else data = descriptor.ReadFiles(mesh, textures, out failure);
        if (data == null)
        {
            if (staged != null) { Drop(staged); _staged.Remove(key); }
            return Refuse(descriptor, failure, true);
        }
        if (Shaders.Find(LitMaterial.ShaderName) == null)
            return Refuse(descriptor, new ImportedModelFailure(ImportedModelCodes.ShaderUnavailable, "the game's gear shader is not loaded yet; models show once a level has been built"), false);

        _staged.Remove(key);
        var owned = staged?.Owned ?? new List<Object>();
        try
        {
            if (staged is { Thrown.Length: > 0 }) throw new InvalidOperationException(staged.Thrown);
            ImportedModel? model;
            if (staged != null) model = Build(descriptor, data, textures, owned, staged.Still!, staged.Rest, staged.Facing, staged.Collision, out failure);
            else
            {
                var still = StillMesh(descriptor, data, data.BoneIndices != null ? Weights(data) : null, out var rest);
                owned.Add(still);
                var facing = new ImportedModelPart[descriptor.Facing.Count];
                for (var f = 0; f < facing.Length; f++) owned.Add((facing[f] = FacingPart(descriptor, data, f)).Mesh);
                var collision = new Mesh[data.Collision.Length];
                for (var i = 0; i < collision.Length; i++) owned.Add(collision[i] = CollisionMesh(descriptor, data, i));
                model = Build(descriptor, data, textures, owned, still, rest, facing, collision, out failure);
            }
            if (model == null)
            {
                foreach (var o in owned) if (o != null && !o.WasCollected) Object.Destroy(o);
                return Refuse(descriptor, failure, true);
            }
            if (_keep) foreach (var o in owned) ForgeCore.Assets.Keep(o);
            _models.Add(key, model);
            ResidentBytes += descriptor.EstimatedBytes;
            if (staged != null) LongestStepMs = Math.Max(LongestStepMs, watch.Elapsed.TotalMilliseconds);
            return new ImportedModelResult(model, default);
        }
        catch (Exception error)
        {
            foreach (var o in owned) if (o != null && !o.WasCollected) Object.Destroy(o);
            return Refuse(descriptor, new ImportedModelFailure(ImportedModelCodes.Build, error.Message), true);
        }
    }

    // The still part's mesh, a skinned one with its bones (the rest poses out in `rest`; null for a still model). The mesh file carries no
    // tangents: MeshArrays computes them, also for a model without a normal map.
    private static Mesh StillMesh(ImportedModelDescriptor d, ImportedMesh data, BoneWeight[]? weights, out Matrix4x4[]? rest)
    {
        var mesh = Part("Forge imported " + d.MeshSha256[..8], data, 0, d.StillVertices, d.Submeshes);
        rest = null;
        if (d.Bones != null)
        {
            rest = new Matrix4x4[d.Bones.Count];
            var binds = new Matrix4x4[rest.Length];
            for (var b = 0; b < rest.Length; b++)
            {
                var m = d.Bones[b].Rest;
                rest[b] = new Matrix4x4(new Vector4(m[0], m[1], m[2], m[3]), new Vector4(m[4], m[5], m[6], m[7]), new Vector4(m[8], m[9], m[10], m[11]), new Vector4(m[12], m[13], m[14], m[15]));
                binds[b] = rest[b].inverse;
            }
            mesh.boneWeights = new Il2CppStructArray<BoneWeight>(weights ?? Weights(data));
            mesh.bindposes = new Il2CppStructArray<Matrix4x4>(binds);
        }
        // A skinned mesh stays readable: the game may skin it on the CPU.
        mesh.UploadMeshData(rest == null);
        return mesh;
    }

    private static ImportedModelPart FacingPart(ImportedModelDescriptor d, ImportedMesh data, int f)
    {
        var spec = d.Facing[f];
        var own = Part("Forge imported " + d.MeshSha256[..8] + " facing " + f, data, spec.VertexStart, spec.VertexCount, spec.Submeshes);
        own.UploadMeshData(true);
        return new ImportedModelPart(own, new Vector3(spec.Pivot[0], spec.Pivot[1], spec.Pivot[2]), Quaternion.AngleAxis(spec.Yaw * Mathf.Rad2Deg, Vector3.up), spec.Submeshes);
    }

    // Collision meshes stay readable: the physics engine cooks a MeshCollider from the CPU copy.
    private static Mesh CollisionMesh(ImportedModelDescriptor d, ImportedMesh data, int i) =>
        ForgeCore.StaticColliders.Mesh("Forge imported collision " + d.MeshSha256[..8] + "." + i, data.Collision[i].Positions, data.Collision[i].Indices);

    // The materials over the meshes made, and the model.
    private ImportedModel? Build(ImportedModelDescriptor d, ImportedMesh data, IReadOnlyList<byte[]> pngs, List<Object> owned, Mesh mesh, Matrix4x4[]? rest,
        ImportedModelPart[] facing, Mesh[] collision, out ImportedModelFailure failure)
    {
        failure = default;
        Texture2D? Decode(ImportedMaterial spec, int index, bool linear, out ImportedModelFailure fault, bool normal = false)
        {
            fault = default;
            var t = d.Textures[index];
            if (_images.TryGet(t.Sha256, pngs[index], t.Width, t.Height, out var decoded, out var code, out var error, linear, normal)) return decoded;
            fault = new ImportedModelFailure(ImportedModelCodes.TextureDecode, spec.Name + ": texture " + index + " (" + code + (error == null ? "" : ", " + error.Message) + ")");
            return null;
        }

        var made = new Material[d.Materials.Count];
        for (var m = 0; m < made.Length; m++)
        {
            var spec = d.Materials[m];
            if (spec.Glass)
            {
                var glass = LitMaterial.Glass("Forge imported " + spec.Name, new Color(spec.BaseColor[0], spec.BaseColor[1], spec.BaseColor[2], spec.BaseColor[3]));
                if (glass == null) { failure = new ImportedModelFailure(ImportedModelCodes.ShaderUnavailable, spec.Name + ": the game's glass shader is not loaded yet"); return null; }
                owned.Add(glass);
                made[m] = glass;
                continue;
            }
            Texture albedo;
            if (spec.Texture >= 0)
            {
                var decoded = Decode(spec, spec.Texture, false, out failure);
                if (decoded == null) return null;
                albedo = decoded;
            }
            else
            {
                albedo = Flat(spec.Name + " colour", linear: false,
                    ImportedColour.LinearToSrgbByte(spec.BaseColor[0]), ImportedColour.LinearToSrgbByte(spec.BaseColor[1]), ImportedColour.LinearToSrgbByte(spec.BaseColor[2]), ImportedColour.UnitToByte(spec.BaseColor[3]));
                owned.Add(albedo);
            }
            // The gear shader reads metal from red and smoothness from green of one map.
            Texture surface;
            if (spec.MetalTexture >= 0)
            {
                var map = Decode(spec, spec.MetalTexture, true, out failure);
                if (map == null) return null;
                surface = map;
            }
            else
            {
                surface = Flat(spec.Name + " surface", linear: true, ImportedColour.UnitToByte(spec.Metallic), ImportedColour.UnitToByte(1f - spec.Roughness), 255, 255);
                owned.Add(surface);
            }
            // A skinned model (a character) is drawn with this library's lit shader, its opaque materials too (a cut-off of 0 keeps every
            // pixel): on the game's gear shader a character's first-person arms took on the colour of the gun beside them in fast motion and
            // inspecting (Tda Miku Append), the same materials on this shader did not. The cause in the game's shader was not found.
            var own = spec.Cutout || rest != null;
            var material = LitMaterial.Create("Forge imported " + spec.Name, albedo, surface, own, spec.Cutout ? spec.Cutoff : 0f, out var why);
            if (material == null)
            {
                failure = new ImportedModelFailure(spec.Cutout ? ImportedModelCodes.CutoutUnsupported : ImportedModelCodes.ShaderUnavailable, spec.Name + ": " + why);
                return null;
            }
            owned.Add(material);
            if (spec.NormalTexture >= 0)
            {
                var normal = Decode(spec, spec.NormalTexture, true, out failure, normal: true);
                if (normal == null) return null;
                LitMaterial.SetRelief(material, normal);
            }
            if (spec.Emissive)
            {
                Texture2D? map = Texture2D.whiteTexture;
                if (spec.EmissiveTexture >= 0)
                {
                    map = Decode(spec, spec.EmissiveTexture, false, out failure);
                    if (map == null) return null;
                }
                const float gain = ColourSpace.EmissionPerUnit;
                Glow.Write(material, new Color(spec.EmissiveColor[0] * gain, spec.EmissiveColor[1] * gain, spec.EmissiveColor[2] * gain, 1f), map);
            }
            made[m] = material;
        }
        var slots = new Material[d.Submeshes.Count];
        for (var i = 0; i < slots.Length; i++) slots[i] = made[d.Submeshes[i].Material];
        foreach (var part in facing)
        {
            part.Slots = new Material[part.Submeshes.Count];
            for (var i = 0; i < part.Slots.Length; i++) part.Slots[i] = made[part.Submeshes[i].Material];
        }
        return new ImportedModel(d, mesh, slots, owned, rest, collision, facing);
    }

    /// <summary>A mesh over the vertex run <paramref name="first"/>..+<paramref name="count"/> drawn by <paramref name="runs"/> (whose indices the
    /// mesh file check kept inside that run), the indices rebased to the run.</summary>
    private static Mesh Part(string name, ImportedMesh data, int first, int count, IReadOnlyList<ImportedSubmesh> runs)
    {
        if (first == 0 && count * 3 == data.Positions.Length)
        {
            var whole = new (int, int)[runs.Count];
            for (var i = 0; i < whole.Length; i++) whole[i] = (runs[i].IndexStart, runs[i].IndexCount);
            return MeshArrays.Build(name, data.Positions, data.Normals, data.Uvs, null, data.Indices, whole);
        }
        var positions = new float[count * 3];
        var normals = new float[count * 3];
        var uvs = new float[count * 2];
        Array.Copy(data.Positions, first * 3, positions, 0, positions.Length);
        Array.Copy(data.Normals, first * 3, normals, 0, normals.Length);
        Array.Copy(data.Uvs, first * 2, uvs, 0, uvs.Length);
        var total = 0;
        foreach (var run in runs) total += run.IndexCount;
        var indices = new int[total];
        var spans = new (int, int)[runs.Count];
        for (int i = 0, at = 0; i < spans.Length; i++)
        {
            spans[i] = (at, runs[i].IndexCount);
            for (var k = 0; k < runs[i].IndexCount; k++) indices[at++] = data.Indices[runs[i].IndexStart + k] - first;
        }
        return MeshArrays.Build(name, positions, normals, uvs, null, indices, spans);
    }

    /// <summary>Unity's bone weights: each vertex's four, heaviest first.</summary>
    private static BoneWeight[] Weights(ImportedMesh data)
    {
        var index = data.BoneIndices!;
        var weight = data.BoneWeights!;
        var rows = new BoneWeight[index.Length / 4];
        Span<int> order = stackalloc int[4];
        for (var v = 0; v < rows.Length; v++)
        {
            for (var k = 0; k < 4; k++) order[k] = k;
            for (var a = 1; a < 4; a++)
                for (var b = a; b > 0 && weight[v * 4 + order[b]] > weight[v * 4 + order[b - 1]]; b--) (order[b], order[b - 1]) = (order[b - 1], order[b]);
            rows[v] = new BoneWeight
            {
                boneIndex0 = index[v * 4 + order[0]],
                weight0 = weight[v * 4 + order[0]],
                boneIndex1 = index[v * 4 + order[1]],
                weight1 = weight[v * 4 + order[1]],
                boneIndex2 = index[v * 4 + order[2]],
                weight2 = weight[v * 4 + order[2]],
                boneIndex3 = index[v * 4 + order[3]],
                weight3 = weight[v * 4 + order[3]],
            };
        }
        return rows;
    }

    private static Texture2D Flat(string name, bool linear, byte r, byte g, byte b, byte a)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear) { name = "Forge imported " + name };
        var pixel = new Color32(r, g, b, a);
        texture.SetPixels32(new Il2CppStructArray<Color32>(new[] { pixel, pixel, pixel, pixel }));
        texture.Apply(false, true);
        return texture;
    }

    private ImportedModelResult Refuse(ImportedModelDescriptor descriptor, ImportedModelFailure failure, bool remember)
    {
        if (remember) _failures[descriptor.Key] = failure;
        Plugin.Logger.ErrorOnce("visual-imported-model-not-built", "ForgeVisual imported model " + descriptor.MeshSha256[..8] + " was not built, " + failure + ".");
        return new ImportedModelResult(null, failure);
    }

    /// <summary>Destroys every instance and everything the cache built, and forgets the failures.</summary>
    public void Dispose()
    {
        foreach (var s in _staged.Values) Drop(s);
        _staged.Clear();
        foreach (var model in _models.Values)
        {
            model.DestroySpawned();
            foreach (var o in model.Owned) if (o != null && !o.WasCollected) Object.Destroy(o);
        }
        _models.Clear();
        _failures.Clear();
        ResidentBytes = 0;
        _images.Dispose();
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ForgeVisual;
using ForgeWeaponEnergyLab.Native;
using Gear;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>The energy guns' models: loading their bundles and surfaces, and putting them on the game's guns through ForgeVisual's <see cref="GunMount"/>.</summary>
internal static class WeaponModels
{
    internal const string ModelName = "ForgeWeaponEnergyLab.Model";
    // Each model's game hold preset (Data/gun-grips.json); ForgeVisual keeps the model's hand sockets and derives a missing left hand from it.
    private static readonly GunGrips Grips = GunGrips.Read(typeof(WeaponModels).Assembly, "ForgeWeaponEnergyLab.Data.gun-grips.json");
    private static readonly GunSights Sights = GunSights.Read(typeof(WeaponModels).Assembly, "ForgeWeaponEnergyLab.Data.gun-sights.json");
    private const string GearShader = "Cell/Player/CustomGearShader";
    // A bundle loads when the first gun using it is assembled. It unloads only after every gun object using it
    // was destroyed (loadout change, level exit, a departed peer) and stayed gone this long; holstered guns keep it.
    private const float IdleUnloadSeconds = 10f;
    // How often the loaded bundles are looked at for an idle one; an unload is never urgent.
    private const float IdleCheckSeconds = 5f;
    private sealed class PreparedModel
    {
        internal string Digest = "";
        internal GameObject Prefab = null!;
        internal WeaponPresentation Description = null!;
        internal Dictionary<string, SurfaceMaterials> Materials = null!;
        // Body parts that carry a reduced mesh: the full mesh is drawn in first person, the reduced one in every other view.
        internal Dictionary<string, (Mesh Full, Mesh Reduced)> Meshes = null!;
    }
    private sealed class LoadedBundle
    {
        internal AssetBundle Bundle = null!;
        internal PresentationContract Contract = null!;
        internal readonly Dictionary<string, SurfaceMaterials> Bodies = new();
        internal readonly List<Material> Created = new();
        internal float IdleSince = -1f;
    }
    private static readonly PreparedModel?[] Prepared = new PreparedModel[EnergyGears.CategorySlots];
    private static readonly Dictionary<(string Digest, string Key), PreparedModel> Models = new();
    private static readonly Dictionary<string, LoadedBundle> Bundles = new(StringComparer.Ordinal);
    private static readonly List<((string Digest, string Key) Model, IEnumerator Work)> Loading = new();
    private static readonly HashSet<(string Digest, string Key)> Failed = new();
    private static Shader _gearShader = null!, _reticleShader = null!;
    private static float _nextIdleCheck;
    // Every surface uses the game's item-camera projection in first person, including transparent sights.
    internal readonly record struct SurfaceMaterials(Material World, Material FirstPerson);
    private sealed class PendingMount
    {
        internal GearPartHolder Holder = null!;
        internal IntPtr Pointer;
        internal bool MagazineVisible = true;
    }
    private static readonly List<PendingMount> Pending = new();
    private static bool _ready;
    /// <summary>The live mounts and the bundle each one draws from, so a bundle in use stays loaded.</summary>
    private static readonly List<(GunMount Gun, string Digest)> Mounted = new();

    internal static bool TryDescription(uint categoryId, out WeaponPresentation description)
    {
        description = null!;
        var slot = EnergyGears.Slot(categoryId);
        if (slot < 0 || Prepared[slot] is not { } model) return false;
        description = model.Description; return true;
    }

    internal static IEnumerator Preload()
    {
        _gearShader = Shader.Find(GearShader) ?? throw new InvalidOperationException("Vanilla gear shader is unavailable: " + GearShader);
        _reticleShader = Shader.Find("FX/FX_MuzzleFlash") ?? throw new InvalidOperationException("Vanilla FPS reticle shader is unavailable.");
        _ready = true;
        yield break;
    }

    // Returns false when the model already failed to load; guns using it then work on the game's own model.
    private static bool Request(EnergyGear gear)
    {
        var model = (gear.ModelDigest, gear.Definition.Model.Key);
        if (Failed.Contains(model)) return false;
        if (Models.TryGetValue(model, out var prepared)) { Prepared[EnergyGears.Slot(gear.CategoryId)] = prepared; return true; }
        foreach (var load in Loading) if (load.Model == model) return true;
        Loading.Add((model, Load(gear.ModelPath, model.Item1, model.Item2)));
        return true;
    }

    // One load at a time, one step per frame: two models from one provider bundle must not open it twice.
    private static void AdvanceLoad()
    {
        if (Loading.Count == 0) return;
        var (model, work) = Loading[0];
        try { if (work.MoveNext()) return; }
        catch (Exception error)
        {
            Failed.Add(model);
            Plugin.Logger.ErrorOnce($"Energy model {model.Key} failed to load; guns using it fire with the game's own gun model: {error}");
        }
        (work as IDisposable)?.Dispose();
        Loading.RemoveAt(0);
    }

    private static IEnumerator Load(string path, string digest, string key)
    {
        if (!Bundles.TryGetValue(digest, out var loaded))
        {
            var bundleLoad = ForgeCore.Assets.LoadAsync(Plugin.Id, path, digest);
            while (!bundleLoad.IsDone) yield return null;
            // Registered before validation, so a rejected bundle is still released by the idle unload.
            loaded = new LoadedBundle { Bundle = bundleLoad.Bundle ?? throw new InvalidDataException("Cannot load model bundle (changed after registration was sealed, missing or damaged): " + key) };
            Bundles.Add(digest, loaded);
            var version = loaded.Bundle.LoadAssetAsync("presentation-format", Il2CppType.Of<TextAsset>());
            while (!version.isDone) yield return null;
            if (version.asset?.TryCast<TextAsset>()?.text.Trim() != PresentationContract.CurrentFormat.ToString())
                throw new InvalidDataException($"Model bundle requires presentation format {PresentationContract.CurrentFormat}: {key}");
            var profile = loaded.Bundle.LoadAssetAsync("presentation-profile", Il2CppType.Of<TextAsset>());
            while (!profile.isDone) yield return null;
            loaded.Contract = PresentationContract.Parse(profile.asset?.TryCast<TextAsset>()?.text ?? throw new InvalidDataException("Missing presentation profile: " + key));
        }
        var description = Array.Find(loaded.Contract.Weapons, item => item.Mode == key) ?? throw new InvalidDataException("Missing model presentation: " + key);
        var prefabRequest = loaded.Bundle.LoadAssetAsync(key, Il2CppType.Of<GameObject>());
        while (!prefabRequest.isDone) yield return null;
        var prefab = prefabRequest.asset?.TryCast<GameObject>();
        if (prefab == null) throw new InvalidDataException("Model missing: " + key);
        ForgeCore.Assets.Keep(prefab);
        GunSockets.FromChildren(prefab.transform, "Muzzle");
        var materials = new Dictionary<string, SurfaceMaterials>();
        var meshes = new Dictionary<string, (Mesh Full, Mesh Reduced)>();
        foreach (var part in description.Parts)
        {
            var surface = prefab.transform.Find(part.Name);
            if (surface == null || surface.GetComponent<MeshRenderer>() == null) throw new InvalidDataException($"Part surface missing: {key}/{part.Name}");
            if (part.Surface == "body")
            {
                if (!loaded.Bodies.TryGetValue(part.TextureSet, out var body))
                {
                    var set = loaded.Contract.TextureSet(part.TextureSet);
                    var textures = new Texture2D?[4];
                    var suffixes = new[] { "-albedo", "-metallic", "-emission", "-normal" };
                    for (var i = 0; i < 4; i++)
                    {
                        if (i == 2 && !set.Emission || i == 3 && !set.Normal) continue;
                        var request = loaded.Bundle.LoadAssetAsync(set.Name + suffixes[i], Il2CppType.Of<Texture2D>());
                        while (!request.isDone) yield return null;
                        textures[i] = ForgeCore.Assets.Keep(request.asset?.TryCast<Texture2D>() ?? throw new InvalidDataException("Texture missing: " + set.Name + suffixes[i]));
                    }
                    // Vanilla gear materials carry these properties.
                    var world = new Material(_gearShader) { name = "Energy " + set.Name };
                    loaded.Created.Add(ForgeCore.Assets.Keep(world));
                    world.SetTexture("_MainTex", textures[0]); world.SetTexture("_MetallicGlossMap", textures[1]);
                    if (textures[3] != null) world.SetTexture("_BumpMap", textures[3]);
                    world.SetFloat("_ShadingType", 4f); world.SetFloat("_IsCustomGearMaterial", 1f); world.SetFloat("_SupportsFPSRendering", 1f);
                    if (textures[2] != null) ForgeVisual.Glow.Write(world, Color.white, textures[2]);
                    else world.SetFloat("_EnableEmissive", 0f);
                    var firstPerson = LitMaterial.FirstPersonCopy(world);
                    loaded.Created.Add(ForgeCore.Assets.Keep(firstPerson));
                    world.SetPass(0); firstPerson.SetPass(0);
                    body = new SurfaceMaterials(world, firstPerson);
                    loaded.Bodies.Add(set.Name, body);
                }
                materials.Add(part.Name, body);
                if (part.ReducedTriangles > 0)
                {
                    var request = loaded.Bundle.LoadAssetAsync(key + "-" + part.Name + "-reduced", Il2CppType.Of<Mesh>());
                    while (!request.isDone) yield return null;
                    var reduced = ForgeCore.Assets.Keep(request.asset?.TryCast<Mesh>() ?? throw new InvalidDataException($"Reduced mesh missing: {key}/{part.Name}"));
                    meshes.Add(part.Name, (surface.GetComponent<MeshFilter>().sharedMesh, reduced));
                }
            }
            else if (part.Surface == "glass")
            {
                // The optic has no visible pane: the target shows through untinted and the mark floats in the opening.
                // The glass keeps its geometry, which bounds where a mark may show (WeaponBindings.InLens).
                surface.GetComponent<MeshRenderer>().enabled = false;
            }
            else
            {
                var tinted = Reticle(_reticleShader, key, part);
                loaded.Created.Add(ForgeCore.Assets.Keep(tinted));
                // The native FPS shader takes its tint from vertex colors, not a _Color property.
                var mesh = surface.GetComponent<MeshFilter>().sharedMesh;
                var c = part.Color;
                var colors = new Color[mesh.vertexCount];
                Array.Fill(colors, new Color(c.R, c.G, c.B, 1f));
                mesh.colors = colors;
                var firstPerson = LitMaterial.FirstPersonCopy(tinted);
                loaded.Created.Add(ForgeCore.Assets.Keep(firstPerson));
                tinted.SetPass(0); firstPerson.SetPass(0);
                materials.Add(part.Name, new SurfaceMaterials(tinted, firstPerson));
            }
        }
        foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            if (renderer.enabled && !materials.ContainsKey(renderer.name)) throw new InvalidDataException($"Unknown model surface: {key}/{renderer.name}");
        // Body/glass arrive without a CPU copy. Release the reticle's copy after applying its vertex tint.
        foreach (var mesh in prefab.GetComponentsInChildren<MeshFilter>(true))
            if (mesh.sharedMesh.isReadable) mesh.sharedMesh.UploadMeshData(true);
        Models.Add((digest, key), new PreparedModel { Digest = digest, Prefab = prefab, Description = description, Materials = materials, Meshes = meshes });
    }

    private static void UnloadIdle(float now)
    {
        if (now < _nextIdleCheck || Bundles.Count == 0) return;
        _nextIdleCheck = now + IdleCheckSeconds;
        List<string>? idle = null;
        foreach (var (digest, loaded) in Bundles)
        {
            if (InUse(digest)) { loaded.IdleSince = -1f; continue; }
            if (loaded.IdleSince < 0f) loaded.IdleSince = now;
            else if (now - loaded.IdleSince >= IdleUnloadSeconds) (idle ??= new List<string>()).Add(digest);
        }
        if (idle != null) foreach (var digest in idle) Unload(digest);
    }

    private static bool InUse(string digest)
    {
        foreach (var load in Loading) if (load.Model.Digest == digest) return true;
        foreach (var mount in Mounted) if (mount.Digest == digest) return true;
        foreach (var pending in Pending)
            if (pending.Holder != null && pending.Holder.CategoryData != null &&
                EnergyGears.ForCategory(pending.Holder.CategoryData.persistentID) is { } gear &&
                gear.ModelDigest == digest) return true;
        return false;
    }

    private static void Unload(string digest)
    {
        var loaded = Bundles[digest];
        Bundles.Remove(digest);
        foreach (var gear in EnergyGears.All)
        {
            var slot = EnergyGears.Slot(gear.CategoryId);
            if (Prepared[slot]?.Digest == digest) Prepared[slot] = null;
        }
        var stale = new List<(string, string)>();
        foreach (var model in Models.Keys) if (model.Digest == digest) stale.Add(model);
        foreach (var model in stale) Models.Remove(model);
        foreach (var material in loaded.Created) Object.Destroy(material);
        loaded.Bundle.Unload(true);
    }

    // Reticles are lit marks floating in the optic's opening; only the first-person view shows them.
    private static Material Reticle(Shader unlit, string key, PartPresentation part)
    {
        var reticle = new Material(unlit) { name = "Energy reticle " + key + " " + part.Name, renderQueue = 3001 };
        reticle.SetFloat("_SupportsFPSRendering", 1f);
        reticle.SetFloat("_IntensityBoost", 1.6f);
        return reticle;
    }

    /// <summary>Equip/assembly takes a cached mount, takes a prepared model the same frame, or queues preparation. True when the gun has its model, or never
    /// will because that model failed: such a gun works on the game's own model, so a failed model never leaves it silent.</summary>
    internal static bool Ensure(GearPartHolder holder)
    {
        if (holder == null || holder.CategoryData == null) return false;
        var gear = EnergyGears.ForCategory(holder.CategoryData.persistentID);
        if (gear == null || gear.Mode == EnergyMode.Off) return false;
        if (GunMount.Of(holder) is { } mounted)
        {
            mounted.Refresh();
            return true;
        }
        if (Failed.Contains((gear.ModelDigest, gear.Definition.Model.Key))) return true;
        QueueMount(holder);
        // This frame, before the game draws the assembled gun: take the prepared model now instead of on the next LateUpdate, so the bare game gun never shows.
        TickPending();
        return GunMount.Of(holder) != null;
    }

    private static PendingMount QueueMount(GearPartHolder holder)
    {
        var pointer = holder.Pointer;
        foreach (var pending in Pending) if (pending.Pointer == pointer) return pending;
        var queued = new PendingMount { Holder = holder, Pointer = pointer };
        Pending.Add(queued);
        return queued;
    }

    private static void CreateMount(GearPartHolder holder, EnergyGear gear, PreparedModel assets, bool magazineVisible)
    {
        if (Mounted.Count >= 128) throw new InvalidOperationException("Energy animation instance budget exhausted.");
        var mount = GunMount.Create(holder, assets.Prefab, new GunMountOptions
        {
            Name = ModelName,
            Sockets = root => GunSockets.FromChildren(root, "Muzzle"),
            Grips = Grips,
            Sights = Sights,
            MagazineVisible = magazineVisible,
            ApplyView = (model, magazine, layer) => ApplySurfaces(assets, model, magazine, layer, layer == GunMount.FirstPersonLayer),
            Bound = WeaponBindings.Register,
            Released = WeaponBindings.Forget,
        });
        Mounted.Add((mount, assets.Digest));
    }

    // First person uses the FPS gear materials and casts no shadows; glass and reticles never do. Reticles are marks
    // projected along the moving optic's axis; they start hidden and TickSight shows them only while aiming through the glass.
    private static void ApplySurfaces(PreparedModel assets, Transform model, Transform? magazine, int layer, bool fullMesh)
    {
        var firstPerson = layer == GunMount.FirstPersonLayer;
        foreach (var part in assets.Description.Parts)
        {
            var surface = part.Name == "Magazine" && magazine != null ? magazine : model.Find(part.Name);
            if (surface == null || part.Surface == "glass") continue;
            var renderer = surface.GetComponent<MeshRenderer>();
            if (assets.Meshes.TryGetValue(part.Name, out var meshes))
                surface.GetComponent<MeshFilter>().sharedMesh = fullMesh ? meshes.Full : meshes.Reduced;
            var pair = assets.Materials[part.Name];
            renderer.sharedMaterial = firstPerson ? pair.FirstPerson : pair.World;
            var shadows = !firstPerson && part.Surface == "body";
            renderer.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = shadows;
            if (part.Surface == "reticle") surface.gameObject.SetActive(false);
        }
    }

    // After the native pose update, project the optic's virtual mark back onto its authored reticle plane.
    // The virtual point belongs to SightLook, never to the camera's look/raycast target.
    internal static void TickSight(FirstPersonItemHolder? fp)
    {
        if (_heldWeapon == null || fp == null || !WeaponBindings.TryGet(_heldWeapon, out var binding) || binding.Reticles.Length == 0) return;
        var camera = fp.m_LookCamera;
        // Leaving aim hides the mark at once; it would otherwise hang at the screen centre while the gun lowers.
        var weight = fp.m_aimWeight;
        var show = weight > .5f && weight >= binding.AimWeight - .001f && camera != null;
        binding.AimWeight = weight;
        var eye = show ? binding.Sight.InverseTransformPoint(camera!.transform.position) : Vector3.zero;
        foreach (var part in binding.Reticles)
        {
            var surface = part.Surface;
            if (surface == null) continue;
            var offset = default(System.Numerics.Vector3);
            // A mark projected outside the glass is not seen, e.g. while the gun rises into aim after a weapon switch.
            var visible = show && SightProjection.TryOffset(new System.Numerics.Vector3(eye.x, eye.y, eye.z), part.PlaneDepth, out offset) &&
                binding.InLens(part, new Vector2(offset.X, offset.Y));
            surface.localPosition = part.RestPosition + (visible
                ? binding.Model.InverseTransformVector(binding.Sight.TransformVector(new Vector3(offset.X, offset.Y, 0f)))
                : Vector3.zero);
            if (surface.gameObject.activeSelf != visible) surface.gameObject.SetActive(visible);
        }
    }

    /// <summary>A free-standing copy of the gun's model with its world materials, for the game's icon renderer (EnergyIcons); the renderer
    /// destroys it when the picture is taken. Null while the model is still loading (the load is started); <paramref name="failed"/> is set
    /// when it can never load.</summary>
    internal static GameObject? IconModel(EnergyGear gear, out bool failed)
    {
        failed = false;
        if (!_ready) return null;
        if (Prepared[EnergyGears.Slot(gear.CategoryId)] is not { } assets)
        {
            failed = !Request(gear);
            return null;
        }
        var instance = Object.Instantiate(assets.Prefab);
        try
        {
            instance.name = "Energy icon model " + gear.Definition.Id;
            ApplySurfaces(assets, instance.transform, null, instance.layer, true);
            instance.SetActive(true);
            return instance;
        }
        catch
        {
            Object.Destroy(instance);
            throw;
        }
    }

    internal static void BindWeapon(ItemEquippable weapon)
    {
        if (weapon?.GearPartHolder == null) return;
        Ensure(weapon.GearPartHolder);
    }

    /// <summary>Whether a mount, a queued mount, a model load or a loaded bundle exists: the only times the per-frame ticks have work.</summary>
    internal static bool HasWork => Mounted.Count != 0 || Pending.Count != 0 || Loading.Count != 0 || Bundles.Count != 0;

    internal static void TickPending()
    {
        for (var i = Mounted.Count - 1; i >= 0; i--)
            if (!Mounted[i].Gun.Alive) Mounted.RemoveAt(i);
        if (!_ready) return;
        AdvanceLoad();
        UnloadIdle(Time.time);
        for (var i = Pending.Count - 1; i >= 0; i--)
        {
            var pending = Pending[i];
            var holder = pending.Holder;
            if (holder == null || holder.CategoryData == null) { Pending.RemoveAt(i); continue; }
            var gear = EnergyGears.ForCategory(holder.CategoryData.persistentID);
            if (gear == null) continue;
            if (Prepared[EnergyGears.Slot(gear.CategoryId)] is not { } assets)
            {
                if (!Request(gear)) Pending.RemoveAt(i);
                continue;
            }
            Pending.RemoveAt(i);
            // A model that cannot go on is marked failed like one that cannot load: the gun works on the game's own model and nothing asks
            // again; it never stops the controller, whose fault is kept for a damage call that may have committed.
            try { if (GunMount.Of(holder) == null) CreateMount(holder, gear, assets, pending.MagazineVisible); }
            catch (Exception error)
            {
                Failed.Add((gear.ModelDigest, gear.Definition.Model.Key));
                Plugin.Logger.ErrorOnce($"Energy model {gear.Definition.Model.Key} could not be put on a gun; guns using it fire with the game's own gun model: {error}");
            }
            return;
        }
    }

    // The local player's energy gun in the hand, kept by the wield events (the holder's per-frame sight callback acts only for it).
    private static ItemEquippable? _heldWeapon;


    /// <summary>A bullet weapon went into or out of a hand: only the local player's energy gun is kept as the held one.</summary>
    internal static void NoteHeld(BulletWeapon weapon, bool held)
    {
        if (held)
        {
            if (weapon.Owner?.Owner is not { IsLocal: true, IsBot: false }) return;
            _heldWeapon = weapon;
        }
        else if (_heldWeapon != null && _heldWeapon.Pointer == weapon.Pointer) _heldWeapon = null;
    }

    /// <summary>A native reload action can precede the queued mount: its actual visibility value is kept, so the first model frame does not
    /// bring back a magazine already removed. A mounted gun's magazine follows through ForgeVisual's <c>GunMount</c>.</summary>
    internal static void NoteMagazineVisible(ItemEquippable weapon, bool visible)
    {
        if (weapon?.GearPartHolder == null || GunMount.Of(weapon.GearPartHolder) != null) return;
        if (weapon.GearCategoryData != null && EnergyGears.ModeForCategory(weapon.GearCategoryData.persistentID) != EnergyMode.Off)
            QueueMount(weapon.GearPartHolder).MagazineVisible = visible;
    }

}

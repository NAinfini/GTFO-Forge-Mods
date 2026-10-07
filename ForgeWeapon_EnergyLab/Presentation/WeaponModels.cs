using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ForgeWeaponEnergyLabExperimental.Native;
using Gear;
using Il2CppInterop.Runtime;
using Player;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

/// <summary>Metre-scale models attached by the grip, with native hand and sight bindings.</summary>
internal static class WeaponModels
{
    internal const string ModelName = "ForgeWeaponEnergyLabExperimental.Model";
    private const int FirstPersonLayer = 12;
    private const float ModelScale = 0.85f;
    private const string GearShader = "Cell/Player/CustomGearShader";
    // A bundle loads when the first gun using it is assembled. It unloads only after every gun object using it
    // was destroyed (loadout change, level exit, a departed peer) and stayed gone this long; holstered guns keep it.
    private const float IdleUnloadSeconds = 10f;
    private sealed class PreparedModel
    {
        internal string Digest = "";
        internal GameObject Prefab = null!;
        internal WeaponPresentation Description = null!;
        internal Dictionary<string, SurfaceMaterials> Materials = null!;
    }
    private sealed class LoadedBundle
    {
        internal AssetBundle Bundle = null!;
        internal PresentationContract Contract = null!;
        internal readonly Dictionary<string, SurfaceMaterials> Bodies = new();
        internal readonly List<Material> Created = new();
        internal float IdleSince = -1f;
    }
    private static readonly PreparedModel?[] Prepared = new PreparedModel[ushort.MaxValue + 1];
    private static readonly Dictionary<(string Digest, string Key), PreparedModel> Models = new();
    private static readonly Dictionary<string, LoadedBundle> Bundles = new(StringComparer.Ordinal);
    private static readonly List<((string Digest, string Key) Model, IEnumerator Work)> Loading = new();
    private static readonly HashSet<(string Digest, string Key)> Failed = new();
    private static Shader _gearShader = null!, _glassShader = null!, _reticleShader = null!;
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
    private sealed class AnimationMount
    {
        internal EnergyMode Mode;
        internal PreparedModel Assets = null!;
        internal IntPtr HolderPointer;
        internal GearPartHolder Holder = null!;
        internal ItemEquippable? Owner;
        internal Transform HolderTransform = null!;
        internal Transform Model = null!, DonorGrip = null!, DonorLeft = null!;
        internal GameObject ModelObject = null!;
        internal Transform RightProxy = null!, LeftProxy = null!;
        internal BindingSnapshot NativeBindings = null!;
        internal Transform Muzzle = null!, Sight = null!;
        internal GameObject? MagazineObject;
        internal int WorldLayer, ViewLayer = -1;
        internal bool RegisteredFirstPerson;
        internal Transform? DonorMagazine, Magazine;
        internal AnimationFrame GripRest, LeftRest, RightAuthoredRest, LeftAuthoredRest, LeftTargetRest;
        internal AnimationFrame ModelRest, MagazineRest, DonorMagazineRest;
        internal bool MagazineVisible = true;
    }
    private static readonly List<AnimationMount> Animations = new();
    private static readonly Dictionary<IntPtr, AnimationMount> Mounts = new();
    private sealed class BindingSnapshot
    {
        internal Transform? Right, Left, Muzzle, Sight;
        internal ItemEquippable? Weapon;
        internal Transform? WeaponRight, WeaponLeft, WeaponMuzzle, WeaponSight;
        internal PlayerSyncIK? SyncIK;
        internal Transform? SyncLeftTarget, SyncProxy;
        internal WeaponBindings.Snapshot? Registration;
        internal bool WritesAttempted;
    }
    private readonly record struct RendererSnapshot(Renderer Renderer, bool Enabled, bool ForceRenderingOff);
    private readonly record struct AnimatorSnapshot(Animator Animator, AnimatorCullingMode CullingMode);

    private static bool IsProxy(Transform? node, Transform proxy) => node is not null && node.Pointer == proxy.Pointer;

    private static PlayerSyncIK? WieldedSyncIK(ItemEquippable? weapon)
    {
        var owner = weapon != null ? weapon.Owner : null;
        var sync = owner != null ? owner.SyncIK : null;
        return owner != null && owner.Inventory != null && owner.Inventory.WieldedItem == weapon &&
            sync != null && sync.m_leftArmSolver != null ? sync : null;
    }

    private static BindingSnapshot CaptureBindings(GearPartHolder holder, AnimationMount? mount)
    {
        var weapon = holder.GetComponentInParent<ItemEquippable>();
        var snapshot = new BindingSnapshot
        {
            Right = holder.m_aRightHand, Left = holder.m_aLeftHand, Muzzle = holder.m_aMuzzle, Sight = holder.m_aSightLook,
            Weapon = weapon,
            WeaponRight = weapon != null ? weapon.m_rightHandGripAlign : null,
            WeaponLeft = weapon != null ? weapon.m_leftHandGripAlign : null,
            WeaponMuzzle = weapon != null ? weapon.MuzzleAlign : null,
            WeaponSight = weapon != null ? weapon.SightLookAlign : null,
            Registration = weapon != null ? WeaponBindings.Capture(weapon) : null
        };
        snapshot.SyncIK = WieldedSyncIK(weapon);
        if (snapshot.SyncIK != null) snapshot.SyncLeftTarget = snapshot.SyncIK.m_leftArmSolver.target;
        if (mount != null)
        {
            snapshot.SyncProxy = mount.LeftProxy;
            // A rebind's rollback restores native nodes, never the proxies installed by an earlier bind.
            if (IsProxy(snapshot.Right, mount.RightProxy)) snapshot.Right = mount.DonorGrip;
            if (IsProxy(snapshot.Left, mount.LeftProxy)) snapshot.Left = mount.NativeBindings.Left;
            if (snapshot.Muzzle == mount.Muzzle) snapshot.Muzzle = mount.NativeBindings.Muzzle;
            if (snapshot.Sight == mount.Sight) snapshot.Sight = mount.NativeBindings.Sight;
            if (IsProxy(snapshot.WeaponRight, mount.RightProxy)) snapshot.WeaponRight = mount.DonorGrip;
            if (IsProxy(snapshot.WeaponLeft, mount.LeftProxy)) snapshot.WeaponLeft = mount.DonorLeft;
            if (snapshot.WeaponMuzzle == mount.Muzzle) snapshot.WeaponMuzzle = mount.NativeBindings.WeaponMuzzle;
            if (snapshot.WeaponSight == mount.Sight) snapshot.WeaponSight = mount.NativeBindings.WeaponSight;
            if (IsProxy(snapshot.SyncLeftTarget, mount.LeftProxy)) snapshot.SyncLeftTarget = mount.DonorLeft;
        }
        return snapshot;
    }

    private static void AttemptRollback(Action action, List<Exception> failures)
    {
        try { action(); }
        catch (Exception error) { failures.Add(error); }
    }

    // Unity's null comparison rejects destroyed wrappers. Never reattach a dead donor socket.
    private static Transform Live(Transform? value) => value != null ? value : null!;

    private static void RestoreSyncTarget(BindingSnapshot snapshot, List<Exception> failures)
    {
        if (snapshot.SyncIK == null || snapshot.SyncProxy is null) return;
        AttemptRollback(() =>
        {
            // A weapon switch may already have installed another item's target; only undo our own binding.
            if (snapshot.SyncIK.m_leftArmSolver != null && IsProxy(snapshot.SyncIK.m_leftArmSolver.target, snapshot.SyncProxy))
                snapshot.SyncIK.SetLeftArmTarget(Live(snapshot.SyncLeftTarget));
        }, failures);
    }

    private static void RestoreBindings(GearPartHolder holder, BindingSnapshot snapshot, List<Exception> failures)
    {
        if (holder != null)
        {
            AttemptRollback(() => holder.m_aRightHand = Live(snapshot.Right), failures);
            AttemptRollback(() => holder.m_aLeftHand = Live(snapshot.Left), failures);
            AttemptRollback(() => holder.m_aMuzzle = Live(snapshot.Muzzle), failures);
            AttemptRollback(() => holder.m_aSightLook = Live(snapshot.Sight), failures);
        }
        var weapon = snapshot.Weapon;
        if (weapon != null)
        {
            AttemptRollback(() => weapon.m_rightHandGripAlign = Live(snapshot.WeaponRight), failures);
            AttemptRollback(() => weapon.m_leftHandGripAlign = Live(snapshot.WeaponLeft), failures);
            AttemptRollback(() => weapon.MuzzleAlign = Live(snapshot.WeaponMuzzle), failures);
            AttemptRollback(() => weapon.SightLookAlign = Live(snapshot.WeaponSight), failures);
        }
        RestoreSyncTarget(snapshot, failures);
        // Even when the native weapon was destroyed, drop a registration created by the failed attempt.
        if (snapshot.Registration != null) WeaponBindings.Restore(snapshot.Registration);
    }
    private static AnimationFrame Frame(Transform holder, Transform part)
    {
        var p = holder.InverseTransformPoint(part.position);
        var q = Quaternion.Inverse(holder.rotation) * part.rotation;
        return new AnimationFrame(new System.Numerics.Vector3(p.x,p.y,p.z), new System.Numerics.Quaternion(q.x,q.y,q.z,q.w));
    }
    private static void ApplyFrame(Transform holder, Transform part, AnimationFrame frame)
    {
        var p = frame.Position; var q = frame.Rotation;
        part.position = holder.TransformPoint(new Vector3(p.X,p.Y,p.Z));
        part.rotation = holder.rotation * new Quaternion(q.X,q.Y,q.Z,q.W);
    }

    private static Transform CreateHandProxy(Transform holder, string name)
    {
        var proxy = new GameObject(name);
        try { proxy.transform.SetParent(holder, false); return proxy.transform; }
        catch { Object.Destroy(proxy); throw; }
    }

    private static void DestroyHandProxy(Transform? proxy, List<Exception> failures)
    {
        if (proxy == null) return;
        var instance = proxy.gameObject;
        AttemptRollback(() => instance.SetActive(false), failures);
        AttemptRollback(() => proxy.SetParent(null, true), failures);
        AttemptRollback(() => Object.Destroy(instance), failures);
    }
    internal static bool TryDescription(uint categoryId, out WeaponPresentation description)
    {
        description = null!;
        if (categoryId > ushort.MaxValue || Prepared[categoryId] is not { } model) return false;
        description = model.Description; return true;
    }

    internal static IEnumerator Preload()
    {
        _gearShader = Shader.Find(GearShader) ?? throw new InvalidOperationException("Vanilla gear shader is unavailable: " + GearShader);
        _glassShader = Shader.Find("GTFO/Glass") ?? throw new InvalidOperationException("Vanilla glass shader is unavailable.");
        _reticleShader = Shader.Find("FX/FX_MuzzleFlash") ?? throw new InvalidOperationException("Vanilla FPS reticle shader is unavailable.");
        _ready = true;
        yield break;
    }

    // Returns false when the model already failed to load; guns using it then stay unarmed.
    private static bool Request(EnergyGear gear)
    {
        var model = (EnergyGears.ModelHashes[gear.ModelPath], gear.Definition.Model.Key);
        if (Failed.Contains(model)) return false;
        if (Models.TryGetValue(model, out var prepared)) { Prepared[gear.CategoryId] = prepared; return true; }
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
            Plugin.Error($"Energy model {model.Key} failed to load; guns using it stay unarmed: {error}");
        }
        (work as IDisposable)?.Dispose();
        Loading.RemoveAt(0);
    }

    private static IEnumerator Load(string path, string digest, string key)
    {
        if (!Bundles.TryGetValue(digest, out var loaded))
        {
            using (var stream = File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA256.Create())
                if (Convert.ToHexString(sha.ComputeHash(stream)) != digest)
                    throw new InvalidDataException("Model bundle changed after registration was sealed: " + key);
            var bundleLoad = AssetBundle.LoadFromFileAsync(path);
            while (!bundleLoad.isDone) yield return null;
            // Registered before validation, so a rejected bundle is still released by the idle unload.
            loaded = new LoadedBundle { Bundle = bundleLoad.assetBundle ?? throw new InvalidDataException("Cannot load model bundle: " + path) };
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
        AssetRetention.Keep(prefab);
        foreach (var socket in new[] { "RightHand", "LeftHand", "Muzzle", "SightLook" })
            ModelSockets.Require(prefab.transform, socket);
        var materials = new Dictionary<string, SurfaceMaterials>();
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
                        textures[i] = AssetRetention.Keep(request.asset?.TryCast<Texture2D>() ?? throw new InvalidDataException("Texture missing: " + set.Name + suffixes[i]));
                    }
                    // Vanilla gear materials carry these properties.
                    var world = new Material(_gearShader) { name = "Energy " + set.Name };
                    loaded.Created.Add(AssetRetention.Keep(world));
                    world.SetTexture("_MainTex", textures[0]); world.SetTexture("_MetallicGlossMap", textures[1]);
                    if (textures[3] != null) world.SetTexture("_BumpMap", textures[3]);
                    world.SetFloat("_ShadingType", 4f); world.SetFloat("_IsCustomGearMaterial", 1f); world.SetFloat("_SupportsFPSRendering", 1f);
                    world.SetFloat("_EnableEmissive", textures[2] != null ? 1f : 0f);
                    if (textures[2] != null) { world.SetTexture("_EmissiveMap", textures[2]); world.SetColor("_EmissiveColor", Color.white); world.EnableKeyword("ENABLE_EMISSIVE"); }
                    var firstPerson = new Material(world) { name = "Energy " + set.Name + " (first person)" };
                    loaded.Created.Add(AssetRetention.Keep(firstPerson));
                    firstPerson.EnableKeyword(Player.PlayerBackpackManager.ENABLE_FPS_RENDERING);
                    world.SetPass(0); firstPerson.SetPass(0);
                    body = new SurfaceMaterials(world, firstPerson);
                    loaded.Bodies.Add(set.Name, body);
                }
                materials.Add(part.Name, body);
            }
            else
            {
                var tinted = part.Surface == "glass" ? Glass(_glassShader, key, part) : Reticle(_reticleShader, key, part);
                loaded.Created.Add(AssetRetention.Keep(tinted));
                if (part.Surface == "reticle")
                {
                    // The native FPS shader takes its tint from vertex colors, not a _Color property.
                    var mesh = surface.GetComponent<MeshFilter>().sharedMesh;
                    var c = part.Color;
                    var colors = new Color[mesh.vertexCount];
                    Array.Fill(colors, new Color(c.R, c.G, c.B, 1f));
                    mesh.colors = colors;
                }
                var firstPerson = new Material(tinted) { name = tinted.name + " (first person)" };
                loaded.Created.Add(AssetRetention.Keep(firstPerson));
                firstPerson.EnableKeyword(Player.PlayerBackpackManager.ENABLE_FPS_RENDERING);
                tinted.SetPass(0); firstPerson.SetPass(0);
                materials.Add(part.Name, new SurfaceMaterials(tinted, firstPerson));
            }
        }
        // Body/glass arrive without a CPU copy. Release the reticle's copy after applying its vertex tint.
        foreach (var mesh in prefab.GetComponentsInChildren<MeshFilter>(true))
            if (mesh.sharedMesh.isReadable) mesh.sharedMesh.UploadMeshData(true);
        Models.Add((digest, key), new PreparedModel { Digest = digest, Prefab = prefab, Description = description, Materials = materials });
    }

    private static void UnloadIdle(float now)
    {
        if (now < _nextIdleCheck || Bundles.Count == 0) return;
        _nextIdleCheck = now + 1f;
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
        foreach (var mount in Animations) if (mount.Assets.Digest == digest) return true;
        foreach (var pending in Pending)
            if (pending.Holder != null && pending.Holder.CategoryData != null &&
                EnergyGears.ForCategory(pending.Holder.CategoryData.persistentID) is { } gear &&
                EnergyGears.ModelHashes[gear.ModelPath] == digest) return true;
        return false;
    }

    private static void Unload(string digest)
    {
        var loaded = Bundles[digest];
        Bundles.Remove(digest);
        foreach (var gear in EnergyGears.All)
            if (Prepared[gear.CategoryId]?.Digest == digest) Prepared[gear.CategoryId] = null;
        var stale = new List<(string, string)>();
        foreach (var model in Models.Keys) if (model.Digest == digest) stale.Add(model);
        foreach (var model in stale) Models.Remove(model);
        foreach (var material in loaded.Created) Object.Destroy(material);
        loaded.Bundle.Unload(true);
    }

    private static Material Glass(Shader shader, string key, PartPresentation part)
    {
        var c = part.Color;
        var glass = new Material(shader) { name = "Energy glass " + key + " " + part.Name, color = new Color(c.R, c.G, c.B, c.A), renderQueue = 3000 };
        glass.SetFloat("_SupportsFPSRendering", 1f);
        glass.SetFloat("_Smoothness", .85f); glass.SetFloat("_Metallic", 0f);
        return glass;
    }

    // Reticles are lit marks drawn after the glass; only the first-person view shows them.
    private static Material Reticle(Shader unlit, string key, PartPresentation part)
    {
        var reticle = new Material(unlit) { name = "Energy reticle " + key + " " + part.Name, renderQueue = 3001 };
        reticle.SetFloat("_SupportsFPSRendering", 1f);
        reticle.SetFloat("_IntensityBoost", 1f);
        return reticle;
    }

    /// <summary>Equip/assembly takes a cached mount or queues preparation. It never creates a gun.</summary>
    internal static bool Ensure(GearPartHolder holder)
    {
        if (holder == null || holder.CategoryData == null) return false;
        var mode = EnergyGears.ModeForCategory(holder.CategoryData.persistentID);
        if (mode == EnergyMode.Off) return false;
        if (Mounts.TryGetValue(holder.Pointer, out var mounted))
        {
            if (mounted.Model != null && mounted.RightProxy != null && mounted.LeftProxy != null)
            {
                RefreshMount(mounted);
                return true;
            }
            PruneAnimations();
        }
        QueueMount(holder);
        return false;
    }

    private static PendingMount QueueMount(GearPartHolder holder)
    {
        var pointer = holder.Pointer;
        foreach (var pending in Pending) if (pending.Pointer == pointer) return pending;
        var queued = new PendingMount { Holder = holder, Pointer = pointer };
        Pending.Add(queued);
        return queued;
    }

    private static bool CreateMount(GearPartHolder holder, EnergyGear gear, PreparedModel assets, bool magazineVisible)
    {
        var mode = gear.Mode; var prefab = assets.Prefab; var materials = assets.Materials;
        var bindings = CaptureBindings(holder, null);
        var right = bindings.Right; var left = bindings.Left ?? bindings.WeaponLeft;
        var muzzle = bindings.Muzzle; var sightLook = bindings.Sight; var nativeMagazine = holder.m_aMag;
        if (right == null || left == null || muzzle == null || sightLook == null)
            throw new InvalidDataException("Native hand/muzzle/sight alignment is missing: " + mode);
        var rightPosition = right.position; var rightRotation = right.rotation;
        var layer = holder.gameObject.layer;
        if (holder.ReceiverPart != null)
        {
            var sourceRenderer = holder.ReceiverPart.GetComponentInChildren<MeshRenderer>(true);
            if (sourceRenderer != null) layer = sourceRenderer.gameObject.layer;
        }
        var donorRenderers = new List<RendererSnapshot>();
        var donorAnimators = new List<AnimatorSnapshot>();
        AnimationMount? mount = null;
        Transform? rightProxy = null, leftProxy = null;
        var donorVisibilityChanged = false;
        var instance = Object.Instantiate(prefab, holder.transform);
        try
        {
            instance.name = ModelName; var root = instance.transform;
            // Scale the model and all sockets in both views before native grip, bore and sight alignment.
            root.localScale = Vector3.one * ModelScale;
            root.rotation = Quaternion.LookRotation(muzzle.forward, holder.transform.up);
            var grip = ModelSockets.Require(root, "RightHand"); root.position += rightPosition - grip.position;
            // Native hands sit beside the bore, and native aim only corrects height. Align the authored bore
            // laterally and the sight vertically to the donor; move the hand down with the model.
            var side = root.right; var up = root.up;
            var lateral = side * Vector3.Dot(muzzle.position - ModelSockets.Require(root, "Muzzle").position, side);
            var drop = up * Vector3.Dot(sightLook.position - ModelSockets.Require(root, "SightLook").position, up);
            root.position += lateral + drop;
            grip.position = rightPosition + drop; grip.rotation = rightRotation;
            foreach (var renderer in instance.GetComponentsInChildren<MeshRenderer>(true))
                if (!materials.ContainsKey(renderer.name)) throw new InvalidDataException($"Unknown model surface: {mode}/{renderer.name}");
            rightProxy = CreateHandProxy(holder.transform, ModelName + ".RightHandIK");
            leftProxy = CreateHandProxy(holder.transform, ModelName + ".LeftHandIK");
            mount = new AnimationMount
            {
                Mode = mode, Assets = assets, Holder = holder, Model = root, ModelObject = instance, DonorGrip = right,
                Owner = bindings.Weapon, HolderPointer = holder.Pointer, HolderTransform = holder.transform, WorldLayer = layer,
                DonorLeft = left, RightProxy = rightProxy, LeftProxy = leftProxy, NativeBindings = bindings,
                Muzzle = ModelSockets.Require(root, "Muzzle"), Sight = ModelSockets.Require(root, "SightLook"),
                GripRest = Frame(holder.transform, right), ModelRest = Frame(holder.transform, root),
                LeftRest = Frame(holder.transform, left), RightAuthoredRest = Frame(holder.transform, grip),
                LeftAuthoredRest = Frame(holder.transform, ModelSockets.Require(root, "LeftHand")),
                DonorMagazine = nativeMagazine, Magazine = root.Find("Magazine"), MagazineVisible = magazineVisible
            };
            mount.LeftTargetRest = AnimationPose.LimitHandReach(mount.ModelRest, mount.LeftRest, mount.LeftAuthoredRest, EnergyItemFps.HoldOffset.z);
            if (mount.Magazine != null)
            {
                if (nativeMagazine == null) throw new InvalidDataException("Native magazine animation alignment missing: " + mode);
                mount.DonorMagazineRest = Frame(holder.transform, nativeMagazine);
                mount.MagazineRest = Frame(holder.transform, mount.Magazine);
                mount.MagazineObject = mount.Magazine.gameObject;
                mount.MagazineObject.SetActive(magazineVisible);
            }
            if (Animations.Count >= 128) throw new InvalidOperationException("Energy animation instance budget exhausted.");
            // Snapshot every original flag before writes. Preflight failures leave donor renderers untouched.
            foreach (var renderer in holder.GetComponentsInChildren<Renderer>(true))
                if (!renderer.transform.IsChildOf(root))
                    donorRenderers.Add(new RendererSnapshot(renderer, renderer.enabled, renderer.forceRenderingOff));
            foreach (var animator in holder.GetComponentsInChildren<Animator>(true))
                if (!animator.transform.IsChildOf(root))
                    donorAnimators.Add(new AnimatorSnapshot(animator, animator.cullingMode));
            RefreshView(mount);
            FollowNativePose(mount);
            BindChecked(mount, bindings); instance.SetActive(true);
            // Native flashlight/animation components retain these renderer references. Hide, never destroy.
            // Include SpriteRenderer so the donor holographic dot cannot cover the authored red dot.
            donorVisibilityChanged = true;
            // Hidden donor meshes must still evaluate the animation that moves the hand/Mag nodes.
            foreach (var original in donorAnimators) original.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (var original in donorRenderers)
            {
                original.Renderer.enabled = false;
                original.Renderer.forceRenderingOff = true;
            }
            Animations.Add(mount);
            Mounts[holder.Pointer] = mount;
            return true;
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            if (mount != null) { Animations.Remove(mount); Mounts.Remove(holder.Pointer); }
            if (bindings.WritesAttempted) RestoreBindings(holder, bindings, failures);
            if (donorVisibilityChanged) foreach (var original in donorRenderers)
            {
                if (original.Renderer == null) continue;
                AttemptRollback(() => original.Renderer.enabled = original.Enabled, failures);
                AttemptRollback(() => original.Renderer.forceRenderingOff = original.ForceRenderingOff, failures);
            }
            if (donorVisibilityChanged) foreach (var original in donorAnimators)
            {
                if (original.Animator != null)
                    AttemptRollback(() => original.Animator.cullingMode = original.CullingMode, failures);
            }
            DestroyHandProxy(rightProxy, failures);
            DestroyHandProxy(leftProxy, failures);
            // Destroy is deferred in Unity: remove the failed instance from lookup immediately.
            if (instance != null)
            {
                AttemptRollback(() => instance.SetActive(false), failures);
                AttemptRollback(() => instance.transform.SetParent(null, true), failures);
                AttemptRollback(() => Object.Destroy(instance), failures);
            }
            if (failures.Count != 0)
            {
                failures.Insert(0, error);
                throw new AggregateException("Energy model mount failed and rollback was incomplete.", failures);
            }
            throw;
        }
    }

    private static void RefreshView(AnimationMount mount)
    {
        var layer = mount.Owner != null && mount.Owner.IsFirstPerson ? FirstPersonLayer : mount.WorldLayer;
        if (mount.ViewLayer == layer) return;
        SetLayer(mount.Model, layer);
        ApplySurfaces(mount.Assets.Description, mount.Assets.Materials, mount.Model, layer);
        mount.ViewLayer = layer;
    }

    private static void RefreshMount(AnimationMount mount)
    {
        var holder = mount.Holder;
        // Assembly/wield/reload are lifecycle boundaries. Resolve a new owner here, never in the frame loop.
        var owner = holder.GetComponentInParent<ItemEquippable>();
        if (mount.Owner != owner) mount.RegisteredFirstPerson = false;
        mount.Owner = owner;
        var right = holder.m_aRightHand; var left = holder.m_aLeftHand; var magazine = holder.m_aMag;
        // Our public IK outputs must never become animation inputs or be sampled as a new rest pose.
        if (IsProxy(right, mount.RightProxy)) right = mount.DonorGrip;
        if (IsProxy(left, mount.LeftProxy)) left = mount.DonorLeft;
        // Match the game's LeftHandGripTrans fallback when a donor has no holder left-hand alignment.
        if (left is null)
        {
            left = owner != null ? owner.m_leftHandGripAlign : null;
            if (IsProxy(left, mount.LeftProxy)) left = mount.DonorLeft;
        }
        var donorChanged = right != mount.DonorGrip || left != mount.DonorLeft || magazine != mount.DonorMagazine;
        if (right == null || left == null || mount.RightProxy == null || mount.LeftProxy == null ||
            (mount.Magazine != null && magazine == null))
            throw new InvalidDataException("Native hand/Mag animation alignment missing: " + mount.Mode);
        if (right != mount.DonorGrip)
        {
            var rest = Frame(mount.HolderTransform, right);
            mount.ModelRest = AnimationPose.Follow(mount.GripRest, rest, mount.ModelRest);
            mount.RightAuthoredRest = AnimationPose.Follow(mount.GripRest, rest, mount.RightAuthoredRest);
            // Both authored sockets belong to the model that moved with the new right-hand rest frame.
            mount.LeftAuthoredRest = AnimationPose.Follow(mount.GripRest, rest, mount.LeftAuthoredRest);
            mount.GripRest = rest;
        }
        if (left != mount.DonorLeft) mount.LeftRest = Frame(mount.HolderTransform, left);
        if (magazine != mount.DonorMagazine && mount.Magazine != null && magazine != null)
        {
            var rest = Frame(mount.HolderTransform, magazine);
            mount.MagazineRest = AnimationPose.Follow(mount.DonorMagazineRest, rest, mount.MagazineRest);
            mount.DonorMagazineRest = rest;
        }
        mount.DonorGrip = right; mount.DonorLeft = left; mount.DonorMagazine = magazine;
        if (donorChanged)
        {
            mount.LeftTargetRest = AnimationPose.LimitHandReach(mount.ModelRest, mount.LeftRest, mount.LeftAuthoredRest, EnergyItemFps.HoldOffset.z);
            // Reassembly can replace live nodes and add fresh donor renderers/animators under the same holder.
            foreach (var animator in holder.GetComponentsInChildren<Animator>(true))
                if (!animator.transform.IsChildOf(mount.Model)) animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (var renderer in holder.GetComponentsInChildren<Renderer>(true))
                if (!renderer.transform.IsChildOf(mount.Model))
                { renderer.enabled = false; renderer.forceRenderingOff = true; }
            FollowNativePose(mount);
        }
        RefreshView(mount);
        var weapon = mount.Owner;
        var syncIK = WieldedSyncIK(weapon);
        // A changed donor requires a fresh native rollback snapshot even if the proxy outputs survived assembly.
        if (!donorChanged && holder.m_aRightHand == mount.RightProxy && holder.m_aLeftHand == mount.LeftProxy &&
            holder.m_aMuzzle == mount.Muzzle && holder.m_aSightLook == mount.Sight &&
            (weapon == null || (weapon.m_rightHandGripAlign == mount.RightProxy && weapon.m_leftHandGripAlign == mount.LeftProxy &&
                weapon.MuzzleAlign == mount.Muzzle && weapon.SightLookAlign == mount.Sight &&
                (!weapon.IsFirstPerson || mount.RegisteredFirstPerson))) &&
            (syncIK == null || IsProxy(syncIK.m_leftArmSolver.target, mount.LeftProxy))) return;
        Bind(mount);
    }

    // First person uses the FPS gear materials and casts no shadows; glass and reticles never do. Reticles are marks
    // projected along the moving optic's axis; they start hidden and TickSight shows them only while aiming through the glass.
    private static void ApplySurfaces(WeaponPresentation description, Dictionary<string, SurfaceMaterials> materials, Transform model, int layer)
    {
        var firstPerson = layer == FirstPersonLayer;
        foreach (var part in description.Parts)
        {
            var surface = model.Find(part.Name);
            if (surface == null) continue;
            var renderer = surface.GetComponent<MeshRenderer>();
            var pair = materials[part.Name];
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
        if (fp == null || !WeaponBindings.TryGet(fp.WieldedItem, out var binding) || binding.Reticles.Length == 0) return;
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

    private static void Bind(AnimationMount mount)
    {
        var holder = mount.Holder;
        var snapshot = CaptureBindings(holder, mount);
        try { BindChecked(mount, snapshot); mount.NativeBindings = snapshot; }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            if (snapshot.WritesAttempted) RestoreBindings(holder, snapshot, failures);
            if (failures.Count != 0)
            {
                failures.Insert(0, error);
                throw new AggregateException("Energy model binding failed and rollback was incomplete.", failures);
            }
            throw;
        }
    }

    private static void BindChecked(AnimationMount mount, BindingSnapshot snapshot)
    {
        var holder = mount.Holder;
        var muzzle = mount.Muzzle; var sight = mount.Sight;
        var weapon = mount.Owner;
        if (weapon != null && (snapshot.Weapon == null || weapon.Pointer != snapshot.Weapon.Pointer))
            throw new InvalidOperationException("Weapon changed during model binding.");
        // Resolve/validate first. Only a setter attempt can require native rollback, and
        // the flag precedes that call because a native setter may commit before throwing.
        snapshot.WritesAttempted = true;
        snapshot.SyncProxy = mount.LeftProxy;
        // The left proxy is a donor wrist pose with only its longitudinal reach taken from the authored foregrip.
        holder.m_aRightHand = mount.RightProxy; holder.m_aLeftHand = mount.LeftProxy;
        holder.m_aMuzzle = muzzle; holder.m_aSightLook = sight;
        if (weapon == null) return;
        weapon.m_rightHandGripAlign = mount.RightProxy;
        weapon.m_leftHandGripAlign = mount.LeftProxy;
        weapon.MuzzleAlign = holder.m_aMuzzle;
        // Native GetSightPosAdjustment must see the actual optic, not the hidden donor sight.
        weapon.SightLookAlign = holder.m_aSightLook;
        // SyncIK caches its target during native wield; an asynchronously mounted model must replace that target too.
        if (snapshot.SyncIK != null) snapshot.SyncIK.SetLeftArmTarget(mount.LeftProxy);
        WeaponBindings.Register(weapon, snapshot.Registration ?? throw new InvalidOperationException("Weapon changed during model binding."));
        mount.RegisteredFirstPerson = weapon.IsFirstPerson;
    }

    internal static void BindWeapon(ItemEquippable weapon)
    {
        if (weapon?.GearPartHolder == null) return;
        Ensure(weapon.GearPartHolder);
    }

    internal static void TickPending()
    {
        // Restore dead mounts' native targets before a replacement mount samples its donor rest poses.
        PruneAnimations();
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
            if (Prepared[gear.CategoryId] is not { } assets)
            {
                if (!Request(gear)) Pending.RemoveAt(i);
                continue;
            }
            Pending.RemoveAt(i);
            if (!Mounts.ContainsKey(holder.Pointer)) CreateMount(holder, gear, assets, pending.MagazineVisible);
            return;
        }
    }

    private static void PruneAnimations()
    {
        for (var i = Animations.Count - 1; i >= 0; i--)
            if (Animations[i].Holder == null || Animations[i].Model == null ||
                Animations[i].RightProxy == null || Animations[i].LeftProxy == null)
            {
                var mount = Animations[i];
                var failures = new List<Exception>();
                if (mount.Holder != null)
                {
                    var snapshot = CaptureBindings(mount.Holder, mount);
                    // Drop this model's registration, including when a proxy died before the model did.
                    if (snapshot.Weapon != null)
                        snapshot.Registration = new WeaponBindings.Snapshot { WeaponPointer = snapshot.Weapon.Pointer };
                    RestoreBindings(mount.Holder, snapshot, failures);
                }
                else RestoreSyncTarget(mount.NativeBindings, failures);
                DestroyHandProxy(mount.RightProxy, failures);
                DestroyHandProxy(mount.LeftProxy, failures);
                if (mount.ModelObject != null)
                {
                    AttemptRollback(() => mount.ModelObject.SetActive(false), failures);
                    AttemptRollback(() => mount.Model.SetParent(null, true), failures);
                    AttemptRollback(() => Object.Destroy(mount.ModelObject), failures);
                }
                Mounts.Remove(mount.HolderPointer);
                Animations.RemoveAt(i);
                if (failures.Count != 0) throw new AggregateException("Energy hand proxy cleanup was incomplete.", failures);
            }
    }

    // First-person pose updates belong to the native holder prefix below, not this unordered controller loop.
    internal static void TickAnimations()
    {
        foreach (var mount in Animations)
        {
            // A replaced socket can be destroyed before OnAllPartsSpawned refreshes this mount.
            if (!mount.ModelObject.activeInHierarchy || mount.DonorGrip == null || mount.DonorLeft == null ||
                (mount.Owner != null && mount.Owner.IsFirstPerson)) continue;
            FollowNativePose(mount);
        }
    }

    // The native LateUpdate inlines UpdateIKGrips. A prefix on UpdateIKGrips alone never runs in this build.
    // Called from FirstPersonItemHolder.LateUpdate's prefix, before either hand getter and IK target write.
    internal static void UpdateFirstPersonPose(FirstPersonItemHolder fp)
    {
        var weapon = fp.WieldedItem;
        var holder = weapon != null ? weapon.GearPartHolder : null;
        if (holder == null || !Mounts.TryGetValue(holder.Pointer, out var mount) ||
            mount.Model == null || !mount.ModelObject.activeInHierarchy) return;
        FollowNativePose(mount);
    }

    private static void FollowNativePose(AnimationMount mount)
    {
        var holder = mount.HolderTransform;
        var grip = Frame(holder, mount.DonorGrip);
        var left = Frame(holder, mount.DonorLeft);
        // Read every animation input before moving any output; proxies never feed back into the donor poses.
        var magazine = mount.DonorMagazine != null ? Frame(holder, mount.DonorMagazine) : default;
        ApplyFrame(holder, mount.Model, AnimationPose.Follow(mount.GripRest, grip, mount.ModelRest));
        if (mount.Magazine != null && mount.DonorMagazine != null)
            ApplyFrame(holder, mount.Magazine, AnimationPose.Follow(mount.DonorMagazineRest, magazine, mount.MagazineRest));
        // The reach-limited wrist target stays attached to the gun; native wrist rotation must not orbit its offset.
        // Keep the left hand's independent movement and rotation for the donor's reload and grip animations.
        ApplyFrame(holder, mount.RightProxy, AnimationPose.Follow(mount.GripRest, grip, mount.RightAuthoredRest));
        ApplyFrame(holder, mount.LeftProxy, AnimationPose.FollowHand(mount.GripRest, grip,
            mount.LeftRest, left, mount.LeftTargetRest));
    }

    internal static void SetMagazineVisible(ItemEquippable weapon, bool visible)
    {
        if (weapon?.GearPartHolder == null) return;
        if (!Mounts.TryGetValue(weapon.GearPartHolder.Pointer, out var mount))
        {
            // A native reload action can precede the queued mount. Preserve its actual visibility
            // value, so the first model frame does not reintroduce a magazine already removed.
            if (weapon.GearCategoryData != null && EnergyGears.ModeForCategory(weapon.GearCategoryData.persistentID) != EnergyMode.Off)
                QueueMount(weapon.GearPartHolder).MagazineVisible = visible;
            return;
        }
        if (mount.MagazineObject == null || mount.MagazineVisible == visible) return;
        mount.MagazineVisible = visible;
        mount.MagazineObject.SetActive(visible);
    }

    private static void SetLayer(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (var i = 0; i < root.childCount; i++) SetLayer(root.GetChild(i), layer);
    }
}

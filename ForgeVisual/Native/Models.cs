using System;
using System.Collections.Generic;
using Player;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>Registered models by name (<c>Pack:prefab</c>), dressing the game's own objects in them, and putting rigged models on
/// players: worn on the first-person body, following the third-person pose, holding a copy of the held item, and clips played on the
/// player's own body.</summary>
public static class Models
{
    private static readonly NameTable<ModelHandle> All = new();

    /// <summary>The model <paramref name="name"/>, or null (logged once) when the name is bad, its pack is not registered or the pack
    /// has no such model. Resolve once and keep the handle.</summary>
    public static ModelHandle? Get(string name)
    {
        if (All.TryGet(name, out var model)) return model;
        Plugin.Logger.ErrorOnce("ForgeVisual has no model " + name + ": " + Missing.Why(name, Bundles.Known, "model") + ".");
        return null;
    }

    /// <summary>Every registered model name.</summary>
    public static IReadOnlyList<string> Names => All.Names;

    internal static void Index(BundleHandle bundle)
    {
        foreach (var model in bundle.AllModels)
            if (All.Add(model.Name, model) is { } why) Plugin.Logger.ErrorOnce("ForgeVisual model " + model.Name + " not added: " + why + ".");
    }

    /// <summary>
    /// Dresses the game object <paramref name="native"/> (a gear item, a sentry, a thrown item) in <paramref name="look"/>: the
    /// object's own mesh renderers are hidden (its effects, lights and colliders stay), a copy of the model is put under it on its layer,
    /// and with <see cref="DressOptions.FitLength"/> the model takes the native meshes' length along the object's forward axis and their
    /// place. With <see cref="DressOptions.Gun"/> the model instead goes where the game's hands are (see <see cref="GunNodes"/>) and its
    /// <c>Magazine</c> is hung on the game's magazine node, so the game's reload animation carries it. Null (logged once) when the
    /// model cannot be shown yet or a gun model lacks what the game's nodes need. <see cref="Dressed.Undo"/> puts the object back.
    /// </summary>
    public static Dressed? Dress(GameObject native, ModelHandle look, DressOptions options)
    {
        if (native == null || native.WasCollected) throw new ArgumentNullException(nameof(native));
        if (look == null) throw new ArgumentNullException(nameof(look));
        options ??= DressOptions.Default;
        var t = native.transform;
        Bounds nativeBounds = default;
        var measured = options.Gun == null && options.FitLength && NativeBounds(t, null, out nativeBounds);
        if (options.Gun is { } wanted && !CanSeatGun(look, wanted)) return null;
        var instance = look.Spawn(t.position, t.rotation * options.Rotation, t);
        if (instance == null) return null;
        instance.name = options.Name ?? "ForgeVisual." + look.Name;
        var scale = 1f;
        GunSockets? sockets = null;
        if (options.Gun is { } gun)
        {
            sockets = GunSockets.Find(instance.transform);
            if (!SeatGun(instance.transform, t, gun, sockets!, look.Name, out scale))
            {
                Object.Destroy(instance);
                return null;
            }
        }
        var hidden = new List<Renderer>();
        if (options.HideNative) HideMeshes(t, instance.transform, null, hidden);
        if (measured) scale = FitTo(instance, t, nativeBounds, options.MaxScale);
        if (options.Layer) SetLayer(instance.transform, native.layer);
        Transform? magazine = null;
        List<(Animator Animator, AnimatorCullingMode Mode)>? animators = null;
        if (options.Gun is { } nodes)
        {
            magazine = HangMagazine(sockets!.Magazine, nodes, look.Name);
            animators = KeepAnimating(t, instance.transform, magazine);
        }
        return new Dressed(native, instance, hidden, scale, magazine, animators);
    }

    /// <summary>
    /// Wears <paramref name="look"/> on the local player's first-person body (<paramref name="agent"/>'s): see <see cref="FirstPersonWear"/>.
    /// The model is spawned, matched to the game's skeleton by <see cref="FirstPersonWearOptions.Bones"/>, cut to its worn bones and drawn
    /// once off screen; nothing on the game's body changes until <see cref="FirstPersonWear.Activate"/>. It costs a fraction of a second:
    /// build it while the player cannot act (an elevator ride, a countdown). Null (logged once) when the model, its mesh, a bind pose or
    /// the first-person body is missing, or the three bones that line the skeletons up are not on the game's body.
    /// </summary>
    public static FirstPersonWear? WearFirstPerson(PlayerAgent agent, ModelHandle look, FirstPersonWearOptions options)
    {
        if (agent == null || agent.WasCollected) throw new ArgumentNullException(nameof(agent));
        if (look == null) throw new ArgumentNullException(nameof(look));
        if (options == null) throw new ArgumentNullException(nameof(options));
        return FirstPersonWear.Build(agent, look, options);
    }

    /// <summary>The model side of a pose copy: the bones of the model under <paramref name="root"/> skinned by <paramref name="skin"/>,
    /// in <paramref name="links"/> order (<see cref="PoseLink"/>), read from its bind poses. Read once per model instance. Null (logged
    /// once, with <paramref name="label"/>) when the links are wrong or a bone or its bind pose is missing.</summary>
    public static PoseTarget? ReadPoseTarget(Transform root, SkinnedMeshRenderer skin, IReadOnlyList<PoseLink> links, string label)
    {
        if (root == null || root.WasCollected) throw new ArgumentNullException(nameof(root));
        if (skin == null || skin.WasCollected) throw new ArgumentNullException(nameof(skin));
        return PoseTarget.Read(root, skin, links, label);
    }

    /// <summary>Copies the humanoid pose of the body animated by <paramref name="body"/> (for a player, <c>PlayerAgent.AnimatorBody</c>)
    /// onto <paramref name="target"/>: see <see cref="PoseCopy"/>. Bind once per body; call <see cref="PoseCopy.Apply"/> every frame after
    /// the game animated the body (after its limb IK) and <see cref="PoseCopy.Release"/> when done. Each of <paramref name="reach"/>
    /// (may be empty) bends a limb so its end lands where the body's does (<see cref="PoseReach"/>). Null (logged once) when the body is
    /// not a humanoid, lacks a linked bone or its bind pose, or a reach is wrong.</summary>
    public static PoseCopy? CopyPose(Animator body, PoseTarget target, IReadOnlyList<PoseReach> reach, string label)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (reach == null) throw new ArgumentNullException(nameof(reach));
        return PoseCopy.Bind(body, target, reach, label);
    }

    /// <summary>A copy of whatever <paramref name="agent"/> holds, for another model's hand, drawn on <paramref name="layer"/>: see
    /// <see cref="HeldMirror"/>.</summary>
    public static HeldMirror MirrorHeld(PlayerAgent agent, int layer, string label)
    {
        if (agent == null || agent.WasCollected) throw new ArgumentNullException(nameof(agent));
        return new HeldMirror(agent, layer, label);
    }

    /// <summary>A rig for body clips (<see cref="BodyClipRig"/>): <paramref name="animated"/> (under <paramref name="root"/>) is a hidden
    /// model with the game's player bone names and the clips; <paramref name="rootBone"/> is its hips. Null (logged once) when it has no
    /// such bone.</summary>
    public static BodyClipRig? BodyClipRig(Transform root, GameObject animated, string rootBone, string label)
    {
        if (root == null || root.WasCollected) throw new ArgumentNullException(nameof(root));
        if (animated == null || animated.WasCollected) throw new ArgumentNullException(nameof(animated));
        return ForgeVisual.BodyClipRig.Create(root, animated, rootBone, label);
    }

    /// <summary>Plays <paramref name="rig"/>'s clips on <paramref name="agent"/>'s own body: see <see cref="BodyClip"/>. Bind once per
    /// player. Null (logged once) when the body has no hips.</summary>
    public static BodyClip? BindBodyClip(PlayerAgent agent, BodyClipRig rig, BodyClipOptions options)
    {
        if (agent == null || agent.WasCollected) throw new ArgumentNullException(nameof(agent));
        if (rig == null) throw new ArgumentNullException(nameof(rig));
        return BodyClip.Bind(agent, rig, options ?? new BodyClipOptions());
    }

    /// <summary>Before anything is spawned: the model (prefab) has the two hand sockets and the game gun has both hand nodes. False
    /// (logged once) otherwise; a gun not assembled yet is tried again by the caller.</summary>
    private static bool CanSeatGun(ModelHandle look, GunNodes nodes)
    {
        var root = look.Prefab.transform;
        if (Find(root, GunNodes.RightHandSocket) == null || Find(root, GunNodes.LeftHandSocket) == null)
        {
            Plugin.Logger.ErrorOnce("ForgeVisual cannot seat the gun model " + look.Name + ": it has no "
                + GunNodes.RightHandSocket + " and " + GunNodes.LeftHandSocket + " sockets.");
            return false;
        }
        if (nodes.RightHand == null || nodes.LeftHand == null)
        {
            Plugin.Logger.ErrorOnce("ForgeVisual cannot seat the gun model " + look.Name + ": the game gun has no "
                + (nodes.RightHand == null ? "right" : "left") + " hand node (it is not assembled yet, or the gun has none).");
            return false;
        }
        return true;
    }

    /// <summary>The scale and place of a gun model (<paramref name="look"/>, a child of <paramref name="native"/> already turned the way it
    /// should face) on the game gun's nodes: <see cref="GunFit"/>. The model's sockets are measured at its own size with the model at the
    /// gun's origin. False (logged once) when a position is not a number.</summary>
    public static bool SeatGun(Transform look, Transform native, GunNodes nodes, GunSockets sockets, string name, out float scale)
    {
        scale = 1f;
        var lossy = native.lossyScale;
        var unit = new Vector3(SafeInverse(lossy.x), SafeInverse(lossy.y), SafeInverse(lossy.z));
        look.localScale = unit;
        look.localPosition = Vector3.zero;
        System.Numerics.Vector3 Local(Transform node) { var p = native.InverseTransformPoint(node.position); return new System.Numerics.Vector3(p.x, p.y, p.z); }
        var bore = nodes.Muzzle != null && sockets.Muzzle != null;
        var sight = nodes.Sight != null && sockets.Sight != null;
        var fit = GunFit.Solve(Local(nodes.RightHand!), Local(nodes.LeftHand!), bore ? Local(nodes.Muzzle!) : null,
            Local(sockets.RightHand), Local(sockets.LeftHand), bore ? Local(sockets.Muzzle!) : null, nodes.MinScale, nodes.MaxScale,
            sight ? Local(nodes.Sight!) : null, sight ? Local(sockets.Sight!) : null);
        if (fit == null)
        {
            Plugin.Logger.ErrorOnce("ForgeVisual cannot seat the gun model " + name + ": a hand node or socket has no position, or the scale limits are wrong.");
            return false;
        }
        var (s, move) = fit.Value;
        look.localScale = unit * s;
        look.localPosition = new Vector3(move.X, move.Y, move.Z);
        scale = s;
        return true;
    }

    /// <summary>Hangs the model's <paramref name="magazine"/> (its <see cref="GunNodes.MagazineSocket"/>) on the game's magazine node,
    /// keeping where it is now, so the game's reload animation (which moves that node) moves it. Null when the model has none; logged
    /// once when it has one and the gun has no magazine node (the magazine then stays on the gun). Hanging it again on a replaced
    /// magazine node keeps where it is then; it dies with the node it hangs on, so call it before the old node is destroyed.</summary>
    public static Transform? HangMagazine(Transform? magazine, GunNodes nodes, string name)
    {
        if (magazine == null) return null;
        if (nodes.Magazine == null)
        {
            Plugin.Logger.ErrorOnce("ForgeVisual: the game gun has no magazine node, so the " + name + " magazine stays fixed on the gun during a reload.");
            return null;
        }
        magazine.SetParent(nodes.Magazine, true);
        return magazine;
    }

    /// <summary>The game moves its hand and magazine nodes with animators on the hidden gun; an animator whose meshes are all hidden would
    /// stop updating them, so each runs always. Returns what to restore.</summary>
    private static List<(Animator, AnimatorCullingMode)> KeepAnimating(Transform native, Transform look, Transform? magazine)
    {
        var kept = new List<(Animator, AnimatorCullingMode)>();
        foreach (var animator in native.GetComponentsInChildren<Animator>(true))
        {
            if (animator.transform.IsChildOf(look) || (magazine != null && animator.transform.IsChildOf(magazine))) continue;
            kept.Add((animator, animator.cullingMode));
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
        return kept;
    }

    /// <summary>The bounds of the mesh renderers under <paramref name="root"/> in its own frame, skipping <paramref name="skip"/> and
    /// everything under it. False when there is no mesh to measure (an item still being assembled).</summary>
    public static bool NativeBounds(Transform root, Transform? skip, out Bounds bounds)
    {
        bounds = default;
        var meshes = 0;
        var toRoot = root.worldToLocalMatrix;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (skip != null && renderer.transform.IsChildOf(skip)) continue;
            if (renderer.TryCast<MeshRenderer>() != null)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                Encapsulate(ref bounds, ref meshes, toRoot * renderer.transform.localToWorldMatrix, filter.sharedMesh.bounds);
            }
            else if (renderer.TryCast<SkinnedMeshRenderer>() != null) Encapsulate(ref bounds, ref meshes, toRoot, renderer.bounds);
        }
        return meshes > 0;
    }

    /// <summary>The descendant of <paramref name="root"/> (or itself) named <paramref name="name"/>, depth first, or null: a model's
    /// socket (a muzzle, a hand anchor). Search once and keep the transform.</summary>
    public static Transform? Find(Transform root, string name)
    {
        if (root.name == name) return root;
        for (var i = 0; i < root.childCount; i++)
            if (Find(root.GetChild(i), name) is { } found) return found;
        return null;
    }

    /// <summary>Puts <paramref name="root"/> and everything under it on <paramref name="layer"/> (first or third person views draw by layer).</summary>
    public static void SetLayer(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (var i = 0; i < root.childCount; i++) SetLayer(root.GetChild(i), layer);
    }

    /// <summary>Hides the mesh renderers under <paramref name="root"/> except those under <paramref name="skip"/> and
    /// <paramref name="skip2"/>. A renderer is turned off and forced off: the game switches renderers back on (a gun's magazine at
    /// the end of a reload), and a forced-off one stays hidden. One already forced off is not hidden again (nor counted as late).</summary>
    internal static void HideMeshes(Transform root, Transform? skip, Transform? skip2, List<Renderer> hidden)
    {
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer.enabled || renderer.forceRenderingOff) continue;
            if ((skip != null && renderer.transform.IsChildOf(skip)) || (skip2 != null && renderer.transform.IsChildOf(skip2))) continue;
            if (renderer.TryCast<MeshRenderer>() == null && renderer.TryCast<SkinnedMeshRenderer>() == null) continue;
            renderer.enabled = false;
            renderer.forceRenderingOff = true;
            hidden.Add(renderer);
        }
    }

    /// <summary>Scales the look uniformly in the world to the native meshes' length when that is longer (never below the model's
    /// own size, at most <paramref name="maxScale"/>), cancelling a parent's uneven axis scale, and centres it on them across and up,
    /// starting where they start along the forward axis. Returns the scale used.</summary>
    private static float FitTo(GameObject look, Transform native, Bounds nativeBounds, float maxScale)
    {
        if (nativeBounds.size.z <= 1e-4f) return 1f;
        var t = look.transform;
        var lossy = native.lossyScale;
        var unit = new Vector3(SafeInverse(lossy.x), SafeInverse(lossy.y), SafeInverse(lossy.z));
        if (!NativeBounds(t, null, out var own) || own.size.z <= 1e-4f) return 1f;
        var scale = Mathf.Clamp(Math.Max(nativeBounds.size.z * Math.Abs(lossy.z), own.size.z) / own.size.z, 1f, Math.Max(1f, maxScale));
        t.localScale = unit * scale;
        var centre = native.InverseTransformPoint(t.TransformPoint(own.center));
        var rear = centre.z - own.size.z * unit.z * scale * 0.5f;
        t.localPosition += new Vector3(nativeBounds.center.x - centre.x, nativeBounds.center.y - centre.y, nativeBounds.min.z - rear);
        return scale;
    }

    private static void Encapsulate(ref Bounds into, ref int count, Matrix4x4 matrix, Bounds box)
    {
        var c = box.center;
        var e = box.extents;
        for (var i = 0; i < 8; i++)
        {
            var corner = matrix.MultiplyPoint3x4(new Vector3(c.x + ((i & 1) == 0 ? -e.x : e.x), c.y + ((i & 2) == 0 ? -e.y : e.y), c.z + ((i & 4) == 0 ? -e.z : e.z)));
            if (count == 0 && i == 0) into = new Bounds(corner, Vector3.zero);
            else into.Encapsulate(corner);
        }
        count++;
    }

    internal static float SafeInverse(float v) => Math.Abs(v) < 1e-4f ? 1f : 1f / v;
}

/// <summary>A registered model. <see cref="Prefab"/> is readable as soon as its pack is registered (to check its parts); spawning
/// waits until its materials are ready (the game's gear shader exists once a level is built).</summary>
public sealed class ModelHandle
{
    internal ModelHandle(BundleHandle bundle, string name, GameObject prefab)
    {
        Bundle = bundle;
        Name = name;
        Prefab = prefab;
    }

    public BundleHandle Bundle { get; }

    /// <summary><c>Pack:prefab</c>.</summary>
    public string Name { get; }

    /// <summary>The bundle's prefab. Do not change it; spawn instances.</summary>
    public GameObject Prefab { get; }

    /// <summary>True once instances can be spawned with their materials.</summary>
    public bool Ready => Bundle.Prepare();

    /// <summary>A new instance at the given world pose under <paramref name="parent"/> (or none), or null (logged once) when the
    /// materials cannot be prepared yet.</summary>
    public GameObject? Spawn(Vector3 position, Quaternion rotation, Transform? parent = null)
    {
        if (!Bundle.Prepare()) return null;
        return parent == null ? Object.Instantiate(Prefab, position, rotation) : Object.Instantiate(Prefab, position, rotation, parent);
    }
}

/// <summary>How <see cref="Models.Dress"/> puts a model on a game object.</summary>
public sealed class DressOptions
{
    public static readonly DressOptions Default = new();

    /// <summary>Hide the object's own mesh renderers (default true).</summary>
    public bool HideNative { get; set; } = true;

    /// <summary>Take the native meshes' length along the forward axis and their place (gear in the hands).</summary>
    public bool FitLength { get; set; }

    /// <summary>The largest scale <see cref="FitLength"/> may apply (the model is never shrunk below its own size).</summary>
    public float MaxScale { get; set; } = 2f;

    /// <summary>Put the model on the object's layer (default true).</summary>
    public bool Layer { get; set; } = true;

    /// <summary>The model's rotation relative to the object (for example its front turned to +Z).</summary>
    public Quaternion Rotation { get; set; } = Quaternion.identity;

    /// <summary>The name of the model's instance; default <c>ForgeVisual.Pack:prefab</c>.</summary>
    public string? Name { get; set; }

    /// <summary>Dress a gun: the model takes the place and scale that put its hand sockets on the game gun's hand nodes, and its magazine
    /// follows the game's magazine node (see <see cref="GunNodes"/>). Replaces <see cref="FitLength"/>.</summary>
    public GunNodes? Gun { get; set; }
}

/// <summary>
/// The nodes of a game gun that a gun model follows, read from the gun once it is assembled (the gear part holder's <c>m_aRightHand</c>,
/// <c>m_aLeftHand</c>, <c>m_aMuzzle</c>, <c>m_aMag</c>, or the weapon's own hand grip aligns the game's hands are put on). The model
/// faces the game gun's +Z and is a prefab with these children (any depth): <c>RightHand</c> and <c>LeftHand</c> (the palm points of the
/// pistol grip and the fore grip), optionally <c>muzzle</c> (the bore, so the gun is centred on the game's bore) and <c>Magazine</c> (a
/// separate mesh object, hung on the game's magazine node so a reload animation moves it).
/// </summary>
public sealed class GunNodes
{
    public const string RightHandSocket = "RightHand";
    public const string LeftHandSocket = "LeftHand";
    public const string MuzzleSocket = "muzzle";
    public const string MagazineSocket = "Magazine";
    public const string SightSocket = "SightLook";

    public Transform? RightHand { get; set; }
    public Transform? LeftHand { get; set; }
    public Transform? Muzzle { get; set; }
    public Transform? Magazine { get; set; }

    /// <summary>The game's sight-look node. With it and the model's <see cref="GunSockets.Sight"/> the model's height is the one that
    /// puts its sight on the game's (the game's aim corrects height only); without, the two hands share the height error.</summary>
    public Transform? Sight { get; set; }

    /// <summary>The scale the model may take so its hand points are as far apart as the game's (0.7 to 1.4 by default).</summary>
    public float MinScale { get; set; } = 0.7f;
    public float MaxScale { get; set; } = 1.4f;
}

/// <summary>The sockets of a gun model that <see cref="Models.SeatGun"/> and <see cref="Models.HangMagazine"/> use. <see cref="Find"/> reads
/// them by the <see cref="GunNodes"/> socket names; a model whose parts and sockets share names builds this itself.</summary>
public sealed class GunSockets
{
    public Transform RightHand { get; set; } = null!;
    public Transform LeftHand { get; set; } = null!;
    public Transform? Muzzle { get; set; }
    public Transform? Sight { get; set; }
    public Transform? Magazine { get; set; }

    /// <summary>The sockets of <paramref name="model"/> by the <see cref="GunNodes"/> names (any depth); the hand sockets must exist.</summary>
    public static GunSockets Find(Transform model) => new()
    {
        RightHand = Models.Find(model, GunNodes.RightHandSocket)!,
        LeftHand = Models.Find(model, GunNodes.LeftHandSocket)!,
        Muzzle = Models.Find(model, GunNodes.MuzzleSocket),
        Sight = Models.Find(model, GunNodes.SightSocket),
        Magazine = Models.Find(model, GunNodes.MagazineSocket)
    };
}

/// <summary>A game object wearing a model.</summary>
public sealed class Dressed
{
    private readonly List<Renderer> _hidden;
    private readonly Transform? _magazine;
    private readonly List<(Animator Animator, AnimatorCullingMode Mode)>? _animators;

    internal Dressed(GameObject native, GameObject look, List<Renderer> hidden, float scale, Transform? magazine, List<(Animator, AnimatorCullingMode)>? animators)
    {
        Native = native;
        Look = look;
        _hidden = hidden;
        Scale = scale;
        _magazine = magazine;
        _animators = animators;
    }

    public GameObject Native { get; }
    public GameObject Look { get; }

    /// <summary>The world scale the dressing applied (1 = the model's own size).</summary>
    public float Scale { get; }

    /// <summary>How many of the object's renderers were hidden.</summary>
    public int Hidden => _hidden.Count;

    /// <summary>True while the object and the model both exist.</summary>
    public bool Alive => Native != null && !Native.WasCollected && Look != null && !Look.WasCollected;

    /// <summary>Hides mesh renderers the object gained since it was dressed (parts the game adds late); returns how many.</summary>
    public int HideLate()
    {
        if (!Alive) return 0;
        var before = _hidden.Count;
        Models.HideMeshes(Native.transform, Look.transform, _magazine, _hidden);
        return _hidden.Count - before;
    }

    /// <summary>Removes the model (its magazine too) and shows the object's own meshes again.</summary>
    public void Undo()
    {
        foreach (var renderer in _hidden)
        {
            if (renderer == null || renderer.WasCollected) continue;
            renderer.forceRenderingOff = false;
            renderer.enabled = true;
        }
        if (_animators != null)
            foreach (var (animator, mode) in _animators)
                if (animator != null && !animator.WasCollected) animator.cullingMode = mode;
        if (_magazine != null && !_magazine.WasCollected) Object.Destroy(_magazine.gameObject);
        if (Look != null && !Look.WasCollected) Object.Destroy(Look);
    }
}

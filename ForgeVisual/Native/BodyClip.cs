using System;
using System.Collections.Generic;
using Player;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeVisual;

/// <summary>
/// A hidden copy of the player's skeleton that animation clips are sampled on (<see cref="Models.BodyClipRig"/>): a model with the
/// game's player bone names whose bones a <see cref="BodyClip"/> copies onto a player's own body. <see cref="Root"/> is placed where the
/// clip plays, <see cref="Animated"/> is what the clips are sampled on, <see cref="Bones"/> starts with the root bone (the hips) and
/// holds every bone under it. One rig serves every player; the mod that made it keeps it alive.
/// </summary>
public sealed class BodyClipRig
{
    private BodyClipRig(Transform root, GameObject animated, Transform[] bones)
    {
        Root = root;
        Animated = animated;
        Bones = bones;
    }

    public Transform Root { get; }
    public GameObject Animated { get; }
    public IReadOnlyList<Transform> Bones { get; }

    /// <summary>The rig's bone named <paramref name="name"/>, or null.</summary>
    public Transform? Bone(string name)
    {
        for (var i = 0; i < Bones.Count; i++) if (Bones[i].name == name) return Bones[i];
        return null;
    }

    internal static BodyClipRig? Create(Transform root, GameObject animated, string rootBone, string label)
    {
        var hips = Models.Find(animated.transform, rootBone);
        if (hips == null)
        {
            Plugin.Logger.ErrorOnce("ForgeVisual body clip rig " + label + " has no bone " + rootBone + "; it plays nothing.");
            return null;
        }
        var bones = new List<Transform> { hips };
        foreach (var t in hips.GetComponentsInChildren<Transform>(true)) if (t != hips) bones.Add(t);
        return new BodyClipRig(root, animated, bones.ToArray());
    }
}

/// <summary>How <see cref="Models.BindBodyClip"/> plays clips on a player's body.</summary>
public sealed class BodyClipOptions
{
    /// <summary>Turn the player's IK off while a clip plays (default true): the game's FinalIK solvers and <c>PlayerSyncIK</c> would
    /// otherwise pull the limbs away from the clip. They are handed back exactly as they were.</summary>
    public bool PauseIk { get; set; } = true;

    /// <summary>This peer's own player: in <see cref="BodyClip.FirstPerson"/> also draw the third-person head (default false: the
    /// camera sits in it).</summary>
    public bool FirstPersonHead { get; set; }

    /// <summary>This peer's own player: third-person renderers whose name contains any of these (ignoring case) stay undrawn in
    /// <see cref="BodyClip.FirstPerson"/> (gear that would sit in front of the camera).</summary>
    public IReadOnlyList<string>? FirstPersonSkip { get; set; }

    /// <summary>Names the player in error lines; default the player's lookup.</summary>
    public string? Label { get; set; }
}

/// <summary>
/// Animation clips played on a player's own third-person body (<see cref="Models.BindBodyClip"/>). Each frame it plays, after the game's
/// animator, <see cref="Pose"/> samples the clip on the <see cref="BodyClipRig"/> placed at the given spot and every bone of the body
/// takes its pose: the hips their place and turn in the world, every bone below them its local turn (bones matched by name once, at
/// bind). While it plays the player's IK is off (<see cref="BodyClipOptions.PauseIk"/>); <see cref="Stop"/> hands the body back to the
/// game. This peer's own player can watch it from inside (<see cref="FirstPerson"/>): the third-person body draws in the first-person
/// view and the first-person arms and held item are hidden. Posing looks nothing up and allocates nothing.
/// </summary>
public sealed class BodyClip
{
    private readonly PlayerAgent _agent;
    private readonly BodyClipRig _rig;
    private readonly bool _local;
    private readonly Transform?[] _game;
    private readonly Behaviour[] _solvers;
    private readonly bool[] _solversWere;
    private readonly Renderer[] _drawn;
    private readonly ShadowCastingMode[] _drawnMode;
    private readonly int[] _drawnLayer;
    private readonly Renderer[] _arms;
    private readonly bool[] _armsWere;
    private bool _posing, _firstPerson;

    private BodyClip(PlayerAgent agent, BodyClipRig rig, bool local, Transform?[] game, Behaviour[] solvers, Renderer[] drawn, Renderer[] arms)
    {
        _agent = agent;
        _rig = rig;
        _local = local;
        _game = game;
        _solvers = solvers;
        _solversWere = new bool[solvers.Length];
        _drawn = drawn;
        _drawnMode = new ShadowCastingMode[drawn.Length];
        _drawnLayer = new int[drawn.Length];
        _arms = arms;
        _armsWere = new bool[arms.Length];
        Matched = 0;
        foreach (var bone in game) if (bone != null) Matched++;
    }

    /// <summary>How many of the rig's bones the body has.</summary>
    public int Matched { get; }

    /// <summary>Whether a clip is posing the body now.</summary>
    public bool Posing => _posing;

    internal static BodyClip? Bind(PlayerAgent agent, BodyClipRig rig, BodyClipOptions options)
    {
        var label = options.Label ?? "player " + agent.Owner?.Lookup;
        var animator = agent.AnimatorBody;
        var hips = animator != null ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
        if (hips == null)
        {
            Plugin.Logger.ErrorOnce("ForgeVisual body clip " + label + ": the player's body has no hips; no clip plays on it.");
            return null;
        }
        var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
        foreach (var t in hips.GetComponentsInChildren<Transform>(true)) byName.TryAdd(t.name, t);
        var bones = rig.Bones;
        var game = new Transform?[bones.Count];
        for (var i = 0; i < bones.Count; i++) if (byName.TryGetValue(bones[i].name, out var bone)) game[i] = bone;
        game[0] = hips;
        var solvers = new List<Behaviour>();
        if (options.PauseIk)
            foreach (var behaviour in agent.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var type = behaviour.GetIl2CppType();
                var ns = type.Namespace ?? "";
                if (ns.StartsWith("RootMotion.FinalIK", StringComparison.Ordinal) || type.Name == nameof(PlayerSyncIK)) solvers.Add(behaviour);
            }
        var local = BodyConvert.IsLocal(agent);
        var drawn = new List<Renderer>();
        var arms = new List<Renderer>();
        if (local)
        {
            var model = agent.PlayerSyncModel;
            if (model != null)
            {
                var head = new HashSet<IntPtr>();
                if (!options.FirstPersonHead && model.m_gfxHead != null)
                    foreach (var part in model.m_gfxHead)
                        if (part != null) foreach (var r in part.GetComponentsInChildren<Renderer>(true)) head.Add(r.Pointer);
                foreach (var r in model.GetComponentsInChildren<Renderer>(true))
                    if (!head.Contains(r.Pointer) && !Skipped(r.name, options.FirstPersonSkip)) drawn.Add(r);
            }
            var holder = agent.FPItemHolder;
            if (holder != null) arms.AddRange(holder.GetComponentsInChildren<Renderer>(true));
        }
        return new BodyClip(agent, rig, local, game, solvers.ToArray(), drawn.ToArray(), arms.ToArray());
    }

    private static bool Skipped(string name, IReadOnlyList<string>? skip)
    {
        if (skip == null) return false;
        for (var i = 0; i < skip.Count; i++) if (name.IndexOf(skip[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>Poses the body <paramref name="time"/> seconds into <paramref name="clip"/> (a legacy clip of the rig) with the rig's
    /// root at <paramref name="position"/> facing <paramref name="rotation"/>. After the game's animator, every frame it plays. False
    /// (nothing posed) when the player or the rig is gone.</summary>
    public bool Pose(AnimationClip clip, float time, Vector3 position, Quaternion rotation)
    {
        if (_agent.WasCollected || _rig.Root == null || _rig.Root.WasCollected) return false;
        if (!_posing) Pause();
        _rig.Root.SetPositionAndRotation(position, rotation);
        clip.SampleAnimation(_rig.Animated, time);
        var bones = _rig.Bones;
        var hips = _game[0];
        if (hips != null && !hips.WasCollected) hips.SetPositionAndRotation(bones[0].position, bones[0].rotation);
        for (var i = 1; i < bones.Count; i++)
        {
            var bone = _game[i];
            if (bone != null) bone.localRotation = bones[i].localRotation;
        }
        return true;
    }

    /// <summary>The clip stops: the first-person view goes back to the game's, the IK runs again and the game's animator poses the body
    /// from the next frame.</summary>
    public void Stop()
    {
        FirstPerson(false);
        if (!_posing) return;
        _posing = false;
        for (var i = 0; i < _solvers.Length; i++)
            if (_solvers[i] != null && !_solvers[i].WasCollected && _solversWere[i]) _solvers[i].enabled = true;
    }

    private void Pause()
    {
        _posing = true;
        for (var i = 0; i < _solvers.Length; i++)
        {
            var solver = _solvers[i];
            _solversWere[i] = solver != null && !solver.WasCollected && solver.enabled;
            if (_solversWere[i]) solver!.enabled = false;
        }
    }

    /// <summary>This peer's own player: the first-person view shows this body (seen by <paramref name="camera"/>; renderers on a layer
    /// it does not draw move to the default layer meanwhile) instead of the first-person arms and held item, or goes back to the game's
    /// own view. Nothing for another peer's player.</summary>
    public void FirstPerson(bool on, Camera? camera = null)
    {
        if (!_local || on == _firstPerson) return;
        _firstPerson = on;
        var mask = camera != null && !camera.WasCollected ? camera.cullingMask : ~0;
        for (var i = 0; i < _drawn.Length; i++)
        {
            var r = _drawn[i];
            if (r == null || r.WasCollected) continue;
            if (on)
            {
                _drawnMode[i] = r.shadowCastingMode;
                _drawnLayer[i] = r.gameObject.layer;
                r.shadowCastingMode = ShadowCastingMode.On;
                if ((mask & (1 << r.gameObject.layer)) == 0) r.gameObject.layer = 0;
            }
            else
            {
                r.shadowCastingMode = _drawnMode[i];
                r.gameObject.layer = _drawnLayer[i];
            }
        }
        for (var i = 0; i < _arms.Length; i++)
        {
            var r = _arms[i];
            if (r == null || r.WasCollected) continue;
            if (on) { _armsWere[i] = r.forceRenderingOff; r.forceRenderingOff = true; }
            else r.forceRenderingOff = _armsWere[i];
        }
    }
}

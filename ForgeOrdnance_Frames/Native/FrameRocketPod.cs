using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnanceFrames;

/// <summary>A suit's rocket launcher (<see cref="RocketLauncherSpec"/>), on every peer: the launcher model riding its bone, a rocket
/// seated in every loaded tube (<see cref="FrameRocketRack"/>, from the salvo's launch time, so a late joiner sees the same tubes),
/// and the slot's muzzle flash at a tube's mouth the moment its rocket leaves. Drawn
/// like the rest of the suit (the local pilot's own launcher casts its shadow only) and destroyed with its model, whose bone it rides.</summary>
internal sealed class FrameRocketPod
{
    private readonly RocketsSpec _spec;
    private readonly Transform[] _muzzles;
    private readonly GameObject?[] _seated;
    private readonly List<Renderer> _renderers = new();
    private float _salvoAt;
    private bool _shadowOnly;

    /// <summary>How far a seated rocket's nose sits inside its tube's mouth, metres.</summary>
    private const float SeatInset = 0.015f;

    private FrameRocketPod(RocketsSpec spec, GameObject pod, Transform[] muzzles, float salvoAt)
    {
        _spec = spec;
        _muzzles = muzzles;
        _salvoAt = salvoAt;
        _seated = new GameObject?[muzzles.Length];
        _renderers.AddRange(pod.GetComponentsInChildren<Renderer>(true));
        for (var t = 0; t < muzzles.Length; t++)
        {
            var muzzle = muzzles[t];
            var round = FrameAssets.SpawnProp(spec.Launcher.Round, muzzle.position, muzzle.rotation, muzzle);
            if (round == null) continue;
            round.transform.localPosition = new Vector3(0f, 0f, -(NoseReach(round) + SeatInset));
            round.transform.localRotation = Quaternion.identity;
            _renderers.AddRange(round.GetComponentsInChildren<Renderer>(true));
            _seated[t] = round;
        }
    }

    /// <summary>The launcher of a suit whose model is <paramref name="root"/>, riding its bone (a plate that lifts when the suit
    /// opens carries it along), or null (logged) when its art or bone is missing. <paramref name="salvoAt"/>: the last salvo's launch
    /// time on this peer's clock (none: negative infinity).</summary>
    internal static FrameRocketPod? Create(Transform root, RocketsSpec spec, float salvoAt)
    {
        var launcher = spec.Launcher;
        var skin = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
        var bones = skin != null ? skin.bones : null;
        var bindposes = skin != null && skin.sharedMesh != null ? skin.sharedMesh.bindposes : null;
        if (skin == null || bones == null || bindposes == null || bindposes.Length != bones.Length)
        {
            Plugin.Logger.ErrorOnce("Exosuit rocket launcher: the model has no bind poses for its bones; the suit shows no launcher.");
            return null;
        }
        var index = -1;
        for (var b = 0; b < bones.Length; b++)
            if (bones[b] != null && bones[b].name == launcher.Bone) { index = b; break; }
        if (index < 0)
        {
            Plugin.Logger.ErrorOnce("Exosuit rocket launcher: no bone '" + launcher.Bone + "' skins the model; the suit shows no launcher.");
            return null;
        }
        // The launcher's place is in the model root's space at the rest pose; on its bone it follows every pose from there.
        var rest = root.worldToLocalMatrix * skin.transform.localToWorldMatrix * bindposes[index].inverse;
        var onBone = rest.inverse * Matrix4x4.TRS(new Vector3(launcher.X, launcher.Y, launcher.Z), Quaternion.Euler(launcher.Pitch, launcher.Yaw, launcher.Roll), Vector3.one);
        var pod = FrameAssets.SpawnProp(launcher.Pod, Vector3.zero, Quaternion.identity, bones[index]);
        if (pod == null) return null;
        pod.name = "ForgeOrdnanceFrames.RocketLauncher";
        pod.transform.localPosition = onBone.GetColumn(3);
        pod.transform.localRotation = onBone.rotation;
        pod.transform.localScale = onBone.lossyScale;
        var muzzles = new List<Transform>();
        for (var t = 1; ForgeVisual.Models.Find(pod.transform, FrameRocketRack.MuzzlePrefix + t) is { } muzzle; t++) muzzles.Add(muzzle);
        if (muzzles.Count < 1)
        {
            Plugin.Logger.ErrorOnce("Exosuit rocket launcher " + launcher.Pod + " has no tube " + FrameRocketRack.FirstMuzzle + "; the suit shows no launcher.");
            Object.Destroy(pod);
            return null;
        }
        return new FrameRocketPod(spec, pod, muzzles.ToArray(), salvoAt);
    }

    /// <summary>The furthest point of the rocket's mesh along its own +Z (its nose), in its root's space.</summary>
    private static float NoseReach(GameObject round)
    {
        var filter = round.GetComponentInChildren<MeshFilter>(true);
        if (filter == null || filter.sharedMesh == null) return 0f;
        var b = filter.sharedMesh.bounds;
        var reach = float.MinValue;
        for (var corner = 0; corner < 8; corner++)
        {
            var local = new Vector3((corner & 1) == 0 ? b.min.x : b.max.x, (corner & 2) == 0 ? b.min.y : b.max.y, (corner & 4) == 0 ? b.min.z : b.max.z);
            reach = Math.Max(reach, round.transform.InverseTransformPoint(filter.transform.TransformPoint(local)).z);
        }
        return reach;
    }

    /// <summary>The tube rocket <paramref name="index"/> of a salvo leaves from.</summary>
    private Transform Muzzle(int index) => _muzzles[FrameRocketRack.Tube(index, _muzzles.Length)];

    /// <summary>Where rocket <paramref name="index"/> leaves: its tube's mouth, and the way the tube points.</summary>
    internal Vector3 Origin(int index) => Muzzle(index).position;

    internal Vector3 Facing(int index) => Muzzle(index).forward;

    /// <summary>A salvo was granted at <paramref name="now"/>: its rockets leave their tubes on the salvo's timing.</summary>
    internal void Salvo(float now) => _salvoAt = now;

    /// <summary>Rocket <paramref name="index"/> leaves its tube now: the muzzle flash.</summary>
    internal void Fire(int index)
    {
        var mouth = Muzzle(index);
        if (FramesFxSlots.RocketMuzzle is { } flash) ForgeVisual.Fx.Play(flash, mouth.position, mouth.forward);
    }

    /// <summary>Every frame: a rocket in every loaded tube.</summary>
    internal void Tick(float now)
    {
        for (var t = 0; t < _seated.Length; t++)
        {
            var round = _seated[t];
            if (round == null || round.WasCollected) continue;
            var loaded = FrameRocketRack.Loaded(t, _seated.Length, _spec.Salvo, _spec.Cooldown, _salvoAt, now);
            if (round.activeSelf != loaded) round.SetActive(loaded);
        }
    }

    /// <summary>The local pilot's own suit: shadows only, like its shell.</summary>
    internal void ShadowOnly(bool shadowOnly)
    {
        if (shadowOnly == _shadowOnly) return;
        _shadowOnly = shadowOnly;
        foreach (var renderer in _renderers)
            if (renderer != null && !renderer.WasCollected) ForgeVisual.SurfaceLook.SetShadowOnly(renderer, shadowOnly);
    }
}

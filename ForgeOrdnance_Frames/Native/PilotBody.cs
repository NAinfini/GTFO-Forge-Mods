using System;
using ForgeVisual;
using Player;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnanceFrames;

/// <summary>The pilot's leaving motion on a player's own third-person body (<see cref="PilotMotion"/>), played through ForgeVisual's
/// <see cref="BodyClip"/>. forge-frames.bundle's "pilot-motion" is the game's player skeleton (same bone names, same rest pose) with the
/// legacy clip PilotExit, whose root carries the hop out; one hidden copy of it is ForgeVisual's <see cref="BodyClipRig"/>, placed on the
/// suit while the clip plays. The local player sees the same body from its head (head and chest pouches left out) and
/// <see cref="Eye"/> is where the camera goes: the eye point of the rig, carried on its posed head.</summary>
internal sealed class PilotBody
{
    internal const string Prefab = "pilot-motion";
    private static bool _tried;
    private static BodyClipRig? _rig;
    private static AnimationClip? _exit;
    private static Transform? _head;
    private static Vector3 _eyeInHead;
    private static Quaternion _headRestInverse = Quaternion.identity;
    private static readonly string[] FirstPersonSkip = { "pouch" };

    /// <summary>The motion loaded and checked against <see cref="PilotMotion"/>.</summary>
    internal static bool Ready => _exit != null && _rig != null;

    /// <summary>Once, after the first level is built (the bundle is read then): the rig, its clip, and where the eye sits on the head.</summary>
    internal static void Load()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            var bundle = FrameAssets.Bundle ?? throw new InvalidOperationException("forge-frames.bundle is not loaded");
            if (!bundle.TryModel(Prefab, out var model)) throw new InvalidOperationException("forge-frames.bundle has no " + Prefab);
            var proxy = model.Spawn(new Vector3(0f, -1000f, 0f), Quaternion.identity) ?? throw new InvalidOperationException("the pilot proxy could not be spawned (ForgeVisual has logged why)");
            proxy.name = "ForgeOrdnanceFrames.PilotMotion";
            Object.DontDestroyOnLoad(proxy);
            var modelNode = proxy.transform.Find("model") ?? throw new InvalidOperationException(Prefab + " has no 'model'");
            var animation = modelNode.GetComponent<Animation>() ?? throw new InvalidOperationException(Prefab + " has no Animation");
            animation.playAutomatically = false;
            var exit = animation.GetClip("PilotExit") ?? throw new InvalidOperationException(Prefab + " has no PilotExit");
            var rest = animation.GetClip("Rest") ?? throw new InvalidOperationException(Prefab + " has no Rest");
            var rig = Models.BodyClipRig(proxy.transform, modelNode.gameObject, "Hip", "Frames:" + Prefab) ?? throw new InvalidOperationException(Prefab + " has no Hip");
            var head = rig.Bone("Head") ?? throw new InvalidOperationException(Prefab + " has no Head");
            var eye = proxy.transform.Find("eye") ?? throw new InvalidOperationException(Prefab + " has no eye point");
            proxy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            rest.SampleAnimation(modelNode.gameObject, 0f);
            _eyeInHead = head.InverseTransformPoint(eye.position);
            _headRestInverse = Quaternion.Inverse(head.rotation);
            var toes = rig.Bone("LeftToe");
            var foot = rig.Bone("LeftFoot");
            var facing = toes != null && foot != null ? toes.position.z - foot.position.z : float.NaN;
            proxy.transform.position = new Vector3(0f, -1000f, 0f);
            var problems = "";
            if (Math.Abs(exit.length - PilotMotion.ExitSeconds) > 0.02f) problems += " PilotExit is " + Plugin.Num(exit.length) + " s (expected " + Plugin.Num(PilotMotion.ExitSeconds) + ");";
            if (!(facing > 0f)) problems += " the skeleton does not face forward (left toe " + Plugin.Num(facing) + " m ahead of the foot);";
            if (problems.Length > 0) throw new InvalidOperationException("the pilot motion does not match this plugin:" + problems);
            _rig = rig;
            _exit = exit;
            _head = head;
        }
        catch (Exception error)
        {
            Plugin.Error("Exosuit pilot motion could not be loaded; pilots leave without moving their body or camera: " + error);
        }
    }

    private readonly BodyClip _clip;
    private readonly bool _local;
    private int _eyeFrame = -1;

    /// <summary>Where the local pilot's camera goes this frame (the eye on the posed head) and its turn; valid when <see cref="HasEye"/>.</summary>
    internal Vector3 Eye { get; private set; }
    internal Quaternion EyeRotation { get; private set; }
    internal bool HasEye => _eyeFrame == Time.frameCount;

    private PilotBody(BodyClip clip, bool local)
    {
        _clip = clip;
        _local = local;
    }

    /// <summary>The motion bound to <paramref name="agent"/>'s body (its IK paused while it plays). Null when the motion is not loaded or
    /// the body has no hips.</summary>
    internal static PilotBody? Bind(PlayerAgent agent, bool local)
    {
        if (!Ready) return null;
        var clip = Models.BindBodyClip(agent, _rig!, new BodyClipOptions { FirstPersonSkip = FirstPersonSkip, Label = "Frames pilot " + agent.Owner?.Lookup });
        return clip == null ? null : new PilotBody(clip, local);
    }

    /// <summary>Poses the body <paramref name="time"/> seconds into the leaving clip, with the clip's suit at
    /// <paramref name="position"/> facing <paramref name="rotation"/>. After the game's animator, every frame it plays.</summary>
    internal void Pose(float time, Vector3 position, Quaternion rotation)
    {
        if (!_clip.Pose(_exit!, time, position, rotation) || !_local || _head == null) return;
        Eye = _head.TransformPoint(_eyeInHead);
        EyeRotation = _head.rotation * _headRestInverse;
        _eyeFrame = Time.frameCount;
    }

    /// <summary>The motion stops: the body and the first-person view are the game's again from the next frame.</summary>
    internal void Stop()
    {
        _clip.Stop();
        _eyeFrame = -1;
    }

    /// <summary>Local pilot: the first-person view shows this body (seen by <paramref name="camera"/>) instead of the first-person arms and
    /// held item, or goes back to the game's own view.</summary>
    internal void FirstPerson(bool on, Camera? camera = null) => _clip.FirstPerson(on, camera);
}

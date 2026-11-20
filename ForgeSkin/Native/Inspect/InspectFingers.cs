using ForgeCore;
using ForgeSkin.Format;
using ForgeVisual;
using UnityEngine;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace ForgeSkin.Plugin;

// The look's finger shapes on the first-person arms (InspectLook.OffFingers, the off hand's keys relaxed where it touches nothing and
// each finger stopped where it meets the gun; InspectLook.GripFingersBaked, the grip hand, whose thumb and three fingers keep the
// game's own grip while it holds the gun and only open from it on a twirl and round a toss; InspectLook.IndexFinger, its index
// finger, off the trigger and along the gun's side for the look, in the guard for a twirl). Per look, a clip of each hand's fingers alone (ForgeCore
// ClipFingers, sampled from the look) plays on the game's first-person arms (ForgeCore Models.BindArms) masked to those fingers, the
// grip hand's twice: on its index finger and on its other fingers, which the look takes over by different amounts; each frame
// it is moved to the look's time and weighed by how far the look takes those fingers there times the look's own weight, so where it
// takes none, and as the look fades, the game's own grip shows. The arms are posed by this class (JointPlayer.Manual), right after the skin's
// finger turns in the same hook (FingerPose.Hook), so the shape lies over them while it plays and they come back as it fades. First
// person only: teammates and replays see the wrist.
static class InspectFingers
{
    // A hand's finger joints run thumb to pinky, three each (ForgeCore ClipFingers): the index is the second three.
    static readonly int[] RightFingers = JointMask.Joints(BodyPart.RightFingers);
    static readonly JointMask Left = new JointMask().Add(BodyPart.LeftFingers), Index = new JointMask().AddJoints(RightFingers[3..6], 1f),
        Others = new JointMask().AddJoints([.. RightFingers[..3], .. RightFingers[6..]], 1f);
    static JointPlayer? _arms;
    static IntPtr _animator;
    static JointTrack _off, _grip, _trigger;

    // A look starts on these arms: its hands' clips start, at no weight until Pose.
    public static void Start(InspectLook look, Animator? animator)
    {
        Stop();
        if (!look.ShapesOff && !look.ShapesGrip) return;
        if (animator == null || animator.WasCollected) { ForgeSkinPlugin.Note($"inspect {look.Motion.Id}: the first-person arms have no animator; the fingers keep the game's grip"); return; }
        if (animator.Pointer != _animator)
        {
            _animator = animator.Pointer;
            _arms = Models.BindArms(animator, "first-person arms", out _); // null logged once by ForgeCore
            if (_arms != null) _arms.Manual = true;
        }
        if (_arms == null) return;
        FingerPose.Hook();
        try
        {
            if (look.ShapesOff) _off = _arms.Play(Clip(look, false), new JointPlayback(Time.time, mask: Left, weight: 0f));
            if (look.ShapesGrip)
            {
                var clip = Clip(look, true);
                _grip = _arms.Play(clip, new JointPlayback(Time.time, mask: Others, weight: 0f));
                _trigger = _arms.Play(clip, new JointPlayback(Time.time, mask: Index, weight: 0f));
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            ForgeSkinPlugin.Note($"inspect {look.Motion.Id}: the fingers keep the game's grip ({e.Message})");
        }
    }

    public static void Pose(InspectLook look, float time, float weight)
    {
        if (_off.Playing)
        {
            _off.Seek(time);
            _off.Weigh(Math.Clamp(look.OffFingers(time, out _) * weight, 0f, 1f));
        }
        if (_grip.Playing)
        {
            _grip.Seek(time);
            _grip.Weigh(Math.Clamp(look.GripFingersBaked(time, out _) * weight, 0f, 1f));
        }
        if (_trigger.Playing)
        {
            _trigger.Seek(time);
            _trigger.Weigh(Math.Clamp(look.IndexFinger(time, out _) * weight, 0f, 1f));
        }
    }

    // After the first-person holder and the skin's finger turns (FingerPose.Hook): the shapes go on.
    public static void PoseArms()
    {
        if (_off.Playing || _grip.Playing || _trigger.Playing) _arms?.Pose();
    }

    public static void Stop()
    {
        _off.Stop();
        _grip.Stop();
        _trigger.Stop();
        (_off, _grip, _trigger) = (default, default, default);
    }

    // The level ended or a checkpoint reloaded: the arms are rebuilt and ForgeCore stopped their clips.
    public static void Forget()
    {
        (_off, _grip, _trigger) = (default, default, default);
        (_arms, _animator) = (null, IntPtr.Zero);
    }

    // One hand's fingers over the look: its shape every InspectLook.ClipRate (from the look's smooth curve between its samples; the
    // weight comes from Pose), every other joint at rest.
    static JointClip Clip(InspectLook look, bool right)
    {
        var frames = (int)MathF.Ceiling(look.Seconds / InspectLook.ClipRate) + 1;
        var turns = new NQuaternion[frames * ClipRig.Count];
        Array.Fill(turns, NQuaternion.Identity);
        for (var f = 0; f < frames; f++)
        {
            var time = f * InspectLook.ClipRate;
            ForgeVisual.FingerBend bend;
            if (right) look.GripFingersBaked(time, out bend);
            else look.OffFingers(time, out bend);
            var fingers = turns.AsSpan(f * ClipRig.Count + ClipRig.BodyCount + (right ? 15 : 0), 15);
            ClipFingers.Turns(right, NQuaternion.Identity, bend, fingers);
            // The index finger's knuckle bent back, which a FingerBend does not say.
            if (right && look.IndexFinger(time, out var index) > 0f && index < 0f) InspectHand.Lifted(true, 1, index, fingers.Slice(3, 3));
        }
        return JointClip.FromTurns(InspectLook.ClipRate, ClipRig.Count, turns, new NVector3[frames]);
    }
}

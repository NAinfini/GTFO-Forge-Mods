using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameData;
using Gear;
using Player;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The finger poses of the game's grip animations, measured on the local player's first-person arms, one animation per call so the game
/// evaluates it in its own frames (<c>tools/capture_finger_poses.py</c> drives it; ForgeSkin's backend keeps the result as <c>hand-shapes.json</c>):
/// <c>Names</c> lists every grip animation the gear part blocks name (front, receiver and stock parts:
/// <c>General.LeftHandGripAnim</c> / <c>RightHandGripAnim</c>), <c>Joints left|right</c> the hand's 15 finger joints (bone, parent, rest
/// position under the parent), <c>Play left|right name</c> puts the hand's layer on that animation (a gun must be in the hands), and <c>Read left|right</c>, called
/// after it, answers the state the layer is on and the joints' local rotations (x, y, z, w) in joint order.
/// </summary>
internal static class FingerPoseCapture
{
    internal static string Names()
    {
        var left = new SortedSet<string>(StringComparer.Ordinal);
        var right = new SortedSet<string>(StringComparer.Ordinal);
        void Take(string l, string r) { if (!string.IsNullOrEmpty(l)) left.Add(l); if (!string.IsNullOrEmpty(r)) right.Add(r); }
        foreach (var b in GameDataBlockBase<GearFrontPartDataBlock>.GetAllBlocks()) Take(b.General.LeftHandGripAnim, b.General.RightHandGripAnim);
        foreach (var b in GameDataBlockBase<GearReceiverPartDataBlock>.GetAllBlocks()) Take(b.General.LeftHandGripAnim, b.General.RightHandGripAnim);
        foreach (var b in GameDataBlockBase<GearStockPartDataBlock>.GetAllBlocks()) Take(b.General.LeftHandGripAnim, b.General.RightHandGripAnim);
        return "left=" + string.Join(",", left) + ";right=" + string.Join(",", right);
    }

    internal static string Joints(string side)
    {
        if (Hand(side, out var joints, out _, out _) is { } error) return error;
        var text = new StringBuilder();
        foreach (var j in joints)
        {
            var p = j.localPosition;
            text.Append(j.name).Append('<').Append(j.parent.name).Append('@').Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append(';');
        }
        return text.ToString();
    }

    // The game's own way (playing the state on the layer directly changes nothing): the wielded gun's holder names the hands'
    // animations and the inventory plays them.
    internal static string Play(string side, string name)
    {
        if (Hand(side, out _, out var animator, out var layer) is { } error) return error;
        if (!animator.HasState(layer, Animator.StringToHash(name))) return "fingers: no state " + name + " on the " + side + " hand layer";
        var agent = PlayerManager.GetLocalPlayerAgent();
        var item = agent.FPItemHolder.WieldedItem;
        var holder = item != null ? item.GetComponentInChildren<GearPartHolder>() : null;
        if (holder == null) return "fingers: the wielded item has no gear part holder (wield a gun)";
        if (side == "left") holder.m_animLeftHand = name; else holder.m_animRightHand = name;
        agent.Inventory.PlayAnimationsForWieldedItem();
        return "playing " + name;
    }

    internal static string Read(string side)
    {
        if (Hand(side, out var joints, out var animator, out var layer) is { } error) return error;
        var text = new StringBuilder("state ").Append(animator.GetCurrentAnimatorStateInfo(layer).shortNameHash).Append(" weight ").Append(F(animator.GetLayerWeight(layer))).Append(animator.IsInTransition(layer) ? " moving" : " still").Append(':');
        foreach (var j in joints)
        {
            var q = j.localRotation;
            text.Append(F(q.x)).Append(' ').Append(F(q.y)).Append(' ').Append(F(q.z)).Append(' ').Append(F(q.w)).Append(';');
        }
        return text.ToString();
    }

    // Record start|stop <file>: every rendered frame (once per frame, just before the first camera culls: after every LateUpdate, the
    // game's animation and IK and the mods' poses included) both hands' 30 finger joints' local rotations, with the frame number, the
    // unscaled time and its delta. Stop writes one CSV row per frame (frame, time, dt, then x y z w per joint, left 0..14 then right
    // 0..14) to the file and answers how many.
    static IDisposable? _recording;
    static readonly List<float[]> Recorded = new();
    static Transform[] _recordJoints = Array.Empty<Transform>();
    static int _recordFrame = -1;

    internal static string Record(string what, string file = "")
    {
        if (what == "start")
        {
            if (Hand("left", out var l, out _, out _) is { } el) return el;
            if (Hand("right", out var r, out _, out _) is { } er) return er;
            _recordJoints = [.. l, .. r];
            Recorded.Clear();
            _recording?.Dispose();
            _recording = ForgeCore.Frame.OnPreCull("ForgeDevelopment", RecordFrame);
            return "recording";
        }
        if (what != "stop") return "fingers: record start or stop <file>";
        _recording?.Dispose();
        _recording = null;
        var text = new StringBuilder();
        foreach (var row in Recorded) text.Append(string.Join(",", Array.ConvertAll(row, F))).Append('\n');
        System.IO.File.WriteAllText(file, text.ToString());
        return Recorded.Count + " frames";
    }

    static void RecordFrame(Camera camera)
    {
        var frame = Time.frameCount;
        if (frame == _recordFrame) return;
        _recordFrame = frame;
        var row = new float[3 + _recordJoints.Length * 4];
        (row[0], row[1], row[2]) = (frame, Time.unscaledTime, Time.unscaledDeltaTime);
        for (var i = 0; i < _recordJoints.Length; i++)
        {
            if (_recordJoints[i] == null) continue;
            var q = _recordJoints[i].localRotation;
            (row[3 + 4 * i], row[4 + 4 * i], row[5 + 4 * i], row[6 + 4 * i]) = (q.x, q.y, q.z, q.w);
        }
        Recorded.Add(row);
    }
    // HumanBodyBones: 24..38 the left hand's thumb, index, middle, ring and little fingers (proximal, intermediate, distal each), 39..53 the right's.
    static string? Hand(string side, out Transform[] joints, out Animator animator, out int layer)
    {
        joints = Array.Empty<Transform>();
        animator = null!;
        layer = 0;
        var agent = PlayerManager.GetLocalPlayerAgent();
        var holder = agent != null ? agent.FPItemHolder : null;
        var body = holder != null ? holder.FPSArms : null;
        if (body == null || body.m_anim == null || !body.m_anim.isHuman) return "fingers: no local first-person arms with a humanoid animator";
        var left = side == "left";
        if (!left && side != "right") return "fingers: the side is left or right";
        animator = body.m_anim;
        layer = left ? PlayerFPSBody.ANIM_LAYER_LEFT_HAND : PlayerFPSBody.ANIM_LAYER_RIGHT_HAND;
        joints = new Transform[15];
        for (var i = 0; i < 15; i++)
        {
            var joint = animator.GetBoneTransform((HumanBodyBones)((left ? 24 : 39) + i));
            if (joint == null) return "fingers: the " + side + " hand has no joint " + i;
            joints[i] = joint;
        }
        return null;
    }

    static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
}

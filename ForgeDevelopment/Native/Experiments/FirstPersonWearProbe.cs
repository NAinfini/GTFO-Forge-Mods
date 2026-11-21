using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Player;
using RootMotion.FinalIK;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// Reads back a model worn on the local player's first-person body (ForgeVisual <c>Models.WearFirstPerson</c>, a character's own arms):
/// <c>drive_cli.py call static:ForgeDevelopment.Native.FirstPersonWearProbe Report</c>. One line: whether the game's first-person animator
/// is humanoid and how many of its 30 finger joints exist; the game's arm renderers and how many are held off; each skinned renderer
/// under the body that is not the game's (a worn model), drawn or not, its shadow mode against the game arm's; and for each worn model bone
/// that sits on a game hand or finger bone the largest distance between the two joints (the hands must be the game's: on the grip).
/// </summary>
internal static class FirstPersonWearProbe
{
    internal static string Report()
    {
        var agent = PlayerManager.GetLocalPlayerAgent();
        var body = agent == null || agent.FPItemHolder == null ? null : agent.FPItemHolder.FPSArms;
        if (body == null) return "fpwear: no local player with a first-person body";
        var text = new StringBuilder("fpwear:");
        var animator = body.m_anim;
        var game = new HashSet<IntPtr>();
        var gameBones = new HashSet<IntPtr>(); // the game's skeleton: its clothes skin these too, a worn model only its own bones
        var fingers = new List<Transform>();
        var human = animator != null && animator.isHuman;
        if (human)
            for (var b = 24; b < 54; b++)
                if (animator!.GetBoneTransform((HumanBodyBones)b) is { } finger) fingers.Add(finger);
        text.Append(" humanoid=").Append(human).Append("; fingers=").Append(fingers.Count).Append("/30");

        var arms = 0;
        var off = 0;
        Renderer? sample = null;
        var groups = new[] { body.m_gfxArms, body.m_gfxTorso, body.m_gfxLegs, body.m_gfxHead };
        for (var g = 0; g < groups.Length; g++)
        {
            if (groups[g] == null) continue;
            foreach (var part in groups[g])
            {
                if (part == null) continue;
                foreach (var r in part.GetComponentsInChildren<Renderer>(true))
                {
                    game.Add(r.Pointer);
                    if (r.TryCast<SkinnedMeshRenderer>() is { } own)
                        foreach (var bone in own.bones) if (bone != null) gameBones.Add(bone.Pointer);
                    if (g != 0) continue;
                    arms++;
                    sample ??= r;
                    if (r.forceRenderingOff) off++;
                }
            }
        }
        text.Append("; game arm renderers=").Append(arms).Append(", held off=").Append(off);
        if (sample != null) text.Append(", game arm shadows=").Append(sample.shadowCastingMode);

        var hands = new[] { Hand(body.m_leftHandIK), Hand(body.m_rightHandIK) };
        var worn = 0;
        foreach (var skin in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (game.Contains(skin.Pointer) || SharesBone(skin, gameBones)) continue;
            worn++;
            float hand = -1f, finger = -1f;
            int onHands = 0, onFingers = 0;
            var scale = 0f;
            foreach (var bone in skin.bones)
            {
                var parent = bone == null ? null : bone.parent;
                if (parent == null) continue;
                if (ForgeCore.NativeObjects.Same(parent, hands[0]) || ForgeCore.NativeObjects.Same(parent, hands[1]))
                {
                    onHands++;
                    hand = Math.Max(hand, Vector3.Distance(bone!.position, parent.position));
                    scale = bone.lossyScale.x;
                }
                else if (fingers.Exists(f => ForgeCore.NativeObjects.Same(parent, f)))
                {
                    onFingers++;
                    finger = Math.Max(finger, Vector3.Distance(bone!.position, parent.position));
                }
            }
            var mesh = skin.sharedMesh;
            text.Append("; worn ").Append(skin.name).Append(": drawn=").Append(skin.gameObject.activeInHierarchy && skin.enabled && !skin.forceRenderingOff)
                .Append(", shadows=").Append(skin.shadowCastingMode).Append(", triangles=").Append(mesh == null ? 0 : Triangles(mesh))
                .Append(", bones on game hands=").Append(onHands).Append(" (largest joint gap ").Append(Metres(hand)).Append(", scale ").Append(scale.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("), on game fingers=").Append(onFingers).Append(" (largest gap ").Append(Metres(finger)).Append(')');
        }
        if (worn == 0) text.Append("; nothing worn");
        return text.ToString();
    }

    private static Transform? Hand(LimbIK? ik) => ik == null || ik.solver == null || ik.solver.bone3 == null ? null : ik.solver.bone3.transform;

    /// <summary>Every renderer drawn now under the local player's first-person item holder and first-person body (active, enabled, not
    /// held off), with its path from the holder, layer and skinned-or-not: what the first-person view is made of.</summary>
    internal static string Drawn()
    {
        var agent = PlayerManager.GetLocalPlayerAgent();
        var holder = agent == null ? null : agent.FPItemHolder;
        if (holder == null) return "fpdrawn: no local player";
        var text = new StringBuilder("fpdrawn:");
        var seen = new HashSet<IntPtr>();
        foreach (var root in new Component?[] { holder, holder.FPSArms })
        {
            if (root == null) continue;
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!seen.Add(r.Pointer) || !r.enabled || r.forceRenderingOff) continue;
                var path = r.name;
                for (var up = r.transform.parent; up != null && up.Pointer != holder.transform.Pointer && path.Length < 160; up = up.parent) path = up.name + "/" + path;
                text.Append(' ').Append(path).Append(" [layer ").Append(r.gameObject.layer).Append(r.TryCast<SkinnedMeshRenderer>() != null ? ", skinned]" : "]").Append(';');
            }
        }
        return text.ToString();
    }

    /// <summary>The worn models ForgeCore follows each frame (its private list, read by reflection): for each, on or not, shown or not,
    /// game parts held or not, and whether its model and the game body it sits on still exist.</summary>
    internal static string Followed()
    {
        const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var type = typeof(ForgeVisual.FirstPersonWear);
        if (type.GetField("Worn", any)?.GetValue(null) is not System.Collections.IList list) return "fpfollowed: no list";
        var text = new StringBuilder("fpfollowed: ").Append(list.Count);
        foreach (var wear in list)
        {
            text.Append("; ");
            foreach (var name in new[] { "_active", "_shown", "_held" }) text.Append(name).Append('=').Append(type.GetField(name, any)?.GetValue(wear)).Append(' ');
            text.Append("alive=").Append(((ForgeVisual.FirstPersonWear)wear).Alive).Append(" hidden=").Append(((ForgeVisual.FirstPersonWear)wear).Hidden);
            if (type.GetField("_arms", any)?.GetValue(wear) is List<GameObject> arms)
                foreach (var go in arms) text.Append(" arm ").Append(go == null ? "gone" : go.name + (go.activeSelf ? " on" : " off"));
        }
        return text.ToString();
    }

    private static bool SharesBone(SkinnedMeshRenderer skin, HashSet<IntPtr> bones)
    {
        foreach (var bone in skin.bones) if (bone != null && bones.Contains(bone.Pointer)) return true;
        return false;
    }

    private static string Metres(float value) => value < 0f ? "none" : value.ToString("0.#####", CultureInfo.InvariantCulture) + " m";

    private static int Triangles(Mesh mesh)
    {
        var count = 0;
        for (var sub = 0; sub < mesh.subMeshCount; sub++) count += (int)mesh.GetIndexCount(sub) / 3;
        return count;
    }
}

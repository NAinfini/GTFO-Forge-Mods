using System;
using System.Collections.Generic;
using ForgeVisual;

namespace ForgeOrdnanceFrames;

/// <summary>How the suits' rig maps onto the player's body for ForgeVisual's body mechanisms: the first-person shell's bones
/// (<see cref="FrameSpec.Shell"/>, worn on the game's first-person body) and the worn suit's pose copy (<see cref="PoseLinks"/>, the
/// game's third-person body pose on the suit).</summary>
internal static class FrameBodyRig
{
    /// <summary>The suits' rig (tools/build_frame.py: root, pelvis, spine, chest, head, upperarm, forearm, hand, thigh, shin, foot; no
    /// neck, shoulder, finger or toe bones), parents before children. The game's upper chest and neck fold into the suit's chest.</summary>
    internal static readonly PoseLink[] PoseLinks =
    {
        new("pelvis", BodyBone.Hips, toward: 1),
        new("spine", BodyBone.Spine, toward: 2),
        new("chest", BodyBone.UpperChest, toward: 3, bodyAlt: BodyBone.Chest, towardBody: BodyBone.Neck, towardBodyAlt: BodyBone.Head),
        new("head", BodyBone.Head),
        new("upperarm.L", BodyBone.LeftUpperArm, toward: 5),
        new("forearm.L", BodyBone.LeftLowerArm, toward: 6),
        new("hand.L", BodyBone.LeftHand, from: 5),
        new("upperarm.R", BodyBone.RightUpperArm, toward: 8),
        new("forearm.R", BodyBone.RightLowerArm, toward: 9),
        new("hand.R", BodyBone.RightHand, from: 8),
        new("thigh.L", BodyBone.LeftUpperLeg, toward: 11),
        new("shin.L", BodyBone.LeftLowerLeg, toward: 12),
        new("foot.L", BodyBone.LeftFoot),
        new("thigh.R", BodyBone.RightUpperLeg, toward: 14),
        new("shin.R", BodyBone.RightLowerLeg, toward: 15),
        new("foot.R", BodyBone.RightFoot),
    };

    /// <summary>What <paramref name="spec"/> names on its model that <paramref name="has"/> (a node of the prefab by name) does not find,
    /// as (definition field, node): the 16 rig bones, every opening part, every shell bone and the launcher's bone.</summary>
    internal static IEnumerable<(string Field, string Node)> MissingNodes(FrameSpec spec, Func<string, bool> has)
    {
        foreach (var link in PoseLinks)
            if (!has(link.Model)) yield return ("Model", link.Model);
        if (spec.Opening != null)
            for (var i = 0; i < spec.Opening.Parts.Length; i++)
                if (!has(spec.Opening.Parts[i].Node)) yield return ("Opening.Parts[" + i + "].Node", spec.Opening.Parts[i].Node);
        for (var i = 0; i < spec.Shell.Length; i++)
            if (!has(spec.Shell[i].Bone)) yield return ("Shell[" + i + "].Bone", spec.Shell[i].Bone);
        if (spec.Rockets != null && !has(spec.Rockets.Launcher.Bone)) yield return ("Rockets.Launcher.Bone", spec.Rockets.Launcher.Bone);
    }

    internal const int LeftUpperArmLink = 4, RightUpperArmLink = 7, LeftHandLink = 6, RightHandLink = 9;
    private const int ChestLink = 2;

    /// <summary>The suit's arms are not the body's arms scaled, so copied turns alone leave its hands off whatever the body grips: both
    /// arms bend so each wrist lands where the body's does, carried over from the chest (one anchor, so the two hands stay where they are
    /// on the item to each other and the held copy, which hangs from the right hand, stays in both).</summary>
    internal static readonly PoseReach[] ArmReach =
    {
        new(LeftUpperArmLink, LeftUpperArmLink + 1, LeftUpperArmLink + 2, ChestLink),
        new(RightUpperArmLink, RightUpperArmLink + 1, RightUpperArmLink + 2, ChestLink),
    };

    /// <summary>The shell's bones for ForgeVisual: the arms only line the skeletons up (the pilot's arms and hands are the game's own),
    /// each leg bone is sized by its own leg's length, every other bone by the shoulder span.</summary>
    internal static WearBone[] ShellBones(IReadOnlyList<ShellBone> shell)
    {
        var bones = new WearBone[shell.Count];
        for (var i = 0; i < shell.Count; i++)
        {
            var body = Body(shell[i].Body);
            bones[i] = new WearBone(shell[i].Bone, body, worn: !IsArm(body), scale: Measure(body));
        }
        return bones;
    }

    /// <summary>The opening parts' nodes: hinged on a worn bone, they stay shut on the shell and their skin counts as that bone's.</summary>
    internal static string[] HingedParts(OpeningSpec? opening)
    {
        if (opening == null) return Array.Empty<string>();
        var nodes = new string[opening.Parts.Length];
        for (var i = 0; i < nodes.Length; i++) nodes[i] = opening.Parts[i].Node;
        return nodes;
    }

    /// <summary>The definition's body bone as ForgeVisual's (the same humanoid names).</summary>
    internal static BodyBone Body(FrameBodyBone bone) => Enum.Parse<BodyBone>(bone.ToString());

    private static bool IsArm(BodyBone bone)
        => bone is BodyBone.LeftUpperArm or BodyBone.LeftLowerArm or BodyBone.LeftHand or BodyBone.RightUpperArm or BodyBone.RightLowerArm or BodyBone.RightHand;

    private static BodyMeasure Measure(BodyBone bone) => bone switch
    {
        BodyBone.LeftUpperLeg or BodyBone.LeftLowerLeg or BodyBone.LeftFoot or BodyBone.LeftToes => BodyMeasure.LeftLeg,
        BodyBone.RightUpperLeg or BodyBone.RightLowerLeg or BodyBone.RightFoot or BodyBone.RightToes => BodyMeasure.RightLeg,
        _ => BodyMeasure.ShoulderSpan,
    };
}

using System;
using System.Collections.Generic;

namespace ForgeEnemy.Profile;

/// <summary>A part of an enemy's look an appearance entry can take from another vanilla model. `Full` is the whole
/// look; the six humanoid regions are the website preview's own (`ENEMY_HUMANOID_REGIONS` in
/// `Web/src/features/enemy-editor/enemy-editor-hover.ts`), in the same order.</summary>
internal enum EnemyAppearanceRegion
{
    Full,
    Head,
    Torso,
    LeftArm,
    RightArm,
    LeftLeg,
    RightLeg
}

/// <summary>One classifier rule: a bone whose lower-cased name contains <see cref="Side"/> (when it names one) and any
/// of <see cref="Keywords"/> belongs to <see cref="Region"/>. Rules are tried in order; the first match wins.</summary>
internal sealed record EnemyBoneRegionRule(EnemyAppearanceRegion Region, string? Side, IReadOnlyList<string> Keywords);

/// <summary>The one bone-to-region table the game side assembles with. It is the website preview's `enemyBoneRegion`
/// rule for rule, so the part an author picks in the editor is the part the game shows: the EnemyProfile suite parses
/// the TypeScript function and fails when either side changes alone.
///
/// The core skeleton is the 19 humanoid bones every GTFO humanoid enemy rig names identically (striker, shooter,
/// scout, shadow, charger, nightmare, tank and mother families). A model without all 19 cannot be assembled: its
/// regions would have no bone to hang on. That refuses the snatcher (it names `Hip`, not `Hips`, and has no
/// `Chest`), the hybrid (no `Head`), and the non-humanoid flyer and squid rigs.</summary>
internal static class EnemyAppearanceRegions
{

    /// <summary>The ordered rules; a bone no rule claims is <see cref="EnemyAppearanceRegion.Torso"/>.</summary>
    internal static readonly IReadOnlyList<EnemyBoneRegionRule> Rules = new[]
    {
        new EnemyBoneRegionRule(EnemyAppearanceRegion.LeftArm, "left", new[] { "shoulder", "arm", "hand" }),
        new EnemyBoneRegionRule(EnemyAppearanceRegion.RightArm, "right", new[] { "shoulder", "arm", "hand" }),
        new EnemyBoneRegionRule(EnemyAppearanceRegion.LeftLeg, "left", new[] { "leg", "foot", "toe" }),
        new EnemyBoneRegionRule(EnemyAppearanceRegion.RightLeg, "right", new[] { "leg", "foot", "toe" }),
        new EnemyBoneRegionRule(EnemyAppearanceRegion.Head, null, new[] { "head", "neck", "jaw", "mandible", "mouth" })
    };

    internal const EnemyAppearanceRegion Fallback = EnemyAppearanceRegion.Torso;

    /// <summary>The 19 bones a model must name to be assembled, target and source alike.</summary>
    internal static readonly IReadOnlyList<string> CoreBones = new[]
    {
        "Hips", "Spine", "Chest", "Neck", "Head",
        "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
        "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
        "LeftUpperLeg", "LeftLowerLeg", "LeftFoot",
        "RightUpperLeg", "RightLowerLeg", "RightFoot"
    };

    /// <summary>The region a bone belongs to, by name alone.</summary>
    internal static EnemyAppearanceRegion Classify(string? boneName)
    {
        string value = (boneName ?? string.Empty).ToLowerInvariant();
        foreach (var rule in Rules)
        {
            if (rule.Side != null && !value.Contains(rule.Side, StringComparison.Ordinal)) continue;
            foreach (var keyword in rule.Keywords)
                if (value.Contains(keyword, StringComparison.Ordinal)) return rule.Region;
        }
        return Fallback;
    }

    /// <summary>The topmost core bone of a region: where that region meets the rest of the body, and where its zero-scale
    /// anchors hang. `Full` hangs on the skeleton's root core bone.</summary>
    internal static string Boundary(EnemyAppearanceRegion region) => region switch
    {
        EnemyAppearanceRegion.Head => "Neck",
        EnemyAppearanceRegion.LeftArm => "LeftShoulder",
        EnemyAppearanceRegion.RightArm => "RightShoulder",
        EnemyAppearanceRegion.LeftLeg => "LeftUpperLeg",
        EnemyAppearanceRegion.RightLeg => "RightUpperLeg",
        _ => "Hips"
    };

    /// <summary>Whether a bone in <paramref name="region"/> is shown by an entry for <paramref name="shown"/>.</summary>
    internal static bool Covers(EnemyAppearanceRegion shown, EnemyAppearanceRegion region)
        => shown == EnemyAppearanceRegion.Full || shown == region;

    internal static bool TryParse(string? text, out EnemyAppearanceRegion region)
    {
        switch (text)
        {
            case "full": region = EnemyAppearanceRegion.Full; return true;
            case "head": region = EnemyAppearanceRegion.Head; return true;
            case "torso": region = EnemyAppearanceRegion.Torso; return true;
            case "leftArm": region = EnemyAppearanceRegion.LeftArm; return true;
            case "rightArm": region = EnemyAppearanceRegion.RightArm; return true;
            case "leftLeg": region = EnemyAppearanceRegion.LeftLeg; return true;
            case "rightLeg": region = EnemyAppearanceRegion.RightLeg; return true;
            default: region = EnemyAppearanceRegion.Full; return false;
        }
    }

    /// <summary>The wire spelling, which is also the website's.</summary>
    internal static string Name(EnemyAppearanceRegion region) => region switch
    {
        EnemyAppearanceRegion.Head => "head",
        EnemyAppearanceRegion.Torso => "torso",
        EnemyAppearanceRegion.LeftArm => "leftArm",
        EnemyAppearanceRegion.RightArm => "rightArm",
        EnemyAppearanceRegion.LeftLeg => "leftLeg",
        EnemyAppearanceRegion.RightLeg => "rightLeg",
        _ => "full"
    };
}

/// <summary>What one skinning slot of a renderer points at after assembly: a real bone of the target skeleton, the
/// renderer's zero-scale anchor under a region's boundary bone, or nothing (a slot that was already empty).</summary>
internal enum EnemyBoneSlotKind { Bone, Anchor, Empty }

internal readonly struct EnemyBoneSlot<TBone> where TBone : class
{
    private EnemyBoneSlot(EnemyBoneSlotKind kind, TBone? bone, EnemyAppearanceRegion anchor)
    { Kind = kind; Bone = bone; AnchorRegion = anchor; }

    internal EnemyBoneSlotKind Kind { get; }
    internal TBone? Bone { get; }

    /// <summary>For an anchor slot, the region whose boundary bone the anchor hangs under.</summary>
    internal EnemyAppearanceRegion AnchorRegion { get; }

    internal static EnemyBoneSlot<TBone> Real(TBone bone) => new(EnemyBoneSlotKind.Bone, bone, default);
    internal static EnemyBoneSlot<TBone> Anchor(EnemyAppearanceRegion region) => new(EnemyBoneSlotKind.Anchor, null, region);
    internal static EnemyBoneSlot<TBone> None => new(EnemyBoneSlotKind.Empty, null, default);
}

/// <summary>A skeleton by bone name: the union of the bones one model's skinned renderers use. The same name on two
/// different bones is refused rather than resolved by order, because either choice would bind a surface to a bone
/// the author never saw.</summary>
internal sealed class EnemySkeletonIndex<TBone> where TBone : class
{
    private readonly Dictionary<string, TBone> _byName;
    private EnemySkeletonIndex(Dictionary<string, TBone> byName) { _byName = byName; }

    internal int Count => _byName.Count;

    internal static EnemySkeletonIndex<TBone>? Build(IEnumerable<IReadOnlyList<TBone?>> boneLists, Func<TBone, string> name,
        Func<TBone, int> identity, out string? ambiguous)
    {
        ambiguous = null;
        var byName = new Dictionary<string, TBone>(StringComparer.Ordinal);
        foreach (var bones in boneLists)
            foreach (var bone in bones)
            {
                if (bone == null) continue;
                string key = name(bone);
                if (!byName.TryGetValue(key, out var known)) { byName.Add(key, bone); continue; }
                if (identity(known) == identity(bone)) continue;
                ambiguous = key;
                return null;
            }
        return new EnemySkeletonIndex<TBone>(byName);
    }

    internal bool TryGet(string boneName, out TBone bone) => _byName.TryGetValue(boneName, out bone!);

    /// <summary>The core bones this skeleton does not name, in the table's order.</summary>
    internal List<string> MissingCore()
    {
        var missing = new List<string>();
        foreach (var core in EnemyAppearanceRegions.CoreBones) if (!_byName.ContainsKey(core)) missing.Add(core);
        return missing;
    }
}

/// <summary>The bone remap itself, game-independent: the native assembler hands it bone handles plus two accessors
/// (name and parent) and writes back what it answers. Nothing here scales a bone — hiding is always a slot pointed at
/// a zero-scale anchor, so the shared skeleton, and the vanilla `ModelData` part scales that act on it, stay as the
/// game built them.</summary>
internal static class EnemyAppearanceRemap
{
    /// <summary>The target bone a source bone lands on: the bone of the same name, else the nearest ancestor in the
    /// source hierarchy that the target names. Null when no ancestor is named (a bone above the core, such as a rig's
    /// own `root`).</summary>
    internal static TBone? Map<TBone>(TBone? source, EnemySkeletonIndex<TBone> target, Func<TBone, string> name,
        Func<TBone, TBone?> parent, out bool viaAncestor) where TBone : class
    {
        viaAncestor = false;
        for (var cursor = source; cursor != null; cursor = parent(cursor))
        {
            if (target.TryGet(name(cursor), out var bone)) return bone;
            viaAncestor = true;
        }
        viaAncestor = false;
        return null;
    }

    /// <summary>A source renderer copied onto the target, showing only <paramref name="shown"/>: its bones in that
    /// region land on the target skeleton, and every other bone — including one with no target ancestor — goes to
    /// the anchor under the region's boundary.</summary>
    internal static EnemyBoneSlot<TBone>[] Source<TBone>(IReadOnlyList<TBone?> bones, EnemyAppearanceRegion shown,
        EnemySkeletonIndex<TBone> target, Func<TBone, string> name, Func<TBone, TBone?> parent,
        out int mapped, out int viaAncestor) where TBone : class
    {
        mapped = 0; viaAncestor = 0;
        var slots = new EnemyBoneSlot<TBone>[bones.Count];
        for (int index = 0; index < bones.Count; index++)
        {
            var bone = bones[index];
            if (bone == null) { slots[index] = EnemyBoneSlot<TBone>.None; continue; }
            if (!EnemyAppearanceRegions.Covers(shown, EnemyAppearanceRegions.Classify(name(bone))))
            {
                slots[index] = EnemyBoneSlot<TBone>.Anchor(shown);
                continue;
            }
            var landed = Map(bone, target, name, parent, out bool ancestor);
            if (landed == null) { slots[index] = EnemyBoneSlot<TBone>.Anchor(shown); continue; }
            slots[index] = EnemyBoneSlot<TBone>.Real(landed);
            mapped++;
            if (ancestor) viaAncestor++;
        }
        return slots;
    }

    /// <summary>The target's own surfaces with the replaced regions hidden: each bone of a replaced region goes to the
    /// anchor under that region's boundary (for `Full`, every bone goes under the root core bone).</summary>
    internal static EnemyBoneSlot<TBone>[] Hide<TBone>(IReadOnlyList<EnemyBoneSlot<TBone>> slots,
        IReadOnlyCollection<EnemyAppearanceRegion> replaced, Func<TBone, string> name, out int hidden) where TBone : class
    {
        hidden = 0;
        bool full = Contains(replaced, EnemyAppearanceRegion.Full);
        var result = new EnemyBoneSlot<TBone>[slots.Count];
        for (int index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            result[index] = slot;
            if (slot.Kind != EnemyBoneSlotKind.Bone) continue;
            if (full) { result[index] = EnemyBoneSlot<TBone>.Anchor(EnemyAppearanceRegion.Full); hidden++; continue; }
            var region = EnemyAppearanceRegions.Classify(name(slot.Bone!));
            if (!Contains(replaced, region)) continue;
            result[index] = EnemyBoneSlot<TBone>.Anchor(region);
            hidden++;
        }
        return result;
    }

    /// <summary>Whether a renderer currently shows any of <paramref name="region"/>.</summary>
    internal static bool Shows<TBone>(IReadOnlyList<EnemyBoneSlot<TBone>> slots, EnemyAppearanceRegion region,
        Func<TBone, string> name) where TBone : class
    {
        foreach (var slot in slots)
            if (slot.Kind == EnemyBoneSlotKind.Bone && EnemyAppearanceRegions.Covers(region, EnemyAppearanceRegions.Classify(name(slot.Bone!))))
                return true;
        return false;
    }

    /// <summary>Whether every bone a renderer currently shows lies in <paramref name="region"/>, in which case a reskin
    /// of that region is a material swap on the renderer itself rather than a split.</summary>
    internal static bool OnlyShows<TBone>(IReadOnlyList<EnemyBoneSlot<TBone>> slots, EnemyAppearanceRegion region,
        Func<TBone, string> name) where TBone : class
    {
        foreach (var slot in slots)
            if (slot.Kind == EnemyBoneSlotKind.Bone && !EnemyAppearanceRegions.Covers(region, EnemyAppearanceRegions.Classify(name(slot.Bone!))))
                return false;
        return true;
    }

    /// <summary>A reskin split: the copy shows only <paramref name="region"/> (everything else to that region's anchor)
    /// and the original stops showing it.</summary>
    internal static (EnemyBoneSlot<TBone>[] Copy, EnemyBoneSlot<TBone>[] Original) Split<TBone>(
        IReadOnlyList<EnemyBoneSlot<TBone>> slots, EnemyAppearanceRegion region, Func<TBone, string> name) where TBone : class
    {
        var copy = new EnemyBoneSlot<TBone>[slots.Count];
        var original = new EnemyBoneSlot<TBone>[slots.Count];
        for (int index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            if (slot.Kind == EnemyBoneSlotKind.Empty) { copy[index] = slot; original[index] = slot; continue; }
            bool inRegion = slot.Kind == EnemyBoneSlotKind.Bone
                && EnemyAppearanceRegions.Classify(name(slot.Bone!)) == region;
            copy[index] = inRegion ? slot : EnemyBoneSlot<TBone>.Anchor(region);
            original[index] = inRegion ? EnemyBoneSlot<TBone>.Anchor(region) : slot;
        }
        return (copy, original);
    }

    private static bool Contains(IReadOnlyCollection<EnemyAppearanceRegion> set, EnemyAppearanceRegion region)
    {
        foreach (var item in set) if (item == region) return true;
        return false;
    }
}

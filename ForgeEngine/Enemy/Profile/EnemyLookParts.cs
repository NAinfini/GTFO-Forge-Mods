using System;
using System.Collections.Generic;

namespace ForgeEnemy.Profile;

/// <summary>Where a part look lands. `Full` is the whole body; the six humanoid regions, `Neck` (the head region)
/// and `Core` (the body of a model that has no humanoid regions) are body targets; the two appendages and the eight
/// tumours are whole renderers. The spellings are the website's (`forge.enemyAppearance.Looks.Target`).</summary>
internal enum EnemyLookTarget
{
    Full, Head, Torso, LeftArm, RightArm, LeftLeg, RightLeg, Core, LeftAppendage, RightAppendage, Neck,
    Tumor1, Tumor2, Tumor3, Tumor4, Tumor5, Tumor6, Tumor7, Tumor8
}

internal static class EnemyLookTargets
{
    internal const int TumorCount = 8;

    private static readonly string[] Spellings =
    {
        "full", "head", "torso", "leftArm", "rightArm", "leftLeg", "rightLeg", "core", "leftAppendage", "rightAppendage",
        "neck", "tumor1", "tumor2", "tumor3", "tumor4", "tumor5", "tumor6", "tumor7", "tumor8"
    };

    internal static string Name(EnemyLookTarget target) => Spellings[(int)target];

    internal static bool TryParse(string? text, out EnemyLookTarget target)
    {
        target = EnemyLookTarget.Full;
        if (text == null) return false;
        for (int index = 0; index < Spellings.Length; index++)
            if (string.Equals(Spellings[index], text, StringComparison.Ordinal)) { target = (EnemyLookTarget)index; return true; }
        return false;
    }

    internal static bool IsTumor(EnemyLookTarget target) => target >= EnemyLookTarget.Tumor1;

    internal static EnemyLookTarget Tumor(int number) => EnemyLookTarget.Tumor1 + (number - 1);
}

/// <summary>One `Looks` entry, checked: the target, the surface colour, the fixed glow and whether it is invisible (each optional,
/// at least one set), and the glow's brightness (1 when the author left it out). Colours are sRGB, non-negative, rgb, as every
/// other surface look (ForgeVisual `SurfaceAppearance`, which the native pass reads them into).</summary>
internal sealed class EnemyPartLook
{
    internal EnemyPartLook(EnemyLookTarget target) { Target = target; }

    internal EnemyLookTarget Target { get; }
    internal float[]? Color { get; set; }
    internal float[]? Glow { get; set; }
    internal float GlowBrightness { get; set; } = 1f;
    /// <summary>True: only the part's shadow is drawn; false: shown, overriding the whole body's true; null keeps it as it is.</summary>
    internal bool? Invisible { get; set; }
}

/// <summary>What one surface ends up with after the whole-body entry and its own entry are merged per component:
/// the website preview's `{...whole, ...own}` rule. A null member is left as the game has it. An invisible surface draws only its
/// shadow (the game's shadow enemies); a removed one (`Limbs.Removed`, or replaced by a placement) is not drawn at all, shadow
/// included, whatever its look says.</summary>
internal sealed class EnemyLookEffect
{
    internal static readonly EnemyLookEffect Gone = new(null, null, 1f, null, true);

    internal EnemyLookEffect(float[]? color, float[]? glow, float brightness, bool? invisible = null, bool removed = false)
    { Color = color; Glow = glow; Brightness = brightness; Invisible = invisible == true; Removed = removed; }

    internal float[]? Color { get; }
    internal float[]? Glow { get; }
    internal float Brightness { get; }
    internal bool Invisible { get; }
    internal bool Removed { get; }

    /// <summary>The surface's body is not drawn (only its shadow when invisible, nothing when removed).</summary>
    internal bool Off => Invisible || Removed;

    /// <summary>The surface takes a new material (a colour or a glow).</summary>
    internal bool Shades => !Off && (Color != null || Glow != null);

    /// <summary>Two effects take a body out the same way.</summary>
    internal bool SameOff(EnemyLookEffect other) => Off && other.Off && Removed == other.Removed;
}

/// <summary>The merge and the body split plan, from the checked entries alone.</summary>
internal static class EnemyPartLooks
{
    /// <summary>`full`, then each target of <paramref name="chain"/> in order, each overriding per component; <see cref="EnemyLookEffect.Gone"/>
    /// when a target of the chain is in <paramref name="gone"/>; null when nothing is left to write.</summary>
    internal static EnemyLookEffect? Effective(IReadOnlyList<EnemyPartLook> looks, ICollection<EnemyLookTarget> gone, params EnemyLookTarget[] chain)
    {
        foreach (var target in chain) if (gone.Contains(target)) return EnemyLookEffect.Gone;
        float[]? color = null, glow = null;
        float brightness = 1f;
        bool? invisible = null;
        void Take(EnemyLookTarget target)
        {
            foreach (var look in looks)
            {
                if (look.Target != target) continue;
                if (look.Color != null) color = look.Color;
                if (look.Glow != null) { glow = look.Glow; brightness = look.GlowBrightness; }
                if (look.Invisible != null) invisible = look.Invisible;
            }
        }
        Take(EnemyLookTarget.Full);
        foreach (var target in chain) Take(target);
        return color == null && glow == null && invisible == null ? null : new EnemyLookEffect(color, glow, brightness, invisible);
    }

    internal static bool Has(IReadOnlyList<EnemyPartLook> looks, EnemyLookTarget target)
    {
        foreach (var look in looks) if (look.Target == target) return true;
        return false;
    }
}

/// <summary>Which native limbs take no hit. Invisibility only decides drawing (an invisible part still casts its shadow and takes
/// hits, as the game's own shadow enemies do); a part takes no hit only when it is removed (`Limbs.Removed`) or a placement
/// replaces it.</summary>
internal static class EnemyHitVisibility
{
    /// <summary>The native limb ids of the removed or replaced parts.</summary>
    internal static List<int> HiddenLimbs(EnemyLimbResolution resolution, ICollection<EnemyLimbPart> gone)
    {
        var hidden = new List<int>();
        foreach (var (part, ids) in resolution.Parts) if (gone.Contains(part)) hidden.AddRange(ids);
        hidden.Sort();
        return hidden;
    }
}

/// <summary>How a body renderer is split: the surface that takes the whole-body look (`full` merged with `core`)
/// and, per humanoid region that has an entry of its own, the look that region ends with. A region without an entry
/// belongs to the rest surface, so a body with only a whole-body look is not split at all.</summary>
internal sealed class EnemyBodyLookPlan
{
    private static readonly EnemyAppearanceRegion[] Order =
    {
        EnemyAppearanceRegion.Head, EnemyAppearanceRegion.Torso, EnemyAppearanceRegion.LeftArm,
        EnemyAppearanceRegion.RightArm, EnemyAppearanceRegion.LeftLeg, EnemyAppearanceRegion.RightLeg
    };

    private readonly int[] _groupOfRegion = new int[(int)EnemyAppearanceRegion.RightLeg + 1];

    private EnemyBodyLookPlan(EnemyLookEffect? rest, List<(EnemyAppearanceRegion Region, EnemyLookEffect Look)> regions)
    {
        Rest = rest;
        Regions = regions;
        for (int index = 0; index < regions.Count; index++) _groupOfRegion[(int)regions[index].Region] = index + 1;
    }

    /// <summary>The look of every region without an entry of its own (null: they keep the game's).</summary>
    internal EnemyLookEffect? Rest { get; }
    internal IReadOnlyList<(EnemyAppearanceRegion Region, EnemyLookEffect Look)> Regions { get; }
    internal bool NeedsSplit => Regions.Count > 0;

    /// <summary>Every surface is taken out the same way (all invisible, or all removed): the whole renderer is, no split needed.</summary>
    internal bool AllOff
    {
        get
        {
            if (Rest is not { Off: true } rest) return false;
            foreach (var (_, look) in Regions) if (!look.SameOff(rest)) return false;
            return true;
        }
    }

    /// <summary>The surface group of a region: 0 is the rest surface, 1.. are <see cref="Regions"/> in order.</summary>
    internal int GroupOfRegion(EnemyAppearanceRegion region) => _groupOfRegion[(int)region];

    internal int GroupCount => Regions.Count + 1;

    /// <summary>The look of one group: <see cref="Rest"/> for 0, otherwise that region's.</summary>
    internal EnemyLookEffect? LookOf(int group) => group == 0 ? Rest : Regions[group - 1].Look;

    /// <summary>The split of the body for <paramref name="looks"/>; a region in <paramref name="gone"/> (its part removed or replaced)
    /// is a group of its own that is not drawn.</summary>
    internal static EnemyBodyLookPlan Plan(IReadOnlyList<EnemyPartLook> looks, ICollection<EnemyLookTarget> gone)
    {
        var rest = EnemyPartLooks.Effective(looks, gone, EnemyLookTarget.Core);
        var regions = new List<(EnemyAppearanceRegion, EnemyLookEffect)>();
        foreach (var region in Order)
        {
            bool own = region == EnemyAppearanceRegion.Head
                ? EnemyPartLooks.Has(looks, EnemyLookTarget.Head) || EnemyPartLooks.Has(looks, EnemyLookTarget.Neck) || gone.Contains(EnemyLookTarget.Head)
                : EnemyPartLooks.Has(looks, RegionTarget(region)) || gone.Contains(RegionTarget(region));
            if (!own) continue;
            // The head region is gone with the head; a removed neck alone leaves the head drawn.
            var effect = region == EnemyAppearanceRegion.Head
                ? gone.Contains(EnemyLookTarget.Head) ? EnemyLookEffect.Gone
                    : EnemyPartLooks.Effective(looks, Array.Empty<EnemyLookTarget>(), EnemyLookTarget.Core, EnemyLookTarget.Head, EnemyLookTarget.Neck)
                : EnemyPartLooks.Effective(looks, gone, EnemyLookTarget.Core, RegionTarget(region));
            if (effect != null) regions.Add((region, effect));
        }
        return new EnemyBodyLookPlan(rest, regions);
    }

    private static EnemyLookTarget RegionTarget(EnemyAppearanceRegion region) => region switch
    {
        EnemyAppearanceRegion.Head => EnemyLookTarget.Head,
        EnemyAppearanceRegion.LeftArm => EnemyLookTarget.LeftArm,
        EnemyAppearanceRegion.RightArm => EnemyLookTarget.RightArm,
        EnemyAppearanceRegion.LeftLeg => EnemyLookTarget.LeftLeg,
        EnemyAppearanceRegion.RightLeg => EnemyLookTarget.RightLeg,
        _ => EnemyLookTarget.Torso
    };
}

/// <summary>What kind of surface a skinned renderer is, by its name: the model's tumours are the `EggSack*`
/// renderers (numbered by the placement order), an appendage is a renderer named for a tentacle or appendage, and
/// everything else is the body.</summary>
internal enum EnemyRendererRole { Body, Tumor, Appendage }

internal static class EnemyRendererRoles
{
    // The native NanoSwarm is an auxiliary VAT particle renderer, not a body surface. Its source shader must stay
    // intact when the body is recoloured; other unknown surfaces still take the explicit shader refusal path.
    internal static bool IsAuxiliaryEffect(string? rendererName)
        => rendererName?.StartsWith("FX_NanoSwarm_", StringComparison.Ordinal) == true;

    internal static EnemyRendererRole Of(string? rendererName)
    {
        if (EnemyPlacementOrder.IsTumorRenderer(rendererName)) return EnemyRendererRole.Tumor;
        string lower = (rendererName ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("tentacle", StringComparison.Ordinal) || lower.Contains("appendage", StringComparison.Ordinal))
            return EnemyRendererRole.Appendage;
        return EnemyRendererRole.Body;
    }

    /// <summary>The side of an appendage renderer by where its bounds centre lies in the model's own frame:
    /// negative x is the left side.</summary>
    internal static EnemyLookTarget AppendageSide(float centerX)
        => centerX < 0f ? EnemyLookTarget.LeftAppendage : EnemyLookTarget.RightAppendage;
}

/// <summary>`forge.eec.Model.Marker.Icon`: the shape a tagged enemy's marker takes. The game has no sprite for a
/// diamond, so it is the square sprite turned 45 degrees and shrunk to the square's diagonal.</summary>
internal enum EnemyMarkerIcon { Vanilla, Triangle, Circle, Square, Diamond }

internal static class EnemyMarkerIcons
{
    /// <summary>The sprite the game's enemy marker shows (a hollow triangle).</summary>
    internal const string VanillaSprite = "symbol_hostile";

    internal static bool TryParse(string? text, out EnemyMarkerIcon icon)
    {
        switch (text)
        {
            case "vanilla": icon = EnemyMarkerIcon.Vanilla; return true;
            case "triangle": icon = EnemyMarkerIcon.Triangle; return true;
            case "circle": icon = EnemyMarkerIcon.Circle; return true;
            case "square": icon = EnemyMarkerIcon.Square; return true;
            case "diamond": icon = EnemyMarkerIcon.Diamond; return true;
            default: icon = EnemyMarkerIcon.Vanilla; return false;
        }
    }

    internal static string Name(EnemyMarkerIcon icon) => icon switch
    {
        EnemyMarkerIcon.Triangle => "triangle",
        EnemyMarkerIcon.Circle => "circle",
        EnemyMarkerIcon.Square => "square",
        EnemyMarkerIcon.Diamond => "diamond",
        _ => "vanilla"
    };

    /// <summary>The game's sprite asset for an icon (null for the vanilla one, which is already on the marker).</summary>
    internal static string? SpriteName(EnemyMarkerIcon icon) => icon switch
    {
        EnemyMarkerIcon.Triangle => "cellUI_SmallTriangleFill",
        EnemyMarkerIcon.Circle => "CircleBigMiddleSize_Fill",
        EnemyMarkerIcon.Square or EnemyMarkerIcon.Diamond => "GUIX_Detail_BoxFilled_AA_Centered",
        _ => null
    };

    /// <summary>Degrees about the view axis: a diamond is a turned square.</summary>
    internal static float TurnDegrees(EnemyMarkerIcon icon) => icon == EnemyMarkerIcon.Diamond ? 45f : 0f;

    /// <summary>The extra scale that keeps a turned square's corners where the square's sides were.</summary>
    internal static float FitScale(EnemyMarkerIcon icon) => icon == EnemyMarkerIcon.Diamond ? 0.70710677f : 1f;
}

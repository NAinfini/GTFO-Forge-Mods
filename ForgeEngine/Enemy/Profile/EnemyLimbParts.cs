using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeEnemy.Profile;

/// <summary>The kind of body part a limb profile names. `Tumor` carries its own number, so the eight numbered
/// EggSack sacs are eight distinct parts rather than one kind with an index the reader could forget to check.</summary>
internal enum EnemyLimbPartKind
{
    Head,
    Neck,
    Torso,
    LeftArm,
    RightArm,
    LeftLeg,
    RightLeg,
    Tumor,
    Native
}

/// <summary>One named part of an enemy body, as the website's enemy editor writes it. It is a value rather than a
/// string because the profile is read once and then indexed per limb at spawn and per hit at run time: a part that
/// stayed text would mean a string compare in the damage path, and the whole point of the document is that the
/// game side resolved the author's word to native limbs before the level started.</summary>
internal readonly struct EnemyLimbPart : IEquatable<EnemyLimbPart>
{
    /// <summary>The highest tumor number the document grammar accepts (`tumor1`..`tumor8`).</summary>
    internal const int MaximumTumors = 8;

    private readonly int _index;
    private EnemyLimbPart(EnemyLimbPartKind kind, int index) { Kind = kind; _index = index; }

    internal EnemyLimbPartKind Kind { get; }

    /// <summary>The 1-based sac number for <see cref="EnemyLimbPartKind.Tumor"/>, zero for every other kind.</summary>
    internal int Tumor => Kind == EnemyLimbPartKind.Tumor ? _index : 0;
    internal int NativeId => Kind == EnemyLimbPartKind.Native ? _index : -1;

    internal static EnemyLimbPart Head => new(EnemyLimbPartKind.Head, 0);
    internal static EnemyLimbPart Neck => new(EnemyLimbPartKind.Neck, 0);
    internal static EnemyLimbPart Torso => new(EnemyLimbPartKind.Torso, 0);
    internal static EnemyLimbPart LeftArm => new(EnemyLimbPartKind.LeftArm, 0);
    internal static EnemyLimbPart RightArm => new(EnemyLimbPartKind.RightArm, 0);
    internal static EnemyLimbPart LeftLeg => new(EnemyLimbPartKind.LeftLeg, 0);
    internal static EnemyLimbPart RightLeg => new(EnemyLimbPartKind.RightLeg, 0);
    internal static EnemyLimbPart TumorAt(int number) => new(EnemyLimbPartKind.Tumor, number);
    internal static EnemyLimbPart NativeAt(int id) => new(EnemyLimbPartKind.Native, id);

    /// <summary>The wire spelling, which is also the website's.</summary>
    internal string Name => Kind switch
    {
        EnemyLimbPartKind.Head => "head",
        EnemyLimbPartKind.Neck => "neck",
        EnemyLimbPartKind.Torso => "torso",
        EnemyLimbPartKind.LeftArm => "leftArm",
        EnemyLimbPartKind.RightArm => "rightArm",
        EnemyLimbPartKind.LeftLeg => "leftLeg",
        EnemyLimbPartKind.RightLeg => "rightLeg",
        EnemyLimbPartKind.Tumor => "tumor" + Tumor.ToString(CultureInfo.InvariantCulture),
        _ => "native_" + NativeId.ToString(CultureInfo.InvariantCulture)
    };

    internal static bool TryParse(string? text, out EnemyLimbPart part)
    {
        switch (text)
        {
            case "head": part = Head; return true;
            case "neck": part = Neck; return true;
            case "torso": part = Torso; return true;
            case "leftArm": part = LeftArm; return true;
            case "rightArm": part = RightArm; return true;
            case "leftLeg": part = LeftLeg; return true;
            case "rightLeg": part = RightLeg; return true;
            default:
                if (text != null && text.StartsWith("native_", StringComparison.Ordinal)
                    && int.TryParse(text.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                    && id >= 0 && text == "native_" + id.ToString(CultureInfo.InvariantCulture))
                { part = NativeAt(id); return true; }
                // One digit, 1..8, no padding: `tumor01` and `tumor0` are not spellings of a sac, and the strict
                // grammar is what lets the numbered renderers be a finite table rather than a range.
                if (text is { Length: 6 } && text.StartsWith("tumor", StringComparison.Ordinal)
                    && text[5] is >= '1' and <= '8')
                {
                    part = TumorAt(text[5] - '0');
                    return true;
                }
                part = default;
                return false;
        }
    }

    public bool Equals(EnemyLimbPart other) => Kind == other.Kind && _index == other._index;
    public override bool Equals(object? obj) => obj is EnemyLimbPart other && Equals(other);
    public override int GetHashCode() => ((int)Kind * 397) ^ _index;
    public override string ToString() => Name;
}

/// <summary>One limb of a built enemy prefab, as the resolver reads it. Everything the resolver needs is already a
/// plain value here: which native limb type it is, the name of the nearest skinned bone above it (null when no
/// ancestor is a bone the model's own renderers skin), and how far it stands from each numbered EggSack renderer.
/// The game side fills this from the prefab; the pure rules below never touch a Unity type.</summary>
internal sealed class EnemyLimbCandidate
{
    internal EnemyLimbCandidate(int limbId, string name) { LimbId = limbId; Name = name; }

    /// <summary>The native `m_limbID`, which is the id a hit names and the index a rule table is keyed by.</summary>
    internal int LimbId { get; }

    /// <summary>The limb gameObject's name, for the one unresolved line per type.</summary>
    internal string Name { get; }

    /// <summary>`Dam_EnemyDamageLimb_Custom` is the sac limb; only these are paired with EggSack renderers.</summary>
    internal bool IsTumor { get; init; }

    /// <summary>The nearest skinned-bone ancestor's name, or null when the limb has none.</summary>
    internal string? BoneName { get; init; }

    /// <summary>Distance to each EggSack renderer, in the numbered order; null for a limb that is not a tumor.</summary>
    internal float[]? TumorDistances { get; init; }
}

/// <summary>What one enemy type's limbs resolved to: the limbs each named part owns, and the limbs no part could
/// claim. It is cached per enemy type because a prefab is built once per type and every spawn of that type has the
/// same limbs — the spawn path then writes native fields through this table instead of resolving again.</summary>
internal sealed class EnemyLimbResolution
{
    private readonly Dictionary<EnemyLimbPart, List<int>> _parts;
    private readonly Dictionary<int, List<int>> _native;

    private EnemyLimbResolution(Dictionary<EnemyLimbPart, List<int>> parts, IReadOnlyList<EnemyLimbCandidate> unresolved,
        Dictionary<int, List<int>> native)
    {
        _parts = parts;
        _native = native;
        Unresolved = unresolved;
    }

    internal static EnemyLimbResolution Empty { get; } =
        new(new Dictionary<EnemyLimbPart, List<int>>(), Array.Empty<EnemyLimbCandidate>(), new Dictionary<int, List<int>>());

    /// <summary>The limbs no part claims, in the order the damage array declares them.</summary>
    internal IReadOnlyList<EnemyLimbCandidate> Unresolved { get; }

    /// <summary>`part → limb ids`, which is the whole lookup the spawn path and the rule table need.</summary>
    internal IReadOnlyDictionary<EnemyLimbPart, List<int>> Parts => _parts;
    internal int LimbCount
    {
        get
        {
            int count = 0;
            foreach (var id in _native.Keys) if (id >= count) count = id + 1;
            return count;
        }
    }

    internal bool TryLimbIds(EnemyLimbPart part, out List<int> ids) => part.Kind == EnemyLimbPartKind.Native
        ? _native.TryGetValue(part.NativeId, out ids!) : _parts.TryGetValue(part, out ids!);

    internal static EnemyLimbResolution Build(Dictionary<EnemyLimbPart, List<int>> parts, IReadOnlyList<EnemyLimbCandidate> unresolved,
        Dictionary<int, List<int>> native) => new(parts, unresolved, native);
}

/// <summary>The pure limb-part rules: the bone-name classifier, the EggSack name order and the sac pairing. They
/// are game-independent so the EnemyProfile suite can state them without a GTFO process, and the native side feeds
/// them values it read from the prefab.</summary>
internal static class EnemyLimbParts
{
    /// <summary>The whole type's tumors were refused: two sac limbs stood nearest to one renderer, or a tumor
    /// limb had no renderer to pair with, so no sac can be numbered without guessing.</summary>
    internal const string TumorAmbiguous = "limb-tumor-ambiguous";

    /// <summary>The part a skinned bone belongs to. `neck` is the one part the appearance regions do not name —
    /// that table folds the neck into the head — and it is decided first because `Neck` contains `neck` while the
    /// appearance rules would call it a head bone.</summary>
    internal static EnemyLimbPart Classify(string? boneName)
    {
        if (boneName != null && boneName.Contains("neck", StringComparison.OrdinalIgnoreCase)) return EnemyLimbPart.Neck;
        return FromRegion(EnemyAppearanceRegions.Classify(boneName));
    }

    private static EnemyLimbPart FromRegion(EnemyAppearanceRegion region) => region switch
    {
        EnemyAppearanceRegion.Head => EnemyLimbPart.Head,
        EnemyAppearanceRegion.LeftArm => EnemyLimbPart.LeftArm,
        EnemyAppearanceRegion.RightArm => EnemyLimbPart.RightArm,
        EnemyAppearanceRegion.LeftLeg => EnemyLimbPart.LeftLeg,
        EnemyAppearanceRegion.RightLeg => EnemyLimbPart.RightLeg,
        _ => EnemyLimbPart.Torso
    };

    /// <summary>The renderer order: the same natural order the website sorts names with
    /// (`localeCompare(..., {numeric:true})`), so `EggSack2` precedes `EggSack10` on both sides. Digit runs compare
    /// by value with leading zeros ignored; everything else compares case-insensitively (lower-cased, so _ sorts before
    /// letters as it does in the website's collation), and the whole string breaks a tie ordinally, so two spellings that
    /// differ only in case or in leading zeros still have one stable order. The one copy: placement orders its renderers
    /// through it too.</summary>
    internal static int NaturalCompare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left == null) return -1;
        if (right == null) return 1;
        int l = 0, r = 0;
        while (l < left.Length && r < right.Length)
        {
            bool leftDigit = left[l] is >= '0' and <= '9';
            bool rightDigit = right[r] is >= '0' and <= '9';
            if (leftDigit && rightDigit)
            {
                int leftEnd = l, rightEnd = r;
                while (leftEnd < left.Length && left[leftEnd] is >= '0' and <= '9') leftEnd++;
                while (rightEnd < right.Length && right[rightEnd] is >= '0' and <= '9') rightEnd++;
                int leftFirst = l, rightFirst = r;
                while (leftFirst < leftEnd - 1 && left[leftFirst] == '0') leftFirst++;
                while (rightFirst < rightEnd - 1 && right[rightFirst] == '0') rightFirst++;
                int leftDigits = leftEnd - leftFirst, rightDigits = rightEnd - rightFirst;
                if (leftDigits != rightDigits) return leftDigits < rightDigits ? -1 : 1;
                for (int offset = 0; offset < leftDigits; offset++)
                {
                    int difference = left[leftFirst + offset] - right[rightFirst + offset];
                    if (difference != 0) return difference < 0 ? -1 : 1;
                }
                l = leftEnd;
                r = rightEnd;
                continue;
            }
            int folded = char.ToLowerInvariant(left[l]) - char.ToLowerInvariant(right[r]);
            if (folded != 0) return folded < 0 ? -1 : 1;
            l++;
            r++;
        }
        if (l < left.Length) return 1;
        if (r < right.Length) return -1;
        return string.CompareOrdinal(left, right);
    }

    /// <summary>Resolves one type's limbs into `part → limb ids`. The sac limbs are numbered first because their
    /// numbers come from the renderer order, and a pairing that is not one-to-one refuses every sac of the type
    /// while the bone-named parts still resolve — a sac ambiguity says nothing about a leg.
    ///
    /// <paramref name="failure"/> carries the reason code when the sacs were refused, so the caller can report it
    /// once per type; the returned resolution is always usable for the parts that did resolve.</summary>
    internal static EnemyLimbResolution Resolve(IReadOnlyList<EnemyLimbCandidate> limbs, out string? failure)
    {
        failure = null;
        var parts = new Dictionary<EnemyLimbPart, List<int>>();
        var native = new Dictionary<int, List<int>>();
        var ambiguous = new HashSet<int>();
        foreach (var limb in limbs)
        {
            if (limb.LimbId < 0) continue;
            if (native.ContainsKey(limb.LimbId) || ambiguous.Contains(limb.LimbId))
            { native.Remove(limb.LimbId); ambiguous.Add(limb.LimbId); failure = "limb-id-ambiguous"; }
            else native.Add(limb.LimbId, new List<int> { limb.LimbId });
        }
        string? tumorFailure = null;
        PairTumors(limbs, parts, ref tumorFailure);
        failure ??= tumorFailure;
        var unresolved = new List<EnemyLimbCandidate>();
        foreach (var limb in limbs)
        {
            if (limb.IsTumor) continue;
            if (limb.BoneName == null) { unresolved.Add(limb); continue; }
            Add(parts, Classify(limb.BoneName), limb.LimbId);
        }
        return EnemyLimbResolution.Build(parts, unresolved, native);
    }

    /// <summary>Pairs each sac limb with the numbered renderer it stands nearest to. The numbers are the sorted
    /// EggSack order, so a sac's part is stable across builds of the same model. A sac with no renderer, a renderer
    /// list that does not match its siblings, and two sacs claiming one renderer all refuse the whole set rather
    /// than resolving some sacs by order.</summary>
    private static void PairTumors(IReadOnlyList<EnemyLimbCandidate> limbs, Dictionary<EnemyLimbPart, List<int>> parts, ref string? failure)
    {
        var tumors = new List<EnemyLimbCandidate>();
        foreach (var limb in limbs) if (limb.IsTumor) tumors.Add(limb);
        if (tumors.Count == 0) return;
        int renderers = tumors[0].TumorDistances?.Length ?? 0;
        if (renderers == 0) { failure = TumorAmbiguous; return; }
        var taken = new bool[renderers];
        var paired = new List<(EnemyLimbPart Part, int LimbId)>();
        foreach (var tumor in tumors)
        {
            var distances = tumor.TumorDistances;
            if (distances == null || distances.Length != renderers) { failure = TumorAmbiguous; return; }
            int nearest = 0;
            bool tied = false;
            for (int index = 0; index < renderers; index++)
            {
                if (!float.IsFinite(distances[index]) || distances[index] < 0f) { failure = TumorAmbiguous; return; }
                if (distances[index] < distances[nearest]) { nearest = index; tied = false; }
                else if (index != nearest && distances[index] == distances[nearest]) tied = true;
            }
            if (tied || taken[nearest]) { failure = TumorAmbiguous; return; }
            taken[nearest] = true;
            paired.Add((EnemyLimbPart.TumorAt(nearest + 1), tumor.LimbId));
        }
        foreach (var (part, limbId) in paired) Add(parts, part, limbId);
    }

    private static void Add(Dictionary<EnemyLimbPart, List<int>> parts, EnemyLimbPart part, int limbId)
    {
        if (!parts.TryGetValue(part, out var ids)) parts.Add(part, ids = new List<int>());
        ids.Add(limbId);
    }
}

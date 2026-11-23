using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeEnemy.Profile;

/// <summary>One slot a `forge.eec.Model.Bone` `Bones` entry can place when its `Bone` is a slot name: the whole
/// model (`full`), one of the six humanoid regions, or one numbered EggSack tumor. The names are the website's own
/// (`ENEMY_PLACEMENT_TARGETS` in `Web/src/forge/enemy/enemy-editor-model.ts`), so the target an author picks in the
/// preview is the name this reader accepts and no other spelling is a second name for the same slot.</summary>
internal readonly struct EnemyPlacementSlot : IEquatable<EnemyPlacementSlot>
{
    private EnemyPlacementSlot(EnemyAppearanceRegion region, int tumor) { Region = region; Tumor = tumor; }

    private const string TumorPrefix = "tumor";

    /// <summary>`full` for the whole model and for a tumor; the two are told apart by <see cref="Tumor"/>.</summary>
    internal EnemyAppearanceRegion Region { get; }

    /// <summary>The 1-based EggSack number, or zero for `full` and the six regions.</summary>
    internal int Tumor { get; }

    internal bool IsTumor => Tumor > 0;
    internal bool IsWhole => Tumor == 0 && Region == EnemyAppearanceRegion.Full;

    internal static EnemyPlacementSlot Whole { get; } = new(EnemyAppearanceRegion.Full, 0);
    internal static EnemyPlacementSlot Of(EnemyAppearanceRegion region) => new(region, 0);
    internal static EnemyPlacementSlot OfTumor(int tumor) => new(EnemyAppearanceRegion.Full, tumor);

    /// <summary>The wire spelling, which is also the website's.</summary>
    internal string Name => IsTumor
        ? TumorPrefix + Tumor.ToString(CultureInfo.InvariantCulture)
        : EnemyAppearanceRegions.Name(Region);

    internal static bool TryParse(string? text, out EnemyPlacementSlot slot)
    {
        slot = Whole;
        if (text == null) return false;
        if (EnemyAppearanceRegions.TryParse(text, out var region)) { slot = Of(region); return true; }
        if (!text.StartsWith(TumorPrefix, StringComparison.Ordinal)) return false;
        string number = text[TumorPrefix.Length..];
        // `tumor01` and `tumor+1` are second spellings of a slot the document already has one name for; refused so
        // a key a reader cannot see is never silently the same slot as one it can.
        if (number.Length == 0 || (number.Length > 1 && number[0] == '0')
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return false;
        if (index < 1 || index > EnemyLimbPart.MaximumTumors) return false;
        slot = OfTumor(index);
        return true;
    }

    public bool Equals(EnemyPlacementSlot other) => Region == other.Region && Tumor == other.Tumor;
    public override bool Equals(object? other) => other is EnemyPlacementSlot slot && Equals(slot);
    public override int GetHashCode() => ((int)Region * 397) ^ Tumor;
    public override string ToString() => Name;
}

/// <summary>One placement entry's transform, already in Unity model-local coordinates: the website converts the
/// preview frame at export (`previewToUnityTransform` in `Web/src/forge/enemy/enemy-native-blocks.ts`), so the
/// game side applies the numbers as they arrive and never converts a frame of its own.</summary>
internal sealed class EnemyPlacementTransform
{
    internal EnemyPlacementTransform(float[] position, float[] rotation, float[] scale)
    { Position = position; Rotation = rotation; Scale = scale; }

    /// <summary>The quaternion `[x, y, z, w]` of Unity Euler angles in degrees, exactly as
    /// `Quaternion.Euler(x, y, z)` builds it: a turn about Z, then X, then Y (`q = qy · qx · qz`), which is the
    /// order EEC's `RotationOffset` is applied in.</summary>
    internal static float[] FromEuler(float[] degrees)
    {
        double half = Math.PI / 360d;
        double sx = Math.Sin(degrees[0] * half), cx = Math.Cos(degrees[0] * half);
        double sy = Math.Sin(degrees[1] * half), cy = Math.Cos(degrees[1] * half);
        double sz = Math.Sin(degrees[2] * half), cz = Math.Cos(degrees[2] * half);
        return new[]
        {
            (float)(cy * sx * cz + sy * cx * sz),
            (float)(sy * cx * cz - cy * sx * sz),
            (float)(cy * cx * sz - sy * sx * cz),
            (float)(cy * cx * cz + sy * sx * sz)
        };
    }

    /// <summary>Model-local position offset, three finite numbers.</summary>
    internal float[] Position { get; }

    /// <summary>Model-local rotation as a unit quaternion, `[x, y, z, w]`.</summary>
    internal float[] Rotation { get; }

    /// <summary>Model-local scale multiplier, three finite numbers greater than zero.</summary>
    internal float[] Scale { get; }

    /// <summary>The matrix this transform stands for, in the same frame as the model root.</summary>
    internal EnemyPlacementMatrix Matrix => EnemyPlacementMatrix.Trs(Position, Rotation, Scale);
}

/// <summary>One accepted placement entry: its target and its transform. The list keeps the document's own order,
/// which nothing reads: the reader refuses a repeated slot, so no two entries can disagree about a target.</summary>
internal sealed record EnemyPlacementEntry(EnemyPlacementSlot Slot, EnemyPlacementTransform Transform);

/// <summary>The slot placements of `forge.eec.Model.Bone`, keyed by slot. `HasRegions` and `HasTumors` are what
/// the applier checks before it touches a renderer: a document that only moves the whole model has no mesh work to
/// do.</summary>
internal sealed class EnemyPlacementPlan
{
    private readonly Dictionary<EnemyPlacementSlot, EnemyPlacementTransform> _bySlot = new();
    private readonly List<EnemyPlacementEntry> _entries = new();

    internal IReadOnlyList<EnemyPlacementEntry> Entries => _entries;
    internal bool HasRegions { get; private set; }
    internal bool HasTumors { get; private set; }

    internal void Add(EnemyPlacementEntry entry)
    {
        _bySlot.Add(entry.Slot, entry.Transform);
        _entries.Add(entry);
        if (entry.Slot.IsTumor) HasTumors = true;
        else if (!entry.Slot.IsWhole) HasRegions = true;
    }

    internal bool TryGet(EnemyPlacementSlot slot, out EnemyPlacementTransform transform)
        => _bySlot.TryGetValue(slot, out transform!);
    internal bool TryGet(EnemyAppearanceRegion region, out EnemyPlacementTransform transform)
        => TryGet(EnemyPlacementSlot.Of(region), out transform);
    internal bool TryGetTumor(int tumor, out EnemyPlacementTransform transform)
        => TryGet(EnemyPlacementSlot.OfTumor(tumor), out transform);
}

/// <summary>
/// The placement math, game-independent: a 4×4 matrix in the same layout and convention as
/// <c>UnityEngine.Matrix4x4</c> — row-major logical indexing, a column vector product (<c>v' = M·v</c>) and the
/// translation in column three. It exists so the one derivation the game side performs can be stated and checked
/// without the game: a region's offset is baked into a mesh copy's bind poses as
/// <c>M_mesh = R⁻¹ · M · R</c>, where <c>M</c> is the offset in model-local coordinates and <c>R</c> maps the
/// renderer's own local space to the model's (<c>model.worldToLocalMatrix · renderer.localToWorldMatrix</c>).
/// Conjugating by <c>R</c> is what makes the offset a model-space move while the bind pose itself stays in mesh
/// space, so the rest pose moves by exactly <c>M</c> and animation carries the offset with the bone.
/// </summary>
internal readonly struct EnemyPlacementMatrix
{
    private const int Side = 4;
    private readonly float[] _values;

    private EnemyPlacementMatrix(float[] values) { _values = values; }

    internal static EnemyPlacementMatrix Identity { get; } = new(new[]
    {
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 0f, 0f, 1f
    });

    internal float this[int row, int column] => _values[row * Side + column];

    /// <summary>The sixteen components in row-major order, which is the order the Unity matrix's own
    /// <c>m00</c>..<c>m33</c> fields spell.</summary>
    internal float[] CopyRowMajor() => (float[])_values.Clone();

    internal static EnemyPlacementMatrix FromRowMajor(float[] values) => new((float[])values.Clone());

    /// <summary>The matrix of a position, a quaternion (`[x, y, z, w]`) and a scale: translate ∘ rotate ∘ scale,
    /// the order <c>UnityEngine.Matrix4x4.TRS</c> composes.</summary>
    internal static EnemyPlacementMatrix Trs(float[] position, float[] rotation, float[] scale)
    {
        float x = rotation[0], y = rotation[1], z = rotation[2], w = rotation[3];
        float xx = x * x, yy = y * y, zz = z * z;
        float xy = x * y, xz = x * z, yz = y * z;
        float wx = w * x, wy = w * y, wz = w * z;
        return new(new[]
        {
            (1f - 2f * (yy + zz)) * scale[0], (2f * (xy - wz)) * scale[1], (2f * (xz + wy)) * scale[2], position[0],
            (2f * (xy + wz)) * scale[0], (1f - 2f * (xx + zz)) * scale[1], (2f * (yz - wx)) * scale[2], position[1],
            (2f * (xz - wy)) * scale[0], (2f * (yz + wx)) * scale[1], (1f - 2f * (xx + yy)) * scale[2], position[2],
            0f, 0f, 0f, 1f
        });
    }

    internal static EnemyPlacementMatrix Multiply(EnemyPlacementMatrix left, EnemyPlacementMatrix right)
    {
        var product = new float[Side * Side];
        for (int row = 0; row < Side; row++)
            for (int column = 0; column < Side; column++)
            {
                float sum = 0f;
                for (int index = 0; index < Side; index++)
                    sum += left[row, index] * right[index, column];
                product[row * Side + column] = sum;
            }
        return new EnemyPlacementMatrix(product);
    }

    /// <summary>The inverse of a general 4×4 matrix, by Gauss-Jordan elimination with partial pivoting. Every
    /// matrix this file builds is affine and invertible; the general form is kept so a wrong argument is a
    /// refusal here rather than a silently wrong bind pose. A pivot that is tiny next to the matrix's largest entry
    /// (an authored near-zero scale) is refused like an exact zero, since its inverse would be Inf or NaN.</summary>
    internal static EnemyPlacementMatrix Inverse(EnemyPlacementMatrix matrix)
    {
        var source = matrix.CopyRowMajor();
        var inverse = Identity.CopyRowMajor();
        float largest = 0f;
        foreach (var value in source) largest = Math.Max(largest, Math.Abs(value));
        float smallestPivot = largest * 1e-6f;
        for (int column = 0; column < Side; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < Side; row++)
                if (Math.Abs(source[row * Side + column]) > Math.Abs(source[pivot * Side + column])) pivot = row;
            if (!(Math.Abs(source[pivot * Side + column]) > smallestPivot)) throw new InvalidOperationException("A singular matrix has no inverse.");
            if (pivot != column)
                for (int index = 0; index < Side; index++)
                {
                    (source[column * Side + index], source[pivot * Side + index]) = (source[pivot * Side + index], source[column * Side + index]);
                    (inverse[column * Side + index], inverse[pivot * Side + index]) = (inverse[pivot * Side + index], inverse[column * Side + index]);
                }
            float divisor = source[column * Side + column];
            for (int index = 0; index < Side; index++)
            {
                source[column * Side + index] /= divisor;
                inverse[column * Side + index] /= divisor;
            }
            for (int row = 0; row < Side; row++)
            {
                if (row == column) continue;
                float factor = source[row * Side + column];
                if (factor == 0f) continue;
                for (int index = 0; index < Side; index++)
                {
                    source[row * Side + index] -= factor * source[column * Side + index];
                    inverse[row * Side + index] -= factor * inverse[column * Side + index];
                }
            }
        }
        return new EnemyPlacementMatrix(inverse);
    }

    /// <summary>Where a point lands: <c>M · v</c> with an implicit fourth component of one.</summary>
    internal float[] TransformPoint(float x, float y, float z) => new[]
    {
        this[0, 0] * x + this[0, 1] * y + this[0, 2] * z + this[0, 3],
        this[1, 0] * x + this[1, 1] * y + this[1, 2] * z + this[1, 3],
        this[2, 0] * x + this[2, 1] * y + this[2, 2] * z + this[2, 3]
    };
}

/// <summary>
/// The tumor renderers' order, which is the website's own: an EggSack renderer's number is its place in the
/// natural order of the names, so `EggSack_2` comes before `EggSack_10` exactly as
/// `localeCompare(other, undefined, {numeric:true})` orders them in the preview. Non-digit runs compare
/// case-insensitively and a numeric run compares by value; two names that differ only in case are still ordered
/// (by ordinal), so the order is total and a peer cannot pair a different renderer.
/// </summary>
internal static class EnemyPlacementOrder
{
    /// <summary>The game's two sac prefabs. Every `CB_CustomPrefab` of build 20403457 (sharedassets43) attaches one of them to a
    /// `Weakspot_N` anchor: `EggSack` on the Tank and the three Mothers, `FleshSack` on the Nightmare Striker, Scout and Shooter.
    /// Each is one object carrying the renderer, the sac limb (`Dam_EnemyDamageLimb_Custom`) and its collider.</summary>
    private static readonly string[] TumorRendererPrefixes = { "EggSack", "FleshSack" };

    /// <summary>Whether a renderer name is one of the model's sac tumors. The prefix is case-insensitive
    /// because the game's own names are not all spelled the same way.</summary>
    internal static bool IsTumorRenderer(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (string prefix in TumorRendererPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A stable insertion sort by <see cref="Compare"/>, so equal names keep the order they arrived in.</summary>
    internal static void Sort<T>(IList<T> items, Func<T, string> name)
    {
        for (int index = 1; index < items.Count; index++)
        {
            var item = items[index];
            string key = name(item);
            int cursor = index - 1;
            while (cursor >= 0 && Compare(name(items[cursor]), key) > 0)
            {
                items[cursor + 1] = items[cursor];
                cursor--;
            }
            items[cursor + 1] = item;
        }
    }

    internal static int Compare(string left, string right) => EnemyLimbParts.NaturalCompare(left, right);
}

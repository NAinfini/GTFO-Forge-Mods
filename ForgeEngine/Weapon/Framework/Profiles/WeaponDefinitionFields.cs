using System;
using System.Collections.Generic;

namespace ForgeWeapon;

/// <summary>What a `forge/weapons/*.forge` document may say, in the field table's own vocabulary.
///
/// The applier vocabulary includes both author fields from `catalog/authoring/object-fields.json` for `host=weapon`
/// and internal payload properties needed to parse containers and selectors. <see cref="All"/> publishes only
/// the author fields as `definitionFields`; the package test checks that these are exactly the handled paths
/// that exist in the field table. Internal properties remain available to the parser through <see cref="Blocks"/>.
///
/// The table is game-independent and belongs to the managed package assembly rather than to the native half:
/// the one registration that declares it (I-MANIFEST) is a managed registration, and the release exporter
/// registers the same module declaration, so both read this one list instead of a second copy.
///
/// The one identity field a block may carry is the sentry's `ArchetypeID`: the entry consumes it as a second
/// selector for the object the block belongs to and selects the deployed sentry's firing/detection row.
/// It is internal and is not published in `definitionFields`. Item and offline gear ids are only ever the entry's own key.</summary>
internal static class WeaponDefinitionFields
{
    /// <summary>One accepted payload path, its value shape and whether it is a published author field.</summary>
    public sealed class Spec
    {
        internal Spec(string path, WeaponFieldKind kind, double? minimum = null, bool authorField = true, double? maximum = null)
        {
            Path = path; Kind = kind; IsAuthorField = authorField;
            // Every Number path below is stored by a native Single member: the document reader checks that
            // conversion stays finite, preserving native rounding and underflow rather than narrowing its input.
            // Integer also covers gear identities, whose separate projection owns its domain, so only the
            // four native Int32 counters declare bounds here. Warning thresholds are never admission bounds.
            Minimum = minimum; Maximum = maximum;
        }

        public string Path { get; }
        public WeaponFieldKind Kind { get; }
        /// <summary>False for internal containers and selectors that the parser accepts but authors do not edit as fields.</summary>
        public bool IsAuthorField { get; }
        /// <summary>The native counter's integer minimum, where declared; a smaller value cannot be written
        /// to its member. Warning thresholds are not admission bounds.</summary>
        public double? Minimum { get; }
        /// <summary>The native counter's integer maximum, where declared; a larger value cannot be written
        /// to its member. Gear identities retain their separate projection's domain checks.</summary>
        public double? Maximum { get; }
        /// <summary>Entries of a `list` path; each entry is a bag validated against exactly these paths.</summary>
        public Spec[]? Items { get; private set; }

        /// <summary>Assigns the entry shape, used only while the recursive child vocabulary is being built.</summary>
        internal Spec Entries(params Spec[] items) { Items = items; return this; }
    }

    /// <summary>One block id and every path of it the applier handles.</summary>
    public sealed class Block
    {
        internal Block(string blockId, params Spec[] fields) { BlockId = blockId; Fields = fields; }

        public string BlockId { get; }
        public Spec[] Fields { get; }
    }

    // The gear-part vocabulary is recursive: a child node may carry children of its own, and every node uses the
    // same paths. The list's entry shape is therefore built once and pointed back at itself.
    private static readonly Spec[] ChildNodeFields = BuildChildNodeFields();

    private static Spec[] BuildChildNodeFields()
    {
        var children = new Spec("Children", WeaponFieldKind.List);
        var fields = new[]
        {
            new Spec("ChildName", WeaponFieldKind.Text),
            new Spec("Enabled", WeaponFieldKind.Boolean),
            new Spec("LocalPosition", WeaponFieldKind.Vector),
            new Spec("Rotation", WeaponFieldKind.Vector),
            new Spec("Scale", WeaponFieldKind.Vector),
            children
        };
        children.Entries(fields);
        return fields;
    }

    private static readonly Spec[] PartEntryFields =
    {
        new Spec("PartType", WeaponFieldKind.Integer),
        new Spec("Enabled", WeaponFieldKind.Boolean),
        new Spec("LocalPosition", WeaponFieldKind.Vector),
        new Spec("Rotation", WeaponFieldKind.Vector),
        new Spec("Scale", WeaponFieldKind.Vector),
        new Spec("Children", WeaponFieldKind.List).Entries(ChildNodeFields)
    };

    /// <summary>One part the author added to the item: the model it copies (`PartType`, `PartID` and, for a
    /// tool part, `Variant`), the fitted part it hangs under (`ParentType`, absent meaning the item itself) and
    /// the same pose paths a fitted part carries. Its `Name` is the author's own name for the added part and is
    /// unique inside one object.</summary>
    private static readonly Spec[] AddedPartFields =
    {
        new Spec("Name", WeaponFieldKind.Text),
        new Spec("PartType", WeaponFieldKind.Integer),
        new Spec("PartID", WeaponFieldKind.Integer),
        new Spec("Variant", WeaponFieldKind.Text),
        new Spec("ParentType", WeaponFieldKind.Integer),
        new Spec("LocalPosition", WeaponFieldKind.Vector),
        new Spec("Rotation", WeaponFieldKind.Vector),
        new Spec("Scale", WeaponFieldKind.Vector)
    };

    /// <summary>The block ids this applier reads, in the field table's spelling, ordinally sorted.</summary>
    public static IReadOnlyList<Block> Blocks { get; } = Array.AsReadOnly(new[]
    {
        new Block("forge.etc.MineDeployer",
            new Spec("Delay", WeaponFieldKind.Number),
            new Spec("Radius", WeaponFieldKind.Number),
            new Spec("DistanceMin", WeaponFieldKind.Number),
            new Spec("DistanceMax", WeaponFieldKind.Number),
            new Spec("DamageMin", WeaponFieldKind.Number),
            new Spec("DamageMax", WeaponFieldKind.Number),
            new Spec("Force", WeaponFieldKind.Number),
            new Spec("PlacementTime", WeaponFieldKind.Number),
            new Spec("PlacementCooldown", WeaponFieldKind.Number),
            new Spec("FoamData", WeaponFieldKind.List, authorField: false).Entries(MineFoam()),
            new Spec("BeamData", WeaponFieldKind.List, authorField: false).Entries(MineBeam()),
            new Spec("LightData", WeaponFieldKind.List, authorField: false).Entries(MineLight())),
        new Block("forge.etc.MineFoam", MineFoam()),
        new Block("forge.etc.MineBeam", MineBeam()),
        new Block("forge.etc.MineLight", MineLight()),
        new Block("forge.etc.Sentry",
            new Spec("ArchetypeID", WeaponFieldKind.Reference, authorField: false),
            new Spec("ScanDelay", WeaponFieldKind.Number),
            new Spec("PlacementTime", WeaponFieldKind.Number),
            new Spec("ScanColor", WeaponFieldKind.Text),
            new Spec("TargetColor", WeaponFieldKind.Text)),
        // The noise one gun's shot makes, in metres. The game has no per-weapon noise member — a shot raises the
        // shooter's own `Agent.Noise` to `Shoot`, which every enemy weighs by its own detection block — so these
        // two radii replace that flag for this gear with a `NoiseManager.MakeNoise` of the gear's own size
        // (`Native/Combat/WeaponFireNoise.cs`, evidence/fire-noise.json). Both paths use finite native Single
        // conversion; the field table's warnMin=0 is authoring advice, not an admission minimum. Equal ends are refused
        // (the native 1 / (max - min) factor is infinite and the noise value NaN); an inner radius above the outer one is admitted.
        // The thermal sight of one gun: presentation on its own first-person lens (Native/Thermal/ThermalSight.cs).
        new Block("forge.thermalSight",
            new Spec("Enabled", WeaponFieldKind.Boolean),
            new Spec("HighColor", WeaponFieldKind.Text),
            new Spec("LowColor", WeaponFieldKind.Text)),
        new Block("forge.fireNoise",
            new Spec("radiusMax", WeaponFieldKind.Number),
            new Spec("radiusMin", WeaponFieldKind.Number)),
        new Block("forge.gearPartCustomization.Block",
            new Spec("AddedParts", WeaponFieldKind.List).Entries(AddedPartFields),
            new Spec("ParentType", WeaponFieldKind.Integer),
            new Spec("PartID", WeaponFieldKind.Integer),
            new Spec("PartType", WeaponFieldKind.Integer, authorField: false),
            new Spec("Parts", WeaponFieldKind.List, authorField: false).Entries(PartEntryFields),
            new Spec("Variant", WeaponFieldKind.Text, authorField: false)),
        // The flashlight row this gear's flashlight part reads. The game keeps that reference on the part prefab's
        // own `Gear.GearPartFlashlight.m_settingsID`, not in any data block, so it is the one flashlight value a
        // document carries; the row's own values stay in `FlashlightSettingsDataBlock`.
        new Block("forge.vanilla.flashlight",
            new Spec("m_settingsID", WeaponFieldKind.Reference, authorField: false),
            new Spec("HideShell", WeaponFieldKind.Boolean, authorField: false),
            new Spec("LightPosition", WeaponFieldKind.Vector, authorField: false),
            new Spec("LightRotation", WeaponFieldKind.Vector, authorField: false)),
        new Block("forge.vanilla.tool.enemy-scanner-tool",
            new Spec("m_scanConeDotMin", WeaponFieldKind.Number),
            new Spec("m_maxScanWorldRadius", WeaponFieldKind.Number),
            new Spec("m_scanDelay", WeaponFieldKind.Number),
            new Spec("m_pulseDuration", WeaponFieldKind.Number),
            new Spec("m_rechargeDuration", WeaponFieldKind.Number),
            new Spec("s_tagDuration", WeaponFieldKind.Number),
            new Spec("s_maxCourseNodeDistance", WeaponFieldKind.Integer, minimum: int.MinValue, maximum: int.MaxValue)),
        new Block("forge.vanilla.tool.fog-repeller",
            new Spec("sphere", WeaponFieldKind.Number)),
        new Block("forge.vanilla.tool.glue-gun",
            new Spec("m_timeToMaxpressure", WeaponFieldKind.Number),
            new Spec("m_timeToDepleatePressure", WeaponFieldKind.Number),
            new Spec("m_pressureProgressToFire", WeaponFieldKind.Number),
            new Spec("m_fireDelayMin", WeaponFieldKind.Number),
            new Spec("m_fireDelayMax", WeaponFieldKind.Number),
            new Spec("m_forceSizeMin", WeaponFieldKind.Number),
            new Spec("m_forceSizeMax", WeaponFieldKind.Number),
            new Spec("m_spreadMin", WeaponFieldKind.Number),
            new Spec("m_spreadMax", WeaponFieldKind.Number),
            new Spec("m_burstShotsMin", WeaponFieldKind.Integer, minimum: int.MinValue, maximum: int.MaxValue),
            new Spec("m_burstShotsMax", WeaponFieldKind.Integer, minimum: int.MinValue, maximum: int.MaxValue),
            new Spec("m_burstShotDelay", WeaponFieldKind.Number)),
        new Block("forge.vanilla.tool.hacking-tool",
            new Spec("hackDuration", WeaponFieldKind.Number)),
        new Block("forge.vanilla.tool.lock-melter",
            new Spec("unlockDuration", WeaponFieldKind.Number))
    });

    private static Spec[] MineFoam() => new[]
    {
        new Spec("BubbleDelay", WeaponFieldKind.Number),
        new Spec("BatchDelay", WeaponFieldKind.Number),
        new Spec("BubbleCount", WeaponFieldKind.Integer, minimum: int.MinValue, maximum: int.MaxValue),
        new Spec("BubbleAngle", WeaponFieldKind.Number),
        new Spec("BubbleSpeedMin", WeaponFieldKind.Number),
        new Spec("BubbleSpeedMax", WeaponFieldKind.Number)
    };

    private static Spec[] MineBeam() => new[]
    {
        new Spec("Color", WeaponFieldKind.Text),
        new Spec("Length", WeaponFieldKind.Number),
        new Spec("Width", WeaponFieldKind.Number)
    };

    private static Spec[] MineLight() => new[]
    {
        new Spec("Color", WeaponFieldKind.Text),
        new Spec("Intensity", WeaponFieldKind.Number)
    };

    /// <summary>The field table's `forge.gearPartCustomization.Transform` block: the pose of one part. A pose is
    /// not a block of its own in this document — it is carried inside a `Parts`, `AddedParts` or `Children`
    /// entry, where the entry's `PartType` or `ChildName` says which node the pose belongs to — so these paths
    /// are listed here only so their field ids are part of the reported set.</summary>
    public static IReadOnlyList<string> PosePaths { get; } = Array.AsReadOnly(new[]
    {
        "Enabled", "LocalPosition", "Rotation", "Scale", "ChildName", "Children"
    });

    /// <summary>Published author field ids, sorted: `<host>.<block>.<path>` exactly as the field table spells them,
    /// including the pose paths read inside a part entry, but excluding internal payload properties.</summary>
    public static IReadOnlyList<string> All { get; } = Build();

    private static IReadOnlyList<string> Build()
    {
        var ids = new List<string>();
        foreach (var block in Blocks)
            foreach (var field in block.Fields)
                if (field.IsAuthorField) ids.Add(FieldId(block.BlockId, field.Path));
        foreach (var path in PosePaths) ids.Add(FieldId("forge.gearPartCustomization.Transform", path));
        ids.Sort(StringComparer.Ordinal);
        return ids.AsReadOnly();
    }

    /// <summary>The field table's id for one handled path.</summary>
    public static string FieldId(string blockId, string path) => "weapon." + blockId + "." + path;

    /// <summary>The block table's own block ids, sorted: the ones a document may name.</summary>
    public static IReadOnlyList<string> BlockIds { get; } = BuildBlockIds();

    private static IReadOnlyList<string> BuildBlockIds()
    {
        var ids = new List<string>();
        foreach (var block in Blocks) ids.Add(block.BlockId);
        return ids.AsReadOnly();
    }

    public static bool TryBlock(string blockId, out Block block)
    {
        foreach (var candidate in Blocks)
        {
            if (!string.Equals(candidate.BlockId, blockId, StringComparison.Ordinal)) continue;
            block = candidate;
            return true;
        }
        block = null!;
        return false;
    }

    public static bool TryField(Block block, string path, out Spec field)
    {
        foreach (var candidate in block.Fields)
        {
            if (!string.Equals(candidate.Path, path, StringComparison.Ordinal)) continue;
            field = candidate;
            return true;
        }
        field = null!;
        return false;
    }

    public static bool TryField(string blockId, string path, out Spec field)
    {
        field = null!;
        return TryBlock(blockId, out var block) && TryField(block, path, out field);
    }
}

/// <summary>The value shapes the field table declares for a handled path. `Reference` is an id in the game's own
/// spelling (a decimal `persistentID`), `Vector` is three finite numbers and `List` is an ordered array of bags
/// whose paths are the list's own entry shape.</summary>
internal enum WeaponFieldKind
{
    Number,
    Integer,
    Boolean,
    Text,
    Reference,
    Vector,
    List
}

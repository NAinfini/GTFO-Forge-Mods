using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEnemy.Profile;

/// <summary>
/// The Forge block field table for the enemy domain, as `forge/enemies/*.forge` carries it.
///
/// The framework plan (contract §3.3) fixes the document shape: `{schemaVersion, enemies: [{enemyType, blocks:
/// {<block id>: <value expanded by field path>}}]}`, with the block id and the field path being exactly the
/// `block` and `path` of `catalog/authoring/object-fields.json`. This file is that table on the game side: for every
/// accepted block it names the accepted paths and the value kind, so an unknown block id, an unknown path or a
/// misspelled key is refused by name instead of silently applying nothing. The field id the ledger keys on is
/// `"enemy." + block + "." + path`, which is why no id is stored here.
///
/// Only blocks that `object-fields.json` gives host=enemy are accepted. A block this table accepts but the
/// native applier does not write is reported as `enemy.field-unapplied` at spawn; it is never claimed in
/// <see cref="EnemyDefinitionFields"/>, which lists exactly the field ids a native write lands on.
/// </summary>
internal enum EnemyFieldKind
{
    Number,
    Integer,
    UInt32,
    Boolean,
    Text,
    Enum,
    Vector3,
    Color,
    /// <summary>One whole melee timing entry (`MeleeAttackBwd`): the field table types it `list`, so it carries
    /// the same five members the forward entry names, and the applier writes the entry as one native value.</summary>
    TimingList,
    /// <summary>Two numbers in one list, for a path whose name joins two native members with `_`
    /// (`DamageShapeData.Radius_Angle`): the first number is the first member.</summary>
    Pair,
    /// <summary>A native bool the field table types as the enum `True` / `False` / `Unchanged`; a JSON boolean is
    /// read as itself, and `Unchanged` keeps the native value.</summary>
    Switch,
    /// <summary>A list the field table accepts but this build does not write (an animation curve, a sound table,
    /// an effect volume recipe): any array, reported unapplied when set.</summary>
    List
}

/// <summary>One accepted leaf: the block it belongs to, the path inside it, and the value kind. A path segment
/// separates members; a repeated member group (`ModelRefs`, `Bones`, `Limbs`, `MaterialSets`,
/// `FloatProperties`, `PulseEffects`, `Looks`) holds an array of objects whose keys are the remaining segments.</summary>
internal readonly struct EnemyFieldPath
{
    internal EnemyFieldPath(string block, string path, EnemyFieldKind kind)
    { Block = block; Path = path; Kind = kind; }

    internal string Block { get; }
    internal string Path { get; }
    internal EnemyFieldKind Kind { get; }

    /// <summary>The object-fields id, spelled `enemy.<block>.<path>`.</summary>
    internal string FieldId => "enemy." + Block + "." + Path;
}

/// <summary>The value of one accepted leaf, as the document wrote it.</summary>
internal readonly struct EnemyFieldValue
{
    internal EnemyFieldValue(float? number, int? integer, bool? boolean, string? text, float[]? vector)
    { Number = number; Integer = integer; Boolean = boolean; Text = text; Vector = vector; }

    internal float? Number { get; }
    internal int? Integer { get; }
    internal bool? Boolean { get; }
    internal string? Text { get; }
    /// <summary>Three components for a vector, three or four for a colour.</summary>
    internal float[]? Vector { get; }

    internal static EnemyFieldValue OfBoolean(bool value) => new(null, null, value, null, null);
    internal static EnemyFieldValue OfText(string value) => new(null, null, null, value, null);
    internal static EnemyFieldValue OfVector(float[] value) => new(null, null, null, null, value);
}

/// <summary>One parsed enemy's blocks: the block id to its json object, plus the schema that accepted it. The
/// applier reads typed values through <see cref="TryValue"/>; the reader has already refused every key the
/// schema does not name, so a lookup that fails here means the document left the path out.</summary>
internal sealed class EnemyFieldSet
{
    private readonly Dictionary<string, JsonElement> _blocks = new(StringComparer.Ordinal);

    internal EnemyFieldSet(string source) { Source = source; }

    internal string Source { get; }
    internal int Count => _blocks.Count;

    internal void Add(string block, JsonElement value) => _blocks[block] = value;

    internal bool Has(string block) => _blocks.ContainsKey(block);

    internal IReadOnlyCollection<string> Blocks => _blocks.Keys;

    /// <summary>The raw block object, for a block whose entries the applier walks itself.</summary>
    internal bool TryBlock(string block, out JsonElement value) => _blocks.TryGetValue(block, out value);

    internal bool TryRaw(string block, string path, out JsonElement value)
    {
        if (!_blocks.TryGetValue(block, out value)) return false;
        foreach (string segment in path.Split('.'))
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return false;
        return true;
    }

    /// <summary>Whether a block object carries one member, whether or not that member sets a value. A member
    /// present with an empty value is an author edit the reader refuses, and the typed value alone cannot tell that
    /// apart from the member being absent.</summary>
    internal bool HasMember(string block, string name)
        => _blocks.TryGetValue(block, out var root) && root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out _);

    /// <summary>Reads one accepted leaf. A path whose first segment is a repeated group (`Limbs.LimbName`) resolves
    /// the remaining segments against <paramref name="entry"/>, the group element the caller is walking; every
    /// other path resolves against the block object itself.</summary>
    internal bool TryValue(string block, string path, JsonElement entry, out EnemyFieldValue value)
    {
        value = default;
        if (!_blocks.TryGetValue(block, out var root)) return false;
        var node = root;
        int dot = path.IndexOf('.');
        if (dot > 0 && EnemyFieldSchema.IsGroup(path[..dot]))
        {
            node = entry;
            path = path[(dot + 1)..];
        }
        while (true)
        {
            dot = path.IndexOf('.');
            string segment = dot < 0 ? path : path[..dot];
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out var child)) return false;
            if (dot < 0) { value = Read(child); return value.Number != null || value.Integer != null || value.Boolean != null || value.Text != null || value.Vector != null; }
            node = child;
            path = path[(dot + 1)..];
        }
    }

    /// <summary>The repeated group's entries, in document order. The block is the array's parent path; a missing
    /// group answers an empty list so a document that sets only scalar fields needs no array.</summary>
    internal IReadOnlyList<JsonElement> Entries(string block, string group)
    {
        if (!_blocks.TryGetValue(block, out var root)) return Array.Empty<JsonElement>();
        if (!root.TryGetProperty(group, out var array) || array.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
        var list = new List<JsonElement>(array.GetArrayLength());
        foreach (var entry in array.EnumerateArray()) list.Add(entry);
        return list;
    }

    internal static EnemyFieldValue Read(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                // Both views are kept: a length or a cost is read as a number, while a count or a numeric
                // mode target (a pulse `Target`) is read as the whole number it is.
                if (!element.TryGetSingle(out float number)) return default;
                return element.TryGetInt32(out int integer)
                    ? new EnemyFieldValue(number, integer, null, null, null)
                    : new EnemyFieldValue(number, null, null, null, null);
            case JsonValueKind.True: return EnemyFieldValue.OfBoolean(true);
            case JsonValueKind.False: return EnemyFieldValue.OfBoolean(false);
            case JsonValueKind.String: return EnemyFieldValue.OfText(element.GetString() ?? string.Empty);
            case JsonValueKind.Array:
                var vector = new float[element.GetArrayLength()];
                int index = 0;
                foreach (var component in element.EnumerateArray())
                {
                    if (component.ValueKind != JsonValueKind.Number || !component.TryGetSingle(out float value)) return default;
                    vector[index++] = value;
                }
                return EnemyFieldValue.OfVector(vector);
            default: return default;
        }
    }
}

/// <summary>The accepted blocks and paths. Every entry is host=enemy in `catalog/authoring/object-fields.json`; the block
/// ids are the table's own `forge.*` ids, never re-spelled.</summary>
internal static class EnemyFieldSchema
{
    internal const string BirthEffect = "forge.vanilla.spawn.birther_birth_effect";
    internal const string ModelRef = "forge.eec.Model.ModelRef";
    internal const string Bone = "forge.eec.Model.Bone";
    internal const string Limb = "forge.eec.Model.Limb";
    internal const string AnimHandle = "forge.eec.Model.AnimHandle";
    internal const string Material = "forge.eec.Model.Material";
    internal const string Glow = "forge.eec.Model.Glow";
    internal const string Shadow = "forge.eec.Model.Shadow";
    internal const string Marker = "forge.eec.Model.Marker";
    /// <summary>The Forge-owned part looks of one enemy type: a surface colour and a fixed glow for the whole body, a body
    /// region, a tumour or an appendage (`Looks`).</summary>
    internal const string Appearance = "forge.enemyAppearance";
    internal const string Scanner = "forge.eec.Model.Scanner";
    internal const string Feeler = "forge.eec.Detection.Feeler";
    internal const string ScoutScream = "forge.eec.Ability.ScoutScream";
    internal const string Screaming = "forge.eec.Detection.Screaming";
    internal const string Birthing = "forge.eec.Ability.Birthing";
    internal const string ChildSpawning = "forge.enemySpawning";
    internal const string Pouncer = "forge.eec.Ability.Pouncer";
    /// <summary>The enemy's spawn points. The Rundown export writes them into the level's own `EnemyPopulation`
    /// role cost and `EnemyGroup` score; nothing in this package writes them at run time (see
    /// <see cref="EnemyFieldResolver"/>'s export-only blocks).</summary>
    internal const string SpawnCost = "forge.eec.Property.SpawnCost";
    /// <summary>The Forge-owned spawn value of one enemy type that the game reads per life: how much of the level's
    /// on-screen cap (`EnemyCostManager.AllowedTotalCost`) the enemy takes while it is aggressive.</summary>
    internal const string SpawnCap = "forge.enemySpawn";
    internal const string FogSphere = "forge.eec.Ability.FogSphere";
    /// <summary>The Forge-owned combat values of one enemy type that are not a limb: the behind-damage scaling the
    /// hit-time rule applies on top of the receiver's own bonus.</summary>
    internal const string Combat = "forge.enemyCombat";
    /// <summary>The Forge-owned wake rules of one enemy type: how close a sneaking player must come, and whether
    /// player gunfire and damage wake it. The walking distance and the gunfire distances are the type's own native
    /// `EnemyDetection` block, which the website exports; these three have no native member and are applied by the
    /// host's detection and damage hooks (`EnemyModule.Wake`).</summary>
    internal const string Wake = "forge.enemyWake";

    /// <summary>Every accepted field path, grouped by block. The list is the field table's own block/path pairs;
    /// a block or path that is not here is refused.</summary>
    internal static readonly EnemyFieldPath[] Paths =
    {
        new(BirthEffect, "birth_effect", EnemyFieldKind.Text),
        new(ModelRef, "ModelRefs.Type", EnemyFieldKind.Enum),
        new(ModelRef, "ModelRefs.CopyFrom", EnemyFieldKind.Enum),
        new(ModelRef, "ModelRefs.CreateFromPath", EnemyFieldKind.Text),
        new(ModelRef, "ModelRefs.Offset", EnemyFieldKind.Vector3),
        new(ModelRef, "ModelRefs.RotateOffset", EnemyFieldKind.Vector3),
        new(Bone, "Bones.Bone", EnemyFieldKind.Text),
        new(Bone, "Bones.Offset", EnemyFieldKind.Vector3),
        new(Bone, "Bones.RotationOffset", EnemyFieldKind.Vector3),
        new(Bone, "Bones.Scale", EnemyFieldKind.Vector3),
        new(Bone, "Bones.UsingRelativeScale", EnemyFieldKind.Boolean),
        new(Bone, "Prefabs.Bone", EnemyFieldKind.Text),
        new(Bone, "Prefabs.Prefab", EnemyFieldKind.Text),
        new(Bone, "Prefabs.Position", EnemyFieldKind.Vector3),
        new(Bone, "Prefabs.Rotation", EnemyFieldKind.Vector3),
        new(Bone, "Prefabs.Scale", EnemyFieldKind.Vector3),
        new(Limb, "Limbs.LimbName", EnemyFieldKind.Text),
        new(Limb, "Limbs.LimbType", EnemyFieldKind.Enum),
        new(Limb, "Limbs.CustomMulti", EnemyFieldKind.Number),
        new(Limb, "Limbs.Health", EnemyFieldKind.Number),
        new(Limb, "Limbs.MaxDamage", EnemyFieldKind.Number),
        new(Limb, "Limbs.Breakable", EnemyFieldKind.Boolean),
        new(Limb, "Limbs.Removed", EnemyFieldKind.Boolean),
        new(Limb, "Limbs.DestroyedMultiplier", EnemyFieldKind.Number),
        new(AnimHandle, "WalkSpeed", EnemyFieldKind.Number),
        new(AnimHandle, "RunSpeed", EnemyFieldKind.Number),
        new(AnimHandle, "StrafeWalkSpeed", EnemyFieldKind.Number),
        new(AnimHandle, "StrafeRunSpeed", EnemyFieldKind.Number),
        new(AnimHandle, "ClimbSpeed", EnemyFieldKind.Number),
        new(AnimHandle, "InPlayerRoomSpeed", EnemyFieldKind.Number),
        new(AnimHandle, "JumpMoveSpeed", EnemyFieldKind.Number),
        new(AnimHandle, "TentacleAttackWindUpLen", EnemyFieldKind.Number),
        new(AnimHandle, "AllowMelee180", EnemyFieldKind.Boolean),
        new(AnimHandle, "NeedsArmsToMove", EnemyFieldKind.Boolean),
        new(AnimHandle, "MeleeAttackFwd.Duration", EnemyFieldKind.Number),
        new(AnimHandle, "MeleeAttackFwd.DamageStart", EnemyFieldKind.Number),
        new(AnimHandle, "MeleeAttackFwd.DamageEnd", EnemyFieldKind.Number),
        new(AnimHandle, "MeleeAttackBwd", EnemyFieldKind.TimingList),
        new(Material, "MaterialSets.From", EnemyFieldKind.Text),
        new(Material, "MaterialSets.To", EnemyFieldKind.Text),
        new(Material, "MaterialSets.SkinNoise", EnemyFieldKind.Enum),
        new(Material, "MaterialSets.SkinNoiseTexture", EnemyFieldKind.Text),
        new(Material, "MaterialSets.FloatProperties.Name", EnemyFieldKind.Text),
        new(Material, "MaterialSets.FloatProperties.Value", EnemyFieldKind.Number),
        new(Glow, "DefaultColor", EnemyFieldKind.Color),
        new(Glow, "DetectionColor", EnemyFieldKind.Color),
        new(Glow, "HeartbeatColor", EnemyFieldKind.Color),
        new(Glow, "SelfWakeupColor", EnemyFieldKind.Color),
        new(Glow, "TentacleAttackColor", EnemyFieldKind.Color),
        new(Glow, "ShooterFireColor", EnemyFieldKind.Color),
        new(Glow, "PulseEffects.GlowColor", EnemyFieldKind.Color),
        new(Glow, "PulseEffects.GlowRawPattern", EnemyFieldKind.Text),
        new(Glow, "PulseEffects.Duration", EnemyFieldKind.Number),
        new(Glow, "PulseEffects.Target", EnemyFieldKind.Enum),
        new(Glow, "PulseEffects.AlwaysPulse", EnemyFieldKind.Boolean),
        new(Glow, "PulseEffects.KeepOnDead", EnemyFieldKind.Boolean),
        new(Glow, "PulseEffects.RandomizeTime", EnemyFieldKind.Boolean),
        new(Glow, "PropagateWakeupColor", EnemyFieldKind.Color),
        new(Glow, "Transition", EnemyFieldKind.Number),
        new(Shadow, "RequireTagForDetection", EnemyFieldKind.Boolean),
        new(Marker, "MarkerColor", EnemyFieldKind.Color),
        new(Marker, "SpriteScale", EnemyFieldKind.Number),
        new(Marker, "Icon", EnemyFieldKind.Enum),
        new(Marker, "AllowMarkingOnHibernate", EnemyFieldKind.Boolean),
        new(Marker, "BlinkIn", EnemyFieldKind.Boolean),
        new(Marker, "Blink", EnemyFieldKind.Boolean),
        new(Marker, "BlinkDuration", EnemyFieldKind.Number),
        new(Marker, "BlinkMinDelay", EnemyFieldKind.Number),
        new(Marker, "BlinkMaxDelay", EnemyFieldKind.Number),
        new(Appearance, "Looks.Target", EnemyFieldKind.Enum),
        new(Appearance, "Looks.Color", EnemyFieldKind.Color),
        new(Appearance, "Looks.Glow", EnemyFieldKind.Color),
        new(Appearance, "Looks.GlowBrightness", EnemyFieldKind.Number),
        new(Appearance, "Looks.Invisible", EnemyFieldKind.Boolean),
        new(Scanner, "Internal_DefaultColor", EnemyFieldKind.Color),
        new(Scanner, "Internal_DefaultSize", EnemyFieldKind.Number),
        new(Scanner, "Internal_WakeupColor", EnemyFieldKind.Color),
        new(Scanner, "Internal_WakeupSize", EnemyFieldKind.Number),
        new(Scanner, "Internal_DetectionColor", EnemyFieldKind.Color),
        new(Scanner, "Internal_DetectionSize", EnemyFieldKind.Number),
        new(Scanner, "Internal_HeartbeatColor", EnemyFieldKind.Color),
        new(Scanner, "Internal_HeartbeatSize", EnemyFieldKind.Number),
        new(Scanner, "Internal_ScoutPatrolColor", EnemyFieldKind.Color),
        new(Scanner, "Internal_ScoutPatrolSize", EnemyFieldKind.Number),
        new(Scanner, "Internal_ScoutFeelerColor", EnemyFieldKind.Color),
        new(Scanner, "Internal_ScoutFeelerSize", EnemyFieldKind.Number),
        new(Feeler, "TendrilAngleOffset", EnemyFieldKind.Number),
        new(Feeler, "TendrilStepAngle", EnemyFieldKind.Number),
        new(Feeler, "TendrilMinYSpread", EnemyFieldKind.Number),
        new(Feeler, "TendrilMaxYSpread", EnemyFieldKind.Number),
        new(Feeler, "TendrilOutTimer", EnemyFieldKind.Number),
        new(Feeler, "Distance", EnemyFieldKind.Number),
        new(Feeler, "StepDistance", EnemyFieldKind.Number),
        new(Feeler, "NormalColor", EnemyFieldKind.Color),
        new(Feeler, "DetectColor", EnemyFieldKind.Color),
        new(ScoutScream, "ChargeupColor", EnemyFieldKind.Color),
        new(ScoutScream, "ScreamColor", EnemyFieldKind.Color),
        new(ScoutScream, "FogColor", EnemyFieldKind.Color),
        new(ScoutScream, "FogIntensity", EnemyFieldKind.Number),
        new(ScoutScream, "EffectVolume.Enabled", EnemyFieldKind.Boolean),
        new(ScoutScream, "EffectVolume.Contents", EnemyFieldKind.Enum),
        new(ScoutScream, "EffectVolume.Modification", EnemyFieldKind.Enum),
        new(ScoutScream, "EffectVolume.Scale", EnemyFieldKind.Number),
        new(Screaming, "ImmortalDuringScream", EnemyFieldKind.Boolean),
        new(Screaming, "DetectedBehaviour", EnemyFieldKind.Enum),
        new(Birthing, "ChildrenPerBirthMin", EnemyFieldKind.Integer),
        new(Birthing, "ChildrenMax", EnemyFieldKind.Integer),
        new(Birthing, "ChildrenCost", EnemyFieldKind.Number),
        new(ChildSpawning, "Attacks.AbilityIndex", EnemyFieldKind.Integer),
        new(ChildSpawning, "Attacks.Group", EnemyFieldKind.UInt32),
        new(ChildSpawning, "Attacks.SourceName", EnemyFieldKind.Text),
        new(ChildSpawning, "Attacks.Population", EnemyFieldKind.UInt32),
        new(ChildSpawning, "Attacks.WaveIndex", EnemyFieldKind.Integer),
        new(ChildSpawning, "Attacks.WaveSettings", EnemyFieldKind.UInt32),
        new(ChildSpawning, "Attacks.WavePopulation", EnemyFieldKind.UInt32),
        new(ChildSpawning, "Attacks.Count", EnemyFieldKind.Integer),
        new(ChildSpawning, "Attacks.Delay", EnemyFieldKind.Number),
        new(ChildSpawning, "Attacks.Interval", EnemyFieldKind.Number),
        new(Pouncer, "DashCooldownMin", EnemyFieldKind.Number),
        new(Pouncer, "DashCooldownMax", EnemyFieldKind.Number),
        new(Pouncer, "DashMaxTime", EnemyFieldKind.Number),
        new(Pouncer, "ChargeDuration", EnemyFieldKind.Number),
        new(Pouncer, "ConsumeDuration", EnemyFieldKind.Number),
        new(Pouncer, "DashEndPhaseDistance", EnemyFieldKind.Number),
        new(Pouncer, "ChargeStaggerDamageThreshold", EnemyFieldKind.Number),
        new(Pouncer, "DashStaggerDamageThreshold", EnemyFieldKind.Number),
        new(Pouncer, "StaggerDuration", EnemyFieldKind.Number),
        new(Pouncer, "FaceToTargetDuringProwlingDistance", EnemyFieldKind.Number),
        new(Pouncer, "StaggerLeadsToAfterHeld", EnemyFieldKind.Switch),
        new(Pouncer, "EnableOffMeshLinkDash", EnemyFieldKind.Switch),
        new(Pouncer, "LOSRequiredForDash", EnemyFieldKind.Switch),
        new(Pouncer, "DashStaggerUsesHeavyHitreaction", EnemyFieldKind.Switch),
        new(Pouncer, "DashMovementModifier.SpeedModifier_AccelerationModifier", EnemyFieldKind.Pair),
        new(Pouncer, "HeldStateData.MaxHeldDuration", EnemyFieldKind.Number),
        new(Pouncer, "HeldStateData.DamageToPlayerPerSecond", EnemyFieldKind.Number),
        new(Pouncer, "HeldStateData.DamageOnStartHolding", EnemyFieldKind.Number),
        new(Pouncer, "HeldStateData.AfterHeldRunAwayDuration", EnemyFieldKind.Number),
        new(Pouncer, "HeldStateData.SpitOutStateDuration", EnemyFieldKind.Number),
        new(Pouncer, "DamageShapeData.Radius_Angle", EnemyFieldKind.Pair),
        new(Pouncer, "PouncerSoundData", EnemyFieldKind.List),
        new(SpawnCost, "SpawnCost", EnemyFieldKind.Number),
        new(SpawnCap, "CostTowardsCap", EnemyFieldKind.Number),
        new(FogSphere, "ColorMin", EnemyFieldKind.Color),
        new(FogSphere, "ColorMax", EnemyFieldKind.Color),
        new(FogSphere, "IntensityMin", EnemyFieldKind.Number),
        new(FogSphere, "IntensityMax", EnemyFieldKind.Number),
        new(FogSphere, "RangeMin", EnemyFieldKind.Number),
        new(FogSphere, "RangeMax", EnemyFieldKind.Number),
        new(FogSphere, "DensityMin", EnemyFieldKind.Number),
        new(FogSphere, "DensityMax", EnemyFieldKind.Number),
        new(FogSphere, "DensityAmountMin", EnemyFieldKind.Number),
        new(FogSphere, "DensityAmountMax", EnemyFieldKind.Number),
        new(FogSphere, "Duration", EnemyFieldKind.Number),
        new(FogSphere, "ColorCurve", EnemyFieldKind.List),
        new(FogSphere, "IntensityCurve", EnemyFieldKind.List),
        new(FogSphere, "RangeCurve", EnemyFieldKind.List),
        new(FogSphere, "DensityCurve", EnemyFieldKind.List),
        new(FogSphere, "DensityAmountCurve", EnemyFieldKind.List),
        new(FogSphere, "EffectVolume", EnemyFieldKind.List),
        new(Combat, "BackDamageMultiplier", EnemyFieldKind.Number),
        new(Wake, "SneakDistance", EnemyFieldKind.Number),
        new(Wake, "GunfireWakes", EnemyFieldKind.Boolean),
        new(Wake, "DamageWakes", EnemyFieldKind.Boolean)
    };

    /// <summary>The repeated member groups: a node with one of these names is an array of objects, and the
    /// remaining path segments are the keys of each element.</summary>
    internal static readonly string[] Groups =
        { "Attacks", "ModelRefs", "Bones", "Prefabs", "Limbs", "MaterialSets", "FloatProperties", "PulseEffects", "Looks" };

    internal static bool IsGroup(string name)
    {
        foreach (var group in Groups) if (group == name) return true;
        return false;
    }

    private static readonly Dictionary<string, EnemyFieldPath[]> ByBlock = BuildIndex();

    private static Dictionary<string, EnemyFieldPath[]> BuildIndex()
    {
        var index = new Dictionary<string, EnemyFieldPath[]>(StringComparer.Ordinal);
        var lists = new Dictionary<string, List<EnemyFieldPath>>(StringComparer.Ordinal);
        foreach (var path in Paths)
        {
            if (!lists.TryGetValue(path.Block, out var list)) lists.Add(path.Block, list = new List<EnemyFieldPath>());
            list.Add(path);
        }
        foreach (var entry in lists) index.Add(entry.Key, entry.Value.ToArray());
        return index;
    }

    /// <summary>The accepted paths of one block id, or false when the block id is not in the table.</summary>
    internal static bool ById(string block, out IReadOnlyList<EnemyFieldPath> paths)
    {
        if (ByBlock.TryGetValue(block, out var found)) { paths = found; return true; }
        paths = Array.Empty<EnemyFieldPath>();
        return false;
    }

    /// <summary>The field ids the native applier actually writes, in ordinal order. The package's
    /// I-MANIFEST `definitionFields` reports exactly this list, and the package test proves the applier's
    /// dispatch and this list agree: a path the dispatch handles but this list omits, and an id here the
    /// dispatch does not handle, both fail the test.
    ///
    /// The region entries of `ModelRefs`, `MaterialSets` and `Bones` (a `Type`, `From` or `Bone` that names a body
    /// region or a tumor, see <see cref="EnemyRegionEntries"/>) are the region assembly and are claimed here. The
    /// paths the field table accepts but this build does not write — an EEC attach-point model ref, a material
    /// swap to a named material (`MaterialSets.To` on a non-region set, and the skin-noise members), the
    /// feeler height spread (the native balancing block's), the scream fog colour and intensity (no
    /// native member), the two effect volumes (behaviour templates), the screamer's detected behaviour, the
    /// snatcher's sound table and the fog-sphere curves — are accepted by the reader and
    /// reported as `enemy.field-unapplied` at spawn with their own reasons; they are not claimed here. The spawn points
    /// (`forge.eec.Property.SpawnCost`) are not claimed either: the Rundown export writes them into native data
    /// blocks, and the one native member they used to be written to at spawn, `EnemyAgent.m_enemyCost`, is the
    /// life's on-screen cap share, which `forge.enemySpawn.CostTowardsCap` owns.
    /// `Docs/forge-contract/FORGE-FRAMEWORK.md` §3.3 makes this list the package's own statement
    /// of what it applies, so over-claiming a path would be a false report.</summary>
    internal static readonly string[] EnemyDefinitionFields =
    {
        "enemy.forge.eec.Ability.Birthing.ChildrenCost",
        "enemy.forge.eec.Ability.Birthing.ChildrenMax",
        "enemy.forge.eec.Ability.Birthing.ChildrenPerBirthMin",
        "enemy.forge.eec.Ability.FogSphere.ColorMax",
        "enemy.forge.eec.Ability.FogSphere.ColorMin",
        "enemy.forge.eec.Ability.FogSphere.DensityAmountMax",
        "enemy.forge.eec.Ability.FogSphere.DensityAmountMin",
        "enemy.forge.eec.Ability.FogSphere.DensityMax",
        "enemy.forge.eec.Ability.FogSphere.DensityMin",
        "enemy.forge.eec.Ability.FogSphere.Duration",
        "enemy.forge.eec.Ability.FogSphere.IntensityMax",
        "enemy.forge.eec.Ability.FogSphere.IntensityMin",
        "enemy.forge.eec.Ability.FogSphere.RangeMax",
        "enemy.forge.eec.Ability.FogSphere.RangeMin",
        "enemy.forge.eec.Ability.Pouncer.ChargeDuration",
        "enemy.forge.eec.Ability.Pouncer.ChargeStaggerDamageThreshold",
        "enemy.forge.eec.Ability.Pouncer.ConsumeDuration",
        "enemy.forge.eec.Ability.Pouncer.DamageShapeData.Radius_Angle",
        "enemy.forge.eec.Ability.Pouncer.DashCooldownMax",
        "enemy.forge.eec.Ability.Pouncer.DashCooldownMin",
        "enemy.forge.eec.Ability.Pouncer.DashEndPhaseDistance",
        "enemy.forge.eec.Ability.Pouncer.DashMaxTime",
        "enemy.forge.eec.Ability.Pouncer.DashMovementModifier.SpeedModifier_AccelerationModifier",
        "enemy.forge.eec.Ability.Pouncer.DashStaggerDamageThreshold",
        "enemy.forge.eec.Ability.Pouncer.DashStaggerUsesHeavyHitreaction",
        "enemy.forge.eec.Ability.Pouncer.EnableOffMeshLinkDash",
        "enemy.forge.eec.Ability.Pouncer.FaceToTargetDuringProwlingDistance",
        "enemy.forge.eec.Ability.Pouncer.HeldStateData.AfterHeldRunAwayDuration",
        "enemy.forge.eec.Ability.Pouncer.HeldStateData.DamageOnStartHolding",
        "enemy.forge.eec.Ability.Pouncer.HeldStateData.DamageToPlayerPerSecond",
        "enemy.forge.eec.Ability.Pouncer.HeldStateData.MaxHeldDuration",
        "enemy.forge.eec.Ability.Pouncer.HeldStateData.SpitOutStateDuration",
        "enemy.forge.eec.Ability.Pouncer.LOSRequiredForDash",
        "enemy.forge.eec.Ability.Pouncer.StaggerDuration",
        "enemy.forge.eec.Ability.Pouncer.StaggerLeadsToAfterHeld",
        "enemy.forge.eec.Ability.ScoutScream.ChargeupColor",
        "enemy.forge.eec.Ability.ScoutScream.ScreamColor",
        "enemy.forge.eec.Detection.Feeler.DetectColor",
        "enemy.forge.eec.Detection.Feeler.Distance",
        "enemy.forge.eec.Detection.Feeler.NormalColor",
        "enemy.forge.eec.Detection.Feeler.StepDistance",
        "enemy.forge.eec.Detection.Feeler.TendrilAngleOffset",
        "enemy.forge.eec.Detection.Feeler.TendrilOutTimer",
        "enemy.forge.eec.Detection.Feeler.TendrilStepAngle",
        "enemy.forge.eec.Detection.Screaming.ImmortalDuringScream",
        "enemy.forge.eec.Model.AnimHandle.AllowMelee180",
        "enemy.forge.eec.Model.AnimHandle.ClimbSpeed",
        "enemy.forge.eec.Model.AnimHandle.InPlayerRoomSpeed",
        "enemy.forge.eec.Model.AnimHandle.JumpMoveSpeed",
        "enemy.forge.eec.Model.AnimHandle.MeleeAttackBwd",
        "enemy.forge.eec.Model.AnimHandle.MeleeAttackFwd.DamageEnd",
        "enemy.forge.eec.Model.AnimHandle.MeleeAttackFwd.DamageStart",
        "enemy.forge.eec.Model.AnimHandle.MeleeAttackFwd.Duration",
        "enemy.forge.eec.Model.AnimHandle.NeedsArmsToMove",
        "enemy.forge.eec.Model.AnimHandle.RunSpeed",
        "enemy.forge.eec.Model.AnimHandle.StrafeRunSpeed",
        "enemy.forge.eec.Model.AnimHandle.StrafeWalkSpeed",
        "enemy.forge.eec.Model.AnimHandle.TentacleAttackWindUpLen",
        "enemy.forge.eec.Model.AnimHandle.WalkSpeed",
        "enemy.forge.eec.Model.Bone.Bones.Bone",
        "enemy.forge.eec.Model.Bone.Bones.Offset",
        "enemy.forge.eec.Model.Bone.Bones.RotationOffset",
        "enemy.forge.eec.Model.Bone.Bones.Scale",
        "enemy.forge.eec.Model.Bone.Bones.UsingRelativeScale",
        "enemy.forge.eec.Model.Bone.Prefabs.Bone",
        "enemy.forge.eec.Model.Bone.Prefabs.Position",
        "enemy.forge.eec.Model.Bone.Prefabs.Prefab",
        "enemy.forge.eec.Model.Bone.Prefabs.Rotation",
        "enemy.forge.eec.Model.Bone.Prefabs.Scale",
        "enemy.forge.eec.Model.Glow.DefaultColor",
        "enemy.forge.eec.Model.Glow.DetectionColor",
        "enemy.forge.eec.Model.Glow.HeartbeatColor",
        "enemy.forge.eec.Model.Glow.PropagateWakeupColor",
        "enemy.forge.eec.Model.Glow.PulseEffects.AlwaysPulse",
        "enemy.forge.eec.Model.Glow.PulseEffects.Duration",
        "enemy.forge.eec.Model.Glow.PulseEffects.GlowColor",
        "enemy.forge.eec.Model.Glow.PulseEffects.GlowRawPattern",
        "enemy.forge.eec.Model.Glow.PulseEffects.KeepOnDead",
        "enemy.forge.eec.Model.Glow.PulseEffects.RandomizeTime",
        "enemy.forge.eec.Model.Glow.PulseEffects.Target",
        "enemy.forge.eec.Model.Glow.SelfWakeupColor",
        "enemy.forge.eec.Model.Glow.ShooterFireColor",
        "enemy.forge.eec.Model.Glow.TentacleAttackColor",
        "enemy.forge.eec.Model.Glow.Transition",
        "enemy.forge.eec.Model.Limb.Limbs.Breakable",
        "enemy.forge.eec.Model.Limb.Limbs.CustomMulti",
        "enemy.forge.eec.Model.Limb.Limbs.DestroyedMultiplier",
        "enemy.forge.eec.Model.Limb.Limbs.Health",
        "enemy.forge.eec.Model.Limb.Limbs.LimbName",
        "enemy.forge.eec.Model.Limb.Limbs.LimbType",
        "enemy.forge.eec.Model.Limb.Limbs.MaxDamage",
        "enemy.forge.eec.Model.Limb.Limbs.Removed",
        "enemy.forge.eec.Model.Marker.AllowMarkingOnHibernate",
        "enemy.forge.eec.Model.Marker.Blink",
        "enemy.forge.eec.Model.Marker.BlinkDuration",
        "enemy.forge.eec.Model.Marker.BlinkIn",
        "enemy.forge.eec.Model.Marker.BlinkMaxDelay",
        "enemy.forge.eec.Model.Marker.BlinkMinDelay",
        "enemy.forge.eec.Model.Marker.Icon",
        "enemy.forge.eec.Model.Marker.MarkerColor",
        "enemy.forge.eec.Model.Marker.SpriteScale",
        "enemy.forge.eec.Model.Material.MaterialSets.FloatProperties.Name",
        "enemy.forge.eec.Model.Material.MaterialSets.FloatProperties.Value",
        "enemy.forge.eec.Model.Material.MaterialSets.From",
        "enemy.forge.eec.Model.Material.MaterialSets.To",
        "enemy.forge.eec.Model.ModelRef.ModelRefs.CreateFromPath",
        "enemy.forge.eec.Model.ModelRef.ModelRefs.Type",
        "enemy.forge.eec.Model.Scanner.Internal_DefaultColor",
        "enemy.forge.eec.Model.Scanner.Internal_DefaultSize",
        "enemy.forge.eec.Model.Scanner.Internal_DetectionColor",
        "enemy.forge.eec.Model.Scanner.Internal_DetectionSize",
        "enemy.forge.eec.Model.Scanner.Internal_HeartbeatColor",
        "enemy.forge.eec.Model.Scanner.Internal_HeartbeatSize",
        "enemy.forge.eec.Model.Scanner.Internal_ScoutFeelerColor",
        "enemy.forge.eec.Model.Scanner.Internal_ScoutFeelerSize",
        "enemy.forge.eec.Model.Scanner.Internal_ScoutPatrolColor",
        "enemy.forge.eec.Model.Scanner.Internal_ScoutPatrolSize",
        "enemy.forge.eec.Model.Scanner.Internal_WakeupColor",
        "enemy.forge.eec.Model.Scanner.Internal_WakeupSize",
        "enemy.forge.eec.Model.Shadow.RequireTagForDetection",
        "enemy.forge.enemyAppearance.Looks.Color",
        "enemy.forge.enemyAppearance.Looks.Glow",
        "enemy.forge.enemyAppearance.Looks.GlowBrightness",
        "enemy.forge.enemyAppearance.Looks.Invisible",
        "enemy.forge.enemyAppearance.Looks.Target",
        "enemy.forge.enemyCombat.BackDamageMultiplier",
        "enemy.forge.enemySpawn.CostTowardsCap",
        "enemy.forge.enemySpawning.Attacks.AbilityIndex",
        "enemy.forge.enemySpawning.Attacks.Count",
        "enemy.forge.enemySpawning.Attacks.Delay",
        "enemy.forge.enemySpawning.Attacks.Group",
        "enemy.forge.enemySpawning.Attacks.Interval",
        "enemy.forge.enemySpawning.Attacks.Population",
        "enemy.forge.enemySpawning.Attacks.SourceName",
        "enemy.forge.enemySpawning.Attacks.WaveIndex",
        "enemy.forge.enemySpawning.Attacks.WavePopulation",
        "enemy.forge.enemySpawning.Attacks.WaveSettings",
        "enemy.forge.enemyWake.DamageWakes",
        "enemy.forge.enemyWake.GunfireWakes",
        "enemy.forge.enemyWake.SneakDistance"
    };
}

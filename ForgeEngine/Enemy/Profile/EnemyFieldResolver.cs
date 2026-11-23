using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEnemy.Profile;

/// <summary>
/// The one reader of the block values into the typed resolution the native sink applies, and the one place that
/// decides which accepted field ids this build actually writes.
///
/// The resolver is pure: it only reads <see cref="EnemyFieldSet"/> and fills an
/// <see cref="EnemyFieldResolution"/>. Every path it reads into a typed value lands in `Applied`; every accepted
/// path the document set but no case reads lands in `Unapplied`. That split is what the package test asserts
/// against <see cref="EnemyFieldSchema.EnemyDefinitionFields"/> — a case added without the list, and a list entry
/// without a case, both change `Applied` and fail the test.
///
/// A path inside a repeated group is read per entry: `Limbs` entries are correlated so one limb's name, type,
/// multiplier and health stay one limb, which is why the limbs are walked as entries rather than as separate
/// leaves.
/// </summary>
internal static partial class EnemyFieldResolver
{
    /// <summary>The blocks this build's sink writes. A block not named here has every present path reported as
    /// unapplied with its own reason.</summary>
    private static readonly Dictionary<string, string> UnappliedBlocks = new(StringComparer.Ordinal)
    {
        [EnemyFieldSchema.ModelRef] = "an entry whose `Type` is an EEC attach point (Tentacle, ShooterFire, ScoutFeeler, BioMarker…) needs the prefab-time model-ref assembly, which is not implemented in this build; only region entries are applied",
        [EnemyFieldSchema.Material] = "a set whose `From` is a material name and names `To` swaps in a material from EEC's asset cache, which this build does not load; that set, and the skin-noise members, are not applied",
        [EnemyFieldSchema.BirthEffect] = "the birth effect is a CD_TriggerEffect component reference; instantiating it from an asset path is not implemented"
    };

    /// <summary>The paths of an otherwise written block that this build does not write, each with its own reason.
    /// A path here is looked up before its block's reason.</summary>
    private static readonly Dictionary<string, string> UnappliedPaths = BuildUnappliedPaths();

    private static Dictionary<string, string> BuildUnappliedPaths()
    {
        const string curve = "an animation curve needs a curve editor on the website; the handler keeps the prefab's own curve";
        const string effectVolume = "an effect volume is a behaviour template (the effect-volume card on the ability's trigger), not a field write";
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string block, string path, string reason) => paths[Find(block, path).FieldId] = reason;
        const string spread = "the tendril height spread is read from the type's native EnemyBalancing (ScoutTentacleAngleMinMax), which the website exports; this block's copy is not written";
        Add(EnemyFieldSchema.Feeler, "TendrilMinYSpread", spread);
        Add(EnemyFieldSchema.Feeler, "TendrilMaxYSpread", spread);
        const string fog = "the scream fog glows with the scream colour itself; the game has no separate fog colour or intensity member";
        Add(EnemyFieldSchema.ScoutScream, "FogColor", fog);
        Add(EnemyFieldSchema.ScoutScream, "FogIntensity", fog);
        foreach (string member in new[] { "Enabled", "Contents", "Modification", "Scale" })
            Add(EnemyFieldSchema.ScoutScream, "EffectVolume." + member, effectVolume);
        Add(EnemyFieldSchema.Screaming, "DetectedBehaviour",
            "sending a non-scout into the scout scream needs an in-game check of its animation state machine; not implemented in this build");
        Add(EnemyFieldSchema.Pouncer, "PouncerSoundData",
            "the snatcher's sound table is native sound event ids; the website has no sound picker yet, so it is not written in this build");
        foreach (string member in new[] { "ColorCurve", "IntensityCurve", "RangeCurve", "DensityCurve", "DensityAmountCurve" })
            Add(EnemyFieldSchema.FogSphere, member, curve);
        Add(EnemyFieldSchema.FogSphere, "EffectVolume", effectVolume);
        return paths;
    }

    /// <summary>The blocks the Rundown export consumes before the game starts, so the document carries them for the
    /// website's own compile and this package has nothing to write: the spawn points become the level's
    /// `EnemyPopulation` role cost and `EnemyGroup` score. They are neither applied nor reported as unapplied.</summary>
    private static readonly HashSet<string> ExportOnlyBlocks = new(StringComparer.Ordinal) { EnemyFieldSchema.SpawnCost };

    internal static EnemyFieldResolution Resolve(EnemyFieldSet set)
    {
        var resolution = new EnemyFieldResolution(set.Source);
        foreach (var block in set.Blocks)
        {
            switch (block)
            {
                case EnemyFieldSchema.Limb: ReadLimbs(set, resolution); break;
                case EnemyFieldSchema.Bone: ReadBone(set, resolution); break;
                case EnemyFieldSchema.Wake: ReadWake(set, resolution); break;
                case EnemyFieldSchema.Shadow: ReadShadow(set, resolution); break;
                case EnemyFieldSchema.SpawnCap: ReadCapCost(set, resolution); break;
                case EnemyFieldSchema.Glow: ReadGlow(set, resolution); break;
                case EnemyFieldSchema.AnimHandle: ReadAnimHandle(set, resolution); break;
                case EnemyFieldSchema.Combat: ReadCombat(set, resolution); break;
                case EnemyFieldSchema.Material: ReadMaterialSets(set, resolution); break;
                case EnemyFieldSchema.ModelRef: ReadModelRefs(set, resolution); break;
                case EnemyFieldSchema.Marker: ReadMarker(set, resolution); break;
                case EnemyFieldSchema.Appearance: ReadLooks(set, resolution); break;
                case EnemyFieldSchema.Feeler: ReadFeeler(set, resolution); break;
                case EnemyFieldSchema.ScoutScream: ReadScoutScream(set, resolution); break;
                case EnemyFieldSchema.Screaming: ReadScreaming(set, resolution); break;
                case EnemyFieldSchema.Birthing: ReadBirthing(set, resolution); break;
                case EnemyFieldSchema.ChildSpawning: ReadChildSpawning(set, resolution); break;
                case EnemyFieldSchema.Pouncer: ReadPouncer(set, resolution); break;
                case EnemyFieldSchema.FogSphere: ReadFogSphere(set, resolution); break;
                case EnemyFieldSchema.Scanner: ReadScanner(set, resolution); break;
            }
        }
        // Every accepted path the document actually set that no case above read is reported, so a block this
        // build cannot apply is a named line and never a silent no-op. The walk is over the schema, so a path a
        // later field-table revision adds is picked up without editing this list. A path one entry applied and
        // another entry of the same group did not is already named by the case that skipped that entry.
        var applied = new HashSet<string>(resolution.Applied, StringComparer.Ordinal);
        foreach (var path in EnemyFieldSchema.Paths)
            if (!applied.Contains(path.FieldId) && !ExportOnlyBlocks.Contains(path.Block) && Contains(set, path)) Skip(resolution, path);
        return resolution;
    }

    private static void Mark(EnemyFieldResolution resolution, EnemyFieldPath path)
    {
        if (!resolution.Applied.Contains(path.FieldId)) resolution.Applied.Add(path.FieldId);
    }

    private static void Skip(EnemyFieldResolution resolution, EnemyFieldPath path)
    {
        foreach (var (_, id) in resolution.Unapplied) if (id == path.FieldId) return;
        resolution.Unapplied.Add((path.Block, path.FieldId));
    }

    /// <summary>Reports every member one group entry set as unapplied: the entry is one this build does not write,
    /// even when another entry of the same group applied the same path.</summary>
    private static void SkipEntry(EnemyFieldResolution resolution, string block, string group, JsonElement entry)
    {
        foreach (var path in EnemyFieldSchema.Paths)
        {
            if (path.Block != block || !path.Path.StartsWith(group + ".", StringComparison.Ordinal)) continue;
            if (ContainsNode(entry, path.Path[(group.Length + 1)..])) Skip(resolution, path);
        }
    }

    private static bool ReadNumber(EnemyFieldSet set, string block, string path, out float value)
    {
        value = 0f;
        if (!set.TryValue(block, path, default, out var raw) || raw.Number is not float number) return false;
        value = number;
        return true;
    }

    private static bool ReadBool(EnemyFieldSet set, string block, string path, out bool value)
    {
        value = false;
        if (!set.TryValue(block, path, default, out var raw) || raw.Boolean is not bool flag) return false;
        value = flag;
        return true;
    }

    private static bool ReadColor(EnemyFieldSet set, string block, string path, out float[] value)
        => ReadColor(set, block, path, default, out value);

    /// <summary>A colour as three or four numbers or as colour text (<see cref="EnemyColorText"/>), read from
    /// <paramref name="entry"/> when the path is inside a repeated group.</summary>
    private static bool ReadColor(EnemyFieldSet set, string block, string path, JsonElement entry, out float[] value)
    {
        value = Array.Empty<float>();
        if (!set.TryValue(block, path, entry, out var raw)) return false;
        if (raw.Vector is { Length: 3 or 4 } vector) { value = vector; return true; }
        if (raw.Text is { } text && EnemyColorText.TryParse(text, out var parsed)) { value = parsed; return true; }
        return false;
    }

    /// <summary>One placement vector of exactly <paramref name="length"/> components, read from
    /// <paramref name="entry"/>, the repeated-group element it belongs to.</summary>
    private static bool ReadVector(EnemyFieldSet set, string block, string path, JsonElement entry, int length, out float[] value)
    {
        value = Array.Empty<float>();
        if (!set.TryValue(block, path, entry, out var raw) || raw.Vector is not { } vector || vector.Length != length) return false;
        value = vector;
        return true;
    }

    private static void ReadBone(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        // `Bones` entries restate one skeleton node's model-local transform: the node is found by name under the
        // type's main model, so an entry is the name plus whichever of the four values it carries. An entry whose
        // `Bone` is a placement slot (a body region or `tumor1`..`tumor8`, spelled exactly) is the placement of that
        // slot instead: the region assembly bakes its transform into the slot's own mesh copies.
        var namePath = Find(EnemyFieldSchema.Bone, "Bones.Bone");
        var offsetPath = Find(EnemyFieldSchema.Bone, "Bones.Offset");
        var boneRotationPath = Find(EnemyFieldSchema.Bone, "Bones.RotationOffset");
        var boneScalePath = Find(EnemyFieldSchema.Bone, "Bones.Scale");
        var relativePath = Find(EnemyFieldSchema.Bone, "Bones.UsingRelativeScale");
        foreach (var entry in set.Entries(EnemyFieldSchema.Bone, "Bones"))
        {
            if (!set.TryValue(EnemyFieldSchema.Bone, namePath.Path, entry, out var rawName) || rawName.Text is not { Length: > 0 } name) continue;
            if (EnemyPlacementSlot.TryParse(name, out _))
            {
                var placement = new EnemyPlacementValue { Target = name };
                Mark(resolution, namePath);
                if (ReadVector(set, EnemyFieldSchema.Bone, offsetPath.Path, entry, 3, out var position)) { placement.Position = position; Mark(resolution, offsetPath); }
                if (ReadVector(set, EnemyFieldSchema.Bone, boneRotationPath.Path, entry, 3, out var angles)) { placement.Rotation = angles; Mark(resolution, boneRotationPath); }
                if (ReadVector(set, EnemyFieldSchema.Bone, boneScalePath.Path, entry, 3, out var size)) { placement.Scale = size; Mark(resolution, boneScalePath); }
                if (set.TryValue(EnemyFieldSchema.Bone, relativePath.Path, entry, out var rawSlotRelative) && rawSlotRelative.Boolean is bool slotRelative)
                { placement.RelativeScale = slotRelative; Mark(resolution, relativePath); }
                resolution.Placement.Add(placement);
                continue;
            }
            var bone = new EnemyFieldBone(name);
            Mark(resolution, namePath);
            if (ReadVector(set, EnemyFieldSchema.Bone, offsetPath.Path, entry, 3, out var offset)) { bone.Offset = offset; Mark(resolution, offsetPath); }
            if (ReadVector(set, EnemyFieldSchema.Bone, boneRotationPath.Path, entry, 3, out var rotation)) { bone.RotationOffset = rotation; Mark(resolution, boneRotationPath); }
            if (ReadVector(set, EnemyFieldSchema.Bone, boneScalePath.Path, entry, 3, out var scale)) { bone.Scale = scale; Mark(resolution, boneScalePath); }
            if (set.TryValue(EnemyFieldSchema.Bone, relativePath.Path, entry, out var rawRelative) && rawRelative.Boolean is bool relative)
            { bone.UsingRelativeScale = relative; Mark(resolution, relativePath); }
            resolution.Bones.Add(bone);
        }
        // `Prefabs` entries hang a vanilla customization prefab on a named bone; the assembler already owns that
        // operation, so the resolver only correlates one entry's five values into one attachment.
        var bonePath = Find(EnemyFieldSchema.Bone, "Prefabs.Bone");
        var prefabPath = Find(EnemyFieldSchema.Bone, "Prefabs.Prefab");
        var positionPath = Find(EnemyFieldSchema.Bone, "Prefabs.Position");
        var rotationPath = Find(EnemyFieldSchema.Bone, "Prefabs.Rotation");
        var scalePath = Find(EnemyFieldSchema.Bone, "Prefabs.Scale");
        foreach (var entry in set.Entries(EnemyFieldSchema.Bone, "Prefabs"))
        {
            if (!set.TryValue(EnemyFieldSchema.Bone, bonePath.Path, entry, out var rawBone) || rawBone.Text is not { Length: > 0 } bone) continue;
            if (!set.TryValue(EnemyFieldSchema.Bone, prefabPath.Path, entry, out var rawPrefab) || rawPrefab.Text is not { Length: > 0 } prefab) continue;
            var attachment = new EnemyAppearanceAttachment(prefab, bone);
            Mark(resolution, bonePath);
            Mark(resolution, prefabPath);
            if (set.TryValue(EnemyFieldSchema.Bone, positionPath.Path, entry, out var rawPosition) && rawPosition.Vector is { Length: 3 } position)
            { attachment.Position = position; Mark(resolution, positionPath); }
            if (set.TryValue(EnemyFieldSchema.Bone, rotationPath.Path, entry, out var rawRotation) && rawRotation.Vector is { Length: 3 } rotation)
            { attachment.Rotation = rotation; Mark(resolution, rotationPath); }
            if (set.TryValue(EnemyFieldSchema.Bone, scalePath.Path, entry, out var rawScale) && rawScale.Vector is { Length: 3 } scale)
            { attachment.Scale = scale; Mark(resolution, scalePath); }
            resolution.Attachments.Add(attachment);
        }
    }

    /// <summary>`forge.enemyWake`: the sneak wake distance and the gunfire and damage wake switches, applied by the
    /// host's detection and damage hooks. <see cref="EnemyBlockReader"/> refuses a negative or non-finite distance.</summary>
    private static void ReadWake(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        var sneakPath = Find(EnemyFieldSchema.Wake, "SneakDistance");
        if (ReadNumber(set, EnemyFieldSchema.Wake, sneakPath.Path, out float sneak))
        {
            resolution.SneakWakeDistance = sneak;
            Mark(resolution, sneakPath);
        }
        var gunfirePath = Find(EnemyFieldSchema.Wake, "GunfireWakes");
        if (ReadBool(set, EnemyFieldSchema.Wake, gunfirePath.Path, out bool gunfire))
        {
            resolution.GunfireWakes = gunfire;
            Mark(resolution, gunfirePath);
        }
        var damagePath = Find(EnemyFieldSchema.Wake, "DamageWakes");
        if (ReadBool(set, EnemyFieldSchema.Wake, damagePath.Path, out bool damage))
        {
            resolution.DamageWakes = damage;
            Mark(resolution, damagePath);
        }
    }

    private static void ReadShadow(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        var path = Find(EnemyFieldSchema.Shadow, "RequireTagForDetection");
        if (ReadBool(set, EnemyFieldSchema.Shadow, path.Path, out bool required))
        {
            resolution.RequireTagForDetection = required;
            Mark(resolution, path);
        }
    }

    /// <summary>`forge.enemySpawn.CostTowardsCap`: this type's share of the on-screen cap while a life is
    /// aggressive. <see cref="EnemyBlockReader"/> refuses the document when the share is negative or not finite.</summary>
    private static void ReadCapCost(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        var path = Find(EnemyFieldSchema.SpawnCap, "CostTowardsCap");
        if (ReadNumber(set, EnemyFieldSchema.SpawnCap, path.Path, out float cost))
        {
            resolution.CostTowardsCap = cost;
            Mark(resolution, path);
        }
    }

    private static void ReadGlow(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        ReadGlowOne(set, resolution, "DefaultColor", color => resolution.GlowDefaultColor = color);
        ReadGlowOne(set, resolution, "SelfWakeupColor", color => resolution.GlowSelfWakeup = color);
        ReadGlowOne(set, resolution, "DetectionColor", color => resolution.GlowDetection = color);
        ReadGlowOne(set, resolution, "HeartbeatColor", color => resolution.GlowHeartbeat = color);
        ReadGlowOne(set, resolution, "TentacleAttackColor", color => resolution.GlowTentacleAttack = color);
        ReadGlowOne(set, resolution, "ShooterFireColor", color => resolution.GlowShooterFire = color);
        ReadGlowOne(set, resolution, "PropagateWakeupColor", color => resolution.GlowPropagateWakeup = color);
        var transitionPath = Find(EnemyFieldSchema.Glow, "Transition");
        if (ReadNumber(set, EnemyFieldSchema.Glow, transitionPath.Path, out float transition))
        {
            resolution.GlowTransition = transition;
            Mark(resolution, transitionPath);
        }
        ReadPulses(set, resolution);
    }

    /// <summary>`PulseEffects`: one value per entry, each member as written. The reader checks the pattern, the
    /// target and the duration; every member present is claimed here because the pulse loop writes all of them.</summary>
    private static void ReadPulses(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        var colorPath = Find(EnemyFieldSchema.Glow, "PulseEffects.GlowColor");
        var patternPath = Find(EnemyFieldSchema.Glow, "PulseEffects.GlowRawPattern");
        var durationPath = Find(EnemyFieldSchema.Glow, "PulseEffects.Duration");
        var targetPath = Find(EnemyFieldSchema.Glow, "PulseEffects.Target");
        var alwaysPath = Find(EnemyFieldSchema.Glow, "PulseEffects.AlwaysPulse");
        var deadPath = Find(EnemyFieldSchema.Glow, "PulseEffects.KeepOnDead");
        var randomPath = Find(EnemyFieldSchema.Glow, "PulseEffects.RandomizeTime");
        foreach (var entry in set.Entries(EnemyFieldSchema.Glow, "PulseEffects"))
        {
            var pulse = new EnemyPulseValue();
            if (ReadColor(set, EnemyFieldSchema.Glow, colorPath.Path, entry, out var color)) { pulse.Color = color; Mark(resolution, colorPath); }
            if (set.TryValue(EnemyFieldSchema.Glow, patternPath.Path, entry, out var rawPattern) && rawPattern.Text is { } pattern)
            { pulse.Pattern = pattern; Mark(resolution, patternPath); }
            if (set.TryValue(EnemyFieldSchema.Glow, durationPath.Path, entry, out var rawDuration) && rawDuration.Number is float duration)
            { pulse.Duration = duration; Mark(resolution, durationPath); }
            if (set.TryValue(EnemyFieldSchema.Glow, targetPath.Path, entry, out var rawTarget)) { pulse.Target = rawTarget; Mark(resolution, targetPath); }
            if (set.TryValue(EnemyFieldSchema.Glow, alwaysPath.Path, entry, out var rawAlways) && rawAlways.Boolean is bool always)
            { pulse.AlwaysPulse = always; Mark(resolution, alwaysPath); }
            if (set.TryValue(EnemyFieldSchema.Glow, deadPath.Path, entry, out var rawDead) && rawDead.Boolean is bool dead)
            { pulse.KeepOnDead = dead; Mark(resolution, deadPath); }
            if (set.TryValue(EnemyFieldSchema.Glow, randomPath.Path, entry, out var rawRandom) && rawRandom.Boolean is bool random)
            { pulse.RandomizeTime = random; Mark(resolution, randomPath); }
            resolution.Pulses.Add(pulse);
        }
    }

    /// <summary>The marker members this build writes: the icon, the colour, the size and the blinks.</summary>
    private static void ReadMarker(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.Marker;
        resolution.MarkerColor = Color(set, resolution, block, "MarkerColor");
        resolution.MarkerSpriteScale = Number(set, resolution, block, "SpriteScale");
        if (set.TryValue(block, "Icon", default, out var icon) && icon.Text is { } iconName)
        { resolution.MarkerIcon = iconName; Mark(resolution, Find(block, "Icon")); }
        resolution.MarkerIconDeclared = set.HasMember(block, "Icon");
        if (ReadBool(set, block, "AllowMarkingOnHibernate", out bool onHibernate)) { resolution.MarkerAllowOnHibernate = onHibernate; Mark(resolution, Find(block, "AllowMarkingOnHibernate")); }
        if (ReadBool(set, block, "BlinkIn", out bool blinkIn)) { resolution.MarkerBlinkIn = blinkIn; Mark(resolution, Find(block, "BlinkIn")); }
        if (ReadBool(set, block, "Blink", out bool blink)) { resolution.MarkerBlink = blink; Mark(resolution, Find(block, "Blink")); }
        if (ReadNumber(set, block, "BlinkDuration", out float blinkDuration)) { resolution.MarkerBlinkDuration = blinkDuration; Mark(resolution, Find(block, "BlinkDuration")); }
        if (ReadNumber(set, block, "BlinkMinDelay", out float blinkMin)) { resolution.MarkerBlinkMinDelay = blinkMin; Mark(resolution, Find(block, "BlinkMinDelay")); }
        if (ReadNumber(set, block, "BlinkMaxDelay", out float blinkMax)) { resolution.MarkerBlinkMaxDelay = blinkMax; Mark(resolution, Find(block, "BlinkMaxDelay")); }
    }

    /// <summary>`forge.enemyAppearance.Looks`: one value per entry as written. A member that is present but not
    /// readable (a colour that is neither numbers nor colour text, a glow brightness that is not a number) is kept as
    /// declared, so the reader can refuse it by name instead of the resolver dropping it.</summary>
    private static void ReadLooks(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.Appearance;
        var targetPath = Find(block, "Looks.Target");
        var colorPath = Find(block, "Looks.Color");
        var glowPath = Find(block, "Looks.Glow");
        var brightnessPath = Find(block, "Looks.GlowBrightness");
        var invisiblePath = Find(block, "Looks.Invisible");
        foreach (var entry in set.Entries(block, "Looks"))
        {
            var look = new EnemyLookValue
            {
                ColorDeclared = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("Color", out _),
                GlowDeclared = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("Glow", out _),
                BrightnessDeclared = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("GlowBrightness", out _),
                InvisibleDeclared = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("Invisible", out _)
            };
            if (set.TryValue(block, targetPath.Path, entry, out var rawTarget) && rawTarget.Text is { } target) { look.Target = target; Mark(resolution, targetPath); }
            if (ReadColor(set, block, colorPath.Path, entry, out var color)) { look.Color = color; Mark(resolution, colorPath); }
            if (ReadColor(set, block, glowPath.Path, entry, out var glow)) { look.Glow = glow; Mark(resolution, glowPath); }
            if (set.TryValue(block, brightnessPath.Path, entry, out var rawBrightness) && rawBrightness.Number is float brightness)
            { look.GlowBrightness = brightness; Mark(resolution, brightnessPath); }
            if (set.TryValue(block, invisiblePath.Path, entry, out var rawInvisible) && rawInvisible.Boolean is bool invisible)
            { look.Invisible = invisible; Mark(resolution, invisiblePath); }
            resolution.Looks.Add(look);
        }
    }

    /// <summary>`forge.eec.Detection.Feeler`: the tendril layout and the tendril itself. The height spread stays
    /// unread (the native balancing block owns it).</summary>
    private static void ReadFeeler(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.Feeler;
        var feeler = new EnemyFeelerValues
        {
            AngleOffset = Number(set, resolution, block, "TendrilAngleOffset"),
            StepAngle = Number(set, resolution, block, "TendrilStepAngle"),
            OutTimer = Number(set, resolution, block, "TendrilOutTimer"),
            Distance = Number(set, resolution, block, "Distance"),
            StepDistance = Number(set, resolution, block, "StepDistance"),
            NormalColor = Color(set, resolution, block, "NormalColor"),
            DetectColor = Color(set, resolution, block, "DetectColor")
        };
        if (feeler.Count != 0) resolution.Feeler = feeler;
    }

    /// <summary>`forge.eec.Ability.ScoutScream`: the charge-up and scream colours. The fog colour, fog intensity and
    /// effect volume stay unread and are reported with their own reasons.</summary>
    private static void ReadScoutScream(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.ScoutScream;
        var scream = resolution.Scream ?? new EnemyScreamValues();
        scream.ChargeupColor = Color(set, resolution, block, "ChargeupColor");
        scream.ScreamColor = Color(set, resolution, block, "ScreamColor");
        if (scream.Count != 0) resolution.Scream = scream;
    }

    /// <summary>`forge.eec.Detection.Screaming.ImmortalDuringScream`, which shares the scream section.</summary>
    private static void ReadScreaming(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.Screaming;
        if (!ReadBool(set, block, "ImmortalDuringScream", out bool immortal)) return;
        Mark(resolution, Find(block, "ImmortalDuringScream"));
        (resolution.Scream ??= new EnemyScreamValues()).ImmortalDuringScream = immortal;
    }

    /// <summary>`forge.eec.Ability.Birthing`: the five numbers of a birth cycle and the per-child cost. The group
    /// to spawn stays unread.</summary>
    private static void ReadBirthing(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.Birthing;
        var birthing = new EnemyBirthingValues
        {
            ChildrenPerBirthMin = Integer(set, resolution, block, "ChildrenPerBirthMin"),
            ChildrenMax = Integer(set, resolution, block, "ChildrenMax"),
            ChildrenCost = Number(set, resolution, block, "ChildrenCost")
        };
        if (birthing.Count != 0) resolution.Birthing = birthing;
    }

    /// <summary>`forge.eec.Ability.Pouncer`. The four switches take `True`, `False` or `Unchanged` (or a JSON
    /// boolean); `Unchanged` is claimed as written and keeps the data's own value. The sound table stays unread.</summary>
    private static void ReadPouncer(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.Pouncer;
        var pouncer = new EnemyPouncerValues
        {
            DashCooldownMin = Number(set, resolution, block, "DashCooldownMin"),
            DashCooldownMax = Number(set, resolution, block, "DashCooldownMax"),
            DashMaxTime = Number(set, resolution, block, "DashMaxTime"),
            ChargeDuration = Number(set, resolution, block, "ChargeDuration"),
            ConsumeDuration = Number(set, resolution, block, "ConsumeDuration"),
            DashEndPhaseDistance = Number(set, resolution, block, "DashEndPhaseDistance"),
            ChargeStaggerDamageThreshold = Number(set, resolution, block, "ChargeStaggerDamageThreshold"),
            DashStaggerDamageThreshold = Number(set, resolution, block, "DashStaggerDamageThreshold"),
            StaggerDuration = Number(set, resolution, block, "StaggerDuration"),
            FaceToTargetDuringProwlingDistance = Number(set, resolution, block, "FaceToTargetDuringProwlingDistance"),
            StaggerLeadsToAfterHeld = Switch(set, resolution, block, "StaggerLeadsToAfterHeld"),
            EnableOffMeshLinkDash = Switch(set, resolution, block, "EnableOffMeshLinkDash"),
            LOSRequiredForDash = Switch(set, resolution, block, "LOSRequiredForDash"),
            DashStaggerUsesHeavyHitreaction = Switch(set, resolution, block, "DashStaggerUsesHeavyHitreaction"),
            DashMovementModifier = Pair(set, resolution, block, "DashMovementModifier.SpeedModifier_AccelerationModifier"),
            MaxHeldDuration = Number(set, resolution, block, "HeldStateData.MaxHeldDuration"),
            DamageToPlayerPerSecond = Number(set, resolution, block, "HeldStateData.DamageToPlayerPerSecond"),
            DamageOnStartHolding = Number(set, resolution, block, "HeldStateData.DamageOnStartHolding"),
            AfterHeldRunAwayDuration = Number(set, resolution, block, "HeldStateData.AfterHeldRunAwayDuration"),
            SpitOutStateDuration = Number(set, resolution, block, "HeldStateData.SpitOutStateDuration"),
            DamageShape = Pair(set, resolution, block, "DamageShapeData.Radius_Angle")
        };
        if (pouncer.Count != 0) resolution.Pouncer = pouncer;
    }

    /// <summary>`forge.eec.Ability.FogSphere`: the colour, intensity, range, density and density-amount ranges and
    /// the total length. The five curves and the effect volume stay unread.</summary>
    private static void ReadFogSphere(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.FogSphere;
        var fog = new EnemyFogSphereValues
        {
            ColorMin = Color(set, resolution, block, "ColorMin"),
            ColorMax = Color(set, resolution, block, "ColorMax"),
            IntensityMin = Number(set, resolution, block, "IntensityMin"),
            IntensityMax = Number(set, resolution, block, "IntensityMax"),
            RangeMin = Number(set, resolution, block, "RangeMin"),
            RangeMax = Number(set, resolution, block, "RangeMax"),
            DensityMin = Number(set, resolution, block, "DensityMin"),
            DensityMax = Number(set, resolution, block, "DensityMax"),
            DensityAmountMin = Number(set, resolution, block, "DensityAmountMin"),
            DensityAmountMax = Number(set, resolution, block, "DensityAmountMax"),
            Duration = Number(set, resolution, block, "Duration")
        };
        if (fog.Count != 0) resolution.FogSphere = fog;
    }

    /// <summary>`forge.eec.Model.Scanner`: one colour and one size per bio-tracker state.</summary>
    private static void ReadScanner(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.Scanner;
        var scanner = new EnemyScannerValues();
        for (int index = 0; index < EnemyScannerValues.Names.Length; index++)
        {
            string name = EnemyScannerValues.Names[index];
            scanner.Colors[index] = Color(set, resolution, block, "Internal_" + name + "Color");
            scanner.Sizes[index] = Number(set, resolution, block, "Internal_" + name + "Size");
        }
        if (scanner.Count != 0) resolution.Scanner = scanner;
    }

    /// <summary>One number leaf, claimed when present.</summary>
    private static float? Number(EnemyFieldSet set, EnemyFieldResolution resolution, string block, string path)
    {
        if (!ReadNumber(set, block, path, out float value)) return null;
        Mark(resolution, Find(block, path));
        return value;
    }

    /// <summary>One whole-number leaf, claimed when present.</summary>
    private static int? Integer(EnemyFieldSet set, EnemyFieldResolution resolution, string block, string path)
    {
        if (!set.TryValue(block, path, default, out var raw) || raw.Integer is not int value) return null;
        Mark(resolution, Find(block, path));
        return value;
    }

    /// <summary>One colour leaf (three or four numbers, or colour text), claimed when present.</summary>
    private static float[]? Color(EnemyFieldSet set, EnemyFieldResolution resolution, string block, string path)
    {
        if (!ReadColor(set, block, path, out var color)) return null;
        Mark(resolution, Find(block, path));
        return color;
    }

    /// <summary>One two-number list leaf, claimed when present.</summary>
    private static float[]? Pair(EnemyFieldSet set, EnemyFieldResolution resolution, string block, string path)
    {
        if (!set.TryValue(block, path, default, out var raw) || raw.Vector is not { Length: 2 } pair) return null;
        Mark(resolution, Find(block, path));
        return pair;
    }

    /// <summary>One `True` / `False` / `Unchanged` switch (a JSON boolean is read as itself). Every one of the three
    /// is claimed; `Unchanged` answers null, which keeps the native value.</summary>
    private static bool? Switch(EnemyFieldSet set, EnemyFieldResolution resolution, string block, string path)
    {
        if (!set.TryValue(block, path, default, out var raw)) return null;
        bool? value = raw.Boolean ?? raw.Text switch { "True" => true, "False" => false, _ => null };
        if (value != null || raw.Text == "Unchanged") Mark(resolution, Find(block, path));
        return value;
    }

    /// <summary>`MaterialSets`: every entry is kept so the reader sees duplicates. A set whose `From` names a body
    /// region (<see cref="EnemyAppearanceRegions.TryParse"/>, spelled exactly) is the region reskin — its `To` is the
    /// vanilla model the region's surface comes from — and a `full` set's properties go on every material that has
    /// them. Any other set naming `To` is a swap to a material from EEC's asset cache, which this build does not
    /// load, so none of its members is claimed and each is reported unapplied. Any other set without `To` writes its
    /// float properties on the found material's per-type copy, which is what its claimed members mean.
    /// The nested property lists are walked here because `TryValue` resolves one group level.</summary>
    private static void ReadMaterialSets(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.Material;
        var fromPath = Find(block, "MaterialSets.From");
        var toPath = Find(block, "MaterialSets.To");
        var floatName = Find(block, "MaterialSets.FloatProperties.Name");
        var floatValue = Find(block, "MaterialSets.FloatProperties.Value");
        foreach (var entry in set.Entries(block, "MaterialSets"))
        {
            var value = new EnemyMaterialSetValue { NamesTo = entry.TryGetProperty("To", out _) };
            if (set.TryValue(block, fromPath.Path, entry, out var rawFrom) && rawFrom.Text is { } from) value.From = from;
            value.Region = EnemyAppearanceRegions.TryParse(value.From, out _);
            if (value.Region)
            {
                Mark(resolution, fromPath);
                if (set.TryValue(block, toPath.Path, entry, out var rawTo) && rawTo.Text is { Length: > 0 } to)
                { value.To = to; Mark(resolution, toPath); }
            }
            else if (value.NamesTo) SkipEntry(resolution, block, "MaterialSets", entry);
            if (value.Region || !value.NamesTo)
            {
                if (value.From != null) Mark(resolution, fromPath);
                if (entry.TryGetProperty("FloatProperties", out var floats) && floats.ValueKind == JsonValueKind.Array)
                    foreach (var number in floats.EnumerateArray())
                    {
                        string? name = Text(number, "Name");
                        float? amount = number.TryGetProperty("Value", out var rawAmount) && rawAmount.ValueKind == JsonValueKind.Number
                            && rawAmount.TryGetSingle(out float parsed) ? parsed : null;
                        if (name != null) Mark(resolution, floatName);
                        if (amount != null) Mark(resolution, floatValue);
                        value.Floats.Add((name, amount));
                    }
            }
            resolution.MaterialSets.Add(value);
        }
    }

    private static string? Text(JsonElement node, string name)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static void ReadGlowOne(EnemyFieldSet set, EnemyFieldResolution resolution, string name, Action<float[]> store)
    {
        var path = Find(EnemyFieldSchema.Glow, name);
        if (!ReadColor(set, EnemyFieldSchema.Glow, path.Path, out var color)) return;
        store(color);
        Mark(resolution, path);
    }

    /// <summary>`ModelRefs`: an entry whose `Type` names a body region (spelled exactly) is the region swap — the
    /// region takes its mesh from the vanilla model `CreateFromPath` names, and the appearance assembler remaps it
    /// onto this type's skeleton. An entry whose `Type` is one of EEC's attach points needs the prefab-time model-ref
    /// assembly this build does not have, so every member it set is reported unapplied.</summary>
    private static void ReadModelRefs(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        const string block = EnemyFieldSchema.ModelRef;
        var typePath = Find(block, "ModelRefs.Type");
        var modelPath = Find(block, "ModelRefs.CreateFromPath");
        foreach (var entry in set.Entries(block, "ModelRefs"))
        {
            if (!set.TryValue(block, typePath.Path, entry, out var rawType) || !EnemyAppearanceRegions.TryParse(rawType.Text, out _))
            {
                SkipEntry(resolution, block, "ModelRefs", entry);
                continue;
            }
            var value = new EnemyAppearanceRegionValue { Region = rawType.Text };
            Mark(resolution, typePath);
            if (set.TryValue(block, modelPath.Path, entry, out var rawModel) && rawModel.Text is { Length: > 0 } model)
            { value.Model = model; Mark(resolution, modelPath); }
            foreach (string member in new[] { "CopyFrom", "Offset", "RotateOffset" })
                if (entry.TryGetProperty(member, out _)) { value.Extra = member; break; }
            resolution.Parts.Add(value);
        }
    }

    /// <summary>`forge.enemyCombat.BackDamageMultiplier`: the enemy-level scaling the hit-time rule applies on top
    /// of the receiver's own behind bonus.</summary>
    private static void ReadCombat(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        var path = Find(EnemyFieldSchema.Combat, "BackDamageMultiplier");
        if (ReadNumber(set, EnemyFieldSchema.Combat, path.Path, out float back))
        {
            resolution.BackDamageMultiplier = back;
            Mark(resolution, path);
        }
    }

    private static void ReadAnimHandle(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        var handles = new EnemyAnimHandleOverrides();
        foreach (var path in EnemyFieldSchema.Paths)
        {
            if (path.Block != EnemyFieldSchema.AnimHandle || !set.TryRaw(path.Block, path.Path, out var entries)) continue;
            foreach (var entry in entries.EnumerateArray())
            {
                var handle = handles.GetOrCreate(entry.GetProperty("animationController").GetInt32());
                var value = entry.GetProperty("value");
                switch (path.Path)
                {
                    case "WalkSpeed": handle.WalkSpeed = value.GetSingle(); break;
                    case "RunSpeed": handle.RunSpeed = value.GetSingle(); break;
                    case "StrafeWalkSpeed": handle.StrafeWalkSpeed = value.GetSingle(); break;
                    case "StrafeRunSpeed": handle.StrafeRunSpeed = value.GetSingle(); break;
                    case "ClimbSpeed": handle.ClimbSpeed = value.GetSingle(); break;
                    case "InPlayerRoomSpeed": handle.InPlayerRoomSpeed = value.GetSingle(); break;
                    case "JumpMoveSpeed": handle.JumpMoveSpeed = value.GetSingle(); break;
                    case "TentacleAttackWindUpLen": handle.TentacleAttackWindUpLen = value.GetSingle(); break;
                    case "MeleeAttackFwd.Duration": handle.MeleeAttackFwdDuration = value.GetSingle(); break;
                    case "MeleeAttackFwd.DamageStart": handle.MeleeAttackFwdDamageStart = value.GetSingle(); break;
                    case "MeleeAttackFwd.DamageEnd": handle.MeleeAttackFwdDamageEnd = value.GetSingle(); break;
                    case "AllowMelee180": handle.AllowMelee180 = value.GetBoolean(); break;
                    case "NeedsArmsToMove": handle.NeedsArmsToMove = value.GetBoolean(); break;
                    case "MeleeAttackBwd":
                        var timing = value[0];
                        if (timing.TryGetProperty("Duration", out var duration)) handle.MeleeAttackBwdDuration = duration.GetSingle();
                        if (timing.TryGetProperty("DamageStart", out var start)) handle.MeleeAttackBwdDamageStart = start.GetSingle();
                        if (timing.TryGetProperty("DamageEnd", out var end)) handle.MeleeAttackBwdDamageEnd = end.GetSingle();
                        if (timing.TryGetProperty("SFXId", out var sfx)) handle.MeleeAttackBwdSfxId = sfx.GetInt32();
                        break;
                    default: throw new InvalidOperationException("Unknown controller field: " + path.Path);
                }
            }
            Mark(resolution, path);
        }
        resolution.AnimHandle = handles;
    }

    private static void ReadLimbs(EnemyFieldSet set, EnemyFieldResolution resolution)
    {
        var namePath = Find(EnemyFieldSchema.Limb, "Limbs.LimbName");
        var typePath = Find(EnemyFieldSchema.Limb, "Limbs.LimbType");
        var multiPath = Find(EnemyFieldSchema.Limb, "Limbs.CustomMulti");
        var healthPath = Find(EnemyFieldSchema.Limb, "Limbs.Health");
        var maxDamagePath = Find(EnemyFieldSchema.Limb, "Limbs.MaxDamage");
        var breakablePath = Find(EnemyFieldSchema.Limb, "Limbs.Breakable");
        var destroyedPath = Find(EnemyFieldSchema.Limb, "Limbs.DestroyedMultiplier");
        var removedPath = Find(EnemyFieldSchema.Limb, "Limbs.Removed");
        var entries = set.Entries(EnemyFieldSchema.Limb, "Limbs");
        int entryIndex = -1;
        foreach (var entry in entries)
        {
            entryIndex++;
            // A duplicate part is refused by `EnemyBlockReader.BuildSections`, which is where the part name is
            // parsed; keeping every entry here is what lets it see the second one.
            if (!set.TryValue(EnemyFieldSchema.Limb, namePath.Path, entry, out var rawName) || rawName.Text is not { Length: > 0 } name) continue;
            var limb = new EnemyFieldLimb(name) { Entry = entryIndex };
            Mark(resolution, namePath);
            if (set.TryValue(EnemyFieldSchema.Limb, typePath.Path, entry, out var rawType) && rawType.Text is { } typeText && TryLimbType(typeText, out var type))
            { limb.Type = type; Mark(resolution, typePath); }
            if (set.TryValue(EnemyFieldSchema.Limb, multiPath.Path, entry, out var rawMulti) && rawMulti.Number is float multi)
            { limb.CustomMultiplier = multi; Mark(resolution, multiPath); }
            if (set.TryValue(EnemyFieldSchema.Limb, healthPath.Path, entry, out var rawHealth) && rawHealth.Number is float health)
            { limb.Health = health; Mark(resolution, healthPath); }
            // The three durability rules the per-hit hooks read out of the type's rule table: the per-hit cap and
            // the destroyed multiplier on `ApplyWeakspotAndArmorModifiers`, and the unbreakable flag on
            // `CheckDestruction`. They are values of the rule table, not a write at spawn, which is why they are
            // applied from `AssembleLimbs` and claimed here.
            if (set.TryValue(EnemyFieldSchema.Limb, maxDamagePath.Path, entry, out var rawMax) && rawMax.Number is float maxDamage)
            { limb.MaxDamage = maxDamage; Mark(resolution, maxDamagePath); }
            if (set.TryValue(EnemyFieldSchema.Limb, breakablePath.Path, entry, out var rawBreakable) && rawBreakable.Boolean is bool breakable)
            { limb.Breakable = breakable; Mark(resolution, breakablePath); }
            if (set.TryValue(EnemyFieldSchema.Limb, removedPath.Path, entry, out var rawRemoved) && rawRemoved.Boolean is bool removed)
            { limb.Removed = removed; Mark(resolution, removedPath); }
            if (set.TryValue(EnemyFieldSchema.Limb, destroyedPath.Path, entry, out var rawDestroyed) && rawDestroyed.Number is float destroyed)
            { limb.DestroyedMultiplier = destroyed; Mark(resolution, destroyedPath); }
            resolution.Limbs.Add(limb);
        }
    }

    internal static bool TryLimbType(string text, out EnemyFieldLimbType type)
    {
        switch (text)
        {
            case "Normal": type = EnemyFieldLimbType.Normal; return true;
            case "Weakspot": type = EnemyFieldLimbType.Weakspot; return true;
            case "WeakspotCustom": type = EnemyFieldLimbType.WeakspotCustom; return true;
            case "Armor": type = EnemyFieldLimbType.Armor; return true;
            case "ArmorCustom": type = EnemyFieldLimbType.ArmorCustom; return true;
            default: type = EnemyFieldLimbType.Normal; return false;
        }
    }

    private static EnemyFieldPath Find(string block, string path)
    {
        foreach (var candidate in EnemyFieldSchema.Paths)
            if (candidate.Block == block && candidate.Path == path) return candidate;
        throw new InvalidOperationException("no field-table path " + block + "." + path);
    }

    /// <summary>Whether the document set one accepted path. A path inside a repeated group counts when the group
    /// holds at least one entry that carries it.</summary>
    private static bool Contains(EnemyFieldSet set, EnemyFieldPath path)
    {
        if (!set.TryBlock(path.Block, out var block)) return false;
        int dot = path.Path.IndexOf('.');
        if (dot < 0) return block.ValueKind == JsonValueKind.Object && block.TryGetProperty(path.Path, out _);
        string group = path.Path[..dot], rest = path.Path[(dot + 1)..];
        if (!IsGroup(group)) return ContainsNode(block, path.Path);
        if (!block.TryGetProperty(group, out var array) || array.ValueKind != JsonValueKind.Array) return false;
        foreach (var entry in array.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.Object && ContainsNode(entry, rest)) return true;
        return false;
    }

    private static bool ContainsNode(JsonElement node, string path)
    {
        int dot = path.IndexOf('.');
        if (dot < 0) return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(path, out _);
        string group = path[..dot], rest = path[(dot + 1)..];
        if (!node.TryGetProperty(group, out var child)) return false;
        if (child.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in child.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object && ContainsNode(entry, rest)) return true;
            return false;
        }
        return ContainsNode(child, rest);
    }

    private static bool IsGroup(string name)
    {
        foreach (var group in EnemyFieldSchema.Groups) if (group == name) return true;
        return false;
    }

    /// <summary>The reason one unapplied path is reported with: the path's own when it has one, else the block's.</summary>
    internal static string UnappliedReason(string block, string fieldId)
        => UnappliedPaths.TryGetValue(fieldId, out var own) ? own
            : UnappliedBlocks.TryGetValue(block, out var reason) ? reason : "not implemented in this build";
}

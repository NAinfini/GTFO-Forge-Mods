using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ForgeEnemy.Profile;

/// <summary>
/// The one reader of a `forge/enemies/*.forge` document. The shape is the framework plan's (contract §3.3):
/// `{schemaVersion, enemies: [{enemyType, blocks: {<block id>: <value expanded by field path>}, ...models}]}`. The shared
/// model payload is validated by the native store before admission. The block ids
/// and paths are the ones `catalog/authoring/object-fields.json` gives for host=enemy; `EnemyFieldSchema` is that table on
/// this side.
///
/// Strict by design: an unknown top-level key, an unknown enemy key, an unknown block id, an unknown path and a
/// value of the wrong kind are each refused with their own code and the offending name, so a typo cannot turn a
/// rule into a silent no-op. An entry that writes nothing (an empty block, a limb with no value, a transition with no
/// colour, a spawn attack that changes nothing) is no rule the game could break on, so it is skipped and named in the log
/// (enemy.field-unapplied, reason writes-nothing) and the rest of the document applies.
/// </summary>
internal static class EnemyBlockReader
{
    private static readonly string[] TopLevelKeys = { "schemaVersion", "enemies" };
    private static readonly string[] EnemyKeys = { "enemyType", "authorId", "displayName", "blocks", "sounds", "actions", "modelAssemblies", "nativeBindings", "resourceDefinitions", "resourceScope", "resourcePackages" };

    internal static EnemyProfileParse Read(string source, string json)
    {
        if (json.Length > EnemyProfileSchema.MaximumDocumentBytes)
            return EnemyProfileParse.Refused(EnemyProfileCodes.Budget, source, "Document exceeds the per-file byte cap.");
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException error)
        {
            return EnemyProfileParse.Refused(EnemyProfileCodes.Json, source, "Not valid JSON: " + error.Message);
        }
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return EnemyProfileParse.Refused(EnemyProfileCodes.Schema, source, "The document root must be an object.");
            if (UnknownKey(root, TopLevelKeys) is { } extra)
                return EnemyProfileParse.Refused(EnemyProfileCodes.UnknownKey, source, "Unknown or duplicate top-level key `" + extra + "`.");
            if (!ForgeCore.FormatVersion.Matches(root))
                return EnemyProfileParse.Refused(EnemyProfileCodes.Schema, source,
                    "`schemaVersion` must be the string " + ForgeCore.FormatVersion.Current + ".");

            var document = new EnemyProfileDocument(source);
            if (!root.TryGetProperty("enemies", out var enemies) || enemies.ValueKind != JsonValueKind.Array)
                return EnemyProfileParse.Refused(EnemyProfileCodes.Schema, source, "`enemies` must be an array.");
            if (enemies.GetArrayLength() > EnemyProfileSchema.MaximumEnemiesPerDocument)
                return EnemyProfileParse.Refused(EnemyProfileCodes.Budget, source, "Too many enemies.");
            var seen = new HashSet<uint>();
            foreach (var element in enemies.EnumerateArray())
            {
                var enemy = ReadEnemy(element, document, out var failure);
                if (enemy == null) return EnemyProfileParse.Refused(failure.Code, source, failure.Detail);
                // The same type twice in one file is the same data error as the same type in two files, caught
                // where it happens: the file is refused rather than silently keeping one of the two.
                if (!seen.Add(enemy.EnemyTypeId))
                    return EnemyProfileParse.Refused(EnemyProfileCodes.Duplicate, source,
                        "`enemyType` " + enemy.EnemyTypeId.ToString(CultureInfo.InvariantCulture)
                        + " is declared twice in this document.");
                document.Enemies.Add(enemy);
            }
            return EnemyProfileParse.Parsed(document);
        }
    }

    private static EnemyProfileEnemy? ReadEnemy(JsonElement element, EnemyProfileDocument document, out EnemyProfileRejection failure)
    {
        failure = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Schema, document.Source, "An enemy must be an object.");
            return null;
        }
        if (UnknownKey(element, EnemyKeys) is { } extra)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.UnknownKey, document.Source, "Unknown or duplicate enemy key `" + extra + "`.");
            return null;
        }
        // The type is the native data block's own `persistentID`: a positive whole number, because a zero or a
        // negative id names no block the game can build.
        if (!element.TryGetProperty("enemyType", out var typeElement) || typeElement.ValueKind != JsonValueKind.Number
            || !typeElement.TryGetUInt32(out uint enemyTypeId) || enemyTypeId == 0)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.EnemyType,
                document.Source, "An enemy has no positive integer `enemyType`.");
            return null;
        }
        if (!element.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Object)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Schema, document.Source, "An enemy needs a `blocks` object.");
            return null;
        }
        var set = new EnemyFieldSet(document.Source);
        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        var skippedBlocks = new List<string>();
        foreach (var property in blocks.EnumerateObject())
        {
            if (!EnemyFieldSchema.ById(property.Name, out var paths))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.UnknownBlock, document.Source,
                    "Unknown block id `" + property.Name + "`.");
                return null;
            }
            if (!blockIds.Add(property.Name))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.UnknownKey, document.Source,
                    "Block `" + property.Name + "` is declared twice.");
                return null;
            }
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Schema, document.Source,
                    "Block `" + property.Name + "` must be an object.");
                return null;
            }
            if (!property.Value.EnumerateObject().MoveNext())
            {
                skippedBlocks.Add(property.Name);
                continue;
            }
            if (Validate(property.Value, paths, property.Name, out string detail) is { } code)
            {
                failure = new EnemyProfileRejection(code, document.Source, detail);
                return null;
            }
            set.Add(property.Name, property.Value);
        }
        bool models = element.TryGetProperty("modelAssemblies", out _) || element.TryGetProperty("nativeBindings", out _)
            || element.TryGetProperty("resourceDefinitions", out _) || element.TryGetProperty("resourceScope", out _)
            || element.TryGetProperty("resourcePackages", out _);
        var sounds = Array.Empty<EnemySoundEntry>();
        if (element.TryGetProperty("sounds", out var soundsElement) && EnemySoundSlots.Read(soundsElement, out sounds) is { } soundProblem)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Value, document.Source, soundProblem);
            return null;
        }
        var actions = Array.Empty<EnemyActionEntry>();
        if (element.TryGetProperty("actions", out var actionsElement) && EnemyActionSlots.Read(actionsElement, out actions) is { } actionProblem)
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Value, document.Source, actionProblem);
            return null;
        }
        if (set.Count == 0 && skippedBlocks.Count == 0 && sounds.Length == 0 && actions.Length == 0 && (!element.TryGetProperty("modelAssemblies", out var assemblies)
            || assemblies.ValueKind != JsonValueKind.Array || assemblies.GetArrayLength() == 0))
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Schema, document.Source, "An enemy sets no block.");
            return null;
        }
        var resolved = EnemyFieldResolver.Resolve(set);
        foreach (string block in skippedBlocks) resolved.Skipped.Add((block, block));
        var enemy = new EnemyProfileEnemy(enemyTypeId, document.Source) { Fields = resolved, Sounds = sounds, Actions = actions, Models = models ? element.Clone() : null };
        // The work's name `<authorId>_<displayName>_<enemyType>` (the final in-game number): what the enemy's log lines say instead of a bare number.
        string? displayName = element.TryGetProperty("displayName", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
        string? authorId = element.TryGetProperty("authorId", out var authorElement) && authorElement.ValueKind == JsonValueKind.String ? authorElement.GetString() : null;
        if (displayName != null || authorId != null)
            enemy.Label = (authorId != null ? authorId + "_" : "") + (displayName != null ? displayName + "_" : "") + enemyTypeId.ToString(CultureInfo.InvariantCulture);
        if (BuildSections(enemy, resolved, out failure)) return null;
        return enemy;
    }

    /// <summary>Turns the typed block values into the sections the native appliers key on. A limb name that is not
    /// one of the editor's parts, a limb that sets nothing, and a limb multiplier with no type to reach are each
    /// refused by name.</summary>
    private static bool BuildSections(EnemyProfileEnemy enemy, EnemyFieldResolution fields, out EnemyProfileRejection failure)
    {
        failure = default;
        var limbs = new List<EnemyLimbProfile>();
        var parts = new HashSet<EnemyLimbPart>();
        foreach (var limb in fields.Limbs)
        {
            if (!EnemyLimbPart.TryParse(limb.Name, out var part))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Limb, enemy.Source,
                    "Limb `" + limb.Name + "` is not a body part, tumor1..tumor8 or a canonical native_<id> identity.");
                return true;
            }
            if (!parts.Add(part))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Duplicate, enemy.Source,
                    "Enemy " + enemy.EnemyTypeId + " names limb `" + part.Name + "` twice.");
                return true;
            }
            var profile = new EnemyLimbProfile(part) { Entry = limb.Entry };
            bool sets = false;
            if (limb.Health is float health)
            {
                if (!(health > 0f))
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                        "Limb `" + part.Name + "` health must be finite and greater than zero.");
                    return true;
                }
                profile.Health = health;
                sets = true;
            }
            if (limb.Type is { } type)
            {
                switch (type)
                {
                    case EnemyFieldLimbType.Normal:
                        profile.Kind = EnemyLimbKind.Normal;
                        sets = true;
                        break;
                    case EnemyFieldLimbType.Weakspot:
                        profile.Kind = EnemyLimbKind.Weakspot;
                        if (limb.CustomMultiplier is float weakspot) profile.WeakspotMultiplier = weakspot;
                        sets = true;
                        break;
                    case EnemyFieldLimbType.Armor:
                        profile.Kind = EnemyLimbKind.Armor;
                        if (limb.CustomMultiplier is float armor) profile.ArmorMultiplier = armor;
                        sets = true;
                        break;
                    case EnemyFieldLimbType.WeakspotCustom:
                        if (limb.CustomMultiplier is not float weakspotCustom)
                        {
                            failure = new EnemyProfileRejection(EnemyProfileCodes.Limb, enemy.Source,
                                "Limb `" + part.Name + "` is WeakspotCustom but sets no custom multiplier.");
                            return true;
                        }
                        profile.Kind = EnemyLimbKind.Weakspot;
                        profile.WeakspotMultiplier = weakspotCustom;
                        sets = true;
                        break;
                    case EnemyFieldLimbType.ArmorCustom:
                        if (limb.CustomMultiplier is not float armorCustom)
                        {
                            failure = new EnemyProfileRejection(EnemyProfileCodes.Limb, enemy.Source,
                                "Limb `" + part.Name + "` is ArmorCustom but sets no custom multiplier.");
                            return true;
                        }
                        profile.Kind = EnemyLimbKind.Armor;
                        profile.ArmorMultiplier = armorCustom;
                        sets = true;
                        break;
                }
            }
            else if (limb.CustomMultiplier != null)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Limb, enemy.Source,
                    "Limb `" + part.Name + "` sets a custom multiplier without a `LimbType` to reach.");
                return true;
            }
            // The three per-hit durability rules. `MaxDamage` is a cap and `DestroyedMultiplier` a multiplier, so
            // a zero or a negative one is not "unset": the hit-time rule reads a finite value and a cap of zero
            // would stop every hit, which is a rule the author cannot see in the data.
            if (limb.MaxDamage is float maxDamage)
            {
                if (!(maxDamage > 0f))
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                        "Limb `" + part.Name + "` max damage per hit must be finite and greater than zero.");
                    return true;
                }
                profile.MaxDamage = maxDamage;
                sets = true;
            }
            if (limb.DestroyedMultiplier is float destroyed)
            {
                if (destroyed < 0f)
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                        "Limb `" + part.Name + "` destroyed multiplier must be finite and non-negative.");
                    return true;
                }
                profile.DestroyedMultiplier = destroyed;
                sets = true;
            }
            if (limb.Breakable is bool breakable)
            {
                profile.Breakable = breakable;
                sets = true;
            }
            if (limb.Removed == true)
            {
                if (part.Kind == EnemyLimbPartKind.Torso)
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Limb, enemy.Source,
                        "Limb `torso` is the enemy's body and cannot be removed.");
                    return true;
                }
                profile.Removed = true;
                sets = true;
            }
            if (!sets)
            {
                fields.Skipped.Add(("limbs", part.Name));
                continue;
            }
            limbs.Add(profile);
        }
        enemy.Limbs = limbs;

        // A distance is compared with the game's own target distance, so a negative or non-finite value would be a
        // rule no position satisfies or every position does, neither of which the author wrote.
        if (fields.SneakWakeDistance is float sneak && !(sneak >= 0f && float.IsFinite(sneak)))
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                "`SneakDistance` must be finite and non-negative.");
            return true;
        }
        if (fields.SneakWakeDistance != null || fields.GunfireWakes != null || fields.DamageWakes != null)
            enemy.Wake = new EnemyWakeRule
            {
                SneakDistance = fields.SneakWakeDistance,
                GunfireWakes = fields.GunfireWakes,
                DamageWakes = fields.DamageWakes
            };
        if (fields.BackDamageMultiplier is float behind)
        {
            if (behind < 0f)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                    "`BackDamageMultiplier` must be finite and non-negative.");
                return true;
            }
            enemy.BackDamageMultiplier = behind;
        }
        // A negative share would take the level's own count below what is standing, and the game only frees a
        // share greater than zero, so neither can be written as authored.
        if (fields.CostTowardsCap is float capCost && !(capCost >= 0f && float.IsFinite(capCost)))
        {
            failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                "`CostTowardsCap` must be finite and non-negative.");
            return true;
        }
        var regionParts = new List<EnemyAppearancePart>();
        if (ReadRegionEntries(fields.Parts, "ModelRefs", enemy, regionParts, out failure)) return true;
        var regionMaterials = new List<EnemyAppearancePart>();
        if (ReadRegionMaterials(fields.MaterialSets, enemy, out var reskins, out failure)) return true;
        if (ReadRegionEntries(reskins, "MaterialSets", enemy, regionMaterials, out failure)) return true;
        EnemyPlacementPlan? placement = null;
        if (fields.Placement.Count != 0 && ReadPlacement(fields.Placement, enemy, out placement, out failure)) return true;
        if (fields.GlowTransition is float transition && !(transition >= 0f))
        {
            failure = Refuse(enemy, "`Glow.Transition` must be a finite number of seconds, zero or more.");
            return true;
        }
        // A transition with no colour would animate to whatever the prefab already had: it writes nothing, so it is
        // skipped and named, and the rest of the document applies.
        float? transitionSeconds = fields.GlowTransition;
        if (transitionSeconds != null && fields.GlowDefaultColor == null)
        {
            fields.Skipped.Add(("Glow", "Transition"));
            transitionSeconds = null;
        }
        if (fields.GlowDefaultColor != null || fields.Attachments.Count > 0 || regionParts.Count > 0
            || regionMaterials.Count > 0 || placement != null || transitionSeconds != null)
        {
            var appearance = new EnemyAppearanceProfile();
            if (fields.GlowDefaultColor is { } glow) appearance.GlowColor = glow;
            if (transitionSeconds is float seconds) appearance.GlowTransition = seconds;
            if (fields.Attachments.Count > 0) appearance.Attachments = fields.Attachments;
            appearance.Parts = regionParts;
            appearance.Materials = regionMaterials;
            appearance.Placement = placement;
            enemy.Appearance = appearance;
        }
        var look = new EnemyLookProfile { PropagateWakeupColor = fields.GlowPropagateWakeup };
        if (ReadMaterialSets(fields, enemy, look, out failure)) return true;
        if (ReadPulses(fields, enemy, look, out failure)) return true;
        if (ReadMarker(fields, enemy, look, out failure)) return true;
        if (ReadPartLooks(fields, enemy, look, out failure)) return true;
        if (!look.IsEmpty) enemy.Look = look;
        if (ReadAbilities(fields, enemy, out failure)) return true;
        return false;
    }

    /// <summary>The per-ability blocks. Every number must be finite; a length, a duration, a distance, a count, a
    /// cost, a threshold, a damage, a modifier and a size must also not be negative (the two tendril angles are the
    /// only signed values). Where one block names both ends of a native random range — the children per birth, the
    /// birth delay, the dash cooldown — the lower end must not exceed the upper, because the game draws between them
    /// and a reversed pair is a range the author did not mean. A scanner size needs its state's colour: the size is
    /// the colour's alpha, and the game's own colour of a state is not data this package can read.</summary>
    private static bool ReadAbilities(EnemyFieldResolution fields, EnemyProfileEnemy enemy, out EnemyProfileRejection failure)
    {
        failure = default;
        var abilities = new EnemyAbilityProfile { Spawning = new List<EnemySpawnAttackValues>(fields.SpawnAttacks).ToArray() };
        string? problem = EnemySpawnAttackValues.Problem(abilities.Spawning);
        if (problem == null)
        {
            // An attack row that changes nothing is skipped and named; it still counted for the duplicate check above.
            var kept = new List<EnemySpawnAttackValues>(abilities.Spawning.Length);
            foreach (var row in abilities.Spawning)
                if (row.ChangesNothing) fields.Skipped.Add(("Spawning.Attacks", "AbilityIndex " + row.AbilityIndex.ToString(CultureInfo.InvariantCulture)));
                else kept.Add(row);
            abilities.Spawning = kept.ToArray();
        }
        if (problem == null && fields.Feeler is { } feeler)
        {
            problem = Signed("Feeler.TendrilAngleOffset", feeler.AngleOffset) ?? Signed("Feeler.TendrilStepAngle", feeler.StepAngle)
                ?? NotNegative("Feeler.TendrilOutTimer", feeler.OutTimer) ?? NotNegative("Feeler.Distance", feeler.Distance)
                ?? NotNegative("Feeler.StepDistance", feeler.StepDistance);
            abilities.Feeler = feeler;
        }
        if (problem == null && fields.Scream is { } scream) abilities.Scream = scream;
        if (problem == null && fields.Birthing is { } birthing)
        {
            problem = Count("Birthing.ChildrenPerBirthMin", birthing.ChildrenPerBirthMin) ?? Count("Birthing.ChildrenMax", birthing.ChildrenMax)
                ?? NotNegative("Birthing.ChildrenCost", birthing.ChildrenCost);
            abilities.Birthing = birthing;
        }
        if (problem == null && fields.Pouncer is { } pouncer)
        {
            problem = NotNegative("Pouncer.DashCooldownMin", pouncer.DashCooldownMin) ?? NotNegative("Pouncer.DashCooldownMax", pouncer.DashCooldownMax)
                ?? NotNegative("Pouncer.DashMaxTime", pouncer.DashMaxTime) ?? NotNegative("Pouncer.ChargeDuration", pouncer.ChargeDuration)
                ?? NotNegative("Pouncer.ConsumeDuration", pouncer.ConsumeDuration) ?? NotNegative("Pouncer.DashEndPhaseDistance", pouncer.DashEndPhaseDistance)
                ?? NotNegative("Pouncer.ChargeStaggerDamageThreshold", pouncer.ChargeStaggerDamageThreshold)
                ?? NotNegative("Pouncer.DashStaggerDamageThreshold", pouncer.DashStaggerDamageThreshold)
                ?? NotNegative("Pouncer.StaggerDuration", pouncer.StaggerDuration)
                ?? NotNegative("Pouncer.FaceToTargetDuringProwlingDistance", pouncer.FaceToTargetDuringProwlingDistance)
                ?? NotNegative("Pouncer.HeldStateData.MaxHeldDuration", pouncer.MaxHeldDuration)
                ?? NotNegative("Pouncer.HeldStateData.DamageToPlayerPerSecond", pouncer.DamageToPlayerPerSecond)
                ?? NotNegative("Pouncer.HeldStateData.DamageOnStartHolding", pouncer.DamageOnStartHolding)
                ?? NotNegative("Pouncer.HeldStateData.AfterHeldRunAwayDuration", pouncer.AfterHeldRunAwayDuration)
                ?? NotNegative("Pouncer.HeldStateData.SpitOutStateDuration", pouncer.SpitOutStateDuration)
                ?? NotNegative("Pouncer.DashMovementModifier.SpeedModifier_AccelerationModifier", pouncer.DashMovementModifier)
                ?? NotNegative("Pouncer.DamageShapeData.Radius_Angle", pouncer.DamageShape)
                ?? Ordered("Pouncer.DashCooldownMin", pouncer.DashCooldownMin, "DashCooldownMax", pouncer.DashCooldownMax);
            abilities.Pouncer = pouncer;
        }
        if (problem == null && fields.FogSphere is { } fog)
        {
            problem = NotNegative("FogSphere.IntensityMin", fog.IntensityMin) ?? NotNegative("FogSphere.IntensityMax", fog.IntensityMax)
                ?? NotNegative("FogSphere.RangeMin", fog.RangeMin) ?? NotNegative("FogSphere.RangeMax", fog.RangeMax)
                ?? NotNegative("FogSphere.DensityMin", fog.DensityMin) ?? NotNegative("FogSphere.DensityMax", fog.DensityMax)
                ?? NotNegative("FogSphere.DensityAmountMin", fog.DensityAmountMin) ?? NotNegative("FogSphere.DensityAmountMax", fog.DensityAmountMax);
            if (problem == null && fog.Duration is float duration && !(duration > 0f && float.IsFinite(duration)))
                problem = "`FogSphere.Duration` must be a finite number of seconds greater than zero";
            abilities.FogSphere = fog;
        }
        if (problem == null && fields.Scanner is { } scanner)
        {
            var written = new float[]?[EnemyScannerValues.Names.Length];
            for (int index = 0; index < written.Length && problem == null; index++)
            {
                string name = "Scanner.Internal_" + EnemyScannerValues.Names[index];
                if (scanner.Sizes[index] != null && scanner.Colors[index] == null)
                    problem = "`" + name + "Size` is set without `" + name[8..] + "Color`; the size is that colour's alpha";
                else problem = NotNegative(name + "Size", scanner.Sizes[index]);
                written[index] = scanner.Written((EnemyScannerState)index);
            }
            abilities.Scanner = written;
        }
        if (problem != null) { failure = Refuse(enemy, problem + "."); return true; }
        if (!abilities.IsEmpty) enemy.Abilities = abilities;
        return false;
    }

    private static string? Signed(string name, float? value)
        => value is float number && !float.IsFinite(number) ? "`" + name + "` must be a finite number" : null;

    private static string? NotNegative(string name, float? value)
        => value is float number && !(number >= 0f && float.IsFinite(number)) ? "`" + name + "` must be a finite number, zero or more" : null;

    private static string? NotNegative(string name, float[]? values)
    {
        if (values == null) return null;
        foreach (float number in values) if (!(number >= 0f && float.IsFinite(number))) return "`" + name + "` must be finite numbers, zero or more";
        return null;
    }

    private static string? Count(string name, int? value)
        => value is int count && count < 0 ? "`" + name + "` must be a whole number, zero or more" : null;

    private static string? Ordered(string lowName, float? low, string highName, float? high)
        => low is float from && high is float to && from > to ? "`" + lowName + "` must not exceed `" + highName + "`" : null;

    private static EnemyProfileRejection Refuse(EnemyProfileEnemy enemy, string detail)
        => new(EnemyProfileCodes.Value, enemy.Source, "Enemy " + enemy.EnemyTypeId.ToString(CultureInfo.InvariantCulture) + ": " + detail);

    /// <summary>The region entries of `MaterialSets` that reskin a region: a region set names the vanilla model in
    /// `To`. A region other than `full` is a reskin and nothing else — it needs `To` and takes no property, because
    /// its properties would have no material list of their own to land on. `full` may carry a reskin, properties
    /// for every material, or both, but not neither.</summary>
    private static bool ReadRegionMaterials(List<EnemyMaterialSetValue> sets, EnemyProfileEnemy enemy,
        out List<EnemyAppearanceRegionValue> reskins, out EnemyProfileRejection failure)
    {
        failure = default;
        reskins = new List<EnemyAppearanceRegionValue>();
        foreach (var value in sets)
        {
            if (!value.Region) continue;
            bool properties = value.Floats.Count != 0;
            if (value.From != "full" && properties)
            { failure = Refuse(enemy, "`MaterialSets` region `" + value.From + "` reskins a region and takes no property; material properties go on `full` or on a named material."); return true; }
            if (value.To == null && !properties)
            { enemy.Fields?.Skipped.Add(("MaterialSets", value.From ?? "")); continue; }
            if (value.To != null) reskins.Add(new EnemyAppearanceRegionValue { Region = value.From, Model = value.To });
        }
        return false;
    }

    /// <summary>`MaterialSets`: `From` is required and unique (EEC keeps the first of a duplicate silently; here it
    /// is refused) and every property names itself. A set without `To` and without
    /// a property would write nothing and is refused; a non-region set naming `To` is reported unapplied by the
    /// resolver and is not part of the look. The `full` region set's properties go on every material that has
    /// them; the other region sets are reskins only (<see cref="ReadRegionMaterials"/>).</summary>
    private static bool ReadMaterialSets(EnemyFieldResolution fields, EnemyProfileEnemy enemy, EnemyLookProfile look,
        out EnemyProfileRejection failure)
    {
        failure = default;
        if (fields.MaterialSets.Count == 0) return false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sets = new List<EnemyMaterialSetProfile>();
        foreach (var value in fields.MaterialSets)
        {
            if (string.IsNullOrWhiteSpace(value.From)) { failure = Refuse(enemy, "a `MaterialSets` entry has no `From` material name."); return true; }
            if (!seen.Add(value.From)) { failure = Refuse(enemy, "`MaterialSets` names `From` `" + value.From + "` twice."); return true; }
            if (value.Region)
            {
                if (value.Floats.Count == 0) continue;
            }
            else if (value.NamesTo) continue;
            if (value.Floats.Count == 0)
            { fields.Skipped.Add(("MaterialSets", value.From)); continue; }
            var profile = new EnemyMaterialSetProfile(value.From) { AllMaterials = value.Region };
            foreach (var (name, amount) in value.Floats)
            {
                if (string.IsNullOrWhiteSpace(name)) { failure = Refuse(enemy, "a float property of `" + value.From + "` has no name."); return true; }
                if (amount is not float number || !float.IsFinite(number))
                { failure = Refuse(enemy, "float property `" + name + "` of `" + value.From + "` has no finite value."); return true; }
                profile.Floats.Add((name, number));
            }
            sets.Add(profile);
        }
        look.MaterialSets = sets;
        return false;
    }

    /// <summary>`PulseEffects`: every entry needs a pattern the EEC grammar reads, a positive cycle length, a mode
    /// target and a colour. EEC would default a missing colour to transparent black and a missing target to no mode,
    /// which plays nothing; both are refused by name here instead.</summary>
    private static bool ReadPulses(EnemyFieldResolution fields, EnemyProfileEnemy enemy, EnemyLookProfile look,
        out EnemyProfileRejection failure)
    {
        failure = default;
        if (fields.Pulses.Count == 0) return false;
        var pulses = new List<EnemyPulseProfile>(fields.Pulses.Count);
        for (int index = 0; index < fields.Pulses.Count; index++)
        {
            var value = fields.Pulses[index];
            string label = "`PulseEffects` entry " + (index + 1).ToString(CultureInfo.InvariantCulture);
            if (!EnemyGlowPattern.TryParse(value.Pattern, out var steps, out string error))
            { failure = Refuse(enemy, label + " glow pattern: " + error + "."); return true; }
            if (value.Duration is not float duration || !(duration > 0f) || !float.IsFinite(duration))
            { failure = Refuse(enemy, label + " needs a finite `Duration` greater than zero."); return true; }
            if (value.Target is not { } rawTarget || !EnemyModeTargets.TryParse(rawTarget.Text, rawTarget.Integer, out var target, out error))
            { failure = Refuse(enemy, label + " target: " + (value.Target == null ? "no mode is named" : error) + "."); return true; }
            if (value.Color is not { } color)
            { failure = Refuse(enemy, label + " needs a `GlowColor`."); return true; }
            pulses.Add(new EnemyPulseProfile(steps, duration, color, target)
            {
                AlwaysPulse = value.AlwaysPulse ?? false,
                KeepOnDead = value.KeepOnDead ?? false,
                RandomizeTime = value.RandomizeTime ?? false
            });
        }
        look.Pulses = pulses;
        return false;
    }

    /// <summary>`forge.eec.Model.Marker`. Where EEC ignores a member silently this reader refuses it by name: a
    /// blink member without `Blink`, a non-positive or non-finite size or duration, an `Icon` that is none of
    /// vanilla, triangle, circle, square or diamond, and delays outside `0 &lt;= BlinkMinDelay &lt; BlinkMaxDelay`
    /// (EEC then schedules no blink). EEC's defaults fill whatever the author left out of an enabled blink.
    /// `AllowMarkingOnHibernate` is a spawn write and needs no marker. `MarkerColor`, `SpriteScale` and `Icon` stand
    /// on their own; the size is a multiple of the vanilla one, so it must be finite and greater than zero.</summary>
    private static bool ReadMarker(EnemyFieldResolution fields, EnemyProfileEnemy enemy, EnemyLookProfile look,
        out EnemyProfileRejection failure)
    {
        failure = default;
        look.AllowMarkingOnHibernate = fields.MarkerAllowOnHibernate ?? false;
        bool declared = fields.MarkerIconDeclared || fields.MarkerBlinkIn != null
            || fields.MarkerBlink != null || fields.MarkerBlinkDuration != null || fields.MarkerBlinkMinDelay != null
            || fields.MarkerBlinkMaxDelay != null || fields.MarkerColor != null || fields.MarkerSpriteScale != null;
        if (!declared) return false;
        var profile = new EnemyMarkerProfile { Color = fields.MarkerColor };
        if (fields.MarkerIconDeclared)
        {
            if (!EnemyMarkerIcons.TryParse(fields.MarkerIcon, out var icon))
            { failure = Refuse(enemy, "`Marker.Icon` must be one of vanilla, triangle, circle, square or diamond."); return true; }
            profile.Icon = icon;
        }
        if (fields.MarkerSpriteScale is float spriteScale)
        {
            if (!(spriteScale > 0f) || !float.IsFinite(spriteScale))
            { failure = Refuse(enemy, "`Marker.SpriteScale` must be a finite number greater than zero."); return true; }
            profile.SpriteScale = spriteScale;
        }
        profile.BlinkIn = fields.MarkerBlinkIn ?? false;
        if (fields.MarkerBlink == true)
        {
            float duration = fields.MarkerBlinkDuration ?? 30f, minDelay = fields.MarkerBlinkMinDelay ?? 1f, maxDelay = fields.MarkerBlinkMaxDelay ?? 5f;
            if (!(duration > 0f) || !float.IsFinite(duration))
            { failure = Refuse(enemy, "`Marker.BlinkDuration` must be a finite number greater than zero."); return true; }
            if (!float.IsFinite(minDelay) || !float.IsFinite(maxDelay) || !(minDelay >= 0f) || !(minDelay < maxDelay))
            { failure = Refuse(enemy, "`Marker.BlinkMinDelay` and `BlinkMaxDelay` must satisfy 0 <= BlinkMinDelay < BlinkMaxDelay, or no blink is scheduled."); return true; }
            profile.Blink = new EnemyMarkerBlink(duration, minDelay, maxDelay);
        }
        else if (fields.MarkerBlinkDuration != null || fields.MarkerBlinkMinDelay != null || fields.MarkerBlinkMaxDelay != null)
            fields.Skipped.Add(("Marker", "Blink timing without Blink"));
        if (profile.Icon == EnemyMarkerIcon.Vanilla && !profile.BlinkIn && profile.Blink == null
            && profile.Color == null && profile.SpriteScale == null) return false;
        look.Marker = profile;
        return false;
    }

    /// <summary>`forge.enemyAppearance.Looks`: every entry names a known target once, carries a colour, a glow or
    /// both, and the colour and glow are three finite non-negative numbers (or colour text of three channels). The
    /// whole-body entry (`full`) takes a colour only: a fixed glow belongs to a part. A glow brightness needs a glow
    /// and is finite and zero or more. A member that is present but unreadable is refused by name.</summary>
    private static bool ReadPartLooks(EnemyFieldResolution fields, EnemyProfileEnemy enemy, EnemyLookProfile look,
        out EnemyProfileRejection failure)
    {
        failure = default;
        if (fields.Looks.Count == 0) return false;
        var seen = new HashSet<EnemyLookTarget>();
        var looks = new List<EnemyPartLook>(fields.Looks.Count);
        for (int index = 0; index < fields.Looks.Count; index++)
        {
            var value = fields.Looks[index];
            string label = "`Looks` entry " + (index + 1).ToString(CultureInfo.InvariantCulture);
            if (!EnemyLookTargets.TryParse(value.Target, out var target))
            {
                failure = Refuse(enemy, label + " needs a `Target`: full, head, torso, leftArm, rightArm, leftLeg, rightLeg, core, "
                    + "leftAppendage, rightAppendage, neck or tumor1 to tumor8.");
                return true;
            }
            if (!seen.Add(target)) { failure = Refuse(enemy, "`Looks` names target `" + value.Target + "` twice."); return true; }
            label += " (" + value.Target + ")";
            if (value.ColorDeclared && value.Color == null)
            { failure = Refuse(enemy, label + " `Color` is not three numbers or colour text."); return true; }
            if (value.GlowDeclared && value.Glow == null)
            { failure = Refuse(enemy, label + " `Glow` is not three numbers or colour text."); return true; }
            if (value.BrightnessDeclared && value.GlowBrightness == null)
            { failure = Refuse(enemy, label + " `GlowBrightness` is not a number."); return true; }
            if (value.InvisibleDeclared && value.Invisible == null)
            { failure = Refuse(enemy, label + " `Invisible` is not true or false."); return true; }
            if (value.Color == null && value.Glow == null && value.Invisible == null)
            { failure = Refuse(enemy, label + " sets none of `Color`, `Glow` and `Invisible`, so it writes nothing."); return true; }
            if (value.Color is { } color && ChannelProblem(color) is { } colorProblem)
            { failure = Refuse(enemy, label + " `Color` " + colorProblem); return true; }
            if (value.Glow is { } glow && ChannelProblem(glow) is { } glowProblem)
            { failure = Refuse(enemy, label + " `Glow` " + glowProblem); return true; }
            if (target == EnemyLookTarget.Full && value.Glow != null)
            { failure = Refuse(enemy, "`Looks` entry `full` takes a colour only; a fixed glow belongs to a part."); return true; }
            if (value.GlowBrightness != null && value.Glow == null)
            { failure = Refuse(enemy, label + " sets `GlowBrightness` without a `Glow` to scale."); return true; }
            if (value.GlowBrightness is float brightness && !(brightness >= 0f && float.IsFinite(brightness)))
            { failure = Refuse(enemy, label + " `GlowBrightness` must be a finite number, zero or more."); return true; }
            looks.Add(new EnemyPartLook(target)
            {
                Color = value.Color,
                Glow = value.Glow,
                GlowBrightness = value.GlowBrightness ?? 1f,
                Invisible = value.Invisible
            });
        }
        look.PartLooks = looks;
        return false;
    }

    private static string? ChannelProblem(float[] channels)
    {
        if (channels.Length != 3) return "must be exactly three channels (red, green, blue); a fourth is refused rather than ignored.";
        foreach (float channel in channels)
            if (!(channel >= 0f && float.IsFinite(channel))) return "channels must be finite numbers, zero or more.";
        return null;
    }
    /// <summary>The region entries of `ModelRefs` (part swaps) or `MaterialSets` (reskins): each a region and a
    /// vanilla model asset path. A region named twice is refused, and `full` stands alone — a whole-look replacement
    /// next to a region entry would leave the result depending on which one the applier ran first. A region swap
    /// that also sets an attach-point member (`CopyFrom`, `Offset`, `RotateOffset`) is refused: the swap is remapped
    /// onto this type's own skeleton, so those members would have nothing to act on.</summary>
    private static bool ReadRegionEntries(List<EnemyAppearanceRegionValue> values, string key, EnemyProfileEnemy enemy,
        List<EnemyAppearancePart> list, out EnemyProfileRejection failure)
    {
        failure = default;
        foreach (var value in values)
        {
            if (value.Region == null || !EnemyAppearanceRegions.TryParse(value.Region, out var region))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                    "A `" + key + "` entry needs a region: `full`, `head`, `torso`, `leftArm`, `rightArm`, `leftLeg` or `rightLeg`.");
                return true;
            }
            if (value.Extra != null)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                    "`" + key + "` region `" + value.Region + "` sets `" + value.Extra + "`, which only an EEC attach point takes.");
                return true;
            }
            if (value.Model == null || value.Model.Length > EnemyProfileSchema.MaximumAssetPathLength)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                    "A `" + key + "` entry needs a model asset path of at most "
                    + EnemyProfileSchema.MaximumAssetPathLength.ToString(CultureInfo.InvariantCulture) + " characters.");
                return true;
            }
            foreach (var known in list)
            {
                if (known.Region == region)
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Duplicate, enemy.Source,
                        "Enemy " + enemy.EnemyTypeId + " names `" + key + "` region `"
                        + EnemyAppearanceRegions.Name(region) + "` twice.");
                    return true;
                }
                if (known.Region == EnemyAppearanceRegion.Full || region == EnemyAppearanceRegion.Full)
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                        "Enemy " + enemy.EnemyTypeId + " combines `full` with another `" + key + "` region.");
                    return true;
                }
            }
            list.Add(new EnemyAppearancePart(region, value.Model));
        }
        return false;
    }

    /// <summary>The `Bones` entries whose `Bone` is a placement slot: one entry per slot the author moved, each a
    /// Unity model-local transform. The slot names are the website's own (`ENEMY_PLACEMENT_TARGETS` in
    /// `Web/src/forge/enemy/enemy-editor-model.ts`), and a slot named twice is refused instead of one of the two
    /// winning. Every transform states all three members: a placement that left one out would silently keep the
    /// prefab's own, which is a rule the author cannot see in the data. `RotationOffset` is Unity Euler degrees
    /// (<see cref="EnemyPlacementTransform.FromEuler"/>), and the scale multiplies the slot's own, so
    /// `UsingRelativeScale: false` is refused.</summary>
    private static bool ReadPlacement(List<EnemyPlacementValue> values, EnemyProfileEnemy enemy,
        out EnemyPlacementPlan? plan, out EnemyProfileRejection failure)
    {
        failure = default;
        plan = null;
        var accepted = new EnemyPlacementPlan();
        foreach (var value in values)
        {
            if (value.Target == null || !EnemyPlacementSlot.TryParse(value.Target, out var slot))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                    "Unknown placement target `" + (value.Target ?? "(none)") + "`.");
                return true;
            }
            if (accepted.TryGet(slot, out _))
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Duplicate, enemy.Source,
                    "Enemy " + enemy.EnemyTypeId + " names placement `" + slot.Name + "` twice.");
                return true;
            }
            string key = "Bones." + slot.Name;
            if (value.Position == null || value.Rotation == null || value.Scale == null)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                    "`" + key + "` needs an `Offset`, a `RotationOffset` and a `Scale`.");
                return true;
            }
            if (value.RelativeScale == false)
            {
                failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                    "`" + key + "` places a body slot, whose scale multiplies the slot's own; `UsingRelativeScale` must not be false.");
                return true;
            }
            foreach (float angle in value.Rotation)
                if (!float.IsFinite(angle))
                {
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                        "Every `" + key + "` rotation angle must be a finite number of degrees.");
                    return true;
                }
            foreach (float component in value.Scale)
                if (component <= 0f)
                {
                    // A zero scale would collapse the part instead of placing it, and the website's own transform
                    // editor refuses it; a negative one mirrors the mesh through its own origin.
                    failure = new EnemyProfileRejection(EnemyProfileCodes.Value, enemy.Source,
                        "Every `" + key + "` scale component must be greater than zero.");
                    return true;
                }
            accepted.Add(new EnemyPlacementEntry(slot,
                new EnemyPlacementTransform(value.Position, EnemyPlacementTransform.FromEuler(value.Rotation), value.Scale)));
        }
        plan = accepted;
        return false;
    }

    /// <summary>Validates one block object against the table's paths: an unknown key, a member that is neither an
    /// object nor the array of the repeated group it names, and a leaf of the wrong value kind are each refused
    /// with the block and key that caused it.</summary>
    private static string? Validate(JsonElement node, IReadOnlyList<EnemyFieldPath> paths, string block, out string detail)
    {
        detail = string.Empty;
        foreach (var property in node.EnumerateObject())
        {
            var candidates = new List<EnemyFieldPath>();
            foreach (var path in paths)
            {
                if (path.Path == property.Name) { candidates.Add(path); continue; }
                if (path.Path.StartsWith(property.Name + ".", StringComparison.Ordinal)) candidates.Add(path);
            }
            if (candidates.Count == 0)
            {
                detail = "Unknown path `" + property.Name + "` in block `" + block + "`.";
                return EnemyProfileCodes.UnknownKey;
            }
            bool leaf = false;
            foreach (var candidate in candidates) if (candidate.Path == property.Name) { leaf = true; break; }
            if (leaf)
            {
                var kind = candidates.Find(candidate => candidate.Path == property.Name).Kind;
                if (block == EnemyFieldSchema.AnimHandle)
                {
                    if (ValidateControllerValues(property.Value, kind, block + "." + property.Name, out detail) is { } controllerCode) return controllerCode;
                    continue;
                }
                if (!KindMatches(kind, property.Value))
                {
                    detail = "`" + block + "." + property.Name + "` must be a " + Describe(kind) + ".";
                    return EnemyProfileCodes.Value;
                }
                continue;
            }
            var tails = new List<EnemyFieldPath>(candidates.Count);
            foreach (var candidate in candidates) tails.Add(new EnemyFieldPath(block, candidate.Path[(property.Name.Length + 1)..], candidate.Kind));
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                if (!EnemyFieldSchema.IsGroup(property.Name))
                {
                    detail = "`" + block + "." + property.Name + "` is not a list.";
                    return EnemyProfileCodes.Value;
                }
                foreach (var entry in property.Value.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        detail = "`" + block + "." + property.Name + "` entries must be objects.";
                        return EnemyProfileCodes.Value;
                    }
                    if (Validate(entry, tails, block, out detail) is { } code) return code;
                }
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                detail = "`" + block + "." + property.Name + "` must be an object or a list.";
                return EnemyProfileCodes.Value;
            }
            if (Validate(property.Value, tails, block, out detail) is { } code2) return code2;
        }
        return null;
    }

    private static string? ValidateControllerValues(JsonElement value, EnemyFieldKind kind, string path, out string detail)
    {
        detail = "`" + path + "` requires a non-empty list of {animationController, value}; the old shared-value format is invalid.";
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 12) return EnemyProfileCodes.Value;
        var seen = new HashSet<int>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || UnknownKey(entry, new[] { "animationController", "value" }) != null
                || !entry.TryGetProperty("animationController", out var identity) || identity.ValueKind != JsonValueKind.Number || !identity.TryGetInt32(out int controller)
                || EnemyAnimHandleOverrides.ControllerIndex(controller) < 0 || !entry.TryGetProperty("value", out var scalar)) return EnemyProfileCodes.Value;
            int members = 0;
            foreach (var unused in entry.EnumerateObject()) members++;
            if (members != 2) return EnemyProfileCodes.Value;
            if (!seen.Add(controller))
            {
                detail = "`" + path + "` declares animationController " + controller + " twice.";
                return EnemyProfileCodes.Duplicate;
            }
            if (!KindMatches(kind, scalar)) return EnemyProfileCodes.Value;
            if (kind == EnemyFieldKind.Number && (!scalar.TryGetSingle(out float number) || !float.IsFinite(number) || number < 0)) return EnemyProfileCodes.Value;
            if (kind == EnemyFieldKind.TimingList)
            {
                if (scalar.GetArrayLength() != 1 || !scalar[0].EnumerateObject().MoveNext()) return EnemyProfileCodes.Value;
                foreach (var member in scalar[0].EnumerateObject())
                    if (!member.Value.TryGetSingle(out float timing) || !float.IsFinite(timing) || timing < 0) return EnemyProfileCodes.Value;
            }
        }
        detail = string.Empty;
        return null;
    }

    private static bool KindMatches(EnemyFieldKind kind, JsonElement value)
    {
        switch (kind)
        {
            case EnemyFieldKind.Number: return value.ValueKind == JsonValueKind.Number;
            case EnemyFieldKind.Integer: return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);
            case EnemyFieldKind.UInt32: return value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out _);
            case EnemyFieldKind.Boolean: return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case EnemyFieldKind.Text: return value.ValueKind == JsonValueKind.String;
            case EnemyFieldKind.Enum: return value.ValueKind is JsonValueKind.String or JsonValueKind.Number;
            case EnemyFieldKind.Vector3:
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) return false;
                foreach (var component in value.EnumerateArray()) if (component.ValueKind != JsonValueKind.Number) return false;
                return true;
            case EnemyFieldKind.Color:
                if (value.ValueKind == JsonValueKind.String) return EnemyColorText.TryParse(value.GetString(), out _);
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is not (3 or 4)) return false;
                foreach (var component in value.EnumerateArray()) if (component.ValueKind != JsonValueKind.Number) return false;
                return true;
            case EnemyFieldKind.TimingList:
                if (value.ValueKind != JsonValueKind.Array) return false;
                foreach (var entry in value.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) return false;
                    foreach (var member in entry.EnumerateObject())
                    {
                        // The resolver reads a timing member as a float and SFXId as an int. Validate each
                        // supplied member before resolution so a malformed sibling cannot be silently ignored
                        // after another member makes the entry count as applied.
                        if (member.Value.ValueKind != JsonValueKind.Number) return false;
                        switch (member.Name)
                        {
                            case "Duration":
                            case "DamageStart":
                            case "DamageEnd":
                                if (!member.Value.TryGetSingle(out _)) return false;
                                break;
                            case "SFXId":
                                if (!member.Value.TryGetInt32(out _)) return false;
                                break;
                            default: return false;
                        }
                    }
                }
                return true;
            case EnemyFieldKind.Pair:
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2) return false;
                foreach (var component in value.EnumerateArray()) if (component.ValueKind != JsonValueKind.Number) return false;
                return true;
            case EnemyFieldKind.List: return value.ValueKind == JsonValueKind.Array;
            case EnemyFieldKind.Switch:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    || (value.ValueKind == JsonValueKind.String && value.GetString() is "True" or "False" or "Unchanged");
            default: return false;
        }
    }

    private static string Describe(EnemyFieldKind kind) => kind switch
    {
        EnemyFieldKind.Pair => "list of two numbers",
        EnemyFieldKind.Switch => "`True`, `False` or `Unchanged`",
        _ => kind.ToString().ToLowerInvariant()
    };

    private static string? UnknownKey(JsonElement element, string[] accepted)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || Array.IndexOf(accepted, property.Name) < 0) return property.Name;
        return null;
    }
}

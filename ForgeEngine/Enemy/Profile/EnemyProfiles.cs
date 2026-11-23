using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ForgeEnemy.Profile;

/// <summary>The enemy profile family: what a website-exported `forge/enemies/*.forge` document may say, and what
/// the game side is allowed to conclude from a set of them. This file is the whole store and the derivation that
/// is game-independent on purpose — the same entry is called from `Plugin.Load` (through the filesystem
/// discovery) and from the tests' fixtures, so the accepted shape and the rejection codes cannot drift from what
/// is shipped.
///
/// The document shape is the one the framework plan fixes (contract §3.3): one object per enemy type,
/// `{schemaVersion, enemies: [{enemyType, blocks: {<block id>: <value expanded by field path>}, ...models}]}`, with
/// `enemyType` the native data block's `persistentID`, the block ids and paths the ones `catalog/authoring/object-fields.json`
/// gives for host=enemy, and the values those paths name. There is no per-attribute section any more: the old
/// `limbs` / `detection` / `appearance` / `birthing` sections were a second shape for the same fields, and the
/// block shape is the only one read. `EnemyBlockReader` is the one parser; `EnemyFieldResolver` turns the blocks
/// into the typed values the native sink writes.
///
/// Why this is data and not a capability row. The facts here are per-enemy constants: which limbs a type has, how
/// tough they are, how far it detects, what colour it glows. They do not change while a level runs, so there is
/// nothing for a plan step to decide, nothing to replicate and no host/client divergence to resolve: every peer
/// applies the identical document to the identical native setup.
///
/// One enemy type, one document. The same `enemyType` twice in the loaded documents is a data error rather than a
/// precedence question: that enemy is refused whole, with both file names named, because letting load order
/// decide would make the same file mean different things after an unrelated edit. A block or path no document
/// sets is left exactly as the prefab authored it.
/// </summary>
internal static class EnemyProfileSchema
{
    internal const int MaximumDocuments = 64;
    internal const int MaximumDocumentBytes = PublishedResources.MaximumFileBytes;
    internal const int MaximumEnemiesPerDocument = 256;
    internal const int MaximumAssetPathLength = 260;
}

/// <summary>Which native limb damage type a limb is. The names are the interop enum's, spelled out so a
/// document never carries a raw integer whose meaning only the current build knows.</summary>
internal enum EnemyLimbKind
{
    Normal = 0,
    Weakspot = 1,
    Armor = 2
}

/// <summary>One limb's overrides, resolved from the `forge.eec.Model.Limb` block. A null member means "this
/// document says nothing about that attribute", which is different from a zero. The part is the author's own
/// word (`head`, `tumor3`, …); which native limb that is is resolved from the enemy prefab once per type, so the
/// document never carries a limb index that only the current build's prefab order would explain.</summary>
internal sealed class EnemyLimbProfile
{
    internal EnemyLimbProfile(EnemyLimbPart part) { Part = part; }

    internal EnemyLimbPart Part { get; }

    /// <summary>The position of the entry in the document's `Limbs` list this profile was read from.</summary>
    internal int Entry { get; init; }
    internal float? Health { get; set; }
    internal float? WeakspotMultiplier { get; set; }
    internal float? ArmorMultiplier { get; set; }
    internal EnemyLimbKind? Kind { get; set; }

    /// <summary>`false` blocks the host's destruction of this part; `true` asks for a destruction the limb must
    /// already have, from `Limbs.Breakable`.</summary>
    internal bool? Breakable { get; set; }

    /// <summary>The value true removes the limb's part from every live enemy of the type: model hidden, collider and damage limb off.</summary>
    internal bool Removed { get; set; }

    /// <summary>The damage multiplier a destroyed limb takes, applied on the limb's own modifier result.</summary>
    internal float? DestroyedMultiplier { get; set; }

    /// <summary>The most damage one hit may deal to this limb.</summary>
    internal float? MaxDamage { get; set; }
}

/// <summary>
/// The wake rules of one enemy type, from `forge.enemyWake` (see `EnemyModule.Wake` for what the game does):
///   * <see cref="SneakDistance"/>: a sneaking player inside this distance is heard like a walking one, and outside it
///     is not heard at all — in place of the game's own fixed 1.9 m close-sneak rule and its noise-wave window;
///   * <see cref="GunfireWakes"/> false: a player's shot (`Shoot`, `ShootSilenced`) is not heard. Sounds the level's
///     noise system carries (sentry guns, melee hits, mines) are not this switch's;
///   * <see cref="DamageWakes"/> false: a hit that does not kill neither detects its attacker nor plays a hit reaction
///     while the enemy is asleep.
/// Unset members keep the game's own behaviour.
/// </summary>
internal sealed class EnemyWakeRule
{
    internal float? SneakDistance { get; init; }
    internal bool? GunfireWakes { get; init; }
    internal bool? DamageWakes { get; init; }

    /// <summary>Whether the rule changes what the sleeping enemy hears (the noise-window hook's business).</summary>
    internal bool ChangesHearing => SneakDistance != null || GunfireWakes == false;

    /// <summary>Whether the rule keeps a hit from waking the enemy (the damage hooks' business).</summary>
    internal bool KeepsAsleepOnDamage => DamageWakes == false;
}

/// <summary>The glow the enemy's own glow shader value animates to, plus the prefabs the `forge.eec.Model.Bone`
/// block hangs on named bones. Parts, materials and placement are assembled once on the enemy prefab from vanilla
/// assets already on the player's machine; see `EnemyModule.Appearance`. The three are the region entries of the
/// EEC blocks — parts from `forge.eec.Model.ModelRef` `ModelRefs`, reskins from `forge.eec.Model.Material`
/// `MaterialSets`, placement from `forge.eec.Model.Bone` `Bones` — the glow colour and its transition come from
/// `forge.eec.Model.Glow` and the attachments from `forge.eec.Model.Bone.Prefabs`.</summary>
internal sealed class EnemyAppearanceProfile
{
    internal float[]? GlowColor { get; set; }
    internal float? GlowTransition { get; set; }
    internal IReadOnlyList<EnemyAppearancePart> Parts { get; set; } = Array.Empty<EnemyAppearancePart>();
    internal IReadOnlyList<EnemyAppearancePart> Materials { get; set; } = Array.Empty<EnemyAppearancePart>();
    internal IReadOnlyList<EnemyAppearanceAttachment> Attachments { get; set; } = Array.Empty<EnemyAppearanceAttachment>();
    internal EnemyPlacementPlan? Placement { get; set; }

    /// <summary>Whether anything here is assembled onto the prefab (as opposed to glow, written at spawn).</summary>
    internal bool Assembles => Parts.Count > 0 || Materials.Count > 0 || Attachments.Count > 0 || Placement != null;
}

/// <summary>One region taken from another vanilla enemy model: `Model` is that model's game asset path (a
/// `ModelDatas[].ModelFile` value). Filled from one region entry of `ModelRefs` (`CreateFromPath`) or of
/// `MaterialSets` (`To`).</summary>
internal sealed record EnemyAppearancePart(EnemyAppearanceRegion Region, string Model);

/// <summary>A vanilla attachment prefab hung on a target bone, filled from one `forge.eec.Model.Bone.Prefabs`
/// entry. `Prefab` is `&lt;ModelCustomization asset path&gt;#&lt;prefab name&gt;`, the key the ForgeDevelopment
/// inventory lists for every `CB_CustomPrefab` entry; the three vectors are local to the bone (position, Euler
/// degrees, scale) and default to identity.</summary>
internal sealed class EnemyAppearanceAttachment
{
    internal EnemyAppearanceAttachment(string prefab, string bone) { Prefab = prefab; Bone = bone; }

    internal string Prefab { get; }
    internal string Bone { get; }
    internal float[] Position { get; set; } = { 0f, 0f, 0f };
    internal float[] Rotation { get; set; } = { 0f, 0f, 0f };
    internal float[] Scale { get; set; } = { 1f, 1f, 1f };

    /// <summary>The asset half of <see cref="Prefab"/>.</summary>
    internal string Asset => Prefab[..Prefab.IndexOf('#')];

    /// <summary>The prefab-name half of <see cref="Prefab"/>.</summary>
    internal string Name => Prefab[(Prefab.IndexOf('#') + 1)..];
}

/// <summary>One enemy type's profile, resolved from its block document plus the sections the native appliers
/// still key on (limbs, wake rules, glow). The block set itself is carried so the native sink can write the
/// blocks whose native carrier is not one of those sections.</summary>
internal sealed class EnemyProfileEnemy
{
    internal EnemyProfileEnemy(uint enemyTypeId, string source) { EnemyTypeId = enemyTypeId; Source = source; }

    internal uint EnemyTypeId { get; }

    /// <summary>The file this enemy came from, for the diagnostics a duplicate enemy type names.</summary>
    internal string Source { get; }

    /// <summary>the work name `<authorId>_<displayName>_<enemyType>` for log lines; null when the document names neither.</summary>
    internal string? Label { get; set; }

    /// <summary>The shared resource payload, validated by the native model reader before this document is admitted.</summary>
    internal JsonElement? Models { get; set; }

    internal IReadOnlyList<EnemyLimbProfile> Limbs { get; set; } = Array.Empty<EnemyLimbProfile>();

    /// <summary>The wake rules, from `forge.enemyWake`; null when the document sets none.</summary>
    internal EnemyWakeRule? Wake { get; set; }
    internal EnemyAppearanceProfile? Appearance { get; set; }

    /// <summary>The surface tint, the material property sets, the glow pulses, the scout animation override and
    /// the marker text; see <see cref="EnemyLookProfile"/>.</summary>
    internal EnemyLookProfile? Look { get; set; }

    /// <summary>The native sound events the author replaced, from `sounds`; empty when none.</summary>
    internal EnemySoundEntry[] Sounds { get; set; } = Array.Empty<EnemySoundEntry>();
    /// <summary>The native actions the author replaced, from `actions`; empty when none.</summary>
    internal EnemyActionEntry[] Actions { get; set; } = Array.Empty<EnemyActionEntry>();

    /// <summary>The enemy-level behind-damage scaling, from `forge.enemyCombat.BackDamageMultiplier`. The hit-time
    /// rule table carries it, and the receiver's own bonus is scaled by it (see `EnemyLimbRules.ScaleBehindBonus`);
    /// the vanilla "allow bonus from behind" switch is a native data block the website exports on its own.</summary>
    internal float? BackDamageMultiplier { get; set; }

    /// <summary>The typed block values, for the native sink.</summary>
    internal EnemyFieldResolution Fields { get; set; } = null!;

    /// <summary>The checked per-ability sections (scout feeler, scream, birthing, snatcher, fog sphere, scanner);
    /// null when the document sets none.</summary>
    internal EnemyAbilityProfile? Abilities { get; set; }
}

/// <summary>One parsed document: the enemy types it profiles. A document owns its own enemy types and nothing
/// else — there is no table another document could merge into and no author grouping left to resolve.</summary>
internal sealed class EnemyProfileDocument
{
    internal EnemyProfileDocument(string source) { Source = source; }

    internal string Source { get; }
    internal List<EnemyProfileEnemy> Enemies { get; } = new();
}

/// <summary>Closed set of rejection codes. A code is part of the contract: the release report and the website
/// both branch on it, so a new kind of refusal gets a new member instead of a reworded detail.</summary>
internal static class EnemyProfileCodes
{
    internal const string Json = "profile-json";
    internal const string Read = "profile-read";
    internal const string Schema = "profile-schema";
    internal const string Budget = "profile-budget";
    internal const string UnknownKey = "profile-unknown-key";
    internal const string UnknownBlock = "profile-unknown-block";
    internal const string Value = "profile-value";
    internal const string EnemyType = "profile-enemy-type";
    internal const string Limb = "profile-limb";
    internal const string Duplicate = "profile-duplicate";
}

/// <summary>One refused document or enemy, with the code the caller branches on and a bounded detail.</summary>
internal readonly struct EnemyProfileRejection
{
    internal EnemyProfileRejection(string code, string source, string detail)
    {
        Code = code; Source = source;
        Detail = detail.Length <= 512 ? detail : detail[..512];
    }

    internal string Code { get; }
    internal string Source { get; }
    internal string Detail { get; }

    public override string ToString() => Code + " in " + Source + ": " + Detail;
}

/// <summary>The parse result: either a document or the first refusal. A document with any refused entry yields
/// no document at all, because a partially applied profile is a different profile than the author wrote.</summary>
internal readonly struct EnemyProfileParse
{
    private EnemyProfileParse(EnemyProfileDocument? document, EnemyProfileRejection rejection)
    {
        Document = document; Rejection = rejection;
    }

    internal EnemyProfileDocument? Document { get; }
    internal EnemyProfileRejection Rejection { get; }
    internal bool Ok => Document != null;

    internal static EnemyProfileParse Parsed(EnemyProfileDocument document) => new(document, default);
    internal static EnemyProfileParse Refused(string code, string source, string detail)
        => new(null, new EnemyProfileRejection(code, source, detail));
}

/// <summary>What one enemy type's profile resolved to. `Limbs`, `Wake` and `Appearance` are the sections the
/// native appliers key on, derived from the blocks; `Fields` is the whole typed block set for the sink. A type
/// two documents both claim resolves to nothing at all.</summary>
internal sealed class EnemyProfileResolution
{
    /// <summary>The file that owns this type's profile, for the diagnostics an applier writes; null when none does.</summary>
    internal string? Source { get; set; }
    /// <summary>The log label from <see cref="EnemyProfileEnemy.Label"/>; null when the type has none.</summary>
    internal string? Label { get; set; }
    internal IReadOnlyList<EnemyLimbProfile> Limbs { get; set; } = Array.Empty<EnemyLimbProfile>();
    internal EnemyWakeRule? Wake { get; set; }
    internal EnemyAppearanceProfile? Appearance { get; set; }
    internal EnemyLookProfile? Look { get; set; }
    internal EnemyFieldResolution? Fields { get; set; }
    internal EnemySoundEntry[] Sounds { get; set; } = Array.Empty<EnemySoundEntry>();
    internal EnemyActionEntry[] Actions { get; set; } = Array.Empty<EnemyActionEntry>();

    /// <summary>The checked per-ability sections; null when the type authors none.</summary>
    internal EnemyAbilityProfile? Abilities { get; set; }

    /// <summary>The enemy-level behind-damage scaling the hit-time rule reads, from
    /// `forge.enemyCombat.BackDamageMultiplier`. The native "allow bonus from behind" switch is a data block the
    /// website exports on its own; this is the multiplier above it.</summary>
    internal float? BackDamageMultiplier { get; set; }

    internal bool IsEmpty => Limbs.Count == 0 && Wake == null && Appearance == null && Look == null && BackDamageMultiplier == null
        && Abilities == null && Sounds.Length == 0 && Actions.Length == 0 && (Fields == null || Fields.IsEmpty);
}

/// <summary>The loaded profile table: every accepted document's enemy types, keyed by the native type id. There
/// is nothing to resolve between documents, so the table is a lookup, and the one question it can answer wrongly
/// is a type two files both claim. That one refuses the type whole, naming both files.</summary>
internal sealed class EnemyProfileCatalog
{
    private readonly Dictionary<uint, EnemyProfileEnemy> _enemies = new();
    private readonly Dictionary<uint, EnemyProfileResolution> _resolved = new();
    private readonly List<EnemyProfileRejection> _rejections = new();
    /// <summary>The types a duplicate claim dropped, so a refused enemy stays refused however many later files
    /// describe it.</summary>
    private readonly HashSet<uint> _refused = new();

    internal IReadOnlyList<EnemyProfileRejection> Rejections => _rejections;
    internal int EnemyCount => _enemies.Count;
    internal IEnumerable<EnemyProfileEnemy> Enemies => _enemies.Values;

    /// <summary>Startup preparation: a rejected content profile must not remove an already installed profile.</summary>
    internal EnemyProfileCatalog Copy()
    {
        var result = new EnemyProfileCatalog();
        foreach (var row in _enemies) result._enemies.Add(row.Key, row.Value);
        result._refused.UnionWith(_refused); result._rejections.AddRange(_rejections);
        return result;
    }

    /// <summary>Preparation-time summaries over accepted types only. Conflicting or refused profiles never
    /// keep a marker hook open; the module caches these answers when publishing a new store.</summary>
    internal bool AnyMarker
    {
        get
        {
            foreach (var enemy in _enemies.Values) if (enemy.Look?.Marker != null) return true;
            return false;
        }
    }

    /// <summary>Whether any accepted type pulses its glow or blinks its marker. The per-tick look walk is skipped
    /// entirely when none does, so an installation without either pays one boolean read per tick.</summary>
    internal bool AnyTickedLook
    {
        get
        {
            foreach (var enemy in _enemies.Values) if (enemy.Look is { IsTicked: true }) return true;
            return false;
        }
    }

    /// <summary>Whether any accepted type authors its propagated wake-up colour; the wake-up hook returns at once
    /// when none does.</summary>
    internal bool AnyPropagateWakeupColor
    {
        get
        {
            foreach (var enemy in _enemies.Values) if (enemy.Look?.PropagateWakeupColor != null) return true;
            return false;
        }
    }

    /// <summary>Whether any accepted type authors its on-screen cap share; the mode-change hook returns at once
    /// when none does.</summary>
    internal bool AnyCapCost
    {
        get
        {
            foreach (var enemy in _enemies.Values) if (enemy.Fields?.CostTowardsCap != null) return true;
            return false;
        }
    }

    /// <summary>Whether any accepted type changes what it hears while asleep; the noise-window hook returns at once
    /// when none does.</summary>
    internal bool AnyWakeHearing
    {
        get
        {
            foreach (var enemy in _enemies.Values) if (enemy.Wake is { ChangesHearing: true }) return true;
            return false;
        }
    }

    /// <summary>Whether any accepted type stays asleep when hit; the two damage hooks return at once when none does.</summary>
    internal bool AnyWakeDamage
    {
        get
        {
            foreach (var enemy in _enemies.Values) if (enemy.Wake is { KeepsAsleepOnDamage: true }) return true;
            return false;
        }
    }

    /// <summary>Which per-ability sections any accepted type authors, so each ability hook returns at once when no
    /// loaded type has its section.</summary>
    internal EnemyAbilityHooks AbilityHooks
    {
        get
        {
            var hooks = EnemyAbilityHooks.None;
            foreach (var enemy in _enemies.Values)
            {
                if (enemy.Abilities is not { } abilities) continue;
                if (abilities.Feeler is { Layout: true }) hooks |= EnemyAbilityHooks.FeelerLayout;
                if (abilities.Feeler is { Tendril: true }) hooks |= EnemyAbilityHooks.FeelerTendril;
                if (abilities.Scream is { Colors: true }) hooks |= EnemyAbilityHooks.ScreamColors;
                if (abilities.Scream is { ClearsImmortal: true }) hooks |= EnemyAbilityHooks.ScreamMortal;
                if (abilities.FogSphere != null) hooks |= EnemyAbilityHooks.FogSphere;
                if (abilities.Scanner != null) hooks |= EnemyAbilityHooks.Scanner;
                if (abilities.Spawning.Length != 0) hooks |= EnemyAbilityHooks.ChildSpawning;
            }
            return hooks;
        }
    }

    /// <summary>Adds one accepted document. The only refusal here is a type another document already declared:
    /// that enemy is dropped from the table — both claims, not the later one — and the diagnostic names the two
    /// files, which is the whole answer to "why did my edit do nothing".</summary>
    internal bool Add(EnemyProfileDocument document)
    {
        bool accepted = true;
        foreach (var enemy in document.Enemies)
        {
            if (_refused.Contains(enemy.EnemyTypeId)) { accepted = false; continue; }
            if (_enemies.TryGetValue(enemy.EnemyTypeId, out var owner))
            {
                _rejections.Add(new EnemyProfileRejection(EnemyProfileCodes.Duplicate, document.Source,
                    "Enemy type " + enemy.EnemyTypeId.ToString(CultureInfo.InvariantCulture)
                    + " is declared by `" + owner.Source + "` and by `" + document.Source
                    + "`; neither profile is applied."));
                _enemies.Remove(enemy.EnemyTypeId);
                _refused.Add(enemy.EnemyTypeId);
                accepted = false;
                continue;
            }
            _enemies.Add(enemy.EnemyTypeId, enemy);
        }
        _resolved.Clear();
        return accepted;
    }

    internal void Reject(EnemyProfileRejection rejection) => _rejections.Add(rejection);

    /// <summary>The one enemy type's profile, or an empty resolution for a type no document describes and for one
    /// that was refused.</summary>
    internal EnemyProfileResolution Resolve(uint enemyTypeId)
    {
        if (_resolved.TryGetValue(enemyTypeId, out var cached)) return cached;
        var resolution = new EnemyProfileResolution();
        if (_enemies.TryGetValue(enemyTypeId, out var enemy))
        {
            resolution.Source = enemy.Source;
            resolution.Label = enemy.Label;
            resolution.Limbs = enemy.Limbs;
            resolution.Wake = enemy.Wake;
            resolution.Appearance = enemy.Appearance;
            resolution.Look = enemy.Look;
            resolution.BackDamageMultiplier = enemy.BackDamageMultiplier;
            resolution.Fields = enemy.Fields;
            resolution.Sounds = enemy.Sounds;
            resolution.Actions = enemy.Actions;
            resolution.Abilities = enemy.Abilities;
        }
        _resolved.Add(enemyTypeId, resolution);
        return resolution;
    }
}

/// <summary>
/// The enemy profile store lives in the native half (`Native/Content/EnemyModuleProfiles.cs`) because the filesystem
/// convention it implements is the package's own; the reader and the models it fills are here, game-independent.
/// </summary>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Enemies;
using ForgeEnemy.Profile;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using GameData;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>
/// The enemy profile store: the accepted documents plus the filesystem convention that finds them. It follows
/// the package convention the runtime already uses for plans — a package owns
/// <c>BepInEx/plugins/&lt;package&gt;/forge/&lt;kind&gt;/</c> — with the enemy kind at
/// <c>forge/enemies/*.forge</c>. Nothing here reads a plan, a command or the network: a profile is data, so the
/// only thing this type decides is which documents are well-formed and which enemy type each entry reaches.
///
/// Determinism. Directories and files are enumerated in ordinal order and merged in that order, so the same
/// installation resolves to the same values on every peer and on every run. Order itself never picks a winner:
/// two entries claiming the same field for the same enemy are a refusal, not a precedence.
///
/// The store never throws. A package with an unreadable directory, a file that is not JSON, or a document
/// written for another schema is refused with a code and the rest of the installation still loads, because one
/// author's typo must not remove another author's attributes. A refusal is reported once through the module's
/// own diagnostics and is never retried during the session.
/// </summary>
internal sealed class EnemyProfileStore
{
    /// <summary>A document over its cap is a budget refusal, bad JSON is a JSON refusal, and every other failure to
    /// reach or read the file is a read refusal.</summary>
    private static readonly PublishedTextCodes Codes = new(EnemyProfileCodes.Read, EnemyProfileCodes.Read,
        EnemyProfileCodes.Budget, EnemyProfileCodes.Json, EnemyProfileCodes.Read);

    private static readonly IReadOnlyDictionary<string, ImportedModelPackage> NoImported = new Dictionary<string, ImportedModelPackage>();
    private readonly EnemyProfileCatalog _catalog;
    private readonly IReadOnlyDictionary<uint, (NativeModelContent Content, IReadOnlyDictionary<string, ImportedModelPackage> Imported)> _models;

    private EnemyProfileStore(EnemyProfileCatalog catalog, int documents,
        IReadOnlyDictionary<uint, (NativeModelContent Content, IReadOnlyDictionary<string, ImportedModelPackage> Imported)>? models = null)
    {
        _catalog = catalog;
        DocumentCount = documents;
        _models = models ?? new Dictionary<uint, (NativeModelContent Content, IReadOnlyDictionary<string, ImportedModelPackage> Imported)>();
    }

    internal int DocumentCount { get; }
    internal int EnemyCount => _catalog.EnemyCount;
    internal IEnumerable<EnemyProfileEnemy> Enemies => _catalog.Enemies;
    /// <summary>Each surviving type's model assemblies, with the imported models its package ships (by reference key).</summary>
    internal IEnumerable<(uint EnemyType, NativeModelContent Content, IReadOnlyDictionary<string, ImportedModelPackage> Imported)> Models => Enemies
        .Where(enemy => _models.ContainsKey(enemy.EnemyTypeId))
        .Select(enemy => (enemy.EnemyTypeId, _models[enemy.EnemyTypeId].Content, _models[enemy.EnemyTypeId].Imported));
    /// <summary>Whether any loaded profile declares a glow pulse or a marker blink: the module walks its lives on
    /// each kernel tick only when one does.</summary>
    internal bool AnyTickedLook => _catalog.AnyTickedLook;
    internal bool AnyMarker => _catalog.AnyMarker;
    /// <summary>Whether any loaded profile authors its propagated wake-up colour.</summary>
    internal bool AnyPropagateWakeupColor => _catalog.AnyPropagateWakeupColor;
    /// <summary>Whether any loaded profile authors its on-screen cap share.</summary>
    internal bool AnyCapCost => _catalog.AnyCapCost;
    /// <summary>Whether any loaded profile changes what a sleeping enemy hears.</summary>
    internal bool AnyWakeHearing => _catalog.AnyWakeHearing;
    /// <summary>Whether any loaded profile keeps a sleeping enemy asleep when hit.</summary>
    internal bool AnyWakeDamage => _catalog.AnyWakeDamage;
    /// <summary>Which per-ability hooks some loaded profile needs.</summary>
    internal EnemyAbilityHooks AbilityHooks => _catalog.AbilityHooks;
    internal IReadOnlyList<EnemyProfileRejection> Rejections => _catalog.Rejections;

    /// <summary>Reads the <c>forge/enemies/*.forge</c> documents ForgeEngine's one content discovery hands this package
    /// (I-RESOLVE). The root is a parameter rather than <c>Paths</c> so the tests can point the same entry at a fixture
    /// directory.</summary>
    internal static EnemyProfileStore Read(string bepInExRoot)
    {
        var catalog = new EnemyProfileCatalog();
        if (string.IsNullOrEmpty(bepInExRoot)) return new EnemyProfileStore(catalog, 0);
        void Refused(PublishedResourceFailure failure)
            => catalog.Reject(new EnemyProfileRejection(failure.Code, failure.Relative, failure.Reason));
        var discovered = ContentPackages.Read(bepInExRoot, ContentPackages.EngineReader, "forge/enemies", "*.forge", Codes,
            (Action<PublishedResourceFailure>)Refused);
        int documents = 0;
        var models = new Dictionary<uint, (NativeModelContent Content, string Document)>();
        foreach (var document in discovered)
        {
            if (documents >= EnemyProfileSchema.MaximumDocuments)
            {
                catalog.Reject(new EnemyProfileRejection(EnemyProfileCodes.Budget, document.Relative,
                    "The installation describes more than " + EnemyProfileSchema.MaximumDocuments + " profile documents."));
                continue;
            }
            if (!PublishedResources.TryRead(document, EnemyProfileSchema.MaximumDocumentBytes, Codes,
                Refused, out var text, out _)) continue;
            var parse = EnemyBlockReader.Read(document.Relative, text!);
            if (!parse.Ok) { catalog.Reject(parse.Rejection); continue; }
            var pending = new Dictionary<uint, NativeModelContent>();
            try
            {
                foreach (var enemy in parse.Document!.Enemies)
                    if (enemy.Models is { } value) pending.Add(enemy.EnemyTypeId, NativeModelContent.Read(value, "enemy"));
            }
            catch (Exception error)
            {
                catalog.Reject(new EnemyProfileRejection(EnemyProfileCodes.Schema, document.Relative, error.Message));
                continue;
            }
            // `Add` refuses the enemies a later document duplicates and names both files in its own
            // diagnostic; a document that added at least one enemy is a document this installation holds.
            if (catalog.Add(parse.Document!)) documents++;
            // A replaced sound event names its sound by revision; the file is found in this document's package or its
            // dependencies (I-RESOLVE) and kept for the player, so a missing file is an Error now, not a silent game sound.
            foreach (var enemy in parse.Document!.Enemies)
                foreach (var sound in enemy.Sounds)
                    if (sound.Resource != null)
                        ForgeEngine.Presentation.CustomSoundLibrary.Require(bepInExRoot, EnemyHost.ModId, PublishedResources.PackageRoot(document.Relative),
                            sound.Resource, document.Relative, Refused);
            // A conflicting type is removed by the catalog, but the document's other types still survive.
            // Models enumerates only those surviving types, including models from a partially accepted file.
            foreach (var model in pending) models.TryAdd(model.Key, (model.Value, document.Relative));
        }
        // Imported model files are found by hash in the profile's package or its dependencies (I-RESOLVE).
        return new EnemyProfileStore(catalog, documents, models.ToDictionary(row => row.Key,
            row => (row.Value.Content, ImportedModelFiles.ForEntry(bepInExRoot, row.Value.Document, row.Value.Content, Refused) ?? NoImported)));
    }

    /// <summary>An empty store, for a session that never found a document. It resolves every enemy to nothing,
    /// which is what "no profile was written" means.</summary>
    internal static EnemyProfileStore Empty() => new(new EnemyProfileCatalog(), 0);

    internal EnemyProfileStore With(EnemyProfileDocument document)
    {
        var candidate = _catalog.Copy();
        if (!candidate.Add(document)) throw new InvalidOperationException("Content enemy profile conflicts with an installed profile.");
        return new EnemyProfileStore(candidate, DocumentCount + 1, _models);
    }

    internal EnemyProfileResolution Resolve(uint enemyTypeId) => _catalog.Resolve(enemyTypeId);

}

/// <summary>What one profile's limb section came to: the parts this enemy has no limb for, and the parts whose
/// `breakable: true` asked for a destruction the limb does not have. Both are refusals of one part while the rest
/// of the section is written, so they are reported and not thrown.</summary>
internal readonly struct EnemyLimbApply
{
    internal EnemyLimbApply(List<EnemyLimbPart> missing, List<EnemyLimbPart> notDestructible, List<string> notKept)
    { Missing = missing; NotDestructible = notDestructible; NotKept = notKept; }

    internal List<EnemyLimbPart> Missing { get; }
    internal List<EnemyLimbPart> NotDestructible { get; }
    /// <summary>One sentence per written value the limb did not keep when read straight back: part, limb id, member,
    /// what was written and what the limb holds.</summary>
    internal List<string> NotKept { get; }
}

/// <summary>
/// The one native write path a profile submits through, kept in its own type for the same reason
/// <see cref="EnemyNativeWrite"/> is: the game assembly is reached from exactly one audited member. Every member
/// written here was read back from the frozen interop of build 20403457 as a public writable field or a public
/// native method; nothing is reached by reflection and no private member is touched.
///
/// The writes are additive. A profile that names no limb leaves every limb exactly as the prefab authored it,
/// which is why the entry point takes the resolved profile instead of a full limb table: there is no default to
/// invent, and no "unspecified" value that could be mistaken for zero.
/// </summary>
internal static class EnemyNativeProfile
{
    /// <summary>
    /// Applies the limb overrides to every limb the type's resolved parts name, and answers the parts that could
    /// not be written. A part the type has no limb for is not an error the native side can fix — the enemy type
    /// simply has no such part — so it is reported instead of skipped silently, and the rest of the section still
    /// lands.
    ///
    /// Health keeps the ratio the native setup already chose. The receiver's own <c>Setup</c> decides both the
    /// maximum and the current health (a checkpoint-restored or difficulty-scaled enemy starts below its
    /// maximum), so writing the maximum alone would leave the enemy standing at the old absolute health and
    /// silently change how hurt it is. The current health is therefore rescaled by the ratio it had, which makes
    /// a full-health enemy stay full and an already-damaged one keep its wound.
    /// </summary>
    internal static EnemyLimbApply ApplyLimbs(Dam_EnemyDamageBase damage, IReadOnlyList<EnemyLimbProfile> profile,
        EnemyLimbResolution resolution)
    {
        var missing = new List<EnemyLimbPart>();
        var notDestructible = new List<EnemyLimbPart>();
        var notKept = new List<string>();
        var limbs = damage.DamageLimbs;
        if (limbs == null) return new EnemyLimbApply(missing, notDestructible, notKept);
        foreach (var entry in profile)
        {
            // A removed part takes no hit (EnemyHiddenLimbs); nothing else is written to it.
            if (entry.Removed) continue;
            if (!resolution.TryLimbIds(entry.Part, out var ids) || ids.Count == 0)
            {
                missing.Add(entry.Part);
                continue;
            }
            var targets = new List<Dam_EnemyDamageLimb>();
            foreach (int id in ids)
            {
                var limb = Find(limbs, id);
                if (limb != null) targets.Add(limb);
            }
            if (targets.Count == 0)
            {
                missing.Add(entry.Part);
                continue;
            }
            // `breakable: true` is a request for a destruction the limb must already have: a limb the game cannot
            // break would silently ignore the author's `false` later, so the whole entry is refused here instead
            // of writing half of it.
            if (entry.Breakable == true && !AllDestructible(targets))
            {
                notDestructible.Add(entry.Part);
                continue;
            }
            foreach (var limb in targets)
            {
                Write(limb, entry);
                ReadBack(limb, entry, notKept);
            }
        }
        return new EnemyLimbApply(missing, notDestructible, notKept);
    }

    /// <summary>Reads the limb straight back after the write: a type or multiplier the limb does not hold is reported with
    /// the part, the limb id and both values instead of leaving the author's armor or weakspot silently absent.</summary>
    private static void ReadBack(Dam_EnemyDamageLimb limb, EnemyLimbProfile entry, List<string> notKept)
    {
        string where = entry.Part.Name + "#" + limb.m_limbID.ToString(CultureInfo.InvariantCulture);
        if (entry.Kind is EnemyLimbKind kind && (int)limb.m_type != (int)kind)
            notKept.Add(where + " type wrote " + kind + " holds " + limb.m_type);
        if (entry.WeakspotMultiplier is float weakspot && limb.m_weakspotDamageMulti != weakspot)
            notKept.Add(where + " weakspot multiplier wrote " + weakspot.ToString("R", CultureInfo.InvariantCulture)
                + " holds " + limb.m_weakspotDamageMulti.ToString("R", CultureInfo.InvariantCulture));
        if (entry.ArmorMultiplier is float armor && limb.m_armorDamageMulti != armor)
            notKept.Add(where + " armor multiplier wrote " + armor.ToString("R", CultureInfo.InvariantCulture)
                + " holds " + limb.m_armorDamageMulti.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>Every limb the live receiver has, as `id:name:type`, for the report of a part that matched none.</summary>
    internal static string DescribeLimbs(Dam_EnemyDamageBase damage)
    {
        var limbs = damage.DamageLimbs;
        if (limbs == null) return "none";
        var parts = new List<string>();
        foreach (var limb in limbs)
            if (limb != null)
                parts.Add(limb.m_limbID.ToString(CultureInfo.InvariantCulture) + ":" + limb.gameObject?.name + ":" + limb.m_type);
        return string.Join(",", parts);
    }

    private static Dam_EnemyDamageLimb? Find(Dam_EnemyDamageLimb[] limbs, int limbId)
    {
        for (int index = 0; index < limbs.Length; index++)
        {
            var candidate = limbs[index];
            if (candidate == null || candidate.m_limbID != limbId) continue;
            return candidate;
        }
        return null;
    }

    private static bool AllDestructible(List<Dam_EnemyDamageLimb> limbs)
    {
        foreach (var limb in limbs) if (!limb.HasDestruction()) return false;
        return true;
    }

    private static void Write(Dam_EnemyDamageLimb limb, EnemyLimbProfile entry)
    {
        if (entry.Health is float health) SetHealth(limb, health);
        // The native setter, not the field: the type is what `TestDamageModifiers` and the destruction rules
        // branch on, and only the receiver's own method is allowed to be the answer for "this limb is now a
        // weakspot". It also rewrites both multipliers (build 20403457, RVA 0x1384DB0: normal = 1/1, weakspot =
        // datablock weakspot/1, armor = 1/datablock armor), so it runs first and the authored multipliers
        // are written after it; the reverse order loses them.
        if (entry.Kind is EnemyLimbKind kind) limb.SetLimbDamageType((eLimbDamageType)(int)kind);
        if (entry.WeakspotMultiplier is float weakspot) limb.m_weakspotDamageMulti = weakspot;
        if (entry.ArmorMultiplier is float armor) limb.m_armorDamageMulti = armor;
    }

    private static void SetHealth(Dam_EnemyDamageLimb limb, float maximum)
    {
        float previousMax = limb.m_healthMax;
        float previousHealth = limb.m_health;
        limb.m_healthMax = maximum;
        limb.m_health = previousMax > 0f
            ? maximum * Math.Clamp(previousHealth / previousMax, 0f, 1f)
            : maximum;
    }

    /// <summary>
    /// Applies the glow through the receiver's own <c>InterpolateGlow(colour, transitionTime)</c> rather than a
    /// direct write of <c>m_glowColor</c>: the glow is driven by its own animator, so the native entry point that
    /// the game itself uses to change it is the only write whose result the animator will keep. A profile that
    /// names no transition asks for the colour immediately.
    ///
    /// The entry has no null guard (build 20403457, RVA 0x163F0E0): it hands the owner's <c>MaterialHandler.m_supportsInnerGlow</c>
    /// material holders to the animator. At this spawn point the handler's per-instance groups are not built yet — the game builds
    /// them after setup (<c>EnemyAllocator.OnEnemySpawned</c> ends with <c>CB_ParsableCollection.InstantiationInitialize</c>) — so
    /// the handler's own <c>InstantiationInitialize</c> builds them first. It runs once per instance (<c>m_isMaterialRefsUnique</c>),
    /// so the game's later call returns at once, exactly as for an assembled appearance (<see cref="EnemyModule"/> rebuilds the groups
    /// the same way). A model with no handler or no glow group cannot glow: that is answered as <c>glow-surface-missing</c>.
    /// </summary>
    /// <returns>Null when the glow was handed to the game, else the reason it could not be.</returns>
    internal static string? ApplyAppearance(EnemyAgent enemy, EnemyAppearance appearance, EnemyAppearanceProfile profile)
    {
        if (profile.GlowColor is not { } color) return null;
        if (enemy.MaterialHandler is not { } handler) return "glow-surface-missing";
        if (handler.m_supportsInnerGlow == null) handler.InstantiationInitialize();
        if (handler.m_supportsInnerGlow == null) return "glow-surface-missing";
        var rgba = new Color(color[0], color[1], color[2], color.Length > 3 ? color[3] : 1f);
        appearance.InterpolateGlow(rgba, profile.GlowTransition ?? 0f);
        return null;
    }
}

/// <summary>
/// The prefab reader: it turns one built enemy prefab into the plain candidates the pure resolver works on. Every
/// read here is a read — nothing writes the prefab — so a type whose limbs cannot be resolved keeps the vanilla
/// setup and the report names the limb.
///
/// The two readings are the ones the framework plan states. A sac limb (`Dam_EnemyDamageLimb_Custom`) is numbered
/// by the sac renderers (EggSack or FleshSack, <see cref="EnemyPlacementOrder.IsTumorRenderer"/>) under the main model, in the
/// website's own natural name order with the `Weakspot_N` anchor breaking a name tie, and stands against the renderer it is
/// nearest to. Every other limb is the nearest ancestor — itself included — that one of the model's own skinned renderers
/// bones, and its bone name names the part.
/// </summary>
internal static class EnemyLimbPrefab
{
    internal static List<EnemyLimbCandidate> Candidates(Dam_EnemyDamageBase damage, Transform model)
    {
        var candidates = new List<EnemyLimbCandidate>();
        var limbs = damage.DamageLimbs;
        if (limbs == null) return candidates;
        var bones = BoneIdentities(model);
        var renderers = TumorRenderers(model);
        for (int index = 0; index < limbs.Length; index++)
        {
            var limb = limbs[index];
            if (limb == null) continue;
            if (limb.TryCast<Dam_EnemyDamageLimb_Custom>() != null)
            {
                var distances = new float[renderers.Count];
                var origin = limb.transform.position;
                for (int slot = 0; slot < renderers.Count; slot++) distances[slot] = Distance(origin, renderers[slot]);
                candidates.Add(new EnemyLimbCandidate(limb.m_limbID, limb.gameObject.name)
                { IsTumor = true, TumorDistances = distances });
                continue;
            }
            candidates.Add(new EnemyLimbCandidate(limb.m_limbID, limb.gameObject.name)
            { BoneName = NearestBone(limb.transform, bones) });
        }
        return candidates;
    }

    /// <summary>A CB_CustomPrefab attaches rigid sac clones to Weakspot_1..Weakspot_N, in that order (EggSack on the Tank and
    /// the Mothers, FleshSack on the Nightmare enemies). The preview calls these EggSack, EggSack.001, etc. Their shared clone
    /// name is not an identity: use the
    /// native attachment anchor to order them, never a process-local instance id. Missing or duplicate anchors
    /// refuse the set. Skinned sacs retain their distinct renderer names and root-bone positions.</summary>
    private static List<Vector3> TumorRenderers(Transform model)
    {
        var positions = new List<Vector3>();
        foreach (var renderer in OrderedTumorRenderers(model))
        {
            var skin = renderer.TryCast<SkinnedMeshRenderer>();
            positions.Add((skin != null && skin.rootBone != null ? skin.rootBone : renderer.transform).position);
        }
        return positions;
    }

    /// <summary>The model's EggSack renderers in tumor order (tumor N is element N-1), or an empty list when two
    /// renderers cannot be told apart. The order is the pairing's own, so a removal hides exactly the renderer the
    /// tumor limb was numbered against.</summary>
    internal static List<Renderer> OrderedTumorRenderers(Transform model)
    {
        var named = new List<(string Name, string Anchor, Renderer Renderer)>();
        foreach (var renderer in model.gameObject.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null) continue;
            if (!EnemyPlacementOrder.IsTumorRenderer(renderer.name)) continue;
            named.Add((renderer.name, TumorAnchor(renderer.transform, model), renderer));
        }
        named.Sort((left, right) =>
        {
            int byName = EnemyLimbParts.NaturalCompare(left.Name, right.Name);
            return byName != 0 ? byName : EnemyLimbParts.NaturalCompare(left.Anchor, right.Anchor);
        });
        for (int index = 1; index < named.Count; index++)
        {
            var previous = named[index - 1];
            var current = named[index];
            if (EnemyLimbParts.NaturalCompare(previous.Name, current.Name) == 0 && (previous.Anchor.Length == 0 || current.Anchor.Length == 0
                || EnemyLimbParts.NaturalCompare(previous.Anchor, current.Anchor) == 0)) return new List<Renderer>();
        }
        var renderers = new List<Renderer>(named.Count);
        foreach (var entry in named) renderers.Add(entry.Renderer);
        return renderers;
    }

    /// <summary>The tumor number (1-based) of <paramref name="renderer"/> in an <see cref="OrderedTumorRenderers"/> list, or 0.
    /// Matched by instance id: two interop wrappers of one renderer are different managed objects, so `IndexOf` never finds it.</summary>
    internal static int TumorNumber(List<Renderer> tumors, Renderer renderer)
    {
        int id = renderer.GetInstanceID();
        for (int index = 0; index < tumors.Count; index++)
            if (tumors[index].GetInstanceID() == id) return index + 1;
        return 0;
    }

    private static string TumorAnchor(Transform renderer, Transform model)
    {
        for (var cursor = renderer.parent; cursor != null && cursor != model; cursor = cursor.parent)
        {
            const string prefix = "Weakspot_";
            string name = cursor.name;
            if (name.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                && number > 0 && name == prefix + number.ToString(CultureInfo.InvariantCulture)) return name;
        }
        return string.Empty;
    }

    /// <summary>The instance identities of every bone the model's own skinned renderers bind.</summary>
    internal static HashSet<int> BoneIdentities(Transform model)
    {
        var bones = new HashSet<int>();
        foreach (var renderer in model.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null) continue;
            var list = renderer.bones;
            if (list == null) continue;
            for (int index = 0; index < list.Length; index++)
                if (list[index] != null) bones.Add(list[index].GetInstanceID());
        }
        return bones;
    }

    /// <summary>The nearest ancestor — the limb itself included — that the model's skinned renderers bind, or null
    /// when the limb hangs off the skeleton entirely.</summary>
    private static string? NearestBone(Transform limb, HashSet<int> bones)
    {
        for (var cursor = limb; cursor != null; cursor = cursor.parent)
            if (bones.Contains(cursor.GetInstanceID())) return cursor.name;
        return null;
    }

    private static float Distance(Vector3 left, Vector3 right)
    {
        float x = left.x - right.x, y = left.y - right.y, z = left.z - right.z;
        return MathF.Sqrt(x * x + y * y + z * z);
    }
}

/// <summary>
/// The module's profile half: it owns the store, resolves one enemy at the point the enemy becomes a known
/// native life, and reports what it refused. The application point is <see cref="EnemyModule.TrackSpawn"/>
/// because that is the first moment after the receiver finished setting itself up — the limb array, the
/// detection component and the appearance component are all built by then — and it happens once per native life
/// on every peer, from identical data, which is why no replication channel is involved.
///
/// Diagnostics are one per enemy type for the whole session, not one per spawn: a profile is static, so a
/// conflict or a limb the type does not declare produces the same sentence every time, and a level with fifty
/// strikers must not produce fifty identical lines.
/// </summary>
internal sealed partial class EnemyModule
{
    private const int MaximumProfileDiagnostics = 256;

    internal EnemyProfileStore? _profiles;
    internal readonly Dictionary<uint, EnemyLimbResolution> _limbParts = new();
    private readonly HashSet<uint> _fieldUnappliedReported = new();
    private readonly HashSet<uint> _appliedTypes = new();

    /// <summary>The log value naming an enemy type as the work name `<authorId>_<displayName>_<enemyType>`: the profile's label, else the
    /// enemy block's readable game name (authored blocks carry that same form, vanilla ones their own name); the bare number when
    /// neither exists. Log points are cold paths, so the lookup (cached per type) is paid only when a line is written.</summary>
    internal RuntimeLogValue EnemyLabel(uint typeId)
        => RuntimeLogValue.Of("enemy", _profiles?.Resolve(typeId).Label ?? GameData.EnemyDataBlock.GetBlock(typeId)?.name
            ?? typeId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal bool MarkerConfigured { get; private set; }

    /// <summary>Hands the session's store to the module. Called once, from Load, before any level exists; every content
    /// generation's entries start again from this store and its models.</summary>
    internal void LoadProfiles(EnemyProfileStore profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var childWaves = PrepareChildWaveProfiles(profiles);
        var models = Models;
        var registrations = new List<NativeModelRuntime.NativeModelRegistration>();
        foreach (var row in profiles.Models)
        {
            string owner = "enemy:" + row.EnemyType.ToString(CultureInfo.InvariantCulture);
            registrations.Add(EnemyModelAssemblies.Prepare(owner, row.Content, row.Imported));
            models = models.With(row.EnemyType, owner, row.Content);
        }
        try { foreach (var registration in registrations) registration.Commit(); }
        catch
        {
            for (int index = registrations.Count - 1; index >= 0; index--) registrations[index].Rollback();
            throw;
        }
        UseProfiles(profiles, childWaves);
        _models = models;
        _baseProfiles = profiles; _baseModels = models;
        ReportProfiles(profiles);
    }

    /// <summary>The withdrawn functions the loaded profiles author content for: the document is read, but no hook applies
    /// it, so the startup reports each one instead of letting an author wonder why nothing happens.</summary>
    internal IReadOnlyList<EnemyFeature> AuthoredButUnavailable(EnemyHookAvailability hooks)
    {
        var used = new List<EnemyFeature>();
        if (CapCostAuthored) used.Add(EnemyFeature.CapCost);
        if (WakeHearingAuthored) used.Add(EnemyFeature.WakeHearing);
        if (WakeDamageAuthored) used.Add(EnemyFeature.Wake);
        if (MarkerConfigured || _propagateLooks) used.Add(EnemyFeature.Look);
        foreach (var flag in new[]
        {
            EnemyAbilityHooks.FeelerLayout, EnemyAbilityHooks.FeelerTendril, EnemyAbilityHooks.ScreamColors, EnemyAbilityHooks.ScreamMortal,
            EnemyAbilityHooks.FogSphere, EnemyAbilityHooks.Scanner, EnemyAbilityHooks.ChildSpawning
        })
            if ((AbilityHooks & flag) != 0) used.Add(EnemyHookFeatures.Of(flag));
        return used.Distinct().Where(feature => !hooks.Offers(feature)).ToArray();
    }

    // Preparing a content transaction computes the catalog first; publication is only these assignments.
    private void UseProfiles(EnemyProfileStore profiles, Dictionary<uint, ChildWaveProfile> childWaves)
    {
        _profiles = profiles;
        _childWaveProfiles = childWaves;
        _tickedLooks = profiles.AnyTickedLook;
        MarkerConfigured = profiles.AnyMarker;
        _propagateLooks = profiles.AnyPropagateWakeupColor;
        CapCostAuthored = profiles.AnyCapCost;
        WakeHearingAuthored = profiles.AnyWakeHearing;
        WakeDamageAuthored = profiles.AnyWakeDamage;
        AbilityHooks = profiles.AbilityHooks;
        // Action files are read here, never on a spawn; Preload does not throw, and files read before stay cached.
        foreach (var enemy in profiles.Enemies) _actions.Preload(enemy.Actions, enemy.Source);
    }

    private void ReportProfiles(EnemyProfileStore profiles)
    {
        if (Log.Logs(RuntimeLogLevel.Info) && (profiles.DocumentCount != 0 || profiles.EnemyCount != 0 || profiles.Rejections.Count != 0))
            Log.Write(RuntimeLogLevel.Info, EnemyLogCodes.ContentLoaded, new RuntimeLogFacts
            {
                Values = new[]
                {
                    RuntimeLogValue.Of("profileDocuments", (long)profiles.DocumentCount),
                    RuntimeLogValue.Of("profileEnemies", (long)profiles.EnemyCount),
                    RuntimeLogValue.Of("profileRefusals", (long)profiles.Rejections.Count)
                }
            });
        if (profiles.Rejections.Count == 0 || !Log.Logs(RuntimeLogLevel.Error)) return;
        int reported = 0;
        foreach (var rejection in profiles.Rejections)
        {
            if (reported++ >= MaximumProfileDiagnostics)
            {
                Truncated("profile-refusals", profiles.Rejections.Count - MaximumProfileDiagnostics);
                break;
            }
            Log.Write(RuntimeLogLevel.Error, EnemyLogCodes.ProfileRefused, new RuntimeLogFacts
            {
                Values = new[] { RuntimeLogValue.Of("code", rejection.Code), RuntimeLogValue.Quote("source", rejection.Source ?? "-") },
                Detail = rejection.Detail
            });
        }
    }

    private readonly EnemySoundReplacement _sounds = new();
    private readonly EnemyActionReplacement _actions = new();

    /// <summary>One refused sound slot of one enemy type, once per session: the slot keeps the game's sound.</summary>
    private void SoundRefusal(uint typeId, string? source, ForgeEnemy.Profile.EnemySoundSlot slot, string sound, string reason)
    {
        if (!TypeRecordDue(RuntimeLogLevel.Error, EnemyLogCodes.SoundRefused, typeId, reason, slot.ToString())) return;
        WriteTypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.SoundRefused, typeId, source, reason, "this event keeps the game's sound", new[]
        {
            RuntimeLogValue.Quote("sound", sound), RuntimeLogValue.Of("event", slot.ToString())
        });
    }

    /// <summary>
    /// Applies this enemy's profile, if any. Called once per native life from the spawn path
    /// (<see cref="TrackSpawn"/>), which runs after the receiver's own setup on every peer.
    /// </summary>
    private void ApplyProfiles(EnemyAgent enemy)
    {
        var profiles = _profiles;
        var readType = _enemyType;
        if (profiles == null || readType == null) return;
        uint typeId;
        try
        {
            // The type is read from the block the spawn already carries. An unreadable type is said once, never skipped
            // silently: every authored limb, wake and field of the life would be missing without a trace.
            if (readType(enemy) is not { } resolved)
            {
                if (!_typeUnreadableReported && Log.Logs(RuntimeLogLevel.Error))
                {
                    _typeUnreadableReported = true;
                    Log.Write(RuntimeLogLevel.Error, EnemyLogCodes.ProfileUnapplied, new RuntimeLogFacts
                    {
                        Values = new[] { RuntimeLogValue.Of("reason", "enemy-type-unreadable"),
                            RuntimeLogValue.Of("pointer", enemy.Pointer.ToInt64()) },
                        Detail = "the spawned enemy's data block could not be read, so no profile was applied to it"
                    });
                }
                return;
            }
            typeId = resolved;
        }
        catch (Exception error) { NativeFault.Note("enemy.profile.type-read", error); return; }
        var profile = profiles.Resolve(typeId);
        if (profile.IsEmpty) return;
        var missing = new List<EnemyLimbPart>();
        var notDestructible = new List<EnemyLimbPart>();
        bool limbsUnresolved = false;
        var notKept = new List<string>();
        string liveLimbs = "";
        // Each independent section is applied on its own: a section that throws is named in a ProfileUnapplied record and
        // the sections after it still run. Writes a section made before it threw stay in place; nothing is rolled back.
        bool failed = false;
        void Section(string name, Action apply)
        {
            try { apply(); }
            catch (Exception error)
            {
                EnemyLog.Contained(error);
                failed = true;
                // Once per (section, exception type): one failing section never hides the next section's report.
                if (TypeRecordDue(RuntimeLogLevel.Error, EnemyLogCodes.ProfileUnapplied, typeId, name, error.GetType().Name))
                    WriteTypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.ProfileUnapplied, typeId, profile.Source, null, null,
                        new[] { RuntimeLogValue.Of("section", name), RuntimeLogValue.Of("exception", error.GetType().Name) });
            }
        }
        Section("limbs", () =>
        {
            if (profile.Limbs.Count != 0 && enemy.Damage is not { })
                limbsUnresolved = true;
            if (profile.Limbs.Count != 0 && enemy.Damage is { } damage)
            {
                // The resolution is per type and belongs to the prefab this life was instantiated from, so a type
                // whose prefab never resolved is reported rather than written with nothing.
                if (_limbParts.TryGetValue(typeId, out var resolution))
                {
                    var applied = EnemyNativeProfile.ApplyLimbs(damage, profile.Limbs, resolution);
                    missing = applied.Missing;
                    notDestructible = applied.NotDestructible;
                    notKept = applied.NotKept;
                    if (missing.Count != 0 || notKept.Count != 0) liveLimbs = EnemyNativeProfile.DescribeLimbs(damage);
                }
                else limbsUnresolved = true;
            }
        });
        Section("wake", () => { if (profile.Wake is { } wake) StartWake(enemy, typeId, profile.Source, wake); });
        Section("appearance", () =>
        {
            if (profile.Appearance is { } appearance && enemy.Appearance is { } nativeAppearance
                && EnemyNativeProfile.ApplyAppearance(enemy, nativeAppearance, appearance) is { } refused)
                TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, typeId, profile.Source, refused,
                    "the default glow is not written; the rest of the appearance is");
        });
        Section("fields", () => { if (profile.Fields is { } fields) ApplyBlockFields(enemy, fields); });
        Section("look", () => { if (profile.Look is { } look) StartLook(enemy, typeId, profile.Source, look, profile.Appearance); });
        Section("abilities", () => { if (profile.Abilities is { } abilities) StartAbilityFields(enemy, typeId, profile.Source, abilities); });
        _sounds.Played ??= (type, sound, refusal) =>
        {
            if (Log.Logs(RuntimeLogLevel.Info)) WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.SoundPlayed, type, null, refusal, null, new[] { RuntimeLogValue.Quote("sound", sound) });
        };
        Section("sounds", () =>
        {
            if (profile.Sounds.Length != 0)
                _sounds.Apply(enemy, typeId, profile.Sounds, (slot, sound, reason) => SoundRefusal(typeId, profile.Source, slot, sound, reason));
        });
        {
            _actions.Shown ??= (type, slot, seconds, window, cut) =>
            {
                if (Log.Logs(RuntimeLogLevel.Info)) WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.ActionShown, type, null, null, null, new[]
                {
                    RuntimeLogValue.Of("slot", slot.ToString()), RuntimeLogValue.Of("seconds", seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)),
                    RuntimeLogValue.Of("window", window.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)), RuntimeLogValue.Of("cut", cut ? "true" : "false")
                });
            };
            _actions.Tracing ??= () => Log.Logs(RuntimeLogLevel.Info);
            _actions.Traced ??= (type, name, hash, row, state, elapsed) =>
            {
                if (Log.Logs(RuntimeLogLevel.Info)) WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.ActionState, type, null, null, null, new[]
                {
                    RuntimeLogValue.Of("event", name), RuntimeLogValue.Of("hash", (long)hash), RuntimeLogValue.Of("row", row), RuntimeLogValue.Of("state", state),
                    RuntimeLogValue.Of("elapsed", elapsed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                });
            };
            Section("actions", () =>
            {
                if (profile.Actions.Length != 0 && _actions.Apply(enemy, profile.Actions, profile.Source ?? "") is { } unplayable
                    && TypeRecordDue(RuntimeLogLevel.Error, EnemyLogCodes.ActionUnplayable, typeId, unplayable.Reason, unplayable.Action))
                    WriteTypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.ActionUnplayable, typeId, profile.Source, unplayable.Reason, "this action stays the game's", new[]
                    {
                        RuntimeLogValue.Quote("action", unplayable.Action), RuntimeLogValue.Of("slot", unplayable.Slot.ToString())
                    });
            });
        }
        if (!failed && _appliedTypes.Count < MaximumProfileDiagnostics && Log.Logs(RuntimeLogLevel.Info) && _appliedTypes.Add(typeId))
            WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.ProfileApplied, typeId, profile.Source, null, null, new[]
            {
                RuntimeLogValue.Of("limbs", (long)profile.Limbs.Count), RuntimeLogValue.Of("wake", profile.Wake != null),
                RuntimeLogValue.Of("appearance", profile.Appearance != null), RuntimeLogValue.Of("fields", profile.Fields != null),
                RuntimeLogValue.Of("look", profile.Look != null)
            });
        if (profile.Fields is { } blockFields) ReportUnapplied(typeId, blockFields);
        if (missing.Count == 0 && notDestructible.Count == 0 && notKept.Count == 0 && !limbsUnresolved) return;
        if (!Log.Logs(RuntimeLogLevel.Error)) return;
        // Each reason is its own once-per-type fact, so a section that already reported never hides a limb reason, and the
        // detail text is only built for the one spawn that reports it.
        void LimbReport(string reason, Func<string> detail)
        {
            if (TypeRecordDue(RuntimeLogLevel.Error, EnemyLogCodes.LimbsFailed, typeId, reason))
                WriteTypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.LimbsFailed, typeId, profile.Source, reason, detail(), null);
        }
        if (limbsUnresolved)
            LimbReport("limb-unresolved", static () => "the type's prefab limbs were not resolved, or the life has no damage receiver");
        if (notKept.Count != 0) LimbReport("limb-write-not-kept", () => string.Join("; ", notKept) + " | live limbs " + liveLimbs);
        if (notDestructible.Count != 0) LimbReport("limb-not-destructible", () => Names(notDestructible));
        if (missing.Count != 0)
            LimbReport("limb-part-missing", () => Names(missing) + " | live limbs " + liveLimbs + " | resolved " + ResolvedParts(typeId));
    }

    /// <summary>
    /// Applies the fields whose native carrier is not one of the three sections above — the block fields
    /// `EnemyFieldResolver` resolved (the animator-controller handle, the state glows, the tag requirement, the
    /// door-break switch). The implementation lives in <c>EnemyFieldsNative.cs</c>, which only the projects that compile
    /// the whole native set include: the game types those writes name are not part of the shim a slice suite
    /// compiles, and a classic partial declaration is what lets those suites compile the reader and the appliers
    /// they own without the block sink. A build without the implementation writes nothing here, which is the
    /// no-op a suite that never authors a block field expects.
    /// </summary>
    partial void ApplyBlockFields(EnemyAgent enemy, EnemyFieldResolution fields);

    /// <summary>
    /// Names every accepted field the document set that this build has no writer for, once per enemy type. The
    /// reader accepts the whole field table so the website can write any implemented block; a path this build
    /// does not write is therefore a report, never a silent half-applied document.
    /// </summary>
    private void ReportUnapplied(uint typeId, EnemyFieldResolution fields)
    {
        if (fields.Unapplied.Count == 0 && fields.Skipped.Count == 0 || !Log.Logs(RuntimeLogLevel.Error) || !_fieldUnappliedReported.Add(typeId)) return;
        foreach (var (block, fieldId) in fields.Unapplied)
            WriteTypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.FieldUnapplied, typeId, fields.Source,
                EnemyFieldResolver.UnappliedReason(block, fieldId), null, new[] { RuntimeLogValue.Quote("field", fieldId) });
        // An entry that wrote nothing is skipped, not a reason to drop the document: named here so the author sees it.
        foreach (var (block, entry) in fields.Skipped)
            WriteTypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.FieldUnapplied, typeId, fields.Source,
                "writes-nothing", null, new[] { RuntimeLogValue.Quote("field", block + ": " + entry) });
    }

    /// <summary>
    /// Resolves one enemy type's limbs from the prefab the game has just built and caches the answer for every
    /// spawn of that type. Called from the prefab builder's postfix, which is the only moment the type's limb array
    /// and model exist together and the only one that happens once per type rather than once per life.
    ///
    /// The hit-time rule table is rebuilt here too, because it is keyed by the native limb id the resolution just
    /// answered: a type with no hit-time values drops its entry, so a reloaded prefab cannot leave a stale rule
    /// standing on a limb. A refusal keeps the vanilla limbs and reports once per type.
    /// </summary>
    /// <summary>The parts of a type that no longer exist: removed (`Limbs.Removed`) or replaced by a placement. They take no hit and
    /// are not drawn, shadow included.</summary>
    private HashSet<EnemyLimbPart> GoneParts(EnemyProfileResolution profile, uint typeId)
    {
        var gone = new HashSet<EnemyLimbPart>();
        foreach (var entry in profile.Limbs) if (entry.Removed) gone.Add(entry.Part);
        foreach (string name in Models.Replaced(typeId)) if (EnemyLimbPart.TryParse(name, out var part)) gone.Add(part);
        return gone;
    }

    internal void AssembleLimbs(EnemyDataBlock? data, GameObject? prefab)
    {
        var profiles = _profiles;
        if (profiles == null || data == null || prefab == null) return;
        uint typeId = data.persistentID;
        var profile = profiles.Resolve(typeId);
        // The behind-damage multiplier is an enemy-level constant and needs no limb, so a document that sets only
        // it still gets a rule. A removed part, or one a placement replaces, needs the resolution too: its limbs take no
        // hit. Invisibility does not: an invisible part still takes hits.
        var gone = GoneParts(profile, typeId);
        bool hides = gone.Count != 0;
        bool wantsParts = profile.Limbs.Count != 0 || hides;
        _hiddenLimbs.Remove(typeId);
        if (!wantsParts && profile.BackDamageMultiplier == null)
        {
            _limbParts.Remove(typeId);
            EnemyLimbRules.Remove(typeId);
            return;
        }
        var resolution = EnemyLimbResolution.Empty;
        if (wantsParts)
        {
            var agent = prefab.GetComponent<EnemyAgent>();
            if (agent == null || agent.Damage is not { } damage || agent.MainModelTransform is not { } model)
            {
                LimbPrefabLine(typeId, profile.Source, "limb-unresolved",
                    "the prefab has no enemy damage receiver or main model");
            }
            else
            {
                try
                {
                    var candidates = EnemyLimbPrefab.Candidates(damage, model);
                    resolution = EnemyLimbParts.Resolve(candidates, out var failure);
                    bool wantsRegions = false;
                    foreach (var limb in profile.Limbs) if (limb.Part.Kind != EnemyLimbPartKind.Native) wantsRegions = true;
                    if (wantsRegions && resolution.Unresolved.Count != 0)
                    {
                        var names = new List<string>();
                        foreach (var limb in resolution.Unresolved)
                            names.Add(limb.Name + "#" + limb.LimbId.ToString(CultureInfo.InvariantCulture));
                        LimbPrefabLine(typeId, profile.Source, "limb-unresolved", string.Join(",", names));
                    }
                    // Tumors the document never names do not need the sac pairing: a sac ambiguity is only a failure for a document that writes a tumor part.
                    bool wantsTumors = false;
                    foreach (var limb in profile.Limbs) if (limb.Part.Kind == EnemyLimbPartKind.Tumor && !limb.Removed) wantsTumors = true;
                    if (failure != null && (failure == EnemyLimbParts.TumorAmbiguous ? wantsTumors : wantsRegions || failure == "limb-id-ambiguous"))
                        LimbPrefabLine(typeId, profile.Source, failure, failure != EnemyLimbParts.TumorAmbiguous
                            ? "the requested limb identities could not be resolved uniquely" : TumorAmbiguity(candidates));
                }
                catch (Exception error)
                {
                    EnemyLog.Contained(error);
                    LimbPrefabLine(typeId, profile.Source, "limb-unresolved", error.GetType().Name);
                }
            }
            // The answer is cached even when it is empty: the spawn path then reports each configured part it
            // could not write instead of reporting that the type was never resolved at all.
            _limbParts[typeId] = resolution;
            if (hides)
            {
                var hidden = EnemyHitVisibility.HiddenLimbs(resolution, gone);
                if (hidden.Count != 0) _hiddenLimbs[typeId] = hidden.ToArray();
            }
        }
        var rule = EnemyLimbRules.Build(profile, resolution);
        if (rule == null) EnemyLimbRules.Remove(typeId);
        else EnemyLimbRules.Set(typeId, rule);
    }

    /// <summary>One line per enemy type and reason for the prefab phase. The game regenerates prefabs on a data
    /// reload and the same data gives the same answer, so the sentence is written once; the phase keeps its own set
    /// because a spawn-time `limb-part-missing` and a prefab-time `limb-tumor-ambiguous` are two different facts
    /// about one type and neither may hide the other.</summary>
    private void LimbPrefabLine(uint typeId, string? source, string reason, string detail)
        => TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.LimbsFailed, typeId, source, reason, detail);

    /// <summary>What an author does with a sac set that cannot be numbered: the tumor entries are not written (every other part
    /// is), each sac limb by its native number, and how far it stands from each sac surface.</summary>
    private static string TumorAmbiguity(List<EnemyLimbCandidate> candidates)
    {
        var sacs = candidates.Where(c => c.IsTumor).ToList();
        int surfaces = sacs.Count == 0 ? 0 : sacs[0].TumorDistances?.Length ?? 0;
        return "the tumor entries were not written, the other parts were: the model has " + sacs.Count.ToString(CultureInfo.InvariantCulture)
            + " sac limb(s) and " + surfaces.ToString(CultureInfo.InvariantCulture)
            + " sac surface(s) (EggSack/FleshSack), and each sac limb must stand nearest to a surface of its own; write these limbs by number ("
            + string.Join(", ", sacs.Select(c => "native_" + c.LimbId.ToString(CultureInfo.InvariantCulture))) + ") or remove the tumor entries | "
            + string.Join(" | ", sacs.Select(c => c.Name + "#" + c.LimbId.ToString(CultureInfo.InvariantCulture) + " distances ["
                + string.Join(",", (c.TumorDistances ?? Array.Empty<float>()).Select(d => d.ToString("0.###", CultureInfo.InvariantCulture))) + "]"));
    }

    private bool _typeUnreadableReported;

    /// <summary>What the prefab resolution mapped each authored part to, `part=id+id`, for the report of a part that matched.</summary>
    internal string ResolvedParts(uint typeId)
    {
        if (!_limbParts.TryGetValue(typeId, out var resolution)) return "none (type never resolved)";
        var rows = new List<string>();
        foreach (var pair in resolution.Parts) rows.Add(pair.Key.Name + "=" + string.Join("+", pair.Value));
        foreach (var unresolved in resolution.Unresolved)
            rows.Add("unresolved " + unresolved.LimbId.ToString(CultureInfo.InvariantCulture) + ":" + unresolved.Name);
        return rows.Count == 0 ? "empty" : string.Join(",", rows);
    }

    private static string Names(List<EnemyLimbPart> parts)
    {
        var names = new List<string>(parts.Count);
        foreach (var part in parts) names.Add(part.Name);
        return string.Join(",", names);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Enemies;
using ForgeEnemy.Profile;
using ForgeEngine.Framework;
using GameData;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>
/// The module's appearance half (I-ENEMY-APPEARANCE): the parts, reskins and attachments an enemy profile names,
/// assembled onto each live enemy before vanilla appearance initialization. The parts are the region entries of
/// `forge.eec.Model.ModelRef` `ModelRefs`, the reskins the region entries of `forge.eec.Model.Material`
/// `MaterialSets`, the attachments `forge.eec.Model.Bone.Prefabs`.
///
/// The application point is the native appearance initialization boundary. The game's prefab, damage setup and spawn chain
/// remain intact; renderers and material references belong only to that live object. The
/// existing despawn/world lifecycle releases those writes, including object-pool reuse; a placed mesh copy is shared and goes with the world. Nothing is networked.
///
/// Everything is presentation. Only renderer objects are added (copies of vanilla skinned renderers from assets that
/// are already loaded, and vanilla attachment prefabs), stripped of every component that is not a transform, mesh
/// filter or renderer; colliders, limbs and the damage setup stay the target's own. Nothing is ever scaled on the
/// shared skeleton: a surface that must not show is re-bound to a zero-scale anchor the assembler owns.
///
/// Placement (the slot entries of `forge.eec.Model.Bone` `Bones`) follows the same rule: the whole model is moved by
/// its own local pose, which takes the hit boxes under it along, and a region or a tumor is an offset baked into
/// the bind poses of a mesh copy, which moves the display and nothing else — the skeleton and the hit boxes stay
/// where the game put them, and the rest pose is the one the website's preview shows. `Place` states it.
///
/// A missing asset, a skeleton without the 19 core bones, an ambiguous bone name or an unknown prefab is a planned
/// refusal: the whole assembly is checked before the first write, the enemy keeps its vanilla look and the log names
/// the enemy, the file and the reason. A native call that still throws while writing is undone from the write log.
/// </summary>
internal sealed partial class EnemyModule
{
    /// <summary>The per-type records already written this session, by code, type, reason and detail: the game
    /// regenerates prefabs on a data reload and every spawn restarts a look, and the same data gives the same answer.
    /// A tuple of the strings the record point already holds, so a repeat allocates nothing.</summary>
    private readonly HashSet<(string Code, uint Type, string? Reason, string? Detail)> _typeRecords = new();
    private readonly ForgeCore.OnceReporter _truncatedTables = new();
    private readonly Dictionary<int, (GameObject Instance, List<Action> Undo)> _instanceAppearanceUndo = new();

    internal void InitializeInstanceAppearance(EnemyAgent enemy)
    {
        var instance = enemy.gameObject;
        if (instance == null || _instanceAppearanceUndo.ContainsKey(instance.GetInstanceID())) return;
        bool renderersChanged = AssembleAppearance(enemy.EnemyData, instance);
        if (!_instanceAppearanceUndo.TryGetValue(instance.GetInstanceID(), out var row)) return;
        var undo = row.Undo;
        EnemyProfileResolution? profile = enemy.EnemyData is { } data ? _profiles?.Resolve(data.persistentID) : null;
        var gone = new HashSet<EnemyLookTarget>();
        if (profile != null)
            foreach (var part in GoneParts(profile, enemy.EnemyData!.persistentID))
                if (EnemyLookTargets.TryParse(part.Name, out var target)) gone.Add(target);
        if (undo.Count == 0 && profile?.Look is not { HasPartLooks: true } && gone.Count == 0) { ReleaseInstanceAppearance(instance); return; }
        try
        {
            // The native glow animator can write every participating material. Source-part assets must therefore
            // be private even when no tint was authored.
            var copies = EnemyMaterialPass.Isolate(instance, undo);
            if (enemy.MaterialHandler is { } handler) RebuildMaterialReferences(handler, instance, copies, renderersChanged, undo);
            // The part looks come last: the handler now holds the instance's private materials, and the looks
            // replace the materials of the parts they name.
            if (profile != null && (profile.Look is { HasPartLooks: true } || gone.Count != 0))
            {
                ApplyPartLooks(enemy, instance, profile.Look ?? new EnemyLookProfile(), gone, enemy.EnemyData!.persistentID, profile.Source, undo);
            }
        }
        catch { ReleaseInstanceAppearance(instance); throw; }
    }

    /// <summary>
    /// Gives the handler an instance-owned reference list and the four feature groups the native per-instance
    /// initialization builds from it. Existing references retain their native feature flags through Carry; added or
    /// reskinned renderers get references directly, without invoking the build-time parser on a live character.
    /// Every material is already private, so <c>InstantiationInitialize</c> only builds the groups.
    /// </summary>
    private static void RebuildMaterialReferences(CharacterMaterialHandler handler, GameObject instance, Dictionary<int, Material> copies,
        bool renderersChanged, List<Action> undo)
    {
        var previous = handler.m_materialRefs;
        var own = new Il2CppSystem.Collections.Generic.List<MaterialRef>();
        undo.Add(() => { if (handler.m_materialRefs?.Pointer == own.Pointer) handler.m_materialRefs = previous; });
        handler.m_materialRefs = own;
        var uniqueFlag = handler.m_isMaterialRefsUnique;
        undo.Add(() => handler.m_isMaterialRefsUnique = uniqueFlag);
        var ellipsoids = handler.m_supportsEllipsoids;
        var glow = handler.m_supportsInnerGlow;
        var blobs = handler.m_supportsBlobs;
        var glue = handler.m_supportsGlue;
        MaterialGroup? newEllipsoids = null, newGlow = null, newBlobs = null, newGlue = null;
        undo.Add(() =>
        {
            if (SameGroup(handler.m_supportsEllipsoids, newEllipsoids)) handler.m_supportsEllipsoids = ellipsoids;
            if (SameGroup(handler.m_supportsInnerGlow, newGlow)) handler.m_supportsInnerGlow = glow;
            if (SameGroup(handler.m_supportsBlobs, newBlobs)) handler.m_supportsBlobs = blobs;
            if (SameGroup(handler.m_supportsGlue, newGlue)) handler.m_supportsGlue = glue;
        });
        try
        {
            if (previous != null)
                for (int index = 0; index < previous.Count; index++)
                    if (previous[index] is { } reference)
                    {
                        var carried = Carry(reference, copies, renderersChanged);
                        if (!renderersChanged || reference.m_renderers is not { Count: > 0 } || carried.m_renderers.Count > 0)
                            own.Add(carried);
                    }
            if (renderersChanged) AddAssemblyMaterialReferences(own, instance);
            for (int index = 0; index < own.Count; index++) own[index].m_hasBeenMadeUnique = true;
            handler.m_isMaterialRefsUnique = false;
            handler.m_supportsEllipsoids = null;
            handler.m_supportsInnerGlow = null;
            handler.m_supportsBlobs = null;
            handler.m_supportsGlue = null;
            handler.InstantiationInitialize();
        }
        finally
        {
            newEllipsoids = handler.m_supportsEllipsoids; newGlow = handler.m_supportsInnerGlow;
            newBlobs = handler.m_supportsBlobs; newGlue = handler.m_supportsGlue;
        }
    }

    private static MaterialRef Carry(MaterialRef reference, Dictionary<int, Material> copies, bool reconcile)
    {
        var material = reference.m_material;
        if (material != null && copies.TryGetValue(material.GetInstanceID(), out var copy)) material = copy;
        var renderers = new Il2CppSystem.Collections.Generic.List<Renderer>();
        if (reference.m_renderers is { } source)
            for (int index = 0; index < source.Count; index++)
            {
                var renderer = source[index];
                if (renderer == null) continue;
                var current = reconcile ? renderer.sharedMaterials : null;
                if (!reconcile || current is { Length: > 0 } && current[0] != null && ForgeCore.NativeObjects.Same(current[0], material)) renderers.Add(renderer);
            }
        return new MaterialRef
        {
            m_material = material,
            m_renderers = renderers,
            m_featureSupport = reference.m_featureSupport
        };
    }

    private static void AddAssemblyMaterialReferences(Il2CppSystem.Collections.Generic.List<MaterialRef> references, GameObject instance)
    {
        var byMaterial = new Dictionary<int, MaterialRef>();
        var covered = new HashSet<int>();
        for (int index = 0; index < references.Count; index++)
        {
            var reference = references[index];
            if (reference.m_material is { } material) byMaterial.TryAdd(material.GetInstanceID(), reference);
            if (reference.m_renderers is { } renderers)
                for (int renderer = 0; renderer < renderers.Count; renderer++)
                    if (renderers[renderer] is { } item) covered.Add(item.GetInstanceID());
        }
        foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            if (covered.Contains(renderer.GetInstanceID())) continue;
            var materials = renderer.sharedMaterials;
            // The native character material system has no per-slot reference. Preserve carried vanilla references,
            // but exclude new multi-material renderers and Ellipse helpers from its groups. Tint still visits all slots.
            if (materials is not { Length: 1 } || materials[0] is not { } material
                || renderer.name.Contains("Ellipse", StringComparison.Ordinal)) continue;
            if (!byMaterial.TryGetValue(material.GetInstanceID(), out var reference))
            {
                reference = new MaterialRef { m_material = material, m_renderers = new() };
                // Native CharacterBuilderParse, build 20403457, RVA 0x1738536..0x17385F5:
                // destruction follows the shader name; the other features follow property presence, not values.
                if (material.shader?.name.Contains("_CD", StringComparison.Ordinal) == true) reference.AddFeatureSupport(MaterialSupport.Destruction);
                if (material.HasProperty("_SupportsInnerGlow")) reference.AddFeatureSupport(MaterialSupport.InnerGlow);
                if (material.HasProperty("_SupportsBlobs")) reference.AddFeatureSupport(MaterialSupport.Blobs);
                if (material.HasProperty("_SupportsGlue")) reference.AddFeatureSupport(MaterialSupport.Glue);
                byMaterial.Add(material.GetInstanceID(), reference);
                references.Add(reference);
            }
            reference.m_renderers.Add(renderer);
        }
    }

    private static bool SameGroup(MaterialGroup? left, MaterialGroup? right)
        => left == null ? right == null : right != null && left.Pointer == right.Pointer;

    internal void ReleaseInstanceAppearance(GameObject? instance)
    {
        if (instance != null) ReleaseInstanceAppearance(instance.GetInstanceID());
    }

    internal void ReleaseInstanceAppearance(int instanceId)
    {
        if (!_instanceAppearanceUndo.Remove(instanceId, out var row)) return;
        EnemyAppearanceAssembler.Undo(row.Undo);
    }

    private void ClearInstanceAppearance()
    {
        foreach (var row in _instanceAppearanceUndo.Values) EnemyAppearanceAssembler.Undo(row.Undo);
        _instanceAppearanceUndo.Clear();
        EnemyAppearanceAssembler.ReleasePlacedMeshes();
    }

    /// <summary>A client's world can change while the level it built stays: its enemies keep the appearance native setup was
    /// built on (undoing it takes away the material groups the glow animator reads), and despawn releases each of them. Only
    /// instances the game has already destroyed are given up.</summary>
    private void ReleaseDestroyedInstanceAppearance()
    {
        foreach (var id in _instanceAppearanceUndo.Where(pair => pair.Value.Instance == null).Select(pair => pair.Key).ToArray())
            ReleaseInstanceAppearance(id);
    }

    /// <summary>Called by the native appearance initialization boundary for the live instance.</summary>
    /// <returns>Whether the assembly added renderers, materials or attachments to the instance.</returns>
    internal bool AssembleAppearance(EnemyDataBlock? data, GameObject? prefab)
    {
        try { return AssembleInstanceAppearance(data, prefab); }
        catch { ReleaseInstanceAppearance(prefab); throw; }
    }

    private bool AssembleInstanceAppearance(EnemyDataBlock? data, GameObject? prefab)
    {
        var profiles = _profiles;
        if (profiles == null || data == null || prefab == null) return false;
        uint typeId = data.persistentID;
        EnemyAppearanceAssembler.SweepPlacedMeshes();
        var profile = profiles.Resolve(typeId);
        if (profile.Appearance is not { Assembles: true } && profile.Fields is not { Bones.Count: > 0 }
            && profile.Look is not { HasMaterialPass: true } && profile.Look is not { HasPartLooks: true }
            && GoneParts(profile, typeId).Count == 0) return false;
        bool renderersChanged = false;
        ReleaseInstanceAppearance(prefab);
        var undo = new List<Action>();
        _instanceAppearanceUndo.Add(prefab.GetInstanceID(), (prefab, undo));
        // Skeleton entries are independent instance writes; they never alter the source prefab's transforms.
        var unresolvedBones = new List<string>();
        if (profile.Fields is { Bones.Count: > 0 } boneFields) ApplyBlockBones(prefab, boneFields.Bones, unresolvedBones, undo);
        foreach (string bone in unresolvedBones)
            TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.BonesFailed, typeId, profile.Source, "bone-missing", bone);
        if (profile.Appearance is { Assembles: true } appearance)
        {
            EnemyAppearanceOutcome outcome;
            try { outcome = EnemyAppearanceAssembler.Assemble(prefab, appearance); }
            catch (Exception error) { EnemyLog.Contained(error); outcome = EnemyAppearanceOutcome.Refused("native-error", error.GetType().Name); }
            if (outcome.Undo != null) undo.AddRange(outcome.Undo);
            if (outcome.Failure != null)
                TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, typeId, profile.Source, outcome.Failure,
                    outcome.Detail.Length == 0 ? null : outcome.Detail);
            else
            {
                var counts = outcome.Counts;
                renderersChanged = counts.Renderers > 0 || counts.Materials > 0 || counts.Attachments > 0;
                if (TypeRecordDue(RuntimeLogLevel.Info, EnemyLogCodes.AppearanceApplied, typeId))
                {
                    WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.AppearanceApplied, typeId, profile.Source, null, null, new[]
                    {
                        RuntimeLogValue.Of("renderers", counts.Renderers), RuntimeLogValue.Of("bones", counts.Bones),
                        RuntimeLogValue.Of("ancestorBones", counts.AncestorBones), RuntimeLogValue.Of("anchors", counts.Anchors),
                        RuntimeLogValue.Of("hiddenBones", counts.HiddenBones), RuntimeLogValue.Of("hiddenRigid", counts.HiddenRigid),
                        RuntimeLogValue.Of("materials", counts.Materials), RuntimeLogValue.Of("attachments", counts.Attachments),
                        RuntimeLogValue.Of("removedComponents", counts.RemovedComponents),
                        RuntimeLogValue.Of("placedMeshes", counts.PlacedMeshes), RuntimeLogValue.Of("placedBones", counts.PlacedBones)
                    });
                }
            }
        }
        // The material pass runs after the assembly so the renderers it copied in are tinted too, and whether or
        // not the assembly was refused: the two are independent writes.
        if (profile.Look is { HasMaterialPass: true } look) ApplyMaterialPass(prefab, look, typeId, profile.Source, undo);
        return renderersChanged;
    }

    /// <summary>
    /// Applies the `forge.eec.Model.Bone.Bones` entries onto the live instance's skeleton. The implementation lives
    /// in <c>EnemyFieldsNative.cs</c> because the bone lookup writes a `Transform` and the resolver that produced
    /// the entries is the same file's other half; a build without it (a slice suite that compiles this partial but
    /// not the block sink) writes nothing here, which is the no-op a suite that never authors a skeleton expects.
    /// </summary>
    partial void ApplyBlockBones(GameObject prefab, IReadOnlyList<EnemyFieldBone> bones, List<string> unresolved, List<Action> undo);

    /// <summary>Whether a per-type record is due: its level is open, this code, type, reason and detail have not been
    /// written this session, and the table has room. The first answer of true claims the key. Nothing is allocated
    /// on a closed level or a repeat, so a caller builds its value array only after this answers true.</summary>
    private bool TypeRecordDue(RuntimeLogLevel level, string code, uint typeId, string? reason = null, string? detail = null)
    {
        if (!Log.Logs(level)) return false;
        var key = (code, typeId, reason, detail);
        if (_typeRecords.Contains(key)) return false;
        if (_typeRecords.Count >= MaximumProfileDiagnostics) { Truncated("type-records", MaximumProfileDiagnostics); return false; }
        _typeRecords.Add(key);
        return true;
    }

    /// <summary>One per-type record with no values beyond the common ones, once per session.</summary>
    private void TypeRecord(RuntimeLogLevel level, string code, uint typeId, string? source, string? reason = null, string? detail = null)
    {
        if (TypeRecordDue(level, code, typeId, reason, detail)) WriteTypeRecord(level, code, typeId, source, reason, detail, null);
    }

    /// <summary>Writes one per-type record: values enemyType, file and, when given, reason, then <paramref name="extra"/>
    /// in order; the detail is message-only text. Callers have already passed the level check.</summary>
    private void WriteTypeRecord(RuntimeLogLevel level, string code, uint typeId, string? source, string? reason, string? detail,
        RuntimeLogValue[]? extra)
    {
        var values = new RuntimeLogValue[(reason == null ? 3 : 4) + (extra?.Length ?? 0)];
        values[0] = RuntimeLogValue.Of("enemyType", (long)typeId);
        values[1] = EnemyLabel(typeId);
        values[2] = RuntimeLogValue.Quote("file", source ?? "-");
        int next = 3;
        if (reason != null) values[next++] = RuntimeLogValue.Of("reason", reason);
        if (extra != null) foreach (var value in extra) values[next++] = value;
        // log-values-max: 15 -- enemyType, enemy, file, reason and at most the 11 values of the largest caller (the appearance record).
        Log.Write(level, code, new RuntimeLogFacts { Values = values, Detail = detail });
    }

    /// <summary>A per-session diagnostic table reached its bound: said once per table, at error.</summary>
    private void Truncated(string table, long count)
    {
        if (!Log.Logs(RuntimeLogLevel.Error) || !_truncatedTables.ShouldReport(table)) return;
        Log.Write(RuntimeLogLevel.Error, EnemyLogCodes.DiagnosticsTruncated, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("table", table), RuntimeLogValue.Of("count", count) }
        });
    }
}

/// <summary>What one assembly wrote, counted by kind.</summary>
internal readonly struct EnemyAppearanceCounts
{
    internal EnemyAppearanceCounts(int renderers, int bones, int ancestorBones, int anchors, int hiddenBones, int hiddenRigid,
        int materials, int attachments, int removedComponents, int placedMeshes, int placedBones)
    {
        Renderers = renderers; Bones = bones; AncestorBones = ancestorBones; Anchors = anchors; HiddenBones = hiddenBones;
        HiddenRigid = hiddenRigid; Materials = materials; Attachments = attachments; RemovedComponents = removedComponents;
        PlacedMeshes = placedMeshes; PlacedBones = placedBones;
    }
    internal int Renderers { get; }
    internal int Bones { get; }
    internal int AncestorBones { get; }
    internal int Anchors { get; }
    internal int HiddenBones { get; }
    internal int HiddenRigid { get; }
    internal int Materials { get; }
    internal int Attachments { get; }
    internal int RemovedComponents { get; }
    internal int PlacedMeshes { get; }
    internal int PlacedBones { get; }
}

/// <summary>What one assembly came to: a refusal code (and detail), or the counts of what was written.</summary>
internal readonly struct EnemyAppearanceOutcome
{
    private EnemyAppearanceOutcome(string? failure, string detail, EnemyAppearanceCounts counts, List<Action>? undo = null)
    { Failure = failure; Detail = detail; Counts = counts; Undo = undo; }

    internal string? Failure { get; }
    internal string Detail { get; }
    internal EnemyAppearanceCounts Counts { get; }
    internal List<Action>? Undo { get; }

    internal static EnemyAppearanceOutcome Refused(string code, string detail) => new(code, detail, default);
    internal static EnemyAppearanceOutcome Applied(EnemyAppearanceCounts counts, List<Action> undo) => new(null, string.Empty, counts, undo);
}

/// <summary>The assembler proper: a plan built from reads only, then the writes, each with its undo.</summary>
internal static class EnemyAppearanceAssembler
{
    private const string AnchorName = "ForgeAppearanceAnchor";
    private const string CopyPrefix = "ForgeAppearance ";

    private static string Name(Transform bone) => bone.name;
    private static Transform? Parent(Transform bone) => bone.parent;
    private static int Identity(Transform bone) => bone.GetInstanceID();

    private sealed class Refusal : Exception
    {
        internal Refusal(string code, string detail) : base(code) { Code = code; Detail = detail; }
        internal string Code { get; }
        internal string Detail { get; }
    }

    private sealed class SourceCopy
    {
        internal SourceCopy(SkinnedMeshRenderer renderer, EnemyBoneSlot<Transform>[] slots, Transform root, int ancestors)
        { Renderer = renderer; Slots = slots; Root = root; Ancestors = ancestors; }
        internal int Ancestors { get; }
        internal SkinnedMeshRenderer Renderer { get; }
        internal EnemyBoneSlot<Transform>[] Slots { get; }
        internal Transform Root { get; }
    }

    private sealed class Skin
    {
        internal Skin(SkinnedMeshRenderer renderer, EnemyBoneSlot<Transform>[] slots) { Renderer = renderer; Slots = slots; }
        internal SkinnedMeshRenderer Renderer { get; }
        internal EnemyBoneSlot<Transform>[] Slots { get; set; }
        internal readonly Dictionary<EnemyAppearanceRegion, Transform> Anchors = new();
    }

    private sealed class Counters
    {
        internal int Renderers, Bones, ViaAncestor, Anchors, Hidden, RigidHidden, Materials, Attachments, Removed;
        internal int PlacedMeshes, PlacedBones;
    }

    internal static EnemyAppearanceOutcome Assemble(GameObject prefab, EnemyAppearanceProfile appearance)
    {
        var undo = new List<Action>();
        try
        {
            var counts = Run(prefab, appearance, undo);
            return EnemyAppearanceOutcome.Applied(new EnemyAppearanceCounts(counts.Renderers, counts.Bones,
                counts.ViaAncestor, counts.Anchors, counts.Hidden, counts.RigidHidden, counts.Materials,
                counts.Attachments, counts.Removed, counts.PlacedMeshes, counts.PlacedBones), undo);
        }
        catch (Refusal refusal)
        {
            Undo(undo);
            return EnemyAppearanceOutcome.Refused(refusal.Code, refusal.Detail);
        }
        catch (Exception error)
        {
            EnemyLog.Contained(error);
            Undo(undo);
            return EnemyAppearanceOutcome.Refused("native-error", error.GetType().Name);
        }
    }

    /// <summary>Adds an undo step that touches <paramref name="target"/>, an object of the instance: the instance can be destroyed
    /// with its level before its appearance is released, and then nothing of it is left to restore.</summary>
    internal static void UndoOn(List<Action> undo, UnityEngine.Object target, Action restore)
        => undo.Add(() => { if (!ForgeCore.NativeObjects.Gone(target)) restore(); });

    internal static void Undo(List<Action> undo)
    {
        for (int index = undo.Count - 1; index >= 0; index--)
        {
            try { undo[index](); }
            catch (Exception error) { NativeFault.Note("enemy.appearance.undo", error); }
        }
        undo.Clear();
    }

    private static Counters Run(GameObject prefab, EnemyAppearanceProfile appearance, List<Action> undo)
    {
        // ---- Plan: reads only. Every refusal happens here, before the prefab is touched. ----
        var model = prefab.GetComponent<EnemyAgent>()?.MainModelTransform;
        if (model == null) throw new Refusal("target-model-missing", "the prefab has no EnemyAgent main model");
        var targetRenderers = new List<SkinnedMeshRenderer>();
        foreach (var renderer in model.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (renderer != null) targetRenderers.Add(renderer);
        if (targetRenderers.Count == 0) throw new Refusal("no-renderer", "target has no skinned renderer");
        // A part replacement, a reskin and an attachment all reach the target's own skeleton by bone name, and that
        // skeleton is the 19-bone humanoid one; a document that only places the model, its regions or its tumors
        // needs no index at all. Building it only when one of those three is asked for is what lets a non-humanoid
        // model be placed — and what makes a region the model has no bone for the explicit refusal below instead of
        // a core-bones refusal the author cannot connect to the region they moved.
        bool needsSkeleton = appearance.Parts.Count > 0 || appearance.Materials.Count > 0 || appearance.Attachments.Count > 0;
        EnemySkeletonIndex<Transform>? target = null;
        // The index refuses a target that does not name all 19 core bones, so a document that needs it always has a
        // `Hips` to hang a copied renderer's root on; a placement-only document never reads this.
        Transform hips = null!;
        if (needsSkeleton)
        {
            target = Index(targetRenderers, "target");
            target.TryGet("Hips", out hips);
        }

        var copies = new List<SourceCopy>();
        foreach (var part in appearance.Parts)
        {
            var sources = VisibleSkins(LoadModel(part.Model), part.Model);
            Index(sources, part.Model);
            int before = copies.Count;
            foreach (var source in sources)
            {
                var slots = EnemyAppearanceRemap.Source(Bones(source), part.Region, target!, Name, Parent, out int mapped, out int ancestors);
                if (mapped == 0) continue;
                var root = EnemyAppearanceRemap.Map(source.rootBone, target!, Name, Parent, out _) ?? hips;
                copies.Add(new SourceCopy(source, slots, root, ancestors));
            }
            if (copies.Count == before)
                throw new Refusal("region-empty", "model " + part.Model + " shows no " + EnemyAppearanceRegions.Name(part.Region) + " bone");
        }

        var reskins = new List<(EnemyAppearanceRegion Region, Material Material)>();
        foreach (var entry in appearance.Materials)
        {
            Material? material = null;
            foreach (var source in VisibleSkins(LoadModel(entry.Model), entry.Model))
            {
                if (!ShowsByName(Bones(source), entry.Region)) continue;
                var materials = source.sharedMaterials;
                if (materials != null && materials.Length > 0 && materials[0] != null) { material = materials[0]; break; }
            }
            if (material == null)
                throw new Refusal("material-missing", "model " + entry.Model + " has no material on a " + EnemyAppearanceRegions.Name(entry.Region) + " renderer");
            reskins.Add((entry.Region, material));
        }

        var hangs = new List<(EnemyAppearanceAttachment Entry, GameObject Prefab, Transform Bone)>();
        foreach (var attachment in appearance.Attachments)
        {
            if (!target!.TryGet(attachment.Bone, out var bone))
                throw new Refusal("bone-missing", "target has no bone " + attachment.Bone);
            hangs.Add((attachment, FindPrefab(attachment), bone));
        }

        // ---- Apply. ----
        var counts = new Counters();
        int layer = targetRenderers[0].gameObject.layer;
        var skins = new List<Skin>();
        foreach (var renderer in targetRenderers)
        {
            var bones = Bones(renderer);
            var slots = new EnemyBoneSlot<Transform>[bones.Count];
            for (int index = 0; index < bones.Count; index++)
                slots[index] = bones[index] is { } bone ? EnemyBoneSlot<Transform>.Real(bone) : EnemyBoneSlot<Transform>.None;
            skins.Add(new Skin(renderer, slots));
        }

        if (appearance.Parts.Count > 0)
        {
            var replaced = new List<EnemyAppearanceRegion>();
            foreach (var part in appearance.Parts) replaced.Add(part.Region);
            foreach (var skin in skins)
            {
                var hidden = EnemyAppearanceRemap.Hide(skin.Slots, replaced, Name, out int count);
                if (count == 0) continue;
                counts.Hidden += count;
                Write(skin, hidden, target!, counts, undo);
            }
            HideRigid(model, target!, replaced, counts, undo);
            foreach (var copy in copies)
            {
                var clone = Copy(copy.Renderer.gameObject, model, layer, counts, undo);
                var renderer = clone.GetComponent<SkinnedMeshRenderer>()
                    ?? throw new Refusal("no-renderer", "copied renderer lost its SkinnedMeshRenderer");
                renderer.rootBone = copy.Root;
                var skin = new Skin(renderer, copy.Slots);
                Write(skin, copy.Slots, target!, counts, undo);
                foreach (var slot in copy.Slots)
                    if (slot.Kind == EnemyBoneSlotKind.Bone) counts.Bones++;
                counts.ViaAncestor += copy.Ancestors;
                skins.Add(skin);
                counts.Renderers++;
            }
        }

        foreach (var (region, material) in reskins)
        {
            bool any = false;
            int count = skins.Count;
            for (int index = 0; index < count; index++)
            {
                var skin = skins[index];
                if (!EnemyAppearanceRemap.Shows(skin.Slots, region, Name)) continue;
                any = true;
                if (region == EnemyAppearanceRegion.Full || EnemyAppearanceRemap.OnlyShows(skin.Slots, region, Name))
                {
                    SetMaterial(skin.Renderer, material, undo);
                    counts.Materials++;
                    continue;
                }
                var (copySlots, originalSlots) = EnemyAppearanceRemap.Split(skin.Slots, region, Name);
                var clone = Copy(skin.Renderer.gameObject, skin.Renderer.transform.parent ?? model, layer, counts, undo);
                var renderer = clone.GetComponent<SkinnedMeshRenderer>()
                    ?? throw new Refusal("no-renderer", "split renderer lost its SkinnedMeshRenderer");
                var split = new Skin(renderer, copySlots);
                Write(split, copySlots, target!, counts, undo);
                Write(skin, originalSlots, target!, counts, undo);
                SetMaterial(renderer, material, undo);
                skins.Add(split);
                counts.Renderers++;
                counts.Materials++;
            }
            if (!any)
                throw new Refusal("region-empty", "no renderer shows " + EnemyAppearanceRegions.Name(region) + " to reskin");
        }

        // Placement runs after the parts and reskins because it states its regions over the renderers the assembly
        // actually ends with, copies included, and before the attachments because an attachment is hung on a bone
        // with its own authored transform and is not part of the model's own placement.
        if (appearance.Placement is { } placement) Place(model, skins, placement, counts, undo);

        foreach (var (entry, attachmentPrefab, bone) in hangs)
        {
            var instance = UnityEngine.Object.Instantiate<GameObject>(attachmentPrefab, bone);
            undo.Add(() => UnityEngine.Object.DestroyImmediate(instance));
            instance.name = CopyPrefix + entry.Name;
            counts.Removed += Strip(instance, recursive: true);
            instance.transform.localPosition = new Vector3(entry.Position[0], entry.Position[1], entry.Position[2]);
            instance.transform.localEulerAngles = new Vector3(entry.Rotation[0], entry.Rotation[1], entry.Rotation[2]);
            instance.transform.localScale = new Vector3(entry.Scale[0], entry.Scale[1], entry.Scale[2]);
            counts.Attachments++;
        }
        return counts;
    }

    /// <summary>
    /// The `placement` section. `full` is a local pose on the model root — the hit boxes ride under it, so they move
    /// with it — and every region and tumor is an offset baked into the bind poses of a mesh copy, which moves the
    /// display only: the skeleton, the colliders and the limb damage setup are untouched, the rest pose matches the
    /// website's preview, and animation carries the offset with the bone. Nothing here runs per frame: at most one
    /// copy per source mesh and placement, made by the first life and shared by every later life of the type until the world ends.
    ///
    /// Every rule is checked before the first mesh is copied, and a refusal anywhere leaves the prefab as the game
    /// built it because <see cref="Assemble"/> undoes the writes made so far.
    /// </summary>
    private static void Place(Transform model, List<Skin> skins, EnemyPlacementPlan placement, Counters counts, List<Action> undo)
    {
        if (placement.TryGet(EnemyPlacementSlot.Whole, out var whole))
        {
            var position = model.localPosition;
            var rotation = model.localRotation;
            var scale = model.localScale;
            // The preview places the whole enemy in the model's own frame, so the new local pose is `L0 · M`: the
            // offset's position goes through the model's own rotation and scale before it is added, and Unity's
            // `localRotation * offset` composes the offset's rotation inside the model's.
            var own = EnemyPlacementMatrix.Trs(new[] { position.x, position.y, position.z },
                new[] { rotation.x, rotation.y, rotation.z, rotation.w }, new[] { scale.x, scale.y, scale.z });
            var moved = own.TransformPoint(whole.Position[0], whole.Position[1], whole.Position[2]);
            var writtenPosition = new Vector3(moved[0], moved[1], moved[2]);
            var writtenRotation = rotation * new Quaternion(whole.Rotation[0], whole.Rotation[1], whole.Rotation[2], whole.Rotation[3]);
            var writtenScale = new Vector3(scale.x * whole.Scale[0], scale.y * whole.Scale[1], scale.z * whole.Scale[2]);
            UndoOn(undo, model, () =>
            {
                if (model.localPosition.Equals(writtenPosition)) model.localPosition = position;
                if (model.localRotation.Equals(writtenRotation)) model.localRotation = rotation;
                if (model.localScale.Equals(writtenScale)) model.localScale = scale;
            });
            model.localPosition = writtenPosition;
            model.localRotation = writtenRotation;
            model.localScale = writtenScale;
        }
        if (!placement.HasRegions && !placement.HasTumors) return;

        // A region the model has no bone for is a rule the document states and the prefab cannot honour: the offset
        // would apply to nothing, so the whole assembly is refused before the first mesh is copied.
        foreach (var entry in placement.Entries)
        {
            if (entry.Slot.IsTumor || entry.Slot.IsWhole) continue;
            if (!ShowsRegion(skins, entry.Slot.Region))
                throw new Refusal("placement-region-empty",
                    "no bone of the model lies in " + EnemyAppearanceRegions.Name(entry.Slot.Region));
        }
        var tumors = TumorRenderers(model, skins);
        foreach (var entry in placement.Entries)
            if (entry.Slot.IsTumor && entry.Slot.Tumor > tumors.Count)
                throw new Refusal("placement-tumor-missing",
                    "the model has " + Number(tumors.Count) + " EggSack renderer(s); " + entry.Slot.Name + " is not one of them");

        foreach (var skin in skins)
        {
            var renderer = skin.Renderer;
            // Only a slot bound to a real bone is a bone of the model; a slot bound to a zero-scale anchor is a
            // surface that is not shown, so an offset baked into its bind pose would be invisible work and would
            // make the count claim bones the document did not move.
            var slots = skin.Slots;
            var matrices = new EnemyPlacementMatrix[slots.Length];
            var placed = new bool[slots.Length];
            int placedBones = 0;
            EnemyPlacementMatrix? tumor = TumorMatrix(skin, tumors, placement);
            for (int index = 0; index < slots.Length; index++)
            {
                if (slots[index].Kind != EnemyBoneSlotKind.Bone) continue;
                if (BoneMatrix(slots[index].Bone, placement, tumor) is not { } matrix) continue;
                matrices[index] = matrix;
                placed[index] = true;
                placedBones++;
            }
            if (placedBones == 0) continue;
            var previous = renderer.sharedMesh
                ?? throw new Refusal("placement-mesh-missing", "renderer " + renderer.name + " has no mesh to place");
            var poses = previous.bindposes;
            if (poses.Length != slots.Length)
                throw new Refusal("placement-bindpose-mismatch", "renderer " + renderer.name + " has "
                    + Number(poses.Length) + " bind poses for " + Number(slots.Length) + " bones");
            var relative = Relative(renderer.transform, model);
            var inverse = EnemyPlacementMatrix.Inverse(relative);
            var updated = new Matrix4x4[poses.Length];
            for (int index = 0; index < poses.Length; index++)
                updated[index] = placed[index] ? poses[index] * ToUnity(MeshMatrix(inverse, relative, matrices[index])) : poses[index];
            // The copy is shared by every life with the same source mesh and the same placed poses, so a life only
            // restores its own renderer's mesh and never destroys the copy; the world's end does.
            var copy = PlacedMeshFor(previous, updated);
            undo.Add(() =>
            {
                if (renderer != null && ForgeCore.NativeObjects.Same(renderer.sharedMesh, copy)) renderer.sharedMesh = previous;
            });
            renderer.sharedMesh = copy;
            counts.PlacedMeshes++;
            counts.PlacedBones += placedBones;
        }
    }

    private sealed class PlacedMesh
    {
        internal PlacedMesh(Mesh source, Mesh copy, Matrix4x4[] poses) { Source = source; Copy = copy; Poses = poses; }
        internal Mesh Source { get; }
        internal Mesh Copy { get; }
        internal Matrix4x4[] Poses { get; }
    }

    // Placed mesh copies by source mesh. The placed poses are compared too, so another document's placement of the same
    // source mesh gets its own copy and two documents never share one. Nothing writes a copy after it is built.
    private static readonly Dictionary<int, List<PlacedMesh>> PlacedMeshes = new();

    private static Mesh PlacedMeshFor(Mesh source, Matrix4x4[] updated)
    {
        int key = source.GetInstanceID();
        if (!PlacedMeshes.TryGetValue(key, out var list)) PlacedMeshes.Add(key, list = new List<PlacedMesh>());
        foreach (var entry in list)
            if (entry.Copy != null && SamePoses(entry.Poses, updated)) return entry.Copy;
        var copy = UnityEngine.Object.Instantiate<Mesh>(source);
        copy.bindposes = new Il2CppStructArray<Matrix4x4>(updated);
        list.Add(new PlacedMesh(source, copy, updated));
        return copy;
    }

    private static bool SamePoses(Matrix4x4[] left, Matrix4x4[] right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            var a = left[index];
            var b = right[index];
            if (a.m00 != b.m00 || a.m01 != b.m01 || a.m02 != b.m02 || a.m03 != b.m03
                || a.m10 != b.m10 || a.m11 != b.m11 || a.m12 != b.m12 || a.m13 != b.m13
                || a.m20 != b.m20 || a.m21 != b.m21 || a.m22 != b.m22 || a.m23 != b.m23
                || a.m30 != b.m30 || a.m31 != b.m31 || a.m32 != b.m32 || a.m33 != b.m33) return false;
        }
        return true;
    }

    /// <summary>Destroys the copies whose source mesh the game has destroyed (a level unloaded); a placed copy is
    /// otherwise kept for the next life of its type.</summary>
    internal static void SweepPlacedMeshes()
    {
        if (PlacedMeshes.Count == 0) return;
        List<int>? dead = null;
        foreach (var (key, list) in PlacedMeshes)
            if (list[0].Source == null) (dead ??= new List<int>()).Add(key);
        if (dead == null) return;
        foreach (int key in dead)
        {
            foreach (var entry in PlacedMeshes[key]) if (entry.Copy != null) UnityEngine.Object.DestroyImmediate(entry.Copy);
            PlacedMeshes.Remove(key);
        }
    }

    /// <summary>The world's end: every placed copy goes, after the lives' own undo has put their meshes back.</summary>
    internal static void ReleasePlacedMeshes()
    {
        foreach (var list in PlacedMeshes.Values)
            foreach (var entry in list) if (entry.Copy != null) UnityEngine.Object.DestroyImmediate(entry.Copy);
        PlacedMeshes.Clear();
    }

    /// <summary>Whether any bone the assembly currently shows lies in a region. A hidden slot is an anchor, not a
    /// bone, so a region whose bones a part replacement hid still shows through the copy that replacement made.</summary>
    private static bool ShowsRegion(List<Skin> skins, EnemyAppearanceRegion region)
    {
        foreach (var skin in skins)
            foreach (var slot in skin.Slots)
                if (slot.Kind == EnemyBoneSlotKind.Bone && EnemyAppearanceRegions.Classify(Name(slot.Bone!)) == region) return true;
        return false;
    }

    /// <summary>The model's EggSack renderers in the website's natural name order, which is what numbers the tumors.
    /// Two renderers with the same name would make the Nth pairing depend on enumeration order, so that is refused
    /// rather than resolved.</summary>
    private static List<Renderer> TumorRenderers(Transform model, List<Skin> skins)
    {
        // The same pairing the hit parts and the part looks number tumors with: name order, same-named clones told apart by
        // their native Weakspot anchor, and nothing at all when two cannot be told apart.
        var found = EnemyLimbPrefab.OrderedTumorRenderers(model);
        if (found.Count == 0)
            foreach (var skin in skins)
                if (EnemyPlacementOrder.IsTumorRenderer(skin.Renderer.name))
                    throw new Refusal("placement-tumor-ambiguous", "two EggSack renderers cannot be told apart by name or anchor");
        return found;
    }

    /// <summary>The offset a whole tumor renderer carries, or null when this renderer is not a placed tumor.</summary>
    private static EnemyPlacementMatrix? TumorMatrix(Skin skin, List<Renderer> tumors, EnemyPlacementPlan placement)
    {
        int number = EnemyLimbPrefab.TumorNumber(tumors, skin.Renderer);
        return number != 0 && placement.TryGetTumor(number, out var transform) ? transform.Matrix : (EnemyPlacementMatrix?)null;
    }

    /// <summary>The model-space offset one bone carries: its own region's, its tumor renderer's, or both composed
    /// with the region outside the tumor. A bone belongs to exactly one region, so no two region offsets can claim
    /// it; a tumor renderer's own offset applies to every bone of that renderer on top.</summary>
    private static EnemyPlacementMatrix? BoneMatrix(Transform? bone, EnemyPlacementPlan placement, EnemyPlacementMatrix? tumor)
    {
        EnemyPlacementMatrix? matrix = null;
        if (bone != null && placement.TryGet(EnemyAppearanceRegions.Classify(Name(bone)), out var region))
            matrix = region.Matrix;
        if (tumor is { } whole) matrix = matrix is { } inner ? EnemyPlacementMatrix.Multiply(inner, whole) : whole;
        return matrix;
    }

    /// <summary>The renderer's own space in the model's: `model.worldToLocalMatrix * renderer.localToWorldMatrix`.
    /// The model's own world pose cancels out of that product, so `full` placement — which moves that pose — leaves
    /// it unchanged.</summary>
    private static EnemyPlacementMatrix Relative(Transform renderer, Transform model)
        => FromUnity(model.worldToLocalMatrix * renderer.localToWorldMatrix);

    /// <summary>The bind pose offset: `R⁻¹ · M · R`, with `R⁻¹` computed once for the renderer the bones belong to.</summary>
    private static EnemyPlacementMatrix MeshMatrix(EnemyPlacementMatrix inverse, EnemyPlacementMatrix relative, EnemyPlacementMatrix offset)
        => EnemyPlacementMatrix.Multiply(inverse, EnemyPlacementMatrix.Multiply(offset, relative));

    /// <summary>A Unity matrix as the pure 4×4, in the row-major order the `m00`..`m33` fields spell.</summary>
    private static EnemyPlacementMatrix FromUnity(Matrix4x4 matrix) => EnemyPlacementMatrix.FromRowMajor(new[]
    {
        matrix.m00, matrix.m01, matrix.m02, matrix.m03,
        matrix.m10, matrix.m11, matrix.m12, matrix.m13,
        matrix.m20, matrix.m21, matrix.m22, matrix.m23,
        matrix.m30, matrix.m31, matrix.m32, matrix.m33
    });

    private static Matrix4x4 ToUnity(EnemyPlacementMatrix matrix) => new Matrix4x4
    {
        m00 = matrix[0, 0],
        m01 = matrix[0, 1],
        m02 = matrix[0, 2],
        m03 = matrix[0, 3],
        m10 = matrix[1, 0],
        m11 = matrix[1, 1],
        m12 = matrix[1, 2],
        m13 = matrix[1, 3],
        m20 = matrix[2, 0],
        m21 = matrix[2, 1],
        m22 = matrix[2, 2],
        m23 = matrix[2, 3],
        m30 = matrix[3, 0],
        m31 = matrix[3, 1],
        m32 = matrix[3, 2],
        m33 = matrix[3, 3]
    };

    /// <summary>A skeleton by name from the bones its skinned renderers use, refusing ambiguity and a missing core.</summary>
    private static EnemySkeletonIndex<Transform> Index(IReadOnlyList<SkinnedMeshRenderer> renderers, string owner)
    {
        var lists = new List<IReadOnlyList<Transform?>>();
        foreach (var renderer in renderers) lists.Add(Bones(renderer));
        var index = EnemySkeletonIndex<Transform>.Build(lists, Name, Identity, out var ambiguous)
            ?? throw new Refusal("bone-ambiguous", owner + " names two different bones " + ambiguous);
        var missing = index.MissingCore();
        if (missing.Count != 0)
            throw new Refusal("core-bones-missing", owner + " lacks " + string.Join(",", missing));
        return index;
    }

    private static GameObject LoadModel(string path)
        => ForgeCore.GameAssets.Get<GameObject>(path)
            ?? throw new Refusal("asset-missing", "model " + path + " is not a loaded game asset");

    /// <summary>The skinned renderers a vanilla model asset shows as authored: enabled, on an active chain up to the
    /// asset root. Variant switches a customization would make at build time are not applied.</summary>
    private static List<SkinnedMeshRenderer> VisibleSkins(GameObject asset, string path)
    {
        var visible = new List<SkinnedMeshRenderer>();
        foreach (var renderer in asset.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (renderer != null && renderer.enabled && ActiveUpTo(renderer.transform, asset.transform)) visible.Add(renderer);
        if (visible.Count == 0) throw new Refusal("no-renderer", "model " + path + " has no visible skinned renderer");
        return visible;
    }

    private static bool ActiveUpTo(Transform node, Transform root)
    {
        for (var cursor = node; cursor != null; cursor = cursor.parent)
        {
            if (!cursor.gameObject.activeSelf) return false;
            if (ForgeCore.NativeObjects.Same(cursor, root)) return true;
        }
        return true;
    }

    private static List<Transform?> Bones(SkinnedMeshRenderer renderer)
    {
        var bones = renderer.bones;
        var list = new List<Transform?>(bones?.Length ?? 0);
        if (bones == null) return list;
        for (int index = 0; index < bones.Length; index++) list.Add(bones[index]);
        return list;
    }

    private static bool ShowsByName(List<Transform?> bones, EnemyAppearanceRegion region)
    {
        foreach (var bone in bones)
            if (bone != null && EnemyAppearanceRegions.Covers(region, EnemyAppearanceRegions.Classify(bone.name))) return true;
        return false;
    }

    /// <summary>`asset#name`: the one prefab of that name among the asset's `CB_CustomPrefab` entries.</summary>
    private static GameObject FindPrefab(EnemyAppearanceAttachment attachment)
    {
        var asset = ForgeCore.GameAssets.Get<GameObject>(attachment.Asset)
            ?? throw new Refusal("asset-missing", "customization " + attachment.Asset + " is not a loaded game asset");
        GameObject? found = null;
        foreach (var customizer in asset.GetComponentsInChildren<CB_CustomPrefab>(true))
        {
            var entries = customizer.prefabs;
            if (entries == null) continue;
            for (int index = 0; index < entries.Length; index++)
            {
                var candidate = entries[index]?.Prefab;
                if (candidate == null || candidate.name != attachment.Name) continue;
                if (found != null && !ForgeCore.NativeObjects.Same(found, candidate))
                    throw new Refusal("prefab-ambiguous", attachment.Prefab + " names two different prefabs");
                found = candidate;
            }
        }
        return found ?? throw new Refusal("prefab-missing", attachment.Prefab + " is not a CB_CustomPrefab entry of that asset");
    }

    /// <summary>A copy of one renderer object under <paramref name="parent"/>: its children dropped, everything but
    /// the renderer itself stripped, on the target model's layer.</summary>
    private static GameObject Copy(GameObject original, Transform parent, int layer, Counters counts, List<Action> undo)
    {
        var clone = UnityEngine.Object.Instantiate<GameObject>(original, parent);
        undo.Add(() => UnityEngine.Object.DestroyImmediate(clone));
        clone.name = CopyPrefix + original.name;
        for (int index = clone.transform.childCount - 1; index >= 0; index--)
            UnityEngine.Object.DestroyImmediate(clone.transform.GetChild(index).gameObject);
        counts.Removed += Strip(clone, recursive: false);
        clone.layer = layer;
        return clone;
    }

    /// <summary>Removes every component that is not presentation; one that survives removal refuses the assembly.</summary>
    private static int Strip(GameObject root, bool recursive)
    {
        var components = recursive ? root.GetComponentsInChildren<Component>(true) : root.GetComponents<Component>();
        int removed = 0;
        foreach (var component in components)
        {
            if (component == null || Presentation(component)) continue;
            UnityEngine.Object.DestroyImmediate(component);
            removed++;
        }
        var left = recursive ? root.GetComponentsInChildren<Component>(true) : root.GetComponents<Component>();
        foreach (var component in left)
            if (component != null && !Presentation(component))
                throw new Refusal("non-presentation-component", "a non-presentation component survived removal on " + root.name);
        return removed;
    }

    private static bool Presentation(Component component)
        => component.TryCast<Transform>() != null || component.TryCast<SkinnedMeshRenderer>() != null
            || component.TryCast<MeshRenderer>() != null || component.TryCast<MeshFilter>() != null;

    /// <summary>Writes a renderer's slots: real bones as they are, anchors resolved to the renderer's own zero-scale
    /// anchor under the region's boundary bone (made on first use).</summary>
    private static void Write(Skin skin, EnemyBoneSlot<Transform>[] slots, EnemySkeletonIndex<Transform> target,
        Counters counts, List<Action> undo)
    {
        var renderer = skin.Renderer;
        var previous = renderer.bones;
        var bones = new Transform?[slots.Length];
        for (int index = 0; index < slots.Length; index++)
        {
            var slot = slots[index];
            bones[index] = slot.Kind switch
            {
                EnemyBoneSlotKind.Bone => slot.Bone,
                EnemyBoneSlotKind.Anchor => Anchor(skin, slot.AnchorRegion, target, counts, undo),
                _ => null
            };
        }
        UndoOn(undo, renderer, () =>
        {
            var current = renderer.bones;
            if (current == null || current.Length != bones.Length) return;
            var restored = new Transform[current.Length];
            for (int i = 0; i < current.Length; i++)
                restored[i] = ForgeCore.NativeObjects.Same(current[i], bones[i]) && previous != null && i < previous.Length ? previous[i] : current[i];
            renderer.bones = new Il2CppReferenceArray<Transform>(restored);
        });
        renderer.bones = new Il2CppReferenceArray<Transform>(bones!);
        skin.Slots = slots;
    }

    private static Transform Anchor(Skin skin, EnemyAppearanceRegion region, EnemySkeletonIndex<Transform> target,
        Counters counts, List<Action> undo)
    {
        if (skin.Anchors.TryGetValue(region, out var existing)) return existing;
        string boundary = EnemyAppearanceRegions.Boundary(region);
        if (!target.TryGet(boundary, out var parent)) throw new Refusal("core-bones-missing", "target lacks " + boundary);
        var anchor = new GameObject(AnchorName);
        undo.Add(() => UnityEngine.Object.DestroyImmediate(anchor));
        anchor.transform.SetParent(parent, false);
        anchor.transform.localPosition = new Vector3(0f, 0f, 0f);
        anchor.transform.localEulerAngles = new Vector3(0f, 0f, 0f);
        anchor.transform.localScale = new Vector3(0f, 0f, 0f);
        skin.Anchors.Add(region, anchor.transform);
        counts.Anchors++;
        return anchor.transform;
    }

    /// <summary>Rigid meshes riding a replaced region's bones stop rendering (never disabled, so no script that reads
    /// `enabled` changes behaviour). `full` hides every rigid mesh of the model.</summary>
    private static void HideRigid(Transform model, EnemySkeletonIndex<Transform> target, List<EnemyAppearanceRegion> replaced,
        Counters counts, List<Action> undo)
    {
        bool full = replaced.Contains(EnemyAppearanceRegion.Full);
        var hidden = new List<Renderer>();
        undo.Add(() => ForgeVisual.Models.ShowRenderers(hidden));
        counts.RigidHidden += ForgeVisual.Models.HideRenderers(model, renderer =>
        {
            if (renderer.TryCast<MeshRenderer>() == null) return true;
            if (full) return false;
            for (var cursor = renderer.transform; cursor != null; cursor = cursor.parent)
            {
                if (!target.TryGet(cursor.name, out var bone) || !ForgeCore.NativeObjects.Same(bone, cursor)) continue;
                return !replaced.Contains(EnemyAppearanceRegions.Classify(cursor.name));
            }
            return true;
        }, hidden);
    }

    private static void SetMaterial(SkinnedMeshRenderer renderer, Material material, List<Action> undo)
    {
        var previous = renderer.sharedMaterials;
        int slots = Math.Max(1, previous?.Length ?? 0);
        var materials = new Material[slots];
        for (int index = 0; index < slots; index++) materials[index] = material;
        undo.Add(() => RestoreMaterials(renderer, material, previous));
        renderer.sharedMaterials = new Il2CppReferenceArray<Material>(materials);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal static void RestoreMaterials(Renderer renderer, Material written, Il2CppReferenceArray<Material>? previous)
    {
        if (renderer == null) return; // Destroyed with its level: nothing is left to restore.
        var current = renderer.sharedMaterials;
        if (current == null) return;
        var restored = new Material[current.Length];
        bool changed = false;
        for (int i = 0; i < current.Length; i++)
        {
            restored[i] = current[i];
            if (!ForgeCore.NativeObjects.Same(current[i], written) || previous == null || i >= previous.Length) continue;
            restored[i] = previous[i];
            changed = true;
        }
        if (changed) renderer.sharedMaterials = new Il2CppReferenceArray<Material>(restored);
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using Enemies;
using ForgeEnemy.Profile;
using ForgeEngine.Framework;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>
/// The part-look pass (`forge.enemyAppearance.Looks`): a surface colour and a fixed glow for the whole body, one body
/// region, a tumour or an appendage, written onto each live enemy at the appearance initialization boundary, right
/// after the game's own material handler parsed the instance's renderers.
///
/// Nothing shared is written. The instance's materials are already private copies (<see cref="EnemyMaterialPass.Isolate"/>);
/// a surface that takes a look is drawn with a new material instead, and a body renderer that has regions with their own
/// look is drawn from a split copy of its mesh: the same vertices, bones and triangles, sorted into one submesh per
/// surface by the region of each triangle's heaviest bone (ForgeVisual <c>SurfaceLook.SplitByBones</c>). The split mesh is
/// made once per source mesh and shared by every life that splits it the same way. The new material is ForgeVisual's
/// (<c>SurfaceLook.Paint</c>, the one implementation every host shares: sRGB colour, glow through <c>Glow.Write</c>) and is
/// not entered in the game's material handler, so the game's state glow, foam and blood no
/// longer reach that part. Only the enemy shaders the game uses (<see cref="SurfaceShaders"/>) are replaced. An invisible part
/// draws only its shadow (<c>SurfaceLook.SetShadowOnly</c>, a region through a shadow-only twin of the body); a removed or replaced
/// part is not drawn at all (<c>SurfaceLook.SetDrawn</c>, a region left out of the split).
///
/// Every refusal is named in the type's log. All writes are undone from the instance's undo list when the life ends.
/// </summary>
internal sealed partial class EnemyModule
{
    private readonly Dictionary<(int Source, string Signature), ForgeVisual.SurfaceSplit> _splitMeshes = new();

    private sealed class PartLookRun
    {
        internal PartLookRun(uint typeId, string? source, List<Action> undo)
        { TypeId = typeId; Source = source; Undo = undo; }

        internal uint TypeId { get; }
        internal string? Source { get; }
        internal List<Action> Undo { get; }
        internal int Parts, SplitMeshes, Submeshes, Materials, Glowing;
        internal readonly HashSet<EnemyLookTarget> Hit = new();
        internal readonly HashSet<string> Wrote = new(StringComparer.Ordinal);
    }

    private void ApplyPartLooks(EnemyAgent enemy, GameObject instance, EnemyLookProfile look, ICollection<EnemyLookTarget> gone,
        uint typeId, string? source, List<Action> undo)
    {
        var local = new List<Action>();
        var run = new PartLookRun(typeId, source, local);
        try { RunPartLooks(enemy, instance, look, gone, run); }
        catch (Exception error)
        {
            EnemyLog.Contained(error);
            EnemyAppearanceAssembler.Undo(local);
            TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, typeId, source, "native-error", error.GetType().Name);
            return;
        }
        undo.AddRange(local);
        foreach (var part in look.PartLooks)
            if (part.Target != EnemyLookTarget.Full && !run.Hit.Contains(part.Target))
                TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, typeId, source, "part-look-target-missing",
                    EnemyLookTargets.Name(part.Target));
        if (run.Parts == 0 && EnemyPartLooks.Has(look.PartLooks, EnemyLookTarget.Full))
            TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, typeId, source, "part-look-target-missing", "full");
        if ((run.Materials > 0 || run.Parts > 0) && TypeRecordDue(RuntimeLogLevel.Info, EnemyLogCodes.AppearanceApplied, typeId, "part-look"))
        {
            var wrote = new StringBuilder();
            foreach (string name in run.Wrote) { if (wrote.Length != 0) wrote.Append(','); wrote.Append(name); }
            WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.AppearanceApplied, typeId, source, null, "wrote=" + wrote, new[]
            {
                RuntimeLogValue.Of("phase", "part-look"), RuntimeLogValue.Of("parts", run.Parts),
                RuntimeLogValue.Of("splitMeshes", run.SplitMeshes), RuntimeLogValue.Of("submeshes", run.Submeshes),
                RuntimeLogValue.Of("materials", run.Materials), RuntimeLogValue.Of("glowing", run.Glowing)
            });
        }
    }

    private void RunPartLooks(EnemyAgent enemy, GameObject instance, EnemyLookProfile look, ICollection<EnemyLookTarget> gone, PartLookRun run)
    {
        SweepSplitMeshes();
        var plan = EnemyBodyLookPlan.Plan(look.PartLooks, gone);
        var renderers = instance.GetComponentsInChildren<Renderer>(false);
        var model = enemy.MainModelTransform ?? instance.transform;
        // Tumor N is the same renderer the hit parts number: one ordering for the look and the damage.
        var tumors = EnemyLimbPrefab.OrderedTumorRenderers(model);
        foreach (var renderer in renderers)
        {
            if (renderer == null || EnemyRendererRoles.IsAuxiliaryEffect(renderer.name) || IsPlacedModel(renderer.transform)) continue;
            var skin = renderer.TryCast<SkinnedMeshRenderer>();
            var mesh = skin != null ? skin.sharedMesh : renderer.TryCast<MeshRenderer>()?.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) continue;
            switch (EnemyRendererRoles.Of(renderer.name))
            {
                case EnemyRendererRole.Tumor:
                    int number = EnemyLimbPrefab.TumorNumber(tumors, renderer);
                    var tumorTarget = number >= 1 && number <= EnemyLookTargets.TumorCount ? EnemyLookTargets.Tumor(number) : EnemyLookTarget.Full;
                    if (ShadeWhole(renderer, EnemyPartLooks.Effective(look.PartLooks, gone, tumorTarget), run)) run.Hit.Add(tumorTarget);
                    break;
                case EnemyRendererRole.Appendage:
                    var side = EnemyRendererRoles.AppendageSide(model.InverseTransformPoint(renderer.bounds.center).x);
                    if (ShadeWhole(renderer, EnemyPartLooks.Effective(look.PartLooks, gone, side), run)) run.Hit.Add(side);
                    break;
                default:
                    if (skin != null) ShadeBody(skin, plan, run);
                    else if (ShadeWhole(renderer, plan.Rest, run)) run.Hit.Add(EnemyLookTarget.Core);
                    break;
            }
        }
    }

    /// <summary>A body renderer: split by region when some region has its own look, otherwise coloured whole.</summary>
    private void ShadeBody(SkinnedMeshRenderer renderer, EnemyBodyLookPlan plan, PartLookRun run)
    {
        if (!plan.NeedsSplit || plan.AllOff)
        {
            if (!ShadeWhole(renderer, plan.Rest, run)) return;
            run.Hit.Add(EnemyLookTarget.Core);
            // Every region taken out the same way is one whole-renderer look: each region's own entry reached this renderer too.
            if (plan.AllOff) foreach (var (region, _) in plan.Regions) Reached(region, run);
            return;
        }
        var mesh = renderer.sharedMesh;
        if (mesh == null) return;
        if (!mesh.isReadable)
        {
            TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, run.TypeId, run.Source, "part-look-mesh-unreadable", renderer.name);
            return;
        }
        var materials = renderer.sharedMaterials;
        if (materials == null || materials.Length != mesh.subMeshCount)
        {
            TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, run.TypeId, run.Source, "part-look-mesh-unsupported",
                renderer.name + " has " + (materials?.Length ?? 0) + " materials for " + mesh.subMeshCount + " submeshes");
            return;
        }
        var bones = renderer.bones;
        var groupOfBone = new int[bones?.Length ?? 0];
        for (int index = 0; index < groupOfBone.Length; index++)
            groupOfBone[index] = plan.GroupOfRegion(EnemyAppearanceRegions.Classify(bones![index]?.name));
        var hiddenGroup = new bool[plan.GroupCount];
        var notShadow = new bool[plan.GroupCount];
        bool shadows = false;
        for (int group = 0; group < hiddenGroup.Length; group++)
        {
            var effect = plan.LookOf(group);
            hiddenGroup[group] = effect?.Off == true;
            notShadow[group] = effect is not { Invisible: true, Removed: false };
            shadows |= !notShadow[group];
        }
        var split = SplitOnce(mesh, groupOfBone, plan, hiddenGroup, renderer, run);
        if (split == null) return;
        // The invisible regions keep their shadow: a shadow-only twin of the body draws them, skinned to the same bones.
        if (shadows && SplitOnce(mesh, groupOfBone, plan, notShadow, renderer, run) is { } shadow)
        {
            var sources = new Material[materials.Length];
            for (int index = 0; index < sources.Length; index++) sources[index] = materials[index];
            var twin = ForgeVisual.SurfaceLook.ShadowTwin(renderer, shadow, sources);
            run.Undo.Add(() => UnityEngine.Object.DestroyImmediate(twin));
        }
        var result = new Material[split.OriginSubMesh.Length];
        var present = new HashSet<int>();
        for (int index = 0; index < result.Length; index++)
        {
            var original = materials[split.OriginSubMesh[index]];
            var effect = plan.LookOf(split.Group[index]);
            result[index] = effect == null || !effect.Shades || original == null ? original! : Shade(original, effect, run) ?? original;
            present.Add(split.Group[index]);
        }
        for (int group = 0; group < hiddenGroup.Length; group++) if (hiddenGroup[group]) present.Add(group);
        var previousMesh = mesh;
        var previousMaterials = materials;
        var splitMesh = split.Result;
        EnemyAppearanceAssembler.UndoOn(run.Undo, renderer, () =>
        {
            if (!ForgeCore.NativeObjects.Same(renderer.sharedMesh, splitMesh)) return;
            renderer.sharedMesh = previousMesh;
            renderer.sharedMaterials = previousMaterials;
        });
        renderer.sharedMesh = splitMesh;
        renderer.sharedMaterials = new Il2CppReferenceArray<Material>(result);
        run.Parts++;
        run.Submeshes += result.Length;
        if (present.Contains(0) && plan.Rest != null) run.Hit.Add(EnemyLookTarget.Core);
        foreach (var (region, _) in plan.Regions)
            if (present.Contains(plan.GroupOfRegion(region))) Reached(region, run);
    }

    /// <summary>The split of one body mesh leaving out <paramref name="hiddenGroup"/>, made once per source mesh and shared by every life.</summary>
    private ForgeVisual.SurfaceSplit? SplitOnce(Mesh mesh, int[] groupOfBone, EnemyBodyLookPlan plan, bool[] hiddenGroup, Renderer renderer,
        PartLookRun run)
    {
        var key = (mesh.GetInstanceID(), ForgeVisual.SurfaceLook.SplitKey(groupOfBone, plan.GroupCount, hiddenGroup, null));
        if (_splitMeshes.TryGetValue(key, out var split)) return split;
        split = ForgeVisual.SurfaceLook.SplitByBones(mesh, groupOfBone, plan.GroupCount,
            plan.GroupOfRegion(EnemyAppearanceRegions.Fallback), hiddenGroup, null, renderer.name, out string reason);
        if (split == null)
        {
            TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, run.TypeId, run.Source, "part-look-mesh-unsupported", reason);
            return null;
        }
        _splitMeshes.Add(key, split);
        run.SplitMeshes++;
        return split;
    }

    /// <summary>The look targets a humanoid region answers for (the head region also carries the neck).</summary>
    private static void Reached(EnemyAppearanceRegion region, PartLookRun run)
    {
        switch (region)
        {
            case EnemyAppearanceRegion.Head: run.Hit.Add(EnemyLookTarget.Head); run.Hit.Add(EnemyLookTarget.Neck); break;
            case EnemyAppearanceRegion.Torso: run.Hit.Add(EnemyLookTarget.Torso); break;
            case EnemyAppearanceRegion.LeftArm: run.Hit.Add(EnemyLookTarget.LeftArm); break;
            case EnemyAppearanceRegion.RightArm: run.Hit.Add(EnemyLookTarget.RightArm); break;
            case EnemyAppearanceRegion.LeftLeg: run.Hit.Add(EnemyLookTarget.LeftLeg); break;
            case EnemyAppearanceRegion.RightLeg: run.Hit.Add(EnemyLookTarget.RightLeg); break;
        }
    }

    /// <summary>A renderer that takes one look on every material: tumours and appendages, and a body nothing splits.</summary>
    private bool ShadeWhole(Renderer renderer, EnemyLookEffect? effect, PartLookRun run)
    {
        if (effect == null) return false;
        if (effect.Removed)
        {
            // forceRenderingOff, not enabled: the game's culling switches enabled back on by itself. No shadow either.
            var hidden = new List<Renderer>(1);
            if (ForgeVisual.Models.HideRenderers(renderer.transform, other => other != renderer, hidden) != 0)
                run.Undo.Add(() => ForgeVisual.Models.ShowRenderers(hidden));
            run.Parts++;
            return true;
        }
        if (effect.Invisible)
        {
            // A body the game already draws as a shadow (its shadow enemies) is left as it is.
            if (renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
            {
                ForgeVisual.SurfaceLook.SetShadowOnly(renderer, true);
                run.Undo.Add(() => { if (renderer != null) ForgeVisual.SurfaceLook.SetShadowOnly(renderer, false); });
            }
            run.Parts++;
            return true;
        }
        if (!effect.Shades) return true;
        var materials = renderer.sharedMaterials;
        if (materials == null || materials.Length == 0) return false;
        var result = new Material[materials.Length];
        bool changed = false;
        for (int index = 0; index < result.Length; index++)
        {
            var original = materials[index];
            var copy = original == null ? null : Shade(original, effect, run);
            result[index] = copy ?? original!;
            if (copy == null) continue;
            changed = true;
            var written = copy;
            run.Undo.Add(() => EnemyAppearanceAssembler.RestoreMaterials(renderer, written, materials));
        }
        if (!changed) return false;
        renderer.sharedMaterials = new Il2CppReferenceArray<Material>(result);
        run.Parts++;
        run.Submeshes += result.Length;
        return true;
    }

    /// <summary>The enemy shaders a part look replaces; any other surface keeps the game's look and is refused by name.</summary>
    private static readonly HashSet<string> SurfaceShaders = new(StringComparer.Ordinal)
    { "Cell/Enemy/EnemyFlesh_CD", "Cell/Enemy/EnemyFlesh", "GTFO/FleshBulb", "Cell/CellSurface/Default" };
    private const string InfestedShaders = "Cell/Infested/";

    /// <summary>The material that replaces <paramref name="source"/> for a part with a look: ForgeVisual's
    /// <c>SurfaceLook.Paint</c> of the effect, undone with the life. Null (and a named log) when the source's shader is not an
    /// enemy surface or the look cannot be written on it.</summary>
    private Material? Shade(Material source, EnemyLookEffect effect, PartLookRun run)
    {
        string shader;
        try { shader = source.shader?.name ?? string.Empty; }
        catch (Exception error) { NativeFault.Note("enemy.part-look.shader-name", error); shader = string.Empty; }
        if (!SurfaceShaders.Contains(shader) && !shader.StartsWith(InfestedShaders, StringComparison.Ordinal))
        {
            TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, run.TypeId, run.Source, "part-look-shader-unavailable",
                shader.Length == 0 ? "(no shader)" : shader);
            return null;
        }
        var look = new ForgeVisual.SurfaceAppearance(effect.Color, effect.Glow, effect.Brightness);
        Material copy;
        try { copy = ForgeVisual.SurfaceLook.Paint(source, look); }
        catch (InvalidOperationException error)
        {
            TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, run.TypeId, run.Source, "part-look-unsupported", error.Message);
            return null;
        }
        run.Undo.Add(() => UnityEngine.Object.DestroyImmediate(copy));
        if (effect.Color != null) run.Wrote.Add("colour");
        if (effect.Glow != null) { run.Wrote.Add("glow"); run.Glowing++; }
        run.Materials++;
        return copy;
    }
    /// <summary>Whether the renderer belongs to a model assembly placed on the enemy (<see cref="ForgeEngine.GameBindings.NativeModelRuntime"/>):
    /// the author's own pieces, which the enemy's part looks never reach.</summary>
    private static bool IsPlacedModel(Transform transform)
    {
        for (var cursor = transform; cursor != null; cursor = cursor.parent)
            if (cursor.name.StartsWith(ForgeEngine.GameBindings.NativeModelRuntime.CopyPrefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Drops the split meshes whose source mesh the game has destroyed (a placed copy goes with its life).</summary>
    private void SweepSplitMeshes()
    {
        if (_splitMeshes.Count == 0) return;
        List<(int, string)>? dead = null;
        foreach (var entry in _splitMeshes)
            if (entry.Value.Source == null) (dead ??= new()).Add(entry.Key);
        if (dead == null) return;
        foreach (var key in dead)
        {
            if (_splitMeshes.Remove(key, out var split)) UnityEngine.Object.DestroyImmediate(split.Result);
        }
    }
}

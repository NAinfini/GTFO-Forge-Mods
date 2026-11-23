using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CharacterDestruction;
using Enemies;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeVisual;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeEnemy.Native;

/// <summary>Accepted flattened model assemblies, keyed by the exact authored enemy DataBlock.</summary>
internal sealed class EnemyModelAssemblies : IDisposable
{
    private readonly IReadOnlyDictionary<uint, Entry> _entries;
    private readonly EnemyLog _log;
    private readonly OnceReporter _once = new();
    // Stages a host reported before this machine placed the part (a client spawn arriving after the state).
    private Dictionary<int, int> _pending = new();
    // Bind-pose copies of deforming placements, one per enemy type, assembly and renderer: every spawn of the type
    // shares them. Released with the world.
    private readonly Dictionary<(uint Type, int Ordinal, int Renderer, int Mesh), Mesh> _skins = new();
    /// <summary>The bind-pose agreement a deforming placement needs between the part bone and the target bone it
    /// follows: the tolerance the website's mapping generator admitted the pair with (metres, and per basis element).</summary>
    private const float DeformTolerance = 0.001f;
    // The pose copies of skinned placements, by enemy and assembly: released (the enemy's animator handed back) with the placement.
    private readonly Dictionary<(IntPtr Enemy, string Assembly), PoseCopy> _poses = new();
    // The enemies this machine placed a type's assemblies on: a death removes only what its own type placed, and an enemy without
    // a placement costs one lookup.
    private readonly Dictionary<IntPtr, (Entry Entry, string Instance)> _placed = new();
    private readonly List<int> _stale = new();

    // The `enemy` log value naming a type by its work name (`EnemyModule.EnemyLabel`); the bare number without it.
    private readonly Func<uint, RuntimeLogValue> _label;

    private EnemyModelAssemblies(IReadOnlyDictionary<uint, Entry> entries, EnemyLog log, Func<uint, RuntimeLogValue> label)
    { _entries = entries; _log = log; _label = label; }

    internal static EnemyModelAssemblies Empty(EnemyLog log, Func<uint, RuntimeLogValue>? label = null)
        => new(new Dictionary<uint, Entry>(), log, label ?? (type => RuntimeLogValue.Of("enemy", type.ToString(CultureInfo.InvariantCulture))));

    // Accepted enemy definitions enter the native spawn pool, including child waves. Their models are common
    // enemy resources, not placements owned by the level that happens to demonstrate the enemy. `imported` holds the imported models
    // the shipping package carries, by reference key.
    internal static NativeModelRuntime.NativeModelRegistration Prepare(string owner, NativeModelContent content,
        IReadOnlyDictionary<string, ImportedModelPackage>? imported = null)
        => NativeModelRuntime.Prepare(owner, content, static () => true, imported);

    internal EnemyModelAssemblies With(uint enemyType, string owner, NativeModelContent content)
    {
        var copy = new Dictionary<uint, Entry>(_entries) { [enemyType] = new(owner, content) };
        return new EnemyModelAssemblies(copy, _log, _label);
    }

    /// <summary>How many of an enemy's placements have a hit role, and how many of those took their place as a hit part.</summary>
    internal readonly record struct HitPlacement(int Requested, int Placed);

    /// <summary>The names of the vanilla parts the type's placements replace; they take no hit.</summary>
    internal IEnumerable<string> Replaced(uint enemyType)
    {
        if (!_entries.TryGetValue(enemyType, out var entry)) yield break;
        foreach (var assembly in entry.Content.Assemblies) if (assembly.Replaces is { } part) yield return part;
    }

    /// <summary>
    /// Places every assembly of the enemy's type. An assembly with a hit role becomes one native damage limb on its
    /// placement root, hit on the model's own simplified mesh; breakEffect answers the vanilla tumor burst a
    /// breakable part plays.
    /// </summary>
    internal HitPlacement Apply(EnemyAgent? enemy, Func<EnemyAgent, uint?>? readType, Func<GameObject?> breakEffect)
    {
        if (enemy == null || readType == null || readType(enemy) is not { } type
            || !_entries.TryGetValue(type, out var entry)) return default;
        int requested = 0, placedHits = 0;
        Remove(enemy, discardPending: false);
        string instance = Instance(enemy);
        _placed[enemy.Pointer] = (entry, instance);
        // Imported parts draw where the body draws: a bone's layer is its hit layer, which the camera need not render.
        int? bodyLayer = entry.Content.ImportedResources.Count == 0 ? null : BodyLayer(enemy);
        for (int ordinal = 0; ordinal < entry.Content.Assemblies.Count; ordinal++)
        {
            var assembly = entry.Content.Assemblies[ordinal];
            if (assembly.Hit != null) requested++;
            if (_pending.TryGetValue(StageKey(enemy.GlobalID, ordinal), out int pendingStage)
                && (assembly.Hit == null || pendingStage > assembly.Hit.CrackAt.Count))
            { Refused(type, assembly.Id, "enemy-model-limb-stage-range"); Reserve(enemy, type, entry, instance, ordinal, assembly, null); continue; }
            var host = Host(enemy, assembly.Host, out var refusal);
            if (host == null) { Refused(type, assembly.Id, refusal!); Reserve(enemy, type, entry, instance, ordinal, assembly, null); continue; }
            EnemyModelHitParts.HitEntry? added = null;
            var pieces = new List<EnemyModelHitParts.HitEntry>();
            var skipped = new List<string>();
            int index = ordinal;
            SkinRebindResult deformed = default;
            try
            {
                refusal = NativeModelRuntime.Place(entry.Owner, assembly, host, instance, assembly.Hit == null && assembly.Deform == null ? null
                    : (root, draws) =>
                    {
                        if (assembly.Skin != null) return Skin(entry, instance, index, enemy, assembly, root, breakEffect, pieces, skipped);
                        // The skin moves onto the enemy's bones first: the hit box and the crack layer are then
                        // built from the renderers as they will draw.
                        if (assembly.Deform != null && Deform(enemy, type, index, assembly, root, out deformed) is { } bent) return bent;
                        if (assembly.Hit == null) return null;
                        var result = EnemyModelHitParts.Add(entry.Owner, instance, index, enemy, assembly, host, root,
                            breakEffect, draws, bodyLayer, skipped, out var row);
                        added = row;
                        return result;
                    }, drawLayer: bodyLayer);
            }
            catch
            {
                EnemyModelHitParts.Rollback(added);
                foreach (var piece in pieces) EnemyModelHitParts.Rollback(piece);
                ReleasePose((enemy.Pointer, assembly.Id));
                throw;
            }
            if (refusal != null)
            {
                EnemyModelHitParts.Rollback(added);
                foreach (var piece in pieces) EnemyModelHitParts.Rollback(piece);
                ReleasePose((enemy.Pointer, assembly.Id));
                Refused(type, assembly.Id, refusal);
                Reserve(enemy, type, entry, instance, ordinal, assembly, host);
                continue;
            }
            if (added != null || pieces.Count != 0) placedHits++;
            foreach (var reason in skipped) Skipped(type, assembly.Id, reason);
            if (added != null && _pending.Remove(StageKey(added.EnemyId, added.Ordinal), out int stage))
                ShowStage(added, stage);
            if (_log.Logs(RuntimeLogLevel.Info))
                _log.Write(RuntimeLogLevel.Info, EnemyLogCodes.ModelApplied, new RuntimeLogFacts
                {
                    Values = new[]
                    {
                        RuntimeLogValue.Of("enemyType", (long)type),
                        _label(type),
                        RuntimeLogValue.Of("assembly", assembly.Id),
                        RuntimeLogValue.Of("parts", (long)assembly.Parts.Count),
                        RuntimeLogValue.Of("importedParts", (long)assembly.ImportedParts.Count),
                        RuntimeLogValue.Of("deformRenderers", (long)deformed.Renderers),
                        RuntimeLogValue.Of("deformBones", (long)deformed.Bones),
                        RuntimeLogValue.Of("deformPositionError", deformed.PositionError),
                        RuntimeLogValue.Of("deformBasisError", deformed.BasisError),
                        RuntimeLogValue.Of("hitLimb", (long)(added?.Limb.m_limbID ?? (pieces.Count != 0 ? pieces[0].Limb.m_limbID : -1))),
                        RuntimeLogValue.Of("hitTriangles", (long)((assembly.HitMesh?.Indices.Length ?? assembly.Skin?.Links.Sum(link => link.HitMesh?.Indices.Length ?? 0) ?? 0) / 3)),
                        RuntimeLogValue.Of("skinLinks", (long)(assembly.Skin?.Links.Count ?? 0)),
                        RuntimeLogValue.Of("skinHitPieces", (long)pieces.Count)
                    }
                });
        }
        return new HitPlacement(requested, placedHits);
    }

    /// <summary>
    /// A deforming placement: the copied enemy model's skinned renderers follow this enemy's own bones through the
    /// package's verified bone pairs (I-ENEMY-APPEARANCE, Web/src/forge/enemy/enemy-skin-families.json). Every pair must
    /// resolve on both skeletons and agree in bind pose, or the placement is refused with the reason; it is never drawn
    /// rigid instead. The target's bind poses are read from its model asset as the game ships it: this enemy's own
    /// appearance (a placement offset, a part replacement, a split look) rewrites its meshes' bind poses, not its skeleton.
    /// The rebinding itself is ForgeVisual's.
    /// </summary>
    private string? Deform(EnemyAgent enemy, uint type, int ordinal, NativeModelAssembly assembly, GameObject root, out SkinRebindResult result)
    {
        result = default;
        var target = enemy.MainModelTransform;
        if (target == null) return "model-enemy-host-missing";
        var shipped = enemy.MainModelData?.ModelFile is { Length: > 0 } file ? ForgeCore.GameAssets.Get<GameObject>(file) : null;
        if (shipped == null) return "model-deform-target-asset-missing";
        var source = NativeModelRuntime.PlacedPartRoot(root, assembly.Parts[0]);
        if (source == null) return "model-deform-part-missing";
        var shippedRest = SkinRebind.BindRest(shipped.transform);
        var bones = new Dictionary<IntPtr, Transform>(assembly.Deform!.Bones.Count);
        var rest = new Dictionary<IntPtr, Matrix4x4>(assembly.Deform.Bones.Count);
        foreach (var pair in assembly.Deform.Bones)
        {
            var from = NativeModelRuntime.ResolveChild(source, pair.SourcePath, pair.SourceIndices, out var missing);
            if (from == null) return "model-deform-source-bone:" + string.Join("/", pair.SourcePath) + ":" + missing;
            var to = NativeModelRuntime.ResolveChild(target, pair.TargetPath, pair.TargetIndices, out missing);
            if (to == null) return "model-deform-target-bone:" + string.Join("/", pair.TargetPath) + ":" + missing;
            bones[from.Pointer] = to;
            if (NativeModelRuntime.ResolveChild(shipped.transform, pair.TargetPath, pair.TargetIndices, out _) is { } asShipped
                && shippedRest.TryGetValue(asShipped.Pointer, out var bind)) rest[to.Pointer] = bind;
        }
        var refusal = SkinRebind.Rebind(root.transform, source, rest, bones, DeformTolerance, (renderer, mesh, binds) =>
        {
            var key = (type, ordinal, renderer, mesh.GetInstanceID());
            if (!_skins.TryGetValue(key, out var copy) || copy == null) _skins[key] = copy = SkinRebind.WithBindposes(mesh, binds);
            return copy;
        }, out result);
        return refusal == null ? null : "model-" + refusal;
    }

    /// <summary>
    /// A skinned placement (M26c, Web/src/forge/enemy/enemy-skin-binding.ts): the imported model keeps its own skeleton, and every frame
    /// each linked model bone copies the turn of its host bone (ForgeVisual <see cref="PoseCopy"/>, after the enemy's animator and the
    /// joint clips), so it moves with the vanilla and the custom actions alike. The host bones are found by path below the enemy's model,
    /// their rest read from the model asset as the game ships it (this enemy's own appearance may have rewritten its meshes' bind poses).
    /// The author's bone offsets move the model bones once, at rest. Each link with a hit piece gets its own native limb on its model
    /// bone, on the values of the native limb its host bone carries (the vanilla part it stands for), the authored role over them.
    /// </summary>
    private string? Skin(Entry entry, string instance, int ordinal, EnemyAgent enemy, NativeModelAssembly assembly, GameObject root,
        Func<GameObject?> breakEffect, List<EnemyModelHitParts.HitEntry> pieces, List<string> skipped)
    {
        var body = enemy.MainModelTransform;
        if (body == null) return "model-enemy-host-missing";
        var shipped = enemy.MainModelData?.ModelFile is { Length: > 0 } file ? ForgeCore.GameAssets.Get<GameObject>(file) : null;
        if (shipped == null) return "model-skin-host-asset-missing";
        var renderer = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault();
        if (renderer == null) return "model-skin-model-missing";
        var links = assembly.Skin!.Links;
        var shippedRest = SkinRebind.BindRest(shipped.transform);
        var hostBones = new Transform[links.Count];
        var rest = new Matrix4x4[links.Count];
        var pose = new PoseLink[links.Count];
        var modelBones = new Dictionary<string, Transform>(StringComparer.Ordinal);
        foreach (var bone in renderer.bones) if (bone != null) modelBones.TryAdd(bone.name, bone);
        for (int at = 0; at < links.Count; at++)
        {
            var link = links[at];
            if (!Enum.TryParse<BodyBone>(link.Body, out var humanoid)) return "model-skin-body-bone:" + link.Body;
            if (!modelBones.TryGetValue(link.Model, out var own)) return "model-skin-model-bone:" + link.Model;
            hostBones[at] = NativeModelRuntime.ResolveChild(body, link.HostPath, link.HostIndices, out var missing)!;
            if (hostBones[at] == null) return "model-skin-host-bone:" + string.Join("/", link.HostPath) + ":" + missing;
            if (NativeModelRuntime.ResolveChild(shipped.transform, link.HostPath, link.HostIndices, out _) is not { } asShipped
                || !shippedRest.TryGetValue(asShipped.Pointer, out rest[at])) return "model-skin-host-rest:" + string.Join("/", link.HostPath);
            pose[at] = new PoseLink(link.Model, humanoid, link.Toward, link.From);
            if (link.Offset[0] != 0f || link.Offset[1] != 0f || link.Offset[2] != 0f)
                own.position += renderer.transform.TransformVector(new Vector3(link.Offset[0], link.Offset[1], link.Offset[2]));
        }
        string label = "enemy skin " + assembly.Id;
        var target = Models.ReadPoseTarget(renderer.transform, renderer, pose, label);
        if (target == null) return "model-skin-pose-target";
        // The model stands as tall as the body it rides (head bone over the floor, both at rest); the placement's own scale multiplies this.
        // The placement sits under the body, which already carries the enemy's size, so the body's head height is read in its own unscaled units.
        for (int at = 0; at < links.Count; at++)
            if (pose[at].Body == BodyBone.Head && rest[at].m13 > 0f && target.HeadHeight > 0f)
                root.transform.localScale *= rest[at].m13 / target.HeadHeight;
        var copy = Models.CopyPose(body, hostBones, rest, body.gameObject.GetComponentsInChildren<Animator>(true).FirstOrDefault(), target, label);
        if (copy == null) return "model-skin-pose-copy";
        copy.Follow();
        _poses[(enemy.Pointer, assembly.Id)] = copy;
        for (int at = 0; at < links.Count; at++)
        {
            if (links[at].HitMesh is not { } mesh) continue;
            var piece = new GameObject("ForgeModel.SkinHit." + links[at].Model);
            piece.transform.SetParent(target.Bone(at), false);
            var refusal = EnemyModelHitParts.Add(entry.Owner, instance, ordinal, enemy, assembly with { HitMesh = mesh }, hostBones[at], piece,
                breakEffect, Array.Empty<InstancedDraw>(), null, skipped, out var row);
            if (refusal != null) return refusal;
            pieces.Add(row!);
        }
        return null;
    }

    private void ReleasePose((IntPtr Enemy, string Assembly) key)
    {
        if (_poses.Remove(key, out var copy)) copy.Release();
    }

    /// <summary>A refused assembly loses only its visual: its hit role still takes its place in the limb table, so the
    /// limb indices every machine derives come from the authored assemblies alone and never from which local
    /// resources happened to load.</summary>
    private void Reserve(EnemyAgent enemy, uint type, Entry entry, string instance, int ordinal,
        NativeModelAssembly assembly, Transform? anchor)
    {
        if (assembly.Hit == null) return;
        // A skinned placement holds one limb per hit piece.
        int limbs = assembly.Skin?.Links.Count(link => link.HitMesh != null) ?? 1;
        string? refusal = null;
        for (int at = 0; at < limbs && refusal == null; at++)
        {
            try { refusal = EnemyModelHitParts.Reserve(entry.Owner, instance, ordinal, enemy, assembly, anchor ?? enemy.MainModelTransform); }
            catch (Exception caught) { EnemyLog.Contained(caught); refusal = "model-hit-reserve-exception"; }
        }
        if (refusal != null) Refused(type, assembly.Id, refusal);
    }

    /// <summary>Removes the enemy's placements and hit limbs. True when a removed part had a shown crack stage,
    /// which the host's replicated stage table must then drop.</summary>
    internal bool Remove(EnemyAgent? enemy, bool discardPending = true)
    {
        if (enemy == null) return false;
        if (discardPending && _pending.Count != 0)
        {
            int id = enemy.GlobalID;
            foreach (int key in _pending.Keys) if (key >> 8 == id) _stale.Add(key);
            foreach (int key in _stale) _pending.Remove(key);
            _stale.Clear();
        }
        if (!_placed.Remove(enemy.Pointer, out var placed)) return false;
        foreach (var assembly in placed.Entry.Content.Assemblies) ReleasePose((enemy.Pointer, assembly.Id));
        bool cracked = EnemyModelHitParts.Remove(placed.Entry.Owner, placed.Instance);
        NativeModelRuntime.RemoveCopies(placed.Entry.Owner, placed.Instance);
        return cracked;
    }

    internal void ClearWorld()
    {
        _pending = new Dictionary<int, int>();
        _placed.Clear();
        foreach (var key in _poses.Keys.ToArray()) ReleasePose(key);
        foreach (var mesh in _skins.Values) if (mesh != null) UnityEngine.Object.Destroy(mesh);
        _skins.Clear();
        foreach (var entry in _entries.Values)
        {
            EnemyModelHitParts.Remove(entry.Owner, "");
            NativeModelRuntime.RemoveCopies(entry.Owner, "");
        }
    }

    /// <summary>The host's crack decision for the limb one hit just damaged. True when a stage was crossed.</summary>
    internal bool AdvanceFromHit(Dam_EnemyDamageBase receiver, int limbID)
    {
        if (EnemyModelHitParts.Damaged(receiver, limbID) is not { } entry) return false;
        int stage = EnemyModelHitParts.Stage(entry.Limb.m_health, entry.Limb.m_healthMax, entry.CrackAt);
        if (stage <= entry.Stage) return false;
        ShowStage(entry, stage);
        return true;
    }

    /// <summary>The host's stages recomputed from its own limb health, after a restore handed it a table.</summary>
    internal bool AdvanceFromHealth()
    {
        bool changed = false;
        foreach (var entry in EnemyModelHitParts.Cracking())
        {
            int stage = EnemyModelHitParts.Stage(entry.Limb.m_health, entry.Limb.m_healthMax, entry.CrackAt);
            if (stage <= entry.Stage) continue;
            ShowStage(entry, stage);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// A client's copy of the host's full stage table. Live parts advance; rows for parts this
    /// machine has not placed yet wait for their placement. A stage never goes back: limb health never returns.
    /// </summary>
    internal void RestoreStages(IReadOnlyList<int[]> rows)
    {
        var table = new Dictionary<int, int>(rows.Count);
        foreach (var row in rows) table[StageKey((ushort)row[0], row[1])] = row[2];
        foreach (var entry in EnemyModelHitParts.Cracking())
            if (table.TryGetValue(StageKey(entry.EnemyId, entry.Ordinal), out int stage) && stage > entry.CrackAt.Length)
                throw new FormatException("enemy-model-limb-stage-range");
        foreach (var entry in EnemyModelHitParts.Cracking())
            if (table.Remove(StageKey(entry.EnemyId, entry.Ordinal), out int stage) && stage > entry.Stage)
                ShowStage(entry, stage);
        _pending = table;
    }

    internal static JsonElement CaptureStages() => JsonSerializer.SerializeToElement(EnemyModelHitParts.StageRows());

    /// <summary>The replicated table: rows of [enemy GlobalID, assembly ordinal, stage], stage 1-4.</summary>
    internal static IReadOnlyList<int[]> ReadStages(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Array) throw new FormatException("enemy-model-limb-stages");
        var rows = new List<int[]>(state.GetArrayLength());
        var keys = new HashSet<int>();
        foreach (var row in state.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 3) throw new FormatException("enemy-model-limb-stages");
            var values = new int[3];
            int index = 0;
            foreach (var value in row.EnumerateArray())
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out values[index++]))
                    throw new FormatException("enemy-model-limb-stages");
            if (values[0] is < 0 or > ushort.MaxValue || values[1] is < 0 or > 255 || values[2] is < 1 or > 4)
                throw new FormatException("enemy-model-limb-stages");
            if (!keys.Add(StageKey((ushort)values[0], values[1]))) throw new FormatException("enemy-model-limb-stages");
            rows.Add(values);
        }
        return rows;
    }

    /// <summary>Shows the host's stage on the original mesh surface without changing its pose or material.</summary>
    private static void ShowStage(EnemyModelHitParts.HitEntry entry, int stage)
    {
        if (stage <= entry.Stage) return;
        if (stage > entry.CrackAt.Length) throw new FormatException("enemy-model-limb-stage-range");
        entry.Cracks?.Show(stage);
        entry.Stage = stage;
    }

    private static int StageKey(ushort enemy, int ordinal) => enemy << 8 | ordinal;

    /// <summary>The layer of the enemy's first skinned body renderer, or null when it has none.</summary>
    private static int? BodyLayer(EnemyAgent enemy)
    {
        var model = enemy.MainModelTransform;
        if (model == null) return null;
        foreach (var renderer in model.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (renderer != null) return renderer.gameObject.layer;
        return null;
    }

    private static Transform? Host(EnemyAgent enemy, NativeModelHost host, out string? refusal)
    {
        refusal = null;
        if (host.Kind != "enemy") { refusal = "model-enemy-host-invalid"; return null; }
        var root = enemy.MainModelTransform;
        if (root == null) { refusal = "model-enemy-host-missing"; return null; }
        return NativeModelRuntime.ResolveChild(root, host.ChildPath, host.ChildIndices, out refusal);
    }

    private void Refused(uint type, string assembly, string reason)
    {
        var key = type.ToString(CultureInfo.InvariantCulture) + "\0" + assembly + "\0" + reason;
        if (!_once.ShouldReport(key)) return;
        // EnemyLog.Logs and Write never throw (a failed write is kept in LastFailure), so no guard is needed here.
        if (_log.Logs(RuntimeLogLevel.Error))
            _log.Write(RuntimeLogLevel.Error, EnemyLogCodes.ModelRefused, new RuntimeLogFacts
            {
                Values = new[]
                {
                    RuntimeLogValue.Of("enemyType", (long)type),
                    _label(type),
                    RuntimeLogValue.Of("assembly", assembly),
                    RuntimeLogValue.Of("reason", reason)
                }
            });
    }

    /// <summary>A placed part went without a crack or break presentation (the part itself is shown and hit as authored).</summary>
    private void Skipped(uint type, string assembly, string reason)
    {
        if (!_once.ShouldReport(type.ToString(CultureInfo.InvariantCulture) + "\0" + assembly + "\0skipped\0" + reason)) return;
        if (_log.Logs(RuntimeLogLevel.Error))
            _log.Write(RuntimeLogLevel.Error, EnemyLogCodes.ModelPresentationSkipped, new RuntimeLogFacts
            {
                Values = new[]
                {
                    RuntimeLogValue.Of("enemyType", (long)type),
                    _label(type),
                    RuntimeLogValue.Of("assembly", assembly),
                    RuntimeLogValue.Of("reason", reason)
                }
            });
    }

    public void Dispose()
    {
        ClearWorld();
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _entries.Values)
            if (owners.Add(entry.Owner)) NativeModelRuntime.Remove(entry.Owner);
    }

    private static string Instance(EnemyAgent enemy)
        => enemy.Pointer.ToInt64().ToString("x", CultureInfo.InvariantCulture) + ":";

    private sealed record Entry(string Owner, NativeModelContent Content);
}

/// <summary>
/// Native damage limbs owned by model attachments: one `Dam_EnemyDamageLimb_Custom` per placed assembly with a hit
/// role, on the placement root beside its mesh collider (the model's own simplified surface). A bullet resolves its receiver with
/// `GetComponent&lt;IDamageable&gt;` on the hit collider's own GameObject (BulletWeapon.BulletHit, no parent walk), so the
/// collider and the limb share that object. Every row is removed with its enemy life.
/// </summary>
internal static class EnemyModelHitParts
{
    // The packet limb index is one byte, and ReceiveBulletDamage only applies a hit whose index is above zero.
    private const int MaximumNativeLimbs = 256;
    private static readonly Dictionary<IntPtr, HitEntry> Hits = new();
    private static readonly Dictionary<string, List<HitEntry>> Instances = new(StringComparer.Ordinal);
    private static readonly Dictionary<ushort, List<HitEntry>> ByEnemy = new();
    private static readonly List<HitEntry> Cracked = new();

    /// <summary>Live parts with authored crack stages; zero lets the damage hook return at once.</summary>
    internal static int CrackCount { get; private set; }

    /// <summary>Adds the part's limb. A crack layer or break burst that cannot be made leaves the part shown and hit as authored
    /// without that presentation; its reason goes to <paramref name="skipped"/>. <paramref name="draws"/> are the placement's
    /// imported models, which draw batched (no renderer below <paramref name="root"/>) on <paramref name="drawLayer"/>.</summary>
    internal static string? Add(string owner, string instance, int ordinal, EnemyAgent enemy,
        NativeModelAssembly assembly, Transform host, GameObject root, Func<GameObject?> breakEffect,
        IReadOnlyList<InstancedDraw> draws, int? drawLayer, List<string> skipped, out HitEntry? entry)
    {
        entry = null;
        var hit = assembly.Hit;
        if (hit == null) return null;
        var damage = enemy.Damage;
        if (damage == null) return "model-hit-receiver-missing";
        var current = damage.DamageLimbs ?? Array.Empty<Dam_EnemyDamageLimb>();
        if (current.Length >= MaximumNativeLimbs) return "model-hit-limb-budget";
        int id = current.Length;
        if (id == 0) return "model-hit-limb-index-zero";
        if (ordinal > 255) return "model-hit-ordinal-budget";
        var original = HostLimb(current, host);
        // A breakable part needs a health to run out of: its own, or the part it sits on.
        if (hit.Health == null && original == null && hit.Breakable) return "model-hit-host-limb-unresolved";
        if (assembly.HitMesh is not { } hitMesh) return "model-hit-mesh-missing";
        GameObject? burst = null;
        if (hit.Breakable)
        {
            burst = breakEffect();
            if (burst == null) skipped.Add("model-hit-destruction-effect-missing");
        }
        if (!AddCollider(root, hitMesh)) return "model-hit-collider-create";
        // The placement inherited its host's layer; a bullet only reaches colliders on the enemy-damageable layer.
        root.layer = LayerManager.LAYER_ENEMY_DAMAGABLE;
        var limb = root.AddComponent<Dam_EnemyDamageLimb_Custom>();
        if (limb == null || limb.Pointer == IntPtr.Zero) return "model-hit-limb-create";
        limb.SetBaseDamagable(damage);
        limb.m_limbID = id;
        var type = hit.Type switch
        {
            "armor" => eLimbDamageType.Armor,
            "weakpoint" => eLimbDamageType.Weakspot,
            _ => eLimbDamageType.Normal
        };
        limb.SetLimbDamageType(type);
        limb.Setup();
        if (original != null && original.m_type == type)
        {
            limb.m_weakspotDamageMulti = original.m_weakspotDamageMulti;
            limb.m_armorDamageMulti = original.m_armorDamageMulti;
        }
        if (hit.Multiplier is float multiplier)
        {
            if (type == eLimbDamageType.Weakspot) limb.m_weakspotDamageMulti = multiplier;
            else limb.m_armorDamageMulti = multiplier;
        }
        if (hit.Health is float health)
        {
            limb.m_healthMax = health;
            limb.m_health = health;
        }
        else if (original != null)
        {
            limb.m_healthMax = original.m_healthMax;
            limb.m_health = original.m_health;
        }
        else
        {
            // An unbreakable part on no single vanilla part never runs out; the enemy's own health is the bound.
            limb.m_healthMax = damage.HealthMax;
            limb.m_health = damage.HealthMax;
        }
        CD_TriggerEffect? effect = null;
        if (burst != null)
        {
            effect = root.AddComponent<CD_TriggerEffect>();
            if (effect == null || effect.Pointer == IntPtr.Zero) skipped.Add("model-hit-destruction-effect-create");
            else
            {
                effect.m_linkedEffect = burst;
                effect.m_atTransform = root.transform;
                effect.m_linkToTransform = false;
                limb.m_effectOnDestroy = effect;
            }
        }
        string? crackRefusal = null;
        var cracks = hit.CrackAt.Count == 0 ? null : EnemyModelCracks.Create(root, draws, drawLayer, out crackRefusal);
        if (crackRefusal != null) skipped.Add(crackRefusal);
        var expanded = new Dam_EnemyDamageLimb[current.Length + 1];
        Array.Copy(current, expanded, current.Length);
        expanded[id] = limb;
        damage.DamageLimbs = expanded;
        string key = owner + "\0" + instance;
        var crackAt = new float[hit.CrackAt.Count];
        for (int index = 0; index < crackAt.Length; index++) crackAt[index] = hit.CrackAt[index];
        entry = new HitEntry(key, limb, damage, ordinal, enemy.GlobalID, crackAt, hit.Breakable, cracks)
        { HostLimbId = original?.m_limbID ?? -1 };
        Register(entry);
        return null;
    }

    /// <summary>The hit role of an assembly whose visual could not be placed: one inert limb (no collider, never
    /// targeted, never destroyed) appended in authored order so the limb indices after it keep their meaning on every
    /// machine. It is removed with the enemy life like any other row.</summary>
    internal static string? Reserve(string owner, string instance, int ordinal, EnemyAgent enemy,
        NativeModelAssembly assembly, Transform? anchor)
    {
        var hit = assembly.Hit;
        if (hit == null) return null;
        var damage = enemy.Damage;
        if (damage == null) return "model-hit-receiver-missing";
        var current = damage.DamageLimbs ?? Array.Empty<Dam_EnemyDamageLimb>();
        if (current.Length >= MaximumNativeLimbs) return "model-hit-limb-budget";
        int id = current.Length;
        if (id == 0) return "model-hit-limb-index-zero";
        if (ordinal > 255) return "model-hit-ordinal-budget";
        if (anchor == null) return "model-enemy-host-missing";
        var root = new GameObject("ForgeModel.HitReserved." + assembly.Id);
        root.SetActive(false);
        root.transform.SetParent(anchor, false);
        var limb = root.AddComponent<Dam_EnemyDamageLimb_Custom>();
        if (limb == null || limb.Pointer == IntPtr.Zero) { UnityEngine.Object.Destroy(root); return "model-hit-limb-create"; }
        limb.SetBaseDamagable(damage);
        limb.m_limbID = id;
        limb.SetLimbDamageType(eLimbDamageType.Normal);
        float health = hit.Health ?? 1f;
        limb.m_healthMax = health;
        limb.m_health = health;
        var expanded = new Dam_EnemyDamageLimb[current.Length + 1];
        Array.Copy(current, expanded, current.Length);
        expanded[id] = limb;
        damage.DamageLimbs = expanded;
        string key = owner + "\0" + instance;
        var entry = new HitEntry(key, limb, damage, ordinal, enemy.GlobalID, Array.Empty<float>(), false, null) { Reserved = root };
        Register(entry);
        return null;
    }

    private static void Register(HitEntry entry)
    {
        Hits.Add(entry.Limb.Pointer, entry);
        if (!Instances.TryGetValue(entry.Key, out var rows)) Instances.Add(entry.Key, rows = new List<HitEntry>());
        rows.Add(entry);
        if (!ByEnemy.TryGetValue(entry.EnemyId, out var own)) ByEnemy.Add(entry.EnemyId, own = new List<HitEntry>());
        own.Add(entry);
        if (entry.CrackAt.Length != 0) { Cracked.Add(entry); CrackCount++; }
    }

    /// <summary>Use the native limb on the anchor's ancestor chain, or its sole descendant limb. A whole model
    /// containing several limbs is ambiguous; an authored health is required there rather than guessing a part.</summary>
    private static Dam_EnemyDamageLimb? HostLimb(IEnumerable<Dam_EnemyDamageLimb> limbs, Transform host)
    {
        for (var node = host; node != null; node = node.parent)
            foreach (var limb in limbs)
                if (limb != null && !Hits.ContainsKey(limb.Pointer) && limb.transform == node) return limb;
        Dam_EnemyDamageLimb? found = null;
        foreach (var limb in limbs)
        {
            if (limb == null || Hits.ContainsKey(limb.Pointer)) continue;
            for (var node = limb.transform.parent; node != null; node = node.parent)
                if (node == host)
                {
                    if (found != null) return null;
                    found = limb;
                    break;
                }
        }
        return found;
    }

    /// <summary>One Unity mesh per compiled hit mesh, shared by every enemy of the type (built once, never simplified here).</summary>
    private static readonly Dictionary<NativeModelHitMesh, Mesh> HitMeshes = new(ReferenceEqualityComparer.Instance);

    /// <summary>The model's own simplified surface as a non-convex MeshCollider on the placement root, in the root's own
    /// frame (the placement's scale applies to it with the model). Raycasts (bullets, sentries), overlap queries
    /// (explosions, Forge range queries) and melee sweeps all resolve against a static non-convex mesh; the placement root
    /// carries no Rigidbody of its own.</summary>
    private static bool AddCollider(GameObject root, NativeModelHitMesh hitMesh)
    {
        if (!HitMeshes.TryGetValue(hitMesh, out var mesh) || mesh == null)
        {
            var vertices = new Vector3[hitMesh.Positions.Length / 3];
            for (int at = 0; at < vertices.Length; at++)
                vertices[at] = new Vector3(hitMesh.Positions[3 * at], hitMesh.Positions[3 * at + 1], hitMesh.Positions[3 * at + 2]);
            mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.triangles = hitMesh.Indices;
            HitMeshes[hitMesh] = mesh;
        }
        var collider = root.AddComponent<MeshCollider>();
        if (collider == null || collider.Pointer == IntPtr.Zero) return false;
        collider.convex = false;
        collider.sharedMesh = mesh;
        return true;
    }

    /// <summary>How many authored thresholds (remaining-health percentages, strictly decreasing) the limb's
    /// remaining health has reached. A limb without a positive maximum has no stage.</summary>
    internal static int Stage(float health, float healthMax, float[] crackAt)
    {
        if (!(healthMax > 0f)) return 0;
        float percent = health / healthMax * 100f;
        int stage = 0;
        while (stage < crackAt.Length && percent <= crackAt[stage]) stage++;
        return stage;
    }

    /// <summary>Whether any hit part is placed; false lets the destruction hook return before it reads the limb.</summary>
    internal static bool HasParts => Hits.Count != 0;

    /// <summary>The native destruction of a breakable hit part: the part stops being a target, so the next shot at the
    /// same place reaches the limb behind it at that limb's own multiplier, and its model is gone. The placement root
    /// carries the part's collider, limb and model together, so deactivating it removes all three. Every machine runs
    /// the destruction on its own copy of the enemy.</summary>
    internal static void Broke(Dam_EnemyDamageLimb limb)
    {
        if (Hits.Count == 0 || limb == null) return;
        if (!Hits.TryGetValue(limb.Pointer, out var entry))
        {
            HostBroke(limb);
            return;
        }
        if (!entry.Breakable || entry.Broken) return;
        entry.Broken = true;
        var host = limb.gameObject;
        if (host != null) host.SetActive(false);
        GiveBackWhenUnhittable(entry);
    }

    /// <summary>The vanilla colliders switched off for a removed or replaced part, per enemy: when the last placed part
    /// breaks and nothing else can be hit, they go back on, so an enemy never ends unkillable.</summary>
    private sealed record HiddenParts(List<Collider> Colliders, Func<bool> AnyHittable, Action<int> GivenBack);
    private static readonly Dictionary<ushort, HiddenParts> Hidden = new();
    /// <summary>`anyHittable` asks whether some vanilla or placed part can still be hit; `givenBack` reports the colliders
    /// switched back on, both supplied by the module that knows the enemy.</summary>
    internal static void KeepHidden(ushort enemyId, List<Collider> colliders, Func<bool> anyHittable, Action<int> givenBack)
        => Hidden[enemyId] = new HiddenParts(colliders, anyHittable, givenBack);
    internal static void ForgetHidden(ushort enemyId) => Hidden.Remove(enemyId);
    internal static void ClearHidden() => Hidden.Clear();

    private static void GiveBackWhenUnhittable(HitEntry entry)
    {
        if (!Hidden.TryGetValue(entry.EnemyId, out var hidden) || hidden.AnyHittable()) return;
        Hidden.Remove(entry.EnemyId);
        foreach (var collider in hidden.Colliders) if (collider != null) collider.enabled = true;
        hidden.GivenBack(hidden.Colliders.Count);
    }

    /// <summary>The enemy is dying (before `EnemyAgent.OnDead` runs): its hit parts' colliders go off, the model stays drawn on the
    /// corpse. The ragdoll turns the limb rigidbodies non-kinematic, and a non-convex MeshCollider under a non-kinematic Rigidbody
    /// is unsupported in Unity (an error, then ignored). A dead part has no life left to damage, and the game keeps its corpses on
    /// their own layer (`LAYER_ENEMY_DEAD`, apart from `LAYER_ENEMY_DAMAGABLE`), so the part's collider has nothing left to do.</summary>
    internal static void Died(EnemyAgent enemy)
    {
        if (Hits.Count == 0 || enemy == null) return;
        if (!ByEnemy.TryGetValue(enemy.GlobalID, out var own)) return;
        foreach (var entry in own)
        {
            if (entry.Reserved != null || entry.Limb.gameObject is not { } root) continue;
            var collider = root.GetComponent<MeshCollider>();
            if (collider != null) collider.enabled = false;
        }
    }

    /// <summary>A vanilla part broke: the hit parts that sit on it go with it, model and collider together (seen means
    /// hittable), on every machine that runs the destruction.</summary>
    private static void HostBroke(Dam_EnemyDamageLimb limb)
    {
        List<HitEntry>? gone = null;
        foreach (var entry in Hits.Values)
        {
            if (entry.Broken || entry.Reserved != null || entry.HostLimbId < 0) continue;
            var limbs = entry.Damage.DamageLimbs;
            if (limbs == null || entry.HostLimbId >= limbs.Length || limbs[entry.HostLimbId] is not { } host
                || host.Pointer != limb.Pointer) continue;
            (gone ??= new List<HitEntry>()).Add(entry);
        }
        if (gone == null) return;
        foreach (var entry in gone)
        {
            entry.Broken = true;
            var root = entry.Limb.gameObject;
            if (root != null) root.SetActive(false);
            GiveBackWhenUnhittable(entry);
        }
    }

    internal static bool BlocksDestruction(Dam_EnemyDamageLimb limb)
        => limb != null && Hits.TryGetValue(limb.Pointer, out var entry) && !entry.Breakable;

    /// <summary>The cracking part one hit damaged: the array position the receiver was handed, or null.</summary>
    internal static HitEntry? Damaged(Dam_EnemyDamageBase receiver, int limbID)
    {
        if (CrackCount == 0 || receiver == null) return null;
        var limbs = receiver.DamageLimbs;
        if (limbs == null || (uint)limbID >= (uint)limbs.Length) return null;
        var limb = limbs[limbID];
        return limb != null && Hits.TryGetValue(limb.Pointer, out var entry) && entry.CrackAt.Length != 0 ? entry : null;
    }

    internal static IEnumerable<HitEntry> Cracking()
    {
        for (int index = 0; index < Cracked.Count; index++) yield return Cracked[index];
    }

    internal static int[][] StageRows()
    {
        var rows = new List<int[]>();
        foreach (var entry in Hits.Values)
            if (entry.Stage > 0) rows.Add(new[] { (int)entry.EnemyId, entry.Ordinal, entry.Stage });
        rows.Sort((left, right) => left[0] != right[0] ? left[0].CompareTo(right[0]) : left[1].CompareTo(right[1]));
        return rows.ToArray();
    }

    internal static void Rollback(HitEntry? entry)
    {
        if (entry != null && Hits.ContainsKey(entry.Limb.Pointer)) Remove(entry);
    }

    /// <summary>Removes the rows of one exact <paramref name="instance"/> of <paramref name="owner"/>, or of every instance when it is
    /// empty. True when one of them showed a crack stage.</summary>
    internal static bool Remove(string owner, string instance)
    {
        string prefix = owner + "\0" + instance;
        if (instance.Length != 0) return Instances.TryGetValue(prefix, out var own) && RemoveRows(own);
        bool cracked = false;
        foreach (var key in new List<string>(Instances.Keys))
            if (key.StartsWith(prefix, StringComparison.Ordinal)) cracked |= RemoveRows(Instances[key]);
        return cracked;
    }

    private static bool RemoveRows(List<HitEntry> rows)
    {
        bool cracked = false;
        for (int index = rows.Count - 1; index >= 0; index--) cracked |= Remove(rows[index]);
        return cracked;
    }

    private static bool Remove(HitEntry entry)
    {
        entry.Cracks?.Dispose();
        if (entry.Reserved != null) UnityEngine.Object.Destroy(entry.Reserved);
        Hits.Remove(entry.Limb.Pointer);
        if (entry.CrackAt.Length != 0) { Cracked.Remove(entry); CrackCount--; }
        if (ByEnemy.TryGetValue(entry.EnemyId, out var own))
        {
            own.Remove(entry);
            if (own.Count == 0) ByEnemy.Remove(entry.EnemyId);
        }
        if (Instances.TryGetValue(entry.Key, out var rows))
        {
            rows.Remove(entry);
            if (rows.Count == 0) Instances.Remove(entry.Key);
        }
        var current = entry.Damage.DamageLimbs;
        if (current != null)
        {
            var kept = new List<Dam_EnemyDamageLimb>(current.Length);
            foreach (var limb in current)
                if (limb != null && limb.Pointer != entry.Limb.Pointer) kept.Add(limb);
            for (int index = 0; index < kept.Count; index++) kept[index].m_limbID = index;
            entry.Damage.DamageLimbs = kept.ToArray();
        }
        entry.Damage.DamageLimbsWithDestruction?.Remove(entry.Limb);
        return entry.Stage > 0;
    }

    /// <summary>One placed hit part: its native limb, the placement it lives on and the stage it shows.</summary>
    internal sealed class HitEntry
    {
        internal HitEntry(string key, Dam_EnemyDamageLimb_Custom limb, Dam_EnemyDamageBase damage,
            int ordinal, ushort enemyId, float[] crackAt, bool breakable, EnemyModelCracks? cracks)
        {
            Key = key; Limb = limb; Damage = damage; Ordinal = ordinal; EnemyId = enemyId;
            CrackAt = crackAt; Breakable = breakable; Cracks = cracks;
        }
        internal string Key { get; }
        internal Dam_EnemyDamageLimb_Custom Limb { get; }
        internal Dam_EnemyDamageBase Damage { get; }
        internal int Ordinal { get; }
        internal ushort EnemyId { get; }
        internal float[] CrackAt { get; }
        internal bool Breakable { get; }
        internal EnemyModelCracks? Cracks { get; }
        internal int Stage { get; set; }
        /// <summary>The native limb this part sits in front of (its array position), or -1 when it sits on none.</summary>
        internal int HostLimbId { get; init; } = -1;
        internal bool Broken { get; set; }
        /// <summary>The inert placeholder object of a part whose visual was refused, or null for a placed part.</summary>
        internal GameObject? Reserved { get; init; }
    }
}

/// <summary>A transparent crack layer on the exact static or animated mesh, sharing its geometry and bones.
/// Only the small UV mask and its material are owned; neither the model's material nor its pose changes. An imported
/// model draws batched without a renderer, so its layer is the same mesh drawn batched with the crack material below an
/// inactive anchor that a shown stage switches on.</summary>
internal sealed class EnemyModelCracks : IDisposable
{
    private const int Size = 64;
    private readonly List<Renderer> _layers = new();
    private GameObject? _batched;
    private Material? _material;
    private Texture2D? _texture;

    internal static EnemyModelCracks? Create(GameObject root, IReadOnlyList<InstancedDraw> draws, int? drawLayer, out string? refusal)
    {
        refusal = null;
        var cracks = new EnemyModelCracks();
        try
        {
            var shader = ForgeVisual.Shaders.Find("Sprites/Default");
            if (shader == null) { refusal = "model-crack-shader-missing"; return null; }
            cracks._texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { name = "ForgeModel.Cracks" };
            cracks._material = new Material(shader) { name = "ForgeModel.Cracks", renderQueue = 3001 };
            cracks._material.SetTexture("_MainTex", cracks._texture);
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter == null || filter.sharedMesh is not { } mesh || mesh == null) continue;
                var source = filter.GetComponent<MeshRenderer>();
                if (source == null || !source.enabled || Invisible(source)) continue;
                if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) { refusal = "model-crack-uv-missing"; break; }
                var child = cracks.Child(source.transform);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                cracks.Add(child.AddComponent<MeshRenderer>(), mesh.subMeshCount, source);
            }
            if (refusal == null)
                foreach (var source in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (source == null || !source.enabled || Invisible(source) || source.sharedMesh is not { } mesh || mesh == null) continue;
                    if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) { refusal = "model-crack-uv-missing"; break; }
                    var layer = cracks.Child(source.transform).AddComponent<SkinnedMeshRenderer>();
                    layer.sharedMesh = mesh; layer.bones = source.bones; layer.rootBone = source.rootBone;
                    layer.localBounds = source.localBounds;
                    cracks.Add(layer, mesh.subMeshCount, source);
                }
            if (refusal == null && draws.Count != 0)
            {
                var layered = new List<InstancedDraw>(draws.Count);
                foreach (var draw in draws)
                {
                    if (draw.Mesh == null || draw.ShadowOnly?.All(only => only) == true) continue;
                    if (!draw.Mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) { refusal = "model-crack-uv-missing"; break; }
                    var materials = new Material?[draw.Materials.Length];
                    for (int slot = 0; slot < materials.Length; slot++)
                        materials[slot] = draw.ShadowOnly?[slot] == true ? null : cracks._material;
                    layered.Add(new InstancedDraw(draw.Mesh, materials, draw.Local));
                }
                if (refusal == null && layered.Count != 0)
                {
                    cracks._batched = cracks.Child(root.transform);
                    cracks._batched.SetActive(false);
                    InstancedMeshBatches.Register(cracks._batched.transform, layered, drawLayer, instanced: false);
                }
            }
            if (refusal == null && cracks._layers.Count == 0 && cracks._batched == null) refusal = "model-crack-mesh-missing";
            if (refusal == null) return cracks;
            cracks.Dispose();
            return null;
        }
        catch
        {
            cracks.Dispose();
            throw;
        }
    }

    /// <summary>An invisible surface (only its shadow drawn) gets no crack layer: a crack would float over a body nobody sees.</summary>
    private static bool Invisible(Renderer source) => source.shadowCastingMode == ShadowCastingMode.ShadowsOnly;

    private GameObject Child(Transform parent)
    {
        var child = new GameObject("ForgeModel.Cracks") { layer = parent.gameObject.layer };
        child.transform.SetParent(parent, false);
        return child;
    }

    private void Add(Renderer layer, int slots, Renderer source)
    {
        _layers.Add(layer);
        var materials = new Material[slots];
        for (int index = 0; index < slots; index++) materials[index] = _material!;
        layer.sharedMaterials = materials;
        layer.enabled = false;
    }

    internal void Show(int stage)
    {
        var pixels = new Color[Size * Size];
        void Line(int x, int y, int toX, int toY)
        {
            int dx = Math.Abs(toX - x), dy = -Math.Abs(toY - y), sx = x < toX ? 1 : -1, sy = y < toY ? 1 : -1;
            int error = dx + dy;
            while (true)
            {
                for (int oy = -1; oy <= 1; oy++) for (int ox = -1; ox <= 1; ox++)
                {
                    int px = x + ox, py = y + oy;
                    if (px < 0 || px >= Size || py < 0 || py >= Size) continue;
                    int pixel = py * Size + px;
                    if (pixels[pixel].a == 0f) pixels[pixel] = new Color(0.7f, 0.7f, 0.7f, 1f);
                }
                pixels[y * Size + x] = new Color(0f, 0f, 0f, 1f);
                if (x == toX && y == toY) break;
                int twice = error * 2;
                if (twice >= dy) { error += dy; x += sx; }
                if (twice <= dx) { error += dx; y += sy; }
            }
        }
        for (int branch = 0; branch < stage; branch++)
        {
            int x = 9 + branch * 13, y = 3;
            for (int step = 1; step <= 7; step++)
            {
                int nextX = 9 + branch * 13 + ((step & 1) == 0 ? -5 : 5), nextY = 3 + step * 8;
                Line(x, y, nextX, nextY);
                if ((step & 1) == 0) Line(nextX, nextY, nextX + 6, nextY - 6);
                x = nextX; y = nextY;
            }
        }
        _texture!.SetPixels(pixels);
        _texture.Apply(false, false);
        foreach (var layer in _layers) if (layer != null) layer.enabled = true;
        if (_batched != null) _batched.SetActive(true);
    }

    public void Dispose()
    {
        foreach (var layer in _layers) if (layer != null) UnityEngine.Object.DestroyImmediate(layer.gameObject);
        _layers.Clear();
        if (_batched != null) UnityEngine.Object.DestroyImmediate(_batched);
        _batched = null;
        if (_material != null) UnityEngine.Object.DestroyImmediate(_material);
        if (_texture != null) UnityEngine.Object.DestroyImmediate(_texture);
        _material = null; _texture = null;
    }
}

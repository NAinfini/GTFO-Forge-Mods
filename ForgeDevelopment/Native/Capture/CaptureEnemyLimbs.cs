using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Enemies;
using GameData;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The enemy limb inventory. It answers, for every loaded enemy type, what the prefab the running game built for it
/// carries: the model root's skinned renderers with their bone sets and bind poses, and the native
/// <c>Dam_EnemyDamageLimb</c> components with the position, bone, type, multipliers, destruction answer and collider
/// each one has. The tumor pairing the contract's <c>tumor1</c>–<c>tumor8</c> numbering depends on is computed from
/// those raw facts at the end of each entry.
///
/// The prefabs come from <c>Enemies.EnemyPrefabManager</c>: one entry per loaded <c>GameData.EnemyDataBlock</c>, with
/// the prefab fetched through the manager's own <c>GetEnemyPrefab(uint)</c> and the manager's built-prefab table
/// counted, so a type the game has not built says so instead of being silently absent. Nothing here changes a game
/// object, installs a hook or runs per frame; every read is guarded and a read that throws is recorded in the
/// document's <c>errors</c> array and the rest of the inventory continues.
///
/// Values the game writes only after setup are reported as they are on the prefab: a limb whose <c>m_base</c> is null
/// has not been set up, so its spawn-time health is written as null with the reason, never guessed.
/// </summary>
internal static class CaptureEnemyLimbs
{
    internal const string FileName = "enemy-limbs.json";
    internal const int SchemaVersion = 1;


    /// <summary>The member the prefabs are taken from. It is named in the document so a reader can tell which
    /// native accessor produced the inventory.</summary>
    private const string PrefabSource = "Enemies.EnemyPrefabManager.GetEnemyPrefab(uint) over EnemyPrefabManager.m_enemyPrefabs";

    /// <summary>The renderer name prefix the contract's tumor pairing numbers. The match is the same ordinal
    /// <c>StartsWith</c> a reader of the document can repeat.</summary>
    private const string EggSackPrefix = "EggSack";

    private static readonly CaptureInventoryDocument Document = new(FileName, "gtfo-forge-enemy-limb-inventory", SchemaVersion);

    /// <summary>
    /// Takes the inventory at the level start, the same once-per-level boundary (<c>FactoryDone</c>) the appearance
    /// inventory uses: that is the point the game's enemy prefabs and their damage limbs exist at. The document is
    /// written only when the result differs from the last one.
    /// </summary>
    internal static IEnumerable<int> SnapshotSlices(string reason, long tick)
    {
        if (reason != CaptureReason.LevelGenerated) yield break;
        var started = Stopwatch.GetTimestamp();
        var inventory = new Inventory();
        foreach (var step in Collect(inventory)) yield return step;
        Document.Write(inventory, reason, tick, started, json => WriteBody(json, inventory), inventory.Errors);
    }

    private static IEnumerable<int> Collect(Inventory inventory)
    {
        EnemyPrefabManager? manager = null;
        if (Read(inventory, "EnemyPrefabManager.Current", () => EnemyPrefabManager.Current, out var current)) manager = current;
        inventory.PrefabManagerAvailable = manager != null;
        if (manager == null)
        {
            inventory.Notes.Add("EnemyPrefabManager.Current is null: the prefab manager is not available, so no built prefab could be read");
            yield break;
        }
        if (Read(inventory, "EnemyPrefabManager.m_enemyPrefabs", () => manager.m_enemyPrefabs, out var prefabs) && prefabs != null)
        {
            if (Read(inventory, "EnemyPrefabManager.m_enemyPrefabs.Count", () => prefabs.Count, out var count)) inventory.PrefabCount = count;
        }
        else inventory.Errors.Add(new CaptureInventoryError("EnemyPrefabManager.m_enemyPrefabs", "unavailable"));

        var blocks = Blocks(inventory);
        inventory.EnemyBlockCount = blocks.Count;
        foreach (var block in blocks)
        {
            yield return 0;
            var row = new EnemyRow(block.persistentID, block.name ?? "");
            inventory.Enemies.Add(row);
            using var cursor = ReadEnemy(inventory, block, row).GetEnumerator();
            while (true)
            {
                bool more;
                try { more = cursor.MoveNext(); }
                catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError("enemy-block", error.GetType().Name)); break; }
                if (!more) break;
                yield return 0;
            }
        }
    }

    /// <summary>Every loaded enemy block, ordered by persistent id so the document is stable across runs. The block
    /// table is the game's own; the prefab each one answers is the manager's, not a second lookup.</summary>
    private static List<EnemyDataBlock> Blocks(Inventory inventory)
    {
        var blocks = new List<EnemyDataBlock>();
        if (!Read(inventory, "GameDataBlockBase<EnemyDataBlock>.GetAllBlocks", () => GameDataBlockBase<EnemyDataBlock>.GetAllBlocks(), out var all)
            || all == null) return blocks;
        try
        {
            foreach (var block in all) if (block != null) blocks.Add(block);
        }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError("EnemyDataBlock", error.GetType().Name)); }
        blocks.Sort((left, right) => left.persistentID.CompareTo(right.persistentID));
        return blocks;
    }

    private static IEnumerable<int> ReadEnemy(Inventory inventory, EnemyDataBlock block, EnemyRow row)
    {
        Read(inventory, "enemy.internalEnabled", () => block.internalEnabled, out var enabled);
        row.InternalEnabled = enabled;
        Read(inventory, "enemy.balancingDataId", () => block.BalancingDataId, out var balancingId);
        row.BalancingDataId = balancingId;
        // The back damage rule lives on the balancing block the enemy names, read as data rather than from a spawned
        // agent: this inventory runs before any spawn.
        if (balancingId != 0)
        {
            if (Read(inventory, "EnemyBalancingDataBlock.GetBlock(" + balancingId.ToString(CultureInfo.InvariantCulture) + ")",
                () => GameDataBlockBase<EnemyBalancingDataBlock>.GetBlock(balancingId), out var balancing) && balancing != null)
            {
                if (Read(inventory, "EnemyBalancingDataBlock.AllowDamgeBonusFromBehind", () => balancing.AllowDamgeBonusFromBehind, out var bonus))
                    row.AllowDamageBonusFromBehind = bonus;
            }
            else inventory.Errors.Add(new CaptureInventoryError("balancing:" + balancingId.ToString(CultureInfo.InvariantCulture), "block-not-found"));
        }
        else inventory.Errors.Add(new CaptureInventoryError("enemy:" + row.EnemyType.ToString(CultureInfo.InvariantCulture), "no-balancing-data-id"));

        GameObject? prefab = null;
        if (Read(inventory, "EnemyPrefabManager.GetEnemyPrefab(" + row.EnemyType.ToString(CultureInfo.InvariantCulture) + ")",
            () => EnemyPrefabManager.GetEnemyPrefab(row.EnemyType), out var built)) prefab = built;
        row.PrefabBuilt = prefab != null;
        if (prefab == null)
        {
            row.Notes.Add("the prefab manager returned no prefab for this type; its limbs are not in this document");
            yield break;
        }
        if (Read(inventory, "prefab.name", () => prefab.name ?? "", out var prefabName)) row.PrefabName = prefabName;

        Transform? model = null;
        if (Read(inventory, "prefab.EnemyAgent.MainModelTransform", () => prefab.GetComponent<EnemyAgent>()?.MainModelTransform, out var transform)) model = transform;
        if (model == null)
        {
            row.Notes.Add("the prefab has no EnemyAgent.MainModelTransform; there is no model root to measure against");
            yield break;
        }
        row.ModelPath = RuntimeDiagnostics.PathOf(model);

        var renderers = new List<RendererRow>();
        foreach (var step in Renderers(inventory, model, renderers)) yield return step;
        row.Renderers.AddRange(renderers);

        var boneNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var renderer in renderers)
            foreach (var bone in renderer.Bones) boneNames.Add(bone);

        Dam_EnemyDamageBase? damage = null;
        if (Read(inventory, "prefab.Dam_EnemyDamageBase", () => prefab.GetComponentInChildren<Dam_EnemyDamageBase>(true), out var receiver)) damage = receiver;
        row.DamageBaseFound = damage != null;
        if (damage == null)
        {
            row.Notes.Add("the prefab has no Dam_EnemyDamageBase; it carries no native limbs");
            yield break;
        }

        if (!Read(inventory, "Dam_EnemyDamageBase.DamageLimbs", () => damage.DamageLimbs, out var limbs) || limbs == null)
        {
            row.Notes.Add("Dam_EnemyDamageBase.DamageLimbs is null");
            yield break;
        }
        for (var index = 0; index < limbs.Length; index++)
        {
            yield return 0;
            Dam_EnemyDamageLimb? limb = null;
            if (!Read(inventory, "DamageLimbs[" + index.ToString(CultureInfo.InvariantCulture) + "]", () => limbs[index], out var entry) || entry == null) continue;
            limb = entry;
            row.Limbs.Add(ReadLimb(inventory, index, limb, model, boneNames));
        }
        PairTumors(row);
        yield break;
    }

    private static IEnumerable<int> Renderers(Inventory inventory, Transform model, List<RendererRow> rows)
    {
        Renderer[]? renderers = null;
        if (!Read(inventory, "MainModelTransform.Renderers", () => model.gameObject.GetComponentsInChildren<Renderer>(true), out renderers)
            || renderers == null) yield break;
        foreach (var renderer in renderers)
        {
            yield return 0;
            if (renderer == null) continue;
            var skinned = renderer.TryCast<SkinnedMeshRenderer>();
            if (skinned == null && renderer.TryCast<MeshRenderer>() == null) continue;
            var row = new RendererRow();
            if (Read(inventory, "renderer.name", () => renderer.gameObject.name ?? "", out var name)) row.Name = name;
            row.Object = RuntimeDiagnostics.PathOf(renderer.transform);
            if (Read(inventory, "renderer.matrix", () => model.worldToLocalMatrix * renderer.transform.localToWorldMatrix, out var matrix))
            {
                row.Matrix = matrix;
                row.HasMatrix = true;
            }
            // Rigid meshes have no skeleton. Limb paths and positions still come from each native limb's transform.
            if (skinned != null)
            {
                if (Read(inventory, "renderer.rootBone", () => skinned.rootBone, out var rootBone) && rootBone != null)
                {
                    if (Read(inventory, "renderer.rootBone.name", () => rootBone.name ?? "", out var rootBoneName)) row.RootBone = rootBoneName;
                    row.RootBonePosition = Local(inventory, model, rootBone.position, "renderer.rootBone.position");
                }
                if (Read(inventory, "renderer.bones", () => skinned.bones, out var bones) && bones != null)
                    for (var index = 0; index < bones.Length; index++)
                    {
                        var bone = bones[index];
                        row.Bones.Add(bone == null ? "" : bone.name ?? "");
                    }
                if (Read(inventory, "renderer.sharedMesh", () => skinned.sharedMesh, out var mesh) && mesh != null
                    && Read(inventory, "mesh.bindposes", () => mesh.bindposes, out var bindposes) && bindposes != null)
                    row.Bindposes = bindposes.Length;
            }
            rows.Add(row);
        }
        yield break;
    }

    private static LimbRow ReadLimb(Inventory inventory, int index, Dam_EnemyDamageLimb limb, Transform model, HashSet<string> boneNames)
    {
        var row = new LimbRow(index);
        Read(inventory, "limb.m_limbID", () => limb.m_limbID, out var limbId);
        row.LimbId = limbId;
        if (Read(inventory, "limb.DebugName", () => limb.DebugName ?? "", out var debugName)) row.DebugName = debugName;
        row.Custom = limb is Dam_EnemyDamageLimb_Custom;
        if (Read(inventory, "limb.gameObject.name", () => limb.gameObject.name ?? "", out var objectName)) row.ObjectName = objectName;
        row.Path = RelativePath(model, limb.transform);
        row.Bone = NearestBone(limb.transform, boneNames, out var found);
        row.BoneFound = found;
        row.Position = Local(inventory, model, limb.transform.position, "limb.position");
        if (Read(inventory, "limb.m_type", () => limb.m_type.ToString(), out var type)) row.Type = type;
        // m_base is assigned by SetBaseDamagable when the limb is set up. A prefab whose limb has none has not run
        // that step, so the fields Setup writes (health, health max) carry no value here and are reported as null.
        row.BaseAssigned = Read(inventory, "limb.m_base", () => limb.m_base, out var assigned) && assigned != null;
        if (row.BaseAssigned)
        {
            if (Read(inventory, "limb.m_health", () => limb.m_health, out var health)) row.Health = health;
            if (Read(inventory, "limb.m_healthMax", () => limb.m_healthMax, out var healthMax)) row.HealthMax = healthMax;
        }
        else row.Notes.Add("m_health/m_healthMax are null: this limb's m_base is null on the prefab, so Dam_EnemyDamageLimb.Setup has not written them");
        if (Read(inventory, "limb.m_weakspotDamageMulti", () => limb.m_weakspotDamageMulti, out var weakspot)) row.WeakspotMulti = weakspot;
        if (Read(inventory, "limb.m_armorDamageMulti", () => limb.m_armorDamageMulti, out var armor)) row.ArmorMulti = armor;
        if (Read(inventory, "limb.HasDestruction", () => limb.HasDestruction(), out var destruction)) row.HasDestruction = destruction;
        if (Read(inventory, "limb.DestructionType", () => limb.DestructionType.ToString(), out var destructionType)) row.DestructionType = destructionType;
        if (limb is Dam_EnemyDamageLimb_Custom custom)
            row.ClampToLimbMaxHealth = Read(inventory, "limb.m_clampDamageToLimbMaxHealth", () => custom.m_clampDamageToLimbMaxHealth, out var clamp) ? clamp : null;
        ReadCollider(inventory, row, model, limb.gameObject);
        return row;
    }

    private static void ReadCollider(Inventory inventory, LimbRow row, Transform model, GameObject limb)
    {
        Collider[]? colliders = null;
        if (!Read(inventory, "limb.colliders", () => limb.GetComponentsInChildren<Collider>(true), out colliders) || colliders == null) return;
        foreach (var collider in colliders)
        {
            if (collider == null) continue;
            row.ColliderType = collider.GetType().Name;
            row.ColliderOnLimb = collider.gameObject == limb;
            row.ColliderObject = RuntimeDiagnostics.PathOf(collider.transform);
            if (Read(inventory, "collider.bounds", () => collider.bounds.center, out var center))
                row.ColliderCenter = Local(inventory, model, center, "collider.bounds.center");
            return;
        }
        row.Notes.Add("no Collider on this limb's game object or its children");
    }

    /// <summary>
    /// The contract's tumor numbering: the custom limbs and the renderers whose name starts with <c>EggSack</c> (both
    /// ordered, the renderers by a natural name order) are paired one to one by the smallest distance between the
    /// limb's position and the renderer's root bone position, both relative to the model root. Every pair and every
    /// distance is written, and a side with no partner is written as unpaired rather than dropped.
    /// </summary>
    private static void PairTumors(EnemyRow enemy)
    {
        var eggSacks = new List<EggSackRow>();
        foreach (var renderer in enemy.Renderers)
            if (renderer.Name.StartsWith(EggSackPrefix, StringComparison.Ordinal))
                eggSacks.Add(new EggSackRow(renderer.Name, renderer.RootBone, renderer.RootBonePosition));
        eggSacks.Sort((left, right) => NaturalCompare(left.Name, right.Name));
        for (var index = 0; index < eggSacks.Count; index++) eggSacks[index].Number = index + 1;
        enemy.Pairing.EggSacks.AddRange(eggSacks);

        var custom = new List<LimbRow>();
        foreach (var limb in enemy.Limbs) if (limb.Custom) custom.Add(limb);

        var candidates = new List<Candidate>();
        foreach (var limb in custom)
        {
            if (limb.Position == null) continue;
            foreach (var eggSack in eggSacks)
            {
                if (eggSack.Position == null) continue;
                candidates.Add(new Candidate(Vector3.Distance(limb.Position.Value, eggSack.Position.Value), limb.Index, eggSack.Number));
            }
        }
        candidates.Sort((left, right) =>
        {
            var byDistance = left.Distance.CompareTo(right.Distance);
            if (byDistance != 0) return byDistance;
            var byLimb = left.LimbIndex.CompareTo(right.LimbIndex);
            return byLimb != 0 ? byLimb : left.RendererNumber.CompareTo(right.RendererNumber);
        });

        var limbTaken = new HashSet<int>();
        var eggTaken = new HashSet<int>();
        var pairs = new List<PairRow>();
        foreach (var candidate in candidates)
        {
            if (limbTaken.Contains(candidate.LimbIndex) || eggTaken.Contains(candidate.RendererNumber)) continue;
            limbTaken.Add(candidate.LimbIndex);
            eggTaken.Add(candidate.RendererNumber);
            LimbRow? limb = null;
            foreach (var entry in custom) if (entry.Index == candidate.LimbIndex) { limb = entry; break; }
            EggSackRow? eggSack = null;
            foreach (var entry in eggSacks) if (entry.Number == candidate.RendererNumber) { eggSack = entry; break; }
            if (limb == null || eggSack == null) continue;
            pairs.Add(new PairRow(limb, eggSack, candidate.Distance));
        }
        pairs.Sort((left, right) => left.LimbIndex.CompareTo(right.LimbIndex));
        enemy.Pairing.Pairs.AddRange(pairs);
        foreach (var limb in custom)
        {
            if (limbTaken.Contains(limb.Index)) continue;
            enemy.Pairing.UnpairedLimbs.Add("limb[" + limb.Index.ToString(CultureInfo.InvariantCulture) + "] "
                + (limb.Position == null ? "has no position relative to the model root" : "has no unpaired EggSack renderer with a root bone"));
        }
        foreach (var eggSack in eggSacks)
        {
            if (eggTaken.Contains(eggSack.Number)) continue;
            enemy.Pairing.UnpairedRenderers.Add("EggSack#" + eggSack.Number.ToString(CultureInfo.InvariantCulture) + " " + eggSack.Name
                + (eggSack.Position == null ? " has no root bone position" : " has no unpaired custom limb"));
        }
    }

    private static string NearestBone(Transform start, HashSet<string> boneNames, out bool found)
    {
        for (var current = start; current != null; current = current.parent)
        {
            if (!boneNames.Contains(current.name)) continue;
            found = true;
            return current.name;
        }
        found = false;
        return "";
    }

    /// <summary>The limb's path from the model root, both ends included. A limb that is not under the model still
    /// answers the path it does have rather than an empty string.</summary>
    private static string RelativePath(Transform model, Transform transform)
    {
        var names = new List<string>();
        for (var current = transform; current != null; current = current.parent)
        {
            names.Add(current.name ?? "");
            if (current == model) break;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private static Vector3? Local(Inventory inventory, Transform model, Vector3 world, string path)
    {
        if (!Read(inventory, path, () => model.InverseTransformPoint(world), out var local)) return null;
        return local;
    }

    /// <summary>Natural name order: digit runs compare as numbers, so EggSack2 precedes EggSack10.</summary>
    private static int NaturalCompare(string left, string right)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
            {
                var leftStart = i;
                var rightStart = j;
                while (i < left.Length && char.IsDigit(left[i])) i++;
                while (j < right.Length && char.IsDigit(right[j])) j++;
                var leftNumber = left.Substring(leftStart, i - leftStart).TrimStart('0');
                var rightNumber = right.Substring(rightStart, j - rightStart).TrimStart('0');
                if (leftNumber.Length != rightNumber.Length) return leftNumber.Length - rightNumber.Length;
                var digits = string.CompareOrdinal(leftNumber, rightNumber);
                if (digits != 0) return digits;
                continue;
            }
            var characters = left[i].CompareTo(right[j]);
            if (characters != 0) return characters;
            i++;
            j++;
        }
        return (left.Length - i) - (right.Length - j);
    }

    /// <summary>One guarded read. A member that throws is one line in the document's <c>errors</c> array and the rest
    /// of the inventory continues; the caller sees whether the value is real.</summary>
    private static bool Read<T>(Inventory inventory, string path, Func<T> read, out T value)
    {
        try { value = read(); return true; }
        catch (Exception error)
        {
            inventory.Errors.Add(new CaptureInventoryError(path, error.GetType().Name));
            value = default!;
            return false;
        }
    }

    private static void WriteBody(Utf8JsonWriter json, Inventory inventory)
    {
        json.WriteString("prefabSource", PrefabSource);
        json.WriteBoolean("prefabManagerAvailable", inventory.PrefabManagerAvailable);
        json.WriteNumber("prefabCount", inventory.PrefabCount);
        json.WriteNumber("enemyBlockCount", inventory.EnemyBlockCount);

        json.WritePropertyName("enemies");
        json.WriteStartArray();
        foreach (var enemy in inventory.Enemies) WriteEnemy(json, enemy);
        json.WriteEndArray();

        json.WritePropertyName("notes");
        json.WriteStartArray();
        foreach (var note in inventory.Notes) json.WriteStringValue(note);
        json.WriteEndArray();
    }

    private static void WriteEnemy(Utf8JsonWriter json, EnemyRow enemy)
    {
        json.WriteStartObject();
        json.WriteNumber("enemyType", enemy.EnemyType);
        json.WriteString("name", enemy.Name);
        json.WriteBoolean("internalEnabled", enemy.InternalEnabled);
        json.WriteNumber("balancingDataId", enemy.BalancingDataId);
        if (enemy.AllowDamageBonusFromBehind.HasValue) json.WriteBoolean("allowDamageBonusFromBehind", enemy.AllowDamageBonusFromBehind.Value);
        else json.WriteNull("allowDamageBonusFromBehind");
        json.WriteBoolean("prefabBuilt", enemy.PrefabBuilt);
        json.WriteString("prefab", enemy.PrefabName);
        json.WriteString("modelPath", enemy.ModelPath);
        json.WriteBoolean("damageBaseFound", enemy.DamageBaseFound);

        json.WritePropertyName("renderers");
        json.WriteStartArray();
        foreach (var renderer in enemy.Renderers)
        {
            json.WriteStartObject();
            json.WriteString("name", renderer.Name);
            json.WriteString("object", renderer.Object);
            if (renderer.HasMatrix)
            {
                json.WritePropertyName("matrix");
                json.WriteStartArray();
                WriteMatrix(json, renderer.Matrix);
                json.WriteEndArray();
            }
            else json.WriteNull("matrix");
            json.WriteString("rootBone", renderer.RootBone);
            WriteVectorOrNull(json, "rootBonePosition", renderer.RootBonePosition);
            json.WritePropertyName("bones");
            json.WriteStartArray();
            foreach (var bone in renderer.Bones) json.WriteStringValue(bone);
            json.WriteEndArray();
            if (renderer.Bindposes.HasValue) json.WriteNumber("bindposes", renderer.Bindposes.Value);
            else json.WriteNull("bindposes");
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WritePropertyName("limbs");
        json.WriteStartArray();
        foreach (var limb in enemy.Limbs) WriteLimb(json, limb);
        json.WriteEndArray();

        json.WritePropertyName("tumorPairing");
        json.WriteStartObject();
        json.WriteString("rule", "custom-limb-position to EggSack-rootBone-position nearest, one to one");
        json.WritePropertyName("eggSackRenderers");
        json.WriteStartArray();
        foreach (var eggSack in enemy.Pairing.EggSacks)
        {
            json.WriteStartObject();
            json.WriteNumber("number", eggSack.Number);
            json.WriteString("name", eggSack.Name);
            json.WriteString("rootBone", eggSack.RootBone);
            WriteVectorOrNull(json, "rootBonePosition", eggSack.Position);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WritePropertyName("pairs");
        json.WriteStartArray();
        foreach (var pair in enemy.Pairing.Pairs)
        {
            json.WriteStartObject();
            json.WriteNumber("limbIndex", pair.LimbIndex);
            json.WriteNumber("limbId", pair.LimbId);
            json.WriteString("limbName", pair.LimbName);
            WriteVectorOrNull(json, "limbPosition", pair.LimbPosition);
            json.WriteNumber("rendererNumber", pair.RendererNumber);
            json.WriteString("rendererName", pair.RendererName);
            WriteVectorOrNull(json, "rendererPosition", pair.RendererPosition);
            WriteNumber(json, "distance", pair.Distance);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WritePropertyName("unpairedLimbs");
        json.WriteStartArray();
        foreach (var limb in enemy.Pairing.UnpairedLimbs) json.WriteStringValue(limb);
        json.WriteEndArray();
        json.WritePropertyName("unpairedRenderers");
        json.WriteStartArray();
        foreach (var renderer in enemy.Pairing.UnpairedRenderers) json.WriteStringValue(renderer);
        json.WriteEndArray();
        json.WriteEndObject();

        json.WritePropertyName("notes");
        json.WriteStartArray();
        foreach (var note in enemy.Notes) json.WriteStringValue(note);
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteLimb(Utf8JsonWriter json, LimbRow limb)
    {
        json.WriteStartObject();
        json.WriteNumber("index", limb.Index);
        json.WriteNumber("m_limbID", limb.LimbId);
        json.WriteString("debugName", limb.DebugName);
        json.WriteBoolean("custom", limb.Custom);
        json.WriteString("object", limb.ObjectName);
        json.WriteString("path", limb.Path);
        json.WriteString("bone", limb.Bone);
        json.WriteBoolean("boneFound", limb.BoneFound);
        WriteVectorOrNull(json, "position", limb.Position);
        json.WriteString("m_type", limb.Type);
        json.WriteBoolean("baseAssigned", limb.BaseAssigned);
        WriteNumberOrNull(json, "m_health", limb.Health);
        WriteNumberOrNull(json, "m_healthMax", limb.HealthMax);
        WriteNumberOrNull(json, "m_weakspotDamageMulti", limb.WeakspotMulti);
        WriteNumberOrNull(json, "m_armorDamageMulti", limb.ArmorMulti);
        if (limb.HasDestruction.HasValue) json.WriteBoolean("hasDestruction", limb.HasDestruction.Value);
        else json.WriteNull("hasDestruction");
        json.WriteString("destructionType", limb.DestructionType);
        if (limb.ClampToLimbMaxHealth.HasValue) json.WriteBoolean("m_clampDamageToLimbMaxHealth", limb.ClampToLimbMaxHealth.Value);
        else json.WriteNull("m_clampDamageToLimbMaxHealth");
        json.WriteStartObject("collider");
        json.WriteString("type", limb.ColliderType);
        json.WriteString("object", limb.ColliderObject);
        json.WriteBoolean("onLimb", limb.ColliderOnLimb);
        WriteVectorOrNull(json, "boundsCenter", limb.ColliderCenter);
        json.WriteEndObject();
        json.WritePropertyName("notes");
        json.WriteStartArray();
        foreach (var note in limb.Notes) json.WriteStringValue(note);
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteMatrix(Utf8JsonWriter json, Matrix4x4 matrix)
    {
        // Row-major: m00 m01 m02 m03, then m10 .. m33.
        WriteNumber(json, null, matrix.m00); WriteNumber(json, null, matrix.m01); WriteNumber(json, null, matrix.m02); WriteNumber(json, null, matrix.m03);
        WriteNumber(json, null, matrix.m10); WriteNumber(json, null, matrix.m11); WriteNumber(json, null, matrix.m12); WriteNumber(json, null, matrix.m13);
        WriteNumber(json, null, matrix.m20); WriteNumber(json, null, matrix.m21); WriteNumber(json, null, matrix.m22); WriteNumber(json, null, matrix.m23);
        WriteNumber(json, null, matrix.m30); WriteNumber(json, null, matrix.m31); WriteNumber(json, null, matrix.m32); WriteNumber(json, null, matrix.m33);
    }

    private static void WriteVectorOrNull(Utf8JsonWriter json, string name, Vector3? value)
    {
        if (value == null)
        {
            if (name != null) json.WriteNull(name);
            else json.WriteNullValue();
            return;
        }
        if (name != null) json.WritePropertyName(name);
        json.WriteStartArray();
        WriteNumber(json, null, value.Value.x);
        WriteNumber(json, null, value.Value.y);
        WriteNumber(json, null, value.Value.z);
        json.WriteEndArray();
    }

    private static void WriteNumberOrNull(Utf8JsonWriter json, string name, float? value)
    {
        if (value == null) json.WriteNull(name);
        else WriteNumber(json, name, value.Value);
    }

    /// <summary>A float that is not finite has no JSON number; it is written as null so a broken read cannot make the
    /// document invalid.</summary>
    private static void WriteNumber(Utf8JsonWriter json, string? name, float value)
    {
        if (!float.IsFinite(value))
        {
            if (name != null) json.WriteNull(name);
            else json.WriteNullValue();
            return;
        }
        if (name != null) json.WriteNumber(name, value);
        else json.WriteNumberValue(value);
    }

    private sealed class Inventory
    {
        internal readonly List<CaptureInventoryError> Errors = new();
        internal readonly List<string> Notes = new();
        internal readonly List<EnemyRow> Enemies = new();
        internal bool PrefabManagerAvailable;
        internal int PrefabCount;
        internal int EnemyBlockCount;
    }

    private sealed class EnemyRow
    {
        internal EnemyRow(uint enemyType, string name) { EnemyType = enemyType; Name = name; }
        internal uint EnemyType { get; }
        internal string Name { get; }
        internal bool InternalEnabled { get; set; }
        internal uint BalancingDataId { get; set; }
        internal bool? AllowDamageBonusFromBehind { get; set; }
        internal bool PrefabBuilt { get; set; }
        internal string PrefabName { get; set; } = "";
        internal string ModelPath { get; set; } = "";
        internal bool DamageBaseFound { get; set; }
        internal List<RendererRow> Renderers { get; } = new();
        internal List<LimbRow> Limbs { get; } = new();
        internal PairingRow Pairing { get; } = new();
        internal List<string> Notes { get; } = new();
    }

    private sealed class RendererRow
    {
        internal string Name { get; set; } = "";
        internal string Object { get; set; } = "";
        internal Matrix4x4 Matrix { get; set; }
        internal bool HasMatrix { get; set; }
        internal string RootBone { get; set; } = "";
        internal Vector3? RootBonePosition { get; set; }
        internal List<string> Bones { get; } = new();
        internal int? Bindposes { get; set; }
    }

    private sealed class LimbRow
    {
        internal LimbRow(int index) { Index = index; }
        internal int Index { get; }
        internal int LimbId { get; set; }
        internal string DebugName { get; set; } = "";
        internal bool Custom { get; set; }
        internal string ObjectName { get; set; } = "";
        internal string Path { get; set; } = "";
        internal string Bone { get; set; } = "";
        internal bool BoneFound { get; set; }
        internal Vector3? Position { get; set; }
        internal string Type { get; set; } = "";
        internal bool BaseAssigned { get; set; }
        internal float? Health { get; set; }
        internal float? HealthMax { get; set; }
        internal float? WeakspotMulti { get; set; }
        internal float? ArmorMulti { get; set; }
        internal bool? HasDestruction { get; set; }
        internal string DestructionType { get; set; } = "";
        internal bool? ClampToLimbMaxHealth { get; set; }
        internal string ColliderType { get; set; } = "";
        internal string ColliderObject { get; set; } = "";
        internal bool ColliderOnLimb { get; set; }
        internal Vector3? ColliderCenter { get; set; }
        internal List<string> Notes { get; } = new();
    }

    private sealed class PairingRow
    {
        internal List<EggSackRow> EggSacks { get; } = new();
        internal List<PairRow> Pairs { get; } = new();
        internal List<string> UnpairedLimbs { get; } = new();
        internal List<string> UnpairedRenderers { get; } = new();
    }

    private sealed class EggSackRow
    {
        internal EggSackRow(string name, string rootBone, Vector3? position)
        { Name = name; RootBone = rootBone; Position = position; }
        internal int Number { get; set; }
        internal string Name { get; }
        internal string RootBone { get; }
        internal Vector3? Position { get; }
    }

    private sealed class PairRow
    {
        internal PairRow(LimbRow limb, EggSackRow eggSack, float distance)
        {
            LimbIndex = limb.Index;
            LimbId = limb.LimbId;
            LimbName = limb.DebugName.Length != 0 ? limb.DebugName : limb.ObjectName;
            LimbPosition = limb.Position;
            RendererNumber = eggSack.Number;
            RendererName = eggSack.Name;
            RendererPosition = eggSack.Position;
            Distance = distance;
        }

        internal int LimbIndex { get; }
        internal int LimbId { get; }
        internal string LimbName { get; }
        internal Vector3? LimbPosition { get; }
        internal int RendererNumber { get; }
        internal string RendererName { get; }
        internal Vector3? RendererPosition { get; }
        internal float Distance { get; }
    }

    private readonly struct Candidate
    {
        internal Candidate(float distance, int limbIndex, int rendererNumber)
        { Distance = distance; LimbIndex = limbIndex; RendererNumber = rendererNumber; }
        internal float Distance { get; }
        internal int LimbIndex { get; }
        internal int RendererNumber { get; }
    }
}

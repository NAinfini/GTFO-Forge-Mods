using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AssetShards;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The enemy appearance inventory. It sits beside the data block export and answers two questions only the running
/// game can answer, as raw facts and without judging them: which vanilla appearance customizations are loaded and
/// what they contain (<c>CB_CustomPrefab</c> attachment entries, <c>CB_CustomMaterials</c> replacements), and which
/// shader properties a vanilla enemy material actually exposes for tinting.
///
/// The data block table is read through the same <see cref="CaptureDataBlocks"/> the export uses, so a block type a
/// mod adds is treated like a shipped one. Every model and customization asset path is inspected once, however many
/// enemy blocks reference it, and each enemy block only references the paths. The result is one JSON document written
/// by the recorder's own writer thread into the session directory beside the data block records; identical content is
/// not written again.
///
/// Every read is guarded: a path that throws, a renderer that cannot be read or a stripped shader property table is
/// recorded in the document's <c>errors</c> array (path plus exception type) and the rest of the inventory continues.
/// Nothing here changes a game object, installs a hook or runs per frame.
/// </summary>
internal static class CaptureEnemyAppearance
{
    internal const string FileName = "enemy-appearance.json";
    internal const int SchemaVersion = 1;


    /// <summary>The property table is read through the interop members this build's UnityEngine.CoreModule.dll
    /// exposes: <c>Shader.GetPropertyCount</c>, <c>GetPropertyName</c> and <c>GetPropertyType</c> were confirmed
    /// present with Cecil before this file used them. A built shader can still answer an empty table, and then the
    /// candidate names are probed on the material instead; each shader says which one produced its row.</summary>
    private const string ShaderApiMethod = "Shader.GetPropertyCount/GetPropertyName/GetPropertyType";
    private const string CandidateMethod = "Material.HasProperty(candidate names)";

    private const string EnemyBlockFullName = "GameData.EnemyDataBlock";

    private static readonly string[] CandidateProperties =
    {
        "_Color", "_BaseColor", "_TintColor", "_Tint", "_ColorTint", "_MainColor",
        "_EmissionColor", "_Emission", "_GlowColor"
    };

    private static readonly CaptureInventoryDocument Document = new(FileName, "gtfo-forge-enemy-appearance-inventory", SchemaVersion);

    /// <summary>
    /// Takes the inventory at the level start. <c>level-generated</c> is the game's own once-per-level boundary
    /// (FactoryDone), which is the point the data block table and the level's assets exist at; the document is written
    /// only when the result differs from the last one, so a level that finds the same assets does not rewrite it.
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
        Type? type = null;
        try
        {
            foreach (var entry in CaptureDataBlocks.Types())
                if (entry.Type.FullName == EnemyBlockFullName) { type = entry.Type; break; }
        }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(EnemyBlockFullName, error.GetType().Name)); }
        if (type == null)
        {
            inventory.Errors.Add(new CaptureInventoryError(EnemyBlockFullName, "type-not-found"));
            yield break;
        }

        IReadOnlyList<object>? blocks = null;
        try { blocks = CaptureDataBlocks.Blocks(type); }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(EnemyBlockFullName, error.GetType().Name)); }
        if (blocks == null)
        {
            inventory.Errors.Add(new CaptureInventoryError(EnemyBlockFullName, "blocks-unavailable"));
            yield break;
        }

        foreach (var block in blocks)
        {
            yield return 0;
            try
            {
                if (block is not GameData.EnemyDataBlock enemy) continue;
                var row = new EnemyRow(CaptureDataBlocks.PersistentId(block), CaptureDataBlocks.Name(block));
                var datas = enemy.ModelDatas;
                if (datas != null)
                    for (var index = 0; index < datas.Count; index++)
                    {
                        GameData.ModelData? data;
                        try { data = datas[index]; }
                        catch (Exception error)
                        {
                            inventory.Errors.Add(new CaptureInventoryError(row.Name + "#" + index.ToString(CultureInfo.InvariantCulture), error.GetType().Name));
                            continue;
                        }
                        if (data == null) continue;
                        string modelFile = "", customization = "";
                        try { modelFile = data.ModelFile ?? ""; }
                        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(row.Name + "#" + index.ToString(CultureInfo.InvariantCulture), error.GetType().Name)); }
                        try { customization = data.ModelCustomization ?? ""; }
                        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(row.Name + "#" + index.ToString(CultureInfo.InvariantCulture), error.GetType().Name)); }
                        row.Models.Add(new ModelRow(modelFile, customization));
                        if (modelFile.Length != 0) Resource(inventory, modelFile);
                        if (customization.Length != 0) Resource(inventory, customization);
                    }
                inventory.Enemies.Add(row);
            }
            catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError("enemy-block", error.GetType().Name)); }
        }

        // The paths are deduplicated first, so a model an enemy type shares with another is inspected once.
        foreach (var resource in inventory.ResourceOrder) { yield return 0; foreach (var step in Inspect(inventory, resource)) yield return step; }
    }

    private static ResourceRow Resource(Inventory inventory, string path)
    {
        if (inventory.Resources.TryGetValue(path, out var existing)) return existing;
        var row = new ResourceRow(path);
        inventory.Resources[path] = row;
        inventory.ResourceOrder.Add(row);
        return row;
    }

    private static IEnumerable<int> Inspect(Inventory inventory, ResourceRow resource)
    {
        GameObject? asset = null;
        try { asset = AssetShardManager.GetLoadedAsset<GameObject>(resource.Path, false); }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(resource.Path, error.GetType().Name)); }
        resource.Loaded = asset != null;
        if (asset == null) yield break;
        yield return 0;
        // Rigid-mesh enemies (flyers) carry MeshRenderer only; skinned ones carry SkinnedMeshRenderer. Both are inventoried.
        Renderer[]? itemsRenderers = null;
        try { itemsRenderers = asset.GetComponentsInChildren<Renderer>(true); }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(resource.Path, error.GetType().Name)); }
        if (itemsRenderers != null) foreach (var renderer in itemsRenderers)
        {
            yield return 0;
            if (renderer == null) continue;
            var skinned = renderer.TryCast<SkinnedMeshRenderer>();
            if (skinned == null && renderer.TryCast<MeshRenderer>() == null) continue;
            try { resource.Renderers.Add(ReadRenderer(inventory, renderer, skinned)); }
            catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(resource.Path, error.GetType().Name)); }
        }
        yield return 0;
        CB_CustomPrefab[]? itemsCustomPrefabs = null;
        try { itemsCustomPrefabs = asset.GetComponentsInChildren<CB_CustomPrefab>(true); }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(resource.Path, error.GetType().Name)); }
        if (itemsCustomPrefabs != null) foreach (var customizer in itemsCustomPrefabs)
        {
            yield return 0;
            if (customizer == null) continue;
            try { resource.CustomPrefabs.Add(ReadCustomPrefab(inventory, customizer)); }
            catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(resource.Path, error.GetType().Name)); }
        }
        yield return 0;
        CB_CustomMaterials[]? itemsCustomMaterials = null;
        try { itemsCustomMaterials = asset.GetComponentsInChildren<CB_CustomMaterials>(true); }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(resource.Path, error.GetType().Name)); }
        if (itemsCustomMaterials != null) foreach (var customizer in itemsCustomMaterials)
        {
            yield return 0;
            if (customizer == null) continue;
            try { resource.CustomMaterials.Add(ReadCustomMaterials(inventory, customizer)); }
            catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(resource.Path, error.GetType().Name)); }
        }
    }

    /// <summary>A rigid MeshRenderer has no skeleton, so <paramref name="skinned"/> is null and its root bone and bones stay empty.</summary>
    private static RendererRow ReadRenderer(Inventory inventory, Renderer renderer, SkinnedMeshRenderer? skinned)
    {
        var row = new RendererRow(RuntimeDiagnostics.PathOf(renderer.transform), renderer.gameObject.name,
            renderer.enabled, renderer.gameObject.activeInHierarchy);
        if (skinned != null)
        {
            try { row.RootBone = skinned.rootBone == null ? "" : skinned.rootBone.name; }
            catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(row.Object, error.GetType().Name)); }
            try
            {
                var bones = skinned.bones;
                if (bones != null)
                    for (var index = 0; index < bones.Length; index++)
                    {
                        var bone = bones[index];
                        row.Bones.Add(bone == null ? "" : bone.name);
                    }
            }
            catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(row.Object, error.GetType().Name)); }
        }
        try
        {
            var materials = renderer.sharedMaterials;
            if (materials != null)
                for (var index = 0; index < materials.Length; index++)
                {
                    var material = materials[index];
                    if (material == null) continue;
                    row.Materials.Add(ReadMaterial(inventory, material));
                }
        }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(row.Object, error.GetType().Name)); }
        return row;
    }

    private static MaterialRow ReadMaterial(Inventory inventory, Material material)
    {
        string name = "", shaderName = "";
        Shader? shader = null;
        try { name = material.name ?? ""; }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError("material", error.GetType().Name)); }
        try { shader = material.shader; }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(name, error.GetType().Name)); }
        try { shaderName = shader == null ? "" : shader.name ?? ""; }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(name, error.GetType().Name)); }

        var row = new MaterialRow(name, shaderName);
        if (shader == null || shaderName.Length == 0) return row;
        var shaderRow = Shader(inventory, shader, shaderName);
        if (shaderRow.Method != CandidateMethod) return row;
        foreach (var property in CandidateProperties) row.Candidates.Add(Probe(inventory, material, property));
        return row;
    }

    private static ShaderRow Shader(Inventory inventory, Shader shader, string name)
    {
        if (inventory.Shaders.TryGetValue(name, out var existing)) return existing;
        var row = new ShaderRow(name);
        try
        {
            var count = shader.GetPropertyCount();
            if (count > 0)
            {
                for (var index = 0; index < count; index++)
                    row.Properties.Add(new PropertyRow(shader.GetPropertyName(index) ?? "", shader.GetPropertyType(index).ToString()));
                row.Method = ShaderApiMethod;
            }
            else row.Method = CandidateMethod;
        }
        catch (Exception error)
        {
            inventory.Errors.Add(new CaptureInventoryError("shader:" + name, error.GetType().Name));
            row.Method = CandidateMethod;
        }
        inventory.Shaders[name] = row;
        inventory.ShaderOrder.Add(row);
        return row;
    }

    private static CandidateRow Probe(Inventory inventory, Material material, string property)
    {
        try
        {
            if (!material.HasProperty(property)) return new CandidateRow(property, false, null);
            var color = material.GetColor(property);
            return new CandidateRow(property, true, new[] { color.r, color.g, color.b, color.a });
        }
        catch (Exception error)
        {
            inventory.Errors.Add(new CaptureInventoryError(property, error.GetType().Name));
            return new CandidateRow(property, false, null);
        }
    }

    private static CustomPrefabRow ReadCustomPrefab(Inventory inventory, CB_CustomPrefab customizer)
    {
        var row = new CustomPrefabRow(RuntimeDiagnostics.PathOf(customizer.transform));
        try
        {
            var entries = customizer.prefabs;
            if (entries != null)
                for (var index = 0; index < entries.Length; index++)
                {
                    try
                    {
                        var entry = entries[index];
                        var prefab = entry.Prefab;
                        row.Entries.Add(new PrefabEntryRow(prefab == null ? "" : prefab.name, entry.Align ?? "", entry.ScaleTweak));
                    }
                    catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(row.Object, error.GetType().Name)); }
                }
        }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(row.Object, error.GetType().Name)); }
        return row;
    }

    private static CustomMaterialsRow ReadCustomMaterials(Inventory inventory, CB_CustomMaterials customizer)
    {
        var row = new CustomMaterialsRow(RuntimeDiagnostics.PathOf(customizer.transform));
        try
        {
            var entries = customizer.m_materialDefs;
            if (entries != null)
                for (var index = 0; index < entries.Length; index++)
                {
                    try
                    {
                        var entry = entries[index];
                        row.Entries.Add(new MaterialEntryRow(
                            MaterialName(entry.m_fromMaterial), MaterialShader(entry.m_fromMaterial),
                            MaterialName(entry.m_toMaterial), MaterialShader(entry.m_toMaterial)));
                    }
                    catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(row.Object, error.GetType().Name)); }
                }
        }
        catch (Exception error) { inventory.Errors.Add(new CaptureInventoryError(row.Object, error.GetType().Name)); }
        return row;
    }

    private static string MaterialName(Material? material)
    {
        if (material == null) return "";
        try { return material.name ?? ""; }
        catch (Exception) { return ""; } // Destroyed Unity wrapper: the material has no readable name, and the empty name is the recorded fact.
    }

    private static string MaterialShader(Material? material)
    {
        if (material == null) return "";
        try { var shader = material.shader; return shader == null ? "" : shader.name ?? ""; }
        catch (Exception) { return ""; } // Destroyed Unity wrapper: the material has no readable shader, and the empty name is the recorded fact.
    }

    private static void WriteBody(Utf8JsonWriter json, Inventory inventory)
    {
        json.WriteString("shaderPropertyMethod", ShaderApiMethod);

        json.WritePropertyName("resources");
        json.WriteStartArray();
        foreach (var resource in inventory.ResourceOrder)
        {
            json.WriteStartObject();
            json.WriteString("path", resource.Path);
            json.WriteBoolean("loaded", resource.Loaded);
            json.WritePropertyName("renderers");
            json.WriteStartArray();
            foreach (var renderer in resource.Renderers)
            {
                json.WriteStartObject();
                json.WriteString("object", renderer.Object);
                json.WriteString("name", renderer.Name);
                json.WriteBoolean("enabled", renderer.Enabled);
                json.WriteBoolean("activeInHierarchy", renderer.ActiveInHierarchy);
                json.WriteString("rootBone", renderer.RootBone);
                json.WritePropertyName("bones");
                json.WriteStartArray();
                foreach (var bone in renderer.Bones) json.WriteStringValue(bone);
                json.WriteEndArray();
                json.WritePropertyName("materials");
                json.WriteStartArray();
                foreach (var material in renderer.Materials)
                {
                    json.WriteStartObject();
                    json.WriteString("name", material.Name);
                    json.WriteString("shader", material.Shader);
                    if (material.Candidates.Count != 0)
                    {
                        json.WritePropertyName("candidates");
                        json.WriteStartArray();
                        foreach (var candidate in material.Candidates)
                        {
                            json.WriteStartObject();
                            json.WriteString("property", candidate.Property);
                            json.WriteBoolean("hasProperty", candidate.HasProperty);
                            if (candidate.Value != null)
                            {
                                json.WritePropertyName("rgba");
                                json.WriteStartArray();
                                foreach (var component in candidate.Value) json.WriteNumberValue(component);
                                json.WriteEndArray();
                            }
                            json.WriteEndObject();
                        }
                        json.WriteEndArray();
                    }
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WritePropertyName("customPrefabs");
            json.WriteStartArray();
            foreach (var customizer in resource.CustomPrefabs)
            {
                json.WriteStartObject();
                json.WriteString("object", customizer.Object);
                json.WritePropertyName("entries");
                json.WriteStartArray();
                foreach (var entry in customizer.Entries)
                {
                    json.WriteStartObject();
                    json.WriteString("prefab", entry.Prefab);
                    json.WriteString("align", entry.Align);
                    json.WriteNumber("scaleTweak", entry.ScaleTweak);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WritePropertyName("customMaterials");
            json.WriteStartArray();
            foreach (var customizer in resource.CustomMaterials)
            {
                json.WriteStartObject();
                json.WriteString("object", customizer.Object);
                json.WritePropertyName("entries");
                json.WriteStartArray();
                foreach (var entry in customizer.Entries)
                {
                    json.WriteStartObject();
                    json.WriteString("from", entry.From);
                    json.WriteString("fromShader", entry.FromShader);
                    json.WriteString("to", entry.To);
                    json.WriteString("toShader", entry.ToShader);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WritePropertyName("shaders");
        json.WriteStartArray();
        foreach (var shader in inventory.ShaderOrder)
        {
            json.WriteStartObject();
            json.WriteString("name", shader.Name);
            json.WriteString("method", shader.Method);
            json.WritePropertyName("properties");
            json.WriteStartArray();
            foreach (var property in shader.Properties)
            {
                json.WriteStartObject();
                json.WriteString("name", property.Name);
                json.WriteString("type", property.Type);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WritePropertyName("enemies");
        json.WriteStartArray();
        foreach (var enemy in inventory.Enemies)
        {
            json.WriteStartObject();
            json.WriteString("persistentId", enemy.PersistentId);
            json.WriteString("name", enemy.Name);
            json.WritePropertyName("models");
            json.WriteStartArray();
            foreach (var model in enemy.Models)
            {
                json.WriteStartObject();
                json.WriteString("modelFile", model.ModelFile);
                json.WriteString("modelCustomization", model.ModelCustomization);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private sealed class Inventory
    {
        internal readonly List<CaptureInventoryError> Errors = new();
        internal readonly List<EnemyRow> Enemies = new();
        internal readonly Dictionary<string, ResourceRow> Resources = new(StringComparer.Ordinal);
        internal readonly List<ResourceRow> ResourceOrder = new();
        internal readonly Dictionary<string, ShaderRow> Shaders = new(StringComparer.Ordinal);
        internal readonly List<ShaderRow> ShaderOrder = new();
    }

    private sealed class EnemyRow
    {
        internal EnemyRow(string persistentId, string name) { PersistentId = persistentId; Name = name; }
        internal string PersistentId { get; }
        internal string Name { get; }
        internal List<ModelRow> Models { get; } = new();
    }

    private sealed class ModelRow
    {
        internal ModelRow(string modelFile, string modelCustomization)
        { ModelFile = modelFile; ModelCustomization = modelCustomization; }
        internal string ModelFile { get; }
        internal string ModelCustomization { get; }
    }

    private sealed class ResourceRow
    {
        internal ResourceRow(string path) { Path = path; }
        internal string Path { get; }
        internal bool Loaded { get; set; }
        internal List<RendererRow> Renderers { get; } = new();
        internal List<CustomPrefabRow> CustomPrefabs { get; } = new();
        internal List<CustomMaterialsRow> CustomMaterials { get; } = new();
    }

    private sealed class RendererRow
    {
        internal RendererRow(string path, string name, bool enabled, bool activeInHierarchy)
        { Object = path; Name = name; Enabled = enabled; ActiveInHierarchy = activeInHierarchy; }
        internal string Object { get; }
        internal string Name { get; }
        internal bool Enabled { get; }
        internal bool ActiveInHierarchy { get; }
        internal string RootBone { get; set; } = "";
        internal List<string> Bones { get; } = new();
        internal List<MaterialRow> Materials { get; } = new();
    }

    private sealed class MaterialRow
    {
        internal MaterialRow(string name, string shader) { Name = name; Shader = shader; }
        internal string Name { get; }
        internal string Shader { get; }
        internal List<CandidateRow> Candidates { get; } = new();
    }

    private sealed class CandidateRow
    {
        internal CandidateRow(string property, bool hasProperty, float[]? value)
        { Property = property; HasProperty = hasProperty; Value = value; }
        internal string Property { get; }
        internal bool HasProperty { get; }
        internal float[]? Value { get; }
    }

    private sealed class ShaderRow
    {
        internal ShaderRow(string name) { Name = name; }
        internal string Name { get; }
        internal string Method { get; set; } = ShaderApiMethod;
        internal List<PropertyRow> Properties { get; } = new();
    }

    private sealed class PropertyRow
    {
        internal PropertyRow(string name, string type) { Name = name; Type = type; }
        internal string Name { get; }
        internal string Type { get; }
    }

    private sealed class CustomPrefabRow
    {
        internal CustomPrefabRow(string path) { Object = path; }
        internal string Object { get; }
        internal List<PrefabEntryRow> Entries { get; } = new();
    }

    private sealed class PrefabEntryRow
    {
        internal PrefabEntryRow(string prefab, string align, float scaleTweak)
        { Prefab = prefab; Align = align; ScaleTweak = scaleTweak; }
        internal string Prefab { get; }
        internal string Align { get; }
        internal float ScaleTweak { get; }
    }

    private sealed class CustomMaterialsRow
    {
        internal CustomMaterialsRow(string path) { Object = path; }
        internal string Object { get; }
        internal List<MaterialEntryRow> Entries { get; } = new();
    }

    private sealed class MaterialEntryRow
    {
        internal MaterialEntryRow(string from, string fromShader, string to, string toShader)
        { From = from; FromShader = fromShader; To = to; ToShader = toShader; }
        internal string From { get; }
        internal string FromShader { get; }
        internal string To { get; }
        internal string ToShader { get; }
    }
}

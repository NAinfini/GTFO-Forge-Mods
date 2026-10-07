using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

public static class ForgeEnergyDiscBuild
{
    public static void Build(string output)
    {
        if (Application.unityVersion != "2019.4.21f1") throw new InvalidOperationException("Use Unity 2019.4.21f1.");
        const string asset = "Assets/Projectiles/ricochet-disc.prefab", file = "forge-energy-disc.bundle";
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
        if (source == null) throw new FileNotFoundException(asset);
        var staging = Path.GetFullPath("Library/EnergyDiscBuild");
        Directory.CreateDirectory(staging);
        var build = new AssetBundleBuild { assetBundleName = file, assetNames = new[] { asset }, addressableNames = new[] { "forge_ricochet_disc" } };
        if (BuildPipeline.BuildAssetBundles(staging, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression |
            BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64) == null)
            throw new InvalidOperationException("Disc bundle build failed.");
        var path = Path.Combine(staging, file);
        var bundle = AssetBundle.LoadFromFile(path);
        if (bundle == null) throw new InvalidDataException("Disc bundle does not load.");
        try
        {
            var prefab = bundle.LoadAsset<GameObject>("forge_ricochet_disc");
            var expected = source.GetComponentInChildren<MeshFilter>().sharedMesh;
            var mesh = prefab == null ? null : prefab.GetComponentInChildren<MeshFilter>().sharedMesh;
            if (mesh == null || mesh.subMeshCount != 4 || !mesh.vertices.SequenceEqual(expected.vertices) ||
                !mesh.normals.SequenceEqual(expected.normals) || !mesh.colors.SequenceEqual(expected.colors) || mesh.bounds != expected.bounds)
                throw new InvalidDataException("Bundled disc geometry differs from its native asset.");
            for (var i = 0; i < 4; i++)
                if (!mesh.GetTriangles(i).SequenceEqual(expected.GetTriangles(i))) throw new InvalidDataException("Disc winding differs.");
            var renderer = prefab.GetComponentInChildren<MeshRenderer>();
            var original = source.GetComponentInChildren<MeshRenderer>();
            if (renderer.sharedMaterials.Length != 4 || renderer.shadowCastingMode != original.shadowCastingMode ||
                renderer.receiveShadows != original.receiveShadows || renderer.lightProbeUsage != original.lightProbeUsage ||
                renderer.reflectionProbeUsage != original.reflectionProbeUsage)
                throw new InvalidDataException("Disc surface settings differ.");
            for (var i = 0; i < 4; i++)
            {
                var material = renderer.sharedMaterials[i]; var reference = original.sharedMaterials[i];
                if (material.name != reference.name || material.renderQueue != reference.renderQueue ||
                    !material.shaderKeywords.SequenceEqual(reference.shaderKeywords)) throw new InvalidDataException("Disc material differs.");
                var saved = new SerializedObject(material).FindProperty("m_SavedProperties");
                var expectedProperties = new SerializedObject(reference).FindProperty("m_SavedProperties");
                if (saved.FindPropertyRelative("m_Floats").arraySize < 6 || saved.FindPropertyRelative("m_Colors").arraySize < 2)
                    throw new InvalidDataException("Disc material parameters are missing.");
                foreach (var property in new[] { "m_Floats", "m_Colors", "m_TexEnvs" })
                {
                    var values = saved.FindPropertyRelative(property); var expectedValues = expectedProperties.FindPropertyRelative(property);
                    if (values.arraySize != expectedValues.arraySize) throw new InvalidDataException("Disc material property count differs.");
                    for (var j = 0; j < values.arraySize; j++)
                    {
                        var value = values.GetArrayElementAtIndex(j); var expectedValue = expectedValues.GetArrayElementAtIndex(j);
                        if (value.FindPropertyRelative("first").stringValue != expectedValue.FindPropertyRelative("first").stringValue)
                            throw new InvalidDataException("Disc material property name differs.");
                        var data = value.FindPropertyRelative("second"); var expectedData = expectedValue.FindPropertyRelative("second");
                        if (property == "m_Floats" ? data.floatValue != expectedData.floatValue : property == "m_Colors" ? data.colorValue != expectedData.colorValue :
                            data.FindPropertyRelative("m_Texture").objectReferenceValue != expectedData.FindPropertyRelative("m_Texture").objectReferenceValue ||
                            data.FindPropertyRelative("m_Scale").vector2Value != expectedData.FindPropertyRelative("m_Scale").vector2Value ||
                            data.FindPropertyRelative("m_Offset").vector2Value != expectedData.FindPropertyRelative("m_Offset").vector2Value)
                            throw new InvalidDataException("Disc material property value differs.");
                    }
                }
            }
            var trail = prefab.GetComponent<TrailRenderer>(); var sourceTrail = source.GetComponent<TrailRenderer>();
            if (trail == null || trail.time != sourceTrail.time || trail.startWidth != sourceTrail.startWidth ||
                trail.endWidth != sourceTrail.endWidth || trail.minVertexDistance != sourceTrail.minVertexDistance ||
                trail.startColor != sourceTrail.startColor || trail.endColor != sourceTrail.endColor || trail.emitting)
                throw new InvalidDataException("Disc trail differs.");
        }
        finally { bundle.Unload(true); }
        Directory.CreateDirectory(output);
        File.Copy(path, Path.Combine(output, file), true);
    }
}

// This shader stores material properties; runtime binds GTFO's shaders before any draw.
public sealed class DiscParameterShader : IPreprocessShaders
{
    public int callbackOrder { get { return 0; } }
    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> variants)
    {
        if (shader.name == "ForgeEnergyLab/DiscParameters") variants.Clear();
    }
}

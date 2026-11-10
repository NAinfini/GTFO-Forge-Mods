using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Builds one bundle (Unity 2019.4.21f1, the game's version) from the stage folder that export_blender.py wrote:
/// one prefab per model folder (renderers carry no
/// materials; the plugin builds them with the game's gear shader), the textures, flat 4x4 textures for untextured
/// surfaces, the standalone textures of the stage's optional textures/ folder (effect sprites, imported uncrunched with alpha)
/// and the profile TextAsset that ties renderers to surfaces and lists the standalone textures.
/// Environment: FORGE_ORDNANCE_STAGE (input), FORGE_ORDNANCE_OUT (output folder) and FORGE_ORDNANCE_BUNDLE (the bundle's file
/// name, e.g. forge-ordnance.bundle; add-on packages pass their own). The manifest beside it is <name>.manifest.json.</summary>
public static class OrdnanceBundleBuild
{
    [Serializable] public sealed class StageObject { public string name, parent, mesh; public float[] position, rotation, scale; public int triangles; public int[] submeshTriangles; public string[] surfaces; }
    [Serializable] public sealed class StageSurface { public string name, albedo, metallic, normal; public float[] color, emission; public float metallicValue, smoothness; }
    [Serializable] public sealed class StageModel { public int format; public string prefab, source; public StageObject[] objects; public StageSurface[] surfaces; }

    [Serializable] public sealed class ProfileRenderer { public string name; public string[] surfaces; }
    [Serializable] public sealed class ProfilePrefab { public string name; public ProfileRenderer[] renderers; public string[] anchors; }
    [Serializable] public sealed class ProfileSurface { public string key, albedo, metallic, normal, emission; public float[] emissionColor; }
    [Serializable] public sealed class Profile { public int format; public ProfilePrefab[] prefabs; public ProfileSurface[] surfaces; public string[] textures; }
    [Serializable] public sealed class Manifest { public string unity, bundle, sha256; public long bytes; public Profile profile; }

    static string BundleName = "";
    const string Stage = "Assets/OrdnanceStage";
    const string Generated = "Assets/OrdnanceGenerated";

    public static void Build()
    {
        try { Run(); EditorApplication.Exit(0); }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void Run()
    {
        if (Application.unityVersion != "2019.4.21f1") throw new InvalidOperationException("Use the game's Unity 2019.4.21f1.");
        var input = Required("FORGE_ORDNANCE_STAGE");
        var output = Required("FORGE_ORDNANCE_OUT");
        BundleName = Environment.GetEnvironmentVariable("FORGE_ORDNANCE_BUNDLE") ?? "";
        if (!BundleName.EndsWith(".bundle", StringComparison.Ordinal) || BundleName.Length == ".bundle".Length || BundleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Set FORGE_ORDNANCE_BUNDLE to the bundle's file name, ending in .bundle.");
        var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        foreach (var folder in new[] { Stage, Generated })
        {
            var full = Path.Combine(project, folder);
            if (Directory.Exists(full)) Directory.Delete(full, true);
            if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
        }
        Copy(input, Path.Combine(project, Stage));
        Directory.CreateDirectory(Path.Combine(project, Generated));

        var models = Directory.GetDirectories(Path.Combine(project, Stage, "models")).OrderBy(d => d, StringComparer.Ordinal)
            .Select(d => (folder: d, model: JsonUtility.FromJson<StageModel>(File.ReadAllText(Path.Combine(d, "manifest.json"))))).ToList();
        if (models.Count == 0) throw new InvalidDataException("No models in the stage folder.");

        // Flat textures for surfaces without image textures, written before the import pass.
        var profileSurfaces = new List<ProfileSurface>();
        foreach (var (folder, model) in models)
        {
            if (model.format != 1 || string.IsNullOrEmpty(model.prefab)) throw new InvalidDataException("Bad stage manifest in " + folder);
            foreach (var s in model.surfaces)
            {
                var key = model.prefab + "/" + s.name;
                var stem = model.prefab + "-" + s.name;
                if (string.IsNullOrEmpty(s.albedo)) { s.albedo = stem + "-albedo.png"; Flat(Path.Combine(folder, s.albedo), Gamma(s.color)); }
                if (string.IsNullOrEmpty(s.metallic)) { s.metallic = stem + "-metallic.png"; Flat(Path.Combine(folder, s.metallic), new Color(s.metallicValue, s.smoothness, 0, s.smoothness)); }
                var emissive = s.emission != null && s.emission.Length == 3 && s.emission.Any(v => v > 0);
                // The export writes an emission mask when the source marks only part of a texture as lit; otherwise the whole surface glows.
                if (emissive && !File.Exists(Path.Combine(folder, stem + "-emission.png"))) Flat(Path.Combine(folder, stem + "-emission.png"), Color.white);
                profileSurfaces.Add(new ProfileSurface
                {
                    key = key,
                    albedo = Stem(s.albedo),
                    metallic = Stem(s.metallic),
                    normal = string.IsNullOrEmpty(s.normal) ? "" : Stem(s.normal),
                    emission = emissive ? stem + "-emission" : "",
                    emissionColor = emissive ? s.emission : new float[] { 0, 0, 0 }
                });
            }
        }
        AssetDatabase.Refresh();

        var assets = new List<(string path, string name)>();
        var profilePrefabs = new List<ProfilePrefab>();
        foreach (var (folder, model) in models)
        {
            var assetFolder = Stage + "/models/" + Path.GetFileName(folder);
            foreach (var s in model.surfaces)
            {
                assets.Add(ImportTexture(assetFolder + "/" + s.albedo, TextureImporterType.Default, true));
                assets.Add(ImportTexture(assetFolder + "/" + s.metallic, TextureImporterType.Default, false));
                if (!string.IsNullOrEmpty(s.normal)) assets.Add(ImportTexture(assetFolder + "/" + s.normal, TextureImporterType.NormalMap, false));
                var emission = assetFolder + "/" + model.prefab + "-" + s.name + "-emission.png";
                if (File.Exists(Path.Combine(project, emission))) assets.Add(ImportTexture(emission, TextureImporterType.Default, true));
            }
            var root = new GameObject(model.prefab);
            var renderers = new List<ProfileRenderer>();
            var anchors = new List<string>();
            try
            {
                var nodes = model.objects.ToDictionary(o => o.name, o => new GameObject(o.name));
                foreach (var o in model.objects)
                {
                    var child = nodes[o.name];
                    child.transform.SetParent(string.IsNullOrEmpty(o.parent) ? root.transform : nodes[o.parent].transform, false);
                    child.transform.localPosition = Vector(o.position);
                    child.transform.localRotation = new Quaternion(o.rotation[0], o.rotation[1], o.rotation[2], o.rotation[3]);
                    child.transform.localScale = Vector(o.scale);
                    if (string.IsNullOrEmpty(o.mesh)) { anchors.Add(PathOf(o, model)); continue; }
                    var mesh = ReadMesh(Path.Combine(folder, o.mesh), o);
                    mesh.name = model.prefab + "-" + o.name;
                    var winding = WindingFraction(mesh);
                    if (winding < 0.9) throw new InvalidDataException(mesh.name + ": only " + winding.ToString("F3") + " of the area faces along its normals.");
                    mesh.UploadMeshData(true);
                    AssetDatabase.CreateAsset(mesh, Generated + "/" + mesh.name + ".asset");
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    child.AddComponent<MeshRenderer>().sharedMaterials = new Material[0];
                    renderers.Add(new ProfileRenderer { name = PathOf(o, model), surfaces = o.surfaces.Select(s => model.prefab + "/" + s).ToArray() });
                }
                var prefabPath = Generated + "/" + model.prefab + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                assets.Add((prefabPath, model.prefab));
                profilePrefabs.Add(new ProfilePrefab { name = model.prefab, renderers = renderers.ToArray(), anchors = anchors.ToArray() });
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        var standalone = new List<string>();
        var textureFolder = Path.Combine(project, Stage, "textures");
        if (Directory.Exists(textureFolder))
            foreach (var file in Directory.GetFiles(textureFolder, "*.png").OrderBy(f => f, StringComparer.Ordinal))
            {
                var asset = ImportEffect(Stage + "/textures/" + Path.GetFileName(file));
                assets.Add(asset);
                standalone.Add(asset.Item2);
            }
        if (BundleName == "forge-ordnance.bundle") { BuildPodImpact(assets, profilePrefabs); BuildFxSlot(assets, profilePrefabs); }
        var profile = new Profile
        {
            format = 1,
            prefabs = profilePrefabs.ToArray(),
            surfaces = profileSurfaces.ToArray(),
            textures = standalone.ToArray()
        };
        File.WriteAllText(Path.Combine(project, Generated, "ordnance-profile.json"), JsonUtility.ToJson(profile, true));
        AssetDatabase.Refresh();
        assets.Add((Generated + "/ordnance-profile.json", "ordnance-profile"));
        AssetDatabase.SaveAssets();

        var staging = Path.Combine(project, "Library/OrdnanceBundle");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = assets.Select(a => a.path).ToArray(), addressableNames = assets.Select(a => a.name).ToArray() };
        if (BuildPipeline.BuildAssetBundles(staging, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64) == null)
            throw new InvalidOperationException("Unity bundle build failed.");
        var built = Path.Combine(staging, BundleName);
        Verify(built, profile, assets.Count(a => a.path.EndsWith(".png")));
        Directory.CreateDirectory(output);
        var destination = Path.Combine(output, BundleName);
        File.Copy(built, destination, true);
        var length = new FileInfo(destination).Length;
        var hash = Hash(destination);
        File.WriteAllText(Path.Combine(output, Path.GetFileNameWithoutExtension(BundleName) + ".manifest.json"),
            JsonUtility.ToJson(new Manifest { unity = Application.unityVersion, bundle = BundleName, sha256 = hash, bytes = length, profile = profile }, true));
        Debug.Log("ORDNANCE_BUNDLE_VERIFIED " + BundleName + " " + length + " bytes " + hash);
    }

    /// <summary>The node's path under the prefab, what Transform.Find takes.</summary>
    static string PathOf(StageObject o, StageModel model)
        => string.IsNullOrEmpty(o.parent) ? o.name : PathOf(model.objects.First(p => p.name == o.parent), model) + "/" + o.name;

    static string Required(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Set " + variable + ".");
        return Path.GetFullPath(value);
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(from)) Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    static string Stem(string file) => Path.GetFileNameWithoutExtension(file);
    static Vector3 Vector(float[] v) => new Vector3(v[0], v[1], v[2]);
    static Color Gamma(float[] linear) => new Color(Mathf.LinearToGammaSpace(linear[0]), Mathf.LinearToGammaSpace(linear[1]), Mathf.LinearToGammaSpace(linear[2]), linear.Length > 3 ? linear[3] : 1f);

    static void Flat(string path, Color colour)
    {
        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        texture.SetPixels(Enumerable.Repeat(colour, 16).ToArray());
        texture.Apply();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
    }

    // Author complete modules here: GTFO strips many of their runtime managed property setters.
    static void BuildPodImpact(List<(string path, string name)> assets, List<ProfilePrefab> prefabs)
    {
        var root = new GameObject("pod-impact");
        try
        {
            AddImpactParticles(root, "dust-ring", 384, true, 0f);
            AddImpactParticles(root, "debris-puffs", 96, true, .8f);
            var path = Generated + "/pod-impact.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            assets.Add((path, "pod-impact"));
            prefabs.Add(new ProfilePrefab { name = "pod-impact", renderers = new ProfileRenderer[0], anchors = new string[0] });
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    /// <summary>The six reusable effect systems every presentation moment shares (ForgeVisual's particle pool Ordnance:ordnance-fx: active
    /// prefab, looping world-space systems with emission off). maxParticles is the budget of every effect together, the capacity the plugin
    /// passes to ForgeVisual (OrdnanceFx.*Capacity): ten effects' worth. Fade, growth, flipbook and spin are baked here; the plugin only emits.</summary>
    internal static readonly string[] FxNames = { "flame", "smoke", "spark", "flare", "ring", "rock" };
    internal static readonly int[] FxLimits = { 160, 240, 160, 40, 40, 720 };

    static void BuildFxSlot(List<(string path, string name)> assets, List<ProfilePrefab> prefabs)
    {
        var root = new GameObject("ordnance-fx");
        try
        {
            // flame (Unity Labs Flame02, 8 x 4 cells): drawn additively; the 32-frame flipbook plays over the particle's life.
            AddFx(root, "flame", 160, 8, 4, true, 0f, -.1f, 0f, new[] { 0f, .2f, 1f }, new[] { .7f, 1f, .6f },
                new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(new Color(1f, .9f, .7f), .5f), new GradientColorKey(new Color(.8f, .45f, .25f), 1f) },
                new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, .08f), new GradientAlphaKey(.8f, .6f), new GradientAlphaKey(0, 1) });
            // smoke (Particle Pack Starter Assets SmokePuff01, 5 x 5 cells): the 25-frame flipbook plays over the particle's life, grows and thins out.
            AddFx(root, "smoke", 240, 5, 5, true, 0f, -.04f, 30f, new[] { 0f, 1f }, new[] { .5f, 1.6f }, White(), Alpha(1f));
            // ring (Free Quick Effects Vol 1 Circle01_v1): the beacon shock ring lying flat on the ground, additive; it runs out fast and fades.
            AddFx(root, "ring", 40, 1, 1, false, 0f, 0f, 0f, new[] { 0f, .3f, 1f }, new[] { .08f, .7f, 1f }, White(), new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, .05f), new GradientAlphaKey(.7f, .4f), new GradientAlphaKey(0, 1) }, true);


            AddFx(root, "spark", 160, 1, 1, false, 0f, 1f, 0f, new[] { 0f, 1f }, new[] { 1f, .1f }, White(), Alpha(1f));
            AddFx(root, "flare", 40, 1, 1, false, 0f, 0f, 0f, new[] { 0f, 1f }, new[] { .35f, 1.2f }, White(), new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, .15f), new GradientAlphaKey(0, 1) });
            // rock (tools/make_rocks.py, 2 x 2 faceted chunks, two concrete and two earth): thrown out of the ground where a pod lands. One random chunk per particle, tumbling,
            // falling under gravity, opaque until the last quarter of its life and then fading out.
            AddFx(root, "rock", 720, 2, 2, false, 1f, 1.6f, 260f, new[] { 0f, .75f, 1f }, new[] { 1f, 1f, .7f }, White(), new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, .03f), new GradientAlphaKey(1, .75f), new GradientAlphaKey(0, 1) });
            var path = Generated + "/ordnance-fx.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            assets.Add((path, "ordnance-fx"));
            prefabs.Add(new ProfilePrefab { name = "ordnance-fx", renderers = new ProfileRenderer[0], anchors = new string[0] });
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    static GradientColorKey[] White() => new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) };
    static GradientAlphaKey[] Alpha(float peak) => new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(peak, .1f), new GradientAlphaKey(0, 1) };

    static void AddFx(GameObject root, string name, int limit, int tilesX, int tilesY, bool flipbook, float startFrameMax, float gravity, float spin,
        float[] sizeTimes, float[] sizes, GradientColorKey[] colours, GradientAlphaKey[] alphas, bool flat = false)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(root.transform, false);
        var particles = obj.AddComponent<ParticleSystem>();
        var main = particles.main;
        main.playOnAwake = false;
        main.loop = true;
        main.duration = 4f;
        main.maxParticles = limit;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = gravity;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        var emission = particles.emission; emission.enabled = false;
        var shape = particles.shape; shape.enabled = false;
        var colour = particles.colorOverLifetime;
        colour.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(colours, alphas);
        colour.color = gradient;
        var size = particles.sizeOverLifetime;
        size.enabled = true;
        var curve = new AnimationCurve();
        for (var i = 0; i < sizes.Length; i++) curve.AddKey(sizeTimes[i], sizes[i]);
        size.size = new ParticleSystem.MinMaxCurve(1f, curve);
        if (spin > 0f)
        {
            var rotation = particles.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-spin * Mathf.Deg2Rad, spin * Mathf.Deg2Rad);
        }
        if (tilesX * tilesY > 1)
        {
            var sheet = particles.textureSheetAnimation;
            sheet.enabled = true;
            sheet.numTilesX = tilesX;
            sheet.numTilesY = tilesY;
            if (flipbook) { sheet.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0, 1, 1)); sheet.startFrame = 0f; }
            else { sheet.frameOverTime = 0f; sheet.startFrame = new ParticleSystem.MinMaxCurve(0f, startFrameMax); }
        }
        var renderer = obj.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = null; // Runtime assigns TryTexture-backed materials after shader availability.
        renderer.renderMode = flat ? ParticleSystemRenderMode.HorizontalBillboard : ParticleSystemRenderMode.Billboard;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    static void AddImpactParticles(GameObject root, string name, int limit, bool atlas, float gravity)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(root.transform, false);
        var particles = obj.AddComponent<ParticleSystem>();
        var main = particles.main;
        main.playOnAwake = false;
        main.loop = true;
        main.duration = 2f;
        main.maxParticles = limit;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = gravity;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        var emission = particles.emission; emission.enabled = false;
        var shape = particles.shape; shape.enabled = false;
        var colour = particles.colorOverLifetime;
        colour.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
            new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, .08f), new GradientAlphaKey(0, 1) });
        colour.color = fade;
        var size = particles.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, atlas ? .45f : 1f, 1, atlas ? 1.8f : .1f));
        if (atlas)
        {
            var sheet = particles.textureSheetAnimation;
            sheet.enabled = true;
            sheet.numTilesX = sheet.numTilesY = 2;
            sheet.frameOverTime = 0f;
            sheet.startFrame = new ParticleSystem.MinMaxCurve(0f, 1f);
        }
        var renderer = obj.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = null; // Runtime assigns TryTexture-backed materials after shader availability.
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    static (string, string) ImportTexture(string path, TextureImporterType type, bool srgb)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) throw new FileNotFoundException(path);
        importer.textureType = type;
        importer.sRGBTexture = srgb;
        importer.mipmapEnabled = true;
        importer.isReadable = false;
        importer.maxTextureSize = 2048;
        importer.textureCompression = TextureImporterCompression.Compressed;
        importer.crunchedCompression = true;
        importer.compressionQuality = 75;
        importer.SaveAndReimport();
        return (path, Stem(path));
    }

    /// <summary>An effect sprite: colour plus alpha, mipmapped, compressed without crunch (crunch smears soft alpha edges).</summary>
    static (string, string) ImportEffect(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) throw new FileNotFoundException(path);
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = true;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.mipmapEnabled = true;
        importer.isReadable = false;
        importer.maxTextureSize = 2048;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
        importer.crunchedCompression = false;
        importer.SaveAndReimport();
        return (path, Stem(path));
    }

    static Mesh ReadMesh(string path, StageObject o)
    {
        using (var r = new BinaryReader(File.OpenRead(path)))
        {
            var n = r.ReadInt32();
            var subs = r.ReadInt32();
            if (n < 3 || n > 1000000 || subs < 1 || subs > 16 || o.surfaces == null || subs != o.surfaces.Length) throw new InvalidDataException("Bad mesh header: " + path);
            var counts = new int[subs];
            for (var i = 0; i < subs; i++) counts[i] = r.ReadInt32();
            if (r.BaseStream.Length != 8L + 4L * subs + n * 32L + counts.Sum(c => 4L * c)) throw new InvalidDataException("Bad mesh length: " + path);
            var p = new Vector3[n]; var normals = new Vector3[n]; var uv = new Vector2[n];
            for (var i = 0; i < n; i++)
            {
                p[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                normals[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
            }
            var mesh = new Mesh { indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = p; mesh.normals = normals; mesh.uv = uv;
            mesh.subMeshCount = subs;
            var total = 0;
            for (var s = 0; s < subs; s++)
            {
                var indices = new int[counts[s]];
                for (var i = 0; i < indices.Length; i++) { indices[i] = r.ReadInt32(); if (indices[i] < 0 || indices[i] >= n) throw new InvalidDataException("Index out of range: " + path); }
                mesh.SetTriangles(indices, s);
                total += indices.Length / 3;
            }
            if (total != o.triangles) throw new InvalidDataException("Triangle count differs from the manifest: " + path);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }
    }

    /// <summary>Unity front faces are clockwise, so Cross(v1-v0, v2-v0) must point along the supplied normals.</summary>
    static double WindingFraction(Mesh mesh)
    {
        var v = mesh.vertices; var n = mesh.normals; double good = 0, all = 0;
        for (var s = 0; s < mesh.subMeshCount; s++)
        {
            var t = mesh.GetTriangles(s);
            for (var i = 0; i < t.Length; i += 3)
            {
                var c = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                var area = c.magnitude;
                all += area;
                if (Vector3.Dot(c, n[t[i]] + n[t[i + 1]] + n[t[i + 2]]) > 0) good += area;
            }
        }
        return all > 0 ? good / all : 0;
    }

    static void Verify(string file, Profile profile, int textures)
    {
        var bundle = AssetBundle.LoadFromFile(file);
        if (bundle == null) throw new InvalidDataException("The built bundle does not load.");
        try
        {
            var text = bundle.LoadAsset<TextAsset>("ordnance-profile");
            if (text == null || JsonUtility.FromJson<Profile>(text.text).prefabs.Length != profile.prefabs.Length) throw new InvalidDataException("Bundled profile missing or different.");
            foreach (var p in profile.prefabs)
            {
                var prefab = bundle.LoadAsset<GameObject>(p.name);
                if (prefab == null) throw new InvalidDataException("Prefab missing: " + p.name);
                if (p.name == "pod-impact")
                {
                    if (!prefab.activeSelf) throw new InvalidDataException("Pod impact prefab must be active.");
                    var names = new[] { "dust-ring", "debris-puffs" };
                    var limits = new[] { 384, 96 };
                    for (var i = 0; i < names.Length; i++)
                    {
                        var particles = prefab.transform.Find(names[i])?.GetComponent<ParticleSystem>();
                        if (particles == null || particles.main.maxParticles != limits[i] || particles.main.playOnAwake
                            || !particles.main.loop || particles.main.simulationSpace != ParticleSystemSimulationSpace.World
                            || particles.emission.enabled || particles.shape.enabled)
                            throw new InvalidDataException("Pod impact particle budget/settings differ: " + names[i]);
                    }
                }
                if (p.name == "ordnance-fx")
                {
                    if (!prefab.activeSelf) throw new InvalidDataException("Effect prefab must be active.");
                    for (var i = 0; i < FxNames.Length; i++)
                    {
                        var particles = prefab.transform.Find(FxNames[i])?.GetComponent<ParticleSystem>();
                        if (particles == null || particles.main.maxParticles != FxLimits[i] || particles.main.playOnAwake || !particles.main.loop
                            || particles.main.simulationSpace != ParticleSystemSimulationSpace.World || particles.emission.enabled || particles.shape.enabled)
                            throw new InvalidDataException("Effect particle budget/settings differ: " + FxNames[i]);
                    }
                }
                foreach (var r in p.renderers)
                {
                    var t = prefab.transform.Find(r.name);
                    var filter = t == null ? null : t.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null || filter.sharedMesh.subMeshCount != r.surfaces.Length) throw new InvalidDataException("Renderer differs from the profile: " + p.name + "/" + r.name);
                    if (t.GetComponent<MeshRenderer>().sharedMaterials.Any(m => m != null)) throw new InvalidDataException(p.name + "/" + r.name + " carries a material.");
                }
                foreach (var a in p.anchors) if (prefab.transform.Find(a) == null) throw new InvalidDataException("Anchor missing: " + p.name + "/" + a);
            }
            foreach (var s in profile.surfaces)
                foreach (var name in new[] { s.albedo, s.metallic, s.normal, s.emission }.Where(n => !string.IsNullOrEmpty(n)))
                    if (bundle.LoadAsset<Texture2D>(name) == null) throw new InvalidDataException("Texture missing: " + name);
            foreach (var name in profile.textures)
                if (bundle.LoadAsset<Texture2D>(name) == null) throw new InvalidDataException("Standalone texture missing: " + name);
            if (bundle.LoadAllAssets<Texture2D>().Length != textures) throw new InvalidDataException("Texture count differs.");
        }
        finally { bundle.Unload(true); }
    }

    static string Hash(string file)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(file))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}

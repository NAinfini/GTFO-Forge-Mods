#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

/// <summary>Builds the exosuit effects (Unity 2019.4.21f1, the game's version) into frames-fx.bundle and writes the plugin's
/// embedded copy (Native/FxBundle.Generated.cs). One prefab, "FramesFx", holds twenty particle systems, one per kind of
/// effect. Every system is authored here (shape, speed, size, colour and size over life, texture sheet) because the game's
/// interop strips the module setters; the plugin only positions a system and emits into it. Each system loops, plays and has no
/// emission of its own, simulates in world space, and its maxParticles is the budget for everything that draws into it. The shipped
/// prefab has no materials: the bundle carries the prefab and the textures, and the plugin draws each system with ForgeVisual's particle
/// shader (texture and blend per system, FrameFx.Table). For the preview the build also keeps, in the Unity project only, a copy of the
/// prefab with materials made from that shader.
/// Environment: FORGE_FX_TEXTURES (the staged textures), FORGE_FX_SHADER (ForgeVisual's particle .shader, preview only), FORGE_FX_GENERATED (the .cs to write).</summary>
public static class FrameFxBuild
{
    public const string PrefabName = "FramesFx";
    const string Folder = "Assets/FrameFx", ShipFolder = "Assets/FrameFx/Ship";

    /// <summary>How one effect kind is authored. Lifetime, speed and size are random between their two values.</summary>
    public sealed class Spec
    {
        public string Name, Texture;
        public int Tx = 1, Ty = 1, Max;
        public bool Add, Animate, Stretch, Flat, Upright;
        public Vector2 Life, Speed, Size;
        public float Gravity, Drag, Spin = 0.5f, FadeIn = 0.1f, Hold = 0.4f;
        public ParticleSystemShapeType Shape = ParticleSystemShapeType.Sphere;
        public float Radius = 0.1f, Angle = 25f;
        public Color[] Tint = { Color.white };
        public Vector2 Grow = new Vector2(1f, 1f);
        public bool EaseOut;
        public float LenScale = 2f, VelScale = 0.06f, FrameTo = 1f;
    }

    static Color C(float r, float g, float b, float a = 1f) => new Color(r, g, b, a);
    static Vector2 V(float a, float b) => new Vector2(a, b);

    /// <summary>The twenty effect kinds. Names are the contract with Native/FrameFx.cs.</summary>
    public static readonly Spec[] Specs =
    {
        // Landing and siege dust: a ring of tan dust thrown out along the ground, slowing fast.
        new Spec { Name = "BurstDust", Texture = "fx-mist", Tx = 2, Ty = 2, Max = 80, Life = V(1.4f, 2.2f), Speed = V(3f, 6.5f), Size = V(1.6f, 2.8f),
            Shape = ParticleSystemShapeType.Circle, Radius = 0.7f, Drag = 0.1f, Grow = V(0.4f, 1.5f), FadeIn = 0.08f, Hold = 0.25f,
            Tint = new[] { C(0.7f, 0.62f, 0.5f, 0.8f), C(0.64f, 0.57f, 0.46f, 0.6f), C(0.58f, 0.52f, 0.44f, 0.3f) } },
        // A dust column rising where a suit lands.
        new Spec { Name = "ColumnSmoke", Texture = "fx-smoke", Tx = 5, Ty = 3, Max = 40, Life = V(2f, 3f), Speed = V(1f, 2.8f), Size = V(2f, 3.4f),
            Shape = ParticleSystemShapeType.Cone, Radius = 0.45f, Angle = 16f, Drag = 0.25f, Grow = V(0.5f, 1.6f), Animate = true, FadeIn = 0.1f, Hold = 0.3f,
            Tint = new[] { C(0.5f, 0.47f, 0.42f, 0.65f), C(0.43f, 0.41f, 0.38f, 0.5f), C(0.35f, 0.34f, 0.33f, 0.2f) } },
        // Stones thrown up and falling back.
        new Spec { Name = "Chunks", Texture = "fx-disc", Max = 60, Life = V(1f, 1.6f), Speed = V(4f, 8.5f), Size = V(0.09f, 0.24f), Gravity = 1.1f,
            Shape = ParticleSystemShapeType.Cone, Radius = 0.5f, Angle = 55f, Spin = 2f, FadeIn = 0.02f, Hold = 0.75f,
            Tint = new[] { C(0.34f, 0.29f, 0.23f, 1f), C(0.28f, 0.24f, 0.2f, 1f), C(0.25f, 0.22f, 0.19f, 0.9f) } },
        // Sparks and embers, stretched along their flight.
        new Spec { Name = "Ember", Texture = "fx-dot", Max = 100, Life = V(0.4f, 0.95f), Speed = V(4f, 10f), Size = V(0.07f, 0.15f), Gravity = 0.7f, Stretch = true, Add = true,
            Shape = ParticleSystemShapeType.Cone, Radius = 0.3f, Angle = 60f, FadeIn = 0.02f, Hold = 0.4f,
            Tint = new[] { C(1f, 0.92f, 0.55f, 1f), C(1f, 0.5f, 0.12f, 0.9f), C(0.6f, 0.12f, 0.02f, 0f) } },
        // A flat ring that grows (the plugin sets its size, life and colour per emission): shock, scan wave.
        new Spec { Name = "Ring", Texture = "fx-ring", Max = 16, Life = V(0.6f, 0.6f), Speed = V(0f, 0f), Size = V(1f, 1f), Add = true, Flat = true, EaseOut = true,
            Grow = V(0.04f, 1f), Spin = 0f, FadeIn = 0.04f, Hold = 0.2f },
        // The barrier's push pulse: a ring that starts at the dome's wall (about half the final size) and spreads out.
        new Spec { Name = "PushRing", Texture = "fx-ring", Max = 8, Life = V(0.5f, 0.5f), Speed = V(0f, 0f), Size = V(1f, 1f), Add = true, Flat = true, EaseOut = true,
            Grow = V(0.5f, 1f), Spin = 0f, FadeIn = 0.04f, Hold = 0.15f },
        // Blue-white thruster flame; the plugin points the system's forward along the jet.
        new Spec { Name = "Thruster", Texture = "fx-fireball-grey", Tx = 8, Ty = 8, Animate = true, Max = 60, Life = V(0.14f, 0.14f), Speed = V(0f, 0f), Size = V(0.5f, 0.9f), Add = true,
            Grow = V(1f, 0.55f), FadeIn = 0.05f, Hold = 0.3f,
            Tint = new[] { C(0.55f, 0.75f, 1f, 0.7f), C(0.2f, 0.45f, 1f, 0.6f), C(0.1f, 0.2f, 0.9f, 0f) } },
        // The thruster cone: a stretched billboard along the flame's velocity (the plugin gives each its velocity), narrow bright root, widening and fading.
        new Spec { Name = "JetCone", Texture = "fx-jet", Max = 120, Life = V(0.12f, 0.12f), Speed = V(0f, 0f), Size = V(1f, 1f), Add = true, Stretch = true, LenScale = 2f, VelScale = 0.01f, Spin = 0f,
            Grow = V(1f, 0.8f), FadeIn = 0.05f, Hold = 0.5f,
            Tint = new[] { C(0.85f, 0.95f, 1f, 1f), C(0.3f, 0.55f, 1f, 0.85f), C(0.1f, 0.2f, 0.9f, 0f) } },
        // Orange blast flame (rocket back-blast); forward is the direction of the blast.
        new Spec { Name = "FlameBlast", Texture = "fx-fireball", Tx = 8, Ty = 8, Animate = true, Max = 60, Life = V(0.2f, 0.42f), Speed = V(5f, 10f), Size = V(0.85f, 1.5f), Add = true,
            Shape = ParticleSystemShapeType.Cone, Radius = 0.08f, Angle = 20f, Grow = V(0.8f, 1.5f), FadeIn = 0.05f, Hold = 0.25f,
            Tint = new[] { C(1f, 0.95f, 0.7f, 1f), C(1f, 0.55f, 0.15f, 0.8f), C(0.5f, 0.1f, 0f, 0f) } },
        // A rocket's own tail flame.
        new Spec { Name = "FlameSmall", Texture = "fx-fireball", Tx = 8, Ty = 8, Animate = true, Max = 60, Life = V(0.14f, 0.26f), Speed = V(0.5f, 1.5f), Size = V(0.14f, 0.24f), Add = true,
            Shape = ParticleSystemShapeType.Cone, Radius = 0.02f, Angle = 10f, Grow = V(1f, 0.3f), FadeIn = 0.05f, Hold = 0.3f,
            Tint = new[] { C(1f, 0.95f, 0.7f, 1f), C(1f, 0.55f, 0.15f, 0.8f), C(0.5f, 0.1f, 0f, 0f) } },
        // The fireball of a suit's destruction.
        new Spec { Name = "Blast", Texture = "fx-blast", Tx = 5, Ty = 5, Animate = true, FrameTo = 0.4f, Add = true, Max = 6, Life = V(1.1f, 1.1f), Speed = V(0f, 0f), Size = V(1f, 1f), Spin = 0f,
            Grow = V(0.8f, 1f), FadeIn = 0.02f, Hold = 0.7f },
        new Spec { Name = "Fireball", Texture = "fx-fireball", Tx = 8, Ty = 8, Animate = true, Max = 40, Life = V(0.6f, 1.1f), Speed = V(1f, 3.2f), Size = V(2.8f, 4.8f), Add = true,
            Shape = ParticleSystemShapeType.Sphere, Radius = 0.8f, Drag = 0.2f, Grow = V(0.45f, 1.3f), FadeIn = 0.04f, Hold = 0.25f,
            Tint = new[] { C(1f, 0.95f, 0.75f, 1f), C(1f, 0.55f, 0.14f, 0.8f), C(0.45f, 0.08f, 0f, 0f) } },
        // Small flames licking over a wreck.
        new Spec { Name = "WreckFlame", Texture = "fx-flame", Tx = 8, Ty = 8, Animate = true, Upright = true, Max = 40, Life = V(0.8f, 1.3f), Speed = V(0.2f, 0.6f), Size = V(1.4f, 2.2f), Add = true,
            Shape = ParticleSystemShapeType.Sphere, Radius = 0.45f, Gravity = -0.05f, Spin = 0f, Grow = V(1f, 0.35f), FadeIn = 0.08f, Hold = 0.3f,
            Tint = new[] { C(1f, 0.85f, 0.45f, 0.9f), C(1f, 0.42f, 0.1f, 0.7f), C(0.35f, 0.06f, 0f, 0f) } },
        // Black smoke of the explosion and the burning wreck.
        new Spec { Name = "SmokeBlack", Texture = "fx-smoke", Tx = 5, Ty = 3, Max = 80, Life = V(3f, 4.6f), Speed = V(0.8f, 2f), Size = V(1.7f, 2.9f),
            Shape = ParticleSystemShapeType.Sphere, Radius = 0.5f, Gravity = -0.04f, Drag = 0.2f, Grow = V(0.6f, 1.8f), Animate = true, FadeIn = 0.1f, Hold = 0.35f,
            Tint = new[] { C(0.1f, 0.1f, 0.11f, 0.85f), C(0.14f, 0.14f, 0.15f, 0.7f), C(0.2f, 0.2f, 0.21f, 0f) } },
        // Pale smoke: the rocket's exhaust and back-blast puff.
        new Spec { Name = "SmokeLight", Texture = "fx-smoke", Tx = 5, Ty = 3, Max = 80, Life = V(0.8f, 1.35f), Speed = V(0.2f, 0.9f), Size = V(0.4f, 0.75f),
            Shape = ParticleSystemShapeType.Sphere, Radius = 0.1f, Drag = 0.3f, Grow = V(0.5f, 2.2f), Animate = true, FadeIn = 0.06f, Hold = 0.2f,
            Tint = new[] { C(0.86f, 0.86f, 0.86f, 0.5f), C(0.7f, 0.7f, 0.7f, 0.3f), C(0.6f, 0.6f, 0.6f, 0f) } },
        // White steam jets (the back plates blowing open on ejection).
        new Spec { Name = "Steam", Texture = "fx-smoke", Tx = 5, Ty = 3, Max = 60, Life = V(0.6f, 1.05f), Speed = V(4f, 7f), Size = V(0.3f, 0.55f),
            Shape = ParticleSystemShapeType.Cone, Radius = 0.06f, Angle = 14f, Drag = 0.12f, Grow = V(0.5f, 2.4f), Animate = true, FadeIn = 0.04f, Hold = 0.2f,
            Tint = new[] { C(0.93f, 0.95f, 0.97f, 0.6f), C(0.85f, 0.88f, 0.92f, 0.4f), C(0.8f, 0.83f, 0.88f, 0f) } },
        // Hot haze from an overheated suit's shoulders.
        new Spec { Name = "HeatSteam", Texture = "fx-smoke", Tx = 5, Ty = 3, Max = 24, Life = V(0.9f, 1.3f), Speed = V(0.5f, 1.1f), Size = V(0.2f, 0.34f), Add = true,
            Shape = ParticleSystemShapeType.Cone, Radius = 0.05f, Angle = 25f, Drag = 0.2f, Grow = V(0.6f, 1.7f), Animate = true, FadeIn = 0.12f, Hold = 0.3f,
            Tint = new[] { C(1f, 0.5f, 0.28f, 0.34f), C(1f, 0.35f, 0.18f, 0.22f), C(0.7f, 0.2f, 0.1f, 0f) } },
        // A round glow: the explosion's flash (size, life and colour set per emission).
        new Spec { Name = "Glow", Texture = "fx-glow", Max = 40, Life = V(0.4f, 0.4f), Speed = V(0f, 0f), Size = V(1f, 1f), Add = true, Spin = 0f,
            Grow = V(0.5f, 1.2f), FadeIn = 0.05f, Hold = 0.15f },
        // The cracks a landing leaves in the floor (size, life and colour set per emission); stays flat for several seconds.
        new Spec { Name = "Crack", Texture = "fx-crack", Max = 6, Life = V(10f, 10f), Speed = V(0f, 0f), Size = V(1f, 1f), Flat = true, Spin = 0f,
            FadeIn = 0.01f, Hold = 0.7f },
        // Dust kicked up behind a charging suit's feet.
        new Spec { Name = "TrailDust", Texture = "fx-mist", Tx = 2, Ty = 2, Max = 40, Life = V(0.7f, 1.1f), Speed = V(1f, 2.2f), Size = V(0.5f, 0.85f),
            Shape = ParticleSystemShapeType.Cone, Radius = 0.15f, Angle = 16f, Drag = 0.2f, Grow = V(0.6f, 1.8f), FadeIn = 0.08f, Hold = 0.3f,
            Tint = new[] { C(0.66f, 0.58f, 0.46f, 0.5f), C(0.6f, 0.53f, 0.43f, 0.4f), C(0.55f, 0.5f, 0.42f, 0f) } },
    };

    public static void Build()
    {
        try { Run(); EditorApplication.Exit(0); }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static string Need(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is not set.");

    static void Run()
    {
        if (Application.unityVersion != "2019.4.21f1") throw new InvalidOperationException("Use the game's Unity 2019.4.21f1.");
        var textures = Need("FORGE_FX_TEXTURES");
        var shaderSource = Need("FORGE_FX_SHADER");
        var generated = Need("FORGE_FX_GENERATED");
        var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var folder = Path.Combine(project, Folder);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
        Directory.CreateDirectory(folder);
        foreach (var png in Directory.GetFiles(textures, "fx-*.png")) File.Copy(png, Path.Combine(folder, Path.GetFileName(png)), true);
        File.Copy(shaderSource, Path.Combine(folder, "ForgeVisualParticle.shader"), true);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        foreach (var png in Directory.GetFiles(folder, "fx-*.png")) ImportTexture(Folder + "/" + Path.GetFileName(png));
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(Folder + "/ForgeVisualParticle.shader") ?? throw new InvalidOperationException("Unity did not import the shader.");
        if (!shader.isSupported) throw new InvalidOperationException("The effect shader is not supported on this device.");

        var root = Assemble(shader);
        PrefabUtility.SaveAsPrefabAsset(root, Folder + "/" + PrefabName + ".prefab");
        // The shipped prefab: the same systems, no materials.
        foreach (var renderer in root.GetComponentsInChildren<ParticleSystemRenderer>(true)) renderer.sharedMaterial = null;
        Directory.CreateDirectory(Path.Combine(project, ShipFolder));
        var prefabPath = ShipFolder + "/" + PrefabName + ".prefab";
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();

        var output = Path.Combine(project, "fx-out");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        Directory.CreateDirectory(output);
        var built = BuildPipeline.BuildAssetBundles(output, new[] { new AssetBundleBuild { assetBundleName = "frames-fx.bundle", assetNames = new[] { prefabPath }.Concat(Specs.Select(s => Folder + "/" + s.Texture + ".png").Distinct()).ToArray() } },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DeterministicAssetBundle, BuildTarget.StandaloneWindows64);
        if (built == null) throw new InvalidOperationException("The effect bundle build failed.");
        var bundlePath = Path.Combine(output, "frames-fx.bundle");
        Verify(bundlePath);
        var bytes = File.ReadAllBytes(bundlePath);
        string sha;
        using (var hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        File.WriteAllText(generated,
            "// Generated by tools/fx/build_fx.ps1 with Unity 2019.4.21f1 from tools/fx (shader, authored particle systems, staged textures); do not edit.\n"
            + "namespace ForgeOrdnanceFrames;\n\ninternal static class FxBundleBytes\n{\n"
            + "    internal const string BundleSha256 = \"" + sha + "\";\n"
            + "    internal const string PrefabName = \"" + PrefabName + "\";\n"
            + "    internal static byte[] Read() => System.Convert.FromBase64String(\"" + Convert.ToBase64String(bytes) + "\");\n}\n");
        Debug.Log("FRAMES_FX_BUILT " + bytes.Length + " bytes " + sha + " systems " + Specs.Length);
    }

    static void ImportTexture(string path)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = true;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.mipmapEnabled = true;
        importer.isReadable = false;
        importer.maxTextureSize = 1024;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
        importer.crunchedCompression = false;
        importer.SaveAndReimport();
    }

    /// <summary>The prefab: a root with one particle system per spec. Used by the build and by the preview.</summary>
    public static GameObject Assemble(Shader shader)
    {
        var root = new GameObject(PrefabName);
        var materials = new Dictionary<string, Material>();
        foreach (var spec in Specs)
        {
            var key = spec.Texture + (spec.Add ? "+" : "-");
            if (!materials.TryGetValue(key, out var material))
            {
                material = new Material(shader) { name = "Fx " + spec.Texture + (spec.Add ? " add" : " alpha") };
                material.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "/" + spec.Texture + ".png"));
                // The same settings ForgeVisual gives its additive and alpha particle materials.
                material.SetFloat("_Src", spec.Add ? 1f : 5f);
                material.SetFloat("_Dst", spec.Add ? 1f : 10f);
                material.SetFloat("_Additive", spec.Add ? 1f : 0f);
                if (AssetDatabase.Contains(shader)) AssetDatabase.CreateAsset(material, Folder + "/" + material.name.Replace(' ', '_').Replace('+', 'p') + ".mat");
                materials[key] = material;
            }
            Make(root.transform, spec, material);
        }
        return root;
    }

    static void Make(Transform parent, Spec s, Material material)
    {
        var go = new GameObject(s.Name);
        go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.playOnAwake = true;
        main.loop = true;
        main.duration = 5f;
        main.prewarm = false;
        main.maxParticles = s.Max;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        main.startLifetime = new ParticleSystem.MinMaxCurve(s.Life.x, s.Life.y);
        main.startSpeed = new ParticleSystem.MinMaxCurve(s.Speed.x, s.Speed.y);
        main.startSize = new ParticleSystem.MinMaxCurve(s.Size.x, s.Size.y);
        main.startRotation = s.Flat || s.Upright ? new ParticleSystem.MinMaxCurve(0f) : new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = Color.white;
        main.gravityModifier = s.Gravity;
        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        if (s.Speed.y <= 0f) shape.enabled = false;
        else
        {
            shape.enabled = true;
            shape.shapeType = s.Shape;
            shape.radius = s.Radius;
            shape.angle = s.Angle;
            if (s.Shape == ParticleSystemShapeType.Circle) { shape.radiusThickness = 0f; shape.arc = 360f; }
        }
        // Colour: the tint's colours spread evenly over the life, alpha rising over FadeIn, held to Hold, then out; the tint's own alpha scales it.
        var colour = ps.colorOverLifetime;
        colour.enabled = true;
        var colourKeys = new GradientColorKey[s.Tint.Length];
        var alphaKeys = new List<GradientAlphaKey> { new GradientAlphaKey(0f, 0f) };
        for (var i = 0; i < s.Tint.Length; i++)
        {
            var at = s.Tint.Length == 1 ? 0f : i / (float)(s.Tint.Length - 1);
            colourKeys[i] = new GradientColorKey(new Color(s.Tint[i].r, s.Tint[i].g, s.Tint[i].b), at);
        }
        var peak = s.Tint[0].a;
        alphaKeys.Add(new GradientAlphaKey(peak, s.FadeIn));
        alphaKeys.Add(new GradientAlphaKey(s.Tint[Mathf.Min(1, s.Tint.Length - 1)].a, Mathf.Clamp(s.Hold, s.FadeIn + 0.01f, 0.9f)));
        alphaKeys.Add(new GradientAlphaKey(0f, 1f));
        var gradient = new Gradient();
        gradient.SetKeys(colourKeys, alphaKeys.ToArray());
        colour.color = gradient;
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        var curve = s.EaseOut
            ? new AnimationCurve(new Keyframe(0f, s.Grow.x, 0f, 6f), new Keyframe(1f, s.Grow.y, 0f, 0f))
            : AnimationCurve.Linear(0f, s.Grow.x, 1f, s.Grow.y);
        size.size = new ParticleSystem.MinMaxCurve(1f, curve);
        if (s.Spin > 0f)
        {
            var rotation = ps.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-s.Spin, s.Spin);
        }
        if (s.Drag > 0f)
        {
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = new ParticleSystem.MinMaxCurve(0.25f);
            limit.dampen = s.Drag;
        }
        if (s.Tx * s.Ty > 1)
        {
            var sheet = ps.textureSheetAnimation;
            sheet.enabled = true;
            sheet.numTilesX = s.Tx;
            sheet.numTilesY = s.Ty;
            sheet.cycleCount = 1;
            if (s.Animate) { sheet.startFrame = new ParticleSystem.MinMaxCurve(0f); sheet.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, s.FrameTo)); }
            else { sheet.startFrame = new ParticleSystem.MinMaxCurve(0f, 1f); sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f); }
        }
        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = s.Flat ? ParticleSystemRenderMode.HorizontalBillboard : s.Stretch ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
        if (s.Stretch) { renderer.velocityScale = s.VelScale; renderer.lengthScale = s.LenScale; }
        renderer.sharedMaterial = material;
        renderer.maxParticleSize = 1f;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
    }

    /// <summary>Reloads the built bundle as the game will and checks every system's name, budget and loop/emission settings.</summary>
    static void Verify(string bundlePath)
    {
        var bundle = AssetBundle.LoadFromFile(bundlePath) ?? throw new InvalidDataException("Cannot reopen the effect bundle.");
        try
        {
            var prefab = bundle.LoadAsset<GameObject>(PrefabName) ?? throw new InvalidDataException("The bundle has no " + PrefabName + ".");
            var systems = prefab.GetComponentsInChildren<ParticleSystem>(true);
            if (systems.Length != Specs.Length) throw new InvalidDataException("System count differs: " + systems.Length);
            foreach (var spec in Specs)
            {
                var ps = prefab.transform.Find(spec.Name)?.GetComponent<ParticleSystem>() ?? throw new InvalidDataException("System missing: " + spec.Name);
                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (ps.main.maxParticles != spec.Max || !ps.main.loop || ps.emission.enabled || ps.main.simulationSpace != ParticleSystemSimulationSpace.World)
                    throw new InvalidDataException("System settings differ: " + spec.Name);
                if (renderer.sharedMaterial != null) throw new InvalidDataException("System carries a material: " + spec.Name);
                if (!bundle.Contains(Folder + "/" + spec.Texture + ".png") || bundle.LoadAsset<Texture2D>(Folder + "/" + spec.Texture + ".png") == null)
                    throw new InvalidDataException("The bundle lacks the texture of " + spec.Name + ": " + spec.Texture);
            }
        }
        finally { bundle.Unload(true); }
    }
}
#endif

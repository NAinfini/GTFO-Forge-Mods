using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

/// <summary>Builds forge-frames.bundle (Unity 2019.4.21f1, the game's version) from the stage build_assets.ps1 wrote:
/// stage/frames/&lt;prefab&gt;/ holds model.fbx (skinned, takes open/close; sub-mesh 0 the armour on the texture atlas,
/// sub-meshes 1 to 3 the inside: fabric, metal, rubber), albedo.png, metallic.png, normal.png, emission.png (the lights in grey, the light strips white),
/// lining_albedo.png (the fabric's colours) and frame.json (height, the definition's opening parts,
/// the inside's triangle counts). stage/lining/ holds the inside's other maps, one set for every suit. Each frame becomes a prefab
/// "&lt;prefab&gt;": an empty root with the imported model as child "model" (facing +Z by the export convention), a legacy
/// Animation component on "model" holding the two opening clips ("open" and "close", cut down to the opening parts' bones, which
/// FrameVisual samples; a worn suit's limbs follow its pilot's own game body), the bones at their bind pose, and renderers without materials (the plugin builds them with the
/// game's gear shader). stage/pilot/ holds PilotMotion.fbx (the game's player skeleton, no mesh, takes Rest and PilotExit; any other take is left out) and
/// pilot.json (the clips' root path and the eye at rest): it becomes the prefab "pilot-motion", the skeleton as child "model" with a legacy
/// Animation holding the two clips, the leaving hop keyed onto its "root" bone, and an "eye" point.
/// stage/props/&lt;prefab&gt;/ holds a part shown with a suit (<see cref="Prop"/>: the rocket launcher, the rocket). The
/// "ordnance-profile" TextAsset uses ForgeOrdnance's bundle profile format 1, so ForgeVisual's Bundles.Register reads it.
/// Environment: FORGE_FRAMES_STAGE (input) and FORGE_FRAMES_OUT (output folder).</summary>
public static class FramesBundleBuild
{
    [Serializable] public sealed class FrameStage { public string prefab; public float height; public string[] opening, intoBody; public float[] slotRays; public int[] liningTriangles; }
    [Serializable] public sealed class ProfileRenderer { public string name; public string[] surfaces; }
    [Serializable] public sealed class ProfilePrefab { public string name; public ProfileRenderer[] renderers; public string[] anchors; }
    [Serializable] public sealed class ProfileSurface { public string key, albedo, metallic, normal, emission; public float[] emissionColor; }
    [Serializable] public sealed class Profile { public int format; public ProfilePrefab[] prefabs; public ProfileSurface[] surfaces; }
    [Serializable] public sealed class PilotClipStage { public float seconds; public float rootStartY; public float[] rootForward; }
    [Serializable] public sealed class PilotStage { public int fps; public float[] eye; public PilotClipStage exit; }
    [Serializable] public sealed class PropStage { public string prefab; public int texture; public string[] sockets; public int triangles; public float[] size; }

    public const string BundleName = "forge-frames.bundle";
    static readonly string[] RequiredModels = { "frame-m15", "frame-m16", "frame-m17", "frame-m18" };
    static readonly string[] Clips = { "open", "close" };
    const string PilotPrefab = "pilot-motion";
    static readonly string[] PilotClips = { "Rest", "PilotExit" };
    const float BoundsPadding = 0.05f;
    const int BoundsSamplesPerFrame = 2;
    const string Stage = "Assets/FramesStage";
    /// <summary>Gain on the suits' lights until a suit tints them (FrameVisual, Core FrameGlow): emission.png holds the light strips in white and the source models' lamps and visors in grey, kept below the gear shader's wash-out to white (a gain of 1.5 already washed out).</summary>
    static readonly float[] LightColour = { 0.8f, 0.8f, 0.8f };
    const string Generated = "Assets/FramesGenerated";
    /// <summary>How far behind each pilot-slot ray's start the open suit must be clear (build_frame.py SLOT_DEPTH).</summary>
    const float SlotDepth = 1f;
    /// <summary>Metres: every opening part's centre moves at least this far in the open clip, and ends at most this much nearer the body's nearest axis (a plate tucked under its neighbour).</summary>
    const float MovesAtLeast = 0.002f, TuckAtMost = 0.03f;
    /// <summary>The inside's textures: the fabric atlas (sub-mesh 1) and the tiling metal (2) and rubber (3); file stem in stage/lining and size.</summary>
    const int FabricSize = 1024, LiningSize = 512;
    static readonly (string name, string file, int size)[] LiningKinds = { ("lining", "lining", FabricSize), ("lining-metal", "lining_metal", LiningSize), ("lining-rubber", "lining_rubber", LiningSize) };
    /// <summary>The asset name of a map every suit shares.</summary>
    static string SharedMap(string file, string map) => "frames-" + file.Replace('_', '-') + "-" + map;
    /// <summary>The armour atlas (albedo) and the metal-smoothness map; the normal and emission maps stay at 2048.</summary>
    const int AtlasSize = 4096, MetalSize = 2048, DetailSize = 2048;
    /// <summary>The body's axes, by bone kind: each runs from that bone to this one (build_frame.py's rig: a limb from its joint to the next, the trunk from the pelvis up to the neck).</summary>
    static readonly Dictionary<string, string> AxisEnd = new Dictionary<string, string>
    {
        { "upperarm", "forearm" }, { "forearm", "hand" }, { "thigh", "shin" }, { "shin", "foot" },
        { "pelvis", "spine" }, { "spine", "chest" }, { "chest", "head" },
    };

    public static void Build()
    {
        try { Run(); EditorApplication.Exit(0); }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void Run()
    {
        if (Application.unityVersion != "2019.4.21f1") throw new InvalidOperationException("Use the game's Unity 2019.4.21f1.");
        var input = Environment.GetEnvironmentVariable("FORGE_FRAMES_STAGE") ?? throw new InvalidOperationException("FORGE_FRAMES_STAGE is not set.");
        var output = Environment.GetEnvironmentVariable("FORGE_FRAMES_OUT") ?? throw new InvalidOperationException("FORGE_FRAMES_OUT is not set.");
        var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        foreach (var folder in new[] { Stage, Generated })
        {
            var full = Path.Combine(project, folder);
            if (Directory.Exists(full)) Directory.Delete(full, true);
            if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
        }
        Copy(input, Path.Combine(project, Stage));
        Directory.CreateDirectory(Path.Combine(project, Generated));
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var assets = new List<(string path, string name)>();
        var prefabs = new List<ProfilePrefab>();
        var surfaces = new List<ProfileSurface>();
        // The inside's maps every suit shares: only the fabric's colour map is a suit's own.
        foreach (var kind in LiningKinds)
        {
            var maps = Stage + "/lining/" + kind.file + "_";
            if (kind.file != "lining") assets.Add((Texture(maps + "albedo.png", TextureImporterType.Default, true, kind.size), SharedMap(kind.file, "albedo")));
            assets.Add((Texture(maps + "metallic.png", TextureImporterType.Default, false, kind.size), SharedMap(kind.file, "metallic")));
            assets.Add((Texture(maps + "normal.png", TextureImporterType.NormalMap, false, kind.size), SharedMap(kind.file, "normal")));
        }
        foreach (var dir in Directory.GetDirectories(Path.Combine(project, Stage, "frames")).OrderBy(d => d, StringComparer.Ordinal))
        {
            var stage = JsonUtility.FromJson<FrameStage>(File.ReadAllText(Path.Combine(dir, "frame.json")));
            var folder = Stage + "/frames/" + Path.GetFileName(dir);
            // One surface per frame, keyed "<prefab>/body"; ForgeVisual's ModelHandle.Spawn applies it.
            var key = stage.prefab + "/body";
            var albedo = stage.prefab + "-body-albedo";
            var metallic = stage.prefab + "-body-metallic";
            var normal = stage.prefab + "-body-normal";
            var emission = stage.prefab + "-body-emission";
            assets.Add((Texture(folder + "/albedo.png", TextureImporterType.Default, true, AtlasSize), albedo));
            assets.Add((Texture(folder + "/metallic.png", TextureImporterType.Default, false, MetalSize), metallic));
            assets.Add((Texture(folder + "/normal.png", TextureImporterType.NormalMap, false, DetailSize), normal));
            assets.Add((Texture(folder + "/emission.png", TextureImporterType.Default, true, DetailSize), emission));
            // The lights in grey (sRGB); ForgeOrdnance scales them by this gain (_EmissiveMap x _EmissiveColor on the gear shader), each suit then by its state's colour.
            surfaces.Add(new ProfileSurface { key = key, albedo = albedo, metallic = metallic, normal = normal, emission = emission, emissionColor = LightColour });
            // The inside: sub-mesh 1 "<prefab>/lining" (fabric, the suit's own colours), 2 "<prefab>/lining-metal" and 3 "<prefab>/lining-rubber"; no lights.
            var liningKeys = new string[LiningKinds.Length];
            for (var kind = 0; kind < LiningKinds.Length; kind++)
            {
                var file = LiningKinds[kind].file;
                var liningKey = liningKeys[kind] = stage.prefab + "/" + LiningKinds[kind].name;
                var liningAlbedo = SharedMap(file, "albedo");
                if (kind == 0)
                {
                    liningAlbedo = stage.prefab + "-lining-albedo";
                    assets.Add((Texture(folder + "/lining_albedo.png", TextureImporterType.Default, true, FabricSize), liningAlbedo));
                }
                surfaces.Add(new ProfileSurface { key = liningKey, albedo = liningAlbedo, metallic = SharedMap(file, "metallic"), normal = SharedMap(file, "normal"), emission = "", emissionColor = new float[0] });
            }

            var modelPath = folder + "/model.fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath) ?? throw new InvalidDataException("No model importer for " + modelPath);
            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.animationType = ModelImporterAnimationType.Legacy;
            importer.importAnimation = true;
            importer.optimizeGameObjects = false;
            // The first-person shell cuts its arms and legs out of this mesh at run time.
            importer.isReadable = true;
            // The opening is every part's rigid path, frame by frame: keep each key as built.
            importer.animationCompression = ModelImporterAnimationCompression.Off;
            var takes = importer.importedTakeInfos;
            var clips = new List<ModelImporterClipAnimation>();
            foreach (var clip in Clips)
            {
                var take = takes.FirstOrDefault(t => t.name == clip || t.name.EndsWith("|" + clip, StringComparison.Ordinal));
                if (take.name == null) throw new InvalidDataException(modelPath + " has no take '" + clip + "' (takes: " + string.Join(", ", takes.Select(t => t.name)) + ").");
                var fps = take.sampleRate > 0 ? take.sampleRate : 30f;
                clips.Add(new ModelImporterClipAnimation
                {
                    name = clip,
                    takeName = take.name,
                    firstFrame = take.startTime * fps,
                    lastFrame = take.stopTime * fps,
                    loopTime = false,
                    wrapMode = WrapMode.ClampForever,
                });
            }
            importer.clipAnimations = clips.ToArray();
            importer.SaveAndReimport();

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath) ?? throw new InvalidDataException("Unity did not import " + modelPath);
            var root = new GameObject(stage.prefab);
            var model = (GameObject)PrefabUtility.InstantiatePrefab(source);
            PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            model.name = "model";
            model.transform.SetParent(root.transform, false);
            var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length != 1) throw new InvalidDataException(stage.prefab + " has " + renderers.Length + " skinned renderers; expected one.");
            var skin = renderers[0];
            skin.sharedMaterials = new Material[skin.sharedMesh.subMeshCount];
            // The model was made facing glTF +Z with the hands forward; turn it when the import came out facing -Z.
            var bounds = skin.sharedMesh.bounds;
            var animation = model.GetComponent<Animation>() ?? throw new InvalidDataException(stage.prefab + " imported without a legacy Animation component.");
            animation.playAutomatically = false;
            foreach (var clip in Clips)
                if (animation.GetClip(clip) == null) throw new InvalidDataException(stage.prefab + " lacks clip " + clip);
            OpeningClip(stage, animation, "open");
            OpeningClip(stage, animation, "close");
            CheckSurface(stage, skin.sharedMesh);
            skin.sharedMesh = ShippedMesh(skin.sharedMesh, Generated + "/" + stage.prefab + "-mesh.asset");
            CheckOpening(stage, root, skin);
            skin.localBounds = AnimatedBounds(model, skin, animation);
            skin.updateWhenOffscreen = false;
            BindPose(skin);
            var front = Front(skin);
            var size = Bounds(root);
            Debug.Log("FRAME " + stage.prefab + " size " + size.size.ToString("F3") + " centre " + size.center.ToString("F3") + " mesh bounds " + bounds.size.ToString("F3")
                + " animated bounds " + skin.localBounds.size.ToString("F3") + " centre " + skin.localBounds.center.ToString("F3")
                + " front reach " + front.ToString("F3") + " bones " + skin.bones.Length + " clips " + animation.GetClipCount());
            // The bind-pose mesh, not the baked pose: the editor samples a clip into the bones on import.
            if (Math.Abs(bounds.size.y - stage.height) > 0.05f * stage.height) throw new InvalidDataException(stage.prefab + " mesh is " + bounds.size.y + " m tall; the rig says " + stage.height + " m (scale import is wrong).");
            var rendererPath = RelativePath(skin.transform, root.transform);
            var prefabPath = Generated + "/" + stage.prefab + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            assets.Add((prefabPath, stage.prefab));
            prefabs.Add(new ProfilePrefab { name = stage.prefab, renderers = new[] { new ProfileRenderer { name = rendererPath, surfaces = new[] { key, liningKeys[0], liningKeys[1], liningKeys[2] } } }, anchors = new string[0] });
        }

        assets.Add((Pilot(project), PilotPrefab));
        prefabs.Add(new ProfilePrefab { name = PilotPrefab, renderers = new ProfileRenderer[0], anchors = new string[0] });

        var propTextures = new Dictionary<string, int>(StringComparer.Ordinal);
        var propsDir = Path.Combine(project, Stage, "props");
        if (Directory.Exists(propsDir))
            foreach (var dir in Directory.GetDirectories(propsDir).OrderBy(d => d, StringComparer.Ordinal))
                prefabs.Add(Prop(dir, assets, surfaces, propTextures));

        var profile = new Profile { format = 1, prefabs = prefabs.ToArray(), surfaces = surfaces.ToArray() };
        var profilePath = Generated + "/ordnance-profile.txt";
        File.WriteAllText(Path.Combine(project, profilePath), JsonUtility.ToJson(profile, true));
        AssetDatabase.ImportAsset(profilePath, ImportAssetOptions.ForceSynchronousImport);
        assets.Add((profilePath, "ordnance-profile"));

        Directory.CreateDirectory(output);
        var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = assets.Select(a => a.path).ToArray(), addressableNames = assets.Select(a => a.name).ToArray() };
        if (BuildPipeline.BuildAssetBundles(output, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64) == null)
            throw new InvalidOperationException("BuildAssetBundles failed.");
        var file2 = Path.Combine(output, BundleName);
        Verify(file2, profile, propTextures);
        var length = new FileInfo(file2).Length;
        string hash;
        using (var sha = SHA256.Create()) using (var s = File.OpenRead(file2)) hash = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        Debug.Log("FRAMES_BUNDLE_VERIFIED " + BundleName + " " + length + " bytes " + hash);
    }

    /// <summary>The pilot's motion prefab: PilotMotion.fbx imported as a legacy skeleton with the clips Rest and PilotExit (keys
    /// as built, clamped at their ends), the leaving clip's root path (pilot.json: metres along the facing, 30 fps) keyed onto the "root" bone
    /// (Blender keeps the root still), and the eye point at rest. Checks the skeleton faces +Z and the leaving lands where pilot.json says.</summary>
    static string Pilot(string project)
    {
        var folder = Stage + "/pilot";
        var stage = JsonUtility.FromJson<PilotStage>(File.ReadAllText(Path.Combine(project, folder, "pilot.json")));
        var path = folder + "/PilotMotion.fbx";
        var importer = (ModelImporter)AssetImporter.GetAtPath(path) ?? throw new InvalidDataException("No model importer for " + path);
        importer.globalScale = 1f;
        importer.useFileScale = true;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.animationType = ModelImporterAnimationType.Legacy;
        importer.importAnimation = true;
        importer.resampleCurves = true;
        importer.optimizeGameObjects = false;
        importer.animationCompression = ModelImporterAnimationCompression.Off;
        var takes = importer.importedTakeInfos;
        var clips = new List<ModelImporterClipAnimation>();
        foreach (var name in PilotClips)
        {
            var take = takes.FirstOrDefault(t => t.name == name || t.name.EndsWith("|" + name, StringComparison.Ordinal));
            if (take.name == null) throw new InvalidDataException(path + " has no take '" + name + "' (takes: " + string.Join(", ", takes.Select(t => t.name)) + ").");
            var fps = take.sampleRate > 0 ? take.sampleRate : 30f;
            clips.Add(new ModelImporterClipAnimation { name = name, takeName = take.name, firstFrame = take.startTime * fps, lastFrame = take.stopTime * fps, loopTime = false, wrapMode = WrapMode.ClampForever });
        }
        importer.clipAnimations = clips.ToArray();
        importer.SaveAndReimport();

        var source = AssetDatabase.LoadAssetAtPath<GameObject>(path) ?? throw new InvalidDataException("Unity did not import " + path);
        var prefabRoot = new GameObject(PilotPrefab);
        var model = (GameObject)PrefabUtility.InstantiatePrefab(source);
        PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        model.name = "model";
        model.transform.SetParent(prefabRoot.transform, false);
        if (model.GetComponentsInChildren<Renderer>(true).Length != 0) throw new InvalidDataException(path + " must hold the skeleton only.");
        var animation = model.GetComponent<Animation>() ?? throw new InvalidDataException(path + " imported without a legacy Animation component.");
        animation.playAutomatically = false;
        var hip = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Hip") ?? throw new InvalidDataException(path + " has no Hip.");
        var root = hip.parent;
        var rootPath = AnimationUtility.CalculateTransformPath(root, model.transform);
        var rest = root.localPosition;
        var clipFolder = Generated + "/" + PilotPrefab;
        if (!AssetDatabase.IsValidFolder(clipFolder)) AssetDatabase.CreateFolder(Generated, PilotPrefab);
        foreach (var (name, path2) in new[] { ("PilotExit", stage.exit) })
        {
            var src = animation.GetClip(name) ?? throw new InvalidDataException(path + " lacks clip " + name);
            if (Math.Abs(src.length - path2.seconds) > 0.02f) throw new InvalidDataException(name + " is " + src.length + " s; pilot.json says " + path2.seconds + " s.");
            var clip = new AnimationClip { name = name, legacy = true, frameRate = src.frameRate, wrapMode = WrapMode.ClampForever };
            foreach (var binding in AnimationUtility.GetCurveBindings(src))
            {
                if (binding.path == rootPath && binding.propertyName.StartsWith("m_LocalPosition", StringComparison.Ordinal)) continue;
                AnimationUtility.SetEditorCurve(clip, binding, AnimationUtility.GetEditorCurve(src, binding));
            }
            // Blender: the root stands rootStartY behind the suit's feet and moves rootForward metres forward; the skeleton faces +Z.
            var x = new AnimationCurve();
            var y = new AnimationCurve();
            var z = new AnimationCurve();
            for (var k = 0; k < path2.rootForward.Length; k++)
            {
                var t = k / (float)stage.fps;
                x.AddKey(t, rest.x);
                y.AddKey(t, rest.y);
                z.AddKey(t, rest.z - path2.rootStartY + path2.rootForward[k]);
            }
            foreach (var curve in new[] { x, y, z })
                for (var k = 0; k < curve.length; k++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.Linear);
                    AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.Linear);
                }
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(rootPath, typeof(Transform), "m_LocalPosition.x"), x);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(rootPath, typeof(Transform), "m_LocalPosition.y"), y);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(rootPath, typeof(Transform), "m_LocalPosition.z"), z);
            clip.EnsureQuaternionContinuity();
            AssetDatabase.CreateAsset(clip, clipFolder + "/" + name + ".anim");
            animation.RemoveClip(name);
            animation.AddClip(clip, name);
        }
        var eye = new GameObject("eye").transform;
        eye.SetParent(prefabRoot.transform, false);
        eye.localPosition = new Vector3(stage.eye[0], stage.eye[1], stage.eye[2]);
        // Checks on the built prefab: facing +Z at rest and the leaving landing where pilot.json says.
        animation.GetClip("Rest").SampleAnimation(model, 0f);
        var toe = model.GetComponentsInChildren<Transform>(true).First(t => t.name == "LeftToe");
        var foot = model.GetComponentsInChildren<Transform>(true).First(t => t.name == "LeftFoot");
        if (!(toe.position.z > foot.position.z)) throw new InvalidDataException("The pilot skeleton does not face +Z (left toe " + toe.position.ToString("F3") + ", foot " + foot.position.ToString("F3") + ").");
        animation.GetClip("PilotExit").SampleAnimation(model, stage.exit.seconds);
        var landed = -root.localPosition.z;

        var expectLanded = stage.exit.rootStartY - stage.exit.rootForward[stage.exit.rootForward.Length - 1];
        if (Math.Abs(landed - expectLanded) > 0.005f) throw new InvalidDataException("PilotExit lands " + landed + " m behind; pilot.json says " + expectLanded + ".");
        animation.GetClip("Rest").SampleAnimation(model, 0f);
        Debug.Log("PILOT " + PilotPrefab + " bones " + model.GetComponentsInChildren<Transform>(true).Length + ", PilotExit " + animation.GetClip("PilotExit").length.ToString("F3") + " s landing " + landed.ToString("F3") + " m behind, eye " + eye.localPosition.ToString("F3"));
        var prefabPath = Generated + "/" + PilotPrefab + ".prefab";
        PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
        UnityEngine.Object.DestroyImmediate(prefabRoot);
        return prefabPath;
    }

    /// <summary>A part shown with a suit (build_prop.py staged it under stage/props/&lt;prefab&gt;/: model.fbx, albedo.png,
    /// metallic.png, normal.png, prop.json): the prefab "&lt;prefab&gt;", an empty root with the imported model as child "model"
    /// (one mesh renderer, no materials: the plugin shades it with the game's gear shader from the surface "&lt;prefab&gt;/body") and
    /// its sockets (tube mouths and the like), each turned to face the prefab's +Z. Checks the imported size against the staged one.</summary>
    static ProfilePrefab Prop(string dir, List<(string path, string name)> assets, List<ProfileSurface> surfaces, Dictionary<string, int> textures)
    {
        var stage = JsonUtility.FromJson<PropStage>(File.ReadAllText(Path.Combine(dir, "prop.json")));
        var folder = Stage + "/props/" + Path.GetFileName(dir);
        var key = stage.prefab + "/body";
        var albedo = stage.prefab + "-body-albedo";
        var metallic = stage.prefab + "-body-metallic";
        var normal = stage.prefab + "-body-normal";
        assets.Add((Texture(folder + "/albedo.png", TextureImporterType.Default, true, stage.texture), albedo));
        assets.Add((Texture(folder + "/metallic.png", TextureImporterType.Default, false, stage.texture), metallic));
        assets.Add((Texture(folder + "/normal.png", TextureImporterType.NormalMap, false, stage.texture), normal));
        surfaces.Add(new ProfileSurface { key = key, albedo = albedo, metallic = metallic, normal = normal, emission = "", emissionColor = new float[0] });
        textures[key] = stage.texture;

        var modelPath = folder + "/model.fbx";
        var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath) ?? throw new InvalidDataException("No model importer for " + modelPath);
        importer.globalScale = 1f;
        importer.useFileScale = true;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.animationType = ModelImporterAnimationType.None;
        importer.importAnimation = false;
        importer.isReadable = false;
        importer.SaveAndReimport();
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath) ?? throw new InvalidDataException("Unity did not import " + modelPath);
        var root = new GameObject(stage.prefab);
        var model = (GameObject)PrefabUtility.InstantiatePrefab(source);
        PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        model.name = "model";
        model.transform.SetParent(root.transform, false);
        if (model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0) throw new InvalidDataException(stage.prefab + " must not be skinned.");
        var renderers = model.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length != 1) throw new InvalidDataException(stage.prefab + " has " + renderers.Length + " mesh renderers; expected one.");
        var renderer = renderers[0];
        var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
        renderer.sharedMaterials = new Material[mesh.subMeshCount];
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        foreach (var socket in stage.sockets)
        {
            var t = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == socket) ?? throw new InvalidDataException(stage.prefab + " lost its socket " + socket + ".");
            t.rotation = root.transform.rotation;
        }
        var b = renderer.bounds;
        var expected = new Vector3(stage.size[0], stage.size[1], stage.size[2]);
        if ((b.size - expected).magnitude > 0.02f * expected.magnitude)
            throw new InvalidDataException(stage.prefab + " imported " + b.size.ToString("F3") + " m; the stage says " + expected.ToString("F3") + " (axes or scale are wrong).");
        Debug.Log("PROP " + stage.prefab + " " + mesh.triangles.Length / 3 + " triangles, " + b.size.ToString("F3") + " m centred " + b.center.ToString("F3") + ", sockets " + string.Join(", ", stage.sockets));
        var prefabPath = Generated + "/" + stage.prefab + ".prefab";
        var rendererPath = RelativePath(renderer.transform, root.transform);
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        assets.Add((prefabPath, stage.prefab));
        return new ProfilePrefab { name = stage.prefab, renderers = new[] { new ProfileRenderer { name = rendererPath, surfaces = new[] { key } } }, anchors = stage.sockets };
    }

    /// <summary>Replaces the imported take <paramref name="name"/> ("open" or "close") with a clip holding only the opening parts'
    /// bones (their position and rotation; the exporter bakes every bone into every take), so sampling it moves the parts and nothing
    /// else. Every listed part must have its curves, and any scale curve must stay at 1: the parts are rigid.</summary>
    static void OpeningClip(FrameStage stage, Animation animation, string name)
    {
        if (stage.opening == null || stage.opening.Length == 0) throw new InvalidDataException(stage.prefab + " has no opening parts in its definition.");
        var source = animation.GetClip(name);
        var clip = new AnimationClip { name = name, legacy = true, frameRate = source.frameRate, wrapMode = WrapMode.ClampForever };
        var moved = new HashSet<string>();
        foreach (var binding in AnimationUtility.GetCurveBindings(source))
        {
            var node = binding.path.Substring(binding.path.LastIndexOf('/') + 1);
            if (binding.type != typeof(Transform) || Array.IndexOf(stage.opening, node) < 0) continue;
            var curve = AnimationUtility.GetEditorCurve(source, binding);
            if (binding.propertyName.StartsWith("m_LocalScale", StringComparison.Ordinal))
            {
                if (curve.keys.Any(k => Math.Abs(k.value - 1f) > 1e-4f)) throw new InvalidDataException(stage.prefab + " " + node + " changes its scale while opening.");
                continue;
            }
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            moved.Add(node);
        }
        var missing = stage.opening.Where(n => !moved.Contains(n)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException(stage.prefab + " " + name + " clip does not move " + string.Join(", ", missing));
        clip.EnsureQuaternionContinuity();
        // A native asset takes its file's name when it is loaded, and the game finds the clip by name: the file is "<name>.anim".
        var folder = Generated + "/" + stage.prefab;
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(Generated, stage.prefab);
        AssetDatabase.CreateAsset(clip, folder + "/" + name + ".anim");
        animation.RemoveClip(name);
        animation.AddClip(clip, name);
        Debug.Log("PLATE_CLIP " + stage.prefab + " " + name + " " + moved.Count + " parts, " + clip.length.ToString("F3") + " s");
    }

    /// <summary>Poses the opening at <paramref name="open"/> (0 shut, 1 open) of the "open" clip, the way FrameVisual does.</summary>
    static void Pose(GameObject model, AnimationClip clip, float open) => clip.SampleAnimation(model, Mathf.Clamp01(open) * clip.length);

    /// <summary>Checks the opening on the imported mesh. At 0, 1/3, 2/3 and the whole of the opening every part keeps scale 1 (local
    /// and in the world); shut, every part is at its bind pose; each part's own vertices move (<see cref="MovesAtLeast"/>) and end
    /// no more than <see cref="TuckAtMost"/> nearer the body (its nearest axis, <see cref="AxisEnd"/>: a part that goes into the body is wrong, unless the owner allowed its hinge into the body in the add-on: frame.json intoBody), and every pilot-slot ray the model build checked
    /// (frame.json slotRays: from inside each body part's cavity straight out of the back) meets no triangle of the open suit within
    /// <see cref="SlotDepth"/>: the build's check, on Unity's mesh.</summary>
    static void CheckOpening(FrameStage stage, GameObject root, SkinnedMeshRenderer skin)
    {
        if (stage.slotRays == null || stage.slotRays.Length == 0 || stage.slotRays.Length % 6 != 0) throw new InvalidDataException(stage.prefab + " frame.json has no slotRays.");
        var model = root.transform.Find("model").gameObject;
        var clip = model.GetComponent<Animation>().GetClip("open");
        var bones = skin.bones;
        var weights = skin.sharedMesh.boneWeights;
        // The build cast the slot rays at the rest pose; the import may leave the bones at a clip's first frame.
        BindPose(skin);
        var mesh = new Mesh();
        var nodes = new List<(Transform node, int index, Vector3 position, Quaternion rotation)>();
        try
        {
            foreach (var part in stage.opening)
            {
                var index = Array.FindIndex(bones, b => b != null && b.name == part);
                if (index < 0) throw new InvalidDataException(stage.prefab + " has no bone " + part + " (Opening).");
                nodes.Add((bones[index], index, bones[index].localPosition, bones[index].localRotation));
            }
            Pose(model, clip, 0f);
            foreach (var (node, _, position, rotation) in nodes)
                if ((node.localPosition - position).magnitude > 1e-3f || Quaternion.Angle(node.localRotation, rotation) > 0.1f)
                    throw new InvalidDataException(stage.prefab + " " + node.name + " is not at its place on the shut suit at the open clip's first frame: moved "
                        + (node.localPosition - position).magnitude.ToString("F4") + " m, turned " + Quaternion.Angle(node.localRotation, rotation).ToString("F2") + " degrees.");
            foreach (var open in new[] { 0f, 1f / 3f, 2f / 3f, 1f })
            {
                Pose(model, clip, open);
                foreach (var (node, _, _, _) in nodes)
                    if ((node.localScale - Vector3.one).sqrMagnitude > 1e-8f || (node.lossyScale - root.transform.lossyScale).sqrMagnitude > 1e-6f)
                        throw new InvalidDataException(stage.prefab + " " + node.name + " is not at scale 1 at " + open.ToString("F2") + " of the opening: " + node.lossyScale.ToString("F4"));
            }
            Debug.Log("OPENING_SCALE " + stage.prefab + " " + nodes.Count + " parts at scale 1 at 0, 1/3, 2/3 and 1 of the opening");
            // The body's axes, in the root's space at the bind pose: each limb from its joint to the next, the trunk from the
            // pelvis up to the head (AxisEnd). A part opens away from the body (round a hinge along a limb, the halves of a limb's
            // shell swing out from it, a shoulder cap lifts off the shoulder toward the neck); one that ends nearer the nearest
            // of them than a tuck moves into the body.
            Pose(model, clip, 0f);
            var axes = new List<(string name, Vector3 from, Vector3 to)>();
            foreach (var b in bones.Where(b => b != null && !b.name.StartsWith("plate.")))
            {
                var dot = b.name.IndexOf('.');
                var kind = dot < 0 ? b.name : b.name.Substring(0, dot);
                if (!AxisEnd.TryGetValue(kind, out var end)) continue;
                var endName = end + (dot < 0 ? "" : b.name.Substring(dot));
                var endBone = bones.FirstOrDefault(x => x != null && x.name == endName) ?? throw new InvalidDataException(stage.prefab + " has no bone " + endName + ".");
                axes.Add((b.name, root.transform.InverseTransformPoint(b.position), root.transform.InverseTransformPoint(endBone.position)));
            }
            if (axes.Count == 0) throw new InvalidDataException(stage.prefab + " has none of the body's bones.");
            (float distance, string name) Out(Vector3 p)
            {
                var best = (float.MaxValue, "");
                foreach (var (name, from, to) in axes)
                {
                    var d = to - from;
                    var t = Mathf.Clamp01(Vector3.Dot(p - from, d) / Mathf.Max(1e-8f, d.sqrMagnitude));
                    var r = (p - (from + d * t)).magnitude;
                    if (r < best.Item1) best = (r, name);
                }
                return best;
            }
            foreach (var (node, index, _, _) in nodes)
            {
                Pose(model, clip, 0f);
                var before = Centroid(skin, mesh, weights, index, root.transform);
                var reach = 0f;
                for (var k = 1; k <= 8; k++)
                {
                    Pose(model, clip, k / 8f);
                    reach = Math.Max(reach, (Centroid(skin, mesh, weights, index, root.transform) - before).magnitude);
                }
                var after = Centroid(skin, mesh, weights, index, root.transform);
                var (shut, shutAxis) = Out(before);
                var (opened, openAxis) = Out(after);
                Debug.Log("OPENING " + stage.prefab + " " + node.name + " centre " + before.ToString("F3") + " -> " + after.ToString("F3")
                    + ", from the body " + shut.ToString("F3") + " m (" + shutAxis + " axis) -> " + opened.ToString("F3") + " m (" + openAxis + " axis), moved at most " + reach.ToString("F3") + " m");
                // A part may swing out, slide along the suit, tuck under its neighbour (about a shell's thickness) or flare and
                // drop back: it must move, and must not end deeper toward the body than a tuck.
                if (reach < MovesAtLeast) throw new InvalidDataException(stage.prefab + " " + node.name + " does not move in the open clip.");
                if (opened < shut - TuckAtMost && Array.IndexOf(stage.intoBody ?? new string[0], node.name) >= 0)
                    Debug.Log("OPENING " + stage.prefab + " " + node.name + " ends " + (shut - opened).ToString("F3") + " m nearer the body (" + openAxis + " axis): the owner allowed it into the body in the add-on.");
                else if (opened < shut - TuckAtMost) throw new InvalidDataException(stage.prefab + " " + node.name + " ends " + (shut - opened).ToString("F3") + " m nearer the body (" + openAxis + " axis) than it starts (" + shutAxis + " axis): it moves into the body.");
            }
            Pose(model, clip, 1f);
            skin.BakeMesh(mesh);
            var vertices = mesh.vertices.Select(v => root.transform.InverseTransformPoint(skin.transform.TransformPoint(v))).ToArray();
            var triangles = mesh.triangles;
            var blocked = new Dictionary<string, int>();
            var rays = stage.slotRays.Length / 6;
            for (var r = 0; r < rays; r++)
            {
                // Blender (x, y, z) is the suit's Unity space (-x, z, -y).
                var a = stage.slotRays;
                var o = new Vector3(-a[6 * r], a[6 * r + 2], -a[6 * r + 1]);
                var d = new Vector3(-a[6 * r + 3], a[6 * r + 5], -a[6 * r + 4]).normalized;
                var hit = FirstHit(o, d, vertices, triangles);
                if (hit < 0) continue;
                var name = bones[weights[triangles[hit]].boneIndex0].name;
                blocked[name] = blocked.TryGetValue(name, out var n) ? n + 1 : 1;
            }
            // Reported only: the slots are a reference body, and the opening is judged by eye.
            Debug.Log("SLOTS " + stage.prefab + " " + (rays - blocked.Values.Sum()) + " of " + rays + " pilot-slot rays clear with all " + stage.opening.Length + " parts open"
                + (blocked.Count > 0 ? "; blocked by " + string.Join(", ", blocked.Select(b => b.Key + " (" + b.Value + " rays)")) : ""));
            var close = model.GetComponent<Animation>().GetClip("close");
            close.SampleAnimation(model, close.length);
            foreach (var (node, _, position, rotation) in nodes)
                if ((node.localPosition - position).magnitude > 1e-3f || Quaternion.Angle(node.localRotation, rotation) > 0.1f)
                    throw new InvalidDataException(stage.prefab + " " + node.name + " is not shut at the close clip's last frame.");
        }
        finally
        {
            Pose(model, clip, 0f);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
    /// <summary>The first triangle (its first index in <paramref name="triangles"/>) the ray from <paramref name="o"/> along unit
    /// <paramref name="d"/> meets within <see cref="SlotDepth"/>, or -1 (Moller-Trumbore, both faces).</summary>
    static int FirstHit(Vector3 o, Vector3 d, Vector3[] v, int[] triangles)
    {
        var best = SlotDepth;
        var found = -1;
        for (var i = 0; i < triangles.Length; i += 3)
        {
            var p0 = v[triangles[i]];
            var e1 = v[triangles[i + 1]] - p0;
            var e2 = v[triangles[i + 2]] - p0;
            var h = Vector3.Cross(d, e2);
            var det = Vector3.Dot(e1, h);
            if (Math.Abs(det) < 1e-12f) continue;
            var s = o - p0;
            var u = Vector3.Dot(s, h) / det;
            if (u < 0f || u > 1f) continue;
            var q = Vector3.Cross(s, e1);
            var w = Vector3.Dot(d, q) / det;
            if (w < 0f || u + w > 1f) continue;
            var t = Vector3.Dot(e2, q) / det;
            if (t > 0f && t < best) { best = t; found = i; }
        }
        return found;
    }

    /// <summary>The model's surface: two sub-meshes, the armour (every UV inside the 0..1 atlas) and the lining (the triangle
    /// count the build wrote; its UVs tile), and the shell closed and outward. Blender writes a second UV
    /// set (x = its vertex index), so the check sees Blender's topology rather than Unity's
    /// split vertices: every shell edge must be used by exactly two triangles running opposite ways, and every connected shell
    /// piece must enclose positive volume.</summary>
    /// <summary>Per model, the imported sub-mesh holding the armour, the fabric lining and the bare-metal lining (CheckSurface); ShippedMesh puts them in that order.</summary>
    static int[] SubmeshOrder;

    static void CheckSurface(FrameStage stage, Mesh mesh)
    {
        var prefab = stage.prefab;
        var uv = mesh.uv;
        var ids = mesh.uv2;
        var tris = mesh.triangles;
        var positions = mesh.vertices;
        if (ids == null || ids.Length != uv.Length) throw new InvalidDataException(prefab + " has no vertex-id UV set; rebuild it with build_frame.py.");
        if (mesh.subMeshCount != 4) throw new InvalidDataException(prefab + " has " + mesh.subMeshCount + " sub-meshes; expected the armour and the fabric, bare-metal and rubber linings.");
        if (stage.liningTriangles == null || stage.liningTriangles.Length != 3) throw new InvalidDataException(prefab + " frame.json lists no fabric, bare-metal and rubber lining triangle counts.");
        // The importer orders the sub-meshes by first use, not by the build's material order: find each kind by its triangle count.
        var lining = 0;
        SubmeshOrder = new[] { -1, -1, -1, -1 };
        for (var kind = 0; kind < 3; kind++)
        {
            var want = stage.liningTriangles[kind];
            for (var sub = 0; sub < 4; sub++)
                if (mesh.GetTriangles(sub).Length / 3 == want && Array.IndexOf(SubmeshOrder, sub) < 0 && SubmeshOrder[kind + 1] < 0) SubmeshOrder[kind + 1] = sub;
            if (want <= 0 || SubmeshOrder[kind + 1] < 0) throw new InvalidDataException(prefab + " has no sub-mesh with the " + want + " " + LiningKinds[kind].name + " triangles the build made.");
            lining += want;
        }
        SubmeshOrder[0] = Enumerable.Range(0, 4).First(i => Array.IndexOf(SubmeshOrder, i) < 0);
        foreach (var i in mesh.GetTriangles(SubmeshOrder[0]))
            if (!(uv[i].x >= 0f && uv[i].x <= 1f && uv[i].y >= 0f && uv[i].y <= 1f))
                throw new InvalidDataException(prefab + " has an armour UV outside the atlas: " + uv[i].ToString("F4"));
        var edges = new Dictionary<long, int>();
        var shellTriangles = tris.Length / 3;
        for (var t = 0; t < tris.Length; t += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                long a = Mathf.RoundToInt(ids[tris[t + k]].x), b = Mathf.RoundToInt(ids[tris[t + (k + 1) % 3]].x);
                var key = (a << 32) | b;
                edges[key] = edges.TryGetValue(key, out var n) ? n + 1 : 1;
            }
        }
        var open = 0;
        foreach (var e in edges)
        {
            var reverse = ((e.Key & 0xffffffffL) << 32) | (e.Key >> 32);
            if (e.Value != 1 || !edges.TryGetValue(reverse, out var r) || r != 1) open++;
        }
        // Signed volume per connected piece (vertices joined through shared Blender ids).
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int x, int y) { if (!parent.ContainsKey(x)) parent[x] = x; if (!parent.ContainsKey(y)) parent[y] = y; x = Find(x); y = Find(y); if (x != y) parent[x] = y; }
        for (var t = 0; t < tris.Length; t += 3)
        {
            int a = Mathf.RoundToInt(ids[tris[t]].x), b = Mathf.RoundToInt(ids[tris[t + 1]].x), c = Mathf.RoundToInt(ids[tris[t + 2]].x);
            Union(a, b); Union(b, c);
        }
        var volume = new Dictionary<int, double>();
        for (var t = 0; t < tris.Length; t += 3)
        {
            var piece = Find(Mathf.RoundToInt(ids[tris[t]].x));
            Vector3 p0 = positions[tris[t]], p1 = positions[tris[t + 1]], p2 = positions[tris[t + 2]];
            volume[piece] = (volume.TryGetValue(piece, out var v) ? v : 0.0) + Vector3.Dot(p0, Vector3.Cross(p1, p2)) / 6.0;
        }
        var inward = volume.Count(v => v.Value <= 0.0);
        Debug.Log("SURFACE " + prefab + " " + shellTriangles + " shell triangles (" + lining + " lining) in " + volume.Count + " closed pieces, " + open + " open or reversed edges, " + inward + " inward pieces, all armour UVs in the atlas");
        if (open > 0 || inward > 0) throw new InvalidDataException(prefab + " shell is not closed and outward: " + open + " open or reversed edges, " + inward + " inward pieces.");
    }

    /// <summary>A copy of the imported mesh without the id UV set (only the check reads it), saved as its own asset for the prefab.</summary>
    static Mesh ShippedMesh(Mesh imported, string path)
    {
        var mesh = UnityEngine.Object.Instantiate(imported);
        mesh.name = imported.name;
        mesh.uv2 = new Vector2[0];
        var parts = SubmeshOrder.Select(i => imported.GetTriangles(i)).ToArray();
        for (var i = 0; i < parts.Length; i++) mesh.SetTriangles(parts[i], i);
        if (mesh.uv2.Length != 0) throw new InvalidOperationException("Could not drop the id UV set from " + imported.name + ".");
        if (!mesh.isReadable) throw new InvalidOperationException(imported.name + " copy is not readable; the first-person shell cuts it at run time.");
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    static Vector3 Centroid(SkinnedMeshRenderer skin, Mesh mesh, BoneWeight[] weights, int bone, Transform root)
    {
        skin.BakeMesh(mesh);
        var vertices = mesh.vertices;
        var sum = Vector3.zero;
        var count = 0;
        for (var i = 0; i < vertices.Length; i++)
        {
            if (weights[i].boneIndex0 != bone || weights[i].weight0 < 0.99f) continue;
            sum += root.InverseTransformPoint(skin.transform.TransformPoint(vertices[i]));
            count++;
        }
        if (count == 0) throw new InvalidDataException(skin.name + " has no vertices on bone " + bone + ".");
        return sum / count;
    }

    /// <summary>Every bone of <paramref name="skin"/> at its bind pose, parents before children.</summary>
    static void BindPose(SkinnedMeshRenderer skin)
    {
        var bones = skin.bones;
        var bind = skin.sharedMesh.bindposes;
        int Depth(Transform t) { var d = 0; for (; t != null; t = t.parent) d++; return d; }
        foreach (var i in Enumerable.Range(0, bones.Length).Where(i => bones[i] != null).OrderBy(i => Depth(bones[i])))
        {
            var world = skin.transform.localToWorldMatrix * bind[i].inverse;
            bones[i].SetPositionAndRotation(world.GetColumn(3), world.rotation);
        }
    }

    /// <summary>Signed reach of the mesh along +Z in the root's frame, logged for review only: the facing comes from the
    /// export convention (Blender -Y is the front, written as +Z), not from this number; a tank's back plating outreaches its
    /// arms.</summary>
    static float Front(SkinnedMeshRenderer skin)
    {
        var mesh = new Mesh();
        skin.BakeMesh(mesh);
        var minZ = float.MaxValue; var maxZ = float.MinValue;
        var root = skin.transform.root;
        foreach (var v in mesh.vertices)
        {
            var p = root.InverseTransformPoint(skin.transform.TransformPoint(v));
            minZ = Math.Min(minZ, p.z); maxZ = Math.Max(maxZ, p.z);
        }
        UnityEngine.Object.DestroyImmediate(mesh);
        return maxZ + minZ;
    }

    /// <summary>Samples every imported clip at twice its frame rate. The extra padding covers interpolation and cross-fades
    /// between sampled poses; the resulting renderer-local bounds include the fully open boarding plates.</summary>
    static Bounds AnimatedBounds(GameObject model, SkinnedMeshRenderer skin, Animation animation)
    {
        var result = new Bounds();
        var found = false;
        var mesh = new Mesh();
        var vertices = new List<Vector3>();
        try
        {
            foreach (var name in Clips)
            {
                var clip = animation.GetClip(name) ?? throw new InvalidDataException(model.name + " lacks clip " + name);
                var samples = Math.Max(1, Mathf.CeilToInt(clip.length * Math.Max(clip.frameRate, 1f) * BoundsSamplesPerFrame));
                for (var sample = 0; sample <= samples; sample++)
                {
                    clip.SampleAnimation(model, clip.length * sample / samples);
                    skin.BakeMesh(mesh);
                    vertices.Clear();
                    mesh.GetVertices(vertices);
                    foreach (var vertex in vertices)
                    {
                        if (!found) { result = new Bounds(vertex, Vector3.zero); found = true; }
                        else result.Encapsulate(vertex);
                    }
                }
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(mesh); }
        if (!found) throw new InvalidDataException(model.name + " has no skinned vertices for animated bounds.");
        result.Expand(BoundsPadding * 2f);
        return result;
    }

    static Bounds Bounds(GameObject root)
    {
        var b = new Bounds(root.transform.position, Vector3.zero);
        foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = new Mesh();
            r.BakeMesh(mesh);
            foreach (var v in mesh.vertices) b.Encapsulate(r.transform.TransformPoint(v));
            UnityEngine.Object.DestroyImmediate(mesh);
        }
        return b;
    }

    static string RelativePath(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (var c = t; c != null && c != root; c = c.parent) parts.Insert(0, c.name);
        return string.Join("/", parts);
    }

    static void Verify(string file, Profile profile, Dictionary<string, int> propTextures)
    {
        bool IsProp(string name) => propTextures.ContainsKey(name + "/body");
        if (!profile.prefabs.Select(p => p.name).Where(n => n != PilotPrefab && !IsProp(n)).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(RequiredModels)
            || profile.prefabs.Count(p => p.name == PilotPrefab) != 1)
            throw new InvalidDataException("Frames release bundles require all four models M15-M18 and the pilot motion, without duplicates.");
        var bundle = AssetBundle.LoadFromFile(file) ?? throw new InvalidDataException("The built bundle does not open.");
        try
        {
            if (bundle.LoadAsset<TextAsset>("ordnance-profile") == null) throw new InvalidDataException("Profile missing.");
            foreach (var p in profile.prefabs)
            {
                var go = bundle.LoadAsset<GameObject>(p.name) ?? throw new InvalidDataException("Prefab missing: " + p.name);
                if (p.name == PilotPrefab)
                {
                    var motion = go.transform.Find("model")?.GetComponent<Animation>() ?? throw new InvalidDataException(p.name + " lost its Animation component.");
                    foreach (var clip in PilotClips) if (motion.GetClip(clip) == null) throw new InvalidDataException(p.name + " lost clip " + clip);
                    if (go.transform.Find("eye") == null) throw new InvalidDataException(p.name + " lost its eye point.");
                    continue;
                }
                if (IsProp(p.name))
                {
                    foreach (var r in p.renderers)
                        if (go.transform.Find(r.name)?.GetComponent<MeshRenderer>() == null) throw new InvalidDataException(p.name + "/" + r.name + " is not a mesh renderer.");
                    foreach (var socket in p.anchors)
                        if (!go.GetComponentsInChildren<Transform>(true).Any(t => t.name == socket)) throw new InvalidDataException(p.name + " lost its socket " + socket + ".");
                    continue;
                }
                foreach (var r in p.renderers)
                {
                    var t = go.transform.Find(r.name) ?? throw new InvalidDataException(p.name + " lacks " + r.name);
                    var skin = t.GetComponent<SkinnedMeshRenderer>() ?? throw new InvalidDataException(p.name + "/" + r.name + " is not a skinned renderer.");
                    if (skin.updateWhenOffscreen) throw new InvalidDataException(p.name + "/" + r.name + " still forces offscreen skinning.");
                    if (skin.localBounds.size.x <= 0f || skin.localBounds.size.y <= 0f || skin.localBounds.size.z <= 0f)
                        throw new InvalidDataException(p.name + "/" + r.name + " has empty animated bounds.");
                }
                var anim = go.transform.Find("model")?.GetComponent<Animation>() ?? throw new InvalidDataException(p.name + " lost its Animation component.");
                foreach (var clip in Clips) if (anim.GetClip(clip) == null) throw new InvalidDataException(p.name + " lost clip " + clip);
            }
            foreach (var s in profile.surfaces)
            {
                if (propTextures.TryGetValue(s.key, out var propSize))
                {
                    VerifyTexture(bundle, s.albedo, propSize);
                    VerifyTexture(bundle, s.normal, propSize);
                    VerifyTexture(bundle, s.metallic, propSize);
                    continue;
                }
                var kind = Array.FindIndex(LiningKinds, k => s.key.EndsWith("/" + k.name, StringComparison.Ordinal));
                var lining = kind >= 0;
                VerifyTexture(bundle, s.albedo, lining ? LiningKinds[kind].size : AtlasSize);
                VerifyTexture(bundle, s.normal, lining ? LiningKinds[kind].size : DetailSize);
                VerifyTexture(bundle, s.metallic, lining ? LiningKinds[kind].size : MetalSize);
                if (!lining) VerifyTexture(bundle, s.emission, DetailSize);
            }
        }
        finally { bundle.Unload(true); }
    }

    static void VerifyTexture(AssetBundle bundle, string name, int size)
    {
        var texture = bundle.LoadAsset<Texture2D>(name) ?? throw new InvalidDataException("Texture missing: " + name);
        if (texture.width != size || texture.height != size)
            throw new InvalidDataException(name + " is " + texture.width + "x" + texture.height + "; expected " + size + "x" + size + ".");
    }

    static string Texture(string path, TextureImporterType type, bool srgb, int maxSize)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter ?? throw new InvalidDataException("No texture importer for " + path);
        importer.textureType = type;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.sRGBTexture = srgb;
        importer.mipmapEnabled = true;
        importer.isReadable = false;
        importer.maxTextureSize = maxSize;
        importer.textureCompression = TextureImporterCompression.Compressed;
        importer.SaveAndReimport();
        return path;
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(from)) Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}

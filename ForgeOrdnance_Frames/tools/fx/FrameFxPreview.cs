#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Renders key frames of every exosuit effect with Unity 2019.4.21f1 (needs a graphics device: no -nographics) from the
/// prefab FrameFxBuild saved. Each moment replays the plugin's recipe (Native/FrameFx.cs and Core/FrameEffectsPlan.cs) against the
/// real authored particle systems in edit-mode simulation, with a 2.1 m grey stand-in for the suit, and writes PNG frames to
/// FORGE_FX_PREVIEW/&lt;moment&gt;/. tools/fx/make_previews.py turns them into GIFs and the index page.
/// Environment: FORGE_FX_PREVIEW (output).</summary>
public static class FrameFxPreview
{
    const int W = 640, H = 360;
    const float Step = 1f / 30f;
    static readonly Quaternion Ground = Quaternion.Euler(-90f, 0f, 0f);
    static readonly Quaternion Down = Quaternion.Euler(90f, 0f, 0f);
    static readonly Color Tan = new Color(0.92f, 0.8f, 0.62f, 0.8f), White = new Color(0.95f, 0.97f, 1f, 0.6f),
        CrackColour = new Color(0.1f, 0.08f, 0.06f, 0.85f), Flash = new Color(1f, 0.82f, 0.5f, 0.9f);

    static Dictionary<string, ParticleSystem> systems;
    static Camera camera;
    static RenderTexture target;
    static Transform figure;
    static string outDir;
    static Texture2D readback;

    public static void Render()
    {
        try { Run(); EditorApplication.Exit(0); }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void Run()
    {
        outDir = Environment.GetEnvironmentVariable("FORGE_FX_PREVIEW") ?? throw new InvalidOperationException("FORGE_FX_PREVIEW is not set.");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/FrameFx/FramesFx.prefab") ?? throw new InvalidOperationException("Build the effects first.");
        var holder = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        systems = new Dictionary<string, ParticleSystem>();
        foreach (var ps in holder.GetComponentsInChildren<ParticleSystem>()) systems[ps.name] = ps;

        var camObject = new GameObject("cam");
        camera = camObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.Nothing;
        camera.backgroundColor = new Color(0.06f, 0.07f, 0.09f);
        camera.fieldOfView = 50f;
        camera.allowMSAA = false;
        camera.allowHDR = false;
        camera.allowDynamicResolution = false;
        QualitySettings.antiAliasing = 0;
        target = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
        camera.targetTexture = target;
        readback = new Texture2D(W, H, TextureFormat.RGB24, false);

        var flat = new Material(PreviewShader()) { color = new Color(0.16f, 0.15f, 0.14f) };
        Debug.Log("PIXEL shader " + (flat.shader == null ? "null" : flat.shader.name + " supported " + flat.shader.isSupported));
        var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
        floor.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        floor.transform.localScale = new Vector3(200f, 200f, 1f);
        floor.GetComponent<Renderer>().sharedMaterial = flat;
        var grid = new Material(PreviewShader()) { color = new Color(0.24f, 0.23f, 0.22f) };
        for (var i = -10; i <= 10; i++)
        {
            foreach (var along in new[] { true, false })
            {
                var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
                line.transform.position = along ? new Vector3(i * 4f, 0.005f, 0f) : new Vector3(0f, 0.005f, i * 4f);
                line.transform.localScale = along ? new Vector3(0.05f, 0.01f, 80f) : new Vector3(80f, 0.01f, 0.05f);
                line.GetComponent<Renderer>().sharedMaterial = grid;
            }
        }
        figure = MakeFigure();

        Moment("01-arrival", 1.35f, 0.05f, Arrival);
        Moment("02-landing", 3.0f, 0.1f, Landing);
        Moment("03-charge", 3.0f, 0.1f, Charge);
        Moment("04-siege", 2.0f, 0.1f, Siege);
        Moment("05-rockets", 2.4f, 0.1f, Rockets);
        Moment("07-push-pulse", 1.2f, 0.05f, Pulse);
        Moment("08-destroyed", 12.0f, 0.5f, Destroyed);
        Moment("09-eject", 2.0f, 0.1f, Eject);
        Moment("10-overheat", 4.0f, 0.2f, Heat);
        Debug.Log("FRAMES_FX_PREVIEWED " + outDir);
    }

    static Shader PreviewShader()
    {
        AssetDatabase.ImportAsset("Assets/Editor/PreviewUnlit.shader", ImportAssetOptions.ForceSynchronousImport);
        return AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewUnlit.shader");
    }

    static Transform MakeFigure()
    {
        var root = new GameObject("figure").transform;
        var m = new Material(PreviewShader()) { color = new Color(0.45f, 0.5f, 0.55f) };
        void Part(Vector3 at, Vector3 size)
        {
            var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
            c.transform.SetParent(root, false);
            c.transform.localPosition = at;
            c.transform.localScale = size;
            c.GetComponent<Renderer>().sharedMaterial = m;
        }
        Part(new Vector3(0, 1.0f, 0), new Vector3(0.9f, 1.2f, 0.55f));
        Part(new Vector3(0, 1.85f, 0), new Vector3(0.45f, 0.4f, 0.45f));
        Part(new Vector3(-0.3f, 0.35f, 0), new Vector3(0.3f, 0.7f, 0.3f));
        Part(new Vector3(0.3f, 0.35f, 0), new Vector3(0.3f, 0.7f, 0.3f));
        Part(new Vector3(-0.62f, 1.1f, 0), new Vector3(0.22f, 0.9f, 0.25f));
        Part(new Vector3(0.62f, 1.1f, 0), new Vector3(0.22f, 0.9f, 0.25f));
        return root;
    }

    // ---- stepping ----

    static void Moment(string name, float seconds, float every, Action<float, float> recipe)
    {
        var only = Environment.GetEnvironmentVariable("FORGE_FX_ONLY");
        if (!string.IsNullOrEmpty(only) && !Array.Exists(only.Split(','), n => n == name)) return;
        var dir = Path.Combine(outDir, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        foreach (var ps in systems.Values) { ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); ps.Simulate(0f, false, true); }
        figure.gameObject.SetActive(true);
        figure.position = Vector3.zero;
        figure.rotation = Quaternion.identity;
        var frame = 0;
        var nextShot = 0f;
        for (var t = 0f; t <= seconds + 1e-4f; t += Step)
        {
            recipe(t, Step);
            foreach (var ps in systems.Values) ps.Simulate(Step, false, false, true);
            if (t + 1e-4f >= nextShot)
            {
                nextShot += t < 2f ? Mathf.Min(every, 0.1f) : every;
                Shot(Path.Combine(dir, "f" + Mathf.RoundToInt(t * 1000f).ToString("00000") + ".png"));
                frame++;
            }
        }
    }

    static void Shot(string path)
    {
        RenderTexture.active = target;
        GL.Clear(true, true, new Color(0.06f, 0.07f, 0.09f));
        readback.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        Debug.Log("PIXEL after clear " + readback.GetPixel(5, 5));
        camera.Render();
        RenderTexture.active = target;
        readback.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        Debug.Log("PIXEL after render " + readback.GetPixel(5, 5) + " centre " + readback.GetPixel(W / 2, H / 3));
        RenderTexture.active = target;
        readback.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        readback.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes(path, readback.EncodeToPNG());
    }

    static void Look(Vector3 from, Vector3 at) { camera.transform.position = from; camera.transform.LookAt(at); }

    // ---- the plugin's recipes, as authored ----

    static void Burst(string kind, Vector3 at, Quaternion rotation, int count)
    {
        var ps = systems[kind];
        ps.transform.SetPositionAndRotation(at, rotation);
        ps.Emit(count);
    }

    static void Place(string kind, Vector3 at, float size, float life, Color colour) => systems[kind].Emit(at, Vector3.zero, size, life, colour);

    static float thrusterNext, chargeNext, wreckFlame, wreckSmoke, heatNext;
    static int chargeTicks;

    static void Shoot(string kind, Vector3 at, Vector3 velocity, float size, float life, Color colour) => systems[kind].Emit(at, velocity, size, life, colour);

    static void Jet(Vector3 nozzle, Vector3 carry, Vector3 direction, float strength)
    {
        var speed = 10f + 10f * strength;
        var size = 0.45f + 0.5f * strength;
        Shoot("JetCone", nozzle, carry + direction * speed, size, 0.11f + 0.06f * strength, Color.white);
        Shoot("Thruster", nozzle + direction * 0.15f, carry + direction * (speed * 0.7f), size * 0.9f, 0.14f, Color.white);
    }

    static bool kicked;

    static void Arrival(float t, float dt)
    {
        var fall = Mathf.Clamp01(t / 1.2f);
        figure.position = Vector3.up * (40f * (1f - fall * fall));
        Look(figure.position + new Vector3(5f, 2.5f, -9f), figure.position + Vector3.up * 1f);
        if (t == 0f) kicked = false;
        var strength = ArrivalThrust(fall);
        if (strength <= 0f || fall >= 1f) return;
        var carry = Vector3.down * (40f * 2f * fall / 1.2f);
        var origin = figure.position + Vector3.up * 1.5f - figure.forward * 0.5f;
        Jet(origin + figure.right * 0.3f, carry, Vector3.down, strength);
        Jet(origin - figure.right * 0.3f, carry, Vector3.down, strength);
        if (kicked || fall < 0.92f) return;
        kicked = true;
        Burst("BurstDust", Vector3.up * 0.1f, Ground, 10);
        Place("Ring", Vector3.up * 0.12f, 7f, 0.45f, Tan);
    }

    static float ArrivalThrust(float fall)
    {
        var x = (fall - 0.12f) / 0.88f;
        return x <= 0f ? 0f : x >= 1f ? 1f : x * x;
    }

    static void Landing(float t, float dt)
    {
        Look(new Vector3(6f, 2.2f, -7.5f), new Vector3(0f, 1.2f, 0f));
        if (t > 0f) return;
        var ground = Vector3.zero;
        var low = ground + Vector3.up * 0.1f;
        Burst("BurstDust", low, Ground, 24);
        Burst("ColumnSmoke", ground, Ground, 10);
        Burst("Chunks", low, Ground, 14);
        Burst("Ember", low, Ground, 10);
        Place("Ring", ground + Vector3.up * 0.15f, 14f, 0.7f, Tan);
        Place("Crack", ground + Vector3.up * 0.04f, 3.6f, 10f, CrackColour);
    }

    static void Charge(float t, float dt)
    {
        figure.position = new Vector3(-2f, 0f, 0f) + Vector3.right * (t * 8f);
        figure.rotation = Quaternion.Euler(0f, 90f, 0f);
        Look(figure.position + new Vector3(-4f, 2.2f, -7f), figure.position + Vector3.up * 1.2f + Vector3.right * 3f);
        if (t == 0f) { chargeNext = 0f; chargeTicks = 0; }
        if (t >= chargeNext)
        {
            chargeNext = t + 0.06f;
            var f = figure.forward;
            var back = figure.position + Vector3.up * 1.5f - f * 0.45f;
            Jet(back, f * 8f, -f - Vector3.up * 0.1f, 0.6f);
            if (chargeTicks++ % 2 == 0) Burst("TrailDust", figure.position + Vector3.up * 0.1f, Quaternion.LookRotation(-f + Vector3.up * 0.15f), 1);
        }
    }

    static void Siege(float t, float dt)
    {
        Look(new Vector3(4.5f, 2f, -6f), new Vector3(0f, 1f, 0f));
        if (t > 0f) return;
        var low = Vector3.up * 0.1f;
        Burst("BurstDust", low, Ground, 12);
        Burst("Chunks", low, Ground, 4);
        Place("Ring", Vector3.up * 0.15f, 6f, 0.5f, Tan);
    }

    static readonly float[] RocketDelay = { 0f, 0.12f, 0.24f, 0.36f };
    static readonly bool[] RocketFired = new bool[4];
    static readonly float[] RocketPuff = new float[4];
    static readonly int[] RocketCount = new int[4];

    static void Rockets(float t, float dt)
    {
        Look(new Vector3(9f, 3f, -4f), new Vector3(0f, 3f, 8f));
        if (t == 0f) for (var i = 0; i < 4; i++) { RocketFired[i] = false; RocketPuff[i] = 0f; RocketCount[i] = 0; }
        for (var i = 0; i < 4; i++)
        {
            var local = t - RocketDelay[i];
            if (local < 0f) continue;
            var from = figure.position + Vector3.up * 2.15f + Vector3.right * ((i & 1) == 0 ? 0.35f : -0.35f) - Vector3.forward * 0.1f;
            var to = from + new Vector3(0.4f * i - 0.6f, -1f, 45f);
            var u = local / 0.9f;
            if (u >= 1f) continue;
            var p = Vector3.Lerp(from, to, u) + Vector3.up * Mathf.Sin(u * Mathf.PI) * 3f;
            var q = Vector3.Lerp(from, to, u + 0.02f) + Vector3.up * Mathf.Sin((u + 0.02f) * Mathf.PI) * 3f;
            var back = (p - q).normalized;
            if (!RocketFired[i]) { RocketFired[i] = true; Burst("FlameBlast", from, Quaternion.LookRotation(back), 5); Burst("SmokeLight", from, Quaternion.identity, 3); }
            if (t >= RocketPuff[i])
            {
                RocketPuff[i] = t + 0.045f;
                Burst("FlameSmall", p, Quaternion.LookRotation(back), 1);
                if ((RocketCount[i]++ & 1) == 0) Burst("SmokeLight", p, Quaternion.identity, 1);
            }
        }
    }

    static void Pulse(float t, float dt)
    {
        Look(new Vector3(4.5f, 3f, -6f), new Vector3(0f, 1.2f, 0f));
        if (t == 0f) Place("PushRing", Vector3.up * 1.2f, (2.75f + 2.2f) * 2f, 0.5f, White);
    }

    static void Destroyed(float t, float dt)
    {
        Look(new Vector3(5.5f, 2.5f, -8f), new Vector3(0f, 1.2f, 0f));
        const float life = 12f;
        var sink = Mathf.Clamp01((t - (life - 2f)) / 2f);
        figure.position = Vector3.down * sink * 2.5f;
        figure.rotation = Quaternion.identity;
        if (t == 0f)
        {
            var middle = Vector3.up * 1.1f;
            Place("Blast", middle + Vector3.up * 0.9f, 8f, 1.1f, Color.white);
            Burst("Fireball", middle, Quaternion.identity, 6);
            Burst("SmokeBlack", middle, Quaternion.identity, 8);
            Burst("Ember", middle, Ground, 18);
            Burst("Chunks", Vector3.up * 0.2f, Ground, 6);
            Place("Glow", middle, 7f, 0.25f, Flash);
            wreckFlame = wreckSmoke = 0f;
        }
        var x = (t - (life - 3f)) / 3f;
        var fire = t >= life ? 0f : x <= 0f ? 1f : 1f - Mathf.Clamp01(x);
        var body = figure.position + Vector3.up * 1f;
        if (fire > 0.02f && t >= wreckFlame) { wreckFlame = t + 0.16f / Mathf.Max(0.25f, fire); Burst("WreckFlame", body, Quaternion.identity, 1); }
        if (t < life - 1.5f && t >= wreckSmoke) { wreckSmoke = t + 0.14f; Burst("SmokeBlack", body + Vector3.up * 0.4f, Quaternion.identity, 1); }
    }

    static void Eject(float t, float dt)
    {
        Look(new Vector3(-3.5f, 2f, 6f), new Vector3(0f, 1.3f, 0f));
        if (t > 0f) return;
        for (var i = 0; i < 12; i++)
        {
            var side = i % 2 == 0 ? 1f : -1f;
            var plate = new Vector3(0.28f * side, 0.7f + 0.2f * (i / 2), -0.3f);
            var outward = -Vector3.forward * 0.8f + Vector3.up * 0.3f + new Vector3(side, 0f, 0f) * 0.5f;
            Burst("Steam", plate, Quaternion.LookRotation(outward), 2);
            Burst("Ember", plate, Quaternion.LookRotation(outward), 1);
        }
    }

    static void Heat(float t, float dt)
    {
        Look(new Vector3(0f, 2.6f, 3.2f), new Vector3(0f, 1.6f, 0f));
        // From 60 percent heat at t=0 to overheated.
        var level = Mathf.Clamp01(t / 2.5f);
        if (t >= heatNext)
        {
            heatNext = t + 0.34f - 0.22f * level;
            Burst("HeatSteam", new Vector3(-0.35f, 1.6f, 0f), Ground, 1);
            Burst("HeatSteam", new Vector3(0.35f, 1.6f, 0f), Ground, 1);
        }
    }
}
#endif

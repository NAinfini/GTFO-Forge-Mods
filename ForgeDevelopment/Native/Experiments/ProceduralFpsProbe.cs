using System;
using System.Globalization;
using ForgeVisual;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeDevelopment.Native;

/// <summary>
/// Trial of ForgeVisual's procedural shader on the gun in the hands. <c>drive_cli.py call static:ForgeDevelopment.Native.ProceduralFpsProbe Start</c>
/// (in a level, a gun wielded) hangs two small squares on the held item, on its layer, at the same place over the middle of the gun: a cyan one
/// drawn through the game's first-person item camera (ForgeVisual <c>Procedural.CreateMaterial(firstPerson: true)</c>) and a red one in the
/// world's projection. If the item camera is honoured, the cyan square sits on the gun and the red one stands off it. The answer also gives
/// what can be read back: whether the gun's own material draws through the item camera, whether the game set <c>_FPS_VP</c>, where the
/// square's centre lands on screen under each projection, and whether a painter lays a mesh out by UV the right way up (a mesh covering
/// only the top half of UV space must colour the top half of its target). <c>Stop</c> removes the squares.
/// </summary>
internal static class ProceduralFpsProbe
{
    private static GameObject? _held, _world;
    private static Material? _heldMaterial, _worldMaterial;
    private static ProceduralLook? _heldLook, _worldLook;
    private static Mesh? _quad;
    private static bool _ticking;

    internal static string Start()
    {
        Stop();
        var agent = PlayerManager.GetLocalPlayerAgent();
        var hands = agent == null ? null : agent.FPItemHolder;
        var item = hands == null ? null : hands.WieldedItem;
        if (agent == null || hands == null || item == null) return "no local player with an item in the hands";
        var renderers = item.GetComponentsInChildren<Renderer>(false);
        // The largest drawn mesh of the item (the body of the gun): the squares go just above its middle.
        Renderer? gun = null;
        var bounds = new Bounds();
        var drawn = 0;
        foreach (var r in renderers)
        {
            if (!r.enabled || r.forceRenderingOff || r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue;
            drawn++;
            var b = r.bounds;
            if (gun == null || b.size.sqrMagnitude > bounds.size.sqrMagnitude) { gun = r; bounds = b; }
        }
        if (gun == null) return "the held item has no drawn renderer";
        var gunFps = gun.sharedMaterial != null && gun.sharedMaterial.IsKeywordEnabled(PlayerBackpackManager.ENABLE_FPS_RENDERING);

        var catalog = Procedural.Catalog;
        _heldLook = new ProceduralLook(catalog.Find("polar")!);
        _heldLook.SetColour(0, new Color(0.2f, 0.95f, 1f, 1f));
        _worldLook = new ProceduralLook(catalog.Find("pulse")!);
        _worldLook.SetColour(0, new Color(0.6f, 0.05f, 0.05f, 1f));
        _worldLook.SetColour(1, new Color(1f, 0.2f, 0.2f, 1f));
        _heldMaterial = Procedural.CreateMaterial(FxBlend.Alpha, true, "probe procedural first person");
        _worldMaterial = Procedural.CreateMaterial(FxBlend.Alpha, false, "probe procedural world");
        if (_heldMaterial == null || _worldMaterial == null) return "ForgeVisual's shaders are not loaded";

        var size = Mathf.Clamp(bounds.size.magnitude * 0.15f, 0.02f, 0.06f);
        var above = bounds.center + item.transform.up * (bounds.extents.y + size * 0.6f);
        var mesh = _quad = Quad();
        _held = Square("ForgeDevelopment.ProceduralProbe.FirstPerson", mesh, _heldMaterial, item.transform, above, size, gun.gameObject.layer, hands.m_LookCamera);
        _world = Square("ForgeDevelopment.ProceduralProbe.World", mesh, _worldMaterial, item.transform, above, size, gun.gameObject.layer, hands.m_LookCamera);
        if (!_ticking) { _ticking = true; ForgeCore.Frame.OnUpdate("NAinfini.ForgeDevelopment.procedural-probe", Tick); }

        var camera = hands.m_LookCamera;
        var fps = Shader.GetGlobalMatrix("_FPS_VP");
        var worldVp = camera == null ? Matrix4x4.identity : GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix;
        return Line("gun", gun.name) + Line("drawnMeshes", drawn) + Line("gunSize", F(bounds.size.magnitude))
            + Line("gunNdcItemCamera", Ndc(fps, bounds.center)) + Line("gunNdcWorld", Ndc(worldVp, bounds.center)) + Line("gunLayer", LayerMask.LayerToName(gun.gameObject.layer)) + Line("gunMaterialFirstPerson", gunFps)
            + Line("squareFirstPersonKeyword", _heldMaterial.IsKeywordEnabled(PlayerBackpackManager.ENABLE_FPS_RENDERING))
            + Line("fpsMatrixSet", fps != Matrix4x4.zero && fps != Matrix4x4.identity)
            + Line("squareNdcWorld", Ndc(worldVp, above)) + Line("squareNdcItemCamera", Ndc(fps, above))
            + Line("unwrap", UnwrapReadback());
    }

    internal static string Stop()
    {
        if (_held != null) Object.Destroy(_held);
        if (_world != null) Object.Destroy(_world);
        if (_heldMaterial != null) Object.Destroy(_heldMaterial);
        if (_worldMaterial != null) Object.Destroy(_worldMaterial);
        if (_quad != null) Object.Destroy(_quad);
        _quad = null;
        _held = _world = null;
        _heldMaterial = _worldMaterial = null;
        return "removed";
    }

    private static void Tick()
    {
        var now = Time.time;
        if (_heldMaterial != null && _heldLook != null) _heldLook.Apply(_heldMaterial, now);
        if (_worldMaterial != null && _worldLook != null) _worldLook.Apply(_worldMaterial, now);
    }

    private static GameObject Square(string name, Mesh mesh, Material material, Transform parent, Vector3 at, float size, int layer, Camera? camera)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent, true);
        go.transform.position = at;
        if (camera != null) go.transform.rotation = Quaternion.LookRotation(-camera.transform.forward, camera.transform.up);
        go.transform.localScale = Vector3.one * size / Mathf.Max(parent.lossyScale.x, 1e-4f);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return go;
    }

    /// <summary>A unit square facing +Z with normals, white colours and UV over the whole square.</summary>
    private static Mesh Quad(float v0 = 0f, float v1 = 1f)
    {
        var mesh = new Mesh { name = "ForgeDevelopment procedural probe quad" };
        mesh.vertices = new Il2CppStructArray<Vector3>(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) });
        mesh.normals = new Il2CppStructArray<Vector3>(new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward });
        mesh.uv = new Il2CppStructArray<Vector2>(new[] { new Vector2(0f, v0), new Vector2(1f, v0), new Vector2(0f, v1), new Vector2(1f, v1) });
        var white = new Color32(255, 255, 255, 255);
        mesh.colors32 = new Il2CppStructArray<Color32>(new[] { white, white, white, white });
        mesh.triangles = new Il2CppStructArray<int>(new[] { 0, 2, 1, 2, 3, 1 });
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Paints a mesh covering only v 0.5 to 1 into a 32 x 32 target with a rim program at full base (every pixel it covers is
    /// red) and reads the target back: the top half must be red and the bottom half empty.</summary>
    private static string UnwrapReadback()
    {
        var target = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "probe unwrap" };
        var mesh = Quad(0.5f, 1f);
        var look = new ProceduralLook(Procedural.Catalog.Find("fresnel")!);
        look.SetColour(0, Color.red);
        look.Set(look.Program.Param("bias")!.Index, 1f);
        var painter = Procedural.Painter(mesh, 0, target, look, "probe");
        if (painter == null) { Object.Destroy(target); Object.Destroy(mesh); return "no painter"; }
        var read = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        var active = RenderTexture.active;
        try
        {
            painter.Paint(0f);
            RenderTexture.active = target;
            read.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
            read.Apply();
            var top = read.GetPixel(16, 24);
            var bottom = read.GetPixel(16, 8);
            var ok = top.r > 0.9f && top.a > 0.9f && bottom.a < 0.1f;
            return (ok ? "upright" : "WRONG") + " top=" + Rgba(top) + " bottom=" + Rgba(bottom);
        }
        finally
        {
            RenderTexture.active = active;
            painter.Dispose();
            Object.Destroy(read);
            Object.Destroy(target);
            Object.Destroy(mesh);
        }
    }

    private static string Ndc(Matrix4x4 viewProjection, Vector3 world)
    {
        var clip = viewProjection * new Vector4(world.x, world.y, world.z, 1f);
        return Math.Abs(clip.w) < 1e-6f ? "w=0" : F(clip.x / clip.w) + "," + F(clip.y / clip.w);
    }

    private static string Rgba(Color c) => F(c.r) + "/" + F(c.g) + "/" + F(c.b) + "/" + F(c.a);
    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Line(string key, object value) => key + "=" + (value is bool b ? (b ? "yes" : "no") : value) + "; ";
}

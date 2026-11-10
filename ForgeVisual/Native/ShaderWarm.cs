using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>
/// Draws each of the library's shaders once, off screen, during the elevator ride (or the first level frame), so the frame that first
/// shows a sprite, a ground mark or a beam does not create the shader's GPU programs. Loading a shader does not create them;
/// only a draw does, on the render thread, and the main thread then waits for it at the next present: on 10-28 the frame right after the
/// first beacon marker was drawn waited 119 ms there. A temporary camera renders the library's unit quad with every material into a
/// 16 x 16 texture and is destroyed; nothing is shown. The marks and the draw are steps of ForgeCore <c>LandingWork</c>, one per frame
/// after the elevator stops, so they do not share a frame with the other libraries' warm-ups.
/// </summary>
internal static class ShaderWarm
{
    private const int Layer = 31;
    private static bool _done;

    internal static void Tick()
    {
        if (_done) return;
        var state = GameStateManager.CurrentStateName;
        if (state != eGameStateName.StopElevatorRide && state != eGameStateName.InLevel) return;
        _done = true;
        // The shared surface marks are built here too, so the first hit does not make their quads in its own frame.
        ForgeCore.LandingWork.Add(Plugin.Id, "decal-marks", Decals.Prepare);
        ForgeCore.LandingWork.Add(Plugin.Id, "shaders", Draw);
    }

    private static void Draw()
    {
        GameObject? holder = null;
        RenderTexture? target = null;
        Material? additive = null, alpha = null;
        try
        {
            var marker = Shaders.Marker();
            additive = Shaders.Make(Texture2D.whiteTexture, FxBlend.Additive, "ForgeVisual warm additive");
            alpha = Shaders.Make(Texture2D.whiteTexture, FxBlend.Alpha, "ForgeVisual warm alpha");
            if (marker == null || additive == null || alpha == null) return;
            holder = new GameObject("ForgeVisual shader warm") { hideFlags = HideFlags.HideAndDontSave };
            var camera = holder.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 10f;
            camera.cullingMask = 1 << Layer;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.useOcclusionCulling = false;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            holder.transform.position = new Vector3(0f, -10000f, 0f);
            target = RenderTexture.GetTemporary(16, 16, 16);
            camera.targetTexture = target;
            var quad = Shaders.Quad();
            var at = Matrix4x4.TRS(holder.transform.position + new Vector3(0f, 0f, 2f), Quaternion.Euler(0f, 180f, 0f), Vector3.one);
            foreach (var material in new[] { marker, additive, alpha })
                Graphics.DrawMesh(quad, at, material, Layer, camera);
            camera.Render();
            camera.targetTexture = null;
        }
        catch (Exception error) { Plugin.Logger.Error("visual-shaders-could-not-be-drawn-ahead", "ForgeVisual shaders could not be drawn ahead of use (their first real draw pays for it): " + error); }
        finally
        {
            if (target != null) RenderTexture.ReleaseTemporary(target);
            if (holder != null) Object.Destroy(holder);
            if (additive != null) Object.Destroy(additive);
            if (alpha != null) Object.Destroy(alpha);
        }
    }
}

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ForgeFrameGen;

/// <summary>While the world camera renders into a smaller target, Unity's <c>WorldToScreenPoint</c> on it answers in render pixels, but every
/// caller in the game places HUD elements on the screen-sized HUD (NavMarkerLayer and CrosshairGuiLayer AfterCameraUpdate, GuiManager,
/// PlayerInteraction, PlayerFPSUtils, FPSCamera, FirstPersonItemHolder.GetMeleeScreenSpacePos: the game's only screen-point calls on that
/// camera). Both overloads (each calls Unity's internal function itself, neither calls the other) are scaled back to screen pixels for that
/// one camera only; every other camera keeps Unity's answer. The patch is this mod's own (ForgeCore <c>Hooks.Own</c>: the native entry is
/// checked before anything is written, and a second owner is refused). It sits on the methods only while the pass renders below the
/// screen's resolution: every call on a patched method makes the interop wrap its camera, so with DLAA, DLSS off or the pass stopped
/// the methods are left as the game has them (<see cref="Route"/> puts the patch on, <see cref="Clear"/> and a ratio of 1 take it off).
/// The same goes for the screen size inside <c>PreLitVolume.CollectCommands</c>: the game takes <c>Screen.width × Screen.height</c> over the
/// indirect light volume's cells as the pixel count per cell (<c>_InputCount</c>), and kernel AdvancedInput divides the summed depth and
/// colour of the camera's (render-sized) depth and input by it, so below the screen's resolution every cell's average came out
/// (render/screen)² too small and distant surfaces lost their indirect light. Inside that call alone <c>Screen.width</c> and
/// <c>Screen.height</c> answer the render size, which also sizes the call's two downsample dispatches to the textures they read.</summary>
internal static class ScreenSpace
{
    private static IntPtr _camera;
    private static float _x = 1f, _y = 1f;
    private static int _renderWidth, _renderHeight;
    private static bool _patched, _failed, _volume;
    private static MethodBase? _single, _eye, _collect, _width, _height;

    private static void Patch()
    {
        if (_patched || _failed) return;
        try
        {
            var postfix = new[] { new ForgeCore.HookPart(Postfix: new HarmonyMethod(typeof(ScreenSpace), nameof(Scale))) };
            _single = AccessTools.Method(typeof(Camera), nameof(Camera.WorldToScreenPoint), new[] { typeof(Vector3) });
            _eye = AccessTools.Method(typeof(Camera), nameof(Camera.WorldToScreenPoint), new[] { typeof(Vector3), typeof(Camera.MonoOrStereoscopicEye) });
            _collect = AccessTools.Method(typeof(PreLitVolume), nameof(PreLitVolume.CollectCommands));
            _width = AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.width));
            _height = AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.height));
            ForgeCore.Hooks.Own(Plugin.Id, _single, postfix);
            ForgeCore.Hooks.Own(Plugin.Id, _eye, postfix);
            ForgeCore.Hooks.Own(Plugin.Id, _collect, new[] { new ForgeCore.HookPart(new HarmonyMethod(typeof(ScreenSpace), nameof(EnterVolume)), new HarmonyMethod(typeof(ScreenSpace), nameof(LeaveVolume))) });
            ForgeCore.Hooks.Own(Plugin.Id, _width, new[] { new ForgeCore.HookPart(Postfix: new HarmonyMethod(typeof(ScreenSpace), nameof(Width))) });
            ForgeCore.Hooks.Own(Plugin.Id, _height, new[] { new ForgeCore.HookPart(Postfix: new HarmonyMethod(typeof(ScreenSpace), nameof(Height))) });
            _patched = true;
        }
        catch (Exception error)
        {
            _failed = true;
            Unpatch();
            Plugin.Logger.ErrorOnce("framegen-screen-points-unpatched", "Camera.WorldToScreenPoint, PreLitVolume.CollectCommands or Screen.width/height could not be patched; while DLSS renders below the screen's resolution HUD markers that follow world positions sit at the wrong place and distant surfaces lose their indirect light: " + error);
        }
    }

    private static void Unpatch()
    {
        try
        {
            foreach (var method in new[] { _single, _eye, _collect, _width, _height })
                if (method != null) ForgeCore.Hooks.Disown(Plugin.Id, method);
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("framegen-screen-points-not-released", "Camera.WorldToScreenPoint, PreLitVolume.CollectCommands or Screen.width/height could not be taken off again: " + error);
        }
        _patched = _volume = false;
    }

    internal static void Route(Camera camera, int renderWidth, int renderHeight, int outputWidth, int outputHeight)
    {
        _camera = camera.Pointer;
        (_renderWidth, _renderHeight) = (renderWidth, renderHeight);
        _x = (float)outputWidth / renderWidth;
        _y = (float)outputHeight / renderHeight;
        if (_x != 1f || _y != 1f) Patch();
        else if (_patched) Unpatch();
    }

    internal static void Clear()
    {
        _camera = IntPtr.Zero;
        _x = _y = 1f;
        if (_patched) Unpatch();
    }

    private static void EnterVolume() => _volume = true;

    private static void LeaveVolume() => _volume = false;

    private static void Width(ref int __result)
    {
        if (_volume) __result = _renderWidth;
    }

    private static void Height(ref int __result)
    {
        if (_volume) __result = _renderHeight;
    }

    private static void Scale(Camera __instance, ref Vector3 __result)
    {
        if (__instance.Pointer != _camera) return;
        __result.x *= _x;
        __result.y *= _y;
    }
}

using ForgeCore;
using System;
using Enemies;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace ForgeReplay;

/// <summary>What is heard in a replay is heard from the camera, as the game hears it from the local player. Wwise's listener sits on the
/// game's <c>FPSCamera</c>, which the replay moves, so positioned sounds already follow the view; two things the game ties to the local
/// player's body (the observer, which stands still) are moved to the view here:
/// - The room tone: <c>DramaManager.OnPlayerEnterArea</c> (0x149C2E0) posts the area's <c>AMBIENCE_LOOP_*</c>, and the game calls it only
///   from <c>PlayerAgent.set_CourseNode</c> of the local player. During a replay that call is held, and the area at the view is entered
///   instead when it changes.
/// - How far an enemy's sounds reach: <c>EnemyAgent.UpdateSfxLOD</c> (0x1564DA0) sets <c>SfxLOD</c> from the distance to the local
///   player, min(99, distance^0.5); during a replay it is set from the distance to the view the same way.
/// Main thread only.</summary>
internal static class Listening
{
    private static bool _entering;
    private static LG_Area? _area;
    private static float _next;

    internal static void Install()
    {
        var harmony = new Harmony(Plugin.Id + ".listening");
        foreach (var feature in new[] { typeof(RoomTone), typeof(EnemyReach) })
            try { harmony.CreateClassProcessor(feature).Patch(); }
            catch (Exception error) { Plugin.Logger.Error("The replay's " + feature.Name + " hook could not be put on, so that part is heard from the observer: " + error.Message); }
    }

    internal static void Reset() => _area = null;

    /// <summary>The view's position: the game's camera transform, which the replay moves (<c>FPSCamera.Position</c> stays at the observer).</summary>
    internal static bool View(out Vector3 at)
    {
        var camera = Players.Local?.FPSCamera;
        at = camera == null || camera.WasCollected ? default : camera.transform.position;
        return camera != null && !camera.WasCollected;
    }

    /// <summary>Enters the area at the view when it is another one, a few times a second.</summary>
    internal static void Frame()
    {
        if (ForgeCore.Frame.UnscaledTime < _next) return;
        _next = ForgeCore.Frame.UnscaledTime + 0.25f;
        if (!View(out var at) || !ForgeCore.Level.CourseNodeAt(at, 1f, out var node, out _) || node?.m_area is not { } area || area == null) return;
        if (_area != null && !_area.WasCollected && _area.Pointer == area.Pointer) return;
        _area = area;
        _entering = true;
        try { DramaManager.OnPlayerEnterArea(area); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The room tone at the view could not be started: " + error.Message); }
        finally { _entering = false; }
    }

    [HarmonyPatch(typeof(DramaManager), nameof(DramaManager.OnPlayerEnterArea))]
    private static class RoomTone
    {
        private static bool Prefix() => !Replayer.Active || _entering;
    }

    [HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.UpdateSfxLOD))]
    private static class EnemyReach
    {
        private static void Postfix(EnemyAgent __instance)
        {
            if (!Replayer.Active || !View(out var at)) return;
            try { __instance.SfxLOD = (uint)Mathf.Min(99f, Mathf.Pow(Vec.SqrLength(__instance.Position - at), 0.25f)); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("An enemy's sound reach could not be set from the view: " + error.Message); }
        }
    }
}

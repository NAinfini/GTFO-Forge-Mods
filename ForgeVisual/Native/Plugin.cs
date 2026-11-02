using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeCore;
using Player;
using UnityEngine;

namespace ForgeVisual;

[BepInPlugin(Id, "Forge Visual", Version)]
[BepInDependency("NAinfini.ForgeCore", ">=0.1.0")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeVisual", Version = "0.1.0";

    internal static ModLog Logger { get; private set; } = null!;
    internal static ConfigEntry<bool> FlashLights { get; private set; } = null!;
    internal static ConfigEntry<float> ShakeStrength { get; private set; } = null!;
    internal static ConfigEntry<bool> Outlines { get; private set; } = null!;

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Id);
        FlashLights = Settings.Bind(Id, "Effects", "FlashLights", true,
            "Short point lights at blasts, hits and muzzles drawn through ForgeVisual, for every mod together. Lights held on burning or glowing things stay.");
        ShakeStrength = Settings.Bind(Id, "Camera", "ShakeStrength", 1f,
            "How strongly blasts, landings and hits drawn through ForgeVisual shake your view, from 0 (off) to 1 (full), for every mod together.");
        Outlines = Settings.Bind(Id, "Outlines", "Enabled", true,
            "Draws the outlines mods put round enemies and objects (Forge Frames' cockpit and others), including those seen through walls. Turn off to hide them in every mod.");
        Scene.Subscribe();
        EffectBundle.Register();
        ForgeCore.Frame.OnUpdate(Id, VisualFrame.Update);
        ForgeCore.Frame.OnLateUpdate(Id, VisualFrame.LateUpdate);
    }

    public override bool Unload() => false;
}

/// <summary>Once a frame, through ForgeCore's frame callbacks, it steps everything this library draws.
/// An ordinary frame allocates nothing.</summary>
internal static class VisualFrame
{
    internal static void Update()
    {
        try { Scene.Tick(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("ForgeVisual frame update failed: " + error); }
    }

    /// <summary>The outlines, after animation and movement, before the cameras render.</summary>
    internal static void LateUpdate()
    {
        try { OutlineDraw.Tick(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("ForgeVisual outline update failed: " + error); }
    }
}

/// <summary>What every part of the library reads each frame (time, the local eye), and the one place that resets everything this
/// library holds when the level ends or a checkpoint is reloaded (both by the game's events). The pools themselves outlive levels; what they show does not.</summary>
internal static class Scene
{
    private static int _eyeFrame = -1;
    private static Vector3 _eye;
    private static Transform? _eyeTransform;
    private static bool _hasEye;
    private static float _tanHalfFov = 0.7f;

    internal static float Now { get; private set; }
    internal static float Delta { get; private set; }

    /// <summary>The local eye: read from the game the first time something asks in a frame, so a frame with nothing drawn reads nothing.</summary>
    internal static Vector3 Eye { get { ReadEye(); return _eye; } }
    internal static Transform? EyeTransform { get { ReadEye(); return _eyeTransform; } }
    internal static bool HasEye { get { ReadEye(); return _hasEye; } }

    /// <summary>Tangent of half the camera's vertical field of view, read at most once a frame.</summary>
    internal static float TanHalfFov { get { ReadEye(); return _tanHalfFov; } }

    /// <summary>Clears what is shown when the level ends or a checkpoint reloads (the game's own events, not a per-frame check).</summary>
    internal static void Subscribe()
    {
        GameEvents.LevelEnded += () => Clear();
        GameEvents.CheckpointReloaded += () => Clear();
    }

    internal static int WorldMask => LayerManager.MASK_WORLD;

    internal static void Tick()
    {
        Now = Time.time;
        Delta = Time.deltaTime;
        ShaderWarm.Tick();
        SpritePool.Tick(Now);
        PointLights.Tick(Now);
        Fx.Tick(Now);
        GroundMarker.TickAll(Now);
        Beams.TickAll(Now);
        GlowSpot.TickAll(Now);
    }

    private static void ReadEye()
    {
        var frame = Time.frameCount;
        if (_eyeFrame == frame) return;
        _eyeFrame = frame;
        Transform? eye = null;
        Camera? camera = null;
        var agent = PlayerManager.GetLocalPlayerAgent();
        if (agent != null && !agent.WasCollected && agent.FPSCamera != null)
        {
            eye = agent.FPSCamera.transform;
            camera = agent.FPSCamera.m_camera;
        }
        if (eye == null)
        {
            camera = Camera.main;
            if (camera != null) eye = camera.transform;
        }
        _eyeTransform = eye;
        _hasEye = eye != null;
        if (eye != null) _eye = eye.position;
        if (camera != null) _tanHalfFov = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
    }

    internal static void Clear()
    {
        SpritePool.Clear();
        PointLights.Clear();
        CameraShake.Clear();
        FxInstancePool.ClearAll();
        ParticlePool.ClearAll();
        Fx.Clear();
        GroundMarker.ClearAll();
        Beams.ClearAll();
        Trails.ClearAll();
        GlowSpot.ClearAll();
        OutlineDraw.Clear();
    }
}

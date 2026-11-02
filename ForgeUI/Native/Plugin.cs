using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace ForgeUI;

[BepInPlugin(Id, "Forge UI", Version)]
[BepInDependency("NAinfini.ForgeCore", ">=0.1.0")]
internal sealed partial class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeUI";
    internal const string Version = "0.1.0";

    /// <summary>This library's ForgeCore log.</summary>
    internal static readonly ForgeCore.ModLog Logger = ForgeCore.Log.For(Id);
    internal static ConfigEntry<float> LobbyOffsetX = null!, LobbyOffsetY = null!, HudOffsetXEntry = null!, HudOffsetYEntry = null!, HudSafeMarginEntry = null!;
    internal static ConfigEntry<bool> VisorEntry = null!;
    /// <summary>The player's <c>Screen / Visor</c> setting.</summary>
    internal static bool VisorEnabled { get; private set; } = true;
    /// <summary>The <c>Screen</c> strength caps every overlay is drawn under (0..1, or seconds).</summary>
    internal static float VisorFrame { get; private set; } = 0.13f;
    internal static float CrackMax { get; private set; } = 0.3f;
    internal static float HitFlashPeak { get; private set; } = 0.35f;
    internal static float HitFlashSeconds { get; private set; } = 0.3f;
    /// <summary>The player's <c>Screen / Glow</c>: multiplies the soft glow every HUD piece is drawn with (0..2).</summary>
    internal static float Glow { get; private set; } = 1f;
    /// <summary>The player's <c>Screen / Fringe</c>: whether HUD pieces get their 1 px colour fringe.</summary>
    internal static bool Fringe { get; private set; } = true;
    internal static float OffsetX { get; private set; }
    internal static float OffsetY { get; private set; }
    internal static float HudOffsetX { get; private set; }
    internal static float HudOffsetY { get; private set; }
    /// <summary>How far screen-anchored HUD elements stay from the screen edge, in HUD units.</summary>
    internal static float HudSafeMargin { get; private set; } = 48f;

    private static float ValidateOffset(ConfigEntry<float> entry)
    {
        if (float.IsFinite(entry.Value)) return entry.Value;
        Error(entry.Definition.Section + " / " + entry.Definition.Key + " must be finite; using 0 instead.");
        return 0f;
    }

    /// <summary>Reads a strength setting clamped to 0..<paramref name="max"/> now and whenever it changes.</summary>
    private static void Strength(ConfigEntry<float> entry, Action<float> set, float max)
    {
        void Read() => set(Math.Clamp(float.IsFinite(entry.Value) ? entry.Value : (float)entry.DefaultValue, 0f, max));
        Read();
        entry.SettingChanged += (_, _) => Read();
    }

    /// <summary>An error is written once per exact text.</summary>
    internal static void Error(string message) => Logger.ErrorOnce(message);

    public override void Load()
    {
        ForgeCore.Settings.Adopt(Id, Config);
        LobbyOffsetX = ForgeCore.Settings.Bind(Id, "Lobby", "ButtonOffsetX", 0f,
            "Moves every lobby button added by mods right (positive) or left, in menu units (about pixels at 1080p). Use it when another mod draws over them.");
        LobbyOffsetY = ForgeCore.Settings.Bind(Id, "Lobby", "ButtonOffsetY", 0f,
            "Moves every lobby button added by mods up (positive) or down, in menu units (about pixels at 1080p).");
        HudOffsetXEntry = ForgeCore.Settings.Bind(Id, "Hud", "OffsetX", 0f,
            "Moves every in-level HUD element added by mods right (positive) or left, in HUD units (about pixels at 1080p).");
        HudOffsetYEntry = ForgeCore.Settings.Bind(Id, "Hud", "OffsetY", 0f,
            "Moves every in-level HUD element added by mods up (positive) or down, in HUD units (about pixels at 1080p).");
        HudSafeMarginEntry = ForgeCore.Settings.Bind(Id, "Hud", "SafeMargin", 48f,
            "How far HUD elements that sit on a screen corner or edge stay from the screen edge, in HUD units (about pixels at 1080p). Raise it on a TV that crops the picture.");
        HudSafeMargin = Math.Max(0f, ValidateOffset(HudSafeMarginEntry));
        HudSafeMarginEntry.SettingChanged += (_, _) => HudSafeMargin = Math.Max(0f, ValidateOffset(HudSafeMarginEntry));
        OffsetX = ValidateOffset(LobbyOffsetX);
        OffsetY = ValidateOffset(LobbyOffsetY);
        HudOffsetX = ValidateOffset(HudOffsetXEntry);
        HudOffsetY = ValidateOffset(HudOffsetYEntry);
        LobbyOffsetX.SettingChanged += (_, _) => OffsetX = ValidateOffset(LobbyOffsetX);
        LobbyOffsetY.SettingChanged += (_, _) => OffsetY = ValidateOffset(LobbyOffsetY);
        HudOffsetXEntry.SettingChanged += (_, _) => HudOffsetX = ValidateOffset(HudOffsetXEntry);
        HudOffsetYEntry.SettingChanged += (_, _) => HudOffsetY = ValidateOffset(HudOffsetYEntry);
        ForgeUI.Language.Bind();
        ForgeCore.GameEvents.LevelEnded += GameHud.LevelEnded;
        ForgeCore.GameEvents.LevelEnded += LocalHits.LevelEnded;
        KeyBinds.Load(Paths.ConfigPath);
        VisorEntry = ForgeCore.Settings.Bind(Id, "Screen", "Visor", true,
            "Draws the helmet visor overlay some mods show (its frame, shade, cracks and heat glow). Turn it off for a clear screen.");
        VisorEnabled = VisorEntry.Value;
        VisorEntry.SettingChanged += (_, _) => VisorEnabled = VisorEntry.Value;
        Strength(ForgeCore.Settings.Bind(Id, "Screen", "VisorFrame", 0.13f, "Opacity of the visor's frame lines, 0 to 1."), v => VisorFrame = v, 1f);
        Strength(ForgeCore.Settings.Bind(Id, "Screen", "CracksMax", 0.3f, "Highest opacity of visor cracks, 0 to 1 (they keep clear of the middle of the screen)."), v => CrackMax = v, 1f);
        Strength(ForgeCore.Settings.Bind(Id, "Screen", "HitFlashPeak", 0.35f, "Peak opacity of the hit-direction flash on the screen edge, 0 to 1."), v => HitFlashPeak = v, 1f);
        Strength(ForgeCore.Settings.Bind(Id, "Screen", "Glow", 1f, "Multiplies the soft glow round HUD pieces mods draw, 0 to 2; 0 turns it off."), v => Glow = v, 2f);
        var fringe = ForgeCore.Settings.Bind(Id, "Screen", "Fringe", true, "Draws a faint 1 px red and blue colour fringe on HUD pieces mods draw, like a display seen through a visor. Turn it off for clean edges.");
        Fringe = fringe.Value;
        fringe.SettingChanged += (_, _) => Fringe = fringe.Value;
        Strength(ForgeCore.Settings.Bind(Id, "Screen", "HitFlashSeconds", 0.3f, "How long a full hit-direction flash lasts, 0.1 to 2 seconds."), v => HitFlashSeconds = Math.Max(0.1f, v), 2f);
        // No game method is patched here: each feature puts its own hooks on the first time it is used (PatchGroups).
        try
        {
            ForgeCore.Frame.OnUpdate(Id, UIKitFrame.Update);
            ForgeCore.Frame.OnLateUpdate(Id, UIKitFrame.LateUpdate);
            // The confirmation keys are listed under Controls from the start, so they can be rebound before the first question.
            ConfirmDialogs.RegisterKeys();
        }
        catch (Exception error)
        {
            Error("Forge UI initialization failed: " + error);
            throw;
        }
    }
}

/// <summary>Runs the per-frame work: keeps the lobby and menu bar buttons and the HUD elements in step, destroys closed picker
/// pieces and holds an open picker still on screen.</summary>
internal static class UIKitFrame
{


    internal static void Update()
    {
        ForgeUI.Language.Update();
        KeyBinds.Update(); SettingsPages.Update();
        Input.PruneDestroyed();
        LobbyButtons.Update();
        MenuBar.Update();
        ConfirmDialogs.Update();
        HudFocus.Update();
        Hud.Update();
        LobbyStrips.Update();
        if (PatchGroups.Has(PatchGroup.Objectives)) UIKit.ObjectivesReading.Tick();
        Picker.Tick();
    }

    /// <summary>After the page has panned its content for the cursor.</summary>
    internal static void LateUpdate()
    {
        Picker.Pin();
        GameHud.LateUpdate();
        CrosshairAttachments.LateUpdate();
        // Supplied gear icon jobs wait for their mod's model; with nothing waiting this is one comparison.
        try { GearIcons.Update(); }
        catch (Exception error) { Plugin.Error("Gear icon update failed: " + error); }
        // After every mod's Update, so an effect pushed or a visor shown this frame shows this frame.
        try { ScreenStack.Tick(); }
        catch (Exception error) { Plugin.Error("Screen effect stack update failed: " + error); }
    }
}

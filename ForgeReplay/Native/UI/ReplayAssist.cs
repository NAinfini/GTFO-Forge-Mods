using System;
using ForgeVisual;
using UnityEngine;

namespace ForgeReplay;

/// <summary>What helps the observer see (PLAN 5.4), all of it switched on and off from the replay screen and put back whole when the
/// replay ends: clearing the fog (the game's own no-fog block shown, ForgeCore <see cref="ForgeCore.WorldStates.ClearFog"/>), global brightness (a ForgeVisual <see cref="Ambient"/> lease on the local player's ambient light,
/// the one a level's global light sets, raised and carried with the camera), and an outline round every teammate that shows through walls (ForgeVisual <see cref="Outlines"/>, X-ray;
/// enemies are outlined only where the camera sees them, by the screen's automatic enemy outline). Main thread only.</summary>
internal static class ReplayAssist
{
    // A map's global light turned on: white and reaching 64 m; 0.2 strong, a map's usual 0.6 turning the level's fog white.
    private const float BrightRange = 64f;
    private static float _brightLevel = 0.2f;
    private static bool _fog, _bright, _allies = true;
    private static AmbientLease? _lease;
    // When the clearing last asked the game for the no-fog block (it is asked again while fog still shows, at most once a second).
    private static float _fogAsked = float.MinValue;
    private static readonly OutlineHandle?[] Ally = new OutlineHandle?[4];
    private static int _wieldSeen = -1, _viewedSeen = -1;

    internal static bool Fog { get => _fog; set { if (_fog == value) return; _fog = value; if (!value) RestoreFog(); } }
    internal static bool Bright { get => _bright; set { if (_bright == value) return; _bright = value; if (!value) RestoreBright(); } }
    /// <summary>Global brightness switched on at <paramref name="level"/> (ambient strength; the screen's switch uses 0.2); a lease already held is let go and taken again at it.</summary>
    internal static void BrightAt(float level) { _brightLevel = level; if (_bright) RestoreBright(); _bright = true; }

    /// <summary>What the observer's assists show now: the two switches, the brightness level, and the fog the volume draws (density, height boost) with the fog block the level shows.</summary>
    internal static string State()
    {
        var volume = PreLitVolume.Current;
        var fog = volume == null ? "fog volume none" : "fog density " + volume.m_fogDensity.ToString("0.#####") + " boost " + volume.m_densityHeightMaxBoost.ToString("0.#####");
        uint block; try { block = ForgeCore.WorldStates.CurrentFog(); } catch (Exception) { block = 0; }
        return "assist fog " + (_fog ? "on" : "off") + " brightness " + (_bright ? "on " + _brightLevel.ToString("0.##") + (_lease is { Active: true } ? " lease" : " no-lease") : "off") + " | " + fog + " level-fog-block " + block;
    }

    internal static bool Allies { get => _allies; set { _allies = value; if (!value) RemoveAllies(); } }

    /// <summary>Per frame, after the game's own update: holds the fog and the exposure where the observer asked, keeps the teammates' outlines.</summary>
    internal static void Frame(int viewed, bool eyes)
    {
        if (_fog) HoldFog();
        if (_bright) HoldBright();
        var redo = Puppeteer.WieldVersion != _wieldSeen || (eyes ? viewed : -1) != _viewedSeen;
        if (!redo && UnityEngine.Time.frameCount % 60 != 0) return;
        _wieldSeen = Puppeteer.WieldVersion; _viewedSeen = eyes ? viewed : -1;
        for (var slot = 0; slot < 4; slot++)
        {
            var body = Puppeteer.BodyOf(slot);
            var want = _allies && body != null && !(eyes && slot == viewed);
            if (!want) { Ally[slot]?.Remove(); Ally[slot] = null; continue; }
            if (!redo && Ally[slot] is { Active: true }) continue;
            Ally[slot]?.Remove();
            var colour = ReplayScreen.SlotColour(slot);
            Ally[slot] = Outlines.Add(Plugin.Id, body!, new OutlineColor(colour.r, colour.g, colour.b, 0.95f), priority: 10, xRay: true);
        }
    }

    /// <summary>Everything back as the game had it; called when the replay ends.</summary>
    internal static void Reset()
    {
        if (_fog) RestoreFog();
        if (_bright) RestoreBright();
        _fog = _bright = false; _allies = true;
        RemoveAllies();
        _wieldSeen = _viewedSeen = -1;
    }

    private static void RemoveAllies()
    {
        for (var slot = 0; slot < 4; slot++) { Ally[slot]?.Remove(); Ally[slot] = null; }
        _wieldSeen = -1;
    }

    private static void HoldFog()
    {
        try
        {
            // Both the even fog and the height fog: a level's fog steps can be height fog only (some levels' FogSettings have density 0 and a
            // height boost of 0.04), which a check of the even density alone never cleared. Read only: the fog itself is set through the game's own fog block.
            var volume = PreLitVolume.Current;
            if (volume == null || volume.m_fogDensity <= 0.0001f && volume.m_densityHeightMaxBoost <= 0.0001f) return;
            if (ForgeCore.Frame.UnscaledTime - _fogAsked < 1f) return;
            _fogAsked = ForgeCore.Frame.UnscaledTime;
            if (!ForgeCore.WorldStates.ClearFog()) { Plugin.Logger.ErrorOnce("Clearing the fog failed: the level has no environment manager."); _fog = false; }
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Clearing the fog failed: " + error.Message); _fog = false; }
    }

    private static void RestoreFog()
    {
        _fogAsked = float.MinValue;
        try { ForgeCore.WorldStates.LevelFog(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The fog could not be put back: " + error.Message); }
    }

    private static void HoldBright()
    {
        try
        {
            if (_lease is not { Active: true }) _lease = Ambient.Lease(Plugin.Id, _brightLevel, BrightRange, Color.white);
            if (!_lease.Active) { Plugin.Logger.ErrorOnce("Global brightness failed: the local player has no ambient light."); _bright = false; return; }
            // The observer stands apart from what is watched: the light goes where the camera is.
            _lease.Follow(ReplayCamera.Shown);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Global brightness failed: " + error.Message); _bright = false; }
    }

    private static void RestoreBright()
    {
        try { _lease?.Release(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The ambient light could not be put back: " + error.Message); }
        _lease = null;
    }
}

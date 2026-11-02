using System.Collections.Generic;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>The shell's lights (<see cref="FrameGlow"/>): the light strips and the visor in the armour's emission map, tinted by one
/// emission colour on this suit's own copy of its armour material (ForgeVisual's <see cref="ForgeVisual.Glow.For(Renderer, GlowMode)"/>). A steady look writes its colour once; a moving look (an empty
/// suit breathing, an overheated one pulsing) or a transition (power-on, armour falling to red, the wreck going dark) writes it every
/// frame while it lasts: one SetColor per lit material, no allocation. No light or post effect is added.</summary>
internal sealed partial class FrameVisual
{
    private readonly List<GlowHandle> _glows = new();
    private bool _glowReady, _overheated, _glowMoving;
    private GlowLook _glowLook = (GlowLook)255, _glowFrom;
    private float _glowSince;

    /// <summary>The emission colour the suit's lights have now (the first-person shell takes the same).</summary>
    internal Color GlowColour { get; private set; } = Color.black;

    /// <summary>Every frame: the look from the record (and the local pilot's heat); its colour is written when the look changes and
    /// on every frame of a moving look.</summary>
    private void TickGlow(FrameRecord r, float now)
    {
        if (!_glowReady) PrepareGlow();
        var look = FrameGlow.Look(r, _pilotLocal && _overheated);
        if (look != _glowLook)
        {
            // The first look a suit shows (spawned, or joined mid-way) starts steady, with no transition.
            _glowFrom = _glowLook == (GlowLook)255 ? look : _glowLook;
            _glowLook = look;
            _glowSince = now;
            _glowMoving = true;
        }
        if (!_glowMoving) return;
        var age = now - _glowSince;
        _glowMoving = FrameGlow.Moving(look, _glowFrom, age);
        var (red, green, blue) = FrameGlow.Colour(r.Spec.Light, look, _glowFrom, _glowMoving ? age : float.MaxValue, now);
        GlowColour = new Color(red, green, blue, 1f);
        foreach (var glow in _glows) glow.Set(GlowColour);
        if (_pilotLocal) LocalPilot.Glow(_unit, GlowColour);
    }
    /// <summary>Once: this suit's own copy of each lit material (the prefab's are shared by every suit of the model).</summary>
    private void PrepareGlow()
    {
        _glowReady = true;
        foreach (var renderer in _renderers)
        {
            if (renderer == null || renderer.WasCollected) continue;
            var glow = ForgeVisual.Glow.For(renderer);
            if (glow != null) _glows.Add(glow);
        }
        if (_glows.Count == 0) Plugin.Error("Exosuit model " + _unit.Record.Spec.Model + " has no lit material; its light strips stay dark.");
    }

    private void DestroyGlow()
    {
        foreach (var glow in _glows) glow.Dispose();
        _glows.Clear();
    }
}
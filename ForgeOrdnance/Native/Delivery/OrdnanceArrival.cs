using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>Which parts of a touchdown <see cref="OrdnanceArrival.Impact"/> plays.</summary>
[Flags]
public enum OrdnanceImpactParts
{
    /// <summary>The impact thud.</summary>
    Thud = 1,
    /// <summary>The rubble settling after it.</summary>
    Debris = 2,
    /// <summary>The camera shake of the local player (ForgeVisual's <c>CameraShake</c>, scaled by the player's shake setting).</summary>
    Shake = 4,
    All = Thud | Debris | Shake,
}

/// <summary>How hard and how far a touchdown shakes the camera, and which of its sounds play. Make one per kind of arrival and
/// reuse it.</summary>
public sealed class OrdnanceImpact
{
    public OrdnanceImpactParts Parts { get; set; } = OrdnanceImpactParts.All;
    /// <summary>Shake amplitude at the landing point, falling to nothing at <see cref="Range"/>.</summary>
    public float Strength { get; set; }
    /// <summary>Metres from the landing point at which the shake has faded out.</summary>
    public float Range { get; set; } = 40f;
    /// <summary>Seconds the shake lasts.</summary>
    public float Seconds { get; set; } = 0.7f;
    /// <summary>The shake's frequency.</summary>
    public float Frequency { get; set; } = 18f;
    /// <summary>Asked each time the shake would play; false skips it (the caller's own reasons, for example no shake inside a vehicle).</summary>
    public Func<bool>? ShakeIf { get; set; }
    /// <summary>Metres within which the touchdown wakes sleeping enemies (the game's own noise, made by the host only); 0 makes none.
    /// Independent of <see cref="Parts"/>.</summary>
    public float NoiseRadius { get; set; }
}

/// <summary>How <see cref="OrdnanceArrival.Drop"/> falls and lands.</summary>
public sealed class OrdnanceDropStyle
{
    /// <summary>Metres above the landing point the fall starts.</summary>
    public float FallHeight { get; set; } = 40f;
    /// <summary>The touchdown (<see cref="OrdnanceImpact.Parts"/> default to all three).</summary>
    public OrdnanceImpact Impact { get; set; } = new() { Strength = 0.2f };
    /// <summary>Every frame of the fall with its progress, 0 to 1 (exhaust jets, a trail). Not called on the touchdown frame.</summary>
    public Action<float>? OnFall { get; set; }
    /// <summary>Once on touchdown, after the impact.</summary>
    public Action? OnLanded { get; set; }
}

/// <summary>One container-free drop in flight (<see cref="OrdnanceArrival.Drop"/>).</summary>
public sealed class OrdnanceDrop
{
    private readonly Transform _body;
    private readonly Vector3 _landing;
    private readonly float _seconds;
    private readonly OrdnanceDropStyle _style;
    private readonly bool _quiet;
    private readonly float _start;

    internal OrdnanceDrop(Transform body, Vector3 landing, float seconds, OrdnanceDropStyle style, float elapsed, bool replayed)
    {
        _body = body; _landing = landing; _seconds = seconds; _style = style; _start = Time.time - elapsed; _quiet = replayed && elapsed >= seconds;
    }

    /// <summary>True from the touchdown on.</summary>
    public bool Landed { get; private set; }
    /// <summary>True once it landed or was cancelled; the drop no longer moves the body.</summary>
    public bool Done { get; private set; }

    /// <summary>Stops the drop where it is without a touchdown.</summary>
    public void Cancel() => Done = true;

    internal void Start(bool replayed)
    {
        if (!replayed) OrdnanceArrivalSounds.Play(OrdnanceArrivalSound.Incoming, _body, ForgeAudio.AudioKit.DimensionAt(_landing));
        Tick();
    }

    internal void Tick()
    {
        if (Done) return;
        if (_body == null || _body.WasCollected) { Done = true; return; }
        var t = _seconds <= 0f ? 1f : Mathf.Clamp01((Time.time - _start) / _seconds);
        _body.position = _landing + Vector3.up * (_style.FallHeight * (1f - t * t));
        if (t < 1f)
        {
            try { _style.OnFall?.Invoke(t); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("A drop's fall callback failed: " + error); }
            return;
        }
        Landed = Done = true;
        if (_quiet) return;
        OrdnanceArrival.Impact(_body, _landing, _style.Impact);
        try { _style.OnLanded?.Invoke(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A drop's landing callback failed: " + error); }
    }
}

/// <summary>The shared arrival feel of every package: the touchdown sounds and camera shake of a landing, and a plain fall from the
/// sky for a body that has no container. Shakes go through ForgeVisual's <see cref="ForgeVisual.CameraShake"/> (the player's one
/// shake setting, distance fall-off and the cap on shakes running together); a blast that is not a touchdown calls it directly.</summary>
public static class OrdnanceArrival
{
    private static readonly List<OrdnanceDrop> Drops = new();

    /// <summary>The touchdown of something that landed at <paramref name="ground"/>: the sounds, shake and (on the host) noise of
    /// <paramref name="impact"/>. The sounds follow <paramref name="owner"/>.</summary>
    public static void Impact(Transform owner, Vector3 ground, OrdnanceImpact impact)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (impact == null) throw new ArgumentNullException(nameof(impact));
        if (impact.Parts.HasFlag(OrdnanceImpactParts.Thud) || impact.Parts.HasFlag(OrdnanceImpactParts.Debris))
        {
            var dimension = ForgeAudio.AudioKit.DimensionAt(ground);
            if (impact.Parts.HasFlag(OrdnanceImpactParts.Thud)) OrdnanceArrivalSounds.Play(OrdnanceArrivalSound.Impact, owner, dimension);
            if (impact.Parts.HasFlag(OrdnanceImpactParts.Debris)) OrdnanceArrivalSounds.Play(OrdnanceArrivalSound.Debris, owner, dimension);
        }
        if (impact.Parts.HasFlag(OrdnanceImpactParts.Shake) && (impact.ShakeIf?.Invoke() ?? true))
            ForgeVisual.CameraShake.Shake(ground, new ForgeVisual.ShakeSpec(impact.Strength, impact.Range, impact.Seconds, impact.Frequency));
        OrdnanceImpactNoise.Make(ground, impact.NoiseRadius);
    }

    /// <summary>Falls <paramref name="body"/> from <see cref="OrdnanceDropStyle.FallHeight"/> above <paramref name="landing"/> onto it
    /// over <paramref name="seconds"/> (slow at first, quick at the end), starting with the fall whistle and ending in
    /// <see cref="Impact"/> and <see cref="OrdnanceDropStyle.OnLanded"/>. The drop moves the body every frame until it lands or
    /// <see cref="OrdnanceDrop.Cancel"/>; a level exit cancels it. <paramref name="elapsed"/> seconds are already played, and
    /// <paramref name="replayed"/> (a late joiner) plays no whistle and, when the fall is already over, no touchdown. Every peer
    /// calls it with the same arguments so they match.</summary>
    public static OrdnanceDrop Drop(Transform body, Vector3 landing, float seconds, OrdnanceDropStyle style, float elapsed = 0f, bool replayed = false)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (style == null) throw new ArgumentNullException(nameof(style));
        var drop = new OrdnanceDrop(body, landing, seconds, style, elapsed, replayed);
        Drops.Add(drop);
        drop.Start(replayed);
        return drop;
    }

    internal static void Tick()
    {
        for (var i = Drops.Count - 1; i >= 0; i--)
        {
            var drop = Drops[i];
            try { drop.Tick(); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("A drop failed and was removed: " + error); drop.Cancel(); }
            if (drop.Done) Drops.RemoveAt(i);
        }
    }

    internal static void Clear()
    {
        foreach (var drop in Drops) drop.Cancel();
        Drops.Clear();
    }
}

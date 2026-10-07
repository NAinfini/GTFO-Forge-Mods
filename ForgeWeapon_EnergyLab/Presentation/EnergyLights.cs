using FX_EffectSystem;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

/// <summary>One light's colour, reach, peak intensity, lifetime and flicker. A flash fades out over
/// <see cref="Life"/>; a held light ignores it.</summary>
internal readonly struct LightSpec
{
    internal readonly Color Color;
    internal readonly float Range, Intensity, Life, Flicker;

    internal LightSpec(Color color, float range, float intensity, float life = 0f, float flicker = 0.1f)
    {
        Color = color;
        Range = range;
        Intensity = intensity;
        Life = life;
        Flicker = flicker;
    }
}

/// <summary>GTFO's clustered renderer requires native FX_PointLight instances instead of Unity particle lights.
/// Local and remote weapons share a fixed budget. Saturation replaces the flash nearest its end; exhaustion of
/// the native pool skips the light while particles continue. Slot generations reject stale handles.</summary>
internal static class EnergyLights
{
    internal const int Capacity = 16;
    private const float HoldFadeIn = 0.04f, HoldGrace = 0.06f, HoldFadeOut = 0.12f;

    private static readonly FX_PointLight?[] Lights = new FX_PointLight?[Capacity];
    private static readonly Vector3[] Positions = new Vector3[Capacity];
    private static readonly float[] Born = new float[Capacity];
    private static readonly float[] Seen = new float[Capacity];
    private static readonly float[] Life = new float[Capacity];
    private static readonly float[] Peak = new float[Capacity];
    private static readonly float[] Strength = new float[Capacity];
    private static readonly float[] Flicker = new float[Capacity];
    private static readonly bool[] Held = new bool[Capacity];
    private static readonly int[] Generation = new int[Capacity];

    internal static void Flash(Vector3 position, in LightSpec spec, float now, float strength = 1f, float delay = 0f)
    {
        var slot = Allocate(spec, now);
        if (slot < 0) return;
        Held[slot] = false;
        Positions[slot] = position;
        Born[slot] = now + delay;
        Life[slot] = Mathf.Max(0.02f, spec.Life);
        Strength[slot] = strength;
    }

    /// <summary>Keeps the light <paramref name="handle"/> (0 = none) at <paramref name="position"/> this frame, taking a
    /// slot the first time. A light not held again for a moment fades out and its handle goes stale.</summary>
    internal static void Hold(ref int handle, Vector3 position, in LightSpec spec, float now, float strength = 1f)
    {
        var slot = Resolve(handle);
        if (slot < 0 || !Held[slot])
        {
            slot = Allocate(spec, now);
            if (slot < 0) { handle = 0; return; }
            Held[slot] = true;
            Born[slot] = now;
            handle = ((Generation[slot] & 0x7fffff) << 8) | (slot + 1);
        }
        Positions[slot] = position;
        Seen[slot] = now;
        Strength[slot] = strength;
    }

    internal static void Release(ref int handle)
    {
        var slot = Resolve(handle);
        if (slot >= 0 && Held[slot]) Seen[slot] = float.MinValue;
        handle = 0;
    }

    internal static void Tick(float now)
    {
        for (var slot = 0; slot < Capacity; slot++)
        {
            var light = Lights[slot];
            if (light is null) continue;
            if (light == null || !light.m_isInUse) { Lights[slot] = null; Generation[slot]++; continue; }
            float envelope;
            if (Held[slot])
            {
                var silent = now - Seen[slot];
                envelope = Mathf.Clamp01((now - Born[slot]) / HoldFadeIn)
                    * (silent <= HoldGrace ? 1f : 1f - Mathf.Clamp01((silent - HoldGrace) / HoldFadeOut));
                if (silent > HoldGrace + HoldFadeOut) { Free(slot); continue; }
            }
            else
            {
                var age = now - Born[slot];
                if (age >= Life[slot]) { Free(slot); continue; }
                var left = age < 0f ? 0f : 1f - age / Life[slot];
                envelope = age < 0f ? 0f : left * left;
            }
            var flicker = 1f - Flicker[slot] * Mathf.PerlinNoise(now * 14f, slot * 3.7f);
            light.m_intensity = Peak[slot] * Strength[slot] * envelope * flicker;
            light.m_position = Positions[slot];
            light.UpdateData();
            light.UpdateTransform();
        }
    }

    internal static void Clear()
    {
        for (var slot = 0; slot < Capacity; slot++) if (Lights[slot] is not null) Free(slot);
    }

    private static int Resolve(int handle)
    {
        if (handle == 0) return -1;
        var slot = (handle & 0xff) - 1;
        if (slot < 0 || slot >= Capacity || Lights[slot] is null || (Generation[slot] & 0x7fffff) != handle >> 8) return -1;
        return slot;
    }

    private static int Allocate(in LightSpec spec, float now)
    {
        var slot = -1;
        for (var index = 0; index < Capacity; index++) if (Lights[index] is null) { slot = index; break; }
        if (slot < 0)
        {
            // Every slot is busy: take over the flash closest to its end; held lights are never stolen.
            var best = float.MaxValue;
            for (var index = 0; index < Capacity; index++)
            {
                if (Held[index]) continue;
                var left = Born[index] + Life[index] - now;
                if (left < best) { best = left; slot = index; }
            }
            if (slot < 0) return -1;
        }
        var light = Lights[slot];
        if (light is null || light == null || !light.m_isInUse)
        {
            Lights[slot] = null;
            if (!FX_Manager.TryAllocateFXLight(out var allocated, false) || allocated == null) return -1;
            Lights[slot] = light = allocated;
        }
        Generation[slot]++;
        light.SetColor(spec.Color);
        light.SetRange(spec.Range);
        light.m_intensity = 0f;
        Peak[slot] = spec.Intensity;
        Flicker[slot] = spec.Flicker;
        return slot;
    }

    private static void Free(int slot)
    {
        var light = Lights[slot];
        Lights[slot] = null;
        Generation[slot]++;
        if (light is null || light == null || !light.m_isInUse) return;
        light.m_intensity = 0f;
        light.UpdateData();
        FX_Manager.DeallocateFXLight(light);
    }
}

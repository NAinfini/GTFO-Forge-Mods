using System;

namespace ForgeOrdnance.Frames;

/// <summary>What the shell's lights say. The light strips along the shell's structural lines and the visor are one emission map on
/// the suit's armour material (strips at <see cref="FrameGlow.StripLevel"/>, the visor full), so one emission colour on that material
/// is every light of the suit. A steady look is written once when it begins; only the looks that move (an empty suit breathing, an
/// overheated suit pulsing) and the short transitions (power-on, armour falling to red, the wreck going dark) write a colour every
/// frame while they last. No light or post effect is added.</summary>
internal enum GlowLook : byte
{
    /// <summary>Wrecked: dark.</summary>
    Off,
    /// <summary>Falling in or standing empty: the model's colour, dim, breathing slowly.</summary>
    Standby,
    /// <summary>Standing empty with its armour at or below <see cref="FrameGlow.DamagedShare"/>: red, dim, breathing slowly.</summary>
    StandbyDamaged,
    /// <summary>Worn: the model's colour.</summary>
    Normal,
    /// <summary>Worn with its armour at or below <see cref="FrameGlow.DamagedShare"/>: red.</summary>
    Damaged,
    /// <summary>Worn and overheated (the local pilot's sprint heat; only that peer knows it): red-orange, pulsing.</summary>
    Overheat,
    /// <summary>Opening or closing for a pilot climbing in or out: a highlight of the model's colour (a quarter of the way to white).</summary>
    Opening,
}

internal static class FrameGlow
{
    /// <summary>The armour share at or below which the lights turn red: the same line as the HUD's red armour bar and the alarm.</summary>
    public const float DamagedShare = 0.25f;

    /// <summary>The brightest channel of the emission colour (sRGB, as Unity's SetColor takes it) on the game's gear shader, which the
    /// visor reaches; the strips are painted at <see cref="StripLevel"/> of it. A grey gain of 1.5 washed the lights out to white in
    /// game and 0.8 shows them; a saturated colour at this gain blooms and keeps its hue.</summary>
    public const float Strength = 1.35f;

    /// <summary>The strips' level in the emission map (the visor is 1): the head is the suit's brightest light.</summary>
    public const float StripLevel = 0.8f;

    /// <summary>An empty suit breathes between these shares of its colour, once every <see cref="BreathSeconds"/>.</summary>
    public const float StandbyLow = 0.12f, StandbyHigh = 0.22f, BreathSeconds = 1f / 0.3f;

    /// <summary>An overheated suit pulses between <see cref="PulseLow"/> and full, <see cref="PulseHz"/> times a second.</summary>
    public const float PulseLow = 0.3f, PulseHz = 2f;

    /// <summary>Power-on after boarding: dark, two quick flashes, then up to full (seconds into the worn look).</summary>
    public const float BootSeconds = 0.9f;

    /// <summary>The wreck's lights flicker this long before going dark.</summary>
    public const float WreckFlickerSeconds = 0.5f;

    /// <summary>Armour falling to red: one bright flash this long, then steady red.</summary>
    public const float DamageFlashSeconds = 0.15f;

    public static readonly (float R, float G, float B) DamagedColour = (1f, 0.06f, 0.08f);
    public static readonly (float R, float G, float B) OverheatColour = (1f, 0.3f, 0.02f);

    /// <summary>How far the opening look takes the model's colour toward white.</summary>
    public const float OpeningWhite = 0.25f;

    public static GlowLook Look(FrameRecord r, bool overheated)
    {
        var damaged = r.Armor <= r.Spec.Armor * DamagedShare;
        return r.State switch
        {
            FrameState.Destroyed => GlowLook.Off,
            FrameState.Entering or FrameState.Exiting => GlowLook.Opening,
            FrameState.Piloted => overheated ? GlowLook.Overheat : damaged ? GlowLook.Damaged : GlowLook.Normal,
            _ => damaged ? GlowLook.StandbyDamaged : GlowLook.Standby,
        };
    }

    /// <summary>Whether <paramref name="look"/>, <paramref name="age"/> seconds after it began (coming from <paramref name="from"/>), still
    /// changes colour from frame to frame; when false its colour stays what <see cref="Colour"/> gives now.</summary>
    public static bool Moving(GlowLook look, GlowLook from, float age) => look switch
    {
        GlowLook.Standby or GlowLook.StandbyDamaged or GlowLook.Overheat => true,
        GlowLook.Normal => from == GlowLook.Opening && age < BootSeconds,
        GlowLook.Damaged => Flashes(from) && age < DamageFlashSeconds,
        GlowLook.Off => from != GlowLook.Off && age < WreckFlickerSeconds,
        _ => false,
    };

    /// <summary>The emission colour (sRGB, the shader's scale) of <paramref name="look"/> on a suit of <paramref name="light"/>,
    /// <paramref name="age"/> seconds after the look began, coming from <paramref name="from"/>; <paramref name="now"/> is the game clock
    /// (the breathing follows it, so it does not restart on every change).</summary>
    public static (float R, float G, float B) Colour(LightSpec light, GlowLook look, GlowLook from, float age, float now)
    {
        var model = (light.GlowR, light.GlowG, light.GlowB);
        switch (look)
        {
            case GlowLook.Off:
                if (from == GlowLook.Off || age >= WreckFlickerSeconds) return (0f, 0f, 0f);
                // A quick, uneven flicker of what it showed, fading.
                var on = (int)(age * 24f) % 3 != 1;
                return Scale(Hue(light, from), on ? 1f - age / WreckFlickerSeconds * 0.5f : 0.05f);
            case GlowLook.Standby:
                return Scale(model, Breath(now));
            case GlowLook.StandbyDamaged:
                return Scale(DamagedColour, Breath(now));
            case GlowLook.Overheat:
                return Scale(OverheatColour, PulseLow + (1f - PulseLow) * (0.5f + 0.5f * MathF.Cos(2f * MathF.PI * PulseHz * age)));
            case GlowLook.Opening:
                return Scale(Toward(model, (1f, 1f, 1f), OpeningWhite), 1f);
            case GlowLook.Damaged:
                return Scale(Flashes(from) && age < DamageFlashSeconds ? Toward(DamagedColour, (1f, 1f, 1f), 0.45f) : DamagedColour, 1f);
            default:
                return Scale(model, from == GlowLook.Opening ? Boot(age) : 1f);
        }
    }

    /// <summary>Power-on: dark, a flash at 0.25 s and at 0.45 s, then up from 0.6 s to full at <see cref="BootSeconds"/>.</summary>
    public static float Boot(float age)
    {
        if (age >= BootSeconds) return 1f;
        if (age >= 0.25f && age < 0.33f || age >= 0.45f && age < 0.53f) return 1f;
        if (age < 0.6f) return 0.04f;
        return 0.04f + 0.96f * (age - 0.6f) / (BootSeconds - 0.6f);
    }

    /// <summary>An empty suit's share of its colour at game time <paramref name="now"/>.</summary>
    public static float Breath(float now)
        => StandbyLow + (StandbyHigh - StandbyLow) * (0.5f + 0.5f * MathF.Sin(2f * MathF.PI * now / BreathSeconds));

    // Armour falls to red from a healthy worn suit (an overheated one cooling down into red does not flash).
    private static bool Flashes(GlowLook from) => from == GlowLook.Normal;

    private static (float R, float G, float B) Hue(LightSpec light, GlowLook look) => look switch
    {
        GlowLook.Damaged or GlowLook.StandbyDamaged => DamagedColour,
        GlowLook.Overheat => OverheatColour,
        _ => (light.GlowR, light.GlowG, light.GlowB),
    };

    private static (float R, float G, float B) Toward((float R, float G, float B) a, (float R, float G, float B) b, float t)
        => (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    /// <summary>The colour at <paramref name="k"/> of <see cref="Strength"/>, its brightest channel setting the scale.</summary>
    private static (float R, float G, float B) Scale((float R, float G, float B) c, float k)
    {
        var peak = Math.Max(c.R, Math.Max(c.G, c.B));
        var s = peak > 0f ? Strength * k / peak : 0f;
        return (c.R * s, c.G * s, c.B * s);
    }
}

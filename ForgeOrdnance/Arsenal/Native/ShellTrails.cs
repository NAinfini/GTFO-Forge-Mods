using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance.Arsenal;

/// <summary>What a launcher round leaves along its flight: a ribbon thinning to nothing behind it, in the world where it has been, and puffs of smoke in the air it
/// passed (a rocket's: hotter, thicker, smokier). ForgeVisual's <see cref="Trails"/> draws it; this only holds the looks.</summary>
internal static class ShellTrails
{
    private static TrailStyle? _grenade, _rocket;

    /// <summary>A trail for a round starting at <paramref name="at"/>; null when ForgeVisual has none to give (its budget is used up or its shaders are missing).</summary>
    internal static Trail? Rent(Vector3 at, bool rocket)
    {
        var smoke = ArsenalArt.Sheets?.Smoke;
        var style = rocket
            ? _rocket ??= new TrailStyle
            {
                RibbonPoints = 30,
                RibbonStep = 0.02f,
                RibbonWidth = 0.22f,
                RibbonTaper = 0.08f,
                RibbonHead = new Color(1f, 0.85f, 0.55f, 0.95f),
                RibbonTail = new Color(1f, 0.45f, 0.15f, 0f),
                RibbonBlend = FxBlend.Additive,
                Puffs = new[] { Smoke(smoke, 26f, 0.5f, 3f, 1.8f, 0.45f) },
            }
            : _grenade ??= new TrailStyle
            {
                RibbonPoints = 26,
                RibbonStep = 0.02f,
                RibbonWidth = 0.1f,
                RibbonTaper = 0.06f,
                RibbonHead = new Color(1f, 0.9f, 0.7f, 0.8f),
                RibbonTail = new Color(0.9f, 0.6f, 0.3f, 0f),
                RibbonBlend = FxBlend.Additive,
                Puffs = new[] { Smoke(smoke, 18f, 0.3f, 2.4f, 1.3f, 0.3f) },
            };
        return Trails.Rent(at, style);
    }

    private static TrailPuff Smoke(SpriteSheet? sheet, float perSecond, float size, float end, float life, float alpha) => new()
    {
        Sheet = sheet,
        PerSecond = perSecond,
        Size = size,
        SizeJitter = 0.25f,
        SizeEnd = end,
        Life = life,
        LifeJitter = 0.25f,
        Spread = 0.04f,
        Velocity = new Vector3(0f, 0.25f, 0f),
        VelocityJitter = new Vector3(0.15f, 0.1f, 0.15f),
        Drag = 0.8f,
        Spin = 0.6f,
        Colour0 = new Color(0.62f, 0.6f, 0.58f, alpha),
        Colour1 = new Color(0.45f, 0.44f, 0.43f, 0f),
    };
}

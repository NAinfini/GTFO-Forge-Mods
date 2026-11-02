using System.Collections.Generic;

namespace ForgeVisual;

/// <summary>One effect of the bundle Forge ships (<c>Forge:forge_sparks</c>) and how it plays: <see cref="Line"/> is a one-metre +Z prefab
/// stretched from a point to an end, <see cref="DefaultSize"/> the size (m) it is authored at (NaN: it takes no size), and a
/// <see cref="HoldSeconds"/> above 0 a prefab that loops at its root and is held (<see cref="HoldReach"/>: a hold within that many metres of a
/// live copy moves it).</summary>
internal readonly record struct ForgeEffectSpec(string Item, bool Line, float DefaultSize, float HoldSeconds, float HoldReach);

/// <summary>The effects in <c>forge-vfx.bundle</c>, registered under the reserved pack <c>Forge</c> when the library loads. This table is the
/// one place their line, size and hold behaviour is written; every mod plays them by name through <c>Fx</c>.</summary>
internal static class ForgeEffects
{
    internal static readonly IReadOnlyList<ForgeEffectSpec> All = new[]
    {
        new ForgeEffectSpec("forge_energy_beam", true, float.NaN, 0.12f, 1.5f),
        new ForgeEffectSpec("forge_lightning_arc", true, float.NaN, 0f, 0f),
        new ForgeEffectSpec("forge_blast_tracer", true, float.NaN, 0f, 0f),
        new ForgeEffectSpec("forge_plasma_discharge", true, float.NaN, 0f, 0f),
        new ForgeEffectSpec("forge_plasma_orb", false, 0.3f, 0.25f, 1.5f),
        new ForgeEffectSpec("forge_plasma_explosion", false, 3f, 0f, 0f),
        new ForgeEffectSpec("forge_explosion", false, 2.2f, 0f, 0f),
        new ForgeEffectSpec("forge_flame", false, 8f, 0.12f, 1.5f),
        new ForgeEffectSpec("forge_flame_impact", false, float.NaN, 0f, 0f),
        new ForgeEffectSpec("forge_sparks", false, float.NaN, 0f, 0f),
        new ForgeEffectSpec("forge_black_hole", false, 8f, 0.5f, 2f),
    };
}

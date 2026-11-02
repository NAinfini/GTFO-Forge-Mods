using System;
using System.Collections;
using System.IO;
using ForgeVisual;
using ForgeWeaponEnergyLab.Native;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>
/// The energy weapons' effect prefabs. The effects come from ForgeVisual's shared bundle (pack <c>Forge</c>, CC0); the ricochet disc bundle is embedded in this DLL and
/// registered with ForgeVisual as the pack <c>EnergyLabDisc</c>; every prefab is resolved once into a
/// <see cref="ModelHandle"/> and played from ForgeVisual's pools. Prefabs face +Z; line members are one metre long. A missing
/// bundle or prefab fails preparation rather than substituting another effect.
/// </summary>
internal static class EnergyVfx
{
    private const string Pack = "Forge", DiscPack = "EnergyLabDisc";

    internal static ModelHandle Beam = null!, Arc = null!, Explosion = null!, Flame = null!, FlameImpact = null!, Discharge = null!,
        PlasmaExplosion = null!, Orb = null!, Sparks = null!, BlackHole = null!, Disc = null!;

    internal static IEnumerator Preload()
    {
        Register(DiscPack, "forge-energy-disc.bundle");
        Beam = Resolve(Pack, "forge_energy_beam");
        Arc = Resolve(Pack, "forge_lightning_arc");
        Explosion = Resolve(Pack, "forge_explosion");
        Flame = Resolve(Pack, "forge_flame");
        FlameImpact = Resolve(Pack, "forge_flame_impact");
        Discharge = Resolve(Pack, "forge_plasma_discharge");
        PlasmaExplosion = Resolve(Pack, "forge_plasma_explosion");
        Orb = Resolve(Pack, "forge_plasma_orb");
        Sparks = Resolve(Pack, "forge_sparks");
        BlackHole = Resolve(Pack, "forge_black_hole");
        Disc = Resolve(DiscPack, "forge_ricochet_disc");
        UseNativeDiscShaders(Disc.Prefab);
        yield break;
    }

    private static void Register(string pack, string file)
    {
        var resource = "ForgeWeaponEnergyLab.Vfx." + file;
        using var stream = typeof(EnergyVfx).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("Missing " + resource);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        if (ForgeVisual.Bundles.RegisterBytes(Plugin.Id, pack, file, bytes.ToArray()) == null)
            throw new InvalidDataException("ForgeVisual refused the effect bundle " + file + " (see its error in the log).");
    }

    private static ModelHandle Resolve(string pack, string member)
        => Models.Get(pack + ":" + member) ?? throw new InvalidDataException("Missing VFX " + member);

    // Baked material parameters use the game's shaders, just like native disc construction.
    private static void UseNativeDiscShaders(GameObject prefab)
    {
        var renderer = prefab.GetComponentInChildren<MeshRenderer>();
        var mesh = prefab.GetComponentInChildren<MeshFilter>()?.sharedMesh;
        if (renderer == null || mesh == null || mesh.subMeshCount != 4 || renderer.sharedMaterials.Length != 4)
            throw new InvalidDataException("Disc prefab requires four surfaces.");
        for (var i = 0; i < 4; i++)
        {
            var material = renderer.sharedMaterials[i] ?? throw new InvalidDataException("Missing disc material.");
            material.shader = Shader.Find(i == 3 ? "Sprites/Default" : "Standard")
                ?? throw new InvalidOperationException("Native disc shader is unavailable.");
            if (i < 3) material.EnableKeyword("_EMISSION");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

// The bundle's presentation-profile, written by tools/BuildWeaponBundle.cs from the parts library.
// Unity coordinates in metres: +Z muzzle, +Y up, root origin at the right-hand grip.
internal sealed class PresentationColor
{
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public float A { get; set; }
}

internal sealed class PartPresentation
{
    public string Name { get; set; } = "";
    // body: textured with its texture set; glass: transparent tint; reticle: unlit first-person mark.
    public string Surface { get; set; } = "";
    public string TextureSet { get; set; } = "";
    public Vector3 Pivot { get; set; }
    public PresentationColor Color { get; set; } = new();
}

internal sealed class TextureSetPresentation
{
    public string Name { get; set; } = "";
    public bool Emission { get; set; }
    public bool Normal { get; set; }
}

internal sealed class WeaponPresentation
{
    public string Mode { get; set; } = "";
    public Vector3 RightHand { get; set; }
    public Vector3 LeftHand { get; set; }
    public Vector3 Muzzle { get; set; }
    public Vector3 SightLook { get; set; }
    public Vector3 BoundsMin { get; set; }
    public Vector3 BoundsMax { get; set; }
    public PartPresentation[] Parts { get; set; } = Array.Empty<PartPresentation>();
}

internal sealed class PresentationContract
{
    internal const int CurrentFormat = 4;
    private static readonly string[] RequiredParts = { "Receiver", "Rail", "Front", "Stock", "Magazine", "Sight" };
    public int Format { get; set; }
    public WeaponPresentation[] Weapons { get; set; } = Array.Empty<WeaponPresentation>();
    public TextureSetPresentation[] TextureSets { get; set; } = Array.Empty<TextureSetPresentation>();

    internal TextureSetPresentation TextureSet(string name) =>
        Array.Find(TextureSets, set => set.Name == name) ?? throw new ArgumentException("Unknown texture set: " + name);

    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    internal static PresentationContract Parse(string json)
    {
        var result = JsonSerializer.Deserialize<PresentationContract>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, IncludeFields = true })
            ?? throw new ArgumentException("Missing presentation contract.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (result.Format != CurrentFormat || result.Weapons.Length == 0 || result.Weapons.Length > 128)
            throw new ArgumentException($"Expected one to 128 format-{CurrentFormat} model presentations.");
        var sets = new HashSet<string>();
        foreach (var set in result.TextureSets)
            if (string.IsNullOrEmpty(set.Name) || !sets.Add(set.Name)) throw new ArgumentException("Invalid or duplicate texture set.");
        foreach (var weapon in result.Weapons)
        {
            if (string.IsNullOrWhiteSpace(weapon.Mode) || !keys.Add(weapon.Mode)) throw new ArgumentException("Invalid or duplicate weapon: " + weapon.Mode);
            foreach (var point in new[] { weapon.RightHand, weapon.LeftHand, weapon.Muzzle, weapon.SightLook, weapon.BoundsMin, weapon.BoundsMax })
                if (!Finite(point)) throw new ArgumentException("Non-finite socket or bounds: " + weapon.Mode);
            var depth = weapon.BoundsMax.Z - weapon.BoundsMin.Z;
            if (weapon.BoundsMin.X >= weapon.BoundsMax.X || weapon.BoundsMin.Y >= weapon.BoundsMax.Y || depth < .5f || depth > 1.3f)
                throw new ArgumentException("Invalid metric weapon bounds: " + weapon.Mode);
            var names = new HashSet<string>();
            foreach (var part in weapon.Parts)
            {
                if (string.IsNullOrEmpty(part.Name) || !names.Add(part.Name) || !Finite(part.Pivot))
                    throw new ArgumentException($"Invalid or duplicate part: {weapon.Mode}/{part.Name}");
                var tinted = part.Surface is "glass" or "reticle";
                if (part.Surface == "body" ? !sets.Contains(part.TextureSet) : !tinted || part.TextureSet != "" || part.Color.A <= 0)
                    throw new ArgumentException($"Invalid part surface: {weapon.Mode}/{part.Name}");
            }
            foreach (var required in RequiredParts)
                if (!names.Contains(required)) throw new ArgumentException($"Missing part: {weapon.Mode}/{required}");
        }
        return result;
    }
}

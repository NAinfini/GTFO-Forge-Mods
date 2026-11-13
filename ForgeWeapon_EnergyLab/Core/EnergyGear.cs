global using ForgeWeaponEnergyLab.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace ForgeWeaponEnergyLab;

internal sealed class EnergyGear
{
    internal readonly WeaponDefinition Definition;
    internal readonly uint GearId, CategoryId, ArchetypeId;
    internal readonly WeaponNumbers Numbers;
    internal EnergyMode Mode => Definition.Mode;
    internal string Name => Definition.Name;
    internal bool Main => Definition.Slot == WeaponSlot.Main;
    internal int Clip => Definition.Tuning.Magazine;
    internal float AmmoPerRound => Definition.Tuning.AmmoPerRound;
    internal string NativeName => "ForgeWeaponEnergyLab_" + Definition.Id;
    internal readonly string ModelPath;
    /// <summary>Content identity of the model bundle and the bundles it needs, computed once when registration is sealed.</summary>
    internal string ModelDigest { get; set; } = "";
    internal EnergyGear(WeaponDefinition definition)
    {
        Definition = definition;
        ModelPath = Path.GetFullPath(definition.Model.BundlePath);
        (GearId, CategoryId, ArchetypeId) = EnergyGears.Ids(definition.Id);
        Numbers = WeaponNumbers.Resolve(definition.Tuning);
    }
}

internal static class EnergyGears
{
    internal static EnergyGear[] All { get; private set; } = Array.Empty<EnergyGear>();
    /// <summary>Every category ID is <c>CategoryBase | (hash &amp; 0x7fff)</c>, so tables indexed by category hold <see cref="CategorySlots"/> rows from <see cref="Slot"/>.</summary>
    internal const uint CategoryBase = 0x8000u;
    internal const int CategorySlots = 0x8000;
    internal static int Slot(uint categoryId) => categoryId - CategoryBase < CategorySlots ? (int)(categoryId - CategoryBase) : -1;
    private static readonly EnergyGear?[] Categories = new EnergyGear[CategorySlots];
    private static bool _frozen;
    internal static (uint Gear, uint Category, uint Archetype) Ids(string id)
        => (0x20000000u | (ForgeCore.Fnv.Hash32(id + "/gear") & 0x1fffffffu),
            CategoryBase | (ForgeCore.Fnv.Hash32(id + "/category") & (CategorySlots - 1u)),
            0x20000000u | (ForgeCore.Fnv.Hash32(id + "/archetype") & 0x1fffffffu));

    /// <summary>Closes registration once. A weapon whose bundle is missing or the wrong size is left out with a line in <paramref name="errors"/>; the others stay.
    /// <paramref name="digest"/> is the content identity of a bundle with the bundles it needs (ForgeCore <c>Assets.Digest</c>).</summary>
    internal static void Freeze(List<string> errors, Func<string, string> digest)
    {
        if (_frozen) return;
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<EnergyGear>();
        foreach (var gear in EnergyLab.Freeze(errors))
        {
            if (!hashes.ContainsKey(gear.ModelPath))
            {
                var info = new FileInfo(gear.ModelPath);
                if (!info.Exists || info.Length < 1024 || info.Length > 128L * 1024 * 1024)
                {
                    errors.Add(gear.Definition.Id + ".model.bundle: missing bundle or size outside 1 KiB..128 MiB: " + gear.ModelPath + "; this weapon is skipped.");
                    continue;
                }
                try { hashes.Add(gear.ModelPath, digest(gear.ModelPath)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    errors.Add(gear.Definition.Id + ".model.bundle: could not be read (" + error.Message + "): " + gear.ModelPath + "; this weapon is skipped.");
                    continue;
                }
            }
            gear.ModelDigest = hashes[gear.ModelPath];
            kept.Add(gear);
        }
        foreach (var gear in kept) Categories[Slot(gear.CategoryId)] = gear;
        All = kept.ToArray();
        _frozen = true;
    }
    internal static EnergyGear? ForCategory(uint categoryId)
    {
        var slot = Slot(categoryId);
        return slot < 0 ? null : Categories[slot];
    }
    internal static EnergyMode ModeForCategory(uint categoryId) => ForCategory(categoryId)?.Mode ?? EnergyMode.Off;
    internal static EnergyGear? Match(uint categoryId, int mode) => ForCategory(categoryId) is { } gear && (int)gear.Mode == mode ? gear : null;
}

internal static class EnergyGearJson
{
    // Gear component 21 is the sight part, and the sight part sets the aim zoom. Vanilla Sight_Empty
    // (world 54, item 32) is the lightest zoom and leaves the gun its hip size; our models draw the optic.
    internal const int SightComponent = 21;
    internal const uint ZoomSightPart = 25;

    internal static string Build(string original, EnergyGear gear)
    {
        if (gear.CategoryId > ushort.MaxValue)
            throw new InvalidOperationException("Gear category ID exceeds the native 16-bit packet limit.");
        var root = JsonNode.Parse(original)?.AsObject() ?? throw new InvalidOperationException("Invalid native gear JSON.");
        root["Name"] = gear.Name;
        var packet = root["Packet"]?.AsObject() ?? throw new InvalidOperationException("Native gear packet missing.");
        var components = packet["Comps"]?.AsObject() ?? throw new InvalidOperationException("Native gear components missing.");
        bool category = false, sight = false, audio = false, casing = false;
        foreach (var (_, value) in components)
        {
            if (value is not JsonObject component) continue;
            switch (component["c"]?.GetValue<int>())
            {
                case 2: component["v"] = gear.CategoryId; category = true; break;
                case 5: component["v"] = gear.CategoryId; audio = true; break;
                case 7: component["v"] = gear.CategoryId; casing = true; break;
                case SightComponent: component["v"] = ZoomSightPart; sight = true; break;
            }
        }
        if (!category) throw new InvalidOperationException("Native gear has no category component.");
        if (!sight) throw new InvalidOperationException("Native gear has no sight component.");
        if (!audio || !casing) throw new InvalidOperationException("Native gear has no audio or casing component.");
        var publicName = packet["publicName"]?.AsObject() ?? throw new InvalidOperationException("Native gear public name missing.");
        publicName["data"] = gear.Name;
        return root.ToJsonString();
    }
}

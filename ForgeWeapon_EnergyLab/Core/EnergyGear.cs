global using ForgeEnergyLab.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ForgeWeaponEnergyLabExperimental;

// Built-in IDs stay fixed because saved loadouts reference them.
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
    internal string NativeName => "ForgeWeaponEnergyLabExperimental_" + (GearId is >= 794201 and <= 794208 ? Mode.ToString() : Definition.Id);
    internal readonly string ModelPath;
    internal EnergyGear(WeaponDefinition definition)
    {
        Definition = definition;
        // Each built-in model ships as its own bundle beside the plugin.
        ModelPath = Path.GetFullPath(definition.Model.BundlePath ?? Path.Combine(
            Path.GetDirectoryName(typeof(EnergyGear).Assembly.Location)!, $"forge-energy-model-{definition.Model.Key}.bundle"));
        (GearId, CategoryId, ArchetypeId) = EnergyGears.Ids(definition.Id);
        Numbers = WeaponNumbers.Resolve(definition.Tuning);
    }
}

internal static class EnergyGears
{
    internal static EnergyGear[] All { get; private set; } = Array.Empty<EnergyGear>();
    private static readonly EnergyGear?[] Categories = new EnergyGear[ushort.MaxValue + 1];
    internal static readonly Dictionary<string, string> ModelHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ModelKeys = { "beam", "arc", "flame", "plasma-blast", "plasma-arc", "blast", "disc", "hole" };
    private static readonly string[] BuiltInIds = { "forge.energylab.beam", "forge.energylab.arc", "forge.energylab.flame", "forge.energylab.plasma-blast", "forge.energylab.plasma-arc", "forge.energylab.blast", "forge.energylab.disc", "forge.energylab.hole" };
    internal static WeaponModel BuiltInModel(string key)
    {
        if (!Array.Exists(ModelKeys, model => model == key)) throw new ArgumentException("Unknown built-in model key: " + key, nameof(key));
        return new WeaponModel(key);
    }
    internal static WeaponDefinition[] Defaults() => new[]
    {
        new WeaponDefinition(BuiltInIds[0], "Forge Beam", WeaponSlot.Main, EnergyMode.Beam, new BeamTuning(), BuiltInModel("beam")),
        new WeaponDefinition(BuiltInIds[1], "Forge Chain Arc", WeaponSlot.Main, EnergyMode.ArcChain, new ArcChainTuning(), BuiltInModel("arc")),
        new WeaponDefinition(BuiltInIds[2], "Forge Flamethrower", WeaponSlot.Main, EnergyMode.Flame, new FlameTuning(), BuiltInModel("flame")),
        new WeaponDefinition(BuiltInIds[3], "Forge Charge Orb", WeaponSlot.Special, EnergyMode.PlasmaBlast, new PlasmaBlastTuning(), BuiltInModel("plasma-blast")),
        new WeaponDefinition(BuiltInIds[4], "Forge Shock Orb", WeaponSlot.Special, EnergyMode.PlasmaArc, new PlasmaArcTuning(), BuiltInModel("plasma-arc")),
        new WeaponDefinition(BuiltInIds[5], "Forge Blast Gun", WeaponSlot.Special, EnergyMode.Blast, new BlastTuning(), BuiltInModel("blast")),
        new WeaponDefinition(BuiltInIds[6], "Forge Ricochet Disc", WeaponSlot.Special, EnergyMode.Disc, new DiscTuning(), BuiltInModel("disc")),
        new WeaponDefinition(BuiltInIds[7], "Forge Gravity Core", WeaponSlot.Special, EnergyMode.BlackHole, new BlackHoleTuning(), BuiltInModel("hole"))
    };
    internal static (uint Gear, uint Category, uint Archetype) Ids(string id)
    {
        var builtIn = Array.IndexOf(BuiltInIds, id);
        if (builtIn >= 0) return ((uint)(794201 + builtIn), (uint)(65001 + builtIn), (uint)(794101 + builtIn));
        static uint Hash(string text)
        {
            var hash = 2166136261u;
            foreach (var value in Encoding.UTF8.GetBytes(text)) hash = unchecked((hash ^ value) * 16777619u);
            return hash;
        }
        return (0x20000000u | (Hash(id + "/gear") & 0x1fffffffu),
            0x8000u | (Hash(id + "/category") & 0x7fffu),
            0x20000000u | (Hash(id + "/archetype") & 0x1fffffffu));
    }
    internal static void Freeze()
    {
        if (All.Length != 0) return;
        var staged = EnergyLab.Freeze();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gear in staged)
        {
            if (gear.Definition.Model.IconPath is { } icon && new FileInfo(icon) is var iconFile &&
                (!iconFile.Exists || iconFile.Length < 32 || iconFile.Length > 1024 * 1024))
                throw new InvalidDataException(gear.Definition.Id + ".model.iconPath: missing PNG or size outside 32 B..1 MiB: " + icon);
            if (!hashes.ContainsKey(gear.ModelPath))
            {
                var info = new FileInfo(gear.ModelPath);
                if (!info.Exists || info.Length < 1024 || info.Length > 128L * 1024 * 1024)
                    throw new InvalidDataException(gear.Definition.Id + ".model.bundlePath: missing bundle or size outside 1 KiB..128 MiB: " + gear.ModelPath);
                using var stream = File.OpenRead(gear.ModelPath);
                using var sha = SHA256.Create();
                hashes.Add(gear.ModelPath, Convert.ToHexString(sha.ComputeHash(stream)));
            }
        }
        foreach (var gear in staged) Categories[gear.CategoryId] = gear;
        foreach (var pair in hashes) ModelHashes.Add(pair.Key, pair.Value);
        All = staged;
    }
    internal static EnergyGear? ForCategory(uint categoryId) => categoryId <= ushort.MaxValue ? Categories[categoryId] : null;
    internal static EnergyMode ModeForCategory(uint categoryId) => ForCategory(categoryId)?.Mode ?? EnergyMode.Off;
    internal static bool IsSelection(uint categoryId, int mode) => categoryId == 0 && mode == (int)EnergyMode.Off || Match(categoryId, mode) != null;
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

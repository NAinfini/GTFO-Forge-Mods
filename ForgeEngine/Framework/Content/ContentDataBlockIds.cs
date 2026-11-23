using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeEngine.Framework;

/// <summary>I-PACK identity. The website issues every number at first publish and writes it into the pack
/// (<c>ids</c> in pack.forge or rundown.forge); the game never derives one. This class holds the number ranges and
/// the collision grouping. The numbers travel with the accepted entry, never in a process-wide table.</summary>
public static class ContentDataBlockIds
{
    /// <summary>Every native table is its own pool. A UInt16 table issues from [First16, Last16]; every other table and
    /// the non-native content number (type "") from [FirstWide, LastWide] without [FirstReserved, LastReserved] (ForgeMap's
    /// compile-time identities). Vanilla ids lie below both pools except Text and LevelLayout, whose vanilla ids are hashes
    /// the website skips when it issues; 0xFFFFFFFF is the UInt32 empty value.</summary>
    public const uint First16 = Wire.ContentIds.First16, Last16 = Wire.ContentIds.Last16;
    public const uint FirstWide = Wire.ContentIds.FirstWide, LastWide = Wire.ContentIds.LastWide;
    public const uint FirstReserved = Wire.ContentIds.FirstReserved, LastReserved = Wire.ContentIds.LastReserved;
    /// <summary>The windows the website allocator starts from. Only test issuers use them.</summary>
    public const uint SeedFirst16 = Wire.ContentIds.SeedFirst16, SeedLast16 = Wire.ContentIds.SeedLast16,
        SeedFirstWide = Wire.ContentIds.SeedFirstWide, SeedLastWide = Wire.ContentIds.SeedLastWide;

    /// <summary>Tables the game itself carries in 16 bits, although DataBlock persistentID is UInt32 (GTFO build read from
    /// Modules-ASM interop and GameAssembly.dll):
    /// EnemyGroup — <c>LG_PopulateArea.Build</c> zero-extends the group id from 16 bits (<c>movzx eax, si</c>) into the
    /// UInt32 <c>persistentGameDataID</c> argument of <c>Enemies.EnemyGroup.GetSpawnData</c>;
    /// SurvivalWaveSettings — <c>pSurvivalWaveSpawnData.settingsID</c> UInt16;
    /// SurvivalWavePopulation — <c>pSurvivalWaveSpawnData.populationDataID</c>, <c>Enemies.pEnemyGroupSpawnData.persistentWavePopDataID</c>,
    /// <c>SurvivalWave.m_populationDataID</c> UInt16;
    /// every gear component table — <c>Gear.GearIDRange.m_comps</c> is <c>UInt16[]</c> indexed by <c>Gear.eGearComponent</c>
    /// (Category, BaseItem, ItemFPSSettings, AudioSetting, MuzzleFlash, ShellCasing, Pattern, Palette, DecalA/B, each
    /// *Part, *PartAttachment and *PartPerk slot), and <c>Gear.GearIDRange.GearComponentValue.v</c> is UInt16; Item is
    /// 16-bit because it fills the BaseItem slot of that array.</summary>
    public static bool UsesUInt16(string type) => Wire.ContentIds.UInt16Types.Contains(type);

    /// <summary>The local band of a 16-bit table and of every other table: an export with no site-issued number (signed out
    /// or offline) takes its numbers there, and the website never issues them.</summary>
    internal const uint FirstLocal16 = Wire.ContentIds.FirstLocal16, LastLocal16 = Wire.ContentIds.LastLocal16;
    internal const uint FirstLocalWide = Wire.ContentIds.FirstLocalWide, LastLocalWide = Wire.ContentIds.LastLocalWide;
    /// <summary>Whether the number lies in the table's local band. Two installed packs that collide on such a number are
    /// refused whole, since neither was issued it.</summary>
    internal static bool IsLocal(string type, uint number)
        => UsesUInt16(type) ? number is >= FirstLocal16 and <= LastLocal16 : number is >= FirstLocalWide and <= LastLocalWide;

    /// <summary>Whether the website (or, in its local band, an offline export) could have issued this number for this table.</summary>
    public static bool InIssuedRange(string type, uint number)
        => UsesUInt16(type) ? number is >= First16 and <= Last16
            : number is >= FirstWide and <= LastWide and not (>= FirstReserved and <= LastReserved);

    /// <summary>Every collision reports every participant, grouped by native table and number. The caller rejects the
    /// owning entry or package; neither side wins by load order.</summary>
    public static IReadOnlyList<ContentIdConflict> Conflicts(IEnumerable<ContentIssuedBlock> entries,
        IEnumerable<ContentVanillaId>? vanilla = null)
    {
        var rows = entries.ToArray();
        var occupied = (vanilla ?? Array.Empty<ContentVanillaId>()).GroupBy(row => (row.Type, row.Id))
            .ToDictionary(group => group.Key, group => group.First());
        var result = new List<ContentIdConflict>();
        foreach (var row in rows)
            if (occupied.TryGetValue((row.Type, row.Number), out var existing))
                result.Add(new(row.Id, existing.Owner ?? "vanilla:" + row.Type + "/" + row.Number, row.Number));
        foreach (var group in rows.GroupBy(row => (row.Type, row.Number)).Where(group => group.Count() > 1))
            foreach (var row in group)
                result.Add(new(row.Id, string.Join(",", group.Where(other => other.Id != row.Id).Select(other => other.Id).OrderBy(id => id, StringComparer.Ordinal)), group.Key.Number));
        // Stable strings remain unique even when the native tables have separate numeric spaces.
        foreach (var group in rows.GroupBy(row => row.Id, StringComparer.Ordinal).Where(group => group.Select(row => row.Type).Distinct(StringComparer.Ordinal).Count() > 1))
            foreach (var row in group) result.Add(new(row.Id, row.Id, row.Number));
        return result.OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }
}
/// <summary>A native block header as an entry declares it, before any number is attached.</summary>
internal readonly record struct ContentDataBlockId(string Id, string Type);
public readonly record struct ContentIssuedBlock(string Id, string Type, uint Number);
public readonly record struct ContentVanillaId(string Type, uint Id, string? Owner = null);
public readonly record struct ContentIdConflict(string Id, string Other, uint PersistentId);

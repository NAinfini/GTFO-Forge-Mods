using System;
using System.Collections.Generic;
using System.Linq;
using ForgeCore;
using ForgeEngine.GameBindings;
using GameData;
using Gear;

namespace ForgeWeapon.Native;

/// <summary>What the weapon tables and the gear catalogue hold now, for the author read <c>AuthorReads.EntryRows</c> (contract I-RELOAD,
/// tier D): every PlayerOfflineGear row Forge owns, whether the gear instance-key index holds its record, the slot pool that holds it, and
/// whether a content entry published it. Read only.</summary>
internal static class WeaponContentReadback
{
    internal static object Read()
    {
        var manager = GearManager.Current;
        var index = manager?.m_allGearPerInstanceKey;
        var pools = manager?.m_gearPerSlot;
        var published = (ContentEntryGeneration.Standing?.Published ?? Array.Empty<(string, uint)>()).Where(row => row.Item1 == "PlayerOfflineGear").Select(row => row.Item2).ToHashSet();
        var rows = NativeDataBlockTables.OwnedRows().Where(row => row.Type == "PlayerOfflineGear").Select(row =>
        {
            var record = EquipmentNativeAdapter.GearBlockPrefix + row.Id;
            int slot = -1;
            for (int at = 0; pools != null && at < pools.Length && slot < 0; at++)
            {
                var pool = pools[at];
                if (pool == null) continue;
                foreach (var gear in pool) if (gear != null && gear.PlayfabItemInstanceId == record) { slot = at; break; }
            }
            return new
            {
                id = row.Id,
                name = PlayerOfflineGearDataBlock.GetBlock(row.Id)?.name ?? "",
                source = published.Contains(row.Id) ? "entry" : "other",
                catalogued = index != null && index.ContainsKey(record),
                slot
            };
        }).ToArray();
        return new
        {
            rows = rows.Select(row => new { row.id, row.source, row.catalogued, row.slot }).ToArray(),
            entryNames = rows.Where(row => row.source == "entry").Select(row => row.name).OrderBy(name => name, StringComparer.Ordinal).ToArray()
        };
    }
}

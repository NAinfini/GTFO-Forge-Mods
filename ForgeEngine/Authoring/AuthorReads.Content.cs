using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.GameBindings;
using LevelGeneration;

namespace ForgeEngine.Authoring;

/// <summary>Author reads of the content generations (contract I-RELOAD).</summary>
public static partial class AuthorReads
{
    /// <summary>Commits a new content generation from the install on disk (<see cref="Plugin.ReloadContent"/>); the answer is the
    /// command result and the generation that is current afterwards, as JSON, refusals included.</summary>
    public static string ContentReload()
    {
        Kernel();
        var result = Plugin.ReloadContent();
        var current = GameRuntimeBridge.Generations.Current;
        return JsonSerializer.Serialize(new { result.Status, result.CommitState, result.Code, result.Detail, generation = current.Number, current.Digest });
    }

    /// <summary>What the committed generation's entry consumers hold now (tier D): the enemy rows, spawn lookup and replication list,
    /// and the weapon gear rows with their catalogue slots, as compact JSON. The replication list is the order the game copies enemy
    /// prefabs by over the network; <c>replicationDigest</c> is its SHA-256 over <c>index:id</c> lines, equal on two machines that hold
    /// the same content.</summary>
    public static string EntryRows()
    {
        Kernel();
        var (enemy, digest) = ForgeEnemy.Native.EnemyContentReadback.Read();
        return JsonSerializer.Serialize(new
        {
            generation = GameRuntimeBridge.Generations.Current.Number,
            enemy,
            weapon = ForgeWeapon.Native.WeaponContentReadback.Read(),
            replicationDigest = digest
        });
    }

    /// <summary>What the committed generation's Map content holds now: the published Rundowns, the native rows they wrote, sidecar
    /// row counts, the active generation plans, the menu's load list and selection, and (in a level) the global light state. As JSON.</summary>
    public static string MapContent()
    {
        Kernel();
        var session = ForgeMap.Native.MapHost.Session;
        var content = session?.Content;
        var active = Globals.Global.ActiveRundownIds;
        var ids = new List<uint>();
        for (int index = 0; active != null && index < active.Length; index++) ids.Add(active[index]);
        var programs = ForgeMap.Native.CompiledMapGenerator.ActivePrograms;
        return JsonSerializer.Serialize(new
        {
            generation = GameRuntimeBridge.Generations.Current.Number,
            rundowns = content == null ? null : content.Published.Packs.Select(pack => new { pack.Package, pack.RundownId, pack.MenuSlot }).ToArray(),
            nativeRows = content?.Rows.Rows.Count,
            rowsWritten = content?.Rows.Written,
            keptSidecars = content?.KeptSidecars,
            triggerZones = content?.Accepted.TriggerZones.Count,
            placedModels = content?.Accepted.PlacedModels.Count,
            programs = programs.Count,
            programLevels = programs.Sum(program => program.Levels.Count()),
            programGeneration = ForgeMap.Native.CompiledMapGenerator.ContentGeneration,
            activeRundownIds = ids,
            rundownIdToLoad = Globals.Global.RundownIdToLoad,
            globalLight = session?.GlobalLight?.Snapshot()
        });
    }

    /// <summary>Where each built zone of the current level reads its zone data from: for every zone, whether
    /// <c>m_settings.m_zoneData</c> is the very object of the LevelLayout row in the table now (same native pointer), and the
    /// alias the build gave the zone beside the <c>AliasOverride</c> of that data and of the table row, so a table change is
    /// seen to reach the next build. As JSON; an empty <c>zones</c> list when no level is built.</summary>
    public static string ZoneDataSource()
    {
        Kernel();
        var expedition = Builder.LevelGenExpedition;
        var layouts = expedition == null ? new uint[3] : new[] { expedition.LevelLayoutData, expedition.SecondaryLayout, expedition.ThirdLayout };
        var zones = new List<object>();
        var floor = LG_LevelBuilder.Current == null ? null : LG_LevelBuilder.Current.m_currentFloor;
        var all = floor == null ? null : floor.allZones;
        for (int index = 0; all != null && index < all.Count; index++)
        {
            var zone = all[index];
            if (zone == null) continue;
            int layer = zone.Layer == null ? -1 : (int)zone.Layer.m_type;
            var data = zone.m_settings == null ? null : zone.m_settings.m_zoneData;
            uint layout = layer is >= 0 and < 3 ? layouts[layer] : 0;
            var block = layout == 0 ? null : GameData.LevelLayoutDataBlock.GetBlock(layout);
            GameData.ExpeditionZoneData? row = null;
            var rows = block == null ? null : block.Zones;
            for (int at = 0; rows != null && at < rows.Count; at++)
                if (rows[at] != null && rows[at].LocalIndex == zone.LocalIndex) { row = rows[at]; break; }
            zones.Add(new
            {
                dimension = (int)zone.DimensionIndex,
                layer,
                localIndex = (int)zone.LocalIndex,
                layout,
                alias = zone.Alias,
                dataAliasOverride = data?.AliasOverride,
                rowAliasOverride = row?.AliasOverride,
                sameObject = data != null && row != null && data.Pointer == row.Pointer
            });
        }
        return JsonSerializer.Serialize(new { generation = GameRuntimeBridge.Generations.Current.Number, layouts, zones });
    }
}

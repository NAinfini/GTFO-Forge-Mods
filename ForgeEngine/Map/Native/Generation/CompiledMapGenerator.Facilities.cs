using System.Collections.Generic;
using LevelGeneration;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    // A requested patrol snapshot only. Never scans unfinished generation or adds frame work.
    private static object[] ReadZoneFacilities(Run run, bool factoryDone)
    {
        if (!factoryDone) return System.Array.Empty<object>();
        ForgeMap.Native.ZoneIndex.Reset();
        var rows = new List<object>();
        foreach (int index in run.LevelProgram.Zones)
        {
            if (!EnabledZone(run, index)) continue;
            var definition = run.Program.Zones[index];
            var zone = run.Zones[index];
            if (zone == null || zone.WasCollected)
            {
                rows.Add(new { zone = definition.Id, status = "unavailable", reason = "gen-zone-unavailable" });
                continue;
            }
            var data = zone.m_settings?.m_zoneData;
            var gate = zone.m_sourceGate;
            var door = gate?.SpawnedDoor;
            var security = door?.TryCast<LG_SecurityDoor>();
            var decision = security == null ? (MapObjectAddressDecision?)null : DoorObservation.Decide(security);
            rows.Add(new
            {
                zone = definition.Id,
                status = "read",
                nativeZone = zone.Pointer.ToInt64(),
                forbidTerminals = data?.ForbidTerminalsInZone,
                terminalPlacements = data?.TerminalPlacements?.Count,
                terminalsSpawned = zone.TerminalsSpawnedInZone?.Count,
                sourceGate = gate?.Pointer.ToInt64(),
                sourceDoorType = door?.DoorType.ToString(),
                securityDoor = security?.Pointer.ToInt64(),
                entranceAddress = decision?.Address?.ToString(),
                entranceRefusal = decision?.Refusal?.ToString()
            });
        }
        return rows.ToArray();
    }
}

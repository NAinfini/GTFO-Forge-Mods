using AIGraph;
using ForgeEngine.Framework;
using GameData;

namespace ForgeMap.Native;

/// <summary>Admission for the native positioned-wave query, not a replacement spawn selector.
/// GetScoredSpawnPoint (20403457, 0x13f40da) passes the supplied node's cluster to
/// TryGetClosestNodeInCluster (0x1a0e9a0), which dereferences its volume and voxel volume.
/// Other spawn strategies do not use that query and retain their native admission.</summary>
internal static class WaveSpawnValidation
{
    internal static SurvivalWaveSpawnType EffectiveType(uint settingsId, SurvivalWaveSpawnType requested)
    {
        var settings = GameDataBlockBase<SurvivalWaveSettingsDataBlock>.GetBlock(settingsId);
        return settings != null && !settings.WasCollected && settings.m_overrideWaveSpawnType
            ? settings.m_survivalWaveSpawnType : requested;
    }

    internal static string? Refusal(AIG_CourseNode? node, SurvivalWaveSpawnType spawnType)
    {
        if (spawnType != SurvivalWaveSpawnType.InSuppliedCourseNode_OnPosition) return null;
        if (node == null || node.WasCollected) return "wave-spawn-course-node-unavailable";
        var cluster = node.m_nodeCluster;
        if (cluster == null || cluster.WasCollected) return "wave-spawn-node-cluster-unavailable";
        var volume = cluster.m_nodeVolume;
        if (volume == null || volume.WasCollected) return "wave-spawn-node-volume-unavailable";
        var voxels = volume.m_voxelNodeVolume;
        return voxels == null || voxels.WasCollected ? "wave-spawn-voxel-volume-unavailable" : null;
    }

    internal static void Record(string refusal, AIG_CourseNode? node, uint settingsId, uint populationId,
        SurvivalWaveSpawnType spawnType)
    {
        var current = node != null && !node.WasCollected ? node : null;
        var zone = current?.m_zone;
        var area = current?.m_area;
        MapLog.Record(RuntimeLogLevel.Error, "map.wave-spawn-refused",
            "Wave request refused: its positioned spawn query has no native navigation volume.",
            RuntimeLogValue.Of("reason", refusal), RuntimeLogValue.Of("settingsId", (long)settingsId),
            RuntimeLogValue.Of("populationId", (long)populationId), RuntimeLogValue.Of("spawnType", (long)spawnType),
            RuntimeLogValue.Of("level", LevelIdentity.Read()?.ToString() ?? ""),
            RuntimeLogValue.Of("courseNode", current?.Pointer.ToInt64() ?? 0),
            RuntimeLogValue.Of("dimension", current?.m_dimension == null ? -1 : (long)current.m_dimension.DimensionIndex),
            RuntimeLogValue.Of("layer", zone == null || zone.WasCollected || zone.m_layer == null ? -1 : (long)zone.m_layer.m_type),
            RuntimeLogValue.Of("zone", zone == null || zone.WasCollected ? -1 : (long)zone.LocalIndex),
            RuntimeLogValue.Of("area", area == null || area.WasCollected ? "" : area.UID.ToString()));
    }
}

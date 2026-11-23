using GameData;
using LevelGeneration;
using Player;
using AIGraph;
using ForgeEngine.Framework;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>Reads one native event invocation. No pending warp table, timer or second event executor.
/// Setup owns the event object; pointer identity distinguishes it from every terminal and other portal event.
/// A coroutine yield without an actual player location transition publishes nothing.</summary>
internal static class PortalWarpObservation
{
    internal sealed record Reading(LG_DimensionPortal Portal, PlayerAgent Player, AIG_CourseNode FromNode,
        UnityEngine.Vector3 FromPosition, string PortalId, int From, int To);
    internal sealed record Warp(string PortalId, int From, int To);

    internal static bool IsCurrent(EntityReference reference)
    {
        if (reference.LifeEpoch != 1 || LevelGeneration.Dimension.WarpableObjects == null) return false;
        foreach (var warpable in LevelGeneration.Dimension.WarpableObjects)
        {
            var portal = warpable?.TryCast<LG_DimensionPortal>();
            if (portal != null && LevelEventObservation.PortalId(portal) == reference.Id) return true;
        }
        return false;
    }

    internal static Reading? Capture(WardenObjectiveEventData? data)
    {
        if (data == null || data.Type != eWardenObjectiveEventType.DimensionWarpTeam) return null;
        var player = Players.Local;
        var fromNode = player?.CourseNode;
        if (player == null || fromNode?.m_dimension == null || LevelGeneration.Dimension.WarpableObjects == null) return null;
        LG_DimensionPortal? source = null;
        foreach (var warpable in LevelGeneration.Dimension.WarpableObjects)
        {
            var portal = warpable?.TryCast<LG_DimensionPortal>();
            if (portal == null || portal.m_portalEventData == null
                || portal.m_portalEventData.Pointer != data.Pointer) continue;
            // An ambiguous source is not a fact about either door.
            if (source != null) return null;
            source = portal;
        }
        if (source == null || LevelEventObservation.PortalId(source) is not { } id
            || source.SpawnNode?.m_dimension == null) return null;
        int from = LevelEventObservation.NodeDimension(fromNode), to = (int)data.DimensionIndex;
        if (from != LevelEventObservation.NodeDimension(source.SpawnNode)
            || to != (int)source.m_targetDimension || data.LocalIndex != source.m_targetZone) return null;
        return new Reading(source, player, fromNode, player.Position, id, from, to);
    }

    internal static Warp? Completed(WardenObjectiveEventData data, Reading? reading)
    {
        if (reading == null || reading.Portal == null || reading.Player == null
            || reading.Portal.m_portalEventData == null || reading.Portal.m_portalEventData.Pointer != data.Pointer
            || data.Type != eWardenObjectiveEventType.DimensionWarpTeam) return null;
        var arrived = reading.Player.CourseNode;
        if (arrived?.m_dimension == null || LevelEventObservation.NodeDimension(arrived) != reading.To
            || arrived.Pointer == reading.FromNode.Pointer && reading.Player.Position.Equals(reading.FromPosition)) return null;
        return new Warp(reading.PortalId, reading.From, reading.To);
    }
}

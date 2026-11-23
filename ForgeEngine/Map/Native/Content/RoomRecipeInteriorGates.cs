using Expedition;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

internal static partial class RoomRecipeBuilder
{
    /// <summary>Supply native interior gates without importing connector geometry. A door uses InternalGate; an open
    /// passage (`passage`) uses the free-passage gate a door-less vanilla connector carries, traversable from the start,
    /// which <see cref="RoomRecipeDoorSelection.GateBuilt"/> checks ends with no door.</summary>
    private static void BuildInteriorGates(LG_Geomorph geo, RoomBuildRecipe recipe)
    {
        foreach (var door in recipe.InteriorDoorways)
        {
            var from = geo.m_areas[recipe.AreaIndex(door.FromArea!)];
            var to = geo.m_areas[recipe.AreaIndex(door.ToArea!)];
            var entry = Child(from.transform, door.Name);
            entry.transform.localPosition = V(door.Position);
            entry.transform.localRotation = Quaternion.Euler(door.Pitch, door.Yaw, door.Roll);
            entry.transform.localScale = Vector3.one;
            bool passage = door.Kind != "door";
            LG_Gate gate = passage ? entry.AddComponent<LG_FreePassageGate>() : entry.AddComponent<LG_InternalGate>();
            gate.Type = RoomRecipeDoorSelection.GateType(door.Size);
            gate.m_originalParentArea = from; gate.m_linksFrom = from; gate.m_linksTo = to;
            gate.m_originalForward = V(door.Forward); gate.m_hasOriginalForward = true;
            gate.m_subComplex = SubComplex.Storage; gate.ExpanderStatus = LG_ZoneExpanderStatus.Connected;
            gate.m_isZoneSource = false; gate.m_wasProcessed = false; gate.m_wasProcessedByCourseGraph = false;
            gate.m_isPositionComputed = false;
            gate.ForceSecurityGate = gate.ForceApexGate = gate.ForceBulkheadGate = gate.ForceBulkheadGateMainPath = false;
            // An `open` doorway asks the native opening decision for a door that starts open; an interior gate is no zone
            // source, so native CalcOpenAtStart reads this flag. Every other kind leaves that decision to the native roll.
            gate.ForceOpenFromStart = false; gate.IsCheckpointDoor = false;
            gate.m_isTraversable = gate.m_isTraversableFromStart = passage;
            RoomRecipeDoorIdentity.Stamp(gate, door);
            from.m_gates.Add(gate); to.m_gates.Add(gate);
            from.m_zoneExpanders.Add(gate); to.m_zoneExpanders.Add(gate);
            entry.SetActive(true);
        }
    }
}

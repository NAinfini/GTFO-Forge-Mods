using Expedition;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>The socket construction used by the full room builder and offline package admission.
/// Each exit belongs to the area the recipe declares; geo.m_areas follows the recipe's area order.</summary>
internal static class RoomRecipeSockets
{
    internal static void Build(LG_Geomorph geo, RoomBuildRecipe recipe)
    {
        foreach (var doorway in recipe.BuildDoorways)
        {
            int index = recipe.AreaIndex(doorway.FromArea ?? "");
            if (index < 0 || index >= geo.m_areas.Length) throw new System.InvalidOperationException("recipe-exterior-door-area-missing: " + doorway.Name);
            var owner = geo.m_areas[index];
            var entry = new GameObject(doorway.Name);
            entry.transform.SetParent(owner.transform, false);
            entry.transform.localPosition = new Vector3(doorway.Position[0], doorway.Position[1], doorway.Position[2]);
            entry.transform.localRotation = Quaternion.Euler(doorway.Pitch, doorway.Yaw, doorway.Roll);
            var plug = entry.AddComponent<LG_Plug>();
            RoomRecipeDoorIdentity.Stamp(plug, doorway);
            plug.m_originalParentArea = owner; plug.m_linksFrom = owner;
            plug.m_originalForward = new Vector3(doorway.Forward[0], doorway.Forward[1], doorway.Forward[2]);
            plug.m_hasOriginalForward = true;
            plug.m_subComplex = (SubComplex)doorway.SubComplex;
            geo.m_plugs.Add(plug); owner.m_zoneExpanders.Add(plug);
        }
    }
}

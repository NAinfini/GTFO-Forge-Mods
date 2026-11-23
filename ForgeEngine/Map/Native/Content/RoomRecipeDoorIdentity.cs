using System;
using System.Collections.Generic;
using System.Linq;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>A child written by the constructor survives native scene naming and Unity cloning.
/// Only the actual socket or interior gate carrying that child can own an authored connection.</summary>
internal static class RoomRecipeDoorIdentity
{
    private const string Prefix = "Forge doorway identity: ";
    private static readonly Dictionary<IntPtr, (RoomBuildRecipe Recipe, RoomBuildDoor Door)> Sockets = new();

    internal static void Stamp(LG_ZoneExpander plug, RoomBuildDoor door)
    {
        var marker = new GameObject(Prefix + door.Name);
        marker.transform.SetParent(plug.transform, false);
    }

    internal static void Register(LG_Geomorph room, RoomBuildRecipe recipe)
    {
        var doors = recipe.BuildDoorways.Concat(recipe.InteriorLinks).ToDictionary(row => Prefix + row.Name, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<(LG_ZoneExpander Plug, RoomBuildDoor Door)>();
        foreach (var plug in Enumerate(room))
        {
            if (plug == null || plug.WasCollected) throw new InvalidOperationException("recipe-door-socket-invalid");
            string? identity = null;
            for (int i = 0; i < plug.transform.childCount; i++)
            {
                var name = plug.transform.GetChild(i).gameObject.name;
                if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                if (identity != null) throw new InvalidOperationException("recipe-door-identity-ambiguous");
                identity = name;
            }
            if (identity == null || !doors.TryGetValue(identity, out var door) || !found.Add(identity))
                throw new InvalidOperationException("recipe-door-identity-mismatch: " + plug.gameObject.name);
            var position = plug.transform.localPosition;
            if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)
                || Math.Abs(position.x - door.Position[0]) > .001f || Math.Abs(position.y - door.Position[1]) > .001f
                || Math.Abs(position.z - door.Position[2]) > .001f)
                throw new InvalidOperationException("recipe-door-identity-pose-mismatch: " + door.Name);
            pending.Add((plug, door));
            if (door.ToArea != null)
            {
                int from = recipe.AreaIndex(door.FromArea!), to = recipe.AreaIndex(door.ToArea);
                if (from < 0 || to < 0 || from >= room.m_areas.Length || to >= room.m_areas.Length || plug.m_linksFrom == null || plug.m_linksTo == null
                    || plug.m_linksFrom.Pointer != room.m_areas[from].Pointer || plug.m_linksTo.Pointer != room.m_areas[to].Pointer || from == to)
                    throw new InvalidOperationException("recipe-door-area-identity-mismatch: " + door.Name);
            }
        }
        if (found.Count != doors.Count) throw new InvalidOperationException("recipe-door-identity-incomplete");
        foreach (var row in pending) Sockets[row.Plug.Pointer] = (recipe, row.Door);
    }

    internal static (RoomBuildRecipe Recipe, RoomBuildDoor Door)? Resolve(LG_ZoneExpander plug)
    {
        if (Sockets.TryGetValue(plug.Pointer, out var row)) return row;
        for (int i = 0; i < plug.transform.childCount; i++)
            if (plug.transform.GetChild(i).gameObject.name.StartsWith(Prefix, StringComparison.Ordinal))
                throw RoomRecipeDoorSelection.Fail("recipe-door-socket-unregistered: " + plug.gameObject.name);
        var recipe = RoomRecipeHost.RecipeFor(plug.OriginalParentArea);
        if (recipe == null) return null;
        // Native connectors can contain their own plugs under the original Area. They are not
        // geomorph sockets. A missing receipt for a geomorph socket is corruption, never vanilla.
        var room = plug.OriginalParentArea?.m_geomorph
            ?? throw RoomRecipeDoorSelection.Fail("recipe-door-area-missing");
        foreach (var socket in Enumerate(room))
            if (socket.Pointer == plug.Pointer) throw RoomRecipeDoorSelection.Fail("recipe-door-socket-unregistered: " + plug.gameObject.name);
        return null;
    }

    internal static void Clear() => Sockets.Clear();
    private static IEnumerable<LG_ZoneExpander> Enumerate(LG_Geomorph room)
    {
        foreach (var plug in room.m_plugs) yield return plug;
        foreach (var gate in AuthoredGates(room)) yield return gate;
    }
    /// <summary>The room's own authored gates. LG_PlugDoorAlign derives from LG_InternalGate, so a spawned native connector
    /// (`PlugDoorAlign__FROM&lt;_zoneGate`) hangs below the room with a gate of the same base type; it is the connector's
    /// gate, not a recipe door, and carries no receipt.</summary>
    internal static IEnumerable<LG_Gate> AuthoredGates(LG_Geomorph room)
    {
        foreach (var gate in room.gameObject.GetComponentsInChildren<LG_Gate>(true))
        {
            if (gate.TryCast<LG_PlugDoorAlign>() != null) continue;
            for (int i = 0; i < gate.transform.childCount; i++)
                if (gate.transform.GetChild(i).gameObject.name.StartsWith(Prefix, StringComparison.Ordinal))
                { yield return gate; break; }
        }
    }
}

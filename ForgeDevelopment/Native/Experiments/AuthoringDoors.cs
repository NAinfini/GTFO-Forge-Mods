using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

// Reads and opens the doors around one point through the vanilla entries:
// LG_SecurityDoor.ForceOpenSecurityDoor, the weak door's sync interaction, and the gate's own traversable flag.
// Nothing here moves an actor or changes a door a request did not name by position.
internal static class AuthoringDoors
{
    private sealed record Found(string Kind, Component Door, LG_Gate? Gate, float Distance);

    internal static string Read(string json) => Describe(Collect(json));

    internal static string Open(string json)
    {
        var found = Collect(json);
        foreach (var door in found)
        {
            var security = door.Door.TryCast<LG_SecurityDoor>();
            var weak = door.Door.TryCast<LG_WeakDoor>();
            foreach (var animator in door.Door.GetComponentsInChildren<Animator>(true)) animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (security != null) security.ForceOpenSecurityDoor();
            else if (weak?.m_sync != null) weak.m_sync.AttemptDoorInteraction(eDoorInteractionType.Open, 0f, 0f, Vector3.zero, null);
            if (door.Gate != null && !door.Gate.WasCollected) door.Gate.IsTraversable = true;
        }
        return Describe(found);
    }

    private static List<Found> Collect(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var c = root.GetProperty("center");
        var center = new Vector3(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle());
        float radius = root.GetProperty("radius").GetSingle();
        var rows = new List<Found>();
        foreach (var door in UnityEngine.Object.FindObjectsOfType<LG_SecurityDoor>())
            if (door != null && !door.WasCollected) rows.Add(new("security", door, door.Gate, Vector3.Distance(door.transform.position, center)));
        foreach (var door in UnityEngine.Object.FindObjectsOfType<LG_WeakDoor>())
            if (door != null && !door.WasCollected) rows.Add(new("weak", door, door.Gate, Vector3.Distance(door.transform.position, center)));
        return rows.Where(row => row.Distance <= radius).OrderBy(row => row.Distance).Take(6).ToList();
    }

    private static string Describe(List<Found> found) => JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0.0",
        doors = found.Select(row =>
        {
            var p = row.Door.transform.position;
            var internalGate = row.Gate != null && !row.Gate.WasCollected ? row.Gate.TryCast<LG_InternalGate>() : null;
            var obstacle = internalGate?.m_navmeshObstacle;
            var sync = row.Door.TryCast<LG_SecurityDoor>()?.m_sync ?? row.Door.TryCast<LG_WeakDoor>()?.m_sync;
            return new
            {
                row.Kind,
                name = row.Door.name,
                position = new[] { Math.Round(p.x, 2), Math.Round(p.y, 2), Math.Round(p.z, 2) },
                distance = Math.Round(row.Distance, 2),
                status = sync?.GetCurrentSyncState().status.ToString(),
                gateTraversable = row.Gate != null && !row.Gate.WasCollected ? (bool?)row.Gate.IsTraversable : null,
                obstacle = obstacle == null || obstacle.WasCollected ? null : (bool?)obstacle.isActiveAndEnabled
            };
        }).ToArray()
    });
}

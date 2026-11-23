using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>F5 map area resources. The shape is the existing area-field definition; placement uses the shared
/// room-local volume reader and mathematics, without creating an entity, collider, trigger or scheduler.</summary>
internal sealed class TeleportAreaTable
{
    /// <summary>The optional authored landing: a room-local point and a unit quaternion, both in the room's own frame.</summary>
    private sealed record AuthoredLanding(double[] Position, double[] Rotation);
    private sealed record Entry(string Id, string Revision, string Name, TriggerZone Volume, AuthoredLanding? Landing);
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    public int Count => _entries.Count;
    internal IEnumerable<string> Ids => _entries.Keys;
    internal IEnumerable<MapLevelReference> Levels => _entries.Values.Select(entry => entry.Volume.Level);
    public ResourceRef? Resolve(string id) => _entries.ContainsKey(id) ? new ResourceRef("area_field", id) : null;
    public string? Revision(string id) => _entries.TryGetValue(id, out var entry) ? entry.Revision : null;
    public string? Name(string id) => _entries.TryGetValue(id, out var entry) ? entry.Name : null;
    public int? Dimension(string id, MapLevelReference? level)
        => _entries.TryGetValue(id, out var entry) && level == entry.Volume.Level ? entry.Volume.Room.Dimension : null;

    /// <summary>Activates a table validated before publication while preserving the table object held by
    /// registered services. No document parsing or placement takes place at this boundary.</summary>
    internal void Admit(TeleportAreaTable prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        _entries = prepared._entries;
    }

    /// <summary>Merges parsed area declarations in preparation, before any table is exposed to a session.</summary>
    internal static TeleportAreaTable CombinePrepared(IEnumerable<TeleportAreaTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var combined = new TeleportAreaTable();
        foreach (var table in tables)
        {
            ArgumentNullException.ThrowIfNull(table);
            foreach (var entry in table._entries)
                if (!combined._entries.TryAdd(entry.Key, entry.Value))
                    throw new FormatException("Repeated named area identity: " + entry.Key);
        }
        return combined;
    }

    /// <summary>The named areas of the given <c>area_field</c> documents (path, text); a reusable geometry definition has
    /// no fixed map placement and adds nothing.</summary>
    public static TeleportAreaTable ReadDocuments(IEnumerable<(string Path, string Text)> documents, Action<string> rejected)
    {
        var table = new TeleportAreaTable();
        foreach (var (path, text) in documents)
        {
            if (!TryRead(text, out var entry, out var reason))
            { rejected(PublishedResources.Diagnostic(path, AreaFieldCodes.Measurement, reason)); continue; }
            if (entry != null && !table._entries.TryAdd(entry.Id, entry))
                rejected(PublishedResources.Diagnostic(path, AreaFieldCodes.Measurement, "Repeated named area identity."));
        }
        return table;
    }

    private static bool TryRead(string text, out Entry? entry, out string reason)
    {
        entry = null; reason = "Invalid named area resource.";
        if (!AreaFieldDocument.TryRead(text, out var id, out var revision, out var definition, out _, out _)
            || !PublishedResources.IsSha256(revision)) return false;
        using var json = JsonDocument.Parse(text); var payload = json.RootElement.GetProperty("payload");
        if (!payload.TryGetProperty("placement", out var placement)) return true;
        var size = definition!.Shape switch
        {
            AreaFieldShape.Box => definition.Extents!.Select(value => value * 2).ToArray(),
            AreaFieldShape.Sphere => new[] { definition.Radius * 2, definition.Radius * 2, definition.Radius * 2 },
            AreaFieldShape.Cylinder => new[] { definition.Radius * 2, definition.Height!.Value, definition.Radius * 2 },
            AreaFieldShape.Capsule => new[] { definition.Radius * 2, definition.Height!.Value, definition.Radius * 2 },
            _ => null
        };
        if (size == null) return false;
        // Only the existing volume/room parser validates this placement grammar. The local surrogate is
        // never registered as an object identity; the table and the plan continue to name the resource id.
        var volume = RuntimeJson.From(new
        {
            schemaVersion = FormatVersion.Current,
            zones = new[] { new
        {
            id = "teleport-area", level = placement.GetProperty("level"),room = placement.GetProperty("room"),
            shape = AreaFieldDefinition.MemberOf(definition.Shape),size,
            position = placement.GetProperty("position"),rotation = placement.GetProperty("rotation"),
            who = "player",blocksPlayers = false
        } }
        });
        if (!TriggerZoneManifest.TryParse(volume.GetRawText(), out var zones, out _, out var refusal))
        { reason = refusal ?? reason; return false; }
        AuthoredLanding? landing = null;
        if (placement.TryGetProperty("landing", out var landingJson))
        {
            var rotation = landingJson.GetProperty("rotation").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            if (!Rotations.IsUnit(rotation)) { reason = "The landing rotation is not a unit quaternion."; return false; }
            landing = new AuthoredLanding(landingJson.GetProperty("position").EnumerateArray().Select(value => value.GetDouble()).ToArray(), rotation);
        }
        entry = new Entry(id!, revision!, payload.GetProperty("name").GetString()!, zones![0], landing); return true;
    }

    /// <summary>The authored landing in world terms, `x y z` then the facing as a unit forward vector, or null when
    /// the area is unresolved, belongs to another level/world, or carries no landing. Whether the landing lies in
    /// the area and can be stood on is the teleport's own check against the game's sampler.</summary>
    public double[]? Landing(string id, long world, MapLevelReference? level, Func<long, RoomLocator, RoomPose?> resolve)
    {
        if (!_entries.TryGetValue(id, out var entry) || entry.Landing is not { } landing
            || level == null || entry.Volume.Level != level.Value || resolve(world, entry.Volume.Room) is not { } room) return null;
        var point = room.Point(landing.Position);
        var (x, y, z) = Rotations.Rotate(room.Compose(landing.Rotation), 0, 0, 1);
        return new[] { point[0], point[1], point[2], x, y, z };
    }

    /// <summary>Null is unresolved or a different level/world; false is an actual point outside this area.</summary>
    public bool? Contains(string id, long world, MapLevelReference? level, IReadOnlyList<double> point,
        Func<long, RoomLocator, RoomPose?> resolve)
    {
        if (!_entries.TryGetValue(id, out var entry) || level == null || entry.Volume.Level != level.Value) return null;
        if (resolve(world, entry.Volume.Room) is not { } room) return null;
        return entry.Volume.PlacedIn(room).Contains(point);
    }
}

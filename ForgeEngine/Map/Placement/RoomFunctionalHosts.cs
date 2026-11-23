using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

internal sealed record RoomFunctionalHost(MapLevelReference Level, RoomLocator Room);
internal sealed partial record RoomBuildRecipe
{
    public IReadOnlyList<RoomFunctionalHost> Hosts { get; private init; } = Array.Empty<RoomFunctionalHost>();
    private static IReadOnlyList<RoomFunctionalHost> ReadHosts(JsonElement root, string? function, string id, string revision)
    {
        if (!root.TryGetProperty("hosts", out var rows)) return Array.Empty<RoomFunctionalHost>();
        if (function == null || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 1024)
            throw new FormatException("functional room hosts");
        var result = new List<RoomFunctionalHost>(); var levels = new HashSet<MapLevelReference>();
        foreach (var row in rows.EnumerateArray())
        {
            Fields(row, "level", "room");
            var level = MapLevelReference.TryParse(Text(row, "level")) ?? throw new FormatException("functional room level");
            if (!levels.Add(level) || !RoomLocator.TryParse(row.GetProperty("room"), out var room, out _, out var reason))
                throw new FormatException("functional room locator");
            if (room!.InstanceId == null || room.RoomId != "forge.room:" + id || room.Revision != revision
                || room.SourcePrefab != "Assets/Forge/Rooms/" + id + "/" + revision + ".prefab")
                throw new FormatException("functional room reference mismatch");
            if (function == "start" && (room.Dimension != 0 || room.Layer != 0 || room.LocalIndex != 0))
                throw new FormatException("start room must belong to main dimension/main layer/zone 0");
            if (function == "dimension" && (room.Dimension == 0 || room.Layer != 0 || room.LocalIndex != 0))
                throw new FormatException("dimension root room must belong to another dimension's main layer/zone 0");
            result.Add(new(level, room));
        }
        return result.AsReadOnly();
    }
    /// <summary>One start per level, one root per dimension of a level.</summary>
    public static string FunctionalKey(RoomFunctionalHost host, string function)
        => host.Level + "/" + function + (function == "dimension" ? "/" + host.Room.Dimension : "");
    public static void RequireUniqueFunctionalHosts(IEnumerable<RoomBuildRecipe> recipes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in recipes) foreach (var host in recipe.Hosts)
            if (!seen.Add(FunctionalKey(host, recipe.Design!.Function!))) throw new FormatException("duplicate level functional room host");
    }
}

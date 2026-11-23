using System.Text.Json;

namespace ForgeMap;

/// <summary>The author's group name on a placed trigger zone, placed model or interaction point. One rule reads it in
/// every manifest, and `forge.selector.target.object_group` answers every placed object of one name in this level.</summary>
internal static class MapObjectGroup
{
    public const string Rule = "A group is a non-empty, unpadded name of at most 256 printable characters.";

    /// <summary>True with null when the row has no group; false when the field is present but not a group name.</summary>
    public static bool TryRead(JsonElement row, out string? group)
    {
        group = null;
        return !row.TryGetProperty("group", out _) || DocumentReading.Text(row, "group", out group);
    }
}

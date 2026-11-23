using System;
using System.Text;
using System.Text.Json;

namespace ForgeMap;

/// <summary>The authored room instance plus the locked doorway identity, shared with the website.
/// Native serials, runtime pointers and connector list order are never persisted.</summary>
internal static class InteriorDoorIdentity
{
    public const string KeyPrefix = "interior-";
    public const string ObjectPrefix = "interior-door-";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Key(string room, string door) => KeyPrefix + Pair(room, door);
    public static string ObjectId(string room, string door) => ObjectPrefix + Pair(room, door);
    public static (string Room, string Door)? ParseKey(string value) => Parse(value, KeyPrefix);
    public static (string Room, string Door)? ParseObjectId(string value) => Parse(value, ObjectPrefix);

    private static string Pair(string room, string door)
    {
        if (!Valid(room) || !Valid(door)) throw new ArgumentException("Invalid interior doorway identity.");
        // JSON.stringify's string spelling; validated IDs have no control characters. JsonSerializer's
        // default encoder would escape Unicode and change the website's canonical byte identity.
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return Convert.ToHexString(Utf8.GetBytes("[" + Quote(room) + "," + Quote(door) + "]")).ToLowerInvariant();
    }
    private static bool Valid(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512 || value.Trim() != value) return false;
        foreach (char character in value) if (character is <= '\u001f' or >= '\u007f' and <= '\u009f') return false;
        return true;
    }
    private static (string Room, string Door)? Parse(string value, string prefix)
    {
        if (value == null || !value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var hex = value[prefix.Length..];
        if (hex.Length is < 2 or > 4096 || hex.Length % 2 != 0) return null;
        foreach (char character in hex) if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return null;
        try
        {
            using var json = JsonDocument.Parse(Utf8.GetString(Convert.FromHexString(hex)));
            var pair = json.RootElement;
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2
                || pair[0].ValueKind != JsonValueKind.String || pair[1].ValueKind != JsonValueKind.String) return null;
            var room = pair[0].GetString()!; var door = pair[1].GetString()!;
            return Valid(room) && Valid(door) && Pair(room, door) == hex ? (room, door) : null;
        }
        catch (Exception error) when (error is ArgumentException or JsonException) { return null; }
    }
}

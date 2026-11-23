using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

/// <summary>
/// The one strict JSON dialect every authored package document in this family is read with: a field this build does
/// not declare is refused rather than defaulted, a text is bounded and printable, and a number is finite and inside
/// the range the thing it names can really take. The documents are data an author's own editor wrote, so a mistyped
/// field has to be refused at the document instead of being read as the default nobody chose — which is why the
/// dialect is one implementation: a second reader with a looser rule would be a second answer to whether a
/// document is well formed.
/// </summary>
internal static class DocumentReading
{
    /// <summary>Whether every property of an object is one of the fields the caller declares, so a mistyped field
    /// is refused instead of being read as its own default.</summary>
    internal static bool Known(JsonElement row, out string? unknown, params string[] allowed)
    {
        foreach (var property in row.EnumerateObject())
        {
            var name = property.Name;
            if (Array.IndexOf(allowed, name) < 0) { unknown = name; return false; }
        }
        unknown = null;
        return true;
    }

    /// <summary>Whether the value is an object whose property names are unique and all among the declared fields.</summary>
    internal static bool Known(JsonElement row, params string[] allowed)
    {
        if (row.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in row.EnumerateObject())
            if (!seen.Add(property.Name) || Array.IndexOf(allowed, property.Name) < 0) return false;
        return true;
    }

    /// <summary>One required text field of at most 256 characters.</summary>
    internal static bool Text(JsonElement row, string field, out string? value) => Text(row, field, 256, out value);

    /// <summary>One required text field of at most <paramref name="maximum"/> characters: not empty, not padded,
    /// and no control character, so a value this reads is one a diagnostic can print.</summary>
    internal static bool Text(JsonElement row, string field, int maximum, out string? value)
    {
        value = null;
        if (!row.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.String) return false;
        var text = property.GetString();
        if (text == null || text.Length == 0 || text.Length > maximum || text.Trim() != text) return false;
        foreach (var character in text) if (character < 32 || character == 127) return false;
        value = text;
        return true;
    }

    /// <summary>One required array of exactly <paramref name="count"/> finite numbers inside a range.</summary>
    internal static bool Numbers(JsonElement row, string field, int count, double minimum, double maximum, out double[]? values)
    {
        values = null;
        if (!row.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() != count) return false;
        var parsed = new double[count];
        var index = 0;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number) || !double.IsFinite(number)
                || number < minimum || number > maximum) return false;
            parsed[index++] = number;
        }
        values = parsed;
        return true;
    }

    /// <summary>One required finite number inside a range that is not an array.</summary>
    internal static bool Number(JsonElement row, string field, double minimum, double maximum, out double value)
    {
        value = 0;
        if (!row.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.Number
            || !property.TryGetDouble(out var parsed) || !double.IsFinite(parsed)
            || parsed < minimum || parsed > maximum) return false;
        value = parsed;
        return true;
    }

    /// <summary>One required zone coordinate: a JSON integer in the range the level itself addresses zones by. A
    /// fractional or negative coordinate names no zone, so it is refused at the document instead of being rounded
    /// into one that exists.</summary>
    internal static bool Coordinate(JsonElement row, string field, int minimum, int maximum, out int value)
    {
        value = 0;
        if (!row.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var parsed) || parsed < minimum || parsed > maximum) return false;
        value = parsed;
        return true;
    }

    /// <summary>The site's own zone identity grammar for one authored id: an alphanumeric first character and no
    /// whitespace, so the id a document carries is one a level's own address can be compared against.</summary>
    internal static bool AuthorId(string value)
    {
        if (value.Length == 0 || value.Length > 64 || !AsciiLetterOrDigit(value[0])) return false;
        foreach (var character in value)
            if (!(AsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-')) return false;
        return true;
    }

    /// <summary>A definition id: bounded, no control character and no path separator, because the id is what a
    /// resolver matches against the level's own object identity.</summary>
    internal static bool Identity(string value)
    {
        if (value.Length == 0 || value.Length > RoomLocator.MaximumRoomIdChars) return false;
        foreach (var character in value)
            if (character < 32 || character == 127 || char.IsWhiteSpace(character) || character is '/' or '\\') return false;
        return value.Trim() == value;
    }

    /// <summary>A complete, unchanged native prefab identity: an `Assets/` path with no separator of another kind
    /// and no empty, `.` or `..` segment. A prefab's display name is not this identity, which is why the whole path
    /// is carried and checked instead of being matched loosely.</summary>
    internal static bool Prefab(string value)
    {
        if (value.Length > RoomLocator.MaximumPrefabChars || !value.StartsWith("Assets/", StringComparison.Ordinal)) return false;
        foreach (var character in value) if (char.IsControl(character)) return false;
        return ForgeCore.PosixPath.IsRelative(value);
    }

    /// <summary>ASCII letter or digit. Spelled here rather than with `char.IsAsciiLetterOrDigit`, which is a
    /// .NET 7 member this package's `net6.0` target does not have.</summary>
    internal static bool AsciiLetterOrDigit(char character)
        => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}

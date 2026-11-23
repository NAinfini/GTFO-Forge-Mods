using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.Network;

/// <summary>
/// What turns one JSON value into another, so a change of a few rows travels as those rows. A patch is a form byte and its body:
/// <list type="bullet">
/// <item><c>0</c>: the value is unchanged;</item>
/// <item><c>1</c> value: the value is replaced (<see cref="WriteValue"/>);</item>
/// <item><c>2</c> count (name patch)…: an object that now has exactly these members, in this order, each patched against the
/// member of that name (a new member is a replacement);</item>
/// <item><c>3</c> count index… count (index value)…: an array that lost the rows at these places of the old array and then
/// gained these rows at these places of the new one, both ascending.</item>
/// </list>
/// Counts and places are 7-bit encoded, names are length-prefixed UTF-8. Rows have no identity of their own: a row that changed
/// is one row dropped and one added. The host writes a patch against the value it last sent and every receiver applies it to the
/// value it last took, so both hold the same rows in the same places. A patch that does not fit the value it is applied to is
/// refused whole (<see cref="Code"/>), never applied in part.
/// </summary>
internal static class StatePatch
{
    internal const string Code = "native-state-patch";
    /// <summary>How deep objects are compared member by member; below it a changed object is replaced.</summary>
    private const int MemberDepth = 2;
    private const byte Unchanged = 0, Replaced = 1, Members = 2, Rows = 3;

    internal static void Write(BinaryWriter writer, JsonElement before, JsonElement after, int depth = 0)
    {
        if (Same(before, after)) { writer.Write(Unchanged); return; }
        if (before.ValueKind == JsonValueKind.Object && after.ValueKind == JsonValueKind.Object && depth < MemberDepth)
        {
            writer.Write(Members);
            var count = 0;
            foreach (var _ in after.EnumerateObject()) count++;
            writer.Write7BitEncodedInt(count);
            foreach (var member in after.EnumerateObject())
            {
                writer.Write(member.Name);
                if (before.TryGetProperty(member.Name, out var held)) Write(writer, held, member.Value, depth + 1);
                else { writer.Write(Replaced); WriteValue(writer, member.Value); }
            }
        }
        else if (before.ValueKind == JsonValueKind.Array && after.ValueKind == JsonValueKind.Array) WriteRows(writer, before, after);
        else { writer.Write(Replaced); WriteValue(writer, after); }
    }

    /// <summary>One JSON value as its compact UTF-8 text behind a 7-bit length.</summary>
    internal static void WriteValue(BinaryWriter writer, JsonElement value)
    {
        var text = RawJson.Bytes(value);
        writer.Write7BitEncodedInt(text.Length);
        writer.Write(text);
    }

    /// <summary>The text <see cref="WriteValue"/> wrote; a length past the body's end is refused.</summary>
    internal static byte[] ReadValue(BinaryReader reader)
    {
        var length = reader.Read7BitEncodedInt();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new FormatException("value length out of range");
        return reader.ReadBytes(length);
    }

    private static bool Same(JsonElement a, JsonElement b) => RawJson.Bytes(a).SequenceEqual(RawJson.Bytes(b));

    private static int Hash(JsonElement row)
    {
        var hash = new HashCode();
        hash.AddBytes(RawJson.Bytes(row));
        return hash.ToHashCode();
    }

    private static void WriteRows(BinaryWriter writer, JsonElement before, JsonElement after)
    {
        var held = RowsOf(before);
        var rows = RowsOf(after);
        // Rows that stand where they stood, at either end, are compared in place; only what lies between is matched by text.
        int shared = Math.Min(held.Count, rows.Count), head = 0, tail = 0;
        while (head < shared && Same(held[head], rows[head])) head++;
        while (tail < shared - head && Same(held[held.Count - 1 - tail], rows[rows.Count - 1 - tail])) tail++;
        // Places of the earlier rows by the hash of their text, ascending; a candidate is taken only when its text is the same.
        var places = new Dictionary<int, List<int>>();
        for (var index = head; index < held.Count - tail; index++)
        {
            var hash = Hash(held[index]);
            if (!places.TryGetValue(hash, out var list)) places.Add(hash, list = new List<int>());
            list.Add(index);
        }
        // A kept row keeps its order among the kept ones, so each match must lie past the one before it; an equal row before
        // it can never be kept any more and is dropped from its list.
        var kept = new bool[held.Count];
        var added = new List<int>();
        var last = -1;
        for (var index = head; index < rows.Count - tail; index++)
        {
            var found = -1;
            if (places.TryGetValue(Hash(rows[index]), out var list))
                for (var at = 0; at < list.Count; at++)
                {
                    if (!Same(held[list[at]], rows[index])) continue;
                    var place = list[at];
                    list.RemoveAt(at--);
                    if (place > last) { found = place; break; }
                }
            if (found > last) { kept[found] = true; last = found; }
            else added.Add(index);
        }
        writer.Write(Rows);
        var dropped = 0;
        for (var index = head; index < held.Count - tail; index++) if (!kept[index]) dropped++;
        writer.Write7BitEncodedInt(dropped);
        for (var index = head; index < held.Count - tail; index++) if (!kept[index]) writer.Write7BitEncodedInt(index);
        writer.Write7BitEncodedInt(added.Count);
        foreach (var index in added) { writer.Write7BitEncodedInt(index); WriteValue(writer, rows[index]); }
    }

    private static List<JsonElement> RowsOf(JsonElement array)
    {
        var rows = new List<JsonElement>(array.GetArrayLength());
        foreach (var row in array.EnumerateArray()) rows.Add(row);
        return rows;
    }

    /// <summary>The value the patch <paramref name="reader"/> stands at makes of <paramref name="before"/>.</summary>
    internal static JsonElement Apply(JsonElement before, BinaryReader reader)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        try
        {
            using (var writer = new Utf8JsonWriter(buffer)) Apply(writer, before, reader);
            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException or FormatException or IOException or ArgumentException)
        { throw Refused(error.GetType().Name); }
    }

    private static RuntimeContractException Refused(string detail) => new(Code, "A state patch does not fit the state it names: " + detail);

    /// <summary>A count no longer than the bytes left: every counted item takes at least one byte.</summary>
    private static int Count(BinaryReader reader)
    {
        var count = reader.Read7BitEncodedInt();
        if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position) throw Refused("count");
        return count;
    }

    private static void Apply(Utf8JsonWriter writer, JsonElement before, BinaryReader reader)
    {
        switch (reader.ReadByte())
        {
            case Unchanged: before.WriteTo(writer); return;
            case Replaced: writer.WriteRawValue(ReadValue(reader)); return;
            case Members: ApplyMembers(writer, before, reader); return;
            case Rows: ApplyRows(writer, before, reader); return;
        }
        throw Refused("unknown form");
    }

    private static void ApplyMembers(Utf8JsonWriter writer, JsonElement before, BinaryReader reader)
    {
        if (before.ValueKind != JsonValueKind.Object) throw Refused("members");
        var count = Count(reader);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        writer.WriteStartObject();
        for (var index = 0; index < count; index++)
        {
            var name = reader.ReadString();
            if (!seen.Add(name)) throw Refused("member repeated");
            writer.WritePropertyName(name);
            if (before.TryGetProperty(name, out var held)) Apply(writer, held, reader);
            else if (reader.ReadByte() == Replaced) writer.WriteRawValue(ReadValue(reader));
            else throw Refused("member missing");
        }
        writer.WriteEndObject();
    }

    private static void ApplyRows(Utf8JsonWriter writer, JsonElement before, BinaryReader reader)
    {
        if (before.ValueKind != JsonValueKind.Array) throw Refused("rows");
        var held = RowsOf(before);
        // A row is a held element or the text of an added one.
        var rows = new List<(JsonElement Held, byte[]? Added)>(held.Count);
        int next = 0, last = -1;
        for (var count = Count(reader); count > 0; count--)
        {
            var index = reader.Read7BitEncodedInt();
            if (index <= last || index >= held.Count) throw Refused("dropped row");
            while (next < index) rows.Add((held[next++], null));
            next = index + 1;
            last = index;
        }
        while (next < held.Count) rows.Add((held[next++], null));
        last = -1;
        for (var count = Count(reader); count > 0; count--)
        {
            var at = reader.Read7BitEncodedInt();
            if (at <= last || at > rows.Count) throw Refused("added row");
            rows.Insert(at, (default, ReadValue(reader)));
            last = at;
        }
        writer.WriteStartArray();
        foreach (var (row, added) in rows)
            if (added != null) writer.WriteRawValue(added);
            else row.WriteTo(writer);
        writer.WriteEndArray();
    }
}

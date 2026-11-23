using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap.Fields;

/// <summary>
/// The object one entry is addressed to: the level it belongs to, and — for every kind but a level — the object's
/// own address in the shape this module already writes for it. `Object` is the text the author wrote, so a
/// diagnostic names the target exactly as the document does.
/// </summary>
internal sealed record MapFieldTarget(MapFieldTargetKind Kind, MapLevelReference Level, string Object,
    int Dimension, int Layer, int Zone, string Key)
{
    /// <summary>The zone the object stands in (for a zone, the zone itself; for a door, the zone it guards).</summary>
    internal MapZoneKey ZoneKey => new(Dimension, Layer, Zone);

    public override string ToString() => Level + (Object.Length == 0 ? "" : " " + Object);
}

/// <summary>The three coordinates of one zone, as every map address spells them.</summary>
internal readonly record struct MapZoneKey(int Dimension, int Layer, int Zone);

/// <summary>One typed document value. Only the member its <see cref="Kind"/> names is meaningful; an enum value
/// is the member text for Enum and the native integer for NumericEnum. A `Vector3` and a number list read into
/// <see cref="Numbers"/> and a whole-number list into <see cref="Integers"/>.</summary>
internal readonly record struct MapFieldValue(MapFieldKind Kind, bool Boolean, long Integer, double Number, string? Text,
    IReadOnlyList<double>? Numbers = null, IReadOnlyList<long>? Integers = null);

/// <summary>One accepted document entry: the block, the target and the fields that read. A field that did not read
/// was reported and is absent here; the entry keeps the ones that did. <see cref="Room"/> is the room a
/// `ZoneObject` target stands in when the author placed it at a point, and null otherwise; it is not part of the
/// target's identity, so one object is one target whatever room it names.</summary>
internal sealed record MapFieldEntry(string Source, int Index, MapFieldBlock Block, MapFieldTarget Target,
    IReadOnlyDictionary<string, MapFieldValue> Fields, RoomLocator? Room = null);

/// <summary>
/// Reads the map editor's block field documents: `plugins/&lt;package&gt;/forge/map-fields/*.forge`, each
/// `{ "entries": [ { "block": "&lt;forge block id&gt;", "target": { "level": "&lt;rundown&gt;:&lt;tier&gt;:&lt;index&gt;",
/// "object": "&lt;address&gt;" }, "fields": { "&lt;path&gt;": &lt;value&gt; } } ] }`.
///
/// `object` is absent for a level entry, `gtfo.zone:&lt;d&gt;:&lt;l&gt;:&lt;z&gt;` for a zone, `door/&lt;d&gt;/&lt;l&gt;/&lt;z&gt;/security`
/// for a zone's entrance door, `terminal/…` and `generator/…` for those objects — the same spellings the rest of this
/// module writes. A weak door's key is a per-run instance serial, so it cannot be authored and is refused. A
/// `ZoneObject` target is the map editor's own object id in `object`, its zone in `zone` (`gtfo.zone:…`) and,
/// for an object placed at a point, the room locator in `room` that trigger zones and fixed items also carry;
/// its `Position` and `Rotation` fields are then the prefab root's Unity-local metres and Euler degrees in that room.
///
/// Every refusal names what it refused by id — the block, the field id, the target — and the entry or field is
/// left out; nothing is silently dropped. Accepted fields no applier writes are reported once per document, field
/// and object as `map.field-unapplied`.
/// </summary>
internal static class MapFieldDocuments
{
    /// <summary>Cap on one document, enforced on the opened stream.</summary>
    internal const int MaximumFileBytes = PublishedResources.MaximumFileBytes;

    /// <summary>Cap on the documents one package may carry, so a directory that grew without bound is refused
    /// as a set rather than read one file at a time.</summary>
    internal const int MaximumFilesPerPackage = 64;


    /// <summary>The accepted entries of one document's text. `relative` is the name every diagnostic carries.</summary>
    internal static IReadOnlyList<MapFieldEntry> Read(string relative, string text, Action<MapDiagnostic> report)
    {
        var entries = new List<MapFieldEntry>();
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException error) { report(Diagnostic(MapLogCodes.FieldDocument, relative, error.Message)); return entries; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                report(Diagnostic(MapLogCodes.FieldDocument, relative, "The document is not an object."));
                return entries;
            }
            if (!DocumentReading.Known(root, out var unknown, "entries"))
            {
                report(Diagnostic(MapLogCodes.FieldDocument, relative, "Unknown top-level member `" + unknown + "`."));
                return entries;
            }
            if (!root.TryGetProperty("entries", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                report(Diagnostic(MapLogCodes.FieldDocument, relative, "`entries` must be an array."));
                return entries;
            }
            int index = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (ReadEntry(relative, index, row, report) is { } entry) entries.Add(entry);
                index++;
            }
        }
        return entries;
    }

    private static MapFieldEntry? ReadEntry(string relative, int index, JsonElement row, Action<MapDiagnostic> report)
    {
        string where = relative + " entry=" + index.ToString(CultureInfo.InvariantCulture);
        if (row.ValueKind != JsonValueKind.Object)
        {
            report(Diagnostic(MapLogCodes.FieldEntry, where, "An entry must be an object."));
            return null;
        }
        if (!DocumentReading.Known(row, out var unknown, "block", "target", "fields"))
        {
            report(Diagnostic(MapLogCodes.FieldEntry, where, "Unknown entry member `" + unknown + "`."));
            return null;
        }
        if (!DocumentReading.Text(row, "block", out var blockId) || blockId == null)
        {
            report(Diagnostic(MapLogCodes.FieldEntry, where, "`block` must be a non-empty string."));
            return null;
        }
        if (!MapFieldSchema.Blocks.TryGetValue(blockId, out var block))
        {
            report(Diagnostic(MapLogCodes.FieldBlockUnknown, where + " block=" + blockId,
                "This build reads no block of that id."));
            return null;
        }
        where += " block=" + blockId;
        if (!row.TryGetProperty("target", out var targetElement))
        {
            report(Diagnostic(MapLogCodes.FieldTarget, where, "`target` is missing."));
            return null;
        }
        if (ReadTarget(targetElement, block.Target, out var room, out var reason) is not { } target)
        {
            report(Diagnostic(MapLogCodes.FieldTarget, where, reason ?? "`target` does not read."));
            return null;
        }
        if (!row.TryGetProperty("fields", out var fieldsElement) || fieldsElement.ValueKind != JsonValueKind.Object)
        {
            report(Diagnostic(MapLogCodes.FieldEntry, where, "`fields` must be an object."));
            return null;
        }
        var fields = new Dictionary<string, MapFieldValue>(StringComparer.Ordinal);
        foreach (var property in fieldsElement.EnumerateObject())
        {
            var path = block.Paths.FirstOrDefault(p => p.Path == property.Name);
            if (path == null)
            {
                report(Diagnostic(MapLogCodes.FieldUnknown, where + " field=" + block.Id(property.Name),
                    "Block " + blockId + " has no field path `" + property.Name + "`."));
                continue;
            }
            if (!ReadValue(property.Value, path, out var value, out var expected))
            {
                report(Diagnostic(MapLogCodes.FieldValue, where + " field=" + block.Id(path.Path), expected));
                continue;
            }
            fields[path.Path] = value;
        }
        return new MapFieldEntry(relative, index, block, target, fields, room);
    }

    private static MapFieldTarget? ReadTarget(JsonElement element, MapFieldTargetKind kind, out RoomLocator? room, out string? reason)
    {
        reason = null;
        room = null;
        if (element.ValueKind != JsonValueKind.Object) { reason = "`target` must be an object."; return null; }
        bool known = kind == MapFieldTargetKind.ZoneObject
            ? DocumentReading.Known(element, out var unknown, "level", "object", "zone", "room")
            : DocumentReading.Known(element, out unknown, "level", "object");
        if (!known)
        {
            reason = "Unknown target member `" + unknown + "`.";
            return null;
        }
        if (!element.TryGetProperty("level", out var levelElement) || levelElement.ValueKind != JsonValueKind.String
            || MapLevelReference.TryParse(levelElement.GetString()) is not { } level)
        {
            reason = "`target.level` must be `<rundown>:<tier A-E>:<index>`.";
            return null;
        }
        string? text = null;
        if (element.TryGetProperty("object", out var objectElement))
        {
            if (objectElement.ValueKind != JsonValueKind.String) { reason = "`target.object` must be a string."; return null; }
            text = objectElement.GetString();
        }
        if (kind == MapFieldTargetKind.Level)
        {
            if (text != null) { reason = "This block is addressed to a level; `target.object` " + text + " is not allowed."; return null; }
            return new MapFieldTarget(kind, level, "", 0, 0, 0, "");
        }
        if (text == null) { reason = "This block is addressed to a " + Spell(kind) + "; `target.object` is missing."; return null; }
        if (kind == MapFieldTargetKind.ZoneObject) return ReadZoneObject(element, level, text, out room, out reason);
        MapObjectReference? address = kind switch
        {
            MapFieldTargetKind.Zone => ZoneAddress(text),
            MapFieldTargetKind.Door => MapObjectDoorAddress.TryParse(text) is { Key: MapObjectDoorAddress.Security } door ? door : null,
            MapFieldTargetKind.Terminal => MapObjectTerminalAddress.TryParse(text),
            MapFieldTargetKind.Generator => MapObjectGeneratorAddress.TryParse(text),
            _ => null
        };
        if (address == null
            || !int.TryParse(address.Dimension, NumberStyles.None, CultureInfo.InvariantCulture, out int dimension)
            || !int.TryParse(address.Layer, NumberStyles.None, CultureInfo.InvariantCulture, out int layer)
            || !int.TryParse(address.Zone, NumberStyles.None, CultureInfo.InvariantCulture, out int zone))
        {
            reason = "`" + text + "` is not a " + Spell(kind) + " address" + (kind == MapFieldTargetKind.Door
                ? " (`door/<d>/<l>/<z>/security`; a weak door's key is a per-run serial and cannot be authored)." : ".");
            return null;
        }
        return new MapFieldTarget(kind, level, text, dimension, layer, zone, address.Key);
    }

    /// <summary>A map editor object: its own author id, the zone it belongs to and, when it was placed at a point,
    /// the room locator. A room locator that names another zone than `zone` is refused: the object would be written
    /// into one zone and positioned in another.</summary>
    private static MapFieldTarget? ReadZoneObject(JsonElement element, MapLevelReference level, string id, out RoomLocator? room, out string? reason)
    {
        room = null;
        reason = null;
        if (!DocumentReading.AuthorId(id)) { reason = "`target.object` " + id + " is not a map editor object id."; return null; }
        if (!element.TryGetProperty("zone", out var zoneElement) || zoneElement.ValueKind != JsonValueKind.String
            || ZoneAddress(zoneElement.GetString() ?? "") is not { } zone
            || !int.TryParse(zone.Dimension, NumberStyles.None, CultureInfo.InvariantCulture, out int dimension)
            || !int.TryParse(zone.Layer, NumberStyles.None, CultureInfo.InvariantCulture, out int layer)
            || !int.TryParse(zone.Zone, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            reason = "`target.zone` must be `gtfo.zone:<d>:<l>:<z>`.";
            return null;
        }
        if (element.TryGetProperty("room", out var roomElement))
        {
            if (!RoomLocator.TryParse(roomElement, out room, out _, out var roomReason))
            {
                reason = "`target.room`: " + roomReason;
                return null;
            }
            if (room!.Dimension != dimension || room.Layer != layer || room.LocalIndex != index)
            {
                reason = "`target.room` stands in another zone than `target.zone`.";
                room = null;
                return null;
            }
        }
        return new MapFieldTarget(MapFieldTargetKind.ZoneObject, level, id, dimension, layer, index, "");
    }

    /// <summary>The zone spelling the runtime's zone entities carry, `gtfo.zone:&lt;d&gt;:&lt;l&gt;:&lt;z&gt;`, read into the
    /// same reference shape as the object addresses so the coordinates are parsed one way.</summary>
    private static MapObjectReference? ZoneAddress(string text)
    {
        const string prefix = "gtfo.zone:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = text[prefix.Length..].Split(':');
        if (parts.Length != 3 || parts.Any(part => !MapObjectReference.IsCoordinate(part))) return null;
        return new MapObjectReference("zone", parts[0], parts[1], parts[2], "");
    }

    private static bool ReadValue(JsonElement element, MapFieldPath path, out MapFieldValue value, out string expected)
    {
        value = default;
        switch (path.Kind)
        {
            case MapFieldKind.Boolean:
                expected = "Expected true or false.";
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                value = new MapFieldValue(path.Kind, element.GetBoolean(), 0, 0, null);
                return true;
            case MapFieldKind.Integer:
                expected = "Expected a whole number.";
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long integer)) return false;
                value = new MapFieldValue(path.Kind, false, integer, 0, null);
                return true;
            case MapFieldKind.Number:
                expected = "Expected a finite number.";
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out double number) || !double.IsFinite(number)) return false;
                value = new MapFieldValue(path.Kind, false, 0, number, null);
                return true;
            case MapFieldKind.Text:
                expected = "Expected a string of at most 256 characters.";
                if (element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: <= 256 } text) return false;
                value = new MapFieldValue(path.Kind, false, 0, 0, text);
                return true;
            case MapFieldKind.NumericEnum:
                expected = "Expected a native integer enum value: " + string.Join(", ", path.Members) + ".";
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long enumValue)
                    || !path.Members.Any(candidate => candidate.StartsWith(
                        enumValue.ToString(CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal))) return false;
                value = new MapFieldValue(path.Kind, false, enumValue, 0, null);
                return true;
            case MapFieldKind.Enum:
                expected = "Expected one of: " + string.Join(", ", path.Members) + ".";
                if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } member
                    || !path.Members.Contains(member, StringComparer.Ordinal)) return false;
                value = new MapFieldValue(path.Kind, false, 0, 0, member);
                return true;
            case MapFieldKind.Vector3:
                expected = "Expected three finite numbers.";
                if (!Items(element, 3, out var axes) || !Finite(axes, out var vector)) return false;
                value = new MapFieldValue(path.Kind, false, 0, 0, null, Numbers: vector);
                return true;
            case MapFieldKind.NumberList:
                expected = ListExpectation(path, "finite numbers");
                if (!Items(element, path.Length, out var numberItems) || !Finite(numberItems, out var numbers)) return false;
                value = new MapFieldValue(path.Kind, false, 0, 0, null, Numbers: numbers);
                return true;
            case MapFieldKind.IntegerList:
                expected = ListExpectation(path, "whole numbers");
                if (!Items(element, path.Length, out var integerItems)) return false;
                var integers = new long[integerItems.Count];
                for (int i = 0; i < integers.Length; i++)
                    if (integerItems[i].ValueKind != JsonValueKind.Number || !integerItems[i].TryGetInt64(out integers[i])) return false;
                value = new MapFieldValue(path.Kind, false, 0, 0, null, Integers: integers);
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path.Kind, "Unknown map field kind.");

        }
    }

    /// <summary>The items of one JSON array: exactly <paramref name="length"/> of them when it is positive, and any number otherwise.</summary>
    private static bool Items(JsonElement element, int length, out IReadOnlyList<JsonElement> items)
    {
        items = Array.Empty<JsonElement>();
        if (element.ValueKind != JsonValueKind.Array) return false;
        int count = element.GetArrayLength();
        if (length > 0 && count != length) return false;
        items = element.EnumerateArray().ToArray();
        return true;
    }

    private static bool Finite(IReadOnlyList<JsonElement> items, out double[] numbers)
    {
        numbers = new double[items.Count];
        for (int i = 0; i < numbers.Length; i++)
            if (items[i].ValueKind != JsonValueKind.Number || !items[i].TryGetDouble(out numbers[i]) || !double.IsFinite(numbers[i])) return false;
        return true;
    }

    private static string ListExpectation(MapFieldPath path, string items)
        => "Expected " + (path.Length > 0 ? "exactly " + path.Length : "any number of") + " " + items + ".";

    /// <summary>Refuses a second value for a field one target already has — the first document in ordinal order
    /// keeps it — and reports every accepted field no applier writes, once per document, field and object; a map
    /// editor object's line names the object's own id, which is how a log line points back at it.</summary>
    internal static IReadOnlyList<MapFieldEntry> Merge(IEnumerable<MapFieldEntry> entries, Action<MapDiagnostic> report)
    {
        var merged = new List<MapFieldEntry>();
        var owners = new Dictionary<(string Block, MapFieldTarget Target, string Path), string>();
        var unapplied = new HashSet<(string Source, string Field)>();
        foreach (var entry in entries)
        {
            var kept = new Dictionary<string, MapFieldValue>(StringComparer.Ordinal);
            foreach (var (path, value) in entry.Fields)
            {
                string field = entry.Block.Id(path);
                string where = entry.Source + " entry=" + entry.Index.ToString(CultureInfo.InvariantCulture) + " field=" + field;
                if (!owners.TryAdd((entry.Block.Block, entry.Target, path), where))
                {
                    report(Diagnostic(MapLogCodes.FieldDuplicate, where,
                        "Target " + entry.Target + " already has this field from " + owners[(entry.Block.Block, entry.Target, path)] + "."));
                    continue;
                }
                kept[path] = value;
                if (entry.Block.Paths.First(p => p.Path == path).UnappliedIn(entry.Block) is { } reason && unapplied.Add((entry.Source, field + " " + entry.Target.Object)))
                    report(new MapDiagnostic(MapLogCodes.FieldUnapplied, "field=" + field + " file=" + entry.Source
                        + (entry.Target.Kind == MapFieldTargetKind.ZoneObject ? " object=" + entry.Target.Object : "") + " reason=" + reason,
                        entry.Source));
            }
            if (kept.Count != 0) merged.Add(entry with { Fields = kept });
        }
        return merged;
    }

    private static string Spell(MapFieldTargetKind kind) => kind switch
    {
        MapFieldTargetKind.Level => "level",
        MapFieldTargetKind.Zone => "zone",
        MapFieldTargetKind.Door => "door",
        MapFieldTargetKind.Terminal => "terminal",
        MapFieldTargetKind.Generator => "generator",
        _ => "map editor object"
    };

    /// <summary>One refusal: the code, the document (and entry, block, field) it came from, and why. `where` starts
    /// with the document's BepInEx-relative path, so the document is what comes before its first ` entry=`.</summary>
    internal static MapDiagnostic Diagnostic(string code, string where, string reason)
    {
        var entry = where.IndexOf(" entry=", StringComparison.Ordinal);
        return new(code, "file=" + where + ": " + reason, entry < 0 ? where : where[..entry]);
    }
}

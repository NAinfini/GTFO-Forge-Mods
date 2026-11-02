using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ForgeOrdnance;

/// <summary>When a developer drop arrives: once when the level starts, or each time a level event of type
/// <see cref="DeveloperDrops.EventType"/> names it.</summary>
public enum DropWhen
{
    LevelStart,
    Event
}

/// <summary>A point in a zone: a world position (<see cref="Area"/> negative), or the centre of the zone's area
/// <see cref="Area"/> (0-based, in the zone's own area order) moved by the offset.</summary>
public sealed record DropPoint(int Area, float X, float Y, float Z)
{
    internal bool IsWorld => Area < 0;
}

/// <summary>Where a developer drop lands, written like a vanilla level event: <c>DimensionIndex</c>, <c>Layer</c>, <c>LocalIndex</c>,
/// then the point.</summary>
public sealed record DropWhere(int Dimension, int Layer, int LocalIndex, DropPoint Point)
{
    public override string ToString()
        => "dimension " + Dimension + ", layer " + Layer + ", zone " + LocalIndex + ", "
           + (Point.IsWorld ? "position " + DeveloperDrops.Format(Point.X, Point.Y, Point.Z) : "area " + Point.Area + " offset " + DeveloperDrops.Format(Point.X, Point.Y, Point.Z));
}

/// <summary>One entry of a level's <c>Drops</c> (<c>Name</c> is unique within a level). <see cref="Where"/> is null only for an event drop that lands where its event says.</summary>
public sealed record OrdnanceLevelDrop(string Name, string Ordnance, DropWhen When, OrdnanceArrivalMode Arrival, double Delay, float Yaw, DropWhere? Where);

/// <summary>What <see cref="DropPlacement.Resolve"/> needs from the level being played.</summary>
internal interface IDropLevel
{
    bool HasDimension(int dimension);
    /// <summary>False when the dimension has no such zone; otherwise its number of areas.</summary>
    bool TryZone(int dimension, int layer, int localIndex, out int areaCount);
    /// <summary>The centre of area <paramref name="area"/> (in range) of a zone <see cref="TryZone"/> found.</summary>
    Vec3 AreaCentre(int dimension, int layer, int localIndex, int area);
}

/// <summary>The <c>Drops</c> lists of <c>Settings.json</c>: parsing, checking against the loaded call-ins, and the level event that fires them.</summary>
internal static class DeveloperDrops
{
    /// <summary>The level event type (<c>WardenObjectiveEventData.Type</c>) that fires a developer drop; its
    /// <c>WorldEventObjectFilter</c> names the drop.</summary>
    internal const int EventType = 75000;
    internal const double MaxDelay = 3600;
    private const int MaxLayer = 2;
    private static readonly string[] EntryFields = { "Name", "Ordnance", "When", "Arrival", "Delay", "Yaw", "Where" };
    private static readonly string[] WhereFields = { "DimensionIndex", "Layer", "LocalIndex", "Position", "Area", "Offset" };
    private static readonly string[] VectorFields = { "x", "y", "z" };

    /// <summary>The call number of a level-start drop: a hash of its name in the upper half of the number range, so a drop scheduled
    /// again after a checkpoint reload or a host change is the same call on every peer, and never meets a number of
    /// <see cref="NextCall"/> (lower half).</summary>
    internal static uint LevelStartCall(string dropName) => ForgeCore.Fnv.Hash32("LevelStart:" + dropName) | 0x80000000u;

    /// <summary>The first call number of a host: the clock, kept in the lower half.</summary>
    internal static uint SeedCalls(ulong clock) => (uint)(clock & 0x7FFFFFFF) | 1u;

    /// <summary>The next call number of an event drop or a placement, staying in the lower half and never 0.</summary>
    internal static uint NextCall(ref uint next)
    {
        var call = next;
        next = (next + 1) & 0x7FFFFFFF;
        if (next == 0) next = 1;
        return call;
    }

    private static bool NumberTaken(Dictionary<uint, string> numbers, string name, string at, List<string> errors)
    {
        var number = LevelStartCall(name);
        if (numbers.TryGetValue(number, out var other))
        {
            errors.Add(at + " Name: '" + name + "' has the same call number as '" + other + "'; rename one.");
            return true;
        }
        numbers[number] = name;
        return false;
    }

    internal static string Format(float x, float y, float z)
        => "(" + x.ToString("0.##", CultureInfo.InvariantCulture) + ", " + y.ToString("0.##", CultureInfo.InvariantCulture) + ", " + z.ToString("0.##", CultureInfo.InvariantCulture) + ")";

    /// <summary>Reads a <c>Drops</c> array. Every problem is added to <paramref name="errors"/> by entry and field.</summary>
    internal static IReadOnlyList<OrdnanceLevelDrop> Parse(JsonElement array, string where, List<string> errors)
    {
        var drops = new List<OrdnanceLevelDrop>();
        if (array.ValueKind != JsonValueKind.Array) { errors.Add(where + " Drops: must be an array of drops."); return drops; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var numbers = new Dictionary<uint, string>();
        var index = 0;
        foreach (var entry in array.EnumerateArray())
        {
            var at = where + " Drops[" + index++ + "]";
            var before = errors.Count;
            var drop = ParseEntry(entry, at, errors);
            if (drop == null || errors.Count != before) continue;
            if (!names.Add(drop.Name)) { errors.Add(at + " Name: '" + drop.Name + "' is used by another drop of this list."); continue; }
            if (NumberTaken(numbers, drop.Name, at, errors)) continue;
            drops.Add(drop);
        }
        return drops;
    }

    private static OrdnanceLevelDrop? ParseEntry(JsonElement entry, string at, List<string> errors)
    {
        if (entry.ValueKind != JsonValueKind.Object) { errors.Add(at + ": must be an object."); return null; }
        Unknown(entry, EntryFields, at, errors);
        var name = Text(entry, "Name", at, errors);
        var ordnance = Text(entry, "Ordnance", at, errors);
        var whenText = Text(entry, "When", at, errors);
        var when = whenText == "Event" ? DropWhen.Event : DropWhen.LevelStart;
        if (whenText is not (null or "LevelStart" or "Event")) errors.Add(at + " When: must be \"LevelStart\" or \"Event\".");
        var arrival = OrdnanceArrivalMode.Drop;
        if (entry.TryGetProperty("Arrival", out var arrivalElement)
            && (arrivalElement.ValueKind != JsonValueKind.String || !ArrivalPlan.TryParse(arrivalElement.GetString(), out arrival)))
            errors.Add(at + " Arrival: must be \"Drop\" or \"Direct\".");
        var delay = 0.0;
        if (entry.TryGetProperty("Delay", out var delayElement))
        {
            if (when == DropWhen.Event) errors.Add(at + " Delay: an event drop waits for its event's own Delay; remove this field.");
            else if (!Number(delayElement, 0, MaxDelay, out delay)) errors.Add(at + " Delay: must be seconds 0.." + MaxDelay + ".");
        }
        var yaw = 0.0;
        if (entry.TryGetProperty("Yaw", out var yawElement) && !Number(yawElement, 0, 360, out yaw)) errors.Add(at + " Yaw: must be degrees 0..360.");
        DropWhere? place = null;
        if (entry.TryGetProperty("Where", out var whereElement)) place = ParseWhere(whereElement, at + " Where", errors);
        else if (when == DropWhen.LevelStart) errors.Add(at + " Where: a level-start drop needs a place.");
        if (name == null || ordnance == null || whenText == null) return null;
        return new OrdnanceLevelDrop(name, ordnance, when, arrival, delay, (float)yaw, place);
    }

    private static DropWhere? ParseWhere(JsonElement element, string at, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object) { errors.Add(at + ": must be an object."); return null; }
        Unknown(element, WhereFields, at, errors);
        var dimension = Whole(element, "DimensionIndex", 0, 255, at, errors);
        var layer = Whole(element, "Layer", 0, MaxLayer, at, errors);
        var zone = Whole(element, "LocalIndex", 0, 255, at, errors);
        var hasPosition = element.TryGetProperty("Position", out var position);
        var hasArea = element.TryGetProperty("Area", out var areaElement);
        var hasOffset = element.TryGetProperty("Offset", out var offset);
        DropPoint? point = null;
        if (hasPosition == hasArea) errors.Add(at + ": give either Position (a world point) or Area (an area of the zone, with an optional Offset).");
        else if (hasPosition)
        {
            if (hasOffset) errors.Add(at + " Offset: only goes with Area.");
            if (Vector(position, at + " Position", errors) is { } p) point = new DropPoint(-1, p.X, p.Y, p.Z);
        }
        else
        {
            var area = -1;
            if (areaElement.ValueKind == JsonValueKind.Number && areaElement.TryGetInt32(out var value) && value >= 0) area = value;
            else errors.Add(at + " Area: must be a whole number from 0 (the zone's first area).");
            var shift = hasOffset ? Vector(offset, at + " Offset", errors) : new Vec3(0, 0, 0);
            if (area >= 0 && shift is { } s) point = new DropPoint(area, s.X, s.Y, s.Z);
        }
        return dimension < 0 || layer < 0 || zone < 0 || point == null ? null : new DropWhere(dimension, layer, zone, point);
    }

    /// <summary>Reports every field of drops a plugin built in code that a <c>Drops</c> array would have refused.</summary>
    internal static void Check(IReadOnlyList<OrdnanceLevelDrop> drops, string where, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var numbers = new Dictionary<uint, string>();
        for (var i = 0; i < drops.Count; i++)
        {
            var drop = drops[i];
            var at = where + " Drops[" + i + "]";
            if (string.IsNullOrWhiteSpace(drop.Name)) errors.Add(at + " Name: must be a non-empty string.");
            else if (!names.Add(drop.Name)) errors.Add(at + " Name: '" + drop.Name + "' is used by another drop of this list.");
            else NumberTaken(numbers, drop.Name, at, errors);
            if (string.IsNullOrWhiteSpace(drop.Ordnance)) errors.Add(at + " Ordnance: must be a non-empty string.");
            if (!Enum.IsDefined(drop.When)) errors.Add(at + " When: must be LevelStart or Event.");
            if (!Enum.IsDefined(drop.Arrival)) errors.Add(at + " Arrival: must be Drop or Direct.");
            if (drop.When == DropWhen.Event ? drop.Delay != 0 : !double.IsFinite(drop.Delay) || drop.Delay < 0 || drop.Delay > MaxDelay)
                errors.Add(at + " Delay: " + (drop.When == DropWhen.Event ? "an event drop waits for its event's own Delay; leave it 0." : "must be seconds 0.." + MaxDelay + "."));
            if (!float.IsFinite(drop.Yaw) || drop.Yaw < 0 || drop.Yaw > 360) errors.Add(at + " Yaw: must be degrees 0..360.");
            if (drop.Where == null)
            {
                if (drop.When == DropWhen.LevelStart) errors.Add(at + " Where: a level-start drop needs a place.");
                continue;
            }
            var place = drop.Where;
            if (place.Dimension is < 0 or > 255 || place.LocalIndex is < 0 or > 255 || place.Layer is < 0 or > MaxLayer || place.Point == null
                || !float.IsFinite(place.Point.X) || !float.IsFinite(place.Point.Y) || !float.IsFinite(place.Point.Z))
                errors.Add(at + " Where: dimension 0..255, layer 0.." + MaxLayer + ", zone 0..255 and a finite point are required.");
        }
    }

    /// <summary>The drops whose call-in is loaded and can arrive as asked; each one that cannot is reported and left out.
    /// <paramref name="supportsDirect"/> tells whether a payload type can arrive without its vehicle.</summary>
    internal static IReadOnlyList<OrdnanceLevelDrop> Usable(IReadOnlyList<OrdnanceLevelDrop> drops, DefinitionSet set, Func<string, bool> supportsDirect, string where, List<string> errors)
    {
        var usable = new List<OrdnanceLevelDrop>(drops.Count);
        foreach (var drop in drops)
        {
            if (!set.TryGetById(drop.Ordnance, out var definition))
            {
                errors.Add(where + " drop '" + drop.Name + "' Ordnance: '" + drop.Ordnance + "' is not a loaded call-in; this drop is skipped.");
                continue;
            }
            if (drop.Arrival == OrdnanceArrivalMode.Direct && !supportsDirect(definition.PayloadType))
            {
                errors.Add(where + " drop '" + drop.Name + "' Arrival: '" + drop.Ordnance + "' (payload " + definition.PayloadType + ") cannot arrive Direct; this drop is skipped.");
                continue;
            }
            usable.Add(drop);
        }
        return usable;
    }

    /// <summary>Whether one vanilla event is a developer drop that fires now, by the game's own rule for one event: its trigger
    /// matches (or triggers are ignored) and, inside a running duration, its delay is still ahead. <paramref name="wait"/> is the
    /// time left until it arrives.</summary>
    internal static bool Fires(int type, int eventTrigger, int trigger, bool ignoreTrigger, float delay, float currentDuration, out float wait)
    {
        wait = 0f;
        if (type != EventType) return false;
        if (!ignoreTrigger && eventTrigger != trigger) return false;
        if (currentDuration != 0f && delay <= currentDuration) return false;
        wait = Math.Max(0f, delay - currentDuration);
        return true;
    }

    private static void Unknown(JsonElement owner, string[] known, string at, List<string> errors)
    {
        foreach (var member in owner.EnumerateObject())
            if (Array.IndexOf(known, member.Name) < 0) errors.Add(at + " " + member.Name + ": unknown field.");
    }

    private static string? Text(JsonElement owner, string field, string at, List<string> errors)
    {
        if (owner.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
            return element.GetString()!;
        errors.Add(at + " " + field + ": " + (owner.TryGetProperty(field, out _) ? "must be a non-empty string." : "is required."));
        return null;
    }

    private static int Whole(JsonElement owner, string field, int min, int max, string at, List<string> errors)
    {
        if (!owner.TryGetProperty(field, out var element)) { errors.Add(at + " " + field + ": is required."); return -1; }
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value >= min && value <= max) return value;
        errors.Add(at + " " + field + ": must be a whole number " + min + ".." + max + ".");
        return -1;
    }

    private static bool Number(JsonElement element, double min, double max, out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var read) || !double.IsFinite(read) || read < min || read > max) return false;
        value = read;
        return true;
    }

    private static Vec3? Vector(JsonElement element, string at, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object) { errors.Add(at + ": must be an object { \"x\": .., \"y\": .., \"z\": .. }."); return null; }
        Unknown(element, VectorFields, at, errors);
        var parts = new float[3];
        for (var i = 0; i < 3; i++)
        {
            if (element.TryGetProperty(VectorFields[i], out var part) && part.ValueKind == JsonValueKind.Number && part.TryGetDouble(out var v) && double.IsFinite(v) && Math.Abs(v) <= 100000)
                parts[i] = (float)v;
            else { errors.Add(at + " " + VectorFields[i] + ": must be a finite number."); return null; }
        }
        return new Vec3(parts[0], parts[1], parts[2]);
    }
}

/// <summary>Turns a drop's place into a world point on the level being played.</summary>
internal static class DropPlacement
{
    /// <summary>Null with <paramref name="point"/> set, or the reason the place does not exist in this level.</summary>
    internal static string? Resolve(DropWhere where, IDropLevel level, out Vec3 point)
    {
        point = default;
        if (!level.HasDimension(where.Dimension)) return "dimension " + where.Dimension + " does not exist in this level";
        if (!level.TryZone(where.Dimension, where.Layer, where.LocalIndex, out var areas))
            return "there is no zone " + where.LocalIndex + " in layer " + where.Layer + " of dimension " + where.Dimension;
        var p = where.Point;
        if (p.IsWorld) { point = new Vec3(p.X, p.Y, p.Z); return null; }
        if (p.Area >= areas) return "zone " + where.LocalIndex + " (layer " + where.Layer + ", dimension " + where.Dimension + ") has " + areas + " area(s); Area " + p.Area + " does not exist";
        var centre = level.AreaCentre(where.Dimension, where.Layer, where.LocalIndex, p.Area);
        point = new Vec3(centre.X + p.X, centre.Y + p.Y, centre.Z + p.Z);
        return null;
    }
}

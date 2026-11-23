using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The authored trigger-zone shapes. The site's object editor writes one of these names in the
/// `shape` field, so the spelling here is the wire spelling.</summary>
internal enum TriggerZoneShape { Box, Sphere, Capsule, Cylinder }

/// <summary>Who a zone reacts to. The site's editor writes it as `who`; `both` is the value a zone that names
/// nobody carries, which is the only default that cannot hide a target.</summary>
internal enum TriggerZoneWho { Player, Enemy, Both }

/// <summary>
/// One authored trigger zone, read from a package's own exported map data and nothing else: an oriented volume
/// placed in a room, the level it belongs to, who it reacts to, and whether it also blocks players.
///
/// A zone is one map object of this provider: its object data is this record, its address is
/// <see cref="TriggerZoneAddress"/>, and the two facts it publishes are the map-object half's own, so a plan is
/// hung on the zone it is about rather than on a volume id a second mechanism would have to match.
///
/// The volume is the author's own object, not a native collider read back; its optional appearance is drawn by Forge. Containment is closed — a position exactly on
/// the boundary is inside — and every measurement is a world metre. The orientation is a unit quaternion in
/// `x y z w` order, the order the site's own transform writes, and a point is tested in the zone's local frame so
/// a rotated box, capsule or sphere is the same object the author placed.
///
/// The document's `position` and `rotation` are the room's local coordinates, because that is the only coordinate
/// system the site's editor can draw in. <see cref="Placed"/> says which pose this instance carries: an authored
/// zone is not judged, and <see cref="PlacedIn"/> is the one conversion into the world pose the tick, the
/// containment test and the blocking bodies all use.
/// </summary>
internal sealed class TriggerZone
{
    /// <summary>Rotated volume height above its centre; both label renderers use this exact geometry.</summary>
    public double TopHeight()
    {
        var q = Rotation; var ax = 2 * (q[0] * q[1] + q[3] * q[2]); var ay = 1 - 2 * (q[0] * q[0] + q[2] * q[2]); var az = 2 * (q[1] * q[2] - q[3] * q[0]);
        if (Shape == TriggerZoneShape.Box) return (Math.Abs(ax * Size[0]) + Math.Abs(ay * Size[1]) + Math.Abs(az * Size[2])) / 2;
        var r = Math.Abs(Size[0]) / 2;
        if (Shape == TriggerZoneShape.Sphere) return r;
        if (Shape == TriggerZoneShape.Cylinder) return r * Math.Sqrt(Math.Max(0, 1 - ay * ay)) + Math.Abs(ay * Size[1]) / 2;
        return r + Math.Max((Size[1] - Size[0]) / 2, 0) * Math.Abs(ay);
    }

    /// <summary>The quaternion components of one rotation, in the site's `x y z w` order.</summary>
    public const int RotationComponents = Rotations.Components;

    internal TriggerZone(string id, MapLevelReference level, TriggerZoneShape shape, double[] size, double[] position,
        double[] rotation, TriggerZoneWho who, bool blocksPlayers, RoomLocator room, bool placed,
        TriggerZoneAppearance? appearance = null, TriggerZoneLabel? label = null, bool enabled = true, bool visible = true,
        string? group = null, TriggerZoneRoute? route = null, bool bodyHit = false)
    {
        Id = id;
        Level = level;
        Shape = shape;
        MutableSize = size;
        MutablePosition = route?.Points[0].ToArray() ?? position;
        Size = Array.AsReadOnly(MutableSize);
        Position = Array.AsReadOnly(MutablePosition);
        Rotation = Array.AsReadOnly(rotation);
        Who = who;
        BlocksPlayers = blocksPlayers;
        Room = room;
        Placed = placed;
        Appearance = appearance;
        Label = label;
        Enabled = enabled;
        Visible = visible;
        Group = group;
        Route = route;
        BodyHit = bodyHit;
        LabelText = label?.Text ?? "";
        LabelVisible = label != null;
    }

    /// <summary>The authored object's own id inside the package: the address key a plan mounts on and the identity
    /// both facts are published under. Opaque here; the package document is its only source, which is also what
    /// refuses an id the map-object address cannot carry.</summary>
    public string Id { get; }
    /// <summary>The level this zone was placed in. A zone of another level is not this world's zone.</summary>
    public MapLevelReference Level { get; }
    public TriggerZoneShape Shape { get; }
    /// <summary>Full sizes in metres: a box's three edge lengths, a sphere's diameter repeated three times, and a
    /// capsule's diameter, full height and diameter again.</summary>
    public IReadOnlyList<double> Size { get; }
    /// <summary>The zone's origin: room-local metres while the zone is unplaced, world metres once
    /// <see cref="PlacedIn"/> produced it.</summary>
    public IReadOnlyList<double> Position { get; }
    /// <summary>The zone's orientation as a unit quaternion in `x y z w` order, in the frame
    /// <see cref="Position"/> is written in.</summary>
    public IReadOnlyList<double> Rotation { get; }
    public TriggerZoneWho Who { get; }
    /// <summary>Whether every machine builds a player-blocking collider for this zone. Blocking is local physics:
    /// it is decided by the package data here and instantiated identically on every machine.</summary>
    public bool BlocksPlayers { get; }
    /// <summary>Judge an entity's body, a capsule from its feet up, rather than the one point at its feet: for a thin
    /// volume such as a laser, which a point at the feet would walk under.</summary>
    public bool BodyHit { get; }
    public const double BodyHeight = 1.8;
    public const double BodyRadius = 0.35;
    /// <summary>Metres of an entity's standing column above its feet that a zone without <see cref="BodyHit"/> judges: the short
    /// segment from the feet up meets the zone. A zone whose floor face is laid on the authored floor stays enterable when the game's
    /// walkable floor, and so the feet, sit a little below it; a zone wholly above the head is still not entered.</summary>
    public const double FootLift = 0.1;
    /// <summary>The authored room this zone stands in. The document always names one: a pose without a room is a
    /// pose in a coordinate system nothing can convert, so a zone that names no room is refused at the document.</summary>
    public RoomLocator Room { get; }
    /// <summary>Whether <see cref="Position"/> and <see cref="Rotation"/> are world coordinates. Only a placed
    /// zone is judged, tested for containment or given a body: an authored zone is the author's own room-local
    /// numbers and means nothing in the world until the room it names has been resolved.</summary>
    public bool Placed { get; }
    /// <summary>Whether, and in which colour, every machine draws the zone's outline in the level: null keeps the
    /// zone invisible. The outline is presentation only — it is drawn from the same package data on every machine
    /// and never takes part in the judgment.</summary>
    public TriggerZoneAppearance? Appearance { get; }
    public TriggerZoneLabel? Label { get; }
    public bool Enabled { get; internal set; }
    public bool Visible { get; internal set; }
    public string? Group { get; }
    public TriggerZoneRoute? Route { get; }
    public string LabelText { get; internal set; }
    public bool LabelVisible { get; internal set; }
    // Local presentation ownership; network snapshots cannot revive the authored label after a sign replaces it.
    internal bool LabelOverridden { get; set; }
    internal double[] MutablePosition { get; }
    internal double[] MutableSize { get; }
    internal int RuntimeIndex { get; set; } = -1;
    internal uint? LabelResourceId { get; set; }
    internal long LabelRevision { get; set; }

    /// <summary>The same zone in the world coordinates of the room it was authored in. A zone that is already
    /// placed answers itself, so a caller that re-places a table cannot compose a room pose twice.</summary>
    public TriggerZone PlacedIn(RoomPose room)
    {
        if (Placed) return this;
        var position = room.Point(Position);
        var rotation = room.Compose(Rotation);
        var route = Route == null ? null : Route with { Points = Route.Points.Select(point => (IReadOnlyList<double>)room.Point(point)).ToArray() };
        return new TriggerZone(Id, Level, Shape, Size.ToArray(), position, rotation, Who, BlocksPlayers, Room, true,
            Appearance, Label, Enabled, Visible, Group, route, BodyHit);
    }

    public double[] BlockingExtents() => Shape switch
    {
        TriggerZoneShape.Box => new[] { Size[0], Size[1], Size[2] },
        TriggerZoneShape.Sphere => new[] { Size[0], Size[0], Size[0] },
        _ => new[] { Size[0], Size[1], Size[0] }
    };

    /// <summary>Whether one world position is inside this zone. The point is rotated into the zone's own frame
    /// first, so an oriented box is tested against its own axes rather than against the world's.</summary>
    public bool Contains(IReadOnlyList<double> position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.Count != 3) throw new RuntimeContractException("spatial-point", "A position requires three coordinates.");
        var local = ToLocal(position[0] - Position[0], position[1] - Position[1], position[2] - Position[2]);
        const double epsilon = 1e-9;
        switch (Shape)
        {
            case TriggerZoneShape.Box:
                return Math.Abs(local.X) <= Size[0] / 2d + epsilon
                    && Math.Abs(local.Y) <= Size[1] / 2d + epsilon
                    && Math.Abs(local.Z) <= Size[2] / 2d + epsilon;
            case TriggerZoneShape.Sphere:
                return Length(local.X, local.Y, local.Z) <= Size[0] / 2d + epsilon;
            case TriggerZoneShape.Cylinder:
                return Length(local.X, 0, local.Z) <= Size[0] / 2d + epsilon
                    && Math.Abs(local.Y) <= Size[1] / 2d + epsilon;
            default:
                // Capsule: a `size[1]`-long segment along the zone's own Y axis with `size[0]`-diameter caps, so an
                // axis shorter than the diameter degenerates to the sphere of the same diameter.
                var segment = Math.Max(Size[1] / 2d - Size[0] / 2d, 0);
                var along = Math.Max(Math.Abs(local.Y) - segment, 0);
                return Length(local.X, along, local.Z) <= Size[0] / 2d + epsilon;
        }
    }

    /// <summary>The one membership test of the judging tick: the vertical segment from the entity's feet up meets the zone — the
    /// <see cref="FootLift"/> stretch for a plain zone, the whole <see cref="BodyHeight"/> widened by <see cref="BodyRadius"/> on a
    /// <see cref="BodyHit"/> zone.</summary>
    public bool Holds(IReadOnlyList<double> position)
        => Meets(position[0], position[1], position[2], position[0], position[1] + (BodyHit ? BodyHeight : FootLift), position[2], BodyHit ? BodyRadius : 0);

    /// <summary>Whether an entity moved through this zone between two judgments, from <paramref name="from"/> to <paramref name="to"/>
    /// (feet positions). A tick judges a cadence apart, so a body can cross a thin volume entirely in between; this keeps that crossing
    /// observable. A plain zone sweeps the top of its <see cref="FootLift"/> stretch, so a zone laid on the floor is crossed the way it
    /// is held; a <see cref="BodyHit"/> zone sweeps the feet.</summary>
    public bool Crossed(IReadOnlyList<double> from, IReadOnlyList<double> to)
    {
        var lift = BodyHit ? 0 : FootLift;
        return Meets(from[0], from[1] + lift, from[2], to[0], to[1] + lift, to[2], 0);
    }

    /// <summary>Named-point queries use the shared area-field test in this already resolved room frame.</summary>
    public bool ContainsPoint(double x, double y, double z)
    {
        if (!Placed) throw new RuntimeContractException("area-point-unresolved", "The range has no resolved room pose.");
        var p = ToLocal(x - Position[0], y - Position[1], z - Position[2]);
        var shape = Shape switch
        {
            TriggerZoneShape.Box => AreaFieldShape.Box,
            TriggerZoneShape.Sphere => AreaFieldShape.Sphere,
            TriggerZoneShape.Cylinder => AreaFieldShape.Cylinder,
            _ => AreaFieldShape.Capsule
        };
        return AreaFieldDefinition.Contains(shape, p.X, p.Y, p.Z, Size[0] / 2, Size[1], Size[0] / 2, Size[1] / 2, Size[2] / 2);
    }

    /// <summary>Whether the straight line between two world positions, widened by <paramref name="margin"/>, meets this zone: the
    /// one shape test of <see cref="Holds"/> and <see cref="Crossed"/>. The line is carried into the zone's own frame and the
    /// shape's own rule is applied to the whole line: a box is met when the line crosses the box, a cylinder when it crosses the
    /// height slab inside the radius, a sphere when its closest approach to the centre is within the radius, and a capsule when
    /// its closest approach to the zone's own Y-axis segment is.</summary>
    private bool Meets(double fromX, double fromY, double fromZ, double toX, double toY, double toZ, double margin)
    {
        var start = ToLocal(fromX - Position[0], fromY - Position[1], fromZ - Position[2]);
        var end = ToLocal(toX - Position[0], toY - Position[1], toZ - Position[2]);
        var dx = end.X - start.X; var dy = end.Y - start.Y; var dz = end.Z - start.Z;
        if (Shape == TriggerZoneShape.Box)
        {
            var low = 0d; var high = 1d;
            return Clip(start.X, dx, Size[0] / 2 + margin + 1e-9, ref low, ref high)
                && Clip(start.Y, dy, Size[1] / 2 + margin + 1e-9, ref low, ref high) && Clip(start.Z, dz, Size[2] / 2 + margin + 1e-9, ref low, ref high);
        }
        var radius = Size[0] / 2 + margin + 1e-9;
        if (radius < 0) return false;
        if (Shape == TriggerZoneShape.Cylinder)
        {
            var low = 0d; var high = 1d;
            if (!Clip(start.Y, dy, Size[1] / 2 + margin + 1e-9, ref low, ref high)) return false;
            var a = dx * dx + dz * dz; var b = 2 * (start.X * dx + start.Z * dz); var c = start.X * start.X + start.Z * start.Z - radius * radius;
            if (a <= 1e-24) return c <= 0;
            var discriminant = b * b - 4 * a * c; if (discriminant < 0) return false;
            var root = Math.Sqrt(discriminant); var first = (-b - root) / (2 * a); var last = (-b + root) / (2 * a);
            return Math.Max(low, first) <= Math.Min(high, last);
        }
        var axis = Shape == TriggerZoneShape.Sphere ? 0 : Math.Max((Size[1] - Size[0]) / 2, 0);
        return DistanceToSpine(start.X, start.Y, start.Z, dx, dy, dz, axis) <= radius;
    }

    private (double X, double Y, double Z) ToLocal(double x, double y, double z) => Rotations.InverseRotate(Rotation, x, y, z);
    private static double Length(double x, double y, double z)
    {
        var scale = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))); if (scale == 0) return 0;
        x /= scale; y /= scale; z /= scale; return scale * Math.Sqrt(x * x + y * y + z * z);
    }
    private static bool Clip(double start, double direction, double half, ref double low, ref double high)
    {
        if (half < 0) return false;
        if (Math.Abs(direction) <= 1e-12) return Math.Abs(start) <= half;
        var first = (-half - start) / direction; var second = (half - start) / direction;
        if (first > second) (first, second) = (second, first);
        low = Math.Max(low, first); high = Math.Min(high, second); return low <= high;
    }
    private static double DistanceToSpine(double x, double y, double z, double dx, double dy, double dz, double axis)
    {
        const double epsilon = 1e-12;
        var a = dx * dx + dy * dy + dz * dz; var e = 4 * axis * axis; var f = 2 * axis * (y + axis); var c = dx * x + dy * (y + axis) + dz * z;
        double s, t;
        if (a <= epsilon && e <= epsilon) { s = 0; t = 0; }
        else if (a <= epsilon) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else if (e <= epsilon) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
        else
        {
            var b = dy * 2 * axis; var denominator = a * e - b * b;
            s = denominator != 0 ? Math.Clamp((b * f - c * e) / denominator, 0, 1) : 0; t = (b * s + f) / e;
            if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); } else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
        }
        return Length(x + dx * s, y + dy * s - (-axis + 2 * axis * t), z + dz * s);
    }

    public override string ToString() => Id + "@" + Level;
}

/// <summary>A zone's in-game outline colour: the document's `display.color`, `#RRGGBB`, as three channels in
/// [0, 1]. Its presence is the author's "show the range in the game" choice; its absence keeps the zone invisible.</summary>
internal sealed record TriggerZoneAppearance(string Style, double Red, double Green, double Blue, double Opacity, double Glow)
{
    /// <summary>The sensor style, decided once when the row is read so the per-tick frame-work check compares no string.</summary>
    public bool IsSensor { get; } = Style == "sensor";

    /// <summary>Reads `#RRGGBB` (either case) and nothing else.</summary>
    public static TriggerZoneAppearance? TryParse(string? color, string style, double opacity, double glow)
    {
        if (color is not { Length: 7 } || color[0] != '#') return null;
        var channels = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (!byte.TryParse(color.AsSpan(1 + i * 2, 2), System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;
            channels[i] = value / 255d;
        }
        return new TriggerZoneAppearance(style, channels[0], channels[1], channels[2], opacity, glow);
    }
}

internal sealed record TriggerZoneLabel(string Text, double Offset);
internal sealed record TriggerZoneRoute(IReadOnlyList<IReadOnlyList<double>> Points, double Speed, string Mode, bool AutoStart);

/// <summary>One drawn line of a zone's outline, in the zone's own frame: its points in order and whether the last
/// point joins the first.</summary>

/// <summary>
/// The one package document that carries a level's trigger zones:
/// `plugins/&lt;package&gt;/forge/trigger-zones.forge`, the same one-level package directory rule plans and the other
/// authored content files use. The document is data the author's own editor wrote, so it is parsed strictly and
/// all-or-nothing: a document that names one id twice, a shape it cannot build, or a number it cannot use names no
/// zone set at all. Reading the file off disk is the native half's job; this half is the document's one grammar.
/// </summary>
internal static class TriggerZoneManifest
{
    /// <summary>Zones one level may carry across every installed package.</summary>
    public const int MaximumZones = 512;
    /// <summary>Authored dimensions retain the native Single range, including empty or inverted volumes.</summary>
    public const double MinimumSize = -float.MaxValue;
    public const double MaximumSize = float.MaxValue;
    public const int MaximumIdLength = 128;
    private const string FileName = "trigger-zones.forge";

    /// <summary>The document's one file name, which is what a package scan looks for.</summary>
    public static string FileNameOnly => FileName;

    /// <summary>Reads one document. Every refusal is one code and one sentence naming the first thing that could not
    /// be read; a document that reads answers its zones in the order the author wrote them.</summary>
    public static bool TryParse(string text, out IReadOnlyList<TriggerZone>? zones, out string? code, out string? reason)
    {
        zones = null; code = null; reason = null;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException error) { code = "trigger-zone-json"; reason = error.Message; return false; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Refuse(out zones, out code, out reason, "trigger-zone-json", "The document's root is not an object.");
            if (!DocumentReading.Known(root, out var unknown, "schemaVersion", "zones"))
                return Refuse(out zones, out code, out reason, "trigger-zone-field", "Unknown field `" + unknown + "`.");
            if (!FormatVersion.Matches(root))
                return Refuse(out zones, out code, out reason, "trigger-zone-schema", "The document must declare schemaVersion " + FormatVersion.Current + ".");
            if (!root.TryGetProperty("zones", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return Refuse(out zones, out code, out reason, "trigger-zone-zones", "The document carries no `zones` array.");
            var parsed = new List<TriggerZone>(rows.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                if (!TryZone(row, out var zone, out code, out reason)) return false;
                if (!seen.Add(zone!.Id))
                    return Refuse(out zones, out code, out reason, "trigger-zone-duplicate",
                        "Zone id `" + zone.Id + "` is claimed more than once; the document names no zone set.");
                parsed.Add(zone);
            }
            // The cap bounds one level's table: only one level runs at a time, so a document may carry several levels.
            var crowded = parsed.GroupBy(zone => zone.Level).FirstOrDefault(group => group.Count() > MaximumZones);
            if (crowded != null)
                return Refuse(out zones, out code, out reason, "trigger-zone-limit",
                    "The document carries " + crowded.Count() + " zones for level " + crowded.Key + "; the cap is " + MaximumZones + " per level.");
            zones = Array.AsReadOnly(parsed.ToArray());
            return true;
        }
    }

    private static bool TryZone(JsonElement row, out TriggerZone? zone, out string? code, out string? reason)
    {
        zone = null; code = null; reason = null;
        if (row.ValueKind != JsonValueKind.Object) return RefuseZone(out zone, out code, out reason, "trigger-zone-json", "A zone entry is not an object.");
        if (!DocumentReading.Known(row, out var unknown, "id", "level", "room", "shape", "size", "position", "rotation", "who", "blocksPlayers", "appearance", "label", "enabled", "visible", "group", "route", "bodyHit"))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-field", "Unknown zone field `" + unknown + "`.");
        if (!row.TryGetProperty("room", out var roomRow))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-room",
                "A zone names the room it stands in; a room-local pose without a room cannot be placed.");
        if (!RoomLocator.TryParse(roomRow, out var room, out _, out var roomReason))
            // The document's own code names the field a zone could not read; the locator reader's code names the
            // shared grammar, and a zone document reports its own vocabulary so one refusal reads the same however
            // it was reached.
            return RefuseZone(out zone, out code, out reason, "trigger-zone-room", roomReason!);
        if (!DocumentReading.Text(row, "id", out var id) || id!.Length > MaximumIdLength || !TriggerZoneAddress.IsZoneId(id))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-id", "A zone needs an id of at most "
                + MaximumIdLength + " characters that is not `" + TriggerZoneAddress.Category + "/` and carries no `:` or `/`.");
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
            return RefuseZone(out zone, out code, out reason, "trigger-zone-level", "A zone needs a level reference of the form `<rundown>:<tier>:<index>`.");
        if (!DocumentReading.Text(row, "shape", out var shapeText) || !TryShape(shapeText!, out var shape))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-shape", "A zone shape is box, sphere, capsule or cylinder.");
        if (!DocumentReading.Numbers(row, "size", 3, MinimumSize, MaximumSize, out var size))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-size", "A size carries three finite metre lengths in [" + MinimumSize + ", " + MaximumSize + "].");
        if (!DocumentReading.Numbers(row, "position", 3, -float.MaxValue, float.MaxValue, out var position))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-position", "A position carries three finite metre coordinates.");
        if (!DocumentReading.Numbers(row, "rotation", TriggerZone.RotationComponents, -1, 1, out var rotation) || !Rotations.IsUnit(rotation!))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-rotation", "A rotation is a unit quaternion in `x y z w` order.");
        var who = TriggerZoneWho.Both;
        if (row.TryGetProperty("who", out var whoValue))
        {
            if (whoValue.ValueKind != JsonValueKind.String || !TryWho(whoValue.GetString()!, out who))
                return RefuseZone(out zone, out code, out reason, "trigger-zone-who", "`who` is `player`, `enemy` or `both`.");
        }
        var blocks = false;
        if (row.TryGetProperty("blocksPlayers", out var blocksValue))
        {
            if (blocksValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return RefuseZone(out zone, out code, out reason, "trigger-zone-blocks", "`blocksPlayers` is a boolean.");
            blocks = blocksValue.GetBoolean();
        }
        TriggerZoneAppearance? appearance = null;
        if (row.TryGetProperty("appearance", out var a))
        {
            if (a.ValueKind != JsonValueKind.Object || !DocumentReading.Known(a, out _, "style", "color", "opacity", "glow")
                || !DocumentReading.Text(a, "style", out var style) || style is not ("outline" or "solid" or "sensor")
                || !a.TryGetProperty("color", out var color) || color.ValueKind != JsonValueKind.String
                || !Single(a, "opacity", out var opacity) || !Single(a, "glow", out var glow)
                || TriggerZoneAppearance.TryParse(color.GetString(), style!, opacity, glow) is not { } parsed)
                return RefuseZone(out zone, out code, out reason, "trigger-zone-appearance", "Appearance requires style, #RRGGBB color, finite Single opacity and glow.");
            appearance = parsed;
        }
        TriggerZoneLabel? label = null;
        if (row.TryGetProperty("label", out var l))
        {
            if (l.ValueKind != JsonValueKind.Object || !DocumentReading.Known(l, out _, "text", "offset")
                || !l.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
                || !TextResourceContract.TryParseReference(text.GetString(), false, out _)
                || !Single(l, "offset", out var offset))
                return RefuseZone(out zone, out code, out reason, "trigger-zone-label",
                    "Label requires a compiled `forge:text:<id>@<Language>` reference and finite Single offset.");
            label = new TriggerZoneLabel(text.GetString()!, offset);
        }
        if (!OptionalBoolean(row, "bodyHit", false, out var bodyHit))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-body-hit", "`bodyHit` is a boolean.");
        if (!OptionalBoolean(row, "enabled", true, out var enabled) || !OptionalBoolean(row, "visible", true, out var visible))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-state", "Enabled and visible are booleans.");
        if (!MapObjectGroup.TryRead(row, out var group))
            return RefuseZone(out zone, out code, out reason, "trigger-zone-group", MapObjectGroup.Rule);
        TriggerZoneRoute? route = null;
        if (row.TryGetProperty("route", out var r))
        {
            if (r.ValueKind != JsonValueKind.Object || !DocumentReading.Known(r, out _, "points", "speed", "mode", "autoStart")
                || !r.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array || points.GetArrayLength() < 2
                || !Single(r, "speed", out var speed) || !DocumentReading.Text(r, "mode", out var mode) || mode is not ("once" or "pingpong" or "loop")
                || !r.TryGetProperty("autoStart", out _) || !OptionalBoolean(r, "autoStart", false, out var autoStart))
                return RefuseZone(out zone, out code, out reason, "trigger-zone-route", "Route requires at least two points, finite Single speed, mode and autoStart.");
            var parsedPoints = new List<IReadOnlyList<double>>();
            foreach (var point in points.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 3)
                    return RefuseZone(out zone, out code, out reason, "trigger-zone-route", "A route point has three finite Single coordinates.");
                var values = new double[3]; var i = 0;
                foreach (var value in point.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var v) || !double.IsFinite(v) || Math.Abs(v) > float.MaxValue)
                        return RefuseZone(out zone, out code, out reason, "trigger-zone-route", "A route point has three finite Single coordinates.");
                    values[i++] = v;
                }
                parsedPoints.Add(Array.AsReadOnly(values));
            }
            route = new TriggerZoneRoute(parsedPoints.AsReadOnly(), speed, mode!, autoStart);
        }
        zone = new TriggerZone(id!, level, shape, size!, position!, rotation!, who, blocks, room!, false, appearance, label, enabled, visible, group, route, bodyHit);
        return true;
    }

    private static bool Single(JsonElement row, string key, out double value)
    {
        value = 0;
        return row.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out value)
            && double.IsFinite(value) && Math.Abs(value) <= float.MaxValue;
    }

    private static bool OptionalBoolean(JsonElement row, string key, bool fallback, out bool value)
    {
        value = fallback;
        if (!row.TryGetProperty(key, out var field)) return true;
        if (field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = field.GetBoolean(); return true;
    }

    /// <summary>The refusal one zone entry gets, in the shape that entry's own reader answers with. It is the
    /// same answer the set reader gives: a refusal names its code and says what the entry should have carried,
    /// and the zone it refused is null.</summary>
    private static bool RefuseZone(out TriggerZone? zone, out string? code, out string? reason, string refused, string message)
    {
        zone = null; code = refused; reason = message;
        return false;
    }

    private static bool TryShape(string text, out TriggerZoneShape shape)
    {
        switch (text)
        {
            case "box": shape = TriggerZoneShape.Box; return true;
            case "sphere": shape = TriggerZoneShape.Sphere; return true;
            case "capsule": shape = TriggerZoneShape.Capsule; return true;
            case "cylinder": shape = TriggerZoneShape.Cylinder; return true;
            default: shape = TriggerZoneShape.Box; return false;
        }
    }

    private static bool TryWho(string text, out TriggerZoneWho who)
    {
        switch (text)
        {
            case "player": who = TriggerZoneWho.Player; return true;
            case "enemy": who = TriggerZoneWho.Enemy; return true;
            case "both": who = TriggerZoneWho.Both; return true;
            default: who = TriggerZoneWho.Both; return false;
        }
    }

    private static bool Refuse(out IReadOnlyList<TriggerZone>? zones, out string? code, out string? reason, string refused, string message)
    {
        zones = null; code = refused; reason = message;
        return false;
    }
}

/// <summary>
/// The address of one trigger zone: the one map-object namespace, this category, and the zone's own authored id as
/// the address key. A zone is not placed by the level generator, so it carries none of the level coordinates a door
/// or a terminal address is built from — it is placed by the author, and the id the author wrote is the whole
/// identity. The three coordinate fields are the constant `0` every zone address writes, which keeps one address
/// shape across the categories while the key stays the zone's own name.
///
/// The key is what makes a mount work: a plan's `map-object` attachment carries this category and a zone id, and
/// the `map-object` matcher compares that address with the address the publishing zone re-read from itself, so a
/// plan mounted on one zone is dispatched for that zone and for no other.
/// </summary>
internal static class TriggerZoneAddress
{
    /// <summary>The category every trigger-zone address carries.</summary>
    public static readonly string Category = "trigger_zone";

    /// <summary>The one coordinate a zone address writes. A zone is not addressed by level coordinates, so the
    /// three fields carry this constant rather than an invented dimension, layer or local index.</summary>
    private const string ZoneCoordinate = "0";

    /// <summary>Whether a text is a zone id this address can carry. The id is the address's key, so it is the
    /// object's whole name inside the namespace: an empty key, a key with a `/` — which the address format reads as
    /// the next segment — and a key with a `:` — which an entity id reads as the namespace's own separator — are
    /// refused at the document instead of being published as an address nothing can match.</summary>
    public static bool IsZoneId(string? id)
        => !string.IsNullOrEmpty(id) && id.IndexOf('/') < 0 && id.IndexOf(':') < 0 && id != Category;

    /// <summary>Reads a zone address: this category, three constant coordinates and a zone id. Anything else —
    /// another category, another shape, an empty or unnameable key — is not this category's address.</summary>
    public static MapObjectReference? TryParse(string? text)
        => MapObjectReference.TryParse(text, Category, IsZoneId);

    /// <summary>The address of one zone, or null when the id is not one the address can carry. A caller that gets
    /// null never had an address to publish: the document refuses the same id at load.</summary>
    public static MapObjectReference? Of(string? zoneId)
        => IsZoneId(zoneId)
            ? new MapObjectReference(Category, ZoneCoordinate, ZoneCoordinate, ZoneCoordinate, zoneId!)
            : null;
}

/// <summary>
/// The two trigger rows of this family, declared the way every other Map row is: the capability row, its binding
/// row, and the registration row. A zone is a map object of this provider, so both rows are published through the
/// one map-object half — the event's subject is the zone and its `zone` port is the same object — and a plan is
/// mounted on the zone instead of on a volume id a second mechanism would match.
///
/// Both rows declare no inputs and no parameters, because that is what a trigger row is in this runtime: the plan
/// loader refuses a trigger node whose contract declares an input or a structural parameter (`trigger-shape`), so a
/// row carrying either would make every plan that uses it unloadable. What the author chooses instead lives on the
/// zone object itself — its shape, size, position, rotation, `who` and `blocksPlayers`.
/// </summary>
internal static class TriggerZoneContract
{
    /// <summary>The Map provider these rows belong to: the same provider id the Map declaration names, and the
    /// owner every capability and binding row below is registered under. A registration refuses a row whose owner
    /// is not a declared provider, so a mismatch here fails at load rather than drifting quietly.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    public const string EnteredFact = "trigger_zone.entered";
    public const string ExitedFact = "trigger_zone.exited";

    /// <summary>Every trigger row, in declaration order, as (fact, capability).</summary>
    public static readonly IReadOnlyList<(string Fact, string Capability)> Rows = Array.AsReadOnly(new[]
    {
        (EnteredFact, Blocks.TriggerSpatialEntered.Id),
        (ExitedFact, Blocks.TriggerSpatialExited.Id)
    });

    /// <summary>The binding id of one capability: this provider's own suffix under its own namespace, exactly as
    /// the other Map facts spell it.</summary>
    public static string Binding(string capabilityId)
        => ProviderId + ".binding." + Suffix(capabilityId);

    /// <summary>The binding one fact is published through, by the fact's own name. A fact outside the two rows is
    /// refused by name rather than published on a binding nothing declared.</summary>
    public static string BindingOf(string fact) => Binding(Rows.Single(row => row.Fact == fact).Capability);

    internal static string Suffix(string capabilityId)
        => capabilityId.StartsWith("forge.trigger.", StringComparison.Ordinal)
            ? capabilityId["forge.trigger.".Length..]
            : throw new RuntimeContractException("trigger-zone-capability", capabilityId);

    // ---- the members read: who the judging tick currently holds inside one zone ----------------------------------

    /// <summary>The one value row of this family: the entities the host's own judgment currently holds inside a
    /// zone. It reads the membership table the two facts above are the edges of, so "everyone is inside" is answered
    /// by the same judgment that published the entry, never by a second containment test.</summary>
    public const string MembersBinding = ProviderId + ".binding.query.map.trigger_zone_members";
    public const string MembersHandlerName = "gtfo.map.trigger_zone_members";
    /// <summary>The refusal an input that is not a trigger-zone address of this provider gets.</summary>
    public const string MembersInputCode = "trigger-zone-members-input";
    /// <summary>The refusal a zone this machine is not judging gets: a client, a zone of another level, or a level
    /// whose zones the host has not judged yet. An empty answer is reserved for a judged zone nobody stands in.</summary>
    public const string MembersUnjudgedCode = "trigger-zone-members-unjudged";

    /// <summary>The one shape of the members handler: the zone in, the members out.</summary>
    public static readonly HandlerShape MembersShape = new HandlerShape().Inputs("zone").Outputs("targets");

    /// <summary>The members evaluator. <paramref name="read"/> answers the members the host holds inside the zone
    /// an id names, or null when this machine is not judging that zone; it is the game-bound session's read of the
    /// judging module, handed over because this assembly holds none of it.</summary>
    public static EvaluatorHandler MembersEvaluator(Func<string, IReadOnlyCollection<EntityReference>?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return (context, outputs) =>
        {
            if (!context.Inputs.TryGetProperty("zone", out var input) || input.ValueKind != JsonValueKind.Object)
                throw new RuntimeContractException(MembersInputCode, "The members read needs a trigger zone.");
            var zone = RuntimeJson.Entity(input);
            const string prefix = EntityKinds.MapObject + ":";
            var address = zone.Id.StartsWith(prefix, StringComparison.Ordinal)
                ? TriggerZoneAddress.TryParse(zone.Id[prefix.Length..]) : null;
            if (address == null)
                throw new RuntimeContractException(MembersInputCode, "Not a trigger zone: " + zone.Id);
            var members = read(address.Key)
                ?? throw new RuntimeContractException(MembersUnjudgedCode, "This machine is not judging trigger zone " + address.Key + ".");
            outputs.Entities(0, members as IReadOnlyList<EntityReference> ?? members.ToArray());
        };
    }

    /// <summary>The zones of one level, in the document's own order. A zone placed in another level names a volume
    /// this world does not have.</summary>
    public static IReadOnlyList<TriggerZone> ForLevel(IReadOnlyList<TriggerZone> zones, MapLevelReference level)
        => Array.AsReadOnly(zones.Where(zone => zone.Level == level).ToArray());

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.spatial.entered", Blocks.TriggerSpatialEntered.Id, "gtfo.map.spatial_entered", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.map.binding.spatial.exited", Blocks.TriggerSpatialExited.Id, "gtfo.map.spatial_exited", "observe", Array.Empty<string>()),
        new ProviderBinding(MembersBinding, Blocks.QueryMapTriggerZoneMembers.Id, MembersHandlerName, "observe", Array.Empty<string>(), MembersShape)
    }.Concat(TriggerZoneActionsContract.Bindings()).ToArray();
}

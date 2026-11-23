using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>The volumetric members an `area_field` definition can express, in the order the shared `query_shape`
/// set declares them. The set's other members (`room`, `area`, `zone`, `path`, and the open `cone`) name a region
/// this definition carries no measurement for, so a definition that names one is refused by name instead of being
/// mapped onto the nearest volume it resembles.</summary>
internal enum AreaFieldShape { Sphere, Cylinder, Capsule, Box }

/// <summary>
/// One authored `area_field` definition: the shape and the sizes a field has, and nothing about where in the world
/// it is. The centre is the consumer's — a `move_to`/`teleport` destination, a selector's own `center` port, or the
/// entity a live field is hosted on — so one definition is reusable by every consumer that carries one, and a
/// definition never goes stale because the world moved.
///
/// Measurements are metres and validated once, here, and each shape owns exactly its own: a sphere carries a
/// <see cref="Radius"/> and nothing else; a cylinder and a capsule carry a <see cref="Radius"/> and a
/// <see cref="Height"/>, the length along Y; a box carries neither and is measured by its three half sizes in
/// <see cref="Extents"/>. A measurement the selected shape does not use is refused rather than ignored, because
/// an author who wrote a radius on a box wrote it for a reason this row cannot honour.
/// Membership is closed and world-axis aligned, matching the spatial selector's own volume test
/// (`ObservedSpatialNodes.Overlap`): a position exactly on the boundary is inside.
/// </summary>
internal sealed class AreaFieldDefinition
{
    /// <summary>The greatest finite positive length the game's Single can store, for radii, heights and half sizes.</summary>
    public const double MaximumSize = float.MaxValue;

    private AreaFieldDefinition(string id, AreaFieldShape shape, string member, double radius, double? height, IReadOnlyList<double>? extents)
    {
        Id = id; Shape = shape; Member = member; Radius = radius; Height = height; Extents = extents;
    }

    /// <summary>The resource id the definition was published under: the author's own block id.</summary>
    public string Id { get; }
    public AreaFieldShape Shape { get; }
    /// <summary>The `query_shape` member this definition's shape is spelled as on the wire.</summary>
    public string Member { get; }
    /// <summary>The sphere's, the cylinder's and the capsule's own outer size, and zero for a box, which is
    /// measured by <see cref="Extents"/> alone.</summary>
    public double Radius { get; }
    /// <summary>The cylinder's and capsule's length along Y, or null for a shape that carries none.</summary>
    public double? Height { get; }
    /// <summary>The box's three half sizes (x, y, z), or null for a shape that carries none.</summary>
    public IReadOnlyList<double>? Extents { get; }

    /// <summary>The one membership test: a world-axis aligned closed volume around <paramref name="center"/>. Both
    /// points are metre coordinates in one world, and a non-finite one is refused rather than answered "outside",
    /// because a position this test cannot read is not a fact about the world.</summary>
    public bool Contains(IReadOnlyList<double> position, IReadOnlyList<double> center)
    {
        var point = Point(position, "position");
        var origin = Point(center, "center");
        return Contains(Shape, point[0] - origin[0], point[1] - origin[1], point[2] - origin[2],
            Radius, Height ?? 0, Extents?[0] ?? 0, Extents?[1] ?? 0, Extents?[2] ?? 0);
    }

    /// <summary>The same closed volume test in its local frame, without temporary coordinate arrays.</summary>
    public static bool Contains(AreaFieldShape shape, double x, double y, double z,
        double radius, double height, double extentX, double extentY, double extentZ)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            throw new RuntimeContractException("spatial-overflow", "Field volume difference overflowed.");
        x = Math.Abs(x); y = Math.Abs(y); z = Math.Abs(z);
        return shape switch
        {
            AreaFieldShape.Sphere => Magnitude(x, y, z) <= radius,
            AreaFieldShape.Cylinder => Magnitude(x, 0, z) <= radius && y <= height / 2,
            AreaFieldShape.Capsule => Magnitude(x, Math.Max(y - Math.Max(height / 2 - radius, 0), 0), z) <= radius,
            _ => x <= extentX && y <= extentY && z <= extentZ
        };
    }

    /// <summary>The shape one `query_shape` member names, or false for a member this definition cannot express.</summary>
    public static bool TryShape(string? member, out AreaFieldShape shape)
    {
        switch (member)
        {
            case "sphere": shape = AreaFieldShape.Sphere; return true;
            case "cylinder": shape = AreaFieldShape.Cylinder; return true;
            case "capsule": shape = AreaFieldShape.Capsule; return true;
            case "box": shape = AreaFieldShape.Box; return true;
            default: shape = AreaFieldShape.Sphere; return false;
        }
    }

    /// <summary>The `query_shape` member one shape is spelled as.</summary>
    public static string MemberOf(AreaFieldShape shape) => shape switch
    {
        AreaFieldShape.Sphere => "sphere",
        AreaFieldShape.Cylinder => "cylinder",
        AreaFieldShape.Capsule => "capsule",
        _ => "box"
    };

    /// <summary>
    /// One definition, read from the payload of a published runtime resource. The four fields are the resource
    /// catalog's own (`shape`, `radius`, `height`, `extents`), and the shape is dispatched before any measurement
    /// is read, because each shape owns exactly its own fields: a sphere carries exactly a `radius`; a cylinder
    /// and a capsule carry exactly a `radius` and a `height`; a box carries exactly three half sizes in `extents`
    /// and neither a radius nor a height. Anything else — an unknown shape, a missing measurement, a measurement
    /// outside its bound, or a measurement the selected shape does not use — is refused with its own code and a
    /// sentence naming the field, never rounded to a neighbouring shape.
    /// </summary>
    public static bool TryParse(string id, JsonElement payload, out AreaFieldDefinition? definition, out string? code, out string? reason)
    {
        definition = null; code = null; reason = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            code = AreaFieldCodes.Measurement; reason = "The field payload is not an object.";
            return false;
        }
        foreach (var property in payload.EnumerateObject())
            if (property.Name is not ("shape" or "radius" or "height" or "extents" or "name" or "placement"))
            {
                code = AreaFieldCodes.Measurement; reason = "Unknown field payload member: " + property.Name + ".";
                return false;
            }
        // F5 named map areas carry their actual room placement. Geometry consumers still use this same
        // definition; the Map placement reader validates and resolves the room before teleport can commit.
        if (payload.TryGetProperty("name", out var name) || payload.TryGetProperty("placement", out _))
        {
            if (name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())
                || name.GetString()!.Length > 128 || !payload.TryGetProperty("placement", out var placement)
                || placement.ValueKind != JsonValueKind.Object
                || placement.EnumerateObject().Any(property => property.Name is not ("level" or "room" or "position" or "rotation" or "landing"))
                || placement.TryGetProperty("landing", out var landing) && (landing.ValueKind != JsonValueKind.Object
                    || landing.EnumerateObject().Any(property => property.Name is not ("position" or "rotation"))
                    || !FiniteTuple(landing, "position", 3) || !FiniteTuple(landing, "rotation", 4))
                || !placement.TryGetProperty("level", out var level) || level.ValueKind != JsonValueKind.String
                || !placement.TryGetProperty("room", out var room) || room.ValueKind != JsonValueKind.Object
                || !FiniteTuple(placement, "position", 3) || !FiniteTuple(placement, "rotation", 4))
            { code = AreaFieldCodes.Measurement; reason = "A named area needs a name and a complete room placement."; return false; }
        }
        if (!payload.TryGetProperty("shape", out var shapeValue) || shapeValue.ValueKind != JsonValueKind.String
            || !TryShape(shapeValue.GetString(), out var shape))
        {
            code = AreaFieldCodes.Shape;
            reason = "A field definition declares a shape of sphere, cylinder, capsule or box.";
            return false;
        }
        // The selected shape's own measurements, stated once: the fields it owns, and the only fields it may
        // carry. A field outside this list is refused by name rather than read as if the shape used it.
        var owns = shape switch
        {
            AreaFieldShape.Sphere => new[] { "radius" },
            AreaFieldShape.Box => new[] { "extents" },
            _ => new[] { "radius", "height" }
        };
        foreach (var property in payload.EnumerateObject())
            if (property.Name is not ("shape" or "name" or "placement") && Array.IndexOf(owns, property.Name) < 0)
            {
                code = AreaFieldCodes.Measurement;
                reason = "A " + MemberOf(shape) + " field carries no " + property.Name + "; it is measured by "
                    + string.Join(" and ", owns) + ".";
                return false;
            }
        double radius = 0; double? height = null; IReadOnlyList<double>? extents = null;
        if (shape == AreaFieldShape.Box)
        {
            if (!HalfSizes(payload, out extents))
            {
                code = AreaFieldCodes.Measurement;
                reason = "A box field requires extents: three finite positive half sizes the game can store.";
                return false;
            }
        }
        else
        {
            if (!Number(payload, "radius", out radius) || radius <= 0 || radius > MaximumSize)
            {
                code = AreaFieldCodes.Measurement;
                reason = "A " + MemberOf(shape) + " field requires a finite positive radius the game can store.";
                return false;
            }
            if (shape is AreaFieldShape.Cylinder or AreaFieldShape.Capsule)
            {
                if (!Number(payload, "height", out var length) || length <= 0 || length > MaximumSize)
                {
                    code = AreaFieldCodes.Measurement;
                    reason = "A " + MemberOf(shape) + " field requires a finite positive height the game can store.";
                    return false;
                }
                height = length;
            }
        }
        definition = new AreaFieldDefinition(RuntimeJson.Text(id), shape, MemberOf(shape), radius, height, extents);
        return true;
    }

    private static bool FiniteTuple(JsonElement value, string key, int count)
        => value.TryGetProperty(key, out var tuple) && tuple.ValueKind == JsonValueKind.Array
            && tuple.GetArrayLength() == count && tuple.EnumerateArray().All(number => number.ValueKind == JsonValueKind.Number
                && number.TryGetDouble(out var parsed) && double.IsFinite(parsed));

    private static bool Number(JsonElement payload, string name, out double value)
    {
        value = 0;
        return payload.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static bool HalfSizes(JsonElement payload, out IReadOnlyList<double>? extents)
    {
        extents = null;
        if (!payload.TryGetProperty("extents", out var element) || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() != 3) return false;
        var values = new double[3];
        var index = 0;
        foreach (var coordinate in element.EnumerateArray())
        {
            if (coordinate.ValueKind != JsonValueKind.Number || !coordinate.TryGetDouble(out var value)
                || !double.IsFinite(value) || value <= 0 || value > MaximumSize) return false;
            values[index++] = value;
        }
        extents = Array.AsReadOnly(values);
        return true;
    }

    private static double[] Point(IReadOnlyList<double> value, string name)
    {
        if (value == null || value.Count != 3)
            throw new RuntimeContractException("spatial-point", "A field volume requires three metre coordinates: " + name + ".");
        var point = new[] { value[0], value[1], value[2] };
        if (point.Any(coordinate => !double.IsFinite(coordinate)))
            throw new RuntimeContractException("spatial-point", "A field volume point is not finite: " + name + ".");
        return point;
    }

    private static double Magnitude(double x, double y, double z)
    {
        var scale = Math.Max(x, Math.Max(y, z));
        if (scale == 0) return 0;
        x /= scale; y /= scale; z /= scale;
        var length = scale * Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(length)) throw new RuntimeContractException("spatial-overflow", "Field volume distance overflowed.");
        return length;
    }
}

/// <summary>The codes a field definition or document is refused with. They are this one reader's vocabulary: the
/// row that consumes a definition reports the code the provider refused with, and a document no provider can read
/// is an install defect rather than a step's business.</summary>
internal static class AreaFieldCodes
{
    /// <summary>The shape member. A shape the definition carries no measurement for is named, not approximated.</summary>
    public const string Shape = "area-field-shape";
    /// <summary>A measurement: missing, out of range, malformed, unknown, or one the shape does not use.</summary>
    public const string Measurement = "area-field-measurement";
    /// <summary>The file is not a published runtime resource document at all.</summary>
    public const string Format = "area-field-format";
    /// <summary>The document is a runtime resource of another kind: the resource folder is shared by every kind.</summary>
    public const string Kind = "area-field-kind";
    /// <summary>The document's own reference disagrees with its payload id, so it names two instances.</summary>
    public const string Reference = "area-field-reference";
    /// <summary>Documents claim the same field id with different revisions or content; all are withdrawn.</summary>
    public const string DuplicateId = "area-field-duplicate-id";
    /// <summary>The install ships more resources than one install may declare.</summary>
    public const string Limit = "area-field-limit";
    /// <summary>A resource path leaves the install or crosses a link.</summary>
    public const string PathRefused = "area-field-path";
    /// <summary>A resource folder or document cannot be inspected or read.</summary>
    public const string Unreadable = "area-field-unreadable";
    /// <summary>A document is over the byte cap.</summary>
    public const string FileSize = "area-field-file-size";
    /// <summary>A document is not valid UTF-8.</summary>
    public const string NotUtf8 = "area-field-utf8";

    /// <summary>The refusals of the walk over the install's resource documents.</summary>
    public static readonly PublishedResourceSetCodes Set = new(PathRefused, Unreadable, Limit, DuplicateId);
    /// <summary>The refusals of reading one document; syntax that is not JSON is a document that is no resource at all.</summary>
    public static readonly PublishedTextCodes Read = new(PathRefused, Unreadable, FileSize, Format, NotUtf8);
}

/// <summary>
/// One published runtime resource document, read as an `area_field` definition. The grammar is the website's own
/// (`Web/src/forge/runtime-resources.ts`): `format`, `schemaVersion`, `kind`, the `{id, revision}` reference the
/// plan compiles, and the payload the definition is read from. The reader answers whether the file is a document
/// of this kind at all, so the folder's other kinds — and a file that is no resource document — are told apart
/// from a broken field.
/// </summary>
internal static class AreaFieldDocument
{
    /// <summary>The published runtime resource format, spelled the way every kind's writer spells it.</summary>
    public const string Format = "gtfo-forge-engine-resource";
    /// <summary>The one resource kind this reader owns.</summary>
    public const string ResourceKind = "area_field";

    /// <summary>
    /// The document's own kind, read without parsing the payload: the resource folder is shared by every kind and a
    /// kind this reader does not own is left alone. A file that is not a runtime resource document, or one without
    /// a kind, is refused with its own code and sentence, because a broken file in the resource folder is an
    /// install defect rather than a document of another kind.
    /// </summary>
    public static bool TryKind(string text, out string? kind, out string? code, out string? reason)
        => PublishedResources.TryResourceKind(text, Format, AreaFieldCodes.Format, AreaFieldCodes.Kind,
            out kind, out code, out reason);

    /// <summary>
    /// One document of this kind as a definition: the reference id, the revision it was published under, and the
    /// definition itself. The revision travels with the definition because it is the content hash a plan's compiled
    /// reference was written against; whether a plan's own revision still matches is the plan loader's business,
    /// and this reader only proves that the document carries one.
    /// </summary>
    public static bool TryRead(string text, out string? id, out string? revision, out AreaFieldDefinition? definition,
        out string? code, out string? reason)
    {
        id = null; revision = null; definition = null; code = null; reason = null;
        if (!TryKind(text, out var kind, out code, out reason)) return false;
        if (kind != ResourceKind)
        {
            code = AreaFieldCodes.Kind; reason = "The document declares kind " + kind + ", not " + ResourceKind + ".";
            return false;
        }
        using var parsed = JsonDocument.Parse(text);
        var root = parsed.RootElement;
        if (!ForgeCore.FormatVersion.Matches(root))
        {
            code = AreaFieldCodes.Format; reason = "Unsupported runtime resource schemaVersion.";
            return false;
        }
        if (!root.TryGetProperty("reference", out var reference) || reference.ValueKind != JsonValueKind.Object
            || !reference.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
            || !reference.TryGetProperty("revision", out var revisionValue) || revisionValue.ValueKind != JsonValueKind.String)
        {
            code = AreaFieldCodes.Reference; reason = "A runtime resource carries an {id, revision} reference.";
            return false;
        }
        id = RuntimeJson.Text(idValue.GetString()!);
        revision = RuntimeJson.Text(revisionValue.GetString()!);
        if (!root.TryGetProperty("payload", out var payload))
        {
            code = AreaFieldCodes.Measurement; reason = "The field document carries no payload.";
            return false;
        }
        if (!AreaFieldDefinition.TryParse(id, payload, out definition, out code, out reason)) return false;
        if (payload.TryGetProperty("placement", out _) && !MatchesRevision(id, revision, payload))
        { definition = null; code = AreaFieldCodes.Reference; reason = "Named area content differs from its locked revision."; return false; }
        return true;
    }

    private static bool MatchesRevision(string id, string revision, JsonElement payload)
    {
        var semantic = "{\"format\":\"gtfo-forge-engine-resource\",\"id\":" + JsonSerializer.Serialize(id)
            + ",\"kind\":\"area_field\",\"payload\":" + RuntimeJson.Canonical(payload) + ",\"schemaVersion\":\"" + ForgeCore.FormatVersion.Current + "\"}";
        return ForgeCore.Hash.Sha256Hex(semantic) == revision;
    }

}

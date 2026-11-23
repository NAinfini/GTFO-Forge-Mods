using ForgeCore;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>A room-part prototype the level already knows how to instantiate through LG_PrefabSpawner.</summary>
internal sealed record PlacedModelPrototype(string SourcePrefab, string SourcePath);

/// <summary>The stable author resource plus the exact exported PNG revision this installed board decodes.</summary>
internal sealed record PlacedImageReference(string Id, string Revision);

/// <summary>An image panel fitted inside the author's width/height box without changing pixel aspect ratio.</summary>
internal sealed record PlacedModelImage(PlacedImageReference Reference, double Width, double Height, string PngSha256);

/// <summary>An independent text board with an authored metre-sized face and no native prototype.</summary>
internal sealed record PlacedModelSign(double Width, double Height);

/// <summary>A root-local bullet hit volume. It is independent of entity collision and never creates health,
/// navigation or an enemy identity.</summary>
internal sealed record PlacedModelShotCollider(bool Enabled, IReadOnlyList<double> Center, IReadOnlyList<double> Size);

/// <summary>Colours use #RRGGBB; font size is TextMeshPro's size, not metres. Zero opacity has no quad.</summary>
internal sealed record PlacedModelTextStyle(string Color, double FontSize, string BackgroundColor, double BackgroundOpacity, double? LineWidth = null, double? BackgroundPadding = null)
{
    /// <summary>Metres of background beyond the text on every side; the background always hugs the text block.</summary>
    public const double DefaultBackgroundPadding = .05;
    public float Padding => (float)(BackgroundPadding ?? DefaultBackgroundPadding);
    public static readonly PlacedModelTextStyle Default = new("#ffffff", 3, "#000000", 0);
    public static readonly PlacedModelTextStyle SignDefault = new("#ffffff", 3, "#000000", 1);
    public const double DefaultLineWidth = 5.4;
    /// <summary>TextMeshPro line spacing (hundredths of an em) of every placed-model text and world label.</summary>
    public const float LineSpacing = 12;
}

/// <summary>One room-authored object with a native prototype, image or text-board appearance.
/// Its stable map-object identity also hosts mounted behavior.</summary>
internal sealed class PlacedModel
{
    internal PlacedModel(string id, MapLevelReference level, RoomLocator room, PlacedModelPrototype? model,
        double[] position, double[] rotation, double[] scale, double[] colliderCenter, double[] colliderSize,
        string? text, PlacedModelTextStyle? textStyle = null, PlacedModelImage? image = null, PlacedModelSign? sign = null,
        JsonElement? content = null, PlacedModelShotCollider? shotCollider = null, bool nativeCollision = false,
        string? group = null)
    {
        NativeCollision = nativeCollision;
        Group = group;
        Id = id; Level = level; Room = room; Model = model;
        Position = Array.AsReadOnly(position); Rotation = Array.AsReadOnly(rotation); Scale = Array.AsReadOnly(scale);
        ColliderCenter = Array.AsReadOnly(colliderCenter); ColliderSize = Array.AsReadOnly(colliderSize); Text = text;
        TextStyle = textStyle; Image = image; Sign = sign; Content = content;
        ShotCollider = shotCollider;
    }

    public string Id { get; }
    public MapLevelReference Level { get; }
    public RoomLocator Room { get; }
    public PlacedModelPrototype? Model { get; }
    public PlacedModelImage? Image { get; }
    public PlacedModelSign? Sign { get; }
    /// <summary>Frozen flattened assembly and author backreferences. Native admission validates its shared model contract.</summary>
    public JsonElement? Content { get; }
    public PlacedModelShotCollider? ShotCollider { get; }
    public bool HasSolidCollider => ColliderSize[0] > 0 && ColliderSize[1] > 0 && ColliderSize[2] > 0;
    /// <summary>The model's own parts keep the collision their source room gives them (`"collision": "native"`): players
    /// stand on and against them and enemy navigation includes them. Only composed models carry it.</summary>
    public bool NativeCollision { get; }
    /// <summary>The author's group name: `forge.selector.target.object_group` answers every placed object of one group.</summary>
    public string? Group { get; }
    /// <summary>Solid models are placed inside the level's navigation-build window, before anything bakes the geometry.</summary>
    public bool RequiresBuildWindow => HasSolidCollider || NativeCollision;
    public IReadOnlyList<double> Position { get; }
    public IReadOnlyList<double> Rotation { get; }
    public IReadOnlyList<double> Scale { get; }
    public IReadOnlyList<double> ColliderCenter { get; }
    public IReadOnlyList<double> ColliderSize { get; }
    /// <summary>The one exported vanilla TextDataBlock reference. Its suffix is the author's primary language;
    /// the block itself owns all authored columns, so a selected empty column can still fall back correctly.</summary>
    public string? Text { get; }
    public PlacedModelTextStyle? TextStyle { get; }
}

internal static class PlacedModelAddress
{
    public const string Category = "model";
    public static bool IsId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > PlacedModelManifest.MaximumIdLength || id.Trim() != id
            || id == Category || id.IndexOf('/') >= 0) return false;
        foreach (char value in id) if (char.IsControl(value)) return false;
        return true;
    }
    public static MapObjectReference? Of(string? id) => IsId(id) ? MapObjectReference.Compact(Category, id!) : null;
    public static MapObjectReference? TryParse(string? text)
    {
        if (text == null || !text.StartsWith(Category + "/", StringComparison.Ordinal)) return null;
        string id = text[(Category.Length + 1)..];
        return IsId(id) ? Of(id) : null;
    }
}

internal static class PlacedModelManifest
{
    public const int MaximumIdLength = 128;
    public const string FileNameOnly = "placed-models.forge";
    public const string ComposedFileNameOnly = "composed-models.forge";
    private const double MaximumNumber = float.MaxValue;
    public static bool TryParse(string text, out IReadOnlyList<PlacedModel>? models, out string? code, out string? reason)
        => TryParse(text, false, out models, out code, out reason);

    internal static bool TryParseComposed(string text, out IReadOnlyList<PlacedModel>? models, out string? code, out string? reason)
        => TryParse(text, true, out models, out code, out reason);

    private static bool TryParse(string text, bool composed, out IReadOnlyList<PlacedModel>? models, out string? code, out string? reason)
    {
        models = null; code = null; reason = null;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException error) { code = "placed-model-json"; reason = error.Message; return false; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Refuse(out models, out code, out reason, "placed-model-json", "The document root is not an object.");
            if (!KnownOnce(root, out var unknown, "schemaVersion", "models"))
                return Refuse(out models, out code, out reason, "placed-model-field", "Unknown or repeated document field `" + unknown + "`.");
            if (!FormatVersion.Matches(root))
                return Refuse(out models, out code, out reason, "placed-model-schema", "The document must declare schemaVersion 1.0.0.");
            if (!root.TryGetProperty("models", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return Refuse(out models, out code, out reason, "placed-model-models", "The document needs a models array.");
            var parsed = new List<PlacedModel>(rows.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                if (!TryRow(row, composed, out var model, out code, out reason)) return false;
                if (!ids.Add(model!.Id))
                    return Refuse(out models, out code, out reason, "placed-model-duplicate", "Model id `" + model.Id + "` is claimed more than once.");
                parsed.Add(model);
            }
            models = Array.AsReadOnly(parsed.ToArray());
            return true;
        }
    }

    private static bool TryRow(JsonElement row, bool composed, out PlacedModel? model, out string? code, out string? reason)
    {
        model = null; code = null; reason = null;
        if (row.ValueKind != JsonValueKind.Object)
            return RefuseRow(out model, out code, out reason, "placed-model-json", "A model entry is not an object.");
        if (!KnownOnce(row, out var unknown, "id", "level", "room", "model", "image", "sign",
                "position", "rotation", "scale", "collider", "shotCollider", "text", "textStyle", "content", "collision", "group"))
            return RefuseRow(out model, out code, out reason, "placed-model-field", "Unknown or repeated model field `" + unknown + "`.");
        if (!DocumentReading.Text(row, "id", MaximumIdLength, out var id) || !PlacedModelAddress.IsId(id))
            return RefuseRow(out model, out code, out reason, "placed-model-id", "A model id is trimmed, printable, at most 128 characters and contains no `/`.");
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
            return RefuseRow(out model, out code, out reason, "placed-model-level", "A model needs a level reference of the form `<rundown>:<tier>:<index>`.");
        if (!row.TryGetProperty("room", out var roomRow))
            return RefuseRow(out model, out code, out reason, "placed-model-room", "A model names its room.");
        if (!RoomLocator.TryParse(roomRow, out var room, out _, out var roomReason))
            return RefuseRow(out model, out code, out reason, "placed-model-room", roomReason ?? "A model names its room.");
        PlacedModelPrototype? prototype = null;
        if (row.TryGetProperty("model", out var source))
        {
            if (source.ValueKind != JsonValueKind.Object || !KnownOnce(source, out unknown, "sourcePrefab", "sourcePath")
                || !DocumentReading.Text(source, "sourcePrefab", RoomLocator.MaximumPrefabChars, out var sourcePrefab)
                || !DocumentReading.Prefab(sourcePrefab!)
                || !DocumentReading.Text(source, "sourcePath", RoomBuildRecipe.MaximumTextChars, out var sourcePath)
                || !ForgeCore.PosixPath.IsRelative(sourcePath!))
                return RefuseRow(out model, out code, out reason, "placed-model-prototype", "A model needs an exact native sourcePrefab and LG_PrefabSpawner sourcePath.");
            prototype = new(sourcePrefab!, sourcePath!);
        }
        PlacedModelImage? image = null;
        if (row.TryGetProperty("image", out var imageRow))
        {
            if (imageRow.ValueKind != JsonValueKind.Object
                || !KnownOnce(imageRow, out unknown, "reference", "size", "pngSha256"))
                return RefuseRow(out model, out code, out reason, "placed-model-image",
                    "An image board declares reference, size and pngSha256 exactly once.");
            if (!imageRow.TryGetProperty("reference", out var reference) || reference.ValueKind != JsonValueKind.Object
                || !KnownOnce(reference, out unknown, "id", "revision")
                || !DocumentReading.Text(reference, "id", RoomLocator.MaximumRoomIdChars, out var imageId)
                || !ImageId(imageId!)
                || !DocumentReading.Text(reference, "revision", RoomLocator.RevisionChars, out var revision)
                || !PublishedResources.IsSha256(revision!))
                return RefuseRow(out model, out code, out reason, "placed-model-image-reference",
                    "An image reference is {id: image:<uuid>, revision: <lower-case SHA-256>}.");
            if (!DocumentReading.Numbers(imageRow, "size", 2, 0, MaximumNumber, out var imageSize)
                || imageSize![0] <= 0 || imageSize[1] <= 0
                || (float)imageSize[0] <= 0 || (float)imageSize[1] <= 0)
                return RefuseRow(out model, out code, out reason, "placed-model-image-size",
                    "An image board size has two positive finite metre values representable as Unity floats.");
            if (!DocumentReading.Text(imageRow, "pngSha256", RoomLocator.RevisionChars, out var pngSha256)
                || !PublishedResources.IsSha256(pngSha256!))
                return RefuseRow(out model, out code, out reason, "placed-model-image-png",
                    "An image board names the lower-case SHA-256 of its exported PNG bytes.");
            image = new(new(imageId!, revision!), imageSize[0], imageSize[1], pngSha256!);
        }
        PlacedModelSign? sign = null;
        if (row.TryGetProperty("sign", out var signRow))
        {
            if (signRow.ValueKind != JsonValueKind.Object || !KnownOnce(signRow, out unknown, "size"))
                return RefuseRow(out model, out code, out reason, "placed-model-sign",
                    "A text board declares only size, exactly once.");
            if (!DocumentReading.Numbers(signRow, "size", 2, 0, MaximumNumber, out var signSize)
                || signSize![0] <= 0 || signSize[1] <= 0
                || (float)signSize[0] <= 0 || (float)signSize[1] <= 0)
                return RefuseRow(out model, out code, out reason, "placed-model-sign-size",
                    "A text board size has two positive finite metre values representable as Unity floats.");
            sign = new(signSize[0], signSize[1]);
        }
        JsonElement? content = null;
        if (row.TryGetProperty("content", out var contentRow))
        {
            if (!composed || contentRow.ValueKind != JsonValueKind.Object)
                return RefuseRow(out model, out code, out reason, "placed-model-content", "Flattened model content belongs in composed-models.forge.");
            content = contentRow.Clone();
        }
        bool nativeCollision = false;
        if (row.TryGetProperty("collision", out var collisionRow))
        {
            if (content == null || collisionRow.ValueKind != JsonValueKind.String || collisionRow.GetString() != "native")
                return RefuseRow(out model, out code, out reason, "placed-model-collision",
                    "Model collision is the text `native` and belongs to a composed model.");
            nativeCollision = true;
        }
        if (composed && content == null || (prototype != null ? 1 : 0) + (image != null ? 1 : 0) + (sign != null ? 1 : 0) + (content != null ? 1 : 0) != 1)
            return RefuseRow(out model, out code, out reason, "placed-model-source",
                "A placed model declares exactly one appearance; composed-models.forge requires content.");
        if (!Vector(row, "position", out var position))
            return RefuseRow(out model, out code, out reason, "placed-model-position", "A model position has three finite room-local metre coordinates.");
        if (!DocumentReading.Numbers(row, "rotation", Rotations.Components, -1, 1, out var rotation) || !Rotations.IsUnit(rotation!))
            return RefuseRow(out model, out code, out reason, "placed-model-rotation", "A model rotation is a unit quaternion in x y z w order.");
        if (!Vector(row, "scale", out var scale))
            return RefuseRow(out model, out code, out reason, "placed-model-scale", "A model scale has three finite components.");
        if (!row.TryGetProperty("collider", out var collider) || collider.ValueKind != JsonValueKind.Object
            || !KnownOnce(collider, out unknown, "center", "size") || !Vector(collider, "center", out var center)
            || !Vector(collider, "size", out var size))
            return RefuseRow(out model, out code, out reason, "placed-model-collider", "A model collider has a finite center and size.");
        PlacedModelShotCollider? shotCollider = null;
        if (row.TryGetProperty("shotCollider", out var shot))
        {
            if (shot.ValueKind != JsonValueKind.Object || !KnownOnce(shot, out unknown, "enabled", "center", "size")
                || !shot.TryGetProperty("enabled", out var enabledRow)
                || enabledRow.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !Vector(shot, "center", out var shotCenter)
                || !Vector(shot, "size", out var shotSize)
                || (enabledRow.GetBoolean() && (shotSize![0] <= 0 || shotSize[1] <= 0 || shotSize[2] <= 0
                    || (float)shotSize[0] <= 0 || (float)shotSize[1] <= 0 || (float)shotSize[2] <= 0)))
                return RefuseRow(out model, out code, out reason, "placed-model-shot-collider",
                    "A shot collider declares enabled and finite root-local centre and full edge values; enabled edges are positive Unity floats.");
            shotCollider = new(enabledRow.GetBoolean(), Array.AsReadOnly(shotCenter!), Array.AsReadOnly(shotSize!));
        }
        string? text = null;
        if (row.TryGetProperty("text", out var textRow)
            && (textRow.ValueKind != JsonValueKind.String
                || !TextResourceContract.TryParseReference(text = textRow.GetString(), false, out _)))
            return RefuseRow(out model, out code, out reason, "placed-model-text",
                "Model text is one compiled `forge:text:<id>@<Language>` reference.");
        if (sign != null && text == null)
            return RefuseRow(out model, out code, out reason, "placed-model-text", "A text board needs a compiled text reference.");
        PlacedModelTextStyle? textStyle = null;
        if (row.TryGetProperty("textStyle", out var style))
        {
            if (style.ValueKind != JsonValueKind.Object || !KnownOnce(style, out unknown,
                    "color", "fontSize", "lineWidth", "backgroundColor", "backgroundOpacity", "backgroundPadding"))
                return RefuseRow(out model, out code, out reason, "placed-model-text-style", "Model `" + id + "` textStyle must be an object with only color, fontSize, lineWidth, backgroundColor, backgroundOpacity and backgroundPadding, each once.");
            if (!HexColor(style, "color", out var color) || !HexColor(style, "backgroundColor", out var backgroundColor))
                return RefuseRow(out model, out code, out reason, "placed-model-text-style-color", "Model `" + id + "` text colours must be #RRGGBB.");
            if (!StyleNumber(style, "fontSize", MaximumNumber, out var fontSize))
                return RefuseRow(out model, out code, out reason, "placed-model-text-style-size", "Model `" + id + "` fontSize must be a finite non-negative TextMeshPro size representable as a Unity float.");
            if (!StyleNumber(style, "backgroundOpacity", 1, out var opacity))
                return RefuseRow(out model, out code, out reason, "placed-model-text-style-opacity", "Model `" + id + "` backgroundOpacity must be a finite number from 0 (no quad) to 1 (opaque).");
            double? lineWidth = null;
            if (style.TryGetProperty("lineWidth", out _))
            {
                if (!StyleNumber(style, "lineWidth", MaximumNumber, out var width) || (float)width <= 0)
                    return RefuseRow(out model, out code, out reason, "placed-model-text-style-width", "Model `" + id + "` lineWidth must be a positive finite metre value representable as a Unity float.");
                lineWidth = width;
            }
            double? padding = null;
            if (style.TryGetProperty("backgroundPadding", out _))
            {
                if (!StyleNumber(style, "backgroundPadding", MaximumNumber, out var margin))
                    return RefuseRow(out model, out code, out reason, "placed-model-text-style-padding", "Model `" + id + "` backgroundPadding must be a finite non-negative metre value representable as a Unity float.");
                padding = margin;
            }
            textStyle = new(color!, fontSize, backgroundColor!, opacity, lineWidth, padding);
        }
        if (composed && (size![0] < 0 || size[1] < 0 || size[2] < 0))
            return RefuseRow(out model, out code, out reason, "placed-model-collider", "Collider size cannot be negative.");
        if (!MapObjectGroup.TryRead(row, out var group))
            return RefuseRow(out model, out code, out reason, "placed-model-group", MapObjectGroup.Rule);
        model = new(id!, level, room!, prototype, position!, rotation!, scale!, center!, size!, text,
            textStyle ?? (sign != null ? PlacedModelTextStyle.SignDefault : null), image, sign, content, shotCollider, nativeCollision, group);
        return true;
    }
    private static bool ImageId(string value)
    {
        const string prefix = "image:";
        return value.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(value[prefix.Length..], "D", out var id)
            && value == prefix + id.ToString("D");
    }
    private static bool HexColor(JsonElement row, string field, out string? value)
        => DocumentReading.Text(row, field, out value) && Regex.IsMatch(value!, "\\A#[0-9a-fA-F]{6}\\z");
    private static bool StyleNumber(JsonElement row, string field, double maximum, out double value)
    {
        value = 0;
        return row.TryGetProperty(field, out var number) && number.ValueKind == JsonValueKind.Number
            && number.TryGetDouble(out value) && double.IsFinite(value) && value >= 0 && value <= maximum;
    }
    private static bool Vector(JsonElement row, string field, out double[]? value)
        => DocumentReading.Numbers(row, field, 3, -MaximumNumber, MaximumNumber, out value);
    private static bool KnownOnce(JsonElement row, out string? invalid, params string[] allowed)
    {
        var known = new HashSet<string>(allowed, StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in row.EnumerateObject()) if (!known.Contains(property.Name) || !seen.Add(property.Name)) { invalid = property.Name; return false; }
        invalid = null; return true;
    }
    private static bool Refuse(out IReadOnlyList<PlacedModel>? models, out string? code, out string? reason, string refused, string message)
    { models = null; code = refused; reason = message; return false; }
    private static bool RefuseRow(out PlacedModel? model, out string? code, out string? reason, string refused, string message)
    { model = null; code = refused; reason = message; return false; }
}

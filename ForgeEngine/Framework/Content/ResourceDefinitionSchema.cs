using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeEngine.Framework;

internal sealed class ResourceDefinitionException : Exception
{
    internal ResourceDefinitionException(string code, string detail, IReadOnlyList<object>? path = null,
        JsonElement? reference = null, JsonElement? root = null, IReadOnlyList<JsonElement>? chain = null)
        : base(code + ": " + detail)
    {
        Code = code;
        Path = Array.AsReadOnly((path ?? Array.Empty<object>()).ToArray());
        Reference = reference is { } referenceValue ? referenceValue.Clone() : null;
        Root = root is { } rootValue ? rootValue.Clone() : null;
        Chain = Array.AsReadOnly((chain ?? Array.Empty<JsonElement>()).Select(value => value.Clone()).ToArray());
    }

    internal string Code { get; }
    internal IReadOnlyList<object> Path { get; }
    internal JsonElement? Reference { get; }
    internal JsonElement? Root { get; }
    internal IReadOnlyList<JsonElement> Chain { get; }
}

internal static partial class ResourceDefinitions
{
    private const long SchemaMaximumSafeInteger = 9007199254740991;
    private const int SchemaMaximumParts = 65536;
    private const string SchemaNativePrefix = "forge.native.asset:";
    private static readonly object[] SchemaNoPath = Array.Empty<object>();
    private static readonly string[] SchemaDomains =
        { "map", "room", "enemy", "weapon", "tool", "consumable", "player", "session", "logic", "editor" };
    private static readonly string[] SchemaModelTierNames = { "original", "low" };
    private static readonly string[] SchemaAssetRoots =
    {
        "assets/models", "assets/textures", "assets/audio", "assets/animations", "assets/poses",
        "assets/thumbnails", "assets/vendor", "assets/downloads", "assets/imports", "assets/pixel",
        "data/catalogs", "data/maps", "data/workshop"
    };
    private static readonly Regex SchemaHash = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex SchemaPackagePin = new(
        "^[A-Za-z0-9_]+-[A-Za-z0-9_]+-(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SchemaPathId = new("^(?:0|-?[1-9][0-9]*)$", RegexOptions.CultureInvariant);
    private static readonly Regex SchemaNativeId = new("^forge\\.native\\.asset:[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex SchemaGeometrySemantic = new(
        "^(?:POSITION|NORMAL|TANGENT|INDICES|(?:TEXCOORD|COLOR|JOINTS|WEIGHTS)_[0-9]+)$",
        RegexOptions.CultureInvariant);

    internal static void Validate(JsonElement value)
    {
        SchemaValidateJson(value, 0);

        var rootFields = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("composition", out _)
            ? new[] { "schemaVersion", "reference", "source", "domains", "dependencies", "preview", "hierarchy", "previewParts", "blockers", "composition", "locator", "description" }
            : new[] { "schemaVersion", "reference", "source", "domains", "dependencies", "preview", "hierarchy", "previewParts", "blockers", "locator", "description" };
        SchemaObject(value, rootFields, SchemaNoPath);
        SchemaDemand(SchemaString(value, "schemaVersion", SchemaNoPath) == ForgeCore.FormatVersion.Current,
            "Unsupported offline resource schema", new object[] { "schemaVersion" });
        try { SchemaValidateBase(value); }
        catch (ResourceDefinitionException error)
        { throw new ResourceDefinitionException("resource-invalid", SchemaDetail(error), SchemaNoPath); }

        var locator = value.GetProperty("locator");
        if (locator.ValueKind != JsonValueKind.Null)
        {
            ReadLocator(locator);
            SchemaDemand(SchemaString(value.GetProperty("source"), "kind", SchemaNoPath) == "native",
                "Only native sources have a locator", new object[] { "locator" });
        }
        var reference = value.GetProperty("reference");
        var id = SchemaString(reference, "id", SchemaNoPath);
        if (id.StartsWith(SchemaNativePrefix, StringComparison.Ordinal))
            SchemaDemand(locator.ValueKind != JsonValueKind.Null && SchemaNativeId.IsMatch(id),
                "Invalid derived native identity", new object[] { "reference", "id" });
        SchemaValidateDescription(value);
    }

    internal static void Verify(JsonElement value)
    {
        Validate(value);
        var reference = value.GetProperty("reference");
        var locator = value.GetProperty("locator");
        var id = SchemaBaseText(reference.GetProperty("id"), SchemaNoPath);
        if (id.StartsWith(SchemaNativePrefix, StringComparison.Ordinal) && locator.ValueKind != JsonValueKind.Null)
            VerifyNativeReference(reference, locator);
        if (SchemaString(value.GetProperty("source"), "kind", SchemaNoPath) == "author"
            && SchemaString(reference, "revision", SchemaNoPath) != HashAuthored(value))
            throw new ResourceDefinitionException("resource-definition-conflict",
                "Authored content does not match its locked revision", new object[] { "reference", "revision" }, reference);
    }

    internal static (string Id, string Revision) ReadReference(JsonElement value)
    {
        SchemaObject(value, new[] { "id", "revision" }, SchemaNoPath);
        var id = SchemaBaseText(value.GetProperty("id"), SchemaNoPath);
        var revision = SchemaString(value, "revision", SchemaNoPath);
        SchemaDemand(id.Length <= 512 && id.All(character => !SchemaEcmaWhitespace(character)
                && character is not '/' and not '\\') && SchemaHash.IsMatch(revision),
            "Invalid locked resource reference", SchemaNoPath);
        return (id, revision);
    }

    internal static (string Namespace, string Loader, string? Bundle, string ResourceName) ReadLocator(JsonElement value)
    {
        var path = new object[] { "locator" };
        SchemaObject(value, new[] { "namespace", "loader", "bundle", "resourceName" }, path,
            "Expected a native locator", "Unexpected native locator fields");
        var ns = SchemaScalarText(value.GetProperty("namespace"), SchemaAdd(path, "namespace"));
        var loader = SchemaScalarText(value.GetProperty("loader"), SchemaAdd(path, "loader"));
        var name = SchemaScalarText(value.GetProperty("resourceName"), SchemaAdd(path, "resourceName"));
        var bundleValue = value.GetProperty("bundle");
        string? bundle = bundleValue.ValueKind == JsonValueKind.Null ? null
            : SchemaScalarText(bundleValue, SchemaAdd(path, "bundle"));
        return (ns, loader, bundle, name);
    }

    internal static string DeriveNativeId(JsonElement locator)
    {
        var value = ReadLocator(locator);
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("forge.native.asset\0"));
        foreach (var field in new[] { value.Namespace, value.Loader, value.Bundle, value.ResourceName })
        {
            if (field == null) stream.Write(new byte[] { 255, 255, 255, 255 });
            else
            {
                var bytes = Encoding.UTF8.GetBytes(field);
                stream.WriteByte((byte)(bytes.Length >> 24));
                stream.WriteByte((byte)(bytes.Length >> 16));
                stream.WriteByte((byte)(bytes.Length >> 8));
                stream.WriteByte((byte)bytes.Length);
                stream.Write(bytes);
            }
        }
        return SchemaNativePrefix + ForgeCore.Hash.Sha256Hex(stream.ToArray());
    }

    internal static void VerifyNativeReference(JsonElement reference, JsonElement locator)
    {
        var locked = ReadReference(reference);
        if (locked.Id != DeriveNativeId(locator))
            throw new ResourceDefinitionException("resource-locator-conflict",
                "Derived ID does not match the complete locator", new object[] { "locator" }, reference);
    }

    internal static string Canonical(JsonElement value)
    {
        SchemaValidateJson(value, 0);
        var text = new StringBuilder();
        SchemaAppendCanonical(text, value, null);
        return text.ToString();
    }

    internal static string HashAuthored(JsonElement value)
    {
        SchemaDemand(value.ValueKind == JsonValueKind.Object && value.TryGetProperty("source", out var source)
            && source.ValueKind == JsonValueKind.Object && source.TryGetProperty("kind", out var kind)
            && kind.ValueKind == JsonValueKind.String
            && SchemaReadString(kind, new object[] { "source" }, "Expected an authored source") == "author",
            "Expected an authored source", new object[] { "source" });
        var text = new StringBuilder();
        SchemaAppendAuthored(text, value);
        return ForgeCore.Hash.Sha256Hex(text.ToString());
    }

    private static void SchemaValidateBase(JsonElement value)
    {
        SchemaDemand(SchemaString(value, "schemaVersion", SchemaNoPath) == ForgeCore.FormatVersion.Current,
            "Unsupported resource schema", SchemaNoPath);
        ReadReference(value.GetProperty("reference"));
        var reference = value.GetProperty("reference");
        var source = value.GetProperty("source");
        SchemaValidateSource(source, reference);

        var domains = SchemaBaseStrings(value.GetProperty("domains"), SchemaNoPath);
        SchemaDemand(domains.Length > 0 && domains.All(domain => SchemaDomains.Contains(domain, StringComparer.Ordinal)),
            "Unknown resource domain", SchemaNoPath);
        var dependencies = SchemaBaseStrings(value.GetProperty("dependencies"), SchemaNoPath);
        SchemaDemand(dependencies.All(SchemaPackagePin.IsMatch), "Resource dependency is not exactly pinned", SchemaNoPath);

        var blockers = value.GetProperty("blockers");
        SchemaObject(blockers, new[] { "preview", "runtime" }, SchemaNoPath);
        SchemaBaseStrings(blockers.GetProperty("preview"), SchemaNoPath);
        var runtimeBlockers = SchemaBaseStrings(blockers.GetProperty("runtime"), SchemaNoPath);
        if (SchemaString(source, "kind", SchemaNoPath) != "package")
            SchemaDemand(runtimeBlockers.Contains("native-resource-binding-unverified", StringComparer.Ordinal),
                "Unbound resource must retain its runtime blocker", SchemaNoPath);

        if (value.TryGetProperty("composition", out var composition))
        {
            SchemaValidateComposition(composition, SchemaNoPath);
            SchemaDemand(domains.Contains(SchemaString(composition, "domain", SchemaNoPath), StringComparer.Ordinal),
                "Resource composition domain mismatch", SchemaNoPath);
        }
        var preview = value.GetProperty("preview");
        if (preview.ValueKind != JsonValueKind.Null) SchemaValidateBytes(preview, SchemaNoPath);
        var hierarchy = value.GetProperty("hierarchy");
        if (hierarchy.ValueKind != JsonValueKind.Null) SchemaValidateHierarchy(hierarchy);
        var previewParts = value.GetProperty("previewParts");
        if (previewParts.ValueKind != JsonValueKind.Null) SchemaValidatePreviewParts(previewParts);
    }

    private static void SchemaValidateSource(JsonElement source, JsonElement reference)
    {
        JsonElement kindValue = default;
        SchemaDemand(source.ValueKind == JsonValueKind.Object && source.TryGetProperty("kind", out kindValue)
            && kindValue.ValueKind == JsonValueKind.String, "Invalid resource source", SchemaNoPath);
        var kind = SchemaReadString(kindValue, SchemaNoPath, "Invalid resource source");
        var revision = SchemaString(reference, "revision", SchemaNoPath);
        switch (kind)
        {
            case "package":
                SchemaObject(source, new[] { "kind", "packagePin", "archiveSha256", "bundle", "assetPath", "object" }, SchemaNoPath);
                var archive = SchemaString(source, "archiveSha256", SchemaNoPath);
                SchemaDemand(SchemaPackagePin.IsMatch(SchemaString(source, "packagePin", SchemaNoPath))
                    && SchemaHash.IsMatch(archive)
                    && SchemaResourcePath(source.GetProperty("bundle"))
                    && SchemaResourcePath(source.GetProperty("assetPath")), "Invalid resource source", SchemaNoPath);
                SchemaDemand(revision == archive, "Imported resource revision must lock its original archive", SchemaNoPath);
                SchemaValidateSourceObject(source.GetProperty("object"), SchemaNoPath);
                break;
            case "native":
                SchemaObject(source, new[] { "kind", "sourceId", "contentSha256", "evidence", "assetPath", "object" }, SchemaNoPath);
                var evidence = source.GetProperty("evidence");
                SchemaObject(evidence, new[] { "path", "sha256" }, SchemaNoPath);
                var content = SchemaString(source, "contentSha256", SchemaNoPath);
                SchemaDemand(SchemaBaseText(source.GetProperty("sourceId"), SchemaNoPath).Length > 0
                    && SchemaResourcePath(source.GetProperty("assetPath"))
                    && SchemaResourcePath(evidence.GetProperty("path"))
                    && SchemaHash.IsMatch(SchemaString(evidence, "sha256", SchemaNoPath)),
                    "Invalid native source evidence", SchemaNoPath);
                SchemaDemand(SchemaHash.IsMatch(content) && revision == content,
                    "Native resource revision must lock source content", SchemaNoPath);
                if (source.GetProperty("object").ValueKind != JsonValueKind.Null)
                    SchemaValidateSourceObject(source.GetProperty("object"), SchemaNoPath);
                break;
            case "author":
                SchemaObject(source, new[] { "kind", "documentId", "contentSha256" }, SchemaNoPath);
                var id = SchemaBaseText(reference.GetProperty("id"), SchemaNoPath);
                var document = SchemaBaseText(source.GetProperty("documentId"), SchemaNoPath);
                var authored = SchemaString(source, "contentSha256", SchemaNoPath);
                SchemaDemand(document == id && SchemaHash.IsMatch(authored) && revision == authored,
                    "Authored resource revision must lock its document", SchemaNoPath);
                SchemaDemand(!id!.StartsWith("forge.native.", StringComparison.Ordinal)
                    && !id.StartsWith("forge.native:", StringComparison.Ordinal)
                    && !id.StartsWith("forge.imported:", StringComparison.Ordinal),
                    "Authored resource cannot use a reserved source identity", SchemaNoPath);
                break;
            default: throw SchemaReject("Invalid resource source", SchemaNoPath);
        }
    }

    private static void SchemaValidateComposition(JsonElement value, IReadOnlyList<object> path)
    {
        SchemaObject(value, new[] { "schemaVersion", "domain", "space", "instances" }, path);
        var domain = SchemaString(value, "domain", path);
        SchemaDemand(SchemaString(value, "schemaVersion", path) == ForgeCore.FormatVersion.Current
            && SchemaDomains.Contains(domain, StringComparer.Ordinal)
            && SchemaString(value, "space", path) == "unity-left-handed-meters",
            "Unsupported resource composition", path);
        var instances = value.GetProperty("instances");
        SchemaDemand(instances.ValueKind == JsonValueKind.Array, "Missing resource instances", path);
        var rows = SchemaParentRows(instances, domain is "room" or "map" ? 65536 : 8192, true,
            new[] { "id", "parent", "resource", "local" }, path);
        for (var index = 0; index < rows.Length; index++)
        {
            ReadReference(rows[index].GetProperty("resource"));
            SchemaValidateTransform(rows[index].GetProperty("local"), path, false);
        }
    }

    private static void SchemaValidateHierarchy(JsonElement value)
    {
        SchemaObject(value, new[] { "space", "evidence", "nodes" }, SchemaNoPath);
        SchemaDemand(SchemaString(value, "space", SchemaNoPath) == "unity-left-handed-meters",
            "Unsupported source coordinate space", SchemaNoPath);
        var evidence = value.GetProperty("evidence");
        SchemaObject(evidence, new[] { "path", "sha256" }, SchemaNoPath);
        SchemaDemand(SchemaResourcePath(evidence.GetProperty("path"))
            && SchemaHash.IsMatch(SchemaString(evidence, "sha256", SchemaNoPath)),
            "Missing source hierarchy evidence", SchemaNoPath);
        var nodes = SchemaParentRows(value.GetProperty("nodes"), 16384, false,
            new[] { "id", "parent", "name", "source", "gameObject", "local", "active", "components", "scripts" }, SchemaNoPath);
        SchemaDemand(nodes.Count(node => node.GetProperty("parent").ValueKind == JsonValueKind.Null) == 1,
            "Imported prefab must have one source root", SchemaNoPath);
        foreach (var node in nodes)
        {
            SchemaBaseText(node.GetProperty("name"), SchemaNoPath);
            SchemaDemand(node.GetProperty("active").ValueKind is JsonValueKind.True or JsonValueKind.False,
                "Invalid resource node metadata", SchemaNoPath);
            SchemaValidateSourceObject(node.GetProperty("source"), SchemaNoPath);
            SchemaValidateSourceObject(node.GetProperty("gameObject"), SchemaNoPath);
            SchemaValidateTransform(node.GetProperty("local"), SchemaNoPath, false);
            SchemaBaseStrings(node.GetProperty("components"), SchemaNoPath);
            SchemaBaseStrings(node.GetProperty("scripts"), SchemaNoPath);
        }
    }

    private static void SchemaValidatePreviewParts(JsonElement value)
    {
        SchemaObject(value, new[] { "space", "coverage", "parts" }, SchemaNoPath);
        var parts = value.GetProperty("parts");
        SchemaDemand(SchemaString(value, "space", SchemaNoPath) == "gltf-right-handed-meters"
            && SchemaString(value, "coverage", SchemaNoPath) is "partial" or "complete"
            && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() <= 32768,
            "Invalid preview part projection", SchemaNoPath);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts.EnumerateArray())
        {
            SchemaObject(part, new[] { "id", "topology", "matrix", "source", "materialSlots", "geometry", "material" }, SchemaNoPath);
            var id = SchemaBaseText(part.GetProperty("id"), SchemaNoPath);
            SchemaDemand(ids.Add(id), "Duplicate preview part", SchemaNoPath);
            SchemaDemand(SchemaString(part, "topology", SchemaNoPath) == "triangles",
                "Unsupported preview primitive topology", SchemaNoPath);
            var matrix = SchemaVector(part.GetProperty("matrix"), 16, SchemaNoPath);
            SchemaDemand(matrix[3] == 0 && matrix[7] == 0 && matrix[11] == 0 && matrix[15] == 1,
                "Invalid affine preview matrix", SchemaNoPath);
            SchemaValidateSourceObject(part.GetProperty("source"), SchemaNoPath);
            var materialSlots = part.GetProperty("materialSlots");
            SchemaDemand(materialSlots.ValueKind == JsonValueKind.Array, "Missing source material slots", SchemaNoPath);
            foreach (var slot in materialSlots.EnumerateArray()) SchemaValidateSourceObject(slot, SchemaNoPath);

            var material = part.GetProperty("material");
            SchemaObject(material, new[] { "sha256", "description" }, SchemaNoPath);
            SchemaDemand(SchemaHash.IsMatch(SchemaString(material, "sha256", SchemaNoPath))
                && SchemaBaseText(material.GetProperty("description"), SchemaNoPath).Length > 0,
                "Invalid material identity", SchemaNoPath);

            var geometry = part.GetProperty("geometry");
            SchemaDemand(geometry.ValueKind == JsonValueKind.Array && geometry.GetArrayLength() is > 0 and <= 32,
                "Invalid geometry attributes", SchemaNoPath);
            var semantics = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attribute in geometry.EnumerateArray())
            {
                SchemaObject(attribute, new[] { "semantic", "componentType", "shape", "count", "normalized", "byteStride", "data" }, SchemaNoPath);
                var semantic = SchemaString(attribute, "semantic", SchemaNoPath);
                SchemaDemand(SchemaGeometrySemantic.IsMatch(semantic) && semantics.Add(semantic),
                    "Unsupported or duplicate geometry attribute", SchemaNoPath);
                var componentType = SchemaInteger(attribute.GetProperty("componentType"), SchemaNoPath);
                var componentSize = componentType switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => 0 };
                var componentWidth = SchemaString(attribute, "shape", SchemaNoPath) switch
                { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => 0 };
                var count = SchemaInteger(attribute.GetProperty("count"), SchemaNoPath, 1);
                var stride = SchemaInteger(attribute.GetProperty("byteStride"), SchemaNoPath);
                SchemaDemand(componentSize != 0 && componentWidth != 0
                    && attribute.GetProperty("normalized").ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "Invalid geometry accessor", SchemaNoPath);
                var element = componentSize * componentWidth;
                SchemaDemand(stride >= element && stride % componentSize == 0, "Invalid geometry stride", SchemaNoPath);
                var data = attribute.GetProperty("data");
                SchemaValidateBytes(data, SchemaNoPath);
                SchemaDemand(new BigInteger(SchemaInteger(data.GetProperty("bytes"), SchemaNoPath, 1))
                    == (new BigInteger(count) - 1) * stride + element,
                    "Geometry interval does not match accessor", SchemaNoPath);
            }
        }
    }

    private static void SchemaValidateDescription(JsonElement definition)
    {
        var path = new object[] { "description" };
        var value = definition.GetProperty("description");
        JsonElement kindValue = default;
        SchemaDemand(value.ValueKind == JsonValueKind.Object && value.TryGetProperty("kind", out kindValue)
            && kindValue.ValueKind == JsonValueKind.String, "Expected a typed resource description", path);
        var kind = SchemaReadString(kindValue, SchemaAdd(path, "kind"), "Unknown resource description kind");
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var hierarchy = definition.GetProperty("hierarchy");
        if (hierarchy.ValueKind != JsonValueKind.Null)
            foreach (var node in hierarchy.GetProperty("nodes").EnumerateArray())
                nodes.Add(SchemaBaseText(node.GetProperty("id"), SchemaNoPath));
        switch (kind)
        {
            case "composition":
                SchemaObject(value, new[] { "kind" }, path);
                SchemaDemand(SchemaString(definition.GetProperty("source"), "kind", path) == "author"
                    && definition.TryGetProperty("composition", out _),
                    "A composition must be an authored locked instance document", path);
                break;
            case "model": SchemaValidateModelDescription(definition, value, nodes, path); break;
            case "material": SchemaValidateMaterialDescription(value, path); break;
            case "texture":
                SchemaObject(value, new[] { "kind", "data", "mime", "width", "height" }, path);
                SchemaValidateBytes(value.GetProperty("data"), SchemaAdd(path, "data"));
                SchemaChoice(value.GetProperty("mime"), new[] { "image/png", "image/jpeg", "image/ktx2" }, SchemaAdd(path, "mime"));
                SchemaInteger(value.GetProperty("width"), SchemaAdd(path, "width"), 1);
                SchemaInteger(value.GetProperty("height"), SchemaAdd(path, "height"), 1);
                break;
            case "rig": SchemaValidateRigDescription(value, path); break;
            case "animation": SchemaValidateAnimationDescription(value, path); break;
            case "collision": SchemaValidateCollisionDescription(value, path); break;
            case "audio":
                SchemaObject(value, new[] { "kind", "data", "codec", "sampleRate", "channels" }, path);
                SchemaValidateBytes(value.GetProperty("data"), SchemaAdd(path, "data"));
                SchemaChoice(value.GetProperty("codec"), new[] { "wav", "flac", "ogg" }, SchemaAdd(path, "codec"));
                SchemaInteger(value.GetProperty("sampleRate"), SchemaAdd(path, "sampleRate"), 1);
                SchemaInteger(value.GetProperty("channels"), SchemaAdd(path, "channels"), 1);
                break;
            default: throw SchemaReject("Unknown resource description kind", SchemaAdd(path, "kind"));
        }
        if (kind != "composition")
            SchemaDemand(!definition.TryGetProperty("composition", out _),
                "Only composition descriptions contain instances", path);
    }

    private static void SchemaValidateModelDescription(JsonElement definition, JsonElement value,
        HashSet<string> nodes, IReadOnlyList<object> path)
    {
        SchemaObject(value, new[] { "kind", "variants", "sockets", "collisionParts", "hitParts" }, path);
        SchemaDemand(!definition.TryGetProperty("composition", out _), "A model cannot also contain a composition", path);
        var variants = value.GetProperty("variants");
        SchemaObject(variants, SchemaModelTierNames, SchemaAdd(path, "variants"));
        var revision = SchemaString(definition.GetProperty("reference"), "revision", path);
        var native = SchemaString(definition.GetProperty("source"), "kind", path) == "native";
        foreach (var tier in SchemaModelTierNames)
        {
            var variant = variants.GetProperty(tier);
            var variantPath = SchemaAdd(SchemaAdd(path, "variants"), tier);
            JsonElement available = default;
            SchemaDemand(variant.ValueKind == JsonValueKind.Object && variant.TryGetProperty("available", out available),
                "Invalid variant", variantPath);
            if (available.ValueKind == JsonValueKind.False)
            {
                SchemaObject(variant, new[] { "available", "reason" }, variantPath);
                SchemaScalarText(variant.GetProperty("reason"), SchemaAdd(variantPath, "reason"));
                continue;
            }
            SchemaObject(variant, new[] { "available", "payload", "selection", "materials", "skin", "animations" }, variantPath);
            SchemaDemand(available.ValueKind == JsonValueKind.True, "Variant availability must be explicit", variantPath);
            var payload = variant.GetProperty("payload");
            var payloadPath = SchemaAdd(variantPath, "payload");
            JsonElement payloadKind = default;
            SchemaDemand(payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("kind", out payloadKind)
                && payloadKind.ValueKind == JsonValueKind.String, "Missing model payload", payloadPath);
            var payloadName = SchemaReadString(payloadKind, payloadPath, "Invalid model payload");
            if (payloadName == "native")
            {
                SchemaObject(payload, new[] { "kind" }, payloadPath);
                SchemaDemand(tier == "original" && native, "Only Original may use a native root", payloadPath);
            }
            else
            {
                SchemaObject(payload, new[] { "kind", "data" }, payloadPath);
                SchemaDemand(payloadName == "bytes", "Invalid model payload", payloadPath);
                SchemaDemand(tier != "original" || !native,
                    "Native Original requires the original locator, not website geometry", payloadPath);
                SchemaValidateBytes(payload.GetProperty("data"), SchemaAdd(payloadPath, "data"));
            }
            var selection = variant.GetProperty("selection");
            SchemaValidateSelection(selection, revision, SchemaAdd(variantPath, "selection"));
            if (selection.ValueKind != JsonValueKind.Null)
                SchemaDemand(native, "Only a native model has a native selection", SchemaAdd(variantPath, "selection"));

            var materials = SchemaDescriptionList(variant.GetProperty("materials"), SchemaAdd(variantPath, "materials"));
            var materialKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < materials.Length; index++)
            {
                var row = materials[index];
                var rowPath = SchemaAdd(SchemaAdd(variantPath, "materials"), index);
                SchemaObject(row, new[] { "slot", "renderer", "source", "material" }, rowPath);
                var slot = SchemaScalarText(row.GetProperty("slot"), SchemaAdd(rowPath, "slot"));
                var renderer = SchemaNode(row.GetProperty("renderer"), nodes, SchemaAdd(rowPath, "renderer"));
                if (row.GetProperty("source").ValueKind != JsonValueKind.Null)
                    SchemaValidateSourceObject(row.GetProperty("source"), SchemaAdd(rowPath, "source"));
                SchemaValidateReference(row.GetProperty("material"), SchemaAdd(rowPath, "material"));
                SchemaDemand(materialKeys.Add(renderer + "\0" + slot), "Duplicate key", SchemaAdd(variantPath, "materials"));
            }

            var skin = variant.GetProperty("skin");
            if (skin.ValueKind != JsonValueKind.Null)
            {
                var skinPath = SchemaAdd(variantPath, "skin");
                SchemaObject(skin, new[] { "rig", "bones", "evidence" }, skinPath);
                SchemaValidateReference(skin.GetProperty("rig"), SchemaAdd(skinPath, "rig"));
                SchemaValidateEvidence(skin.GetProperty("evidence"), SchemaAdd(skinPath, "evidence"));
                var bones = SchemaDescriptionList(skin.GetProperty("bones"), SchemaAdd(skinPath, "bones"));
                SchemaDemand(bones.Length > 0, "Skin mapping cannot be empty", SchemaAdd(skinPath, "bones"));
                var skinNodes = new HashSet<string>(StringComparer.Ordinal);
                var skinBones = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < bones.Length; index++)
                {
                    var bone = bones[index];
                    var bonePath = SchemaAdd(SchemaAdd(skinPath, "bones"), index);
                    SchemaObject(bone, new[] { "node", "bone" }, bonePath);
                    var node = SchemaNode(bone.GetProperty("node"), nodes, SchemaAdd(bonePath, "node"));
                    var boneName = SchemaScalarText(bone.GetProperty("bone"), SchemaAdd(bonePath, "bone"));
                    SchemaDemand(skinNodes.Add(node), "Duplicate key", SchemaAdd(skinPath, "bones"));
                    SchemaDemand(skinBones.Add(boneName), "Duplicate key", SchemaAdd(skinPath, "bones"));
                }
            }
            var animations = SchemaDescriptionList(variant.GetProperty("animations"), SchemaAdd(variantPath, "animations"));
            var animationKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < animations.Length; index++)
            {
                var animation = animations[index];
                SchemaValidateReference(animation, SchemaAdd(SchemaAdd(variantPath, "animations"), index));
                var locked = SchemaReferenceValues(animation);
                SchemaDemand(animationKeys.Add(locked.Id + "\0" + locked.Revision), "Duplicate key", SchemaAdd(variantPath, "animations"));
            }
            SchemaDemand(animations.Length == 0 || skin.ValueKind != JsonValueKind.Null,
                "Animated models require an explicit rig mapping", SchemaAdd(variantPath, "animations"));
        }

        var sockets = SchemaDescriptionList(value.GetProperty("sockets"), SchemaAdd(path, "sockets"));
        var socketIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sockets.Length; index++)
        {
            var socket = sockets[index];
            var socketPath = SchemaAdd(SchemaAdd(path, "sockets"), index);
            SchemaObject(socket, new[] { "id", "node", "position", "rotation" }, socketPath);
            var id = SchemaScalarText(socket.GetProperty("id"), SchemaAdd(socketPath, "id"));
            SchemaNode(socket.GetProperty("node"), nodes, SchemaAdd(socketPath, "node"));
            SchemaValidatePartialTransform(socket.GetProperty("position"), socket.GetProperty("rotation"), socketPath);
            SchemaDemand(socketIds.Add(id), "Duplicate key", SchemaAdd(path, "sockets"));
        }

        var collisionParts = SchemaDescriptionList(value.GetProperty("collisionParts"), SchemaAdd(path, "collisionParts"));
        var collisions = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < collisionParts.Length; index++)
        {
            var part = collisionParts[index];
            var partPath = SchemaAdd(SchemaAdd(path, "collisionParts"), index);
            SchemaObject(part, new[] { "id", "node", "shape", "local" }, partPath);
            var id = SchemaScalarText(part.GetProperty("id"), SchemaAdd(partPath, "id"));
            SchemaNode(part.GetProperty("node"), nodes, SchemaAdd(partPath, "node"));
            SchemaValidateReference(part.GetProperty("shape"), SchemaAdd(partPath, "shape"));
            SchemaValidateTransform(part.GetProperty("local"), SchemaAdd(partPath, "local"), true);
            SchemaDemand(collisions.Add(id), "Duplicate key", SchemaAdd(path, "collisionParts"));
        }
        var hitParts = SchemaDescriptionList(value.GetProperty("hitParts"), SchemaAdd(path, "hitParts"));
        var hitIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < hitParts.Length; index++)
        {
            var part = hitParts[index];
            var partPath = SchemaAdd(SchemaAdd(path, "hitParts"), index);
            SchemaObject(part, new[] { "id", "node", "collisionPart" }, partPath);
            var id = SchemaScalarText(part.GetProperty("id"), SchemaAdd(partPath, "id"));
            SchemaNode(part.GetProperty("node"), nodes, SchemaAdd(partPath, "node"));
            SchemaDemand(part.GetProperty("collisionPart").ValueKind == JsonValueKind.String
                && collisions.Contains(SchemaScalarText(part.GetProperty("collisionPart"), SchemaAdd(partPath, "collisionPart"))),
                "Missing collision part", SchemaAdd(partPath, "collisionPart"));
            SchemaDemand(hitIds.Add(id), "Duplicate key", SchemaAdd(path, "hitParts"));
        }
    }

    private static void SchemaValidateSelection(JsonElement value, string revision, IReadOnlyList<object> path)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        var hasIndices = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("childIndices", out _);
        SchemaObject(value, hasIndices
            ? new[] { "sourceRevision", "childPath", "object", "materialSlots", "childIndices" }
            : new[] { "sourceRevision", "childPath", "object", "materialSlots" }, path);
        SchemaDemand(SchemaString(value, "sourceRevision", path) == revision,
            "Selection must lock source revision", SchemaAdd(path, "sourceRevision"));
        var childPath = SchemaDescriptionList(value.GetProperty("childPath"), SchemaAdd(path, "childPath"));
        for (var index = 0; index < childPath.Length; index++)
            SchemaScalarText(childPath[index], SchemaAdd(SchemaAdd(path, "childPath"), index));
        if (hasIndices)
        {
            var indices = SchemaDescriptionList(value.GetProperty("childIndices"), SchemaAdd(path, "childIndices"));
            SchemaDemand(indices.Length == childPath.Length, "Child indices must match the exact path", path);
            for (var index = 0; index < indices.Length; index++)
                SchemaInteger(indices[index], SchemaAdd(SchemaAdd(path, "childIndices"), index));
        }
        if (value.GetProperty("object").ValueKind != JsonValueKind.Null)
            SchemaValidateSourceObject(value.GetProperty("object"), SchemaAdd(path, "object"));
        var slots = SchemaDescriptionList(value.GetProperty("materialSlots"), SchemaAdd(path, "materialSlots"));
        for (var index = 0; index < slots.Length; index++)
            SchemaValidateSourceObject(slots[index], SchemaAdd(SchemaAdd(path, "materialSlots"), index));
    }

    private static void SchemaValidateMaterialDescription(JsonElement value, IReadOnlyList<object> path)
    {
        SchemaObject(value, new[] { "kind", "workflow", "baseColor", "metallic", "roughness", "emissive", "alphaMode", "alphaCutoff", "doubleSided", "textures" }, path);
        SchemaDemand(SchemaString(value, "workflow", path) == "metallic-roughness"
            && value.GetProperty("doubleSided").ValueKind is JsonValueKind.True or JsonValueKind.False,
            "Invalid PBR workflow", path);
        SchemaVector(value.GetProperty("baseColor"), 4, SchemaAdd(path, "baseColor"), 0, 1);
        SchemaVector(value.GetProperty("emissive"), 3, SchemaAdd(path, "emissive"), 0);
        foreach (var field in new[] { "metallic", "roughness", "alphaCutoff" })
            SchemaFinite(value.GetProperty(field), SchemaAdd(path, field), 0, 1);
        SchemaChoice(value.GetProperty("alphaMode"), new[] { "opaque", "mask", "blend" }, SchemaAdd(path, "alphaMode"));
        var textures = SchemaDescriptionList(value.GetProperty("textures"), SchemaAdd(path, "textures"));
        var channels = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < textures.Length; index++)
        {
            var binding = textures[index];
            var bindingPath = SchemaAdd(SchemaAdd(path, "textures"), index);
            SchemaObject(binding, new[] { "channel", "texture", "colorSpace", "uv", "sampler" }, bindingPath);
            var channel = SchemaChoice(binding.GetProperty("channel"),
                new[] { "baseColor", "metallicRoughness", "normal", "occlusion", "emissive" }, SchemaAdd(bindingPath, "channel"));
            SchemaChoice(binding.GetProperty("colorSpace"),
                new[] { channel is "baseColor" or "emissive" ? "srgb" : "linear" }, SchemaAdd(bindingPath, "colorSpace"));
            SchemaValidateReference(binding.GetProperty("texture"), SchemaAdd(bindingPath, "texture"));
            SchemaInteger(binding.GetProperty("uv"), SchemaAdd(bindingPath, "uv"));
            var sampler = binding.GetProperty("sampler");
            var samplerPath = SchemaAdd(bindingPath, "sampler");
            SchemaObject(sampler, new[] { "wrapS", "wrapT", "min", "mag" }, samplerPath);
            foreach (var wrap in new[] { "wrapS", "wrapT" })
                SchemaChoice(sampler.GetProperty(wrap), new[] { "repeat", "clamp", "mirror" }, SchemaAdd(samplerPath, wrap));
            foreach (var filter in new[] { "min", "mag" })
                SchemaChoice(sampler.GetProperty(filter), new[] { "nearest", "linear" }, SchemaAdd(samplerPath, filter));
            SchemaDemand(channels.Add(channel), "Duplicate key", SchemaAdd(path, "textures"));
        }
    }

    private static void SchemaValidateRigDescription(JsonElement value, IReadOnlyList<object> path)
    {
        SchemaObject(value, new[] { "kind", "bones" }, path);
        var bones = SchemaDescriptionList(value.GetProperty("bones"), SchemaAdd(path, "bones"));
        SchemaDemand(bones.Length > 0, "Rig has no bones", path);
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < bones.Length; index++)
        {
            var bone = bones[index];
            var bonePath = SchemaAdd(SchemaAdd(path, "bones"), index);
            SchemaObject(bone, new[] { "id", "parent", "rest", "inverseBindMatrix" }, bonePath);
            var id = SchemaScalarText(bone.GetProperty("id"), SchemaAdd(bonePath, "id"));
            string? parent = null;
            if (bone.GetProperty("parent").ValueKind != JsonValueKind.Null)
                parent = SchemaScalarText(bone.GetProperty("parent"), SchemaAdd(bonePath, "parent"));
            SchemaDemand(parents.TryAdd(id, parent), "Duplicate bone", SchemaAdd(bonePath, "id"));
            SchemaValidateTransform(bone.GetProperty("rest"), SchemaAdd(bonePath, "rest"), true);
            var matrixPath = SchemaAdd(bonePath, "inverseBindMatrix");
            var matrix = SchemaVector(bone.GetProperty("inverseBindMatrix"), 16, matrixPath);
            var determinant = SchemaDeterminant(matrix);
            SchemaDemand(double.IsFinite(determinant) && determinant != 0,
                "Inverse bind matrix must be invertible", matrixPath);
            SchemaDemand(matrix[3] == 0 && matrix[7] == 0 && matrix[11] == 0 && matrix[15] == 1,
                "Bind matrix must be affine, column-major", matrixPath);
        }
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in parents.Keys)
        {
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = id;
            while (cursor != null && !finished.Contains(cursor))
            {
                SchemaDemand(parents.TryGetValue(cursor, out var parent) && visiting.Add(cursor),
                    "Missing bone parent or rig cycle", SchemaAdd(path, "bones"));
                cursor = parent;
            }
            finished.UnionWith(visiting);
        }
    }

    private static void SchemaValidateAnimationDescription(JsonElement value, IReadOnlyList<object> path)
    {
        SchemaObject(value, new[] { "kind", "rig", "data", "duration", "tracks" }, path);
        SchemaValidateReference(value.GetProperty("rig"), SchemaAdd(path, "rig"));
        SchemaValidateBytes(value.GetProperty("data"), SchemaAdd(path, "data"));
        SchemaFinite(value.GetProperty("duration"), SchemaAdd(path, "duration"), 0);
        var tracks = SchemaDescriptionList(value.GetProperty("tracks"), SchemaAdd(path, "tracks"));
        SchemaDemand(tracks.Length > 0, "Animation has no tracks", SchemaAdd(path, "tracks"));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tracks.Length; index++)
        {
            var track = tracks[index];
            var trackPath = SchemaAdd(SchemaAdd(path, "tracks"), index);
            SchemaObject(track, new[] { "bone", "property" }, trackPath);
            var bone = SchemaScalarText(track.GetProperty("bone"), SchemaAdd(trackPath, "bone"));
            var property = SchemaChoice(track.GetProperty("property"),
                new[] { "translation", "rotation", "scale" }, SchemaAdd(trackPath, "property"));
            SchemaDemand(keys.Add(bone + "\0" + property), "Duplicate key", SchemaAdd(path, "tracks"));
        }
    }

    private static void SchemaValidateCollisionDescription(JsonElement value, IReadOnlyList<object> path)
    {
        SchemaDemand(value.TryGetProperty("shape", out var shapeValue) && shapeValue.ValueKind == JsonValueKind.String,
            "Unknown collision shape", SchemaAdd(path, "shape"));
        switch (SchemaReadString(shapeValue, SchemaAdd(path, "shape"), "Unknown collision shape"))
        {
            case "box":
                SchemaObject(value, new[] { "kind", "shape", "size" }, path);
                var size = SchemaVector(value.GetProperty("size"), 3, SchemaAdd(path, "size"));
                SchemaDemand(size.All(number => number > 0), "Box dimensions must be positive", SchemaAdd(path, "size"));
                break;
            case "sphere":
                SchemaObject(value, new[] { "kind", "shape", "radius" }, path);
                SchemaFinite(value.GetProperty("radius"), SchemaAdd(path, "radius"), double.Epsilon);
                break;
            case "capsule":
                SchemaObject(value, new[] { "kind", "shape", "radius", "height", "axis" }, path);
                var radius = SchemaFinite(value.GetProperty("radius"), SchemaAdd(path, "radius"), double.Epsilon);
                SchemaFinite(value.GetProperty("height"), SchemaAdd(path, "height"), radius * 2);
                SchemaChoice(value.GetProperty("axis"), new[] { "x", "y", "z" }, SchemaAdd(path, "axis"));
                break;
            case "mesh":
                SchemaObject(value, new[] { "kind", "shape", "data", "convex" }, path);
                SchemaValidateBytes(value.GetProperty("data"), SchemaAdd(path, "data"));
                SchemaDemand(value.GetProperty("convex").ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "Convex must be explicit", SchemaAdd(path, "convex"));
                break;
            default: throw SchemaReject("Unknown collision shape", SchemaAdd(path, "shape"));
        }
    }

    private static void SchemaValidateBytes(JsonElement value, IReadOnlyList<object> path)
    {
        var hasRange = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("range", out _);
        SchemaObject(value, hasRange
            ? new[] { "location", "sha256", "bytes", "range" }
            : new[] { "location", "sha256", "bytes" }, path, "Expected bytes");
        var location = value.GetProperty("location");
        var locationPath = SchemaAdd(path, "location");
        SchemaObject(location, new[] { "kind", "path" }, locationPath);
        var kind = SchemaChoice(location.GetProperty("kind"), new[] { "site", "repository-evidence" },
            SchemaAdd(locationPath, "kind"));
        var resourcePath = location.GetProperty("path");
        SchemaDemand(SchemaResourcePath(resourcePath)
            && SchemaHash.IsMatch(SchemaString(value, "sha256", path)),
            "Invalid byte location or digest", path);
        if (kind == "site")
        {
            var text = SchemaBaseText(resourcePath, SchemaAdd(locationPath, "path"));
            SchemaDemand(SchemaAssetRoots.Any(root => text.StartsWith(root + "/", StringComparison.Ordinal))
                || text.StartsWith("api/", StringComparison.Ordinal),
                "Unsupported site resource location", SchemaAdd(locationPath, "path"));
        }
        var bytes = SchemaInteger(value.GetProperty("bytes"), SchemaAdd(path, "bytes"), 1);
        if (!hasRange) return;
        var range = value.GetProperty("range");
        var rangePath = SchemaAdd(path, "range");
        SchemaObject(range, new[] { "offset", "fileSha256" }, rangePath);
        var offset = SchemaInteger(range.GetProperty("offset"), SchemaAdd(rangePath, "offset"));
        SchemaDemand(offset <= SchemaMaximumSafeInteger - bytes
            && SchemaHash.IsMatch(SchemaString(range, "fileSha256", rangePath)),
            "Invalid byte interval", path);
    }

    private static void SchemaValidateSourceObject(JsonElement value, IReadOnlyList<object> path)
    {
        SchemaObject(value, new[] { "file", "pathId" }, path);
        var file = SchemaBaseText(value.GetProperty("file"), path);
        var pathId = SchemaString(value, "pathId", path);
        SchemaDemand(SchemaResourcePath(value.GetProperty("file")) && file.IndexOf('/') < 0,
            "Invalid Unity source file", path);
        SchemaDemand(pathId.Length <= 20 && SchemaPathId.IsMatch(pathId),
            "Unity pathId must be an exact decimal string", path);
        SchemaDemand(long.TryParse(pathId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            "Unity pathId exceeds int64", path);
    }

    private static void SchemaValidateReference(JsonElement value, IReadOnlyList<object> path)
    {
        SchemaObject(value, new[] { "id", "revision" }, path);
        var id = SchemaBaseText(value.GetProperty("id"), path);
        var revision = SchemaString(value, "revision", path);
        SchemaDemand(id.Length <= 512 && id.All(character => !SchemaEcmaWhitespace(character)
                && character is not '/' and not '\\') && SchemaHash.IsMatch(revision),
            "Invalid locked resource reference", path);
    }

    private static (string Id, string Revision) SchemaReferenceValues(JsonElement value)
        => (SchemaBaseText(value.GetProperty("id"), SchemaNoPath),
            SchemaBaseText(value.GetProperty("revision"), SchemaNoPath));

    private static void SchemaValidateTransform(JsonElement value, IReadOnlyList<object> path, bool float32)
    {
        double[] position, rotation, scale;
        try
        {
            SchemaObject(value, new[] { "position", "rotation", "scale" }, path);
            position = SchemaVector(value.GetProperty("position"), 3, path);
            rotation = SchemaVector(value.GetProperty("rotation"), 4, path);
            scale = SchemaVector(value.GetProperty("scale"), 3, path);
        }
        catch (ResourceDefinitionException error) { throw SchemaReject(SchemaDetail(error), path); }
        SchemaDemand(Math.Abs(SchemaHypot(rotation) - 1) <= 0.0001,
            "Resource quaternion is not normalized", path);
        if (float32)
            SchemaDemand(position.Concat(rotation).Concat(scale).All(number => float.IsFinite((float)number)),
                "Native transform exceeds float32", path);
    }

    private static void SchemaValidatePartialTransform(JsonElement positionValue, JsonElement rotationValue,
        IReadOnlyList<object> path)
    {
        var position = SchemaVector(positionValue, 3, path);
        var rotation = SchemaVector(rotationValue, 4, path);
        SchemaDemand(Math.Abs(SchemaHypot(rotation) - 1) <= 0.0001,
            "Resource quaternion is not normalized", path);
        SchemaDemand(position.Concat(rotation).All(number => float.IsFinite((float)number)),
            "Native transform exceeds float32", path);
    }

    private static void SchemaValidateEvidence(JsonElement value, IReadOnlyList<object> path)
    {
        SchemaObject(value, new[] { "path", "sha256", "selector" }, path);
        SchemaDemand(SchemaResourcePath(value.GetProperty("path"))
            && SchemaHash.IsMatch(SchemaString(value, "sha256", path)), "Invalid evidence lock", path);
        SchemaScalarText(value.GetProperty("selector"), SchemaAdd(path, "selector"));
    }

    private static JsonElement[] SchemaParentRows(JsonElement value, int budget, bool allowEmpty,
        string[] fields, IReadOnlyList<object> path)
    {
        SchemaDemand(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= budget
            && (allowEmpty || value.GetArrayLength() > 0), "Resource node budget exceeded or empty", path);
        var rows = value.EnumerateArray().ToArray();
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            SchemaObject(row, fields, path);
            var id = SchemaBaseText(row.GetProperty("id"), path);
            SchemaDemand(id.Length <= 512 && !parents.ContainsKey(id),
                "Duplicate or invalid resource node: " + id, path);
            var parentValue = row.GetProperty("parent");
            string? parent = null;
            if (parentValue.ValueKind != JsonValueKind.Null) parent = SchemaBaseText(parentValue, path);
            parents.Add(id, parent);
        }
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in parents.Keys)
        {
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var cursor = id;
            while (!finished.Contains(cursor))
            {
                SchemaDemand(visiting.Add(cursor), "Resource hierarchy cycle: " + cursor, path);
                var parent = parents[cursor];
                if (parent == null) break;
                SchemaDemand(parents.ContainsKey(parent), "Missing resource parent: " + parent, path);
                cursor = parent;
            }
            finished.UnionWith(visiting);
        }
        return rows;
    }

    private static string SchemaNode(JsonElement value, HashSet<string> nodes, IReadOnlyList<object> path)
    {
        var node = SchemaScalarText(value, path);
        SchemaDemand(nodes.Contains(node), "Missing source hierarchy node", path);
        return node;
    }

    private static JsonElement[] SchemaDescriptionList(JsonElement value, IReadOnlyList<object> path)
    {
        SchemaDemand(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= SchemaMaximumParts,
            "Invalid or oversized list", path);
        return value.EnumerateArray().ToArray();
    }

    private static string[] SchemaBaseStrings(JsonElement value, IReadOnlyList<object> path)
    {
        SchemaDemand(value.ValueKind == JsonValueKind.Array, "Invalid resource list", path);
        var strings = value.EnumerateArray().Select(item => SchemaBaseText(item, path)).ToArray();
        SchemaDemand(strings.Distinct(StringComparer.Ordinal).Count() == strings.Length,
            "Invalid resource list", path);
        return strings;
    }

    private static double[] SchemaVector(JsonElement value, int size, IReadOnlyList<object> path,
        double minimum = double.NegativeInfinity, double maximum = double.PositiveInfinity)
    {
        SchemaDemand(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == size,
            "Invalid vector length", path);
        var result = new double[size];
        var index = 0;
        foreach (var number in value.EnumerateArray())
            result[index] = SchemaFinite(number, SchemaAdd(path, index++), minimum, maximum);
        return result;
    }

    private static double SchemaFinite(JsonElement value, IReadOnlyList<object> path,
        double minimum = double.NegativeInfinity, double maximum = double.PositiveInfinity)
    {
        double number = 0;
        SchemaDemand(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number)
            && double.IsFinite(number) && number >= minimum && number <= maximum,
            "Invalid numeric value", path);
        return number;
    }

    private static long SchemaInteger(JsonElement value, IReadOnlyList<object> path, long minimum = 0)
    {
        var number = SchemaFinite(value, path, minimum, SchemaMaximumSafeInteger);
        SchemaDemand(number == Math.Truncate(number), "Expected an integer", path);
        return (long)number;
    }

    private static string SchemaChoice(JsonElement value, string[] choices, IReadOnlyList<object> path)
    {
        var choice = SchemaReadString(value, path, "Unknown value");
        SchemaDemand(choices.Contains(choice, StringComparer.Ordinal), "Unknown value", path);
        return choice;
    }

    private static string SchemaString(JsonElement value, string property, IReadOnlyList<object> path)
        => SchemaBaseText(value.GetProperty(property), path);

    private static string SchemaBaseText(JsonElement value, IReadOnlyList<object> path)
    {
        var text = SchemaReadString(value, path, "Expected a nonempty string");
        SchemaDemand(text.Length <= 2048 && HasText(text)
            && text.All(character => character >= 0x20), "Expected a nonempty string", path);
        return text;
    }

    private static string SchemaScalarText(JsonElement value, IReadOnlyList<object> path)
    {
        var text = SchemaReadString(value, path, "Expected a nonempty Unicode scalar string");
        var valid = text.Length <= 2048 && HasText(text);
        for (var index = 0; valid && index < text.Length; index++)
        {
            var character = text[index];
            if (character < 0x20 || character == 0x7f) valid = false;
            else if (char.IsHighSurrogate(character))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) valid = false;
            }
            else if (char.IsLowSurrogate(character)) valid = false;
        }
        SchemaDemand(valid, "Expected a nonempty Unicode scalar string", path);
        return text;
    }

    private static string SchemaReadString(JsonElement value, IReadOnlyList<object> path, string detail)
    {
        SchemaDemand(value.ValueKind == JsonValueKind.String, detail, path);
        try { return value.GetString()!; }
        catch (InvalidOperationException)
        {
            // System.Text.Json deliberately refuses an escaped lone surrogate. JavaScript keeps that UTF-16
            // code unit, so decode the already syntax-checked token here and let the caller's scalar rule decide.
            var raw = value.GetRawText();
            var decoded = new StringBuilder(raw.Length - 2);
            for (var index = 1; index < raw.Length - 1; index++)
            {
                var character = raw[index];
                if (character != '\\') { decoded.Append(character); continue; }
                switch (raw[++index])
                {
                    case '"': decoded.Append('"'); break;
                    case '\\': decoded.Append('\\'); break;
                    case '/': decoded.Append('/'); break;
                    case 'b': decoded.Append('\b'); break;
                    case 'f': decoded.Append('\f'); break;
                    case 'n': decoded.Append('\n'); break;
                    case 'r': decoded.Append('\r'); break;
                    case 't': decoded.Append('\t'); break;
                    case 'u':
                        decoded.Append((char)int.Parse(raw.AsSpan(index + 1, 4),
                            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                }
            }
            return decoded.ToString();
        }
    }

    private static bool SchemaResourcePath(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = SchemaReadString(value, SchemaNoPath, "Expected a resource path");
        return SchemaBaseTextValue(text) && text.IndexOfAny(new[] { ':', '?', '#' }) < 0 && ForgeCore.PosixPath.IsRelative(text);
    }

    private static bool SchemaBaseTextValue(string text)
        => text.Length <= 2048 && HasText(text) && text.All(character => character >= 0x20);

    internal static bool HasText(string text)
        => text.Length > 0 && text.Any(character => !SchemaEcmaWhitespace(character));

    private static bool SchemaEcmaWhitespace(char character)
        => character is '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or '\u00A0'
            or '\u1680' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF'
            || character is >= '\u2000' and <= '\u200A';

    private static void SchemaObject(JsonElement value, string[] fields, IReadOnlyList<object> path,
        string objectDetail = "Expected an object", string fieldsDetail = "Unexpected or missing fields")
    {
        SchemaDemand(value.ValueKind == JsonValueKind.Object, objectDetail, path);
        try
        {
            var properties = value.EnumerateObject().ToArray();
            SchemaDemand(properties.Length == fields.Length
                && properties.All(property => fields.Contains(property.Name, StringComparer.Ordinal)), fieldsDetail, path);
        }
        catch (InvalidOperationException) { throw SchemaReject(fieldsDetail, path); }
    }

    private static object[] SchemaAdd(IReadOnlyList<object> path, object segment)
    {
        var result = new object[path.Count + 1];
        for (var index = 0; index < path.Count; index++) result[index] = path[index];
        result[^1] = segment;
        return result;
    }

    private static void SchemaDemand(bool condition, string detail, IReadOnlyList<object> path)
    {
        if (!condition) throw SchemaReject(detail, path);
    }

    private static ResourceDefinitionException SchemaReject(string detail, IReadOnlyList<object> path)
        => new("resource-invalid", detail, path);

    private static string SchemaDetail(ResourceDefinitionException error)
    {
        var prefix = error.Code + ": ";
        return error.Message.StartsWith(prefix, StringComparison.Ordinal) ? error.Message[prefix.Length..] : error.Message;
    }

    private static void SchemaValidateJson(JsonElement value, int ancestors)
    {
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            SchemaDemand(ancestors < 64, "Metadata nesting budget exceeded", SchemaNoPath);
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var property in value.EnumerateObject())
                {
                    SchemaDemand(keys.Add(property.Name), "Duplicate JSON key", SchemaNoPath);
                    SchemaValidateJson(property.Value, ancestors + 1);
                }
            }
            catch (InvalidOperationException) { throw SchemaReject("Non-JSON object", SchemaNoPath); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) SchemaValidateJson(item, ancestors + 1);
        else if (value.ValueKind == JsonValueKind.Number)
            SchemaDemand(value.TryGetDouble(out var number) && double.IsFinite(number), "Non-JSON number", SchemaNoPath);
        else SchemaDemand(value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null,
            "Non-JSON value", SchemaNoPath);
    }

    private static double SchemaHypot(double[] values)
    {
        var largest = values.Max(value => Math.Abs(value));
        if (largest == 0) return 0;
        return largest * Math.Sqrt(values.Sum(value => (value / largest) * (value / largest)));
    }

    private static double SchemaDeterminant(double[] te)
    {
        var n11 = te[0]; var n12 = te[4]; var n13 = te[8]; var n14 = te[12];
        var n21 = te[1]; var n22 = te[5]; var n23 = te[9]; var n24 = te[13];
        var n31 = te[2]; var n32 = te[6]; var n33 = te[10]; var n34 = te[14];
        var n41 = te[3]; var n42 = te[7]; var n43 = te[11]; var n44 = te[15];
        return n41 * (+n14 * n23 * n32 - n13 * n24 * n32 - n14 * n22 * n33 + n12 * n24 * n33 + n13 * n22 * n34 - n12 * n23 * n34)
            + n42 * (+n11 * n23 * n34 - n11 * n24 * n33 + n14 * n21 * n33 - n13 * n21 * n34 + n13 * n24 * n31 - n14 * n23 * n31)
            + n43 * (+n11 * n24 * n32 - n11 * n22 * n34 - n14 * n21 * n32 + n12 * n21 * n34 + n14 * n22 * n31 - n12 * n24 * n31)
            + n44 * (-n13 * n22 * n31 - n11 * n23 * n32 + n11 * n22 * n33 + n13 * n21 * n32 - n12 * n21 * n33 + n12 * n23 * n31);
    }

    private static void SchemaAppendAuthored(StringBuilder text, JsonElement value)
    {
        text.Append('{');
        var first = true;
        foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (!first) text.Append(',');
            first = false;
            SchemaAppendString(text, property.Name);
            text.Append(':');
            if (property.NameEquals("reference")) SchemaAppendCanonical(text, property.Value, "revision");
            else if (property.NameEquals("source")) SchemaAppendCanonical(text, property.Value, "contentSha256");
            else SchemaAppendCanonical(text, property.Value, null);
        }
        text.Append('}');
    }

    private static void SchemaAppendCanonical(StringBuilder text, JsonElement value, string? omittedProperty)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                text.Append('{');
                var first = true;
                foreach (var property in value.EnumerateObject().Where(property => property.Name != omittedProperty)
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!first) text.Append(',');
                    first = false;
                    SchemaAppendString(text, property.Name);
                    text.Append(':');
                    SchemaAppendCanonical(text, property.Value, null);
                }
                text.Append('}');
                break;
            case JsonValueKind.Array:
                text.Append('[');
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (index++ != 0) text.Append(',');
                    SchemaAppendCanonical(text, item, null);
                }
                text.Append(']');
                break;
            case JsonValueKind.String: SchemaAppendString(text, SchemaReadString(value, SchemaNoPath, "Invalid JSON string")); break;
            case JsonValueKind.Number: text.Append(SchemaJavaScriptNumber(value.GetDouble())); break;
            case JsonValueKind.True: text.Append("true"); break;
            case JsonValueKind.False: text.Append("false"); break;
            case JsonValueKind.Null: text.Append("null"); break;
            default: throw SchemaReject("Invalid JSON value", SchemaNoPath);
        }
    }

    private static void SchemaAppendString(StringBuilder text, string value)
    {
        text.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                    { text.Append(character).Append(value[++index]); }
                    else if (character < 0x20 || char.IsSurrogate(character))
                        text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(character);
                    break;
            }
        }
        text.Append('"');
    }

    private static string SchemaJavaScriptNumber(double value)
    {
        if (value == 0) return "0";
        var negative = value < 0;
        var raw = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = raw.IndexOfAny(new[] { 'E', 'e' });
        var exponent = exponentAt < 0 ? 0 : int.Parse(raw[(exponentAt + 1)..], CultureInfo.InvariantCulture);
        var mantissa = exponentAt < 0 ? raw : raw[..exponentAt];
        var point = mantissa.IndexOf('.');
        var decimalPosition = (point < 0 ? mantissa.Length : point) + exponent;
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        while (digits.Length > 1 && digits[0] == '0') { digits = digits[1..]; decimalPosition--; }
        while (digits.Length > 1 && digits[^1] == '0') digits = digits[..^1];
        var scientificExponent = decimalPosition - 1;
        string formatted;
        if (scientificExponent is >= -6 and < 21)
        {
            if (decimalPosition <= 0) formatted = "0." + new string('0', -decimalPosition) + digits;
            else if (decimalPosition >= digits.Length) formatted = digits + new string('0', decimalPosition - digits.Length);
            else formatted = digits[..decimalPosition] + "." + digits[decimalPosition..];
        }
        else
        {
            formatted = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            formatted += "e" + (scientificExponent >= 0 ? "+" : "")
                + scientificExponent.ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + formatted : formatted;
    }
}

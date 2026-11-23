using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeEngine.Framework;

internal static partial class ResourceDefinitions
{
    private sealed record Use(JsonElement Reference, string? Tier, string? Expected,
        JsonElement Root, JsonElement[] Chain, object[] Path);
    private sealed record Node(Use Request, JsonElement Definition, List<Use> Children);
    private sealed record Group(string Id, JsonElement[] Roots);
    private sealed record Resolution(JsonElement Closure, Group[] Groups, Dictionary<string, JsonElement> Definitions);
    private static readonly string[] Tiers = { "original", "low" };
    private static string RefKey(JsonElement reference) => "[" + Canonical(reference.GetProperty("id")) + "," + Canonical(reference.GetProperty("revision")) + "]";
    private static string UseKey(Use use) => RefKey(use.Reference)[..^1] + "," + Canonical(J(use.Tier)) + "]";
    private static string S(JsonElement value, string key) => SchemaReadString(value.GetProperty(key), Array.Empty<object>(), "Invalid text");
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement[] Rows(JsonElement value) => value.EnumerateArray().ToArray();
    private static object[] At(object[] path, params object[] suffix) => path.Concat(suffix).ToArray();
    private static ResourceDefinitionException Refused(string code, string detail, Use use, object[]? path = null)
        => new(code, detail, path ?? use.Path, use.Reference, use.Root, use.Chain.Append(use.Reference).ToArray());
    private static void Invalid(bool ok, string detail, params object[] path)
    { if (!ok) throw new ResourceDefinitionException("resource-invalid", detail, path); }
    private static readonly Regex PinPattern = new(@"^[A-Za-z0-9_]+-[A-Za-z0-9_]+-(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$", RegexOptions.CultureInvariant);
    private static string PackageName(string pin) => pin[..pin.LastIndexOf('-')];

    private static Group[] ReadGroups(JsonElement scope)
    {
        Invalid(scope.ValueKind == JsonValueKind.Object && scope.EnumerateObject().Count() == 2, "Invalid resource scope", "scope");
        string kind = scope.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "";
        if (kind != "rundown")
        {
            Invalid(kind is "weapon" or "level" && scope.TryGetProperty("roots", out var roots) && roots.ValueKind == JsonValueKind.Array,
                "Invalid resource scope", "scope");
            return new[] { new Group(kind, Rows(scope.GetProperty("roots"))) };
        }
        Invalid(scope.TryGetProperty("levels", out var levels) && levels.ValueKind == JsonValueKind.Array, "Invalid Rundown scope", "scope");
        var ids = new HashSet<string>(StringComparer.Ordinal); var result = new List<Group>();
        foreach (var level in levels.EnumerateArray())
        {
            Invalid(level.ValueKind == JsonValueKind.Object && level.EnumerateObject().Count() == 2
                && level.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && HasText(S(level, "id"))
                && ids.Add(S(level, "id")) && level.TryGetProperty("roots", out var roots) && roots.ValueKind == JsonValueKind.Array,
                "Invalid or duplicate level", "scope", "levels", result.Count);
            result.Add(new Group(S(level, "id"), Rows(level.GetProperty("roots"))));
        }
        return result.ToArray();
    }

    private static List<Use> Edges(Node use)
    {
        var value = use.Definition; var request = use.Request; var description = value.GetProperty("description");
        var result = new List<Use>();
        void Add(JsonElement reference, string? expected, object[] path, string? tier = null)
            => result.Add(new Use(reference, tier, expected, request.Root,
                request.Chain.Append(request.Reference).ToArray(), path));
        var source = value.GetProperty("source");
        switch (S(description, "kind"))
        {
            case "composition":
                int index = 0;
                foreach (var instance in value.GetProperty("composition").GetProperty("instances").EnumerateArray())
                    Add(instance.GetProperty("resource"), "part", new object[] { "composition", "instances", index++, "resource" }, request.Tier);
                break;
            case "model":
                if (request.Tier == null) throw Refused("resource-variant-unavailable", "A model tier must be explicit", request);
                var variant = description.GetProperty("variants").GetProperty(request.Tier);
                var path = new object[] { "description", "variants", request.Tier };
                if (!variant.GetProperty("available").GetBoolean()) throw Refused("resource-variant-unavailable", S(variant, "reason"), request, path);
                if (S(variant.GetProperty("payload"), "kind") == "native" && value.GetProperty("locator").ValueKind == JsonValueKind.Null)
                    throw Refused("resource-locator-missing", "Original has no verified native locator", request, new object[] { "locator" });
                index = 0;
                foreach (var slot in variant.GetProperty("materials").EnumerateArray())
                    Add(slot.GetProperty("material"), "material", At(path, "materials", index++, "material"));
                if (variant.GetProperty("skin").ValueKind != JsonValueKind.Null) Add(variant.GetProperty("skin").GetProperty("rig"), "rig", At(path, "skin", "rig"));
                index = 0;
                foreach (var animation in variant.GetProperty("animations").EnumerateArray()) Add(animation, "animation", At(path, "animations", index++));
                index = 0;
                foreach (var part in description.GetProperty("collisionParts").EnumerateArray())
                    Add(part.GetProperty("shape"), "collision", new object[] { "description", "collisionParts", index++, "shape" });
                break;
            case "material":
                index = 0;
                foreach (var texture in description.GetProperty("textures").EnumerateArray())
                    Add(texture.GetProperty("texture"), "texture", new object[] { "description", "textures", index++, "texture" });
                break;
            case "animation": Add(description.GetProperty("rig"), "rig", new object[] { "description", "rig" }); break;
        }
        return result;
    }

    private static void Compatible(Node use, Dictionary<string, JsonElement> definitions)
    {
        var description = use.Definition.GetProperty("description"); string kind = S(description, "kind");
        HashSet<string> Bones(JsonElement reference) => definitions[RefKey(reference)].GetProperty("description").GetProperty("bones")
            .EnumerateArray().Select(bone => S(bone, "id")).ToHashSet(StringComparer.Ordinal);
        if (kind == "animation")
        {
            var bones = Bones(description.GetProperty("rig")); int index = 0;
            foreach (var track in description.GetProperty("tracks").EnumerateArray())
            {
                if (!bones.Contains(S(track, "bone"))) throw Refused("resource-type-mismatch", "Animation track targets a missing bone", use.Request,
                    new object[] { "description", "tracks", index, "bone" });
                index++;
            }
        }
        else if (kind == "model")
        {
            string tier = use.Request.Tier!; var variant = description.GetProperty("variants").GetProperty(tier); var skin = variant.GetProperty("skin");
            if (skin.ValueKind == JsonValueKind.Null) return;
            var rig = skin.GetProperty("rig"); var bones = Bones(rig);
            if (skin.GetProperty("bones").EnumerateArray().Any(mapping => !bones.Contains(S(mapping, "bone"))))
                throw Refused("resource-type-mismatch", "Skin maps to a missing rig bone", use.Request, new object[] { "description", "variants", tier, "skin", "bones" });
            foreach (var reference in variant.GetProperty("animations").EnumerateArray())
                if (RefKey(definitions[RefKey(reference)].GetProperty("description").GetProperty("rig")) != RefKey(rig))
                    throw Refused("resource-type-mismatch", "Animation and model must lock the same rig", use.Request, new object[] { "description", "variants", tier, "animations" });
        }
    }

    private static IEnumerable<(JsonElement Data, object[] Path)> Payloads(Node use)
    {
        var description = use.Definition.GetProperty("description");
        if (S(description, "kind") == "model")
        {
            var payload = description.GetProperty("variants").GetProperty(use.Request.Tier!).GetProperty("payload");
            if (S(payload, "kind") == "bytes") yield return (payload.GetProperty("data"), new object[] { "description", "variants", use.Request.Tier!, "payload", "data" });
        }
        else if (description.TryGetProperty("data", out var data)) yield return (data, new object[] { "description", "data" });
    }
    private static string ByteKey(JsonElement data) => S(data, "sha256") + ":" + Canonical(data.GetProperty("bytes"));
    private static IEnumerable<string> Pins(JsonElement definition)
    {
        foreach (var pin in definition.GetProperty("dependencies").EnumerateArray()) yield return pin.GetString()!;
        var source = definition.GetProperty("source"); if (S(source, "kind") == "package") yield return S(source, "packagePin");
    }

    private static Resolution Resolve(JsonElement scope, Func<JsonElement, JsonElement?> read, Func<string, JsonElement?>? readPackage)
    {
        var groups = ReadGroups(scope.Clone()); var definitions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal); var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        var locatorIdentities = new Dictionary<string, string>(StringComparer.Ordinal); var packageRequests = new Dictionary<string, Use>(StringComparer.Ordinal);
        if (groups.Sum(group => (long)group.Roots.Length) > 65536) throw new ResourceDefinitionException("resource-budget-exceeded", "Scope root budget exceeded", new object[] { "scope" });
        var roots = groups.SelectMany(group => group.Roots.Select((value, i) =>
        {
            Invalid(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 2
                && value.TryGetProperty("reference", out _) && value.TryGetProperty("tier", out var tier)
                && (tier.ValueKind == JsonValueKind.Null || tier.ValueKind == JsonValueKind.String && Tiers.Contains(tier.GetString()!)), "Invalid resource use", "scope", group.Id, i);
            var reference = value.GetProperty("reference");
            try { ReadReference(reference); }
            catch (ResourceDefinitionException error) { throw new ResourceDefinitionException(error.Code, error.Message, new object[] { "scope", group.Id, i, "reference" }); }
            return new Use(reference.Clone(), value.GetProperty("tier").GetString(), null, reference.Clone(), Array.Empty<JsonElement>(), new object[] { "scope", group.Id, i });
        })).ToArray();
        void Visit(Use request)
        {
            if (request.Chain.Length >= 64) throw Refused("resource-budget-exceeded", "Resource nesting budget exceeded", request);
            if (request.Chain.Any(reference => RefKey(reference) == RefKey(request.Reference))) throw Refused("resource-cycle", "Locked resource cycle", request);
            JsonElement? candidate;
            try { candidate = read(request.Reference); }
            catch (ResourceDefinitionException error) { throw Refused(error.Code, error.Message, request, error.Path.ToArray()); }
            catch (Exception error) { throw Refused("resource-invalid", error.Message, request); }
            if (candidate == null) throw Refused("content-entry-missing", "Locked resource was not found", request);
            var value = candidate.Value;
            try { Validate(value); }
            catch (ResourceDefinitionException error) { throw Refused(error.Code, error.Message, request, error.Path.ToArray()); }
            if (RefKey(value.GetProperty("reference")) != RefKey(request.Reference)) throw Refused("resource-revision-mismatch", "Resolver returned a different locked reference", request);
            string id = S(request.Reference, "id"), revision = S(request.Reference, "revision");
            if (revisions.TryGetValue(id, out var previous) && previous != revision) throw Refused("resource-revision-conflict", "One closure cannot install two revisions", request);
            revisions[id] = revision;
            string key = RefKey(request.Reference); bool exists = definitions.TryGetValue(key, out var prior);
            if (exists && Canonical(prior) != Canonical(value)) throw Refused("resource-definition-conflict", "One locked reference has different definitions", request);
            var locator = value.GetProperty("locator");
            if (locator.ValueKind != JsonValueKind.Null)
            {
                string identity = Canonical(locator);
                if (locatorIdentities.TryGetValue(id, out var old) && identity != old) throw Refused("resource-locator-conflict", "One ID has different native locators", request, new object[] { "locator" });
                locatorIdentities[id] = identity;
            }
            string kind = S(value.GetProperty("description"), "kind");
            if (request.Expected != null && (request.Expected == "part" ? kind is not ("model" or "composition") : kind != request.Expected))
                throw Refused("resource-type-mismatch", "Referenced resource has an incompatible description", request);
            if (!exists)
            {
                if (definitions.Count >= 16384) throw Refused("resource-budget-exceeded", "Definition budget exceeded", request);
                definitions[key] = value.Clone();
            }
            foreach (string pin in Pins(value)) packageRequests.TryAdd(pin, request);
            string useKey = UseKey(request); if (nodes.ContainsKey(useKey)) return;
            var node = new Node(request, definitions[key], new List<Use>()); nodes.Add(useKey, node);
            node.Children.AddRange(Edges(node));
            foreach (var child in node.Children)
            {
                if (child.Chain.Any(reference => RefKey(reference) == RefKey(child.Reference))) throw Refused("resource-cycle", "Locked resource cycle", child);
                if (child.Chain.Length >= 64) throw Refused("resource-budget-exceeded", "Resource nesting budget exceeded", child);
            }
            foreach (var child in node.Children) Visit(child);
        }
        foreach (var root in roots) Visit(root);
        foreach (var node in nodes.Values)
        {
            try { Verify(node.Definition); }
            catch (ResourceDefinitionException error) { throw Refused(error.Code, error.Message, node.Request, error.Path.ToArray()); }
            Compatible(node, definitions);
        }
        var packages = new Dictionary<string, JsonElement>(StringComparer.Ordinal); var packageNames = new Dictionary<string, string>(StringComparer.Ordinal);
        void VisitPackage(string pin, string[] chain, Use source)
        {
            var path = new object[] { "dependencies" }.Concat(chain).Append(pin).ToArray();
            if (!PinPattern.IsMatch(pin)) throw Refused("resource-invalid", "Invalid package pin", source, path);
            string name = PackageName(pin);
            if (packageNames.TryGetValue(name, out var prior) && prior != pin) throw Refused("resource-revision-conflict", "Package revision conflict", source, path);
            JsonElement? candidate;
            try { candidate = readPackage?.Invoke(pin); }
            catch (ResourceDefinitionException error) { throw Refused(error.Code, error.Message, source, path); }
            catch (Exception error) { throw Refused("resource-invalid", error.Message, source, path); }
            if (candidate == null || candidate.Value.ValueKind != JsonValueKind.Object || !candidate.Value.TryGetProperty("pin", out var declared)
                || declared.ValueKind != JsonValueKind.String || declared.GetString() != pin) throw Refused("content-dependency-missing", "Missing pinned package: " + pin, source, path);
            var value = candidate.Value;
            if (value.EnumerateObject().Count() != 2 || !value.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array
                || dependencies.EnumerateArray().Any(child => child.ValueKind != JsonValueKind.String || !PinPattern.IsMatch(child.GetString()!))
                || dependencies.EnumerateArray().Select(child => child.GetString()).Distinct(StringComparer.Ordinal).Count() != dependencies.GetArrayLength())
                throw Refused("resource-invalid", "Invalid package dependency list", source, path);
            if (packages.TryGetValue(pin, out var existing))
            {
                if (Canonical(existing) != Canonical(value)) throw Refused("resource-definition-conflict", "Pinned package definition changed", source, path);
                return;
            }
            if (packages.Count >= 16384) throw Refused("resource-budget-exceeded", "Package budget exceeded", source, path);
            packageNames[name] = pin; packages[pin] = value.Clone();
            foreach (var child in dependencies.EnumerateArray())
            {
                string childPin = child.GetString()!; var childChain = chain.Append(pin).ToArray();
                if (childChain.Contains(childPin)) throw Refused("resource-cycle", "Pinned package dependency cycle", source, new object[] { "dependencies" }.Concat(childChain).Append(childPin).ToArray());
                if (childChain.Length >= 64) throw Refused("resource-budget-exceeded", "Package nesting budget exceeded", source, new object[] { "dependencies" }.Concat(childChain).Append(childPin).ToArray());
                VisitPackage(childPin, childChain, source);
            }
        }
        foreach (string pin in packageRequests.Keys.OrderBy(pin => pin, StringComparer.Ordinal)) VisitPackage(pin, Array.Empty<string>(), packageRequests[pin]);
        var sortedDefinitions = definitions.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => row.Value).ToArray();
        var sortedNodes = nodes.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => row.Value).ToArray();
        var byteMap = new Dictionary<string, (JsonElement Data, List<object> Sources)>(StringComparer.Ordinal);
        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in sortedNodes) foreach (var (data, path) in Payloads(node))
        {
            string key = ByteKey(data);
            if (!byteMap.TryGetValue(key, out var entry)) byteMap[key] = entry = (data, new List<object>());
            var reference = node.Definition.GetProperty("reference");
            if (sourceKeys.Add(key + RefKey(reference) + JsonSerializer.Serialize(path))) entry.Sources.Add(new { reference, path, data });
        }
        var bytes = byteMap.Values.OrderBy(entry => S(entry.Data, "sha256"), StringComparer.Ordinal).ThenBy(entry => entry.Data.GetProperty("bytes").GetDouble()).ToArray();
        var levels = groups.Select(group =>
        {
            var subset = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<string>(roots.Where(root => (string)root.Path[1] == group.Id).Select(UseKey));
            while (pending.TryPop(out var key)) if (subset.Add(key)) foreach (var child in nodes[key].Children) pending.Push(UseKey(child));
            var refs = subset.Select(key => RefKey(nodes[key].Definition.GetProperty("reference"))).ToHashSet(StringComparer.Ordinal);
            var pins = subset.SelectMany(key => Pins(nodes[key].Definition)).ToHashSet(StringComparer.Ordinal); var queue = new Stack<string>(pins);
            while (queue.TryPop(out var pin)) foreach (var child in packages[pin].GetProperty("dependencies").EnumerateArray()) if (pins.Add(child.GetString()!)) queue.Push(child.GetString()!);
            var payloadKeys = subset.SelectMany(key => Payloads(nodes[key])).Select(row => ByteKey(row.Data)).ToHashSet(StringComparer.Ordinal);
            return new
            {
                id = group.Id,
                resources = sortedDefinitions.Select((value, i) => (value, i)).Where(row => refs.Contains(RefKey(row.value.GetProperty("reference")))).Select(row => row.i),
                uses = sortedNodes.Select((node, i) => (node, i)).Where(row => subset.Contains(UseKey(row.node.Request))).Select(row => row.i),
                bytes = bytes.Select((entry, i) => (entry, i)).Where(row => payloadKeys.Contains(ByteKey(row.entry.Data))).Select(row => row.i),
                packagePins = pins.OrderBy(pin => pin, StringComparer.Ordinal)
            };
        });
        var closure = J(new
        {
            definitions = sortedDefinitions,
            uses = sortedNodes.Select(node => new { reference = node.Definition.GetProperty("reference"), tier = node.Request.Tier }),
            packagePins = packages.Keys.OrderBy(pin => pin, StringComparer.Ordinal),
            bytes = bytes.Select(entry => new { sha256 = S(entry.Data, "sha256"), bytes = entry.Data.GetProperty("bytes"), sources = entry.Sources }),
            levels,
            blockers = sortedDefinitions.Where(value => value.GetProperty("blockers").GetProperty("runtime").GetArrayLength() > 0)
                .Select(value => new { reference = value.GetProperty("reference"), reasons = value.GetProperty("blockers").GetProperty("runtime") })
        });
        return new Resolution(closure, groups, definitions);
    }

    internal static void AssertRuntimeBlockersCleared(JsonElement closure)
    {
        var blockers = closure.GetProperty("blockers"); if (blockers.GetArrayLength() == 0) return;
        var blocker = blockers[0]; throw new ResourceDefinitionException("resource-runtime-blocked", string.Join(", ", blocker.GetProperty("reasons").EnumerateArray().Select(row => row.GetString())),
            new object[] { "blockers", "runtime" }, blocker.GetProperty("reference"));
    }

    internal static JsonElement CompileAssembly(JsonElement scope, Func<JsonElement, JsonElement?> read, Func<string, JsonElement?>? readPackage = null)
    {
        var resolved = Resolve(scope, read, readPackage); var resources = resolved.Closure.GetProperty("definitions").EnumerateArray().Select(value => value.GetProperty("reference")).ToArray();
        var indices = resources.Select((reference, i) => (reference, i)).ToDictionary(row => RefKey(row.reference), row => row.i, StringComparer.Ordinal);
        var parts = new List<object>(); var traces = new List<object>(); int expanded = 0;
        foreach (var group in resolved.Groups) for (int rootIndex = 0; rootIndex < group.Roots.Length; rootIndex++)
        {
            var root = group.Roots[rootIndex]; var rootReference = root.GetProperty("reference");
            void Expand(JsonElement reference, string? tier, double[] matrix, List<object> chain)
            {
                var value = resolved.Definitions[RefKey(reference)]; var description = value.GetProperty("description"); string kind = S(description, "kind");
                if (kind is "composition" or "model" && ++expanded > 65536) throw new ResourceDefinitionException("resource-budget-exceeded", "Expanded placement budget exceeded", new object[] { "parts" }, reference, rootReference);
                if (kind == "composition")
                {
                    var instances = Rows(value.GetProperty("composition").GetProperty("instances")); var byId = instances.ToDictionary(instance => S(instance, "id"), StringComparer.Ordinal);
                    var world = new Dictionary<string, double[]>(StringComparer.Ordinal);
                    double[] InstanceMatrix(string id)
                    {
                        var pending = new Stack<string>(); string? cursor = id;
                        while (cursor != null && !world.ContainsKey(cursor))
                        {
                            if (pending.Count >= 64) throw new ResourceDefinitionException("resource-budget-exceeded", "Instance parent depth exceeded", new object[] { "composition", "instances" }, reference, rootReference);
                            pending.Push(cursor); cursor = byId[cursor].GetProperty("parent").GetString();
                        }
                        var parent = cursor == null ? matrix : world[cursor];
                        while (pending.TryPop(out var current)) { parent = Multiply(parent, LocalMatrix(byId[current].GetProperty("local"))); world[current] = parent; }
                        return world[id];
                    }
                    foreach (var instance in instances)
                    {
                        if (chain.Count >= 64) throw new ResourceDefinitionException("resource-budget-exceeded", "Assembly nesting exceeded", new object[] { "composition" }, reference, rootReference);
                        var next = new List<object>(chain) { new { definition = reference, instanceId = S(instance, "id") } };
                        Expand(instance.GetProperty("resource"), tier, InstanceMatrix(S(instance, "id")), next);
                    }
                }
                else if (kind == "model")
                {
                    if (matrix.Any(number => !float.IsFinite((float)number))) throw new ResourceDefinitionException("resource-invalid", "Assembly matrix exceeds float32", new object[] { "parts", parts.Count, "matrix" }, reference, rootReference);
                    var variant = description.GetProperty("variants").GetProperty(tier!);
                    parts.Add(new { resourceIndex = indices[RefKey(reference)], tier, matrix, traceIndex = traces.Count });
                    traces.Add(new
                    {
                        scope = group.Id,
                        rootIndex,
                        root = rootReference,
                        chain,
                        selection = variant.GetProperty("selection"),
                        materialSlots = variant.GetProperty("materials").EnumerateArray().Select(slot => new { renderer = S(slot, "renderer"), slot = S(slot, "slot") })
                    });
                }
            }
            Expand(rootReference, root.GetProperty("tier").GetString(), IdentityMatrix(), new List<object>());
        }
        var result = J(new { schemaVersion = ForgeCore.FormatVersion.Current, space = "unity-left-handed-meters", closure = resolved.Closure, resources, parts, traces });
        if (Encoding.UTF8.GetByteCount(Canonical(result)) > 64 * 1024 * 1024) throw new ResourceDefinitionException("resource-budget-exceeded", "Flat assembly exceeds document byte budget", new object[] { "parts" });
        return result;
    }

    private static double[] IdentityMatrix() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    private static double[] LocalMatrix(JsonElement local)
    {
        var p = local.GetProperty("position"); var q = local.GetProperty("rotation"); var s = local.GetProperty("scale");
        double x = q[0].GetDouble(), y = q[1].GetDouble(), z = q[2].GetDouble(), w = q[3].GetDouble();
        double x2 = x + x, y2 = y + y, z2 = z + z, xx = x * x2, xy = x * y2, xz = x * z2, yy = y * y2, yz = y * z2, zz = z * z2, wx = w * x2, wy = w * y2, wz = w * z2;
        double sx = s[0].GetDouble(), sy = s[1].GetDouble(), sz = s[2].GetDouble();
        return new[] { (1 - (yy + zz)) * sx, (xy + wz) * sx, (xz - wy) * sx, 0, (xy - wz) * sy, (1 - (xx + zz)) * sy, (yz + wx) * sy, 0,
            (xz + wy) * sz, (yz - wx) * sz, (1 - (xx + yy)) * sz, 0, p[0].GetDouble(), p[1].GetDouble(), p[2].GetDouble(), 1 };
    }
    private static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[16];
        for (int column = 0; column < 4; column++) for (int row = 0; row < 4; row++)
            result[column * 4 + row] = a[row] * b[column * 4] + a[4 + row] * b[column * 4 + 1] + a[8 + row] * b[column * 4 + 2] + a[12 + row] * b[column * 4 + 3];
        return result;
    }
}

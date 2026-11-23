using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>Startup-only admission of a behavior source declaration as a content prerequisite. This never
/// registers capabilities, compiles or executes the graph, mounts a plan, or writes native game data. The
/// website owns graph semantics and verifies the author's semantic revision and archive/entry locks. Here
/// revision is only a declared identity with a checked spelling: RuntimeJson.StableText is not the website's
/// canonicalization. The existing content admission snapshot freezes the bytes installed at startup; it
/// cannot establish that those initial bytes match an author's archive lock, which this wire does not carry.</summary>
internal static class BehaviorContentRegistration
{
    internal const string Invalid = "behavior-content-invalid";
    private const int MaximumNodes = 2048, MaximumEdges = 8192;
    private static readonly Regex NodeName = new(@"\A[A-Za-z][A-Za-z0-9_-]{0,127}\z", RegexOptions.CultureInvariant);
    private static readonly Regex PortName = new(@"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex Boundary = new(@"\Aforge\.module\.(input|output)_[1-9][0-9]*\z", RegexOptions.CultureInvariant);

    internal static void Register(RuntimeModuleHandle owner)
        => owner.RegisterContentDomain("behavior", Accept);

    internal static ContentRegistrationResult Accept(RuntimeContentEntry entry)
    {
        try
        {
            Require(entry.Domain == "behavior", "Expected the behavior domain.");
            Require(entry.ContentJson != null && Encoding.UTF8.GetByteCount(entry.ContentJson) <= ContentPackDiscovery.MaximumEntryBytes,
                "Behavior source exceeds 64 MiB.");
            References(entry.References);
            using var parsed = JsonDocument.Parse(entry.ContentJson!, PublishedResources.JsonOptions);
            var content = parsed.RootElement;
            RuntimeJson.ValidateJson(content);
            RuntimeJson.Shape(content, "kind revision document", "version");
            var kind = RuntimeJson.Text(content, "kind");
            Require(kind is "behavior" or "module", "Unknown behavior source kind.");
            Require(PublishedResources.IsSha256(RuntimeJson.Text(content, "revision")), "Invalid declared behavior revision.");
            string? version = null;
            if (kind == "module")
            {
                Require(content.TryGetProperty("version", out var declared), "Module version is required.");
                version = RuntimeJson.Text(declared);
                Require(ContentIdentity.IsVersion(version), "Invalid module version.");
            }
            else Require(!content.TryGetProperty("version", out _), "A behavior cannot declare a module version.");
            Document(content.GetProperty("document"), version);
            return ContentRegistrationResult.Accept();
        }
        catch (RuntimeContractException error) { return ContentRegistrationResult.Reject(Invalid, error.Message); }
        catch (JsonException) { return ContentRegistrationResult.Reject(Invalid, "Malformed or over-depth behavior source JSON."); }
    }

    private static void References(IReadOnlyList<string> references)
    {
        Require(references.Count <= ContentPackDiscovery.MaximumEntries, "Behavior source reference budget exceeded.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            Require(reference != null, "Invalid behavior source reference.");
            foreach (var character in reference!)
                Require(character is >= (char)33 and <= (char)126, "Invalid behavior source reference spelling.");
            Require(ContentIdentity.TryParse(reference!, out _, out _) && seen.Add(reference!), "Invalid or duplicate behavior source reference.");
        }
        // Installed ownership, exact package versions, existence and cross-entry cycles remain discovery's
        // and dispatch's authority. A document id and module version are not package or stable-entry identities.
    }

    private static void Document(JsonElement document, string? version)
    {
        RuntimeJson.Shape(document, "format schemaVersion id name graph manifest limits layout", "contracts publication audio inputs");
        Require(RuntimeJson.Text(document, "format") == "gtfo-forge-behavior", "Unsupported behavior document format.");
        Require(RuntimeJson.Text(document, "schemaVersion") == ForgeCore.FormatVersion.Current, "Unsupported behavior document version.");
        RuntimeJson.Text(document, "id");
        var name = document.GetProperty("name");
        Require(name.ValueKind == JsonValueKind.String && name.GetString()!.Length is > 0 and <= 512
            && name.GetString()!.Trim().Length != 0, "Invalid behavior document name.");
        var manifest = document.GetProperty("manifest");
        Require(manifest.ValueKind is JsonValueKind.Null or JsonValueKind.Object, "Invalid behavior manifest container.");
        Object(document.GetProperty("limits"), "behavior limits");
        Object(document.GetProperty("layout"), "behavior layout");
        if (document.TryGetProperty("publication", out var publication)) Object(publication, "behavior publication");
        if (document.TryGetProperty("audio", out var audio)) Array(audio, 8, "behavior audio");
        if (document.TryGetProperty("inputs", out var inputs)) Array(inputs, 64, "behavior inputs");

        var graph = document.GetProperty("graph");
        RuntimeJson.Shape(graph, "schemaVersion domain authority nodes edges entrypoints");
        Require(RuntimeJson.Text(graph, "schemaVersion") == ForgeCore.FormatVersion.Current, "Unsupported behavior graph version.");
        Require(RuntimeJson.Text(graph, "domain") is "map" or "room" or "enemy" or "weapon" or "tool" or "consumable"
            or "player" or "session" or "logic" or "editor", "Unknown graph domain.");
        Require(RuntimeJson.Text(graph, "authority") is "host" or "owner" or "presentation", "Unknown graph authority.");
        var nodes = graph.GetProperty("nodes"); Array(nodes, MaximumNodes, "graph nodes");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var references = new List<(string Capability, string Version)>();
        foreach (var node in nodes.EnumerateArray())
        {
            RuntimeJson.Shape(node, "id capabilityId capabilityVersion bindingId parameters", "promotions inputs gate effect cost");
            var id = RuntimeJson.Text(node, "id");
            Require(NodeName.IsMatch(id) && ids.Add(id), "Invalid or duplicate graph node identity.");
            // Authoring references are only bounded text here. Runtime capability/binding availability and
            // execution semantics are checked against the separately compiled plan, never against this source.
            references.Add((ReferenceText(node.GetProperty("capabilityId")), ReferenceText(node.GetProperty("capabilityVersion"))));
            ReferenceText(node.GetProperty("bindingId"), allowEmpty: true);
            Object(node.GetProperty("parameters"), "graph parameters");
            if (node.TryGetProperty("promotions", out var promotions))
            {
                Array(promotions, 64, "graph promotions");
                var promoted = new HashSet<string>(StringComparer.Ordinal);
                foreach (var promotion in promotions.EnumerateArray())
                {
                    Require(promotion.ValueKind == JsonValueKind.String, "Invalid graph promotion.");
                    var port = promotion.GetString()!;
                    Require(PortName.IsMatch(port) && promoted.Add(port), "Invalid or duplicate graph promotion.");
                }
            }
            if (node.TryGetProperty("inputs", out var literals))
            {
                Object(literals, "graph input literals");
                int count = 0;
                foreach (var literal in literals.EnumerateObject())
                    Require(++count <= 64 && PortName.IsMatch(literal.Name), "Invalid graph input literal name or count.");
            }
            foreach (var option in new[] { "gate", "effect", "cost" })
                if (node.TryGetProperty(option, out var block)) Object(block, "graph card option");
        }
        var edges = graph.GetProperty("edges"); Array(edges, MaximumEdges, "graph edges");
        foreach (var edge in edges.EnumerateArray())
        {
            RuntimeJson.Shape(edge, "from to");
            Endpoint(edge.GetProperty("from"), ids); Endpoint(edge.GetProperty("to"), ids);
        }
        var entrypoints = graph.GetProperty("entrypoints"); Array(entrypoints, MaximumNodes, "graph entrypoints");
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entrypoint in entrypoints.EnumerateArray())
        {
            var id = RuntimeJson.Text(entrypoint);
            Require(ids.Contains(id) && entries.Add(id), "Missing or duplicate graph entrypoint.");
        }
        Module(document, references, version);
    }

    private static void Module(JsonElement document, List<(string Capability, string Version)> nodes, string? version)
    {
        var boundaries = new HashSet<string>(StringComparer.Ordinal);
        if (document.TryGetProperty("contracts", out var contracts))
        {
            RuntimeJson.Shape(contracts, "providers capabilities bindings");
            foreach (var table in new[] { "providers", "capabilities", "bindings" })
            {
                var rows = contracts.GetProperty(table);
                Require(rows.ValueKind == JsonValueKind.Array, "Invalid behavior contracts table.");
                foreach (var row in rows.EnumerateArray()) Object(row, "behavior contract row");
            }
            foreach (var row in contracts.GetProperty("capabilities").EnumerateArray())
            {
                Require(row.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.String
                    && row.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String,
                    "Missing capability declaration identity.");
                // This is exactly isBehaviorModule's classification, not a prefix match on arbitrary nodes.
                if (owner.GetString() != "forge.contract.module" || !Boundary.IsMatch(row.GetProperty("id").GetString()!)) continue;
                var capability = row.GetProperty("id").GetString()!;
                Require(version != null && boundaries.Add(capability), "Behavior kind disagrees with module boundary declarations.");
                Require(row.TryGetProperty("version", out var declared) && declared.ValueKind == JsonValueKind.String
                    && declared.GetString() == version, "Module boundary declaration version mismatch.");
            }
        }
        Require((boundaries.Count != 0) == (version != null), "Behavior kind disagrees with its declared module interface.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!boundaries.Contains(node.Capability)) continue;
            Require(node.Version == version, "Module boundary node version mismatch.");
            found.Add(node.Capability);
        }
        Require(found.SetEquals(boundaries), "Module boundary declaration has no corresponding node.");
    }

    private static void Endpoint(JsonElement endpoint, HashSet<string> nodes)
    {
        RuntimeJson.Shape(endpoint, "node port");
        Require(nodes.Contains(RuntimeJson.Text(endpoint, "node")), "Graph edge references a missing node.");
        var port = endpoint.GetProperty("port");
        Require(port.ValueKind == JsonValueKind.String && PortName.IsMatch(port.GetString()!), "Invalid graph edge port.");
    }
    private static string ReferenceText(JsonElement value, bool allowEmpty = false)
    {
        Require(value.ValueKind == JsonValueKind.String, "Invalid graph reference.");
        var text = value.GetString()!;
        Require(text.Length <= 256 && (allowEmpty || text.Length != 0), "Invalid graph reference length.");
        return text;
    }
    private static void Object(JsonElement value, string name) => Require(value.ValueKind == JsonValueKind.Object, "Invalid " + name + " container.");
    private static void Array(JsonElement value, int maximum, string name)
        => Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum, "Invalid " + name + " count or container.");
    private static void Require(bool condition, string message) => RuntimeJson.Require(condition, Invalid, message);
}

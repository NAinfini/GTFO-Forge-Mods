using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>The fact outputs that identify an equipment attachment independently of the event's subject.</summary>
public sealed record EquipmentAttachmentPorts(string Holder, string Equipment)
{
    internal static (PortView Holder, PortView Equipment)? Resolve(JsonElement binding, JsonElement capability)
    {
        if (!binding.TryGetProperty("equipmentAttachment", out var roles)) return null;
        RuntimeJson.Shape(roles, "holder equipment");
        RuntimeJson.Require(RuntimeJson.Text(capability, "kind") == "trigger", RuntimeAbiCodes.AttachmentPort,
            RuntimeJson.Text(binding, "id"));
        var outputs = RuntimeJson.Rows(capability.GetProperty("graph"), "outputs");
        return (Port("holder", EntityKinds.Player), Port("equipment", EntityKinds.Equipment));

        PortView Port(string role, string kind)
        {
            var name = RuntimeJson.Text(roles, role);
            var port = outputs.FirstOrDefault(output => RuntimeJson.Text(output, "id") == name);
            RuntimeJson.Require(port.ValueKind == JsonValueKind.Object && RuntimeJson.Text(port, "type") == "entity"
                && !RuntimeGraphContracts.Many(port) && !port.TryGetProperty("when", out _)
                && port.TryGetProperty("entityKinds", out var kinds) && RuntimeJson.Strings(kinds).Contains(kind),
                RuntimeAbiCodes.AttachmentPort, name);
            return PortView.Of(port);
        }
    }
}

/// <summary>One binding, including the permission and ABI shape that belong to it. Declaration-time data only.</summary>
public sealed record ProviderBinding(string Id, string CapabilityId, string Handler, string Role,
    IReadOnlyList<string> RequiredPermissions, HandlerShape? Shape = null)
{
    public string Status { get; init; } = "implemented";
    public string Verification { get; init; } = "implementation-only";
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();
    public EquipmentAttachmentPorts? EquipmentAttachment { get; init; }
    /// <summary>The provider's statement that a `wait_event` step may name this trigger binding: the first event it publishes
    /// resumes the wait. Only an `observe` binding of a trigger row may say so.</summary>
    public bool Waitable { get; init; }

    internal object Row(string providerId)
    {
        var row = new Dictionary<string, object>
        {
            ["id"] = Id,
            ["capabilityId"] = CapabilityId,
            ["providerId"] = providerId,
            ["handler"] = Handler,
            ["role"] = Role,
            ["status"] = Status,
            ["dependencies"] = Dependencies,
            ["requires"] = Requires
        };
        if (EquipmentAttachment != null) row.Add("equipmentAttachment", EquipmentAttachment);
        if (Waitable) row.Add("waitable", true);
        return row;
    }
}

/// <summary>A provider's single registration declaration. Contract rows retain their reviewed graphs; binding
/// permissions and shapes travel with the binding instead of separate provider-wide tables.</summary>
public sealed class ProviderDeclaration
{
    private readonly List<JsonElement> capabilities = new();
    private readonly List<ProviderBinding> bindings = new();

    public ProviderDeclaration(string id, string version, string? package = null, params string[] dependencies)
    {
        Id = id;
        Version = version;
        Package = package;
        Dependencies = Array.AsReadOnly(dependencies.ToArray());
    }

    private ProviderDeclaration(ProviderDeclaration source)
    {
        Id = source.Id; Version = source.Version; Kind = source.Kind; Package = source.Package; Dependencies = source.Dependencies;
        capabilities.AddRange(source.capabilities);
        bindings.AddRange(source.bindings);
    }

    public string Id { get; }
    public string Version { get; }
    public string Kind { get; init; } = "native";
    public string? Package { get; }
    public IReadOnlyList<string> Dependencies { get; }
    public IReadOnlyList<JsonElement> Capabilities => capabilities.AsReadOnly();
    public IReadOnlyList<ProviderBinding> Bindings => bindings.AsReadOnly();

    public ProviderDeclaration Declare(params object[] rows) => Declare((IEnumerable<object>)rows);

    public ProviderDeclaration Declare(IEnumerable<object> rows)
    {
        var next = new ProviderDeclaration(this);
        foreach (var value in rows)
        {
            var row = value is JsonElement json ? json.Clone() : RuntimeJson.From(value);
            RuntimeJson.Require(RuntimeJson.Text(row, "owner") == Id, "capability-owner", Id);
            var id = RuntimeJson.Id(row, "id");
            RuntimeJson.Require(!next.capabilities.Any(existing => RuntimeJson.Text(existing, "id") == id),
                "capability-conflict", id);
            next.capabilities.Add(row);
        }
        return next;
    }

    /// <summary>Declares every row the generated table (<see cref="Blocks"/>) gives this provider. A registration that
    /// has no body for some of them names those ids in <paramref name="except"/> and they stay undeclared; an id
    /// named there that is not one of this provider's rows is a fault in the caller and is refused.</summary>
    public ProviderDeclaration DeclareBlocks(params string[] except)
    {
        var next = new ProviderDeclaration(this);
        var owned = 0;
        var skipped = 0;
        for (var index = 0; index < Blocks.Count; index++)
        {
            var id = Blocks.Rows[index].Id;
            if (Blocks.Rows[index].Provider != Id) continue;
            owned++;
            if (Array.IndexOf(except, id) >= 0) { skipped++; continue; }
            RuntimeJson.Require(!next.capabilities.Any(existing => RuntimeJson.Text(existing, "id") == id), "capability-conflict", id);
            next.capabilities.Add(Blocks.Capability(index));
        }
        RuntimeJson.Require(owned > 0, "primitive-contract-provider", "No block belongs to " + Id + ".");
        RuntimeJson.Require(skipped == except.Distinct(StringComparer.Ordinal).Count(), "capability-owner",
            Id + " names a block it does not own: " + string.Join(", ", except));
        return next;
    }

    public ProviderDeclaration Bind(params ProviderBinding[] rows)
    {
        var next = new ProviderDeclaration(this);
        foreach (var row in rows)
        {
            RuntimeJson.Require(!next.bindings.Any(existing => existing.Id == row.Id), "binding-conflict", row.Id);
            next.bindings.Add(row with
            {
                RequiredPermissions = Array.AsReadOnly(row.RequiredPermissions.ToArray()),
                Dependencies = Array.AsReadOnly(row.Dependencies.ToArray()),
                Requires = Array.AsReadOnly(row.Requires.ToArray())
            });
        }
        return next;
    }

    internal JsonElement Provider => RuntimeJson.From(new { id = Id, kind = Kind, version = Version, dependencies = Dependencies });

    public JsonElement Registry => RuntimeJson.From(new
    {
        providers = new[] { Provider },
        capabilities,
        bindings = bindings.Select(row => row.Row(Id)).ToArray()
    });

    public IReadOnlyList<BindingSupport> BindingSupport => bindings.Select(row =>
        new BindingSupport(row.Id, row.Verification, row.RequiredPermissions)).ToArray();

    public IReadOnlyDictionary<string, HandlerShape> Shapes => bindings.Where(row => row.Shape != null)
        .GroupBy(row => row.Handler, StringComparer.Ordinal).ToDictionary(group => group.Key, group =>
        {
            var shape = group.First().Shape!;
            RuntimeJson.Require(group.All(row => ReferenceEquals(row.Shape, shape)), "shape-conflict", group.Key);
            return shape;
        }, StringComparer.Ordinal);

    public IReadOnlyList<string> CommandHandlers => bindings.Where(row => row.Shape != null && row.Role == "execute")
        .Select(row => row.Handler).Distinct(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<string> EvaluatorHandlers => bindings.Where(row => row.Shape != null && row.Role is "observe" or "evaluate")
        .Select(row => row.Handler).Distinct(StringComparer.Ordinal).ToArray();

    public RuntimeModule Module(string apiFingerprint, IReadOnlyDictionary<string, CommandHandler>? handlers = null,
        IReadOnlyDictionary<string, EvaluatorHandler>? evaluators = null,
        IReadOnlyDictionary<string, Func<EntityReference, bool>>? entityResolvers = null) =>
        new(RuntimeKernel.ApiVersion, apiFingerprint, this, handlers ?? new Dictionary<string, CommandHandler>(), entityResolvers)
        {
            Evaluators = evaluators ?? new Dictionary<string, EvaluatorHandler>()
        };
}

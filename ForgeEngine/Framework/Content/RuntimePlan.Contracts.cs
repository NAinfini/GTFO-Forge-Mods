using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

internal static partial class RuntimePlan
{
    // One synchronous LoadPlans batch owns this table. Only immutable registration and contract derivations are
    // shared: plan frames, values, wiring, resources, permissions and global variable declarations remain per plan.
    // A later load starts with a new table, so registration/removal never leaves stale parse state behind.
    internal sealed class ParseTable
    {
        private readonly RuntimeRegistry registry;
        private readonly Dictionary<string, BindingContract> bindings = new(StringComparer.Ordinal);
        internal ParseTable(RuntimeRegistry registry) { this.registry = registry; }
        internal BindingContract Binding(string id)
        {
            if (!bindings.TryGetValue(id, out var entry)) bindings.Add(id, entry = new BindingContract(registry, id));
            return entry;
        }
    }

    internal sealed class BindingContract
    {
        internal readonly JsonElement Binding, Capability, Graph;
        internal readonly string CapabilityId, ProviderId, HandlerName;
        private JsonElement[]? parameters;
        private string[]? parameterNames;
        private IReadOnlyList<string>? requiredActorRoles;
        // Read lazily: pins never touch parameters, and Load refuses a node without them before any reader runs.
        internal JsonElement[] Parameters => parameters ??= RuntimeJson.Rows(Graph, "parameters");
        internal string[] ParameterNames => parameterNames ??= Parameters.Select(parameter => RuntimeJson.Text(parameter, "id")).ToArray();
        internal IReadOnlyList<string> RequiredActorRoles => requiredActorRoles ??= RuntimeActorRoles.Required(Capability);
        internal readonly int SubjectOutput;
        internal readonly (PortView Holder, PortView Equipment)? EquipmentAttachment;
        private readonly Dictionary<string, NodeContract> contracts = new(StringComparer.Ordinal);
        // Reused for every node's cache key: the layout is serialised into this buffer and only its digest becomes a string.
        private readonly System.Buffers.ArrayBufferWriter<byte> keyBytes = new();
        private Utf8JsonWriter? keyWriter;
        private string ConstantsKey(JsonElement node)
        {
            keyBytes.Clear();
            if (keyWriter == null) keyWriter = new Utf8JsonWriter(keyBytes); else keyWriter.Reset(keyBytes);
            keyWriter.WriteStartArray();
            node.GetProperty("constants").WriteTo(keyWriter);
            node.GetProperty("promoted").WriteTo(keyWriter);
            keyWriter.WriteEndArray();
            keyWriter.Flush();
            return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(keyBytes.WrittenSpan));
        }
        /// <summary>The block id as the layout hash reads it.</summary>
        internal readonly byte[] CapabilityIdUtf8;
        internal BindingContract(RuntimeRegistry registry, string id)
        {
            Binding = registry.Bindings[id];
            CapabilityId = RuntimeJson.Text(Binding, "capabilityId"); ProviderId = RuntimeJson.Text(Binding, "providerId");
            HandlerName = RuntimeJson.Text(Binding, "handler"); Capability = registry.Capabilities[CapabilityId];
            CapabilityIdUtf8 = System.Text.Encoding.UTF8.GetBytes(CapabilityId);
            EquipmentAttachment = EquipmentAttachmentPorts.Resolve(Binding, Capability);
            Capability.TryGetProperty("graph", out Graph);
            var subject = RuntimeGraphContracts.SubjectPort(Graph);
            SubjectOutput = subject.ValueKind == JsonValueKind.Undefined ? -1 : Array.FindIndex(
                RuntimeJson.Rows(Graph, "outputs").Where(port => RuntimeJson.Text(port, "type") != "execution").ToArray(),
                port => RuntimeJson.Text(port, "id") == RuntimeJson.Text(subject, "id"));
        }
        /// <summary>The contract this binding resolves to for one plan node's constants and promotions, with the
        /// canonical layout bytes the plan's layout hash is computed from. Nodes with the same constants share it.</summary>
        internal NodeContract Resolve(JsonElement node, JsonElement[] constants, IReadOnlySet<string> promoted, string nodeId)
        {
            // The serialised constants keep literal object order and numeric spelling; sharing cannot change resolved JSON bytes.
            var key = ConstantsKey(node);
            var cached = contracts.TryGetValue(key, out var result);
            Dictionary<string, JsonElement>? keyed = cached ? null : new(StringComparer.Ordinal);
            for (var i = 0; i < Parameters.Length; i++)
            {
                var name = ParameterNames[i];
                if (promoted.Contains(name))
                {
                    if (constants[i].ValueKind != JsonValueKind.Null) throw new RuntimeContractException("promoted-constant", nodeId + "." + name);
                    continue;
                }
                if (constants[i].ValueKind != JsonValueKind.Null) { keyed?.Add(name, constants[i]); continue; }
                if (RuntimeJson.Flag(Parameters[i], "required")) throw new RuntimeContractException("missing-constant", nodeId + "." + name);
            }
            if (cached) return result!;
            var parameters = RuntimeJson.From(keyed!);
            var contract = RuntimeGraphContracts.Resolve(Graph, parameters, promoted);
            var inputLayout = RuntimeJson.StableBytes(RuntimeGraphContracts.Layout(contract, "inputs")).ToArray();
            var outputLayout = RuntimeJson.StableBytes(RuntimeGraphContracts.Layout(contract, "outputs")).ToArray();
            var authoritative = AuthoritativeContract(contract, nodeId);
            result = new NodeContract(parameters, authoritative, RuntimeJson.Rows(authoritative, "inputs"),
                RuntimeJson.Rows(authoritative, "outputs"), inputLayout, outputLayout);
            contracts.Add(key, result);
            return result;
        }
    }
    /// <summary>One resolved node contract, shared by every node of a batch that has the same binding and constants. What
    /// is derived from the contract alone is derived once here, not once per node.</summary>
    internal sealed record NodeContract(JsonElement Parameters, JsonElement Contract, JsonElement[] Inputs, JsonElement[] Outputs,
        byte[] InputLayout, byte[] OutputLayout)
    {
        internal JsonElement[] InputExecution { get; } = Execution(Inputs, true);
        internal JsonElement[] OutputExecution { get; } = Execution(Outputs, true);
        internal JsonElement[] InputValues { get; } = Execution(Inputs, false);
        internal JsonElement[] OutputValues { get; } = Execution(Outputs, false);
        /// <summary>An entity, resource or handle port that carries a value across the node's boundary, on either side.</summary>
        internal bool WorldPorts { get; } = Inputs.Concat(Outputs).Any(port => RuntimeGraphContracts.WorldPort(RuntimeJson.Text(port, "type")));
        private bool handlerParametersKnown;
        private JsonElement handlerParameters;
        /// <summary>The parameters a handler of a step with no promoted parameter reads: enum indices resolved to member names.
        /// A parameter set the resolution refuses stays undefined, so the dispatch asks again and refuses where it always did.</summary>
        internal JsonElement HandlerParametersFor(JsonElement capability)
        {
            if (!handlerParametersKnown)
            {
                try { handlerParameters = RuntimeJson.ResolveEnumParameters(Parameters, capability); }
                // Only a refused parameter set (an index past its member list, a value of the wrong JSON kind) is
                // remembered as undefined; a program error is not a parameter set and still throws.
                catch (Exception error) when (error is RuntimeContractException or ArgumentOutOfRangeException
                    or InvalidOperationException or KeyNotFoundException)
                {
                    handlerParameters = default;
                }
                handlerParametersKnown = true;
            }
            return handlerParameters;
        }
        private static JsonElement[] Execution(JsonElement[] ports, bool execution)
            => ports.Where(port => (RuntimeJson.Text(port, "type") == "execution") == execution).ToArray();
    }
}

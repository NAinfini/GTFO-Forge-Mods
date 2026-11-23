using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    /// <summary>
    /// Resolves port shape from an exact registered capability revision, preserving metadata.
    /// Only the variable count is evaluated; other parameter values still require their node's
    /// validation. This read-only result is not a binding, execution plan or permission grant.
    /// </summary>
    public JsonElement ResolveGraphContract(string capabilityId, string capabilityVersion, JsonElement parameters)
    {
        ReadThread(); RuntimeJson.Text(capabilityId); RuntimeJson.Text(capabilityVersion);
        RuntimeJson.Require(registry.Tables.Capabilities.TryGetValue(capabilityId, out var capability),
            "capability-unavailable", capabilityId);
        RuntimeJson.Require(RuntimeJson.Text(capability, "version") == capabilityVersion,
            "capability-version", capabilityId);
        RuntimeJson.Require(capability.TryGetProperty("graph", out var graph), "graph-unavailable", capabilityId);
        var captured = RuntimeJson.Parse(parameters.GetRawText());
        RuntimeJson.Shape(captured, "", string.Join(" ", RuntimeJson.Rows(graph, "parameters")
            .Select(p => RuntimeJson.Text(p, "id"))));
        return RuntimeGraphContracts.Resolve(graph, captured);
    }
}

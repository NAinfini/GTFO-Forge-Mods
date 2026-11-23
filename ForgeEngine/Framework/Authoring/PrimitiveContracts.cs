using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>The registry's reads of the generated block table (<see cref="Blocks"/>). A provider declares its rows from
/// the table instead of carrying a copy, so a sentence or a port has exactly one written form.</summary>
internal static class PrimitiveContracts
{
    /// <summary>One reviewed contract provider: every row the table gives it, with no handwritten registry envelope.</summary>
    internal static ProviderDeclaration Declaration(string providerId) =>
        new ProviderDeclaration(providerId, EnginePackage.Version).DeclareBlocks();

    /// <summary>One row of <paramref name="providerId"/> by id. An id that provider does not own is a fault in the
    /// caller, not a row to invent, so it is refused with <paramref name="code"/>.</summary>
    internal static JsonElement Row(string providerId, string capabilityId, string code)
    {
        var index = Blocks.IndexOf(capabilityId);
        RuntimeJson.Require(index >= 0 && Blocks.Rows[index].Provider == providerId, code,
            providerId + " declares no " + capabilityId + ".");
        return Blocks.Capability(index);
    }

    /// <summary>The reviewed graph of one block. An id the table does not hold is the caller's error and throws.</summary>
    internal static JsonElement Graph(string capabilityId) =>
        Blocks.Capability(Blocks.IndexOf(capabilityId)).GetProperty("graph");
}

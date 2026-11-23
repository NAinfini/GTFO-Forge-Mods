using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>Shared combat semantics. Receiver implementations register separate explicit bindings. The rows are
/// the generated block table's (<see cref="Blocks"/>), so a sentence a registration publishes is the catalog's own
/// text with no second copy to keep in step.</summary>
internal static class CombatContracts
{
    private const string ProviderId = "forge.contract.combat";
    public static RuntimeModule Module() => PrimitiveContracts.Declaration(ProviderId).Module(RuntimeApi.Fingerprint);

    /// <summary>The sourced attribute apply row and the generic effect-cancel row owned by the shared combat contract.
    /// Native providers bind these rows without restating their semantic graphs.</summary>
    public const string AttributeApplyCapabilityId = Blocks.ActionCombatAttributeApply.Id;

    /// <summary>The foam row: one combat row whose recipients are enemies and doors, so the enemy package and the
    /// map package each bind it for the kind they own. It is declared here rather than by either of them because
    /// a capability has exactly one owner and both providers answer it.</summary>
    public const string FoamingCapabilityId = Blocks.ActionCombatFoaming.Id;

    /// <summary>The canonical handle cancellation outcome replacing the legacy attribute-remove row.</summary>
    public const string EffectCancelCapabilityId = Blocks.ActionEffectCancel.Id;

    /// <summary>Both rows as the registry parses them, read from the table by id, so a provider that binds them
    /// reads the shape this provider publishes instead of a copy of its text.</summary>
    public static readonly JsonElement AttributeApplyCapability = Capability(AttributeApplyCapabilityId);

    /// <summary>The foam row read from the table: the enemy package and the map package each bind it for their own
    /// recipient kind without restating its graph.</summary>
    public static readonly JsonElement FoamingCapability = Capability(FoamingCapabilityId);

    /// <summary>The reviewed effect-cancel row read from the table.</summary>
    public static readonly JsonElement EffectCancelCapability = Capability(EffectCancelCapabilityId);

    private static JsonElement Capability(string capabilityId) =>
        PrimitiveContracts.Row(ProviderId, capabilityId, "combat-contract-row");
}

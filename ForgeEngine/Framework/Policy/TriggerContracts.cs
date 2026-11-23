using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>Every Trigger capability shape the runtime publishes, declared once by the contract provider
/// `forge.contract.trigger`. A domain package binds one of these ids to its own observation and registers no
/// shape of its own, so an id has exactly one owner and the catalog row, this declaration and the registered
/// manifest cannot drift apart. This provider implements no binding, so it registers capabilities only.
///
/// The rows are the generated block table's (<see cref="Blocks"/>), not written here. A label, description or
/// authoring parameter a registration carries is therefore the catalog's own text with no second copy to keep in
/// step — a sentence change belongs in `Tools/Forge/capability-copy.json`, never in this file. What stays here is
/// identity: the provider, and which ids `Rows` answers for.
///
/// Three ports are declared `optional` by their reviewed graph: `combat.killed.source`, `combat.damage_applied.limb`
/// and `objective.wave_*.wave`. Each one names a fact the observing provider only sometimes holds — an unattributed
/// kill, a hit that named no part, a wave instance this runtime mints no handle for — and a required port the
/// publisher cannot fill would fail the kernel's own event shape check on every publish, so the port is declared
/// absent-able instead of always-null.</summary>
internal static class TriggerContracts
{
    private const string ProviderId = "forge.contract.trigger";
    public static RuntimeModule Module() => PrimitiveContracts.Declaration(ProviderId).Module(RuntimeApi.Fingerprint);

    /// <summary>
    /// The shipped rows a domain package binds, by id, in the order asked for. A package's own tests register
    /// these rows under a fixture provider instead of restating the shape, so a port this file changes fails the
    /// package's cases rather than drifting in a private copy. An id this contract does not declare is a fault in
    /// the caller, not a row to invent, so it is refused by name.
    /// </summary>
    public static IReadOnlyList<JsonElement> Rows(IReadOnlyList<string> capabilityIds)
    {
        ArgumentNullException.ThrowIfNull(capabilityIds);
        var rows = new JsonElement[capabilityIds.Count];
        for (var index = 0; index < capabilityIds.Count; index++)
            rows[index] = PrimitiveContracts.Row(ProviderId, capabilityIds[index], "trigger-contract-row");
        return Array.AsReadOnly(rows);
    }
}

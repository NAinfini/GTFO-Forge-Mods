using System;

namespace ForgeEngine.Framework;

/// <summary>
/// The status contract provider `forge.contract.status`: the row the kernel itself answers for, declared once with
/// the binding that carries it. It is kernel state, not a package's — the status instances belong to the effect
/// table and the ending fact is published from the one place an instance ends — so no domain package binds it and
/// no provider body is registered for it.
///
/// A row is registered with its binding here for the same reason every other row is: a plan pins a binding, and a
/// row nothing binds is a row an author can see in the catalog and never place. The ending is observed by mounting
/// a card on <see cref="EndedBinding"/>.
///
/// The row is the generated block table's (<see cref="Blocks"/>), not written here. A sentence change belongs in
/// `Tools/Forge/capability-copy.json`, never in this file; what stays here is identity — the provider and its binding.
/// </summary>
internal static class StatusContracts
{
    /// <summary>The provider that owns the row: the runtime's own, like the other contract providers.</summary>
    public const string ProviderId = "forge.contract.status";

    /// <summary>The binding a card mounts on to hear that one declared status instance of one receiver ended. The
    /// kernel publishes the fact through every binding the registry holds for the row, so this id is what a plan
    /// pins and what the ending reaches.</summary>
    public const string EndedBinding = ProviderId + ".binding.ended";

    public static RuntimeModule Module() => PrimitiveContracts.Declaration(ProviderId)
        .Bind(new ProviderBinding(EndedBinding, Blocks.TriggerStatusEnded.Id, ProviderId + ".handler.ended",
            "observe", Array.Empty<string>())).Module(RuntimeApi.Fingerprint);
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The three `forge.trigger.combat.burst_started` / `burst_ended` / `dry_fire` rows this family binds: a burst
/// sequence opening and closing, counted by the weapon's own `m_burstMax`, and the weapon's own empty-clip path
/// (`BWA_Burst.OnFireShotEmptyClip` / `BWA_Auto.OnFireShotEmptyClip`).
///
/// <b>Every one of the three is declared by the framework's trigger contract.</b> They are reviewed Primitive
/// rows of the catalog, so `forge.contract.trigger` carries their catalog text and the reviewed graph
/// (`ForgeEngine/Framework/Policy/TriggerContracts.cs`), and a second declaration of one of these ids here would be
/// refused at registration as a conflict. What this file owns is the other half: the ids, the observe bindings
/// and the handler names the native half answers to. The shipped rows are read back from that contract where a
/// caller needs them, so a label or a port list written twice — the drift the one contract exists to prevent —
/// cannot appear here.
///
/// Nothing here reads a plan, a world or a game type: the whole file is vocabulary and ids.
/// </summary>
internal static class WeaponBurstContract
{
    /// <summary>The provider these bindings belong to: this package's own.</summary>
    public const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>A burst sequence opened, counted by the weapon's own `m_burstMax`.</summary>
    public const string BurstStartedCapability = Blocks.TriggerCombatBurstStarted.Id;
    /// <summary>A burst sequence closed. `count` is the weapon's own burst length, never a Forge tally.</summary>
    public const string BurstEndedCapability = Blocks.TriggerCombatBurstEnded.Id;
    /// <summary>The weapon's own empty-clip path ran: `BWA_Burst.OnFireShotEmptyClip` / `BWA_Auto.OnFireShotEmptyClip`.</summary>
    public const string DryFireCapability = Blocks.TriggerCombatDryFire.Id;

    public const string BurstStartedBinding = ProviderId + ".binding.burst_started";
    public const string BurstEndedBinding = ProviderId + ".binding.burst_ended";
    public const string DryFireBinding = ProviderId + ".binding.dry_fire";

    /// <summary>The permission the observation half of every row here reads under. It is the same
    /// `gtfo.weapon.combat.read` the package's `shot_committed` binding already declares: these facts come from
    /// the weapon's own body and from the equipment life the adapter recorded, and nothing here writes.</summary>
    public const string CombatReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The capability ids this family binds, in catalog order.</summary>
    public static readonly IReadOnlyList<string> CapabilityIds = Array.AsReadOnly(new[]
    {
        BurstStartedCapability, BurstEndedCapability, DryFireCapability
    });

    /// <summary>The handler name a binding resolves to. The native half supplies these bodies through
    /// <c>WeaponNativeSession.Attack</c>, so the row is registered by the integration batch together with that
    /// handler set or not at all.</summary>
    public static string Handler(string capability) => capability switch
    {
        BurstStartedCapability => "gtfo.weapon.burst_started",
        BurstEndedCapability => "gtfo.weapon.burst_ended",
        DryFireCapability => "gtfo.weapon.dry_fire",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability,
            "No such attack-instance row.")
    };

    // ---- the shipped rows --------------------------------------------------------------------------------

    /// <summary>The three rows as the runtime's trigger contract carries them, in <see cref="CapabilityIds"/>
    /// order: the catalog's own label and sentence with the reviewed Primitive's own graph, read back rather than
    /// written a second time here. This family's own cases hold the ports the native half fills to that shape —
    /// the shape the kernel checks a published fact with.</summary>
    public static IReadOnlyList<JsonElement> RowsJson() => TriggerContracts.Rows(CapabilityIds);

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BurstStartedBinding, BurstStartedCapability, "gtfo.weapon.burst_started", "observe", new[] { CombatReadPermission }),
        new ProviderBinding(BurstEndedBinding, BurstEndedCapability, "gtfo.weapon.burst_ended", "observe", new[] { CombatReadPermission }),
        new ProviderBinding(DryFireBinding, DryFireCapability, "gtfo.weapon.dry_fire", "observe", new[] { CombatReadPermission })
    };
}

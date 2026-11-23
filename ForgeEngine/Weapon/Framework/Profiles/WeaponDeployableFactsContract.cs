using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The deployed-device facts the node list names beyond the life-cycle pair this package already binds:
/// a device that really fired, a device that ran out of ammunition, and a device that detonated.
///
/// The three rows are canonical catalog ids, and each one is here because the existing catalog rows answer a
/// different question. `forge.trigger.equipment.expired` is a life ending, which a mine's detonation is not: a
/// glue or explosive mine detonates and the instance is destroyed as a consequence, and an author who reads
/// `expired` learns "it is gone", not "it went off here". A timer running out is the explosive mine's own trigger
/// but not the glue mine's, and not the sentry's depletion either.
///
/// The glue-gun half of the same question is `fired` as well: the node list's `e-glue` is "the glue gun fired",
/// and the enemy that ends up glued is the enemy domain's fact, not this one. The `equipment_kind` port is what
/// tells the two apart, and the members published under it are the shells this package really hooks — the
/// `equipment_kind` table `WeaponPlacementContract` compiles an index out of.
///
/// <b>Every one of the three rows is declared by the runtime's trigger contract.</b> All three are reviewed
/// Primitive rows of the catalog, so `forge.contract.trigger` carries their label, sentence and graph
/// (`ForgeEngine/Framework/Policy/TriggerContracts.cs`) and a second declaration of one of these ids here would be
/// refused at registration as a conflict. What this file owns is the ids, the observe bindings and the support
/// lines; the shipped rows are read back from that contract where a caller needs them.
/// </summary>
internal static class WeaponDeployableFactsContract
{
    /// <summary>A deployed device or tool really produced one shot.</summary>
    public const string FiredCapability = Blocks.TriggerEquipmentFired.Id;
    /// <summary>A deployed device's ammunition reached zero.</summary>
    public const string AmmoDepletedCapability = Blocks.TriggerEquipmentAmmoDepleted.Id;
    /// <summary>A deployed device detonated.</summary>
    public const string DetonatedCapability = Blocks.TriggerEquipmentDetonated.Id;

    public const string FiredBinding = ModuleDefinition.ProviderId + ".binding.fired";
    public const string AmmoDepletedBinding = ModuleDefinition.ProviderId + ".binding.ammo_depleted";
    public const string DetonatedBinding = ModuleDefinition.ProviderId + ".binding.detonated";

    /// <summary>The permission all three facts are read under. They observe a placed device this provider already
    /// owns an identity for, so they need the same deployable read the life-cycle rows carry and nothing more.</summary>
    public const string DeployableReadPermission = ModuleDefinition.DeployableReadPermission;

    /// <summary>One bound row: the capability and the binding that carries it.</summary>
    public sealed record Row(string Capability, string Binding);

    /// <summary>The three rows, in node-list order.</summary>
    public static readonly IReadOnlyList<Row> Rows = new[]
    {
        new Row(FiredCapability, FiredBinding),
        new Row(AmmoDepletedCapability, AmmoDepletedBinding),
        new Row(DetonatedCapability, DetonatedBinding)
    };

    /// <summary>The three shipped rows, read back from the runtime's trigger contract in the order above: the
    /// shape a plan compiles against is that provider's own row, so a port renamed there reaches this family
    /// without a second copy to keep in step.</summary>
    public static IReadOnlyList<JsonElement> RowsJson()
    {
        var ids = new string[Rows.Count];
        for (var index = 0; index < Rows.Count; index++) ids[index] = Rows[index].Capability;
        return TriggerContracts.Rows(ids);
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(FiredBinding, FiredCapability, FiredBinding, "observe", new[] { DeployableReadPermission }),
        new ProviderBinding(AmmoDepletedBinding, AmmoDepletedCapability, AmmoDepletedBinding, "observe", new[] { DeployableReadPermission }),
        new ProviderBinding(DetonatedBinding, DetonatedCapability, DetonatedBinding, "observe", new[] { DeployableReadPermission })
    };
}

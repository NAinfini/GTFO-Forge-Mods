using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The placement half of the deployed-device facts: the two rows a sentry or a mine is placed and recalled
/// under, and the one port that tells the two devices apart.
///
/// The two rows are the catalog's own and are reused rather than re-created.
/// `forge.trigger.equipment.deploy_completed` is the node list's "哨戒炮放下" and "绊雷放下": the world instance's
/// own spawn is the moment both families really become deployed — `SentryGunInstance.OnSpawn` is one of the three
/// bodies in this build that write the owning backpack's deployed marker — and it is also the one deploy signal
/// the mine family shares, because the mine never writes that marker at all.
/// `forge.trigger.equipment.recall_completed` is "收回": it is cleared by the instance's own `SyncedPickup`,
/// which the recall interaction reaches on every machine. Both are declared by the runtime's trigger contract, so
/// this file names their ids, their bindings and the ports they carry, and holds no row document of its own.
///
/// `equipment_kind` is the port that separates the two placements. It is the same set the device facts carry
/// (`sentry_gun`, `mine`, and the glue gun's `glue_gun`), so a plan that reads either family switches on one
/// vocabulary instead of on two row ids. The set is a closed enum and a fact carries a member's index, which is
/// what this file's own table answers for both the publication and the shape.
///
/// `zone` is deliberately not a port here. The level's zone is a resource of the map domain, read for any entity
/// through `forge.selector.target.zone`; putting a copy of it on the placement fact would be a second spelling of
/// a reference that query already owns, and the position this row does carry is what the query is anchored on.
/// </summary>
internal static class WeaponPlacementContract
{
    /// <summary>The placement fact: `e-sentry-place` and the placement half of `e-mine`.</summary>
    public const string DeployCapability = ModuleDefinition.DeployCompletedCapability;
    /// <summary>The recall fact: the other half of `e-sentry-place`.</summary>
    public const string RecallCapability = ModuleDefinition.RecallCompletedCapability;

    public const string DeployBinding = ModuleDefinition.DeployCompletedBinding;
    public const string RecallBinding = ModuleDefinition.RecallCompletedBinding;

    /// <summary>The permission both rows are read under. They observe a placement this provider already owns an
    /// identity for, so they need the same deployable read the life-cycle rows carry and nothing more.</summary>
    public const string DeployableReadPermission = ModuleDefinition.DeployableReadPermission;

    /// <summary>The three shells this package can name, in the compiled order of the set they belong to. That order
    /// is the wire contract: a fact carries a member's index, so this list and the framework's own
    /// `equipment_kind` set have to spell the same members in the same order. The integration fragment records the
    /// framework half; the focused cases pin this half, and the index a publication writes is read out of this one
    /// list rather than from a second copy somewhere else. `sentry_gun` and `mine` are the two placed devices;
    /// `glue_gun` is the hand-held launcher whose shot is the same kind of fact and travels with the same
    /// vocabulary.</summary>
    public static readonly IReadOnlyList<string> EquipmentKinds = new[] { "sentry_gun", "mine", "glue_gun" };

    /// <summary>Canonical member names, so a publication never spells one by hand.</summary>
    public const string SentryGunKind = "sentry_gun";
    public const string MineKind = "mine";
    public const string GlueGunKind = "glue_gun";

    /// <summary>The two declared rows, in the order they are published: a placement, then its recall.</summary>
    public static readonly IReadOnlyList<(string Capability, string Binding)> Rows = new[]
    {
        (DeployCapability, DeployBinding), (RecallCapability, RecallBinding)
    };

    /// <summary>The two rows as the trigger contract writes them: the placement row carrying the node list's own
    /// ports plus the kind, and the recall row. Both are the runtime's own rows — `forge.contract.trigger` declares
    /// every shape a trigger id has, and this package binds them — so they are read back from
    /// <see cref="TriggerContracts"/> rather than written a second time here. That is also why this file carries no
    /// sentence of its own: the label and the description a registration publishes are the catalog's, projected
    /// into the contract provider, and a copy here could only ever drift from them.</summary>
    public static IReadOnlyList<System.Text.Json.JsonElement> RowsJson()
        => TriggerContracts.Rows(new[] { DeployCapability, RecallCapability });

    /// <summary>The compiled value of one kind name: its position in the set, which is what a fact carries on an
    /// enum port. A name that is not in this file's own table is refused rather than published as an index into a
    /// set it does not belong to, so a misspelled member fails at the publication instead of at the reader.</summary>
    public static int? KindIndex(string? kind)
    {
        for (var position = 0; position < EquipmentKinds.Count; position++)
            if (string.Equals(EquipmentKinds[position], kind, StringComparison.Ordinal)) return position;
        return null;
    }

    /// <summary>The outputs of one placement, which is the whole port set of the placement row: the actor, the
    /// device, the kind when the machine could name it, and the place when the instance reported one. It is built
    /// here rather than in the native half so the row and the fact are one artifact — a port added to the row
    /// without a writer, or written without a port, is the drift this pairing exists to prevent. A port whose value
    /// this machine could not read is left out only where the row declares it optional (the place); the kind is the row's
    /// nullable port, so a kind this machine could not name is an explicit null, because the kernel refuses a fact that
    /// leaves a nullable port out. The place is the row's optional port and is left out when the instance reported none,
    /// so a reader never takes an unread place for one.
    /// </summary>
    public static void DeployOutputs(EvaluationOutputs values, EntityReference? actor, EntityReference deployed,
        int? kind, double[]? position)
    {
        ArgumentNullException.ThrowIfNull(deployed);
        values.Entity(Blocks.TriggerEquipmentDeployCompleted.OutActor, actor);
        values.Entity(Blocks.TriggerEquipmentDeployCompleted.OutDeployed, deployed);
        if (kind is { } member) values.Enum(Blocks.TriggerEquipmentDeployCompleted.OutEquipmentKind, member);
        else values.Null(Blocks.TriggerEquipmentDeployCompleted.OutEquipmentKind);
        if (position is { Length: 3 }) values.Vector(Blocks.TriggerEquipmentDeployCompleted.OutPosition, position[0], position[1], position[2]);
    }

    /// <summary>The outputs of one recall: the same three ports as a placement without the place, because a device
    /// being picked up has no position its own placement did not already report.</summary>
    public static void RecallOutputs(EvaluationOutputs values, EntityReference? actor, EntityReference deployed, int? kind)
    {
        ArgumentNullException.ThrowIfNull(deployed);
        values.Entity(Blocks.TriggerEquipmentRecallCompleted.OutActor, actor);
        values.Entity(Blocks.TriggerEquipmentRecallCompleted.OutDeployed, deployed);
        if (kind is { } member) values.Enum(Blocks.TriggerEquipmentRecallCompleted.OutEquipmentKind, member);
        else values.Null(Blocks.TriggerEquipmentRecallCompleted.OutEquipmentKind);
    }
}

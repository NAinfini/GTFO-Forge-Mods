using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The two read-only rows this package owns and evaluates on demand: the ammunition of one equipment
/// life (`forge.query.equipment.ammo`) and one item's pocket count in a holder's backpack
/// (`forge.query.player.item_count`). The equipment-ammunition row is this package's own id: authors first read the
/// wielded equipment with `forge.query.player.wielded_gear`, then pass that explicit equipment entity to
/// `forge.query.equipment.ammo`. The pocket count is the give action's own readback: it reads the count
/// `forge.action.inventory.give` moves in either direction, so no second read path is invented for it, and a plan
/// asks "does the player hold at least N" by comparing that count. Both rows are the generated block table's, and
/// both are answered by this package's native half through
/// <see cref="IInventoryQuerySource"/>: a process without that half refuses them by code rather than declaring a
/// row nothing answers.</summary>
internal static class InventoryQueryContract
{
    /// <summary>The equipment-ammunition row. Its canonical id is this provider's own: no contract in the
    /// runtime declares it.</summary>
    public const string EquipmentAmmoCapability = Blocks.QueryEquipmentAmmo.Id;
    /// <summary>The player-addressed pocket count: how many of one `item` resource the player's backpack holds,
    /// which is the same quantity `forge.action.inventory.give` changes.</summary>
    public const string PocketItemCountCapability = Blocks.QueryPlayerItemCount.Id;

    public const string EquipmentAmmoBinding = ModuleDefinition.ProviderId + ".binding.equipment_ammo";
    public const string PocketItemCountBinding = ModuleDefinition.ProviderId + ".binding.query.player.item_count";

    /// <summary>The two evaluator names. A name is also the shape key, so a registration cannot supply an
    /// evaluator whose ports were never declared.</summary>
    public const string EquipmentAmmoHandler = "gtfo.weapon.equipment_ammo";
    public const string PocketItemCountHandler = "gtfo.weapon.pocket_item_count";

    /// <summary>The permission these reads carry. They read the equipment a player holds and the backpack behind
    /// it — the same subject the wield and reload reads already carry — so the name is not split per row.</summary>
    public const string ReadPermission = ModuleDefinition.WieldReadPermission;

    /// <summary>The ports each evaluator really reads, resolved at registration against the capability's own
    /// ports. `equipment` is the life the ammunition row is about and its three magazine numbers (`clip_max` is
    /// what the weapon itself reports, so a partly filled magazine reads as one); the pocket count reads the holder
    /// and the item resource, and answers a number.</summary>
    public static readonly HandlerShape EquipmentAmmoShape = new HandlerShape()
        .Inputs("equipment").Outputs("clip", "clip_max", "reserve");
    public static readonly HandlerShape PocketItemCountShape = new HandlerShape()
        .Inputs("holder", "item").Outputs("count");

    /// <summary>The evaluator table the registration carries, taken from the reads that own the bodies so a shape
    /// and its evaluator can never be registered apart.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => InventoryQueryReads.Evaluators();

    /// <summary>`forge.query.equipment.ammo`: the magazine numbers.</summary>
    public static void AmmoAnswer(EquipmentAmmo ammo, EvaluationOutputs outputs)
    {
        outputs.Integer(0, ammo.Clip);
        outputs.Integer(1, ammo.ClipMaximum);
        outputs.Integer(2, ammo.Reserve);
    }


    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(EquipmentAmmoBinding, EquipmentAmmoCapability, EquipmentAmmoHandler, "observe", new[] { ReadPermission }, EquipmentAmmoShape),
        new ProviderBinding(PocketItemCountBinding, PocketItemCountCapability, PocketItemCountHandler, "observe", new[] { ReadPermission }, PocketItemCountShape)
    };
}

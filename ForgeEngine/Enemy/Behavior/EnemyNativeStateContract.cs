using System;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeEnemy;

internal static class EnemyNativeStateContract
{
    private static readonly string[] Ids = { Blocks.QueryEnemyInCombat.Id, Blocks.QueryEnemyLimb.Id, Blocks.ActionEnemyKnockdown.Id, Blocks.ActionEnemyLimbSet.Id,
        Blocks.TriggerEnemyCombatEntered.Id, Blocks.TriggerEnemyCombatLeft.Id, Blocks.TriggerEnemyKnockedDown.Id, Blocks.TriggerEnemyKnockdownRecovering.Id, Blocks.TriggerEnemyPhaseChanged.Id };
    internal static string Binding(string id) => ModuleDefinition.ProviderId + ".binding." + id["forge.".Length..];
    internal static string Handler(string id) => "gtfo.enemy." + id["forge.".Length..];
    internal static readonly HandlerShape KnockdownShape = new HandlerShape().Inputs("enemies").Outputs("result");
    internal static readonly HandlerShape LimbSetShape = new HandlerShape()
        .Inputs("enemies", "limb_id", "weakspot_multiplier", "armor_multiplier", "limb_type").Outputs("result");
    internal static readonly HandlerShape InCombatShape = new HandlerShape().Inputs("enemy").Outputs("value");
    internal static readonly HandlerShape LimbShape = new HandlerShape().Inputs("enemy", "limb_id")
        .Outputs("limb_type", "weakspot_multiplier", "armor_multiplier");
    internal static readonly HandlerSlot LimbReadIdSlot = LimbShape.InputSlot("limb_id");

    internal static ProviderBinding[] Bindings() => Ids.Select(id => new ProviderBinding(Binding(id), id, Handler(id),
        id == Blocks.ActionEnemyKnockdown.Id || id == Blocks.ActionEnemyLimbSet.Id ? "execute" : "observe",
        id == Blocks.ActionEnemyKnockdown.Id ? new[] { "gtfo.enemy.behavior.write" }
            : id == Blocks.ActionEnemyLimbSet.Id ? new[] { "gtfo.enemy.limbs.write" } : Array.Empty<string>(),
        id == Blocks.ActionEnemyKnockdown.Id ? KnockdownShape : id == Blocks.ActionEnemyLimbSet.Id ? LimbSetShape
            : id == Blocks.QueryEnemyInCombat.Id ? InCombatShape : id == Blocks.QueryEnemyLimb.Id ? LimbShape : null)).ToArray();
}

using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>Current enemy roster, whole or narrowed to the row's optional zone and range before it is counted;
/// relation selection is an explicit downstream operation.</summary>
internal static class EnemySelector
{
    internal const string CapabilityId = Blocks.SelectorTargetEnemies.Id;
    internal const string BindingId = ForgeEnemy.EnemySelectorContract.BindingId;
    internal const string HandlerName = ForgeEnemy.EnemySelectorContract.HandlerName;
    internal const string EntityKind = EntityKinds.Enemy;
    internal static void Evaluate(EvaluationContext context, EvaluationOutputs outputs) => EntitySelector.Evaluate(context, outputs, EntityKind, scoped: true);
}

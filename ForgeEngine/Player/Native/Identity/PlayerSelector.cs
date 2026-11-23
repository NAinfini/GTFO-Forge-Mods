using ForgeEngine.Framework;

namespace ForgePlayer.Native;

/// <summary>Enumerate the player kind. Faction and effect polarity are not roster filters.</summary>
internal static class PlayerSelector
{
    internal const string CapabilityId = ForgePlayer.PlayerSelectorContract.CapabilityId;
    internal const string BindingId = ForgePlayer.PlayerSelectorContract.BindingId;
    internal const string HandlerName = ForgePlayer.PlayerSelectorContract.HandlerName;
    internal static void Evaluate(EvaluationContext context, EvaluationOutputs outputs) => EntitySelector.Evaluate(context, outputs, EntityKinds.Player);
}

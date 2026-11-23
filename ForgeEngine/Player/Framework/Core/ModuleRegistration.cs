using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The one Player registration, used by the native session and the release exporter.</summary>
internal static partial class ModuleDefinition
{
    public const string ProviderId = "forge.module.gtfo.player";
    private static readonly ProviderDeclaration Declaration = Declare();

    public static ProviderDeclaration Describe() => Declaration;

    public static RuntimeModule Create(IReadOnlyDictionary<string, CommandHandler> handlers, IReadOnlyDictionary<string, EvaluatorHandler> evaluators)
        => Declaration.Module(RuntimeApi.Fingerprint, handlers, evaluators: evaluators);

    private static ProviderDeclaration Declare() => new ProviderDeclaration(ProviderId, EnginePackage.Version, EnginePackage.Name)
        .DeclareBlocks()
        .Bind(new ProviderBinding(PlayerSelectorContract.BindingId, PlayerSelectorContract.CapabilityId,
            PlayerSelectorContract.HandlerName, "observe", Array.Empty<string>(), PlayerSelectorContract.Shape))
        .Bind(ForgePlayer.PlayerHealthContract.DeclareBindings())
        .Bind(ForgePlayer.PlayerEventContract.DeclareBindings())
        .Bind(ForgePlayer.PlayerActionLockContract.DeclareBindings())
        .Bind(ForgePlayer.PlayerActionContract.DeclareBindings())
        .Bind(ForgePlayer.BotOrderContract.DeclareBindings())
        .Bind(ForgePlayer.AgentModifierContract.DeclareBindings())
        .Bind(ForgePlayer.PlayerStateContract.DeclareFactBindings())
        .Bind(ForgePlayer.PlayerLifeContract.DeclareBindings())
        .Bind(ForgePlayer.PlayerStateContract.DeclareValueBindings())
        .Bind(PlayerBackpackContract.Bindings())
        .Bind(ForgePlayer.PlayerCommandContract.DeclareBindings())
        .Bind(ForgePlayer.CombatImpulseContract.DeclareBindings())
        .Bind(ForgePlayer.PlayerStaminaContract.DeclareBindings())
        .Bind(ForgePlayer.PlayerMovementStateContract.DeclareBindings())
        .Bind(ForgePlayer.PlayerAimContract.DeclareBindings())
        .Bind(ForgePlayer.PlayerTextLanguageContract.DeclareBindings())
        .Bind(ForgePlayer.PresentationActionContract.DeclareBindings());
}

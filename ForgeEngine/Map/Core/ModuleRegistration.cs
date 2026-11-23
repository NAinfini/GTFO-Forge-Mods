using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The one Map registration, used by the native session and the release exporter.</summary>
internal static partial class ModuleDefinition
{
    public const string ProviderId = "forge.module.gtfo.map";
    private static readonly ProviderDeclaration Declaration = Declare();

    public static ProviderDeclaration Describe() => Declaration;

    public static RuntimeModule Create(IReadOnlyDictionary<string, CommandHandler> handlers, IReadOnlyDictionary<string, EvaluatorHandler> evaluators)
        => Declaration.Module(RuntimeApi.Fingerprint, handlers, evaluators: evaluators);

    private static ProviderDeclaration Declare() => new ProviderDeclaration(ProviderId, EnginePackage.Version, EnginePackage.Name)
        .DeclareBlocks()
        .Bind(ZoneSelectorContract.DeclareBindings())
        .Bind(MapObjectSelectorContract.DeclareBindings())
        .Bind(MapObjectContract.DeclareBindings())
        .Bind(LevelObjectContract.DeclareBindings())
        .Bind(GeneratorContract.DeclareBindings())
        .Bind(TerminalObjectContract.DeclareBindings())
        .Bind(ObjectiveActionContract.DeclareBindings())
        .Bind(DoorTerminalActionContract.DeclareBindings())
        .Bind(EnvironmentContract.DeclareBindings())
        .Bind(EnemySpawnContract.Binding())
        .Bind(SpacePointContract.Bindings())
        .Bind(WorldLabelContract.Binding())
        .Bind(ModelTextContract.DeclareBindings())
        .Bind(TextStyleContract.DeclareBindings())
        .Bind(PlacedModelTransformContract.DeclareBindings())
        .Bind(AlarmWaveContract.DeclareBindings())
        .Bind(LevelObjectiveValueContract.DeclareBindings())
        .Bind(DoorQueryContract.DeclareBindings())
        .Bind(LevelEventContract.DeclareBindings())
        .Bind(TriggerZoneContract.DeclareBindings())
        .Bind(InteractionPointContract.DeclareBindings())
        .Bind(WorldEventContract.DeclareBindings())
        .Bind(ObjectiveEventContract.DeclareBindings())
        .Bind(ObjectivePresentationContract.DeclareBindings())
        .Bind(InteractionTextContract.DeclareBindings())
        .Bind(TerminalLogContract.DeclareBindings())
        .Bind(TerminalContentContract.DeclareBindings())
        .Bind(MapStateContract.DeclareBindings())
        .Bind(SessionActionContract.DeclareBindings())
        .Bind(ObjectiveTriggerContract.DeclareBindings())
        .Bind(ObjectiveRoundContract.DeclareBindings())
        .Bind(PowerStateContract.DeclareBindings())
        .Bind(ItemSpawnContract.DeclareBindings())
        .Bind(ItemRemoveContract.DeclareBindings())
        .Bind(GeneratorResetContract.DeclareBindings())
        .Bind(EncounterStateContract.DeclareBindings())
        .Bind(FogRepellerContract.DeclareBindings())
        .Bind(EntityDimensionContract.DeclareBindings())
        .Bind(ItemTypeContract.DeclareBindings())
        .Bind(DimensionEnvironmentContract.DeclareBindings())
        .Bind(FailRuleContract.DeclareBindings())
        .Bind(ItemSlotContract.DeclareBindings())
        .Bind(MapWorldTriggerContract.DeclareBindings())
        .Bind(SuccessScreenContract.DeclareBindings())
        .Bind(DoorActionContract.DeclareBindings())
        .Bind(ExpeditionContract.DeclareBindings())
        .Bind(FoamingContract.DeclareBindings())
        .Bind(DoorTerminalEventContract.DeclareBindings());
}

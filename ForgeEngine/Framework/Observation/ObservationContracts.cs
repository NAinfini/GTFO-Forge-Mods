using System;
using System.Collections.Generic;

namespace ForgeEngine.Framework;

/// <summary>
/// Kernel-owned read-only Data operators: the entity values already published through RuntimeEntitySnapshot, plus
/// the one row that reads the kernel's own effect table. Domain packages own the observation that fills the
/// snapshot; this provider owns the reusable reads, and no package declares either row on its behalf.
/// </summary>
internal static class ObservationContracts
{
    internal const string ProviderId = "forge.contract.observation";

    public const string EntityPositionCapability = Blocks.QueryEntityPosition.Id;
    public const string EntityPositionBinding = ProviderId + ".binding.entity_position";
    public const string EntityPositionHandler = "runtime.query.entity_position";

    public const string EntityEyePositionCapability = Blocks.QueryEntityEyePosition.Id;
    public const string EntityEyePositionBinding = ProviderId + ".binding.entity_eye_position";
    public const string EntityEyePositionHandler = "runtime.query.entity_eye_position";

    public const string EntityHealthCapability = Blocks.QueryEntityHealth.Id;
    public const string EntityHealthBinding = ProviderId + ".binding.entity_health";
    public const string EntityHealthHandler = "runtime.query.entity_health";

    public const string EntityLifeStateCapability = Blocks.QueryEntityLifeState.Id;
    public const string EntityLifeStateBinding = ProviderId + ".binding.entity_life_state";
    public const string EntityLifeStateHandler = "runtime.query.entity_life_state";

    public const string EntityOwnerCapability = Blocks.QueryEntityOwner.Id;
    public const string EntityOwnerBinding = ProviderId + ".binding.entity_owner";
    public const string EntityOwnerHandler = "runtime.query.entity_owner";

    /// <summary>`forge.query.effect.active`: whether an effect handle the plan already holds is still running. Its
    /// input is an effect handle of the lifetime the row's port declares, and its answer comes from the kernel's own
    /// effect table — the one place a kernel-managed effect exists — so nothing here observes a game member.</summary>
    public const string EffectActiveCapability = Blocks.QueryEffectActive.Id;
    public const string EffectActiveBinding = ProviderId + ".binding.effect_active";
    public const string EffectActiveHandler = "runtime.query.effect_active";

    /// <summary>The handle kind and lifetime the row's own `effect` port declares, spelled here because a host
    /// answers the read without receiving the port: the port's contract and this check are one statement.</summary>
    public const string EffectHandleKind = "effect";
    public const string EffectHandleLifetime = "entity_life";

    public const string PositionUnavailableCode = "position-unavailable";
    public const string EyePositionUnavailableCode = "eye-position-unavailable";
    public const string HealthUnavailableCode = "health-unavailable";
    public const string HealthKindUnsupportedCode = "health-kind-unsupported";
    public const string LifeStateKindUnsupportedCode = "life-state-kind-unsupported";

    public static readonly HandlerShape EntityPositionShape =
        new HandlerShape().Inputs("entity").Outputs("position");
    public static readonly HandlerShape EntityHealthShape =
        new HandlerShape().Inputs("entity").Outputs("value", "maximum", "fraction");
    public static readonly HandlerShape EntityLifeStateShape =
        new HandlerShape().Inputs("entity").Outputs("state");
    public static readonly HandlerShape EntityOwnerShape =
        new HandlerShape().Inputs("entity").Outputs("owner");
    public static readonly HandlerShape EffectActiveShape =
        new HandlerShape().Inputs("effect").Outputs("active");
    private static readonly HandlerSlot EffectSlot = EffectActiveShape.InputSlot("effect");

    private static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators { get; } =
        new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [EntityPositionHandler] = EntityPosition,
            [EntityEyePositionHandler] = EntityEyePosition,
            [EntityHealthHandler] = EntityHealth,
            [EntityLifeStateHandler] = EntityLifeState,
            [EntityOwnerHandler] = EntityOwner,
            [EffectActiveHandler] = EffectActive
        };

    public static RuntimeModule Module() => PrimitiveContracts.Declaration(ProviderId)
        .Bind(
            new ProviderBinding(EntityPositionBinding, EntityPositionCapability, EntityPositionHandler, "observe", Array.Empty<string>(), EntityPositionShape),
            new ProviderBinding(EntityEyePositionBinding, EntityEyePositionCapability, EntityEyePositionHandler, "observe", Array.Empty<string>(), EntityPositionShape),
            new ProviderBinding(EntityHealthBinding, EntityHealthCapability, EntityHealthHandler, "observe", Array.Empty<string>(), EntityHealthShape),
            new ProviderBinding(EntityLifeStateBinding, EntityLifeStateCapability, EntityLifeStateHandler, "observe", Array.Empty<string>(), EntityLifeStateShape),
            new ProviderBinding(EntityOwnerBinding, EntityOwnerCapability, EntityOwnerHandler, "observe", Array.Empty<string>(), EntityOwnerShape),
            new ProviderBinding(EffectActiveBinding, EffectActiveCapability, EffectActiveHandler, "observe", Array.Empty<string>(), EffectActiveShape)).Module(RuntimeApi.Fingerprint, evaluators: Evaluators);

    public static void EntityPosition(EvaluationContext context, EvaluationOutputs outputs)
    {
        var snapshot = Snapshot(context, out _);
        if (snapshot.Position.Count != 3)
            throw new RuntimeContractException(PositionUnavailableCode,
                "This provider publishes no position for the entity.");
        outputs.Vector(0, snapshot.Position[0], snapshot.Position[1], snapshot.Position[2]);
    }

    /// <summary>Where the entity's eyes are (the game's `Agent.EyePosition`). A kind whose observer publishes no
    /// eyes is refused by name, never answered with its feet or origin.</summary>
    public static void EntityEyePosition(EvaluationContext context, EvaluationOutputs outputs)
    {
        var eye = Snapshot(context, out _).EyePosition
            ?? throw new RuntimeContractException(EyePositionUnavailableCode,
                "This provider publishes no eye position for the entity.");
        outputs.Vector(0, eye[0], eye[1], eye[2]);
    }

    public static void EntityHealth(EvaluationContext context, EvaluationOutputs outputs)
    {
        var snapshot = Snapshot(context, out _);
        if (snapshot.Kind != EntityKinds.Player && snapshot.Kind != EntityKinds.Enemy)
            throw new RuntimeContractException(HealthKindUnsupportedCode,
                "Health is currently defined only for player and enemy entities.");
        if (snapshot.Health is not { } current || snapshot.HealthMaximum is not { } maximum)
            throw new RuntimeContractException(HealthUnavailableCode,
                "This provider publishes no health for the entity.");
        outputs.Number(0, current);
        outputs.Number(1, maximum);
        outputs.Number(2, maximum > 0d ? current / maximum : 0d);
    }

    public static void EntityLifeState(EvaluationContext context, EvaluationOutputs outputs)
    {
        var snapshot = Snapshot(context, out _);
        if (snapshot.Kind != EntityKinds.Player && snapshot.Kind != EntityKinds.Enemy)
            throw new RuntimeContractException(LifeStateKindUnsupportedCode,
                "Life state is currently defined only for player and enemy entities.");
        var state = snapshot.LifeState switch
        {
            "alive" => 0,
            "downed" => 1,
            "dead" => 2,
            _ => throw new RuntimeContractException("entity-life-state", "Unknown life state.")
        };
        outputs.Enum(0, state);
    }

    /// <summary>Reads the explicit parent/owner relation a domain observer published. Parent is the shared
    /// observation field for ownership/lifetime containment; absence is a real nullable answer, never a guessed
    /// player from faction, tags or an entity id.</summary>
    public static void EntityOwner(EvaluationContext context, EvaluationOutputs outputs)
    {
        var snapshot = Snapshot(context, out _);
        outputs.Entity(0, snapshot.Parent);
    }

    /// <summary>
    /// Whether the effect handle the step holds is still running. The answer is the kernel's own record of the
    /// instance the handle names, so the row observes nothing about the game; an ended instance answers `false`
    /// rather than being refused, because "it is not active any more" is precisely what the row asks.
    /// </summary>
    public static void EffectActive(EvaluationContext context, EvaluationOutputs outputs)
        => outputs.Boolean(0, context.Query.EffectActive(
            context.TryRuntimeHandle(EffectSlot, out var effect) ? effect : throw new RuntimeContractException("missing-field", "effect"),
            EffectHandleKind, EffectHandleLifetime));

    private static RuntimeEntitySnapshot Snapshot(EvaluationContext context, out EntityReference reference)
    {
        reference = RuntimeJson.Entity(context.RequiredInput("entity"));
        if (!context.Query.TrySnapshot(reference, out var snapshot, out var code))
            throw new RuntimeContractException(code, "Entity data read failed: " + code);
        return snapshot!;
    }
}

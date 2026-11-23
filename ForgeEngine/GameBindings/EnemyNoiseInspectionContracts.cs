using System.Collections.Generic;
using System.Reflection;

namespace ForgeEngine.GameBindings;

/// <summary>The enemy provider's reviewed managed callback and the two native postfixes that feed it.
/// The consumer still verifies every signature and live Harmony registration before observing the callback.</summary>
public sealed record EnemyNoiseTraceSource(MethodInfo ManagedCallback, IReadOnlyList<EnemyNoiseHookSource> NativeHooks);

public sealed record EnemyNoiseHookSource(MethodInfo NativeTarget, MethodInfo ManagedPostfix, string HarmonyOwner);

/// <summary>A resolved source, or the provider's explicit reason that its own inspection source is unavailable.</summary>
public readonly record struct EnemyNoiseInspectionResult(EnemyNoiseTraceSource? Source, string? Reason)
{
    public bool Resolved => Source != null && Reason == null;
    public static EnemyNoiseInspectionResult ResolvedSource(EnemyNoiseTraceSource source) => new(source, null);
    public static EnemyNoiseInspectionResult Unresolved(string reason) => new(null, reason);
}

/// <summary>The enemy provider's noise trace source, published on its registration for the authoring observer.</summary>
public delegate EnemyNoiseInspectionResult EnemyNoiseTraceResolver();

/// <summary>Refusal codes shared by the provider and the authoring observer. The provider publishes its own callback and Harmony owner through the source record.</summary>
public static class EnemyNoiseInspectionContracts
{
    public const string RuntimeUnavailableCode = "enemy-noise-inspection-unresolved-runtime";
    public const string MissingProviderCode = "enemy-noise-inspection-unresolved-provider";
    public const string InvalidSourceCode = "enemy-noise-inspection-unresolved-source";
}

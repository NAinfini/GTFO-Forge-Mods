using System;
using System.Collections.Generic;

namespace ForgeMap.Native;

/// <summary>The environment features of the declared hooks, as rows of <see cref="MapHookFeature"/>.</summary>
internal static class EnvironmentHookFeatures
{
    internal static readonly IReadOnlyList<MapHookFeature> All = Array.AsReadOnly(new[]
    {
        // The six light bodies are one observation: a later native SetEnabled on any light subtype gives that light
        // back to the native flow. A hold started without all six could restore over a native decision.
        new MapHookFeature("zone-light-hold",
            new[]
            {
                typeof(LightBaseEnabledReadback), typeof(PointLightEnabledReadback), typeof(PointLightEnabledReasonReadback),
                typeof(SpotLightEnabledReadback), typeof(SpotLightEnabledReasonReadback), typeof(AmbientLightEnabledCommonReadback)
            },
            "forge.action.map.device_disable no longer holds environment lights (device-category-unavailable)"),
        // The guard only keeps a repeated exterior close from aborting player spawn in a scene with no exterior camera.
        new MapHookFeature("exterior-close-guard", new[] { typeof(ExteriorSceneGuard) },
            "the repeated exterior close is no longer absorbed"),
        // A fog transition's replicated start time is the sender's expedition clock; a client behind that clock waits
        // for its own clock to reach it instead of blending now.
        new MapHookFeature("environment-state-clock", new[] { typeof(EnvironmentStateClock) },
            "a client no longer retimes a replicated fog start to its own expedition clock (a fog transition can wait for the host's clock)")
    });

    /// <summary>The features a declaration pass took down: those with at least one refused target.</summary>
    internal static IReadOnlyList<MapHookFeature> Unavailable(IReadOnlyDictionary<Type, string> refused)
        => ForgeEngine.Framework.HookFeature.Withdrawn(All, refused);
}

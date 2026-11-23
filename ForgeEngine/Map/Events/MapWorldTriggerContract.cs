using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The three level-world trigger rows this provider publishes from the game's own environment entries:
/// `forge.trigger.map.spitter_exploded` (an infection spitter on the level burst, `InfectionSpitter.DoExplode`),
/// `forge.trigger.map.zone_lights_changed` (one zone's lights were switched,
/// `EnvironmentStateManager.AttemptSetExpeditionLightModeInZone`) and `forge.trigger.map.fog_changed` (the level began
/// a fog transition, `EnvironmentStateManager.AttemptStartFogTransition`). None of them has an instance host: a
/// spitter is a level fixture, and the lights and the fog are level state, so every row is level-scoped.
///
/// The capability rows come from the generated block table, so the ports are the frozen contract's: the spitter row carries the `position` it burst at, the light row the `zone` resource and whether the
/// lights went `on`, and the fog row the `fog` preset id the transition goes to and its `duration` in ticks. The fog
/// preset is the same integer `forge.action.presentation.fog` writes, so the write and this trigger name one value.
///
/// All three are observations of a native entry and publish nothing a plan could write, so their bindings carry role
/// `observe`, no shape, and the environment read permission the value rows of the same state already carry.</summary>
internal static class MapWorldTriggerContract
{
    /// <summary>The facts the rows publish, which are also their observe bindings' handler suffixes and the second
    /// part of each event id.</summary>
    public const string SpitterExplodedFact = "spitter_exploded";
    public const string ZoneLightsChangedFact = "zone_lights_changed";
    public const string FogChangedFact = "fog_changed";

    /// <summary>The permission every row reads under: the level environment's own read permission, the one the
    /// environment value row already carries.</summary>
    public const string ReadPermission = EnvironmentContract.EnvironmentReadPermission;

    public static string SpitterExplodedBinding => BindingOf(Blocks.TriggerMapSpitterExploded.Id);
    public static string ZoneLightsChangedBinding => BindingOf(Blocks.TriggerMapZoneLightsChanged.Id);
    public static string FogChangedBinding => BindingOf(Blocks.TriggerMapFogChanged.Id);

    /// <summary>The event id of one fact: the level namespace, the fact, the world epoch and this world's own
    /// sequence of that fact. Two bursts, two switches or two transitions are two facts, so the sequence and not
    /// the reading is what keeps the ids apart.</summary>
    public static string EventId(string fact, long worldEpoch, long sequence)
        => "gtfo.map." + fact + ":" + worldEpoch.ToString(CultureInfo.InvariantCulture) + ":"
            + sequence.ToString(CultureInfo.InvariantCulture);

    private static string BindingOf(string capability)
        => ModuleDefinition.ProviderId + ".binding." + capability["forge.trigger.".Length..];

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.map.spitter_exploded", Blocks.TriggerMapSpitterExploded.Id, "gtfo.map.spitter_exploded", "observe", new[] { ReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.map.zone_lights_changed", Blocks.TriggerMapZoneLightsChanged.Id, "gtfo.map.zone_lights_changed", "observe", new[] { ReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.map.fog_changed", Blocks.TriggerMapFogChanged.Id, "gtfo.map.fog_changed", "observe", new[] { ReadPermission })
    };
}

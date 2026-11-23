using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The trigger bindings this provider implements for map objects, one per catalog row. Every one of
/// them is an observation of native state, so each is registered with role `observe`, declares no handler and
/// no result row, and publishes its subject's state instead of acting on it.</summary>
internal static class MapObjectContract
{
    public const string MapObjectReadPermission = EntityKinds.MapObject + ".read";

    /// <summary>Internal binding ids, by capability: what a native hook names when it reports a fact. The
    /// binding id is the capability's own suffix under the Map provider, so the counterpart of a row is
    /// readable from either side.</summary>
    public static string Binding(string capabilityId)
        => ModuleDefinition.ProviderId + ".binding." + capabilityId["forge.trigger.".Length..];

    internal static readonly string DoorStateFact = "door_state";
    internal static readonly string LockStateFact = "lock_state";
    internal static readonly string TerminalResultFact = "terminal_result";
    internal static readonly string TerminalSessionFact = "terminal_session";

    internal static string BindingFor(string fact) => Binding(Capability(fact));

    internal static string Capability(string fact) => fact switch
    {
        "door_state" => Blocks.TriggerInteractionDoorState.Id,
        "lock_state" => Blocks.TriggerInteractionLockState.Id,
        "terminal_command" => Blocks.TriggerInteractionTerminalCommand.Id,
        "terminal_result" => Blocks.TriggerInteractionTerminalResult.Id,
        "terminal_session" => Blocks.TriggerInteractionTerminalSession.Id,
        _ => throw new RuntimeContractException("map-object-fact", "Unknown map-object fact kind.")
    };

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.door_state", Blocks.TriggerInteractionDoorState.Id, "gtfo.map_object.door_state", "observe", new[] { MapObjectReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.lock_state", Blocks.TriggerInteractionLockState.Id, "gtfo.map_object.lock_state", "observe", new[] { MapObjectReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.terminal_command", Blocks.TriggerInteractionTerminalCommand.Id, "gtfo.map_object.terminal_command", "observe", new[] { MapObjectReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.terminal_result", Blocks.TriggerInteractionTerminalResult.Id, "gtfo.map_object.terminal_result", "observe", new[] { MapObjectReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.terminal_session", Blocks.TriggerInteractionTerminalSession.Id, "gtfo.map_object.terminal_session", "observe", new[] { MapObjectReadPermission })
    };
}

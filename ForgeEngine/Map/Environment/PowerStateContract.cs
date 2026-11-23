using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The one row of the zone power switch: `forge.action.map.power_state`. A level's "power" is the game's own
/// expedition light mode, which the environment state manager holds per zone and replicates through its own state
/// replicator (`EnvironmentStateManager.AttemptSetExpeditionLightModeInZone`); there is no second blackout state in
/// this build — no member of either truth source spells one — so the row's write is that light mode and nothing
/// else. What follows the mode is whatever the game attaches to it: the light objects the zone itself holds
/// (`LG_Zone.m_lightsInZone`), which is the collection every device class of a zone — a door's lights, a sign, an
/// emergency lamp — is a member of.
///
/// It is declared beside, and not inside, `EnvironmentContract`: the presentation lighting row is a level event
/// with a transition and a sequence and writes under `presentation.light`, while this row is the zone's own
/// replicated state and writes under `power.state`. A plan that may dim a room is therefore not thereby allowed
/// to cut its power, which is exactly what two permissions are for.
/// </summary>
internal static class PowerStateContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.power_state";
    public const string HandlerName = "gtfo.map.power_state";

    /// <summary>The permission the row's own recipient contract names (`recipients.requires`), spelled the way the
    /// reviewed graph spells it.</summary>
    public const string PowerPermission = "power.state";

    /// <summary>The row's ports: the zones it acts on and the state it asks for, against the result row. The two
    /// execution ports are not part of a shape, the same way no other action row of this provider declares
    /// them.</summary>
    public static readonly HandlerShape Shape = new HandlerShape().Inputs("zones", "enabled").Outputs("result");
    public static readonly HandlerSlot Zones = Shape.InputSlot("zones");
    public static readonly HandlerSlot Enabled = Shape.InputSlot("enabled");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionMapPowerState.Id, HandlerName, "execute", new[] { PowerPermission }, Shape)
    };
}

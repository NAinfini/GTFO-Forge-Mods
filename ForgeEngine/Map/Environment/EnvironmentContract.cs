using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The ten checklist rows this batch owns: the nine whose native half is the game's own level-event system
/// — the environment actions (`a-lights`, `a-fog`, `a-fog-repeat`, `a-sound`, `a-sound-stop`,
/// `a-navmarker`, `a-anim`), the player line (`a-p-say`) and the read-only environment value row (`v-env`) — and
/// the light-colour row (`a-lights-color`), whose effect the game has no entry for at all. Every one of the nine
/// executes the game's own entry rather than re-implementing its effect: lights, fog, the repeating-fog
/// pair, sound, its paired stop, the nav marker and the animation trigger are all
/// `eWardenObjectiveEventType` members
/// of `WardenObjectiveEventData`, and the host hands one built event to `WorldEventManager.ExecuteEvent` /
/// `AttemptTriggerSustainedEventMaster` / `AttemptClearSustainedEventMaster`; a player line is
/// `PlayerDialogManager.WantToStartDialogForced`. The two value reads are `EnvironmentStateManager.GetCurrentFogID` and
/// `GetLightMode`. The light-colour row is the exception the checklist's own row asked for: it writes the zone's
/// light objects itself, because no event carries a colour or an intensity and no state replicator would carry
/// one.
///
/// The rows themselves come from the generated block table. This file holds what that table does not: the
/// bindings, their permissions, the handler shapes and the member lists the handlers index. The handlers are the
/// native assembly's `EnvironmentActions` and `EnvironmentQuery`, which answer through
/// these same shapes.
/// </summary>
internal static class EnvironmentContract
{
    /// <summary>The permission the catalog's own recipient contract names for the EMP row: a plan that may blank a
    /// zone's devices is not thereby allowed to drive its lights, which is why this is not `presentation.light`.</summary>
    public const string DevicePermission = "device.emp";

    public const string LightingBindingId = ModuleDefinition.ProviderId + ".binding.lighting";
    public const string LightColorBindingId = ModuleDefinition.ProviderId + ".binding.light_color";
    public const string FogBindingId = ModuleDefinition.ProviderId + ".binding.fog";
    public const string FogCycleBindingId = ModuleDefinition.ProviderId + ".binding.fog_cycle";
    public const string NavMarkerBindingId = ModuleDefinition.ProviderId + ".binding.nav_marker";
    public const string AnimationBindingId = ModuleDefinition.ProviderId + ".binding.animation_trigger";
    public const string PlayerVoiceBindingId = ModuleDefinition.ProviderId + ".binding.player_voice";
    public const string EnvironmentStateBindingId = ModuleDefinition.ProviderId + ".binding.environment_state";
    public const string FogAtBindingId = ModuleDefinition.ProviderId + ".binding.environment_fog_at";
    public const string DeviceDisableBindingId = ModuleDefinition.ProviderId + ".binding.device_disable";

    public const string LightingHandler = "gtfo.map.lighting";
    public const string LightColorHandler = "gtfo.map.light_color";
    public const string FogHandler = "gtfo.map.fog";
    public const string FogCycleHandler = "gtfo.map.fog_cycle";
    public const string NavMarkerHandler = "gtfo.map.nav_marker";
    public const string AnimationHandler = "gtfo.map.animation_trigger";
    public const string PlayerVoiceHandler = "gtfo.map.player_voice";
    public const string EnvironmentStateHandler = "gtfo.map.environment_state";
    public const string FogAtHandler = "gtfo.map.environment_fog_at";
    public const string DeviceDisableHandler = "gtfo.map.device_disable";

    /// <summary>Permissions, spelled the way the catalog's own `recipients.requires` spells the entries these
    /// rows do have. A row's binding carries the permission of the effect it asks the game for, so a plan that
    /// may dim the lights is not thereby allowed to change the fog.</summary>
    public const string LightingPermission = "presentation.light";
    public const string FogPermission = "presentation.fog";
    public const string NavMarkerPermission = "presentation.marker";
    public const string AnimationPermission = "presentation.animation";
    public const string PlayerVoicePermission = "presentation.voice";
    public const string EnvironmentReadPermission = "environment.read";

    /// <summary>`mode` members. `toggle` is the game's own `LightsInZoneToggle` and exists only in zone scope:
    /// there is no level-wide flip in the game's event table, and a request for one is refused by name rather
    /// than served as a read-then-write that would not be atomic.</summary>
    public static readonly string[] LightingModes = { "on", "off", "toggle" };

    /// <summary>`category` members, in the order `LG_Light.LightCategory` declares them: the seven kinds of light
    /// the game's own `LightSettings` blocks are written per, so the member an author picks is the category the
    /// game would have picked. The order is the enum's own, which is what lets the index of a member here be the
    /// value the game indexes a light's `m_category` with. A request that names none drives every category.</summary>
    public static readonly string[] LightCategories =
        { "general", "special", "emergency", "independent", "door", "sign", "door_important", "ambient" };

    // One shape per handler, resolved once at registration against the capability its binding implements. Every
    // one names exactly the ports and parameters its row declares, so the shape and the row cannot drift.
    public static readonly HandlerShape LightingShape = new HandlerShape()
        .Inputs("zones", "transition", "position", "count").Outputs("result").Parameters("scope", "mode");
    public static readonly HandlerSlot LightingZones = LightingShape.InputSlot("zones");
    public static readonly HandlerSlot LightingTransition = LightingShape.InputSlot("transition");
    public static readonly HandlerSlot LightingPosition = LightingShape.InputSlot("position");
    public static readonly HandlerSlot LightingCount = LightingShape.InputSlot("count");
    public static readonly HandlerSlot LightingScope = LightingShape.ParameterSlot("scope");
    public static readonly HandlerSlot LightingMode = LightingShape.ParameterSlot("mode");
    public static readonly HandlerShape LightColorShape = new HandlerShape()
        .Inputs("zones", "color", "brightness", "transition").Outputs("result").Parameters("scope", "category");
    public static readonly HandlerSlot LightColorZones = LightColorShape.InputSlot("zones");
    public static readonly HandlerSlot LightColorValue = LightColorShape.InputSlot("color");
    public static readonly HandlerSlot LightColorBrightness = LightColorShape.InputSlot("brightness");
    public static readonly HandlerSlot LightColorTransition = LightColorShape.InputSlot("transition");
    public static readonly HandlerSlot LightColorScope = LightColorShape.ParameterSlot("scope");
    public static readonly HandlerSlot LightColorCategory = LightColorShape.ParameterSlot("category");
    public static readonly HandlerShape FogShape = new HandlerShape()
        .Inputs("zone", "fog", "transition").Outputs("result");
    public static readonly HandlerSlot FogZone = FogShape.InputSlot("zone");
    public static readonly HandlerSlot FogValue = FogShape.InputSlot("fog");
    public static readonly HandlerSlot FogTransition = FogShape.InputSlot("transition");
    public static readonly HandlerShape FogCycleShape = new HandlerShape()
        .Inputs("zone", "fog", "transition", "state_duration", "start_delay", "sound").Outputs("result")
        .Parameters("mode", "slot", "states");
    public static readonly HandlerSlot FogCycleZone = FogCycleShape.InputSlot("zone");
    public static readonly HandlerSlot FogCycleFog = FogCycleShape.InputSlot("fog");
    public static readonly HandlerSlot FogCycleTransition = FogCycleShape.InputSlot("transition");
    public static readonly HandlerSlot FogCycleStateDuration = FogCycleShape.InputSlot("state_duration");
    public static readonly HandlerSlot FogCycleStartDelay = FogCycleShape.InputSlot("start_delay");
    public static readonly HandlerSlot FogCycleSound = FogCycleShape.InputSlot("sound");
    public static readonly HandlerSlot FogCycleMode = FogCycleShape.ParameterSlot("mode");
    public static readonly HandlerSlot FogCycleSlot = FogCycleShape.ParameterSlot("slot");
    public static readonly HandlerSlot FogCycleStates = FogCycleShape.ParameterSlot("states");
    public static readonly HandlerShape NavMarkerShape = new HandlerShape()
        .Inputs("zone", "filter", "enabled", "style", "text", "position", "offset", "viewers", "duration").Outputs("result");
    public static readonly HandlerSlot NavMarkerViewers = NavMarkerShape.InputSlot("viewers");
    public static readonly HandlerSlot NavMarkerDuration = NavMarkerShape.InputSlot("duration");
    public static readonly HandlerSlot NavMarkerZone = NavMarkerShape.InputSlot("zone");
    public static readonly HandlerSlot NavMarkerFilter = NavMarkerShape.InputSlot("filter");
    public static readonly HandlerSlot NavMarkerEnabled = NavMarkerShape.InputSlot("enabled");
    public static readonly HandlerShape AnimationShape = new HandlerShape()
        .Inputs("zone", "filter", "enabled").Outputs("result");
    public static readonly HandlerSlot AnimationZone = AnimationShape.InputSlot("zone");
    public static readonly HandlerSlot AnimationFilter = AnimationShape.InputSlot("filter");
    public static readonly HandlerSlot AnimationEnabled = AnimationShape.InputSlot("enabled");
    public static readonly HandlerShape PlayerVoiceShape = new HandlerShape()
        .Inputs("speaker", "voice").Outputs("result");
    public static readonly HandlerSlot Voice = PlayerVoiceShape.InputSlot("voice");
    /// <summary>The environment read's ports: the wired zone, and the four answers the reviewed row declares —
    /// the fog block, whether the zone's lights are on, the fog's live world-space density height derived from the
    /// replicated fog transition and the dimension ground (not the target preset altitude or a Forge-side state
    /// mirror), and how many light objects the zone holds.</summary>
    public static readonly HandlerShape EnvironmentStateShape = new HandlerShape()
        .Inputs("zone").Outputs("fog", "light", "height", "count");
    public static readonly HandlerShape FogAtShape = new HandlerShape().Inputs("position", "dimension").Outputs("in_fog", "fog");
    /// <summary>The EMP row's own ports: the recipient objects it acts on, the optional device class and the
    /// reboot time, against the result row. `operation` is the one structural parameter, exactly as the catalog
    /// declares it.</summary>
    public static readonly HandlerShape DeviceDisableShape = new HandlerShape()
        .Inputs("targets", "device_filter", "duration").Outputs("result").Parameters("operation");
    public static readonly HandlerSlot DeviceTargets = DeviceDisableShape.InputSlot("targets");
    public static readonly HandlerSlot DeviceFilter = DeviceDisableShape.InputSlot("device_filter");
    public static readonly HandlerSlot DeviceDuration = DeviceDisableShape.InputSlot("duration");
    public static readonly HandlerSlot DeviceOperation = DeviceDisableShape.ParameterSlot("operation");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(LightingBindingId, Blocks.ActionPresentationLighting.Id, LightingHandler, "execute", new[] { LightingPermission }, LightingShape),
        new ProviderBinding(LightColorBindingId, Blocks.ActionPresentationLightColor.Id, LightColorHandler, "execute", new[] { LightingPermission }, LightColorShape),
        new ProviderBinding(FogBindingId, Blocks.ActionPresentationFog.Id, FogHandler, "execute", new[] { FogPermission }, FogShape),
        new ProviderBinding(FogCycleBindingId, Blocks.ActionPresentationFogCycle.Id, FogCycleHandler, "execute", new[] { FogPermission }, FogCycleShape),
        new ProviderBinding(NavMarkerBindingId, Blocks.ActionPresentationHudMarker.Id, NavMarkerHandler, "execute", new[] { NavMarkerPermission }, NavMarkerShape),
        new ProviderBinding(AnimationBindingId, Blocks.ActionPresentationAnimationTrigger.Id, AnimationHandler, "execute", new[] { AnimationPermission }, AnimationShape),
        new ProviderBinding(PlayerVoiceBindingId, Blocks.ActionPlayerVoice.Id, PlayerVoiceHandler, "execute", new[] { PlayerVoicePermission }, PlayerVoiceShape),
        new ProviderBinding(EnvironmentStateBindingId, Blocks.QueryEnvironmentState.Id, EnvironmentStateHandler, "evaluate", new[] { EnvironmentReadPermission }, EnvironmentStateShape),
        new ProviderBinding(FogAtBindingId, Blocks.QueryEnvironmentFogAt.Id, FogAtHandler, "evaluate", new[] { EnvironmentReadPermission }, FogAtShape),
        new ProviderBinding(DeviceDisableBindingId, Blocks.ActionMapDeviceDisable.Id, DeviceDisableHandler, "execute", new[] { DevicePermission }, DeviceDisableShape)
    };
}

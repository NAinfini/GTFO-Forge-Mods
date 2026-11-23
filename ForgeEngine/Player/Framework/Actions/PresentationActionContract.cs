using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The two `forge.action.presentation.*` rows of the generic-player batch that have a native entry on
/// build 20403457: the camera shake and the screen liquid.
///
/// Both are `presentation` tier rows: what they write is one machine's own viewport, so a plan that shakes a camera
/// or splashes a viewer's screen is not changing the world any other machine can see. The tier is the runtime's own
/// (`Wire.ExecutionTiers`), and it is what keeps a local visual from being dispatched as a world
/// write.
///
/// The third row the ruling grouped here, the numbered HUD timer, is not declared: the game has exactly one timer
/// widget (`PUI_ObjectiveTimer`, dump.cs 555371) and its value belongs to `WardenObjectiveManager`, which is the
/// objective-timer row this provider already publishes. A second, numbered timer has no native carrier, so the row
/// is reported as needing evidence instead of being built on a widget the mod would have to invent. The report
/// carries the same note.</summary>
internal static class PresentationActionContract
{
    public const string CameraShakeHandlerName = "gtfo.presentation.camera_shake";
    public const string CameraShakeBindingId = ModuleDefinition.ProviderId + ".binding.presentation.camera_shake";

    public const string ScreenLiquidHandlerName = "gtfo.presentation.screen_liquid";
    public const string ScreenLiquidBindingId = ModuleDefinition.ProviderId + ".binding.presentation.screen_liquid";

    /// <summary>The permission a camera shake writes under, in the presentation namespace the environment rows
    /// already use.</summary>
    public const string CameraPermission = "presentation.camera";
    /// <summary>The permission a screen liquid writes under.</summary>
    public const string ScreenPermission = "presentation.screen";

    /// <summary>The sixteen members of the native `ScreenLiquidSettingName` enum (dump.cs 543674-543693), spelled
    /// the way the card layer names them. An index in this array is the member's native value.</summary>
    public static readonly string[] LiquidPresets =
    {
        "enemy_blood_big_blood_bomb", "enemy_blood_small_random_streak", "enemy_blood_squirt", "shooter_goo",
        "spitter_jizz", "elevator_rain", "water_drizzle", "water_drip", "player_blood",
        "disinfection_pack_apply", "disinfection_station_apply", "infection_sweat", "player_blood_small_damage",
        "player_blood_big_damage", "player_blood_downed", "anemone_goo"
    };

    /// <summary>The camera-shake shape: the viewers it reaches, the optional centre the distance falloff is
    /// measured from, and the six structural parameters of the shake itself. `direction` is a parameter and not an
    /// input because a shake's direction is a fixed authored vector in the game's own API
    /// (`FPSCamera.Shake(duration, amplitude, frequency, worldDirection)`, dump.cs 530638), not a per-recipient
    /// computed value.</summary>
    public static readonly HandlerShape CameraShakeShape = new HandlerShape()
        .Inputs("viewers", "center").Outputs("result")
        .Parameters("amplitude", "frequency", "duration", "inner_radius", "radius", "direction");

    public static readonly HandlerSlot CameraViewersSlot = CameraShakeShape.InputSlot("viewers");
    public static readonly HandlerSlot CameraCenterSlot = CameraShakeShape.InputSlot("center");
    public static readonly HandlerSlot CameraAmplitudeSlot = CameraShakeShape.ParameterSlot("amplitude");
    public static readonly HandlerSlot CameraFrequencySlot = CameraShakeShape.ParameterSlot("frequency");
    public static readonly HandlerSlot CameraDurationSlot = CameraShakeShape.ParameterSlot("duration");
    public static readonly HandlerSlot CameraInnerRadiusSlot = CameraShakeShape.ParameterSlot("inner_radius");
    public static readonly HandlerSlot CameraRadiusSlot = CameraShakeShape.ParameterSlot("radius");
    public static readonly HandlerSlot CameraDirectionSlot = CameraShakeShape.ParameterSlot("direction");

    /// <summary>The screen-liquid shape: the viewers whose own viewport is splashed, and the preset's name. The
    /// native entry takes no range, no opacity and no fade (`ScreenLiquidManager.DirectApply(setting,
    /// screenPosition, direction)`, dump.cs 588846), so the draft's `range` and `fade_out` are not declared: a port no native call
    /// can honour is not written down as if it could.</summary>
    public static readonly HandlerShape ScreenLiquidShape = new HandlerShape()
        .Inputs("viewers").Outputs("result").Parameters("preset");

    public static readonly HandlerSlot LiquidViewersSlot = ScreenLiquidShape.InputSlot("viewers");
    public static readonly HandlerSlot LiquidPresetSlot = ScreenLiquidShape.ParameterSlot("preset");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(CameraShakeBindingId, Blocks.ActionPresentationCameraShake.Id, CameraShakeHandlerName, "execute", new[] { CameraPermission }, CameraShakeShape),
        new ProviderBinding(ScreenLiquidBindingId, Blocks.ActionPresentationScreenLiquid.Id, ScreenLiquidHandlerName, "execute", new[] { ScreenPermission }, ScreenLiquidShape)
    };
}

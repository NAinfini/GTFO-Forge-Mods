using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEngine.Presentation;

/// <summary>
/// The cross-domain presentation provider (`forge.module.presentation`): the one implementation of the presentation
/// actions every domain shares (contract §3.1). It is part of the host package, like the trigger provider, so it has
/// no package, cfg or plugin of its own; the host registers it on the kernel's built-in path before any domain
/// package loads.
///
/// Each row is a `presentation` step: the host decides which step runs and for whom, and each addressed client runs
/// the body on its own machine. Nothing here commits world state, so every row answers the tier's own
/// `partial + none`. The rows are the generated block table's (<see cref="Blocks"/>).
///
/// This file is game-free: the release exporter source-links it to export the same rows the game registers, and
/// only the bodies live in the game-bound half (`Native/Presentation`).
/// </summary>
internal static class PresentationContract
{
    public const string ProviderId = "forge.module.presentation";
    public const string Version = "1.0.0"; // package-version: NAinfini-ForgeEngine

    /// <summary>`a-fx`: one member of `EffectMembers` played at the position (and direction, end and size)
    /// a plan names, on each addressed client. It never damages anything.</summary>
    public const string EffectCapability = Blocks.ActionPresentationEffectPlay.Id;
    /// <summary>One temporary point light borrowed from the game's own effect-light pool
    /// (`FX_Manager.TryAllocateFXLight`) on each addressed client and given back when its duration ends.</summary>
    public const string PointLightCapability = Blocks.ActionPresentationPointLight.Id;

    /// <summary>A line of text, literal or a Text-block reference, on each addressed client's warden-intel feed.</summary>
    public const string HudMessageCapability = Blocks.ActionPresentationHudMessage.Id;
    /// <summary>One numeric readout on each addressed client's screen: beside the health bar, at the top centre or
    /// under a teammate's name marker.</summary>
    public const string HudValueCapability = Blocks.ActionPresentationHudValue.Id;
    /// <summary>One sound on each addressed client: a game sound event (`sound`) or a custom sound the author ships
    /// (`clip`, played by ForgeAudio), placed globally, at a world point, or on a followed object.</summary>
    public const string AudioCapability = Blocks.ActionPresentationAudioPlay.Id;
    /// <summary>Stops one managed playback by its handle, or a game loop by the stop event paired with it.</summary>
    public const string AudioStopCapability = Blocks.ActionPresentationAudioStop.Id;

    public const string EffectBindingId = ProviderId + ".binding.effect_play";
    public const string PointLightBindingId = ProviderId + ".binding.point_light";
    public const string HudMessageBindingId = ProviderId + ".binding.hud_message";
    public const string HudValueBindingId = ProviderId + ".binding.hud_value";
    public const string AudioBindingId = ProviderId + ".binding.audio_play";
    public const string AudioStopBindingId = ProviderId + ".binding.audio_stop";

    public const string EffectHandler = "presentation.effect_play";
    public const string PointLightHandler = "presentation.point_light";
    public const string HudMessageHandler = "presentation.hud_message";
    public const string HudValueHandler = "presentation.hud_value";
    public const string AudioHandler = "presentation.audio_play";
    public const string AudioStopHandler = "presentation.audio_stop";

    /// <summary>The runtime resource kind the `clip` port names. This provider owns it: a custom sound is a plain
    /// Ogg Vorbis file the author's package ships, and every domain plays it through the same row.</summary>
    public const string AudioResourceKind = "audio";

    /// <summary>The permission both rows draw under, spelled as the catalog's recipient contract spells it.</summary>
    public const string EffectPermission = "presentation.effect";
    /// <summary>The permission the two HUD rows draw under: they write on a player's own screen.</summary>
    public const string HudPermission = "presentation.hud";

    /// <summary>`placement` members of the value row: the native timer at the top of the screen, the shield readout
    /// beside health, the extra information line above a teammate, the interaction message line, and `screen`, where
    /// the author says where it stands.</summary>
    public static readonly string[] HudPlacements = { "status_bar", "teammate_overhead", "top_center", "interaction", "screen" };
    /// <summary>`anchor` members of the `screen` placement, in the order of the library's nine anchors.</summary>
    public static readonly string[] HudAnchors = { "top_left", "top", "top_right", "left", "middle", "right", "bottom_left", "bottom", "bottom_right" };
    /// <summary>`form` members: `bar` writes the game's own bar and no text (only the status bar has a sprite to
    /// write); the others render text: the value as a number, number-of-maximum, percentage or, for `time`, a clock of seconds (`MM:SS`).</summary>
    public static readonly string[] HudForms = { "number", "number_of_max", "percent", "time", "bar", "text", "segments" };
    /// <summary>`audience` members: `team` draws for every addressed client, `self` only on the machine of the one
    /// player the value belongs to.</summary>
    public static readonly string[] HudAudiences = { "team", "self" };
    /// <summary>The permission the two sound rows draw under.</summary>
    public const string AudioPermission = "presentation.audio";

    /// <summary>The effect row's ports: the audience it is presented to, the position the effect is played at, the
    /// direction it is oriented by, the far end of a line member and a Forge member's size, against the result row.
    /// `preset` is the one structural parameter. The body keeps one held copy per play (EffectStep), so the row's
    /// commands carry the host's play.</summary>
    public static readonly HandlerShape EffectShape = new HandlerShape()
        .Inputs("viewers", "position", "direction", "end", "size").Outputs("result").Parameters("preset").KeysByPlay();
    public static readonly HandlerSlot EffectViewers = EffectShape.InputSlot("viewers");
    public static readonly HandlerSlot EffectPosition = EffectShape.InputSlot("position");
    public static readonly HandlerSlot EffectDirection = EffectShape.InputSlot("direction");
    public static readonly HandlerSlot EffectEnd = EffectShape.InputSlot("end");
    public static readonly HandlerSlot EffectSize = EffectShape.InputSlot("size");
    public static readonly HandlerSlot EffectPreset = EffectShape.ParameterSlot("preset");

    /// <summary>The point-light row's ports: the audience, the world point, the 0-1 RGB colour, the peak intensity,
    /// the reach in metres and the lifetime in ticks, against the result row. It has no structural parameter.</summary>
    public static readonly HandlerShape PointLightShape = new HandlerShape()
        .Inputs("viewers", "position", "color", "intensity", "range", "duration").Outputs("result");
    public static readonly HandlerSlot PointLightViewers = PointLightShape.InputSlot("viewers");
    public static readonly HandlerSlot PointLightPosition = PointLightShape.InputSlot("position");
    public static readonly HandlerSlot PointLightColor = PointLightShape.InputSlot("color");
    public static readonly HandlerSlot PointLightIntensity = PointLightShape.InputSlot("intensity");
    public static readonly HandlerSlot PointLightRange = PointLightShape.InputSlot("range");
    public static readonly HandlerSlot PointLightDuration = PointLightShape.InputSlot("duration");

    /// <summary>The message row's ports: the audience and the two entries its text comes from — the literal a plan
    /// computed and the stored Text block its id entry names. The compiler refuses a node that fills both and one
    /// that fills neither, so what reaches the handler is exactly one of them. The two optional structural parameters
    /// style the whole line: a `#rrggbb` colour and a size relative to the game's own.</summary>
    public static readonly HandlerShape HudMessageShape = new HandlerShape()
        .Inputs("viewers", "text", "text_id").Outputs("result").Parameters("color", "size");
    public static readonly HandlerSlot HudMessageViewers = HudMessageShape.InputSlot("viewers");
    public static readonly HandlerSlot HudMessageText = HudMessageShape.InputSlot("text");
    public static readonly HandlerSlot HudMessageTextId = HudMessageShape.InputSlot("text_id");
    public static readonly HandlerSlot HudMessageColor = HudMessageShape.ParameterSlot("color");
    public static readonly HandlerSlot HudMessageSize = HudMessageShape.ParameterSlot("size");
    /// <summary>The play-sound row's ports: the audience, the game sound event id, the free-text subtitle, the level
    /// object the event is placed on, the world point, the followed object and the custom sound, against the result
    /// row and the playback handle a later stop names. `sound` and `clip` are exclusive.</summary>
    public static readonly HandlerShape AudioShape = new HandlerShape()
        .Inputs("viewers", "sound", "subtitle", "filter", "position", "follow", "clip").Outputs("result", "sound");
    public static readonly HandlerSlot AudioViewers = AudioShape.InputSlot("viewers");
    public static readonly HandlerSlot AudioSound = AudioShape.InputSlot("sound");
    public static readonly HandlerSlot AudioSubtitle = AudioShape.InputSlot("subtitle");
    public static readonly HandlerSlot AudioFilter = AudioShape.InputSlot("filter");
    public static readonly HandlerSlot AudioPosition = AudioShape.InputSlot("position");
    public static readonly HandlerSlot AudioFollow = AudioShape.InputSlot("follow");
    public static readonly HandlerSlot AudioClip = AudioShape.InputSlot("clip");

    /// <summary>The value row's ports: the recipients, the value with its optional maximum and label, the
    /// visibility flag, the four structural parameters and the settings of the `screen` placement.</summary>
    public static readonly HandlerShape HudValueShape = new HandlerShape()
        .Inputs("viewers", "value", "maximum", "label", "visible", "text").Outputs("result")
        .Parameters("placement", "form", "audience", "color", "element", "anchor", "x", "y", "width", "height", "text_px", "low_color", "low_below", "rate");
    public static readonly HandlerSlot HudValueViewers = HudValueShape.InputSlot("viewers");
    public static readonly HandlerSlot HudValueValue = HudValueShape.InputSlot("value");
    public static readonly HandlerSlot HudValueMaximum = HudValueShape.InputSlot("maximum");
    public static readonly HandlerSlot HudValueLabel = HudValueShape.InputSlot("label");
    public static readonly HandlerSlot HudValueVisible = HudValueShape.InputSlot("visible");
    public static readonly HandlerSlot HudValueText = HudValueShape.InputSlot("text");
    public static readonly HandlerSlot HudValuePlacement = HudValueShape.ParameterSlot("placement");
    public static readonly HandlerSlot HudValueForm = HudValueShape.ParameterSlot("form");
    public static readonly HandlerSlot HudValueAudience = HudValueShape.ParameterSlot("audience");
    public static readonly HandlerSlot HudValueColor = HudValueShape.ParameterSlot("color");
    public static readonly HandlerSlot HudValueElement = HudValueShape.ParameterSlot("element");
    public static readonly HandlerSlot HudValueAnchor = HudValueShape.ParameterSlot("anchor");
    public static readonly HandlerSlot HudValueX = HudValueShape.ParameterSlot("x");
    public static readonly HandlerSlot HudValueY = HudValueShape.ParameterSlot("y");
    public static readonly HandlerSlot HudValueWidth = HudValueShape.ParameterSlot("width");
    public static readonly HandlerSlot HudValueHeight = HudValueShape.ParameterSlot("height");
    public static readonly HandlerSlot HudValueTextPx = HudValueShape.ParameterSlot("text_px");
    public static readonly HandlerSlot HudValueLowColor = HudValueShape.ParameterSlot("low_color");
    public static readonly HandlerSlot HudValueLowBelow = HudValueShape.ParameterSlot("low_below");
    public static readonly HandlerSlot HudValueRate = HudValueShape.ParameterSlot("rate");
    /// <summary>The stop-sound row's ports: the audience, the game stop event id, the level object or door it is
    /// aimed at, and the playback handle, against the result row.</summary>
    public static readonly HandlerShape AudioStopShape = new HandlerShape()
        .Inputs("viewers", "sound", "filter", "instance").Outputs("result");
    public static readonly HandlerSlot AudioStopViewers = AudioStopShape.InputSlot("viewers");
    public static readonly HandlerSlot AudioStopSound = AudioStopShape.InputSlot("sound");
    public static readonly HandlerSlot AudioStopFilter = AudioStopShape.InputSlot("filter");

    private static readonly ProviderDeclaration Declaration = Declare();

    /// <summary>The rows and bindings alone; <see cref="ProviderDeclaration.CommandHandlers"/> names the bodies a
    /// registration must supply.</summary>
    public static ProviderDeclaration Describe() => Declaration;

    /// <summary>The declaration with the bodies the caller supplies (the game-bound host, or the release exporter's
    /// refusing stand-ins) and the session routing of its presentation rows.</summary>
    public static RuntimeModule Module(IReadOnlyDictionary<string, CommandHandler> handlers,
        Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>? sessions = null,
        IReadOnlyDictionary<string, RuntimeResourceProvider>? resources = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var module = Declaration.Module(RuntimeApi.Fingerprint, handlers);
        if (sessions != null)
            module = module with
            {
                PresentationSessions = new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>(StringComparer.Ordinal)
                {
                    [ProviderId] = sessions
                }
            };
        if (resources != null)
            module = module with { ResourceProviders = new Dictionary<string, RuntimeResourceProvider>(resources, StringComparer.Ordinal) };
        return module;
    }

    private static ProviderDeclaration Declare() => new ProviderDeclaration(ProviderId, Version)
        .DeclareBlocks()
        .Bind(new[]
        {
            new ProviderBinding(EffectBindingId, EffectCapability, EffectHandler, "execute", new[] { EffectPermission }, EffectShape),
            new ProviderBinding(PointLightBindingId, PointLightCapability, PointLightHandler, "execute", new[] { EffectPermission }, PointLightShape),
            new ProviderBinding(HudMessageBindingId, HudMessageCapability, HudMessageHandler, "execute", new[] { HudPermission }, HudMessageShape),
            new ProviderBinding(HudValueBindingId, HudValueCapability, HudValueHandler, "execute", new[] { HudPermission }, HudValueShape),
            new ProviderBinding(AudioBindingId, AudioCapability, AudioHandler, "execute", new[] { AudioPermission }, AudioShape),
            new ProviderBinding(AudioStopBindingId, AudioStopCapability, AudioStopHandler, "execute", new[] { AudioPermission }, AudioStopShape)
        });
}

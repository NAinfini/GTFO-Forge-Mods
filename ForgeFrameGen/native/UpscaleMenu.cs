using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using ForgeUI;
using UnityEngine;

namespace ForgeFrameGen;

/// <summary>Every setting of this mod as rows of the game's own settings, after the game's rows of the video page's ADVANCED GRAPHICS
/// window (ForgeUI <c>UIKitMod.AddSettings</c>; copies of the game's own dropdown and on/off rows): a name and a control each, nothing
/// else in the list. What a row does, or why it cannot be used now, is its hint, which the game shows in its own tooltip while the
/// cursor is on the row; what a player must know to choose is in the option names (the render scale of each mode, a word on each model). Every row writes
/// the config file like any setting and applies at once, with no restart (another upscaler restarts the bridge); frame generation also
/// needs the upscaler on. With the upscaler Off every other row is dimmed. A row this PC cannot use is dimmed. The game's RESET TO DEFAULT puts every row back to its
/// default.</summary>
internal static class UpscaleMenu
{
    private const string Name = "Dlss";
    private static readonly DlssMode[] Modes = { DlssMode.DLAA, DlssMode.Quality, DlssMode.Balanced, DlssMode.Performance, DlssMode.UltraPerformance };
    private static readonly UIText NoDlss = UIText.Key("mode.unavailable"), NoGeneration = UIText.Key("generation.unavailable"), DlssOnly = UIText.Key("model.dlssonly"),
        UpscalerText = UIText.Key("upscaler.hint"), UpscalerOff = UIText.Key("upscaler.offhint");
    private static readonly UIText[] ModelHints = Array.ConvertAll(Enum.GetValues<DlssPreset>(), model => UIText.Key("model.hint." + model));

    internal static void Load()
    {
        var rows = new List<SettingsRow>
        {
            Choice("upscaler.title", Plugin.Upscaler, () => DlssBridge.Loaded, UpscalerHint),
            // Every row below acts only through an upscaler: with the upscaler Off each is dimmed and its hint says so.
            new SettingsChoice(UIText.Key("mode.title"), Array.ConvertAll(Modes, mode => UIText.Key("mode." + mode)),
                () => Array.IndexOf(Modes, Plugin.Mode.Value), index => Plugin.Mode.Value = Modes[index], Upscaling(Available), Dlss("mode.hint")),
            // The hint is the picked model's trade-off: the dropdown's own options carry no tooltip, so each also has a word in its name.
            Choice("model.title", Plugin.Preset, Upscaling(DlssRuns),
                Off(() => !Available() ? NoDlss : DlssRuns() ? ModelHints[(int)Plugin.Preset.Value] : DlssOnly)),
            Choice("exposure.title", Plugin.Exposure, Upscaling(Available), Dlss("exposure.hint")),
            // Off or which frame generation, apart from the upscaler; the hint says which one runs. Without the present bridge (this PC can
            // run neither) only Off is left.
            Choice("generation.title", Plugin.FrameGeneration, Upscaling(() => Plugin.FrameGenerationOn || Bridged()), Off(GenerationStatus)),
            // These act through the present bridge, which exists whenever this PC can run frame generation; Reflex and its limit only
            // with DLSS Frame Generation.
            new SettingsToggle(UIText.Key("hud.title"), () => Plugin.HudInGeneratedFrames.Value, on => Plugin.HudInGeneratedFrames.Value = on, Upscaling(Bridged),
                Bridge("hud.hint", "hud.hint.fsr")),
            Choice("reflex.title", Plugin.Reflex, Upscaling(Reflex), Bridge("reflex.hint", "reflex.fsr")),
            new SettingsChoice(UIText.Key("limit.title"), Array.ConvertAll(Plugin.FrameLimits, limit => limit == 0 ? UIText.Key("limit.off") : UIText.Key("limit.fps", limit)),
                () => Array.IndexOf(Plugin.FrameLimits, Plugin.FrameLimit.Value), index => Plugin.FrameLimit.Value = Plugin.FrameLimits[index], Upscaling(Reflex),
                Bridge("limit.hint", "reflex.fsr")),
        };
        UIKit.Mod(Plugin.Id).AddSettings(Name, SettingsBlock.AdvancedGraphics, rows, Reset);
    }

    /// <summary>A dropdown over an enum setting; the options' texts are keyed <c>title's first word.value</c> (model.Auto, reflex.Boost).</summary>
    private static SettingsChoice Choice<T>(string title, ConfigEntry<T> setting, Func<bool> enabled, Func<UIText?> hint) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        var prefix = title[..(title.IndexOf('.') + 1)];
        return new SettingsChoice(UIText.Key(title), Array.ConvertAll(values, value => UIText.Key(prefix + value)),
            () => Array.IndexOf(values, setting.Value), index => setting.Value = values[index], enabled, hint);
    }

    /// <summary>A row's enabled state that also needs the upscaler on.</summary>
    private static Func<bool> Upscaling(Func<bool> enabled) => () => Plugin.UpscalerOn && enabled();

    /// <summary>A row's hint, or that the upscaler is Off.</summary>
    private static Func<UIText?> Off(Func<UIText?> hint) => () => Plugin.UpscalerOn ? hint() : UpscalerOff;

    /// <summary>The hint of a DLSS row: what it does, or why this PC cannot use it.</summary>
    private static Func<UIText?> Dlss(string key)
    {
        var text = UIText.Key(key);
        return Off(() => Available() ? text : NoDlss);
    }

    /// <summary>The hint of a row that acts through the present bridge: what it does with the backend that runs (<paramref name="fsr"/>
    /// with AMD FSR Frame Generation), or why this PC cannot use it.</summary>
    private static Func<UIText?> Bridge(string key, string fsr)
    {
        UIText text = UIText.Key(key), fsrText = UIText.Key(fsr);
        return Off(() => !Bridged() ? NoGeneration : Backend() == 2 ? fsrText : text);
    }

    /// <summary>The game's RESET TO DEFAULT on the video page.</summary>
    private static void Reset()
    {
        foreach (var setting in new ConfigEntryBase[]
                 {
                     Plugin.Upscaler, Plugin.Mode, Plugin.Preset, Plugin.Exposure, Plugin.FrameGeneration, Plugin.HudInGeneratedFrames, Plugin.Reflex,
                     Plugin.FrameLimit,
                 })
            setting.BoxedValue = setting.DefaultValue;
    }

    /// <summary>Development driver: one line per row as the game laid it out (exists, active, shown, extent, in view, text).</summary>
    internal static string Rows() => UIKit.Mod(Plugin.Id).SettingsReadback(Name);

    /// <summary>Development driver: opens the game's menu on the settings page and shows its settings window number <paramref name="window"/>
    /// (the video windows hold these rows), or closes the menu again (a negative number).</summary>
    internal static string Show(int window)
    {
        if (window < 0)
        {
            FocusStateManager.ExitMenu();
            return "closed";
        }
        FocusStateManager.EnterMenu(eCM_MenuPage.CMP_SETTINGS, true);
        if (GuiManager.MainMenuLayer?.GetPage(eCM_MenuPage.CMP_SETTINGS)?.TryCast<CellMenu.CM_PageSettings>() is not { } page) return "no settings page";
        if (window >= page.m_allSettingsWindows.Count) return "windows: " + page.m_allSettingsWindows.Count;
        page.ShowSettingsWindow(page.m_allSettingsWindows[window]);
        return "page=" + page.m_currentSubMenuId + "\n" + Rows();
    }

    /// <summary>Development driver: what picking a row's value does (a mode name, an upscaler name, or "fg:Off" / "fg:Auto" / "fg:DLSS" /
    /// "fg:FSR" for frame generation).</summary>
    internal static string Choose(string row)
    {
        if (row.StartsWith("fg:")) Plugin.FrameGeneration.Value = Enum.Parse<FrameGenerationMethod>(row[3..]);
        else if (Enum.TryParse<UpscalerMethod>(row, out var upscaler)) Plugin.Upscaler.Value = upscaler;
        else Plugin.Mode.Value = (DlssMode)Enum.Parse(typeof(DlssMode), row);
        return "upscaler=" + Plugin.Upscaler.Value + " running=" + DlssBridge.Upscaler().Running + " mode=" + Plugin.Mode.Value + " generation="
               + Plugin.FrameGeneration.Value + " backend=" + DlssBridge.FrameGenerationBackend + " bridge=" + DlssBridge.PresentState + " status="
               + GenerationStatus();
    }

    /// <summary>An upscaler can run here. With the upscaler not Off, the rows start the bridge when the game builds them (as the first frame
    /// of a level does), so a PC without one shows them dimmed before any level was entered; with Off nothing is loaded until one is picked.</summary>
    private static bool Available()
    {
        if (!DlssBridge.Loaded) return false;
        if (DlssBridge.Status == DlssBridge.State.Idle && Plugin.UpscalerOn) DlssBridge.Initialize(Texture2D.whiteTexture);
        return DlssBridge.Status is not (DlssBridge.State.Unavailable or DlssBridge.State.Failed or DlssBridge.State.ForeignLibrary);
    }

    private static bool Bridged() => DlssBridge.Bridged;

    /// <summary>The frame generation that runs now: 0 none, 1 DLSS Frame Generation, 2 AMD FSR Frame Generation.</summary>
    private static int Backend() => DlssBridge.FrameGenerationBackend;

    /// <summary>Reflex and its frame rate limit run with DLSS Frame Generation only.</summary>
    private static bool Reflex() => Bridged() && Backend() == 1;

    /// <summary>DLSS is the upscaler that runs (the model row is DLSS's own).</summary>
    private static bool DlssRuns() => Available() && DlssBridge.Upscaler().Running == UpscalerMethod.DLSS;

    /// <summary>The upscaler row's hint: that the pick (or DLSS under Auto on a PC with an NVIDIA card) cannot run and why, and which one
    /// runs instead; else which one runs, else what Auto does.</summary>
    private static UIText? UpscalerHint()
    {
        if (!Plugin.UpscalerOn) return UpscalerOff;
        if (!Available()) return NoDlss;
        var (running, _, fellBack) = DlssBridge.Upscaler();
        if (running == UpscalerMethod.Auto) return UpscalerText;
        if (!fellBack) return UIText.Key("upscaler.running", Brand(running));
        if (running == UpscalerMethod.DLSS) return UIText.Key("upscaler.fallback", Brand(UpscalerMethod.FSR), Brand(running), Text("fsr.missing"));
        var (missing, major, minor) = DlssBridge.DlssMissing();
        var why = missing == DlssBridge.Missing.Driver && major > 0 ? Text("dlss.missing.DriverVersion", major, minor) : MissingText(missing, false);
        return Plugin.Upscaler.Value == UpscalerMethod.Auto ? UIText.Key("upscaler.auto.fallback", why) : UIText.Key("upscaler.fallback", Brand(UpscalerMethod.DLSS), Brand(running), why);
    }

    /// <summary>Why DLSS (super resolution, or <paramref name="generation"/>) does not run, in the player's words.</summary>
    private static string MissingText(DlssBridge.Missing missing, bool generation) =>
        Text(generation && missing == DlssBridge.Missing.Gpu ? "dlssg.missing.Gpu" : "dlss.missing." + missing);

    private static string Text(string key, params object?[] args) => UIKit.Mod(Plugin.Id).Text(key, args);

    private static string Brand(UpscalerMethod method) => method == UpscalerMethod.FSR ? "AMD FSR™" : "NVIDIA® DLSS";

    private static string GenerationBrand(int backend) => backend == 2 ? "AMD FSR™" : "NVIDIA® DLSS";

    /// <summary>The frame generation row's hint: what the choices do while it is off; that the pick cannot run here and which one runs
    /// instead (with what the pick needs); that it needs an upscaling mode; else which one runs, or is waiting for a game frame.</summary>
    private static UIText GenerationStatus()
    {
        if (!DlssBridge.Bridged || Backend() == 0) return NoGeneration;
        if (!Plugin.FrameGenerationOn) return UIText.Key("generation.hint");
        if (DlssBridge.FrameGenerationFallback() is { } wanted)
        {
            if (wanted == FrameGenerationMethod.FSR) return UIText.Key("generation.fallback.fsr");
            var why = MissingText(DlssBridge.FrameGenerationDlssMissing().Code, true);
            return UIText.Key(Plugin.FrameGeneration.Value == FrameGenerationMethod.Auto ? "generation.auto.fallback" : "generation.fallback.dlss", why);
        }
        return UIText.Key(DlssBridge.Generating ? "generation.running" : "generation.waiting", GenerationBrand(Backend()));
    }
}

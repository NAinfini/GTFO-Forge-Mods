using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeCore;

namespace ForgeFrameGen;

/// <summary>DLSS and FSR super resolution and DLSS and FSR frame generation for GTFO. The plugin ID, display name, native bridge file name and package
/// folder are all read from these constants. Every setting is also a row of the game's settings (<see cref="UpscaleMenu"/>).</summary>
[BepInPlugin(Id, DisplayName, Version)]
[BepInDependency("NAinfini.ForgeCore", ">=1.0.3")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = PackageFiles.PluginId, DisplayName = "Forge FrameGen", Version = "1.0.1"; // package-version: NAinfini-ForgeFrameGen
    internal const string NativeFile = PackageFiles.NativeFile, DlssLibrary = "nvngx_dlss.dll", FsrLoader = "amd_fidelityfx_loader_dx12.dll";
    private const string InSettings = " Also in the game's settings (Video, Advanced Graphics), where a change applies at once.";

    /// <summary>The frame rate limits offered, in frames a second; 0 is no limit.</summary>
    internal static readonly int[] FrameLimits = { 0, 30, 40, 50, 60, 72, 90, 120, 144, 165, 240 };

    internal static ModLog Logger { get; private set; } = null!;
    internal static BepInEx.Logging.ManualLogSource Info { get; private set; } = null!;
    internal static ConfigEntry<UpscalerMethod> Upscaler = null!;
    internal static ConfigEntry<DlssMode> Mode = null!;
    internal static ConfigEntry<DlssPreset> Preset = null!;
    internal static ConfigEntry<DlssExposure> Exposure = null!;
    internal static ConfigEntry<FrameGenerationMethod> FrameGeneration = null!;
    internal static ConfigEntry<bool> HudInGeneratedFrames = null!;
    internal static ConfigEntry<ReflexMode> Reflex = null!;
    internal static ConfigEntry<int> FrameLimit = null!;

    /// <summary>Frame generation is not switched off.</summary>
    internal static bool FrameGenerationOn => FrameGeneration.Value != FrameGenerationMethod.Off;

    /// <summary>The folder this plugin was loaded from: the native bridge and the packaged NVIDIA and AMD libraries sit beside the DLL, and
    /// the bridge loads DLSS and FSR only from here.</summary>
    internal static string Folder { get; private set; } = "";

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Id);
        Info = Log;
        Folder = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "";
        Upscaler = Config.Bind("DLSS", "Upscaler", UpscalerMethod.Auto,
            "Which upscaler runs the mode below. Auto: NVIDIA® DLSS on an NVIDIA RTX card, AMD FSR™ on every other card (FSR 4 on the AMD Radeon "
            + "cards AMD lists for it, RX 7000 and 9000 series; FSR 3.1 elsewhere). DLSS or FSR: that one; when this card cannot run it, Auto is used "
            + "and the settings row says so. Frame generation has its own setting." + InSettings + " This mod is not an NVIDIA or AMD product.");
        Mode = Config.Bind("DLSS", "Mode", DlssMode.DLAA,
            "The upscaling mode. DLAA: anti-aliasing at your screen resolution (FSR's Native AA), in place of the game's own anti-aliasing. Quality, "
            + "Balanced, Performance, UltraPerformance: the world renders at a lower resolution (about 67%, 58%, 50% and 33% of the screen's width "
            + "and height) and the upscaler brings it up to the screen's; the HUD stays at full resolution. Off: no upscaler, and no frame "
            + "generation, which needs one of the other modes." + InSettings
            + " DLSS needs an NVIDIA RTX graphics card: RTX 40 and 50 series supported, RTX 20 and 30 series untested. FSR runs on any DirectX 12 card.");
        Preset = Config.Bind("DLSS", "Model", DlssPreset.Auto,
            "The DLSS model. Auto: model M, and model L in the Ultra Performance mode. K, M, L: that model in every mode. K is the lightest on "
            + "the graphics card. M and L give a sharper, steadier picture with less ghosting and cost more, L the most; they run best on RTX 40 "
            + "and 50 series cards." + InSettings);
        Exposure = Config.Bind("DLSS", "Exposure", DlssExposure.Auto,
            "How the upscaler learns how bright the picture is. Auto: it measures it. Fixed: it is told the picture is already exposed; try it if dark "
            + "scenes ghost or flicker with Auto. By NVIDIA's guide only DLSS model K takes it; M and L measure the picture themselves." + InSettings);
        FrameGeneration = Config.Bind("Frame Generation", "Method", FrameGenerationMethod.Off,
            "A generated frame between every two rendered frames. Off; Auto: NVIDIA® DLSS Frame Generation with NVIDIA® Reflex on an RTX 40 or 50 "
            + "series card with Windows' hardware-accelerated GPU scheduling turned on, AMD FSR™ Frame Generation on every other DirectX 12 card; DLSS "
            + "or FSR: that one, with either upscaler (when this PC cannot run it, Auto is used and the settings row says so). Changes apply at once, "
            + "while the game runs. Runs while the upscaling mode is not Off (DLAA counts), in windowed or borderless mode only, and pauses while the "
            + "map or the menu is open or you use a terminal." + InSettings);
        HudInGeneratedFrames = Config.Bind("Frame Generation", "HudInGeneratedFrames", true,
            "Keeps the HUD and crosshair out of the interpolation: DLSS Frame Generation gets the HUD as its own layer and composites it, AMD FSR "
            + "Frame Generation gets the picture without the HUD. Turn it off if the HUD flickers or doubles while frame generation runs." + InSettings);
        Reflex = Config.Bind("Frame Generation", "Reflex", ReflexMode.On,
            "NVIDIA® Reflex low latency, which runs with DLSS Frame Generation (not with AMD FSR Frame Generation). On, or Boost (On + Boost: keeps the graphics card's clock up for a "
            + "little less delay at more power)." + InSettings);
        FrameLimit = Config.Bind("Frame Generation", "FrameLimit", 0, new ConfigDescription(
            "Reflex frame rate limit, in frames a second; 0 is no limit. Runs with DLSS Frame Generation." + InSettings,
            new AcceptableValueList<int>(FrameLimits)));
        DlssBridge.Load();
        WorldPass.Load();
        UpscaleMenu.Load();
    }

    public override bool Unload() => false;
}

/// <summary>The upscaler the player asks for; the native bridge's Method has the same numbers.</summary>
internal enum UpscalerMethod
{
    Auto,
    DLSS,
    FSR,
}

/// <summary>The frame generation the player asks for; the native bridge's choice is this number less one (Auto 0, DLSS 1, FSR 2).</summary>
internal enum FrameGenerationMethod
{
    Off,
    Auto,
    DLSS,
    FSR,
}

/// <summary>What the player picks; the NGX quality each one asks for is <see cref="DlssBridge.Quality"/> (FSR maps it to its own modes).</summary>
internal enum DlssMode
{
    Off,
    DLAA,
    Quality,
    Balanced,
    Performance,
    UltraPerformance,
}

/// <summary>The DLSS model, lightest first; <see cref="DlssBridge.Preset"/> gives NGX's number.</summary>
internal enum DlssPreset
{
    Auto,
    K,
    M,
    L,
}

internal enum DlssExposure
{
    Auto,
    Fixed,
}

internal enum ReflexMode
{
    On,
    Boost,
}

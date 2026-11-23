using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using ForgeCore;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeFrameGen;

/// <summary>The managed side of the native bridge (native/forge_framegen.cpp): loads it from the plugin folder, starts the upscaler (DLSS or
/// FSR, as <see cref="Plugin.Upscaler"/> and the card allow) on Unity's render thread, hands out per-frame parameter slots and turns the
/// bridge's messages into log lines. The structs here mirror the bridge's InitParams and FrameParams byte for byte; <see cref="AbiVersion"/>
/// must match its kAbiVersion.</summary>
internal static unsafe class DlssBridge
{
    internal const uint AbiVersion = 3;
    internal const int EventInitialize = 1, EventEvaluate = 2, EventShutdown = 3, EventFrameGeneration = 4;
    internal const uint FrameGenerationVersion = 2;
    private const int FrameGenerationSize = 424;

    /// <summary>The bridge's status codes (native Status).</summary>
    internal enum State
    {
        Idle = 0,
        Ready = 1,
        Running = 2,
        Unavailable = 3,
        Failed = 4,
        ForeignLibrary = 5,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct InitParams
    {
        public uint Size, Version;
        public IntPtr AnyTexture, LibraryFolder, DataFolder;
        public int Method;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct FrameParams
    {
        public uint Size, Version;
        public IntPtr Color, Depth, Motion, Output;
        public uint RenderWidth, RenderHeight, OutputWidth, OutputHeight;
        public float JitterX, JitterY, MotionScaleX, MotionScaleY;
        public int Reset, Hdr, DepthInverted, Quality;
        public int Exposure, Preset;
        public float CameraNear, CameraFar, CameraFov;
    }

    /// <summary>One frame's frame generation inputs: native fg::FrameParams (frame_generation.h) byte for byte. Matrices are Unity's
    /// <c>Matrix4x4</c> memory, which is Streamline's row-vector layout.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct FrameGenerationParams
    {
        public uint Size, Version;
        public int Frame, Enabled;
        public IntPtr Depth, Motion, Hudless, HudOnBlack, HudOnWhite;
        public uint RenderWidth, RenderHeight, OutputWidth, OutputHeight;
        public Matrix4x4 ViewToClip, ClipToView, ClipToPrevClip, PrevClipToClip;
        public float JitterX, JitterY, MotionScaleX, MotionScaleY;
        public Vector3 CameraPosition, CameraUp, CameraRight, CameraForward;
        public float CameraNear, CameraFar, CameraFov, CameraAspect;
        public int Reset, DepthInverted, FlipRows, EncodeSrgb;
    }

    /// <summary>The NVSDK_NGX_PerfQuality_Value a mode asks for (MaxPerf 0, Balanced 1, MaxQuality 2, UltraPerformance 3, DLAA 5).</summary>
    internal static int Quality(DlssMode mode) => mode switch
    {
        DlssMode.Quality => 2,
        DlssMode.Balanced => 1,
        DlssMode.Performance => 0,
        DlssMode.UltraPerformance => 3,
        _ => 5,
    };

    /// <summary>The NVSDK_NGX_DLSS_Hint_Render_Preset a model asks for in every mode (K 11, L 12, M 13); 0 leaves the choice per mode to the bridge.</summary>
    internal static int Preset(DlssPreset preset) => preset switch
    {
        DlssPreset.K => 11,
        DlssPreset.M => 13,
        DlssPreset.L => 12,
        _ => 0,
    };

    /// <summary>The upscaler's render size for an output size and mode; DLAA (and a failed query, logged once) is the output size itself.</summary>
    internal static (int Width, int Height) RenderSize(int outputWidth, int outputHeight, DlssMode mode)
    {
        if (mode == DlssMode.DLAA) return (outputWidth, outputHeight);
        uint width = 0, height = 0;
        if (_optimal((uint)outputWidth, (uint)outputHeight, Quality(mode), &width, &height) != 0 && width > 0 && height > 0) return ((int)width, (int)height);
        Plugin.Logger.ErrorOnce("framegen-native-failed", "The upscaler gave no render size for " + mode + " at " + outputWidth + "x" + outputHeight + "; rendering at the screen's resolution (DLAA).");
        return (outputWidth, outputHeight);
    }

    // The render thread reads a slot up to a frame or two after the main thread filled it: a ring of four keeps every slot in flight untouched.
    private const int Slots = 4;
    private static FrameParams* _frames;
    private static FrameGenerationParams* _generationFrames;
    private static int _nextSlot, _nextGenerationSlot;

    private static delegate* unmanaged<int> _status;
    private static delegate* unmanaged<long> _evaluations;
    private static delegate* unmanaged<byte*, int, int> _popMessage;
    private static delegate* unmanaged<uint, uint, int, uint*, uint*, int> _optimal;
    private static delegate* unmanaged<int> _presentState;
    private static delegate* unmanaged<int*, int*, int*, long*, void> _graphicsCounts;
    private static delegate* unmanaged<byte*, int, int*, int> _popBridgeMessage;
    private static delegate* unmanaged<byte*, int, long*, long*, int*, int> _generationState;
    private static delegate* unmanaged<int, int, void> _frameMarker;
    private static delegate* unmanaged<int, void> _setDeveloperLog;
    private static delegate* unmanaged<void> _releaseGeneration;
    private static delegate* unmanaged<byte*, int, int> _generationCounters;
    private static delegate* unmanaged<int, int, void> _setReflex;
    private static delegate* unmanaged<byte*, int, int*, int> _upscaler;
    private static delegate* unmanaged<int> _generationBackend;
    private static delegate* unmanaged<int, void> _setHud;
    private static delegate* unmanaged<int, void> _prefer;
    private static delegate* unmanaged<void> _pluginConnected;
    private static delegate* unmanaged<uint*, uint*, int> _dlssMissing;
    private static delegate* unmanaged<byte*, int, int> _generationDlssMissing;
    internal static IntPtr RenderEvent { get; private set; }

    /// <summary>The bridge's plain render event (<c>GL.IssuePluginEvent</c>; the event id is the Unity frame number) that marks the start of a
    /// frame's render submission on the render thread.</summary>
    internal static IntPtr RenderSubmitStartEvent { get; private set; }

    /// <summary>The bridge is loaded and the packaged DLSS library is present; false means the game renders unchanged.</summary>
    internal static bool Loaded { get; private set; }

    private static bool _initIssued;
    private static IntPtr _init;
    private static readonly byte[] MessageBuffer = new byte[1024];

    /// <summary>The bridge's status; Idle while a <see cref="Restart"/> waits for the running upscaler to stop.</summary>
    internal static State Status => Loaded && !_restarting ? (State)_status() : State.Idle;

    /// <summary>Successful upscaler evaluations on the render thread since start.</summary>
    internal static long Evaluations => Loaded ? _evaluations() : 0;

    internal static void Load()
    {
        var native = Path.Combine(Plugin.Folder, Plugin.NativeFile);
        var missing = Array.Find(new[] { native, Path.Combine(Plugin.Folder, Plugin.DlssLibrary), Path.Combine(Plugin.Folder, Plugin.FsrLoader) }, path => !File.Exists(path));
        if (missing != null)
        {
            Plugin.Logger.ErrorOnce("framegen-library-missing", "The package is incomplete (" + missing + " is missing); the upscaler stays off and the game renders unchanged. Reinstall the package.");
            return;
        }
        try
        {
            var handle = NativeLibrary.Load(native);
            var abi = (delegate* unmanaged<uint>)NativeLibrary.GetExport(handle, "ForgeFrameGen_AbiVersion");
            if (abi() != AbiVersion)
            {
                Plugin.Logger.ErrorOnce("framegen-native-failed", "The native bridge " + native + " is from another build (layout " + abi() + ", plugin expects " + AbiVersion + "); DLSS stays off.");
                return;
            }
            _status = (delegate* unmanaged<int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_Status");
            _popMessage = (delegate* unmanaged<byte*, int, int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_PopMessage");
            _evaluations = (delegate* unmanaged<long>)NativeLibrary.GetExport(handle, "ForgeFrameGen_Evaluations");
            _optimal = (delegate* unmanaged<uint, uint, int, uint*, uint*, int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_OptimalRenderSize");
            _presentState = (delegate* unmanaged<int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_PresentState");
            _graphicsCounts = (delegate* unmanaged<int*, int*, int*, long*, void>)NativeLibrary.GetExport(handle, "ForgeFrameGen_GraphicsCounts");
            _popBridgeMessage = (delegate* unmanaged<byte*, int, int*, int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_PopBridgeMessage");
            _generationState = (delegate* unmanaged<byte*, int, long*, long*, int*, int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_FrameGenerationState");
            _frameMarker = (delegate* unmanaged<int, int, void>)NativeLibrary.GetExport(handle, "ForgeFrameGen_FrameMarker");
            _setDeveloperLog = (delegate* unmanaged<int, void>)NativeLibrary.GetExport(handle, "ForgeFrameGen_SetDeveloperLog");
            _releaseGeneration = (delegate* unmanaged<void>)NativeLibrary.GetExport(handle, "ForgeFrameGen_ReleaseFrameGeneration");
            _generationCounters = (delegate* unmanaged<byte*, int, int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_FrameGenerationCounters");
            _setReflex = (delegate* unmanaged<int, int, void>)NativeLibrary.GetExport(handle, "ForgeFrameGen_SetReflex");
            _upscaler = (delegate* unmanaged<byte*, int, int*, int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_Upscaler");
            _generationBackend = (delegate* unmanaged<int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_FrameGenerationBackend");
            _setHud = (delegate* unmanaged<int, void>)NativeLibrary.GetExport(handle, "ForgeFrameGen_SetHudInGeneratedFrames");
            _prefer = (delegate* unmanaged<int, void>)NativeLibrary.GetExport(handle, "ForgeFrameGen_SetFrameGenerationPreference");
            _pluginConnected = (delegate* unmanaged<void>)NativeLibrary.GetExport(handle, "ForgeFrameGen_PluginConnected");
            _dlssMissing = (delegate* unmanaged<uint*, uint*, int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_DlssMissing");
            _generationDlssMissing = (delegate* unmanaged<byte*, int, int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_DlssFrameGenerationMissing");
            RenderEvent = ((delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(handle, "ForgeFrameGen_RenderEventFunc"))();
            RenderSubmitStartEvent = ((delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(handle, "ForgeFrameGen_RenderSubmitStartFunc"))();
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("framegen-native-failed", "Loading the native bridge " + native + " failed; DLSS stays off: " + error);
            return;
        }
        if (sizeof(FrameGenerationParams) != FrameGenerationSize)
        {
            Plugin.Logger.ErrorOnce("framegen-native-failed", "The frame generation parameter layout is " + sizeof(FrameGenerationParams) + " bytes, not the native " + FrameGenerationSize + "; DLSS stays off.");
            return;
        }
        _frames = (FrameParams*)NativeMemory.AllocZeroed((nuint)(Slots * sizeof(FrameParams)));
        _generationFrames = (FrameGenerationParams*)NativeMemory.AllocZeroed((nuint)(Slots * sizeof(FrameGenerationParams)));
        Loaded = true;
        // Before the game's first present: without this the bridge parks on a plain swap chain (a plugin that never loads gives frame
        // generation no inputs or settings).
        _pluginConnected();
        SendReflex();
        Plugin.Reflex.SettingChanged += (_, _) => SendReflex();
        Plugin.FrameLimit.SettingChanged += (_, _) => SendReflex();
        SendHud();
        Plugin.HudInGeneratedFrames.SettingChanged += (_, _) => SendHud();
        SendPreference();
        Plugin.FrameGeneration.SettingChanged += (_, _) => SendPreference();
        if (_presentState() == 2) FrameMarkers.Load();
        Frame.OnUpdate(Plugin.Id, DrainMessages);
    }

    /// <summary>Reflex as the player set it; the bridge sets it before the next frame's Reflex sleep.</summary>
    private static void SendReflex() => _setReflex(Plugin.Reflex.Value == ReflexMode.Boost ? 1 : 0, Plugin.FrameLimit.Value);

    /// <summary>"HUD in generated frames" for AMD FSR Frame Generation, which takes it as the HUD-less colour (DLSS Frame Generation gets the
    /// HUD layer from the world pass instead).</summary>
    private static void SendHud() => _setHud(Plugin.HudInGeneratedFrames.Value ? 1 : 0);

    /// <summary>The frame generation the player picked, for the bridge: it moves the display to that backend at its next present (Off keeps
    /// the one that runs; frame generation is then not used).</summary>
    private static void SendPreference()
    {
        if (Plugin.FrameGenerationOn) _prefer((int)Plugin.FrameGeneration.Value - 1);
        _pickedAt = Frame.Count;
    }

    private static int _pickedAt;
    private static FrameGenerationMethod? _generationFallbackWarned;

    /// <summary>Once per pick: the frame generation wanted (picked, or DLSS's under Auto on an NVIDIA card) cannot run on this PC and the
    /// other one runs, with the reason (the settings row says so too).</summary>
    private static void ReportGenerationFallback()
    {
        if (FrameGenerationFallback() is not { } wanted || Plugin.FrameGeneration.Value == _generationFallbackWarned) return;
        _generationFallbackWarned = Plugin.FrameGeneration.Value;
        const string code = "framegen-frame-generation-fallback";
        var why = wanted == FrameGenerationMethod.DLSS ? FrameGenerationDlssMissing().Why : "see the bridge's error lines above";
        Plugin.Info.LogWarning(code + ": " + (Plugin.FrameGeneration.Value == FrameGenerationMethod.Auto ? "Auto wanted " : "picked ") + wanted
            + " frame generation, which cannot run on this PC (" + why + "); " + (wanted == FrameGenerationMethod.DLSS ? "AMD FSR" : "DLSS")
            + " frame generation runs instead.");
    }

    /// <summary>The frame generation backend that runs now: 0 none, 1 NVIDIA DLSS Frame Generation, 2 AMD FSR Frame Generation. It follows
    /// <see cref="Plugin.FrameGeneration"/> while the game runs.</summary>
    internal static int FrameGenerationBackend => Loaded ? _generationBackend() : 0;

    /// <summary>The frame generation wanted and not running because this PC cannot run it, while the other one runs (null otherwise): the
    /// pick, DLSS or FSR; under Auto, DLSS's on an NVIDIA card (on another card AMD's is Auto's own choice, not a fallback). A pick takes a
    /// present or two to reach the display, so for ten frames after it nothing counts as a fallback.</summary>
    internal static FrameGenerationMethod? FrameGenerationFallback()
    {
        var picked = Plugin.FrameGeneration.Value;
        if (picked == FrameGenerationMethod.Off || Frame.Count - _pickedAt < 10) return null;
        int running = FrameGenerationBackend;
        if (picked == FrameGenerationMethod.Auto)
            return running == 2 && (Missing)_generationDlssMissing(null, 0) is not (Missing.Runs or Missing.NotNvidia) ? FrameGenerationMethod.DLSS : null;
        return running != 0 && running != (int)picked - 1 ? picked : null;
    }

    /// <summary>Why DLSS does not run on the card the game renders on (native DlssMissing, bridge_common.h).</summary>
    internal enum Missing
    {
        Runs = 0,
        NotNvidia = 1,
        Driver = 2,
        Gpu = 3,
        Scheduling = 4,
        Windows = 5,
        Failed = 6,
        OtherCard = 7,
    }

    /// <summary>Why DLSS Super Resolution does not run (Runs when it does or was not tried), with the driver NGX asks for when it is too old.</summary>
    internal static (Missing Code, uint Major, uint Minor) DlssMissing()
    {
        if (!Loaded) return (Missing.Failed, 0, 0);
        uint major, minor;
        var code = (Missing)_dlssMissing(&major, &minor);
        return (code, major, minor);
    }

    private static readonly byte[] MissingBuffer = new byte[512];

    /// <summary>Why DLSS Frame Generation did not start (Runs when it did, or before the present bridge started it), with the reason and the
    /// card (developer text).</summary>
    internal static (Missing Code, string Why) FrameGenerationDlssMissing()
    {
        if (!Loaded) return (Missing.Failed, "native bridge not loaded");
        fixed (byte* buffer = MissingBuffer)
        {
            var code = (Missing)_generationDlssMissing(buffer, MissingBuffer.Length);
            int length = Array.IndexOf(MissingBuffer, (byte)0);
            return (code, Encoding.UTF8.GetString(buffer, length < 0 ? 0 : length));
        }
    }

    /// <summary>The present bridge is active (frame generation was asked for and can run): markers and inputs are worth sending.</summary>
    internal static bool Bridged => PresentState == 2;

    /// <summary>The present bridge's state: 0 the preloader did not install the hooks, 1 installed and no bridge built yet (also: this PC cannot run frame generation), 2 active,
    /// negative failed.</summary>
    internal static int PresentState => Loaded ? _presentState() : 0;

    /// <summary>Frame generation is generating frames now.</summary>
    internal static bool Generating => Loaded && _generationState(null, 0, null, null, null) == 2;

    /// <summary>Main thread: a Reflex/PCL marker for a Unity frame (0 sleep and SimulationStart, 1 SimulationEnd).</summary>
    internal static void Marker(int marker, int frame) => _frameMarker(marker, frame);

    /// <summary>Main thread: frame generation's shared inputs and resources go, a back buffer slot at a time as the presents go on (a present
    /// still being drawn keeps its own); the pass is off, or frame generation was switched off. The next frame with inputs builds them again.</summary>
    internal static void ReleaseFrameGeneration()
    {
        if (Bridged) _releaseGeneration();
    }

    /// <summary>The next frame generation parameter slot; the caller fills it and passes the pointer to the render event.</summary>
    internal static FrameGenerationParams* NextGenerationFrame()
    {
        var slot = _generationFrames + _nextGenerationSlot;
        _nextGenerationSlot = (_nextGenerationSlot + 1) % Slots;
        slot->Size = (uint)sizeof(FrameGenerationParams);
        slot->Version = FrameGenerationVersion;
        return slot;
    }

    private static readonly byte[] ReasonBuffer = new byte[512];

    /// <summary>Frame generation as a readback line: state (0 not running, 1 running and off, 2 on), why it is off, frames presented with
    /// it on, frames generated, frames the last present showed; then <see cref="Counters"/>.</summary>
    internal static string FrameGeneration()
    {
        if (!Loaded) return "native bridge not loaded";
        long frames, generated;
        int last, state;
        string reason;
        fixed (byte* buffer = ReasonBuffer)
        {
            state = _generationState(buffer, ReasonBuffer.Length, &frames, &generated, &last);
            int length = Array.IndexOf(ReasonBuffer, (byte)0);
            reason = Encoding.UTF8.GetString(buffer, length < 0 ? 0 : length);
        }
        return "frame generation state " + state + " (" + reason + "), frames " + frames + ", generated " + generated + ", last present showed " + last
            + " (frame " + Time.frameCount + "); " + Counters();
    }

    /// <summary>Development driver, AMD FSR Frame Generation: 1 starts watching what its swap chain shows (the middle row of up to 128 shown
    /// frames, rendered and generated); 0 stops and compares. A generated frame G between game frames A and B flickers when it is further
    /// from both than they are from each other (mean absolute difference, plus 8 levels of slack), or its brightness is more than 10 levels
    /// off theirs.</summary>
    internal static string ShownFrames(int start)
    {
        if (!Loaded) return "native bridge not loaded";
        var handle = NativeLibrary.Load(Path.Combine(Plugin.Folder, Plugin.NativeFile));
        var watch = (delegate* unmanaged<int, void>)NativeLibrary.GetExport(handle, "ForgeFrameGen_WatchShownFrames");
        if (start != 0)
        {
            watch(1);
            return "watching";
        }
        var read = (delegate* unmanaged<byte*, int, int*, uint*, int>)NativeLibrary.GetExport(handle, "ForgeFrameGen_ShownFrames");
        var rows = new byte[8192 * 4 * 128];
        var generated = new int[128];
        uint width;
        int count;
        fixed (byte* r = rows)
        fixed (int* g = generated)
            count = read(r, 128, g, &width);
        watch(0);
        int w = (int)width;
        if (count == 0 || w == 0) return "nothing watched (AMD FSR Frame Generation not on?)";
        double Mean(int i)
        {
            double sum = 0;
            for (int x = 0; x < w; x++) sum += rows[(i * w + x) * 4] + rows[(i * w + x) * 4 + 1] + rows[(i * w + x) * 4 + 2];
            return sum / (3.0 * w);
        }
        double Difference(int i, int j)
        {
            double sum = 0;
            for (int x = 0; x < w * 4; x++)
                if ((x & 3) != 3) sum += Math.Abs(rows[i * w * 4 + x] - rows[j * w * 4 + x]);
            return sum / (3.0 * w);
        }
        int judged = 0, flickers = 0;
        double worst = 0;
        for (int i = 1; i + 1 < count; i++)
        {
            if (generated[i] == 0 || generated[i - 1] != 0 || generated[i + 1] != 0) continue;
            judged++;
            double ga = Difference(i, i - 1), gb = Difference(i, i + 1), ab = Difference(i - 1, i + 1);
            double off = Math.Abs(Mean(i) - (Mean(i - 1) + Mean(i + 1)) / 2);
            worst = Math.Max(worst, Math.Min(ga, gb) - ab);
            if (Math.Min(ga, gb) > ab + 8 || off > 10) flickers++;
        }
        int generatedCount = 0;
        for (int i = 0; i < count; i++) generatedCount += generated[i];
        return "shown " + count + ", generated " + generatedCount + ", judged " + judged + ", flickering " + flickers + ", worst excess difference "
            + worst.ToString("F1");
    }

    /// <summary>Frame generation's counters since the game started: presents, presents with it on, frames generated, and the presents it
    /// left out by reason (the first four reasons are ordinary states, the others defects, each also in the error log once).</summary>
    internal static string Counters()
    {
        if (!Loaded) return "native bridge not loaded";
        fixed (byte* buffer = ReasonBuffer)
            return Encoding.UTF8.GetString(buffer, _generationCounters(buffer, ReasonBuffer.Length));
    }

    /// <summary>The present bridge (frame generation stage 4a) as a readback line: state (0 off, 1 requested but not built, 2 active,
    /// -1 not buildable, -2 stopped), what the graphics hooks saw being created, and frames presented through D3D12.</summary>
    internal static string PresentBridge()
    {
        if (!Loaded) return "native bridge not loaded";
        int devices, factories, chains;
        long presents;
        _graphicsCounts(&devices, &factories, &chains, &presents);
        return "present bridge state " + _presentState() + ", hooks saw " + devices + " devices, " + factories + " factories, " + chains
            + " swap chains; bridge presents " + presents + " (frame " + Time.frameCount + ")";
    }

    /// <summary>Starts the upscaler the player picked (<see cref="Plugin.Upscaler"/>), or the automatic one when the card cannot run it, on the
    /// render thread with <paramref name="anyTexture"/>'s device (Unity's D3D11 device), once per <see cref="Restart"/>.</summary>
    internal static void Initialize(Texture anyTexture)
    {
        if (!Loaded || !Plugin.UpscalerOn || _initIssued || (_restarting && _status() != (int)State.Idle)) return; // Off is never handed to the bridge
        _initIssued = true;
        _restarting = false;
        var data = Path.Combine(Paths.CachePath, "ForgeFrameGen");
        Directory.CreateDirectory(data);
        var init = (InitParams*)(_init != IntPtr.Zero ? _init : (IntPtr)NativeMemory.AllocZeroed((nuint)sizeof(InitParams)));
        if (_init == IntPtr.Zero)
        {
            init->LibraryFolder = Marshal.StringToHGlobalUni(Plugin.Folder);
            init->DataFolder = Marshal.StringToHGlobalUni(data);
            _init = (IntPtr)init;
        }
        init->Size = (uint)sizeof(InitParams);
        init->Version = AbiVersion;
        init->AnyTexture = anyTexture.GetNativeTexturePtr();
        init->Method = (int)Plugin.Upscaler.Value;
        var command = new CommandBuffer { name = "ForgeFrameGen upscaler start" };
        command.IssuePluginEventAndData(RenderEvent, EventInitialize, _init);
        Graphics.ExecuteCommandBuffer(command);
        command.Release();
    }

    private static bool _restarting;

    /// <summary>The player picked another upscaler: the running one stops on the render thread, and the next <see cref="Initialize"/> (the
    /// pass's next frame, or the settings rows) starts the new choice once it has. The pass must be stopped first.</summary>
    internal static void Restart()
    {
        if (!Loaded || !_initIssued) return;
        _initIssued = false;
        _restarting = true;
        _reportedChoice = null;
        _fallbackWarned = false;
        var command = new CommandBuffer { name = "ForgeFrameGen upscaler stop" };
        command.IssuePluginEventAndData(RenderEvent, EventShutdown, IntPtr.Zero);
        Graphics.ExecuteCommandBuffer(command);
        command.Release();
    }

    private static readonly byte[] ChoiceBuffer = new byte[1024];

    /// <summary>The upscaler that runs (<see cref="UpscalerMethod.Auto"/> for none), why it was chosen (developer text) and whether the
    /// player's choice could not run here so the automatic one was taken.</summary>
    internal static (UpscalerMethod Running, string Why, bool FellBack) Upscaler()
    {
        if (!Loaded) return (UpscalerMethod.Auto, "native bridge not loaded", false);
        int fellBack;
        fixed (byte* buffer = ChoiceBuffer)
        {
            var running = (UpscalerMethod)_upscaler(buffer, ChoiceBuffer.Length, &fellBack);
            int length = Array.IndexOf(ChoiceBuffer, (byte)0);
            return (running, Encoding.UTF8.GetString(buffer, length < 0 ? 0 : length), fellBack != 0);
        }
    }

    /// <summary>The next parameter slot for this frame's evaluation; the caller fills it and passes the pointer to the render event.</summary>
    internal static FrameParams* NextFrame()
    {
        var slot = _frames + _nextSlot;
        _nextSlot = (_nextSlot + 1) % Slots;
        slot->Size = (uint)sizeof(FrameParams);
        slot->Version = AbiVersion;
        return slot;
    }

    private static State _reported = State.Idle;
    private static bool _developer = true;
    private static string? _reportedChoice;
    private static bool _fallbackWarned;

    /// <summary>Once per start of the bridge: which upscaler runs and why (a developer line), and, when the player's choice cannot run
    /// on this card, a warning that the automatic choice was taken (the settings row says so too).</summary>
    private static void ReportChoice()
    {
        var (running, why, fellBack) = Upscaler();
        if (why == _reportedChoice) return;
        _reportedChoice = why;
        const string fallback = "framegen-upscaler-fallback", selected = "framegen-upscaler-selected";
        if (fellBack && !_fallbackWarned) Plugin.Info.LogWarning(fallback + ": " + why);
        _fallbackWarned |= fellBack;
        if (ForgeCore.Log.Developer) Plugin.Info.LogInfo(selected + ": " + running + "; " + why);
    }

    private static void DrainMessages()
    {
        if (_developer != ForgeCore.Log.Developer) _setDeveloperLog((_developer = ForgeCore.Log.Developer) ? 1 : 0);
        ReportGenerationFallback();
        var status = Status;
        if (status != _reported)
        {
            _reported = status;
            if (ForgeCore.Log.Developer) Plugin.Info.LogInfo("Upscaler bridge: " + status);
            if (status is not State.Idle) ReportChoice();
        }
        fixed (byte* buffer = MessageBuffer)
            for (int length; (length = _popMessage(buffer, MessageBuffer.Length)) >= 0;)
            {
                var text = Encoding.UTF8.GetString(buffer, length);
                var code = Status switch
                {
                    State.Unavailable => "framegen-unavailable",
                    State.ForeignLibrary => "framegen-foreign-library",
                    _ => "framegen-native-failed",
                };
                // A message while DLSS keeps running names what it runs without; any other leaves the game's own rendering.
                Plugin.Logger.ErrorOnce(code, Status is State.Ready or State.Running ? text + "." : text + "; the game renders unchanged.");
            }
        // The bridge's line kinds (native BridgeLogKind): 0 a developer line, 1 a failure, 2 a present frame generation left out.
        int kind;
        fixed (byte* buffer = MessageBuffer)
            for (int length; (length = _popBridgeMessage(buffer, MessageBuffer.Length, &kind)) >= 0;)
            {
                if (kind == 0 && !_developer) continue;
                var text = Encoding.UTF8.GetString(buffer, length);
                if (kind == 1) Plugin.Logger.ErrorOnce("framegen-frame-bridge-failed", text);
                else if (kind == 2) Plugin.Logger.ErrorOnce("framegen-frame-generation-skipped", text);
                else if (kind == 3) Plugin.Logger.ErrorOnce("framegen-frame-generation-switch-failed", text);
                else if (ForgeCore.Log.Developer) Plugin.Info.LogInfo(text);
            }
    }
}

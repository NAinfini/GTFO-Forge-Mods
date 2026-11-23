// ForgeFrameGen DLSS Frame Generation backend (Docs/forge-upscale/PLAN.md §3.3, stage 4b). See frame_generation_backend.h.
//
// Streamline is loaded only from the plugin folder, only after its signature checks out and only when no other copy of it (or of
// the frame generation library) is in the process. It runs with manual hooking on the bridge's own D3D12 device: the present queue
// and the display swap chain are made through its proxies, everything else talks to the real objects.
//
// Markers (Reflex and PCL, frame tokens indexed by the Unity frame number): Sleep and SimulationStart in the plugin's Update, SimulationEnd and
// RenderSubmitStart (a render event) at the end of LateUpdate, RenderSubmitEnd, PresentStart and PresentEnd around the bridge's Present.
//
// Portions adapted from RiskOfRainUpscaling (https://github.com/xanderfrangos/RiskOfRainUpscaling, commit
// 2bea5e1e1122751264ff2512ca66d646112e24eb: native/FrameGeneration/DlssFrameGenerator.cpp, FrameGenerationSpace.h).
// Copyright (c) 2026 Xander Frangos. MIT License; the full text ships as RiskOfRainUpscaling-LICENSE.txt.
#include "frame_generation_backend.h"
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cstring>
#include <mutex>
#include <set>
#include <stdexcept>
#include <string>

#include <sl.h>
#include <sl_dlss_g.h>
#include <sl_pcl.h>
#include <sl_reflex.h>
#include <sl_security.h>

using Microsoft::WRL::ComPtr;

namespace fg
{
namespace
{
// The same project ID and engine as the DLSS Super Resolution side (forge_framegen.cpp).
constexpr const char *kProjectId = "6f1c2a8e-4b7d-4e3a-9c55-1d0f7a2b9e41";
constexpr const char *kEngineVersion = "2019.4.21f1";

HMODULE g_module = nullptr;
std::wstring g_folder;
PFun_slInit *g_init{};
PFun_slShutdown *g_shutdown{};
PFun_slSetD3DDevice *g_setDevice{};
PFun_slIsFeatureSupported *g_supported{};
PFun_slGetFeatureFunction *g_function{};
PFun_slUpgradeInterface *g_upgrade{};
PFun_slGetNewFrameToken *g_token{};
PFun_slSetConstants *g_constants{};
PFun_slSetTagForFrame *g_tag{};
PFun_slFreeResources *g_free{};
PFun_slSetFeatureLoaded *g_setLoaded{};
PFun_slDLSSGSetOptions *g_options{};
PFun_slDLSSGGetState *g_state{};
PFun_slReflexSetOptions *g_reflexOptions{};
PFun_slReflexSleep *g_sleep{};
PFun_slPCLSetMarker *g_marker{};

const sl::ViewportHandle g_viewport{0u};

std::atomic<bool> g_on{false};  // the options last set were eOn (set on the render thread)
bool g_retained = false;        // off with its resources kept (render thread)
bool g_loaded = true;           // DLSS Frame Generation is loaded (Streamline loads it at start; unloaded while AMD's runs)
uint32_t g_minDimension = 0, g_maxGenerated = 0;
bool g_vsync = false, g_haveState = false;

// Reflex as the player set it (any thread): bit 0 On + Boost, the rest the frame rate limit in frames a second (0 none). Applied on the
// main thread, before the frame's Reflex sleep, whenever it differs from what was last set.
std::atomic<unsigned> g_reflexWanted{0};
unsigned g_reflexSet = ~0u;

const char *ResultName(sl::Result result)
{
    switch (result)
    {
    case sl::Result::eOk: return "ok";
    case sl::Result::eErrorDriverOutOfDate: return "the NVIDIA driver is too old";
    case sl::Result::eErrorOSOutOfDate: return "Windows is too old";
    case sl::Result::eErrorOSDisabledHWS: return "hardware-accelerated GPU scheduling is off in Windows";
    case sl::Result::eErrorNoSupportedAdapterFound: return "no supported NVIDIA GPU";
    case sl::Result::eErrorAdapterNotSupported: return "this GPU does not support it";
    case sl::Result::eErrorNoPlugins: return "the Streamline plugins are missing";
    case sl::Result::eErrorFeatureNotSupported: return "not supported on this system";
    case sl::Result::eErrorFeatureMissing: return "the feature library is missing";
    case sl::Result::eErrorFeatureFailedToLoad: return "the feature library failed to load";
    case sl::Result::eErrorMissingProxy: return "a Streamline proxy is missing";
    default: return "Streamline error";
    }
}

// Why DLSS Frame Generation could not start (DlssMissing, bridge_common.h), from the Streamline result that stopped it.
int g_missing = DlssFailed;
int MissingOf(sl::Result result)
{
    switch (result)
    {
    case sl::Result::eOk: return DlssRuns;
    case sl::Result::eErrorDriverOutOfDate: return DlssDriver;
    case sl::Result::eErrorOSOutOfDate: return DlssWindows;
    case sl::Result::eErrorOSDisabledHWS: return DlssScheduling;
    case sl::Result::eErrorNoSupportedAdapterFound:
    case sl::Result::eErrorAdapterNotSupported:
    case sl::Result::eErrorFeatureNotSupported: return DlssGpu;
    default: return DlssFailed;
    }
}

void Check(sl::Result result, const char *what)
{
    if (result == sl::Result::eOk) return;
    char text[256];
    std::snprintf(text, sizeof text, "%s: %s (%d)", what, ResultName(result), (int)result);
    throw std::runtime_error(text);
}

// Streamline's own lines go to the developer log; the failure a player sees is this file's one line with the reason. A line from the
// same source place (file:line, past the time stamp) is written the first time only; repeats are counted in the counters line.
std::mutex g_streamlineLock;
std::set<std::string> g_streamlineSeen;
std::atomic<long long> g_streamlineRepeats{0};
void OnStreamlineLog(sl::LogType type, const char *message)
{
    if (type != sl::LogType::eError && type != sl::LogType::eWarn) return;
    std::string text = message ? message : "";
    size_t at = text.find(".cpp:"), start = text.find_last_of("] ", at), end = text.find_first_not_of("0123456789", at + 5);
    std::string place = at == std::string::npos ? text : text.substr(start == std::string::npos ? 0 : start + 1, end - (start == std::string::npos ? 0 : start + 1));
    {
        std::lock_guard<std::mutex> lock(g_streamlineLock);
        if (!g_streamlineSeen.insert(place).second)
        {
            ++g_streamlineRepeats;
            return;
        }
    }
    BridgeLog(LogInfo, "Streamline: %s (repeats of this line are only counted)", text.c_str());
}

void OnApiError(const sl::APIError &error)
{
    if (FAILED(error.hres)) Fail("Streamline reported a failed present");
}

template <class T> T Export(const char *name)
{
    auto pointer = reinterpret_cast<T>(GetProcAddress(g_module, name));
    if (!pointer) throw std::runtime_error(std::string("sl.interposer.dll has no ") + name);
    return pointer;
}

template <class T> T FeatureFunction(sl::Feature feature, const char *name)
{
    void *pointer = nullptr;
    if (g_function(feature, name, pointer) != sl::Result::eOk || !pointer) throw std::runtime_error(std::string("Streamline has no ") + name);
    return reinterpret_cast<T>(pointer);
}

// Another copy of Streamline or of the frame generation library already in the process (an overlay, another mod) would share
// global state with ours; frame generation then stays off.
bool ForeignRuntime(std::string &found) { return ForeignModule(g_folder, {L"sl.", L"nvngx_dlssg"}, found); }

// One entry per Unity frame: its frame token and the markers already set for it. slGetNewFrameToken hands out the slots of a ring of
// six (sl::MAX_FRAMES_IN_FLIGHT) that later calls overwrite, so a kept token is used only while it still reads its own frame's
// index and is asked for again otherwise. That is one call a frame: two or three frames are in flight, and nothing here asks for
// the token of a frame that old (the inputs of another frame than the one presented are refused before their token is taken).
struct Timing
{
    int frame = 0;
    unsigned markers = 0;
    sl::FrameToken *token = nullptr;
};
std::mutex g_tokenLock;
std::array<Timing, 16> g_timings; // by frame number modulo the size; the frames asked for only move forward
int g_presentingFrame = 0;        // the frame of the present under way (render thread)
sl::FrameToken *g_useToken = nullptr; // the token Admit took for Use (render thread)

// Both with g_tokenLock held.
Timing *Find(int frame)
{
    if (frame <= 0) return nullptr;
    auto &timing = g_timings[(unsigned)frame % g_timings.size()];
    return timing.frame == frame ? &timing : nullptr;
}

Timing *TimingOf(int frame)
{
    if (frame <= 0) return nullptr;
    auto &timing = g_timings[(unsigned)frame % g_timings.size()];
    if (timing.frame != frame) timing = Timing{frame, 0, nullptr};
    return &timing;
}

sl::FrameToken *TokenOf(Timing &timing)
{
    if (timing.token && (uint32_t)*timing.token == (uint32_t)timing.frame) return timing.token;
    uint32_t index = (uint32_t)timing.frame;
    sl::FrameToken *token = nullptr;
    timing.token = g_token(token, &index) == sl::Result::eOk ? token : nullptr;
    return timing.token;
}

std::atomic<bool> g_markerReported{false};
bool SetMarker(sl::PCLMarker marker, int frame)
{
    std::lock_guard<std::mutex> lock(g_tokenLock);
    auto *timing = TimingOf(frame);
    if (!timing) return false;
    unsigned bit = 1u << (unsigned)marker;
    if (timing->markers & bit) return true;
    auto *token = TokenOf(*timing);
    sl::Result result = token ? g_marker(marker, *token) : sl::Result::eErrorInvalidParameter;
    if (result != sl::Result::eOk)
    {
        ++g_markerRefusals;
        if (!g_markerReported.exchange(true))
            BridgeLog(LogSkipped, "Streamline refused marker %u of frame %d: %s. Frame generation leaves out the presents of frames without their markers; later refusals are only counted.",
                      (unsigned)marker, frame, token ? ResultName(result) : "no frame token");
        return false;
    }
    timing->markers |= bit;
    return true;
}

struct OptionsKey
{
    bool on = false, hud = false;
    UINT width = 0, height = 0, renderWidth = 0, renderHeight = 0, buffers = 0;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    bool operator==(const OptionsKey &other) const
    {
        return on == other.on && hud == other.hud && width == other.width && height == other.height && renderWidth == other.renderWidth && renderHeight == other.renderHeight &&
               buffers == other.buffers && format == other.format;
    }
} g_optionsKey, g_logged;

// Off for now (a menu, the map, a resize, a present without inputs): frame generation keeps its resources, as NVIDIA's guide asks for
// anything short, so the next frame with inputs turns it on again without allocating. release: off for long (the player's setting),
// so the resources go: with the Off itself, or, when it was already off with them kept, by slFreeResources once a Present has run
// since that Off (the options take effect in the next Present).
long long g_offAt = 0;
bool g_bound = false;
void SetOff(bool release = false)
{
    if (!g_bound) return;
    if (g_on)
    {
        sl::DLSSGOptions off{};
        off.mode = sl::DLSSGMode::eOff;
        if (!release) off.flags = sl::DLSSGFlags::eRetainResourcesWhenOff;
        g_options(g_viewport, off);
        g_on = false;
        g_retained = !release;
        g_offAt = g_presents;
        g_optionsKey = OptionsKey{};
        return;
    }
    if (!release || !g_retained || g_presents <= g_offAt + 1) return;
    g_retained = false;
    sl::Result freed = g_free(sl::kFeatureDLSS_G, g_viewport);
    if (freed != sl::Result::eOk)
        BridgeLog(LogFailure, "Freeing frame generation's resources failed (%s, %d); they stay allocated until frame generation runs again.", ResultName(freed), (int)freed);
}

// Sets Reflex as the player wants it now. Low latency is always on: frame generation needs it.
sl::Result ApplyReflex()
{
    unsigned wanted = g_reflexWanted;
    sl::ReflexOptions reflex{};
    reflex.mode = wanted & 1 ? sl::ReflexMode::eLowLatencyWithBoost : sl::ReflexMode::eLowLatency;
    reflex.frameLimitUs = wanted >> 1 ? 1000000u / (wanted >> 1) : 0u;
    sl::Result result = g_reflexOptions(reflex);
    if (result == sl::Result::eOk)
    {
        g_reflexSet = wanted;
        BridgeLog(LogInfo, "Reflex: %s, frame rate limit %u.", wanted & 1 ? "on + boost" : "on", wanted >> 1);
    }
    return result;
}

class Dlss final : public Backend
{
public:
    bool Start(const wchar_t *folder, IDXGIAdapter *adapter, std::string &why) override
    {
        g_folder = folder;
        g_missing = DlssFailed;
        bool initialized = false;
        // Each Streamline answer that can stop it says why, for the plugin's fallback warning and settings hint.
        auto check = [](sl::Result result, const char *what) {
            if (result != sl::Result::eOk) g_missing = MissingOf(result);
            Check(result, what);
        };
        try
        {
            std::string foreign;
            if (ForeignRuntime(foreign)) throw std::runtime_error("another copy of Streamline or of the frame generation library is loaded (" + foreign + ")");
            std::wstring path = g_folder + L"\\sl.interposer.dll";
            if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) throw std::runtime_error("sl.interposer.dll is missing from the plugin folder");
            if (!sl::security::verifyEmbeddedSignature(path.c_str())) throw std::runtime_error("sl.interposer.dll is not signed by NVIDIA");
            g_module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (!g_module) throw std::runtime_error("sl.interposer.dll could not be loaded");
            wchar_t loaded[MAX_PATH * 2]{};
            GetModuleFileNameW(g_module, loaded, MAX_PATH * 2);
            if (_wcsicmp(loaded, path.c_str())) throw std::runtime_error("sl.interposer.dll was loaded from another folder");
            g_init = Export<PFun_slInit *>("slInit");
            g_shutdown = Export<PFun_slShutdown *>("slShutdown");
            g_setDevice = Export<PFun_slSetD3DDevice *>("slSetD3DDevice");
            g_supported = Export<PFun_slIsFeatureSupported *>("slIsFeatureSupported");
            g_function = Export<PFun_slGetFeatureFunction *>("slGetFeatureFunction");
            g_upgrade = Export<PFun_slUpgradeInterface *>("slUpgradeInterface");
            g_token = Export<PFun_slGetNewFrameToken *>("slGetNewFrameToken");
            g_constants = Export<PFun_slSetConstants *>("slSetConstants");
            g_tag = Export<PFun_slSetTagForFrame *>("slSetTagForFrame");
            g_free = Export<PFun_slFreeResources *>("slFreeResources");
            g_setLoaded = Export<PFun_slSetFeatureLoaded *>("slSetFeatureLoaded");

            const wchar_t *paths[] = {g_folder.c_str()};
            const sl::Feature features[] = {sl::kFeatureDLSS_G, sl::kFeatureReflex, sl::kFeaturePCL};
            sl::Preferences preferences{};
            preferences.pathsToPlugins = paths;
            preferences.numPathsToPlugins = 1;
            preferences.featuresToLoad = features;
            preferences.numFeaturesToLoad = 3;
            preferences.renderAPI = sl::RenderAPI::eD3D12;
            preferences.engine = sl::EngineType::eUnity;
            preferences.engineVersion = kEngineVersion;
            preferences.projectId = kProjectId;
            preferences.logMessageCallback = &OnStreamlineLog;
            // No over-the-air updates or downloaded plugins: only the packaged, pinned files run.
            preferences.flags = sl::PreferenceFlags::eUseManualHooking | sl::PreferenceFlags::eDisableCLStateTracking | sl::PreferenceFlags::eUseFrameBasedResourceTagging;
            check(g_init(preferences, sl::kSDKVersion), "starting Streamline");
            initialized = true;

            DXGI_ADAPTER_DESC desc{};
            if (FAILED(adapter->GetDesc(&desc))) throw std::runtime_error("adapter description");
            sl::AdapterInfo info{};
            info.deviceLUID = reinterpret_cast<uint8_t *>(&desc.AdapterLuid);
            info.deviceLUIDSizeInBytes = sizeof(LUID);
            check(g_supported(sl::kFeatureDLSS_G, info), "DLSS Frame Generation");
            check(g_supported(sl::kFeatureReflex, info), "NVIDIA Reflex");
            check(g_supported(sl::kFeaturePCL, info), "PC latency markers");
            g_missing = DlssRuns;
            SetReason("waiting for the first frame");
            BridgeLog(LogInfo, "Frame generation: Streamline started (DLSS Frame Generation, Reflex and PCL supported).");
            return true;
        }
        catch (const std::exception &error)
        {
            why = error.what();
            // Streamline started but this card cannot run DLSS Frame Generation: it goes, so that another backend has the process to itself.
            if (initialized) g_shutdown();
            return false;
        }
    }

    void Bind(ID3D12Device *device) override
    {
        Check(g_setDevice(device), "binding the D3D12 device");
        g_options = FeatureFunction<PFun_slDLSSGSetOptions *>(sl::kFeatureDLSS_G, "slDLSSGSetOptions");
        g_state = FeatureFunction<PFun_slDLSSGGetState *>(sl::kFeatureDLSS_G, "slDLSSGGetState");
        g_reflexOptions = FeatureFunction<PFun_slReflexSetOptions *>(sl::kFeatureReflex, "slReflexSetOptions");
        g_sleep = FeatureFunction<PFun_slReflexSleep *>(sl::kFeatureReflex, "slReflexSleep");
        g_marker = FeatureFunction<PFun_slPCLSetMarker *>(sl::kFeaturePCL, "slPCLSetMarker");
        auto reflexState = FeatureFunction<PFun_slReflexGetState *>(sl::kFeatureReflex, "slReflexGetState");
        Check(ApplyReflex(), "Reflex low latency");
        sl::ReflexState state{};
        Check(reflexState(state), "Reflex state");
        if (!state.lowLatencyAvailable) throw std::runtime_error("Reflex low latency is not available");
        g_bound = true;
    }

    void Upgrade(ID3D12Device *device, ID3D12Device **queueDevice, IDXGIFactory2 **factory) override
    {
        void *upgraded = device;
        device->AddRef();
        if (g_upgrade(&upgraded) != sl::Result::eOk || upgraded == device)
        {
            device->Release();
            throw std::runtime_error("Streamline could not take over the D3D12 device");
        }
        *queueDevice = static_cast<ID3D12Device *>(upgraded);
        ComPtr<IDXGIFactory2> plain;
        if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&plain)))) throw std::runtime_error("DXGI factory for the display");
        upgraded = plain.Detach();
        auto *original = upgraded;
        if (g_upgrade(&upgraded) != sl::Result::eOk || upgraded == original)
        {
            static_cast<IUnknown *>(original)->Release();
            throw std::runtime_error("Streamline could not take over the DXGI factory");
        }
        *factory = static_cast<IDXGIFactory2 *>(upgraded);
    }

    // AMD FSR Frame Generation takes the display (the bridge let go of this backend's swap chain): DLSS Frame Generation's resources go
    // and it is unloaded, as NVIDIA's guide does when it is switched off for long (DLSS-G guide §18), and Reflex goes off: it runs with
    // DLSS Frame Generation only.
    void Unload() override
    {
        if (g_retained) g_free(sl::kFeatureDLSS_G, g_viewport);
        g_on = false;
        g_retained = false;
        g_optionsKey = OptionsKey{};
        sl::ReflexOptions off{};
        off.mode = sl::ReflexMode::eOff;
        Check(g_reflexOptions(off), "switching Reflex off");
        g_reflexSet = ~0u;
        Check(g_setLoaded(sl::kFeatureDLSS_G, false), "unloading DLSS Frame Generation");
        g_loaded = false;
    }

    // Before the bridge makes this backend's display swap chain again: DLSS Frame Generation loaded (its functions taken anew), and Reflex
    // set as the player wants it before the next frame's sleep.
    void Load() override
    {
        if (!g_loaded)
        {
            Check(g_setLoaded(sl::kFeatureDLSS_G, true), "loading DLSS Frame Generation");
            g_options = FeatureFunction<PFun_slDLSSGSetOptions *>(sl::kFeatureDLSS_G, "slDLSSGSetOptions");
            g_state = FeatureFunction<PFun_slDLSSGGetState *>(sl::kFeatureDLSS_G, "slDLSSGGetState");
            g_loaded = true;
        }
        g_reflexSet = ~0u;
    }

    int Kind() const override { return 1; }
    bool On() const override { return g_on; }

    void SetReflex(unsigned wanted) override { g_reflexWanted = wanted; }

    void RenderSubmitStart(int frame) override { SetMarker(sl::PCLMarker::eRenderSubmitStart, frame); }

    void Marker(int marker, int frame) override
    {
        if (marker == (int)sl::PCLMarker::eSimulationStart)
        {
            sl::FrameToken *token = nullptr;
            {
                std::lock_guard<std::mutex> lock(g_tokenLock);
                auto *timing = TimingOf(frame);
                token = timing ? TokenOf(*timing) : nullptr;
            }
            // The player's Reflex setting changed: set before this frame's sleep. A refusal is reported once and tried no more until the
            // setting changes again; Reflex keeps what it had.
            if (g_reflexSet != g_reflexWanted.load())
            {
                unsigned wanted = g_reflexWanted;
                sl::Result result = ApplyReflex();
                if (result != sl::Result::eOk)
                {
                    g_reflexSet = wanted;
                    BridgeLog(LogFailure, "Reflex refused its new setting (%s, %d); it keeps the one before.", ResultName(result), (int)result);
                }
            }
            // Reflex sleeps first, then the simulation starts (the guide's order). The sleep runs outside the lock: the render thread sets
            // the previous frame's markers meanwhile, and the token stays this frame's (the ring moves one slot a frame).
            if (token && g_sleep(*token) != sl::Result::eOk) return Fail("Reflex sleep failed");
        }
        if (marker == (int)sl::PCLMarker::eSimulationStart || marker == (int)sl::PCLMarker::eSimulationEnd) SetMarker((sl::PCLMarker)marker, frame);
    }

    void PresentBegins(int frame) override
    {
        g_presentingFrame = frame;
        g_useToken = nullptr;
        SetMarker(sl::PCLMarker::eRenderSubmitEnd, frame);
    }

    void Admit(const SlotInputs &inputs, int frame, unsigned slot, Skip &why, const char *&off, char *detail, size_t capacity) override
    {
        std::lock_guard<std::mutex> lock(g_tokenLock);
        const int inputFrame = inputs.params.frame;
        auto *timing = Find(inputFrame);
        const unsigned markers = timing ? timing->markers : 0u;
        const unsigned needed = (1u << (unsigned)sl::PCLMarker::eSimulationStart) | (1u << (unsigned)sl::PCLMarker::eSimulationEnd) |
                                (1u << (unsigned)sl::PCLMarker::eRenderSubmitStart);
        // The present's markers go to the frame whose render submission started last; tags and constants must name the same frame.
        if (inputFrame != frame) why = SkipOtherFrame, off = "the frame generation inputs belong to another frame than the one presented";
        else if ((markers & needed) != needed) why = SkipMarkersMissing, off = "the frame's simulation and render markers are missing";
        else if (!(g_useToken = TokenOf(*timing))) why = SkipNoToken, off = "Streamline gave no frame token";
        if (off)
        {
            auto *presented = Find(frame);
            std::snprintf(detail, capacity, "inputs of frame %d (markers 0x%X), presenting frame %d (markers 0x%X), back buffer %u, present %lld", inputFrame,
                          markers, frame, presented ? presented->markers : 0u, slot, g_presents.load());
        }
    }

    void Supported(const DXGI_SWAP_CHAIN_DESC1 &desc, UINT interval, Skip &why, const char *&off) override
    {
        if (!off && g_haveState && (desc.Width < g_minDimension || desc.Height < g_minDimension))
            why = SkipUnsupported, off = "the window is smaller than frame generation supports";
        if (!off && g_haveState && g_maxGenerated < 1) why = SkipUnsupported, off = "this GPU generates no frames";
        if (!off && interval && g_haveState && !g_vsync) why = SkipUnsupported, off = "VSync is on and frame generation does not support it here";
    }

    void LeaveOut(int frame, const DXGI_SWAP_CHAIN_DESC1 &desc, ID3D12Resource *backbuffer) override
    {
        // A present left out still passes through frame generation (the one that turns it off, the first after a resize), which asks for
        // this frame's back buffer.
        std::lock_guard<std::mutex> lock(g_tokenLock);
        auto *presented = Find(frame);
        if (auto *own = presented ? TokenOf(*presented) : nullptr)
        {
            sl::Extent full{0, 0, desc.Width, desc.Height};
            sl::Resource back{sl::ResourceType::eTex2d, backbuffer, nullptr, nullptr, D3D12_RESOURCE_STATE_PRESENT};
            sl::ResourceTag tag{&back, sl::kBufferTypeBackbuffer, sl::ResourceLifecycle::eValidUntilPresent, &full};
            g_tag(*own, g_viewport, &tag, 1, nullptr);
        }
    }

    void Off(bool release) override { SetOff(release); }

    void Use(const SlotInputs &inputs, unsigned, const DXGI_SWAP_CHAIN_DESC1 &desc, UINT, ID3D12Resource *backbuffer) override
    {
        sl::FrameToken *token = g_useToken;
        const auto &p = inputs.params;
        sl::Constants constants{};
        std::memcpy(&constants.cameraViewToClip, p.viewToClip, sizeof p.viewToClip);
        std::memcpy(&constants.clipToCameraView, p.clipToView, sizeof p.clipToView);
        std::memcpy(&constants.clipToPrevClip, p.clipToPrevClip, sizeof p.clipToPrevClip);
        std::memcpy(&constants.prevClipToClip, p.prevClipToClip, sizeof p.prevClipToClip);
        sl::float4x4 identity{};
        identity[0] = {1, 0, 0, 0};
        identity[1] = {0, 1, 0, 0};
        identity[2] = {0, 0, 1, 0};
        identity[3] = {0, 0, 0, 1};
        constants.clipToLensClip = identity;
        constants.jitterOffset = {p.jitterX, p.jitterY};
        constants.mvecScale = {p.motionScaleX, p.motionScaleY};
        constants.cameraPinholeOffset = {0, 0};
        constants.cameraPos = {p.cameraPosition[0], p.cameraPosition[1], p.cameraPosition[2]};
        constants.cameraUp = {p.cameraUp[0], p.cameraUp[1], p.cameraUp[2]};
        constants.cameraRight = {p.cameraRight[0], p.cameraRight[1], p.cameraRight[2]};
        constants.cameraFwd = {p.cameraForward[0], p.cameraForward[1], p.cameraForward[2]};
        constants.cameraNear = p.cameraNear;
        constants.cameraFar = p.cameraFar;
        constants.cameraFOV = p.cameraFov;
        constants.cameraAspectRatio = p.cameraAspect;
        constants.depthInverted = p.depthInverted ? sl::Boolean::eTrue : sl::Boolean::eFalse;
        constants.cameraMotionIncluded = sl::Boolean::eTrue;
        constants.motionVectors3D = sl::Boolean::eFalse;
        constants.reset = p.reset || !g_on ? sl::Boolean::eTrue : sl::Boolean::eFalse;
        Check(g_constants(constants, *token, g_viewport), "frame generation constants");

        sl::Extent render{};
        render.width = p.renderWidth;
        render.height = p.renderHeight;
        sl::Extent full{};
        full.width = desc.Width;
        full.height = desc.Height;
        sl::Resource depth{sl::ResourceType::eTex2d, inputs.depth.texture12.Get(), nullptr, nullptr, D3D12_RESOURCE_STATE_COMMON};
        sl::Resource motion{sl::ResourceType::eTex2d, inputs.motion.texture12.Get(), nullptr, nullptr, D3D12_RESOURCE_STATE_COMMON};
        sl::Resource hudless{sl::ResourceType::eTex2d, inputs.hudless.texture12.Get(), nullptr, nullptr, D3D12_RESOURCE_STATE_COMMON};
        sl::Resource hud{sl::ResourceType::eTex2d, inputs.hud.texture12.Get(), nullptr, nullptr, D3D12_RESOURCE_STATE_COMMON};
        // The back buffer is tagged with its whole extent (as on a present left out): without it Streamline warns and assumes the same.
        // The HUD's colour and opacity come last and are tagged only when the plugin measured them this frame.
        sl::Resource back{sl::ResourceType::eTex2d, backbuffer, nullptr, nullptr, D3D12_RESOURCE_STATE_PRESENT};
        sl::ResourceTag tags[] = {
            {&back, sl::kBufferTypeBackbuffer, sl::ResourceLifecycle::eValidUntilPresent, &full},
            {&depth, sl::kBufferTypeDepth, sl::ResourceLifecycle::eValidUntilPresent, &render},
            {&motion, sl::kBufferTypeMotionVectors, sl::ResourceLifecycle::eValidUntilPresent, &render},
            {&hudless, sl::kBufferTypeHUDLessColor, sl::ResourceLifecycle::eValidUntilPresent, &full},
            {&hud, sl::kBufferTypeUIColorAndAlpha, sl::ResourceLifecycle::eValidUntilPresent, &full},
        };
        Check(g_tag(*token, g_viewport, tags, inputs.hasHud ? 5 : 4, nullptr), "frame generation inputs");

        OptionsKey key;
        key.on = true;
        key.hud = inputs.hasHud;
        key.width = desc.Width;
        key.height = desc.Height;
        key.renderWidth = p.renderWidth;
        key.renderHeight = p.renderHeight;
        key.buffers = desc.BufferCount;
        key.format = desc.Format;
        if (!(key == g_optionsKey))
        {
            sl::DLSSGOptions on{};
            on.mode = sl::DLSSGMode::eOn;
            on.flags = sl::DLSSGFlags::eRetainResourcesWhenOff;
            on.numFramesToGenerate = 1;
            on.numBackBuffers = desc.BufferCount;
            on.colorWidth = desc.Width;
            on.colorHeight = desc.Height;
            on.mvecDepthWidth = p.renderWidth;
            on.mvecDepthHeight = p.renderHeight;
            on.colorBufferFormat = desc.Format;
            on.depthBufferFormat = DXGI_FORMAT_R32_FLOAT;
            on.mvecBufferFormat = DXGI_FORMAT_R16G16_FLOAT;
            on.hudLessBufferFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
            if (key.hud)
            {
                // With the HUD-less colour and the HUD both tagged, the scene and the HUD are interpolated apart and composited afterwards
                // (the guide's user interface recomposition). Streamline's own full-screen menu detection stays off: the helmet glass
                // lies over the whole screen, and the plugin already leaves the map and the menu out by the game's focus state.
                on.uiBufferFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
                on.enableUserInterfaceRecomposition = sl::Boolean::eTrue;
            }
            on.onErrorCallback = &OnApiError;
            on.queueParallelismMode = sl::DLSSGQueueParallelismMode::eBlockPresentingClientQueue;
            g_on = true; // an Off is attempted even if this call fails half way
            Check(g_options(g_viewport, on), "turning frame generation on");
            if (!(key.width == g_logged.width && key.height == g_logged.height && key.renderWidth == g_logged.renderWidth && key.renderHeight == g_logged.renderHeight))
                BridgeLog(LogInfo, "Frame generation options: on, %ux%u from %ux%u inputs, %u back buffers.", desc.Width, desc.Height, p.renderWidth, p.renderHeight,
                          desc.BufferCount);
            if (key.hud != g_logged.hud) BridgeLog(LogInfo, "Frame generation composites the HUD itself: %s.", key.hud ? "yes" : "no");
            g_optionsKey = g_logged = key;
        }
    }

    void PresentMarker(bool start) override { SetMarker(start ? sl::PCLMarker::ePresentStart : sl::PCLMarker::ePresentEnd, g_presentingFrame); }

    void After() override
    {
        sl::DLSSGState state{};
        if (g_state(g_viewport, state, nullptr) != sl::Result::eOk) return Fail("reading the frame generation state failed");
        if (!g_haveState)
            BridgeLog(LogInfo, "Frame generation capabilities: up to %u generated frames, minimum size %u, VSync %s.", state.numFramesToGenerateMax,
                      state.minWidthOrHeight, state.bIsVsyncSupportAvailable == sl::Boolean::eTrue ? "supported" : "not supported");
        g_haveState = true;
        g_minDimension = state.minWidthOrHeight;
        g_maxGenerated = state.numFramesToGenerateMax;
        g_vsync = state.bIsVsyncSupportAvailable == sl::Boolean::eTrue;
        g_lastPresented = (int)state.numFramesActuallyPresented;
        if (g_on)
        {
            ++g_frames;
            if (state.numFramesActuallyPresented > 1) g_generated += state.numFramesActuallyPresented - 1;
            if (state.status != sl::DLSSGStatus::eOk)
            {
                // Off, as the guide asks; a status that comes back at the next try holds frame generation off for a while (Defect).
                char text[96];
                std::snprintf(text, sizeof text, "DLSS Frame Generation reported status 0x%X", (unsigned)state.status);
                Skipped(SkipStatus, text);
                Defect(SkipStatus);
                SetOff();
                SetReason(text);
            }
        }
    }

    int CountersTail(char *text, int capacity) override
    {
        return std::snprintf(text, (size_t)capacity, "; Streamline lines repeated %lld", g_streamlineRepeats.load());
    }
};
} // namespace

Backend &DlssBackend()
{
    static Dlss backend;
    return backend;
}

int DlssStartMissing() { return g_missing; }
} // namespace fg

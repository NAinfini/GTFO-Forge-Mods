// ForgeFrameGen native bridge: super resolution on Unity's D3D11 device, by NVIDIA DLSS (NGX) or AMD FSR (fsr_upscale.cpp), one
// backend at a time, chosen when the plugin starts the bridge (Docs/forge-upscale/PLAN.md §3.2). Both take the same frame inputs; only
// the hand-over differs.
// Everything that touches the device context runs on Unity's render thread, through the render-event callback that the plugin issues
// from a CommandBuffer (IssuePluginEventAndData). The managed side never calls NGX or FSR directly; it only reads status and messages.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <psapi.h>
#include <d3d11.h>
#include <atomic>
#include <mutex>
#include <string>
#include <deque>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <algorithm>
#include <cwchar>

#include "nvsdk_ngx.h"
#include "nvsdk_ngx_helpers.h"
#include "nvsdk_ngx_helpers_d3d.h"
#include "frame_generation.h"
#include "fsr_upscale.h"
#include <wrl/client.h>
#include <stdexcept>

using Microsoft::WRL::ComPtr;

namespace
{
// Bumped whenever a struct below changes layout; the plugin passes the same number and the bridge refuses a mismatch.
constexpr uint32_t kAbiVersion = 3;

// The project ID NGX requires from an application without an NVIDIA application ID (engine type CUSTOM, GUID-like).
constexpr const char *kProjectId = "6f1c2a8e-4b7d-4e3a-9c55-1d0f7a2b9e41";
constexpr const char *kEngineVersion = "Unity 2019.4.21f1";

enum Event : int
{
    EventInitialize = 1,
    EventEvaluate = 2,
    EventShutdown = 3,
    EventFrameGeneration = 4, // data: fg::FrameParams* (frame_generation.h)
};

enum Status : int
{
    StatusIdle = 0,
    StatusReady = 1,           // an upscaler is started (NGX initialised with DLSS SR available, or FSR)
    StatusRunning = 2,         // a DLSS feature or an FSR context exists and evaluates
    StatusUnavailable = 3,     // neither DLSS nor FSR can run on this GPU and driver
    StatusFailed = 4,          // an NGX or FSR call failed; see the messages
    StatusForeignLibrary = 5,  // a DLSS library other than the packaged one was loaded; DLSS stays off
};

// Which upscaler the player asked for (InitParams::method) and which one runs (ForgeFrameGen_Upscaler).
enum Method : int32_t
{
    MethodAuto = 0,
    MethodDlss = 1,
    MethodFsr = 2,
};

#pragma pack(push, 8)
struct InitParams
{
    uint32_t size;
    uint32_t version;
    void *anyTexture;                // any D3D11 texture of Unity's device: its device is the one NGX or FSR uses
    const wchar_t *libraryFolder;    // the folder holding the packaged nvngx_dlss.dll and FSR DLLs; the only path they are loaded from
    const wchar_t *dataFolder;       // NGX's writable folder (logs)
    int32_t method;                  // Method: Auto picks DLSS on an NVIDIA card that has it, FSR everywhere else
};
#pragma pack(pop)

static_assert(sizeof(InitParams) == 40, "InitParams layout is shared with the plugin");

std::atomic<int> g_status{StatusIdle};
std::atomic<int> g_backend{MethodAuto}; // the upscaler started: MethodDlss, MethodFsr, or MethodAuto for none
std::mutex g_choiceLock;
std::string g_choice;      // why that upscaler (or none) runs, for the plugin's log and settings hint
bool g_fellBack = false;   // the player's choice could not run here and the automatic choice was taken
std::string g_fsrVersions; // the FSR versions the SDK offers for this card, newest first
std::atomic<long long> g_evaluations{0};
std::mutex g_messagesLock;
std::deque<std::string> g_messages;

ID3D11Device *g_device = nullptr;
ID3D11DeviceContext *g_context = nullptr;
NVSDK_NGX_Parameter *g_params = nullptr;
NVSDK_NGX_Handle *g_feature = nullptr;
HMODULE g_packagedLibrary = nullptr;
std::wstring g_expectedLibrary;
bool g_ngxInitialized = false;

struct FeatureKey
{
    uint32_t renderWidth = 0, renderHeight = 0, outputWidth = 0, outputHeight = 0;
    int32_t flags = 0, quality = -1, preset = 0;
    bool operator==(const FeatureKey &other) const { return std::memcmp(this, &other, sizeof(FeatureKey)) == 0; }
} g_featureKey;

// A 1x1 texture holding 1: the exposure DLSS is given when it is not to measure one.
ComPtr<ID3D11Texture2D> g_exposureOne;

void Message(const std::string &text)
{
    std::lock_guard<std::mutex> lock(g_messagesLock);
    if (g_messages.size() < 64) g_messages.push_back(text);
}

std::string Narrow(const std::wstring &text)
{
    if (text.empty()) return {};
    int length = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), nullptr, 0, nullptr, nullptr);
    std::string result(length, '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(), result.data(), length, nullptr, nullptr);
    return result;
}

std::string ResultText(const char *what, NVSDK_NGX_Result result)
{
    char buffer[160];
    std::snprintf(buffer, sizeof buffer, "%s failed: NGX result 0x%08X", what, (unsigned)result);
    return buffer;
}

void Fail(int status, const std::string &text)
{
    g_status = status;
    Message(text);
}

// Paths of every module named nvngx_dlss.dll in the process. The packaged one must be the only one.
bool OnlyPackagedLibraryLoaded(std::wstring &foreign)
{
    HMODULE modules[1024];
    DWORD needed = 0;
    if (!EnumProcessModules(GetCurrentProcess(), modules, sizeof modules, &needed)) return true;
    for (DWORD i = 0; i < needed / sizeof(HMODULE); i++)
    {
        wchar_t path[MAX_PATH * 2];
        if (!GetModuleFileNameW(modules[i], path, (DWORD)(sizeof path / sizeof path[0]))) continue;
        const wchar_t *name = std::wcsrchr(path, L'\\');
        name = name ? name + 1 : path;
        if (_wcsicmp(name, L"nvngx_dlss.dll") != 0) continue;
        if (_wcsicmp(path, g_expectedLibrary.c_str()) != 0)
        {
            foreign = path;
            return false;
        }
    }
    return true;
}

void ReleaseFeature()
{
    if (g_feature)
    {
        NVSDK_NGX_D3D11_ReleaseFeature(g_feature);
        g_feature = nullptr;
    }
    g_featureKey = FeatureKey{};
}

ID3D11Resource *ExposureOne()
{
    if (!g_exposureOne)
    {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = desc.Height = desc.MipLevels = desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R32_FLOAT;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_IMMUTABLE;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        const float one = 1.0f;
        D3D11_SUBRESOURCE_DATA data{&one, sizeof one, 0};
        static bool reported = false;
        if (FAILED(g_device->CreateTexture2D(&desc, &data, &g_exposureOne)) && !reported)
        {
            reported = true;
            Message("The fixed exposure texture could not be made; DLSS measures the exposure itself");
        }
    }
    return g_exposureOne.Get();
}

void ReleaseNgx()
{
    ReleaseFeature();
    g_exposureOne.Reset();
    if (g_params)
    {
        NVSDK_NGX_D3D11_DestroyParameters(g_params);
        g_params = nullptr;
    }
    if (g_ngxInitialized)
    {
        NVSDK_NGX_D3D11_Shutdown1(g_device);
        g_ngxInitialized = false;
    }
}

void Shutdown()
{
    ReleaseNgx();
    fsr::Shutdown();
    if (g_context) { g_context->Release(); g_context = nullptr; }
    if (g_device) { g_device->Release(); g_device = nullptr; }
    g_backend = MethodAuto;
    g_status = StatusIdle;
}

// Starts NGX with DLSS Super Resolution. False with the status and reason when DLSS cannot run; nothing of NGX is left started then.
bool StartDlss(const InitParams *init, int &status, std::string &why)
{
    // Load the packaged library by full path first, so the copy in the package is the one in the process.
    g_expectedLibrary = std::wstring(init->libraryFolder) + L"\\nvngx_dlss.dll";
    if (!g_packagedLibrary) g_packagedLibrary = LoadLibraryExW(g_expectedLibrary.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!g_packagedLibrary)
    {
        status = StatusFailed;
        why = "Loading the packaged DLSS library failed (Windows error " + std::to_string(GetLastError()) + "): " + Narrow(g_expectedLibrary);
        return false;
    }

    const wchar_t *paths[] = {init->libraryFolder};
    NVSDK_NGX_FeatureCommonInfo common{};
    common.PathListInfo.Path = paths;
    common.PathListInfo.Length = 1;
    common.LoggingInfo.MinimumLoggingLevel = NVSDK_NGX_LOGGING_LEVEL_OFF;

    NVSDK_NGX_Result result = NVSDK_NGX_D3D11_Init_with_ProjectID(kProjectId, NVSDK_NGX_ENGINE_TYPE_CUSTOM, kEngineVersion,
                                                                  init->dataFolder, g_device, &common);
    if (NVSDK_NGX_FAILED(result))
    {
        status = result == NVSDK_NGX_Result_FAIL_FeatureNotSupported ? StatusUnavailable : StatusFailed;
        why = ResultText("NVSDK_NGX_D3D11_Init_with_ProjectID", result);
        return false;
    }
    g_ngxInitialized = true;

    result = NVSDK_NGX_D3D11_GetCapabilityParameters(&g_params);
    if (NVSDK_NGX_FAILED(result) || !g_params)
    {
        status = StatusFailed;
        why = ResultText("NVSDK_NGX_D3D11_GetCapabilityParameters", result);
        ReleaseNgx();
        return false;
    }

    int available = 0;
    result = g_params->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &available);
    if (NVSDK_NGX_FAILED(result) || !available)
    {
        int needsDriver = 0;
        unsigned int major = 0, minor = 0;
        g_params->Get(NVSDK_NGX_Parameter_SuperSampling_NeedsUpdatedDriver, &needsDriver);
        g_params->Get(NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMajor, &major);
        g_params->Get(NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMinor, &minor);
        status = StatusUnavailable;
        why = needsDriver ? "DLSS Super Resolution needs a newer NVIDIA driver (at least " + std::to_string(major) + "." + std::to_string(minor) + ")"
                          : "DLSS Super Resolution is not available on this GPU";
        ReleaseNgx();
        return false;
    }
    return true;
}

void Choose(int backend, const std::string &why, bool fellBack)
{
    std::lock_guard<std::mutex> lock(g_choiceLock);
    g_choice = why;
    g_fellBack = fellBack;
    g_backend = backend;
}

// The upscaler: the player's choice when this PC can run it, else the automatic one (DLSS on an NVIDIA card that has it, FSR on
// every other card, and on an NVIDIA card without DLSS). The card is told by its PCI vendor ID, what DLSS and FSR can run by their
// own SDKs (NGX's capability parameters, the FSR SDK's versions for this device).
void Initialize(const InitParams *init)
{
    if (!init || init->size != sizeof(InitParams) || init->version != kAbiVersion)
        return Fail(StatusFailed, "Initialize: the plugin and the native bridge disagree on the parameter layout");
    if (g_ngxInitialized || g_backend != MethodAuto) return;
    auto *texture = static_cast<ID3D11Resource *>(init->anyTexture);
    if (!texture) return Fail(StatusFailed, "Initialize: no texture to find the D3D11 device from");
    texture->GetDevice(&g_device);
    g_device->GetImmediateContext(&g_context);

    UINT vendor = 0;
    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC adapterDesc{};
    if (SUCCEEDED(g_device->QueryInterface(IID_PPV_ARGS(&dxgi))) && SUCCEEDED(dxgi->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&adapterDesc)))
        vendor = adapterDesc.VendorId;
    char card[64];
    std::snprintf(card, sizeof card, "%s graphics card (vendor 0x%04X)", vendor == 0x10DE ? "NVIDIA" : vendor == 0x1002 ? "AMD" : vendor == 0x8086 ? "Intel" : "other", vendor);
    const std::string cardText = card;

    int method = init->method == MethodDlss || init->method == MethodFsr ? init->method : MethodAuto;
    bool fellBack = false;
    std::string dlssWhy, fsrWhy;
    int dlssStatus = StatusUnavailable;
    bool triedDlss = false, triedFsr = false;
    auto tryDlss = [&] {
        triedDlss = true;
        if (!StartDlss(init, dlssStatus, dlssWhy)) return false;
        g_status = StatusReady;
        return true;
    };
    auto tryFsr = [&] {
        triedFsr = true;
        std::string versions;
        if (!fsr::Start(g_device, g_context, init->libraryFolder, &Message, fsrWhy, versions)) return false;
        g_status = StatusReady;
        g_fsrVersions = versions;
        return true;
    };

    if (method == MethodDlss)
    {
        if (tryDlss()) return Choose(MethodDlss, "DLSS, as chosen, on the " + cardText, false);
        fellBack = true;
    }
    if (method == MethodFsr)
    {
        if (tryFsr()) return Choose(MethodFsr, "FSR, as chosen, on the " + cardText + "; the FSR SDK offers " + g_fsrVersions, false);
        fellBack = true;
    }
    std::string chosen = method == MethodDlss ? "DLSS was chosen but cannot run here (" + dlssWhy + "); automatic choice: "
                         : method == MethodFsr ? "FSR was chosen but cannot run here (" + fsrWhy + "); automatic choice: " : "";
    if (vendor == 0x10DE && !triedDlss && tryDlss()) return Choose(MethodDlss, chosen + "DLSS on the " + cardText, fellBack);
    if (!triedFsr && tryFsr())
        return Choose(MethodFsr, chosen + "FSR on the " + cardText + (triedDlss ? " without DLSS (" + dlssWhy + ")" : "") + "; the FSR SDK offers " + g_fsrVersions, fellBack);
    Choose(MethodAuto, chosen + "no upscaler on the " + cardText, fellBack);
    std::string reasons = (triedDlss ? "DLSS: " + dlssWhy : std::string()) + (triedDlss && triedFsr ? "; " : "") + (triedFsr ? "FSR: " + fsrWhy : std::string());
    Fail(triedDlss && dlssStatus == StatusFailed && !triedFsr ? StatusFailed : StatusUnavailable, "No upscaler can run here (" + reasons + ")");
}

void Evaluate(const FrameParams *frame)
{
    int status = g_status;
    if (status != StatusReady && status != StatusRunning) return;
    if (!frame || frame->size != sizeof(FrameParams) || frame->version != kAbiVersion)
        return Fail(StatusFailed, "Evaluate: the plugin and the native bridge disagree on the parameter layout");
    if (!frame->color || !frame->depth || !frame->motion || !frame->output) return Fail(StatusFailed, "Evaluate: a texture is missing");
    if (g_backend == MethodFsr)
    {
        std::string why;
        if (!fsr::Evaluate(*frame, why))
        {
            fsr::Shutdown();
            g_backend = MethodAuto;
            return Fail(StatusFailed, why);
        }
        g_status = StatusRunning;
        g_evaluations++;
        return;
    }

    FeatureKey key;
    key.renderWidth = frame->renderWidth;
    key.renderHeight = frame->renderHeight;
    key.outputWidth = frame->outputWidth;
    key.outputHeight = frame->outputHeight;
    ID3D11Resource *exposure = frame->exposure ? ExposureOne() : nullptr;
    key.flags = (exposure ? 0 : NVSDK_NGX_DLSS_Feature_Flags_AutoExposure) | (frame->hdr ? NVSDK_NGX_DLSS_Feature_Flags_IsHDR : 0) |
                (frame->depthInverted ? NVSDK_NGX_DLSS_Feature_Flags_DepthInverted : 0) |
                (frame->renderWidth < frame->outputWidth ? NVSDK_NGX_DLSS_Feature_Flags_MVLowRes : 0);
    key.quality = frame->quality;
    key.preset = frame->preset;

    bool created = false;
    if (!g_feature || !(key == g_featureKey))
    {
        ReleaseFeature();
        NVSDK_NGX_DLSS_Create_Params create{};
        create.Feature.InWidth = key.renderWidth;
        create.Feature.InHeight = key.renderHeight;
        create.Feature.InTargetWidth = key.outputWidth;
        create.Feature.InTargetHeight = key.outputHeight;
        create.Feature.InPerfQualityValue = static_cast<NVSDK_NGX_PerfQuality_Value>(key.quality);
        create.InFeatureCreateFlags = key.flags;
        // DLSS 4.5's second-generation transformer on every RTX card: preset M for DLAA to Performance, preset L for Ultra Performance,
        // unless the player picked one preset for every mode. Left to its default, 310.9.16 picks the first-generation K for DLAA,
        // Quality and Balanced.
        const unsigned presetM = key.preset ? (unsigned)key.preset : NVSDK_NGX_DLSS_Hint_Render_Preset_M,
                       presetL = key.preset ? (unsigned)key.preset : NVSDK_NGX_DLSS_Hint_Render_Preset_L;
        g_params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA, presetM);
        g_params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality, presetM);
        g_params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced, presetM);
        g_params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Performance, presetM);
        g_params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraPerformance, presetL);
        NVSDK_NGX_Result result = NGX_D3D11_CREATE_DLSS_EXT(g_context, &g_feature, g_params, &create);
        if (NVSDK_NGX_FAILED(result) || !g_feature) return Fail(StatusFailed, ResultText("Creating the DLSS feature", result));
        std::wstring foreign;
        if (!OnlyPackagedLibraryLoaded(foreign))
        {
            ReleaseFeature();
            return Fail(StatusForeignLibrary, "A DLSS library other than the packaged one was loaded (" + Narrow(foreign) + "); DLSS stays off");
        }
        g_featureKey = key;
        created = true;
        g_status = StatusRunning;
    }

    NVSDK_NGX_D3D11_DLSS_Eval_Params eval{};
    eval.Feature.pInColor = static_cast<ID3D11Resource *>(frame->color);
    eval.Feature.pInOutput = static_cast<ID3D11Resource *>(frame->output);
    eval.pInDepth = static_cast<ID3D11Resource *>(frame->depth);
    eval.pInMotionVectors = static_cast<ID3D11Resource *>(frame->motion);
    eval.InJitterOffsetX = frame->jitterX;
    eval.InJitterOffsetY = frame->jitterY;
    eval.InRenderSubrectDimensions.Width = frame->renderWidth;
    eval.InRenderSubrectDimensions.Height = frame->renderHeight;
    eval.InReset = frame->reset || created ? 1 : 0;
    eval.InMVScaleX = frame->motionScaleX;
    eval.InMVScaleY = frame->motionScaleY;
    if (exposure)
    {
        eval.pInExposureTexture = exposure;
        eval.InPreExposure = 1.0f;
    }
    NVSDK_NGX_Result result = NGX_D3D11_EVALUATE_DLSS_EXT(g_context, g_feature, g_params, &eval);
    if (NVSDK_NGX_FAILED(result))
    {
        ReleaseFeature();
        return Fail(StatusFailed, ResultText("Evaluating DLSS", result));
    }
    g_evaluations++;
}

} // namespace

// Unity's render-event-with-data signature: void (__stdcall*)(int, void*). On x64 the calling convention is the single native one.
static void __stdcall OnRenderEvent(int eventId, void *data)
{
    switch (eventId)
    {
    case EventInitialize: Initialize(static_cast<const InitParams *>(data)); break;
    case EventEvaluate: Evaluate(static_cast<const FrameParams *>(data)); break;
    case EventShutdown: Shutdown(); break;
    case EventFrameGeneration: fg::Submit(static_cast<const fg::FrameParams *>(data)); break;
    default: Message("Unknown render event " + std::to_string(eventId)); break;
    }
}

// Unity's plain render-event signature: void (__stdcall*)(int). GL.IssuePluginEvent queues it for the render thread on its own, without a
// command buffer to execute on the main thread; the event id is the Unity frame number whose render submission starts.
static void __stdcall OnRenderSubmitStart(int frame) { fg::RenderSubmitStart(frame); }

extern "C"
{
__declspec(dllexport) uint32_t ForgeFrameGen_AbiVersion() { return kAbiVersion; }
__declspec(dllexport) void *ForgeFrameGen_RenderEventFunc() { return reinterpret_cast<void *>(&OnRenderEvent); }
__declspec(dllexport) void *ForgeFrameGen_RenderSubmitStartFunc() { return reinterpret_cast<void *>(&OnRenderSubmitStart); }
__declspec(dllexport) int ForgeFrameGen_Status() { return g_status; }
// Successful DLSS evaluations since start: the readback that shows the render-thread path really runs.
__declspec(dllexport) long long ForgeFrameGen_Evaluations() { return g_evaluations; }

// Copies the oldest pending message into buffer (UTF-8, NUL-terminated) and returns its length, or -1 when there is none.
__declspec(dllexport) int ForgeFrameGen_PopMessage(char *buffer, int capacity)
{
    std::lock_guard<std::mutex> lock(g_messagesLock);
    if (g_messages.empty() || capacity <= 0) return -1;
    std::string text = std::move(g_messages.front());
    g_messages.pop_front();
    int length = (int)std::min<size_t>(text.size(), (size_t)capacity - 1);
    std::memcpy(buffer, text.data(), length);
    buffer[length] = '\0';
    return length;
}

// The upscaler that runs (Method: 0 none, 1 DLSS, 2 FSR); why it was chosen into buffer (UTF-8, NUL-terminated), with the FSR
// version once FSR runs; fellBack 1 when the player's choice could not run here and the automatic choice was taken.
__declspec(dllexport) int ForgeFrameGen_Upscaler(char *buffer, int capacity, int *fellBack)
{
    std::lock_guard<std::mutex> lock(g_choiceLock);
    if (fellBack) *fellBack = g_fellBack ? 1 : 0;
    if (buffer && capacity > 0)
    {
        std::string running = g_backend == MethodFsr ? fsr::Running() : std::string();
        std::string text = running.empty() ? g_choice : g_choice + "; running " + running;
        int length = (int)std::min<size_t>(text.size(), (size_t)capacity - 1);
        std::memcpy(buffer, text.data(), length);
        buffer[length] = '\0';
    }
    return g_backend;
}

// Render size for an output size and quality, from NGX or FSR; 0 when no upscaler is ready.
__declspec(dllexport) int ForgeFrameGen_OptimalRenderSize(uint32_t outputWidth, uint32_t outputHeight, int quality, uint32_t *renderWidth, uint32_t *renderHeight)
{
    if (g_status != StatusReady && g_status != StatusRunning) return 0;
    if (g_backend == MethodFsr) return fsr::RenderSize(outputWidth, outputHeight, quality, renderWidth, renderHeight) ? 1 : 0;
    if (!g_params) return 0;
    unsigned int maxW = 0, maxH = 0, minW = 0, minH = 0;
    float sharpness = 0;
    NVSDK_NGX_Result result = NGX_DLSS_GET_OPTIMAL_SETTINGS(g_params, outputWidth, outputHeight, static_cast<NVSDK_NGX_PerfQuality_Value>(quality),
                                                            renderWidth, renderHeight, &maxW, &maxH, &minW, &minH, &sharpness);
    return NVSDK_NGX_FAILED(result) ? 0 : 1;
}
}

// ForgeFrameGen FSR super resolution. See fsr_upscale.h.
#include "fsr_upscale.h"
#include "bridge_common.h"
#include <dxgi1_4.h>
#include <array>
#include <cstdio>
#include <vector>

#include "ffx_runtime.h"
#include "dx12/ffx_api_dx12.h"
#include "ffx_upscale.h"

using Microsoft::WRL::ComPtr;

namespace fsr
{
namespace
{
Sink g_sink = nullptr;
bool g_loaded = false;

ID3D11DeviceContext *g_context11 = nullptr; // Unity's immediate context, owned by Unity
ComPtr<ID3D11Device> g_device11;
ComPtr<ID3D11DeviceContext4> g_context4;
ComPtr<ID3D11Fence> g_fence11;
ComPtr<ID3D12Device> g_device12;
ComPtr<ID3D12CommandQueue> g_queue;
ComPtr<ID3D12Fence> g_fence12;
ComPtr<ID3D12GraphicsCommandList> g_list;
HANDLE g_event = nullptr;
UINT64 g_value = 0;

// The D3D12 queue may still run the last two frames' FSR when the next one is recorded: an allocator is reset only once its frame is done.
struct Recorder
{
    ComPtr<ID3D12CommandAllocator> allocator;
    UINT64 done = 0;
};
std::array<Recorder, 3> g_recorders;
unsigned g_nextRecorder = 0;

Shared g_color, g_depth, g_motion, g_output;

struct ContextKey
{
    uint32_t renderWidth = 0, renderHeight = 0, outputWidth = 0, outputHeight = 0, flags = 0;
    bool operator==(const ContextKey &other) const
    {
        return renderWidth == other.renderWidth && renderHeight == other.renderHeight && outputWidth == other.outputWidth && outputHeight == other.outputHeight &&
               flags == other.flags;
    }
} g_key;
ffxContext g_upscaler = nullptr;
std::string g_running;
LARGE_INTEGER g_lastFrame{}, g_frequency{};

std::string Narrow(const wchar_t *text)
{
    int length = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
    std::string result(length > 0 ? length - 1 : 0, '\0');
    if (length > 1) WideCharToMultiByte(CP_UTF8, 0, text, -1, result.data(), length, nullptr, nullptr);
    return result;
}

void OnMessage(uint32_t type, const wchar_t *message)
{
    if (type == FFX_API_MESSAGE_TYPE_ERROR && g_sink && message) g_sink("FSR: " + Narrow(message));
}

std::string Code(const char *what, long long code)
{
    char text[160];
    std::snprintf(text, sizeof text, "%s failed (0x%08llX)", what, (unsigned long long)code);
    return text;
}

// The typed format of a texture Unity made typeless: the shared copy must be typed for FSR's views, and CopyResource takes any two
// formats of one typeless group.
DXGI_FORMAT Typed(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case DXGI_FORMAT_R32G32B32A32_TYPELESS: return DXGI_FORMAT_R32G32B32A32_FLOAT;
    case DXGI_FORMAT_R32_TYPELESS: return DXGI_FORMAT_R32_FLOAT;
    case DXGI_FORMAT_R16G16_TYPELESS: return DXGI_FORMAT_R16G16_FLOAT;
    case DXGI_FORMAT_R32G32_TYPELESS: return DXGI_FORMAT_R32G32_FLOAT;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
    default: return format;
    }
}

// Waits on the CPU until the D3D12 queue has run up to value (only when a recorder is reused before its frame is done, or at shutdown).
void WaitFor(UINT64 value)
{
    if (g_fence12->GetCompletedValue() >= value) return;
    g_fence12->SetEventOnCompletion(value, g_event);
    WaitForSingleObject(g_event, 5000);
}

void DestroyUpscaler()
{
    if (g_upscaler)
    {
        WaitFor(g_value);
        ffxrt::Api().DestroyContext(&g_upscaler, nullptr);
        g_upscaler = nullptr;
    }
    g_key = ContextKey{};
    g_running.clear();
}

uint32_t QualityMode(int ngxQuality)
{
    switch (ngxQuality) // NVSDK_NGX_PerfQuality_Value: MaxPerf 0, Balanced 1, MaxQuality 2, UltraPerformance 3, DLAA 5
    {
    case 2: return FFX_UPSCALE_QUALITY_MODE_QUALITY;
    case 1: return FFX_UPSCALE_QUALITY_MODE_BALANCED;
    case 0: return FFX_UPSCALE_QUALITY_MODE_PERFORMANCE;
    case 3: return FFX_UPSCALE_QUALITY_MODE_ULTRA_PERFORMANCE;
    default: return FFX_UPSCALE_QUALITY_MODE_NATIVEAA;
    }
}

void Barrier(ID3D12Resource *resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after, D3D12_RESOURCE_BARRIER &barrier)
{
    barrier = {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = resource;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = before;
    barrier.Transition.StateAfter = after;
}

void Transition(bool toFsr)
{
    D3D12_RESOURCE_STATES common = D3D12_RESOURCE_STATE_COMMON, read = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, write = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
    D3D12_RESOURCE_BARRIER barriers[4];
    Barrier(g_color.texture12.Get(), toFsr ? common : read, toFsr ? read : common, barriers[0]);
    Barrier(g_depth.texture12.Get(), toFsr ? common : read, toFsr ? read : common, barriers[1]);
    Barrier(g_motion.texture12.Get(), toFsr ? common : read, toFsr ? read : common, barriers[2]);
    Barrier(g_output.texture12.Get(), toFsr ? common : write, toFsr ? write : common, barriers[3]);
    g_list->ResourceBarrier(4, barriers);
}

bool CreateUpscaler(const ContextKey &key, std::string &why)
{
    ffxCreateBackendDX12Desc backend{};
    backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backend.device = g_device12.Get();
    ffxCreateContextDescUpscaleVersion version{};
    version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION;
    version.header.pNext = &backend.header;
    version.version = FFX_UPSCALER_VERSION;
    ffxCreateContextDescUpscale create{};
    create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    create.header.pNext = &version.header;
    create.flags = key.flags;
    create.maxRenderSize = {key.renderWidth, key.renderHeight};
    create.maxUpscaleSize = {key.outputWidth, key.outputHeight};
    create.fpMessage = &OnMessage;
    ffxReturnCode_t result = ffxrt::Api().CreateContext(&g_upscaler, &create.header, nullptr);
    if (result != FFX_API_RETURN_OK || !g_upscaler)
    {
        g_upscaler = nullptr;
        why = Code("Creating the FSR upscaler", result);
        return false;
    }
    g_key = key;
    ffxQueryGetProviderVersion provider{};
    provider.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
    g_running = ffxrt::Api().Query(&g_upscaler, &provider.header) == FFX_API_RETURN_OK && provider.versionName ? provider.versionName : "FSR (version not reported)";
    return true;
}
} // namespace

bool Start(ID3D11Device *device, ID3D11DeviceContext *context, const wchar_t *folder, Sink sink, std::string &why, std::string &versions)
{
    g_sink = sink;
    if (!g_loaded && !(g_loaded = ffxrt::Load(folder, ffxrt::kUpscaler, why))) return false;

    g_device11 = device;
    g_context11 = context;
    ComPtr<ID3D11Device5> device5;
    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&device5))) || FAILED(context->QueryInterface(IID_PPV_ARGS(&g_context4))))
    {
        why = "the D3D11 device has no shared fences (Windows 10 or later is needed)";
        return false;
    }
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgi))) || FAILED(dxgi->GetAdapter(&adapter)))
    {
        why = "the D3D11 device's adapter could not be read";
        return false;
    }
    // On one adapter D3D12 hands out one device: with the present bridge running this is its device.
    HRESULT result = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&g_device12));
    if (FAILED(result))
    {
        why = Code("Making a D3D12 device on the game's adapter", result);
        return false;
    }

    uint64_t count = 0;
    ffxQueryDescGetVersions query{};
    query.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
    query.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    query.device = g_device12.Get();
    query.outputCount = &count;
    if (ffxrt::Api().Query(nullptr, &query.header) != FFX_API_RETURN_OK || count == 0)
    {
        why = "the AMD FSR SDK offers no upscaler for this graphics card";
        Shutdown();
        return false;
    }
    std::vector<uint64_t> ids(count);
    std::vector<const char *> names(count);
    query.versionIds = ids.data();
    query.versionNames = names.data();
    ffxrt::Api().Query(nullptr, &query.header);
    versions.clear();
    for (uint64_t i = 0; i < count; i++) versions += (i ? ", " : "") + std::string(names[i] ? names[i] : "?");

    try
    {
        D3D12_COMMAND_QUEUE_DESC desc{};
        desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        if (FAILED(g_device12->CreateCommandQueue(&desc, IID_PPV_ARGS(&g_queue)))) throw std::runtime_error("FSR command queue");
        for (auto &recorder : g_recorders)
            if (FAILED(g_device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&recorder.allocator)))) throw std::runtime_error("FSR command allocator");
        if (FAILED(g_device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_recorders[0].allocator.Get(), nullptr, IID_PPV_ARGS(&g_list))))
            throw std::runtime_error("FSR command list");
        g_list->Close();
        if (FAILED(g_device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&g_fence12)))) throw std::runtime_error("FSR shared fence");
        HANDLE handle = nullptr;
        if (FAILED(g_device12->CreateSharedHandle(g_fence12.Get(), nullptr, GENERIC_ALL, nullptr, &handle))) throw std::runtime_error("FSR shared fence handle");
        HRESULT opened = device5->OpenSharedFence(handle, IID_PPV_ARGS(&g_fence11));
        CloseHandle(handle);
        if (FAILED(opened)) throw std::runtime_error("opening the FSR fence on D3D11");
        g_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!g_event) throw std::runtime_error("FSR fence event");
    }
    catch (const std::exception &error)
    {
        why = std::string(error.what()) + " could not be made";
        Shutdown();
        return false;
    }
    QueryPerformanceFrequency(&g_frequency);
    g_lastFrame.QuadPart = 0;
    return true;
}

bool RenderSize(uint32_t outputWidth, uint32_t outputHeight, int quality, uint32_t *renderWidth, uint32_t *renderHeight)
{
    if (!g_device12 || !g_loaded) return false;
    ffxCreateBackendDX12Desc backend{};
    backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backend.device = g_device12.Get();
    ffxQueryDescUpscaleGetRenderResolutionFromQualityMode query{};
    query.header.type = FFX_API_QUERY_DESC_TYPE_UPSCALE_GETRENDERRESOLUTIONFROMQUALITYMODE;
    query.header.pNext = &backend.header;
    query.displayWidth = outputWidth;
    query.displayHeight = outputHeight;
    query.qualityMode = QualityMode(quality);
    query.pOutRenderWidth = renderWidth;
    query.pOutRenderHeight = renderHeight;
    return ffxrt::Api().Query(nullptr, &query.header) == FFX_API_RETURN_OK && *renderWidth > 0 && *renderHeight > 0;
}

bool Evaluate(const FrameParams &frame, std::string &why)
{
    if (!g_queue) return why = "FSR is not started", false;
    auto *color = static_cast<ID3D11Texture2D *>(frame.color), *depth = static_cast<ID3D11Texture2D *>(frame.depth),
         *motion = static_cast<ID3D11Texture2D *>(frame.motion), *output = static_cast<ID3D11Texture2D *>(frame.output);
    D3D11_TEXTURE2D_DESC colorDesc{}, depthDesc{}, motionDesc{}, outputDesc{};
    color->GetDesc(&colorDesc);
    depth->GetDesc(&depthDesc);
    motion->GetDesc(&motionDesc);
    output->GetDesc(&outputDesc);
    // A copy needs equal sizes: the plugin's targets are the render and output sizes it names.
    if (colorDesc.Width != frame.renderWidth || colorDesc.Height != frame.renderHeight || depthDesc.Width != frame.renderWidth || depthDesc.Height != frame.renderHeight ||
        motionDesc.Width != frame.renderWidth || motionDesc.Height != frame.renderHeight || outputDesc.Width != frame.outputWidth || outputDesc.Height != frame.outputHeight)
        return why = "the FSR inputs are not the render and output sizes the frame names", false;

    ContextKey key;
    key.renderWidth = frame.renderWidth;
    key.renderHeight = frame.renderHeight;
    key.outputWidth = frame.outputWidth;
    key.outputHeight = frame.outputHeight;
    key.flags = (frame.hdr ? FFX_UPSCALE_ENABLE_HIGH_DYNAMIC_RANGE : 0u) | (frame.depthInverted ? FFX_UPSCALE_ENABLE_DEPTH_INVERTED : 0u) |
                (frame.exposure ? 0u : FFX_UPSCALE_ENABLE_AUTO_EXPOSURE);
    bool created = false;
    try
    {
        EnsureShared(g_color, g_device11.Get(), g_device12.Get(), colorDesc.Width, colorDesc.Height, Typed(colorDesc.Format));
        EnsureShared(g_depth, g_device11.Get(), g_device12.Get(), depthDesc.Width, depthDesc.Height, Typed(depthDesc.Format));
        EnsureShared(g_motion, g_device11.Get(), g_device12.Get(), motionDesc.Width, motionDesc.Height, Typed(motionDesc.Format));
        EnsureShared(g_output, g_device11.Get(), g_device12.Get(), outputDesc.Width, outputDesc.Height, Typed(outputDesc.Format), D3D11_BIND_UNORDERED_ACCESS);
    }
    catch (const std::exception &error)
    {
        return why = std::string("FSR: ") + error.what() + " could not be made", false;
    }
    if (!g_upscaler || !(key == g_key))
    {
        DestroyUpscaler();
        if (!CreateUpscaler(key, why)) return false;
        created = true;
    }

    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    float delta = g_lastFrame.QuadPart ? (float)((now.QuadPart - g_lastFrame.QuadPart) * 1000.0 / g_frequency.QuadPart) : 16.6f;
    g_lastFrame = now;
    if (delta <= 0.0f || delta > 100.0f) delta = 16.6f;

    // D3D11: the inputs into the shared textures, then the queue may run.
    g_context11->CopyResource(g_color.texture11.Get(), color);
    g_context11->CopyResource(g_depth.texture11.Get(), depth);
    g_context11->CopyResource(g_motion.texture11.Get(), motion);
    if (FAILED(g_context4->Signal(g_fence11.Get(), ++g_value))) return why = "FSR: the D3D11 fence could not be signalled", false;
    if (FAILED(g_queue->Wait(g_fence12.Get(), g_value))) return why = "FSR: the D3D12 queue could not wait for the D3D11 frame", false;

    auto &recorder = g_recorders[g_nextRecorder];
    g_nextRecorder = (g_nextRecorder + 1) % (unsigned)g_recorders.size();
    WaitFor(recorder.done);
    if (FAILED(recorder.allocator->Reset()) || FAILED(g_list->Reset(recorder.allocator.Get(), nullptr))) return why = "FSR: the command list could not be reset", false;
    Transition(true);

    ffxDispatchDescUpscale dispatch{};
    dispatch.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
    dispatch.commandList = g_list.Get();
    dispatch.color = ffxApiGetResourceDX12(g_color.texture12.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
    dispatch.depth = ffxApiGetResourceDX12(g_depth.texture12.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
    dispatch.motionVectors = ffxApiGetResourceDX12(g_motion.texture12.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
    dispatch.output = ffxApiGetResourceDX12(g_output.texture12.Get(), FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
    // The same jitter and motion DLSS gets: render pixels, Y down, motion from the current pixel to its place in the previous frame.
    dispatch.jitterOffset = {frame.jitterX, frame.jitterY};
    dispatch.motionVectorScale = {frame.motionScaleX, frame.motionScaleY};
    dispatch.renderSize = {frame.renderWidth, frame.renderHeight};
    dispatch.upscaleSize = {frame.outputWidth, frame.outputHeight};
    dispatch.enableSharpening = false;
    dispatch.frameTimeDelta = delta;
    dispatch.preExposure = 1.0f;
    dispatch.reset = frame.reset || created;
    dispatch.cameraNear = frame.cameraNear;
    dispatch.cameraFar = frame.cameraFar;
    dispatch.cameraFovAngleVertical = frame.cameraFov;
    dispatch.viewSpaceToMetersFactor = 1.0f; // Unity units are metres
    ffxReturnCode_t result = ffxrt::Api().Dispatch(&g_upscaler, &dispatch.header);
    Transition(false);
    if (FAILED(g_list->Close())) return why = "FSR: the command list could not be closed", false;
    ID3D12CommandList *lists[] = {g_list.Get()};
    g_queue->ExecuteCommandLists(1, lists);
    if (FAILED(g_queue->Signal(g_fence12.Get(), ++g_value))) return why = "FSR: the D3D12 fence could not be signalled", false;
    recorder.done = g_value;
    // D3D11 waits on the GPU for FSR, then takes its output.
    if (FAILED(g_context4->Wait(g_fence11.Get(), g_value))) return why = "FSR: the D3D11 context could not wait for FSR", false;
    if (result != FFX_API_RETURN_OK) return why = Code("FSR upscaling", result), false;
    g_context11->CopyResource(output, g_output.texture11.Get());
    return true;
}

std::string Running() { return g_running; }

void Shutdown()
{
    if (g_queue && g_fence12 && g_event)
    {
        g_queue->Signal(g_fence12.Get(), ++g_value);
        WaitFor(g_value);
    }
    DestroyUpscaler();
    g_color = g_depth = g_motion = g_output = Shared{};
    g_list.Reset();
    for (auto &recorder : g_recorders) recorder = Recorder{};
    g_nextRecorder = 0;
    g_fence11.Reset();
    g_fence12.Reset();
    g_queue.Reset();
    g_device12.Reset();
    g_context4.Reset();
    g_device11.Reset();
    g_context11 = nullptr;
    if (g_event)
    {
        CloseHandle(g_event);
        g_event = nullptr;
    }
    g_value = 0;
}
} // namespace fsr

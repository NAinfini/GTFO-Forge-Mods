// ForgeFrameGen AMD FSR Frame Generation backend (Docs/forge-upscale/PLAN.md §3.3). See frame_generation_backend.h.
//
// The display swap chain is the AMD FSR SDK's frame generation swap chain, made on the game's window from the bridge's queue
// (ffxCreateContextDescFrameGenerationSwapChainForHwndDX12; it makes the real swap chain itself and presents it from its own queue and
// threads). The bridge copies each frame into that chain's back buffer as before. For a present with inputs this backend configures
// frame generation (ffxConfigureDescFrameGeneration, its frame ID one more each present) and records the preparation
// (ffxDispatchDescFrameGenerationPrepareV2: the slot's depth and motion, the camera's planes, field of view, position and axes, the
// jitter) on the bridge's queue before the Present; the swap chain then dispatches the interpolation itself through the callback
// given in the configuration and paces the generated and the rendered frame. The HUD-less colour goes with the configuration, so
// the interpolation keeps the HUD out of its motion; the HUD itself stays in the back buffer, so every rendered frame is the game's
// own picture. There is no Reflex or PCL here.
//
// The game's frame-latency waitable object stays the bridge's own semaphore: the SDK's chain has a waitable object of its own, which
// nothing here hands out or waits on.
#include "frame_generation_backend.h"
#include "ffx_runtime.h"
#include "dx12/ffx_api_dx12.h"
#include "ffx_framegeneration.h"
#include "dx12/ffx_api_framegeneration_dx12.h"
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cstring>
#include <mutex>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace fg
{
namespace
{
ComPtr<ID3D12Device> g_device;
ComPtr<ID3D12CommandQueue> g_queue;
IDXGISwapChain4 *g_proxy = nullptr; // the SDK's swap chain; the bridge holds it
ffxContext g_swapChain = nullptr, g_context = nullptr;
std::string g_versions, g_running;
bool g_on = false;
uint64_t g_frameId = 0;
LARGE_INTEGER g_lastFrame{}, g_frequency{};
UINT g_lastCount = 0;
long long g_presentsOn = 0, g_shownWhileOn = 0, g_counted = 0; // this backend's presents with it on, frames they showed, generated counted

// The preparation of a slot's present is recorded in that slot's list; the bridge reuses a slot only after its last present is done.
struct Recorder
{
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
};
std::array<Recorder, 4> g_recorders;

struct ContextKey
{
    UINT width = 0, height = 0, renderWidth = 0, renderHeight = 0;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    bool inverted = false;
    bool operator==(const ContextKey &other) const
    {
        return width == other.width && height == other.height && renderWidth == other.renderWidth && renderHeight == other.renderHeight && format == other.format &&
               inverted == other.inverted;
    }
} g_key;

std::string Code(const char *what, long long code)
{
    char text[160];
    std::snprintf(text, sizeof text, "%s failed (0x%08llX)", what, (unsigned long long)code);
    return text;
}

void Require(ffxReturnCode_t result, const char *what)
{
    if (result != FFX_API_RETURN_OK) throw std::runtime_error(Code(what, result));
}

// The swap chain dispatches the interpolation through this, on the bridge's thread, inside the Present.
ffxReturnCode_t Generate(ffxDispatchDescFrameGeneration *params, void *context)
{
    return ffxrt::Api().Dispatch(static_cast<ffxContext *>(context), &params->header);
}

// The offline check's view of what the SDK's swap chain shows (WatchShown): its present callback is set, so the swap chain hands every
// frame it shows, rendered or generated, to Shown, which does the copy into the output buffer the swap chain does itself without one and
// copies the middle row into a readback ring. Only the offline check turns this on.
constexpr int kWatched = 128;
struct Watch
{
    std::atomic<bool> on{false};
    std::mutex lock;
    ComPtr<ID3D12Resource> ring;
    UINT width = 0, pitch = 0;
    int count = 0;
    std::array<int, kWatched> generated{};
} g_watch;

D3D12_RESOURCE_STATES Dx12State(uint32_t state)
{
    switch (state)
    {
    case FFX_API_RESOURCE_STATE_UNORDERED_ACCESS: return D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
    case FFX_API_RESOURCE_STATE_COMPUTE_READ: return D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    case FFX_API_RESOURCE_STATE_PIXEL_READ: return D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
    case FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ: return D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
    case FFX_API_RESOURCE_STATE_COPY_SRC: return D3D12_RESOURCE_STATE_COPY_SOURCE;
    case FFX_API_RESOURCE_STATE_COPY_DEST: return D3D12_RESOURCE_STATE_COPY_DEST;
    case FFX_API_RESOURCE_STATE_GENERIC_READ: return D3D12_RESOURCE_STATE_GENERIC_READ;
    case FFX_API_RESOURCE_STATE_RENDER_TARGET: return D3D12_RESOURCE_STATE_RENDER_TARGET;
    default: return D3D12_RESOURCE_STATE_COMMON; // COMMON and PRESENT
    }
}

ffxReturnCode_t Shown(ffxCallbackDescFrameGenerationPresent *params, void *)
{
    auto *list = static_cast<ID3D12GraphicsCommandList *>(params->commandList);
    auto *source = static_cast<ID3D12Resource *>(params->currentBackBuffer.resource);
    auto *target = static_cast<ID3D12Resource *>(params->outputSwapChainBuffer.resource);
    D3D12_RESOURCE_BARRIER barriers[2]{};
    UINT count = 0;
    auto add = [&](ID3D12Resource *resource, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to) {
        if (from == to) return;
        auto &barrier = barriers[count++];
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = resource;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore = from;
        barrier.Transition.StateAfter = to;
    };
    const D3D12_RESOURCE_STATES sourceState = Dx12State(params->currentBackBuffer.state), targetState = Dx12State(params->outputSwapChainBuffer.state);
    add(source, sourceState, D3D12_RESOURCE_STATE_COPY_SOURCE);
    add(target, targetState, D3D12_RESOURCE_STATE_COPY_DEST);
    if (count) list->ResourceBarrier(count, barriers);
    list->CopyResource(target, source);
    {
        std::lock_guard<std::mutex> lock(g_watch.lock);
        auto desc = source->GetDesc();
        if (g_watch.ring && g_watch.count < kWatched && desc.Width == g_watch.width)
        {
            D3D12_TEXTURE_COPY_LOCATION from{}, to{};
            from.pResource = source;
            from.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            to.pResource = g_watch.ring.Get();
            to.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            to.PlacedFootprint.Offset = (UINT64)g_watch.count * g_watch.pitch;
            to.PlacedFootprint.Footprint = {desc.Format, g_watch.width, 1, 1, g_watch.pitch};
            const UINT row = desc.Height / 2;
            D3D12_BOX box{0, row, 0, g_watch.width, row + 1, 1};
            list->CopyTextureRegion(&to, 0, 0, 0, &from, &box);
            g_watch.generated[g_watch.count++] = params->isGeneratedFrame ? 1 : 0;
        }
    }
    count = 0;
    add(source, D3D12_RESOURCE_STATE_COPY_SOURCE, sourceState);
    add(target, D3D12_RESOURCE_STATE_COPY_DEST, targetState);
    if (count) list->ResourceBarrier(count, barriers);
    return FFX_API_RETURN_OK;
}

// Frame generation off on the swap chain (it waits for the presents that use its resources) and, release, its context gone.
void SwitchOff(bool release)
{
    if (g_context && g_on)
    {
        ffxConfigureDescFrameGeneration config{};
        config.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
        config.swapChain = g_proxy;
        config.frameGenerationEnabled = false;
        config.frameID = ++g_frameId;
        ffxReturnCode_t result = ffxrt::Api().Configure(&g_context, &config.header);
        g_on = false;
        if (result != FFX_API_RETURN_OK) BridgeLog(LogFailure, "Switching AMD FSR Frame Generation off failed (0x%08X).", (unsigned)result);
    }
    if (release && g_context)
    {
        ffxrt::Api().DestroyContext(&g_context, nullptr);
        g_context = nullptr;
        g_key = ContextKey{};
    }
}

void CreateContext(const ContextKey &key)
{
    ffxCreateBackendDX12Desc backend{};
    backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backend.device = g_device.Get();
    ffxCreateContextDescFrameGenerationVersion version{};
    version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_VERSION;
    version.header.pNext = &backend.header;
    version.version = FFX_FRAMEGENERATION_VERSION;
    // The HUD-less colour is drawn as RGBA8 whatever the back buffer's format.
    ffxCreateContextDescFrameGenerationHudless hudless{};
    hudless.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_HUDLESS;
    hudless.header.pNext = &version.header;
    hudless.hudlessBackBufferFormat = ffxApiGetSurfaceFormatDX12(DXGI_FORMAT_R8G8B8A8_UNORM);
    ffxCreateContextDescFrameGeneration create{};
    create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
    create.header.pNext = &hudless.header;
    create.flags = key.inverted ? FFX_FRAMEGENERATION_ENABLE_DEPTH_INVERTED : 0u;
    create.displaySize = {key.width, key.height};
    create.maxRenderSize = {key.renderWidth, key.renderHeight};
    create.backBufferFormat = ffxApiGetSurfaceFormatDX12(key.format);
    ffxReturnCode_t result = ffxrt::Api().CreateContext(&g_context, &create.header, nullptr);
    if (result != FFX_API_RETURN_OK || !g_context)
    {
        g_context = nullptr;
        Require(result != FFX_API_RETURN_OK ? result : FFX_API_RETURN_ERROR, "Creating the AMD FSR frame generation context");
    }
    g_key = key;
    ffxQueryGetProviderVersion provider{};
    provider.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
    std::string running = ffxrt::Api().Query(&g_context, &provider.header) == FFX_API_RETURN_OK && provider.versionName ? provider.versionName : "version not reported";
    if (running != g_running)
        BridgeLog(LogInfo, "AMD FSR Frame Generation context: %s, %ux%u from %ux%u inputs.", running.c_str(), key.width, key.height, key.renderWidth, key.renderHeight);
    g_running = running;
}

void Barriers(ID3D12GraphicsCommandList *list, const SlotInputs &inputs, bool toFsr)
{
    D3D12_RESOURCE_BARRIER barriers[2]{};
    ID3D12Resource *resources[2] = {inputs.depth.texture12.Get(), inputs.motion.texture12.Get()};
    for (int i = 0; i < 2; i++)
    {
        barriers[i].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barriers[i].Transition.pResource = resources[i];
        barriers[i].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barriers[i].Transition.StateBefore = toFsr ? D3D12_RESOURCE_STATE_COMMON : D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        barriers[i].Transition.StateAfter = toFsr ? D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE : D3D12_RESOURCE_STATE_COMMON;
    }
    list->ResourceBarrier(2, barriers);
}

class Fsr final : public Backend
{
public:
    bool Start(const wchar_t *folder, IDXGIAdapter *adapter, std::string &why) override
    {
        if (!ffxrt::Load(folder, ffxrt::kFrameGeneration, why)) return false;
        ComPtr<ID3D12Device> device;
        HRESULT made = D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device));
        if (FAILED(made))
        {
            why = Code("Making a D3D12 device on the game's adapter", made);
            return false;
        }
        // The SDK's stated requirement: typed UAV loads of R16G16B16A16_UNORM.
        D3D12_FEATURE_DATA_FORMAT_SUPPORT format{DXGI_FORMAT_R16G16B16A16_UNORM};
        if (FAILED(device->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT, &format, sizeof format)) || !(format.Support2 & D3D12_FORMAT_SUPPORT2_UAV_TYPED_LOAD))
        {
            why = "this graphics card has no typed UAV loads of R16G16B16A16_UNORM, which AMD FSR Frame Generation needs";
            return false;
        }
        uint64_t count = 0;
        ffxQueryDescGetVersions query{};
        query.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
        query.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
        query.device = device.Get();
        query.outputCount = &count;
        if (ffxrt::Api().Query(nullptr, &query.header) != FFX_API_RETURN_OK || count == 0)
        {
            why = "the AMD FSR SDK offers no frame generation for this graphics card";
            return false;
        }
        std::vector<uint64_t> ids(count);
        std::vector<const char *> names(count);
        query.versionIds = ids.data();
        query.versionNames = names.data();
        ffxrt::Api().Query(nullptr, &query.header);
        g_versions.clear();
        for (uint64_t i = 0; i < count; i++) g_versions += (i ? ", " : "") + std::string(names[i] ? names[i] : "?");
        QueryPerformanceFrequency(&g_frequency);
        BridgeLog(LogInfo, "Frame generation: AMD FSR SDK started (frame generation %s).", g_versions.c_str());
        return true;
    }

    void Bind(ID3D12Device *device) override { g_device = device; }

    void Upgrade(ID3D12Device *device, ID3D12Device **queueDevice, IDXGIFactory2 **factory) override
    {
        device->AddRef();
        *queueDevice = device;
        if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(factory)))) throw std::runtime_error("DXGI factory for the display");
    }

    bool CreateDisplay(ID3D12CommandQueue *queue, HWND window, const DXGI_SWAP_CHAIN_DESC1 *desc, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC *fullscreen,
                       IDXGIFactory2 *factory, IDXGISwapChain1 **chain) override
    {
        ffxCreateContextDescFrameGenerationSwapChainVersionDX12 version{};
        version.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_VERSION_DX12;
        version.version = FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION;
        DXGI_SWAP_CHAIN_DESC1 display = *desc;
        DXGI_SWAP_CHAIN_FULLSCREEN_DESC windowed{};
        if (fullscreen) windowed = *fullscreen;
        IDXGISwapChain4 *made = nullptr;
        ffxCreateContextDescFrameGenerationSwapChainForHwndDX12 create{};
        create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_FOR_HWND_DX12;
        create.header.pNext = &version.header;
        create.swapchain = &made;
        create.hwnd = window;
        create.desc = &display;
        create.fullscreenDesc = fullscreen ? &windowed : nullptr;
        create.dxgiFactory = factory;
        create.gameQueue = queue;
        ffxReturnCode_t result = ffxrt::Api().CreateContext(&g_swapChain, &create.header, nullptr);
        if (result != FFX_API_RETURN_OK || !made)
        {
            g_swapChain = nullptr;
            throw std::runtime_error(Code("Making the AMD FSR frame generation swap chain", result));
        }
        g_queue = queue;
        g_proxy = made;
        g_lastCount = 0; // a new chain counts its presents from its own start
        *chain = made; // the reference the SDK handed out goes to the bridge
        for (auto &recorder : g_recorders)
        {
            if (FAILED(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&recorder.allocator))) ||
                FAILED(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, recorder.allocator.Get(), nullptr, IID_PPV_ARGS(&recorder.list))))
                throw std::runtime_error("AMD FSR frame generation command lists");
            recorder.list->Close();
        }
        BridgeLog(LogInfo, "Present bridge: the display swap chain is AMD FSR's frame generation swap chain.");
        return true;
    }

    void ReleaseDisplay() override
    {
        SwitchOff(true);
        if (g_swapChain) ffxrt::Api().DestroyContext(&g_swapChain, nullptr);
        g_swapChain = nullptr;
        g_proxy = nullptr;
        for (auto &recorder : g_recorders) recorder = Recorder{};
        g_queue.Reset();
    }

    int Kind() const override { return 2; }
    bool On() const override { return g_on; }

    void Admit(const SlotInputs &inputs, int frame, unsigned slot, Skip &why, const char *&off, char *detail, size_t capacity) override
    {
        // Like DLSS's: the inputs must be the frame whose render submission started last.
        if (inputs.params.frame == frame) return;
        why = SkipOtherFrame, off = "the frame generation inputs belong to another frame than the one presented";
        std::snprintf(detail, capacity, "inputs of frame %d, presenting frame %d, back buffer %u, present %lld", inputs.params.frame, frame, slot, g_presents.load());
    }

    void Off(bool release) override { SwitchOff(release); }

    void Use(const SlotInputs &inputs, unsigned slot, const DXGI_SWAP_CHAIN_DESC1 &desc, UINT, ID3D12Resource *) override
    {
        if (!g_proxy || slot >= g_recorders.size()) throw std::runtime_error("AMD FSR Frame Generation has no swap chain");
        const auto &p = inputs.params;
        ContextKey key;
        key.width = desc.Width;
        key.height = desc.Height;
        key.renderWidth = p.renderWidth;
        key.renderHeight = p.renderHeight;
        key.format = desc.Format;
        key.inverted = p.depthInverted != 0;
        bool reset = p.reset || !g_on;
        if (!g_context || !(key == g_key))
        {
            SwitchOff(true);
            CreateContext(key);
            reset = true;
        }

        ffxConfigureDescFrameGeneration config{};
        config.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
        config.swapChain = g_proxy;
        config.frameGenerationCallback = &Generate;
        config.frameGenerationCallbackUserContext = &g_context;
        config.frameGenerationEnabled = true;
        config.allowAsyncWorkloads = false;
        if (g_watch.on)
        {
            std::lock_guard<std::mutex> lock(g_watch.lock);
            if (!g_watch.ring || g_watch.width != desc.Width)
            {
                g_watch.width = desc.Width;
                // Each row starts at a placement-aligned offset (512 bytes, a multiple of the 256-byte pitch alignment).
                g_watch.pitch = (desc.Width * 4 + D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT - 1) & ~(D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT - 1);
                D3D12_HEAP_PROPERTIES heap{};
                heap.Type = D3D12_HEAP_TYPE_READBACK;
                D3D12_RESOURCE_DESC buffer{};
                buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
                buffer.Width = (UINT64)g_watch.pitch * kWatched;
                buffer.Height = buffer.DepthOrArraySize = buffer.MipLevels = 1;
                buffer.SampleDesc.Count = 1;
                buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
                g_watch.ring.Reset();
                g_watch.count = 0;
                if (FAILED(g_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buffer, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&g_watch.ring))))
                    throw std::runtime_error("the watch readback buffer");
            }
            config.presentCallback = &Shown;
        }
        // Read by the interpolation on the bridge's queue inside this present, before the slot is used again.
        if (g_hudWanted) config.HUDLessColor = ffxApiGetResourceDX12(inputs.hudless.texture12.Get(), FFX_API_RESOURCE_STATE_COMMON);
        config.generationRect = {0, 0, (int32_t)desc.Width, (int32_t)desc.Height};
        config.frameID = ++g_frameId;
        g_on = true; // an Off is attempted even if this call fails half way
        Require(ffxrt::Api().Configure(&g_context, &config.header), "Configuring AMD FSR Frame Generation");
        // When it first runs and when the player's "HUD in generated frames" changes; each switch on and off is a reason change, which the
        // shared layer logs and counts.
        static int logged = -1;
        if (const int hud = g_hudWanted ? 1 : 0; std::exchange(logged, hud) != hud)
            BridgeLog(LogInfo, "AMD FSR Frame Generation on; the HUD-less colour %s (HUD in generated frames %s).",
                      hud ? "keeps the HUD out of the interpolation" : "is not given", hud ? "on" : "off");

        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        float delta = g_lastFrame.QuadPart ? (float)((now.QuadPart - g_lastFrame.QuadPart) * 1000.0 / g_frequency.QuadPart) : 16.6f;
        g_lastFrame = now;
        if (delta <= 0.0f || delta > 100.0f) delta = 16.6f;

        auto &recorder = g_recorders[slot];
        if (FAILED(recorder.allocator->Reset()) || FAILED(recorder.list->Reset(recorder.allocator.Get(), nullptr)))
            throw std::runtime_error("the AMD FSR frame generation command list could not be reset");
        Barriers(recorder.list.Get(), inputs, true);
        ffxDispatchDescFrameGenerationPrepareV2 prepare{};
        prepare.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_V2;
        prepare.frameID = g_frameId;
        prepare.commandList = recorder.list.Get();
        prepare.renderSize = {p.renderWidth, p.renderHeight};
        // The jitter DLSS Frame Generation gets (render pixels, swap-chain rows); the motion in render pixels from the current pixel to
        // its place in the previous frame (the scale DLSS gets is in screen units).
        prepare.jitterOffset = {p.jitterX, p.jitterY};
        prepare.motionVectorScale = {p.motionScaleX * (float)p.renderWidth, p.motionScaleY * (float)p.renderHeight};
        prepare.frameTimeDelta = delta;
        prepare.reset = reset;
        prepare.cameraNear = p.cameraNear;
        prepare.cameraFar = p.cameraFar;
        prepare.cameraFovAngleVertical = p.cameraFov;
        prepare.viewSpaceToMetersFactor = 1.0f; // Unity units are metres
        prepare.depth = ffxApiGetResourceDX12(inputs.depth.texture12.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
        prepare.motionVectors = ffxApiGetResourceDX12(inputs.motion.texture12.Get(), FFX_API_RESOURCE_STATE_COMPUTE_READ);
        std::copy(p.cameraPosition, p.cameraPosition + 3, prepare.cameraPosition);
        std::copy(p.cameraUp, p.cameraUp + 3, prepare.cameraUp);
        std::copy(p.cameraRight, p.cameraRight + 3, prepare.cameraRight);
        std::copy(p.cameraForward, p.cameraForward + 3, prepare.cameraForward);
        ffxReturnCode_t prepared = ffxrt::Api().Dispatch(&g_context, &prepare.header);
        Barriers(recorder.list.Get(), inputs, false);
        if (FAILED(recorder.list->Close())) throw std::runtime_error("the AMD FSR frame generation command list could not be closed");
        ID3D12CommandList *lists[] = {recorder.list.Get()};
        g_queue->ExecuteCommandLists(1, lists);
        Require(prepared, "Preparing AMD FSR Frame Generation");
    }

    // The real swap chain's present count (the SDK's chain forwards it) against the presents made with frame generation on: what is
    // over is generated frames. The SDK presents from its own thread, so a present's count may come one present later.
    void After() override
    {
        UINT count = 0;
        if (!g_proxy || FAILED(g_proxy->GetLastPresentCount(&count))) return;
        int shown = g_lastCount ? (int)(count - g_lastCount) : 1;
        g_lastCount = count;
        g_lastPresented = shown;
        if (!g_on) return;
        ++g_frames;
        ++g_presentsOn;
        g_shownWhileOn += shown;
        if (long long generated = g_shownWhileOn - g_presentsOn; generated > g_counted)
        {
            g_generated += generated - g_counted;
            g_counted = generated;
        }
    }
};
} // namespace

Backend &FsrBackend()
{
    static Fsr backend;
    return backend;
}

void WatchShown(bool on)
{
    std::lock_guard<std::mutex> lock(g_watch.lock);
    g_watch.on = on;
    g_watch.count = 0;
    if (!on) g_watch.ring.Reset();
}

int ShownFrames(unsigned char *rows, int capacity, int *generated, unsigned *width)
{
    // Every present handed to the SDK's swap chain is shown before the rows are read.
    if (g_swapChain)
    {
        ffxDispatchDescFrameGenerationSwapChainWaitForPresentsDX12 wait{};
        wait.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_WAIT_FOR_PRESENTS_DX12;
        ffxrt::Api().Dispatch(&g_swapChain, &wait.header);
    }
    std::lock_guard<std::mutex> lock(g_watch.lock);
    if (width) *width = g_watch.width;
    if (!g_watch.ring || !rows) return 0;
    int count = std::min(g_watch.count, capacity);
    void *mapped = nullptr;
    D3D12_RANGE range{0, (SIZE_T)g_watch.pitch * kWatched};
    if (FAILED(g_watch.ring->Map(0, &range, &mapped))) return 0;
    for (int i = 0; i < count; i++)
    {
        std::memcpy(rows + (size_t)i * g_watch.width * 4, static_cast<unsigned char *>(mapped) + (size_t)i * g_watch.pitch, (size_t)g_watch.width * 4);
        if (generated) generated[i] = g_watch.generated[i];
    }
    D3D12_RANGE written{0, 0};
    g_watch.ring->Unmap(0, &written);
    return count;
}
} // namespace fg

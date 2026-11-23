// ForgeFrameGen present bridge (Docs/forge-upscale/PLAN.md §3.3, stage 4a): Unity keeps rendering with D3D11 while the window is
// presented by a D3D12 swap chain on the same adapter, so that DLSS Frame Generation (D3D12 only) can later run on that chain.
//
// The preloader calls ForgeFrameGen_InstallGraphicsHooks before Unity creates its device: the import tables of the game and
// UnityPlayer.dll are patched for the D3D11 and DXGI entry points (and GetProcAddress, for the same names looked up dynamically),
// and every DXGI factory gets its swap-chain creation slots patched. When this PC can run frame generation, the swap chain Unity asks for
// is this file's Bridge: buffer 0 is a D3D11 texture; each Present copies it into a shared texture, signals a shared fence, and the
// D3D12 queue copies it into the real back buffer and presents. With Streamline running (frame_generation.cpp) the present queue and
// the display swap chain are made through its proxies, and each Present hands it the frame's inputs.
//
// Portions adapted from RiskOfRainUpscaling (https://github.com/xanderfrangos/RiskOfRainUpscaling, commit
// 2bea5e1e1122751264ff2512ca66d646112e24eb: native/Presentation/GraphicsHooks.cpp, native/FrameGeneration/FrameBridge.cpp).
// With AMD FSR Frame Generation instead (FSR picked, or a card DLSS Frame Generation cannot run on), the display swap chain is the AMD
// FSR SDK's frame generation swap chain, made on the plain device and a new factory; the rest of the bridge is the same. When the
// player picks the other one while the game runs, the bridge drains its queue, lets go of the display swap chain and makes it again
// for the other backend on the same window (Switch); the game keeps its swap chain, buffer 0 and waitable object.
// Copyright (c) 2026 Xander Frangos. MIT License; the full text ships as RiskOfRainUpscaling-LICENSE.txt.
#include "frame_generation.h"
#include "bridge_common.h"
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <map>
#include <mutex>
#include <set>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>

using Microsoft::WRL::ComPtr;

namespace
{
std::atomic<int> g_requested{0}, g_active{0}, g_creationFailures{0}, g_runtimeFailures{0}, g_fallbacks{0};
std::atomic<int> g_devices{0}, g_factories{0}, g_chains{0};
std::atomic<long long> g_presents{0};

// Where the render thread is in the bridge (PresentStage), for the preloader's line when no frame comes through for a while: a game that
// froze inside the bridge says where.
enum PresentStage : int
{
    StageIdle = 0,
    StageSlot = 1,      // waiting for a back buffer slot of the D3D12 queue
    StageInputs = 2,    // frame generation's inputs and options for this present
    StageDisplay = 3,   // inside the display swap chain's Present
    StageResize = 4,    // ResizeBuffers
    StageSwitch = 5,    // moving the display to another frame generation
    StageFallback = 6,  // handing the window back to the game's own D3D11 swap chain
};
std::atomic<int> g_stage{StageIdle};
struct StageScope
{
    int previous;
    explicit StageScope(int stage) : previous(g_stage.exchange(stage)) {}
    ~StageScope() { g_stage = previous; }
};

// Lines for the plugin's log (BridgeLogKind); the preloader runs before the plugin, so they wait here until it drains them. Info
// lines are developer logs: the plugin says whether those are on, and until it has they are kept (the lines of the start-up). With
// developer logs off an info line costs one test; at most 128 info lines wait, failures always get in.
std::mutex g_logLock;
std::deque<std::pair<int, std::string>> g_log;
std::atomic<bool> g_developerLog{true};

void Queue(int kind, const char *format, va_list args)
{
    if (kind == LogInfo && !g_developerLog) return;
    char buffer[768];
    std::vsnprintf(buffer, sizeof buffer, format, args);
    std::lock_guard<std::mutex> lock(g_logLock);
    if (kind != LogInfo || g_log.size() < 128) g_log.emplace_back(kind, buffer);
}

void Log(int kind, const char *format, ...)
{
    va_list args;
    va_start(args, format);
    Queue(kind, format, args);
    va_end(args);
}

// The folder this library was loaded from (the plugin folder, where the packaged Streamline files are).
std::wstring OwnFolder()
{
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&OwnFolder), &self);
    wchar_t path[MAX_PATH * 2]{};
    GetModuleFileNameW(self, path, MAX_PATH * 2);
    std::wstring folder = path;
    return folder.substr(0, folder.find_last_of(L"\\/"));
}

void Require(HRESULT result, const char *what)
{
    if (FAILED(result))
    {
        char text[160];
        std::snprintf(text, sizeof text, "%s failed: HRESULT 0x%08lX", what, (unsigned long)result);
        throw std::runtime_error(text);
    }
}

// While the bridge builds its own D3D12 swap chain through the game's factory, the factory hook passes the call through.
thread_local bool t_building = false;
struct BuildingScope
{
    bool previous = t_building;
    BuildingScope() { t_building = true; }
    ~BuildingScope() { t_building = previous; }
};

std::mutex g_patchLock;
std::map<void **, void *> g_originals;

// Writes a pointer-sized slot (import table entry or COM vtable entry); the replaced target is kept for forwarding.
bool PatchSlot(void **slot, void *hook)
{
    std::lock_guard<std::mutex> lock(g_patchLock);
    if (*slot == hook) return false;
    DWORD old = 0;
    if (!VirtualProtect(slot, sizeof(void *), PAGE_READWRITE, &old)) return false;
    g_originals[slot] = *slot;
    InterlockedExchangePointer(slot, hook);
    DWORD ignored = 0;
    VirtualProtect(slot, sizeof(void *), old, &ignored);
    return true;
}

template <class T> T Original(void *object, int index)
{
    std::lock_guard<std::mutex> lock(g_patchLock);
    auto table = *reinterpret_cast<void ***>(object);
    return reinterpret_cast<T>(g_originals.at(table + index));
}

// ---------------------------------------------------------------------------------------------------------------------------------
// The bridge swap chain.

std::atomic<HWND> g_window{nullptr};
class Bridge;
Bridge *g_bridge = nullptr; // the game's chain, for readback; owned by the game

bool WindowEligible(HWND window)
{
    if (!g_requested || t_building || !IsWindow(window)) return false;
    DWORD process = 0;
    GetWindowThreadProcessId(window, &process);
    if (process != GetCurrentProcessId() || (GetWindowLongPtrW(window, GWL_STYLE) & WS_CHILD)) return false;
    HWND selected = g_window;
    return !selected || selected == window || !IsWindow(selected);
}

DXGI_FORMAT Typeless(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_R8G8B8A8_UNORM: return DXGI_FORMAT_R8G8B8A8_TYPELESS;
    case DXGI_FORMAT_B8G8R8A8_UNORM: return DXGI_FORMAT_B8G8R8A8_TYPELESS;
    case DXGI_FORMAT_R10G10B10A2_UNORM: return DXGI_FORMAT_R10G10B10A2_TYPELESS;
    case DXGI_FORMAT_R16G16B16A16_FLOAT: return DXGI_FORMAT_R16G16B16A16_TYPELESS;
    default: return DXGI_FORMAT_UNKNOWN;
    }
}

bool IsFlip(DXGI_SWAP_EFFECT effect) { return effect == DXGI_SWAP_EFFECT_FLIP_DISCARD || effect == DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; }

class Bridge final : public IDXGISwapChain2
{
    struct Slot
    {
        ComPtr<ID3D11Texture2D> shared11;
        ComPtr<ID3D12Resource> shared12, backbuffer;
        ComPtr<ID3D12CommandAllocator> allocator;
        ComPtr<ID3D12GraphicsCommandList> list;
        UINT64 done = 0;
    };

    std::atomic<ULONG> _refs{1};
    std::recursive_mutex _lock;
    ComPtr<IDXGIFactory2> _factory;
    ComPtr<ID3D11Device> _device11;
    ComPtr<ID3D11Device5> _device5;
    ComPtr<ID3D11DeviceContext4> _context;
    ComPtr<ID3D12Device> _device12;
    ComPtr<ID3D12CommandQueue> _queue;
    ComPtr<IDXGISwapChain3> _display;
    ComPtr<ID3D12Fence> _produced12, _consumed12;
    ComPtr<ID3D11Fence> _produced11;
    HANDLE _event = nullptr;
    ComPtr<ID3D11Texture2D> _producer;
    std::array<Slot, 4> _slots;
    DXGI_SWAP_CHAIN_DESC1 _desc{};
    // What the display swap chain is made from again at a switch: the window, the description as asked for (after the last resize), the
    // game's fullscreen description and output; and the frame generation it runs (0 none, 1 DLSS, 2 AMD FSR).
    HWND _window = nullptr;
    DXGI_SWAP_CHAIN_DESC1 _displayDesc{};
    DXGI_SWAP_CHAIN_FULLSCREEN_DESC _fullscreen{};
    bool _hasFullscreen = false;
    ComPtr<IDXGIOutput> _output;
    int _kind = 0;
    UINT _applicationFlags = 0;
    UINT64 _serial = 0;
    UINT _lastIndex = 0;
    bool _hasImage = false, _failed = false;
    // Native: the bridge gave the window back to a D3D11 swap chain of the game's device (GoNative); _display is that chain, each present
    // copies buffer 0 into its back buffer, and nothing of D3D12 or frame generation runs any more.
    bool _native = false;
    ComPtr<ID3D11Texture2D> _nativeBuffer;
    // The frame-latency waitable object the game gets when it creates its chain with one: the bridge's own semaphore, never the display
    // chain's. Streamline owns that chain and its waitable object (its DLSS-G guide, 12.1), so the display chain is made without one; a
    // game waiting on Streamline's object waited seconds at a time in the offline check. As DXGI's, it gives a frame back when a present
    // is done, not when it is called: the retirer thread releases it once the present queue has finished that present's work (a present
    // that failed gives its frame back at once). A frame the game started then never runs more than the limit ahead of the GPU.
    // While AMD FSR Frame Generation is on the limit is at most kFsrLatency (AMD: the game should keep the CPU from running far ahead of
    // the GPU with the waitable object; FSR has no Reflex here): the game builds one frame while the GPU draws the one before.
    static constexpr LONG kFsrLatency = 2;
    HANDLE _latency = nullptr;
    LONG _latencyLimit = 1, _latencyApplied = 1, _latencyDebt = 0; // DXGI's default limit is 1; the bridge's own lower cap is paid by skipped releases
    std::mutex _latencyLock;
    std::atomic<bool> _waitableAsked{false};
    bool _fsrOn = false; // _latencyLock
    std::thread _retirer;
    std::mutex _retireLock;
    std::condition_variable _retireWake;
    std::deque<UINT64> _retiring;
    bool _stopRetiring = false;
    HANDLE _retireEvent = nullptr;

    void FrameRetired()
    {
        if (!_latency) return;
        std::lock_guard<std::mutex> lock(_latencyLock);
        if (_latencyDebt > 0) _latencyDebt--;
        else ReleaseSemaphore(_latency, 1, nullptr);
    }

    // The present queue's work up to value is done: one frame back to the game.
    void RetireAt(UINT64 value)
    {
        {
            std::lock_guard<std::mutex> lock(_retireLock);
            _retiring.push_back(value);
        }
        _retireWake.notify_one();
    }

    void Retire()
    {
        for (;;)
        {
            UINT64 value;
            {
                std::unique_lock<std::mutex> lock(_retireLock);
                _retireWake.wait(lock, [this] { return _stopRetiring || !_retiring.empty(); });
                if (_retiring.empty()) return;
                value = _retiring.front();
                _retiring.pop_front();
            }
            // A removed device completes everything (UINT64_MAX); a queue stuck for 5 seconds gives the frame back anyway.
            if (_consumed12->GetCompletedValue() < value && SUCCEEDED(_consumed12->SetEventOnCompletion(value, _retireEvent)))
                WaitForSingleObject(_retireEvent, 5000);
            FrameRetired();
        }
    }

    // The limit in force: the game's, at most kFsrLatency while AMD FSR Frame Generation is on. A change moves the semaphore by the
    // difference (a lower limit is taken back from the coming releases). _latencyLock held.
    void ApplyLatency(LONG target)
    {
        LONG change = target - _latencyApplied;
        if (!change) return;
        _latencyApplied = target;
        LONG paid = std::min(std::max(change, 0L), _latencyDebt);
        _latencyDebt += change < 0 ? -change : -paid;
        if (change - paid > 0) ReleaseSemaphore(_latency, change - paid, nullptr);
    }

    LONG LatencyFor(bool fsrOn) const { return fsrOn ? std::min(_latencyLimit, kFsrLatency) : _latencyLimit; }

    void FollowFrameGeneration()
    {
        if (!_latency) return;
        const bool fsrOn = fg::Kind() == 2 && fg::State(nullptr, 0, nullptr, nullptr, nullptr) == 2;
        std::lock_guard<std::mutex> lock(_latencyLock);
        _fsrOn = fsrOn;
        if (LatencyFor(fsrOn) == _latencyApplied) return;
        Log(LogInfo, "Present bridge: frames the game may start ahead of the GPU: %ld (AMD FSR Frame Generation %s, the game's maximum frame latency %ld).",
            LatencyFor(fsrOn), fsrOn ? "on" : "off", _latencyLimit);
        ApplyLatency(LatencyFor(fsrOn));
    }
    std::set<std::string> _unknownInterfaces;

    void Wait(UINT64 value)
    {
        if (!value || _consumed12->GetCompletedValue() >= value) return;
        Require(_consumed12->SetEventOnCompletion(value, _event), "Bridge completion event");
        ULONGLONG deadline = GetTickCount64() + 5000;
        for (;;)
        {
            UINT64 completed = _consumed12->GetCompletedValue();
            if (completed == UINT64_MAX) throw std::runtime_error("the D3D12 device was removed");
            if (completed >= value) return;
            ULONGLONG now = GetTickCount64();
            if (now >= deadline || WaitForSingleObject(_event, (DWORD)(deadline - now)) != WAIT_OBJECT_0)
                throw std::runtime_error("the D3D12 queue did not finish a frame within 5 seconds");
        }
    }

    void Drain()
    {
        UINT64 value = ++_serial;
        Require(_queue->Signal(_consumed12.Get(), value), "Bridge drain signal");
        Wait(value);
    }

    // The display swap chain's buffers and, with producer, buffer 0 at its size (a switch keeps buffer 0: the game holds it).
    void CreateBuffers(bool producer = true)
    {
        Require(_display->GetDesc1(&_desc), "Bridge display description");
        if (_desc.BufferCount < 2 || _desc.BufferCount > _slots.size()) throw std::runtime_error("unsupported back buffer count");
        D3D11_TEXTURE2D_DESC texture{};
        texture.Width = _desc.Width;
        texture.Height = _desc.Height;
        texture.MipLevels = texture.ArraySize = 1;
        texture.Format = Typeless(_desc.Format);
        texture.SampleDesc.Count = 1;
        texture.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        if (producer) Require(_device11->CreateTexture2D(&texture, nullptr, &_producer), "Bridge buffer 0");
        texture.Format = _desc.Format;
        texture.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
        for (UINT i = 0; i < _desc.BufferCount; i++)
        {
            auto &slot = _slots[i];
            Require(_device11->CreateTexture2D(&texture, nullptr, &slot.shared11), "Bridge shared texture");
            ComPtr<IDXGIResource1> resource;
            Require(slot.shared11.As(&resource), "Bridge shared texture interface");
            HANDLE handle = nullptr;
            Require(resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &handle), "Bridge shared handle");
            HRESULT opened = _device12->OpenSharedHandle(handle, IID_PPV_ARGS(&slot.shared12));
            CloseHandle(handle);
            Require(opened, "Opening the shared texture on D3D12");
            Require(_display->GetBuffer(i, IID_PPV_ARGS(&slot.backbuffer)), "D3D12 back buffer");
            Require(_device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&slot.allocator)), "Bridge allocator");
            Require(_device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, slot.allocator.Get(), nullptr, IID_PPV_ARGS(&slot.list)), "Bridge command list");
            Require(slot.list->Close(), "Bridge command list close");
            slot.done = 0;
        }
        _hasImage = false;
    }

    void ReleaseBuffers(bool producer = true)
    {
        for (auto &slot : _slots) slot = Slot{};
        if (producer) _producer.Reset();
    }

    // The present queue and the display swap chain for frame generation kind (0: none, the game's factory) on the game's window.
    void MakeDisplay(int kind)
    {
        _display.Reset();
        _queue.Reset();
        ComPtr<ID3D12Device> queueDevice = _device12;
        ComPtr<IDXGIFactory2> factory = _factory;
        if (kind) fg::Upgrade(kind, _device12.Get(), &queueDevice, &factory); // DLSS: Streamline's proxies of both
        D3D12_COMMAND_QUEUE_DESC queue{};
        queue.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Require(queueDevice->CreateCommandQueue(&queue, IID_PPV_ARGS(&_queue)), "D3D12 present queue");
        ComPtr<IDXGISwapChain1> chain;
        {
            // With AMD FSR the SDK makes the display chain (its frame generation swap chain, which makes the real one through the same factory).
            BuildingScope building;
            const DXGI_SWAP_CHAIN_FULLSCREEN_DESC *fullscreen = _hasFullscreen ? &_fullscreen : nullptr;
            if (!(kind && fg::CreateDisplay(kind, _queue.Get(), _window, &_displayDesc, fullscreen, factory.Get(), &chain)))
                Require(factory->CreateSwapChainForHwnd(_queue.Get(), _window, &_displayDesc, fullscreen, _output.Get(), &chain), "D3D12 swap chain for the game window");
        }
        Require(chain.As(&_display), "D3D12 swap chain interface");
        // The display chain comes from a factory of the backend's that nothing has told to leave Alt+Enter alone: DXGI itself would
        // switch the real chain to exclusive fullscreen behind the bridge, with frame generation still on.
        if (kind && FAILED(factory->MakeWindowAssociation(_window, DXGI_MWA_NO_ALT_ENTER)))
            Log(LogInfo, "Present bridge: DXGI's own Alt+Enter handling could not be switched off for the display swap chain.");
    }

    // The player picked the other frame generation (render thread, inside the game's Present, the bridge's lock held): the queue is
    // drained, the running backend lets go of the display swap chain, and the chain is made again on the same window for kind. The game's
    // swap chain, buffer 0 and waitable object stay as they are, so the game sees nothing but this present taking longer. When kind cannot
    // take over, that is logged once (framegen-frame-generation-switch-failed) and the backend that ran takes the display again.
    void Switch(int kind)
    {
        const int from = _kind;
        LARGE_INTEGER start{}, end{}, frequency{};
        QueryPerformanceCounter(&start);
        std::lock_guard<std::mutex> hold(fg::SwitchLock());
        fg::Suspend();
        Drain();
        fg::ReleaseInputs();
        ReleaseBuffers(false);
        fg::ReleaseDisplay();
        _display.Reset();
        _queue.Reset();
        try
        {
            fg::Leave();
            fg::Enter(kind);
            MakeDisplay(kind);
            CreateBuffers(false);
            _kind = kind;
        }
        catch (const std::exception &error)
        {
            ReleaseBuffers(false);
            fg::ReleaseDisplay();
            _display.Reset();
            _queue.Reset();
            try
            {
                fg::Leave();
            }
            catch (const std::exception &)
            {
                // Already reported below; the backend that ran takes over again either way.
            }
            fg::Broken(kind, error.what());
            fg::Enter(from);
            MakeDisplay(from);
            CreateBuffers(false);
        }
        _hasImage = false;
        QueryPerformanceCounter(&end);
        QueryPerformanceFrequency(&frequency);
        auto name = [](int kind) { return kind == 1 ? "DLSS" : kind == 2 ? "AMD FSR" : "none (plain swap chain)"; };
        Log(LogInfo, "Frame generation switched from %s to %s in %.1f ms (%ux%u, %u buffers).", name(from), name(_kind),
            (end.QuadPart - start.QuadPart) * 1000.0 / frequency.QuadPart, _desc.Width, _desc.Height, _desc.BufferCount);
    }

    // Flip-model Present unbinds the back buffer from the output merger; Unity may rely on that, so the bridge does the same.
    void UnbindProducer()
    {
        ID3D11RenderTargetView *targets[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT]{};
        ComPtr<ID3D11DepthStencilView> depth;
        _context->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, targets, &depth);
        bool changed = false;
        for (auto &target : targets)
        {
            if (!target) continue;
            ComPtr<ID3D11Resource> resource;
            target->GetResource(&resource);
            if (resource.Get() == _producer.Get())
            {
                target->Release();
                target = nullptr;
                changed = true;
            }
        }
        if (changed) _context->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, targets, depth.Get());
        for (auto *target : targets)
            if (target) target->Release();
    }

    // Buffer 0 of the game's own swap chain and the producer the game draws into at its size (kept when the size is the same: the game
    // holds it).
    void NativeBuffers()
    {
        Require(_display->GetDesc1(&_desc), "The game's own swap chain description");
        Require(_display->GetBuffer(0, IID_PPV_ARGS(&_nativeBuffer)), "The game's own back buffer");
        D3D11_TEXTURE2D_DESC have{};
        if (_producer) _producer->GetDesc(&have);
        if (_producer && have.Width == _desc.Width && have.Height == _desc.Height && have.Format == Typeless(_desc.Format)) return;
        D3D11_TEXTURE2D_DESC texture{};
        texture.Width = _desc.Width;
        texture.Height = _desc.Height;
        texture.MipLevels = texture.ArraySize = 1;
        texture.Format = Typeless(_desc.Format);
        texture.SampleDesc.Count = 1;
        texture.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        _producer.Reset();
        Require(_device11->CreateTexture2D(&texture, nullptr, &_producer), "Bridge buffer 0");
    }

    // The way back to the game's own rendering when the bridge cannot go on (a D3D12 call failed or timed out, the D3D12 device was
    // removed, the game asked for exclusive fullscreen): frame generation stops, the D3D12 display swap chain goes, and the window gets a
    // D3D11 swap chain of the game's own device and factory, made as the game asked for its own (without the waitable object: the game
    // keeps the bridge's). Written once as a failure line. False when even that chain cannot be made; the bridge then stops (Fail).
    bool GoNative(const char *why)
    {
        if (_native) return true;
        StageScope stage(StageFallback);
        {
            // As at a switch: no marker call of the main thread while the backend lets go (Streamline asks for none in parallel).
            std::lock_guard<std::mutex> hold(fg::SwitchLock());
            try
            {
                fg::Suspend();
                Drain();
            }
            catch (const std::exception &)
            {
                // A queue that does not finish or a removed device: the chain is let go of all the same.
            }
            fg::ReleaseInputs();
            ReleaseBuffers(false);
            try
            {
                fg::ReleaseDisplay();
            }
            catch (const std::exception &)
            {
                // The backend's display goes with its references below either way.
            }
            _display.Reset();
            _queue.Reset();
            fg::Fail(why);
        }
        try
        {
            DXGI_SWAP_CHAIN_DESC1 desc = _displayDesc;
            ComPtr<IDXGISwapChain1> chain;
            {
                BuildingScope building;
                Require(_factory->CreateSwapChainForHwnd(_device11.Get(), _window, &desc, _hasFullscreen ? &_fullscreen : nullptr, _output.Get(), &chain),
                        "The game's own D3D11 swap chain");
            }
            Require(chain.As(&_display), "The game's own swap chain interface");
            NativeBuffers();
        }
        catch (const std::exception &error)
        {
            _display.Reset();
            _nativeBuffer.Reset();
            Log(LogFailure, "The present bridge could not give the window back to the game's own swap chain either (%s).", error.what());
            return false;
        }
        _native = true;
        ++g_fallbacks;
        FollowFrameGeneration(); // frame generation is off: the game's own frame latency again
        Log(LogFailure,
            "The present bridge handed the window back to the game's own D3D11 swap chain (%s): the game renders as without Forge FrameGen's frame "
            "generation until it restarts.",
            why);
        return true;
    }

    // A present on the game's own swap chain (_native): buffer 0 copied into its back buffer.
    HRESULT NativePresent(UINT interval, UINT flags, const DXGI_PRESENT_PARAMETERS *parameters, bool present1)
    {
        StageScope stage(StageDisplay);
        _context->CopyResource(_nativeBuffer.Get(), _producer.Get());
        HRESULT result = present1 ? _display->Present1(interval, flags, parameters) : _display->Present(interval, flags);
        if (SUCCEEDED(result))
        {
            if (!(flags & DXGI_PRESENT_DO_NOT_SEQUENCE)) UnbindProducer();
            ++g_presents;
            _hasImage = true;
        }
        return result;
    }

    HRESULT Fail(const char *what, const std::exception &error)
    {
        _failed = true;
        ++g_runtimeFailures;
        Log(LogFailure, "The present bridge stopped (%s: %s); the game cannot present any more frames. Restart the game; if it happens again, remove Forge FrameGen.", what, error.what());
        return DXGI_ERROR_DEVICE_REMOVED;
    }

    HRESULT Show(UINT interval, UINT flags, const DXGI_PRESENT_PARAMETERS *parameters, bool present1)
    {
        if (flags & DXGI_PRESENT_TEST) return present1 ? _display->Present1(interval, flags, parameters) : _display->Present(interval, flags);
        // Every Present the game makes gives its waitable object one frame back, whatever happens below (a failure included, at once),
        // except the one refused as still drawing, which the game makes again; a present that went out gives it back when it is done.
        struct Retire
        {
            Bridge *bridge;
            bool retire = true;
            UINT64 at = 0;
            ~Retire()
            {
                if (!retire) return;
                if (at && bridge->_retirer.joinable()) bridge->RetireAt(at);
                else bridge->FrameRetired();
            }
        } retire{this};
        std::lock_guard<std::recursive_mutex> lock(_lock);
        if (_failed) return DXGI_ERROR_DEVICE_REMOVED;
        auto native = [&] {
            HRESULT result = NativePresent(interval, flags, parameters, present1);
            if (result == DXGI_ERROR_WAS_STILL_DRAWING) retire.retire = false;
            return result;
        };
        if (_native) return native();
        try
        {
            if (g_testFaults.fetch_and(~(unsigned)FaultPresent) & FaultPresent) throw std::runtime_error("a present failure the offline check asked for");
            // The player picked the other frame generation: the display moves to it before this present.
            // Without the plugin (it loads before the first present) the display parks on a plain chain, and comes back if it shows up.
            if (const int target = fg::Target(); target ? target != _kind : _kind && fg::Unclaimed())
            {
                StageScope stage(StageSwitch);
                Switch(target);
            }
            UINT index = _display->GetCurrentBackBufferIndex();
            auto &slot = _slots.at(index);
            if ((flags & DXGI_PRESENT_DO_NOT_WAIT) && _consumed12->GetCompletedValue() < slot.done)
            {
                retire.retire = false;
                return DXGI_ERROR_WAS_STILL_DRAWING;
            }
            {
                StageScope stage(StageSlot);
                Wait(slot.done);
            }
            StageScope stage(StageInputs);
            fg::BeforeSignal(_device11.Get(), _context.Get(), _device12.Get(), index, _desc);
            _context->CopyResource(slot.shared11.Get(), _producer.Get());
            Require(slot.allocator->Reset(), "Bridge allocator reset");
            Require(slot.list->Reset(slot.allocator.Get(), nullptr), "Bridge list reset");
            D3D12_RESOURCE_BARRIER barriers[2]{};
            for (auto &barrier : barriers)
            {
                barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
                barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            }
            barriers[0].Transition.pResource = slot.shared12.Get();
            barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
            barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
            barriers[1].Transition.pResource = slot.backbuffer.Get();
            barriers[1].Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
            barriers[1].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
            slot.list->ResourceBarrier(2, barriers);
            slot.list->CopyResource(slot.backbuffer.Get(), slot.shared12.Get());
            for (auto &barrier : barriers) std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter);
            slot.list->ResourceBarrier(2, barriers);
            Require(slot.list->Close(), "Bridge list close");

            // Separate producer and consumer fences: a later D3D11 signal must never complete an earlier D3D12 use of a slot.
            UINT64 value = ++_serial;
            Require(_context->Signal(_produced11.Get(), value), "D3D11 frame-finished signal");
            _context->Flush();
            Require(_queue->Wait(_produced12.Get(), value), "D3D12 wait for the D3D11 frame");
            ID3D12CommandList *lists[] = {slot.list.Get()};
            _queue->ExecuteCommandLists(1, lists);
            fg::Prepare(index, _desc, interval, slot.backbuffer.Get());
            fg::PresentMarker(true);
            g_stage = StageDisplay;
            HRESULT result = present1 ? _display->Present1(interval, flags, parameters) : _display->Present(interval, flags);
            g_stage = StageInputs;
            fg::PresentMarker(false);
            Require(_queue->Signal(_consumed12.Get(), value), "D3D12 slot-retired signal");
            retire.at = value;
            fg::After();
            FollowFrameGeneration();
            slot.done = value;
            _lastIndex = index;
            _hasImage = true;
            if (result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET || result == DXGI_ERROR_DEVICE_HUNG)
            {
                // The D3D12 device is gone while the game's D3D11 one may be fine: this frame goes out on the game's own swap chain.
                char why[128];
                std::snprintf(why, sizeof why, "the D3D12 swap chain's Present failed with HRESULT 0x%08lX", (unsigned long)result);
                if (GoNative(why)) return NativePresent(interval, flags, parameters, present1);
                _failed = true;
                ++g_runtimeFailures;
                Log(LogFailure, "The D3D12 swap chain's Present failed (HRESULT 0x%08lX); the present bridge stopped.", (unsigned long)result);
            }
            if (SUCCEEDED(result))
            {
                if (!(flags & DXGI_PRESENT_DO_NOT_SEQUENCE)) UnbindProducer();
                long long count = ++g_presents;
                if (count == 1)
                    Log(LogInfo, "Present bridge: first frame presented through D3D12 (sync interval %u, flags 0x%X, %s).", interval, flags,
                        present1 ? "Present1" : "Present");
            }
            return result;
        }
        catch (const std::exception &error)
        {
            char why[384];
            std::snprintf(why, sizeof why, "Present: %s", error.what());
            if (GoNative(why)) return native();
            return Fail("Present", error);
        }
    }

public:
    Bridge(IDXGIFactory2 *factory, ID3D11Device *device, HWND window, const DXGI_SWAP_CHAIN_DESC1 &requested,
           const DXGI_SWAP_CHAIN_FULLSCREEN_DESC *fullscreen, IDXGIOutput *output, IDXGIAdapter *adapter, bool generating)
        : _factory(factory), _device11(device), _window(window), _output(output)
    {
        Require(device->QueryInterface(IID_PPV_ARGS(&_device5)), "D3D11.5 device interface (shared fences)");
        ComPtr<ID3D11DeviceContext> context;
        device->GetImmediateContext(&context);
        Require(context.As(&_context), "D3D11.4 context interface");
        Require(g_testFaults & FaultBridgeDevice ? E_FAIL : D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&_device12)),
                "D3D12 device on the renderer's adapter");
        if (generating)
        {
            try
            {
                fg::Bind(_device12.Get());
            }
            catch (const std::exception &error)
            {
                fg::Fail(error.what());
            }
        }

        // D3D12 swap chains are flip-model only; a blit-model request becomes flip-discard with at least two buffers.
        _displayDesc = requested;
        _applicationFlags = requested.Flags;
        if (!IsFlip(_displayDesc.SwapEffect))
        {
            _displayDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
            _displayDesc.Scaling = DXGI_SCALING_STRETCH;
        }
        _displayDesc.BufferCount = std::max<UINT>(2, std::min<UINT>(_displayDesc.BufferCount, (UINT)_slots.size()));
        _displayDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        _displayDesc.AlphaMode = DXGI_ALPHA_MODE_UNSPECIFIED;
        _displayDesc.Flags &= ~DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT; // the game's waitable object is the bridge's own (_latency)
        if (fullscreen) _fullscreen = *fullscreen, _hasFullscreen = true;
        // With Streamline the queue and the display chain come from its proxies; with AMD FSR the SDK makes the display chain. When the
        // backend cannot, frame generation stops and the bridge presents on a plain chain.
        _kind = fg::Kind();
        try
        {
            MakeDisplay(_kind);
        }
        catch (const std::exception &error)
        {
            if (!_kind) throw;
            fg::Fail(error.what());
            _kind = 0;
            MakeDisplay(0);
        }
        try
        {
            Finish(window, requested);
        }
        catch (const std::exception &error)
        {
            // The window must be free for the game's own swap chain (CreateBridge falls back to it): the display chain, its buffers and
            // the backend's hold on it go now, not with the members.
            ReleaseBuffers();
            _display.Reset();
            _queue.Reset();
            try
            {
                fg::ReleaseDisplay();
            }
            catch (const std::exception &)
            {
                // The construction's own failure is the one reported.
            }
            fg::Fail(error.what());
            for (HANDLE *handle : {&_event, &_latency, &_retireEvent})
                if (*handle) CloseHandle(*handle), *handle = nullptr;
            throw;
        }
    }

    // The rest of the construction once the display swap chain exists: fences, buffers and the waitable object.
    void Finish(HWND window, const DXGI_SWAP_CHAIN_DESC1 &requested)
    {
        Require(_device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&_produced12)), "Shared producer fence");
        HANDLE handle = nullptr;
        Require(_device12->CreateSharedHandle(_produced12.Get(), nullptr, GENERIC_ALL, nullptr, &handle), "Shared fence handle");
        HRESULT opened = _device5->OpenSharedFence(handle, IID_PPV_ARGS(&_produced11));
        CloseHandle(handle);
        Require(opened, "Opening the shared fence on D3D11");
        Require(_device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&_consumed12)), "Consumer fence");
        _event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!_event) throw std::runtime_error("completion event");
        CreateBuffers();
        if ((requested.Flags & DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT) && !(_latency = CreateSemaphoreW(nullptr, _latencyLimit, 16, nullptr)))
            throw std::runtime_error("frame-latency semaphore"); // 16: DXGI's highest maximum frame latency
        if (_latency)
        {
            if (!(_retireEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr))) throw std::runtime_error("retire event");
            _retirer = std::thread([this] { Retire(); });
        }
        ++g_active;
        Log(LogInfo, "Present bridge ready: window %p, %ux%u, format %d, %u buffers, swap effect %d (requested %d, %u buffers), flags 0x%X.", (void *)window,
            _desc.Width, _desc.Height, (int)_desc.Format, _desc.BufferCount, (int)_desc.SwapEffect, (int)requested.SwapEffect, requested.BufferCount, _desc.Flags);
    }

    ~Bridge()
    {
        // Handed back to the game's own swap chain, nothing of frame generation is left to release.
        if (!_native) try
        {
            fg::Suspend();
            if (!_failed && _queue) Drain();
            fg::ReleaseInputs();
            fg::ReleaseDisplay();
        }
        catch (const std::exception &error)
        {
            Log(LogFailure, "Releasing the present bridge failed: %s", error.what());
        }
        if (_retirer.joinable())
        {
            {
                std::lock_guard<std::mutex> lock(_retireLock);
                _stopRetiring = true;
            }
            _retireWake.notify_one();
            _retirer.join();
        }
        if (_retireEvent) CloseHandle(_retireEvent);
        if (_event) CloseHandle(_event);
        if (_latency) CloseHandle(_latency);
        if (g_bridge == this) g_bridge = nullptr;
        --g_active;
    }

    // On the game's own swap chain: buffer 0 as the game drew it, which the last present copied out (its back buffer is the next one by now).
    HRESULT NativeReadback(void *bytes)
    {
        D3D11_TEXTURE2D_DESC desc{};
        _producer->GetDesc(&desc);
        desc.Format = _desc.Format;
        desc.BindFlags = 0;
        desc.Usage = D3D11_USAGE_STAGING;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging;
        HRESULT result = _device11->CreateTexture2D(&desc, nullptr, &staging);
        if (FAILED(result)) return result;
        _context->CopyResource(staging.Get(), _producer.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(result = _context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped))) return result;
        for (UINT y = 0; y < _desc.Height; y++)
            std::memcpy(static_cast<unsigned char *>(bytes) + (size_t)y * _desc.Width * 4, static_cast<unsigned char *>(mapped.pData) + (size_t)y * mapped.RowPitch,
                        (size_t)_desc.Width * 4);
        _context->Unmap(staging.Get(), 0);
        return S_OK;
    }

    // Copies the last presented back buffer (4 bytes a pixel, top row first) for the offline check and the development driver.
    HRESULT Readback(void *bytes, UINT size)
    {
        std::lock_guard<std::recursive_mutex> lock(_lock);
        if (_failed) return DXGI_ERROR_DEVICE_REMOVED;
        if (!_hasImage || !bytes || size != _desc.Width * _desc.Height * 4) return E_INVALIDARG;
        if (_native) return NativeReadback(bytes);
        try
        {
            auto &slot = _slots[_lastIndex];
            Wait(slot.done);
            auto source = slot.backbuffer->GetDesc();
            D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
            UINT64 total = 0;
            _device12->GetCopyableFootprints(&source, 0, 1, 0, &footprint, nullptr, nullptr, &total);
            D3D12_HEAP_PROPERTIES heap{};
            heap.Type = D3D12_HEAP_TYPE_READBACK;
            D3D12_RESOURCE_DESC buffer{};
            buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
            buffer.Width = total;
            buffer.Height = buffer.DepthOrArraySize = buffer.MipLevels = 1;
            buffer.SampleDesc.Count = 1;
            buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            ComPtr<ID3D12Resource> readback;
            Require(_device12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buffer, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readback)), "Readback buffer");
            Require(slot.allocator->Reset(), "Readback allocator reset");
            Require(slot.list->Reset(slot.allocator.Get(), nullptr), "Readback list reset");
            D3D12_RESOURCE_BARRIER barrier{};
            barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            barrier.Transition.pResource = slot.backbuffer.Get();
            barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
            barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
            slot.list->ResourceBarrier(1, &barrier);
            D3D12_TEXTURE_COPY_LOCATION from{}, to{};
            from.pResource = slot.backbuffer.Get();
            from.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            to.pResource = readback.Get();
            to.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            to.PlacedFootprint = footprint;
            slot.list->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
            std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter);
            slot.list->ResourceBarrier(1, &barrier);
            Require(slot.list->Close(), "Readback list close");
            ID3D12CommandList *lists[] = {slot.list.Get()};
            _queue->ExecuteCommandLists(1, lists);
            UINT64 value = ++_serial;
            Require(_queue->Signal(_consumed12.Get(), value), "Readback signal");
            slot.done = value;
            Wait(value);
            void *mapped = nullptr;
            D3D12_RANGE range{0, (SIZE_T)total};
            Require(readback->Map(0, &range, &mapped), "Readback map");
            for (UINT y = 0; y < _desc.Height; y++)
                std::memcpy(static_cast<unsigned char *>(bytes) + (size_t)y * _desc.Width * 4,
                            static_cast<unsigned char *>(mapped) + footprint.Offset + (size_t)y * footprint.Footprint.RowPitch, (size_t)_desc.Width * 4);
            D3D12_RANGE written{0, 0};
            readback->Unmap(0, &written);
            return S_OK;
        }
        catch (const std::exception &error)
        {
            return Fail("Readback", error);
        }
    }

    // IUnknown
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void **output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IDXGIObject) || iid == __uuidof(IDXGIDeviceSubObject) || iid == __uuidof(IDXGISwapChain) ||
            iid == __uuidof(IDXGISwapChain1) || iid == __uuidof(IDXGISwapChain2))
        {
            *output = static_cast<IDXGISwapChain2 *>(this);
            AddRef();
            return S_OK;
        }
        // Unity 2019.4's actual use of the swap chain is measured, not assumed: every other interface it asks for is logged once.
        char name[64];
        std::snprintf(name, sizeof name, "%08lX-%04X-%04X", iid.Data1, iid.Data2, iid.Data3);
        std::lock_guard<std::recursive_mutex> lock(_lock);
        if (_unknownInterfaces.insert(name).second) Log(LogInfo, "Present bridge: the game asked the swap chain for interface {%s}; not offered.", name);
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++_refs; }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG count = --_refs;
        if (!count) delete this;
        return count;
    }
    // IDXGIObject
    HRESULT STDMETHODCALLTYPE SetPrivateData(REFGUID id, UINT size, const void *data) override { return _display->SetPrivateData(id, size, data); }
    HRESULT STDMETHODCALLTYPE SetPrivateDataInterface(REFGUID id, const IUnknown *object) override { return _display->SetPrivateDataInterface(id, object); }
    HRESULT STDMETHODCALLTYPE GetPrivateData(REFGUID id, UINT *size, void *data) override { return _display->GetPrivateData(id, size, data); }
    HRESULT STDMETHODCALLTYPE GetParent(REFIID iid, void **output) override { return _factory->QueryInterface(iid, output); }
    // IDXGIDeviceSubObject: the game's device is the D3D11 one.
    HRESULT STDMETHODCALLTYPE GetDevice(REFIID iid, void **output) override { return _device11->QueryInterface(iid, output); }
    // IDXGISwapChain
    HRESULT STDMETHODCALLTYPE Present(UINT interval, UINT flags) override { return Show(interval, flags, nullptr, false); }
    HRESULT STDMETHODCALLTYPE GetBuffer(UINT index, REFIID iid, void **output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        // Unity renders buffer 0 only; another index would hand out an unrelated image, so it is refused.
        if (index)
        {
            Log(LogInfo, "Present bridge: the game asked for back buffer %u; only buffer 0 exists.", index);
            return DXGI_ERROR_INVALID_CALL;
        }
        std::lock_guard<std::recursive_mutex> lock(_lock);
        return _producer ? _producer->QueryInterface(iid, output) : DXGI_ERROR_DEVICE_REMOVED;
    }
    HRESULT STDMETHODCALLTYPE SetFullscreenState(BOOL fullscreen, IDXGIOutput *target) override
    {
        std::lock_guard<std::recursive_mutex> lock(_lock);
        if (_failed) return DXGI_ERROR_DEVICE_REMOVED;
        // Frame generation runs in windowed and borderless mode only; asked for exclusive fullscreen, the game gets it on its own swap chain.
        if (fullscreen && !GoNative("the game asked for exclusive fullscreen, which frame generation does not run in")) return DXGI_ERROR_NOT_CURRENTLY_AVAILABLE;
        return _display->SetFullscreenState(fullscreen, target);
    }
    HRESULT STDMETHODCALLTYPE GetFullscreenState(BOOL *fullscreen, IDXGIOutput **target) override { return _display->GetFullscreenState(fullscreen, target); }
    // The descriptions carry the game's own waitable flag: the display chain has none (_latency).
    HRESULT STDMETHODCALLTYPE GetDesc(DXGI_SWAP_CHAIN_DESC *desc) override
    {
        HRESULT result = _display->GetDesc(desc);
        if (SUCCEEDED(result)) desc->Flags |= _applicationFlags & DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        return result;
    }
    HRESULT STDMETHODCALLTYPE ResizeBuffers(UINT count, UINT width, UINT height, DXGI_FORMAT format, UINT flags) override
    {
        if (format != DXGI_FORMAT_UNKNOWN && Typeless(format) == DXGI_FORMAT_UNKNOWN) return DXGI_ERROR_UNSUPPORTED;
        std::lock_guard<std::recursive_mutex> lock(_lock);
        if (_failed) return DXGI_ERROR_DEVICE_REMOVED;
        StageScope stage(StageResize);
        const UINT asked = count;
        // On the game's own swap chain: buffer 0 goes with the game's views on it (DXGI's rule for ResizeBuffers) and comes back at the new size.
        auto native = [&]() -> HRESULT {
            try
            {
                UnbindProducer();
                _nativeBuffer.Reset();
                _producer.Reset();
                HRESULT result = _display->ResizeBuffers(asked, width, height, format, flags & ~DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);
                NativeBuffers();
                return result;
            }
            catch (const std::exception &error)
            {
                return Fail("ResizeBuffers", error);
            }
        };
        if (_native) return native();
        try
        {
            fg::Suspend(); // off while the size changes; the next frame's inputs at the new size turn it back on
            Drain();
            fg::ReleaseInputs();
            UnbindProducer();
            ReleaseBuffers();
            if (count) count = std::max<UINT>(2, std::min<UINT>(count, (UINT)_slots.size()));
            HRESULT result = _display->ResizeBuffers(count, width, height, format, flags & ~DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);
            CreateBuffers(); // at the new size, or the old one when DXGI refused
            if (SUCCEEDED(result))
            {
                // A switch makes the display chain again as it is now.
                _displayDesc.Width = _desc.Width;
                _displayDesc.Height = _desc.Height;
                if (count) _displayDesc.BufferCount = count;
                if (format != DXGI_FORMAT_UNKNOWN) _displayDesc.Format = format;
                _displayDesc.Flags = flags & ~DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
            }
            Log(LogInfo, "Present bridge resized to %ux%u (%u buffers, flags 0x%X): HRESULT 0x%08lX.", _desc.Width, _desc.Height, _desc.BufferCount, flags,
                (unsigned long)result);
            return result;
        }
        catch (const std::exception &error)
        {
            char why[384];
            std::snprintf(why, sizeof why, "ResizeBuffers: %s", error.what());
            if (GoNative(why)) return native();
            return Fail("ResizeBuffers", error);
        }
    }
    HRESULT STDMETHODCALLTYPE ResizeTarget(const DXGI_MODE_DESC *target) override { return _display->ResizeTarget(target); }
    HRESULT STDMETHODCALLTYPE GetContainingOutput(IDXGIOutput **output) override { return _display->GetContainingOutput(output); }
    HRESULT STDMETHODCALLTYPE GetFrameStatistics(DXGI_FRAME_STATISTICS *stats) override { return _display->GetFrameStatistics(stats); }
    HRESULT STDMETHODCALLTYPE GetLastPresentCount(UINT *count) override { return _display->GetLastPresentCount(count); }
    // IDXGISwapChain1
    HRESULT STDMETHODCALLTYPE GetDesc1(DXGI_SWAP_CHAIN_DESC1 *desc) override
    {
        HRESULT result = _display->GetDesc1(desc);
        if (SUCCEEDED(result)) desc->Flags |= _applicationFlags & DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        return result;
    }
    HRESULT STDMETHODCALLTYPE GetFullscreenDesc(DXGI_SWAP_CHAIN_FULLSCREEN_DESC *desc) override { return _display->GetFullscreenDesc(desc); }
    HRESULT STDMETHODCALLTYPE GetHwnd(HWND *window) override { return _display->GetHwnd(window); }
    HRESULT STDMETHODCALLTYPE GetCoreWindow(REFIID, void **output) override
    {
        if (output) *output = nullptr;
        return DXGI_ERROR_INVALID_CALL;
    }
    HRESULT STDMETHODCALLTYPE Present1(UINT interval, UINT flags, const DXGI_PRESENT_PARAMETERS *parameters) override
    {
        if (!parameters) return E_INVALIDARG;
        // The whole frame is copied every time, so partial-present hints would be wrong for the real chain.
        DXGI_PRESENT_PARAMETERS whole{};
        return Show(interval, flags, &whole, true);
    }
    BOOL STDMETHODCALLTYPE IsTemporaryMonoSupported() override { return FALSE; }
    HRESULT STDMETHODCALLTYPE GetRestrictToOutput(IDXGIOutput **output) override { return _display->GetRestrictToOutput(output); }
    HRESULT STDMETHODCALLTYPE SetBackgroundColor(const DXGI_RGBA *color) override { return _display->SetBackgroundColor(color); }
    HRESULT STDMETHODCALLTYPE GetBackgroundColor(DXGI_RGBA *color) override { return _display->GetBackgroundColor(color); }
    HRESULT STDMETHODCALLTYPE SetRotation(DXGI_MODE_ROTATION rotation) override { return _display->SetRotation(rotation); }
    HRESULT STDMETHODCALLTYPE GetRotation(DXGI_MODE_ROTATION *rotation) override { return _display->GetRotation(rotation); }
    // IDXGISwapChain2
    HRESULT STDMETHODCALLTYPE SetSourceSize(UINT width, UINT height) override { return _display->SetSourceSize(width, height); }
    HRESULT STDMETHODCALLTYPE GetSourceSize(UINT *width, UINT *height) override { return _display->GetSourceSize(width, height); }
    // The game's frame latency is the bridge's own semaphore (_latency) when it created its chain with a waitable object; the display
    // chain's latency is Streamline's. Without one, DXGI refuses these calls, and so does the display chain they go to.
    HRESULT STDMETHODCALLTYPE SetMaximumFrameLatency(UINT value) override
    {
        if (!_latency) return _display->SetMaximumFrameLatency(value);
        if (value < 1 || value > 16) return DXGI_ERROR_INVALID_CALL;
        std::lock_guard<std::mutex> lock(_latencyLock);
        if ((LONG)value == _latencyLimit) return S_OK;
        // As DXGI: a higher limit gives the waitable object the frames of difference, a lower one takes none back, because the game waits
        // them off itself (Unity 2019.4 waits once per frame of difference right after lowering it, UnityPlayer.dll 0x180469a5e). Taking
        // them back here as well left the game one frame short for good: at a limit of 1 (GTFO's 最大渲染队列帧数 0 or 1) its next wait
        // never returned and the picture froze. Only the bridge's own cap (AMD FSR Frame Generation) is still taken back from the releases.
        if ((LONG)value < _latencyLimit) _latencyApplied -= _latencyLimit - (LONG)value;
        _latencyLimit = (LONG)value;
        Log(LogInfo, "Present bridge: the game set the maximum frame latency to %u (in force: %ld; AMD FSR Frame Generation %s).", value, LatencyFor(_fsrOn),
            _fsrOn ? "on" : "off");
        // A higher limit gives frames now (first paying any debt); a lower one is taken back from the coming releases.
        ApplyLatency(LatencyFor(_fsrOn));
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetMaximumFrameLatency(UINT *value) override
    {
        if (!_latency) return _display->GetMaximumFrameLatency(value);
        if (!value) return E_INVALIDARG;
        std::lock_guard<std::mutex> lock(_latencyLock);
        *value = (UINT)_latencyLimit;
        return S_OK;
    }
    // A new handle each time, as DXGI gives: the game closes it when it is done.
    HANDLE STDMETHODCALLTYPE GetFrameLatencyWaitableObject() override
    {
        if (!_waitableAsked.exchange(true))
            Log(LogInfo, "Present bridge: the game took the frame-latency waitable object (%s).", _latency ? "the bridge's own" : "not created with it, none given");
        HANDLE handle = nullptr;
        if (_latency && !DuplicateHandle(GetCurrentProcess(), _latency, GetCurrentProcess(), &handle, 0, FALSE, DUPLICATE_SAME_ACCESS))
        {
            Log(LogFailure, "The frame-latency waitable object could not be handed to the game (Windows error %lu); the game paces without it.", GetLastError());
            handle = nullptr;
        }
        return handle;
    }
    HRESULT STDMETHODCALLTYPE SetMatrixTransform(const DXGI_MATRIX_3X2_F *matrix) override { return _display->SetMatrixTransform(matrix); }
    HRESULT STDMETHODCALLTYPE GetMatrixTransform(DXGI_MATRIX_3X2_F *matrix) override { return _display->GetMatrixTransform(matrix); }
};


HRESULT CreateBridge(IDXGIFactory2 *factory, ID3D11Device *device, HWND window, const DXGI_SWAP_CHAIN_DESC1 *desc,
                     const DXGI_SWAP_CHAIN_FULLSCREEN_DESC *fullscreen, IDXGIOutput *output, IDXGISwapChain1 **result)
{
    if (desc->SampleDesc.Count != 1 || desc->Stereo || Typeless(desc->Format) == DXGI_FORMAT_UNKNOWN || (fullscreen && !fullscreen->Windowed))
    {
        ++g_creationFailures;
        Log(LogFailure, "The game's swap chain (format %d, %u samples, %s) cannot be bridged; frame generation stays off and the game presents as usual.",
            (int)desc->Format, desc->SampleDesc.Count, fullscreen && !fullscreen->Windowed ? "exclusive fullscreen" : "windowed");
        return DXGI_ERROR_UNSUPPORTED;
    }
    try
    {
        ComPtr<IDXGIDevice> dxgi;
        Require(device->QueryInterface(IID_PPV_ARGS(&dxgi)), "D3D11 DXGI interface");
        ComPtr<IDXGIAdapter> adapter;
        Require(dxgi->GetAdapter(&adapter), "Renderer adapter");
        // Without frame generation the bridge only costs a copy a frame, so the game keeps its own swap chain (the offline check asks
        // for the bridge anyway, request 2).
        bool generating = fg::Start(OwnFolder().c_str(), adapter.Get());
        if (!generating && g_requested != 2)
        {
            ++g_creationFailures;
            return DXGI_ERROR_UNSUPPORTED;
        }
        auto *bridge = new Bridge(factory, device, window, *desc, fullscreen, output, adapter.Get(), generating);
        g_window = window;
        g_bridge = bridge;
        *result = bridge;
        return S_OK;
    }
    catch (const std::exception &error)
    {
        ++g_creationFailures;
        Log(LogFailure, "Building the present bridge failed (%s); frame generation stays off and the game presents as usual.", error.what());
        return E_FAIL;
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------
// Hooks: DXGI factory swap-chain creation, and the import tables that hand out the D3D11 and DXGI entry points.

HRESULT STDMETHODCALLTYPE CreateSwapChain(IDXGIFactory *factory, IUnknown *device, DXGI_SWAP_CHAIN_DESC *desc, IDXGISwapChain **output)
{
    auto original = Original<HRESULT(STDMETHODCALLTYPE *)(IDXGIFactory *, IUnknown *, DXGI_SWAP_CHAIN_DESC *, IDXGISwapChain **)>(factory, 10);
    if (desc && device && output && WindowEligible(desc->OutputWindow))
    {
        ComPtr<IDXGIFactory2> factory2;
        ComPtr<ID3D11Device> device11;
        if (!desc->Windowed)
            Log(LogFailure, "The game creates its swap chain in exclusive fullscreen; frame generation needs windowed or borderless mode and stays off.");
        else if (SUCCEEDED(factory->QueryInterface(IID_PPV_ARGS(&factory2))) && SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&device11))))
        {
            DXGI_SWAP_CHAIN_DESC1 d{};
            d.Width = desc->BufferDesc.Width;
            d.Height = desc->BufferDesc.Height;
            d.Format = desc->BufferDesc.Format;
            d.SampleDesc = desc->SampleDesc;
            d.BufferUsage = desc->BufferUsage;
            d.BufferCount = desc->BufferCount;
            d.SwapEffect = desc->SwapEffect;
            d.Flags = desc->Flags;
            d.Scaling = DXGI_SCALING_STRETCH;
            d.AlphaMode = DXGI_ALPHA_MODE_UNSPECIFIED;
            Log(LogInfo, "Graphics: the game creates its swap chain with CreateSwapChain (%ux%u, format %d, %u buffers, swap effect %d, flags 0x%X).", d.Width,
                d.Height, (int)d.Format, d.BufferCount, (int)d.SwapEffect, d.Flags);
            ComPtr<IDXGISwapChain1> bridge;
            if (SUCCEEDED(CreateBridge(factory2.Get(), device11.Get(), desc->OutputWindow, &d, nullptr, nullptr, &bridge)))
            {
                ++g_chains;
                return bridge->QueryInterface(IID_PPV_ARGS(output));
            }
        }
    }
    HRESULT result = original(factory, device, desc, output);
    if (SUCCEEDED(result) && !t_building) ++g_chains;
    return result;
}

HRESULT STDMETHODCALLTYPE CreateSwapChainForHwnd(IDXGIFactory2 *factory, IUnknown *device, HWND window, const DXGI_SWAP_CHAIN_DESC1 *desc,
                                                 const DXGI_SWAP_CHAIN_FULLSCREEN_DESC *fullscreen, IDXGIOutput *restrictTo, IDXGISwapChain1 **output)
{
    auto original = Original<HRESULT(STDMETHODCALLTYPE *)(IDXGIFactory2 *, IUnknown *, HWND, const DXGI_SWAP_CHAIN_DESC1 *,
                                                          const DXGI_SWAP_CHAIN_FULLSCREEN_DESC *, IDXGIOutput *, IDXGISwapChain1 **)>(factory, 15);
    if (desc && device && output && WindowEligible(window))
    {
        ComPtr<ID3D11Device> device11;
        if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&device11))))
        {
            Log(LogInfo, "Graphics: the game creates its swap chain with CreateSwapChainForHwnd (%ux%u, format %d, %u buffers, swap effect %d, scaling %d, flags 0x%X%s).",
                desc->Width, desc->Height, (int)desc->Format, desc->BufferCount, (int)desc->SwapEffect, (int)desc->Scaling, desc->Flags,
                fullscreen && !fullscreen->Windowed ? ", exclusive fullscreen" : "");
            if (SUCCEEDED(CreateBridge(factory, device11.Get(), window, desc, fullscreen, restrictTo, output)))
            {
                ++g_chains;
                return S_OK;
            }
        }
    }
    HRESULT result = original(factory, device, window, desc, fullscreen, restrictTo, output);
    if (SUCCEEDED(result) && !t_building) ++g_chains;
    return result;
}

void ObserveFactory(IUnknown *unknown)
{
    if (!unknown) return;
    ++g_factories;
    ComPtr<IDXGIFactory> factory;
    if (SUCCEEDED(unknown->QueryInterface(IID_PPV_ARGS(&factory))))
        PatchSlot(*reinterpret_cast<void ***>(factory.Get()) + 10, reinterpret_cast<void *>(&CreateSwapChain));
    ComPtr<IDXGIFactory2> factory2;
    if (SUCCEEDED(unknown->QueryInterface(IID_PPV_ARGS(&factory2))))
    {
        auto table = *reinterpret_cast<void ***>(factory2.Get());
        PatchSlot(table + 10, reinterpret_cast<void *>(&CreateSwapChain));
        PatchSlot(table + 15, reinterpret_cast<void *>(&CreateSwapChainForHwnd));
    }
}

// The real entry points, as resolved when the hooks were installed (or when the game looked them up), so overlays that hooked them
// first stay in the chain.
std::mutex g_targetsLock;
std::map<std::string, FARPROC> g_targets;

template <class T> T Target(const char *name, T fallback)
{
    std::lock_guard<std::mutex> lock(g_targetsLock);
    auto found = g_targets.find(name);
    return found == g_targets.end() ? fallback : reinterpret_cast<T>(found->second);
}

bool RememberTarget(const char *name, FARPROC actual)
{
    std::lock_guard<std::mutex> lock(g_targetsLock);
    auto found = g_targets.find(name);
    if (found != g_targets.end()) return found->second == actual;
    g_targets.emplace(name, actual);
    return true;
}

HRESULT WINAPI HookCreateDevice(IDXGIAdapter *adapter, D3D_DRIVER_TYPE driver, HMODULE software, UINT flags, const D3D_FEATURE_LEVEL *levels, UINT count,
                                UINT sdk, ID3D11Device **device, D3D_FEATURE_LEVEL *level, ID3D11DeviceContext **context)
{
    ++g_devices;
    return Target("D3D11CreateDevice", &D3D11CreateDevice)(adapter, driver, software, flags, levels, count, sdk, device, level, context);
}

HRESULT WINAPI HookCreateDeviceAndSwapChain(IDXGIAdapter *adapter, D3D_DRIVER_TYPE driver, HMODULE software, UINT flags, const D3D_FEATURE_LEVEL *levels,
                                            UINT count, UINT sdk, const DXGI_SWAP_CHAIN_DESC *desc, IDXGISwapChain **chain, ID3D11Device **device,
                                            D3D_FEATURE_LEVEL *level, ID3D11DeviceContext **context)
{
    ++g_devices;
    if (chain) ++g_chains;
    if (g_requested && chain)
        Log(LogFailure, "The game creates its device and swap chain in one call; the present bridge needs the separate factory path, so frame generation stays off.");
    return Target("D3D11CreateDeviceAndSwapChain", &D3D11CreateDeviceAndSwapChain)(adapter, driver, software, flags, levels, count, sdk, desc, chain, device,
                                                                                   level, context);
}

HRESULT WINAPI HookFactory(REFIID iid, void **output)
{
    HRESULT result = Target("CreateDXGIFactory", &CreateDXGIFactory)(iid, output);
    if (SUCCEEDED(result) && output) ObserveFactory(static_cast<IUnknown *>(*output));
    return result;
}

HRESULT WINAPI HookFactory1(REFIID iid, void **output)
{
    HRESULT result = Target("CreateDXGIFactory1", &CreateDXGIFactory1)(iid, output);
    if (SUCCEEDED(result) && output) ObserveFactory(static_cast<IUnknown *>(*output));
    return result;
}

HRESULT WINAPI HookFactory2(UINT flags, REFIID iid, void **output)
{
    HRESULT result = Target("CreateDXGIFactory2", &CreateDXGIFactory2)(flags, iid, output);
    if (SUCCEEDED(result) && output) ObserveFactory(static_cast<IUnknown *>(*output));
    return result;
}

void *Replacement(const char *name)
{
    if (!std::strcmp(name, "D3D11CreateDevice")) return reinterpret_cast<void *>(&HookCreateDevice);
    if (!std::strcmp(name, "D3D11CreateDeviceAndSwapChain")) return reinterpret_cast<void *>(&HookCreateDeviceAndSwapChain);
    if (!std::strcmp(name, "CreateDXGIFactory")) return reinterpret_cast<void *>(&HookFactory);
    if (!std::strcmp(name, "CreateDXGIFactory1")) return reinterpret_cast<void *>(&HookFactory1);
    if (!std::strcmp(name, "CreateDXGIFactory2")) return reinterpret_cast<void *>(&HookFactory2);
    return nullptr;
}

using Resolver = FARPROC(WINAPI *)(HMODULE, LPCSTR);
std::atomic<Resolver> g_gameResolver{nullptr}, g_unityResolver{nullptr};

FARPROC Lookup(Resolver resolver, HMODULE module, LPCSTR name)
{
    FARPROC actual = resolver(module, name);
    DWORD error = GetLastError();
    if (reinterpret_cast<uintptr_t>(name) > 0xffff && actual && (module == GetModuleHandleW(L"d3d11.dll") || module == GetModuleHandleW(L"dxgi.dll")))
    {
        void *hook = Replacement(name);
        if (hook && actual != reinterpret_cast<FARPROC>(hook) && RememberTarget(name, actual))
        {
            SetLastError(error);
            return reinterpret_cast<FARPROC>(hook);
        }
    }
    SetLastError(error);
    return actual;
}

FARPROC WINAPI LookupGame(HMODULE module, LPCSTR name) { return Lookup(g_gameResolver.load(), module, name); }
FARPROC WINAPI LookupUnity(HMODULE module, LPCSTR name) { return Lookup(g_unityResolver.load(), module, name); }

bool Executable(void *pointer)
{
    MEMORY_BASIC_INFORMATION memory{};
    return VirtualQuery(pointer, &memory, sizeof memory) && memory.State == MEM_COMMIT && !(memory.Protect & PAGE_GUARD) &&
           (memory.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY));
}

int PatchImports(HMODULE module, const char *label, std::atomic<Resolver> &resolver, void *lookup)
{
    if (!module) return 0;
    auto base = reinterpret_cast<unsigned char *>(module);
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER *>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS64 *>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;
    auto directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!directory.VirtualAddress) return 0;
    int patched = 0;
    for (auto imports = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR *>(base + directory.VirtualAddress); imports->Name; ++imports)
    {
        if (!imports->OriginalFirstThunk) continue;
        auto names = reinterpret_cast<IMAGE_THUNK_DATA64 *>(base + imports->OriginalFirstThunk);
        auto slots = reinterpret_cast<IMAGE_THUNK_DATA64 *>(base + imports->FirstThunk);
        auto dll = reinterpret_cast<const char *>(base + imports->Name);
        bool graphics = !_stricmp(dll, "d3d11.dll") || !_stricmp(dll, "dxgi.dll");
        for (; names->u1.AddressOfData; ++names, ++slots)
        {
            if (IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal)) continue;
            auto name = reinterpret_cast<IMAGE_IMPORT_BY_NAME *>(base + names->u1.AddressOfData)->Name;
            bool isResolver = !std::strcmp(name, "GetProcAddress");
            void *hook = isResolver ? lookup : graphics ? Replacement(name) : nullptr;
            if (!hook) continue;
            auto slot = reinterpret_cast<void **>(&slots->u1.Function);
            if (*slot == hook) continue;
            if (isResolver)
            {
                // The previous resolver (for example Doorstop's) stays in the chain; ours forwards to it.
                if (resolver.load() || !Executable(*slot))
                {
                    Log(LogInfo, "Graphics hooks: %s!GetProcAddress left as it is (already chained or not executable).", label);
                    continue;
                }
                resolver = reinterpret_cast<Resolver>(*slot);
                if (PatchSlot(slot, hook)) patched++;
                else resolver = nullptr;
                continue;
            }
            // An import another hook already replaced is left alone: bypassing it could break an overlay.
            HMODULE source = GetModuleHandleA(dll);
            void *expected = source ? reinterpret_cast<void *>(GetProcAddress(source, name)) : nullptr;
            if (!expected || *slot != expected || !RememberTarget(name, reinterpret_cast<FARPROC>(*slot)))
            {
                Log(LogInfo, "Graphics hooks: %s!%s already hooked by something else; left as it is.", label, name);
                continue;
            }
            if (PatchSlot(slot, hook)) patched++;
        }
    }
    return patched;
}
} // namespace

void BridgeLog(int kind, const char *format, ...)
{
    va_list args;
    va_start(args, format);
    Queue(kind, format, args);
    va_end(args);
}

extern "C"
{
// Called by the preloader before Unity creates its graphics device, always (bridge 1): the game's swap chain is then replaced by the
// present bridge if this PC can run frame generation (the card and the driver; otherwise the game keeps its own chain and a line says why);
// 2 (the offline check) builds the bridge even when it cannot. 0 patches nothing. Returns the number of import slots patched.
__declspec(dllexport) int ForgeFrameGen_InstallGraphicsHooks(int bridge)
{
    g_requested = bridge == 2 ? 2 : bridge ? 1 : 0;
    if (!g_requested) return 0;
    bool d3d11Loaded = GetModuleHandleW(L"d3d11.dll") != nullptr, dxgiLoaded = GetModuleHandleW(L"dxgi.dll") != nullptr;
    int patched = PatchImports(GetModuleHandleW(nullptr), "game", g_gameResolver, reinterpret_cast<void *>(&LookupGame)) +
                  PatchImports(GetModuleHandleW(L"UnityPlayer.dll"), "UnityPlayer.dll", g_unityResolver, reinterpret_cast<void *>(&LookupUnity));
    Log(LogInfo, "Graphics hooks installed for the present bridge: %d import slots; d3d11.dll %s, dxgi.dll %s at install time.", patched,
        d3d11Loaded ? "already loaded" : "not loaded yet", dxgiLoaded ? "already loaded" : "not loaded yet");
    return patched;
}

// 0 no bridge requested, 1 requested but no bridge built yet, 2 bridge active, -1 the bridge could not be built, -2 it stopped, -3 it
// handed the window back to the game's own D3D11 swap chain (the game renders on without frame generation).
__declspec(dllexport) int ForgeFrameGen_PresentState()
{
    if (g_runtimeFailures) return -2;
    if (g_fallbacks) return -3;
    if (g_active) return 2;
    if (g_creationFailures) return -1;
    return g_requested;
}

// Where the render thread is in the bridge now (PresentStage: 0 not in it, 1 waiting for a back buffer slot, 2 frame generation's
// inputs, 3 the display swap chain's Present, 4 ResizeBuffers, 5 switching frame generation, 6 handing the window back to the game's
// own swap chain), for the preloader's line when no frame has come through for a while.
__declspec(dllexport) int ForgeFrameGen_PresentStage() { return g_stage; }

// Offline check only (BridgeCheck --fault): the failures to make happen (TestFault, bridge_common.h). Never called in the game.
__declspec(dllexport) void ForgeFrameGen_TestFaults(unsigned faults) { g_testFaults = faults; }

// Devices, factories and swap chains the hooks saw being created (0 when the hooks came too late), and frames the bridge presented.
__declspec(dllexport) void ForgeFrameGen_GraphicsCounts(int *devices, int *factories, int *chains, long long *presents)
{
    if (devices) *devices = g_devices;
    if (factories) *factories = g_factories;
    if (chains) *chains = g_chains;
    if (presents) *presents = g_presents;
}

// Copies the oldest pending present-bridge line into buffer (UTF-8, NUL-terminated); *kind is its BridgeLogKind (0 a developer line,
// 1 a failure, 2 a present frame generation left out). Returns its length, or -1 when there is none.
__declspec(dllexport) int ForgeFrameGen_PopBridgeMessage(char *buffer, int capacity, int *kind)
{
    std::lock_guard<std::mutex> lock(g_logLock);
    if (g_log.empty() || capacity <= 0) return -1;
    auto line = std::move(g_log.front());
    g_log.pop_front();
    int length = (int)std::min<size_t>(line.second.size(), (size_t)capacity - 1);
    std::memcpy(buffer, line.second.data(), length);
    buffer[length] = '\0';
    if (kind) *kind = line.first;
    return length;
}

// Main thread: frame generation's shared inputs and resources go (the plugin's DLSS pass is off), a back buffer slot at a time.
__declspec(dllexport) void ForgeFrameGen_ReleaseFrameGeneration() { fg::RequestRelease(); }

// Whether developer logs are on (the plugin's ForgeCore Log.Developer): with them off, info lines are not queued at all.
__declspec(dllexport) void ForgeFrameGen_SetDeveloperLog(int on) { g_developerLog = on != 0; }

// Frame generation for the plugin's readback: 0 not running, 1 running and off, 2 on; the reason it is off; frames presented with it
// on, frames it generated, and the frames the last present showed.
__declspec(dllexport) int ForgeFrameGen_FrameGenerationState(char *reason, int capacity, long long *frames, long long *generated, int *lastPresented)
{
    return fg::State(reason, capacity, frames, generated, lastPresented);
}

// Frame generation's counters since start as one line (UTF-8, NUL-terminated); returns its length.
__declspec(dllexport) int ForgeFrameGen_FrameGenerationCounters(char *text, int capacity) { return fg::Counters(text, capacity); }

// Main thread: Reflex sleep with SimulationStart (marker 0), and SimulationEnd (marker 1), for Unity frame `frame`.
__declspec(dllexport) void ForgeFrameGen_FrameMarker(int marker, int frame) { fg::Marker(marker, frame); }

// Reflex as the player set it: boost 1 for On + Boost, frameLimit in frames a second (0 none). Takes effect before the next frame's sleep.
__declspec(dllexport) void ForgeFrameGen_SetReflex(int boost, int frameLimit) { fg::SetReflex(boost, frameLimit); }

// The frame generation the player picked (0 Auto, 1 DLSS, 2 FSR): the preloader before ForgeFrameGen_InstallGraphicsHooks, the plugin
// whenever the setting changes. With FSR, frame generation is AMD FSR's even where DLSS Frame Generation runs; a change moves the display
// swap chain to the other backend at the bridge's next present.
__declspec(dllexport) void ForgeFrameGen_SetFrameGenerationPreference(int method) { fg::Prefer(method); }

// Why DLSS Frame Generation did not start (DlssMissing, bridge_common.h; 0 when it did), with the reason and the adapter in why.
__declspec(dllexport) int ForgeFrameGen_DlssFrameGenerationMissing(char *why, int capacity) { return fg::WhyNoDlss(why, capacity); }

// The frame generation that runs: 0 none, 1 DLSS Frame Generation, 2 AMD FSR Frame Generation.
__declspec(dllexport) int ForgeFrameGen_FrameGenerationBackend() { return fg::Kind(); }

// The player's "HUD in generated frames" (1 on). AMD FSR gets the HUD-less colour only while it is on.
__declspec(dllexport) void ForgeFrameGen_SetHudInGeneratedFrames(int on) { fg::SetHud(on); }

// The plugin, when it loads (before the game's first present): frame generation may run. Without this call the bridge parks at its first
// present (fg::Enter(0)). Parked, for the preloader's log line: 1 once it has, -1 the plugin is there, 0 neither yet.
__declspec(dllexport) void ForgeFrameGen_PluginConnected() { fg::PluginConnected(); }
__declspec(dllexport) int ForgeFrameGen_Parked() { return fg::Parked() ? 1 : fg::Connected() ? -1 : 0; }

// Offline check only: what AMD FSR Frame Generation's swap chain shows, rendered and generated frames (fg::WatchShown, fg::ShownFrames).
__declspec(dllexport) void ForgeFrameGen_WatchShownFrames(int on) { fg::WatchShown(on != 0); }
__declspec(dllexport) int ForgeFrameGen_ShownFrames(unsigned char *rows, int capacity, int *generated, unsigned *width)
{
    return fg::ShownFrames(rows, capacity, generated, width);
}

// The last frame the bridge presented, 4 bytes a pixel, top row first; size must be width * height * 4 of the swap chain.
__declspec(dllexport) HRESULT ForgeFrameGen_BridgeReadback(void *bytes, unsigned size)
{
    return g_bridge ? g_bridge->Readback(bytes, size) : E_NOINTERFACE;
}
}

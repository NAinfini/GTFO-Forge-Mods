// ForgeFrameGen frame generation (Docs/forge-upscale/PLAN.md §3.3, stage 4b): the layer both backends share. See frame_generation.h and
// frame_generation_backend.h. Here: which backends started and which one runs (the player's upscaler picks it while the game runs), the
// frame's inputs drawn into the slot's shared textures, why a present is left out (with the hold after repeated defects), the readback,
// the counters and the log lines.
//
// Portions adapted from RiskOfRainUpscaling (https://github.com/xanderfrangos/RiskOfRainUpscaling, commit
// 2bea5e1e1122751264ff2512ca66d646112e24eb: native/FrameGeneration/DlssFrameGenerator.cpp, FrameGenerationSpace.h).
// Copyright (c) 2026 Xander Frangos. MIT License; the full text ships as RiskOfRainUpscaling-LICENSE.txt.
#include "frame_generation_backend.h"
#include "screen_pass.h"
#include <algorithm>
#include <array>
#include <cstdlib>
#include <cstring>
#include <set>
#include <mutex>
#include <stdexcept>
#include <string>

using Microsoft::WRL::ComPtr;

namespace fg
{
std::atomic<long long> g_presents{0}, g_frames{0}, g_generated{0}, g_markerRefusals{0};
std::atomic<int> g_lastPresented{0};
std::atomic<bool> g_hudWanted{true};

namespace
{
constexpr uint32_t kParamsVersion = 2;

std::atomic<Backend *> g_backend{nullptr}; // the backend that runs (switched on the render thread, read by the main thread too)
Backend *g_started[3]{};                   // by kind (1 DLSS, 2 AMD FSR): the backends that started
bool g_broken[3]{};                        // could not start, or failed to take over the display swap chain: not wanted again
std::atomic<int> g_preferred{0};           // the plugin's Method (the preloader, then the plugin whenever the setting changes)
std::atomic<bool> g_bound{false}, g_failed{false};
std::wstring g_folder;
ComPtr<IDXGIAdapter> g_adapter;
ComPtr<ID3D12Device> g_device;
ComPtr<ID3D12Device> g_queueDevice[3]; // by kind: the device for the present queue and the factory for the display swap chain
ComPtr<IDXGIFactory2> g_factory[3];
std::mutex g_switchLock;               // held by a switch; the main thread's markers wait for it
int g_targetFor = -1, g_target = 0;    // render thread: the pick Target last answered, and its answer
std::atomic<int> g_lastMarked{0};      // the last frame the main thread marked
int g_graceUntil = 0;                  // render thread: frames up to this one were marked before the last switch
// The plugin says it is there when it loads, before the game's first present. Without it nothing gives frame generation its inputs or
// its settings, so at the first present the bridge parks: the running backend is unloaded and the display is a plain swap chain.
std::atomic<bool> g_pluginConnected{false}, g_parked{false};

const char *Name(int kind) { return kind == 1 ? "DLSS Frame Generation" : "AMD FSR Frame Generation"; }
Backend &Of(int kind) { return kind == 1 ? DlssBackend() : FsrBackend(); }

// Readback.
std::mutex g_reasonLock;
std::string g_reason = "not started";

constexpr const char *kSkipNames[SkipCount] = {"no-inputs", "switched-off", "menu", "resizing", "unsupported", "switching", "other-frame", "markers-missing", "no-token", "status"};
std::atomic<long long> g_switches{0}, g_skips[SkipCount]{};
std::atomic<bool> g_skipReported[SkipCount]{};
std::set<std::string> g_reasonsSeen;
std::atomic<int> g_presenting{0}; // the frame whose render submission started last (render thread)

std::array<SlotInputs, 4> g_slots;
// RequestRelease bumps the epoch; each slot drops its shared inputs at its next present, once the present that last used them is over.
std::atomic<unsigned> g_releaseEpoch{0};
bool g_releaseOff = false; // render thread: the resources frame generation kept while off are to go too
std::mutex g_pendingLock;
FrameParams g_pending{};
bool g_hasPending = false;

// A defect that leaves one present out is tried again at the next present. A second defect within 120 presents of the last holds
// frame generation off for 120 presents (about a second or two) before the next try, so a defect on every other present cannot
// switch frame generation on and off present by present: each switch back to plain presenting can cost a displayed frame.
constexpr long long kDefectWindow = 120;
long long g_lastDefect = -kDefectWindow, g_heldUntil = 0;
Skip g_heldFor = SkipOtherFrame;
} // namespace

// The reason shown in the readback and on the settings screen (empty: on). A change is a developer line the first time that reason
// occurs and for the first sixteen changes: a present-by-present alternation must not fill the log, and the counters carry the totals.
void SetReason(const char *reason)
{
    std::lock_guard<std::mutex> lock(g_reasonLock);
    if (g_reason == reason) return;
    bool switched = g_reason.empty() != !*reason; // on to off or off to on, not one reason for another
    g_reason = reason;
    bool first = g_reasonsSeen.insert(g_reason).second;
    if (++g_switches > 16 && !first) return;
    char counters[512] = "";
    if (switched) Counters(counters, sizeof counters);
    if (*reason) BridgeLog(LogInfo, "Frame generation off: %s.%s%s", reason, switched ? " So far: " : "", counters);
    else BridgeLog(LogInfo, "Frame generation on. So far: %s", counters);
}

// Counts a present that went out without a generated frame; a defect is written to the error log the first time, with what was seen.
void Skipped(Skip why, const char *detail)
{
    ++g_skips[why];
    if (why < SkipOtherFrame || g_skipReported[why].exchange(true)) return;
    BridgeLog(LogSkipped, "Frame generation left a present out (%s): %s. Later ones of this kind are only counted.", kSkipNames[why],
              detail ? detail : "no detail");
}

void Defect(Skip why)
{
    long long now = g_presents;
    if (now - g_lastDefect < kDefectWindow) g_heldUntil = now + kDefectWindow, g_heldFor = why;
    g_lastDefect = now;
}

void Fail(const char *what)
{
    if (g_failed.exchange(true)) return;
    // Any thread: the render thread switches frame generation off at its next present.
    BridgeLog(LogFailure, "Frame generation stopped: %s; the game keeps presenting without it.", what);
    SetReason(what);
}

void Prefer(int method) { g_preferred = method; }

namespace
{
// The backend the player's pick wants among those that can run: AMD's first for FSR, else DLSS's first. AMD's starts the first time it
// is wanted once the device exists (Streamline cannot start that late, so DLSS's started with the swap chain or never).
int Want()
{
    const int first = g_preferred == 2 ? 2 : 1;
    for (int kind : {first, 3 - first})
    {
        if (g_broken[kind]) continue;
        if (!g_started[kind] && kind == 2 && g_bound)
        {
            std::string reason;
            try
            {
                if (!FsrBackend().Start(g_folder.c_str(), g_adapter.Get(), reason)) throw std::runtime_error(reason);
                FsrBackend().Bind(g_device.Get());
                g_started[2] = &FsrBackend();
            }
            catch (const std::exception &error)
            {
                Broken(2, error.what());
                continue;
            }
        }
        if (g_started[kind]) return kind;
    }
    return 0;
}
} // namespace

bool Start(const wchar_t *folder, IDXGIAdapter *adapter)
{
    // Once per process: a second swap chain gets the first answer.
    static int attempted = 0;
    if (attempted) return attempted == 1;
    attempted = 2;
    g_folder = folder;
    g_adapter = adapter;
    // Streamline must start before the bridge's D3D12 device is made, so DLSS Frame Generation starts now wherever it can run, whatever
    // the player picked, and stays ready for a switch. AMD's starts now when it is picked or DLSS's cannot run, else when first picked.
    std::string why[3];
    if (DlssBackend().Start(folder, adapter, why[1])) g_started[1] = &DlssBackend();
    else g_broken[1] = true;
    if (g_preferred == 2 || !g_started[1])
    {
        if (FsrBackend().Start(folder, adapter, why[2])) g_started[2] = &FsrBackend();
        else g_broken[2] = true;
    }
    const int first = g_preferred == 2 ? 2 : 1, kind = Want();
    if (!kind)
    {
        std::string reason = std::string("DLSS: ") + why[1] + "; AMD FSR: " + why[2];
        BridgeLog(LogFailure, "Frame generation is not available (%s). The game presents without it.", reason.c_str());
        SetReason(reason.c_str());
        g_failed = true;
        return false;
    }
    if (kind != first) BridgeLog(LogInfo, "%s is not available (%s); %s runs.", Name(first), why[first].c_str(), Name(kind));
    g_backend = g_started[kind];
    attempted = 1;
    SetReason("waiting for the first frame");
    return true;
}

void Bind(ID3D12Device *device)
{
    g_device = device;
    Backend *running = g_backend;
    running->Bind(device);
    g_bound = true;
    // The other backend that started is bound too, and kept out of the way until a switch: DLSS Frame Generation unloaded, Reflex off.
    for (int kind = 1; kind <= 2; kind++)
    {
        if (!g_started[kind] || g_started[kind] == running) continue;
        try
        {
            g_started[kind]->Bind(device);
            g_started[kind]->Unload();
        }
        catch (const std::exception &error)
        {
            Broken(kind, error.what());
        }
    }
}

int Target()
{
    if (!g_backend.load() || !g_bound || g_failed) return Kind();
    if (!g_pluginConnected) return 0;
    const int preferred = g_preferred;
    if (preferred == g_targetFor) return g_target;
    g_targetFor = preferred;
    const int wanted = Want();
    g_target = wanted ? wanted : g_backend.load()->Kind();
    return g_target;
}

void Upgrade(int kind, ID3D12Device *device, ID3D12Device **queueDevice, IDXGIFactory2 **factory)
{
    if (!g_queueDevice[kind]) Of(kind).Upgrade(device, &g_queueDevice[kind], &g_factory[kind]);
    g_queueDevice[kind].CopyTo(queueDevice);
    g_factory[kind].CopyTo(factory);
}

bool CreateDisplay(int kind, ID3D12CommandQueue *queue, HWND window, const DXGI_SWAP_CHAIN_DESC1 *desc, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC *fullscreen,
                   IDXGIFactory2 *factory, IDXGISwapChain1 **chain)
{
    return Of(kind).CreateDisplay(queue, window, desc, fullscreen, factory, chain);
}

void ReleaseDisplay()
{
    if (Backend *running = g_backend; running && g_bound && !g_parked) running->ReleaseDisplay();
}

void PluginConnected() { g_pluginConnected = true; }

bool Unclaimed() { return g_backend.load() && g_bound && !g_failed && !g_parked && !g_pluginConnected; }

bool Parked() { return g_parked; }

bool Connected() { return g_pluginConnected; }

std::mutex &SwitchLock() { return g_switchLock; }

void Leave()
{
    if (Backend *running = g_backend; running && g_bound && !g_parked) running->Unload();
}

void Enter(int kind)
{
    g_targetFor = -1;
    if (!kind)
    {
        g_parked = true;
        BridgeLog(LogFailure, "The plugin did not load before the game's first frame (a missing dependency or a load error, see above); frame generation is "
                              "not used and the bridge passes the game's frames through to a plain swap chain.");
        SetReason("the plugin did not load");
        return;
    }
    g_parked = false;
    g_backend = g_started[kind];
    // The frames the main thread marked before now carry no markers of the new backend: their presents are left out as switching.
    g_graceUntil = g_lastMarked;
    g_heldUntil = 0;
    g_started[kind]->Load();
}

void Broken(int kind, const char *why)
{
    g_broken[kind] = true;
    g_targetFor = -1;
    BridgeLog(LogSwitchFailed, "%s could not take over (%s); frame generation stays with what runs, and %s is not tried again until the game restarts.", Name(kind), why,
              Name(kind));
}

int Kind()
{
    Backend *running = g_backend;
    return running && g_bound && !g_failed && !g_parked ? running->Kind() : 0;
}

bool Running() { return g_backend.load() && g_bound && !g_failed && !g_parked; }

void Submit(const FrameParams *params)
{
    if (!Running() || !params) return;
    if (params->size != sizeof(FrameParams) || params->version != kParamsVersion) return Fail("the plugin and the native bridge disagree on the frame layout");
    std::lock_guard<std::mutex> lock(g_pendingLock);
    g_pending = *params;
    g_hasPending = true;
}

// Reflex belongs to DLSS Frame Generation; the setting is kept there whichever backend runs.
void SetReflex(int boost, int frameLimit) { DlssBackend().SetReflex((boost ? 1u : 0u) | ((unsigned)std::max(frameLimit, 0) << 1)); }

void SetHud(int on) { g_hudWanted = on != 0; }

void RenderSubmitStart(int frame)
{
    if (!Running()) return;
    g_presenting = frame;
    g_backend.load()->RenderSubmitStart(frame);
}

void Marker(int marker, int frame)
{
    if (!Running()) return;
    // Main thread: not while the render thread switches backends (Streamline asks for no other graphics call meanwhile).
    std::lock_guard<std::mutex> lock(g_switchLock);
    g_lastMarked = frame;
    g_backend.load()->Marker(marker, frame);
}

bool BeforeSignal(ID3D11Device *device11, ID3D11DeviceContext *context, ID3D12Device *device12, unsigned slot, const DXGI_SWAP_CHAIN_DESC1 &desc)
{
    if (!Running() || slot >= g_slots.size()) return false;
    // Whatever this present brings, the slot's inputs of two presents ago are over: a present without a new frame of the world camera
    // must not find them and tag them as this frame's.
    auto &inputs = g_slots[slot];
    if (const unsigned epoch = g_releaseEpoch.load(); inputs.epoch != epoch)
    {
        inputs = SlotInputs{};
        inputs.epoch = epoch;
        g_releaseOff = true;
        pass::Forget(pass::Depth, pass::HudOnWhite);
    }
    inputs.filled = inputs.hasHud = false;
    inputs.empty = SkipNoInputs;
    FrameParams params;
    {
        std::lock_guard<std::mutex> lock(g_pendingLock);
        if (!g_hasPending)
        {
            // No frame of the world camera: the views on the plugin's textures go, so textures the plugin has released are freed.
            pass::Forget(pass::Depth, pass::HudOnWhite);
            return false;
        }
        params = g_pending;
        g_hasPending = false;
    }
    if (params.enabled != 1)
    {
        if (params.enabled == 0)
        {
            // Switched off by the player: this slot's shared inputs go (its previous present is over), and with them the views on the plugin's textures.
            const unsigned epoch = inputs.epoch;
            inputs = SlotInputs{};
            inputs.epoch = epoch;
            pass::Forget(pass::Depth, pass::HudOnWhite);
        }
        inputs.empty = params.enabled == 2 ? SkipMenu : SkipSwitchedOff;
        return false;
    }
    if (!params.depth || !params.motion || !params.hudless) return false;
    // The game's picture fills its back buffer at the back buffer's size, or scaled up from a smaller resolution in a borderless window
    // (1920x1080 on a 3840x2160 screen): the screen-size inputs are then drawn up to the back buffer's size, as the game scales its picture.
    // A picture of another shape (more than a pixel's rounding, as 1366x768 on 1920x1080) is a resize under way, or one the game
    // letterboxes, which these inputs cannot describe.
    const int64_t across = int64_t(params.outputWidth) * desc.Height, down = int64_t(params.outputHeight) * desc.Width;
    if (!params.outputWidth || !params.outputHeight || std::llabs(across - down) > int64_t(std::max(desc.Width, desc.Height)))
    {
        inputs.empty = SkipResizing;
        return false;
    }
    try
    {
        pass::Prepare(device11);
        EnsureShared(inputs.depth, device11, device12, params.renderWidth, params.renderHeight, DXGI_FORMAT_R32_FLOAT);
        EnsureShared(inputs.motion, device11, device12, params.renderWidth, params.renderHeight, DXGI_FORMAT_R16G16_FLOAT);
        EnsureShared(inputs.hudless, device11, device12, desc.Width, desc.Height, DXGI_FORMAT_R8G8B8A8_UNORM);
        bool hud = params.hudOnBlack && params.hudOnWhite;
        if (hud) EnsureShared(inputs.hud, device11, device12, desc.Width, desc.Height, DXGI_FORMAT_R8G8B8A8_UNORM);
        else pass::Forget(pass::HudOnBlack, pass::HudOnWhite);
        bool flip = params.flipRows != 0;
        pass::Scope scope(context);
        scope.Draw(pass::Copy, flip, pass::Depth, params.depth, pass::Depth, nullptr, inputs.depth.target.Get(), inputs.depth.width, inputs.depth.height);
        scope.Draw(pass::Copy, flip, pass::Motion, params.motion, pass::Motion, nullptr, inputs.motion.target.Get(), inputs.motion.width, inputs.motion.height);
        scope.Draw(params.encodeSrgb ? pass::EncodeSrgb : pass::Copy, flip, pass::Hudless, params.hudless, pass::Hudless, nullptr, inputs.hudless.target.Get(),
                   inputs.hudless.width, inputs.hudless.height);
        if (hud)
            scope.Draw(pass::Hud, flip, pass::HudOnBlack, params.hudOnBlack, pass::HudOnWhite, params.hudOnWhite, inputs.hud.target.Get(), inputs.hud.width,
                       inputs.hud.height);
        inputs.hasHud = hud;
    }
    catch (const std::exception &error)
    {
        Fail(error.what());
        return false;
    }
    inputs.params = params;
    inputs.filled = true;
    g_releaseOff = false;
    return true;
}

void Prepare(unsigned slot, const DXGI_SWAP_CHAIN_DESC1 &desc, UINT interval, ID3D12Resource *backbuffer)
{
    Backend *backend = g_backend;
    if (!backend || !g_bound || g_parked) return;
    if (g_failed)
    {
        backend->Off();
        return;
    }
    try
    {
        ++g_presents;
        int frame = g_presenting;
        backend->PresentBegins(frame);
        const SlotInputs *inputs = slot < g_slots.size() ? &g_slots[slot] : nullptr;
        Skip why = SkipCount;
        const char *off = nullptr;
        char detail[224]{};
        if (!inputs || !inputs->filled)
        {
            why = inputs ? inputs->empty : SkipNoInputs;
            off = why == SkipSwitchedOff ? "frame generation is switched off"
                  : why == SkipMenu      ? "a full-screen menu or the map is open"
                  : why == SkipResizing  ? "the window is being resized"
                                         : "the world camera draws no frame (a menu, loading, or DLSS off)";
            g_heldUntil = 0; // a pause ends a hold: the next frame with inputs tries again
        }
        else
        {
            backend->Admit(*inputs, frame, slot, why, off, detail, sizeof detail);
            // A frame the main thread marked before the switch has no markers of the new backend: not a defect.
            if (off && why >= SkipOtherFrame && inputs->params.frame <= g_graceUntil && frame <= g_graceUntil)
                why = SkipSwitching, off = "the frame generation backend is being switched";
        }
        backend->Supported(desc, interval, why, off);
        bool held = !off && g_presents < g_heldUntil;
        if (held) why = g_heldFor, off = "held off after repeated defects (the error log names the first)";
        if (off)
        {
            Skipped(why, detail);
            if (why >= SkipOtherFrame && !held) Defect(why);
            backend->LeaveOut(frame, desc, backbuffer);
            backend->Off(why == SkipSwitchedOff || g_releaseOff);
            SetReason(off);
            return;
        }
        backend->Use(*inputs, slot, desc, interval, backbuffer);
        SetReason("");
    }
    catch (const std::exception &error)
    {
        Fail(error.what());
    }
}

void PresentMarker(bool start)
{
    if (!Running()) return;
    g_backend.load()->PresentMarker(start);
}

void After()
{
    if (!Running()) return;
    g_backend.load()->After();
}

void Suspend()
{
    Backend *backend = g_backend;
    if (!backend || !g_bound || g_parked) return;
    backend->Off();
    g_heldUntil = 0;
    std::lock_guard<std::mutex> lock(g_pendingLock);
    g_hasPending = false;
}

void RequestRelease() { ++g_releaseEpoch; }

void ReleaseInputs()
{
    for (auto &slot : g_slots) slot = SlotInputs{};
}

int State(char *reason, int capacity, long long *frames, long long *generated, int *lastPresented)
{
    if (reason && capacity > 0)
    {
        std::lock_guard<std::mutex> lock(g_reasonLock);
        int length = (int)std::min<size_t>(g_reason.size(), (size_t)capacity - 1);
        std::memcpy(reason, g_reason.data(), length);
        reason[length] = '\0';
    }
    if (frames) *frames = g_frames;
    if (generated) *generated = g_generated;
    if (lastPresented) *lastPresented = g_lastPresented;
    if (!Running()) return 0;
    return g_backend.load()->On() ? 2 : 1;
}

int Counters(char *text, int capacity)
{
    if (!text || capacity <= 0) return 0;
    int length = std::snprintf(text, (size_t)capacity, "presents %lld, with frame generation on %lld, generated %lld, reason changes %lld, markers refused %lld; left out:",
                               g_presents.load(), g_frames.load(), g_generated.load(), g_switches.load(), g_markerRefusals.load());
    for (int why = 0; why < SkipCount && length > 0 && length < capacity; why++)
        length += std::snprintf(text + length, (size_t)(capacity - length), " %s %lld", kSkipNames[why], g_skips[why].load());
    for (Backend *started : g_started)
        if (started && length > 0 && length < capacity) length += started->CountersTail(text + length, capacity - length);
    return std::min(length, capacity - 1);
}
} // namespace fg

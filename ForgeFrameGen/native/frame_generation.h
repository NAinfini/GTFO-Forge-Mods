// ForgeFrameGen frame generation (Docs/forge-upscale/PLAN.md §3.3, stage 4b) on the present bridge's D3D12 device: NVIDIA DLSS Frame
// Generation with Reflex and PCL through Streamline, or, on a card that cannot run it, AMD FSR Frame Generation through the AMD FSR SDK's
// frame generation swap chain (frame_generation_backend.h). present_bridge.cpp owns the swap chain and calls in here; forge_framegen.cpp
// forwards the plugin's render events.
#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <cstdint>
#include <mutex>

// The present bridge's log queue (present_bridge.cpp), drained by the plugin. An info line is a developer log and is dropped at once
// while developer logs are off; a failure reaches the error log as framegen-frame-bridge-failed, a skipped line as
// framegen-frame-generation-skipped (frame generation left a present out for a reason that is a defect, written once per reason), a
// failed switch of frame generation as framegen-frame-generation-switch-failed.
enum BridgeLogKind : int
{
    LogInfo = 0,
    LogFailure = 1,
    LogSkipped = 2,
    LogSwitchFailed = 3,
};
void BridgeLog(int kind, const char *format, ...);

namespace fg
{
// One frame's inputs, filled by the plugin on the main thread and handed to the render thread by a render event (layout shared with
// DlssBridge.FrameGenerationParams). Matrices are Unity's Matrix4x4 memory (column-major), which is Streamline's row-vector layout.
#pragma pack(push, 8)
struct FrameParams
{
    uint32_t size;
    uint32_t version;
    int32_t frame;            // Time.frameCount the frame was simulated in
    int32_t enabled;          // 0 the player's setting is off; 1 on; 2 on, and left out while a full-screen menu or the map is open
    void *depth;              // render-size raw device depth (ID3D11Texture2D*, R32 family), rows in Unity render-texture order
    void *motion;             // render-size motion (R16G16 family), current minus previous in UV units
    void *hudless;            // output-size colour without the HUD (R16G16B16A16 family, linear)
    void *hudOnBlack;         // output-size: the game's HUD compositor's picture of a black frame (R8G8B8A8 family), or null
    void *hudOnWhite;         // the same of a white frame; with both, the HUD's colour and opacity are tagged for Streamline
    uint32_t renderWidth, renderHeight, outputWidth, outputHeight;
    float viewToClip[16];     // unjittered GPU projection in swap-chain space
    float clipToView[16];
    float clipToPrevClip[16];
    float prevClipToClip[16];
    float jitterX, jitterY;   // render pixels, swap-chain space
    float motionScaleX, motionScaleY;
    float cameraPosition[3], cameraUp[3], cameraRight[3], cameraForward[3];
    float cameraNear, cameraFar, cameraFov, cameraAspect;
    int32_t reset;
    int32_t depthInverted;
    int32_t flipRows;         // 1: the inputs are flipped into swap-chain row order (development switch for the first in-game check)
    int32_t encodeSrgb;       // 1: the HUD-less colour is linear and is encoded like the back buffer
};
#pragma pack(pop)
static_assert(sizeof(FrameParams) == 424, "fg::FrameParams layout is shared with the plugin");

// The upscaler the player picked (the plugin's Method: 0 Auto, 1 DLSS, 2 FSR), any time: the preloader before the swap chain is made,
// the plugin whenever the setting changes. With FSR, frame generation is AMD's even on a card DLSS Frame Generation runs on; otherwise
// DLSS Frame Generation where it runs. The bridge follows at its next present (Target).
void Prefer(int method);
// Once per process, when the game makes its swap chain, before the bridge's D3D12 device exists: starts DLSS Frame Generation where it
// can run (the packaged Streamline runtime from folder, its signature checked; Streamline must start before the device is made), and
// AMD FSR Frame Generation (the packaged AMD FSR SDK DLLs) when FSR is picked or DLSS Frame Generation cannot run; otherwise AMD's starts
// when it is first picked. False: neither runs and frame generation stays off (the reasons are logged once).
bool Start(const wchar_t *folder, IDXGIAdapter *adapter);
// After the bridge's D3D12 device exists: binds it to every backend that started (DLSS: takes the feature functions, turns Reflex on)
// and makes the one the player's pick wants the running one. Throws on failure.
void Bind(ID3D12Device *device);
// The frame generation the player's pick wants now (Kind() when nothing is to change): AMD FSR Frame Generation for FSR, else DLSS
// Frame Generation, each where it can run, else the other. Starts AMD's the first time it is wanted; a backend that failed to start
// or to take the display swap chain is not wanted again. Render thread, cheap when nothing changed.
int Target();
// The device for the present queue and the DXGI factory for kind's display swap chain (DLSS: Streamline's proxies of both, made once).
// Throws on failure.
void Upgrade(int kind, ID3D12Device *device, ID3D12Device **queueDevice, IDXGIFactory2 **factory);
// Makes the display swap chain when kind's backend owns it (FSR: the SDK's frame generation swap chain, which makes the real one on
// window); false: the bridge makes it from factory as usual. Throws on failure.
bool CreateDisplay(int kind, ID3D12CommandQueue *queue, HWND window, const DXGI_SWAP_CHAIN_DESC1 *desc, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC *fullscreen,
                   IDXGIFactory2 *factory, IDXGISwapChain1 **chain);
// The bridge goes away or switches (its queue drained): what the running backend made on the display swap chain goes before the chain.
void ReleaseDisplay();
// A switch of backend, render thread, with SwitchLock held: Leave after the bridge let go of the running backend's display swap chain,
// Enter (throws on failure) before it makes kind's. Broken: kind failed to take over (logged once with the reason); it is not wanted again.
std::mutex &SwitchLock();
void Leave();
void Enter(int kind); // 0: park (no backend runs, the display is a plain swap chain) until the plugin is there
// The plugin loaded (ForgeFrameGen_PluginConnected), which it does before the game's first present. Until then Target is 0, and
// Unclaimed says the bridge, which makes the backend's display at the game's start, is to park at its first present; Parked, that it has.
void PluginConnected();
bool Unclaimed();
bool Parked();
bool Connected();
void Broken(int kind, const char *why);
// 0 none, 1 DLSS Frame Generation, 2 AMD FSR Frame Generation.
int Kind();
bool Running();
// Stops frame generation for good after a failure (logged once).
void Fail(const char *what);

// Render-event entry points (render thread): the frame's inputs, and the start of its render submission.
void Submit(const FrameParams *params);
void RenderSubmitStart(int frame);
// Main thread: Reflex sleep and simulation markers.
void Marker(int marker, int frame);
// Any thread: Reflex as the player set it (boost: On + Boost; frameLimit: frames a second, 0 none). It takes effect before the next
// frame's sleep; low latency itself is always on while frame generation is loaded. DLSS Frame Generation only.
void SetReflex(int boost, int frameLimit);
// Any thread: the player's "HUD in generated frames". DLSS gets the HUD measured by the plugin; FSR gets the HUD-less colour to keep the
// HUD out of its interpolation, only while this is on.
void SetHud(int on);

// The bridge's per-present work. BeforeSignal runs on the D3D11 context before the producer fence is signalled and converts the frame's
// inputs into slot's shared textures; it returns false when frame generation is not used for this present. Prepare (D3D12 side,
// before the display Present) sets constants, tags and options; Present markers wrap the display Present; After reads the state.
// The slot's textures are written again only after the bridge has waited for the fence it signals on the present queue behind that
// slot's last Present: frame generation blocks that queue until it has read its inputs (eBlockPresentingClientQueue).
bool BeforeSignal(ID3D11Device *device11, ID3D11DeviceContext *context, ID3D12Device *device12, unsigned slot, const DXGI_SWAP_CHAIN_DESC1 &desc);
void Prepare(unsigned slot, const DXGI_SWAP_CHAIN_DESC1 &desc, UINT interval, ID3D12Resource *backbuffer);
void PresentMarker(bool start);
void After();
// The swap chain is resized or the bridge goes away: frame generation off, slot inputs released (the caller has drained the queue).
void Suspend();
void ReleaseInputs();
// Any thread: the shared inputs and the options' resources go, a back buffer slot at a time as the presents go on (the plugin's pass is off).
void RequestRelease();

// Readback for the plugin: 0 not loaded, 1 loaded and off, 2 on; the reason it is off (empty when on); totals.
int State(char *reason, int capacity, long long *frames, long long *generated, int *lastPresented);
// One line of counters since start: presents seen, presents left out per reason, switches between on and off, markers refused.
int Counters(char *text, int capacity);

// Offline check only (AMD FSR Frame Generation): while watching, every frame the SDK's swap chain shows, rendered or generated, goes
// through the bridge's present callback, which keeps its middle row (up to 128 frames). ShownFrames waits for the presents, copies the
// rows (width * 4 bytes each, in the order shown) and whether each was generated; returns how many.
void WatchShown(bool on);
int ShownFrames(unsigned char *rows, int capacity, int *generated, unsigned *width);
} // namespace fg

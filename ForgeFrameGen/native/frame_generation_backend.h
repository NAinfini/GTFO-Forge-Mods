// ForgeFrameGen frame generation backends (Docs/forge-upscale/PLAN.md §3.3). frame_generation.cpp is the shared layer: the frame's
// inputs drawn into textures shared with D3D12 (one set per back buffer slot), why a present is left out, the counters and the log
// lines. A backend hands those inputs to one vendor's frame generation: NVIDIA DLSS Frame Generation with Reflex and PCL through
// Streamline (dlss_frame_generation.cpp), or AMD FSR Frame Generation through the AMD FSR SDK's frame generation swap chain
// (fsr_frame_generation.cpp). One backend runs at a time; the player's upscaler picks it, and the bridge moves the display swap chain from
// one to the other while the game runs (present_bridge.cpp, Switch).
#pragma once
#include "frame_generation.h"
#include "bridge_common.h"
#include <atomic>
#include <cstddef>

namespace fg
{
// Why a present went out without a generated frame. The first five are states a player can be in (a menu, the setting, the map, a
// resize, a small window); the rest are defects of this integration and are written to the error log, once per reason.
enum Skip : int
{
    SkipNoInputs,       // the world camera drew no frame for this present (menu, loading, DLSS off)
    SkipSwitchedOff,    // the player's frame generation setting is off
    SkipMenu,           // a full-screen menu or the map lies over the world the camera still draws
    SkipResizing,       // the inputs are of another size than the swap chain
    SkipUnsupported,    // window too small, no generated frames on this GPU, VSync without support
    SkipSwitching,      // the frame was simulated before the backend changed, so the new one lacks its markers
    SkipOtherFrame,     // the inputs belong to another frame than the one presented
    SkipMarkersMissing, // the frame lacks its simulation or render-submit markers
    SkipNoToken,        // Streamline gave no frame token
    SkipStatus,         // frame generation reported a failed status
    SkipCount,
};

// One back buffer slot's inputs, drawn on the D3D11 side and read by the backend on the D3D12 queue.
struct SlotInputs
{
    Shared depth, motion, hudless, hud;
    FrameParams params{};
    bool filled = false, hasHud = false;
    unsigned epoch = 0;        // the release request this slot has answered
    Skip empty = SkipNoInputs; // why the slot holds no inputs for its present
};

// The shared layer's services to the backends.
extern std::atomic<long long> g_presents, g_frames, g_generated, g_markerRefusals;
extern std::atomic<int> g_lastPresented;
extern std::atomic<bool> g_hudWanted; // the player's "HUD in generated frames" (ForgeFrameGen_SetHudInGeneratedFrames)
void SetReason(const char *reason);
void Skipped(Skip why, const char *detail = nullptr);
void Defect(Skip why);

class Backend
{
public:
    virtual ~Backend() = default;
    // Loads and checks the vendor runtime for adapter. False with the reason in why; nothing of it stays in use.
    virtual bool Start(const wchar_t *folder, IDXGIAdapter *adapter, std::string &why) = 0;
    // The bridge's D3D12 device exists. Throws on failure.
    virtual void Bind(ID3D12Device *device) = 0;
    // The device for the present queue and the DXGI factory for the display swap chain. Throws on failure.
    virtual void Upgrade(ID3D12Device *device, ID3D12Device **queueDevice, IDXGIFactory2 **factory) = 0;
    // Makes the display swap chain itself (the vendor's own swap chain on that window); false: the bridge makes it.
    virtual bool CreateDisplay(ID3D12CommandQueue *, HWND, const DXGI_SWAP_CHAIN_DESC1 *, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC *, IDXGIFactory2 *,
                               IDXGISwapChain1 **)
    {
        return false;
    }
    // The bridge goes away or moves to the other backend: everything the backend made on the display swap chain goes before it (the
    // queue is drained).
    virtual void ReleaseDisplay() {}
    // Around a switch of backend: Unload after the bridge let go of this backend's display swap chain, Load before it makes one again.
    // Throw on failure.
    virtual void Unload() {}
    virtual void Load() {}
    // 1 DLSS Frame Generation, 2 AMD FSR Frame Generation (ForgeFrameGen_FrameGenerationBackend).
    virtual int Kind() const = 0;
    virtual bool On() const = 0;

    // Reflex and the PCL markers (DLSS Frame Generation only).
    virtual void Marker(int, int) {}
    virtual void RenderSubmitStart(int) {}
    virtual void PresentMarker(bool) {}
    virtual void SetReflex(unsigned) {}

    // One present, on the render thread, in this order: PresentBegins; Admit (inputs are filled) and Supported, each setting off and
    // why when the present is left out; then LeaveOut and Off, or Use; Present; After.
    virtual void PresentBegins(int) {}
    virtual void Admit(const SlotInputs &inputs, int frame, unsigned slot, Skip &why, const char *&off, char *detail, size_t capacity) = 0;
    virtual void Supported(const DXGI_SWAP_CHAIN_DESC1 &, UINT, Skip &, const char *&) {}
    virtual void LeaveOut(int, const DXGI_SWAP_CHAIN_DESC1 &, ID3D12Resource *) {}
    // Off for now (release false: resources kept), or for long (release: resources freed).
    virtual void Off(bool release = false) = 0;
    // Frame generation on for this present with slot's inputs. Throws on failure.
    virtual void Use(const SlotInputs &inputs, unsigned slot, const DXGI_SWAP_CHAIN_DESC1 &desc, UINT interval, ID3D12Resource *backbuffer) = 0;
    virtual void After() = 0;
    // The end of the counters line (the backend's own counts).
    virtual int CountersTail(char *, int) { return 0; }
};

Backend &DlssBackend();
Backend &FsrBackend();
} // namespace fg

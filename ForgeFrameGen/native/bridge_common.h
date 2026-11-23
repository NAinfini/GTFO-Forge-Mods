// ForgeFrameGen native helpers shared by the frame generation inputs (frame_generation.cpp) and the FSR upscaler (fsr_upscale.cpp):
// textures shared between Unity's D3D11 device and a D3D12 device on the same adapter, and the check that only the packaged copy of a
// runtime library is in the process; and, with the upscaler start (forge_framegen.cpp), why DLSS does not run on the game's adapter.
#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdio>
#include <initializer_list>
#include <stdexcept>
#include <string>

// A texture made on the D3D11 device with an NT handle and opened on the D3D12 device: D3D11 draws or copies into it, D3D12 reads or
// writes it (D3D12_RESOURCE_STATE_COMMON between the two).
struct Shared
{
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture11;
    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> target;
    Microsoft::WRL::ComPtr<ID3D12Resource> texture12;
    UINT width = 0, height = 0;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
};

// Makes shared again when its size or format differs. extraBind adds bind flags (D3D11_BIND_UNORDERED_ACCESS for a texture D3D12
// writes). Throws std::runtime_error.
inline void EnsureShared(Shared &shared, ID3D11Device *device11, ID3D12Device *device12, UINT width, UINT height, DXGI_FORMAT format, UINT extraBind = 0)
{
    if (shared.texture11 && shared.width == width && shared.height == height && shared.format == format) return;
    shared = Shared{};
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE | extraBind;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    if (FAILED(device11->CreateTexture2D(&desc, nullptr, &shared.texture11))) throw std::runtime_error("shared input texture");
    if (FAILED(device11->CreateRenderTargetView(shared.texture11.Get(), nullptr, &shared.target))) throw std::runtime_error("shared input target");
    Microsoft::WRL::ComPtr<IDXGIResource1> resource;
    if (FAILED(shared.texture11.As(&resource))) throw std::runtime_error("shared input interface");
    HANDLE handle = nullptr;
    if (FAILED(resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &handle))) throw std::runtime_error("shared input handle");
    HRESULT opened = device12->OpenSharedHandle(handle, IID_PPV_ARGS(&shared.texture12));
    CloseHandle(handle);
    if (FAILED(opened)) throw std::runtime_error("opening a shared input on D3D12");
    shared.width = width;
    shared.height = height;
    shared.format = format;
}

// Why NVIDIA DLSS (super resolution or frame generation) does not run on the graphics card the game renders on; the plugin turns it
// into its fallback warning and settings hint (DlssBridge.Missing). The numbers are shared with the plugin.
enum DlssMissing : int
{
    DlssRuns = 0,       // it runs, or it was not tried (the player picked AMD FSR)
    DlssNotNvidia = 1,  // the game renders on a card that is not NVIDIA's
    DlssDriver = 2,     // the NVIDIA driver is too old
    DlssGpu = 3,        // this NVIDIA card has no such feature (no RTX; frame generation: not RTX 40 or 50)
    DlssScheduling = 4, // frame generation: Windows' hardware-accelerated GPU scheduling is off
    DlssWindows = 5,    // frame generation: Windows is too old
    DlssFailed = 6,     // anything else (a library missing or refused, a runtime call failed); the reason text says what
    DlssOtherCard = 7,  // the game renders on a card that is not NVIDIA's while this PC has an NVIDIA one (Windows picks the card)
};

// Failures the offline check (BridgeCheck --fault) makes happen, so that the way back to the game's own rendering runs on a PC where
// nothing fails (ForgeFrameGen_TestFaults; never set in the game): the bridge's D3D12 device cannot be made; the next present of the
// bridge fails; the game's adapter reads as Intel's (vendor 0x8086), as on a card DLSS does not run on.
enum TestFault : unsigned
{
    FaultBridgeDevice = 1,
    FaultPresent = 2,
    FaultVendor = 4,
};
inline std::atomic<unsigned> g_testFaults{0};

// An NVIDIA adapter of this PC other than the one the game renders on, by name (UTF-8), or empty: on such a PC Windows' graphics
// preference put the game on another card (an integrated GPU, say), where DLSS cannot run. Reads the adapter's own factory; makes none.
inline std::string OtherNvidiaCard(IDXGIAdapter *adapter)
{
    DXGI_ADAPTER_DESC own{};
    Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
    if (!adapter || FAILED(adapter->GetDesc(&own)) || FAILED(adapter->GetParent(IID_PPV_ARGS(&factory)))) return {};
    Microsoft::WRL::ComPtr<IDXGIAdapter1> other;
    for (UINT i = 0; factory->EnumAdapters1(i, &other) != DXGI_ERROR_NOT_FOUND; i++, other.Reset())
    {
        DXGI_ADAPTER_DESC1 desc{};
        if (FAILED(other->GetDesc1(&desc)) || desc.VendorId != 0x10DE || (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) continue;
        if (desc.AdapterLuid.LowPart == own.AdapterLuid.LowPart && desc.AdapterLuid.HighPart == own.AdapterLuid.HighPart) continue;
        char name[256];
        WideCharToMultiByte(CP_UTF8, 0, desc.Description, -1, name, sizeof name, nullptr, nullptr);
        return name;
    }
    return {};
}

// The adapter the game renders on, for the log: its name and PCI vendor ID ("NVIDIA GeForce RTX 4080, vendor 0x10DE").
inline std::string AdapterText(IDXGIAdapter *adapter, UINT &vendor)
{
    DXGI_ADAPTER_DESC desc{};
    vendor = 0;
    if (!adapter || FAILED(adapter->GetDesc(&desc))) return "an unknown graphics card";
    vendor = g_testFaults & FaultVendor ? 0x8086 : desc.VendorId;
    char name[256];
    WideCharToMultiByte(CP_UTF8, 0, desc.Description, -1, name, sizeof name, nullptr, nullptr);
    char text[320];
    std::snprintf(text, sizeof text, "%s, vendor 0x%04X%s", name, vendor, vendor != desc.VendorId ? " (simulated)" : "");
    return text;
}

// A module whose file name starts with one of prefixes and that was not loaded from folder (another copy of a runtime this mod
// packages: an overlay, another mod) would share global state with ours. True and its path in found (UTF-8) when there is one.
inline bool ForeignModule(const std::wstring &folder, std::initializer_list<const wchar_t *> prefixes, std::string &found)
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
    if (snapshot == INVALID_HANDLE_VALUE) return false;
    MODULEENTRY32W item{};
    item.dwSize = sizeof item;
    bool foreign = false;
    std::wstring prefix = folder + L"\\";
    if (Module32FirstW(snapshot, &item)) do
        {
            bool named = false;
            for (const wchar_t *name : prefixes) named |= !_wcsnicmp(item.szModule, name, wcslen(name));
            if (named && _wcsnicmp(item.szExePath, prefix.c_str(), prefix.size()))
            {
                char path[MAX_PATH * 3];
                WideCharToMultiByte(CP_UTF8, 0, item.szExePath, -1, path, sizeof path, nullptr, nullptr);
                found = path;
                foreign = true;
            }
        } while (Module32NextW(snapshot, &item));
    CloseHandle(snapshot);
    return foreign;
}

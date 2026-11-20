// ForgeFrameGen native helpers shared by the frame generation inputs (frame_generation.cpp) and the FSR upscaler (fsr_upscale.cpp):
// textures shared between Unity's D3D11 device and a D3D12 device on the same adapter, and the check that only the packaged copy of a
// runtime library is in the process.
#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>
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

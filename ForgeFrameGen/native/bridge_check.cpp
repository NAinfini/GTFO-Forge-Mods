// Offline check of the present bridge (present_bridge.cpp): a window, a D3D11 device and a swap chain created the way Unity does,
// through the hooked imports; known images are copied into buffer 0, presented, and read back from the D3D12 back buffer, which must
// match byte for byte, before and after a resize. Where Streamline starts (an RTX GPU with hardware-accelerated GPU scheduling), frame
// generation is then driven the way the plugin drives it: markers, render events and inputs for each frame; every such present must
// run with frame generation on (the HUD's colour and opacity tagged with the other inputs, Reflex set to On + Boost with a frame rate
// limit), a present without inputs, a present under a menu and inputs of another frame must each be left out and counted, and
// Streamline must not miss a frame's constants or tags. Generated frames are not checked: none come in this window without a game,
// and the count printed here is only an observation. The swap chain is made and paced as GTFO's (flags 0x842, the frame-latency waitable
// object waited on before every present, latency 2), and the game's two threads are run through window resizes with VSync off and on:
// the main thread must never wait five seconds for the render thread, nor a wait on the waitable object take a second. The game's
// threads run once more with frame generation inputs from the render thread, switched off and on and through the resizes. Last, the
// frame generation is switched to the other backend and back while the game runs (Switching), presents one by one with resizes around
// the switches, then picked from the main thread while the game's threads run.
//
// Usage: ForgeFrameGen.BridgeCheck.exe <folder holding ForgeFrameGen.Native.dll> [--fsr] [--trace <file>]. --fsr asks for AMD FSR
// Frame Generation, as the plugin does when the player picked FSR (a native bridge without that choice is refused). --trace writes
// every Streamline call the bridge makes to <file>: in order, with its key arguments, where only one thread calls (the start, the
// byte-for-byte presents, the frame generation scenario), and counted per function for each scenario. Two native bridges driven by
// the same BridgeCheck must write the same file if their DLSS Frame Generation behaves the same.
#include "frame_generation.h"
#include "upscale_params.h"
#include <dxgi1_5.h>
#include <DirectXPackedVector.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <mutex>
#include <thread>
#include <string>
#include <type_traits>
#include <vector>

#include <sl.h>
#include <sl_dlss_g.h>
#include <sl_pcl.h>
#include <sl_reflex.h>

using Microsoft::WRL::ComPtr;

// The Streamline trace: the bridge's sl.interposer.dll is loaded first, from the same file, and its exports are pointed at the
// wrappers below (export table entries moved to jumps placed after the module); the feature functions slGetFeatureFunction hands
// out are swapped for wrappers too. Nothing of Streamline's behaviour changes: each wrapper notes the call and calls the original.
namespace trace
{
std::mutex g_lock;
std::vector<std::string> g_order;
std::map<std::string, std::map<std::string, long long>> g_counts; // scenario, function, calls
std::string g_scenario = "start";
bool g_ordered = true, g_on = false;

void Scenario(const char *name, bool ordered)
{
    std::lock_guard<std::mutex> lock(g_lock);
    g_scenario = name;
    g_ordered = ordered;
    if (g_on && ordered) g_order.push_back(std::string("== ") + name);
}

void Note(const char *function, const std::string &detail = {})
{
    std::lock_guard<std::mutex> lock(g_lock);
    g_counts[g_scenario][function]++;
    if (g_ordered) g_order.push_back(detail.empty() ? function : std::string(function) + " " + detail);
}

std::string Format(const char *format, ...)
{
    char text[512];
    va_list args;
    va_start(args, format);
    std::vsnprintf(text, sizeof text, format, args);
    va_end(args);
    return text;
}

unsigned Index(const sl::FrameToken &token) { return (uint32_t)token; }

uint64_t Hash(uint64_t hash, const void *data, size_t size)
{
    for (size_t i = 0; i < size; i++) hash = (hash ^ static_cast<const unsigned char *>(data)[i]) * 1099511628211ull;
    return hash;
}

PFun_slInit *o_init;
PFun_slShutdown *o_shutdown;
PFun_slSetD3DDevice *o_setDevice;
PFun_slIsFeatureSupported *o_supported;
PFun_slGetFeatureFunction *o_function;
PFun_slUpgradeInterface *o_upgrade;
PFun_slGetNewFrameToken *o_token;
PFun_slSetConstants *o_constants;
PFun_slSetTagForFrame *o_tag;
PFun_slFreeResources *o_free;
PFun_slDLSSGSetOptions *o_options;
PFun_slDLSSGGetState *o_state;
PFun_slReflexSetOptions *o_reflexOptions;
PFun_slReflexSleep *o_sleep;
PFun_slPCLSetMarker *o_marker;
PFun_slReflexGetState *o_reflexState;

sl::Result Init(const sl::Preferences &p, uint64_t sdkVersion)
{
    Note("slInit", Format("features %u flags 0x%llx api %d engine %d", p.numFeaturesToLoad, (unsigned long long)p.flags, (int)p.renderAPI, (int)p.engine));
    return o_init(p, sdkVersion);
}
sl::Result Shutdown()
{
    Note("slShutdown");
    return o_shutdown();
}
PFun_slSetFeatureLoaded *o_setLoaded;
sl::Result SetLoaded(sl::Feature feature, bool loaded)
{
    Note("slSetFeatureLoaded", Format("feature %u %s", (unsigned)feature, loaded ? "loaded" : "unloaded"));
    return o_setLoaded(feature, loaded);
}
sl::Result SetDevice(void *device)
{
    Note("slSetD3DDevice");
    return o_setDevice(device);
}
sl::Result Supported(sl::Feature feature, const sl::AdapterInfo &info)
{
    sl::Result result = o_supported(feature, info);
    Note("slIsFeatureSupported", Format("feature %u -> %d", (unsigned)feature, (int)result));
    return result;
}
sl::Result Upgrade(void **base)
{
    Note("slUpgradeInterface");
    return o_upgrade(base);
}
sl::Result Token(sl::FrameToken *&token, const uint32_t *index)
{
    sl::Result result = o_token(token, index);
    Note("slGetNewFrameToken", Format("frame %u -> %d", index ? *index : 0u, (int)result));
    return result;
}
sl::Result Constants(const sl::Constants &c, const sl::FrameToken &token, const sl::ViewportHandle &viewport)
{
    uint64_t hash = 14695981039346656037ull;
    hash = Hash(hash, &c.cameraViewToClip, sizeof c.cameraViewToClip);
    hash = Hash(hash, &c.clipToCameraView, sizeof c.clipToCameraView);
    hash = Hash(hash, &c.clipToLensClip, sizeof c.clipToLensClip);
    hash = Hash(hash, &c.clipToPrevClip, sizeof c.clipToPrevClip);
    hash = Hash(hash, &c.prevClipToClip, sizeof c.prevClipToClip);
    for (const void *v : {(const void *)&c.jitterOffset, (const void *)&c.mvecScale, (const void *)&c.cameraPinholeOffset}) hash = Hash(hash, v, sizeof(sl::float2));
    for (const void *v : {(const void *)&c.cameraPos, (const void *)&c.cameraUp, (const void *)&c.cameraRight, (const void *)&c.cameraFwd}) hash = Hash(hash, v, sizeof(sl::float3));
    for (float v : {c.cameraNear, c.cameraFar, c.cameraFOV, c.cameraAspectRatio, c.motionVectorsInvalidValue, c.minRelativeLinearDepthObjectSeparation}) hash = Hash(hash, &v, sizeof v);
    for (sl::Boolean v : {c.depthInverted, c.cameraMotionIncluded, c.motionVectors3D, c.reset, c.orthographicProjection, c.motionVectorsDilated, c.motionVectorsJittered})
        hash = Hash(hash, &v, sizeof v);
    Note("slSetConstants", Format("frame %u viewport %u reset %d constants %016llx", Index(token), (uint32_t)viewport, (int)c.reset, (unsigned long long)hash));
    return o_constants(c, token, viewport);
}
sl::Result Tag(const sl::FrameToken &token, const sl::ViewportHandle &viewport, const sl::ResourceTag *tags, uint32_t count, sl::CommandBuffer *buffer)
{
    std::string detail = Format("frame %u viewport %u tags %u:", Index(token), (uint32_t)viewport, count);
    for (uint32_t i = 0; tags && i < count; i++)
        detail += Format(" [type %u life %u %ux%u+%u+%u%s]", (unsigned)tags[i].type, (unsigned)tags[i].lifecycle, tags[i].extent.width, tags[i].extent.height,
                         tags[i].extent.left, tags[i].extent.top, tags[i].resource ? "" : " null");
    Note("slSetTagForFrame", detail + (buffer ? " with list" : ""));
    return o_tag(token, viewport, tags, count, buffer);
}
sl::Result Free(sl::Feature feature, const sl::ViewportHandle &viewport)
{
    Note("slFreeResources", Format("feature %u viewport %u", (unsigned)feature, (uint32_t)viewport));
    return o_free(feature, viewport);
}
sl::Result Options(const sl::ViewportHandle &viewport, const sl::DLSSGOptions &o)
{
    Note("slDLSSGSetOptions", Format("viewport %u mode %d flags 0x%x generate %u buffers %u color %ux%u fmt %u mvec %ux%u fmt %u depth fmt %u hudless fmt %u ui fmt %u",
                                     (uint32_t)viewport, (int)o.mode, (unsigned)o.flags, o.numFramesToGenerate, o.numBackBuffers, o.colorWidth, o.colorHeight,
                                     o.colorBufferFormat, o.mvecDepthWidth, o.mvecDepthHeight, o.mvecBufferFormat, o.depthBufferFormat, o.hudLessBufferFormat,
                                     o.uiBufferFormat));
    return o_options(viewport, o);
}
sl::Result State(const sl::ViewportHandle &viewport, sl::DLSSGState &state, const sl::DLSSGOptions *options)
{
    Note("slDLSSGGetState", options ? "with options" : "");
    return o_state(viewport, state, options);
}
sl::Result ReflexOptions(const sl::ReflexOptions &o)
{
    Note("slReflexSetOptions", Format("mode %d limit %u", (int)o.mode, o.frameLimitUs));
    return o_reflexOptions(o);
}
sl::Result Sleep(const sl::FrameToken &token)
{
    Note("slReflexSleep", Format("frame %u", Index(token)));
    return o_sleep(token);
}
sl::Result Marker(sl::PCLMarker marker, const sl::FrameToken &token)
{
    sl::Result result = o_marker(marker, token);
    Note("slPCLSetMarker", Format("marker %u frame %u -> %d", (unsigned)marker, Index(token), (int)result));
    return result;
}
sl::Result ReflexState(sl::ReflexState &state)
{
    Note("slReflexGetState");
    return o_reflexState(state);
}
sl::Result Function(sl::Feature feature, const char *name, void *&function)
{
    sl::Result result = o_function(feature, name, function);
    Note("slGetFeatureFunction", Format("feature %u %s -> %d", (unsigned)feature, name, (int)result));
    if (result != sl::Result::eOk || !function) return result;
    auto swap = [&](auto &original, auto wrapper) {
        original = reinterpret_cast<std::remove_reference_t<decltype(original)>>(function);
        function = reinterpret_cast<void *>(wrapper);
    };
    if (!std::strcmp(name, "slDLSSGSetOptions")) swap(o_options, &Options);
    else if (!std::strcmp(name, "slDLSSGGetState")) swap(o_state, &State);
    else if (!std::strcmp(name, "slReflexSetOptions")) swap(o_reflexOptions, &ReflexOptions);
    else if (!std::strcmp(name, "slReflexSleep")) swap(o_sleep, &Sleep);
    else if (!std::strcmp(name, "slPCLSetMarker")) swap(o_marker, &Marker);
    else if (!std::strcmp(name, "slReflexGetState")) swap(o_reflexState, &ReflexState);
    else Note("untraced feature function", name);
    return result;
}

// Points the export `name` of `module` at `wrapper` through a jump in `stubs` (after the module, within reach of a 32-bit RVA).
template <class T> bool Redirect(HMODULE module, unsigned char *&stubs, const char *name, T *&original, T *wrapper)
{
    auto *base = reinterpret_cast<unsigned char *>(module);
    auto *nt = reinterpret_cast<IMAGE_NT_HEADERS *>(base + reinterpret_cast<IMAGE_DOS_HEADER *>(base)->e_lfanew);
    auto &entry = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    auto *exports = reinterpret_cast<IMAGE_EXPORT_DIRECTORY *>(base + entry.VirtualAddress);
    auto *names = reinterpret_cast<DWORD *>(base + exports->AddressOfNames);
    auto *ordinals = reinterpret_cast<WORD *>(base + exports->AddressOfNameOrdinals);
    auto *functions = reinterpret_cast<DWORD *>(base + exports->AddressOfFunctions);
    for (DWORD i = 0; i < exports->NumberOfNames; i++)
    {
        if (std::strcmp(reinterpret_cast<const char *>(base + names[i]), name)) continue;
        DWORD *slot = &functions[ordinals[i]];
        original = reinterpret_cast<T *>(base + *slot);
        unsigned char jump[12] = {0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xE0}; // mov rax, wrapper; jmp rax
        auto target = reinterpret_cast<uint64_t>(wrapper);
        std::memcpy(jump + 2, &target, 8);
        std::memcpy(stubs, jump, sizeof jump);
        DWORD old = 0;
        if (!VirtualProtect(slot, sizeof *slot, PAGE_READWRITE, &old)) return false;
        *slot = (DWORD)(stubs - base);
        VirtualProtect(slot, sizeof *slot, old, &old);
        stubs += 16;
        return true;
    }
    return false;
}

bool Install(const std::wstring &folder)
{
    std::wstring path = folder + L"\\sl.interposer.dll";
    HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) return false;
    auto *base = reinterpret_cast<unsigned char *>(module);
    auto *nt = reinterpret_cast<IMAGE_NT_HEADERS *>(base + reinterpret_cast<IMAGE_DOS_HEADER *>(base)->e_lfanew);
    unsigned char *stubs = nullptr;
    for (uintptr_t at = (reinterpret_cast<uintptr_t>(base) + nt->OptionalHeader.SizeOfImage + 0xFFFF) & ~(uintptr_t)0xFFFF; !stubs && at < reinterpret_cast<uintptr_t>(base) + 0x7FFF0000ull;
         at += 0x10000)
        stubs = static_cast<unsigned char *>(VirtualAlloc(reinterpret_cast<void *>(at), 4096, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE));
    if (!stubs) return false;
    bool ok = Redirect(module, stubs, "slInit", o_init, &Init) && Redirect(module, stubs, "slShutdown", o_shutdown, &Shutdown) &&
              Redirect(module, stubs, "slSetD3DDevice", o_setDevice, &SetDevice) && Redirect(module, stubs, "slIsFeatureSupported", o_supported, &Supported) &&
              Redirect(module, stubs, "slGetFeatureFunction", o_function, &Function) && Redirect(module, stubs, "slUpgradeInterface", o_upgrade, &Upgrade) &&
              Redirect(module, stubs, "slGetNewFrameToken", o_token, &Token) && Redirect(module, stubs, "slSetConstants", o_constants, &Constants) &&
              Redirect(module, stubs, "slSetTagForFrame", o_tag, &Tag) && Redirect(module, stubs, "slFreeResources", o_free, &Free) &&
              Redirect(module, stubs, "slSetFeatureLoaded", o_setLoaded, &SetLoaded);
    FlushInstructionCache(GetCurrentProcess(), nullptr, 0);
    g_on = ok;
    return ok;
}

bool Write(const std::wstring &file)
{
    FILE *out = nullptr;
    if (_wfopen_s(&out, file.c_str(), L"w") || !out) return false;
    std::lock_guard<std::mutex> lock(g_lock);
    std::fprintf(out, "# Streamline calls in order (single-threaded scenarios)\n");
    for (auto &line : g_order) std::fprintf(out, "%s\n", line.c_str());
    std::fprintf(out, "# Streamline calls per scenario\n");
    for (auto &scenario : g_counts)
        for (auto &function : scenario.second) std::fprintf(out, "%s %s %lld\n", scenario.first.c_str(), function.first.c_str(), function.second);
    std::fclose(out);
    return true;
}
} // namespace trace

namespace
{
using Install = int(__cdecl *)(int);
using State = int(__cdecl *)();
using Counts = void(__cdecl *)(int *, int *, int *, long long *);
using Pop = int(__cdecl *)(char *, int, int *);
using Readback = HRESULT(__cdecl *)(void *, unsigned);
using DeveloperLog = void(__cdecl *)(int);
using GenerationState = int(__cdecl *)(char *, int, long long *, long long *, int *);
using GenerationCounters = int(__cdecl *)(char *, int);
using FrameMarker = void(__cdecl *)(int, int);
using SetReflex = void(__cdecl *)(int, int);
using RenderEvent = void(__stdcall *)(int, void *);
using SubmitStart = void(__stdcall *)(int);
using RenderEventFunc = void *(__cdecl *)();

Pop g_pop;
int g_failures = 0;
// The game's frame pacing, as Unity 2019.4 does it on GTFO's swap chain: created with the frame-latency waitable object, the maximum
// latency set to 2, a wait on the object before every frame and Present with tearing allowed. A wait that times out is the hang.
HANDLE g_waitable = nullptr;
UINT g_chainFlags = 0, g_presentFlags = 0, g_interval = 0; // GTFO's VSync setting presents with interval 1 and no tearing
int g_waits = 0, g_waitTimeouts = 0;
ULONGLONG g_longestWait = 0; // milliseconds
int g_frame = 1000; // the next Unity frame number: it only goes up, across the scenarios too

HRESULT PacedPresent(IDXGISwapChain1 *chain)
{
    if (g_waitable)
    {
        g_waits++;
        ULONGLONG start = GetTickCount64();
        if (WaitForSingleObjectEx(g_waitable, 1000, TRUE) != WAIT_OBJECT_0) g_waitTimeouts++;
        g_longestWait = std::max(g_longestWait, GetTickCount64() - start);
    }
    return chain->Present(g_interval, g_interval ? 0 : g_presentFlags);
}
int g_bridgeFailures = 0, g_leftOutLines = 0, g_lookupLines = 0, g_switches = 0;
bool g_fsr = false, g_amdChain = false; // --fsr; the bridge said its display swap chain is AMD FSR's

void Drain()
{
    char line[1024];
    int kind = 0;
    while (g_pop(line, sizeof line, &kind) >= 0)
    {
        std::printf("  native %s: %s\n", kind == 1 ? "ERROR" : kind == 2 ? "left out" : kind == 3 ? "SWITCH FAILED" : "info", line);
        if (kind == 1 || kind == 3) g_bridgeFailures++;
        if (kind == 2) g_leftOutLines++;
        if (std::strstr(line, "the display swap chain is AMD FSR's frame generation swap chain")) g_amdChain = true;
        if (std::strstr(line, "Frame generation switched from")) g_switches++;
        // Streamline's own complaint that a frame's constants or tags are not there (the present's frame and the inputs' frame differ).
        if (std::strstr(line, "Streamline:") && (std::strstr(line, "nable to find") || std::strstr(line, "ailed to find"))) g_lookupLines++;
    }
}

void Check(bool ok, const char *what)
{
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) g_failures++;
}

std::vector<unsigned char> Pattern(UINT width, UINT height, int seed)
{
    std::vector<unsigned char> pixels((size_t)width * height * 4);
    for (UINT y = 0; y < height; y++)
        for (UINT x = 0; x < width; x++)
        {
            auto *p = &pixels[((size_t)y * width + x) * 4];
            p[0] = (unsigned char)(x * 7 + seed * 31);
            p[1] = (unsigned char)(y * 5 + seed * 17);
            p[2] = (unsigned char)((x ^ y) + seed);
            p[3] = 255;
        }
    return pixels;
}

// Copies a pattern into buffer 0, presents, reads back the presented frame and compares.
bool PresentAndCompare(ID3D11Device *device, ID3D11DeviceContext *context, IDXGISwapChain1 *chain, Readback readback, UINT width, UINT height, int seed)
{
    auto pixels = Pattern(width, height, seed);
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA data{pixels.data(), width * 4, 0};
    ComPtr<ID3D11Texture2D> source, buffer;
    if (FAILED(device->CreateTexture2D(&desc, &data, &source)) || FAILED(chain->GetBuffer(0, IID_PPV_ARGS(&buffer)))) return false;
    context->CopyResource(buffer.Get(), source.Get());
    buffer.Reset();
    if (FAILED(PacedPresent(chain))) return false;
    std::vector<unsigned char> presented(pixels.size());
    if (FAILED(readback(presented.data(), (unsigned)presented.size()))) return false;
    return presented == pixels;
}

long long Counter(GenerationCounters counters, const char *name)
{
    char text[512];
    counters(text, sizeof text);
    std::string key = std::string(" ") + name + " ";
    const char *at = std::strstr(text, key.c_str());
    return at ? std::atoll(at + key.size()) : -1;
}

bool MakeTexture(ID3D11Device *device, UINT width, UINT height, DXGI_FORMAT format, ComPtr<ID3D11Texture2D> &texture, unsigned char fill = 0)
{
    std::vector<unsigned char> pixels((size_t)width * height * 8, fill);
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA data{pixels.data(), width * 8, 0};
    return SUCCEEDED(device->CreateTexture2D(&desc, &data, &texture));
}

// A frame's inputs at one output size, as the plugin hands them over: depth, motion, the HUD-less colour and the HUD drawn on black
// and on white (here no HUD: black stays black, white white).
struct Inputs
{
    UINT width = 0, height = 0;
    ComPtr<ID3D11Texture2D> depth, motion, hudless, hudOnBlack, hudOnWhite;
};

bool MakeInputs(ID3D11Device *device, UINT width, UINT height, Inputs &inputs)
{
    inputs.width = width;
    inputs.height = height;
    return MakeTexture(device, width, height, DXGI_FORMAT_R32_FLOAT, inputs.depth) && MakeTexture(device, width, height, DXGI_FORMAT_R16G16_FLOAT, inputs.motion) &&
           MakeTexture(device, width, height, DXGI_FORMAT_R16G16B16A16_FLOAT, inputs.hudless) &&
           MakeTexture(device, width, height, DXGI_FORMAT_R8G8B8A8_UNORM, inputs.hudOnBlack) &&
           MakeTexture(device, width, height, DXGI_FORMAT_R8G8B8A8_UNORM, inputs.hudOnWhite, 255);
}

fg::FrameParams Params(const Inputs &inputs, int frame, int enabled)
{
    fg::FrameParams p{};
    p.size = sizeof p;
    p.version = 2;
    p.frame = frame;
    p.enabled = enabled;
    p.depth = inputs.depth.Get();
    p.motion = inputs.motion.Get();
    p.hudless = inputs.hudless.Get();
    p.hudOnBlack = inputs.hudOnBlack.Get();
    p.hudOnWhite = inputs.hudOnWhite.Get();
    p.renderWidth = p.outputWidth = inputs.width;
    p.renderHeight = p.outputHeight = inputs.height;
    // A reversed-depth perspective projection (row vectors) and its inverse; the camera stands still.
    const float nearPlane = 0.1f, farPlane = 1000.f, fov = 1.0f, aspect = (float)inputs.width / inputs.height;
    const float a = 1.f / (aspect * std::tan(fov / 2)), b = 1.f / std::tan(fov / 2), c = nearPlane / (nearPlane - farPlane),
                d = -farPlane * nearPlane / (nearPlane - farPlane);
    p.viewToClip[0] = a, p.viewToClip[5] = b, p.viewToClip[10] = c, p.viewToClip[11] = 1, p.viewToClip[14] = d;
    p.clipToView[0] = 1 / a, p.clipToView[5] = 1 / b, p.clipToView[11] = 1 / d, p.clipToView[14] = 1, p.clipToView[15] = -c / d;
    for (int i = 0; i < 4; i++) p.clipToPrevClip[i * 5] = p.prevClipToClip[i * 5] = 1;
    p.motionScaleX = p.motionScaleY = 1;
    p.cameraUp[1] = p.cameraRight[0] = p.cameraForward[2] = 1;
    p.cameraNear = nearPlane, p.cameraFar = farPlane, p.cameraFov = fov, p.cameraAspect = aspect;
    p.depthInverted = 1;
    p.encodeSrgb = 1;
    return p;
}

// The plugin's frame generation calls on the bridge, at one output size. Each frame gets what the plugin gives it: Reflex sleep and the
// simulation markers (main thread), the render-submit marker and the frame's inputs (render events), then the present of image.
struct Driver
{
    GenerationState state = nullptr;
    GenerationCounters counters = nullptr;
    FrameMarker marker = nullptr;
    SetReflex reflex = nullptr;
    RenderEvent event = nullptr;
    SubmitStart submitStart = nullptr;
    State backend = nullptr; // absent from a native bridge from before AMD's frame generation (DLSS only)
    ID3D11DeviceContext *context = nullptr;
    IDXGISwapChain1 *chain = nullptr;
    Inputs inputs;
    ComPtr<ID3D11Texture2D> image;
    char reason[512]{};
    long long frames = 0, generated = 0;
    int shown = 0;
    void (*adjust)(fg::FrameParams &) = nullptr; // a scenario's own inputs on top of Params

    bool Load(HMODULE native, ID3D11DeviceContext *deviceContext, IDXGISwapChain1 *swapChain)
    {
        state = (GenerationState)GetProcAddress(native, "ForgeFrameGen_FrameGenerationState");
        counters = (GenerationCounters)GetProcAddress(native, "ForgeFrameGen_FrameGenerationCounters");
        marker = (FrameMarker)GetProcAddress(native, "ForgeFrameGen_FrameMarker");
        reflex = (SetReflex)GetProcAddress(native, "ForgeFrameGen_SetReflex");
        backend = (State)GetProcAddress(native, "ForgeFrameGen_FrameGenerationBackend");
        auto function = (RenderEventFunc)GetProcAddress(native, "ForgeFrameGen_RenderEventFunc");
        auto submitFunction = (RenderEventFunc)GetProcAddress(native, "ForgeFrameGen_RenderSubmitStartFunc");
        context = deviceContext;
        chain = swapChain;
        Check(state && counters && marker && function && reflex && submitFunction, "the native bridge has the frame generation exports");
        if (!state || !counters || !marker || !function || !reflex || !submitFunction) return false;
        event = (RenderEvent)function();
        submitStart = (SubmitStart)submitFunction();
        return true;
    }

    int Kind() { return backend ? backend() : 1; }

    // Resizes the window and the buffers to width x height and makes the inputs and the presented image (pattern seed) at that size.
    bool Size(ID3D11Device *device, HWND window, UINT width, UINT height, int seed)
    {
        SetWindowPos(window, nullptr, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (FAILED(chain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, g_chainFlags)) || !MakeInputs(device, width, height, inputs)) return false;
        if (seed < 0) return MakeTexture(device, width, height, DXGI_FORMAT_R8G8B8A8_UNORM, image);
        auto pixels = Pattern(width, height, seed);
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = width;
        desc.Height = height;
        desc.MipLevels = desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA data{pixels.data(), width * 4, 0};
        image.Reset();
        return SUCCEEDED(device->CreateTexture2D(&desc, &data, &image));
    }

    // inputsOf: the frame number the inputs carry (the plugin's Time.frameCount); 0 sends no inputs, as in a menu. enabled: 1 on, 2 on
    // and left out under a menu that lies over the drawn world.
    bool Frame(int number, int inputsOf, int enabled = 1)
    {
        marker(0, number);
        marker(1, number);
        submitStart(number);
        if (inputsOf)
        {
            fg::FrameParams p = Params(inputs, inputsOf, enabled);
            if (adjust) adjust(p);
            event(4, &p);
        }
        ComPtr<ID3D11Texture2D> buffer;
        if (FAILED(chain->GetBuffer(0, IID_PPV_ARGS(&buffer)))) return false;
        context->CopyResource(buffer.Get(), image.Get());
        buffer.Reset();
        bool presented = SUCCEEDED(PacedPresent(chain));
        Sleep(4);
        return presented;
    }

    // Presents that ran with frame generation on, or -1 while it is not on.
    long long On()
    {
        int running = state(reason, sizeof reason, &frames, &generated, &shown);
        return running == 2 ? frames : -1;
    }
};

// Frame generation on the bridge, driven the way the plugin drives it.
void FrameGeneration(HMODULE native, ID3D11Device *device, ID3D11DeviceContext *context, IDXGISwapChain1 *chain, HWND window)
{
    Driver driver;
    if (!driver.Load(native, context, chain)) return;
    auto counters = driver.counters;
    auto reflex = driver.reflex;
    char *reason = driver.reason;
    long long &frames = driver.frames, &generated = driver.generated;
    int &shown = driver.shown;
    if (driver.state(reason, sizeof driver.reason, &frames, &generated, &shown) == 0)
    {
        std::printf("NOT RUN frame generation: it did not start here (%s)\n", reason);
        return;
    }
    int kind = driver.Kind();
    std::printf("  frame generation backend: %s\n", kind == 1 ? "DLSS Frame Generation" : kind == 2 ? "AMD FSR Frame Generation" : "none");
    if (g_fsr) Check(kind == 2 && g_amdChain, "AMD FSR Frame Generation runs and the display swap chain is AMD FSR's frame generation swap chain");
    else Check(kind == 1, "DLSS Frame Generation runs where it can (FSR not picked)");

    const UINT width = 1280, height = 720;
    SetWindowPos(window, nullptr, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    Check(SUCCEEDED(chain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, g_chainFlags)), "ResizeBuffers to 1280x720 for frame generation");
    bool made = MakeInputs(device, width, height, driver.inputs) && MakeTexture(device, width, height, DXGI_FORMAT_R8G8B8A8_UNORM, driver.image);
    Check(made, "frame generation input textures");
    if (!made) return;
    auto frame = [&](int number, int inputsOf, int enabled = 1) { return driver.Frame(number, inputsOf, enabled); };
    auto on = [&] { return driver.On(); };

    reflex(1, 240); // On + Boost with a limit above what this window presents at: the setting is taken at the first frame's sleep
    int number = g_frame;
    bool presented = true;
    long long start = frames;
    for (int i = 0; i < 40; i++, number++) presented = frame(number, number) && presented;
    Drain();
    Check(presented && on() == start + 40, "forty frames with inputs: every present ran with frame generation on");
    if (generated)
        std::printf("  observed: %lld generated frames in 40 presents, the last present showed %d\n", generated, shown);
    else
        std::printf("NOT RUN generated frames: none came in this window without a game (each present showed %d frame); they are counted in the game\n", shown);

    long long before = Counter(counters, "no-inputs");
    presented = frame(number, 0);
    number++;
    Check(presented && Counter(counters, "no-inputs") == before + 1 && Counter(counters, "other-frame") == 0,
          "a present without inputs is left out and counted as no-inputs, not taken for the frame two presents back");
    long long resumed = frames;
    for (int i = 0; i < 10; i++, number++) presented = frame(number, number) && presented;
    Check(presented && on() == resumed + 10, "frame generation is on again with the next frames that have inputs");

    before = Counter(counters, "menu");
    presented = frame(number, number, 2);
    number++;
    Check(presented && Counter(counters, "menu") == before + 1, "a present under a menu is left out and counted as menu");
    resumed = frames;
    for (int i = 0; i < 10; i++, number++) presented = frame(number, number) && presented;
    Check(presented && on() == resumed + 10, "frame generation is on again after the menu");

    before = Counter(counters, "other-frame");
    int lines = g_leftOutLines;
    presented = frame(number, number - 1); // the inputs name the frame before the one presented
    number++;
    Drain();
    Check(presented && Counter(counters, "other-frame") == before + 1 && g_leftOutLines == lines + 1,
          "inputs of another frame than the one presented are left out, counted as other-frame and reported once");
    resumed = frames;
    for (int i = 0; i < 10; i++, number++) presented = frame(number, number) && presented;
    Drain();
    Check(presented && on() == resumed + 10, "frame generation is on again after the mismatched frame");

    char text[512];
    counters(text, sizeof text);
    std::printf("  counters: %s\n", text);
    Check(g_lookupLines == 0, "Streamline never missed a frame's constants or tags");
    Check(g_bridgeFailures == 0, "the bridge reported no failure");
    g_frame = number;
}

using Prefer = void(__cdecl *)(int);

// The player picks the other upscaler while the game runs, as the plugin passes it on (ForgeFrameGen_SetFrameGenerationPreference): the
// bridge moves the display to the other frame generation at its next present. Six switches between the two (three round trips), the
// third with a resize between the pick and the present, the fourth with a resize right after the switching present. Each switch must
// take the display over (the backend reported; AMD's own frame generation swap chain on AMD's turn), the switching present must show the
// game's frame (read back byte for byte, no black frame) and go through, every frame after the first two must run with frame generation
// on, and no present, wait on the game's waitable object or Streamline lookup may fail. Ends on the backend it began with.
void Switching(HMODULE native, ID3D11Device *device, ID3D11DeviceContext *context, IDXGISwapChain1 *chain, HWND window, Readback readback)
{
    Driver driver;
    auto prefer = (Prefer)GetProcAddress(native, "ForgeFrameGen_SetFrameGenerationPreference");
    if (!driver.Load(native, context, chain) || !prefer) return Check(false, "the native bridge has the frame generation choice");
    if (driver.state(driver.reason, sizeof driver.reason, nullptr, nullptr, nullptr) == 0)
    {
        std::printf("NOT RUN switching frame generation: it did not start here (%s)\n", driver.reason);
        return;
    }
    const int start = driver.Kind(), other = 3 - start;
    UINT width = 1280, height = 720;
    if (!driver.Size(device, window, width, height, 3)) return Check(false, "switching: buffers, inputs and image at 1280x720");
    int number = g_frame, seed = 3, failedPresents = 0, notTaken = 0, notShown = 0, notOn = 0, timeoutsBefore = g_waitTimeouts, failuresBefore = g_bridgeFailures,
        lookupsBefore = g_lookupLines;
    const long long defectsBefore = Counter(driver.counters, "other-frame") + Counter(driver.counters, "markers-missing") + Counter(driver.counters, "no-token");
    ULONGLONG slowest = 0;
    for (int i = 0; i < 4; i++, number++) failedPresents += !driver.Frame(number, number);
    for (int round = 1; round <= 6; round++)
    {
        const int target = round % 2 ? other : start;
        g_amdChain = false;
        prefer(target == 2 ? 2 : 0);
        if (round == 3)
        {
            width = 1600, height = 900, seed = 6;
            if (!driver.Size(device, window, width, height, seed)) return Check(false, "switching: resize between the pick and the present");
        }
        // The switching present: the game's frame must reach the new display chain's back buffer.
        ULONGLONG begun = GetTickCount64();
        failedPresents += !driver.Frame(number, number);
        const ULONGLONG took = GetTickCount64() - begun;
        slowest = std::max(slowest, took);
        number++;
        Drain();
        std::vector<unsigned char> expected = Pattern(width, height, seed), presented(expected.size());
        if (FAILED(readback(presented.data(), (unsigned)presented.size())) || presented != expected) notShown++;
        if (driver.Kind() != target || (target == 2 && !g_amdChain)) notTaken++;
        if (round == 4)
        {
            width = 1280, height = 720, seed = 3;
            if (!driver.Size(device, window, width, height, seed)) return Check(false, "switching: resize right after the switching present");
        }
        failedPresents += !driver.Frame(number, number);
        number++;
        long long before = driver.On();
        for (int i = 0; i < 10; i++, number++) failedPresents += !driver.Frame(number, number);
        long long after = driver.On();
        if (before < 0 || after != before + 10) notOn++;
        std::printf("  switch %d to %s: the switching present took %llu ms; frame generation on for %lld of the next 10 presents\n", round,
                    target == 1 ? "DLSS Frame Generation" : "AMD FSR Frame Generation", took, before < 0 || after < 0 ? 0 : after - before);
    }
    Drain();
    char text[512];
    driver.counters(text, sizeof text);
    std::printf("  counters after switching: %s\n", text);
    std::printf("  slowest switching present: %llu ms\n", slowest);
    const long long defects = Counter(driver.counters, "other-frame") + Counter(driver.counters, "markers-missing") + Counter(driver.counters, "no-token") - defectsBefore;
    Check(!notTaken, "every switch hands the display to the picked frame generation (AMD's on its own swap chain)");
    Check(!notShown, "every switching present shows the game's frame, read back byte for byte from the new display chain");
    Check(!notOn, "after every switch frame generation runs for every present from the third on");
    Check(!failedPresents && g_waitTimeouts == timeoutsBefore, "switching: every present succeeds and every wait on the waitable object returns within a second");
    Check(g_bridgeFailures == failuresBefore && g_lookupLines == lookupsBefore, "switching: no bridge failure and no Streamline lookup failure");
    Check(defects == 0, "switching leaves no present out as a defect (the frames marked before a switch count as switching)");
    g_frame = number;
}

// The game's resolution below its borderless window's (1920x1080 on a 3840x2160 screen): Unity's back buffer stays the window's size and
// the game's picture is scaled up into it, while the plugin's inputs are of the game's resolution. Buffers at 2560x1440 and inputs at
// 1280x720, with each frame generation in turn: every present must run with frame generation on (none left out as resizing), show the
// game's frame byte for byte, and leave no defect, failure or Streamline lookup line. Inputs of another shape (1280x1024) are a resize
// under way and are left out as resizing. Ends at 1280x720 on the backend it began with.
void Scaled(HMODULE native, ID3D11Device *device, ID3D11DeviceContext *context, IDXGISwapChain1 *chain, HWND window, Readback readback)
{
    Driver driver;
    auto prefer = (Prefer)GetProcAddress(native, "ForgeFrameGen_SetFrameGenerationPreference");
    if (!driver.Load(native, context, chain) || !prefer) return Check(false, "the native bridge has the frame generation choice");
    if (driver.state(driver.reason, sizeof driver.reason, nullptr, nullptr, nullptr) == 0)
    {
        std::printf("NOT RUN smaller game resolution: frame generation did not start here (%s)\n", driver.reason);
        return;
    }
    const int start = driver.Kind();
    const UINT width = 2560, height = 1440;
    if (!driver.Size(device, window, width, height, 7) || !MakeInputs(device, 1280, 720, driver.inputs))
        return Check(false, "smaller game resolution: buffers at 2560x1440, inputs at 1280x720");
    int number = g_frame, failuresBefore = g_bridgeFailures, lookupsBefore = g_lookupLines;
    const long long defectsBefore = Counter(driver.counters, "other-frame") + Counter(driver.counters, "markers-missing") + Counter(driver.counters, "no-token");
    for (const int target : {start, 3 - start})
    {
        prefer(target == 2 ? 2 : 0);
        bool presented = true;
        for (int i = 0; i < 3; i++, number++) presented = driver.Frame(number, number) && presented; // the switch, if any, and its grace
        const long long resizing = Counter(driver.counters, "resizing"), before = driver.On();
        for (int i = 0; i < 20; i++, number++) presented = driver.Frame(number, number) && presented;
        Drain();
        const long long after = driver.On();
        std::vector<unsigned char> expected = Pattern(width, height, 7), shown(expected.size());
        const bool same = SUCCEEDED(readback(shown.data(), (unsigned)shown.size())) && shown == expected;
        const char *name = target == 1 ? "DLSS Frame Generation" : "AMD FSR Frame Generation";
        std::printf("  smaller game resolution, %s: frame generation on for %lld of 20 presents, %lld left out as resizing\n", name,
                    before < 0 || after < 0 ? 0 : after - before, Counter(driver.counters, "resizing") - resizing);
        Check(presented && driver.Kind() == target && before >= 0 && after == before + 20 && Counter(driver.counters, "resizing") == resizing && same,
              (std::string("smaller game resolution, ") + name + ": inputs at 1280x720 with 2560x1440 buffers run frame generation on every present, "
               "and the game's frame reads back byte for byte").c_str());
    }
    const long long resizing = Counter(driver.counters, "resizing");
    bool made = MakeInputs(device, 1280, 1024, driver.inputs);
    for (int i = 0; i < 5; i++, number++) made = driver.Frame(number, number) && made;
    Check(made && Counter(driver.counters, "resizing") == resizing + 5, "inputs of another shape than the buffers (1280x1024 on 2560x1440) are left out as resizing");
    prefer(start == 2 ? 2 : 0);
    made = driver.Size(device, window, 1280, 720, 3);
    for (int i = 0; i < 5; i++, number++) made = driver.Frame(number, number) && made;
    Drain();
    const long long defects = Counter(driver.counters, "other-frame") + Counter(driver.counters, "markers-missing") + Counter(driver.counters, "no-token") - defectsBefore;
    Check(made && driver.Kind() == start && defects == 0 && g_bridgeFailures == failuresBefore && g_lookupLines == lookupsBefore,
          "smaller game resolution: no defect, bridge failure or Streamline lookup line; back at 1280x720 on the backend it began with");
    g_frame = number;
}

// The game's threads, as Unity 2019.4 runs them: the main thread owns the window and pumps its messages, sleeps in Reflex and sets the
// simulation markers; the render thread, at most one frame behind, marks the render submission, waits on the frame-latency waitable
// object and presents, and resizes the buffers after the main thread resized the window (1920x1080, then 2560x1440 as in the hung
// start, then back). The main thread waits for the render thread as Unity does; a wait of five seconds is the hang Windows reports.
//
// inputs: the render thread also hands over each frame's frame generation inputs before the present (the plugin's render event), at
// the buffers' size, switched off by the player for 50 frames in every 150; frame generation must run between the switches and the
// resizes and be counted off while switched off. switching: the main thread also picks the other frame generation every 70 frames
// (the plugin passes the upscaler on from the main thread), so the render thread switches backends between the resizes.
void GameThreads(HMODULE native, ID3D11DeviceContext *context, IDXGISwapChain1 *chain, HWND window, int frames, bool inputs = false, bool switching = false)
{
    auto prefer = (Prefer)GetProcAddress(native, "ForgeFrameGen_SetFrameGenerationPreference");
    auto backend = (State)GetProcAddress(native, "ForgeFrameGen_FrameGenerationBackend");
    if (switching && (!prefer || !backend)) return Check(false, "the native bridge has the frame generation choice");
    const int startKind = switching ? backend() : 0;
    int picks = 0;
    auto marker = (FrameMarker)GetProcAddress(native, "ForgeFrameGen_FrameMarker");
    auto submitFunction = (RenderEventFunc)GetProcAddress(native, "ForgeFrameGen_RenderSubmitStartFunc");
    auto eventFunction = (RenderEventFunc)GetProcAddress(native, "ForgeFrameGen_RenderEventFunc");
    auto state = (GenerationState)GetProcAddress(native, "ForgeFrameGen_FrameGenerationState");
    auto counters = (GenerationCounters)GetProcAddress(native, "ForgeFrameGen_FrameGenerationCounters");
    if (!marker || !submitFunction || !eventFunction || !state || !counters) return Check(false, "the native bridge has the marker and frame generation exports");
    auto submitStart = (SubmitStart)submitFunction();
    auto event = (RenderEvent)eventFunction();
    char reason[512];
    long long onBefore = 0, generated = 0;
    int shown = 0;
    if (inputs && state(reason, sizeof reason, &onBefore, &generated, &shown) == 0)
    {
        std::printf("NOT RUN game threads with frame generation inputs: frame generation did not start here (%s)\n", reason);
        return;
    }
    const long long switchedOffBefore = Counter(counters, "switched-off");
    ComPtr<ID3D11Device> device;
    context->GetDevice(&device);
    DXGI_SWAP_CHAIN_DESC1 chainDesc{};
    chain->GetDesc1(&chainDesc);
    std::vector<Inputs> sizes; // the render thread's inputs, one set per buffer size
    int failedInputs = 0;
    const int first = g_frame;
    g_frame += frames + 1;
    ShowWindow(window, SW_SHOWNOACTIVATE);
    struct Shared
    {
        std::mutex lock;
        std::condition_variable wake;
        int queued = 0, done = 0;
        UINT resizeWidth = 0, resizeHeight = 0;
        bool stop = false;
        int failedPresents = 0, failedResizes = 0;
    } shared;
    std::thread render([&] {
        UINT bufferWidth = chainDesc.Width, bufferHeight = chainDesc.Height;
        for (int next = 1;; next++)
        {
            UINT width = 0, height = 0;
            {
                std::unique_lock<std::mutex> lock(shared.lock);
                shared.wake.wait(lock, [&] { return shared.stop || shared.queued >= next; });
                if (shared.stop) return;
                std::swap(width, shared.resizeWidth);
                std::swap(height, shared.resizeHeight);
            }
            if (width && FAILED(chain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, g_chainFlags))) shared.failedResizes++;
            if (width) bufferWidth = width, bufferHeight = height;
            submitStart(first + next);
            if (inputs)
            {
                auto found = std::find_if(sizes.begin(), sizes.end(), [&](const Inputs &set) { return set.width == bufferWidth && set.height == bufferHeight; });
                if (found == sizes.end())
                {
                    sizes.emplace_back();
                    if (!MakeInputs(device.Get(), bufferWidth, bufferHeight, sizes.back())) failedInputs++;
                    found = sizes.end() - 1;
                }
                fg::FrameParams p = Params(*found, first + next, (next / 50) % 3 == 2 ? 0 : 1);
                event(4, &p);
            }
            ComPtr<ID3D11Texture2D> buffer;
            if (SUCCEEDED(chain->GetBuffer(0, IID_PPV_ARGS(&buffer))))
            {
                const float colour[4] = {(next % 60) / 60.f, 0.2f, 0.4f, 1.f};
                ComPtr<ID3D11RenderTargetView> target;
                if (SUCCEEDED(device->CreateRenderTargetView(buffer.Get(), nullptr, &target))) context->ClearRenderTargetView(target.Get(), colour);
            }
            buffer.Reset();
            if (FAILED(PacedPresent(chain))) shared.failedPresents++;
            {
                std::lock_guard<std::mutex> lock(shared.lock);
                shared.done = next;
            }
            shared.wake.notify_all();
        }
    });
    bool hung = false;
    int timeoutsBefore = g_waitTimeouts;
    for (int frame = 1; frame <= frames && !hung; frame++)
    {
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&message);
        UINT width = frame == frames / 4 ? 2560 : frame == frames * 3 / 4 ? 1920 : 0, height = width == 2560 ? 1440 : width ? 1080 : 0;
        if (width) SetWindowPos(window, nullptr, 0, 0, (int)width, (int)height, SWP_NOMOVE | SWP_NOZORDER);
        // An even number of picks: the run ends on the frame generation it began with.
        if (switching && frame % 70 == 35 && frame <= frames - 70) prefer(++picks % 2 ? (startKind == 1 ? 2 : 0) : (startKind == 1 ? 0 : 2));
        marker(0, first + frame);
        marker(1, first + frame);
        std::unique_lock<std::mutex> lock(shared.lock);
        if (width) shared.resizeWidth = width, shared.resizeHeight = height;
        shared.queued = frame;
        shared.wake.notify_all();
        hung = !shared.wake.wait_for(lock, std::chrono::seconds(5), [&] { return shared.done >= frame - 1; });
    }
    std::printf("  game threads (sync interval %u%s): %d frames, waits timed out %d (longest wait so far %llu ms), failed presents %d, failed resizes %d%s\n", g_interval,
                inputs ? ", frame generation inputs, switched off and on" : "", frames, g_waitTimeouts - timeoutsBefore, g_longestWait, shared.failedPresents,
                shared.failedResizes, hung ? ", the main thread waited 5 s for the render thread" : "");
    Check(!hung, "game threads: the main thread never waits five seconds for the render thread (no hang)");
    Check(g_waitTimeouts == timeoutsBefore && !shared.failedPresents && !shared.failedResizes,
          "game threads: every wait on the waitable object returns within a second, every present and resize succeeds");
    if (inputs && !hung)
    {
        long long on = 0;
        state(reason, sizeof reason, &on, &generated, &shown);
        long long switchedOff = Counter(counters, "switched-off") - switchedOffBefore;
        char text[512];
        counters(text, sizeof text);
        std::printf("  game threads with inputs: %lld presents with frame generation on, %lld left out switched off; counters: %s\n", on - onBefore, switchedOff, text);
        Check(!failedInputs && on - onBefore >= frames / 2 && switchedOff >= frames / 6,
              "game threads with inputs: frame generation runs between the switches and resizes, and is off while switched off");
    }
    if (hung)
    {
        Drain();
        std::printf("FAILED %d check(s)\n", g_failures);
        std::fflush(stdout);
        TerminateProcess(GetCurrentProcess(), 1); // the render thread cannot be joined
    }
    {
        std::lock_guard<std::mutex> lock(shared.lock);
        shared.stop = true;
    }
    shared.wake.notify_all();
    render.join();
}
// The upscalers through the plugin's render events on this program's D3D11 device (forge_framegen.cpp, fsr_upscale.cpp): Auto must pick
// DLSS on an NVIDIA card that has it; FSR picked by hand must start (FSR 3.1 on a card FSR 4 is not offered for), give FSR's render
// sizes, upscale twenty frames of a flat colour into the output-size target with no message, and give the same colour back.
#pragma pack(push, 8)
struct UpscalerInit
{
    uint32_t size, version;
    void *anyTexture;
    const wchar_t *libraryFolder, *dataFolder;
    int32_t method;
};
#pragma pack(pop)
static_assert(sizeof(UpscalerInit) == 40, "the native bridge's InitParams");

void Upscalers(HMODULE native, const std::wstring &folder, ID3D11Device *device, ID3D11DeviceContext *context)
{
    auto eventFunc = (RenderEventFunc)GetProcAddress(native, "ForgeFrameGen_RenderEventFunc");
    auto status = (State)GetProcAddress(native, "ForgeFrameGen_Status");
    auto evaluations = (long long(__cdecl *)())GetProcAddress(native, "ForgeFrameGen_Evaluations");
    auto popMessage = (int(__cdecl *)(char *, int))GetProcAddress(native, "ForgeFrameGen_PopMessage");
    auto upscaler = (int(__cdecl *)(char *, int, int *))GetProcAddress(native, "ForgeFrameGen_Upscaler");
    auto renderSize = (int(__cdecl *)(uint32_t, uint32_t, int, uint32_t *, uint32_t *))GetProcAddress(native, "ForgeFrameGen_OptimalRenderSize");
    Check(eventFunc && status && evaluations && popMessage && upscaler && renderSize, "the native bridge has the upscaler exports");
    if (!eventFunc || !status || !evaluations || !popMessage || !upscaler || !renderSize) return;
    auto event = (RenderEvent)eventFunc();
    auto messages = [&] {
        int count = 0;
        char text[1024];
        while (popMessage(text, sizeof text) >= 0) std::printf("  upscaler message: %s\n", text), count++;
        return count;
    };
    char why[1024];
    int fellBack = 0;
    ComPtr<ID3D11Texture2D> any;
    D3D11_TEXTURE2D_DESC anyDesc{};
    anyDesc.Width = anyDesc.Height = anyDesc.MipLevels = anyDesc.ArraySize = 1;
    anyDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    anyDesc.SampleDesc.Count = 1;
    anyDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    device->CreateTexture2D(&anyDesc, nullptr, &any);
    wchar_t temp[MAX_PATH];
    GetTempPathW(MAX_PATH, temp);
    UpscalerInit init{sizeof(UpscalerInit), 3, any.Get(), folder.c_str(), temp, 0};

    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC adapterDesc{};
    if (SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&dxgi))) && SUCCEEDED(dxgi->GetAdapter(&adapter))) adapter->GetDesc(&adapterDesc);
    std::printf("  adapter: %ls (vendor 0x%04X)\n", adapterDesc.Description, adapterDesc.VendorId);

    event(1, &init);
    int running = upscaler(why, sizeof why, &fellBack);
    std::printf("  Auto: upscaler %d, %s\n", running, why);
    if (adapterDesc.VendorId == 0x10DE && running == 1) Check(status() == 1 && !fellBack, "Auto picks DLSS on this NVIDIA card");
    else if (adapterDesc.VendorId == 0x10DE) std::printf("NOT RUN Auto on an RTX card: DLSS did not start on this NVIDIA card (%s)\n", why);
    else Check(running == 2, "Auto picks FSR on a card that is not NVIDIA's");
    messages();
    event(3, nullptr);
    Check(status() == 0 && upscaler(why, sizeof why, &fellBack) == 0, "the upscaler stops and the bridge is idle again");

    init.method = 2;
    event(1, &init);
    running = upscaler(why, sizeof why, &fellBack);
    std::printf("  FSR picked: upscaler %d, %s\n", running, why);
    Check(running == 2 && status() == 1 && !fellBack, "FSR picked by hand starts");
    if (running != 2)
    {
        messages();
        return;
    }
    uint32_t w = 0, h = 0;
    bool sizes = renderSize(1920, 1080, 2, &w, &h) && w == 1280 && h == 720;
    sizes = renderSize(1920, 1080, 0, &w, &h) && w == 960 && h == 540 && sizes;
    std::printf("  FSR render sizes for 1920x1080: Quality %s, Performance %ux%u\n", sizes ? "1280x720" : "?", w, h);
    Check(sizes, "FSR's render sizes: Quality 1280x720, Performance 960x540 for 1920x1080");

    const UINT rw = 960, rh = 540, ow = 1920, oh = 1080;
    auto make = [&](UINT width, UINT height, DXGI_FORMAT format, UINT bind, const void *fill, UINT pixelBytes) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = width;
        desc.Height = height;
        desc.MipLevels = desc.ArraySize = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET | bind;
        std::vector<unsigned char> pixels((size_t)width * height * pixelBytes);
        for (size_t i = 0; i < (size_t)width * height; i++) std::memcpy(&pixels[i * pixelBytes], fill, pixelBytes);
        D3D11_SUBRESOURCE_DATA data{pixels.data(), width * pixelBytes, 0};
        ComPtr<ID3D11Texture2D> texture;
        device->CreateTexture2D(&desc, &data, &texture);
        return texture;
    };
    // Half floats: 0.25, 0.5, 0.75, 1 for the colour; 0 for the motion; depth 0.5 (32-bit float).
    const uint16_t colour[4] = {0x3400, 0x3800, 0x3A00, 0x3C00}, still[2] = {0, 0}, zero[4] = {0, 0, 0, 0};
    const float halfDepth = 0.5f;
    auto color = make(rw, rh, DXGI_FORMAT_R16G16B16A16_TYPELESS, 0, colour, 8), depth = make(rw, rh, DXGI_FORMAT_R32_FLOAT, 0, &halfDepth, 4),
         motion = make(rw, rh, DXGI_FORMAT_R16G16_FLOAT, 0, still, 4), output = make(ow, oh, DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_BIND_UNORDERED_ACCESS, zero, 8);
    Check(color && depth && motion && output, "upscaler input and output textures");
    if (!color || !depth || !motion || !output) return;

    long long before = evaluations();
    std::vector<FrameParams> frames(20);
    for (int i = 0; i < 20; i++)
    {
        FrameParams &frame = frames[i];
        frame = FrameParams{};
        frame.size = sizeof(FrameParams);
        frame.version = 3;
        frame.color = color.Get();
        frame.depth = depth.Get();
        frame.motion = motion.Get();
        frame.output = output.Get();
        frame.renderWidth = rw;
        frame.renderHeight = rh;
        frame.outputWidth = ow;
        frame.outputHeight = oh;
        frame.jitterX = (float)((i % 4) / 4.0 - 0.375);
        frame.jitterY = (float)((i % 3) / 3.0 - 0.33);
        frame.motionScaleX = -(float)rw;
        frame.motionScaleY = -(float)rh;
        frame.reset = i == 0;
        frame.hdr = 1;
        frame.depthInverted = 1;
        frame.quality = 0;
        frame.cameraNear = 0.1f;
        frame.cameraFar = 500.0f;
        frame.cameraFov = 1.0f;
        event(2, &frame);
    }
    context->Flush();
    int lines = messages();
    Check(evaluations() == before + 20 && status() == 2, "FSR upscaled twenty frames");
    Check(lines == 0, "FSR sent no message");
    running = upscaler(why, sizeof why, &fellBack);
    std::printf("  FSR running: %s\n", why);
    const char *version = std::strstr(why, "; running ");
    if (version) version += std::strcspn(version, "0123456789");
    Check(version && *version, "the bridge reports the FSR version that runs");
    if (version && *version && adapterDesc.VendorId != 0x1002) Check(*version == '3', "on a card that is not AMD's the FSR SDK runs FSR 3.1, not FSR 4");
    if (adapterDesc.VendorId != 0x1002) std::printf("NOT RUN FSR 4: this card is not an AMD Radeon RX 7000 or 9000\n");

    D3D11_TEXTURE2D_DESC stagingDesc{};
    output->GetDesc(&stagingDesc);
    stagingDesc.BindFlags = 0;
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;
    device->CreateTexture2D(&stagingDesc, nullptr, &staging);
    context->CopyResource(staging.Get(), output.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    bool same = false;
    if (SUCCEEDED(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)))
    {
        auto half = [](uint16_t value) {
            int exponent = (value >> 10) & 31, mantissa = value & 1023;
            float magnitude = exponent == 0 ? mantissa / 16777216.0f : std::ldexp(1.0f + mantissa / 1024.0f, exponent - 15);
            return value & 0x8000 ? -magnitude : magnitude;
        };
        double sum[3] = {};
        int off = 0, count = 0;
        for (UINT y = 0; y < oh; y += 37)
            for (UINT x = 0; x < ow; x += 41)
            {
                auto *pixel = reinterpret_cast<const uint16_t *>(static_cast<const unsigned char *>(mapped.pData) + y * mapped.RowPitch) + x * 4;
                float r = half(pixel[0]), g = half(pixel[1]), b = half(pixel[2]);
                sum[0] += r, sum[1] += g, sum[2] += b, count++;
                if (!(std::fabs(r - 0.25f) < 0.03f && std::fabs(g - 0.5f) < 0.03f && std::fabs(b - 0.75f) < 0.03f)) off++;
            }
        context->Unmap(staging.Get(), 0);
        std::printf("  FSR output mean %.3f %.3f %.3f over %d samples, %d off by more than 0.03\n", sum[0] / count, sum[1] / count, sum[2] / count, count, off);
        same = off == 0;
    }
    Check(same, "FSR's output is the flat input colour (0.25, 0.5, 0.75) at the output size");
    event(3, nullptr);
    Check(status() == 0, "FSR stops and the bridge is idle again");
}
} // namespace

// ---------------------------------------------------------------------------------------------------------------------------------
// What AMD FSR Frame Generation shows (--fsr): a sine pattern (period 64 px) that moves 16 px to the right each frame, with the motion,
// depth and camera inputs the plugin gives for it (Unity's motion: current minus previous in UV units; rows flipped into the swap
// chain's order; reversed depth). The bridge's watch passes every shown frame's middle row back: each game frame must be shown unchanged
// and in order, and each generated frame must sit halfway (the pattern's phase 8 px behind the next real frame, its amplitude and mean
// brightness those of a real frame). A frame from the wrong place, a blend of two frames (amplitude down to 71 %), a dark or bright
// frame between two real ones: each is the "whole screen flickers" a player sees.
namespace shown
{
constexpr UINT kWidth = 1280, kHeight = 720, kPeriod = 64, kStep = 16;
std::vector<unsigned char> g_pattern;
ComPtr<ID3D11Texture2D> g_motion, g_depth, g_hudless;
float g_motionSign = -1.f; // the plugin's MotionScaleX

double Value(UINT x, int frame) { return 128.0 + 100.0 * std::sin(6.283185307179586 * ((double)x - (double)kStep * frame) / kPeriod); }

void Row(int frame, unsigned char *row)
{
    for (UINT x = 0; x < kWidth; x++)
    {
        auto v = (unsigned char)std::lround(Value(x, frame));
        row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = v, row[x * 4 + 3] = 255;
    }
}

// The phase of a row's pattern as the shift in pixels (mod the period), its amplitude and its mean.
void Fit(const unsigned char *row, double &shift, double &amplitude, double &mean)
{
    double sum = 0, s = 0, c = 0;
    for (UINT x = 0; x < kWidth; x++) sum += row[x * 4];
    mean = sum / kWidth;
    for (UINT x = 0; x < kWidth; x++)
    {
        double a = 6.283185307179586 * x / kPeriod, v = row[x * 4] - mean;
        s += v * std::sin(a), c += v * std::cos(a);
    }
    // v = A sin(a - 2 pi shift / P) = A (sin a cos p - cos a sin p): s ~ A cos p, c ~ -A sin p.
    amplitude = 2.0 * std::sqrt(s * s + c * c) / kWidth;
    double p = std::atan2(-c, s);
    shift = std::fmod(p * kPeriod / 6.283185307179586 + 10.0 * kPeriod, (double)kPeriod);
}

double Gap(double a, double b) // the distance of two shifts mod the period
{
    double d = std::fmod(std::fabs(a - b), (double)kPeriod);
    return std::min(d, kPeriod - d);
}

void Adjust(fg::FrameParams &p)
{
    p.motion = g_motion.Get();
    p.depth = g_depth.Get();
    p.hudless = g_hudless.Get();
    p.motionScaleX = g_motionSign, p.motionScaleY = 1; // the plugin's, rows flipped
    p.flipRows = 1;
    p.encodeSrgb = 1;
}

struct Result
{
    int real = 0, generated = 0, realExact = 0, generatedRight = 0;
    double worstGap = 0, lowestAmplitude = 1e9, worstMean = 0;
};

// frames game frames, each with a generated frame before it; hud: the HUD-less colour given.
Result Run(Driver &driver, ID3D11DeviceContext *context, HMODULE native, int frames, bool hud)
{
    Result result;
    auto watch = (void(__cdecl *)(int))GetProcAddress(native, "ForgeFrameGen_WatchShownFrames");
    auto read = (int(__cdecl *)(unsigned char *, int, int *, unsigned *))GetProcAddress(native, "ForgeFrameGen_ShownFrames");
    auto setHud = (void(__cdecl *)(int))GetProcAddress(native, "ForgeFrameGen_SetHudInGeneratedFrames");
    setHud(hud ? 1 : 0);
    std::vector<unsigned char> image((size_t)kWidth * kHeight * 4);
    std::vector<uint16_t> linear((size_t)kWidth * kHeight * 4);
    driver.adjust = &Adjust;
    const int first = g_frame;
    watch(1);
    for (int i = 0; i < frames; i++, g_frame++)
    {
        for (UINT y = 0; y < kHeight; y++) Row(i, &image[(size_t)y * kWidth * 4]);
        context->UpdateSubresource(driver.image.Get(), 0, nullptr, image.data(), kWidth * 4, 0);
        // The HUD-less colour: the same picture, linear (the bridge encodes it as sRGB like the back buffer).
        for (size_t p = 0; p < image.size(); p++)
        {
            float v = image[p] / 255.f;
            v = (p & 3) == 3 ? 1.f : v <= 0.04045f ? v / 12.92f : std::pow((v + 0.055f) / 1.055f, 2.4f);
            linear[p] = DirectX::PackedVector::XMConvertFloatToHalf(v);
        }
        context->UpdateSubresource(g_hudless.Get(), 0, nullptr, linear.data(), kWidth * 8, 0);
        driver.Frame(g_frame, g_frame);
        Sleep(12); // a game frame of about 16 ms: the generated frame has its own time in between
    }
    std::vector<unsigned char> rows((size_t)kWidth * 4 * 128);
    int flags[128]{};
    unsigned width = 0;
    int count = read(rows.data(), 128, flags, &width);
    watch(0);
    driver.adjust = nullptr;
    setHud(1);
    if (width != kWidth) return result;
    std::vector<unsigned char> expected(kWidth * 4);
    int lastReal = -1;
    double lastRealShift = -1;
    for (int i = 0; i < count; i++)
    {
        const unsigned char *row = &rows[(size_t)i * kWidth * 4];
        double shift, amplitude, mean;
        Fit(row, shift, amplitude, mean);
        if (!flags[i])
        {
            result.real++;
            // Which game frame: the next in order must match byte for byte.
            Row(lastReal + 1, expected.data());
            bool exact = std::memcmp(expected.data(), row, expected.size()) == 0;
            if (exact) result.realExact++;
            lastReal = exact ? lastReal + 1 : (int)std::lround(shift / kStep) % (kPeriod / kStep);
            lastRealShift = shift;
            continue;
        }
        // A generated frame is shown before the real frame it leads to: halfway from the last real frame's pattern to the next one's
        // (one shown before the first real frame watched has nothing to be measured against).
        if (lastRealShift < 0) continue;
        result.generated++;
        double want = std::fmod(lastRealShift + kStep / 2.0, (double)kPeriod), gap = Gap(shift, want);
        result.worstGap = std::max(result.worstGap, gap);
        result.lowestAmplitude = std::min(result.lowestAmplitude, amplitude);
        result.worstMean = std::max(result.worstMean, std::fabs(mean - 128.0));
        if (gap <= 2.0 && amplitude >= 85.0 && std::fabs(mean - 128.0) <= 6.0) result.generatedRight++;
    }
    (void)first;
    return result;
}
} // namespace shown

void ShownFrames(HMODULE native, ID3D11Device *device, ID3D11DeviceContext *context, IDXGISwapChain1 *chain, HWND window)
{
    if (!GetProcAddress(native, "ForgeFrameGen_WatchShownFrames"))
    {
        std::printf("NOT RUN shown frames: this native bridge cannot watch what AMD FSR Frame Generation shows\n");
        return;
    }
    Driver driver;
    if (!driver.Load(native, context, chain) || driver.Kind() != 2)
    {
        std::printf("NOT RUN shown frames: AMD FSR Frame Generation does not run\n");
        return;
    }
    if (!driver.Size(device, window, shown::kWidth, shown::kHeight, -1)) return Check(false, "shown frames: 1280x720 buffers and inputs");
    // Motion of the pattern: +16 px a frame to the right, Unity's current minus previous in UV units; depth halfway.
    std::vector<uint16_t> motion((size_t)shown::kWidth * shown::kHeight * 2);
    for (size_t i = 0; i < motion.size(); i += 2)
        motion[i] = DirectX::PackedVector::XMConvertFloatToHalf((float)shown::kStep / shown::kWidth), motion[i + 1] = 0;
    std::vector<float> depth((size_t)shown::kWidth * shown::kHeight, 0.5f);
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = shown::kWidth;
    desc.Height = shown::kHeight;
    desc.MipLevels = desc.ArraySize = 1;
    desc.SampleDesc.Count = 1;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    desc.Format = DXGI_FORMAT_R16G16_FLOAT;
    D3D11_SUBRESOURCE_DATA data{motion.data(), shown::kWidth * 4, 0};
    bool made = SUCCEEDED(device->CreateTexture2D(&desc, &data, &shown::g_motion));
    desc.Format = DXGI_FORMAT_R32_FLOAT;
    data = {depth.data(), shown::kWidth * 4, 0};
    made = made && SUCCEEDED(device->CreateTexture2D(&desc, &data, &shown::g_depth));
    desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
    made = made && SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &shown::g_hudless));
    Check(made, "shown frames: motion, depth and HUD-less textures");
    if (!made) return;

    const int frames = 40;
    auto report = [&](const char *name, const shown::Result &r) {
        std::printf("  shown frames, %s: %d game frames (%d exact and in order), %d generated between two of them (%d halfway); worst phase gap %.1f px, lowest amplitude %.1f "
                    "(a real frame's 100), worst mean off %.1f\n",
                    name, r.real, r.realExact, r.generated, r.generatedRight, r.worstGap, r.lowestAmplitude, r.worstMean);
    };
    for (bool hud : {false, true})
    {
        shown::g_motionSign = -1.f;
        auto r = shown::Run(driver, context, native, frames, hud);
        report(hud ? "HUD-less colour given" : "no HUD-less colour", r);
        Check(r.real >= frames - 2 && r.realExact == r.real, hud ? "shown frames (HUD-less colour given): every game frame is shown unchanged and in order"
                                                                : "shown frames (no HUD-less colour): every game frame is shown unchanged and in order");
        Check(r.generated >= frames / 2 && r.generatedRight == r.generated,
              hud ? "shown frames (HUD-less colour given): every generated frame sits halfway between its game frames, as bright and as sharp as they are"
                  : "shown frames (no HUD-less colour): every generated frame sits halfway between its game frames, as bright and as sharp as they are");
    }
    // Detection: the same frames with the motion of the wrong sign must be caught.
    shown::g_motionSign = 1.f;
    auto broken = shown::Run(driver, context, native, frames, false);
    report("motion of the wrong sign (deliberately broken)", broken);
    Check(broken.generated > 0 && broken.generatedRight < broken.generated, "shown frames: motion of the wrong sign is caught (generated frames out of place)");
    shown::g_motionSign = -1.f;
    shown::g_motion.Reset(), shown::g_depth.Reset(), shown::g_hudless.Reset();
    Drain();
}

// ---------------------------------------------------------------------------------------------------------------------------------
// Latency (--fsr): the game's two threads as Unity runs them with GTFO's maximum frame latency of 4, the GPU the bottleneck (a heavy
// copy load each frame), AMD FSR Frame Generation on. The main thread waits on the waitable object, then hands the frame to the render
// thread (which may lag one frame); the render thread draws, signals a fence and presents. Measured: from the main thread's start of a
// frame to the GPU finishing that frame, in milliseconds and in frames.
void Latency(HMODULE native, ID3D11Device *device, ID3D11DeviceContext *context, IDXGISwapChain1 *chain, HWND window)
{
    Driver driver;
    if (!driver.Load(native, context, chain) || driver.Kind() != 2)
    {
        std::printf("NOT RUN latency: AMD FSR Frame Generation does not run\n");
        return;
    }
    const UINT width = 1280, height = 720;
    if (!driver.Size(device, window, width, height, 3)) return Check(false, "latency: buffers and inputs");
    ComPtr<IDXGISwapChain2> chain2;
    ComPtr<ID3D11Device5> device5;
    ComPtr<ID3D11DeviceContext4> context4;
    ComPtr<ID3D11Fence> fence;
    ComPtr<ID3D11Texture2D> loadA, loadB;
    D3D11_TEXTURE2D_DESC load{};
    load.Width = load.Height = 4096;
    load.MipLevels = load.ArraySize = 1;
    load.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
    load.SampleDesc.Count = 1;
    load.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    bool made = SUCCEEDED(chain->QueryInterface(IID_PPV_ARGS(&chain2))) && SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&device5))) &&
                SUCCEEDED(context->QueryInterface(IID_PPV_ARGS(&context4))) && SUCCEEDED(device5->CreateFence(0, D3D11_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence))) &&
                SUCCEEDED(device->CreateTexture2D(&load, nullptr, &loadA)) && SUCCEEDED(device->CreateTexture2D(&load, nullptr, &loadB));
    Check(made, "latency: fence and load textures");
    if (!made) return;
    UINT before = 2;
    chain2->GetMaximumFrameLatency(&before);
    chain2->SetMaximumFrameLatency(4); // GTFO's
    // A few frames to settle frame generation and the pacing.
    for (int i = 0; i < 20; i++, g_frame++) driver.Frame(g_frame, g_frame);

    const int frames = 240, copies = 24;
    std::vector<std::chrono::steady_clock::time_point> started(frames + 1), done(frames + 1);
    std::mutex lock;
    std::condition_variable wake;
    int handed = -1, taken = -1;
    bool stop = false;
    HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    std::thread watcher([&] {
        for (int k = 0; k < frames; k++)
        {
            if (fence->GetCompletedValue() < (UINT64)k + 1 && SUCCEEDED(fence->SetEventOnCompletion(k + 1, event))) WaitForSingleObject(event, 5000);
            done[k] = std::chrono::steady_clock::now();
        }
    });
    std::thread render([&] {
        for (int k = 0; k < frames; k++)
        {
            {
                std::unique_lock<std::mutex> hold(lock);
                wake.wait(hold, [&] { return handed >= k || stop; });
                taken = k;
            }
            wake.notify_all();
            driver.marker(0, g_frame + k);
            driver.marker(1, g_frame + k);
            driver.submitStart(g_frame + k);
            fg::FrameParams p = Params(driver.inputs, g_frame + k, 1);
            driver.event(4, &p);
            for (int c = 0; c < copies; c++) context->CopyResource(c & 1 ? loadA.Get() : loadB.Get(), c & 1 ? loadB.Get() : loadA.Get());
            ComPtr<ID3D11Texture2D> buffer;
            chain->GetBuffer(0, IID_PPV_ARGS(&buffer));
            context->CopyResource(buffer.Get(), driver.image.Get());
            buffer.Reset();
            context4->Signal(fence.Get(), (UINT64)k + 1);
            chain->Present(0, g_presentFlags);
        }
    });
    for (int k = 0; k < frames; k++)
    {
        WaitForSingleObjectEx(g_waitable, 1000, TRUE);
        started[k] = std::chrono::steady_clock::now();
        // Unity's main thread may be one frame ahead of its render thread, no more.
        std::unique_lock<std::mutex> hold(lock);
        wake.wait(hold, [&] { return taken >= k - 1; });
        handed = k;
        wake.notify_all();
    }
    render.join();
    watcher.join();
    CloseHandle(event);
    g_frame += frames;
    double latency = 0, interval = 0;
    const int from = 40; // past the start
    for (int k = from; k < frames; k++) latency += std::chrono::duration<double, std::milli>(done[k] - started[k]).count();
    latency /= frames - from;
    interval = std::chrono::duration<double, std::milli>(done[frames - 1] - done[from]).count() / (frames - 1 - from);
    std::printf("  latency, AMD FSR Frame Generation on, the game's maximum frame latency 4, GPU-bound: %.1f ms from a frame's start to the GPU finishing "
                "it, %.1f ms a frame: %.2f frames\n",
                latency, interval, latency / interval);
    chain2->SetMaximumFrameLatency(before);
    Drain();
    Check(latency / interval <= 2.6, "latency: with AMD FSR Frame Generation on, a frame is done on the GPU within about two frames of its start");
}

// No plugin (--no-plugin): the preloader installed the hooks but the plugin never loaded, so nothing says the plugin is there. At the first
// present the bridge parks: no frame generation backend runs, the display is a plain swap chain, every game frame reads back unchanged and
// nothing is generated.
int NoPlugin(HMODULE native, ID3D11Device *device, ID3D11DeviceContext *context, IDXGISwapChain1 *chain, Readback readback, UINT width, UINT height)
{
    auto backend = (State)GetProcAddress(native, "ForgeFrameGen_FrameGenerationBackend");
    auto parked = (State)GetProcAddress(native, "ForgeFrameGen_Parked");
    auto connected = (void(__cdecl *)())GetProcAddress(native, "ForgeFrameGen_PluginConnected");
    auto generation = (GenerationState)GetProcAddress(native, "ForgeFrameGen_FrameGenerationState");
    if (!backend || !parked || !connected || !generation)
    {
        std::printf("FAIL --no-plugin: this native bridge does not know whether the plugin is there\n");
        return 1;
    }
    const int before = backend();
    std::printf("  frame generation backend before the first present: %d\n", before);
    bool exact = true;
    for (int seed = 0; seed < 30; seed++) exact = PresentAndCompare(device, context, chain, readback, width, height, seed) && exact;
    Drain();
    char reason[256]{};
    long long frames = 0, generated = 0;
    int shown = 0;
    int running = generation(reason, sizeof reason, &frames, &generated, &shown);
    std::printf("  after 30 presents: parked %d, backend %d, frame generation state %d (%s), generated %lld\n", parked(), backend(), running, reason, generated);
    Check(parked() == 1 && backend() == 0 && running == 0, "no plugin: the bridge parks at its first present, with no frame generation backend running");
    Check(exact && generated == 0, "no plugin: thirty game frames read back byte for byte from a plain swap chain, none generated");
    // The plugin showing up after all (it never does in the game): the display goes back to the frame generation that was to run.
    connected();
    exact = true;
    for (int seed = 30; seed < 35; seed++) exact = PresentAndCompare(device, context, chain, readback, width, height, seed) && exact;
    Drain();
    Check(exact && backend() == before && parked() == -1, "no plugin: once the plugin is there, the frame generation backend runs again and frames still read back");
    return 0;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc < 2)
    {
        std::printf("usage: ForgeFrameGen.BridgeCheck.exe <folder with ForgeFrameGen.Native.dll> [--fsr] [--trace <file>]\n");
        return 2;
    }
    std::wstring traceFile;
    bool noPlugin = false;
    for (int i = 2; i < argc; i++)
        if (!_wcsicmp(argv[i], L"--fsr")) g_fsr = true;
        else if (!_wcsicmp(argv[i], L"--no-plugin")) noPlugin = true;
        else if (!_wcsicmp(argv[i], L"--trace") && i + 1 < argc) traceFile = argv[++i];
    std::wstring path = std::wstring(argv[1]) + L"\\ForgeFrameGen.Native.dll";
    HMODULE native = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!native)
    {
        std::printf("FAIL loading %ls (error %lu)\n", path.c_str(), GetLastError());
        return 1;
    }
    if (!traceFile.empty())
    {
        // The folder exactly as the native bridge names it, so that its sl.interposer.dll is this very module.
        wchar_t loaded[MAX_PATH * 2]{};
        GetModuleFileNameW(native, loaded, MAX_PATH * 2);
        std::wstring folder = loaded;
        folder.resize(folder.find_last_of(L'\\'));
        if (!trace::Install(folder))
        {
            std::printf("FAIL the Streamline trace could not be installed\n");
            return 1;
        }
    }
    if (g_fsr)
    {
        auto prefer = (void(__cdecl *)(int))GetProcAddress(native, "ForgeFrameGen_SetFrameGenerationPreference");
        if (!prefer)
        {
            std::printf("FAIL --fsr: this native bridge has no frame generation choice\n");
            return 1;
        }
        prefer(2);
    }
    auto install = (Install)GetProcAddress(native, "ForgeFrameGen_InstallGraphicsHooks");
    auto state = (State)GetProcAddress(native, "ForgeFrameGen_PresentState");
    auto counts = (Counts)GetProcAddress(native, "ForgeFrameGen_GraphicsCounts");
    auto readback = (Readback)GetProcAddress(native, "ForgeFrameGen_BridgeReadback");
    g_pop = (Pop)GetProcAddress(native, "ForgeFrameGen_PopBridgeMessage");
    if (!install || !state || !counts || !readback || !g_pop)
    {
        std::printf("FAIL the native bridge lacks the present-bridge exports\n");
        return 1;
    }
    if (auto developer = (DeveloperLog)GetProcAddress(native, "ForgeFrameGen_SetDeveloperLog")) developer(1); // info lines are queued only with developer logs on
    // The plugin loads before the game's first present and says so; --no-plugin plays the game whose plugin did not load.
    if (auto connected = (void(__cdecl *)())GetProcAddress(native, "ForgeFrameGen_PluginConnected"); connected && !noPlugin) connected();
    Check(install(2) > 0, "hooks patched this program's D3D11/DXGI imports");
    Drain();

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"ForgeFrameGenBridgeCheck";
    RegisterClassW(&windowClass);
    const UINT width = 320, height = 200;
    HWND window = CreateWindowExW(0, windowClass.lpszClassName, L"ForgeFrameGen bridge check", WS_OVERLAPPEDWINDOW, 0, 0, width, height, nullptr, nullptr,
                                  windowClass.hInstance, nullptr);

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL level{};
    HRESULT result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device, &level, &context);
    Check(SUCCEEDED(result), "D3D11 device");
    ComPtr<IDXGIFactory2> factory;
    Check(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))), "DXGI factory");
    if (!device || !factory) return 1;

    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = width;
    desc.Height = height;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    // GTFO's flags 0x842: the frame-latency waitable object and mode switches, and tearing where the system offers it.
    BOOL tearing = FALSE;
    ComPtr<IDXGIFactory5> factory5;
    if (SUCCEEDED(factory.As(&factory5))) factory5->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &tearing, sizeof tearing);
    desc.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT | DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH | (tearing ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0);
    g_chainFlags = desc.Flags;
    ComPtr<IDXGISwapChain1> chain;
    result = factory->CreateSwapChainForHwnd(device.Get(), window, &desc, nullptr, nullptr, &chain);
    Drain();
    Check(SUCCEEDED(result) && state() == 2, "the game's swap chain is the present bridge");
    if (!chain || state() != 2) return 1;

    // The game's start-up order: a resize, the waitable object taken, the latency set to 2, a second resize to the same size.
    Check(SUCCEEDED(chain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, g_chainFlags)), "ResizeBuffers before the waitable object is taken");
    ComPtr<IDXGISwapChain2> chain2;
    Check(SUCCEEDED(chain.As(&chain2)), "the bridge offers IDXGISwapChain2");
    if (!chain2) return 1;
    g_waitable = chain2->GetFrameLatencyWaitableObject();
    Check(g_waitable != nullptr, "the game gets a frame-latency waitable object");
    Check(SUCCEEDED(chain2->SetMaximumFrameLatency(2)), "the game sets the maximum frame latency to 2");
    Check(SUCCEEDED(chain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, g_chainFlags)), "ResizeBuffers after the waitable object is taken");
    g_presentFlags = (g_chainFlags & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING) ? DXGI_PRESENT_ALLOW_TEARING : 0;
    if (noPlugin)
    {
        int code = NoPlugin(native, device.Get(), context.Get(), chain.Get(), readback, width, height);
        chain.Reset();
        DestroyWindow(window);
        Drain();
        std::printf(code || g_failures ? "FAILED %d check(s)\n" : "ALL PASSED\n", g_failures);
        return code || g_failures ? 1 : 0;
    }

    bool exact = true;
    for (int seed = 0; seed < 5; seed++) exact = PresentAndCompare(device.Get(), context.Get(), chain.Get(), readback, width, height, seed) && exact;
    Check(exact, "five presented frames read back byte for byte from the D3D12 back buffer");

    const UINT width2 = 256, height2 = 144;
    result = chain->ResizeBuffers(0, width2, height2, DXGI_FORMAT_UNKNOWN, g_chainFlags);
    Check(SUCCEEDED(result), "ResizeBuffers");
    exact = true;
    for (int seed = 5; seed < 8; seed++) exact = PresentAndCompare(device.Get(), context.Get(), chain.Get(), readback, width2, height2, seed) && exact;
    Check(exact, "after the resize, three frames read back byte for byte");

    Check(chain->SetFullscreenState(TRUE, nullptr) == DXGI_ERROR_NOT_CURRENTLY_AVAILABLE, "exclusive fullscreen is refused");
    int devices = 0, factories = 0, chains = 0;
    long long presents = 0;
    counts(&devices, &factories, &chains, &presents);
    std::printf("  counts: devices %d, factories %d, swap chains %d, bridge presents %lld\n", devices, factories, chains, presents);
    Check(devices == 1 && factories == 1 && chains == 1 && presents == 8, "the hooks saw one device, one factory and one swap chain; eight presents");
    Drain();
    // Frame generation off, as at the game's start: the game's threads through two window resizes.
    trace::Scenario("game-threads-off", false);
    SetWindowPos(window, nullptr, 0, 0, 1920, 1080, SWP_NOMOVE | SWP_NOZORDER);
    Check(SUCCEEDED(chain->ResizeBuffers(0, 1920, 1080, DXGI_FORMAT_UNKNOWN, g_chainFlags)), "ResizeBuffers to 1920x1080 for the game's threads");
    GameThreads(native, context.Get(), chain.Get(), window, 600);
    Drain();
    g_interval = 1; // the same with VSync on, as GTFO's setting has it
    GameThreads(native, context.Get(), chain.Get(), window, 600);
    g_interval = 0;
    Drain();
    int offTimeouts = g_waitTimeouts;
    trace::Scenario("frame-generation", true);
    FrameGeneration(native, device.Get(), context.Get(), chain.Get(), window);
    Drain();
    // After frame generation ran: the game's threads again, frame generation now off with its resources kept.
    trace::Scenario("game-threads-after", false);
    GameThreads(native, context.Get(), chain.Get(), window, 600);
    Drain();
    // The game's threads with frame generation inputs: switched off and on by the player and through the resizes, VSync off and on.
    trace::Scenario("game-threads-inputs", false);
    GameThreads(native, context.Get(), chain.Get(), window, 600, true);
    Drain();
    g_interval = 1;
    GameThreads(native, context.Get(), chain.Get(), window, 600, true);
    g_interval = 0;
    Drain();
    std::printf("  waits on the waitable object: %d, timed out (1 s each): %d\n", g_waits, g_waitTimeouts);
    Check(g_waitTimeouts == offTimeouts, "frame generation on, off and on again: every wait on the game's waitable object returns within a second");
    trace::Scenario("upscalers", true);
    Upscalers(native, argv[1], device.Get(), context.Get());
    // Frame generation switched while the game runs: six picks presented one by one, then picks from the main thread while the render
    // thread presents with inputs, through the resizes, switched off and on.
    trace::Scenario("switching", true);
    Switching(native, device.Get(), context.Get(), chain.Get(), window, readback);
    Drain();
    trace::Scenario("game-threads-switching", false);
    auto backend = (State)GetProcAddress(native, "ForgeFrameGen_FrameGenerationBackend");
    const int kindBefore = backend ? backend() : 0, switchesBefore = g_switches, failuresBefore = g_bridgeFailures;
    GameThreads(native, context.Get(), chain.Get(), window, 600, true, true);
    Drain();
    if (kindBefore)
    {
        std::printf("  game threads switching: %d switches\n", g_switches - switchesBefore);
        Check(g_switches - switchesBefore == 8 && backend() == kindBefore && g_bridgeFailures == failuresBefore,
              "game threads: eight picks from the main thread switch the display eight times without a failure, ending where it began");
    }
    // The game's resolution below its window's: inputs smaller than the buffers, of the same shape, with each frame generation.
    trace::Scenario("smaller-resolution", true);
    Scaled(native, device.Get(), context.Get(), chain.Get(), window, readback);
    Drain();
    if (g_fsr)
    {
        // What AMD FSR Frame Generation shows, and how far the game runs ahead of the GPU with it on.
        ShownFrames(native, device.Get(), context.Get(), chain.Get(), window);
        Latency(native, device.Get(), context.Get(), chain.Get(), window);
    }
    trace::Scenario("end", true);
    chain.Reset();
    DestroyWindow(window);
    Drain();
    if (!traceFile.empty()) Check(trace::Write(traceFile), "the Streamline trace is written");
    std::printf(g_failures ? "FAILED %d check(s)\n" : "ALL PASSED\n", g_failures);
    return g_failures ? 1 : 0;
}

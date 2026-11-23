// Developer measurement helper for ForgePerformance's Probe option. Not packaged: it is built by hand (CMakeLists.txt beside this file)
// and put next to ForgePerformance.dll as ForgePerformance.NativeProbe.dll only for a measurement run. Without the file the probe
// writes its lines without the columns that come from here.
//
// 1. Sampler: a native thread suspends the main and the render thread about a thousand times a second (and Unity's job worker
//    threads WorkersPerTick at a time in turn, so each of N workers is sampled at hz * WorkersPerTick / N), walks each stack with
//    RtlVirtualUnwind and writes the return addresses to samples.bin. A thread is suspended only while its registers and the top of
//    its stack are copied; the walk runs over the copy, so no lock is ever taken while a thread is suspended.
// 2. Direct3D 11: the draw, dispatch, render-target, map and create-shader entries of the device context and the device are counted
//    and timed through their vtable. On request a number of frames is captured as a timeline: one GPU timestamp query per
//    render-target binding or label, written to gpu-timeline.csv with the target's size and format and the draws made into it.
// Everything is written under the folder given to fpp_start.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define CINTERFACE
#define COBJMACROS
#define D3D11_NO_HELPERS
#include <windows.h>
#include <winternl.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <string>
#include <d3d11_4.h>
#include <intrin.h>
#include <atomic>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <algorithm>
#include <vector>

#define FPP_API extern "C" __declspec(dllexport)

namespace {

// ---------------------------------------------------------------------------------------------------------------- sampler
struct Module { uint64_t base, end; const RUNTIME_FUNCTION* functions; uint32_t count; };
// Targets 0 and 1 are the main and render thread and are sampled every tick; the rest are job workers, WorkersPerTick of them per tick in turn.
constexpr int MaxModules = 1024, MaxFrames = 96, MaxThreads = 64, FixedTargets = 2, WorkersPerTick = 3;
Module g_modules[MaxModules];
int g_moduleCount;
wchar_t g_folder[MAX_PATH];
HANDLE g_sampler, g_targets[MaxThreads];
uint64_t g_tebs[MaxThreads];
int g_targetCount;
std::atomic<bool> g_run{false};
std::atomic<int> g_hz{1000};
std::atomic<uint64_t> g_samples{0};
CONTEXT* g_context;

void RefreshModules()
{
    static HMODULE handles[MaxModules];
    DWORD needed = 0;
    if (!EnumProcessModules(GetCurrentProcess(), handles, sizeof handles, &needed)) return;
    int count = (int)std::min<size_t>(needed / sizeof(HMODULE), MaxModules);
    static Module fresh[MaxModules];
    int kept = 0;
    wchar_t path[MAX_PATH];
    wcscpy_s(path, g_folder); wcscat_s(path, L"\\modules.tsv");
    FILE* list = _wfsopen(path, L"wb", _SH_DENYNO);
    for (int i = 0; i < count; i++)
    {
        MODULEINFO info{};
        if (!GetModuleInformation(GetCurrentProcess(), handles[i], &info, sizeof info)) continue;
        auto base = (const uint8_t*)info.lpBaseOfDll;
        auto dos = (const IMAGE_DOS_HEADER*)base;
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) continue;
        auto nt = (const IMAGE_NT_HEADERS64*)(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) continue;
        const auto& directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
        Module& m = fresh[kept++];
        m.base = (uint64_t)base; m.end = m.base + info.SizeOfImage;
        m.functions = directory.VirtualAddress ? (const RUNTIME_FUNCTION*)(base + directory.VirtualAddress) : nullptr;
        m.count = directory.VirtualAddress ? directory.Size / sizeof(RUNTIME_FUNCTION) : 0;
        if (list)
        {
            wchar_t name[MAX_PATH]{};
            GetModuleFileNameW(handles[i], name, MAX_PATH);
            fwprintf(list, L"%llx\t%x\t%x\t%x\t%ls\n", m.base, info.SizeOfImage, nt->FileHeader.TimeDateStamp, nt->OptionalHeader.CheckSum, name);
        }
    }
    if (list) fclose(list);
    std::sort(fresh, fresh + kept, [](const Module& a, const Module& b) { return a.base < b.base; });
    memcpy(g_modules, fresh, sizeof(Module) * kept);
    g_moduleCount = kept;
}

const Module* FindModule(uint64_t address)
{
    int low = 0, high = g_moduleCount - 1;
    while (low <= high)
    {
        int mid = (low + high) / 2;
        if (address < g_modules[mid].base) high = mid - 1;
        else if (address >= g_modules[mid].end) low = mid + 1;
        else return &g_modules[mid];
    }
    return nullptr;
}

const RUNTIME_FUNCTION* FindFunction(const Module& m, uint64_t address)
{
    auto rva = (uint32_t)(address - m.base);
    int low = 0, high = (int)m.count - 1;
    while (low <= high)
    {
        int mid = (low + high) / 2;
        if (rva < m.functions[mid].BeginAddress) high = mid - 1;
        else if (rva >= m.functions[mid].EndAddress) low = mid + 1;
        else return &m.functions[mid];
    }
    return nullptr;
}

// A thread is suspended only long enough to copy its registers and the top of its stack; the walk then runs over the copy
// with the thread running again, so the function-table lookup (which takes a lock for JIT-compiled code) can never wait on
// a lock the suspended thread holds. Stack addresses the walk meets (the stack pointer, a frame pointer in rbp) are moved
// into the copy.
constexpr size_t StackCopyBytes = 160 * 1024;
uint8_t g_stackCopy[StackCopyBytes];

size_t Snapshot(int target)
{
    CONTEXT& c = *g_context;
    c.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
    if (!GetThreadContext(g_targets[target], &c)) return 0;
    uint64_t base = *(uint64_t*)(g_tebs[target] + 8), limit = *(uint64_t*)(g_tebs[target] + 16); // NT_TIB StackBase, StackLimit
    if (c.Rsp < limit || c.Rsp >= base || (c.Rsp & 7)) return 0;
    size_t size = (size_t)std::min<uint64_t>(base - c.Rsp, StackCopyBytes);
    memcpy(g_stackCopy, (const void*)c.Rsp, size);
    return size;
}

int Walk(size_t size, uint64_t* out)
{
    CONTEXT& c = *g_context;
    const uint64_t low = c.Rsp, high = c.Rsp + size, copy = (uint64_t)g_stackCopy, delta = copy - low;
    auto move = [&](DWORD64& value) { if (value >= low && value < high) value += delta; };
    move(c.Rsp); move(c.Rbp);
    int n = 0;
    __try
    {
        while (n < MaxFrames)
        {
            out[n++] = c.Rip;
            if (c.Rsp < copy || c.Rsp + 8 > copy + size) break;
            const uint64_t before = c.Rsp;
            DWORD64 image = 0;
            PRUNTIME_FUNCTION function = RtlLookupFunctionEntry(c.Rip, &image, nullptr);
            if (!function) { c.Rip = *(uint64_t*)c.Rsp; c.Rsp += 8; }
            else
            {
                void* handler; DWORD64 frame;
                RtlVirtualUnwind(UNW_FLAG_NHANDLER, image, c.Rip, function, &c, &handler, &frame, nullptr);
            }
            move(c.Rsp); move(c.Rbp);
            if (!c.Rip || c.Rsp <= before) break;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
    return n;
}
DWORD WINAPI Sampler(void*)
{
    wchar_t path[MAX_PATH];
    wcscpy_s(path, g_folder); wcscat_s(path, L"\\samples.bin");
    FILE* file = _wfsopen(path, L"wb", _SH_DENYNO);
    if (!file) return 1;
    LARGE_INTEGER frequency; QueryPerformanceFrequency(&frequency);
    uint64_t header[4] = {0x31504D4153505046ull /* FPPSAMP1 */, (uint64_t)frequency.QuadPart, (uint64_t)g_targetCount, 0};
    fwrite(header, sizeof header, 1, file);
    static uint8_t buffer[1 << 20];
    size_t used = 0;
    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */, TIMER_ALL_ACCESS);
    uint64_t nextModules = 0, nextFlush = 0;
    uint64_t frames[MaxFrames];
    uint64_t rotation = 0;
    while (g_run.load(std::memory_order_relaxed))
    {
        LARGE_INTEGER now; QueryPerformanceCounter(&now);
        if ((uint64_t)now.QuadPart >= nextModules) { RefreshModules(); nextModules = now.QuadPart + 5 * frequency.QuadPart; }
        // The two fixed targets every tick, then the next WorkersPerTick workers of the rotation.
        int workers = g_targetCount > FixedTargets ? g_targetCount - FixedTargets : 0;
        int tick = std::min(g_targetCount, FixedTargets) + std::min(workers, WorkersPerTick);
        for (int k = 0; k < tick; k++)
        {
            int t = k < FixedTargets ? k : FixedTargets + (int)((rotation + (uint64_t)(k - FixedTargets)) % (uint64_t)workers);
            if (SuspendThread(g_targets[t]) == (DWORD)-1) continue;
            size_t copied = Snapshot(t);
            ResumeThread(g_targets[t]);
            int n = copied ? Walk(copied, frames) : 0;
            if (!n) continue;
            QueryPerformanceCounter(&now);
            uint64_t stamp = (uint64_t)now.QuadPart;
            uint16_t head[2] = {(uint16_t)t, (uint16_t)n};
            memcpy(buffer + used, &stamp, 8); memcpy(buffer + used + 8, head, 4); memcpy(buffer + used + 12, frames, (size_t)n * 8);
            used += 12 + (size_t)n * 8;
            g_samples.fetch_add(1, std::memory_order_relaxed);
        }
        rotation += (uint64_t)std::min(workers, WorkersPerTick);
        if (used > sizeof buffer - 2 * MaxThreads * (12 + MaxFrames * 8) || (uint64_t)now.QuadPart >= nextFlush)
        {
            fwrite(buffer, 1, used, file); fflush(file); used = 0;
            nextFlush = now.QuadPart + frequency.QuadPart;
        }
        int hz = g_hz.load(std::memory_order_relaxed);
        if (timer)
        {
            LARGE_INTEGER due; due.QuadPart = -(10000000ll / (hz > 0 ? hz : 1000));
            SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE);
            WaitForSingleObject(timer, 50);
        }
        else Sleep(1);
    }
    fwrite(buffer, 1, used, file);
    fclose(file);
    if (timer) CloseHandle(timer);
    return 0;
}

uint64_t TebOf(HANDLE thread)
{
    struct Basic { NTSTATUS exit; void* teb; void* pid; void* tid; ULONG_PTR affinity; LONG priority; LONG base; } info{};
    typedef NTSTATUS(NTAPI* Query)(HANDLE, int, void*, ULONG, ULONG*);
    auto query = (Query)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationThread");
    return query && query(thread, 0, &info, sizeof info, nullptr) >= 0 ? (uint64_t)info.teb : 0;
}

// Unity's job worker threads: those whose description starts with "Worker" or contains "Job.Worker" (Unity names them through
// SetThreadDescription); if none carries such a name, the largest group of at least 6 other threads that started at the same
// address inside UnityPlayer.dll. The choice, each thread's description and its start address are written to workers.tsv.
uint64_t StartAddressOf(HANDLE thread)
{
    typedef NTSTATUS(NTAPI* Query)(HANDLE, int, void*, ULONG, ULONG*);
    auto query = (Query)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationThread");
    uint64_t start = 0;
    return query && query(thread, 9 /* ThreadQuerySetWin32StartAddress */, &start, sizeof start, nullptr) >= 0 ? start : 0;
}

void AddWorkers(const uint32_t* known, int knownCount)
{
    struct Row { uint32_t id; uint64_t start; std::wstring name; };
    std::vector<Row> rows;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return;
    THREADENTRY32 entry{sizeof entry};
    for (BOOL more = Thread32First(snapshot, &entry); more; more = Thread32Next(snapshot, &entry))
    {
        if (entry.th32OwnerProcessID != GetCurrentProcessId()) continue;
        bool skip = false;
        for (int i = 0; i < knownCount; i++) skip |= known[i] == entry.th32ThreadID;
        if (skip || entry.th32ThreadID == GetThreadId(GetCurrentThread())) continue;
        HANDLE thread = OpenThread(THREAD_QUERY_INFORMATION | THREAD_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ThreadID);
        if (!thread) continue;
        PWSTR text = nullptr;
        std::wstring name;
        if (SUCCEEDED(GetThreadDescription(thread, &text)) && text) { name = text; LocalFree(text); }
        rows.push_back({entry.th32ThreadID, StartAddressOf(thread), name});
        CloseHandle(thread);
    }
    CloseHandle(snapshot);
    std::vector<bool> picked(rows.size(), false);
    const char* method = "description";
    size_t count = 0;
    for (size_t i = 0; i < rows.size(); i++)
        if (rows[i].name.rfind(L"Worker", 0) == 0 || rows[i].name.find(L"Job.Worker") != std::wstring::npos) { picked[i] = true; count++; }
    if (!count)
    {
        method = "start-address";
        uint64_t unity = (uint64_t)GetModuleHandleW(L"UnityPlayer.dll");
        MODULEINFO info{};
        if (unity && GetModuleInformation(GetCurrentProcess(), (HMODULE)unity, &info, sizeof info))
        {
            uint64_t bestStart = 0; size_t best = 0;
            for (auto& a : rows)
            {
                if (a.start < unity || a.start >= unity + info.SizeOfImage) continue;
                size_t same = 0;
                for (auto& b : rows) same += b.start == a.start;
                if (same > best) { best = same; bestStart = a.start; }
            }
            if (best >= 6) for (size_t i = 0; i < rows.size(); i++) if (rows[i].start == bestStart) { picked[i] = true; count++; }
        }
    }
    wchar_t path[MAX_PATH];
    wcscpy_s(path, g_folder); wcscat_s(path, L"\\workers.tsv");
    FILE* list = _wfsopen(path, L"wb", _SH_DENYNO);
    for (size_t i = 0; i < rows.size(); i++)
    {
        if (list) fprintf(list, "%u\t%llx\t%d\t%ls\n", rows[i].id, rows[i].start, picked[i] ? 1 : 0, rows[i].name.c_str());
        if (!picked[i] || g_targetCount >= MaxThreads) continue;
        HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, rows[i].id);
        if (!thread) continue;
        uint64_t teb = TebOf(thread);
        if (!teb) { CloseHandle(thread); continue; }
        g_targets[g_targetCount] = thread; g_tebs[g_targetCount++] = teb;
    }
    if (list) { fprintf(list, "# method=%s picked=%zu\n", method, count); fclose(list); }
}

// ---------------------------------------------------------------------------------------------------------------- Direct3D 11
enum Counter
{
    DrawIndexed, Draw, DrawIndexedInstanced, DrawInstanced, DrawIndexedInstancedIndirect, DrawInstancedIndirect, Dispatch, DispatchIndirect,
    SetTargets, Map, Unmap, UpdateSubresource, Clear, Copy, SetShaderResources, SetConstantBuffers, SetShader, SetState, SetBuffers,
    CreateShader, CreateTexture, CreateBuffer, CreateView, CounterCount
};
const char* const CounterNames[CounterCount] = {
    "DrawIndexed", "Draw", "DrawIndexedInstanced", "DrawInstanced", "DrawIndexedInstancedIndirect", "DrawInstancedIndirect", "Dispatch",
    "DispatchIndirect", "SetTargets", "Map", "Unmap", "UpdateSubresource", "Clear", "Copy", "SetShaderResources", "SetConstantBuffers",
    "SetShader", "SetState", "SetBuffers", "CreateShader", "CreateTexture", "CreateBuffer", "CreateView"};
// Written by the render thread (and whichever thread creates resources), read by the main thread once a window: plain 64-bit adds.
uint64_t g_calls[CounterCount], g_ticks[CounterCount], g_instances, g_indices;
double g_tscPerMs = 1;

struct Scope
{
    Counter counter; uint64_t start;
    explicit Scope(Counter c) : counter(c), start(__rdtsc()) {}
    ~Scope() { g_calls[counter]++; g_ticks[counter] += __rdtsc() - start; }
};

// The immediate context answers each interface version (ID3D11DeviceContext, 1, 2, 3, 4) through its own function table, and
// Unity draws through a later one than GetImmediateContext hands out: every table is patched and keeps its own original entries.
struct ContextTable { ID3D11DeviceContextVtbl* table; ID3D11DeviceContextVtbl original; };
constexpr int MaxTables = 8;
ContextTable g_tables[MaxTables];
int g_tableCount;
inline const ID3D11DeviceContextVtbl& Original(ID3D11DeviceContext* c)
{
    for (int i = 0; i < g_tableCount; i++) if (g_tables[i].table == c->lpVtbl) return g_tables[i].original;
    return g_tables[0].original;
}ID3D11DeviceVtbl g_device0;
ID3D11DeviceContext* g_d3dContext;
ID3D11Device* g_d3dDevice;
bool g_d3dInstalled;
int g_tableSwaps;

// Shader creations, each with when and how long: stutter from shaders compiled in the driver shows here.
struct ShaderRow { uint64_t qpc; uint32_t microseconds; uint32_t bytes; uint32_t kind; uint32_t thread; };
constexpr int MaxShaderRows = 1 << 16;
ShaderRow g_shaders[MaxShaderRows];
std::atomic<int> g_shaderRows{0};

// GPU timeline capture.
struct Segment
{
    uint32_t frame, label, targets, width, height, format, depth, draws, dispatches, clears;
    uint64_t instances, indices, target;
    ID3D11Query* query; uint64_t stamp;
};
std::vector<Segment> g_segments;
std::vector<ID3D11Query*> g_disjoint;
int g_captureFrames;          // frames still to capture (render thread)
std::atomic<int> g_captureRequest{0};
std::atomic<int> g_captureState{0}; // 0 idle, 1 armed/capturing, 2 resolving, 3 written
uint32_t g_frame, g_label;
bool g_capturing, g_lastWasDispatch;
int g_captureIndex;

void BeginSegment(uint32_t targets, ID3D11RenderTargetView* first, ID3D11DepthStencilView* depth)
{
    Segment s{};
    s.frame = g_frame; s.label = g_label; s.targets = targets; s.depth = depth != nullptr; s.target = (uint64_t)first;
    ID3D11Resource* resource = nullptr;
    if (first) ID3D11RenderTargetView_GetResource(first, &resource);
    else if (depth) ID3D11DepthStencilView_GetResource(depth, &resource);
    if (resource)
    {
        D3D11_RESOURCE_DIMENSION dimension;
        ID3D11Resource_GetType(resource, &dimension);
        if (dimension == D3D11_RESOURCE_DIMENSION_TEXTURE2D)
        {
            D3D11_TEXTURE2D_DESC desc;
            ID3D11Texture2D_GetDesc((ID3D11Texture2D*)resource, &desc);
            s.width = desc.Width; s.height = desc.Height; s.format = desc.Format;
            if (!first) s.target = (uint64_t)resource;
        }
        ID3D11Resource_Release(resource);
    }
    D3D11_QUERY_DESC description{D3D11_QUERY_TIMESTAMP, 0};
    if (SUCCEEDED(g_device0.CreateQuery(g_d3dDevice, &description, &s.query)) && s.query) g_tables[0].original.End(g_d3dContext, (ID3D11Asynchronous*)s.query);
    g_segments.push_back(s);
    g_lastWasDispatch = false;
}

void SplitForKind(bool dispatch)
{
    if (!g_capturing || g_segments.empty()) return;
    Segment& last = g_segments.back();
    if (dispatch == g_lastWasDispatch || (last.draws == 0 && last.dispatches == 0)) { g_lastWasDispatch = dispatch; return; }
    Segment copy = last;
    copy.draws = copy.dispatches = copy.clears = 0; copy.instances = copy.indices = 0; copy.query = nullptr; copy.label = g_label;
    D3D11_QUERY_DESC description{D3D11_QUERY_TIMESTAMP, 0};
    if (SUCCEEDED(g_device0.CreateQuery(g_d3dDevice, &description, &copy.query)) && copy.query) g_tables[0].original.End(g_d3dContext, (ID3D11Asynchronous*)copy.query);
    g_segments.push_back(copy);
    g_lastWasDispatch = dispatch;
}

inline void CountDraw(uint64_t instances, uint64_t indices)
{
    g_instances += instances; g_indices += indices * instances;
    if (g_capturing)
    {
        SplitForKind(false);
        if (!g_segments.empty()) { Segment& s = g_segments.back(); s.draws++; s.instances += instances; s.indices += indices * instances; }
    }
}
inline void CountDispatch()
{
    if (g_capturing) { SplitForKind(true); if (!g_segments.empty()) g_segments.back().dispatches++; }
}

void STDMETHODCALLTYPE HookDrawIndexed(ID3D11DeviceContext* c, UINT indices, UINT start, INT base)
{ CountDraw(1, indices); Scope s(DrawIndexed); Original(c).DrawIndexed(c, indices, start, base); }
void STDMETHODCALLTYPE HookDraw(ID3D11DeviceContext* c, UINT vertices, UINT start)
{ CountDraw(1, vertices); Scope s(Draw); Original(c).Draw(c, vertices, start); }
void STDMETHODCALLTYPE HookDrawIndexedInstanced(ID3D11DeviceContext* c, UINT indices, UINT instances, UINT start, INT base, UINT first)
{ CountDraw(instances, indices); Scope s(DrawIndexedInstanced); Original(c).DrawIndexedInstanced(c, indices, instances, start, base, first); }
void STDMETHODCALLTYPE HookDrawInstanced(ID3D11DeviceContext* c, UINT vertices, UINT instances, UINT start, UINT first)
{ CountDraw(instances, vertices); Scope s(DrawInstanced); Original(c).DrawInstanced(c, vertices, instances, start, first); }
void STDMETHODCALLTYPE HookDrawIndexedInstancedIndirect(ID3D11DeviceContext* c, ID3D11Buffer* arguments, UINT offset)
{ CountDraw(1, 0); Scope s(DrawIndexedInstancedIndirect); Original(c).DrawIndexedInstancedIndirect(c, arguments, offset); }
void STDMETHODCALLTYPE HookDrawInstancedIndirect(ID3D11DeviceContext* c, ID3D11Buffer* arguments, UINT offset)
{ CountDraw(1, 0); Scope s(DrawInstancedIndirect); Original(c).DrawInstancedIndirect(c, arguments, offset); }
void STDMETHODCALLTYPE HookDispatch(ID3D11DeviceContext* c, UINT x, UINT y, UINT z)
{ CountDispatch(); Scope s(Dispatch); Original(c).Dispatch(c, x, y, z); }
void STDMETHODCALLTYPE HookDispatchIndirect(ID3D11DeviceContext* c, ID3D11Buffer* arguments, UINT offset)
{ CountDispatch(); Scope s(DispatchIndirect); Original(c).DispatchIndirect(c, arguments, offset); }
void STDMETHODCALLTYPE HookSetTargets(ID3D11DeviceContext* c, UINT count, ID3D11RenderTargetView* const* views, ID3D11DepthStencilView* depth)
{
    if (g_capturing) BeginSegment(count, count && views ? views[0] : nullptr, depth);
    Scope s(SetTargets); Original(c).OMSetRenderTargets(c, count, views, depth);
}
void STDMETHODCALLTYPE HookSetTargetsAndViews(ID3D11DeviceContext* c, UINT count, ID3D11RenderTargetView* const* views, ID3D11DepthStencilView* depth,
    UINT slot, UINT viewCount, ID3D11UnorderedAccessView* const* accessViews, const UINT* initial)
{
    bool keeps = count == D3D11_KEEP_RENDER_TARGETS_AND_DEPTH_STENCIL;
    if (g_capturing && !keeps) BeginSegment(count, count && views ? views[0] : nullptr, depth);
    Scope s(SetTargets); Original(c).OMSetRenderTargetsAndUnorderedAccessViews(c, count, views, depth, slot, viewCount, accessViews, initial);
}
HRESULT STDMETHODCALLTYPE HookMap(ID3D11DeviceContext* c, ID3D11Resource* r, UINT sub, D3D11_MAP type, UINT flags, D3D11_MAPPED_SUBRESOURCE* mapped)
{ Scope s(Map); return Original(c).Map(c, r, sub, type, flags, mapped); }
void STDMETHODCALLTYPE HookUnmap(ID3D11DeviceContext* c, ID3D11Resource* r, UINT sub)
{ Scope s(Unmap); Original(c).Unmap(c, r, sub); }
void STDMETHODCALLTYPE HookUpdateSubresource(ID3D11DeviceContext* c, ID3D11Resource* r, UINT sub, const D3D11_BOX* box, const void* data, UINT row, UINT depth)
{ Scope s(UpdateSubresource); Original(c).UpdateSubresource(c, r, sub, box, data, row, depth); }
void STDMETHODCALLTYPE HookClearTarget(ID3D11DeviceContext* c, ID3D11RenderTargetView* view, const FLOAT color[4])
{ if (g_capturing && !g_segments.empty()) g_segments.back().clears++; Scope s(Clear); Original(c).ClearRenderTargetView(c, view, color); }
void STDMETHODCALLTYPE HookClearDepth(ID3D11DeviceContext* c, ID3D11DepthStencilView* view, UINT flags, FLOAT depth, UINT8 stencil)
{ if (g_capturing && !g_segments.empty()) g_segments.back().clears++; Scope s(Clear); Original(c).ClearDepthStencilView(c, view, flags, depth, stencil); }
void STDMETHODCALLTYPE HookCopyResource(ID3D11DeviceContext* c, ID3D11Resource* to, ID3D11Resource* from)
{ Scope s(Copy); Original(c).CopyResource(c, to, from); }
void STDMETHODCALLTYPE HookCopyRegion(ID3D11DeviceContext* c, ID3D11Resource* to, UINT sub, UINT x, UINT y, UINT z, ID3D11Resource* from, UINT fromSub, const D3D11_BOX* box)
{ Scope s(Copy); Original(c).CopySubresourceRegion(c, to, sub, x, y, z, from, fromSub, box); }
void STDMETHODCALLTYPE HookPSSetShaderResources(ID3D11DeviceContext* c, UINT slot, UINT count, ID3D11ShaderResourceView* const* views)
{ Scope s(SetShaderResources); Original(c).PSSetShaderResources(c, slot, count, views); }
void STDMETHODCALLTYPE HookVSSetConstantBuffers(ID3D11DeviceContext* c, UINT slot, UINT count, ID3D11Buffer* const* buffers)
{ Scope s(SetConstantBuffers); Original(c).VSSetConstantBuffers(c, slot, count, buffers); }
void STDMETHODCALLTYPE HookPSSetConstantBuffers(ID3D11DeviceContext* c, UINT slot, UINT count, ID3D11Buffer* const* buffers)
{ Scope s(SetConstantBuffers); Original(c).PSSetConstantBuffers(c, slot, count, buffers); }
void STDMETHODCALLTYPE HookPSSetShader(ID3D11DeviceContext* c, ID3D11PixelShader* shader, ID3D11ClassInstance* const* instances, UINT count)
{ Scope s(SetShader); Original(c).PSSetShader(c, shader, instances, count); }
void STDMETHODCALLTYPE HookVSSetShader(ID3D11DeviceContext* c, ID3D11VertexShader* shader, ID3D11ClassInstance* const* instances, UINT count)
{ Scope s(SetShader); Original(c).VSSetShader(c, shader, instances, count); }
void STDMETHODCALLTYPE HookOMSetBlendState(ID3D11DeviceContext* c, ID3D11BlendState* state, const FLOAT factor[4], UINT mask)
{ Scope s(SetState); Original(c).OMSetBlendState(c, state, factor, mask); }
void STDMETHODCALLTYPE HookOMSetDepthStencilState(ID3D11DeviceContext* c, ID3D11DepthStencilState* state, UINT reference)
{ Scope s(SetState); Original(c).OMSetDepthStencilState(c, state, reference); }
void STDMETHODCALLTYPE HookRSSetState(ID3D11DeviceContext* c, ID3D11RasterizerState* state)
{ Scope s(SetState); Original(c).RSSetState(c, state); }
void STDMETHODCALLTYPE HookIASetVertexBuffers(ID3D11DeviceContext* c, UINT slot, UINT count, ID3D11Buffer* const* buffers, const UINT* strides, const UINT* offsets)
{ Scope s(SetBuffers); Original(c).IASetVertexBuffers(c, slot, count, buffers, strides, offsets); }
void STDMETHODCALLTYPE HookIASetIndexBuffer(ID3D11DeviceContext* c, ID3D11Buffer* buffer, DXGI_FORMAT format, UINT offset)
{ Scope s(SetBuffers); Original(c).IASetIndexBuffer(c, buffer, format, offset); }

template <typename Create> HRESULT Shader(uint32_t kind, SIZE_T bytes, Create create)
{
    LARGE_INTEGER a, b, f;
    QueryPerformanceCounter(&a);
    HRESULT result;
    { Scope s(CreateShader); result = create(); }
    QueryPerformanceCounter(&b); QueryPerformanceFrequency(&f);
    int row = g_shaderRows.fetch_add(1, std::memory_order_relaxed);
    if (row < MaxShaderRows)
        g_shaders[row] = {(uint64_t)a.QuadPart, (uint32_t)((b.QuadPart - a.QuadPart) * 1000000 / f.QuadPart), (uint32_t)bytes, kind, GetCurrentThreadId()};
    return result;
}
HRESULT STDMETHODCALLTYPE HookCreateVertexShader(ID3D11Device* d, const void* code, SIZE_T size, ID3D11ClassLinkage* linkage, ID3D11VertexShader** out)
{ return Shader(0, size, [&] { return g_device0.CreateVertexShader(d, code, size, linkage, out); }); }
HRESULT STDMETHODCALLTYPE HookCreatePixelShader(ID3D11Device* d, const void* code, SIZE_T size, ID3D11ClassLinkage* linkage, ID3D11PixelShader** out)
{ return Shader(1, size, [&] { return g_device0.CreatePixelShader(d, code, size, linkage, out); }); }
HRESULT STDMETHODCALLTYPE HookCreateGeometryShader(ID3D11Device* d, const void* code, SIZE_T size, ID3D11ClassLinkage* linkage, ID3D11GeometryShader** out)
{ return Shader(2, size, [&] { return g_device0.CreateGeometryShader(d, code, size, linkage, out); }); }
HRESULT STDMETHODCALLTYPE HookCreateHullShader(ID3D11Device* d, const void* code, SIZE_T size, ID3D11ClassLinkage* linkage, ID3D11HullShader** out)
{ return Shader(3, size, [&] { return g_device0.CreateHullShader(d, code, size, linkage, out); }); }
HRESULT STDMETHODCALLTYPE HookCreateDomainShader(ID3D11Device* d, const void* code, SIZE_T size, ID3D11ClassLinkage* linkage, ID3D11DomainShader** out)
{ return Shader(4, size, [&] { return g_device0.CreateDomainShader(d, code, size, linkage, out); }); }
HRESULT STDMETHODCALLTYPE HookCreateComputeShader(ID3D11Device* d, const void* code, SIZE_T size, ID3D11ClassLinkage* linkage, ID3D11ComputeShader** out)
{ return Shader(5, size, [&] { return g_device0.CreateComputeShader(d, code, size, linkage, out); }); }
HRESULT STDMETHODCALLTYPE HookCreateTexture2D(ID3D11Device* d, const D3D11_TEXTURE2D_DESC* desc, const D3D11_SUBRESOURCE_DATA* data, ID3D11Texture2D** out)
{ Scope s(CreateTexture); return g_device0.CreateTexture2D(d, desc, data, out); }
HRESULT STDMETHODCALLTYPE HookCreateBuffer(ID3D11Device* d, const D3D11_BUFFER_DESC* desc, const D3D11_SUBRESOURCE_DATA* data, ID3D11Buffer** out)
{ Scope s(CreateBuffer); return g_device0.CreateBuffer(d, desc, data, out); }
HRESULT STDMETHODCALLTYPE HookCreateShaderResourceView(ID3D11Device* d, ID3D11Resource* r, const D3D11_SHADER_RESOURCE_VIEW_DESC* desc, ID3D11ShaderResourceView** out)
{ Scope s(CreateView); return g_device0.CreateShaderResourceView(d, r, desc, out); }

template <typename T> void Swap(T* slot, T hook)
{
    DWORD old;
    if (!VirtualProtect(slot, sizeof(T), PAGE_READWRITE, &old)) return;
    *slot = hook;
    VirtualProtect(slot, sizeof(T), old, &old);
}

// The runtime swaps the immediate context's whole function table when its state changes (multithread protection turned on by
// another component, for one), so the table is looked at again every frame and the one in use is patched.
void PatchTable(ID3D11DeviceContextVtbl* c)
{
    for (int i = 0; i < g_tableCount; i++) if (g_tables[i].table == c) return;
    if (g_tableCount == MaxTables || c->DrawIndexed == &HookDrawIndexed) return;
    g_tables[g_tableCount].original = *c;
    g_tables[g_tableCount].table = c;
    g_tableCount++;
    Swap(&c->DrawIndexed, &HookDrawIndexed); Swap(&c->Draw, &HookDraw);
    Swap(&c->DrawIndexedInstanced, &HookDrawIndexedInstanced); Swap(&c->DrawInstanced, &HookDrawInstanced);
    Swap(&c->DrawIndexedInstancedIndirect, &HookDrawIndexedInstancedIndirect); Swap(&c->DrawInstancedIndirect, &HookDrawInstancedIndirect);
    Swap(&c->Dispatch, &HookDispatch); Swap(&c->DispatchIndirect, &HookDispatchIndirect);
    Swap(&c->OMSetRenderTargets, &HookSetTargets); Swap(&c->OMSetRenderTargetsAndUnorderedAccessViews, &HookSetTargetsAndViews);
    Swap(&c->Map, &HookMap); Swap(&c->Unmap, &HookUnmap); Swap(&c->UpdateSubresource, &HookUpdateSubresource);
    Swap(&c->ClearRenderTargetView, &HookClearTarget); Swap(&c->ClearDepthStencilView, &HookClearDepth);
    Swap(&c->CopyResource, &HookCopyResource); Swap(&c->CopySubresourceRegion, &HookCopyRegion);
    Swap(&c->PSSetShaderResources, &HookPSSetShaderResources);
    Swap(&c->VSSetConstantBuffers, &HookVSSetConstantBuffers); Swap(&c->PSSetConstantBuffers, &HookPSSetConstantBuffers);
    Swap(&c->PSSetShader, &HookPSSetShader); Swap(&c->VSSetShader, &HookVSSetShader);
    Swap(&c->OMSetBlendState, &HookOMSetBlendState); Swap(&c->OMSetDepthStencilState, &HookOMSetDepthStencilState); Swap(&c->RSSetState, &HookRSSetState);
    Swap(&c->IASetVertexBuffers, &HookIASetVertexBuffers); Swap(&c->IASetIndexBuffer, &HookIASetIndexBuffer);
    g_tableSwaps++;
}

// Looked at again every frame: the runtime can also swap a table when the context's state changes.
void HookContext()
{
    PatchTable(g_d3dContext->lpVtbl);
    static const IID* const versions[] = {&IID_ID3D11DeviceContext1, &IID_ID3D11DeviceContext2, &IID_ID3D11DeviceContext3, &IID_ID3D11DeviceContext4};
    for (const IID* version : versions)
    {
        ID3D11DeviceContext* other = nullptr;
        if (SUCCEEDED(g_tables[0].original.QueryInterface(g_d3dContext, *version, (void**)&other)) && other)
        {
            PatchTable(other->lpVtbl);
            other->lpVtbl->Release(other);
        }
    }
}
void WriteTimeline(){
    wchar_t path[MAX_PATH];
    swprintf_s(path, L"%ls\\gpu-timeline-%d.csv", g_folder, g_captureIndex);
    FILE* file = _wfsopen(path, L"wb", _SH_DENYNO);
    if (file)
    {
        fprintf(file, "frame,segment,label,targets,width,height,format,depth,target,draws,instances,indices,dispatches,clears,gpu_us,disjoint\n");
        size_t frame = 0;
        int ordinal = 0;
        for (size_t i = 0; i < g_segments.size(); i++)
        {
            const Segment& s = g_segments[i];
            if (i && s.frame != g_segments[i - 1].frame) { frame++; ordinal = 0; }
            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint{};
            bool known = frame < g_disjoint.size() && g_disjoint[frame]
                && g_tables[0].original.GetData(g_d3dContext, (ID3D11Asynchronous*)g_disjoint[frame], &disjoint, sizeof disjoint, 0) == S_OK;
            double microseconds = -1;
            if (known && disjoint.Frequency && i + 1 < g_segments.size() && s.stamp && g_segments[i + 1].stamp && g_segments[i + 1].frame <= s.frame + 1)
                microseconds = (double)(int64_t)(g_segments[i + 1].stamp - s.stamp) * 1e6 / (double)disjoint.Frequency;
            fprintf(file, "%u,%d,%u,%u,%u,%u,%u,%u,%llx,%u,%llu,%llu,%u,%u,%.1f,%d\n", s.frame, ordinal++, s.label, s.targets, s.width, s.height, s.format,
                s.depth, s.target, s.draws, s.instances, s.indices, s.dispatches, s.clears, microseconds, known ? (int)disjoint.Disjoint : -1);
        }
        fclose(file);
    }
    for (Segment& s : g_segments) if (s.query) ID3D11Query_Release(s.query);
    for (ID3D11Query* q : g_disjoint) if (q) ID3D11Query_Release(q);
    g_segments.clear(); g_disjoint.clear();
}

// Unity calls this on the render thread: event 1 is the start of a frame, anything else a label for the segments that follow.
void STDMETHODCALLTYPE RenderEvent(int event)
{
    if (!g_d3dInstalled) return;
    HookContext();
    if (event != 1)
    {
        g_label = (uint32_t)event;
        if (g_capturing && !g_segments.empty())
        {   // a label starts a new segment on the same targets
            Segment copy = g_segments.back();
            copy.draws = copy.dispatches = copy.clears = 0; copy.instances = copy.indices = 0; copy.query = nullptr; copy.label = g_label;
            D3D11_QUERY_DESC description{D3D11_QUERY_TIMESTAMP, 0};
            if (SUCCEEDED(g_device0.CreateQuery(g_d3dDevice, &description, &copy.query)) && copy.query) g_tables[0].original.End(g_d3dContext, (ID3D11Asynchronous*)copy.query);
            g_segments.push_back(copy);
        }
        return;
    }
    g_frame++;
    g_label = 0;
    int state = g_captureState.load(std::memory_order_relaxed);
    if (g_capturing)
    {
        if (!g_disjoint.empty() && g_disjoint.back()) g_tables[0].original.End(g_d3dContext, (ID3D11Asynchronous*)g_disjoint.back());
        if (--g_captureFrames <= 0)
        {   // one more stamp closes the last captured frame
            g_capturing = false;
            Segment end{}; end.frame = g_frame; end.label = 0xFFFFFFFF;
            D3D11_QUERY_DESC description{D3D11_QUERY_TIMESTAMP, 0};
            if (SUCCEEDED(g_device0.CreateQuery(g_d3dDevice, &description, &end.query)) && end.query) g_tables[0].original.End(g_d3dContext, (ID3D11Asynchronous*)end.query);
            g_segments.push_back(end);
            g_captureState.store(2, std::memory_order_relaxed);
            return;
        }
    }
    if (!g_capturing && state == 1 && g_captureRequest.load(std::memory_order_relaxed) > 0)
    {
        g_captureFrames = g_captureRequest.exchange(0, std::memory_order_relaxed);
        g_capturing = true;
        g_segments.reserve(1 << 16);
    }
    if (g_capturing)
    {
        ID3D11Query* disjoint = nullptr;
        D3D11_QUERY_DESC description{D3D11_QUERY_TIMESTAMP_DISJOINT, 0};
        if (SUCCEEDED(g_device0.CreateQuery(g_d3dDevice, &description, &disjoint)) && disjoint) g_tables[0].original.Begin(g_d3dContext, (ID3D11Asynchronous*)disjoint);
        g_disjoint.push_back(disjoint);
        // The frame's first segment keeps whatever targets are bound.
        ID3D11RenderTargetView* view = nullptr; ID3D11DepthStencilView* depth = nullptr;
        g_tables[0].original.OMGetRenderTargets(g_d3dContext, 1, &view, &depth);
        BeginSegment(view ? 1u : 0u, view, depth);
        if (view) ID3D11RenderTargetView_Release(view);
        if (depth) ID3D11DepthStencilView_Release(depth);
        return;
    }
    if (g_captureState.load(std::memory_order_relaxed) == 2)
    {   // every query answered? then write; else look again next frame
        for (Segment& s : g_segments)
        {
            if (s.stamp || !s.query) continue;
            if (g_tables[0].original.GetData(g_d3dContext, (ID3D11Asynchronous*)s.query, &s.stamp, sizeof s.stamp, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) return;
        }
        WriteTimeline();
        g_captureIndex++;
        g_captureState.store(3, std::memory_order_relaxed);
    }
}

} // namespace

/// Starts the sampler on the given threads (main first). Returns the number of threads opened.
FPP_API int fpp_start(const wchar_t* folder, const uint32_t* threads, int count, int hz)
{
    if (g_run.load()) return -1;
    wcsncpy_s(g_folder, folder, _TRUNCATE);
    CreateDirectoryW(g_folder, nullptr);
    g_targetCount = 0;
    for (int i = 0; i < count && g_targetCount < MaxThreads; i++)
    {
        HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, threads[i]);
        if (!thread) continue;
        uint64_t teb = TebOf(thread);
        if (!teb) { CloseHandle(thread); continue; }
        g_targets[g_targetCount] = thread; g_tebs[g_targetCount++] = teb;
    }
    if (!g_targetCount) return 0;
    AddWorkers(threads, count);
    if (!g_context) g_context = (CONTEXT*)_aligned_malloc(sizeof(CONTEXT), 16);
    g_hz.store(hz);
    LARGE_INTEGER a, b, f; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&a);
    uint64_t t0 = __rdtsc(); Sleep(20); uint64_t t1 = __rdtsc(); QueryPerformanceCounter(&b);
    g_tscPerMs = (double)(t1 - t0) / ((double)(b.QuadPart - a.QuadPart) * 1000.0 / (double)f.QuadPart);
    g_run.store(true);
    g_sampler = CreateThread(nullptr, 0, Sampler, nullptr, 0, nullptr);
    if (g_sampler) { SetThreadPriority(g_sampler, THREAD_PRIORITY_TIME_CRITICAL); SetThreadDescription(g_sampler, L"ForgePerformance sampler"); }
    return g_targetCount;
}

FPP_API void fpp_rate(int hz) { g_hz.store(hz); }
FPP_API uint64_t fpp_samples() { return g_samples.load(); }

/// Stops the sampler and writes the shader list. The Direct3D entries stay hooked (they only count).
FPP_API void fpp_stop()
{
    if (g_run.exchange(false) && g_sampler) { WaitForSingleObject(g_sampler, 5000); CloseHandle(g_sampler); g_sampler = nullptr; }
    wchar_t path[MAX_PATH];
    wcscpy_s(path, g_folder); wcscat_s(path, L"\\shaders.csv");
    FILE* file = _wfsopen(path, L"wb", _SH_DENYNO);
    if (!file) return;
    fprintf(file, "qpc,microseconds,bytes,kind,thread\n");
    int rows = std::min(g_shaderRows.load(), MaxShaderRows);
    for (int i = 0; i < rows; i++)
        fprintf(file, "%llu,%u,%u,%u,%u\n", g_shaders[i].qpc, g_shaders[i].microseconds, g_shaders[i].bytes, g_shaders[i].kind, g_shaders[i].thread);
    fclose(file);
}

/// Hooks the device and immediate context that own the given ID3D11Resource (any texture's native pointer). Once.
FPP_API int fpp_install_d3d(void* anyResource)
{
    if (g_d3dInstalled) return 1;
    if (!anyResource) return 0;
    ID3D11Device* device = nullptr;
    ID3D11Resource_GetDevice((ID3D11Resource*)anyResource, &device);
    if (!device) return 0;
    ID3D11DeviceContext* context = nullptr;
    ID3D11Device_GetImmediateContext(device, &context);
    if (!context) { ID3D11Device_Release(device); return 0; }
    g_d3dDevice = device; g_d3dContext = context; // both stay referenced for the life of the process
    g_device0 = *device->lpVtbl;
    HookContext();
    ID3D11DeviceVtbl* d = device->lpVtbl;
    Swap(&d->CreateVertexShader, &HookCreateVertexShader); Swap(&d->CreatePixelShader, &HookCreatePixelShader);
    Swap(&d->CreateGeometryShader, &HookCreateGeometryShader); Swap(&d->CreateHullShader, &HookCreateHullShader);
    Swap(&d->CreateDomainShader, &HookCreateDomainShader); Swap(&d->CreateComputeShader, &HookCreateComputeShader);
    Swap(&d->CreateTexture2D, &HookCreateTexture2D); Swap(&d->CreateBuffer, &HookCreateBuffer);
    Swap(&d->CreateShaderResourceView, &HookCreateShaderResourceView);
    g_d3dInstalled = true;
    return 1;
}

void STDMETHODCALLTYPE RenderEventData(int event, void*) { RenderEvent(event); }
FPP_API void* fpp_render_event() { return (void*)&RenderEvent; }
FPP_API void* fpp_render_event_data() { return (void*)&RenderEventData; }

/// Asks for a timeline of the next frames. fpp_capture_state: 0 idle, 1 armed or capturing, 2 waiting for the GPU, 3 written.
FPP_API int fpp_capture(int frames)
{
    int state = g_captureState.load();
    if (!g_d3dInstalled || state == 1 || state == 2) return 0;
    g_captureRequest.store(frames);
    g_captureState.store(1);
    return 1;
}
FPP_API int fpp_capture_state() { return g_captureState.load(); }

/// Copies the cumulative counters: calls[CounterCount], then milliseconds[CounterCount], instances and indices. Returns CounterCount.
FPP_API int fpp_counters(uint64_t* calls, double* milliseconds, uint64_t* instances, uint64_t* indices, uint32_t* shaderRows)
{
    if (g_d3dInstalled) HookContext();
    for (int i = 0; i < CounterCount; i++) { calls[i] = g_calls[i]; milliseconds[i] = (double)g_ticks[i] / g_tscPerMs; }
    *instances = g_instances; *indices = g_indices; *shaderRows = (uint32_t)g_shaderRows.load() | ((uint32_t)g_tableSwaps << 24);
    return CounterCount;
}
FPP_API int fpp_table_swaps() { return g_tableSwaps; }
FPP_API const char* fpp_counter_name(int index) { return index >= 0 && index < CounterCount ? CounterNames[index] : ""; }

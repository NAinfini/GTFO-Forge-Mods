// ForgeFrameGen AMD FSR SDK loader. See ffx_runtime.h.
#include "ffx_runtime.h"
#include "bridge_common.h"
#include <mutex>

namespace ffxrt
{
namespace
{
constexpr const wchar_t *kLoader = L"amd_fidelityfx_loader_dx12.dll";
std::mutex g_lock;
ffxFunctions g_ffx{};
bool g_loaded = false;
} // namespace

bool Load(const std::wstring &folder, const wchar_t *effect, std::string &why)
{
    std::lock_guard<std::mutex> lock(g_lock);
    std::string foreign;
    if (ForeignModule(folder, {L"amd_fidelityfx_"}, foreign))
    {
        why = "another copy of the AMD FSR libraries is loaded (" + foreign + ")";
        return false;
    }
    std::wstring path = folder + L"\\" + effect;
    if (!LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH))
    {
        why = "the packaged AMD FSR libraries could not be loaded (Windows error " + std::to_string(GetLastError()) + ")";
        return false;
    }
    if (g_loaded) return true;
    std::wstring loader = folder + L"\\" + kLoader;
    HMODULE module = LoadLibraryExW(loader.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!module)
    {
        why = "the packaged AMD FSR libraries could not be loaded (Windows error " + std::to_string(GetLastError()) + ")";
        return false;
    }
    ffxLoadFunctions(&g_ffx, module);
    if (!g_ffx.CreateContext || !g_ffx.DestroyContext || !g_ffx.Configure || !g_ffx.Query || !g_ffx.Dispatch)
    {
        why = "the AMD FSR loader lacks its entry points";
        return false;
    }
    g_loaded = true;
    return true;
}

const ffxFunctions &Api() { return g_ffx; }
} // namespace ffxrt

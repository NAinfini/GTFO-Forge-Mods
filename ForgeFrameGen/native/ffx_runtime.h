// ForgeFrameGen's one copy of the AMD FSR SDK loader, shared by FSR super resolution (fsr_upscale.cpp) and FSR frame generation
// (fsr_frame_generation.cpp): only the packaged, signed DLLs from the plugin folder are loaded, each effect DLL by its full path before
// the loader (which takes an effect DLL already in the process before looking in its own folder).
#pragma once
#include <string>
#include "ffx_api.h"
#include "ffx_api_loader.h"

namespace ffxrt
{
constexpr const wchar_t *kUpscaler = L"amd_fidelityfx_upscaler_dx12.dll", *kFrameGeneration = L"amd_fidelityfx_framegeneration_dx12.dll";

// Loads effect (one of the names above) and, the first time, the loader from folder. False with the reason in why: another copy of
// the SDK's DLLs is in the process, or a packaged DLL could not be loaded or lacks its entry points.
bool Load(const std::wstring &folder, const wchar_t *effect, std::string &why);
// The loader's entry points; valid after a Load that returned true.
const ffxFunctions &Api();
} // namespace ffxrt

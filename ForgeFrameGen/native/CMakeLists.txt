# ForgeFrameGen native bridge. Built by tools/build_native.ps1, which first fetches the pinned NVIDIA DLSS SDK into
# artifacts/cache/tools/dlss-sdk/<tag> and passes it as DLSS_SDK, the pinned Streamline files as STREAMLINE_SDK and the pinned AMD FSR SDK files as
# FFX_SDK. Nothing of any SDK is in Git.
cmake_minimum_required(VERSION 3.20)
project(ForgeFrameGenNative CXX)

if(NOT DLSS_SDK OR NOT EXISTS "${DLSS_SDK}/include/nvsdk_ngx.h")
  message(FATAL_ERROR "Set DLSS_SDK to the fetched NVIDIA DLSS SDK folder (tools/build_native.ps1 does this).")
endif()
if(NOT STREAMLINE_SDK OR NOT EXISTS "${STREAMLINE_SDK}/include/sl.h")
  message(FATAL_ERROR "Set STREAMLINE_SDK to the files taken from the pinned Streamline archive (tools/build_native.ps1 does this).")
endif()
if(NOT FFX_SDK OR NOT EXISTS "${FFX_SDK}/Kits/FidelityFX/api/include/ffx_api.h")
  message(FATAL_ERROR "Set FFX_SDK to the fetched AMD FSR SDK folder (tools/build_native.ps1 does this).")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT with the static-CRT NGX loader (nvsdk_ngx_s.lib): players need no Visual C++ runtime installed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

add_library(ForgeFrameGen.Native SHARED forge_framegen.cpp present_bridge.cpp frame_generation.cpp dlss_frame_generation.cpp fsr_frame_generation.cpp
  screen_pass.cpp fsr_upscale.cpp ffx_runtime.cpp)
target_include_directories(ForgeFrameGen.Native PRIVATE "${DLSS_SDK}/include" "${STREAMLINE_SDK}/include" "${FFX_SDK}/Kits/FidelityFX/api/include"
  "${FFX_SDK}/Kits/FidelityFX/upscalers/include" "${FFX_SDK}/Kits/FidelityFX/framegeneration/include")
target_compile_definitions(ForgeFrameGen.Native PRIVATE UNICODE _UNICODE)
target_compile_options(ForgeFrameGen.Native PRIVATE /W4 /permissive- /utf-8 /Brepro)
target_link_options(ForgeFrameGen.Native PRIVATE /Brepro)
target_link_libraries(ForgeFrameGen.Native PRIVATE "${DLSS_SDK}/lib/Windows_x86_64/x64/nvsdk_ngx_s.lib" d3d11 d3d12 dxgi dxguid psapi d3dcompiler wintrust crypt32)

# Offline check of the present bridge (not packaged): ForgeFrameGen.BridgeCheck.exe <folder with ForgeFrameGen.Native.dll> [--fsr]
# [--trace <file>]. Streamline's headers only give it the signatures of the calls it traces.
add_executable(ForgeFrameGen.BridgeCheck bridge_check.cpp)
target_include_directories(ForgeFrameGen.BridgeCheck PRIVATE "${STREAMLINE_SDK}/include")
target_compile_definitions(ForgeFrameGen.BridgeCheck PRIVATE UNICODE _UNICODE)
target_compile_options(ForgeFrameGen.BridgeCheck PRIVATE /W4 /permissive- /utf-8)
target_link_libraries(ForgeFrameGen.BridgeCheck PRIVATE d3d11 dxgi)
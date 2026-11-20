# ForgePerformance's developer measurement helper (see forge_perf_probe.cpp). Not packaged.
#   cmake -S Mods/ForgePerformance/tools/NativeProbe -B artifacts/native/ForgePerformanceProbe -A x64
#   cmake --build artifacts/native/ForgePerformanceProbe --config Release
cmake_minimum_required(VERSION 3.20)
project(ForgePerformanceNativeProbe CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
add_library(ForgePerformance.NativeProbe SHARED forge_perf_probe.cpp)
target_compile_definitions(ForgePerformance.NativeProbe PRIVATE UNICODE _UNICODE)
target_compile_options(ForgePerformance.NativeProbe PRIVATE /W4 /permissive- /utf-8)
target_link_libraries(ForgePerformance.NativeProbe PRIVATE d3d11 dxguid psapi)

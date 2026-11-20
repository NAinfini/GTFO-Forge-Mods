// ForgeFrameGen FSR super resolution (Docs/forge-upscale/PLAN.md §3.2): AMD FSR Upscaling through the AMD FSR SDK's signed DLLs on a
// D3D12 device on the adapter of Unity's D3D11 device. The SDK picks the version the card supports (FSR 4 on the cards AMD lists for
// it, FSR 3.1 everywhere else). The inputs DLSS gets (upscale_params.h) are copied into textures shared with D3D12, FSR runs on the
// D3D12 queue between two waits on a shared fence, and its output is copied back on the D3D11 context: no CPU wait in a frame.
// Render thread only (Unity's render-event callback), except RenderSize, which only reads.
#pragma once
#include "upscale_params.h"
#include <d3d11.h>
#include <string>

namespace fsr
{
// Where the SDK's and this module's messages go (forge_framegen.cpp's message queue).
using Sink = void (*)(const std::string &text);

// Loads the packaged SDK DLLs from folder (only those copies) and makes the D3D12 device, queue and fence on device's adapter. False
// with the reason in why when FSR cannot run here. version: the SDK's version names offered for this card, newest first.
bool Start(ID3D11Device *device, ID3D11DeviceContext *context, const wchar_t *folder, Sink sink, std::string &why, std::string &versions);
// FSR's render size for an output size and an NGX quality value (the mode the player picked); false when FSR is not started.
bool RenderSize(uint32_t outputWidth, uint32_t outputHeight, int quality, uint32_t *renderWidth, uint32_t *renderHeight);
// Upscales one frame. False with the reason in why; the caller stops FSR.
bool Evaluate(const FrameParams &frame, std::string &why);
// The version name of the FSR the current context runs (empty before the first frame).
std::string Running();
// Waits for the queue, then releases everything (the DLLs stay loaded).
void Shutdown();
} // namespace fsr

// One frame's super resolution inputs, filled by the plugin on the main thread and handed to the render thread by a render event
// (layout shared with DlssBridge.FrameParams). Both upscalers take the same inputs: forge_framegen.cpp hands them to DLSS (NGX) or to
// FSR (fsr_upscale.cpp).
#pragma once
#include <cstdint>

#pragma pack(push, 8)
struct FrameParams
{
    uint32_t size;
    uint32_t version;
    void *color;     // render-size colour before post-processing (ID3D11Texture2D*)
    void *depth;     // render-size depth, single channel float
    void *motion;    // render-size motion vectors, RG half, UV units (current - previous)
    void *output;    // output-size colour, UAV-capable
    uint32_t renderWidth, renderHeight, outputWidth, outputHeight;
    float jitterX, jitterY;   // render-pixel jitter applied to the projection this frame
    float motionScaleX, motionScaleY;
    int32_t reset;            // 1 when history must be dropped (first frame, cut, resize)
    int32_t hdr;              // colour is linear HDR
    int32_t depthInverted;    // reversed Z
    int32_t quality;          // NVSDK_NGX_PerfQuality_Value
    int32_t exposure;         // 0: the upscaler measures the exposure itself; 1: fixed at 1 (the colour is already exposed and tone mapped)
    int32_t preset;           // 0: the mod's choice for the mode; otherwise an NVSDK_NGX_DLSS_Hint_Render_Preset for every mode (DLSS only)
    float cameraNear, cameraFar; // the camera's planes (FSR only)
    float cameraFov;          // vertical field of view in radians (FSR only)
};
#pragma pack(pop)

static_assert(sizeof(FrameParams) == 112, "FrameParams layout is shared with the plugin");

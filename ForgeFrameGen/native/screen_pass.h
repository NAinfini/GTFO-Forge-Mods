// ForgeFrameGen full-screen passes on Unity's D3D11 device (render thread only), used by frame generation (frame_generation.cpp: its inputs). One pixel of the target is one pixel of the sources when they are of the
// same size; a target of another size (the game draws its picture smaller than its window) samples them bilinearly.
#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>

namespace pass
{
enum Mode : unsigned
{
    Copy = 0,
    EncodeSrgb = 1, // a linear source, encoded like an sRGB back buffer
    Hud = 2,        // what the game's HUD compositor made of a black frame (source) and of a white one (other): the HUD's colour, already
                    // multiplied by its opacity, and its opacity (Final = HUD.rgb + (1 - HUD.a) x frame, Streamline's UI colour and alpha)
};

// The plugin texture each cached view is for; a view is made again when the plugin hands over another texture for that input.
enum Input : int
{
    Depth,
    Motion,
    Hudless,
    HudOnBlack,
    HudOnWhite,
    InputCount,
};

// Compiles the shader and makes the pass's own context state for device, once per device. Throws std::runtime_error.
void Prepare(ID3D11Device *device);

// The pass's context state is bound for the life of a Scope, so Unity's bound state is exactly as it was afterwards. Prepare first.
class Scope
{
  public:
    explicit Scope(ID3D11DeviceContext *context);
    ~Scope();
    Scope(const Scope &) = delete;
    Scope &operator=(const Scope &) = delete;

    // flip: the target's rows are the sources' rows in reverse order (Unity's render textures to a swap chain's order).
    // other and otherInput are used by Hud only. Throws std::runtime_error when a view cannot be made.
    void Draw(Mode mode, bool flip, Input input, void *source, Input otherInput, void *other, ID3D11RenderTargetView *target, UINT width, UINT height);

  private:
    ID3D11DeviceContext *_context;
    ID3D11DeviceContext1 *_context1 = nullptr;
    ID3DDeviceContextState *_previous = nullptr;
};

// Drops the views of first..last, so textures the plugin has released are freed.
void Forget(Input first, Input last);
} // namespace pass

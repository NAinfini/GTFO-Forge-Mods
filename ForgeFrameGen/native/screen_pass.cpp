// ForgeFrameGen full-screen passes. See screen_pass.h.
#include "screen_pass.h"
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <array>
#include <cstring>
#include <stdexcept>
#include <string>
#include <utility>

using Microsoft::WRL::ComPtr;

namespace pass
{
namespace
{
const char *kShader = R"(
Texture2D<float4> Source : register(t0);
Texture2D<float4> Other : register(t1);
SamplerState Linear : register(s0);
// scaled: the sources are of another size than the target (the game draws its picture smaller than its window and scales it up), so they
// are sampled bilinearly at the target pixel's place instead of read pixel for pixel.
cbuffer Params : register(b0) { uint flip; uint mode; uint height; uint decoded; uint scaled; uint width; uint2 unused; };
float4 Vertex(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float3 Encode(float3 v)
{
    v = saturate(v);
    return v <= 0.0031308 ? v * 12.92 : 1.055 * pow(v, 1.0 / 2.4) - 0.055;
}
float Most(float3 v) { return max(v.r, max(v.g, v.b)); }
float4 Read(Texture2D<float4> source, int3 at, float2 uv) { return scaled ? source.SampleLevel(Linear, uv, 0) : source.Load(at); }
float4 Pixel(float4 position : SV_Position) : SV_Target
{
    int3 at = int3(position.xy, 0);
    float2 uv = position.xy / float2(width, height);
    if (flip) at.y = (int)height - 1 - at.y, uv.y = 1 - uv.y;
    float4 c = Read(Source, at, uv);
    if (mode == 1) c.rgb = Encode(c.rgb);
    if (mode == 2)
    {
        // Both are compared as the back buffer stores them (sRGB encoded); a view that decodes them is encoded back.
        float3 black = c.rgb, white = Read(Other, at, uv).rgb;
        if (decoded) black = Encode(black), white = Encode(white);
        float alpha = 1 - dot(saturate(white - black), 1.0 / 3);
        // No HUD here (the rest is 8-bit rounding): Streamline wants exactly 0. A pixel the HUD only adds light to keeps a non-zero alpha.
        if (alpha < 2.5 / 255 && Most(black) < 2.5 / 255) return 0;
        return float4(black, max(alpha, 1.0 / 255));
    }
    return c;
}
)";

struct State
{
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11SamplerState> linear;
    ComPtr<ID3DDeviceContextState> state;
    struct View
    {
        void *texture = nullptr;
        ComPtr<ID3D11ShaderResourceView> view;
        bool decodes = false; // reading through it decodes sRGB
        UINT width = 0, height = 0;
    };
    std::array<View, InputCount> views;
} g;

DXGI_FORMAT Typed(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_R32_TYPELESS: return DXGI_FORMAT_R32_FLOAT;
    case DXGI_FORMAT_R16G16_TYPELESS: return DXGI_FORMAT_R16G16_FLOAT;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R24G8_TYPELESS: return DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
    case DXGI_FORMAT_R32G8X24_TYPELESS: return DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
    default: return format;
    }
}

// The view of a plugin texture, whether reading through it decodes sRGB, and the texture's size.
const State::View &ViewOf(Input input, void *texture)
{
    auto &cached = g.views[input];
    if (cached.texture == texture) return cached;
    auto *resource = static_cast<ID3D11Texture2D *>(texture);
    D3D11_TEXTURE2D_DESC desc{};
    resource->GetDesc(&desc);
    D3D11_SHADER_RESOURCE_VIEW_DESC view{};
    view.Format = Typed(desc.Format);
    view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    view.Texture2D.MipLevels = 1;
    ComPtr<ID3D11ShaderResourceView> made;
    if (FAILED(g.device->CreateShaderResourceView(resource, &view, &made))) throw std::runtime_error("input view");
    bool decodes = view.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || view.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    cached = {texture, made, decodes, desc.Width, desc.Height};
    return cached;
}
} // namespace

void Prepare(ID3D11Device *device)
{
    if (g.device.Get() == device) return;
    g = State{};
    ComPtr<ID3DBlob> code, errors;
    auto compile = [&](const char *entry, const char *profile) {
        code.Reset();
        errors.Reset();
        HRESULT result = D3DCompile(kShader, std::strlen(kShader), "ForgeFrameGenPass", nullptr, nullptr, entry, profile, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
        if (FAILED(result))
            throw std::runtime_error(std::string("compiling the pass shader failed: ") + (errors ? static_cast<const char *>(errors->GetBufferPointer()) : "no message"));
    };
    State made;
    compile("Vertex", "vs_5_0");
    if (FAILED(device->CreateVertexShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &made.vertex))) throw std::runtime_error("pass vertex shader");
    compile("Pixel", "ps_5_0");
    if (FAILED(device->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &made.pixel))) throw std::runtime_error("pass pixel shader");
    D3D11_BUFFER_DESC buffer{};
    buffer.ByteWidth = 32;
    buffer.Usage = D3D11_USAGE_DEFAULT;
    buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    if (FAILED(device->CreateBuffer(&buffer, nullptr, &made.constants))) throw std::runtime_error("pass constants");
    D3D11_SAMPLER_DESC sampler{};
    sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(device->CreateSamplerState(&sampler, &made.linear))) throw std::runtime_error("pass sampler");
    ComPtr<ID3D11Device1> device1;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&device1)))) throw std::runtime_error("D3D11.1 device interface");
    D3D_FEATURE_LEVEL level = device->GetFeatureLevel(), chosen{};
    if (FAILED(device1->CreateDeviceContextState(0, &level, 1, D3D11_SDK_VERSION, __uuidof(ID3D11Device), &chosen, &made.state)))
        throw std::runtime_error("pass context state");
    made.device = device;
    g = std::move(made);
}

Scope::Scope(ID3D11DeviceContext *context) : _context(context)
{
    if (FAILED(context->QueryInterface(IID_PPV_ARGS(&_context1)))) throw std::runtime_error("D3D11.1 context interface");
    _context1->SwapDeviceContextState(g.state.Get(), &_previous);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->VSSetShader(g.vertex.Get(), nullptr, 0);
    context->PSSetShader(g.pixel.Get(), nullptr, 0);
    ID3D11Buffer *buffers[] = {g.constants.Get()};
    context->PSSetConstantBuffers(0, 1, buffers);
    ID3D11SamplerState *samplers[] = {g.linear.Get()};
    context->PSSetSamplers(0, 1, samplers);
}

Scope::~Scope()
{
    ID3D11ShaderResourceView *none[] = {nullptr, nullptr};
    _context->PSSetShaderResources(0, 2, none);
    _context->OMSetRenderTargets(0, nullptr, nullptr);
    _context1->SwapDeviceContextState(_previous, nullptr);
    if (_previous) _previous->Release();
    _context1->Release();
}

void Scope::Draw(Mode mode, bool flip, Input input, void *source, Input otherInput, void *other, ID3D11RenderTargetView *target, UINT width, UINT height)
{
    const auto &first = ViewOf(input, source);
    ID3D11ShaderResourceView *views[] = {first.view.Get(), other ? ViewOf(otherInput, other).view.Get() : nullptr};
    const bool scaled = first.width != width || first.height != height;
    UINT values[8] = {flip ? 1u : 0u, (UINT)mode, height, first.decodes ? 1u : 0u, scaled ? 1u : 0u, width, 0, 0};
    _context->UpdateSubresource(g.constants.Get(), 0, nullptr, values, 0, 0);
    D3D11_VIEWPORT viewport{0, 0, (float)width, (float)height, 0, 1};
    _context->RSSetViewports(1, &viewport);
    _context->OMSetRenderTargets(1, &target, nullptr);
    _context->PSSetShaderResources(0, 2, views);
    _context->Draw(3, 0);
}

void Forget(Input first, Input last)
{
    for (int input = first; input <= last; input++) g.views[input] = {};
}
} // namespace pass

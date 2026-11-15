#include "linear_pass.h"

#ifdef _WIN32
#include <d3dcompiler.h>
#include <wrl/client.h>

#include <cstring>

namespace vlqt::linear_pass {

namespace {

using Microsoft::WRL::ComPtr;

// One triangle that covers the target; no vertex buffer.
const char s_vertexShader[] = R"(
float4 main(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
}
)";

// Premultiplied gamma-space colour -> premultiplied linear colour. Alpha 0 with a colour (additive content) has no
// coverage to divide by: its colour is decoded as it is, so it reads the same as before through an SRGB view.
const char s_pixelShader[] = R"(
Texture2D<float4> source : register(t0);

float3 decode(float3 c)
{
    c = max(c, 0);
    return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}

float4 main(float4 position : SV_Position) : SV_Target
{
    float4 p = source.Load(int3(position.xy, 0));
    float coverage = p.a > 0 ? p.a : 1;
    return float4(decode(p.rgb / coverage) * coverage, p.a);
}
)";

struct Bytecode
{
    ComPtr<ID3DBlob> vertex;
    ComPtr<ID3DBlob> pixel;
    HRESULT hr = S_OK;
};

// Compiled once per process (shader model 4: feature level 10 and up, WARP included).
const Bytecode& bytecode()
{
    static const Bytecode compiled = [] {
        Bytecode b;
        b.hr = D3DCompile(s_vertexShader, std::strlen(s_vertexShader), "linear_pass_vs", nullptr, nullptr, "main", "vs_4_0",
            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, b.vertex.GetAddressOf(), nullptr);
        if (SUCCEEDED(b.hr))
            b.hr = D3DCompile(s_pixelShader, std::strlen(s_pixelShader), "linear_pass_ps", nullptr, nullptr, "main", "ps_4_0",
                D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, b.pixel.GetAddressOf(), nullptr);
        return b;
    }();
    return compiled;
}

template <class T> void releaseCom(T*& p)
{
    if (p) {
        p->Release();
        p = nullptr;
    }
}

} // namespace

bool isLinear(DXGI_FORMAT format)
{
    return format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB
        || format == DXGI_FORMAT_R16G16B16A16_FLOAT;
}

HRESULT create(ID3D11Device* device, ID3D11Texture2D* target, DXGI_FORMAT targetFormat, unsigned width, unsigned height,
               Pass& pass, const char*& what)
{
    release(pass);
    const Bytecode& code = bytecode();
    HRESULT hr = code.hr;
    if (FAILED(hr)) {
        what = "D3DCompile of the linear pass";
        return hr;
    }
    ComPtr<ID3D11Texture2D> source;
    ComPtr<ID3D11ShaderResourceView> sourceView;
    ComPtr<ID3D11RenderTargetView> targetView;
    ComPtr<ID3D11VertexShader> vertexShader;
    ComPtr<ID3D11PixelShader> pixelShader;
    ComPtr<ID3D11RasterizerState> rasterizer;

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(hr = device->CreateTexture2D(&desc, nullptr, source.GetAddressOf()))) {
        what = "CreateTexture2D of the linear pass source";
        return hr;
    }
    if (FAILED(hr = device->CreateShaderResourceView(source.Get(), nullptr, sourceView.GetAddressOf()))) {
        what = "CreateShaderResourceView of the linear pass source";
        return hr;
    }
    D3D11_RENDER_TARGET_VIEW_DESC rtv{};
    rtv.Format = targetFormat;
    rtv.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    if (FAILED(hr = device->CreateRenderTargetView(target, &rtv, targetView.GetAddressOf()))) {
        what = "CreateRenderTargetView of the linear pass target";
        return hr;
    }
    if (FAILED(hr = device->CreateVertexShader(code.vertex->GetBufferPointer(), code.vertex->GetBufferSize(), nullptr,
            vertexShader.GetAddressOf()))
        || FAILED(hr = device->CreatePixelShader(code.pixel->GetBufferPointer(), code.pixel->GetBufferSize(), nullptr,
            pixelShader.GetAddressOf()))) {
        what = "CreateVertexShader/CreatePixelShader of the linear pass";
        return hr;
    }
    D3D11_RASTERIZER_DESC rs{};
    rs.FillMode = D3D11_FILL_SOLID;
    rs.CullMode = D3D11_CULL_NONE;
    rs.DepthClipEnable = TRUE;
    if (FAILED(hr = device->CreateRasterizerState(&rs, rasterizer.GetAddressOf()))) {
        what = "CreateRasterizerState of the linear pass";
        return hr;
    }

    pass.source = source.Detach();
    pass.sourceView = sourceView.Detach();
    pass.targetView = targetView.Detach();
    pass.vertexShader = vertexShader.Detach();
    pass.pixelShader = pixelShader.Detach();
    pass.rasterizer = rasterizer.Detach();
    pass.viewport = { 0.0f, 0.0f, float(width), float(height), 0.0f, 1.0f };
    return S_OK;
}

// Sets every state the draw depends on: the context is shared with the QRhis of QtNative, which set their own state
// for each pass and draw.
void run(ID3D11DeviceContext* context, const Pass& pass)
{
    context->OMSetRenderTargets(1, &pass.targetView, nullptr);
    context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
    context->OMSetDepthStencilState(nullptr, 0);
    context->RSSetState(pass.rasterizer);
    context->RSSetViewports(1, &pass.viewport);
    context->IASetInputLayout(nullptr);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->VSSetShader(pass.vertexShader, nullptr, 0);
    context->HSSetShader(nullptr, nullptr, 0);
    context->DSSetShader(nullptr, nullptr, 0);
    context->GSSetShader(nullptr, nullptr, 0);
    context->PSSetShader(pass.pixelShader, nullptr, 0);
    context->PSSetShaderResources(0, 1, &pass.sourceView);
    context->Draw(3, 0);
    // Qt renders into source next frame: it must not stay bound as a shader resource.
    ID3D11ShaderResourceView* noView = nullptr;
    context->PSSetShaderResources(0, 1, &noView);
    context->OMSetRenderTargets(0, nullptr, nullptr);
}

void release(Pass& pass)
{
    releaseCom(pass.rasterizer);
    releaseCom(pass.pixelShader);
    releaseCom(pass.vertexShader);
    releaseCom(pass.targetView);
    releaseCom(pass.sourceView);
    releaseCom(pass.source);
}

} // namespace vlqt::linear_pass
#endif

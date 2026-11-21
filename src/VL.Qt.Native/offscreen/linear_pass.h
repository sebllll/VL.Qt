#pragma once

#ifdef _WIN32
#include <d3d11.h>

namespace vlqt::linear_pass {

// A QtTexture whose consumer samples linear values (an SRGB view or R16G16B16A16_FLOAT; render-texture.md "Format,
// colour space, alpha"). Qt renders premultiplied gamma-space values; sRGB decoding does not commute with
// premultiplication, so the texture cannot hold Qt's values as they are. Qt renders into source instead, a texture of
// QtNative, and run writes each pixel into the consumer's texture as a * linear(c): unpremultiply, sRGB decode,
// premultiply again. Through an SRGB view the GPU encodes on write, so the consumer's SRGB view reads a * linear(c).
// Opaque pixels come out as through the UNORM view before.
struct Pass
{
    ID3D11Texture2D* source = nullptr; // Qt's render target, R16G16B16A16_FLOAT: no 8-bit rounding before unpremultiply
    ID3D11ShaderResourceView* sourceView = nullptr;
    ID3D11RenderTargetView* targetView = nullptr; // the consumer's texture, in the format the consumer samples
    ID3D11VertexShader* vertexShader = nullptr;
    ID3D11PixelShader* pixelShader = nullptr;
    ID3D11RasterizerState* rasterizer = nullptr;
    D3D11_VIEWPORT viewport{};
};

// Returns true if the consumer samples format as linear values: an 8-bit SRGB format or R16G16B16A16_FLOAT.
bool isLinear(DXGI_FORMAT format);

// Creates pass for target (width x height, written through targetFormat) on device. S_OK, or the failing HRESULT with
// the failing step in what; pass is then empty.
HRESULT create(ID3D11Device* device, ID3D11Texture2D* target, DXGI_FORMAT targetFormat, unsigned width, unsigned height,
               Pass& pass, const char*& what);

// Converts source into the target on context, after Qt's endFrame on the same context. Leaves no view of the pass bound.
void run(ID3D11DeviceContext* context, const Pass& pass);

void release(Pass& pass);

} // namespace vlqt::linear_pass
#endif

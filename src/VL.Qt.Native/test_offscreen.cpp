// TEST ONLY exports of #116 (QtTexture): a second D3D11 device standing in for Stride's, shared textures, readback and
// leak probes for the SmokeTest (vlqt.h, "TEST ONLY (#116 ...)").
#include "offscreen.h"
#include "protocol.h"
#include "vlqt.h"

#ifdef _WIN32

#include "device.h"

#include <rhi/qrhi.h>

#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <windows.h>

#include <cstdio>
#include <cstring>

namespace {

// Releases a COM pointer on scope exit.
template <class T> struct ComPtr
{
    T* p = nullptr;
    ComPtr() = default;
    ComPtr(const ComPtr&) = delete;
    ComPtr& operator=(const ComPtr&) = delete;
    ~ComPtr()
    {
        if (p)
            p->Release();
    }
    T** put() { return &p; }
    T* operator->() const { return p; }
    explicit operator bool() const { return p != nullptr; }
};

// Sets vlqt_last_error and returns -1.
int fail(const char* what, HRESULT hr)
{
    char text[160];
    std::snprintf(text, sizeof text, "vlqt_test: %s failed (HRESULT 0x%08lX)", what, static_cast<unsigned long>(hr));
    vlqt::offscreen::setError(text);
    return -1;
}

// Bytes per pixel of the 4 and 8 byte formats of the readback, 0 = unsupported.
int bytesPerPixel(DXGI_FORMAT f)
{
    switch (f) {
    case DXGI_FORMAT_R32G32_TYPELESS:
    case DXGI_FORMAT_R32G32_FLOAT:
    case DXGI_FORMAT_R32G32_UINT:
    case DXGI_FORMAT_R32G32_SINT:
    case DXGI_FORMAT_R16G16B16A16_TYPELESS:
    case DXGI_FORMAT_R16G16B16A16_FLOAT:
    case DXGI_FORMAT_R16G16B16A16_UNORM:
    case DXGI_FORMAT_R16G16B16A16_UINT:
    case DXGI_FORMAT_R16G16B16A16_SNORM:
    case DXGI_FORMAT_R16G16B16A16_SINT:
        return 8;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS:
    case DXGI_FORMAT_R10G10B10A2_UNORM:
    case DXGI_FORMAT_R10G10B10A2_UINT:
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_R8G8B8A8_UINT:
    case DXGI_FORMAT_R8G8B8A8_SNORM:
    case DXGI_FORMAT_R8G8B8A8_SINT:
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8X8_TYPELESS:
    case DXGI_FORMAT_B8G8R8X8_UNORM:
    case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB:
    case DXGI_FORMAT_R32_TYPELESS:
    case DXGI_FORMAT_R32_FLOAT:
    case DXGI_FORMAT_R32_UINT:
    case DXGI_FORMAT_R32_SINT:
        return 4;
    default:
        return 0;
    }
}

// The adapter of QtNative's shared device, or null (no device yet, or none shared): then the default adapter is used.
// Never creates the device: the caller's device must not decide how QtNative's is created.
// The returned adapter carries one reference.
IDXGIAdapter* qtNativeAdapter()
{
    if (vlqt::device::generation() == 0)
        return nullptr;
    QRhi* rhi = vlqt::device::uploadRhi();
    auto* qtDevice = static_cast<IUnknown*>(vlqt::device::nativeDevice(rhi));
    if (!qtDevice)
        return nullptr;
    ComPtr<IDXGIDevice> dxgiDevice;
    if (FAILED(qtDevice->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(dxgiDevice.put()))))
        return nullptr;
    IDXGIAdapter* adapter = nullptr;
    dxgiDevice->GetAdapter(&adapter);
    return adapter;
}

} // namespace

extern "C" {

VLQT_API int vlqt_test_d3d11_create_device(int adapter, void** device, void** context)
{
    if (device)
        *device = nullptr;
    if (context)
        *context = nullptr;
    if (!device || !context || (adapter != 0 && adapter != 1))
        return VLQT_OFFSCREEN_BAD_ARGUMENT;

    static const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    HRESULT hr;
    if (adapter == 1) {
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels,
                               _countof(levels), D3D11_SDK_VERSION, &dev, nullptr, &ctx);
    } else {
        ComPtr<IDXGIAdapter> qtAdapter;
        qtAdapter.p = qtNativeAdapter();
        hr = D3D11CreateDevice(qtAdapter.p, qtAdapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr,
                               D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, _countof(levels), D3D11_SDK_VERSION, &dev,
                               nullptr, &ctx);
    }
    if (FAILED(hr))
        return fail("D3D11CreateDevice", hr);
    *device = dev;
    *context = ctx;
    return 0;
}

VLQT_API int vlqt_test_d3d11_release_device(void* device, void* context)
{
    if (!device)
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    if (context)
        static_cast<ID3D11DeviceContext*>(context)->Release();
    return static_cast<int>(static_cast<ID3D11Device*>(device)->Release());
}

VLQT_API int vlqt_test_d3d11_create_shared_texture(void* device, int width, int height, int dxgiFormat, void** texture,
                                                   void** ntHandle)
{
    if (texture)
        *texture = nullptr;
    if (ntHandle)
        *ntHandle = nullptr;
    if (!device || !texture || !ntHandle || width < 1 || height < 1)
        return VLQT_OFFSCREEN_BAD_ARGUMENT;

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = static_cast<UINT>(width);
    desc.Height = static_cast<UINT>(height);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = static_cast<DXGI_FORMAT>(dxgiFormat);
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

    ComPtr<ID3D11Texture2D> tex;
    HRESULT hr = static_cast<ID3D11Device*>(device)->CreateTexture2D(&desc, nullptr, tex.put());
    if (FAILED(hr))
        return fail("CreateTexture2D", hr);
    ComPtr<IDXGIResource1> resource;
    hr = tex->QueryInterface(__uuidof(IDXGIResource1), reinterpret_cast<void**>(resource.put()));
    if (FAILED(hr))
        return fail("QueryInterface(IDXGIResource1)", hr);
    HANDLE handle = nullptr;
    hr = resource->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &handle);
    if (FAILED(hr))
        return fail("CreateSharedHandle", hr);
    *ntHandle = handle;
    *texture = tex.p;
    tex.p = nullptr; // the caller owns the reference
    return 0;
}

VLQT_API int vlqt_test_d3d11_read_texture(void* texture, unsigned char* out, int cap, int* width, int* height,
                                          int* dxgiFormat)
{
    if (!texture || !width || !height || !dxgiFormat || cap < 0 || (cap > 0 && !out))
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    auto* tex = static_cast<ID3D11Texture2D*>(texture);
    D3D11_TEXTURE2D_DESC desc;
    tex->GetDesc(&desc);
    const int bpp = bytesPerPixel(desc.Format);
    if (bpp == 0)
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    *width = static_cast<int>(desc.Width);
    *height = static_cast<int>(desc.Height);
    *dxgiFormat = static_cast<int>(desc.Format);
    const long long required = static_cast<long long>(desc.Width) * desc.Height * bpp;
    if (required > cap)
        return -static_cast<int>(required);

    ComPtr<ID3D11Device> device;
    tex->GetDevice(device.put());
    ComPtr<ID3D11DeviceContext> context;
    device->GetImmediateContext(context.put());

    D3D11_TEXTURE2D_DESC stagingDesc = desc;
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.BindFlags = 0;
    stagingDesc.MiscFlags = 0;
    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;
    HRESULT hr = device->CreateTexture2D(&stagingDesc, nullptr, staging.put());
    if (FAILED(hr))
        return fail("CreateTexture2D(staging)", hr);
    // Copy, Map and Unmap as one sequence on a context the render threads may share (device.h).
    vlqt::device::ContextLock lock(context.p);
    context->CopyResource(staging.p, tex);
    D3D11_MAPPED_SUBRESOURCE mapped;
    // Blocks until the GPU has finished the copy (and everything queued before it).
    hr = context->Map(staging.p, 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr))
        return fail("Map(staging)", hr);
    const size_t rowBytes = static_cast<size_t>(desc.Width) * bpp;
    for (UINT y = 0; y < desc.Height; y++)
        std::memcpy(out + y * rowBytes, static_cast<const unsigned char*>(mapped.pData) + y * mapped.RowPitch, rowBytes);
    context->Unmap(staging.p, 0);
    return static_cast<int>(required);
}

VLQT_API int vlqt_test_com_ref_count(void* obj)
{
    if (!obj)
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    auto* unknown = static_cast<IUnknown*>(obj);
    unknown->AddRef();
    return static_cast<int>(unknown->Release());
}

VLQT_API int vlqt_test_com_release(void* obj)
{
    if (!obj)
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    return static_cast<int>(static_cast<IUnknown*>(obj)->Release());
}

VLQT_API int vlqt_test_close_handle(void* handle)
{
    if (!handle)
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    return CloseHandle(handle) ? 0 : -1;
}

VLQT_API int vlqt_test_process_handle_count(void)
{
    DWORD count = 0;
    return GetProcessHandleCount(GetCurrentProcess(), &count) ? static_cast<int>(count) : -1;
}

#else // !_WIN32

extern "C" {

VLQT_API int vlqt_test_d3d11_create_device(int, void** device, void** context)
{
    if (device)
        *device = nullptr;
    if (context)
        *context = nullptr;
    return VLQT_OFFSCREEN_NOT_SUPPORTED;
}

VLQT_API int vlqt_test_d3d11_release_device(void*, void*) { return VLQT_OFFSCREEN_NOT_SUPPORTED; }

VLQT_API int vlqt_test_d3d11_create_shared_texture(void*, int, int, int, void** texture, void** ntHandle)
{
    if (texture)
        *texture = nullptr;
    if (ntHandle)
        *ntHandle = nullptr;
    return VLQT_OFFSCREEN_NOT_SUPPORTED;
}

VLQT_API int vlqt_test_d3d11_read_texture(void*, unsigned char*, int, int*, int*, int*)
{
    return VLQT_OFFSCREEN_NOT_SUPPORTED;
}

VLQT_API int vlqt_test_com_ref_count(void*) { return VLQT_OFFSCREEN_NOT_SUPPORTED; }

VLQT_API int vlqt_test_com_release(void*) { return VLQT_OFFSCREEN_NOT_SUPPORTED; }

VLQT_API int vlqt_test_close_handle(void*) { return VLQT_OFFSCREEN_NOT_SUPPORTED; }

VLQT_API int vlqt_test_process_handle_count(void) { return VLQT_OFFSCREEN_NOT_SUPPORTED; }

#endif // _WIN32

VLQT_API int vlqt_test_offscreen_state(void* scene, unsigned long long* readFence, unsigned long long* writeFence,
                                       long long* renders)
{
    return vlqt::offscreen::testState(scene, readFence, writeFence, renders);
}

} // extern "C"

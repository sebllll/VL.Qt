// vlqt_testkit (testkit.h): the stand-in "Stride" D3D11 device of the QtTexture tests, shared textures, readback and
// leak probes. Windows only, no Qt, no QtNative.
#include "testkit.h"

#include "../vlqt.h"

#include <d3d11_4.h> // ID3D11Multithread
#include <dxgi1_4.h>
#include <windows.h>

#include <climits>
#include <cstdio>
#include <cstring>
#include <string>

namespace {

// Per thread, as vlqt_last_error (vlqt.h).
thread_local std::string t_lastError;

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

// Holds the immediate context of a device for a stateful sequence (ID3D11Multithread::Enter/Leave): the critical
// section QtNative's ContextLock (device.h) takes on a context its render threads share.
class ContextLock
{
public:
    explicit ContextLock(ID3D11DeviceContext* context)
    {
        if (context)
            context->QueryInterface(__uuidof(ID3D11Multithread), reinterpret_cast<void**>(m_multithread.put()));
        if (m_multithread)
            m_multithread->Enter();
    }
    ~ContextLock()
    {
        if (m_multithread)
            m_multithread->Leave();
    }
    ContextLock(const ContextLock&) = delete;
    ContextLock& operator=(const ContextLock&) = delete;

private:
    ComPtr<ID3D11Multithread> m_multithread;
};

// Sets vlqt_test_last_error and returns code.
int fail(int code, const std::string& message)
{
    t_lastError = "vlqt_test: " + message;
    return code;
}

// A failed D3D call: VLQT_ERR_INTERNAL with the HRESULT.
int failHr(const char* what, HRESULT hr)
{
    char text[160];
    std::snprintf(text, sizeof text, "%s failed (HRESULT 0x%08lX)", what, static_cast<unsigned long>(hr));
    return fail(VLQT_ERR_INTERNAL, text);
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

// The adapter with this LUID (LowPart | HighPart << 32), null if there is none. Carries one reference.
IDXGIAdapter* adapterByLuid(unsigned long long luid)
{
    ComPtr<IDXGIFactory4> factory;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory4), reinterpret_cast<void**>(factory.put()))))
        return nullptr;
    LUID l{};
    l.LowPart = DWORD(luid & 0xffffffffu);
    l.HighPart = LONG(luid >> 32);
    IDXGIAdapter* adapter = nullptr;
    factory->EnumAdapterByLuid(l, __uuidof(IDXGIAdapter), reinterpret_cast<void**>(&adapter));
    return adapter;
}

} // namespace

extern "C" {

VLQT_TESTKIT_API const char* vlqt_test_last_error(void)
{
    return t_lastError.c_str();
}

VLQT_TESTKIT_API int vlqt_test_d3d11_create_device(int adapter, unsigned long long adapterLuid, void** device, void** context)
{
    if (device)
        *device = nullptr;
    if (context)
        *context = nullptr;
    if (!device || !context || (adapter != 0 && adapter != 1))
        return fail(VLQT_ERR_BAD_ARGUMENT, "create_device: null out or unknown adapter");

    static const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    HRESULT hr;
    if (adapter == 1) {
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels,
                               _countof(levels), D3D11_SDK_VERSION, &dev, nullptr, &ctx);
    } else {
        ComPtr<IDXGIAdapter> byLuid;
        if (adapterLuid) {
            byLuid.p = adapterByLuid(adapterLuid);
            if (!byLuid)
                return fail(VLQT_ERR_BAD_ARGUMENT, "create_device: no adapter with LUID " + std::to_string(adapterLuid));
        }
        hr = D3D11CreateDevice(byLuid.p, byLuid ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr,
                               D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, _countof(levels), D3D11_SDK_VERSION, &dev,
                               nullptr, &ctx);
    }
    if (FAILED(hr))
        return failHr("D3D11CreateDevice", hr);
    *device = dev;
    *context = ctx;
    return 0;
}

VLQT_TESTKIT_API int vlqt_test_d3d11_release_device(void* device, void* context)
{
    if (!device)
        return fail(VLQT_ERR_BAD_ARGUMENT, "release_device: null device");
    if (context)
        static_cast<ID3D11DeviceContext*>(context)->Release();
    return static_cast<int>(static_cast<ID3D11Device*>(device)->Release());
}

VLQT_TESTKIT_API int vlqt_test_d3d11_create_shared_texture(void* device, int width, int height, int dxgiFormat,
                                                          void** texture, void** ntHandle)
{
    if (texture)
        *texture = nullptr;
    if (ntHandle)
        *ntHandle = nullptr;
    if (!device || !texture || !ntHandle || width < 1 || height < 1)
        return fail(VLQT_ERR_BAD_ARGUMENT, "create_shared_texture: null argument or size < 1");

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
        return failHr("CreateTexture2D", hr);
    ComPtr<IDXGIResource1> resource;
    hr = tex->QueryInterface(__uuidof(IDXGIResource1), reinterpret_cast<void**>(resource.put()));
    if (FAILED(hr))
        return failHr("QueryInterface(IDXGIResource1)", hr);
    HANDLE handle = nullptr;
    hr = resource->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &handle);
    if (FAILED(hr))
        return failHr("CreateSharedHandle", hr);
    *ntHandle = handle;
    *texture = tex.p;
    tex.p = nullptr; // the caller owns the reference
    return 0;
}

VLQT_TESTKIT_API int vlqt_test_d3d11_read_texture(void* texture, unsigned char* out, int cap, int* width, int* height,
                                                 int* dxgiFormat, int* written, int* required)
{
    if (written)
        *written = 0;
    if (required)
        *required = 0;
    if (!texture || !width || !height || !dxgiFormat || cap < 0 || (cap > 0 && !out))
        return fail(VLQT_ERR_BAD_ARGUMENT, "read_texture: null argument, negative cap or null buffer");
    auto* tex = static_cast<ID3D11Texture2D*>(texture);
    D3D11_TEXTURE2D_DESC desc;
    tex->GetDesc(&desc);
    const int bpp = bytesPerPixel(desc.Format);
    if (bpp == 0)
        return fail(VLQT_ERR_UNSUPPORTED, "read_texture: format " + std::to_string(int(desc.Format)) + " not supported");
    *width = static_cast<int>(desc.Width);
    *height = static_cast<int>(desc.Height);
    *dxgiFormat = static_cast<int>(desc.Format);
    const long long size = static_cast<long long>(desc.Width) * desc.Height * bpp;
    // The byte counts are ints: a larger texture (e.g. 16384 x 16384 RGBA16F) cannot be read.
    if (size > INT_MAX)
        return fail(VLQT_ERR_UNSUPPORTED, "read_texture: more than 2^31 - 1 bytes");
    if (required)
        *required = static_cast<int>(size);
    if (size > cap)
        return fail(VLQT_ERR_BUFFER_TOO_SMALL, "read_texture: buffer too small: *required holds the size");

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
        return failHr("CreateTexture2D(staging)", hr);
    // Copy, Map and Unmap as one sequence on a context QtNative's render threads may share (device.h).
    ContextLock lock(context.p);
    context->CopyResource(staging.p, tex);
    D3D11_MAPPED_SUBRESOURCE mapped;
    // Blocks until the GPU has finished the copy (and everything queued before it).
    hr = context->Map(staging.p, 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr))
        return failHr("Map(staging)", hr);
    const size_t rowBytes = static_cast<size_t>(desc.Width) * bpp;
    for (UINT y = 0; y < desc.Height; y++)
        std::memcpy(out + y * rowBytes, static_cast<const unsigned char*>(mapped.pData) + y * mapped.RowPitch, rowBytes);
    context->Unmap(staging.p, 0);
    if (written)
        *written = static_cast<int>(size);
    return VLQT_OK;
}

VLQT_TESTKIT_API int vlqt_test_com_ref_count(void* obj)
{
    if (!obj)
        return fail(VLQT_ERR_BAD_ARGUMENT, "com_ref_count: null object");
    auto* unknown = static_cast<IUnknown*>(obj);
    unknown->AddRef();
    return static_cast<int>(unknown->Release());
}

VLQT_TESTKIT_API int vlqt_test_com_release(void* obj)
{
    if (!obj)
        return fail(VLQT_ERR_BAD_ARGUMENT, "com_release: null object");
    return static_cast<int>(static_cast<IUnknown*>(obj)->Release());
}

VLQT_TESTKIT_API int vlqt_test_close_handle(void* handle)
{
    if (!handle)
        return fail(VLQT_ERR_BAD_ARGUMENT, "close_handle: null handle");
    return CloseHandle(handle) ? VLQT_OK : failHr("CloseHandle", HRESULT_FROM_WIN32(GetLastError()));
}

VLQT_TESTKIT_API int vlqt_test_process_handle_count(void)
{
    DWORD count = 0;
    return GetProcessHandleCount(GetCurrentProcess(), &count) ? static_cast<int>(count)
                                                              : failHr("GetProcessHandleCount", HRESULT_FROM_WIN32(GetLastError()));
}

} // extern "C"

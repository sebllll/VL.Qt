// The D3D11 binding of an offscreen scene: the texture of the foreign device opened on QtNative's device, two shared
// fences, the adapter match and the linear pass (docs/design/stride-output.md). Windows only (CMakeLists.txt).
// Private Qt API: QQuickWindowPrivate::ensureCustomRenderTarget; check on Qt updates (list: elements/elements.h).
#include "offscreen/offscreen_scene.h"
#include "app/exports.h"
#include "device/context_lock.h"
#include "device/device.h"
#include "vlqt.h"

#include <QtQuick/QQuickRenderControl>
#include <QtQuick/QQuickRenderTarget>
#include <QtQuick/QQuickWindow>
#include <QtQuick/private/qquickwindow_p.h>

#include <algorithm>
#include <cstdio>
#include <utility>

#include <d3d11_4.h>
#include <windows.h>
#include <wrl/client.h>

namespace vlqt::offscreen {

namespace {

using Microsoft::WRL::ComPtr;

// The adapters of the foreign device and of QtNative's device differ: move QtNative's device to the foreign adapter
// if nothing renders on it yet (no on-screen window with a QRhi on it, no other scene bound), else ADAPTER_MISMATCH.
int matchAdapter(OffscreenScene* s, unsigned long long foreignLuid)
{
    const bool otherBound = std::any_of(scenes().begin(), scenes().end(), [s](const OffscreenScene* o) { return o != s && o->bound; });
    if (!otherBound && vlqt::device::canMove()) {
        if (vlqt::device::moveTo(foreignLuid))
            return VLQT_OK;
        return fail(VLQT_ERR_DEVICE_LOST, "bind: moving QtNative's device to the foreign adapter failed (see the log)");
    }
    static bool warned = false;
    if (!warned) {
        warned = true;
        qCWarning(lcOffscreen, "offscreen bind: the foreign device is on adapter %016llx, QtNative's device on %016llx, "
            "which a Qt window or another offscreen scene already uses. Use one adapter for both (this warning appears once)",
            foreignLuid, vlqt::device::adapterLuid());
    }
    return fail(VLQT_ERR_ADAPTER_MISMATCH, "bind: the foreign device is on another adapter than QtNative's device, "
        "which a Qt window or another offscreen scene already uses");
}

// The format the consumer samples (vlqt.h): 8-bit formats may view a typeless texture of their family.
bool formatFits(int format, DXGI_FORMAT texture)
{
    switch (format) {
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        return texture == DXGI_FORMAT(format) || texture == DXGI_FORMAT_R8G8B8A8_TYPELESS;
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return texture == DXGI_FORMAT(format) || texture == DXGI_FORMAT_B8G8R8A8_TYPELESS;
    case DXGI_FORMAT_R16G16B16A16_FLOAT:
        return texture == DXGI_FORMAT_R16G16B16A16_FLOAT;
    default:
        return false;
    }
}

// A fence on QtNative's device (qt), shared and opened on the foreign device (foreign). The NT handle is closed once
// opened: both fence objects keep the shared fence alive.
int createFence(ID3D11Device5* qt, ID3D11Device5* foreign, ComPtr<ID3D11Fence>& qtFence, ComPtr<ID3D11Fence>& foreignFence)
{
    HRESULT hr = qt->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(qtFence.GetAddressOf()));
    if (FAILED(hr))
        return fail(VLQT_ERR_FENCE_FAILED, "bind: CreateFence failed", hr);
    HANDLE handle = nullptr;
    hr = qtFence->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &handle);
    if (FAILED(hr))
        return fail(VLQT_ERR_FENCE_FAILED, "bind: ID3D11Fence::CreateSharedHandle failed", hr);
    hr = foreign->OpenSharedFence(handle, IID_PPV_ARGS(foreignFence.GetAddressOf()));
    CloseHandle(handle);
    if (FAILED(hr))
        return fail(VLQT_ERR_FENCE_FAILED, "bind: OpenSharedFence on the foreign device failed", hr);
    return VLQT_OK;
}

// True if device runs on a software adapter: the software flag, or the Microsoft Basic Render Driver (WARP, vendor
// 0x1414, device 0x8c) by its ids. On a GPU-less machine (CI runner) DXGI enumerates the Basic Render Driver as adapter 0,
// next to the adapter of D3D_DRIVER_TYPE_WARP; a pair on it must take the software path too, else the foreign context's
// wait before Qt's signal deadlocks (a software device's context cannot wait for a value that is signalled later).
// Unknown (no adapter): software, the safe path.
bool isSoftware(ID3D11Device* device)
{
    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    ComPtr<IDXGIAdapter1> adapter1;
    DXGI_ADAPTER_DESC1 desc{};
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(dxgi.GetAddressOf()))) || FAILED(dxgi->GetAdapter(adapter.GetAddressOf()))
        || FAILED(adapter.As(&adapter1)) || FAILED(adapter1->GetDesc1(&desc)))
        return true;
    constexpr UINT microsoftVendor = 0x1414, basicRenderDriver = 0x8c;
    return (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || (desc.VendorId == microsoftVendor && desc.DeviceId == basicRenderDriver);
}

} // namespace

int fail(int code, const char* what, HRESULT hr)
{
    char text[256];
    if (hr == S_OK)
        std::snprintf(text, sizeof text, "offscreen: %s", what);
    else
        std::snprintf(text, sizeof text, "offscreen: %s (HRESULT 0x%08lX)", what, static_cast<unsigned long>(hr));
    return vlqt::fail(code, text);
}

ID3D11Device* qtDevice(const OffscreenScene* s)
{
    return static_cast<ID3D11Device*>(vlqt::device::nativeDevice(s->window->rhi()));
}

// Frees the binding: render target -> opened texture -> fences. While the QRhi still exists (before the QQuickWindow
// is deleted). Through the scene's executor (executor.call).
void unbind(OffscreenScene* s)
{
    if (s->texture) {
        vlqt::device::ContextLock lock(s->qtContext.Get());
        s->window->setRenderTarget(QQuickRenderTarget());
        // Qt turns a render target into QRhi wrappers (texture, render target view) only at the next frame
        // (ensureCustomRenderTarget); drop them now, else their view keeps the opened texture alive.
        QQuickWindowPrivate::get(s->window)->ensureCustomRenderTarget();
        // QRhi D3D11 leaves the last render target view bound to the immediate context, which also holds it. Every
        // render pass of QRhi binds its own targets, so clearing them here changes nothing for the windows.
        s->qtContext->OMSetRenderTargets(0, nullptr, nullptr);
    }
    s->linearPass = {};
    s->texture.Reset();
    s->foreignDevice = nullptr;
    s->foreignRead.Reset();
    s->foreignWrite.Reset();
    s->qtRead.Reset();
    s->qtWrite.Reset();
    s->qtContext.Reset();
    s->bound = false;
}

// Main thread; the device calls are free-threaded. Unbind and initialize run through the executor (drains it), so no
// render of the old binding is in flight when the render target changes.
int bind(OffscreenScene* s, void* device, void* ntHandle, int width, int height, int format, int samples, int* usedSamples)
{
    s->executor.call([s] { unbind(s); });
    if (!device || !ntHandle)
        return fail(VLQT_ERR_BAD_ARGUMENT, "bind: null device or handle");
    if (width < 1 || height < 1 || width > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION || height > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION)
        return fail(VLQT_ERR_BAD_ARGUMENT, "bind: size out of range");
    if (!formatFits(format, static_cast<DXGI_FORMAT>(format)))
        return fail(VLQT_ERR_FORMAT_UNSUPPORTED, "bind: format is not R8G8B8A8_UNORM(_SRGB), B8G8R8A8_UNORM(_SRGB) or R16G16B16A16_FLOAT");
    auto* foreign = static_cast<ID3D11Device*>(device);
    const unsigned long long foreignLuid = vlqt::device::adapterLuidOf(foreign);
    if (!foreignLuid)
        return fail(VLQT_ERR_BAD_ARGUMENT, "bind: device is no D3D11 device with a DXGI adapter");
    // A failed initialize after a device rebuild (deviceRestored) is retried here.
    if (!s->window->rhi() && vlqt::device::isOnSharedDevice(s->window))
        s->executor.call([s] { s->renderControl->initialize(); });
    ID3D11Device* qt = qtDevice(s);
    if (!qt)
        return fail(VLQT_ERR_DEVICE_LOST, "bind: the scene has no QtNative device");
    if (FAILED(qt->GetDeviceRemovedReason()))
        return fail(VLQT_ERR_DEVICE_LOST, "bind: QtNative's device is lost", qt->GetDeviceRemovedReason());
    if (vlqt::device::adapterLuidOf(qt) != foreignLuid) {
        if (int rc = matchAdapter(s, foreignLuid); rc != VLQT_OK)
            return rc;
        qt = qtDevice(s); // the scene's QRhi is on the moved device now
        if (!qt)
            return fail(VLQT_ERR_DEVICE_LOST, "bind: the scene has no QtNative device after the move");
    }

    ComPtr<ID3D11Device1> qt1;
    HRESULT hr = qt->QueryInterface(IID_PPV_ARGS(qt1.GetAddressOf()));
    if (FAILED(hr))
        return fail(VLQT_ERR_OPEN_FAILED, "bind: QtNative's device has no ID3D11Device1", hr);
    ComPtr<ID3D11Texture2D> texture;
    hr = qt1->OpenSharedResource1(static_cast<HANDLE>(ntHandle), IID_PPV_ARGS(texture.GetAddressOf()));
    if (FAILED(hr))
        return fail(VLQT_ERR_OPEN_FAILED, "bind: OpenSharedResource1 failed", hr);
    D3D11_TEXTURE2D_DESC desc;
    texture->GetDesc(&desc);
    if (!(desc.BindFlags & D3D11_BIND_RENDER_TARGET) || desc.ArraySize != 1 || desc.SampleDesc.Count != 1)
        return fail(VLQT_ERR_OPEN_FAILED, "bind: the texture is no single-sampled render target");
    if (desc.Width != UINT(width) || desc.Height != UINT(height))
        return fail(VLQT_ERR_BAD_ARGUMENT, "bind: width/height are not the texture's size");
    if (!formatFits(format, desc.Format))
        return fail(VLQT_ERR_FORMAT_UNSUPPORTED, "bind: format does not fit the texture's format");

    ComPtr<ID3D11Device5> qt5;
    ComPtr<ID3D11Device5> foreign5;
    ComPtr<ID3D11DeviceContext> qtImmediate;
    ComPtr<ID3D11DeviceContext4> qtContext;
    ComPtr<ID3D11DeviceContext> foreignImmediate;
    ComPtr<ID3D11DeviceContext4> foreignContext;
    qt->GetImmediateContext(qtImmediate.GetAddressOf());
    foreign->GetImmediateContext(foreignImmediate.GetAddressOf());
    if (FAILED(qt->QueryInterface(IID_PPV_ARGS(qt5.GetAddressOf())))
        || FAILED(foreign->QueryInterface(IID_PPV_ARGS(foreign5.GetAddressOf())))
        || FAILED(qtImmediate.As(&qtContext)) || FAILED(foreignImmediate.As(&foreignContext)))
        return fail(VLQT_ERR_FENCE_FAILED, "bind: no ID3D11Device5 / ID3D11DeviceContext4 (Windows 10 1703 or newer)");
    ComPtr<ID3D11Fence> qtRead, foreignRead, qtWrite, foreignWrite;
    if (int rc = createFence(qt5.Get(), foreign5.Get(), qtRead, foreignRead); rc != VLQT_OK)
        return rc;
    if (int rc = createFence(qt5.Get(), foreign5.Get(), qtWrite, foreignWrite); rc != VLQT_OK)
        return rc;

    vlqt::linear_pass::Pass linearPass;
    if (vlqt::linear_pass::isLinear(DXGI_FORMAT(format))) {
        const char* what = "";
        if (HRESULT hr = vlqt::linear_pass::create(qt, texture.Get(), DXGI_FORMAT(format), UINT(width), UINT(height), linearPass, what);
            FAILED(hr))
            return fail(VLQT_ERR_OPEN_FAILED, what, hr);
    }

    // MSAA: Qt renders into a multisample colour and depth-stencil texture of its own and resolves into the
    // target at the end of the pass (MultisampleResolve; without the flag the count would mean "the target itself is
    // multisample"). With the linear pass Qt resolves into the pass's float source, the pass reads resolved pixels.
    // Layers in the scene stay single-sample (the render control's count, docs/design/clear-and-msaa.md, "MSAA").
    const DXGI_FORMAT targetFormat = linearPass.source ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT(format);
    const int used = vlqt::device::clampSamples(samples, int(targetFormat), s->window->rhi());
    const QQuickRenderTarget::Flags msaa = used > 1 ? QQuickRenderTarget::Flag::MultisampleResolve : QQuickRenderTarget::Flags();
    // 1 DIP = 1 texture pixel, also on a high-DPI screen.
    auto target = QQuickRenderTarget::fromD3D11Texture(linearPass.source ? linearPass.source.Get() : texture.Get(), uint(targetFormat),
        QSize(width, height), used, msaa);
    target.setDevicePixelRatio(1.0);
    s->window->setGeometry(0, 0, width, height);
    s->window->setRenderTarget(target);
    s->polished = false;

    s->texture = std::move(texture);
    s->foreignDevice = foreign;
    s->linearPass = std::move(linearPass);
    s->qtContext = std::move(qtContext);
    s->qtRead = std::move(qtRead);
    s->qtWrite = std::move(qtWrite);
    s->foreignRead = std::move(foreignRead);
    s->foreignWrite = std::move(foreignWrite);
    s->readValue = 0;
    s->writeValue = 0;
    s->renders = 0;
    s->software = isSoftware(qt) || isSoftware(foreign);
    static bool softwareLogged = false;
    if (s->software && s->executor.thread() && !std::exchange(softwareLogged, true))
        qCInfo(lcOffscreen, "offscreen bind on a software adapter: render_batch waits for the render thread's submission "
            "(logged once)");
    s->bound = true;
    s->dirty = true;
    if (usedSamples)
        *usedSamples = used;
    return VLQT_OK;
}

} // namespace vlqt::offscreen

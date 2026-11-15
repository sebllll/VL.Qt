#include "offscreen.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QLoggingCategory>
#include <QtGui/QGuiApplication>
#include <QtQuick/QQuickRenderControl>
#include <QtQuick/QQuickRenderTarget>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>

#include <algorithm>
#include <cstdio>
#include <exception>
#include <vector>

#ifdef _WIN32
#include "device.h"

#include <QtQuick/private/qquickrendercontrol_p.h>
#include <QtQuick/private/qquickwindow_p.h>

#ifndef NOMINMAX
#define NOMINMAX // std::max
#endif
#include <d3d11_4.h>
#include <windows.h>
#include <wrl/client.h>
#endif

namespace vlqt::offscreen {

// Lifetime: vlqt_offscreen_create -> (bind -> render_batch ... -> unbind)* -> vlqt_window_destroy (release, then
// destroy after the QQuickWindow). The QRhi of the scene belongs to renderControl and lives on QtNative's shared device
// from create on, so the scene is never bound to another device than the one its QRhi uses.
//
// A binding is the texture opened on QtNative's device (Qt's render target), QtNative's immediate context and two
// shared fences, each as created on QtNative's device and as opened on the foreign one. render_batch orders the GPUs
// with them: readFence = the foreign device is done reading the texture (Qt may write), writeFence = Qt is done
// writing (the foreign device may read). The values count up from 0 per binding.
//
// Device rebuild (loss, or a move to the foreign adapter in bind): a device::Listener unbinds every scene and drops its
// scene graph and QRhi (lost); device::rebuild hands the scene's window, which then has no QRhi, the new device like
// any window not yet exposed; restored initializes the render control again. The caller sees is_bound 0 and binds again.
//
// Foreign device: QtNative holds no reference to it of its own; the two fences opened on it do, until unbind. So a
// bound scene keeps it alive after its owner released it, and unbind or destroy then frees it.
struct State
{
    QQuickRenderControl* renderControl = nullptr;
    QQuickWindow* window = nullptr; // the scene's window, owned by the handle (vlqt.cpp)
    // The scene wants a frame: sceneChanged (items changed: polish, sync, render) or renderRequested (render only,
    // e.g. QQuickWindow::update() in vlqt_window_apply); bind sets it too. render_batch renders a bound scene only
    // while it is set, so an unchanged scene costs nothing.
    bool dirty = true;
    bool bound = false;
    unsigned long long readValue = 0;  // last value signalled on the read fence
    unsigned long long writeValue = 0; // last value signalled on the write fence
    long long renders = 0;             // renders into the texture since bind
#ifdef _WIN32
    ID3D11Texture2D* texture = nullptr;        // the foreign texture, opened on QtNative's device
    ID3D11DeviceContext4* qtContext = nullptr; // immediate context of QtNative's device
    ID3D11Fence* qtRead = nullptr;             // the fences on QtNative's device ...
    ID3D11Fence* qtWrite = nullptr;
    ID3D11Fence* foreignRead = nullptr;        // ... and opened on the foreign device
    ID3D11Fence* foreignWrite = nullptr;
#endif
};

namespace {

Q_LOGGING_CATEGORY(lcOffscreen, "vlqt.offscreen")

// Every scene between create and destroy, for the device listener. Main thread.
std::vector<State*> s_scenes;

#ifdef _WIN32
void deviceLost();
void deviceRestored();
#endif

void* create(int width, int height)
{
    if (!qGuiApp) {
        setError("offscreen scene: app not initialized");
        return nullptr;
    }
    // The fences and the opened texture (bind) are D3D11 objects on QtNative's shared device.
    if (QQuickWindow::graphicsApi() != QSGRendererInterface::Direct3D11) {
        setError("offscreen scene: needs Direct3D 11 (Windows)");
        return nullptr;
    }
    auto* state = new State();
    state->renderControl = new QQuickRenderControl();
    auto* window = new QQuickWindow(state->renderControl);
    state->window = window;
    // 1 DIP = 1 texture pixel; the size until the first bind, which sets the texture's.
    window->setGeometry(0, 0, std::max(width, 1), std::max(height, 1));
    void* handle = createHandle(window, state);
    // device::attach marks a window that got the shared device; without it initialize would create a device of its own.
    if (!window->property("_vlqt_shared_device").toBool()) {
        vlqt_window_destroy(handle);
        setError("offscreen scene: QtNative has no shared D3D11 device (VLQT_SHARED_DEVICE=0 or creation failed, see the log)");
        return nullptr;
    }
    // The QRhi now, not at bind: vlqt_test_device_info, snapshots and resources see the scene's device at once.
    if (!state->renderControl->initialize()) {
        vlqt_window_destroy(handle);
        setError("offscreen scene: QQuickRenderControl::initialize failed (see the log)");
        return nullptr;
    }
    QObject::connect(state->renderControl, &QQuickRenderControl::sceneChanged, state->renderControl,
        [state] { state->dirty = true; });
    QObject::connect(state->renderControl, &QQuickRenderControl::renderRequested, state->renderControl,
        [state] { state->dirty = true; });
#ifdef _WIN32
    static const bool listening = [] {
        vlqt::device::addListener({ deviceLost, deviceRestored });
        return true;
    }();
    (void)listening;
#endif
    s_scenes.push_back(state);
    return handle;
}

#ifdef _WIN32

using Microsoft::WRL::ComPtr;

template <class T> void releaseCom(T*& p)
{
    if (p) {
        p->Release();
        p = nullptr;
    }
}

int fail(int code, const char* what, HRESULT hr = S_OK)
{
    char text[256];
    if (hr == S_OK)
        std::snprintf(text, sizeof text, "offscreen: %s", what);
    else
        std::snprintf(text, sizeof text, "offscreen: %s (HRESULT 0x%08lX)", what, static_cast<unsigned long>(hr));
    setError(text);
    return code;
}

ID3D11Device* qtDevice(const State* s)
{
    return static_cast<ID3D11Device*>(vlqt::device::nativeDevice(s->window->rhi()));
}

// Frees the binding: render target -> opened texture -> fences. While the QRhi still exists (before the QQuickWindow
// is deleted).
void unbind(State* s)
{
    if (s->texture) {
        s->window->setRenderTarget(QQuickRenderTarget());
        // Qt turns a render target into QRhi wrappers (texture, render target view) only at the next frame
        // (ensureCustomRenderTarget); drop them now, else their view keeps the opened texture alive.
        QQuickWindowPrivate::get(s->window)->ensureCustomRenderTarget();
        // QRhi D3D11 leaves the last render target view bound to the immediate context, which also holds it. Every
        // render pass of QRhi binds its own targets, so clearing them here changes nothing for the windows.
        s->qtContext->OMSetRenderTargets(0, nullptr, nullptr);
    }
    releaseCom(s->texture);
    releaseCom(s->foreignRead);
    releaseCom(s->foreignWrite);
    releaseCom(s->qtRead);
    releaseCom(s->qtWrite);
    releaseCom(s->qtContext);
    s->bound = false;
}

// device::Listener lost: the old device goes away. Unbind (render target, opened texture, fences on both sides), then
// drop scene graph and QRhi as ~QQuickRenderControl does: initialize() reuses an existing QRhi, so a new one on the
// new device needs resetRhi. The window has no QRhi afterwards; device::rebuild hands it the new device.
void deviceLost()
{
    for (State* s : s_scenes) {
        const bool wasBound = s->bound;
        unbind(s);
        s->renderControl->invalidate();
        QQuickWindowPrivate* wd = QQuickWindowPrivate::get(s->window);
        wd->rhi = nullptr;
        QQuickRenderControlPrivate::get(s->renderControl)->resetRhi(wd->graphicsConfig);
        s->dirty = true;
        if (wasBound)
            qCInfo(lcOffscreen, "offscreen scene %p unbound: QtNative's device is rebuilt, the caller binds again",
                static_cast<void*>(s));
    }
}

// device::Listener restored: a QRhi on the new device (the window got it from device::rebuild).
void deviceRestored()
{
    for (State* s : s_scenes)
        if (!s->renderControl->initialize())
            qCWarning(lcOffscreen, "offscreen scene %p: QQuickRenderControl::initialize failed after the device "
                "rebuild, bind retries it", static_cast<void*>(s));
}

// The adapters of the foreign device and of QtNative's device differ: move QtNative's device to the foreign adapter
// if nothing renders on it yet (no on-screen window with a QRhi on it, no other scene bound), else ADAPTER_MISMATCH.
int matchAdapter(State* s, unsigned long long foreignLuid)
{
    const bool otherBound = std::any_of(s_scenes.begin(), s_scenes.end(), [s](const State* o) { return o != s && o->bound; });
    if (!otherBound && vlqt::device::canMove()) {
        if (vlqt::device::moveTo(foreignLuid))
            return VLQT_OFFSCREEN_OK;
        return fail(VLQT_OFFSCREEN_DEVICE_LOST, "bind: moving QtNative's device to the foreign adapter failed (see the log)");
    }
    static bool warned = false;
    if (!warned) {
        warned = true;
        qCWarning(lcOffscreen, "offscreen bind: the foreign device is on adapter %016llx, QtNative's device on %016llx, "
            "which a Qt window or another offscreen scene already uses. Use one adapter for both (this warning appears once)",
            foreignLuid, vlqt::device::adapterLuid());
    }
    return fail(VLQT_OFFSCREEN_ADAPTER_MISMATCH, "bind: the foreign device is on another adapter than QtNative's device, "
        "which a Qt window or another offscreen scene already uses");
}

// The 8-bit render target formats may view a typeless texture of their family.
bool formatFits(int rtvFormat, DXGI_FORMAT texture)
{
    switch (rtvFormat) {
    case DXGI_FORMAT_R8G8B8A8_UNORM:
        return texture == DXGI_FORMAT_R8G8B8A8_UNORM || texture == DXGI_FORMAT_R8G8B8A8_TYPELESS;
    case DXGI_FORMAT_B8G8R8A8_UNORM:
        return texture == DXGI_FORMAT_B8G8R8A8_UNORM || texture == DXGI_FORMAT_B8G8R8A8_TYPELESS;
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
        return fail(VLQT_OFFSCREEN_FENCE_FAILED, "bind: CreateFence failed", hr);
    HANDLE handle = nullptr;
    hr = qtFence->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &handle);
    if (FAILED(hr))
        return fail(VLQT_OFFSCREEN_FENCE_FAILED, "bind: ID3D11Fence::CreateSharedHandle failed", hr);
    hr = foreign->OpenSharedFence(handle, IID_PPV_ARGS(foreignFence.GetAddressOf()));
    CloseHandle(handle);
    if (FAILED(hr))
        return fail(VLQT_OFFSCREEN_FENCE_FAILED, "bind: OpenSharedFence on the foreign device failed", hr);
    return VLQT_OFFSCREEN_OK;
}

int bind(State* s, void* device, void* ntHandle, int width, int height, int rtvFormat)
{
    unbind(s);
    if (!device || !ntHandle)
        return fail(VLQT_OFFSCREEN_BAD_ARGUMENT, "bind: null device or handle");
    if (width < 1 || height < 1 || width > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION || height > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION)
        return fail(VLQT_OFFSCREEN_BAD_ARGUMENT, "bind: size out of range");
    if (!formatFits(rtvFormat, static_cast<DXGI_FORMAT>(rtvFormat)))
        return fail(VLQT_OFFSCREEN_FORMAT_UNSUPPORTED, "bind: rtvFormat is not R8G8B8A8_UNORM, B8G8R8A8_UNORM or R16G16B16A16_FLOAT");
    auto* foreign = static_cast<ID3D11Device*>(device);
    const unsigned long long foreignLuid = vlqt::device::adapterLuidOf(foreign);
    if (!foreignLuid)
        return fail(VLQT_OFFSCREEN_BAD_ARGUMENT, "bind: device is no D3D11 device with a DXGI adapter");
    // A failed initialize after a device rebuild (deviceRestored) is retried here.
    if (!s->window->rhi() && s->window->property("_vlqt_shared_device").toBool())
        s->renderControl->initialize();
    ID3D11Device* qt = qtDevice(s);
    if (!qt)
        return fail(VLQT_OFFSCREEN_DEVICE_LOST, "bind: the scene has no QtNative device");
    if (FAILED(qt->GetDeviceRemovedReason()))
        return fail(VLQT_OFFSCREEN_DEVICE_LOST, "bind: QtNative's device is lost", qt->GetDeviceRemovedReason());
    if (vlqt::device::adapterLuidOf(qt) != foreignLuid) {
        if (int rc = matchAdapter(s, foreignLuid); rc != VLQT_OFFSCREEN_OK)
            return rc;
        qt = qtDevice(s); // the scene's QRhi is on the moved device now
        if (!qt)
            return fail(VLQT_OFFSCREEN_DEVICE_LOST, "bind: the scene has no QtNative device after the move");
    }

    ComPtr<ID3D11Device1> qt1;
    HRESULT hr = qt->QueryInterface(IID_PPV_ARGS(qt1.GetAddressOf()));
    if (FAILED(hr))
        return fail(VLQT_OFFSCREEN_OPEN_FAILED, "bind: QtNative's device has no ID3D11Device1", hr);
    ComPtr<ID3D11Texture2D> texture;
    hr = qt1->OpenSharedResource1(static_cast<HANDLE>(ntHandle), IID_PPV_ARGS(texture.GetAddressOf()));
    if (FAILED(hr))
        return fail(VLQT_OFFSCREEN_OPEN_FAILED, "bind: OpenSharedResource1 failed", hr);
    D3D11_TEXTURE2D_DESC desc;
    texture->GetDesc(&desc);
    if (!(desc.BindFlags & D3D11_BIND_RENDER_TARGET) || desc.ArraySize != 1 || desc.SampleDesc.Count != 1)
        return fail(VLQT_OFFSCREEN_OPEN_FAILED, "bind: the texture is no single-sampled render target");
    if (desc.Width != UINT(width) || desc.Height != UINT(height))
        return fail(VLQT_OFFSCREEN_BAD_ARGUMENT, "bind: width/height are not the texture's size");
    if (!formatFits(rtvFormat, desc.Format))
        return fail(VLQT_OFFSCREEN_FORMAT_UNSUPPORTED, "bind: rtvFormat does not fit the texture's format");

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
        return fail(VLQT_OFFSCREEN_FENCE_FAILED, "bind: no ID3D11Device5 / ID3D11DeviceContext4 (Windows 10 1703 or newer)");
    ComPtr<ID3D11Fence> qtRead, foreignRead, qtWrite, foreignWrite;
    if (int rc = createFence(qt5.Get(), foreign5.Get(), qtRead, foreignRead); rc != VLQT_OFFSCREEN_OK)
        return rc;
    if (int rc = createFence(qt5.Get(), foreign5.Get(), qtWrite, foreignWrite); rc != VLQT_OFFSCREEN_OK)
        return rc;

    // 1 DIP = 1 texture pixel, also on a high-DPI screen.
    auto target = QQuickRenderTarget::fromD3D11Texture(texture.Get(), uint(rtvFormat), QSize(width, height));
    target.setDevicePixelRatio(1.0);
    s->window->setGeometry(0, 0, width, height);
    s->window->setRenderTarget(target);

    s->texture = texture.Detach();
    s->qtContext = qtContext.Detach();
    s->qtRead = qtRead.Detach();
    s->qtWrite = qtWrite.Detach();
    s->foreignRead = foreignRead.Detach();
    s->foreignWrite = foreignWrite.Detach();
    s->readValue = 0;
    s->writeValue = 0;
    s->renders = 0;
    s->bound = true;
    s->dirty = true;
    return VLQT_OFFSCREEN_OK;
}

// One frame of a bound, dirty scene, ordered against the foreign context by the fences (vlqt.h).
void render(State* s, ID3D11DeviceContext4* foreign)
{
    // The commands of the previous frame that read the texture are already submitted on the foreign context.
    foreign->Signal(s->foreignRead, ++s->readValue);
    // Before beginFrame: QRhi D3D11 submits the frame's commands to the immediate context only in endFrame.
    s->qtContext->Wait(s->qtRead, s->readValue);

    QQuickRenderControl* rc = s->renderControl;
    rc->polishItems();
    // Polish's own changes are in this frame; a change made while syncing or rendering renders next frame.
    s->dirty = false;
    rc->beginFrame();
    rc->sync();
    rc->render();
    rc->endFrame();
    // TODO: GPU time of the frame: perf.cpp afterRendering reads it only from a swap chain; a scene has
    // renderControl->commandBuffer() (and its QRhi needs EnableTimestamps).

    s->qtContext->Signal(s->qtWrite, ++s->writeValue);
    // Submits the frame and the signal; without it the foreign GPU would wait forever.
    s->qtContext->Flush();
    foreign->Wait(s->foreignWrite, s->writeValue);
    s->renders++;
}

int renderBatch(void* context, void* const* scenes, int count, double driverSeconds)
{
    if (!qGuiApp || QQuickWindow::graphicsApi() != QSGRendererInterface::Direct3D11)
        return fail(VLQT_OFFSCREEN_NOT_SUPPORTED, "render_batch: needs Direct3D 11 and an initialized app");
    vlqt_driver_advance(driverSeconds);
    ComPtr<ID3D11DeviceContext4> foreign;
    int rendered = 0;
    for (int i = 0; i < count; i++) {
        State* s = stateOf(scenes[i]);
        if (!s || !s->bound || !s->dirty)
            continue;
        if (!foreign) {
            HRESULT hr = static_cast<ID3D11DeviceContext*>(context)->QueryInterface(IID_PPV_ARGS(foreign.GetAddressOf()));
            if (FAILED(hr))
                return fail(VLQT_OFFSCREEN_BAD_ARGUMENT, "render_batch: context is no ID3D11DeviceContext4", hr);
        }
        // Lost but not rebuilt yet: the next pump (device::checkLost) rebuilds and unbinds every scene.
        ID3D11Device* qt = qtDevice(s);
        if (!qt || FAILED(qt->GetDeviceRemovedReason()))
            return fail(VLQT_OFFSCREEN_DEVICE_LOST, "render_batch: QtNative's device is lost");
        render(s, foreign.Get());
        rendered++;
    }
    return rendered;
}

#endif // _WIN32

} // namespace

void release(State* state)
{
#ifdef _WIN32
    unbind(state);
#else
    (void)state;
#endif
}

void destroy(State* state)
{
    s_scenes.erase(std::remove(s_scenes.begin(), s_scenes.end(), state), s_scenes.end());
    delete state->renderControl;
    delete state;
}

namespace {

State* sceneOf(const QQuickWindow* window)
{
    const auto it = std::find_if(s_scenes.begin(), s_scenes.end(), [window](const State* s) { return s->window == window; });
    return it != s_scenes.end() ? *it : nullptr;
}

} // namespace

bool isScene(const QQuickWindow* window)
{
    return window && sceneOf(window);
}

bool requestFrame(const QQuickWindow* window)
{
    State* s = sceneOf(window);
    if (!s || !s->bound)
        return false;
    s->dirty = true;
    return true;
}

int testState(void* scene, unsigned long long* readFence, unsigned long long* writeFence, long long* renders)
{
    State* s = stateOf(scene);
    if (!s)
        return VLQT_OFFSCREEN_NULL_SCENE;
    if (!s->bound)
        return VLQT_OFFSCREEN_UNBOUND;
    if (readFence)
        *readFence = s->readValue;
    if (writeFence)
        *writeFence = s->writeValue;
    if (renders)
        *renders = s->renders;
    return VLQT_OFFSCREEN_OK;
}

} // namespace vlqt::offscreen

using namespace vlqt::offscreen;

namespace {

template <class F>
int guardedCode(F&& f)
{
    try {
        return f();
    } catch (const std::exception& e) {
        setError(std::string("exception: ") + e.what());
    } catch (...) {
        setError("unknown exception");
    }
    return VLQT_OFFSCREEN_BAD_ARGUMENT;
}

} // namespace

extern "C" {

VLQT_API void* vlqt_offscreen_create(int width, int height)
{
    try {
        return create(width, height);
    } catch (const std::exception& e) {
        setError(std::string("exception: ") + e.what());
    } catch (...) {
        setError("unknown exception");
    }
    return nullptr;
}

VLQT_API int vlqt_offscreen_bind(void* scene, void* device, void* ntHandle, int width, int height, int rtvFormat)
{
    State* state = stateOf(scene);
    if (!state)
        return VLQT_OFFSCREEN_NULL_SCENE;
#ifdef _WIN32
    return guardedCode([&] {
        int rc = bind(state, device, ntHandle, width, height, rtvFormat);
        if (rc != VLQT_OFFSCREEN_OK)
            unbind(state);
        return rc;
    });
#else
    (void)device;
    (void)ntHandle;
    (void)width;
    (void)height;
    (void)rtvFormat;
    setError("offscreen bind: needs Direct3D 11 (Windows)");
    return VLQT_OFFSCREEN_NOT_SUPPORTED;
#endif
}

VLQT_API int vlqt_offscreen_unbind(void* scene)
{
    State* state = stateOf(scene);
    if (!state)
        return VLQT_OFFSCREEN_NULL_SCENE;
    return guardedCode([&] {
        release(state);
        return VLQT_OFFSCREEN_OK;
    });
}

VLQT_API int vlqt_offscreen_is_bound(void* scene)
{
    State* state = stateOf(scene);
    if (!state)
        return VLQT_OFFSCREEN_NULL_SCENE;
    return state->bound ? 1 : 0;
}

VLQT_API int vlqt_offscreen_render_batch(void* context, void* const* scenes, int count, double driverSeconds)
{
    if (!context || count < 0 || (!scenes && count > 0)) {
        setError("offscreen render_batch: null context, null scenes or negative count");
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    }
#ifdef _WIN32
    return guardedCode([&] { return renderBatch(context, scenes, count, driverSeconds); });
#else
    (void)driverSeconds;
    setError("offscreen render_batch: needs Direct3D 11 (Windows)");
    return VLQT_OFFSCREEN_NOT_SUPPORTED;
#endif
}

} // extern "C"

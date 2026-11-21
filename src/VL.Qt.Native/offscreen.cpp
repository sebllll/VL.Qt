#include "offscreen.h"
#include "app/exports.h"
#include "protocol.h"
#include "render_thread.h"
#include "vlqt.h"

#include <QtCore/QElapsedTimer>
#include <QtCore/QLoggingCategory>
#include <QtGui/QGuiApplication>
#include <QtGui/QFocusEvent>
#include <QtGui/QInputMethodEvent>
#include <QtGui/QKeyEvent>
#include <QtGui/QMouseEvent>
#include <QtGui/QPointingDevice>
#include <QtGui/QStyleHints>
#include <QtGui/QTabletEvent>
#include <QtGui/QWheelEvent>
#include <QtGui/qpa/qwindowsysteminterface.h>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickRenderControl>
#include <QtQuick/QQuickRenderTarget>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>
// Scene focus: SceneRenderControlPrivate. Private API, check on Qt updates.
#include <QtQuick/private/qquickrendercontrol_p.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <exception>
#include <vector>

#ifdef _WIN32
#include "device.h"

#include <QtQuick/private/qquickwindow_p.h>

#ifndef NOMINMAX
#define NOMINMAX // std::max
#endif
#include <d3d11_4.h>
#include <windows.h>
#include <wrl/client.h>

#include "linear_pass.h" // after NOMINMAX: it includes d3d11.h, and with it windows.h
#endif

namespace vlqt::offscreen {

// Lifetime: vlqt_offscreen_create -> (bind -> render_batch ... -> unbind)* -> vlqt_scene_destroy (release, then
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
//
// Render thread (phase 5, render-texture.md "Qt render thread", decisions c and e): a scene of a threaded process
// gets a render thread at create (render_thread::assign) and its scene graph and QRhi live there: initialize, beginFrame,
// sync, render, endFrame, invalidate and unbind run there, the main thread waits for each of them except render +
// endFrame + Signal + Flush (posted by render_batch). Items, polish, input, apply and bind stay on the main thread; the
// render thread touches items only in sync, while the main thread waits. Every blocking job runs after the jobs posted
// before it, so unbind, destroy, device loss and adapter move drain the thread first. Without a render thread
// (vlqt_offscreen_set_render_threads(0), VLQT_OFFSCREEN_THREAD=0) everything runs on the main thread as before.
struct State
{
    QQuickRenderControl* renderControl = nullptr;
    QQuickWindow* window = nullptr; // the scene's window, owned by the handle (vlqt.cpp)
    // The scene wants a frame: sceneChanged (items changed: polish, sync, render) or renderRequested (render only,
    // e.g. QQuickWindow::update() in vlqt_scene_apply); bind sets it too. render_batch renders a bound scene only
    // while it is set, so an unchanged scene costs nothing.
    bool dirty = true;
    bool bound = false;
    unsigned long long readValue = 0;  // last value signalled on the read fence
    unsigned long long writeValue = 0; // last value signalled on the write fence
    std::atomic<long long> renders = 0; // renders into the texture since bind (the render thread counts them)
    render_thread::RenderThread* thread = nullptr; // the scene's render thread; null = main thread
    // Input. Devices of their own per scene, children of renderControl, created at the first event: point ids
    // and grabs live in the device, so two scenes fed the same host input (a press lands in both) must not share one.
    QPointingDevice* mouse = nullptr;
    QPointingDevice* touchscreen = nullptr;
    QPointingDevice* pen = nullptr; // a stylus, as Qt's Windows plugin reports a pen (WM_POINTER)
    // The scene has the keyboard focus (FocusIn sent): a scene window never becomes QGuiApplication's focus window.
    bool focused = false;
    Qt::MouseButtons buttons = Qt::NoButton;          // held after the last mouse event
    std::vector<std::pair<int, QPointF>> touchPoints; // active points (id, DIP) of touchscreen
    bool penDown = false;                             // the pen touches (between PRESS and RELEASE)
    QPointF penPos;                                   // its last position
    QElapsedTimer clock;                              // event timestamps
    // The last press that may start a double click (QGuiApplication's rule); button NoButton after a double click.
    ulong lastPressTime = 0;
    QPointF lastPressPos;
    Qt::MouseButton lastPressButton = Qt::NoButton;
#ifdef _WIN32
    ID3D11Texture2D* texture = nullptr;        // the foreign texture, opened on QtNative's device
    // A consumer that samples linear values (SRGB view, float): Qt renders into the pass's source, the pass writes the
    // texture after each frame. Empty for a UNORM view: Qt renders into the texture itself.
    vlqt::linear_pass::Pass linearPass;
    ID3D11DeviceContext4* qtContext = nullptr; // immediate context of QtNative's device
    ID3D11Fence* qtRead = nullptr;             // the fences on QtNative's device ...
    ID3D11Fence* qtWrite = nullptr;
    ID3D11Fence* foreignRead = nullptr;        // ... and opened on the foreign device
    ID3D11Fence* foreignWrite = nullptr;
    // A device of the binding is a software adapter (WARP): its context deadlocks waiting for a value that is signalled
    // only later (spike 5.1), so render_batch waits until the render thread submitted the signal.
    bool software = false;
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

// f on the scene's render thread, blocking and after every job posted before (drains it); here without one.
template <class F> void onRenderThread(State* s, F&& f)
{
    if (s->thread)
        s->thread->call(std::forward<F>(f));
    else
        f();
}

// Keyboard focus of a scene: Qt Quick gives a window's root item the focus, and so its items the active focus,
// only while QGuiApplication's focus window is that window, its render window
// (QQuickRenderControlPrivate::isRenderWindow) or none. A scene has no render window, and right after the host window
// was clicked a Qt window can still be the focus window (its deactivation is queued). So while the scene has the focus
// (FocusIn sent), it counts the focus window as its render window, as QQuickWidget's render control does for its window.
class SceneRenderControlPrivate : public QQuickRenderControlPrivate
{
public:
    SceneRenderControlPrivate(QQuickRenderControl* renderControl, const State* state)
        : QQuickRenderControlPrivate(renderControl), m_state(state) {}
    bool isRenderWindow(const QWindow* w) override
    {
        return (m_state->focused && w && w == QGuiApplication::focusWindow()) || QQuickRenderControlPrivate::isRenderWindow(w);
    }
private:
    const State* m_state;
};

class SceneRenderControl : public QQuickRenderControl
{
public:
    explicit SceneRenderControl(const State* state) : QQuickRenderControl(*new SceneRenderControlPrivate(this, state)) {}
};

vlqt_scene* create(int width, int height)
{
    if (!qGuiApp) {
        vlqt::setLastError("offscreen scene: app not initialized");
        return nullptr;
    }
    // The fences and the opened texture (bind) are D3D11 objects on QtNative's shared device.
    if (QQuickWindow::graphicsApi() != QSGRendererInterface::Direct3D11) {
        vlqt::setLastError("offscreen scene: needs Direct3D 11 (Windows)");
        return nullptr;
    }
    auto* state = new State();
    state->renderControl = new SceneRenderControl(state);
    auto* window = new QQuickWindow(state->renderControl);
    state->window = window;
    // 1 DIP = 1 texture pixel; the size until the first bind, which sets the texture's.
    window->setGeometry(0, 0, std::max(width, 1), std::max(height, 1));
    vlqt_scene* handle = createHandle(window, state);
    // device::attach marks a window that got the shared device; without it initialize would create a device of its own.
    if (!window->property("_vlqt_shared_device").toBool()) {
        vlqt_scene_destroy(handle);
        vlqt::setLastError("offscreen scene: QtNative has no shared D3D11 device (VLQT_SHARED_DEVICE=0 or creation failed, see the log)");
        return nullptr;
    }
    // From here on the scene graph and the QRhi belong to the render thread (prepareThread before initialize).
    state->thread = render_thread::assign();
    if (state->thread)
        state->renderControl->prepareThread(state->thread->thread());
    // The QRhi now, not at bind: vlqt_test_device_info, snapshots and resources see the scene's device at once.
    bool initialized = false;
    onRenderThread(state, [state, &initialized] { initialized = state->renderControl->initialize(); });
    if (!initialized) {
        vlqt_scene_destroy(handle);
        vlqt::setLastError("offscreen scene: QQuickRenderControl::initialize failed (see the log)");
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
    return vlqt::fail(code, text);
}

ID3D11Device* qtDevice(const State* s)
{
    return static_cast<ID3D11Device*>(vlqt::device::nativeDevice(s->window->rhi()));
}

// Frees the binding: render target -> opened texture -> fences. While the QRhi still exists (before the QQuickWindow
// is deleted). On the scene's render thread (onRenderThread).
void unbind(State* s)
{
    if (s->texture) {
        vlqt::device::ContextLock lock(s->qtContext);
        s->window->setRenderTarget(QQuickRenderTarget());
        // Qt turns a render target into QRhi wrappers (texture, render target view) only at the next frame
        // (ensureCustomRenderTarget); drop them now, else their view keeps the opened texture alive.
        QQuickWindowPrivate::get(s->window)->ensureCustomRenderTarget();
        // QRhi D3D11 leaves the last render target view bound to the immediate context, which also holds it. Every
        // render pass of QRhi binds its own targets, so clearing them here changes nothing for the windows.
        s->qtContext->OMSetRenderTargets(0, nullptr, nullptr);
    }
    vlqt::linear_pass::release(s->linearPass);
    releaseCom(s->texture);
    releaseCom(s->foreignRead);
    releaseCom(s->foreignWrite);
    releaseCom(s->qtRead);
    releaseCom(s->qtWrite);
    releaseCom(s->qtContext);
    s->bound = false;
}

// Drops scene graph and QRhi as ~QQuickRenderControl does: initialize() reuses an existing QRhi, so a new one (on a
// new device) needs resetRhi. The window has no QRhi afterwards. On the scene's render thread.
void dropRhi(State* s)
{
    s->renderControl->invalidate();
    QQuickWindowPrivate* wd = QQuickWindowPrivate::get(s->window);
    wd->rhi = nullptr;
    QQuickRenderControlPrivate::get(s->renderControl)->resetRhi(wd->graphicsConfig);
}

// device::Listener lost: the old device goes away. Unbind (render target, opened texture, fences on both sides), then
// drop scene graph and QRhi; device::rebuild hands the window the new device.
void deviceLost()
{
    for (State* s : s_scenes) {
        const bool wasBound = s->bound;
        onRenderThread(s, [s] {
            unbind(s);
            dropRhi(s);
        });
        s->dirty = true;
        if (wasBound)
            qCInfo(lcOffscreen, "offscreen scene %p unbound: QtNative's device is rebuilt, the caller binds again",
                static_cast<void*>(s));
    }
}

// device::Listener restored: a QRhi on the new device (the window got it from device::rebuild).
void deviceRestored()
{
    for (State* s : s_scenes) {
        bool initialized = false;
        onRenderThread(s, [s, &initialized] { initialized = s->renderControl->initialize(); });
        if (!initialized)
            qCWarning(lcOffscreen, "offscreen scene %p: QQuickRenderControl::initialize failed after the device "
                "rebuild, bind retries it", static_cast<void*>(s));
    }
}

// The adapters of the foreign device and of QtNative's device differ: move QtNative's device to the foreign adapter
// if nothing renders on it yet (no on-screen window with a QRhi on it, no other scene bound), else ADAPTER_MISMATCH.
int matchAdapter(State* s, unsigned long long foreignLuid)
{
    const bool otherBound = std::any_of(s_scenes.begin(), s_scenes.end(), [s](const State* o) { return o != s && o->bound; });
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
// wait before Qt's signal deadlocks (render-texture.md, spike 5.1). Unknown (no adapter): software, the safe path.
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

// Main thread; the device calls are free-threaded. Unbind and initialize run on the render thread (drains it), so no
// render of the old binding is in flight when the render target changes.
int bind(State* s, void* device, void* ntHandle, int width, int height, int format, int samples, int* usedSamples)
{
    onRenderThread(s, [s] { unbind(s); });
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
    if (!s->window->rhi() && s->window->property("_vlqt_shared_device").toBool())
        onRenderThread(s, [s] { s->renderControl->initialize(); });
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
    // Layers in the scene stay single-sample (the render control's count, render-texture.md "MSAA").
    const DXGI_FORMAT targetFormat = linearPass.source ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT(format);
    const int used = vlqt::device::clampSamples(samples, int(targetFormat), s->window->rhi());
    const QQuickRenderTarget::Flags msaa = used > 1 ? QQuickRenderTarget::Flag::MultisampleResolve : QQuickRenderTarget::Flags();
    // 1 DIP = 1 texture pixel, also on a high-DPI screen.
    auto target = QQuickRenderTarget::fromD3D11Texture(linearPass.source ? linearPass.source : texture.Get(), uint(targetFormat),
        QSize(width, height), used, msaa);
    target.setDevicePixelRatio(1.0);
    s->window->setGeometry(0, 0, width, height);
    s->window->setRenderTarget(target);

    s->texture = texture.Detach();
    s->linearPass = linearPass;
    s->qtContext = qtContext.Detach();
    s->qtRead = qtRead.Detach();
    s->qtWrite = qtWrite.Detach();
    s->foreignRead = foreignRead.Detach();
    s->foreignWrite = foreignWrite.Detach();
    s->readValue = 0;
    s->writeValue = 0;
    s->renders = 0;
    s->software = isSoftware(qt) || isSoftware(foreign);
    static bool softwareLogged = false;
    if (s->software && s->thread && !std::exchange(softwareLogged, true))
        qCInfo(lcOffscreen, "offscreen bind on a software adapter: render_batch waits for the render thread's submission "
            "(logged once)");
    s->bound = true;
    s->dirty = true;
    if (usedSamples)
        *usedSamples = used;
    return VLQT_OK;
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
    if (s->linearPass.source)
        vlqt::linear_pass::run(s->qtContext, s->linearPass);
    // TODO: GPU time of the frame: perf.cpp afterRendering reads it only from a swap chain; a scene has
    // renderControl->commandBuffer() (and its QRhi needs EnableTimestamps).

    s->qtContext->Signal(s->qtWrite, ++s->writeValue);
    // Submits the frame and the signal; without it the foreign GPU would wait forever.
    s->qtContext->Flush();
    foreign->Wait(s->foreignWrite, s->writeValue);
    s->renders++;
}

// The scenes of one render thread in one batch, with the write value each one signals. Two lists per thread: the
// main thread fills one while the render thread may still render the other (at most one batch is in flight per
// thread, see renderThreaded). Main thread; the render thread reads the list of its jobs.
struct ThreadBatch
{
    render_thread::RenderThread* thread = nullptr;
    std::vector<std::pair<State*, unsigned long long>> lists[2];
    int current = 0;
    bool used = false;     // in the batch being built
    bool software = false; // a scene of it is bound to a software device
};
std::vector<ThreadBatch> s_threadBatches; // one per render thread that ever rendered

// beginFrame (after Qt's GPU waited for the read value) and sync of one scene. Render thread, blocking job.
void beginAndSync(State* s)
{
    try {
        {
            vlqt::device::ContextLock lock(s->qtContext);
            // Before beginFrame: QRhi D3D11 submits the frame's commands to the immediate context only in endFrame.
            s->qtContext->Wait(s->qtRead, s->readValue);
            s->renderControl->beginFrame();
        }
        s->renderControl->sync();
    } catch (const std::exception& e) {
        qCWarning(lcOffscreen, "offscreen scene %p: sync failed: %s", static_cast<void*>(s), e.what());
    } catch (...) {
        qCWarning(lcOffscreen, "offscreen scene %p: sync failed", static_cast<void*>(s));
    }
}

// render, endFrame, then the write value: signalled on every path, the foreign context already waits for it (a value
// never signalled would hang the foreign GPU). Render thread.
void renderAndSignal(State* s, unsigned long long writeValue)
{
    try {
        s->renderControl->render();
    } catch (...) {
        qCWarning(lcOffscreen, "offscreen scene %p: render failed", static_cast<void*>(s));
    }
    vlqt::device::ContextLock lock(s->qtContext);
    try {
        s->renderControl->endFrame();
    } catch (...) {
        qCWarning(lcOffscreen, "offscreen scene %p: endFrame failed", static_cast<void*>(s));
    }
    if (s->linearPass.source)
        vlqt::linear_pass::run(s->qtContext, s->linearPass);
    // TODO: GPU time of the frame (as in render).
    s->qtContext->Signal(s->qtWrite, writeValue);
    // Submits the frame and the signal; without it the foreign GPU would wait forever.
    s->qtContext->Flush();
    s->renders++;
}

// The threaded batch (decision c): per scene the read signal on the foreign context and polish (main thread); one
// flush of the foreign context, so Qt's GPU need not wait for the host's next flush; per render thread one blocking job
// that waits for the read value, begins the frame and syncs its scenes (the main thread waits, items stay untouched);
// the foreign context's GPU wait for each write value (it waits for a value signalled later, its CPU never waits);
// then per render thread one posted job that renders, ends the frame, signals the write value and flushes. The next
// batch's sync job queues behind these renders: a render thread more than a frame behind blocks the main thread
// there (back pressure, at most one frame in flight per scene). Software devices: the job is blocking and the wait
// is queued after it.
void renderThreaded(const std::vector<State*>& scenes, ID3D11DeviceContext4* foreign)
{
    for (State* s : scenes) {
        foreign->Signal(s->foreignRead, ++s->readValue);
        s->renderControl->polishItems();
        // Polish's own changes are in this frame; a change made while syncing or rendering renders next frame.
        s->dirty = false;
    }
    foreign->Flush();

    for (ThreadBatch& b : s_threadBatches)
        b.used = false;
    for (State* s : scenes) {
        auto it = std::find_if(s_threadBatches.begin(), s_threadBatches.end(),
            [s](const ThreadBatch& b) { return b.thread == s->thread; });
        if (it == s_threadBatches.end()) {
            s_threadBatches.emplace_back();
            it = s_threadBatches.end() - 1;
            it->thread = s->thread;
        }
        if (!it->used) {
            it->used = true;
            it->software = false;
            it->current ^= 1;
            it->lists[it->current].clear();
        }
        it->lists[it->current].push_back({ s, ++s->writeValue });
        it->software = it->software || s->software;
    }

    for (ThreadBatch& b : s_threadBatches) {
        if (!b.used)
            continue;
        const auto& list = b.lists[b.current];
        b.thread->call([&list] {
            for (const auto& [s, writeValue] : list)
                beginAndSync(s);
        });
    }
    for (const ThreadBatch& b : s_threadBatches)
        if (b.used && !b.software)
            for (const auto& [s, writeValue] : b.lists[b.current])
                foreign->Wait(s->foreignWrite, writeValue);
    for (ThreadBatch& b : s_threadBatches) {
        if (!b.used)
            continue;
        const auto* list = &b.lists[b.current];
        const auto job = [list] {
            for (const auto& [s, writeValue] : *list)
                renderAndSignal(s, writeValue);
        };
        if (b.software)
            b.thread->call(job);
        else
            b.thread->post(job);
    }
    for (const ThreadBatch& b : s_threadBatches)
        if (b.used && b.software)
            for (const auto& [s, writeValue] : b.lists[b.current])
                foreign->Wait(s->foreignWrite, writeValue);
}

int renderBatch(void* context, vlqt_scene* const* scenes, int count, double driverSeconds)
{
    if (!qGuiApp || QQuickWindow::graphicsApi() != QSGRendererInterface::Direct3D11)
        return fail(VLQT_ERR_UNSUPPORTED, "render_batch: needs Direct3D 11 and an initialized app");
    vlqt_app_advance_animations(driverSeconds);
    ComPtr<ID3D11DeviceContext4> foreign;
    int rendered = 0;
    static std::vector<State*> threaded; // reused: no allocation per frame
    threaded.clear();
    for (int i = 0; i < count; i++) {
        State* s = stateOf(scenes[i]);
        if (!s || !s->bound || !s->dirty)
            continue;
        if (!foreign) {
            HRESULT hr = static_cast<ID3D11DeviceContext*>(context)->QueryInterface(IID_PPV_ARGS(foreign.GetAddressOf()));
            if (FAILED(hr))
                return fail(VLQT_ERR_BAD_ARGUMENT, "render_batch: context is no ID3D11DeviceContext4", hr);
        }
        // Lost but not rebuilt yet: the next pump (device::checkLost) rebuilds and unbinds every scene.
        ID3D11Device* qt = qtDevice(s);
        if (!qt || FAILED(qt->GetDeviceRemovedReason()))
            return fail(VLQT_ERR_DEVICE_LOST, "render_batch: QtNative's device is lost");
        if (s->thread)
            threaded.push_back(s);
        else
            render(s, foreign.Get());
        rendered++;
    }
    if (!threaded.empty())
        renderThreaded(threaded, foreign.Get());
    return rendered;
}

#endif // _WIN32

} // namespace

void release(State* state)
{
#ifdef _WIN32
    onRenderThread(state, [state] {
        unbind(state);
        // The render thread's scene graph and QRhi go there, before the main thread deletes window and render control.
        if (state->thread)
            dropRhi(state);
    });
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

// test hook: vlqt_test_offscreen_state (test/test_probes.cpp).
int testState(vlqt_scene* scene, unsigned long long* readFence, unsigned long long* writeFence, long long* renders)
{
    State* s = stateOf(scene);
    if (!s)
        return vlqt::fail(VLQT_ERR_NULL_HANDLE, "test_offscreen_state: null handle or not an offscreen scene");
    // The renders posted by the last batch are counted.
    if (s->thread)
        s->thread->drain();
    if (!s->bound)
        return vlqt::fail(VLQT_ERR_UNBOUND, "test_offscreen_state: the scene is not bound");
    if (readFence)
        *readFence = s->readValue;
    if (writeFence)
        *writeFence = s->writeValue;
    if (renders)
        *renders = s->renders;
    return VLQT_OK;
}

} // namespace vlqt::offscreen

using namespace vlqt::offscreen;

namespace {

using vlqt::guarded;

// The offscreen part of handle for the export function; nullptr with vlqt_last_error set for a null handle or a window.
State* sceneOrFail(vlqt_scene* handle, const char* function)
{
    State* s = stateOf(handle);
    if (!s)
        vlqt::setLastError(std::string(function) + ": null handle or not an offscreen scene");
    return s;
}

// --- Input (vlqt.h, render-texture.md "Input") ---
// Events go with QCoreApplication::sendEvent to the scene's QQuickWindow, like vlqt_test_send_*: synchronous, so the
// FocusRelease filter, handlers and vlqt_scene_poll see them as window input.

int badArgument(const std::string& what)
{
    return vlqt::fail(VLQT_ERR_BAD_ARGUMENT, "offscreen " + what);
}

// Registered once per scene: QWindowSystemInterface lists it for Qt Quick (handler acceptedDevices); deleted with the
// render control, which unregisters it. The platform's mouse is looked up first, so a scene's mouse never becomes
// QPointingDevice::primaryPointingDevice().
QPointingDevice* newDevice(State* s, const char* name, QInputDevice::DeviceType type, QPointingDevice::PointerType pointer,
    QInputDevice::Capabilities caps, int maxPoints, int buttons)
{
    static qint64 s_systemId = 0x766c716f0000; // "vlqo" + counter: unique, not 0 (= "unknown")
    (void)QPointingDevice::primaryPointingDevice();
    auto* d = new QPointingDevice(QString::fromLatin1(name), ++s_systemId, type, pointer, caps, maxPoints, buttons,
        QString(), QPointingDeviceUniqueId(), s->renderControl);
    QWindowSystemInterface::registerInputDevice(d);
    return d;
}

QPointingDevice* mouseOf(State* s)
{
    if (!s->mouse)
        s->mouse = newDevice(s, "vlqt offscreen mouse", QInputDevice::DeviceType::Mouse, QPointingDevice::PointerType::Generic,
            QInputDevice::Capability::Position | QInputDevice::Capability::Hover | QInputDevice::Capability::Scroll, 1, 5);
    return s->mouse;
}

QPointingDevice* touchscreenOf(State* s)
{
    if (!s->touchscreen)
        s->touchscreen = newDevice(s, "vlqt offscreen touchscreen", QInputDevice::DeviceType::TouchScreen,
            QPointingDevice::PointerType::Finger, QInputDevice::Capability::Position | QInputDevice::Capability::Area, 32, 0);
    return s->touchscreen;
}

// Like a pen in a window on Windows (Qt 6, WM_POINTER): a stylus of pointer type Pen (Devices = Pen of the
// Interaction nodes), one point.
QPointingDevice* penOf(State* s)
{
    if (!s->pen)
        s->pen = newDevice(s, "vlqt offscreen pen", QInputDevice::DeviceType::Stylus, QPointingDevice::PointerType::Pen,
            QInputDevice::Capability::Position | QInputDevice::Capability::Pressure | QInputDevice::Capability::Hover, 1, 3);
    return s->pen;
}

// Monotonic, never 0 (TapHandler compares timestamps).
ulong timestamp(State* s)
{
    if (!s->clock.isValid())
        s->clock.start();
    return ulong(s->clock.elapsed()) + 1;
}

// device: the scene's mouse, or its pen for a mouse event synthesized from an unaccepted tablet event.
void sendMouse(State* s, QEvent::Type type, QPointF pos, Qt::MouseButton button, Qt::MouseButtons buttons,
    Qt::KeyboardModifiers modifiers, ulong ts, const QPointingDevice* device)
{
    QQuickWindow* w = s->window;
    const auto source = device == s->mouse ? Qt::MouseEventNotSynthesized : Qt::MouseEventSynthesizedByQt;
    QMouseEvent ev(type, pos, pos, w->mapToGlobal(pos), button, buttons, modifiers, source, device);
    ev.setTimestamp(ts);
    QCoreApplication::sendEvent(w, &ev);
}

// A press after a press of the same button within the double click interval and distance also sends
// MouseButtonDblClick, as QGuiApplication does for platform input (sendEvent bypasses it). TapHandler counts taps
// itself; MouseArea and the text inputs need the event.
void sendPress(State* s, QPointF pos, Qt::MouseButton button, Qt::MouseButtons buttons, Qt::KeyboardModifiers modifiers, ulong ts,
    const QPointingDevice* device)
{
    const QStyleHints* hints = QGuiApplication::styleHints();
    const QPointF d = pos - s->lastPressPos;
    const bool doubleClick = button == s->lastPressButton && ts - s->lastPressTime < ulong(hints->mouseDoubleClickInterval())
        && std::abs(d.x()) + std::abs(d.y()) <= hints->mouseDoubleClickDistance();
    sendMouse(s, QEvent::MouseButtonPress, pos, button, buttons, modifiers, ts, device);
    s->lastPressButton = doubleClick ? Qt::NoButton : button;
    s->lastPressTime = ts;
    s->lastPressPos = pos;
    if (doubleClick)
        sendMouse(s, QEvent::MouseButtonDblClick, pos, button, buttons, modifiers, ts, device);
}

// One event per changed button, lowest first (the host may report several in one call); a press or release without a
// changed button is an error.
int sendMousePointer(State* s, int type, QPointF pos, Qt::MouseButtons buttons, Qt::KeyboardModifiers modifiers, ulong ts)
{
    switch (type) {
    case VLQT_POINTER_MOVE:
        s->buttons = buttons;
        sendMouse(s, QEvent::MouseMove, pos, Qt::NoButton, buttons, modifiers, ts, mouseOf(s));
        return VLQT_OK;
    case VLQT_POINTER_LEAVE: {
        // Hover ends; Qt Quick clears its hover state.
        QEvent ev(QEvent::Leave);
        QCoreApplication::sendEvent(s->window, &ev);
        return VLQT_OK;
    }
    case VLQT_POINTER_PRESS:
    case VLQT_POINTER_RELEASE: {
        const bool press = type == VLQT_POINTER_PRESS;
        const Qt::MouseButtons changed = press ? buttons & ~s->buttons : s->buttons & ~buttons;
        if (changed == Qt::NoButton)
            return badArgument(std::string("send_pointer: mouse ") + (press ? "press" : "release")
                + " without a changed button (buttons = the buttons held after the event)");
        Qt::MouseButtons held = s->buttons;
        for (unsigned bit = 1; bit != 0 && bit <= unsigned(Qt::MaxMouseButton); bit <<= 1) {
            const auto button = Qt::MouseButton(bit);
            if (!changed.testFlag(button))
                continue;
            held.setFlag(button, press);
            s->buttons = held;
            if (press)
                sendPress(s, pos, button, held, modifiers, ts, mouseOf(s));
            else
                sendMouse(s, QEvent::MouseButtonRelease, pos, button, held, modifiers, ts, mouseOf(s));
        }
        return VLQT_OK;
    }
    default:
        return badArgument("send_pointer: mouse event type " + std::to_string(type) + " unknown");
    }
}

int sendTouchPointer(State* s, int type, int id, QPointF pos, Qt::KeyboardModifiers modifiers, ulong ts)
{
    QPointingDevice* device = touchscreenOf(s);
    auto& active = s->touchPoints;
    QEventPoint::State state;
    switch (type) {
    case VLQT_POINTER_PRESS: state = QEventPoint::State::Pressed; break;
    case VLQT_POINTER_MOVE: state = QEventPoint::State::Updated; break;
    case VLQT_POINTER_RELEASE: state = QEventPoint::State::Released; break;
    case VLQT_POINTER_CANCEL:
        cancelTouch(s->window, device, active, ts, modifiers);
        return VLQT_OK;
    default:
        // LEAVE: a touch has no hover.
        return badArgument("send_pointer: touch event type " + std::to_string(type) + " unknown");
    }
    return sendTouch(s->window, device, active, state, id, pos, ts, modifiers, "offscreen send_pointer")
        ? VLQT_OK
        : VLQT_ERR_BAD_ARGUMENT;
}

// A pen as QGuiApplication delivers one from Qt's Windows plugin: a QTabletEvent from the stylus (pointer handlers with
// Devices = Pen take it); if nobody accepts it, the same as a mouse event synthesized by Qt from the same device
// (MouseArea, Controls, hover), with double clicks. id is ignored: one pen point. Public API only.
int sendPenPointer(State* s, int type, QPointF pos, Qt::KeyboardModifiers modifiers, ulong ts)
{
    QPointingDevice* pen = penOf(s);
    QEvent::Type tablet;
    QEvent::Type mouse;
    Qt::MouseButton button = Qt::NoButton;
    switch (type) {
    case VLQT_POINTER_PRESS:
        if (s->penDown)
            return badArgument("send_pointer: the pen is already pressed");
        tablet = QEvent::TabletPress;
        mouse = QEvent::MouseButtonPress;
        button = Qt::LeftButton;
        s->penDown = true;
        break;
    case VLQT_POINTER_MOVE:
        tablet = QEvent::TabletMove;
        mouse = QEvent::MouseMove;
        break;
    case VLQT_POINTER_RELEASE:
        if (!s->penDown)
            return badArgument("send_pointer: the pen is not pressed");
        tablet = QEvent::TabletRelease;
        mouse = QEvent::MouseButtonRelease;
        button = Qt::LeftButton;
        s->penDown = false;
        break;
    case VLQT_POINTER_LEAVE: {
        // The hovering pen left: as the mouse.
        QEvent ev(QEvent::Leave);
        QCoreApplication::sendEvent(s->window, &ev);
        return VLQT_OK;
    }
    case VLQT_POINTER_CANCEL: {
        // No tablet cancel in Qt: the grabs of the pen point (tablet and synthesized mouse) are dropped, no release.
        if (s->penDown) {
            QTabletEvent ev(QEvent::TabletMove, pen, s->penPos, s->window->mapToGlobal(s->penPos), 0, 0, 0, 0, 0, 0,
                modifiers, Qt::NoButton, Qt::NoButton);
            ev.setExclusiveGrabber(ev.point(0), nullptr);
            ev.clearPassiveGrabbers(ev.point(0));
            s->penDown = false;
        }
        return VLQT_OK;
    }
    default:
        return badArgument("send_pointer: pen event type " + std::to_string(type) + " unknown");
    }
    s->penPos = pos;
    const Qt::MouseButtons buttons = s->penDown ? Qt::LeftButton : Qt::NoButton;
    QTabletEvent ev(tablet, pen, pos, s->window->mapToGlobal(pos), s->penDown ? 1.0 : 0.0, 0, 0, 0, 0, 0, modifiers, button,
        buttons);
    ev.setTimestamp(ts);
    QCoreApplication::sendEvent(s->window, &ev);
    if (!ev.isAccepted() && QCoreApplication::testAttribute(Qt::AA_SynthesizeMouseForUnhandledTabletEvents)) {
        if (mouse == QEvent::MouseButtonPress)
            sendPress(s, pos, button, buttons, modifiers, ts, pen);
        else
            sendMouse(s, mouse, pos, button, buttons, modifiers, ts, pen);
    }
    return VLQT_OK;
}

// FocusIn / FocusOut to the scene's window, as the Qt render control examples do: QQuickWindow gives its content item
// the focus and the active focus item follows (activeFocusOnPress of TextInput works; SceneRenderControlPrivate).
// FocusOut alone keeps the active focus item while no window has the focus (Qt keeps it for the window's return), so
// the focus loss releases it first: a running edit commits. Nothing if unchanged.
void setFocus(State* s, bool focused, Qt::FocusReason reason)
{
    if (s->focused == focused)
        return;
    if (!focused)
        releaseFocus(s->window);
    // Before the event: SceneRenderControlPrivate reads it while QQuickWindow handles FocusIn.
    s->focused = focused;
    QFocusEvent ev(focused ? QEvent::FocusIn : QEvent::FocusOut, reason);
    QCoreApplication::sendEvent(s->window, &ev);
}

int sendPointer(State* s, int device, int type, int id, QPointF pos, int buttons, int modifiers)
{
    const auto mods = Qt::KeyboardModifiers(modifiers);
    const ulong ts = timestamp(s);
    // Like a window: a press inside the scene focuses it, outside unfocuses it, before the press is delivered (so the
    // pressed TextInput takes the active focus). An unknown device changes no focus.
    const bool knownDevice = device == VLQT_POINTER_DEVICE_MOUSE || device == VLQT_POINTER_DEVICE_TOUCH
        || device == VLQT_POINTER_DEVICE_PEN;
    if (type == VLQT_POINTER_PRESS && knownDevice) {
        const QSize size = s->window->size();
        setFocus(s, pos.x() >= 0 && pos.y() >= 0 && pos.x() < size.width() && pos.y() < size.height(), Qt::MouseFocusReason);
    }
    switch (device) {
    case VLQT_POINTER_DEVICE_MOUSE:
        return sendMousePointer(s, type, pos, Qt::MouseButtons(buttons), mods, ts);
    case VLQT_POINTER_DEVICE_TOUCH:
        return sendTouchPointer(s, type, id, pos, mods, ts);
    case VLQT_POINTER_DEVICE_PEN:
        return sendPenPointer(s, type, pos, mods, ts);
    default:
        return badArgument("send_pointer: device " + std::to_string(device) + " unknown");
    }
}

int sendWheel(State* s, QPointF pos, int dx, int dy, int modifiers)
{
    QQuickWindow* w = s->window;
    QWheelEvent ev(pos, w->mapToGlobal(pos), QPoint(), QPoint(dx, dy), s->buttons, Qt::KeyboardModifiers(modifiers),
        Qt::NoScrollPhase, false, Qt::MouseEventNotSynthesized, mouseOf(s));
    ev.setTimestamp(timestamp(s));
    QCoreApplication::sendEvent(w, &ev);
    return VLQT_OK;
}

int sendKey(State* s, int type, int key, int modifiers, bool autoRepeat)
{
    if (type != VLQT_KEY_EVENT_PRESS && type != VLQT_KEY_EVENT_RELEASE)
        return badArgument("send_key: type " + std::to_string(type) + " unknown");
    if (!s->focused)
        return VLQT_OK; // dropped, like keys of a window without focus
    QKeyEvent ev(type == VLQT_KEY_EVENT_PRESS ? QEvent::KeyPress : QEvent::KeyRelease, key, Qt::KeyboardModifiers(modifiers),
        QString(), autoRepeat);
    QCoreApplication::sendEvent(s->window, &ev);
    return VLQT_OK;
}

// Like vlqt_test_send_key_text: a key press/release with the text and Qt::Key_unknown. An item that ignores it but
// takes input method events gets the text as a QInputMethodEvent commit.
int sendText(State* s, const char* utf8, int length)
{
    if (length < 0 || (!utf8 && length > 0))
        return badArgument("send_text: null text or negative length");
    if (length == 0 || !s->focused)
        return VLQT_OK;
    const QString text = QString::fromUtf8(utf8, length);
    QKeyEvent press(QEvent::KeyPress, Qt::Key_unknown, Qt::NoModifier, text);
    QCoreApplication::sendEvent(s->window, &press);
    QKeyEvent release(QEvent::KeyRelease, Qt::Key_unknown, Qt::NoModifier, text);
    QCoreApplication::sendEvent(s->window, &release);
    QQuickItem* focus = s->window->activeFocusItem();
    if (!press.isAccepted() && focus && focus->flags().testFlag(QQuickItem::ItemAcceptsInputMethod)) {
        QInputMethodEvent commit;
        commit.setCommitString(text);
        QCoreApplication::sendEvent(s->window, &commit);
    }
    return VLQT_OK;
}

} // namespace

extern "C" {

VLQT_API vlqt_scene* vlqt_offscreen_create(int width, int height)
{
    vlqt_scene* handle = nullptr;
    guarded([&]() -> int {
        handle = create(width, height); // sets vlqt_last_error on nullptr
        return handle ? VLQT_OK : VLQT_ERR_INTERNAL;
    });
    return handle;
}

VLQT_API int vlqt_offscreen_bind(vlqt_scene* scene, void* device, void* ntHandle, int width, int height, int format, int samples,
    int* usedSamples)
{
    return guarded([&]() -> int {
        if (usedSamples)
            *usedSamples = 0;
        State* state = sceneOrFail(scene, "offscreen bind");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
#ifdef _WIN32
        // bind unbinds first and takes the new binding only after every check: a failure leaves the scene unbound.
        return bind(state, device, ntHandle, width, height, format, samples, usedSamples);
#else
        (void)device;
        (void)ntHandle;
        (void)width;
        (void)height;
        (void)format;
        (void)samples;
        return vlqt::fail(VLQT_ERR_UNSUPPORTED, "offscreen bind: needs Direct3D 11 (Windows)");
#endif
    });
}

VLQT_API int vlqt_offscreen_unbind(vlqt_scene* scene)
{
    return guarded([&]() -> int {
        State* state = sceneOrFail(scene, "offscreen unbind");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
#ifdef _WIN32
        onRenderThread(state, [state] { unbind(state); });
#endif
        return VLQT_OK;
    });
}

VLQT_API int vlqt_offscreen_set_render_threads(int threadCount)
{
    return guarded([&] { return vlqt::render_thread::setThreadCount(threadCount); });
}

VLQT_API int vlqt_offscreen_is_bound(vlqt_scene* scene)
{
    return guarded([&]() -> int {
        State* state = sceneOrFail(scene, "offscreen is_bound");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        return state->bound ? 1 : 0;
    });
}

VLQT_API int vlqt_offscreen_render_batch(void* context, vlqt_scene* const* scenes, int count, double driverSeconds)
{
    return guarded([&]() -> int {
        if (!context || count < 0 || (!scenes && count > 0))
            return vlqt::fail(VLQT_ERR_BAD_ARGUMENT, "offscreen render_batch: null context, null scenes or negative count");
#ifdef _WIN32
        return renderBatch(context, scenes, count, driverSeconds);
#else
        (void)driverSeconds;
        return vlqt::fail(VLQT_ERR_UNSUPPORTED, "offscreen render_batch: needs Direct3D 11 (Windows)");
#endif
    });
}

VLQT_API int vlqt_offscreen_send_pointer(vlqt_scene* scene, int device, int type, int id, float x, float y, int buttons,
                                         int modifiers)
{
    return guarded([&]() -> int {
        State* state = sceneOrFail(scene, "offscreen send_pointer");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        return sendPointer(state, device, type, id, QPointF(x, y), buttons, modifiers);
    });
}

VLQT_API int vlqt_offscreen_send_wheel(vlqt_scene* scene, float x, float y, int dx, int dy, int modifiers)
{
    return guarded([&]() -> int {
        State* state = sceneOrFail(scene, "offscreen send_wheel");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        return sendWheel(state, QPointF(x, y), dx, dy, modifiers);
    });
}

VLQT_API int vlqt_offscreen_send_key(vlqt_scene* scene, int type, int key, int modifiers, int autoRepeat)
{
    return guarded([&]() -> int {
        State* state = sceneOrFail(scene, "offscreen send_key");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        return sendKey(state, type, key, modifiers, autoRepeat != 0);
    });
}

VLQT_API int vlqt_offscreen_send_text(vlqt_scene* scene, const char* utf8, int length)
{
    return guarded([&]() -> int {
        State* state = sceneOrFail(scene, "offscreen send_text");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        return sendText(state, utf8, length);
    });
}

VLQT_API int vlqt_offscreen_set_focus(vlqt_scene* scene, int focused)
{
    return guarded([&]() -> int {
        State* state = sceneOrFail(scene, "offscreen set_focus");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        setFocus(state, focused != 0, Qt::ActiveWindowFocusReason);
        return VLQT_OK;
    });
}

} // extern "C"

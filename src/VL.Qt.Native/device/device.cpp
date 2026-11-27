// Private Qt API: QQuickWindowPrivate::renderControl (which windows are on the shared device); check on Qt updates (list: elements/elements.h).
#include "device/device.h"

#include "device/context_lock.h"

#include <QtCore/QElapsedTimer>
#include <QtCore/QLoggingCategory>
#include <QtGui/QGuiApplication>
#include <QtQuick/QQuickGraphicsDevice>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>
#include <QtQuick/private/qquickwindow_p.h>   // canMove: offscreen windows (renderControl)
#include <rhi/qrhi.h>

#include <algorithm>
#include <unordered_map>
#include <utility>
#include <vector>

#ifdef Q_OS_WIN
#include "test/test_device.h"

#include <d3d11_4.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#endif

namespace vlqt::device {

namespace {

Q_LOGGING_CATEGORY(lcDevice, "vlqt.device")

#ifdef Q_OS_WIN
using Microsoft::WRL::ComPtr;

// Never released at process end (see device.h): these live in objects that are never destroyed, so no static destructor
// releases a device after Qt is gone. Only rebuild replaces them.
ComPtr<ID3D11Device>& s_dev = *new ComPtr<ID3D11Device>;
ComPtr<ID3D11DeviceContext>& s_ctx = *new ComPtr<ID3D11DeviceContext>;
QRhi* s_upload = nullptr;
bool s_attempted = false;   // the first creation ran (ensureDevice); later ones come from rebuild and the retry
bool s_lostReported = false;
// Valid while there is no shared device after a failed creation: checkLost retries it at most every retryMs.
QElapsedTimer s_retry;
constexpr qint64 retryMs = 1000;
// test hook: the D3D12 device and queue under a D3D11On12 device (VLQT_TEST_D3D11ON12=1, test/test_device.h).
test::On12& s_on12 = *new test::On12;
// Old devices after a rebuild: released once no window's QRhi uses them any more (Qt drops the window QRhi at its
// next render), so a window QRhi never points to a released device. Main thread.
struct Retired
{
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    test::On12 on12; // test hook: empty for a plain D3D11 device
};
std::vector<Retired>& s_retired = *new std::vector<Retired>;
#endif
unsigned long long s_requestedLuid = 0; // adapter of moveTo, kept for later rebuilds (chooseAdapter)
unsigned s_generation = 0;           // generation()
std::vector<Listener> s_listeners;   // addListener, main thread
// markOnSharedDevice: windows by whether they are on the shared device; an entry goes with its window. Main thread.
std::unordered_map<const QQuickWindow*, bool> s_onShared;

#ifdef Q_OS_WIN

// The adapter Qt's D3D11 backend would pick: QT_D3D_ADAPTER_INDEX, with QSG_RHI_PREFER_SOFTWARE_RENDERER the first
// software adapter (WARP), else the first one DXGI enumerates. nullptr = let D3D11CreateDevice choose.
ComPtr<IDXGIAdapter1> chooseAdapter()
{
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(factory.GetAddressOf()))))
        return nullptr;
    // moveTo: the requested adapter, found by LUID (also WARP, which EnumAdapters1 may not list).
    if (s_requestedLuid) {
        ComPtr<IDXGIFactory4> factory4;
        ComPtr<IDXGIAdapter1> byLuid;
        LUID luid{};
        luid.LowPart = DWORD(s_requestedLuid & 0xffffffffu);
        luid.HighPart = LONG(s_requestedLuid >> 32);
        if (SUCCEEDED(factory.As(&factory4)))
            factory4->EnumAdapterByLuid(luid, IID_PPV_ARGS(byLuid.GetAddressOf()));
        if (byLuid)
            return byLuid;
        qCWarning(lcDevice, "adapter %08lx:%08lx of the last move not found, choosing as at the start",
            long(luid.HighPart), long(luid.LowPart));
    }
    const int requested = qEnvironmentVariableIsSet("QT_D3D_ADAPTER_INDEX") ? qEnvironmentVariableIntValue("QT_D3D_ADAPTER_INDEX") : -1;
    const bool software = qEnvironmentVariableIntValue("QSG_RHI_PREFER_SOFTWARE_RENDERER") != 0;
    ComPtr<IDXGIAdapter1> chosen;
    ComPtr<IDXGIAdapter1> adapter;
    for (UINT i = 0; !chosen && factory->EnumAdapters1(i, adapter.ReleaseAndGetAddressOf()) != DXGI_ERROR_NOT_FOUND; i++) {
        DXGI_ADAPTER_DESC1 desc{};
        adapter->GetDesc1(&desc);
        const bool match = requested >= 0 ? int(i) == requested
                         : software       ? (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0
                                          : true;
        if (match)
            chosen = std::move(adapter);
    }
    return chosen;
}

void releaseRetired();

// test hook: a D3D11On12 device on the D3D12 singleton found only the removed one (test::dropWindowRhisOn). Drops the
// window QRhis on the retired devices, then releases those.
void dropWindowRhisOnRetired()
{
    std::vector<void*> devices;
    for (const Retired& r : s_retired)
        devices.push_back(r.dev.Get());
    const int dropped = test::dropWindowRhisOn(devices);
    releaseRetired();
    qCInfo(lcDevice, "VLQT_TEST_D3D11ON12: %d window QRhi(s) dropped, %d retired device(s) left", dropped,
        int(s_retired.size()));
}

// Creates the shared device, its context and the upload QRhi. A failure arms the retry (s_retry) and is logged only when
// the attempt before succeeded, not at every retry.
bool createDevice()
{
    if (qEnvironmentVariableIsSet("VLQT_SHARED_DEVICE") && qEnvironmentVariableIntValue("VLQT_SHARED_DEVICE") == 0) {
        qCInfo(lcDevice, "VLQT_SHARED_DEVICE=0: every window creates its own D3D11 device");
        return false;
    }

    ComPtr<IDXGIAdapter1> adapter = chooseAdapter();
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    if (qEnvironmentVariableIntValue("QSG_RHI_DEBUG_LAYER"))
        flags |= D3D11_CREATE_DEVICE_DEBUG;
    D3D_FEATURE_LEVEL level{};
    HRESULT hr = E_FAIL;
    const bool failForTest = test::takeCreationFailure(); // test hook: vlqt_test_device_lost REMOVE_FAIL_ONCE
    if (failForTest)
        qCInfo(lcDevice, "test: this creation of the shared device fails");
    else if (test::isOn12Requested()) { // test hook: the device of vlqt_test_device_lost REMOVE (test/test_device.h)
        hr = test::createOn12(adapter.Get(), flags, s_dev.GetAddressOf(), s_ctx.GetAddressOf(), &level, s_on12);
        if (hr == DXGI_ERROR_DEVICE_REMOVED && !s_retired.empty()) {
            dropWindowRhisOnRetired();
            hr = test::createOn12(adapter.Get(), flags, s_dev.GetAddressOf(), s_ctx.GetAddressOf(), &level, s_on12);
        }
        if (FAILED(hr))
            qCWarning(lcDevice, "VLQT_TEST_D3D11ON12: D3D11On12 device failed (0x%08lx), plain D3D11 device instead",
                long(hr));
    }
    if (FAILED(hr) && !failForTest)
        hr = D3D11CreateDevice(adapter.Get(), adapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
            nullptr, 0, D3D11_SDK_VERSION, s_dev.GetAddressOf(), &level, s_ctx.GetAddressOf());
    QString name;
    if (adapter) {
        DXGI_ADAPTER_DESC1 desc{};
        adapter->GetDesc1(&desc);
        name = QString::fromWCharArray(desc.Description);
        adapter.Reset();
    }
    if (FAILED(hr)) {
        s_dev.Reset();
        s_ctx.Reset();
        if (!s_retry.isValid())
            qCWarning(lcDevice, "D3D11CreateDevice failed (0x%08lx) on '%s': no shared device, retried once per second; "
                "meanwhile windows create their own device, images are not uploaded and offscreen scenes report DEVICE_LOST "
                "(not repeated at every retry)", long(hr), qPrintable(name));
        s_retry.start();
        return false;
    }

    // The render threads of offscreen scenes use this context too (ContextLock); protection serializes single calls.
    ComPtr<ID3D11Multithread> multithread;
    if (SUCCEEDED(s_ctx.As(&multithread))) {
        multithread->SetMultithreadProtected(TRUE);
    } else {
        qCWarning(lcDevice, "the shared device's context has no ID3D11Multithread: offscreen render threads are unsafe");
    }

    QRhiD3D11InitParams params;
    params.enableDebugLayer = (flags & D3D11_CREATE_DEVICE_DEBUG) != 0;
    QRhiD3D11NativeHandles handles;
    handles.dev = s_dev.Get();
    handles.context = s_ctx.Get();
    s_upload = QRhi::create(QRhi::D3D11, &params, QRhi::EnableTimestamps, &handles);
    if (!s_upload)
        qCWarning(lcDevice, "upload QRhi on the shared device could not be created");
    s_generation++;
    s_retry.invalidate();
    qCInfo(lcDevice, "shared D3D11 device %p%s on '%s' (feature level 0x%x), upload QRhi %p, generation %u",
        static_cast<void*>(s_dev.Get()), s_on12.dev12 ? " (D3D11On12, test)" : "", qPrintable(name), unsigned(level),
        static_cast<void*>(s_upload), s_generation);
    return true;
}

// The first creation (attach, uploadRhi); after a failure only checkLost tries again.
bool ensureDevice()
{
    if (s_dev)
        return true;
    if (s_attempted)
        return false;
    s_attempted = true;
    return createDevice();
}

bool isRetired(const void* dev)
{
    return std::any_of(s_retired.begin(), s_retired.end(), [dev](const Retired& r) { return r.dev.Get() == dev; });
}

// Steps 4 and 5 of rebuild, also after a successful retry. Every QQuickWindow whose QRhi is on a retired device imports
// the shared device (an empty one if there is none) and renders: only at that render Qt notices the loss itself and
// creates a new QRhi with the imported device. A window without a QRhi gets the shared device when there is one (else it
// keeps the shared device's mark); a window on a device of its own keeps that until its next loss. Then the listeners
// upload again. Returns the windows handed over.
int handOver()
{
    const bool ok = s_dev.Get() != nullptr;
    int moved = 0;
    for (QWindow* w : QGuiApplication::topLevelWindows()) {
        auto* q = qobject_cast<QQuickWindow*>(w);
        if (!q)
            continue;
        const bool onOld = q->rhi() ? isRetired(nativeDevice(q->rhi())) : ok || isOnSharedDevice(q);
        if (!onOld)
            continue;
        q->setGraphicsDevice(ok ? QQuickGraphicsDevice::fromDeviceAndContext(s_dev.Get(), s_ctx.Get()) : QQuickGraphicsDevice());
        markOnSharedDevice(q, ok);
        q->update();
        moved++;
    }
    for (const Listener& l : s_listeners)
        if (l.restored)
            l.restored();
    return moved;
}

// No shared device after a failed creation: tries again at most every retryMs; on success as rebuild (handOver).
void retryIfDue()
{
    if (!s_retry.isValid() || !s_retry.hasExpired(retryMs))
        return;
    QElapsedTimer t;
    t.start();
    if (!createDevice())
        return;
    const double createMs = t.nsecsElapsed() / 1e6;
    // The listeners run blocking render-thread jobs that take the context lock (device.h, lock order).
    releaseFrameLocks();
    const int moved = handOver();
    qCInfo(lcDevice, "shared device created again in %.1f ms (device %.1f ms, hand-over %.1f ms): generation %u, %d "
        "window(s) handed over to device %p", t.nsecsElapsed() / 1e6, createMs, t.nsecsElapsed() / 1e6 - createMs,
        s_generation, moved, static_cast<void*>(s_dev.Get()));
}

// Remaining references after the last own Release point to a leak (an object of the old device still alive).
void release(Retired& r)
{
    void* const old = r.dev.Get();
    if (r.ctx)
        r.ctx->ClearState();
    r.ctx.Reset();
    const ULONG dev = r.dev.Reset();
    const ULONG dev12 = test::release(r.on12);
    if (dev || dev12)
        qCWarning(lcDevice, "old D3D11 device %p still has %lu reference(s) (D3D12 device %lu): leak", old, dev, dev12);
}

// Releases retired devices that no window's QRhi uses any more. Cheap while nothing is retired.
void releaseRetired()
{
    if (s_retired.empty())
        return;
    const QWindowList windows = QGuiApplication::topLevelWindows();
    for (auto it = s_retired.begin(); it != s_retired.end();) {
        bool used = false;
        for (QWindow* w : windows)
            if (auto* q = qobject_cast<QQuickWindow*>(w); q && nativeDevice(q->rhi()) == it->dev.Get())
                used = true;
        if (used) {
            ++it;
            continue;
        }
        qCInfo(lcDevice, "old D3D11 device %p released", static_cast<void*>(it->dev.Get()));
        release(*it);
        it = s_retired.erase(it);
    }
}
#endif

} // namespace

void attach(QQuickWindow* window)
{
#ifdef Q_OS_WIN
    if (QQuickWindow::graphicsApi() != QSGRendererInterface::Direct3D11 || !ensureDevice())
        return;
    window->setGraphicsDevice(QQuickGraphicsDevice::fromDeviceAndContext(s_dev.Get(), s_ctx.Get()));
    markOnSharedDevice(window, true); // rebuild: also windows without a QRhi yet
#else
    Q_UNUSED(window);
#endif
}

bool isOnSharedDevice(const QQuickWindow* window)
{
    const auto it = s_onShared.find(window);
    return it != s_onShared.end() && it->second;
}

void markOnSharedDevice(QQuickWindow* window, bool on)
{
    const auto [it, added] = s_onShared.try_emplace(window, on);
    if (!added) {
        it->second = on;
        return;
    }
    QObject::connect(window, &QObject::destroyed, [window] { s_onShared.erase(window); });
}

QRhi* uploadRhi()
{
#ifdef Q_OS_WIN
    // Resources may come before the first window: create the device here then.
    if (!s_dev && QQuickWindow::graphicsApi() == QSGRendererInterface::Direct3D11)
        ensureDevice();
    return s_upload;
#else
    return nullptr;
#endif
}

void* nativeDevice(QRhi* rhi)
{
#ifdef Q_OS_WIN
    if (rhi && rhi->backend() == QRhi::D3D11)
        return static_cast<const QRhiD3D11NativeHandles*>(rhi->nativeHandles())->dev;
#else
    Q_UNUSED(rhi);
#endif
    return nullptr;
}

int clampSamples(int requested, int dxgiFormat, QRhi* rhi)
{
    if (requested <= 1)
        return 1;
    if (!rhi)
        rhi = uploadRhi();
    const QList<int> counts = rhi ? rhi->supportedSampleCounts() : QList<int>{1, 2, 4, 8};
#ifdef Q_OS_WIN
    auto* dev = static_cast<ID3D11Device*>(nativeDevice(rhi));
    const auto supports = [dev](int format, int count) {
        UINT levels = 0;
        return SUCCEEDED(dev->CheckMultisampleQualityLevels(DXGI_FORMAT(format), UINT(count), &levels)) && levels > 0;
    };
#else
    Q_UNUSED(dxgiFormat);
#endif
    int best = 1;
    for (int c : counts) {
        if (c <= best || c > requested)
            continue;
#ifdef Q_OS_WIN
        if (dev && (!supports(dxgiFormat, c) || !supports(DXGI_FORMAT_D24_UNORM_S8_UINT, c)))
            continue;
#endif
        best = c;
    }
    return best;
}

int windowSwapChainFormat()
{
#ifdef Q_OS_WIN
    return DXGI_FORMAT_R8G8B8A8_UNORM;
#else
    return 0;
#endif
}

void checkLost()
{
#ifdef Q_OS_WIN
    releaseRetired();
    if (!s_dev) {
        retryIfDue();
        return;
    }
    if (s_lostReported)
        return;
    const HRESULT reason = s_dev->GetDeviceRemovedReason();
    if (reason == S_OK)
        return;
    s_lostReported = true;
    qCWarning(lcDevice, "shared D3D11 device lost (GetDeviceRemovedReason 0x%08lx), rebuilding device, windows and "
        "resources", long(reason));
    rebuild();
#endif
}

unsigned long long adapterLuidOf(void* d3d11Device)
{
#ifdef Q_OS_WIN
    if (!d3d11Device)
        return 0;
    ComPtr<IDXGIDevice> dxgi;
    if (FAILED(static_cast<IUnknown*>(d3d11Device)->QueryInterface(IID_PPV_ARGS(dxgi.GetAddressOf()))))
        return 0;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC desc{};
    const bool ok = SUCCEEDED(dxgi->GetAdapter(adapter.GetAddressOf())) && SUCCEEDED(adapter->GetDesc(&desc));
    return ok ? (static_cast<unsigned long long>(static_cast<DWORD>(desc.AdapterLuid.HighPart)) << 32)
            | desc.AdapterLuid.LowPart : 0;
#else
    Q_UNUSED(d3d11Device);
    return 0;
#endif
}

unsigned long long adapterLuid()
{
#ifdef Q_OS_WIN
    return adapterLuidOf(s_dev.Get());
#else
    return 0;
#endif
}

bool canMove()
{
#ifdef Q_OS_WIN
    if (!s_dev)
        return false;
    for (QWindow* w : QGuiApplication::topLevelWindows()) {
        auto* q = qobject_cast<QQuickWindow*>(w);
        if (q && !QQuickWindowPrivate::get(q)->renderControl && nativeDevice(q->rhi()) == s_dev.Get())
            return false;
    }
    return true;
#else
    return false;
#endif
}

bool moveTo(unsigned long long luid)
{
#ifdef Q_OS_WIN
    if (!luid || !canMove())
        return false;
    const unsigned long long from = adapterLuid();
    s_requestedLuid = luid;
    qCInfo(lcDevice, "moving the shared device from adapter %016llx to %016llx (no window renders on it yet)", from, luid);
    rebuild();
    return adapterLuid() == luid;
#else
    Q_UNUSED(luid);
    return false;
#endif
}

unsigned generation()
{
    return s_generation;
}

void rebuild()
{
#ifdef Q_OS_WIN
    QElapsedTimer t;
    t.start();
    // The listeners run blocking render-thread jobs that take the context lock (device.h, lock order).
    releaseFrameLocks();

    // 1. Listeners free every object of the old device (textures, staging, QRhi resources), keep their sources.
    for (const Listener& l : s_listeners)
        if (l.lost)
            l.lost();

    // 2. Upload QRhi after its resources, then the device. The window QRhis still use the device until Qt drops them
    // at their next render: retire it, releaseRetired frees it afterwards.
    delete s_upload;
    s_upload = nullptr;
    if (s_dev)
        s_retired.push_back({ std::move(s_dev), std::move(s_ctx), std::move(s_on12) }); // leaves the three empty
    s_on12 = {};

    // 3. New device (generation + 1; D3D11On12 again with VLQT_TEST_D3D11ON12). A failure arms the retry (checkLost).
    const bool ok = QQuickWindow::graphicsApi() == QSGRendererInterface::Direct3D11 && createDevice();

    // 4. Windows on the old device import the new one (or their own, if there is none); 5. listeners upload again.
    const int moved = handOver();

    // 6. A further loss is noticed again.
    s_lostReported = false;
    if (ok)
        qCInfo(lcDevice, "device rebuilt in %.1f ms: generation %u, %d window(s) moved to device %p",
            t.nsecsElapsed() / 1e6, s_generation, moved, static_cast<void*>(s_dev.Get()));
    else
        qCWarning(lcDevice, "no new shared device after the loss (retried once per second): %d window(s) create their own "
            "device and keep it until their next loss", moved);
#endif
}

void addListener(Listener listener)
{
    s_listeners.push_back(listener);
}

void* testDevice12()
{
#ifdef Q_OS_WIN
    return s_on12.dev12.Get();
#else
    return nullptr;
#endif
}

void* sharedContext()
{
#ifdef Q_OS_WIN
    return s_ctx.Get();
#else
    return nullptr;
#endif
}

} // namespace vlqt::device
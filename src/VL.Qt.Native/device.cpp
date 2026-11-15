#include "device.h"

#include "protocol.h"
#include "render_thread.h"

#include <QtCore/QElapsedTimer>
#include <QtCore/QLoggingCategory>
#include <QtGui/QGuiApplication>
#include <QtQuick/QQuickGraphicsDevice>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>
#include <QtQuick/private/qquickwindow_p.h>   // canMove: offscreen windows (renderControl)
#include <QtQuick/private/qsgrenderloop_p.h> // test only: dropWindowRhisOnRetired
#include <rhi/qrhi.h>

#include <algorithm>
#include <optional>
#include <vector>

#ifdef Q_OS_WIN
#include <d3d11_4.h>
#include <d3d11on12.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#endif

namespace vlqt::device {

namespace {

Q_LOGGING_CATEGORY(lcDevice, "vlqt.device")

#ifdef Q_OS_WIN
// Never released at process end (see device.h); only rebuild replaces them.
ID3D11Device* s_dev = nullptr;
ID3D11DeviceContext* s_ctx = nullptr;
QRhi* s_upload = nullptr;
bool s_tried = false;
bool s_lostReported = false;
// Test device (VLQT_TEST_D3D11ON12=1): the D3D12 device and queue under the 11on12 device, for testLost REMOVE.
ID3D12Device* s_dev12 = nullptr;
ID3D12CommandQueue* s_queue12 = nullptr;
// Old devices after a rebuild: released once no window's QRhi uses them any more (Qt drops the window QRhi at its
// next render), so a window QRhi never points to a released device. Main thread.
struct Retired
{
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    ID3D12Device* dev12 = nullptr;
    ID3D12CommandQueue* queue12 = nullptr;
};
std::vector<Retired> s_retired;
#endif
unsigned long long s_requestedLuid = 0; // adapter of moveTo, kept for later rebuilds (chooseAdapter)
unsigned s_generation = 0;           // generation()
std::vector<Listener> s_listeners;   // addListener, main thread

// lockFrames: holds the shared context from beforeRendering to afterFrameEnd of an on-screen window's frame (basic
// loop, main thread); releaseFrameLocks ends every one still held. Child of its window.
class FrameLock final : public QObject
{
public:
    explicit FrameLock(QQuickWindow* window) : QObject(window)
    {
        s_all.push_back(this);
        connect(window, &QQuickWindow::beforeRendering, this, [this] {
            if (!m_lock)
                m_lock.emplace();
        }, Qt::DirectConnection);
        connect(window, &QQuickWindow::afterFrameEnd, this, &FrameLock::release, Qt::DirectConnection);
    }
    ~FrameLock() override { s_all.erase(std::remove(s_all.begin(), s_all.end(), this), s_all.end()); }
    void release() { m_lock.reset(); }
    static void releaseAll()
    {
        for (FrameLock* l : s_all)
            l->release();
    }
private:
    std::optional<ContextLock> m_lock;
    static inline std::vector<FrameLock*> s_all; // main thread
};
#ifdef Q_OS_WIN

// The adapter Qt's D3D11 backend would pick: QT_D3D_ADAPTER_INDEX, with QSG_RHI_PREFER_SOFTWARE_RENDERER the first
// software adapter (WARP), else the first one DXGI enumerates. nullptr = let D3D11CreateDevice choose.
IDXGIAdapter1* chooseAdapter()
{
    IDXGIFactory1* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory))))
        return nullptr;
    // moveTo: the requested adapter, found by LUID (also WARP, which EnumAdapters1 may not list).
    if (s_requestedLuid) {
        IDXGIFactory4* factory4 = nullptr;
        IDXGIAdapter1* byLuid = nullptr;
        LUID luid{};
        luid.LowPart = DWORD(s_requestedLuid & 0xffffffffu);
        luid.HighPart = LONG(s_requestedLuid >> 32);
        if (SUCCEEDED(factory->QueryInterface(__uuidof(IDXGIFactory4), reinterpret_cast<void**>(&factory4)))) {
            factory4->EnumAdapterByLuid(luid, __uuidof(IDXGIAdapter1), reinterpret_cast<void**>(&byLuid));
            factory4->Release();
        }
        if (byLuid) {
            factory->Release();
            return byLuid;
        }
        qCWarning(lcDevice, "adapter %08lx:%08lx of the last move not found, choosing as at the start",
            long(luid.HighPart), long(luid.LowPart));
    }
    const int requested = qEnvironmentVariableIsSet("QT_D3D_ADAPTER_INDEX") ? qEnvironmentVariableIntValue("QT_D3D_ADAPTER_INDEX") : -1;
    const bool software = qEnvironmentVariableIntValue("QSG_RHI_PREFER_SOFTWARE_RENDERER") != 0;
    IDXGIAdapter1* chosen = nullptr;
    IDXGIAdapter1* adapter = nullptr;
    for (UINT i = 0; !chosen && factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; i++) {
        DXGI_ADAPTER_DESC1 desc{};
        adapter->GetDesc1(&desc);
        const bool match = requested >= 0 ? int(i) == requested
                         : software       ? (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0
                                          : true;
        if (match)
            chosen = adapter;
        else
            adapter->Release();
    }
    factory->Release();
    return chosen;
}

bool testD3D11On12()
{
    wchar_t v[4] = {};
    return GetEnvironmentVariableW(L"VLQT_TEST_D3D11ON12", v, 4) == 1 && v[0] == L'1';
}

// Test only: D3D12 device + direct queue on the adapter, D3D11 device on top (D3D11On12), so testLost can remove it
// for real (ID3D12Device5::RemoveDevice). d3d12.dll is loaded dynamically: no link dependency in the shipped QtNative.
HRESULT createD3D11On12(IDXGIAdapter1* adapter, UINT flags, D3D_FEATURE_LEVEL* level)
{
    // CLSID_D3D12DeviceFactory (d3d12.h), local copy: no dxguid/d3d12 import library.
    static constexpr GUID clsidDeviceFactory = { 0x114863bf, 0xc386, 0x4aee,
        { 0xb3, 0x9d, 0x8f, 0x0b, 0xbb, 0x06, 0x29, 0x55 } };
    static const HMODULE d3d12 = LoadLibraryW(L"d3d12.dll");
    if (!d3d12)
        return E_NOTIMPL;
    const auto create12 = reinterpret_cast<PFN_D3D12_CREATE_DEVICE>(GetProcAddress(d3d12, "D3D12CreateDevice"));
    const auto getInterface = reinterpret_cast<PFN_D3D12_GET_INTERFACE>(GetProcAddress(d3d12, "D3D12GetInterface"));
    const HMODULE d3d11 = GetModuleHandleW(L"d3d11.dll");
    const auto create11on12 = d3d11
        ? reinterpret_cast<PFN_D3D11ON12_CREATE_DEVICE>(GetProcAddress(d3d11, "D3D11On12CreateDevice"))
        : nullptr;
    if (!create11on12)
        return E_NOTIMPL;
    // D3D12 devices are singletons per adapter: after RemoveDevice, D3D12CreateDevice returns the removed one as long as
    // a window QRhi still holds it. A device factory that stores no singleton creates a fresh device every time.
    // Without the factory (before Windows 11 24H2, e.g. Windows Server 2022 on the CI runner) only the singleton.
    HRESULT hr = E_NOTIMPL;
    ID3D12DeviceFactory* factory = nullptr;
    if (getInterface && SUCCEEDED(getInterface(clsidDeviceFactory, __uuidof(ID3D12DeviceFactory),
            reinterpret_cast<void**>(&factory)))) {
        factory->SetFlags(D3D12_DEVICE_FACTORY_FLAG_DISALLOW_STORING_NEW_DEVICE_AS_SINGLETON);
        hr = factory->CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device),
            reinterpret_cast<void**>(&s_dev12));
        factory->Release();
        if (FAILED(hr))
            qCWarning(lcDevice, "VLQT_TEST_D3D11ON12: ID3D12DeviceFactory::CreateDevice failed (0x%08lx)", long(hr));
    }
    const char* path = SUCCEEDED(hr) ? "device factory" : "D3D12CreateDevice (singleton)";
    if (FAILED(hr) && create12)
        hr = create12(adapter, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), reinterpret_cast<void**>(&s_dev12));
    if (SUCCEEDED(hr)) {
        // A removed singleton comes back as a valid pointer: only its removed reason tells.
        const HRESULT removed = s_dev12->GetDeviceRemovedReason();
        if (FAILED(removed))
            hr = removed;
    }
    if (SUCCEEDED(hr)) {
        D3D12_COMMAND_QUEUE_DESC desc{};
        desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        hr = s_dev12->CreateCommandQueue(&desc, __uuidof(ID3D12CommandQueue), reinterpret_cast<void**>(&s_queue12));
    }
    if (SUCCEEDED(hr)) {
        IUnknown* queues[] = { s_queue12 };
        hr = create11on12(s_dev12, flags, nullptr, 0, queues, 1, 0, &s_dev, &s_ctx, level);
    }
    qCInfo(lcDevice, "VLQT_TEST_D3D11ON12: D3D12 device %p via %s: 0x%08lx", static_cast<void*>(s_dev12), path,
        long(hr));
    if (FAILED(hr)) {
        if (s_queue12)
            s_queue12->Release();
        if (s_dev12)
            s_dev12->Release();
        s_queue12 = nullptr;
        s_dev12 = nullptr;
    }
    return hr;
}

void releaseRetired();

// Test only, D3D12 singleton (no device factory): the removed D3D12 device lives on as long as a window QRhi holds it,
// so a new D3D11On12 device would sit on the removed one. Let each window on a retired device render now (past the
// update gate): Qt notices the loss at Present and drops its QRhi, then the retired device is released. The windows
// get no device until rebuild imports the new one; an empty one meanwhile is safer than the released pointer.
void dropWindowRhisOnRetired()
{
    QSGRenderLoop* const loop = QSGRenderLoop::instance();
    int dropped = 0;
    for (QWindow* w : QGuiApplication::topLevelWindows()) {
        auto* q = qobject_cast<QQuickWindow*>(w);
        if (!q || !q->rhi())
            continue;
        void* const dev = nativeDevice(q->rhi());
        for (const Retired& r : s_retired) {
            if (r.dev != dev)
                continue;
            q->setGraphicsDevice(QQuickGraphicsDevice());
            q->update(); // the basic loop presents (and so notices the loss) only with a pending update
            loop->handleUpdateRequest(q);
            if (!q->rhi())
                dropped++;
            break;
        }
    }
    releaseRetired();
    qCInfo(lcDevice, "VLQT_TEST_D3D11ON12: %d window QRhi(s) dropped, %d retired device(s) left", dropped,
        int(s_retired.size()));
}

bool ensureDevice()
{
    if (s_tried)
        return s_dev != nullptr;
    s_tried = true;
    if (qEnvironmentVariableIsSet("VLQT_SHARED_DEVICE") && qEnvironmentVariableIntValue("VLQT_SHARED_DEVICE") == 0) {
        qCInfo(lcDevice, "VLQT_SHARED_DEVICE=0: every window creates its own D3D11 device");
        return false;
    }

    IDXGIAdapter1* adapter = chooseAdapter();
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    if (qEnvironmentVariableIntValue("QSG_RHI_DEBUG_LAYER"))
        flags |= D3D11_CREATE_DEVICE_DEBUG;
    D3D_FEATURE_LEVEL level{};
    HRESULT hr = E_FAIL;
    if (testD3D11On12()) {
        hr = createD3D11On12(adapter, flags, &level);
        if (hr == DXGI_ERROR_DEVICE_REMOVED && !s_retired.empty()) {
            dropWindowRhisOnRetired();
            hr = createD3D11On12(adapter, flags, &level);
        }
        if (FAILED(hr))
            qCWarning(lcDevice, "VLQT_TEST_D3D11ON12: D3D11On12 device failed (0x%08lx), plain D3D11 device instead",
                long(hr));
    }
    if (FAILED(hr))
        hr = D3D11CreateDevice(adapter, adapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
            nullptr, 0, D3D11_SDK_VERSION, &s_dev, &level, &s_ctx);
    QString name;
    if (adapter) {
        DXGI_ADAPTER_DESC1 desc{};
        adapter->GetDesc1(&desc);
        name = QString::fromWCharArray(desc.Description);
        adapter->Release();
    }
    if (FAILED(hr)) {
        s_dev = nullptr;
        s_ctx = nullptr;
        qCWarning(lcDevice, "D3D11CreateDevice failed (0x%08lx) on '%s': every window creates its own device", long(hr),
            qPrintable(name));
        return false;
    }

    // The render threads of offscreen scenes use this context too (ContextLock); protection serializes single calls.
    ID3D11Multithread* multithread = nullptr;
    if (SUCCEEDED(s_ctx->QueryInterface(__uuidof(ID3D11Multithread), reinterpret_cast<void**>(&multithread)))) {
        multithread->SetMultithreadProtected(TRUE);
        multithread->Release();
    } else {
        qCWarning(lcDevice, "the shared device's context has no ID3D11Multithread: offscreen render threads are unsafe");
    }

    QRhiD3D11InitParams params;
    params.enableDebugLayer = (flags & D3D11_CREATE_DEVICE_DEBUG) != 0;
    QRhiD3D11NativeHandles handles;
    handles.dev = s_dev;
    handles.context = s_ctx;
    s_upload = QRhi::create(QRhi::D3D11, &params, QRhi::EnableTimestamps, &handles);
    if (!s_upload)
        qCWarning(lcDevice, "upload QRhi on the shared device could not be created");
    s_generation++;
    qCInfo(lcDevice, "shared D3D11 device %p%s on '%s' (feature level 0x%x), upload QRhi %p, generation %u",
        static_cast<void*>(s_dev), s_dev12 ? " (D3D11On12, test)" : "", qPrintable(name), unsigned(level),
        static_cast<void*>(s_upload), s_generation);
    return true;
}

// Remaining references after the last own Release point to a leak (an object of the old device still alive).
void release(Retired& r)
{
    if (r.ctx) {
        r.ctx->ClearState();
        r.ctx->Release();
    }
    const ULONG dev = r.dev ? r.dev->Release() : 0;
    if (r.queue12)
        r.queue12->Release();
    const ULONG dev12 = r.dev12 ? r.dev12->Release() : 0;
    if (dev || dev12)
        qCWarning(lcDevice, "old D3D11 device %p still has %lu reference(s) (D3D12 device %lu): leak",
            static_cast<void*>(r.dev), dev, dev12);
    r = {};
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
            if (auto* q = qobject_cast<QQuickWindow*>(w); q && nativeDevice(q->rhi()) == it->dev)
                used = true;
        if (used) {
            ++it;
            continue;
        }
        qCInfo(lcDevice, "old D3D11 device %p released", static_cast<void*>(it->dev));
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
    window->setGraphicsDevice(QQuickGraphicsDevice::fromDeviceAndContext(s_dev, s_ctx));
    window->setProperty("_vlqt_shared_device", true); // rebuild: also windows without a QRhi yet
#else
    Q_UNUSED(window);
#endif
}

void lockFrames(QQuickWindow* window)
{
    new FrameLock(window);
}

void releaseFrameLocks()
{
    FrameLock::releaseAll();
}

QRhi* uploadRhi()
{
#ifdef Q_OS_WIN
    // Resources may come before the first window (#360): create the device here then.
    if (!s_tried && QQuickWindow::graphicsApi() == QSGRendererInterface::Direct3D11)
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

void checkLost()
{
#ifdef Q_OS_WIN
    releaseRetired();
    if (!s_dev || s_lostReported)
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
    IDXGIDevice* dxgi = nullptr;
    if (FAILED(static_cast<IUnknown*>(d3d11Device)->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgi))))
        return 0;
    IDXGIAdapter* adapter = nullptr;
    DXGI_ADAPTER_DESC desc{};
    const bool ok = SUCCEEDED(dxgi->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&desc));
    if (adapter)
        adapter->Release();
    dxgi->Release();
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
    return adapterLuidOf(s_dev);
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
        if (q && !QQuickWindowPrivate::get(q)->renderControl && nativeDevice(q->rhi()) == s_dev)
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
    ID3D11Device* const old = s_dev;
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
        s_retired.push_back({ s_dev, s_ctx, s_dev12, s_queue12 });
    s_dev = nullptr;
    s_ctx = nullptr;
    s_dev12 = nullptr;
    s_queue12 = nullptr;

    // 3. New device (generation + 1; D3D11On12 again with VLQT_TEST_D3D11ON12).
    s_tried = false;
    const bool ok = QQuickWindow::graphicsApi() == QSGRendererInterface::Direct3D11 && ensureDevice();

    // 4. Every QQuickWindow on the old device imports the new one (or its own, if there is none) and renders: only at
    // that render Qt notices the loss itself and creates a new QRhi with the imported device.
    int moved = 0;
    for (QWindow* w : QGuiApplication::topLevelWindows()) {
        auto* q = qobject_cast<QQuickWindow*>(w);
        if (!q)
            continue;
        const bool onOld = q->rhi() ? nativeDevice(q->rhi()) == old : q->property("_vlqt_shared_device").toBool();
        if (!onOld)
            continue;
        q->setGraphicsDevice(ok ? QQuickGraphicsDevice::fromDeviceAndContext(s_dev, s_ctx) : QQuickGraphicsDevice());
        q->setProperty("_vlqt_shared_device", ok);
        q->update();
        moved++;
    }

    // 5. Listeners upload again on the new device.
    for (const Listener& l : s_listeners)
        if (l.restored)
            l.restored();

    // 6. A further loss is noticed again.
    s_lostReported = false;
    if (ok)
        qCInfo(lcDevice, "device rebuilt in %.1f ms: generation %u, %d window(s) moved to device %p",
            t.nsecsElapsed() / 1e6, s_generation, moved, static_cast<void*>(s_dev));
    else
        qCWarning(lcDevice, "no new shared device after the loss: %d window(s) create their own device", moved);
#endif
}

void addListener(Listener listener)
{
    s_listeners.push_back(listener);
}

int testLost(int mode)
{
    if (mode != VLQT_DEVICE_LOST_REMOVE && mode != VLQT_DEVICE_LOST_HANG)
        return -7;
#ifdef Q_OS_WIN
    if (mode == VLQT_DEVICE_LOST_HANG) {
        // Not implemented: a TDR resets the whole GPU (every application). Manual check: dxcap -forcetdr (admin).
        qCWarning(lcDevice, "vlqt_test_device_lost(HANG) not implemented, use 'dxcap -forcetdr' for a real TDR");
        return -2;
    }
    ID3D12Device5* dev5 = nullptr;
    if (!s_dev12 || FAILED(s_dev12->QueryInterface(__uuidof(ID3D12Device5), reinterpret_cast<void**>(&dev5)))) {
        qCWarning(lcDevice, "vlqt_test_device_lost(REMOVE) needs the shared device on D3D11On12 "
            "(VLQT_TEST_D3D11ON12=1 before the first window or resource)");
        return -2;
    }
    // The device goes between two frames, not under a render job a render_batch just posted: D3D11On12 then keeps an
    // acquire of the scene's shared texture in its batch without a fence, and destroying the old device (releaseRetired)
    // runs it as ID3D12CommandQueue::Wait(nullptr) - an access violation in D3D12Core (T6, 1 in ~10 threaded runs).
    render_thread::drainAll();
    qCInfo(lcDevice, "test: removing the shared device %p", static_cast<void*>(s_dev));
    dev5->RemoveDevice();
    dev5->Release();
    return 0;
#else
    return -2;
#endif
}

ContextLock::ContextLock()
#ifdef Q_OS_WIN
    : ContextLock(s_ctx)
#endif
{
}

ContextLock::ContextLock(void* d3d11Context)
{
#ifdef Q_OS_WIN
    auto* const context = static_cast<ID3D11DeviceContext*>(d3d11Context);
    ID3D11Multithread* multithread = nullptr;
    if (context && SUCCEEDED(context->QueryInterface(__uuidof(ID3D11Multithread), reinterpret_cast<void**>(&multithread)))) {
        multithread->Enter();
        m_multithread = multithread;
    }
#else
    Q_UNUSED(d3d11Context);
#endif
}

ContextLock::~ContextLock()
{
#ifdef Q_OS_WIN
    if (auto* const multithread = static_cast<ID3D11Multithread*>(m_multithread)) {
        multithread->Leave();
        multithread->Release();
    }
#endif
}

} // namespace vlqt::device

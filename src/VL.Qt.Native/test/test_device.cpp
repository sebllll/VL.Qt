// Test device of vlqt_test_device_lost (test_device.h), Windows only.
#include "test_device.h"

#include "../app/last_error.h"
#include "../device.h"
#include "../vlqt.h"

#include <QtCore/QLoggingCategory>
#include <QtGui/QGuiApplication>
#include <QtQuick/QQuickGraphicsDevice>
#include <QtQuick/QQuickWindow>
#include <QtQuick/private/qsgrenderloop_p.h> // dropWindowRhisOn: private API, check on Qt updates

#include <d3d11on12.h>
#include <d3d12.h>

namespace vlqt::device::test {

namespace {

Q_LOGGING_CATEGORY(lcDevice, "vlqt.device")

} // namespace

bool isOn12Requested()
{
    wchar_t v[4] = {};
    return GetEnvironmentVariableW(L"VLQT_TEST_D3D11ON12", v, 4) == 1 && v[0] == L'1';
}

HRESULT createOn12(IDXGIAdapter1* adapter, UINT flags, ID3D11Device** dev, ID3D11DeviceContext** ctx,
    D3D_FEATURE_LEVEL* level, On12& out)
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
            reinterpret_cast<void**>(&out.dev12));
        factory->Release();
        if (FAILED(hr))
            qCWarning(lcDevice, "VLQT_TEST_D3D11ON12: ID3D12DeviceFactory::CreateDevice failed (0x%08lx)", long(hr));
    }
    const char* path = SUCCEEDED(hr) ? "device factory" : "D3D12CreateDevice (singleton)";
    if (FAILED(hr) && create12)
        hr = create12(adapter, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), reinterpret_cast<void**>(&out.dev12));
    if (SUCCEEDED(hr)) {
        // A removed singleton comes back as a valid pointer: only its removed reason tells.
        const HRESULT removed = out.dev12->GetDeviceRemovedReason();
        if (FAILED(removed))
            hr = removed;
    }
    if (SUCCEEDED(hr)) {
        D3D12_COMMAND_QUEUE_DESC desc{};
        desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        hr = out.dev12->CreateCommandQueue(&desc, __uuidof(ID3D12CommandQueue), reinterpret_cast<void**>(&out.queue12));
    }
    if (SUCCEEDED(hr)) {
        IUnknown* queues[] = { out.queue12 };
        hr = create11on12(out.dev12, flags, nullptr, 0, queues, 1, 0, dev, ctx, level);
    }
    qCInfo(lcDevice, "VLQT_TEST_D3D11ON12: D3D12 device %p via %s: 0x%08lx", static_cast<void*>(out.dev12), path,
        long(hr));
    if (FAILED(hr))
        release(out);
    return hr;
}

int dropWindowRhisOn(const std::vector<void*>& devices)
{
    QSGRenderLoop* const loop = QSGRenderLoop::instance();
    int dropped = 0;
    for (QWindow* w : QGuiApplication::topLevelWindows()) {
        auto* q = qobject_cast<QQuickWindow*>(w);
        if (!q || !q->rhi())
            continue;
        void* const dev = nativeDevice(q->rhi());
        for (void* d : devices) {
            if (d != dev)
                continue;
            q->setGraphicsDevice(QQuickGraphicsDevice());
            q->update(); // the basic loop presents (and so notices the loss) only with a pending update
            loop->handleUpdateRequest(q);
            if (!q->rhi())
                dropped++;
            break;
        }
    }
    return dropped;
}

unsigned long release(On12& on12)
{
    if (on12.queue12)
        on12.queue12->Release();
    const unsigned long left = on12.dev12 ? on12.dev12->Release() : 0;
    on12 = {};
    return left;
}

int removeDevice(ID3D12Device* dev12)
{
    ID3D12Device5* dev5 = nullptr;
    if (!dev12 || FAILED(dev12->QueryInterface(__uuidof(ID3D12Device5), reinterpret_cast<void**>(&dev5)))) {
        return vlqt::fail(VLQT_ERR_UNSUPPORTED, "test_device_lost(REMOVE) needs the shared device on D3D11On12 "
                                                "(VLQT_TEST_D3D11ON12=1 before the first window or resource)");
    }
    qCInfo(lcDevice, "test: removing the D3D12 device %p of the shared device", static_cast<void*>(dev12));
    dev5->RemoveDevice();
    dev5->Release();
    return 0;
}

} // namespace vlqt::device::test

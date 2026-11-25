#include "device/renderer_info.h"

#include <QtCore/QLoggingCategory>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>

#include <atomic>

#ifdef Q_OS_WIN
#include <dxgi1_5.h>
#endif

Q_LOGGING_CATEGORY(lcGpu, "vlqt.gpu")

namespace vlqt {
namespace {

std::atomic<bool> s_softwareWarned{false}; // once per process

#ifdef Q_OS_WIN
// Same query as QRhiD3D11::create(): IDXGIFactory5::CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING),
// qtbase v6.12.0 src/gui/rhi/qrhid3d11.cpp:211-219 (supportsAllowTearing). Asked once per process.
bool dxgiAllowTearingSupported()
{
    static const bool supported = [] {
        bool result = false;
        IDXGIFactory1* factory = nullptr;
        if (SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory)))) {
            IDXGIFactory5* factory5 = nullptr;
            if (SUCCEEDED(factory->QueryInterface(__uuidof(IDXGIFactory5), reinterpret_cast<void**>(&factory5)))) {
                BOOL allowTearing = FALSE;
                if (SUCCEEDED(factory5->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &allowTearing, sizeof(allowTearing))))
                    result = allowTearing != FALSE;
                factory5->Release();
            }
            factory->Release();
        }
        return result;
    }();
    return supported;
}
#endif

// The software name of a window that has a QRhi, empty for a hardware adapter.
QString softwareAdapterName(const QRhiDriverInfo& info)
{
    const QString name = QString::fromUtf8(info.deviceName);
    static const char* const kSoftware[] = {"llvmpipe", "softpipe", "Software Rasterizer", "SwiftShader"};
    bool sw = info.deviceType == QRhiDriverInfo::CpuDevice;
    for (const char* s : kSoftware)
        sw = sw || name.contains(QLatin1String(s), Qt::CaseInsensitive);
    return sw ? (name.isEmpty() ? QStringLiteral("CPU device") : name) : QString();
}

} // namespace

bool isSoftwareBackend(QQuickWindow* window)
{
    if (QQuickWindow::sceneGraphBackend() == QLatin1String("software"))
        return true;
    const QSGRendererInterface* ri = window ? window->rendererInterface() : nullptr;
    return ri && ri->graphicsApi() == QSGRendererInterface::Software;
}

RendererInfo describeRenderer(QQuickWindow* window)
{
    RendererInfo r;
    if (!window)
        return r;
    r.quickSoftware = isSoftwareBackend(window);
    QRhi* rhi = window->rhi();
    if (rhi) {
        const QRhiDriverInfo info = rhi->driverInfo();
        r.hasRhi = true;
        r.backend = rhi->backend();
        r.backendName = QString::fromLatin1(rhi->backendName());
        r.adapter = QString::fromUtf8(info.deviceName);
        r.deviceType = info.deviceType;
    }
    if (r.quickSoftware)
        r.software = QStringLiteral("Qt Quick software backend");
    else if (rhi)
        r.software = softwareAdapterName(rhi->driverInfo());
#ifdef Q_OS_WIN
    if (r.hasRhi && r.backend == QRhi::D3D11)
        r.allowTearingSupported = dxgiAllowTearingSupported();
#endif
    return r;
}

void warnSoftwareRendering(const RendererInfo& info)
{
    if (info.software.isEmpty() || s_softwareWarned.exchange(true))
        return;
    // GPU rendering is required (software rendering is not supported): warn loudly when Qt fell back to the CPU.
    qCWarning(lcGpu, "SOFTWARE RENDERING: Qt Quick renders on the CPU (%s), not on the GPU. Expect low fps. "
        "WSLg: GALLIUM_DRIVER=d3d12 selects the host GPU via Mesa d3d12.", qPrintable(info.software));
    // no extra stderr line; exports see it via a logging provider or VLQT_LOG_STDERR (app/log.cpp emitLog).
}

} // namespace vlqt

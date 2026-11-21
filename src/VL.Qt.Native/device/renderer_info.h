// What a window renders with: backend, adapter, device type, whether that is the CPU, tearing support. The perf
// monitor (F5 System page, CPU/GPU badge) only formats it; the one-time "SOFTWARE RENDERING" warning lives here.
#pragma once

#include <QtCore/QString>
#include <rhi/qrhi.h>

class QQuickWindow;

namespace vlqt {

struct RendererInfo
{
    bool hasRhi = false;           // false: no QRhi (not initialized yet, or the Qt Quick software backend)
    bool quickSoftware = false;    // Qt Quick software backend (2D renderer, no QRhi)
    QRhi::Implementation backend = QRhi::Null; // only if hasRhi
    QString backendName;           // QRhi::backendName(), empty without QRhi
    QString adapter;               // QRhiDriverInfo::deviceName, empty if unknown
    QRhiDriverInfo::DeviceType deviceType = QRhiDriverInfo::UnknownDevice;
    // Non-empty (renderer name) if the window renders on the CPU: Qt Quick software backend, a software QRhi adapter
    // (CpuDevice, e.g. WARP) or a Mesa software rasterizer (llvmpipe, softpipe). Valid after sceneGraphInitialized.
    QString software;
    // Direct3D 11 only: the system supports DXGI_FEATURE_PRESENT_ALLOW_TEARING; false elsewhere.
    bool allowTearingSupported = false;
};

// Reads the window's rendering state. Call it where QQuickWindow::rhi() is valid: the GUI thread, or the render thread
// inside a frame signal of the window (sceneGraphInitialized, frameSwapped).
RendererInfo describeRenderer(QQuickWindow* window);

// Logs "SOFTWARE RENDERING" (logging category vlqt.gpu, warning) for the first software renderer of the process, once,
// whichever thread reports first. Does nothing if info.software is empty.
void warnSoftwareRendering(const RendererInfo& info);

} // namespace vlqt

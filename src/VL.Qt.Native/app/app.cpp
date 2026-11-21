// The application: the ABI version and the struct layouts of vlqt.h, the start of Qt (vlqt_app_init) and the pump
// (vlqt_app_process_events_for). Qt GUI thread, the thread that called vlqt_app_init.
#include "app/app.h"
#include "app/animation_clock.h"
#include "app/exports.h"
#include "app/fonts.h"
#include "app/log.h"
#include "app/render_gate.h"
#include "app/thread_name.h"
#include "platform/platform.h"
#include "window/scene_handle.h"
#include "device.h"
#include "perf.h"
#include "protocol.h"
#include "qtimageitem.h"
#include "resources.h"
#include "shader_item.h"
#include "vlqt.h"
#include "vlstyle.h"

#include <QtCore/QCoreApplication>
#include <QtCore/QElapsedTimer>
#include <QtCore/QLoggingCategory>
#include <QtCore/QPointer>
#include <QtGui/QGuiApplication>
#include <QtGui/QSurfaceFormat>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>
#include <QtQuickControls2/QQuickStyle>

#include <cstddef>
#include <vector>

// Layout of the structs of vlqt.h: sizes and the offset of the last field. The same numbers are in
// src/VL.Qt.Tests/NativeLayoutTests.cs, which checks them against the mirrors in Native.cs. Change both, and bump
// VLQT_ABI_VERSION (protocol.h). All members are 8-, 4- or 2-byte types (long long, double, int, float, pointer on a
// 64-bit target), so the numbers are the same on Windows (x64, arm64) and Linux.
static_assert(sizeof(void*) == 8, "the layouts below assume a 64-bit target");
static_assert(sizeof(vlqt_perf) == 104 && offsetof(vlqt_perf, vsync_ms) == 96, "vlqt_perf layout");
static_assert(sizeof(vlqt_perf_sample) == 36 && offsetof(vlqt_perf_sample, vsync) == 32, "vlqt_perf_sample layout");
static_assert(sizeof(vlqt_perf_stats) == 160 && offsetof(vlqt_perf_stats, recent_multi_swap) == 152, "vlqt_perf_stats layout");
static_assert(sizeof(vlqt_apply_error) == 24 && offsetof(vlqt_apply_error, reason) == 16, "vlqt_apply_error layout");
static_assert(sizeof(vlqt_host_pump) == 56 && offsetof(vlqt_host_pump, wait) == 52, "vlqt_host_pump layout");
static_assert(sizeof(vlqt_host_pump_stats) == 152 && offsetof(vlqt_host_pump_stats, wait_ms_max) == 144, "vlqt_host_pump_stats layout");
static_assert(sizeof(vlqt_res_info) == 32 && offsetof(vlqt_res_info, element_refs) == 28, "vlqt_res_info layout");
static_assert(sizeof(vlqt_pixel_desc) == 24 && offsetof(vlqt_pixel_desc, flags) == 20, "vlqt_pixel_desc layout");
static_assert(sizeof(vlqt_audio_tap_state) == 96 && offsetof(vlqt_audio_tap_state, reserved) == 88, "vlqt_audio_tap_state layout");
static_assert(sizeof(vlqt_scene_metrics) == 12 && offsetof(vlqt_scene_metrics, dpr) == 8, "vlqt_scene_metrics layout");

namespace vlqt {

QGuiApplication* g_app = nullptr;
QString g_nativeDir;
QString g_pipelineCachePath;

} // namespace vlqt

using vlqt::asWindow;
using vlqt::fail;
using vlqt::g_app;
using vlqt::g_nativeDir;
using vlqt::g_pipelineCachePath;
using vlqt::guarded;
using vlqt::UpdateGate;
using vlqt::log::emitLog;

extern "C" {

VLQT_API int vlqt_app_get_abi_version(void)
{
    return VLQT_ABI_VERSION;
}

VLQT_API int vlqt_app_init(const char* nativeDir, vlqt_log_fn log)
{
    return guarded([&]() -> int {
        if (g_app)
            return 0;

        // 1. Logging first; the callback is never changed afterwards.
        vlqt::log::attach(log);
        QLoggingCategory::setFilterRules(QStringLiteral(
            "qt.scenegraph.general=true\n"
            "qt.rhi.general=true"));

        g_nativeDir = QString::fromUtf8(nativeDir ? nativeDir : "");

        // 2. Plugins from our own deployment folder (before the app ctor).
        if (!g_nativeDir.isEmpty())
            QCoreApplication::addLibraryPath(g_nativeDir + QStringLiteral("/plugins"));

        // 3./4. Graphics API and default format.
#ifdef _WIN32
        QQuickWindow::setGraphicsApi(QSGRendererInterface::Direct3D11);
#endif
        // Linux: Qt's default RHI backend (OpenGL; QSG_RHI_BACKEND overrides). No other default is chosen yet.
        QSurfaceFormat fmt = QSurfaceFormat::defaultFormat();
        fmt.setSwapInterval(0);
        // No MSAA samples here: Qt gives a swap chain max(default, window) samples
        // (QSGRhiSupport::chooseSampleCountForWindowWithRhi), so a default count would be a floor for every window.
        // Each window sets its own count (vlqt_window_create, vlqt_window_set_samples).
        QSurfaceFormat::setDefaultFormat(fmt);

        // 5. Qt 6 has no API for the render loop; the CRT env copy must be set here.
        qputenv("QSG_RENDER_LOOP", "basic");
        // Deliver UpdateRequest on the next pump instead of after a coarse timer
        // (default 5 ms rounds up to ~16 ms+ on Windows: only ~1 in 4 pumps rendered).
        qputenv("QT_QPA_UPDATE_IDLE_TIME", "0");
        // Windows press-and-hold on a touchscreen sends a right click synthesized by the system; Qt Quick lets it
        // through (only that one) unless this is 0. Touch has its own long press (Tap handler), so drop it.
        // Read once by Qt Quick on the first mouse event; an explicit value set before the start wins.
        if (!qEnvironmentVariableIsSet("QT_QUICK_ALLOW_SYNTHETIC_RIGHT_CLICK"))
            qputenv("QT_QUICK_ALLOW_SYNTHETIC_RIGHT_CLICK", "0");

#ifndef _WIN32
        vlqt::platform::applyWslGpuDefault();
#endif

        // Qt Quick Controls style: the own base style "VLQt" on QtQuick.Templates, embedded in QtNative
        // (qrc:/qt/qml/VLQt, CMakeLists.txt), values from the attached VlStyle (vlstyle.h). Controls without an own file
        // fall back to Basic, the only Qt style deployed (trim-deploy/trim-linux). The single place that picks it.
        QQuickStyle::setStyle(QStringLiteral("VLQt"));

        // 6. One app per process, never deleted.
        static int argc = 1;
        static char* argv[] = {const_cast<char*>("vvvv"), nullptr};
        g_app = new QGuiApplication(argc, argv);
        // The calling thread is the Qt GUI thread from here on.
        vlqt::nameGuiThreadIfUnnamed("vlqt-gui");
        QGuiApplication::setQuitOnLastWindowClosed(false);
        emitLog(1, "vlqt.app", "QPA platform: " + QGuiApplication::platformName().toUtf8());
#ifndef _WIN32
        vlqt::platform::ensureFonts();
#endif
#ifdef _WIN32
        vlqt::platform::installDialogCodeFilter(*g_app); // Enter/Tab/Esc/arrows past IsDialogMessage
#endif

        // 7. Animation driver bound to vvvv time.
        vlqt::clock::install();

        registerPerfTypes();
        vlqt::registerStyleTypes();
        return 0;
    });
}

VLQT_API int vlqt_app_process_events_for(vlqt_scene* const* windows, int count)
{
    return guarded([&]() -> int {
        if (!g_app)
            return fail(VLQT_ERR_NOT_INITIALIZED, "process_events: app not initialized");
        QElapsedTimer t;
        t.start();
        static std::vector<UpdateGate*> s_active; // reused, no allocation per pump
        // processEvents also dispatches the Win32 messages of the host thread; a host callback in there (vvvv
        // restart) may destroy a window of this list, so its frame is ended only if the monitor is still alive.
        static std::vector<QPointer<PerfMonitor>> s_perf;
        s_active.clear();
        s_perf.clear();
        for (int i = 0; windows && i < count; i++)
            if (auto* w = asWindow(windows[i])) {
                s_active.push_back(w->gate);
                s_perf.emplace_back(w->perf);
            }
        // Device lost: also before anything renders, so no window builds a new QRhi on the removed device.
        vlqt::device::checkLost();
        vlqt::res::beginFrame(); // worker results + uploads within the budget, before the windows render
        vlqt::QtImageItem::afterResourceFrame(); // Image elements take new Ready content, drawn in this pump
        vlqt::ShaderItem::afterResourceFrame(); // ShaderEffects take new Ready / Failed shaders
        UpdateGate::beginPump(!windows, s_active.data(), int(s_active.size()));
        QCoreApplication::sendPostedEvents();
        QCoreApplication::processEvents(QEventLoop::AllEvents);
        UpdateGate::endPump();
        // Every window frame of this pump has ended: no lock may outlive it (device.h, lock order).
        vlqt::device::releaseFrameLocks();
        vlqt::res::endFrame(); // frees resources without references, never in a worker
        vlqt::fontsEndFrame(); // app fonts without references
        vlqt::device::checkLost();
        const float ms = float(t.nsecsElapsed() / 1e6);
        if (!windows)
            PerfMonitor::endPumpAll(ms);
        else
            for (const auto& perf : s_perf)
                if (perf)
                    perf->endPump(ms);
        return 0;
    });
}

VLQT_API int vlqt_app_set_pipeline_cache(const char* utf8Path)
{
    return guarded([&]() -> int {
        g_pipelineCachePath = QString::fromUtf8(utf8Path ? utf8Path : "");
        return VLQT_OK;
    });
}

} // extern "C"

// Linux implementation of platform.h. Private Qt API (setFrameless): QWindowPrivate, QHighDpi, QPlatformWindow; check
// on Qt updates (list: elements/elements.h).
#include "platform/platform.h"
#include "app/app.h"
#include "app/log.h"

#include <QtCore/QByteArray>
#include <QtCore/QFileInfo>
#include <QtCore/QStringList>
#include <QtCore/QtGlobal>
#include <QtGui/QFont>
#include <QtGui/QFontDatabase>
#include <QtGui/QGuiApplication>
#include <QtGui/private/qhighdpiscaling_p.h>
#include <QtGui/private/qwindow_p.h>
#include <QtGui/qpa/qplatformwindow.h>
#include <QtQuick/QQuickWindow>

#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <link.h>
#include <signal.h>
#include <spawn.h>
#include <sys/wait.h>
#include <unistd.h>

extern char** environ;

using vlqt::g_nativeDir;
using vlqt::log::emitLog;

namespace {

// The ELF interpreter (dynamic loader) of the host process, such as /lib64/ld-linux-x86-64.so.2, or null. It runs
// vlqt-glprobe when the helper's file lost the executable bit: NuGet packages and CI artifacts do not keep file modes.
const char* hostInterpreter()
{
    const char* interpreter = nullptr;
    dl_iterate_phdr([](dl_phdr_info* info, size_t, void* data) -> int {
        // The first object is the program itself; stop after it.
        for (int i = 0; i < info->dlpi_phnum; ++i)
            if (info->dlpi_phdr[i].p_type == PT_INTERP)
                *static_cast<const char**>(data) = reinterpret_cast<const char*>(info->dlpi_addr + info->dlpi_phdr[i].p_vaddr);
        return 1;
    }, &interpreter);
    return interpreter;
}

// Mesa's d3d12 driver does not work on every WSL system (seen on an Intel NUC: "glx: failed to create drisw screen",
// then Qt aborts). The helper vlqt-glprobe (src/VL.Qt.Native/tools/glprobe.c, next to libvlqt.so) does what Qt does
// first on xcb: open the X display and create a GLX context. It is a process of its own, started with posix_spawn,
// because it loads libX11 and libGL: in a fork of the multithreaded host that could deadlock on a lock another thread
// held at the fork. It inherits the current environment, so it sees the GALLIUM_DRIVER set here.
// True if OpenGL works, or if the helper cannot be started (keep the driver). False: the helper failed (exit code
// != 0, crash, or no answer within 10 s).
bool probeGlx()
{
    const QByteArray helper = (g_nativeDir + QStringLiteral("/vlqt-glprobe")).toLocal8Bit();
    int rc = ENOENT;
    pid_t pid = 0;
    if (!g_nativeDir.isEmpty() && access(helper.constData(), R_OK) == 0) {
        const char* interpreter = access(helper.constData(), X_OK) == 0 ? nullptr : hostInterpreter();
        char* argv[] = {const_cast<char*>(interpreter ? interpreter : helper.constData()),
            interpreter ? const_cast<char*>(helper.constData()) : nullptr, nullptr};
        rc = posix_spawn(&pid, argv[0], nullptr, nullptr, argv, environ);
    }
    if (rc != 0) {
        emitLog(2, "vlqt.gpu", "OpenGL check skipped: cannot start " + helper + " (" + QByteArray(std::strerror(rc))
            + "); keeping GALLIUM_DRIVER=" + qgetenv("GALLIUM_DRIVER"));
        return true;
    }
    for (int i = 0; i < 100; ++i) {
        int status = 0;
        if (waitpid(pid, &status, WNOHANG) == pid)
            return WIFEXITED(status) && WEXITSTATUS(status) == 0;
        usleep(100 * 1000);
    }
    kill(pid, SIGKILL);
    waitpid(pid, nullptr, 0);
    return false;
}

// a minimal/kiosk Linux without any font (fontconfig finds none) renders all text as tofu boxes, silently.
// Warn in the vvvv log, then register the bundled DejaVu Sans (<native>/fonts, deploy-linux.sh) and make it the app
// font. Only when there is no real font: with system fonts nothing changes. Needs the QGuiApplication.
// QFontDatabase::families() is not empty then: Qt (QFontconfigDatabase) always lists the generic aliases "Sans Serif",
// "Serif" and "Monospace", even with no font at all (and gives them synthesized styles), so only other names count.
bool hasSystemFonts()
{
    for (const QString& family : QFontDatabase::families()) {
        if (family != QLatin1String("Sans Serif") && family != QLatin1String("Serif") && family != QLatin1String("Monospace"))
            return true;
    }
    return false;
}

} // namespace

namespace vlqt::platform {

// WSLg: WSL has no DRM node (/dev/dri), only /dev/dxg, so Mesa's EGL loader finds no
// hardware driver, Zink finds no Vulkan device (Ubuntu ships no Dozen) and Mesa falls back to llvmpipe.
// GALLIUM_DRIVER=d3d12 selects Mesa's D3D12 Gallium driver (host GPU via /usr/lib/wsl/lib). Mesa reads it with
// libc getenv when EGL initializes (first window), so setenv here, before QGuiApplication, is early enough.
// Default under WSL; VLQT_GPU=software (set before process start) keeps Mesa's own choice.
// A GALLIUM_DRIVER set by the user always wins.
// Wayland + d3d12 hangs WSLg at the first window/GL context (process in state D, whole WSL VM
// unresponsive until `wsl --shutdown`); xcb + d3d12 works. So when we set d3d12 ourselves and the
// platform is not chosen explicitly, force QT_QPA_PLATFORM=xcb (Qt would otherwise pick Wayland via
// WAYLAND_DISPLAY). An explicit wayland* platform together with d3d12 only gets a warning.
void prepareBeforeApp()
{
    if (qgetenv("VLQT_GPU") != "software" && !std::getenv("GALLIUM_DRIVER")
        && access("/dev/dxg", F_OK) == 0 && access("/usr/lib/wsl/lib/libd3d12.so", F_OK) == 0) {
        setenv("GALLIUM_DRIVER", "d3d12", 0);
        emitLog(1, "vlqt.gpu", "WSLg detected: GALLIUM_DRIVER=d3d12 (Mesa D3D12, host GPU)");
        // VLQT_GPU_PROBE=off skips the check. Without DISPLAY there is no X server to probe (Wayland only: no check).
        if (qgetenv("VLQT_GPU_PROBE") != "off" && std::getenv("DISPLAY") && !probeGlx()) {
            setenv("GALLIUM_DRIVER", "llvmpipe", 1);
            emitLog(2, "vlqt.gpu", "Mesa D3D12 cannot create an OpenGL context on this system: falling back to GALLIUM_DRIVER=llvmpipe "
                                   "(CPU rendering, low fps; the perf overlay shows CPU instead of GPU). Check the GPU driver of Windows "
                                   "and 'wsl --update'; VLQT_GPU=software silences this check, VLQT_GPU_PROBE=off skips it.");
            // Last stage: no OpenGL at all, then Qt Quick's own software renderer (no GL needed).
            if (!probeGlx() && !std::getenv("QT_QUICK_BACKEND")) {
                setenv("QT_QUICK_BACKEND", "software", 0);
                emitLog(2, "vlqt.gpu", "llvmpipe cannot create an OpenGL context either: falling back to the Qt Quick software backend "
                                       "(CPU rendering, effects are not drawn).");
            }
        }
        // Only with an X display: without DISPLAY xcb cannot start (WSL without WSLg), Qt then aborts at once.
        if (!std::getenv("QT_QPA_PLATFORM") && std::getenv("DISPLAY")) {
            setenv("QT_QPA_PLATFORM", "xcb", 0);
            emitLog(1, "vlqt.gpu", "WSLg + d3d12: QT_QPA_PLATFORM=xcb (Wayland + d3d12 hangs WSLg)");
        }
    }
    // Windowed platforms need a display server; say so before Qt aborts with its generic "no Qt platform plugin" message.
    const char* platform = std::getenv("QT_QPA_PLATFORM");
    const bool headless = platform && (std::strcmp(platform, "offscreen") == 0 || std::strcmp(platform, "minimal") == 0
                                       || std::strncmp(platform, "eglfs", 5) == 0 || std::strncmp(platform, "linuxfb", 7) == 0);
    if (!headless && !std::getenv("DISPLAY") && !std::getenv("WAYLAND_DISPLAY")) {
        emitLog(3, "vlqt.app", "Neither DISPLAY nor WAYLAND_DISPLAY is set: there is no display server, so Qt cannot open a window. "
                               "Under WSL this means WSLg is not running (needs Windows 11 or Windows 10 22H2, 'wsl --update', "
                               "then 'wsl --shutdown'); over SSH use an X server or QT_QPA_PLATFORM=offscreen.");
    }
    const char* gallium = std::getenv("GALLIUM_DRIVER");
    const char* qpa = std::getenv("QT_QPA_PLATFORM");
    if (gallium && std::strcmp(gallium, "d3d12") == 0 && qpa && std::strncmp(qpa, "wayland", 7) == 0) {
        const char* msg = "QT_QPA_PLATFORM=wayland with GALLIUM_DRIVER=d3d12 can hang the whole WSL VM "
                          "; use QT_QPA_PLATFORM=xcb or GALLIUM_DRIVER=llvmpipe";
        emitLog(2, "vlqt.gpu", msg);
    }
}

void initAfterApp(QGuiApplication&)
{
    if (hasSystemFonts())
        return;
    emitLog(2, "vlqt.font", "No system fonts found (fontconfig lists no font family); all text would render as empty boxes. "
                            "Install a font package on this system, e.g. fonts-dejavu-core. Falling back to the bundled DejaVu Sans.");
    const QString path = g_nativeDir + QStringLiteral("/fonts/DejaVuSans.ttf");
    const int id = g_nativeDir.isEmpty() ? -1 : QFontDatabase::addApplicationFont(path);
    const QStringList families = id < 0 ? QStringList() : QFontDatabase::applicationFontFamilies(id);
    if (families.isEmpty()) {
        emitLog(3, "vlqt.font", "Fallback font " + path.toUtf8() + " could not be loaded; text will render as empty boxes");
        return;
    }
    QGuiApplication::setFont(QFont(families.first()));
    emitLog(1, "vlqt.font", "Fallback font registered: " + families.first().toUtf8());
}

// xcb: the window manager re-frames asynchronously; client geometry requested frame exclusive.
// QWindow::setGeometry returns early for an unchanged rect, so the platform window would never see it. offscreen
// (CI) recomputes its frame margins (2 px) only in QOffscreenWindow::setGeometry (no setWindowFlags), so the
// reported frame position kept the old margins and wandered by them on every toggle. The client rect is therefore
// passed to the platform window directly, frame exclusive, as QWindow::setGeometry would.
void setFrameless(QQuickWindow& window, bool on, bool keepClientRect)
{
    const Qt::WindowFlags flags = on ? (window.flags() | Qt::FramelessWindowHint) : (window.flags() & ~Qt::FramelessWindowHint);
    const QRect client = window.geometry();
    window.setFlags(flags);
    if (!keepClientRect)
        return;
    if (window.geometry() != client)
        window.setGeometry(client);
    else if (QPlatformWindow* pw = window.handle()) {
        QWindowPrivate::get(&window)->positionPolicy = QWindowPrivate::WindowFrameExclusive;
        pw->setGeometry(QHighDpi::toNativeWindowGeometry(client, &window));
    }
}

// libc environ (getenv): set before the process starts. .NET on Linux keeps variables set at run time to itself.
bool testSyntheticInputOnly()
{
    return qgetenv("VLQT_TEST_SYNTHETIC_INPUT") == "1";
}

// Linux file systems are case-sensitive.
QString pathKey(const QFileInfo& file)
{
    return file.canonicalFilePath();
}

} // namespace vlqt::platform

// Unload hook. At process exit the CLR is already gone when Qt's static destructors log. libvlqt.so's destructors run
// before those of libQt6Core.so (reverse dependency order), so cut the managed callback here. A destructor cannot tell
// dlclose from exit, so Qt's default message handler replaces ours in both cases. Exported apps end with _exit
// (ExportedApp.cs), which runs no destructors. Unloading QtNative is not supported otherwise (docs/architecture.md,
// Lifetime).
__attribute__((destructor)) static void vlqtUnload()
{
    vlqt::log::detach(true);
}
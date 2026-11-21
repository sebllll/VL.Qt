#include "platform/platform.h"
#include "app/app.h"
#include "app/log.h"

#include <QtCore/QByteArray>
#include <QtCore/QStringList>
#include <QtCore/QtGlobal>
#include <QtGui/QFont>
#include <QtGui/QFontDatabase>
#include <QtGui/QGuiApplication>

#include <cstdlib>
#include <cstring>
#include <dlfcn.h>
#include <signal.h>
#include <sys/wait.h>
#include <unistd.h>

using vlqt::g_nativeDir;
using vlqt::log::emitLog;

namespace {

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
// Mesa's d3d12 driver does not work on every WSL system (seen on an Intel NUC: "glx: failed to create drisw screen",
// then Qt aborts). Probe it in a child process that does what Qt does first on xcb: open the X display and create
// a GLX context. A failing probe (exit code != 0, crash, or no answer within 10 s) means: use llvmpipe instead.
// The child only uses libX11/libGL through dlopen and ends with _exit; the parent stays untouched.
bool probeGlx()
{
    const pid_t pid = fork();
    if (pid < 0)
        return true; // cannot probe: keep d3d12
    if (pid == 0) {
        using OpenDisplayFn = void* (*)(const char*);
        using SyncFn = int (*)(void*, int);
        using ChooseFn = void** (*)(void*, int, const int*, int*);
        using CreateFn = void* (*)(void*, void*, int, void*, int);
        void* x11 = dlopen("libX11.so.6", RTLD_NOW);
        void* gl = dlopen("libGL.so.1", RTLD_NOW);
        if (!x11 || !gl)
            _exit(2);
        auto openDisplay = reinterpret_cast<OpenDisplayFn>(dlsym(x11, "XOpenDisplay"));
        auto sync = reinterpret_cast<SyncFn>(dlsym(x11, "XSync"));
        auto choose = reinterpret_cast<ChooseFn>(dlsym(gl, "glXChooseFBConfig"));
        auto create = reinterpret_cast<CreateFn>(dlsym(gl, "glXCreateNewContext"));
        if (!openDisplay || !sync || !choose || !create)
            _exit(2);
        void* display = openDisplay(nullptr);
        if (!display)
            _exit(3);
        const int attribs[] = {0x8011 /*GLX_RENDER_TYPE*/, 0x1 /*GLX_RGBA_BIT*/, 0};
        int count = 0;
        void** configs = choose(display, 0, attribs, &count);
        if (!configs || count < 1)
            _exit(4);
        void* context = create(display, configs[0], 0x8014 /*GLX_RGBA_TYPE*/, nullptr, 1);
        sync(display, 0); // an X error from the context request ends the process here (default handler)
        _exit(context ? 0 : 5);
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

void applyWslGpuDefault()
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
            emitLog(1, "vlqt.gpu", "WSLg + d3d12: QT_QPA_PLATFORM=xcb (Wayland + d3d12 hangs WSLg, #216)");
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
                          "(#216); use QT_QPA_PLATFORM=xcb or GALLIUM_DRIVER=llvmpipe";
        emitLog(2, "vlqt.gpu", msg);
    }
}

void ensureFonts()
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

} // namespace vlqt::platform

// At process exit the CLR is already gone when Qt's static destructors log. libvlqt.so's destructors run before those
// of libQt6Core.so (reverse dependency order), so cut the managed callback here.
__attribute__((destructor)) static void vlqtUnload()
{
    vlqt::log::detach();
}

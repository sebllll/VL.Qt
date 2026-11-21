#include "window/window.h"
#include "window/focus_release.h"
#include "window/scene_handle.h"
#include "window/window_filters.h"
#include "app/app.h"
#include "app/log.h"
#include "app/render_gate.h"
#include "app/scene_change.h"
#include "platform/platform.h"
#include "test/test_hooks.h"
#include "scene/scene.h"
#include "device.h"
#include "offscreen/offscreen.h"
#include "diagnostics/perf.h"
#include "protocol.h"

#include <QtCore/QCoreApplication>
#include <QtCore/QFileInfo>
#include <QtCore/QVariant>
#include <QtGui/QGuiApplication>
#include <QtGui/QIcon>
#include <QtGui/QScreen>
#include <QtGui/QSurfaceFormat>
#include <QtQml/QQmlEngine>
#include <QtQuick/QQuickGraphicsConfiguration>
#include <QtQuick/QQuickWindow>

#include <cmath>
#include <memory>
#include <optional>
#include <utility>

using vlqt::log::emitLog;

namespace vlqt {

// Initial geometry, Qt logical pixels (DIP), applied before the first show so the window never
// appears at Qt's default position. Same rules as VL.Skia's SkiaRenderer.SetBounds, but via QScreen (platform
// neutral, no user32): a window rect that intersects no screen, and the NaN sentinel, are centered on the primary
// screen's working area; size < 1 = 600x400. The position is the outer top-left: setFramePosition marks it frame
// inclusive, the platform plugin adds the frame when it creates the native window (Windows: from the window style,
// xcb: NorthWest gravity), so the frame size need not be known yet. Wayland ignores client positions.
// embedded placeholder (CMakeLists.txt, :/vlqt/icon), several sizes so Windows picks title bar / taskbar / Alt-Tab sharp.
const QIcon& defaultIcon()
{
    static const QIcon icon = [] {
        QIcon i;
        for (const int size : {16, 24, 32, 48, 64, 128, 256})
            i.addFile(QStringLiteral(":/vlqt/icon/default-%1.png").arg(size), QSize(size, size));
        return i;
    }();
    return icon;
}

void placeWindow(QQuickWindow* win, float x, float y, int width, int height)
{
    if (width < 1 || height < 1) {
        width = 600;
        height = 400;
    }
    const QSize size(width, height);
    QScreen* target = nullptr;
    QPoint pos;
    constexpr float lim = 1e6f; // keeps qRound in int range; farther away is on no screen anyway
    if (std::isfinite(x) && std::isfinite(y) && std::fabs(x) < lim && std::fabs(y) < lim) {
        const QRect r(QPoint(qRound(x), qRound(y)), size);
        qint64 best = 0;
        for (QScreen* s : QGuiApplication::screens()) {
            const QRect i = s->geometry().intersected(r);
            const qint64 area = qint64(i.width()) * i.height();
            if (!i.isEmpty() && area > best) { // screen with the largest share: its DPI scales the window
                best = area;
                target = s;
            }
        }
        pos = r.topLeft();
    }
    if (!target) {
        target = QGuiApplication::primaryScreen();
        if (target) {
            const QRect a = target->availableGeometry();
            pos = QPoint((2 * a.left() + a.width()) / 2 - width / 2, (2 * a.top() + a.height()) / 2 - height / 2);
        }
    }
    if (target)
        win->setScreen(target);
    win->resize(size);
    if (target) // no screen at all: leave the position to Qt
        win->setFramePosition(pos);
}

// The parts of a scene handle that windows and offscreen scenes share: the QML engine with QtNative's imports.
std::unique_ptr<QQmlEngine> newEngine()
{
    auto engine = std::make_unique<QQmlEngine>();
    if (!g_nativeDir.isEmpty())
        engine->addImportPath(g_nativeDir + QStringLiteral("/qml"));
    return engine;
}

SceneHandle::SceneHandle() = default;

SceneHandle::~SceneHandle()
{
    // The order of the parts is explained at the struct (window/scene_handle.h).
    if (offscreen)
        vlqt::offscreen::release(offscreen.get());
}

void OffscreenSceneDeleter::operator()(OffscreenScene* scene) const
{
    vlqt::offscreen::destroy(scene);
}

// The event filters, the change counter and the frame lock of an on-screen window, all children of win: close, render
// gate, change counter, frame lock, fullscreen keys, focus release, test input. At creation and for the window swap
// (replaceWindow).
void installWindowFilters(SceneHandle* w, QQuickWindow* win)
{
    w->closeFilter = new CloseFilter(win);
    win->installEventFilter(w->closeFilter);
    w->gate = new RenderGate(win);
    win->installEventFilter(w->gate);
    w->changes = new SceneChangeCounter(win);
    vlqt::device::lockFrames(win); // windows only: a scene's render thread locks itself (offscreen/offscreen_render.cpp)
    win->installEventFilter(new FullscreenKeys(win, asHandle(w)));
    win->installEventFilter(new FocusRelease(win));
    vlqt::test::installInputFilter(win); // test hook: VLQT_TEST_SYNTHETIC_INPUT (test/test_input.cpp)
}

// MSAA count of a window's swap chain.
int clampWindowSamples(int requested, QQuickWindow* win)
{
    return vlqt::device::clampSamples(requested, vlqt::device::windowSwapChainFormat(), win->rhi());
}

// ... and, once w->window, w->engine and the window's own event filters exist: GPU timings and pipeline cache, the
// shared D3D11 device, the scene (apply/poll) and the perf monitor (its event filter, installed last, runs first).
void initShared(SceneHandle* w)
{
    // GPU timings for the perf overlay; must be set before the scenegraph initializes.
    QQuickGraphicsConfiguration cfg = w->window->graphicsConfiguration();
    cfg.setTimestamps(true);
    if (!g_pipelineCachePath.isEmpty()) {
        // Load + save in one file; an unwritable folder only costs the cache (QRhi would fail silently otherwise).
        const QFileInfo dir(QFileInfo(g_pipelineCachePath).absolutePath());
        if (dir.isDir() && dir.isWritable()) {
            cfg.setPipelineCacheLoadFile(g_pipelineCachePath);
            cfg.setPipelineCacheSaveFile(g_pipelineCachePath);
        } else {
            qWarning("pipeline cache: folder of %s is not writable, continuing without cache",
                qPrintable(g_pipelineCachePath));
        }
    }
    w->window->setGraphicsConfiguration(cfg);
    vlqt::device::attach(w->window.get()); // one D3D11 device for all windows, before the first expose
    w->scene = std::make_unique<vlqt::Scene>(w->window.get(), w->engine.get());
    w->perf = std::make_unique<PerfMonitor>(w->window.get(), w->engine.get());
    w->perf->setScene(w->scene.get()); // F5 Scene page; perf is deleted before scene
}

// Window modes (vlqt_window_set_mode): each applies one bit, w->mode already holds the new modes.
// Frame on/off (Qt::FramelessWindowHint) without re-creating the window. Also called by the fullscreen part
// when leaving fullscreen (restores the borderless state from w->mode).
// Before the first show (no platform window yet) only the flag changes: placeWindow's setFramePosition keeps the
// position frame inclusive, so the outer top-left = Bounds position, which without a frame is the client top-left.
// On a shown window Qt changes the window style live (Windows: SetWindowLong + SWP_FRAMECHANGED, keeps the outer rect;
// xcb: _MOTIF_WM_HINTS, the window manager re-frames), no hide/show. The client rect is restored afterwards so the
// content does not jump or shrink; the frame grows or disappears around it. Maximized/minimized windows keep their
// state, only the flag changes.
static void applyFrameless(SceneHandle* w, bool on)
{
    QQuickWindow* win = w->window.get();
    const Qt::WindowFlags flags = on ? (win->flags() | Qt::FramelessWindowHint) : (win->flags() & ~Qt::FramelessWindowHint);
    if (flags == win->flags())
        return;
    const bool keepClient = win->handle() && (win->windowStates() & (Qt::WindowMinimized | Qt::WindowMaximized | Qt::WindowFullScreen)) == 0;
    vlqt::platform::setFrameless(*win, on, keepClient);
}

// Borderless pin: while fullscreen only w->mode remembers it (applied by the fullscreen part on leaving fullscreen).
void applyBorderless(SceneHandle* w, bool on)
{
    if ((w->mode & VLQT_WINDOW_MODE_FULLSCREEN) || (w->window->windowStates() & Qt::WindowFullScreen))
        return;
    applyFrameless(w, on);
}

// Screen with the largest share of the window's frame rect, the rule of placeWindow. Windows keeps
// QWindow::screen() current while moving (MonitorFromWindow, nearest = largest intersection), xcb updates it from the
// window center only, so it is computed here and set if it differs.
static QScreen* screenWithLargestShare(QWindow* win)
{
    const QRect r = win->frameGeometry();
    QScreen* best = nullptr;
    qint64 bestArea = 0;
    for (QScreen* s : QGuiApplication::screens()) {
        const QRect i = s->geometry().intersected(r);
        const qint64 area = qint64(i.width()) * i.height();
        if (!i.isEmpty() && area > bestArea) {
            bestArea = area;
            best = s;
        }
    }
    return best ? best : win->screen();
}

// fullscreen on the screen the window is on now; off restores frame position, size and maximized state from
// before and the Borderless state from w->mode. Hidden windows (Fullscreen pin on create, before show) only get the
// state, show() keeps it.
// Windows: Qt saves/restores the native frame itself and hides the frame while fullscreen; the explicit restore
// below covers xcb, where the window manager answers asynchronously. eglfs is always fullscreen, nothing changes.
void applyFullscreen(SceneHandle* w, bool on)
{
    QQuickWindow* win = w->window.get();
    const bool isFs = win->windowStates().testFlag(Qt::WindowFullScreen);
    if (on) {
        if (isFs)
            return;
        w->fullscreenRestore = FullscreenRestore{win->framePosition(), win->size(),
            win->windowStates() & ~(Qt::WindowFullScreen | Qt::WindowMinimized)};
        QScreen* target = screenWithLargestShare(win);
        if (target && target != win->screen()) {
            emitLog(1, "vlqt.window", (QStringLiteral("fullscreen: screen ") + target->name()
                                       + QStringLiteral(" (largest share) instead of ")
                                       + (win->screen() ? win->screen()->name() : QStringLiteral("none")))
                                          .toUtf8());
            win->setScreen(target); // same virtual desktop: no re-creation, the fullscreen rect comes from screen()
        }
        win->setWindowStates(Qt::WindowFullScreen);
        if (win->isVisible())
            win->requestActivate(); // like showFullScreen; Windows hides the taskbar only for the active window
        return;
    }
    const std::optional<FullscreenRestore> restore = std::exchange(w->fullscreenRestore, std::nullopt);
    if (!isFs && !restore)
        return;
    win->setWindowStates(restore ? restore->states : Qt::WindowNoState);
    if (restore) {
        // Borderless state from w->mode (changes during fullscreen are only kept there).
        applyFrameless(w, w->mode & VLQT_WINDOW_MODE_BORDERLESS);
        if (!restore->states.testFlag(Qt::WindowMaximized)) { // maximized: Qt keeps the normal geometry itself
            win->setFramePosition(restore->framePos);
            win->resize(restore->size);
        }
    }
}

// Window swap (vlqt_window_set_samples; docs/decisions/render-texture.md, "MSAA"). The basic render loop fixes a
// window's sample count when it creates the window's QRhi (QSGGuiThreadRenderLoop::ensureRhi: the swap chain and the
// render context that layers use); QWindow::destroy()/show() recreates the swap chain with the old count. So the handle
// gets a new QQuickWindow with format f: same screen, geometry, window states, flags, title, icon, colour, filters,
// graphics configuration and shared device. The scene moves over (items, their state and the QML engine stay), a new
// PerfMonitor takes the overlay state, windows with the old one as transient parent (Popup.Window popups, QML windows)
// get the new one, then the old window is deleted. Input needs nothing else: the WM_GETDLGCODE filter looks the HWND up
// per message. The new native window is shown without activation unless the old one was active.
void replaceWindow(SceneHandle* w, const QSurfaceFormat& f)
{
    // The old window lives until the end: the new one takes its scene, filters' state, perf overlay and popups first.
    std::unique_ptr<QQuickWindow> old = std::move(w->window);
    const bool visible = old->isVisible();
    const bool active = old->isActive();
    auto* nw = new QQuickWindow();
    nw->setFormat(f);
    nw->setFlags(old->flags());
    nw->setScreen(old->screen());
    nw->setGeometry(old->geometry());
    nw->setWindowStates(old->windowStates());
    nw->setTitle(old->title());
    nw->setIcon(old->icon());
    nw->setColor(old->color());
    const bool closeRequested = w->closeFilter->requested;
    const SceneChangeCounter* oldChanges = w->changes;
    w->window.reset(nw);
    installWindowFilters(w, nw);
    w->closeFilter->requested = closeRequested;
    w->changes->continueFrom(*oldChanges);
    nw->setGraphicsConfiguration(old->graphicsConfiguration());
    vlqt::device::attach(nw);
    w->scene->moveToWindow(nw);
    const int overlay = w->perf->overlay();
    w->perf.reset();
    w->perf = std::make_unique<PerfMonitor>(nw, w->engine.get());
    w->perf->setScene(w->scene.get());
    if (overlay & (PerfMonitor::Meter | PerfMonitor::Stats))
        w->perf->setOverlay(overlay);
    for (QWindow* t : QGuiApplication::topLevelWindows())
        if (t->transientParent() == old.get())
            t->setTransientParent(nw);
    if (w->mode & VLQT_WINDOW_MODE_BORDERLESS)
        applyBorderless(w, true);
    if (visible) {
        // Qt's Windows plugin shows with SW_SHOWNOACTIVATE then: a change from the patch must not take the focus.
        if (!active)
            nw->setProperty("_q_showWithoutActivating", true);
        if (w->mode & VLQT_WINDOW_MODE_FULLSCREEN)
            nw->showFullScreen();
        else
            nw->show();
        nw->setProperty("_q_showWithoutActivating", QVariant());
    }
    old.reset();
    QCoreApplication::sendPostedEvents(nullptr, QEvent::DeferredDelete);
}

} // namespace vlqt

// Offscreen scenes: the scene handle part, declared in offscreen/offscreen.h.
namespace vlqt::offscreen {

vlqt_scene* createHandle(QQuickWindow* window, OffscreenScene* state)
{
    auto w = std::make_unique<SceneHandle>();
    w->offscreen.reset(state);
    w->window.reset(window);
    w->engine = newEngine();
    // Transparent until the first VLQT_OP_SET_CLEAR_COLOR: the QtTexture default (render-texture.md), unlike a window.
    window->setColor(Qt::transparent);
    // Change count (vlqt_scene_get_change_count); no render gate: without a platform window a scene gets no
    // UpdateRequest or Expose.
    w->changes = new SceneChangeCounter(window); // child of the window
    // Input comes from vlqt_offscreen_send_*; Esc and a press outside release the focus as in a window.
    window->installEventFilter(new FocusRelease(window)); // child of the window
    initShared(w.get());
    return asHandle(w.release());
}

OffscreenScene* stateOf(vlqt_scene* handle)
{
    return handle ? asSceneHandle(handle)->offscreen.get() : nullptr;
}

} // namespace vlqt::offscreen
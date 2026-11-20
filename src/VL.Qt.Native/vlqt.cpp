#include "vlqt.h"
#include "commands.h"
#include "device.h"
#include "perf.h"
#include "image_resource.h"
#include "offscreen.h"
#ifndef _WIN32
#include "offscreen_glx.h"
#endif
#include "protocol.h"
#include "qtimageitem.h"
#include "resources.h"
#include "shader_resource.h"
#include "audio_tap.h"
#include "shadereffectitem.h"
#include "snapshot_resource.h"
#include "image_writer.h"
#include "threadname.h"
#include "vlstyle.h"

#include <QtCore/QAbstractAnimation>
#include <QtCore/QAbstractNativeEventFilter>
#include <QtCore/QAnimationDriver>
#include <QtCore/QByteArray>
#include <QtCore/QCoreApplication>
#include <QtCore/QElapsedTimer>
#include <QtCore/QLoggingCategory>
#include <QtCore/QPointer>
#include <QtCore/QThread>
#include <QtGui/QFontDatabase>
#include <QtGui/QGuiApplication>
#include <QtGui/QIcon>
#include <QtGui/QImage>
#include <QtGui/QImageReader>
#include <QtGui/QMouseEvent>
#include <QtGui/QPointingDevice>
#include <QtGui/QScreen>
#include <QtGui/QSurfaceFormat>
#include <QtQml/QJSValue>
#include <QtQml/QQmlEngine>
#include <QtQuick/QQuickItem>
#include <QtCore/QFileInfo>
#include <QtQuick/QQuickGraphicsConfiguration>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>
#include <rhi/qrhi.h> // vlqt_window_samples: QRhiSwapChain
#include <QtQuickControls2/QQuickStyle>
// Dirty item list of a window (change count): private API, check on Qt updates.
// QAbstractAnimationTimer / QUnifiedTimer: the keep-alive timer of the vvvv animation clock (VlqtAnimationDriver).
#include <QtCore/private/qabstractanimation_p.h>
#include <QtQuick/private/qquickitem_p.h>
#include <QtQuick/private/qquickwindow_p.h>
// Touch moves held back until the next render (vlqt_app_deliver_input): private API, check on Qt updates.
#include <QtQuick/private/qquickdeliveryagent_p_p.h>

#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#else
#include <QtGui/private/qhighdpiscaling_p.h>
#include <QtGui/private/qwindow_p.h>
#include <QtGui/QOpenGLContext>
#include <QtGui/QOpenGLFunctions>
#include <QtGui/qpa/qplatformwindow.h>
#include <cstdlib>
#include <dlfcn.h>
#include <signal.h>
#include <sys/wait.h>
#include <unistd.h>
#endif

// TEST ONLY (vlqt_test_send_touch): event points with an item-local position, one registered touchscreen.
#include <QtGui/private/qeventpoint_p.h>
#include <QtGui/private/qpointingdevice_p.h>
#include <QtGui/qpa/qwindowsysteminterface.h>
// vlqt_app_deliver_input: QWindowSystemInterface::flushWindowSystemEvents. TEST ONLY: native positions of queued input.
#include <QtGui/private/qhighdpiscaling_p.h>

#include <string>
#include <utility>
#include <vector>
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <cmath>
#include <limits>
#include <cstddef>

// Layout of the structs of vlqt.h: sizes and the offset of the last field. The same numbers are in
// src/VL.Qt.Tests/NativeLayoutTests.cs, which checks them against the mirrors in Native.cs. Change both, and bump
// VLQT_ABI_VERSION (protocol.h). All members are 8-, 4- or 2-byte types (long long, double, int, float, pointer on a
// 64-bit target), so the numbers are the same on Windows (x64, arm64) and Linux.
static_assert(sizeof(void*) == 8, "the layouts below assume a 64-bit target");
static_assert(sizeof(vlqt_perf) == 104 && offsetof(vlqt_perf, vsync_ms) == 96, "vlqt_perf layout");
static_assert(sizeof(vlqt_perf_sample) == 36 && offsetof(vlqt_perf_sample, vsync) == 32, "vlqt_perf_sample layout");
static_assert(sizeof(vlqt_perf_stats) == 160 && offsetof(vlqt_perf_stats, recent_multi_swap) == 152, "vlqt_perf_stats layout");
static_assert(sizeof(vlqt_apply_error) == 24 && offsetof(vlqt_apply_error, reason) == 16, "vlqt_apply_error layout");
static_assert(sizeof(vlqt_host_frame) == 56 && offsetof(vlqt_host_frame, wait) == 52, "vlqt_host_frame layout");
static_assert(sizeof(vlqt_host_stats) == 152 && offsetof(vlqt_host_stats, wait_ms_max) == 144, "vlqt_host_stats layout");
static_assert(sizeof(vlqt_res_info) == 32 && offsetof(vlqt_res_info, element_refs) == 28, "vlqt_res_info layout");
static_assert(sizeof(vlqt_pixel_desc) == 24 && offsetof(vlqt_pixel_desc, flags) == 20, "vlqt_pixel_desc layout");
static_assert(sizeof(vlqt_audio_tap_state) == 96 && offsetof(vlqt_audio_tap_state, reserved) == 88, "vlqt_audio_tap_state layout");
static_assert(sizeof(vlqt_scene_metrics) == 12 && offsetof(vlqt_scene_metrics, dpr) == 8, "vlqt_scene_metrics layout");

namespace {

// Atomic: cut at unload (DllMain / ELF destructor), possibly on another thread than a logging one (exit() from a
// side thread, TSan finding #365); emitLog reads it once.
std::atomic<vlqt_log_fn> g_log{nullptr};
QString g_nativeDir;
QString g_pipelineCachePath; // vlqt_set_pipeline_cache, empty = Qt's pipeline cache off
std::string g_lastError;
QGuiApplication* g_app = nullptr;

void setError(const std::string& e) { g_lastError = e; }
void setErrorQ(const QString& e) { g_lastError = e.toStdString(); }

// every message of QtNative and of Qt goes to the managed logger (vvvv Log window, logging providers).
// Levels: 0 debug, 1 info, 2 warning, 3 critical, 4 fatal (C# maps them to LogLevel); the category is passed verbatim.
// VLQT_LOG_STDERR (set before process start) additionally mirrors everything to stderr: a diagnostic for exports
// without a console logging provider, not a second log path of the library.
void emitLog(int level, const char* category, const QByteArray& msg)
{
    const char* cat = category && *category ? category : "default";
    if (const auto log = g_log.load())
        log(level, cat, msg.constData());
    static const bool toStderr = qEnvironmentVariableIsSet("VLQT_LOG_STDERR");
    if (toStderr || level >= 3) // Critical/Fatal always also on stderr (exports without a log provider)
        std::fprintf(stderr, "[%s] %s\n", cat, msg.constData());
}

void messageHandler(QtMsgType type, const QMessageLogContext& ctx, const QString& msg)
{
    int level = 0;
    switch (type) {
    case QtDebugMsg:    level = 0; break;
    case QtInfoMsg:     level = 1; break;
    case QtWarningMsg:  level = 2; break;
    case QtCriticalMsg: level = 3; break;
    case QtFatalMsg:    level = 4; break;
    }
    emitLog(level, ctx.category, msg.toUtf8());
}

// Animation time comes from vvvv (IFrameClock), not from a wall clock (decisions/user-qml.md, timing.md).
// QUnifiedTimer (Qt 6, qabstractanimation.cpp) computes its time as driverStartTime + driver->elapsed() while the driver
// runs, so elapsed() must count from start() like QAnimationDriver's own timer: the absolute vvvv time made every
// animation after the clock had moved jump by that time (one step to the end value). When the driver is stopped,
// QUnifiedTimer takes the wall clock instead: it stops the driver when no animation runs (stopTimer, the time base is
// reset) and when only pauses run (localRestart, a wall-clock pause timer), and takes driverStartTime from the wall clock
// at the next start. VlqtKeepAlive below keeps it running, so every delta is a difference of vvvv times.
class VlqtAnimationDriver : public QAnimationDriver
{
public:
    qint64 elapsed() const override { return m_ms - m_startMs; }
    void setTime(double seconds) { m_ms = qint64(seconds * 1000.0); }
protected:
    void start() override
    {
        m_startMs = m_ms;
        QAnimationDriver::start();
    }
private:
    qint64 m_ms = 0;
    qint64 m_startMs = 0;
};
VlqtAnimationDriver* g_driver = nullptr;

// An animation timer that is always registered and never paused: QUnifiedTimer then never stops the driver (no
// "no animations" stop, no pause-only phase on the wall clock) and never resets its time base. It animates nothing and
// counts no running animation (perf overlay "animations").
class VlqtKeepAlive : public QAbstractAnimationTimer
{
public:
    void updateAnimationsTime(qint64) override {}
    void restartAnimationTimer() override {}
    qsizetype runningAnimationCount() const override { return 0; }
};

// Swallows the X-button close so the host decides (hide / close document), like VL.Skia's FormClosing.
class CloseFilter : public QObject
{
public:
    using QObject::QObject;
    bool requested = false;
protected:
    bool eventFilter(QObject* o, QEvent* e) override
    {
        if (e->type() == QEvent::Close) {
            e->ignore();
            requested = true;
            return true;
        }
        return QObject::eventFilter(o, e);
    }
};

// F11 and Alt+Enter toggle fullscreen like VL.Skia's renderer: the same path as the Fullscreen pin
// (vlqt_window_set_mode). The pin only acts on change, so a toggle by key stays until the pin changes.
class FullscreenKeys : public QObject
{
public:
    FullscreenKeys(QObject* parent, void* window) : QObject(parent), m_window(window) {}
protected:
    bool eventFilter(QObject* o, QEvent* e) override
    {
        if (e->type() == QEvent::KeyPress) {
            auto* ke = static_cast<QKeyEvent*>(e);
            const auto mods = ke->modifiers() & ~Qt::KeypadModifier;
            const bool enter = ke->key() == Qt::Key_Return || ke->key() == Qt::Key_Enter;
            if (!ke->isAutoRepeat() && ((ke->key() == Qt::Key_F11 && mods == Qt::NoModifier)
                                        || (enter && mods == Qt::AltModifier))) {
                const unsigned fs = VLQT_WINDOW_MODE_FULLSCREEN;
                vlqt_window_set_mode(m_window, fs, (vlqt_window_get_mode(m_window) & fs) ^ fs);
                return true;
            }
        }
        return QObject::eventFilter(o, e);
    }
private:
    void* m_window;
};

// Qt Quick never takes the focus away by itself, so an input element kept it until another one was clicked.
//   Esc        discards the running edit (TextField text -> vlqtCommitted, SpinBox text -> displayText of value) and
//              releases the focus: the following focus loss finds nothing changed, so no EV_VALUE.
//   Press      (mouse, touch, tablet) outside the focused element releases the focus *before* the press is delivered:
//              a running edit is committed like on any focus loss, the press itself is delivered unchanged (a Click
//              handler still clicks, another input takes the focus on its press as before). Nothing is consumed and
//              no grab is taken, so the input handlers (#319-#321) see the same event sequence as without the filter.
//   Enter      is not touched: the control commits and keeps the focus.
// Deciding before delivery (hit test against the focused element) instead of after it needs no second pass and no
// queued check; a passive TapHandler on the root would only see presses that reach the root and not those taken by an
// element's handler, and it would add a handler to every pointer grab.
class FocusRelease : public QObject
{
public:
    FocusRelease(QQuickWindow* window) : QObject(window), m_window(window) {}
protected:
    bool eventFilter(QObject* o, QEvent* e) override
    {
        switch (e->type()) {
        case QEvent::KeyPress: {
            auto* ke = static_cast<QKeyEvent*>(e);
            if (ke->key() == Qt::Key_Escape && (ke->modifiers() & ~Qt::KeypadModifier) == Qt::NoModifier) {
                if (QQuickItem* owner = focusOwner()) {
                    revert(owner);
                    release();
                    return true;
                }
            }
            break;
        }
        case QEvent::MouseButtonPress:
        case QEvent::MouseButtonDblClick:
        case QEvent::TouchBegin:
        case QEvent::TouchUpdate:
        case QEvent::TabletPress: {
            QQuickItem* owner = focusOwner();
            if (!owner)
                break;
            const auto* pe = static_cast<QPointerEvent*>(e);
            for (const QEventPoint& p : pe->points()) {
                if (p.state() == QEventPoint::State::Pressed && !owner->contains(owner->mapFromScene(p.scenePosition()))) {
                    release();
                    break;
                }
            }
            break;
        }
        default:
            break;
        }
        return QObject::eventFilter(o, e);
    }
private:
    // The element that holds the active focus: the item itself or its innermost Control / TextField ancestor (a
    // SpinBox gives the focus to its contentItem). nullptr if nothing below the root has the focus.
    QQuickItem* focusOwner() const
    {
        QQuickItem* f = m_window->activeFocusItem();
        if (!f || f == m_window->contentItem())
            return nullptr;
        for (QQuickItem* i = f; i && i != m_window->contentItem(); i = i->parentItem()) {
            if (i->inherits("QQuickControl") || i->inherits("QQuickTextField"))
                return i;
        }
        return f;
    }

    // Esc: back to the last set or committed value, without a user signal (see "Value inputs" in elements.cpp).
    static void revert(QQuickItem* owner)
    {
        if (owner->inherits("QQuickTextField")) {
            const QVariant committed = owner->property("vlqtCommitted");
            if (committed.isValid() && owner->property("text").toString() != committed.toString())
                owner->setProperty("text", committed);
        } else if (owner->inherits("QQuickSpinBox")) {
            // The typed text lives in contentItem (and displayText follows it); the focus loss would parse and commit
            // it (valueModified). Show the value again, formatted by the control's own textFromValue: a C++ write keeps
            // the style's binding text: control.displayText, and the value is unchanged, so no valueModified.
            auto* content = owner->property("contentItem").value<QQuickItem*>();
            if (!content)
                return;
            const int value = owner->property("value").toInt();
            QString shown = QString::number(value);
            QJSValue fn = owner->property("textFromValue").value<QJSValue>();
            if (fn.isCallable()) {
                const QJSValue r = fn.call({QJSValue(value)});
                if (!r.isError())
                    shown = r.toString();
            }
            if (content->property("text").toString() != shown)
                content->setProperty("text", shown);
        }
    }

    void release() { vlqt::offscreen::releaseFocus(m_window); }
    QQuickWindow* m_window;
};

#ifdef _WIN32
// vvvv's main thread runs a WinForms message loop. Its PreTranslateMessage hands every key message for a
// non-WinForms HWND (our Qt windows) to IsDialogMessage(root), which takes Enter, Tab, Escape and the arrow keys for
// dialog navigation unless the window asks for them via WM_GETDLGCODE. Qt answers 0 (DefWindowProc), so Enter never
// reached a TextField or SpinBox (commit only on focus loss). Qt does its own focus handling: want all keys.
class DialogCodeFilter : public QAbstractNativeEventFilter
{
public:
    bool nativeEventFilter(const QByteArray&, void* message, qintptr* result) override
    {
        const auto* msg = static_cast<const MSG*>(message);
        if (msg->message != WM_GETDLGCODE)
            return false;
        for (QWindow* w : QGuiApplication::topLevelWindows()) {
            if (w->handle() && reinterpret_cast<HWND>(w->winId()) == msg->hwnd) {
                *result = DLGC_WANTALLKEYS | DLGC_WANTARROWS | DLGC_WANTTAB | DLGC_WANTCHARS;
                return true;
            }
        }
        return false;
    }
};
#endif

// At most one render per pump, and only inside the pump. With the basic loop Qt renders on UpdateRequest,
// which vvvv's Win32 message loop also delivers between two pumps. A MultiEffect source (layer) requests a second,
// redundant render right after each one (frame interval ~0.9 ms, "1100 fps"). Requests outside the pump or after
// this pump's render are swallowed and delivered once at the end of the next pump. The pump never waits for VSync.
// several vvvv apps (entry points) pump the one QGuiApplication, each once per vvvv frame. Only the windows of the
// pumping app may render (the others are blocked and keep their request for their own app's pump), so each window
// still renders at most once per vvvv frame instead of once per app.
class UpdateGate : public QObject
{
public:
    explicit UpdateGate(QQuickWindow* window) : QObject(window), m_window(window)
    {
        s_gates.push_back(this);
        // Every render inside the pump counts, also one started by an Expose (basic loop renders it directly).
        connect(window, &QQuickWindow::beforeSynchronizing, this, [this] {
            m_everRendered = true;
            if (s_inPump && !m_blocked)
                m_rendered = true;
            const bool dirty = sceneDirty(); // always: keeps the content children up to date
            if (std::exchange(m_changed, false) || dirty)
                ++m_changeCount;
        }, Qt::DirectConnection);
    }
    ~UpdateGate() override { s_gates.erase(std::remove(s_gates.begin(), s_gates.end(), this), s_gates.end()); }

    // Qt rendered this window in the last pump (and so polished its items). Valid until the next pump starts.
    bool rendered() const { return m_rendered; }

    // Frames with a change of the scene (vlqt_window_change_count): counted where Qt syncs a render, so a
    // static scene keeps the count however often the pump runs or Expose re-renders it.
    long long changeCount() const { return m_changeCount; }
    // The scene changed without a dirty item (e.g. the window color): count the next render.
    void markChanged() { m_changed = true; }
    // Window swap: the gate of the new window goes on with the change count of the old one, so the count never
    // goes back; the first render of the new window counts as a change (other sample count).
    void continueFrom(const UpdateGate& old)
    {
        m_changeCount = old.m_changeCount;
        m_changed = true;
    }

    // all = every window may render; else only the given gates (the windows of the pumping app).
    static void beginPump(bool all, UpdateGate* const* active, int count)
    {
        s_inPump = true;
        for (UpdateGate* g : s_gates)
            g->m_blocked = !all;
        for (int i = 0; i < count; i++)
            active[i]->m_blocked = false;
        for (UpdateGate* g : s_gates)
            if (!g->m_blocked)
                g->m_rendered = false;
    }
    static void endPump()
    {
        for (UpdateGate* g : s_gates) {
            if (!g->m_blocked && g->m_pending && !g->m_rendered) {
                QEvent request(QEvent::UpdateRequest);
                QCoreApplication::sendEvent(g->m_window, &request);
            }
            g->m_blocked = false;
        }
        s_inPump = false;
    }
    static bool inPump() { return s_inPump; }
    // input delivered at the host frame start (vlqt_app_deliver_input) renders nothing: update requests are
    // swallowed as outside every pump, and an Expose is deferred to the next pump as well.
    static void beginDelivery() { s_delivering = true; }
    static void endDelivery() { s_delivering = false; }
    // Qt Quick compresses touch moves and delivers the last one right before the next render (flushFrameSynchronousEvents).
    // The delivery at the frame start renders nothing, so it hands them over itself; the render finds none left.
    static void deliverDelayedTouch()
    {
        for (size_t i = 0; i < s_gates.size(); i++) {
            QQuickDeliveryAgentPrivate* agent = QQuickWindowPrivate::get(s_gates[i]->m_window)->deliveryAgentPrivate();
            if (agent && agent->delayedTouch)
                agent->deliverDelayedTouchEvent();
        }
    }
protected:
    bool eventFilter(QObject* o, QEvent* e) override
    {
        if (e->type() == QEvent::UpdateRequest) {
            if (!s_inPump || m_blocked || m_rendered) {
                m_pending = true;
                return true;
            }
            m_pending = false;
        } else if (e->type() == QEvent::Expose && m_window->isExposed()
                   && (s_inPump ? m_blocked || m_rendered : s_delivering || !m_everRendered)) {
            // Re-expose after this pump's render (XWayland sends two Expose on show) and the initial Expose outside
            // the pump (Windows: on show) render once in the pump instead. Later Expose outside the pump
            // (live resize in Windows' modal size loop) still renders directly, except during the input delivery of
            // the frame start; un-expose always passes.
            m_pending = true;
            return true;
        }
        return QObject::eventFilter(o, e);
    }
private:
    // The window's dirty items (Qt syncs them right after beforeSynchronizing) hold a change other than the footprint
    // of a snapshot (grabs must not count, or an open tooltip would grab a static scene forever): grabToImage
    // and a snapshot's frame source ref the item when they start and deref it when they are done (EffectReference on
    // the item, ChildrenStackingChanged on its parent), and the frame source comes and goes under the content item.
    // A real change in the same frame as such a ref (a reorder of the grabbed item's siblings) is missed until the
    // next one. Every item update() (elements, reconciler, video, images), polish result, resize or hover style is a
    // dirty item.
    bool sceneDirty()
    {
        using D = QQuickItemPrivate;
        constexpr quint32 grabRef = D::EffectReference | D::ChildrenStackingChanged;
        constexpr quint32 children = D::ChildrenChanged | grabRef;
        QQuickItem* content = m_window->contentItem();
        bool changed = false, contentDirty = false;
        for (QQuickItem* i = QQuickWindowPrivate::get(m_window)->dirtyItemList; i; i = D::get(i)->nextDirtyItem) {
            const quint32 bits = D::get(i)->dirtyAttributes;
            if (i == content) {
                contentDirty = true;
                changed |= (bits & ~children) != 0; // e.g. Size on resize
            } else if (!changed && !isGrabSource(i) && !inPerfOverlay(i) && (bits & ~D::EffectReference)
                       && ((bits & ~grabRef) || !hasRefChild(i))) {
                changed = true;
            }
        }
        // Children of the content item: a frame source is no change, any other child added or removed is.
        const bool contentChanged = contentDirty && contentChildrenChanged();
        return contentChanged || changed;
    }
    // The hidden ShaderEffectSource of a snapshot (snapshot_resource.cpp frameTarget): the only one directly under the
    // content item (Group frame proxies of effects live under the scene root).
    bool isGrabSource(QQuickItem* i) const
    {
        return i->parentItem() == m_window->contentItem() && !i->isVisible() && i->inherits("QQuickShaderEffectSource");
    }
    // The F5 overlay (perf.cpp, objectName perfOverlayName under the content item) is no scene change: its graphs
    // repaint every frame, and package 05 measures the tooltip's snapshot rate with the overlay open.
    bool inPerfOverlay(QQuickItem* i) const
    {
        QQuickItem* content = m_window->contentItem();
        while (i->parentItem() && i->parentItem() != content)
            i = i->parentItem();
        return isPerfOverlay(i);
    }
    static bool isPerfOverlay(QQuickItem* i) { return i->objectName() == QLatin1String(vlqt::perfOverlayName); }
    static bool hasRefChild(QQuickItem* parent)
    {
        for (QQuickItem* c : std::as_const(QQuickItemPrivate::get(parent)->childItems))
            if (QQuickItemPrivate::get(c)->dirtyAttributes & QQuickItemPrivate::EffectReference)
                return true;
        return false;
    }
    bool contentChildrenChanged()
    {
        s_children.clear();
        for (QQuickItem* c : std::as_const(QQuickItemPrivate::get(m_window->contentItem())->childItems))
            if (!isGrabSource(c) && !isPerfOverlay(c))
                s_children.push_back(c);
        if (s_children == m_contentChildren)
            return false;
        m_contentChildren.swap(s_children);
        return true;
    }

    QQuickWindow* m_window;
    long long m_changeCount = 0;
    bool m_changed = false;
    std::vector<QQuickItem*> m_contentChildren; // the content item's children at the last render, without frame sources
    static inline std::vector<QQuickItem*> s_children; // scratch, no allocation per render
    bool m_pending = false, m_rendered = false, m_everRendered = false;
    bool m_blocked = false; // this pump belongs to another app
    static inline bool s_inPump = false;
    static inline bool s_delivering = false; // vlqt_app_deliver_input
    static inline std::vector<UpdateGate*> s_gates;
};

// TEST ONLY: with VLQT_TEST_SYNTHETIC_INPUT=1 (read per window at create, SmokeTest) the window drops spontaneous
// (OS) pointer/hover events, so a real cursor resting over a test window cannot change hover/press state; input
// from vlqt_test_send_mouse (sendEvent, not spontaneous) passes, and so does queued input of the test devices
// (vlqt_test_queue_mouse / _touch, spontaneous like platform input).
constexpr qint64 testTouchSystemId = 0x766c7174;
constexpr qint64 testMouseSystemId = 0x766c716d;
class SystemInputFilter : public QObject
{
public:
    using QObject::QObject;
protected:
    static bool fromTestDevice(QEvent* e)
    {
        if (!e->isPointerEvent())
            return false;
        const QPointingDevice* d = static_cast<QPointerEvent*>(e)->pointingDevice();
        return d && (d->systemId() == testTouchSystemId || d->systemId() == testMouseSystemId);
    }
    bool eventFilter(QObject* o, QEvent* e) override
    {
        if (e->spontaneous() && !fromTestDevice(e)) {
            switch (e->type()) {
            case QEvent::MouseMove: case QEvent::MouseButtonPress: case QEvent::MouseButtonRelease:
            case QEvent::MouseButtonDblClick: case QEvent::HoverEnter: case QEvent::HoverMove:
            case QEvent::HoverLeave: case QEvent::Enter: case QEvent::Leave: case QEvent::Wheel:
            case QEvent::TouchBegin: case QEvent::TouchUpdate: case QEvent::TouchEnd: case QEvent::TouchCancel:
            case QEvent::TabletMove: case QEvent::TabletPress: case QEvent::TabletRelease:
                return true;
            default:
                break;
            }
        }
        return QObject::eventFilter(o, e);
    }
};

#ifdef _WIN32
// Win32 process environment, not the CRT copy (qgetenv): the .NET host sets the variable at runtime.
bool testSyntheticInputOnly()
{
    wchar_t v[4] = {};
    return GetEnvironmentVariableW(L"VLQT_TEST_SYNTHETIC_INPUT", v, 4) == 1 && v[0] == L'1';
}
#else
// libc environ (getenv).
bool testSyntheticInputOnly()
{
    return qgetenv("VLQT_TEST_SYNTHETIC_INPUT") == "1";
}
#endif

// A window handle (void* of the vlqt_window_* exports): a window, or an offscreen scene (offscreen.cpp) whose
// QQuickWindow is driven by a QQuickRenderControl. Both share engine, scene, gate and perf; a scene has only the
// FocusRelease input filter (closeFilter etc. stay null) and its own part in offscreen.
struct Window {
    QQuickWindow* window = nullptr;
    vlqt::offscreen::State* offscreen = nullptr; // offscreen scene: render control and binding; null for a window
    CloseFilter* closeFilter = nullptr;
    QQmlEngine* engine = nullptr;
    vlqt::Scene* scene = nullptr; // apply/poll
    UpdateGate* gate = nullptr;
    PerfMonitor* perf = nullptr;
    Qt::MouseButtons testButtons = Qt::NoButton; // vlqt_test_send_mouse: buttons held between test events
    QElapsedTimer testClock;                     // vlqt_test_send_mouse / _touch: event timestamps
    std::vector<std::pair<int, QPointF>> testTouch; // vlqt_test_send_touch: active points (id, window DIP)
    Qt::MouseButtons testQueuedButtons = Qt::NoButton;    // vlqt_test_queue_mouse
    std::vector<std::pair<int, QPointF>> testQueuedTouch; // vlqt_test_queue_touch: active points (id, window DIP)
    unsigned mode = 0;                           // vlqt_window_set_mode: requested vlqt_window_mode bits
    // geometry before fullscreen (DIP, frame position like vlqt_window_create_at), restored when leaving it
    bool fsSaved = false;
    QPoint fsFramePos;
    QSize fsSize;
    Qt::WindowStates fsStates;
};

// vlqt_test_send_key*: deliver like the platform does, to the focus window. A Qt Quick popup of type Popup.Window
// (e.g. the TextField context menu under Linux offscreen) is its own Qt::Popup window and holds the keyboard
// focus; sent to the main window, Escape would never close it and all later keys would be lost. Once the popup is
// hidden, a window manager gives the focus back to the main window; the offscreen QPA has none and leaves it on the
// hidden popup (main window inactive, no active focus item), so the test hands it back here.
QWindow* testKeyTarget(Window* w)
{
    QWindow* f = QGuiApplication::focusWindow();
    if (!f || f == w->window || f->type() != Qt::Popup || f->transientParent() != w->window)
        return w->window;
    if (f->isVisible())
        return f;
    QWindowSystemInterface::handleFocusWindowChanged<QWindowSystemInterface::SynchronousDelivery>(w->window, Qt::PopupFocusReason);
    return w->window;
}

// The test input exports on an offscreen scene: a scene takes input through vlqt_offscreen_send_*.
int notOnScene(const char* function)
{
    setError(std::string(function) + ": not supported on an offscreen scene");
    return VLQT_OFFSCREEN_NOT_SUPPORTED;
}

template <class F>
int guarded(F&& f)
{
    try {
        return f();
    } catch (const std::exception& e) {
        setError(std::string("exception: ") + e.what());
    } catch (...) {
        setError("unknown exception");
    }
    return -1;
}

#ifndef _WIN32
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
#endif

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

// The parts of a window handle that windows and offscreen scenes share: the QML engine with QtNative's imports.
QQmlEngine* newEngine()
{
    auto* engine = new QQmlEngine();
    if (!g_nativeDir.isEmpty())
        engine->addImportPath(g_nativeDir + QStringLiteral("/qml"));
    return engine;
}

// The event filters and the frame lock of an on-screen window, all children of win: close, update gate, frame lock,
// fullscreen keys, focus release, test input. At creation and for the window swap (replaceWindow).
void installWindowFilters(Window* w, QQuickWindow* win)
{
    w->closeFilter = new CloseFilter(win);
    win->installEventFilter(w->closeFilter);
    w->gate = new UpdateGate(win);
    win->installEventFilter(w->gate);
    vlqt::device::lockFrames(win); // windows only: a scene's render thread locks itself (offscreen.cpp)
    win->installEventFilter(new FullscreenKeys(win, w));
    win->installEventFilter(new FocusRelease(win));
    if (testSyntheticInputOnly())
        win->installEventFilter(new SystemInputFilter(win)); // test only
}

// MSAA count of a window's swap chain: R8G8B8A8_UNORM is the format of Qt's D3D11 swap chain (SDR).
int clampWindowSamples(int requested, QQuickWindow* win)
{
    constexpr int swapChainFormat = 28; // DXGI_FORMAT_R8G8B8A8_UNORM
    return vlqt::device::clampSamples(requested, swapChainFormat, win->rhi());
}

// ... and, once w->window, w->engine and the window's own event filters exist: GPU timings and pipeline cache, the
// shared D3D11 device, the scene (apply/poll) and the perf monitor (its event filter, installed last, runs first).
void initShared(Window* w)
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
    vlqt::device::attach(w->window); // one D3D11 device for all windows, before the first expose
    w->scene = new vlqt::Scene(w->window, w->engine);
    w->perf = new PerfMonitor(w->window, w->engine);
    w->perf->setScene(w->scene); // F5 Scene page; perf is deleted before scene
}

// App fonts (LoadFont node). One entry per font file, shared by every LoadFont node that names it.
// Thread: QFontDatabase app fonts and every text layout belong to the Qt GUI thread, which is the vvvv main thread
// (vlqt_app_init, basic render loop, no render thread). Adding there means the next layout in the same pump already
// sees the family, so no queue to another thread is needed; calls from other threads are rejected.
// Key: canonical path + size + last-modified time. Qt reads the file when it is registered; a later change on disk is
// not watched, but the next add after a change (path edit, app restart) registers the new version as its own entry.
// A deleted file keeps working until its entry is removed.
// Removal: refs 0 only marks the entry; fontsEndFrame removes it at the end of the pump if nobody took it again, so a
// path swap between nodes, a node re-created by a live edit or an app restart (F8) does not remove and re-add the font.
// Texts still naming a removed family keep their glyphs until their next layout, then Qt substitutes another font.
// The QGuiApplication is never deleted, so fonts still registered at shutdown stay with the process.
struct FontEntry {
    QString key;
    int qtId = -1;
    int refs = 0;
    QByteArray families; // '\n'-separated, UTF-8
};
std::vector<std::pair<unsigned long long, FontEntry>> g_fonts; // few entries: linear search
unsigned long long g_nextFontHandle = 1;

FontEntry* findFont(unsigned long long handle)
{
    for (auto& [h, e] : g_fonts)
        if (h == handle)
            return &e;
    return nullptr;
}

void fontsEndFrame()
{
    for (auto it = g_fonts.begin(); it != g_fonts.end();) {
        if (it->second.refs > 0) {
            ++it;
            continue;
        }
        QFontDatabase::removeApplicationFont(it->second.qtId);
        emitLog(0, "vlqt.font", "Font removed: " + it->second.families);
        it = g_fonts.erase(it);
    }
}

} // namespace

// Offscreen scenes: the window handle part, declared in offscreen.h.
namespace vlqt::offscreen {

void* createHandle(QQuickWindow* window, State* state)
{
    auto* w = new Window();
    w->offscreen = state;
    w->engine = newEngine();
    w->window = window;
    // Transparent until the first VLQT_OP_CLEAR_COLOR: the QtTexture default (render-texture.md), unlike a window.
    window->setColor(Qt::transparent);
    // Change count (vlqt_window_change_count) only: without a platform window a scene gets no UpdateRequest or Expose,
    // so the gate's event filter is not installed.
    w->gate = new UpdateGate(window); // child of the window
    // Input comes from vlqt_offscreen_send_*; Esc and a press outside release the focus as in a window.
    window->installEventFilter(new FocusRelease(window)); // child of the window
    initShared(w);
    return w;
}

void releaseFocus(QQuickWindow* window)
{
    QQuickItem* root = window->contentItem();
    QQuickItem* f = window->activeFocusItem();
    if (!f || f == root)
        return;
    // A Control is a focus scope (SpinBox: the focus sits on its contentItem), clearing only the inner item keeps it
    // inside.
    QQuickItem* top = f;
    for (QQuickItem* i = f; i && i != root; i = i->parentItem()) {
        if (i->hasFocus())
            top = i;
    }
    top->setFocus(false, Qt::OtherFocusReason);
    if (window->activeFocusItem() != root)
        root->forceActiveFocus(Qt::OtherFocusReason);
}

bool sendTouch(QQuickWindow* window, const QPointingDevice* device, std::vector<std::pair<int, QPointF>>& active,
    QEventPoint::State state, int id, QPointF pos, ulong timestamp, Qt::KeyboardModifiers modifiers, const char* function)
{
    auto it = std::find_if(active.begin(), active.end(), [id](const auto& a) { return a.first == id; });
    const bool known = it != active.end();
    const bool press = state == QEventPoint::State::Pressed;
    if (known == press) {
        ::setError(std::string(function) + ": point " + std::to_string(id) + (known ? " is already pressed" : " is not pressed"));
        return false;
    }
    if (known)
        it->second = pos;
    else
        active.emplace_back(id, pos);

    auto* devicePrivate = QPointingDevicePrivate::get(const_cast<QPointingDevice*>(device));
    // What QGuiApplication does before delivering a press: the device registers the point. Without it the grabs
    // of the delivery are not stored ("point is not in activePoints"), the release goes to every handler under the
    // point and stacked Tap handlers all tap.
    if (press)
        devicePrivate->pointById(id);
    // The complete touch state: every active point, the changed one with its state, the others stationary.
    QList<QEventPoint> points;
    for (const auto& a : active) {
        const QEventPoint::State st = a.first == id ? state : QEventPoint::State::Stationary;
        QEventPoint ep = QMutableEventPoint::withTimeStamp(timestamp, a.first, st, a.second, a.second, window->mapToGlobal(a.second));
        QMutableEventPoint::setDevice(ep, device);
        QMutableEventPoint::setWindow(ep, window);
        points.append(ep);
    }
    const bool release = state == QEventPoint::State::Released;
    const QEvent::Type t = press && active.size() == 1 ? QEvent::TouchBegin
        : release && active.size() == 1               ? QEvent::TouchEnd
                                                      : QEvent::TouchUpdate;
    QTouchEvent ev(t, device, modifiers, points);
    ev.setTimestamp(timestamp);
    QCoreApplication::sendEvent(window, &ev);
    if (release) {
        active.erase(std::find_if(active.begin(), active.end(), [id](const auto& a) { return a.first == id; }));
        // What QGuiApplication does after delivering a release: the device forgets the point (and its grabbers).
        devicePrivate->removePointById(id);
    }
    return true;
}

void cancelTouch(QQuickWindow* window, const QPointingDevice* device, std::vector<std::pair<int, QPointF>>& active,
    ulong timestamp, Qt::KeyboardModifiers modifiers)
{
    if (active.empty())
        return;
    // Without points, like the platform's: Qt Quick cancels every grab of the device's active points.
    QTouchEvent ev(QEvent::TouchCancel, device, modifiers);
    ev.setTimestamp(timestamp);
    QCoreApplication::sendEvent(window, &ev);
    auto* devicePrivate = QPointingDevicePrivate::get(const_cast<QPointingDevice*>(device));
    for (const auto& a : active)
        devicePrivate->removePointById(a.first);
    active.clear();
}

State* stateOf(void* handle)
{
    return handle ? static_cast<Window*>(handle)->offscreen : nullptr;
}

QQuickWindow* quickWindow(void* handle)
{
    return handle ? static_cast<Window*>(handle)->window : nullptr;
}

void setError(const std::string& message)
{
    ::setError(message);
}

} // namespace vlqt::offscreen

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
        g_log = log;
        qInstallMessageHandler(messageHandler);
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
        // Linux: Qt's default RHI backend (OpenGL; QSG_RHI_BACKEND overrides). Choosing a default is L4b.
        QSurfaceFormat fmt = QSurfaceFormat::defaultFormat();
        fmt.setSwapInterval(0);
        // No MSAA samples here: Qt gives a swap chain max(default, window) samples
        // (QSGRhiSupport::chooseSampleCountForWindowWithRhi), so a default count would be a floor for every window.
        // Each window sets its own count (vlqt_window_create_at, vlqt_window_set_samples).
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
        applyWslGpuDefault();
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
        ensureFonts();
#endif
#ifdef _WIN32
        static DialogCodeFilter dialogCodeFilter; // Enter/Tab/Esc/arrows past IsDialogMessage
        g_app->installNativeEventFilter(&dialogCodeFilter);
#endif

        // 7. Animation driver bound to vvvv time.
        g_driver = new VlqtAnimationDriver();
        g_driver->install();
        // Starts the driver at the next event processing and keeps it running (VlqtAnimationDriver); lives as long as g_driver.
        QUnifiedTimer::startAnimationTimer(new VlqtKeepAlive());

        registerPerfTypes();
        vlqt::registerStyleTypes();
        return 0;
    });
}

VLQT_API void vlqt_app_process_events(void)
{
    vlqt_app_process_events_for(nullptr, 0);
}

VLQT_API void vlqt_app_process_events_for(void* const* windows, int count)
{
    guarded([&]() -> int {
        if (!g_app)
            return -1;
        QElapsedTimer t;
        t.start();
        static std::vector<UpdateGate*> s_active; // reused, no allocation per pump
        // processEvents also dispatches the Win32 messages of the host thread; a host callback in there (vvvv
        // restart) may destroy a window of this list, so its frame is ended only if the monitor is still alive.
        static std::vector<QPointer<PerfMonitor>> s_perf;
        s_active.clear();
        s_perf.clear();
        for (int i = 0; windows && i < count; i++)
            if (auto* w = static_cast<Window*>(windows[i])) {
                s_active.push_back(w->gate);
                s_perf.emplace_back(w->perf);
            }
        // Device lost: also before anything renders, so no window builds a new QRhi on the removed device.
        vlqt::device::checkLost();
        vlqt::res::beginFrame(); // worker results + uploads within the budget, before the windows render
        vlqt::QtImageItem::afterResourceFrame(); // Image elements take new Ready content, drawn in this pump
        vlqt::ShaderEffectItem::afterResourceFrame(); // ShaderEffects take new Ready / Failed shaders
        UpdateGate::beginPump(!windows, s_active.data(), int(s_active.size()));
        QCoreApplication::sendPostedEvents();
        QCoreApplication::processEvents(QEventLoop::AllEvents);
        UpdateGate::endPump();
        // Every window frame of this pump has ended: no lock may outlive it (device.h, lock order).
        vlqt::device::releaseFrameLocks();
        vlqt::res::endFrame(); // frees resources without references, never in a worker
        fontsEndFrame();       // app fonts without references
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

VLQT_API int vlqt_app_deliver_input(void)
{
    return guarded([&]() -> int {
        if (!g_app) {
            setError("app not initialized");
            return -1;
        }
        // From inside the pump (a host callback in processEvents) the pump itself delivers.
        if (UpdateGate::inPump())
            return 0;
        // Windows: vvvv's message loop between two frames has run Qt's window procedure, which queued the input here
        // (or already delivered it on Qt's posted-events message). Delivery goes to the target window of each event;
        // events of a destroyed window are dropped by Qt (QPointer), so the window list of the pump is not needed.
        const int queued = QWindowSystemInterface::windowSystemEventsQueued();
        struct Delivery {
            Delivery() { UpdateGate::beginDelivery(); }
            ~Delivery() { UpdateGate::endDelivery(); }
        } delivery;
        if (queued > 0)
            QWindowSystemInterface::flushWindowSystemEvents(QEventLoop::AllEvents);
        // Also touch moves that the message loop between two frames already delivered to Qt Quick.
        UpdateGate::deliverDelayedTouch();
        return queued;
    });
}

VLQT_API int vlqt_driver_advance(double seconds)
{
    return guarded([&]() -> int {
        if (!g_driver) {
            setError("app not initialized");
            return -1;
        }
        // An animation started since the last tick (in the apply, e.g. a QML Behavior retargeted by a property set every
        // frame) enters the running list of QAnimationTimer only through a queued startAnimations() call. Without this the
        // call ran in processEvents after the tick, so an animation restarted every frame was never advanced and stood
        // still. Delivering the queued calls first gives it this tick, like Qt's own loop does before a vsync tick.
        QCoreApplication::sendPostedEvents(nullptr, QEvent::MetaCall);
        g_driver->setTime(seconds);
        g_driver->advance();
        return 0;
    });
}

VLQT_API const char* vlqt_last_error(void)
{
    return g_lastError.c_str();
}

VLQT_API void* vlqt_window_create(int width, int height, const char* title)
{
    const float nan = std::numeric_limits<float>::quiet_NaN();
    return vlqt_window_create_at(nan, nan, width, height, title, 0);
}

VLQT_API int vlqt_set_pipeline_cache(const char* utf8Path)
{
    g_pipelineCachePath = QString::fromUtf8(utf8Path ? utf8Path : "");
    return 0;
}

VLQT_API void* vlqt_window_create_at(float x, float y, int width, int height, const char* title, int swapInterval)
{
    Window* result = nullptr;
    guarded([&]() -> int {
        if (!g_app) {
            setError("app not initialized");
            return -1;
        }
        auto* w = new Window();
        w->engine = newEngine();
        w->window = new QQuickWindow();
        // Swap interval is read from the requested format when the swap chain is created (first show), so it cannot
        // change afterwards. 0 = Present never waits (default), >0 = VSync.
        // Start from the window's own format: QQuickWindow adds depth 24 + stencil 8 to the default format. Without
        // them the OpenGL default framebuffer has no depth buffer and Qt Quick's opaque pass (front to back, depth
        // test) inverts the stacking order (Linux/GL only, D3D11 always gets a depth-stencil).
        QSurfaceFormat winFmt = w->window->requestedFormat();
        winFmt.setSwapInterval(swapInterval > 0 ? 1 : 0);
        // MSAA: 4 by default, the look of every window before the count became a choice; also fixed per
        // QQuickWindow (vlqt_window_set_samples replaces the window).
        winFmt.setSamples(clampWindowSamples(4, w->window));
        w->window->setFormat(winFmt);
        placeWindow(w->window, x, y, width, height);
        w->window->setTitle(QString::fromUtf8(title ? title : ""));
        w->window->setIcon(defaultIcon());
        w->window->setColor(Qt::black);
        installWindowFilters(w, w->window);
        initShared(w);
        result = w;
        return 0;
    });
    return result;
}

VLQT_API void vlqt_window_destroy(void* p)
{
    guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w)
            return 0;
        // A scene unbinds first: its render target and opened texture belong to the QRhi of its render control.
        if (w->offscreen)
            vlqt::offscreen::release(w->offscreen);
        delete w->perf;
        delete w->scene;
        delete w->window;
        delete w->engine;
        if (w->offscreen)
            vlqt::offscreen::destroy(w->offscreen); // after its QQuickWindow: the render control and its QRhi
        delete w;
        QCoreApplication::sendPostedEvents(nullptr, QEvent::DeferredDelete);
        return 0;
    });
}

VLQT_API void vlqt_window_show(void* p)
{
    guarded([&]() -> int {
        if (auto* w = static_cast<Window*>(p); w && !w->offscreen) {
            if (w->mode & VLQT_WINDOW_MODE_FULLSCREEN)
                w->window->showFullScreen(); // show() = showNormal() would drop the state set before show
            else
                w->window->show();
        }
        return 0;
    });
}

VLQT_API int vlqt_window_set_icon(void* p, const char* utf8Path)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("null window");
            return -1;
        }
        if (w->offscreen)
            return 0;
        const QString path = QString::fromUtf8(utf8Path ? utf8Path : "");
        if (path.isEmpty()) {
            w->window->setIcon(defaultIcon());
            return 0;
        }
        // QIcon(file) accepts missing/broken files silently (null icon later), so probe with the image readers first.
        QIcon icon;
        if (QImageReader(path).canRead())
            icon = QIcon(path);
        if (icon.isNull()) {
            // The default icon is shown instead (also replaces a custom icon set before).
            w->window->setIcon(defaultIcon());
            setErrorQ(QStringLiteral("icon not loadable: %1").arg(path));
            return -7;
        }
        w->window->setIcon(icon);
        return 0;
    });
}

VLQT_API int vlqt_window_set_title(void* p, const char* title)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("null window");
            return -1;
        }
        if (!w->offscreen)
            w->window->setTitle(QString::fromUtf8(title ? title : ""));
        return 0;
    });
}

VLQT_API int vlqt_window_resize(void* p, int width, int height)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("null window");
            return -1;
        }
        if (w->offscreen) {
            setError("resize: an offscreen scene takes its size from vlqt_offscreen_bind");
            return VLQT_OFFSCREEN_BAD_ARGUMENT;
        }
        w->window->resize(width, height);
        return 0;
    });
}

VLQT_API void vlqt_window_hide(void* p)
{
    guarded([&]() -> int {
        if (auto* w = static_cast<Window*>(p); w && !w->offscreen)
            w->window->hide();
        return 0;
    });
}

VLQT_API int vlqt_window_take_close_request(void* p)
{
    auto* w = static_cast<Window*>(p);
    if (!w || !w->closeFilter || !w->closeFilter->requested) // a scene has no close filter
        return 0;
    w->closeFilter->requested = false;
    return 1;
}

VLQT_API int vlqt_window_is_visible(void* p)
{
    auto* w = static_cast<Window*>(p);
    return (w && !w->offscreen && w->window->isVisible()) ? 1 : 0;
}

#ifndef _WIN32
// W1-21: on Qt's offscreen platform with OpenGL, Mesa's software winsys can keep an old size of the window's default
// framebuffer (offscreen_glx.h), so a grab would read only part of the frame. Before a grab, make the window's context
// current (Qt sizes its X window to the window) and invalidate the drawable, so the grab's frame takes the window's size.
// Returns the GL context, or null on other platforms and backends.
static QOpenGLContext* refreshOffscreenGlx(QQuickWindow* window)
{
    if (QGuiApplication::platformName() != QLatin1String("offscreen"))
        return nullptr;
    QRhi* rhi = window->rhi();
    if (!rhi || rhi->backend() != QRhi::OpenGLES2)
        return nullptr;
    auto* handles = static_cast<const QRhiGles2NativeHandles*>(rhi->nativeHandles());
    QOpenGLContext* ctx = handles ? handles->context : nullptr;
    if (!ctx || !ctx->makeCurrent(window))
        return nullptr;
    static bool s_warned = false;
    if (!vlqt::glx::invalidateCurrentDrawable() && !s_warned) {
        s_warned = true;
        emitLog(2, "vlqt.grab", "offscreen GLX: no current drawable or libGL not found, a window grab may read a framebuffer of an old size");
    }
    return ctx;
}

// Returns false if the default framebuffer of the current context does not reach the window's last pixel (a grab then
// read memory that was never written).
static bool glxCoversWindow(QOpenGLContext* ctx, QQuickWindow* window)
{
    if (!ctx->makeCurrent(window))
        return true;
    const QSize px = window->size() * window->devicePixelRatio();
    auto* f = ctx->functions();
    f->glBindFramebuffer(GL_FRAMEBUFFER, ctx->defaultFramebufferObject());
    quint32 v = 0xDEADBEEF;
    f->glReadPixels(px.width() - 1, px.height() - 1, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, &v);
    return v != 0xDEADBEEF;
}
#endif

VLQT_API int vlqt_window_grab(void* p, const char* pngPath)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("null window");
            return -1;
        }
        if (w->offscreen)
            return notOnScene("grab");
        QImage img;
#ifndef _WIN32
        QOpenGLContext* glx = refreshOffscreenGlx(w->window);
#endif
        {
            // The frame of grabWindow ends without afterFrameEnd (QSGGuiThreadRenderLoop::grab).
            vlqt::device::ContextLock lock;
            img = w->window->grabWindow();
            vlqt::device::releaseFrameLocks();
        }
#ifndef _WIN32
        if (glx && !glxCoversWindow(glx, w->window)) {
            int w3, h3;
            vlqt::glx::currentDrawableSize(&w3, &h3);
            const QSize px = w->window->size() * w->window->devicePixelRatio();
            emitLog(2, "vlqt.grab", QStringLiteral("offscreen GLX framebuffer smaller than the window (%1 x %2 px, X window %3 x %4 px): grab %5 is incomplete")
                .arg(px.width()).arg(px.height()).arg(w3).arg(h3).arg(QString::fromUtf8(pngPath)).toUtf8());
        }
#endif
        if (img.isNull()) {
            setError("grabWindow returned null image");
            return -2;
        }
        if (!img.save(QString::fromUtf8(pngPath), "PNG")) {
            setError("saving png failed");
            return -3;
        }
        return 0;
    });
}

VLQT_API int vlqt_window_get_perf(void* p, vlqt_perf* out)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w || !out) {
            setError("null window or out");
            return -1;
        }
        w->perf->summary(out);
        return 0;
    });
}

VLQT_API int vlqt_window_get_perf_sample(void* p, vlqt_perf_sample* out)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w || !out) {
            setError("null window or out");
            return -1;
        }
        PerfSample s;
        if (!w->perf->lastSample(&s)) {
            *out = vlqt_perf_sample{};
            setError("no pump yet");
            return -2;
        }
        *out = vlqt_perf_sample{s.frame, s.pump, s.sync, s.render, s.present, s.gpu, s.events, s.swaps, s.vsync};
        return 0;
    });
}

VLQT_API int vlqt_window_get_perf_stats(void* p, vlqt_perf_stats* out)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w || !out) {
            setError("null window or out");
            return -1;
        }
        w->perf->stats(out);
        return 0;
    });
}

VLQT_API int vlqt_perf_eval(const vlqt_perf_sample* samples, const double* t_ms, int n, double budget_ms, vlqt_perf_stats* out)
{
    return guarded([&]() -> int {
        if (!out || n < 0 || (n > 0 && (!samples || !t_ms))) {
            setError("null or negative argument");
            return -1;
        }
        perfEval(samples, t_ms, n, budget_ms, out);
        return 0;
    });
}

VLQT_API int vlqt_window_report_host_frame(void* p, const vlqt_host_frame* in)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w || !in) {
            setError("null window or in");
            return -1;
        }
        if (!w->perf->reportHostFrame(*in)) {
            setError("no pump yet");
            return -2;
        }
        return 0;
    });
}

VLQT_API int vlqt_window_get_host_frame(void* p, vlqt_host_frame* out)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w || !out) {
            setError("null window or out");
            return -1;
        }
        if (!w->perf->lastHostFrame(out)) {
            *out = vlqt_host_frame{};
            setError("no host frame reported for the last pump");
            return -2;
        }
        return 0;
    });
}

VLQT_API int vlqt_window_get_host_stats(void* p, vlqt_host_stats* out)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w || !out) {
            setError("null window or out");
            return -1;
        }
        w->perf->hostStats(out);
        return 0;
    });
}

VLQT_API int vlqt_perf_host_eval(const vlqt_host_frame* frames, int n, vlqt_host_stats* out)
{
    return guarded([&]() -> int {
        if (!out || n < 0 || (n > 0 && !frames)) {
            setError("null or negative argument");
            return -1;
        }
        perfHostStats(frames, n, out);
        return 0;
    });
}

VLQT_API int vlqt_window_set_overlay(void* p, int flags)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("null window");
            return -1;
        }
        w->perf->setOverlay(flags);
        return 0;
    });
}

VLQT_API int vlqt_window_get_overlay(void* p)
{
    auto* w = static_cast<Window*>(p);
    return w ? w->perf->overlay() : 0;
}

VLQT_API int vlqt_window_set_debug(void* p, int on)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("null window");
            return -1;
        }
        w->scene->setDebugAll(on != 0);
        w->window->update();
        return 0;
    });
}

VLQT_API int vlqt_window_get_debug(void* p)
{
    auto* w = static_cast<Window*>(p);
    return w && w->scene->debugAll ? 1 : 0;
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
static void applyFrameless(Window* w, bool on)
{
    QQuickWindow* win = w->window;
    const Qt::WindowFlags flags = on ? (win->flags() | Qt::FramelessWindowHint) : (win->flags() & ~Qt::FramelessWindowHint);
    if (flags == win->flags())
        return;
    const bool keepClient = win->handle() && (win->windowStates() & (Qt::WindowMinimized | Qt::WindowMaximized | Qt::WindowFullScreen)) == 0;
#ifdef _WIN32
    // Windows: QWindow::setGeometry does not work right after setFlags (Qt still has the old frame margins and a stale
    // cached geometry; frame on: the new style only takes effect later, the outer rect stays = old client rect). So the
    // outer rect for the unchanged client rect is computed from the new style (AdjustWindowRectExForDpi, includes the
    // invisible resize borders) and set with SWP_FRAMECHANGED; Qt follows from WM_NCCALCSIZE/WM_MOVE/WM_SIZE.
    if (!keepClient) {
        win->setFlags(flags);
        return;
    }
    const HWND hwnd = reinterpret_cast<HWND>(win->winId());
    RECT c {};
    GetClientRect(hwnd, &c);
    MapWindowPoints(hwnd, nullptr, reinterpret_cast<POINT*>(&c), 2);
    win->setFlags(flags);
    RECT r = c;
    const DWORD style = DWORD(GetWindowLongPtrW(hwnd, GWL_STYLE)), exStyle = DWORD(GetWindowLongPtrW(hwnd, GWL_EXSTYLE));
    AdjustWindowRectExForDpi(&r, style, FALSE, exStyle, GetDpiForWindow(hwnd));
    SetWindowPos(hwnd, nullptr, r.left, r.top, r.right - r.left, r.bottom - r.top,
        SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE);
#else
    const QRect client = win->geometry();
    win->setFlags(flags);
    if (!keepClient)
        return;
    // xcb: the window manager re-frames asynchronously; client geometry requested frame exclusive.
    // QWindow::setGeometry returns early for an unchanged rect, so the platform window would never see it. offscreen
    // (CI) recomputes its frame margins (2 px) only in QOffscreenWindow::setGeometry (no setWindowFlags), so the
    // reported frame position kept the old margins and wandered by them on every toggle. The client rect is therefore
    // passed to the platform window directly, frame exclusive, as QWindow::setGeometry would.
    if (win->geometry() != client)
        win->setGeometry(client);
    else if (QPlatformWindow* pw = win->handle()) {
        QWindowPrivate::get(win)->positionPolicy = QWindowPrivate::WindowFrameExclusive;
        pw->setGeometry(QHighDpi::toNativeWindowGeometry(client, win));
    }
#endif
}

// Borderless pin: while fullscreen only w->mode remembers it (applied by the fullscreen part on leaving fullscreen).
static void applyBorderless(Window* w, bool on)
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
static void applyFullscreen(Window* w, bool on)
{
    QQuickWindow* win = w->window;
    const bool isFs = win->windowStates().testFlag(Qt::WindowFullScreen);
    if (on) {
        if (isFs)
            return;
        w->fsFramePos = win->framePosition();
        w->fsSize = win->size();
        w->fsStates = win->windowStates() & ~(Qt::WindowFullScreen | Qt::WindowMinimized);
        w->fsSaved = true;
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
    if (!isFs && !w->fsSaved)
        return;
    win->setWindowStates(w->fsSaved ? w->fsStates : Qt::WindowNoState);
    if (w->fsSaved) {
        // Borderless state from w->mode (changes during fullscreen are only kept there).
        applyFrameless(w, w->mode & VLQT_WINDOW_MODE_BORDERLESS);
        if (!w->fsStates.testFlag(Qt::WindowMaximized)) { // maximized: Qt keeps the normal geometry itself
            win->setFramePosition(w->fsFramePos);
            win->resize(w->fsSize);
        }
        w->fsSaved = false;
    }
}

VLQT_API int vlqt_window_set_mode(void* p, unsigned int mask, unsigned int value)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("null window");
            return -1;
        }
        if (w->offscreen)
            return 0;
        mask &= VLQT_WINDOW_MODE_BORDERLESS | VLQT_WINDOW_MODE_FULLSCREEN;
        const unsigned changed = (w->mode ^ value) & mask;
        w->mode = (w->mode & ~mask) | (value & mask);
        if (changed & VLQT_WINDOW_MODE_BORDERLESS)
            applyBorderless(w, w->mode & VLQT_WINDOW_MODE_BORDERLESS);
        if (changed & VLQT_WINDOW_MODE_FULLSCREEN)
            applyFullscreen(w, w->mode & VLQT_WINDOW_MODE_FULLSCREEN);
        return 0;
    });
}

// Window swap (vlqt_window_set_samples; docs/decisions/render-texture.md, "MSAA"). The basic render loop fixes a
// window's sample count when it creates the window's QRhi (QSGGuiThreadRenderLoop::ensureRhi: the swap chain and the
// render context that layers use); QWindow::destroy()/show() recreates the swap chain with the old count. So the handle
// gets a new QQuickWindow with format f: same screen, geometry, window states, flags, title, icon, colour, filters,
// graphics configuration and shared device. The scene moves over (items, their state and the QML engine stay), a new
// PerfMonitor takes the overlay state, windows with the old one as transient parent (Popup.Window popups, QML windows)
// get the new one, then the old window is deleted. Input needs nothing else: the WM_GETDLGCODE filter looks the HWND up
// per message. The new native window is shown without activation unless the old one was active.
static void replaceWindow(Window* w, const QSurfaceFormat& f)
{
    QQuickWindow* old = w->window;
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
    const UpdateGate* oldGate = w->gate;
    w->window = nw;
    installWindowFilters(w, nw);
    w->closeFilter->requested = closeRequested;
    w->gate->continueFrom(*oldGate);
    nw->setGraphicsConfiguration(old->graphicsConfiguration());
    vlqt::device::attach(nw);
    w->scene->moveToWindow(nw);
    const int overlay = w->perf->overlay();
    delete w->perf;
    w->perf = new PerfMonitor(nw, w->engine);
    w->perf->setScene(w->scene);
    if (overlay & (PerfMonitor::Meter | PerfMonitor::Stats))
        w->perf->setOverlay(overlay);
    for (QWindow* t : QGuiApplication::topLevelWindows())
        if (t->transientParent() == old)
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
    delete old;
    QCoreApplication::sendPostedEvents(nullptr, QEvent::DeferredDelete);
}

VLQT_API int vlqt_window_set_samples(void* p, int requested)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("null window");
            return -1;
        }
        if (w->offscreen) {
            setError("set_samples: an offscreen scene takes its sample count from vlqt_offscreen_bind");
            return VLQT_OFFSCREEN_BAD_ARGUMENT;
        }
        const int samples = clampWindowSamples(requested, w->window);
        QSurfaceFormat f = w->window->requestedFormat();
        if (qMax(1, f.samples()) == samples)
            return samples;
        f.setSamples(samples);
        // No native window yet (never shown): no swap chain either, Qt reads the format at the first expose.
        if (!w->window->handle())
            w->window->setFormat(f);
        else
            replaceWindow(w, f);
        return samples;
    });
}

VLQT_API int vlqt_window_samples(void* p)
{
    auto* w = static_cast<Window*>(p);
    if (!w || w->offscreen)
        return 0;
    if (const QRhiSwapChain* sc = w->window->swapChain())
        return sc->sampleCount();
    return qMax(1, w->window->requestedFormat().samples());
}

VLQT_API unsigned int vlqt_window_get_mode(void* p)
{
    auto* w = static_cast<Window*>(p);
    return w ? w->mode : 0;
}

VLQT_API int vlqt_window_get_scene_metrics(void* p, vlqt_scene_metrics* out)
{
    auto* w = static_cast<Window*>(p);
    if (!w || !w->scene || !out)
        return -1;
    // The root is sized to the window (Scene::attachRoot), also offscreen where the content item stays 0 x 0.
    const QQuickItem* root = w->scene->root;
    out->width = float(root->width());
    out->height = float(root->height());
    out->dpr = w->offscreen ? 1.f : float(w->window->effectiveDevicePixelRatio());
    return 0;
}

VLQT_API int vlqt_window_apply(void* p, const unsigned char* buf, int len)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("apply: null window");
            return vlqt::ApplyNullWindow;
        }
        std::string err;
        const int rc = w->scene->apply(buf, len, err);
        if (rc != 0)
            setError(err);
        w->scene->updateDebug(); // show/hide/repaint the debug overlay, no-op while debug was never used
        // Commands changed the scene: request the render ourselves. Inside a MultiEffect source (layer)
        // Qt stops requesting updates for changed items after the first frames; the UpdateGate keeps it at one per pump.
        if (len > 0) {
            w->window->update();
            w->gate->markChanged(); // also changes without a dirty item (clear color)
        }
        return rc;
    });
}

VLQT_API int vlqt_window_get_apply_error(void* p, vlqt_apply_error* out)
{
    auto* w = static_cast<Window*>(p);
    if (!w || !out)
        return -1;
    const vlqt::Scene::ApplyError& e = w->scene->lastApplyError;
    *out = vlqt_apply_error{e.rc, e.command, e.id, e.op, e.prop, e.reason.c_str()};
    return 0;
}

VLQT_API int vlqt_window_poll(void* p, unsigned char* out, int cap)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("poll: null window");
            return -1;
        }
        // No render in this pump (Wayland: UpdateRequest only on the compositor's frame callback; not exposed,
        // e.g. minimized): Qt has not polished, layouts would report stale geometry.
        if (!w->gate->rendered())
            w->scene->polish();
        return w->scene->poll(out, cap);
    });
}

VLQT_API int vlqt_test_send_mouse(void* p, int type, float x, float y)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("test_send_mouse: null window");
            return -1;
        }
        if (w->offscreen)
            return notOnScene("test_send_mouse");
        if (type == VLQT_MOUSE_LEAVE) {
            QEvent ev(QEvent::Leave);
            QCoreApplication::sendEvent(w->window, &ev);
            return 0;
        }
        QEvent::Type t;
        Qt::MouseButton button = Qt::NoButton;
        Qt::MouseButtons buttons = Qt::NoButton;
        Qt::MouseEventSource source = Qt::MouseEventNotSynthesized;
        switch (type) {
        case VLQT_MOUSE_MOVE: t = QEvent::MouseMove; buttons = w->testButtons; break;
        case VLQT_MOUSE_PRESS: t = QEvent::MouseButtonPress; button = Qt::LeftButton; buttons = Qt::LeftButton; break;
        case VLQT_MOUSE_RELEASE: t = QEvent::MouseButtonRelease; button = Qt::LeftButton; break;
        case VLQT_MOUSE_PRESS_RIGHT: t = QEvent::MouseButtonPress; button = Qt::RightButton; buttons = Qt::RightButton; break;
        case VLQT_MOUSE_RELEASE_RIGHT: t = QEvent::MouseButtonRelease; button = Qt::RightButton; break;
        case VLQT_MOUSE_PRESS_RIGHT_SYSTEM:
            t = QEvent::MouseButtonPress; button = Qt::RightButton; buttons = Qt::RightButton; source = Qt::MouseEventSynthesizedBySystem;
            break;
        case VLQT_MOUSE_RELEASE_RIGHT_SYSTEM:
            t = QEvent::MouseButtonRelease; button = Qt::RightButton; source = Qt::MouseEventSynthesizedBySystem;
            break;
        default:
            setError("test_send_mouse: unknown type " + std::to_string(type));
            return -2;
        }
        w->testButtons = buttons;
        const QPointF local(x, y);
        QMouseEvent ev(t, local, local, w->window->mapToGlobal(local), button, buttons, Qt::NoModifier, source);
        if (!w->testClock.isValid())
            w->testClock.start();
        ev.setTimestamp(quint64(w->testClock.elapsed()) + 1); // monotonic (TapHandler compares timestamps)
        QCoreApplication::sendEvent(w->window, &ev);
        return 0;
    });
}

VLQT_API int vlqt_test_send_wheel(void* p, float x, float y, int dx, int dy)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("test_send_wheel: null window");
            return -1;
        }
        if (w->offscreen)
            return notOnScene("test_send_wheel");
        const QPointF local(x, y);
        QWheelEvent ev(local, w->window->mapToGlobal(local), QPoint(), QPoint(dx, dy), w->testButtons, Qt::NoModifier,
            Qt::NoScrollPhase, false);
        if (!w->testClock.isValid())
            w->testClock.start();
        ev.setTimestamp(quint64(w->testClock.elapsed()) + 1);
        QCoreApplication::sendEvent(w->window, &ev);
        return 0;
    });
}

// TEST ONLY: one touchscreen for vlqt_test_send_touch, registered once (child of the app, recreated with it).
static const QPointingDevice* testTouchDevice()
{
    static QPointer<QPointingDevice> s_device;
    if (!s_device) {
        s_device = new QPointingDevice(QStringLiteral("vlqt test touchscreen"), testTouchSystemId, QInputDevice::DeviceType::TouchScreen,
            QPointingDevice::PointerType::Finger, QInputDevice::Capability::Position | QInputDevice::Capability::Area, 10, 0,
            QString(), QPointingDeviceUniqueId(), QCoreApplication::instance());
        QWindowSystemInterface::registerInputDevice(s_device);
    }
    return s_device;
}

VLQT_API int vlqt_test_send_touch(void* p, int type, int id, float x, float y)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("test_send_touch: null window");
            return -1;
        }
        if (w->offscreen)
            return notOnScene("test_send_touch");
        QEventPoint::State state;
        switch (type) {
        case VLQT_TOUCH_PRESS: state = QEventPoint::State::Pressed; break;
        case VLQT_TOUCH_MOVE: state = QEventPoint::State::Updated; break;
        case VLQT_TOUCH_RELEASE: state = QEventPoint::State::Released; break;
        default:
            setError("test_send_touch: unknown type " + std::to_string(type));
            return -2;
        }
        if (!w->testClock.isValid())
            w->testClock.start();
        const ulong ts = ulong(w->testClock.elapsed()) + 1; // monotonic (TapHandler compares timestamps)
        return vlqt::offscreen::sendTouch(w->window, testTouchDevice(), w->testTouch, state, id, QPointF(x, y), ts,
                   Qt::NoModifier, "test_send_touch")
            ? 0
            : -2;
    });
}

// TEST ONLY: one mouse for vlqt_test_queue_mouse, registered once like the touchscreen.
static const QPointingDevice* testMouseDevice()
{
    static QPointer<QPointingDevice> s_device;
    if (!s_device) {
        s_device = new QPointingDevice(QStringLiteral("vlqt test mouse"), testMouseSystemId, QInputDevice::DeviceType::Mouse,
            QPointingDevice::PointerType::Generic, QInputDevice::Capability::Position | QInputDevice::Capability::Hover, 1, 3,
            QString(), QPointingDeviceUniqueId(), QCoreApplication::instance());
        QWindowSystemInterface::registerInputDevice(s_device);
    }
    return s_device;
}

VLQT_API int vlqt_test_queue_mouse(void* p, int type, float x, float y)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("test_queue_mouse: null window");
            return -1;
        }
        if (w->offscreen)
            return notOnScene("test_queue_mouse");
        QEvent::Type t;
        Qt::MouseButton button = Qt::NoButton;
        Qt::MouseButtons buttons = w->testQueuedButtons;
        switch (type) {
        case VLQT_MOUSE_MOVE: t = QEvent::MouseMove; break;
        case VLQT_MOUSE_PRESS: t = QEvent::MouseButtonPress; button = Qt::LeftButton; buttons |= Qt::LeftButton; break;
        case VLQT_MOUSE_RELEASE: t = QEvent::MouseButtonRelease; button = Qt::LeftButton; buttons &= ~Qt::LeftButton; break;
        default:
            setError("test_queue_mouse: unknown type " + std::to_string(type));
            return -2;
        }
        w->testQueuedButtons = buttons;
        if (!w->testClock.isValid())
            w->testClock.start();
        const QPointF local(x, y);
        // Native pixels, like the platform plugin hands them over (QWindowSystemInterface scales them back).
        QWindowSystemInterface::handleMouseEvent<QWindowSystemInterface::AsynchronousDelivery>(w->window,
            ulong(w->testClock.elapsed()) + 1, testMouseDevice(), QHighDpi::toNativeLocalPosition(local, w->window),
            QHighDpi::toNativeGlobalPosition(QPointF(w->window->mapToGlobal(local)), w->window), buttons, button, t);
        return 0;
    });
}

VLQT_API int vlqt_test_queue_touch(void* p, int type, int id, float x, float y)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("test_queue_touch: null window");
            return -1;
        }
        if (w->offscreen)
            return notOnScene("test_queue_touch");
        auto& active = w->testQueuedTouch;
        auto it = std::find_if(active.begin(), active.end(), [id](const auto& a) { return a.first == id; });
        const bool known = it != active.end();
        QEventPoint::State state;
        switch (type) {
        case VLQT_TOUCH_PRESS: state = QEventPoint::State::Pressed; break;
        case VLQT_TOUCH_MOVE: state = QEventPoint::State::Updated; break;
        case VLQT_TOUCH_RELEASE: state = QEventPoint::State::Released; break;
        default:
            setError("test_queue_touch: unknown type " + std::to_string(type));
            return -2;
        }
        if (known == (type == VLQT_TOUCH_PRESS)) {
            setError("test_queue_touch: point " + std::to_string(id) + (known ? " is already pressed" : " is not pressed"));
            return -2;
        }
        const QPointF pos(x, y);
        if (known)
            it->second = pos;
        else
            active.emplace_back(id, pos);
        if (!w->testClock.isValid())
            w->testClock.start();
        // The complete touch state like a touchscreen driver: every active point, the changed one with its state.
        // QGuiApplication registers and forgets the points at the device itself (not the sendEvent path).
        QList<QWindowSystemInterface::TouchPoint> points;
        for (const auto& a : active) {
            QWindowSystemInterface::TouchPoint tp;
            tp.id = a.first;
            tp.state = a.first == id ? state : QEventPoint::State::Stationary;
            const QPointF global = QHighDpi::toNativeGlobalPosition(QPointF(w->window->mapToGlobal(a.second)), w->window);
            tp.area = QRectF(global - QPointF(2, 2), QSizeF(4, 4));
            tp.pressure = tp.state == QEventPoint::State::Released ? 0 : 1;
            points.append(tp);
        }
        QWindowSystemInterface::handleTouchEvent<QWindowSystemInterface::AsynchronousDelivery>(w->window,
            ulong(w->testClock.elapsed()) + 1, testTouchDevice(), points);
        if (type == VLQT_TOUCH_RELEASE)
            active.erase(std::find_if(active.begin(), active.end(), [id](const auto& a) { return a.first == id; }));
        return 0;
    });
}

VLQT_API int vlqt_test_handler_count(void* p, unsigned int id)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("test_handler_count: null window");
            return -1;
        }
        auto it = w->scene->nodes.find(id);
        if (it == w->scene->nodes.end()) {
            setError("test_handler_count: unknown id " + std::to_string(id));
            return vlqt::ApplyUnknownId;
        }
        int n = 0;
        for (QObject* c : it->second.item->children())
            if (c->inherits("QQuickPointerHandler"))
                ++n;
        return n;
    });
}

VLQT_API int vlqt_test_perf_spike(void* p, int phase, float ms)
{
    auto* w = static_cast<Window*>(p);
    if (!w) {
        setError("test_perf_spike: null window");
        return -1;
    }
    w->perf->injectSpike(phase, ms);
    return 0;
}

// header text of the F5 stats page as UTF-8 (HTML); returns the full length, copies up to len - 1 bytes.
VLQT_API int vlqt_test_perf_stats_text(void* p, char* buf, int len)
{
    auto* w = static_cast<Window*>(p);
    if (!w) {
        setError("test_perf_stats_text: null window");
        return -1;
    }
    const QByteArray t = w->perf->statsText().toUtf8();
    if (buf && len > 0) {
        const int n = std::min<int>(len - 1, int(t.size()));
        std::memcpy(buf, t.constData(), size_t(n));
        buf[n] = 0;
    }
    return int(t.size());
}

VLQT_API int vlqt_test_perf_text(void* p, int page, char* out, int cap)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("test_perf_text: null window");
            return -1;
        }
        const QByteArray utf8 = w->perf->pageText(page).toUtf8();
        const int n = int(utf8.size());
        if (!out || cap < n + 1)
            return -(n + 1);
        std::memcpy(out, utf8.constData(), size_t(n) + 1);
        return n;
    });
}

VLQT_API int vlqt_test_device_info(void* p, void** device, void** rhi)
{
    return guarded([&]() -> int {
        QRhi* r = p ? static_cast<Window*>(p)->window->rhi() : vlqt::device::uploadRhi();
        if (device)
            *device = vlqt::device::nativeDevice(r);
        if (rhi)
            *rhi = r;
        return r ? 0 : -2;
    });
}

VLQT_API int vlqt_test_device_lost(int mode)
{
    return guarded([&]() -> int { return vlqt::device::testLost(mode); });
}

VLQT_API int vlqt_test_send_key(void* p, int key, int modifiers)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("test_send_key: null window");
            return -1;
        }
        if (w->offscreen)
            return notOnScene("test_send_key");
        QKeyEvent ev(QEvent::KeyPress, key, Qt::KeyboardModifiers(modifiers));
        QCoreApplication::sendEvent(testKeyTarget(w), &ev);
        return 0;
    });
}

VLQT_API int vlqt_test_send_key_text(void* p, int key, int modifiers, const char* text)
{
    return guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w) {
            setError("test_send_key_text: null window");
            return -1;
        }
        if (w->offscreen)
            return notOnScene("test_send_key_text");
        const QString t = text ? QString::fromUtf8(text) : QString();
        QKeyEvent press(QEvent::KeyPress, key, Qt::KeyboardModifiers(modifiers), t);
        QCoreApplication::sendEvent(testKeyTarget(w), &press);
        QKeyEvent release(QEvent::KeyRelease, key, Qt::KeyboardModifiers(modifiers), t);
        QCoreApplication::sendEvent(testKeyTarget(w), &release);
        return 0;
    });
}

// Resources: thin wrappers, the manager logs its own warnings (category VL.Qt.Resources).
VLQT_API int vlqt_res_create(int kind, unsigned long long* outId)
{
    return guarded([&] {
        uint64_t id = 0;
        const int rc = vlqt::res::create(kind, &id);
        if (outId)
            *outId = id;
        return rc;
    });
}

VLQT_API int vlqt_res_release(unsigned long long id)
{
    return guarded([&] { return vlqt::res::release(id); });
}

VLQT_API int vlqt_res_set_budget(int bytes)
{
    return guarded([&] { return vlqt::res::setBudget(bytes); });
}

VLQT_API int vlqt_res_poll(unsigned char* out, int cap)
{
    return guarded([&] { return vlqt::res::poll(out, cap); });
}

VLQT_API int vlqt_res_wait(unsigned long long id, int timeoutMs)
{
    return guarded([&] { return vlqt::res::wait(id, timeoutMs); });
}

VLQT_API int vlqt_res_list(vlqt_res_info* out, int cap)
{
    return guarded([&] { return vlqt::res::list(out, cap); });
}

VLQT_API void vlqt_res_shutdown(void)
{
    guarded([&] {
        vlqt::res::shutdown();
        return 0;
    });
}

VLQT_API int vlqt_res_load_image(unsigned long long id, const char* utf8Path, unsigned flags)
{
    return guarded([&] { return vlqt::res::image::load(id, utf8Path, flags); });
}

VLQT_API int vlqt_res_set_pixels(unsigned long long id, const vlqt_pixel_desc* desc, const void* data, int len)
{
    return guarded([&] { return vlqt::res::image::setPixels(id, desc, data, len); });
}

VLQT_API int vlqt_res_load_shader(unsigned long long id, const char* utf8Path, int stage)
{
    return guarded([&] { return vlqt::res::shader::load(id, utf8Path, stage); });
}

VLQT_API int vlqt_res_load_shader_source(unsigned long long id, const char* utf8Source, int stage)
{
    return guarded([&] { return vlqt::res::shader::loadSource(id, utf8Source, stage); });
}

VLQT_API int vlqt_shader_reflect(const char* utf8Source, int stage, char* buf, int bufLen)
{
    return guarded([&] { return vlqt::res::shader::reflect(utf8Source, stage, buf, bufLen); });
}

VLQT_API int vlqt_res_snapshot(unsigned long long id, void* p, unsigned elementId, float scale)
{
    return guarded([&] {
        auto* w = static_cast<Window*>(p);
        if (!w || !w->scene) {
            setError("snapshot: null window");
            return vlqt::res::badValueRc;
        }
        return vlqt::res::snap::start(id, *w->scene, elementId, scale);
    });
}

VLQT_API int vlqt_res_read_pixels(unsigned long long id, void* dst, int cap, vlqt_pixel_desc* out)
{
    return guarded([&] { return vlqt::res::snap::readPixels(id, dst, cap, out); });
}

VLQT_API int vlqt_res_read_preview(unsigned long long id, unsigned char* dst, int cap, vlqt_pixel_desc* desc)
{
    return guarded([&] { return vlqt::res::image::readPreview(id, dst, cap, desc); });
}

// Audio taps: audio_tap.h; read runs on VL.Audio's audio thread.
VLQT_API int vlqt_res_audio_tap_init(unsigned long long id, int channels, int sampleRate)
{
    return guarded([&] { return vlqt::res::audio::init(id, channels, sampleRate); });
}

VLQT_API int vlqt_res_read_audio(unsigned long long id, float* dst, int maxFrames, int channels, vlqt_audio_tap_state* state)
{
    return guarded([&] { return vlqt::res::audio::read(id, dst, maxFrames, channels, state); });
}

VLQT_API long long vlqt_now_ns(void)
{
    return vlqt::res::audio::nowNs();
}

VLQT_API int vlqt_test_res_read_pixels(unsigned long long id, unsigned char* out, int cap, int* width, int* height)
{
    return guarded([&] { return vlqt::res::image::testRead(id, out, cap, width, height); });
}

VLQT_API int vlqt_image_write(const vlqt_pixel_desc* desc, const void* data, int len, const char* utf8Path, int quality, char* err, int errCap)
{
    return guarded([&] { return vlqt::imagewriter::write(desc, data, len, utf8Path, quality, err, errCap); });
}

VLQT_API int vlqt_test_write_image(const char* utf8Path, int width, int height, int quality)
{
    return guarded([&] { return vlqt::res::image::testWrite(utf8Path, width, height, quality); });
}

VLQT_API int vlqt_test_res_start(unsigned long long id, int decodeMs, long long uploadBytes, int flags)
{
    return guarded([&] { return vlqt::res::testStart(id, decodeMs, uploadBytes, flags); });
}

VLQT_API int vlqt_test_res_transition(unsigned long long id, int from, int to)
{
    return guarded([&] { return vlqt::res::testTransition(id, from, to); });
}

VLQT_API int vlqt_test_res_element_ref(unsigned long long id, int delta)
{
    return guarded([&] { return vlqt::res::testElementRef(id, delta); });
}

// Tooltip previews (package 01).
VLQT_API long long vlqt_window_change_count(void* p)
{
    long long count = vlqt::res::staleIdRc;
    const int rc = guarded([&]() -> int {
        auto* w = static_cast<Window*>(p);
        if (!w)
            return vlqt::res::staleIdRc;
        count = w->gate->changeCount();
        return 0;
    });
    return rc < 0 ? rc : count;
}

VLQT_API int vlqt_res_snapshot_fit(unsigned long long id, void* p, unsigned elementId, int maxEdgePx)
{
    return guarded([&] {
        auto* w = static_cast<Window*>(p);
        if (!w || !w->scene) {
            setError("snapshot_fit: null window");
            return vlqt::res::badValueRc;
        }
        return vlqt::res::snap::startFit(id, *w->scene, elementId, maxEdgePx);
    });
}

VLQT_API int vlqt_font_add(const char* utf8Path, unsigned long long* outHandle, char* families, int familiesCap)
{
    return guarded([&]() -> int {
        if (outHandle)
            *outHandle = 0;
        if (families && familiesCap > 0)
            families[0] = 0;
        if (!g_app) {
            setError("font_add: app not initialized");
            return -1;
        }
        if (QThread::currentThread() != g_app->thread()) {
            setError("font_add: not on the Qt GUI thread (the vvvv main thread)");
            return -1;
        }
        if (!utf8Path || !outHandle) {
            setError("font_add: null argument");
            return -1;
        }
        const QFileInfo info(QString::fromUtf8(utf8Path));
        if (!info.isFile()) {
            setErrorQ(QStringLiteral("font file not found: %1").arg(info.filePath()));
            return -1;
        }
        QString key = info.canonicalFilePath();
#ifdef _WIN32
        key = key.toLower(); // NTFS paths are case-insensitive
#endif
        key += QStringLiteral("|%1|%2").arg(info.size()).arg(info.lastModified().toMSecsSinceEpoch());
        unsigned long long handle = 0;
        FontEntry* entry = nullptr;
        for (auto& [h, e] : g_fonts)
            if (e.key == key) {
                handle = h;
                entry = &e;
                break;
            }
        if (!entry) {
            const int id = QFontDatabase::addApplicationFont(info.filePath());
            const QStringList names = id < 0 ? QStringList() : QFontDatabase::applicationFontFamilies(id);
            if (names.isEmpty()) {
                if (id >= 0)
                    QFontDatabase::removeApplicationFont(id);
                setErrorQ(QStringLiteral("not a font file Qt can read: %1").arg(info.filePath()));
                return -1;
            }
            FontEntry e;
            e.key = key;
            e.qtId = id;
            e.families = names.join(QLatin1Char('\n')).toUtf8();
            handle = g_nextFontHandle++;
            g_fonts.emplace_back(handle, std::move(e));
            entry = &g_fonts.back().second;
            emitLog(1, "vlqt.font", "Font registered: " + entry->families + " (" + info.filePath().toUtf8() + ")");
        }
        entry->refs++;
        *outHandle = handle;
        if (families && familiesCap > 0) {
            const int n = std::min(int(entry->families.size()), familiesCap - 1);
            std::memcpy(families, entry->families.constData(), size_t(n));
            families[n] = 0;
        }
        return 0;
    });
}

VLQT_API int vlqt_font_release(unsigned long long handle)
{
    return guarded([&]() -> int {
        if (g_app && QThread::currentThread() != g_app->thread()) {
            setError("font_release: not on the Qt GUI thread (the vvvv main thread)");
            return -1;
        }
        FontEntry* e = findFont(handle);
        if (!e || e->refs <= 0) {
            setError("font_release: unknown handle " + std::to_string(handle));
            return -9;
        }
        e->refs--; // removed at the end of the pump if still 0 (fontsEndFrame)
        return 0;
    });
}

VLQT_API int vlqt_test_font_refs(unsigned long long handle)
{
    const FontEntry* e = findFont(handle);
    return e ? e->refs : -9;
}

} // extern "C"

// At process exit the CLR is already gone when Qt's static destructors log.
// vlqt.dll detaches before Qt6Core (it depends on it), so cut the managed callback here.
#ifdef _WIN32
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_DETACH)
        g_log = nullptr;
    return TRUE;
}
#else
// Same on ELF: libvlqt.so's destructors run before those of libQt6Core.so (reverse dependency order).
__attribute__((destructor)) static void vlqtUnload()
{
    g_log = nullptr;
}
#endif

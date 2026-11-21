// Test exports of vlqt_test.h: synthetic mouse, touch, wheel and key input into on-screen windows (SmokeTest). Qt GUI
// thread; VLQT_ERR_UNSUPPORTED on an offscreen scene, which takes input through vlqt_offscreen_send_*.
#include "../vlqt_test.h"
#include "../app/exports.h"
#include "offscreen/offscreen.h" // sendTouch
#include "../protocol.h"
#include "test_hooks.h"
#include "platform/platform.h"

#include <QtCore/QCoreApplication>
#include <QtCore/QPointer>
#include <QtGui/QGuiApplication>
#include <QtGui/QMouseEvent>
#include <QtGui/QPointingDevice>
#include <QtQuick/QQuickWindow>
#include <QtGui/qpa/qwindowsysteminterface.h>
// Native positions of queued input: private API, check on Qt updates.
#include <QtGui/private/qhighdpiscaling_p.h>

#include <algorithm>
#include <memory>
#include <string>

using vlqt::fail;
using vlqt::SceneHandle;
using vlqt::withWindow;

namespace {

// System ids of the test devices: their queued (spontaneous) input passes the SystemInputFilter.
constexpr qint64 testTouchSystemId = 0x766c7174;
constexpr qint64 testMouseSystemId = 0x766c716d;

// With VLQT_TEST_SYNTHETIC_INPUT=1 the window drops spontaneous (OS) pointer/hover events; input from
// vlqt_test_send_mouse (sendEvent, not spontaneous) passes, and so does queued input of the test devices
// (vlqt_test_queue_mouse / _touch, spontaneous like platform input).
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

// vlqt_test_send_key*: deliver like the platform does, to the focus window. A Qt Quick popup of type Popup.Window
// (e.g. the TextField context menu under Linux offscreen) is its own Qt::Popup window and holds the keyboard
// focus; sent to the main window, Escape would never close it and all later keys would be lost. Once the popup is
// hidden, a window manager gives the focus back to the main window; the offscreen QPA has none and leaves it on the
// hidden popup (main window inactive, no active focus item), so the test hands it back here.
QWindow* testKeyTarget(QQuickWindow* window)
{
    QWindow* f = QGuiApplication::focusWindow();
    if (!f || f == window || f->type() != Qt::Popup || f->transientParent() != window)
        return window;
    if (f->isVisible())
        return f;
    QWindowSystemInterface::handleFocusWindowChanged<QWindowSystemInterface::SynchronousDelivery>(window, Qt::PopupFocusReason);
    return window;
}

// One touchscreen for vlqt_test_send_touch / _queue_touch, registered once (child of the app, recreated with it).
const QPointingDevice* testTouchDevice()
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

// One mouse for vlqt_test_queue_mouse, registered once like the touchscreen.
const QPointingDevice* testMouseDevice()
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

// Synthetic input state of a scene handle, created by its first test input.
vlqt::test::TestInput& inputOf(SceneHandle& h)
{
    if (!h.testInput)
        h.testInput = std::make_unique<vlqt::test::TestInput>();
    return *h.testInput;
}

} // namespace

namespace vlqt::test {

void installInputFilter(QQuickWindow* window)
{
    if (vlqt::platform::testSyntheticInputOnly())
        window->installEventFilter(new SystemInputFilter(window));
}

} // namespace vlqt::test

extern "C" {

VLQT_API int vlqt_test_send_mouse(vlqt_scene* p, int type, float x, float y)
{
    return withWindow(p, "vlqt_test_send_mouse", VLQT_ERR_UNSUPPORTED, [&](SceneHandle& h) -> int {
        QQuickWindow* window = h.window.get();
        vlqt::test::TestInput& in = inputOf(h);
        if (type == VLQT_MOUSE_LEAVE) {
            QEvent ev(QEvent::Leave);
            QCoreApplication::sendEvent(window, &ev);
            return 0;
        }
        QEvent::Type t;
        Qt::MouseButton button = Qt::NoButton;
        Qt::MouseButtons buttons = Qt::NoButton;
        Qt::MouseEventSource source = Qt::MouseEventNotSynthesized;
        switch (type) {
        case VLQT_MOUSE_MOVE: t = QEvent::MouseMove; buttons = in.buttons; break;
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
            return fail(VLQT_ERR_BAD_ARGUMENT, "test_send_mouse: unknown type " + std::to_string(type));
        }
        in.buttons = buttons;
        const QPointF local(x, y);
        QMouseEvent ev(t, local, local, window->mapToGlobal(local), button, buttons, Qt::NoModifier, source);
        if (!in.clock.isValid())
            in.clock.start();
        ev.setTimestamp(quint64(in.clock.elapsed()) + 1); // monotonic (TapHandler compares timestamps)
        QCoreApplication::sendEvent(window, &ev);
        return 0;
    });
}

VLQT_API int vlqt_test_send_wheel(vlqt_scene* p, float x, float y, int dx, int dy)
{
    return withWindow(p, "vlqt_test_send_wheel", VLQT_ERR_UNSUPPORTED, [&](SceneHandle& h) -> int {
        QQuickWindow* window = h.window.get();
        vlqt::test::TestInput& in = inputOf(h);
        const QPointF local(x, y);
        QWheelEvent ev(local, window->mapToGlobal(local), QPoint(), QPoint(dx, dy), in.buttons, Qt::NoModifier,
            Qt::NoScrollPhase, false);
        if (!in.clock.isValid())
            in.clock.start();
        ev.setTimestamp(quint64(in.clock.elapsed()) + 1);
        QCoreApplication::sendEvent(window, &ev);
        return 0;
    });
}

VLQT_API int vlqt_test_send_touch(vlqt_scene* p, int type, int id, float x, float y)
{
    return withWindow(p, "vlqt_test_send_touch", VLQT_ERR_UNSUPPORTED, [&](SceneHandle& h) -> int {
        vlqt::test::TestInput& in = inputOf(h);
        QEventPoint::State state;
        switch (type) {
        case VLQT_TOUCH_PRESS: state = QEventPoint::State::Pressed; break;
        case VLQT_TOUCH_MOVE: state = QEventPoint::State::Updated; break;
        case VLQT_TOUCH_RELEASE: state = QEventPoint::State::Released; break;
        default:
            return fail(VLQT_ERR_BAD_ARGUMENT, "test_send_touch: unknown type " + std::to_string(type));
        }
        if (!in.clock.isValid())
            in.clock.start();
        const ulong ts = ulong(in.clock.elapsed()) + 1; // monotonic (TapHandler compares timestamps)
        return vlqt::offscreen::sendTouch(h.window.get(), testTouchDevice(), in.touch, state, id,
                   QPointF(x, y), ts, Qt::NoModifier, "test_send_touch")
            ? VLQT_OK
            : VLQT_ERR_BAD_ARGUMENT;
    });
}

VLQT_API int vlqt_test_queue_mouse(vlqt_scene* p, int type, float x, float y)
{
    return withWindow(p, "vlqt_test_queue_mouse", VLQT_ERR_UNSUPPORTED, [&](SceneHandle& h) -> int {
        QQuickWindow* window = h.window.get();
        vlqt::test::TestInput& in = inputOf(h);
        QEvent::Type t;
        Qt::MouseButton button = Qt::NoButton;
        Qt::MouseButtons buttons = in.queuedButtons;
        switch (type) {
        case VLQT_MOUSE_MOVE: t = QEvent::MouseMove; break;
        case VLQT_MOUSE_PRESS: t = QEvent::MouseButtonPress; button = Qt::LeftButton; buttons |= Qt::LeftButton; break;
        case VLQT_MOUSE_RELEASE: t = QEvent::MouseButtonRelease; button = Qt::LeftButton; buttons &= ~Qt::LeftButton; break;
        default:
            return fail(VLQT_ERR_BAD_ARGUMENT, "test_queue_mouse: unknown type " + std::to_string(type));
        }
        in.queuedButtons = buttons;
        if (!in.clock.isValid())
            in.clock.start();
        const QPointF local(x, y);
        // Native pixels, like the platform plugin hands them over (QWindowSystemInterface scales them back).
        QWindowSystemInterface::handleMouseEvent<QWindowSystemInterface::AsynchronousDelivery>(window,
            ulong(in.clock.elapsed()) + 1, testMouseDevice(), QHighDpi::toNativeLocalPosition(local, window),
            QHighDpi::toNativeGlobalPosition(QPointF(window->mapToGlobal(local)), window), buttons, button, t);
        return 0;
    });
}

VLQT_API int vlqt_test_queue_touch(vlqt_scene* p, int type, int id, float x, float y)
{
    return withWindow(p, "vlqt_test_queue_touch", VLQT_ERR_UNSUPPORTED, [&](SceneHandle& h) -> int {
        QQuickWindow* window = h.window.get();
        vlqt::test::TestInput& in = inputOf(h);
        auto& active = in.queuedTouch;
        auto it = std::find_if(active.begin(), active.end(), [id](const auto& a) { return a.first == id; });
        const bool known = it != active.end();
        QEventPoint::State state;
        switch (type) {
        case VLQT_TOUCH_PRESS: state = QEventPoint::State::Pressed; break;
        case VLQT_TOUCH_MOVE: state = QEventPoint::State::Updated; break;
        case VLQT_TOUCH_RELEASE: state = QEventPoint::State::Released; break;
        default:
            return fail(VLQT_ERR_BAD_ARGUMENT, "test_queue_touch: unknown type " + std::to_string(type));
        }
        if (known == (type == VLQT_TOUCH_PRESS))
            return fail(VLQT_ERR_BAD_ARGUMENT,
                "test_queue_touch: point " + std::to_string(id) + (known ? " is already pressed" : " is not pressed"));
        const QPointF pos(x, y);
        if (known)
            it->second = pos;
        else
            active.emplace_back(id, pos);
        if (!in.clock.isValid())
            in.clock.start();
        // The complete touch state like a touchscreen driver: every active point, the changed one with its state.
        // QGuiApplication registers and forgets the points at the device itself (not the sendEvent path).
        QList<QWindowSystemInterface::TouchPoint> points;
        for (const auto& a : active) {
            QWindowSystemInterface::TouchPoint tp;
            tp.id = a.first;
            tp.state = a.first == id ? state : QEventPoint::State::Stationary;
            const QPointF global = QHighDpi::toNativeGlobalPosition(QPointF(window->mapToGlobal(a.second)), window);
            tp.area = QRectF(global - QPointF(2, 2), QSizeF(4, 4));
            tp.pressure = tp.state == QEventPoint::State::Released ? 0 : 1;
            points.append(tp);
        }
        QWindowSystemInterface::handleTouchEvent<QWindowSystemInterface::AsynchronousDelivery>(window,
            ulong(in.clock.elapsed()) + 1, testTouchDevice(), points);
        if (type == VLQT_TOUCH_RELEASE)
            active.erase(std::find_if(active.begin(), active.end(), [id](const auto& a) { return a.first == id; }));
        return 0;
    });
}

VLQT_API int vlqt_test_send_key(vlqt_scene* p, int key, int modifiers)
{
    return withWindow(p, "vlqt_test_send_key", VLQT_ERR_UNSUPPORTED, [&](SceneHandle& h) -> int {
        QKeyEvent ev(QEvent::KeyPress, key, Qt::KeyboardModifiers(modifiers));
        QCoreApplication::sendEvent(testKeyTarget(h.window.get()), &ev);
        return 0;
    });
}

VLQT_API int vlqt_test_send_key_text(vlqt_scene* p, int key, int modifiers, const char* text)
{
    return withWindow(p, "vlqt_test_send_key_text", VLQT_ERR_UNSUPPORTED, [&](SceneHandle& h) -> int {
        QQuickWindow* window = h.window.get();
        const QString t = text ? QString::fromUtf8(text) : QString();
        QKeyEvent press(QEvent::KeyPress, key, Qt::KeyboardModifiers(modifiers), t);
        QCoreApplication::sendEvent(testKeyTarget(window), &press);
        QKeyEvent release(QEvent::KeyRelease, key, Qt::KeyboardModifiers(modifiers), t);
        QCoreApplication::sendEvent(testKeyTarget(window), &release);
        return 0;
    });
}

} // extern "C"
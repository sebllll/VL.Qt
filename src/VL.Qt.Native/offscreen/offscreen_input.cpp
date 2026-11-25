// Input of an offscreen scene: the render control that gives a scene the keyboard focus, the scene's own pointing
// devices, and the events of the vlqt_offscreen_send_* exports (vlqt.h, render-texture.md "Input").
// Private Qt API: QQuickRenderControlPrivate (scene focus), QWindowSystemInterface (qpa); check on Qt updates (list: elements/elements.h).
#include "offscreen/offscreen_scene.h"
#include "app/exports.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QCoreApplication>
#include <QtGui/QFocusEvent>
#include <QtGui/QGuiApplication>
#include <QtGui/QInputMethodEvent>
#include <QtGui/QKeyEvent>
#include <QtGui/QMouseEvent>
#include <QtGui/QPointingDevice>
#include <QtGui/QStyleHints>
#include <QtGui/QTabletEvent>
#include <QtGui/QWheelEvent>
#include <QtGui/qpa/qwindowsysteminterface.h>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickRenderControl>
#include <QtQuick/QQuickWindow>
// Scene focus: SceneRenderControlPrivate. Private API, check on Qt updates.
#include <QtQuick/private/qquickrendercontrol_p.h>

#include <cmath>
#include <string>

namespace vlqt::offscreen {

namespace {

// Keyboard focus of a scene: Qt Quick gives a window's root item the focus, and so its items the active focus,
// only while QGuiApplication's focus window is that window, its render window
// (QQuickRenderControlPrivate::isRenderWindow) or none. A scene has no render window, and right after the host window
// was clicked a Qt window can still be the focus window (its deactivation is queued). So while the scene has the focus
// (FocusIn sent), it counts the focus window as its render window, as QQuickWidget's render control does for its window.
class SceneRenderControlPrivate : public QQuickRenderControlPrivate
{
public:
    SceneRenderControlPrivate(QQuickRenderControl* renderControl, const OffscreenScene* state)
        : QQuickRenderControlPrivate(renderControl), m_state(state) {}
    bool isRenderWindow(const QWindow* w) override
    {
        return (m_state->focused && w && w == QGuiApplication::focusWindow()) || QQuickRenderControlPrivate::isRenderWindow(w);
    }
private:
    const OffscreenScene* m_state;
};

class SceneRenderControl : public QQuickRenderControl
{
public:
    explicit SceneRenderControl(const OffscreenScene* state) : QQuickRenderControl(*new SceneRenderControlPrivate(this, state)) {}
};

// --- Input (vlqt.h, render-texture.md "Input") ---
// Events go with QCoreApplication::sendEvent to the scene's QQuickWindow, like vlqt_test_send_*: synchronous, so the
// FocusRelease filter, handlers and vlqt_scene_poll see them as window input.

int badArgument(const std::string& what)
{
    return vlqt::fail(VLQT_ERR_BAD_ARGUMENT, "offscreen " + what);
}

// Registered once per scene: QWindowSystemInterface lists it for Qt Quick (handler acceptedDevices); deleted with the
// render control, which unregisters it. The platform's mouse is looked up first, so a scene's mouse never becomes
// QPointingDevice::primaryPointingDevice().
QPointingDevice* newDevice(OffscreenScene* s, const char* name, QInputDevice::DeviceType type, QPointingDevice::PointerType pointer,
    QInputDevice::Capabilities caps, int maxPoints, int buttons)
{
    static qint64 s_systemId = 0x766c716f0000; // "vlqo" + counter: unique, not 0 (= "unknown")
    (void)QPointingDevice::primaryPointingDevice();
    auto* d = new QPointingDevice(QString::fromLatin1(name), ++s_systemId, type, pointer, caps, maxPoints, buttons,
        QString(), QPointingDeviceUniqueId(), s->renderControl);
    QWindowSystemInterface::registerInputDevice(d);
    return d;
}

QPointingDevice* mouseOf(OffscreenScene* s)
{
    if (!s->mouse)
        s->mouse = newDevice(s, "vlqt offscreen mouse", QInputDevice::DeviceType::Mouse, QPointingDevice::PointerType::Generic,
            QInputDevice::Capability::Position | QInputDevice::Capability::Hover | QInputDevice::Capability::Scroll, 1, 5);
    return s->mouse;
}

QPointingDevice* touchscreenOf(OffscreenScene* s)
{
    if (!s->touchscreen)
        s->touchscreen = newDevice(s, "vlqt offscreen touchscreen", QInputDevice::DeviceType::TouchScreen,
            QPointingDevice::PointerType::Finger, QInputDevice::Capability::Position | QInputDevice::Capability::Area, 32, 0);
    return s->touchscreen;
}

// Like a pen in a window on Windows (Qt 6, WM_POINTER): a stylus of pointer type Pen (Devices = Pen of the
// Interaction nodes), one point.
QPointingDevice* penOf(OffscreenScene* s)
{
    if (!s->pen)
        s->pen = newDevice(s, "vlqt offscreen pen", QInputDevice::DeviceType::Stylus, QPointingDevice::PointerType::Pen,
            QInputDevice::Capability::Position | QInputDevice::Capability::Pressure | QInputDevice::Capability::Hover, 1, 3);
    return s->pen;
}

// Monotonic, never 0 (TapHandler compares timestamps).
ulong timestamp(OffscreenScene* s)
{
    if (!s->clock.isValid())
        s->clock.start();
    return ulong(s->clock.elapsed()) + 1;
}

// device: the scene's mouse, or its pen for a mouse event synthesized from an unaccepted tablet event.
void sendMouse(OffscreenScene* s, QEvent::Type type, QPointF pos, Qt::MouseButton button, Qt::MouseButtons buttons,
    Qt::KeyboardModifiers modifiers, ulong ts, const QPointingDevice* device)
{
    QQuickWindow* w = s->window;
    const auto source = device == s->mouse ? Qt::MouseEventNotSynthesized : Qt::MouseEventSynthesizedByQt;
    QMouseEvent ev(type, pos, pos, w->mapToGlobal(pos), button, buttons, modifiers, source, device);
    ev.setTimestamp(ts);
    QCoreApplication::sendEvent(w, &ev);
}

// A press after a press of the same button within the double click interval and distance also sends
// MouseButtonDblClick, as QGuiApplication does for platform input (sendEvent bypasses it). TapHandler counts taps
// itself; MouseArea and the text inputs need the event.
void sendPress(OffscreenScene* s, QPointF pos, Qt::MouseButton button, Qt::MouseButtons buttons, Qt::KeyboardModifiers modifiers, ulong ts,
    const QPointingDevice* device)
{
    const QStyleHints* hints = QGuiApplication::styleHints();
    const QPointF d = pos - s->lastPressPos;
    const bool doubleClick = button == s->lastPressButton && ts - s->lastPressTime < ulong(hints->mouseDoubleClickInterval())
        && std::abs(d.x()) + std::abs(d.y()) <= hints->mouseDoubleClickDistance();
    sendMouse(s, QEvent::MouseButtonPress, pos, button, buttons, modifiers, ts, device);
    s->lastPressButton = doubleClick ? Qt::NoButton : button;
    s->lastPressTime = ts;
    s->lastPressPos = pos;
    if (doubleClick)
        sendMouse(s, QEvent::MouseButtonDblClick, pos, button, buttons, modifiers, ts, device);
}

// One event per changed button, lowest first (the host may report several in one call); a press or release without a
// changed button is an error.
int sendMousePointer(OffscreenScene* s, int type, QPointF pos, Qt::MouseButtons buttons, Qt::KeyboardModifiers modifiers, ulong ts)
{
    switch (type) {
    case VLQT_POINTER_MOVE:
        s->buttons = buttons;
        sendMouse(s, QEvent::MouseMove, pos, Qt::NoButton, buttons, modifiers, ts, mouseOf(s));
        return VLQT_OK;
    case VLQT_POINTER_LEAVE: {
        // Hover ends; Qt Quick clears its hover state.
        QEvent ev(QEvent::Leave);
        QCoreApplication::sendEvent(s->window, &ev);
        return VLQT_OK;
    }
    case VLQT_POINTER_PRESS:
    case VLQT_POINTER_RELEASE: {
        const bool press = type == VLQT_POINTER_PRESS;
        const Qt::MouseButtons changed = press ? buttons & ~s->buttons : s->buttons & ~buttons;
        if (changed == Qt::NoButton)
            return badArgument(std::string("send_pointer: mouse ") + (press ? "press" : "release")
                + " without a changed button (buttons = the buttons held after the event)");
        Qt::MouseButtons held = s->buttons;
        for (unsigned bit = 1; bit != 0 && bit <= unsigned(Qt::MaxMouseButton); bit <<= 1) {
            const auto button = Qt::MouseButton(bit);
            if (!changed.testFlag(button))
                continue;
            held.setFlag(button, press);
            s->buttons = held;
            if (press)
                sendPress(s, pos, button, held, modifiers, ts, mouseOf(s));
            else
                sendMouse(s, QEvent::MouseButtonRelease, pos, button, held, modifiers, ts, mouseOf(s));
        }
        return VLQT_OK;
    }
    default:
        return badArgument("send_pointer: mouse event type " + std::to_string(type) + " unknown");
    }
}

int sendTouchPointer(OffscreenScene* s, int type, int id, QPointF pos, Qt::KeyboardModifiers modifiers, ulong ts)
{
    QPointingDevice* device = touchscreenOf(s);
    auto& active = s->touchPoints;
    QEventPoint::State state;
    switch (type) {
    case VLQT_POINTER_PRESS: state = QEventPoint::State::Pressed; break;
    case VLQT_POINTER_MOVE: state = QEventPoint::State::Updated; break;
    case VLQT_POINTER_RELEASE: state = QEventPoint::State::Released; break;
    case VLQT_POINTER_CANCEL:
        cancelTouch(s->window, device, active, ts, modifiers);
        return VLQT_OK;
    default:
        // LEAVE: a touch has no hover.
        return badArgument("send_pointer: touch event type " + std::to_string(type) + " unknown");
    }
    return sendTouch(s->window, device, active, state, id, pos, ts, modifiers, "offscreen send_pointer")
        ? VLQT_OK
        : VLQT_ERR_BAD_ARGUMENT;
}

// A pen as QGuiApplication delivers one from Qt's Windows plugin: a QTabletEvent from the stylus (pointer handlers with
// Devices = Pen take it); if nobody accepts it, the same as a mouse event synthesized by Qt from the same device
// (MouseArea, Controls, hover), with double clicks. id is ignored: one pen point. Public API only.
int sendPenPointer(OffscreenScene* s, int type, QPointF pos, Qt::KeyboardModifiers modifiers, ulong ts)
{
    QPointingDevice* pen = penOf(s);
    QEvent::Type tablet;
    QEvent::Type mouse;
    Qt::MouseButton button = Qt::NoButton;
    switch (type) {
    case VLQT_POINTER_PRESS:
        if (s->penDown)
            return badArgument("send_pointer: the pen is already pressed");
        tablet = QEvent::TabletPress;
        mouse = QEvent::MouseButtonPress;
        button = Qt::LeftButton;
        s->penDown = true;
        break;
    case VLQT_POINTER_MOVE:
        tablet = QEvent::TabletMove;
        mouse = QEvent::MouseMove;
        break;
    case VLQT_POINTER_RELEASE:
        if (!s->penDown)
            return badArgument("send_pointer: the pen is not pressed");
        tablet = QEvent::TabletRelease;
        mouse = QEvent::MouseButtonRelease;
        button = Qt::LeftButton;
        s->penDown = false;
        break;
    case VLQT_POINTER_LEAVE: {
        // The hovering pen left: as the mouse.
        QEvent ev(QEvent::Leave);
        QCoreApplication::sendEvent(s->window, &ev);
        return VLQT_OK;
    }
    case VLQT_POINTER_CANCEL: {
        // No tablet cancel in Qt: the grabs of the pen point (tablet and synthesized mouse) are dropped, no release.
        if (s->penDown) {
            QTabletEvent ev(QEvent::TabletMove, pen, s->penPos, s->window->mapToGlobal(s->penPos), 0, 0, 0, 0, 0, 0,
                modifiers, Qt::NoButton, Qt::NoButton);
            ev.setExclusiveGrabber(ev.point(0), nullptr);
            ev.clearPassiveGrabbers(ev.point(0));
            s->penDown = false;
        }
        return VLQT_OK;
    }
    default:
        return badArgument("send_pointer: pen event type " + std::to_string(type) + " unknown");
    }
    s->penPos = pos;
    const Qt::MouseButtons buttons = s->penDown ? Qt::LeftButton : Qt::NoButton;
    QTabletEvent ev(tablet, pen, pos, s->window->mapToGlobal(pos), s->penDown ? 1.0 : 0.0, 0, 0, 0, 0, 0, modifiers, button,
        buttons);
    ev.setTimestamp(ts);
    QCoreApplication::sendEvent(s->window, &ev);
    if (!ev.isAccepted() && QCoreApplication::testAttribute(Qt::AA_SynthesizeMouseForUnhandledTabletEvents)) {
        if (mouse == QEvent::MouseButtonPress)
            sendPress(s, pos, button, buttons, modifiers, ts, pen);
        else
            sendMouse(s, mouse, pos, button, buttons, modifiers, ts, pen);
    }
    return VLQT_OK;
}

} // namespace

// FocusIn / FocusOut to the scene's window, as the Qt render control examples do: QQuickWindow gives its content item
// the focus and the active focus item follows (activeFocusOnPress of TextInput works; SceneRenderControlPrivate).
// FocusOut alone keeps the active focus item while no window has the focus (Qt keeps it for the window's return), so
// the focus loss releases it first: a running edit commits. Nothing if unchanged.
void setFocus(OffscreenScene* s, bool focused, Qt::FocusReason reason)
{
    if (s->focused == focused)
        return;
    s->polished = false; // input can change items (here the focus): the poll polishes by itself
    if (!focused)
        releaseFocus(s->window);
    // Before the event: SceneRenderControlPrivate reads it while QQuickWindow handles FocusIn.
    s->focused = focused;
    QFocusEvent ev(focused ? QEvent::FocusIn : QEvent::FocusOut, reason);
    QCoreApplication::sendEvent(s->window, &ev);
}

int sendPointer(OffscreenScene* s, int device, int type, int id, QPointF pos, int buttons, int modifiers)
{
    s->polished = false; // input can change items (hover, drag): the poll polishes by itself
    const auto mods = Qt::KeyboardModifiers(modifiers);
    const ulong ts = timestamp(s);
    // Like a window: a press inside the scene focuses it, outside unfocuses it, before the press is delivered (so the
    // pressed TextInput takes the active focus). An unknown device changes no focus.
    const bool knownDevice = device == VLQT_POINTER_DEVICE_MOUSE || device == VLQT_POINTER_DEVICE_TOUCH
        || device == VLQT_POINTER_DEVICE_PEN;
    if (type == VLQT_POINTER_PRESS && knownDevice) {
        const QSize size = s->window->size();
        setFocus(s, pos.x() >= 0 && pos.y() >= 0 && pos.x() < size.width() && pos.y() < size.height(), Qt::MouseFocusReason);
    }
    switch (device) {
    case VLQT_POINTER_DEVICE_MOUSE:
        return sendMousePointer(s, type, pos, Qt::MouseButtons(buttons), mods, ts);
    case VLQT_POINTER_DEVICE_TOUCH:
        return sendTouchPointer(s, type, id, pos, mods, ts);
    case VLQT_POINTER_DEVICE_PEN:
        return sendPenPointer(s, type, pos, mods, ts);
    default:
        return badArgument("send_pointer: device " + std::to_string(device) + " unknown");
    }
}

int sendWheel(OffscreenScene* s, QPointF pos, int dx, int dy, int modifiers)
{
    s->polished = false;
    QQuickWindow* w = s->window;
    QWheelEvent ev(pos, w->mapToGlobal(pos), QPoint(), QPoint(dx, dy), s->buttons, Qt::KeyboardModifiers(modifiers),
        Qt::NoScrollPhase, false, Qt::MouseEventNotSynthesized, mouseOf(s));
    ev.setTimestamp(timestamp(s));
    QCoreApplication::sendEvent(w, &ev);
    return VLQT_OK;
}

int sendKey(OffscreenScene* s, int type, int key, int modifiers, bool autoRepeat)
{
    s->polished = false;
    if (type != VLQT_KEY_EVENT_PRESS && type != VLQT_KEY_EVENT_RELEASE)
        return badArgument("send_key: type " + std::to_string(type) + " unknown");
    if (!s->focused)
        return VLQT_OK; // dropped, like keys of a window without focus
    QKeyEvent ev(type == VLQT_KEY_EVENT_PRESS ? QEvent::KeyPress : QEvent::KeyRelease, key, Qt::KeyboardModifiers(modifiers),
        QString(), autoRepeat);
    QCoreApplication::sendEvent(s->window, &ev);
    return VLQT_OK;
}

// Like vlqt_test_send_key_text: a key press/release with the text and Qt::Key_unknown. An item that ignores it but
// takes input method events gets the text as a QInputMethodEvent commit.
int sendText(OffscreenScene* s, const char* utf8, int length)
{
    s->polished = false;
    if (length < 0 || (!utf8 && length > 0))
        return badArgument("send_text: null text or negative length");
    if (length == 0 || !s->focused)
        return VLQT_OK;
    const QString text = QString::fromUtf8(utf8, length);
    QKeyEvent press(QEvent::KeyPress, Qt::Key_unknown, Qt::NoModifier, text);
    QCoreApplication::sendEvent(s->window, &press);
    QKeyEvent release(QEvent::KeyRelease, Qt::Key_unknown, Qt::NoModifier, text);
    QCoreApplication::sendEvent(s->window, &release);
    QQuickItem* focus = s->window->activeFocusItem();
    if (!press.isAccepted() && focus && focus->flags().testFlag(QQuickItem::ItemAcceptsInputMethod)) {
        QInputMethodEvent commit;
        commit.setCommitString(text);
        QCoreApplication::sendEvent(s->window, &commit);
    }
    return VLQT_OK;
}

QQuickRenderControl* newRenderControl(const OffscreenScene* scene)
{
    return new SceneRenderControl(scene);
}

} // namespace vlqt::offscreen

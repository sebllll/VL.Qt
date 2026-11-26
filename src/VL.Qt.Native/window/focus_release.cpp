#include "window/focus_release.h"
#include "offscreen/offscreen.h"

#include <QtCore/QEvent>
#include <QtCore/QVariant>
#include <QtGui/QEventPoint>
#include <QtGui/QKeyEvent> // also QPointerEvent
#include <QtQml/QJSValue>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>

namespace vlqt {

FocusRelease::FocusRelease(QQuickWindow* window) : QObject(window), m_window(window) {}

bool FocusRelease::eventFilter(QObject* o, QEvent* e)
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

// The element that holds the active focus: the item itself or its innermost Control / TextField ancestor (a
// SpinBox gives the focus to its contentItem). nullptr if nothing below the root has the focus.
QQuickItem* FocusRelease::focusOwner() const
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

// Esc: back to the last set or committed value, without a user signal (see "Value inputs" in elements/type_controls.cpp).
void FocusRelease::revert(QQuickItem* owner)
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

void FocusRelease::release()
{
    vlqt::offscreen::releaseFocus(m_window);
}

} // namespace vlqt

namespace vlqt::offscreen {

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

} // namespace vlqt::offscreen

#include "window/focus_release.h"
#include "elements/element_types.h"
#include "offscreen/offscreen.h"
#include "user_qml/qml_item.h"

#include <QtCore/QEvent>
#include <QtGui/QEventPoint>
#include <QtGui/QKeyEvent> // also QPointerEvent
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
            if (QQuickItem* owner = focusOwner(); owner && discardEdit(owner)) {
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

// The element that holds the active focus: the user QML element around it, else its innermost Control / TextField
// ancestor (a SpinBox gives the focus to its contentItem), else the item itself. nullptr if nothing below the root has
// the focus. A user QML element is one owner as a whole, so a press on another part of the same widget keeps the focus.
QQuickItem* FocusRelease::focusOwner() const
{
    QQuickItem* f = m_window->activeFocusItem();
    if (!f || f == m_window->contentItem())
        return nullptr;
    QQuickItem* control = nullptr;
    for (QQuickItem* i = f; i && i != m_window->contentItem(); i = i->parentItem()) {
        if (qobject_cast<QmlItem*>(i))
            return i;
        if (!control && (i->inherits("QQuickControl") || i->inherits("QQuickTextField")))
            control = i;
    }
    return control ? control : f;
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
